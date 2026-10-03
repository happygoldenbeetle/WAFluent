//! wafluent-core: the WhatsApp connection behind WAFluent.
//!
//! Started by the WinUI app with redirected stdio. Events go out on stdout and
//! commands come in on stdin, one JSON object per line (see `protocol.rs`).
//! Logs go to stderr. Closing stdin shuts the connection down cleanly.
#![recursion_limit = "512"]

mod actions;
mod avatars;
mod call_log;
mod calls;
mod channels;
mod media;
mod extract;
mod favorites;
mod protocol;
mod status;
mod store;

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use log::{error, info, warn};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::{Notify, mpsc};
use whatsapp_rust::prelude::*;

use protocol::{Command, Event as Out};
use store::{ChatMeta, Store, StoredMessage};

type Tx = mpsc::UnboundedSender<Out>;
type Db = Arc<Mutex<Store>>;

/// An on-demand history request: its anchor, and the request id the answer will carry.
#[derive(Clone)]
struct Pending {
    ts: i64,
    id: String,
    request: Option<String>,
    /// The phone had nothing under the chat's address here, and was asked under its other one.
    other_tried: bool,
}

/// Everything the event handlers share.
#[derive(Clone)]
pub(crate) struct Ctx {
    tx: Tx,
    pub(crate) db: Db,
    pub(crate) data_dir: PathBuf,
    /// Pinged whenever the chat list changed in bulk; a debounced task sends one snapshot.
    pub(crate) chats_dirty: Arc<Notify>,
    /// Chats waiting for an on-demand history reply from the phone -> anchor (ts, message id).
    pending_history: Arc<Mutex<HashMap<String, Pending>>>,
    /// Profile-picture fetch queue (see avatars.rs).
    pub(crate) avatars: mpsc::UnboundedSender<avatars::Request>,
    /// Attachment download queue (see media.rs).
    media: mpsc::UnboundedSender<media::Request>,
    /// (chat, anchor message) pairs already re-requested from the phone to fill in media details.
    backfill: Arc<Mutex<HashSet<(String, String)>>>,
    /// Spaces those requests out so a long chat doesn't flood the phone.
    backfill_gate: Arc<tokio::sync::Mutex<()>>,
    /// When a media refill was last asked for, per chat: its answer isn't "nothing older".
    backfill_sent: Arc<Mutex<HashMap<String, std::time::Instant>>>,
    /// Pinged whenever the call history changed; a debounced task sends the list (call_log.rs).
    pub(crate) calls_dirty: Arc<Notify>,
    /// Pinged whenever the status updates changed; a debounced task sends the list (status.rs).
    pub(crate) statuses_dirty: Arc<Notify>,
    /// The call that's ringing or in progress (see calls.rs).
    pub(crate) calls: Arc<Mutex<calls::Calls>>,
}

impl Ctx {
    pub(crate) fn send(&self, event: Out) {
        let _ = self.tx.send(event);
    }

    fn status(&self, state: &'static str, detail: Option<String>) {
        self.send(Out::Status { state, detail });
    }

    pub(crate) fn db(&self) -> std::sync::MutexGuard<'_, Store> {
        self.db.lock().unwrap_or_else(|p| p.into_inner())
    }
}

fn main() {
    env_logger::Builder::from_env(
        // Call signalling is logged (ids only): a call that fails can only be understood from it.
        env_logger::Env::default().default_filter_or("info,whatsapp_rust=warn,wacore=warn,whatsapp_rust::handlers::call=debug"),
    )
    .target(env_logger::Target::Stderr)
    .init();

    let data_dir = data_dir();
    if let Err(e) = std::fs::create_dir_all(&data_dir) {
        eprintln!("cannot create {}: {e}", data_dir.display());
        std::process::exit(1);
    }

    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("tokio runtime");
    rt.block_on(run(data_dir));
}

/// `--data-dir <path>`, defaulting to %LOCALAPPDATA%\WAFluent.
fn data_dir() -> PathBuf {
    let args: Vec<String> = std::env::args().collect();
    if let Some(i) = args.iter().position(|a| a == "--data-dir")
        && let Some(dir) = args.get(i + 1)
    {
        return PathBuf::from(dir);
    }
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."));
    base.join("WAFluent")
}

async fn run(dir: PathBuf) {
    let (tx, mut rx) = mpsc::unbounded_channel::<Out>();

    // Single writer keeps stdout lines whole.
    let writer = tokio::spawn(async move {
        let mut out = tokio::io::stdout();
        while let Some(event) = rx.recv().await {
            let Ok(mut line) = serde_json::to_vec(&event) else { continue };
            line.push(b'\n');
            if out.write_all(&line).await.is_err() || out.flush().await.is_err() {
                break;
            }
        }
    });

    let store = match Store::open(&dir.join("wafluent.db")) {
        Ok(s) => s,
        Err(e) => {
            let _ = tx.send(Out::Status { state: "error", detail: Some(format!("Local database: {e}")) });
            return;
        }
    };
    let (avatar_tx, avatar_rx) = mpsc::unbounded_channel();
    let (media_tx, media_rx) = mpsc::unbounded_channel();
    let ctx = Ctx {
        tx,
        db: Arc::new(Mutex::new(store)),
        data_dir: dir.clone(),
        chats_dirty: Arc::new(Notify::new()),
        pending_history: Arc::default(),
        avatars: avatar_tx,
        media: media_tx,
        backfill: Arc::default(),
        backfill_gate: Arc::default(),
        backfill_sent: Arc::default(),
        calls: Arc::default(),
        calls_dirty: Arc::new(Notify::new()),
        statuses_dirty: Arc::new(Notify::new()),
    };
    ctx.status("starting", None);
    whatsapp_rust::wafluent_hooks::on_sticker_mutation({
        let ctx = ctx.clone();
        move |m| favorite_sticker(&ctx, m)
    });
    whatsapp_rust::wafluent_hooks::on_link_call({
        let ctx = ctx.clone();
        move |m| calls::link_call(&ctx, m)
    });
    whatsapp_rust::wafluent_hooks::on_favorites({
        let ctx = ctx.clone();
        move |m| favorites::mutation(&ctx, m)
    });
    whatsapp_rust::wafluent_hooks::on_channel_note({
        let ctx = ctx.clone();
        move |note| channels::note(&ctx, note)
    });
    whatsapp_rust::wafluent_hooks::on_call_log({
        let ctx = ctx.clone();
        move |m| call_log::mutation(&ctx, m)
    });

    ctx.db().call_cards_once();
    // Show what we already have while connecting.
    let cached = ctx.db().chats();
    if !cached.is_empty() {
        ctx.send(Out::Chats { chats: cached });
    }
    if let Some((_, path, _)) = ctx.db().avatar(avatars::SELF_ID).filter(|(_, p, _)| !p.is_empty()) {
        ctx.send(Out::Avatar { chat_id: avatars::SELF_ID.into(), path: Some(path) });
    }

    status::send(&ctx);   // the rail's dot: status updates kept from before
    spawn_snapshot_debouncer(ctx.clone());
    spawn_expiry(ctx.clone());
    call_log::spawn_debouncer(ctx.clone());
    status::spawn_debouncer(ctx.clone());
    call_log::backfill_notices_once(&ctx);
    avatars::retry_missing_once(&ctx);

    let session = dir.join("whatsapp.db");
    let backend = match SqliteStore::new(&session.to_string_lossy()).await {
        Ok(b) => b,
        Err(e) => {
            ctx.status("error", Some(format!("Session store: {e}")));
            return;
        }
    };

    let bot = Bot::builder()
        .with_backend(backend)
        .on_qr_code({
            let ctx = ctx.clone();
            move |code, timeout| {
                let ctx = ctx.clone();
                async move {
                    ctx.status("qr", None);
                    ctx.send(Out::Qr { code, timeout_secs: timeout.as_secs() });
                }
            }
        })
        .on_message({
            let ctx = ctx.clone();
            move |m| {
                let ctx = ctx.clone();
                async move {
                    if let Some(vote) = extract::poll_vote(&m.message) {
                        poll_vote(&ctx, &m.client, &m.info, vote).await;
                    } else {
                        on_message(&ctx, &m.message, &m.info);
                        // A group this PC hasn't a name for (it wasn't in the phone's history): ask for it.
                        let chat_id = m.info.source.chat.to_non_ad_string();
                        if ctx.db().group_is_nameless(&chat_id) {
                            name_group(&ctx, &m.client, chat_id);
                        }
                    }
                }
            }
        })
        .on_event({
            let ctx = ctx.clone();
            move |event, client| {
                let ctx = ctx.clone();
                async move { on_event(&ctx, &client, event).await }
            }
        })
        .build()
        .await;

    let bot = match bot {
        Ok(bot) => bot,
        Err(e) => {
            ctx.status("error", Some(format!("Could not start: {e}")));
            return;
        }
    };

    let handle = bot.spawn();
    let client = handle.client();
    avatars::spawn(ctx.clone(), Arc::clone(&client), avatar_rx);
    media::spawn(ctx.clone(), Arc::clone(&client), media_rx);

    // Commands from the UI until it closes stdin.
    let mut lines = BufReader::new(tokio::io::stdin()).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        let line = line.trim_start_matches('\u{feff}').trim();
        if line.is_empty() {
            continue;
        }
        match serde_json::from_str::<Command>(line) {
            Ok(cmd) => on_command(&ctx, &client, cmd).await,
            Err(e) => warn!("bad command {line:?}: {e}"),
        }
    }

    info!("stdin closed, shutting down");
    handle.shutdown().await;
    drop(ctx);
    let _ = tokio::time::timeout(Duration::from_secs(2), writer).await;
}

/// History sync and app-state sync produce hundreds of changes in bursts;
/// send at most one full chat list per 700 ms.
fn spawn_snapshot_debouncer(ctx: Ctx) {
    tokio::spawn(async move {
        loop {
            ctx.chats_dirty.notified().await;
            tokio::time::sleep(Duration::from_millis(700)).await;
            let chats = ctx.db().chats();
            ctx.send(Out::Chats { chats });
        }
    });
}

/// Disappearing messages: once a minute, messages whose time is up are removed here too.
fn spawn_expiry(ctx: Ctx) {
    tokio::spawn(async move {
        loop {
            let expired = ctx.db().expired(store::unix_now());
            let mut chats = std::collections::BTreeSet::new();
            for (chat_id, id) in expired {
                if chat_id == status::CHAT {
                    status::remove(&ctx, &id);   // a status update is 24 hours old
                    ctx.statuses_dirty.notify_one();
                    continue;
                }
                if ctx.db().delete_message(&chat_id, &id) {
                    ctx.send(Out::MessageRemoved { chat_id: chat_id.clone(), message_id: id });
                    chats.insert(chat_id);
                }
            }
            // Pins that ran out unpin themselves, like on the phone.
            chats.extend(ctx.db().expire_pins(store::unix_now()));
            for chat_id in chats {
                send_chat(&ctx, &chat_id);
            }
            tokio::time::sleep(Duration::from_secs(60)).await;
        }
    });
}

/// A notice in the chat ("You turned on disappearing messages…"), shown at once.
pub(crate) fn add_notice(ctx: &Ctx, chat_id: &str, text: String, ts: i64) {
    let stored = StoredMessage {
        id: format!("notice-{ts}-{:x}", rand_u32()),
        from_me: false,
        sender: String::new(),
        push_name: String::new(),
        ts,
        kind: "system".into(),
        text,
        file_name: String::new(),
        status: 0,
    };
    let dto = {
        let db = ctx.db();
        db.insert_message(chat_id, &stored);
        db.to_dto(chat_id, stored)
    };
    ctx.send(Out::Message { chat_id: chat_id.to_string(), message: dto });
}

