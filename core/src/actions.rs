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
    // The phone applies archive and read state only with the chat's last message attached.
    let range = || last_message_range(ctx, client, &jid, &chat_id);
    let result: Result<(), String> = match action.as_str() {
        "archive" => actions.archive_chat(&jid, range()).await.map_err(|e| e.to_string()),
        "unarchive" => actions.unarchive_chat(&jid, range()).await.map_err(|e| e.to_string()),
        "mute" => match until_ms {
            Some(ms) => actions.mute_chat_until(&jid, ms).await,
            None => actions.mute_chat(&jid).await,
        }
        .map_err(|e| e.to_string()),
        "unmute" => actions.unmute_chat(&jid).await.map_err(|e| e.to_string()),
        "markRead" => actions.mark_chat_as_read(&jid, true, range()).await.map_err(|e| e.to_string()),
        "markUnread" => actions.mark_chat_as_read(&jid, false, range()).await.map_err(|e| e.to_string()),
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

/// The chat's last message, as WhatsApp's archive / mark-read actions carry it.
fn last_message_range(ctx: &Ctx, client: &Client, jid: &Jid, chat_id: &str) -> Option<wa::sync_action_value::SyncActionMessageRange> {
    let m = ctx.db().last_message(chat_id)?;
    let key = message_key(client, jid, chat_id, &m);
    Some(wa::sync_action_value::SyncActionMessageRange {
        last_message_timestamp: Some(m.ts),
        last_system_message_timestamp: None,
        messages: vec![wa::sync_action_value::SyncActionMessage { key: MessageField::some(key), timestamp: Some(m.ts) }],
    })
}

/// Disappearing messages on or off for a chat: a group through the group's settings; a 1:1
/// chat with the message the phone sends (both phones, and yours, apply it).
pub async fn set_ephemeral(ctx: &Ctx, client: &Arc<Client>, chat_id: String, seconds: u32) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let result = if is_group(&chat_id) {
        client.groups().set_ephemeral(jid, seconds).await.map_err(|e| e.to_string())
    } else {
        let message = wa::Message {
            protocol_message: MessageField::some(wa::message::ProtocolMessage {
                r#type: Some(wa::message::protocol_message::Type::EPHEMERAL_SETTING),
                ephemeral_expiration: Some(seconds),
                ephemeral_setting_timestamp: Some(store::unix_now()),
                ..Default::default()
            }),
            ..Default::default()
        };
        client.send_message(jid, message).await.map(|_| ()).map_err(|e| e.to_string())
    };
    match result {
        Ok(()) => crate::ephemeral_changed(ctx, &chat_id, seconds, "You", store::unix_now()),
        Err(e) => {
            warn!("disappearing messages for {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't change disappearing messages. Try again.");
            send_chat(ctx, &chat_id);   // the panel goes back to what it was
        }
    }
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
/// New chat > New group: creates it with these members and opens it.
pub async fn create_group(ctx: &Ctx, client: &Arc<Client>, subject: String, members: Vec<String>) {
    use whatsapp_rust::wacore::iq::groups::{GroupCreateOptions, GroupParticipantOptions};
    let subject = subject.trim().to_string();
    let participants: Vec<GroupParticipantOptions> = {
        let db = ctx.db();
        members
            .iter()
            .filter_map(|m| {
                let jid = m.parse::<Jid>().ok()?;
                let lid = jid.is_lid();
                let p = GroupParticipantOptions::new(jid);
                // Members known by their LID need their number alongside.
                Some(match db.phone_jid(m).and_then(|pn| pn.parse::<Jid>().ok()) {
                    Some(pn) if lid => p.with_phone_number(pn),
                    _ => p,
                })
            })
            .collect()
    };
    if subject.is_empty() || participants.is_empty() {
        notice(ctx, false, "A group needs a name and at least one member.");
        return;
    }
    let options: GroupCreateOptions = GroupCreateOptions::builder().subject(subject.clone()).participants(participants).build();
    match client.groups().create_group(options).await {
        Ok(created) => {
            let chat_id = created.metadata.id.to_non_ad_string();
            ctx.db().upsert_chat(&store::ChatMeta {
                id: &chat_id,
                name: &subject,
                is_group: true,
                unread: 0,
                pinned: 0,
                archived: false,
                mute_end: 0,
            });
            crate::add_notice(ctx, &chat_id, format!("You created group \"{subject}\""), store::unix_now());
            send_chat(ctx, &chat_id);
            ctx.send(Out::Opened { chat_id });
        }
        Err(e) => {
            warn!("creating group {subject} failed: {e}");
            notice(ctx, false, "Couldn't create the group. Try again.");
        }
    }
}

// ───────────── Group info ─────────────

pub async fn set_group_subject(ctx: &Ctx, client: &Arc<Client>, chat_id: String, subject: String) {
    use whatsapp_rust::wacore::iq::groups::GroupSubject;
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let subject = subject.trim().to_string();
    let result = match GroupSubject::new(subject.clone()) {
        Ok(valid) => client.groups().set_subject(jid, valid).await.map_err(|e| e.to_string()),
        Err(e) => Err(e.to_string()),
    };
    match result {
        Ok(()) => {
            ctx.db().set_chat_name(&chat_id, &subject);
            crate::add_notice(ctx, &chat_id, format!("You changed the group name to \"{subject}\""), store::unix_now());
        }
        Err(e) => {
            warn!("renaming {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't change the group name. Only admins may be allowed to.");
        }
    }
    send_chat(ctx, &chat_id);   // the new name, or the old one back
}

pub async fn set_group_description(ctx: &Ctx, client: &Arc<Client>, chat_id: String, description: String) {
    use whatsapp_rust::features::PreviousDescription;
    use whatsapp_rust::wacore::iq::groups::GroupDescription;
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let text = description.trim().to_string();
    let wanted = if text.is_empty() { Ok(None) } else { GroupDescription::new(text).map(Some) };
    let result = match wanted {
        Ok(value) => client.groups().set_description(jid, value, PreviousDescription::Resolve).await.map_err(|e| e.to_string()),
        Err(e) => Err(e.to_string()),
    };
    match result {
        Ok(()) => crate::add_notice(ctx, &chat_id, "You changed the group description".into(), store::unix_now()),
        Err(e) => {
            warn!("description of {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't change the group description. Only admins may be allowed to.");
        }
    }
    crate::refresh_group(ctx, client, chat_id).await;
}

pub async fn set_group_picture(ctx: &Ctx, client: &Arc<Client>, chat_id: String, path: String) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let Ok(image) = std::fs::read(&path) else {
        notice(ctx, false, "That picture couldn't be read.");
        return;
    };
    match client.groups().set_profile_picture(jid, image).await {
        Ok(_) => {
            crate::add_notice(ctx, &chat_id, "You changed this group's icon".into(), store::unix_now());
            let _ = ctx.avatars.send(crate::avatars::Request { chat_id: chat_id.clone(), force: true });
            send_chat(ctx, &chat_id);
        }
        Err(e) => {
            warn!("picture of {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't change the group picture. Only admins may be allowed to.");
        }
    }
}

