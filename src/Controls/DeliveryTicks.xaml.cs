using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhatsAppNative.Models;

namespace WhatsAppNative.Controls;

/// <summary>Clock (sending), single grey tick (sent), double grey (delivered), double blue (read), red mark (failed).</summary>
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
        PendingPath.Visibility = Delivery == Delivery.Pending ? Visibility.Visible : Visibility.Collapsed;
        FailedIcon.Visibility = Delivery == Delivery.Failed ? Visibility.Visible : Visibility.Collapsed;
    }
}
