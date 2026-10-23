using System.Runtime.InteropServices;

// Registers the calling thread with Windows' Multimedia Class Scheduler as "Pro Audio"
// (what DAWs use), so games or background work can't starve audio threads.
internal static class ProAudio
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, int priority);

    private const int AvrtPriorityHigh = 1;

    public static void EnterThread()
    {
        try
        {
            uint index = 0;
            IntPtr handle = AvSetMmThreadCharacteristics("Pro Audio", ref index);
            if (handle != IntPtr.Zero)
            {
                AvSetMmThreadPriority(handle, AvrtPriorityHigh);
            }
            else
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
        }
        catch
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
    }
}
