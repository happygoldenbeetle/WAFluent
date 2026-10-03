//! Status updates (the Status page): what your contacts posted in the last 24 hours, and
//! your own posts from the phone.
//!
//! They arrive as ordinary messages addressed to `status@broadcast` and are kept in the
//! message table under that id (it is not a chat: it has no row in `chats`), so pictures and
//! videos download the same way a chat's do. Each goes when it is 24 hours old. Which ones
//! you've looked at is kept here; looking at one tells its author, as on the phone.

use std::sync::Arc;
use std::time::Duration;

use log::{info, warn};
use serde_json::json;
use whatsapp_rust::prelude::*;

use crate::protocol::Event as Out;
use crate::store::{self, StoredMessage};
use crate::{Ctx, extract, wa};

/// Where status updates are kept in the message table.
pub(crate) const CHAT: &str = "status@broadcast";

/// How long a status lives, seconds.
const LIFE: u32 = 86_400;

/// One list for the page, a moment after the last change (reconnecting brings many at once).
pub(crate) fn spawn_debouncer(ctx: Ctx) {
    tokio::spawn(async move {
        loop {
            ctx.statuses_dirty.notified().await;
            tokio::time::sleep(Duration::from_millis(400)).await;
            send(&ctx);
        }
    });
}

pub(crate) fn send(ctx: &Ctx) {
    let statuses = ctx.db().statuses(store::unix_now() - i64::from(LIFE));
    // Their pictures: an author listed without one is asked for (see avatars.rs).
    let mut asked = std::collections::HashSet::new();
    for status in statuses.iter().filter(|s| s.avatar.is_none() && !s.author.is_empty()) {
        if asked.insert(status.author.clone()) {
            let _ = ctx.avatars.send(crate::avatars::Request { chat_id: status.author.clone(), force: false });
        }
    }
    ctx.send(Out::Statuses { statuses });
}

/// A message addressed to `status@broadcast`: a new status, or one being taken back.
pub(crate) fn on_message(ctx: &Ctx, message: &wa::Message, info: &MessageInfo) {
    let source = &info.source;
    if let Some(extract::Control::Revoke(id)) = extract::control(message) {
        remove(ctx, &id);
        ctx.statuses_dirty.notify_one();
        return;
    }
    let ts = info.timestamp.timestamp();
    if ts + i64::from(LIFE) <= store::unix_now() {
        return;
    }
    let Some(content) = extract::content(message) else { return };
    // Only what the viewer can show.
    if !matches!(content.kind, "text" | "image" | "video" | "gif" | "voice" | "audio") {
        return;
    }
    let sender = if source.is_from_me { String::new() } else { source.sender.to_non_ad_string() };
    // A text status has a coloured card: its colour and font ride along.
    let mut extra = content.extra;
    if let Some(ext) = extract::base(message).extended_text_message.as_option() {
        let style = json!({ "background": ext.background_argb.unwrap_or(0), "font": ext.font.map(|f| f as i32).unwrap_or(0) });
        match extra.as_mut().and_then(|e| e.as_object_mut()) {
            Some(map) => {
                map.insert("status".into(), style);
            }
            None => extra = Some(json!({ "status": style })),
        }
    }
    let stored = StoredMessage {
        id: info.id.to_string(),
        from_me: source.is_from_me,
        sender,
        push_name: info.push_name.clone(),
        ts,
        kind: content.kind.to_string(),
        text: content.text,
        file_name: content.file_name,
        status: if source.is_from_me { 1 } else { 0 },
    };
    {
        let db = ctx.db();
        if !source.is_from_me {
            db.set_push_name(&stored.sender, &info.push_name);
        }
        if !db.insert_message(CHAT, &stored) {
            return; // delivered twice
        }
        if let Some(media) = &content.media {
            db.insert_media(CHAT, &stored.id, media);
        }
        db.insert_extra(CHAT, &stored.id, &content.thumb, extra.as_ref());
        db.set_expiry(CHAT, &stored.id, ts, LIFE);
    }
    info!("status: one more ({})", stored.kind);
    ctx.statuses_dirty.notify_one();
}

