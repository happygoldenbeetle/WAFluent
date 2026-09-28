using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WhatsAppNative.Controls;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The in-app emoji keyboard (composer button, "+" reactions, Settings) and :shortcode:
/// autocomplete in the composer (":sob" + Enter or Tab → 😭; typing ":sob:" converts by itself).
/// </summary>
public sealed partial class MainWindow
{
    // ───────────── Emoji keyboard ─────────────

    private Flyout? _emojiFlyout;
    private EmojiPicker? _emojiPicker;
    private Action<string>? _emojiPicked;
    private bool _emojiCloseOnPick;

    /// <summary>
    /// Opens the emoji keyboard at <paramref name="anchor"/>. The composer keeps it open for
    /// several emoji; reactions and Settings close it after one.
    /// </summary>
    private void OpenEmojiPicker(FrameworkElement anchor, Action<string> picked, bool closeOnPick,
                                 FlyoutPlacementMode placement = FlyoutPlacementMode.Auto)
    {
        if (_emojiFlyout is null)
        {
            _emojiPicker = new EmojiPicker { Settings = _ui };
            _emojiPicker.Picked += emoji =>
            {
                if (_emojiCloseOnPick) _emojiFlyout?.Hide();
                _emojiPicked?.Invoke(emoji);
            };
            var presenter = new Style(typeof(FlyoutPresenter));
            presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 1000.0));
            presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 1000.0));
            presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            _emojiFlyout = new Flyout { Content = _emojiPicker, FlyoutPresenterStyle = presenter, ShouldConstrainToRootBounds = true };
        }
        _emojiPicked = picked;
        _emojiCloseOnPick = closeOnPick;
        _emojiPicker!.Reset(focusSearch: true);
        _emojiFlyout.Placement = placement;
        if (_emojiFlyout.IsOpen) _emojiFlyout.Hide();
        try
        {
            _emojiFlyout.ShowAt(anchor);
        }
        catch (ArgumentException)
        {
            // The anchor left the tree (a recycled message row, a closed menu's item): show it by the composer.
            _emojiFlyout.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;
            try { _emojiFlyout.ShowAt(ComposerBox.XamlRoot is null ? Root : ComposerBox); }
            catch (ArgumentException) { }   // still closing from the last time: the next click works
        }
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var caret = ComposerBox.SelectionStart;
        OpenEmojiPicker(button, emoji =>
        {
            caret = Math.Min(caret, ComposerBox.Text.Length);
            ComposerBox.Text = ComposerBox.Text.Insert(caret, emoji);
            caret += emoji.Length;
            ComposerBox.SelectionStart = caret;
        }, closeOnPick: false, FlyoutPlacementMode.TopEdgeAlignedLeft);
        _emojiFlyout!.Closed += BackToComposer;

        void BackToComposer(object? s, object a)
        {
            _emojiFlyout!.Closed -= BackToComposer;
            ComposerBox.Focus(FocusState.Programmatic);
            ComposerBox.SelectionStart = Math.Min(caret, ComposerBox.Text.Length);
        }
    }

    // ───────────── :shortcode: autocomplete ─────────────

    // A ":" counts when it follows anything but a letter, digit or colon: "hi :so", "😭:so" yes; "http://", "10:30" no.
    private static readonly Regex ShortcodeTail = new(@"(?:^|[^\p{L}\p{N}_:])(:([a-z0-9_+\-]{2,}))$", RegexOptions.IgnoreCase);
    private static readonly Regex FinishedShortcode = new(@"(?:^|[^\p{L}\p{N}_:])(:([a-z0-9_+\-]+):)$", RegexOptions.IgnoreCase);

    private List<(EmojiEntry Entry, string Code)> _suggestions = [];
    private int _suggestion;
    private int _tokenStart;   // where ":so" starts in the composer text

    /// <summary>Runs on every composer change: converts ":sob:" and offers matches for ":so".</summary>
    private void UpdateShortcodes()
    {
        var caret = ComposerBox.SelectionStart;
        var before = ComposerBox.Text[..Math.Min(caret, ComposerBox.Text.Length)];

        // A finished ":sob:" turns into 😭 right away.
        if (FinishedShortcode.Match(before) is { Success: true } done && EmojiData.ByCode(done.Groups[2].Value) is { } exact)
        {
            ReplaceToken(done.Groups[1].Index, done.Groups[1].Length, exact.Emoji);
            return;
        }

        var m = ShortcodeTail.Match(before);
        _suggestions = m.Success ? EmojiData.Shortcodes(m.Groups[2].Value) : [];
        if (_suggestions.Count == 0)
        {
            HideShortcodes();
            return;
        }
        _tokenStart = m.Groups[1].Index;
        _suggestion = 0;
        RenderShortcodes();
        ShortcodePanel.Visibility = Visibility.Visible;
    }

    private void RenderShortcodes()
    {
        ShortcodeList.Children.Clear();
        for (var i = 0; i < _suggestions.Count; i++)
        {
            var (entry, code) = _suggestions[i];
            var index = i;
            var row = new Grid
            {
                Padding = new Thickness(10, 6, 14, 6),
                ColumnSpacing = 10,
                CornerRadius = new CornerRadius(4),
                Background = i == _suggestion
                    ? Helpers.Themed.Brush("SubtleFillColorSecondaryBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = entry.Emoji, FontSize = 20, FontFamily = EmojiPicker.EmojiFont, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = $":{code}:", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            row.PointerEntered += (_, _) => { _suggestion = index; RenderShortcodes(); };
            row.Tapped += (_, e) => { e.Handled = true; _suggestion = index; ApplyShortcode(); };
            ShortcodeList.Children.Add(row);
        }
    }

    /// <summary>Keys while the list is open: ↑/↓ choose, Enter/Tab insert, Esc closes. Returns true if handled.</summary>
    private bool ShortcodeKey(VirtualKey key)
    {
        if (ShortcodePanel.Visibility != Visibility.Visible) return false;
        switch (key)
        {
            case VirtualKey.Up:
                _suggestion = (_suggestion - 1 + _suggestions.Count) % _suggestions.Count;
                RenderShortcodes();
                return true;
            case VirtualKey.Down:
                _suggestion = (_suggestion + 1) % _suggestions.Count;
                RenderShortcodes();
                return true;
            case VirtualKey.Enter or VirtualKey.Tab:
                ApplyShortcode();
                return true;
            case VirtualKey.Escape:
                HideShortcodes();
                return true;
        }
        return false;
    }

    private void ApplyShortcode()
    {
        if (_suggestion >= _suggestions.Count) return;
        var caret = ComposerBox.SelectionStart;
        ReplaceToken(_tokenStart, caret - _tokenStart, _suggestions[_suggestion].Entry.Emoji);
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void ReplaceToken(int start, int length, string emoji)
    {
        HideShortcodes();
        var text = ComposerBox.Text;
        ComposerBox.Text = text[..start] + emoji + text[(start + length)..];
        ComposerBox.SelectionStart = start + emoji.Length;
    }

    /// <summary>Moving the caret away from ":so" closes the list.</summary>
    private void ComposerBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (ShortcodePanel.Visibility == Visibility.Visible) UpdateShortcodes();
    }

    private void HideShortcodes()
    {
        _suggestions = [];
        ShortcodePanel.Visibility = Visibility.Collapsed;
    }
}
