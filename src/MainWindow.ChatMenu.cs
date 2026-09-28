using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

/// <summary>
/// The chat list's right-click menu (WhatsApp Desktop's set, minus lists), the filter menu,
/// the Starred view, the pinned-message banner, select mode and the toast.
/// </summary>
public sealed partial class MainWindow
{
    private const int MaxPinned = 3;   // WhatsApp's limit

    private void SetupChatMenus()
    {
        ViewModel.UseFavourites(_ui.Favourites);
        ViewModel.FavouritesChanged += _ui.Save;
        ViewModel.Toast += ShowToast;

        // Esc leaves select mode wherever the focus is.
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            if (!ViewModel.IsSelecting) return;
            ViewModel.EndSelect();
            e.Handled = true;
        };
        Root.KeyboardAccelerators.Add(escape);
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // In select mode a tap on a message ticks it instead of opening anything.
        Messages.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, e) =>
        {
            if (!ViewModel.IsSelecting || RowOf(e.OriginalSource as DependencyObject) is not { Tag: Message m }) return;
            ViewModel.ToggleSelect(m);
            e.Handled = true;
        }), handledEventsToo: true);
    }

    // ───────────── Chat list menu ─────────────

    private void Chat_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Chat chat } item) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        var person = !chat.IsGroup;

        if (person && !chat.IsSaved)
            menu.Items.Add(Item("Add to contacts", Glyphs.AddContact, () => OpenNewContact(chat)));
        menu.Items.Add(chat.IsArchived
            ? Item("Unarchive chat", Glyphs.Archive, () => ViewModel.ChatAction(chat, "unarchive"))
            : Item("Archive chat", Glyphs.Archive, () => ViewModel.ChatAction(chat, "archive")));

        menu.Items.Add(MuteMenu(chat));

        if (chat.IsPinned)
            menu.Items.Add(Item("Unpin chat", Glyphs.Unpin, () => ViewModel.SetPinned(chat, false)));
        else
        {
            var full = ViewModel.PinnedCount >= MaxPinned;
            menu.Items.Add(Item(full ? $"Pin chat (up to {MaxPinned})" : "Pin chat", Glyphs.Pin, () => ViewModel.SetPinned(chat, true), enabled: !full));
        }

        menu.Items.Add(chat.HasUnread
            ? Item("Mark as read", Glyphs.MarkRead, () => ViewModel.ChatAction(chat, "markRead"))
            : Item("Mark as unread", Glyphs.MarkUnread, () => ViewModel.ChatAction(chat, "markUnread")));
        menu.Items.Add(chat.IsFavourite
            ? Item("Remove from favourites", Glyphs.HeartFill, () => ViewModel.ToggleFavourite(chat))
            : Item("Add to favourites", Glyphs.Heart, () => ViewModel.ToggleFavourite(chat)));
        if (chat == ViewModel.SelectedChat)
            menu.Items.Add(Item("Close chat", Glyphs.CloseCircle, () => { ViewModel.CloseChat(); ChatList.SelectedItem = null; }));

        menu.Items.Add(new MenuFlyoutSeparator());
        if (person)
            menu.Items.Add(Danger(chat.IsBlocked
                ? Item("Unblock", Glyphs.Block, () => ViewModel.ChatAction(chat, "unblock"))
                : Item("Block", Glyphs.Block, () => _ = ConfirmAsync($"Block {chat.Name}?",
                    "Blocked contacts can't call you or send you messages. They won't be told.", "Block",
                    () => ViewModel.ChatAction(chat, "block")))));
        var clear = Item("Clear chat", null, () => _ = ConfirmAsync("Clear this chat?",
            "All messages in this chat will be removed, here and on your phone.", "Clear chat",
            () => ViewModel.ChatAction(chat, "clear")));
        clear.Icon = Icons.MinusCircle(16);
        menu.Items.Add(Danger(clear));
        menu.Items.Add(Danger(Item("Delete chat", Glyphs.Delete, () => _ = ConfirmAsync($"Delete chat with {chat.Name}?",
            "The chat and its messages will be removed, here and on your phone.", "Delete chat",
            () => ViewModel.ChatAction(chat, "delete")))));

        if (e.TryGetPosition(item, out var point))
            menu.ShowAt(item, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(item);
    }

    /// <summary>Mute for 8 hours / 1 week / always, or unmute (outline bells, like the other menu icons).</summary>
    private MenuFlyoutItemBase MuteMenu(Chat chat)
    {
        if (chat.IsMuted)
        {
            return Item("Unmute notifications", Glyphs.Ringer, () => ViewModel.ChatAction(chat, "unmute"));
        }
        var mute = new MenuFlyoutSubItem { Text = "Mute notifications", Icon = new FontIcon { Glyph = Glyphs.RingerSilent } };
        mute.Items.Add(Item("8 hours", null, () => ViewModel.ChatAction(chat, "mute", TimeSpan.FromHours(8))));
        mute.Items.Add(Item("1 week", null, () => ViewModel.ChatAction(chat, "mute", TimeSpan.FromDays(7))));
        mute.Items.Add(Item("Always", null, () => ViewModel.ChatAction(chat, "mute")));
        return mute;
    }

    /// <summary>A yes/no question before something that can't be undone: the action in red, Cancel grey.</summary>
    private async Task ConfirmAsync(string title, string text, string action, Action confirmed)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
        };
        DangerButtons(dialog);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) confirmed();
    }

    private static readonly Windows.UI.Color Red = Windows.UI.Color.FromArgb(255, 0xE8, 0x11, 0x23);   // bright red

    /// <summary>
    /// The dialog's primary (and secondary) buttons red with white text; Cancel keeps the plain
    /// grey style. The accent button style reads these resources, so only those buttons change.
    /// </summary>
    internal static void DangerButtons(ContentDialog dialog, bool secondaryToo = false)
    {
        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        dialog.PrimaryButtonStyle = accent;
        if (secondaryToo) dialog.SecondaryButtonStyle = accent;
        SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
        var white = Brush(255, 255, 255);
        dialog.Resources["AccentButtonBackground"] = Brush(Red.R, Red.G, Red.B);
        dialog.Resources["AccentButtonBackgroundPointerOver"] = Brush(0xF1, 0x2B, 0x3C);
        dialog.Resources["AccentButtonBackgroundPressed"] = Brush(0xC5, 0x0F, 0x1F);
        dialog.Resources["AccentButtonForeground"] = white;
        dialog.Resources["AccentButtonForegroundPointerOver"] = white;
        dialog.Resources["AccentButtonForegroundPressed"] = Brush(0xF2, 0xD0, 0xCC);
        dialog.Resources["AccentButtonBorderBrush"] = Brush(Red.R, Red.G, Red.B);
        dialog.Resources["AccentButtonBorderBrushPointerOver"] = Brush(0xF1, 0x2B, 0x3C);
        dialog.Resources["AccentButtonBorderBrushPressed"] = Brush(0xC5, 0x0F, 0x1F);
    }

    /// <summary>A red menu item (Block, Clear chat, Delete chat), hover and press included.</summary>
    private static MenuFlyoutItem Danger(MenuFlyoutItem item)
    {
        var red = (Brush)Application.Current.Resources["DangerBrush"];
        item.Foreground = red;
        item.Resources["MenuFlyoutItemForeground"] = red;
        item.Resources["MenuFlyoutItemForegroundPointerOver"] = red;
        item.Resources["MenuFlyoutItemForegroundPressed"] = red;
        item.Resources["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = red;
        if (item.Icon is { } icon) icon.Foreground = red;
        return item;
    }

    // ───────────── Filter ─────────────

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var (filter, text) in new[] { (ChatFilter.All, "All chats"), (ChatFilter.Unread, "Unread"),
                                               (ChatFilter.Favourites, "Favourites"), (ChatFilter.Groups, "Groups") })
        {
            var option = new RadioMenuFlyoutItem { Text = text, GroupName = "chat-filter", IsChecked = ViewModel.Filter == filter };
            option.Click += (_, _) =>
            {
                ViewModel.Filter = filter;
                ListTitle.Text = filter == ChatFilter.All ? "Chats" : text;
            };
            menu.Items.Add(option);
        }
        menu.ShowAt(FilterButton);
    }

    // ───────────── Starred view, pinned banner ─────────────

    private async void Starred_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not StarredItem item || ViewModel.FindChat(item) is not { } chat) return;
        ViewModel.SelectedChat = chat;
        await Task.Delay(350);   // let the conversation load
        ScrollToMessage(item.Message.Id);
    }

    private void PinnedBanner_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedChat is { HasPinnedMessage: true } chat) ScrollToMessage(chat.PinnedMessageId);
    }

    // ───────────── Select mode ─────────────

    private void SelectCancel_Click(object sender, RoutedEventArgs e) => ViewModel.EndSelect();

    private void SelectCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, ViewModel.SelectedMessages.Where(m => m.HasText).Select(m => m.Text));
        if (text.Length > 0) MediaActions.CopyText(text);
        ShowToast(true, "Copied");
        ViewModel.EndSelect();
    }

    private void SelectStar_Click(object sender, RoutedEventArgs e)
    {
        var picked = ViewModel.SelectedMessages.ToList();
        ViewModel.Star(picked, star: !picked.All(m => m.Starred));
        ViewModel.EndSelect();
    }

    private void SelectForward_Click(object sender, RoutedEventArgs e)
    {
        var picked = ViewModel.SelectedMessages.ToList();
        if (picked.Count > 0) _ = ForwardAsync(picked);
    }

    private void SelectDelete_Click(object sender, RoutedEventArgs e)
    {
        var picked = ViewModel.SelectedMessages.ToList();
        if (picked.Count > 0) _ = DeleteAsync(picked);
    }

    // ───────────── Toast ─────────────

    private int _toastVersion;

    private async void ShowToast(bool ok, string text)
    {
        var version = ++_toastVersion;
        ToastIcon.Glyph = ok ? Glyphs.CheckMark : Glyphs.Warning;
        ToastText.Text = text;
        Toast.Opacity = 1;
        await Task.Delay(2600);
        if (version == _toastVersion) Toast.Opacity = 0;
    }
}
