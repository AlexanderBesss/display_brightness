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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly string _oledHistoryPath;
    private readonly string _oledNotificationPath;
    private readonly object _saveLock = new();

    public StorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, AppName);
        _settingsPath = Path.Combine(appFolder, "settings.json");
        _oledHistoryPath = GetOledHistoryPath(AppContext.BaseDirectory);
        _oledNotificationPath = GetOledNotificationPath(AppContext.BaseDirectory);

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
        _oledHistoryPath = Path.Combine(
            Path.GetDirectoryName(settingsPath) ?? string.Empty,
            "oled-care-history.json");
        _oledNotificationPath = Path.Combine(
            Path.GetDirectoryName(settingsPath) ?? string.Empty,
            "oled-care-notifications.json");
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
        SaveJson(_settingsPath, settings);
    }

    public Dictionary<string, OledPanelProtectHistory> LoadOledPanelProtectHistory()
    {
        var normalized = CreateOledHistoryDictionary();
        if (!File.Exists(_oledHistoryPath))
            return normalized;

        try
        {
            string json = File.ReadAllText(_oledHistoryPath);
            var history = JsonSerializer.Deserialize<
                Dictionary<string, OledPanelProtectHistory>>(json, JsonOptions);
            if (history == null)
                return normalized;

            foreach (var (devicePath, entry) in history)
            {
                if (string.IsNullOrWhiteSpace(devicePath) ||
                    entry == null ||
                    entry.LastStartedAtUtc == default)
                    continue;

                int? usageHours = entry.TotalUsageHoursAtStart is >= 0
                    ? entry.TotalUsageHoursAtStart
                    : null;
                normalized[devicePath] = entry with
                {
                    LastStartedAtUtc = entry.LastStartedAtUtc.ToUniversalTime(),
                    TotalUsageHoursAtStart = usageHours
                };
            }

            return normalized;
        }
        catch
        {
            return CreateOledHistoryDictionary();
        }
    }

    public void SaveOledPanelProtectHistory(
        Dictionary<string, OledPanelProtectHistory> history)
    {
        var normalized = CreateOledHistoryDictionary();
        foreach (var (devicePath, entry) in history)
        {
            if (string.IsNullOrWhiteSpace(devicePath) ||
                entry == null ||
                entry.LastStartedAtUtc == default)
                continue;

            normalized[devicePath] = entry with
            {
                LastStartedAtUtc = entry.LastStartedAtUtc.ToUniversalTime(),
                TotalUsageHoursAtStart = entry.TotalUsageHoursAtStart is >= 0
                    ? entry.TotalUsageHoursAtStart
                    : null
            };
        }

        SaveJson(_oledHistoryPath, normalized);
    }

    public Dictionary<string, OledPanelProtectNotification>
        LoadOledPanelProtectNotifications()
    {
        var normalized = CreateOledNotificationDictionary();
        if (!File.Exists(_oledNotificationPath))
            return normalized;

        try
        {
            string json = File.ReadAllText(_oledNotificationPath);
            var notifications = JsonSerializer.Deserialize<
                Dictionary<string, OledPanelProtectNotification>>(
                    json,
                    JsonOptions);
            if (notifications == null)
                return normalized;

            foreach (var (devicePath, entry) in notifications)
            {
                if (string.IsNullOrWhiteSpace(devicePath) ||
                    entry == null ||
                    entry.FirstObservedAtUtc == default ||
                    entry.Type == OledPanelProtectEventType.None ||
                    !Enum.IsDefined(entry.Type))
                {
                    continue;
                }

                normalized[devicePath] = entry with
                {
                    FirstObservedAtUtc =
                        entry.FirstObservedAtUtc.ToUniversalTime(),
                    TotalUsageHoursAtObservation =
                        entry.TotalUsageHoursAtObservation is >= 0
                            ? entry.TotalUsageHoursAtObservation
                            : null
                };
            }

            return normalized;
        }
        catch
        {
            return CreateOledNotificationDictionary();
        }
    }

    public void SaveOledPanelProtectNotifications(
        Dictionary<string, OledPanelProtectNotification> notifications)
    {
        var normalized = CreateOledNotificationDictionary();
        foreach (var (devicePath, entry) in notifications)
        {
            if (string.IsNullOrWhiteSpace(devicePath) ||
                entry == null ||
                entry.FirstObservedAtUtc == default ||
                entry.Type == OledPanelProtectEventType.None ||
                !Enum.IsDefined(entry.Type))
            {
                continue;
            }

            normalized[devicePath] = entry with
            {
                FirstObservedAtUtc = entry.FirstObservedAtUtc.ToUniversalTime(),
                TotalUsageHoursAtObservation =
                    entry.TotalUsageHoursAtObservation is >= 0
                        ? entry.TotalUsageHoursAtObservation
                        : null
            };
        }

        SaveJson(_oledNotificationPath, normalized);
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

    internal static string GetOledHistoryPath(string executableDirectory) =>
        Path.Combine(executableDirectory, "oled-care-history.json");

    internal static string GetOledNotificationPath(string executableDirectory) =>
        Path.Combine(executableDirectory, "oled-care-notifications.json");

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

    private static Dictionary<string, OledPanelProtectHistory>
        CreateOledHistoryDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, OledPanelProtectNotification>
        CreateOledNotificationDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);
}
