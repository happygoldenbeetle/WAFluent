# WAFluent

A native WinUI 3 WhatsApp client for Windows 11 — Fluent design, Mica, light/dark theme, no browser engine.

> **Status:** links to your phone by QR code, shows your real chats: photos, stickers, voice notes and audio, videos and GIFs,
> documents, locations, contact cards, polls, link previews and WhatsApp's notices (missed calls, group changes),
> and sends text messages, replies (drag a message to the right) and reactions (right-click or double-click a message).
> Right-click a chat for WhatsApp Desktop's menu (archive, mute, pin, mark unread, favourites, block, clear, delete, add to contacts)
> and a message to forward, pin, star, select, report or delete it. Emoji: in-app keyboard and :shortcode: autocomplete.
> Sending media and calls are not wired up yet (the call window is a mockup).
> WAFluent is unofficial and not affiliated with WhatsApp or Meta; unofficial clients can get accounts suspended.
> Use a spare number while testing.

## How it works

```
WAFluent.exe (WinUI 3, C#)  ⇄  JSON lines over stdin/stdout  ⇄  core\wafluent-core.exe (Rust)
                                                                          └─ whatsapp-rust 0.7 → WhatsApp servers
```

- **`core/`** is a small Rust program built on [whatsapp-rust](https://crates.io/crates/whatsapp-rust). It handles pairing, encryption and the connection, and keeps a local SQLite store of chats and messages (WhatsApp only sends history once, right after linking). The protocol is documented in `core/src/protocol.rs`.
- **`src/`** is the WinUI app. It starts the core, shows the QR/link screen until chats are available, then the chat list and conversations.

Data lives in `%LOCALAPPDATA%\WAFluent`: `whatsapp.db` (session keys), `wafluent.db` (chats/messages), `avatars\` (profile pictures, re-checked daily), `core.log`.
**Settings → Log out** unlinks the device and deletes the local chats.

## Build

Requires the .NET 10 SDK and stable Rust (`rustup`) on Windows 10 1904+ / Windows 11.

```powershell
cd src
dotnet build -p:Platform=x64      # also runs `cargo build` for core/
.\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WAFluent.exe
```

- Emoji use the iOS set when `src/Assets/Fonts/AppleColorEmoji.ttf` exists (not committed: it's Apple's artwork and 35 MB).
  Extract `system/fonts/NotoColorEmoji.ttf` from the iOS-emoji Magisk module zip and save it under that name; without it Windows' emoji are used.
- `--sample` shows placeholder chats instead of connecting (also used automatically when the core isn't built).
- `--theme light|dark` forces a theme.
- `--panel info|contact` opens Contact info or New contact for the open chat at start.
- `-p:SkipCore=true` builds the UI without compiling the Rust core.

The app is unpackaged and self-contained (Windows App SDK 2.5), so no MSIX install or runtime download is needed.

## Credits

Emoji names, categories and shortcodes: [emojibase](https://github.com/milesj/emojibase) (MIT), trimmed by `tools/make-emoji-data.py`.
Tray icon: [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (MIT).

## Layout

| Path | What |
|---|---|
| `core/src/main.rs` | Connection, event handling, history-sync ingestion |
| `core/src/store.rs` | SQLite chat/message/contact-name store |
| `core/src/extract.rs` | WhatsApp message → text/media label |
| `core/src/avatars.rs` | Profile-picture fetch queue and disk cache |
| `src/Services/CoreClient.cs` | Starts the core and translates its events |
| `src/MainWindow.xaml` | Rail, chat list, conversation pane, link screen |
| `src/CallWindow.xaml` | In-call window (mockup) |
| `src/Controls/` | `Avatar` (with status ring), `Bubble`, `ChatLayout` (conversation list layout), `DeliveryTicks`, `VoicePlayer`, `QuoteBlock`, `TiledBackground` (chat wallpaper) |
| `src/MainWindow.Swipe.cs` | Drag-to-reply gesture |
| `src/MainWindow.Reactions.cs` | Reaction row in the message menu, emoji flight into the pill |
| `src/MainWindow.ChatListPane.cs` | Resizable / collapsible chat list (remembered in `ui.json`) |
| `src/MainWindow.Settings.cs` | Settings (quick reactions, developer mode) |
| `src/MainWindow.ChatMenu.cs` | Chat list menu, filters, Starred view, select mode, toast |
| `src/MainWindow.Emoji.cs`, `src/Controls/EmojiPicker.xaml` | Emoji keyboard and :shortcode: autocomplete |
| `core/src/actions.rs` | Chat and message actions sent to WhatsApp |
| `src/Helpers/` | Formatting, QR rendering, x:Bind functions, window sizing, icon glyphs |
| `src/Styles/Theme.xaml` | Light/dark/high-contrast colours, bubble style |
| `src/Models/` | `Chat`, `Message`, sample data |
| `src/ViewModels/MainViewModel.cs` | Connection state, chat list sync, messages |
| `tools/make-assets.ps1` | Regenerates the app icon and profile placeholder |
| `src/Assets/Wallpaper.*.png` | WhatsApp's chat doodle tiles ([source](https://gist.github.com/abdurrahmanekr/2747d704edec93a06e454eba2653e0df)) |
