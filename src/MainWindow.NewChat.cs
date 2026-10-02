using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Controls;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// New chat (the pencil beside "Chats"), like WhatsApp: over the chat list, your contacts by
/// letter with a search box, "New group", "New contact" and "Message yourself" on top, and a
/// number pad (the grid button) to start a chat with any number.
///   New group   : pick members (chips along the top, ✕ to drop one; a blocked contact asks
///                 first), the arrow, then the group's name.
///   New contact : name, country code and number, optionally synced to the phone.
/// Back steps out of a page, then closes the pane.
/// </summary>
public sealed partial class MainWindow
{
    private enum NewChatPage { Contacts, Number, Contact, Members, Subject }

    private NewChatPage _newChatPage;
    private List<ContactRow> _newChatContacts = [];
    private readonly List<ContactRow> _groupMembers = [];
    private bool _newChatWired;

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (!_newChatWired)
        {
            _newChatWired = true;
            EnsureContacts();
            BuildNumberPad(NumberPad, NumberBox);
        }
        _groupMembers.Clear();
        _addingTo = null;
        NewChatSearch.Text = "";
        NewChatPane.Visibility = Visibility.Visible;
        SetChatListHidden(true);
        ShowNewChatPage(NewChatPage.Contacts);
        ViewModel.LoadContacts();
    }

    private bool _contactsWired;

    /// <summary>Keeps your saved contacts (New chat, and the Calls page's New call) as they're loaded.</summary>
    private void EnsureContacts()
    {
        if (_contactsWired) return;
        _contactsWired = true;
        ViewModel.ContactsLoaded += contacts =>
        {
            _newChatContacts = contacts.Select(c => new ContactRow
            {
                Name = c.Name,
                Phone = c.Phone,
                ChatId = c.ChatId,
                IsBlocked = c.Blocked,
                Subtitle = c.Blocked ? "Contact is blocked" : c.Phone.Length > 0 ? "+" + c.Phone : "",
                AvatarPath = c.Avatar,
            }).ToList();
            if (NewChatPane.Visibility == Visibility.Visible) FilterNewChat();
            if (CallsSubPane.Visibility == Visibility.Visible && _callsPage == CallsPage.NewCall) FillCallsPick();
        };
    }

    /// <summary>The list stays where it is under a pane over it (New chat, a Calls page), unseen and unclickable.</summary>
    private void SetChatListHidden(bool hidden)
    {
        foreach (var child in ChatListPane.Children)
        {
            if (ReferenceEquals(child, NewChatPane) || ReferenceEquals(child, CallsSubPane)) continue;
            child.Opacity = hidden ? 0 : 1;
            child.IsHitTestVisible = !hidden;
        }
    }

    private void CloseNewChat()
    {
        NewChatPane.Visibility = Visibility.Collapsed;
        SetChatListHidden(false);
    }

    private void ShowNewChatPage(NewChatPage page)
    {
        _newChatPage = page;
        NewChatTitle.Text = page switch
        {
            NewChatPage.Number => "Phone number",
            NewChatPage.Contact => "New contact",
            NewChatPage.Members => _addingTo is null ? "Add group members" : "Add members",
            NewChatPage.Subject => "New group",
            _ => "New chat",
        };
        var list = page is NewChatPage.Contacts or NewChatPage.Members;
        NewChatListPage.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        NewChatNumberPage.Visibility = page == NewChatPage.Number ? Visibility.Visible : Visibility.Collapsed;
        NewChatContactPage.Visibility = page == NewChatPage.Contact ? Visibility.Visible : Visibility.Collapsed;
        NewChatSubjectPage.Visibility = page == NewChatPage.Subject ? Visibility.Visible : Visibility.Collapsed;
        NewChatDialpad.Visibility = page == NewChatPage.Contacts ? Visibility.Visible : Visibility.Collapsed;
        GroupChipsArea.Visibility = page == NewChatPage.Members ? Visibility.Visible : Visibility.Collapsed;
        GroupNext.Visibility = page == NewChatPage.Members && _groupMembers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list)
        {
            FilterNewChat();
            NewChatSearch.Focus(FocusState.Programmatic);
        }
    }

    private void NewChatBack_Click(object sender, RoutedEventArgs e)
    {
        switch (_newChatPage)
        {
            case NewChatPage.Contacts: CloseNewChat(); break;
            case NewChatPage.Members when _addingTo is not null: CloseNewChat(); break;
            case NewChatPage.Subject: ShowNewChatPage(NewChatPage.Members); break;
            default:
                NewChatSearch.Text = "";
                ShowNewChatPage(NewChatPage.Contacts);
                break;
        }
    }

    // ───── Contacts (and picking group members) ─────

    private void NewChatSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FilterNewChat();

    private void FilterNewChat()
    {
        var picking = _newChatPage == NewChatPage.Members;
        var query = NewChatSearch.Text.Trim();
        var digits = new string(query.Where(char.IsAsciiDigit).ToArray());
        // Adding to a group: the people already in it aren't offered.
        var inGroup = picking && _addingTo is { } group && _members.TryGetValue(group.Id, out var current)
            ? current.Select(m => m.ChatId).ToHashSet()
            : [];
        var shown = _newChatContacts
            .Where(r => !picking || (!_groupMembers.Contains(r) && !inGroup.Contains(r.ChatId)))
            .Where(r => query.Length == 0
                        || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || (digits.Length > 0 && r.Phone.Contains(digits)))
            .ToList();
        // By first letter, like the phone's contact list ("#": names that don't start with one).
        var groups = shown
            .GroupBy(r => r.Name.Length > 0 && char.IsLetter(r.Name[0]) ? char.ToUpperInvariant(r.Name[0]).ToString() : "#")
            .OrderBy(g => g.Key == "#" ? "￿" : g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ContactGroup(g.Key, g))
            .ToList();
        NewChatList.ItemsSource = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        NewChatActions.Visibility = !picking && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewChatEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewChatEmpty.Text = query.Length > 0 ? "No contacts found." : picking ? "Everyone has been added." : "No saved contacts yet.";
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        _groupMembers.Clear();
        NewChatSearch.Text = "";
        RebuildGroupChips();
        ShowNewChatPage(NewChatPage.Members);
    }

    private void NewContact_Click(object sender, RoutedEventArgs e)
    {
        NewContactFirst.Text = NewContactLast.Text = NewContactPhone.Text = "";
        NewContactCode.Text = ViewModel.CommonCountryCode();
        NewContactSync.IsOn = false;
        ValidateNewContact();
        ShowNewChatPage(NewChatPage.Contact);
        NewContactFirst.Focus(FocusState.Programmatic);
    }

    private void MessageYourself_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelfPhone.Length > 0) ViewModel.OpenNumber(ViewModel.SelfPhone);
        CloseNewChat();
    }

    private async void NewChatList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ContactRow row) return;
        if (_newChatPage != NewChatPage.Members)
        {
            ViewModel.OpenContact(row.ChatId, row.Phone, row.Name);
            CloseNewChat();
            return;
        }
        await PickMemberAsync(row);
    }

    /// <summary>Adds a contact to the group being made; a blocked one asks first, and is unblocked on Yes.</summary>
    private async Task PickMemberAsync(ContactRow row)
    {
        if (row.IsBlocked)
        {
            var ask = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Add blocked contact",
                Content = new TextBlock
                {
                    Text = "The contact you have selected is blocked. Would you like to unblock them and add them to the group?",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
            ViewModel.Unblock(row.ChatId);
            row.IsBlocked = false;
            row.Subtitle = row.Phone.Length > 0 ? "+" + row.Phone : "";
        }
        _groupMembers.Add(row);
        NewChatSearch.Text = "";
        RebuildGroupChips();
        FilterNewChat();
    }

    /// <summary>The picked members along the top: picture, name and ✕ each, wrapping.</summary>
    private void RebuildGroupChips()
    {
        GroupChips.Children.Clear();
        foreach (var member in _groupMembers)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var picture = new Redact { VeilRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center };
            picture.Children.Add(new Avatar { DisplayName = member.Name, Source = member.AvatarPath, Size = 28 });
            chip.Children.Add(picture);
            chip.Children.Add(new TextBlock { Text = member.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
            var drop = new Button
            {
                Style = (Style)Application.Current.Resources["IconButtonStyle"],
                Width = 26,
                Height = 26,
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            };
            ToolTipService.SetToolTip(drop, $"Remove {member.Name}");
            drop.Click += (_, _) =>
            {
                _groupMembers.Remove(member);
                RebuildGroupChips();
                FilterNewChat();
            };
            chip.Children.Add(drop);
            GroupChips.Children.Add(chip);
        }
        GroupChipsDivider.Visibility = _groupMembers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupNext.Visibility = _groupMembers.Count > 0 && _newChatPage == NewChatPage.Members ? Visibility.Visible : Visibility.Collapsed;
        GroupNext.Content = _addingTo is null ? "Next" : "Add";
    }

    private void GroupNext_Click(object sender, RoutedEventArgs e)
    {
        if (_addingTo is { } group)
        {
            ViewModel.AddGroupMembers(group, _groupMembers.Select(m => m.ChatId).ToList());
            CloseNewChat();
            return;
        }
        GroupSubject.Text = "";
        GroupCreate.IsEnabled = false;
        GroupSubjectMembers.Text = _groupMembers.Count == 1 ? "1 member" : $"{_groupMembers.Count} members";
        ShowNewChatPage(NewChatPage.Subject);
        GroupSubject.Focus(FocusState.Programmatic);
    }

    private void GroupSubject_TextChanged(object sender, TextChangedEventArgs e) =>
        GroupCreate.IsEnabled = GroupSubject.Text.Trim().Length > 0;

    private void GroupCreate_Click(object sender, RoutedEventArgs e)
    {
        var subject = GroupSubject.Text.Trim();
        if (subject.Length == 0 || _groupMembers.Count == 0) return;
        ViewModel.CreateGroup(subject, _groupMembers.Select(m => m.ChatId).ToList());
        CloseNewChat();
    }

    // ───── Phone number ─────

    /// <summary>1-9 with their letters, then + 0 ⌫, like a phone's keypad, typing into <paramref name="box"/>.</summary>
    private static void BuildNumberPad(Grid pad, TextBox box)
    {
        (string Key, string Letters)[] keys =
        [
            ("1", ""), ("2", "ABC"), ("3", "DEF"), ("4", "GHI"), ("5", "JKL"), ("6", "MNO"),
            ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"), ("+", ""), ("0", ""), ("\uE750", ""),
        ];
        for (var i = 0; i < keys.Length; i++)
        {
            var (key, letters) = keys[i];
            var back = key == "\uE750";
            var face = new StackPanel { Spacing = 2 };
            face.Children.Add(back
                ? new FontIcon { Glyph = key, FontSize = 18 }
                : new TextBlock { Text = key, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
            if (letters.Length > 0)
                face.Children.Add(new TextBlock { Text = letters, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
            var button = new Button
            {
                Content = face,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Height = 76,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, back ? "Backspace" : key);
            button.Click += (_, _) =>
            {
                var text = box.Text;
                box.Text = back ? (text.Length > 0 ? text[..^1] : "") : text + key;
                box.SelectionStart = box.Text.Length;
            };
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            pad.Children.Add(button);
        }
    }

    private void NewChatDialpad_Click(object sender, RoutedEventArgs e)
    {
        NumberBox.Text = "";
        ShowNewChatPage(NewChatPage.Number);
        NumberBox.Focus(FocusState.Programmatic);
    }

    private void NumberBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NumberChat.IsEnabled = NumberBox.Text.Count(char.IsAsciiDigit) >= 7;

    private void NumberBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && NumberChat.IsEnabled) NumberChat_Click(sender, e);
    }

    private void NumberChat_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenNumber(NumberBox.Text);
        CloseNewChat();
    }

    // ───── New contact ─────

    private void NewContact_TextChanged(object sender, TextChangedEventArgs e) => ValidateNewContact();

    private void ValidateNewContact() =>
        NewContactSave.IsEnabled = NewContactFirst.Text.Trim().Length > 0
                                   && (NewContactCode.Text + NewContactPhone.Text).Count(char.IsAsciiDigit) >= 7;

    private void NewContactSave_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveNewContact(NewContactCode.Text + NewContactPhone.Text, NewContactFirst.Text.Trim(), NewContactLast.Text.Trim(), NewContactSync.IsOn);
        CloseNewChat();
    }
}
