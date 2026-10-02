//! The call history (the Calls page). It comes from three places: the phone's history sync
//! (`callLogRecords`), the phone's app state (`call_log` changes, handed over by the library
//! patch) and the calls made or answered here (calls.rs). All three write the same table,
//! keyed by WhatsApp's call id, so a call the phone also reports isn't listed twice.

use std::sync::Arc;
use std::time::Duration;

use log::{info, warn};
use whatsapp_rust::prelude::*;
use whatsapp_rust::wafluent_hooks::CallLogMutation;

use crate::protocol::Event as Out;
use crate::store::{CallEntry, Store};
use crate::{Ctx, wa};

/// How many calls the page gets.
const SHOWN: u32 = 100;

/// One list for the page, a moment after the last change (a sync brings many at once).
pub(crate) fn spawn_debouncer(ctx: Ctx) {
    tokio::spawn(async move {
        loop {
            ctx.calls_dirty.notified().await;
            tokio::time::sleep(Duration::from_millis(500)).await;
            send(&ctx);
        }
    });
}

pub(crate) fn send(ctx: &Ctx) {
    let calls = ctx.db().calls(SHOWN);
    // Their pictures: anyone listed without one is asked for (once a day at most; see avatars.rs).
    let mut asked = std::collections::HashSet::new();
    for call in calls.iter().filter(|c| c.avatar.is_none() && !c.chat_id.is_empty()) {
        if asked.insert(call.chat_id.clone()) {
            let _ = ctx.avatars.send(crate::avatars::Request { chat_id: call.chat_id.clone(), force: false });
        }
    }
    ctx.send(Out::Calls { calls });
}

/// Calls made here before the history was kept only left a line in their chat ("Voice call ·
/// 0:29"): those lines become history entries, once. Such a line doesn't say who called, so
/// a call that connected is taken to be one you made.
pub(crate) fn backfill_notices_once(ctx: &Ctx) {
    const FLAG: &str = "call_notices_backfilled_v1";
    let db = ctx.db();
    if db.flag(FLAG) {
        return;
    }
    let mut added = 0;
    for (message_id, chat_id, ts, text) in db.call_notices() {
        let detail = text.rsplit_once(" · ").map(|(_, d)| d.trim()).unwrap_or("");
        let (incoming, result, duration) = if text.starts_with("Missed") {
            (true, "missed", 0)
        } else if detail == "Declined" {
            (false, "rejected", 0)
        } else if let Some(seconds) = clock_seconds(detail) {
            (false, "connected", seconds)
        } else {
            (false, "cancelled", 0)
        };
        let started = ts - duration;   // the line was written when the call ended
        if db.call_near(&chat_id, started) {
            continue;
        }
        added += db.put_call(&CallEntry {
            id: format!("notice:{message_id}"),
            ts: started,
            duration,
            incoming,
            video: text.contains("ideo call"),
            result: result.to_string(),
            group_jid: String::new(),
            peers: vec![chat_id],
        }) as usize;
    }
    db.set_flag(FLAG);
    if added > 0 {
        info!("call history: {added} earlier calls made here added");
    }
}

/// "0:29" or "1:02:33" as seconds.
fn clock_seconds(text: &str) -> Option<i64> {
    let parts: Vec<i64> = text.split(':').map(|p| p.parse::<i64>().ok()).collect::<Option<_>>()?;
    match parts[..] {
        [m, s] => Some(m * 60 + s),
        [h, m, s] => Some(h * 3600 + m * 60 + s),
        _ => None,
    }
}

/// A call made, answered, missed or declined on this PC.
pub(crate) fn record(ctx: &Ctx, entry: CallEntry) {
    if entry.id.is_empty() {
        return;
    }
    if ctx.db().put_call(&entry) {
        ctx.calls_dirty.notify_one();
    }
}

/// A call-history change from the phone's app state.
pub(crate) fn mutation(ctx: &Ctx, m: CallLogMutation) {
    let changed = match (&m.record, m.removed) {
        (Some(record), false) => entry(record).is_some_and(|e| ctx.db().put_call(&e)),
        // A removal names the call in its index (after "call_log").
        _ => m.index.iter().skip(1).fold(false, |any, id| ctx.db().remove_call(id) || any),
    };
    if changed {
        ctx.calls_dirty.notify_one();
    }
}

/// The calls in a history-sync chunk.
pub(crate) fn ingest(store: &Store, records: &[wa::CallLogRecord]) -> usize {
    records.iter().filter_map(entry).filter(|e| store.put_call(e)).count()
}