/// Takes a status out: taken back by its author, or 24 hours old. Its file goes too.
pub(crate) fn remove(ctx: &Ctx, id: &str) {
    let db = ctx.db();
    if let Some((_, path)) = db.media(CHAT, id).filter(|(_, p)| !p.is_empty() && !p.starts_with('!')) {
        let _ = std::fs::remove_file(path);
    }
    db.delete_message(CHAT, id);
    db.forget_status_seen(id);
}

/// You looked at these: remembered, and their authors are told (as the phone does).
pub(crate) fn seen(ctx: &Ctx, client: &Arc<Client>, ids: Vec<String>) {
    let mut receipts: Vec<(Jid, String)> = Vec::new();
    {
        let db = ctx.db();
        for id in ids {
            let Some(m) = db.message(CHAT, &id) else { continue };
            if !db.set_status_seen(&id) || m.from_me {
                continue; // seen before, or your own
            }
            if let Ok(author) = m.sender.parse::<Jid>() {
                receipts.push((author, id));
            }
        }
    }
    if receipts.is_empty() {
        return;
    }
    let client = Arc::clone(client);
    tokio::spawn(async move {
        let to = Jid::status_broadcast();
        for (author, id) in receipts {
            if let Err(e) = client.mark_as_read(&to, Some(&author), &[id.as_str()]).await {
                warn!("status: the seen receipt wasn't sent: {e}");
            }
        }
    });
}

/// A reply to someone's status: a message in your chat with them that quotes it.
pub(crate) async fn reply(ctx: &Ctx, client: &Arc<Client>, id: String, text: String) {
    let found = {
        let db = ctx.db();
        db.message(CHAT, &id).filter(|m| !m.from_me).map(|m| {
            let media = db.media_dto(CHAT, &id);
            let chat_id = db.canonical(&m.sender);
            (m, media, chat_id)
        })
    };
    let Some((m, media, chat_id)) = found else {
        ctx.send(Out::Notice { ok: false, text: "That status is gone.".into() });
        return;
    };
    let Ok(jid) = chat_id.parse::<Jid>() else {
        ctx.send(Out::Notice { ok: false, text: "Couldn't reply to that status.".into() });
        return;
    };
    let (mime, seconds) = media.as_ref().map_or((String::new(), 0), |d| (d.mime.clone(), d.seconds));
    let quoted = extract::quoted_message(&m.kind, &m.text, &m.file_name, &mime, seconds);
    let mut context = whatsapp_rust::wacore::proto_helpers::build_quote_context(m.id.clone(), m.sender.clone(), &quoted);
    context.remote_jid = Some(CHAT.to_string());   // what marks it as a reply to a status
    let message = wa::Message::text_with_context(text.clone(), context);
    let timer = ctx.db().ephemeral(&chat_id);
    let sent = match client.send_message(jid, extract::with_expiration(message, timer)).await {
        Ok(sent) => sent,
        Err(e) => {
            warn!("status: reply failed: {e}");
            ctx.send(Out::Notice { ok: false, text: "The reply couldn't be sent.".into() });
            return;
        }
    };
    let kind = match m.kind.as_str() {
        "image" => "image",
        "video" => "video",
        "gif" => "gif",
        "voice" => "voice",
        "audio" => "audio",
        _ => "text",
    };
    let (dto, chat) = {
        let db = ctx.db();
        let stored = StoredMessage {
            id: sent.message_id,
            from_me: true,
            sender: String::new(),
            push_name: String::new(),
            ts: store::unix_now(),
            kind: "text".into(),
            text,
            file_name: String::new(),
            status: 1,
        };
        db.ensure_chat(&chat_id, false);
        db.insert_message(&chat_id, &stored);
        db.set_expiry(&chat_id, &stored.id, stored.ts, timer);
        db.insert_quote(&chat_id, &stored.id, &extract::Quote { id: m.id, sender: m.sender, kind, text: m.text, file_name: m.file_name });
        (db.to_dto(&chat_id, stored), db.chat(&chat_id))
    };
    ctx.send(Out::Message { chat_id, message: dto });
    if let Some(chat) = chat {
        ctx.send(Out::Chat { chat });
    }
    ctx.send(Out::Notice { ok: true, text: "Reply sent".into() });
}
