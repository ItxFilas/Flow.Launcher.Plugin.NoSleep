using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Flow.Launcher.Plugin.NoSleep.Utilities;

/// <summary>
/// Utilities for preventing the system from going to sleep
/// </summary>
public static class PowerUtilities
{
    /// <summary>
    /// Execution state flags for preventing power save mode
    /// </summary>
    [Flags]
    public enum EXECUTION_STATE : uint
    {
        /// <summary>
        /// Away mode required
        /// </summary>
        ES_AWAYMODE_REQUIRED = 0x00000040,
        /// <summary>
        /// Continuous execution state
        /// </summary>
        ES_CONTINUOUS = 0x80000000,
        /// <summary>
        /// Display required to stay on
        /// </summary>
        ES_DISPLAY_REQUIRED = 0x00000002,
        /// <summary>
        /// System required to stay awake
        /// </summary>
        ES_SYSTEM_REQUIRED = 0x00000001
        // Legacy flag, should not be used.
        // ES_USER_PRESENT = 0x00000004
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern uint SetThreadExecutionState(EXECUTION_STATE esFlags);

    private static readonly object Lock = new();
    private static ManualResetEvent _stopEvent;
    private static Thread _powerThread;
    private static bool _keepDisplayAwake;

    /// <summary>
    /// Prevent the system from entering power save mode
    /// </summary>
    public static void PreventPowerSave(bool keepDisplayAwake)
    {
        lock (Lock)
        {
            if (_powerThread?.IsAlive == true && _keepDisplayAwake == keepDisplayAwake)
                return;

            ShutdownLocked();

            _keepDisplayAwake = keepDisplayAwake;
            _stopEvent = new ManualResetEvent(false);
            var stopEvent = _stopEvent;
            _powerThread = new Thread(() => HoldExecutionState(keepDisplayAwake, stopEvent))
            {
                IsBackground = true,
                Name = "NoSleepPowerRequestThread"
            };
            _powerThread.Start();
        }
    }

    private static void HoldExecutionState(bool keepDisplayAwake, WaitHandle stopEvent)
    {
        var flags = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED;
        if (keepDisplayAwake)
            flags |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;

        try
        {
            SetThreadExecutionState(flags);
            stopEvent.WaitOne();
        }
        finally
        {
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
    }

    /// <summary>
    /// Allow the system to enter power save mode again
    /// </summary>
    public static void Shutdown()
    {
        lock (Lock)
        {
            ShutdownLocked();
        }
    }

    private static void ShutdownLocked()
    {
        if (_powerThread == null)
            return;

        _stopEvent?.Set();
        _powerThread.Join(500);
        _stopEvent?.Dispose();
        _stopEvent = null;
        _powerThread = null;
    }
}
