using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace AppShotScreen.Capture;

/// <summary>
/// Captura con desplazamiento: pone el ratón sobre la zona, gira la rueda,
/// captura de nuevo y une los fragmentos hasta llegar al final (o hasta que el usuario pulse Esc).
/// </summary>
public static class ScrollingCapture
{
    public sealed record Options
    {
        public int MaxHeight { get; init; } = 20000;
        public int SettleMs { get; init; } = 350;
        public int MaxSteps { get; init; } = 400;
    }

    public static async Task<BitmapSource> CaptureAsync(PixelRect region, CancellationToken ct, Options? options = null, IProgress<int>? progress = null)
    {
        options ??= new Options();
        var oldCursor = GetCursor();
        try
        {
            // El cursor va al centro de la zona para que la rueda desplace esa ventana.
            SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
            await Task.Delay(120, CancellationToken.None);

            var stitcher = new ScrollStitcher(ScreenCapture.Capture(region));
            int notches = 1;
            int pxPerNotch = 0;
            int stillCount = 0;

            for (int step = 0; step < options.MaxSteps && !ct.IsCancellationRequested; step++)
            {
                Wheel(-120 * notches);
                await Task.Delay(options.SettleMs, CancellationToken.None);
                var frame = ScreenCapture.Capture(region);

                int expected = pxPerNotch * notches;
                var r = stitcher.Add(frame, expected);
                if (r == ScrollStitcher.AddResult.NoMovement)
                {
                    // Puede que la página aún esté cargando o animando: un reintento antes de terminar.
                    if (++stillCount >= 2) break;
                    await Task.Delay(400, CancellationToken.None);
                    continue;
                }
                if (r == ScrollStitcher.AddResult.NoMatch)
                {
                    // Saltó demasiado: retrocedemos y probamos con menos pasos de rueda.
                    if (notches == 1) break;
                    Wheel(120 * notches);
                    await Task.Delay(options.SettleMs, CancellationToken.None);
                    notches = Math.Max(1, notches / 2);
                    continue;
                }

                stillCount = 0;
                pxPerNotch = Math.Max(1, stitcher.LastShift / notches);
                // Apuntamos a desplazar ~60 % de la zona en cada paso.
                int target = (int)(region.Height * 0.6);
                notches = Math.Clamp(target / pxPerNotch, 1, 10);
                progress?.Report(stitcher.StitchedHeight);
                if (stitcher.StitchedHeight >= options.MaxHeight) break;
            }
            return stitcher.Result();
        }
        finally
        {
            SetCursorPos(oldCursor.X, oldCursor.Y);
        }
    }

    // ───────────── Win32 ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public int mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        // MOUSEINPUT es el miembro más grande de la unión, así que no hace falta relleno (40 bytes en x64).
        public MOUSEINPUT mi;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private static POINT GetCursor() { GetCursorPos(out var p); return p; }

    private static void Wheel(int delta)
    {
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
