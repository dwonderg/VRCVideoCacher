using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VRCVideoCacher.Views;

public partial class PopupWindow : Window
{
    public bool Confirmed { get; private set; }

    public PopupWindow() : this(string.Empty)
    {
    }

    public PopupWindow(string error)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("ErrorTextBlock")!.Text = error;
    }

    public static PopupWindow CreateConfirm(string message, string confirmLabel = "Yes", string cancelLabel = "No")
    {
        var w = new PopupWindow(message);
        var ok = w.FindControl<Button>("OkButton")!;
        var cancel = w.FindControl<Button>("CancelButton")!;
        ok.Content = confirmLabel;
        cancel.Content = cancelLabel;
        cancel.IsVisible = true;
        return w;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        this.Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        this.Close();
    }
}
