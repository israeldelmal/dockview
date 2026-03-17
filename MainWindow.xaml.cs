using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Dockview.ViewModels;

namespace Dockview;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref uint value, uint size);
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DEFAULT    = 0;
    private const uint DWMWCP_DONOTROUND = 1;

    private void SetWindowCorners(uint preference)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(uint));
    }

    private readonly MainViewModel _vm;

    // Auto-hide: hide controls after 3 s of mouse inactivity in fullscreen
    private readonly DispatcherTimer _hideTimer;
    private bool _controlsVisible = true;
    private bool _isFullscreen    = false;

    // Saved window state for fullscreen toggle
    private WindowState  _savedState  = WindowState.Normal;
    private WindowStyle  _savedStyle  = WindowStyle.SingleBorderWindow;
    private ResizeMode   _savedResize = ResizeMode.CanResize;
    private double       _savedLeft, _savedTop, _savedWidth, _savedHeight;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideTimer.Tick += (_, _) => SetControlsVisible(false);

        StateChanged += (_, _) =>
            MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    // ── Fullscreen toggle ─────────────────────────────────────────────────────

    private void PresetButton_Click(object sender, RoutedEventArgs e)
        => _vm.CyclePreset();

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
            EnterFullscreen();
        else
            ExitFullscreen();
    }

    private void EnterFullscreen()
    {
        // Save current window geometry
        _savedState  = WindowState;
        _savedStyle  = WindowStyle;
        _savedResize = ResizeMode;
        _savedLeft   = Left;
        _savedTop    = Top;
        _savedWidth  = Width;
        _savedHeight = Height;

        // True fullscreen: cover the entire screen including taskbar
        WindowStyle  = WindowStyle.None;
        ResizeMode   = ResizeMode.NoResize;
        WindowState  = WindowState.Normal;
        Topmost      = true;
        Left         = 0;
        Top          = 0;
        Width        = SystemParameters.PrimaryScreenWidth;
        Height       = SystemParameters.PrimaryScreenHeight;

        SetWindowCorners(DWMWCP_DONOTROUND);
        _isFullscreen = true;
        _hideTimer.Start();
        SetControlsVisible(true);
    }

    private void ExitFullscreen()
    {
        _hideTimer.Stop();
        SetControlsVisible(true);

        Topmost      = false;
        WindowState  = _savedState;
        WindowStyle  = _savedStyle;
        ResizeMode   = _savedResize;
        Left         = _savedLeft;
        Top          = _savedTop;
        Width        = _savedWidth;
        Height       = _savedHeight;

        SetWindowCorners(DWMWCP_DEFAULT);
        _isFullscreen = false;
    }

    // ── Controls auto-hide ────────────────────────────────────────────────────

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;

        SetControlsVisible(true);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void SetControlsVisible(bool visible)
    {
        _controlsVisible = visible;
        double opacity   = visible ? 1.0 : 0.0;

        // Animate opacity for a smooth fade
        FadeElement(TopBar,        opacity);
        FadeElement(BottomOverlay, opacity);

        Cursor = visible ? Cursors.Arrow : Cursors.None;
    }

    private static void FadeElement(FrameworkElement el, double to)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(to,
            System.Windows.Duration.Automatic)
        {
            Duration  = new System.Windows.Duration(TimeSpan.FromMilliseconds(200)),
        };
        el.BeginAnimation(OpacityProperty, anim);
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case Key.Escape when _isFullscreen:
                ExitFullscreen();
                e.Handled = true;
                break;

            // Volume shortcuts
            case Key.Up:
                _vm.Volume = Math.Min(1.0, _vm.Volume + 0.05);
                e.Handled = true;
                break;
            case Key.Down:
                _vm.Volume = Math.Max(0.0, _vm.Volume - 0.05);
                e.Handled = true;
                break;
        }
    }

    // ── Caption buttons ───────────────────────────────────────────────────────

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => SystemCommands.MinimizeWindow(this);

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => SystemCommands.CloseWindow(this);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _hideTimer.Stop();
        _vm.SaveSettings();
        _vm.Dispose();
    }
}
