//! wafluent-core: the WhatsApp connection behind WAFluent.
//!
//! Started by the WinUI app with redirected stdio. Events go out on stdout and
//! commands come in on stdin, one JSON object per line (see `protocol.rs`).
//! Logs go to stderr. Closing stdin shuts the connection down cleanly.
#![recursion_limit = "512"]

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

/// Everything the event handlers share.
#[derive(Clone)]
pub(crate) struct Ctx {
    tx: Tx,
    db: Db,
    pub(crate) data_dir: PathBuf,
    /// Pinged whenever the chat list changed in bulk; a debounced task sends one snapshot.
    chats_dirty: Arc<Notify>,
    /// Chats waiting for an on-demand history reply from the phone -> anchor (ts, message id).
    pending_history: Arc<Mutex<HashMap<String, (i64, String)>>>,
    /// Profile-picture fetch queue (see avatars.rs).
    pub(crate) avatars: mpsc::UnboundedSender<avatars::Request>,
    /// Attachment download queue (see media.rs).
    media: mpsc::UnboundedSender<media::Request>,
    /// Chats re-requested from the phone to fill in missing media details.
    backfill: Arc<Mutex<HashSet<String>>>,
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
                async move { on_message(&ctx, &m.message, &m.info) }
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
        Command::DownloadMedia { chat_id, message_id } => {
            let _ = ctx.media.send(media::Request { chat_id, message_id });
        }
        Command::BackfillMedia { chat_id } => {
            if !ctx.backfill.lock().unwrap_or_else(|p| p.into_inner()).insert(chat_id.clone()) {
                return; // already asked this session
            }
            let newest = ctx.db().newest(&chat_id);
            let (Some((id, from_me, ts)), Ok(jid)) = (newest, chat_id.parse::<Jid>()) else { return };
            if let Err(e) = client.fetch_message_history(&jid, &id, from_me, ts * 1000, 50).await {
                warn!("media backfill request failed for {chat_id}: {e}");
                ctx.backfill.lock().unwrap_or_else(|p| p.into_inner()).remove(&chat_id);
            }
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
            ctx.status("connected", None);
            ctx.chats_dirty.notify_one();
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
            let result = tokio::task::spawn_blocking(move || ingest_history(&lazy, &db)).await;
            match result {
                Ok(Ok(chats)) => {
                    info!("history chunk: {} conversations (progress {progress:?}, on-demand {on_demand})", chats.len());
                    if on_demand {
                        answer_pending_history(ctx, &chats);
                        answer_backfill(ctx, &chats);
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
    let Some(content) = extract::content(message) else { return };

    let (chat_id, stored, media) = {
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
        (chat_id, stored, content.media)
    };

    let (dto, chat) = {
        let db = ctx.db();
        if !db.insert_message(&chat_id, &stored) {
            return; // duplicate delivery
        }
        if let Some(media) = &media {
            db.insert_media(&chat_id, &stored.id, media);
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

    let oldest = ctx.db().oldest(&chat_id);
    let (Some((oldest_id, from_me, ts)), Ok(jid)) = (oldest, chat_id.parse::<Jid>()) else {
        ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: true });
        return;
    };

    ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).insert(chat_id.clone(), (ts, oldest_id.clone()));
    if let Err(e) = client.fetch_message_history(&jid, &oldest_id, from_me, ts * 1000, 50).await {
        warn!("on-demand history request failed for {chat_id}: {e}");
        ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner()).remove(&chat_id);
        // Not `complete`: the phone may just be offline; the UI can retry later.
        ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: false });
    }
}

/// An ON_DEMAND chunk arrived: hand each waiting chat whatever is now older than its anchor.
fn answer_pending_history(ctx: &Ctx, chats_in_chunk: &HashSet<String>) {
    let answered: Vec<(String, (i64, String))> = {
        let mut pending = ctx.pending_history.lock().unwrap_or_else(|p| p.into_inner());
        let ids: Vec<String> = pending.keys().filter(|id| chats_in_chunk.contains(*id)).cloned().collect();
        ids.into_iter().filter_map(|id| pending.remove(&id).map(|anchor| (id, anchor))).collect()
    };
    for (chat_id, (ts, id)) in answered {
        let messages = ctx.db().messages_before(&chat_id, ts, &id, 200);
        let complete = messages.is_empty();
        ctx.send(Out::OlderMessages { chat_id, messages, complete });
    }
}

/// Chats whose media details were just refilled get their message list resent.
fn answer_backfill(ctx: &Ctx, chats_in_chunk: &HashSet<String>) {
    let done: Vec<String> = {
        let backfill = ctx.backfill.lock().unwrap_or_else(|p| p.into_inner());
        backfill.iter().filter(|id| chats_in_chunk.contains(*id)).cloned().collect()
    };
    for chat_id in done {
        let messages = ctx.db().messages(&chat_id, 300);
        ctx.send(Out::Messages { chat_id, messages });
    }
}

/// Decodes one history-sync chunk into the store. Returns the chat ids it covered.
fn ingest_history(
    lazy: &whatsapp_rust::wacore::types::events::LazyHistorySync,
    db: &Mutex<Store>,
) -> Result<HashSet<String>, String> {
    let mut stream = lazy.stream();
    let mut store = db.lock().unwrap_or_else(|p| p.into_inner());
    store.batch(|s| {
        let mut chats = HashSet::new();
        while let Some(conv) = stream.next_conversation().map_err(|e| e.to_string())? {
            if let Some(chat_id) = ingest_conversation(s, &conv) {
                chats.insert(chat_id);
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
        Ok(chats)
    })
}

fn ingest_conversation(s: &Store, conv: &wa::Conversation) -> Option<String> {
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
        pinned: conv.pinned.unwrap_or(0) > 0,
        archived: conv.archived.unwrap_or(false),
        mute_end: conv.mute_end_time.map(|t| t as i64).unwrap_or(0),
    });

    for hm in &conv.messages {
        let Some(wmi) = hm.message.as_option() else { continue };
        let Some(key) = wmi.key.as_option() else { continue };
        let Some(msg) = wmi.message.as_option() else { continue };
        let Some(content) = extract::content(msg) else { continue };
        let from_me = key.from_me.unwrap_or(false);
        let sender = match (from_me, is_group) {
            (true, _) => String::new(),
            (false, true) => key.participant.clone().or_else(|| wmi.participant.clone()).unwrap_or_default(),
            (false, false) => chat_id.clone(),
        };
        let push_name = wmi.push_name.clone().unwrap_or_default();
        if !from_me && !sender.is_empty() {
            s.set_push_name(&sender, &push_name);
        }
        let id = key.id.clone().unwrap_or_default();
        if let Some(media) = &content.media {
            s.insert_media(&chat_id, &id, media);
        }
        s.insert_message(
            &chat_id,
            &StoredMessage {
                id,
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
    }
    Some(chat_id)
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
