using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AppShotScreen.Editor;

/// <summary>Fábrica de elementos de anotación que se colocan sobre el lienzo.</summary>
public static class Annotations
{
    public const string StepTag = "step";

    private static readonly Effect SoftShadow = CreateShadow();

    private static Effect CreateShadow()
    {
        var s = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.45, Color = Colors.Black };
        s.Freeze();
        return s;
    }

    public static UIElement Arrow(Point from, Point to, Brush brush, double thickness)
    {
        double len = (to - from).Length;
        double head = Math.Max(10, thickness * 4);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (len > 0.5)
            {
                var dir = (to - from) / len;
                var normal = new Vector(-dir.Y, dir.X);
                var basePt = to - dir * Math.Min(head, len);
                // Cuerpo (termina en la base de la punta para que no sobresalga)
                ctx.BeginFigure(from, false, false);
                ctx.LineTo(basePt, true, true);
                // Punta rellena
                ctx.BeginFigure(to, true, true);
                ctx.LineTo(basePt + normal * head * 0.55, true, true);
                ctx.LineTo(basePt - normal * head * 0.55, true, true);
            }
        }
        geo.Freeze();
        return new Path
        {
            Data = geo,
            Stroke = brush,
            Fill = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = SoftShadow,
        };
    }

    public static UIElement Rectangle(Rect r, Brush brush, double thickness)
    {
        var el = new Rectangle { Stroke = brush, StrokeThickness = thickness, RadiusX = 3, RadiusY = 3, Effect = SoftShadow };
        Place(el, r);
        return el;
    }

    public static UIElement Ellipse(Rect r, Brush brush, double thickness)
    {
        var el = new Ellipse { Stroke = brush, StrokeThickness = thickness, Effect = SoftShadow };
        Place(el, r);
        return el;
    }

    public static UIElement Highlight(Rect r, Brush brush)
    {
        var c = brush is SolidColorBrush sb ? sb.Color : Colors.Yellow;
        var el = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)) };
        Place(el, r);
        return el;
    }

    public static Polyline PenLine(Point start, Brush brush, double thickness) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Points = new PointCollection { start },
    };

    public static TextBox Text(Point at, Brush brush, double thickness)
    {
        var tb = new TextBox
        {
            Foreground = brush,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x29, 0xB6, 0xF6)),
            BorderThickness = new Thickness(1),
            FontSize = 14 + thickness * 2.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            AcceptsReturn = true,
            MinWidth = 40,
            Padding = new Thickness(2),
            CaretBrush = brush,
            Effect = SoftShadow,
        };
        // El borde guía solo se ve mientras se escribe, para que no salga en la imagen final.
        tb.GotKeyboardFocus += (_, _) => tb.BorderThickness = new Thickness(1);
        tb.LostKeyboardFocus += (_, _) => tb.BorderThickness = new Thickness(0);
        Canvas.SetLeft(tb, at.X);
        Canvas.SetTop(tb, at.Y - tb.FontSize * 0.7);
        return tb;
    }

    public static UIElement Step(Point center, int number, Brush brush, double thickness)
    {
        double size = 22 + thickness * 3;
        var grid = new Grid { Width = size, Height = size, Tag = StepTag, Effect = SoftShadow };
        grid.Children.Add(new Ellipse { Fill = brush, Stroke = Brushes.White, StrokeThickness = 2 });
        grid.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            Foreground = IsLight(brush) ? Brushes.Black : Brushes.White,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(grid, center.X - size / 2);
        Canvas.SetTop(grid, center.Y - size / 2);
        return grid;
    }

    public static UIElement BlurPreview(Rect r)
    {
        var el = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)),
        };
        Place(el, r);
        return el;
    }

    /// <summary>Crea un bloque pixelado a partir de la imagen base para ocultar datos sensibles.</summary>
    public static UIElement Pixelate(BitmapSource source, Rect r)
    {
        int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y);
        int w = Math.Min((int)Math.Ceiling(r.Width), source.PixelWidth - x);
        int h = Math.Min((int)Math.Ceiling(r.Height), source.PixelHeight - y);
        if (w < 1 || h < 1) return new Canvas();

        var crop = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        double block = Math.Min(Math.Clamp(Math.Min(w, h) / 6.0, 6, 16), Math.Min(w, h));
        var small = new TransformedBitmap(crop, new ScaleTransform(1 / block, 1 / block));
        small.Freeze();

        var img = new Image { Source = small, Width = w, Height = h, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, x);
        Canvas.SetTop(img, y);
        return img;
    }

    private static void Place(FrameworkElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        el.Width = r.Width;
        el.Height = r.Height;
    }

    private static bool IsLight(Brush b) =>
        b is SolidColorBrush s && (0.299 * s.Color.R + 0.587 * s.Color.G + 0.114 * s.Color.B) > 170;
}
