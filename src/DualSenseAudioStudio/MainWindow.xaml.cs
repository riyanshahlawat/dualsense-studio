using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DualSenseAudioStudio.Models;
using DualSenseAudioStudio.Services;
using NAudio.CoreAudioApi;

namespace DualSenseAudioStudio;

public partial class MainWindow : Window
{
    private static readonly Regex StatisticsPattern = new(
        @"Produced:\s*(?<reports>\d+).*?Queue:\s*(?<queue>\d+)/10.*?PCM buffer:\s*(?<buffer>[\d,.]+)\s*ms",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly EngineProcessService _engine = new();
    private readonly ControlFileService _controlFile = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly DispatcherTimer _controlDebounce;

    private bool _initialized;
    private bool _synchronizing;
    private bool _allowClose;
    private bool _controllerConfirmed;
    private bool _lightingDirty;
    private int _preferredDeviceIndex = -1;
    private EngineMode _currentMode = EngineMode.Loopback;
    private string _appliedLedHex = "627CFF";
    private int _appliedLedBrightness;
    private int _appliedPlayerLeds = 4;
    private bool _appliedLightbarEnabled = true;

    public MainWindow()
    {
        InitializeComponent();

        _controlDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _controlDebounce.Tick += ControlDebounce_Tick;

        _engine.OutputReceived += Engine_OutputReceived;
        _engine.ProcessExited += Engine_ProcessExited;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppSettings settings = _settingsStore.Load();
        ApplySettings(settings);
        _preferredDeviceIndex = settings.DeviceIndex;
        _initialized = true;
        SetLightingDirty(false);
        UpdateLightingOwnershipInterface();

        UpdateColorPreview();
        UpdatePlayerLedPreview();
        await RefreshAudioDevicesAsync();
        await WriteCurrentControlAsync();
        ShowPage("Dashboard");
    }

    private void ApplySettings(AppSettings settings)
    {
        _synchronizing = true;

        DashboardHeadsetRadio.IsChecked = settings.UseHeadset;
        DashboardSpeakerRadio.IsChecked = !settings.UseHeadset;
        HeadsetRadio.IsChecked = settings.UseHeadset;
        SpeakerRadio.IsChecked = !settings.UseHeadset;

        DashboardVolumeSlider.Value = settings.Volume;
        VolumeSlider.Value = settings.Volume;

        SelectComboItemByTag(ProtocolCombo, settings.Protocol);
        BufferLengthSlider.Value = settings.BufferLength;
        PeriodTextBox.Text = settings.PeriodMs.ToString("0.######", CultureInfo.CurrentCulture);

        ToneFrequencyTextBox.Text = settings.ToneFrequency.ToString(CultureInfo.CurrentCulture);
        ToneSecondsTextBox.Text = settings.ToneSeconds.ToString(CultureInfo.CurrentCulture);
        ToneAmplitudeTextBox.Text = settings.ToneAmplitude.ToString("0.##", CultureInfo.CurrentCulture);

        HexTextBox.Text = "#" + settings.LedHex.TrimStart('#').ToUpperInvariant();
        if (TryParseHex(settings.LedHex, out Color color))
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
        }

        SelectComboItemByTag(BrightnessCombo, settings.LedBrightness.ToString(CultureInfo.InvariantCulture));
        SelectComboItemByTag(PlayerLedsCombo, settings.PlayerLeds.ToString(CultureInfo.InvariantCulture));
        LedEnabledCheckBox.IsChecked = settings.LightbarEnabled;
        AppLightingControlCheckBox.IsChecked = settings.AppControlsLighting;

        _appliedLedHex = settings.LedHex.TrimStart('#').ToUpperInvariant();
        _appliedLedBrightness = settings.LedBrightness;
        _appliedPlayerLeds = settings.PlayerLeds;
        _appliedLightbarEnabled = settings.LightbarEnabled;

        _synchronizing = false;
        UpdateVolumeLabels(settings.Volume);
        BufferLengthValueText.Text = settings.BufferLength.ToString(CultureInfo.CurrentCulture);
    }

