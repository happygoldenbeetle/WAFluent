using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhatsAppNative;

/// <summary>
/// Contact info › Disappearing messages, like WhatsApp: Off, 24 hours, 7 days or 90 days. The
/// choice goes to the chat (and your phone) at once; the chat gets the "You turned on…"
/// notice, the chat list a timer on the picture, and new messages are removed here when their
/// time is up (the core checks once a minute). Changes made on the phone or by the other side
/// show up the same way.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly int[] DisappearingSeconds = [0, 86_400, 604_800, 7_776_000];
    private bool _showingDisappearing;

    private void OpenDisappearing()
    {
        if (ViewModel.SelectedChat is null) return;
        ContactInfoView.Visibility = Visibility.Collapsed;
        NewContactView.Visibility = Visibility.Collapsed;
        SearchView.Visibility = Visibility.Collapsed;
        MessageInfoView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        ChatStarredView.Visibility = Visibility.Collapsed;
        DisappearingView.Visibility = Visibility.Visible;
        InfoTitle.Text = "Disappearing messages";
        InfoCloseIcon.Glyph = "\uE72B";   // back to Contact info
        ShowDisappearingChoice();
    }

    /// <summary>"Change timer." at the end of a disappearing-messages notice.</summary>
    private void ChangeTimer_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        OpenInfo();
        OpenDisappearing();
    }

    /// <summary>Ticks the chat's current timer (a custom one from the phone ticks nothing).</summary>
    private void ShowDisappearingChoice()
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        _showingDisappearing = true;
        DisappearingChoices.SelectedIndex = Array.IndexOf(DisappearingSeconds, chat.Ephemeral);
        _showingDisappearing = false;
    }

    private void DisappearingChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingDisappearing || ViewModel.SelectedChat is not { } chat || DisappearingChoices.SelectedIndex < 0) return;
        ViewModel.SetEphemeral(chat, DisappearingSeconds[DisappearingChoices.SelectedIndex]);
    }
}
