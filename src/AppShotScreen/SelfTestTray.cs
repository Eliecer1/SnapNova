using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Pruebas del editor único con bandeja de capturas.</summary>
internal static class SelfTestTray
{
    private static BitmapSource Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        img.Freeze();
        return img;
    }

    private static byte[] Pixel(BitmapSource img, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(new CroppedBitmap(img, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0).CopyPixels(px, 4, 0);
        return px;
    }

    public static async Task RunAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        string real = History.Folder;
        History.Folder = Path.Combine(outDir, "bandeja_historial");
        EditorWindow? editor = null;
        try
        {
            Directory.CreateDirectory(History.Folder);
            Exporter.Save(Solid(320, 200, 40, 160, 60), Path.Combine(History.Folder, "20260101_100000_000.png")); // verde (antigua)
            Exporter.Save(Solid(300, 300, 40, 80, 220), Path.Combine(History.Folder, "20260101_100001_000.png")); // azul

            // Captura A (blanca 600×400) abre el editor.
            var a = Solid(600, 400, 255, 255, 255);
            editor = new EditorWindow(a, new Settings { Persist = false, SaveFolder = outDir }, Path.Combine(History.Folder, "20260101_100002_000.png"));
            editor.Show();
            await Task.Delay(300);
            var surface = (Canvas)editor.FindName("Surface");
            bool startOk = editor.TrayCount == 3 && editor.CurrentIndex == 0;

            // Anotamos A con un rectángulo rojo.
            var rect = Annotations.Rectangle(new Rect(100, 100, 200, 120), Brushes.Red, 8);
            surface.Children.Add(rect);
            editor.MoveAnnotation(rect, new Vector(0, 0)); // deja una entrada en "deshacer"

            // Llega la captura B: mismo editor, nueva miniatura delante, lienzo limpio.
            var b = Solid(500, 300, 250, 200, 40);
            editor.AddCapture(b, Path.Combine(History.Folder, "20260101_100003_000.png"));
            editor.UpdateLayout();
            bool bOk = editor.TrayCount == 4 && editor.CurrentIndex == 0 && editor.CurrentImage.PixelWidth == 500
                       && surface.Children.Count == 1; // solo la imagen base

            // Volver a A: la anotación sigue ahí y se puede deshacer.
            editor.SwitchToIndex(1);
            editor.UpdateLayout();
            var renderA = Exporter.Render(surface);
            bool aBack = editor.CurrentImage.PixelWidth == 600 && surface.Children.Contains(rect) && Pixel(renderA, 100, 160)[2] > 200 && Pixel(renderA, 100, 160)[1] < 60;
            bool undoKept = ((Button)editor.FindName("UndoBtn")).IsEnabled;

            // Abrir una captura antigua de la bandeja (se carga del disco).
            editor.SwitchToIndex(3);
            editor.UpdateLayout();
            var old = Exporter.Render(surface);
            bool oldOk = editor.CurrentImage.PixelWidth == 320 && Pixel(old, 10, 10)[1] == 160;

            // Abrir un archivo que ya está en la bandeja no la duplica.
            editor.OpenOrSwitch(b, Path.Combine(History.Folder, "20260101_100003_000.png"));
            bool noDup = editor.TrayCount == 4 && editor.CurrentIndex == 0;

            // Ctrl+→ / botón siguiente
            editor.Step(+1);
            bool stepOk = editor.CurrentIndex == 1;

            await Task.Delay(900); // miniaturas actualizadas
            editor.UpdateLayout();
            var content = (FrameworkElement)editor.Content;
            var rtb = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);
            Exporter.Save(rtb, Path.Combine(outDir, "ui_bandeja.png"));

            if (startOk && bOk && aBack && undoKept && oldOk && noDup && stepOk)
                ok("bandeja: un solo editor; cada captura conserva sus anotaciones y su deshacer; abre capturas antiguas y no duplica");
            else
                fail($"bandeja: inicio={startOk} nueva={bOk} volver a A={aBack} deshacer={undoKept} antigua={oldOk} sin duplicar={noDup} siguiente={stepOk} (total={editor.TrayCount}, actual={editor.CurrentIndex})");
        }
        catch (Exception ex) { fail("bandeja: " + ex); }
        finally
        {
            editor?.Close();
            History.Folder = real;
        }
    }
}
