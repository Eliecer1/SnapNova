using System.Diagnostics;
using System.IO;
using AppShotScreen.Capture;
using AppShotScreen.Editor;

namespace AppShotScreen;

public partial class App
{
    private const uint VK_G = 0x47;

    /// <summary>Graba una zona como GIF animado: marco rojo + panel con Detener y guardar (o Esc).</summary>
    private async Task CaptureGifAsync()
    {
        if (_capturing) return;
        _capturing = true;
        int escId = 0;
        RegionFrameWindow? frame = null;
        ScrollPanelWindow? panel = null;
        try
        {
            var overlay = await SelectAsync(hint: "Grabar GIF: arrastra la zona que quieres grabar, o haz clic en una ventana  ·  Esc = cancelar");
            if (overlay.SelectedScreenRect is not { } region) return;
            await Task.Delay(250);

            var recorder = new GifRecorder(region);
            frame = new RegionFrameWindow(region);
            panel = new ScrollPanelWindow(region, recording: true);
            bool cancelled = false;
            var p = panel;
            recorder.Tick += s => p.SetMessage($"● Grabando  {TimeSpan.FromSeconds(s):m\\:ss}  (máx. {recorder.MaxSeconds / 60}:00)");
            panel.StopRequested += recorder.Stop;
            panel.CancelRequested += () => { cancelled = true; recorder.Stop(); };
            escId = _hotkeys!.RegisterTemporary(0, VK_ESCAPE, recorder.Stop);

            frame.Show();
            panel.Show();
            await recorder.RunAsync();
            frame.Close();
            frame = null;
            if (cancelled || recorder.FrameCount == 0) return;

            panel.SetMessage("Guardando el GIF…");
            string path = Path.ChangeExtension(_settings.NextFilePath(), ".gif").Replace("Captura_", "Grabacion_");
            await recorder.SaveAsync(path);
            panel.Close();
            panel = null;

            ToastWindow.Show("GIF guardado", $"{Path.GetFileName(path)}  ·  {new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB");
            // Abrimos la carpeta con el archivo seleccionado, listo para arrastrarlo o compartirlo.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastWindow.Show("No se pudo grabar el GIF", ex.Message);
        }
        finally
        {
            frame?.Close();
            panel?.Close();
            _hotkeys?.Unregister(escId);
            _capturing = false;
        }
    }
}
