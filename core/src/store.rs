//! Local chat/message store. WhatsApp only sends history once (right after
//! linking), so everything shown in the UI is persisted here.

use rusqlite::{Connection, OptionalExtension, params};

use crate::extract::{Media, Quote};
use crate::protocol::{ChatDto, MediaDto, MessageDto, PinnedDto, ReplyDto, StarredDto};

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
-- Phone numbers behind LIDs, for showing +92 333 1234567 (display only).
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
";

const CHAT_SELECT: &str = "
SELECT c.id, c.name, c.is_group, c.unread, c.pinned, c.archived, c.mute_end, c.last_ts,
       m.kind, m.text, m.file_name, m.from_me, m.status, m.sender, m.push_name,
       a.path, c.blocked, c.pinned_msg
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
        ] {
            let _ = db.execute(sql, []);
        }
        Ok(Self { db, me: Vec::new() })
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

    pub fn set_number(&self, lid: &str, number: &str) {
        let _ = self.db.execute(
            "INSERT INTO numbers(lid, number) VALUES(?1, ?2) ON CONFLICT(lid) DO UPDATE SET number = excluded.number",
            params![lid, number],
        );
    }

    // ───────────── Chat / message actions ─────────────

    pub fn set_blocked(&self, chat_id: &str, blocked: bool) {
        let _ = self.db.execute("UPDATE chats SET blocked = ?2 WHERE id = ?1", params![chat_id, blocked]);
    }

    /// The server's blocklist replaces ours.
    pub fn set_blocklist(&self, chat_ids: &[String]) {
        let _ = self.db.execute("UPDATE chats SET blocked = 0", []);
        for id in chat_ids {
            self.set_blocked(id, true);
        }
    }

    pub fn set_pinned_message(&self, chat_id: &str, message_id: &str) {
        let _ = self.db.execute("UPDATE chats SET pinned_msg = ?2 WHERE id = ?1", params![chat_id, message_id]);
    }

    pub fn set_unread(&self, chat_id: &str, unread: u32) {
        let _ = self.db.execute("UPDATE chats SET unread = ?2 WHERE id = ?1", params![chat_id, unread]);
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
        self.db
            .execute(
                "UPDATE messages SET kind = 'deleted', text = '', file_name = '' WHERE chat_id = ?1 AND id = ?2",
                [chat_id, message_id],
            )
            .unwrap_or(0)
            > 0
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
        for table in ["media", "quotes", "reactions"] {
            let _ = self.db.execute(&format!("DELETE FROM {table} WHERE chat_id = ?1 AND message_id = ?2"), [chat_id, message_id]);
        }
        self.db.execute("DELETE FROM messages WHERE chat_id = ?1 AND id = ?2", [chat_id, message_id]).unwrap_or(0) > 0
    }

    /// Empties a chat but keeps it in the list.
    pub fn clear_messages(&self, chat_id: &str) {
        for table in ["media", "quotes", "reactions"] {
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
        let _ = self.db.execute("UPDATE chats SET unread = unread + 1 WHERE id = ?1", [chat_id]);
    }

    pub fn mark_read(&self, chat_id: &str) {
        let _ = self.db.execute("UPDATE chats SET unread = 0 WHERE id = ?1", [chat_id]);
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
            "DELETE FROM messages; DELETE FROM chats; DELETE FROM names; DELETE FROM aliases; DELETE FROM avatars; DELETE FROM media; DELETE FROM quotes; DELETE FROM reactions; DELETE FROM numbers;",
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
        let sql = format!("{CHAT_SELECT} WHERE c.last_ts > 0 ORDER BY c.pinned DESC, c.last_ts DESC");
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
            ))
        });
        let Ok(rows) = rows else { return Vec::new() };
        rows.flatten()
            .map(|(id, name, is_group, unread, pinned, archived, mute_end, last_ts, kind, text, file_name, from_me, status, sender, push_name, avatar, blocked, pinned_msg)| {
                let kind = kind.unwrap_or_default();
                let from_me = from_me.unwrap_or(false);
                let saved = !is_group && self.has_full_name(&id);
                let pinned_message = (!pinned_msg.is_empty()).then(|| PinnedDto {
                    preview: self.message(&id, &pinned_msg).map(|m| preview(&m.kind, &m.text, &m.file_name)).unwrap_or_default(),
                    id: pinned_msg.clone(),
                });
                let last_sender = match (&sender, is_group && !from_me) {
                    (Some(s), true) => Some(short_name(&self.person_name(s, push_name.as_deref().unwrap_or("")))),
                    _ => None,
                };
                ChatDto {
                    name: if name.is_empty() { self.person_name(&id, "") } else { name },
                    preview: preview(&kind, text.as_deref().unwrap_or(""), file_name.as_deref().unwrap_or("")),
                    preview_kind: kind,
                    muted: mute_end == -1 || mute_end > now,
                    last_from_me: from_me,
                    last_status: status.unwrap_or(0),
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
                    pinned_message,
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
        let (starred, edited): (bool, bool) = self
            .db
            .query_row("SELECT starred, edited FROM messages WHERE chat_id = ?1 AND id = ?2", [chat_id, &m.id], |r| Ok((r.get(0)?, r.get(1)?)))
            .unwrap_or((false, false));
        MessageDto {
            starred,
            edited,
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
            text: m.text,
            file_name: m.file_name,
            status: m.status,
        }
    }

    // ───────────── Media ─────────────

    pub fn insert_media(&self, chat_id: &str, message_id: &str, m: &Media) {
        let _ = self.db.execute(
            "INSERT OR IGNORE INTO media(chat_id, message_id, media_type, direct_path, media_key, file_sha256,
                                         file_enc_sha256, file_length, mimetype, width, height, seconds, waveform)
             VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)",
            params![
                chat_id, message_id, m.media_type, m.direct_path, m.media_key, m.file_sha256,
                m.file_enc_sha256, m.file_length as i64, m.mimetype, m.width, m.height, m.seconds,
                (!m.waveform.is_empty()).then_some(&m.waveform)
            ],
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
        Some(ReplyDto { id: quoted_id, from_me, sender_name, preview: preview(&kind, &text, &file_name), kind })
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

    /// Best available name: your address book, then the phone number ("+92 333 1234567"),
    /// then the name they gave themselves (only when WhatsApp hides the number).
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

/// "923331234567" -> "+92 333 1234567" (international format for the number's country).
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
        "" => "",
        _ => "Message",
    };
    if text.is_empty() { label.to_string() } else { text.to_string() }
}
