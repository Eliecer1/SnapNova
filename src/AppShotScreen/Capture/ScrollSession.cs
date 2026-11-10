using System.Windows.Media.Imaging;

namespace AppShotScreen.Capture;

/// <summary>
/// Sesión de captura con desplazamiento "manual" (como Snagit): el usuario gira la rueda
/// sobre la zona y la sesión toma fotogramas continuamente, agregando solo el contenido nuevo.
/// Opcionalmente puede desplazar sola (modo automático).
/// </summary>
public sealed class ScrollSession
{
    private readonly PixelRect _region;
    private readonly ScrollStitcher _stitcher;
    private volatile bool _stop;
    private volatile bool _auto;

    public int MaxHeight { get; init; } = 30000;
    public int FrameIntervalMs { get; init; } = 70;

    /// <summary>Se dispara con la altura acumulada cada vez que se agrega contenido.</summary>
    public event Action<int>? HeightChanged;
    /// <summary>Se dispara cuando el modo automático llega al final.</summary>
    public event Action? AutoReachedEnd;

    public ScrollSession(PixelRect region)
    {
        _region = region;
        _stitcher = new ScrollStitcher(ScreenCapture.Capture(region));
    }

    public int Height => _stitcher.StitchedHeight;

    // Diagnóstico
    public int Appended { get; private set; }
    public int NoMatches { get; private set; }
    public int Stills { get; private set; }
    public bool IsAuto => _auto;

    public void Stop() => _stop = true;

    public void SetAuto(bool on)
    {
        _auto = on;
        if (on) ScrollingCapture.MoveCursorToCenter(_region);
    }

    /// <summary>Corre hasta que se llame a <see cref="Stop"/> o se alcance el alto máximo.</summary>
    public async Task<BitmapSource> RunAsync()
    {
        // Estado del modo automático: girar → esperar a que se quede quieto → decidir.
        bool waitingAfterWheel = false;
        bool movedSinceWheel = false;
        int stillFrames = 0;
        int failedWheels = 0;
        int notches = 1, pxPerNotch = 0;

        while (!_stop)
        {
            if (_auto && !waitingAfterWheel)
            {
                ScrollingCapture.Wheel(-120 * notches);
                waitingAfterWheel = true;
                movedSinceWheel = false;
                stillFrames = 0;
            }
            else if (!_auto)
            {
                waitingAfterWheel = false;
            }

            await Task.Delay(FrameIntervalMs);
            if (_stop) break;

            var result = _stitcher.Add(ScreenCapture.Capture(_region), pxPerNotch * notches);
            switch (result)
            {
                case ScrollStitcher.AddResult.Appended:
                    Appended++;
                    movedSinceWheel = true;
                    stillFrames = 0;
                    if (_auto)
                    {
                        pxPerNotch = Math.Max(pxPerNotch, Math.Max(1, _stitcher.LastShift / notches));
                    }
                    HeightChanged?.Invoke(_stitcher.StitchedHeight);
                    if (_stitcher.StitchedHeight >= MaxHeight) _stop = true;
                    break;

                case ScrollStitcher.AddResult.NoMovement:
                    Stills++;
                    stillFrames++;
                    break;

                case ScrollStitcher.AddResult.NoMatch:
                    // El usuario subió, o bajó demasiado rápido: ignoramos el fotograma y esperamos
                    // a que vuelva a coincidir con lo último que guardamos.
                    NoMatches++;
                    stillFrames = 0;
                    if (_auto) notches = Math.Max(1, notches / 2);
                    break;
            }

            if (_auto && waitingAfterWheel && stillFrames >= 3)
            {
                // La página dejó de moverse tras el giro.
                if (movedSinceWheel)
                {
                    failedWheels = 0;
                    if (pxPerNotch > 0) notches = Math.Clamp((int)(_region.Height * 0.5) / pxPerNotch, 1, 8);
                }
                else if (++failedWheels >= 2)
                {
                    // Dos giros seguidos sin movimiento: llegamos al final.
                    _auto = false;
                    AutoReachedEnd?.Invoke();
                }
                waitingAfterWheel = false; // siguiente giro
            }
        }
        return _stitcher.Result();
    }
}