pub async fn add_group_members(ctx: &Ctx, client: &Arc<Client>, chat_id: String, members: Vec<String>) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let (jids, names): (Vec<Jid>, Vec<String>) = {
        let db = ctx.db();
        members.iter().filter_map(|m| Some((m.parse::<Jid>().ok()?, db.person_name(m, "")))).unzip()
    };
    if jids.is_empty() {
        return;
    }
    match client.groups().add_participants(jid, &jids).await {
        Ok(_) => crate::add_notice(ctx, &chat_id, format!("You added {}", names.join(", ")), store::unix_now()),
        Err(e) => {
            warn!("adding to {chat_id} failed: {e}");
            notice(ctx, false, "Couldn't add them to the group. Only admins may be allowed to.");
        }
    }
    crate::refresh_group(ctx, client, chat_id).await;
}

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
    let m = if chat_id == store::FAVORITES {
        // Not a message: a sticker starred on the phone. Its hash comes from the file.
        if ctx.db().media(&chat_id, &message_id).is_some_and(|(x, _)| x.file_sha256.is_empty())
            && let Err(e) = crate::media::fetch_now(ctx, client, &chat_id, &message_id).await
        {
            warn!("favourite sticker {message_id}: {e}");
            notice(ctx, false, "This sticker couldn't be downloaded.");
            return;
        }
        StoredMessage {
            id: message_id.clone(),
            from_me: true,
            sender: String::new(),
            push_name: String::new(),
            ts: 0,
            kind: "sticker".into(),
            text: String::new(),
            file_name: String::new(),
            status: 1,
        }
    } else {
        let Some((_, m)) = lookup(ctx, &chat_id, &message_id) else { return };
        m
    };
    let media = ctx.db().media(&chat_id, &message_id);
    // Like WhatsApp: forwarding what you wrote yourself isn't labelled; anything else counts one more.
    let before = ctx.db().forwarded(&chat_id, &message_id);
    let score = if !as_forward || (m.from_me && before == 0) { 0 } else { before + 1 };
    let Some(message) = extract::forwarded(&m.kind, &m.text, &m.file_name, media.as_ref().map(|(x, _)| x), score) else {
        notice(ctx, false, "This message can't be forwarded yet.");
        return;
    };

    let mut sent = 0;
    for target in &to {
        let Ok(jid) = target.parse::<Jid>() else { continue };
        let timer = ctx.db().ephemeral(target);
        match client.send_message(jid, extract::with_expiration(message.clone(), timer)).await {
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
                    db.set_forwarded(target, &stored.id, score);
                    db.set_expiry(target, &stored.id, stored.ts, db.ephemeral(target));
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

/// Pin for 24 hours, 7 days or 30 days (like the phone), or unpin; synced to everyone.
pub async fn pin_message(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, pin: bool, duration: Option<u32>) {
    use whatsapp_rust::send::PinDuration;
    let Some((jid, m)) = lookup(ctx, &chat_id, &message_id) else { return };
    let key = message_key(client, &jid, &chat_id, &m);
    let (length, secs) = match duration {
        Some(86_400) => (PinDuration::Hours24, 86_400),
        Some(2_592_000) => (PinDuration::Days30, 2_592_000),
        _ => (PinDuration::Days7, 604_800),
    };
    let result = if pin { client.pin_message(jid, key, length).await } else { client.unpin_message(jid, key).await };
    match result {
        Ok(()) => {
            {
                let db = ctx.db();
                let now = store::unix_now();
                if pin { db.add_pin(&chat_id, &message_id, now, now + secs) } else { db.remove_pin(&chat_id, &message_id) }
            }
            if pin {
                crate::add_notice(ctx, &chat_id, "You pinned a message".into(), store::unix_now());
            }
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

// ───────────── GIF search ─────────────

/// Uploads a GIF picked from search (an MP4, as WhatsApp sends GIFs) and sends it.
pub async fn send_gif(ctx: &Ctx, client: &Arc<Client>, to: String, path: String, width: u32, height: u32, thumb: Option<String>) {
    use whatsapp_rust::download::MediaType;
    let Ok(jid) = to.parse::<Jid>() else { return };
    let data = match std::fs::read(&path) {
        Ok(data) => data,
        Err(e) => {
            warn!("gif {path}: {e}");
            notice(ctx, false, "The GIF couldn't be read.");
            return;
        }
    };
    let thumb = thumb.and_then(|p| std::fs::read(p).ok()).unwrap_or_default();
    let up = match client.upload(data, MediaType::Video, Default::default()).await {
        Ok(up) => up,
        Err(e) => {
            warn!("gif upload failed: {e}");
            notice(ctx, false, "The GIF couldn't be uploaded.");
            return;
        }
    };
    let media = extract::Media {
        media_type: "video",
        direct_path: up.direct_path.clone(),
        media_key: up.media_key.to_vec(),
        file_sha256: up.file_sha256.to_vec(),
        file_enc_sha256: up.file_enc_sha256.to_vec(),
        file_length: up.file_length,
        mimetype: "video/mp4".into(),
        width,
        height,
        seconds: 0,
        waveform: Vec::new(),
    };
    let mut message = wa::Message::default();
    message.video_message = MessageField::some(wa::message::VideoMessage {
        url: Some(up.url),
        direct_path: Some(up.direct_path),
        media_key: Some(up.media_key.to_vec()),
        media_key_timestamp: Some(up.media_key_timestamp),
        file_sha256: Some(up.file_sha256.to_vec()),
        file_enc_sha256: Some(up.file_enc_sha256.to_vec()),
        file_length: Some(up.file_length),
        mimetype: Some("video/mp4".into()),
        width: Some(width),
        height: Some(height),
        gif_playback: Some(true),
        gif_attribution: Some(wa::message::video_message::Attribution::GIPHY),
        jpeg_thumbnail: (!thumb.is_empty()).then(|| thumb.clone()),
        streaming_sidecar: up.streaming_sidecar,
        ..Default::default()
    });
    let timer = ctx.db().ephemeral(&to);
    let sent = match client.send_message(jid, extract::with_expiration(message, timer)).await {
        Ok(sent) => sent,
        Err(e) => {
            warn!("gif to {to} failed: {e}");
            notice(ctx, false, "The GIF couldn't be sent.");
            return;
        }
    };
    let stored = StoredMessage {
        id: sent.message_id,
        from_me: true,
        sender: String::new(),
        push_name: String::new(),
        ts: store::unix_now(),
        kind: "gif".into(),
        text: String::new(),
        file_name: String::new(),
        status: 1,
    };
    let dto = {
        let db = ctx.db();
        db.ensure_chat(&to, to.ends_with("@g.us"));
        db.insert_message(&to, &stored);
        db.insert_media(&to, &stored.id, &media);
        db.set_media_path(&to, &stored.id, &path);
        db.insert_extra(&to, &stored.id, &thumb, None);
        db.to_dto(&to, stored)
    };
    ctx.send(Out::Message { chat_id: to.clone(), message: dto });
    send_chat(ctx, &to);
}

// ───────────── Attach menu: photos, videos, documents, contacts, polls ─────────────

/// A file to upload and send (from the attach menu).
pub struct Outgoing {
    pub path: String,
    pub kind: String,
    pub caption: String,
    pub mime: String,
    pub width: u32,
    pub height: u32,
    pub seconds: u32,
    pub thumb: Option<String>,
    /// Voice notes: 64 levels, 0-100.
    pub waveform: Vec<u8>,
}

/// Uploads in progress, by their bubble's id, so ✕ can stop one.
pub fn uploads() -> std::sync::MutexGuard<'static, std::collections::HashMap<String, tokio::task::AbortHandle>> {
    static UPLOADS: std::sync::LazyLock<std::sync::Mutex<std::collections::HashMap<String, tokio::task::AbortHandle>>> =
        std::sync::LazyLock::new(Default::default);
    UPLOADS.lock().unwrap_or_else(|p| p.into_inner())
}

/// Uploads a picture, video or document and sends it; the uploading bubble (`temp_id`)
/// becomes the sent message, or shows it failed.
pub async fn send_media(ctx: &Ctx, client: &Arc<Client>, chat_id: String, file: Outgoing, temp_id: String) {
    let fail = |reason: String| ctx.send(Out::SendFailed { chat_id: chat_id.clone(), temp_id: temp_id.clone(), reason });
    use whatsapp_rust::download::MediaType;
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let what = match file.kind.as_str() {
        "image" => "photo",
        "video" => "video",
        "gif" => "GIF",
        "voice" => "voice message",
        _ => "file",
    };
    let data = match std::fs::read(&file.path) {
        Ok(data) => data,
        Err(e) => {
            warn!("send {}: {e}", file.path);
            notice(ctx, false, format!("The {what} couldn't be read."));
            fail(e.to_string());
            return;
        }
    };
    let media_type = match file.kind.as_str() {
        "image" => MediaType::Image,
        "video" | "gif" => MediaType::Video,
        "voice" => MediaType::Audio,
        _ => MediaType::Document,
    };
    let up = match client.upload(data, media_type, Default::default()).await {
        Ok(up) => up,
        Err(e) => {
            warn!("upload of {} failed: {e}", file.path);
            notice(ctx, false, format!("The {what} couldn't be uploaded."));
            fail(e.to_string());
            return;
        }
    };
    let thumb = file.thumb.as_deref().and_then(|p| std::fs::read(p).ok()).filter(|t| !t.is_empty());
    let caption = (!file.caption.trim().is_empty()).then(|| file.caption.trim().to_string());
    let file_name = std::path::Path::new(&file.path).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let mut message = wa::Message::default();
    match file.kind.as_str() {
        "image" => {
            message.image_message = MessageField::some(wa::message::ImageMessage {
                url: Some(up.url),
                direct_path: Some(up.direct_path),
                media_key: Some(up.media_key.to_vec()),
                media_key_timestamp: Some(up.media_key_timestamp),
                file_sha256: Some(up.file_sha256.to_vec()),
                file_enc_sha256: Some(up.file_enc_sha256.to_vec()),
                file_length: Some(up.file_length),
                mimetype: Some(file.mime.clone()),
                width: Some(file.width),
                height: Some(file.height),
                jpeg_thumbnail: thumb,
                caption,
                ..Default::default()
            })
        }
        "voice" => {
            // A voice note (push-to-talk): OGG Opus, its length and waveform, like the phone sends.
            message.audio_message = MessageField::some(wa::message::AudioMessage {
                url: Some(up.url),
                direct_path: Some(up.direct_path),
                media_key: Some(up.media_key.to_vec()),
                media_key_timestamp: Some(up.media_key_timestamp),
                file_sha256: Some(up.file_sha256.to_vec()),
                file_enc_sha256: Some(up.file_enc_sha256.to_vec()),
                file_length: Some(up.file_length),
                mimetype: Some(file.mime.clone()),
                seconds: Some(file.seconds),
                ptt: Some(true),
                waveform: (!file.waveform.is_empty()).then(|| file.waveform.clone()),
                streaming_sidecar: up.streaming_sidecar,
                ..Default::default()
            })
        }
        "video" | "gif" => {
            message.video_message = MessageField::some(wa::message::VideoMessage {
                gif_playback: (file.kind == "gif").then_some(true),
                url: Some(up.url),
                direct_path: Some(up.direct_path),
                media_key: Some(up.media_key.to_vec()),
                media_key_timestamp: Some(up.media_key_timestamp),
                file_sha256: Some(up.file_sha256.to_vec()),
                file_enc_sha256: Some(up.file_enc_sha256.to_vec()),
                file_length: Some(up.file_length),
                mimetype: Some(file.mime.clone()),
                width: Some(file.width),
                height: Some(file.height),
                seconds: Some(file.seconds),
                jpeg_thumbnail: thumb,
                streaming_sidecar: up.streaming_sidecar,
                caption,
                ..Default::default()
            })
        }
        _ => {
            message.document_message = MessageField::some(wa::message::DocumentMessage {
                url: Some(up.url),
                direct_path: Some(up.direct_path),
                media_key: Some(up.media_key.to_vec()),
                media_key_timestamp: Some(up.media_key_timestamp),
                file_sha256: Some(up.file_sha256.to_vec()),
                file_enc_sha256: Some(up.file_enc_sha256.to_vec()),
                file_length: Some(up.file_length),
                mimetype: Some(file.mime.clone()),
                title: Some(file_name.clone()),
                file_name: Some(file_name),
                jpeg_thumbnail: thumb,
                caption,
                ..Default::default()
            })
        }
    }
    let timer = ctx.db().ephemeral(&chat_id);
    let sent = match client.send_message(jid, extract::with_expiration(message.clone(), timer)).await {
        Ok(sent) => sent,
        Err(e) => {
            warn!("send to {chat_id} failed: {e}");
            notice(ctx, false, format!("The {what} couldn't be sent."));
            fail(e.to_string());
            return;
        }
    };
    store_sent_as(ctx, &chat_id, sent.message_id, &message, Some(&file.path), None, Some(temp_id.clone()));
}

/// Shares contact cards: one as a contact message, several as one "N contacts" message.
pub async fn send_contacts(ctx: &Ctx, client: &Arc<Client>, chat_id: String, contacts: Vec<crate::protocol::ContactCard>) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let cards: Vec<wa::message::ContactMessage> = contacts
        .iter()
        .map(|c| {
            let digits: String = c.phone.chars().filter(char::is_ascii_digit).collect();
            let name = c.name.replace(['\n', ';'], " ");
            wa::message::ContactMessage {
                display_name: Some(name.clone()),
                vcard: Some(format!(
                    "BEGIN:VCARD\nVERSION:3.0\nN:;{name};;;\nFN:{name}\nTEL;type=CELL;type=VOICE;waid={digits}:+{digits}\nEND:VCARD"
                )),
                ..Default::default()
            }
        })
        .collect();
    let mut message = wa::Message::default();
    match cards.len() {
        0 => return,
        1 => message.contact_message = MessageField::some(cards.into_iter().next().unwrap_or_default()),
        n => {
            message.contacts_array_message = MessageField::some(wa::message::ContactsArrayMessage {
                display_name: Some(format!("{n} contacts")),
                contacts: cards,
                ..Default::default()
            })
        }
    }
    let timer = ctx.db().ephemeral(&chat_id);
    match client.send_message(jid, extract::with_expiration(message.clone(), timer)).await {
        Ok(sent) => store_sent(ctx, &chat_id, sent.message_id, &message, None, None),
        Err(e) => {
            warn!("contacts to {chat_id} failed: {e}");
            notice(ctx, false, "The contact couldn't be sent.");
        }
    }
}

/// A poll from the Create poll panel.
pub struct NewPoll {
    pub question: String,
    pub options: Vec<String>,
    pub multiple: bool,
    pub hide_voters: bool,
    pub end_time: Option<i64>,
}

/// Starts a poll, built here like WhatsApp Web does (the library's builder can't do
/// multiple answers, end times or hidden voters): single-answer polls as v3, others as v1,
/// with a fresh 32-byte secret that's kept so everyone's votes can be read.
pub async fn send_poll(ctx: &Ctx, client: &Arc<Client>, chat_id: String, poll: NewPoll) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let mut options: Vec<String> = Vec::new();
    for option in poll.options.iter().map(|o| o.trim().to_string()).filter(|o| !o.is_empty()) {
        if !options.contains(&option) {
            options.push(option);
        }
    }
    if options.len() < 2 || poll.question.trim().is_empty() {
        notice(ctx, false, "A poll needs a question and at least two different options.");
        return;
    }
    options.truncate(12);
    let mut secret = vec![0u8; 32];
    if getrandom::fill(&mut secret).is_err() {
        notice(ctx, false, "The poll couldn't be sent.");
        return;
    }
    let creation = wa::message::PollCreationMessage {
        name: Some(poll.question.trim().to_string()),
        options: options
            .iter()
            .map(|o| wa::message::poll_creation_message::Option { option_name: Some(o.clone()), ..Default::default() })
            .collect(),
        selectable_options_count: Some(if poll.multiple { 0 } else { 1 }),
        end_time: poll.end_time,
        hide_participant_name: poll.hide_voters.then_some(true),
        ..Default::default()
    };
    let mut message = wa::Message::default();
    if poll.multiple {
        message.poll_creation_message = MessageField::some(creation);
    } else {
        message.poll_creation_message_v3 = MessageField::some(creation);
    }
    message.message_context_info = MessageField::some(wa::MessageContextInfo {
        message_secret: Some(secret.clone()),
        ..Default::default()
    });
    let timer = ctx.db().ephemeral(&chat_id);
    let sent = match client.send_message(jid.clone(), extract::with_expiration(message.clone(), timer)).await {
        Ok(sent) => sent,
        Err(e) => {
            warn!("poll to {chat_id} failed: {e}");
            notice(ctx, false, "The poll couldn't be sent.");
            return;
        }
    };
    let own = client.persistence_manager().get_device_snapshot();
    let me = if jid.server == Server::Lid { own.lid.as_ref().or(own.pn.as_ref()) } else { own.pn.as_ref().or(own.lid.as_ref()) };
    let creator = me.map(|j| j.to_non_ad_string()).unwrap_or_default();
    store_sent(ctx, &chat_id, sent.message_id, &message, None, Some((&secret, &creator)));
}

/// Stores a message you just sent the way received ones are stored, and shows it.
/// `local`: the file it came from (opens without downloading). `poll`: secret and creator.
fn store_sent(ctx: &Ctx, chat_id: &str, id: String, message: &wa::Message, local: Option<&str>, poll: Option<(&[u8], &str)>) {
    store_sent_as(ctx, chat_id, id, message, local, poll, None);
}

/// `temp_id`: the uploading bubble this replaces (answered with `sent`, not a new message).
fn store_sent_as(ctx: &Ctx, chat_id: &str, id: String, message: &wa::Message, local: Option<&str>, poll: Option<(&[u8], &str)>, temp_id: Option<String>) {
    let Some(content) = extract::content(message) else { return };
    let stored = StoredMessage {
        id,
        from_me: true,
        sender: String::new(),
        push_name: String::new(),
        ts: store::unix_now(),
        kind: content.kind.to_string(),
        text: content.text,
        file_name: content.file_name,
        status: 1,
    };
    let dto = {
        let db = ctx.db();
        db.ensure_chat(chat_id, chat_id.ends_with("@g.us"));
        db.insert_message(chat_id, &stored);
        db.set_expiry(chat_id, &stored.id, stored.ts, db.ephemeral(chat_id));
        if let Some(media) = &content.media {
            db.insert_media(chat_id, &stored.id, media);
            if let Some(path) = local {
                db.set_media_path(chat_id, &stored.id, path);
            }
        }
        db.insert_extra(chat_id, &stored.id, &content.thumb, content.extra.as_ref());
        if let Some((secret, creator)) = poll {
            db.set_poll(chat_id, &stored.id, secret, creator);
        }
        db.to_dto(chat_id, stored)
    };
    match temp_id {
        Some(temp_id) => ctx.send(Out::Sent { chat_id: chat_id.to_string(), temp_id, message: dto }),
        None => ctx.send(Out::Message { chat_id: chat_id.to_string(), message: dto }),
    }
    send_chat(ctx, chat_id);
}

// ───────────── Editing ─────────────

/// Sends new text for one of your messages; the bubble updates (with "Edited") once it's sent.
pub async fn edit_message(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, text: String) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let text = text.trim().to_string();
    if text.is_empty() {
        return;
    }
    match client.edit_message(jid, message_id.clone(), wa::Message::text(text.clone())).await {
        Ok(_) => {
            if ctx.db().set_edited(&chat_id, &message_id, &text) {
                send_message_update(ctx, &chat_id, &message_id);
                send_chat(ctx, &chat_id);
            }
        }
        Err(e) => {
            warn!("edit of {chat_id}/{message_id} failed: {e}");
            notice(ctx, false, "The message couldn't be edited.");
        }
    }
}
