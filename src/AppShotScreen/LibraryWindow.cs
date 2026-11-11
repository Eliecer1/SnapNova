using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Editor;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>
/// Biblioteca: todas las capturas del historial. Un clic selecciona (se pueden marcar varias),
/// doble clic abre en el editor. Desde aquí se copian, eliminan o combinan en una sola imagen.
/// </summary>
public sealed class LibraryWindow : Window
{
    private readonly Settings _settings;
    private readonly Action<string> _openFile;
    private readonly Action<BitmapSource> _openImage;
    private readonly WrapPanel _grid = new() { Margin = new Thickness(14, 8, 14, 14) };
    private readonly List<string> _selected = new(); // en el orden en que se marcaron
    private readonly Dictionary<string, Border> _tiles = new();
    private readonly TextBlock _info;
    private readonly Button _open, _copy, _combineV, _combineH, _delete;
    private int _loadVersion;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public LibraryWindow(Settings settings, Action<string> openFile, Action<BitmapSource> openImage)
    {
        _settings = settings;
        _openFile = openFile;
        _openImage = openImage;
        Title = $"{Brand.Name} - Biblioteca";
        Icon = App.AppIconImage;
        Width = 1040;
        Height = 720;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Res("Bg0");
        Foreground = Res("Text");
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        WindowFinder.UseDarkTitleBar(this);

        Button Btn(string text, string? icon, Action action, string tip, string style = "SoftButton")
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), ToolTip = tip };
            if (icon != null) b.Tag = Application.Current.FindResource(icon);
            b.Click += (_, _) => action();
            return b;
        }
        _open = Btn("Abrir", "IcoImage", OpenSelected, "Abre la captura en el editor (doble clic)");
        _copy = Btn("Copiar", "IcoCopy", CopySelected, "Copia la captura al portapapeles");
        _combineV = Btn("Combinar ↓", null, () => CombineSelected(true), "Une las capturas marcadas una debajo de otra, en el orden en que las marcaste");
        _combineH = Btn("Combinar →", null, () => CombineSelected(false), "Une las capturas marcadas una al lado de otra");
        _delete = Btn("Eliminar", null, () => DeleteSelected(), "Elimina del historial las capturas marcadas (Supr)");
        var folder = Btn("Carpeta", "IcoFolder", () =>
        {
            Directory.CreateDirectory(History.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{History.Folder}\"") { UseShellExecute = true });
        }, "Abre la carpeta del historial", "GhostButton");

        _info = new TextBlock { Foreground = Res("TextDim"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _open, _copy, _combineV, _combineH, _delete, folder }) actions.Children.Add(b);
        var bar = new DockPanel { Margin = new Thickness(18, 12, 18, 4) };
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        var titles = new StackPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(new TextBlock { Text = "Biblioteca", FontSize = 20, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        titles.Children.Add(_info);
        bar.Children.Add(titles);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) DeleteSelected(true);
            else if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) OpenSelected();
        };
        History.Changed += OnHistoryChanged;
        Closed += (_, _) => History.Changed -= OnHistoryChanged;
        _ = ReloadAsync();
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(() => { _ = ReloadAsync(); });

    internal int TileCount => _tiles.Count;
    internal IReadOnlyList<string> Selected => _selected;

    /// <summary>Carga las miniaturas poco a poco para que la ventana aparezca al instante.</summary>
    internal async Task ReloadAsync()
    {
        int version = ++_loadVersion;
        var files = History.Recent(int.MaxValue);
        _grid.Children.Clear();
        _tiles.Clear();
        _selected.RemoveAll(f => !files.Contains(f));
        UpdateBar(files.Count);

        foreach (var file in files)
        {
            BitmapSource? thumb = await Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 400;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    bmp.Freeze();
                    return (BitmapSource)bmp;
                }
                catch { return null; }
            });
            if (version != _loadVersion) return; // empezó otra recarga
            if (thumb == null) continue;
            var tile = Tile(file, thumb);
            _tiles[file] = tile;
            _grid.Children.Add(tile);
        }
        RefreshSelectionVisuals();
    }

    private Border Tile(string file, BitmapSource thumb)
    {
        var img = new Image { Source = thumb, Stretch = Stretch.UniformToFill, Width = 220, Height = 138 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var picture = new Border { Width = 220, Height = 138, Background = Res("Bg2"), Child = img, Clip = new RectangleGeometry(new Rect(0, 0, 220, 138), 9, 9) };
        var badge = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Res("Accent"), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), Visibility = Visibility.Collapsed,
            Child = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var layers = new Grid();
        layers.Children.Add(picture);
        layers.Children.Add(badge);
        var stack = new StackPanel();
        stack.Children.Add(layers);
        stack.Children.Add(new TextBlock
        {
            Text = File.GetLastWriteTime(file).ToString("d MMM yyyy  HH:mm"), FontSize = 11.5, Foreground = Res("TextDim"), Margin = new Thickness(2, 6, 0, 0),
        });
        var tile = new Border
        {
            Margin = new Thickness(5), Padding = new Thickness(5), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Child = stack, Tag = badge,
        };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _openFile(file); return; }
            Toggle(file);
        };
        return tile;
    }

    internal void Toggle(string file)
    {
        if (!_selected.Remove(file)) _selected.Add(file);
        RefreshSelectionVisuals();
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var (file, tile) in _tiles)
        {
            int index = _selected.IndexOf(file);
            tile.BorderBrush = index >= 0 ? Res("Accent") : Brushes.Transparent;
            tile.Background = index >= 0 ? Res("Bg1") : Brushes.Transparent;
            var badge = (Border)tile.Tag;
            badge.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)badge.Child).Text = (index + 1).ToString();
        }
        UpdateBar(_tiles.Count);
    }

    private void UpdateBar(int total)
    {
        int n = _selected.Count;
        _info.Text = n == 0 ? $"   {total} capturas  ·  clic para marcar, doble clic para abrir" : $"   {n} marcada{(n == 1 ? "" : "s")} de {total}";
        _open.IsEnabled = _copy.IsEnabled = n == 1;
        _combineV.IsEnabled = _combineH.IsEnabled = n >= 2;
        _delete.IsEnabled = n >= 1;
    }

    private void OpenSelected()
    {
        if (_selected.Count == 1) _openFile(_selected[0]);
    }

    private void CopySelected()
    {
        if (_selected.Count != 1) return;
        try
        {
            Exporter.CopyToClipboard(ImageTools.Load(_selected[0]));
            ToastWindow.Show("Captura copiada al portapapeles");
        }
        catch (Exception ex) { ToastWindow.Show("No se pudo copiar", ex.Message); }
    }

    /// <summary>Une las capturas marcadas (en el orden en que se marcaron) y abre el resultado en el editor.</summary>
    internal BitmapSource? CombineSelected(bool vertical)
    {
        if (_selected.Count < 2) return null;
        try
        {
            var combined = ImageTools.Combine(_selected.Select(ImageTools.Load).ToList(), vertical);
            _openImage(combined);
            return combined;
        }
        catch (Exception ex)
        {
            ToastWindow.Show("No se pudieron combinar", ex.Message);
            return null;
        }
    }

    internal void DeleteSelected(bool confirm = true)
    {
        if (_selected.Count == 0) return;
        int n = _selected.Count;
        if (confirm && MessageBox.Show(this, n == 1 ? "¿Eliminar esta captura del historial?" : $"¿Eliminar {n} capturas del historial?",
                Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var file in _selected.ToList())
        {
            try { File.Delete(file); } catch { /* en uso: se queda */ }
        }
        _selected.Clear();
        _ = ReloadAsync();
    }
}
