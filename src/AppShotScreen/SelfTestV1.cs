using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Pruebas de: biblioteca, combinar capturas, lupa, redimensionar y atajos personalizables.</summary>
internal static class SelfTestV1
{
    public static async Task RunAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        await LibraryAsync(outDir, ok, fail);
        HotkeysTest(outDir, ok, fail);
        await EditorAsync(outDir, ok, fail);
    }

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

    private static void Snap(Window w, string path)
    {
        var el = (FrameworkElement)w.Content;
        el.UpdateLayout();
        var host = new DrawingVisual();
        using (var ctx = host.RenderOpen())
        {
            ctx.DrawRectangle(w.Background, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            ctx.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None }, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        Exporter.Save(rtb, path);
    }

    private static async Task LibraryAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        string real = History.Folder;
        History.Folder = Path.Combine(outDir, "biblioteca");
        try
        {
            Directory.CreateDirectory(History.Folder);
            // Tres capturas de tamaños distintos, con nombres en orden cronológico.
            Exporter.Save(Solid(300, 200, 220, 40, 40), Path.Combine(History.Folder, "20260101_100000_000.png"));
            Exporter.Save(Solid(500, 120, 40, 160, 60), Path.Combine(History.Folder, "20260101_100001_000.png"));
            Exporter.Save(Solid(260, 260, 40, 80, 220), Path.Combine(History.Folder, "20260101_100002_000.png"));

            BitmapSource? opened = null;
            var lib = new LibraryWindow(new Settings { Persist = false, SaveFolder = outDir }, _ => { }, img => opened = img);
            lib.Show();
            await lib.ReloadAsync();
            if (lib.TileCount != 3) { fail($"biblioteca: muestra {lib.TileCount} capturas, esperado 3"); lib.Close(); return; }

            var files = History.Recent(10); // más reciente primero: azul, verde, rojo
            lib.Toggle(files[2]); // roja 300×200
            lib.Toggle(files[1]); // verde 500×120
            await Task.Delay(200);
            Snap(lib, Path.Combine(outDir, "ui_biblioteca.png"));

            var vertical = lib.CombineSelected(true);
            var horizontal = lib.CombineSelected(false);
            // Vertical: ancho = mayor + márgenes; alto = suma + separación + márgenes.
            bool sizeV = vertical is { PixelWidth: 500 + 32, PixelHeight: 200 + 120 + 16 + 32 };
            bool sizeH = horizontal is { PixelWidth: 300 + 500 + 16 + 32, PixelHeight: 200 + 32 };
            bool order = false, exact = false;
            if (vertical != null)
            {
                var top = Pixel(vertical, 266, 60);     // dentro de la roja (centrada: x de 116 a 416)
                var bottom = Pixel(vertical, 266, 290); // dentro de la verde
                order = top[2] == 220 && top[1] == 40 && bottom[1] == 160 && bottom[2] == 40;
                exact = Pixel(vertical, 8, 8)[0] == 255; // margen blanco
            }
            if (sizeV && sizeH && order && exact && opened != null)
                ok("biblioteca: lista 3 capturas y las combina en vertical y horizontal, en el orden marcado y sin alterar colores");
            else
                fail($"combinar: tamañoV={sizeV} tamañoH={sizeH} orden={order} margen={exact} abierta={opened != null}");
            if (vertical != null) Exporter.Save(vertical, Path.Combine(outDir, "10_combinada.png"));

            lib.DeleteSelected(confirm: false);
            await lib.ReloadAsync();
            if (lib.TileCount == 1 && History.Recent(10).Count == 1) ok("biblioteca: eliminar quita las capturas marcadas");
            else fail($"biblioteca: tras eliminar quedan {lib.TileCount} (archivos: {History.Recent(10).Count}), esperado 1");
            lib.Close();
        }
        catch (Exception ex) { fail("biblioteca: " + ex.Message); }
        finally { History.Folder = real; }
    }

    private static void HotkeysTest(string outDir, Action<string> ok, Action<string> fail)
    {
        try
        {
            bool p1 = Hotkeys.TryParse("Ctrl+Alt+G", out uint m1, out uint k1) && m1 == (Native.MOD_CONTROL | Native.MOD_ALT) && k1 == 0x47;
            bool p2 = Hotkeys.TryParse("PrintScreen", out uint m2, out uint k2) && m2 == 0 && k2 == 0x2C;
            bool p3 = Hotkeys.TryParse("Ctrl+Shift+F9", out uint m3, out uint k3) && m3 == (Native.MOD_CONTROL | Native.MOD_SHIFT) && k3 == 0x78;
            bool p4 = !Hotkeys.TryParse("Ctrl+Nada", out _, out _);
            bool f1 = Hotkeys.FromKey(Key.G, ModifierKeys.Control | ModifierKeys.Alt) == "Ctrl+Alt+G";
            bool f2 = Hotkeys.FromKey(Key.A, ModifierKeys.None) == null && Hotkeys.FromKey(Key.A, ModifierKeys.Shift) == null;
            bool f3 = Hotkeys.FromKey(Key.F8, ModifierKeys.None) == "F8";
            // Todos los atajos por defecto deben poder convertirse.
            bool defaults = Hotkeys.Defaults.Values.All(v => Hotkeys.TryParse(v, out _, out _));

            var settings = new Settings { Persist = false, SaveFolder = outDir };
            var dlg = new HotkeysWindow(settings);
            dlg.Show();
            Snap(dlg, Path.Combine(outDir, "ui_atajos.png"));
            bool clash = !dlg.TryAssign(Hotkeys.Full, "Ctrl+Shift+D");   // ya lo usa "desplazamiento"
            bool assigned = dlg.TryAssign(Hotkeys.Full, "Ctrl+Alt+F");
            dlg.Save();
            bool saved = dlg.Saved && settings.Hotkeys.Count == 1 && Hotkeys.Get(settings, Hotkeys.Full) == "Ctrl+Alt+F"
                         && Hotkeys.Get(settings, Hotkeys.Gif) == "Ctrl+Alt+G";

            if (p1 && p2 && p3 && p4 && f1 && f2 && f3 && defaults && clash && assigned && saved)
                ok("atajos: se leen, se validan (sin duplicados ni letras sueltas) y se guardan solo los cambiados");
            else
                fail($"atajos: parse={p1},{p2},{p3},{p4} teclas={f1},{f2},{f3} defaults={defaults} duplicado rechazado={clash} asignado={assigned} guardado={saved}");
        }
        catch (Exception ex) { fail("atajos: " + ex.Message); }
    }

    private static async Task EditorAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        var editor = new EditorWindow(Solid(800, 500, 255, 255, 255), new Settings { Persist = false, SaveFolder = outDir });
        editor.Show();
        var surface = (Canvas)editor.FindName("Surface");
        try
        {
            // ── Lupa: un cuadrito rojo de 20×20 debe verse ampliado (unos 40×40) ──
            var dot = new System.Windows.Shapes.Rectangle { Width = 20, Height = 20, Fill = Brushes.Red };
            Canvas.SetLeft(dot, 390); Canvas.SetTop(dot, 240);
            surface.Children.Add(dot);
            editor.UpdateLayout();
            editor.AddMagnifier(new Rect(360, 210, 80, 80));
            editor.UpdateLayout();
            await Task.Delay(200);
            var img = Exporter.Render(surface);
            Exporter.Save(img, Path.Combine(outDir, "11_lupa.png"));
            int redRun = 0;
            for (int x = 300; x < 500; x++) { var p = Pixel(img, x, 250); if (p[2] > 200 && p[1] < 60 && p[0] < 60) redRun++; }
            if (redRun is >= 36 and <= 44) ok($"lupa: la zona se ve ampliada al doble ({redRun} px de ancho frente a 20)");
            else fail($"lupa: el cuadro rojo mide {redRun} px de ancho en la lupa, esperado ≈40");

            // ── Redimensionar ──
            editor.ResizeTo(400);
            editor.UpdateLayout();
            var small = editor.RenderFinal();
            bool resized = small.PixelWidth == 400 && small.PixelHeight == 250;
            ((Button)editor.FindName("UndoBtn")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            editor.UpdateLayout();
            var back = editor.RenderFinal();
            bool undone = back.PixelWidth == 800 && back.PixelHeight == 500;
            if (resized && undone) ok("redimensionar: 800×500 → 400×250 manteniendo proporción, y deshacer la restaura");
            else fail($"redimensionar: {small.PixelWidth}x{small.PixelHeight}, tras deshacer {back.PixelWidth}x{back.PixelHeight}");
        }
        catch (Exception ex) { fail("lupa/redimensionar: " + ex.Message); }
        finally { editor.Close(); }
    }
}
