//! Local chat/message store. WhatsApp only sends history once (right after
//! linking), so everything shown in the UI is persisted here.

use rusqlite::{Connection, OptionalExtension, params};

use crate::extract::{Media, Quote};
use crate::protocol::{CallLogDto, ChatDto, ContactDto, MediaDto, MessageDto, PhoneDto, PinnedDto, ReplyDto, StarredDto};

pub struct Store {
    db: Connection,
    /// Your own JIDs (phone number and LID), to tell your messages apart in quotes.
    me: Vec<String>,
}

/// A message as stored; names are resolved when it is read back.
pub struct StoredMessage {
    pub id: String,
    pub from_me: bool,
    pub sender: String,
    pub push_name: String,
    pub ts: i64,
    pub kind: String,
    pub text: String,
    pub file_name: String,
    pub status: u8,
}

/// Pseudo chat id the favourite stickers' files are stored under.
pub const FAVORITES: &str = "favorites";

/// What a history entry made from a chat's call message is called: this, the chat, `|`, the message.
const CHAT_CALL: &str = "msg:";

/// A call as read from the tables: id, time, seconds, incoming, video, result, group, peers.
type CallRow = (String, i64, i64, bool, bool, String, String, String);

/// One call in the history. `peers`: everyone else in it (JIDs); `result`: connected |
/// missed | rejected | cancelled | elsewhere | failed.
pub struct CallEntry {
    pub id: String,
    pub ts: i64,
    pub duration: i64,
    pub incoming: bool,
    pub video: bool,
    pub result: String,
    pub group_jid: String,
    pub peers: Vec<String>,
}

pub struct ChatMeta<'a> {
    pub id: &'a str,
    pub name: &'a str,
    pub is_group: bool,
    pub unread: u32,
    /// Pin time (Unix seconds), 0 when not pinned.
    pub pinned: i64,
    pub archived: bool,
    pub mute_end: i64,
}

