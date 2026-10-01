using Microsoft.UI.Xaml;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Each chat keeps its own draft, like WhatsApp: text left in the composer stays with its chat
/// when you open another (the chat list shows "Draft: …" in its place) and comes back, with
/// its @mentions, when you return. A half-done edit of a sent message isn't a draft; it's
/// dropped. Drafts last until WAFluent closes.
/// </summary>
public sealed partial class MainWindow
{
    private Chat? _draftChat;
    private readonly Dictionary<Chat, List<Mention>> _draftMentions = [];
    /// <summary>The text just put back, so its TextChanged isn't sent as "typing…".</summary>
    private string? _restoredDraft;

    private void SetupDrafts()
    {
        _draftChat = ViewModel.SelectedChat;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) SwapDraft(ViewModel.SelectedChat);
        };
    }

    private void SwapDraft(Chat? next)
    {
        if (next == _draftChat) return;
        if (_draftChat is { } previous)
        {
            if (_editing is not null)
            {
                _editing = null;
                EditBanner.Visibility = Visibility.Collapsed;
                previous.Draft = "";
            }
            else
            {
                var text = ComposerBox.Text;
                previous.Draft = text.Trim().Length > 0 ? text : "";
                if (previous.HasDraft) _draftMentions[previous] = [.. _mentions];
                else _draftMentions.Remove(previous);
            }
        }
        _draftChat = next;

        var draft = next?.Draft ?? "";
        _restoredDraft = ComposerBox.Text == draft ? null : draft;
        ComposerBox.Text = draft;
        ComposerBox.SelectionStart = draft.Length;
        _mentions.Clear();
        if (next is not null && _draftMentions.Remove(next, out var mentions)) _mentions.AddRange(mentions);
        if (next is not null) next.Draft = "";   // it's in the composer now
    }
}
