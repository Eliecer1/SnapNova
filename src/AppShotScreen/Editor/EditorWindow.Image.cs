using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using AppShotScreen.Services;

namespace AppShotScreen.Editor;

// Lupa (ampliar una zona) y redimensionar la imagen.
public partial class EditorWindow
{
    /// <summary>
    /// Lupa: muestra la zona marcada ampliada, con marco blanco y sombra, centrada sobre ella.
    /// Luego se puede mover con la herramienta Seleccionar.
    /// </summary>
    internal UIElement AddMagnifier(Rect r, double factor = 2)
    {
        var flat = RenderAnnotated();
        var px = new Int32Rect(
            Math.Clamp((int)r.X, 0, flat.PixelWidth - 1), Math.Clamp((int)r.Y, 0, flat.PixelHeight - 1),
            Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        px.Width = Math.Min(px.Width, flat.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, flat.PixelHeight - px.Y);

        // Ampliamos ×2, pero sin pasarnos del tamaño del lienzo.
        factor = Math.Min(factor, Math.Min(Surface.Width * 0.95 / px.Width, Surface.Height * 0.95 / px.Height));
        factor = Math.Max(1.15, factor);
        double w = px.Width * factor, h = px.Height * factor;

        var source = new CroppedBitmap(flat, px);
        source.Freeze();
        var image = new Image { Source = source, Width = w, Height = h, Stretch = Stretch.Fill, Clip = new RectangleGeometry(new Rect(0, 0, w, h), 8, 8) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            BorderBrush = Brushes.White, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(10), Background = Brushes.White,
            Child = image, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black },
        };
        double left = px.X + px.Width / 2.0 - w / 2 - 3, top = px.Y + px.Height / 2.0 - h / 2 - 3;
        Canvas.SetLeft(frame, Math.Clamp(left, 0, Math.Max(0, Surface.Width - w - 6)));
        Canvas.SetTop(frame, Math.Clamp(top, 0, Math.Max(0, Surface.Height - h - 6)));
        AddElement(frame);
        return frame;
    }

    /// <summary>Sustituye la imagen base (aplanando las anotaciones) con posibilidad de deshacer.</summary>
    private void ReplaceImage(BitmapSource newBase)
    {
        var oldBase = _base;
        var oldChildren = Surface.Children.Cast<UIElement>().Where(c => c != BaseImage).ToList();
        var oldHoles = _spotHoles.ToList();
        void ApplyNew()
        {
            foreach (var c in oldChildren) Surface.Children.Remove(c);
            _spotHoles.Clear();
            SetBase(newBase);
        }
        void Restore()
        {
            SetBase(oldBase);
            foreach (var c in oldChildren) Surface.Children.Add(c);
            _spotHoles.AddRange(oldHoles);
        }
        Select(null);
        ApplyNew();
        _undo.Push(new DelegateAction(Restore, ApplyNew));
    }

    /// <summary>Redimensiona la imagen (con sus anotaciones) al ancho indicado, manteniendo la proporción.</summary>
    internal void ResizeTo(int width)
    {
        var flat = RenderAnnotated();
        width = Math.Clamp(width, 16, 12000);
        if (width == flat.PixelWidth) return;
        int height = Math.Max(1, (int)Math.Round(flat.PixelHeight * (double)width / flat.PixelWidth));
        ReplaceImage(ImageTools.Resize(flat, width, height));
        FitIfLarger();
    }

    private void Resize_Click(object sender, RoutedEventArgs e)
    {
        string text = ResizeWidth.Text.Trim().TrimEnd('%', ' ');
        bool percent = ResizeWidth.Text.Contains('%');
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) || value <= 0)
        {
            Flash("Escribe un ancho en píxeles (por ejemplo 800) o un porcentaje (por ejemplo 50%)");
            return;
        }
        int width = percent ? (int)Math.Round(_base.PixelWidth * value / 100.0) : (int)Math.Round(value);
        ResizeTo(width);
        Flash($"Imagen redimensionada a {_base.PixelWidth} × {_base.PixelHeight} px");
        ResizeWidth.Text = _base.PixelWidth.ToString();
    }

    private void ResizeWidth_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Resize_Click(sender, e); e.Handled = true; }
    }

    private void ResizeWidth_GotFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResizeWidth.Text)) ResizeWidth.Text = _base.PixelWidth.ToString();
        ResizeWidth.SelectAll();
    }
}
