//! Attachment downloads: decrypted into <data>\media\<chat>\<message>.<ext>, three at a
//! time, each message fetched once.
//!
//! Newest request first: opening another chat (or scrolling) puts what is on screen now
//! ahead of whatever was queued before. Links older than a few weeks have expired on the
//! CDN (403/404/410); the phone is then asked to upload the file again, and the download
//! retried from the new path. Rate limits (429) back off and retry.

use std::collections::HashSet;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use log::{info, warn};
use tokio::sync::{Notify, mpsc};
use whatsapp_rust::download::{DownloadParams, MediaType};
use whatsapp_rust::http::HttpStatusError;
use whatsapp_rust::prelude::*;
use whatsapp_rust::NodeFilter;
use whatsapp_rust::wacore::media_retry::{
    MediaRetryResult, build_media_retry_receipt, encrypt_media_retry_receipt, parse_media_retry_notification,
};

use crate::Ctx;
use crate::extract::Media;
use crate::protocol::Event as Out;

const WORKERS: usize = 3;

/// Marks an attachment the phone no longer has, so it isn't asked again every launch.
const GONE: &str = "!gone";

pub struct Request {
    pub chat_id: String,
    pub message_id: String,
    /// "Retry download": also try attachments already known to be gone.
    pub force: bool,
}

#[derive(Default)]
struct Queue {
    stack: Mutex<Vec<Request>>,
    /// Queued or downloading: (chat, message).
    known: Mutex<HashSet<(String, String)>>,
    ready: Notify,
}

pub fn spawn(ctx: Ctx, client: Arc<Client>, mut rx: mpsc::UnboundedReceiver<Request>) {
    let queue = Arc::new(Queue::default());

    let feeder = Arc::clone(&queue);
    tokio::spawn(async move {
        while let Some(req) = rx.recv().await {
            let key = (req.chat_id.clone(), req.message_id.clone());
            if !lock(&feeder.known).insert(key) {
                continue; // already queued or downloading; its result reaches the UI anyway
            }
            lock(&feeder.stack).push(req);
            feeder.ready.notify_one();
        }
    });

    for _ in 0..WORKERS {
        let (ctx, client, queue) = (ctx.clone(), Arc::clone(&client), Arc::clone(&queue));
        tokio::spawn(async move {
            loop {
                let next = lock(&queue.stack).pop();
                let Some(req) = next else {
                    queue.ready.notified().await;
                    continue;
                };
                let result = fetch(&ctx, &client, &req).await;
                lock(&queue.known).remove(&(req.chat_id.clone(), req.message_id.clone()));
                match result {
                    Ok(path) => ctx.send(Out::Media { chat_id: req.chat_id, message_id: req.message_id, path }),
                    Err(reason) => {
                        warn!("media download failed for {}/{}: {reason}", req.chat_id, req.message_id);
                        ctx.send(Out::MediaFailed { chat_id: req.chat_id, message_id: req.message_id, reason });
                    }
                }
            }
        });
    }
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|p| p.into_inner())
}

pub fn clear_cache(ctx: &Ctx) {
    let _ = std::fs::remove_dir_all(ctx.data_dir.join("media"));
}

/// Downloads now, outside the queue (a favourite sticker about to be sent).
pub async fn fetch_now(ctx: &Ctx, client: &Arc<Client>, chat_id: &str, message_id: &str) -> Result<String, String> {
    fetch(ctx, client, &Request { chat_id: chat_id.into(), message_id: message_id.into(), force: false }).await
}

async fn fetch(ctx: &Ctx, client: &Arc<Client>, req: &Request) -> Result<String, String> {
    let (mut media, cached) = ctx.db().media(&req.chat_id, &req.message_id).ok_or("no attachment")?;
    if cached == GONE && !req.force {
        return Err("no longer on your phone".into());
    }
    if !cached.is_empty() && cached != GONE && Path::new(&cached).exists() {
        return Ok(cached);
    }

    let mut reuploaded = false;
    let mut rate_limited = 0u32;
    let bytes = loop {
        match download(client, &media).await {
            Ok(bytes) => break bytes,
            Err(e) => {
                let expired = e.chain().find_map(|c| c.downcast_ref::<HttpStatusError>()).is_some_and(|s| matches!(s.status, 403 | 404 | 410));
                let detail = format!("{e:#}");
                if expired && !reuploaded && !crate::channels::is_channel(&req.chat_id) {
                    reuploaded = true;
                    match reupload(ctx, client, req, &media.media_key).await {
                        Ok(path) => {
                            media.direct_path = path;
                            continue;
                        }
                        Err(Reupload::Gone) => {
                            ctx.db().set_media_path(&req.chat_id, &req.message_id, GONE);
                            return Err("no longer on your phone".into());
                        }
                        Err(Reupload::NoAnswer) => return Err("expired, and your phone didn't send it again".into()),
                    }
                }
                if (detail.contains("429") || detail.contains("rate-overlimit")) && rate_limited < 4 {
                    rate_limited += 1;
                    tokio::time::sleep(Duration::from_secs(2u64.pow(rate_limited))).await; // 2, 4, 8, 16 s
                    continue;
                }
                return Err(detail);
            }
        }
    };

    let dir = ctx.data_dir.join("media").join(file_safe(&req.chat_id));
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let path = dir.join(format!("{}.{}", file_safe(&req.message_id), extension(&media.mimetype, media.media_type)));
    std::fs::write(&path, &bytes).map_err(|e| e.to_string())?;
    if media.file_sha256.is_empty() {
        use sha2::Digest;
        ctx.db().set_file_sha256(&req.chat_id, &req.message_id, &sha2::Sha256::digest(&bytes));
    }

    let path = path.to_string_lossy().into_owned();
    ctx.db().set_media_path(&req.chat_id, &req.message_id, &path);
    Ok(path)
}

