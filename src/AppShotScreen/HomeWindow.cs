using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Ventana de inicio: modos de captura en tarjetas y las capturas recientes.</summary>
public sealed class HomeWindow : Window
{
    public const string ModeRegion = "region", ModeFull = "full", ModeScroll = "scroll",
        ModeText = "text", ModePin = "pin", ModeDelay = "delay", ModeOpen = "open", ModeGif = "gif";

    /// <summary>El usuario eligió un modo de captura (uno de los Mode*).</summary>
    public event Action<string>? ModeRequested;
    /// <summary>El usuario quiere abrir una captura del historial.</summary>
    public event Action<string>? OpenFileRequested;

    private readonly WrapPanel _recent = new() { Margin = new Thickness(-6, 0, -6, 0) };
    private readonly TextBlock _emptyRecent;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static CheckBox Option(string text, bool value, Action<bool> set, Settings settings)
    {
        var cb = new CheckBox
        {
            Content = text, IsChecked = value, Style = (Style)Application.Current.FindResource("Switch"), Margin = new Thickness(10, 10, 10, 10),
        };
        cb.Click += (_, _) =>
        {
            set(cb.IsChecked == true);
            try { settings.Save(); } catch { /* no crítico */ }
        };
        return cb;
    }

    public HomeWindow(Settings? settings = null)
    {
        Title = "APPSHOTACREEN";
        Icon = App.AppIconImage;
        Width = 860;
        Height = 760;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Res("Bg0");
        Foreground = Res("Text");
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        WindowFinder.UseDarkTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 24) };

        // ── Cabecera ──
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var logo = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Background = Res("AccentGradient"), Margin = new Thickness(0, 0, 14, 0),
            Child = Icon24("IcoRegion", Brushes.White, 2.2),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.55, Color = Color.FromRgb(0x5B, 0x8C, 0xFF) },
        };
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "APPSHOTACREEN", FontSize = 22, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = "Captura, anota y comparte en segundos", FontSize = 13, Foreground = Res("TextDim"), Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(titles);
        root.Children.Add(header);

        // ── Modos de captura ──
        root.Children.Add(SectionTitle("NUEVA CAPTURA"));
        var cards = new WrapPanel { Margin = new Thickness(-6, 4, -6, 8) };
        cards.Children.Add(Card("IcoRegion", "Región o ventana", "Impr Pant", ModeRegion, primary: true));
        cards.Children.Add(Card("IcoScroll", "Desplazamiento", "Ctrl+Shift+D", ModeScroll));
        cards.Children.Add(Card("IcoScreen", "Pantalla completa", "Ctrl+Shift+F", ModeFull));
        cards.Children.Add(Card("IcoGif", "Grabar GIF", "Ctrl+Alt+G", ModeGif));
        cards.Children.Add(Card("IcoOcr", "Copiar texto", "Ctrl+Alt+T", ModeText));
        cards.Children.Add(Card("IcoPin", "Capturar y fijar", "Ctrl+Alt+P", ModePin));
        cards.Children.Add(Card("IcoTimer", "Retardo de 3 s", "Para menús abiertos", ModeDelay));
        root.Children.Add(cards);

        // ── Qué hacer después de capturar ──
        if (settings != null)
        {
            root.Children.Add(new TextBlock
            {
                Text = "DESPUÉS DE CAPTURAR", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res("TextDim"), Margin = new Thickness(0, 14, 0, 8),
            });
            var options = new UniformGrid { Rows = 1 };
            options.Children.Add(Option("Abrir en el editor", settings.OpenEditorAfterCapture, v => settings.OpenEditorAfterCapture = v, settings));
            options.Children.Add(Option("Copiar al portapapeles", settings.CopyToClipboardAfterCapture, v => settings.CopyToClipboardAfterCapture = v, settings));
            options.Children.Add(Option("Guardar automáticamente", settings.AutoSaveAfterCapture, v => settings.AutoSaveAfterCapture = v, settings));
            root.Children.Add(new Border
            {
                Background = Res("Bg1"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 4, 8, 4), Child = options,
            });
        }

        // ── Recientes ──
        var recentHeader = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
        var open = LinkButton("Abrir imagen…", () => ModeRequested?.Invoke(ModeOpen));
        var folder = LinkButton("Ver historial", () =>
        {
            Directory.CreateDirectory(History.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{History.Folder}\"") { UseShellExecute = true });
        });
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(folder, Dock.Right);
        recentHeader.Children.Add(open);
        recentHeader.Children.Add(folder);
        recentHeader.Children.Add(SectionTitle("RECIENTES"));
        root.Children.Add(recentHeader);
        _emptyRecent = new TextBlock
        {
            Text = "Aquí aparecerán tus últimas capturas. Haz la primera con Impr Pant.",
            Foreground = Res("TextDim"), FontSize = 13, Margin = new Thickness(0, 10, 0, 0),
        };
        root.Children.Add(_emptyRecent);
        root.Children.Add(_recent);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        History.Changed += OnHistoryChanged;
        Closed += (_, _) => History.Changed -= OnHistoryChanged;
        Activated += (_, _) => RefreshRecent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        RefreshRecent();
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(RefreshRecent);

    public int RecentCount => _recent.Children.Count;

    public void RefreshRecent()
    {
        var files = History.Recent(8);
        _recent.Children.Clear();
        _emptyRecent.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var file in files)
        {
            try { _recent.Children.Add(Thumb(file)); }
            catch { /* archivo dañado o en uso: lo saltamos */ }
        }
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res("TextDim"), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Path Icon24(string key, Brush stroke, double thickness = 1.7) => new()
    {
        Data = (Geometry)Application.Current.FindResource(key),
        Stroke = stroke, StrokeThickness = thickness, Width = 24, Height = 24,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Card(string icon, string title, string hint, string mode, bool primary = false)
    {
        var iconBox = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12),
            Background = primary ? Res("AccentGradient") : Res("Bg3"),
            Child = Icon24(icon, Brushes.White, 1.9),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var stack = new StackPanel();
        stack.Children.Add(iconBox);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 3) });
        stack.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Res("TextDim") });

        var normal = Res("Bg1");
        var hover = Res("Bg2");
        var card = new Border
        {
            Width = 183, Margin = new Thickness(6), Padding = new Thickness(16, 16, 12, 16), CornerRadius = new CornerRadius(14),
            Background = normal, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = stack,
        };
        card.MouseEnter += (_, _) => { card.Background = hover; card.BorderBrush = Res("Accent"); };
        card.MouseLeave += (_, _) => { card.Background = normal; card.BorderBrush = Res("Line"); };
        card.MouseLeftButtonUp += (_, _) => ModeRequested?.Invoke(mode);
        return card;
    }

    private static Button LinkButton(string text, Action action)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("GhostButton"), Height = 30, Foreground = Res("Accent") };
        b.Click += (_, _) => action();
        return b;
    }

    private Border Thumb(string file)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo bloqueado
        bmp.DecodePixelWidth = 360;
        bmp.UriSource = new Uri(file);
        bmp.EndInit();
        bmp.Freeze();

        var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Width = 180, Height = 112 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var clip = new Border
        {
            Width = 180, Height = 112, CornerRadius = new CornerRadius(10), Background = Res("Bg2"),
            Child = img, Clip = new RectangleGeometry(new Rect(0, 0, 180, 112), 10, 10),
        };
        var when = new TextBlock
        {
            Text = File.GetLastWriteTime(file).ToString("d MMM  HH:mm"), FontSize = 11.5, Foreground = Res("TextDim"), Margin = new Thickness(2, 6, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(clip);
        stack.Children.Add(when);
        var card = new Border
        {
            Margin = new Thickness(6), Padding = new Thickness(4), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, Child = stack, ToolTip = "Abrir en el editor",
        };
        card.MouseEnter += (_, _) => card.Background = Res("Bg2");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, _) => OpenFileRequested?.Invoke(file);
        return card;
    }
}
