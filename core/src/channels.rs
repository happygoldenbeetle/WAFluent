//! Channels (WhatsApp's newsletters): the ones you follow, ones to find, and their posts.
//!
//! A channel isn't a chat (it has no row in `chats`), but its posts are kept in the message
//! table under its id, so the app shows them with the same bubbles and their pictures and
//! videos download through the same queue (they aren't encrypted: media.rs). What a post has
//! that a message doesn't, its server id and its reaction counts, rides in its extras as
//! {channel: {serverId, reactions: [[emoji, count]…]}}.
//!
//! WhatsApp answers the lists through its GraphQL door (`mex`). Two things the library's own
//! calls get wrong against the live service are done here instead: the followed list must
//! name both of its switches, and the directory must be asked with a country.

use std::sync::Arc;

use log::{info, warn};
use serde_json::{Value, json};
use whatsapp_rust::NewsletterMessage;
use whatsapp_rust::prelude::*;
use whatsapp_rust::wacore::iq::mex_operations::{
    fetch_all_newsletters_metadata as followed, fetch_newsletter_directory_list as directory, fetch_newsletter_directory_search_results as find,
};

use crate::protocol::{ChannelDto, Event as Out};
use crate::store::{self, StoredMessage};
use crate::{Ctx, extract};

/// Posts fetched at a time.
const PAGE: u32 = 30;

pub(crate) fn is_channel(id: &str) -> bool {
    id.ends_with("@newsletter")
}

/// The Channels page's lists: what's kept at once, then what WhatsApp says now.
pub(crate) fn load(ctx: &Ctx, client: &Arc<Client>) {
    let kept = ctx.db().channels();
    if !kept.is_empty() {
        ctx.send(Out::Channels { followed: kept, suggested: Vec::new(), fresh: false });
    }
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let request = whatsapp_rust::MexRequest::new(
            followed::NAME,
            followed::DOC_ID,
            followed::Variables { fetch_status_metadata: Some(false), fetch_wamo_sub: Some(false) },
        );
        let mine = match client.mex().query(request).await {
            Ok(answer) => answer.data.map(|d| d["xwa2_newsletter_subscribed"].as_array().cloned().unwrap_or_default()).unwrap_or_default(),
            Err(e) => {
                warn!("channels: the followed list failed: {e:?}");
                ctx.send(Out::Notice { ok: false, text: "Couldn't load your channels. Try again in a moment.".into() });
                return;
            }
        };
        let mut list = Vec::new();
        for value in &mine {
            let Some(mut channel) = parse(&ctx, value, true).await else { continue };
            // Its newest posts: the line under its name, and how many are new since you looked.
            if let Ok(jid) = channel.id.parse::<Jid>()
                && let Ok(posts) = fetch(&client, &jid, 10, None).await
            {
                let db = ctx.db();
                let seen = db.channel_seen(&channel.id);
                for post in &posts {
                    keep(&db, &channel.id, post);
                }
                if !channel.muted {
                    channel.unread = posts.iter().filter(|p| p.timestamp as i64 > seen && p.message.is_some()).count() as u32;
                }
                if let Some(last) = db.last_message(&channel.id) {
                    channel.last_ts = last.ts;
                    channel.preview = store::preview(&last.kind, &last.text, &last.file_name);
                }
            }
            list.push(channel);
        }
        list.sort_by_key(|c| std::cmp::Reverse(c.last_ts));
        ctx.db().set_channels(&list);
        info!("channels: {} followed", list.len());
        ctx.send(Out::Channels { followed: list.clone(), suggested: Vec::new(), fresh: true });

        // Ones to follow, from WhatsApp's directory for your country.
        let variables = json!({ "fetch_status_metadata": false, "input": { "limit": 12, "view": "RECOMMENDED", "filters": { "country_codes": [country(&client)] } } });
        match client.mex().query(whatsapp_rust::MexRequest::new(directory::NAME, directory::DOC_ID, variables)).await {
            Ok(answer) => {
                let found = answer.data.map(|d| d["xwa2_newsletters_directory_list"]["result"].as_array().cloned().unwrap_or_default()).unwrap_or_default();
                let mut suggested = Vec::new();
                for value in found.iter().filter(|v| !list.iter().any(|c| Some(c.id.as_str()) == v["id"].as_str())).take(8) {
                    suggested.extend(parse(&ctx, value, false).await);
                }
                info!("channels: {} suggested", suggested.len());
                ctx.send(Out::Channels { followed: list, suggested, fresh: true });
            }
            Err(e) => warn!("channels: the directory failed: {e:?}"),
        }
    });
}

