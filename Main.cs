using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Flow.Launcher.Plugin.NoSleep.Settings;
using Flow.Launcher.Plugin.NoSleep.Tray;
using Flow.Launcher.Plugin.NoSleep.Utilities;
using Microsoft.Win32;

namespace Flow.Launcher.Plugin.NoSleep;

/// <summary>
/// Main plugin class for the NoSleep Flow Launcher plugin
/// </summary>
public class NoSleep : IPlugin, ISettingProvider, IDisposable
{
    internal static bool IsActive { get; private set; } = false;

    private PluginInitContext _context;
    private Settings.Settings _settings;
    private string _iconPath;
    private NoSleepTimer _timer;
    private TimeSpan? _activeDuration;
    private bool _activeKeepDisplayAwake;
    private bool _systemEventsSubscribed;

    // Serializes all state transitions. State (IsActive, _timer, _activeDuration,
    // _activeKeepDisplayAwake) is touched from Flow Launcher action callbacks, the
    // countdown's Expired event (thread pool), tray menu clicks (Task.Run), and
    // SystemEvents power/session callbacks. Monitor is reentrant, so nested calls
    // (e.g. ExtendNoSleep -> StartNoSleep) are safe. GetStatusText must NOT take this
    // lock — the tray thread calls it while holding TrayIconManager's lock, so taking
    // _stateLock there would invert lock ordering and risk a deadlock.
    private readonly object _stateLock = new();

    private static readonly (string label, TimeSpan? duration)[] Presets =
    {
        ("indefinitely", null),
        ("30 minutes", TimeSpan.FromMinutes(30)),
        ("1 hour", TimeSpan.FromHours(1)),
        ("2 hours", TimeSpan.FromHours(2)),
        ("8 hours", TimeSpan.FromHours(8)),
    };

    /// <summary>
    /// Initialize the plugin
    /// </summary>
    /// <param name="context">Plugin initialization context</param>
    public void Init(PluginInitContext context)
    {
        _context = context;
        _settings = context.API.LoadSettingJsonStorage<Settings.Settings>();
        _iconPath = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, "Images/icon.png");
        _activeKeepDisplayAwake = _settings.KeepDisplayAwake;
        SubscribeSystemEvents();

