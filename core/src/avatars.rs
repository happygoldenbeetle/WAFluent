//! Profile pictures: fetched one at a time (WhatsApp rate-limits bursts), cached as
//! JPEGs under <data>\avatars, and re-checked once a day.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use log::{debug, warn};
use tokio::sync::mpsc;
use whatsapp_rust::prelude::*;

use crate::Ctx;
use crate::protocol::Event as Out;
use crate::store::unix_now;

/// Chat id used for your own picture.
pub const SELF_ID: &str = "self";
const RECHECK_SECS: i64 = 24 * 3600;
const RETRY_SECS: i64 = 3600;

pub struct Request {
    pub chat_id: String,
    /// Ignore the once-a-day check (the picture is known to have changed).
    pub force: bool,
}

pub fn spawn(ctx: Ctx, client: Arc<Client>, mut rx: mpsc::UnboundedReceiver<Request>) {
    tokio::spawn(async move {
        while let Some(req) = rx.recv().await {
            match refresh(&ctx, &client, &req).await {
                Ok(false) => {}
                Ok(true) => tokio::time::sleep(Duration::from_millis(250)).await,
                Err(e) => {
                    debug!("profile picture for {} failed: {e}", req.chat_id);
                    tokio::time::sleep(Duration::from_millis(250)).await;
                }
            }
        }
    });
}

/// Queue your own picture and every chat whose picture is missing or a day old.
pub fn queue_stale(ctx: &Ctx) {
    let _ = ctx.avatars.send(Request { chat_id: SELF_ID.into(), force: false });
    for chat_id in ctx.db().chats_needing_avatar(unix_now() - RECHECK_SECS) {
        let _ = ctx.avatars.send(Request { chat_id, force: false });
    }
}

/// Pictures that came back empty for people known by their LID are asked for again, once,
/// now that their number is tried too.
pub fn retry_missing_once(ctx: &Ctx) {
    const FLAG: &str = "avatars_number_retry_v1";
    let db = ctx.db();
    if !db.flag(FLAG) {
        db.forget_missing_lid_avatars();
        db.set_flag(FLAG);
    }
}

pub fn clear_cache(ctx: &Ctx) {
    let dir = cache_dir(ctx);
    let _ = std::fs::remove_dir_all(&dir);
}

fn cache_dir(ctx: &Ctx) -> PathBuf {
    ctx.data_dir.join("avatars")
}

/// Returns whether the server was asked (so the worker paces only real requests).
async fn refresh(ctx: &Ctx, client: &Arc<Client>, req: &Request) -> Result<bool, String> {
    let now = unix_now();
    let known = ctx.db().avatar(&req.chat_id);
    // Also de-duplicates chats queued several times.
    if !req.force && known.as_ref().is_some_and(|(_, _, checked)| now - checked < RECHECK_SECS) {
        return Ok(false);
    }
    let (known_id, known_path, _) = known.unwrap_or_default();

    let jid: Jid = if req.chat_id == SELF_ID {
        client
            .persistence_manager()
            .get_device_snapshot()
            .pn
            .clone()
            .ok_or("not linked yet")?
            .to_non_ad()
    } else {
        req.chat_id.parse().map_err(|e| format!("bad jid: {e:?}"))?
    };

    let picture = client
        .contacts()
        .get_profile_picture_with_timeout(&jid, false, Some(Duration::from_secs(15)))
        .await;

    // Someone known here by their LID may only show their picture under their number.
    let number = if req.chat_id.ends_with("@lid") { ctx.db().phone_jid(&req.chat_id).and_then(|pn| pn.parse::<Jid>().ok()) } else { None };
    let picture = match (picture, number) {
        (Ok(None), Some(pn)) => client.contacts().get_profile_picture_with_timeout(&pn, false, Some(Duration::from_secs(15))).await,
        (picture, _) => picture,
    };

    match picture {
        Ok(Some(pic)) => {
            if pic.id == known_id && !known_path.is_empty() && Path::new(&known_path).exists() {
                ctx.db().set_avatar(&req.chat_id, &known_id, &known_path, now);
                return Ok(true);
            }
            let url = pic.url.clone();
            let bytes = tokio::task::spawn_blocking(move || download(&url))
                .await
                .map_err(|e| e.to_string())??;

            let dir = cache_dir(ctx);
            std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
            // The picture id is in the name, so a changed picture never hits a stale image cache.
            let path = dir.join(format!("{}_{}.jpg", file_safe(&req.chat_id), file_safe(&pic.id)));
            std::fs::write(&path, &bytes).map_err(|e| e.to_string())?;
            if !known_path.is_empty() && Path::new(&known_path) != path {
                let _ = std::fs::remove_file(&known_path);
            }

            let path = path.to_string_lossy().into_owned();
            ctx.db().set_avatar(&req.chat_id, &pic.id, &path, now);
            ctx.send(Out::Avatar { chat_id: req.chat_id.clone(), path: Some(path) });
            ctx.calls_dirty.notify_one();   // the Calls page shows pictures too
        }
        Ok(None) => {
            // No picture, or hidden by their privacy settings.
            if !known_path.is_empty() {
                let _ = std::fs::remove_file(&known_path);
                ctx.send(Out::Avatar { chat_id: req.chat_id.clone(), path: None });
            }
            ctx.db().set_avatar(&req.chat_id, "", "", now);
        }
        Err(e) => {
            // Try again in an hour instead of on every reconnect.
            ctx.db().set_avatar(&req.chat_id, &known_id, &known_path, now - RECHECK_SECS + RETRY_SECS);
            warn!("profile picture request failed for {}: {e}", req.chat_id);
            return Err(e.to_string());
        }
    }
    Ok(true)
}

fn download(url: &str) -> Result<Vec<u8>, String> {
    let mut response = ureq::get(url).call().map_err(|e| e.to_string())?;
    response
        .body_mut()
        .with_config()
        .limit(8 * 1024 * 1024)
        .read_to_vec()
        .map_err(|e| e.to_string())
}

fn file_safe(s: &str) -> String {
    s.chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' || c == '.' { c } else { '_' }).collect()
}