/// A call's card in its chat (like the one the phone writes): on your side when you made the
/// call. `result`: connected | missed | rejected | cancelled; `duration`: seconds talked.
pub(crate) fn add_call_card(ctx: &Ctx, chat_id: &str, outgoing: bool, video: bool, result: &str, duration: i64, ts: i64) {
    let stored = StoredMessage {
        // "notice-": the Calls page has this call from its own log, not from this message.
        id: format!("notice-{ts}-{:x}", rand_u32()),
        from_me: outgoing,
        sender: String::new(),
        push_name: String::new(),
        ts,
        kind: "call".into(),
        text: extract::call_title(video, result == "missed").into(),
        file_name: String::new(),
        status: 0,
    };
    let details = serde_json::json!({ "call": { "video": video, "result": result, "duration": duration.max(0) } });
    let dto = {
        let db = ctx.db();
        db.insert_message(chat_id, &stored);
        db.insert_extra(chat_id, &stored.id, &[], Some(&details));
        db.to_dto(chat_id, stored)
    };
    ctx.send(Out::Message { chat_id: chat_id.to_string(), message: dto });
    send_chat(ctx, chat_id);
}

fn rand_u32() -> u32 {
    let mut b = [0u8; 4];
    let _ = getrandom::fill(&mut b);
    u32::from_le_bytes(b)
}

/// The chat's disappearing-messages timer changed (here, on the phone, or by the other side).
pub(crate) fn ephemeral_changed(ctx: &Ctx, chat_id: &str, seconds: u32, who: &str, ts: i64) {
    let previous = ctx.db().ephemeral(chat_id);
    if !ctx.db().set_ephemeral(chat_id, seconds) {
        return;   // already so (our own change coming back)
    }
    add_notice(ctx, chat_id, extract::ephemeral_notice(who, seconds, previous), ts);
    send_chat(ctx, chat_id);
}

async fn on_command(ctx: &Ctx, client: &Arc<Client>, cmd: Command) {
    // A channel isn't a chat: nobody is typing in it or online, and reading it tells no one.
    if let Command::WatchPresence { chat_id } | Command::SendTyping { chat_id, .. } | Command::MarkRead { chat_id } = &cmd
        && channels::is_channel(chat_id)
    {
        return;
    }
    match cmd {
        Command::LoadChannels => channels::load(ctx, client),
        Command::SearchChannels { query } => channels::search(ctx, client, query),
        Command::ChannelAction { chat_id, action } => channels::action(ctx, client, chat_id, action),
        Command::CallAudio { data } => calls::microphone(ctx, &data),
        Command::SetFavourites { ids } => favorites::set(ctx, client, ids),
        Command::JoinCallLink { url, video } => calls::join_link(ctx, client, url, video),
        Command::LoadCalls => call_log::send(ctx),
        Command::LoadStatuses => status::send(ctx),
        Command::StatusSeen { ids } => status::seen(ctx, client, ids),
        Command::ReplyStatus { id, text } => status::reply(ctx, client, id, text).await,
        Command::DeleteCall { id } => {
            if ctx.db().remove_call(&id) {
                call_log::send(ctx);
            }
        }
        Command::CreateCallLink { video } => call_log::create_link(ctx, client, video),
        Command::StartCall { chat_id, video } => calls::start(ctx, client, chat_id, video),
        Command::AcceptCall { call_id, video } => calls::accept(ctx, client, call_id, video),
        Command::CallVideo { data } => calls::camera(ctx, &data),
        Command::SetCallVideo { on } => calls::set_video(ctx, client, on),
        Command::RejectCall { call_id } => calls::reject(ctx, client, call_id).await,
        Command::EndCall => calls::end(ctx, client, "ended").await,
        Command::MuteCall { muted } => calls::mute(ctx, muted),
        Command::LoadMessages { chat_id, limit } => {
            let messages = ctx.db().messages(&chat_id, limit.unwrap_or(300));
            if channels::is_channel(&chat_id) {
                // What's kept at once (when there is any), then the channel's newest posts from WhatsApp.
                if !messages.is_empty() {
                    ctx.send(Out::Messages { chat_id: chat_id.clone(), messages });
                }
                channels::posts(ctx, client, chat_id, false);
                return;
            }
            ctx.send(Out::Messages { chat_id, messages });
        }
        Command::LoadOlderUntil { chat_id, before_ts, before_id, until_ts } => {
            let messages = {
                let db = ctx.db();
                let count = db.count_between(&chat_id, before_ts, &before_id, until_ts);
                db.messages_before(&chat_id, before_ts, &before_id, (count + 15).min(5000))
            };
            ctx.send(Out::OlderMessages { chat_id, messages, complete: false });
        }
        Command::MessageInfo { chat_id, message_id } => {
            let receipts = ctx.db().receipts(&chat_id, &message_id);
            ctx.send(Out::MessageInfo { chat_id, message_id, receipts });
        }
        Command::SearchMessages { chat_id, query } => {
            let (results, oldest_ts) = {
                let db = ctx.db();
                (db.search(&chat_id, &query), db.oldest_ts(&chat_id))
            };
            ctx.send(Out::SearchResults { chat_id, query, results, oldest_ts });
        }
        Command::FindMessageAt { chat_id, ts } => {
            let found = ctx.db().message_at(&chat_id, ts);
            ctx.send(Out::FoundMessage {
                chat_id,
                message_id: found.as_ref().map(|f| f.0.clone()),
                ts: found.map_or(ts, |f| f.1),
            });
        }
        Command::LoadOlder { chat_id, .. } if channels::is_channel(&chat_id) => channels::posts(ctx, client, chat_id, true),
        Command::LoadOlder { chat_id, before_ts, before_id, limit } => {
            load_older(ctx, client, chat_id, before_ts, before_id, limit.unwrap_or(100)).await;
        }
        Command::VotePoll { chat_id, message_id, options } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { vote_poll(&ctx, &client, chat_id, message_id, options).await });
        }
        Command::OpenNumber { phone } => open_number(ctx, client, &phone).await,
        Command::LoadStickers => {
            let (favorites, stickers, gifs) = {
                let db = ctx.db();
                (db.favorites(), db.recent_media("sticker", 120), db.recent_media("gif", 60))
            };
            ctx.send(Out::Stickers { favorites, stickers, gifs });
        }
        Command::SendMedia { chat_id, path, kind, caption, mime, width, height, seconds, thumb, temp_id, waveform } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            let media = actions::Outgoing { path, kind, caption, mime, width, height, seconds, thumb, waveform };
            let id = temp_id.clone();
            let task = tokio::spawn(async move {
                actions::send_media(&ctx, &client, chat_id, media, temp_id.clone()).await;
                actions::uploads().remove(&temp_id);
            });
            actions::uploads().insert(id, task.abort_handle());
        }
        Command::CancelSend { temp_id } => {
            if let Some(task) = actions::uploads().remove(&temp_id) {
                task.abort();
            }
        }
        Command::SendContacts { chat_id, contacts } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::send_contacts(&ctx, &client, chat_id, contacts).await });
        }
        Command::SendPoll { chat_id, question, options, multiple, hide_voters, end_time } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            let poll = actions::NewPoll { question, options, multiple, hide_voters, end_time };
            tokio::spawn(async move { actions::send_poll(&ctx, &client, chat_id, poll).await });
        }
        Command::SendGif { to, path, width, height, thumb } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::send_gif(&ctx, &client, to, path, width, height, thumb).await });
        }
        Command::SendStored { chat_id, message_id, to } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::forward(&ctx, &client, chat_id, message_id, vec![to], false).await });
        }
        Command::SetPresence { available } => {
            let client = Arc::clone(client);
            tokio::spawn(async move {
                let result = if available { client.presence().set_available().await } else { client.presence().set_unavailable().await };
                if let Err(e) = result {
                    warn!("presence {available} failed: {e}");
                }
            });
        }
        Command::WatchPresence { chat_id } => {
            if chat_id.ends_with("@g.us") {
                return; // group typing arrives without subscribing
            }
            let Ok(jid) = chat_id.parse::<Jid>() else { return };
            let client = Arc::clone(client);
            tokio::spawn(async move {
                if let Err(e) = client.presence().subscribe(jid).await {
                    warn!("presence subscribe {chat_id} failed: {e}");
                }
            });
        }
        Command::SendTyping { chat_id, state } => {
            use whatsapp_rust::features::ChatStateType;
            if ctx.db().is_me(&chat_id) {
                return;   // nobody to tell in "Message yourself"
            }
            let Ok(jid) = chat_id.parse::<Jid>() else { return };
            let state = match state.as_str() {
                "typing" => ChatStateType::Composing,
                "recording" => ChatStateType::Recording,
                _ => ChatStateType::Paused,
            };
            let client = Arc::clone(client);
            tokio::spawn(async move {
                if let Err(e) = client.chatstate().send(&jid, state).await {
                    warn!("typing state to {jid} failed: {e}");
                }
            });
        }
        Command::DownloadMedia { chat_id, message_id, force } => {
            let _ = ctx.media.send(media::Request { chat_id, message_id, force });
        }
        Command::BackfillMedia { chat_id, before_id } => {
            let anchor = {
                let db = ctx.db();
                match before_id {
                    Some(id) => db.message(&chat_id, &id).map(|m| (m.id, m.from_me, m.ts)),
                    None => db.newest(&chat_id),
                }
            };
            let (Some((id, from_me, ts)), Ok(jid)) = (anchor, chat_id.parse::<Jid>()) else { return };
            let key = (chat_id.clone(), id.clone());
            if !ctx.backfill.lock().unwrap_or_else(|p| p.into_inner()).insert(key.clone()) {
                return; // already asked this session
            }
            // One request at a time, 1.5 s apart; keep reading commands meanwhile.
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move {
                let _turn = ctx.backfill_gate.lock().await;
                ctx.backfill_sent.lock().unwrap_or_else(|p| p.into_inner()).insert(chat_id.clone(), std::time::Instant::now());
                if let Err(e) = client.fetch_message_history(&jid, &id, from_me, ts * 1000, 50).await {
                    warn!("media backfill request failed for {chat_id}: {e}");
                    ctx.backfill.lock().unwrap_or_else(|p| p.into_inner()).remove(&key);
                }
                tokio::time::sleep(Duration::from_millis(1500)).await;
            });
        }
        Command::GroupMembers { chat_id } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { group_members(&ctx, &client, chat_id).await });
        }
        Command::SendText { chat_id, text, reply_to, temp_id, mentions, link, everyone } => {
            // Sending waits on the server; keep reading commands meanwhile.
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { send_text(&ctx, &client, chat_id, text, reply_to, temp_id, mentions, link, everyone).await });
        }
        Command::ChatAction { chat_id, action, until_ms } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::chat_action(&ctx, &client, chat_id, action, until_ms).await });
        }
        Command::SaveContact { chat_id, first_name, last_name, sync_to_phone } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::save_contact(&ctx, &client, chat_id, first_name, last_name, sync_to_phone).await });
        }
        Command::ReportContact { chat_id } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::report_contact(&ctx, &client, chat_id).await });
        }
        Command::ExportChat { chat_id, path, utc_offset_minutes } => actions::export_chat(ctx, &chat_id, &path, utc_offset_minutes),
        Command::Forward { chat_id, message_id, to } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::forward(&ctx, &client, chat_id, message_id, to, true).await });
        }
        Command::PinMessage { chat_id, message_id, pin, duration } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::pin_message(&ctx, &client, chat_id, message_id, pin, duration).await });
        }
        Command::EditMessage { chat_id, message_id, text } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::edit_message(&ctx, &client, chat_id, message_id, text).await });
        }
        Command::StarMessage { chat_id, message_id, star } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::star_message(&ctx, &client, chat_id, message_id, star).await });
        }
        Command::DeleteMessage { chat_id, message_id, for_everyone } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::delete_message(&ctx, &client, chat_id, message_id, for_everyone).await });
        }
        Command::Report { chat_id, message_id } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::report(&ctx, &client, chat_id, message_id).await });
        }
        Command::LoadStarred => {
            let items = ctx.db().starred();
            ctx.send(Out::Starred { items });
        }
        Command::SetEphemeral { chat_id, seconds } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::set_ephemeral(&ctx, &client, chat_id, seconds).await });
        }
        Command::LoadContacts => {
            // What's stored, at once; then again with the block list as it is now (someone
            // blocked on the phone since the app connected).
            let contacts = ctx.db().contacts();
            ctx.send(Out::Contacts { contacts });
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move {
                update_blocklist(&ctx, &client).await;
                let contacts = ctx.db().contacts();
                ctx.send(Out::Contacts { contacts });
            });
        }
        Command::SetGroupSubject { chat_id, subject } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::set_group_subject(&ctx, &client, chat_id, subject).await });
        }
        Command::SetGroupDescription { chat_id, description } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::set_group_description(&ctx, &client, chat_id, description).await });
        }
        Command::SetGroupPicture { chat_id, path } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::set_group_picture(&ctx, &client, chat_id, path).await });
        }
        Command::AddGroupMembers { chat_id, members } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::add_group_members(&ctx, &client, chat_id, members).await });
        }
        Command::CreateGroup { subject, members } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::create_group(&ctx, &client, subject, members).await });
        }
        Command::SaveNewContact { phone, first_name, last_name, sync_to_phone } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move {
                let Some(chat_id) = resolve_number(&ctx, &client, &phone).await else { return };
                actions::save_contact(&ctx, &client, chat_id.clone(), first_name, last_name, sync_to_phone).await;
                ctx.send(Out::Opened { chat_id });
            });
        }
        Command::LoadChatMedia { chat_id } => {
            let (media, docs, links) = ctx.db().chat_media(&chat_id);
            ctx.send(Out::ChatMedia { chat_id, media, docs, links });
        }
        Command::SetPinned { chat_id, pinned } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { set_pinned(&ctx, &client, chat_id, pinned).await });
        }
        Command::React { chat_id, message_id, emoji } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { react(&ctx, &client, chat_id, message_id, emoji).await });
        }
        Command::MarkRead { chat_id } => {
            let chat = {
                let db = ctx.db();
                db.mark_read(&chat_id);
                db.chat(&chat_id)
            };
            if let Some(chat) = chat {
                ctx.send(Out::Chat { chat });
            }
        }
        Command::Logout => {
            client.logout().await;
            forget_everything(ctx);
        }
    }
}

