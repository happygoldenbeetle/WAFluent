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

/// The list as the app knows chats (and whether the phone's has been heard yet).
pub(crate) fn send(ctx: &Ctx) {
    let (ids, synced) = {
        let db = ctx.db();
        let mut ids: Vec<String> = Vec::new();
        for jid in db.favorite_chats() {
            let chat = db.canonical(&jid);
            if !ids.contains(&chat) {
                ids.push(chat);
            }
        }
        (ids, db.flag(SYNCED))
    };
    ctx.send(Out::Favourites { ids, synced });
}

/// The phone's list (or another device's change to it).
pub(crate) fn mutation(ctx: &Ctx, m: FavoritesMutation) {
    info!("favourites from the phone: {} (removed: {})", m.ids.len(), m.removed);
    ctx.db().set_favorite_chats(if m.removed { &[] } else { &m.ids });
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
