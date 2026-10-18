using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ForcedRefreshFloor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BrightnessReadRetryDelay = TimeSpan.FromMilliseconds(150);
    private readonly IDisplayService _displayService;
    private readonly IStorageService _storageService;
    private readonly IOledCareService _oledCareService;
    private readonly IUserDialogService _dialogService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _displayIo = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Dictionary<string, int> _savedSettings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, OledPanelProtectState> _oledPanelProtectState = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastAutomaticRefreshAtUtc;
    private DateTimeOffset _lastForcedRefreshAtUtc = DateTimeOffset.MinValue;
    private Task _refreshTask = Task.CompletedTask;
    private Task _persistenceTask = Task.CompletedTask;
    private bool _isRefreshRunning;
    private bool _refreshAgain;
    private bool _isDisposed;
    private int _topologyVersion;
    private CancellationTokenSource? _topologyDebounce;
    private bool _startOnStartup;
    private bool _isLoading = true;
    private bool _isStartupReady;

    public bool IsStartupReady
    {
        get => _isStartupReady;
        private set => SetProperty(ref _isStartupReady, value);
    }

    public bool StartOnStartup
    {
        get => _startOnStartup;
        set
        {
            if (_startOnStartup == value)
                return;
            if (_storageService.SetStartOnStartup(value))
                SetProperty(ref _startOnStartup, value);
            else
                OnPropertyChanged();
        }
    }

    public bool NoMonitors => !_isLoading && Monitors.Count == 0;
    public bool CanAdjustBrightness => GetPrimaryBrightnessTarget() != null;
    public ObservableCollection<MonitorSliderViewModel> Monitors { get; } = new();
    public int? AverageBrightness => Monitors.Any(m => m.CanAdjustBrightness)
        ? (int)Math.Round(Monitors.Where(m => m.CanAdjustBrightness).Average(m => m.BrightnessValue), MidpointRounding.AwayFromZero)
        : null;
    public int? PrimaryBrightness => GetPrimaryBrightnessTarget() is { } target
        ? (int)Math.Round(target.BrightnessValue, MidpointRounding.AwayFromZero) : null;
    public string DisplayStatusText => _isLoading ? "Checking displays…" :
        Monitors.Count == 0 ? "No displays connected" :
        $"{Monitors.Count(m => m.CanAdjustBrightness)} of {Monitors.Count} displays support brightness control";
    public ICommand RefreshCommand { get; }
    public Task InitializationTask { get; }
    internal Task PersistenceTask => _persistenceTask;

    public MainWindowViewModel(
        IDisplayService? displayService = null,
        IStorageService? storageService = null,
        IOledCareService? oledCareService = null,
        IUserDialogService? dialogService = null,
        TimeProvider? timeProvider = null)
    {
        _displayService = displayService ?? new DisplayService();
        _storageService = storageService ?? new StorageService();
        _oledCareService = oledCareService ?? new OledCareService();
        _dialogService = dialogService ?? new UserDialogService();
        _timeProvider = timeProvider ?? TimeProvider.System;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        InitializationTask = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var stored = await Task.Run(() => (
                Settings: _storageService.LoadSettings(),
                OledState: _storageService.LoadOledPanelProtectState(),
                Startup: _storageService.GetStartOnStartup()));
            if (_isDisposed)
                return;
            _savedSettings = stored.Settings;
            _oledPanelProtectState = stored.OledState;
            _startOnStartup = stored.Startup;
            OnPropertyChanged(nameof(StartOnStartup));
        }
        catch (Exception ex)
        {
            // A storage failure must not prevent monitor discovery.
            AppLog.Error("settings load failed; continuing with defaults", ex);
        }
        if (!_isDisposed)
        {
            IsStartupReady = true;
            await RequestRefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        await InitializationTask;
        if (!_isDisposed)
            await RequestRefreshAsync();
    }

    internal async Task RefreshIfStaleAsync(bool forceWhenUnavailable = false)
    {
        await InitializationTask;
        if (_isDisposed || _isRefreshRunning)
            return;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan elapsed = now - _lastAutomaticRefreshAtUtc;
        bool isStale = elapsed < TimeSpan.Zero || elapsed >= AutomaticRefreshInterval;

        // Opening the popup is an explicit request for control, so a display that
        // currently looks unusable is re-read even inside the throttle window. The
        // floor keeps that from turning every click into a full DDC/CI sweep when a
        // monitor is permanently unreadable.
        bool retryUnavailable = forceWhenUnavailable && !CanAdjustBrightness &&
            now - _lastForcedRefreshAtUtc >= ForcedRefreshFloor;
        if (isStale || retryUnavailable)
        {
            if (retryUnavailable)
                _lastForcedRefreshAtUtc = now;
            await RequestRefreshAsync();
        }
    }

    internal async Task HandleDisplayChangeAsync(TimeSpan? debounce = null)
    {
        if (_isDisposed)
            return;
        AppLog.Info("display configuration change");
        _topologyVersion++;
        SuspendAllMonitorRows();
        NotifyMonitorSummaryChanged();
        _topologyDebounce?.Cancel();
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _topologyDebounce = delay;
        try
        {
            await Task.Delay(debounce ?? TimeSpan.FromMilliseconds(500), delay.Token);
            await InitializationTask;
            if (!delay.IsCancellationRequested && !_isDisposed)
                await RequestRefreshAsync();
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_topologyDebounce, delay))
                _topologyDebounce = null;
        }
    }

    private Task RequestRefreshAsync()
    {
        _refreshAgain = true;
        if (_isRefreshRunning)
            return _refreshTask;
        _isRefreshRunning = true;
        return _refreshTask = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (_refreshAgain && !_isDisposed)
            {
                _refreshAgain = false;
                int topology = _topologyVersion;
                var revisions = Monitors.ToDictionary(m => m.Monitor.DevicePath,
                    m => m.BrightnessRevision, StringComparer.OrdinalIgnoreCase);
                _lastAutomaticRefreshAtUtc = _timeProvider.GetUtcNow();
                try
                {
                    List<MonitorReading> readings = await ReadMonitorsAsync();
                    if (_isDisposed)
                        return;
                    if (topology != _topologyVersion)
                    {
                        _refreshAgain = true;
                        continue;
                    }
                    ApplyMonitorReadings(readings, revisions);
                }
                catch (OperationCanceledException) when (_isDisposed) { }
                catch (Exception ex)
                {
                    // Keep rows but stop using potentially stale targets. The
                    // background refresh timer retries even with the popup hidden.
                    AppLog.Error("monitor refresh failed; rows suspended until next cycle", ex);
                    _topologyVersion++;
                    SuspendAllMonitorRows();
                }
                _isLoading = false;
                NotifyMonitorSummaryChanged();
            }
        }
        finally { _isRefreshRunning = false; }
    }

    private void SuspendAllMonitorRows()
    {
        foreach (MonitorSliderViewModel monitor in Monitors)
            monitor.SuspendForTopologyChange();
    }

    private async Task<List<MonitorReading>> ReadMonitorsAsync()
    {
        // Snapshot on the calling (UI) thread: the read loop below runs on a worker
        // thread and must not walk a collection the UI thread mutates.
        // Demonstrated support, not current health, decides the retry: a row that
        // slipped into Unavailable on a wake-up timeout is exactly the case that
        // needs the retry, while a display that has never answered still pays none.
        HashSet<string> retryOnFailure = Monitors
            .Where(monitor => monitor.HasDemonstratedBrightness)
            .Select(monitor => monitor.Monitor.DevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A freshly (re)created row has no reading history, but a stored brightness
        // is evidence this physical display answered DDC/CI in an earlier session —
        // typically a reconnect after the link retrained and changed the device path.
        foreach (string devicePath in _savedSettings.Keys)
            retryOnFailure.Add(devicePath);

        await _displayIo.WaitAsync(_lifetime.Token);
        try
        {
            return await Task.Run(async () =>
            {
                var readings = new List<MonitorReading>();
                foreach (MonitorInfo monitor in _displayService.GetExternalMonitors())
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    int? brightness = await ReadBrightnessWithRetryAsync(
                        monitor,
                        retryOnFailure);
                    readings.Add(new MonitorReading(monitor, brightness));
                }
                return readings;
            }, _lifetime.Token);
        }
        finally { _displayIo.Release(); }
    }

    private async Task<int?> ReadBrightnessWithRetryAsync(
        MonitorInfo monitor,
        HashSet<string> retryOnFailure)
    {
        int? brightness = TryReadBrightness(monitor);
        if (brightness.HasValue || !retryOnFailure.Contains(monitor.DevicePath))
            return brightness;

        // A DDC/CI read can time out on a panel that is waking or retraining its
        // link. One paced retry keeps a transient timeout from taking control away
        // from a display that was answering a moment ago — and from one that lost
        // control to exactly such a timeout. A display that has never answered is
        // not retried, so it does not pay the delay every cycle.
        await Task.Delay(BrightnessReadRetryDelay, _lifetime.Token);
        return TryReadBrightness(monitor);
    }

    private int? TryReadBrightness(MonitorInfo monitor)
    {
        try { return _displayService.GetBrightness(monitor); }
        catch { return null; }
    }

    public Task<bool> AdjustPrimaryBrightnessAsync(int delta) =>
        delta != 0 && GetPrimaryBrightnessTarget() is { } target
            ? target.AdjustBrightnessAsync(delta) : Task.FromResult(false);

    private MonitorSliderViewModel? GetPrimaryBrightnessTarget() =>
        Monitors.FirstOrDefault(m => m.IsPrimary && m.CanAdjustBrightness)
            ?? Monitors.FirstOrDefault(m => m.CanAdjustBrightness);

    private void Monitor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorSliderViewModel.BrightnessValue) or
            nameof(MonitorSliderViewModel.CanAdjustBrightness))
            NotifyMonitorSummaryChanged();
    }

    private async Task<bool> CommitBrightnessAsync(MonitorInfo monitor, int brightness)
    {
        int topology = _topologyVersion;
        MonitorInfo target = monitor.Snapshot();
        try
        {
            if (!await WriteBrightnessAsync(target, brightness, topology))
                return false;

            _savedSettings[target.DevicePath] = brightness;
            var snapshot = new Dictionary<string, int>(_savedSettings, StringComparer.OrdinalIgnoreCase);
            await QueuePersistence(() => _storageService.SaveSettings(snapshot));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                $"brightness commit threw ({target.FriendlyName} [{target.DevicePath}] -> {brightness}%)", ex);
            return false;
        }
    }

    private async Task<bool> WriteBrightnessAsync(
        MonitorInfo target,
        int brightness,
        int topology)
    {
        await _displayIo.WaitAsync(_lifetime.Token);
        try
        {
            return await Task.Run(() =>
            {
                if (_isDisposed || topology != Volatile.Read(ref _topologyVersion))
                    return false;
                bool applied = _displayService.SetBrightness(target, brightness);
                if (!applied)
                    AppLog.Warn(
                        $"brightness write rejected by display " +
                        $"({target.FriendlyName} [{target.DevicePath}] -> {brightness}%)");
                return applied;
            });
        }
        finally { _displayIo.Release(); }
    }

    private void ApplyMonitorReadings(List<MonitorReading> readings, Dictionary<string, long> revisions)
    {
        string? primaryName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        var existing = Monitors.ToDictionary(m => m.Monitor.DevicePath, StringComparer.OrdinalIgnoreCase);
        var updated = new List<MonitorSliderViewModel>();
        foreach (MonitorReading reading in readings.DistinctBy(r => r.Monitor.DevicePath, StringComparer.OrdinalIgnoreCase))
        {
            MonitorInfo monitor = reading.Monitor;
            if (existing.Remove(monitor.DevicePath, out var vm))
            {
                vm.ApplyReading(monitor, reading.Brightness, revisions.GetValueOrDefault(monitor.DevicePath, -1));
            }
            else
            {
                vm = CreateMonitorRow(monitor, reading.Brightness);
                vm.PropertyChanged += Monitor_PropertyChanged;
                AppLog.Info(
                    $"display connected: {monitor.FriendlyName} ({monitor.ModelName}) " +
                    $"[{monitor.DevicePath}]" +
                    (reading.Brightness is int brightnessValue
                        ? $" brightness={brightnessValue}%"
                        : " brightness=unreadable"));
            }
            vm.IsPrimary = string.Equals(monitor.DisplayName, primaryName, StringComparison.OrdinalIgnoreCase);
            updated.Add(vm);
        }
        RetireMonitorRows(existing.Values);
        OrderRowsByPrimary(updated);
    }

    private MonitorSliderViewModel CreateMonitorRow(MonitorInfo monitor, int? brightness)
    {
        _oledPanelProtectState.TryGetValue(monitor.DevicePath, out OledPanelProtectState? oledState);
        return new MonitorSliderViewModel(
            monitor,
            brightness,
            value => CommitBrightnessAsync(monitor, value),
            _oledCareService,
            _dialogService,
            oledState?.History,
            history => SaveOledPanelProtectHistory(monitor, history),
            _timeProvider,
            oledState?.Notification,
            notification => SaveOledPanelProtectNotification(monitor, notification));
    }

    private void RetireMonitorRows(IEnumerable<MonitorSliderViewModel> retired)
    {
        foreach (MonitorSliderViewModel monitor in retired)
        {
            AppLog.Info($"display retired: {monitor.Monitor.FriendlyName} [{monitor.Monitor.DevicePath}]");
            monitor.PropertyChanged -= Monitor_PropertyChanged;
            monitor.Dispose();
            Monitors.Remove(monitor);
        }
    }

    private void OrderRowsByPrimary(List<MonitorSliderViewModel> rows)
    {
        List<MonitorSliderViewModel> ordered =
            rows.OrderByDescending(m => m.IsPrimary).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int current = Monitors.IndexOf(ordered[i]);
            if (current < 0) Monitors.Insert(i, ordered[i]);
            else if (current != i) Monitors.Move(current, i);
        }
    }

    private void SaveOledPanelProtectHistory(MonitorInfo monitor, OledPanelProtectHistory history)
    {
        if (string.IsNullOrWhiteSpace(monitor.DevicePath)) return;
        OledPanelProtectState state = GetOrCreateOledState(monitor.DevicePath);
        _oledPanelProtectState[monitor.DevicePath] = state with { History = history };
        QueueOledStatePersistence();
    }

    private void SaveOledPanelProtectNotification(MonitorInfo monitor, OledPanelProtectNotification? notification)
    {
        if (string.IsNullOrWhiteSpace(monitor.DevicePath)) return;
        if (_oledPanelProtectState.TryGetValue(monitor.DevicePath, out var state))
        {
            OledPanelProtectState updated = state with { Notification = notification };
            if (updated.History == null && updated.Notification == null)
                _oledPanelProtectState.Remove(monitor.DevicePath);
            else
                _oledPanelProtectState[monitor.DevicePath] = updated;
        }
        else if (notification != null)
        {
            _oledPanelProtectState[monitor.DevicePath] =
                new OledPanelProtectState(null, notification);
        }
        QueueOledStatePersistence();
    }

    private OledPanelProtectState GetOrCreateOledState(string devicePath) =>
        _oledPanelProtectState.TryGetValue(devicePath, out OledPanelProtectState? existing)
            ? existing
            : new OledPanelProtectState(null, null);

    private void QueueOledStatePersistence()
    {
        var snapshot = new Dictionary<string, OledPanelProtectState>(
            _oledPanelProtectState,
            StringComparer.OrdinalIgnoreCase);
        _ = QueuePersistence(() => _storageService.SaveOledPanelProtectState(snapshot));
    }

    private Task QueuePersistence(Action save) => _persistenceTask = PersistAfterAsync(_persistenceTask, save);

    private static async Task PersistAfterAsync(Task previous, Action save)
    {
        await previous;
        try { await Task.Run(save); }
        catch (Exception ex)
        {
            // Optional persistence must not change a hardware write result.
            AppLog.Error("state persistence failed", ex);
        }
    }

    private void NotifyMonitorSummaryChanged()
    {
        OnPropertyChanged(nameof(NoMonitors));
        OnPropertyChanged(nameof(CanAdjustBrightness));
        OnPropertyChanged(nameof(DisplayStatusText));
        OnPropertyChanged(nameof(AverageBrightness));
        OnPropertyChanged(nameof(PrimaryBrightness));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lifetime.Cancel();
        _topologyDebounce?.Cancel();
        foreach (var monitor in Monitors)
        {
            monitor.PropertyChanged -= Monitor_PropertyChanged;
            monitor.Dispose();
        }
        // In-flight native calls are not cancellable. Their continuations still
        // need the semaphore and cancellation source after the view is closed.
        GC.SuppressFinalize(this);
    }

    private sealed record MonitorReading(MonitorInfo Monitor, int? Brightness);
}
