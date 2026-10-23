using System.Diagnostics;
using System.Windows.Automation;

namespace DualSenseAudioStudio.Services;

// Opens the Windows on-screen keyboard when a text box anywhere on the desktop
// gets focus while the controller is driving Windows (Desktop mode), and closes
// it again a moment after focus leaves text. Uses UI Automation focus events.
internal sealed class AutoKeyboard : IDisposable
{
    private readonly AutomationFocusChangedEventHandler _handler;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly System.Threading.Timer _closeTimer;
    private bool _openedByUs;

    public AutoKeyboard()
    {
        _closeTimer = new System.Threading.Timer(_ => CloseKeyboard());
        _handler = OnFocusChanged;
        Automation.AddAutomationFocusChangedEventHandler(_handler);
    }

    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        if (!GamepadBridge.DesktopMode)
        {
            return;
        }

        try
        {
            if (sender is not AutomationElement element)
            {
                return;
            }

            AutomationElement.AutomationElementInformation info = element.Current;
            // Our own window has its own PS5-style keyboard; skip the on-screen keyboard itself.
            if (info.ProcessId == _ownProcessId || info.ClassName == "OSKMainClass")
            {
                return;
            }

            if (IsEditableText(element, info))
            {
                _closeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                OpenKeyboard();
            }
            else if (_openedByUs)
            {
                // Small delay so clicking a key on the keyboard doesn't close it.
                _closeTimer.Change(1500, Timeout.Infinite);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
    }

    private static bool IsEditableText(AutomationElement element, AutomationElement.AutomationElementInformation info)
    {
        if (info.IsPassword)
        {
            return true;
        }

        if (info.ControlType != ControlType.Edit && info.ControlType != ControlType.Document && info.ControlType != ControlType.ComboBox)
        {
            return false;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
        {
            return !((ValuePattern)pattern).Current.IsReadOnly;
        }

        return info.ControlType == ControlType.Edit || info.ControlType == ControlType.Document;
    }

    private void OpenKeyboard()
    {
        if (Process.GetProcessesByName("osk").Length > 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
            _openedByUs = true;
        }
        catch
        {
        }
    }

    private void CloseKeyboard()
    {
        if (!_openedByUs)
        {
            return;
        }

        foreach (Process osk in Process.GetProcessesByName("osk"))
        {
            osk.CloseMainWindow();
        }
        _openedByUs = false;
    }

    public void Dispose()
    {
        Automation.RemoveAutomationFocusChangedEventHandler(_handler);
        _closeTimer.Dispose();
    }
}
