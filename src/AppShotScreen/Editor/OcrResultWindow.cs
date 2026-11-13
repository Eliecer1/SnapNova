using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppShotScreen.Services;

namespace AppShotScreen.Editor;

/// <summary>Ventana que muestra el texto reconocido para revisarlo o copiarlo.</summary>
public sealed class OcrResultWindow : Window
{
    public OcrResultWindow(string text, bool copied = true)
    {
        Title = "SnapNova - Texto extraído";
        Icon = App.AppIconImage;
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x23));
        // Sin ventana dueña (captura directa de texto) la ponemos delante de todo para que se vea.
        Loaded += (_, _) => { if (Owner == null) { Topmost = true; Activate(); } };
        WindowFinder.UseDarkTitleBar(this);

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
        var status = new TextBlock
        {
            Text = copied ? "Ya está copiado al portapapeles." : "Otra aplicación tenía el portapapeles ocupado. Pulsa Copiar para intentarlo de nuevo.",
            Foreground = new SolidColorBrush(copied ? Color.FromRgb(0x9B, 0xA1, 0xB5) : Color.FromRgb(0xFF, 0xC1, 0x4D)),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 12),
        };
        var copy = new Button { Content = "Copiar", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 12, 12) };
        copy.Click += async (_, _) =>
        {
            // Si hay texto seleccionado, se copia solo eso.
            string toCopy = box.SelectionLength > 0 ? box.SelectedText : box.Text;
            copy.IsEnabled = false;
            bool ok = await ClipboardHelper.TrySetTextAsync(toCopy);
            copy.IsEnabled = true;
            status.Text = ok ? "Copiado al portapapeles." : "El portapapeles sigue ocupado. Cierra la app que lo usa o selecciona el texto y pulsa Ctrl+C.";
            status.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x2E, 0xB8, 0x5C) : Color.FromRgb(0xFF, 0x6B, 0x6B));
        };

        var bottom = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        bottom.Children.Add(copy);
        bottom.Children.Add(status);

        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(box);
        Content = dock;
    }
}
