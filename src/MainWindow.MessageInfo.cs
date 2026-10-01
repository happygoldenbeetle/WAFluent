using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Message info, like WhatsApp: in the side panel, your message over the chat wallpaper,
/// then who read it and who it reached, each with their picture and when. 1:1 chats show
/// "Read" and "Delivered" with times. The core keeps every receipt it sees (per person); ones
/// from before WAFluent started keeping them aren't known. While it's open it keeps up: each
/// new receipt for the message (a group member reading it, its ticks changing) asks again.
/// </summary>
public sealed partial class MainWindow
{
    private Message? _infoMessage;
    private bool _infoWired;

    private void OpenMessageInfo(Message m)
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        if (!_infoWired)
        {
            _infoWired = true;
            if (_core is not null)
            {
                _core.MessageInfoReceived += (chatId, messageId, receipts) =>
                {
                    if (_infoMessage?.Id == messageId && ViewModel.SelectedChat is { } open && open.Id == chatId
                        && MessageInfoView.Visibility == Visibility.Visible)
                        ShowMessageInfo(open.IsGroup, receipts);
                };
                _core.ReceiptsChanged += (chatId, ids) =>
                {
                    if (_infoMessage is { } shown && ids.Contains(shown.Id) && ViewModel.SelectedChat?.Id == chatId
                        && InfoPanel.Visibility == Visibility.Visible && MessageInfoView.Visibility == Visibility.Visible)
                        _core.MessageInfo(chatId, shown.Id);
                };
            }
            // Its ticks can change while it's open.
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.SelectedChat) && MessageInfoView.Visibility == Visibility.Visible) CloseInfo();
            };
        }
        if (_infoMessage is { } before) before.PropertyChanged -= InfoMessage_PropertyChanged;
        _infoMessage = m;
        m.PropertyChanged += InfoMessage_PropertyChanged;
        ContactInfoView.Visibility = Visibility.Collapsed;
        NewContactView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        InfoCloseIcon.Glyph = "\uE711";
        SearchView.Visibility = Visibility.Collapsed;
        MessageInfoView.Visibility = Visibility.Visible;
        InfoTitle.Text = "Message info";
        InfoPanel.Visibility = Visibility.Visible;
        PlaceInfoPanel();
        ShowMessageInfo(chat.IsGroup, _core is null ? [] : null);
        _core?.MessageInfo(chat.Id, m.Id);
    }

    /// <summary>Its ticks moved (or it was edited): the info is asked for again.</summary>
    private void InfoMessage_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender != _infoMessage || e.PropertyName is not (nameof(Message.Delivery) or nameof(Message.Text) or nameof(Message.Id))) return;
        if (InfoPanel.Visibility != Visibility.Visible || MessageInfoView.Visibility != Visibility.Visible) return;
        if (ViewModel.SelectedChat is not { } chat || _infoMessage is not { } m) return;
        if (_core is null) ShowMessageInfo(chat.IsGroup, []);
        else _core.MessageInfo(chat.Id, m.Id);
    }

    /// <summary><paramref name="receipts"/> null: still asking the core.</summary>
    private void ShowMessageInfo(bool group, IReadOnlyList<ReceiptDto>? receipts)
    {
        if (_infoMessage is not { } m) return;
        MessageInfoRows.Children.Clear();
        MessageInfoRows.Children.Add(Preview(m));
        if (receipts is null)
        {
            MessageInfoRows.Children.Add(new ProgressRing { Width = 24, Height = 24, IsActive = true, Margin = new Thickness(0, 24, 0, 0) });
            return;
        }

        var read = receipts.Where(r => r.Status >= 3).ToList();
        var delivered = receipts.Where(r => r.Status == 2).ToList();
        if (group)
        {
            Section("Read by", Delivery.Read, read.Select(r => (r, r.Ts)).ToList());
            Section("Delivered to", Delivery.Delivered, delivered.Select(r => (r, r.Ts)).ToList());
            if (read.Count + delivered.Count == 0)
                MessageInfoRows.Children.Add(Hint(m.Delivery >= Delivery.Delivered
                    ? "Who got it isn't known on this PC (it arrived before WAFluent kept receipts)."
                    : "Not delivered yet."));
        }
        else
        {
            // 1:1: when it was read and when it arrived.
            var person = receipts.FirstOrDefault();
            long? readAt = person is { Status: >= 3 } ? person.Ts : null;
            long? deliveredAt = person is null ? null : person.DeliveredTs > 0 ? person.DeliveredTs : person.Status == 2 ? person.Ts : null;
            AddStatus("Read", Delivery.Read, readAt, m.Delivery == Delivery.Read);
            AddStatus("Delivered", Delivery.Delivered, deliveredAt ?? readAt, m.Delivery >= Delivery.Delivered);
        }
    }

    /// <summary>The message as sent, over the chat wallpaper (like WhatsApp's info screen).</summary>
    private FrameworkElement Preview(Message m)
    {
        var text = m.Kind == MessageKind.Text ? m.Text : Format.QuotePreview(m);
        var bubble = new Border
        {
            Background = Themed.Brush("OutgoingBubbleBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 8, 6),
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 28, 24, 28),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxLines = 8, TextTrimming = TextTrimming.CharacterEllipsis },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right,
                        Children =
                        {
                            new TextBlock { Text = m.Time, FontSize = 11, Foreground = Themed.Brush("MetaTextBrush") },
                            new Controls.DeliveryTicks { Delivery = m.Delivery },
                        },
                    },
                },
            },
        };
        var back = new Grid();
        back.Children.Add(new Controls.TiledBackground
        {
            Background = Themed.Brush("WallpaperColorBrush"),
            Source = (ImageSource)Application.Current.Resources["WallpaperTile"],
            TileOpacity = (double)Application.Current.Resources["WallpaperTileOpacity"],
            TileWidth = (double)Application.Current.Resources["WallpaperTileWidth"],
            TileHeight = (double)Application.Current.Resources["WallpaperTileHeight"],
        });
        back.SizeChanged += (_, e) => back.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        back.Children.Add(bubble);
        return back;
    }

    private void Section(string title, Delivery ticks, List<(ReceiptDto Person, long Ts)> people)
    {
        if (people.Count == 0) return;
        MessageInfoRows.Children.Add(SectionHeader(title, ticks));
        foreach (var (person, ts) in people)
        {
            var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(24, 8, 24, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var avatarPath = ViewModel.Chats.FirstOrDefault(c => c.Id == person.ChatId)?.AvatarPath;
            var picture = new Controls.Redact { VeilRadius = new CornerRadius(24) };
            picture.Children.Add(new Controls.Avatar { DisplayName = person.Name, Source = avatarPath, Size = 48 });
            row.Children.Add(picture);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var name = new Controls.Redact { HorizontalAlignment = HorizontalAlignment.Left };
            name.Children.Add(new TextBlock { Text = person.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(name);
            text.Children.Add(new TextBlock { Text = When(ts), FontSize = 13, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            MessageInfoRows.Children.Add(row);
        }
    }

    private void AddStatus(string title, Delivery ticks, long? ts, bool happened)
    {
        MessageInfoRows.Children.Add(SectionHeader(title, ticks));
        MessageInfoRows.Children.Add(new TextBlock
        {
            Text = ts is { } t ? When(t) : happened ? "Before WAFluent was linked" : "—",
            Margin = new Thickness(50, 0, 24, 8),
            FontSize = 14,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        });
    }

    private static FrameworkElement SectionHeader(string title, Delivery ticks) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Margin = new Thickness(24, 20, 24, 6),
        Children =
        {
            new Controls.DeliveryTicks { Delivery = ticks, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = title, FontSize = 15 },
        },
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24, 16, 24, 0),
        Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
    };

    /// <summary>"Today at 8:12 pm", "Yesterday at …", or the date.</summary>
    private static string When(long unix)
    {
        var at = Format.FromUnix(unix);
        var time = Format.Clock(at);
        if (at.Date == DateTime.Today) return $"Today at {time}";
        if (at.Date == DateTime.Today.AddDays(-1)) return $"Yesterday at {time}";
        return $"{at:d MMMM yyyy} at {time}";
    }
}
