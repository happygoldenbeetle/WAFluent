using Microsoft.UI.Xaml;

namespace WhatsAppNative;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // Anything unhandled is written to %LOCALAPPDATA%\WAFluent\crash.log before the app goes down.
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    private static void LogCrash(Exception? e)
    {
        try
        {
            var dir = Services.CoreClient.DataDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Helpers.AppColors.Apply(Services.UiSettings.Load().UseSystemAccent);
        Helpers.AppColors.FollowWindows(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow();
        _window.Activate();
    }
}
