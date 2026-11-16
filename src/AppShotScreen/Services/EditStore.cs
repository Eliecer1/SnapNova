using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Editor;

namespace AppShotScreen.Services;

/// <summary>
/// Guarda las anotaciones de cada captura para recuperarlas en otra sesión (como Snagit).
/// Por cada imagen hay un JSON en Historial\ediciones con: las anotaciones (vectoriales en XAML;
/// las que llevan imagen —pixelado, lupa— como PNG), las zonas de "foco", la imagen base si se
/// recortó o redimensionó, y una miniatura. La imagen original no se modifica nunca.
/// </summary>
public static class EditStore
{
    public sealed class Annotation
    {
        public string Kind { get; set; } = "xaml";   // "xaml" o "bitmap"
        public string? Xaml { get; set; }
        public string? Png { get; set; }             // base64
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public sealed class Data
    {
        public int Version { get; set; } = 1;
        public string? ImagePath { get; set; }
        public string? BasePng { get; set; }         // solo si se recortó o redimensionó
        public List<Annotation> Annotations { get; set; } = new();
        public List<double[]> SpotHoles { get; set; } = new();
        public string? ThumbPng { get; set; }
    }

    public sealed record Loaded(BitmapSource? Base, List<UIElement> Annotations, List<Rect> SpotHoles);

    private static string Folder => Path.Combine(History.Folder, "ediciones");

    public static string PathFor(string imagePath)
    {
        string key = Path.GetFullPath(imagePath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return Path.Combine(Folder, hash + ".json");
    }

    public static bool Exists(string? imagePath) => imagePath != null && File.Exists(PathFor(imagePath));

    public static void Delete(string imagePath)
    {
        try { File.Delete(PathFor(imagePath)); } catch { /* no crítico */ }
    }

    // ───────────── Guardar ─────────────

    /// <summary>Guarda (o borra, si no hay cambios) las ediciones de una captura.</summary>
    public static void Save(string imagePath, BitmapSource? changedBase, IReadOnlyList<UIElement> annotations, IReadOnlyList<Rect> spotHoles,
                            FrameworkElement surface, BitmapSource? thumbnail)
    {
        if (changedBase == null && annotations.Count == 0)
        {
            Delete(imagePath);
            return;
        }
        var data = new Data
        {
            ImagePath = imagePath,
            BasePng = changedBase == null ? null : ToBase64(changedBase),
            SpotHoles = spotHoles.Select(r => new[] { r.X, r.Y, r.Width, r.Height }).ToList(),
            ThumbPng = thumbnail == null ? null : ToBase64(thumbnail),
        };
        foreach (var el in annotations)
        {
            var a = Serialize(el, surface);
            if (a != null) data.Annotations.Add(a);
        }
        Directory.CreateDirectory(Folder);
        string target = PathFor(imagePath), tmp = target + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data));
        File.Move(tmp, target, overwrite: true); // escritura atómica: nunca queda un JSON a medias
    }

