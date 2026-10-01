using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

        // Right-click on an email address or link: its own menu, not the message's.
        if (LinkUnderPointer(bubble, e) is var (text, at, link))
        {
            var linkMenu = new MenuFlyout();
            if (link.IsEmail)
            {
                linkMenu.Items.Add(Item("Send email", Glyphs.Mail, () => Controls.LinkText.Open(link)));
                linkMenu.Items.Add(Item("Copy email", Glyphs.Copy, () => MediaActions.CopyText(link.Text)));
            }
            else
            {
                linkMenu.Items.Add(Item("Open link", Glyphs.OpenExternal, () => Controls.LinkText.Open(link)));
                linkMenu.Items.Add(Item("Copy link", Glyphs.Copy, () => MediaActions.CopyText(link.Text)));
            }
            linkMenu.ShouldConstrainToRootBounds = true;
            linkMenu.ShowAt(text, new FlyoutShowOptions { Position = at });
            return;
        }

        var selection = (e.OriginalSource as TextBlock)?.SelectedText;
        var menu = BuildMessageMenu(message, bubble, string.IsNullOrEmpty(selection) ? null : selection);
        // Inside the window, so the reaction row's positions line up with the bubble for the flight.
        menu.ShouldConstrainToRootBounds = true;
        if (e.TryGetPosition(bubble, out var point))
            menu.ShowAt(bubble, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(bubble);   // keyboard (menu key / Shift+F10)
    }

    /// <summary>The email address or link the right-click landed on, if any (in the text or a caption).</summary>
    private static (TextBlock Text, Windows.Foundation.Point At, Controls.LinkText.Target Link)? LinkUnderPointer(FrameworkElement bubble, ContextRequestedEventArgs e)
    {
        foreach (var text in Descendants(bubble).OfType<TextBlock>())
        {
            if (Controls.LinkText.GetMessage(text) is null || !e.TryGetPosition(text, out var at)) continue;
            if (at.X < 0 || at.Y < -4 || at.X > text.ActualWidth || at.Y > text.ActualHeight + 4) continue;
            if (Controls.LinkText.HitTest(text, at) is { } link) return (text, at, link);
        }
        return null;
    }

#if DEBUG
    /// <summary>WAFLUENT_SELFTEST=menu: opens the menu of the last text message on screen.</summary>
    private void SelfTestMenu()
    {
        var bubble = Descendants(Messages).OfType<FrameworkElement>().LastOrDefault(f => f.Tag is Message { Kind: MessageKind.Text });
        if (bubble?.Tag is not Message m) return;
        var menu = BuildMessageMenu(m, bubble, null);
        menu.ShouldConstrainToRootBounds = true;
        menu.ShowAt(bubble);
    }

    /// <summary>
    /// Debug self-check (WAFLUENT_SELFTEST=links): hit-tests the middle of every link on screen
    /// and a point beside it, writing the results to %TEMP%\wafluent-selftest.txt.
    /// </summary>
    private void SelfTestLinks()
    {
        var lines = new List<string>();
        foreach (var text in Descendants(Messages).OfType<TextBlock>().Where(t => Controls.LinkText.GetMessage(t) is not null))
            foreach (var link in text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Hyperlink>())
            {
                var start = link.ContentStart.GetPositionAtOffset(2, Microsoft.UI.Xaml.Documents.LogicalDirection.Forward).GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                var inside = new Windows.Foundation.Point(start.X + 3, start.Y + start.Height / 2);
                var before = link.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                var outside = new Windows.Foundation.Point(Math.Max(0, before.X - 30), before.Y + before.Height / 2);
                lines.Add($"link rect {start} inside -> {Controls.LinkText.HitTest(text, inside)?.Uri ?? "none"}; 30px before -> {Controls.LinkText.HitTest(text, outside)?.Uri ?? "none"}");
            }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines.Count > 0 ? lines : ["no links on screen"]);
    }
