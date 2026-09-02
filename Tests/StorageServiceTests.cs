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
    public void OledCareStatePath_IsNextToExecutable()
    {
        const string executableDirectory = @"C:\Tools\Brightness";

        Assert.Equal(
            @"C:\Tools\Brightness\oled-care-state.json",
            StorageService.GetOledCareStatePath(executableDirectory));
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
    public void OledState_RoundTripsWithNormalizedKeysAndValues()
    {
        string testDirectory = CreateTestDirectory();
        string settingsPath = Path.Combine(testDirectory, "settings.json");
        var startedAt = new DateTimeOffset(
            2026, 9, 1, 12, 30, 0, TimeSpan.FromHours(3));
        var observedAt = new DateTimeOffset(
            2026, 9, 1, 17, 30, 0, TimeSpan.FromHours(3));

        try
        {
            var storage = new StorageService(settingsPath);
            storage.SaveOledPanelProtectState(new Dictionary<
                string,
                OledPanelProtectState>
            {
                [@"MONITOR\MSI3CD7\INSTANCE"] = new(
                    new OledPanelProtectHistory(startedAt, 10250),
                    new OledPanelProtectNotification(
                        OledPanelProtectEventType.ShortTimeWithLater,
                        observedAt,
                        10258)),
                [@"MONITOR\MSI3CD7\INVALID"] = new(
                    new OledPanelProtectHistory(startedAt, -1),
                    new OledPanelProtectNotification(
                        OledPanelProtectEventType.None,
                        observedAt,
                        10258)),
                [@"MONITOR\MSI3CD7\EMPTY"] = new(
                    null,
                    new OledPanelProtectNotification(
                        OledPanelProtectEventType.None,
                        observedAt,
                        10258))
            });

            Dictionary<string, OledPanelProtectState> state =
                storage.LoadOledPanelProtectState();

            OledPanelProtectState saved =
                state[@"monitor\msi3cd7\instance"];
            Assert.Equal(startedAt.ToUniversalTime(), saved.History!.LastStartedAtUtc);
            Assert.Equal(10250, saved.History.TotalUsageHoursAtStart);
            Assert.Equal(
                OledPanelProtectEventType.ShortTimeWithLater,
                saved.Notification!.Type);
            Assert.Equal(
                observedAt.ToUniversalTime(),
                saved.Notification.FirstObservedAtUtc);
            Assert.Equal(10258, saved.Notification.TotalUsageHoursAtObservation);

            OledPanelProtectState invalid =
                state[@"monitor\msi3cd7\invalid"];
            Assert.Null(invalid.History?.TotalUsageHoursAtStart);
            Assert.Null(invalid.Notification);
            Assert.DoesNotContain(
                @"monitor\msi3cd7\empty",
                state.Keys,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void LoadOledState_ReturnsEmptyForMissingOrMalformedFile()
    {
        string testDirectory = CreateTestDirectory();
        string settingsPath = Path.Combine(testDirectory, "settings.json");

        try
        {
            var storage = new StorageService(settingsPath);
            Assert.Empty(storage.LoadOledPanelProtectState());

            File.WriteAllText(
                Path.Combine(testDirectory, "oled-care-state.json"),
                "{ definitely not valid JSON");

            Assert.Empty(storage.LoadOledPanelProtectState());
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
