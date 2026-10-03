//! Favourites (the chats' Favourites filter, the Calls page's Favourites), synced with the phone.
//!
//! WhatsApp keeps them as one app-state value, the whole list at a time (`favorites`). The
//! phone's list arrives through the library patch (wafluent_hooks) and goes to the app; a
//! change made here is written back the same way, so the phone and other linked devices follow.

use std::sync::Arc;
use std::time::Duration;

use log::{info, warn};
use whatsapp_rust::prelude::*;
use whatsapp_rust::wafluent_hooks::FavoritesMutation;

use crate::protocol::Event as Out;
use crate::{Ctx, wa};

/// Set once the phone's list has had its chance to arrive: from then on the app may write.
const SYNCED: &str = "favorites_synced_v1";

/// The connection, once there is one: ids that match no chat are looked up with it.
static CLIENT: std::sync::OnceLock<Arc<Client>> = std::sync::OnceLock::new();

/// The list as the app knows chats (and whether the phone's has been heard yet).
pub(crate) fn send(ctx: &Ctx) {
    let unmatched = send_now(ctx);
    if unmatched.is_empty() {
        return;
    }
    // The phone may name a person by the other form of their id (number or LID) than their
    // chat is kept under: WhatsApp is asked which chat it is, and the list sent again.
    let Some(client) = CLIENT.get().cloned() else { return };
    let ctx = ctx.clone();
    tokio::spawn(async move {
        let mut found = 0;
        for jid in &unmatched {
            let Ok(parsed) = jid.parse::<Jid>() else { continue };
            let chat = crate::chat_for(&ctx, &client, &parsed).await;
            let db = ctx.db();
            if db.chat(&chat).is_some() {
                found += 1;
            } else if db.is_saved(&chat) && (chat.ends_with("@lid") || chat.ends_with("@s.whatsapp.net")) {
                // A saved contact you've never written to: they get an (empty) chat, so they can be listed.
                db.ensure_chat(&chat, false);
                found += 1;
            }
        }
        info!("favourites: {} of {} that matched no chat were found", found, unmatched.len());
        if found > 0 {
            ctx.chats_dirty.notify_one();
            tokio::time::sleep(Duration::from_millis(900)).await;   // after the chat list
            send_now(&ctx);
        }
    });
}

/// Sends the list; returns the phone's ids that match no chat here.
fn send_now(ctx: &Ctx) -> Vec<String> {
    let mut unmatched = Vec::new();
    let (ids, synced) = {
        let db = ctx.db();
        let mut ids: Vec<String> = Vec::new();
        for jid in db.favorite_chats() {
            let mut chat = db.canonical(&jid);
            if db.chat(&chat).is_none() {
                // Kept under their number while the phone names their LID.
                match db.phone_jid(&jid).map(|pn| db.canonical(&pn)).filter(|pn| db.chat(pn).is_some()) {
                    Some(pn) => chat = pn,
                    None => {
                        let kind = jid.rsplit('@').next().unwrap_or("");
                        info!("favourites: one from the phone matches no chat (an @{kind} id)");
                        unmatched.push(jid.clone());
                    }
                }
            }
            if !ids.contains(&chat) {
                ids.push(chat);
            }
        }
        (ids, db.flag(SYNCED))
    };
    ctx.send(Out::Favourites { ids, synced });
    unmatched
}

/// The phone's list (or another device's change to it).
pub(crate) fn mutation(ctx: &Ctx, m: FavoritesMutation) {
    info!("favourites from the phone: {} (removed: {})", m.ids.len(), m.removed);
    ctx.db().set_favorite_chats(if m.removed { &[] } else { &m.ids });
    // The chat list first: a favourite with no messages on this PC is only listed because it is one.
    let chats = ctx.db().chats();
    ctx.send(Out::Chats { chats });
    send(ctx);
}

/// The app's list, kept and written to the phone.
pub(crate) fn set(ctx: &Ctx, client: &Arc<Client>, chats: Vec<String>) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        // Each chat under the id the phone used for it, when it's one the phone listed.
        let ids: Vec<String> = {
            let db = ctx.db();
            let known = db.favorite_chats();
            chats
                .iter()
                .map(|chat| known.iter().find(|jid| &db.canonical(jid) == chat).cloned().unwrap_or_else(|| chat.clone()))
                .collect()
        };
        let before = ctx.db().favorite_chats();
        ctx.db().set_favorite_chats(&ids);
        let value = wa::SyncActionValue {
            favorites_action: Some(wa::sync_action_value::FavoritesAction {
                favorites: ids
                    .iter()
                    .map(|id| wa::sync_action_value::favorites_action::Favorite { id: Some(id.clone()), ..Default::default() })
                    .collect(),
                ..Default::default()
            })
            .into(),
            timestamp: Some(crate::store::unix_now() * 1000),
            ..Default::default()
        };
        if let Err(e) = client.send_app_state_action(&whatsapp_rust::schemas::FAVORITES, &[], &value).await {
            warn!("favourites: couldn't be synced: {e}");
            // What the phone still has, so the app doesn't show a change that didn't happen.
            ctx.db().set_favorite_chats(&before);
            ctx.send(Out::Notice { ok: false, text: "Favourites couldn't be synced with your phone.".into() });
        } else {
            info!("favourites: {} synced to the phone", ids.len());
        }
        send(&ctx);
    });
}

/// Favourites set on the phone before this PC listened for them: its app state is read once
/// more (the one collection they're in), then the app is told it may merge and write.
pub(crate) fn resync_once(ctx: &Ctx, client: &Arc<Client>) {
    let _ = CLIENT.set(Arc::clone(client));
    if ctx.db().flag(SYNCED) {
        send(ctx);
        return;
    }
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        use whatsapp_rust::sync_task::MajorSyncTask;
        use whatsapp_rust::wacore::appstate::hash::HashState;
        use whatsapp_rust::wacore::appstate::patch_decode::WAPatchName;
        if ctx.db().flag(crate::STICKERS_SYNCED) {
            // After the other one-time resyncs, which read this collection too.
            tokio::time::sleep(Duration::from_secs(60)).await;
            let name = WAPatchName::RegularHigh;
            let backend = client.persistence_manager().backend();
            if let Err(e) = backend.set_version(name.as_str(), HashState::default()).await {
                warn!("could not reset {}: {e}", name.as_str());
            }
            let _ = backend.clear_mutation_macs(name.as_str()).await;
            client.process_sync_task(MajorSyncTask::AppStateSync { name, full_sync: true }).await;
        } else {
            // A first link: the sticker resync replays everything; wait for it to finish.
            for _ in 0..120 {
                tokio::time::sleep(Duration::from_secs(5)).await;
                if ctx.db().flag(crate::STICKERS_SYNCED) {
                    break;
                }
            }
        }
        ctx.db().set_flag(SYNCED);
        info!("favourites synced: {} on the phone", ctx.db().favorite_chats().len());
        send(&ctx);
    });
}
