using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.Utilities;
using System.Windows.Threading;

namespace DisplayBrightness.ViewModels;

/// <summary>
/// Owns one monitor's Panel Protect bookkeeping: the last protect run, the
/// notification latched from the scaler-event register, the panel hours the
/// elapsed-time text is measured against, and the two polls that keep them
/// current. The view model stays a thin binding surface over this state.
/// </summary>
internal sealed class PanelProtectTracker : IDisposable
{
    private static readonly TimeSpan HistoryRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EventPollInterval = TimeSpan.FromSeconds(1);

    private readonly MonitorInfo _monitor;
    private readonly IOledCareService _oledCareService;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<bool> _isBusy;
    private readonly Action<OledPanelProtectHistory>? _saveHistory;
    private readonly Action<OledPanelProtectNotification?>? _saveNotification;

    private DispatcherTimer? _historyTimer;
    private DispatcherTimer? _eventTimer;
    private OledPanelProtectHistory? _history;
    private OledPanelProtectNotification? _notification;
    private OledPanelProtectEventType? _ignoredEventTypeUntilClear;
    private int? _totalUsageHours;
    private string _lastValueText = string.Empty;
    private string _lastExplanationText = "Not tracked yet · Panel Protect";
    private bool _isUsageHoursRefreshRunning;
    private bool _isEventRefreshRunning;
    private readonly FailureStreak _eventPollFailures = new();
    private readonly FailureStreak _usageHoursFailures = new();
    private bool _isDisposed;

    /// <summary>Raised whenever any value the view binds to has changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after a poll reads fresh panel hours from the monitor.</summary>
    public event Action<int>? TotalUsageHoursUpdated;

    internal PanelProtectTracker(
        MonitorInfo monitor,
        IOledCareService oledCareService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Func<bool> isBusy,
        OledPanelProtectHistory? history,
        OledPanelProtectNotification? notification,
        Action<OledPanelProtectHistory>? saveHistory,
        Action<OledPanelProtectNotification?>? saveNotification)
    {
        _monitor = monitor;
        _oledCareService = oledCareService;
        _timeProvider = timeProvider;
        _cancellationToken = cancellationToken;
        _isBusy = isBusy;
        _history = history;
        _notification = notification;
        _saveHistory = saveHistory;
        _saveNotification = saveNotification;

        if (_history is { } completedProtect &&
            _notification is { } pendingNotification &&
            completedProtect.LastStartedAtUtc >= pendingNotification.FirstObservedAtUtc)
        {
            SetNotification(null);
        }
    }

    public OledPanelProtectHistory? History => _history;
    public OledPanelProtectNotification? Notification => _notification;
    public int? TotalUsageHours => _totalUsageHours;

    public bool HasPendingNotification => _notification != null;

    public string NotificationTooltip =>
        _notification is { } notification
            ? $"{OledCareService.DescribePanelProtectEvent(notification.Type)}. " +
              $"Detected {notification.FirstObservedAtUtc.ToLocalTime():g}. " +
              "Run Panel Protect from OLED Care."
            : string.Empty;

    public string LastValueText => _lastValueText;
    public string LastExplanationText => _lastExplanationText;

    public bool IsHistoryTimerRunning => _historyTimer?.IsEnabled == true;
    public bool IsEventTimerRunning => _eventTimer?.IsEnabled == true;

    /// <summary>
    /// Starts the two polling timers. Must run on the thread that owns the
    /// dispatcher the timers should tick on.
    /// </summary>
    public void Start()
    {
        RefreshText();

        _historyTimer = new DispatcherTimer(
            HistoryRefreshInterval,
            DispatcherPriority.Background,
            HistoryTimer_Tick,
            Dispatcher.CurrentDispatcher);
        _historyTimer.Start();

        _eventTimer = new DispatcherTimer(
            EventPollInterval,
            DispatcherPriority.Background,
            EventTimer_Tick,
            Dispatcher.CurrentDispatcher);
        _eventTimer.Start();
    }

    /// <summary>
    /// Records that a Panel Protect routine was accepted by the monitor just now.
    /// The notification that prompted it is cleared and ignored until the scaler
    /// event register reports no event again.
    /// </summary>
    public void RecordProtectStarted()
    {
        _history = new OledPanelProtectHistory(
            _timeProvider.GetUtcNow(),
            _totalUsageHours);
        _ignoredEventTypeUntilClear = _notification?.Type;
        SetNotification(null);
        RefreshText();

        try
        {
            _saveHistory?.Invoke(_history);
        }
        catch
        {
            // History is optional and must not turn a successfully sent
            // panel-protect command into an application error.
        }
    }

