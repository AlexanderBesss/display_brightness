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
            _ => { },
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
            _ => { },
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
            value => committedBrightness = value,
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

    private sealed class FakeOledCareService : IOledCareService
    {
        public int StartCount { get; private set; }

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
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult(new PixelRefreshResult(true, "started"));
        }
    }
}
