using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using WhatsAppNative.Controls;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WhatsAppNative;

/// <summary>
/// The Status page (the rail's rings), like WhatsApp's.
///   Left   : My status (what you posted from your phone), then Recent (people with an update
///            you haven't looked at) and Viewed. The ring round a picture has a piece per update,
///            green until you've looked at it.
///   Viewer : over the whole window. A person's updates play one after another, a bar each along
///            the top (six seconds for a picture or text, a video for its length), then the next
///            person's. Hold the picture to pause, click its left or right side (or the arrows,
///            or ← →) to step, Esc to close. A reply, typed or one of the emoji, goes to your
///            chat with them, quoting the update. Looking at an update tells its author.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>How long a picture or a text update stays up.</summary>
    private const double StatusSeconds = 6;

    private static readonly string[] StatusReactions = ["😍", "😂", "😮", "😢", "🙏", "👏", "🎉", "💯"];

    private static readonly string PlayGlyph = char.ConvertFromUtf32(0xE768), PauseGlyph = char.ConvertFromUtf32(0xE769),
                                   SoundGlyph = char.ConvertFromUtf32(0xE767), MutedGlyph = char.ConvertFromUtf32(0xE74F);

    private bool _statusOpen;
    private List<StatusPerson> _statusPeople = [];
    /// <summary>The people the viewer goes through, in the order the list had when it opened.</summary>
    private List<string> _statusOrder = [];
    private StatusPerson? _statusPerson;
    private int _statusIndex;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private MediaPlayer? _statusPlayer;
    private DateTime _statusTick, _statusPressedAt;
    private double _statusElapsed, _statusLength = StatusSeconds;
    /// <summary>Ready: what's on screen has loaded. Plays: it's a video or a voice update (the player keeps the time).</summary>
    private bool _statusReady, _statusPlays, _statusPaused, _statusHeld, _statusReplying, _statusMuted;
    private readonly List<Border> _statusFills = [];

    private bool StatusStopped => _statusPaused || _statusHeld || _statusReplying;

    private StatusDto? StatusShown => _statusPerson is { } p && _statusIndex < p.Items.Count ? p.Items[_statusIndex] : null;

    private void SetupStatus()
    {
        ViewModel.StatusesChanged += OnStatusesChanged;
        ViewModel.StatusMedia += OnStatusMedia;
        foreach (var emoji in StatusReactions)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = emoji, FontSize = 26, FontFamily = (FontFamily)Application.Current.Resources["EmojiFontFamily"] },
                Padding = new Thickness(4, 2, 4, 4),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                AllowFocusOnInteraction = false,   // the reply box keeps the caret
            };
            AutomationProperties.SetName(button, $"Reply with {emoji}");
            button.Click += (_, _) => SendStatusReply(emoji);
            StatusEmojis.Children.Add(button);
        }
#if DEBUG
        // WAFLUENT_SELFTEST=status | status-view | status-reply | status-mine: the page on the sample data.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is { } test && test.StartsWith("status"))
            Root.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                Nav.SelectedItem = Nav.MenuItems[2];
                await Task.Delay(600);
                var person = test == "status-mine" ? _statusPeople.FirstOrDefault(p => p.Mine) : _statusPeople.Where(p => !p.Mine && !p.AllSeen).OrderByDescending(p => p.Items.Count).FirstOrDefault();
                if (test == "status" || person is null) return;
                OpenStatusViewer(person, test == "status-view" ? 1 : 0);
                if (test == "status-view") _statusPaused = true;
                if (test == "status-reply") StatusReply.Focus(FocusState.Programmatic);
            };
