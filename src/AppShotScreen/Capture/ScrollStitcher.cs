using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Capture;

/// <summary>
/// Une fotogramas sucesivos de una zona que se va desplazando hacia abajo.
/// Cada fila de píxeles se resume en una "firma" (promedios de gris por tramos), y se busca
/// el desplazamiento en el que las firmas coinciden. La comparación es tolerante: los navegadores
/// no siempre desplazan píxel exacto (suavizado, zoom, efectos al pasar el ratón), así que basta
/// con que la gran mayoría de filas se parezcan. También detecta encabezados y pies fijos.
/// </summary>
public sealed class ScrollStitcher
{
    // Umbrales en escala de gris 0–255 (promedio por tramo)
    private const float SameRow = 1.5f;      // misma fila (para detectar zonas fijas / sin movimiento)
    private const double AcceptCost = 2.5;   // distancia media máxima para aceptar un desplazamiento

    private readonly int _width, _height, _stride, _bins, _binPx;
    private readonly List<byte[]> _bodyRows = new();
    private byte[][] _headerRows = Array.Empty<byte[]>();
    private Frame _last;
    private int _header = -1, _footer = -1;

    private sealed record Frame(byte[][] Rows, float[][] Sig, float[] Mean, bool[] Informative);

    public int LastShift { get; private set; }
    public int StitchedHeight => _header < 0 ? _height : _headerRows.Length + _bodyRows.Count + Math.Max(0, _footer);

    public ScrollStitcher(BitmapSource first)
    {
        _width = first.PixelWidth;
        _height = first.PixelHeight;
        _stride = _width * 4;
        // La barra de desplazamiento de la derecha cambia en cada paso: no la usamos para comparar.
        int ignoreRight = _width > 120 ? Math.Max(24, _width / 40) : 0;
        int usable = Math.Max(8, _width - ignoreRight);
        _bins = Math.Clamp(usable / 4, 16, 256);
        _binPx = Math.Max(1, usable / _bins);
        _last = Analyze(first);
    }

    public enum AddResult { Appended, NoMovement, NoMatch }

    /// <param name="expectedShift">Desplazamiento esperado en píxeles (0 = desconocido), para desempatar contenido repetitivo.</param>
    public AddResult Add(BitmapSource bitmap, int expectedShift = 0)
    {
        var cur = Analyze(bitmap);

        // ¿Cambió algo? Contamos filas iguales en la misma posición.
        // Se mira sobre las filas con contenido: si casi todas siguen en su sitio, no hubo desplazamiento
        // (solo un cambio local, como el resaltado al pasar el ratón o una animación pequeña).
        int same = 0, informativeRows = 0;
        for (int y = 0; y < _height; y++)
        {
            bool isSame = Dist(cur.Sig[y], _last.Sig[y]) < SameRow;
            if (cur.Informative[y] || _last.Informative[y])
            {
                informativeRows++;
                if (isSame) same++;
            }
        }
        if (informativeRows == 0 || same >= informativeRows * 0.85) return AddResult.NoMovement;

        // Filas fijas arriba y abajo.
        int top = 0;
        while (top < _height && Dist(cur.Sig[top], _last.Sig[top]) < SameRow) top++;
        int bottom = 0;
        while (bottom < _height - top && Dist(cur.Sig[_height - 1 - bottom], _last.Sig[_height - 1 - bottom]) < SameRow) bottom++;

        int header = _header < 0 ? top : Math.Min(_header, top);
        int footer = _footer < 0 ? bottom : Math.Min(_footer, bottom);
        if (header + footer > _height * 0.6) { header = Math.Min(header, _height / 4); footer = Math.Min(footer, _height / 4); }

        int n = _height - header - footer;
        if (n < 24) return AddResult.NoMatch;
        int shift = FindShift(_last, cur, header, n, expectedShift);
        if (shift <= 0) return AddResult.NoMatch;

        if (_header < 0)
        {
            _headerRows = _last.Rows.Take(header).ToArray();
            for (int i = header; i < header + n; i++) _bodyRows.Add(_last.Rows[i]);
        }
        _header = header;
        _footer = footer;

        for (int i = header + n - shift; i < header + n; i++) _bodyRows.Add(cur.Rows[i]);

        _last = cur;
        LastShift = shift;
        return AddResult.Appended;
    }

