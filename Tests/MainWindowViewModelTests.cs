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
    public void TrayWheel_UsesPrimaryMonitorFromCurrentMonitorList()
    {
        string stalePrimaryDisplayName =
            System.Windows.Forms.Screen.PrimaryScreen?.DeviceName
            ?? @"\\.\DISPLAY1";
        var staleMonitor = CreateMonitor(
            @"MONITOR\OLD0001\instance",
            stalePrimaryDisplayName,
            "Disconnected display");
        var currentPrimaryMonitor = CreateMonitor(
            @"MONITOR\NEW0001\instance",
            @"\\.\DISPLAY-CURRENT",
            "Current primary display");
        var display = new RecordingDisplayService(
            staleMonitor,
            currentPrimaryMonitor);
        var viewModel = new MainWindowViewModel(
            display,
            new FakeStorageService(),
            new UnsupportedOledCareService(),
            new RejectingDialogService());

        foreach (MonitorSliderViewModel monitor in viewModel.Monitors)
            monitor.IsPrimary = monitor.DisplayName == currentPrimaryMonitor.DisplayName;

        bool adjusted = viewModel.AdjustPrimaryBrightness(2);

        Assert.True(adjusted);
        Assert.Equal(currentPrimaryMonitor.DevicePath, display.LastAdjustedDevicePath);
    }

    [Fact]
    public async Task OledHistory_ReappearsAfterMonitorReload()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var storage = new FakeStorageService();
        storage.OledState[@"MONITOR\MSI3CD7\instance"] =
            new OledPanelProtectState(
                new OledPanelProtectHistory(
                    now - TimeSpan.FromMinutes(10),
                    100),
                null);
        var display = new FakeDisplayService(oled: true);
        var viewModel = new MainWindowViewModel(
            display,
            storage,
            new ReadyOledCareService(),
            new RejectingDialogService(),
            timeProvider);

        Assert.EndsWith(
            "Panel Protect completed",
            viewModel.Monitors.Single().LastPanelProtectExplanationText);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await viewModel.RefreshIfStaleAsync();

        Assert.Equal(2, display.EnumerationCount);
        Assert.EndsWith(
            "Panel Protect completed",
            viewModel.Monitors.Single().LastPanelProtectExplanationText);
        viewModel.Monitors.Single().Dispose();
    }

    [Fact]
    public async Task OledNotification_ReappearsAfterMonitorReload()
    {
        var now = new DateTimeOffset(2026, 9, 1, 17, 30, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var storage = new FakeStorageService();
        storage.OledState[@"MONITOR\MSI3CD7\instance"] =
            new OledPanelProtectState(
                null,
                new OledPanelProtectNotification(
                    OledPanelProtectEventType.ShortTime,
                    now - TimeSpan.FromMinutes(5),
                    105));
        var display = new FakeDisplayService(oled: true);
        var viewModel = new MainWindowViewModel(
            display,
            storage,
            new ReadyOledCareService(),
            new RejectingDialogService(),
            timeProvider);

        Assert.True(viewModel.Monitors.Single()
            .HasPendingPanelProtectNotification);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await viewModel.RefreshIfStaleAsync();

        Assert.True(viewModel.Monitors.Single()
            .HasPendingPanelProtectNotification);
        viewModel.Monitors.Single().Dispose();
    }

    [Fact]
    public async Task TrayRefresh_IsLimitedToOncePerMinute()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var display = new FakeDisplayService();
        var viewModel = new MainWindowViewModel(
            display,
            new FakeStorageService(),
            new UnsupportedOledCareService(),
            new RejectingDialogService(),
            timeProvider);

        await viewModel.RefreshIfStaleAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(59));
        await viewModel.RefreshIfStaleAsync();

        Assert.Equal(1, display.EnumerationCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await viewModel.RefreshIfStaleAsync();

        Assert.Equal(2, display.EnumerationCount);
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
        public int EnumerationCount { get; private set; }

        public List<MonitorInfo> GetExternalMonitors()
        {
            EnumerationCount++;
            return [_monitor];
        }

        public int? GetBrightness(MonitorInfo monitor) => 50;

        public bool SetBrightness(MonitorInfo monitor, int brightness)
        {
            SetCount++;
            return false;
        }
    }

    private sealed class RecordingDisplayService(params MonitorInfo[] monitors)
        : IDisplayService
    {
        public string? LastAdjustedDevicePath { get; private set; }

        public List<MonitorInfo> GetExternalMonitors() => [.. monitors];

        public int? GetBrightness(MonitorInfo monitor) => 50;

        public bool SetBrightness(MonitorInfo monitor, int brightness)
        {
            LastAdjustedDevicePath = monitor.DevicePath;
            return true;
        }
    }

    private static MonitorInfo CreateMonitor(
        string devicePath,
        string displayName,
        string friendlyName) => new()
        {
            DevicePath = devicePath,
            DisplayName = displayName,
            FriendlyName = friendlyName,
            ModelName = devicePath.Split('\\')[1]
        };

    private sealed class FakeStorageService : IStorageService
    {
        public Dictionary<string, int> Settings { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, OledPanelProtectState> OledState { get; } =
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

        public Dictionary<string, OledPanelProtectState>
            LoadOledPanelProtectState() =>
            new(OledState, StringComparer.OrdinalIgnoreCase);

        public void SaveOledPanelProtectState(
            Dictionary<string, OledPanelProtectState> state)
        {
            OledState.Clear();
            foreach (var (key, value) in state)
                OledState[key] = value;
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

        public Task<int?> GetTotalUsageHoursAsync(
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

        public Task<int?> GetTotalUsageHoursAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(100);

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

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }
}
