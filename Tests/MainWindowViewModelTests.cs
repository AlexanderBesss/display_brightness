using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.ViewModels;

namespace DisplayBrightness.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void FailedBrightnessWrite_IsNotDisplayedOrPersisted()
    {
        var storage = new FakeStorageService();
        var display = new FakeDisplayService();
        var viewModel = new MainWindowViewModel(
            display,
            storage,
            new UnsupportedOledCareService(),
            new RejectingDialogService());

        bool adjusted = viewModel.Monitors.Single().AdjustBrightness(10);

        Assert.False(adjusted);
        Assert.Equal(50, viewModel.Monitors.Single().BrightnessValue);
        Assert.Empty(storage.Settings);
        Assert.Equal(0, storage.SaveCount);
        Assert.Equal(1, display.SetCount);
    }

    [Fact]
    public void FailedStartupRegistration_DoesNotLeaveToggleEnabled()
    {
        var storage = new FakeStorageService { StartupWriteSucceeds = false };
        var viewModel = new MainWindowViewModel(
            new FakeDisplayService(),
            storage,
            new UnsupportedOledCareService(),
            new RejectingDialogService());

        viewModel.StartOnStartup = true;

        Assert.False(viewModel.StartOnStartup);
    }

    private sealed class FakeDisplayService : IDisplayService
    {
        private readonly MonitorInfo _monitor = new()
        {
            DevicePath = @"MONITOR\GBT2800\instance",
            DisplayName = @"\\.\DISPLAY1",
            FriendlyName = "Test display",
            ModelName = "GBT2800"
        };

        public int SetCount { get; private set; }

        public List<MonitorInfo> GetExternalMonitors() => [_monitor];

        public int? GetBrightness(MonitorInfo monitor) => 50;

        public bool SetBrightness(MonitorInfo monitor, int brightness)
        {
            SetCount++;
            return false;
        }
    }

    private sealed class FakeStorageService : IStorageService
    {
        public Dictionary<string, int> Settings { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int SaveCount { get; private set; }
        public bool StartupWriteSucceeds { get; init; } = true;

        public Dictionary<string, int> LoadSettings() =>
            new(Settings, StringComparer.OrdinalIgnoreCase);

        public void SaveSettings(Dictionary<string, int> settings)
        {
            SaveCount++;
            Settings.Clear();
            foreach (var (key, value) in settings)
                Settings[key] = value;
        }

        public bool GetStartOnStartup() => false;

        public bool SetStartOnStartup(bool enabled) => StartupWriteSucceeds;
    }

    private sealed class UnsupportedOledCareService : IOledCareService
    {
        public OledSupportLevel GetSupportLevel(MonitorInfo monitor) =>
            OledSupportLevel.Unsupported;

        public Task<OledCareStatus> GetStatusAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unsupported displays must not be queried.");

        public Task<PixelRefreshResult> StartPixelRefreshAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unsupported displays must not be queried.");
    }

    private sealed class RejectingDialogService : IUserDialogService
    {
        public bool ConfirmPixelRefresh(
            MonitorInfo monitor,
            OledSupportLevel supportLevel) => false;
    }
}
