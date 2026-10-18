using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.ViewModels;

namespace DisplayBrightness.Tests;

public sealed class DisplayRecoveryTests
{
    [Theory]
    [InlineData(@"\\?\DISPLAY#MSI3CD7#instance#{guid}", @"DISPLAY\MSI3CD7\instance", true)]
    [InlineData(@"\\?\DISPLAY#MSI3CD7#instance2#{guid}", @"DISPLAY\MSI3CD7\instance", false)]
    [InlineData(@"\\?\DISPLAY#OTHER01#instance#{guid}", @"DISPLAY\MSI3CD7\instance", false)]
    public void BrightnessTarget_RequiresTheSamePhysicalDevice(string candidate, string requested, bool expected) =>
        Assert.Equal(expected, BrightnessController.MonitorIdsMatch(candidate, requested));

    [Theory]
    [InlineData(6u)]
    [InlineData(11u)]
    [InlineData(13u)]
    [InlineData(0x80000000u)]
    public void InternalConnectors_UseWmi(uint connector) =>
        Assert.IsType<WmiBrightnessController>(new DisplayService().GetController(new MonitorInfo { OutputTechnology = connector }));

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    [InlineData(10u)]
    [InlineData(16u)]
    [InlineData(18u)]
    public void ExternalConnectors_UseDdc(uint connector) =>
        Assert.IsType<BrightnessController>(new DisplayService().GetController(new MonitorInfo { OutputTechnology = connector }));

    [Theory]
    [InlineData(@"\\?\DISPLAY#MSI3CD7#instance#{guid}", @"DISPLAY\MSI3CD7\instance_0", true)]
    [InlineData(@"\\?\DISPLAY#MSI3CD7#instance#{guid}", @"DISPLAY\MSI3CD7\instance2_0", false)]
    [InlineData(@"\\?\DISPLAY#MSI3CD7#instance#{guid}", @"DISPLAY\MSI3CD7\instance_bad", false)]
    [InlineData("", @"DISPLAY\MSI3CD7\instance_0", false)]
    public void EdidIdentity_RequiresTheExactDisplayInstance(string display, string wmi, bool expected) =>
        Assert.Equal(expected, MonitorIdentityReader.InstanceMatches(display, wmi));

    [Theory]
    [InlineData(0x007E, 0, true)]
    [InlineData(0x0219, 7, false)]
    [InlineData(0x0219, 0x8000, true)]
    [InlineData(0x0219, 0x8004, true)]
    [InlineData(0x0218, 0x12, true)]
    [InlineData(0x0218, 7, true)]
    [InlineData(0x0218, 4, false)]
    [InlineData(0x0200, 0, false)]
    public void NativeEvents_TriggerRecovery(int message, long eventCode, bool expected) =>
        Assert.Equal(expected, NativeTrayIcon.IsDisplayConfigurationMessage(message, eventCode));

