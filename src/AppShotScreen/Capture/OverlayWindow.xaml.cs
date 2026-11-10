using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Capture;

/// <summary>
/// Ventana a pantalla completa sobre todos los monitores que muestra la pantalla "congelada"
/// y deja al usuario arrastrar un rectángulo. Devuelve la región recortada en píxeles reales.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly BitmapSource _frozen;
    private readonly PixelRect _bounds;
    private Point? _start;
    private Rect _selection;

    public BitmapSource? Result { get; private set; }

    /// <summary>Región elegida en coordenadas de pantalla (píxeles físicos).</summary>
    public PixelRect? SelectedScreenRect { get; private set; }

    public string HintText { set => HintLabel.Text = value; }

    public OverlayWindow(BitmapSource frozenScreen, PixelRect bounds)
    {
        InitializeComponent();
        _frozen = frozenScreen;
        _bounds = bounds;
        Shot.Source = frozenScreen;

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
            UpdateDim();
        };
        SizeChanged += (_, _) => UpdateDim();

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter)
        {
            Result = _frozen;
            SelectedScreenRect = _bounds;
            Close();
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Root);
        Hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Root);
        CrossH.X1 = 0; CrossH.X2 = Root.ActualWidth; CrossH.Y1 = CrossH.Y2 = p.Y;
        CrossV.Y1 = 0; CrossV.Y2 = Root.ActualHeight; CrossV.X1 = CrossV.X2 = p.X;

        if (_start is not { } s) return;
        _selection = new Rect(s, p);
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Margin = new Thickness(_selection.X, _selection.Y, 0, 0);
        SelectionBorder.Width = _selection.Width;
        SelectionBorder.Height = _selection.Height;

        var px = ToPixels(_selection);
        SizeText.Text = $"{px.Width} × {px.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        double labelY = _selection.Y - 26 < 0 ? _selection.Bottom + 4 : _selection.Y - 26;
        SizeLabel.Margin = new Thickness(_selection.X, labelY, 0, 0);
        UpdateDim();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_start is null) return;
        var px = ToPixels(_selection);
        if (px.Width >= 3 && px.Height >= 3)
        {
            Result = ScreenCapture.Crop(_frozen, px);
            SelectedScreenRect = new PixelRect(_bounds.X + px.X, _bounds.Y + px.Y, px.Width, px.Height);
            Close();
        }
        else
        {
            // Clic sin arrastrar: reiniciamos.
            _start = null;
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            _selection = Rect.Empty;
            UpdateDim();
        }
    }

    /// <summary>Convierte un rectángulo en unidades WPF de la ventana a píxeles de la imagen congelada.</summary>
    private Int32Rect ToPixels(Rect r)
    {
        if (r.IsEmpty || Root.ActualWidth <= 0) return Int32Rect.Empty;
        double sx = _frozen.PixelWidth / Root.ActualWidth;
        double sy = _frozen.PixelHeight / Root.ActualHeight;
        int x = (int)Math.Round(r.X * sx), y = (int)Math.Round(r.Y * sy);
        int w = (int)Math.Round(r.Width * sx), h = (int)Math.Round(r.Height * sy);
        x = Math.Clamp(x, 0, _frozen.PixelWidth - 1);
        y = Math.Clamp(y, 0, _frozen.PixelHeight - 1);
        w = Math.Clamp(w, 0, _frozen.PixelWidth - x);
        h = Math.Clamp(h, 0, _frozen.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        if (_selection.IsEmpty || _start is null)
        {
            Dim.Data = full;
            return;
        }
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
    }
}