/// Channels whose name matches (the page's search box).
pub(crate) fn search(ctx: &Ctx, client: &Arc<Client>, query: String) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let variables = json!({ "fetch_status_metadata": false, "input": { "limit": 20, "search_text": query } });
        let mut results = Vec::new();
        match client.mex().query(whatsapp_rust::MexRequest::new(find::NAME, find::DOC_ID, variables)).await {
            Ok(answer) => {
                let found = answer.data.map(|d| d["xwa2_newsletters_directory_search"]["result"].as_array().cloned().unwrap_or_default()).unwrap_or_default();
                let mine: Vec<String> = ctx.db().channels().into_iter().map(|c| c.id).collect();
                for value in &found {
                    let is_mine = value["id"].as_str().is_some_and(|id| mine.iter().any(|m| m == id));
                    results.extend(parse(&ctx, value, is_mine).await);
                }
            }
            Err(e) => warn!("channels: search failed: {e:?}"),
        }
        ctx.send(Out::ChannelSearch { query, results });
    });
}

/// The two-letter country of your own number ("PK"), for the directory.
fn country(client: &Client) -> String {
    let digits = client.persistence_manager().get_device_snapshot().pn.as_ref().map(|j| j.user.to_string()).unwrap_or_default();
    store::phone_region(&digits).unwrap_or_else(|| "US".into())
}

/// One channel from WhatsApp's JSON, its picture fetched (once per picture) into the avatar cache.
async fn parse(ctx: &Ctx, value: &Value, is_followed: bool) -> Option<ChannelDto> {
    let id = value["id"].as_str()?.to_string();
    let thread = &value["thread_metadata"];
    let number = |v: &Value| v.as_u64().or_else(|| v.as_str().and_then(|s| s.parse().ok())).unwrap_or(0);
    let kept = ctx.db().channel(&id);
    let picture_id = thread["picture"]["id"].as_str().or_else(|| thread["preview"]["id"].as_str()).unwrap_or("").to_string();
    let path = thread["preview"]["direct_path"].as_str().or_else(|| thread["picture"]["direct_path"].as_str()).map(str::to_string);
    let dir = ctx.data_dir.join("avatars");
    let file = dir.join(format!("channel_{}_{}.jpg", safe(&id), safe(&picture_id)));
    let avatar = if file.exists() {
        Some(file.to_string_lossy().into_owned())
    } else if let Some(path) = path {
        let url = format!("https://pps.whatsapp.net{path}");
        let bytes = tokio::task::spawn_blocking(move || crate::avatars::download(&url)).await.ok().and_then(Result::ok);
        bytes.and_then(|b| {
            std::fs::create_dir_all(&dir).ok()?;
            std::fs::write(&file, b).ok()?;
            Some(file.to_string_lossy().into_owned())
        })
    } else {
        None
    };
    Some(ChannelDto {
        name: thread["name"]["text"].as_str().unwrap_or("").to_string(),
        description: thread["description"]["text"].as_str().unwrap_or("").to_string(),
        followers: number(&thread["subscribers_count"]),
        verified: thread["verification"].as_str() == Some("VERIFIED"),
        avatar,
        followed: is_followed,
        muted: kept.as_ref().is_some_and(|k| k.muted),
        last_ts: kept.as_ref().map_or(0, |k| k.last_ts),
        preview: kept.as_ref().map(|k| k.preview.clone()).unwrap_or_default(),
        unread: 0,
        id,
    })
}

