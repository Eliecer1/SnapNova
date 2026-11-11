using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Services;

/// <summary>Operaciones sobre imágenes: cargar, combinar varias y redimensionar.</summary>
public static class ImageTools
{
    /// <summary>Carga un archivo como BGRA a 96 ppp (1 píxel = 1 unidad), sin dejarlo bloqueado.</summary>
    public static BitmapSource Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.EndInit();
        return ToBgra(bmp);
    }

    public static BitmapSource ToBgra(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[stride * conv.PixelHeight];
        conv.CopyPixels(px, stride, 0);
        var img = BitmapSource.Create(conv.PixelWidth, conv.PixelHeight, 96, 96, PixelFormats.Bgra32, null, px, stride);
        img.Freeze();
        return img;
    }

    /// <summary>Une varias imágenes en una, apiladas (vertical) o en fila (horizontal), sobre fondo blanco.</summary>
    public static BitmapSource Combine(IReadOnlyList<BitmapSource> images, bool vertical, int gap = 16, int margin = 16)
    {
        if (images.Count == 0) throw new ArgumentException("No hay imágenes para combinar.");
        int across = images.Max(i => vertical ? i.PixelWidth : i.PixelHeight);
        int along = images.Sum(i => vertical ? i.PixelHeight : i.PixelWidth) + gap * (images.Count - 1);
        int w = (vertical ? across : along) + margin * 2, h = (vertical ? along : across) + margin * 2;

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            int pos = margin;
            foreach (var img in images)
            {
                // Coordenadas enteras: cada captura se copia píxel a píxel, centrada en el otro eje.
                int x = vertical ? margin + (across - img.PixelWidth) / 2 : pos;
                int y = vertical ? pos : margin + (across - img.PixelHeight) / 2;
                ctx.DrawImage(img, new Rect(x, y, img.PixelWidth, img.PixelHeight));
                pos += (vertical ? img.PixelHeight : img.PixelWidth) + gap;
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return ToBgra(rtb);
    }

    /// <summary>Cambia el tamaño con remuestreo de alta calidad.</summary>
    public static BitmapSource Resize(BitmapSource img, int width, int height)
    {
        width = Math.Clamp(width, 1, 20000);
        height = Math.Clamp(height, 1, 20000);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.Fant);
        using (var ctx = dv.RenderOpen()) ctx.DrawImage(img, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return ToBgra(rtb);
    }
}