/// A group's name, asked from WhatsApp and kept: for a group stored without one, which would
/// otherwise show as its number.
pub(crate) fn name_group(ctx: &Ctx, client: &Arc<Client>, chat_id: String) {
    let (ctx, client) = (ctx.clone(), client.clone());
    tokio::spawn(async move {
        let Ok(jid) = chat_id.parse::<Jid>() else { return };
        match client.groups().get_metadata(&jid).await {
            Ok(meta) if !meta.subject.is_empty() => {
                ctx.db().set_chat_name(&chat_id, &meta.subject);
                send_chat(&ctx, &chat_id);
            }
            Ok(_) => {}
            Err(e) => warn!("name of a group: {e}"),
        }
    });
}

/// A group's members for the @mention list (you left out), named as the chat list names them.
/// A group changed here: its members and details are read again and sent.
pub(crate) async fn refresh_group(ctx: &Ctx, client: &Arc<Client>, chat_id: String) {
    group_members(ctx, client, chat_id).await;
}

async fn group_members(ctx: &Ctx, client: &Arc<Client>, chat_id: String) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let meta = match client.groups().get_metadata(&jid).await {
        Ok(meta) => meta,
        Err(e) => {
            warn!("members of {chat_id}: {e}");
            return;
        }
    };
    let info = {
        let db = ctx.db();
        let mine = |p: &whatsapp_rust::features::GroupParticipant| {
            db.is_me(&p.jid.to_non_ad_string()) || p.phone_number.as_ref().is_some_and(|pn| db.is_me(&pn.to_non_ad_string()))
        };
        let creator = meta.creator_pn.as_ref().or(meta.creator.as_ref()).map(|j| j.to_non_ad_string());
        crate::protocol::GroupInfoDto {
            members: meta.participants.len() as u32,
            contacts: meta
                .participants
                .iter()
                .filter(|p| !mine(p))
                .filter(|p| db.is_saved(&p.phone_number.as_ref().unwrap_or(&p.jid).to_non_ad_string()))
                .count() as u32,
            creator: match &creator {
                Some(c) if db.is_me(c) => "You".to_string(),
                Some(c) => db.person_name(c, ""),
                None => String::new(),
            },
            created: meta.creation_time.unwrap_or(0) as i64,
            description: meta.description.clone().unwrap_or_default(),
            admin: meta.participants.iter().any(|p| mine(p) && p.is_admin()),
        }
    };
    ctx.send(Out::GroupInfo { chat_id: chat_id.clone(), info });
    if !meta.subject.is_empty() && ctx.db().group_is_nameless(&chat_id) {
        ctx.db().set_chat_name(&chat_id, &meta.subject);
        send_chat(ctx, &chat_id);
    }
    let members = {
        let db = ctx.db();
        meta.participants
            .iter()
            .filter(|p| !db.is_me(&p.jid.to_non_ad_string()))
            .map(|p| {
                let id = p.jid.to_non_ad_string();
                let pn = p.phone_number.as_ref().map(|j| j.to_non_ad_string());
                let name = db.person_name(pn.as_deref().unwrap_or(&id), "");
                crate::protocol::MemberDto {
                    chat_id: db.canonical(pn.as_deref().unwrap_or(&id)),
                    phone: pn.as_ref().map(|j| j.split('@').next().unwrap_or("").to_string()).unwrap_or_default(),
                    jid: id,
                    name,
                }
            })
            .collect::<Vec<_>>()
    };
    ctx.send(Out::GroupMembers { chat_id, members });
}

#[allow(clippy::too_many_arguments)]
async fn send_text(
    ctx: &Ctx,
    client: &Arc<Client>,
    chat_id: String,
    text: String,
    reply_to: Option<String>,
    temp_id: String,
    mentions: Vec<String>,
    link: Option<protocol::LinkPreview>,
    everyone: bool,
) {
    let jid = match chat_id.parse::<Jid>() {
        Ok(jid) => jid,
        Err(e) => {
            ctx.send(Out::SendFailed { chat_id, temp_id, reason: format!("Bad chat id: {e}") });
            return;
        }
    };

    // A reply carries a copy of what it quotes (kind, text) plus who wrote it.
    let quoted = reply_to.as_deref().and_then(|id| {
        let db = ctx.db();
        let m = db.message(&chat_id, id)?;
        let media = db.media_dto(&chat_id, id);
        Some((m, media))
    });
    let (message, quote) = match quoted {
        Some((m, media)) => {
            let participant = if m.from_me {
                let own = client.persistence_manager().get_device_snapshot();
                // Same address family as the chat: LID chats quote your LID.
                let mine = if jid.server == Server::Lid { own.lid.as_ref().or(own.pn.as_ref()) } else { own.pn.as_ref().or(own.lid.as_ref()) };
                mine.map(|j| j.to_non_ad_string()).unwrap_or_default()
            } else if m.sender.is_empty() {
                chat_id.clone()
            } else {
                m.sender.clone()
            };
            let (mime, seconds) = media.as_ref().map_or((String::new(), 0), |d| (d.mime.clone(), d.seconds));
            let quoted = extract::quoted_message(&m.kind, &m.text, &m.file_name, &mime, seconds);
            let context = whatsapp_rust::wacore::proto_helpers::build_quote_context(m.id.clone(), participant.clone(), &quoted);
            let quote = extract::Quote { id: m.id, sender: participant, kind: "", text: m.text, file_name: m.file_name };
            (wa::Message::text_with_context(text.clone(), context), Some(quote))
        }
        None => (wa::Message::text(text.clone()), None),
    };
    // @mentions ride in the context (a plain text message becomes an extended one).
    let message = if mentions.is_empty() && link.is_none() {
        message
    } else {
        let mut message = message;
        let mut ext = message.extended_text_message.as_option().cloned().unwrap_or_else(|| wa::message::ExtendedTextMessage {
            text: Some(text.clone()),
            ..Default::default()
        });
        if !mentions.is_empty() {
            let mut context = ext.context_info.as_option().cloned().unwrap_or_default();
            context.mentioned_jid = mentions.clone();
            // "@all": the everyone marker newer apps show, plus every member's mention (so all are pinged).
            if everyone {
                context.non_jid_mentions = Some(1);
            }
            ext.context_info = MessageField::some(context);
        }
        // The link card: WhatsApp shows it from these fields.
        if let Some(link) = &link {
            ext.matched_text = Some(link.url.clone());
            ext.title = (!link.title.is_empty()).then(|| link.title.clone());
            ext.description = (!link.description.is_empty()).then(|| link.description.clone());
            ext.jpeg_thumbnail = link.thumb.as_deref().and_then(|p| std::fs::read(p).ok()).filter(|t| !t.is_empty());
        }
        message.conversation = None;
        message.extended_text_message = MessageField::some(ext);
        message
    };

    // The card is kept like a received one (thumb and link details), so the bubble shows it.
    let card = link.is_some().then(|| extract::content(&message)).flatten();
    let timer = ctx.db().ephemeral(&chat_id);
    let sent = match client.send_message(jid, extract::with_expiration(message, timer)).await {
        Ok(sent) => sent,
        Err(e) => {
            warn!("send to {chat_id} failed: {e}");
            ctx.send(Out::SendFailed { chat_id, temp_id, reason: e.to_string() });
            return;
        }
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
        db.ensure_chat(&chat_id, chat_id.ends_with("@g.us"));
        db.insert_message(&chat_id, &stored);
        db.set_expiry(&chat_id, &stored.id, stored.ts, timer);
        if let Some(card) = &card {
            db.insert_extra(&chat_id, &stored.id, &card.thumb, card.extra.as_ref());
        }
        if let Some(quote) = &quote {
            db.insert_quote(&chat_id, &stored.id, quote);
        }
        (db.to_dto(&chat_id, stored), db.chat(&chat_id))
    };
    ctx.send(Out::Sent { chat_id, temp_id, message: dto });
    if let Some(chat) = chat {
        ctx.send(Out::Chat { chat });
    }
}

