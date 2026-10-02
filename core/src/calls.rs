//! Voice calls: WhatsApp's own calling (whatsapp-rust's voip stack) joined to the app.
//!
//! The library does the signalling, the relay and the codec. The app has the microphone and
//! the speakers, so sound crosses the pipe both ways as `callAudio` lines: 60 ms of 16 kHz mono
//! 16-bit PCM (960 samples), base64. One call at a time; group calls aren't answered here.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use log::{info, warn};
use whatsapp_rust::prelude::*;
use whatsapp_rust::voip::{CallEvent, CallHandle};
use whatsapp_rust::wacore::types::call::{CallAction, IncomingCall};

use crate::protocol::Event as Out;
use crate::{Ctx, add_notice, chat_for, send_chat, store};

/// One frame: 60 ms at 16 kHz.
const FRAME_SAMPLES: usize = 960;
/// How long your call rings there before it gives up.
const RING_TIME: Duration = Duration::from_secs(60);

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
}

struct Ringing {
    incoming: IncomingCall,
    chat_id: String,
    video: bool,
}

struct Active {
    call_id: String,
    chat_id: String,
    handle: CallHandle,
    /// Your microphone, to the library.
    microphone: async_channel::Sender<Vec<i16>>,
    outgoing: bool,
    /// When the other side picked up (or you did), Unix seconds.
    connected_at: Option<i64>,
    /// Why it ended, once known.
    reason: Option<&'static str>,
    detail: Option<String>,
}

impl Ctx {
    fn calls(&self) -> std::sync::MutexGuard<'_, Calls> {
        self.calls.lock().unwrap_or_else(|p| p.into_inner())
    }

    fn call(&self, call_id: &str, chat_id: &str, outgoing: bool, state: &'static str, reason: Option<&'static str>, detail: Option<String>) {
        self.send(Out::Call { call_id: call_id.to_string(), chat_id: chat_id.to_string(), state, video: false, outgoing, reason, detail });
    }
}

// ───── From the app ─────

/// Calls a chat. The offer is sent, then `calling` until the other side answers.
pub(crate) fn start(ctx: &Ctx, client: &Arc<Client>, chat_id: String, video: bool) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let fail = |detail: &str| ctx.call("", &chat_id, true, "ended", Some("failed"), Some(detail.to_string()));
        if video {
            return fail("Video calls aren't available yet.");
        }
        let Ok(jid) = chat_id.parse::<Jid>() else { return fail("This chat can't be called.") };
        if chat_id.ends_with("@g.us") {
            return fail("Group calls aren't available yet.");
        }
        {
            let mut calls = ctx.calls();
            if calls.active.is_some() || calls.starting {
                drop(calls);
                return fail("You're already in a call.");
            }
            calls.starting = true;
            calls.cancelled = false;
        }
        let (mic_tx, mic_rx) = async_channel::bounded::<Vec<i16>>(8);
        let (speaker_tx, speaker_rx) = async_channel::bounded::<Vec<i16>>(16);
        let started = client.voip().call(&jid).audio(mic_rx, speaker_tx).start().await;
        let handle = match started {
            Ok(handle) => handle,
            Err(e) => {
                warn!("call: couldn't start: {e}");
                ctx.calls().starting = false;
                return fail(&failure(&e.to_string()));
            }
        };
        let call_id = handle.call_id().to_string();
        info!("call: ringing {call_id}");
        let cancelled = register(&ctx, Active {
            call_id: call_id.clone(),
            chat_id: chat_id.clone(),
            handle: handle.clone(),
            microphone: mic_tx,
            outgoing: true,
            connected_at: None,
            reason: None,
            detail: None,
        });
        ctx.call(&call_id, &chat_id, true, "calling", None, None);
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
        drive(ctx.clone(), handle, speaker_rx).await;
    });
}

/// Answers the call that's ringing.
pub(crate) fn accept(ctx: &Ctx, client: &Arc<Client>, call_id: String) {
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
        ctx.call(&call_id, &ring.chat_id, false, "connecting", None, None);
        let (mic_tx, mic_rx) = async_channel::bounded::<Vec<i16>>(8);
        let (speaker_tx, speaker_rx) = async_channel::bounded::<Vec<i16>>(16);
        let started = client.voip().accept(&ring.incoming).audio(mic_rx, speaker_tx).start().await;
        let handle = match started {
            Ok(handle) => handle,
            Err(e) => {
                warn!("call: couldn't answer {call_id}: {e}");
                ctx.calls().starting = false;
                return ctx.call(&call_id, &ring.chat_id, false, "ended", Some("failed"), Some(failure(&e.to_string())));
            }
        };
        info!("call: answered {call_id}");
        let cancelled = register(&ctx, Active {
            call_id: call_id.clone(),
            chat_id: ring.chat_id.clone(),
            handle: handle.clone(),
            microphone: mic_tx,
            outgoing: false,
            connected_at: Some(store::unix_now()),
            reason: None,
            detail: None,
        });
        ctx.call(&call_id, &ring.chat_id, false, "connected", None, None);
        if cancelled {
            end(&ctx, &client, "ended").await;
        }
        drive(ctx.clone(), handle, speaker_rx).await;
    });
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
    let Some(ring) = ctx.calls().ringing.remove(&call_id) else { return };
    if let Err(e) = client.voip().reject(&ring.incoming).await {
        warn!("call: couldn't decline {call_id}: {e}");
    }
    ctx.call(&call_id, &ring.chat_id, false, "ended", Some("declined"), None);
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
                calls.cancelled = calls.starting;
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

