using System;
using System.Windows.Forms;
using Timer = System.Timers.Timer;

namespace Flow.Launcher.Plugin.NoSleep.Tray;

/// <summary>
/// Manages the system tray icon for the NoSleep plugin.
/// </summary>
public static class TrayIconManager
{
    private static TrayIconThreadManager _threadManager;
    private static readonly object _lock = new();
    private static volatile bool _isVisible = false;

    private static NotifyIcon _notifyIcon;
    private static ToolStripMenuItem _statusItem;
    private static Func<string> _getStatusText;
    private static Timer _refreshTimer;
    private static string _lastStatusText;

    /// <summary>
    /// Shows the nosleep tray icon in a separate background thread.
    /// </summary>
    /// <param name="context">The plugin initialization context</param>
    /// <param name="onDurationSelected">Callback when a duration preset is clicked</param>
    /// <param name="onDisplayModeSelected">Callback when a display mode is clicked</param>
    /// <param name="onTurnOff">Callback when Turn Off is clicked</param>
    /// <param name="getStatusText">Returns current status text (e.g. "1h 23m remaining" or "Indefinite")</param>
    public static void ShowTray(
        PluginInitContext context,
        Action<TimeSpan?> onDurationSelected,
        Action<bool> onDisplayModeSelected,
        Action onTurnOff,
        Func<string> getStatusText)
    {
        lock (_lock)
        {
            if (_isVisible) return;

            _isVisible = true;
            _getStatusText = getStatusText;
            _threadManager = new TrayIconThreadManager();

            var (notifyIcon, statusItem) = TrayIconFactory.CreateNoSleepIcon(
                context,
                onDurationSelected,
                onDisplayModeSelected,
                onTurnOff);
            _notifyIcon = notifyIcon;
            _statusItem = statusItem;

            RefreshStatusText();
            _threadManager.StartTrayThread(notifyIcon);
            StartRefreshTimer();
        }
    }

    /// <summary>
    /// Hides the nosleep tray icon and stops the thread.
    /// </summary>
    public static void HideTray()
    {
        lock (_lock)
        {
            if (!_isVisible) return;

            _isVisible = false;
            StopRefreshTimer();
            _threadManager?.Dispose();
            _threadManager = null;
            _notifyIcon = null;
            _statusItem = null;
            _getStatusText = null;
            _lastStatusText = null;
        }
    }

    /// <summary>
    /// Immediately refresh the tooltip and status menu item text.
    /// Call this when the duration changes so the user sees the update right away.
    /// </summary>
    public static void UpdateStatus()
    {
        lock (_lock)
        {
            if (!_isVisible) return;
            RefreshStatusText();
        }
    }

    private static void StartRefreshTimer()
    {
        _refreshTimer = new Timer(60_000);
        _refreshTimer.Elapsed += (s, e) => UpdateStatus();
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();
    }

    private static void StopRefreshTimer()
    {
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _refreshTimer = null;
        }
    }

    /// <summary>
    /// Updates the tooltip and status menu item by dispatching to the tray's STA thread.
    /// </summary>
    private static void RefreshStatusText()
    {
        var statusText = _getStatusText?.Invoke();
        if (statusText == null || _notifyIcon == null) return;

        // Nothing changed since the last refresh (common in indefinite mode, where the
        // status is constant). Skip the cross-thread Invoke marshal entirely.
        if (statusText == _lastStatusText) return;
        _lastStatusText = statusText;

        var menuText = $"NoSleep - {statusText}";
        var tooltipText = TrimNotifyIconText(menuText);

        try
        {
            if (_notifyIcon.ContextMenuStrip?.IsHandleCreated == true)
            {
                _notifyIcon.ContextMenuStrip.Invoke(new Action(() =>
                {
                    _notifyIcon.Text = tooltipText;
                    if (_statusItem != null)
                        _statusItem.Text = menuText;
                }));
            }
            else
            {
                _notifyIcon.Text = tooltipText;
                if (_statusItem != null)
                    _statusItem.Text = menuText;
            }
        }
        catch
        {
            // Icon may be disposing; safe to ignore
        }
    }

    private static string TrimNotifyIconText(string text)
    {
        return text.Length <= 63 ? text : text[..60] + "...";
    }
}
