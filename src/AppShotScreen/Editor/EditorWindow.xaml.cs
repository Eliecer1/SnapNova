using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using AppShotScreen.Capture;
using AppShotScreen.Services;

namespace AppShotScreen.Editor;

public partial class EditorWindow : Window
{
    private enum Tool { Arrow, Rect, Ellipse, Highlight, Pen, Text, Step, Spot, Blur, Crop }

    private readonly Settings _settings;
    private readonly UndoStack _undo = new();
    private BitmapSource _base;
    private Brush _color;
    private int _background;
    private bool _loaded;

    // Estado del arrastre en curso
    private Point _start;
    private bool _dragging;
    private UIElement? _preview;
    private Polyline? _penLine;

    // Capa de foco (una sola, con varios "huecos")
    private Path? _spotLayer;
    private readonly List<Rect> _spotHoles = new();

    public EditorWindow(BitmapSource image, Settings settings)
    {
        InitializeComponent();
        Icon = App.AppIconImage;
        WindowFinder.UseDarkTitleBar(this);
        _settings = settings;
        _base = image;
        _color = BrushFrom(settings.ToolColor);
        CurrentColor.Background = _color;
        Thickness.Value = settings.ToolThickness;

        _background = settings.PresentationBackground;
        PaddingSlider.Value = settings.PresentationPadding;
        RadiusSlider.Value = settings.PresentationRadius;
        ShadowToggle.IsChecked = settings.EffectShadow;
        BorderToggle.IsChecked = settings.EffectBorder;
        BuildBackgroundSwatches();

        SetBase(image);
        Zoom.ValueChanged += (_, e) => { ZoomTransform.ScaleX = ZoomTransform.ScaleY = e.NewValue; UpdateScalingMode(); UpdateStatus(); };
        PaddingSlider.ValueChanged += (_, _) => ApplyPresentation();
        RadiusSlider.ValueChanged += (_, _) => ApplyPresentation();
        ShadowToggle.Click += (_, _) => ApplyPresentation();
        BorderToggle.Click += (_, _) => ApplyPresentation();
        _undo.Changed += UpdateUndoButtons;
        UpdateUndoButtons();

        Surface.MouseLeftButtonDown += Surface_Down;
        Surface.MouseMove += Surface_Move;
        Surface.MouseLeftButtonUp += Surface_Up;
        Surface.MouseRightButtonUp += Surface_RightClick;
        PreviewKeyDown += OnKeys;
        Loaded += (_, _) => { _loaded = true; ApplyPresentation(); FitIfLarger(); };
        Closing += (_, _) => SaveToolPrefs();
    }

    // ───────────────────────── Imagen base ─────────────────────────

    private void SetBase(BitmapSource img)
    {
        _base = img;
        BaseImage.Source = img;
        BaseImage.Width = img.PixelWidth;
        BaseImage.Height = img.PixelHeight;
        Surface.Width = Interaction.Width = img.PixelWidth;
        Surface.Height = Interaction.Height = img.PixelHeight;
        ApplyPresentation();
        UpdateStatus();
    }

