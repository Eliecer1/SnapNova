using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AppShotScreen.Services;

public sealed record OcrWord(string Text, Rect Bounds);
public sealed record OcrLine(string Text, List<OcrWord> Words);

/// <summary>OCR usando el motor que ya trae Windows 10/11 (sin licencias de terceros).</summary>
public static class OcrService
{
    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        var lines = await RecognizeLinesAsync(image);
        return string.Join(Environment.NewLine, lines.Select(l => l.Text));
    }

    /// <summary>Devuelve líneas y palabras con su posición en píxeles de la imagen original.</summary>
    public static async Task<List<OcrLine>> RecognizeLinesAsync(BitmapSource image)
    {
        var engine = CreateEngine()
            ?? throw new InvalidOperationException("Windows no tiene instalado ningún idioma de OCR. Agrega uno en Configuración > Hora e idioma > Idioma.");

        int max = (int)OcrEngine.MaxImageDimension;
        // El texto pequeño de pantalla se reconoce mejor ampliado al doble.
        int scale = image.PixelWidth * 2 <= max && image.PixelHeight <= 2000 ? 2 : 1;
        int tileH = Math.Min(image.PixelHeight, max / scale);
        const int overlap = 60;
        var result = new List<OcrLine>();

        for (int y = 0; y < image.PixelHeight; y += tileH - (tileH < image.PixelHeight ? overlap : 0))
        {
            int h = Math.Min(tileH, image.PixelHeight - y);
            int w = Math.Min(image.PixelWidth, max / scale);
            BitmapSource tile = h == image.PixelHeight && w == image.PixelWidth
                ? image
                : new CroppedBitmap(image, new Int32Rect(0, y, w, h));
            if (scale != 1) tile = new TransformedBitmap(tile, new ScaleTransform(scale, scale));

            using var bitmap = await ToSoftwareBitmapAsync(tile);
            var ocr = await engine.RecognizeAsync(bitmap);
            double minTop = result.Count > 0 && y > 0 ? y + overlap / 2.0 : 0; // evita duplicar la franja compartida
            foreach (var line in ocr.Lines)
            {
                var words = line.Words.Select(wd => new OcrWord(wd.Text, new Rect(
                    wd.BoundingRect.X / scale, wd.BoundingRect.Y / scale + y,
                    wd.BoundingRect.Width / scale, wd.BoundingRect.Height / scale))).ToList();
                if (words.Count == 0 || words[0].Bounds.Top < minTop) continue;
                result.Add(new OcrLine(line.Text, words));
            }
            if (y + h >= image.PixelHeight) break;
        }
        return result;
    }

    private static OcrEngine? CreateEngine()
    {
        // Preferimos español; si no está, el idioma del perfil del usuario.
        foreach (var tag in new[] { "es-ES", "es-MX", "es" })
        {
            var lang = new Language(tag);
            if (OcrEngine.IsLanguageSupported(lang)) return OcrEngine.TryCreateFromLanguage(lang);
        }
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(BitmapSource image)
    {
        var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        enc.Save(ms);
        ms.Position = 0;

        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
