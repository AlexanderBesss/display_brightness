using System.Text;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

/// <summary>
/// Reads the application log written next to the running test host. AppLog
/// rotates at 1 MiB, and the file accumulates across runs, so a line written
/// during a test can land in either the active or the rotated file by the time
/// the test reads it back.
/// </summary>
internal static class LogProbe
{
    public static string ReadApplicationLog()
    {
        string path = AppLog.LogFilePath;
        var text = new StringBuilder();
        if (File.Exists(path + ".old"))
            text.Append(File.ReadAllText(path + ".old")).AppendLine();
        if (File.Exists(path))
            text.Append(File.ReadAllText(path));
        return text.ToString();
    }
}
