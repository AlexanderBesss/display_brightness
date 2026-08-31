using System;
using System.ComponentModel;
using DisplayBrightness.Models;
using DisplayBrightness.Services;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : INotifyPropertyChanged
{
    private readonly StorageService _storageService;
    private readonly Dictionary<string, int> _savedSettings;
    private readonly Action<MonitorSliderViewModel> _onSliderReleased;

    public string DevicePath { get; }
    public string FriendlyName { get; }
    public string ModelName { get; }

    private double _brightnessValue;
    public double BrightnessValue
    {
        get => _brightnessValue;
        set
        {
            if (_brightnessValue != value)
            {
                _brightnessValue = value;
                OnPropertyChanged(nameof(BrightnessValue));
                OnPropertyChanged(nameof(BrightnessText));
            }
        }
    }

    public string BrightnessText => $"{(int)BrightnessValue}%";

    public MonitorSliderViewModel(
        MonitorInfo monitor,
        int initialBrightness,
        StorageService storageService,
        Dictionary<string, int> savedSettings,
        Action<MonitorSliderViewModel> onSliderReleased)
    {
        DevicePath = monitor.DevicePath;
        FriendlyName = monitor.FriendlyName;
        ModelName = monitor.ModelName;
        _brightnessValue = Math.Clamp(initialBrightness, 0, 100);
        _storageService = storageService;
        _savedSettings = savedSettings;
        _onSliderReleased = onSliderReleased;

    }

    private void SaveBrightness()
    {
        _savedSettings[DevicePath] = (int)BrightnessValue;
        _storageService.SaveSettings(_savedSettings);
        _onSliderReleased(this);
    }

    public void CommitBrightness()
    {
        SaveBrightness();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
