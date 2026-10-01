//! Turns a WhatsApp protobuf message into what the chat UI shows.

use serde_json::{Value, json};
use whatsapp_rust::prelude::*;

pub struct Content {
    pub kind: &'static str,
    pub text: String,
    pub file_name: String,
    /// Everything needed to download the attachment later (the message itself isn't kept).
    pub media: Option<Media>,
    /// Small JPEG preview the sender embedded: pictures, videos, map snapshots, link cards.
    pub thumb: Vec<u8>,
    /// What a kind needs beyond text: the map pin, contact numbers, poll options, link card...
    pub extra: Option<Value>,
}

/// CDN reference + display hints for an attachment.
#[derive(Default)]
pub struct Media {
    /// image | video | audio | sticker | document
    pub media_type: &'static str,
    pub direct_path: String,
    pub media_key: Vec<u8>,
    pub file_sha256: Vec<u8>,
    pub file_enc_sha256: Vec<u8>,
    pub file_length: u64,
    pub mimetype: String,
    pub width: u32,
    pub height: u32,
    pub seconds: u32,
    /// Voice notes: 64 amplitude samples, 0-100.
    pub waveform: Vec<u8>,
}

impl Content {
    fn new(kind: &'static str, text: impl Into<String>) -> Self {
        Self { kind, text: text.into(), file_name: String::new(), media: None, thumb: Vec::new(), extra: None }
    }

    fn with_thumb(mut self, thumb: &Option<Vec<u8>>) -> Self {
        self.thumb = bytes(thumb);
        self
    }

    fn with_extra(mut self, extra: Value) -> Self {
        self.extra = Some(extra);
        self
    }

    fn with_media(mut self, media: Media) -> Self {
        // Without a CDN path there is nothing to download.
        if !media.direct_path.is_empty() && !media.media_key.is_empty() {
            self.media = Some(media);
        }
        self
    }
}

fn bytes(b: &Option<Vec<u8>>) -> Vec<u8> {
    b.clone().unwrap_or_default()
}

/// The message inside the wrappers: device-sent, disappearing, view-once, document with
/// caption, edited (all `get_base_message`), and the newer ones it doesn't know: album
/// items, lottie stickers, @group mentions, spoilers, messages to Meta AI.
pub fn base(message: &wa::Message) -> &wa::Message {
    let mut m = message.get_base_message();
    for _ in 0..4 {
        let inner = [
            &m.associated_child_message,
            &m.lottie_sticker_message,
            &m.group_mentioned_message,
            &m.spoiler_message,
            &m.bot_invoke_message,
            &m.bot_forwarded_message,
            &m.question_message,
            &m.question_reply_message,
            &m.limit_sharing_message,
        ]
        .into_iter()
        .find_map(|w| w.as_option().and_then(|w| w.message.as_option()));
        match inner {
            Some(next) => m = next.get_base_message(),
            None => break,
        }
    }
    m
}

