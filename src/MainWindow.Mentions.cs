using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// @mentions while typing, in groups and 1:1 chats: "@" (after a space or at the start)
/// opens a list over the composer, filtered as you type — the group's members, or the
/// person you're chatting with. ↑/↓ choose, Enter/Tab or a click inserts "@Name", Esc
/// closes. On send the names become WhatsApp mentions (the core gets their JIDs).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Regex MentionTail = new(@"(?:^|\s)@([^\s@]{0,30})$");

    /// <summary>Names inserted in the composer and who they are, for the next send.</summary>
    private readonly List<Mention> _mentions = [];
    private readonly Dictionary<string, List<MemberDto>> _members = [];
    private List<MemberDto> _mentionMatches = [];
    private int _mentionIndex;
    private int _mentionStart;   // where "@que" starts in the composer text
    private bool _mentionsWired;

    private void UpdateMentions()
    {
        if (ViewModel.SelectedChat is not { } chat)
        {
            HideMentions();
            return;
        }
        var caret = ComposerBox.SelectionStart;
        var before = ComposerBox.Text[..Math.Min(caret, ComposerBox.Text.Length)];
        var m = MentionTail.Match(before);
        if (!m.Success)
        {
            HideMentions();
            return;
        }
        WireMentions();
        var people = PeopleFor(chat);
        var query = m.Groups[1].Value;
        var everyone = chat.IsGroup && people.Count > 1 && "all".StartsWith(query, StringComparison.OrdinalIgnoreCase);
        _mentionMatches = people
            .Where(p => query.Length == 0
                        || p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || p.Phone.Contains(query))
            .OrderBy(p => p.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .ToList();
        if (everyone) _mentionMatches.Insert(0, new MemberDto("all", "all", "", ""));
        if (_mentionMatches.Count == 0)
        {
            HideMentions();
            return;
        }
        _mentionStart = m.Groups[1].Index - 1;
        _mentionIndex = 0;
        RenderMentions();
        MentionPanel.Visibility = Visibility.Visible;
    }

    /// <summary>A group's members (asked once, then kept), or the person in a 1:1 chat.</summary>
    private List<MemberDto> PeopleFor(Chat chat)
    {
        if (!chat.IsGroup)
            return [new MemberDto(chat.Id, chat.Name, chat.Id, new string((chat.PhoneCode + chat.PhoneNational).Where(char.IsAsciiDigit).ToArray()))];
        if (_core is null)   // sample mode: the names in the header ("Rebecca, Chris, Maya, You")
            return chat.Status.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(n => n != "You").Select(n => new MemberDto(n, n, "", "")).ToList();
        if (_members.TryGetValue(chat.Id, out var known)) return known;
        _members[chat.Id] = [];   // asked; filled when the core answers
        _core?.GroupMembers(chat.Id);
        return [];
    }

    private void WireMentions()
    {
        if (_mentionsWired || _core is null) return;
        _mentionsWired = true;
        _core.GroupMembersReceived += (chatId, members) =>
        {
            _members[chatId] = members.ToList();
            if (ViewModel.SelectedChat?.Id == chatId) UpdateMentions();   // the list was waiting for them
        };
    }

    private void RenderMentions()
    {
        MentionList.Children.Clear();
        for (var i = 0; i < _mentionMatches.Count; i++)
        {
            var person = _mentionMatches[i];
            var index = i;
            var row = new Grid
            {
                Padding = new Thickness(10, 6, 14, 6),
                ColumnSpacing = 12,
                CornerRadius = new CornerRadius(4),
                Background = i == _mentionIndex
                    ? Themed.Brush("SubtleFillColorSecondaryBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (person.Jid == "all")
            {
                // Like WhatsApp: "all — Mention all members in this chat".
                row.Children.Add(new Grid
                {
                    Width = 36,
                    Height = 36,
                    Children =
                    {
                        new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = Themed.Brush("SubtleFillColorSecondaryBrush") },
                        new FontIcon { Glyph = "", FontSize = 16 },
                    },
                });
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = "all", FontSize = 15 });
                label.Children.Add(new TextBlock { Text = "Mention all members in this chat", FontSize = 12.5, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
            }
            else
            {
                var avatar = ViewModel.Chats.FirstOrDefault(c => c.Id == person.ChatId)?.AvatarPath;
                var picture = new Controls.Redact { VeilRadius = new CornerRadius(18) };
                picture.Children.Add(new Controls.Avatar { DisplayName = person.Name, Source = avatar, Size = 36 });
                row.Children.Add(picture);
                var name = new Controls.Redact { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                name.Children.Add(new TextBlock { Text = person.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
            }
            row.PointerEntered += (_, _) => { _mentionIndex = index; RenderMentions(); };
            row.Tapped += (_, e) => { e.Handled = true; _mentionIndex = index; ApplyMention(); };
            MentionList.Children.Add(row);
        }
        // Keep the chosen row in view while arrowing through a long list.
        if (MentionList.Children.Count > _mentionIndex && MentionList.Children[_mentionIndex] is FrameworkElement chosen)
            DispatcherQueue.TryEnqueue(() => chosen.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
    }

    /// <summary>Keys while the list is open: ↑/↓ choose, Enter/Tab insert, Esc closes. Returns true if handled.</summary>
    private bool MentionKey(VirtualKey key)
    {
        if (MentionPanel.Visibility != Visibility.Visible || _mentionMatches.Count == 0) return false;
        switch (key)
        {
            case VirtualKey.Up:
                _mentionIndex = (_mentionIndex - 1 + _mentionMatches.Count) % _mentionMatches.Count;
                RenderMentions();
                return true;
            case VirtualKey.Down:
                _mentionIndex = (_mentionIndex + 1) % _mentionMatches.Count;
                RenderMentions();
                return true;
            case VirtualKey.Enter or VirtualKey.Tab:
                ApplyMention();
                return true;
            case VirtualKey.Escape:
                HideMentions();
                return true;
        }
        return false;
    }

    private void ApplyMention()
    {
        if (_mentionIndex >= _mentionMatches.Count) return;
        var person = _mentionMatches[_mentionIndex];
        var caret = ComposerBox.SelectionStart;
        var text = ComposerBox.Text;
        var insert = "@" + person.Name + " ";
        HideMentions();
        ComposerBox.Text = text[.._mentionStart] + insert + text[caret..];
        ComposerBox.SelectionStart = _mentionStart + insert.Length;
        _mentions.RemoveAll(m => m.Name == person.Name);
        _mentions.Add(new Mention(person.Name, person.Jid, person.ChatId, person.Phone));
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void HideMentions()
    {
        if (MentionPanel.Visibility == Visibility.Collapsed) return;
        MentionPanel.Visibility = Visibility.Collapsed;
        MentionList.Children.Clear();
    }
}

public sealed partial class MainWindow
{
    /// <summary>Everyone to mention for "@all" in the open group (its members' JIDs).</summary>
    private List<string> EveryoneInOpenChat() =>
        ViewModel.SelectedChat is { IsGroup: true } chat && _members.TryGetValue(chat.Id, out var members)
            ? members.Select(m => m.Jid).ToList()
            : [];

    /// <summary>A mention in a message was clicked: their chat, or a new one with their number.</summary>
    private void OpenMention(string chatId, string phone)
    {
        if (ViewModel.Chats.FirstOrDefault(c => c.Id == chatId) is { } chat) ViewModel.OpenChat(chat.Id);
        else if (phone.Length > 0) ViewModel.OpenNumber(phone);
        else ShowToast(false, "Their number isn't known on this PC.");
    }
}
