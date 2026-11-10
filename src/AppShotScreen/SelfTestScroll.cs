using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Capture;
using AppShotScreen.Editor;

namespace AppShotScreen;

/// <summary>Pruebas de la captura con desplazamiento (algoritmo sintético + ventana real).</summary>
internal static class SelfTestScroll
{
    public static async Task RunAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        SyntheticStitch(ok, fail);
        await RealWindowAsync(outDir, ok, fail);
    }

    /// <summary>Imagen alta conocida → fotogramas recortados con encabezado fijo → se debe reconstruir exacta.</summary>
    private static void SyntheticStitch(Action<string> ok, Action<string> fail)
    {
        const int w = 300, tall = 3000, view = 400, header = 50;
        var rnd = new Random(7);
        var content = new byte[w * 4 * tall];
        for (int y = 0; y < tall; y++)
        {
            // Bandas con ruido: cada fila es distinta, como un texto real.
            byte r = (byte)rnd.Next(256), g = (byte)rnd.Next(256), b = (byte)rnd.Next(256);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                content[i] = (byte)(b ^ (x * 3)); content[i + 1] = g; content[i + 2] = (byte)(r + x); content[i + 3] = 255;
            }
        }
        var headerPx = new byte[w * 4 * header];
        for (int i = 0; i < headerPx.Length; i += 4) { headerPx[i] = 200; headerPx[i + 1] = 80; headerPx[i + 2] = 20; headerPx[i + 3] = 255; }
        for (int y = 10; y < 40; y++) for (int x = 20; x < 120; x++) { int i = (y * w + x) * 4; headerPx[i] = headerPx[i + 1] = headerPx[i + 2] = 255; }

        BitmapSource Frame(int scrollY)
        {
            int stride = w * 4;
            var px = new byte[stride * view];
            Buffer.BlockCopy(headerPx, 0, px, 0, headerPx.Length);
            Buffer.BlockCopy(content, scrollY * stride, px, header * stride, (view - header) * stride);
            return BitmapSource.Create(w, view, 96, 96, PixelFormats.Bgra32, null, px, stride);
        }

        var stitcher = new ScrollStitcher(Frame(0));
        int pos = 0;
        int[] steps = { 120, 200, 150, 220, 90, 210, 180, 160 };
        foreach (int s in steps)
        {
            pos = Math.Min(pos + s, tall - (view - header));
            if (stitcher.Add(Frame(pos)) != ScrollStitcher.AddResult.Appended) { fail($"sintético: no se pudo unir en scroll={pos}"); return; }
        }
        if (stitcher.Add(Frame(pos)) != ScrollStitcher.AddResult.NoMovement) fail("sintético: no detectó el final");

        var result = stitcher.Result();
        int expected = header + pos + (view - header);
        if (result.PixelHeight != expected) { fail($"sintético: alto {result.PixelHeight} != {expected}"); return; }

        // Comparación píxel a píxel del contenido reconstruido.
        int st = w * 4;
        var got = new byte[st * result.PixelHeight];
        result.CopyPixels(got, st, 0);
        bool same = got.AsSpan(0, headerPx.Length).SequenceEqual(headerPx)
                    && got.AsSpan(header * st, (expected - header) * st).SequenceEqual(content.AsSpan(0, (expected - header) * st));
        if (same) ok($"sintético: {steps.Length} pasos unidos exactamente ({result.PixelHeight} px, encabezado fijo sin repetir)");
        else fail("sintético: el contenido reconstruido no coincide");
    }

    /// <summary>Abre una ventana real con una lista larga y la captura girando la rueda del ratón.</summary>
    private static async Task RealWindowAsync(string outDir, Action<string> ok, Action<string> fail)
    {
        const int lines = 120, lineH = 30, headerH = 44;
        var list = new StackPanel();
        for (int i = 1; i <= lines; i++)
        {
            list.Children.Add(new Border
            {
                Height = lineH,
                Background = i % 2 == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xFB)),
                Child = new TextBlock { Text = $"Línea {i}  —  APPSHOTACREEN prueba de desplazamiento #{i * 7919 % 1000}", FontSize = 15, Margin = new Thickness(10, 5, 0, 0) },
            });
        }
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, CanContentScroll = false };
        var header = new Border
        {
            Height = headerH,
            Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
            Child = new TextBlock { Text = "Encabezado fijo", Foreground = Brushes.White, FontSize = 18, Margin = new Thickness(12, 9, 0, 0) },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroller);

        var win = new Window
        {
            Title = "Prueba desplazamiento", Width = 520, Height = 420, Left = 80, Top = 80,
            WindowStyle = WindowStyle.None, Topmost = true, ResizeMode = ResizeMode.NoResize, Content = dock,
        };
        win.Show();
        win.Activate();
        await Task.Delay(500);

        var topLeft = dock.PointToScreen(new Point(0, 0));
        var bottomRight = dock.PointToScreen(new Point(dock.ActualWidth, dock.ActualHeight));
        var region = new PixelRect((int)topLeft.X, (int)topLeft.Y, (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));
        double scale = region.Height / dock.ActualHeight;

        var result = await ScrollingCapture.CaptureAsync(region, CancellationToken.None);
        win.Close();
        Exporter.Save(result, Path.Combine(outDir, "4_desplazamiento.png"));

        double expected = (headerH + lines * lineH) * scale;
        double ratio = result.PixelHeight / expected;
        if (Math.Abs(ratio - 1) <= 0.02)
            ok($"ventana real: {result.PixelWidth}x{result.PixelHeight} px (esperado ≈{expected:0}), toda la lista capturada");
        else
            fail($"ventana real: alto {result.PixelHeight} px, esperado ≈{expected:0} (ver 4_desplazamiento.png)");
    }
}
