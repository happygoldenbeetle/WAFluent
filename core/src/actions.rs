//! Chat-list and message menu actions: each one goes to WhatsApp (so the phone and other
//! devices follow), then the local store is updated and the UI told. Failures come back
//! as a `notice` toast and leave the store as it was.

use std::sync::Arc;

use log::warn;
use whatsapp_rust::prelude::*;

use crate::protocol::Event as Out;
use crate::store::{self, StoredMessage};
use crate::{Ctx, extract, mark_unread, send_chat, send_message_update};

fn notice(ctx: &Ctx, ok: bool, text: impl Into<String>) {
    ctx.send(Out::Notice { ok, text: text.into() });
}

fn is_group(chat_id: &str) -> bool {
    chat_id.ends_with("@g.us")
}

/// Your own JID in the address family a chat uses (LID chats get your LID).
fn own_jid(client: &Client, chat: &Jid) -> Option<Jid> {
    let own = client.persistence_manager().get_device_snapshot();
    let mine = if chat.server == Server::Lid { own.lid.as_ref().or(own.pn.as_ref()) } else { own.pn.as_ref().or(own.lid.as_ref()) };
    mine.map(|j| j.to_non_ad())
}

/// Who wrote a message, as the protocol wants it: `None` in 1:1 chats and for your own.
fn participant(chat_id: &str, m: &StoredMessage) -> Option<Jid> {
    (is_group(chat_id) && !m.from_me).then(|| m.sender.parse().ok()).flatten()
}

fn message_key(client: &Client, chat: &Jid, chat_id: &str, m: &StoredMessage) -> wa::MessageKey {
    let participant = if !is_group(chat_id) {
        None
    } else if m.from_me {
        own_jid(client, chat).map(|j| j.to_string())
    } else {
        Some(m.sender.clone())
    };
    wa::MessageKey { remote_jid: Some(chat_id.to_string()), from_me: Some(m.from_me), id: Some(m.id.clone()), participant }
}

fn lookup(ctx: &Ctx, chat_id: &str, message_id: &str) -> Option<(Jid, StoredMessage)> {
    let jid = chat_id.parse::<Jid>().ok()?;
    let m = ctx.db().message(chat_id, message_id)?;
    Some((jid, m))
}

// ───────────── Chat list menu ─────────────

pub async fn chat_action(ctx: &Ctx, client: &Arc<Client>, chat_id: String, action: String, until_ms: Option<i64>) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let actions = client.chat_actions();
    let result: Result<(), String> = match action.as_str() {
        "archive" => actions.archive_chat(&jid, None).await.map_err(|e| e.to_string()),
        "unarchive" => actions.unarchive_chat(&jid, None).await.map_err(|e| e.to_string()),
        "mute" => match until_ms {
            Some(ms) => actions.mute_chat_until(&jid, ms).await,
            None => actions.mute_chat(&jid).await,
        }
        .map_err(|e| e.to_string()),
        "unmute" => actions.unmute_chat(&jid).await.map_err(|e| e.to_string()),
        "markRead" => actions.mark_chat_as_read(&jid, true, None).await.map_err(|e| e.to_string()),
        "markUnread" => actions.mark_chat_as_read(&jid, false, None).await.map_err(|e| e.to_string()),
        "clear" => actions.clear_chat(&jid, false, false, None).await.map_err(|e| e.to_string()),
        "delete" => actions.delete_chat(&jid, false, None).await.map_err(|e| e.to_string()),
        "block" => client.blocking().block(&jid).await.map_err(|e| e.to_string()),
        "unblock" => client.blocking().unblock(&jid).await.map_err(|e| e.to_string()),
        other => Err(format!("unknown chat action {other}")),
    };
    if let Err(e) = result {
        warn!("{action} {chat_id} failed: {e}");
        notice(ctx, false, format!("Couldn't {} this chat. Try again.", verb(&action)));
        send_chat(ctx, &chat_id);   // put the list back as it was
        return;
    }

    {
        let db = ctx.db();
        match action.as_str() {
            "archive" => db.set_archived(&chat_id, true),
            "unarchive" => db.set_archived(&chat_id, false),
            "mute" => db.set_mute_end(&chat_id, until_ms.map(|ms| ms / 1000).unwrap_or(-1)),
            "unmute" => db.set_mute_end(&chat_id, 0),
            "markRead" => db.mark_read(&chat_id),
            "block" => db.set_blocked(&chat_id, true),
            "unblock" => db.set_blocked(&chat_id, false),
            "clear" => db.clear_messages(&chat_id),
            "delete" => db.delete_chat(&chat_id),
            _ => {}
        }
    }
    match action.as_str() {
        "markUnread" => mark_unread(ctx, &chat_id),
        "clear" => ctx.send(Out::Messages { chat_id: chat_id.clone(), messages: Vec::new() }),
        "delete" => {
            ctx.send(Out::ChatRemoved { chat_id });
            return;
        }
        _ => {}
    }
    send_chat(ctx, &chat_id);
}

