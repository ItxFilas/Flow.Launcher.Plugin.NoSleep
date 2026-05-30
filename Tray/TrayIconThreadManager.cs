using System;
using System.Threading;
using System.Windows.Forms;

namespace Flow.Launcher.Plugin.NoSleep.Tray;

/// <summary>
/// Handles the threading aspects of the tray icon management.
/// </summary>
internal class TrayIconThreadManager : IDisposable
{
    private Thread _trayThread;
    private NotifyIcon _notifyIcon;
    private readonly object _lock = new();
    private volatile bool _isRunning = false;

    /// <summary>
    /// Starts the tray icon in a separate background thread.
    /// </summary>
    /// <param name="notifyIcon">The NotifyIcon to display</param>
    public void StartTrayThread(NotifyIcon notifyIcon)
    {
        lock (_lock)
        {
            if (_isRunning) return;

            _notifyIcon = notifyIcon;
            _isRunning = true;

            _trayThread = new Thread(RunTrayMessageLoop)
            {
                IsBackground = true,
                Name = "NoSleepTrayIconThread"
            };

            _trayThread.SetApartmentState(ApartmentState.STA);
            _trayThread.Start();
        }
    }

    /// <summary>
    /// Stops the tray icon thread and cleans up resources.
    /// </summary>
    public void StopTrayThread()
    {
        lock (_lock)
        {
            if (!_isRunning) return;

            _isRunning = false;
            CleanupTrayIcon();
            WaitForThreadToExit();
        }
    }

    /// <summary>
    /// Runs the Windows message loop for the tray icon.
    /// </summary>
    private void RunTrayMessageLoop()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = true;
            Application.Run(); // Run message pump
        }
    }

    /// <summary>
    /// Cleans up the tray icon safely.
    /// </summary>
    private void CleanupTrayIcon()
    {
        if (_notifyIcon == null) return;

        try
        {
            if (_notifyIcon.ContextMenuStrip != null)
            {
                _notifyIcon.ContextMenuStrip.Invoke(new Action(() =>
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                    Application.ExitThread();
                }));
            }
            else
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
        catch
        {
            // Force cleanup if invoke fails
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            catch { }
        }
    }

    /// <summary>
    /// Waits for the tray thread to exit with timeout.
    /// </summary>
    private void WaitForThreadToExit()
    {
        _trayThread?.Join(200);
        _trayThread = null;
    }

    /// <summary>
    /// Disposes of the thread manager and cleans up resources.
    /// </summary>
    public void Dispose()
    {
        StopTrayThread();
    }
}
