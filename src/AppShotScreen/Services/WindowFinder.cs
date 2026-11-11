using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using AppShotScreen.Capture;

namespace AppShotScreen.Services;

/// <summary>Lista las ventanas visibles (de arriba hacia abajo) con su rectángulo real en píxeles.</summary>
public static class WindowFinder
{
    public sealed record WindowInfo(IntPtr Handle, string Title, PixelRect Bounds);

    public static List<WindowInfo> Snapshot()
    {
        var list = new List<WindowInfo>();
        IntPtr shell = GetShellWindow();
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell || !IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var cls = new StringBuilder(64);
            GetClassName(hWnd, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW") return true; // el escritorio

            RECT r;
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hWnd, out r))
                return true;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 60 || h < 40) return true;

            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            list.Add(new WindowInfo(hWnd, title.ToString(), new PixelRect(r.Left, r.Top, w, h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Barra de título oscura (Windows 10 2004+ / 11).</summary>
    public static void UseDarkTitleBar(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
}
