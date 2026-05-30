using System.Windows.Controls;

namespace Flow.Launcher.Plugin.NoSleep.Settings;

public partial class PluginSettings : UserControl
{
    private readonly NoSleep _nosleep;
    private readonly PluginInitContext _context;
    private readonly Settings _settings;
    private bool _isLoading;

    /// <summary>
    /// Initialize the plugin settings UI
    /// </summary>
    /// <param name="nosleep">The plugin instance</param>
    /// <param name="context">Plugin context</param>
    /// <param name="settings">Plugin settings</param>
    public PluginSettings(NoSleep nosleep, PluginInitContext context, Settings settings)
    {
        InitializeComponent();
        _nosleep = nosleep;
        _context = context;
        _settings = settings;
        LoadSettings();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        StartWithFlowLauncherCheckBox.IsChecked = _settings.StartWithFlowLauncher;
        SendNotificationsCheckBox.IsChecked = _settings.SendNotifications;
        ShowTrayIconCheckBox.IsChecked = _settings.ShowTrayIcon;
        KeepDisplayAwakeCheckBox.IsChecked = _settings.KeepDisplayAwake;
        DisableOnLowBatteryCheckBox.IsChecked = _settings.DisableOnLowBattery;
        LowBatteryThresholdTextBox.Text = _settings.LowBatteryThresholdPercent.ToString();
        PluggedInOnlyCheckBox.IsChecked = _settings.PluggedInOnly;
        StopOnSessionLockCheckBox.IsChecked = _settings.StopOnSessionLock;
        _isLoading = false;
    }

    private void StartWithFlowLauncherCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void SendNotificationsCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void ShowTrayIconCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void KeepDisplayAwakeCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void DisableOnLowBatteryCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void LowBatteryThresholdTextBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void PluggedInOnlyCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void StopOnSessionLockCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_isLoading)
            SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.StartWithFlowLauncher = StartWithFlowLauncherCheckBox.IsChecked ?? false;
        _settings.SendNotifications = SendNotificationsCheckBox.IsChecked ?? true;
        _settings.ShowTrayIcon = ShowTrayIconCheckBox.IsChecked ?? true;
        _settings.KeepDisplayAwake = KeepDisplayAwakeCheckBox.IsChecked ?? true;
        _settings.DisableOnLowBattery = DisableOnLowBatteryCheckBox.IsChecked ?? true;
        _settings.LowBatteryThresholdPercent = ReadLowBatteryThreshold();
        _settings.PluggedInOnly = PluggedInOnlyCheckBox.IsChecked ?? false;
        _settings.StopOnSessionLock = StopOnSessionLockCheckBox.IsChecked ?? false;

        _context.API.SaveSettingJsonStorage<Settings>();

        _nosleep.ApplySettings();
    }

    private int ReadLowBatteryThreshold()
    {
        if (int.TryParse(LowBatteryThresholdTextBox.Text, out var threshold))
            return System.Math.Clamp(threshold, 5, 95);

        return System.Math.Clamp(_settings.LowBatteryThresholdPercent, 5, 95);
    }
}