/// `None` for messages with nothing to show (reactions, edits, revokes, key
/// distribution, ...). Those are protocol traffic, not chat bubbles.
pub fn content(message: &wa::Message) -> Option<Content> {
    let m = base(message);

    // View once: linked devices never get the file; WhatsApp says to open it on the phone.
    if message.is_view_once() {
        let what = if m.video_message.is_set() {
            "video"
        } else if m.audio_message.is_set() {
            "voice message"
        } else {
            "photo"
        };
        return Some(Content::new("viewonce", what));
    }

    if let Some(text) = m.conversation.as_deref().filter(|t| !t.is_empty()) {
        return Some(Content::new("text", text));
    }
    if let Some(ext) = m.extended_text_message.as_option() {
        if let Some(text) = ext.text.as_deref() {
            let mut content = Content::new("text", text);
            // Link preview card, when the sender's app made one.
            let url = ext.matched_text.clone().unwrap_or_default();
            let title = ext.title.clone().unwrap_or_default();
            if !url.is_empty() && (!title.is_empty() || ext.jpeg_thumbnail.is_some()) {
                content = content.with_thumb(&ext.jpeg_thumbnail).with_extra(json!({
                    "link": { "url": url, "title": title, "description": ext.description.clone().unwrap_or_default() }
                }));
            }
            return Some(content);
        }
    }
    if let Some(img) = m.image_message.as_option() {
        return Some(Content::new("image", img.caption.clone().unwrap_or_default()).with_thumb(&img.jpeg_thumbnail).with_media(Media {
            media_type: "image",
            direct_path: img.direct_path.clone().unwrap_or_default(),
            media_key: bytes(&img.media_key),
            file_sha256: bytes(&img.file_sha256),
            file_enc_sha256: bytes(&img.file_enc_sha256),
            file_length: img.file_length.unwrap_or(0),
            mimetype: img.mimetype.clone().unwrap_or_default(),
            width: img.width.unwrap_or(0),
            height: img.height.unwrap_or(0),
            ..Default::default()
        }));
    }
    if let Some(vid) = m.video_message.as_option().or_else(|| m.ptv_message.as_option()) {
        let kind = if vid.gif_playback == Some(true) { "gif" } else { "video" };
        let mut content = Content::new(kind, vid.caption.clone().unwrap_or_default()).with_thumb(&vid.jpeg_thumbnail);
        if m.ptv_message.is_set() {
            content = content.with_extra(json!({ "note": true }));   // round video message
        }
        return Some(content.with_media(Media {
            media_type: "video",
            direct_path: vid.direct_path.clone().unwrap_or_default(),
            media_key: bytes(&vid.media_key),
            file_sha256: bytes(&vid.file_sha256),
            file_enc_sha256: bytes(&vid.file_enc_sha256),
            file_length: vid.file_length.unwrap_or(0),
            mimetype: vid.mimetype.clone().unwrap_or_default(),
            width: vid.width.unwrap_or(0),
            height: vid.height.unwrap_or(0),
            seconds: vid.seconds.unwrap_or(0),
            ..Default::default()
        }));
    }
    if let Some(audio) = m.audio_message.as_option() {
        let kind = if audio.ptt == Some(true) { "voice" } else { "audio" };
        return Some(Content::new(kind, "").with_media(Media {
            media_type: "audio",
            direct_path: audio.direct_path.clone().unwrap_or_default(),
            media_key: bytes(&audio.media_key),
            file_sha256: bytes(&audio.file_sha256),
            file_enc_sha256: bytes(&audio.file_enc_sha256),
            file_length: audio.file_length.unwrap_or(0),
            mimetype: audio.mimetype.clone().unwrap_or_default(),
            seconds: audio.seconds.unwrap_or(0),
            waveform: bytes(&audio.waveform),
            ..Default::default()
        }));
    }
    if let Some(doc) = m.document_message.as_option() {
        let file_name = doc.file_name.clone().or_else(|| doc.title.clone()).unwrap_or_default();
        let mut content = Content::new("document", doc.caption.clone().unwrap_or_default()).with_thumb(&doc.jpeg_thumbnail).with_extra(json!({
            "pages": doc.page_count.unwrap_or(0),
        })).with_media(Media {
            media_type: "document",
            direct_path: doc.direct_path.clone().unwrap_or_default(),
            media_key: bytes(&doc.media_key),
            file_sha256: bytes(&doc.file_sha256),
            file_enc_sha256: bytes(&doc.file_enc_sha256),
            file_length: doc.file_length.unwrap_or(0),
            mimetype: doc.mimetype.clone().unwrap_or_default(),
            ..Default::default()
        });
        content.file_name = file_name;
        return Some(content);
    }
    if let Some(st) = m.sticker_message.as_option() {
        return Some(Content::new("sticker", "").with_extra(json!({ "animated": st.is_animated == Some(true) })).with_media(Media {
            media_type: "sticker",
            direct_path: st.direct_path.clone().unwrap_or_default(),
            media_key: bytes(&st.media_key),
            file_sha256: bytes(&st.file_sha256),
            file_enc_sha256: bytes(&st.file_enc_sha256),
            file_length: st.file_length.unwrap_or(0),
            mimetype: st.mimetype.clone().unwrap_or_default(),
            width: st.width.unwrap_or(0),
            height: st.height.unwrap_or(0),
            ..Default::default()
        }));
    }
    if let Some(loc) = m.location_message.as_option() {
        let label = loc.name.clone().or_else(|| loc.address.clone()).unwrap_or_default();
        return Some(Content::new("location", label).with_thumb(&loc.jpeg_thumbnail).with_extra(json!({
            "lat": loc.degrees_latitude.unwrap_or(0.0),
            "lng": loc.degrees_longitude.unwrap_or(0.0),
            "name": loc.name.clone().unwrap_or_default(),
            "address": loc.address.clone().unwrap_or_default(),
            "url": loc.url.clone().unwrap_or_default(),
        })));
    }
    if let Some(contact) = m.contact_message.as_option() {
        let name = contact.display_name.clone().unwrap_or_default();
        return Some(Content::new("contact", name).with_extra(json!({ "contacts": [card(contact)] })));
    }
    if let Some(poll) = m
        .poll_creation_message
        .as_option()
        .or_else(|| m.poll_creation_message_v2.as_option())
        .or_else(|| m.poll_creation_message_v3.as_option())
        .or_else(|| m.poll_creation_message_v5.as_option())
        .or_else(|| m.poll_creation_message_v6.as_option())
    {
        let options: Vec<String> = poll.options.iter().filter_map(|o| o.option_name.clone()).collect();
        let multi = poll.selectable_options_count.unwrap_or(0) != 1;
        return Some(Content::new("poll", poll.name.clone().unwrap_or_default()).with_extra(json!({ "options": options, "multi": multi })));
    }
    if let Some(inner) = m.poll_creation_message_v4.as_option().and_then(|w| w.message.as_option()) {
        return content(inner);
    }
    if let Some(live) = m.live_location_message.as_option() {
        let label = live.caption.clone().filter(|c| !c.is_empty()).unwrap_or_else(|| "Live location".into());
        return Some(Content::new("location", label).with_thumb(&live.jpeg_thumbnail).with_extra(json!({
            "lat": live.degrees_latitude.unwrap_or(0.0),
            "lng": live.degrees_longitude.unwrap_or(0.0),
            "name": "Live location",
            "address": live.caption.clone().unwrap_or_default(),
            "live": true,
        })));
    }
    if let Some(contacts) = m.contacts_array_message.as_option() {
        let label = contacts.display_name.clone().filter(|n| !n.is_empty()).unwrap_or_else(|| format!("{} contacts", contacts.contacts.len()));
        let cards: Vec<Value> = contacts.contacts.iter().map(card).collect();
        return Some(Content::new("contact", label).with_extra(json!({ "contacts": cards })));
    }
    if let Some(invite) = m.group_invite_message.as_option() {
        let name = invite.group_name.clone().unwrap_or_default();
        return Some(Content::new("text", format!("👥 Invitation to join the group \"{name}\"")));
    }
    if let Some(event) = m.event_message.as_option() {
        let name = event.name.clone().unwrap_or_default();
        let state = if event.is_canceled == Some(true) { " (cancelled)" } else { "" };
        return Some(Content::new("text", format!("📅 {name}{state}")));
    }
    if let Some(call) = m.call_log_messsage.as_option() {
        let what = if call.is_video == Some(true) { "Video call" } else { "Voice call" };
        return Some(Content::new("text", format!("📞 {what}")));
    }
    if let Some(pack) = m.sticker_pack_message.as_option() {
        return Some(Content::new("text", format!("💟 Sticker pack: {}", pack.name.clone().unwrap_or_default())));
    }
    // Business messages: their text, without the buttons.
    let business = m
        .buttons_message
        .as_option()
        .and_then(|b| b.content_text.clone())
        .or_else(|| m.interactive_message.as_option().and_then(|i| i.body.as_option()).and_then(|b| b.text.clone()))
        .or_else(|| m.interactive_response_message.as_option().and_then(|i| i.body.as_option()).and_then(|b| b.text.clone()))
        .or_else(|| m.list_message.as_option().and_then(|l| l.description.clone().or_else(|| l.title.clone())))
        .or_else(|| m.list_response_message.as_option().and_then(|l| l.title.clone()))
        .or_else(|| m.template_button_reply_message.as_option().and_then(|t| t.selected_display_text.clone()));
    if let Some(text) = business.filter(|t| !t.is_empty()) {
        return Some(Content::new("text", text));
    }

    // Reactions, protocol messages (revoke/edit/app-state keys), sender-key
    // distribution and the like have no bubble of their own.
    None
}