    private async Task RefreshAudioDevicesAsync()
    {
        SetStatus("Searching for outputs", StatusTone.Neutral);

        try
        {
            IReadOnlyList<AudioDeviceItem> devices = await Task.Run(EnumerateAudioDevices);

            _synchronizing = true;
            AudioDeviceCombo.ItemsSource = devices;
            DashboardDeviceCombo.ItemsSource = devices;

            AudioDeviceItem? selection = devices.FirstOrDefault(
                item => item.Index == _preferredDeviceIndex);
            selection ??= devices.FirstOrDefault(item => item.IsDefault);
            selection ??= devices.FirstOrDefault();

            AudioDeviceCombo.SelectedItem = selection;
            DashboardDeviceCombo.SelectedItem = selection;
            _synchronizing = false;

            SetStatus(
                devices.Count == 0 ? "No active output" : "Ready to start",
                devices.Count == 0 ? StatusTone.Error : StatusTone.Ready);
        }
        catch (Exception ex)
        {
            _synchronizing = false;
            AppendLog($"Failed to list Windows outputs: {ex.Message}");
            SetStatus("Failed to list audio", StatusTone.Error);
        }
    }

    private static IReadOnlyList<AudioDeviceItem> EnumerateAudioDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;

        try
        {
            using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch
        {
        }

        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render,
            DeviceState.Active);

        var result = new List<AudioDeviceItem>(devices.Count);

