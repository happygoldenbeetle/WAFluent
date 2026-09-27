# WAFluent

A native WinUI 3 WhatsApp client for Windows 11 — Fluent design, Mica, light/dark theme, no browser engine.

> **Status:** links to your phone by QR code and shows your real chats and incoming messages.
> Sending, media downloads and calls are not wired up yet (the call window is a mockup).
> WAFluent is unofficial and not affiliated with WhatsApp or Meta; unofficial clients can get accounts suspended.
> Use a spare number while testing.

## How it works

```
WhatsAppNative.exe (WinUI 3, C#)  ⇄  JSON lines over stdin/stdout  ⇄  core\wafluent-core.exe (Rust)
                                                                          └─ whatsapp-rust 0.7 → WhatsApp servers
```

- **`core/`** is a small Rust program built on [whatsapp-rust](https://crates.io/crates/whatsapp-rust). It handles pairing, encryption and the connection, and keeps a local SQLite store of chats and messages (WhatsApp only sends history once, right after linking). The protocol is documented in `core/src/protocol.rs`.
- **`src/`** is the WinUI app. It starts the core, shows the QR/link screen until chats are available, then the chat list and conversations.

Data lives in `%LOCALAPPDATA%\WAFluent`: `whatsapp.db` (session keys), `wafluent.db` (chats/messages), `core.log`.
**Settings → Log out** unlinks the device and deletes the local chats.

## Build

Requires the .NET 10 SDK and stable Rust (`rustup`) on Windows 10 1904+ / Windows 11.

```powershell
cd src
dotnet build -p:Platform=x64      # also runs `cargo build` for core/
.\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WhatsAppNative.exe
```

- `--sample` shows placeholder chats instead of connecting (also used automatically when the core isn't built).
- `--theme light|dark` forces a theme.
- `-p:SkipCore=true` builds the UI without compiling the Rust core.

The app is unpackaged and self-contained (Windows App SDK 2.5), so no MSIX install or runtime download is needed.

## Layout

| Path | What |
|---|---|
| `core/src/main.rs` | Connection, event handling, history-sync ingestion |
| `core/src/store.rs` | SQLite chat/message/contact-name store |
| `core/src/extract.rs` | WhatsApp message → text/media label |
| `src/Services/CoreClient.cs` | Starts the core and translates its events |
| `src/MainWindow.xaml` | Rail, chat list, conversation pane, link screen |
| `src/CallWindow.xaml` | In-call window (mockup) |
| `src/Controls/` | `Avatar` (with status ring), `Bubble`, `DeliveryTicks` |
| `src/Helpers/` | Formatting, QR rendering, x:Bind functions, window sizing, icon glyphs |
| `src/Styles/Theme.xaml` | Light/dark/high-contrast colours, bubble style |
| `src/Models/` | `Chat`, `Message`, sample data |
| `src/ViewModels/MainViewModel.cs` | Connection state, chat list sync, messages |
| `tools/make-assets.ps1` | Regenerates the doodle wallpapers and app icon |
