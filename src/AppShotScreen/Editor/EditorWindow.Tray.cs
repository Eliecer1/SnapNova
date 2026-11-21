using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppShotScreen.Services;

namespace AppShotScreen.Editor;

// Bandeja de capturas (como la de Snagit): un solo editor, y abajo las capturas recientes.
// Cada captura es un "documento" con su imagen, sus anotaciones y su deshacer; al cambiar de
// miniatura se guarda el estado de la actual y se carga el de la elegida.
public partial class EditorWindow
{
    private const int TrayLimit = 40;

    /// <summary>Una captura en la bandeja.</summary>
    internal sealed class TrayDoc
    {
        public string? FilePath;               // archivo del historial (puede no existir aún)
        public BitmapSource? Image;            // imagen base (null = aún sin cargar del disco)
        public BitmapSource? Original;         // imagen tal como está en el archivo (para saber si se recortó)
        public List<UIElement> Annotations = new();
        public List<Rect> SpotHoles = new();
        public (IUndoAction[] Undo, IUndoAction[] Redo) UndoState = (Array.Empty<IUndoAction>(), Array.Empty<IUndoAction>());
        public Border? Tile;
        public bool Edited => Image == null ? EditStore.Exists(FilePath) : Annotations.Count > 0 || !ReferenceEquals(Image, Original);
    }

    private readonly List<TrayDoc> _docs = new();
    private TrayDoc? _current;

    internal int TrayCount => _docs.Count;
    internal int CurrentIndex => _current == null ? -1 : _docs.IndexOf(_current);
    internal BitmapSource CurrentImage => _base;

    private void InitTray(BitmapSource first, string? historyPath)
    {
        var doc = new TrayDoc { FilePath = historyPath, Image = first, Original = first };
        _docs.Add(doc);
        _current = doc;
        if (ApplySavedEdits(doc)) // si esta imagen ya tenía anotaciones guardadas, las ponemos en el lienzo
        {
            if (!ReferenceEquals(doc.Image, first)) SetBase(doc.Image!);
            foreach (var el in doc.Annotations) Surface.Children.Add(el);
            _spotLayer = doc.Annotations.OfType<System.Windows.Shapes.Path>().FirstOrDefault(p => Equals(p.Tag, Annotations.SpotlightTag));
            _spotHoles.AddRange(doc.SpotHoles);
        }
        Closing += (_, _) => { if (_current != null) { Keyboard.ClearFocus(); PersistCurrent(null); } };

        // Completamos con capturas anteriores del historial (sin cargarlas aún).
        foreach (var file in History.Recent(TrayLimit))
        {
            if (_docs.Count >= TrayLimit) break;
            if (SamePath(file, historyPath)) continue;
            _docs.Add(new TrayDoc { FilePath = file });
        }
        foreach (var d in _docs) TrayPanel.Children.Add(BuildTile(d));
        HighlightCurrent();
        UpdateTrayCount();
    }

    private static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Abre una imagen: si ese archivo ya está en la bandeja, vuelve a él (con sus anotaciones).</summary>
    public void OpenOrSwitch(BitmapSource image, string? path)
    {
        var existing = path == null ? null : _docs.FirstOrDefault(d => SamePath(d.FilePath, path));
        if (existing != null) SwitchTo(existing);
        else AddCapture(image, path);
    }

    /// <summary>Llega una captura nueva: se pone la primera de la bandeja y (si se pide) se abre.</summary>
    public void AddCapture(BitmapSource image, string? historyPath = null, bool activate = true)
    {
        var doc = new TrayDoc { FilePath = historyPath, Image = image, Original = image };
        ApplySavedEdits(doc);
        _docs.Insert(0, doc);
        TrayPanel.Children.Insert(0, BuildTile(doc));
        // Si el historial ya la trajo (por la carga inicial), quitamos el duplicado.
        var dup = _docs.Skip(1).FirstOrDefault(d => SamePath(d.FilePath, historyPath));
        if (dup != null) RemoveDoc(dup);
        while (_docs.Count > TrayLimit)
        {
            var last = _docs[^1];
            if (last == _current) break;
            RemoveDoc(last);
        }
        if (activate) SwitchTo(doc);
        UpdateTrayCount();
        TrayScroller.ScrollToLeftEnd();
    }

    private void RemoveDoc(TrayDoc d)
    {
        _docs.Remove(d);
        if (d.Tile != null) TrayPanel.Children.Remove(d.Tile);
    }

