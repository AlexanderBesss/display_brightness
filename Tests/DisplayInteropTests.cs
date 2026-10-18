using System.Runtime.InteropServices;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

public sealed class DisplayInteropTests
{
    [Fact]
    public void ChildEnumeration_PreservesTheParentDeviceName()
    {
        // Read-only API smoke test: production import against an explicit UTF-16 import; no DDC, WMI brightness, or HID commands.
        for (uint index = 0; index < 16; index++)
        {
            var adapter = NewDevice();
            if (!EnumUnicode(null, index, ref adapter, 0)) break;
            var expected = NewDevice();
            var actual = NewDevice();
            bool found = EnumUnicode(adapter.DeviceName, 0, ref expected, 1);
            Assert.Equal(found, DisplayInterop.EnumDisplayDevicesW(adapter.DeviceName, 0, ref actual, 1));
            if (found)
                Assert.Equal(expected.DeviceID, actual.DeviceID);
        }
    }

    private static DisplayInterop.DISPLAY_DEVICEW NewDevice() =>
        new() { cb = (uint)Marshal.SizeOf<DisplayInterop.DISPLAY_DEVICEW>() };

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumUnicode(string? device, uint index,
        ref DisplayInterop.DISPLAY_DEVICEW information, uint flags);
}