/// Delete-for-everyone, edits and pins arriving as messages.
fn apply_control(ctx: &Ctx, chat_id: &str, control: extract::Control, who: &str) {
    match control {
        extract::Control::Revoke(id) => {
            if ctx.db().set_deleted(chat_id, &id) {
                send_message_update(ctx, chat_id, &id);
                send_chat(ctx, chat_id);
            }
        }
        extract::Control::Edit(id, content) => {
            if ctx.db().set_edited(chat_id, &id, &content.text) {
                send_message_update(ctx, chat_id, &id);
                send_chat(ctx, chat_id);
            }
        }
        extract::Control::Pin(id, pinned, duration, at_ms) => {
            apply_pin(&ctx.db(), chat_id, &id, pinned, duration, at_ms);
            if pinned {
                // "Abdullah pinned a message", like the phone shows in the chat.
                add_notice(ctx, chat_id, format!("{who} pinned a message"), if at_ms > 0 { at_ms / 1000 } else { store::unix_now() });
            }
            send_chat(ctx, chat_id);
        }
    }
}

/// A pin or unpin, from the phone, the other side or history: pins last their duration
/// (7 days when it isn't said) from when they were made.
fn apply_pin(db: &store::Store, chat_id: &str, message_id: &str, pinned: bool, duration: u32, at_ms: i64) {
    if !pinned {
        db.remove_pin(chat_id, message_id);
        return;
    }
    let at = if at_ms > 0 { at_ms / 1000 } else { store::unix_now() };
    let duration = if duration > 0 { duration } else { 604_800 };
    db.add_pin(chat_id, message_id, at, at + i64::from(duration));
}

pub(crate) fn send_chat(ctx: &Ctx, chat_id: &str) {
    if let Some(chat) = ctx.db().chat(chat_id) {
        ctx.send(Out::Chat { chat });
    }
}

pub(crate) fn send_message_update(ctx: &Ctx, chat_id: &str, message_id: &str) {
    let dto = {
        let db = ctx.db();
        db.message(chat_id, message_id).map(|m| db.to_dto(chat_id, m))
    };
    if let Some(message) = dto {
        ctx.send(Out::MessageUpdated { chat_id: chat_id.to_string(), message });
    }
}

/// "Mark as unread": a dot in the list (no number), like the phone, until the chat is opened.
pub(crate) fn mark_unread(ctx: &Ctx, chat_id: &str) {
    ctx.db().set_marked_unread(chat_id, true);
}

/// Who you blocked, from the server (the phone manages the list too).
fn refresh_blocklist(ctx: &Ctx, client: &Arc<Client>) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move { update_blocklist(&ctx, &client).await });
}

/// Fetches the block list and stores it (with each blocked person's number where known).
async fn update_blocklist(ctx: &Ctx, client: &Arc<Client>) {
    {
        let Ok(entries) = client.blocking().get_blocklist().await else { return };
        let mut ids = Vec::new();
        let (total, mut numbered) = (entries.len(), 0);
        for entry in entries {
            // WhatsApp lists blocks by LID; contacts are known by number. Keep the number
            // for each, so a blocked contact is recognised even with no chat.
            if entry.jid.is_lid()
                && let Ok(Some(known)) = client.get_lid_pn_entry(&entry.jid).await
            {
                ctx.db().set_number(&entry.jid.to_non_ad_string(), &known.phone_number);
                numbered += 1;
            }
            ids.push(chat_for(&ctx, &client, &entry.jid).await);
        }
        // Still unknown: ask WhatsApp for each saved contact's LID (once per contact list),
        // so the blocked LIDs can be matched to contacts by number.
        if numbered < total {
            let contacts = ctx.db().contact_number_jids();
            let flag = format!("contact_lids_v1_{}", contacts.len());
            if !ctx.db().flag(&flag) {
                let mut learned = 0;
                for chunk in contacts.chunks(100) {
                    let jids: Vec<Jid> = chunk.iter().filter_map(|j| j.parse().ok()).collect();
                    match client.contacts().is_on_whatsapp(&jids).await {
                        Ok(results) => {
                            let db = ctx.db();
                            for r in results {
                                let number = r.pn_jid.as_ref().unwrap_or(&r.jid);
                                if let Some(lid) = r.lid.as_ref().filter(|_| number.is_pn()) {
                                    db.set_number(&lid.to_non_ad_string(), &number.user);
                                    learned += 1;
                                } else if r.jid.is_lid() && let Some(pn) = &r.pn_jid {
                                    db.set_number(&r.jid.to_non_ad_string(), &pn.user);
                                    learned += 1;
                                }
                            }
                        }
                        Err(e) => warn!("looking up contacts' LIDs failed: {e}"),
                    }
                }
                ctx.db().set_flag(&flag);
                info!("contact LIDs learned: {learned} of {}", contacts.len());
            }
        }
        info!("blocklist: {total} blocked, {numbered} with a known number");
        ctx.db().set_blocklist(&ids);
        ctx.chats_dirty.notify_one();
    }
}

/// Applies one app-state change to a chat and refreshes the list.
async fn chat_setting(ctx: &Ctx, client: &Arc<Client>, jid: &Jid, apply: impl FnOnce(&Store, &str)) {
    let id = chat_for(ctx, client, jid).await;
    let chat = {
        let db = ctx.db();
        apply(&db, &id);
        db.chat(&id)
    };
    if let Some(chat) = chat {
        ctx.send(Out::Chat { chat });
    }
    ctx.chats_dirty.notify_one();
}

/// The stored chat for a JID. The phone may name a chat by phone number while we keep
/// it under its LID (or the reverse): try the other form from the LID<->number map.
pub(crate) async fn chat_for(ctx: &Ctx, client: &Arc<Client>, jid: &Jid) -> String {
    let raw = jid.to_non_ad_string();
    let id = ctx.db().canonical(&raw);
    if ctx.db().chat(&id).is_some() {
        return id;
    }
    if let Ok(Some(entry)) = client.get_lid_pn_entry(jid).await {
        let other = if raw.ends_with("@lid") {
            format!("{}@s.whatsapp.net", entry.phone_number)
        } else {
            format!("{}@lid", entry.lid)
        };
        let db = ctx.db();
        let other = db.canonical(&other);
        if db.chat(&other).is_some() {
            db.add_alias(&raw, &other);
            return other;
        }
    }
    id
}

/// LID-only chats show a phone number once we know it: copy the connection's
/// LID<->number map (display only; it doesn't merge chats).
async fn learn_phone_numbers(ctx: &Ctx, client: &Arc<Client>) {
    let lids = ctx.db().lid_chats_without_number();
    let mut learned = 0;
    for lid in lids {
        let Ok(jid) = lid.parse::<Jid>() else { continue };
        if let Ok(Some(entry)) = client.get_lid_pn_entry(&jid).await {
            ctx.db().set_number(&lid, &entry.phone_number);
            learned += 1;
        }
    }
    if learned > 0 {
        info!("phone numbers learned for {learned} chats");
        ctx.chats_dirty.notify_one();
    }
}

/// Pins (regular_low) and mutes (regular_high) were ignored before; pull the full
/// state from the phone once so the list matches it. Later changes arrive as events.
/// The stored collection versions are reset first: regular_low was stuck on a patch
/// the server keeps resending ("expected 15, got 14"), so no pin change got through.
fn resync_chat_settings_once(ctx: &Ctx, client: &Arc<Client>) {
    const FLAG: &str = "chat_settings_synced_v3";
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        learn_phone_numbers(&ctx, &client).await;
        if ctx.db().flag(FLAG) {
            return;
        }
        use whatsapp_rust::sync_task::MajorSyncTask;
        use whatsapp_rust::wacore::appstate::patch_decode::WAPatchName;
        use whatsapp_rust::wacore::appstate::hash::HashState;
        ctx.db().clear_pins();
        let backend = client.persistence_manager().backend();
        for name in [WAPatchName::RegularLow, WAPatchName::RegularHigh] {
            if let Err(e) = backend.set_version(name.as_str(), HashState::default()).await {
                warn!("could not reset {}: {e}", name.as_str());
            }
            let _ = backend.clear_mutation_macs(name.as_str()).await;
            client.process_sync_task(MajorSyncTask::AppStateSync { name, full_sync: true }).await;
        }
        ctx.db().set_flag(FLAG);
        info!("chat settings resynced: {} pinned", ctx.db().pinned_count());
        ctx.chats_dirty.notify_one();
    });
}

/// A sticker starred or unstarred on the phone (app state, via the patched library).
fn favorite_sticker(ctx: &Ctx, m: whatsapp_rust::wafluent_hooks::StickerMutation) {
    let action = m.action.as_ref();
    let key = m.index.get(1).cloned().or_else(|| action.and_then(|a| a.image_hash.clone())).unwrap_or_default();
    if key.is_empty() {
        return;
    }
    let media = action.filter(|a| !m.removed && a.is_favorite != Some(false)).and_then(|a| {
        Some(extract::Media {
            media_type: "sticker",
            direct_path: a.direct_path.clone()?,
            media_key: a.media_key.clone()?,
            file_sha256: Vec::new(),
            file_enc_sha256: a.file_enc_sha256.clone()?,
            file_length: a.file_length.unwrap_or(0),
            mimetype: a.mimetype.clone().unwrap_or_else(|| "image/webp".into()),
            width: a.width.unwrap_or(0),
            height: a.height.unwrap_or(0),
            seconds: 0,
            waveform: Vec::new(),
        })
    });
    info!("favourite sticker {} (full sync: {})", if media.is_some() { "added" } else { "removed" }, m.full_sync);
    match media {
        Some(media) => ctx.db().set_favorite(&key, &media),
        None => ctx.db().remove_favorite(&key),
    }
    ctx.send(Out::FavoritesChanged);
}

pub(crate) const STICKERS_SYNCED: &str = "favorite_stickers_synced_v3";

/// Favourite stickers were dropped before the library patch; pull the app state once more
/// so the ones starred earlier arrive too. Later changes come as they happen. v3: every
/// collection (the critical ones too), in case the phone keeps favourites in one of those.
fn resync_stickers_once(ctx: &Ctx, client: &Arc<Client>) {
    const FLAG: &str = STICKERS_SYNCED;
    if ctx.db().flag(FLAG) {
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
        info!(
            "favourite stickers synced: {} (app state seen: {:?})",
            ctx.db().favorites().len(),
            whatsapp_rust::wafluent_hooks::mutation_kinds()
        );
    });
}

async fn set_pinned(ctx: &Ctx, client: &Arc<Client>, chat_id: String, pinned: bool) {
    let Ok(jid) = chat_id.parse::<Jid>() else { return };
    let actions = client.chat_actions();
    let result = if pinned { actions.pin_chat(&jid).await } else { actions.unpin_chat(&jid).await };
    match result {
        Ok(()) => ctx.db().set_pinned(&chat_id, if pinned { store::unix_now() } else { 0 }),
        Err(e) => warn!("pin {chat_id} failed: {e}"),
    }
    // The stored state goes back either way (a failure puts the old one back).
    if let Some(chat) = ctx.db().chat(&chat_id) {
        ctx.send(Out::Chat { chat });
    }
}

