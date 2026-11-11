using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AppShotScreen.Editor;

// Seleccionar/mover anotaciones, globos, sellos y arrastrar la imagen a otras apps.
public partial class EditorWindow
{
    // ───────────────────────── Seleccionar y mover ─────────────────────────

    private UIElement? _selected;
    private Rectangle? _selectionBox;
    private bool _moving;
    private Point _moveStart;
    private Vector _moveOrigin;
    private string _stampKind = "ok";

    private void InitExtras()
    {
        Surface.PreviewMouseLeftButtonDown += Select_PreviewDown;
        foreach (var rb in new[] { ToolArrow, ToolRect, ToolEllipse, ToolHighlight, ToolPen, ToolText, ToolStep, ToolSpot, ToolBlur, ToolCrop, ToolCallout, ToolStamp })
            rb.Checked += (_, _) => Select(null);
        ToolSelect.Checked += (_, _) => Surface.Cursor = Cursors.Arrow;
        ToolSelect.Unchecked += (_, _) => Surface.Cursor = Cursors.Cross;
        BuildStampButtons();
        _undo.Changed += () => { if (_selected != null && !Surface.Children.Contains(_selected)) Select(null); else UpdateSelectionBox(); };
    }

    /// <summary>Anotación (hija directa del lienzo) que hay bajo un elemento visual, o null.</summary>
    private UIElement? AnnotationFrom(object? source)
    {
        var hit = source as DependencyObject;
        while (hit != null && hit != Surface)
        {
            var parent = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            if (parent == Surface) break;
            hit = parent;
        }
        return hit is UIElement el && el != Surface && el != BaseImage && el != _spotLayer ? el : null;
    }

    private static Vector OffsetOf(UIElement el) =>
        el.RenderTransform is TranslateTransform t ? new Vector(t.X, t.Y) : new Vector();

    private static void SetOffset(UIElement el, Vector v) => el.RenderTransform = new TranslateTransform(v.X, v.Y);