fn safe(s: &str) -> String {
    s.chars().map(|c| if c.is_ascii_alphanumeric() { c } else { '_' }).collect()
}

fn post_id(post: &NewsletterMessage) -> String {
    if post.message_id.is_empty() { format!("post-{}", post.server_id) } else { post.message_id.clone() }
}

/// Keeps one post (or brings its text and reaction counts up to date). False: nothing to show in it.
fn keep(db: &store::Store, chat_id: &str, post: &NewsletterMessage) -> bool {
    let Some(content) = post.message.as_ref().and_then(extract::content) else { return false };
    let id = post_id(post);
    let mut extra = content.extra.clone().unwrap_or_else(|| json!({}));
    extra["channel"] = json!({ "serverId": post.server_id, "reactions": post.reactions.iter().map(|r| json!([r.code, r.count])).collect::<Vec<_>>() });
    let stored = StoredMessage {
        id: id.clone(),
        from_me: false,
        sender: String::new(),
        push_name: String::new(),
        ts: post.timestamp as i64,
        kind: content.kind.to_string(),
        text: content.text,
        file_name: content.file_name,
        status: 0,
    };
    if db.insert_message(chat_id, &stored) {
        if let Some(media) = &content.media {
            db.insert_media(chat_id, &id, media);
        }
        db.insert_extra(chat_id, &id, &content.thumb, Some(&extra));
    } else {
        db.update_post(chat_id, &id, &stored.text, &extra);
    }
    true
}

/// A channel's posts: the newest page, or (`older`) the page before the oldest one kept.
/// Answered like a chat's: `messages`, or `olderMessages`.
pub(crate) fn posts(ctx: &Ctx, client: &Arc<Client>, chat_id: String, older: bool) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let Ok(jid) = chat_id.parse::<Jid>() else { return };
        let before = if older { ctx.db().oldest_post(&chat_id) } else { None };
        if older && before.is_none() {
            ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: true });
            return;
        }
        let fetched = match fetch(&client, &jid, PAGE, before).await {
            Ok(posts) => posts,
            Err(e) => {
                warn!("channels: posts failed: {e}");
                if older {
                    ctx.send(Out::OlderMessages { chat_id, messages: Vec::new(), complete: false });
                } else {
                    ctx.send(Out::Notice { ok: false, text: "Couldn't load this channel's updates.".into() });
                }
                return;
            }
        };
        info!("channels: {} posts ({})", fetched.len(), if older { "older" } else { "newest" });
        let messages = {
            let db = ctx.db();
            let ids: Vec<String> = fetched.iter().filter(|post| keep(&db, &chat_id, post)).map(post_id).collect();
            if older {
                let mut found: Vec<_> = ids.iter().filter_map(|id| db.message(&chat_id, id)).map(|m| db.to_dto(&chat_id, m)).collect();
                found.sort_by_key(|m| m.ts);
                found
            } else {
                db.set_channel_seen(&chat_id, store::unix_now());
                db.messages(&chat_id, 300)
            }
        };
        if older {
            ctx.send(Out::OlderMessages { chat_id, messages, complete: fetched.len() < PAGE as usize / 2 });
        } else {
            ctx.send(Out::Messages { chat_id: chat_id.clone(), messages });
            // Reaction counts as they change, while it's open (WhatsApp sends them for a few minutes).
            let _ = client.newsletter().subscribe_live_updates(jid).await;
        }
    });
}

