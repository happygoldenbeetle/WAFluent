using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Search messages, like WhatsApp Web: in the side panel, results for the open chat as you
/// type (date, ticks, the text with the match in green), newest first; clicking one scrolls
/// the chat to it (loading older messages first when needed). The calendar goes to a date.
/// The footer says how far back this PC's copy goes: older messages are only on the phone.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _searchDebounce;
    private bool _searchWired;

    private void SearchChat_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        WireSearch();
        ContactInfoView.Visibility = Visibility.Collapsed;
        NewContactView.Visibility = Visibility.Collapsed;
        SearchView.Visibility = Visibility.Visible;
        MessageInfoView.Visibility = Visibility.Collapsed;
        InfoTitle.Text = "Search messages";
        MessageSearchBox.Text = "";
        ShowSearchIdle(chat);
        InfoPanel.Visibility = Visibility.Visible;
        PlaceInfoPanel();
        MessageSearchBox.Focus(FocusState.Programmatic);
    }

    private void WireSearch()
    {
        if (_searchWired) return;
        _searchWired = true;
        SearchCalendar.MaxDate = DateTimeOffset.Now;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RunSearch();
        };
        ViewModel.RevealMessage += ScrollToMessage;
        ViewModel.PropertyChanged += (_, e) =>
        {
            // Another chat: its own search.
            if (e.PropertyName == nameof(ViewModel.SelectedChat) && SearchView.Visibility == Visibility.Visible
                && InfoPanel.Visibility == Visibility.Visible)
            {
                if (ViewModel.SelectedChat is { } other) { MessageSearchBox.Text = ""; ShowSearchIdle(other); }
                else CloseInfo();
            }
        };
        if (_core is null) return;
        _core.SearchResults += (chatId, query, results, oldestTs) =>
        {
            if (ViewModel.SelectedChat?.Id != chatId || query != MessageSearchBox.Text.Trim()) return;   // stale
            ShowResults(results, oldestTs);
        };
        _core.FoundMessage += (chatId, messageId, ts) =>
        {
            if (ViewModel.SelectedChat?.Id != chatId) return;
            if (messageId is null) ShowToast(false, "No messages from that day on this PC.");
            else ViewModel.Reveal(messageId, ts);
        };
    }

    private void ShowSearchIdle(Chat chat)
    {
        _searchDebounce?.Stop();
        SearchRing.IsActive = false;
        SearchRing.Visibility = Visibility.Collapsed;
        SearchResultsList.ItemsSource = null;
        SearchFooter.Visibility = SearchFooterLine.Visibility = Visibility.Collapsed;
        SearchHint.Text = $"Search for messages with {chat.Name}.";
        SearchHint.Visibility = Visibility.Visible;
    }

    private void MessageSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce?.Stop();
        if (MessageSearchBox.Text.Trim().Length == 0)
        {
            if (ViewModel.SelectedChat is { } chat) ShowSearchIdle(chat);
            return;
        }
        _searchDebounce?.Start();
    }

    private void RunSearch()
    {
        var query = MessageSearchBox.Text.Trim();
        if (query.Length == 0 || ViewModel.SelectedChat is not { } chat) return;
        if (_core is null)
        {
            // Sample mode: search what's loaded.
            var hits = chat.Messages.Where(m => m.Kind != MessageKind.DateDivider
                                                && (m.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                                    || m.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                                    .Reverse().ToList();
            ShowResults(hits, null);
            return;
        }
        SearchHint.Visibility = Visibility.Collapsed;
        SearchRing.IsActive = true;
        SearchRing.Visibility = Visibility.Visible;
        _core.SearchMessages(chat.Id, query);
    }

    private void ShowResults(IReadOnlyList<MessageDto> results, long? oldestTs)
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        ShowResults(results.Select(d => Format.ToMessage(d, chat.IsGroup)).ToList(), oldestTs);
    }

    private void ShowResults(List<Message> hits, long? oldestTs)
    {
        SearchRing.IsActive = false;
        SearchRing.Visibility = Visibility.Collapsed;
        var query = MessageSearchBox.Text.Trim();
        SearchHint.Text = hits.Count == 0 ? "No messages found." : "";
        SearchHint.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResultsList.ItemsSource = hits.Select(m => ResultRow(m, query)).ToList();
        var footer = oldestTs is { } ts
            ? $"Use WhatsApp on your phone to search messages from before {Format.FromUnix(ts):dd/MM/yyyy}"
            : "";
        SearchFooter.Text = footer;
        SearchFooter.Visibility = SearchFooterLine.Visibility = footer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One result: its date, then ticks (yours) or the sender (groups) and the text with the match in green.</summary>
    private static FrameworkElement ResultRow(Message m, string query)
    {
        var row = new StackPanel { Spacing = 2, Padding = new Thickness(8, 10, 8, 10), Tag = m };
        row.Children.Add(new TextBlock
        {
            Text = m.Timestamp == default || m.Timestamp.Date == DateTime.Today ? m.Time : m.Timestamp.ToString("dd/MM/yyyy"),
            FontSize = 13,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (m.IsOutgoing)
            line.Children.Add(new Controls.DeliveryTicks { Delivery = m.Delivery, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        if (m.SenderName.Length > 0) text.Inlines.Add(new Run { Text = m.SenderName + ": ", Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
        var body = m.Text.Length > 0 ? m.Text : m.FileName;
        var at = body.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        // Long texts: start a little before the match so it shows.
        var start = at > 60 ? at - 30 : 0;
        if (start > 0) text.Inlines.Add(new Run { Text = "…" });
        if (at < 0)
        {
            text.Inlines.Add(new Run { Text = body[start..] });
        }
        else
        {
            text.Inlines.Add(new Run { Text = body[start..at] });
            text.Inlines.Add(new Run
            {
                Text = body.Substring(at, query.Length),
                Foreground = Themed.Brush("ChatAccentTextBrush"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            text.Inlines.Add(new Run { Text = body[(at + query.Length)..] });
        }
        line.Children.Add(text);
        row.Children.Add(line);
        return row;
    }

    private void SearchResult_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrameworkElement { Tag: Message m }) return;
        ViewModel.Reveal(m.Id, m.UnixTs);
    }

    private void SearchCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (args.AddedDates.Count == 0 || ViewModel.SelectedChat is not { } chat) return;
        SearchDateFlyout.Hide();
        var day = args.AddedDates[0].Date;
        sender.SelectedDates.Clear();
        var ts = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)).ToUnixTimeSeconds();
        if (_core is not null)
        {
            _core.FindMessageAt(chat.Id, ts);
            return;
        }
        var first = chat.Messages.FirstOrDefault(m => m.Kind != MessageKind.DateDivider && m.Timestamp >= day);
        if (first is not null) ScrollToMessage(first.Id);
    }
}
