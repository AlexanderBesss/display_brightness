using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.Utilities;
using System.Windows.Input;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : ViewModelBase
{
    private readonly MonitorInfo _monitor;
    private readonly Action<int> _commitBrightness;
    private readonly IOledCareService _oledCareService;
    private readonly IUserDialogService _dialogService;

    internal string DisplayName { get; }
    public string FriendlyName { get; }
    public string ModelName { get; }
    public bool IsPrimary { get; internal set; }

    public bool HasRefreshRate => RefreshRateText.Length > 0;
    public string RefreshRateText =>
        MonitorInfoParser.FormatRefreshRate(_monitor.RefreshRateHz);

    public bool ShowOledCare => OledSupportLevel != OledSupportLevel.Unsupported;
    public bool IsOledExperimental => OledSupportLevel == OledSupportLevel.Experimental;
    public string OledSupportText => OledSupportLevel switch
    {
        OledSupportLevel.Verified => "Verified",
        OledSupportLevel.Experimental => "Experimental",
        _ => string.Empty
    };

    public OledSupportLevel OledSupportLevel { get; }

    private bool _isOledBusy;
    public bool IsOledBusy
    {
        get => _isOledBusy;
        private set
        {
            if (SetProperty(ref _isOledBusy, value))
            {
                OnPropertyChanged(nameof(CanRunPixelRefresh));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private OledCareStatus? _oledStatus;
    public bool CanRunPixelRefresh =>
        !IsOledBusy && (_oledStatus?.CanRunPixelRefresh ?? false);

    private string _oledStatusText = "Checking OLED Panel Info…";
    public string OledStatusText
    {
        get => _oledStatusText;
        private set => SetProperty(ref _oledStatusText, value);
    }

    public ICommand RefreshOledStatusCommand { get; }
    public ICommand RunPixelRefreshCommand { get; }

    private double _brightnessValue;
    public double BrightnessValue
    {
        get => _brightnessValue;
        set
        {
            if (SetProperty(ref _brightnessValue, value))
                OnPropertyChanged(nameof(BrightnessText));
        }
    }

    public string BrightnessText => $"{(int)BrightnessValue}%";

    public MonitorSliderViewModel(
        MonitorInfo monitor,
        int initialBrightness,
        Action<int> commitBrightness,
        IOledCareService oledCareService,
        IUserDialogService dialogService)
    {
        _monitor = monitor;
        DisplayName = monitor.DisplayName;
        FriendlyName = monitor.FriendlyName;
        ModelName = monitor.ModelName;
        _brightnessValue = Math.Clamp(initialBrightness, 0, 100);
        _commitBrightness = commitBrightness;
        _oledCareService = oledCareService;
        _dialogService = dialogService;
        OledSupportLevel = _oledCareService.GetSupportLevel(monitor);
        RefreshOledStatusCommand = new AsyncRelayCommand(
            RefreshOledStatusAsync,
            () => ShowOledCare && !IsOledBusy);
        RunPixelRefreshCommand = new AsyncRelayCommand(
            RunPixelRefreshAsync,
            () => CanRunPixelRefresh);

        if (ShowOledCare)
            AsyncHelper.FireAndForget(RefreshOledStatusAsync, "OLED initial status");
    }

    public void CommitBrightness()
    {
        _commitBrightness((int)BrightnessValue);
    }

    internal bool AdjustBrightness(int delta)
    {
        int currentBrightness = (int)BrightnessValue;
        int adjustedBrightness = Math.Clamp(currentBrightness + delta, 0, 100);
        if (adjustedBrightness == currentBrightness)
            return false;

        BrightnessValue = adjustedBrightness;
        _commitBrightness(adjustedBrightness);
        return true;
    }

    private async Task RefreshOledStatusAsync()
    {
        IsOledBusy = true;
        try
        {
            OledCareStatus status = await _oledCareService.GetStatusAsync(_monitor);
            ApplyOledStatus(status);
        }
        catch (Exception ex)
        {
            OledStatusText = $"Status unavailable: {ex.Message}";
        }
        finally
        {
            IsOledBusy = false;
        }
    }

    private async Task RunPixelRefreshAsync()
    {
        if (!_dialogService.ConfirmPixelRefresh(_monitor, OledSupportLevel))
            return;

        IsOledBusy = true;
        try
        {
            PixelRefreshResult result = await _oledCareService.StartPixelRefreshAsync(_monitor);
            OledStatusText = result.Message;
        }
        catch (Exception ex)
        {
            OledStatusText = $"Pixel refresh could not be started: {ex.Message}";
        }
        finally
        {
            IsOledBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyOledStatus(OledCareStatus status)
    {
        _oledStatus = status;

        if (status.RefreshRateHz is int refreshRateHz)
        {
            _monitor.RefreshRateHz = refreshRateHz;
            OnPropertyChanged(nameof(RefreshRateText));
            OnPropertyChanged(nameof(HasRefreshRate));
        }
        if (status.PanelInfo is OledPanelInfo panelInfo &&
            panelInfo.PanelProtect.HasValue)
        {
            OledStatusText = panelInfo.TotalUsageHours is int hours
                ? $"{hours:N0} total panel hours"
                : "Panel protect";
        }
        else
        {
            OledStatusText = status.Message;
        }

        OnPropertyChanged(nameof(CanRunPixelRefresh));
        CommandManager.InvalidateRequerySuggested();
    }
}
