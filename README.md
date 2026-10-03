# WAFluent

A native WinUI 3 WhatsApp client for Windows 11 — Fluent design, Mica, light/dark theme, no browser engine.

![WAFluent with a chat open, dark theme](docs/screenshots/chat-dark.jpg)

| Light theme | Calls |
|---|---|
| ![Light theme](docs/screenshots/chat-light.jpg) | ![Calls: favourites, recent calls and a contact's call history](docs/screenshots/calls.jpg) |
| **Channels** | **Status** |
| ![A channel's posts with reactions and forward counts](docs/screenshots/channels.jpg) | ![Status: your contacts' updates](docs/screenshots/status.jpg) |

<sub>Screenshots show the app's built-in sample chats (`--sample`), not a real account.</sub>

> **Status:** links to your phone by QR code, shows your real chats: photos, stickers, voice notes and audio, videos and GIFs,
> documents, locations, contact cards, polls, link previews and WhatsApp's notices (missed calls, group changes),
> and sends text messages, replies (drag a message to the right), reactions (right-click or double-click a message),
> stickers (your favourites synced from the phone, and recents) and GIFs (recents and GIPHY search).
> Right-click a chat for WhatsApp Desktop's menu (archive, mute, pin, mark unread, favourites, block, clear, delete, add to contacts)
> and a message to forward, pin, star, select, report or delete it. Emoji: in-app keyboard and :shortcode: autocomplete.
> Voice and video calls with one person: call from a chat, answer or decline one that rings (with Windows' incoming-call
> notification), mute, turn the camera on or off, switch a voice call to video, pick the microphone and camera, hang up.
> The Calls page lists your call history (from the phone and this PC) with favourites, a number pad to call any number,
> and call links to share. Calls show in the chat as cards you can click to call again. Group calls are switched off for now.
> Status: your contacts' updates in a viewer, with seen receipts and replies.
> Channels: the ones you follow and ones to find, their posts, reactions, follow, unfollow and mute.
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
**Profile → Log out** unlinks the device and deletes the local chats.

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

### Installer

`tools/installer.iss` ([Inno Setup 6](https://jrsoftware.org/isinfo.php)) makes `dist\WAFluent-<version>-setup.exe` from a release build:
a per-user install with no admin prompt, Start menu and desktop shortcuts, and an entry in Installed apps to remove it.

```powershell
cd src
dotnet build -c Release -p:Platform=x64 --self-contained true -p:Version=0.1.0
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" ..\tools\installer.iss
```

## Credits

Emoji names, categories and shortcodes: [emojibase](https://github.com/milesj/emojibase) (MIT), trimmed by `tools/make-emoji-data.py`.
Tray icon: [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (MIT).
Screenshot background: a photo by [Milad Fakurian](https://unsplash.com/@fakurian) on [Unsplash](https://unsplash.com/photos/E8Ufcyxz514), blurred.

## Layout

| Path | What |
|---|---|
| `core/src/main.rs` | Connection, event handling, history-sync ingestion |
| `core/src/store.rs` | SQLite chat/message/contact-name store |
| `core/src/extract.rs` | WhatsApp message → text/media label |
| `core/src/avatars.rs` | Profile-picture fetch queue and disk cache |
| `src/Services/CoreClient.cs` | Starts the core and translates its events |
| `src/MainWindow.xaml` | Rail, chat list, conversation pane, link screen |
| `core/src/calls.rs` | Calls: whatsapp-rust's voip stack, with sound and H.264 video passed to and from the app |
| `core/src/call_log.rs` | The call history: the phone's (history sync, app state) and this PC's; call links |
| `src/MainWindow.CallsPage.cs` | The Calls page: favourites, recent calls, call a number, new call link |
| `src/CallWindow.xaml` | The call window (calling, ringing, in a call; video in `CallWindow.Video.cs`) |
| `src/Services/CallAudio.cs` | A call's microphone and speakers (16 kHz frames, AudioGraph) |
| `src/Services/CallCamera.cs`, `H264Encoder.cs` | Your camera in a video call, encoded with Windows' H.264 encoder |
| `src/Services/CallVideoPlayer.cs` | The other side's picture, decoded and drawn by a MediaPlayer |
| `src/Controls/` | `Avatar` (with status ring), `Bubble`, `ChatLayout` (conversation list layout), `DeliveryTicks`, `VoicePlayer`, `QuoteBlock`, `TiledBackground` (chat wallpaper) |
| `src/MainWindow.Swipe.cs` | Drag-to-reply gesture |
| `src/MainWindow.Reactions.cs` | Reaction row in the message menu, emoji flight into the pill |
| `src/MainWindow.ChatListPane.cs` | Resizable / collapsible chat list (remembered in `ui.json`) |
| `src/MainWindow.Settings.cs` | Settings (quick reactions, accent colour) |
| `core/src/status.rs`, `src/MainWindow.Status.cs` | Status: contacts' updates, the viewer, replies |
| `core/src/channels.rs`, `src/MainWindow.Channels.cs` | Channels: the list, posts, follow, mute, reactions |
| `tools/installer.iss` | The installer (Inno Setup) |
| `docs/screenshots/` | The pictures in this README |
| `src/MainWindow.ChatMenu.cs` | Chat list menu, filters, Starred view, select mode, toast |
| `src/MainWindow.Emoji.cs`, `src/Controls/EmojiPicker.xaml` | Emoji keyboard and :shortcode: autocomplete |
| `core/src/actions.rs` | Chat and message actions sent to WhatsApp |
| `src/Helpers/` | Formatting, QR rendering, x:Bind functions, window sizing, icon glyphs |
| `src/Styles/Theme.xaml` | Light/dark/high-contrast colours, bubble style |
| `src/Models/` | `Chat`, `Message`, sample data |
| `src/ViewModels/MainViewModel.cs` | Connection state, chat list sync, messages |
| `tools/make-assets.ps1` | Regenerates the app icon and profile placeholder |
| `src/Assets/Wallpaper.*.png` | WhatsApp's chat doodle tiles ([source](https://gist.github.com/abdurrahmanekr/2747d704edec93a06e454eba2653e0df)) |
