//! JSON-lines protocol between wafluent-core (this process) and the WinUI app.
//! One JSON object per line: events go to stdout, commands arrive on stdin.
//! Logs never go to stdout — they are on stderr.

use serde::{Deserialize, Serialize};

/// Core -> UI.
#[derive(Serialize, Debug)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Event {
    /// You: your WhatsApp name and number (for "You" in the contact picker).
    Me { name: String, phone: String },
    /// The sticker panel's contents: recent stickers and GIFs.
    Stickers { favorites: Vec<StickerDto>, stickers: Vec<StickerDto>, gifs: Vec<StickerDto> },
    /// A favourite sticker was added or removed on the phone (the open panel reloads).
    FavoritesChanged,
    /// Someone in the chat is typing ("typing"), recording a voice note ("recording") or
    /// stopped ("paused"). `who` names them in groups.
    Typing { chat_id: String, who: String, state: &'static str },
    /// A contact came online or left; `last_seen` (Unix seconds) when they share it.
    Presence { chat_id: String, online: bool, last_seen: Option<i64> },
    /// The chat asked for with `openNumber` (already sent as `chat`): show it.
    Opened { chat_id: String },
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
    /// Search results; `oldest_ts`: the oldest message this PC has for the chat (older ones
    /// are only on the phone).
    SearchResults { chat_id: String, query: String, results: Vec<MessageDto>, oldest_ts: Option<i64> },
    /// Receipts for one of your messages (Message info).
    MessageInfo { chat_id: String, message_id: String, receipts: Vec<ReceiptDto> },
    /// The message a date points at (none: nothing on or after it).
    FoundMessage { chat_id: String, message_id: Option<String>, ts: i64 },
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
    /// The name they gave themselves (shown as "~name" under their number or contact name).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub push_name: Option<String>,
    /// 1:1 chats: their number split for the New contact form.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub phone: Option<PhoneDto>,
    /// The message pinned in this chat, if any: its id and a one-line preview.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pinned_message: Option<PinnedDto>,
}

#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct PhoneDto {
    /// ISO region ("PK").
    pub region: String,
    /// Calling code with "+" ("+92").
    pub code: String,
    /// The rest, spaced ("302 9328645").
    pub national: String,
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
    /// text | image | video | gif | voice | audio | document | sticker | location | contact | poll |
    /// viewonce | system | deleted
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
    /// The sender's small JPEG preview, base64 (pictures, videos, maps, link cards).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub thumb: Option<String>,
    /// Per-kind details: location {lat, lng, name, address, url, live}, contact {contacts:
    /// [{name, phones}]}, poll {options, multi}, text {link: {url, title, description}},
    /// video {note}, sticker {animated}, document {pages}.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub extra: Option<serde_json::Value>,
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

fn yes() -> bool {
    true
}

