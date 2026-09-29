using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Services;

namespace WhatsAppNative.Controls;

/// <summary>What the emoji grid shows for one emoji: your chosen skin tone of it, if any.</summary>
public sealed class EmojiCell
{
    public required string Glyph { get; init; }
    public required EmojiEntry Entry { get; init; }
    public string Name => Entry.Name;
    public Visibility ToneMark => Entry.HasSkins ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// WhatsApp-style emoji keyboard: Recent + eight category tabs, search (names, :shortcodes:
/// and keywords), and skin tones (right-click or hold an emoji with a corner mark). Emoji are
/// drawn with the app's emoji font. Remembers recents and each emoji's chosen tone.
/// </summary>
public sealed partial class EmojiPicker : UserControl
{
    private const int RecentTab = -1;
    private const int MaxRecent = 40;

    // Tab faces: a clock for Recent, then one emoji per category.
    private static readonly string[] TabFaces = ["😀", "🐻", "🍔", "⚽", "🚗", "💡", "🔣", "🏳️"];

    /// <summary>App.xaml's emoji-first font.</summary>
    public static FontFamily EmojiFont => (FontFamily)Application.Current.Resources["EmojiFontFamily"];

    private readonly List<FrameworkElement> _tabButtons = new();
    private int _tab = int.MinValue;
    private DispatcherTimer? _holdTimer;
    private bool _holdFired;

    /// <summary>Recents and skin tones live here (saved by the host).</summary>
    public UiSettings? Settings { get; set; }

    /// <summary>An emoji was chosen.</summary>
    public event Action<string>? Picked;

    public EmojiPicker()
    {
        InitializeComponent();
        BuildTabs();
    }

    /// <summary>Call before showing: fresh search, Recent if there are any.</summary>
    public void Reset(bool focusSearch)
    {
        HideTones();
        SearchBox.Text = "";
        ShowTab(Settings?.RecentEmoji.Count > 0 ? RecentTab : 0);
        if (focusSearch) DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    // ───────────── Tabs ─────────────

    private void BuildTabs()
    {
        AddTab(new FontIcon { Glyph = "", FontSize = 16 }, RecentTab, "Recent");
        for (var i = 0; i < TabFaces.Length; i++)
            AddTab(new TextBlock { Text = TabFaces[i], FontSize = 19, FontFamily = EmojiFont }, i, EmojiData.TabNames[i]);

        void AddTab(FrameworkElement face, int tab, string name)
        {
            face.HorizontalAlignment = HorizontalAlignment.Center;
            face.VerticalAlignment = VerticalAlignment.Center;
            var button = new Grid { Width = 40, Height = 38, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Tag = tab, Opacity = 0.55 };
            button.Children.Add(face);
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Tapped += (_, e) => { e.Handled = true; SearchBox.Text = ""; ShowTab(tab); };
            button.PointerEntered += (_, _) => { if (_tab != tab) button.Opacity = 0.85; };
            button.PointerExited += (_, _) => { if (_tab != tab) button.Opacity = 0.55; };
            _tabButtons.Add(button);
            Tabs.Children.Add(button);
        }
    }

    private void ShowTab(int tab)
    {
        HideTones();
        _tab = tab;
        var entries = tab == RecentTab
            ? (Settings?.RecentEmoji ?? []).Select(EmojiData.Find).OfType<EmojiEntry>().ToList()
            : EmojiData.InTab(tab).ToList();
        SectionTitle.Text = tab == RecentTab ? "Recent" : EmojiData.TabNames[tab];
        Show(entries, tab == RecentTab ? Settings?.RecentEmoji : null);
        MarkTab(tab);
    }

    private void MarkTab(int? tab)
    {
        for (var i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].Opacity = (int)_tabButtons[i].Tag == tab ? 1 : 0.55;
        TabLine.Visibility = tab is null ? Visibility.Collapsed : Visibility.Visible;
        if (tab is null) return;

        // Slide the underline to the tab.
        var index = _tabButtons.FindIndex(b => (int)b.Tag == tab);
        ElementCompositionPreview.SetIsTranslationEnabled(TabLine, true);
        var visual = ElementCompositionPreview.GetElementVisual(TabLine);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3((float)(index * 40 + 8), 0, 0);
        spring.DampingRatio = 0.8f;
        spring.Period = TimeSpan.FromMilliseconds(40);
        visual.StartAnimation("Translation", spring);
    }

    /// <summary>Recent keeps the exact emoji you used (tone included); categories use your preferred tone.</summary>
    private void Show(List<EmojiEntry> entries, IReadOnlyList<string>? exact)
    {
        Cells.ItemsSource = entries.Select((e, i) => new EmojiCell
        {
            Entry = e,
            Glyph = exact is not null ? exact[i] : PreferredTone(e),
        }).ToList();
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _tab == RecentTab && SearchBox.Text.Length == 0 ? "Emoji you use show up here" : "No emoji found";
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private string PreferredTone(EmojiEntry e) =>
        e.HasSkins && Settings?.SkinTones.TryGetValue(e.Emoji, out var tone) == true && e.Skins.Contains(tone) ? tone : e.Emoji;

    // ───────────── Search ─────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            if (_tab == int.MaxValue) ShowTab(Settings?.RecentEmoji.Count > 0 ? RecentTab : 0);
            return;
        }
        HideTones();
        _tab = int.MaxValue;   // "search results"
        MarkTab(null);
        SectionTitle.Text = "Search results";
        Show(EmojiData.Search(query), null);
    }

