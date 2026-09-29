using Microsoft.UI.Xaml;
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
/// The composer's + menu: send documents, photos and videos, a camera shot, a contact card
/// or a poll. Files are previewed (with a caption) before the core uploads and sends them.
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
            item.Click += (_, _) => _ = text switch
            {
                "Document" => PickDocumentsAsync(),
                "Photos & videos" => PickPhotosAsync(),
                "Camera" => TakePhotoAsync(),
                "Contact" => ShareContactAsync(),
                _ => CreatePollAsync(),
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(AttachButton);
    }

    private Chat? SendTarget => ViewModel.SelectedChat is { } chat && ViewModel.CanSend ? chat : null;

    // ───── Files ─────

    /// <summary>One file ready to go: what the core needs to upload and describe it.</summary>
    private sealed record Outgoing(string Path, string Kind, string Mime, int Width, int Height, int Seconds, string? Thumb, ImageSource? Preview);

    private async Task PickDocumentsAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await PreviewAndSendAsync(files.ToList(), asDocuments: true);
    }

    private static readonly string[] PhotoTypes = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoTypes = [".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".avi", ".webm"];

    private async Task PickPhotosAsync()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var type in PhotoTypes.Concat(VideoTypes)) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await PreviewAndSendAsync(files.ToList(), asDocuments: false);
    }

    /// <summary>Windows' camera window; the photo is previewed like a picked one.</summary>
    private async Task TakePhotoAsync()
    {
        try
        {
            var camera = new Microsoft.Windows.Media.Capture.CameraCaptureUI(AppWindow.Id);
            camera.PhotoSettings.Format = Microsoft.Windows.Media.Capture.CameraCaptureUIPhotoFormat.Jpeg;
            camera.PhotoSettings.AllowCropping = false;
            var photo = await camera.CaptureFileAsync(Microsoft.Windows.Media.Capture.CameraCaptureUIMode.Photo);
            if (photo is not null) await PreviewAndSendAsync([photo], asDocuments: false);
        }
        catch (Exception)
        {
            ShowToast(false, "The camera isn't available.");
        }
    }

    /// <summary>Thumbnails (or file names) and a caption box, then Send uploads each file.</summary>
    private async Task PreviewAndSendAsync(List<StorageFile> files, bool asDocuments)
    {
        if (SendTarget is not { } chat) return;
        var ready = new List<Outgoing>();
        foreach (var file in files.Take(30))
        {
            try { ready.Add(await PrepareAsync(file, asDocuments)); }
            catch (Exception) { ready.Add(new Outgoing(file.Path, "document", Mime(file), 0, 0, 0, null, null)); }
        }

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in ready) strip.Children.Add(PreviewTile(item));
        var caption = new TextBox { PlaceholderText = "Add a caption", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(new ScrollViewer
        {
            Content = strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        });
        panel.Children.Add(caption);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Send to {chat.Name}",
            Content = panel,
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // The caption goes with the first file, as on the phone.
        var text = caption.Text.Trim();
        foreach (var item in ready)
        {
            _core?.SendMedia(chat.Id, item.Path, item.Kind, text, item.Mime, item.Width, item.Height, item.Seconds, item.Thumb);
            text = "";
        }
        ShowToast(true, ready.Count == 1 ? "Sending…" : $"Sending {ready.Count} files…");
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private static FrameworkElement PreviewTile(Outgoing item)
    {
        if (item.Preview is { } preview)
        {
            var tile = new Grid { Width = 120, Height = 120, CornerRadius = new CornerRadius(8) };
            tile.Children.Add(new Image { Source = preview, Stretch = Stretch.UniformToFill });
            if (item.Kind == "video")
                tile.Children.Add(new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)),
                    Child = new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
                });
            return tile;
        }
        var card = new StackPanel { Width = 120, Height = 120, Spacing = 6, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(new Image { Source = FileIcons.For(item.Path), Width = 48, Height = 48 });
        card.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(item.Path),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new Border { Child = card, CornerRadius = new CornerRadius(8), Background = Themed.Brush("FileCardBrush") };
    }

    /// <summary>Size, length and a JPEG preview for pictures and videos; documents go as they are.</summary>
    private static async Task<Outgoing> PrepareAsync(StorageFile file, bool asDocument)
    {
        var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
        var kind = asDocument ? "document" : VideoTypes.Contains(ext) ? "video" : PhotoTypes.Contains(ext) ? "image" : "document";
        if (ext == ".gif" && !asDocument) kind = "document";   // animated GIFs would lose their motion as a photo
        if (kind == "document") return new Outgoing(file.Path, kind, Mime(file), 0, 0, 0, null, null);

        int width, height, seconds = 0;
        if (kind == "video")
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

        using var source = kind == "video"
            ? await file.GetThumbnailAsync(ThumbnailMode.VideosView, 256)
            : await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 256);
        var thumb = await JpegThumbAsync(source);
        var preview = new BitmapImage { DecodePixelWidth = 240 };
        await preview.SetSourceAsync(await file.GetThumbnailAsync(kind == "video" ? ThumbnailMode.VideosView : ThumbnailMode.PicturesView, 240));
        return new Outgoing(file.Path, kind, Mime(file), width, height, seconds, thumb, preview);
    }

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

    // ───── Contact ─────

    /// <summary>Pick someone from your chats and share their card.</summary>
    private async Task ShareContactAsync()
    {
        if (SendTarget is not { } target) return;
        var people = ViewModel.ForwardTargets().Where(c => !c.IsGroup && ContactPhone(c).Length > 0).ToList();
        var search = new TextBox { PlaceholderText = "Search name or number" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 360, ItemsSource = people, DisplayMemberPath = nameof(Chat.Name) };
        search.TextChanged += (_, _) => list.ItemsSource = people
            .Where(c => c.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) || ContactPhone(c).Contains(search.Text.Replace(" ", "")))
            .ToList();
        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(search);
        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Share contact",
            Content = panel,
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is Chat;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not Chat person) return;
        _core?.SendContact(target.Id, person.Name, ContactPhone(person));
        ComposerBox.Focus(FocusState.Programmatic);
    }

    /// <summary>A 1:1 chat's number, digits only ("" when only a LID is known).</summary>
    private static string ContactPhone(Chat chat)
    {
        var digits = new string((chat.PhoneCode + chat.PhoneNational).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length > 0) return digits;
        return chat.Id.EndsWith("@s.whatsapp.net") ? new string(chat.Id.TakeWhile(char.IsAsciiDigit).ToArray()) : "";
    }

    // ───── Poll ─────

    /// <summary>Question, up to 12 options (a new box appears as the last one fills), multiple answers.</summary>
    private async Task CreatePollAsync()
    {
        if (SendTarget is not { } chat) return;
        var question = new TextBox { Header = "Question", PlaceholderText = "Ask question" };
        var options = new StackPanel { Spacing = 6 };
        var multiple = new ToggleSwitch { Header = "Allow multiple answers", IsOn = true };
        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(question);
        panel.Children.Add(new TextBlock { Text = "Options", Margin = new Thickness(0, 4, 0, -4) });
        panel.Children.Add(options);
        panel.Children.Add(multiple);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Create poll",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        List<string> Filled() => options.Children.OfType<TextBox>().Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToList();
        void Validate() => dialog.IsPrimaryButtonEnabled = question.Text.Trim().Length > 0 && Filled().Count >= 2
                                                          && Filled().Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == Filled().Count;
        void AddOption()
        {
            var box = new TextBox { PlaceholderText = "+ Add option", MaxLength = 100 };
            box.TextChanged += (_, _) =>
            {
                var boxes = options.Children.OfType<TextBox>().ToList();
                if (box == boxes[^1] && box.Text.Length > 0 && boxes.Count < 12) AddOption();
                Validate();
            };
            options.Children.Add(box);
        }
        AddOption();
        AddOption();
        question.TextChanged += (_, _) => Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _core?.SendPoll(chat.Id, question.Text.Trim(), Filled(), multiple.IsOn);
        ComposerBox.Focus(FocusState.Programmatic);
    }
}
