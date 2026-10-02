# Vendored crates

## whatsapp-rust 0.7.0

The published crate (src, manifest without its example and test targets) plus one patch:
app-state `favoriteSticker`, `call_log` and `favorites` mutations, which 0.7.0 decodes and then
drops, are handed to the app through `whatsapp_rust::wafluent_hooks::on_sticker_mutation`,
`on_call_log` and `on_favorites`. Calls through a call link also needed changes, all marked
"WAFluent patch": WhatsApp's own messages about such a call (sent from the call's JID) are
accepted, participant id 0 means "none yet", joining waits for a second person instead of
giving up after ten seconds, and stanzas describing a link call are handed to the app
(`on_link_call`). `Client::fetch_full_history` (pdo.rs) asks the phone for a full history sync
again, for the call history. The patch is the new
`src/wafluent_hooks.rs`, its `pub mod` line in `src/lib.rs`, and a four-line call at the top
of `dispatch_app_state_mutation` in `src/client/app_state.rs` (search "WAFluent patch").

To update: copy the new release's `src`, `Cargo.toml`, `LICENSE` and `README.md` here,
drop the `[[example]]`/`[[test]]` targets from the manifest and re-apply the patch.