    /// <summary>Guarda el estado de la captura actual y abre otra.</summary>
    internal void SwitchTo(TrayDoc target)
    {
        if (target == _current && target.Image != null) return;

        // Cargamos primero: si el archivo falla, nos quedamos donde estábamos.
        if (target.Image == null)
        {
            try
            {
                if (target.FilePath == null || !File.Exists(target.FilePath)) throw new FileNotFoundException();
                target.Image = target.Original = ImageTools.Load(target.FilePath);
                ApplySavedEdits(target);
            }
            catch
            {
                Flash("No se pudo abrir esa captura (¿se borró del historial?)");
                RemoveDoc(target);
                UpdateTrayCount();
                return;
            }
        }

        Keyboard.ClearFocus();
        Select(null);
        if (_current != null) SaveCurrentState(_current);

        // Vaciamos el lienzo y cargamos la otra captura con sus anotaciones.
        foreach (var c in Surface.Children.Cast<UIElement>().Where(c => c != BaseImage).ToList()) Surface.Children.Remove(c);
        _spotHoles.Clear();
        _spotLayer = null;
        _current = target;
        SetBase(target.Image!);
        foreach (var el in target.Annotations) Surface.Children.Add(el);
        _spotLayer = target.Annotations.OfType<System.Windows.Shapes.Path>().FirstOrDefault(p => Equals(p.Tag, Annotations.SpotlightTag));
        _spotHoles.AddRange(target.SpotHoles);
        _undo.Restore(target.UndoState);

        HighlightCurrent();
        RefreshTile(target);
        if (_loaded) FitIfLarger();
    }

    private void SaveCurrentState(TrayDoc d)
    {
        d.Image = _base;
        d.Annotations = Surface.Children.Cast<UIElement>().Where(c => c != BaseImage).ToList();
        d.SpotHoles = _spotHoles.ToList();
        d.UndoState = _undo.Save();
        PersistCurrent(null);
        RefreshTile(d);
    }

    // ───────────── Guardado entre sesiones ─────────────

    /// <summary>Recupera las anotaciones guardadas de otra sesión. Devuelve si había algo.</summary>
    private bool ApplySavedEdits(TrayDoc d)
    {
        if (d.FilePath == null) return false;
        var saved = EditStore.Load(d.FilePath);
        if (saved == null) return false;
        if (saved.Base != null) d.Image = saved.Base;
        d.Annotations = saved.Annotations;
        d.SpotHoles = saved.SpotHoles;
        return true;
    }

    /// <summary>Guarda en disco las anotaciones de la captura abierta (con su archivo).</summary>
    internal void PersistCurrent(BitmapSource? rendered)
    {
        var d = _current;
        if (d?.FilePath == null) return;
        try
        {
            var annotations = Surface.Children.Cast<UIElement>().Where(c => c != BaseImage).ToList();
            bool baseChanged = !ReferenceEquals(_base, d.Original);
            BitmapSource? thumb = null;
            if (annotations.Count > 0 || baseChanged) thumb = Thumbnail(rendered ?? Exporter.Render(Surface));
            EditStore.Save(d.FilePath, baseChanged ? _base : null, annotations, _spotHoles, Surface, thumb);
        }
        catch
        {
            // Guardar ediciones es un extra: si falla (disco lleno), el editor sigue funcionando.
        }
    }

    // ───────────── Miniaturas ─────────────

    private Border BuildTile(TrayDoc d)
    {
        var img = new Image { Stretch = Stretch.UniformToFill, Width = 128, Height = 76 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var dot = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = (Brush)FindResource("Accent"),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0), Visibility = Visibility.Collapsed,
            ToolTip = "Tiene cambios en esta sesión",
        };
        var grid = new Grid { Width = 128, Height = 76, Clip = new RectangleGeometry(new Rect(0, 0, 128, 76), 7, 7), Background = (Brush)FindResource("Bg2") };
        grid.Children.Add(img);
        grid.Children.Add(dot);

