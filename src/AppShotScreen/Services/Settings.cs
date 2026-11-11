using System.IO;
using System.Text.Json;

namespace AppShotScreen.Services;

/// <summary>Preferencias del usuario, guardadas como JSON en %APPDATA%\APPSHOTACREEN\settings.json.</summary>
public sealed class Settings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "APPSHOTACREEN");

    /// <summary>false en las pruebas automáticas, para que no toquen las preferencias reales del usuario.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Persist { get; set; } = true;

    public bool CopyToClipboardAfterCapture { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; } = true;
    public bool AutoSaveAfterCapture { get; set; } = false;
    public string FileNamePattern { get; set; } = "Captura_{0:yyyy-MM-dd_HH-mm-ss}";

    // Efectos al exportar
    public bool EffectShadow { get; set; } = false;
    public bool EffectBorder { get; set; } = false;
    public string BorderColor { get; set; } = "#FF2D2D2D";
    public int BorderWidth { get; set; } = 2;

    // Presentación (fondo degradado, margen, esquinas)
    public int PresentationBackground { get; set; } = -1;
    public int PresentationPadding { get; set; } = 64;
    public int PresentationRadius { get; set; } = 0;

    public bool ShowHomeOnStart { get; set; } = true;

    // Estilo de herramientas del editor
    public string ToolColor { get; set; } = "#FFE53935";
    public double ToolThickness { get; set; } = 4;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APPSHOTACREEN", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Archivo dañado: seguimos con valores por defecto.
        }
        return new Settings();
    }

    public void Save()
    {
        if (!Persist) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public string NextFilePath()
    {
        Directory.CreateDirectory(SaveFolder);
        string baseName = string.Format(FileNamePattern, DateTime.Now);
        string path = Path.Combine(SaveFolder, baseName + ".png");
        int i = 2;
        while (File.Exists(path)) path = Path.Combine(SaveFolder, $"{baseName}_{i++}.png");
        return path;
    }
}
