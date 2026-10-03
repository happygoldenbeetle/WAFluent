using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace WhatsAppNative.Helpers;

/// <summary>
/// WinUI shows the busy pointer (the arrow with a blue ring) when a menu or drop-down opens
/// under a still pointer, until it moves (microsoft-ui-xaml#8829). For a moment after every
/// click, right-click and menu key, a busy pointer the app never asked for is put back to the arrow.
/// </summary>
public static class BusyCursorFix
{
    public static void Watch(FrameworkElement root)
    {
        var timer = root.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        var until = 0L;
        timer.Tick += (_, _) =>
        {
            Restore();
            if (Environment.TickCount64 > until) timer.Stop();
        };
        void Start()
        {
            until = Environment.TickCount64 + 800;   // the menu's opening animation, and some
            timer.Start();
        }
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Start()), handledEventsToo: true);
        root.AddHandler(UIElement.ContextRequestedEvent,
            new Windows.Foundation.TypedEventHandler<UIElement, ContextRequestedEventArgs>((_, _) => Start()), handledEventsToo: true);
    }

    private static void Restore()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info)) return;
        if (info.hCursor == LoadCursor(0, IDC_APPSTARTING) || info.hCursor == LoadCursor(0, IDC_WAIT))
            SetCursor(LoadCursor(0, IDC_ARROW));
    }

    private const int IDC_ARROW = 32512, IDC_WAIT = 32514, IDC_APPSTARTING = 32650;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public int x, y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
}
