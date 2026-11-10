using System.Windows.Interop;

namespace AppShotScreen.Services;

/// <summary>Registra atajos de teclado globales usando una ventana de mensajes oculta.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x9000;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("AppShotScreenHotkeys") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <returns>true si Windows aceptó el atajo (puede fallar si otra app ya lo usa).</returns>
    public bool Register(uint modifiers, uint virtualKey, Action action)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, modifiers | Native.MOD_NOREPEAT, virtualKey))
            return false;
        _actions[id] = action;
        return true;
    }

    /// <summary>Registra un atajo temporal; devuelve su id (0 si falló) para quitarlo con <see cref="Unregister"/>.</summary>
    public int RegisterTemporary(uint modifiers, uint virtualKey, Action action)
    {
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, modifiers | Native.MOD_NOREPEAT, virtualKey)) return 0;
        _actions[id] = action;
        return id;
    }

    public void Unregister(int id)
    {
        if (id == 0 || !_actions.Remove(id)) return;
        Native.UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.Dispose();
    }
}
