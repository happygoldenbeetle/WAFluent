using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Right-click menu on message bubbles, built for the kind of message that was clicked.
/// (React / forward / delete are still to come.)
/// </summary>
public sealed partial class MainWindow
{
    private void Message_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message message } bubble) return;
        e.Handled = true;

        var selection = (e.OriginalSource as TextBlock)?.SelectedText;
        var menu = BuildMessageMenu(message, bubble, string.IsNullOrEmpty(selection) ? null : selection);
        if (e.TryGetPosition(bubble, out var point))
            menu.ShowAt(bubble, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(bubble);   // keyboard (menu key / Shift+F10)
    }

    private MenuFlyout BuildMessageMenu(Message m, FrameworkElement bubble, string? selection)
    {
        var menu = new MenuFlyout();
        var hasFile = MediaActions.Exists(m);

        if (m.Delivery == Delivery.Failed)
        {
            menu.Items.Add(Item("Try sending again", Glyphs.Refresh, () => ViewModel.RetrySend(m)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (m.Delivery != Delivery.Pending)
        {
            menu.Items.Add(Item("Reply", Glyphs.Reply, () => StartReply(m)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        switch (m.Kind)
        {
            case MessageKind.Text:
                if (selection is not null) menu.Items.Add(Item("Copy selection", Glyphs.Copy, () => MediaActions.CopyText(selection)));
                menu.Items.Add(Item("Copy", Glyphs.Copy, () => MediaActions.CopyText(m.Text)));
                break;

            case MessageKind.Image or MessageKind.Sticker:
                menu.Items.Add(Item("Open", Glyphs.View, () => OpenFromBubble(m, bubble), hasFile));
                menu.Items.Add(Item("Copy image", Glyphs.Copy, () => _ = MediaActions.CopyImageAsync(m.MediaPath!), hasFile));
                if (m.Kind == MessageKind.Image && m.HasText)
                    menu.Items.Add(Item(selection is not null ? "Copy selection" : "Copy caption", Glyphs.Copy,
                                        () => MediaActions.CopyText(selection ?? m.Text)));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Save as…", Glyphs.Save, () => _ = MediaActions.SaveAsAsync(this, m.MediaPath!, m), hasFile));
                if (m.Kind == MessageKind.Image)
                    menu.Items.Add(Item("Open in Photos", Glyphs.OpenExternal, () => MediaActions.OpenExternally(m.MediaPath!), hasFile));
                menu.Items.Add(Item("Show in folder", Glyphs.Folder, () => MediaActions.ShowInFolder(m.MediaPath!), hasFile));
                break;

            case MessageKind.Voice:
                var playing = AudioPlayback.Current == m && AudioPlayback.IsPlaying;
                menu.Items.Add(Item(playing ? "Pause" : "Play", playing ? Glyphs.Pause : Glyphs.Play, () => AudioPlayback.Toggle(m), hasFile));
                menu.Items.Add(SpeedMenu());
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Save as…", Glyphs.Save, () => _ = MediaActions.SaveAsAsync(this, m.MediaPath!, m), hasFile));
                menu.Items.Add(Item("Show in folder", Glyphs.Folder, () => MediaActions.ShowInFolder(m.MediaPath!), hasFile));
                break;

            case MessageKind.File:
                menu.Items.Add(Item("Copy file name", Glyphs.Copy, () => MediaActions.CopyText(m.FileName)));
                if (m.HasText) menu.Items.Add(Item("Copy caption", Glyphs.Copy, () => MediaActions.CopyText(m.Text)));
                break;
        }

        if (m.MediaFailed)
            menu.Items.Add(Item("Retry download", Glyphs.Refresh, () => ViewModel.RetryDownload(m)));

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Message info", Glyphs.Info, () => ShowMessageInfo(m, bubble)));
        return menu;
    }

    private static MenuFlyoutSubItem SpeedMenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Playback speed", Icon = new FontIcon { Glyph = Glyphs.Speed } };
        foreach (var rate in new[] { 1.0, 1.5, 2.0 })
        {
            var option = new RadioMenuFlyoutItem { Text = $"{rate:0.#}×", GroupName = "voice-rate", IsChecked = AudioPlayback.Rate == rate };
            option.Click += (_, _) => AudioPlayback.SetRate(rate);
            sub.Items.Add(option);
        }
        return sub;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>"Open" from the menu flies out of the same picture a click would.</summary>
    private void OpenFromBubble(Message m, FrameworkElement bubble)
    {
        var picture = FindDescendant(bubble, e => e != bubble && ReferenceEquals(e.Tag, m)) ?? bubble;
        OpenViewer(m, picture);
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, Func<FrameworkElement, bool> match)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && match(fe)) return fe;
            if (FindDescendant(child, match) is { } found) return found;
        }
        return null;
    }

    /// <summary>Who/when/status (and media details) in a small flyout next to the bubble.</summary>
    private void ShowMessageInfo(Message m, FrameworkElement bubble)
    {
        var chat = ViewModel.SelectedChat;
        var from = m.IsOutgoing ? "You" : m.SenderName.Length > 0 ? m.SenderName : chat?.Name ?? "";
        var lines = new List<(string Label, string Value)>
        {
            (m.IsOutgoing ? "Sent by" : "From", from),
            (m.IsOutgoing ? "Sent" : "Received", m.Timestamp.ToString("dddd, d MMMM yyyy 'at' H:mm")),
        };
        if (m.IsOutgoing)
            lines.Add(("Status", m.Delivery switch { Delivery.Read => "Read", Delivery.Delivered => "Delivered", _ => "Sent" }));
        switch (m.Kind)
        {
            case MessageKind.Voice:
                lines.Add(("Voice message", Format.Duration(TimeSpan.FromSeconds(m.Seconds))));
                break;
            case MessageKind.Image or MessageKind.Sticker:
                lines.Add((m.Kind == MessageKind.Image ? "Photo" : "Sticker",
                           MediaActions.Exists(m) ? "Downloaded" : m.MediaFailed ? "Unavailable" : "Downloading…"));
                break;
            case MessageKind.File:
                lines.Add(("File", m.FileName));
                break;
        }

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, MaxWidth = 360 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < lines.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = lines[i].Label, Opacity = 0.7 };
            var value = new TextBlock { Text = lines[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Message info", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16 });
        panel.Children.Add(grid);
        new Flyout { Content = panel, Placement = FlyoutPlacementMode.Auto }.ShowAt(bubble);
    }
}