/// A notice in the middle of the chat ("Missed voice call", "Ali added Sara").
pub fn system(text: String) -> Content {
    Content::new("system", text)
}

/// A shared contact: its name and the phone numbers in its vCard.
fn card(contact: &wa::message::ContactMessage) -> Value {
    let vcard = contact.vcard.as_deref().unwrap_or("");
    let phones: Vec<String> = vcard
        .lines()
        .filter(|l| l.to_ascii_uppercase().contains("TEL"))
        .filter_map(|l| l.rsplit_once(':').map(|(_, n)| n.trim().to_string()))
        .filter(|n| !n.is_empty())
        .collect();
    json!({ "name": contact.display_name.clone().unwrap_or_default(), "phones": phones })
}

/// Protocol traffic that changes an existing message.
pub enum Control {
    /// "Delete for everyone": the message with this id is gone.
    Revoke(String),
    /// The message with this id was edited to this content.
    Edit(String, Content),
    /// A message was pinned (true) or unpinned in the chat: for how long (seconds, 0 not
    /// said) and when (sender's time, Unix ms; 0 not said).
    Pin(String, bool, u32, i64),
}

/// Someone turned disappearing messages on (seconds) or off (0) in a 1:1 chat.
pub fn ephemeral_setting(message: &wa::Message) -> Option<u32> {
    use wa::message::protocol_message::Type;
    let pm = base(message).protocol_message.as_option()?;
    (pm.r#type == Some(Type::EPHEMERAL_SETTING)).then(|| pm.ephemeral_expiration.unwrap_or(0))
}

/// The timer a message was sent with (seconds; 0 none).
pub fn expiration(message: &wa::Message) -> u32 {
    context_info(message).and_then(|c| c.expiration).unwrap_or(0)
}

/// "X turned on disappearing messages…", as the phone words it.
pub fn ephemeral_notice(who: &str, seconds: u32) -> String {
    if seconds == 0 {
        return format!("{who} turned off disappearing messages.");
    }
    format!("{who} turned on disappearing messages. New messages will disappear from this chat {} after they're sent.", duration_words(seconds))
}

pub fn duration_words(seconds: u32) -> String {
    match seconds {
        86_400 => "24 hours".into(),
        604_800 => "7 days".into(),
        7_776_000 => "90 days".into(),
        s if s % 86_400 == 0 => format!("{} days", s / 86_400),
        s if s >= 3_600 => format!("{} hours", s / 3_600),
        s => format!("{} minutes", (s / 60).max(1)),
    }
}

/// Marks an outgoing message to disappear after `seconds` (the chat's timer), the way the
/// phone does: in its context info. 0 leaves it alone.
pub fn with_expiration(mut m: wa::Message, seconds: u32) -> wa::Message {
    if seconds == 0 {
        return m;
    }
    if let Some(text) = m.conversation.take() {
        m.extended_text_message = MessageField::some(wa::message::ExtendedTextMessage { text: Some(text), ..Default::default() });
    }
    macro_rules! stamp {
        ($($field:ident),*) => {$(
            if let Some(x) = m.$field.as_option_mut() {
                x.context_info.get_or_insert_default().expiration = Some(seconds);
            }
        )*};
    }
    stamp!(extended_text_message, image_message, video_message, audio_message, document_message, sticker_message,
           location_message, contact_message, contacts_array_message, poll_creation_message, poll_creation_message_v3);
    m
}

pub fn control(message: &wa::Message) -> Option<Control> {
    use wa::message::pin_in_chat_message::Type as PinType;
    use wa::message::protocol_message::Type;
    let m = base(message);
    if let Some(pm) = m.protocol_message.as_option() {
        let id = pm.key.as_option()?.id.clone().filter(|id| !id.is_empty())?;
        return match pm.r#type {
            Some(Type::REVOKE) => Some(Control::Revoke(id)),
            Some(Type::MESSAGE_EDIT) => pm.edited_message.as_option().and_then(content).map(|c| Control::Edit(id, c)),
            _ => None,
        };
    }
    if let Some(pin) = m.pin_in_chat_message.as_option() {
        let id = pin.key.as_option()?.id.clone().filter(|id| !id.is_empty())?;
        return match pin.r#type {
            Some(PinType::PIN_FOR_ALL) => Some(Control::Pin(id, true, pin_duration(message), pin.sender_timestamp_ms.unwrap_or(0))),
            Some(PinType::UNPIN_FOR_ALL) => Some(Control::Pin(id, false, 0, pin.sender_timestamp_ms.unwrap_or(0))),
            _ => None,
        };
    }
    None
}

