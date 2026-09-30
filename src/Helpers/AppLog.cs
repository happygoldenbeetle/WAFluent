namespace WhatsAppNative.Helpers;

/// <summary>
/// Problems the app handles quietly (a toast for the user), with the details written to
/// %LOCALAPPDATA%\WAFluent\app.log so they can be looked into. Kept under ~1 MB.
/// </summary>
public static class AppLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent", "app.log");
    private static readonly Lock Gate = new();

    public static void Write(string what, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}{(error is null ? "" : "\n" + error)}\n");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}