        var tile = new Border
        {
            Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(3), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid, Tag = (img, dot),
            ToolTip = d.FilePath != null && File.Exists(d.FilePath) ? File.GetLastWriteTime(d.FilePath).ToString("d MMM  HH:mm") : "Captura nueva",
        };
        tile.MouseEnter += (_, _) => { if (d != _current) tile.Background = (Brush)FindResource("Bg2"); };
        tile.MouseLeave += (_, _) => { if (d != _current) tile.Background = Brushes.Transparent; };
        tile.MouseLeftButtonUp += (_, _) => SwitchTo(d);
        tile.ContextMenu = BuildTileMenu(d);
        d.Tile = tile;
        RefreshTile(d);
        return tile;
    }

    /// <summary>Clic derecho en una miniatura: copiarla (con sus anotaciones) sin tener que abrirla.</summary>
    private ContextMenu BuildTileMenu(TrayDoc d)
    {
        var menu = new ContextMenu();
        void Item(string header, Action action, string? tip = null)
        {
            var mi = new MenuItem { Header = header, ToolTip = tip };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Item("Copiar con anotaciones", () => CopyDoc(d, annotated: true), "Copia la captura tal como se ve, con flechas, textos y demás");
        Item("Copiar original", () => CopyDoc(d, annotated: false), "Copia la captura sin anotaciones");
        menu.Items.Add(new Separator());
        Item("Abrir", () => SwitchTo(d));
        menu.Opened += (_, _) => ((MenuItem)menu.Items[1]).IsEnabled = d.FilePath != null || d.Original != null;
        return menu;
    }

    internal void CopyDoc(TrayDoc d, bool annotated)
    {
        try
        {
            Exporter.CopyToClipboard(annotated ? RenderDoc(d) : d.Original ?? ImageTools.Load(d.FilePath!));
            Flash(annotated ? "Copiada al portapapeles con sus anotaciones" : "Captura original copiada al portapapeles");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Flash("Otra aplicación tiene ocupado el portapapeles. Inténtalo de nuevo.");
        }
        catch (Exception ex)
        {
            Flash($"No se pudo copiar: {ex.Message}");
        }
    }

    /// <summary>Imagen de una captura de la bandeja con sus anotaciones (la abierta, en memoria o guardada en disco).</summary>
    internal BitmapSource RenderDoc(TrayDoc d)
    {
        if (d == _current) return RenderAnnotated();
        if (d.Image != null) return Exporter.RenderComposite(d.Image, d.Annotations);
        if (d.FilePath == null) throw new FileNotFoundException();
        return EditStore.RenderEdited(d.FilePath);
    }

    internal TrayDoc DocAt(int i) => _docs[i];

    /// <summary>Actualiza la miniatura (con las anotaciones si es la captura abierta).</summary>
    private void RefreshTile(TrayDoc d)
    {
        if (d.Tile?.Tag is not (Image img, Border dot)) return;
        dot.Visibility = d.Edited ? Visibility.Visible : Visibility.Collapsed;
        if (d.Image != null)
        {
            img.Source = Thumbnail(d.Image);
        }
        else if (d.FilePath != null && img.Source == null && EditStore.LoadThumb(d.FilePath) is { } savedThumb)
        {
            img.Source = savedThumb; // miniatura con las anotaciones guardadas
        }
        else if (d.FilePath != null && img.Source == null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 256;
                bmp.UriSource = new Uri(d.FilePath);
                bmp.EndInit();
                bmp.Freeze();
                img.Source = bmp;
            }
            catch { /* se quita al intentar abrirla */ }
        }
    }

    private static BitmapSource Thumbnail(BitmapSource src)
    {
        double scale = Math.Min(1, 256.0 / src.PixelWidth);
        if (scale >= 1) return src;
        var t = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }

    private void HighlightCurrent()
    {
        foreach (var d in _docs)
        {
            if (d.Tile == null) continue;
            bool on = d == _current;
            d.Tile.BorderBrush = on ? (Brush)FindResource("Accent") : Brushes.Transparent;
            d.Tile.Background = on ? (Brush)FindResource("Bg2") : Brushes.Transparent;
        }
        _current?.Tile?.BringIntoView();
    }

    private void UpdateTrayCount() => TrayCount_Text.Text = $"{_docs.Count} capturas";

    /// <summary>Tras cada cambio en la captura abierta, refrescamos su miniatura (con un pequeño retraso).</summary>
    private int _thumbVersion;
    private async void ScheduleThumbRefresh()
    {
        int v = ++_thumbVersion;
        await Task.Delay(600);
        if (v != _thumbVersion || _current?.Tile?.Tag is not (Image img, Border dot)) return;
        // Mientras se escribe un texto no tocamos nada (no hay que quitarle el foco).
        if (Keyboard.FocusedElement is TextBox) { ScheduleThumbRefresh(); return; }
        try
        {
            var rendered = Exporter.Render(Surface);
            img.Source = Thumbnail(rendered);
            PersistCurrent(rendered);
            dot.Visibility = Surface.Children.Count > 1 || !ReferenceEquals(_base, _current.Original) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { /* la ventana se está cerrando */ }
    }

    private void TrayPrev_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void TrayNext_Click(object sender, RoutedEventArgs e) => Step(+1);

    /// <summary>Ir a la captura anterior/siguiente de la bandeja (también con Ctrl+← / Ctrl+→).</summary>
    internal void Step(int delta)
    {
        if (_current == null || _docs.Count < 2) return;
        int i = Math.Clamp(_docs.IndexOf(_current) + delta, 0, _docs.Count - 1);
        SwitchTo(_docs[i]);
    }

    internal void SwitchToIndex(int i) => SwitchTo(_docs[i]);
}