#endif
    }

    /// <summary>The rail's Status was picked (or left).</summary>
    private void ShowStatus(bool show)
    {
        _statusOpen = show;
        StatusPanel.Visibility = StatusPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        FillStatuses();
        ViewModel.LoadStatuses();
    }

    // ───── The list ─────

    private void FillStatuses()
    {
        _statusPeople = StatusPerson.Group(ViewModel.Statuses);
        StatusRows.Children.Clear();

        var mine = _statusPeople.FirstOrDefault(p => p.Mine);
        var me = new StatusPerson { Author = "", Name = "My status", AvatarPath = ViewModel.SelfAvatarPath, Items = mine?.Items ?? [] };
        StatusRows.Children.Add(StatusRow(me, mine is null ? "No updates in the last 24 hours" : StatusPerson.When(mine.LastTs)));

        var others = _statusPeople.Where(p => !p.Mine).OrderByDescending(p => p.LastTs).ToList();
        foreach (var (title, people) in new[] { ("Recent", others.Where(p => !p.AllSeen).ToList()), ("Viewed", others.Where(p => p.AllSeen).ToList()) })
        {
            if (people.Count == 0) continue;
            StatusRows.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                Margin = new Thickness(14, 18, 0, 6),
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
            foreach (var person in people) StatusRows.Children.Add(StatusRow(person, StatusPerson.When(person.LastTs)));
        }
        if (others.Count == 0)
            StatusRows.Children.Add(new TextBlock
            {
                Text = "No recent updates from your contacts.",
                Margin = new Thickness(14, 22, 14, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
    }

    private Button StatusRow(StatusPerson person, string detail)
    {
        var picture = new Grid { Width = 54, Height = 54 };
        foreach (var piece in StatusRing(person.Items.Select(s => s.Seen).ToList(), 54)) picture.Children.Add(piece);
        var veil = new Redact { VeilRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        veil.Children.Add(new Avatar { DisplayName = person.Mine ? ViewModel.SelfName : person.Name, Source = person.AvatarPath, Size = 44 });
        picture.Children.Add(veil);

        var name = new Redact { HorizontalAlignment = HorizontalAlignment.Left };
        name.Children.Add(new TextBlock { Text = person.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        text.Children.Add(person.Mine ? new TextBlock { Text = person.Name, FontSize = 15 } : name);
        text.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = Themed.Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(picture);
        content.Children.Add(text);
        var row = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 8, 8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsEnabled = person.Items.Count > 0 || !person.Mine,
        };
        var author = person.Author;
        row.Click += (_, _) =>
        {
            // The row may be older than the list: whoever it was about, as they are now.
            if (_statusPeople.FirstOrDefault(p => p.Author == author) is { } now) OpenStatusViewer(now, now.FirstUnseen);
        };
        return row;
    }

    /// <summary>The ring round a picture: a piece per update, clockwise from the top, green until it's been looked at.</summary>
    private static IEnumerable<Path> StatusRing(IReadOnlyList<bool> seen, double size)
    {
        var n = seen.Count;
        if (n == 0) yield break;
        var (centre, radius) = (size / 2, size / 2 - 1.5);
        var sweep = 360.0 / n;
        var gap = n == 1 ? 0 : Math.Min(10, sweep * 0.3);
        Point At(double degrees)
        {
            var a = degrees * Math.PI / 180;
            return new Point(centre + radius * Math.Cos(a), centre + radius * Math.Sin(a));
        }
        for (var i = 0; i < n; i++)
        {
            var brush = seen[i] ? Themed.Brush("TextFillColorTertiaryBrush") : AppColors.StatusRing;
            Geometry shape;
            if (n == 1)
            {
                shape = new EllipseGeometry { Center = new Point(centre, centre), RadiusX = radius, RadiusY = radius };
            }
            else
            {
                var (from, to) = (-90 + i * sweep + gap / 2, -90 + (i + 1) * sweep - gap / 2);
                var figure = new PathFigure { StartPoint = At(from), IsClosed = false };
                figure.Segments.Add(new ArcSegment
                {
                    Point = At(to),
                    Size = new Size(radius, radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = to - from > 180,
                });
                var path = new PathGeometry();
                path.Figures.Add(figure);
                shape = path;
            }
            yield return new Path
            {
                Data = shape,
                Stroke = brush,
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
        }
    }

    private void OnStatusesChanged()
    {
        if (_statusOpen) FillStatuses();
        else _statusPeople = StatusPerson.Group(ViewModel.Statuses);
        RemapStatus();
    }

    /// <summary>The list changed under the viewer: it keeps showing the same update, from the new list.</summary>
    private void RemapStatus()
    {
        if (_statusPerson is not { } was || StatusShown is not { } shown) return;
        var now = _statusPeople.FirstOrDefault(p => p.Author == was.Author);
        var at = now?.Items.FindIndex(s => s.Message.Id == shown.Message.Id) ?? -1;
        if (now is null || at < 0)
        {
            CloseStatusViewer();   // it was taken back, or its 24 hours ran out
            return;
        }
        var bars = now.Items.Count != was.Items.Count || at != _statusIndex;
        (_statusPerson, _statusIndex) = (now, at);
        if (bars) BuildStatusBars(now.Items.Count, at);
        StatusViewsText.Text = now.Items[at].Views == 1 ? "1 view" : $"{now.Items[at].Views} views";
    }

    // ───── The viewer ─────

    private void OpenStatusViewer(StatusPerson person, int index)
    {
        if (person.Items.Count == 0) return;
        _statusOrder = person.Mine
            ? [""]
            : _statusPeople.Where(p => !p.Mine).OrderBy(p => p.AllSeen).ThenByDescending(p => p.LastTs).Select(p => p.Author).ToList();
        _statusPaused = false;
        StatusViewer.Visibility = Visibility.Visible;
        if (_statusTimer is null)
        {
            _statusTimer = DispatcherQueue.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromMilliseconds(33);
            _statusTimer.Tick += (_, _) => StatusTick();
        }
        _statusTimer.Start();
        ShowStatusItem(person, Math.Clamp(index, 0, person.Items.Count - 1));
        StatusPause.Focus(FocusState.Programmatic);   // the keys go to the viewer (Space pauses)
    }

    private void CloseStatusViewer()
    {
        _statusTimer?.Stop();
        StopStatusPlayer();
        (_statusPerson, _statusReplying, _statusHeld) = (null, false, false);
        StatusQuick.Visibility = Visibility.Collapsed;
        StatusReply.Text = "";
        StatusViewer.Visibility = Visibility.Collapsed;
        StatusImage.Source = StatusBlur.Source = StatusBackdrop.Source = null;
    }

    private void StopStatusPlayer()
    {
        if (_statusPlayer is null) return;
        _statusPlayer.Pause();
        _statusPlayer.Source = null;
    }

    private void ShowStatusItem(StatusPerson person, int index)
    {
        (_statusPerson, _statusIndex) = (person, index);
        var status = person.Items[index];
        var m = status.Message;
        StopStatusPlayer();
        (_statusElapsed, _statusLength, _statusReady, _statusPlays) = (0, StatusSeconds, false, false);
        _statusTick = DateTime.UtcNow;

        StatusAvatar.DisplayName = person.Mine ? ViewModel.SelfName : person.Name;
        StatusAvatar.Source = person.Mine ? ViewModel.SelfAvatarPath : person.AvatarPath;
        StatusName.Text = person.Mine ? "You" : person.Name;
        StatusTime.Text = StatusPerson.When(m.Ts);
        BuildStatusBars(person.Items.Count, index);

        var thumb = Ui.Thumb(m.Thumb);
        StatusBackdrop.Source = StatusBlur.Source = thumb;
        StatusImage.Source = null;
        StatusVideo.Visibility = StatusTextCard.Visibility = StatusFailed.Visibility = Visibility.Collapsed;
        StatusLoading.IsActive = false;
        var caption = m.Kind == "text" ? "" : m.Text;
        StatusCaption.Text = caption;
        StatusCaptionBar.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusMute.Visibility = m.Kind is "video" or "voice" or "audio" ? Visibility.Visible : Visibility.Collapsed;
        StatusReplyBar.Visibility = person.Mine ? Visibility.Collapsed : Visibility.Visible;
        StatusViews.Visibility = person.Mine ? Visibility.Visible : Visibility.Collapsed;
        StatusViewsText.Text = status.Views == 1 ? "1 view" : $"{status.Views} views";
        StatusPrev.Visibility = index > 0 || _statusOrder.IndexOf(person.Author) > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowStatusPause();

        if (m.Kind == "text")
        {
            ShowStatusCard(m.Text, StatusColour(m));
            _statusReady = true;
        }
        else if (m.Media?.Path is { } path && File.Exists(path))
        {
            LoadStatusMedia(m, path);
        }
        else
        {
            StatusLoading.IsActive = true;
            ViewModel.DownloadStatus(m.Id);
        }
        // The one after it is fetched meanwhile, so it's there when its turn comes.
        if (index + 1 < person.Items.Count && person.Items[index + 1].Message is { Media: { Path: null } } next) ViewModel.DownloadStatus(next.Id);
        ViewModel.StatusSeen(status);
    }

    /// <summary>A text update's card: its words on its colour.</summary>
    private void ShowStatusCard(string text, Windows.UI.Color colour)
    {
        StatusTextCard.Background = new SolidColorBrush(colour);
        StatusTextBody.Text = text;
        StatusTextBody.FontSize = text.Length < 40 ? 32 : text.Length < 140 ? 24 : text.Length < 400 ? 19 : 16;
        StatusTextCard.Visibility = Visibility.Visible;
    }

    private static Windows.UI.Color StatusColour(MessageDto m)
    {
        uint argb = 0;
        if (m.Extra is { ValueKind: System.Text.Json.JsonValueKind.Object } extra && extra.TryGetProperty("status", out var style)
            && style.TryGetProperty("background", out var background) && background.TryGetInt64(out var value))
            argb = (uint)value;
        if (argb == 0) argb = 0xFF1E6E4F;
        return Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private void LoadStatusMedia(MessageDto m, string path)
    {
        StatusLoading.IsActive = false;
        StatusFailed.Visibility = Visibility.Collapsed;
        if (m.Kind == "image")
        {
            var image = new BitmapImage(new Uri(path));
            StatusImage.Source = image;
            if (StatusBackdrop.Source is null) StatusBackdrop.Source = image;
            (_statusReady, _statusTick) = (true, DateTime.UtcNow);
            return;
        }
        if (_statusPlayer is null)
        {
            _statusPlayer = new MediaPlayer();
            StatusVideo.SetMediaPlayer(_statusPlayer);
            _statusPlayer.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_statusPlays) return;
                StatusLoading.IsActive = false;
                _statusReady = true;
            });
            _statusPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_statusPlays && _statusReady) NextStatus(); });
            _statusPlayer.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_statusPlays) return;
                // Shown as a failure for the usual six seconds, then on to the next.
                (_statusPlays, _statusReady, _statusElapsed) = (false, true, 0);
                StatusLoading.IsActive = false;
                StatusFailed.Visibility = Visibility.Visible;
            });
        }
        var sound = m.Kind is "voice" or "audio";
        if (sound) ShowStatusCard("Voice update", Windows.UI.Color.FromArgb(255, 0x1E, 0x6E, 0x4F));
        StatusVideo.Visibility = sound ? Visibility.Collapsed : Visibility.Visible;
        StatusLoading.IsActive = true;
        (_statusPlays, _statusLength) = (true, m.Media?.Seconds > 0 ? m.Media.Seconds : 30);
        _statusPlayer.IsMuted = _statusMuted;
        _statusPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
        if (!StatusStopped) _statusPlayer.Play();
    }

    private void OnStatusMedia(string id, string? path)
    {
        _statusPeople = StatusPerson.Group(ViewModel.Statuses);   // the file is on the update now
        RemapStatus();
        if (StatusShown is not { } shown || shown.Message.Id != id || _statusReady || _statusPlays) return;
        if (path is not null)
        {
            LoadStatusMedia(shown.Message, path);
            return;
        }
        StatusLoading.IsActive = false;
        StatusFailed.Visibility = Visibility.Visible;
        (_statusReady, _statusTick) = (true, DateTime.UtcNow);   // it moves on after the usual time
    }

    private void BuildStatusBars(int count, int index)
    {
        StatusBars.Children.Clear();
        StatusBars.ColumnDefinitions.Clear();
        _statusFills.Clear();
        for (var i = 0; i < count; i++)
        {
            StatusBars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var fill = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
            var track = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 255, 255, 255)),
                CornerRadius = new CornerRadius(1.5),
                Child = fill,
            };
            Grid.SetColumn(track, i);
            StatusBars.Children.Add(track);
            _statusFills.Add(fill);
        }
        SetStatusProgress(index, _statusLength > 0 ? _statusElapsed / _statusLength : 0);
    }

    /// <summary>The bars before this one are full, this one is `fraction` full, the rest are empty.</summary>
    private void SetStatusProgress(int index, double fraction)
    {
        for (var i = 0; i < _statusFills.Count; i++)
        {
            var width = ((FrameworkElement)_statusFills[i].Parent).ActualWidth;
            _statusFills[i].Width = i < index ? width : i > index ? 0 : width * Math.Clamp(fraction, 0, 1);
        }
    }

    private void StatusTick()
    {
        var now = DateTime.UtcNow;
        var passed = (now - _statusTick).TotalSeconds;
        _statusTick = now;
        if (_statusPerson is null) return;
        if (_statusPlays)
        {
            if (_statusReady && _statusPlayer is { } player)
            {
                var session = player.PlaybackSession;
                if (session.NaturalDuration.TotalSeconds > 0) _statusLength = session.NaturalDuration.TotalSeconds;
                _statusElapsed = session.Position.TotalSeconds;
            }
        }
        else if (_statusReady && !StatusStopped)
        {
            _statusElapsed += passed;
        }
        SetStatusProgress(_statusIndex, _statusElapsed / _statusLength);
        if (!_statusPlays && _statusElapsed >= _statusLength) NextStatus();
    }

    private void NextStatus()
    {
        if (_statusPerson is not { } person) return;
        if (_statusIndex + 1 < person.Items.Count)
        {
            ShowStatusItem(person, _statusIndex + 1);
            return;
        }
        for (var i = _statusOrder.IndexOf(person.Author) + 1; i > 0 && i < _statusOrder.Count; i++)
            if (_statusPeople.FirstOrDefault(p => p.Author == _statusOrder[i]) is { } next)
            {
                ShowStatusItem(next, next.FirstUnseen);
                return;
            }
        CloseStatusViewer();
    }

    private void PreviousStatus()
    {
        if (_statusPerson is not { } person) return;
        if (_statusIndex > 0)
        {
            ShowStatusItem(person, _statusIndex - 1);
            return;
        }
        for (var i = _statusOrder.IndexOf(person.Author) - 1; i >= 0; i--)
            if (_statusPeople.FirstOrDefault(p => p.Author == _statusOrder[i]) is { } previous)
            {
                ShowStatusItem(previous, previous.FirstUnseen);
                return;
            }
        ShowStatusItem(person, 0);   // the very first: from its start again
    }

    /// <summary>Paused or held or replying: the bar stops, and so does a video.</summary>
    private void ShowStatusPause()
    {
        StatusPauseIcon.Glyph = _statusPaused ? PlayGlyph : PauseGlyph;
        ToolTipService.SetToolTip(StatusPause, _statusPaused ? "Play" : "Pause");
        StatusMuteIcon.Glyph = _statusMuted ? MutedGlyph : SoundGlyph;
        ToolTipService.SetToolTip(StatusMute, _statusMuted ? "Unmute" : "Mute");
        if (!_statusPlays || _statusPlayer is not { } player) return;
        if (StatusStopped) player.Pause();
        else player.Play();
    }

    private void StatusPause_Click(object sender, RoutedEventArgs e)
    {
        _statusPaused = !_statusPaused;
        ShowStatusPause();
    }

    private void StatusMute_Click(object sender, RoutedEventArgs e)
    {
        _statusMuted = !_statusMuted;
        if (_statusPlayer is { } player) player.IsMuted = _statusMuted;
        ShowStatusPause();
    }

    private void StatusClose_Click(object sender, RoutedEventArgs e) => CloseStatusViewer();

    private void StatusPrev_Click(object sender, RoutedEventArgs e) => PreviousStatus();

    private void StatusNext_Click(object sender, RoutedEventArgs e) => NextStatus();

    private void StatusViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape: CloseStatusViewer(); break;
            case VirtualKey.Left: PreviousStatus(); break;
            case VirtualKey.Right: NextStatus(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>The picture is as tall as the window allows and 9:16, like a phone's screen.</summary>
    private void StatusViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = Math.Max(200, e.NewSize.Height - 74);   // less the reply box
        StatusStage.Width = Math.Max(220, Math.Min(e.NewSize.Width - 240, height * 9 / 16));
    }

    // Hold to pause; a short click on the left third goes back, anywhere else goes on.

    private void StatusCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_statusReplying)
        {
            EndStatusReply();
            return;
        }
        (_statusHeld, _statusPressedAt) = (true, DateTime.UtcNow);
        StatusCard.CapturePointer(e.Pointer);
        ShowStatusPause();
    }

    private void StatusCard_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_statusHeld) return;
        _statusHeld = false;
        StatusCard.ReleasePointerCapture(e.Pointer);
        if ((DateTime.UtcNow - _statusPressedAt).TotalMilliseconds < 300)
        {
            if (e.GetCurrentPoint(StatusCard).Position.X < StatusCard.ActualWidth / 3) PreviousStatus();
            else NextStatus();
        }
        else
        {
            ShowStatusPause();
        }
    }

    private void StatusCard_PointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_statusHeld) return;
        _statusHeld = false;
        ShowStatusPause();
    }

    // ───── Replying ─────

    private void StatusReply_GotFocus(object sender, RoutedEventArgs e)
    {
        _statusReplying = true;
        StatusQuick.Visibility = Visibility.Visible;
        ShowStatusPause();
    }

    private void StatusReply_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) SendStatusReply(StatusReply.Text);
        else if (e.Key == VirtualKey.Escape) EndStatusReply();
        else return;
        e.Handled = true;
    }

    private void StatusSend_Click(object sender, RoutedEventArgs e) => SendStatusReply(StatusReply.Text);

    private void SendStatusReply(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || StatusShown is not { } shown || shown.Author.Length == 0) return;
        ViewModel.ReplyStatus(shown, text);
        StatusReply.Text = "";
        EndStatusReply();
    }

    private void EndStatusReply()
    {
        _statusReplying = false;
        StatusQuick.Visibility = Visibility.Collapsed;
        StatusPause.Focus(FocusState.Programmatic);
        ShowStatusPause();
    }
}
