using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace AppShotScreen.Capture;

/// <summary>Rectángulo del escritorio virtual (todos los monitores) en píxeles físicos.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class ScreenCapture
{
    public static PixelRect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    /// <summary>Captura un rectángulo de la pantalla (píxeles físicos) como imagen WPF congelada.</summary>
    public static BitmapSource Capture(PixelRect r)
    {
        using var bmp = new Drawing.Bitmap(r.Width, r.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Drawing.Size(r.Width, r.Height), Drawing.CopyPixelOperation.SourceCopy);
        }
        return ToBitmapSource(bmp);
    }

    public static BitmapSource CaptureVirtualScreen() => Capture(VirtualScreen);

    /// <summary>Pantalla del monitor donde está el cursor.</summary>
    public static BitmapSource CaptureCurrentMonitor()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var b = screen.Bounds;
        return Capture(new PixelRect(b.X, b.Y, b.Width, b.Height));
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Normalizamos a 96 DPI para que 1 píxel = 1 unidad WPF en el editor.
            var normalized = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = normalized.PixelWidth * 4;
            var pixels = new byte[stride * normalized.PixelHeight];
            normalized.CopyPixels(pixels, stride, 0);
            var result = BitmapSource.Create(normalized.PixelWidth, normalized.PixelHeight, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            Native.DeleteObject(h);
        }
    }

    /// <summary>Recorta una imagen (coordenadas en píxeles de la propia imagen).</summary>
    public static BitmapSource Crop(BitmapSource src, Int32Rect rect)
    {
        var c = new CroppedBitmap(src, rect);
        int stride = rect.Width * 4;
        var pixels = new byte[stride * rect.Height];
        c.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(rect.Width, rect.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
