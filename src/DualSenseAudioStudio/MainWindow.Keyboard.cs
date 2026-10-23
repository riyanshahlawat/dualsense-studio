using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DualSenseAudioStudio;

// PS5-style on-screen keyboard: opens when the controller selects a text box.
// D-pad moves, Cross types, Square deletes, Triangle adds a space, Circle closes.
public partial class MainWindow
{
    private Border? _keyboard;
    private TextBox? _keyboardTarget;
    private bool _keyboardShift;
    private readonly List<Button> _letterKeys = new();

    private bool KeyboardOpen => _keyboard?.Visibility == Visibility.Visible;

    private void OpenKeyboard(TextBox target)
    {
        _keyboardTarget = target;
        _keyboard ??= BuildKeyboard();
        _keyboard.Visibility = Visibility.Visible;
        _letterKeys.FirstOrDefault()?.Focus();
    }

    private void CloseKeyboard()
    {
        if (_keyboard is null)
        {
            return;
        }

        _keyboard.Visibility = Visibility.Collapsed;
        _keyboardTarget?.Focus();
        // Move off and back so LostFocus-based validation (e.g. HEX) applies.
        _keyboardTarget?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        _keyboardTarget?.Focus();
    }

    private Border BuildKeyboard()
    {
        string[] rows = { "1234567890", "qwertyuiop", "asdfghjkl#", "zxcvbnm.-_" };
        var panel = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Contained);

        foreach (string row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (char c in row)
            {
                Button key = MakeKey(c.ToString(), 52, () => Type(_keyboardShift ? char.ToUpperInvariant(c).ToString() : c.ToString()));
                _letterKeys.Add(key);
                line.Children.Add(key);
            }
            panel.Children.Add(line);
        }

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        bottom.Children.Add(MakeKey("⇧ Shift", 110, ToggleShift));
        bottom.Children.Add(MakeKey("Space", 230, () => Type(" ")));
        bottom.Children.Add(MakeKey("⌫", 80, Backspace));
        bottom.Children.Add(MakeKey("Done", 110, CloseKeyboard));
        panel.Children.Add(bottom);

        panel.Children.Add(new TextBlock
        {
            Text = "✕ type   □ delete   △ space   ○ close",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAD, 0xB9)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var keyboard = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0A, 0x1A, 0x3A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(keyboard, 2);
        var root = (Grid)FocusOverlay.Parent;
        root.Children.Insert(root.Children.IndexOf(FocusOverlay), keyboard);
        return keyboard;
    }

    private Button MakeKey(string label, double width, Action action)
    {
        var key = new Button
        {
            Content = label,
            Width = width,
            Height = 44,
            Margin = new Thickness(3),
            FontSize = 16,
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        key.Click += (_, _) => action();
        return key;
    }

    private void Type(string text)
    {
        if (_keyboardTarget is null || _keyboardTarget.IsReadOnly)
        {
            return;
        }

        int caret = _keyboardTarget.CaretIndex;
        _keyboardTarget.Text = _keyboardTarget.Text.Insert(caret, text);
        _keyboardTarget.CaretIndex = caret + text.Length;
    }

    private void Backspace()
    {
        if (_keyboardTarget is null || _keyboardTarget.Text.Length == 0)
        {
            return;
        }

        int caret = Math.Max(1, _keyboardTarget.CaretIndex == 0 ? _keyboardTarget.Text.Length : _keyboardTarget.CaretIndex);
        _keyboardTarget.Text = _keyboardTarget.Text.Remove(caret - 1, 1);
        _keyboardTarget.CaretIndex = caret - 1;
    }

    private void ToggleShift()
    {
        _keyboardShift = !_keyboardShift;
        foreach (Button key in _letterKeys)
        {
            string s = (string)key.Content;
            key.Content = _keyboardShift ? s.ToUpperInvariant() : s.ToLowerInvariant();
        }
    }

    // Returns true when the keyboard handled the controller button.
    private bool HandleKeyboardNav(string button)
    {
        if (!KeyboardOpen)
        {
            return false;
        }

        switch (button)
        {
            case "Circle":
                CloseKeyboard();
                return true;
            case "Square":
                Backspace();
                return true;
            case "Triangle":
                Type(" ");
                return true;
            case "L1":
            case "R1":
                return true; // stay in the keyboard
            default:
                return false; // D-pad and Cross work like everywhere else
        }
    }
}
