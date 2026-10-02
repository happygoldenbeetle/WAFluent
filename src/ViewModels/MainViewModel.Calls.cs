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

    /// <summary>The other side's picture: an H.264 access unit, whether a decoder can start at it, their camera's quarter turns. Not on the UI thread.</summary>
    public event Action<byte[], bool, int>? CallVideo;

    /// <summary>Video in the call: request | on | off | declined | ended | failed | keyframe.</summary>
    public event Action<string>? CallVideoState;

    public Chat? ChatById(string chatId) => _byId.GetValueOrDefault(chatId);

    public void StartCall(Chat chat, bool video) => _core?.StartCall(chat.Id, video);

    public void AcceptCall(string callId, bool video) => _core?.AcceptCall(callId, video);

    public void RejectCall(string callId) => _core?.RejectCall(callId);

    public void EndCall() => _core?.EndCall();

    public void MuteCall(bool muted) => _core?.MuteCall(muted);

    public void SendCallAudio(byte[] pcm) => _core?.SendCallAudio(pcm);

    public void SetCallVideo(bool on) => _core?.SetCallVideo(on);

    public void SendCallVideo(byte[] unit) => _core?.SendCallVideo(unit);
}
