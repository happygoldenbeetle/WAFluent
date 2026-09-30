using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WhatsAppNative;

/// <summary>
/// Files dragged onto the conversation, and files or screenshots pasted into the composer,
/// open the same send preview as the + menu, like WhatsApp: photos and videos as media,
/// anything else as documents. With the preview already open they're added to it.
/// Copied text still pastes as text.
/// </summary>
public sealed partial class MainWindow
{
    private void Conversation_DragOver(object sender, DragEventArgs e)
    {
        if (SendTarget is not { } chat || !HasFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Send";
        DropDetail.Text = MediaComposer.Visibility == Visibility.Visible ? "They'll be added to the preview" : $"Send to {chat.Name}";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void Conversation_DragLeave(object sender, DragEventArgs e)
    {
        // Moving between the pane's children raises this too; only leaving the pane counts.
        var at = e.GetPosition(ConversationPane);
        if (at.X > 0 && at.Y > 0 && at.X < ConversationPane.ActualWidth && at.Y < ConversationPane.ActualHeight) return;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Conversation_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (SendTarget is null) return;
        var deferral = e.GetDeferral();
        IReadOnlyList<StorageFile> files;
        try
        {
            files = await FilesFromAsync(e.DataView);
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Write("reading dropped files failed", ex);
            files = [];
        }
        finally
        {
            deferral.Complete();
        }
        await SendFilesAsync(files);
    }

    private void ComposerBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView clipboard;
        try { clipboard = Clipboard.GetContent(); }
        catch (Exception) { return; }   // the clipboard is busy: paste as usual
        // Text wins (a copied Word paragraph also carries a picture of itself).
        if (SendTarget is null || !HasFiles(clipboard) || clipboard.Contains(StandardDataFormats.Text)) return;
        e.Handled = true;
        _ = PasteFilesAsync(clipboard);
    }

    private async Task PasteFilesAsync(DataPackageView clipboard)
    {
        try
        {
            await SendFilesAsync(await FilesFromAsync(clipboard));
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Write("pasting files failed", ex);
            ShowToast(false, "That couldn't be pasted.");
        }
    }

    private static bool HasFiles(DataPackageView data) =>
        data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.Bitmap);

    /// <summary>The files in a drop or on the clipboard; a bare picture (a screenshot) is saved as a PNG first.</summary>
    private static async Task<IReadOnlyList<StorageFile>> FilesFromAsync(DataPackageView data)
    {
        if (data.Contains(StandardDataFormats.StorageItems))
            return (await data.GetStorageItemsAsync()).OfType<StorageFile>().ToList();
        if (!data.Contains(StandardDataFormats.Bitmap)) return [];

        var reference = await data.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WAFluent", "pasted"));
        var path = Path.Combine(folder.FullName, $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        await using (var file = File.Create(path))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }
        return [await StorageFile.GetFileFromPathAsync(path)];
    }

    /// <summary>Into the send preview: media when they're all photos and videos, otherwise documents.</summary>
    private async Task SendFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0) return;
        var documents = MediaComposer.Visibility == Visibility.Visible
            ? _composingDocuments
            : !files.All(f => PhotoTypes.Concat(VideoTypes).Contains(Path.GetExtension(f.Name).ToLowerInvariant()));
        await OpenComposerAsync(files, documents);
    }
}
