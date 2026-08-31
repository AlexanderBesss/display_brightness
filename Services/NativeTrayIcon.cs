using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayBrightness.Services;

internal sealed class NativeTrayIcon : NativeWindow, IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifGuid = 0x00000020;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const int WmApp = 0x8000;
    private const int WmInput = 0x00FF;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int TrayCallbackMessage = WmApp + 1;
    private const int MaximumTooltipLength = 127;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort RawMouseWheel = 0x0400;
    private const int WheelDelta = 120;
    private const uint RawInputError = uint.MaxValue;

    private static readonly Guid TrayIconGuid =
        new("1B71E3C2-4F25-4CD8-9767-3E17C9C83B39");

    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _windowHandle;
    private Icon _icon;
    private string _tooltip;
    private int _wheelRemainder;
    private bool _isWheelEnabled;
    private bool _isDisposed;

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action<int>? MouseWheel;

    public bool IsWheelEnabled
    {
        get => _isWheelEnabled;
        set
        {
            _isWheelEnabled = value;
            if (!value)
                _wheelRemainder = 0;
        }
    }

    public NativeTrayIcon(Icon icon, string tooltip)
    {
        _icon = icon;
        _tooltip = NormalizeTooltip(tooltip);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        CreateHandle(new CreateParams
        {
            Caption = "Brightness Tray Icon Window"
        });
        _windowHandle = Handle;

        try
        {
            AddIcon();
            RegisterForRawMouseInput();
        }
        catch
        {
            DestroyHandle();
            _icon.Dispose();
            throw;
        }
    }

    public void Update(Icon icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Icon previousIcon = _icon;
        _icon = icon;
        _tooltip = NormalizeTooltip(tooltip);

        try
        {
            NotifyIconData data = CreateIconData();
            if (!ShellNotifyIcon(NimModify, ref data))
                AddIcon();
        }
        finally
        {
            previousIcon.Dispose();
        }
    }

    public bool IsPointOverIcon(Point point)
    {
        if (_isDisposed)
            return false;

        NotifyIconIdentifier identifier = new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            WindowHandle = _windowHandle,
            Id = 0,
            Guid = TrayIconGuid
        };

        return ShellNotifyIconGetRect(ref identifier, out NativeRect bounds) == 0 &&
               point.X >= bounds.Left && point.X < bounds.Right &&
               point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == TrayCallbackMessage)
        {
            int notification = unchecked((ushort)(long)message.LParam);
            if (notification == WmLeftButtonUp)
                LeftClick?.Invoke();
            else if (notification == WmRightButtonUp)
                RightClick?.Invoke();
        }
        else if (message.Msg == WmInput)
        {
            ProcessRawMouseInput(message.LParam);
        }
        else if ((uint)message.Msg == _taskbarCreatedMessage)
        {
            try
            {
                AddIcon();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore tray icon: {ex.Message}");
            }
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        IsWheelEnabled = false;

        UnregisterRawMouseInput();

        NotifyIconData data = CreateIconData();
        ShellNotifyIcon(NimDelete, ref data);

        DestroyHandle();
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }

    private void AddIcon()
    {
        NotifyIconData data = CreateIconData();
        if (!ShellNotifyIcon(NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create tray icon.");

        data.VersionOrTimeout = NotifyIconVersion4;
        if (!ShellNotifyIcon(NimSetVersion, ref data))
            Debug.WriteLine("Failed to set tray icon notification version.");
    }

    private void ProcessRawMouseInput(IntPtr rawInputHandle)
    {
        uint dataSize = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint sizeResult = GetRawInputData(
            rawInputHandle,
            RidInput,
            IntPtr.Zero,
            ref dataSize,
            headerSize);
        if (sizeResult == RawInputError || dataSize < headerSize + 8)
            return;

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)dataSize));
        try
        {
            uint readSize = dataSize;
            uint readResult = GetRawInputData(
                rawInputHandle,
                RidInput,
                buffer,
                ref readSize,
                headerSize);
            if (readResult == RawInputError || readResult != dataSize)
                return;

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 0)
                return;

            int mouseDataOffset = checked((int)headerSize);
            ushort buttonFlags = unchecked((ushort)Marshal.ReadInt16(buffer, mouseDataOffset + 4));
            if ((buttonFlags & RawMouseWheel) == 0)
                return;

            int delta = Marshal.ReadInt16(buffer, mouseDataOffset + 6);
            if (!_isWheelEnabled)
                return;

            Point cursorPosition = Cursor.Position;
            if (!IsPointOverIcon(cursorPosition))
            {
                _wheelRemainder = 0;
                return;
            }

            _wheelRemainder += delta;
            int detents = _wheelRemainder / WheelDelta;
            _wheelRemainder %= WheelDelta;

            if (detents != 0)
                MouseWheel?.Invoke(detents);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tray raw mouse input failed: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void RegisterForRawMouseInput()
    {
        RawInputDevice[] devices =
        [
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = MouseUsage,
                Flags = RidevInputSink,
                TargetWindow = _windowHandle
            }
        ];

        if (!RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to register tray mouse-wheel input.");
        }

    }

    private static void UnregisterRawMouseInput()
    {
        RawInputDevice[] devices =
        [
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = MouseUsage,
                Flags = RidevRemove,
                TargetWindow = IntPtr.Zero
            }
        ];

        if (!RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            Debug.WriteLine(
                $"Failed to unregister tray mouse input: {Marshal.GetLastWin32Error()}");
        }
    }

    private NotifyIconData CreateIconData()
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 0,
            Flags = NifMessage | NifIcon | NifTip | NifGuid | NifShowTip,
            CallbackMessage = TrayCallbackMessage,
            IconHandle = _icon.Handle,
            Tip = _tooltip,
            Info = string.Empty,
            InfoTitle = string.Empty,
            Guid = TrayIconGuid
        };
    }

    private static string NormalizeTooltip(string tooltip)
    {
        string value = tooltip ?? string.Empty;
        return value.Length <= MaximumTooltipLength
            ? value
            : value[..MaximumTooltipLength];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr TargetWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(
        ref NotifyIconIdentifier identifier,
        out NativeRect iconLocation);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices,
        uint deviceCount,
        uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInputHandle,
        uint command,
        IntPtr data,
        ref uint dataSize,
        uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
