//! Turns a WhatsApp protobuf message into what the chat UI shows.

use whatsapp_rust::prelude::*;

pub struct Content {
    pub kind: &'static str,
    pub text: String,
    pub file_name: String,
}

impl Content {
    fn new(kind: &'static str, text: impl Into<String>) -> Self {
        Self { kind, text: text.into(), file_name: String::new() }
    }
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
        return Some(Content::new("image", img.caption.clone().unwrap_or_default()));
    }
    if let Some(vid) = m.video_message.as_option() {
        let kind = if vid.gif_playback == Some(true) { "gif" } else { "video" };
        return Some(Content::new(kind, vid.caption.clone().unwrap_or_default()));
    }
    if let Some(audio) = m.audio_message.as_option() {
        let kind = if audio.ptt == Some(true) { "voice" } else { "audio" };
        return Some(Content::new(kind, ""));
    }
    if let Some(doc) = m.document_message.as_option() {
        let file_name = doc.file_name.clone().or_else(|| doc.title.clone()).unwrap_or_default();
        return Some(Content {
            kind: "document",
            text: doc.caption.clone().unwrap_or_default(),
            file_name,
        });
    }
    if m.sticker_message.as_option().is_some() {
        return Some(Content::new("sticker", ""));
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
