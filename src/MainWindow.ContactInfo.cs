using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// The right-hand panel: Contact / Group info (click the name or photo in the header) and
/// New contact (its Add button, or "Add to contacts" in the chat list menu).
/// </summary>
public sealed partial class MainWindow
{
    private Chat? _infoChat;
    private bool _newContactFromInfo;

    private void Header_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (InfoPanel.Visibility == Visibility.Visible && ContactInfoView.Visibility == Visibility.Visible) CloseInfo();
        else OpenInfo();
    }

    private void OpenInfo()
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        Track(chat);
        ContactInfoView.Visibility = Visibility.Visible;
        NewContactView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        DisappearingView.Visibility = Visibility.Collapsed;
        ChatStarredView.Visibility = Visibility.Collapsed;
        InfoCloseIcon.Glyph = "\uE711";
        SearchView.Visibility = Visibility.Collapsed;
        MessageInfoView.Visibility = Visibility.Collapsed;
        InfoTitle.Text = chat.IsGroup ? "Group info" : "Contact info";
        RebuildInfo();
        if (_galleryData?.Chat != chat) ViewModel.LoadChatMedia(chat);   // the count and the strip
        InfoPanel.Visibility = Visibility.Visible;
        PlaceInfoPanel();
        ScrollInfoToTop();
    }

    /// <summary>Opens at the photo, not wherever the last chat's panel was scrolled to.</summary>
    private async void ScrollInfoToTop()
    {
        ContactInfoView.ChangeView(null, 0, null, disableAnimation: true);
        await Task.Delay(120);   // after the rows and the photo have been laid out
        ContactInfoView.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>
    /// Beside the conversation when there's room; floating over its right side (with a shadow)
    /// when the conversation would be squeezed below a readable width.
    /// </summary>
    private void PlaceInfoPanel()
    {
        if (InfoPanel.Visibility != Visibility.Visible) return;
        var conversation = ContentGrid.ActualWidth - ChatListColumn.Width.Value;
        var overlay = conversation - InfoPanel.Width < 460;
        if (overlay == (Grid.GetColumn(InfoPanel) == 1)) return;
        Grid.SetColumn(InfoPanel, overlay ? 1 : 2);
        InfoPanel.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        InfoPanel.Shadow = overlay ? new ThemeShadow() : null;
        InfoPanel.Translation = overlay ? new System.Numerics.Vector3(0, 0, 32) : System.Numerics.Vector3.Zero;
    }

    private void CloseInfo()
    {
        InfoPanel.Visibility = Visibility.Collapsed;
        Track(null);
    }

    private void InfoClose_Click(object sender, RoutedEventArgs e)
    {
        // Leaving New contact or Media, links and docs goes back to Contact info when that's where it came from.
        if (NewContactView.Visibility == Visibility.Visible && _newContactFromInfo) OpenInfo();
        else if (GalleryView.Visibility == Visibility.Visible || DisappearingView.Visibility == Visibility.Visible
                 || ChatStarredView.Visibility == Visibility.Visible) OpenInfo();
        else CloseInfo();
    }

    /// <summary>Follows the open chat: its state changes redraw the rows; another chat redraws everything.</summary>
    private void Track(Chat? chat)
    {
        if (_infoChat is not null) _infoChat.PropertyChanged -= InfoChat_PropertyChanged;
        _infoChat = chat;
        if (chat is not null) chat.PropertyChanged += InfoChat_PropertyChanged;
    }

    private void InfoChat_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Chat.Ephemeral) && DisappearingView.Visibility == Visibility.Visible) ShowDisappearingChoice();
        if ((e.PropertyName is nameof(Chat.IsMuted) or nameof(Chat.IsFavourite) or nameof(Chat.IsBlocked) or nameof(Chat.IsSaved) or nameof(Chat.Name) or nameof(Chat.Ephemeral))
            && ContactInfoView.Visibility == Visibility.Visible)
            RebuildInfo();
    }

    private void SetupInfoPanel()
    {
        ContentGrid.SizeChanged += (_, _) => PlaceInfoPanel();
        ChatListColumn.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, (_, _) => PlaceInfoPanel());
        // `--panel info` / `--panel contact` opens the panel at start (screenshots, trying it out).
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--panel");
        if (i >= 0 && i + 1 < args.Length)
        {
            var which = args[i + 1];
            Root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (which == "contact" && ViewModel.SelectedChat is { } chat) OpenNewContact(chat);
                else OpenInfo();
            });
        }

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModel.SelectedChat) || InfoPanel.Visibility != Visibility.Visible) return;
            if (ViewModel.SelectedChat is null) CloseInfo();
            else OpenInfo();
        };
    }

    // ───────────── Contact info ─────────────

    private void RebuildInfo()
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        var person = !chat.IsGroup;

        InfoActions.Children.Clear();
        InfoActions.Children.Add(ActionButton(Glyphs.Phone2, "Voice", () => StartCall(video: false)));
        InfoActions.Children.Add(ActionButton(Glyphs.VideoCall, "Video", () => StartCall(video: true)));
        if (person && !chat.IsSaved)
            InfoActions.Children.Add(ActionButton(Glyphs.AddContact, "Add", () => OpenNewContact(chat)));

        InfoRows.Children.Clear();
        InfoRows.Children.Add(Divider());
        var gallery = _galleryData?.Chat == chat ? _galleryData : null;
        InfoRows.Children.Add(Row(Glyphs.Media, "Media, links and docs", trailing: gallery?.Count.ToString() ?? ViewModel.MediaCount(chat).ToString(),
                                  action: OpenGallery));
        if (gallery is { Media.Count: > 0 }) InfoRows.Children.Add(GalleryStrip(gallery));
        InfoRows.Children.Add(Divider());
        InfoRows.Children.Add(Row(Glyphs.Star, "Starred messages", action: OpenChatStarred));
        if (!chat.IsBlocked)
            InfoRows.Children.Add(Row("\uE916", "Disappearing messages", detail: chat.EphemeralText, action: OpenDisappearing));
        InfoRows.Children.Add(Row(chat.IsMuted ? Glyphs.RingerSilent : Glyphs.Ringer, "Notification settings",
                                  detail: chat.IsMuted ? "Muted" : null, flyout: NotificationMenu(chat)));
        InfoRows.Children.Add(Row(Glyphs.Lock, "Encryption", detail: "Messages are end-to-end encrypted."));
        InfoRows.Children.Add(Divider());
        InfoRows.Children.Add(chat.IsFavourite
            ? Row(Glyphs.HeartFill, "Remove from favourites", action: () => ViewModel.ToggleFavourite(chat))
            : Row(Glyphs.Heart, "Add to favourites", action: () => ViewModel.ToggleFavourite(chat)));
        InfoRows.Children.Add(Row(Glyphs.Export, "Export chat", action: () => _ = ExportChatAsync(chat)));
        InfoRows.Children.Add(Row(null, "Clear chat", danger: true, icon: Icons.MinusCircle(), action: () => _ = ConfirmAsync("Clear this chat?",
            "All messages in this chat will be removed, here and on your phone.", "Clear chat", () => ViewModel.ChatAction(chat, "clear"))));
        if (person)
        {
            InfoRows.Children.Add(chat.IsBlocked
                ? Row(Glyphs.Block, $"Unblock {chat.Name}", danger: true, action: () => ViewModel.ChatAction(chat, "unblock"))
                : Row(Glyphs.Block, $"Block {chat.Name}", danger: true, action: () => _ = ConfirmAsync($"Block {chat.Name}?",
                    "Blocked contacts can't call you or send you messages. They won't be told.", "Block", () => ViewModel.ChatAction(chat, "block"))));
            InfoRows.Children.Add(Row(Glyphs.Report, $"Report {chat.Name}", danger: true, action: () => _ = ConfirmAsync($"Report {chat.Name}?",
                "Their most recent message is sent to WhatsApp. They won't be told.", "Report", () => ViewModel.ReportContact(chat))));
        }
        InfoRows.Children.Add(Row(Glyphs.Delete, "Delete chat", danger: true, action: () => _ = ConfirmAsync($"Delete chat with {chat.Name}?",
            "The chat and its messages will be removed, here and on your phone.", "Delete chat", () => ViewModel.ChatAction(chat, "delete"))));
    }

    private MenuFlyout NotificationMenu(Chat chat)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MuteMenu(chat));
        return menu;
    }

    /// <summary>Round-cornered square with an icon, the label underneath (Voice / Video / Add).</summary>
    private static StackPanel ActionButton(string glyph, string label, Action action)
    {
        var button = new Button
        {
            Width = 66,
            Height = 50,
            CornerRadius = new CornerRadius(25),
            Content = new FontIcon { Glyph = glyph, FontSize = 18 },
            BorderThickness = new Thickness(0),
            Background = Helpers.Themed.Brush("SubtleFillColorSecondaryBrush"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(button);
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    /// <summary>One line of the panel: icon, text (+ grey detail), optional number on the right. Red for the destructive ones.</summary>
    private static Button Row(string? glyph, string text, string? detail = null, string? trailing = null, bool danger = false,
                              Action? action = null, IconElement? icon = null, FlyoutBase? flyout = null)
    {
        var color = danger ? Helpers.Themed.Brush("DangerBrush") : null;
        var grid = new Grid { ColumnSpacing = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = icon ?? new FontIcon { Glyph = glyph ?? "", FontSize = 18 };
        mark.VerticalAlignment = VerticalAlignment.Center;
        if (color is not null) mark.Foreground = color;
        else mark.Foreground = Helpers.Themed.Brush("TextFillColorSecondaryBrush");
        grid.Children.Add(mark);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var title = new TextBlock { Text = text, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        if (color is not null) title.Foreground = color;
        texts.Children.Add(title);
        if (detail is not null)
            texts.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = Helpers.Themed.Brush("TextFillColorSecondaryBrush"),
            });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (trailing is not null)
        {
            var right = new TextBlock
            {
                Text = trailing, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Helpers.Themed.Brush("TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 14, 12, 14),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsEnabled = action is not null || flyout is not null || trailing is not null || detail is not null,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        if (flyout is not null) button.Flyout = flyout;
        else if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static Microsoft.UI.Xaml.Shapes.Rectangle Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 8, 0, 8),
        Fill = Helpers.Themed.Brush("DividerStrokeColorDefaultBrush"),
    };

    private async Task ExportChatAsync(Chat chat)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"WhatsApp Chat with {chat.Name}", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Text", [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is { } file) ViewModel.ExportChat(chat, file.Path);
    }

    // ───────────── New contact ─────────────

    /// <summary>Opens the New contact form for a 1:1 chat, first name filled with the name they chose.</summary>
    private void OpenNewContact(Chat chat)
    {
        _newContactFromInfo = InfoPanel.Visibility == Visibility.Visible && ContactInfoView.Visibility == Visibility.Visible;
        if (ViewModel.SelectedChat != chat)
        {
            ViewModel.SelectedChat = chat;
            ChatList.SelectedItem = chat;
        }
        Track(chat);
        FirstNameBox.Text = chat.PushName;
        LastNameBox.Text = "";
        CountryBox.ItemsSource = new[] { chat.PhoneRegion.Length > 0 ? $"{chat.PhoneRegion} {chat.PhoneCode}" : chat.PhoneCode };
        CountryBox.SelectedIndex = 0;
        PhoneBox.Text = chat.PhoneNational.Length > 0 ? chat.PhoneNational : chat.Name;
        SyncToPhoneSwitch.IsOn = false;
        SaveContactButton.IsEnabled = FirstNameBox.Text.Trim().Length > 0;
        InfoTitle.Text = "New contact";
        ContactInfoView.Visibility = Visibility.Collapsed;
        NewContactView.Visibility = Visibility.Visible;
        GalleryView.Visibility = Visibility.Collapsed;
        DisappearingView.Visibility = Visibility.Collapsed;
        ChatStarredView.Visibility = Visibility.Collapsed;
        InfoCloseIcon.Glyph = "\uE711";
        SearchView.Visibility = Visibility.Collapsed;
        MessageInfoView.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Visible;
        PlaceInfoPanel();
        FirstNameBox.Focus(FocusState.Programmatic);
    }

    private void ContactName_TextChanged(object sender, TextChangedEventArgs e) =>
        SaveContactButton.IsEnabled = FirstNameBox.Text.Trim().Length > 0;

    private void SaveContact_Click(object sender, RoutedEventArgs e)
    {
        if (_infoChat is not { } chat) return;
        ViewModel.SaveContact(chat, FirstNameBox.Text.Trim(), LastNameBox.Text.Trim(), SyncToPhoneSwitch.IsOn);
        if (_newContactFromInfo) OpenInfo();
        else CloseInfo();
    }
}
