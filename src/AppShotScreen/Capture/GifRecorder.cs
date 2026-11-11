using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Capture;

/// <summary>
/// Graba una zona de la pantalla como GIF animado (con el puntero del ratón).
/// Los fotogramas iguales al anterior no se guardan: solo alargan su duración,
/// así una pantalla quieta casi no ocupa memoria ni espacio.
/// </summary>
public sealed class GifRecorder
{
    private readonly PixelRect _region;
    private readonly List<(BitmapSource Frame, int Centiseconds)> _frames = new();
    private volatile bool _stop;
    private byte[]? _lastPixels;

    public int Fps { get; init; } = 8;
    public int MaxSeconds { get; init; } = 60;

    /// <summary>Segundos transcurridos; se dispara unas dos veces por segundo.</summary>
    public event Action<double>? Tick;

    public GifRecorder(PixelRect region) => _region = region;

    public int FrameCount => _frames.Count;
    public void Stop() => _stop = true;

    /// <summary>Graba hasta que se llame a <see cref="Stop"/> o se alcance el máximo.</summary>
    public async Task RunAsync()
    {
        var clock = Stopwatch.StartNew();
        long lastFrameAt = 0;
        int interval = 1000 / Fps;
        double lastTick = -1;

        while (!_stop && clock.Elapsed.TotalSeconds < MaxSeconds)
        {
            var shot = ScreenCapture.Capture(_region, includeCursor: true);
            long now = clock.ElapsedMilliseconds;
            int stride = shot.PixelWidth * 4;
            var pixels = new byte[stride * shot.PixelHeight];
            shot.CopyPixels(pixels, stride, 0);

            if (_frames.Count > 0)
            {
                // Cerramos la duración del fotograma anterior.
                var (frame, cs) = _frames[^1];
                _frames[^1] = (frame, cs + (int)Math.Max(1, Math.Round((now - lastFrameAt) / 10.0)));
            }
            if (_lastPixels == null || !pixels.AsSpan().SequenceEqual(_lastPixels))
            {
                // Paleta de 256 colores calculada para este fotograma (mejor que una paleta fija).
                var indexed = await Task.Run(() =>
                {
                    var palette = new BitmapPalette(shot, 256);
                    var conv = new FormatConvertedBitmap(shot, PixelFormats.Indexed8, palette, 0);
                    // Copiamos a un bitmap propio: el convertido no se puede congelar ni pasar entre hilos.
                    var idx = new byte[conv.PixelWidth * conv.PixelHeight];
                    conv.CopyPixels(idx, conv.PixelWidth, 0);
                    var f = BitmapSource.Create(conv.PixelWidth, conv.PixelHeight, 96, 96, PixelFormats.Indexed8, palette, idx, conv.PixelWidth);
                    f.Freeze();
                    return f;
                });
                _frames.Add((indexed, 0));
                _lastPixels = pixels;
            }
            lastFrameAt = now;

            double secs = Math.Floor(clock.Elapsed.TotalSeconds * 2) / 2;
            if (secs != lastTick) { lastTick = secs; Tick?.Invoke(clock.Elapsed.TotalSeconds); }

            int wait = interval - (int)(clock.ElapsedMilliseconds - now);
            await Task.Delay(Math.Max(5, wait));
        }
        if (_frames.Count > 0)
        {
            var (frame, cs) = _frames[^1];
            _frames[^1] = (frame, Math.Max(cs, 100 / Fps) + 80); // pausa al final antes de repetir
        }
    }

    /// <summary>Escribe el GIF (se repite en bucle).</summary>
    public Task SaveAsync(string path) => Task.Run(() =>
    {
        var encoder = new GifBitmapEncoder();
        foreach (var (frame, _) in _frames) encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var data = ms.ToArray();

        // El codificador de WPF no escribe ni el bucle ni la duración de cada fotograma.
        // Recorremos los bloques del GIF y los añadimos nosotros.
        byte[] loop = { 0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 0x03, 0x01, 0x00, 0x00, 0x00 };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);

        int pos = 13 + ((data[10] & 0x80) != 0 ? 3 * (1 << ((data[10] & 0x07) + 1)) : 0);
        fs.Write(data, 0, pos);
        fs.Write(loop, 0, loop.Length);

        int SkipSubBlocks(int p)
        {
            while (p < data.Length && data[p] != 0) p += data[p] + 1;
            return p + 1;
        }

        int frameIndex = 0;
        while (pos < data.Length)
        {
            int start = pos;
            byte block = data[pos];
            if (block == 0x2C)
            {
                // Descriptor de imagen: antes va nuestra extensión de control con la duración.
                int cs = Math.Clamp(frameIndex < _frames.Count ? _frames[frameIndex].Centiseconds : 10, 2, 6000);
                fs.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(cs & 0xFF), (byte)(cs >> 8), 0x00, 0x00 });
                frameIndex++;
                byte packed = data[pos + 9];
                pos += 10;
                if ((packed & 0x80) != 0) pos += 3 * (1 << ((packed & 0x07) + 1));
                pos = SkipSubBlocks(pos + 1); // +1: tamaño mínimo de código LZW
            }
            else if (block == 0x21)
            {
                bool isControl = data[pos + 1] == 0xF9;
                pos = SkipSubBlocks(pos + 2);
                if (isControl) continue; // no duplicar si algún día WPF la escribe
            }
            else
            {
                // Fin (0x3B) o algo inesperado: copiamos el resto tal cual.
                pos = data.Length;
            }
            fs.Write(data, start, Math.Min(pos, data.Length) - start);
        }
    });
}
