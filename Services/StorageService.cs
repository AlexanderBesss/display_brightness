using System.IO;
using System.Text.Json;

namespace DisplayBrightness.Services;

public class StorageService : IStorageService
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
        _settingsPath = Path.Combine(appFolder, "settings.json");

        try
        {
            Directory.CreateDirectory(appFolder);

            var legacySettingsPath = Path.Combine(appData, LegacyAppName, "settings.json");
            if (!File.Exists(_settingsPath) && File.Exists(legacySettingsPath))
                File.Copy(legacySettingsPath, _settingsPath);
        }
        catch
        {
            // Settings are optional. A read-only or unavailable roaming profile
            // must not prevent brightness control from starting.
        }
    }

    internal StorageService(string settingsPath)
    {
        _settingsPath = settingsPath;
        try
        {
            string? directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }
        catch
        {
        }
    }

    public Dictionary<string, int> LoadSettings()
    {
        if (!File.Exists(_settingsPath))
            return CreateSettingsDictionary();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, int>>(
                json, JsonOptions);
            var normalized = CreateSettingsDictionary();
            if (settings != null)
            {
                foreach (var (devicePath, brightness) in settings)
                {
                    if (!string.IsNullOrWhiteSpace(devicePath))
                        normalized[devicePath] = Math.Clamp(brightness, 0, 100);
                }
            }

            return normalized;
        }
        catch
        {
            return CreateSettingsDictionary();
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
            object? currentValue = key?.GetValue(AppName);
            string? currentCommand = Convert.ToString(currentValue);
            bool hasLegacyCommand = key?.GetValue(LegacyAppName) != null;
            bool isEnabled = currentValue != null || hasLegacyCommand;

            if (isEnabled && !string.Equals(
                    currentCommand,
                    FormatStartupCommand(GetExecutablePath()),
                    StringComparison.OrdinalIgnoreCase))
            {
                SetStartOnStartup(enabled: true);
            }

            return isEnabled;
        }
        catch
        {
            return false;
        }
    }

    public bool SetStartOnStartup(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                StartupRegistryPath);

            if (enabled)
            {
                key.SetValue(
                    AppName,
                    FormatStartupCommand(GetExecutablePath()),
                    Microsoft.Win32.RegistryValueKind.String);
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            return processPath;

        return System.Windows.Forms.Application.ExecutablePath;
    }

    internal static string FormatStartupCommand(string executablePath) =>
        $"\"{executablePath.Trim('\"')}\" --startup";

    private static Dictionary<string, int> CreateSettingsDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);
}
