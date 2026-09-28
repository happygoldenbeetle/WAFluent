//! Turns a WhatsApp protobuf message into what the chat UI shows.

use whatsapp_rust::prelude::*;

pub struct Content {
    pub kind: &'static str,
    pub text: String,
    pub file_name: String,
    /// Everything needed to download the attachment later (the message itself isn't kept).
    pub media: Option<Media>,
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
        Self { kind, text: text.into(), file_name: String::new(), media: None }
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

/// `None` for messages with nothing to show (reactions, edits, revokes, key
/// distribution, ...). Those are protocol traffic, not chat bubbles.
pub fn content(message: &wa::Message) -> Option<Content> {
    let m = message.get_base_message();

    if let Some(text) = m.conversation.as_deref().filter(|t| !t.is_empty()) {
        return Some(Content::new("text", text));
    }
    if let Some(ext) = m.extended_text_message.as_option() {
        if let Some(text) = ext.text.as_deref() {
            return Some(Content::new("text", text));
        }
    }
    if let Some(img) = m.image_message.as_option() {
        return Some(Content::new("image", img.caption.clone().unwrap_or_default()).with_media(Media {
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
    if let Some(vid) = m.video_message.as_option() {
        let kind = if vid.gif_playback == Some(true) { "gif" } else { "video" };
        return Some(Content::new(kind, vid.caption.clone().unwrap_or_default()).with_media(Media {
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
        let mut content = Content::new("document", doc.caption.clone().unwrap_or_default()).with_media(Media {
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
        return Some(Content::new("sticker", "").with_media(Media {
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
        return Some(Content::new("location", label));
    }
    if let Some(contact) = m.contact_message.as_option() {
        return Some(Content::new("contact", contact.display_name.clone().unwrap_or_default()));
    }
    if let Some(poll) = m
        .poll_creation_message
        .as_option()
        .or_else(|| m.poll_creation_message_v2.as_option())
        .or_else(|| m.poll_creation_message_v3.as_option())
    {
        return Some(Content::new("poll", poll.name.clone().unwrap_or_default()));
    }

    // Reactions, protocol messages (revoke/edit/app-state keys), sender-key
    // distribution and the like have no bubble of their own.
    None
}

/// Protocol traffic that changes an existing message.
pub enum Control {
    /// "Delete for everyone": the message with this id is gone.
    Revoke(String),
    /// The message with this id was edited to this content.
    Edit(String, Content),
    /// A message was pinned (true) or unpinned in the chat.
    Pin(String, bool),
}

pub fn control(message: &wa::Message) -> Option<Control> {
    use wa::message::pin_in_chat_message::Type as PinType;
    use wa::message::protocol_message::Type;
    let m = message.get_base_message();
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
            Some(PinType::PIN_FOR_ALL) => Some(Control::Pin(id, true)),
            Some(PinType::UNPIN_FOR_ALL) => Some(Control::Pin(id, false)),
            _ => None,
        };
    }
    None
}

/// A reaction: (id of the message reacted to, emoji). An empty emoji removes it.
pub fn reaction(message: &wa::Message) -> Option<(String, String)> {
    let r = message.get_base_message().reaction_message.as_option()?;
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

/// `Some` when the message is a reply (swipe / "Reply" on another message).
pub fn quote(message: &wa::Message) -> Option<Quote> {
    let m = message.get_base_message();
    let info = m.extended_text_message.as_option().and_then(|x| x.context_info.as_option())
        .or_else(|| m.image_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.video_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.audio_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.document_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.sticker_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.location_message.as_option().and_then(|x| x.context_info.as_option()))
        .or_else(|| m.contact_message.as_option().and_then(|x| x.context_info.as_option()))?;
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

/// A forwarded copy: text as-is, attachments re-sent from their CDN reference (no re-upload).
pub fn forwarded(kind: &str, text: &str, file_name: &str, media: Option<&Media>) -> Option<wa::Message> {
    let context = MessageField::some(wa::ContextInfo { is_forwarded: Some(true), forwarding_score: Some(1), ..Default::default() });
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