const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS chats(
    id       TEXT PRIMARY KEY,
    name     TEXT NOT NULL DEFAULT '',
    is_group INTEGER NOT NULL,
    unread   INTEGER NOT NULL DEFAULT 0,
    pinned   INTEGER NOT NULL DEFAULT 0,
    archived INTEGER NOT NULL DEFAULT 0,
    mute_end INTEGER NOT NULL DEFAULT 0,
    last_ts  INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS messages(
    chat_id   TEXT NOT NULL,
    id        TEXT NOT NULL,
    from_me   INTEGER NOT NULL,
    sender    TEXT NOT NULL,
    push_name TEXT NOT NULL DEFAULT '',
    ts        INTEGER NOT NULL,
    kind      TEXT NOT NULL,
    text      TEXT NOT NULL,
    file_name TEXT NOT NULL DEFAULT '',
    status    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(chat_id, id)
);
CREATE INDEX IF NOT EXISTS messages_by_time ON messages(chat_id, ts);
-- push_name: what the person calls themselves; full_name: your address-book name.
CREATE TABLE IF NOT EXISTS names(
    jid       TEXT PRIMARY KEY,
    push_name TEXT NOT NULL DEFAULT '',
    full_name TEXT NOT NULL DEFAULT ''
);
-- Profile pictures cached on disk. path = '' means the chat has no (visible) picture.
CREATE TABLE IF NOT EXISTS avatars(
    jid        TEXT PRIMARY KEY,
    picture_id TEXT NOT NULL DEFAULT '',
    path       TEXT NOT NULL DEFAULT '',
    checked_at INTEGER NOT NULL DEFAULT 0
);
-- Attachments: the CDN reference needed to download later, plus the local file once fetched.
CREATE TABLE IF NOT EXISTS media(
    chat_id         TEXT NOT NULL,
    message_id      TEXT NOT NULL,
    media_type      TEXT NOT NULL,
    direct_path     TEXT NOT NULL,
    media_key       BLOB NOT NULL,
    file_sha256     BLOB NOT NULL,
    file_enc_sha256 BLOB NOT NULL,
    file_length     INTEGER NOT NULL,
    mimetype        TEXT NOT NULL DEFAULT '',
    width           INTEGER NOT NULL DEFAULT 0,
    height          INTEGER NOT NULL DEFAULT 0,
    seconds         INTEGER NOT NULL DEFAULT 0,
    waveform        BLOB,
    path            TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(chat_id, message_id)
);
-- A chat can be addressed by phone-number JID or by LID; both map to one chat id.
CREATE TABLE IF NOT EXISTS aliases(
    alt     TEXT PRIMARY KEY,
    chat_id TEXT NOT NULL
);
-- Reactions: one per person per message. reactor = 'me' for yours.
CREATE TABLE IF NOT EXISTS reactions(
    chat_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    reactor    TEXT NOT NULL,
    emoji      TEXT NOT NULL,
    ts         INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(chat_id, message_id, reactor)
);
-- Phone numbers behind LIDs, for showing +44 20 7946 0123 (display only).
CREATE TABLE IF NOT EXISTS numbers(
    lid    TEXT PRIMARY KEY,
    number TEXT NOT NULL
);
-- Replies: the message each one quotes, as the quote itself described it.
CREATE TABLE IF NOT EXISTS quotes(
    chat_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    quoted_id  TEXT NOT NULL,
    sender     TEXT NOT NULL DEFAULT '',
    kind       TEXT NOT NULL DEFAULT '',
    text       TEXT NOT NULL DEFAULT '',
    file_name  TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(chat_id, message_id)
);

-- Polls: the secret their votes are encrypted with, and who created them (me = you).
CREATE TABLE IF NOT EXISTS polls(
    chat_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    secret     BLOB NOT NULL,
    creator    TEXT NOT NULL,
    PRIMARY KEY(chat_id, message_id)
);

-- Each voter current choice (JSON array of option names; me = you). Newer votes replace older.
CREATE TABLE IF NOT EXISTS poll_votes(
    chat_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    voter      TEXT NOT NULL,
    options    TEXT NOT NULL,
    ts         INTEGER NOT NULL,
    PRIMARY KEY(chat_id, message_id, voter)
);

-- Who got / read your messages (Message info): one row per person, the furthest status.
CREATE TABLE IF NOT EXISTS receipts(
    chat_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    user       TEXT NOT NULL,
    status     INTEGER NOT NULL,
    ts         INTEGER NOT NULL,
    PRIMARY KEY(chat_id, message_id, user)
);
-- Favourite stickers, synced from the phone. Their files live in media under
-- chat_id FAVORITES, message_id = key.
CREATE TABLE IF NOT EXISTS favorite_stickers(
    key TEXT PRIMARY KEY,
    ts  INTEGER NOT NULL
);
-- Per-kind details (JSON: map pin, contact numbers, poll options, link card...) and the
-- sender's JPEG preview.
CREATE TABLE IF NOT EXISTS extras(
    chat_id    TEXT NOT NULL,
    message_id TEXT NOT NULL,
    thumb      BLOB,
    data       TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(chat_id, message_id)
);
";

const CHAT_SELECT: &str = "
SELECT c.id, c.name, c.is_group, c.unread, c.pinned, c.archived, c.mute_end, c.last_ts,
       m.kind, m.text, m.file_name, m.from_me, m.status, m.sender, m.push_name,
       a.path, c.blocked, '' AS pinned_msg, c.ephemeral, c.marked_unread
FROM chats c
LEFT JOIN messages m ON m.rowid = (
    SELECT rowid FROM messages WHERE chat_id = c.id ORDER BY ts DESC, rowid DESC LIMIT 1)
LEFT JOIN avatars a ON a.jid = c.id
";

impl Store {
    pub fn open(path: &std::path::Path) -> rusqlite::Result<Self> {
        let db = Connection::open(path)?;
        db.pragma_update(None, "journal_mode", "WAL")?;
        db.pragma_update(None, "synchronous", "NORMAL")?;
        db.execute_batch(SCHEMA)?;
        // Columns added later; "duplicate column" once they exist is fine.
        for sql in [
            "ALTER TABLE chats ADD COLUMN blocked INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE chats ADD COLUMN pinned_msg TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE messages ADD COLUMN starred INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE messages ADD COLUMN edited INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE receipts ADD COLUMN delivered_ts INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE messages ADD COLUMN forwarded INTEGER NOT NULL DEFAULT 0",
            // Disappearing messages: the chat's timer (seconds, 0 off) and when each message goes.
            "ALTER TABLE chats ADD COLUMN ephemeral INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE messages ADD COLUMN expires_at INTEGER NOT NULL DEFAULT 0",
            // "Mark as unread" (a dot, no count), apart from real unread messages.
            "ALTER TABLE chats ADD COLUMN marked_unread INTEGER NOT NULL DEFAULT 0",
            // Everyone you've blocked, whether or not there's a chat with them (New chat marks them).
            "CREATE TABLE IF NOT EXISTS blocklist(id TEXT PRIMARY KEY)",
            "INSERT OR IGNORE INTO blocklist SELECT id FROM chats WHERE blocked = 1",
            // Pinned messages: up to three per chat, each until it runs out (WhatsApp's 2024 pins).
            "CREATE TABLE IF NOT EXISTS pins(chat_id TEXT NOT NULL, message_id TEXT NOT NULL, pinned_at INTEGER NOT NULL,
                                             expires_at INTEGER NOT NULL, PRIMARY KEY(chat_id, message_id))",
            "INSERT OR IGNORE INTO pins SELECT id, pinned_msg, CAST(strftime('%s','now') AS INTEGER),
                                               CAST(strftime('%s','now') AS INTEGER) + 604800 FROM chats WHERE pinned_msg != ''",
            "UPDATE chats SET pinned_msg = '' WHERE pinned_msg != ''",
            // The call history: from the phone (history sync, app state) and calls made here.
            // `peers`: everyone else in the call, comma-separated JIDs.
            "CREATE TABLE IF NOT EXISTS call_log(id TEXT PRIMARY KEY, ts INTEGER NOT NULL, duration INTEGER NOT NULL DEFAULT 0,
                                                  incoming INTEGER NOT NULL, video INTEGER NOT NULL, result TEXT NOT NULL,
                                                  group_jid TEXT NOT NULL DEFAULT '', peers TEXT NOT NULL DEFAULT '')",
            "CREATE INDEX IF NOT EXISTS call_log_ts ON call_log(ts DESC)",
            // Favourite chats, as the phone lists them (its ids, in its order).
            "CREATE TABLE IF NOT EXISTS favorite_chats(jid TEXT PRIMARY KEY, pos INTEGER NOT NULL)",
            // Calls taken out of the Calls page that live on as messages in a chat.
            "CREATE TABLE IF NOT EXISTS call_log_hidden(id TEXT PRIMARY KEY)",
            // Status updates you've looked at (status.rs).
            "CREATE TABLE IF NOT EXISTS status_seen(id TEXT PRIMARY KEY)",
            // The channels you follow (channels.rs): each as its JSON, and when you last opened it.
            "CREATE TABLE IF NOT EXISTS channels(id TEXT PRIMARY KEY, data TEXT NOT NULL, seen_ts INTEGER NOT NULL DEFAULT 0, pos INTEGER NOT NULL DEFAULT 0)",
            // Group receipts once saved under the group instead of the person.
            "DELETE FROM receipts WHERE chat_id LIKE '%@g.us' AND user = chat_id",
        ] {
            let _ = db.execute(sql, []);
        }
        let store = Self { db, me: Vec::new() };
        store.merge_split_chats();
        Ok(store)
    }

    /// Runs `f` inside one transaction (history chunks insert thousands of rows).
    pub fn batch<T>(&mut self, f: impl FnOnce(&Self) -> T) -> T {
        let _ = self.db.execute_batch("BEGIN");
        let out = f(self);
        let _ = self.db.execute_batch("COMMIT");
        out
    }

    // ───────────── Writes ─────────────

    pub fn add_alias(&self, alt: &str, chat_id: &str) {
        if alt.is_empty() || alt == chat_id {
            return;
        }
        let _ = self.db.execute(
            "INSERT INTO aliases(alt, chat_id) VALUES(?1, ?2) ON CONFLICT(alt) DO UPDATE SET chat_id = excluded.chat_id",
            params![alt, chat_id],
        );
        // The same person under both their number and their LID: one chat, not two
        // half-chats that each miss the other's messages.
        if self.chat_exists(alt) {
            self.merge_chat(alt, chat_id);
        }
    }

    fn chat_exists(&self, id: &str) -> bool {
        self.db.query_row("SELECT 1 FROM chats WHERE id = ?1", [id], |_| Ok(())).is_ok()
    }

    /// Moves everything stored under `from` into `into` and drops `from`.
    fn merge_chat(&self, from: &str, into: &str) {
        if from == into || !self.chat_exists(into) {
            return;
        }
        for table in ["media", "quotes", "reactions", "extras", "polls", "poll_votes"] {
            let _ = self.db.execute(&format!("UPDATE OR IGNORE {table} SET chat_id = ?2 WHERE chat_id = ?1"), [from, into]);
            let _ = self.db.execute(&format!("DELETE FROM {table} WHERE chat_id = ?1"), [from]);
        }
        let _ = self.db.execute("UPDATE OR IGNORE messages SET chat_id = ?2 WHERE chat_id = ?1", [from, into]);
        let _ = self.db.execute("DELETE FROM messages WHERE chat_id = ?1", [from]);
        let _ = self.db.execute(
            "UPDATE chats SET
                last_ts = MAX(chats.last_ts, f.last_ts),
                unread  = MAX(chats.unread, f.unread),
                pinned  = MAX(chats.pinned, f.pinned),
                name    = CASE WHEN chats.name = '' THEN f.name ELSE chats.name END
             FROM (SELECT last_ts, unread, pinned, name FROM chats WHERE id = ?1) AS f
             WHERE chats.id = ?2",
            [from, into],
        );
        let _ = self.db.execute("DELETE FROM chats WHERE id = ?1", [from]);
        let _ = self.db.execute("UPDATE aliases SET chat_id = ?2 WHERE chat_id = ?1", [from, into]);
        let _ = self.db.execute("DELETE FROM aliases WHERE alt = chat_id", []);
    }

    /// Chats split before merging existed (an alias whose other half still has a chat row).
    fn merge_split_chats(&self) {
        let pairs: Vec<(String, String)> = self
            .db
            .prepare("SELECT a.alt, a.chat_id FROM aliases a JOIN chats c ON c.id = a.alt")
            .and_then(|mut stmt| stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?)))?.collect())
            .unwrap_or_default();
        for (from, into) in pairs {
            self.merge_chat(&from, &into);
        }
    }

    /// A chat's id followed by the other ids it is known by (number / LID).
    pub fn chat_jids(&self, chat_id: &str) -> Vec<String> {
        let mut jids = vec![chat_id.to_string()];
        if let Ok(mut stmt) = self.db.prepare("SELECT alt FROM aliases WHERE chat_id = ?1") {
            if let Ok(rows) = stmt.query_map([chat_id], |r| r.get::<_, String>(0)) {
                jids.extend(rows.flatten());
            }
        }
        jids
    }

    /// The chat id a JID belongs to (follows phone-number/LID aliases).
    pub fn canonical(&self, jid: &str) -> String {
        self.db
            .query_row("SELECT chat_id FROM aliases WHERE alt = ?1", [jid], |r| r.get(0))
            .optional()
            .ok()
            .flatten()
            .unwrap_or_else(|| jid.to_string())
    }

    /// A 1:1 chat's other address (its LID when it's kept by number, or the reverse), when known.
    pub fn other_address(&self, chat_id: &str) -> Option<String> {
        self.db
            .query_row(
                "SELECT alt FROM aliases WHERE chat_id = ?1
                   AND substr(alt, instr(alt, '@')) != substr(?1, instr(?1, '@'))
                   AND (alt LIKE '%@lid' OR alt LIKE '%@s.whatsapp.net') LIMIT 1",
                [chat_id],
                |r| r.get(0),
            )
            .optional()
            .ok()
            .flatten()
    }

    pub fn upsert_chat(&self, meta: &ChatMeta) {
        let _ = self.db.execute(
            "INSERT INTO chats(id, name, is_group, unread, pinned, archived, mute_end)
             VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7)
             ON CONFLICT(id) DO UPDATE SET
                name     = CASE WHEN excluded.name != '' THEN excluded.name ELSE chats.name END,
                unread   = excluded.unread,
                pinned   = CASE WHEN excluded.pinned > 0 THEN excluded.pinned ELSE chats.pinned END,
                archived = MAX(chats.archived, excluded.archived),
                mute_end = CASE WHEN excluded.mute_end != 0 THEN excluded.mute_end ELSE chats.mute_end END",
            params![meta.id, meta.name, meta.is_group, meta.unread, meta.pinned, meta.archived, meta.mute_end],
        );
    }

    pub fn set_chat_name(&self, chat_id: &str, name: &str) {
        let _ = self.db.execute("UPDATE chats SET name = ?2 WHERE id = ?1", params![chat_id, name]);
    }

    /// Groups stored without a name: ones first seen through a message, not the phone's history.
    pub fn nameless_groups(&self) -> Vec<String> {
        let Ok(mut q) = self.db.prepare("SELECT id FROM chats WHERE id LIKE '%@g.us' AND (name IS NULL OR name = '')") else { return Vec::new() };
        q.query_map([], |r| r.get(0)).map(|rows| rows.flatten().collect()).unwrap_or_default()
    }

    pub fn group_is_nameless(&self, chat_id: &str) -> bool {
        chat_id.ends_with("@g.us")
            && self
                .db
                .query_row("SELECT name IS NULL OR name = '' FROM chats WHERE id = ?1", [chat_id], |r| r.get(0))
                .unwrap_or(false)
    }

    /// Returns true if the chat is new.
    pub fn ensure_chat(&self, id: &str, is_group: bool) -> bool {
        self.db
            .execute("INSERT OR IGNORE INTO chats(id, is_group) VALUES(?1, ?2)", params![id, is_group])
            .unwrap_or(0)
            > 0
    }

    /// Returns true if the message was new.
    pub fn insert_message(&self, chat_id: &str, m: &StoredMessage) -> bool {
        let inserted = self
            .db
            .execute(
                "INSERT OR IGNORE INTO messages(chat_id, id, from_me, sender, push_name, ts, kind, text, file_name, status)
                 VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
                params![chat_id, m.id, m.from_me, m.sender, m.push_name, m.ts, m.kind, m.text, m.file_name, m.status],
            )
            .unwrap_or(0)
            > 0;
        if inserted {
            let _ = self.db.execute(
                "UPDATE chats SET last_ts = MAX(last_ts, ?2) WHERE id = ?1",
                params![chat_id, m.ts],
            );
        }
        inserted
    }

    pub fn set_me(&mut self, jids: Vec<String>) {
        self.me = jids;
    }

    pub fn is_me(&self, jid: &str) -> bool {
        let user = jid.split(['@', ':']).next().unwrap_or("");
        self.me.iter().any(|me| me.split(['@', ':']).next() == Some(user))
    }

    pub fn insert_quote(&self, chat_id: &str, message_id: &str, q: &Quote) {
        let _ = self.db.execute(
            "INSERT OR IGNORE INTO quotes(chat_id, message_id, quoted_id, sender, kind, text, file_name)
             VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![chat_id, message_id, q.id, q.sender, q.kind, q.text, q.file_name],
        );
    }

    /// Records (or with an empty emoji, removes) one person's reaction. Older updates lose.
    pub fn set_reaction(&self, chat_id: &str, message_id: &str, reactor: &str, emoji: &str, ts: i64) {
        let _ = if emoji.is_empty() {
            self.db.execute(
                "DELETE FROM reactions WHERE chat_id = ?1 AND message_id = ?2 AND reactor = ?3 AND ts <= ?4",
                params![chat_id, message_id, reactor, ts],
            )
        } else {
            self.db.execute(
                "INSERT INTO reactions(chat_id, message_id, reactor, emoji, ts) VALUES(?1, ?2, ?3, ?4, ?5)
                 ON CONFLICT(chat_id, message_id, reactor) DO UPDATE SET emoji = excluded.emoji, ts = excluded.ts
                 WHERE excluded.ts >= reactions.ts",
                params![chat_id, message_id, reactor, emoji, ts],
            )
        };
    }

    /// (every reaction oldest first, yours).
    pub fn reactions(&self, chat_id: &str, message_id: &str) -> (Vec<String>, Option<String>) {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT reactor, emoji FROM reactions WHERE chat_id = ?1 AND message_id = ?2 ORDER BY ts, rowid",
        ) else {
            return (Vec::new(), None);
        };
        let rows: Vec<(String, String)> = stmt
            .query_map([chat_id, message_id], |r| Ok((r.get(0)?, r.get(1)?)))
            .map(|rows| rows.flatten().collect())
            .unwrap_or_default();
        let mine = rows.iter().find(|(who, _)| who == "me").map(|(_, e)| e.clone());
        (rows.into_iter().map(|(_, e)| e).collect(), mine)
    }

    /// Receipts only move forward (sent -> delivered -> read). Returns the ids that changed.
    pub fn upgrade_status(&self, chat_id: &str, ids: &[String], status: u8) -> Vec<String> {
        ids.iter()
            .filter(|id| {
                self.db
                    .execute(
                        "UPDATE messages SET status = ?3 WHERE chat_id = ?1 AND id = ?2 AND from_me = 1 AND status < ?3",
                        params![chat_id, id, status],
                    )
                    .unwrap_or(0)
                    > 0
            })
            .cloned()
            .collect()
    }

    /// App-state (phone) chat settings. `pinned_at` 0 = unpinned.
    pub fn set_pinned(&self, chat_id: &str, pinned_at: i64) {
        let _ = self.db.execute("UPDATE chats SET pinned = ?2 WHERE id = ?1", params![chat_id, pinned_at]);
    }

    pub fn set_archived(&self, chat_id: &str, archived: bool) {
        let _ = self.db.execute("UPDATE chats SET archived = ?2 WHERE id = ?1", params![chat_id, archived]);
    }

    pub fn set_mute_end(&self, chat_id: &str, mute_end: i64) {
        let _ = self.db.execute("UPDATE chats SET mute_end = ?2 WHERE id = ?1", params![chat_id, mute_end]);
    }

    /// 1:1 chats kept under a LID with no phone-number alias yet.
    pub fn lid_chats_without_number(&self) -> Vec<String> {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT id FROM chats WHERE id LIKE '%@lid'
               AND NOT EXISTS (SELECT 1 FROM aliases WHERE chat_id = chats.id AND alt LIKE '%@s.whatsapp.net')
               AND NOT EXISTS (SELECT 1 FROM aliases WHERE alt = chats.id AND chat_id LIKE '%@s.whatsapp.net')
               AND NOT EXISTS (SELECT 1 FROM numbers WHERE lid = chats.id)",
        ) else {
            return Vec::new();
        };
        stmt.query_map([], |r| r.get(0)).map(|rows| rows.flatten().collect()).unwrap_or_default()
    }

    /// Saved contacts known by phone number (their JIDs), for looking up their LIDs.
    pub fn contact_number_jids(&self) -> Vec<String> {
        self.db
            .prepare("SELECT jid FROM names WHERE full_name != '' AND jid LIKE '%@s.whatsapp.net'")
            .and_then(|mut s| s.query_map([], |r| r.get(0)).map(|r| r.flatten().collect()))
            .unwrap_or_default()
    }

    pub fn set_number(&self, lid: &str, number: &str) {
        let _ = self.db.execute(
            "INSERT INTO numbers(lid, number) VALUES(?1, ?2) ON CONFLICT(lid) DO UPDATE SET number = excluded.number",
            params![lid, number],
        );
    }

    // ───────────── Chat / message actions ─────────────

    pub fn set_blocked(&self, chat_id: &str, blocked: bool) {
        let _ = self.db.execute("UPDATE chats SET blocked = ?2 WHERE id = ?1", params![chat_id, blocked]);
        // Kept apart from the chats too: a blocked contact may have no chat.
        let _ = if blocked {
            self.db.execute("INSERT OR IGNORE INTO blocklist(id) VALUES(?1)", [chat_id])
        } else {
            self.db.execute("DELETE FROM blocklist WHERE id = ?1", [chat_id])
        };
    }

    /// The server's blocklist replaces ours.
    pub fn set_blocklist(&self, chat_ids: &[String]) {
        let _ = self.db.execute("UPDATE chats SET blocked = 0", []);
        let _ = self.db.execute("DELETE FROM blocklist", []);
        for id in chat_ids {
            self.set_blocked(id, true);
        }
    }

    /// Blocked people, as chat ids and as phone numbers (a block may be listed under the
    /// LID while the contact is known by number, or the other way round).
    fn blocked_keys(&self) -> std::collections::HashSet<String> {
        let ids: Vec<String> = self
            .db
            .prepare("SELECT id FROM blocklist")
            .and_then(|mut s| s.query_map([], |r| r.get(0)).map(|r| r.flatten().collect()))
            .unwrap_or_default();
        let mut keys = std::collections::HashSet::new();
        for id in ids {
            if let Some(number) = self.phone_number(&id) {
                keys.insert(number);
            }
            keys.insert(self.canonical(&id));
            keys.insert(id);
        }
        keys
    }

    /// Pins a message until `expires_at`; a chat keeps its four newest pins, like WhatsApp.
    pub fn add_pin(&self, chat_id: &str, message_id: &str, pinned_at: i64, expires_at: i64) {
        let _ = self.db.execute(
            "INSERT OR REPLACE INTO pins(chat_id, message_id, pinned_at, expires_at) VALUES(?1, ?2, ?3, ?4)",
            params![chat_id, message_id, pinned_at, expires_at],
        );
        let _ = self.db.execute(
            "DELETE FROM pins WHERE chat_id = ?1 AND message_id NOT IN
                 (SELECT message_id FROM pins WHERE chat_id = ?1 ORDER BY pinned_at DESC LIMIT 4)",
            [chat_id],
        );
    }

    pub fn remove_pin(&self, chat_id: &str, message_id: &str) {
        let _ = self.db.execute("DELETE FROM pins WHERE chat_id = ?1 AND message_id = ?2", [chat_id, message_id]);
    }

    /// A chat's pins, newest first, with what each message says.
    pub fn pins(&self, chat_id: &str) -> Vec<PinnedDto> {
        let Ok(mut stmt) = self
            .db
            .prepare("SELECT message_id, expires_at FROM pins WHERE chat_id = ?1 AND expires_at > ?2 ORDER BY pinned_at DESC LIMIT 4")
        else {
            return Vec::new();
        };
        let rows: Vec<(String, i64)> =
            stmt.query_map(params![chat_id, unix_now()], |r| Ok((r.get(0)?, r.get(1)?))).map(|r| r.flatten().collect()).unwrap_or_default();
        rows.into_iter()
            .map(|(id, expires_at)| {
                let m = self.message(chat_id, &id);
                PinnedDto {
                    preview: m.as_ref().map(|m| preview(&m.kind, &plain_mentions(&self.render_mentions(&m.text)), &m.file_name)).unwrap_or_else(|| "Message".into()),
                    ts: m.map(|m| m.ts).unwrap_or(0),
                    id,
                    expires_at,
                }
            })
            .collect()
    }

    /// Pins that ran out are removed; returns the chats that changed.
    pub fn expire_pins(&self, now: i64) -> Vec<String> {
        let chats: Vec<String> = self
            .db
            .prepare("SELECT DISTINCT chat_id FROM pins WHERE expires_at <= ?1")
            .and_then(|mut s| s.query_map([now], |r| r.get(0)).map(|r| r.flatten().collect()))
            .unwrap_or_default();
        if !chats.is_empty() {
            let _ = self.db.execute("DELETE FROM pins WHERE expires_at <= ?1", [now]);
        }
        chats
    }

    pub fn set_starred(&self, chat_id: &str, message_id: &str, starred: bool) -> bool {
        self.db
            .execute("UPDATE messages SET starred = ?3 WHERE chat_id = ?1 AND id = ?2", params![chat_id, message_id, starred])
            .unwrap_or(0)
            > 0
    }

    /// "Delete for everyone": the bubble stays as "This message was deleted".
    pub fn set_deleted(&self, chat_id: &str, message_id: &str) -> bool {
        let _ = self.db.execute("DELETE FROM media WHERE chat_id = ?1 AND message_id = ?2", [chat_id, message_id]);
        let _ = self.db.execute("DELETE FROM quotes WHERE chat_id = ?1 AND message_id = ?2", [chat_id, message_id]);
        let _ = self.db.execute("DELETE FROM extras WHERE chat_id = ?1 AND message_id = ?2", [chat_id, message_id]);
        self.db
            .execute(
                "UPDATE messages SET kind = 'deleted', text = '', file_name = '' WHERE chat_id = ?1 AND id = ?2",
                [chat_id, message_id],
            )
            .unwrap_or(0)
            > 0
    }

    /// How many times the message had been forwarded (from its context info).
    pub fn set_forwarded(&self, chat_id: &str, message_id: &str, score: u32) {
        if score > 0 {
            let _ = self.db.execute(
                "UPDATE messages SET forwarded = ?3 WHERE chat_id = ?1 AND id = ?2",
                params![chat_id, message_id, score],
            );
        }
    }

    pub fn forwarded(&self, chat_id: &str, message_id: &str) -> u32 {
        self.db
            .query_row("SELECT forwarded FROM messages WHERE chat_id = ?1 AND id = ?2", [chat_id, message_id], |r| r.get(0))
            .unwrap_or(0)
    }

    pub fn set_edited(&self, chat_id: &str, message_id: &str, text: &str) -> bool {
        self.db
            .execute(
                "UPDATE messages SET text = ?3, edited = 1 WHERE chat_id = ?1 AND id = ?2 AND kind != 'deleted'",
                params![chat_id, message_id, text],
            )
            .unwrap_or(0)
            > 0
    }

    /// "Delete for me": gone from this device.
    pub fn delete_message(&self, chat_id: &str, message_id: &str) -> bool {
        for table in ["media", "quotes", "reactions", "extras", "polls", "poll_votes", "pins"] {
            let _ = self.db.execute(&format!("DELETE FROM {table} WHERE chat_id = ?1 AND message_id = ?2"), [chat_id, message_id]);
        }
        self.db.execute("DELETE FROM messages WHERE chat_id = ?1 AND id = ?2", [chat_id, message_id]).unwrap_or(0) > 0
    }

    /// Empties a chat but keeps it in the list.
    pub fn clear_messages(&self, chat_id: &str) {
        for table in ["media", "quotes", "reactions", "extras", "polls", "poll_votes", "pins"] {
            let _ = self.db.execute(&format!("DELETE FROM {table} WHERE chat_id = ?1"), [chat_id]);
        }
        let _ = self.db.execute("DELETE FROM messages WHERE chat_id = ?1", [chat_id]);
        let _ = self.db.execute("UPDATE chats SET pinned_msg = '', unread = 0 WHERE id = ?1", [chat_id]);
    }

    pub fn delete_chat(&self, chat_id: &str) {
        self.clear_messages(chat_id);
        let _ = self.db.execute("DELETE FROM chats WHERE id = ?1", [chat_id]);
    }

    /// Starred messages across all chats, newest first.
    /// One chat's messages matching `condition` (SQL over the messages table), newest first.
    fn chat_messages_where(&self, chat_id: &str, condition: &str, limit: u32) -> Vec<MessageDto> {
        let sql = format!(
            "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status
             FROM messages WHERE chat_id = ?1 AND ({condition}) ORDER BY ts DESC LIMIT {limit}"
        );
        let Ok(mut stmt) = self.db.prepare(&sql) else {
            return Vec::new();
        };
        let rows: Vec<StoredMessage> = stmt
            .query_map([chat_id], |r| {
                Ok(StoredMessage {
                    id: r.get(0)?,
                    from_me: r.get(1)?,
                    sender: r.get(2)?,
                    push_name: r.get(3)?,
                    ts: r.get(4)?,
                    kind: r.get(5)?,
                    text: r.get(6)?,
                    file_name: r.get(7)?,
                    status: r.get(8)?,
                })
            })
            .map(|rows| rows.flatten().collect())
            .unwrap_or_default();
        rows.into_iter().map(|m| self.to_dto(chat_id, m)).collect()
    }

    /// Media, links and docs: (photos/videos/GIFs, documents, messages with links).
    pub fn chat_media(&self, chat_id: &str) -> (Vec<MessageDto>, Vec<MessageDto>, Vec<MessageDto>) {
        (
            self.chat_messages_where(chat_id, "kind IN ('image', 'video', 'gif')", 1000),
            self.chat_messages_where(chat_id, "kind = 'document'", 500),
            self.chat_messages_where(
                chat_id,
                "kind != 'deleted' AND (text LIKE '%http://%' OR text LIKE '%https://%' OR text LIKE '%www.%')",
                500,
            ),
        )
    }

    pub fn starred(&self) -> Vec<StarredDto> {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT chat_id, id, from_me, sender, push_name, ts, kind, text, file_name, status
             FROM messages WHERE starred = 1 ORDER BY ts DESC LIMIT 500",
        ) else {
            return Vec::new();
        };
        let rows: Vec<(String, StoredMessage)> = stmt
            .query_map([], |r| {
                Ok((
                    r.get(0)?,
                    StoredMessage {
                        id: r.get(1)?,
                        from_me: r.get(2)?,
                        sender: r.get(3)?,
                        push_name: r.get(4)?,
                        ts: r.get(5)?,
                        kind: r.get(6)?,
                        text: r.get(7)?,
                        file_name: r.get(8)?,
                        status: r.get(9)?,
                    },
                ))
            })
            .map(|rows| rows.flatten().collect())
            .unwrap_or_default();
        rows.into_iter()
            .map(|(chat_id, m)| {
                let chat_name = self.chat(&chat_id).map(|c| c.name).unwrap_or_else(|| self.person_name(&chat_id, ""));
                let message = self.to_dto(&chat_id, m);
                StarredDto { chat_id, chat_name, message }
            })
            .collect()
    }

    /// The name someone gave themselves, if we've seen it.
    pub fn push_name_of(&self, jid: &str) -> Option<String> {
        self.db
            .query_row(
                "SELECT push_name FROM names WHERE push_name != '' AND (jid = ?1 OR jid IN (SELECT alt FROM aliases WHERE chat_id = ?1)) LIMIT 1",
                [jid],
                |r| r.get(0),
            )
            .optional()
            .ok()
            .flatten()
    }

    /// Every message of a chat, oldest first, for export: (ts, from_me, sender name, text line).
    pub fn export_lines(&self, chat_id: &str) -> Vec<(i64, String, String)> {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status FROM messages WHERE chat_id = ?1 ORDER BY ts, rowid",
        ) else {
            return Vec::new();
        };
        let rows: Vec<StoredMessage> = stmt
            .query_map([chat_id], |r| {
                Ok(StoredMessage {
                    id: r.get(0)?,
                    from_me: r.get(1)?,
                    sender: r.get(2)?,
                    push_name: r.get(3)?,
                    ts: r.get(4)?,
                    kind: r.get(5)?,
                    text: r.get(6)?,
                    file_name: r.get(7)?,
                    status: r.get(8)?,
                })
            })
            .map(|rows| rows.flatten().collect())
            .unwrap_or_default();
        rows.into_iter()
            .map(|m| {
                let who = if m.from_me {
                    "You".to_string()
                } else if m.sender.is_empty() {
                    self.person_name(chat_id, "")
                } else {
                    self.person_name(&m.sender, &m.push_name)
                };
                let text = match m.kind.as_str() {
                    "text" => m.text.clone(),
                    "deleted" => "This message was deleted".into(),
                    kind => {
                        let label = preview(kind, "", &m.file_name);
                        if m.text.is_empty() { format!("<{label}>") } else { format!("<{label}> {}", m.text) }
                    }
                };
                (m.ts, who, text)
            })
            .collect()
    }

    /// The newest message they sent (reports quote it).
    pub fn newest_incoming(&self, chat_id: &str) -> Option<(String, i64)> {
        self.db
            .query_row(
                "SELECT id, ts FROM messages WHERE chat_id = ?1 AND from_me = 0 ORDER BY ts DESC LIMIT 1",
                [chat_id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()
            .ok()
            .flatten()
    }

    /// In your address book (a saved name is known for them).
    pub fn is_saved(&self, jid: &str) -> bool {
        self.has_full_name(jid) || self.has_full_name(&self.canonical(jid))
    }

    fn has_full_name(&self, jid: &str) -> bool {
        self.db
            .query_row(
                "SELECT 1 FROM names WHERE full_name != '' AND (jid = ?1 OR jid IN (SELECT alt FROM aliases WHERE chat_id = ?1))",
                [jid],
                |_| Ok(()),
            )
            .is_ok()
    }

    /// The phone-number JID for a chat (its own id, an alias, or the LID map).
    /// Saved contacts (names from your address book), one per person, by name.
    pub fn contacts(&self) -> Vec<ContactDto> {
        let rows: Vec<(String, String)> = self
            .db
            .prepare("SELECT jid, full_name FROM names WHERE full_name != '' AND jid NOT LIKE '%@g.us'")
            .and_then(|mut s| s.query_map([], |r| Ok((r.get(0)?, r.get(1)?))).map(|r| r.flatten().collect()))
            .unwrap_or_default();
        let blocked_keys = self.blocked_keys();
        let mut seen = std::collections::HashSet::new();
        let mut contacts: Vec<ContactDto> = rows
            .into_iter()
            .filter_map(|(jid, name)| {
                if self.is_me(&jid) {
                    return None;
                }
                let chat_id = self.canonical(&jid);
                let phone = self.phone_number(&chat_id).or_else(|| self.phone_number(&jid)).unwrap_or_default();
                // The same person under their number and their LID is one contact.
                if !seen.insert(if phone.is_empty() { chat_id.clone() } else { phone.clone() }) {
                    return None;
                }
                let (blocked, has_chat): (bool, bool) = self
                    .db
                    .query_row("SELECT blocked FROM chats WHERE id = ?1", [&chat_id], |r| Ok((r.get(0)?, true)))
                    .unwrap_or((false, false));
                let blocked = blocked
                    || blocked_keys.contains(&chat_id)
                    || blocked_keys.contains(&jid)
                    || (!phone.is_empty() && blocked_keys.contains(&phone));
                let avatar: Option<String> = self
                    .db
                    .query_row("SELECT path FROM avatars WHERE jid = ?1", [&chat_id], |r| r.get(0))
                    .ok()
                    .filter(|p: &String| !p.is_empty());
                Some(ContactDto { chat_id, name, phone, avatar, blocked, has_chat })
            })
            .collect();
        contacts.sort_by_key(|c| c.name.to_lowercase());
        contacts
    }

    pub fn favorite_chats(&self) -> Vec<String> {
        self.db
            .prepare("SELECT jid FROM favorite_chats ORDER BY pos")
            .and_then(|mut s| s.query_map([], |r| r.get(0)).map(|r| r.flatten().collect()))
            .unwrap_or_default()
    }

    pub fn set_favorite_chats(&self, jids: &[String]) {
        let _ = self.db.execute("DELETE FROM favorite_chats", []);
        for (pos, jid) in jids.iter().enumerate() {
            let _ = self.db.execute("INSERT OR IGNORE INTO favorite_chats(jid, pos) VALUES(?1, ?2)", params![jid, pos as i64]);
        }
    }

    /// Adds a call to the history (or replaces what's known of it). True when something changed.
    pub fn put_call(&self, c: &CallEntry) -> bool {
        self.db
            .execute(
                "INSERT INTO call_log(id, ts, duration, incoming, video, result, group_jid, peers) VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
                 ON CONFLICT(id) DO UPDATE SET ts = ?2, duration = ?3, incoming = ?4, video = ?5, result = ?6, group_jid = ?7, peers = ?8
                 WHERE ts != ?2 OR duration != ?3 OR result != ?6 OR peers != ?8 OR video != ?5",
                params![c.id, c.ts, c.duration, c.incoming, c.video, c.result, c.group_jid, c.peers.join(",")],
            )
            .unwrap_or(0)
            > 0
    }

    pub fn remove_call(&self, id: &str) -> bool {
        if id.starts_with(CHAT_CALL) {
            // A call that's a message in a chat: the message stays, the Calls page stops listing it.
            return self.db.execute("INSERT OR IGNORE INTO call_log_hidden(id) VALUES(?1)", [id]).unwrap_or(0) > 0;
        }
        self.db.execute("DELETE FROM call_log WHERE id = ?1", [id]).unwrap_or(0) > 0
    }

    /// The lines WAFluent left in 1:1 chats about its own calls ("Voice call · 0:29"): (message, chat, time, text).
    pub fn call_notices(&self) -> Vec<(String, String, i64, String)> {
        self.db
            .prepare(
                "SELECT id, chat_id, ts, text FROM messages WHERE id LIKE 'notice-%' AND chat_id NOT LIKE '%@g.us'
                 AND (text LIKE '%oice call%' OR text LIKE '%ideo call%')",
            )
            .and_then(|mut s| s.query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?))).map(|r| r.flatten().collect()))
            .unwrap_or_default()
    }

    /// Calls in chats used to be a grey line (the ones made here) or a "📞" text (the phone's):
    /// they become call cards (kind "call", with the call's details), once.
    pub fn call_cards_once(&self) {
        const FLAG: &str = "call_cards_v1";
        if self.flag(FLAG) {
            return;
        }
        let _ = self.db.execute("UPDATE messages SET kind = 'call', text = substr(text, 3) WHERE kind = 'text' AND text LIKE '📞 %call'", []);
        let lines: Vec<(String, String, i64, String)> = self
            .db
            .prepare(
                "SELECT id, chat_id, ts, text FROM messages WHERE id LIKE 'notice-%' AND kind = 'system'
                 AND (text LIKE 'Voice call ·%' OR text LIKE 'Video call ·%' OR text LIKE 'Missed % call')",
            )
            .and_then(|mut s| s.query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?))).map(|r| r.flatten().collect()))
            .unwrap_or_default();
        for (id, chat_id, ts, text) in lines {
            let video = text.contains("ideo call");
            let detail = text.rsplit_once(" · ").map(|(_, d)| d.trim()).unwrap_or("");
            // "0:21", "1:02:33": the time talked.
            let talked = detail.split(':').try_fold(0i64, |sum, part| part.parse::<i64>().ok().map(|n| sum * 60 + n)).filter(|_| detail.contains(':'));
            let (result, duration) = match talked {
                _ if text.starts_with("Missed") => ("missed", 0),
                _ if detail == "Declined" => ("rejected", 0),
                Some(seconds) => ("connected", seconds),
                None => ("cancelled", 0),
            };
            // Who called is in the call history (the line didn't say); without it, a missed call was theirs.
            let incoming: bool = self
                .db
                .query_row(
                    "SELECT incoming FROM call_log WHERE ts BETWEEN ?2 - ?3 - 180 AND ?2 + 5
                     AND ((',' || peers || ',') LIKE '%,' || ?1 || ',%' OR group_jid = ?1) ORDER BY ts DESC LIMIT 1",
                    params![chat_id, ts, duration],
                    |r| r.get(0),
                )
                .unwrap_or(result == "missed");
            let _ = self.db.execute(
                "UPDATE messages SET kind = 'call', from_me = ?3, text = ?4 WHERE chat_id = ?1 AND id = ?2",
                params![chat_id, id, !incoming, crate::extract::call_title(video, result == "missed")],
            );
            self.insert_extra(&chat_id, &id, &[], Some(&serde_json::json!({ "call": { "video": video, "result": result, "duration": duration } })));
        }
        self.set_flag(FLAG);
    }

    /// Whether the history has a call with this chat within two minutes of `ts`.
    pub fn call_near(&self, chat_id: &str, ts: i64) -> bool {
        self.db
            .query_row(
                "SELECT 1 FROM call_log WHERE abs(ts - ?2) <= 120 AND (',' || peers || ',') LIKE '%,' || ?1 || ',%'",
                params![chat_id, ts],
                |_| Ok(()),
            )
            .is_ok()
    }

    /// The calls WhatsApp wrote into 1:1 chats ("Voice call", "Missed video call"), as history
    /// rows: most of the history, since the phone's own call list only syncs a few. Newer ones
    /// carry their outcome and length (extract.rs); older ones only say voice or video.
    fn chat_calls(&self, limit: u32) -> Vec<CallRow> {
        let found: Vec<(String, String, i64, bool, String, String)> = self
            .db
            .prepare(
                "SELECT m.id, m.chat_id, m.ts, m.from_me, m.text, COALESCE(e.data, '') FROM messages m
                 LEFT JOIN extras e ON e.chat_id = m.chat_id AND e.message_id = m.id
                 WHERE m.chat_id NOT LIKE '%@g.us' AND m.id NOT LIKE 'notice-%'
                   AND (m.kind = 'call' OR (m.kind = 'text' AND m.text LIKE ?2) OR (m.kind = 'system' AND m.text LIKE '%Missed % call'))
                 ORDER BY m.ts DESC LIMIT ?1",
            )
            .and_then(|mut s| {
                s.query_map(params![limit, "📞 %call"], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))
                    .map(|r| r.flatten().collect())
            })
            .unwrap_or_default();
        found
            .into_iter()
            .filter_map(|(message_id, chat_id, ts, from_me, text, extra)| {
                let id = format!("{CHAT_CALL}{chat_id}|{message_id}");
                if self.db.query_row("SELECT 1 FROM call_log_hidden WHERE id = ?1", [&id], |_| Ok(())).is_ok() {
                    return None;
                }
                let details = serde_json::from_str::<serde_json::Value>(&extra).ok().and_then(|e| e.get("call").cloned());
                let detail = |key: &str| details.as_ref().and_then(|d| d.get(key).cloned());
                let video = detail("video").and_then(|v| v.as_bool()).unwrap_or_else(|| text.contains("ideo call"));
                let result = detail("result")
                    .and_then(|v| v.as_str().map(str::to_string))
                    .unwrap_or_else(|| if text.contains("Missed") { "missed" } else { "connected" }.to_string());
                let duration = detail("duration").and_then(|v| v.as_i64()).unwrap_or(0);
                Some((id, ts, duration, !from_me, video, result, String::new(), chat_id))
            })
            .collect()
    }

    /// The call history, newest first, with each call's person (or people) named.
    pub fn calls(&self, limit: u32) -> Vec<CallLogDto> {
        let mut rows: Vec<CallRow> = self
            .db
            .prepare("SELECT id, ts, duration, incoming, video, result, group_jid, peers FROM call_log ORDER BY ts DESC LIMIT ?1")
            .and_then(|mut s| {
                s.query_map([limit], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?, r.get(6)?, r.get(7)?)))
                    .map(|r| r.flatten().collect())
            })
            .unwrap_or_default();
        // The ones in chats too, unless the phone's list has the same call (same person, same minute).
        for call in self.chat_calls(limit) {
            let listed = rows.iter().any(|r| (r.1 - call.1).abs() <= 120 && r.7.split(',').any(|p| self.canonical(p) == call.7));
            if !listed {
                rows.push(call);
            }
        }
        rows.sort_by(|a, b| b.1.cmp(&a.1));
        rows.truncate(limit as usize);
        rows.into_iter()
            .map(|(id, ts, duration, incoming, video, result, group_jid, peers)| {
                let mut people: Vec<String> = Vec::new();
                for peer in peers.split(',').filter(|p| !p.is_empty()) {
                    let chat = self.canonical(peer);
                    if !self.is_me(&chat) && !self.is_me(peer) && !people.contains(&chat) {
                        people.push(chat);
                    }
                }
                let named = |jid: &str| self.chat(jid).map(|c| c.name).filter(|n| !n.is_empty()).unwrap_or_else(|| self.person_name(jid, ""));
                let group = !group_jid.is_empty() || people.len() > 1;
                let (chat_id, name) = if !group_jid.is_empty() && self.chat(&group_jid).is_some() {
                    (group_jid.clone(), named(&group_jid))
                } else if people.len() == 1 {
                    (people[0].clone(), named(&people[0]))
                } else if people.is_empty() {
                    (String::new(), "Unknown".to_string())
                } else {
                    // "Iqra & khadija": first names, like WhatsApp.
                    let first: Vec<String> = people.iter().map(|p| named(p).split_whitespace().next().unwrap_or("").to_string()).collect();
                    (String::new(), first.join(" & "))
                };
                let phone = if group { String::new() } else { self.phone_number(&chat_id).unwrap_or_default() };
                let avatar: Option<String> = self
                    .db
                    .query_row("SELECT path FROM avatars WHERE jid = ?1", [&chat_id], |r| r.get(0))
                    .ok()
                    .filter(|p: &String| !p.is_empty());
                CallLogDto { id, ts, duration, incoming, video, result, chat_id, name, phone, avatar, group }
            })
            .collect()
    }

    // ───────────── Channels ─────────────

    /// The channels you follow, as last heard from WhatsApp.
    pub fn channels(&self) -> Vec<crate::protocol::ChannelDto> {
        self.db
            .prepare("SELECT data FROM channels ORDER BY pos")
            .and_then(|mut s| s.query_map([], |r| r.get::<_, String>(0)).map(|r| r.flatten().collect::<Vec<_>>()))
            .unwrap_or_default()
            .iter()
            .filter_map(|data| serde_json::from_str(data).ok())
            .collect()
    }

    pub fn channel(&self, id: &str) -> Option<crate::protocol::ChannelDto> {
        let data: String = self.db.query_row("SELECT data FROM channels WHERE id = ?1", [id], |r| r.get(0)).ok()?;
        serde_json::from_str(&data).ok()
    }

    /// The followed list, whole: ones no longer in it go (when each was last opened is kept).
    pub fn set_channels(&self, list: &[crate::protocol::ChannelDto]) {
        for (pos, channel) in list.iter().enumerate() {
            let _ = self.db.execute(
                "INSERT INTO channels(id, data, pos) VALUES(?1, ?2, ?3) ON CONFLICT(id) DO UPDATE SET data = excluded.data, pos = excluded.pos",
                params![channel.id, serde_json::to_string(channel).unwrap_or_default(), pos as i64],
            );
        }
        let kept: Vec<String> = self
            .db
            .prepare("SELECT id FROM channels")
            .and_then(|mut s| s.query_map([], |r| r.get(0)).map(|r| r.flatten().collect()))
            .unwrap_or_default();
        for id in kept.iter().filter(|id| !list.iter().any(|c| &c.id == *id)) {
            let _ = self.db.execute("DELETE FROM channels WHERE id = ?1", [id]);
        }
    }

    pub fn forget_channel(&self, id: &str) {
        let _ = self.db.execute("DELETE FROM channels WHERE id = ?1", [id]);
    }

    pub fn set_channel_muted(&self, id: &str, muted: bool) {
        if let Some(mut channel) = self.channel(id) {
            channel.muted = muted;
            let _ = self.db.execute("UPDATE channels SET data = ?2 WHERE id = ?1", params![id, serde_json::to_string(&channel).unwrap_or_default()]);
        }
    }

    /// When you last opened a channel (0: never), Unix seconds.
    pub fn channel_seen(&self, id: &str) -> i64 {
        self.db.query_row("SELECT seen_ts FROM channels WHERE id = ?1", [id], |r| r.get(0)).unwrap_or(0)
    }

    pub fn set_channel_seen(&self, id: &str, ts: i64) {
        let _ = self.db.execute("UPDATE channels SET seen_ts = ?2 WHERE id = ?1", params![id, ts]);
    }

    /// A post that's kept already: its text (it may have been edited) and its details (the reaction counts).
    pub fn update_post(&self, chat_id: &str, id: &str, text: &str, extra: &serde_json::Value) {
        let _ = self.db.execute("UPDATE messages SET text = ?3 WHERE chat_id = ?1 AND id = ?2", params![chat_id, id, text]);
        let _ = self.db.execute("UPDATE extras SET data = ?3 WHERE chat_id = ?1 AND message_id = ?2", params![chat_id, id, extra.to_string()]);
    }

    /// The server id of the oldest post kept for a channel (where asking for older ones starts).
    pub fn oldest_post(&self, chat_id: &str) -> Option<u64> {
        self.db
            .query_row("SELECT MIN(json_extract(data, '$.channel.serverId')) FROM extras WHERE chat_id = ?1 AND data != ''", [chat_id], |r| {
                r.get::<_, Option<i64>>(0)
            })
            .ok()
            .flatten()
            .map(|n| n as u64)
    }

    pub fn post_server_id(&self, chat_id: &str, id: &str) -> Option<u64> {
        self.db
            .query_row("SELECT json_extract(data, '$.channel.serverId') FROM extras WHERE chat_id = ?1 AND message_id = ?2 AND data != ''", [chat_id, id], |r| {
                r.get::<_, Option<i64>>(0)
            })
            .ok()
            .flatten()
            .map(|n| n as u64)
    }

    /// Your reaction to a post ("" none), as kept here.
    pub fn post_mine(&self, chat_id: &str, id: &str) -> Option<String> {
        self.db
            .query_row("SELECT json_extract(data, '$.channel.mine') FROM extras WHERE chat_id = ?1 AND message_id = ?2 AND data != ''", [chat_id, id], |r| {
                r.get::<_, Option<String>>(0)
            })
            .ok()
            .flatten()
            .filter(|mine| !mine.is_empty())
    }

    /// You reacted to a post ("" took it back): the one you had counts one less, the new one one more.
    pub fn set_post_mine(&self, chat_id: &str, id: &str, emoji: &str) {
        let Ok(data) = self.db.query_row("SELECT data FROM extras WHERE chat_id = ?1 AND message_id = ?2", [chat_id, id], |r| r.get::<_, String>(0)) else { return };
        let Ok(mut extra) = serde_json::from_str::<serde_json::Value>(&data) else { return };
        let before = extra["channel"]["mine"].as_str().unwrap_or("").to_string();
        let mut counts: Vec<(String, u64)> = extra["channel"]["reactions"]
            .as_array()
            .map(|all| all.iter().filter_map(|one| Some((one[0].as_str()?.to_string(), one[1].as_u64()?))).collect())
            .unwrap_or_default();
        if !before.is_empty()
            && let Some(old) = counts.iter_mut().find(|(code, _)| *code == before)
        {
            old.1 = old.1.saturating_sub(1);
        }
        if !emoji.is_empty() {
            match counts.iter_mut().find(|(code, _)| code == emoji) {
                Some(now) => now.1 += 1,
                None => counts.push((emoji.to_string(), 1)),
            }
        }
        counts.retain(|(_, n)| *n > 0);
        extra["channel"]["reactions"] = counts.iter().map(|(code, n)| serde_json::json!([code, n])).collect();
        extra["channel"]["mine"] = serde_json::json!(emoji);
        let _ = self.db.execute("UPDATE extras SET data = ?3 WHERE chat_id = ?1 AND message_id = ?2", params![chat_id, id, extra.to_string()]);
    }

    /// The kept post with this server id.
    pub fn post_by_server_id(&self, chat_id: &str, server_id: u64) -> Option<String> {
        self.db
            .query_row(
                "SELECT message_id FROM extras WHERE chat_id = ?1 AND data != '' AND json_extract(data, '$.channel.serverId') = ?2",
                params![chat_id, server_id as i64],
                |r| r.get(0),
            )
            .ok()
    }

    /// New reaction counts for the post with this server id; its message id when it's kept.
    pub fn set_post_reactions(&self, chat_id: &str, server_id: u64, reactions: &[(String, u64)]) -> Option<String> {
        let (id, data): (String, String) = self
            .db
            .query_row(
                "SELECT message_id, data FROM extras WHERE chat_id = ?1 AND data != '' AND json_extract(data, '$.channel.serverId') = ?2",
                params![chat_id, server_id as i64],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .ok()?;
        let mut extra: serde_json::Value = serde_json::from_str(&data).ok()?;
        extra["channel"]["reactions"] = reactions.iter().map(|(emoji, count)| serde_json::json!([emoji, count])).collect();
        let _ = self.db.execute("UPDATE extras SET data = ?3 WHERE chat_id = ?1 AND message_id = ?2", params![chat_id, id, extra.to_string()]);
        Some(id)
    }

    // ───────────── Status updates ─────────────

    /// Status updates newer than `since`, oldest first (see status.rs).
    pub fn statuses(&self, since: i64) -> Vec<crate::protocol::StatusDto> {
        let chat = crate::status::CHAT;
        let rows: Vec<StoredMessage> = self
            .db
            .prepare(
                "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status FROM messages
                 WHERE chat_id = ?1 AND ts > ?2 AND kind != 'deleted' ORDER BY ts ASC, rowid ASC",
            )
            .and_then(|mut stmt| {
                stmt.query_map(params![chat, since], |r| {
                    Ok(StoredMessage {
                        id: r.get(0)?,
                        from_me: r.get(1)?,
                        sender: r.get(2)?,
                        push_name: r.get(3)?,
                        ts: r.get(4)?,
                        kind: r.get(5)?,
                        text: r.get(6)?,
                        file_name: r.get(7)?,
                        status: r.get(8)?,
                    })
                })?
                .collect()
            })
            .unwrap_or_default();
        let mut people: std::collections::HashMap<String, (String, Option<String>)> = Default::default();
        rows.into_iter()
            .map(|m| {
                let author = if m.from_me { String::new() } else { self.canonical(&m.sender) };
                let (name, avatar) = people
                    .entry(author.clone())
                    .or_insert_with(|| {
                        let key = if author.is_empty() { crate::avatars::SELF_ID } else { author.as_str() };
                        let avatar = self.avatar(key).map(|(_, path, _)| path).filter(|p| !p.is_empty());
                        let name = if author.is_empty() { "My status".to_string() } else { self.person_name(&author, &m.push_name) };
                        (name, avatar)
                    })
                    .clone();
                let seen = self.db.query_row("SELECT 1 FROM status_seen WHERE id = ?1", [&m.id], |_| Ok(())).is_ok();
                let views = if m.from_me {
                    self.receipts(chat, &m.id).iter().filter(|r| r.status >= 3 && !self.is_me(&r.user)).count() as u32
                } else {
                    0
                };
                crate::protocol::StatusDto { author, name, avatar, seen, views, message: self.to_dto(chat, m) }
            })
            .collect()
    }

    /// True when it hadn't been looked at before.
    pub fn set_status_seen(&self, id: &str) -> bool {
        self.db.execute("INSERT OR IGNORE INTO status_seen(id) VALUES(?1)", [id]).unwrap_or(0) > 0
    }

    pub fn forget_status_seen(&self, id: &str) {
        let _ = self.db.execute("DELETE FROM status_seen WHERE id = ?1", [id]);
    }

    pub fn phone_jid(&self, jid: &str) -> Option<String> {
        self.phone_number(jid).map(|n| format!("{n}@s.whatsapp.net"))
    }

    pub fn clear_pins(&self) {
        let _ = self.db.execute("UPDATE chats SET pinned = 0", []);
    }

    pub fn pinned_count(&self) -> u32 {
        self.db.query_row("SELECT COUNT(*) FROM chats WHERE pinned > 0", [], |r| r.get(0)).unwrap_or(0)
    }

    /// Small key/value flags (one-time migrations).
    pub fn flag(&self, key: &str) -> bool {
        let _ = self.db.execute("CREATE TABLE IF NOT EXISTS flags(key TEXT PRIMARY KEY)", []);
        self.db.query_row("SELECT 1 FROM flags WHERE key = ?1", [key], |_| Ok(())).is_ok()
    }

    pub fn set_flag(&self, key: &str) {
        let _ = self.db.execute("CREATE TABLE IF NOT EXISTS flags(key TEXT PRIMARY KEY)", []);
        let _ = self.db.execute("INSERT OR IGNORE INTO flags(key) VALUES(?1)", [key]);
    }

    pub fn increment_unread(&self, chat_id: &str) {
        let _ = self.db.execute("UPDATE chats SET unread = unread + 1, marked_unread = 0 WHERE id = ?1", [chat_id]);
    }

    pub fn mark_read(&self, chat_id: &str) {
        let _ = self.db.execute("UPDATE chats SET unread = 0, marked_unread = 0 WHERE id = ?1", [chat_id]);
    }

    /// "Mark as unread": a dot in the list, until the chat is opened.
    pub fn set_marked_unread(&self, chat_id: &str, marked: bool) {
        let _ = self.db.execute("UPDATE chats SET marked_unread = ?2 WHERE id = ?1", params![chat_id, marked]);
    }

    // ───────────── Disappearing messages ─────────────

    /// The chat's timer in seconds (0: off).
    pub fn ephemeral(&self, chat_id: &str) -> u32 {
        self.db.query_row("SELECT ephemeral FROM chats WHERE id = ?1", [chat_id], |r| r.get(0)).unwrap_or(0)
    }

    pub fn set_ephemeral(&self, chat_id: &str, seconds: u32) -> bool {
        self.db
            .execute("UPDATE chats SET ephemeral = ?2 WHERE id = ?1 AND ephemeral != ?2", params![chat_id, seconds])
            .unwrap_or(0)
            > 0
    }

    /// When the message goes (Unix seconds; `seconds` 0 leaves it).
    pub fn set_expiry(&self, chat_id: &str, message_id: &str, sent_at: i64, seconds: u32) {
        if seconds > 0 {
            let _ = self.db.execute(
                "UPDATE messages SET expires_at = ?3 WHERE chat_id = ?1 AND id = ?2",
                params![chat_id, message_id, sent_at + i64::from(seconds)],
            );
        }
    }

    /// Messages whose time is up (starred ones stay, like kept ones on the phone).
    pub fn expired(&self, now: i64) -> Vec<(String, String)> {
        let Ok(mut stmt) = self
            .db
            .prepare("SELECT chat_id, id FROM messages WHERE expires_at > 0 AND expires_at <= ?1 AND starred = 0 LIMIT 500")
        else {
            return Vec::new();
        };
        stmt.query_map([now], |r| Ok((r.get(0)?, r.get(1)?))).map(|rows| rows.flatten().collect()).unwrap_or_default()
    }

    /// The newest message in a chat (for archive / read state synced to the phone).
    pub fn last_message(&self, chat_id: &str) -> Option<StoredMessage> {
        self.db
            .query_row(
                "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status
                 FROM messages WHERE chat_id = ?1 AND kind != 'system' ORDER BY ts DESC, rowid DESC LIMIT 1",
                [chat_id],
                |r| {
                    Ok(StoredMessage {
                        id: r.get(0)?,
                        from_me: r.get(1)?,
                        sender: r.get(2)?,
                        push_name: r.get(3)?,
                        ts: r.get(4)?,
                        kind: r.get(5)?,
                        text: r.get(6)?,
                        file_name: r.get(7)?,
                        status: r.get(8)?,
                    })
                },
            )
            .ok()
    }

    pub fn set_push_name(&self, jid: &str, name: &str) {
        if name.is_empty() {
            return;
        }
        let _ = self.db.execute(
            "INSERT INTO names(jid, push_name) VALUES(?1, ?2)
             ON CONFLICT(jid) DO UPDATE SET push_name = excluded.push_name",
            params![jid, name],
        );
    }

    pub fn set_full_name(&self, jid: &str, name: &str) {
        if name.is_empty() {
            return;
        }
        let _ = self.db.execute(
            "INSERT INTO names(jid, full_name) VALUES(?1, ?2)
             ON CONFLICT(jid) DO UPDATE SET full_name = excluded.full_name",
            params![jid, name],
        );
    }

    /// Forget everything (after logging out).
    pub fn clear(&self) {
        let _ = self.db.execute_batch(
            "DELETE FROM messages; DELETE FROM chats; DELETE FROM names; DELETE FROM aliases; DELETE FROM avatars; DELETE FROM media; DELETE FROM quotes; DELETE FROM reactions; DELETE FROM numbers; DELETE FROM extras; DELETE FROM polls; DELETE FROM poll_votes; DELETE FROM status_seen; DELETE FROM channels;",
        );
    }

    /// (picture_id, path, checked_at) for a cached profile picture.
    pub fn avatar(&self, jid: &str) -> Option<(String, String, i64)> {
        self.db
            .query_row("SELECT picture_id, path, checked_at FROM avatars WHERE jid = ?1", [jid], |r| {
                Ok((r.get(0)?, r.get(1)?, r.get(2)?))
            })
            .optional()
            .ok()
            .flatten()
    }

    /// LID chats recorded as having no picture count as never checked again.
    pub fn forget_missing_lid_avatars(&self) {
        let _ = self.db.execute("DELETE FROM avatars WHERE path = '' AND jid LIKE '%@lid'", []);
    }

    pub fn set_avatar(&self, jid: &str, picture_id: &str, path: &str, checked_at: i64) {
        let _ = self.db.execute(
            "INSERT INTO avatars(jid, picture_id, path, checked_at) VALUES(?1, ?2, ?3, ?4)
             ON CONFLICT(jid) DO UPDATE SET picture_id = excluded.picture_id, path = excluded.path, checked_at = excluded.checked_at",
            params![jid, picture_id, path, checked_at],
        );
    }

    /// Chats whose picture was never fetched or was last checked before `older_than`, most recent first.
    pub fn chats_needing_avatar(&self, older_than: i64) -> Vec<String> {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT c.id FROM chats c LEFT JOIN avatars a ON a.jid = c.id
             WHERE c.last_ts > 0 AND (a.jid IS NULL OR a.checked_at < ?1)
             ORDER BY c.pinned DESC, c.last_ts DESC",
        ) else {
            return Vec::new();
        };
        stmt.query_map([older_than], |r| r.get(0)).map(|rows| rows.flatten().collect()).unwrap_or_default()
    }

    // ───────────── Reads ─────────────

    /// Chats that have at least one message, pinned first, newest first.
    pub fn chats(&self) -> Vec<ChatDto> {
        // Chats with something in them, and your favourites even when this PC has none of their messages.
        let sql = format!(
            "{CHAT_SELECT} WHERE c.last_ts > 0 OR c.id IN (SELECT jid FROM favorite_chats)
             OR c.id IN (SELECT chat_id FROM aliases WHERE alt IN (SELECT jid FROM favorite_chats))
             ORDER BY c.pinned DESC, c.last_ts DESC"
        );
        self.query_chats(&sql, [])
    }

    pub fn chat(&self, id: &str) -> Option<ChatDto> {
        let sql = format!("{CHAT_SELECT} WHERE c.id = ?1");
        self.query_chats(&sql, [id]).into_iter().next()
    }

    fn query_chats<P: rusqlite::Params>(&self, sql: &str, params: P) -> Vec<ChatDto> {
        let Ok(mut stmt) = self.db.prepare(sql) else { return Vec::new() };
        let now = unix_now();
        let rows = stmt.query_map(params, |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, bool>(2)?,
                r.get::<_, u32>(3)?,
                r.get::<_, i64>(4)?,
                r.get::<_, bool>(5)?,
                r.get::<_, i64>(6)?,
                r.get::<_, i64>(7)?,
                r.get::<_, Option<String>>(8)?,
                r.get::<_, Option<String>>(9)?,
                r.get::<_, Option<String>>(10)?,
                r.get::<_, Option<bool>>(11)?,
                r.get::<_, Option<u8>>(12)?,
                r.get::<_, Option<String>>(13)?,
                r.get::<_, Option<String>>(14)?,
                r.get::<_, Option<String>>(15)?,
                r.get::<_, bool>(16)?,
                r.get::<_, String>(17)?,
                r.get::<_, u32>(18)?,
                r.get::<_, bool>(19)?,
            ))
        });
        let Ok(rows) = rows else { return Vec::new() };
        rows.flatten()
            .map(|(id, name, is_group, unread, pinned, archived, mute_end, last_ts, kind, text, file_name, from_me, status, sender, push_name, avatar, blocked, pinned_msg, ephemeral, marked_unread)| {
                let kind = kind.unwrap_or_default();
                let from_me = from_me.unwrap_or(false);
                let saved = !is_group && self.has_full_name(&id);
                let push = if is_group { None } else { self.push_name_of(&id) };
                let phone = if is_group { None } else { self.phone_number(&id).and_then(|n| phone_parts(&n)) };
                let _ = pinned_msg;
                let pinned_messages = self.pins(&id);
                let last_sender = match (&sender, is_group && !from_me) {
                    (Some(s), true) => Some(short_name(&self.person_name(s, push_name.as_deref().unwrap_or("")))),
                    _ => None,
                };
                ChatDto {
                    name: if name.is_empty() { self.person_name(&id, "") } else { name },
                    preview: preview(&kind, &plain_mentions(&self.render_mentions(text.as_deref().unwrap_or(""))), file_name.as_deref().unwrap_or("")),
                    preview_kind: kind,
                    muted: mute_end == -1 || mute_end > now,
                    last_from_me: from_me,
                    // Messages to yourself are read as they're sent (blue ticks, like the phone).
                    last_status: if from_me && self.is_me(&id) { 3 } else { status.unwrap_or(0) },
                    ephemeral,
                    marked_unread: marked_unread && unread == 0,
                    id,
                    is_group,
                    unread,
                    pinned: pinned > 0,
                    pinned_at: pinned,
                    archived,
                    last_ts,
                    last_sender,
                    avatar: avatar.filter(|p| !p.is_empty()),
                    blocked,
                    saved,
                    push_name: push,
                    phone,
                    pinned_messages,
                }
            })
            .collect()
    }

    /// The newest `limit` messages of a chat, oldest first.
    pub fn messages(&self, chat_id: &str, limit: u32) -> Vec<MessageDto> {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status
             FROM messages WHERE chat_id = ?1 ORDER BY ts DESC, rowid DESC LIMIT ?2",
        ) else {
            return Vec::new();
        };
        let rows = stmt.query_map(params![chat_id, limit], |r| {
            Ok(StoredMessage {
                id: r.get(0)?,
                from_me: r.get(1)?,
                sender: r.get(2)?,
                push_name: r.get(3)?,
                ts: r.get(4)?,
                kind: r.get(5)?,
                text: r.get(6)?,
                file_name: r.get(7)?,
                status: r.get(8)?,
            })
        });
        let Ok(rows) = rows else { return Vec::new() };
        let mut out: Vec<MessageDto> = rows.flatten().map(|m| self.to_dto(chat_id, m)).collect();
        out.reverse();
        out
    }

    /// Up to `limit` messages older than the anchor message, oldest first.
    /// Newest first, up to 300: text, captions and file names containing `query`
    /// (case-insensitive for Latin letters).
    pub fn search(&self, chat_id: &str, query: &str) -> Vec<MessageDto> {
        let needle = query.trim().to_lowercase();
        if needle.is_empty() {
            return Vec::new();
        }
        let rows: Vec<StoredMessage> = self
            .db
            .prepare(
                "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status FROM messages
                 WHERE chat_id = ?1 AND kind NOT IN ('system', 'deleted')
                   AND (instr(lower(text), ?2) > 0 OR instr(lower(file_name), ?2) > 0)
                 ORDER BY ts DESC, rowid DESC LIMIT 300",
            )
            .and_then(|mut stmt| {
                stmt.query_map(params![chat_id, needle], |r| {
                    Ok(StoredMessage {
                        id: r.get(0)?,
                        from_me: r.get(1)?,
                        sender: r.get(2)?,
                        push_name: r.get(3)?,
                        ts: r.get(4)?,
                        kind: r.get(5)?,
                        text: r.get(6)?,
                        file_name: r.get(7)?,
                        status: r.get(8)?,
                    })
                })?
                .collect()
            })
            .unwrap_or_default();
        rows.into_iter().map(|m| self.to_dto(chat_id, m)).collect()
    }

    /// The oldest message's time in a chat.
    pub fn oldest_ts(&self, chat_id: &str) -> Option<i64> {
        self.db.query_row("SELECT MIN(ts) FROM messages WHERE chat_id = ?1", [chat_id], |r| r.get(0)).ok().flatten()
    }

    /// The first message on or after `ts` (else the last one before it).
    pub fn message_at(&self, chat_id: &str, ts: i64) -> Option<(String, i64)> {
        let after = self
            .db
            .query_row(
                "SELECT id, ts FROM messages WHERE chat_id = ?1 AND ts >= ?2 AND kind != 'system' ORDER BY ts, rowid LIMIT 1",
                params![chat_id, ts],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .ok();
        after.or_else(|| {
            self.db
                .query_row(
                    "SELECT id, ts FROM messages WHERE chat_id = ?1 AND ts < ?2 AND kind != 'system' ORDER BY ts DESC, rowid DESC LIMIT 1",
                    params![chat_id, ts],
                    |r| Ok((r.get(0)?, r.get(1)?)),
                )
                .ok()
        })
    }

    /// How many messages are older than (before_ts, before_id) back to `until_ts`.
    pub fn count_between(&self, chat_id: &str, before_ts: i64, before_id: &str, until_ts: i64) -> u32 {
        self.db
            .query_row(
                "SELECT COUNT(*) FROM messages
                 WHERE chat_id = ?1 AND ts >= ?4
                   AND (ts < ?2 OR (ts = ?2 AND rowid < IFNULL((SELECT rowid FROM messages WHERE chat_id = ?1 AND id = ?3), -1)))",
                params![chat_id, before_ts, before_id, until_ts],
                |r| r.get(0),
            )
            .unwrap_or(0)
    }

    pub fn messages_before(&self, chat_id: &str, before_ts: i64, before_id: &str, limit: u32) -> Vec<MessageDto> {
        let Ok(mut stmt) = self.db.prepare(
            "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status
             FROM messages
             WHERE chat_id = ?1
               AND (ts < ?2 OR (ts = ?2 AND rowid < IFNULL((SELECT rowid FROM messages WHERE chat_id = ?1 AND id = ?3), -1)))
             ORDER BY ts DESC, rowid DESC LIMIT ?4",
        ) else {
            return Vec::new();
        };
        let rows = stmt.query_map(params![chat_id, before_ts, before_id, limit], |r| {
            Ok(StoredMessage {
                id: r.get(0)?,
                from_me: r.get(1)?,
                sender: r.get(2)?,
                push_name: r.get(3)?,
                ts: r.get(4)?,
                kind: r.get(5)?,
                text: r.get(6)?,
                file_name: r.get(7)?,
                status: r.get(8)?,
            })
        });
        let Ok(rows) = rows else { return Vec::new() };
        let mut out: Vec<MessageDto> = rows.flatten().map(|m| self.to_dto(chat_id, m)).collect();
        out.reverse();
        out
    }

    pub fn message(&self, chat_id: &str, id: &str) -> Option<StoredMessage> {
        self.db
            .query_row(
                "SELECT id, from_me, sender, push_name, ts, kind, text, file_name, status
                 FROM messages WHERE chat_id = ?1 AND id = ?2",
                [chat_id, id],
                |r| {
                    Ok(StoredMessage {
                        id: r.get(0)?,
                        from_me: r.get(1)?,
                        sender: r.get(2)?,
                        push_name: r.get(3)?,
                        ts: r.get(4)?,
                        kind: r.get(5)?,
                        text: r.get(6)?,
                        file_name: r.get(7)?,
                        status: r.get(8)?,
                    })
                },
            )
            .optional()
            .ok()
            .flatten()
    }

    /// Newest stored message of a chat: (id, from_me, ts).
    pub fn newest(&self, chat_id: &str) -> Option<(String, bool, i64)> {
        self.db
            .query_row(
                "SELECT id, from_me, ts FROM messages WHERE chat_id = ?1 ORDER BY ts DESC, rowid DESC LIMIT 1",
                [chat_id],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .optional()
            .ok()
            .flatten()
    }

    /// Oldest stored message of a chat: (id, from_me, ts) — the anchor for asking the phone.
    pub fn oldest(&self, chat_id: &str) -> Option<(String, bool, i64)> {
        self.db
            .query_row(
                "SELECT id, from_me, ts FROM messages WHERE chat_id = ?1 ORDER BY ts ASC, rowid ASC LIMIT 1",
                [chat_id],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .optional()
            .ok()
            .flatten()
    }

    pub fn to_dto(&self, chat_id: &str, m: StoredMessage) -> MessageDto {
        let sender_name = if m.from_me { String::new() } else { self.person_name(&m.sender, &m.push_name) };
        let media = self.media_dto(chat_id, &m.id);
        let reply = self.reply_dto(chat_id, &m.id);
        let (reactions, my_reaction) = self.reactions(chat_id, &m.id);
        let (starred, edited, forwarded): (bool, bool, u32) = self
            .db
            .query_row("SELECT starred, edited, forwarded FROM messages WHERE chat_id = ?1 AND id = ?2", [chat_id, &m.id], |r| {
                Ok((r.get(0)?, r.get(1)?, r.get(2)?))
            })
            .unwrap_or((false, false, 0));
        let (thumb, extra) = self.extra(chat_id, &m.id);
        MessageDto {
            thumb,
            extra,
            starred,
            edited,
            forwarded,
            media,
            reply,
            reactions,
            my_reaction,
            id: m.id,
            from_me: m.from_me,
            sender: m.sender,
            sender_name,
            ts: m.ts,
            kind: m.kind,
            text: self.render_mentions(&m.text),
            file_name: m.file_name,
            status: if m.from_me && self.is_me(chat_id) { m.status.max(3) } else { m.status },
        }
    }

    // ───────────── Extras ─────────────

    /// True when this message had no preview/details stored yet.
    pub fn insert_extra(&self, chat_id: &str, message_id: &str, thumb: &[u8], extra: Option<&serde_json::Value>) -> bool {
        if thumb.is_empty() && extra.is_none() {
            return false;
        }
        self.db
            .execute(
                "INSERT OR IGNORE INTO extras(chat_id, message_id, thumb, data) VALUES(?1, ?2, ?3, ?4)",
                params![
                    chat_id,
                    message_id,
                    (!thumb.is_empty()).then_some(thumb),
                    extra.map(|e| e.to_string()).unwrap_or_default()
                ],
            )
            .unwrap_or(0)
            > 0
    }

    /// (preview as base64, details) for the UI. Polls get their current results too.
    fn extra(&self, chat_id: &str, message_id: &str) -> (Option<String>, Option<serde_json::Value>) {
        let (thumb, mut extra) = self
            .db
            .query_row(
                "SELECT thumb, data FROM extras WHERE chat_id = ?1 AND message_id = ?2",
                [chat_id, message_id],
                |r| Ok((r.get::<_, Option<Vec<u8>>>(0)?, r.get::<_, String>(1)?)),
            )
            .map(|(thumb, data)| (thumb.filter(|t| !t.is_empty()).map(|t| base64(&t)), serde_json::from_str::<serde_json::Value>(&data).ok()))
            .unwrap_or((None, None));
        if let Some(obj) = extra.as_mut().and_then(|e| e.as_object_mut()).filter(|o| o.contains_key("options")) {
            let (votes, mine) = self.poll_results(chat_id, message_id);
            obj.insert("votes".into(), votes);
            obj.insert("mine".into(), mine);
        }
        (thumb, extra)
    }

    // ───────────── Sticker panel ─────────────

    /// A sticker starred on the phone (or its new CDN reference); keeps the downloaded file
    /// and its place in the list.
    pub fn set_favorite(&self, key: &str, m: &Media) {
        let _ = self.db.execute(
            "INSERT INTO media(chat_id, message_id, media_type, direct_path, media_key, file_sha256,
                               file_enc_sha256, file_length, mimetype, width, height)
             VALUES(?1, ?2, 'sticker', ?3, ?4, x'', ?5, ?6, ?7, ?8, ?9)
             ON CONFLICT(chat_id, message_id) DO UPDATE SET
                direct_path = excluded.direct_path, media_key = excluded.media_key,
                file_enc_sha256 = excluded.file_enc_sha256, file_length = excluded.file_length,
                mimetype = excluded.mimetype, width = excluded.width, height = excluded.height",
            params![FAVORITES, key, m.direct_path, m.media_key, m.file_enc_sha256, m.file_length as i64, m.mimetype, m.width, m.height],
        );
        let _ = self.db.execute("INSERT OR IGNORE INTO favorite_stickers(key, ts) VALUES(?1, ?2)", params![key, unix_now()]);
    }

    pub fn remove_favorite(&self, key: &str) {
        let _ = self.db.execute("DELETE FROM favorite_stickers WHERE key = ?1", [key]);
        if let Ok(path) = self.db.query_row("SELECT path FROM media WHERE chat_id = ?1 AND message_id = ?2", [FAVORITES, key], |r| r.get::<_, String>(0)) {
            let _ = std::fs::remove_file(path);
        }
        let _ = self.db.execute("DELETE FROM media WHERE chat_id = ?1 AND message_id = ?2", [FAVORITES, key]);
    }

    /// Favourite stickers, newest first.
    pub fn favorites(&self) -> Vec<crate::protocol::StickerDto> {
        self.db
            .prepare(
                "SELECT f.key, x.width, x.height, x.path FROM favorite_stickers f
                 JOIN media x ON x.chat_id = ?1 AND x.message_id = f.key
                 ORDER BY f.ts DESC, f.rowid DESC",
            )
            .and_then(|mut stmt| {
                stmt.query_map([FAVORITES], |r| {
                    let path: String = r.get(3)?;
                    Ok(crate::protocol::StickerDto {
                        chat_id: FAVORITES.into(),
                        message_id: r.get(0)?,
                        width: r.get(1)?,
                        height: r.get(2)?,
                        path: (!path.is_empty() && std::path::Path::new(&path).exists()).then_some(path),
                        thumb: None,
                    })
                })?
                .collect()
            })
            .unwrap_or_default()
    }

    /// The plaintext hash, for attachments that arrived without one (favourite stickers).
    pub fn set_file_sha256(&self, chat_id: &str, message_id: &str, hash: &[u8]) {
        let _ = self.db.execute(
            "UPDATE media SET file_sha256 = ?3 WHERE chat_id = ?1 AND message_id = ?2",
            params![chat_id, message_id, hash],
        );
    }

    /// Stickers (`kind` "sticker") or GIFs ("gif") from every chat, newest first, each file once.
    pub fn recent_media(&self, kind: &str, limit: usize) -> Vec<crate::protocol::StickerDto> {
        let rows: Vec<(String, String, u32, u32, String, Vec<u8>)> = self
            .db
            .prepare(
                "SELECT m.chat_id, m.id, x.width, x.height, x.path, x.file_sha256
                 FROM messages m JOIN media x ON x.chat_id = m.chat_id AND x.message_id = m.id
                 WHERE m.kind = ?1 AND x.direct_path != ''
                 ORDER BY m.ts DESC LIMIT 600",
            )
            .and_then(|mut stmt| stmt.query_map([kind], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?)))?.collect())
            .unwrap_or_default();
        let mut seen = std::collections::HashSet::new();
        rows.into_iter()
            .filter(|row| seen.insert(row.5.clone()))
            .take(limit)
            .map(|(chat_id, message_id, width, height, path, _)| {
                let thumb = if kind == "gif" { self.extra(&chat_id, &message_id).0 } else { None };
                crate::protocol::StickerDto {
                    path: (!path.is_empty() && std::path::Path::new(&path).exists()).then_some(path),
                    thumb,
                    chat_id,
                    message_id,
                    width,
                    height,
                }
            })
            .collect()
    }

    // ───────────── Polls ─────────────

    pub fn set_poll(&self, chat_id: &str, message_id: &str, secret: &[u8], creator: &str) {
        let _ = self.db.execute(
            "INSERT OR IGNORE INTO polls(chat_id, message_id, secret, creator) VALUES(?1, ?2, ?3, ?4)",
            params![chat_id, message_id, secret, creator],
        );
    }

    /// (secret, creator, option names) of a poll.
    pub fn poll(&self, chat_id: &str, message_id: &str) -> Option<(Vec<u8>, String, Vec<String>)> {
        let (secret, creator): (Vec<u8>, String) = self
            .db
            .query_row(
                "SELECT secret, creator FROM polls WHERE chat_id = ?1 AND message_id = ?2",
                [chat_id, message_id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()
            .ok()
            .flatten()?;
        Some((secret, creator, self.poll_options(chat_id, message_id)))
    }

    pub fn poll_options(&self, chat_id: &str, message_id: &str) -> Vec<String> {
        self.db
            .query_row("SELECT data FROM extras WHERE chat_id = ?1 AND message_id = ?2", [chat_id, message_id], |r| r.get::<_, String>(0))
            .ok()
            .and_then(|d| serde_json::from_str::<serde_json::Value>(&d).ok())
            .and_then(|v| v.get("options").and_then(|o| o.as_array()).map(|a| a.iter().filter_map(|x| x.as_str().map(str::to_string)).collect()))
            .unwrap_or_default()
    }

    /// A voter's choice; ignored when an equal-or-newer one is stored. True when it changed.
    pub fn set_vote(&self, chat_id: &str, message_id: &str, voter: &str, options: &[String], ts: i64) -> bool {
        self.db
            .execute(
                "INSERT INTO poll_votes(chat_id, message_id, voter, options, ts) VALUES(?1, ?2, ?3, ?4, ?5)
                 ON CONFLICT(chat_id, message_id, voter) DO UPDATE SET options = excluded.options, ts = excluded.ts
                 WHERE excluded.ts >= poll_votes.ts AND excluded.options != poll_votes.options",
                params![chat_id, message_id, voter, serde_json::to_string(options).unwrap_or_default(), ts],
            )
            .unwrap_or(0)
            > 0
    }

    /// Per option: how many voted for it and who; plus your own choice.
    fn poll_results(&self, chat_id: &str, message_id: &str) -> (serde_json::Value, serde_json::Value) {
        let rows: Vec<(String, String)> = self
            .db
            .prepare("SELECT voter, options FROM poll_votes WHERE chat_id = ?1 AND message_id = ?2 ORDER BY ts")
            .and_then(|mut stmt| stmt.query_map([chat_id, message_id], |r| Ok((r.get(0)?, r.get(1)?)))?.collect())
            .unwrap_or_default();
        let mut votes: Vec<(String, Vec<String>)> = self.poll_options(chat_id, message_id).into_iter().map(|o| (o, Vec::new())).collect();
        let mut mine = Vec::new();
        for (voter, options) in rows {
            let chosen: Vec<String> = serde_json::from_str(&options).unwrap_or_default();
            let name = if voter == "me" || self.is_me(&voter) { "You".to_string() } else { self.person_name(&voter, "") };
            if voter == "me" || self.is_me(&voter) {
                mine = chosen.clone();
            }
            for option in chosen {
                if let Some((_, who)) = votes.iter_mut().find(|(o, _)| *o == option) {
                    who.push(name.clone());
                }
            }
        }
        let votes = votes.into_iter().map(|(name, voters)| serde_json::json!({ "name": name, "voters": voters })).collect();
        (serde_json::Value::Array(votes), serde_json::json!(mine))
    }

    // ───────────── Media ─────────────

    /// True when the attachment details are new (not already stored).
    pub fn insert_media(&self, chat_id: &str, message_id: &str, m: &Media) -> bool {
        self.db.execute(
            "INSERT OR IGNORE INTO media(chat_id, message_id, media_type, direct_path, media_key, file_sha256,
                                         file_enc_sha256, file_length, mimetype, width, height, seconds, waveform)
             VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)",
            params![
                chat_id, message_id, m.media_type, m.direct_path, m.media_key, m.file_sha256,
                m.file_enc_sha256, m.file_length as i64, m.mimetype, m.width, m.height, m.seconds,
                (!m.waveform.is_empty()).then_some(&m.waveform)
            ],
        )
        .unwrap_or(0)
            > 0
    }

    /// The phone re-uploaded an expired attachment: its new CDN path.
    pub fn set_direct_path(&self, chat_id: &str, message_id: &str, direct_path: &str) {
        let _ = self.db.execute(
            "UPDATE media SET direct_path = ?3 WHERE chat_id = ?1 AND message_id = ?2",
            params![chat_id, message_id, direct_path],
        );
    }

    /// The stored CDN reference and local path for an attachment.
    pub fn media(&self, chat_id: &str, message_id: &str) -> Option<(Media, String)> {
        self.db
            .query_row(
                "SELECT media_type, direct_path, media_key, file_sha256, file_enc_sha256, file_length, mimetype, path,
                        width, height, seconds, waveform
                 FROM media WHERE chat_id = ?1 AND message_id = ?2",
                [chat_id, message_id],
                |r| {
                    let media_type: String = r.get(0)?;
                    Ok((
                        Media {
                            media_type: match media_type.as_str() {
                                "image" => "image",
                                "video" => "video",
                                "audio" => "audio",
                                "sticker" => "sticker",
                                _ => "document",
                            },
                            direct_path: r.get(1)?,
                            media_key: r.get(2)?,
                            file_sha256: r.get(3)?,
                            file_enc_sha256: r.get(4)?,
                            file_length: r.get::<_, i64>(5)? as u64,
                            mimetype: r.get(6)?,
                            width: r.get(8)?,
                            height: r.get(9)?,
                            seconds: r.get(10)?,
                            waveform: r.get::<_, Option<Vec<u8>>>(11)?.unwrap_or_default(),
                        },
                        r.get(7)?,
                    ))
                },
            )
            .optional()
            .ok()
            .flatten()
    }

    pub fn set_media_path(&self, chat_id: &str, message_id: &str, path: &str) {
        let _ = self.db.execute(
            "UPDATE media SET path = ?3 WHERE chat_id = ?1 AND message_id = ?2",
            params![chat_id, message_id, path],
        );
    }

    /// The quote shown on top of a reply. Prefers our own copy of the quoted
    /// message (full text, sender) over what the quote carried.
    fn reply_dto(&self, chat_id: &str, message_id: &str) -> Option<ReplyDto> {
        let (quoted_id, sender, kind, text, file_name): (String, String, String, String, String) = self
            .db
            .query_row(
                "SELECT quoted_id, sender, kind, text, file_name FROM quotes WHERE chat_id = ?1 AND message_id = ?2",
                [chat_id, message_id],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)),
            )
            .optional()
            .ok()
            .flatten()?;
        let (from_me, sender, push, kind, text, file_name) = match self.message(chat_id, &quoted_id) {
            Some(m) => (m.from_me, m.sender, m.push_name, m.kind, m.text, m.file_name),
            None => {
                let bare = bare_jid(&sender);
                (self.me.contains(&bare), self.canonical(&bare), String::new(), kind, text, file_name)
            }
        };
        let sender_name = if from_me {
            "You".to_string()
        } else if sender.is_empty() {
            self.person_name(chat_id, "")
        } else {
            self.person_name(&sender, &push)
        };
        let thumb = matches!(kind.as_str(), "image" | "video" | "gif" | "sticker" | "document" | "location")
            .then(|| self.extra(chat_id, &quoted_id).0)
            .flatten();
        Some(ReplyDto { id: quoted_id, from_me, sender_name, preview: preview(&kind, &text, &file_name), kind, thumb })
    }

    pub fn media_dto(&self, chat_id: &str, message_id: &str) -> Option<MediaDto> {
        self.db
            .query_row(
                "SELECT mimetype, width, height, seconds, waveform, path FROM media WHERE chat_id = ?1 AND message_id = ?2",
                [chat_id, message_id],
                |r| {
                    let path: String = r.get(5)?;
                    Ok(MediaDto {
                        mime: r.get(0)?,
                        width: r.get(1)?,
                        height: r.get(2)?,
                        seconds: r.get(3)?,
                        waveform: r.get::<_, Option<Vec<u8>>>(4)?.unwrap_or_default(),
                        // A cleared cache folder shouldn't point at missing files.
                        path: (!path.is_empty() && std::path::Path::new(&path).exists()).then_some(path),
                    })
                },
            )
            .optional()
            .ok()
            .flatten()
    }

    /// Best available name: your address book, then the phone number ("+44 20 7946 0123"),
    /// then the name they gave themselves (only when WhatsApp hides the number).
    /// One person's receipt: kept when it moves forward (delivered, then read).
    pub fn set_receipt(&self, chat_id: &str, message_id: &str, user: &str, status: u8, ts: i64) {
        let _ = self.db.execute(
            "INSERT INTO receipts(chat_id, message_id, user, status, ts, delivered_ts)
             VALUES(?1, ?2, ?3, ?4, ?5, CASE WHEN ?4 = 2 THEN ?5 ELSE 0 END)
             ON CONFLICT(chat_id, message_id, user) DO UPDATE SET
                status = MAX(receipts.status, excluded.status),
                ts = CASE WHEN excluded.status > receipts.status THEN excluded.ts ELSE receipts.ts END,
                delivered_ts = CASE WHEN excluded.status = 2 AND receipts.delivered_ts = 0 THEN excluded.ts ELSE receipts.delivered_ts END",
            params![chat_id, message_id, user, status, ts],
        );
    }

    /// Everyone who got or read a message: (person, name, status, when).
    pub fn receipts(&self, chat_id: &str, message_id: &str) -> Vec<crate::protocol::ReceiptDto> {
        let rows: Vec<(String, u8, i64, i64)> = self
            .db
            .prepare("SELECT user, status, ts, delivered_ts FROM receipts WHERE chat_id = ?1 AND message_id = ?2 ORDER BY ts DESC")
            .and_then(|mut stmt| stmt.query_map([chat_id, message_id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)))?.collect())
            .unwrap_or_default();
        rows.into_iter()
            .map(|(user, status, ts, delivered_ts): (String, u8, i64, i64)| crate::protocol::ReceiptDto {
                delivered_ts,
                name: self.person_name(&user, ""),
                chat_id: self.canonical(&user),
                user,
                status,
                ts,
            })
            .collect()
    }

    /// "@442079460123" (how WhatsApp writes a mention) becomes "@Name", the name set between
    /// Unicode isolates (U+2068 U+2069) so the app can colour it. Unknown numbers stay.
    pub fn render_mentions(&self, text: &str) -> String {
        if !text.contains('@') {
            return text.to_string();
        }
        let chars: Vec<char> = text.chars().collect();
        let mut out = String::with_capacity(text.len());
        let mut i = 0;
        while i < chars.len() {
            let starts_word = i == 0 || !chars[i - 1].is_alphanumeric();
            if chars[i] == '@' && starts_word {
                let digits: String = chars[i + 1..].iter().take_while(|c| c.is_ascii_digit()).collect();
                if (5..=20).contains(&digits.len()) {
                    let name = [format!("{digits}@s.whatsapp.net"), format!("{digits}@lid")]
                        .iter()
                        .find_map(|jid| {
                            if self.is_me(jid) {
                                return Some("You".to_string());
                            }
                            let name = self.person_name(jid, "");
                            (name != pretty_jid(jid) && !name.chars().filter(char::is_ascii_digit).eq(digits.chars())).then_some(name)
                        });
                    if let Some(name) = name {
                        let jid = if self.is_me(&format!("{digits}@s.whatsapp.net")) || self.person_name(&format!("{digits}@s.whatsapp.net"), "") != pretty_jid(&format!("{digits}@s.whatsapp.net")) {
                            format!("{digits}@s.whatsapp.net")
                        } else {
                            format!("{digits}@lid")
                        };
                        let phone = self.phone_number(&jid).map(|p| p.split('@').next().unwrap_or("").to_string()).unwrap_or_default();
                        out.push('@');
                        out.push('\u{2068}');
                        out.push_str(&name);
                        out.push('\u{2063}');
                        out.push_str(&self.canonical(&jid));
                        out.push('\u{2063}');
                        out.push_str(&phone);
                        out.push('\u{2069}');
                        i += 1 + digits.chars().count();
                        continue;
                    }
                }
                // "@all": everyone in the group was mentioned.
                let word: String = chars[i + 1..].iter().take_while(|c| c.is_alphanumeric()).collect();
                if word.eq_ignore_ascii_case("all") {
                    out.push_str("@\u{2068}all\u{2069}");
                    i += 4;
                    continue;
                }
            }
            out.push(chars[i]);
            i += 1;
        }
        out
    }

    pub fn person_name(&self, jid: &str, fallback_push: &str) -> String {
        let found: Option<(String, String)> = self
            .db
            .query_row(
                "SELECT full_name, push_name FROM names
                 WHERE jid = ?1 OR jid IN (SELECT alt FROM aliases WHERE chat_id = ?1)
                 ORDER BY (full_name != '') DESC, (push_name != '') DESC LIMIT 1",
                [jid],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()
            .ok()
            .flatten();
        if let Some((full, _)) = found.as_ref().filter(|(full, _)| !full.is_empty()) {
            return full.clone();
        }
        if let Some(number) = self.phone_number(jid) {
            return format_phone(&number);
        }
        match found {
            Some((_, push)) if !push.is_empty() => push,
            _ if !fallback_push.is_empty() => fallback_push.to_string(),
            _ => pretty_jid(jid),
        }
    }

    /// The phone number behind a JID: its own user part, or for a LID the phone-number JID it maps to.
    fn phone_number(&self, jid: &str) -> Option<String> {
        let pn = if jid.ends_with("@s.whatsapp.net") {
            Some(jid.to_string())
        } else if jid.ends_with("@lid") {
            self.db
                .query_row(
                    "SELECT alt FROM aliases WHERE chat_id = ?1 AND alt LIKE '%@s.whatsapp.net'
                     UNION ALL
                     SELECT chat_id FROM aliases WHERE alt = ?1 AND chat_id LIKE '%@s.whatsapp.net'
                     UNION ALL
                     SELECT number || '@s.whatsapp.net' FROM numbers WHERE lid = ?1
                     LIMIT 1",
                    [jid],
                    |r| r.get::<_, String>(0),
                )
                .optional()
                .ok()
                .flatten()
        } else {
            None
        }?;
        let user = pn.split('@').next()?.split(':').next()?;
        user.chars().all(|c| c.is_ascii_digit()).then(|| user.to_string())
    }
}

