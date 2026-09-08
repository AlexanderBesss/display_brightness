using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IStorageService
{
    Dictionary<string, int> LoadSettings();
    void SaveSettings(Dictionary<string, int> settings);
    Dictionary<string, OledPanelProtectState> LoadOledPanelProtectState();
    void SaveOledPanelProtectState(
        Dictionary<string, OledPanelProtectState> state);
    bool GetStartOnStartup();
    bool SetStartOnStartup(bool enabled);
}
