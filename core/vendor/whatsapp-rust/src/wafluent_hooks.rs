//! WAFluent patch: app-state mutations the library drops (favourite stickers, the call
//! history, favourite chats), handed to the app. The only change to the vendored crate besides the call in
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

/// A synced call-history change: `index` as the phone sent it (`["call_log", ...]`),
/// `removed` for a Remove, `record` for a Set.
pub struct CallLogMutation {
    pub index: Vec<String>,
    pub removed: bool,
    pub record: Option<wa::CallLogRecord>,
}

type CallSink = Box<dyn Fn(CallLogMutation) + Send + Sync>;
static CALLS: OnceLock<CallSink> = OnceLock::new();

/// Receive call-history mutations (set once, at startup).
pub fn on_call_log(sink: impl Fn(CallLogMutation) + Send + Sync + 'static) {
    let _ = CALLS.set(Box::new(sink));
}

/// The synced list of favourite chats (the whole list each time); `removed`: it was cleared.
pub struct FavoritesMutation {
    pub ids: Vec<String>,
    pub removed: bool,
}

type FavoritesSink = Box<dyn Fn(FavoritesMutation) + Send + Sync>;
static FAVORITES: OnceLock<FavoritesSink> = OnceLock::new();

/// Receive the favourite-chats list (set once, at startup).
pub fn on_favorites(sink: impl Fn(FavoritesMutation) + Send + Sync + 'static) {
    let _ = FAVORITES.set(Box::new(sink));
}

/// Who may send a call's details: the call's creator, as upstream has it, or WhatsApp itself.
/// Its own messages about a call-link call (the roster and relay after a join, the waiting
/// room, the end) come from the call's JID (`<call id>@call`) with no participant, which only
/// the server can stamp; upstream drops those, so a joined link never connects.
#[cfg(feature = "voip-runtime")]
pub(crate) fn call_sender_ok(sender: &wacore_binary::Jid, creator: &wacore_binary::Jid, call_id: &str) -> bool {
    sender.to_non_ad() == creator.to_non_ad() || from_the_call(sender, call_id)
}

#[cfg(feature = "voip-runtime")]
fn from_the_call(sender: &wacore_binary::Jid, call_id: &str) -> bool {
    sender.server == wacore_binary::Server::Call && sender.user.as_str() == call_id
}

/// WhatsApp lists a device that has just joined a call-link call with participant id 0 until
/// its real one is given out; upstream takes 0 for an invalid snapshot and drops the whole
/// message (so the join is never acknowledged and the call ends). Here 0 means "none yet".
#[cfg(feature = "voip-runtime")]
pub(crate) fn settle_pids(update: &mut wacore::types::group_call::GroupCallUpdate) {
    for device in update.participants.iter_mut().flat_map(|p| p.devices.iter_mut()) {
        if device.pid == Some(0) {
            device.pid = None;
        }
    }
    log::info!(
        target: "wafluent_core",
        "call: snapshot {} of a group-style call: {} people, participant ids {:?}, relay: {}",
        update.transaction_id,
        update.participants.len(),
        update.participants.iter().flat_map(|p| p.devices.iter().map(|d| d.pid)).collect::<Vec<_>>(),
        update.relay.is_some()
    );
}

/// A call-link call as WhatsApp describes it in a `<call>` stanza: which link, and who's in it
/// (each as their LID and, when given, their number).
pub struct LinkCall {
    pub call_id: String,
    pub token: String,
    pub video: bool,
    pub people: Vec<(String, Option<String>)>,
}

type LinkCallSink = Box<dyn Fn(LinkCall) + Send + Sync>;
static LINK_CALLS: OnceLock<LinkCallSink> = OnceLock::new();

/// Receive call-link calls that are going on (set once, at startup): how the app learns that
/// someone has entered a link of yours, to ring you.
pub fn on_link_call(sink: impl Fn(LinkCall) + Send + Sync + 'static) {
    let _ = LINK_CALLS.set(Box::new(sink));
}

/// Called for every `<call>` stanza: hands over the ones that describe a call-link call.
#[cfg(feature = "voip-runtime")]
pub(crate) fn link_call(node: &wacore_binary::NodeRef<'_>) {
    fn find<'a, 'b>(node: &'b wacore_binary::NodeRef<'a>, wanted: &dyn Fn(&wacore_binary::NodeRef<'a>) -> bool, out: &mut Vec<&'b wacore_binary::NodeRef<'a>>) {
        if wanted(node) {
            out.push(node);
        }
        for child in node.children().unwrap_or_default() {
            find(child, wanted, out);
        }
    }
    let Some(sink) = LINK_CALLS.get() else { return };
    let mut infos = Vec::new();
    find(node, &|n| &*n.tag == "group_info" && n.get_attr("link-token").is_some(), &mut infos);
    let Some(info) = infos.first() else { return };
    let text = |n: &wacore_binary::NodeRef<'_>, key: &str| n.get_attr(key).map(|v| v.to_string());
    let (Some(token), Some(call_id)) = (text(info, "link-token"), text(info, "call-id")) else { return };
    let mut users = Vec::new();
    find(info, &|n| &*n.tag == "user" && n.get_attr("state").is_some_and(|s| *s == "connected"), &mut users);
    sink(LinkCall {
        call_id,
        token,
        video: text(info, "media").as_deref() == Some("video"),
        people: users.iter().filter_map(|u| Some((text(u, "jid")?, text(u, "user_pn")))).collect(),
    });
}

/// A stanza's structure for the log: tags, attribute names and content sizes, never values.
#[cfg(feature = "voip-runtime")]
pub(crate) fn shape(node: &wacore_binary::NodeRef<'_>) -> String {
    let attrs: Vec<String> = node.attrs_iter().map(|(name, _)| name.to_string()).collect();
    let inside = match node.children() {
        Some(children) if !children.is_empty() => format!("[{}]", children.iter().map(shape).collect::<Vec<_>>().join(" ")),
        _ => node.content_bytes().map(|b| format!("#{}", b.len())).unwrap_or_default(),
    };
    format!("{}({}){}", &*node.tag, attrs.join(","), inside)
}

/// [`call_sender_ok`] for a call that's registered: also the generation and creator must match.
#[cfg(feature = "voip-runtime")]
pub(crate) fn group_sender_authorized(
    registry: &wacore::voip::CallRegistry,
    call_id: &str,
    generation: u64,
    creator: &wacore_binary::Jid,
    sender: &wacore_binary::Jid,
) -> bool {
    registry.group_creator_authorized_if_current(call_id, generation, creator, sender)
        || (from_the_call(sender, call_id) && registry.group_creator_authorized_if_current(call_id, generation, creator, creator))
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
    let record = m.action_value.as_ref().and_then(|v| v.call_log_action.as_option()).and_then(|a| a.call_log_record.as_option().cloned());
    if m.index.first().map(String::as_str) == Some("call_log") || record.is_some() {
        if let Some(sink) = CALLS.get() {
            sink(CallLogMutation {
                index: m.index.clone(),
                removed: m.operation == wa::syncd_mutation::SyncdOperation::Remove,
                record,
            });
        }
        return true;
    }
    let favorites = m.action_value.as_ref().and_then(|v| v.favorites_action.as_option());
    if m.index.first().map(String::as_str) == Some("favorites") || favorites.is_some() {
        if let Some(sink) = FAVORITES.get() {
            sink(FavoritesMutation {
                ids: favorites.map(|f| f.favorites.iter().filter_map(|x| x.id.clone()).collect()).unwrap_or_default(),
                removed: m.operation == wa::syncd_mutation::SyncdOperation::Remove,
            });
        }
        return true;
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
