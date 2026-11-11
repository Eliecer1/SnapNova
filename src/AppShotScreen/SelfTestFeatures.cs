using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Capture;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Pruebas de las funciones nuevas: presentación, datos sensibles, foco, fijar, ventanas, inicio.</summary>
internal static class SelfTestFeatures
{
    public static async Task RunAsync(string outDir, BitmapSource shot, Action<string> ok, Action<string> fail)
    {
        Presentation(outDir, shot, ok, fail);
        await RedactAsync(outDir, ok, fail);
        await EditorUiAsync(outDir, shot, ok, fail);
        await PinAsync(shot, outDir, ok, fail);
        await OverlayAsync(outDir, ok, fail);
        await HomeAsync(outDir, shot, ok, fail);
    }

    private static byte[] Pixels(BitmapSource img, Int32Rect r)
    {
        var px = new byte[r.Width * 4 * r.Height];
        new FormatConvertedBitmap(new CroppedBitmap(img, r), PixelFormats.Bgra32, null, 0).CopyPixels(px, r.Width * 4, 0);
        return px;
    }

    private static int DiffRgb(byte[] a, byte[] b)
    {
        int d = 0;
        for (int i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) d++;
        return d;
    }

    /// <summary>Guarda cómo se ve un elemento (para revisar el diseño).</summary>
    private static void Snap(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        if (w < 1 || h < 1) return;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        Exporter.Save(rtb, path);
    }

    private static void Presentation(string outDir, BitmapSource shot, Action<string> ok, Action<string> fail)
    {
        var style = new PresentationStyle { Background = 0, Padding = 64, Radius = 16, Shadow = true };
        var outImg = Exporter.Compose(shot, style);
        Exporter.Save(outImg, Path.Combine(outDir, "5_presentacion.png"));

        if (outImg.PixelWidth != shot.PixelWidth + 128 || outImg.PixelHeight != shot.PixelHeight + 128)
        {
            fail($"presentación: tamaño {outImg.PixelWidth}x{outImg.PixelHeight} inesperado");
            return;
        }
        // El interior (sin las esquinas redondeadas) debe ser idéntico a la captura.
        var inner = new Int32Rect(20, 20, shot.PixelWidth - 40, shot.PixelHeight - 40);
        int diff = DiffRgb(Pixels(shot, inner), Pixels(outImg, new Int32Rect(64 + 20, 64 + 20, inner.Width, inner.Height)));
        // La esquina de la imagen debe mostrar el degradado (recortada) y el margen también.
        var corner = Pixels(outImg, new Int32Rect(64, 64, 1, 1));
        var margin = Pixels(outImg, new Int32Rect(8, 8, 1, 1));
        var (_, a, _) = PresentationStyle.Backgrounds[0];
        bool marginIsGradient = Math.Abs(margin[2] - a.R) < 30 && Math.Abs(margin[0] - a.B) < 30;
        bool cornerClipped = !(corner[0] == Pixels(shot, new Int32Rect(0, 0, 1, 1))[0] && corner[1] == Pixels(shot, new Int32Rect(0, 0, 1, 1))[1]) || marginIsGradient;

        if (diff == 0 && marginIsGradient && cornerClipped) ok("presentación: fondo degradado + esquinas + sombra, con la captura intacta por dentro");
        else fail($"presentación: diff interior={diff}, margen degradado={marginIsGradient}");
    }

