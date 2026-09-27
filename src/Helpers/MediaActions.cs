using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WhatsAppNative.Models;

namespace WhatsAppNative.Helpers;

/// <summary>Clipboard / file actions shared by the message menu and the photo viewer.</summary>
public static class MediaActions
{
    public static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    /// <summary>As a bitmap (paste into chats/editors) and as a file (paste into Explorer).</summary>
    public static async Task CopyImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        data.SetStorageItems([file]);
        Clipboard.SetContent(data);
    }

    /// <summary>Save-as dialog with a WhatsApp-style name; returns true if a copy was saved.</summary>
    public static async Task<bool> SaveAsAsync(Window owner, string path, Message message)
    {
        var ext = Path.GetExtension(path);
        var kind = message.Kind switch
        {
            MessageKind.Voice => "Audio",
            MessageKind.Sticker => "Sticker",
            _ => "Image",
        };
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = message.Kind == MessageKind.Voice ? PickerLocationId.MusicLibrary : PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"WhatsApp {kind} {message.Timestamp:yyyy-MM-dd 'at' HH.mm.ss}",
        };
        picker.FileTypeChoices.Add(kind, [ext]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var target = await picker.PickSaveFileAsync();
        if (target is null) return false;
        File.Copy(path, target.Path, overwrite: true);
        return true;
    }

    /// <summary>Default app for the file (Photos for pictures).</summary>
    public static void OpenExternally(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Explorer with the file selected.</summary>
    public static void ShowInFolder(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"");

    public static bool Exists(Message message) => message.MediaPath is { } p && File.Exists(p);
}
