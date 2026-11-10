using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Capture;

/// <summary>
/// Une fotogramas sucesivos de una zona que se va desplazando hacia abajo.
/// Compara las filas de píxeles (por hash) para encontrar cuánto se movió el contenido,
/// y detecta encabezados y pies fijos (barras que no se mueven) para no repetirlos.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _hashBytes;
    private readonly List<byte[]> _bodyRows = new();
    private byte[][] _headerRows = Array.Empty<byte[]>();
    private byte[][] _lastRows;
    private ulong[] _lastHashes;
    private bool[] _lastUniform;
    private int _header = -1, _footer = -1;

    public int LastShift { get; private set; }
    public int StitchedHeight => _headerRows.Length + _bodyRows.Count + Math.Max(0, _footer);

    public ScrollStitcher(BitmapSource first)
    {
        _width = first.PixelWidth;
        _height = first.PixelHeight;
        _stride = _width * 4;
        // La barra de desplazamiento de la derecha cambia en cada paso (el "pulgar" se mueve),
        // así que la ignoramos al comparar filas. Se conserva en la imagen final.
        int ignoreRight = _width > 120 ? Math.Max(24, _width / 40) : 0;
        _hashBytes = (_width - ignoreRight) * 4;
        (_lastRows, _lastHashes, _lastUniform) = Split(first);
    }

    public enum AddResult { Appended, NoMovement, NoMatch }

    /// <param name="expectedShift">Desplazamiento esperado en píxeles (0 = desconocido), para desempatar contenido repetitivo.</param>
    public AddResult Add(BitmapSource frame, int expectedShift = 0)
    {
        var (rows, hashes, uniform) = Split(frame);

        // Filas fijas arriba y abajo (iguales en la misma posición).
        int top = 0;
        while (top < _height && hashes[top] == _lastHashes[top]) top++;
        if (top == _height) return AddResult.NoMovement;
        int bottom = 0;
        while (bottom < _height - top && hashes[_height - 1 - bottom] == _lastHashes[_height - 1 - bottom]) bottom++;

        // Las primeras dos comparaciones fijan el encabezado/pie; luego solo pueden encogerse.
        int header = _header < 0 ? top : Math.Min(_header, top);
        int footer = _footer < 0 ? bottom : Math.Min(_footer, bottom);
        // Evitamos que un encabezado "falso" (zona en blanco) se coma casi todo.
        if (header + footer > _height * 0.6) { header = Math.Min(header, _height / 4); footer = Math.Min(footer, _height / 4); }

        int n = _height - header - footer;
        int shift = FindShift(_lastHashes, _lastUniform, hashes, uniform, header, n, expectedShift);
        if (shift <= 0) return AddResult.NoMatch;

        if (_header < 0)
        {
            // Primer emparejamiento: guardamos encabezado y el cuerpo completo del primer fotograma.
            _headerRows = _lastRows.Take(header).ToArray();
            for (int i = header; i < header + n; i++) _bodyRows.Add(_lastRows[i]);
        }
        _header = header;
        _footer = footer;

        // Agregamos las filas nuevas que aparecieron abajo del cuerpo.
        for (int i = header + n - shift; i < header + n; i++) _bodyRows.Add(rows[i]);

        _lastRows = rows;
        _lastHashes = hashes;
        _lastUniform = uniform;
        LastShift = shift;
        return AddResult.Appended;
    }

    /// <summary>Busca d tal que las filas del cuerpo actual [0..n-d) coinciden con las anteriores [d..n).</summary>
    internal static int FindShift(ulong[] prev, bool[] prevUniform, ulong[] cur, bool[] curUniform, int offset, int n, int expected)
    {
        int bestD = -1;
        double bestScore = 0;
        double bestDistance = double.MaxValue;
        int minOverlap = Math.Max(12, n / 10);

        for (int d = 1; d <= n - minOverlap; d++)
        {
            int overlap = n - d;
            int informative = 0, matches = 0, mismatches = 0;
            int allowedMismatches = Math.Max(1, overlap / 50);
            for (int i = 0; i < overlap; i++)
            {
                ulong a = cur[offset + i], b = prev[offset + i + d];
                bool informativeRow = !(curUniform[offset + i] && prevUniform[offset + i + d]);
                if (a == b)
                {
                    if (informativeRow) { informative++; matches++; }
                }
                else
                {
                    informative++;
                    if (++mismatches > allowedMismatches) break;
                }
            }
            if (mismatches > allowedMismatches || informative < 6) continue;

            double score = (double)matches / informative;
            double distance = expected > 0 ? Math.Abs(d - expected) : d;
            // Mejor puntuación; ante empate, el más cercano a lo esperado (o el menor desplazamiento).
            if (score > bestScore + 0.005 || (Math.Abs(score - bestScore) <= 0.005 && distance < bestDistance))
            {
                bestScore = score;
                bestD = d;
                bestDistance = distance;
            }
        }
        return bestScore >= 0.97 ? bestD : -1;
    }

    public BitmapSource Result()
    {
        var footerRows = _footer > 0 ? _lastRows.Skip(_height - _footer).ToArray() : Array.Empty<byte[]>();
        var all = _header < 0
            ? _lastRows // nunca se movió: devolvemos el único fotograma
            : _headerRows.Concat(_bodyRows).Concat(footerRows).ToArray();

        var pixels = new byte[_stride * all.Length];
        for (int i = 0; i < all.Length; i++) Buffer.BlockCopy(all[i], 0, pixels, i * _stride, _stride);
        var bmp = BitmapSource.Create(_width, all.Length, 96, 96, PixelFormats.Bgra32, null, pixels, _stride);
        bmp.Freeze();
        return bmp;
    }

    private (byte[][] rows, ulong[] hashes, bool[] uniform) Split(BitmapSource frame)
    {
        if (frame.PixelWidth != _width || frame.PixelHeight != _height)
            throw new ArgumentException("Todos los fotogramas deben tener el mismo tamaño.");
        var src = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var all = new byte[_stride * _height];
        src.CopyPixels(all, _stride, 0);

        var rows = new byte[_height][];
        var hashes = new ulong[_height];
        var uniform = new bool[_height];
        for (int y = 0; y < _height; y++)
        {
            var row = new byte[_stride];
            Buffer.BlockCopy(all, y * _stride, row, 0, _stride);
            rows[y] = row;
            (hashes[y], uniform[y]) = HashRow(row, _hashBytes);
        }
        return (rows, hashes, uniform);
    }

    private static (ulong hash, bool uniform) HashRow(byte[] row, int length)
    {
        // FNV-1a de 64 bits sobre los píxeles (ignorando el canal alfa).
        ulong h = 14695981039346656037UL;
        bool uniform = true;
        byte b0 = row[0], g0 = row[1], r0 = row[2];
        for (int i = 0; i < length; i += 4)
        {
            byte b = row[i], g = row[i + 1], r = row[i + 2];
            h = (h ^ b) * 1099511628211UL;
            h = (h ^ g) * 1099511628211UL;
            h = (h ^ r) * 1099511628211UL;
            if (uniform && (b != b0 || g != g0 || r != r0)) uniform = false;
        }
        return (h, uniform);
    }
}
