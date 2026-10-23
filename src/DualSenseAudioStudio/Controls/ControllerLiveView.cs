using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HidApi;

namespace DualSenseAudioStudio.Controls;

// Live view of the DualSense over Bluetooth HID, drawn on the photoreal controller
// render: pressed buttons glow PS blue, the thumbsticks move, triggers fill and
// touchpad fingers show as dots. Reads on a background thread, redraws at ~60 Hz.
internal sealed class ControllerLiveView : Border
{
    private const ushort VendorId = 0x054C;
    private const ushort ProductId = 0x0CE6;

    // Image space of Assets/DualSense.png (1200 x 630).
    private const double ImageWidth = 1200, ImageHeight = 630;
    private const double TouchLeft = 472, TouchTop = 102, TouchWidth = 256, TouchHeight = 134;

    private readonly object _lock = new();
    private readonly byte[] _state = new byte[20]; // lx ly rx ry l2 r2 b0 b1 b2 bat chg valid t0(4) t1(4)
    private readonly DispatcherTimer _timer;
    private readonly bool _compact;
    private readonly Dictionary<string, Shape> _glows = new();
    private readonly Ellipse _leftCap = StickCap(), _rightCap = StickCap();
    private readonly Rectangle _l2Fill = TriggerFill(), _r2Fill = TriggerFill();
    private readonly Ellipse _touch0 = TouchDot(), _touch1 = TouchDot();
    private readonly TextBlock _status = new() { FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _detail = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xE3)),
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        Margin = new Thickness(0, 10, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private volatile bool _running;
    private DateTime _lastReport;

    private static readonly Color PsBlue = Color.FromRgb(0x1F, 0x8F, 0xFF);

    public ControllerLiveView() : this(compact: false)
    {
    }

    // compact: just the controller (for the Home screen).
    public ControllerLiveView(bool compact)
    {
        _compact = compact;
        Focusable = false;
        Padding = compact ? new Thickness(0) : new Thickness(8);

        var stage = new Grid { Width = ImageWidth, Height = ImageHeight };
        stage.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/DualSense.png")),
            Width = ImageWidth,
            Height = ImageHeight,
            Stretch = Stretch.Fill
        });
        RenderOptions.SetBitmapScalingMode(stage.Children[0], BitmapScalingMode.HighQuality);

        var overlay = new Canvas { Width = ImageWidth, Height = ImageHeight };
        stage.Children.Add(overlay);

        // Triggers: bars above the shoulders.
        AddTrigger(overlay, "L2", _l2Fill, 345);
        AddTrigger(overlay, "R2", _r2Fill, 765);

        GlowRect(overlay, "L1", 346, 90, 90, 22, 8);
        GlowRect(overlay, "R1", 765, 90, 90, 22, 8);
        GlowRect(overlay, "Create", 434, 128, 14, 26, 7);
        GlowRect(overlay, "Options", 751, 128, 14, 26, 7);
        GlowRect(overlay, "Touchpad", TouchLeft, TouchTop, TouchWidth, TouchHeight, 14);

        GlowRect(overlay, "Up", 366, 162, 34, 40, 8);
        GlowRect(overlay, "Down", 368, 232, 34, 40, 8);
        GlowRect(overlay, "Left", 330, 198, 40, 34, 8);
        GlowRect(overlay, "Right", 400, 198, 40, 34, 8);

        GlowCircle(overlay, "Triangle", 812, 165, 25);
        GlowCircle(overlay, "Circle", 859, 214, 25);
        GlowCircle(overlay, "Cross", 812, 264, 25);
        GlowCircle(overlay, "Square", 762, 214, 25);
        GlowCircle(overlay, "PS", 600, 308, 22);
        GlowRect(overlay, "Mute", 580, 341, 38, 12, 6);

        Place(overlay, _leftCap, 490 - 44, 307 - 44);
        Place(overlay, _rightCap, 710 - 44, 307 - 44);
        GlowCircle(overlay, "L3", 490, 307, 46);
        GlowCircle(overlay, "R3", 710, 307, 46);

        Place(overlay, _touch0, 0, 0);
        Place(overlay, _touch1, 0, 0);

        var view = new Viewbox { Child = stage, Stretch = Stretch.Uniform };
        if (compact)
        {
            Child = view;
        }
        else
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = "Every input, in view.", FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAD, 0xB9)) });
            header.Children.Add(_status);
            root.Children.Add(header);
            Grid.SetRow(view, 1);
            view.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(view);
            Grid.SetRow(_detail, 2);
            root.Children.Add(_detail);
            Child = root;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Redraw();
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        Services.GamepadBridge.SharedReport += OnSharedReport;
        new Thread(ReadLoop) { IsBackground = true, Name = "ControllerLiveView" }.Start();
        _timer.Start();
    }

    private void Stop()
    {
        _running = false;
        Services.GamepadBridge.SharedReport -= OnSharedReport;
        _timer.Stop();
    }

    // Normally the game-controller bridge already reads the controller; reuse its reports.
    private DateTime _lastShared = DateTime.MinValue;

    private void OnSharedReport(byte[] report)
    {
        _lastShared = DateTime.UtcNow;
        Parse(report);
    }

    private bool SharedFeedActive => (DateTime.UtcNow - _lastShared).TotalMilliseconds < 500;

    // Fallback reader: only opens its own connection when there is no shared feed
    // (game controller switched off), and closes it again as soon as one appears.
    private void ReadLoop()
    {
        while (_running)
        {
            if (SharedFeedActive)
            {
                Thread.Sleep(250);
                continue;
            }

            try
            {
                using var device = new Device(VendorId, ProductId);
                while (_running && !SharedFeedActive)
                {
                    ReadOnlySpan<byte> data = device.ReadTimeout(547, 100);
                    if (data.Length > 0)
                    {
                        Parse(data);
                    }
                }
            }
            catch
            {
                lock (_lock) { _state[11] = 0; }
                Thread.Sleep(1000);
            }
        }
    }

    private void Parse(ReadOnlySpan<byte> d)
    {
        lock (_lock)
        {
            if (d[0] == 0x31 && d.Length >= 56)
            {
                if ((d[1] & 0x0F) == 0x02) return; // mic audio frame
                const int b = 2;
                _state[0] = d[b]; _state[1] = d[b + 1]; _state[2] = d[b + 2]; _state[3] = d[b + 3];
                _state[4] = d[b + 4]; _state[5] = d[b + 5];
                _state[6] = d[b + 7]; _state[7] = d[b + 8]; _state[8] = d[b + 9];
                _state[9] = (byte)(d[b + 52] & 0x0F);
                _state[10] = (byte)((d[b + 52] >> 4) & 0x0F);
                _state[11] = 1;
                d.Slice(b + 32, 8).CopyTo(_state.AsSpan(12, 8));
            }
            else if (d[0] == 0x01 && d.Length >= 10)
            {
                _state[0] = d[1]; _state[1] = d[2]; _state[2] = d[3]; _state[3] = d[4];
                _state[6] = d[5]; _state[7] = d[6]; _state[8] = d[7];
                _state[4] = d[8]; _state[5] = d[9];
                _state[11] = 1;
                _state[12] = _state[16] = 0x80; // no touch data in basic reports
            }
            _lastReport = DateTime.UtcNow;
        }
    }

    private void Redraw()
    {
        byte[] s;
        DateTime last;
        lock (_lock) { s = (byte[])_state.Clone(); last = _lastReport; }

        bool live = s[11] == 1 && (DateTime.UtcNow - last).TotalSeconds < 1;
        _status.Text = live
            ? "Your controller is connected. Buttons, sticks, triggers and touchpad are live."
            : "Waiting for your DualSense… connect it over Bluetooth.";
        _status.Foreground = live ? Brushes.White : Brushes.Gray;

        int dpad = s[6] & 0x0F;
        Set("Up", dpad is 0 or 1 or 7);
        Set("Right", dpad is 1 or 2 or 3);
        Set("Down", dpad is 3 or 4 or 5);
        Set("Left", dpad is 5 or 6 or 7);
        Set("Square", (s[6] & 0x10) != 0);
        Set("Cross", (s[6] & 0x20) != 0);
        Set("Circle", (s[6] & 0x40) != 0);
        Set("Triangle", (s[6] & 0x80) != 0);
        Set("L1", (s[7] & 0x01) != 0);
        Set("R1", (s[7] & 0x02) != 0);
        Set("Create", (s[7] & 0x10) != 0);
        Set("Options", (s[7] & 0x20) != 0);
        Set("L3", (s[7] & 0x40) != 0);
        Set("R3", (s[7] & 0x80) != 0);
        Set("PS", (s[8] & 0x01) != 0);
        Set("Touchpad", (s[8] & 0x02) != 0);
        Set("Mute", (s[8] & 0x04) != 0);

        MoveCap(_leftCap, 490, 307, s[0], s[1]);
        MoveCap(_rightCap, 710, 307, s[2], s[3]);
        _l2Fill.Width = 90 * s[4] / 255.0;
        _r2Fill.Width = 90 * s[5] / 255.0;

        ShowTouch(_touch0, s.AsSpan(12, 4));
        ShowTouch(_touch1, s.AsSpan(16, 4));

        string battery = s[9] >= 10 ? "100%" : $"{Math.Min(100, s[9] * 10 + 5)}%";
        string charging = s[10] switch { 1 => " · charging", 2 => " · full", _ => "" };
        _detail.Text = live
            ? $"L {s[0],3},{s[1],3}   R {s[2],3},{s[3],3}   L2 {s[4],3}   R2 {s[5],3}   Battery {battery}{charging}"
            : string.Empty;
    }

    private void Set(string name, bool on) => _glows[name].Opacity = on ? 1 : 0;

    private static void MoveCap(Ellipse cap, double cx, double cy, byte x, byte y)
    {
        Canvas.SetLeft(cap, cx - 44 + (x - 128) / 128.0 * 20);
        Canvas.SetTop(cap, cy - 44 + (y - 128) / 128.0 * 20);
    }

    private static void ShowTouch(Ellipse dot, ReadOnlySpan<byte> t)
    {
        bool down = (t[0] & 0x80) == 0 && (t[1] | t[2] | t[3]) != 0;
        dot.Visibility = down ? Visibility.Visible : Visibility.Collapsed;
        if (!down) return;
        int x = t[1] | ((t[2] & 0x0F) << 8);
        int y = (t[2] >> 4) | (t[3] << 4);
        Canvas.SetLeft(dot, TouchLeft + x / 1920.0 * TouchWidth - 9);
        Canvas.SetTop(dot, TouchTop + y / 1080.0 * TouchHeight - 9);
    }

    private void GlowRect(Canvas c, string name, double x, double y, double w, double h, double r)
    {
        var shape = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r };
        StyleGlow(shape);
        Place(c, shape, x, y);
        _glows[name] = shape;
    }

    private void GlowCircle(Canvas c, string name, double cx, double cy, double radius)
    {
        var shape = new Ellipse { Width = radius * 2, Height = radius * 2 };
        StyleGlow(shape);
        Place(c, shape, cx - radius, cy - radius);
        _glows[name] = shape;
    }

    private static void StyleGlow(Shape shape)
    {
        shape.Fill = new SolidColorBrush(Color.FromArgb(0x8C, PsBlue.R, PsBlue.G, PsBlue.B));
        shape.Stroke = new SolidColorBrush(Color.FromRgb(0xBF, 0xE0, 0xFF));
        shape.StrokeThickness = 2;
        shape.Opacity = 0;
        shape.IsHitTestVisible = false;
        shape.Effect = new DropShadowEffect { Color = PsBlue, BlurRadius = 22, ShadowDepth = 0, Opacity = 1 };
    }

    private static void AddTrigger(Canvas c, string name, Rectangle fill, double x)
    {
        var track = new Rectangle
        {
            Width = 90, Height = 12, RadiusX = 6, RadiusY = 6,
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
        };
        Place(c, track, x, 58);
        Place(c, fill, x, 58);
        Place(c, new TextBlock { Text = name, Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAD, 0xB9)), FontSize = 16, FontWeight = FontWeights.SemiBold }, x + 34, 32);
    }

    private static void Place(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    private static Ellipse StickCap() => new()
    {
        Width = 88,
        Height = 88,
        Fill = new RadialGradientBrush(Color.FromRgb(0x3A, 0x3C, 0x44), Color.FromRgb(0x17, 0x18, 0x1D)),
        Stroke = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x10)),
        StrokeThickness = 3,
        IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Opacity = 0.5 }
    };

    private static Rectangle TriggerFill() => new()
    {
        Width = 0, Height = 12, RadiusX = 6, RadiusY = 6,
        Fill = new SolidColorBrush(PsBlue),
        Effect = new DropShadowEffect { Color = PsBlue, BlurRadius = 12, ShadowDepth = 0 }
    };

    private static Ellipse TouchDot() => new()
    {
        Width = 18, Height = 18,
        Fill = Brushes.White,
        Visibility = Visibility.Collapsed,
        Effect = new DropShadowEffect { Color = PsBlue, BlurRadius = 16, ShadowDepth = 0 }
    };
}
