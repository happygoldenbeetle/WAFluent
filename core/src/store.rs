//! Local chat/message store. WhatsApp only sends history once (right after
//! linking), so everything shown in the UI is persisted here.

use rusqlite::{Connection, OptionalExtension, params};

use crate::protocol::{ChatDto, MessageDto};

pub struct Store {
    db: Connection,
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
    pub pinned: bool,
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
-- A chat can be addressed by phone-number JID or by LID; both map to one chat id.
CREATE TABLE IF NOT EXISTS aliases(
    alt     TEXT PRIMARY KEY,
    chat_id TEXT NOT NULL
);
";

const CHAT_SELECT: &str = "
SELECT c.id, c.name, c.is_group, c.unread, c.pinned, c.archived, c.mute_end, c.last_ts,
       m.kind, m.text, m.file_name, m.from_me, m.status, m.sender, m.push_name
FROM chats c
LEFT JOIN messages m ON m.rowid = (
    SELECT rowid FROM messages WHERE chat_id = c.id ORDER BY ts DESC, rowid DESC LIMIT 1)
";

impl Store {
    pub fn open(path: &std::path::Path) -> rusqlite::Result<Self> {
        let db = Connection::open(path)?;
        db.pragma_update(None, "journal_mode", "WAL")?;
        db.pragma_update(None, "synchronous", "NORMAL")?;
        db.execute_batch(SCHEMA)?;
        Ok(Self { db })
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
                pinned   = excluded.pinned,
                archived = excluded.archived,
                mute_end = excluded.mute_end",
            params![meta.id, meta.name, meta.is_group, meta.unread, meta.pinned, meta.archived, meta.mute_end],
        );
    }

    pub fn ensure_chat(&self, id: &str, is_group: bool) {
        let _ = self.db.execute(
            "INSERT OR IGNORE INTO chats(id, is_group) VALUES(?1, ?2)",
            params![id, is_group],
        );
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
        let _ = self.db.execute_batch("DELETE FROM messages; DELETE FROM chats; DELETE FROM names; DELETE FROM aliases;");
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
                r.get::<_, bool>(4)?,
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
            ))
        });
        let Ok(rows) = rows else { return Vec::new() };
        rows.flatten()
            .map(|(id, name, is_group, unread, pinned, archived, mute_end, last_ts, kind, text, file_name, from_me, status, sender, push_name)| {
                let kind = kind.unwrap_or_default();
                let from_me = from_me.unwrap_or(false);
                let last_sender = match (&sender, is_group && !from_me) {
                    (Some(s), true) => Some(first_name(&self.person_name(s, push_name.as_deref().unwrap_or("")))),
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
                    pinned,
                    archived,
                    last_ts,
                    last_sender,
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
        let mut out: Vec<MessageDto> = rows.flatten().map(|m| self.to_dto(m)).collect();
        out.reverse();
        out
    }

    pub fn to_dto(&self, m: StoredMessage) -> MessageDto {
        let sender_name = if m.from_me { String::new() } else { self.person_name(&m.sender, &m.push_name) };
        MessageDto {
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

    /// Best available name: address book, then their push name, then the number.
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
        match found {
            Some((full, _)) if !full.is_empty() => full,
            Some((_, push)) if !push.is_empty() => push,
            _ if !fallback_push.is_empty() => fallback_push.to_string(),
            _ => pretty_jid(jid),
        }
    }
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn first_name(name: &str) -> String {
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
        "" => "",
        _ => "Message",
    };
    if text.is_empty() { label.to_string() } else { text.to_string() }
}
