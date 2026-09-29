//! wafluent-core: the WhatsApp connection behind WAFluent.
//!
//! Started by the WinUI app with redirected stdio. Events go out on stdout and
//! commands come in on stdin, one JSON object per line (see `protocol.rs`).
//! Logs go to stderr. Closing stdin shuts the connection down cleanly.
#![recursion_limit = "512"]

mod actions;
mod avatars;
mod media;
mod extract;
mod protocol;
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
}

/// Everything the event handlers share.
#[derive(Clone)]
pub(crate) struct Ctx {
    tx: Tx,
    pub(crate) db: Db,
    pub(crate) data_dir: PathBuf,
    /// Pinged whenever the chat list changed in bulk; a debounced task sends one snapshot.
    chats_dirty: Arc<Notify>,
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
        env_logger::Env::default().default_filter_or("info,whatsapp_rust=warn,wacore=warn"),
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
    };
    ctx.status("starting", None);

    // Show what we already have while connecting.
    let cached = ctx.db().chats();
    if !cached.is_empty() {
        ctx.send(Out::Chats { chats: cached });
    }
    if let Some((_, path, _)) = ctx.db().avatar(avatars::SELF_ID).filter(|(_, p, _)| !p.is_empty()) {
        ctx.send(Out::Avatar { chat_id: avatars::SELF_ID.into(), path: Some(path) });
    }

    spawn_snapshot_debouncer(ctx.clone());

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