    /// <summary>
    /// Vista nítida: al tamaño real o ampliada se ven los píxeles tal cual; al reducir (Ajustar)
    /// se suaviza con alta calidad para que el texto no se vea "roto". Solo afecta a la vista,
    /// la imagen que se guarda o copia siempre es la original.
    /// </summary>
    private void UpdateScalingMode()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double screenScale = Zoom.Value * dpi; // píxeles de pantalla por píxel de imagen
        var mode = screenScale >= 0.999 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        RenderOptions.SetBitmapScalingMode(BaseImage, mode);
        RenderOptions.SetBitmapScalingMode(Surface, mode);
    }

    private void UpdateStatus()
    {
        Status.Text = $"{_base.PixelWidth} × {_base.PixelHeight} px";
        ZoomText.Text = $"{Math.Round(Zoom.Value * VisualTreeHelper.GetDpi(this).DpiScaleX * 100)} %";
    }

    private void FitIfLarger()
    {
        // La imagen se muestra a tamaño real en píxeles; si no cabe, la ajustamos.
        double actual = 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        double fit = FitFactor();
        // Capturas muy altas (con desplazamiento): ajustamos al ancho y se recorren con la rueda.
        if (_base.PixelHeight > _base.PixelWidth * 2.5)
            fit = Math.Max(100, Scroller.ActualWidth - 70) / ContentSize().Width;
        Zoom.Value = Math.Clamp(Math.Min(actual, fit), Zoom.Minimum, Zoom.Maximum);
        UpdateScalingMode();
        UpdateStatus();
    }

    private Size ContentSize()
    {
        var s = CurrentStyle();
        int extra = (s.EffectivePadding + s.EffectiveBorder) * 2;
        return new Size(_base.PixelWidth + extra, _base.PixelHeight + extra);
    }

    private double FitFactor()
    {
        var size = ContentSize();
        double w = Math.Max(100, Scroller.ActualWidth - 70);
        double h = Math.Max(100, Scroller.ActualHeight - 130);
        return Math.Min(w / size.Width, h / size.Height);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Zoom.Value = Math.Clamp(FitFactor(), Zoom.Minimum, Zoom.Maximum);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => Zoom.Value = 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX;

    // ───────────────────────── Presentación ─────────────────────────

    private PresentationStyle CurrentStyle() => new()
    {
        Background = _background,
        Padding = (int)PaddingSlider.Value,
        Radius = (int)RadiusSlider.Value,
        Shadow = ShadowToggle.IsChecked == true,
        Border = BorderToggle.IsChecked == true,
        BorderBrush = BrushFrom(_settings.BorderColor),
        BorderWidth = _settings.BorderWidth,
    };

    private void BuildBackgroundSwatches()
    {
        BackgroundSwatches.Children.Clear();
        for (int i = -1; i < PresentationStyle.Backgrounds.Length; i++)
        {
            int index = i;
            var tile = new Border
            {
                Width = 50, Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(4),
                Cursor = Cursors.Hand,
                Background = PresentationStyle.BackgroundBrush(i) ?? (Brush)FindResource("Bg2"),
                BorderThickness = new Thickness(2),
                ToolTip = i < 0 ? "Sin fondo" : PresentationStyle.Backgrounds[i].Name,
                Tag = i,
            };
            if (i < 0)
            {
                tile.Child = new Path
                {
                    Data = Geometry.Parse("M4,4 L20,20"), Stroke = (Brush)FindResource("TextDim"), StrokeThickness = 1.6,
                    Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            tile.MouseLeftButtonUp += (_, _) => SelectBackground(index);
            BackgroundSwatches.Children.Add(tile);
        }
        HighlightBackgroundSwatch();
    }

    internal void SelectBackground(int index)
    {
        bool firstTime = _background < 0 && index >= 0;
        _background = index;
        if (firstTime)
        {
            // Primer fondo elegido: activamos de una vez el aspecto "de presentación".
            if (RadiusSlider.Value == 0) RadiusSlider.Value = 12;
            ShadowToggle.IsChecked = true;
            if (PaddingSlider.Value < 16) PaddingSlider.Value = 64;
        }
        HighlightBackgroundSwatch();
        ApplyPresentation();
        if (_loaded && Zoom.Value > FitFactor()) Fit_Click(this, new RoutedEventArgs());
    }

    private void HighlightBackgroundSwatch()
    {
        foreach (Border tile in BackgroundSwatches.Children)
            tile.BorderBrush = (int)tile.Tag == _background ? Brushes.White : Brushes.Transparent;
    }

    /// <summary>Refleja en el lienzo cómo quedará la imagen exportada.</summary>
    private void ApplyPresentation()
    {
        if (PaddingText == null) return;
        var s = CurrentStyle();
        PaddingText.Text = $"{s.Padding} px";
        RadiusText.Text = $"{s.Radius} px";
        PaddingSlider.IsEnabled = s.Background >= 0;
        PaddingSlider.Opacity = s.Background >= 0 ? 1 : 0.4;

        double r = Math.Min(s.Radius, Math.Min(_base.PixelWidth, _base.PixelHeight) / 2.0);
        int bw = s.EffectiveBorder;
        Backdrop.Background = PresentationStyle.BackgroundBrush(s.Background);
        Backdrop.Padding = new Thickness(s.EffectivePadding + bw);
        Surface.Clip = r > 0 ? new RectangleGeometry(new Rect(0, 0, _base.PixelWidth, _base.PixelHeight), r, r) : null;

        ShadowBox.Visibility = s.Shadow ? Visibility.Visible : Visibility.Collapsed;
        ShadowBox.CornerRadius = new CornerRadius(r + bw);
        ShadowBox.Margin = new Thickness(-bw);

        FrameBorder.Visibility = bw > 0 ? Visibility.Visible : Visibility.Collapsed;
        FrameBorder.Margin = new Thickness(-bw);
        FrameBorder.CornerRadius = new CornerRadius(r + bw);
        FrameBorder.Background = s.BorderBrush;
    }

    // ───────────────────────── Herramientas ─────────────────────────

    private Tool CurrentTool =>
        ToolRect.IsChecked == true ? Tool.Rect :
        ToolEllipse.IsChecked == true ? Tool.Ellipse :
        ToolHighlight.IsChecked == true ? Tool.Highlight :
        ToolPen.IsChecked == true ? Tool.Pen :
        ToolText.IsChecked == true ? Tool.Text :
        ToolStep.IsChecked == true ? Tool.Step :
        ToolSpot.IsChecked == true ? Tool.Spot :
        ToolBlur.IsChecked == true ? Tool.Blur :
        ToolCrop.IsChecked == true ? Tool.Crop :
        Tool.Arrow;

    private double Stroke => Thickness.Value;

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        _color = ((Button)sender).Background;
        CurrentColor.Background = _color;
        // Si hay un texto en edición, le aplicamos el color.
        if (Keyboard.FocusedElement is TextBox tb && Surface.Children.Contains(tb)) tb.Foreground = _color;
    }

    private void Surface_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // editando un texto existente
        var p = e.GetPosition(Surface);
        Keyboard.ClearFocus();

        switch (CurrentTool)
        {
            case Tool.Text:
                AddText(p);
                e.Handled = true;
                return;
            case Tool.Step:
                AddElement(Annotations.Step(p, NextStepNumber(), _color, Stroke));
                e.Handled = true;
                return;
        }

        _start = p;
        _dragging = true;
        Surface.CaptureMouse();

        if (CurrentTool == Tool.Pen)
        {
            _penLine = Annotations.PenLine(p, _color, Stroke);
            Surface.Children.Add(_penLine);
        }
        else if (CurrentTool == Tool.Crop)
        {
            CropRect.Visibility = Visibility.Visible;
            Place(CropRect, new Rect(p, p));
        }
    }

    private void Surface_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = ClampToSurface(e.GetPosition(Surface));
        var r = new Rect(_start, p);

        switch (CurrentTool)
        {
            case Tool.Pen:
                _penLine?.Points.Add(p);
                return;
            case Tool.Crop:
                Place(CropRect, r);
                return;
        }

        if (_preview != null) Surface.Children.Remove(_preview);
        _preview = CurrentTool switch
        {
            Tool.Arrow => Annotations.Arrow(_start, p, _color, Stroke),
            Tool.Rect => Annotations.Rectangle(r, _color, Stroke),
            Tool.Ellipse => Annotations.Ellipse(r, _color, Stroke),
            Tool.Highlight => Annotations.Highlight(r, _color),
            Tool.Blur or Tool.Spot => Annotations.BlurPreview(r),
            _ => null
        };
        if (_preview != null) Surface.Children.Add(_preview);
    }

    private void Surface_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Surface.ReleaseMouseCapture();
        var p = ClampToSurface(e.GetPosition(Surface));
        var r = new Rect(_start, p);
        bool tooSmall = r.Width < 3 && r.Height < 3;

        switch (CurrentTool)
        {
            case Tool.Pen:
                if (_penLine != null)
                {
                    Surface.Children.Remove(_penLine);
                    if (_penLine.Points.Count > 1) AddElement(_penLine);
                    _penLine = null;
                }
                return;
            case Tool.Crop:
                CropRect.Visibility = Visibility.Collapsed;
                if (!tooSmall) ApplyCrop(r);
                return;
        }

        if (_preview != null) Surface.Children.Remove(_preview);
        _preview = null;
        if (tooSmall) return;

        if (CurrentTool == Tool.Spot)
        {
            AddSpotlight(r);
            return;
        }

        UIElement? final = CurrentTool switch
        {
            Tool.Arrow => Annotations.Arrow(_start, p, _color, Stroke),
            Tool.Rect => Annotations.Rectangle(r, _color, Stroke),
            Tool.Ellipse => Annotations.Ellipse(r, _color, Stroke),
            Tool.Highlight => Annotations.Highlight(r, _color),
            Tool.Blur => Annotations.Pixelate(_base, r),
            _ => null
        };
        if (final != null) AddElement(final);
    }

    /// <summary>Clic derecho sobre una anotación: la elimina (se puede deshacer).</summary>
    private void Surface_RightClick(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit != Surface && VisualTreeHelper.GetParent(hit) != Surface)
            hit = VisualTreeHelper.GetParent(hit);
        if (hit is not UIElement el || el == Surface || el == BaseImage) return;

        if (el == _spotLayer)
        {
            var holes = _spotHoles.ToList();
            void RemoveAll() { _spotHoles.Clear(); RefreshSpotlight(); }
            void RestoreAll() { _spotHoles.Clear(); _spotHoles.AddRange(holes); RefreshSpotlight(); }
            RemoveAll();
            _undo.Push(new DelegateAction(RestoreAll, RemoveAll));
        }
        else
        {
            int index = Surface.Children.IndexOf(el);
            void Remove() => Surface.Children.Remove(el);
            void Restore() { if (!Surface.Children.Contains(el)) Surface.Children.Insert(Math.Min(index, Surface.Children.Count), el); }
            Remove();
            _undo.Push(new DelegateAction(Restore, Remove));
        }
        e.Handled = true;
    }

    private Point ClampToSurface(Point p) =>
        new(Math.Clamp(p.X, 0, Surface.Width), Math.Clamp(p.Y, 0, Surface.Height));

    private static void Place(FrameworkElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        el.Width = r.Width;
        el.Height = r.Height;
    }

    private void AddElement(UIElement el)
    {
        Surface.Children.Add(el);
        _undo.Push(new AddElementAction(Surface, el));
    }

    private void AddText(Point p)
    {
        var tb = Annotations.Text(p, _color, Stroke);
        AddElement(tb);
        tb.Loaded += (_, _) => { tb.Focus(); Keyboard.Focus(tb); };
    }

    private int NextStepNumber() =>
        Surface.Children.OfType<FrameworkElement>().Count(c => Equals(c.Tag, Annotations.StepTag)) + 1;

    internal void AddSpotlight(Rect r)
    {
        void Add() { _spotHoles.Add(r); RefreshSpotlight(); }
        void Remove() { _spotHoles.Remove(r); RefreshSpotlight(); }
        Add();
        _undo.Push(new DelegateAction(Remove, Add));
    }

    private void RefreshSpotlight()
    {
        if (_spotHoles.Count == 0)
        {
            if (_spotLayer != null) Surface.Children.Remove(_spotLayer);
            return;
        }
        _spotLayer ??= Annotations.SpotlightLayer();
        _spotLayer.Data = Annotations.SpotlightGeometry(new Size(Surface.Width, Surface.Height), _spotHoles);
        // Justo encima de la imagen, debajo de las demás anotaciones.
        if (!Surface.Children.Contains(_spotLayer)) Surface.Children.Insert(1, _spotLayer);
    }

    private void ApplyCrop(Rect r)
    {
        var flat = Exporter.Render(Surface);
        var px = new Int32Rect((int)r.X, (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        px.Width = Math.Min(px.Width, flat.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, flat.PixelHeight - px.Y);
        var cropped = ScreenCapture.Crop(flat, px);

        var oldBase = _base;
        var oldChildren = Surface.Children.Cast<UIElement>().Where(c => c != BaseImage).ToList();
        var oldHoles = _spotHoles.ToList();
        void ApplyNew()
        {
            foreach (var c in oldChildren) Surface.Children.Remove(c);
            _spotHoles.Clear();
            SetBase(cropped);
        }
        void Restore()
        {
            SetBase(oldBase);
            foreach (var c in oldChildren) Surface.Children.Add(c);
            _spotHoles.AddRange(oldHoles);
        }
        ApplyNew();
        _undo.Push(new DelegateAction(Restore, ApplyNew));
    }

    // ───────────────────────── Deshacer ─────────────────────────

    private void Undo_Click(object sender, RoutedEventArgs e) => _undo.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _undo.Redo();

    private void UpdateUndoButtons()
    {
        UndoBtn.IsEnabled = _undo.CanUndo;
        RedoBtn.IsEnabled = _undo.CanRedo;
    }

    // ───────────────────────── Teclado ─────────────────────────

    private void OnKeys(object sender, KeyEventArgs e)
    {
        bool editingText = Keyboard.FocusedElement is TextBox;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.S) { Save_Click(this, e); e.Handled = true; return; }
        if (editingText)
        {
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); Surface.Focus(); e.Handled = true; }
            return;
        }
        if (ctrl && e.Key == Key.Z) { _undo.Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _undo.Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C) { Copy_Click(this, e); e.Handled = true; return; }
        if (ctrl) return;

        RadioButton? tool = e.Key switch
        {
            Key.A => ToolArrow, Key.R => ToolRect, Key.E => ToolEllipse, Key.H => ToolHighlight,
            Key.P => ToolPen, Key.T => ToolText, Key.S => ToolStep, Key.F => ToolSpot, Key.B => ToolBlur, Key.C => ToolCrop,
            _ => null
        };
        if (tool != null) { tool.IsChecked = true; e.Handled = true; }
        else if (e.Key == Key.Escape) Close();
    }

    // ───────────────────────── Exportar ─────────────────────────

    /// <summary>Imagen con anotaciones, sin la presentación (fondo, sombra…).</summary>
    private BitmapSource RenderAnnotated()
    {
        Keyboard.ClearFocus();
        UpdateLayout();
        var clip = Surface.Clip;
        Surface.Clip = null; // las esquinas se recortan al componer, no aquí
        try { return Exporter.Render(Surface); }
        finally { Surface.Clip = clip; }
    }

    internal BitmapSource RenderFinal() => Exporter.Compose(RenderAnnotated(), CurrentStyle());

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Exporter.CopyToClipboard(RenderFinal());
        Flash("Copiado al portapapeles");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string path = _settings.NextFilePath();
        Exporter.SavePng(RenderFinal(), path);
        Flash($"Guardado: {System.IO.Path.GetFileName(path)}");
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _settings.SaveFolder,
            FileName = System.IO.Path.GetFileName(_settings.NextFilePath()),
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
        };
        if (dlg.ShowDialog(this) != true) return;
        Exporter.Save(RenderFinal(), dlg.FileName);
        Flash($"Guardado: {System.IO.Path.GetFileName(dlg.FileName)}");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        PinWindow.Show(RenderFinal(), _settings);
        Flash("Fijada en pantalla");
    }

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Flash("Reconociendo texto…");
            string text = await OcrService.RecognizeAsync(RenderAnnotated());
            if (string.IsNullOrWhiteSpace(text))
            {
                Flash("No se encontró texto");
                return;
            }
            Clipboard.SetText(text);
            Flash("Texto copiado al portapapeles");
            new OcrResultWindow(text) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo extraer el texto:\n" + ex.Message, "APPSHOTACREEN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Busca correos, teléfonos, tarjetas… con OCR y los pixela. Devuelve cuántos ocultó.</summary>
    public async Task<int> RedactSensitiveAsync()
    {
        var img = RenderAnnotated();
        var hits = SensitiveData.Find(await OcrService.RecognizeLinesAsync(img));
        if (hits.Count == 0) return 0;

        var full = new Rect(0, 0, img.PixelWidth, img.PixelHeight);
        var blocks = hits.Select(h => Rect.Intersect(h.Bounds, full)).Where(r => !r.IsEmpty && r.Width >= 2 && r.Height >= 2)
            .Select(r => Annotations.Pixelate(img, r)).ToList();
        void Add() { foreach (var b in blocks) if (!Surface.Children.Contains(b)) Surface.Children.Add(b); }
        void Remove() { foreach (var b in blocks) Surface.Children.Remove(b); }
        Add();
        _undo.Push(new DelegateAction(Remove, Add));
        return hits.Count;
    }

    private async void SmartRedact_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Flash("Buscando datos sensibles…");
            int n = await RedactSensitiveAsync();
            Flash(n == 0 ? "No se encontraron datos sensibles" : n == 1 ? "1 dato ocultado" : $"{n} datos ocultados");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo analizar la imagen:\n" + ex.Message, "APPSHOTACREEN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private int _flashId;
    private async void Flash(string message)
    {
        int id = ++_flashId;
        Status.Text = message;
        Status.Foreground = (Brush)FindResource("Text");
        await Task.Delay(2500);
        if (id != _flashId) return;
        Status.Foreground = (Brush)FindResource("TextDim");
        UpdateStatus();
    }

    private void SaveToolPrefs()
    {
        _settings.ToolThickness = Thickness.Value;
        _settings.ToolColor = _color.ToString();
        _settings.EffectShadow = ShadowToggle.IsChecked == true;
        _settings.EffectBorder = BorderToggle.IsChecked == true;
        _settings.PresentationBackground = _background;
        _settings.PresentationPadding = (int)PaddingSlider.Value;
        _settings.PresentationRadius = (int)RadiusSlider.Value;
        try { _settings.Save(); } catch { /* no crítico */ }
    }

    private static Brush BrushFrom(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return Brushes.Red; }
    }
}
