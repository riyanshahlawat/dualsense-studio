using HidApi;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace DualSenseAudioStudio.Services;

// Mirrors the Bluetooth DualSense to a virtual Xbox 360 controller (ViGEmBus)
// so games that only support XInput can use it. Game rumble is sent back to
// the DualSense motors.
internal sealed class GamepadBridge : IDisposable
{
    private const ushort VendorId = 0x054C;
    private const ushort ProductId = 0x0CE6;

    private readonly ViGEmClient _client;
    private readonly IXbox360Controller? _pad;
    private readonly IDualShock4Controller? _ds4;

    public bool PlayStationMode { get; }
    private readonly Thread _thread;
    private volatile bool _running = true;
    private Device? _device;
    private byte _sequence;

    public event Action<string>? StatusChanged;

    public event Action<string>? NavPressed;

    // Stale reports skipped because newer input was already waiting (lag that was avoided).
    public long SkippedReports { get; private set; }
    private int _lastRumble;
    private int _lastNav;

    public int? BatteryLevel { get; private set; }   // 0..10
    public bool Charging { get; private set; }
    public DateTime LastInput { get; private set; }

    public string? DevicePath { get; }
    public int Player { get; }

    // One bridge per physical DualSense (HID path); player number sets its LEDs.
    public GamepadBridge(bool playStation = false, string? devicePath = null, int player = 1)
    {
        DevicePath = devicePath;
        Player = player;
        _client = new ViGEmClient();
        PlayStationMode = playStation;
        if (playStation)
        {
            // ViGEm has no PS5 target; DualShock 4 gives games PlayStation prompts.
            _ds4 = _client.CreateDualShock4Controller();
            _ds4.AutoSubmitReport = false;
            _ds4.FeedbackReceived += (_, e) => SendRumble(e.LargeMotor, e.SmallMotor);
            _ds4.Connect();
        }
        else
        {
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.FeedbackReceived += (_, e) => SendRumble(e.LargeMotor, e.SmallMotor);
            _pad.Connect();
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "GamepadBridge", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        while (_running)
        {
            try
            {
                using var device = DevicePath is null ? new Device(VendorId, ProductId) : new Device(DevicePath);
                _device = device;
                // No startup rumble write: extra 0x31 writes can interrupt controller audio.
                SendPlayerLeds();
                StatusChanged?.Invoke($"Player {Player}: game controller active ({(PlayStationMode ? "PlayStation" : "Xbox")})");
                while (_running)
                {
                    ReadOnlySpan<byte> first = device.ReadTimeout(547, 100);
                    if (first.Length == 0)
                    {
                        continue;
                    }

                    // Always act on the newest input: drain anything Windows queued
                    // behind it, so a slow moment never leaves the game reading stale
                    // (laggy) controller state.
                    byte[] latest = first.ToArray();
                    while (true)
                    {
                        ReadOnlySpan<byte> next = device.ReadTimeout(547, 0);
                        if (next.Length == 0)
                        {
                            break;
                        }
                        if (next[0] == 0x01 || (next[0] == 0x31 && next.Length > 1 && (next[1] & 0x0F) != 0x02))
                        {
                            latest = next.ToArray();
                            SkippedReports++;
                        }
                    }

                    ReadOnlySpan<byte> d = latest;
                    if (d.Length >= 12 && d[0] == 0x31 && (d[1] & 0x0F) != 0x02)
                    {
                        Submit(d.Slice(2));
                    }
                    else if (d.Length >= 10 && d[0] == 0x01)
                    {
                        // Basic report: lx ly rx ry b0 b1 b2 l2 r2 -> remap to full layout.
                        Span<byte> s = stackalloc byte[10];
                        s[0] = d[1]; s[1] = d[2]; s[2] = d[3]; s[3] = d[4];
                        s[4] = d[8]; s[5] = d[9]; s[7] = d[5]; s[8] = d[6]; s[9] = d[7];
                        Submit(s);
                    }
                }
            }
            catch
            {
                _device = null;
                StatusChanged?.Invoke("Game controller: waiting for DualSense…");
                Thread.Sleep(1000);
            }
        }
    }

    // s = USB-layout payload: 0 lx, 1 ly, 2 rx, 3 ry, 4 l2, 5 r2, 7..9 buttons.
    private void Submit(ReadOnlySpan<byte> s)
    {
        if (_ds4 is not null)
        {
            SubmitDualShock4(s);
        }
        else
        {
            SubmitXbox(s);
        }
        AfterSubmit(s);
    }

    private void SubmitDualShock4(ReadOnlySpan<byte> s)
    {
        var p = _ds4!;
        p.SetAxisValue(DualShock4Axis.LeftThumbX, s[0]);
        p.SetAxisValue(DualShock4Axis.LeftThumbY, s[1]);
        p.SetAxisValue(DualShock4Axis.RightThumbX, s[2]);
        p.SetAxisValue(DualShock4Axis.RightThumbY, s[3]);
        p.SetSliderValue(DualShock4Slider.LeftTrigger, s[4]);
        p.SetSliderValue(DualShock4Slider.RightTrigger, s[5]);

        byte b0 = s[7], b1 = s[8], b2 = s[9];
        p.SetDPadDirection((b0 & 0x0F) switch
        {
            0 => DualShock4DPadDirection.North,
            1 => DualShock4DPadDirection.Northeast,
            2 => DualShock4DPadDirection.East,
            3 => DualShock4DPadDirection.Southeast,
            4 => DualShock4DPadDirection.South,
            5 => DualShock4DPadDirection.Southwest,
            6 => DualShock4DPadDirection.West,
            7 => DualShock4DPadDirection.Northwest,
            _ => DualShock4DPadDirection.None
        });
        p.SetButtonState(DualShock4Button.Square, (b0 & 0x10) != 0);
        p.SetButtonState(DualShock4Button.Cross, (b0 & 0x20) != 0);
        p.SetButtonState(DualShock4Button.Circle, (b0 & 0x40) != 0);
        p.SetButtonState(DualShock4Button.Triangle, (b0 & 0x80) != 0);
        p.SetButtonState(DualShock4Button.ShoulderLeft, (b1 & 0x01) != 0);
        p.SetButtonState(DualShock4Button.ShoulderRight, (b1 & 0x02) != 0);
        p.SetButtonState(DualShock4Button.TriggerLeft, (b1 & 0x04) != 0);
        p.SetButtonState(DualShock4Button.TriggerRight, (b1 & 0x08) != 0);
        p.SetButtonState(DualShock4Button.Share, (b1 & 0x10) != 0);
        p.SetButtonState(DualShock4Button.Options, (b1 & 0x20) != 0);
        p.SetButtonState(DualShock4Button.ThumbLeft, (b1 & 0x40) != 0);
        p.SetButtonState(DualShock4Button.ThumbRight, (b1 & 0x80) != 0);
        // PS opens DualSense Studio Home instead of being sent to the game.
        p.SetButtonState(DualShock4SpecialButton.Touchpad, (b2 & 0x02) != 0);
        p.SubmitReport();
    }

    private void SubmitXbox(ReadOnlySpan<byte> s)
    {
        var _pad = this._pad!;
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, Axis(s[0]));
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, Axis((byte)(255 - s[1])));
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, Axis(s[2]));
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, Axis((byte)(255 - s[3])));
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, s[4]);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, s[5]);

        byte b0 = s[7], b1 = s[8], b2 = s[9];
        int dpad = b0 & 0x0F;
        _pad.SetButtonState(Xbox360Button.Up, dpad is 0 or 1 or 7);
        _pad.SetButtonState(Xbox360Button.Right, dpad is 1 or 2 or 3);
        _pad.SetButtonState(Xbox360Button.Down, dpad is 3 or 4 or 5);
        _pad.SetButtonState(Xbox360Button.Left, dpad is 5 or 6 or 7);
        _pad.SetButtonState(Xbox360Button.X, (b0 & 0x10) != 0);      // Square
        _pad.SetButtonState(Xbox360Button.A, (b0 & 0x20) != 0);      // Cross
        _pad.SetButtonState(Xbox360Button.B, (b0 & 0x40) != 0);      // Circle
        _pad.SetButtonState(Xbox360Button.Y, (b0 & 0x80) != 0);      // Triangle
        _pad.SetButtonState(Xbox360Button.LeftShoulder, (b1 & 0x01) != 0);
        _pad.SetButtonState(Xbox360Button.RightShoulder, (b1 & 0x02) != 0);
        _pad.SetButtonState(Xbox360Button.Back, (b1 & 0x10) != 0 || (b2 & 0x02) != 0); // Create / touchpad click
        _pad.SetButtonState(Xbox360Button.Start, (b1 & 0x20) != 0);  // Options
        _pad.SetButtonState(Xbox360Button.LeftThumb, (b1 & 0x40) != 0);
        _pad.SetButtonState(Xbox360Button.RightThumb, (b1 & 0x80) != 0);
        // PS opens DualSense Studio Home (like a PS5), so it is not sent to games as Guide.
        _pad.SubmitReport();
    }

    private void AfterSubmit(ReadOnlySpan<byte> s)
    {
        byte b0 = s[7], b1 = s[8];
        int dpad = b0 & 0x0F;

        // Rising-edge events for navigating the app with the controller.
        int nav = 0;
        if (dpad is 0 or 1 or 7 || s[1] < 40) nav |= 1;        // Up
        if (dpad is 3 or 4 or 5 || s[1] > 215) nav |= 2;       // Down
        if (dpad is 5 or 6 or 7 || s[0] < 40) nav |= 4;        // Left
        if (dpad is 1 or 2 or 3 || s[0] > 215) nav |= 8;       // Right
        if ((b0 & 0x20) != 0) nav |= 16;                       // Cross
        if ((b0 & 0x40) != 0) nav |= 32;                       // Circle
        if ((b1 & 0x01) != 0) nav |= 64;                       // L1
        if ((b1 & 0x02) != 0) nav |= 128;                      // R1
        if ((b0 & 0x10) != 0) nav |= 256;                      // Square
        if ((b0 & 0x80) != 0) nav |= 512;                      // Triangle
        if ((s[9] & 0x01) != 0) nav |= 1024;                   // PS
        int pressed = nav & ~_lastNav;
        _lastNav = nav;
        if (pressed != 0)
        {
            string[] names = { "Up", "Down", "Left", "Right", "Cross", "Circle", "L1", "R1", "Square", "Triangle", "PS" };
            for (int i = 0; i < names.Length; i++)
            {
                if ((pressed & (1 << i)) != 0) NavPressed?.Invoke(names[i]);
            }
        }

        if (s.Length >= 36)
        {
            UpdateTouchMouse(s.Slice(32, 4));
        }

        if (s.Length > 52)
        {
            BatteryLevel = s[52] & 0x0F;
            Charging = ((s[52] >> 4) & 0x0F) == 1;
        }
        LastInput = DateTime.UtcNow;
    }

    // Touchpad as a laptop trackpad: finger 1 moves the cursor, a quick tap left-clicks.
    private const double MouseSensitivity = 1.6;
    private bool _touching;
    private int _lastX, _lastY, _startX, _startY;
    private long _touchStart;
    private double _carryX, _carryY;

    private void UpdateTouchMouse(ReadOnlySpan<byte> t)
    {
        bool down = (t[0] & 0x80) == 0;
        int x = t[1] | ((t[2] & 0x0F) << 8);
        int y = (t[2] >> 4) | (t[3] << 4);

        if (down && !_touching)
        {
            _touching = true;
            _lastX = _startX = x;
            _lastY = _startY = y;
            _touchStart = Environment.TickCount64;
            return;
        }

        if (down)
        {
            _carryX += (x - _lastX) * MouseSensitivity;
            _carryY += (y - _lastY) * MouseSensitivity;
            int dx = (int)_carryX, dy = (int)_carryY;
            _carryX -= dx;
            _carryY -= dy;
            if (dx != 0 || dy != 0)
            {
                NativeInput.MoveMouse(dx, dy);
            }
            _lastX = x;
            _lastY = y;
            return;
        }

        if (_touching)
        {
            _touching = false;
            bool quick = Environment.TickCount64 - _touchStart < 180;
            bool still = Math.Abs(_lastX - _startX) < 25 && Math.Abs(_lastY - _startY) < 25;
            if (quick && still)
            {
                NativeInput.LeftClick();
            }
        }
    }

    private static short Axis(byte v) => (short)Math.Clamp((v - 128) * 258, short.MinValue, short.MaxValue);

    // Player indicator LEDs like a PS5: 1 = center, 2 = two inner, 3 = three, 4 = four.
    private void SendPlayerLeds()
    {
        Device? device = _device;
        if (device is null) return;
        byte pattern = Player switch { 1 => 0x04, 2 => 0x0A, 3 => 0x15, _ => 0x1B };
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = (byte)((_sequence++ & 0x0F) << 4);
        report[2] = 0x10;
        const int common = 3;
        report[common + 1] = 0x10;          // valid_flag1: player indicator control
        report[common + 43] = pattern;
        uint crc = Crc32(report.AsSpan(0, 74));
        BitConverter.GetBytes(crc).CopyTo(report, 74);
        try { device.Write(report); } catch { }
    }

    // 0x31 state report with only the rumble motors marked valid.
    private void SendRumble(byte large, byte small)
    {
        Device? device = _device;
        if (device is null) return;
        // Only write when the rumble actually changes: every extra 0x31 write
        // competes with the audio stream and can make headphone audio cut out.
        int key = (large << 8) | small;
        if (key == _lastRumble) return;
        _lastRumble = key;
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = (byte)((_sequence++ & 0x0F) << 4);
        report[2] = 0x10;
        const int common = 3;
        report[common + 0] = 0x01 | 0x02;  // valid_flag0: compatible vibration + haptics select
        report[common + 2] = small;        // right (high-frequency) motor
        report[common + 3] = large;        // left (low-frequency) motor
        uint crc = Crc32(report.AsSpan(0, 74));
        BitConverter.GetBytes(crc).CopyTo(report, 74);
        try { device.Write(report); } catch { }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        crc = Step(crc, 0xA2);
        foreach (byte v in data) crc = Step(crc, v);
        return ~crc;

        static uint Step(uint crc, byte v)
        {
            crc ^= v;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            return crc;
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(500);
        try { _pad?.Disconnect(); } catch { }
        try { _ds4?.Disconnect(); } catch { }
        _client.Dispose();
    }
}
