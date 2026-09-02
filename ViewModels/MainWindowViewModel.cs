using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan AutomaticRefreshInterval =
        TimeSpan.FromMinutes(1);

    private readonly IDisplayService _displayService;
    private readonly IStorageService _storageService;
    private readonly IOledCareService _oledCareService;
    private readonly IUserDialogService _dialogService;
    private readonly TimeProvider _timeProvider;
    private Dictionary<string, int> _savedSettings =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, OledPanelProtectState>
        _oledPanelProtectState = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastAutomaticRefreshAtUtc;
    private bool _isRefreshRunning;

    private bool _startOnStartup;
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

    public bool NoMonitors => Monitors.Count == 0;

    public bool CanAdjustBrightness => Monitors.Count > 0;

    public ObservableCollection<MonitorSliderViewModel> Monitors { get; } = new();

    public int? AverageBrightness => Monitors.Count == 0
        ? null
        : (int)Math.Round(
            Monitors.Average(monitor => monitor.BrightnessValue),
            MidpointRounding.AwayFromZero);

    public int? PrimaryBrightness => GetPrimaryBrightnessTarget() is { } target
        ? (int)Math.Round(target.BrightnessValue, MidpointRounding.AwayFromZero)
        : null;

    public string DisplayStatusText => Monitors.Count switch
    {
        0 => "No controllable displays connected",
        1 => "1 controllable display connected",
        _ => $"{Monitors.Count} controllable displays connected"
    };

    public ICommand RefreshCommand { get; }
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

        RefreshCommand = new AsyncRelayCommand(ForceRefreshAsync);
        _startOnStartup = _storageService.GetStartOnStartup();
        LoadMonitors();
        _lastAutomaticRefreshAtUtc = _timeProvider.GetUtcNow();
    }

    internal Task RefreshIfStaleAsync()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan elapsed = now - _lastAutomaticRefreshAtUtc;
        if (_isRefreshRunning ||
            (elapsed >= TimeSpan.Zero && elapsed < AutomaticRefreshInterval))
        {
            return Task.CompletedTask;
        }

        // Record the attempt before starting it so repeated tray opens are
        // coalesced even if monitor I/O is slow or temporarily unavailable.
        _lastAutomaticRefreshAtUtc = now;
        return RefreshMonitorsAsync();
    }

    private async Task ForceRefreshAsync()
    {
        _lastAutomaticRefreshAtUtc = _timeProvider.GetUtcNow();
        await RefreshMonitorsAsync();
    }

    private async Task RefreshMonitorsAsync()
    {
        if (_isRefreshRunning)
            return;

        _isRefreshRunning = true;
        try
        {
            List<MonitorReading> readings = await Task.Run(ReadMonitors);
            ApplyMonitorReadings(readings);
        }
        catch
        {
            // Keep the last usable view. A transient monitor query should not
            // blank the popup while the next automatic retry is throttled.
        }
        finally
        {
            _isRefreshRunning = false;
        }
    }

    private void LoadMonitors()
    {
        try
        {
            ApplyMonitorReadings(ReadMonitors());
        }
        catch
        {
            ClearMonitors();
            NotifyMonitorSummaryChanged();
        }
    }

    private void ClearMonitors()
    {
        foreach (var monitor in Monitors)
        {
            monitor.PropertyChanged -= Monitor_PropertyChanged;
            monitor.Dispose();
        }

        Monitors.Clear();
    }

    private void NotifyMonitorSummaryChanged()
    {
        OnPropertyChanged(nameof(NoMonitors));
        OnPropertyChanged(nameof(CanAdjustBrightness));
        OnPropertyChanged(nameof(DisplayStatusText));
        OnPropertyChanged(nameof(AverageBrightness));
        OnPropertyChanged(nameof(PrimaryBrightness));
    }

    public bool AdjustPrimaryBrightness(int delta)
    {
        if (delta == 0 || GetPrimaryBrightnessTarget() is not { } target)
            return false;

        return target.AdjustBrightness(delta);
    }

    private MonitorSliderViewModel? GetPrimaryBrightnessTarget()
    {
        if (Monitors.Count == 0)
            return null;

        // Use the primary designation captured with the current monitor list.
        // Querying Screen.PrimaryScreen again can briefly return the previous
        // primary display after a monitor is disconnected, causing tray-wheel
        // input to target a monitor that is no longer available.
        return Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? Monitors[0];
    }

    private void Monitor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorSliderViewModel.BrightnessValue))
        {
            OnPropertyChanged(nameof(AverageBrightness));
            OnPropertyChanged(nameof(PrimaryBrightness));
        }
    }

    private bool CommitBrightness(MonitorInfo monitor, int brightness)
    {
        try
        {
            if (!_displayService.SetBrightness(monitor, brightness))
                return false;

            _savedSettings[monitor.DevicePath] = brightness;
            _storageService.SaveSettings(_savedSettings);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<MonitorReading> ReadMonitors()
    {
        var readings = new List<MonitorReading>();
        foreach (MonitorInfo monitor in _displayService.GetExternalMonitors())
        {
            int? brightness = null;
            try
            {
                brightness = _displayService.GetBrightness(monitor);
            }
            catch
            {
                // A single unavailable display must not hide the other displays.
            }

            readings.Add(new MonitorReading(monitor, brightness));
        }

        return readings;
    }

    private void ApplyMonitorReadings(List<MonitorReading> readings)
    {
        Dictionary<string, int> savedSettings = _storageService.LoadSettings();
        Dictionary<string, OledPanelProtectState> oledPanelProtectState =
            _storageService.LoadOledPanelProtectState();

        string? primaryDisplayName =
            System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        var monitorVms = new List<MonitorSliderViewModel>();

        try
        {
            foreach (MonitorReading reading in readings)
            {
                MonitorInfo monitor = reading.Monitor;
                int initialBrightness = reading.Brightness
                    ?? (savedSettings.TryGetValue(
                        monitor.DevicePath,
                        out int saved)
                            ? saved
                            : 50);

                oledPanelProtectState.TryGetValue(
                    monitor.DevicePath,
                    out OledPanelProtectState? oledState);

                var vm = new MonitorSliderViewModel(
                    monitor,
                    initialBrightness,
                    brightness => CommitBrightness(monitor, brightness),
                    _oledCareService,
                    _dialogService,
                    oledState?.History,
                    entry => SaveOledPanelProtectHistory(monitor, entry),
                    panelProtectNotification: oledState?.Notification,
                    savePanelProtectNotification: entry =>
                        SaveOledPanelProtectNotification(monitor, entry));

                vm.IsPrimary = string.Equals(
                    monitor.DisplayName,
                    primaryDisplayName,
                    StringComparison.OrdinalIgnoreCase);
                monitorVms.Add(vm);
            }
        }
        catch
        {
            foreach (MonitorSliderViewModel vm in monitorVms)
                vm.Dispose();
            throw;
        }

        _savedSettings = savedSettings;
        _oledPanelProtectState = oledPanelProtectState;
        ClearMonitors();

        foreach (MonitorSliderViewModel vm in
            monitorVms.Where(monitor => monitor.IsPrimary)
                .Concat(monitorVms.Where(monitor => !monitor.IsPrimary)))
        {
            vm.PropertyChanged += Monitor_PropertyChanged;
            Monitors.Add(vm);
        }

        NotifyMonitorSummaryChanged();
    }

    private void SaveOledPanelProtectHistory(
        MonitorInfo monitor,
        OledPanelProtectHistory history)
    {
        if (string.IsNullOrWhiteSpace(monitor.DevicePath))
            return;

        OledPanelProtectState state = _oledPanelProtectState.TryGetValue(
            monitor.DevicePath,
            out OledPanelProtectState? existing)
            ? existing
            : new OledPanelProtectState(null, null);
        _oledPanelProtectState[monitor.DevicePath] =
            state with { History = history };
        _storageService.SaveOledPanelProtectState(_oledPanelProtectState);
    }

    private void SaveOledPanelProtectNotification(
        MonitorInfo monitor,
        OledPanelProtectNotification? notification)
    {
        if (string.IsNullOrWhiteSpace(monitor.DevicePath))
            return;

        if (_oledPanelProtectState.TryGetValue(
                monitor.DevicePath,
                out OledPanelProtectState? state))
        {
            OledPanelProtectState updated =
                state with { Notification = notification };
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

        _storageService.SaveOledPanelProtectState(_oledPanelProtectState);
    }

    private sealed record MonitorReading(MonitorInfo Monitor, int? Brightness);
}
