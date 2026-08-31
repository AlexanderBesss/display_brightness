using System.IO;
using System.Text.Json;

namespace DisplayBrightness.Services;

public class StorageService
{
    private const string AppName = "Brightness";
    private const string LegacyAppName = "DisplayBrightness";
    private const string StartupRegistryPath =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly object _saveLock = new();

    public StorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, AppName);
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "settings.json");

        var legacySettingsPath = Path.Combine(appData, LegacyAppName, "settings.json");
        if (!File.Exists(_settingsPath) && File.Exists(legacySettingsPath))
        {
            File.Copy(legacySettingsPath, _settingsPath);
        }
    }

    public Dictionary<string, int> LoadSettings()
    {
        if (!File.Exists(_settingsPath))
            return new Dictionary<string, int>();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, int>>(
                json, JsonOptions);
            return settings ?? new Dictionary<string, int>();
        }
        catch
        {
            return new Dictionary<string, int>();
        }
    }

    public void SaveSettings(Dictionary<string, int> settings)
    {
        lock (_saveLock)
        {
            string? tempPath = null;
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                tempPath = Path.Combine(
                    Path.GetDirectoryName(_settingsPath)!,
                    $"settings_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempPath, json);
                if (File.Exists(_settingsPath))
                    File.Replace(tempPath, _settingsPath, null);
                else
                    File.Move(tempPath, _settingsPath);
            }
            catch
            {
            }
            finally
            {
                if (tempPath != null && File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    public bool GetStartOnStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                StartupRegistryPath);
            return key?.GetValue(AppName) != null || key?.GetValue(LegacyAppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public void SetStartOnStartup(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                StartupRegistryPath);

            if (enabled)
            {
                key.SetValue(
                    AppName,
                    GetExecutablePath(),
                    Microsoft.Win32.RegistryValueKind.String);
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }
        }
        catch
        {
        }
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            return processPath;

        return System.Windows.Forms.Application.ExecutablePath;
    }
}
