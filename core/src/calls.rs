//! Calls: WhatsApp's own calling (whatsapp-rust's voip stack) joined to the app.
//!
//! The library does the signalling, the relay and the voice codec. The app has the microphone,
//! the speakers, the camera and the screen, so media crosses the pipe both ways, base64 in
//! lines: `callAudio` is 60 ms of 16 kHz mono 16-bit PCM (960 samples), and `callVideo` is one
//! H.264 access unit (the app encodes and decodes; the library only carries it). One call at a
//! time; group calls aren't answered here.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use log::{info, warn};
use whatsapp_rust::prelude::*;
use whatsapp_rust::voip::{CallEvent, CallHandle, VideoFrame, VideoState, VideoUpgradeToken};
use whatsapp_rust::wacore::stanza::call::{VideoStateParams, build_video_state};
use whatsapp_rust::wacore::types::call::{CallAction, IncomingCall};

use crate::protocol::Event as Out;
use crate::store::CallEntry;
use crate::{Ctx, add_notice, call_log, chat_for, send_chat, store};

/// One frame: 60 ms at 16 kHz.
const FRAME_SAMPLES: usize = 960;
/// How long your call rings there before it gives up.
const RING_TIME: Duration = Duration::from_secs(60);
/// RTCP "payload-specific feedback"; its formats 1 (PLI) and 4 (FIR) ask for a whole picture.
const RTCP_FEEDBACK: u8 = 206;

#[derive(Default)]
pub(crate) struct Calls {
    /// Calls ringing here, not answered yet: call id -> its offer.
    ringing: HashMap<String, Ringing>,
    /// The call in progress.
    active: Option<Active>,
    /// A call is being set up (no handle to hang up yet)...
    starting: bool,
    /// ...and was hung up meanwhile: end it as soon as it exists.
    cancelled: bool,
    /// Call links of yours that someone has entered, ringing here: call id -> the link.
    link_rings: HashMap<String, LinkRing>,
    /// Joining a call link (it can wait a long time to be let in): stopped by hanging up.
    joining: Option<tokio::task::AbortHandle>,
}

struct Ringing {
    incoming: IncomingCall,
    chat_id: String,
    video: bool,
}

/// Someone waiting in a call link: answering joins the link's call.
struct LinkRing {
    token: String,
    video: bool,
    chat_id: String,
}

/// Your camera in the call.
#[derive(Clone, Copy, PartialEq)]
enum Camera {
    /// The call has no video from you (a voice call).
    Off,
    On,
    /// Video is set up, but you turned the camera off: nothing is sent, theirs still shows.
    Paused,
}

struct Active {
    call_id: String,
    chat_id: String,
    handle: CallHandle,
    /// Your microphone, to the library.
    microphone: async_channel::Sender<Vec<i16>>,
    /// Your camera, to the library (the other end is handed over when video starts).
    camera: async_channel::Sender<Vec<u8>>,
    camera_feed: async_channel::Receiver<Vec<u8>>,
    /// Where the library puts their picture.
    view: async_channel::Sender<VideoFrame>,
    camera_state: Camera,
    /// They asked to switch to video and you haven't answered.
    video_request: Option<VideoUpgradeToken>,
    /// It was (or became) a video call: what the line left in the chat says.
    video: bool,
    outgoing: bool,
    /// When it started ringing, Unix seconds.
    started_at: i64,
    /// When the other side picked up (or you did), Unix seconds.
    connected_at: Option<i64>,
    /// Why it ended, once known.
    reason: Option<&'static str>,
    detail: Option<String>,
}

/// The channels a call's media goes through.
struct Media {
    microphone: (async_channel::Sender<Vec<i16>>, async_channel::Receiver<Vec<i16>>),
    speaker: (async_channel::Sender<Vec<i16>>, async_channel::Receiver<Vec<i16>>),
    camera: (async_channel::Sender<Vec<u8>>, async_channel::Receiver<Vec<u8>>),
    view: (async_channel::Sender<VideoFrame>, async_channel::Receiver<VideoFrame>),
}

impl Media {
    fn new() -> Self {
        Self {
            microphone: async_channel::bounded(8),
            speaker: async_channel::bounded(16),
            camera: async_channel::bounded(6),
            view: async_channel::bounded(16),
        }
    }

