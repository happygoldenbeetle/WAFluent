namespace WhatsAppNative.Helpers;

/// <summary>Segoe Fluent Icons code points used from code-behind.</summary>
public static class Glyphs
{
    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);

    public static readonly string Phone = G(0xE717);
    public static readonly string Video = G(0xE714);
    public static readonly string Photo = G(0xEB9F);
    public static readonly string Status = G(0xEA3A);
    public static readonly string Star = G(0xE734);
    public static readonly string Archive = G(0xE7B8);
    public static readonly string Settings = G(0xE713);
    public static readonly string Contact = G(0xE77B);
    public static readonly string Mic = G(0xE720);
    public static readonly string MicOff = G(0xF781);
}
