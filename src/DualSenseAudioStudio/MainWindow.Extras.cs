using System.IO;
using System.Windows;
using System.Windows.Media;
using DualSenseAudioStudio.Models;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DualSenseAudioStudio;

// Tray icon, start with Windows, the power (Start/Stop) button, saved toggles,
// and battery alerts / battery-colored light bar.
public partial class MainWindow
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "DualSenseStudio";

    private static readonly string PrefsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualSenseAudioStudio", "prefs.txt");

    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayPower;
    private bool _exiting;
    private bool _paused;
    private int _lastBatteryAlert = 100;
    private string? _batteryLightHex;

    private static bool StartedFromTray =>
        Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

    // Fit the window inside the screen's usable area (small or high-scaling displays).
    private void FitToScreen()
    {
        Rect area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 24));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    private void InitializeExtras()
    {
        FitToScreen();
        LoadPrefs();
        ApplyLowLatencyTimer();
        StartupCheckBox.IsChecked = IsStartupEnabled();
        BatteryLightCheckBox.Checked += (_, _) => SavePrefs();
        BatteryLightCheckBox.Unchecked += (_, _) => SavePrefs();

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "DualSense Studio",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open DualSense Studio", null, (_, _) => ShowFromTray());
        _trayPower = new Forms.ToolStripMenuItem("Stop", null, (_, _) => Dispatcher.Invoke(TogglePower));
        _tray.ContextMenuStrip.Items.Add(_trayPower);
        _tray.ContextMenuStrip.Items.Add("Check for updates", null, (_, _) => Dispatcher.Invoke(() => _ = CheckForUpdateOnceAsync(manual: true)));
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.DoubleClick += (_, _) => ShowFromTray();

        if (StartedFromTray)
        {
            Hide();
        }

        _ = CheckForUpdateAsync();
    }

    private Services.UpdateChecker.UpdateInfo? _update;

    // Auto-update: check GitHub shortly after start and every 6 hours, then show a button + tray note.
    private async Task CheckForUpdateAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        while (!_exiting)
        {
            if (await CheckForUpdateOnceAsync(manual: false))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private async Task<bool> CheckForUpdateOnceAsync(bool manual)
    {
        _update = await Services.UpdateChecker.CheckAsync();
        if (_update is null)
        {
            if (manual)
            {
                _tray?.ShowBalloonTip(2500, "DualSense Studio", $"You're up to date (v{Services.UpdateChecker.CurrentVersion.ToString(3)}).", Forms.ToolTipIcon.Info);
            }
            return false;
        }

        UpdateButtonText.Text = $"Update to v{_update.Version}";
        UpdateButton.Visibility = Visibility.Visible;
        AppendLog($"Update available: v{_update.Version}.");
        _tray?.ShowBalloonTip(4000, "DualSense Studio update", $"Version {_update.Version} is available. Open the app and click Update.", Forms.ToolTipIcon.Info);
        return true;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null)
        {
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateButtonText.Text = "Downloading…";
        try
        {
            await Services.UpdateChecker.InstallAsync(_update);
            ExitApp();
        }
        catch (Exception ex)
        {
            AppendLog($"Update failed: {ex.Message}");
            UpdateButtonText.Text = "Update failed – retry";
            UpdateButton.IsEnabled = true;
        }
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp()
    {
        _exiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Close();
    }

    // Close button hides to the tray; real exit is from the tray menu.
    private bool HideToTrayInsteadOfClosing()
    {
        if (_exiting || _tray is null)
        {
            return false;
        }

        Hide();
        _tray.ShowBalloonTip(2500, "DualSense Studio", "Still running in the tray. Right-click the icon to stop or exit.", Forms.ToolTipIcon.Info);
        return true;
    }

    private void PowerButton_Click(object sender, RoutedEventArgs e) => TogglePower();

    // Stop: ends audio, mic and virtual controllers so nothing runs in the background.
    private async void TogglePower()
    {
        _paused = !_paused;
        _userStoppedAudio = _paused;
        if (_paused)
        {
            RemoveAllGamepads();
            await StopEngineAsync();
            AppendLog("Stopped: audio, mic and game controllers are off.");
        }
        else
        {
            _nextAutoStart = DateTime.MinValue;
            UpdateGamepadBridge();
            AppendLog("Started.");
        }

        PowerText.Text = _paused ? "Stopped · Start" : "Running · Stop";
        PowerDot.Fill = _paused ? Brushes.Gray : (Brush)FindResource("SuccessBrush");
        if (_trayPower is not null)
        {
            _trayPower.Text = _paused ? "Start" : "Stop";
        }
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartupCheckBox.IsChecked == true)
            {
                key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            }
            else
            {
                key.DeleteValue(RunName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Start with Windows failed: {ex.Message}");
        }
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) is not null;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private bool _fastTimer;

    // Low latency: 1 ms Windows timer so controller input and UI timers wake on time.
    private void ApplyLowLatencyTimer()
    {
        // Toggles fire while the window is still loading, before this checkbox exists.
        if (LowLatencyCheckBox is null) return;
        bool want = LowLatencyCheckBox.IsChecked == true;
        if (want == _fastTimer) return;
        if (want) timeBeginPeriod(1); else timeEndPeriod(1);
        _fastTimer = want;
    }

    private void MicOption_Changed(object sender, RoutedEventArgs e)
    {
        ApplyLowLatencyTimer();
        if (!_initialized)
        {
            return;
        }

        SavePrefs();
        RequestRestart();
    }

    private void LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefsFile))
            {
                return;
            }

            var prefs = File.ReadAllLines(PrefsFile)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1] == "1");
            NoiseRemovalCheckBox.IsChecked = prefs.GetValueOrDefault("noise", true);
            SidetoneCheckBox.IsChecked = prefs.GetValueOrDefault("sidetone", false);
            HapticsCheckBox.IsChecked = prefs.GetValueOrDefault("haptics", false);
            BatteryLightCheckBox.IsChecked = prefs.GetValueOrDefault("batterylight", false);
            LowLatencyCheckBox.IsChecked = prefs.GetValueOrDefault("lowlatency", false);
        }
        catch
        {
        }
    }

    private void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsFile)!);
            File.WriteAllLines(PrefsFile, new[]
            {
                $"noise={(NoiseRemovalCheckBox.IsChecked == true ? 1 : 0)}",
                $"sidetone={(SidetoneCheckBox.IsChecked == true ? 1 : 0)}",
                $"haptics={(HapticsCheckBox.IsChecked == true ? 1 : 0)}",
                $"batterylight={(BatteryLightCheckBox.IsChecked == true ? 1 : 0)}",
                $"lowlatency={(LowLatencyCheckBox.IsChecked == true ? 1 : 0)}"
            });
        }
        catch
        {
        }
    }

    // Called every second: low-battery alerts and the battery-colored light bar.
    private async Task UpdateBatteryAsync()
    {
        int? level = _gamepad?.BatteryLevel;
        if (level is null)
        {
            return;
        }

        int percent = Math.Min(100, level.Value * 10 + 5);
        bool charging = _gamepad!.Charging;

        foreach (int threshold in new[] { 20, 10 })
        {
            if (percent <= threshold && _lastBatteryAlert > threshold && !charging)
            {
                _tray?.ShowBalloonTip(4000, "DualSense battery low", $"Controller battery is at {percent}%. Plug it in soon.", Forms.ToolTipIcon.Warning);
            }
        }
        _lastBatteryAlert = charging ? 100 : percent;

        if (BatteryLightCheckBox.IsChecked != true)
        {
            _batteryLightHex = null;
            return;
        }

        string hex = percent > 50 ? "2EE07A" : percent > 20 ? "FFC23D" : "FF3B4A";
        if (hex == _batteryLightHex)
        {
            return;
        }

        _batteryLightHex = hex;
        _synchronizing = true;
        RedSlider.Value = Convert.ToByte(hex[..2], 16);
        GreenSlider.Value = Convert.ToByte(hex[2..4], 16);
        BlueSlider.Value = Convert.ToByte(hex[4..], 16);
        _synchronizing = false;
        AppLightingControlCheckBox.IsChecked = true;
        await WriteCurrentControlAsync(applyLighting: true);
    }
}
