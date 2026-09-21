using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoiceCapture.Windows;

public sealed class OverlayWindow : Window
{
    private readonly TextBlock label;
    private readonly DispatcherTimer hideTimer;
    public OverlayWindow()
    {
        Width = 430; Height = 78;
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        label = new TextBlock { Foreground = Brushes.White, FontSize = 15, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 10, 18, 10) };
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(242, 25, 31, 43)),
            CornerRadius = new CornerRadius(14), Child = label, BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(67, 86, 115)) };
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, Native.GetWindowLongPtr(handle, -20) | 0x08000000 | 0x20 | 0x80);
        };
        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Hide(); };
    }
    public void Display(string message, bool temporary = false)
    {
        hideTimer.Stop(); label.Text = message;
        var screen = System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = screen.Left / dpi.DpiScaleX + (screen.Width / dpi.DpiScaleX - Width) / 2;
        Top = screen.Bottom / dpi.DpiScaleY - Height - 35;
        Show();
        if (temporary) hideTimer.Start();
    }
}