// ───── From WhatsApp ─────

/// Call signalling: someone is calling, or the other side answered / declined / hung up.
pub(crate) async fn signal(ctx: &Ctx, client: &Arc<Client>, call: &IncomingCall) {
    let call_id = call.action.call_id();
    match &call.action {
        CallAction::Offer { is_video, group_jid, caller_pn, .. } => {
            if call.group.is_some() || group_jid.is_some() {
                return;   // group calls keep ringing on the phone
            }
            let chat_id = chat_for(ctx, client, caller_pn.as_ref().unwrap_or(&call.from)).await;
            if ctx.db().ensure_chat(&chat_id, false) {
                send_chat(ctx, &chat_id);
            }
            info!("call: {call_id} ringing (video: {is_video})");
            ctx.calls().ringing.insert(call_id.to_string(), Ringing { incoming: call.clone(), chat_id: chat_id.clone(), video: *is_video });
            ctx.send(Out::Call { call_id: call_id.to_string(), chat_id, state: "ringing", video: *is_video, outgoing: false, reason: None, detail: None });
        }
        CallAction::Accept { .. } => {
            let answered = {
                let mut calls = ctx.calls();
                match calls.active.as_mut() {
                    Some(active) if active.call_id == call_id && active.outgoing && active.connected_at.is_none() => {
                        active.connected_at = Some(store::unix_now());
                        Some(active.chat_id.clone())
                    }
                    _ => None,
                }
            };
            if let Some(chat_id) = answered {
                info!("call: {call_id} answered");
                ctx.call(call_id, &chat_id, true, "connected", None, None);
            }
        }
        // "busy" is one of their devices that can't take calls; the others keep ringing.
        CallAction::Reject { reason, .. } if reason.as_deref() != Some("busy") => set_reason(ctx, call_id, "declined"),
        CallAction::Terminate { reason, .. } => {
            info!("call: {call_id} ended by the other side ({reason:?})");
            let answered = ctx.calls().active.as_ref().is_some_and(|a| a.call_id == call_id && a.connected_at.is_some());
            set_reason(ctx, call_id, if answered { "ended" } else { "noAnswer" });
        }
        _ => {}
    }
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
            ctx.call(call_id, &ring.chat_id, false, "ended", Some("missed"), None);
            (ring.chat_id, ring.video)
        }
        None => (chat_for(ctx, client, from).await, false),
    };
    if chat_id.ends_with("@g.us") || ctx.db().chat(&chat_id).is_none() {
        return;
    }
    add_notice(ctx, &chat_id, format!("Missed {} call", if video { "video" } else { "voice" }), ts);
}

/// The call was answered or declined on another of your devices: stop ringing here.
pub(crate) fn elsewhere(ctx: &Ctx, call_id: &str) {
    if let Some(ring) = ctx.calls().ringing.remove(call_id) {
        ctx.call(call_id, &ring.chat_id, false, "ended", Some("elsewhere"), None);
    }
}

// ───── A call in progress ─────

/// Runs until the call is over: the other side's voice goes to the app, and the end is told.
async fn drive(ctx: Ctx, handle: CallHandle, speaker: async_channel::Receiver<Vec<i16>>) {
    let call_id = handle.call_id().to_string();
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
        _ => {}
    }
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
    ctx.call(call_id, &active.chat_id, active.outgoing, "ended", Some(reason), active.detail);
    let now = store::unix_now();
    let text = match (active.connected_at, reason) {
        (Some(at), _) => format!("Voice call · {}", length(now - at)),
        (None, "failed") => return,
        (None, "declined") if active.outgoing => "Voice call · Declined".to_string(),
        (None, _) if active.outgoing => "Voice call · No answer".to_string(),
        (None, _) => "Missed voice call".to_string(),
    };
    add_notice(ctx, &active.chat_id, text, now);
}

/// 0:42, 12:05, 1:02:33.
fn length(seconds: i64) -> String {
    let s = seconds.max(0);
    if s >= 3600 { format!("{}:{:02}:{:02}", s / 3600, s % 3600 / 60, s % 60) } else { format!("{}:{:02}", s / 60, s % 60) }
}

/// What to tell you when a call couldn't be set up.
fn failure(error: &str) -> String {
    if error.contains("no resolvable devices") {
        "This contact can't take calls right now.".to_string()
    } else if error.contains("relay") {
        "Couldn't reach WhatsApp's call server.".to_string()
    } else {
        format!("The call couldn't be set up ({error}).")
    }
}