/// A page of posts. The channel is asked directly first (how a follower's client asks); when
/// that isn't answered in a few seconds or fails, as it does for a channel you don't follow,
/// the server is asked for it the way a visitor's client does.
async fn fetch(client: &Client, jid: &Jid, count: u32, before: Option<u64>) -> Result<Vec<NewsletterMessage>, String> {
    let direct = tokio::time::timeout(std::time::Duration::from_secs(6), client.newsletter().get_messages(jid.clone(), count, before)).await;
    match direct {
        Ok(Ok(posts)) if !posts.is_empty() => return Ok(posts),
        Ok(Ok(_)) => info!("channels: asked directly: no posts"),
        Ok(Err(e)) => info!("channels: asked directly: {e:?}"),
        Err(_) => info!("channels: asked directly: no answer"),
    }
    match tokio::time::timeout(std::time::Duration::from_secs(20), client.newsletter().get_messages_as_guest(jid.clone(), count, before)).await {
        Ok(Ok(posts)) => Ok(posts),
        Ok(Err(e)) => Err(format!("{e:?}")),
        Err(_) => Err("no answer".into()),
    }
}

/// follow | unfollow | mute | unmute. The lists are sent again afterwards.
pub(crate) fn action(ctx: &Ctx, client: &Arc<Client>, chat_id: String, action: String) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let Ok(jid) = chat_id.parse::<Jid>() else { return };
        let done = match action.as_str() {
            "follow" => client.newsletter().join(&jid).await.map(|_| "Following"),
            "unfollow" => client.newsletter().leave(&jid).await.map(|_| "Unfollowed"),
            // Kept on this PC: it only decides whether the channel's new updates are counted.
            "mute" | "unmute" => {
                ctx.db().set_channel_muted(&chat_id, action == "mute");
                Ok(if action == "mute" { "Channel muted" } else { "Channel unmuted" })
            }
            _ => return,
        };
        match done {
            Ok(text) => {
                if action == "unfollow" {
                    ctx.db().forget_channel(&chat_id);
                }
                ctx.send(Out::Notice { ok: true, text: text.into() });
                load(&ctx, &client);
            }
            Err(e) => {
                warn!("channels: {action} failed: {e:?}");
                ctx.send(Out::Notice { ok: false, text: format!("Couldn't {action} this channel.") });
            }
        }
    });
}

/// Your reaction to a post ("" takes it back).
pub(crate) fn react(ctx: &Ctx, client: &Arc<Client>, chat_id: String, message_id: String, emoji: String) {
    let (ctx, client) = (ctx.clone(), Arc::clone(client));
    tokio::spawn(async move {
        let Ok(jid) = chat_id.parse::<Jid>() else { return };
        let Some(server_id) = ctx.db().post_server_id(&chat_id, &message_id) else { return };
        match client.newsletter().send_reaction(&jid, server_id, &emoji).await {
            Ok(()) => posts(&ctx, &client, chat_id, false),   // the counts, with yours in them
            Err(e) => {
                warn!("channels: reaction failed: {e:?}");
                ctx.send(Out::Notice { ok: false, text: "The reaction couldn't be sent.".into() });
            }
        }
    });
}

/// A post arrived live (a channel you follow): the app is told to read the channel again.
pub(crate) fn live(ctx: &Ctx, chat_id: &str) {
    ctx.send(Out::ChannelChanged { chat_id: chat_id.to_string() });
}

/// Reaction counts changed on posts of a channel that's open.
pub(crate) fn counts(ctx: &Ctx, chat_id: &str, changes: &[(u64, Vec<(String, u64)>)]) {
    let mut updated = Vec::new();
    {
        let db = ctx.db();
        for (server_id, reactions) in changes {
            if let Some(id) = db.set_post_reactions(chat_id, *server_id, reactions)
                && let Some(m) = db.message(chat_id, &id)
            {
                updated.push(db.to_dto(chat_id, m));
            }
        }
    }
    for message in updated {
        ctx.send(Out::MessageUpdated { chat_id: chat_id.to_string(), message });
    }
}
