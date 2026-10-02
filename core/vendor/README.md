# Vendored crates

## whatsapp-rust 0.7.0

The published crate (src, manifest without its example and test targets) plus one patch:
app-state `favoriteSticker` and `call_log` mutations, which 0.7.0 decodes and then drops, are
handed to the app through `whatsapp_rust::wafluent_hooks::on_sticker_mutation` and
`on_call_log`. The patch is the new
`src/wafluent_hooks.rs`, its `pub mod` line in `src/lib.rs`, and a four-line call at the top
of `dispatch_app_state_mutation` in `src/client/app_state.rs` (search "WAFluent patch").

To update: copy the new release's `src`, `Cargo.toml`, `LICENSE` and `README.md` here,
drop the `[[example]]`/`[[test]]` targets from the manifest and re-apply the patch.
