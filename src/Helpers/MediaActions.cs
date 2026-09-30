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

    /// <summary>"Image", "Video"… as WhatsApp names saved files.</summary>
    private static string KindName(Message message) => message.Kind switch
    {
        MessageKind.Voice => "Audio",
        MessageKind.Sticker => "Sticker",
        MessageKind.Video => "Video",
        MessageKind.File => "Document",
        _ => "Image",
    };

    /// <summary>
    /// The name a saved or dragged-out copy gets, without extension: a document keeps the
    /// name it was sent with; the rest are "WhatsApp Image 2026-10-01 at 15.32.08", like WhatsApp.
    /// </summary>
    public static string SuggestedName(Message message) =>
        message.Kind == MessageKind.File && message.FileName.Length > 0
            ? Path.GetFileNameWithoutExtension(message.FileName)
            : $"WhatsApp {KindName(message)} {message.Timestamp:yyyy-MM-dd 'at' HH.mm.ss}";

    /// <summary>Save-as dialog with a WhatsApp-style name; returns true if a copy was saved.</summary>
    public static async Task<bool> SaveAsAsync(Window owner, string path, Message message)
    {
        var ext = Path.GetExtension(path);
        var kind = KindName(message);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = message.Kind switch
            {
                MessageKind.Voice => PickerLocationId.MusicLibrary,
                MessageKind.Video => PickerLocationId.VideosLibrary,
                MessageKind.File => PickerLocationId.DocumentsLibrary,
                _ => PickerLocationId.PicturesLibrary,
            },
            SuggestedFileName = SuggestedName(message),
        };
        picker.FileTypeChoices.Add(kind, [ext.Length > 0 ? ext : ".bin"]);
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