/// Sends your reaction; the stored reactions go back to the UI either way
/// (so a failed send puts the old state back).
async fn react(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, emoji: String) {
    if channels::is_channel(&chat_id) {
        channels::react(ctx, client, chat_id, message_id, emoji);
        return;
    }
    let target = ctx.db().message(&chat_id, &message_id);
    let (Some(target), Ok(jid)) = (target, chat_id.parse::<Jid>()) else {
        send_reactions(ctx, chat_id, message_id);
        return;
    };
    let participant = if !chat_id.ends_with("@g.us") {
        None
    } else if target.from_me {
        let own = client.persistence_manager().get_device_snapshot();
        own.pn.as_ref().or(own.lid.as_ref()).map(|j| j.to_non_ad_string())
    } else {
        Some(target.sender.clone())
    };
    let key = wa::MessageKey {
        remote_jid: Some(chat_id.clone()),
        from_me: Some(target.from_me),
        id: Some(message_id.clone()),
        participant,
    };
    match client.send_reaction(jid, key, &emoji).await {
        Ok(_) => ctx.db().set_reaction(&chat_id, &message_id, "me", &emoji, store::unix_now() * 1000),
        Err(e) => warn!("reaction in {chat_id} failed: {e}"),
    }
    send_reactions(ctx, chat_id, message_id);
}

fn send_reactions(ctx: &Ctx, chat_id: String, message_id: String) {
    let (reactions, my_reaction) = ctx.db().reactions(&chat_id, &message_id);
    ctx.send(Out::Reactions { chat_id, message_id, reactions, my_reaction });
}

/// Logged out: the local copy of chats, names and pictures goes too.
fn forget_everything(ctx: &Ctx) {
    ctx.db().clear();
    avatars::clear_cache(ctx);
    media::clear_cache(ctx);
    ctx.send(Out::Chats { chats: Vec::new() });
    ctx.send(Out::Avatar { chat_id: avatars::SELF_ID.into(), path: None });
    ctx.status("loggedOut", None);
}

async fn on_event(ctx: &Ctx, client: &Arc<Client>, event: Arc<Event>) {
    match &*event {
        Event::Connected(_) => {
            let own = client.persistence_manager().get_device_snapshot();
            ctx.db().set_me([&own.pn, &own.lid].into_iter().flatten().map(|j| j.to_non_ad_string()).collect());
            ctx.status("connected", None);
            ctx.send(Out::Me { name: own.push_name.clone(), phone: own.pn.as_ref().map(|j| j.user.to_string()).unwrap_or_default() });
            ctx.chats_dirty.notify_one();
            resync_chat_settings_once(ctx, client);
            call_log::resync_once(ctx, client);
            call_log::request_history_once(ctx, client);
            favorites::resync_once(ctx, client);
            channels::load(ctx, client);   // the rail's count of channels with new posts
            for group in ctx.db().nameless_groups() {
                name_group(ctx, client, group);
            }
            ctx.calls_dirty.notify_one();   // the call list's pictures are asked for now that there's a connection
            resync_stickers_once(ctx, client);
            refresh_blocklist(ctx, client);
            avatars::queue_stale(ctx);
        }
        Event::PairSuccess(_) => ctx.status("syncing", Some("Linked. Loading your chatsâ€¦".into())),
        Event::Disconnected(_) => ctx.status("connecting", None),
        Event::IncomingCall(call) => calls::signal(ctx, client, call).await,
        Event::MissedCall(missed) => calls::missed(ctx, client, &missed.from, &missed.call_id, missed.timestamp.timestamp()).await,
        Event::CallEndedElsewhere(ended) => calls::elsewhere(ctx, &ended.call_id),
        Event::LoggedOut(_) => forget_everything(ctx),
        // Something about your channels changed on another device (followed, left, muted): the lists are read again.
        Event::MexNotification(note) if note.op_name.contains("Newsletter") => {
            info!("channels: the phone says {}", note.op_name);
            channels::load(ctx, client);
        }
        Event::NewsletterLiveUpdate(update) => {
            let changes: Vec<(u64, Vec<(String, u64)>)> =
                update.messages.iter().map(|m| (m.server_id, m.reactions.iter().map(|r| (r.code.clone(), r.count)).collect())).collect();
            channels::counts(ctx, &update.newsletter_jid.to_string(), &changes);
        }
        Event::ChatPresence(update) => {
            use whatsapp_rust::wacore::types::presence::{ChatPresence, ChatPresenceMedia};
            // Your own typing (in "Message yourself", or echoed from your other devices) isn't shown.
            if update.source.is_from_me
                || ctx.db().is_me(&update.source.sender.to_non_ad_string())
                || ctx.db().is_me(&update.source.chat.to_non_ad_string())
            {
                return;
            }
            let (chat_id, who) = {
                let db = ctx.db();
                let chat_id = db.canonical(&update.source.chat.to_non_ad_string());
                let who = if update.source.is_group { db.person_name(&update.source.sender.to_non_ad_string(), "") } else { String::new() };
                (chat_id, who)
            };
            let state = match (&update.state, &update.media) {
                (ChatPresence::Composing, ChatPresenceMedia::Audio) => "recording",
                (ChatPresence::Composing, _) => "typing",
                _ => "paused",
            };
            ctx.send(Out::Typing { chat_id, who, state });
        }
        Event::Presence(update) => {
            let chat_id = ctx.db().canonical(&update.from.to_non_ad_string());
            ctx.send(Out::Presence { chat_id, online: !update.unavailable, last_seen: update.last_seen.map(|t| t.timestamp()) });
        }
        Event::PictureUpdate(update) => {
            let jid = update.jid.to_non_ad();
            let own = client.persistence_manager().get_device_snapshot();
            let is_self = [&own.pn, &own.lid].into_iter().flatten().any(|me| me.user == jid.user && me.server == jid.server);
            let chat_id = if is_self { avatars::SELF_ID.to_string() } else { ctx.db().canonical(&jid.to_string()) };
            let _ = ctx.avatars.send(avatars::Request { chat_id, force: true });
        }
        Event::HistorySync(lazy) => {
            let lazy = (**lazy).clone();
            let db = Arc::clone(&ctx.db);
            let progress = lazy.progress();
            let on_demand = lazy.sync_type() == wa::history_sync::HistorySyncType::ON_DEMAND as i32;
            let session = lazy.peer_data_request_session_id().map(str::to_string);
            if on_demand {
                info!("on-demand history answer, session {session:?}");
            }
            let kind = lazy.sync_type();
            let result = tokio::task::spawn_blocking(move || ingest_history(&lazy, &db)).await;
            match result {
                Ok(Ok(Ingested { chats, upgraded, calls })) => {
                    info!(
                        "history chunk: {} conversations, {} messages gained media details, {} calls listed ({} new) (progress {progress:?}, on-demand {on_demand}, kind {})",
                        chats.len(),
                        upgraded.len(),
                        calls.0,
                        calls.1,
                        kind
                    );
                    for (chat_id, message_id) in &upgraded {
                        send_message_update(ctx, chat_id, message_id);
                    }
                    if on_demand {
                        answer_pending_history(ctx, client, &chats, session.as_deref());
                    } else {
                        avatars::queue_stale(ctx);   // new chats from the sync
                        ctx.calls_dirty.notify_one();   // and its calls
                    }
                }
                Ok(Err(e)) => error!("history sync decode failed: {e}"),
                Err(e) => error!("history sync task failed: {e}"),
            }
            if let Some(p) = progress.filter(|p| *p < 100) {
                ctx.status("syncing", Some(format!("Loading your chatsâ€¦ {p}%")));
            }
            ctx.chats_dirty.notify_one();
        }
        // Chat settings from the phone (app-state sync): pins, archive, mute.
        Event::PinUpdate(update) => {
            let pinned = update.action.pinned.unwrap_or(false);
            let at = update.timestamp.timestamp();
            chat_setting(ctx, client, &update.jid, |db, id| db.set_pinned(id, if pinned { at } else { 0 })).await;
        }
        Event::ArchiveUpdate(update) => {
            let archived = update.action.archived.unwrap_or(false);
            chat_setting(ctx, client, &update.jid, |db, id| db.set_archived(id, archived)).await;
        }
        Event::MuteUpdate(update) => {
            let end = match (update.action.muted.unwrap_or(false), update.action.mute_end_timestamp) {
                (false, _) => 0,
                (true, Some(t)) if t > 0 => t / if t > 100_000_000_000 { 1000 } else { 1 },
                (true, _) => -1,
            };
            chat_setting(ctx, client, &update.jid, |db, id| db.set_mute_end(id, end)).await;
        }
        Event::StarUpdate(update) => {
            let id = chat_for(ctx, client, &update.chat_jid).await;
            let starred = update.action.starred.unwrap_or(false);
            if ctx.db().set_starred(&id, &update.message_id, starred) {
                send_message_update(ctx, &id, &update.message_id);
            }
        }
        Event::DeleteMessageForMeUpdate(update) => {
            let id = chat_for(ctx, client, &update.chat_jid).await;
            if ctx.db().delete_message(&id, &update.message_id) {
                ctx.send(Out::MessageRemoved { chat_id: id.clone(), message_id: update.message_id.clone() });
                send_chat(ctx, &id);
            }
        }
        Event::DeleteChatUpdate(update) => {
            let id = chat_for(ctx, client, &update.jid).await;
            ctx.db().delete_chat(&id);
            ctx.send(Out::ChatRemoved { chat_id: id });
        }
        Event::ClearChatUpdate(update) => {
            let id = chat_for(ctx, client, &update.jid).await;
            ctx.db().clear_messages(&id);
            ctx.send(Out::Messages { chat_id: id.clone(), messages: Vec::new() });
            send_chat(ctx, &id);
        }
        Event::MarkChatAsReadUpdate(update) => {
            let id = chat_for(ctx, client, &update.jid).await;
            if update.action.read.unwrap_or(true) { ctx.db().mark_read(&id) } else { mark_unread(ctx, &id) }
            send_chat(ctx, &id);
        }
        Event::GroupUpdate(update) => {
            use whatsapp_rust::wacore::stanza::groups::GroupNotificationAction;
            if let GroupNotificationAction::Subject { subject, .. } = &update.action
                && !subject.is_empty()
            {
                let chat_id = ctx.db().canonical(&update.group_jid.to_non_ad_string());
                ctx.db().set_chat_name(&chat_id, subject);
                send_chat(ctx, &chat_id);
            }
            if let GroupNotificationAction::Ephemeral { expiration, .. } = update.action {
                let chat_id = ctx.db().canonical(&update.group_jid.to_non_ad_string());
                let who = match &update.participant {
                    Some(p) if ctx.db().is_me(&p.to_non_ad_string()) => "You".to_string(),
                    Some(p) => ctx.db().person_name(&p.to_non_ad_string(), update.notify.as_deref().unwrap_or("")),
                    None => "Someone".to_string(),
                };
                ephemeral_changed(ctx, &chat_id, expiration, &who, update.timestamp.timestamp());
            }
        }
        Event::Receipt(receipt) => {
            use whatsapp_rust::wacore::types::presence::ReceiptType;
            let status = match receipt.r#type {
                ReceiptType::Delivered => 2,
                ReceiptType::Read | ReceiptType::Played => 3,
                _ => return,
            };
            let chat_id = ctx.db().canonical(&receipt.source.chat.to_non_ad_string());
            let mut recorded = false;
            let (changed, chat) = {
                let db = ctx.db();
                // Per person, for Message info (in 1:1 chats the sender is the chat).
                // Groups: the person who read it (the receipt's participant); 1:1: the chat.
                // A status update: the person who looked at it.
                let several = chat_id.ends_with("@g.us") || chat_id == status::CHAT;
                let who = if several { db.canonical(&receipt.source.sender.to_non_ad_string()) } else { chat_id.clone() };
                if who == chat_id && several {
                    // No participant: nothing to say who.
                } else {
                for id in &receipt.message_ids {
                    db.set_receipt(&chat_id, id, &who, status, receipt.timestamp.timestamp());
                }
                recorded = true;
                }
                let changed = db.upgrade_status(&chat_id, &receipt.message_ids, status);
                let chat = if changed.is_empty() { None } else { db.chat(&chat_id) };
                (changed, chat)
            };
            if recorded && chat_id == status::CHAT {
                ctx.statuses_dirty.notify_one();   // one more view
            }
            if recorded {
                ctx.send(Out::ReceiptsChanged { chat_id: chat_id.clone(), message_ids: receipt.message_ids.clone() });
            }
            if !changed.is_empty() {
                ctx.send(Out::Receipt { chat_id, message_ids: changed, status });
                if let Some(chat) = chat {
                    ctx.send(Out::Chat { chat });
                }
            }
        }
        Event::ContactUpdate(update) => {
            let name = update.action.full_name.as_deref().or(update.action.first_name.as_deref()).unwrap_or("");
            let db = ctx.db();
            db.set_full_name(&update.jid.to_non_ad_string(), name);
            for alt in [&update.action.pn_jid, &update.action.lid_jid].into_iter().flatten() {
                db.set_full_name(alt, name);
            }
            drop(db);
            ctx.chats_dirty.notify_one();
        }
        Event::PushNameUpdate(update) => {
            ctx.db().set_push_name(&update.jid.to_non_ad_string(), &update.new_push_name);
            ctx.chats_dirty.notify_one();
        }
        Event::ClientOutdated(_) => ctx.status("error", Some("WhatsApp says this client version is outdated.".into())),
        Event::TemporaryBan(ban) => ctx.status("error", Some(format!("Temporarily banned by WhatsApp: {ban:?}"))),
        Event::ConnectFailure(fail) => ctx.status("error", Some(format!("Connection failed: {fail:?}"))),
        Event::StreamReplaced(_) => ctx.status("error", Some("Logged in somewhere else with this session.".into())),
        _ => {}
    }
}

