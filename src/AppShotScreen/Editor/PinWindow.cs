using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using AppShotScreen.Capture;
using AppShotScreen.Services;

namespace AppShotScreen.Editor;

/// <summary>
/// Captura "fijada": flota siempre encima de las demás ventanas. Se arrastra para moverla,
/// la rueda cambia el tamaño, doble clic o Esc la cierra y el clic derecho abre el menú.
/// </summary>
public sealed class PinWindow : Window
{
    private const int Glow = 14; // espacio para la sombra alrededor

    private readonly BitmapSource _image;
    private readonly Settings _settings;
    private readonly Image _view;
    private readonly Border _frame;
    private double _scale = 1;

    public static int OpenCount { get; private set; }

    /// <param name="at">Dónde colocarla (píxeles de pantalla); si es null, cerca del cursor.</param>
    public static PinWindow Show(BitmapSource image, Settings settings, PixelRect? at = null)
    {
        var w = new PinWindow(image, settings, at);
        w.Show();
        return w;
    }

    private PinWindow(BitmapSource image, Settings settings, PixelRect? at)
    {
        _image = image;
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "APPSHOTACREEN - Fijada";

        _view = new Image { Source = image, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
        _frame = new Border
        {
            Margin = new Thickness(Glow),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x5B, 0x8C, 0xFF)),
            Child = _view,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black },
        };
        Content = _frame;

        SourceInitialized += (_, _) =>
        {
            ApplyScale();
            var hwnd = new WindowInteropHelper(this).Handle;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int x, y;
            if (at is { } r)
            {
                // Exactamente sobre la zona capturada.
                x = r.X - (int)Math.Round((Glow + 1) * dpi);
                y = r.Y - (int)Math.Round((Glow + 1) * dpi);
            }
            else
            {
                var c = System.Windows.Forms.Cursor.Position;
                var area = System.Windows.Forms.Screen.FromPoint(c).WorkingArea;
                int w = (int)((image.PixelWidth) + 2 * (Glow + 1) * dpi), h = (int)((image.PixelHeight) + 2 * (Glow + 1) * dpi);
                x = Math.Clamp(area.Left + (area.Width - w) / 2 + 40 * (OpenCount % 5), area.Left, Math.Max(area.Left, area.Right - Math.Min(w, area.Width)));
                y = Math.Clamp(area.Top + (area.Height - h) / 2 + 40 * (OpenCount % 5), area.Top, Math.Max(area.Top, area.Bottom - Math.Min(h, area.Height)));
            }
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOZORDER | SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Close(); return; }
            try { DragMove(); } catch (InvalidOperationException) { /* botón ya soltado */ }
        };
        MouseWheel += (_, e) =>
        {
            _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.15, 5);
            ApplyScale();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) Exporter.CopyToClipboard(_image);
        };
        ContextMenu = BuildMenu();
        Loaded += (_, _) => OpenCount++;
        Closed += (_, _) => OpenCount--;
    }

    private const uint SWP_NOSIZE = 0x0001;

    private void ApplyScale()
    {
        // A escala 1 se ve píxel por píxel, sin importar el escalado de Windows.
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _view.Width = _image.PixelWidth / dpi * _scale;
        _view.Height = _image.PixelHeight / dpi * _scale;
        RenderOptions.SetBitmapScalingMode(_view, Math.Abs(_scale - 1) < 0.001 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Copiar", () => Exporter.CopyToClipboard(_image));
        Item("Guardar", () => { string p = _settings.NextFilePath(); Exporter.SavePng(_image, p); ToastWindow.Show("Captura guardada", System.IO.Path.GetFileName(p)); });
        Item("Abrir en el editor", () => { new EditorWindow(_image, _settings).Show(); Close(); });
        menu.Items.Add(new Separator());
        Item("Tamaño real (100 %)", () => { _scale = 1; ApplyScale(); });
        Item("Opacidad 100 %", () => Opacity = 1);
        Item("Opacidad 70 %", () => Opacity = 0.7);
        Item("Opacidad 40 %", () => Opacity = 0.4);
        menu.Items.Add(new Separator());
        Item("Cerrar (doble clic)", Close);
        return menu;
    }
}

/// <summary>Aviso breve en la esquina inferior derecha (no roba el foco).</summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    public static void Show(string title, string? message = null)
    {
        _current?.Close();
        _current = new ToastWindow(title, message);
        _current.Show();
    }

    private ToastWindow(string title, string? message)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI");

        var stack = new StackPanel { Margin = new Thickness(16, 12, 20, 12), MaxWidth = 360 };
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xCC)), FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 54, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
            });
        }
        var accent = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(10, 10, 0, 10), Background = (Brush)Application.Current.FindResource("AccentGradient") };
        var row = new DockPanel();
        DockPanel.SetDock(accent, Dock.Left);
        row.Children.Add(accent);
        row.Children.Add(stack);
        Content = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1A, 0x1C, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x3C)),
            BorderThickness = new Thickness(1),
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5, Direction = 270 },
        };

        Loaded += async (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
            await Task.Delay(2600);
            if (_current == this) _current = null;
            if (IsLoaded) Close();
        };
    }
}
