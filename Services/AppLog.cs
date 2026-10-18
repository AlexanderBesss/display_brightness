using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DisplayBrightness.Services;

/// <summary>
/// Application events to <c>brightness.log</c> next to the exe (not <c>%APPDATA%</c>),
/// rotated to <c>brightness.log.old</c> past <see cref="MaxLogBytes"/>. One event per
/// line, so <c>Select-String</c> never shows an orphaned stack frame. Written with
/// <see cref="FileShare.ReadWrite"/> so a second running copy cannot silence it, and
/// every write is guarded: logging never throws.
/// </summary>
public static class AppLog
{
    internal const string LogFileName = "brightness.log";
    internal const long MaxLogBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex NewLines = new(@"\s*\r?\n\s*", RegexOptions.Compiled);

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
            Debug.WriteLine($"AppLog: {ex.Message}");
        }
    }

    internal static string FormatLine(string level, string message) =>
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-7} {Flatten(message)}";

    /// <summary>Newlines flattened: multi-line stack traces and WMI messages otherwise detach from their header.</summary>
    internal static string Flatten(string text) => NewLines.Replace(text, " ");

    /// <summary>Appends one line, rotating first past the size limit. Never throws: an unwritable log location is ignored.</summary>
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
            // Rotation failure only leaves the file oversized: a second running copy may hold it open.
            Debug.WriteLine($"AppLog: could not rotate {path}: {ex.Message}");
        }
    }
}
