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
    /// Reply to `loadOlder`: messages older than the anchor, oldest first.
    /// `complete` = the phone has nothing older; stop asking.
    OlderMessages { chat_id: String, messages: Vec<MessageDto>, complete: bool },
    /// A profile picture was downloaded, changed or removed (`path` = None).
    /// `chat_id` is "self" for your own picture.
    Avatar { chat_id: String, path: Option<String> },
    /// Reply to `downloadMedia`: the decrypted file.
    Media { chat_id: String, message_id: String, path: String },
    /// `downloadMedia` failed (e.g. the phone deleted it from WhatsApp's servers).
    MediaFailed { chat_id: String, message_id: String, reason: String },
    /// Reply to `sendText`: the message as stored, with its real id.
    Sent { chat_id: String, temp_id: String, message: MessageDto },
    /// `sendText` failed; the UI marks its pending bubble.
    SendFailed { chat_id: String, temp_id: String, reason: String },
    /// A message's reactions changed (someone reacted, changed or removed theirs).
    Reactions { chat_id: String, message_id: String, reactions: Vec<String>, my_reaction: Option<String> },
    /// A message changed in place (deleted for everyone, edited, starred).
    MessageUpdated { chat_id: String, message: MessageDto },
    /// A message is gone from this device (deleted for me).
    MessageRemoved { chat_id: String, message_id: String },
    /// The chat was deleted.
    ChatRemoved { chat_id: String },
    /// Reply to `loadStarred`, newest first.
    Starred { items: Vec<StarredDto> },
    /// Something you asked for worked (`ok`) or didn't; `text` is for a toast.
    Notice { ok: bool, text: String },
    /// Your messages were delivered to / read by the other side. Status: 2 delivered, 3 read.
    Receipt { chat_id: String, message_ids: Vec<String>, status: u8 },
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ChatDto {
    pub id: String,
    pub name: String,
    pub is_group: bool,
    pub unread: u32,
    pub pinned: bool,
    /// When it was pinned (Unix seconds, 0 = not pinned): newest pin first, like the phone.
    pub pinned_at: i64,
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
    /// Cached profile picture (JPEG path), when there is one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub avatar: Option<String>,
    /// You blocked this contact.
    pub blocked: bool,
    /// The contact is in your address book (1:1 chats).
    pub saved: bool,
    /// The message pinned in this chat, if any: its id and a one-line preview.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pinned_message: Option<PinnedDto>,
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct PinnedDto {
    pub id: String,
    pub preview: String,
}

/// One starred message, for the Starred view.
#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct StarredDto {
    pub chat_id: String,
    pub chat_name: String,
    pub message: MessageDto,
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
    /// Attachment details, when the message has a downloadable one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub media: Option<MediaDto>,
    /// The message this one replies to.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reply: Option<ReplyDto>,
    /// One emoji per person who reacted, oldest first.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub reactions: Vec<String>,
    /// Your own reaction, if any (it is also in `reactions`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub my_reaction: Option<String>,
    pub starred: bool,
    /// The sender changed the text after sending.
    pub edited: bool,
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ReplyDto {
    pub id: String,
    pub from_me: bool,
    pub sender_name: String,
    pub kind: String,
    /// One-line summary ("Photo", the text, the file name...).
    pub preview: String,
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct MediaDto {
    pub mime: String,
    pub width: u32,
    pub height: u32,
    pub seconds: u32,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub waveform: Vec<u8>,
    /// Decrypted file on disk, once downloaded.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

/// UI -> Core.
#[derive(Deserialize, Debug)]
#[serde(tag = "cmd", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Command {
    LoadMessages { chat_id: String, limit: Option<u32> },
    /// Messages before (`before_ts`, `before_id`): served from the local store,
    /// or requested from the phone (history sync on demand) when the store has none.
    LoadOlder { chat_id: String, before_ts: i64, before_id: String, limit: Option<u32> },
    /// Download (or return the cached copy of) a message's attachment.
    DownloadMedia { chat_id: String, message_id: String },
    /// Messages stored before media support have no download details; re-request the
    /// chat's recent history from the phone to fill them in, then resend `messages`.
    BackfillMedia { chat_id: String },
    /// Send a text message, optionally quoting `reply_to` (a message id in the same chat).
    /// `temp_id` names the UI's pending bubble in the `sent` / `sendFailed` answer.
    SendText { chat_id: String, text: String, reply_to: Option<String>, temp_id: String },
    /// Chat menu: archive | unarchive | mute (until_ms, or always) | unmute | markRead | markUnread |
    /// clear | delete | block | unblock. Synced with the phone.
    ChatAction { chat_id: String, action: String, until_ms: Option<i64> },
    /// Save a 1:1 contact to your phone's address book.
    SaveContact { chat_id: String, first_name: String, last_name: String },
    /// Send copies of a message to other chats.
    Forward { chat_id: String, message_id: String, to: Vec<String> },
    PinMessage { chat_id: String, message_id: String, pin: bool },
    StarMessage { chat_id: String, message_id: String, star: bool },
    /// `for_everyone` = revoke (your own messages); otherwise delete for me.
    DeleteMessage { chat_id: String, message_id: String, for_everyone: bool },
    /// Report a message to WhatsApp as spam.
    Report { chat_id: String, message_id: String },
    LoadStarred,
    /// Pin or unpin a chat. Synced with the phone (WhatsApp allows three pins).
    SetPinned { chat_id: String, pinned: bool },
    /// React to a message; an empty `emoji` removes your reaction.
    React { chat_id: String, message_id: String, emoji: String },
    /// Local only for now: clears the unread badge, sends no read receipts.
    MarkRead { chat_id: String },
    Logout,
}
