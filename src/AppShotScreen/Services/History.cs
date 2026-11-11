using System.IO;
using System.Windows.Media.Imaging;
using AppShotScreen.Editor;

namespace AppShotScreen.Services;

/// <summary>Historial local de capturas (las últimas 200), para volver a abrirlas desde la ventana de inicio.</summary>
public static class History
{
    private const int MaxItems = 200;

    public static string Folder { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapNova", "Historial");

    public static event Action? Changed;

    /// <summary>Guarda la captura en segundo plano (la imagen debe estar congelada).</summary>
    public static void Add(BitmapSource image)
    {
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                Exporter.Save(image, path);
                foreach (var old in Recent(int.MaxValue).Skip(MaxItems))
                {
                    try { File.Delete(old); } catch { /* en uso */ }
                }
                Changed?.Invoke();
            }
            catch
            {
                // El historial es un extra: si falla (disco lleno, permisos) no interrumpe la captura.
            }
        });
    }

    public static List<string> Recent(int count)
    {
        if (!Directory.Exists(Folder)) return new List<string>();
        return Directory.GetFiles(Folder, "*.png").OrderByDescending(f => f).Take(count).ToList();
    }
}