fn verb(action: &str) -> &'static str {
    match action {
        "archive" => "archive",
        "unarchive" => "unarchive",
        "mute" => "mute",
        "unmute" => "unmute",
        "markRead" | "markUnread" => "mark",
        "clear" => "clear",
        "delete" => "delete",
        "block" => "block",
        "unblock" => "unblock",
        _ => "change",
    }
}

/// Saves the person to your phone's contacts (WhatsApp needs their phone number).
pub async fn save_contact(ctx: &Ctx, client: &Arc<Client>, chat_id: String, first: String, last: String, sync_to_phone: bool) {
    let full = format!("{} {}", first.trim(), last.trim()).trim().to_string();
    let Some(pn) = ctx.db().phone_jid(&chat_id).and_then(|j| j.parse::<Jid>().ok()) else {
        notice(ctx, false, "This contact's phone number isn't known yet.");
        return;
    };
    let first = (!first.trim().is_empty()).then(|| first.trim().to_string());
    match client.chat_actions().save_contact(&pn, Some(full.clone()), first, sync_to_phone).await {
        Ok(()) => {
            {
                let db = ctx.db();
                db.set_full_name(&chat_id, &full);
                db.set_full_name(&pn.to_string(), &full);
            }
            send_chat(ctx, &chat_id);
            notice(ctx, true, format!("Saved {full} to your contacts"));
        }
        Err(e) => {
            warn!("save contact {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't save the contact. Try again.");
        }
    }
}

// ───────────── Message menu ─────────────

/// Sends a copy to each chat; attachments reuse the original upload.
/// Sends a stored message to other chats from its CDN reference. `as_forward`: marked
/// "Forwarded"; otherwise sent as new (stickers and GIFs from the panel).
pub async fn forward(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, to: Vec<String>, as_forward: bool) {
    let Some((_, m)) = lookup(ctx, &chat_id, &message_id) else { return };
    let media = ctx.db().media(&chat_id, &message_id);
    let Some(message) = extract::forwarded(&m.kind, &m.text, &m.file_name, media.as_ref().map(|(x, _)| x), as_forward) else {
        notice(ctx, false, "This message can't be forwarded yet.");
        return;
    };

    let mut sent = 0;
    for target in &to {
        let Ok(jid) = target.parse::<Jid>() else { continue };
        match client.send_message(jid, message.clone()).await {
            Ok(result) => {
                sent += 1;
                let stored = StoredMessage {
                    id: result.message_id,
                    from_me: true,
                    sender: String::new(),
                    push_name: String::new(),
                    ts: store::unix_now(),
                    kind: m.kind.clone(),
                    text: m.text.clone(),
                    file_name: m.file_name.clone(),
                    status: 1,
                };
                let dto = {
                    let db = ctx.db();
                    db.insert_message(target, &stored);
                    if let Some((x, path)) = &media {
                        db.insert_media(target, &stored.id, x);
                        if !path.is_empty() {
                            db.set_media_path(target, &stored.id, path);
                        }
                    }
                    db.to_dto(target, stored)
                };
                ctx.send(Out::Message { chat_id: target.clone(), message: dto });
                send_chat(ctx, target);
            }
            Err(e) => warn!("forward to {target} failed: {e}"),
        }
    }
    match sent {
        0 => notice(ctx, false, "Couldn't forward the message."),
        n if n == to.len() => notice(ctx, true, if n == 1 { "Forwarded".to_string() } else { format!("Forwarded to {n} chats") }),
        n => notice(ctx, false, format!("Forwarded to {n} of {} chats", to.len())),
    }
}

pub async fn pin_message(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, pin: bool) {
    let Some((jid, m)) = lookup(ctx, &chat_id, &message_id) else { return };
    let key = message_key(client, &jid, &chat_id, &m);
    let result = if pin {
        client.pin_message(jid, key, whatsapp_rust::send::PinDuration::Days7).await
    } else {
        client.unpin_message(jid, key).await
    };
    match result {
        Ok(()) => {
            ctx.db().set_pinned_message(&chat_id, if pin { &message_id } else { "" });
            send_chat(ctx, &chat_id);
        }
        Err(e) => {
            warn!("pin in {chat_id} failed: {e}");
            notice(ctx, false, format!("Couldn't {} the message.", if pin { "pin" } else { "unpin" }));
        }
    }
}