    /// <summary>Enter picks the first result.</summary>
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || Cells.ItemsSource is not List<EmojiCell> { Count: > 0 } cells) return;
        e.Handled = true;
        Pick(cells[0].Glyph, cells[0].Entry);
    }

    // ───────────── Picking ─────────────

    private void Pick(string glyph, EmojiEntry entry)
    {
        HideTones();
        if (Settings is { } settings)
        {
            settings.RecentEmoji.RemoveAll(r => EmojiData.Find(r) == entry);
            settings.RecentEmoji.Insert(0, glyph);
            if (settings.RecentEmoji.Count > MaxRecent) settings.RecentEmoji.RemoveRange(MaxRecent, settings.RecentEmoji.Count - MaxRecent);
            settings.Save();
        }
        Picked?.Invoke(glyph);
    }

    private void Cell_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_holdFired) { _holdFired = false; return; }   // the hold already opened the tones
        if (sender is FrameworkElement { Tag: EmojiCell cell }) Pick(cell.Glyph, cell.Entry);
    }

    private void Cell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: EmojiCell cell } element && cell.Entry.HasSkins) ShowTones(element, cell.Entry);
    }

    // Hold (mouse, pen or touch) for skin tones.
    private void Cell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EmojiCell cell } element || !cell.Entry.HasSkins) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;
        _holdFired = false;
        _holdTimer?.Stop();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            _holdFired = true;
            ShowTones(element, cell.Entry);
        };
        _holdTimer.Start();
    }

    private void Cell_PointerReleased(object sender, PointerRoutedEventArgs e) => _holdTimer?.Stop();

    private void Cell_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid cell) cell.Background = Helpers.Themed.Brush("SubtleFillColorSecondaryBrush");
    }

    private void Cell_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid cell) cell.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _holdTimer?.Stop();
    }

    // ───────────── Skin tones ─────────────

    private void ShowTones(FrameworkElement cell, EmojiEntry entry)
    {
        ToneChoices.Children.Clear();
        foreach (var glyph in entry.Skins.Prepend(entry.Emoji))
        {
            var choice = new Grid { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            choice.Children.Add(new TextBlock { Text = glyph, FontSize = 26, FontFamily = EmojiFont, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            choice.PointerEntered += (_, _) => choice.Background = Helpers.Themed.Brush("SubtleFillColorSecondaryBrush");
            choice.PointerExited += (_, _) => choice.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            choice.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (Settings is { } settings)
                {
                    if (glyph == entry.Emoji) settings.SkinTones.Remove(entry.Emoji);
                    else settings.SkinTones[entry.Emoji] = glyph;
                }
                RefreshCell(entry);
                Pick(glyph, entry);
            };
            ToneChoices.Children.Add(choice);
            if (glyph == entry.Emoji)
                ToneChoices.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 1, Margin = new Thickness(3, 8, 3, 8),
                    Fill = Helpers.Themed.Brush("DividerStrokeColorDefaultBrush"),
                });
        }

        // Above the emoji, kept inside the keyboard.
        ToneBar.Visibility = Visibility.Visible;
        ToneBar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var at = cell.TransformToVisual(ToneLayer).TransformPoint(default);
        var width = ToneBar.DesiredSize.Width;
        Canvas.SetLeft(ToneBar, Math.Clamp(at.X + cell.ActualWidth / 2 - width / 2, 4, Math.Max(4, ToneLayer.ActualWidth - width - 4)));
        Canvas.SetTop(ToneBar, Math.Max(0, at.Y - ToneBar.DesiredSize.Height - 2));

        var visual = ElementCompositionPreview.GetElementVisual(ToneBar);
        visual.CenterPoint = new Vector3((float)width / 2, (float)ToneBar.DesiredSize.Height, 0);
        var pop = visual.Compositor.CreateSpringVector3Animation();
        pop.InitialValue = new Vector3(0.6f, 0.6f, 1);
        pop.FinalValue = Vector3.One;
        pop.DampingRatio = 0.6f;
        pop.Period = TimeSpan.FromMilliseconds(40);
        visual.StartAnimation("Scale", pop);
    }

    private void HideTones() => ToneBar.Visibility = Visibility.Collapsed;

    private void Root_Tapped(object sender, TappedRoutedEventArgs e) => HideTones();

    /// <summary>After choosing a tone, the category grid shows it for that emoji.</summary>
    private void RefreshCell(EmojiEntry entry)
    {
        if (_tab is RecentTab or int.MaxValue || Cells.ItemsSource is not List<EmojiCell> cells) return;
        var i = cells.FindIndex(c => c.Entry == entry);
        if (i < 0) return;
        cells[i] = new EmojiCell { Entry = entry, Glyph = PreferredTone(entry) };
        Cells.ItemsSource = null;
        Cells.ItemsSource = cells;
    }
}