fn is_hidden_chat(jid: &str) -> bool {
    // Status updates and channels are not chats.
    jid.ends_with("@broadcast") || jid.ends_with("@newsletter")
}

fn on_message(ctx: &Ctx, message: &wa::Message, info: &MessageInfo) {
    let source = &info.source;
    let raw_chat = source.chat.to_non_ad_string();
    if channels::is_channel(&raw_chat) {
        channels::live(ctx, &raw_chat);
        return;
    }
    if raw_chat == status::CHAT {
        status::on_message(ctx, message, info);
        return;
    }
    if is_hidden_chat(&raw_chat) {
        return;
    }
    if let Some(seconds) = extract::ephemeral_setting(message) {
        let chat_id = ctx.db().canonical(&raw_chat);
        let who = if source.is_from_me { "You".to_string() } else { ctx.db().person_name(&source.sender.to_non_ad_string(), &info.push_name) };
        ephemeral_changed(ctx, &chat_id, seconds, &who, info.timestamp.timestamp());
        return;
    }
    if let Some(control) = extract::control(message) {
        let chat_id = ctx.db().canonical(&raw_chat);
        let who = if source.is_from_me { "You".to_string() } else { ctx.db().person_name(&source.sender.to_non_ad_string(), &info.push_name) };
        apply_control(ctx, &chat_id, control, &who);
        return;
    }
    if let Some((target, emoji)) = extract::reaction(message) {
        let reactor = if source.is_from_me { "me".to_string() } else { source.sender.to_non_ad_string() };
        let chat_id = ctx.db().canonical(&raw_chat);
        ctx.db().set_reaction(&chat_id, &target, &reactor, &emoji, info.timestamp.timestamp_millis());
        send_reactions(ctx, chat_id, target);
        return;
    }
    let Some(content) = extract::content(message) else { return };
    let quote = extract::quote(message);
    let forwarded = extract::forwarding_score(message);
    let expiration = extract::expiration(message);

    let (chat_id, stored, (media, thumb, extra)) = {
        let db = ctx.db();
        // A 1:1 chat can show up under its LID live while history stored it by
        // phone number (or the other way round); link the two.
        if !source.is_group {
            let alt = if source.is_from_me { &source.recipient_alt } else { &source.sender_alt };
            if let Some(alt) = alt {
                let alt = alt.to_non_ad_string();
                if db.canonical(&raw_chat) == raw_chat && db.chat(&raw_chat).is_none() && db.chat(&alt).is_some() {
                    db.add_alias(&raw_chat, &alt);
                }
            }
        }
        let chat_id = db.canonical(&raw_chat);
        let sender = if source.is_from_me { String::new() } else { source.sender.to_non_ad_string() };
        if !source.is_from_me {
            db.set_push_name(&sender, &info.push_name);
        }
        if db.ensure_chat(&chat_id, source.is_group) {
            let _ = ctx.avatars.send(avatars::Request { chat_id: chat_id.clone(), force: false });
        }
        let stored = StoredMessage {
            id: info.id.to_string(),
            from_me: source.is_from_me,
            sender,
            push_name: info.push_name.clone(),
            ts: info.timestamp.timestamp(),
            kind: content.kind.to_string(),
            text: content.text,
            file_name: content.file_name,
            status: if source.is_from_me { 1 } else { 0 },
        };
        (chat_id, stored, (content.media, content.thumb, content.extra))
    };

    let (dto, chat) = {
        let db = ctx.db();
        if !db.insert_message(&chat_id, &stored) {
            return; // duplicate delivery
        }
        if let Some(media) = &media {
            db.insert_media(&chat_id, &stored.id, media);
        }
        db.insert_extra(&chat_id, &stored.id, &thumb, extra.as_ref());
        if extra.as_ref().is_some_and(|e| e.get("call").is_some()) {
            ctx.calls_dirty.notify_one();   // a call the phone just logged in this chat
        }
        if stored.kind == "poll" {
            if let Some(secret) = extract::message_secret(message) {
                db.set_poll(&chat_id, &stored.id, &secret, &source.sender.to_non_ad_string());
            }
        }
        if let Some(quote) = &quote {
            db.insert_quote(&chat_id, &stored.id, quote);
        }
        db.set_forwarded(&chat_id, &stored.id, forwarded);
        // A timer on the message (its sender's setting), else the chat's.
        let timer = if expiration > 0 { expiration } else { db.ephemeral(&chat_id) };
        db.set_expiry(&chat_id, &stored.id, stored.ts, timer);
        if !stored.from_me {
            db.increment_unread(&chat_id);
        }
        let chat = db.chat(&chat_id);
        (db.to_dto(&chat_id, stored), chat)
    };

    ctx.send(Out::Message { chat_id, message: dto });
    if let Some(chat) = chat {
        ctx.send(Out::Chat { chat });
    }
}

/// Serves older messages from the store, or asks the phone for them when the
/// store has nothing older (the reply arrives later as an ON_DEMAND history sync).
async fn load_older(ctx: &Ctx, client: &Arc<Client>, chat_id: String, before_ts: i64, before_id: String, limit: u32) {
    let local = ctx.db().messages_before(&chat_id, before_ts, &before_id, limit);
    if !local.is_empty() {
        ctx.send(Out::OlderMessages { chat_id, messages: local, complete: false });
        return;
    }

    // The phone already said this is where the chat begins.
    let oldest = ctx.db().oldest(&chat_id);
    let start_known = ctx.db().flag(&start_flag(&chat_id, oldest.as_ref().map_or("", |o| o.0.as_str())));
    let (Some((oldest_id, from_me, ts)), Ok(jid), false) = (oldest, chat_id.parse::<Jid>(), start_known) else {
        ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: true });
        return;
    };
    // The chat begins with a notice written here (a group you just made): that's its start,
    // and the phone couldn't answer for a message it has never seen.
    if oldest_id.starts_with("notice-") {
        ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: true });
        return;
    }

    let pending = Pending { ts, id: oldest_id.clone(), request: None, other_tried: false };
    ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).insert(chat_id.clone(), pending);
    match client.fetch_message_history(&jid, &oldest_id, from_me, ts * 1000, 50).await {
        Ok(request) => {
            info!("older messages for {chat_id}: asked the phone (request {request}, before a message of {ts})");
            if let Some(p) = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).get_mut(&chat_id) {
                p.request = Some(request);
            }
            // A phone that never answers (offline, or it doesn't know the message) mustn't
            // leave "Loading older messages…" up for good: after 25 s the wait is over.
            let (ctx, waiting, asked_for) = (ctx.clone(), chat_id.clone(), oldest_id.clone());
            tokio::spawn(async move {
                tokio::time::sleep(Duration::from_secs(25)).await;
                let still = {
                    let mut pending = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner());
                    let same = pending.get(&waiting).is_some_and(|p| p.id == asked_for);
                    if same {
                        pending.remove(&waiting);
                    }
                    same
                };
                if still {
                    warn!("older messages for {waiting}: the phone didn't answer in 25 s");
                    ctx.send(Out::OlderMessages { chat_id: waiting, messages: Vec::new(), complete: false });
                }
            });
        }
        Err(e) => {
            warn!("on-demand history request failed for {chat_id}: {e}");
            ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).remove(&chat_id);
            // Not `complete`: the phone may just be offline; the UI can retry later.
            ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: false });
        }
    }
}

/// Set once the phone has nothing older than this message: the chat starts here.
/// (`history_start:` flags were set after asking under one address only; they're not read any more.)
fn start_flag(chat_id: &str, oldest_id: &str) -> String {
    format!("history_begins:{chat_id}:{oldest_id}")
}

/// The phone keeps a 1:1 chat under the person's number or their LID, and answers a request
/// made under the other with nothing. Before taking "nothing older" as the chat's start, the
/// same request goes out once more under the chat's other address. True when it was sent.
fn ask_under_other_address(ctx: &Ctx, client: &Arc<Client>, chat_id: &str, p: &Pending) -> bool {
    if p.other_tried {
        return false;
    }
    let (other, from_me) = {
        let db = ctx.db();
        (db.other_address(chat_id), db.message(chat_id, &p.id).map(|m| m.from_me))
    };
    let (Some(other), Some(from_me)) = (other, from_me) else { return false };
    let Ok(jid) = other.parse::<Jid>() else { return false };
    if let Some(waiting) = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).get_mut(chat_id) {
        waiting.other_tried = true;
        waiting.request = None;
    }
    let (ctx, client, chat_id, p) = (ctx.clone(), Arc::clone(client), chat_id.to_string(), p.clone());
    tokio::spawn(async move {
        match client.fetch_message_history(&jid, &p.id, from_me, p.ts * 1000, 50).await {
            Ok(request) => {
                info!("older messages for {chat_id}: nothing under that address; asked under its other one (request {request})");
                if let Some(waiting) = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).get_mut(&chat_id) {
                    waiting.request = Some(request);
                }
            }
            Err(e) => {
                warn!("on-demand history request (other address) failed for {chat_id}: {e}");
                ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).remove(&chat_id);
                ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: false });
            }
        }
    });
    true
}