#endif

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
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
        else if (m.Delivery != Delivery.Pending && !m.IsDeleted)
        {
            menu.Items.Add(ReactionRow(menu, m, bubble));
            menu.Items.Add(new MenuFlyoutSeparator());
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
                menu.Items.Add(Item("Open", Glyphs.OpenExternal, () => WhenDownloaded(m, MediaActions.OpenExternally)));
                menu.Items.Add(Item("Save as…", Glyphs.Save, () => WhenDownloaded(m, path => _ = MediaActions.SaveAsAsync(this, path, m))));
                menu.Items.Add(Item("Show in folder", Glyphs.Folder, () => MediaActions.ShowInFolder(m.MediaPath!), hasFile));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Copy file name", Glyphs.Copy, () => MediaActions.CopyText(m.FileName)));
                if (m.HasText) menu.Items.Add(Item("Copy caption", Glyphs.Copy, () => MediaActions.CopyText(m.Text)));
                break;

            case MessageKind.Video:
                menu.Items.Add(Item("Play", Glyphs.Play, () => WhenDownloaded(m, _ => OpenVideo(m))));
                if (m.HasText) menu.Items.Add(Item("Copy caption", Glyphs.Copy, () => MediaActions.CopyText(m.Text)));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item("Save as…", Glyphs.Save, () => WhenDownloaded(m, path => _ = MediaActions.SaveAsAsync(this, path, m))));
                menu.Items.Add(Item("Open in Media Player", Glyphs.OpenExternal, () => WhenDownloaded(m, MediaActions.OpenExternally)));
                menu.Items.Add(Item("Show in folder", Glyphs.Folder, () => MediaActions.ShowInFolder(m.MediaPath!), hasFile));
                break;

            case MessageKind.Location:
                menu.Items.Add(Item("Open in Maps", Glyphs.Location, () => OpenLocation(m)));
                menu.Items.Add(Item("Copy coordinates", Glyphs.Copy, () => MediaActions.CopyText(Coordinates(m))));
                break;

            case MessageKind.Contact:
                foreach (var card in m.Contacts.Where(c => c.Phones.Count > 0))
                    menu.Items.Add(Item(m.Contacts.Count > 1 ? $"Copy {card.Name}'s number" : "Copy number", Glyphs.Copy, () => MediaActions.CopyText(card.Phone)));
                break;

            case MessageKind.Poll:
                menu.Items.Add(Item("Copy", Glyphs.Copy, () => MediaActions.CopyText(m.Text + "\n" + string.Join("\n", m.PollOptions.Select(o => "• " + o.Name)))));
                break;
        }

        if (m.MediaFailed)
            menu.Items.Add(Item("Retry download", Glyphs.Refresh, () => ViewModel.RetryDownload(m)));

        var sent = m.Delivery is not (Delivery.Pending or Delivery.Failed);
        if (sent && !m.IsDeleted)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var forward = Item("Forward", Glyphs.Forward, () => _ = ForwardAsync([m]));
            forward.Icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            forward.Icon.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = -1 };   // Reply, pointing the other way
            menu.Items.Add(forward);
            var pinned = ViewModel.SelectedChat?.HasPin(m.Id) == true;
            menu.Items.Add(Item(pinned ? "Unpin" : "Pin", pinned ? Glyphs.Unpin : Glyphs.Pin,
                () => { if (pinned) ViewModel.PinMessage(m, false); else _ = ChoosePinAsync(m); }));
            menu.Items.Add(Item(m.Starred ? "Unstar" : "Star", m.Starred ? Glyphs.StarFill : Glyphs.Star, () => ViewModel.Star([m], !m.Starred)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Select", Glyphs.Select, () => ViewModel.BeginSelect(m)));
        if (CanEdit(m)) menu.Items.Add(Item("Edit", "\uE70F", () => BeginEdit(m)));
        if (m.IsOutgoing) menu.Items.Add(Item("Message info", Glyphs.Info, () => OpenMessageInfo(m)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (!m.IsOutgoing && !m.IsDeleted)
            menu.Items.Add(Item("Report", Glyphs.Report, () => _ = ConfirmAsync("Report this message?",
                "The message and who sent it are sent to WhatsApp. The sender isn't told.", "Report", () => ViewModel.Report(m))));
        menu.Items.Add(Item("Delete", Glyphs.Delete, () => _ = DeleteAsync([m])));
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

    /// <summary>A menu icon; Segoe's pins lean 45°, so they're turned upright.</summary>
    private static FontIcon Icon(string glyph)
    {
        var icon = new FontIcon { Glyph = glyph };

        if (glyph == Glyphs.Pin || glyph == Glyphs.Unpin)
        {
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            icon.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = -45 };
        }
        return icon;
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = glyph is null ? null : Icon(glyph), IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Pick chats to forward to, in the same card as Send contacts: search, recent chats
    /// (groups too) with a tick each, the picked names along the bottom and send.
    /// </summary>
    private Task ForwardAsync(IReadOnlyList<Message> messages)
    {
        _forwarding = messages;
        _meRow = null;
        ContactsTitle.Text = messages.Count == 1 ? "Forward message to" : $"Forward {messages.Count} messages to";
        AutomationProperties.SetName(ContactsSend, "Forward");
        _contacts = ViewModel.ForwardTargets()
            .Select(c => new ContactRow
            {
                Name = c.Name,
                Phone = ContactPhone(c),
                Subtitle = c.IsGroup ? "" : c.PhoneCode.Length > 0 ? $"{c.PhoneCode} {c.PhoneNational}" : "",
                AvatarPath = c.AvatarPath,
                Chat = c,
            })
            .ToList();
        ContactSearch.Text = "";
        FilterContacts();
        UpdatePickedContacts();
        ShowSheet(ContactsCard);
        ContactSearch.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    /// <summary>Delete for me, or for everyone when they're all yours (and recent enough for WhatsApp).</summary>
    private async Task DeleteAsync(IReadOnlyList<Message> messages)
    {
        var mine = messages.All(m => m.IsOutgoing && !m.IsDeleted && DateTime.Now - m.Timestamp < TimeSpan.FromDays(2));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = messages.Count == 1 ? "Delete message?" : $"Delete {messages.Count} messages?",
            Content = new TextBlock { Text = mine ? "Delete for everyone removes it from the chat for everyone in it." : "It will be removed from this PC and your phone.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = mine ? "Delete for everyone" : "Delete for me",
            SecondaryButtonText = mine ? "Delete for me" : "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.DefaultButton = ContentDialogButton.None;
        DangerButtons(dialog, secondaryToo: true);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        var forEveryone = mine && result == ContentDialogResult.Primary;
        ViewModel.Delete(messages, forEveryone);
        ViewModel.EndSelect();
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

}