    fn active(&self, handle: &CallHandle, chat_id: &str, outgoing: bool, video: bool) -> Active {
        Active {
            call_id: handle.call_id().to_string(),
            chat_id: chat_id.to_string(),
            handle: handle.clone(),
            microphone: self.microphone.0.clone(),
            camera: self.camera.0.clone(),
            camera_feed: self.camera.1.clone(),
            view: self.view.0.clone(),
            camera_state: if video { Camera::On } else { Camera::Off },
            video_request: None,
            video,
            outgoing,
            started_at: store::unix_now(),
            connected_at: if outgoing { None } else { Some(store::unix_now()) },
            reason: None,
            detail: None,
        }
    }
}

impl Ctx {
    fn calls(&self) -> std::sync::MutexGuard<'_, Calls> {
        self.calls.lock().unwrap_or_else(|p| p.into_inner())
    }

    #[allow(clippy::too_many_arguments)]
    fn call(&self, call_id: &str, chat_id: &str, outgoing: bool, video: bool, state: &'static str, reason: Option<&'static str>, detail: Option<String>) {
        self.send(Out::Call { call_id: call_id.to_string(), chat_id: chat_id.to_string(), state, video, outgoing, reason, detail, link: false });
    }
}

// ───── From the app ─────

/// Calls a chat. The offer is sent, then `calling` until the other side answers.
pub(crate) fn start(ctx: &Ctx, client: &Arc<Client>, chat_id: String, video: bool) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let fail = |detail: &str| ctx.call("", &chat_id, true, video, "ended", Some("failed"), Some(detail.to_string()));
        let Ok(jid) = chat_id.parse::<Jid>() else { return fail("This chat can't be called.") };
        let group = chat_id.ends_with("@g.us");
        {
            let mut calls = ctx.calls();
            if calls.active.is_some() || calls.starting {
                drop(calls);
                return fail("You're already in a call.");
            }
            calls.starting = true;
            calls.cancelled = false;
        }
        let media = Media::new();
        let voip = client.voip();
        // A group chat: one call that rings everyone in it (WhatsApp takes up to 32 people).
        let started = if group {
            let mut call = voip.group_call_by_id(&jid).audio(media.microphone.1.clone(), media.speaker.0.clone());
            if video {
                call = call.video(media.camera.1.clone(), media.view.0.clone());
            }
            call.start().await
        } else {
            let mut call = voip.call(&jid).audio(media.microphone.1.clone(), media.speaker.0.clone());
            if video {
                call = call.video(media.camera.1.clone(), media.view.0.clone());
            }
            call.start().await
        };
        let handle = match started {
            Ok(handle) => handle,
            Err(e) => {
                warn!("call: couldn't start: {e}");
                ctx.calls().starting = false;
                return fail(&failure(&e.to_string()));
            }
        };
        let call_id = handle.call_id().to_string();
        info!("call: ringing {call_id} (video: {video}, group: {group})");
        let cancelled = register(&ctx, media.active(&handle, &chat_id, true, video));
        ctx.call(&call_id, &chat_id, true, video, "calling", None, None);
        if cancelled {
            end(&ctx, &client, "ended").await;
        }
        // Nobody picked up.
        tokio::spawn({
            let (ctx, client, call_id) = (ctx.clone(), Arc::clone(&client), call_id.clone());
            async move {
                tokio::time::sleep(RING_TIME).await;
                let unanswered = ctx.calls().active.as_ref().is_some_and(|a| a.call_id == call_id && a.connected_at.is_none());
                if unanswered {
                    end(&ctx, &client, "noAnswer").await;
                }
            }
        });
        drive(ctx.clone(), handle, media).await;
    });
}

/// Answers the call that's ringing; `video`: with your camera, when it's a video call.
pub(crate) fn accept(ctx: &Ctx, client: &Arc<Client>, call_id: String, video: bool) {
    // Someone waiting in a call link of yours: answering is joining that link.
    let waiting = ctx.calls().link_rings.remove(&call_id);
    if let Some(ring) = waiting {
        return join(ctx, client, ring.token, ring.video, Some((call_id, ring.chat_id)));
    }
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let ring = {
            let mut calls = ctx.calls();
            if calls.active.is_some() || calls.starting {
                return;
            }
            let Some(ring) = calls.ringing.remove(&call_id) else { return };
            calls.starting = true;
            calls.cancelled = false;
            ring
        };
        let video = video && ring.video;
        ctx.call(&call_id, &ring.chat_id, false, video, "connecting", None, None);
        let media = Media::new();
        let voip = client.voip();
        let mut call = voip.accept(&ring.incoming).audio(media.microphone.1.clone(), media.speaker.0.clone());
        if video {
            call = call.video(media.camera.1.clone(), media.view.0.clone());
        }
        let handle = match call.start().await {
            Ok(handle) => handle,
            Err(e) => {
                warn!("call: couldn't answer {call_id}: {e}");
                ctx.calls().starting = false;
                return ctx.call(&call_id, &ring.chat_id, false, video, "ended", Some("failed"), Some(failure(&e.to_string())));
            }
        };
        info!("call: answered {call_id} (video: {video})");
        let cancelled = register(&ctx, media.active(&handle, &ring.chat_id, false, video));
        ctx.call(&call_id, &ring.chat_id, false, video, "connected", None, None);
        if cancelled {
            end(&ctx, &client, "ended").await;
        }
        drive(ctx.clone(), handle, media).await;
    });
}

