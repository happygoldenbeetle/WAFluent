//! WAFluent patch: app-state mutations the library drops (favourite stickers), handed to
//! the app. The only change to the vendored crate besides the call in
//! `client/app_state.rs`; see core/vendor/README.md.

use std::collections::BTreeMap;
use std::sync::{Mutex, OnceLock};
use waproto::whatsapp as wa;

/// A synced sticker change: `index` as the phone sent it (`["favoriteSticker", hash]`),
/// `removed` for a Remove, `action` for a Set.
pub struct StickerMutation {
    pub index: Vec<String>,
    pub removed: bool,
    pub action: Option<wa::sync_action_value::StickerAction>,
    pub full_sync: bool,
}

type Sink = Box<dyn Fn(StickerMutation) + Send + Sync>;
static STICKERS: OnceLock<Sink> = OnceLock::new();

/// Receive favourite-sticker mutations (set once, at startup).
pub fn on_sticker_mutation(sink: impl Fn(StickerMutation) + Send + Sync + 'static) {
    let _ = STICKERS.set(Box::new(sink));
}

static KINDS: Mutex<BTreeMap<String, usize>> = Mutex::new(BTreeMap::new());

/// How many mutations of each kind (index[0]) arrived since launch, for diagnostics.
pub fn mutation_kinds() -> Vec<(String, usize)> {
    KINDS.lock().map(|k| k.iter().map(|(a, b)| (a.clone(), *b)).collect()).unwrap_or_default()
}

/// Called for every decoded mutation; true when it was a sticker one.
pub(crate) fn dispatch(m: &crate::appstate_sync::Mutation, full_sync: bool) -> bool {
    if let (Ok(mut kinds), Some(kind)) = (KINDS.lock(), m.index.first()) {
        *kinds.entry(kind.clone()).or_default() += 1;
    }
    // Live changes from the phone, one line each (diagnoses what a phone action sends).
    if !full_sync && let Some(kind) = m.index.first() {
        log::info!(target: "wafluent_core", "app state change from the phone: {kind} ({:?})", m.operation);
    }
    let action = m.action_value.as_ref().and_then(|v| v.sticker_action.as_option().cloned());
    if m.index.first().map(String::as_str) != Some("favoriteSticker") && action.is_none() {
        return false;
    }
    if let Some(sink) = STICKERS.get() {
        sink(StickerMutation {
            index: m.index.clone(),
            removed: m.operation == wa::syncd_mutation::SyncdOperation::Remove,
            action,
            full_sync,
        });
    }
    true
}
