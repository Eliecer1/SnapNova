using System.IO;
using System.Windows.Input;

namespace AppShotScreen.Services;

/// <summary>Nombre comercial de la app y traslado de los datos guardados con el nombre anterior.</summary>
public static class Brand
{
    public const string Name = "SnapNova";
    private const string OldName = "APPSHOTACREEN";

    /// <summary>Mueve ajustes e historial de las carpetas del nombre anterior a las nuevas (una sola vez).</summary>
    public static void MigrateFromOldName()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                string baseDir = Environment.GetFolderPath(root);
                string oldDir = Path.Combine(baseDir, OldName), newDir = Path.Combine(baseDir, Name);
                if (Directory.Exists(oldDir) && !Directory.Exists(newDir)) Directory.Move(oldDir, newDir);
            }
            catch
            {
                // Si no se puede mover (archivo en uso), la app arranca con datos nuevos; los viejos siguen en su sitio.
            }
        }
    }

    /// <summary>Si la carpeta de guardado era la predeterminada del nombre anterior y está vacía, usa la nueva.</summary>
    public static void FixSaveFolder(Settings s)
    {
        try
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string oldDefault = Path.Combine(pictures, OldName);
            if (!string.Equals(s.SaveFolder.TrimEnd('\\'), oldDefault, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(oldDefault) && Directory.EnumerateFileSystemEntries(oldDefault).Any()) return; // tiene capturas: no la tocamos
            s.SaveFolder = Path.Combine(pictures, Name);
            s.Save();
        }
        catch { /* no crítico */ }
    }
}

/// <summary>Atajos de teclado configurables. Se guardan como texto, por ejemplo "Ctrl+Alt+G".</summary>
public static class Hotkeys
{
    public const string Region = "region", Region2 = "region2", Full = "full", Scroll = "scroll", Text = "text", Pin = "pin", Gif = "gif";

    public static readonly (string Id, string Label)[] Actions =
    {
        (Region, "Región o ventana"),
        (Region2, "Región o ventana (alternativo)"),
        (Full, "Pantalla completa"),
        (Scroll, "Captura con desplazamiento"),
        (Gif, "Grabar GIF"),
        (Text, "Copiar texto"),
        (Pin, "Capturar y fijar"),
    };

    public static readonly Dictionary<string, string> Defaults = new()
    {
        [Region] = "PrintScreen", [Region2] = "Ctrl+Shift+S", [Full] = "Ctrl+Shift+F", [Scroll] = "Ctrl+Shift+D",
        [Gif] = "Ctrl+Alt+G", [Text] = "Ctrl+Alt+T", [Pin] = "Ctrl+Alt+P",
    };

    /// <summary>Atajo configurado para una acción ("" = desactivado).</summary>
    public static string Get(Settings s, string id) =>
        s.Hotkeys.TryGetValue(id, out var v) ? v : Defaults[id];

    /// <summary>Texto para mostrar (Impr Pant en vez de PrintScreen).</summary>
    public static string Display(string combo) =>
        string.IsNullOrEmpty(combo) ? "Sin atajo" : combo.Replace("PrintScreen", "Impr Pant").Replace("Snapshot", "Impr Pant");

    /// <summary>Convierte "Ctrl+Alt+G" en modificadores y código de tecla de Windows.</summary>
    public static bool TryParse(string combo, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    /// <summary>Construye el texto del atajo a partir de una pulsación; null si aún no es una combinación válida.</summary>
    public static string? FromKey(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return null;
        bool functionKey = key is >= Key.F1 and <= Key.F24 || key is Key.PrintScreen or Key.Pause or Key.Scroll;
        // Una letra sola no sirve como atajo global: se la "comería" al escribir en cualquier programa.
        if (!functionKey && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
