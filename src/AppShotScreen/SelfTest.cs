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
    public static async Task<int> RunAsync(string outDir)
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

            var editor = new EditorWindow(crop, new Settings { SaveFolder = outDir });
            editor.Show();
            editor.UpdateLayout();
            var surface = (System.Windows.Controls.Canvas)editor.FindName("Surface");
            var red = Brushes.Red;
            surface.Children.Add(Annotations.Arrow(new Point(40, 40), new Point(300, 200), red, 4));
            surface.Children.Add(Annotations.Rectangle(new Rect(320, 60, 200, 120), Brushes.DodgerBlue, 4));
            surface.Children.Add(Annotations.Highlight(new Rect(60, 260, 300, 40), Brushes.Yellow));
            surface.Children.Add(Annotations.Step(new Point(600, 80), 1, red, 4));
            surface.Children.Add(Annotations.Step(new Point(650, 80), 2, red, 4));
            surface.Children.Add(Annotations.Pixelate(crop, new Rect(400, 300, 200, 100)));
            var label = new System.Windows.Controls.TextBlock { Text = "Hola APPSHOTACREEN 2026", FontSize = 40, Foreground = Brushes.Black, Background = Brushes.White };
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
            var withFx = Exporter.ApplyEffects(rendered, shadow: true, border: true, borderBrush: Brushes.Black, borderWidth: 3);
            if (withFx.PixelWidth > rendered.PixelWidth) Ok($"efectos sombra+borde {withFx.PixelWidth}x{withFx.PixelHeight}");
            else Fail("los efectos no ampliaron la imagen");
            Exporter.Save(withFx, Path.Combine(outDir, "2_efectos.png"));
            Exporter.Save(withFx, Path.Combine(outDir, "3_efectos.jpg"));
            Ok("guardado PNG y JPG");

            Exporter.CopyToClipboard(rendered);
            if (Clipboard.ContainsImage()) Ok("copiado al portapapeles");
            else Fail("el portapapeles no tiene imagen");

            try
            {
                string text = await OcrService.RecognizeAsync(rendered);
                File.WriteAllText(Path.Combine(outDir, "ocr.txt"), text);
                if (text.Contains("APPSHOTACREEN", StringComparison.OrdinalIgnoreCase)) Ok("OCR reconoció el texto de prueba");
                else Fail("OCR no encontró el texto de prueba (ver ocr.txt)");
            }
            catch (Exception ex) { Fail("OCR: " + ex.Message); }

            editor.Close();
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