pub async fn star_message(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, star: bool) {
    let Some((jid, m)) = lookup(ctx, &chat_id, &message_id) else { return };
    let who = participant(&chat_id, &m);
    let actions = client.chat_actions();
    let result = if star {
        actions.star_message(&jid, who.as_ref(), &message_id, m.from_me).await
    } else {
        actions.unstar_message(&jid, who.as_ref(), &message_id, m.from_me).await
    };
    match result {
        Ok(()) => {
            ctx.db().set_starred(&chat_id, &message_id, star);
        }
        Err(e) => {
            warn!("star in {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't update the star.");
        }
    }
    send_message_update(ctx, &chat_id, &message_id);   // confirms, or puts the old state back
}

pub async fn delete_message(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, for_everyone: bool) {
    let Some((jid, m)) = lookup(ctx, &chat_id, &message_id) else { return };
    if for_everyone {
        match client.revoke_message(jid, message_id.clone(), whatsapp_rust::send::RevokeType::Sender).await {
            Ok(()) => {
                ctx.db().set_deleted(&chat_id, &message_id);
                send_message_update(ctx, &chat_id, &message_id);
                send_chat(ctx, &chat_id);
            }
            Err(e) => {
                warn!("revoke in {chat_id} failed: {e}");
                notice(ctx, false, "Couldn't delete the message for everyone.");
            }
        }
        return;
    }
    let who = participant(&chat_id, &m);
    match client.chat_actions().delete_message_for_me(&jid, who.as_ref(), &message_id, m.from_me, false, Some(m.ts * 1000)).await {
        Ok(()) => {
            ctx.db().delete_message(&chat_id, &message_id);
            ctx.send(Out::MessageRemoved { chat_id: chat_id.clone(), message_id });
            send_chat(ctx, &chat_id);
        }
        Err(e) => {
            warn!("delete for me in {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't delete the message.");
        }
    }
}

/// Contact info → Report: the contact, with their newest message as evidence.
pub async fn report_contact(ctx: &Ctx, client: &Arc<Client>, chat_id: String) {
    use whatsapp_rust::wacore::types::spam_report::{SpamFlow, SpamReportRequest};
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let (message_id, ts) = ctx.db().newest_incoming(&chat_id).unwrap_or_default();
    let request = SpamReportRequest {
        message_id,
        message_timestamp: ts.max(0) as u64,
        from_jid: Some(jid),
        spam_flow: SpamFlow::ContactInfo,
        ..Default::default()
    };
    match client.send_spam_report(request).await {
        Ok(_) => notice(ctx, true, "Reported to WhatsApp"),
        Err(e) => {
            warn!("report contact {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't send the report.");
        }
    }
}

/// Writes the whole chat (everything stored on this PC) as WhatsApp's export text.
pub fn export_chat(ctx: &Ctx, chat_id: &str, path: &str, utc_offset_minutes: i32) {
    use whatsapp_rust::wacore::chrono::{DateTime, FixedOffset};
    let zone = FixedOffset::east_opt(utc_offset_minutes * 60).unwrap_or_else(|| FixedOffset::east_opt(0).unwrap());
    let lines = ctx.db().export_lines(chat_id);
    let mut out = String::new();
    for (ts, who, text) in &lines {
        let when = DateTime::from_timestamp(*ts, 0)
            .map(|t| t.with_timezone(&zone).format("%d/%m/%Y, %H:%M").to_string())
            .unwrap_or_default();
        out.push_str(&format!("{when} - {who}: {text}\n"));
    }
    match std::fs::write(path, out) {
        Ok(()) => notice(ctx, true, format!("Exported {} messages", lines.len())),
        Err(e) => {
            warn!("export {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't write the export file.");
        }
    }
}

pub async fn report(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String) {
    use whatsapp_rust::wacore::types::spam_report::{SpamFlow, SpamReportRequest};
    let Some((jid, m)) = lookup(ctx, &chat_id, &message_id) else { return };
    let group = is_group(&chat_id);
    let sender: Option<Jid> = if group { m.sender.parse().ok() } else { Some(jid.clone()) };
    let request = SpamReportRequest {
        message_id,
        message_timestamp: m.ts.max(0) as u64,
        from_jid: sender.clone(),
        participant_jid: if group { sender } else { None },
        group_jid: group.then(|| jid.clone()),
        spam_flow: SpamFlow::MessageMenu,
        ..Default::default()
    };
    match client.send_spam_report(request).await {
        Ok(_) => notice(ctx, true, "Reported to WhatsApp"),
        Err(e) => {
            warn!("report in {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't send the report.");
        }
    }
}