/// WhatsApp's record of a call, as a row. None without an id or a time.
fn entry(r: &wa::CallLogRecord) -> Option<CallEntry> {
    use wa::call_log_record::CallResult;
    let id = r.call_id.clone().filter(|id| !id.is_empty())?;
    let ts = r.start_time.filter(|t| *t > 0)?;
    // Seconds; some clients write milliseconds.
    let ts = if ts > 100_000_000_000 { ts / 1000 } else { ts };
    let duration = r.duration.unwrap_or(0).max(0);
    let duration = if duration > 86_400 * 4 { duration / 1000 } else { duration };
    let result = match r.call_result {
        Some(CallResult::CONNECTED) | Some(CallResult::ONGOING) => "connected",
        Some(CallResult::REJECTED) => "rejected",
        Some(CallResult::CANCELLED) | Some(CallResult::ABANDONED) => "cancelled",
        Some(CallResult::ACCEPTEDELSEWHERE) => "elsewhere",
        Some(CallResult::MISSED) | Some(CallResult::UNAVAILABLE) => "missed",
        Some(CallResult::FAILED) | Some(CallResult::INVALID) => "failed",
        Some(CallResult::UPCOMING) => return None,   // a scheduled call, not one that happened
        None => if duration > 0 { "connected" } else { "missed" },
    };
    let bare = |jid: &str| jid.parse::<Jid>().map(|j| j.to_non_ad_string()).unwrap_or_else(|_| jid.to_string());
    let mut peers: Vec<String> = Vec::new();
    for jid in r.call_creator_jid.iter().chain(r.participants.iter().filter_map(|p| p.user_jid.as_ref())) {
        let jid = bare(jid);
        if !jid.is_empty() && !peers.contains(&jid) {
            peers.push(jid);
        }
    }
    Some(CallEntry {
        id,
        ts,
        duration,
        incoming: r.is_incoming.unwrap_or(false),
        video: r.is_video.unwrap_or(false),
        result: result.to_string(),
        group_jid: r.group_jid.clone().map(|g| bare(&g)).unwrap_or_default(),
        peers,
    })
}

/// Call history that reached this PC before it kept any: pull the phone's app state once more
/// so those calls arrive. Later ones come as they happen.
pub(crate) fn resync_once(ctx: &Ctx, client: &Arc<Client>) {
    const FLAG: &str = "call_log_synced_v1";
    if ctx.db().flag(FLAG) {
        return;
    }
    // A first link: the sticker resync (which replays everything) hasn't run yet and brings these too.
    if !ctx.db().flag(crate::STICKERS_SYNCED) {
        ctx.db().set_flag(FLAG);
        return;
    }
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        use whatsapp_rust::sync_task::MajorSyncTask;
        use whatsapp_rust::wacore::appstate::hash::HashState;
        use whatsapp_rust::wacore::appstate::patch_decode::WAPatchName;
        // After the chat settings resync, which touches two of these.
        tokio::time::sleep(Duration::from_secs(20)).await;
        let backend = client.persistence_manager().backend();
        for name in [
            WAPatchName::CriticalBlock,
            WAPatchName::CriticalUnblockLow,
            WAPatchName::Regular,
            WAPatchName::RegularLow,
            WAPatchName::RegularHigh,
        ] {
            if let Err(e) = backend.set_version(name.as_str(), HashState::default()).await {
                warn!("could not reset {}: {e}", name.as_str());
            }
            let _ = backend.clear_mutation_macs(name.as_str()).await;
            client.process_sync_task(MajorSyncTask::AppStateSync { name, full_sync: true }).await;
        }
        ctx.db().set_flag(FLAG);
        info!("call history synced: {} calls", ctx.db().calls(1000).len());
        ctx.calls_dirty.notify_one();
    });
}

/// A link to a call anyone with WhatsApp can join.
pub(crate) fn create_link(ctx: &Ctx, client: &Arc<Client>, video: bool) {
    use whatsapp_rust::voip::CallLinkMedia;
    info!("call link: asked for (video: {video})");
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let media = if video { CallLinkMedia::Video } else { CallLinkMedia::Audio };
        match client.voip().create_call_link(media).await {
            Ok(link) => {
                // WhatsApp's links say "voice" where its protocol (and the library's own url()) says "audio".
                let url = format!("https://call.whatsapp.com/{}/{}", if video { "video" } else { "voice" }, link.token);
                // Asked back, to know the link is one WhatsApp recognises (the log says, for when one doesn't work).
                match client.voip().preview_call_link(&link.token, media).await {
                    Ok(preview) => info!("call link: made ({} characters, video: {video}); WhatsApp knows it (waiting room: {})", link.token.len(), preview.waiting_room_enabled),
                    Err(e) => warn!("call link: made ({} characters, video: {video}), but WhatsApp didn't recognise it: {e}", link.token.len()),
                }
                ctx.send(Out::CallLink { url, video });
            }
            Err(e) => {
                warn!("call link: {e}");
                ctx.send(Out::Notice { ok: false, text: "Couldn't create a call link. Try again in a moment.".into() });
            }
        }
    });
}
