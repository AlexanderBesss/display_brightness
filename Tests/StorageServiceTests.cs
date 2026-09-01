using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class StorageServiceTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Brightness\Brightness.exe")]
    [InlineData(@"C:\Tools\Brightness.exe")]
    public void FormatStartupCommand_QuotesExecutablePath(string path)
    {
        Assert.Equal(
            $"\"{path}\" --startup",
            StorageService.FormatStartupCommand(path));
    }

    [Fact]
    public void OledHistoryPath_IsNextToExecutable()
    {
        const string executableDirectory = @"C:\Tools\Brightness";

        Assert.Equal(
            @"C:\Tools\Brightness\oled-care-history.json",
            StorageService.GetOledHistoryPath(executableDirectory));
    }

    [Fact]
    public void LoadSettings_NormalizesDevicePathCaseAndBrightnessRange()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "BrightnessTests",
            Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(testDirectory, "settings.json");

        try
        {
            var storage = new StorageService(settingsPath);
            File.WriteAllText(
                settingsPath,
                """{"MONITOR\\MSI3CD7\\INSTANCE": 140}""");

            Dictionary<string, int> settings = storage.LoadSettings();

            Assert.Equal(100, settings[@"monitor\msi3cd7\instance"]);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void OledHistory_RoundTripsWithNormalizedKeysAndValues()
    {
        string testDirectory = CreateTestDirectory();
        string settingsPath = Path.Combine(testDirectory, "settings.json");
        var timestamp = new DateTimeOffset(2026, 9, 1, 12, 30, 0, TimeSpan.FromHours(3));

        try
        {
            var storage = new StorageService(settingsPath);
            storage.SaveOledPanelProtectHistory(new Dictionary<
                string,
                OledPanelProtectHistory>
            {
                [@"MONITOR\MSI3CD7\INSTANCE"] = new(timestamp, 10250),
                [@"MONITOR\MSI3CD7\INVALID"] = new(timestamp, -1)
            });

            Dictionary<string, OledPanelProtectHistory> history =
                storage.LoadOledPanelProtectHistory();

            OledPanelProtectHistory saved =
                history[@"monitor\msi3cd7\instance"];
            Assert.Equal(timestamp.ToUniversalTime(), saved.LastStartedAtUtc);
            Assert.Equal(10250, saved.TotalUsageHoursAtStart);
            Assert.Null(history[@"monitor\msi3cd7\invalid"].TotalUsageHoursAtStart);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void LoadOledHistory_ReturnsEmptyForMissingOrMalformedFile()
    {
        string testDirectory = CreateTestDirectory();
        string settingsPath = Path.Combine(testDirectory, "settings.json");

        try
        {
            var storage = new StorageService(settingsPath);
            Assert.Empty(storage.LoadOledPanelProtectHistory());

            File.WriteAllText(
                Path.Combine(testDirectory, "oled-care-history.json"),
                "{ definitely not valid JSON");

            Assert.Empty(storage.LoadOledPanelProtectHistory());
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "BrightnessTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
