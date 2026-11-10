using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppShotScreen.Editor;

/// <summary>Ventana simple que muestra el texto reconocido para revisarlo o copiarlo.</summary>
public sealed class OcrResultWindow : Window
{
    public OcrResultWindow(string text)
    {
        Title = "APPSHOTACREEN - Texto extraído";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x23));

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Margin = new Thickness(12),
            Padding = new Thickness(6),
        };
        var copy = new Button { Content = "Copiar", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += (_, _) => Clipboard.SetText(box.Text);

        var dock = new DockPanel();
        DockPanel.SetDock(copy, Dock.Bottom);
        dock.Children.Add(copy);
        dock.Children.Add(box);
        Content = dock;
    }
}
