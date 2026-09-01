using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class OledCareServiceTests
{
    [Fact]
    public async Task GetStatus_DoesNotContactHid_ForUnsupportedMonitor()
    {
        var transport = new FakeTransport();
        var service = new OledCareService(transport, _ => null);
        var monitor = new MonitorInfo
        {
            HardwareId = "GBT2800",
            ManufacturerCode = "GBT",
            FriendlyName = "M28U"
        };

        OledCareStatus status = await service.GetStatusAsync(monitor);

        Assert.Equal(OledConnectionState.Unsupported, status.ConnectionState);
        Assert.Equal(0, transport.GetCount);
        Assert.Equal(0, transport.SetCount);
    }

    [Fact]
    public async Task GetStatus_ParsesPanelProtectValue()
    {
        var transport = new FakeTransport
        {
            GetResult = HidOperationResult.Ok("001")
        };
        var service = new OledCareService(transport, _ => 10248);

        OledCareStatus status = await service.GetStatusAsync(CreateMsiMonitor());

        Assert.Equal(OledConnectionState.Ready, status.ConnectionState);
        Assert.NotNull(status.PanelInfo);
        Assert.Equal(1, status.PanelInfo.PanelProtect);
        Assert.Equal(10248, status.PanelInfo.TotalUsageHours);
        Assert.Equal(
            [
                OledCompatibilityRegistry.PanelProtectCode,
                OledCompatibilityRegistry.RefreshRateCode
            ],
            transport.GetCodes);
    }

    [Fact]
    public async Task GetStatus_ReadsRefreshRateAndResolvesMod256Wrap()
    {
        var transport = new FakeTransport
        {
            ResultsByCode = new Dictionary<string, HidOperationResult>
            {
                [OledCompatibilityRegistry.PanelProtectCode] = HidOperationResult.Ok("001"),
                [OledCompatibilityRegistry.RefreshRateCode] = HidOperationResult.Ok("104")
            }
        };
        var service = new OledCareService(transport, _ => null);
        var monitor = CreateMsiMonitor();
        monitor.RefreshRateHz = 360;

        OledCareStatus status = await service.GetStatusAsync(monitor);

        Assert.Equal(360, status.RefreshRateHz);
    }

    [Fact]
    public async Task GetStatus_LeavesRefreshRateNull_WhenRegisterUnreadable()
    {
        var transport = new FakeTransport
        {
            ResultsByCode = new Dictionary<string, HidOperationResult>
            {
                [OledCompatibilityRegistry.PanelProtectCode] = HidOperationResult.Ok("001"),
                [OledCompatibilityRegistry.RefreshRateCode] = HidOperationResult.Ok("nope")
            }
        };
        var service = new OledCareService(transport, _ => null);

        OledCareStatus status = await service.GetStatusAsync(CreateMsiMonitor());

        Assert.Null(status.RefreshRateHz);
    }

    [Theory]
    [InlineData("240", 240.0, 240)]
    [InlineData("144", 144.0, 144)]
    [InlineData("104", 360.0, 360)]
    [InlineData("104", 0, 104)]
    [InlineData("abc", 240.0, null)]
    public void ResolveRefreshRate_ResolvesMod256Wrap(
        string rawValue, double osHz, int? expected)
    {
        Assert.Equal(expected, OledCareService.ResolveRefreshRate(rawValue, osHz));
    }

    [Fact]
    public async Task GetStatus_KeepsUsageHours_WhenPanelRegisterIsUnreadable()
    {
        var transport = new FakeTransport
        {
            GetResult = HidOperationResult.Ok("nope")
        };
        var service = new OledCareService(transport, _ => 512);

        OledCareStatus status = await service.GetStatusAsync(CreateMsiMonitor());

        Assert.Equal(OledConnectionState.Ready, status.ConnectionState);
        Assert.Null(status.PanelInfo);
        Assert.Contains("512", status.Message);
    }

    [Fact]
    public async Task StartPixelRefresh_SendsPanelProtectWithoutWaitingForAck()
    {
        var transport = new FakeTransport();
        var service = new OledCareService(transport, _ => null);

        PixelRefreshResult result = await service.StartPixelRefreshAsync(CreateMsiMonitor());

        Assert.True(result.Started);
        Assert.Equal(OledCompatibilityRegistry.PanelProtectCode, transport.LastNoAckSetCode);
        Assert.Equal("001", transport.LastNoAckSetValue);
        Assert.Equal(1, transport.NoAckSetCount);
        Assert.Equal(0, transport.SetCount);
        Assert.Equal(0, transport.GetCount);
    }

    [Theory]
    [InlineData((int)HidOperationState.NotConnected, OledConnectionState.UsbNotConnected)]
    [InlineData((int)HidOperationState.Busy, OledConnectionState.Busy)]
    [InlineData((int)HidOperationState.TimedOut, OledConnectionState.Error)]
    public async Task GetStatus_MapsTransportFailures(
        int transportState,
        OledConnectionState expectedState)
    {
        var transport = new FakeTransport
        {
            GetResult = new HidOperationResult(
                (HidOperationState)transportState,
                string.Empty,
                "transport message")
        };
        var service = new OledCareService(transport, _ => null);

        OledCareStatus status = await service.GetStatusAsync(CreateMsiMonitor());

        Assert.Equal(expectedState, status.ConnectionState);
        Assert.Equal("transport message", status.Message);
        Assert.False(status.CanRunPixelRefresh);
    }

    [Fact]
    public async Task GetStatus_PropagatesCancellation()
    {
        var transport = new FakeTransport { CancelGets = true };
        var service = new OledCareService(transport, _ => null);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetStatusAsync(CreateMsiMonitor(), source.Token));
    }

    private static MonitorInfo CreateMsiMonitor() => new()
    {
        HardwareId = "MSI3CD7",
        ManufacturerCode = "MSI",
        FriendlyName = "MPG271QX OLED"
    };

    private sealed class FakeTransport : IMsiHidTransport
    {
        public int GetCount { get; private set; }
        public int SetCount { get; private set; }
        public int NoAckSetCount { get; private set; }
        public string? LastGetCode { get; private set; }
        public string? LastSetCode { get; private set; }
        public string? LastSetValue { get; private set; }
        public string? LastNoAckSetCode { get; private set; }
        public string? LastNoAckSetValue { get; private set; }
        public List<string> GetCodes { get; } = new();
        public HidOperationResult GetResult { get; set; } = HidOperationResult.Ok("001");
        public HidOperationResult SetResult { get; set; } = HidOperationResult.Ok();
        public bool CancelGets { get; set; }
        public Dictionary<string, HidOperationResult>? ResultsByCode { get; set; }

        public Task<HidOperationResult> GetAsync(
            ushort vendorId,
            ushort[] productIds,
            string featureCode,
            CancellationToken cancellationToken)
        {
            if (CancelGets)
                return Task.FromCanceled<HidOperationResult>(cancellationToken);

            GetCount++;
            LastGetCode = featureCode;
            GetCodes.Add(featureCode);
            if (ResultsByCode is { } byCode &&
                byCode.TryGetValue(featureCode, out var result))
                return Task.FromResult(result);
            return Task.FromResult(GetResult);
        }

        public Task<HidOperationResult> SetAsync(
            ushort vendorId,
            ushort[] productIds,
            string featureCode,
            string value,
            CancellationToken cancellationToken)
        {
            SetCount++;
            LastSetCode = featureCode;
            LastSetValue = value;
            return Task.FromResult(SetResult);
        }

        public Task<HidOperationResult> SetNoAckAsync(
            ushort vendorId,
            ushort[] productIds,
            string featureCode,
            string value,
            CancellationToken cancellationToken)
        {
            NoAckSetCount++;
            LastNoAckSetCode = featureCode;
            LastNoAckSetValue = value;
            return Task.FromResult(HidOperationResult.Ok());
        }
    }
}
