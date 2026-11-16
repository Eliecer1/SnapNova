using System.IO;

namespace AppShotScreen.Services;

/// <summary>
/// "Iniciar con Windows": un acceso directo SnapNova.lnk en la carpeta Inicio del usuario que abre la app
/// en la bandeja (--tray). Es el mismo acceso directo que crea el instalador, así que nunca hay dos arranques.
/// No necesita permisos de administrador y el usuario lo ve en Administrador de tareas › Aplicaciones de inicio.
/// </summary>
public static class StartupManager
{
    public const string TrayArgument = "--tray";

    /// <summary>Carpeta Inicio (se puede cambiar en las pruebas para no tocar la real).</summary>
    public static string StartupFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static string ShortcutPath => Path.Combine(StartupFolder, Brand.Name + ".lnk");

    /// <summary>El .exe que se está ejecutando ahora.</summary>
    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Brand.Name + ".exe");

    public static bool IsEnabled => File.Exists(ShortcutPath);

    /// <summary>Activa o desactiva el inicio automático. Devuelve el estado final.</summary>
    public static bool SetEnabled(bool enabled, string? exePath = null)
    {
        if (!enabled)
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return false;
        }
        Directory.CreateDirectory(StartupFolder);
        CreateShortcut(ShortcutPath, exePath ?? CurrentExe, TrayArgument);
        return true;
    }

    /// <summary>
    /// Si el acceso directo existe pero apunta a un .exe que ya no está (la app se movió o se reinstaló
    /// en otra carpeta), lo corrige para que apunte a este .exe. No lo activa si estaba desactivado.
    /// </summary>
    public static void RepairIfBroken()
    {
        try
        {
            if (!IsEnabled) return;
            string? target = ReadTarget(ShortcutPath);
            if (target == null || !File.Exists(target)) SetEnabled(true);
        }
        catch { /* no crítico */ }
    }

    public static string? ReadTarget(string lnk)
    {
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic sc = shell!.CreateShortcut(lnk);
            return (string)sc.TargetPath;
        }
        finally
        {
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static string? ReadArguments(string lnk)
    {
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic sc = shell!.CreateShortcut(lnk);
            return (string)sc.Arguments;
        }
        finally
        {
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void CreateShortcut(string lnk, string exe, string args)
    {
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic sc = shell!.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.Arguments = args;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.IconLocation = exe + ",0";
            sc.Description = "Capturas de pantalla con editor";
            sc.Save();
        }
        finally
        {
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
