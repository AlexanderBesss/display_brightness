using DisplayBrightness.Models;

namespace DisplayBrightness.Services;

public interface IStorageService
{
    Dictionary<string, int> LoadSettings();
    void SaveSettings(Dictionary<string, int> settings);
    Dictionary<string, OledPanelProtectHistory> LoadOledPanelProtectHistory();
    void SaveOledPanelProtectHistory(
        Dictionary<string, OledPanelProtectHistory> history);
    bool GetStartOnStartup();
    bool SetStartOnStartup(bool enabled);
}
