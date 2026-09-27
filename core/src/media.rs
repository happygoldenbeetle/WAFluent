//! Attachment downloads: decrypted into <data>\media\<chat>\<message>.<ext>, at most
//! three at a time, each message fetched once.

use std::collections::HashSet;
use std::path::Path;
use std::sync::{Arc, Mutex};

use log::warn;
use tokio::sync::{Semaphore, mpsc};
use whatsapp_rust::download::{DownloadParams, MediaType};
use whatsapp_rust::prelude::*;

use crate::Ctx;
use crate::protocol::Event as Out;

pub struct Request {
    pub chat_id: String,
    pub message_id: String,
}

pub fn spawn(ctx: Ctx, client: Arc<Client>, mut rx: mpsc::UnboundedReceiver<Request>) {
    let slots = Arc::new(Semaphore::new(3));
    let in_flight: Arc<Mutex<HashSet<(String, String)>>> = Arc::default();
    tokio::spawn(async move {
        while let Some(req) = rx.recv().await {
            let key = (req.chat_id.clone(), req.message_id.clone());
            if !in_flight.lock().unwrap_or_else(|p| p.into_inner()).insert(key.clone()) {
                continue; // already downloading; its result reaches the UI anyway
            }
            let Ok(permit) = Arc::clone(&slots).acquire_owned().await else { break };
            let (ctx, client, in_flight) = (ctx.clone(), Arc::clone(&client), Arc::clone(&in_flight));
            tokio::spawn(async move {
                let result = fetch(&ctx, &client, &req).await;
                in_flight.lock().unwrap_or_else(|p| p.into_inner()).remove(&key);
                drop(permit);
                match result {
                    Ok(path) => ctx.send(Out::Media { chat_id: req.chat_id, message_id: req.message_id, path }),
                    Err(reason) => {
                        warn!("media download failed for {}/{}: {reason}", req.chat_id, req.message_id);
                        ctx.send(Out::MediaFailed { chat_id: req.chat_id, message_id: req.message_id, reason });
                    }
                }
            });
        }
    });
}

pub fn clear_cache(ctx: &Ctx) {
    let _ = std::fs::remove_dir_all(ctx.data_dir.join("media"));
}

async fn fetch(ctx: &Ctx, client: &Arc<Client>, req: &Request) -> Result<String, String> {
    let (media, cached) = ctx.db().media(&req.chat_id, &req.message_id).ok_or("no attachment")?;
    if !cached.is_empty() && Path::new(&cached).exists() {
        return Ok(cached);
    }

    let media_type = match media.media_type {
        "image" => MediaType::Image,
        "video" => MediaType::Video,
        "audio" => MediaType::Audio,
        "sticker" => MediaType::Sticker,
        _ => MediaType::Document,
    };
    let params = DownloadParams::encrypted(
        media.direct_path.clone(),
        &media.media_key,
        &media.file_sha256,
        &media.file_enc_sha256,
        media.file_length,
        media_type,
    );
    let bytes = client.download_from_params(&params).await.map_err(|e| e.to_string())?;

    let dir = ctx.data_dir.join("media").join(file_safe(&req.chat_id));
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let path = dir.join(format!("{}.{}", file_safe(&req.message_id), extension(&media.mimetype, media.media_type)));
    std::fs::write(&path, &bytes).map_err(|e| e.to_string())?;

    let path = path.to_string_lossy().into_owned();
    ctx.db().set_media_path(&req.chat_id, &req.message_id, &path);
    Ok(path)
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
