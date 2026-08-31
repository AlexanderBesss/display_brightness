using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using DisplayBrightness.Services;

namespace DisplayBrightness.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
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
            if (_startOnStartup != value)
            {
                _startOnStartup = value;
                OnPropertyChanged(nameof(StartOnStartup));
                _storageService.SetStartOnStartup(value);
            }
        }
    }

    private bool _noMonitors;
    public bool NoMonitors
    {
        get => _noMonitors;
        set
        {
            if (_noMonitors != value)
            {
                _noMonitors = value;
                OnPropertyChanged(nameof(NoMonitors));
            }
        }
    }

    public ObservableCollection<MonitorSliderViewModel> Monitors { get; } = new();

    public int? AverageBrightness => Monitors.Count == 0
        ? null
        : (int)Math.Round(
            Monitors.Average(monitor => monitor.BrightnessValue),
            MidpointRounding.AwayFromZero);

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
        LoadMonitors();
        StartOnStartup = _storageService.GetStartOnStartup();
    }

    private void LoadMonitors()
    {
        try
        {
            var monitors = _displayService.GetExternalMonitors();

            _savedSettings.Clear();
            _savedSettings = _storageService.LoadSettings();
            Monitors.Clear();

            foreach (var monitor in monitors)
            {
                var initialBrightness = _displayService.GetBrightness(monitor)
                    ?? (_savedSettings.TryGetValue(monitor.DevicePath, out var saved)
                        ? saved
                        : 50);

                var vm = new MonitorSliderViewModel(
                    monitor,
                    initialBrightness,
                    _storageService,
                    _savedSettings,
                    CreateSliderReleasedHandler(monitor));

                vm.PropertyChanged += Monitor_PropertyChanged;
                Monitors.Add(vm);
            }

            NoMonitors = Monitors.Count == 0;
            OnPropertyChanged(nameof(DisplayStatusText));
            OnPropertyChanged(nameof(AverageBrightness));
        }
        catch
        {
            Monitors.Clear();
            NoMonitors = true;
            OnPropertyChanged(nameof(DisplayStatusText));
            OnPropertyChanged(nameof(AverageBrightness));
        }
    }

    private void Monitor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorSliderViewModel.BrightnessValue))
            OnPropertyChanged(nameof(AverageBrightness));
    }

    private Action<MonitorSliderViewModel> CreateSliderReleasedHandler(Models.MonitorInfo monitor)
    {
        return vm =>
        {
            _displayService.SetBrightness(monitor, (int)vm.BrightnessValue);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
