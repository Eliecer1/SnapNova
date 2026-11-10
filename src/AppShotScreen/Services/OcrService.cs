using System.IO;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AppShotScreen.Services;

/// <summary>OCR usando el motor que ya trae Windows 10/11 (sin licencias de terceros).</summary>
public static class OcrService
{
    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        var engine = CreateEngine()
            ?? throw new InvalidOperationException("Windows no tiene instalado ningún idioma de OCR. Agrega uno en Configuración > Hora e idioma > Idioma.");

        using var bitmap = await ToSoftwareBitmapAsync(image);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
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
