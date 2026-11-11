using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace AppShotScreen.Editor;

/// <summary>Sellos vectoriales y globos de texto (como los "stamps" y "callouts" de Snagit).</summary>
public static class Stamps
{
    public static readonly (string Kind, string Name)[] Kinds =
    {
        ("ok", "Correcto"), ("no", "Incorrecto"), ("warn", "Atención"),
        ("star", "Destacado"), ("info", "Información"), ("ask", "Duda"),
    };

    private static Effect Shadow() => new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.45, Color = Colors.Black };

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    /// <summary>Crea el sello centrado en <paramref name="center"/>.</summary>
    public static FrameworkElement Create(string kind, Point center, double size)
    {
        var el = Build(kind, size);
        el.Effect = Shadow();
        Canvas.SetLeft(el, center.X - size / 2);
        Canvas.SetTop(el, center.Y - size / 2);
        return el;
    }

    /// <summary>El dibujo del sello, sin posición (sirve también para los botones del panel).</summary>
    public static FrameworkElement Build(string kind, double size)
    {
        var grid = new Grid { Width = size, Height = size };
        var white = Brushes.White;
        double stroke = Math.Max(2, size / 9);

        Path Glyph(string data) => new()
        {
            Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Stroke = white, StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(size * 0.27),
        };
        TextBlock Letter(string s, Brush color) => new()
        {
            Text = s, Foreground = color, FontWeight = FontWeights.Black, FontFamily = new FontFamily("Segoe UI"), FontSize = size * 0.62,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -size * 0.04, 0, 0),
        };
        Ellipse Disc(Brush fill) => new() { Fill = fill, Stroke = white, StrokeThickness = Math.Max(1.5, size / 18) };

        switch (kind)
        {
            case "ok":
                grid.Children.Add(Disc(Rgb(0x2E, 0xB8, 0x5C)));
                grid.Children.Add(Glyph("M2,7 L6,11 L13,3"));
                break;
            case "no":
                grid.Children.Add(Disc(Rgb(0xE5, 0x39, 0x35)));
                grid.Children.Add(Glyph("M2,2 L12,12 M12,2 L2,12"));
                break;
            case "warn":
                grid.Children.Add(new Path
                {
                    Data = Geometry.Parse("M12,2 L23,21 H1 Z"), Stretch = Stretch.Uniform, Fill = Rgb(0xFF, 0xB3, 0x00), Stroke = white,
                    StrokeThickness = Math.Max(1.5, size / 18), StrokeLineJoin = PenLineJoin.Round,
                });
                var bang = Letter("!", Rgb(0x2A, 0x20, 0x00));
                bang.Margin = new Thickness(0, size * 0.12, 0, 0);
                bang.FontSize = size * 0.55;
                grid.Children.Add(bang);
                break;
            case "star":
                grid.Children.Add(new Path
                {
                    Data = Geometry.Parse("M12,1.5 L15.1,8.3 L22.5,9.2 L17,14.2 L18.5,21.5 L12,17.8 L5.5,21.5 L7,14.2 L1.5,9.2 L8.9,8.3 Z"),
                    Stretch = Stretch.Uniform, Fill = Rgb(0xFF, 0xC8, 0x1E), Stroke = white, StrokeThickness = Math.Max(1.5, size / 18), StrokeLineJoin = PenLineJoin.Round,
                });
                break;
            case "info":
                grid.Children.Add(Disc(Rgb(0x1E, 0x88, 0xE5)));
                grid.Children.Add(Letter("i", white));
                break;
            default:
                grid.Children.Add(Disc(Rgb(0x8E, 0x24, 0xAA)));
                grid.Children.Add(Letter("?", white));
                break;
        }
        return grid;
    }

    /// <summary>
    /// Globo de texto con cola. <paramref name="tip"/> es a dónde apunta y <paramref name="at"/> dónde va el globo.
    /// Si coinciden (clic sin arrastrar) no lleva cola.
    /// </summary>
    public static (Canvas Element, TextBox Text) Callout(Point tip, Point at, Brush brush, double thickness)
    {
        var c = brush is SolidColorBrush sb ? sb.Color : Colors.Red;
        bool light = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 170;
        var fg = light ? Brushes.Black : Brushes.White;
        var canvas = new Canvas { Effect = Shadow() };

        var v = at - tip;
        if (v.Length > 24)
        {
            // Cola: triángulo desde la punta hasta el centro del globo.
            v.Normalize();
            var n = new Vector(-v.Y, v.X) * 11;
            canvas.Children.Add(new Polygon { Points = new PointCollection { tip, at + n, at - n }, Fill = brush });
        }

        var text = new TextBox
        {
            Foreground = fg, CaretBrush = fg, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontSize = 13 + thickness * 1.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"),
            AcceptsReturn = true, MinWidth = 70, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var bubble = new Border
        {
            Background = brush, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 12, 8),
            MinWidth = 96, MinHeight = 40, Child = text,
        };
        // El punto "at" queda dentro del globo aunque el texto crezca hacia la derecha y abajo.
        Canvas.SetLeft(bubble, at.X - 48);
        Canvas.SetTop(bubble, at.Y - 20);
        canvas.Children.Add(bubble);
        return (canvas, text);
    }
}
