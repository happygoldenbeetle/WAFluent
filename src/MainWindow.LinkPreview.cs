using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The link card above the composer, like WhatsApp and Discord: typing a link fetches the
/// page's title, description and picture (Services/LinkPreviews.cs) and shows the card that
/// will be sent with the message. ✕ drops it for that link. Sent messages carry the card, so
/// the other side sees it too.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _linkDebounce;
    private string? _linkUrl;                    // the link the card is for
    private LinkPreviews.Card? _linkReady;       // its card, once fetched
    private readonly HashSet<string> _linkDismissed = [];

    private void UpdateLinkCard()
    {
        if (_linkDebounce is null)
        {
            _linkDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _linkDebounce.Tick += (_, _) =>
            {
                _linkDebounce.Stop();
                _ = RefreshLinkCardAsync();
            };
        }
        // Editing a message doesn't change its card.
        var url = _editing is null ? LinkPreviews.FirstLink(ComposerBox.Text) : null;
        if (url is null || _linkDismissed.Contains(url))
        {
            _linkDebounce.Stop();
            ResetLinkCard();
            return;
        }
        if (url == _linkUrl) return;
        _linkDebounce.Stop();
        _linkDebounce.Start();
    }

    private async Task RefreshLinkCardAsync()
    {
        var url = _editing is null ? LinkPreviews.FirstLink(ComposerBox.Text) : null;
        if (url is null || _linkDismissed.Contains(url) || url == _linkUrl) return;
        _linkUrl = url;
        _linkReady = null;
        LinkCardTitle.Text = "";
        LinkCardDescription.Text = "";
        LinkCardSite.Text = "";
        LinkCardImage.ImageSource = null;
        LinkCardPicture.Visibility = Visibility.Collapsed;
        SetLinkCardLoading(true);
        LinkCard.Visibility = Visibility.Visible;

        var card = await LinkPreviews.GetAsync(url);
        if (_linkUrl != url) return;   // the link changed meanwhile
        SetLinkCardLoading(false);
        if (card is null)
        {
            LinkCard.Visibility = Visibility.Collapsed;   // nothing to show: the message goes as plain text
            return;
        }
        _linkReady = card;
        LinkCardTitle.Text = card.Title.Length > 0 ? card.Title : card.Site;
        LinkCardDescription.Text = card.Description;
        LinkCardDescription.Visibility = card.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LinkCardSite.Text = card.Site;
        if (card.Thumb is { } thumb && File.Exists(thumb))
        {
            LinkCardImage.ImageSource = new BitmapImage(new Uri(thumb));
            LinkCardPicture.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The card to send, when it's loaded and still for a link in the text.</summary>
    private LinkPreviews.Card? ReadyLinkCard() =>
        _linkReady is { } card && LinkCard.Visibility == Visibility.Visible && LinkPreviews.FirstLink(ComposerBox.Text) == card.Url ? card : null;

    private void ResetLinkCard()
    {
        _linkUrl = null;
        _linkReady = null;
        SetLinkCardLoading(false);
        LinkCard.Visibility = Visibility.Collapsed;
    }

    private void SetLinkCardLoading(bool loading)
    {
        LinkCardSkeleton.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LinkCardShimmer.IsActive = loading;
    }

    private void LinkCardClose_Click(object sender, RoutedEventArgs e)
    {
        if (_linkUrl is { } url) _linkDismissed.Add(url);
        ResetLinkCard();
        ComposerBox.Focus(FocusState.Programmatic);
    }
}
