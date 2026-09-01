using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.ViewModels;

namespace DisplayBrightness.Tests;

public sealed class MonitorSliderViewModelTests
{
    [Fact]
    public async Task CancelledConfirmation_DoesNotSendPixelRefresh()
    {
        var oledService = new FakeOledCareService();
        int saveCount = 0;
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            oledService,
            new RejectingDialogService(),
            savePanelProtectHistory: _ => saveCount++);
        await WaitUntilAsync(() => viewModel.CanRunPixelRefresh);

        viewModel.RunPixelRefreshCommand.Execute(null);
        await Task.Delay(25);

        Assert.Equal(0, oledService.StartCount);
        Assert.Equal(0, saveCount);
        viewModel.Dispose();
    }

    [Fact]
    public async Task SuccessfulPixelRefresh_RecordsTimestampAndUsageHours()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var oledService = new FakeOledCareService();
        OledPanelProtectHistory? savedHistory = null;
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            oledService,
            new AcceptingDialogService(),
            savePanelProtectHistory: history => savedHistory = history,
            timeProvider: new FixedTimeProvider(now));
        await WaitUntilAsync(() => viewModel.CanRunPixelRefresh);

        viewModel.RunPixelRefreshCommand.Execute(null);
        await WaitUntilAsync(() => savedHistory != null);

        Assert.Equal(now, savedHistory!.LastStartedAtUtc);
        Assert.Equal(100, savedHistory.TotalUsageHoursAtStart);
        Assert.Equal(
            "100 total panel hours · Last panel protect started: just now",
            viewModel.OledSummaryText);
        viewModel.Dispose();
    }

    [Fact]
    public async Task FailedPixelRefresh_DoesNotOverwriteHistory()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var originalHistory = new OledPanelProtectHistory(
            now - TimeSpan.FromMinutes(30),
            90);
        var oledService = new FakeOledCareService
        {
            StartResult = new PixelRefreshResult(false, "could not start")
        };
        int saveCount = 0;
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            oledService,
            new AcceptingDialogService(),
            originalHistory,
            _ => saveCount++,
            new FixedTimeProvider(now));
        await WaitUntilAsync(() => viewModel.CanRunPixelRefresh);

        viewModel.RunPixelRefreshCommand.Execute(null);
        await WaitUntilAsync(() => oledService.StartCount == 1);

        Assert.Equal(0, saveCount);
        Assert.Equal(
            "Last panel protect started: 30m ago",
            viewModel.LastPanelProtectText);
        viewModel.Dispose();
    }

    [Fact]
    public async Task PixelRefreshException_DoesNotOverwriteHistory()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var oledService = new FakeOledCareService
        {
            StartException = new InvalidOperationException("transport failed")
        };
        int saveCount = 0;
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            oledService,
            new AcceptingDialogService(),
            new OledPanelProtectHistory(now - TimeSpan.FromHours(2), 90),
            _ => saveCount++,
            new FixedTimeProvider(now));
        await WaitUntilAsync(() => viewModel.CanRunPixelRefresh);

        viewModel.RunPixelRefreshCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.OledStatusText.Contains("transport failed"));

        Assert.Equal(0, saveCount);
        Assert.Equal(
            "Last panel protect started: 2h 0m ago",
            viewModel.LastPanelProtectText);
        viewModel.Dispose();
    }

    [Theory]
    [InlineData(null, "Last panel protect: not tracked yet")]
    [InlineData(-5, "Last panel protect started: just now")]
    [InlineData(0, "Last panel protect started: just now")]
    [InlineData(1, "Last panel protect started: 1m ago")]
    [InlineData(59, "Last panel protect started: 59m ago")]
    [InlineData(60, "Last panel protect started: 1h 0m ago")]
    [InlineData(130, "Last panel protect started: 2h 10m ago")]
    public void LastPanelProtectText_FormatsElapsedWallTime(
        int? elapsedMinutes,
        string expected)
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        OledPanelProtectHistory? history = elapsedMinutes.HasValue
            ? new OledPanelProtectHistory(
                now - TimeSpan.FromMinutes(elapsedMinutes.Value),
                100)
            : null;

        string result = MonitorSliderViewModel.FormatLastPanelProtectText(
            history,
            now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void PersistedHistory_IsDisplayedAndTimerStopsOnDispose()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            new FakeOledCareService(),
            new RejectingDialogService(),
            new OledPanelProtectHistory(now - TimeSpan.FromMinutes(75), 90),
            timeProvider: new FixedTimeProvider(now));

        Assert.Equal(
            "Last panel protect started: 1h 15m ago",
            viewModel.LastPanelProtectText);
        Assert.True(viewModel.IsPanelProtectHistoryTimerRunning);

        viewModel.Dispose();

        Assert.False(viewModel.IsPanelProtectHistoryTimerRunning);
    }

    [Theory]
    [InlineData(144.0, true, "144 Hz")]
    [InlineData(59.97, true, "59.97 Hz")]
    [InlineData(0, false, "")]
    public void RefreshRate_IsExposedFromMonitor(
        double rate, bool hasRefreshRate, string expectedText)
    {
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(rate),
            50,
            _ => true,
            new FakeOledCareService(),
            new RejectingDialogService());

        Assert.Equal(hasRefreshRate, viewModel.HasRefreshRate);
        Assert.Equal(expectedText, viewModel.RefreshRateText);
    }

    [Fact]
    public void OledSupport_DoesNotChangeBrightnessCommitBehavior()
    {
        int? committedBrightness = null;
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            value =>
            {
                committedBrightness = value;
                return true;
            },
            new FakeOledCareService(),
            new RejectingDialogService());

        bool adjusted = viewModel.AdjustBrightness(2);

        Assert.True(adjusted);
        Assert.Equal(52, viewModel.BrightnessValue);
        Assert.Equal(52, committedBrightness);
    }

    private static MonitorInfo CreateMonitor(double refreshRateHz = 0) => new()
    {
        HardwareId = "MSI3CD7",
        ManufacturerCode = "MSI",
        FriendlyName = "MPG271QX OLED",
        ModelName = "MSI3CD7",
        RefreshRateHz = refreshRateHz
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }

    private sealed class RejectingDialogService : IUserDialogService
    {
        public bool ConfirmPixelRefresh(
            MonitorInfo monitor,
            OledSupportLevel supportLevel) => false;
    }

    private sealed class AcceptingDialogService : IUserDialogService
    {
        public bool ConfirmPixelRefresh(
            MonitorInfo monitor,
            OledSupportLevel supportLevel) => true;
    }

    [Fact]
    public async Task OledStatus_UpdatesRefreshRateFromMonitor()
    {
        var oledService = new FakeOledCareService
        {
            Status = new OledCareStatus(
                OledSupportLevel.Verified,
                OledConnectionState.Ready,
                new OledPanelInfo(1, 100),
                "ready",
                240)
        };
        var monitor = CreateMonitor(360.0);
        var viewModel = new MonitorSliderViewModel(
            monitor,
            50,
            _ => true,
            oledService,
            new RejectingDialogService());

        await WaitUntilAsync(() => viewModel.RefreshRateText == "240 Hz");

        Assert.Equal(240.0, monitor.RefreshRateHz);
    }

    [Fact]
    public void FailedCommit_RestoresLastAppliedBrightness()
    {
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => false,
            new FakeOledCareService(),
            new RejectingDialogService());

        viewModel.BrightnessValue = 75;
        viewModel.CommitBrightness();

        Assert.Equal(50, viewModel.BrightnessValue);
        Assert.False(viewModel.AdjustBrightness(2));
        Assert.Equal(50, viewModel.BrightnessValue);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(120, 100)]
    public void BrightnessValue_ClampsValuesOutsideMonitorRange(
        double requested,
        double expected)
    {
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            new FakeOledCareService(),
            new RejectingDialogService());

        viewModel.BrightnessValue = requested;

        Assert.Equal(expected, viewModel.BrightnessValue);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightInitialStatusRead()
    {
        var oledService = new CancellableOledCareService();
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            oledService,
            new RejectingDialogService());

        await oledService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Dispose();

        await oledService.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FakeOledCareService : IOledCareService
    {
        public int StartCount { get; private set; }
        public PixelRefreshResult StartResult { get; set; } =
            new(true, "started");
        public Exception? StartException { get; set; }
        public OledCareStatus Status { get; set; } = new(
            OledSupportLevel.Verified,
            OledConnectionState.Ready,
            new OledPanelInfo(1, 100),
            "ready");

        public OledSupportLevel GetSupportLevel(MonitorInfo monitor) =>
            OledSupportLevel.Verified;

        public Task<OledCareStatus> GetStatusAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<PixelRefreshResult> StartPixelRefreshAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartException != null
                ? Task.FromException<PixelRefreshResult>(StartException)
                : Task.FromResult(StartResult);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CancellableOledCareService : IOledCareService
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public OledSupportLevel GetSupportLevel(MonitorInfo monitor) =>
            OledSupportLevel.Verified;

        public async Task<OledCareStatus> GetStatusAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        }

        public Task<PixelRefreshResult> StartPixelRefreshAsync(
            MonitorInfo monitor,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by this test.");
    }
}
