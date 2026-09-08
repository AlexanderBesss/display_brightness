using System.IO;
using System.Text.Json;
using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public class StorageService : IStorageService
{
    private const string AppName = "Brightness";
    private const string LegacyAppName = "DisplayBrightness";
    private const string StartupRegistryPath =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string OledStateFileName = "oled-care-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly string _oledStatePath;
    private readonly object _saveLock = new();

    public StorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, AppName);
        _settingsPath = Path.Combine(appFolder, "settings.json");
        string executableDirectory = AppContext.BaseDirectory;
        _oledStatePath = GetOledCareStatePath(executableDirectory);

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
        string directory = Path.GetDirectoryName(settingsPath) ?? string.Empty;
        _oledStatePath = Path.Combine(directory, OledStateFileName);
        try
        {
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
        SaveJson(_settingsPath, settings);
    }

    public Dictionary<string, OledPanelProtectState> LoadOledPanelProtectState()
    {
        if (!File.Exists(_oledStatePath))
            return CreateOledStateDictionary();

        try
        {
            string json = File.ReadAllText(_oledStatePath);
            var loaded = JsonSerializer.Deserialize<
                Dictionary<string, OledPanelProtectState>>(
                    json,
                    JsonOptions);
            return NormalizeStateDictionary(loaded);
        }
        catch
        {
            return CreateOledStateDictionary();
        }
    }

    public void SaveOledPanelProtectState(
        Dictionary<string, OledPanelProtectState> state)
    {
        var normalized = CreateOledStateDictionary();
        if (state != null)
        {
            foreach (var (devicePath, entry) in state)
            {
                var normalizedEntry = NormalizeState(entry);
                if (!string.IsNullOrWhiteSpace(devicePath) &&
                    normalizedEntry != null)
                {
                    normalized[devicePath] = normalizedEntry;
                }
            }
        }

        SaveJson(_oledStatePath, normalized);
    }

    private static OledPanelProtectState? NormalizeState(
        OledPanelProtectState? entry)
    {
        if (entry == null)
            return null;

        OledPanelProtectHistory? history = NormalizeHistory(entry.History);
        OledPanelProtectNotification? notification =
            NormalizeNotification(entry.Notification);
        if (history == null && notification == null)
            return null;

        return new OledPanelProtectState(history, notification);
    }

    private static OledPanelProtectHistory? NormalizeHistory(
        OledPanelProtectHistory? entry)
    {
        if (entry == null || entry.LastStartedAtUtc == default)
            return null;

        return entry with
        {
            LastStartedAtUtc = entry.LastStartedAtUtc.ToUniversalTime(),
            TotalUsageHoursAtStart = entry.TotalUsageHoursAtStart is >= 0
                ? entry.TotalUsageHoursAtStart
                : null
        };
    }

    private static OledPanelProtectNotification? NormalizeNotification(
        OledPanelProtectNotification? entry)
    {
        if (entry == null ||
            entry.FirstObservedAtUtc == default ||
            entry.Type == OledPanelProtectEventType.None ||
            !Enum.IsDefined(entry.Type))
        {
            return null;
        }

        return entry with
        {
            FirstObservedAtUtc = entry.FirstObservedAtUtc.ToUniversalTime(),
            TotalUsageHoursAtObservation =
                entry.TotalUsageHoursAtObservation is >= 0
                    ? entry.TotalUsageHoursAtObservation
                    : null
        };
    }

    private static Dictionary<string, OledPanelProtectState>
        NormalizeStateDictionary(
            Dictionary<string, OledPanelProtectState>? loaded)
    {
        var normalized = CreateOledStateDictionary();
        if (loaded != null)
        {
            foreach (var (devicePath, entry) in loaded)
            {
                var normalizedEntry = NormalizeState(entry);
                if (!string.IsNullOrWhiteSpace(devicePath) &&
                    normalizedEntry != null)
                {
                    normalized[devicePath] = normalizedEntry;
                }
            }
        }

        return normalized;
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

    internal static string GetOledCareStatePath(string executableDirectory) =>
        Path.Combine(executableDirectory, OledStateFileName);

    private void SaveJson<T>(string destinationPath, T value)
    {
        lock (_saveLock)
        {
            string? tempPath = null;
            try
            {
                string json = JsonSerializer.Serialize(value, JsonOptions);
                tempPath = Path.Combine(
                    Path.GetDirectoryName(destinationPath)!,
                    $"{Path.GetFileNameWithoutExtension(destinationPath)}_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempPath, json);
                if (File.Exists(destinationPath))
                    File.Replace(tempPath, destinationPath, null);
                else
                    File.Move(tempPath, destinationPath);
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

    private static Dictionary<string, int> CreateSettingsDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, OledPanelProtectState>
        CreateOledStateDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);
}
