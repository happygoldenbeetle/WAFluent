using System.Collections.ObjectModel;
using WhatsAppNative.Models;

namespace WhatsAppNative.ViewModels;

public sealed class MainViewModel : Observable
{
    private readonly List<Chat> _allChats = SampleData.Create();
    private Chat? _selectedChat;
    private string _searchText = "";

    public ObservableCollection<Chat> Chats { get; } = new();

    public Chat? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (!Set(ref _selectedChat, value)) return;
            if (value is not null) value.Unread = 0;
            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedChat is not null;

    /// <summary>Number of chats with unread messages (the badge on the Chats rail item).</summary>
    public int UnreadChats => _allChats.Count(c => c.HasUnread);

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    public MainViewModel()
    {
        foreach (var chat in _allChats)
            chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Chat.Unread)) Raise(nameof(UnreadChats)); };
        ApplyFilter();
        SelectedChat = _allChats.First(c => c.Name == "Alice Whitman");
    }

    private void ApplyFilter()
    {
        var selected = _selectedChat;
        Chats.Clear();
        foreach (var chat in _allChats.Where(c => c.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase)))
            Chats.Add(chat);
        // Clearing the list drops the ListView selection; keep the open chat open.
        if (selected is not null && Chats.Contains(selected)) SelectedChat = selected;
    }

    /// <summary>Adds an outgoing text message to the open chat. Returns false if there is nothing to send.</summary>
    public bool Send(string text)
    {
        text = text.Trim();
        if (_selectedChat is null || text.Length == 0) return false;

        var time = DateTime.Now.ToString("H:mm");
        _selectedChat.Messages.Add(new Message { Text = text, Time = time, IsOutgoing = true, Delivery = Delivery.Sent });
        _selectedChat.Preview = text;
        _selectedChat.Time = time;
        _selectedChat.LastDelivery = Delivery.Sent;

        // Most recent conversation goes to the top (below pinned chats), as in WhatsApp.
        var target = _allChats.Count(c => c.IsPinned && c != _selectedChat);
        if (_selectedChat.IsPinned) target = 0;
        _allChats.Remove(_selectedChat);
        _allChats.Insert(target, _selectedChat);
        var index = Chats.IndexOf(_selectedChat);
        var visibleTarget = Math.Min(Chats.Count(c => c.IsPinned && c != _selectedChat), Chats.Count - 1);
        if (_selectedChat.IsPinned) visibleTarget = 0;
        if (index >= 0 && index != visibleTarget) Chats.Move(index, visibleTarget);
        return true;
    }
}
