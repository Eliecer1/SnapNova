using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Editor;

/// <summary>Convierte el lienzo en imagen, aplica efectos y la guarda o copia.</summary>
public static class Exporter
{
    /// <summary>Dibuja el elemento a su tamaño en píxeles (1 unidad = 1 píxel), sin zoom.</summary>
    public static BitmapSource Render(FrameworkElement element)
    {
        int w = (int)Math.Ceiling(element.Width), h = (int)Math.Ceiling(element.Height);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Efectos de salida, inspirados en los "efectos de captura": borde y sombra.</summary>
    public static BitmapSource ApplyEffects(BitmapSource img, bool shadow, bool border, Brush borderBrush, int borderWidth)
    {
        if (!shadow && !border) return img;

        var image = new Image { Source = img, Width = img.PixelWidth, Height = img.PixelHeight, Stretch = Stretch.Fill };
        FrameworkElement content = image;
        if (border)
        {
            content = new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(borderWidth),
                Child = image,
            };
        }

        double margin = shadow ? 24 : 0;
        var host = new Grid { Background = shadow ? Brushes.Transparent : null };
        content.Margin = new Thickness(margin * 0.6, margin * 0.6, margin, margin);
        if (shadow) content.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 315, Opacity = 0.5, Color = Colors.Black };
        host.Children.Add(content);

        double totalW = img.PixelWidth + (border ? borderWidth * 2 : 0) + margin * 1.6;
        double totalH = img.PixelHeight + (border ? borderWidth * 2 : 0) + margin * 1.6;
        host.Measure(new Size(totalW, totalH));
        host.Arrange(new Rect(0, 0, totalW, totalH));
        host.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(totalW), (int)Math.Ceiling(totalH), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }

    public static void CopyToClipboard(BitmapSource img)
    {
        // Algunos programas pierden la transparencia; copiamos también como PNG.
        var data = new DataObject();
        data.SetImage(img);
        var png = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        enc.Save(png);
        data.SetData("PNG", png);
        Clipboard.SetDataObject(data, true);
    }

    public static void SavePng(BitmapSource img, string path) => Save(img, path);

    public static void Save(BitmapSource img, string path)
    {
        BitmapEncoder enc = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        BitmapSource frame = enc is PngBitmapEncoder ? img : Flatten(img);
        enc.Frames.Add(BitmapFrame.Create(frame));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>JPG/BMP no tienen transparencia: ponemos fondo blanco.</summary>
    private static BitmapSource Flatten(BitmapSource img)
    {
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            ctx.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
    }
}
