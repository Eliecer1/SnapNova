using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
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
    private const uint VK_T = 0x54;
    private const uint VK_P = 0x50;

    private HomeWindow? _home;
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
            bool fast = e.Args.Contains("--fast");
            Dispatcher.InvokeAsync(async () => Shutdown(await SelfTest.RunAsync(dir, fast)));
            return;
        }

        int stIdx = Array.IndexOf(e.Args, "--scrolltest");
        if (stIdx >= 0)
        {
            // Diagnóstico: --scrolltest x y w h salida.png  (simula la rueda sobre esa zona)
            var a = e.Args.Skip(stIdx + 1).ToArray();
            var region = new PixelRect(int.Parse(a[0]), int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]));
            Dispatcher.InvokeAsync(async () =>
            {
                var session = new ScrollSession(region);
                var run = session.RunAsync();
                ScrollingCapture.MoveCursorToCenter(region);
                for (int i = 0; i < 70; i++) { ScrollingCapture.Wheel(-120); await Task.Delay(160); }
                await Task.Delay(600);
                session.Stop();
                var img = await run;
                Exporter.Save(img, a[4]);
                File.WriteAllText(a[4] + ".txt", $"alto={img.PixelHeight} unidos={session.Appended} sinCoincidencia={session.NoMatches} quietos={session.Stills}");
                Shutdown();
            });
            return;
        }

        _singleInstance = new Mutex(true, "SnapNova_single_instance", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "SnapNova_show_home");
        if (!isFirst)
        {
            // Ya está abierta: le pedimos a esa copia que muestre su ventana de inicio y salimos.
            showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowHome);
        }) { IsBackground = true, Name = "show-home-listener" }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("Ocurrió un error:\n" + ex.Exception.Message, "SnapNova", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        Brand.MigrateFromOldName();
        _settings = Settings.Load();
        Brand.FixSaveFolder(_settings);
        CreateTray();
        string hotkeyInfo = RegisterHotkeys();

        // Modo de prueba / "Abrir con": SnapNova.exe --open imagen.png
        int openIdx = Array.IndexOf(e.Args, "--open");
        if (openIdx >= 0 && openIdx + 1 < e.Args.Length) OpenImageFile(e.Args[openIdx + 1]);
        else if (e.Args.Contains("--capture")) _ = CaptureRegionAsync();
        else if (e.Args.Contains("--tray")) _tray!.ShowBalloonTip(4000, "SnapNova está listo", hotkeyInfo, Forms.ToolTipIcon.Info);
        else ShowHome(); // inicio normal: la ventana principal; con --tray (arranque con Windows) queda en la bandeja
    }

    private void ShowHome()
    {
        if (_home == null)
        {
            _home = new HomeWindow(_settings);
            _home.Closed += (_, _) => _home = null;
            _home.OpenFileRequested += OpenImageFile;
            _home.ModeRequested += mode =>
            {
                switch (mode)
                {
                    case HomeWindow.ModeRegion: _ = CaptureRegionAsync(); break;
                    case HomeWindow.ModeFull: _ = CaptureFullScreenAsync(); break;
                    case HomeWindow.ModeScroll: _ = CaptureScrollingAsync(); break;
                    case HomeWindow.ModeText: _ = CaptureTextAsync(); break;
                    case HomeWindow.ModePin: _ = CapturePinAsync(); break;
                    case HomeWindow.ModeGif: _ = CaptureGifAsync(); break;
                    case HomeWindow.ModeDelay: _ = CaptureRegionAsync(TimeSpan.FromSeconds(3)); break;
                    case HomeWindow.ModeOpen: PickAndOpenImage(); break;
                    case HomeWindow.ModeLibrary: ShowLibrary(); break;
                    case HomeWindow.ModeHotkeys: ShowHotkeysDialog(); break;
                }
            };
        }
        _home.Show();
        if (_home.WindowState == WindowState.Minimized) _home.WindowState = WindowState.Normal;
        _home.Activate();
    }

    /// <summary>
    /// Congela la pantalla y deja elegir región o ventana. Oculta antes la ventana de inicio
    /// para que no salga en la captura.
    /// </summary>
    private async Task<OverlayWindow> SelectAsync(TimeSpan? delay = null, string? hint = null)
    {
        bool homeWasVisible = _home is { IsVisible: true };
        if (homeWasVisible) _home!.Hide();
        // Espera a que se cierren el menú de la bandeja o la ventana de inicio antes de "congelar".
        await Task.Delay(delay ?? TimeSpan.FromMilliseconds(homeWasVisible ? 300 : 150));

        var windows = WindowFinder.Snapshot();
        var bounds = ScreenCapture.VirtualScreen;
        var overlay = new OverlayWindow(ScreenCapture.Capture(bounds), bounds, windows);
        if (hint != null) overlay.HintText = hint;
        overlay.ShowDialog();

        if (overlay.PickedColor is { } color)
        {
            ToastWindow.Show(await ClipboardHelper.TrySetTextAsync(color) ? "Color copiado" : "No se pudo copiar (portapapeles ocupado)", color);
        }
        return overlay;
    }

    /// <summary>Selecciona una zona y copia su texto (OCR) al portapapeles.</summary>
    private async Task CaptureTextAsync()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            var overlay = await SelectAsync(hint: "Copiar texto: selecciona la zona con el texto  ·  Clic = ventana completa  ·  Esc = cancelar");
            if (overlay.Result is not { } shot) return;
            string text = await OcrService.RecognizeAsync(shot);
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastWindow.Show("No se encontró texto", "Prueba con una zona más grande o con letras más nítidas.");
                return;
            }
            if (await ClipboardHelper.TrySetTextAsync(text))
            {
                ToastWindow.Show("Texto copiado al portapapeles", text.Replace(Environment.NewLine, " "));
            }
            else
            {
                // Otra app tiene el portapapeles ocupado: mostramos el texto para copiarlo a mano.
                new OcrResultWindow(text, copied: false).Show();
            }
        }
        catch (Exception ex)
        {
            ToastWindow.Show("No se pudo leer el texto", ex.Message);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>Selecciona una zona y la deja fijada encima, justo donde estaba.</summary>
    private async Task CapturePinAsync()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            var overlay = await SelectAsync(hint: "Fijar en pantalla: selecciona la zona que quieres dejar a la vista  ·  Esc = cancelar");
            if (overlay.Result is not { } shot) return;
            History.Add(shot);
            PinWindow.Show(shot, _settings, overlay.SelectedScreenRect);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>Atajos que Windows rechazó la última vez (normalmente porque otra app ya los usa).</summary>
    private List<string> _hotkeyFailures = new();

    private string RegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        _hotkeys.UnregisterAll();
        var actions = new Dictionary<string, Action>
        {
            [Hotkeys.Region] = () => _ = CaptureRegionAsync(),
            [Hotkeys.Region2] = () => _ = CaptureRegionAsync(),
            [Hotkeys.Full] = () => _ = CaptureFullScreenAsync(),
            [Hotkeys.Scroll] = () => _ = CaptureScrollingAsync(),
            [Hotkeys.Text] = () => _ = CaptureTextAsync(),
            [Hotkeys.Pin] = () => _ = CapturePinAsync(),
            [Hotkeys.Gif] = () => _ = CaptureGifAsync(),
        };
        var ok = new List<string>();
        _hotkeyFailures = new List<string>();
        foreach (var (id, label) in Hotkeys.Actions)
        {
            string combo = Hotkeys.Get(_settings, id);
            if (string.IsNullOrEmpty(combo)) continue; // desactivado por el usuario
            if (Hotkeys.TryParse(combo, out uint mods, out uint vk) && _hotkeys.Register(mods, vk, actions[id]))
                ok.Add($"{Hotkeys.Display(combo)} = {label}");
            else
                _hotkeyFailures.Add($"{Hotkeys.Display(combo)} ({label})");
        }
        return ok.Count > 0
            ? string.Join("\n", ok)
            : "No se pudieron registrar atajos (otra app los usa). Usa el icono de la bandeja.";
    }

    private void ShowHotkeysDialog()
    {
        // Mientras se editan, los atajos globales se desactivan para que las teclas lleguen al cuadro.
        _hotkeys?.UnregisterAll();
        var dlg = new HotkeysWindow(_settings) { Owner = _home is { IsVisible: true } ? _home : null };
        dlg.ShowDialog();
        RegisterHotkeys();
        if (_hotkeyFailures.Count > 0)
            ToastWindow.Show("Algunos atajos están ocupados por otra app", string.Join(", ", _hotkeyFailures));
        else if (dlg.Saved)
            ToastWindow.Show("Atajos actualizados");
        if (dlg.Saved && _home != null)
        {
            // La ventana de inicio muestra los atajos en las tarjetas: la recreamos.
            _home.Close();
            ShowHome();
        }
    }

    private LibraryWindow? _library;

    private void ShowLibrary()
    {
        if (_library == null)
        {
            _library = new LibraryWindow(_settings, OpenImageFile, OpenEditor);
            _library.Closed += (_, _) => _library = null;
        }
        _library.Show();
        if (_library.WindowState == WindowState.Minimized) _library.WindowState = WindowState.Normal;
        _library.Activate();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir SnapNova", null, (_, _) => ShowHome());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Capturar región o ventana", null, (_, _) => _ = CaptureRegionAsync());
        menu.Items.Add("Capturar pantalla completa", null, (_, _) => _ = CaptureFullScreenAsync());
        menu.Items.Add("Captura con desplazamiento", null, (_, _) => _ = CaptureScrollingAsync());
        menu.Items.Add("Copiar texto de la pantalla", null, (_, _) => _ = CaptureTextAsync());
        menu.Items.Add("Capturar y fijar en pantalla", null, (_, _) => _ = CapturePinAsync());
        menu.Items.Add("Grabar GIF animado", null, (_, _) => _ = CaptureGifAsync());
        menu.Items.Add("Capturar región en 3 s", null, (_, _) => _ = CaptureRegionAsync(TimeSpan.FromSeconds(3)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Biblioteca de capturas", null, (_, _) => ShowLibrary());
        menu.Items.Add("Abrir imagen en el editor…", null, (_, _) => PickAndOpenImage());
        menu.Items.Add("Atajos de teclado…", null, (_, _) => ShowHotkeysDialog());
        menu.Items.Add("Abrir carpeta de capturas", null, (_, _) => OpenSaveFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "SnapNova — Impr Pant para capturar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowHome(); };
    }

    // ───────────────────────── Capturas ─────────────────────────

    private async Task CaptureRegionAsync(TimeSpan? delay = null)
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            var overlay = await SelectAsync(delay);
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
            var overlay = await SelectAsync(hint: "Captura con desplazamiento: arrastra la zona que vas a desplazar, o haz clic en la ventana  ·  Esc = cancelar");
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
            bool homeWasVisible = _home is { IsVisible: true };
            if (homeWasVisible) _home!.Hide();
            await Task.Delay(homeWasVisible ? 300 : 150);
            HandleCapture(ScreenCapture.CaptureCurrentMonitor());
        }
        finally
        {
            _capturing = false;
        }
    }

    private void HandleCapture(BitmapSource shot)
    {
        History.Add(shot);
        if (_settings.CopyToClipboardAfterCapture)
        {
            try { Exporter.CopyToClipboard(shot); } catch { /* portapapeles ocupado: no es crítico */ }
        }
        string? saved = null;
        if (_settings.AutoSaveAfterCapture)
        {
            try { saved = _settings.NextFilePath(); Exporter.SavePng(shot, saved); }
            catch (Exception ex) { saved = null; ToastWindow.Show("No se pudo guardar la captura", ex.Message); }
        }
        if (_settings.OpenEditorAfterCapture) OpenEditor(shot);

        // Sin editor, avisamos de lo que pasó con la captura para que no parezca que no hizo nada.
        if (saved != null && !_settings.OpenEditorAfterCapture)
            ToastWindow.Show("Captura guardada", Path.GetFileName(saved) + (_settings.CopyToClipboardAfterCapture ? "  ·  también copiada" : ""));
        else if (!_settings.OpenEditorAfterCapture)
            ToastWindow.Show(_settings.CopyToClipboardAfterCapture ? "Captura copiada al portapapeles" : "Captura guardada en el historial",
                "Ábrela desde la ventana de inicio si quieres editarla.");
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
            MessageBox.Show("No se pudo abrir la imagen:\n" + ex.Message, "SnapNova", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSaveFolder()
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
    }

    /// <summary>Icono de la app para las ventanas (barra de título y barra de tareas).</summary>
    public static readonly ImageSource AppIconImage = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

    private static Drawing.Icon LoadTrayIcon()
    {
        var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (res == null) return BuildIcon();
        using var s = res.Stream;
        return new Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
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
