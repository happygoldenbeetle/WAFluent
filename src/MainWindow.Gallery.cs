using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

/// <summary>
/// Contact info › Media, links and docs, like WhatsApp: tabs for Media (photos, videos and GIFs
/// in a grid of three), Docs and Links, each newest first under "Recent", "This month", then
/// month headings. Everything the chat ever had comes from the core's store, not just the
/// messages loaded on screen. A photo or video opens in the viewer (downloading first if
/// needed), a document in its app, a link in the browser.
/// </summary>
public sealed partial class MainWindow
{
    private ChatMediaSet? _galleryData;

    private void SetupGallery()
    {
        ViewModel.ChatMediaLoaded += set =>
        {
            if (set.Chat != ViewModel.SelectedChat) return;
            _galleryData = set;
            if (GalleryView.Visibility == Visibility.Visible) ShowGalleryTab();
            else if (InfoPanel.Visibility == Visibility.Visible && ContactInfoView.Visibility == Visibility.Visible) RebuildInfo();
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) _galleryData = null;   // another chat's
        };
    }

    private void OpenGallery()
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        ContactInfoView.Visibility = Visibility.Collapsed;
        NewContactView.Visibility = Visibility.Collapsed;
        SearchView.Visibility = Visibility.Collapsed;
        MessageInfoView.Visibility = Visibility.Collapsed;
        DisappearingView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Visible;
        InfoTitle.Text = "Media, links and docs";
        InfoCloseIcon.Glyph = "";   // back to Contact info
        GalleryTabs.Items[0].IsSelected = true;
        ShowGalleryTab();
        ViewModel.LoadChatMedia(chat);   // fresh: anything sent since
    }

    private void GalleryTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowGalleryTab();

    private void ShowGalleryTab()
    {
        GalleryContent.Children.Clear();
        GalleryScroll.ChangeView(null, 0, null, disableAnimation: true);
        if (_galleryData is not { } data || data.Chat != ViewModel.SelectedChat)
        {
            GalleryLoading.IsActive = true;
            return;
        }
        GalleryLoading.IsActive = false;
        switch (GalleryTabs.Items.IndexOf(GalleryTabs.SelectedItem))
        {
            case 1: ShowDocs(data.Docs); break;
            case 2: ShowLinks(data.Links); break;
            default: ShowMedia(data.Media); break;
        }
    }

    // ───── Media ─────

    private void ShowMedia(IReadOnlyList<Message> media)
    {
        if (media.Count == 0)
        {
            GalleryContent.Children.Add(Nothing("", "No media"));
            return;
        }
        foreach (var period in media.GroupBy(m => Period(m.Timestamp)))
        {
            GalleryContent.Children.Add(Heading(period.Key));
            GalleryContent.Children.Add(new ItemsRepeater
            {
                ItemsSource = period.ToList(),
                ItemTemplate = (DataTemplate)Root.Resources["GalleryTileTemplate"],
                Layout = new UniformGridLayout
                {
                    MinItemWidth = 108,
                    MinItemHeight = 108,
                    MinRowSpacing = 4,
                    MinColumnSpacing = 4,
                    MaximumRowsOrColumns = 3,
                    ItemsStretch = UniformGridLayoutItemsStretch.Fill,
                },
            });
        }
    }

    private void GalleryTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message m } tile) return;
        if (m.Kind == MessageKind.Video) WhenDownloaded(m, _ => OpenVideo(m, tile));
        else WhenDownloaded(m, _ => OpenViewer(m, tile));
    }

    /// <summary>Contact info: the four latest photos and videos under the row; any opens the gallery.</summary>
    private FrameworkElement GalleryStrip(ChatMediaSet set)
    {
        var strip = new Grid { ColumnSpacing = 6, Margin = new Thickness(12, 0, 12, 8) };
        var shown = set.Media.Take(4).ToList();
        for (var i = 0; i < 4; i++)
        {
            strip.ColumnDefinitions.Add(new ColumnDefinition());
            if (i >= shown.Count) continue;
            var m = shown[i];
            var tile = new Grid { Height = 76, CornerRadius = new CornerRadius(6), Background = Themed.Brush("FileCardBrush") };
            if ((Ui.GalleryImage(m.MediaPath, m.Kind) ?? Ui.Thumb(m.Thumb)) is { } picture)
                tile.Children.Add(new Border { Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill } });
            if (m.Kind == MessageKind.Video)
                tile.Children.Add(new FontIcon
                {
                    Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 6),
                });
            tile.Tapped += (_, _) => OpenGallery();
            Grid.SetColumn(tile, i);
            strip.Children.Add(tile);
        }
        return strip;
    }

    // ───── Docs ─────

    private void ShowDocs(IReadOnlyList<Message> docs)
    {
        if (docs.Count == 0)
        {
            GalleryContent.Children.Add(Nothing("", "No documents"));
            return;
        }
        foreach (var period in docs.GroupBy(m => Period(m.Timestamp)))
        {
            GalleryContent.Children.Add(Heading(period.Key));
            foreach (var m in period)
            {
                var icon = new Image { Source = FileIcons.For(m.FileName), Width = 36, Height = 36 };
                GalleryContent.Children.Add(GalleryRow(icon, m.FileName.Length > 0 ? m.FileName : "Document", m.FileInfo, m.Timestamp,
                    () => WhenDownloaded(m, MediaActions.OpenExternally)));
            }
        }
    }

    // ───── Links ─────

    private void ShowLinks(IReadOnlyList<Message> links)
    {
        if (links.Count == 0)
        {
            GalleryContent.Children.Add(Nothing("", "No links"));
            return;
        }
        foreach (var period in links.GroupBy(m => Period(m.Timestamp)))
        {
            GalleryContent.Children.Add(Heading(period.Key));
            foreach (var m in period)
            {
                var url = MainViewModel.FirstUrl(m);
                var target = Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var u) ? u : null;
                FrameworkElement leading = Ui.Thumb(m.Thumb) is { } thumb
                    ? new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(6), Background = new ImageBrush { ImageSource = thumb, Stretch = Stretch.UniformToFill } }
                    : new Grid
                    {
                        Width = 48, Height = 48, CornerRadius = new CornerRadius(6), Background = Themed.Brush("FileCardBrush"),
                        Children = { new FontIcon { Glyph = "", FontSize = 18, Foreground = Themed.Brush("TextFillColorSecondaryBrush") } },
                    };
                var title = m.LinkTitle.Length > 0 ? m.LinkTitle : target?.Host ?? url;
                GalleryContent.Children.Add(GalleryRow(leading, title, url, m.Timestamp,
                    () => { if (target is not null) _ = Windows.System.Launcher.LaunchUriAsync(target); }));
            }
        }
    }

    // ───── Pieces ─────

    /// <summary>"Recent" (the last week), "This month", then "September", or "December 2025" for past years ("Older": no date).</summary>
    private static string Period(DateTime t)
    {
        var now = DateTime.Now;
        if (t == default) return "Older";
        if (t > now.AddDays(-7)) return "Recent";
        if (t.Year == now.Year && t.Month == now.Month) return "This month";
        return t.Year == now.Year ? t.ToString("MMMM") : t.ToString("MMMM yyyy");
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Margin = new Thickness(4, 14, 0, 8),
        Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
    };

    private static StackPanel Nothing(string glyph, string text) => new()
    {
        Spacing = 10,
        Margin = new Thickness(0, 80, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        Children =
        {
            new FontIcon { Glyph = glyph, FontSize = 36, Foreground = Themed.Brush("TextFillColorTertiaryBrush") },
            new TextBlock { Text = text, Foreground = Themed.Brush("TextFillColorSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Center },
        },
    };

    /// <summary>A document or link: picture, name, detail line, date on the right; the whole row clicks.</summary>
    private static Button GalleryRow(FrameworkElement leading, string title, string detail, DateTime when, Action open)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leading.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(leading);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0)
            texts.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var date = new TextBlock
        {
            Text = when == default ? "" : when.ToString(when.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        };
        Grid.SetColumn(date, 2);
        grid.Children.Add(date);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 8, 8, 8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        button.Click += (_, _) => open();
        return button;
    }
}
