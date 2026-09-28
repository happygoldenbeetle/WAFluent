using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Settings (quick reactions), picking any emoji through the Windows emoji panel, and the
/// chat list's right-click menu (pin / unpin, synced with the phone).
/// </summary>
public sealed partial class MainWindow
{
    // ───────────── Any emoji: the Windows emoji panel ─────────────

    private Action<string>? _emojiPicked;

    /// <summary>
    /// Opens the Windows emoji panel next to <paramref name="near"/> and calls
    /// <paramref name="picked"/> with the first emoji chosen. The panel types into an
    /// invisible text box (it only works with a focused text field).
    /// </summary>
    private void PickEmoji(FrameworkElement near, Action<string> picked)
    {
        var at = near.TransformToVisual(Root).TransformPoint(default);
        Canvas.SetLeft(EmojiCatcher, at.X);
        Canvas.SetTop(EmojiCatcher, at.Y);
        _emojiPicked = null;
        EmojiCatcher.Text = "";
        _emojiPicked = picked;
        EmojiCatcher.IsTabStop = true;   // focusable only while picking; never reached with Tab
        if (!EmojiCatcher.Focus(FocusState.Programmatic)) return;
        // Once the focus change has settled, so the panel attaches to the box.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, OpenEmojiPanel);
    }

    private void EmojiCatcher_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_emojiPicked is not { } picked || EmojiCatcher.Text.Length == 0) return;
        var emoji = StringInfo.GetNextTextElement(EmojiCatcher.Text);   // one emoji, even a ZWJ family
        _emojiPicked = null;
        EmojiCatcher.Text = "";
        ChatList.Focus(FocusState.Programmatic);   // off the text box, which closes the panel
        picked(emoji);
    }

    private void EmojiCatcher_LostFocus(object sender, RoutedEventArgs e)
    {
        _emojiPicked = null;
        EmojiCatcher.IsTabStop = false;
    }

    private static void OpenEmojiPanel()
    {
        const byte VK_LWIN = 0x5B, VK_OEM_PERIOD = 0xBE;
        const uint KEYUP = 0x2;
        keybd_event(VK_LWIN, 0, 0, 0);
        keybd_event(VK_OEM_PERIOD, 0, 0, 0);
        keybd_event(VK_OEM_PERIOD, 0, KEYUP, 0);
        keybd_event(VK_LWIN, 0, KEYUP, 0);
    }

    // ───────────── Settings: quick reactions ─────────────

    private static readonly string[] DefaultQuickReactions = ["❤️", "👍", "😂", "😮", "😢"];

    /// <summary>The five slots in Settings; click one to swap its emoji.</summary>
    private void BuildQuickReactionSlots()
    {
        QuickReactionSlots.Children.Clear();
        for (var i = 0; i < _ui.QuickReactions.Length; i++)
        {
            var index = i;
            var slot = new Button
            {
                Width = 48,
                Height = 48,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Content = new TextBlock { Text = _ui.QuickReactions[i], FontSize = 26 },
            };
            ToolTipService.SetToolTip(slot, index == 0 ? "First: also used when you double-click a message" : "Click to change");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slot, $"Quick reaction {index + 1}: {_ui.QuickReactions[i]}");
            slot.Click += (_, _) => PickEmoji(slot, emoji =>
            {
                var list = _ui.QuickReactions.ToList();
                var existing = list.IndexOf(emoji);
                if (existing >= 0) list[existing] = list[index];   // already there: swap places
                list[index] = emoji;
                _ui.QuickReactions = [.. list];
                _ui.Save();
                BuildQuickReactionSlots();
            });
            QuickReactionSlots.Children.Add(slot);
        }
    }

    private void DeveloperMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ui.DeveloperMode == DeveloperModeSwitch.IsOn) return;
        _ui.DeveloperMode = DeveloperModeSwitch.IsOn;
        _ui.Save();
        ApplyDeveloperMode();
    }

    private void ApplyDeveloperMode()
    {
        Controls.Redact.SetEnabled(_ui.DeveloperMode);
        ViewModel.HideProfilePhoto = _ui.DeveloperMode;   // your own photo on the rail
    }

    private void ResetQuickReactions_Click(object sender, RoutedEventArgs e)
    {
        _ui.QuickReactions = [.. DefaultQuickReactions];
        _ui.Save();
        BuildQuickReactionSlots();
    }

    // ───────────── Chat list: pin / unpin ─────────────

    private const int MaxPinned = 3;   // WhatsApp's limit

    private void Chat_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Chat chat } item) return;
        e.Handled = true;

        var menu = new MenuFlyout();
        if (chat.IsPinned)
        {
            menu.Items.Add(Item("Unpin chat", Glyphs.Unpin, () => ViewModel.SetPinned(chat, false)));
        }
        else
        {
            var full = ViewModel.PinnedCount >= MaxPinned;
            var pin = Item(full ? $"Pin chat (up to {MaxPinned})" : "Pin chat", Glyphs.Pin, () => ViewModel.SetPinned(chat, true), enabled: !full);
            menu.Items.Add(pin);
        }

        if (e.TryGetPosition(item, out var point))
            menu.ShowAt(item, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(item);
    }
}
