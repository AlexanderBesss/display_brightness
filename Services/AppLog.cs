using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DisplayBrightness.Services;

/// <summary>
/// Collects application events and errors into <c>brightness.log</c>, written next
/// to the executable (same convention as <c>oled-care-state.json</c>). When the
/// file grows past <see cref="MaxLogBytes"/> it is rotated to
/// <c>brightness.log.old</c> and a fresh file is started.
/// One event is one line: newlines inside messages and stack traces are flattened,
/// so <c>Select-String</c> never shows an orphaned stack frame.
/// Files are opened with <see cref="FileShare.ReadWrite"/> so a second running copy
/// of the app, or a log viewer that permits writes, cannot silence the log. A
/// viewer that denies write sharing still blocks writes — nothing on this side can
/// change that — so the failure is reported to the debugger rather than lost.
/// Diagnostics must never interfere with operation: every write is guarded and
/// failures are swallowed.
/// </summary>
public static class AppLog
{
    internal const string LogFileName = "brightness.log";
    internal const long MaxLogBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex NewLines = new(@"\s*\r?\n\s*", RegexOptions.Compiled);

    /// <summary>Absolute path of the active log file, next to the running exe.</summary>
    public static string LogFilePath =>
        Path.Combine(AppContext.BaseDirectory, LogFileName);

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Warn(string message, Exception exception) =>
        Write("WARN", FormatException(message, exception));

    public static void Error(string message, Exception? exception = null)
    {
        if (exception == null)
        {
            Write("ERROR", message);
            return;
        }

        Write("ERROR", FormatException(message, exception));
    }

    internal static string FormatException(string message, Exception exception)
    {
        string text = $"{message} | {exception.GetType().Name}: {exception.Message}";
        if (exception.InnerException != null)
            text += $" <- {exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
        if (exception.StackTrace != null)
            text += $" [{exception.StackTrace}]";
        return text;
    }

    /// <summary>Session banner line, used at startup and shutdown.</summary>
    public static void Session(string message) => Write("SESSION", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Append(LogFilePath, FormatLine(level, message));
            }
        }
        catch (Exception ex)
        {
            // Logging must never take down or block the application.
            Debug.WriteLine($"AppLog: {ex.Message}");
        }
    }

    internal static string FormatLine(string level, string message) =>
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-7} {Flatten(message)}";

    /// <summary>
    /// Keeps an event on a single line. Multi-line stack traces and WMI messages
    /// otherwise detach from their header under <c>Select-String</c>.
    /// </summary>
    internal static string Flatten(string text) => NewLines.Replace(text, " ");

    /// <summary>
    /// Appends one line, rotating first when the file is over the size limit.
    /// Never throws: an unwritable or unavailable log location is ignored.
    /// </summary>
    internal static void Append(string path, string line)
    {
        try
        {
            RotateIfNeeded(path);
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(line);
            writer.Write(Environment.NewLine);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppLog: could not write {path}: {ex.Message}");
        }
    }

    internal static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= MaxLogBytes)
                return;

            string rotated = path + ".old";
            if (File.Exists(rotated))
                File.Replace(path, rotated, null);
            else
                File.Move(path, rotated);
        }
        catch (Exception ex)
        {
            // If rotation fails, keep appending to the oversized file. Another
            // running copy can hold the file; that is expected and harmless.
            Debug.WriteLine($"AppLog: could not rotate {path}: {ex.Message}");
        }
    }
}
