using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WhatsAppNative.Helpers;

namespace WhatsAppNative;

/// <summary>
/// The window opens the way it was left: same size and place, maximized if it was. The size
/// kept is the restored one (so un-maximizing goes back to it), saved a moment after it
/// stops changing. A place that's off every screen now (a monitor unplugged) falls back to
/// the default size, centred.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _placementSave;

    private void RestoreWindowPlacement()
    {
        var saved = new RectInt32(_ui.WindowX, _ui.WindowY, _ui.WindowWidth, _ui.WindowHeight);
        var visible = saved.Width > 0 && saved.Height > 0
                      && DisplayArea.GetFromRect(saved, DisplayAreaFallback.None) is not null;
        if (visible) AppWindow.MoveAndResize(saved);
        else WindowHelper.SizeAndCenter(this, 1100, 720);
        if (_ui.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        _placementSave = DispatcherQueue.CreateTimer();
        _placementSave.Interval = TimeSpan.FromMilliseconds(600);
        _placementSave.IsRepeating = false;
        _placementSave.Tick += (_, _) => SaveWindowPlacement();
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange || e.DidPositionChange || e.DidPresenterChange) _placementSave.Start();
        };
        Closed += (_, _) => SaveWindowPlacement();
    }

    private void SaveWindowPlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        switch (presenter.State)
        {
            case OverlappedPresenterState.Minimized:
                return;   // keep what it was before
            case OverlappedPresenterState.Maximized:
                _ui.WindowMaximized = true;
                break;
            default:
                _ui.WindowMaximized = false;
                var (position, size) = (AppWindow.Position, AppWindow.Size);
                (_ui.WindowX, _ui.WindowY, _ui.WindowWidth, _ui.WindowHeight) = (position.X, position.Y, size.Width, size.Height);
                break;
        }
        _ui.Save();
    }
}
