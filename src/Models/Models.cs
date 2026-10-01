using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhatsAppNative.Models;

/// <summary>Sent → Delivered → Read in that order; Pending/Failed are local states before "Sent".</summary>
public enum Delivery { None, Sent, Delivered, Read, Pending, Failed }

public enum MessageKind { Text, Image, File, DateDivider, Voice, Sticker, Video, Location, Contact, Poll, System, Album }

/// <summary>One poll option: who picked it, and whether you did.</summary>
public sealed class PollOption
{
    public required Message Owner { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Voters { get; init; } = [];
    public bool Selected { get; init; }
    /// <summary>Share of everyone who voted (the bar's length).</summary>
    public double Fraction { get; init; }
    public int Count => Voters.Count;
    public string CountLabel => Count > 0 ? Count.ToString() : "";
    public bool Multi => Owner.PollMulti;
}

/// <summary>A shared contact card: the name and the numbers from its vCard.</summary>
public sealed record ContactCard(string Name, IReadOnlyList<string> Phones)
{
    public string Phone => Phones.Count > 0 ? Phones[0] : "";
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Message : Observable
{
    private string? _mediaPath;
    private bool _mediaFailed;
    private Delivery _delivery;

    /// <summary>WhatsApp message id; a temporary one while an outgoing message is being sent.</summary>
    public string Id { get; set; } = "";
    public MessageKind Kind { get; init; }
    public bool IsOutgoing { get; init; }
    public string Text { get; init; } = "";
    private string _time = "";
    /// <summary>"18:27" (or "6:27 pm"): changes with Settings › Use 24-hour time.</summary>
    public string Time { get => _time; set { if (Set(ref _time, value)) Raise(nameof(TimeSpacer)); } }
    public DateTime Timestamp { get; init; }
    public long UnixTs { get; set; }
    public Delivery Delivery { get => _delivery; set => Set(ref _delivery, value); }

    private bool _hasTail = true;

    /// <summary>Gets the tail: the first bubble of a run (classic) or the last (iMessage style).</summary>
    public bool HasTail { get => _hasTail; set => Set(ref _hasTail, value); }

    // ───── Corners that follow the bubble (iMessage style) ─────

    /// <summary>A picture (4 px from the bubble's edge): its top touches unless a reply sits above, its bottom unless a caption follows.</summary>
    public Microsoft.UI.Xaml.CornerRadius PictureCorners => Helpers.Ui.InsetCorners(!HasReply && !IsForwarded, !HasText, 4);
    public Microsoft.UI.Xaml.CornerRadius VideoCorners => IsVideoNote ? new(MediaWidth / 2) : Helpers.Ui.InsetCorners(!HasReply && !HasHeader, !HasText, 4);
    public Microsoft.UI.Xaml.CornerRadius MapCorners => Helpers.Ui.InsetCorners(!HasReply && !HasHeader, !HasPlaceText, 4);
    /// <summary>A document card sits 6 px in; nothing follows it in the iMessage style.</summary>
    public Microsoft.UI.Xaml.CornerRadius FileCardCorners => Helpers.Ui.InsetCorners(!HasReply && !IsForwarded, true, 6);
    /// <summary>A document's first-page preview: the card's top corners, square at the bottom.</summary>
    public Microsoft.UI.Xaml.CornerRadius FileThumbCorners => new(FileCardCorners.TopLeft, FileCardCorners.TopRight, 0, 0);
    /// <summary>A link preview sits 4 px in, above the text.</summary>
    public Microsoft.UI.Xaml.CornerRadius LinkCardCorners => Helpers.Ui.InsetCorners(!HasReply && !HasHeader, false, 4);

    private int _jumbo = -1;

    /// <summary>Only emoji (up to ten): shown big, without a bubble.</summary>
    public bool IsJumbo => Kind == MessageKind.Text && !HasReply && !IsDeleted && !HasLink && JumboCount > 0;
    private int JumboCount => _jumbo >= 0 ? _jumbo : _jumbo = Helpers.EmojiText.JumboCount(Text);
    public double JumboSize => Helpers.EmojiText.Size(JumboCount);



    /// <summary>Just sent or received: its bubble springs in when it first appears.</summary>
    public bool AnimateIn { get; set; }
    // ───── Reactions ─────

    private IReadOnlyList<string> _reactions = [];
    private string _myReaction = "";

    /// <summary>One emoji per person who reacted, oldest first (yours included).</summary>
    public IReadOnlyList<string> Reactions
    {
        get => _reactions;
        set { _reactions = value; Raise(nameof(Reactions)); Raise(nameof(Reaction)); }
    }

    /// <summary>Your reaction, or "".</summary>
    public string MyReaction { get => _myReaction; set => Set(ref _myReaction, value); }

    /// <summary>The pill under the bubble: up to three different emoji, plus the count when more than one person reacted.</summary>
    public string Reaction
    {
        get
        {
            if (_reactions.Count == 0) return "";
            var distinct = string.Concat(_reactions.Distinct().Take(3));
            return _reactions.Count > 1 ? $"{distinct} {_reactions.Count}" : distinct;
        }
    }

    /// <summary>Shown above incoming messages in groups.</summary>
    public string SenderName { get; init; } = "";

    // ───── Attachments (image, sticker, voice) ─────

    /// <summary>The message has a downloadable attachment.</summary>
    public bool HasMedia { get; init; }

    /// <summary>Downloaded file; null while downloading (or not yet requested).</summary>
    public string? MediaPath
    {
        get => _mediaPath;
        set { if (Set(ref _mediaPath, value)) { Raise(nameof(IsMediaLoading)); Raise(nameof(HasMediaFile)); } }
    }

    public bool MediaFailed
    {
        get => _mediaFailed;
        set { if (Set(ref _mediaFailed, value)) Raise(nameof(IsMediaLoading)); }
    }

    public bool HasMediaFile => _mediaPath is not null;
    public bool IsMediaLoading => HasMedia && _mediaPath is null && !_mediaFailed && (AutoDownloads || _downloadRequested);

    private bool _downloadRequested;
    /// <summary>Videos and documents download when clicked, not by themselves.</summary>
    public bool DownloadRequested
    {
        get => _downloadRequested;
        set { if (Set(ref _downloadRequested, value)) Raise(nameof(IsMediaLoading)); }
    }
    private bool _isUploading;

    /// <summary>Yours, still uploading: a ring with ✕ over it, "Uploading..." under documents.</summary>
    public bool IsUploading { get => _isUploading; set => Set(ref _isUploading, value); }

    public bool AutoDownloads => Kind is MessageKind.Image or MessageKind.Sticker or MessageKind.Voice || IsGif;

    // ───── Previews and per-kind details ─────

    /// <summary>The sender's small JPEG preview, base64: shown while the file downloads, or for good (maps, links).</summary>
    public string? Thumb { get; set; }
    public bool HasThumb => !string.IsNullOrEmpty(Thumb);

    /// <summary>Voice note (with the sender's picture) rather than an audio file (headphones).</summary>
    public bool IsVoiceNote { get; set; } = true;

    /// <summary>Videos: a looping GIF, or a round video message.</summary>
    public bool IsGif { get; set; }
    public bool IsVideoNote { get; set; }
    /// <summary>Length on the video pill; none for GIFs (they just loop, like WhatsApp).</summary>
    public string DurationLabel => Seconds > 0 && !IsGif ? Helpers.Format.Duration(TimeSpan.FromSeconds(Seconds)) : "";

    /// <summary>Locations (the map snapshot is <see cref="Thumb"/>).</summary>
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string PlaceName { get; set; } = "";
    public string PlaceAddress { get; set; } = "";
    public bool IsLiveLocation { get; set; }
    public bool HasPlaceAddress => PlaceAddress.Length > 0;
    /// <summary>A named place: name and address go under the map (a plain pin is just the map).</summary>
    public bool HasPlaceText => PlaceName.Length > 0 || PlaceAddress.Length > 0;
    public string PlaceTitle => PlaceName.Length > 0 ? PlaceName : IsLiveLocation ? "Live location" : Text.Length > 0 ? Text : "Location";

    /// <summary>Shared contacts.</summary>
    public IReadOnlyList<ContactCard> Contacts { get; set; } = [];
    public string ContactTitle => Contacts.Count switch
    {
        0 => Text,
        1 => Contacts[0].Name,
        2 => $"{Contacts[0].Name} and 1 other contact",
        var n => $"{Contacts[0].Name} and {n - 1} other contacts",
    };
    public string ContactSubtitle => Contacts.Count == 1 ? Contacts[0].Phone : "";
    public bool HasContactPhone => Contacts.Any(c => c.Phones.Count > 0);

    /// <summary>Polls: options with their votes (yours and everyone's, synced with the phone).</summary>
    private IReadOnlyList<PollOption> _pollOptions = [];
    public IReadOnlyList<PollOption> PollOptions
    {
        get => _pollOptions;
        set { _pollOptions = value; Raise(nameof(PollOptions)); Raise(nameof(HasVotes)); }
    }
    public bool PollMulti { get; set; }
    public string PollHint => PollMulti ? "Select one or more" : "Select one";
    public int PollVoters => PollOptions.SelectMany(o => o.Voters).Distinct().Count();
    public bool HasVotes => PollVoters > 0;

    /// <summary>Link preview card on a text message.</summary>
    public string LinkUrl { get; set; } = "";
    public string LinkTitle { get; set; } = "";
    public string LinkDescription { get; set; } = "";
    public bool HasLink => LinkUrl.Length > 0;
    public bool HasLinkDescription => LinkDescription.Length > 0;
    public string LinkHost => Uri.TryCreate(LinkUrl.Contains("://") ? LinkUrl : "https://" + LinkUrl, UriKind.Absolute, out var u) ? u.Host : LinkUrl;

    /// <summary>Documents: page count (PDFs), when the sender's app said.</summary>
    public int Pages { get; set; }
    public string FileInfo => Pages switch
    {
        <= 0 => FileDetails,
        1 => $"{FileDetails} · 1 page",
        var n => $"{FileDetails} · {n} pages",
    };

    /// <summary>On-screen size for images/stickers, keeping the original proportions.</summary>
    public double MediaWidth { get; init; } = 300;
    public double MediaHeight { get; init; } = 200;

    /// <summary>Voice notes: length and 64 amplitude samples (0-100).</summary>
    public int Seconds { get; init; }
    public int[] Waveform { get; init; } = [];

    // ───── Replies: the quoted message, shown on top of the bubble ─────

    public string ReplyId { get; init; } = "";
    public string ReplyName { get; init; } = "";
    public string ReplyPreview { get; init; } = "";
    public string ReplyGlyph { get; init; } = "";
    /// <summary>The quoted photo's/video's preview (base64), shown at the quote's right.</summary>
    public string? ReplyThumb { get; init; }
    public bool ReplyFromMe { get; init; }
    public bool HasReply => ReplyId.Length > 0;

    // File messages
    public string FileName { get; init; } = "";
    public string FileDetails { get; init; } = "";

    public bool HasText => Text.Length > 0;

    /// <summary>Deleted for everyone: shows "This message was deleted" in italics.</summary>
    public bool IsDeleted { get; init; }

    private IReadOnlyList<Message>? _albumItems;

    /// <summary>An album (photos and videos sent together): its items, oldest first. Null otherwise.</summary>
    public IReadOnlyList<Message>? AlbumItems { get => _albumItems; set => Set(ref _albumItems, value); }

    /// <summary>The "N unread messages" band (a DateDivider, so everything that skips dividers skips it).</summary>
    public bool IsUnreadDivider { get; init; }
    public bool Starred { get; init; }
    public bool Edited { get; init; }

    /// <summary>Times forwarded (0: not forwarded). WhatsApp says "Forwarded many times" from 5 on.</summary>
    public int Forwarded { get; init; }
    public bool IsForwarded => Forwarded > 0;
    public string ForwardedText => Forwarded >= 5 ? "Forwarded many times" : "Forwarded";

    private bool _isSelected;
    private bool _selecting;
    /// <summary>Picked in select mode (the row is tinted and its circle ticked).</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    /// <summary>Select mode is on: every message shows its circle.</summary>
    public bool Selecting { get => _selecting; set => Set(ref _selecting, value); }
    public bool ShowSender => !IsOutgoing && SenderName.Length > 0;
    /// <summary>Something above the content: the sender's name or "Forwarded".</summary>
    public bool HasHeader => ShowSender || IsForwarded;

    /// <summary>
    /// Invisible run appended to the text so the last line leaves room for the time/ticks overlay.
    /// Must end in a non-whitespace character: trailing spaces take no width at a line end.
    /// </summary>
    public string TimeSpacer => "  " + (Starred ? "__ " : "") + (Edited ? "  Edited " : "") + Time + (IsOutgoing ? " ___" : "");
}

public sealed class Chat : Observable
{
    private string _name = "";
    private string _preview = "";
    private string _previewGlyph = "";
    private string _time = "";
    private int _unread;
    private bool _isPinned;
    private Delivery _lastDelivery;
    private string? _avatarPath;

