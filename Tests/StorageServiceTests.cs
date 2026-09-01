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
}