/// An ON_DEMAND chunk arrived: hand each waiting chat whatever is now older than its anchor.
///
/// The answer to a "load older" request carries that request's id; nothing older in it
/// means the chat starts there (remembered, so it isn't asked again). A chunk without a
/// matching id may answer a media backfill for the same chat instead, so it only passes on
/// what it brought.
fn answer_pending_history(ctx: &Ctx, client: &Arc<Client>, chats_in_chunk: &HashMap<String, usize>, session: Option<&str>) {
    let waiting: Vec<(String, Pending, bool)> = {
        let pending = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner());
        pending
            .iter()
            .filter_map(|(chat, p)| {
                let answers = session.is_some() && p.request.as_deref() == session;
                (answers || chats_in_chunk.contains_key(chat)).then(|| (chat.clone(), p.clone(), answers))
            })
            .collect()
    };
    for (chat_id, p, answers) in waiting {
        let messages = ctx.db().messages_before(&chat_id, p.ts, &p.id, 200);
        let refilling = ctx.backfill_sent.lock().unwrap_or_else(|p| p.into_inner()).get(&chat_id).is_some_and(|t| t.elapsed() < Duration::from_secs(20));
        let complete = messages.is_empty() && (answers || chats_in_chunk.get(&chat_id) == Some(&0) || !refilling);
        info!(
            "older messages for {chat_id}: {} found ({:?} in the chunk), request {:?}, answers it: {answers}, refill in flight: {refilling} -> complete: {complete}",
            messages.len(),
            chats_in_chunk.get(&chat_id),
            p.request
        );
        if messages.is_empty() && !complete {
            continue;
        }
        if complete && answers && ask_under_other_address(ctx, client, &chat_id, &p) {
            continue;
        }
        if complete {
            ctx.db().set_flag(&start_flag(&chat_id, &p.id));
        }
        ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).remove(&chat_id);
        ctx.send(Out::OlderMessages { chat_id, messages, complete });
    }
}

struct Ingested {
    /// Chat id -> messages the chunk carried for it.
    chats: HashMap<String, usize>,
    /// Messages already stored without download details that now have them.
    upgraded: Vec<(String, String)>,
    /// Calls the chunk listed (the phone's call history), and how many of them were new here.
    calls: (usize, usize),
}

/// Decodes one history-sync chunk into the store.
fn ingest_history(
    lazy: &whatsapp_rust::wacore::types::events::LazyHistorySync,
    db: &Mutex<Store>,
) -> Result<Ingested, String> {
    let mut stream = lazy.stream();
    let mut store = db.lock().unwrap_or_else(|p| p.into_inner());
    store.batch(|s| {
        let mut chats = HashMap::new();
        let mut upgraded = Vec::new();
        while let Some(conv) = stream.next_conversation().map_err(|e| e.to_string())? {
            if let Some(chat_id) = ingest_conversation(s, &conv, &mut upgraded) {
                chats.insert(chat_id, conv.messages.len());
            }
        }
        let rest = stream.remainder().map_err(|e| e.to_string())?;
        let calls = (rest.call_log_records.len(), call_log::ingest(s, &rest.call_log_records));
        for p in &rest.pushnames {
            if let (Some(id), Some(name)) = (&p.id, &p.pushname) {
                s.set_push_name(id, name);
            }
        }
        for m in &rest.phone_number_to_lid_mappings {
            if let (Some(pn), Some(lid)) = (&m.pn_jid, &m.lid_jid) {
                // Point whichever form has no chat of its own at the one that does.
                if s.chat(lid).is_some() { s.add_alias(pn, lid) } else { s.add_alias(lid, pn) }
            }
        }
        Ok(Ingested { chats, upgraded, calls })
    })
}

fn ingest_conversation(s: &Store, conv: &wa::Conversation, upgraded: &mut Vec<(String, String)>) -> Option<String> {
    if is_hidden_chat(&conv.id) {
        return None;
    }
    let is_group = conv.id.ends_with("@g.us");
    let chat_id = s.canonical(&conv.id);
    for alt in [&conv.pn_jid, &conv.lid_jid].into_iter().flatten() {
        s.add_alias(alt, &chat_id);
    }

    let name = conv.name.as_deref().or(conv.display_name.as_deref()).unwrap_or("");
    let unread = conv.unread_count.unwrap_or(0);
    s.upsert_chat(&ChatMeta {
        id: &chat_id,
        name,
        is_group,
        unread,
        pinned: conv.pinned.unwrap_or(0) as i64,
        archived: conv.archived.unwrap_or(false),
        mute_end: conv.mute_end_time.map(|t| t as i64).unwrap_or(0),
    });
    if conv.marked_as_unread == Some(true) && unread == 0 {
        s.set_marked_unread(&chat_id, true);
    }
    if let Some(seconds) = conv.ephemeral_expiration {
        s.set_ephemeral(&chat_id, seconds);
    }

    for hm in &conv.messages {
        let Some(wmi) = hm.message.as_option() else { continue };
        let Some(key) = wmi.key.as_option() else { continue };
        for r in &wmi.reactions {
            let (Some(rkey), Some(id)) = (r.key.as_option(), key.id.as_deref()) else { continue };
            let reactor = match (rkey.from_me.unwrap_or(false), is_group) {
                (true, _) => "me".to_string(),
                (false, true) => rkey.participant.clone().unwrap_or_default(),
                (false, false) => chat_id.clone(),
            };
            s.set_reaction(&chat_id, id, &reactor, r.text.as_deref().unwrap_or(""), r.sender_timestamp_ms.unwrap_or(0));
        }
        let msg = wmi.message.as_option();
        // Pins from history: the pin event itself, or a pin message.
        if let Some(pin) = wmi.pin_in_chat.as_option()
            && let Some(target) = pin.key.as_option().and_then(|k| k.id.clone())
        {
            use wa::pin_in_chat::Type as PinType;
            let duration = pin.message_add_on_context_info.as_option().and_then(|c| c.message_add_on_duration_in_secs).unwrap_or(0);
            match pin.r#type {
                Some(PinType::PIN_FOR_ALL) => apply_pin(s, &chat_id, &target, true, duration, pin.sender_timestamp_ms.unwrap_or(0)),
                Some(PinType::UNPIN_FOR_ALL) => apply_pin(s, &chat_id, &target, false, 0, 0),
                _ => {}
            }
        }
        if let Some(extract::Control::Pin(target, pinned, duration, at_ms)) = msg.and_then(extract::control) {
            apply_pin(s, &chat_id, &target, pinned, duration, at_ms);
        }
        let from_me = key.from_me.unwrap_or(false);
        // Who got and read your older messages, and when (Message info).
        if from_me && let Some(id) = key.id.as_deref() {
            for r in &wmi.user_receipt {
                let who = if is_group { s.canonical(&r.user_jid) } else { chat_id.clone() };
                if let Some(ts) = r.receipt_timestamp.filter(|t| *t > 0) {
                    s.set_receipt(&chat_id, id, &who, 2, ts);
                }
                if let Some(ts) = r.read_timestamp.or(r.played_timestamp).filter(|t| *t > 0) {
                    s.set_receipt(&chat_id, id, &who, 3, ts);
                }
            }
        }
        let sender = match (from_me, is_group) {
            (true, _) => String::new(),
            (false, true) => key.participant.clone().or_else(|| wmi.participant.clone()).unwrap_or_default(),
            (false, false) => chat_id.clone(),
        };
        let Some(content) = msg.and_then(extract::content).or_else(|| system_notice(s, wmi, from_me, &sender)) else { continue };
        let push_name = wmi.push_name.clone().unwrap_or_default();
        if !from_me && !sender.is_empty() {
            s.set_push_name(&sender, &push_name);
        }
        let id = key.id.clone().unwrap_or_default();
        let new_media = content.media.as_ref().is_some_and(|media| s.insert_media(&chat_id, &id, media));
        let new_extra = s.insert_extra(&chat_id, &id, &content.thumb, content.extra.as_ref());
        let mut new_votes = false;
        if content.kind == "poll" {
            if let Some(secret) = msg.and_then(extract::message_secret) {
                let creator = if from_me { "me".to_string() } else { sender.clone() };
                s.set_poll(&chat_id, &id, &secret, &creator);
            }
            // History hands over the votes already decrypted.
            let options = s.poll_options(&chat_id, &id);
            for update in &wmi.poll_updates {
                let (Some(vkey), Some(vote)) = (update.poll_update_message_key.as_option(), update.vote.as_option()) else { continue };
                let voter = match (vkey.from_me.unwrap_or(false), is_group) {
                    (true, _) => "me".to_string(),
                    (false, true) => vkey.participant.clone().unwrap_or_default(),
                    (false, false) => chat_id.clone(),
                };
                let chosen = option_names(&options, &vote.selected_options);
                new_votes |= s.set_vote(&chat_id, &id, &voter, &chosen, update.sender_timestamp_ms.unwrap_or(0));
            }
        }
        if let Some(quote) = msg.and_then(extract::quote) {
            s.insert_quote(&chat_id, &id, &quote);
        }
        let forwarded = msg.map_or(0, extract::forwarding_score);
        // Disappearing: when it was sent and for how long (from the phone), else its own timer.
        let (eph_start, eph_secs) = match (wmi.ephemeral_start_timestamp, wmi.ephemeral_duration) {
            (Some(start), Some(secs)) if secs > 0 => (start as i64, secs),
            _ => (wmi.message_timestamp.unwrap_or(0) as i64, msg.map_or(0, extract::expiration)),
        };
        let inserted = s.insert_message(
            &chat_id,
            &StoredMessage {
                id: id.clone(),
                from_me,
                sender,
                push_name,
                ts: wmi.message_timestamp.unwrap_or(0) as i64,
                kind: content.kind.to_string(),
                text: content.text,
                file_name: content.file_name,
                status: if from_me { delivery(wmi) } else { 0 },
            },
        );
        s.set_forwarded(&chat_id, &id, forwarded);
        s.set_expiry(&chat_id, &id, eph_start, eph_secs);
        if (new_media || new_extra || new_votes) && !inserted {
            upgraded.push((chat_id.clone(), id));
        }
    }
    Some(chat_id)
}

/// History carries WhatsApp's own notices as stubs: missed calls, group changes...
fn system_notice(s: &Store, wmi: &wa::WebMessageInfo, from_me: bool, actor: &str) -> Option<extract::Content> {
    use wa::web_message_info::StubType as T;
    let name = |jid: &str| if s.is_me(jid) { "You".to_string() } else { s.person_name(jid, "") };
    let params = &wmi.message_stub_parameters;
    let who = if from_me || actor.is_empty() { "You".to_string() } else { name(actor) };
    let names = params.iter().map(|p| name(p)).collect::<Vec<_>>().join(", ");
    let first = params.first().cloned().unwrap_or_default();
    let text = match wmi.message_stub_type? {
        T::CALL_MISSED_VOICE | T::CALL_MISSED_GROUP_VOICE => "ðŸ“ž Missed voice call".to_string(),
        T::CALL_MISSED_VIDEO | T::CALL_MISSED_GROUP_VIDEO => "ðŸ“¹ Missed video call".to_string(),
        T::GROUP_CREATE => format!("{who} created group \"{first}\""),
        T::GROUP_CHANGE_SUBJECT => format!("{who} changed the group name to \"{first}\""),
        T::GROUP_CHANGE_ICON => format!("{who} changed this group's icon"),
        T::GROUP_CHANGE_DESCRIPTION => format!("{who} changed the group description"),
        T::GROUP_PARTICIPANT_ADD => format!("{who} added {names}"),
        T::GROUP_PARTICIPANT_REMOVE => format!("{who} removed {names}"),
        T::GROUP_PARTICIPANT_LEAVE => format!("{names} left"),
        T::GROUP_PARTICIPANT_PROMOTE => format!("{who} made {names} an admin"),
        T::GROUP_PARTICIPANT_INVITE | T::GROUP_PARTICIPANT_LINKED_GROUP_JOIN | T::GROUP_PARTICIPANT_ADD_REQUEST_JOIN => {
            format!("{names} joined using this group's invite link")
        }
        T::CHANGE_EPHEMERAL_SETTING => match first.parse::<u32>() {
            Ok(seconds) => extract::ephemeral_notice(&who, seconds, 0),
            Err(_) => format!("{who} changed the disappearing messages setting"),
        },
        T::BLOCK_CONTACT => if first == "true" { "You blocked this contact" } else { "You unblocked this contact" }.to_string(),
        _ => return None,
    };
    Some(extract::system(text))
}