pub fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// "123:4@s.whatsapp.net" (a device) -> "123@s.whatsapp.net".
pub fn bare_jid(jid: &str) -> String {
    match jid.split_once('@') {
        Some((user, server)) => format!("{}@{server}", user.split(':').next().unwrap_or(user)),
        None => jid.to_string(),
    }
}

/// "442079460123" -> "GB".
pub fn phone_region(digits: &str) -> Option<String> {
    phone_parts(digits).map(|p| p.region).filter(|r| r.len() == 2)
}

/// "442079460123" -> GB / +44 / "20 7946 0123".
fn phone_parts(digits: &str) -> Option<PhoneDto> {
    let number = phonenumber::parse(None, format!("+{digits}")).ok()?;
    let code = format!("+{}", number.country().code());
    let region = number.country().id().map(|id| format!("{id:?}")).unwrap_or_default();
    let full = format_phone(digits);
    let national = full.strip_prefix(&code).map(|s| s.trim().to_string()).unwrap_or(full);
    Some(PhoneDto { region, code, national })
}

/// "442079460123" -> "+44 20 7946 0123" (international format for the number's country).
pub fn format_phone(digits: &str) -> String {
    let plus = format!("+{digits}");
    match phonenumber::parse(None, &plus) {
        Ok(number) => number.format().mode(phonenumber::Mode::International).to_string(),
        Err(_) => plus,
    }
}

