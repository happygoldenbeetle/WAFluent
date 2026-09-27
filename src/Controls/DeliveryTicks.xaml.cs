using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhatsAppNative.Models;

namespace WhatsAppNative.Controls;

/// <summary>Single grey tick (sent), double grey (delivered), double blue (read).</summary>
public sealed partial class DeliveryTicks : UserControl
{
    public static readonly DependencyProperty DeliveryProperty = DependencyProperty.Register(
        nameof(Delivery), typeof(Delivery), typeof(DeliveryTicks),
        new PropertyMetadata(Delivery.None, (d, _) => ((DeliveryTicks)d).Update()));

    public Delivery Delivery { get => (Delivery)GetValue(DeliveryProperty); set => SetValue(DeliveryProperty, value); }

    public DeliveryTicks()
    {
        InitializeComponent();
        Update();
    }

    private void Update()
    {
        SinglePath.Visibility = Delivery == Delivery.Sent ? Visibility.Visible : Visibility.Collapsed;
        DoublePath.Visibility = Delivery == Delivery.Delivered ? Visibility.Visible : Visibility.Collapsed;
        ReadPath.Visibility = Delivery == Delivery.Read ? Visibility.Visible : Visibility.Collapsed;
    }
}
