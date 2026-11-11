using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Capture;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>
/// Prueba automática sin interacción: AppShotScreen.exe --selftest carpeta
/// Captura la pantalla, recorta, dibuja anotaciones en el editor, exporta con efectos y corre OCR.
/// </summary>
internal static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private static string ClipboardHolder()
    {
        var names = new HashSet<string>();
        for (int i = 0; i < 40; i++)
        {
            var h = GetOpenClipboardWindow();
            if (h != IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out uint pid);
                try { names.Add(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName); } catch { names.Add("pid " + pid); }
            }
            Thread.Sleep(25);
        }
        return names.Count == 0 ? "(nadie en este momento)" : string.Join(", ", names);
    }

    public static async Task<int> RunAsync(string outDir, bool fast = false)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        void Ok(string m) => log.Add("OK   " + m);
        int failures = 0;
        void Fail(string m) { log.Add("FAIL " + m); failures++; }

        try
        {
            var vs = ScreenCapture.VirtualScreen;
            var full = ScreenCapture.Capture(vs);
            if (full.PixelWidth == vs.Width && full.PixelHeight == vs.Height) Ok($"captura escritorio virtual {vs.Width}x{vs.Height}");
            else Fail($"tamaño de captura {full.PixelWidth}x{full.PixelHeight} != {vs.Width}x{vs.Height}");

            var crop = ScreenCapture.Crop(full, new Int32Rect(0, 0, Math.Min(800, full.PixelWidth), Math.Min(500, full.PixelHeight)));
            Ok($"recorte {crop.PixelWidth}x{crop.PixelHeight}");

            var editor = new EditorWindow(crop, new Settings { Persist = false, SaveFolder = outDir });
            editor.Show();
            editor.UpdateLayout();
            var surface = (System.Windows.Controls.Canvas)editor.FindName("Surface");

            // Nitidez: sin anotaciones, la imagen exportada debe ser idéntica píxel a píxel a la captura.
            {
                var plain = Exporter.Render(surface);
                int st = crop.PixelWidth * 4;
                var a = new byte[st * crop.PixelHeight];
                var b = new byte[st * crop.PixelHeight];
                crop.CopyPixels(a, st, 0);
                new FormatConvertedBitmap(plain, PixelFormats.Bgra32, null, 0).CopyPixels(b, st, 0);
                int diff = 0;
                for (int i = 0; i < a.Length; i += 4)
                    if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) diff++;
                if (plain.PixelWidth == crop.PixelWidth && diff == 0) Ok("exportación idéntica píxel a píxel a la captura (sin pérdida)");
                else Fail($"exportación difiere de la captura en {diff} píxeles de {a.Length / 4}");

                // Guardado PNG y portapapeles también sin pérdida
                string tmp = Path.Combine(outDir, "0_sin_anotar.png");
                Exporter.Save(plain, tmp);
                var dec = new PngBitmapDecoder(new Uri(tmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var c = new byte[st * crop.PixelHeight];
                new FormatConvertedBitmap(dec, PixelFormats.Bgra32, null, 0).CopyPixels(c, st, 0);
                int diffPng = 0;
                for (int i = 0; i < a.Length; i += 4)
                    if (a[i] != c[i] || a[i + 1] != c[i + 1] || a[i + 2] != c[i + 2]) diffPng++;
                if (diffPng == 0) Ok("PNG guardado idéntico a la captura");
                else Fail($"PNG guardado difiere en {diffPng} píxeles");

                // Con sombra y borde, la imagen interior debe seguir intacta.
                var fx = Exporter.Compose(plain, new PresentationStyle { Shadow = true, Border = true, BorderBrush = Brushes.Black, BorderWidth = 3 });
                var inner = new byte[st * crop.PixelHeight];
                new FormatConvertedBitmap(new CroppedBitmap(fx, new Int32Rect(32 + 3, 32 + 3, crop.PixelWidth, crop.PixelHeight)), PixelFormats.Bgra32, null, 0)
                    .CopyPixels(inner, st, 0);
                int diffFx = 0;
                for (int i = 0; i < a.Length; i += 4)
                    if (a[i] != inner[i] || a[i + 1] != inner[i + 1] || a[i + 2] != inner[i + 2]) diffFx++;
                if (diffFx == 0) Ok("con sombra y borde la imagen sigue nítida (idéntica)");
                else Fail($"con efectos la imagen difiere en {diffFx} píxeles (se desenfoca)");
            }
            var red = Brushes.Red;
            surface.Children.Add(Annotations.Arrow(new Point(40, 40), new Point(300, 200), red, 4));
            surface.Children.Add(Annotations.Rectangle(new Rect(320, 60, 200, 120), Brushes.DodgerBlue, 4));
            surface.Children.Add(Annotations.Highlight(new Rect(60, 260, 300, 40), Brushes.Yellow));
            surface.Children.Add(Annotations.Step(new Point(600, 80), 1, red, 4));
            surface.Children.Add(Annotations.Step(new Point(650, 80), 2, red, 4));
            surface.Children.Add(Annotations.Pixelate(crop, new Rect(400, 300, 200, 100)));
            var label = new System.Windows.Controls.TextBlock { Text = "Hola SnapNova 2026", FontSize = 40, Foreground = Brushes.Black, Background = Brushes.White };
            System.Windows.Controls.Canvas.SetLeft(label, 40); System.Windows.Controls.Canvas.SetTop(label, 400);
            surface.Children.Add(label);
            editor.UpdateLayout();

            var rendered = Exporter.Render(surface);
            if (rendered.PixelWidth == crop.PixelWidth && rendered.PixelHeight == crop.PixelHeight) Ok("render del lienzo a tamaño real");
            else Fail($"render {rendered.PixelWidth}x{rendered.PixelHeight}");

            // Comprobamos que la flecha roja realmente se dibujó (píxel sobre la línea).
            var px = new byte[4];
            new CroppedBitmap(rendered, new Int32Rect(170, 120, 1, 1)).CopyPixels(px, 4, 0);
            if (px[2] > 180 && px[1] < 80) Ok("la flecha aparece en la imagen exportada");
            else Fail($"color en la flecha inesperado BGRA={string.Join(",", px)}");

            Exporter.Save(rendered, Path.Combine(outDir, "1_anotado.png"));
            var withFx = Exporter.Compose(rendered, new PresentationStyle { Shadow = true, Border = true, BorderBrush = Brushes.Black, BorderWidth = 3 });
            if (withFx.PixelWidth > rendered.PixelWidth) Ok($"efectos sombra+borde {withFx.PixelWidth}x{withFx.PixelHeight}");
            else Fail("los efectos no ampliaron la imagen");
            Exporter.Save(withFx, Path.Combine(outDir, "2_efectos.png"));
            Exporter.Save(withFx, Path.Combine(outDir, "3_efectos.jpg"));
            Ok("guardado PNG y JPG");

            try
            {
                Exporter.CopyToClipboard(rendered);
                bool has = false;
                for (int i = 0; i < 10 && !has; i++) { try { has = Clipboard.ContainsImage(); } catch { await Task.Delay(150); } }
                if (has) Ok("copiado al portapapeles");
                else Fail("el portapapeles no tiene imagen");
            }
            catch (System.Runtime.InteropServices.COMException) { Fail("portapapeles ocupado durante la prueba por: " + ClipboardHolder()); }

            try
            {
                string text = await OcrService.RecognizeAsync(rendered);
                File.WriteAllText(Path.Combine(outDir, "ocr.txt"), text);
                if (text.Contains("SnapNova", StringComparison.OrdinalIgnoreCase)) Ok("OCR reconoció el texto de prueba");
                else Fail("OCR no encontró el texto de prueba (ver ocr.txt)");
            }
            catch (Exception ex) { Fail("OCR: " + ex.Message); }

            editor.Close();

            await SelfTestFeatures.RunAsync(outDir, crop, Ok, Fail);
            await SelfTestMore.RunAsync(outDir, crop, Ok, Fail);
            await SelfTestV1.RunAsync(outDir, Ok, Fail);
            if (!fast) await SelfTestScroll.RunAsync(outDir, Ok, Fail);
        }
        catch (Exception ex)
        {
            Fail("excepción: " + ex);
        }

        log.Add(failures == 0 ? "RESULTADO: todo correcto" : $"RESULTADO: {failures} fallo(s)");
        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log);
        return failures;
    }
}