    private static Annotation? Serialize(UIElement el, FrameworkElement surface)
    {
        if (!ContainsImage(el))
        {
            try
            {
                // Las flechas usan StreamGeometry, que XamlWriter no sabe escribir: la pasamos a PathGeometry.
                foreach (var p in FindVisual<System.Windows.Shapes.Path>(el))
                    if (p.Data is StreamGeometry sg) p.Data = PathGeometry.CreateFromGeometry(sg);
                return new Annotation { Kind = "xaml", Xaml = XamlWriter.Save(el) };
            }
            catch { /* si algo no se puede describir en XAML, lo guardamos como imagen */ }
        }
        // Pixelado, lupa…: se guardan tal como se ven, como una imagen que se puede seguir moviendo.
        // Se dibuja el lienzo con solo esta anotación visible (sin la captura debajo) y se recorta su zona.
        var bounds = VisualTreeHelper.GetDescendantBounds((Visual)el);
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return null;
        bounds = ((Visual)el).TransformToAncestor(surface).TransformBounds(bounds);
        bounds.Inflate(20, 20); // margen para la sombra
        bounds.Intersect(new Rect(0, 0, surface.Width, surface.Height));
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return null;

        var canvas = (Panel)surface;
        var hidden = new List<UIElement>();
        foreach (UIElement c in canvas.Children)
            if (c != el && c.Visibility == Visibility.Visible) { c.Visibility = Visibility.Hidden; hidden.Add(c); }
        var oldBackground = canvas.Background;
        canvas.Background = Brushes.Transparent;
        BitmapSource full;
        try
        {
            canvas.UpdateLayout();
            full = Exporter.Render(surface);
        }
        finally
        {
            canvas.Background = oldBackground;
            foreach (var c in hidden) c.Visibility = Visibility.Visible;
        }
        var px = new Int32Rect((int)bounds.X, (int)bounds.Y, (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
        px.Width = Math.Min(px.Width, full.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, full.PixelHeight - px.Y);
        var piece = new CroppedBitmap(full, px);
        return new Annotation
        {
            Kind = "bitmap", Png = ToBase64(piece), Left = px.X, Top = px.Y, Width = px.Width, Height = px.Height,
        };
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) yield return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var x in FindVisual<T>(VisualTreeHelper.GetChild(root, i))) yield return x;
    }

    private static bool ContainsImage(DependencyObject d)
    {
        if (d is Image) return true;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) if (ContainsImage(VisualTreeHelper.GetChild(d, i))) return true;
        return false;
    }

    // ───────────── Cargar ─────────────

    public static Loaded? Load(string imagePath)
    {
        try
        {
            string file = PathFor(imagePath);
            if (!File.Exists(file)) return null;
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(file));
            if (data == null) return null;

            var list = new List<UIElement>();
            foreach (var a in data.Annotations)
            {
                try
                {
                    if (a.Kind == "xaml" && a.Xaml != null)
                    {
                        if (XamlReader.Parse(a.Xaml) is UIElement el) { ReattachText(el); list.Add(el); }
                    }
                    else if (a.Png != null)
                    {
                        var img = new Image { Source = FromBase64(a.Png), Width = a.Width, Height = a.Height, Stretch = Stretch.Fill };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                        Canvas.SetLeft(img, a.Left);
                        Canvas.SetTop(img, a.Top);
                        list.Add(img);
                    }
                }
                catch { /* una anotación dañada no impide recuperar las demás */ }
            }
            var holes = data.SpotHoles.Where(h => h.Length == 4).Select(h => new Rect(h[0], h[1], h[2], h[3])).ToList();
            return new Loaded(data.BasePng == null ? null : FromBase64(data.BasePng), list, holes);
        }
        catch
        {
            return null; // archivo dañado: se abre la captura limpia
        }
    }

    public static BitmapSource? LoadThumb(string imagePath)
    {
        try
        {
            string file = PathFor(imagePath);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("ThumbPng", out var t) && t.GetString() is { } s ? FromBase64(s) : null;
        }
        catch { return null; }
    }

    /// <summary>Los textos recuperados vuelven a mostrar el borde guía solo mientras se escribe.</summary>
    private static void ReattachText(DependencyObject root)
    {
        foreach (var tb in Find<TextBox>(root))
        {
            if (tb.Background is not SolidColorBrush { Color.A: 0 }) continue; // el del globo no lleva borde
            tb.GotKeyboardFocus += (_, _) => tb.BorderThickness = new Thickness(1);
            tb.LostKeyboardFocus += (_, _) => tb.BorderThickness = new Thickness(0);
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) yield return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var x in Find<T>(child)) yield return x;
    }

    private static string ToBase64(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static BitmapSource FromBase64(string s)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(s));
        var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var f = dec.Frames[0];
        var bgra = ImageTools.ToBgra(f);
        return bgra;
    }
}