/// How long a pin lasts: the pin message's add-on duration (on the message or its wrapper).
fn pin_duration(message: &wa::Message) -> u32 {
    let of = |m: &wa::Message| m.message_context_info.as_option().and_then(|c| c.message_add_on_duration_in_secs);
    of(message)
        .or_else(|| message.device_sent_message.as_option().and_then(|d| d.message.as_option()).and_then(of))
        .or_else(|| of(base(message)))
        .unwrap_or(0)
}

/// The secret a poll's votes are encrypted with; the poll message carries it.
pub fn message_secret(message: &wa::Message) -> Option<Vec<u8>> {
    let secret = |m: &wa::Message| m.message_context_info.as_option().and_then(|c| c.message_secret.clone());
    secret(message)
        .or_else(|| message.device_sent_message.as_option().and_then(|d| d.message.as_option()).and_then(secret))
        .or_else(|| secret(base(message)))
        .filter(|s| !s.is_empty())
}

/// An encrypted poll vote: which poll, the ciphertext, when it was cast (ms).
pub struct PollVote {
    pub poll_id: String,
    pub payload: Vec<u8>,
    pub iv: Vec<u8>,
    pub ts: i64,
}

pub fn poll_vote(message: &wa::Message) -> Option<PollVote> {
    let update = base(message).poll_update_message.as_option()?;
    let poll_id = update.poll_creation_message_key.as_option()?.id.clone().filter(|id| !id.is_empty())?;
    let vote = update.vote.as_option()?;
    Some(PollVote {
        poll_id,
        payload: vote.enc_payload.clone()?,
        iv: vote.enc_iv.clone()?,
        ts: update.sender_timestamp_ms.unwrap_or(0),
    })
}

