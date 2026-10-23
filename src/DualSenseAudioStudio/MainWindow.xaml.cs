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
    private bool _liveLightingPending;
    private bool _restartPending;
    private readonly List<GamepadBridge> _gamepads = new();
    private GamepadBridge? _gamepad => _gamepads.OrderBy(g => g.Player).FirstOrDefault();
    private string _currentPage = "Dashboard";
    private bool _userStoppedAudio;
    private string? _lastDefaultOutputId;
    private DateTime _nextAutoStart = DateTime.MinValue;

    private Button[] HomeTiles => new[]
    {
        HomeTileDashboard, HomeTileController, HomeTileAudio,
        HomeTileLighting, HomeTileDiagnostics, HomeTileSettings
    };

    public MainWindow()
    {
        InitializeComponent();

        _controlDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
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
        InitializeExtras();
        try
        {
            if (File.Exists(EmulationFile) && File.ReadAllText(EmulationFile).Trim() == "playstation")
            {
                EmulatePlayStationRadio.IsChecked = true;
            }
        }
        catch
        {
        }
        UpdateGamepadBridge();

        var tileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tileTimer.Tick += async (_, _) =>
        {
            ClockText.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
            UpdateGamepadBridge();
            UpdateOverviewTiles();
            await UpdateBatteryAsync();
            await AutoStartAudioAsync();
        };
        tileTimer.Start();
        ClockText.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        SelectDualSenseAsWindowsDefault();
        UpdateOverviewTiles();
        ShowPage("Dashboard");
        HomeLiveView.Content = new Controls.ControllerLiveView(compact: true);

        // Containers must not take controller focus; only real controls should.
        DisableContainerFocus(this);

        // Keep the focus ring on the focused control as focus, scroll or layout changes.
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => UpdateFocusRing()), true);
        LayoutUpdated += (_, _) => UpdateFocusRing();
        PreviewMouseDown += (_, _) => { _controllerNavActive = false; FocusRing.Visibility = Visibility.Collapsed; };

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
            // Audio runs all the time; pause it for one-off tests (auto-start resumes it).
            if (_currentMode != EngineMode.Loopback || mode == EngineMode.Loopback)
            {
                return;
            }
            await StopEngineAsync();
            _nextAutoStart = DateTime.UtcNow.AddSeconds(mode == EngineMode.MicProbe ? 20 : 8);
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
                EnableMic = MicEnabledCheckBox.IsChecked == true,
                NoiseRemoval = NoiseRemovalCheckBox.IsChecked == true,
                // Hear-myself removed: over Bluetooth it returns ~300 ms late (sounds like echo).
                Sidetone = false,
                Haptics = HapticsCheckBox.IsChecked == true,
                LowLatency = LowLatencyCheckBox.IsChecked == true,
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

        // Apply lighting live instead of waiting for the Apply button.
        if (AppLightingControlCheckBox.IsChecked == true)
        {
            _liveLightingPending = true;
            _controlDebounce.Stop();
            _controlDebounce.Start();
        }
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

    // Settings the engine only reads at startup: restart a running audio session.
    private void RequestRestart()
    {
        _restartPending = true;
        ScheduleControlUpdate();
    }

    private bool _controllerNavActive;

    private static void DisableContainerFocus(DependencyObject root)
    {
        if (root is ScrollViewer or ContentControl and not (Button or CheckBox or RadioButton or ComboBoxItem))
        {
            ((UIElement)root).Focusable = false;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DisableContainerFocus(VisualTreeHelper.GetChild(root, i));
        }
    }

    // Moves the white focus ring onto the focused control (shown once the controller is used).
    private void UpdateFocusRing()
    {
        if (!_controllerNavActive
            || Keyboard.FocusedElement is not FrameworkElement element
            || !element.IsVisible
            || !IsAncestorOf(element))
        {
            FocusRing.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            Point topLeft = element.TransformToAncestor(FocusOverlay).Transform(new Point(0, 0));
            const double pad = 4;
            Canvas.SetLeft(FocusRing, topLeft.X - pad);
            Canvas.SetTop(FocusRing, topLeft.Y - pad);
            FocusRing.Width = element.ActualWidth + pad * 2;
            FocusRing.Height = element.ActualHeight + pad * 2;
            double radius = element is Button b && b.Style == (Style)FindResource("HomeTileStyle") ? 22
                : element is Button s && s.Style == (Style)FindResource("SwatchStyle") ? FocusRing.Width / 2
                : 12;
            FocusRing.CornerRadius = new CornerRadius(radius);
            FocusRing.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            FocusRing.Visibility = Visibility.Collapsed;
        }
    }

    // Controller navigation while this window is in front:
    // L1/R1 switch home tiles, D-pad/left stick move focus, Cross selects, Circle goes Home.
    private void HandleControllerNav(string button)
    {
        // PS button: show the desktop (minimize all); press again to bring windows back (Win+D).
        if (button == "PS")
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is not null)
                {
                    object shell = Activator.CreateInstance(shellType)!;
                    shellType.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Show desktop failed: {ex.Message}");
            }
            return;
        }

        if (!IsActive)
        {
            return;
        }

        _controllerNavActive = true;
        Button[] tiles = HomeTiles;
        int index = Array.FindIndex(tiles, t => Equals(t.Tag, _currentPage));
        IInputElement? focused = Keyboard.FocusedElement;

        switch (button)
        {
            case "L1":
            case "R1":
                int next = Math.Clamp(index + (button == "R1" ? 1 : -1), 0, tiles.Length - 1);
                ShowPage((string)tiles[next].Tag);
                tiles[next].Focus();
                break;

            case "Circle":
                // Back: close an open dropdown first, otherwise go Home.
                if (focused is ComboBoxItem openItem
                    && ItemsControl.ItemsControlFromItemContainer(openItem) is ComboBox openCombo)
                {
                    openCombo.IsDropDownOpen = false;
                    openCombo.Focus();
                    break;
                }
                ShowPage("Dashboard");
                HomeTileDashboard.Focus();
                break;

            case "Cross":
                switch (focused)
                {
                    case ComboBoxItem item:
                        item.IsSelected = true;
                        if (ItemsControl.ItemsControlFromItemContainer(item) is ComboBox owner)
                        {
                            owner.IsDropDownOpen = false;
                            owner.Focus();
                        }
                        break;
                    case ComboBox dropdown when !dropdown.IsDropDownOpen:
                        dropdown.IsDropDownOpen = true;
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                        {
                            int i = Math.Max(0, dropdown.SelectedIndex);
                            (dropdown.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem)?.Focus();
                        }));
                        break;
                    case Button b:
                        b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        break;
                    case CheckBox c:
                        c.IsChecked = c.IsChecked != true;
                        break;
                    case RadioButton r:
                        r.IsChecked = true;
                        break;
                }
                break;

            default:
                // Sliders take Left/Right as value changes.
                if (focused is Slider slider && button is "Left" or "Right")
                {
                    double step = Math.Max(1, (slider.Maximum - slider.Minimum) / 25);
                    slider.Value = Math.Clamp(slider.Value + (button == "Right" ? step : -step), slider.Minimum, slider.Maximum);
                    break;
                }

                if (focused is not UIElement element || (focused is DependencyObject d && !IsAncestorOf(d) && focused is not ComboBoxItem))
                {
                    tiles[Math.Max(index, 0)].Focus();
                    break;
                }

                // On the tile row, Left/Right walks the tiles and opens them, PS5-style.
                int tileIndex = Array.IndexOf(tiles, focused);
                if (tileIndex >= 0 && button is "Left" or "Right")
                {
                    int target = Math.Clamp(tileIndex + (button == "Right" ? 1 : -1), 0, tiles.Length - 1);
                    ShowPage((string)tiles[target].Tag);
                    tiles[target].Focus();
                    break;
                }

                element.MoveFocus(new TraversalRequest(button switch
                {
                    "Up" => FocusNavigationDirection.Up,
                    "Down" => FocusNavigationDirection.Down,
                    "Left" => FocusNavigationDirection.Left,
                    _ => FocusNavigationDirection.Right
                }));
                break;
        }
    }

    // Selects "DualSense Wireless Controller" as the Windows playback and recording
    // device, like plugging in a real headset (no admin needed).
    private void SelectDualSenseAsWindowsDefault()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                MMDevice? device = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.StartsWith("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase));
                if (device is not null)
                {
                    AudioPolicy.SetDefault(device.ID);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not select DualSense as the Windows device: {ex.Message}");
        }
    }

    // Audio runs automatically: started at launch and restarted whenever it stops
    // while the controller is connected (no Start button).
    private async Task AutoStartAudioAsync()
    {
        // Follow the Windows default output: restart capture when it changes.
        string? defaultId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = device.ID;
        }
        catch
        {
        }

        if (defaultId != _lastDefaultOutputId)
        {
            bool changed = _lastDefaultOutputId is not null;
            _lastDefaultOutputId = defaultId;
            if (changed && _engine.IsRunning && _currentMode == EngineMode.Loopback)
            {
                AppendLog("Windows output changed, following it...");
                await StopEngineAsync();
                _nextAutoStart = DateTime.MinValue;
            }
        }

        if (_userStoppedAudio || _engine.IsRunning || DateTime.UtcNow < _nextAutoStart)
        {
            return;
        }

        bool present;
        try
        {
            present = HidApi.Hid.Enumerate(0x054C, 0x0CE6).Any();
        }
        catch
        {
            present = false;
        }

        if (!present)
        {
            return;
        }

        _nextAutoStart = DateTime.UtcNow.AddSeconds(5);
        await StartEngineAsync(EngineMode.Loopback);
    }

    // Refreshes the Overview tiles once a second (controller, games, audio, mic).
    private void UpdateOverviewTiles()
    {
        bool present;
        try
        {
            present = HidApi.Hid.Enumerate(0x054C, 0x0CE6).Any();
        }
        catch
        {
            present = false;
        }

        TileControllerText.Text = present ? "Connected" : "Not found";
        TileControllerSub.Text = present ? "DualSense · Bluetooth" : "Pair it in Bluetooth settings";
        HeroStatusDot.Fill = present ? (Brush)FindResource("SuccessBrush") : new SolidColorBrush(Color.FromRgb(100, 112, 131));
        HeroStatusText.Text = present ? "CONNECTED" : "DISCONNECTED";

        int? battery = _gamepad?.BatteryLevel;
        HeroBatteryText.Text = battery is null
            ? "BATTERY —"
            : $"BATTERY {Math.Min(100, battery.Value * 10 + 5)}%{(_gamepad!.Charging ? " · CHARGING" : "")}";

        TileGameText.Text = _gamepad is null ? "Off" : $"{(_gamepad.PlayStationMode ? "PlayStation" : "Xbox")} · {_gamepads.Count}P";
        TileControllerSub.Text = present ? $"{Math.Max(1, _gamepads.Count)} controller(s) · Bluetooth" : TileControllerSub.Text;
        bool audio = _engine.IsRunning && _currentMode == EngineMode.Loopback;
        TileAudioText.Text = audio
            ? (HeadsetRadio.IsChecked == true ? "On · Headset" : "On · Speaker")
            : "Off";
        if (!audio)
        {
            BufferText.Text = "Press Start audio";
        }
        TileMicText.Text = audio && MicEnabledCheckBox.IsChecked == true ? "On" : "Off";
    }

    private void GameControllerCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            UpdateGamepadBridge();
        }
    }

    private static readonly string EmulationFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualSenseAudioStudio", "emulation.txt");

    private void EmulationMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        bool playStation = EmulatePlayStationRadio.IsChecked == true;
        try { File.WriteAllText(EmulationFile, playStation ? "playstation" : "xbox"); } catch { }

        // Recreate every virtual controller as the chosen type.
        if (_gamepads.Any(g => g.PlayStationMode != playStation))
        {
            RemoveAllGamepads();
            UpdateGamepadBridge();
        }
    }

    private void RemoveAllGamepads()
    {
        foreach (GamepadBridge g in _gamepads)
        {
            g.Dispose();
        }
        _gamepads.Clear();
    }

    // Multi-controller: one virtual gamepad per connected DualSense (Player 1-4).
    // Called at start and every second, so controllers can join and leave anytime.
    private void UpdateGamepadBridge()
    {
        bool wanted = GameControllerCheckBox.IsChecked == true && !_paused;
        bool playStation = EmulatePlayStationRadio.IsChecked == true;
        if (!wanted)
        {
            if (_gamepads.Count > 0)
            {
                RemoveAllGamepads();
                AppendLog("Game controllers removed.");
            }
            return;
        }

        List<string> paths;
        try
        {
            paths = HidApi.Hid.Enumerate(0x054C, 0x0CE6).Select(d => d.Path).Distinct().Take(4).ToList();
        }
        catch
        {
            return;
        }

        foreach (GamepadBridge gone in _gamepads.Where(g => !paths.Contains(g.DevicePath!)).ToList())
        {
            AppendLog($"Player {gone.Player} disconnected.");
            gone.Dispose();
            _gamepads.Remove(gone);
        }

        foreach (string path in paths.Where(p => _gamepads.All(g => g.DevicePath != p)))
        {
            int player = Enumerable.Range(1, 4).First(n => _gamepads.All(g => g.Player != n));
            try
            {
                var bridge = new GamepadBridge(playStation, path, player);
                bridge.StatusChanged += status =>
                    Dispatcher.BeginInvoke(new Action(() => AppendLog(status)));
                bridge.NavPressed += button =>
                    Dispatcher.BeginInvoke(new Action(() => HandleControllerNav(button)));
                _gamepads.Add(bridge);
                AppendLog($"Player {player} connected ({(playStation ? "PlayStation" : "Xbox")}).");
            }
            catch (Exception ex)
            {
                AppendLog($"Game controller unavailable (is ViGEmBus installed?): {ex.Message}");
                GameControllerCheckBox.IsChecked = false;
                return;
            }
        }
    }

    private void MicEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            RequestRestart();
        }
    }

    private async void ControlDebounce_Tick(object? sender, EventArgs e)
    {
        _controlDebounce.Stop();

        if (_restartPending)
        {
            _restartPending = false;
            if (_engine.IsRunning && _currentMode == EngineMode.Loopback)
            {
                AppendLog("Setting changed, restarting audio...");
                await StopEngineAsync();
                await StartEngineAsync(EngineMode.Loopback);
                return;
            }
        }

        bool applyLighting = _liveLightingPending;
        _liveLightingPending = false;
        await WriteCurrentControlAsync(applyLighting);

        // Audio not running: push lighting with a quick one-shot engine run.
        if (applyLighting && !_engine.IsRunning)
        {
            await StartEngineAsync(EngineMode.Diagnostic);
        }
    }

    private void Engine_OutputReceived(string line)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppendLog(line);

            // Controller mute button pressed: PS5-style on-screen pop-up.
            if (line.StartsWith("Mic: muted", StringComparison.Ordinal))
            {
                Controls.MuteToast.Show(muted: true);
            }
            else if (line.StartsWith("Mic: unmuted", StringComparison.Ordinal))
            {
                Controls.MuteToast.Show(muted: false);
            }

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
                BufferText.Text = $"Streaming · {statistics.Groups["buffer"].Value} ms buffer";
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
        ControllerPage.Content ??= new Controls.ControllerLiveView();
        ControllerPage.Visibility = name == "Controller" ? Visibility.Visible : Visibility.Collapsed;
        _currentPage = name;

        // PS5 home tiles: the selected tile grows, gets a white ring, and its name shows beside the row.
        foreach (Button tile in HomeTiles)
        {
            bool selected = Equals(tile.Tag, name);
            tile.Width = tile.Height = selected ? 124 : 96;
            tile.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            tile.Background = selected
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x70, 0xD1))
                : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        }
        HomeTileLabel.Text = name switch
        {
            "Dashboard" => "Home",
            "Settings" => "Settings",
            _ => name
        };

        foreach (Button button in new[]
                 {
                     DashboardNavButton,
                     AudioNavButton,
                     LightingNavButton,
                     ControllerNavButton,
                     DiagnosticsNavButton,
                     SettingsNavButton
                 })
        {
            bool selected = string.Equals(
                Convert.ToString(button.Tag, CultureInfo.InvariantCulture),
                name,
                StringComparison.Ordinal);

            button.Background = selected
                ? Brushes.White
                : Brushes.Transparent;
            // PS5-style focus: white pill with dark text.
            button.Foreground = selected
                ? new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20))
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
        RequestRestart();
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
        RequestRestart();
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
        RequestRestart();
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
        RequestRestart();
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

    private async void MicProbeButton_Click(object sender, RoutedEventArgs e)
    {
        await StartEngineAsync(EngineMode.MicProbe);
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
        if (HideToTrayInsteadOfClosing())
        {
            e.Cancel = true;
            return;
        }

        if (_allowClose)
        {
            RemoveAllGamepads();
            _engine.Dispose();
            return;
        }

        if (!_engine.IsRunning)
        {
            _allowClose = true;
            RemoveAllGamepads();
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
            EngineMode.MicProbe => "mic probe",
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