/// Joins the call behind a call link (yours or someone's): into its waiting room if it has one,
/// then the call. Whoever made the link is rung by WhatsApp when someone joins.
pub(crate) fn join_link(ctx: &Ctx, client: &Arc<Client>, url: String, video: bool) {
    // The code at the end of the link (its address says "voice" where the library says "audio").
    let token = url.trim().trim_end_matches('/').rsplit('/').next().unwrap_or("").split(['?', '#']).next().unwrap_or("").to_string();
    join(ctx, client, token, video, None);
}

/// Joins a call link's call. `answering`: someone is waiting in it and you were rung (the call
/// and their chat): told as a call of theirs you answered. Alone in it, this waits (WhatsApp
/// sets the call's media up once a second person is in) until hung up.
fn join(ctx: &Ctx, client: &Arc<Client>, token: String, video: bool, answering: Option<(String, String)>) {
    use whatsapp_rust::voip::CallLinkMedia;
    let outgoing = answering.is_none();
    let (rung_id, chat_id) = answering.unwrap_or_default();
    let (task_ctx, client) = (ctx.clone(), Arc::clone(client));
    {
        let mut calls = ctx.calls();
        if calls.active.is_some() || calls.starting {
            drop(calls);
            return ctx.call(&rung_id, &chat_id, outgoing, video, "ended", Some("failed"), Some("You're already in a call.".into()));
        }
        calls.starting = true;
        calls.cancelled = false;
    }
    let task = tokio::spawn(async move {
        let ctx = task_ctx;
        ctx.call(&rung_id, &chat_id, outgoing, video, "connecting", None, None);
        let media = Media::new();
        let voip = client.voip();
        let kind = if video { CallLinkMedia::Video } else { CallLinkMedia::Audio };
        let mut call = voip.call_link(&token, kind).audio(media.microphone.1.clone(), media.speaker.0.clone());
        if video {
            call = call.video(media.camera.1.clone(), media.view.0.clone());
        }
        let handle = match call.start().await {
            Ok(handle) => handle,
            Err(e) => {
                warn!("call link: couldn't join: {e}");
                let mut calls = ctx.calls();
                calls.starting = false;
                calls.joining = None;
                drop(calls);
                return ctx.call(&rung_id, &chat_id, outgoing, video, "ended", Some("failed"), Some(format!("The call link couldn't be joined ({e}).")));
            }
        };
        let call_id = handle.call_id().to_string();
        info!("call link: joined {call_id} (video: {video})");
        let mut active = media.active(&handle, &chat_id, outgoing, video);
        active.connected_at = Some(store::unix_now());
        ctx.calls().joining = None;
        let cancelled = register(&ctx, active);
        ctx.call(&call_id, &chat_id, outgoing, video, "connected", None, None);
        if cancelled {
            end(&ctx, &client, "ended").await;
        }
        drive(ctx.clone(), handle, media).await;
    });
    let mut calls = ctx.calls();
    if calls.starting && calls.active.is_none() {
        calls.joining = Some(task.abort_handle());
    }
}

/// The call exists now; true when it was hung up while it was being set up.
fn register(ctx: &Ctx, active: Active) -> bool {
    let mut calls = ctx.calls();
    calls.active = Some(active);
    calls.starting = false;
    std::mem::take(&mut calls.cancelled)
}

