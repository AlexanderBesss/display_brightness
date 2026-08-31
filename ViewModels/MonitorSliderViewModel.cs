using DisplayBrightness.Models;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : ViewModelBase
{
    private readonly Action<int> _commitBrightness;

    public string FriendlyName { get; }
    public string ModelName { get; }

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
        Action<int> commitBrightness)
    {
        FriendlyName = monitor.FriendlyName;
        ModelName = monitor.ModelName;
        _brightnessValue = Math.Clamp(initialBrightness, 0, 100);
        _commitBrightness = commitBrightness;
    }

    public void CommitBrightness()
    {
        _commitBrightness((int)BrightnessValue);
    }
}