async fn on_command(ctx: &Ctx, client: &Arc<Client>, cmd: Command) {
    match cmd {
        Command::LoadMessages { chat_id, limit } => {
            let messages = ctx.db().messages(&chat_id, limit.unwrap_or(300));
            ctx.send(Out::Messages { chat_id, messages });
        }
        Command::LoadOlder { chat_id, before_ts, before_id, limit } => {
            load_older(ctx, client, chat_id, before_ts, before_id, limit.unwrap_or(100)).await;
        }
        Command::VotePoll { chat_id, message_id, options } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { vote_poll(&ctx, &client, chat_id, message_id, options).await });
        }
        Command::OpenNumber { phone } => open_number(ctx, client, &phone).await,
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
        Command::SendText { chat_id, text, reply_to, temp_id } => {
            // Sending waits on the server; keep reading commands meanwhile.
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { send_text(&ctx, &client, chat_id, text, reply_to, temp_id).await });
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
            tokio::spawn(async move { actions::forward(&ctx, &client, chat_id, message_id, to).await });
        }
        Command::PinMessage { chat_id, message_id, pin } => {
            let (ctx, client) = (ctx.clone(), Arc::clone(client));
            tokio::spawn(async move { actions::pin_message(&ctx, &client, chat_id, message_id, pin).await });
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

async fn send_text(ctx: &Ctx, client: &Arc<Client>, chat_id: String, text: String, reply_to: Option<String>, temp_id: String) {
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

    let sent = match client.send_message(jid, message).await {
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
fn apply_control(ctx: &Ctx, chat_id: &str, control: extract::Control) {
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
        extract::Control::Pin(id, pinned) => {
            ctx.db().set_pinned_message(chat_id, if pinned { &id } else { "" });
            send_chat(ctx, chat_id);
        }
    }
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

/// "Mark as unread": WhatsApp shows a dot; one unread is the closest we have.
pub(crate) fn mark_unread(ctx: &Ctx, chat_id: &str) {
    let db = ctx.db();
    let unread = db.chat(chat_id).map(|c| c.unread).unwrap_or(0).max(1);
    db.set_unread(chat_id, unread);
}

/// Who you blocked, from the server (the phone manages the list too).
fn refresh_blocklist(ctx: &Ctx, client: &Arc<Client>) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let Ok(entries) = client.blocking().get_blocklist().await else { return };
        let mut ids = Vec::new();
        for entry in entries {
            ids.push(chat_for(&ctx, &client, &entry.jid).await);
        }
        ctx.db().set_blocklist(&ids);
        ctx.chats_dirty.notify_one();
    });
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
            ctx.chats_dirty.notify_one();
            resync_chat_settings_once(ctx, client);
            refresh_blocklist(ctx, client);
            avatars::queue_stale(ctx);
        }
        Event::PairSuccess(_) => ctx.status("syncing", Some("Linked. Loading your chats…".into())),
        Event::Disconnected(_) => ctx.status("connecting", None),
        Event::LoggedOut(_) => forget_everything(ctx),
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
            let result = tokio::task::spawn_blocking(move || ingest_history(&lazy, &db)).await;
            match result {
                Ok(Ok(Ingested { chats, upgraded })) => {
                    info!(
                        "history chunk: {} conversations, {} messages gained media details (progress {progress:?}, on-demand {on_demand})",
                        chats.len(),
                        upgraded.len()
                    );
                    for (chat_id, message_id) in &upgraded {
                        send_message_update(ctx, chat_id, message_id);
                    }
                    if on_demand {
                        answer_pending_history(ctx, &chats, session.as_deref());
                    } else {
                        avatars::queue_stale(ctx);   // new chats from the sync
                    }
                }
                Ok(Err(e)) => error!("history sync decode failed: {e}"),
                Err(e) => error!("history sync task failed: {e}"),
            }
            if let Some(p) = progress.filter(|p| *p < 100) {
                ctx.status("syncing", Some(format!("Loading your chats… {p}%")));
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
        Event::Receipt(receipt) => {
            use whatsapp_rust::wacore::types::presence::ReceiptType;
            let status = match receipt.r#type {
                ReceiptType::Delivered => 2,
                ReceiptType::Read | ReceiptType::Played => 3,
                _ => return,
            };
            let chat_id = ctx.db().canonical(&receipt.source.chat.to_non_ad_string());
            let (changed, chat) = {
                let db = ctx.db();
                let changed = db.upgrade_status(&chat_id, &receipt.message_ids, status);
                let chat = if changed.is_empty() { None } else { db.chat(&chat_id) };
                (changed, chat)
            };
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
    if is_hidden_chat(&raw_chat) {
        return;
    }
    if let Some(control) = extract::control(message) {
        let chat_id = ctx.db().canonical(&raw_chat);
        apply_control(ctx, &chat_id, control);
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
        if stored.kind == "poll" {
            if let Some(secret) = extract::message_secret(message) {
                db.set_poll(&chat_id, &stored.id, &secret, &source.sender.to_non_ad_string());
            }
        }
        if let Some(quote) = &quote {
            db.insert_quote(&chat_id, &stored.id, quote);
        }
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

    let pending = Pending { ts, id: oldest_id.clone(), request: None };
    ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).insert(chat_id.clone(), pending);
    match client.fetch_message_history(&jid, &oldest_id, from_me, ts * 1000, 50).await {
        Ok(request) => {
            if let Some(p) = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).get_mut(&chat_id) {
                p.request = Some(request);
            }
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
fn start_flag(chat_id: &str, oldest_id: &str) -> String {
    format!("history_start:{chat_id}:{oldest_id}")
}

/// An ON_DEMAND chunk arrived: hand each waiting chat whatever is now older than its anchor.
///
/// The answer to a "load older" request carries that request's id; nothing older in it
/// means the chat starts there (remembered, so it isn't asked again). A chunk without a
/// matching id may answer a media backfill for the same chat instead, so it only passes on
/// what it brought.
fn answer_pending_history(ctx: &Ctx, chats_in_chunk: &HashMap<String, usize>, session: Option<&str>) {
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
            "older messages for {chat_id}: {} found, request {:?}, answers it: {answers}, refill in flight: {refilling} -> complete: {complete}",
            messages.len(),
            p.request
        );
        if messages.is_empty() && !complete {
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
        Ok(Ingested { chats, upgraded })
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
    let unread = conv.unread_count.unwrap_or(0) + u32::from(conv.marked_as_unread == Some(true) && conv.unread_count.unwrap_or(0) == 0);
    s.upsert_chat(&ChatMeta {
        id: &chat_id,
        name,
        is_group,
        unread,
        pinned: conv.pinned.unwrap_or(0) as i64,
        archived: conv.archived.unwrap_or(false),
        mute_end: conv.mute_end_time.map(|t| t as i64).unwrap_or(0),
    });

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
        let from_me = key.from_me.unwrap_or(false);
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
        T::CALL_MISSED_VOICE | T::CALL_MISSED_GROUP_VOICE => "📞 Missed voice call".to_string(),
        T::CALL_MISSED_VIDEO | T::CALL_MISSED_GROUP_VIDEO => "📹 Missed video call".to_string(),
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
        T::CHANGE_EPHEMERAL_SETTING => format!("{who} changed the disappearing messages setting"),
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

// ───────────── Polls ─────────────

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
    let Some((secret, creator, options)) = ctx.db().poll(&chat_id, &vote.poll_id) else {
        warn!("vote for unknown poll {chat_id}/{}", vote.poll_id);
        return;
    };
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
    let Some((secret, creator, _)) = ctx.db().poll(&chat_id, &poll_id) else {
        ctx.send(Out::Notice { ok: false, text: "This poll can't be voted on from this PC (it arrived before WAFluent kept poll keys).".into() });
        return;
    };
    let (Some(creator), Ok(jid)) = (poll_creator(client, &chat_id, &creator), chat_id.parse::<Jid>()) else { return };
    let previous = ctx.db().set_vote(&chat_id, &poll_id, "me", &options, whatsapp_rust::wacore::time::now_millis());
    if previous {
        send_message_update(ctx, &chat_id, &poll_id);
    }
    if let Err(e) = client.polls().vote(jid, &poll_id, &creator, &secret, &options).await {
        warn!("vote on {chat_id}/{poll_id} failed: {e}");
        ctx.send(Out::Notice { ok: false, text: "Couldn't send your vote.".into() });
    }
}

// ───────────── New chats ─────────────

/// Opens the chat with a phone number, creating it if you've never messaged them.
async fn open_number(ctx: &Ctx, client: &Arc<Client>, phone: &str) {
    let digits: String = phone.chars().filter(char::is_ascii_digit).collect();
    if digits.len() < 6 {
        ctx.send(Out::Notice { ok: false, text: format!("{phone} isn't a phone number.") });
        return;
    }
    let pn = format!("{digits}@s.whatsapp.net");
    let existing = ctx.db().canonical(&pn);
    if ctx.db().chat(&existing).is_some() {
        ctx.send(Out::Opened { chat_id: existing });
        return;
    }
    let Ok(jid) = pn.parse::<Jid>() else { return };
    let found = match client.contacts().is_on_whatsapp(std::slice::from_ref(&jid)).await {
        Ok(results) => results.into_iter().find(|r| r.is_registered),
        Err(e) => {
            warn!("is_on_whatsapp {digits} failed: {e}");
            ctx.send(Out::Notice { ok: false, text: "Couldn't check that number. Try again in a moment.".into() });
            return;
        }
    };
    let Some(found) = found else {
        ctx.send(Out::Notice { ok: false, text: format!("{phone} isn't on WhatsApp.") });
        return;
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
    ctx.send(Out::Opened { chat_id });
}
