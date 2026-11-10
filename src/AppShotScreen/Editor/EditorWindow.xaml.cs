using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AppShotScreen.Capture;
using AppShotScreen.Services;

namespace AppShotScreen.Editor;

public partial class EditorWindow : Window
{
    private enum Tool { Arrow, Rect, Ellipse, Highlight, Pen, Text, Step, Blur, Crop }

    private readonly Settings _settings;
    private readonly UndoStack _undo = new();
    private BitmapSource _base;
    private Brush _color;

    // Estado del arrastre en curso
    private Point _start;
    private bool _dragging;
    private UIElement? _preview;
    private Polyline? _penLine;

    public EditorWindow(BitmapSource image, Settings settings)
    {
        InitializeComponent();
        Icon = App.AppIconImage;
        _settings = settings;
        _base = image;
        _color = BrushFrom(settings.ToolColor);
        CurrentColor.Background = _color;
        Thickness.Value = settings.ToolThickness;
        EffShadow.IsChecked = settings.EffectShadow;
        EffBorder.IsChecked = settings.EffectBorder;

        SetBase(image);
        Zoom.ValueChanged += (_, e) => { ZoomTransform.ScaleX = ZoomTransform.ScaleY = e.NewValue; UpdateScalingMode(); UpdateStatus(); };
        _undo.Changed += UpdateUndoButtons;
        UpdateUndoButtons();

        Surface.MouseLeftButtonDown += Surface_Down;
        Surface.MouseMove += Surface_Move;
        Surface.MouseLeftButtonUp += Surface_Up;
        PreviewKeyDown += OnKeys;
        Loaded += (_, _) => FitIfLarger();
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

    private void UpdateStatus() =>
        Status.Text = $"{_base.PixelWidth} × {_base.PixelHeight} px  ·  {Math.Round(Zoom.Value * 100)} %";

    private void FitIfLarger()
    {
        // La imagen se muestra a tamaño real en píxeles; si no cabe, la ajustamos.
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double actual = 1.0 / dpi;
        double fit = FitFactor();
        // Capturas muy altas (con desplazamiento): ajustamos al ancho y se recorren con la rueda.
        if (_base.PixelHeight > _base.PixelWidth * 2.5)
            fit = Math.Max(100, Scroller.ActualWidth - 60) / _base.PixelWidth;
        Zoom.Value = Math.Clamp(Math.Min(actual, fit), Zoom.Minimum, Zoom.Maximum);
    }

    private double FitFactor()
    {
        double w = Math.Max(100, Scroller.ActualWidth - 60);
        double h = Math.Max(100, Scroller.ActualHeight - 60);
        return Math.Min(w / _base.PixelWidth, h / _base.PixelHeight);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Zoom.Value = Math.Clamp(FitFactor(), Zoom.Minimum, Zoom.Maximum);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => Zoom.Value = 1.0 / VisualTreeHelper.GetDpi(this).DpiScaleX;

    // ───────────────────────── Herramientas ─────────────────────────

    private Tool CurrentTool =>
        ToolRect.IsChecked == true ? Tool.Rect :
        ToolEllipse.IsChecked == true ? Tool.Ellipse :
        ToolHighlight.IsChecked == true ? Tool.Highlight :
        ToolPen.IsChecked == true ? Tool.Pen :
        ToolText.IsChecked == true ? Tool.Text :
        ToolStep.IsChecked == true ? Tool.Step :
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
            Tool.Blur => Annotations.BlurPreview(r),
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

    private void ApplyCrop(Rect r)
    {
        var flat = Exporter.Render(Surface);
        var px = new Int32Rect((int)r.X, (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        px.Width = Math.Min(px.Width, flat.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, flat.PixelHeight - px.Y);
        var cropped = ScreenCapture.Crop(flat, px);

        var oldBase = _base;
        var oldChildren = Surface.Children.Cast<UIElement>().Where(c => c != BaseImage).ToList();
        void ApplyNew()
        {
            foreach (var c in oldChildren) Surface.Children.Remove(c);
            SetBase(cropped);
        }
        void Restore()
        {
            SetBase(oldBase);
            foreach (var c in oldChildren) Surface.Children.Add(c);
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
            Key.P => ToolPen, Key.T => ToolText, Key.S => ToolStep, Key.B => ToolBlur, Key.C => ToolCrop,
            _ => null
        };
        if (tool != null) { tool.IsChecked = true; e.Handled = true; }
        else if (e.Key == Key.Escape) Close();
    }

    // ───────────────────────── Exportar ─────────────────────────

    private BitmapSource RenderFinal()
    {
        Keyboard.ClearFocus();
        UpdateLayout();
        var img = Exporter.Render(Surface);
        return Exporter.ApplyEffects(img,
            shadow: EffShadow.IsChecked == true,
            border: EffBorder.IsChecked == true,
            borderBrush: BrushFrom(_settings.BorderColor),
            borderWidth: _settings.BorderWidth);
    }

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

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Flash("Reconociendo texto…");
            string text = await OcrService.RecognizeAsync(Exporter.Render(Surface));
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

    private async void Flash(string message)
    {
        Status.Text = message;
        await Task.Delay(2500);
        UpdateStatus();
    }

    private void SaveToolPrefs()
    {
        _settings.ToolThickness = Thickness.Value;
        _settings.ToolColor = _color.ToString();
        _settings.EffectShadow = EffShadow.IsChecked == true;
        _settings.EffectBorder = EffBorder.IsChecked == true;
        try { _settings.Save(); } catch { /* no crítico */ }
    }

    private static Brush BrushFrom(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return Brushes.Red; }
    }
}