/// UI -> Core.
#[derive(Deserialize, Debug)]
#[serde(tag = "cmd", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Command {
    LoadMessages { chat_id: String, limit: Option<u32> },
    /// Messages before (`before_ts`, `before_id`): served from the local store,
    /// or requested from the phone (history sync on demand) when the store has none.
    LoadOlder { chat_id: String, before_ts: i64, before_id: String, limit: Option<u32> },
    /// Everything older than (before_ts, before_id) back to `until_ts`, plus a little before
    /// it: to show a search result or a date that isn't loaded yet. Answered by `olderMessages`.
    LoadOlderUntil { chat_id: String, before_ts: i64, before_id: String, until_ts: i64 },
    /// Who got and read one of your messages; answered by `messageInfo`.
    MessageInfo { chat_id: String, message_id: String },
    /// Messages in a chat containing `query` (text, captions, file names), newest first.
    SearchMessages { chat_id: String, query: String },
    /// The first message on or after `ts` (the search calendar); answered by `foundMessage`.
    FindMessageAt { chat_id: String, ts: i64 },
    /// Download (or return the cached copy of) a message's attachment. `force` also
    /// retries one the phone said it no longer has.
    DownloadMedia {
        chat_id: String,
        message_id: String,
        #[serde(default)]
        force: bool,
    },
    /// Messages stored before media support have no download details: re-request the 50
    /// messages before `before_id` (default: the newest) from the phone. Each message that
    /// gains its details comes back as `messageUpdated`.
    BackfillMedia { chat_id: String, before_id: Option<String> },
    /// Vote in a poll: the option names you now choose (none = take your vote back).
    VotePoll { chat_id: String, message_id: String, options: Vec<String> },
    /// Open (or start) the 1:1 chat with a phone number, e.g. from a shared contact card.
    /// Answers `opened`, or a `notice` when the number isn't on WhatsApp.
    OpenNumber { phone: String },
    /// Your recent stickers and GIFs (from every chat, newest first, each once); answered by `stickers`.
    LoadStickers,
    /// Send a sticker or GIF you've seen before (its stored message) to a chat, not as forwarded.
    SendStored { chat_id: String, message_id: String, to: String },
    /// Upload an MP4 from GIF search and send it as a GIF (GIPHY attribution). `thumb`: a
    /// small JPEG preview file.
    SendGif { to: String, path: String, width: u32, height: u32, thumb: Option<String> },
    /// Upload a file and send it: `kind` "image", "video" or "document". `thumb`: a small
    /// JPEG preview file (pictures and videos).
    SendMedia {
        chat_id: String,
        path: String,
        kind: String,
        caption: String,
        mime: String,
        width: u32,
        height: u32,
        seconds: u32,
        thumb: Option<String>,
        /// The uploading bubble's id: answered by `sent` (or `sendFailed`) with it.
        temp_id: String,
    },
    /// Stop an upload that hasn't been sent yet.
    CancelSend { temp_id: String },
    /// Share contact cards (one, or several in one message).
    SendContacts { chat_id: String, contacts: Vec<ContactCard> },
    /// Start a poll. `multiple`: people may pick several options; `hide_voters`: votes are
    /// counted without names; `end_time`: voting closes then (Unix seconds).
    SendPoll { chat_id: String, question: String, options: Vec<String>, multiple: bool, hide_voters: bool, end_time: Option<i64> },
    /// You're here (window focused) or away: WhatsApp shows you online, and only sends
    /// typing and online updates to clients that are available.
    SetPresence { available: bool },
    /// Watch a 1:1 chat's online / last seen / typing (sent when you open it).
    WatchPresence { chat_id: String },
    /// Your own typing state in a chat: "typing", "recording" or "paused".
    SendTyping { chat_id: String, state: String },
    /// Send a text message, optionally quoting `reply_to` (a message id in the same chat).
    /// `temp_id` names the UI's pending bubble in the `sent` / `sendFailed` answer.
    SendText { chat_id: String, text: String, reply_to: Option<String>, temp_id: String },
    /// Chat menu: archive | unarchive | mute (until_ms, or always) | unmute | markRead | markUnread |
    /// clear | delete | block | unblock. Synced with the phone.
    ChatAction { chat_id: String, action: String, until_ms: Option<i64> },
    /// Save a 1:1 contact to your phone's address book.
    SaveContact {
        chat_id: String,
        first_name: String,
        last_name: String,
        /// Also add to the phone's address book (otherwise a WhatsApp-only contact).
        #[serde(default = "yes")]
        sync_to_phone: bool,
    },
    /// Report a contact to WhatsApp (Contact info → Report).
    ReportContact { chat_id: String },
    /// Write the chat as text ("28/09/2026, 21:15 - Name: text") to `path`.
    ExportChat { chat_id: String, path: String, utc_offset_minutes: i32 },
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

/// A sticker or GIF for the panel: the message it came in (to download and resend it).
#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct StickerDto {
    pub chat_id: String,
    pub message_id: String,
    pub width: u32,
    pub height: u32,
    /// Downloaded file, when it is.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    /// The sender's small preview (GIFs), base64.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub thumb: Option<String>,
}

/// A contact to share: a name and a number.
#[derive(Deserialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ContactCard {
    pub name: String,
    pub phone: String,
}

/// One person's receipt for a message: delivered (2) or read/played (3), and when.
#[derive(Serialize, Debug, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ReceiptDto {
    pub user: String,
    /// Their 1:1 chat, when there is one (for the picture).
    pub chat_id: String,
    pub name: String,
    pub status: u8,
    pub ts: i64,
}