/// Declines the call that's ringing.
pub(crate) async fn reject(ctx: &Ctx, client: &Arc<Client>, call_id: String) {
    // Someone waiting in a call link: nothing to send, they just aren't joined.
    let waiting = ctx.calls().link_rings.remove(&call_id);
    if let Some(ring) = waiting {
        return ctx.call(&call_id, &ring.chat_id, false, ring.video, "ended", Some("declined"), None);
    }
    let Some(ring) = ctx.calls().ringing.remove(&call_id) else { return };
    if let Err(e) = client.voip().reject(&ring.incoming).await {
        warn!("call: couldn't decline {call_id}: {e}");
    }
    ctx.call(&call_id, &ring.chat_id, false, ring.video, "ended", Some("declined"), None);
    log(ctx, &call_id, &ring.chat_id, ring.incoming.timestamp.timestamp(), 0, true, ring.video, "rejected");
}

/// Writes a call into the history.
#[allow(clippy::too_many_arguments)]
fn log(ctx: &Ctx, call_id: &str, chat_id: &str, ts: i64, duration: i64, incoming: bool, video: bool, result: &str) {
    call_log::record(ctx, CallEntry {
        id: call_id.to_string(),
        ts,
        duration,
        incoming,
        video,
        result: result.to_string(),
        group_jid: if chat_id.ends_with("@g.us") { chat_id.to_string() } else { String::new() },
        peers: if chat_id.ends_with("@g.us") { Vec::new() } else { vec![chat_id.to_string()] },
    });
}

/// Hangs up (or stops calling). `reason` is kept unless one is already known.
pub(crate) async fn end(ctx: &Ctx, client: &Arc<Client>, reason: &'static str) {
    let handle = {
        let mut calls = ctx.calls();
        match calls.active.as_mut() {
            Some(active) => {
                active.reason.get_or_insert(reason);
                Some(active.handle.clone())
            }
            None => {
                if let Some(joining) = calls.joining.take() {
                    // Still waiting to be let into a call link: stop waiting.
                    joining.abort();
                    calls.starting = false;
                } else {
                    calls.cancelled = calls.starting;
                }
                None
            }
        }
    };
    let Some(handle) = handle else { return };
    let peer = handle.peer_jid();
    if let Err(e) = client.voip().terminate(handle.call_id(), &peer, handle.call_creator()).await {
        warn!("call: hanging up {} wasn't sent: {e}", handle.call_id());
    }
    handle.hangup().await;
}

pub(crate) fn mute(ctx: &Ctx, muted: bool) {
    if let Some(active) = ctx.calls().active.as_ref() {
        active.handle.set_muted(muted);
    }
}

/// A frame from your microphone.
pub(crate) fn microphone(ctx: &Ctx, data: &str) {
    let Ok(bytes) = BASE64.decode(data) else { return };
    if bytes.len() != FRAME_SAMPLES * 2 {
        return;   // the library drops any other length
    }
    let frame: Vec<i16> = bytes.chunks_exact(2).map(|b| i16::from_le_bytes([b[0], b[1]])).collect();
    if let Some(active) = ctx.calls().active.as_ref() {
        let _ = active.microphone.try_send(frame);   // full: the call is behind, drop it
    }
}

/// A picture from your camera (one H.264 access unit).
pub(crate) fn camera(ctx: &Ctx, data: &str) {
    let Ok(unit) = BASE64.decode(data) else { return };
    let dropped = match ctx.calls().active.as_ref() {
        Some(active) if active.camera_state == Camera::On => active.camera.try_send(unit).is_err(),
        _ => false,
    };
    if dropped {
        // The pictures after a lost one build on it: start again from a whole one.
        ctx.send(Out::CallVideoState { state: "keyframe" });
    }
}

/// Your camera, on or off. On in a voice call asks the other side to switch to video (or says
/// yes when they asked); off keeps their picture coming and only stops yours.
pub(crate) fn set_video(ctx: &Ctx, client: &Arc<Client>, on: bool) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let found = {
            let mut calls = ctx.calls();
            calls.active.as_mut().map(|a| (a.handle.clone(), a.camera_state, a.video_request.take(), a.camera_feed.clone(), a.view.clone()))
        };
        let Some((handle, state, request, feed, view)) = found else { return };
        let answering = request.is_some();
        let (result, next) = match (on, state) {
            (true, Camera::Paused) => (handle.announce_video_enabled().await.map_err(|e| e.to_string()), Camera::On),
            (true, Camera::Off) => {
                while feed.try_recv().is_ok() {}   // nothing left over from an earlier try
                let started = match request {
                    Some(token) => handle.accept_video(token, feed, view).await,
                    None => handle.start_video(feed, view).await,
                };
                (started.map_err(|e| e.to_string()), Camera::On)
            }
            (false, Camera::On) => (video_state(&client, &handle, VideoState::Paused).await, Camera::Paused),
            _ => return,
        };
        match result {
            Ok(()) => {
                if let Some(active) = ctx.calls().active.as_mut().filter(|a| a.call_id == handle.call_id()) {
                    active.camera_state = next;
                    active.video |= on;
                }
                if answering && on {
                    ctx.send(Out::CallVideoState { state: "on" });
                }
            }
            Err(e) => {
                warn!("call: video {} failed: {e}", if on { "on" } else { "off" });
                if on {
                    ctx.send(Out::CallVideoState { state: "failed" });
                }
            }
        }
    });
}

