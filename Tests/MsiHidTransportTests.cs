using System.Text;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class MsiHidTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_OnlyReachTheSelectedMonitor(bool write)
    {
        var commands = new List<(string Path, MsiHidTransport.Command Command)>();
        var transport = new MsiHidTransport((_, _) => ["usb-A", "usb-B"],
            (path, command, _) =>
            {
                commands.Add((path, command));
                return Task.FromResult(HidOperationResult.Ok(command.FeatureCode == "00130"
                    ? (path == "usb-A" ? "SERIAL-A" : "SERIAL-B") : "001"));
            });

        var result = write
            ? await transport.SetNoAckAsync(0x1462, [0x3FA4], "SERIAL-B", "00;10", "001", default)
            : await transport.GetAsync(0x1462, [0x3FA4], "SERIAL-B", "00;10", default);

        Assert.Equal(HidOperationState.Success, result.State);
        var selected = Assert.Single(commands, c => c.Command.FeatureCode != "00130");
        Assert.Equal("usb-B", selected.Path);
        Assert.Equal(write, selected.Command.IsSet);
    }

    [Theory]
    [InlineData("", "SERIAL-A", "SERIAL-B")]
    [InlineData("000", "SERIAL-A", "SERIAL-B")]
    [InlineData("SERIAL-MISSING", "SERIAL-A", "SERIAL-B")]
    [InlineData("SERIAL-A", "SERIAL-A", "SERIAL-A")]
    [InlineData("SERIAL", "SERIAL-A", "SERIAL-B")]
    public async Task MissingOrAmbiguousIdentity_NeverSendsPanelProtect(string selected, string first, string second)
    {
        int writes = 0;
        var transport = new MsiHidTransport((_, _) => ["usb-A", "usb-B"],
            (path, command, _) =>
            {
                if (command.IsSet) writes++;
                return Task.FromResult(HidOperationResult.Ok(path == "usb-A" ? first : second));
            });

        var result = await transport.SetNoAckAsync(0x1462, [0x3FA4], selected, "00;10", "001", default);
        Assert.NotEqual(HidOperationState.Success, result.State);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task CancellationAfterIdentification_PreventsTheWrite()
    {
        using var cancellation = new CancellationTokenSource();
        int writes = 0;
        var transport = new MsiHidTransport((_, _) => ["usb-A"], (_, command, _) =>
        {
            if (command.IsSet) writes++;
            cancellation.Cancel();
            return Task.FromResult(HidOperationResult.Ok("SERIAL-A"));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SetNoAckAsync(0x1462, [0x3FA4], "SERIAL-A", "00;10", "001", cancellation.Token));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void BuildOutputReport_PrependsReportIdAndPadsFrame()
    {
        string command = MsiProtocol.SetCommand("00;10", "001");
        byte[]? report = MsiHidTransport.BuildOutputReport(command, 64, 0x01);

        Assert.NotNull(report);
        Assert.Equal(64, report.Length);
        Assert.Equal(1, report[0]);
        Assert.Equal(command, Encoding.ASCII.GetString(report, 1, command.Length));
        Assert.All(
            report[(command.Length + 1)..],
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void BuildOutputReport_RejectsOversizedCommand()
    {
        Assert.Null(MsiHidTransport.BuildOutputReport("too long", 4, 0x01));
    }

    [Fact]
    public void GetCommand_UsestheDocumentedFrame()
    {
        Assert.Equal("5800;10\r", MsiProtocol.GetCommand("00;10"));
        Assert.Equal(
            "6800;30\r",
            MsiProtocol.GetScalerEventCommand("00;30"));
        Assert.Equal("5b00;10001\r", MsiProtocol.SetCommand("00;10", "001"));
    }

    [Theory]
    [InlineData("6b00;30000", "000")]
    [InlineData("6b00;3000=", "00=")]
    public void TryParseScalerEventReply_ExtractsEventPayload(
        string payload,
        string expectedValue)
    {
        Assert.True(MsiProtocol.TryParseScalerEventReply(
            payload,
            "00;30",
            out string value));
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("5b00;10001", "00;10", "001")]
    [InlineData("5b00;10", "00;10", "")]
    public void TryParseReply_ExtractsValueAfterEchoedCode(
        string payload,
        string featureCode,
        string expectedValue)
    {
        Assert.True(MsiProtocol.TryParseReply(payload, featureCode, out string value));
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("5b00;30000", "00;10")]
    [InlineData("6b00;30000", "00;30")]
    [InlineData("5600-", "00;10")]
    public void TryParseReply_IgnoresUnrelatedReports(string payload, string featureCode)
    {
        Assert.False(MsiProtocol.TryParseReply(payload, featureCode, out _));
    }

    [Fact]
    public async Task GetAsync_HonorsPreCancelledTokenWithoutTouchingHardware()
    {
        var transport = new MsiHidTransport();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.GetAsync(
                OledCompatibilityRegistry.MsiVendorId,
                [0x3FA4],
                "TEST-SERIAL",
                OledCompatibilityRegistry.PanelProtectCode,
                source.Token));
    }
}
