using DisplayBrightness.Models;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : ViewModelBase
{
    private readonly Action<int> _commitBrightness;

    internal string DisplayName { get; }
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
        DisplayName = monitor.DisplayName;
        FriendlyName = monitor.FriendlyName;
        ModelName = monitor.ModelName;
        _brightnessValue = Math.Clamp(initialBrightness, 0, 100);
        _commitBrightness = commitBrightness;
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
}