    /// <summary>Busca d tal que las filas actuales [0..n-d) coinciden con las anteriores [d..n).</summary>
    private int FindShift(Frame prev, Frame cur, int offset, int n, int expected)
    {
        int minOverlap = Math.Max(16, n / 8);
        int maxD = n - minOverlap;

        // Comparamos cur[i] con prev[i + d]. d > 0: el contenido subió (el usuario bajó).
        // También probamos d <= 0 (el usuario subió o no se movió) para no confundirlo con un avance.

        // 1) Barrido rápido con el brillo medio de cada fila para elegir candidatos.
        var costs = new List<(int d, double cost)>(2 * maxD + 1);
        for (int d = -maxD; d <= maxD; d++)
        {
            int from = Math.Max(0, -d), to = Math.Min(n, n - d);
            int overlap = to - from;
            int step = overlap > 400 ? 2 : 1;
            double sum = 0;
            int count = 0;
            for (int i = from; i < to; i += step)
            {
                sum += Math.Abs(cur.Mean[offset + i] - prev.Mean[offset + i + d]);
                count++;
            }
            costs.Add((d, sum / Math.Max(1, count)));
        }
        var candidates = costs.OrderBy(c => c.cost).Take(24).Select(c => c.d).ToList();
        if (expected > 0 && expected <= maxD) candidates.Add(expected);

        // 2) Verificación detallada: distancia media recortada (ignoramos el 15 % de filas más distintas,
        //    que suelen ser el resaltado del ratón o un detalle animado).
        int bestD = int.MinValue;
        double bestCost = double.MaxValue, bestDistance = double.MaxValue;
        var dists = new List<float>(n);
        foreach (int d in candidates.Distinct())
        {
            int from = Math.Max(0, -d), to = Math.Min(n, n - d);
            dists.Clear();
            for (int i = from; i < to; i++)
            {
                int a = offset + i, b = offset + i + d;
                if (!cur.Informative[a] && !prev.Informative[b]) continue;
                dists.Add(Dist(cur.Sig[a], prev.Sig[b]));
            }
            if (dists.Count < 10) continue;
            dists.Sort();
            int keep = Math.Max(1, (int)(dists.Count * 0.85));
            double cost = 0;
            for (int k = 0; k < keep; k++) cost += dists[k];
            cost /= keep;

            double distance = expected > 0 ? Math.Abs(d - expected) : Math.Abs(d);
            if (cost < bestCost - 0.05 || (Math.Abs(cost - bestCost) <= 0.05 && distance < bestDistance))
            {
                bestCost = cost;
                bestD = d;
                bestDistance = distance;
            }
        }
        // Solo aceptamos avances (d > 0) con una coincidencia clara.
        return bestD > 0 && bestCost < AcceptCost ? bestD : -1;
    }

    public BitmapSource Result()
    {
        var footerRows = _footer > 0 ? _last.Rows.Skip(_height - _footer).ToArray() : Array.Empty<byte[]>();
        var all = _header < 0 ? _last.Rows : _headerRows.Concat(_bodyRows).Concat(footerRows).ToArray();

        var pixels = new byte[_stride * all.Length];
        for (int i = 0; i < all.Length; i++) Buffer.BlockCopy(all[i], 0, pixels, i * _stride, _stride);
        var bmp = BitmapSource.Create(_width, all.Length, 96, 96, PixelFormats.Bgra32, null, pixels, _stride);
        bmp.Freeze();
        return bmp;
    }

    private static float Dist(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    private Frame Analyze(BitmapSource frame)
    {
        if (frame.PixelWidth != _width || frame.PixelHeight != _height)
            throw new ArgumentException("Todos los fotogramas deben tener el mismo tamaño.");
        var src = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var all = new byte[_stride * _height];
        src.CopyPixels(all, _stride, 0);

        var rows = new byte[_height][];
        var sig = new float[_height][];
        var mean = new float[_height];
        var informative = new bool[_height];
        for (int y = 0; y < _height; y++)
        {
            var row = new byte[_stride];
            Buffer.BlockCopy(all, y * _stride, row, 0, _stride);
            rows[y] = row;

            var s = new float[_bins];
            float total = 0, min = 255, max = 0;
            for (int bIdx = 0; bIdx < _bins; bIdx++)
            {
                int start = bIdx * _binPx * 4;
                float acc = 0;
                for (int x = 0; x < _binPx; x++)
                {
                    int i = start + x * 4;
                    acc += row[i] * 0.114f + row[i + 1] * 0.587f + row[i + 2] * 0.299f;
                }
                float v = acc / _binPx;
                s[bIdx] = v;
                total += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            sig[y] = s;
            mean[y] = total / _bins;
            informative[y] = max - min > 6; // filas lisas (fondo) no ayudan a alinear
        }
        return new Frame(rows, sig, mean, informative);
    }
}
