using System.Text.RegularExpressions;
using System.Windows;

namespace AppShotScreen.Services;

/// <summary>Encuentra datos sensibles (correos, teléfonos, tarjetas, IPs, números de identificación) en el texto reconocido.</summary>
public static class SensitiveData
{
    public sealed record Hit(string Kind, Rect Bounds);

    private static readonly (string Kind, Regex Rx)[] Patterns =
    {
        ("correo", new Regex(@"[\w.+-]+\s?@\s?[\w-]+(\s?\.\s?[A-Za-z]{2,})+", RegexOptions.Compiled)),
        ("IP", new Regex(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled)),
        // Teléfonos, tarjetas, cuentas, cédulas: secuencias con al menos 8 dígitos
        ("número", new Regex(@"\+?\(?\d[\d\s().\-]{6,}\d[A-Za-z]?", RegexOptions.Compiled)),
    };

    private static readonly Regex DateLike = new(@"^\d{1,4}[-.]\d{1,2}[-.]\d{1,4}$", RegexOptions.Compiled);

    public static List<Hit> Find(IEnumerable<OcrLine> lines)
    {
        var hits = new List<Hit>();
        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            // Texto de la línea reconstruido palabra a palabra, recordando qué caracteres son de cada palabra.
            var spans = new List<(int Start, int End, Rect Bounds)>();
            var sb = new System.Text.StringBuilder();
            foreach (var w in line.Words)
            {
                if (sb.Length > 0) sb.Append(' ');
                spans.Add((sb.Length, sb.Length + w.Text.Length, w.Bounds));
                sb.Append(w.Text);
            }
            string text = sb.ToString();

            foreach (var (kind, rx) in Patterns)
            {
                foreach (Match m in rx.Matches(text))
                {
                    string value = m.Value.Trim();
                    if (kind == "número")
                    {
                        if (value.Count(char.IsDigit) < 8 || DateLike.IsMatch(value)) continue;
                    }
                    Rect union = Rect.Empty;
                    foreach (var s in spans)
                        if (s.Start < m.Index + m.Length && s.End > m.Index) union.Union(s.Bounds);
                    if (union.IsEmpty) continue;
                    union.Inflate(3, 3);
                    // Evita duplicar si otro patrón ya cubrió la misma zona.
                    if (hits.Any(h => h.Bounds.Contains(new Point(union.X + union.Width / 2, union.Y + union.Height / 2)))) continue;
                    hits.Add(new Hit(kind, union));
                }
            }
        }
        return hits;
    }
}
