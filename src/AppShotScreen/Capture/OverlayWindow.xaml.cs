using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AppShotScreen.Services;

namespace AppShotScreen.Capture;

/// <summary>
/// Ventana a pantalla completa sobre todos los monitores que muestra la pantalla "congelada".
/// Al pasar el ratón resalta la ventana que hay debajo (un clic la elige); arrastrando se elige
/// una región libre. Después la región se puede ajustar con los tiradores (esquinas y lados),
/// moverla arrastrando por dentro o con las flechas, y se confirma con Enter, doble clic o "Capturar".
/// Incluye una lupa con las coordenadas y el color del píxel.
/// </summary>
public partial class OverlayWindow : Window
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private enum Op { None, New, Move, Resize }

    private readonly BitmapSource _frozen;
    private readonly PixelRect _bounds;
    private readonly List<WindowFinder.WindowInfo> _windows;
    private readonly byte[] _pixels;
    private readonly int _stride;

    private Point? _start;
    private bool _dragging;
    private Op _op;
    private Edge _edges;
    private Rect _anchor;                       // región al empezar a mover o redimensionar
    private bool _adjusting;                    // ya hay una región elegida y se está ajustando
    private Rect _selection = Rect.Empty;       // en unidades WPF
    private Int32Rect _hoverPx = Int32Rect.Empty; // ventana bajo el cursor, en píxeles de la imagen
    private Color _colorUnderCursor;

    private readonly List<(FrameworkElement Shape, Edge Edge)> _handles = new();
    private readonly List<Polyline> _corners = new();
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x5B, 0x8C, 0xFF));

    private const double HandleHit = 11; // radio (en unidades WPF) para "agarrar" un tirador
    private const double EdgeHit = 6;    // distancia a un lado para redimensionar por el borde

    public BitmapSource? Result { get; private set; }

    /// <summary>Región elegida en coordenadas de pantalla (píxeles físicos).</summary>
    public PixelRect? SelectedScreenRect { get; private set; }

    /// <summary>Color copiado con la tecla C (formato #RRGGBB), si el usuario lo pidió.</summary>
    public string? PickedColor { get; private set; }

    /// <summary>Si es false, la región se captura en cuanto se suelta el ratón (sin paso de ajuste).</summary>
    public bool AdjustBeforeCapture { get; set; } = true;

    public string HintText { set => HintLabel.Text = value; }

    internal bool IsAdjusting => _adjusting;

    /// <summary>En la prueba automática, el ratón real no interviene.</summary>
    internal bool TestMode { get; set; }
    internal Int32Rect SelectionPixels => ToPixels(_selection);

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
        BuildHandles();

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
            if (!TestMode) Track(Mouse.GetPosition(Root));
        };
        SizeChanged += (_, _) => UpdateVisuals();

        MouseLeftButtonDown += OnDown;
        MouseMove += (_, e) => { if (!TestMode) Track(e.GetPosition(Root)); };
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) =>
        {
            // Con una región elegida, el clic derecho vuelve a empezar; si no, cancela.
            if (_adjusting) { ResetSelection(); Track(e.GetPosition(Root)); }
            else Close();
        };
        KeyDown += OnKey;

        MakeButton(ConfirmButton, Confirm);
        MakeButton(RedoButton, () => { ResetSelection(); Track(Mouse.GetPosition(Root)); });
        MakeButton(CancelButton, Close);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Un Border que se comporta como botón (sin que el clic llegue a la selección).</summary>
    private static void MakeButton(Border b, Action action)
    {
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; action(); };
    }

    private double ScaleX => Root.ActualWidth > 0 ? _frozen.PixelWidth / Root.ActualWidth : 1;
    private double ScaleY => Root.ActualHeight > 0 ? _frozen.PixelHeight / Root.ActualHeight : 1;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter)
        {
            if (_adjusting) Confirm();
            else Finish(new Int32Rect(0, 0, _frozen.PixelWidth, _frozen.PixelHeight));
        }
        else if (e.Key == Key.C)
        {
            PickedColor = $"#{_colorUnderCursor.R:X2}{_colorUnderCursor.G:X2}{_colorUnderCursor.B:X2}";
            Close();
        }
        else if (_adjusting && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            // Flechas: mover 1 píxel (Mayús = 10). Ctrl + flechas: agrandar o achicar desde la esquina inferior derecha.
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            double dx = (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0) / ScaleX;
            double dy = (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0) / ScaleY;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                _selection = Normalize(new Rect(_selection.X, _selection.Y, Math.Max(MinSize.Width, _selection.Width + dx), Math.Max(MinSize.Height, _selection.Height + dy)));
            else
                _selection = KeepInside(new Rect(_selection.X + dx, _selection.Y + dy, _selection.Width, _selection.Height));
            UpdateVisuals();
            e.Handled = true;
        }
    }

    private Size MinSize => new(3 / ScaleX, 3 / ScaleY);

    private void OnDown(object sender, MouseButtonEventArgs e) => PointerDown(e.GetPosition(Root), e.ClickCount);

    internal void PointerDown(Point p, int clicks = 1)
    {
        if (_adjusting)
        {
            var edge = HitTest(p);
            if (edge == Edge.Move && clicks == 2) { Confirm(); return; }
            if (edge != Edge.None)
            {
                _op = edge == Edge.Move ? Op.Move : Op.Resize;
                _edges = edge;
                _anchor = _selection;
                _start = p;
                if (!TestMode) CaptureMouse();
                UpdateVisuals();
                return;
            }
            // Fuera de la región: se empieza otra.
            ResetSelection();
        }
        _op = Op.New;
        _start = p;
        _dragging = false;
        if (!TestMode) CaptureMouse();
    }

    /// <summary>Actualiza todo según la posición del cursor.</summary>
    private void Track(Point p)
    {
        if (Root.ActualWidth <= 0) return;
        p = new Point(Math.Clamp(p.X, 0, Root.ActualWidth), Math.Clamp(p.Y, 0, Root.ActualHeight));
        CrossH.X1 = 0; CrossH.X2 = Root.ActualWidth; CrossH.Y1 = CrossH.Y2 = p.Y;
        CrossV.Y1 = 0; CrossV.Y2 = Root.ActualHeight; CrossV.X1 = CrossV.X2 = p.X;

        int px = Math.Clamp((int)(p.X * ScaleX), 0, _frozen.PixelWidth - 1);
        int py = Math.Clamp((int)(p.Y * ScaleY), 0, _frozen.PixelHeight - 1);

        if (_start is { } s)
        {
            var d = p - s;
            switch (_op)
            {
                case Op.New:
                    if (!_dragging && (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4))
                    {
                        _dragging = true;
                        Hint.Visibility = Visibility.Collapsed;
                    }
                    if (_dragging) _selection = new Rect(s, p);
                    break;
                case Op.Move:
                    _selection = KeepInside(new Rect(_anchor.X + d.X, _anchor.Y + d.Y, _anchor.Width, _anchor.Height));
                    break;
                case Op.Resize:
                    _selection = Resize(_anchor, _edges, p);
                    break;
            }
        }
        else if (_adjusting)
        {
            Cursor = CursorFor(HitTest(p));
        }
        else
        {
            _hoverPx = WindowAt(px, py);
        }
        UpdateLoupe(p, px, py);
        UpdateVisuals();
    }

    /// <summary>Mueve los lados indicados hasta el punto; si se cruzan, la región se "da la vuelta".</summary>
    private Rect Resize(Rect r, Edge edges, Point p)
    {
        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        if (edges.HasFlag(Edge.Left)) l = p.X;
        if (edges.HasFlag(Edge.Right)) rr = p.X;
        if (edges.HasFlag(Edge.Top)) t = p.Y;
        if (edges.HasFlag(Edge.Bottom)) b = p.Y;
        return Normalize(new Rect(new Point(l, t), new Point(rr, b)));
    }

    /// <summary>Recorta a la pantalla y asegura un tamaño mínimo.</summary>
    private Rect Normalize(Rect r)
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        double l = Math.Clamp(r.Left, 0, w), t = Math.Clamp(r.Top, 0, h);
        double rr = Math.Clamp(r.Right, 0, w), b = Math.Clamp(r.Bottom, 0, h);
        if (rr - l < MinSize.Width) { if (l + MinSize.Width <= w) rr = l + MinSize.Width; else l = rr - MinSize.Width; }
        if (b - t < MinSize.Height) { if (t + MinSize.Height <= h) b = t + MinSize.Height; else t = b - MinSize.Height; }
        return new Rect(new Point(l, t), new Point(rr, b));
    }

    /// <summary>Al mover, la región no se sale de la pantalla (conserva su tamaño).</summary>
    private Rect KeepInside(Rect r)
    {
        double x = Math.Clamp(r.X, 0, Math.Max(0, Root.ActualWidth - r.Width));
        double y = Math.Clamp(r.Y, 0, Math.Max(0, Root.ActualHeight - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>Qué parte de la región hay bajo el punto: un tirador, un lado, el interior o nada.</summary>
    private Edge HitTest(Point p)
    {
        if (_selection.IsEmpty) return Edge.None;
        foreach (var (shape, edge) in _handles)
        {
            if (shape.Visibility != Visibility.Visible) continue;
            if ((p - HandleCenter(edge)).Length <= HandleHit) return edge;
        }
        var r = _selection;
        bool inY = p.Y >= r.Top - EdgeHit && p.Y <= r.Bottom + EdgeHit;
        bool inX = p.X >= r.Left - EdgeHit && p.X <= r.Right + EdgeHit;
        if (inY && Math.Abs(p.X - r.Left) <= EdgeHit) return Edge.Left;
        if (inY && Math.Abs(p.X - r.Right) <= EdgeHit) return Edge.Right;
        if (inX && Math.Abs(p.Y - r.Top) <= EdgeHit) return Edge.Top;
        if (inX && Math.Abs(p.Y - r.Bottom) <= EdgeHit) return Edge.Bottom;
        return r.Contains(p) ? Edge.Move : Edge.None;
    }

    private static Cursor CursorFor(Edge e) => e switch
    {
        Edge.Move => Cursors.SizeAll,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

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

    private void OnUp(object sender, MouseButtonEventArgs e) => PointerUp(e.GetPosition(Root));

    internal void PointerMove(Point p) => Track(p);

    internal void PointerUp(Point up)
    {
        if (!TestMode) ReleaseMouseCapture();
        if (_start is null) return;
        var op = _op;
        bool wasDrag = _dragging;
        _start = null;
        _dragging = false;
        _op = Op.None;

        if (op is Op.Move or Op.Resize)
        {
            Track(up);
            return;
        }

        Rect chosen = Rect.Empty;
        if (wasDrag)
        {
            var px = ToPixels(_selection);
            if (px.Width >= 3 && px.Height >= 3) chosen = Normalize(_selection);
        }
        else
        {
            // Clic sin arrastrar: la ventana resaltada.
            var p = up;
            var hover = WindowAt(Math.Clamp((int)(p.X * ScaleX), 0, _frozen.PixelWidth - 1), Math.Clamp((int)(p.Y * ScaleY), 0, _frozen.PixelHeight - 1));
            if (!hover.IsEmpty) chosen = new Rect(hover.X / ScaleX, hover.Y / ScaleY, hover.Width / ScaleX, hover.Height / ScaleY);
        }

        if (chosen.IsEmpty)
        {
            ResetSelection();
            Track(up);
            return;
        }
        if (!AdjustBeforeCapture) { Finish(ToPixels(chosen)); return; }
        BeginAdjust(chosen);
        Track(up);
    }

    /// <summary>Pasa a ajustar la región: aparecen los tiradores y la barra para confirmar.</summary>
    internal void BeginAdjust(Rect selection)
    {
        _selection = selection;
        _adjusting = true;
        _hoverPx = Int32Rect.Empty;
        Hint.Visibility = Visibility.Collapsed;
        UpdateVisuals();
    }

    private void ResetSelection()
    {
        _adjusting = false;
        _selection = Rect.Empty;
        _op = Op.None;
        Cursor = Cursors.Cross;
        UpdateVisuals();
    }

    internal void Confirm()
    {
        var px = ToPixels(_selection);
        if (px.Width >= 1 && px.Height >= 1) Finish(px);
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

    // ───────────── Tiradores ─────────────

    private static readonly Edge[] HandleEdges =
    {
        Edge.Left | Edge.Top, Edge.Top, Edge.Right | Edge.Top, Edge.Right,
        Edge.Right | Edge.Bottom, Edge.Bottom, Edge.Left | Edge.Bottom, Edge.Left,
    };

    private static bool IsCorner(Edge e) => e is not (Edge.Left or Edge.Right or Edge.Top or Edge.Bottom);

    private void BuildHandles()
    {
        // Escuadras en las esquinas: un trazo más grueso que enmarca la región (debajo de los círculos).
        for (int i = 0; i < 4; i++)
        {
            var corner = new Polyline
            {
                Stroke = AccentBrush, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            _corners.Add(corner);
            Handles.Children.Add(corner);
        }
        foreach (var edge in HandleEdges)
        {
            bool corner = IsCorner(edge);
            double size = corner ? 14 : 11;
            var dot = new Ellipse
            {
                Width = size, Height = size, Fill = Brushes.White, Stroke = AccentBrush, StrokeThickness = corner ? 3 : 2.5,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.45 },
            };
            Handles.Children.Add(dot);
            _handles.Add((dot, edge));
        }
    }

    private Point HandleCenter(Edge e)
    {
        var r = _selection;
        double x = e.HasFlag(Edge.Left) ? r.Left : e.HasFlag(Edge.Right) ? r.Right : r.Left + r.Width / 2;
        double y = e.HasFlag(Edge.Top) ? r.Top : e.HasFlag(Edge.Bottom) ? r.Bottom : r.Top + r.Height / 2;
        return new Point(x, y);
    }

    private void LayoutHandles()
    {
        var r = _selection;
        bool small = r.Width < 48 || r.Height < 48; // en regiones pequeñas solo las esquinas
        foreach (var (shape, edge) in _handles)
        {
            shape.Visibility = small && !IsCorner(edge) ? Visibility.Collapsed : Visibility.Visible;
            var c = HandleCenter(edge);
            Canvas.SetLeft(shape, c.X - shape.Width / 2);
            Canvas.SetTop(shape, c.Y - shape.Height / 2);
        }
        double arm = Math.Min(22, Math.Min(r.Width, r.Height) / 3);
        Point[][] pts =
        {
            new[] { new Point(r.Left, r.Top + arm), r.TopLeft, new Point(r.Left + arm, r.Top) },
            new[] { new Point(r.Right - arm, r.Top), r.TopRight, new Point(r.Right, r.Top + arm) },
            new[] { new Point(r.Right, r.Bottom - arm), r.BottomRight, new Point(r.Right - arm, r.Bottom) },
            new[] { new Point(r.Left + arm, r.Bottom), r.BottomLeft, new Point(r.Left, r.Bottom - arm) },
        };
        for (int i = 0; i < 4; i++) _corners[i].Points = new PointCollection(pts[i]);
    }

    /// <summary>La barra va debajo de la región; si no cabe, encima; y si tampoco, dentro.</summary>
    private void LayoutAdjustBar()
    {
        AdjustBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = AdjustBar.DesiredSize;
        var r = _selection;
        const double gap = 12;
        double x = Math.Clamp(r.Right - size.Width, 4, Math.Max(4, Root.ActualWidth - size.Width - 4));
        double y = r.Bottom + gap;
        if (y + size.Height > Root.ActualHeight - 4) y = r.Top - gap - size.Height;
        if (y < 4) y = Math.Max(4, r.Bottom - size.Height - gap);
        AdjustBar.Margin = new Thickness(x, y, 0, 0);
    }

    private void UpdateVisuals()
    {
        if (Root.ActualWidth <= 0) return;
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));

        Rect shown;
        string label;
        if ((_adjusting || _dragging) && !_selection.IsEmpty)
        {
            shown = _selection;
            var px = ToPixels(_selection);
            label = $"{px.Width} × {px.Height}";
        }
        else if (!_hoverPx.IsEmpty && !_adjusting)
        {
            shown = new Rect(_hoverPx.X / ScaleX, _hoverPx.Y / ScaleY, _hoverPx.Width / ScaleX, _hoverPx.Height / ScaleY);
            label = $"Ventana  ·  {_hoverPx.Width} × {_hoverPx.Height}";
        }
        else
        {
            Dim.Data = full;
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            Handles.Visibility = AdjustBar.Visibility = Visibility.Collapsed;
            CrossH.Visibility = CrossV.Visibility = Visibility.Visible;
            return;
        }

        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(shown));
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.StrokeThickness = _adjusting ? 1.5 : 2;
        SelectionBorder.Margin = new Thickness(shown.X, shown.Y, 0, 0);
        SelectionBorder.Width = shown.Width;
        SelectionBorder.Height = shown.Height;

        SizeText.Text = label;
        SizeLabel.Visibility = Visibility.Visible;
        double labelY = shown.Y - 32 < 0 ? shown.Y + 10 : shown.Y - 32;
        SizeLabel.Margin = new Thickness(Math.Max(0, shown.X), labelY, 0, 0);

        // Mientras se ajusta: tiradores y barra. Las líneas en cruz solo al elegir o al arrastrar un tirador.
        Handles.Visibility = _adjusting ? Visibility.Visible : Visibility.Collapsed;
        CrossH.Visibility = CrossV.Visibility = !_adjusting || _op == Op.Resize ? Visibility.Visible : Visibility.Collapsed;
        if (_adjusting) LayoutHandles();

        bool showBar = _adjusting && _op == Op.None;
        AdjustBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showBar)
        {
            AdjustSize.Text = label;
            LayoutAdjustBar();
        }
    }

    private void UpdateLoupe(Point p, int px, int py)
    {
        // Mientras se ajusta sin arrastrar, la lupa estorbaría al usar la barra: se oculta.
        if (_adjusting && _op == Op.None)
        {
            Loupe.Visibility = Visibility.Collapsed;
        }
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
        if (_adjusting && _op == Op.None) return;

        // Junto al cursor, sin salirse de la pantalla.
        const double w = 146, h = 150, gap = 18;
        double lx = p.X + gap + w > Root.ActualWidth ? p.X - gap - w : p.X + gap;
        double ly = p.Y + gap + h > Root.ActualHeight ? p.Y - gap - h : p.Y + gap;
        Loupe.Margin = new Thickness(Math.Max(0, lx), Math.Max(0, ly), 0, 0);
        Loupe.Visibility = Visibility.Visible;
    }
}