        for (int index = 0; index < devices.Count; index++)
        {
            using MMDevice device = devices[index];
            result.Add(new AudioDeviceItem(
                index,
                device.FriendlyName,
                string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    private async Task StartEngineAsync(EngineMode mode)
    {
        if (_engine.IsRunning)
        {
            MessageBox.Show(
                this,
                "End the current session before starting another test.",
                "DualSense Audio Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            AppSettings settings = WithAppliedLighting(ReadSettingsFromInterface());
            _settingsStore.Save(settings);
            await _controlFile.WriteAsync(settings);

            _currentMode = mode;
            var options = new EngineLaunchOptions
            {
                Mode = mode,
                DeviceIndex = settings.DeviceIndex >= 0
                    ? settings.DeviceIndex
                    : null,
                UseHeadset = settings.UseHeadset,
                Volume = settings.Volume,
                Full39 = settings.Protocol == "Full39",
                BufferLength = settings.BufferLength,
                PeriodMs = settings.PeriodMs,
                ToneFrequency = settings.ToneFrequency,
                ToneSeconds = settings.ToneSeconds,
                ToneAmplitude = settings.ToneAmplitude,
                LedHex = settings.LedHex,
                LedBrightness = settings.LedBrightness,
                PlayerLeds = settings.PlayerLeds,
                LightbarEnabled = settings.LightbarEnabled,
                AppControlsLighting = settings.AppControlsLighting,
                ControlFile = _controlFile.FilePath
            };

            AppendLog(string.Empty);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Starting {ModeName(mode)}...");
            SetRunningInterface(true);
            SetStatus("Opening DualSense", StatusTone.Working);
            await _engine.StartAsync(options);
        }
        catch (Exception ex)
        {
            SetRunningInterface(false);
            SetStatus("Unable to start", StatusTone.Error);
            AppendLog($"Failed to start: {ex.Message}");

            MessageBox.Show(
                this,
                ex.Message,
                "Unable to start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task StopEngineAsync()
    {
        if (!_engine.IsRunning)
        {
            return;
        }

        try
        {
            AppSettings settings = WithAppliedLighting(ReadSettingsFromInterface());
            SetStatus("Stopping session", StatusTone.Working);
            await _controlFile.WriteAsync(settings, stopRequested: true);
            await _engine.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            AppendLog($"Shutdown failed: {ex.Message}");
        }
    }

    private AppSettings ReadSettingsFromInterface()
    {
        string hex = NormalizeHex(HexTextBox.Text);

        int frequency = ParseInt(ToneFrequencyTextBox.Text, 20, 20000, "Frequency");
        int seconds = ParseInt(ToneSecondsTextBox.Text, 1, 30, "Duration");
        double amplitude = ParseDouble(ToneAmplitudeTextBox.Text, 0.001, 0.25, "Amplitude");
        double period = ParseDouble(PeriodTextBox.Text, 5, 40, "Period");

        AudioDeviceItem? selected = AudioDeviceCombo.SelectedItem as AudioDeviceItem
                                    ?? DashboardDeviceCombo.SelectedItem as AudioDeviceItem;

        return new AppSettings
        {
            DeviceIndex = selected?.Index ?? -1,
            UseHeadset = HeadsetRadio.IsChecked == true,
            Volume = (int)Math.Round(VolumeSlider.Value),
            Protocol = SelectedTag(ProtocolCombo, "Full39"),
            BufferLength = (int)Math.Round(BufferLengthSlider.Value),
            PeriodMs = period,
            ToneFrequency = frequency,
            ToneSeconds = seconds,
            ToneAmplitude = amplitude,
            LedHex = hex,
            LedBrightness = int.Parse(
                SelectedTag(BrightnessCombo, "0"),
                CultureInfo.InvariantCulture),
            PlayerLeds = int.Parse(
                SelectedTag(PlayerLedsCombo, "4"),
                CultureInfo.InvariantCulture),
            LightbarEnabled = LedEnabledCheckBox.IsChecked == true,
            AppControlsLighting = AppLightingControlCheckBox.IsChecked == true
        };
    }

    private AppSettings WithAppliedLighting(AppSettings settings)
    {
        return settings with
        {
            LedHex = _appliedLedHex,
            LedBrightness = _appliedLedBrightness,
            PlayerLeds = _appliedPlayerLeds,
            LightbarEnabled = _appliedLightbarEnabled
        };
    }

    private async Task<bool> WriteCurrentControlAsync(bool applyLighting = false)
    {
        if (!_initialized)
        {
            return false;
        }

        try
        {
            AppSettings interfaceSettings = ReadSettingsFromInterface();
            AppSettings settings = applyLighting
                ? interfaceSettings
                : WithAppliedLighting(interfaceSettings);
            _settingsStore.Save(settings);
            await _controlFile.WriteAsync(settings);

            if (applyLighting)
            {
                _appliedLedHex = settings.LedHex;
                _appliedLedBrightness = settings.LedBrightness;
                _appliedPlayerLeds = settings.PlayerLeds;
                _appliedLightbarEnabled = settings.LightbarEnabled;
                SetLightingDirty(false);
                AppendLog($"Lighting applied: #{settings.LedHex}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Setting ignored: {ex.Message}");
            return false;
        }
    }

    private void MarkLightingDirty()
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        SetLightingDirty(true);
    }

    private void SetLightingDirty(bool dirty)
    {
        _lightingDirty = dirty;
        bool appControlsLighting = AppLightingControlCheckBox.IsChecked == true;

        if (!appControlsLighting)
        {
            ApplyLightingButton.IsEnabled = false;
            ApplyLightingButtonText.Text = "Lighting controlled by DSX";
            LightingApplyHint.Text = "Enable the option above to use this editor.";
            LightingApplyHint.Foreground = new SolidColorBrush(Color.FromRgb(141, 177, 170));
            return;
        }

        ApplyLightingButton.IsEnabled = dirty;
        ApplyLightingButtonText.Text = dirty
            ? "Apply lighting"
            : "Lighting applied";
        LightingApplyHint.Text = dirty
            ? "Changes are preview-only. Click to send them to the DualSense."
            : "The displayed configuration is applied to the DualSense.";
        LightingApplyHint.Foreground = dirty
            ? new SolidColorBrush(Color.FromRgb(177, 188, 255))
            : (Brush)FindResource("MutedBrush");
    }

    private void UpdateLightingOwnershipInterface()
    {
        bool enabled = AppLightingControlCheckBox.IsChecked == true;
        LightingEditorPanel.IsEnabled = enabled;
        LedEnabledCheckBox.IsEnabled = enabled;
        LightingOwnershipStateText.Text = enabled
            ? "Enabled. This app will send the color, brightness, and Player LEDs."
            : "Disabled. DSX will continue controlling the lighting.";
        LightingOwnershipStateText.Foreground = enabled
            ? (Brush)FindResource("AccentBrush")
            : new SolidColorBrush(Color.FromRgb(141, 177, 170));
        SetLightingDirty(_lightingDirty);
    }

    private void ScheduleControlUpdate()
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        _controlDebounce.Stop();
        _controlDebounce.Start();
    }

    private async void ControlDebounce_Tick(object? sender, EventArgs e)
    {
        _controlDebounce.Stop();
        await WriteCurrentControlAsync();
    }

    private void Engine_OutputReceived(string line)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppendLog(line);

            if (line.Contains("HID controller opened", StringComparison.OrdinalIgnoreCase))
            {
                _controllerConfirmed = true;
                SetControllerState(true);
                SetStatus("DualSense connected", StatusTone.Ready);
            }
            else if (line.StartsWith("Capturing:", StringComparison.OrdinalIgnoreCase))
            {
                HeroSubtitle.Text = line;
                SetStatus("Audio running", StatusTone.Ready);
            }
            else if (
                line.Contains("ORIGINAL ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("HidException", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Engine error", StatusTone.Error);
            }

            Match statistics = StatisticsPattern.Match(line);
            if (statistics.Success)
            {
                ReportsText.Text = statistics.Groups["reports"].Value;
                QueueText.Text = statistics.Groups["queue"].Value + " / 10";
                BufferText.Text = statistics.Groups["buffer"].Value + " ms";
            }
        }));
    }

    private void Engine_ProcessExited(int exitCode)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetRunningInterface(false);

            if (exitCode == 0)
            {
                if (_currentMode == EngineMode.Diagnostic && _controllerConfirmed)
                {
                    SetStatus("DualSense ready", StatusTone.Ready);
                    SidebarControllerState.Text = "Connection confirmed";
                }
                else
                {
                    SetStatus("Engine stopped", StatusTone.Neutral);
                }
            }
            else
            {
                SetStatus("Session ended with an error", StatusTone.Error);
            }

            AppendLog($"[{DateTime.Now:HH:mm:ss}] Engine exited · code {exitCode}");
        }));
    }

    private void AppendLog(string line)
    {
        DiagnosticsLog.AppendText(line + Environment.NewLine);
        DiagnosticsLog.ScrollToEnd();
    }

    private void SetRunningInterface(bool running)
    {
        StartAudioButton.IsEnabled = !running;
        StopAudioButton.IsEnabled = running;
        SessionButtonText.Text = running ? "Stop audio" : "Start audio";
        SessionButtonIcon.Data = (Geometry)FindResource(running ? "IconStop" : "IconPlay");
        SessionButton.Background = running
            ? new SolidColorBrush(Color.FromRgb(74, 37, 48))
            : (Brush)FindResource("AccentBrush");

        AudioDeviceCombo.IsEnabled = !running;
        DashboardDeviceCombo.IsEnabled = !running;
        HeadsetRadio.IsEnabled = !running;
        SpeakerRadio.IsEnabled = !running;
        DashboardHeadsetRadio.IsEnabled = !running;
        DashboardSpeakerRadio.IsEnabled = !running;
        ProtocolCombo.IsEnabled = !running;
        BufferLengthSlider.IsEnabled = !running;
        PeriodTextBox.IsEnabled = !running;
        AppLightingControlCheckBox.IsEnabled = !running;
    }

    private void SetControllerState(bool connected)
    {
        Brush color = connected
            ? (Brush)FindResource("SuccessBrush")
            : new SolidColorBrush(Color.FromRgb(100, 112, 131));

        HeroStatusDot.Fill = color;
        HeroStatusText.Text = connected ? "CONNECTED" : "DISCONNECTED";
        HeroStatusText.Foreground = connected
            ? new SolidColorBrush(Color.FromRgb(167, 239, 211))
            : new SolidColorBrush(Color.FromRgb(180, 190, 204));
        DashboardConnectionText.Text = connected ? "Connected" : "Waiting";
        SidebarControllerState.Text = connected ? "HID channel active" : "Waiting for test";
    }

    private void SetStatus(string text, StatusTone tone)
    {
        TopStatusText.Text = text;

        Brush color = tone switch
        {
            StatusTone.Ready => (Brush)FindResource("SuccessBrush"),
            StatusTone.Working => (Brush)FindResource("AccentBrush"),
            StatusTone.Error => (Brush)FindResource("DangerBrush"),
            _ => new SolidColorBrush(Color.FromRgb(100, 112, 131))
        };

        TopStatusDot.Fill = color;
    }

    private void UpdateColorPreview()
    {
        int red = (int)Math.Round(RedSlider.Value);
        int green = (int)Math.Round(GreenSlider.Value);
        int blue = (int)Math.Round(BlueSlider.Value);

        Color color = Color.FromRgb((byte)red, (byte)green, (byte)blue);
        string hex = $"{red:X2}{green:X2}{blue:X2}";
        var brush = new SolidColorBrush(color);

        RedValueText.Text = red.ToString(CultureInfo.CurrentCulture);
        GreenValueText.Text = green.ToString(CultureInfo.CurrentCulture);
        BlueValueText.Text = blue.ToString(CultureInfo.CurrentCulture);

        HexTextBox.Text = "#" + hex;
        LightingHexLabel.Text = "#" + hex;
        DashboardHexText.Text = "#" + hex;
        LightingColorSwatch.Background = brush;
        DashboardColorSwatch.Background = brush;
        LightingGlow.Fill = new SolidColorBrush(Color.FromArgb(165, color.R, color.G, color.B));
        DashboardGlow.Fill = new SolidColorBrush(Color.FromArgb(145, color.R, color.G, color.B));

        bool enabled = LedEnabledCheckBox.IsChecked == true;
        LightingGlow.Opacity = enabled ? 0.68 : 0.08;
        DashboardGlow.Opacity = enabled ? 0.55 : 0.06;
    }

    private void UpdatePlayerLedPreview()
    {
        int selectedIndex = Math.Max(0, PlayerLedsCombo.SelectedIndex);
        int litCount = selectedIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            _ => 5
        };

        for (int index = 0; index < PlayerLedPreview.Children.Count; index++)
        {
            if (PlayerLedPreview.Children[index] is System.Windows.Shapes.Ellipse led)
            {
                led.Fill = new SolidColorBrush(index < litCount
                    ? Color.FromRgb(139, 154, 255)
                    : Color.FromRgb(38, 48, 67));
            }
        }
    }

    private void UpdateVolumeLabels(int volume)
    {
        VolumeValueText.Text = $"{volume} / 127";
        DashboardVolumeText.Text = $"{volume} / 127";
    }

    private void ShowPage(string name)
    {
        DashboardPage.Visibility = name == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        AudioPage.Visibility = name == "Audio" ? Visibility.Visible : Visibility.Collapsed;
        LightingPage.Visibility = name == "Lighting" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = name == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button button in new[]
                 {
                     DashboardNavButton,
                     AudioNavButton,
                     LightingNavButton,
                     DiagnosticsNavButton,
                     SettingsNavButton
                 })
        {
            bool selected = string.Equals(
                Convert.ToString(button.Tag, CultureInfo.InvariantCulture),
                name,
                StringComparison.Ordinal);

            button.Background = selected
                ? new SolidColorBrush(Color.FromRgb(29, 38, 64))
                : Brushes.Transparent;
            button.Foreground = selected
                ? Brushes.White
                : (Brush)FindResource("MutedBrush");
        }
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page)
        {
            ShowPage(page);
        }
    }

    private async void SessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            await StopEngineAsync();
        }
        else
        {
            await StartEngineAsync(EngineMode.Loopback);
        }
    }

    private async void StartAudioButton_Click(object sender, RoutedEventArgs e)
    {
        await StartEngineAsync(EngineMode.Loopback);
    }

    private async void StopAudioButton_Click(object sender, RoutedEventArgs e)
    {
        await StopEngineAsync();
    }

    private async void ToneButton_Click(object sender, RoutedEventArgs e)
    {
        await StartEngineAsync(EngineMode.Tone);
    }

    private async void CheckControllerButton_Click(object sender, RoutedEventArgs e)
    {
        _controllerConfirmed = false;
        await StartEngineAsync(EngineMode.Diagnostic);
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        AudioDeviceItem? selected = AudioDeviceCombo.SelectedItem as AudioDeviceItem;
        _preferredDeviceIndex = selected?.Index ?? -1;
        await RefreshAudioDevicesAsync();
    }

    private void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        DashboardDeviceCombo.SelectedItem = AudioDeviceCombo.SelectedItem;
        _synchronizing = false;
        ScheduleControlUpdate();
    }

    private void DashboardDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        AudioDeviceCombo.SelectedItem = DashboardDeviceCombo.SelectedItem;
        _synchronizing = false;
        ScheduleControlUpdate();
    }

    private void AudioTarget_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        _synchronizing = true;
        bool headset = HeadsetRadio.IsChecked == true;
        DashboardHeadsetRadio.IsChecked = headset;
        DashboardSpeakerRadio.IsChecked = !headset;
        _synchronizing = false;
        ScheduleControlUpdate();
    }

    private void DashboardTarget_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        _synchronizing = true;
        bool headset = DashboardHeadsetRadio.IsChecked == true;
        HeadsetRadio.IsChecked = headset;
        SpeakerRadio.IsChecked = !headset;
        _synchronizing = false;
        ScheduleControlUpdate();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        int volume = (int)Math.Round(VolumeSlider.Value);
        _synchronizing = true;
        DashboardVolumeSlider.Value = volume;
        _synchronizing = false;
        UpdateVolumeLabels(volume);
        ScheduleControlUpdate();
    }

    private void DashboardVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        int volume = (int)Math.Round(DashboardVolumeSlider.Value);
        _synchronizing = true;
        VolumeSlider.Value = volume;
        _synchronizing = false;
        UpdateVolumeLabels(volume);
        ScheduleControlUpdate();
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        UpdateColorPreview();
        MarkLightingDirty();
    }

    private void ColorPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } || !TryParseHex(hex, out Color color))
        {
            return;
        }

        _synchronizing = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _synchronizing = false;
        UpdateColorPreview();
        MarkLightingDirty();
    }

    private void HexTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyHexTextBox();
    }

    private void HexTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHexTextBox();
            Keyboard.ClearFocus();
        }
    }

    private void ApplyHexTextBox()
    {
        if (!TryParseHex(HexTextBox.Text, out Color color))
        {
            MessageBox.Show(
                this,
                "Enter a valid HEX color, for example #627CFF.",
                "Invalid color",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateColorPreview();
            return;
        }

        _synchronizing = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _synchronizing = false;
        UpdateColorPreview();
        MarkLightingDirty();
    }

    private void LedEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateColorPreview();
        MarkLightingDirty();
    }

    private void LightingOption_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        UpdatePlayerLedPreview();
        MarkLightingDirty();
    }

    private async void ApplyLightingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_lightingDirty || AppLightingControlCheckBox.IsChecked != true)
        {
            return;
        }

        ApplyLightingButton.IsEnabled = false;
        ApplyLightingButtonText.Text = "Applying...";
        LightingApplyHint.Text = "Sending the configuration to the controller...";

        bool applied = await WriteCurrentControlAsync(applyLighting: true);
        if (!applied)
        {
            SetLightingDirty(true);
            LightingApplyHint.Text = "Unable to apply. Check Diagnostics and try again.";
            LightingApplyHint.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private void AppLightingControlCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        bool enabled = AppLightingControlCheckBox.IsChecked == true;
        UpdateLightingOwnershipInterface();

        if (enabled)
        {
            SetLightingDirty(true);
            MessageBox.Show(
                this,
                "Lighting control is still experimental. Close DSX completely before applying a color; while DSX is running, it will keep control of the lightbar and this feature will not work.",
                "Lighting BETA",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        AppendLog(enabled
            ? "Lighting control from this app enabled."
            : "Lighting control from this app disabled; DSX keeps control of the LEDs.");
        ScheduleControlUpdate();
    }

    private void BufferLengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BufferLengthValueText is null)
        {
            return;
        }

        BufferLengthValueText.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.CurrentCulture);
        ScheduleControlUpdate();
    }

    private void AdvancedOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _synchronizing)
        {
            return;
        }

        if (SelectedTag(ProtocolCombo, "Full39") == "Legacy35")
        {
            PeriodTextBox.Text = "10.666";
        }
        else
        {
            PeriodTextBox.Text = "21.333333";
        }

        ScheduleControlUpdate();
    }

    private void AdvancedTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ScheduleControlUpdate();
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        _synchronizing = true;
        SelectComboItemByTag(ProtocolCombo, "Full39");
        BufferLengthSlider.Value = 64;
        PeriodTextBox.Text = "21.333333";
        _synchronizing = false;
        BufferLengthValueText.Text = "64";
        ScheduleControlUpdate();
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Clear();
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { path },
            UseShellExecute = true
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _engine.Dispose();
            return;
        }

        if (!_engine.IsRunning)
        {
            _allowClose = true;
            _engine.Dispose();
            return;
        }

        e.Cancel = true;
        await StopEngineAsync();
        _allowClose = true;
        Close();
    }

    private static int ParseInt(string text, int minimum, int maximum, string label)
    {
        if (
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ||
            value < minimum ||
            value > maximum)
        {
            throw new FormatException($"{label} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static double ParseDouble(
        string text,
        double minimum,
        double maximum,
        string label)
    {
        bool parsed = double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.CurrentCulture,
            out double value);

        if (!parsed)
        {
            parsed = double.TryParse(
                text.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        if (!parsed || value < minimum || value > maximum)
        {
            throw new FormatException(
                $"{label} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static string NormalizeHex(string text)
    {
        if (!TryParseHex(text, out Color color))
        {
            throw new FormatException("The HEX color is invalid.");
        }

        return $"{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool TryParseHex(string text, out Color color)
    {
        color = default;
        string hex = text.Trim().TrimStart('#');

        if (
            hex.Length != 6 ||
            !int.TryParse(
                hex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out int rgb))
        {
            return false;
        }

        color = Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
        return true;
    }

    private static string SelectedTag(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? Convert.ToString(item.Tag, CultureInfo.InvariantCulture) ?? fallback
            : fallback;
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        foreach (object item in comboBox.Items)
        {
            if (
                item is ComboBoxItem comboItem &&
                string.Equals(
                    Convert.ToString(comboItem.Tag, CultureInfo.InvariantCulture),
                    tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }
    }

    private static string ModeName(EngineMode mode)
    {
        return mode switch
        {
            EngineMode.Diagnostic => "HID diagnostics",
            EngineMode.Tone => "test tone",
            EngineMode.ListDevices => "device listing",
            _ => "audio session"
        };
    }

    private enum StatusTone
    {
        Neutral,
        Ready,
        Working,
        Error
    }
}