/// A reaction: (id of the message reacted to, emoji). An empty emoji removes it.
pub fn reaction(message: &wa::Message) -> Option<(String, String)> {
    let r = base(message).reaction_message.as_option()?;
    let id = r.key.as_option()?.id.clone().filter(|id| !id.is_empty())?;
    Some((id, r.text.clone().unwrap_or_default()))
}

/// The message a reply quotes: its id, who wrote it and what it showed.
pub struct Quote {
    pub id: String,
    pub sender: String,
    pub kind: &'static str,
    pub text: String,
    pub file_name: String,
}

/// How many times it has been forwarded (0: not forwarded). WhatsApp shows "Forwarded",
/// and "Forwarded many times" from 5 on.
pub fn forwarding_score(message: &wa::Message) -> u32 {
    let Some(info) = context_info(message) else { return 0 };
    match info.forwarding_score {
        Some(n) if n > 0 => n,
        _ if info.is_forwarded == Some(true) => 1,
        _ => 0,
    }
}

/// `Some` when the message is a reply (swipe / "Reply" on another message).
pub fn quote(message: &wa::Message) -> Option<Quote> {
    let info = context_info(message)?;
    let id = info.stanza_id.clone().filter(|id| !id.is_empty())?;
    let quoted = info.quoted_message.as_option().and_then(content);
    Some(Quote {
        id,
        sender: info.participant.clone().unwrap_or_default(),
        kind: quoted.as_ref().map_or("", |c| c.kind),
        text: quoted.as_ref().map(|c| c.text.clone()).unwrap_or_default(),
        file_name: quoted.map(|c| c.file_name).unwrap_or_default(),
    })
}

/// The reply/forward details every kind of message can carry.
fn context_info(message: &wa::Message) -> Option<&wa::ContextInfo> {
    let m = base(message);
    m.extended_text_message.as_option().and_then(|x| x.context_info.as_option())
        .or_else(|| m.image_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.video_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.audio_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.document_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.sticker_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.location_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.contact_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.poll_creation_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.poll_creation_message_v3.as_option().and_then(|x| x.context_info.as_option()))
}

