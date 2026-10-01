using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Settings → Theme (System / Light / Dark) and Settings → Minimize to tray: minimizing or
/// closing hides the window to a tray icon (H.NotifyIcon.WinUI) whose right-click menu is a
/// WinUI menu; Quit there ends the app. Click the icon to bring the window back.
/// </summary>
public sealed partial class MainWindow
{
    // ───────────── Theme ─────────────

    private static readonly string[] Themes = ["System", "Light", "Dark"];

    /// <summary>`--theme light|dark` on the command line wins over the setting.</summary>
    private bool _themeFromArgs;

    private void SetupTheme()
    {
        ThemeBox.ItemsSource = new[] { "System (default)", "Light", "Dark" };
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, _ui.Theme));
        ThemeBox.SelectionChanged += (_, _) =>
        {
            var theme = Themes[Math.Max(0, ThemeBox.SelectedIndex)];
            if (theme == _ui.Theme) return;
            _ui.Theme = theme;
            _ui.Save();
            _themeFromArgs = false;
            ApplyTheme();
        };
        Helpers.Themed.Theme = Root.ActualTheme;
        Root.ActualThemeChanged += (_, _) =>
        {
            Helpers.Themed.Theme = Root.ActualTheme;
            if (InfoPanel.Visibility == Visibility.Visible && ContactInfoView.Visibility == Visibility.Visible) RebuildInfo();
        };
        if (!_themeFromArgs) ApplyTheme();
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _ui.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        foreach (var item in _trayThemeItems) item.IsChecked = (string)item.Tag == _ui.Theme;
    }

    // ───────────── Tray ─────────────

    private TaskbarIcon? _tray;
    private readonly List<RadioMenuFlyoutItem> _trayThemeItems = new();
    private ToggleMenuFlyoutItem? _trayDeveloperMode;
    private bool _hiddenToTray;
    private bool _quitting;

    private void SetupTray()
    {
        MinimizeToTraySwitch.IsOn = _ui.MinimizeToTray;
        MinimizeToTraySwitch.Toggled += (_, _) =>
        {
            if (_ui.MinimizeToTray == MinimizeToTraySwitch.IsOn) return;
            _ui.MinimizeToTray = MinimizeToTraySwitch.IsOn;
            _ui.Save();
            UpdateTrayIcon();
        };

        // X with the setting on: into the tray too. Only the tray's Quit ends the app.
        AppWindow.Closing += (_, e) =>
        {
            if (!_ui.MinimizeToTray || _quitting) return;
            e.Cancel = true;
            HideToTray();
        };

        // Minimized with the setting on: off the taskbar, into the tray.
        AppWindow.Changed += (_, _) =>
        {
            if (_ui.MinimizeToTray && !_hiddenToTray && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                HideToTray();
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.UnreadChats) && _tray is not null) _tray.ToolTipText = TrayToolTip();
        };
        Closed += (_, _) => _tray?.Dispose();
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        if (!_ui.MinimizeToTray)
        {
            if (_hiddenToTray) ShowFromTray();
            _tray?.Dispose();
            _tray = null;
            return;
        }
        if (_tray is not null) return;

        _tray = new TaskbarIcon
        {
            ToolTipText = TrayToolTip(),
            IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"))),
            ContextMenuMode = ContextMenuMode.SecondWindow,   // a real WinUI menu, not the old Win32 one
            NoLeftClickDelay = true,
            LeftClickCommand = new Command(() => { if (_hiddenToTray) ShowFromTray(); else HideToTray(); }),
            ContextFlyout = TrayMenu(),
        };
        _tray.ForceCreate(false);   // no Efficiency Mode: the app keeps running normally
    }

    private MenuFlyout TrayMenu()
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open WAFluent", Icon = new FontIcon { Glyph = "\uE8A7" } };
        open.Click += (_, _) => ShowFromTray();
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());

        _trayDeveloperMode = new ToggleMenuFlyoutItem { Text = "Developer mode (blur names)", IsChecked = _ui.DeveloperMode };
        _trayDeveloperMode.Click += (_, _) => DeveloperModeSwitch.IsOn = _trayDeveloperMode.IsChecked;   // the switch saves and applies
        DeveloperModeSwitch.Toggled += (_, _) => _trayDeveloperMode.IsChecked = DeveloperModeSwitch.IsOn;
        menu.Items.Add(_trayDeveloperMode);

        var theme = new MenuFlyoutSubItem { Text = "Theme", Icon = new FontIcon { Glyph = "\uE790" } };
        _trayThemeItems.Clear();
        foreach (var (key, text) in new[] { ("System", "System"), ("Light", "Light"), ("Dark", "Dark") })
        {
            var option = new RadioMenuFlyoutItem { Text = text, GroupName = "tray-theme", Tag = key, IsChecked = _ui.Theme == key };
            option.Click += (_, _) => ThemeBox.SelectedIndex = Array.IndexOf(Themes, key);   // the dropdown saves and applies
            _trayThemeItems.Add(option);
            theme.Items.Add(option);
        }
        menu.Items.Add(theme);

        var settings = new MenuFlyoutItem { Text = "Settings", Icon = new FontIcon { Glyph = "\uE713" } };
        settings.Click += (_, _) =>
        {
            ShowFromTray();
            Nav.SelectedItem = Nav.FooterMenuItems[2];
        };
        menu.Items.Add(settings);

        menu.Items.Add(new MenuFlyoutSeparator());
        var quit = new MenuFlyoutItem { Text = "Quit WAFluent", Icon = new FontIcon { Glyph = "\uE7E8" } };
        quit.Click += (_, _) =>
        {
            _quitting = true;
            _tray?.Dispose();
            _tray = null;
            Close();
        };
        menu.Items.Add(quit);
        return menu;
    }

    private string TrayToolTip() => ViewModel.UnreadChats switch
    {
        0 => "WAFluent",
        1 => "WAFluent · 1 unread chat",
        var n => $"WAFluent · {n} unread chats",
    };

    private void HideToTray()
    {
        _hiddenToTray = true;
        this.Hide(false);   // no Efficiency Mode while hidden: messages keep arriving at full speed
    }

    private void ShowFromTray()
    {
        _hiddenToTray = false;
        this.Show(false);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    /// <summary>The tray icon takes commands for clicks.</summary>
    private sealed class Command(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    // ───────────── Notifications (Services/Notifications.cs) ─────────────

    private bool _windowActive = true;

    private void SetupNotifications()
    {
        Notifications.Register();
        Notifications.Opened += chatId => DispatcherQueue.TryEnqueue(() =>
        {
            ShowFromTray();
            ViewModel.OpenChat(chatId);
        });
        Notifications.Replied += (chatId, text) => DispatcherQueue.TryEnqueue(() => ViewModel.QuickReply(chatId, text));
        ViewModel.IncomingMessage += Notify;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat) && ViewModel.SelectedChat is { } chat) Notifications.Clear(chat.Id);
        };
        Closed += (_, _) => Notifications.Unregister();
        DispatcherQueue.TryEnqueue(Notifications.HandleLaunch);
    }

    /// <summary>
    /// Like WhatsApp Desktop: a notification unless you're looking at that chat right now, and
    /// never for muted chats (or with notifications off in Settings).
    /// </summary>
    private void Notify(Chat chat, Message m)
    {
        if (!_ui.Notifications || chat.IsMuted) return;
        if (_windowActive && !_hiddenToTray && ViewModel.SelectedChat == chat) return;
        var body = Helpers.Format.QuotePreview(m);
        if (chat.IsGroup && m.SenderName.Length > 0) body = $"{m.SenderName}: {body}";
        // Names and pictures stay hidden in developer mode (screen sharing).
        var title = Controls.Redact.Enabled ? "New message" : chat.Name;
        if (Controls.Redact.Enabled) body = "You have a new message";
        Notifications.Show(chat.Id, m.Id, title, body, Controls.Redact.Enabled ? null : chat.AvatarPath);
    }

    private void Notifications_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ui.Notifications == NotificationsSwitch.IsOn) return;
        _ui.Notifications = NotificationsSwitch.IsOn;
        _ui.Save();
    }
}