    [Fact]
    public async Task SlowStartup_DoesNotBlockConstruction()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var display = new FakeDisplay { Read = _ => { entered.Set(); Wait(release); return 50; } };
        using var vm = Create(display);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(vm.InitializationTask.IsCompleted);
            Assert.False(vm.CanAdjustBrightness);
            Assert.Contains("Checking", vm.DisplayStatusText);
        }
        finally { release.Set(); }
        await vm.InitializationTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.CanAdjustBrightness);
    }

    [Fact]
    public async Task Refresh_PreservesChangesMadeAfterTheReadStarted()
    {
        var display = new FakeDisplay();
        using var vm = Create(display);
        await vm.InitializationTask;
        var row = vm.Monitors.Single();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        display.Read = _ => { int value = display.Current; entered.Set(); Wait(release); return value; };
        Task refresh = vm.RefreshAsync();
        Task<bool>? write = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            row.BrightnessValue = 80;
            write = row.CommitBrightnessAsync();
            Assert.False(write.IsCompleted);
        }
        finally { release.Set(); }
        await Task.WhenAll(refresh, write!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(row, vm.Monitors.Single());
        Assert.Equal(80, row.BrightnessValue);
        Assert.Equal(80, display.Current);
        Assert.True(await row.AdjustBrightnessAsync(2));
        Assert.Equal(82, display.Current);
    }

    [Fact]
    public async Task RepeatedInput_CoalescesWhileHardwareWriteIsBlocked()
    {
        var display = new FakeDisplay();
        using var vm = Create(display);
        await vm.InitializationTask;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        display.Write = value => { entered.Set(); Wait(release); display.Current = value; return true; };
        var row = vm.Monitors.Single();
        Task<bool> first = row.AdjustBrightnessAsync(2);
        Task<bool>? last = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 10; i++) last = row.AdjustBrightnessAsync(2);
            Assert.False(first.IsCompleted);
            Assert.Equal(72, row.BrightnessValue);
        }
        finally { release.Set(); }
        await Task.WhenAll(first, last!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 52, 72 }, display.Writes);
        Assert.Equal(72, display.Current);
    }

    [Fact]
    public async Task SlowPersistence_DoesNotBlockBrightnessInput()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var storage = new FakeStorage { Save = () => { entered.Set(); Wait(release); } };
        var display = new FakeDisplay();
        using var vm = Create(display, storage);
        await vm.InitializationTask;
        Task<bool> first = vm.AdjustPrimaryBrightnessAsync(2);
        Task<bool>? next = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
            next = vm.AdjustPrimaryBrightnessAsync(10);
            Assert.Equal(62, vm.PrimaryBrightness);
        }
        finally { release.Set(); }
        await Task.WhenAll(first, next!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(62, display.Current);
        Assert.Equal(62, storage.Settings["A"]);
    }

    [Fact]
    public async Task BackgroundRecovery_FindsAReconnectedDisplayWithoutOpeningThePopup()
    {
        var display = new FakeDisplay();
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        display.Connected = false;
        await vm.HandleDisplayChangeAsync(TimeSpan.Zero);
        Assert.Empty(vm.Monitors);
        display.Connected = true;
        display.DevicePath = "B";
        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.Equal("B", vm.Monitors.Single().Monitor.DevicePath);
        Assert.True(vm.CanAdjustBrightness);
    }

    [Fact]
    public async Task UnknownBrightness_IsNotInventedOrControllable_AndRecovers()
    {
        var display = new FakeDisplay { Read = _ => null };
        using var vm = Create(display);
        await vm.InitializationTask;
        var row = vm.Monitors.Single();
        Assert.False(vm.CanAdjustBrightness);
        Assert.Null(vm.PrimaryBrightness);
        Assert.Equal("Unavailable", row.BrightnessText);
        Assert.False(await vm.AdjustPrimaryBrightnessAsync(2));
        Assert.Empty(display.Writes);
        display.Read = _ => 70;
        await vm.RefreshAsync();
        Assert.Same(row, vm.Monitors.Single());
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(70, vm.PrimaryBrightness);
    }

    [Fact]
    public async Task ReadTimeout_IsRetriedOnceWhenTheDisplayWasAnsweringBefore()
    {
        var display = new FakeDisplay();
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        Assert.Equal(50, vm.PrimaryBrightness);

        int attempts = 0;
        display.Read = _ => Interlocked.Increment(ref attempts) == 1 ? null : 42;
        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(42, vm.PrimaryBrightness);
        Assert.True(vm.CanAdjustBrightness);
    }

    [Fact]
    public async Task NeverReadableDisplay_IsNotRetriedOnEveryCycle()
    {
        int attempts = 0;
        var display = new FakeDisplay { Read = _ => { Interlocked.Increment(ref attempts); return null; } };
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        Assert.Equal(1, attempts);

        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.Equal(2, attempts);
        Assert.False(vm.CanAdjustBrightness);
    }

    [Fact]
    public async Task UnavailableRow_KeepsTheRetryBecauseItOnceAnswered()
    {
        var display = new FakeDisplay();
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;

        // Two consecutive failed cycles take an established display to Unavailable.
        display.Read = _ => null;
        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.False(vm.CanAdjustBrightness);

        // A panel that NAKs the first message after the bus idles but answers the
        // retry must recover without the user clicking Refresh: the retry a healthy
        // row paid is still paid while the row is Unavailable.
        int attempts = 0;
        display.Read = _ => Interlocked.Increment(ref attempts) == 1 ? null : 66;
        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.Equal(2, attempts);
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(66, vm.PrimaryBrightness);
    }

    [Fact]
    public async Task StartupReadFailure_IsRetriedWhenTheDisplayHasAStoredBrightness()
    {
        // A reconnect that changed the device path produces a brand-new row whose
        // first read can time out while the panel wakes. The stored brightness from
        // the previous session marks this physical display as one that answers
        // DDC/CI, so the initial failure gets the paced retry.
        var storage = new FakeStorage { Settings = { ["A"] = 40 } };
        var display = new FakeDisplay();
        int attempts = 0;
        display.Read = _ => Interlocked.Increment(ref attempts) == 1 ? null : 61;
        using var vm = new MainWindowViewModel(
            display, storage, new NoOled(), new RejectDialog(), new AdjustableTime());
        await vm.InitializationTask;
        Assert.Equal(2, attempts);
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(61, vm.PrimaryBrightness);
    }

    [Fact]
    public async Task TransientReadFailure_KeepsControlsUntilASecondReadAlsoFails()
    {
        var display = new FakeDisplay();
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        var row = vm.Monitors.Single();

        display.Read = _ => null;
        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.True(row.CanAdjustBrightness);
        Assert.Equal("50%", row.BrightnessText);
        Assert.Contains("could not be read", row.BrightnessStatusText);

        time.Now += TimeSpan.FromMinutes(1);
        await vm.RefreshIfStaleAsync();
        Assert.False(row.CanAdjustBrightness);
        Assert.Contains("Brightness unavailable", row.BrightnessStatusText);

        display.Read = _ => 70;
        await vm.RefreshAsync();
        Assert.True(row.CanAdjustBrightness);
        Assert.Equal(70, vm.PrimaryBrightness);
    }

    [Fact]
    public async Task TrayOpen_ForcesAReReadWhenNothingIsControllable()
    {
        var display = new FakeDisplay { Read = _ => null };
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        Assert.False(vm.CanAdjustBrightness);
        int reads = display.Enumerations;

        display.Read = _ => 60;
        await vm.RefreshIfStaleAsync();
        Assert.Equal(reads, display.Enumerations);
        Assert.False(vm.CanAdjustBrightness);

        await vm.RefreshIfStaleAsync(forceWhenUnavailable: true);
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(60, vm.PrimaryBrightness);
    }

    [Fact]
    public async Task TrayOpen_DoesNotReReadWhenADisplayIsAlreadyControllable()
    {
        var display = new FakeDisplay();
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        int reads = display.Enumerations;

        await vm.RefreshIfStaleAsync(forceWhenUnavailable: true);

        Assert.Equal(reads, display.Enumerations);
    }

    [Fact]
    public async Task TrayOpen_ForcedReReadIsFlooredWhileNothingIsControllable()
    {
        var display = new FakeDisplay { Read = _ => null };
        var time = new AdjustableTime();
        using var vm = new MainWindowViewModel(
            display, new FakeStorage(), new NoOled(), new RejectDialog(), time);
        await vm.InitializationTask;
        int reads = display.Enumerations;

        await vm.RefreshIfStaleAsync(forceWhenUnavailable: true);
        Assert.Equal(reads + 1, display.Enumerations);

        await vm.RefreshIfStaleAsync(forceWhenUnavailable: true);
        Assert.Equal(reads + 1, display.Enumerations);

        time.Now += TimeSpan.FromSeconds(5);
        await vm.RefreshIfStaleAsync(forceWhenUnavailable: true);
        Assert.Equal(reads + 2, display.Enumerations);
    }

    [Fact]
    public async Task FailedWrite_KeepsKnownSupportAndAllowsAnotherAdjustmentWithoutRefresh()
    {
        var display = new FakeDisplay { Write = _ => false };
        var storage = new FakeStorage();
        using var vm = Create(display, storage);
        await vm.InitializationTask;
        Assert.False(await vm.AdjustPrimaryBrightnessAsync(2));
        Assert.Equal(50, vm.PrimaryBrightness);
        Assert.True(vm.CanAdjustBrightness);
        Assert.Contains("could not be changed", vm.Monitors.Single().BrightnessStatusText);
        Assert.Empty(storage.Settings);
        display.Write = null;
        Assert.True(await vm.AdjustPrimaryBrightnessAsync(2));
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(52, vm.PrimaryBrightness);
        Assert.Equal(52, storage.Settings["A"]);
        Assert.Empty(vm.Monitors.Single().BrightnessStatusText);
        Assert.Equal(1, display.Enumerations);
    }

    [Fact]
    public async Task TopologyChange_RefreshesWhileHiddenWithoutTheMinuteThrottle()
    {
        var display = new FakeDisplay();
        using var vm = Create(display);
        await vm.InitializationTask;
        var old = vm.Monitors.Single();
        display.DevicePath = "B";
        Task recovery = vm.HandleDisplayChangeAsync(TimeSpan.FromMilliseconds(10));
        Assert.False(vm.CanAdjustBrightness);
        Assert.False(await old.AdjustBrightnessAsync(2));
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("B", vm.Monitors.Single().Monitor.DevicePath);
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(2, display.Enumerations);
    }

    [Fact]
    public async Task TopologyChangeDuringRefresh_DiscardsOldSnapshotAndReplaysDiscovery()
    {
        var display = new FakeDisplay();
        using var vm = Create(display);
        await vm.InitializationTask;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        display.Read = _ => { entered.Set(); Wait(release); return 50; };
        Task refresh = vm.RefreshAsync();
        Task? change = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            display.DevicePath = "B";
            change = vm.HandleDisplayChangeAsync(TimeSpan.Zero);
        }
        finally { release.Set(); }
        await Task.WhenAll(refresh, change!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("B", vm.Monitors.Single().Monitor.DevicePath);
        Assert.True(vm.CanAdjustBrightness);
        Assert.Equal(3, display.Enumerations);
    }

    [Fact]
    public async Task TopologyChange_CancelsQueuedWritesToTheOldTarget()
    {
        var display = new FakeDisplay();
        using var vm = Create(display);
        await vm.InitializationTask;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        display.Read = _ => { entered.Set(); Wait(release); return 50; };
        Task refresh = vm.RefreshAsync();
        Task<bool>? write = null;
        Task? recovery = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            write = vm.AdjustPrimaryBrightnessAsync(2);
            display.DevicePath = "B";
            recovery = vm.HandleDisplayChangeAsync(TimeSpan.Zero);
        }
        finally { release.Set(); }
        await Task.WhenAll(refresh, write!, recovery!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await write!);
        Assert.Empty(display.Writes);
        Assert.Equal("B", vm.Monitors.Single().Monitor.DevicePath);
    }

    [Fact]
    public async Task BurstOfTopologyEvents_IsDebounced()
    {
        var display = new FakeDisplay();
        using var vm = Create(display);
        await vm.InitializationTask;
        var events = Enumerable.Range(0, 5).Select(_ => vm.HandleDisplayChangeAsync(TimeSpan.FromMilliseconds(30))).ToArray();
        await Task.WhenAll(events).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, display.Enumerations);
        Assert.True(vm.CanAdjustBrightness);
    }

    private static void Wait(ManualResetEventSlim gate)
    {
        if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test gate was not released");
    }
    private static MainWindowViewModel Create(FakeDisplay display, FakeStorage? storage = null) =>
        new(display, storage ?? new FakeStorage(), new NoOled(), new RejectDialog());
    private sealed class FakeDisplay : IDisplayService
    {
        public bool Connected = true;
        public string DevicePath = "A";
        public int Current = 50;
        public int Enumerations;
        public List<int> Writes = new();
        public Func<MonitorInfo, int?>? Read;
        public Func<int, bool>? Write;
        public List<MonitorInfo> GetExternalMonitors()
        {
            Interlocked.Increment(ref Enumerations);
            if (!Connected) return [];
            return [new() { DevicePath = DevicePath, FriendlyName = DevicePath }];
        }
        public int? GetBrightness(MonitorInfo monitor) => Read != null ? Read(monitor) : Current;
        public bool SetBrightness(MonitorInfo monitor, int value)
        {
            Writes.Add(value);
            if (Write != null) return Write(value);
            Current = value;
            return true;
        }
    }
    private sealed class FakeStorage : IStorageService
    {
        public Action? Save;
        public Dictionary<string, int> Settings = new();
        public Dictionary<string, int> LoadSettings() => new(Settings);
        public void SaveSettings(Dictionary<string, int> value)
        {
            Save?.Invoke();
            Settings = new(value);
        }
        public Dictionary<string, OledPanelProtectState> LoadOledPanelProtectState() => new();
        public void SaveOledPanelProtectState(Dictionary<string, OledPanelProtectState> value) { }
        public bool GetStartOnStartup() => false;
        public bool SetStartOnStartup(bool enabled) => true;
    }
    private sealed class NoOled : IOledCareService
    {
        public OledSupportLevel GetSupportLevel(MonitorInfo monitor) => OledSupportLevel.Unsupported;
        public Task<OledCareStatus> GetStatusAsync(MonitorInfo monitor, CancellationToken token = default) => throw new NotSupportedException();
        public Task<int?> GetTotalUsageHoursAsync(MonitorInfo monitor, CancellationToken token = default) => Task.FromResult<int?>(null);
        public Task<PixelRefreshResult> StartPixelRefreshAsync(MonitorInfo monitor, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class RejectDialog : IUserDialogService
    {
        public bool ConfirmPixelRefresh(MonitorInfo monitor, OledSupportLevel level) => false;
    }
    private sealed class AdjustableTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
