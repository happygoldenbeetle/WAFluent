using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Groups, like WhatsApp: the card a group chat starts with (its picture, name, "2 members ·
/// Created today by …", description, Add members), and Group info's customisation — rename
/// (the pencil by the name), description, picture (click it: upload or take a photo), Add
/// members and Search. Changes go to WhatsApp at once; if the group only lets admins make
/// them, a notice says so and the old value comes back.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The group Add group members is adding to (null: making a new group).</summary>
    private Chat? _addingTo;

    // ───── Name ─────

    private void InfoNameEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedChat is not { IsGroup: true } chat) return;
        GroupNameBox.Text = chat.Name;
        InfoNameView.Visibility = InfoNameEdit.Visibility = Visibility.Collapsed;
        InfoNameEditor.Visibility = Visibility.Visible;
        GroupNameBox.Focus(FocusState.Programmatic);
        GroupNameBox.SelectAll();
    }

    private void InfoNameSave_Click(object sender, RoutedEventArgs e) => SaveGroupName();

    private void GroupNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; SaveGroupName(); }
        else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; ShowGroupNameView(); }
    }

    private void SaveGroupName()
    {
        var name = GroupNameBox.Text.Trim();
        if (ViewModel.SelectedChat is { IsGroup: true } chat && name.Length > 0 && name != chat.Name) ViewModel.RenameGroup(chat, name);
        ShowGroupNameView();
    }

    /// <summary>The name as text again, with the pencil for groups.</summary>
    private void ShowGroupNameView()
    {
        var group = ViewModel.SelectedChat is { IsGroup: true };
        InfoNameEditor.Visibility = Visibility.Collapsed;
        InfoNameView.Visibility = Visibility.Visible;
        InfoNameEdit.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        InfoGroupLine.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───── Description ─────

    /// <summary>"Add group description" (green) or the description, with a pencil; the pencil turns it into a box and a tick.</summary>
    private FrameworkElement GroupDescriptionRow(Chat chat)
    {
        var host = new Grid { Margin = new Thickness(12, 10, 4, 10), ColumnSpacing = 8 };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void Show()
        {
            host.Children.Clear();
            var has = chat.GroupDescription.Length > 0;
            var text = new TextBlock
            {
                Text = has ? chat.GroupDescription : "Add group description",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = has,
                Foreground = Themed.Brush(has ? "TextFillColorPrimaryBrush" : "ChatAccentTextBrush"),
            };
            var pencil = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], Content = new FontIcon { Glyph = "\uE70F", FontSize = 15 } };
            ToolTipService.SetToolTip(pencil, "Edit description");
            pencil.Click += (_, _) => Edit();
            if (!has) { HandCursor.SetOn(text, true); text.Tapped += (_, _) => Edit(); }
            Grid.SetColumn(pencil, 1);
            host.Children.Add(text);
            host.Children.Add(pencil);
        }

        void Edit()
        {
            host.Children.Clear();
            var box = new TextBox { Text = chat.GroupDescription, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 2048, MaxHeight = 160, PlaceholderText = "Group description" };
            var done = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], Content = new FontIcon { Glyph = "\uE73E", FontSize = 15 }, VerticalAlignment = VerticalAlignment.Top };
            ToolTipService.SetToolTip(done, "Save");
            done.Click += (_, _) =>
            {
                var description = box.Text.Trim();
                if (description != chat.GroupDescription) ViewModel.SetGroupDescription(chat, description);
                Show();
            };
            box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Show(); } };
            Grid.SetColumn(done, 1);
            host.Children.Add(box);
            host.Children.Add(done);
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        }

        Show();
        return host;
    }

    // ───── Picture ─────

    private void InfoAvatar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedChat is not { IsGroup: true } chat) return;
        var menu = new MenuFlyout();
        var take = new MenuFlyoutItem { Text = "Take photo", Icon = new FontIcon { Glyph = "\uE722" } };
        take.Click += async (_, _) =>
        {
            try
            {
                var camera = new Microsoft.Windows.Media.Capture.CameraCaptureUI(AppWindow.Id);
                camera.PhotoSettings.Format = Microsoft.Windows.Media.Capture.CameraCaptureUIPhotoFormat.Jpeg;
                if (await camera.CaptureFileAsync(Microsoft.Windows.Media.Capture.CameraCaptureUIMode.Photo) is { } photo) await SetGroupPictureAsync(chat, photo);
            }
            catch (Exception) { ShowToast(false, "The camera isn't available."); }
        };
        var upload = new MenuFlyoutItem { Text = "Upload photo", Icon = new FontIcon { Glyph = "\uE8B7" } };
        upload.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }) picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file) await SetGroupPictureAsync(chat, file);
        };
        menu.Items.Add(take);
        menu.Items.Add(upload);
        menu.ShowAt((FrameworkElement)sender);
    }

    /// <summary>The middle square of the photo, 640 px, as a JPEG (what WhatsApp takes for a picture).</summary>
    private async Task SetGroupPictureAsync(Chat chat, StorageFile file)
    {
        try
        {
            using var source = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var (w, h) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            var side = Math.Min(w, h);
            var scale = 640.0 / side;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Round(w * scale),
                ScaledHeight = (uint)Math.Round(h * scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            transform.Bounds = new BitmapBounds
            {
                X = (transform.ScaledWidth - Math.Min(640, transform.ScaledWidth)) / 2,
                Y = (transform.ScaledHeight - Math.Min(640, transform.ScaledHeight)) / 2,
                Width = Math.Min(640, transform.ScaledWidth),
                Height = Math.Min(640, transform.ScaledHeight),
            };
            var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WAFluent", "pictures")).FullName;
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jpg");
            await using (var output = File.Create(path))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(pixels);
                await encoder.FlushAsync();
            }
            ViewModel.SetGroupPicture(chat, path);
        }
        catch (Exception ex)
        {
            AppLog.Write("preparing a group picture failed", ex);
            ShowToast(false, "That picture couldn't be used.");
        }
    }

    // ───── Add members (the New group picker, adding to this group) ─────

    private void OpenAddMembers()
    {
        if (ViewModel.SelectedChat is not { IsGroup: true } chat) return;
        NewChat_Click(this, new RoutedEventArgs());
        _addingTo = chat;
        _groupMembers.Clear();
        RebuildGroupChips();
        ShowNewChatPage(NewChatPage.Members);
    }

    private void GroupIntroAdd_Click(object sender, RoutedEventArgs e) => OpenAddMembers();

    /// <summary>"Add description…" on the intro card: Group info, with the description ready to type.</summary>
    private void GroupIntroDescription_Click(object sender, RoutedEventArgs e) => OpenInfo();
}
