# WAFluent

A native WinUI 3 WhatsApp client for Windows 11 — Fluent design, Mica, light/dark theme, no browser engine.

> **Status:** UI only. Chats and messages are sample data; nothing connects to WhatsApp yet.
> The planned backend is a Rust sidecar built on [whatsapp-rust](https://github.com/crmne/zapfast) (the library ZapFast uses).
> WAFluent is unofficial and not affiliated with WhatsApp or Meta; unofficial clients can get accounts suspended.

## Build

Requires the .NET 10 SDK on Windows 10 1904+ / Windows 11.

```powershell
cd src
dotnet build -p:Platform=x64
.\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WhatsAppNative.exe
```

The app is unpackaged and self-contained (Windows App SDK 2.5), so no MSIX install or runtime download is needed.

## Layout

| Path | What |
|---|---|
| `src/MainWindow.xaml` | Sidebar, conversation pane, message templates |
| `src/Controls/` | `Avatar`, `Bubble`, `DeliveryTicks` |
| `src/Styles/Theme.xaml` | Light/dark/high-contrast colours, bubble style |
| `src/Models/` | `Chat`, `Message`, sample data |
| `src/ViewModels/MainViewModel.cs` | Chat list, search, selection, sending |
| `tools/make-assets.ps1` | Regenerates the doodle wallpapers and app icon |
