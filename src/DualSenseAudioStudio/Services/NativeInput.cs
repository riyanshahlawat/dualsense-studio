using System.Runtime.InteropServices;

namespace DualSenseAudioStudio.Services;

// Minimal SendInput wrapper for touchpad-as-mouse.
internal static class NativeInput
{
    private const uint InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
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

    public static void MoveMouse(int dx, int dy) => Send(dx, dy, MouseMove);

    public static void LeftClick()
    {
        Send(0, 0, LeftDown);
        Send(0, 0, LeftUp);
    }

    private static void Send(int dx, int dy, uint flags)
    {
        var input = new Input { Type = InputMouse };
        input.U.Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = flags };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}
