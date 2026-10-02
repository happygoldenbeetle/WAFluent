using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

/// <summary>Calls: the window (CallWindow) talks to WhatsApp through these.</summary>
public sealed partial class MainViewModel
{
    /// <summary>A call rang, connected or ended.</summary>
    public event Action<CallDto>? CallChanged;

    /// <summary>The other side's voice: 16-bit samples, or an Opus packet. Not on the UI thread.</summary>
    public event Action<byte[], bool>? CallAudio;

    public Chat? ChatById(string chatId) => _byId.GetValueOrDefault(chatId);

    public void StartCall(Chat chat, bool video) => _core?.StartCall(chat.Id, video);

    public void AcceptCall(string callId) => _core?.AcceptCall(callId);

    public void RejectCall(string callId) => _core?.RejectCall(callId);

    public void EndCall() => _core?.EndCall();

    public void MuteCall(bool muted) => _core?.MuteCall(muted);

    public void SendCallAudio(byte[] pcm) => _core?.SendCallAudio(pcm);
}
