using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Settings (quick reactions, developer mode) and the chat list's right-click menu.
/// </summary>
public sealed partial class MainWindow
{
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
                Content = new TextBlock { Text = _ui.QuickReactions[i], FontSize = 26, FontFamily = Controls.EmojiPicker.EmojiFont },
            };
            ToolTipService.SetToolTip(slot, index == 0 ? "First: also used when you double-click a message" : "Click to change");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slot, $"Quick reaction {index + 1}: {_ui.QuickReactions[i]}");
            slot.Click += (_, _) => OpenEmojiPicker(slot, emoji =>
            {
                var list = _ui.QuickReactions.ToList();
                var existing = list.IndexOf(emoji);
                if (existing >= 0) list[existing] = list[index];   // already there: swap places
                list[index] = emoji;
                _ui.QuickReactions = [.. list];
                _ui.Save();
                BuildQuickReactionSlots();
            }, closeOnPick: true);
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
