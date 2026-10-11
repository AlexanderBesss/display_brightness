using DisplayBrightness.Services;

namespace DisplayBrightness.Tests;

[Collection("ApplicationLog")]
public sealed class AppLogTests
{
    [Fact]
    public void LogFilePath_FollowsTheNextToTheExecutableConvention()
    {
        // Pins the documented convention: the log sits beside the exe, like
        // oled-care-state.json, not in %APPDATA% beside settings.json.
        string path = AppLog.LogFilePath;

        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("brightness.log", Path.GetFileName(path));
        Assert.Equal(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
            Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd('\\'));
    }

    [Fact]
    public void FormatLine_PrefixesTimestampAndLevel()
    {
        string line = AppLog.FormatLine("INFO", "hello world");

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO\s+hello world$", line);
    }

    [Fact]
    public void FormatLine_FlattensNewlinesSoOneEventIsOneLine()
    {
        string line = AppLog.FormatLine("ERROR", "first\r\n   second\n   third");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.EndsWith("ERROR   first second third", line);
    }

    [Fact]
    public void FormatException_ChainsInnerExceptionAndStaysOnOneLine()
    {
        var inner = new IOException("device busy");
        string formatted;
        try
        {
            try
            {
                throw inner;
            }
            catch (IOException)
            {
                throw new InvalidOperationException("read failed", inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            formatted = AppLog.FormatException("outer boom", outer);
        }

        Assert.Contains("outer boom | InvalidOperationException: read failed", formatted);
        Assert.Contains("<- IOException: device busy", formatted);
        Assert.Contains("at DisplayBrightness.Tests.AppLogTests", formatted);
        Assert.DoesNotContain('\n', formatted);
    }

    [Fact]
    public void Append_WritesLineToNewFile()
    {
        string path = NewLogPath();
        try
        {
            AppLog.Append(path, "hello world");

            string content = File.ReadAllText(path);
            Assert.Contains("hello world", content);
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Append_WritesWhileAnotherWriterHoldsTheFile()
    {
        string path = NewLogPath();
        try
        {
            // A second running copy of the app can hold the log open for writing.
            // ReadWrite sharing keeps both copies logging instead of letting the
            // swallowed IOException silence one of them.
            using var otherWriter = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite);

            AppLog.Append(path, "written beside a second writer");

            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var text = new StreamReader(reader);
            Assert.Contains("written beside a second writer", text.ReadToEnd());
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Append_RotatesWhenOverSizeLimit()
    {
        string path = NewLogPath();
        try
        {
            File.WriteAllText(path, new string('x', (int)AppLog.MaxLogBytes + 10));

            AppLog.Append(path, "fresh line");

            Assert.True(File.Exists(path + ".old"));
            string content = File.ReadAllText(path);
            Assert.Contains("fresh line", content);
            Assert.DoesNotContain("xxxx", content);
            Assert.True(new FileInfo(path).Length < AppLog.MaxLogBytes);
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Rotate_ReplacesExistingRotatedFile()
    {
        string path = NewLogPath();
        try
        {
            File.WriteAllText(path, new string('a', (int)AppLog.MaxLogBytes + 10));
            File.WriteAllText(path + ".old", "previous rotation");

            AppLog.RotateIfNeeded(path);

            Assert.Equal(new string('a', (int)AppLog.MaxLogBytes + 10),
                File.ReadAllText(path + ".old"));
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Rotate_KeepsFileWhenUnderSizeLimit()
    {
        string path = NewLogPath();
        try
        {
            File.WriteAllText(path, "small");

            AppLog.RotateIfNeeded(path);

            Assert.Equal("small", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".old"));
        }
        finally
        {
            DeleteDirectory(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void Append_NeverThrowsWhenLogLocationIsUnusable()
    {
        string missingDirectory = Path.Combine(
            Path.GetTempPath(), $"BrightnessLogTests-missing-{Guid.NewGuid():N}");

        // Must not throw even though the directory does not exist.
        AppLog.Append(Path.Combine(missingDirectory, "brightness.log"), "line");
    }

    [Fact]
    public void Error_FormatsExceptionTypeAndMessage()
    {
        // AppLog writes to the fixed path next to the running executable, so this
        // exercises the public entry point and asserts on that file.
        string marker = Guid.NewGuid().ToString("N");

        AppLog.Error($"boom {marker}", new InvalidOperationException("inner boom"));

        string content = LogProbe.ReadApplicationLog();
        Assert.Contains($"ERROR   boom {marker} | InvalidOperationException: inner boom", content);
    }

    [Fact]
    public void Warn_WithException_LogsTheChainAtWarnLevel()
    {
        string marker = Guid.NewGuid().ToString("N");

        AppLog.Warn($"flaky {marker}", new TimeoutException("poll timed out"));

        string content = LogProbe.ReadApplicationLog();
        Assert.Contains($"WARN    flaky {marker} | TimeoutException: poll timed out", content);
    }

    private static string NewLogPath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "BrightnessLogTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "brightness.log");
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}
