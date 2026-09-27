using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Models;

namespace WhatsAppNative.Helpers;

/// <summary>Small functions used from x:Bind in XAML.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NotEmpty(string? value) => Visible(!string.IsNullOrEmpty(value));
    public static Visibility HasDelivery(Delivery d) => Visible(d != Delivery.None);
    public static Visibility IsNull(object? value) => Visible(value is null);
    public static Visibility IsNotNull(object? value) => Visible(value is not null);
    public static Visibility Positive(int value) => Visible(value > 0);

    public static HorizontalAlignment Align(bool isOutgoing) =>
        isOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public static ImageSource? Image(string? path) =>
        path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 640 };

    public static string FileIcon(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".zip" or ".rar" or ".7z" => "🗂️",
        ".pdf" => "📕",
        ".doc" or ".docx" or ".txt" => "📄",
        ".xls" or ".xlsx" or ".csv" => "📊",
        ".mp3" or ".wav" or ".m4a" => "🎵",
        _ => "📁",
    };
}
