using DisplayBrightness.Services;

namespace DisplayBrightness.Utilities;

/// <summary>
/// Logs the first failure of a run of failures and stays quiet until an attempt
/// succeeds again. Polling code calls <see cref="Report"/> on every failure and
/// <see cref="Cleared"/> on every success, so one permanently broken device
/// cannot write a line per poll into the rotating log.
/// Best-effort by design: a race between threads costs at most one extra line.
/// </summary>
internal sealed class FailureStreak
{
    private const int Clean = 0;
    private const int Reported = 1;

    private int _state;

    public void Report(string message)
    {
        if (Interlocked.Exchange(ref _state, Reported) == Reported)
            return;

        AppLog.Warn(message);
    }

    public void Cleared() => Interlocked.Exchange(ref _state, Clean);
}