    /// <summary>Cached profile picture on disk; initials are shown while null.</summary>
    public string? AvatarPath { get => _avatarPath; set => Set(ref _avatarPath, value); }

    /// <summary>WhatsApp JID for live chats; empty for sample data.</summary>
    public string Id { get; init; } = "";
    public bool IsGroup { get; init; }
    private string _status = "", _typing = "";

    /// <summary>1:1: "online" or "last seen …" (when they share it). Groups: who's in it.</summary>
    public string Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(HeaderStatus)); Raise(nameof(HasStatusText)); } }
    }

    public bool HasStatusText => _status.Length > 0;

    /// <summary>"typing…", "recording audio…", "Sara is typing…"; empty when nobody is.</summary>
    public string TypingText
    {
        get => _typing;
        set
        {
            if (!Set(ref _typing, value)) return;
            Raise(nameof(IsTyping));
            Raise(nameof(HeaderStatus));
            Raise(nameof(PreviewHidden));
            Raise(nameof(ShowDraft));
        }
    }

    public bool IsTyping
    {
        get => _typing.Length > 0;
        init => _typing = value ? "typing…" : "";
    }

    private string _draft = "";

    /// <summary>Text left unsent in the composer when you opened another chat (cleared when you come back).</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (!Set(ref _draft, value)) return;
            Raise(nameof(HasDraft));
            Raise(nameof(DraftLine));
            Raise(nameof(PreviewHidden));
            Raise(nameof(ShowDraft));
        }
    }

    public bool HasDraft => _draft.Trim().Length > 0;
    public string DraftLine => _draft.Trim().ReplaceLineEndings(" ");
    /// <summary>The chat list's last-message line gives way to "typing…" or "Draft: …".</summary>
    public bool PreviewHidden => IsTyping || HasDraft;
    public bool ShowDraft => HasDraft && !IsTyping;

    /// <summary>Under the name in the chat header: typing beats online / last seen.</summary>
    public string HeaderStatus => IsTyping ? _typing : _status;
    public bool HasMention { get; init; }
    public bool HasStatus { get; init; }   // unseen status update: green ring around the avatar
    public DateTime LastActivity { get; set; }

    private bool _isMuted, _isArchived, _isBlocked, _isSaved, _isFavourite;
    private string _pinnedMessageId = "", _pinnedMessagePreview = "";
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public bool IsArchived { get => _isArchived; set => Set(ref _isArchived, value); }
    public bool IsBlocked { get => _isBlocked; set => Set(ref _isBlocked, value); }
    /// <summary>In your contacts (1:1 chats); unsaved numbers offer "Add to contacts".</summary>
    public bool IsSaved { get => _isSaved; set => Set(ref _isSaved, value); }
    /// <summary>Favourites are kept on this PC (ui.json).</summary>
    public bool IsFavourite { get => _isFavourite; set => Set(ref _isFavourite, value); }

    private string _pushName = "";
    /// <summary>The name they gave themselves; Contact info shows it as "~name" under the number.</summary>
    public string PushName
    {
        get => _pushName;
        set { if (Set(ref _pushName, value)) Raise(nameof(PushNameLine)); }
    }
    public string PushNameLine => _pushName.Length > 0 && _pushName != Name ? "~" + _pushName : "";

    /// <summary>1:1 chats: the number split for the New contact form ("PK", "+92", "302 9328645").</summary>
    public string PhoneRegion { get; set; } = "";
    public string PhoneCode { get; set; } = "";
    public string PhoneNational { get; set; } = "";

    /// <summary>The message pinned in this chat (banner under the header).</summary>
    public string PinnedMessageId
    {
        get => _pinnedMessageId;
        set { if (Set(ref _pinnedMessageId, value)) Raise(nameof(HasPinnedMessage)); }
    }
    public string PinnedMessagePreview { get => _pinnedMessagePreview; set => Set(ref _pinnedMessagePreview, value); }
    public bool HasPinnedMessage => _pinnedMessageId.Length > 0;

    /// <summary>When the chat was pinned (Unix seconds): the newest pin sits on top.</summary>
    public long PinnedAt { get; set; }

    /// <summary>True once this chat's history has been fetched from the core.</summary>
    public bool MessagesLoaded { get; set; }

    /// <summary>Unread count when the chat was opened: where the "unread messages" band goes.</summary>
    public int UnreadMark { get; set; }

    /// <summary>Nothing older exists on this device or the phone.</summary>
    public bool HistoryComplete { get; set; }

    /// <summary>After an unanswered request for older messages, wait before asking the phone again.</summary>
    public DateTime RetryOlderAfter { get; set; }

    private bool _loadingOlder;
    /// <summary>Waiting for older messages (spinner at the top of the conversation).</summary>
    public bool LoadingOlder { get => _loadingOlder; set => Set(ref _loadingOlder, value); }

    public ObservableCollection<Message> Messages { get; } = new();

    public required string Name { get => _name; set => Set(ref _name, value); }
    public string Preview { get => _preview; set => Set(ref _preview, value); }

    private string _previewSender = "";
    /// <summary>Groups: "Maya:" before the preview (kept apart so developer mode can blur it).</summary>
    public string PreviewSender { get => _previewSender; set => Set(ref _previewSender, value); }
    public string PreviewGlyph { get => _previewGlyph; set => Set(ref _previewGlyph, value); }   // e.g. a camera icon before the preview
    public string Time { get => _time; set => Set(ref _time, value); }
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    public int Unread
    {
        get => _unread;
        set { if (Set(ref _unread, value)) { Raise(nameof(HasUnread)); Raise(nameof(ShowUnreadCount)); Raise(nameof(ShowUnreadDot)); } }
    }

    private bool _markedUnread;

    /// <summary>"Mark as unread" with nothing actually unread: a green dot, no number, like the phone.</summary>
    public bool MarkedUnread
    {
        get => _markedUnread;
        set { if (Set(ref _markedUnread, value)) { Raise(nameof(HasUnread)); Raise(nameof(ShowUnreadCount)); Raise(nameof(ShowUnreadDot)); } }
    }

    public bool ShowUnreadCount => Unread > 0;
    public bool ShowUnreadDot => MarkedUnread && Unread == 0;

    private int _ephemeral;

    /// <summary>Disappearing messages: seconds until new messages go (0 off).</summary>
    public int Ephemeral
    {
        get => _ephemeral;
        set { if (Set(ref _ephemeral, value)) { Raise(nameof(HasEphemeral)); Raise(nameof(EphemeralText)); } }
    }

    public bool HasEphemeral => _ephemeral > 0;
    public string EphemeralText => Helpers.Format.EphemeralText(_ephemeral);

    /// <summary>Delivery state of the last message, when it was outgoing.</summary>
    public Delivery LastDelivery { get => _lastDelivery; set => Set(ref _lastDelivery, value); }

    public bool HasUnread => Unread > 0 || MarkedUnread;
}
