namespace DisplayBrightness.Services;

public interface IStorageService
{
    Dictionary<string, int> LoadSettings();
    void SaveSettings(Dictionary<string, int> settings);
    bool GetStartOnStartup();
    bool SetStartOnStartup(bool enabled);
}
