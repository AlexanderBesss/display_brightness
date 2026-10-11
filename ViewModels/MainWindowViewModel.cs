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
        catch
        {
            // A storage failure must not prevent monitor discovery.
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
        _topologyVersion++;
        foreach (var monitor in Monitors)
            monitor.SuspendForTopologyChange();
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
                catch
                {
                    // Keep rows but stop using potentially stale targets. The
                    // background refresh timer retries even with the popup hidden.
                    _topologyVersion++;
                    foreach (var monitor in Monitors)
                        monitor.SuspendForTopologyChange();
                }
                _isLoading = false;
                NotifyMonitorSummaryChanged();
            }
        }
        finally { _isRefreshRunning = false; }
    }

    private async Task<List<MonitorReading>> ReadMonitorsAsync()
    {
        // Snapshot on the calling (UI) thread: the read loop below runs on a worker
        // thread and must not walk a collection the UI thread mutates.
        HashSet<string> retryOnFailure = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorSliderViewModel monitor in Monitors)
        {
            if (monitor.CanAdjustBrightness)
                retryOnFailure.Add(monitor.Monitor.DevicePath);
        }

        await _displayIo.WaitAsync(_lifetime.Token);
        try
        {
            return await Task.Run(async () =>
            {
                var readings = new List<MonitorReading>();
                foreach (MonitorInfo monitor in _displayService.GetExternalMonitors())
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    int? brightness = null;
                    try { brightness = _displayService.GetBrightness(monitor); }
                    catch { }
                    if (!brightness.HasValue && retryOnFailure.Contains(monitor.DevicePath))
                    {
                        // A DDC/CI read can time out on a panel that is waking or
                        // retraining its link. One paced retry keeps a transient
                        // timeout from taking control away from a display that was
                        // answering a moment ago. A display that has never answered
                        // is not retried, so it does not pay the delay every cycle.
                        await Task.Delay(BrightnessReadRetryDelay, _lifetime.Token);
                        try { brightness = _displayService.GetBrightness(monitor); }
                        catch { }
                    }
                    readings.Add(new MonitorReading(monitor, brightness));
                }
                return readings;
            }, _lifetime.Token);
        }
        finally { _displayIo.Release(); }
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
            await _displayIo.WaitAsync(_lifetime.Token);
            try
            {
                bool written = await Task.Run(() =>
                    !_isDisposed && topology == Volatile.Read(ref _topologyVersion) &&
                    _displayService.SetBrightness(target, brightness));
                if (!written)
                    return false;
            }
            finally { _displayIo.Release(); }
            _savedSettings[target.DevicePath] = brightness;
            var snapshot = new Dictionary<string, int>(_savedSettings, StringComparer.OrdinalIgnoreCase);
            await QueuePersistence(() => _storageService.SaveSettings(snapshot));
            return true;
        }
        catch { return false; }
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
                _oledPanelProtectState.TryGetValue(monitor.DevicePath, out var oledState);
                OledPanelProtectHistory? history = oledState?.History;
                OledPanelProtectNotification? notification = oledState?.Notification;
                vm = new MonitorSliderViewModel(monitor, reading.Brightness,
                    value => CommitBrightnessAsync(monitor, value), _oledCareService, _dialogService,
                    history, entry => SaveOledPanelProtectHistory(monitor, entry), _timeProvider,
                    notification, entry => SaveOledPanelProtectNotification(monitor, entry));
                vm.PropertyChanged += Monitor_PropertyChanged;
            }
            vm.IsPrimary = string.Equals(monitor.DisplayName, primaryName, StringComparison.OrdinalIgnoreCase);
            updated.Add(vm);
        }
        foreach (var removed in existing.Values)
        {
            removed.PropertyChanged -= Monitor_PropertyChanged;
            removed.Dispose();
            Monitors.Remove(removed);
        }
        var ordered = updated.OrderByDescending(m => m.IsPrimary).ToList();
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
        OledPanelProtectState state = _oledPanelProtectState.TryGetValue(
            monitor.DevicePath,
            out var existing)
            ? existing
            : new OledPanelProtectState(null, null);
        _oledPanelProtectState[monitor.DevicePath] = state with { History = history };
        var snapshot = new Dictionary<string, OledPanelProtectState>(
            _oledPanelProtectState, StringComparer.OrdinalIgnoreCase);
        _ = QueuePersistence(() => _storageService.SaveOledPanelProtectState(snapshot));
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
        var snapshot = new Dictionary<string, OledPanelProtectState>(
            _oledPanelProtectState, StringComparer.OrdinalIgnoreCase);
        _ = QueuePersistence(() => _storageService.SaveOledPanelProtectState(snapshot));
    }

    private Task QueuePersistence(Action save) => _persistenceTask = PersistAfterAsync(_persistenceTask, save);

    private static async Task PersistAfterAsync(Task previous, Action save)
    {
        await previous;
        try { await Task.Run(save); }
        catch { /* Optional persistence must not change a hardware write result. */ }
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