/// Tells the other side what your video is doing (the library has no call for "paused").
async fn video_state(client: &Client, handle: &CallHandle, state: VideoState) -> Result<(), String> {
    let id = format!("{:08X}{:08X}", crate::rand_u32(), crate::rand_u32());
    let stanza = build_video_state(&VideoStateParams {
        call_id: handle.call_id(),
        to: &handle.peer_jid(),
        id: &id,
        call_creator: handle.call_creator(),
        state,
        dec: None,
        device_orientation: Some(0),
    });
    client.send_node(stanza).await.map_err(|e| e.to_string())
}

// ───── From WhatsApp ─────

/// Call signalling: someone is calling, or the other side answered / declined / hung up.
pub(crate) async fn signal(ctx: &Ctx, client: &Arc<Client>, call: &IncomingCall) {
    let call_id = call.action.call_id();
    match &call.action {
        CallAction::Offer { is_video, group_jid, caller_pn, call_creator, .. } => {
            // A group-style call (someone joined your call link, a call with several people, a
            // group's call) rings like any other. It's shown as the group's when it belongs to
            // one, else as whoever started it.
            let group = call.group.is_some() || group_jid.is_some();
            let from_call = call.from.to_string().ends_with("@call");
            let caller = caller_pn.as_ref().unwrap_or(if from_call { call_creator } else { &call.from });
            let group_chat = group_jid.as_ref().map(|g| ctx.db().canonical(&g.to_non_ad_string())).filter(|g| ctx.db().chat(g).is_some());
            let chat_id = match group_chat {
                Some(group_chat) => group_chat,
                None => chat_for(ctx, client, caller).await,
            };
            if ctx.db().ensure_chat(&chat_id, chat_id.ends_with("@g.us")) {
                send_chat(ctx, &chat_id);
            }
            info!(
                "call: {call_id} ringing (video: {is_video}, group: {group}, of a group chat: {}, people in it: {})",
                group_jid.is_some(),
                call.group.as_ref().map_or(0, |g| g.participants.len())
            );
            ctx.calls().ringing.insert(call_id.to_string(), Ringing { incoming: call.clone(), chat_id: chat_id.clone(), video: *is_video });
            ctx.call(call_id, &chat_id, false, *is_video, "ringing", None, None);
        }
        CallAction::Accept { .. } => {
            let answered = {
                let mut calls = ctx.calls();
                match calls.active.as_mut() {
                    Some(active) if active.call_id == call_id && active.outgoing && active.connected_at.is_none() => {
                        active.connected_at = Some(store::unix_now());
                        Some((active.chat_id.clone(), active.video))
                    }
                    _ => None,
                }
            };
            if let Some((chat_id, video)) = answered {
                info!("call: {call_id} answered");
                ctx.call(call_id, &chat_id, true, video, "connected", None, None);
            }
        }
        // A call going on that this account could join (a group's): the phone shows it; noted for now.
        CallAction::OfferNotice { is_video, is_group, .. } => info!("call: {call_id} is going on (video: {is_video}, group: {is_group})"),
        // "busy" is one of their devices that can't take calls; the others keep ringing.
        CallAction::Reject { reason, .. } if reason.as_deref() != Some("busy") => set_reason(ctx, call_id, "declined"),
        CallAction::Terminate { reason, .. } => {
            info!("call: {call_id} ended by the other side ({reason:?})");
            // The call in a link of yours ended before you joined it: stop ringing.
            let waiting = ctx.calls().link_rings.remove(call_id);
            if let Some(ring) = waiting {
                ctx.call(call_id, &ring.chat_id, false, ring.video, "ended", Some("missed"), None);
            }
            let answered = ctx.calls().active.as_ref().is_some_and(|a| a.call_id == call_id && a.connected_at.is_some());
            set_reason(ctx, call_id, if answered { "ended" } else { "noAnswer" });
        }
        _ => {}
    }
}

