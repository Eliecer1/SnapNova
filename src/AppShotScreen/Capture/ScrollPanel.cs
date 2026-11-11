using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AppShotScreen.Capture;

/// <summary>Marco punteado alrededor de la zona. Es "transparente al clic": la rueda llega a la ventana de abajo.</summary>
public sealed class RegionFrameWindow : Window
{
    private const int Pad = 4;
    private readonly PixelRect _region;

    public RegionFrameWindow(PixelRect region)
    {
        _region = region;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x3D)),
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            // El marco va justo por fuera de la zona, para que no salga en la captura.
            Native.SetWindowPos(hwnd, IntPtr.Zero, _region.X - Pad, _region.Y - Pad,
                _region.Width + Pad * 2, _region.Height + Pad * 2, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        };
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>Panel flotante con los botones de la captura con desplazamiento.</summary>
public sealed class ScrollPanelWindow : Window
{
    private readonly PixelRect _region;
    private readonly TextBlock _status;
    private readonly Button _auto;

    public event Action? StopRequested;
    public event Action? CancelRequested;
    public event Action<bool>? AutoToggled;

    private bool _autoOn;

    public ScrollPanelWindow(PixelRect region, bool recording = false)
    {
        _region = region;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x23));
        FontFamily = new FontFamily("Segoe UI");

        _status = new TextBlock
        {
            Text = "Desplaza con la rueda del ratón sobre la zona marcada",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0),
            MinWidth = 300,
        };
        var stop = MakeButton("■  Detener y guardar", Color.FromRgb(0x2F, 0x6F, 0xED));
        stop.Click += (_, _) => StopRequested?.Invoke();
        _auto = MakeButton("▼  Auto", Color.FromRgb(0x3A, 0x3A, 0x42));
        _auto.ToolTip = "Desplazar automáticamente hasta el final";
        _auto.Click += (_, _) => { _autoOn = !_autoOn; SetAutoVisual(_autoOn); AutoToggled?.Invoke(_autoOn); };
        var cancel = MakeButton("✕  Cancelar", Color.FromRgb(0x3A, 0x3A, 0x42));
        cancel.Click += (_, _) => CancelRequested?.Invoke();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
        row.Children.Add(_status);
        row.Children.Add(_auto);
        row.Children.Add(cancel);
        row.Children.Add(stop);
        if (recording)
        {
            // Modo grabación de GIF: mismo panel, sin el botón Auto.
            _auto.Visibility = Visibility.Collapsed;
            _status.Text = "● Preparando grabación…";
            _status.MinWidth = 200;
        }
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x45)),
            BorderThickness = new Thickness(1),
            Child = row,
        };

        // Permite mover el panel arrastrándolo.
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };
        Loaded += (_, _) => PlaceOutsideRegion();
    }

    public void SetHeight(int px) => _status.Text = $"Capturado: {px:N0} px de alto  ·  sigue desplazando";
    public void SetMessage(string text) => _status.Text = text;

    public void SetAutoVisual(bool on)
    {
        _autoOn = on;
        _auto.Background = new SolidColorBrush(on ? Color.FromRgb(0xE0, 0x8A, 0x00) : Color.FromRgb(0x3A, 0x3A, 0x42));
        _auto.Content = on ? "❚❚  Pausar auto" : "▼  Auto";
    }

    private static Button MakeButton(string text, Color bg)
    {
        var b = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        return b;
    }

    /// <summary>Coloca el panel debajo de la zona (o encima, o en una esquina) sin taparla.</summary>
    private void PlaceOutsideRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var wr);
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(_region.X, _region.Y, _region.Width, _region.Height)).WorkingArea;
        const int gap = 10;

        int x = Math.Clamp(_region.X + (_region.Width - w) / 2, screen.Left, Math.Max(screen.Left, screen.Right - w));
        int y;
        if (_region.Y + _region.Height + gap + h <= screen.Bottom) y = _region.Y + _region.Height + gap;
        else if (_region.Y - gap - h >= screen.Top) y = _region.Y - gap - h;
        else
        {
            // No cabe arriba ni abajo: a la derecha o izquierda de la zona.
            y = Math.Clamp(_region.Y + _region.Height - h, screen.Top, screen.Bottom - h);
            if (_region.X + _region.Width + gap + w <= screen.Right) x = _region.X + _region.Width + gap;
            else if (_region.X - gap - w >= screen.Left) x = _region.X - gap - w;
            else { x = screen.Right - w - gap; y = screen.Bottom - h - gap; } // último recurso: esquina
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
}
