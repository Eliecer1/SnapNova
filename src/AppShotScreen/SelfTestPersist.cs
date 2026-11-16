using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Las anotaciones se conservan al cerrar el editor y volver a abrir la captura.</summary>
internal static class SelfTestPersist
{
    private static BitmapSource Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        img.Freeze();
        return img;
    }

    /// <summary>Porcentaje de píxeles que difieren en más de 40 niveles (tolerancia de suavizado).</summary>
    private static double DiffPercent(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 100;
        int st = a.PixelWidth * 4;
        var pa = new byte[st * a.PixelHeight];
        var pb = new byte[st * b.PixelHeight];
        new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0).CopyPixels(pa, st, 0);
        new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(pb, st, 0);
        int diff = 0;
        for (int i = 0; i < pa.Length; i += 4)
            if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) > 40) diff++;
        return 100.0 * diff / (pa.Length / 4);
    }

    public static async Task RunAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        string real = History.Folder;
        History.Folder = Path.Combine(outDir, "persistencia");
        var settings = new Settings { Persist = false, SaveFolder = outDir };
        try
        {
            Directory.CreateDirectory(History.Folder);
            string fileA = Path.Combine(History.Folder, "20260102_100000_000.png");
            string fileB = Path.Combine(History.Folder, "20260102_100001_000.png");
            // Imagen A con algo de "contenido" para que el pixelado se note.
            var a = Solid(600, 400, 255, 255, 255);
            var dvA = new DrawingVisual();
            using (var ctx = dvA.RenderOpen())
            {
                ctx.DrawImage(a, new Rect(0, 0, 600, 400));
                for (int i = 0; i < 12; i++) ctx.DrawRectangle(i % 2 == 0 ? Brushes.Navy : Brushes.Orange, null, new Rect(380 + i * 15, 260, 15, 80));
            }
            var rtbA = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            rtbA.Render(dvA);
            Exporter.Save(rtbA, fileA);
            Exporter.Save(Solid(500, 300, 40, 160, 60), fileB);

            // ── Sesión 1: anotar A y redimensionar B ──
            var e1 = new EditorWindow(ImageTools.Load(fileA), settings, fileA);
            e1.Show();
            await Task.Delay(300);
            var s1 = (Canvas)e1.FindName("Surface");
            s1.Children.Add(Annotations.Rectangle(new Rect(40, 40, 200, 120), Brushes.Red, 6));
            s1.Children.Add(Annotations.Arrow(new Point(300, 60), new Point(450, 180), Brushes.DodgerBlue, 5));
            s1.Children.Add(Annotations.Step(new Point(520, 60), 1, Brushes.Red, 4));
            var text = Annotations.Text(new Point(40, 230), Brushes.Black, 4);
            text.Text = "Hola SnapNova";
            s1.Children.Add(text);
            s1.Children.Add(Annotations.Pixelate(ImageTools.Load(fileA), new Rect(370, 250, 200, 100)));
            s1.Children.Add(Stamps.Create("ok", new Point(560, 360), 40));
            e1.AddSpotlight(new Rect(20, 20, 260, 180));
            e1.MoveAnnotation(s1.Children[2], new Vector(10, 0));
            e1.UpdateLayout();
            await Task.Delay(200);
            var before = Exporter.Render(s1);
            int countBefore = s1.Children.Count;

            e1.OpenOrSwitch(ImageTools.Load(fileB), fileB); // cambia a B (guarda A)
            e1.ResizeTo(250);
            e1.UpdateLayout();
            e1.Close(); // guarda B
            await Task.Delay(200);

            bool savedFiles = EditStore.Exists(fileA) && EditStore.Exists(fileB);

            // ── Sesión 2: abrir de nuevo ──
            var e2 = new EditorWindow(ImageTools.Load(fileA), settings, fileA);
            e2.Show();
            await Task.Delay(300);
            var s2 = (Canvas)e2.FindName("Surface");
            e2.UpdateLayout();
            await Task.Delay(200);
            var after = Exporter.Render(s2);
            Exporter.Save(before, Path.Combine(outDir, "12_antes_de_cerrar.png"));
            Exporter.Save(after, Path.Combine(outDir, "13_al_reabrir.png"));
            double diff = DiffPercent(before, after);
            bool sameCount = s2.Children.Count == countBefore;

            e2.OpenOrSwitch(ImageTools.Load(fileB), fileB);
            e2.UpdateLayout();
            bool resizedKept = e2.CurrentImage.PixelWidth == 250;
            e2.Close();


            // Borrar la captura borra también sus ediciones.
            EditStore.Delete(fileA);
            bool cleaned = !EditStore.Exists(fileA);

            if (savedFiles && sameCount && diff < 1.0 && resizedKept && cleaned)
                ok($"anotaciones guardadas entre sesiones: al reabrir, {countBefore - 1} anotaciones idénticas ({diff:0.00} % de diferencia) y el redimensionado se conserva");
            else
                fail($"persistencia: archivos={savedFiles} mismas anotaciones={sameCount} ({s2.Children.Count} vs {countBefore}) diferencia={diff:0.00}% redimensionado={resizedKept} limpieza={cleaned}");
        }
        catch (Exception ex) { fail("persistencia: " + ex); }
        finally { History.Folder = real; }
    }
}
