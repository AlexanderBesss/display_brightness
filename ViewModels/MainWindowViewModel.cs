using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IDisplayService _displayService;
    private readonly IStorageService _storageService;
    private readonly IOledCareService _oledCareService;
    private readonly IUserDialogService _dialogService;
    private Dictionary<string, int> _savedSettings =
        new(StringComparer.OrdinalIgnoreCase);

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
        IUserDialogService? dialogService = null)
    {
        _displayService = displayService ?? new DisplayService();
        _storageService = storageService ?? new StorageService();
        _oledCareService = oledCareService ?? new OledCareService();
        _dialogService = dialogService ?? new UserDialogService();

        RefreshCommand = new RelayCommand(_ => LoadMonitors());
        _startOnStartup = _storageService.GetStartOnStartup();
        LoadMonitors();
    }

    private void LoadMonitors()
    {
        try
        {
            var monitors = _displayService.GetExternalMonitors();

            _savedSettings = _storageService.LoadSettings();
            ClearMonitors();

            string? primaryDisplayName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;

            var monitorVms = new List<MonitorSliderViewModel>();

            foreach (var monitor in monitors)
            {
                int? currentBrightness = null;
                try
                {
                    currentBrightness = _displayService.GetBrightness(monitor);
                }
                catch
                {
                    // A single unavailable display must not hide the other displays.
                }

                var initialBrightness = currentBrightness
                    ?? (_savedSettings.TryGetValue(monitor.DevicePath, out var saved)
                        ? saved
                        : 50);

                var vm = new MonitorSliderViewModel(
                    monitor,
                    initialBrightness,
                    brightness => CommitBrightness(monitor, brightness),
                    _oledCareService,
                    _dialogService);

                vm.IsPrimary = string.Equals(
                    monitor.DisplayName,
                    primaryDisplayName,
                    StringComparison.OrdinalIgnoreCase);

                monitorVms.Add(vm);
            }

            foreach (var vm in monitorVms.Where(monitor => monitor.IsPrimary)
                .Concat(monitorVms.Where(monitor => !monitor.IsPrimary)))
            {
                vm.PropertyChanged += Monitor_PropertyChanged;
                Monitors.Add(vm);
            }

            NotifyMonitorSummaryChanged();
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

        string? primaryDisplayName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        MonitorSliderViewModel? target = Monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.DisplayName,
                primaryDisplayName,
                StringComparison.OrdinalIgnoreCase));

        return target ?? Monitors[0];
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
}
