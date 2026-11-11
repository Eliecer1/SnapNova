using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppShotScreen.Capture;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Pruebas de: grabación GIF, mover anotaciones, globos y sellos, arrastrar a otras apps.</summary>
internal static class SelfTestMore
{
    public static async Task RunAsync(string outDir, BitmapSource shot, Action<string> ok, Action<string> fail)
    {
        await GifAsync(outDir, ok, fail);
        await EditorAsync(outDir, shot, ok, fail);
    }

    private static byte[] Pixel(BitmapSource img, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(new CroppedBitmap(img, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0).CopyPixels(px, 4, 0);
        return px;
    }

    private static bool IsRed(byte[] p) => p[2] > 170 && p[1] < 90 && p[0] < 90;

    /// <summary>Graba 2,5 s de una ventana con un contador que cambia y comprueba el GIF resultante.</summary>
    private static async Task GifAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        try
        {
            var label = new TextBlock { FontSize = 64, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 24, Width = 20, Background = Brushes.Orange, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x3A, 0x8F)) };
            grid.Children.Add(label);
            grid.Children.Add(bar);
            var win = new Window { Width = 360, Height = 220, Left = 120, Top = 120, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, Content = grid };
            win.Show();
            int n = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) => { n++; label.Text = n.ToString(); bar.Width = 20 + n * 22; };
            timer.Start();
            await Task.Delay(400);

            var tl = grid.PointToScreen(new Point(0, 0));
            var br = grid.PointToScreen(new Point(grid.ActualWidth, grid.ActualHeight));
            var region = new PixelRect((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));
            var rec = new GifRecorder(region);
            var run = rec.RunAsync();
            await Task.Delay(2500);
            rec.Stop();
            await run;
            timer.Stop();
            win.Close();

            string path = Path.Combine(outDir, "8_grabacion.gif");
            await rec.SaveAsync(path);
            var bytes = File.ReadAllBytes(path);
            bool loops = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64)).Contains("NETSCAPE2.0");
            var dec = new GifBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int frames = dec.Frames.Count;
            int totalCs = 0;
            foreach (var f in dec.Frames)
                if (f.Metadata is BitmapMetadata m && m.GetQuery("/grctlext/Delay") is ushort d) totalCs += d;
            bool sizeOk = dec.Frames[0].PixelWidth == region.Width && dec.Frames[0].PixelHeight == region.Height;

            // Unos 12 cambios de contador en 2,5 s; exigimos bastantes fotogramas distintos y una duración creíble.
            if (frames >= 6 && loops && sizeOk && totalCs is >= 200 and <= 500)
                ok($"GIF: {frames} fotogramas, {totalCs / 100.0:0.0} s, en bucle, {bytes.Length / 1024} KB");
            else
                fail($"GIF: fotogramas={frames}, bucle={loops}, tamaño ok={sizeOk}, duración={totalCs} cs");
        }
        catch (Exception ex) { fail("GIF: " + ex.Message); }
    }

    private static async Task EditorAsync(string outDir, BitmapSource shot, Action<string> ok, Action<string> fail)
    {
        // Lienzo blanco para comprobar colores sin que interfiera el contenido de la pantalla.
        int w = 800, h = 500;
        var white = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, w * h * 4).ToArray(), w * 4);
        white.Freeze();
        var editor = new EditorWindow(white, new Settings { Persist = false, SaveFolder = outDir, ToolColor = "#FFE53935" });
        editor.Show();
        var surface = (Canvas)editor.FindName("Surface");
        try
        {
            // ── Mover ──
            var rect = Annotations.Rectangle(new Rect(50, 50, 100, 60), Brushes.Red, 6);
            surface.Children.Add(rect);
            editor.UpdateLayout();
            bool before = IsRed(Pixel(Exporter.Render(surface), 100, 50));
            editor.Select(rect);
            editor.MoveAnnotation(rect, new Vector(300, 200));
            editor.UpdateLayout();
            var moved = Exporter.Render(surface);
            bool oldGone = !IsRed(Pixel(moved, 100, 50));
            bool newThere = IsRed(Pixel(moved, 400, 250));
            if (before && oldGone && newThere) ok("mover: la anotación se desplaza y se exporta en su nueva posición");
            else fail($"mover: antes={before}, origen vacío={oldGone}, destino={newThere}");

            bool deleted = editor.DeleteSelected();
            editor.UpdateLayout();
            bool goneAfterDelete = !IsRed(Pixel(Exporter.Render(surface), 400, 250));
            if (deleted && goneAfterDelete) ok("borrar: Supr elimina la anotación seleccionada");
            else fail($"borrar: eliminado={deleted}, desaparece={goneAfterDelete}");

            // ── Globo y sellos ──
            editor.AddCallout(new Point(120, 400), new Point(300, 300), "Mira aquí");
            double x = 80;
            foreach (var (kind, _) in Stamps.Kinds) { editor.AddStamp(kind, new Point(x, 120)); x += 90; }
            editor.UpdateLayout();
            await Task.Delay(300);
            var img = Exporter.Render(surface);
            Exporter.Save(img, Path.Combine(outDir, "9_globo_y_sellos.png"));
            bool bubble = IsRed(Pixel(img, 300 - 40, 300 - 13));
            bool tail = IsRed(Pixel(img, 210, 350)); // a mitad de camino entre la punta y el globo
            var okStamp = Pixel(img, 80, 120 - 19);   // parte alta del disco verde
            bool green = okStamp[1] > 140 && okStamp[2] < 120;
            if (bubble && tail && green) ok("globo de texto con cola y 6 sellos dibujados");
            else fail($"globo/sellos: globo={bubble}, cola={tail}, sello verde={green} (ver 9_globo_y_sellos.png)");

            // ── Arrastrar a otras apps: el archivo temporal debe existir y ser la imagen final ──
            string tmp = editor.ExportTempFile();
            var dec = new PngBitmapDecoder(new Uri(tmp), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (File.Exists(tmp) && dec.PixelWidth == w && dec.PixelHeight == h) ok("arrastrar: genera el archivo PNG listo para soltar en otra app");
            else fail($"arrastrar: archivo={File.Exists(tmp)} {dec.PixelWidth}x{dec.PixelHeight}");
            try { File.Delete(tmp); } catch { /* no importa */ }

            editor.UpdateLayout();
            var content = (FrameworkElement)editor.Content;
            var rtb = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);
            Exporter.Save(rtb, Path.Combine(outDir, "ui_editor2.png"));
        }
        catch (Exception ex) { fail("editor (mover/globos/sellos): " + ex.Message); }
        finally { editor.Close(); }
    }
}
