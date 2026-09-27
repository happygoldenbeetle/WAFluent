//! JSON-lines protocol between wafluent-core (this process) and the WinUI app.
//! One JSON object per line: events go to stdout, commands arrive on stdin.
//! Logs never go to stdout — they are on stderr.

use serde::{Deserialize, Serialize};

/// Core -> UI.
#[derive(Serialize, Debug)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Event {
    /// Connection lifecycle. `state`: starting | qr | connecting | syncing | connected | loggedOut | error
    Status {
        state: &'static str,
        #[serde(skip_serializing_if = "Option::is_none")]
        detail: Option<String>,
    },
    /// Pairing QR payload; render it as a QR code. Replaced every ~20 s until scanned.
    Qr { code: String, timeout_secs: u64 },
    /// Full chat list snapshot (sent on connect and after each history-sync chunk).
    Chats { chats: Vec<ChatDto> },
    /// One chat changed (new message, unread count, name...).
    Chat { chat: ChatDto },
    /// Reply to `loadMessages`: oldest first.
    Messages { chat_id: String, messages: Vec<MessageDto> },
    /// A live message for an existing or new chat.
    Message { chat_id: String, message: MessageDto },
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ChatDto {
    pub id: String,
    pub name: String,
    pub is_group: bool,
    pub unread: u32,
    pub pinned: bool,
    pub archived: bool,
    pub muted: bool,
    /// Unix seconds of the last message (0 if none).
    pub last_ts: i64,
    pub preview: String,
    pub preview_kind: String,
    pub last_from_me: bool,
    pub last_status: u8,
    /// For groups: who sent the last message.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_sender: Option<String>,
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct MessageDto {
    pub id: String,
    pub from_me: bool,
    pub sender: String,
    pub sender_name: String,
    /// Unix seconds.
    pub ts: i64,
    /// text | image | video | voice | audio | document | sticker | location | contact | poll | other
    pub kind: String,
    pub text: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub file_name: String,
    /// Outgoing delivery: 0 unknown, 1 sent, 2 delivered, 3 read.
    pub status: u8,
}

/// UI -> Core.
#[derive(Deserialize, Debug)]
#[serde(tag = "cmd", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Command {
    LoadMessages { chat_id: String, limit: Option<u32> },
    /// Local only for now: clears the unread badge, sends no read receipts.
    MarkRead { chat_id: String },
    Logout,
}
