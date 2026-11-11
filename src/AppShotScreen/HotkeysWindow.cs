using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppShotScreen.Services;

namespace AppShotScreen;

/// <summary>Diálogo para cambiar los atajos de teclado: clic en un cuadro y se pulsa la combinación nueva.</summary>
public sealed class HotkeysWindow : Window
{
    private readonly Settings _settings;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Dictionary<string, string> _values = new();
    private readonly TextBlock _message;

    public bool Saved { get; private set; }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public HotkeysWindow(Settings settings)
    {
        _settings = settings;
        Title = $"{Brand.Name} - Atajos de teclado";
        Icon = App.AppIconImage;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Res("Bg0");
        Foreground = Res("Text");
        FontFamily = new FontFamily("Segoe UI");
        WindowFinder.UseDarkTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        root.Children.Add(new TextBlock { Text = "Atajos de teclado", FontSize = 20, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock
        {
            Text = "Haz clic en un cuadro y pulsa la combinación nueva. Supr deja la acción sin atajo.",
            Foreground = Res("TextDim"), FontSize = 12.5, Margin = new Thickness(0, 4, 0, 16), TextWrapping = TextWrapping.Wrap,
        });

        foreach (var (id, label) in Hotkeys.Actions)
        {
            _values[id] = Hotkeys.Get(settings, id);
            var box = new TextBox
            {
                Text = Hotkeys.Display(_values[id]), IsReadOnly = true, Width = 190, Height = 34, Cursor = Cursors.Hand,
                Background = Res("Bg2"), Foreground = Res("Text"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
                FontSize = 13, FontWeight = FontWeights.SemiBold, CaretBrush = Brushes.Transparent, Tag = id,
            };
            box.GotKeyboardFocus += (_, _) => { box.BorderBrush = Res("Accent"); _message!.Text = "Pulsa la combinación…"; };
            box.LostKeyboardFocus += (_, _) => box.BorderBrush = Res("Line");
            box.PreviewKeyDown += (_, e) => OnKey(id, e, e.Key == Key.System ? e.SystemKey : e.Key);
            // Impr Pant no genera "tecla pulsada", solo "tecla soltada".
            box.PreviewKeyUp += (_, e) => { if (e.Key == Key.PrintScreen) OnKey(id, e, e.Key); };
            _boxes[id] = box;

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(row);
        }

        _message = new TextBlock { Foreground = Res("TextDim"), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 14), MinHeight = 18, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_message);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "Restablecer", Style = (Style)Application.Current.FindResource("GhostButton") };
        reset.Click += (_, _) => { foreach (var (id, _) in Hotkeys.Actions) Set(id, Hotkeys.Defaults[id]); _message.Text = "Valores originales. Pulsa Guardar para aplicarlos."; };
        var cancel = new Button { Content = "Cancelar", Style = (Style)Application.Current.FindResource("SoftButton"), Margin = new Thickness(6, 0, 6, 0) };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "Guardar", Style = (Style)Application.Current.FindResource("SoftButton"), Background = Res("Accent"), Foreground = Brushes.White, Padding = new Thickness(18, 0, 18, 0) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        Content = root;
    }

    private void OnKey(string id, KeyEventArgs e, Key key)
    {
        e.Handled = true;
        if (key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            Set(id, "");
            _message.Text = "Acción sin atajo.";
            return;
        }
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Close(); return; }
        if (key == Key.Tab) { e.Handled = false; return; }

        string? combo = Hotkeys.FromKey(key, Keyboard.Modifiers);
        if (combo == null)
        {
            if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.System))
                _message.Text = "Usa Ctrl o Alt junto con una tecla, o una tecla de función (F1–F12, Impr Pant).";
            return;
        }
        TryAssign(id, combo);
    }

    /// <summary>Asigna el atajo si no lo usa ya otra acción. Devuelve si se asignó.</summary>
    internal bool TryAssign(string id, string combo)
    {
        var clash = _values.FirstOrDefault(kv => kv.Key != id && string.Equals(kv.Value, combo, StringComparison.OrdinalIgnoreCase));
        if (clash.Key != null)
        {
            _message.Text = $"{Hotkeys.Display(combo)} ya se usa en «{Hotkeys.Actions.First(a => a.Id == clash.Key).Label}».";
            return false;
        }
        Set(id, combo);
        _message.Text = $"Nuevo atajo: {Hotkeys.Display(combo)}. Pulsa Guardar para aplicarlo.";
        return true;
    }

    private void Set(string id, string combo)
    {
        _values[id] = combo;
        _boxes[id].Text = Hotkeys.Display(combo);
    }

    internal void Save()
    {
        _settings.Hotkeys.Clear();
        foreach (var (id, value) in _values)
            if (value != Hotkeys.Defaults[id]) _settings.Hotkeys[id] = value; // solo guardamos lo que cambió
        try { _settings.Save(); } catch { /* no crítico */ }
        Saved = true;
        Close();
    }
}