    /// <summary>
    /// Adopts the panel hours reported by a full OLED status read.
    /// </summary>
    public void SetUsageHours(int? totalUsageHours)
    {
        if (_totalUsageHours == totalUsageHours)
            return;

        _totalUsageHours = totalUsageHours;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshText()
    {
        PanelProtectTextParts parts = FormatLastPanelProtectTextParts(
            _history,
            _timeProvider.GetUtcNow(),
            _totalUsageHours);
        if (parts.Value == _lastValueText &&
            parts.Explanation == _lastExplanationText)
            return;

        _lastValueText = parts.Value;
        _lastExplanationText = parts.Explanation;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal async Task RefreshPanelProtectEventAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _isEventRefreshRunning)
            return;

        _isEventRefreshRunning = true;
        try
        {
            OledPanelProtectEvent? panelEvent =
                await _oledCareService.GetPanelProtectEventAsync(
                    _monitor,
                    cancellationToken);
            _eventPollFailures.Cleared();
            if (!_isDisposed && panelEvent?.RequiresAttention == true)
            {
                if (panelEvent.Type == _ignoredEventTypeUntilClear)
                    return;

                LatchNotification(panelEvent.Type);
            }
            else if (panelEvent?.Type == OledPanelProtectEventType.None)
            {
                _ignoredEventTypeUntilClear = null;
                if (_history is { } completedProtect &&
                    _notification is { } pendingNotification &&
                    completedProtect.LastStartedAtUtc >= pendingNotification.FirstObservedAtUtc)
                {
                    SetNotification(null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A notification poll is advisory. Keep a previously latched
            // notification and silently retry on the next timer tick. The poll
            // runs every second, so log only the first failure of a streak.
            _eventPollFailures.Report(
                $"Panel Protect event poll failed for {_monitor.FriendlyName} " +
                $"[{_monitor.DevicePath}]: {ex.Message}");
        }
        finally
        {
            _isEventRefreshRunning = false;
        }
    }

    internal async Task RefreshTotalUsageHoursAsync()
    {
        if (_isDisposed ||
            _history == null ||
            _isBusy() ||
            _isUsageHoursRefreshRunning)
            return;

        _isUsageHoursRefreshRunning = true;
        try
        {
            int? totalUsageHours = await _oledCareService.GetTotalUsageHoursAsync(
                _monitor,
                _cancellationToken);
            _usageHoursFailures.Cleared();
            if (!_isDisposed && totalUsageHours is >= 0)
            {
                _totalUsageHours = totalUsageHours;
                TotalUsageHoursUpdated?.Invoke(totalUsageHours.Value);
                RefreshText();
            }
        }
        catch (OperationCanceledException)
            when (_cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Keep the last known monitor value and retry on the next tick;
            // log only the first failure of a streak.
            _usageHoursFailures.Report(
                $"panel hours read failed for {_monitor.FriendlyName} " +
                $"[{_monitor.DevicePath}]: {ex.Message}");
        }
        finally
        {
            _isUsageHoursRefreshRunning = false;
        }
    }

    internal static string FormatLastPanelProtectText(
        OledPanelProtectHistory? history,
        DateTimeOffset now,
        int? currentTotalUsageHours = null) =>
        FormatLastPanelProtectTextParts(
            history,
            now,
            currentTotalUsageHours).FullText;

    private static PanelProtectTextParts FormatLastPanelProtectTextParts(
        OledPanelProtectHistory? history,
        DateTimeOffset now,
        int? currentTotalUsageHours)
    {
        if (history == null)
            return new(string.Empty,
                "Not tracked yet · Panel Protect");

        TimeSpan elapsed = now - history.LastStartedAtUtc;
        if (elapsed < OledCareService.PanelProtectRoutineDuration)
        {
            TimeSpan running = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            int minutesLeft = (int)Math.Ceiling(
                (OledCareService.PanelProtectRoutineDuration - running)
                    .TotalMinutes);
            return new(
                string.Empty,
                $"Running · about {minutesLeft}m left · Panel Protect");
        }

        TimeSpan sinceCompletion =
            elapsed - OledCareService.PanelProtectRoutineDuration;
        if (sinceCompletion < TimeSpan.FromMinutes(1))
            return new(string.Empty,
                "Just now · Panel Protect completed");

        if (currentTotalUsageHours is int currentHours &&
            history.TotalUsageHoursAtStart is int startedAtHours &&
            currentHours >= startedAtHours)
        {
            int panelHoursElapsed = currentHours - startedAtHours;
            double clockDifference = Math.Abs(
                sinceCompletion.TotalHours - panelHoursElapsed);
            if (clockDifference > 1)
            {
                string unit = panelHoursElapsed == 1
                    ? "panel hour"
                    : "panel hours";
                return new(
                    $"{panelHoursElapsed:N0} {unit}",
                    " ago · Panel Protect completed");
            }
        }

        long totalMinutes = (long)Math.Floor(sinceCompletion.TotalMinutes);
        if (totalMinutes < 60)
        {
            return new(
                $"{totalMinutes}m",
                " ago · Panel Protect completed");
        }

        long hours = totalMinutes / 60;
        long minutes = totalMinutes % 60;
        return new(
            $"{hours}h {minutes}m",
            " ago · Panel Protect completed");
    }

    private void LatchNotification(OledPanelProtectEventType eventType)
    {
        if (_notification?.Type == eventType)
            return;

        AppLog.Info(
            $"Panel Protect notification for {_monitor.FriendlyName} [{_monitor.DevicePath}]: " +
            $"{OledCareService.DescribePanelProtectEvent(eventType)}");
        DateTimeOffset observedAt = _notification?.FirstObservedAtUtc
            ?? _timeProvider.GetUtcNow();
        SetNotification(new OledPanelProtectNotification(
            eventType,
            observedAt,
            _totalUsageHours));
    }

    private void SetNotification(OledPanelProtectNotification? notification)
    {
        if (_notification == notification)
            return;

        _notification = notification;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            _saveNotification?.Invoke(notification);
        }
        catch
        {
            // Persistence must not interfere with OLED control or the UI.
        }
    }

    private async void HistoryTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDisposed)
            return;

        RefreshText();
        await RefreshTotalUsageHoursAsync();
    }

    private async void EventTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshPanelProtectEventAsync(_cancellationToken);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (_historyTimer != null)
        {
            _historyTimer.Stop();
            _historyTimer.Tick -= HistoryTimer_Tick;
        }
        if (_eventTimer != null)
        {
            _eventTimer.Stop();
            _eventTimer.Tick -= EventTimer_Tick;
        }
    }

    private sealed record PanelProtectTextParts(
        string Value,
        string Explanation)
    {
        public string FullText => $"{Value}{Explanation}";
    }
}
