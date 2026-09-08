using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.Utilities;
using System.Windows.Input;
using System.Windows.Threading;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan MinimumBrightnessWriteInterval = TimeSpan.FromMilliseconds(100);
    private long? _lastBrightnessWriteTimestamp;
    private int _wheelRemainder;
    private readonly MonitorInfo _monitor;
    private readonly Func<int, Task<bool>> _commitBrightness;
    private int? _pendingBrightness;
    private bool _isBrightnessWriteRunning;
    private Task<bool> _brightnessWriteTask = Task.FromResult(true);
    private bool _isBrightnessAvailable;
    private bool _isTopologyStale;
    private int _connectionVersion;
    internal long BrightnessRevision { get; private set; }
    internal MonitorInfo Monitor => _monitor;
    private readonly IOledCareService _oledCareService;
    private readonly IUserDialogService _dialogService;
    private readonly Action<OledPanelProtectHistory>? _savePanelProtectHistory;
    private readonly Action<OledPanelProtectNotification?>?
        _savePanelProtectNotification;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherTimer? _panelProtectHistoryTimer;
    private readonly DispatcherTimer? _panelProtectEventTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private OledPanelProtectHistory? _panelProtectHistory;
    private int? _currentTotalUsageHours;
    private bool _isUsageHoursRefreshRunning;
    private bool _isPanelProtectEventRefreshRunning;
    private bool _isDisposed;
    private OledPanelProtectNotification? _panelProtectNotification;
    private OledPanelProtectEventType? _ignoredPanelProtectEventTypeUntilClear;

    internal string DisplayName => _monitor.DisplayName;
    public string FriendlyName => _monitor.FriendlyName;
    public string ModelName => _monitor.ModelName;
    private bool _isPrimary;
    public bool IsPrimary
    {
        get => _isPrimary;
        internal set => SetProperty(ref _isPrimary, value);
    }

    public bool CanAdjustBrightness => !_isDisposed && !_isTopologyStale && _isBrightnessAvailable;
    private string _brightnessStatusText = string.Empty;
    public string BrightnessStatusText
    {
        get => _brightnessStatusText;
        private set
        {
            if (SetProperty(ref _brightnessStatusText, value))
                OnPropertyChanged(nameof(HasBrightnessStatus));
        }
    }
    public bool HasBrightnessStatus => BrightnessStatusText.Length > 0;

    private string _oledOperationMessage = string.Empty;
    public string OledOperationMessage
    {
        get => _oledOperationMessage;
        private set
        {
            if (SetProperty(ref _oledOperationMessage, value))
                OnPropertyChanged(nameof(HasOledOperationMessage));
        }
    }
    public bool HasOledOperationMessage => OledOperationMessage.Length > 0;

    public bool HasRefreshRate => RefreshRateText.Length > 0;
    public string RefreshRateText =>
        MonitorInfoParser.FormatRefreshRate(_monitor.RefreshRateHz);

    public bool ShowOledCare => OledSupportLevel != OledSupportLevel.Unsupported;
    public bool IsOledExperimental => OledSupportLevel == OledSupportLevel.Experimental;
    public string OledSupportText => OledSupportLevel switch
    {
        OledSupportLevel.Verified => "Verified",
        OledSupportLevel.Experimental => "Experimental",
        _ => string.Empty
    };

    public OledSupportLevel OledSupportLevel { get; }

    public bool HasPendingPanelProtectNotification =>
        _panelProtectNotification != null;

    public string PanelProtectNotificationTooltip =>
        _panelProtectNotification is { } notification
            ? $"{OledCareService.DescribePanelProtectEvent(notification.Type)}. " +
              $"Detected {notification.FirstObservedAtUtc.ToLocalTime():g}. " +
              "Run Panel Protect from OLED Care."
            : string.Empty;

    private bool _isOledBusy;
    public bool IsOledBusy
    {
        get => _isOledBusy;
        private set
        {
            if (SetProperty(ref _isOledBusy, value))
            {
                OnPropertyChanged(nameof(CanRunPixelRefresh));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private OledCareStatus? _oledStatus;
    public bool CanRunPixelRefresh =>
        !_isDisposed && !_isTopologyStale && !IsOledBusy && (_oledStatus?.CanRunPixelRefresh ?? false);

    private string _oledStatusText = "Checking OLED Panel Info…";
    public string OledStatusText
    {
        get => _oledStatusText;
        private set
        {
            if (SetProperty(ref _oledStatusText, value))
                OnPropertyChanged(nameof(OledStatusSuffixText));
        }
    }

    public string OledStatusNumberText =>
        _currentTotalUsageHours?.ToString("N0") ?? string.Empty;
    public string OledStatusSuffixText => _currentTotalUsageHours.HasValue
        ? " total panel hours"
        : OledStatusText;

    private string _lastPanelProtectValueText = string.Empty;
    public string LastPanelProtectValueText
    {
        get => _lastPanelProtectValueText;
        private set => SetProperty(ref _lastPanelProtectValueText, value);
    }

    private string _lastPanelProtectExplanationText =
        "Not tracked yet · Panel Protect";
    public string LastPanelProtectExplanationText
    {
        get => _lastPanelProtectExplanationText;
        private set => SetProperty(ref _lastPanelProtectExplanationText, value);
    }

    internal bool IsPanelProtectHistoryTimerRunning =>
        _panelProtectHistoryTimer?.IsEnabled == true;
    internal bool IsPanelProtectEventTimerRunning =>
        _panelProtectEventTimer?.IsEnabled == true;

    public ICommand RunPixelRefreshCommand { get; }

    private double _brightnessValue;
    private int _committedBrightness;
    public double BrightnessValue
    {
        get => _brightnessValue;
        set
        {
            double clampedValue = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _brightnessValue, clampedValue))
            {
                BrightnessRevision++;
                OnPropertyChanged(nameof(BrightnessText));
            }
        }
    }

    public string BrightnessText => CanAdjustBrightness ? $"{(int)BrightnessValue}%" : "Unavailable";

    public MonitorSliderViewModel(
        MonitorInfo monitor,
        int? initialBrightness,
        Func<int, Task<bool>> commitBrightness,
        IOledCareService oledCareService,
        IUserDialogService dialogService,
        OledPanelProtectHistory? panelProtectHistory = null,
        Action<OledPanelProtectHistory>? savePanelProtectHistory = null,
        TimeProvider? timeProvider = null,
        OledPanelProtectNotification? panelProtectNotification = null,
        Action<OledPanelProtectNotification?>? savePanelProtectNotification = null)
    {
        _monitor = monitor;
        _isBrightnessAvailable = initialBrightness.HasValue;
        _brightnessValue = Math.Clamp(initialBrightness ?? 0, 0, 100);
        _committedBrightness = (int)_brightnessValue;
        if (!initialBrightness.HasValue)
            BrightnessStatusText = "Brightness unavailable. Check the display connection and brightness-control support, then refresh.";
        _commitBrightness = commitBrightness;
        _oledCareService = oledCareService;
        _dialogService = dialogService;
        _panelProtectHistory = panelProtectHistory;
        _savePanelProtectHistory = savePanelProtectHistory;
        _panelProtectNotification = panelProtectNotification;
        _savePanelProtectNotification = savePanelProtectNotification;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_panelProtectHistory is { } completedProtect &&
            _panelProtectNotification is { } notification &&
            completedProtect.LastStartedAtUtc >= notification.FirstObservedAtUtc)
        {
            SetPanelProtectNotification(null);
        }
        OledSupportLevel = _oledCareService.GetSupportLevel(monitor);
        RunPixelRefreshCommand = new AsyncRelayCommand(
            RunPixelRefreshAsync,
            () => CanRunPixelRefresh);

        if (ShowOledCare)
        {
            UpdateLastPanelProtectText();
            _panelProtectHistoryTimer = new DispatcherTimer(
                TimeSpan.FromMinutes(1),
                DispatcherPriority.Background,
                PanelProtectHistoryTimer_Tick,
                Dispatcher.CurrentDispatcher);
            _panelProtectHistoryTimer.Start();

            _panelProtectEventTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(1),
                DispatcherPriority.Background,
                PanelProtectEventTimer_Tick,
                Dispatcher.CurrentDispatcher);
            _panelProtectEventTimer.Start();

            AsyncHelper.FireAndForget(
                () => RefreshOledStatusAsync(_lifetimeCancellation.Token),
                "OLED initial status");
            AsyncHelper.FireAndForget(
                () => RefreshPanelProtectEventAsync(
                    _lifetimeCancellation.Token),
                "OLED Panel Protect event poll");
        }
    }

    public Task<bool> CommitBrightnessAsync()
    {
        if (!CanAdjustBrightness)
            return Task.FromResult(false);

        BrightnessRevision++;
        _pendingBrightness = (int)BrightnessValue;
        if (!_isBrightnessWriteRunning)
        {
            _isBrightnessWriteRunning = true;
            _brightnessWriteTask = DrainBrightnessAsync();
        }
        return _brightnessWriteTask;
    }

    internal Task<bool> AdjustBrightnessAsync(int delta)
    {
        if (!CanAdjustBrightness)
            return Task.FromResult(false);
        int currentBrightness = (int)BrightnessValue;
        int adjustedBrightness = Math.Clamp(currentBrightness + delta, 0, 100);
        if (adjustedBrightness == currentBrightness)
            return Task.FromResult(false);

        BrightnessValue = adjustedBrightness;
        return CommitBrightnessAsync();
    }

    internal Task<bool> AdjustBrightnessFromWheelAsync(int delta)
    {
        if (!CanAdjustBrightness)
        {
            _wheelRemainder = 0;
            return Task.FromResult(false);
        }
        // Precision touchpads emit fractions of the standard 120-unit notch.
        // Preserve those fractions rather than multiplying every tiny event by 2%.
        _wheelRemainder += delta;
        int notches = _wheelRemainder / 120;
        _wheelRemainder %= 120;
        return notches == 0 ? Task.FromResult(false) : AdjustBrightnessAsync(notches * 2);
    }

    private async Task<bool> DrainBrightnessAsync()
    {
        bool success = false;
        int failures = 0;
        try
        {
            while (_pendingBrightness.HasValue && CanAdjustBrightness)
            {
                if (_lastBrightnessWriteTimestamp is long previous)
                {
                    TimeSpan remaining = MinimumBrightnessWriteInterval - _timeProvider.GetElapsedTime(previous);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, _lifetimeCancellation.Token);
                }
                if (!CanAdjustBrightness || _pendingBrightness is not int brightness)
                    break;
                int connection = _connectionVersion;
                _pendingBrightness = null;
                _lastBrightnessWriteTimestamp = _timeProvider.GetTimestamp();
                try { success = await _commitBrightness(brightness); }
                catch { success = false; }
                if (_isDisposed)
                    return false;
                if (connection != _connectionVersion)
                    continue;
                BrightnessRevision++;
                if (success)
                {
                    failures = 0;
                    _committedBrightness = brightness;
                    BrightnessStatusText = string.Empty;
                }
                else
                {
                    failures++;
                    if (_pendingBrightness == null && failures < 2)
                    {
                        // One paced retry for a transient failure. Newer input
                        // arriving during the delay replaces this value.
                        _pendingBrightness = brightness;
                    }
                    else if (_pendingBrightness == null)
                    {
                        BrightnessValue = _committedBrightness;
                        BrightnessStatusText = "Brightness could not be changed. Try adjusting it again.";
                        // A rejected write does not erase demonstrated support.
                        // Only a failed discovery/read or disconnect disables it.
                    }
                }
            }
            return success;
        }
        catch (OperationCanceledException) when (_isDisposed)
        {
            return false;
        }
        finally
        {
            _pendingBrightness = null;
            _isBrightnessWriteRunning = false;
        }
    }

    internal void SuspendForTopologyChange()
    {
        _isTopologyStale = true;
        _connectionVersion++;
        _pendingBrightness = null;
        _wheelRemainder = 0;
        BrightnessRevision++;
        SetBrightnessAvailability(false, "Reconnecting display…");
        OnPropertyChanged(nameof(CanRunPixelRefresh));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void ApplyReading(MonitorInfo monitor, int? brightness, long expectedRevision)
    {
        _monitor.DisplayName = monitor.DisplayName;
        _monitor.FriendlyName = monitor.FriendlyName;
        _monitor.ModelName = monitor.ModelName;
        _monitor.OutputTechnology = monitor.OutputTechnology;
        _monitor.RefreshRateHz = monitor.RefreshRateHz;
        _monitor.SerialNumber = monitor.SerialNumber;
        bool reconnecting = _isTopologyStale;
        _isTopologyStale = false;
        if (reconnecting || (!_isBrightnessWriteRunning && BrightnessRevision == expectedRevision))
        {
            BrightnessValue = brightness ?? _committedBrightness;
            if (brightness.HasValue)
                _committedBrightness = (int)BrightnessValue;
            SetBrightnessAvailability(brightness.HasValue, brightness.HasValue ? string.Empty :
                "Brightness unavailable. Check the display connection and brightness-control support, then refresh.");
        }
        OnPropertyChanged(nameof(FriendlyName));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(RefreshRateText));
        OnPropertyChanged(nameof(HasRefreshRate));
        if (ShowOledCare && !IsOledBusy)
            AsyncHelper.FireAndForget(() => RefreshOledStatusAsync(_lifetimeCancellation.Token), "OLED status refresh");
    }

    private void SetBrightnessAvailability(bool available, string message)
    {
        _isBrightnessAvailable = available;
        BrightnessStatusText = message;
        OnPropertyChanged(nameof(CanAdjustBrightness));
        OnPropertyChanged(nameof(BrightnessText));
    }

    private async Task RefreshOledStatusAsync(CancellationToken cancellationToken)
    {
        IsOledBusy = true;
        try
        {
            OledCareStatus status = await _oledCareService.GetStatusAsync(
                _monitor,
                cancellationToken);
            if (!_isDisposed)
                ApplyOledStatus(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
                OledStatusText = $"Status unavailable: {ex.Message}";
        }
        finally
        {
            if (!_isDisposed)
                IsOledBusy = false;
        }
    }

    private async Task RunPixelRefreshAsync()
    {
        if (!_dialogService.ConfirmPixelRefresh(_monitor, OledSupportLevel))
            return;

        IsOledBusy = true;
        OledOperationMessage = string.Empty;
        try
        {
            PixelRefreshResult result = await _oledCareService.StartPixelRefreshAsync(
                _monitor, _lifetimeCancellation.Token);
            if (_isDisposed)
                return;
            OledOperationMessage = result.Message;
            if (result.Started)
            {
                _panelProtectHistory = new OledPanelProtectHistory(
                    _timeProvider.GetUtcNow(),
                    _currentTotalUsageHours);
                _ignoredPanelProtectEventTypeUntilClear =
                    _panelProtectNotification?.Type;
                SetPanelProtectNotification(null);
                UpdateLastPanelProtectText();

                try
                {
                    _savePanelProtectHistory?.Invoke(_panelProtectHistory);
                }
                catch
                {
                    // History is optional and must not turn a successfully sent
                    // panel-protect command into an application error.
                }

                if (_oledStatus?.PanelInfo?.TotalUsageHours == null)
                    OledStatusText = result.Message;
            }
            else
            {
                OledStatusText = result.Message;
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
                OledOperationMessage = OledStatusText = $"Pixel refresh could not be started: {ex.Message}";
        }
        finally
        {
            IsOledBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyOledStatus(OledCareStatus status)
    {
        _oledStatus = status;
        SetCurrentTotalUsageHours(status.PanelInfo?.TotalUsageHours);

        if (status.RefreshRateHz is int refreshRateHz)
        {
            _monitor.RefreshRateHz = refreshRateHz;
            OnPropertyChanged(nameof(RefreshRateText));
            OnPropertyChanged(nameof(HasRefreshRate));
        }
        if (status.PanelInfo is OledPanelInfo panelInfo &&
            panelInfo.PanelProtect.HasValue)
        {
            OledStatusText = panelInfo.TotalUsageHours is int hours
                ? $"{hours:N0} total panel hours"
                : "Panel protect";
        }
        else
        {
            OledStatusText = status.Message;
        }

        UpdateLastPanelProtectText();

        OnPropertyChanged(nameof(CanRunPixelRefresh));
        CommandManager.InvalidateRequerySuggested();
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

    private void UpdateLastPanelProtectText()
    {
        PanelProtectTextParts parts = FormatLastPanelProtectTextParts(
            _panelProtectHistory,
            _timeProvider.GetUtcNow(),
            _currentTotalUsageHours);
        LastPanelProtectValueText = parts.Value;
        LastPanelProtectExplanationText = parts.Explanation;
    }

    private void SetCurrentTotalUsageHours(int? value)
    {
        if (_currentTotalUsageHours == value)
            return;

        _currentTotalUsageHours = value;
        OnPropertyChanged(nameof(OledStatusNumberText));
        OnPropertyChanged(nameof(OledStatusSuffixText));
    }

    private async void PanelProtectHistoryTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDisposed)
            return;

        UpdateLastPanelProtectText();
        await RefreshTotalUsageHoursAsync();
    }

    private async void PanelProtectEventTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshPanelProtectEventAsync(_lifetimeCancellation.Token);
    }

    internal async Task RefreshPanelProtectEventAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _isPanelProtectEventRefreshRunning)
            return;

        _isPanelProtectEventRefreshRunning = true;
        try
        {
            OledPanelProtectEvent? panelEvent =
                await _oledCareService.GetPanelProtectEventAsync(
                    _monitor,
                    cancellationToken);
            if (!_isDisposed && panelEvent?.RequiresAttention == true)
            {
                if (panelEvent.Type == _ignoredPanelProtectEventTypeUntilClear)
                    return;

                LatchPanelProtectNotification(panelEvent.Type);
            }
            else if (panelEvent?.Type == OledPanelProtectEventType.None)
            {
                _ignoredPanelProtectEventTypeUntilClear = null;
                if (_panelProtectHistory is { } completedProtect &&
                    _panelProtectNotification is { } notification &&
                    completedProtect.LastStartedAtUtc >= notification.FirstObservedAtUtc)
                {
                    SetPanelProtectNotification(null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A notification poll is advisory. Keep a previously latched
            // notification and silently retry on the next timer tick.
        }
        finally
        {
            _isPanelProtectEventRefreshRunning = false;
        }
    }

    private void LatchPanelProtectNotification(
        OledPanelProtectEventType eventType)
    {
        if (_panelProtectNotification?.Type == eventType)
            return;

        DateTimeOffset observedAt = _panelProtectNotification?.FirstObservedAtUtc
            ?? _timeProvider.GetUtcNow();
        SetPanelProtectNotification(new OledPanelProtectNotification(
            eventType,
            observedAt,
            _currentTotalUsageHours));
    }

    private void SetPanelProtectNotification(
        OledPanelProtectNotification? notification)
    {
        if (_panelProtectNotification == notification)
            return;

        _panelProtectNotification = notification;
        OnPropertyChanged(nameof(HasPendingPanelProtectNotification));
        OnPropertyChanged(nameof(PanelProtectNotificationTooltip));

        try
        {
            _savePanelProtectNotification?.Invoke(notification);
        }
        catch
        {
            // Persistence must not interfere with OLED control or the UI.
        }
    }

    internal async Task RefreshTotalUsageHoursAsync()
    {
        if (_isDisposed ||
            _panelProtectHistory == null ||
            IsOledBusy ||
            _isUsageHoursRefreshRunning)
            return;

        _isUsageHoursRefreshRunning = true;
        try
        {
            int? totalUsageHours = await _oledCareService.GetTotalUsageHoursAsync(
                _monitor,
                _lifetimeCancellation.Token);
            if (!_isDisposed && totalUsageHours is >= 0)
            {
                SetCurrentTotalUsageHours(totalUsageHours);
                OledStatusText = $"{totalUsageHours:N0} total panel hours";
                UpdateLastPanelProtectText();
            }
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Keep the last known monitor value and retry on the next tick.
        }
        finally
        {
            _isUsageHoursRefreshRunning = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (_panelProtectHistoryTimer != null)
        {
            _panelProtectHistoryTimer.Stop();
            _panelProtectHistoryTimer.Tick -= PanelProtectHistoryTimer_Tick;
        }
        if (_panelProtectEventTimer != null)
        {
            _panelProtectEventTimer.Stop();
            _panelProtectEventTimer.Tick -= PanelProtectEventTimer_Tick;
        }
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record PanelProtectTextParts(
        string Value,
        string Explanation)
    {
        public string FullText => $"{Value}{Explanation}";
    }
}
