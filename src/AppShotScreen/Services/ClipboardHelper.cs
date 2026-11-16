using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;


namespace AppShotScreen.Services;

/// <summary>
/// Copia al portapapeles reintentando: otras apps (AnyViewer y otros escritorios remotos que sincronizan
/// el portapapeles, gestores de portapapeles, otras herramientas de captura) lo abren un instante y
/// Windows devuelve CLIPBRD_E_CANT_OPEN (0x800401D0).
/// </summary>
public static class ClipboardHelper
{
    /// <summary>Tiempo máximo que esperamos a que se libere el portapapeles.</summary>
    public static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Versión que no congela la ventana mientras espera.</summary>
    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryOnceText(text)) return true;
            if (clock.Elapsed >= Budget) return false;
            await Task.Delay(100);
        }
    }

    /// <returns>true si se copió; false si el portapapeles siguió ocupado todo el tiempo de espera.</returns>
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryOnceText(text)) return true;
            if (clock.Elapsed >= Budget) return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>Para datos con varios formatos (imagen + PNG).</summary>
    public static bool TrySetData(System.Windows.DataObject data)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Ojo: este método de WPF ya reintenta por dentro (≈1 s); por eso el bucle se limita por tiempo.
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException) { }
            if (clock.Elapsed >= Budget) return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Un solo intento rápido, con la API básica de Windows (sin OLE). OLE abre el portapapeles dos veces
    /// (al poner los datos y al "fijarlos"), así que con AnyViewer cerca falla el doble; y sus reintentos
    /// internos tardan ~1 s por llamada.
    /// </summary>
    private static bool TryOnceText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            int bytes = (text.Length + 1) * 2;
            IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (mem == IntPtr.Zero) return false;
            IntPtr ptr = GlobalLock(mem);
            if (ptr == IntPtr.Zero) { GlobalFree(mem); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { GlobalUnlock(mem); }
            if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
            {
                GlobalFree(mem); // si Windows no lo aceptó, la memoria sigue siendo nuestra
                return false;
            }
            return true; // a partir de aquí la memoria es del portapapeles
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Copia una imagen como mapa de bits (CF_DIB, lo entienden todas las apps) y como "PNG"
    /// (conserva la transparencia en Word, navegadores, etc.). Misma API básica de Windows que el texto.
    /// </summary>
    public static async Task<bool> TrySetImageAsync(System.Windows.Media.Imaging.BitmapSource image)
    {
        var (dib, png) = EncodeImage(image);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryOnceImage(dib, png)) return true;
            if (clock.Elapsed >= Budget) return false;
            await Task.Delay(100);
        }
    }

    public static bool TrySetImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        var (dib, png) = EncodeImage(image);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryOnceImage(dib, png)) return true;
            if (clock.Elapsed >= Budget) return false;
            Thread.Sleep(100);
        }
    }

    private static (byte[] Dib, byte[] Png) EncodeImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        // DIB de 32 bits de abajo arriba: cabecera de 40 bytes + filas invertidas.
        var dib = new byte[40 + pixels.Length];
        BitConverter.GetBytes(40).CopyTo(dib, 0);          // biSize
        BitConverter.GetBytes(w).CopyTo(dib, 4);           // biWidth
        BitConverter.GetBytes(h).CopyTo(dib, 8);           // biHeight (positivo = de abajo arriba)
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);   // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);  // biBitCount
        BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20); // biSizeImage (BI_RGB = 0 en 16)
        for (int y = 0; y < h; y++) Buffer.BlockCopy(pixels, y * stride, dib, 40 + (h - 1 - y) * stride, stride);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return (dib, ms.ToArray());
    }

    private static bool TryOnceImage(byte[] dib, byte[] png)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            if (!PutBytes(CF_DIB, dib)) return false;
            uint pngFormat = RegisterClipboardFormat("PNG");
            if (pngFormat != 0) PutBytes(pngFormat, png); // extra: si falla, queda al menos el mapa de bits
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Copia bytes a memoria global y la entrega al portapapeles (ya abierto).</summary>
    private static bool PutBytes(uint format, byte[] data)
    {
        IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (mem == IntPtr.Zero) return false;
        IntPtr ptr = GlobalLock(mem);
        if (ptr == IntPtr.Zero) { GlobalFree(mem); return false; }
        try { Marshal.Copy(data, 0, ptr, data.Length); }
        finally { GlobalUnlock(mem); }
        if (SetClipboardData(format, mem) == IntPtr.Zero) { GlobalFree(mem); return false; }
        return true;
    }

    private const uint CF_DIB = 8;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
}