/// A call-link call with people in it. When it's not one you're in (or joining) and someone
/// other than you is there, they're waiting in a link of yours: ring, once per call.
pub(crate) fn link_call(ctx: &Ctx, m: whatsapp_rust::wafluent_hooks::LinkCall) {
    let (chat_id, new_chat) = {
        let db = ctx.db();
        let mut calls = ctx.calls();
        if calls.active.is_some() || calls.starting || calls.link_rings.contains_key(&m.call_id) {
            return;
        }
        let bare = |jid: &str| jid.parse::<Jid>().map(|j| j.to_non_ad_string()).unwrap_or_else(|_| jid.to_string());
        // Whoever is in it that isn't you: under their number when there's a chat by it, else their LID.
        let Some(chat_id) = m.people.iter().find_map(|(lid, pn)| {
            let (lid, pn) = (bare(lid), pn.as_deref().map(bare));
            if db.is_me(&lid) || pn.as_ref().is_some_and(|pn| db.is_me(pn)) {
                return None;
            }
            let by_number = pn.map(|pn| db.canonical(&pn)).filter(|c| db.chat(c).is_some());
            Some(by_number.unwrap_or_else(|| db.canonical(&lid)))
        }) else {
            return;
        };
        info!("call link: someone is waiting in one of yours ({}, video: {})", m.call_id, m.video);
        calls.link_rings.insert(m.call_id.clone(), LinkRing { token: m.token.clone(), video: m.video, chat_id: chat_id.clone() });
        let new_chat = db.ensure_chat(&chat_id, false);
        (chat_id, new_chat)
    };
    if new_chat {
        send_chat(ctx, &chat_id);
    }
    ctx.send(Out::Call { call_id: m.call_id, chat_id, state: "ringing", video: m.video, outgoing: false, reason: None, detail: None, link: true });
}

fn set_reason(ctx: &Ctx, call_id: &str, reason: &'static str) {
    if let Some(active) = ctx.calls().active.as_mut().filter(|a| a.call_id == call_id) {
        active.reason.get_or_insert(reason);
    }
}

/// A call that rang (here, or while this PC was offline) and wasn't answered.
pub(crate) async fn missed(ctx: &Ctx, client: &Arc<Client>, from: &Jid, call_id: &str, ts: i64) {
    let ring = ctx.calls().ringing.remove(call_id);
    let (chat_id, video) = match ring {
        Some(ring) => {
            ctx.call(call_id, &ring.chat_id, false, ring.video, "ended", Some("missed"), None);
            (ring.chat_id, ring.video)
        }
        None => (chat_for(ctx, client, from).await, false),
    };
    if chat_id.ends_with("@g.us") {
        return;
    }
    log(ctx, call_id, &chat_id, ts, 0, true, video, "missed");
    if ctx.db().chat(&chat_id).is_none() {
        return;
    }
    add_notice(ctx, &chat_id, format!("Missed {} call", kind(video)), ts);
}

/// The call was answered or declined on another of your devices: stop ringing here.
pub(crate) fn elsewhere(ctx: &Ctx, call_id: &str) {
    if let Some(ring) = ctx.calls().ringing.remove(call_id) {
        ctx.call(call_id, &ring.chat_id, false, ring.video, "ended", Some("elsewhere"), None);
        log(ctx, call_id, &ring.chat_id, ring.incoming.timestamp.timestamp(), 0, true, ring.video, "elsewhere");
    }
}

// ───── A call in progress ─────

