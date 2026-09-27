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

    protected override DataTemplate? SelectTemplateCore(object item) => item is Message m
        ? m.Kind switch
        {
            MessageKind.Image => Image,
            MessageKind.File => File,
            MessageKind.DateDivider => DateDivider,
            _ => Text,
        }
        : Text;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