    private void Select_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (CurrentTool != Tool.Select) return;
        e.Handled = true; // en este modo los textos no se editan: se mueven
        Keyboard.ClearFocus();
        Focus();
        var el = AnnotationFrom(e.OriginalSource);
        Select(el);
        if (el == null) return;
        _moving = true;
        _moveStart = e.GetPosition(Surface);
        _moveOrigin = OffsetOf(el);
        Surface.CaptureMouse();
    }

    private bool HandleSelectMove(MouseEventArgs e)
    {
        if (!_moving || _selected == null) return false;
        SetOffset(_selected, _moveOrigin + (e.GetPosition(Surface) - _moveStart));
        UpdateSelectionBox();
        return true;
    }

    private bool HandleSelectUp(MouseButtonEventArgs e)
    {
        if (!_moving) return false;
        _moving = false;
        Surface.ReleaseMouseCapture();
        if (_selected is { } el)
        {
            var from = _moveOrigin;
            var to = OffsetOf(el);
            if ((to - from).Length > 0.5) _undo.Push(new DelegateAction(() => SetOffset(el, from), () => SetOffset(el, to)));
        }
        return true;
    }

    /// <summary>Mueve una anotación (también lo usan las pruebas).</summary>
    internal void MoveAnnotation(UIElement el, Vector delta)
    {
        var from = OffsetOf(el);
        var to = from + delta;
        SetOffset(el, to);
        _undo.Push(new DelegateAction(() => SetOffset(el, from), () => SetOffset(el, to)));
        UpdateSelectionBox();
    }

    internal void Select(UIElement? el)
    {
        _selected = el;
        UpdateSelectionBox();
    }

    private void UpdateSelectionBox()
    {
        if (_selected == null || !Surface.Children.Contains(_selected))
        {
            if (_selectionBox != null) _selectionBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (_selectionBox == null)
        {
            _selectionBox = new Rectangle
            {
                Stroke = (Brush)FindResource("Accent"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 4, RadiusY = 4, IsHitTestVisible = false,
            };
            Interaction.Children.Add(_selectionBox);
        }
        Surface.UpdateLayout();
        var bounds = VisualTreeHelper.GetDescendantBounds((Visual)_selected);
        if (bounds.IsEmpty) { _selectionBox.Visibility = Visibility.Collapsed; return; }
        bounds = ((Visual)_selected).TransformToAncestor(Surface).TransformBounds(bounds);
        bounds.Inflate(5, 5);
        Canvas.SetLeft(_selectionBox, bounds.X);
        Canvas.SetTop(_selectionBox, bounds.Y);
        _selectionBox.Width = bounds.Width;
        _selectionBox.Height = bounds.Height;
        _selectionBox.Visibility = Visibility.Visible;
    }

    /// <summary>Elimina la anotación seleccionada (tecla Supr).</summary>
    internal bool DeleteSelected()
    {
        if (_selected is not { } el || !Surface.Children.Contains(el)) return false;
        int index = Surface.Children.IndexOf(el);
        void Remove() => Surface.Children.Remove(el);
        void Restore() { if (!Surface.Children.Contains(el)) Surface.Children.Insert(Math.Min(index, Surface.Children.Count), el); }
        Remove();
        _undo.Push(new DelegateAction(Restore, Remove));
        Select(null);
        return true;
    }

    // ───────────────────────── Globos y sellos ─────────────────────────

    internal UIElement AddCallout(Point tip, Point at, string? text = null)
    {
        var (element, box) = Stamps.Callout(tip, at, _color, Stroke);
        if (text != null) box.Text = text;
        AddElement(element);
        if (text == null) box.Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
        return element;
    }

    internal UIElement AddStamp(string kind, Point center)
    {
        var el = Stamps.Create(kind, center, 34 + Stroke * 4);
        AddElement(el);
        return el;
    }

    private void BuildStampButtons()
    {
        foreach (var (kind, name) in Stamps.Kinds)
        {
            var tile = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand,
                Background = Brushes.Transparent, ToolTip = $"Sello: {name}. Elígelo y haz clic en la imagen.", Tag = kind,
                Child = new Viewbox { Width = 24, Height = 24, Child = Stamps.Build(kind, 24) },
            };
            tile.MouseLeftButtonUp += (_, _) =>
            {
                _stampKind = kind;
                ToolStamp.IsChecked = true;
                foreach (Border b in StampButtons.Children) b.Background = (string)b.Tag == kind ? (Brush)FindResource("Bg3") : Brushes.Transparent;
            };
            StampButtons.Children.Add(tile);
        }
        ToolStamp.Unchecked += (_, _) => { foreach (Border b in StampButtons.Children) b.Background = Brushes.Transparent; };
    }

    // ───────────────────────── Arrastrar a otras apps ─────────────────────────

    private Point? _dragOutStart;

    /// <summary>Guarda la imagen final en un archivo temporal para poder soltarla en otra aplicación.</summary>
    internal string ExportTempFile()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "APPSHOTACREEN");
        Directory.CreateDirectory(dir);
        // Limpieza: los temporales de más de un día ya no hacen falta.
        foreach (var old in Directory.GetFiles(dir, "*.png"))
        {
            try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { /* en uso */ }
        }
        string path = System.IO.Path.Combine(dir, $"Captura_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        Exporter.SavePng(RenderFinal(), path);
        return path;
    }

    private void DragOut_Down(object sender, MouseButtonEventArgs e) => _dragOutStart = e.GetPosition(this);

    private void DragOut_Move(object sender, MouseEventArgs e)
    {
        if (_dragOutStart is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - s.X) < 5 && Math.Abs(p.Y - s.Y) < 5) return;
        _dragOutStart = null;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { ExportTempFile() });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Flash("No se pudo arrastrar: " + ex.Message);
        }
    }

    private void DragOut_Click(object sender, RoutedEventArgs e) =>
        Flash("Mantén pulsado este botón y arrastra a WhatsApp, Word, un correo o una carpeta");
}
