using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Services;

namespace AppShotScreen.Capture;

/// <summary>
/// Ventana a pantalla completa sobre todos los monitores que muestra la pantalla "congelada".
/// Al pasar el ratón resalta la ventana que hay debajo (un clic la captura); arrastrando se elige
/// una región libre. Incluye una lupa con las coordenadas y el color del píxel.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly BitmapSource _frozen;
    private readonly PixelRect _bounds;
    private readonly List<WindowFinder.WindowInfo> _windows;
    private readonly byte[] _pixels;
    private readonly int _stride;

    private Point? _start;
    private bool _dragging;
    private Rect _selection = Rect.Empty;       // en unidades WPF
    private Int32Rect _hoverPx = Int32Rect.Empty; // ventana bajo el cursor, en píxeles de la imagen
    private Color _colorUnderCursor;

    public BitmapSource? Result { get; private set; }

    /// <summary>Región elegida en coordenadas de pantalla (píxeles físicos).</summary>
    public PixelRect? SelectedScreenRect { get; private set; }

    /// <summary>Color copiado con la tecla C (formato #RRGGBB), si el usuario lo pidió.</summary>
    public string? PickedColor { get; private set; }

    public string HintText { set => HintLabel.Text = value; }

    public OverlayWindow(BitmapSource frozenScreen, PixelRect bounds, List<WindowFinder.WindowInfo>? windows = null)
    {
        InitializeComponent();
        _frozen = frozenScreen;
        _bounds = bounds;
        _windows = windows ?? new List<WindowFinder.WindowInfo>();
        Shot.Source = frozenScreen;

        _stride = frozenScreen.PixelWidth * 4;
        _pixels = new byte[_stride * frozenScreen.PixelHeight];
        frozenScreen.CopyPixels(_pixels, _stride, 0);

        SourceInitialized += (_, _) =>
        {
            // Colocamos la ventana en píxeles físicos para cubrir exactamente el escritorio virtual.
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, IntPtr.Zero, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Native.SWP_NOZORDER);
        };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Track(Mouse.GetPosition(Root));
        };
        SizeChanged += (_, _) => UpdateVisuals();

        MouseLeftButtonDown += OnDown;
        MouseMove += (_, e) => Track(e.GetPosition(Root));
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += OnKey;
    }

    private double ScaleX => Root.ActualWidth > 0 ? _frozen.PixelWidth / Root.ActualWidth : 1;
    private double ScaleY => Root.ActualHeight > 0 ? _frozen.PixelHeight / Root.ActualHeight : 1;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter)
        {
            Finish(new Int32Rect(0, 0, _frozen.PixelWidth, _frozen.PixelHeight));
        }
        else if (e.Key == Key.C)
        {
            PickedColor = $"#{_colorUnderCursor.R:X2}{_colorUnderCursor.G:X2}{_colorUnderCursor.B:X2}";
            Close();
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Root);
        _dragging = false;
        CaptureMouse();
    }

    /// <summary>Actualiza todo según la posición del cursor.</summary>
    private void Track(Point p)
    {
        if (Root.ActualWidth <= 0) return;
        CrossH.X1 = 0; CrossH.X2 = Root.ActualWidth; CrossH.Y1 = CrossH.Y2 = p.Y;
        CrossV.Y1 = 0; CrossV.Y2 = Root.ActualHeight; CrossV.X1 = CrossV.X2 = p.X;

        int px = Math.Clamp((int)(p.X * ScaleX), 0, _frozen.PixelWidth - 1);
        int py = Math.Clamp((int)(p.Y * ScaleY), 0, _frozen.PixelHeight - 1);
        UpdateLoupe(p, px, py);

        if (_start is { } s)
        {
            if (!_dragging && (Math.Abs(p.X - s.X) > 4 || Math.Abs(p.Y - s.Y) > 4))
            {
                _dragging = true;
                Hint.Visibility = Visibility.Collapsed;
            }
            if (_dragging) _selection = new Rect(s, p);
        }
        else
        {
            _hoverPx = WindowAt(px, py);
        }
        UpdateVisuals();
    }

    /// <summary>Ventana más alta (en orden Z) que contiene el punto, recortada a la pantalla.</summary>
    private Int32Rect WindowAt(int px, int py)
    {
        int sx = px + _bounds.X, sy = py + _bounds.Y;
        foreach (var w in _windows)
        {
            var b = w.Bounds;
            if (sx < b.X || sy < b.Y || sx >= b.X + b.Width || sy >= b.Y + b.Height) continue;
            int x0 = Math.Max(0, b.X - _bounds.X), y0 = Math.Max(0, b.Y - _bounds.Y);
            int x1 = Math.Min(_frozen.PixelWidth, b.X + b.Width - _bounds.X), y1 = Math.Min(_frozen.PixelHeight, b.Y + b.Height - _bounds.Y);
            if (x1 - x0 < 10 || y1 - y0 < 10) continue;
            return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }
        return Int32Rect.Empty;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_start is null) return;
        bool wasDrag = _dragging;
        _start = null;
        _dragging = false;

        if (wasDrag)
        {
            var px = ToPixels(_selection);
            if (px.Width >= 3 && px.Height >= 3) { Finish(px); return; }
        }
        else if (!_hoverPx.IsEmpty)
        {
            // Clic sin arrastrar: capturamos la ventana resaltada.
            Finish(_hoverPx);
            return;
        }
        _selection = Rect.Empty;
        Track(e.GetPosition(Root));
    }

    private void Finish(Int32Rect px)
    {
        Result = px.Width == _frozen.PixelWidth && px.Height == _frozen.PixelHeight ? _frozen : ScreenCapture.Crop(_frozen, px);
        SelectedScreenRect = new PixelRect(_bounds.X + px.X, _bounds.Y + px.Y, px.Width, px.Height);
        Close();
    }

    /// <summary>Convierte un rectángulo en unidades WPF de la ventana a píxeles de la imagen congelada.</summary>
    private Int32Rect ToPixels(Rect r)
    {
        if (r.IsEmpty || Root.ActualWidth <= 0) return Int32Rect.Empty;
        int x = (int)Math.Round(r.X * ScaleX), y = (int)Math.Round(r.Y * ScaleY);
        int w = (int)Math.Round(r.Width * ScaleX), h = (int)Math.Round(r.Height * ScaleY);
        x = Math.Clamp(x, 0, _frozen.PixelWidth - 1);
        y = Math.Clamp(y, 0, _frozen.PixelHeight - 1);
        w = Math.Clamp(w, 0, _frozen.PixelWidth - x);
        h = Math.Clamp(h, 0, _frozen.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    private void UpdateVisuals()
    {
        if (Root.ActualWidth <= 0) return;
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));

        Rect shown;
        string label;
        if (_dragging && !_selection.IsEmpty)
        {
            shown = _selection;
            var px = ToPixels(_selection);
            label = $"{px.Width} × {px.Height}";
        }
        else if (!_hoverPx.IsEmpty)
        {
            shown = new Rect(_hoverPx.X / ScaleX, _hoverPx.Y / ScaleY, _hoverPx.Width / ScaleX, _hoverPx.Height / ScaleY);
            label = $"Ventana  ·  {_hoverPx.Width} × {_hoverPx.Height}";
        }
        else
        {
            Dim.Data = full;
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(shown));
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Margin = new Thickness(shown.X, shown.Y, 0, 0);
        SelectionBorder.Width = shown.Width;
        SelectionBorder.Height = shown.Height;

        SizeText.Text = label;
        SizeLabel.Visibility = Visibility.Visible;
        double labelY = shown.Y - 28 < 0 ? shown.Y + 6 : shown.Y - 28;
        SizeLabel.Margin = new Thickness(Math.Max(0, shown.X), labelY, 0, 0);
    }

    private void UpdateLoupe(Point p, int px, int py)
    {
        const int cols = 13, rows = 11; // la lupa mide 143×121: píxeles de 11×11
        int x = Math.Clamp(px - cols / 2, 0, Math.Max(0, _frozen.PixelWidth - cols));
        int y = Math.Clamp(py - rows / 2, 0, Math.Max(0, _frozen.PixelHeight - rows));
        if (_frozen.PixelWidth >= cols && _frozen.PixelHeight >= rows)
            LoupeImage.Source = new CroppedBitmap(_frozen, new Int32Rect(x, y, cols, rows));

        int i = py * _stride + px * 4;
        _colorUnderCursor = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
        LoupeSwatch.Background = new SolidColorBrush(_colorUnderCursor);
        LoupeText.Text = $"#{_colorUnderCursor.R:X2}{_colorUnderCursor.G:X2}{_colorUnderCursor.B:X2}";
        LoupeCoords.Text = $"{px + _bounds.X},{py + _bounds.Y}";

        // Junto al cursor, sin salirse de la pantalla.
        const double w = 146, h = 150, gap = 18;
        double lx = p.X + gap + w > Root.ActualWidth ? p.X - gap - w : p.X + gap;
        double ly = p.Y + gap + h > Root.ActualHeight ? p.Y - gap - h : p.Y + gap;
        Loupe.Margin = new Thickness(Math.Max(0, lx), Math.Max(0, ly), 0, 0);
        Loupe.Visibility = Visibility.Visible;
    }
}
