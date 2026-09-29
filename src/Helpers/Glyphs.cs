namespace WhatsAppNative.Helpers;

/// <summary>Segoe Fluent Icons code points used from code-behind.</summary>
public static class Glyphs
{
    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);

    public static readonly string Phone = G(0xE717);
    public static readonly string Video = G(0xE714);
    public static readonly string Mail = G(0xE715);
    public static readonly string Photo = G(0xEB9F);
    public static readonly string Status = G(0xEA3A);
    public static readonly string Star = G(0xE734);
    public static readonly string Archive = G(0xE7B8);
    public static readonly string Settings = G(0xE713);
    public static readonly string Contact = G(0xE77B);
    public static readonly string Mic = G(0xE720);
    public static readonly string MicOff = G(0xF781);
    public static readonly string Document = G(0xE8A5);
    public static readonly string Location = G(0xE81D);
    public static readonly string Poll = G(0xE9D5);
    public static readonly string Play = G(0xE768);
    public static readonly string Pause = G(0xE769);
    public static readonly string Warning = G(0xE7BA);
    public static readonly string Copy = G(0xE8C8);
    public static readonly string Save = G(0xE74E);
    public static readonly string View = G(0xE890);
    public static readonly string OpenExternal = G(0xE8A7);
    public static readonly string Folder = G(0xE838);
    public static readonly string Info = G(0xE946);
    public static readonly string Refresh = G(0xE72C);
    public static readonly string Speed = G(0xE916);
    public static readonly string Reply = G(0xE97A);
    public static readonly string Pin = G(0xE718);
    public static readonly string Unpin = G(0xE77A);
    public static readonly string AddContact = G(0xE8FA);
    public static readonly string Mute = G(0xE74F);
    public static readonly string Volume = G(0xE767);
    public static readonly string MarkRead = G(0xE8C3);
    public static readonly string MarkUnread = G(0xE715);
    public static readonly string Heart = G(0xEB51);
    public static readonly string HeartFill = G(0xEB52);
    public static readonly string CloseCircle = G(0xEA39);
    public static readonly string Block = G(0xE733);
    public static readonly string Clear = G(0xE894);
    public static readonly string Delete = G(0xE74D);
    public static readonly string Forward = G(0xE72D);
    public static readonly string StarFill = G(0xE735);
    public static readonly string Select = G(0xE762);
    public static readonly string Report = G(0xE8E0);
    public static readonly string CheckMark = G(0xE73E);
    public static readonly string Phone2 = G(0xE717);
    public static readonly string VideoCall = G(0xE714);
    public static readonly string Media = G(0xE8B9);
    public static readonly string Lock = G(0xE72E);
    public static readonly string Export = G(0xE896);
    public static readonly string Search = G(0xE721);
    public static readonly string Ringer = G(0xEA8F);        // outline bell
    public static readonly string RingerSilent = G(0xE7ED);  // outline bell with a slash
}
