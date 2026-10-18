using System.Text;
using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

/// <summary>
/// Reads the application log beside the running test host, active and rotated files both: AppLog
/// rotates at 1 MiB and accumulates across runs, so a line written by a test can land in either.
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