/// WebMessageInfo.Status -> 1 sent, 2 delivered, 3 read.
fn delivery(wmi: &wa::WebMessageInfo) -> u8 {
    use wa::web_message_info::Status;
    match wmi.status {
        Some(Status::READ | Status::PLAYED) => 3,
        Some(Status::DELIVERY_ACK) => 2,
        _ => 1,
    }
}

// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Polls â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// Option hashes (what a vote carries) back to option names.
fn option_names(options: &[String], hashes: &[Vec<u8>]) -> Vec<String> {
    options
        .iter()
        .filter(|o| hashes.iter().any(|h| h.as_slice() == whatsapp_rust::wacore::poll::compute_option_hash(o).as_slice()))
        .cloned()
        .collect()
}

/// "me" (a poll you made on your phone, from history) in the chat's address family.
fn poll_creator(client: &Client, chat_id: &str, creator: &str) -> Option<Jid> {
    if creator == "me" {
        let own = if chat_id.ends_with("@lid") { client.lid().or_else(|| client.pn()) } else { client.pn() };
        return own.map(|j| j.to_non_ad());
    }
    creator.parse().ok()
}

/// A vote arrived (someone's, or yours from the phone): decrypt it with the poll's secret.
async fn poll_vote(ctx: &Ctx, client: &Arc<Client>, info: &MessageInfo, vote: extract::PollVote) {
    let chat_id = ctx.db().canonical(&info.source.chat.to_non_ad_string());
    let Some((secret, creator, options)) = poll_meta(ctx, &chat_id, &vote.poll_id) else {
        warn!("vote for unknown poll {chat_id}/{}", vote.poll_id);
        return;
    };
    let late = whatsapp_rust::wacore::time::now_millis() - if vote.ts > 0 { vote.ts } else { info.timestamp.timestamp_millis() };
    info!("vote on {chat_id}/{} from {} (from me: {}), arrived {late} ms after it was cast", vote.poll_id, info.source.sender, info.source.is_from_me);
    let Some(creator) = poll_creator(client, &chat_id, &creator) else { return };
    let voter = info.source.sender.to_non_ad();
    let ciphertext = whatsapp_rust::features::PollVoteCiphertext { enc_payload: &vote.payload, enc_iv: &vote.iv };
    match client.polls().decrypt_vote(ciphertext, &secret, &vote.poll_id, &creator, &voter).await {
        Ok(hashes) => {
            let who = if info.source.is_from_me { "me".to_string() } else { voter.to_string() };
            let ts = if vote.ts > 0 { vote.ts } else { info.timestamp.timestamp_millis() };
            if ctx.db().set_vote(&chat_id, &vote.poll_id, &who, &option_names(&options, &hashes), ts) {
                send_message_update(ctx, &chat_id, &vote.poll_id);
            }
        }
        Err(e) => warn!("couldn't decrypt a vote on {chat_id}/{}: {e}", vote.poll_id),
    }
}

/// Your vote: shown right away, then sent (encrypted with the poll's secret).
async fn vote_poll(ctx: &Ctx, client: &Arc<Client>, chat_id: String, poll_id: String, options: Vec<String>) {
    info!("voting {options:?} on {chat_id}/{poll_id}");
    let Some((secret, creator, _)) = poll_meta(ctx, &chat_id, &poll_id) else {
        warn!("no key for poll {chat_id}/{poll_id}");
        ctx.send(Out::Notice { ok: false, text: "This poll can't be voted on from this PC: its key never reached it.".into() });
        return;
    };
    let Some(creator) = poll_creator(client, &chat_id, &creator) else { return };
    if ctx.db().set_vote(&chat_id, &poll_id, "me", &options, whatsapp_rust::wacore::time::now_millis()) {
        send_message_update(ctx, &chat_id, &poll_id);
    }
    match send_vote(ctx, client, &chat_id, &poll_id, &creator, &secret, &options).await {
        Ok(()) => info!("vote on {chat_id}/{poll_id} sent"),
        Err(e) => {
            warn!("vote on {chat_id}/{poll_id} failed: {e}");
            ctx.send(Out::Notice { ok: false, text: "Couldn't send your vote.".into() });
        }
    }
}

/// A poll's key, creator and options: ours, or (polls from before WAFluent kept keys) the
/// key the WhatsApp library stored from history, remembered for next time.
fn poll_meta(ctx: &Ctx, chat_id: &str, poll_id: &str) -> Option<(Vec<u8>, String, Vec<String>)> {
    if let Some(meta) = ctx.db().poll(chat_id, poll_id) {
        // History stored your own polls as "me"; the library knows which address made them.
        if meta.1 == "me" {
            if let Some((_, sender)) = library_secret(&ctx.data_dir, poll_id) {
                return Some((meta.0, sender, meta.2));
            }
        }
        return Some(meta);
    }
    let (secret, sender) = library_secret(&ctx.data_dir, poll_id)?;
    let db = ctx.db();
    db.set_poll(chat_id, poll_id, &secret, &sender);
    Some((secret, sender, db.poll_options(chat_id, poll_id)))
}

/// (secret, sender) from whatsapp-rust's own message-secret table.
fn library_secret(data_dir: &std::path::Path, msg_id: &str) -> Option<(Vec<u8>, String)> {
    let db = rusqlite::Connection::open_with_flags(data_dir.join("whatsapp.db"), rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY).ok()?;
    db.query_row("SELECT secret, sender FROM msg_secrets WHERE msg_id = ?1 AND length(secret) = 32 LIMIT 1", [msg_id], |r| {
        Ok((r.get::<_, Vec<u8>>(0)?, r.get::<_, String>(1)?))
    })
    .ok()
    .map(|(secret, sender)| (secret, sender.split(':').next().unwrap_or(&sender).to_string()))
}

/// The vote message, like WhatsApp builds it. Not `client.polls().vote()`: for polls you
/// made under your LID it compares your phone number with the LID, marks the poll as
/// someone else's (`from_me: false`), and your phone can't match the vote to the poll.
async fn send_vote(
    ctx: &Ctx,
    client: &Arc<Client>,
    chat_id: &str,
    poll_id: &str,
    creator: &Jid,
    secret: &[u8],
    options: &[String],
) -> Result<(), String> {
    use whatsapp_rust::wacore::poll;
    let (pn, lid) = (client.pn().map(|j| j.to_non_ad()), client.lid().map(|j| j.to_non_ad()));
    let mine = |j: &Jid| [&pn, &lid].into_iter().flatten().any(|m| m.user == j.user);
    let from_me = mine(creator);
    // Vote under the same address family the poll was made in.
    let voter = if creator.server == Server::Lid { lid.clone().or(pn.clone()) } else { pn.clone().or(lid.clone()) }.ok_or("not logged in")?;

    // 1:1: the chat in the poll's address family (their LID for a LID poll).
    let group = chat_id.ends_with("@g.us");
    let chat_jid: Jid = if group {
        chat_id.parse().map_err(|e| format!("{e}"))?
    } else if !from_me {
        creator.clone()
    } else {
        let family = if creator.server == Server::Lid { "@lid" } else { "@s.whatsapp.net" };
        let jids = ctx.db().chat_jids(chat_id);
        jids.iter().find(|j| j.ends_with(family)).unwrap_or(&jids[0]).parse().map_err(|e| format!("{e}"))?
    };

    let hashes: Vec<Vec<u8>> = options.iter().map(|o| poll::compute_option_hash(o).to_vec()).collect();
    let (payload, iv) = poll::encrypt_poll_vote_with_secret(&hashes, secret, poll_id, &creator.to_non_ad_string(), &voter.to_string())
        .map_err(|e| e.to_string())?;
    let update = wa::message::PollUpdateMessage {
        poll_creation_message_key: whatsapp_rust::buffa::MessageField::some(wa::MessageKey {
            remote_jid: Some(chat_jid.to_string()),
            from_me: Some(from_me),
            id: Some(poll_id.to_string()),
            participant: group.then(|| creator.to_string()),
        }),
        vote: whatsapp_rust::buffa::MessageField::some(wa::message::PollEncValue { enc_payload: Some(payload), enc_iv: Some(iv.to_vec()) }),
        metadata: whatsapp_rust::buffa::MessageField::none(),
        sender_timestamp_ms: Some(whatsapp_rust::wacore::time::now_millis()),
    };
    let message = wa::Message { poll_update_message: whatsapp_rust::buffa::MessageField::some(update), ..Default::default() };
    info!("vote key: chat {chat_jid}, creator {creator}, voter {voter}, from me {from_me}");
    client.send_message(chat_jid, message).await.map(|_| ()).map_err(|e| e.to_string())
}

// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ New chats â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

/// Opens the chat with a phone number, creating it if you've never messaged them.
async fn open_number(ctx: &Ctx, client: &Arc<Client>, phone: &str) {
    if let Some(chat_id) = resolve_number(ctx, client, phone).await {
        ctx.send(Out::Opened { chat_id });
    }
}

/// The 1:1 chat for a phone number, made if the number is on WhatsApp and there isn't one
/// yet. None (after a notice) when it isn't a number, isn't on WhatsApp, or can't be checked.
async fn resolve_number(ctx: &Ctx, client: &Arc<Client>, phone: &str) -> Option<String> {
    let digits: String = phone.chars().filter(char::is_ascii_digit).collect();
    if digits.len() < 6 {
        ctx.send(Out::Notice { ok: false, text: format!("{phone} isn't a phone number.") });
        return None;
    }
    let pn = format!("{digits}@s.whatsapp.net");
    let existing = ctx.db().canonical(&pn);
    if ctx.db().chat(&existing).is_some() {
        // A chat with no messages isn't in the app's list yet: it gets it before it's told to open it.
        send_chat(ctx, &existing);
        return Some(existing);
    }
    let jid = pn.parse::<Jid>().ok()?;
    let found = match client.contacts().is_on_whatsapp(std::slice::from_ref(&jid)).await {
        Ok(results) => results.into_iter().find(|r| r.is_registered),
        Err(e) => {
            warn!("is_on_whatsapp {digits} failed: {e}");
            ctx.send(Out::Notice { ok: false, text: "Couldn't check that number. Try again in a moment.".into() });
            return None;
        }
    };
    let Some(found) = found else {
        ctx.send(Out::Notice { ok: false, text: format!("{phone} isn't on WhatsApp.") });
        return None;
    };
    let chat_id = {
        let db = ctx.db();
        // Known by their LID already (a group, say)? Use that chat.
        let lid_chat = found.lid.as_ref().map(|l| db.canonical(&l.to_non_ad_string())).filter(|id| db.chat(id).is_some());
        let chat_id = lid_chat.unwrap_or(pn.clone());
        if let Some(lid) = &found.lid {
            db.add_alias(&lid.to_non_ad_string(), &chat_id);
        }
        db.ensure_chat(&chat_id, false);
        chat_id
    };
    let _ = ctx.avatars.send(avatars::Request { chat_id: chat_id.clone(), force: false });
    send_chat(ctx, &chat_id);
    Some(chat_id)
}
