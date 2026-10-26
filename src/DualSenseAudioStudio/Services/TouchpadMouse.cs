namespace DualSenseAudioStudio.Services;

// MacBook-style trackpad behaviour for the DualSense touchpad (1920 x 1080 units):
// - jitter filter (smoothing + tiny-movement deadzone) so a resting finger doesn't wiggle
// - pointer acceleration: slow = precise, fast = covers the screen
// - ignores the jump when a finger lands or lifts
// - one-finger tap = left click, two-finger tap = right click
// - two-finger scroll with momentum (glides to a stop after a flick)
internal sealed class TouchpadMouse
{
    // Tuning.
    private const double BaseGain = 0.55;      // pixels per touch unit at slow speed
    private const double AccelGain = 0.045;    // extra gain per unit/report of speed
    private const double MaxGain = 2.6;
    private const double Smoothing = 0.45;     // 0..1, higher = smoother (more lag)
    private const double JitterDeadzone = 1.2; // touch units per report ignored as noise
    private const int SettleReports = 2;       // reports ignored after a finger lands
    private const double ScrollGain = 2.2;     // wheel units per touch unit (120 = one notch)

    // User sensitivity from the app (1.0 = default; 0.3 slow .. 3.0 fast). Shared by all controllers.
    public static volatile float Sensitivity = 1f;

    // Off in fullscreen games: any mouse movement makes games like Fortnite switch to
    // keyboard/mouse mode and back (aim and controls jerk around). The touchpad then
    // goes to the game as its normal touchpad button instead.
    public static volatile bool Enabled = true;
    public static volatile bool GameInFront;
    public static bool Active => Enabled && !GameInFront;
    private const double ScrollFriction = 0.93;

    public int LastX { get; private set; }

    private bool _down;
    private int _settle;
    private int _lastX, _lastY, _startX, _startY;
    private double _vx, _vy, _carryX, _carryY;
    private double _pendingX, _pendingY; // one report held back so lift-off jumps are dropped
    private long _touchStart;
    private int _maxFingers;
    private bool _moved;

    // Two-finger scroll.
    private bool _scrolling;
    private int _scrollLastY, _scrollLastX;
    private double _scrollVy, _scrollVx, _scrollCarryY, _scrollCarryX;

    /// <summary>Feed one input report's touch data (8 bytes: finger 1 then finger 2).</summary>
    public void Update(ReadOnlySpan<byte> touch, bool suppressTapClick)
    {
        var (f1, x1, y1) = Finger(touch.Slice(0, 4));
        var (f2, x2, y2) = Finger(touch.Slice(4, 4));
        int fingers = (f1 ? 1 : 0) + (f2 ? 1 : 0);

        if (fingers == 2)
        {
            Scroll((y1 + y2) / 2, (x1 + x2) / 2);
            _maxFingers = 2;
            return;
        }

        EndScroll();
        Glide();

        if (f1)
        {
            Move(x1, y1);
            return;
        }

        if (_down)
        {
            Lift(suppressTapClick);
        }
    }

    private static (bool down, int x, int y) Finger(ReadOnlySpan<byte> t)
    {
        bool down = (t[0] & 0x80) == 0;
        int x = t[1] | ((t[2] & 0x0F) << 8);
        int y = (t[2] >> 4) | (t[3] << 4);
        return (down, x, y);
    }

