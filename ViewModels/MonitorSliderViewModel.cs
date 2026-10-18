using DisplayBrightness.Models;
using DisplayBrightness.Services;
using DisplayBrightness.Utilities;
using System.Windows.Input;

namespace DisplayBrightness.ViewModels;

public class MonitorSliderViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan MinimumBrightnessWriteInterval = TimeSpan.FromMilliseconds(100);
    private const int FailedReadsBeforeUnavailable = 2;
    private const int WriteAttemptsBeforeReverting = 2;
    private const string UnavailableMessage =
        "Brightness unavailable. Check the display connection and brightness-control support, then refresh.";

    private readonly MonitorInfo _monitor;
    private readonly Func<int, Task<bool>> _commitBrightness;
    private readonly IOledCareService _oledCareService;
    private readonly IUserDialogService _dialogService;
    private readonly TimeProvider _timeProvider;
    private readonly PanelProtectTracker _panelProtect;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly FailureStreak _oledReadFailures = new();

    private long? _lastBrightnessWriteTimestamp;
    private int _wheelRemainder;
    private int _failedReads;
    private int? _pendingBrightness;
    private bool _isBrightnessWriteRunning;
    private Task<bool> _brightnessWriteTask = Task.FromResult(true);
    private bool _isBrightnessAvailable;
    private bool _hasDemonstratedBrightness;
    private bool _isTopologyStale;
    private int _connectionVersion;
    private OledCareStatus? _oledStatus;
    private OledConnectionState? _lastOledConnectionState;
    private bool _isDisposed;

    internal long BrightnessRevision { get; private set; }
    /// <summary>
    /// True once this display has answered a brightness read (or arrived with a
    /// readable initial value). Unlike <see cref="CanAdjustBrightness"/> it is not
    /// cleared when the display goes into the Unavailable state, so the read
    /// pipeline keeps giving a transiently silent panel the retry it paid while
    /// healthy, while a display that never answers still pays no retry delay.
    /// </summary>
    internal bool HasDemonstratedBrightness => _hasDemonstratedBrightness;
    internal MonitorInfo Monitor => _monitor;

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

    public bool HasPendingPanelProtectNotification => _panelProtect.HasPendingNotification;
    public string PanelProtectNotificationTooltip => _panelProtect.NotificationTooltip;
    public string LastPanelProtectValueText => _panelProtect.LastValueText;
    public string LastPanelProtectExplanationText => _panelProtect.LastExplanationText;

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
        _panelProtect.TotalUsageHours?.ToString("N0") ?? string.Empty;
    public string OledStatusSuffixText => _panelProtect.TotalUsageHours.HasValue
        ? " total panel hours"
        : OledStatusText;

    internal bool IsPanelProtectHistoryTimerRunning => _panelProtect.IsHistoryTimerRunning;
    internal bool IsPanelProtectEventTimerRunning => _panelProtect.IsEventTimerRunning;

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
        _hasDemonstratedBrightness = initialBrightness.HasValue;
        _brightnessValue = Math.Clamp(initialBrightness ?? 0, 0, 100);
        _committedBrightness = (int)_brightnessValue;
        if (!initialBrightness.HasValue)
            BrightnessStatusText = UnavailableMessage;
        _commitBrightness = commitBrightness;
        _oledCareService = oledCareService;
        _dialogService = dialogService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        OledSupportLevel = _oledCareService.GetSupportLevel(monitor);
        _panelProtect = new PanelProtectTracker(
            monitor,
            oledCareService,
            _timeProvider,
            _lifetimeCancellation.Token,
            () => IsOledBusy,
            panelProtectHistory,
            panelProtectNotification,
            savePanelProtectHistory,
            savePanelProtectNotification);
        _panelProtect.Changed += PanelProtect_Changed;
        _panelProtect.TotalUsageHoursUpdated += hours =>
            OledStatusText = $"{hours:N0} total panel hours";
        RunPixelRefreshCommand = new AsyncRelayCommand(
            RunPixelRefreshAsync,
            () => CanRunPixelRefresh);

        if (ShowOledCare)
        {
            _panelProtect.Start();
            AsyncHelper.FireAndForget(
                () => RefreshOledStatusAsync(_lifetimeCancellation.Token),
                "OLED initial status");
            AsyncHelper.FireAndForget(
                () => _panelProtect.RefreshPanelProtectEventAsync(
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
        int notches = _wheelRemainder / WheelNotch.DetentUnits;
        _wheelRemainder %= WheelNotch.DetentUnits;
        return notches == 0 ? Task.FromResult(false) : AdjustBrightnessAsync(notches * WheelNotch.BrightnessStep);
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
                catch (Exception ex)
                {
                    // The commit pipeline logs its own failures; this only fires
                    // when the injected callback itself throws, so the wording is
                    // deliberately different from that site.
                    success = false;
                    AppLog.Warn(
                        $"brightness commit callback threw for {DisplayName} [{_monitor.DevicePath}] -> {brightness}%", ex);
                }
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
                    if (_pendingBrightness == null && failures < WriteAttemptsBeforeReverting)
                    {
                        // One paced retry for a transient failure. Newer input
                        // arriving during the delay replaces this value.
                        _pendingBrightness = brightness;
                    }
                    else if (_pendingBrightness == null)
                    {
                        AppLog.Warn(
                            $"brightness write to {DisplayName} [{_monitor.DevicePath}] failed at " +
                            $"{brightness}%; reverted to {_committedBrightness}%");
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
        _failedReads = 0;
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
            ApplyReadAvailability(brightness, reconnecting);
        }
        OnPropertyChanged(nameof(FriendlyName));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(RefreshRateText));
        OnPropertyChanged(nameof(HasRefreshRate));
        if (ShowOledCare && !IsOledBusy)
            AsyncHelper.FireAndForget(() => RefreshOledStatusAsync(_lifetimeCancellation.Token), "OLED status refresh");
    }

    private void ApplyReadAvailability(int? brightness, bool reconnecting)
    {
        if (brightness.HasValue)
        {
            _failedReads = 0;
            _hasDemonstratedBrightness = true;
            if (!_isBrightnessAvailable)
                AppLog.Info($"brightness readable again for {DisplayName} [{_monitor.DevicePath}]");
            SetBrightnessAvailability(true, string.Empty);
            return;
        }

        // A display that is still enumerated but unreadable has usually just
        // timed out on DDC/CI. Keep the last known value and the controls through
        // one failure, so a single timeout cannot take the slider away from a
        // monitor that was working moments earlier. The counter only counts while
        // the controls are up; a reconnect or a lost control resets it.
        if (!reconnecting && CanAdjustBrightness)
        {
            _failedReads++;
            if (_failedReads < FailedReadsBeforeUnavailable)
            {
                if (_failedReads == 1)
                    AppLog.Warn(
                        $"brightness read failed for {DisplayName} [{_monitor.DevicePath}]; " +
                        "keeping the last value");
                BrightnessStatusText = "Brightness could not be read. Keeping the last value.";
                return;
            }
        }

        _failedReads = 0;
        if (CanAdjustBrightness)
            AppLog.Warn($"brightness became unreadable for {DisplayName} [{_monitor.DevicePath}]");
        SetBrightnessAvailability(false, UnavailableMessage);
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
            _oledReadFailures.Cleared();
            if (!_isDisposed)
                ApplyOledStatus(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Refreshed with every reading cycle: report the first failure of a
            // streak rather than one line per cycle.
            _oledReadFailures.Report(
                $"OLED status read failed for {DisplayName} [{_monitor.DevicePath}]: {ex.Message}");
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
            AppLog.Info(
                $"pixel refresh {(result.Started ? "started" : "not started")} " +
                $"for {DisplayName} [{_monitor.DevicePath}]: {result.Message}");
            OledOperationMessage = result.Message;
            if (result.Started)
            {
                _panelProtect.RecordProtectStarted();
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
            AppLog.Error($"pixel refresh failed for {DisplayName} [{_monitor.DevicePath}]", ex);
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
        if (_lastOledConnectionState != status.ConnectionState)
        {
            _lastOledConnectionState = status.ConnectionState;
            AppLog.Info(
                $"OLED Care connection for {DisplayName} [{_monitor.DevicePath}]: " +
                $"{status.ConnectionState} ({status.Message})");
        }
        _panelProtect.SetUsageHours(status.PanelInfo?.TotalUsageHours);

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

        _panelProtect.RefreshText();

        OnPropertyChanged(nameof(CanRunPixelRefresh));
        CommandManager.InvalidateRequerySuggested();
    }

    private void PanelProtect_Changed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingPanelProtectNotification));
        OnPropertyChanged(nameof(PanelProtectNotificationTooltip));
        OnPropertyChanged(nameof(LastPanelProtectValueText));
        OnPropertyChanged(nameof(LastPanelProtectExplanationText));
        OnPropertyChanged(nameof(OledStatusNumberText));
        OnPropertyChanged(nameof(OledStatusSuffixText));
    }

    internal Task RefreshPanelProtectEventAsync(CancellationToken cancellationToken = default) =>
        _panelProtect.RefreshPanelProtectEventAsync(cancellationToken);

    internal Task RefreshTotalUsageHoursAsync() =>
        _panelProtect.RefreshTotalUsageHoursAsync();

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _panelProtect.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