async fn download(client: &Client, media: &Media) -> whatsapp_rust::anyhow::Result<Vec<u8>> {
    let media_type = match media.media_type {
        "image" => MediaType::Image,
        "video" => MediaType::Video,
        "audio" => MediaType::Audio,
        "sticker" => MediaType::Sticker,
        _ => MediaType::Document,
    };
    if media.media_key.is_empty() {
        // A channel's post: the file is on the CDN as it is.
        let params = DownloadParams {
            direct_path: media.direct_path.clone(),
            media_key: None,
            file_sha256: media.file_sha256.clone(),
            file_enc_sha256: None,
            file_length: media.file_length,
            media_type,
        };
        return client.download_from_params(&params).await;
    }
    let params = DownloadParams::encrypted(
        media.direct_path.clone(),
        &media.media_key,
        &media.file_sha256,
        &media.file_enc_sha256,
        media.file_length,
        media_type,
    );
    client.download_from_params(&params).await
}

enum Reupload {
    /// The phone doesn't have the file any more.
    Gone,
    /// No answer (phone offline, not linked...); a later retry may work.
    NoAnswer,
}

/// Asks the phone to upload an expired attachment again; the new CDN path is stored.
/// 1:1 chats are known by number and by LID; if the phone doesn't find the message
/// under one, the other is tried.
async fn reupload(ctx: &Ctx, client: &Client, req: &Request, media_key: &[u8]) -> Result<String, Reupload> {
    let (message, jids) = {
        let db = ctx.db();
        (db.message(&req.chat_id, &req.message_id), db.chat_jids(&req.chat_id))
    };
    let message = message.ok_or(Reupload::Gone)?;
    let is_group = req.chat_id.ends_with("@g.us");
    let participant: Option<Jid> = (is_group && !message.from_me).then(|| message.sender.parse().ok()).flatten();

    let mut refused = false;
    for jid in jids.iter().filter_map(|j| j.parse::<Jid>().ok()) {
        match ask_phone(client, &req.message_id, &jid, media_key, message.from_me, participant.as_ref()).await {
            Ok(MediaRetryResult::Success { direct_path }) => {
                info!("phone re-uploaded {}/{}", req.chat_id, req.message_id);
                ctx.db().set_direct_path(&req.chat_id, &req.message_id, &direct_path);
                return Ok(direct_path);
            }
            Ok(other) => {
                warn!("re-upload of {}/{} under {jid}: {other:?}", req.chat_id, req.message_id);
                refused = true;
            }
            // Phone offline: asking under another id won't help now.
            Err(Asked::Timeout) => {
                warn!("re-upload of {}/{} timed out", req.chat_id, req.message_id);
                return Err(Reupload::NoAnswer);
            }
            Err(Asked::Failed(e)) => warn!("re-upload of {}/{} failed: {e}", req.chat_id, req.message_id),
        }
    }
    Err(if refused { Reupload::Gone } else { Reupload::NoAnswer })
}

enum Asked {
    Timeout,
    Failed(String),
}

/// The media-retry request, WhatsApp Web style: a `server-error` receipt addressed to our
/// own account (the phone answers it) and a wait for its `mediaretry` notification.
///
/// Built here rather than with `client.media_reupload()`: whatsapp-rust 0.7 addresses that
/// receipt to this device's own JID (`number:device@…`), so it never reaches the phone and
/// every request times out. Its history-sync twin uses the account JID, which is right.
async fn ask_phone(
    client: &Client,
    msg_id: &str,
    chat: &Jid,
    media_key: &[u8],
    from_me: bool,
    participant: Option<&Jid>,
) -> Result<MediaRetryResult, Asked> {
    let own = client.pn().ok_or_else(|| Asked::Failed("not logged in".into()))?.to_non_ad();
    let (ciphertext, iv) = encrypt_media_retry_receipt(media_key, msg_id).map_err(|e| Asked::Failed(e.to_string()))?;
    // Listen before sending, so a quick answer isn't missed.
    let waiter = client.wait_for_node(NodeFilter::tag("notification").attr("type", "mediaretry").attr("id", msg_id));
    let receipt = build_media_retry_receipt(&own, msg_id, chat, from_me, participant, &ciphertext, &iv);
    client.send_node(receipt).await.map_err(|e| Asked::Failed(e.to_string()))?;
    let answer = tokio::time::timeout(Duration::from_secs(30), waiter)
        .await
        .map_err(|_| Asked::Timeout)?
        .map_err(|_| Asked::Failed("cancelled".into()))?;
    parse_media_retry_notification(answer.get(), media_key).map_err(|e| Asked::Failed(e.to_string()))
}

fn extension(mime: &str, media_type: &str) -> &'static str {
    let mime = mime.split(';').next().unwrap_or("").trim();
    match mime {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "audio/ogg" => "ogg",
        "audio/mpeg" => "mp3",
        "audio/mp4" | "audio/aac" => "m4a",
        "video/mp4" => "mp4",
        "application/pdf" => "pdf",
        _ => match media_type {
            "image" => "jpg",
            "sticker" => "webp",
            "audio" => "ogg",
            "video" => "mp4",
            _ => "bin",
        },
    }
}

fn file_safe(s: &str) -> String {
    s.chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' || c == '.' { c } else { '_' }).collect()
}