    private void Move(int x, int y)
    {
        LastX = x;
        if (!_down)
        {
            _down = true;
            _settle = SettleReports;
            _lastX = _startX = x;
            _lastY = _startY = y;
            _vx = _vy = _pendingX = _pendingY = 0;
            _touchStart = Environment.TickCount64;
            _maxFingers = 1;
            _moved = false;
            _scrollVx = _scrollVy = 0; // touching stops scroll momentum, like a Mac
            return;
        }

        int dx = x - _lastX, dy = y - _lastY;
        _lastX = x;
        _lastY = y;

        // Landing settle: the first readings jump while the finger flattens.
        if (_settle > 0)
        {
            _settle--;
            return;
        }

        // Smooth the raw deltas.
        _vx = _vx * Smoothing + dx * (1 - Smoothing);
        _vy = _vy * Smoothing + dy * (1 - Smoothing);

        double speed = Math.Sqrt(_vx * _vx + _vy * _vy);
        if (speed < JitterDeadzone)
        {
            return; // resting finger: ignore sensor noise
        }

        if (Math.Abs(x - _startX) > 30 || Math.Abs(y - _startY) > 30)
        {
            _moved = true;
        }

        double gain = Math.Min(MaxGain, BaseGain + speed * AccelGain) * Sensitivity;

        // Apply the previous report's movement now; keep this one pending, so if the
        // finger lifts next report its lift-off jump is thrown away.
        Emit(_pendingX, _pendingY);
        _pendingX = _vx * gain;
        _pendingY = _vy * gain;
    }

    private void Emit(double mx, double my)
    {
        _carryX += mx;
        _carryY += my;
        int px = (int)_carryX, py = (int)_carryY;
        _carryX -= px;
        _carryY -= py;
        if (px != 0 || py != 0)
        {
            NativeInput.MoveMouse(px, py);
        }
    }

    private void Lift(bool suppressTapClick)
    {
        _down = false;
        _pendingX = _pendingY = 0; // drop the lift-off jump

        bool quick = Environment.TickCount64 - _touchStart < 200;
        if (quick && !_moved && !suppressTapClick)
        {
            if (_maxFingers >= 2)
            {
                NativeInput.Button(right: true, down: true);
                NativeInput.Button(right: true, down: false);
            }
            else
            {
                NativeInput.LeftClick();
            }
        }
    }

    private void Scroll(int y, int x)
    {
        if (!_scrolling)
        {
            _scrolling = true;
            _scrollLastY = y;
            _scrollLastX = x;
            _scrollVy = _scrollVx = 0;
            _down = false;
            _pendingX = _pendingY = 0;
            if (Environment.TickCount64 - _touchStart > 200)
            {
                _touchStart = Environment.TickCount64; // two fingers placed together = tap candidate
            }
            return;
        }

        int dy = y - _scrollLastY, dx = x - _scrollLastX;
        _scrollLastY = y;
        _scrollLastX = x;
        if (Math.Abs(dy) + Math.Abs(dx) > 2)
        {
            _moved = true;
        }

        // Natural scrolling: content follows the fingers (like a Mac).
        _scrollVy = _scrollVy * 0.5 + dy * ScrollGain * Sensitivity * 0.5;
        _scrollVx = _scrollVx * 0.5 + -dx * ScrollGain * Sensitivity * 0.5;
        EmitScroll(_scrollVy, _scrollVx);
    }

    private void EndScroll()
    {
        if (!_scrolling)
        {
            return;
        }

        _scrolling = false;
        // Two fingers lifted without moving: right click (two-finger tap).
        if (!_moved && Environment.TickCount64 - _touchStart < 250)
        {
            NativeInput.Button(right: true, down: true);
            NativeInput.Button(right: true, down: false);
            _scrollVy = _scrollVx = 0;
        }
        _down = false;
        _maxFingers = 0;
    }

    // Momentum after a two-finger flick.
    private void Glide()
    {
        if (Math.Abs(_scrollVy) < 0.5 && Math.Abs(_scrollVx) < 0.5)
        {
            _scrollVy = _scrollVx = 0;
            return;
        }

        _scrollVy *= ScrollFriction;
        _scrollVx *= ScrollFriction;
        EmitScroll(_scrollVy, _scrollVx);
    }

    private void EmitScroll(double vy, double vx)
    {
        _scrollCarryY += vy;
        _scrollCarryX += vx;
        int wy = (int)_scrollCarryY, wx = (int)_scrollCarryX;
        _scrollCarryY -= wy;
        _scrollCarryX -= wx;
        if (wy != 0 || wx != 0)
        {
            NativeInput.Scroll(wy, wx);
        }
    }
}
