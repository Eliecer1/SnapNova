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

        // Todo en coordenadas enteras: la imagen se copia píxel a píxel, sin re-muestrear (sin desenfoque).
        int bw = border ? Math.Max(1, borderWidth) : 0;
        int padLT = shadow ? 14 : 0, padRB = shadow ? 26 : 0;
        int boxW = img.PixelWidth + bw * 2, boxH = img.PixelHeight + bw * 2;
        int totalW = padLT + boxW + padRB, totalH = padLT + boxH + padRB;
        var box = new Rect(padLT, padLT, boxW, boxH);

        BitmapSource? shadowLayer = null;
        if (shadow)
        {
            // La sombra se dibuja aparte (desenfocada) y debajo; nunca toca los píxeles de la imagen.
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = boxW, Height = boxH, Fill = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                Effect = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian },
            };
            var canvas = new Canvas { Width = totalW, Height = totalH };
            Canvas.SetLeft(rect, padLT + 6);
            Canvas.SetTop(rect, padLT + 6);
            canvas.Children.Add(rect);
            canvas.Measure(new Size(totalW, totalH));
            canvas.Arrange(new Rect(0, 0, totalW, totalH));
            var srtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
            srtb.Render(canvas);
            srtb.Freeze();
            shadowLayer = srtb;
        }

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            if (shadowLayer != null) ctx.DrawImage(shadowLayer, new Rect(0, 0, totalW, totalH));
            if (border) ctx.DrawRectangle(borderBrush, null, box);
            ctx.DrawImage(img, new Rect(padLT + bw, padLT + bw, img.PixelWidth, img.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
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
