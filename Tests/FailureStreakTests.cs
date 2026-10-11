using DisplayBrightness.Utilities;

namespace DisplayBrightness.Tests;

[Collection("ApplicationLog")]
public sealed class FailureStreakTests
{
    [Fact]
    public void Report_LogsOnlyTheFirstFailureOfAStreak()
    {
        string marker = Guid.NewGuid().ToString("N");
        var streak = new FailureStreak();

        streak.Report($"poll failed {marker}");
        streak.Report($"poll failed {marker}");
        streak.Report($"poll failed {marker}");

        Assert.Equal(1, CountOccurrences(LogProbe.ReadApplicationLog(), marker));
    }

    [Fact]
    public void Cleared_LetsTheNextFailureReportAgain()
    {
        string marker = Guid.NewGuid().ToString("N");
        var streak = new FailureStreak();

        streak.Report($"poll failed {marker}");
        streak.Cleared();
        streak.Report($"poll failed {marker}");

        Assert.Equal(2, CountOccurrences(LogProbe.ReadApplicationLog(), marker));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
