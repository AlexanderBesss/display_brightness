using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.Utilities;
using System.Windows.Input;
using System.Windows.Threading;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : ViewModelBase, IDisposable
{
    private readonly MonitorInfo _monitor;
    private readonly Func<int, bool> _commitBrightness;
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

    internal string DisplayName { get; }
    public string FriendlyName { get; }
    public string ModelName { get; }
    public bool IsPrimary { get; internal set; }

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
        !IsOledBusy && (_oledStatus?.CanRunPixelRefresh ?? false);

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
                OnPropertyChanged(nameof(BrightnessText));
        }
    }

    public string BrightnessText => $"{(int)BrightnessValue}%";

    public MonitorSliderViewModel(
        MonitorInfo monitor,
        int initialBrightness,
        Func<int, bool> commitBrightness,
        IOledCareService oledCareService,
        IUserDialogService dialogService,
        OledPanelProtectHistory? panelProtectHistory = null,
        Action<OledPanelProtectHistory>? savePanelProtectHistory = null,
        TimeProvider? timeProvider = null,
        OledPanelProtectNotification? panelProtectNotification = null,
        Action<OledPanelProtectNotification?>? savePanelProtectNotification = null)
    {
        _monitor = monitor;
        DisplayName = monitor.DisplayName;
        FriendlyName = monitor.FriendlyName;
        ModelName = monitor.ModelName;
        _brightnessValue = Math.Clamp(initialBrightness, 0, 100);
        _committedBrightness = (int)_brightnessValue;
        _commitBrightness = commitBrightness;
        _oledCareService = oledCareService;
        _dialogService = dialogService;
        _panelProtectHistory = panelProtectHistory;
        _savePanelProtectHistory = savePanelProtectHistory;
        _panelProtectNotification = panelProtectNotification;
        _savePanelProtectNotification = savePanelProtectNotification;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    public void CommitBrightness()
    {
        int brightness = (int)BrightnessValue;
        if (_commitBrightness(brightness))
        {
            _committedBrightness = brightness;
            return;
        }

        BrightnessValue = _committedBrightness;
    }

    internal bool AdjustBrightness(int delta)
    {
        int currentBrightness = (int)BrightnessValue;
        int adjustedBrightness = Math.Clamp(currentBrightness + delta, 0, 100);
        if (adjustedBrightness == currentBrightness)
            return false;

        BrightnessValue = adjustedBrightness;
        if (_commitBrightness(adjustedBrightness))
        {
            _committedBrightness = adjustedBrightness;
            return true;
        }

        BrightnessValue = _committedBrightness;
        return false;
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
        try
        {
            PixelRefreshResult result = await _oledCareService.StartPixelRefreshAsync(_monitor);
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
            OledStatusText = $"Pixel refresh could not be started: {ex.Message}";
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
