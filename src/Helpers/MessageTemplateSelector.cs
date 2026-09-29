using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhatsAppNative.Models;

namespace WhatsAppNative.Helpers;

public sealed partial class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Image { get; set; }
    public DataTemplate? File { get; set; }
    public DataTemplate? DateDivider { get; set; }
    public DataTemplate? Voice { get; set; }
    public DataTemplate? Sticker { get; set; }
    public DataTemplate? Video { get; set; }
    public DataTemplate? Location { get; set; }
    public DataTemplate? Contact { get; set; }
    public DataTemplate? Poll { get; set; }
    public DataTemplate? System { get; set; }
    public DataTemplate? Jumbo { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is Message m
        ? m.Kind switch
        {
            MessageKind.Image => Image,
            MessageKind.Voice => Voice,
            MessageKind.Sticker => Sticker,
            MessageKind.File => File,
            MessageKind.DateDivider => DateDivider,
            MessageKind.Text when m.IsJumbo => Jumbo,
            MessageKind.Video => Video,
            MessageKind.Location => Location,
            MessageKind.Contact => Contact,
            MessageKind.Poll => Poll,
            MessageKind.System => System,
            _ => Text,
        }
        : Text;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
