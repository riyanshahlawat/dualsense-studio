using System.Runtime.InteropServices;

namespace DualSenseAudioStudio.Services;

// Minimal SendInput wrapper: mouse move/buttons/scroll and keyboard keys,
// used by the touchpad mouse and Desktop mode.
internal static class NativeInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint Wheel = 0x0800;
    private const uint HWheel = 0x1000;
    private const uint KeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] private readonly long _pad0;
        [FieldOffset(8)] private readonly long _pad1;
        [FieldOffset(16)] private readonly long _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    public static void MoveMouse(int dx, int dy) => Mouse(dx, dy, 0, MouseMove);

    public static void LeftClick()
    {
        LeftButton(true);
        LeftButton(false);
    }

    public static void LeftButton(bool down) => Mouse(0, 0, 0, down ? LeftDown : LeftUp);

    public static void RightButton(bool down) => Mouse(0, 0, 0, down ? RightDown : RightUp);

    public static void Scroll(int vertical, int horizontal)
    {
        if (vertical != 0) Mouse(0, 0, vertical, Wheel);
        if (horizontal != 0) Mouse(0, 0, horizontal, HWheel);
    }

    public static void Key(ushort virtualKey, bool down)
    {
        var input = new Input { Type = InputKeyboard };
        input.U.Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = down ? 0 : KeyUp };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    public static void Tap(params ushort[] keys)
    {
        foreach (ushort k in keys) Key(k, true);
        foreach (ushort k in keys.Reverse()) Key(k, false);
    }

    private static void Mouse(int dx, int dy, int data, uint flags)
    {
        var input = new Input { Type = InputMouse };
        input.U.Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}