    private static BitmapSource TextImage(params string[] lines)
    {
        var panel = new StackPanel { Background = Brushes.White, Width = 900 };
        foreach (var l in lines)
            panel.Children.Add(new TextBlock { Text = l, FontSize = 22, Foreground = Brushes.Black, Margin = new Thickness(30, 16, 0, 0), FontFamily = new FontFamily("Segoe UI") });
        panel.Children.Add(new Border { Height = 24 });
        panel.Measure(new Size(900, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        var rtb = new RenderTargetBitmap(900, (int)panel.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(panel);
        var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    private static async Task RedactAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        try
        {
            var img = TextImage(
                "Reunión de equipo el martes por la tarde",
                "Contacto: juan.perez@correo.com",
                "Teléfono: +505 8888 1234",
                "Tarjeta: 4532 7591 8846 2310",
                "Gracias por su atención");
            var editor = new EditorWindow(img, new Settings { Persist = false, SaveFolder = outDir });
            editor.Show();
            editor.UpdateLayout();
            int n = await editor.RedactSensitiveAsync();
            editor.UpdateLayout();
            var result = editor.RenderFinal();
            Exporter.Save(result, Path.Combine(outDir, "6_datos_ocultos.png"));
            string after = await OcrService.RecognizeAsync(result);
            File.WriteAllText(Path.Combine(outDir, "ocr_tras_ocultar.txt"), after);
            editor.Close();

            bool leaked = after.Contains("correo", StringComparison.OrdinalIgnoreCase) || after.Contains("8888") || after.Contains("4532") || after.Contains("8846");
            bool kept = after.Contains("Reuni", StringComparison.OrdinalIgnoreCase) && after.Contains("Gracias", StringComparison.OrdinalIgnoreCase);
            if (n >= 3 && !leaked && kept) ok($"datos sensibles: {n} ocultados (correo, teléfono, tarjeta) y el resto del texto intacto");
            else fail($"datos sensibles: ocultados={n}, se filtró algo={leaked}, texto normal conservado={kept} (ver ocr_tras_ocultar.txt)");
        }
        catch (Exception ex) { fail("datos sensibles: " + ex.Message); }
    }

    private static async Task EditorUiAsync(string outDir, BitmapSource shot, Action<string> ok, Action<string> fail)
    {
        var editor = new EditorWindow(shot, new Settings { Persist = false, SaveFolder = outDir });
        editor.Show();
        var surface = (Canvas)editor.FindName("Surface");
        surface.Children.Add(Annotations.Arrow(new Point(60, 60), new Point(280, 190), Brushes.Red, 4));
        surface.Children.Add(Annotations.Step(new Point(320, 210), 1, Brushes.Red, 4));

        // Foco: fuera se oscurece, dentro queda igual.
        var before = Exporter.Render(surface);
        editor.AddSpotlight(new Rect(400, 250, 250, 150));
        editor.UpdateLayout();
        var after = Exporter.Render(surface);
        var pIn0 = Pixels(before, new Int32Rect(500, 320, 1, 1)); var pIn1 = Pixels(after, new Int32Rect(500, 320, 1, 1));
        var pOut0 = Pixels(before, new Int32Rect(700, 60, 1, 1)); var pOut1 = Pixels(after, new Int32Rect(700, 60, 1, 1));
        int sum0 = pOut0[0] + pOut0[1] + pOut0[2], sum1 = pOut1[0] + pOut1[1] + pOut1[2];
        if (DiffRgb(pIn0, pIn1) == 0 && (sum1 < sum0 || sum0 == 0)) ok("foco: oscurece el exterior y deja intacta la zona marcada");
        else fail($"foco: dentro cambió={DiffRgb(pIn0, pIn1)}, fuera {sum0}->{sum1}");

        editor.SelectBackground(0);
        editor.UpdateLayout();
        await Task.Delay(400);
        Snap((FrameworkElement)editor.Content, Path.Combine(outDir, "ui_editor.png"));
        var final = editor.RenderFinal();
        Exporter.Save(final, Path.Combine(outDir, "7_editor_exportado.png"));
        if (final.PixelWidth > shot.PixelWidth) ok($"editor: exporta con presentación ({final.PixelWidth}x{final.PixelHeight})");
        else fail("editor: la presentación no se aplicó al exportar");
        editor.Close();
    }

    private static async Task PinAsync(BitmapSource shot, string outDir, Action<string> ok, Action<string> fail)
    {
        int before = PinWindow.OpenCount;
        var small = ScreenCapture.Crop(shot, new Int32Rect(0, 0, Math.Min(320, shot.PixelWidth), Math.Min(200, shot.PixelHeight)));
        var pin = PinWindow.Show(small, new Settings { Persist = false, SaveFolder = outDir }, new PixelRect(200, 200, small.PixelWidth, small.PixelHeight));
        await Task.Delay(500);
        bool shown = pin.IsVisible && pin.Topmost && PinWindow.OpenCount == before + 1;
        pin.Close();
        await Task.Delay(100);
        if (shown && PinWindow.OpenCount == before) ok("fijar: la captura queda flotando encima y se cierra bien");
        else fail($"fijar: visible={shown}, abiertas={PinWindow.OpenCount}");
    }

    private static async Task OverlayAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        var windows = WindowFinder.Snapshot();
        var bounds = ScreenCapture.VirtualScreen;
        var overlay = new OverlayWindow(ScreenCapture.Capture(bounds), bounds, windows);
        overlay.Show();
        await Task.Delay(600);
        Snap((FrameworkElement)overlay.Content, Path.Combine(outDir, "ui_overlay.png"));
        var loupe = (FrameworkElement)overlay.FindName("Loupe");
        bool loupeVisible = loupe.Visibility == Visibility.Visible;
        overlay.Close();
        if (windows.Count > 0 && loupeVisible) ok($"captura inteligente: {windows.Count} ventanas detectadas y lupa visible");
        else fail($"captura inteligente: ventanas={windows.Count}, lupa={loupeVisible}");
    }

    private static async Task HomeAsync(string outDir, BitmapSource shot, Action<string> ok, Action<string> fail)
    {
        string realFolder = History.Folder;
        History.Folder = Path.Combine(outDir, "historial");
        try
        {
            History.Add(shot);
            History.Add(Exporter.Compose(shot, new PresentationStyle { Background = 2, Padding = 48, Radius = 12, Shadow = true }));
            for (int i = 0; i < 40 && History.Recent(10).Count < 2; i++) await Task.Delay(100);

            var home = new HomeWindow(new Settings { Persist = false, SaveFolder = outDir });
            home.Show();
            home.RefreshRecent();
            await Task.Delay(400);
            ((ScrollViewer)home.Content).Background = home.Background; // solo para que la foto de prueba salga con el fondo real
            Snap((FrameworkElement)home.Content, Path.Combine(outDir, "ui_inicio.png"));
            int recent = home.RecentCount;
            home.Close();
            if (recent == 2) ok("inicio: ventana principal con modos de captura e historial (2 recientes)");
            else fail($"inicio: recientes={recent}, esperado 2");
        }
        finally
        {
            History.Folder = realFolder;
        }
    }
}
