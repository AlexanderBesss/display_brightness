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
        var viewModel = new MonitorSliderViewModel(
            CreateMonitor(),
            50,
            _ => true,
            oledService,
            new RejectingDialogService());
        await WaitUntilAsync(() => viewModel.CanRunPixelRefresh);

        viewModel.RunPixelRefreshCommand.Execute(null);
        await Task.Delay(25);

        Assert.Equal(0, oledService.StartCount);
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
            return Task.FromResult(new PixelRefreshResult(true, "started"));
        }
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
