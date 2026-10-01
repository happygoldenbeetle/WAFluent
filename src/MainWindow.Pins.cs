using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Pinned messages, like WhatsApp: up to four per chat, each for 24 hours, 7 days or 30 days
/// (picked when pinning; a fifth replaces the oldest, after asking). The banner under the header shows one
/// at a time with a bar per pin on its left, the one shown lit; clicking it goes to that
/// message (loading back to it) and moves on to the next. Right-click: go to it, or unpin.
/// Pins and unpins sync with the phone and everyone in the chat; they run out by themselves.
/// </summary>
public sealed partial class MainWindow
{
    private Chat? _pinsChat;

    private void SetupPins()
    {
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) WatchPins(ViewModel.SelectedChat);
        };
        WatchPins(ViewModel.SelectedChat);
    }

    private void WatchPins(Chat? chat)
    {
        if (_pinsChat is not null) _pinsChat.PropertyChanged -= PinsChat_PropertyChanged;
        _pinsChat = chat;
        if (chat is not null) chat.PropertyChanged += PinsChat_PropertyChanged;
        UpdatePinBanner();
    }

    private void PinsChat_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Chat.Pins) or nameof(Chat.PinIndex)) UpdatePinBanner();
    }

    /// <summary>A bar per pin (stacked, filling the banner's height), the shown one in the accent colour.</summary>
    private void UpdatePinBanner()
    {
        PinSegments.Children.Clear();
        if (_pinsChat is not { HasPinnedMessage: true } chat) return;
        var count = chat.Pins.Count;
        if (count < 2) return;   // one pin needs no bars
        var height = (34 - (count - 1) * 2) / (double)count;
        for (var i = 0; i < count; i++)
            PinSegments.Children.Add(new Rectangle
            {
                Width = 2.5,
                Height = height,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Fill = Themed.Brush(i == chat.PinIndex ? "ChatAccentBrush" : "TextFillColorTertiaryBrush"),
            });
    }

    private void PinnedBanner_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (ViewModel.SelectedChat is not { HasPinnedMessage: true } chat) return;
        e.Handled = true;
        var pin = chat.Pins[chat.PinIndex];
        var menu = new MenuFlyout();
        var go = new MenuFlyoutItem { Text = "Go to message", Icon = new FontIcon { Glyph = "\uE8A7" } };
        go.Click += (_, _) => ViewModel.Reveal(pin.Id, pin.Ts);
        var unpin = new MenuFlyoutItem { Text = "Unpin", Icon = Icon(Glyphs.Unpin) };
        unpin.Click += (_, _) =>
        {
            var message = ViewModel.SelectedChat?.Messages.FirstOrDefault(m => m.Id == pin.Id)
                          ?? new Message { Id = pin.Id, Text = pin.Preview, UnixTs = pin.Ts };
            ViewModel.PinMessage(message, false);
        };
        menu.Items.Add(go);
        menu.Items.Add(unpin);
        if (e.TryGetPosition(PinnedBanner, out var at)) menu.ShowAt(PinnedBanner, at);
        else menu.ShowAt(PinnedBanner);
    }

    /// <summary>"Choose how long your pin lasts": 24 hours, 7 days (picked) or 30 days, like WhatsApp.</summary>
    private async Task ChoosePinAsync(Message m)
    {
        var choices = new RadioButtons { SelectedIndex = 1, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var text in new[] { "24 hours", "7 days", "30 days" }) choices.Items.Add(text);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "You can unpin at any time.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(choices);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Choose how long your pin lasts",
            Content = content,
            PrimaryButtonText = "Pin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // A fifth pin replaces the oldest: WhatsApp asks first.
        if (ViewModel.SelectedChat is { Pins.Count: >= 4 })
        {
            var replace = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Replace oldest pin?",
                Content = new TextBlock { Text = "Your new pin will replace the oldest one.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await replace.ShowAsync() != ContentDialogResult.Primary) return;
        }
        int[] seconds = [86_400, 604_800, 2_592_000];
        ViewModel.PinMessage(m, true, seconds[Math.Max(0, choices.SelectedIndex)]);
    }
}
