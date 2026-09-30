using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Controls;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Pictures let go of when they scroll away. The conversation keeps the rows it has taken
/// off screen to reuse them, and each would otherwise hold its decoded photo, sticker or
/// preview until it's reused; now those are dropped as the row is put aside and decoded
/// again (from disk, quickly) when it comes back.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Rows put aside with their pictures dropped, and the message each showed.</summary>
    private readonly ConditionalWeakTable<UIElement, Message> _released = new();

    private void SetupMemory()
    {
        Messages.ElementClearing += (_, e) =>
        {
            if (e.Element is not FrameworkElement { DataContext: Message m } row) return;
            Release(row);
            _released.AddOrUpdate(row, m);
        };
        Messages.ElementPrepared += (_, e) =>
        {
            if (!_released.TryGetValue(e.Element, out var before)) return;
            _released.Remove(e.Element);
            if (e.Element is not FrameworkElement row) return;
            // Back for another message: its bindings already drew it. Back for the same one:
            // the bindings don't run again by themselves, so they're asked to.
            if (ReferenceEquals(row.DataContext, before))
            {
                row.DataContext = null;
                row.DataContext = before;
            }
            Restore(row);
        };
    }

    private static void Release(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            switch (VisualTreeHelper.GetChild(parent, i))
            {
                case AlbumGrid album:
                    album.Release();
                    continue;
                case MapView or GifPlayer or QuoteBlock:
                    continue;   // they look after their own
                case Image image:
                    image.Source = null;
                    continue;
                case Border { Background: ImageBrush brush } border:
                    brush.ImageSource = null;
                    Release(border);
                    continue;
                case Panel { Background: ImageBrush brush } panel:
                    brush.ImageSource = null;
                    Release(panel);
                    continue;
                case var child:
                    Release(child);
                    continue;
            }
        }
    }

    private static void Restore(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is AlbumGrid album) album.Restore();
            else Restore(child);
        }
    }
}