/// Runs until the call is over: the other side's voice and picture go to the app, and the end is told.
async fn drive(ctx: Ctx, handle: CallHandle, media: Media) {
    let call_id = handle.call_id().to_string();
    let speaker = media.speaker.1;
    let voice = tokio::spawn({
        let ctx = ctx.clone();
        async move {
            while let Ok(frame) = speaker.recv().await {
                let mut bytes = Vec::with_capacity(frame.len() * 2);
                for sample in frame {
                    bytes.extend_from_slice(&sample.to_le_bytes());
                }
                ctx.send(Out::CallAudio { data: BASE64.encode(bytes), opus: false });
            }
        }
    });
    let view = media.view.1;
    let picture = tokio::spawn({
        let ctx = ctx.clone();
        async move {
            // With several people their pictures arrive mixed, and the app shows one: whoever
            // came first, until they've sent nothing for two seconds.
            let mut shown: Option<String> = None;
            let mut last = std::time::Instant::now();
            while let Ok(frame) = view.recv().await {
                let sender = frame.sender.as_ref().map(|j| j.to_string());
                if sender != shown {
                    if (shown.is_some() && last.elapsed() < Duration::from_secs(2)) || !frame.keyframe {
                        continue;
                    }
                    shown = sender;
                }
                last = std::time::Instant::now();
                ctx.send(Out::CallVideo { data: BASE64.encode(&frame.data), key: frame.keyframe, rotation: frame.orientation });
            }
        }
    });
    let events = handle.events();
    let ended = handle.wait_ended();
    tokio::pin!(ended);
    loop {
        tokio::select! {
            _ = &mut ended => break,
            event = events.recv() => match event {
                Ok(event) => on_call_event(&ctx, &handle, event).await,
                Err(_) => {
                    ended.await;
                    break;
                }
            },
        }
    }
    voice.abort();
    picture.abort();
    // The signal that says why (declined, hung up) is handled alongside this: let it land.
    tokio::time::sleep(Duration::from_millis(200)).await;
    finish(&ctx, &call_id);
}

async fn on_call_event(ctx: &Ctx, handle: &CallHandle, event: CallEvent) {
    let failed = |detail: &str| {
        if let Some(active) = ctx.calls().active.as_mut().filter(|a| a.call_id == handle.call_id()) {
            active.reason.get_or_insert("failed");
            active.detail.get_or_insert(detail.to_string());
        }
    };
    match event {
        CallEvent::RelayAllocated => info!("call: {} media path is up", handle.call_id()),
        // The other side sent standard Opus instead of WhatsApp's own codec: the app decodes it.
        CallEvent::ForeignAudio(packet) => {
            let mut packet = packet.to_vec();
            if whatsapp_rust::voip::depacketize_opus_from_mlow(&mut packet).is_ok() {
                ctx.send(Out::CallAudio { data: BASE64.encode(packet), opus: true });
            }
        }
        CallEvent::AudioFormatMismatch { expected_rate, received_rates } => {
            warn!("call: the other side chose audio rates {received_rates:?}, not {expected_rate}");
        }
        CallEvent::RelayAllocateFailed(code) => {
            warn!("call: WhatsApp's relay refused the call ({code})");
            failed("WhatsApp's call server refused the call.");
            handle.hangup().await;
        }
        CallEvent::RelayAllocateTimedOut | CallEvent::RelayReconnectTimedOut => {
            warn!("call: WhatsApp's relay didn't answer");
            failed("Couldn't reach WhatsApp's call server.");
            handle.hangup().await;
        }
        CallEvent::VideoStateChanged { state, upgrade_token, .. } => peer_video(ctx, handle, state, upgrade_token),
        CallEvent::GroupUpdated(update) => group_updated(ctx, handle, &update),
        // They lost part of your picture and ask for a whole one.
        CallEvent::RtcpReceived { feedback, .. } if feedback.iter().any(|f| f.packet_type == RTCP_FEEDBACK && matches!(f.fmt, 1 | 4)) => {
            ctx.send(Out::CallVideoState { state: "keyframe" });
        }
        CallEvent::OutboundMediaDropped { video_access_units, .. } if video_access_units > 0 => {
            ctx.send(Out::CallVideoState { state: "keyframe" });
        }
        _ => {}
    }
}

/// Who's in a call with several people changed. The first person to join a call you started
/// is its "they picked up".
fn group_updated(ctx: &Ctx, handle: &CallHandle, update: &whatsapp_rust::voip::GroupCallUpdate) {
    let (names, waiting) = {
        let db = ctx.db();
        let others: Vec<_> = update
            .participants
            .iter()
            .filter(|p| !db.is_me(&p.jid.to_non_ad_string()) && !p.pn.as_ref().is_some_and(|pn| db.is_me(&pn.to_non_ad_string())))
            .collect();
        let names: Vec<String> = others
            .iter()
            .filter(|p| p.is_connected())
            .map(|p| db.person_name(&db.canonical(&p.pn.as_ref().unwrap_or(&p.jid).to_non_ad_string()), ""))
            .collect();
        let waiting = others.len() - names.len();
        (names, waiting)
    };
    info!("call: {} now has {} other people in it, {} still rung", handle.call_id(), names.len(), waiting);
    let picked_up = {
        let mut calls = ctx.calls();
        match calls.active.as_mut().filter(|a| a.call_id == handle.call_id()) {
            Some(active) if active.connected_at.is_none() && !names.is_empty() => {
                active.connected_at = Some(store::unix_now());
                Some((active.chat_id.clone(), active.outgoing, active.video))
            }
            _ => None,
        }
    };
    if let Some((chat_id, outgoing, video)) = picked_up {
        ctx.call(handle.call_id(), &chat_id, outgoing, video, "connected", None, None);
    }
    ctx.send(Out::CallPeople { names, waiting });
}