        if (_settings.StartWithFlowLauncher)
        {
            StartNoSleep();
        }
    }

    /// <summary>
    /// Query method for Flow Launcher
    /// </summary>
    public List<Result> Query(Query query)
    {
        var results = new List<Result>();
        var input = query.Search.Trim();

        if (string.IsNullOrEmpty(input))
            return BuildPresetResults(results);

        if (input.Equals("help", StringComparison.OrdinalIgnoreCase)
            || input.Equals("?", StringComparison.OrdinalIgnoreCase))
            return BuildHelpResults(results);

        if (input.Equals("s", StringComparison.OrdinalIgnoreCase)
            || input.Equals("status", StringComparison.OrdinalIgnoreCase))
            return BuildStatusResult(results);

        if (input.Equals("screen", StringComparison.OrdinalIgnoreCase)
            || input.Equals("display", StringComparison.OrdinalIgnoreCase))
            return BuildDisplayModeResults(results);

        if (TryParseDisplayModeCommand(input, out var keepDisplayAwake))
            return BuildDisplayModeResult(results, keepDisplayAwake);

        if (input.StartsWith("+", StringComparison.OrdinalIgnoreCase))
            return BuildExtendResult(results, input[1..].Trim());

        if (input.Equals("off", StringComparison.OrdinalIgnoreCase))
            return BuildOffResult(results);

        return BuildCustomDurationResult(results, input);
    }

    private List<Result> BuildPresetResults(List<Result> results)
    {
        var score = 500_000;

        if (IsActive)
        {
            results.Add(new Result
            {
                Title = "Turn off NoSleep",
                SubTitle = $"Currently active - {GetStatusText()}",
                Action = c => { StopNoSleep(); return true; },
                IcoPath = _iconPath,
                Score = score
            });

            foreach (var (label, duration) in Presets)
            {
                if (duration == _activeDuration)
                    continue;

                score -= 100_000;
                var d = duration;
                results.Add(new Result
                {
                    Title = $"Switch to {label}",
                    SubTitle = "",
                    Action = c => { StartNoSleep(d); return true; },
                    IcoPath = _iconPath,
                    Score = score
                });
            }
        }
        else
        {
            foreach (var (label, duration) in Presets)
            {
                var d = duration;
                results.Add(new Result
                {
                    Title = $"Keep awake for {label}",
                    SubTitle = "",
                    Action = c => { StartNoSleep(d); return true; },
                    IcoPath = _iconPath,
                    Score = score
                });
                score -= 100_000;
            }
        }

        results.Add(new Result
        {
            Title = "Help and commands",
            SubTitle = "Type ns help to see timers, status, display, and battery commands",
            Action = c => false,
            IcoPath = _iconPath,
            Score = 1
        });

        return results;
    }

    private List<Result> BuildHelpResults(List<Result> results)
    {
        var score = 900_000;
        AddHelpResult(results, "ns", "Show presets. Press Enter on the first result to turn NoSleep on indefinitely.", score -= 10_000);
        AddHelpResult(results, "ns 45m / ns 1.5 / ns 3h", "Start a timer using minutes or hours.", score -= 10_000);
        AddHelpResult(results, "ns until 5pm / ns 17:30", "Keep awake until a clock time. Past times roll to tomorrow.", score -= 10_000);
        AddHelpResult(results, "ns +30m", "Extend the current timer. If NoSleep is off, starts a new timer.", score -= 10_000);
        AddHelpResult(results, "ns s", "Show ON/OFF status, remaining time, expiry time, and display mode.", score -= 10_000);
        AddHelpResult(results, "ns screen off / ns screen on", "Allow display sleep or keep the display awake for the active session.", score -= 10_000);
        AddHelpResult(results, "ns off", "Turn NoSleep off.", score -= 10_000);
        AddHelpResult(results, "Settings", "AC-only, low battery auto-off, lock auto-off, tray icon, and notification options live in plugin settings.", score -= 10_000);

        return results;
    }

    private void AddHelpResult(List<Result> results, string title, string subtitle, int score)
    {
        results.Add(new Result
        {
            Title = title,
            SubTitle = subtitle,
            Action = c => false,
            IcoPath = _iconPath,
            Score = score
        });
    }

    private List<Result> BuildDisplayModeResults(List<Result> results)
    {
        results.Add(BuildDisplayModeChoice(false, 500_000));
        results.Add(BuildDisplayModeChoice(true, 400_000));
        return results;
    }

    private List<Result> BuildDisplayModeResult(List<Result> results, bool keepDisplayAwake)
    {
        results.Add(BuildDisplayModeChoice(keepDisplayAwake, 500_000));
        return results;
    }

    private Result BuildDisplayModeChoice(bool keepDisplayAwake, int score)
    {
        return new Result
        {
            Title = keepDisplayAwake ? "Keep display awake" : "Allow display to turn off",
            SubTitle = IsActive
                ? "Applies to the current NoSleep session"
                : "Starts NoSleep indefinitely with this display mode",
            Action = c =>
            {
                if (IsActive)
                    SetDisplayMode(keepDisplayAwake);
                else
                    StartNoSleep(null, keepDisplayAwake);
                return true;
            },
            IcoPath = _iconPath,
            Score = score
        };
    }

    private List<Result> BuildStatusResult(List<Result> results)
    {
        results.Add(new Result
        {
            Title = IsActive ? "NoSleep is ON" : "NoSleep is OFF",
            SubTitle = IsActive ? GetStatusText() : "System sleep is not being blocked",
            Action = c => { ShowStatusNotification(); return true; },
            IcoPath = _iconPath
        });

        return results;
    }

    private List<Result> BuildExtendResult(List<Result> results, string input)
    {
        var parsed = ParseDuration(input);
        if (parsed.HasValue)
        {
            var extension = parsed.Value;
            results.Add(new Result
            {
                Title = $"Extend NoSleep by {FormatDurationLabel(extension)}",
                SubTitle = IsActive
                    ? GetStatusText()
                    : "NoSleep is off, so this starts a new timer",
                Action = c => { ExtendNoSleep(extension); return true; },
                IcoPath = _iconPath
            });
        }
        else
        {
            results.Add(new Result
            {
                Title = "Invalid extension",
                SubTitle = "Try +30m, +1h, or +1.5",
                Action = c => false,
                IcoPath = _iconPath
            });
        }

        return results;
    }

    private List<Result> BuildOffResult(List<Result> results)
    {
        if (IsActive)
        {
            results.Add(new Result
            {
                Title = "Turn off NoSleep",
                SubTitle = $"Currently active - {GetStatusText()}",
                Action = c => { StopNoSleep(); return true; },
                IcoPath = _iconPath
            });
        }
        else
        {
            results.Add(new Result
            {
                Title = "NoSleep is not active",
                SubTitle = "Nothing to turn off",
                Action = c => false,
                IcoPath = _iconPath
            });
        }

        return results;
    }

    private List<Result> BuildCustomDurationResult(List<Result> results, string input)
    {
        var parsed = ParseDuration(input);
        if (parsed.HasValue)
        {
            var duration = parsed.Value;
            results.Add(new Result
            {
                Title = $"Keep awake for {FormatDurationLabel(duration)}",
                SubTitle = IsActive ? "Will restart with new duration" : "",
                Action = c => { StartNoSleep(duration); return true; },
                IcoPath = _iconPath
            });
        }
        else if (TryParseUntil(input, out var untilDuration, out var targetTime))
        {
            results.Add(new Result
            {
                Title = $"Keep awake until {FormatClockTime(targetTime)}",
                SubTitle = $"For {FormatDurationLabel(untilDuration)}",
                Action = c => { StartNoSleep(untilDuration); return true; },
                IcoPath = _iconPath
            });
        }
        else
        {
            results.Add(new Result
            {
                Title = "Invalid duration",
                SubTitle = "Try 3, 45m, +30m, until 5pm, 17:30, off, s, or help",
                Action = c => false,
                IcoPath = _iconPath
            });
        }

        return results;
    }

    /// <summary>
    /// Parse user input into a TimeSpan. Returns null if input is invalid.
    /// Supports: "45m" (minutes), "3" or "3h" (hours), "1.5" (1.5 hours = 90 min).
    /// </summary>
    internal static TimeSpan? ParseDuration(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (input.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParsePositiveDouble(input.AsSpan(0, input.Length - 1), out var minutes)
                && minutes > 0 && minutes <= 1440)
            {
                var rounded = Math.Max(1, (int)Math.Ceiling(minutes));
                return TimeSpan.FromMinutes(rounded);
            }
            return null;
        }

        var numStr = input.EndsWith("h", StringComparison.OrdinalIgnoreCase)
            ? input.AsSpan(0, input.Length - 1)
            : input.AsSpan();

        if (TryParsePositiveDouble(numStr, out var hours) && hours > 0 && hours <= 24)
            return TimeSpan.FromHours(hours);

        return null;
    }

    private static bool TryParsePositiveDouble(ReadOnlySpan<char> input, out double value)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               || double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryParseDisplayModeCommand(string input, out bool keepDisplayAwake)
    {
        var normalized = input.Trim().ToLowerInvariant();
        keepDisplayAwake = false;

        if (normalized is "screen off" or "display off" or "monitor off")
            return true;

        if (normalized is "screen on" or "display on" or "monitor on")
        {
            keepDisplayAwake = true;
            return true;
        }

        return false;
    }

    internal static bool TryParseUntil(string input, out TimeSpan duration, out DateTime targetTime)
    {
        duration = default;
        targetTime = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var text = input.Trim();
        if (text.StartsWith("until ", StringComparison.OrdinalIgnoreCase))
        {
            text = text["until ".Length..].Trim();
        }
        else if (!LooksLikeClockInput(text))
        {
            return false;
        }

        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out targetTime)
            && !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out targetTime))
        {
            return false;
        }

        var now = DateTime.Now;
        if (targetTime <= now)
            targetTime = targetTime.AddDays(1);

        duration = targetTime - now;
        return duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(7);
    }

    private static bool LooksLikeClockInput(string input)
    {
        return input.Contains(':')
               || input.EndsWith("am", StringComparison.OrdinalIgnoreCase)
               || input.EndsWith("pm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Format a TimeSpan as a human-readable label. Example: "1 hour", "45 minutes", "2h 30m".
    /// </summary>
    internal static string FormatDurationLabel(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            var days = (int)duration.TotalDays;
            var hours = duration.Hours;
            if (hours == 0)
                return days == 1 ? "1 day" : $"{days} days";
            return $"{days}d {hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            if (minutes == 0)
                return hours == 1 ? "1 hour" : $"{hours} hours";
            return $"{hours}h {minutes}m";
        }
        var mins = (int)Math.Ceiling(duration.TotalMinutes);
        return mins == 1 ? "1 minute" : $"{mins} minutes";
    }

    private static string FormatClockTime(DateTime time)
    {
        return time.ToString("t", CultureInfo.CurrentCulture);
    }

    // Intentionally lock-free: called from the tray refresh thread while it holds
    // TrayIconManager's lock. Capture _timer once so a concurrent transition that nulls
    // the field mid-read can't throw a NullReferenceException.
    private string GetStatusText()
    {
        if (!IsActive) return "OFF";

        var displayMode = _activeKeepDisplayAwake ? "display on" : "display can sleep";
        var timer = _timer;
        if (timer == null)
            return $"indefinite, {displayMode}";

        return $"{timer.FormatRemainingTime()}, until {FormatClockTime(timer.ExpiresAt)}, {displayMode}";
    }

    private void ShowStatusNotification()
    {
        var status = IsActive ? $"On - {GetStatusText()}" : "Off";
        ShowNotification(status);
    }

    private void ShowNotification(string message)
    {
        if (_settings.SendNotifications)
            _context.API.ShowMsg("NoSleep", message, _iconPath);
    }

    /// <summary>
    /// Start nosleep, or change the active duration if already running.
    /// Pass null for indefinite mode.
    /// </summary>
    private bool StartNoSleep(TimeSpan? duration = null, bool? keepDisplayAwake = null)
    {
        lock (_stateLock)
        {
            if (!CanActivate(out var blockedReason))
            {
                ShowNotification($"Off - {blockedReason}");
                return false;
            }

            _activeKeepDisplayAwake = keepDisplayAwake ?? (IsActive ? _activeKeepDisplayAwake : _settings.KeepDisplayAwake);
            _activeDuration = duration;

            if (!IsActive)
            {
                PowerUtilities.PreventPowerSave(_activeKeepDisplayAwake);
                IsActive = true;

                if (duration.HasValue)
                {
                    _timer = new NoSleepTimer();
                    _timer.Expired += OnTimerExpired;
                    _timer.Start(duration.Value);
                }

                if (_settings.ShowTrayIcon)
                    TrayIconManager.ShowTray(_context, OnDurationSelected, SetDisplayMode, () => StopNoSleep(), GetStatusText);

                ShowNotification(duration.HasValue ? $"On for {FormatDurationLabel(duration.Value)}" : "On");
            }
            else
            {
                PowerUtilities.PreventPowerSave(_activeKeepDisplayAwake);

                if (duration.HasValue)
                {
                    if (_timer == null)
                    {
                        _timer = new NoSleepTimer();
                        _timer.Expired += OnTimerExpired;
                    }
                    _timer.Start(duration.Value);
                }
                else
                {
                    _timer?.Dispose();
                    _timer = null;
                }
                TrayIconManager.UpdateStatus();
                var switchText = duration.HasValue ? FormatDurationLabel(duration.Value) : "indefinite";
                ShowNotification(duration.HasValue ? $"On for {switchText}" : "On");
            }

            return true;
        }
    }

    private void ExtendNoSleep(TimeSpan extension)
    {
        lock (_stateLock)
        {
            if (!IsActive)
            {
                StartNoSleep(extension);
                return;
            }

            if (!CanActivate(out var blockedReason))
            {
                StopNoSleep(notificationMessage: $"Off - {blockedReason}");
                return;
            }

            if (_timer == null)
            {
                ShowNotification("Already on indefinitely");
                return;
            }

            var newDuration = _timer.GetRemainingTime() + extension;
            if (newDuration > TimeSpan.FromHours(24))
                newDuration = TimeSpan.FromHours(24);

            _activeDuration = newDuration;
            _timer.Start(newDuration);
            TrayIconManager.UpdateStatus();
            ShowNotification($"Extended until {FormatClockTime(_timer.ExpiresAt)}");
        }
    }

    private void SetDisplayMode(bool keepDisplayAwake)
    {
        lock (_stateLock)
        {
            _activeKeepDisplayAwake = keepDisplayAwake;

            if (IsActive)
            {
                PowerUtilities.PreventPowerSave(_activeKeepDisplayAwake);
                TrayIconManager.UpdateStatus();
                ShowNotification(keepDisplayAwake ? "Screen stays on" : "Screen can sleep");
            }
        }
    }

    private string GetDisplayModeText()
    {
        return _activeKeepDisplayAwake ? "display stays on" : "display can sleep";
    }

    private string GetBatteryStatusSuffix()
    {
        if (!IsRunningOnBattery())
            return "";

        return TryGetBatteryPercent(out var percent)
            ? $" - on battery ({percent}%)"
            : " - on battery";
    }

    private bool CanActivate(out string blockedReason)
    {
        blockedReason = "";

        if (_settings.PluggedInOnly && IsRunningOnBattery())
        {
            blockedReason = "plugged-in only mode is enabled";
            return false;
        }

        if (_settings.DisableOnLowBattery
            && IsRunningOnBattery()
            && TryGetBatteryPercent(out var percent)
            && percent <= GetLowBatteryThreshold())
        {
            blockedReason = $"battery is at {percent}%";
            return false;
        }

        return true;
    }

    private void EnforcePowerPolicy()
    {
        lock (_stateLock)
        {
            if (!IsActive)
                return;

            if (!CanActivate(out var blockedReason))
                StopNoSleep(notificationMessage: $"Off - {blockedReason}");
        }
    }

    /// <summary>
    /// Stop nosleep and clean up timer.
    /// </summary>
    private void StopNoSleep(bool timerExpired = false, string notificationMessage = null)
    {
        lock (_stateLock)
        {
            if (IsActive)
            {
                PowerUtilities.Shutdown();
                IsActive = false;
                _activeDuration = null;
                _activeKeepDisplayAwake = _settings.KeepDisplayAwake;

                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }

                if (_settings.ShowTrayIcon) TrayIconManager.HideTray();
                var message = notificationMessage
                              ?? (timerExpired ? "Timer expired" : "Off");
                ShowNotification(message);
            }
        }
    }

    private void OnTimerExpired(object sender, EventArgs e)
    {
        lock (_stateLock)
        {
            // Ignore a stale expiry. The Elapsed callback is queued on the thread pool,
            // so by the time it runs the user may have restarted the timer with a longer
            // duration, switched to indefinite (_timer == null), or already turned off.
            // Only stop if the active timer has genuinely reached zero.
            if (!IsActive || _timer == null || _timer.GetRemainingTime() > TimeSpan.Zero)
                return;

            StopNoSleep(timerExpired: true);
        }
    }

    private void OnDurationSelected(TimeSpan? duration)
    {
        StartNoSleep(duration);
    }

    private void SubscribeSystemEvents()
    {
        if (_systemEventsSubscribed)
            return;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _systemEventsSubscribed = true;
    }

    private void UnsubscribeSystemEvents()
    {
        if (!_systemEventsSubscribed)
            return;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _systemEventsSubscribed = false;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.StatusChange or PowerModes.Resume)
            EnforcePowerPolicy();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.StopOnSessionLock)
            StopNoSleep(notificationMessage: "Off - Windows locked");
    }

    private static bool IsRunningOnBattery()
    {
        try
        {
            return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetBatteryPercent(out int percent)
    {
        percent = 0;

        try
        {
            var batteryLifePercent = SystemInformation.PowerStatus.BatteryLifePercent;
            if (batteryLifePercent < 0 || batteryLifePercent > 1)
                return false;

            percent = (int)Math.Round(batteryLifePercent * 100);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int GetLowBatteryThreshold()
    {
        return Math.Clamp(_settings.LowBatteryThresholdPercent, 5, 95);
    }

    /// <summary>
    /// Called by PluginSettings when the Show Tray Icon checkbox changes while nosleep is active.
    /// </summary>
    internal void ShowTrayIfActive()
    {
        lock (_stateLock)
        {
            if (IsActive && _settings.ShowTrayIcon)
                TrayIconManager.ShowTray(_context, OnDurationSelected, SetDisplayMode, () => StopNoSleep(), GetStatusText);
        }
    }

    internal void ApplySettings()
    {
        lock (_stateLock)
        {
            if (!IsActive)
                return;

            EnforcePowerPolicy();
            if (!IsActive)
                return;

            _activeKeepDisplayAwake = _settings.KeepDisplayAwake;
            PowerUtilities.PreventPowerSave(_activeKeepDisplayAwake);

            if (_settings.ShowTrayIcon)
                ShowTrayIfActive();
            else
                TrayIconManager.HideTray();

            TrayIconManager.UpdateStatus();
        }
    }

    /// <summary>
    /// Create the settings panel for the plugin
    /// </summary>
    public System.Windows.Controls.Control CreateSettingPanel()
    {
        return new PluginSettings(this, _context, _settings);
    }

    /// <summary>
    /// Dispose of resources when the plugin is unloaded
    /// </summary>
    public void Dispose()
    {
        UnsubscribeSystemEvents();
        StopNoSleep();
    }
}
