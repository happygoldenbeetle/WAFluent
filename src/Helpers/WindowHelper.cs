using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace WhatsAppNative.Helpers;

/// <summary>Sizing, placement and caption-button colours shared by the app's windows.</summary>
public static class WindowHelper
{
    public static double Scale(Window window) =>
        GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;

    public static RectInt32 WorkArea(Window window) =>
        DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

    /// <summary>Resizes (in DIPs) and centres the window on its monitor's work area.</summary>
    public static void SizeAndCenter(Window window, int width, int height)
    {
        var scale = Scale(window);
        var work = WorkArea(window);
        var w = Math.Min((int)(width * scale), work.Width);
        var h = Math.Min((int)(height * scale), work.Height);
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    public static void SetMinimumSize(Window window, int width, int height)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = Scale(window);
        presenter.PreferredMinimumWidth = (int)(width * scale);
        presenter.PreferredMinimumHeight = (int)(height * scale);
    }

    /// <summary>Caption buttons don't follow an app-forced theme on their own.</summary>
    public static void ApplyCaptionColors(Window window, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var bar = window.AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
