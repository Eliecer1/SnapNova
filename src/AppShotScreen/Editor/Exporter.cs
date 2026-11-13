using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Editor;

/// <summary>Cómo se "presenta" la captura al exportar: fondo degradado, margen, esquinas, sombra y borde.</summary>
public sealed record PresentationStyle
{
    /// <summary>Índice en <see cref="Backgrounds"/>; -1 = sin fondo.</summary>
    public int Background { get; init; } = -1;
    public int Padding { get; init; } = 64;
    public int Radius { get; init; }
    public bool Shadow { get; init; }
    public bool Border { get; init; }
    public Brush BorderBrush { get; init; } = Brushes.Black;
    public int BorderWidth { get; init; } = 2;

    public static readonly (string Name, Color A, Color B)[] Backgrounds =
    {
        ("Océano", Color.FromRgb(0x4A, 0x8B, 0xFF), Color.FromRgb(0x9B, 0x5C, 0xFF)),
        ("Atardecer", Color.FromRgb(0xFF, 0x6B, 0x6B), Color.FromRgb(0xFF, 0xC1, 0x4D)),
        ("Menta", Color.FromRgb(0x2B, 0xD9, 0xA8), Color.FromRgb(0x1F, 0xA2, 0xFF)),
        ("Rosa", Color.FromRgb(0xFF, 0x5E, 0x9C), Color.FromRgb(0xFF, 0x9A, 0x62)),
        ("Lavanda", Color.FromRgb(0xA1, 0x8C, 0xD1), Color.FromRgb(0xFB, 0xC2, 0xEB)),
        ("Bosque", Color.FromRgb(0x13, 0x4E, 0x5E), Color.FromRgb(0x71, 0xB2, 0x80)),
        ("Noche", Color.FromRgb(0x1B, 0x1E, 0x2E), Color.FromRgb(0x3F, 0x47, 0x6E)),
        ("Nube", Color.FromRgb(0xF4, 0xF6, 0xFA), Color.FromRgb(0xDD, 0xE3, 0xEE)),
    };

    public static Brush? BackgroundBrush(int index)
    {
        if (index < 0 || index >= Backgrounds.Length) return null;
        var (_, a, b) = Backgrounds[index];
        var brush = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Margen real alrededor de la imagen (si solo hay sombra, deja sitio para que se vea).</summary>
    public int EffectivePadding => Background >= 0 ? Padding : Shadow ? 32 : 0;
    public int EffectiveBorder => Border ? Math.Max(1, BorderWidth) : 0;
    public bool IsPlain => Background < 0 && !Shadow && !Border && Radius == 0;
}

/// <summary>Convierte el lienzo en imagen, aplica la presentación y la guarda o copia.</summary>
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

    /// <summary>
    /// Compone la imagen final. Todo va en coordenadas enteras: la captura se copia píxel a píxel,
    /// sin re-muestrear (solo se recortan las esquinas si hay radio).
    /// </summary>
    public static BitmapSource Compose(BitmapSource img, PresentationStyle s)
    {
        if (s.IsPlain) return img;

        int pad = s.EffectivePadding, bw = s.EffectiveBorder;
        int boxW = img.PixelWidth + bw * 2, boxH = img.PixelHeight + bw * 2;
        int totalW = boxW + pad * 2, totalH = boxH + pad * 2;
        var box = new Rect(pad, pad, boxW, boxH);
        var imgRect = new Rect(pad + bw, pad + bw, img.PixelWidth, img.PixelHeight);
        double r = Math.Min(s.Radius, Math.Min(img.PixelWidth, img.PixelHeight) / 2.0);

        BitmapSource? shadowLayer = null;
        if (s.Shadow)
        {
            // La sombra se dibuja aparte (desenfocada) y debajo; nunca toca los píxeles de la imagen.
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = boxW, Height = boxH, RadiusX = r + bw, RadiusY = r + bw,
                Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                Effect = new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian },
            };
            var canvas = new Canvas { Width = totalW, Height = totalH };
            Canvas.SetLeft(rect, pad);
            Canvas.SetTop(rect, pad + 10);
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
            var bg = PresentationStyle.BackgroundBrush(s.Background);
            if (bg != null) ctx.DrawRectangle(bg, null, new Rect(0, 0, totalW, totalH));
            if (shadowLayer != null) ctx.DrawImage(shadowLayer, new Rect(0, 0, totalW, totalH));
            if (bw > 0) ctx.DrawRoundedRectangle(s.BorderBrush, null, box, r + bw, r + bw);
            if (r > 0) ctx.PushClip(new RectangleGeometry(imgRect, r, r));
            ctx.DrawImage(img, imgRect);
            if (r > 0) ctx.Pop();
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
        // Otra aplicación puede tener el portapapeles abierto un instante: reintentamos unos segundos.
        if (!Services.ClipboardHelper.TrySetData(data))
            throw new System.Runtime.InteropServices.COMException("El portapapeles está ocupado por otra aplicación.", unchecked((int)0x800401D0));
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
