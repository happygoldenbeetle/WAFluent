using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Controls;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The Calls page (the rail's phone), like WhatsApp's.
///   Left  : search, Favourites (the same list as the chats' Favourites filter: "Add favourite",
///           "View all" with a pencil to take some off and + to add) and Recent, your call
///           history from the phone and this PC, calls of one kind in a row grouped ("Outgoing (2)").
///           The header's buttons: a number pad to call any number, and New call (pick a person).
///   Right : "Voice and video calling" with New call link and Call a number; or, once a call or a
///           person is picked, who it is, Message / Voice / Video, and the calls with them.
/// A call link is made by WhatsApp (voice or video), to copy or send to chats. Right-click a
/// recent call to take it out of the list.
/// </summary>
public sealed partial class MainWindow
{
    private enum CallsPage { Number, NewCall, AddFavourites, Favourites }

    /// <summary>How many favourites the Calls page lists before "View all".</summary>
    private const int FavouritesShown = 3;

    private CallsPage _callsPage;
    private bool _callsWired, _callsOpen, _fillingCalls;
    /// <summary>Who the right side is about (null: the "Voice and video calling" card).</summary>
    private CallTarget? _callInfo;
    /// <summary>The page Add to Favourites goes back to.</summary>
    private bool _addingFromFavourites;
    /// <summary>A number being looked up to call: video or not, and until when the answer counts.</summary>
    private (bool Video, DateTime Until)? _numberCall;
    /// <summary>Set while the contact card picks chats to send this text to (a call link).</summary>
    private string? _sendText;

    private void WireCalls()
    {
        if (_callsWired) return;
        _callsWired = true;
        ViewModel.CallsChanged += () =>
        {
            if (!_callsOpen) return;
            FillCalls();
            SeenCalls();   // a call missed while you're looking at the page isn't news
        };
        ViewModel.FavouritesChanged += () => { if (_callsOpen) FillFavourites(); };
        ViewModel.ChatOpened += chat =>
        {
            // The number typed on the pad was found: call it.
            if (_numberCall is not { } wanted) return;
            _numberCall = null;
            if (DateTime.Now < wanted.Until) StartCallWith(chat, wanted.Video);
        };
        EnsureContacts();
        BuildNumberPad(CallNumberPad, CallNumberBox);
        CallsTiles.Children.Add(InfoAction("\uE71B", "New call link", NewCallLink));
        CallsTiles.Children.Add(InfoAction("\uE75F", "Call a number", () => ShowCallsPage(CallsPage.Number)));
    }

    /// <summary>The rail's Calls was picked (or left).</summary>
    private void ShowCalls(bool show)
    {
        _callsOpen = show;
        CallsActions.Visibility = CallsPanel.Visibility = CallsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            if (CallsSubPane.Visibility == Visibility.Visible) CloseCallsPage();
            return;
        }
        WireCalls();
        CallsSearch.Text = "";
        FillFavourites();
        FillCalls();
        ShowCallInfo(_callInfo);
        SeenCalls();
        ViewModel.LoadCalls();
        ViewModel.LoadContacts();
    }

    /// <summary>The rail's missed-call count clears: the page is in front of you.</summary>
    private void SeenCalls()
    {
        if (ViewModel.MissedCalls == 0 && _ui.CallsSeenAt > 0) return;
        _ui.CallsSeenAt = ViewModel.MarkCallsSeen();
        _ui.Save();
    }

    // ───── Recent ─────

    private void CallsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        FillFavourites();
        FillCalls();
    }

    private bool Matches(string name, string phone)
    {
        var query = CallsSearch.Text.Trim();
        if (query.Length == 0) return true;
        var digits = new string(query.Where(char.IsAsciiDigit).ToArray());
        return name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || (digits.Length > 2 && phone.Contains(digits));
    }

    /// <summary>The history as rows: calls of one kind with one person on one day, in a row, are one row.</summary>
    private void FillCalls()
    {
        var rows = new List<CallRow>();
        List<CallLogDto>? run = null;
        foreach (var call in ViewModel.CallLog.Where(c => Matches(ViewModel.ChatFor(c)?.Name ?? c.Name, c.Phone)))
        {
            if (run is not null && CallRow.Key(run[0]) == CallRow.Key(call))
            {
                run.Add(call);
                continue;
            }
            run = [call];
            rows.Add(new CallRow { Calls = run, Chat = ViewModel.ChatFor(call) });
        }
        _fillingCalls = true;
        CallsList.ItemsSource = rows;
        // The row of whoever is shown on the right stays marked.
        CallsList.SelectedItem = _callInfo is { } shown ? rows.FirstOrDefault(r => Same(shown, r)) : null;
        _fillingCalls = false;
        var searching = CallsSearch.Text.Trim().Length > 0;
        CallsRecentTitle.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CallsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CallsEmpty.Text = searching ? "No calls found." : "No recent calls.\nCalls you make or get on your phone and on this PC show here.";
        if (_callInfo is not null) ShowCallInfo(_callInfo);   // its list of calls may have changed
    }

    private static bool Same(CallTarget target, CallRow row) =>
        target.Chat is not null ? ReferenceEquals(target.Chat, row.Chat) : target.ChatId.Length > 0 ? target.ChatId == row.ChatId : target.Name == row.Name;

    private void CallsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCalls || CallsList.SelectedItem is not CallRow row) return;
        ShowCallInfo(new CallTarget(row.ChatId, row.Name, row.Phone, row.AvatarPath, row.IsGroup, row.Chat));
    }

    private void CallRow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CallRow row } element) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove from call log", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += (_, _) => ViewModel.DeleteCalls(row.Calls);
        menu.Items.Add(remove);
        if (e.TryGetPosition(element, out var point)) menu.ShowAt(element, point);
        else menu.ShowAt(element);
    }

    // ───── Favourites ─────

    /// <summary>The first few favourites, or "Add favourite" when there are none; "View all" past a few.</summary>
    private void FillFavourites()
    {
        var all = ViewModel.FavouriteChats();
        var shown = all.Where(c => Matches(c.Name, ContactPhone(c))).ToList();
        var searching = CallsSearch.Text.Trim().Length > 0;
        CallsFavourites.Children.Clear();
        foreach (var chat in shown.Take(FavouritesShown)) CallsFavourites.Children.Add(PersonRow(chat, () => ShowCallInfo(Target(chat))));
        if (all.Count == 0 && !searching) CallsFavourites.Children.Add(AddFavouriteRow());
        FavouritesViewAll.Visibility = all.Count > 0 && !searching ? Visibility.Visible : Visibility.Collapsed;
        // Searching with no favourite matching: the heading goes too.
        CallsFavouritesHeader.Visibility = CallsFavourites.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_callsPage == CallsPage.Favourites && CallsSubPane.Visibility == Visibility.Visible) FillFavouritesPage();
    }

    private static CallTarget Target(Chat chat) => new(chat.Id, chat.Name, ContactPhone(chat), chat.AvatarPath, chat.IsGroup, chat);

    /// <summary>A picture and a name, clickable; <paramref name="remove"/> adds a ✕ at the end.</summary>
    private static Button PersonRow(Chat chat, Action open, Action? remove = null)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var picture = new Redact { VeilRadius = new CornerRadius(24), VerticalAlignment = VerticalAlignment.Center };
        picture.Children.Add(new Avatar { DisplayName = chat.Name, Source = chat.AvatarPath, Size = 48, IsGroup = chat.IsGroup });
        grid.Children.Add(picture);
        var name = new Redact { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = chat.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        if (remove is not null)
        {
            var drop = new Button
            {
                Style = (Style)Application.Current.Resources["IconButtonStyle"],
                Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(drop, "Remove from Favourites");
            AutomationProperties.SetName(drop, $"Remove {chat.Name} from Favourites");
            drop.Click += (_, _) => remove();
            Grid.SetColumn(drop, 2);
            grid.Children.Add(drop);
        }
        var row = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 8, 8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.Click += (_, _) => open();
        return row;
    }

    private Button AddFavouriteRow()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var disc = new Grid { Width = 48, Height = 48 };
        disc.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = Themed.Brush("ChatAccentBrush") });
        disc.Children.Add(new FontIcon { Glyph = "\uE8FA", FontSize = 20, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0B, 0x14, 0x1A)) });
        content.Children.Add(disc);
        content.Children.Add(new TextBlock { Text = "Add favourite", FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var row = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 8, 8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.Click += (_, _) => OpenAddFavourites(fromFavourites: false);
        return row;
    }

    private void FavouritesViewAll_Click(object sender, RoutedEventArgs e)
    {
        FavouritesEdit.IsChecked = false;
        ShowCallsPage(CallsPage.Favourites);
    }

    private void FavouritesEdit_Changed(object sender, RoutedEventArgs e) => FillFavouritesPage();

    private void FavouritesAdd_Click(object sender, RoutedEventArgs e) => OpenAddFavourites(fromFavourites: true);

    private void FillFavouritesPage()
    {
        var editing = FavouritesEdit.IsChecked == true;
        FavouritesRows.Children.Clear();
        foreach (var chat in ViewModel.FavouriteChats())
        {
            FavouritesRows.Children.Add(PersonRow(chat,
                () =>
                {
                    CloseCallsPage();
                    ShowCallInfo(Target(chat));
                },
                editing ? () => ViewModel.ToggleFavourite(chat) : null));
        }
        if (FavouritesRows.Children.Count == 0)
            FavouritesRows.Children.Add(new TextBlock
            {
                Text = "No favourites yet. Add people or groups with +.",
                Margin = new Thickness(12, 40, 12, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
    }

    private void OpenAddFavourites(bool fromFavourites)
    {
        _addingFromFavourites = fromFavourites;
        ShowCallsPage(CallsPage.AddFavourites);
    }

    // ───── The pages over the list ─────

    private void ShowCallsPage(CallsPage page)
    {
        _callsPage = page;
        CallsSubTitle.Text = page switch
        {
            CallsPage.Number => "Phone number",
            CallsPage.NewCall => "New call",
            CallsPage.AddFavourites => "Add to Favourites",
            _ => "Favourites",
        };
        var picking = page is CallsPage.NewCall or CallsPage.AddFavourites;
        CallsNumberPage.Visibility = page == CallsPage.Number ? Visibility.Visible : Visibility.Collapsed;
        CallsPickPage.Visibility = picking ? Visibility.Visible : Visibility.Collapsed;
        FavouritesPage.Visibility = FavouritesActions.Visibility = page == CallsPage.Favourites ? Visibility.Visible : Visibility.Collapsed;
        CallsSubPane.Visibility = Visibility.Visible;
        SetChatListHidden(true);
        switch (page)
        {
            case CallsPage.Number:
                CallNumberBox.Text = "";
                CallNumberBox.Focus(FocusState.Programmatic);
                break;
            case CallsPage.Favourites:
                FillFavouritesPage();
                break;
            default:
                // Ticks for favourites (several at once); a plain click to call.
                var ticking = page == CallsPage.AddFavourites;
                CallsPickList.SelectionMode = ticking ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
                CallsPickList.IsItemClickEnabled = !ticking;
                CallsPickNote.Visibility = ticking ? Visibility.Visible : Visibility.Collapsed;
                CallsPickDone.Visibility = Visibility.Collapsed;
                CallsPickSearch.Text = "";
                FillCallsPick();
                CallsPickSearch.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void CloseCallsPage()
    {
        CallsSubPane.Visibility = Visibility.Collapsed;
        SetChatListHidden(false);
    }

    private void CallsSubBack_Click(object sender, RoutedEventArgs e)
    {
        if (_callsPage == CallsPage.AddFavourites && _addingFromFavourites) ShowCallsPage(CallsPage.Favourites);
        else CloseCallsPage();
    }

    private void CallsDialpad_Click(object sender, RoutedEventArgs e) => ShowCallsPage(CallsPage.Number);

    private void CallsNew_Click(object sender, RoutedEventArgs e) => ShowCallsPage(CallsPage.NewCall);

    private void CallsPickSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FillCallsPick();

    /// <summary>
    /// New call: your contacts by name. Add to Favourites: your chats (groups too), most recent
    /// first, without the ones that already are favourites.
    /// </summary>
    private void FillCallsPick()
    {
        var query = CallsPickSearch.Text.Trim();
        var digits = new string(query.Where(char.IsAsciiDigit).ToArray());
        IEnumerable<ContactRow> rows = _callsPage == CallsPage.AddFavourites
            ? ViewModel.ForwardTargets().Where(c => !c.IsFavourite).Select(c => new ContactRow
            {
                Name = c.Name,
                Phone = ContactPhone(c),
                ChatId = c.Id,
                Subtitle = c.IsGroup ? "Group" : c.PhoneCode.Length > 0 ? $"{c.PhoneCode} {c.PhoneNational}" : "",
                AvatarPath = c.AvatarPath,
                Chat = c,
            })
            : People();
        var shown = rows.Where(r => query.Length == 0
                                    || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                    || (digits.Length > 2 && r.Phone.Contains(digits))).ToList();
        CallsPickList.ItemsSource = shown;
        CallsPickEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CallsPickEmpty.Text = query.Length > 0 ? "Nobody found." : _callsPage == CallsPage.AddFavourites ? "Everyone is a favourite already." : "No saved contacts yet.";
        CallsPickDone.Visibility = Visibility.Collapsed;
    }

    /// <summary>Who can be called: your saved contacts; with the sample data (no contacts), its people.</summary>
    private IEnumerable<ContactRow> People() =>
        _newChatContacts.Count > 0 || ViewModel.IsLive
            ? _newChatContacts.Where(c => !c.IsBlocked)
            : ViewModel.ForwardTargets().Where(c => !c.IsGroup).OrderBy(c => c.Name).Select(c => new ContactRow { Name = c.Name, Phone = "", AvatarPath = c.AvatarPath, Chat = c });

    private void CallsPickList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ContactRow row) return;
        CloseCallsPage();
        var chat = row.Chat ?? ViewModel.ChatById(row.ChatId);
        ShowCallInfo(new CallTarget(row.ChatId, row.Name, row.Phone, row.AvatarPath ?? chat?.AvatarPath, false, chat));
    }

    private void CallsPickList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var picked = CallsPickList.SelectedItems.Count;
        CallsPickDone.Visibility = picked > 0 && _callsPage == CallsPage.AddFavourites ? Visibility.Visible : Visibility.Collapsed;
        CallsPickDone.Content = picked > 1 ? $"Add {picked} to Favourites" : "Add to Favourites";
    }

    private void CallsPickDone_Click(object sender, RoutedEventArgs e)
    {
        var picked = CallsPickList.SelectedItems.OfType<ContactRow>().Select(r => r.Chat).OfType<Chat>().ToList();
        foreach (var chat in picked.Where(c => !c.IsFavourite)) ViewModel.ToggleFavourite(chat);
        if (picked.Count > 0) ShowToast(true, "Added to Favourites list");
        if (_addingFromFavourites) ShowCallsPage(CallsPage.Favourites);
        else CloseCallsPage();
    }

    // ───── Call a number ─────

    private void CallNumberBox_TextChanged(object sender, TextChangedEventArgs e) =>
        CallNumberVoice.IsEnabled = CallNumberVideo.IsEnabled = CallNumberBox.Text.Count(char.IsAsciiDigit) >= 7;

    private void CallNumberVoice_Click(object sender, RoutedEventArgs e) => CallNumber(video: false);

    private void CallNumberVideo_Click(object sender, RoutedEventArgs e) => CallNumber(video: true);

    /// <summary>WhatsApp is asked whether the number is on it; the chat it answers with is called.</summary>
    private void CallNumber(bool video)
    {
        if (!ViewModel.IsLive)
        {
            ShowToast(false, "Calling a number needs your phone linked.");
            return;
        }
        _numberCall = (video, DateTime.Now.AddSeconds(20));
        ViewModel.OpenNumber(CallNumberBox.Text);
        CloseCallsPage();
    }

    // ───── The right side ─────

    /// <summary>Who it is, Message / Voice / Video, and the calls with them; null: the "Voice and video calling" card.</summary>
    private void ShowCallInfo(CallTarget? target)
    {
        _callInfo = target;
        CallsIntro.Visibility = target is null ? Visibility.Visible : Visibility.Collapsed;
        CallInfo.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        if (target is null)
        {
            _fillingCalls = true;
            CallsList.SelectedItem = null;
            _fillingCalls = false;
            return;
        }
        CallInfoAvatar.DisplayName = target.Name;
        CallInfoAvatar.Source = target.Chat?.AvatarPath ?? target.Avatar;
        CallInfoAvatar.IsGroup = target.IsGroup;
        CallInfoName.Text = target.Name;
        CallInfoPhone.Text = target.Phone.Length > 0 && !target.Name.Contains(target.Phone) ? "+" + target.Phone : "";
        CallInfoPhone.Visibility = CallInfoPhone.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        CallInfoActions.Children.Clear();
        if (target.Chat is not null || target.ChatId.Length > 0)
            CallInfoActions.Children.Add(InfoAction("", "Message", () => MessageTarget(target)));
        CallInfoActions.Children.Add(InfoAction(Glyphs.Phone, "Voice", () => CallTargetNow(target, video: false)));
        CallInfoActions.Children.Add(InfoAction(Glyphs.VideoCall, "Video", () => CallTargetNow(target, video: true)));

        // Their calls, newest first, under each day.
        CallInfoEntries.Children.Clear();
        var calls = ViewModel.CallLog.Where(c => target.Chat is not null ? ReferenceEquals(ViewModel.ChatFor(c), target.Chat)
                                                 : target.ChatId.Length > 0 ? c.ChatId == target.ChatId : c.Name == target.Name).Take(40);
        DateTime? day = null;
        foreach (var call in calls)
        {
            var when = CallRow.When(call);
            if (day != when.Date)
            {
                day = when.Date;
                CallInfoEntries.Children.Add(new TextBlock
                {
                    Text = Format.DayLabel(when),
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(12, 14, 0, 4),
                    Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
                });
            }
            CallInfoEntries.Children.Add(CallEntryRow(call, when));
        }
    }

    /// <summary>"Outgoing video call" with its time, and how long it lasted on the right.</summary>
    private static Grid CallEntryRow(CallLogDto call, DateTime when)
    {
        var missed = CallRow.Missed(call);
        var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(12, 8, 12, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = call.Video ? Glyphs.VideoCall : Glyphs.Phone, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.CallBrush(missed) });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = $"{CallRow.Kind(call)} {(call.Video ? "video" : "voice")} call", Foreground = missed ? Ui.CallBrush(true) : Themed.Brush("TextFillColorPrimaryBrush") });
        text.Children.Add(new TextBlock { Text = Format.Clock(when), FontSize = 12, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var detail = call.Duration > 0 ? Format.Duration(TimeSpan.FromSeconds(call.Duration))
            : call.Result == "connected" ? ""
            : call.Incoming ? "" : "Not answered";
        var trailing = new TextBlock { Text = detail, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Themed.Brush("TextFillColorSecondaryBrush") };
        Grid.SetColumn(trailing, 2);
        row.Children.Add(trailing);
        return row;
    }

    /// <summary>A round button with its label underneath (Message, Voice, New call link…).</summary>
    private static StackPanel InfoAction(string glyph, string label, Action action)
    {
        var button = new Button
        {
            Width = 66,
            Height = 50,
            CornerRadius = new CornerRadius(25),
            Content = new FontIcon { Glyph = glyph, FontSize = 18 },
            BorderThickness = new Thickness(0),
            Background = Themed.Brush("SubtleFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(button);
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    private void CallInfoClose_Click(object sender, RoutedEventArgs e) => ShowCallInfo(null);

    /// <summary>Message: their chat, back on the Chats page.</summary>
    private void MessageTarget(CallTarget target)
    {
        Nav.SelectedItem = Nav.MenuItems[0];
        if (target.Chat is { } chat && ViewModel.Exists(chat))
        {
            ViewModel.SelectedChat = chat;
            ChatList.SelectedItem = chat;
        }
        else
        {
            ViewModel.OpenContact(target.ChatId, target.Phone, target.Name);
        }
    }

    private void CallTargetNow(CallTarget target, bool video)
    {
        if (target.IsGroup)
        {
            ShowToast(false, "Group calls aren't available yet.");
            return;
        }
        // Someone called before there's a chat with them: just enough of one for the call window.
        var chat = target.Chat ?? ViewModel.ChatById(target.ChatId) ?? new Chat { Id = target.ChatId, Name = target.Name, AvatarPath = target.Avatar };
        if (chat.Id.Length == 0 && ViewModel.IsLive)
        {
            ShowToast(false, "This person's number isn't known yet.");
            return;
        }
        StartCallWith(chat, video);
    }

    // ───── New call link ─────

    /// <summary>
    /// Asks WhatsApp for a call link (voice or video), to copy, share with chats, or join.
    /// Anyone with WhatsApp can join the call through it; when someone does, you're rung.
    /// </summary>
    private void NewCallLink() => NewCallLink(demo: false);

    /// <param name="demo">The self-test: the dialog with a made-up link, nothing asked of WhatsApp.</param>
    private async void NewCallLink(bool demo)
    {
        if (!ViewModel.IsLive && !demo)
        {
            ShowToast(false, "Call links need your phone linked.");
            return;
        }
        var kind = new ComboBox { MinWidth = 110, VerticalAlignment = VerticalAlignment.Center };
        kind.Items.Add("Voice");
        kind.Items.Add("Video");
        kind.SelectedIndex = 0;
        var link = new TextBox { IsReadOnly = true, PlaceholderText = "Creating the link…", VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 15 },
            IsEnabled = false,
        };
        ToolTipService.SetToolTip(copy, "Copy link");
        AutomationProperties.SetName(copy, "Copy link");
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(kind);
        Grid.SetColumn(link, 1);
        line.Children.Add(link);
        Grid.SetColumn(copy, 2);
        line.Children.Add(copy);
        // A set width: the link arriving (or a longer one) doesn't resize the dialog.
        var body = new StackPanel { Spacing = 14, Width = 440 };
        body.Children.Add(line);
        body.Children.Add(new TextBlock
        {
            Text = "Anyone with WhatsApp can use this link to join this call. Only share it with people you trust.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "New call link",
            Content = body,
            PrimaryButtonText = "Share",
            SecondaryButtonText = "Join call",
            IsSecondaryButtonEnabled = false,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = Root.ActualTheme,
        };

        void Ask()
        {
            link.Text = "";
            copy.IsEnabled = dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            if (demo) Got($"https://call.whatsapp.com/{(kind.SelectedIndex == 1 ? "video" : "voice")}/Example0Link1For2The3Test", kind.SelectedIndex == 1);
            else ViewModel.CreateCallLink(kind.SelectedIndex == 1);
        }
        void Got(string url, bool video)
        {
            if (video != (kind.SelectedIndex == 1)) return;   // the answer to the kind picked before
            link.Text = url;
            copy.IsEnabled = dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
        }
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(link.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            ShowToast(true, "Link copied");
        };
        kind.SelectionChanged += (_, _) => Ask();
        ViewModel.CallLinkReceived += Got;
        Ask();
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { ViewModel.CallLinkReceived -= Got; }
        if (link.Text.Length == 0) return;
        if (result == ContentDialogResult.Primary) SendTextToChats("Share call link with", link.Text);
        else if (result == ContentDialogResult.Secondary) JoinCallLink(link.Text);
    }

    /// <summary>The contact card, picking chats to send <paramref name="text"/> to.</summary>
    private void SendTextToChats(string title, string text)
    {
        _forwarding = [];
        _sendText = text;
        _meRow = null;
        ContactsTitle.Text = title;
        AutomationProperties.SetName(ContactsSend, "Send");
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
    }
}