/// A stored message rebuilt for sending again from its CDN reference (no upload): marked
/// forwarded when `score` > 0 (how many times it has been forwarded, this time included),
/// or plain (your own message forwarded, a sticker or GIF picked from the panel).
pub fn forwarded(kind: &str, text: &str, file_name: &str, media: Option<&Media>, score: u32) -> Option<wa::Message> {
    let context = if score > 0 {
        MessageField::some(wa::ContextInfo { is_forwarded: Some(true), forwarding_score: Some(score), ..Default::default() })
    } else {
        MessageField::none()
    };
    let caption = (!text.is_empty()).then(|| text.to_string());
    let mut m = wa::Message::default();
    let some = |b: &Vec<u8>| (!b.is_empty()).then(|| b.clone());
    match (kind, media) {
        ("text", _) => {
            m.extended_text_message = MessageField::some(wa::message::ExtendedTextMessage {
                text: Some(text.to_string()),
                context_info: context,
                ..Default::default()
            })
        }
        ("image", Some(x)) => {
            m.image_message = MessageField::some(wa::message::ImageMessage {
                direct_path: Some(x.direct_path.clone()),
                media_key: some(&x.media_key),
                file_sha256: some(&x.file_sha256),
                file_enc_sha256: some(&x.file_enc_sha256),
                file_length: Some(x.file_length),
                mimetype: Some(x.mimetype.clone()),
                width: Some(x.width),
                height: Some(x.height),
                caption,
                context_info: context,
                ..Default::default()
            })
        }
        ("video" | "gif", Some(x)) => {
            m.video_message = MessageField::some(wa::message::VideoMessage {
                direct_path: Some(x.direct_path.clone()),
                media_key: some(&x.media_key),
                file_sha256: some(&x.file_sha256),
                file_enc_sha256: some(&x.file_enc_sha256),
                file_length: Some(x.file_length),
                mimetype: Some(x.mimetype.clone()),
                width: Some(x.width),
                height: Some(x.height),
                seconds: Some(x.seconds),
                gif_playback: Some(kind == "gif"),
                caption,
                context_info: context,
                ..Default::default()
            })
        }
        ("voice" | "audio", Some(x)) => {
            m.audio_message = MessageField::some(wa::message::AudioMessage {
                direct_path: Some(x.direct_path.clone()),
                media_key: some(&x.media_key),
                file_sha256: some(&x.file_sha256),
                file_enc_sha256: some(&x.file_enc_sha256),
                file_length: Some(x.file_length),
                mimetype: Some(x.mimetype.clone()),
                seconds: Some(x.seconds),
                ptt: Some(kind == "voice"),
                waveform: some(&x.waveform),
                context_info: context,
                ..Default::default()
            })
        }
        ("document", Some(x)) => {
            m.document_message = MessageField::some(wa::message::DocumentMessage {
                direct_path: Some(x.direct_path.clone()),
                media_key: some(&x.media_key),
                file_sha256: some(&x.file_sha256),
                file_enc_sha256: some(&x.file_enc_sha256),
                file_length: Some(x.file_length),
                mimetype: Some(x.mimetype.clone()),
                file_name: (!file_name.is_empty()).then(|| file_name.to_string()),
                caption,
                context_info: context,
                ..Default::default()
            })
        }
        ("sticker", Some(x)) => {
            m.sticker_message = MessageField::some(wa::message::StickerMessage {
                direct_path: Some(x.direct_path.clone()),
                media_key: some(&x.media_key),
                file_sha256: some(&x.file_sha256),
                file_enc_sha256: some(&x.file_enc_sha256),
                file_length: Some(x.file_length),
                mimetype: Some(x.mimetype.clone()),
                width: Some(x.width),
                height: Some(x.height),
                context_info: context,
                ..Default::default()
            })
        }
        _ if !text.is_empty() => {
            m.extended_text_message = MessageField::some(wa::message::ExtendedTextMessage {
                text: Some(text.to_string()),
                context_info: context,
                ..Default::default()
            })
        }
        _ => return None,
    }
    Some(m)
}

/// What a sent reply carries as its `quotedMessage`: enough for the other side to
/// draw the quote (kind, caption/text, file name), without the attachment itself.
pub fn quoted_message(kind: &str, text: &str, file_name: &str, mime: &str, seconds: u32) -> wa::Message {
    let text_opt = (!text.is_empty()).then(|| text.to_string());
    let mime_opt = (!mime.is_empty()).then(|| mime.to_string());
    let mut m = wa::Message::default();
    match kind {
        "image" => {
            m.image_message = MessageField::some(wa::message::ImageMessage { caption: text_opt, mimetype: mime_opt, ..Default::default() })
        }
        "video" | "gif" => {
            m.video_message = MessageField::some(wa::message::VideoMessage {
                caption: text_opt,
                mimetype: mime_opt,
                seconds: Some(seconds),
                gif_playback: Some(kind == "gif"),
                ..Default::default()
            })
        }
        "voice" | "audio" => {
            m.audio_message = MessageField::some(wa::message::AudioMessage {
                mimetype: mime_opt,
                seconds: Some(seconds),
                ptt: Some(kind == "voice"),
                ..Default::default()
            })
        }
        "document" => {
            m.document_message = MessageField::some(wa::message::DocumentMessage {
                file_name: (!file_name.is_empty()).then(|| file_name.to_string()),
                caption: text_opt,
                mimetype: mime_opt,
                ..Default::default()
            })
        }
        "sticker" => m.sticker_message = MessageField::some(wa::message::StickerMessage { mimetype: mime_opt, ..Default::default() }),
        "location" => m.location_message = MessageField::some(wa::message::LocationMessage { name: text_opt, ..Default::default() }),
        "contact" => m.contact_message = MessageField::some(wa::message::ContactMessage { display_name: text_opt, ..Default::default() }),
        _ => m.conversation = Some(text.to_string()),
    }
    m
}
