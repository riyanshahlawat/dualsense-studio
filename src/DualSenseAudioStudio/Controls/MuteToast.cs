using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DualSenseAudioStudio.Controls;

// PS5-style mic mute pop-up: small top-center card shown over any app or game,
// without taking focus, then fades out.
internal sealed class MuteToast : Window
{
    private static MuteToast? _current;

    private const string MicGeometry =
        "M12,2 A3,3 0 0 1 15,5 L15,11 A3,3 0 0 1 9,11 L9,5 A3,3 0 0 1 12,2 Z M5,10 A7,7 0 0 0 19,10 M12,17 L12,21 M8,21 L16,21";

    private readonly DispatcherTimer _hide;

    private MuteToast(bool muted)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        var icon = new Grid { Width = 26, Height = 26 };
        icon.Children.Add(new Path
        {
            Data = Geometry.Parse(MicGeometry),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        if (muted)
        {
            icon.Children.Add(new Line
            {
                X1 = 2, Y1 = 2, X2 = 24, Y2 = 24,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F)),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = muted
                ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x8A, 0x1F))
                : new SolidColorBrush(Color.FromArgb(0x40, 0x1F, 0x8F, 0xFF)),
            Child = icon
        };

        var text = new TextBlock
        {
            Text = muted ? "Microphone muted" : "Microphone on",
            Foreground = Brushes.White,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 6, 0)
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(badge);
        row.Children.Add(text);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0B, 0x1C, 0x3D)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(30),
            Padding = new Thickness(10, 10, 22, 10),
            Margin = new Thickness(20),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 }
        };

        Loaded += (_, _) =>
        {
            Rect area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + 24;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        };

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        _hide.Start();
    }

    public static void Show(bool muted)
    {
        _current?.Close();
        _current = new MuteToast(muted);
        _current.Closed += (s, _) => { if (ReferenceEquals(_current, s)) _current = null; };
        _current.Show();
    }
}
