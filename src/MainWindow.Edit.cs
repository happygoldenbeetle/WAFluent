using Microsoft.UI.Xaml;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Editing your messages, in the composer: "Edit" in the message menu (or ↑ in an empty
/// composer, for your latest message) puts its text in the box under an "Editing message"
/// banner; Enter saves, Esc or ✕ cancels. WhatsApp allows edits for 15 minutes after
/// sending, so the option only shows (and ↑ only works) within that time. Text messages only.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(15);
    private Message? _editing;

    private static bool CanEdit(Message m) =>
        m.IsOutgoing && m.Kind == MessageKind.Text && !m.IsDeleted && !m.Id.StartsWith("pending-")
        && m.Delivery != Delivery.Failed
        && DateTimeOffset.Now.ToUnixTimeSeconds() - m.UnixTs < EditWindow.TotalSeconds;

    private Message? LatestEditable() =>
        ViewModel.SelectedChat?.Messages.LastOrDefault(m => m.IsOutgoing && m.Kind == MessageKind.Text && !m.IsDeleted) is { } last && CanEdit(last)
            ? last
            : null;

    private void BeginEdit(Message m)
    {
        if (!CanEdit(m)) return;
        ViewModel.CancelReply();
        _editing = m;
        EditBannerText.Text = m.Text;
        EditBanner.Visibility = Visibility.Visible;
        ComposerBox.Text = m.Text;
        ComposerBox.Focus(FocusState.Programmatic);
        ComposerBox.SelectionStart = ComposerBox.Text.Length;
    }

    private void SaveEdit()
    {
        if (_editing is not { } m) return;
        var text = ComposerBox.Text.Trim();
        if (text.Length > 0 && text != m.Text)
        {
            if (CanEdit(m)) ViewModel.Edit(m, text);
            else ShowToast(false, "Messages can only be edited for 15 minutes after sending.");
        }
        CancelEdit();
    }

    private void CancelEdit()
    {
        _editing = null;
        EditBanner.Visibility = Visibility.Collapsed;
        ComposerBox.Text = "";
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => CancelEdit();
}
