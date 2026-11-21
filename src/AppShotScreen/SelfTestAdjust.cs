using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Capture;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>
/// v1.4: la región elegida se puede ajustar con tiradores antes de capturar, y una captura se
/// copia con sus anotaciones desde el menú contextual de la bandeja o la Biblioteca.
/// </summary>
internal static class SelfTestAdjust
{
    public static async Task RunAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        try { await AdjustAsync(outDir, ok, fail); } catch (Exception ex) { fail("ajustar región: " + ex); }
        try { await TrayCopyAsync(outDir, ok, fail); } catch (Exception ex) { fail("copiar desde la bandeja: " + ex); }
    }

    private static async Task AdjustAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        var bounds = ScreenCapture.VirtualScreen;
        var overlay = new OverlayWindow(ScreenCapture.Capture(bounds), bounds, new List<WindowFinder.WindowInfo>()) { TestMode = true };
        overlay.Show();
        await Task.Delay(500);
        var root = (FrameworkElement)overlay.Content;
        double sx = bounds.Width / root.ActualWidth, sy = bounds.Height / root.ActualHeight;

        // Arrastre normal: se elige la región y NO se captura todavía.
        overlay.PointerDown(new Point(100, 100));
        overlay.PointerMove(new Point(250, 200));
        overlay.PointerMove(new Point(400, 300));
        overlay.PointerUp(new Point(400, 300));
        await Task.Delay(100);
        bool adjusting = overlay.IsAdjusting && overlay.Result == null && overlay.IsVisible;
        bool handlesShown = ((FrameworkElement)overlay.FindName("Handles")).Visibility == Visibility.Visible
                         && ((FrameworkElement)overlay.FindName("AdjustBar")).Visibility == Visibility.Visible;
        var first = overlay.SelectionPixels;

        // Tirador de la esquina inferior derecha: agrandar 100×50.
        overlay.PointerDown(new Point(400, 300));
        overlay.PointerMove(new Point(450, 330));
        overlay.PointerMove(new Point(500, 350));
        overlay.PointerUp(new Point(500, 350));
        var resized = overlay.SelectionPixels;

        // Lado izquierdo: hacia la derecha 50.
        overlay.PointerDown(new Point(100, 225));
        overlay.PointerMove(new Point(150, 225));
        overlay.PointerUp(new Point(150, 225));
        var leftMoved = overlay.SelectionPixels;

        // Mover arrastrando por dentro.
        overlay.PointerDown(new Point(300, 220));
        overlay.PointerMove(new Point(320, 240));
        overlay.PointerUp(new Point(320, 240));
        var moved = overlay.SelectionPixels;
        await Task.Delay(150);
        SelfTestFeatures.Snap(root, Path.Combine(outDir, "ui_ajustar_region.png"));

        overlay.Confirm();
        await Task.Delay(100);
        var result = overlay.Result;

        int W(double v) => (int)Math.Round(v * sx);
        int H(double v) => (int)Math.Round(v * sy);
        bool sizesOk = Math.Abs(first.Width - W(300)) <= 1 && Math.Abs(first.Height - H(200)) <= 1
                    && Math.Abs(resized.Width - W(400)) <= 1 && Math.Abs(resized.Height - H(250)) <= 1
                    && Math.Abs(leftMoved.Width - W(350)) <= 1 && Math.Abs(leftMoved.X - W(150)) <= 1
                    && Math.Abs(moved.X - W(170)) <= 1 && Math.Abs(moved.Y - H(120)) <= 1 && moved.Width == leftMoved.Width;
        bool captured = result != null && result.PixelWidth == moved.Width && result.PixelHeight == moved.Height && !overlay.IsVisible;

        if (adjusting && handlesShown && sizesOk && captured)
            ok($"ajustar región: tiradores visibles, se agranda, se mueve un lado y la región entera; captura {result!.PixelWidth}×{result.PixelHeight}");
        else
            fail($"ajustar región: ajustando={adjusting} tiradores={handlesShown} primera={Fmt(first)} agrandada={Fmt(resized)} lado={Fmt(leftMoved)} movida={Fmt(moved)} captura={result?.PixelWidth}×{result?.PixelHeight}");
    }

    private static string Fmt(Int32Rect r) => $"{r.X},{r.Y} {r.Width}×{r.Height}";

    private static BitmapSource Solid(int w, int h, Color c)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        img.Freeze();
        return img;
    }

    private static bool RedAt(BitmapSource img, int x, int y)
    {
        var px = new byte[4];
        new CroppedBitmap(new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0), new Int32Rect(x, y, 1, 1)).CopyPixels(px, 4, 0);
        return px[2] > 180 && px[1] < 90 && px[0] < 90;
    }

    private static async Task<BitmapSource?> ClipboardImageAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (Clipboard.ContainsImage()) return Clipboard.GetImage(); } catch { }
            await Task.Delay(150);
        }
        return null;
    }

    private static async Task TrayCopyAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        string real = History.Folder;
        History.Folder = Path.Combine(outDir, "copiar_bandeja");
        var settings = new Settings { Persist = false, SaveFolder = outDir };
        try
        {
            Directory.CreateDirectory(History.Folder);
            string fileA = Path.Combine(History.Folder, "20260103_100000_000.png");
            string fileB = Path.Combine(History.Folder, "20260103_100001_000.png");
            Exporter.Save(Solid(400, 300, Colors.White), fileA);
            Exporter.Save(Solid(300, 200, Colors.LightGray), fileB);

            // A con un rectángulo rojo; luego se cambia a B: A queda en la bandeja (no abierta).
            var editor = new EditorWindow(ImageTools.Load(fileA), settings, fileA);
            editor.Show();
            await Task.Delay(300);
            var surface = (Canvas)editor.FindName("Surface");
            surface.Children.Add(Annotations.Rectangle(new Rect(50, 50, 200, 120), Brushes.Red, 8));
            editor.UpdateLayout();
            editor.OpenOrSwitch(ImageTools.Load(fileB), fileB);
            editor.UpdateLayout();
            int ia = Enumerable.Range(0, editor.TrayCount).First(i => editor.DocAt(i).FilePath == fileA);

            editor.CopyDoc(editor.DocAt(ia), annotated: true);
            var fromTray = await ClipboardImageAsync();
            bool trayOk = fromTray != null && fromTray.PixelWidth == 400 && RedAt(fromTray, 54, 100);
            if (fromTray != null) Exporter.Save(fromTray, Path.Combine(outDir, "14_copiado_desde_bandeja.png"));

            editor.CopyDoc(editor.DocAt(ia), annotated: false);
            var original = await ClipboardImageAsync();
            bool originalOk = original != null && !RedAt(original, 54, 100);
            editor.Close(); // guarda las anotaciones de A en disco
            await Task.Delay(200);

            // Biblioteca: desde el archivo guardado, sin editor abierto.
            LibraryWindow.CopyFile(fileA, annotated: true);
            var fromLibrary = await ClipboardImageAsync();
            bool libraryOk = fromLibrary != null && fromLibrary.PixelWidth == 400 && RedAt(fromLibrary, 54, 100);
            var thumb = EditStore.LoadThumb(fileA);

            if (trayOk && originalOk && libraryOk && thumb != null)
                ok("copiar con anotaciones: desde la bandeja (sin abrirla), su original, y desde la Biblioteca con las ediciones guardadas");
            else
                fail($"copiar con anotaciones: bandeja={trayOk} original={originalOk} biblioteca={libraryOk} miniatura={thumb != null}");
        }
        finally { History.Folder = real; }
    }
}
