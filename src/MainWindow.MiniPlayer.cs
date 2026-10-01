using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// A voice note keeps playing when you open another chat, like WhatsApp: a small player above
/// the chat list shows who it's from and how far along it is, with play/pause, speed and stop;
/// clicking it goes back to the message. It's hidden while its own chat is open (the bubble
/// shows the same).
/// </summary>
public sealed partial class MainWindow
{
    private Message? _miniMessage;
    private Chat? _miniChat;

    private void SetupMiniPlayer()
    {
        AudioPlayback.Changed += RefreshMiniPlayer;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) RefreshMiniPlayer();
        };
    }

    private void RefreshMiniPlayer()
    {
        if (AudioPlayback.Current is not { } m)
        {
            _miniMessage = null;
            _miniChat = null;
            MiniPlayer.Visibility = Visibility.Collapsed;
            return;
        }
        if (m != _miniMessage)
        {
            _miniMessage = m;
            _miniChat = ViewModel.ChatOf(m) ?? ViewModel.SelectedChat;
            MiniName.Text = m.IsOutgoing ? "You" : m.SenderName.Length > 0 ? m.SenderName : _miniChat?.Name ?? "Voice message";
        }
        MiniPlayer.Visibility = _miniChat is not null && _miniChat != ViewModel.SelectedChat ? Visibility.Visible : Visibility.Collapsed;
        if (MiniPlayer.Visibility != Visibility.Visible) return;

        var duration = AudioPlayback.Duration;
        var position = AudioPlayback.Position;
        var where = _miniChat is { IsGroup: true } chat ? $" · {chat.Name}" : "";
        MiniDetail.Text = $"{Clock(position)} / {Clock(duration)}{where}";
        MiniProgress.Value = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        MiniPlayIcon.Glyph = AudioPlayback.IsPlaying ? "\uE769" : "\uE768";
        MiniRate.Text = $"{AudioPlayback.Rate:0.#}×";
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void MiniPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_miniMessage is { } m) AudioPlayback.Toggle(m);
    }

    private void MiniRate_Click(object sender, RoutedEventArgs e) => AudioPlayback.CycleRate();

    private void MiniClose_Click(object sender, RoutedEventArgs e) => AudioPlayback.Stop();

    private async void MiniOpen_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_miniChat is not { } chat || _miniMessage is not { } m) return;
        ViewModel.SelectedChat = chat;
        await Task.Delay(350);   // let the conversation load
        ScrollToMessage(m.Id);
    }
}
