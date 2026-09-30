using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WhatsAppNative.Services;

/// <summary>
/// Windows notifications for new messages: the chat's picture and name, the message
/// ("Sender: …" in groups), and a reply box with Send. Clicking one opens the chat; replying
/// sends without opening WAFluent. Each chat's notifications share a group, cleared when the
/// chat is opened.
///
/// Windows' classic toasts (Windows App SDK notifications need a component self-contained apps
/// don't have). WAFluent registers its own name and icon for them under the current user
/// (HKCU\Software\Classes\AppUserModelId\WAFluent). Clicks and replies reach the app while it
/// runs, in the tray included.
/// </summary>
public static class Notifications
{
    private const string AppId = "WAFluent";

    /// <summary>A notification was clicked: open this chat.</summary>
    public static event Action<string>? Opened;

    /// <summary>A reply was typed in a notification: send this text to this chat.</summary>
    public static event Action<string, string>? Replied;

    private static ToastNotifier? _notifier;

    /// <summary>Registers the app's name and icon for notifications; call once at startup.</summary>
    public static void Register()
    {
        if (_notifier is not null) return;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            {
                key.SetValue("DisplayName", "WAFluent");
                key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
                key.SetValue("IconBackgroundColor", "FF00A884");
            }
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception e)
        {
            Helpers.AppLog.Write("notifications couldn't be set up", e);
        }
    }

    public static void HandleLaunch() { }

    public static void Unregister() { }

    /// <summary>A new message: <paramref name="title"/> the chat, <paramref name="text"/> the preview.</summary>
    public static void Show(string chatId, string messageId, string title, string text, string? picture)
    {
        if (_notifier is null) return;
        try
        {
            string E(string s) => SecurityElement.Escape(s) ?? "";
            var logo = picture is { } p && File.Exists(p)
                ? $"<image placement='appLogoOverride' hint-crop='circle' src='{E(new Uri(p).AbsoluteUri)}'/>"
                : "";
            var args = $"action=open&amp;chat={E(Uri.EscapeDataString(chatId))}";
            var reply = $"action=reply&amp;chat={E(Uri.EscapeDataString(chatId))}";
            var xml = $@"<toast launch='{args}'>
  <visual><binding template='ToastGeneric'>
    <text hint-maxLines='1'>{E(title)}</text>
    <text>{E(text)}</text>
    {logo}
  </binding></visual>
  <actions>
    <input id='reply' type='text' placeHolderContent='Type a reply'/>
    <action content='Send' arguments='{reply}' hint-inputId='reply' activationType='foreground'/>
  </actions>
  <audio src='ms-winsoundevent:Notification.IM'/>
</toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc) { Group = Key(chatId), Tag = Key(messageId) };
            toast.Activated += (_, e) =>
            {
                if (e is not ToastActivatedEventArgs activated) return;
                string? input = activated.UserInput?.TryGetValue("reply", out var v) == true ? v as string : null;
                Invoked(activated.Arguments, input);
            };
            _notifier.Show(toast);
        }
        catch (Exception e)
        {
            Helpers.AppLog.Write("showing a notification failed", e);
        }
    }

    /// <summary>The chat was opened: its notifications go from the Action Center.</summary>
    public static void Clear(string chatId)
    {
        if (_notifier is null) return;
        try { ToastNotificationManager.History.RemoveGroup(Key(chatId), AppId); }
        catch (Exception) { }
    }

    /// <summary>How many of WAFluent's notifications are in the Action Center (self-test).</summary>
    public static int Count()
    {
        try { return ToastNotificationManager.History.GetHistory(AppId).Count; }
        catch (Exception) { return -1; }
    }

    public static void ClearAll()
    {
        try { ToastNotificationManager.History.Clear(AppId); }
        catch (Exception) { }
    }

    /// <summary>Group and tag ids are limited to 16 characters: a short hash of the id.</summary>
    private static string Key(string id) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];

    private static void Invoked(string argument, string? reply)
    {
        var args = argument.Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        if (!args.TryGetValue("chat", out var chat)) return;
        if (args.GetValueOrDefault("action") == "reply" && reply?.Trim() is { Length: > 0 } text)
            Replied?.Invoke(chat, text);
        else
            Opened?.Invoke(chat);
    }
}
