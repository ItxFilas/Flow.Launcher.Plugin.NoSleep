using System;

namespace Flow.Launcher.Plugin.NoSleep.Settings;

/// <summary>
/// Plugin settings configuration
/// </summary>
public class Settings
{
    /// <summary>
    /// Whether to start nosleep service automatically when Flow Launcher starts
    /// </summary>
    public bool StartWithFlowLauncher { get; set; } = false;

    /// <summary>
    /// Whether to send notifications when nosleep starts/stops
    /// </summary>
    public bool SendNotifications { get; set; } = true;

    /// <summary>
    /// Whether to show tray icon when nosleep is active
    /// </summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Whether nosleep should keep the display awake in addition to the system.
    /// </summary>
    public bool KeepDisplayAwake { get; set; } = true;

    /// <summary>
    /// Whether nosleep should refuse to start or auto-stop when the battery is low.
    /// </summary>
    public bool DisableOnLowBattery { get; set; } = true;

    /// <summary>
    /// Battery percentage at or below which nosleep should stay off while on battery.
    /// </summary>
    public int LowBatteryThresholdPercent { get; set; } = 25;

    /// <summary>
    /// Whether nosleep should only run while the device is plugged in.
    /// </summary>
    public bool PluggedInOnly { get; set; } = false;

    /// <summary>
    /// Whether nosleep should turn off when the Windows session locks.
    /// </summary>
    public bool StopOnSessionLock { get; set; } = false;
}
