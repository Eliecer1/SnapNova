using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AppShotScreen.Services;

/// <summary>
/// Copia al portapapeles reintentando: otras apps (AnyViewer y otros escritorios remotos que sincronizan
/// el portapapeles, gestores de portapapeles, otras herramientas de captura) lo abren un instante y
/// Windows devuelve CLIPBRD_E_CANT_OPEN (0x800401D0).
/// </summary>
public static class ClipboardHelper
{
    /// <summary>Tiempo máximo que esperamos a que se libere el portapapeles.</summary>
    public static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Versión que no congela la ventana mientras espera.</summary>
    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryOnceText(text)) return true;
            if (clock.Elapsed >= Budget) return false;
            await Task.Delay(100);
        }
    }

    /// <returns>true si se copió; false si el portapapeles siguió ocupado todo el tiempo de espera.</returns>
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryOnceText(text)) return true;
            if (clock.Elapsed >= Budget) return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>Para datos con varios formatos (imagen + PNG).</summary>
    public static bool TrySetData(System.Windows.DataObject data)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Ojo: este método de WPF ya reintenta por dentro (≈1 s); por eso el bucle se limita por tiempo.
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException) { }
            if (clock.Elapsed >= Budget) return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>Un solo intento rápido (sin los reintentos internos de WPF).</summary>
    private static bool TryOnceText(string text)
    {
        try
        {
            Forms.Clipboard.SetDataObject(new Forms.DataObject(Forms.DataFormats.UnicodeText, text), true, 1, 0);
            return true;
        }
        catch (ExternalException)
        {
            // A veces Windows sí guardó el texto pero falló el paso final: si ya está, cuenta como copiado.
            try { return Forms.Clipboard.ContainsText() && Forms.Clipboard.GetText() == text; }
            catch (ExternalException) { return false; }
        }
    }
}
