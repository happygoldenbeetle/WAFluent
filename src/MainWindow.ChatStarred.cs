using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

/// <summary>
/// Contact info › Starred messages, like WhatsApp: this chat's starred messages only (the rail's
/// Starred view has every chat's), newest first, each as a bubble (yours green, theirs grey, all
/// down the left) under who sent it and when,
/// with a search box. Clicking one goes to it in the chat (loading back to it); right-click
/// unstars it.
/// </summary>
public sealed partial class MainWindow
{
    private bool _chatStarredWired;

    private void OpenChatStarred()
    {
        if (ViewModel.SelectedChat is null) return;
        if (!_chatStarredWired)
        {
            _chatStarredWired = true;
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.HasStarred) && ChatStarredView.Visibility == Visibility.Visible) ShowChatStarred();
            };
        }
        ContactInfoView.Visibility = Visibility.Collapsed;
        NewContactView.Visibility = Visibility.Collapsed;
        SearchView.Visibility = Visibility.Collapsed;
        MessageInfoView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        DisappearingView.Visibility = Visibility.Collapsed;
        ChatStarredView.Visibility = Visibility.Visible;
        InfoTitle.Text = "Starred messages";
        InfoCloseIcon.Glyph = "\uE72B";   // back to Contact info
        ChatStarredSearch.Text = "";
        ChatStarredRows.Children.Clear();
        ViewModel.LoadStarred();   // answers by updating Starred (HasStarred is raised)
        ShowChatStarred();
    }

    private void ChatStarredSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowChatStarred();

    private void ShowChatStarred()
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        var query = ChatStarredSearch.Text.Trim();
        var items = ViewModel.Starred
            .Where(s => s.ChatId == chat.Id || (s.ChatId.Length == 0 && s.ChatName == chat.Name))
            .Where(s => query.Length == 0 || s.Preview.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        ChatStarredRows.Children.Clear();
        ChatStarredEmpty.Visibility = items.Count == 0 && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0 && query.Length > 0)
            ChatStarredRows.Children.Add(new TextBlock
            {
                Text = "No starred messages found.", Margin = new Thickness(0, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
        foreach (var item in items) ChatStarredRows.Children.Add(StarredRow(chat, item));
    }

    /// <summary>"You · 12 Sep" over the message as a bubble (its side's colour), with its time and a star.</summary>
    private Button StarredRow(Chat chat, StarredItem item)
    {
        var m = item.Message;
        var who = m.IsOutgoing ? "You" : m.SenderName.Length > 0 ? m.SenderName : chat.Name;
        var when = m.Timestamp == default ? "" : m.Timestamp.ToString(m.Timestamp.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy");

        var column = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(new TextBlock
        {
            Text = when.Length > 0 ? $"{who}  ·  {when}" : who,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        meta.Children.Add(new FontIcon { Glyph = "\uE735", FontSize = 10, Foreground = Themed.Brush("MetaTextBrush") });
        meta.Children.Add(new TextBlock { Text = m.Time, FontSize = 11, Foreground = Themed.Brush("MetaTextBrush") });
        var inside = new StackPanel { Spacing = 4 };
        inside.Children.Add(new TextBlock { Text = item.Preview, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 });
        inside.Children.Add(meta);
        column.Children.Add(new Border
        {
            Child = inside,
            Padding = new Thickness(10, 7, 10, 6),
            CornerRadius = new CornerRadius(10),
            Background = Ui.BubbleBrush(m.IsOutgoing),
            HorizontalAlignment = HorizontalAlignment.Left,   // one column; yours are green
            MaxWidth = 300,
        });

        var row = new Button
        {
            Content = column,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 10),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{who}: {item.Preview}");
        row.Click += (_, _) => ViewModel.Reveal(m.Id, m.UnixTs);
        var unstar = new MenuFlyoutItem { Text = "Unstar", Icon = new FontIcon { Glyph = Glyphs.StarFill } };
        unstar.Click += (_, _) =>
        {
            ViewModel.Star([m], false);
            ViewModel.LoadStarred();
        };
        row.ContextFlyout = new MenuFlyout { Items = { unstar } };
        return row;
    }
}
