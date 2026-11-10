using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AppShotScreen.Capture;
using AppShotScreen.Editor;
using AppShotScreen.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AppShotScreen;

public partial class App : Application
{
    private const uint VK_SNAPSHOT = 0x2C; // Impr Pant
    private const uint VK_S = 0x53;
    private const uint VK_F = 0x46;
    private const uint VK_D = 0x44;
    private const uint VK_ESCAPE = 0x1B;

    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private Settings _settings = new();
    private bool _capturing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int selfIdx = Array.IndexOf(e.Args, "--selftest");
        if (selfIdx >= 0)
        {
            string dir = selfIdx + 1 < e.Args.Length ? e.Args[selfIdx + 1] : Path.Combine(Path.GetTempPath(), "appshot-selftest");
            Dispatcher.InvokeAsync(async () => Shutdown(await SelfTest.RunAsync(dir)));
            return;
        }

        _singleInstance = new Mutex(true, "APPSHOTACREEN_single_instance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("APPSHOTACREEN ya se está ejecutando. Búscalo en la bandeja del sistema (junto al reloj).",
                "APPSHOTACREEN", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("Ocurrió un error:\n" + ex.Exception.Message, "APPSHOTACREEN", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        _settings = Settings.Load();
        CreateTray();
        string hotkeyInfo = RegisterHotkeys();

        // Modo de prueba / "Abrir con": APPSHOTACREEN.exe --open imagen.png
        int openIdx = Array.IndexOf(e.Args, "--open");
        if (openIdx >= 0 && openIdx + 1 < e.Args.Length) OpenImageFile(e.Args[openIdx + 1]);
        else if (e.Args.Contains("--capture")) _ = CaptureRegionAsync();

        _tray!.ShowBalloonTip(4000, "APPSHOTACREEN está listo", hotkeyInfo, Forms.ToolTipIcon.Info);
    }

    private string RegisterHotkeys()
    {
        _hotkeys = new HotkeyManager();
        var ok = new List<string>();
        if (_hotkeys.Register(0, VK_SNAPSHOT, () => _ = CaptureRegionAsync())) ok.Add("Impr Pant = región");
        if (_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, VK_S, () => _ = CaptureRegionAsync())) ok.Add("Ctrl+Shift+S = región");
        if (_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, VK_F, () => _ = CaptureFullScreenAsync())) ok.Add("Ctrl+Shift+F = pantalla completa");
        if (_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, VK_D, () => _ = CaptureScrollingAsync())) ok.Add("Ctrl+Shift+D = con desplazamiento");
        return ok.Count > 0
            ? string.Join("\n", ok)
            : "No se pudieron registrar atajos (otra app los usa). Usa el icono de la bandeja.";
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Capturar región", null, (_, _) => _ = CaptureRegionAsync());
        menu.Items.Add("Capturar pantalla completa", null, (_, _) => _ = CaptureFullScreenAsync());
        menu.Items.Add("Captura con desplazamiento", null, (_, _) => _ = CaptureScrollingAsync());
        menu.Items.Add("Capturar región en 3 s", null, (_, _) => _ = CaptureRegionAsync(TimeSpan.FromSeconds(3)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir imagen en el editor…", null, (_, _) => PickAndOpenImage());
        menu.Items.Add("Abrir carpeta de capturas", null, (_, _) => OpenSaveFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "APPSHOTACREEN — Impr Pant para capturar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _ = CaptureRegionAsync(); };
    }

    // ───────────────────────── Capturas ─────────────────────────

    private async Task CaptureRegionAsync(TimeSpan? delay = null)
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            // Pequeña espera para que se cierre el menú de la bandeja antes de "congelar" la pantalla.
            await Task.Delay(delay ?? TimeSpan.FromMilliseconds(150));
            var bounds = ScreenCapture.VirtualScreen;
            var frozen = ScreenCapture.Capture(bounds);
            var overlay = new OverlayWindow(frozen, bounds);
            overlay.ShowDialog();
            if (overlay.Result is { } shot) HandleCapture(shot);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task CaptureScrollingAsync()
    {
        if (_capturing) return;
        _capturing = true;
        int escId = 0;
        RegionFrameWindow? frame = null;
        ScrollPanelWindow? panel = null;
        try
        {
            await Task.Delay(150);
            var bounds = ScreenCapture.VirtualScreen;
            var overlay = new OverlayWindow(ScreenCapture.Capture(bounds), bounds)
            {
                HintText = "Captura con desplazamiento: selecciona la zona que vas a desplazar  ·  Esc = cancelar",
            };
            overlay.ShowDialog();
            if (overlay.SelectedScreenRect is not { } region) return;
            await Task.Delay(250); // deja que la ventana de selección desaparezca por completo

            // Como Snagit: el usuario desplaza con la rueda y detiene con el botón (o Esc).
            var session = new ScrollSession(region);
            frame = new RegionFrameWindow(region);
            panel = new ScrollPanelWindow(region);
            bool cancelled = false;
            var finishing = panel; // para capturar en los manejadores

            session.HeightChanged += h => finishing.SetHeight(h);
            session.AutoReachedEnd += () =>
            {
                finishing.SetAutoVisual(false);
                finishing.SetMessage("Llegó al final  ·  pulsa Detener y guardar");
            };
            panel.StopRequested += session.Stop;
            panel.CancelRequested += () => { cancelled = true; session.Stop(); };
            panel.AutoToggled += session.SetAuto;
            escId = _hotkeys!.RegisterTemporary(0, VK_ESCAPE, session.Stop);

            frame.Show();
            panel.Show();
            var result = await session.RunAsync();
            frame.Close();
            panel.Close();
            frame = null;
            panel = null;
            if (!cancelled) HandleCapture(result);
        }
        finally
        {
            frame?.Close();
            panel?.Close();
            _hotkeys?.Unregister(escId);
            _capturing = false;
        }
    }

    private async Task CaptureFullScreenAsync()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            await Task.Delay(150);
            HandleCapture(ScreenCapture.CaptureCurrentMonitor());
        }
        finally
        {
            _capturing = false;
        }
    }

    private void HandleCapture(BitmapSource shot)
    {
        if (_settings.CopyToClipboardAfterCapture)
        {
            try { Exporter.CopyToClipboard(shot); } catch { /* portapapeles ocupado: no es crítico */ }
        }
        if (_settings.OpenEditorAfterCapture) OpenEditor(shot);
        else
        {
            string path = _settings.NextFilePath();
            Exporter.SavePng(shot, path);
            _tray?.ShowBalloonTip(2500, "Captura guardada", path, Forms.ToolTipIcon.Info);
        }
    }

    private void OpenEditor(BitmapSource image)
    {
        var editor = new EditorWindow(image, _settings);
        editor.Show();
        editor.Activate();
    }

    private void PickAndOpenImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dlg.ShowDialog() == true) OpenImageFile(dlg.FileName);
    }

    private void OpenImageFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.EndInit();
            // Normalizamos a 96 DPI y BGRA para que el editor trabaje en píxeles.
            var conv = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = conv.PixelWidth * 4;
            var px = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(px, stride, 0);
            var img = BitmapSource.Create(conv.PixelWidth, conv.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, stride);
            img.Freeze();
            OpenEditor(img);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo abrir la imagen:\n" + ex.Message, "APPSHOTACREEN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSaveFolder()
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
    }

    /// <summary>Icono dibujado en código (un visor con esquinas), para no depender de archivos.</summary>
    private static Drawing.Icon BuildIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x2F, 0x6F, 0xED));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Drawing.Pen(Drawing.Color.White, 2.6f);
            // Esquinas de "selección"
            g.DrawLines(pen, new[] { new Drawing.PointF(8, 13), new(8, 8), new(13, 8) });
            g.DrawLines(pen, new[] { new Drawing.PointF(19, 8), new(24, 8), new(24, 13) });
            g.DrawLines(pen, new[] { new Drawing.PointF(24, 19), new(24, 24), new(19, 24) });
            g.DrawLines(pen, new[] { new Drawing.PointF(13, 24), new(8, 24), new(8, 19) });
            using var dot = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillEllipse(dot, 13, 13, 6, 6);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
