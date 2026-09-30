using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// The composer's + menu, laid out like WhatsApp Web: documents, photos and videos and camera
/// shots open a full send preview over the conversation (a caption each, a thumbnail strip,
/// + for more, Esc asks before discarding); contacts and polls open as cards.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The menu's icons, filled like WhatsApp's, on a 16 px grid (Segoe Fluent Icons has no
    /// filled photo, camera or poll). F0 = even-odd, for the cut-outs.
    /// </summary>
    private static readonly (string Text, string Icon, uint Color)[] AttachItems =
    [
        ("Document", "F0 M4.2,1 H9 V3.8 A1.2,1.2 0 0 0 10.2,5 H13 V13.8 A1.2,1.2 0 0 1 11.8,15 H4.2 A1.2,1.2 0 0 1 3,13.8 V2.2 A1.2,1.2 0 0 1 4.2,1 Z " +
                     "M5.4,8.2 H10.6 V9.3 H5.4 Z M5.4,10.8 H10.6 V11.9 H5.4 Z M10.2,1.3 L12.7,3.8 H10.7 A0.5,0.5 0 0 1 10.2,3.3 Z", 0xFF7F66FF),
        ("Photos & videos", "F0 M3.2,1.8 H12.8 A2.2,2.2 0 0 1 15,4 V12 A2.2,2.2 0 0 1 12.8,14.2 H3.2 A2.2,2.2 0 0 1 1,12 V4 A2.2,2.2 0 0 1 3.2,1.8 Z " +
                            "M3,12.2 L6.3,8.3 L8.4,10.6 L10.4,8.2 L13,12.2 Z M10.8,3.9 A1.5,1.5 0 1 1 10.79,3.9 Z", 0xFF007BFC),
        ("Camera", "F0 M5.8,2.2 H10.2 A1,1 0 0 1 11.1,2.8 L11.7,4 H13.4 A1.6,1.6 0 0 1 15,5.6 V12.4 A1.6,1.6 0 0 1 13.4,14 H2.6 A1.6,1.6 0 0 1 1,12.4 V5.6 " +
                   "A1.6,1.6 0 0 1 2.6,4 H4.3 L4.9,2.8 A1,1 0 0 1 5.8,2.2 Z M8,5.6 A3.1,3.1 0 1 1 7.99,5.6 Z M8,7.3 A1.4,1.4 0 1 1 7.99,7.3 Z", 0xFFFF2E74),
        ("Contact", "M8,1.3 A3.1,3.1 0 1 1 7.99,1.3 Z M2.4,13.9 C2.4,10.6 4.9,8.9 8,8.9 C11.1,8.9 13.6,10.6 13.6,13.9 A0.9,0.9 0 0 1 12.7,14.8 H3.3 A0.9,0.9 0 0 1 2.4,13.9 Z", 0xFF009DE2),
        ("Poll", "M2.3,2.2 H9.7 A1.3,1.3 0 0 1 9.7,4.8 H2.3 A1.3,1.3 0 0 1 2.3,2.2 Z M2.3,6.7 H13.7 A1.3,1.3 0 0 1 13.7,9.3 H2.3 A1.3,1.3 0 0 1 2.3,6.7 Z " +
                 "M2.3,11.2 H6.7 A1.3,1.3 0 0 1 6.7,13.8 H2.3 A1.3,1.3 0 0 1 2.3,11.2 Z", 0xFFFFBC38),
    ];

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["AttachMenuPresenterStyle"],
        };
        foreach (var (text, data, color) in AttachItems)
        {
            var icon = new PathIcon
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var item = new MenuFlyoutItem { Text = text, Icon = icon };
            item.Click += (_, _) =>
            {
                switch (text)
                {
                    case "Document": _ = PickDocumentsAsync(); break;
                    case "Photos & videos": _ = PickPhotosAsync(); break;
                    case "Camera": _ = TakePhotoAsync(); break;
                    case "Contact": OpenContacts(); break;
                    default: OpenPoll(); break;
                }
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(AttachButton);
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Message m }) ViewModel.CancelUpload(m);
    }

    private Chat? SendTarget => ViewModel.SelectedChat is { } chat && ViewModel.CanSend ? chat : null;

    // ───────────── Send preview (documents, photos and videos, camera) ─────────────

    private readonly List<OutgoingFile> _outgoing = [];
    private OutgoingFile? _shown;
    private bool _composingDocuments;
    private bool _hd;
    private MediaPlayerElement? _stagePlayer;

    private static readonly string[] PhotoTypes = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoTypes = [".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".avi", ".webm"];

    private async Task PickDocumentsAsync()
    {
        if (await PickAsync(documents: true) is { Count: > 0 } files) await OpenComposerAsync(files, documents: true);
    }

    private async Task PickPhotosAsync()
    {
        if (await PickAsync(documents: false) is { Count: > 0 } files) await OpenComposerAsync(files, documents: false);
    }

    private async Task<IReadOnlyList<StorageFile>?> PickAsync(bool documents)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = documents ? PickerViewMode.List : PickerViewMode.Thumbnail,
            SuggestedStartLocation = documents ? PickerLocationId.DocumentsLibrary : PickerLocationId.PicturesLibrary,
        };
        if (documents) picker.FileTypeFilter.Add("*");
        else foreach (var type in PhotoTypes.Concat(VideoTypes)) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickMultipleFilesAsync();
    }

    /// <summary>Windows' camera window; the photo opens in the send preview.</summary>
    private async Task TakePhotoAsync()
    {
        try
        {
            var camera = new Microsoft.Windows.Media.Capture.CameraCaptureUI(AppWindow.Id);
            camera.PhotoSettings.Format = Microsoft.Windows.Media.Capture.CameraCaptureUIPhotoFormat.Jpeg;
            camera.PhotoSettings.AllowCropping = false;
            var photo = await camera.CaptureFileAsync(Microsoft.Windows.Media.Capture.CameraCaptureUIMode.Photo);
            if (photo is not null) await OpenComposerAsync([photo], documents: false);
        }
        catch (Exception)
        {
            ShowToast(false, "The camera isn't available.");
        }
    }

    /// <summary>Opens the preview with these files (or adds them to the one that's open).</summary>
    private async Task OpenComposerAsync(IReadOnlyList<StorageFile> files, bool documents)
    {
        if (SendTarget is null) return;
        if (MediaComposer.Visibility != Visibility.Visible)
        {
            _outgoing.Clear();
            _composingDocuments = documents;
            _hd = _ui.HdMedia;
        }
        OutgoingFile? first = null;
        foreach (var file in files)
        {
            if (_outgoing.Count >= 30) break;
            OutgoingFile item;
            try { item = await PrepareAsync(file, _composingDocuments); }
            catch (Exception) { item = await PlainAsync(file); }
            _outgoing.Add(item);
            first ??= item;
        }
        MediaComposer.Visibility = Visibility.Visible;
        Show(first ?? _outgoing.LastOrDefault());
        MediaCaptionBox.Focus(FocusState.Programmatic);
    }

    private async void MediaComposerAdd_Click(object sender, RoutedEventArgs e)
    {
        if (await PickAsync(_composingDocuments) is { Count: > 0 } files) await OpenComposerAsync(files, _composingDocuments);
    }

    /// <summary>Shows one file big, with its own caption, and marks it in the strip.</summary>
    private void Show(OutgoingFile? item)
    {
        _shown = item;
        StopStagePlayer();
        MediaComposerStage.Children.Clear();
        RebuildStrip();
        UpdateHdButton();
        if (item is null) return;
        MediaComposerTitle.Text = item.FileName;
        MediaCaptionBox.Text = item.Caption;
        MediaCaptionBox.SelectionStart = item.Caption.Length;

        if (item.Kind is "image" or "gif" && item.Preview is not null)
        {
            var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            image.Source = new BitmapImage(new Uri(item.Path));
            MediaComposerStage.Children.Add(image);
        }
        else if (item.Kind == "video" || item.IsAudio)
        {
            _stagePlayer = new MediaPlayerElement
            {
                Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.Path)),
                AreTransportControlsEnabled = true,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (item.IsAudio)
            {
                _stagePlayer.Width = 460;
                _stagePlayer.Height = 110;
                _stagePlayer.TransportControls.IsCompact = true;
            }
            MediaComposerStage.Children.Add(_stagePlayer);
        }
        else
        {
            // No preview: WhatsApp's card, with the file type's own icon.
            var card = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            card.Children.Add(new Image { Source = FileIcons.For(item.Path), Width = 110, Height = 110, Margin = new Thickness(0, 0, 0, 16) });
            card.Children.Add(new TextBlock { Text = "No preview available", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
            card.Children.Add(new TextBlock { Text = item.SizeLabel, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
            MediaComposerStage.Children.Add(new Border
            {
                Width = 520,
                Height = 360,
                MaxWidth = 9999,
                CornerRadius = new CornerRadius(8),
                Background = Themed.Brush("ComposerFieldBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = card,
            });
        }
    }

    /// <summary>The thumbnails under the caption: the shown one ringed in green, ✕ on hover to drop one.</summary>
    private void RebuildStrip()
    {
        MediaComposerStrip.Children.Clear();
        foreach (var item in _outgoing)
        {
            FrameworkElement face;
            if (item.Preview is { } preview)
                face = new Image { Source = preview, Stretch = Stretch.UniformToFill };
            else if (item.IsAudio)
                face = new Grid
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xA6, 0x1A)),
                    Children = { new FontIcon { Glyph = "", FontSize = 20, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } },
                };
            else
                face = new Grid
                {
                    Background = Themed.Brush("ComposerFieldBrush"),
                    Children = { new Image { Source = FileIcons.For(item.Path), Width = 32, Height = 32 } },
                };
            var selected = item == _shown;
            var tile = new Grid
            {
                Width = 60,
                Height = 60,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 0),
                BorderBrush = Themed.Brush("ChatAccentBrush"),
                Padding = new Thickness(selected ? 2 : 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            tile.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = face });
            var remove = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "", FontSize = 9, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                Visibility = Visibility.Collapsed,
            };
            ToolTipService.SetToolTip(remove, "Remove");
            remove.Click += (_, _) => Remove(item);
            tile.Children.Add(remove);
            tile.PointerEntered += (_, _) => remove.Visibility = Visibility.Visible;
            tile.PointerExited += (_, _) => remove.Visibility = Visibility.Collapsed;
            tile.Tapped += (_, e) => { if (e.OriginalSource is not FontIcon) Show(item); };
            ToolTipService.SetToolTip(tile, item.FileName);
            MediaComposerStrip.Children.Add(tile);
        }
    }

    private void Remove(OutgoingFile item)
    {
        var index = _outgoing.IndexOf(item);
        _outgoing.Remove(item);
        if (_outgoing.Count == 0)
        {
            CloseComposer();
            return;
        }
        Show(item == _shown ? _outgoing[Math.Min(index, _outgoing.Count - 1)] : _shown);
    }

    /// <summary>HD pill: green when on; hidden for documents, dimmed when nothing is bigger than standard.</summary>
    private void UpdateHdButton()
    {
        HdButton.Visibility = _composingDocuments ? Visibility.Collapsed : Visibility.Visible;
        var available = _outgoing.Any(o => o.SupportsHd);
        var on = _hd && available;
        HdButton.IsEnabled = available;
        HdButton.Background = on ? Themed.Brush("ChatAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HdButton.BorderBrush = on ? Themed.Brush("ChatAccentBrush") : Themed.Brush("TextFillColorSecondaryBrush");
        HdLabel.Foreground = on ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x0B, 0x14, 0x1A)) : Themed.Brush("TextFillColorSecondaryBrush");
        ToolTipService.SetToolTip(HdButton, !available ? "HD isn't available: these are no bigger than standard quality"
                                           : on ? "HD quality (click for standard)" : "Standard quality (click for HD)");
    }

    private void HdToggle_Click(object sender, RoutedEventArgs e)
    {
        _hd = !_hd;
        UpdateHdButton();
        ShowToast(true, _hd ? "HD quality" : "Standard quality");
    }

    private void HdMedia_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ui.HdMedia == HdMediaSwitch.IsOn) return;
        _ui.HdMedia = HdMediaSwitch.IsOn;
        _ui.Save();
    }

    private void MediaCaption_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_shown is not null) _shown.Caption = MediaCaptionBox.Text;
    }

    private void MediaCaption_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift) return;
        e.Handled = true;
        SendComposer();
    }

    private void MediaCaptionEmoji_Click(object sender, RoutedEventArgs e) => InsertEmoji(MediaCaptionEmoji, MediaCaptionBox);

    /// <summary>The emoji keyboard, typing into <paramref name="box"/> at its caret.</summary>
    private void InsertEmoji(FrameworkElement anchor, TextBox box)
    {
        var caret = box.SelectionStart;
        OpenEmojiPicker(anchor, emoji =>
        {
            caret = Math.Min(caret, box.Text.Length);
            box.Text = box.Text.Insert(caret, emoji);
            caret += emoji.Length;
            box.SelectionStart = caret;
        }, closeOnPick: false, Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight);
    }

    private void MediaComposerSend_Click(object sender, RoutedEventArgs e) => SendComposer();

    private void SendComposer()
    {
        if (SendTarget is not { } chat || _outgoing.Count == 0) return;
        foreach (var item in _outgoing) _ = ViewModel.SendFileAsync(item, hd: _hd && item.SupportsHd);
        CloseComposer();
        ScrollToBottom();
    }

    private void MediaComposerClose_Click(object sender, RoutedEventArgs e) => _ = DiscardComposerAsync();

    private void MediaComposerEscape_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (MediaComposer.Visibility != Visibility.Visible) return;
        args.Handled = true;
        _ = DiscardComposerAsync();
    }

    /// <summary>Esc or ✕: "Discard selection?" first, like WhatsApp.</summary>
    private async Task DiscardComposerAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Discard selection?",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) CloseComposer();
    }

    private void CloseComposer()
    {
        StopStagePlayer();
        _outgoing.Clear();
        _shown = null;
        MediaComposerStage.Children.Clear();
        MediaComposerStrip.Children.Clear();
        MediaComposer.Visibility = Visibility.Collapsed;
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void StopStagePlayer()
    {
        if (_stagePlayer is null) return;
        _stagePlayer.MediaPlayer?.Pause();
        _stagePlayer.Source = null;
        _stagePlayer = null;
    }

    /// <summary>Size, length and a JPEG preview for pictures and videos; other files as they are.</summary>
    private static async Task<OutgoingFile> PrepareAsync(StorageFile file, bool asDocument)
    {
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
        var kind = asDocument ? "document" : ext == ".gif" ? "gif" : VideoTypes.Contains(ext) ? "video" : PhotoTypes.Contains(ext) ? "image" : "document";
        if (kind == "document") return await PlainAsync(file);
        var isVideo = kind == "video" || (kind == "gif" && MediaCompression.Sniff(file.Path) == "mp4");

        int width, height, seconds = 0;
        if (isVideo)
        {
            var props = await file.Properties.GetVideoPropertiesAsync();
            (width, height, seconds) = ((int)props.Width, (int)props.Height, (int)Math.Round(props.Duration.TotalSeconds));
            if (props.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270) (width, height) = (height, width);
        }
        else
        {
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            (width, height) = ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        var mode = isVideo ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView;
        string? thumb;
        using (var source = await file.GetThumbnailAsync(mode, 256)) thumb = await JpegThumbAsync(source);
        var preview = new BitmapImage { DecodePixelWidth = 120 };
        using (var small = await file.GetThumbnailAsync(mode, 120)) await preview.SetSourceAsync(small);
        var size = (long)(await file.GetBasicPropertiesAsync()).Size;
        return new OutgoingFile
        {
            Path = file.Path, Kind = kind, Mime = Mime(file), Width = width, Height = height, Seconds = seconds,
            Thumb = thumb, Preview = preview, Size = size,
        };
    }

    private static async Task<OutgoingFile> PlainAsync(StorageFile file) => new()
    {
        Path = file.Path,
        Kind = "document",
        Mime = Mime(file),
        Size = (long)(await file.GetBasicPropertiesAsync()).Size,
    };

    /// <summary>A small JPEG (longest side 100 px) for the bubble's preview, in %TEMP%.</summary>
    private static async Task<string?> JpegThumbAsync(Windows.Storage.Streams.IRandomAccessStream? image)
    {
        if (image is null) return null;
        var decoder = await BitmapDecoder.CreateAsync(image);
        var scale = Math.Min(1, 100.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                                                     ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WAFluent", "thumbs");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jpg");
        using (var output = File.Create(path).AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
        }
        return path;
    }

    private static string Mime(StorageFile file) =>
        string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;

    // ───────────── Cards: Send contacts, Create poll ─────────────

    private void ShowSheet(FrameworkElement card)
    {
        ContactsCard.Visibility = card == ContactsCard ? Visibility.Visible : Visibility.Collapsed;
        PollCard.Visibility = card == PollCard ? Visibility.Visible : Visibility.Collapsed;
        AttachSheet.Visibility = Visibility.Visible;
    }

    private void CloseSheet()
    {
        AttachSheet.Visibility = Visibility.Collapsed;
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void AttachSheet_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseSheet();

    /// <summary>Clicks inside a card don't reach the dimmed backdrop (which closes it).</summary>
    private void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => e.Handled = true;

    private void AttachSheetClose_Click(object sender, RoutedEventArgs e) => CloseSheet();

    private void AttachSheetEscape_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (AttachSheet.Visibility != Visibility.Visible) return;
        args.Handled = true;
        CloseSheet();
    }

    // ───── Send contacts ─────

    private List<ContactRow> _contacts = [];
    private ContactRow? _meRow;
    /// <summary>Set while the card picks chats to forward these to (null: sending contacts).</summary>
    private IReadOnlyList<Message>? _forwarding;

    /// <summary>You first, then your 1:1 chats by name; tick several to send them together.</summary>
    private void OpenContacts()
    {
        if (SendTarget is null) return;
        _forwarding = null;
        ContactsTitle.Text = "Send contacts";
        AutomationProperties.SetName(ContactsSend, "Send contacts");
        var sample = _core is null;   // --sample: no numbers, but the list still shows
        _meRow = ViewModel.SelfPhone.Length > 0
            ? new ContactRow { Name = ViewModel.SelfName.Length > 0 ? ViewModel.SelfName : "You", Phone = ViewModel.SelfPhone, AvatarPath = ViewModel.SelfAvatarPath }
            : null;
        _contacts = ViewModel.ForwardTargets()
            .Where(c => !c.IsGroup && (sample || ContactPhone(c).Length > 0))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContactRow
            {
                Name = c.Name,
                Phone = ContactPhone(c),
                Subtitle = c.PhoneCode.Length > 0 ? $"{c.PhoneCode} {c.PhoneNational}" : "",
                AvatarPath = c.AvatarPath,
            })
            .ToList();
        ContactSearch.Text = "";
        FilterContacts();
        UpdatePickedContacts();
        ShowSheet(ContactsCard);
        ContactSearch.Focus(FocusState.Programmatic);
    }

    private void ContactSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterContacts();

    private void FilterContacts()
    {
        var query = ContactSearch.Text.Trim();
        var digits = new string(query.Where(char.IsAsciiDigit).ToArray());
        bool Matches(ContactRow r) => query.Length == 0
            || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (digits.Length > 0 && r.Phone.Contains(digits));
        var groups = new List<ContactGroup>();
        if (_meRow is not null && Matches(_meRow)) groups.Add(new ContactGroup("You", [_meRow]));
        var others = _contacts.Where(Matches).ToList();
        if (others.Count > 0) groups.Add(new ContactGroup(_forwarding is null ? "Contacts" : "Recent chats", others));
        ContactList.ItemsSource = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
    }

    private void ContactList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ContactRow row) return;
        row.Picked = !row.Picked;
        UpdatePickedContacts();
    }

    private IEnumerable<ContactRow> PickedContacts =>
        (_meRow is null ? _contacts : _contacts.Prepend(_meRow)).Where(r => r.Picked);

    private void UpdatePickedContacts()
    {
        var picked = PickedContacts.ToList();
        ContactsPicked.Text = string.Join(", ", picked.Select(r => r.Name));
        ContactsSend.IsEnabled = picked.Count > 0;
    }

    private void ContactsSend_Click(object sender, RoutedEventArgs e)
    {
        if (_forwarding is { } messages)
        {
            var to = PickedContacts.Select(r => r.Chat).OfType<Chat>().ToList();
            if (to.Count > 0) ViewModel.Forward(messages, to);
            ViewModel.EndSelect();
            CloseSheet();
            return;
        }
        if (SendTarget is not { } chat) return;
        var picked = PickedContacts.Where(r => r.Phone.Length > 0).Select(r => (r.Name, r.Phone)).ToList();
        if (picked.Count > 0) _core?.SendContacts(chat.Id, picked);
        CloseSheet();
    }

    /// <summary>A 1:1 chat's number, digits only ("" when only a LID is known).</summary>
    private static string ContactPhone(Chat chat)
    {
        var digits = new string((chat.PhoneCode + chat.PhoneNational).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length > 0) return digits;
        return chat.Id.EndsWith("@s.whatsapp.net") ? new string(chat.Id.TakeWhile(char.IsAsciiDigit).ToArray()) : "";
    }

    // ───── Create poll ─────

    public System.Collections.ObjectModel.ObservableCollection<PollOptionDraft> PollOptions { get; } = [];

    private void OpenPoll()
    {
        if (SendTarget is null) return;
        PollQuestion.Text = "";
        PollOptions.Clear();
        PollOptions.Add(new PollOptionDraft());
        PollOptions.Add(new PollOptionDraft());
        PollMultiple.IsOn = true;
        PollHideVoters.IsOn = false;
        PollEnds.IsOn = false;
        PollEndRow.Visibility = Visibility.Collapsed;
        var tomorrow = DateTimeOffset.Now.AddDays(1);
        PollEndDate.Date = tomorrow.Date;
        PollEndTime.Time = new TimeSpan(tomorrow.Hour, tomorrow.Minute, 0);
        PollSend.IsEnabled = false;
        ShowSheet(PollCard);
        PollQuestion.Focus(FocusState.Programmatic);
    }

    /// <summary>Keeps one empty box at the end (up to 12 options) and checks the poll can be sent.</summary>
    private void PollInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: PollOptionDraft draft } box) draft.Text = box.Text;
        if (PollOptions.Count > 0 && PollOptions[^1].Text.Length > 0 && PollOptions.Count < 12) PollOptions.Add(new PollOptionDraft());
        ValidatePoll();
    }

    /// <summary>An emptied box in the middle goes away (at least two stay).</summary>
    private void PollOption_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: PollOptionDraft draft } || draft.Text.Trim().Length > 0) return;
        if (PollOptions.Count > 2 && PollOptions.IndexOf(draft) < PollOptions.Count - 1)
            DispatcherQueue.TryEnqueue(() => { PollOptions.Remove(draft); ValidatePoll(); });
    }

    private void PollEmoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var box = button.Tag as TextBox ?? (button.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault();
        if (box is not null) InsertEmoji(button, box);
    }

    private void PollEnds_Toggled(object sender, RoutedEventArgs e)
    {
        PollEndRow.Visibility = PollEnds.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ValidatePoll();
    }

    private List<string> PollAnswers() => PollOptions.Select(o => o.Text.Trim()).Where(t => t.Length > 0).ToList();

    private DateTimeOffset? PollEnd() => PollEnds.IsOn && PollEndDate.Date is { } date
        ? new DateTimeOffset(date.Date + PollEndTime.Time, DateTimeOffset.Now.Offset)
        : null;

    private void ValidatePoll()
    {
        var answers = PollAnswers();
        PollSend.IsEnabled = PollQuestion.Text.Trim().Length > 0
                             && answers.Count >= 2
                             && answers.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == answers.Count
                             && (PollEnd() is not { } end || end > DateTimeOffset.Now);
    }

    private void PollSend_Click(object sender, RoutedEventArgs e)
    {
        if (SendTarget is not { } chat) return;
        _core?.SendPoll(chat.Id, PollQuestion.Text.Trim(), PollAnswers(), PollMultiple.IsOn, PollHideVoters.IsOn, PollEnd()?.ToUnixTimeSeconds());
        CloseSheet();
    }
}
