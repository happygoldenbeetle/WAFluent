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

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    public MainViewModel()
    {
        ApplyFilter();
        SelectedChat = _allChats.First(c => c.Name == "Emma Warren");
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

        var time = DateTime.Now.ToString("h:mm tt");
        _selectedChat.Messages.Add(new Message { Text = text, Time = time, IsOutgoing = true, Delivery = Delivery.Sent });
        _selectedChat.Preview = text;
        _selectedChat.Time = time;
        _selectedChat.LastDelivery = Delivery.Sent;

        // Most recent conversation goes to the top, as in WhatsApp.
        _allChats.Remove(_selectedChat);
        _allChats.Insert(0, _selectedChat);
        var index = Chats.IndexOf(_selectedChat);
        if (index > 0) Chats.Move(index, 0);
        return true;
    }
}
