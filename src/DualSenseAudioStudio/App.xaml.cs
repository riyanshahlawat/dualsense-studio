using System.Threading;
using System.Windows;

namespace DualSenseAudioStudio;

public partial class App : Application
{
    // Only one DualSense Studio may run (two would fight over the controller, mic and audio).
    // Starting it again just brings the running one to the front.
    private const string InstanceName = "DualSenseStudio.SingleInstance";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // No long GC pauses: keeps controller input to games smooth.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        _instanceMutex = new Mutex(true, InstanceName, out bool firstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");

        if (!firstInstance)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not null)
                    {
                        MainWindow.Show();
                        if (MainWindow.WindowState == WindowState.Minimized)
                        {
                            MainWindow.WindowState = WindowState.Normal;
                        }
                        MainWindow.Activate();
                    }
                });
            }
        })
        { IsBackground = true, Name = "SingleInstance" }.Start();

        base.OnStartup(e);
    }
}
