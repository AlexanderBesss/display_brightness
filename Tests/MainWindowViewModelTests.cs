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

    [Fact]
    public void OledHistory_ReappearsAfterMonitorReload()
    {
        var storage = new FakeStorageService();
        storage.OledHistory[@"MONITOR\MSI3CD7\instance"] =
            new OledPanelProtectHistory(
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
                100);
        var viewModel = new MainWindowViewModel(
            new FakeDisplayService(oled: true),
            storage,
            new ReadyOledCareService(),
            new RejectingDialogService());

        Assert.StartsWith(
            "Last panel protect started:",
            viewModel.Monitors.Single().LastPanelProtectText);

        viewModel.RefreshCommand.Execute(null);

        Assert.StartsWith(
            "Last panel protect started:",
            viewModel.Monitors.Single().LastPanelProtectText);
        viewModel.Monitors.Single().Dispose();
    }

    private sealed class FakeDisplayService : IDisplayService
    {
        private readonly MonitorInfo _monitor;

        public FakeDisplayService(bool oled = false)
        {
            _monitor = new MonitorInfo
            {
                DevicePath = oled
                    ? @"MONITOR\MSI3CD7\instance"
                    : @"MONITOR\GBT2800\instance",
                DisplayName = @"\\.\DISPLAY1",
                FriendlyName = oled ? "MSI OLED" : "Test display",
                ModelName = oled ? "MSI3CD7" : "GBT2800",
                HardwareId = oled ? "MSI3CD7" : "GBT2800",
                ManufacturerCode = oled ? "MSI" : "GBT"
            };
        }

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
        public Dictionary<string, OledPanelProtectHistory> OledHistory { get; } =
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

        public Dictionary<string, OledPanelProtectHistory>
            LoadOledPanelProtectHistory() =>
            new(OledHistory, StringComparer.OrdinalIgnoreCase);

        public void SaveOledPanelProtectHistory(
            Dictionary<string, OledPanelProtectHistory> history)
        {
            OledHistory.Clear();
            foreach (var (key, value) in history)
                OledHistory[key] = value;
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

    private sealed class ReadyOledCareService : IOledCareService
    {
        public OledSupportLevel GetSupportLevel(MonitorInfo monitor) =>
            OledSupportLevel.Verified;

        public Task<OledCareStatus> GetStatusAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OledCareStatus(
                OledSupportLevel.Verified,
                OledConnectionState.Ready,
                new OledPanelInfo(1, 100),
                "ready"));

        public Task<PixelRefreshResult> StartPixelRefreshAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PixelRefreshResult(true, "started"));
    }

    private sealed class RejectingDialogService : IUserDialogService
    {
        public bool ConfirmPixelRefresh(
            MonitorInfo monitor,
            OledSupportLevel supportLevel) => false;
    }
}
