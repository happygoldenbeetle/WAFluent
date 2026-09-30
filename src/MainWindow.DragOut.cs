using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Photos, videos, stickers and documents drag out of the conversation: drop one on a folder or
/// the desktop and it's copied there, under the name Save as would give it ("WhatsApp Image
/// 2026-10-01 at 15.32.08.jpg", or a document's own name). Not yet downloaded: the drag
/// doesn't start, and the download does.
/// </summary>
public sealed partial class MainWindow
{
    private const string OwnDrag = "WAFluent.DragOut";
    private static readonly string DragFolder = Path.Combine(Path.GetTempPath(), "WAFluent", "drag");

    private async void Media_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message m }) return;
        if (m.MediaPath is not { } path || !File.Exists(path))
        {
            e.Cancel = true;
            if (m.MediaFailed) ViewModel.RetryDownload(m);
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var copy = NamedCopy(m, path);
            e.Data.SetStorageItems([await StorageFile.GetFileFromPathAsync(copy)]);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.AllowedOperations = DataPackageOperation.Copy;
            e.Data.Properties[OwnDrag] = true;   // so the conversation doesn't offer to send it again
        }
        catch (Exception ex)
        {
            AppLog.Write("dragging media out failed", ex);
            e.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// The file under its proper name, in a folder of its own in %TEMP% (the stored one is named
    /// after the message id). A hard link where possible, so even a large video starts at once.
    /// </summary>
    private static string NamedCopy(Message m, string path)
    {
        CleanDragFolder();
        var folder = Directory.CreateDirectory(Path.Combine(DragFolder, Guid.NewGuid().ToString("N")[..8])).FullName;
        var ext = Path.GetExtension(path);
        var name = string.Concat(MediaActions.SuggestedName(m).Split(Path.GetInvalidFileNameChars()));
        var target = Path.Combine(folder, (name.Length > 0 ? name : "WhatsApp file") + ext);
        if (!CreateHardLink(target, path, IntPtr.Zero)) File.Copy(path, target);
        return target;
    }

    /// <summary>Drags from before today are done with.</summary>
    private static void CleanDragFolder()
    {
        try
        {
            if (!Directory.Exists(DragFolder)) return;
            foreach (var old in new DirectoryInfo(DragFolder).GetDirectories().Where(d => d.CreationTime < DateTime.Now.AddDays(-1)))
                old.Delete(recursive: true);
        }
        catch (Exception)
        {
            // in use or already gone: next time
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
