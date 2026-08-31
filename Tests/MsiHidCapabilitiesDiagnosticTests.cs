using System.Runtime.InteropServices;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class MsiHidCapabilitiesDiagnosticTests
{
    [Fact]
    public void PrintConnectedMsiHidCapabilities()
    {
        List<string> paths = MsiHidNative.EnumerateDevicePaths(
            OledCompatibilityRegistry.MsiVendorId,
            [0x3FA4]);

        Console.WriteLine($"MSI HID paths: {paths.Count}");
        foreach (string path in paths)
        {
            Console.WriteLine($"Path: {path}");
            using var handle = MsiHidNative.OpenDevice(path);
            if (handle.IsInvalid)
            {
                Console.WriteLine($"Open failed: {Marshal.GetLastWin32Error()}");
                continue;
            }

            bool success = MsiHidNative.TryGetReportCapabilities(
                handle,
                out HidReportCapabilities? capabilities);
            Console.WriteLine($"Caps success: {success}; {capabilities}");
        }

        Assert.NotEmpty(paths);
    }

    [Fact]
    public void DumpAllMsiHidInterfaces()
    {
        List<string> paths = MsiHidNative.EnumerateVendorPaths(
            OledCompatibilityRegistry.MsiVendorId);

        Console.WriteLine($"MSI vendor HID interfaces: {paths.Count}");
        foreach (string path in paths)
        {
            Console.WriteLine($"Path: {path}");
            using var handle = MsiHidNative.OpenDevice(path);
            if (handle.IsInvalid)
            {
                Console.WriteLine($"  Open failed: {Marshal.GetLastWin32Error()}");
                continue;
            }

            bool success = MsiHidNative.TryGetReportCapabilities(
                handle,
                out HidReportCapabilities? capabilities);
            Console.WriteLine($"  Caps: {success}; {capabilities}");
        }

        Assert.NotEmpty(paths);
    }

    [Fact]
    public async Task ProbeOledRegisters()
    {
        var transport = new MsiHidTransport();

        string[] codes =
        [
            "00130", // serial number (sanity, documented working)
            "001<0", // firmware version (documented working)
            "00;00", // 0xB00 PIXEL_SHIFT
            "00;10", // 0xB10 UNKNOWN_B10 (OLED protection related)
            "00;11", // 0xB11 UNKNOWN_B11 (OLED protection related)
            "00;30", // 0xB30 UNKNOWN_B30
            "00;90", // 0xB90 ProtectNotice
        ];

        foreach (string code in codes)
        {
            HidOperationResult result = await transport.GetAsync(
                OledCompatibilityRegistry.MsiVendorId,
                [0x3FA4],
                code,
                CancellationToken.None);

            Console.WriteLine(
                $"GET {code}: state={result.State} value={result.Value} message={result.Message}");
        }
    }
}