/// The other side's video changed: they ask to switch to it, turned theirs on or off, or refused yours.
fn peer_video(ctx: &Ctx, handle: &CallHandle, state: VideoState, request: Option<VideoUpgradeToken>) {
    info!("call: {} their video is {state:?}", handle.call_id());
    let mut calls = ctx.calls();
    let Some(active) = calls.active.as_mut().filter(|a| a.call_id == handle.call_id()) else { return };
    let told = if state.is_upgrade_request() {
        match request {
            Some(token) => {
                active.video_request = Some(token);
                "request"
            }
            None => "on",   // both asked at once: it's a video call now
        }
    } else {
        match state {
            VideoState::Enabled | VideoState::UpgradeAccept => "on",
            VideoState::Paused | VideoState::Stopped => "off",
            VideoState::UpgradeReject | VideoState::UpgradeRejectByTimeout => {
                active.camera_state = Camera::Off;
                "declined"
            }
            VideoState::Disabled | VideoState::UpgradeCancel | VideoState::UpgradeCancelByTimeout | VideoState::Error => {
                active.camera_state = Camera::Off;
                active.video_request = None;
                "ended"
            }
            _ => return,
        }
    };
    active.video |= told == "on";
    drop(calls);
    ctx.send(Out::CallVideoState { state: told });
}

/// The call is over: tell the app, and leave a line in the chat.
fn finish(ctx: &Ctx, call_id: &str) {
    let active = {
        let mut calls = ctx.calls();
        if !calls.active.as_ref().is_some_and(|a| a.call_id == call_id) {
            return;
        }
        calls.active.take()
    };
    let Some(active) = active else { return };
    let reason = active.reason.unwrap_or(match (active.connected_at, active.outgoing) {
        (Some(_), _) => "ended",
        (None, true) => "noAnswer",
        (None, false) => "missed",
    });
    info!("call: {call_id} over ({reason})");
    ctx.call(call_id, &active.chat_id, active.outgoing, active.video, "ended", Some(reason), active.detail);
    let now = store::unix_now();
    let result = match (active.connected_at, reason) {
        (Some(_), _) => "connected",
        (None, "declined") => "rejected",
        (None, "failed") => "failed",
        (None, _) if active.outgoing => "cancelled",
        (None, _) => "missed",
    };
    if active.chat_id.is_empty() {
        return;   // a call link you joined: nobody's chat to write it in
    }
    let talked = active.connected_at.map_or(0, |at| now - at);
    log(ctx, call_id, &active.chat_id, active.started_at, talked, !active.outgoing, active.video, result);
    if ctx.db().ensure_chat(&active.chat_id, active.chat_id.ends_with("@g.us")) {
        send_chat(ctx, &active.chat_id);
    }
    let call = format!("{} call", if active.video { "Video" } else { "Voice" });
    let text = match (active.connected_at, reason) {
        (Some(at), _) => format!("{call} · {}", length(now - at)),
        (None, "failed") => return,
        (None, "declined") if active.outgoing => format!("{call} · Declined"),
        (None, _) if active.outgoing => format!("{call} · No answer"),
        (None, _) => format!("Missed {} call", kind(active.video)),
    };
    add_notice(ctx, &active.chat_id, text, now);
}

fn kind(video: bool) -> &'static str {
    if video { "video" } else { "voice" }
}

/// 0:42, 12:05, 1:02:33.
fn length(seconds: i64) -> String {
    let s = seconds.max(0);
    if s >= 3600 { format!("{}:{:02}:{:02}", s / 3600, s % 3600 / 60, s % 60) } else { format!("{}:{:02}", s / 60, s % 60) }
}

/// What to tell you when a call couldn't be set up.
fn failure(error: &str) -> String {
    if error.contains("remote users") {
        "A group call needs between 2 and 31 other people in the group.".to_string()
    } else if error.contains("no resolvable devices") {
        "This contact can't take calls right now.".to_string()
    } else if error.contains("relay") {
        "Couldn't reach WhatsApp's call server.".to_string()
    } else {
        format!("The call couldn't be set up ({error}).")
    }
}