/// First name for "Maya: hello" previews; phone numbers stay whole.
fn short_name(name: &str) -> String {
    if name.starts_with('+') {
        return name.to_string();
    }
    name.split_whitespace().next().unwrap_or(name).to_string()
}

/// "+15551234567" for phone-number JIDs, the bare id otherwise.
fn pretty_jid(jid: &str) -> String {
    let (user, server) = jid.split_once('@').unwrap_or((jid, ""));
    if server == "s.whatsapp.net" { format!("+{user}") } else { user.to_string() }
}

/// Chat-list preview line for a message.
pub fn preview(kind: &str, text: &str, file_name: &str) -> String {
    let label = match kind {
        "text" => return text.to_string(),
        "image" => "Photo",
        "video" => "Video",
        "gif" => "GIF",
        "voice" => "Voice message",
        "audio" => "Audio",
        "document" => return if file_name.is_empty() { "Document".into() } else { file_name.into() },
        "sticker" => "Sticker",
        "location" => "Location",
        "contact" => "Contact",
        "poll" => return format!("Poll: {text}"),
        "deleted" => return "This message was deleted".into(),
        "system" => return text.to_string(),
        "viewonce" => return format!("View once {text}"),
        "" => "",
        _ => "Message",
    };
    if text.is_empty() { label.to_string() } else { text.to_string() }
}

/// Standard base64 (the UI decodes previews with Convert.FromBase64String).
/// Mentions as plain "@Name" (chat list previews): drops the markers and who they point at.
fn plain_mentions(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut hidden = false;
    for c in text.chars() {
        match c {
            '\u{2068}' => {}
            '\u{2063}' => hidden = true,
            '\u{2069}' => hidden = false,
            _ if hidden => {}
            _ => out.push(c),
        }
    }
    out
}

fn base64(data: &[u8]) -> String {
    const ABC: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = (u32::from(b[0]) << 16) | (u32::from(b[1]) << 8) | u32::from(b[2]);
        for i in 0..4 {
            if i <= chunk.len() {
                out.push(ABC[(n >> (18 - 6 * i) & 63) as usize] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}
