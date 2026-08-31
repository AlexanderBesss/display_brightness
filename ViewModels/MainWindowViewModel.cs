using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly DisplayService _displayService;
    private readonly StorageService _storageService;
    private Dictionary<string, int> _savedSettings = new();

    private bool _startOnStartup;
    public bool StartOnStartup
    {
        get => _startOnStartup;
        set
        {
            if (SetProperty(ref _startOnStartup, value))
                _storageService.SetStartOnStartup(value);
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
    public MainWindowViewModel(DisplayService? displayService = null, StorageService? storageService = null)
    {
        _displayService = displayService ?? new DisplayService();
        _storageService = storageService ?? new StorageService();

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

            foreach (var monitor in monitors)
            {
                var initialBrightness = _displayService.GetBrightness(monitor)
                    ?? (_savedSettings.TryGetValue(monitor.DevicePath, out var saved)
                        ? saved
                        : 50);

                var vm = new MonitorSliderViewModel(
                    monitor,
                    initialBrightness,
                    brightness => CommitBrightness(monitor, brightness));

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
            monitor.PropertyChanged -= Monitor_PropertyChanged;

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

    private void CommitBrightness(MonitorInfo monitor, int brightness)
    {
        _savedSettings[monitor.DevicePath] = brightness;
        _storageService.SaveSettings(_savedSettings);
        _displayService.SetBrightness(monitor, brightness);
    }
}
