using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfMaiTouchEmulator.Managers;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Numerics;

namespace WpfMaiTouchEmulator;
/// <summary>
/// Interaction logic for TouchPanel.xaml
/// </summary>
public partial class TouchPanel : Window
{
    internal Action<TouchValue>? onTouch;
    internal Action<TouchValue>? onRelease;
    internal Action? onInitialReposition;

    private readonly TouchPanelPositionManager _positionManager;
    private List<Polygon> buttons = [];
    private bool isDebugEnabled = Properties.Settings.Default.IsDebugEnabled;
    private bool isRingButtonEmulationEnabled = Properties.Settings.Default.IsRingButtonEmulationEnabled;
    private bool hasRepositioned = false;

    // New optimized touch engine
    private TouchSensorEngine? _touchEngine;

    // Legacy fields for compatibility with MainWindow
    private readonly Dictionary<int, (Polygon polygon, Point lastPoint)> activeTouches = [];
    private readonly Dictionary<uint, Ellipse> _debugEllipses = [];
    private readonly Dictionary<uint, PointerTrack> _pointerStates = [];
    private readonly Dictionary<TouchValue, Polygon> _polygonByValue = [];

    private sealed class PointerTrack
    {
        public Point Last { get; set; }
        public ulong CurrentMask { get; set; }
    }

    private enum ResizeDirection
    {
        BottomRight = 8,
    }


    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Input is handled exclusively in InputSurfaceHost


    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Internal action constants for host → panel routing
    private const int ACT_DOWN = 1;
    private const int ACT_MOVE = 2;
    private const int ACT_UP = 3;

    // (No WM_POINTER structures here)

    // (No WM_TOUCH interop here)

    public enum SizingEdge
    {
        Left = 1,
        Right = 2,
        Top = 3,
        TopLeft = 4,
        TopRight = 5,
        Bottom = 6,
        BottomLeft = 7,
        BottomRight = 8
    }

    private const double FixedAspectRatio = 720.0 / 1280.0; // width / height
    private const int MinWidth = 180;
    private const int MinHeight = 320;

    public TouchPanel()
    {
        InitializeComponent();
        Topmost = true;
        _positionManager = new TouchPanelPositionManager();
        Loaded += Window_Loaded;
        // Keep popup overlay anchored when the panel resizes
        SizeChanged += (_, __) => PositionResizeGripPopup();
        // Track window movement (DragMove) so popup follows
        LocationChanged += (_, __) => ForcePopupReposition();
        // In case only inner border changes under Viewbox
        this.Loaded += (_, __) => touchPanelBorder.SizeChanged += (_, __) => PositionResizeGripPopup();
        // Replaced legacy WPF Touch path with low-latency WM_POINTER handling
        // Touch.FrameReported += OnTouchFrameReported;
        try
        {
            Properties.Settings.Default.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Properties.Settings.Default.ContactRadiusPx) ||
                    e.PropertyName == nameof(Properties.Settings.Default.ButtonContactRadiusPx))
                {
                    UpdateEngineRadii();
                }
            };
        }
        catch { }
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Disable WPF Stylus/Touch pipeline to avoid latency
        try { System.AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true); } catch { }

        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        if (msg == WM_SIZING)
        {
            var rect = Marshal.PtrToStructure<RECT>(lParam);
            var edge = (SizingEdge)wParam.ToInt32();
            EnforceAspectRatio(ref rect, edge);
            Marshal.StructureToPtr(rect, lParam, true);
            handled = true;
        }
        else if (msg == 0x0084 /* WM_NCHITTEST */)
        {
            // Force the entire window surface to be interactive for hit-testing.
            // This helps ensure touch targets this window rather than passing through.
            handled = true;
            return new IntPtr(1 /* HTCLIENT */);
        }
        return IntPtr.Zero;
    }

    // Entry points for the native child host
    internal void HostPointerDown(uint id, double screenX, double screenY) => ProcessScreenPointer(id, ACT_DOWN, new Point(screenX, screenY));
    internal void HostPointerMove(uint id, double screenX, double screenY) => ProcessScreenPointer(id, ACT_MOVE, new Point(screenX, screenY));
    internal void HostPointerUp(uint id, double screenX, double screenY) => ProcessScreenPointer(id, ACT_UP, new Point(screenX, screenY));

    private void ProcessScreenPointer(uint id, int action, Point screenPoint)
    {
        var canvasPoint = TouchCanvas.PointFromScreen(screenPoint);
        if (_touchEngine == null) return;

        if (action == ACT_DOWN)
            _touchEngine.PointerDown((uint)id, canvasPoint);
        else if (action == ACT_MOVE)
            _touchEngine.PointerMove((uint)id, canvasPoint);
        else if (action == ACT_UP)
            _touchEngine.PointerUp((uint)id, canvasPoint);
    }

    private void EnforceAspectRatio(ref RECT rect, SizingEdge edge)
    {
        var currentWidth = rect.Right - rect.Left;
        var currentHeight = rect.Bottom - rect.Top;
        int newWidth, newHeight;

        if (edge == SizingEdge.BottomRight)
        {
            newWidth = (int)(currentHeight * FixedAspectRatio);
            newHeight = currentHeight;
        }
        else
        {
            newHeight = (int)(currentWidth / FixedAspectRatio);
            newWidth = currentWidth;
        }

        // Enforce minimum size while keeping the aspect ratio.
        if (newWidth < MinWidth)
        {
            newWidth = MinWidth;
            newHeight = (int)(newWidth / FixedAspectRatio);
        }
        if (newHeight < MinHeight)
        {
            newHeight = MinHeight;
            newWidth = (int)(newHeight * FixedAspectRatio);
        }

        rect.Right = rect.Left + newWidth;
        rect.Bottom = rect.Top + newHeight;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Find all sensor polygons
        buttons = VisualTreeHelperExtensions.FindVisualChildren<Polygon>(this);
        var polygons = new System.Collections.Generic.Dictionary<TouchValue, Polygon>();
        foreach (var p in buttons)
        {
            if (p.Tag is TouchValue tv)
                polygons[tv] = p;
        }

        // Initialize the new touch engine
        _touchEngine = new TouchSensorEngine(polygons,
            Properties.Settings.Default.ContactRadiusPx,
            Properties.Settings.Default.ButtonContactRadiusPx);

        _touchEngine.OnPress = tv =>
        {
            onTouch?.Invoke(tv);
            if (isRingButtonEmulationEnabled && RingButtonEmulator.HasRingButtonMapping(tv))
            {
                RingButtonEmulator.PressButton(tv);
            }
        };

        _touchEngine.OnRelease = tv =>
        {
            onRelease?.Invoke(tv);
            if (isRingButtonEmulationEnabled)
            {
                RingButtonEmulator.ReleaseButton(tv);
            }
        };

        // Wire up tracing
        _touchEngine.Trace = (category, msg) => InputTracer.Event(category, msg);

        try
        {
            var host = new InputSurfaceHost(this)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = double.NaN,
                Height = double.NaN,
            };
            // Set lower Z-index so ResizeGrip appears on top
            Panel.SetZIndex(host, 0);

            // Add the InputSurfaceHost to the TouchGrid - it will render behind ResizeGrip due to Z-index
            TouchGrid.Children.Add(host);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to create InputSurfaceHost", ex);
        }

        // Position the resize grip popup above the HwndHost (airspace-safe)
        PositionResizeGripPopup();
    }

    private void UpdateEngineRadii()
    {
        if (_touchEngine != null)
        {
            _touchEngine.SetRadii(
                Math.Max(0, Properties.Settings.Default.ContactRadiusPx),
                Math.Max(0, Properties.Settings.Default.ButtonContactRadiusPx));
        }
    }

    public void PositionTouchPanel()
    {
        var position = _positionManager.GetSinMaiWindowPosition();
        if (position != null &&
            (Top != position.Value.Top || Left != position.Value.Left || Width != position.Value.Width || Height != position.Value.Height)
            )
        {
            Logger.Info("Touch panel not over sinmai window, repositioning");
            Top = position.Value.Top;
            Left = position.Value.Left;
            Width = position.Value.Width;
            Height = position.Value.Height;

            if (!hasRepositioned)
            {
                hasRepositioned = true;
                onInitialReposition?.Invoke();
            }

            // Ensure popup repositions after programmatic move/resize
            PositionResizeGripPopup();
            ForcePopupReposition();
        }
    }

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // This event is for the draggable bar, it calls DragMove to move the window
        DragMove();
    }

    private void ResizeGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            ResizeWindow(SizingEdge.BottomRight);
        }
    }

    private void ResizeWindow(SizingEdge edge)
    {
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, 0x112, (IntPtr)(0xF000 + (int)edge), IntPtr.Zero);
    }

    private void PositionResizeGripPopup()
    {
        if (ResizeGripPopup == null || ResizeGrip == null || touchPanelBorder == null)
            return;

        // Fallback sizes if not measured yet
        double gripW = double.IsNaN(ResizeGrip.ActualWidth) || ResizeGrip.ActualWidth == 0 ? 150 : ResizeGrip.ActualWidth;
        double gripH = double.IsNaN(ResizeGrip.ActualHeight) || ResizeGrip.ActualHeight == 0 ? 90 : ResizeGrip.ActualHeight;

        double panelW = touchPanelBorder.ActualWidth;
        double panelH = touchPanelBorder.ActualHeight;

        // Keep a small inset equal to the border thickness used in XAML (10)
        double insetX = touchPanelBorder.BorderThickness.Left;
        double insetY = touchPanelBorder.BorderThickness.Bottom;

        // Place popup relative to the panel's top-left corner
        double offsetX = Math.Max(0, panelW - (gripW + insetX));
        double offsetY = Math.Max(0, panelH - (gripH + insetY));

        ResizeGripPopup.HorizontalOffset = offsetX;
        ResizeGripPopup.VerticalOffset = offsetY;
        if (!ResizeGripPopup.IsOpen) ResizeGripPopup.IsOpen = true;
    }

    private void ForcePopupReposition()
    {
        if (ResizeGripPopup == null) return;
        // Nudge offsets to force WPF to recalc popup position (transparent window quirk)
        double ho = ResizeGripPopup.HorizontalOffset;
        ResizeGripPopup.HorizontalOffset = ho + 0.1;
        ResizeGripPopup.HorizontalOffset = ho;
    }

    // Legacy WPF Touch Frame path removed; input handled by native host

    public void SetBorderMode(BorderSetting borderSetting, string borderColour)
    {
        if (borderSetting == BorderSetting.Rainbow)
        {
            var rotateTransform = new RotateTransform { CenterX = 0.5, CenterY = 0.5 };
            touchPanelBorder.BorderBrush = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(@"pack://application:,,,/Assets/conicalGradient.png")),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                RelativeTransform = rotateTransform,
            };

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(10)),
                RepeatBehavior = RepeatBehavior.Forever
            };

            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
            return;
        }
        else if (borderSetting == BorderSetting.Solid)
        {
            try
            {
                var colour = (Color)ColorConverter.ConvertFromString(borderColour);
                touchPanelBorder.BorderBrush = new SolidColorBrush { Color = colour };
                return;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to parse solid colour", ex);
            }
        }
        touchPanelBorder.BorderBrush = null;
    }

    public void SetEmulateRingButton(bool enabled)
    {
        isRingButtonEmulationEnabled = enabled;
    }

    private void HighlightElement(Polygon element, bool highlight)
    {
        if (isDebugEnabled)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                element.Opacity = highlight ? 0.8 : 0.3;
            });
        }
    }

    public void SetDebugMode(bool enabled)
    {
        isDebugEnabled = enabled;
        if (enabled)
        {
            InputTracer.Start();
            // Show actual sensor polygons as debug visualization
            foreach (var sensor in _touchEngine.GetSensorPolygons())
            {
                var poly = new Polygon
                {
                    Points = new PointCollection(sensor.Points),
                    Stroke = sensor.IsButton ? Brushes.Red : Brushes.Blue,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(sensor.IsButton ? Color.FromArgb(30, 255, 0, 0) : Color.FromArgb(30, 0, 0, 255)),
                    IsHitTestVisible = false,
                };
                TouchCanvas.Children.Add(poly);
            }
            // Also show Voronoi centroids
            foreach (var centroid in _touchEngine.GetCentroids())
            {
                var ellipse = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.Yellow,
                    Stroke = Brushes.Orange,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(ellipse, centroid.X - 4);
                Canvas.SetTop(ellipse, centroid.Y - 4);
                TouchCanvas.Children.Add(ellipse);
            }
        }
        else
        {
            TouchCanvas.Children.Clear();
        }
        InputTracer.Enabled = enabled;
        buttons.ForEach(button =>
        {
            button.Opacity = enabled ? 0.3 : 0;
        });
    }

    public void SetLargeButtonMode(bool enabled)
    {
        TouchValue[] ringButtonsValues = {
            TouchValue.A1,
            TouchValue.A2,
            TouchValue.A3,
            TouchValue.A4,
            TouchValue.A5,
            TouchValue.A6,
            TouchValue.A7,
            TouchValue.A8,
            TouchValue.D1,
            TouchValue.D2,
            TouchValue.D3,
            TouchValue.D4,
            TouchValue.D5,
            TouchValue.D6,
            TouchValue.D7,
            TouchValue.D8,
        };

        var a1 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A1);
        var a2 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A2);
        var a3 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A3);
        var a4 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A4);
        var a5 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A5);
        var a6 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A6);
        var a7 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A7);
        var a8 = buttons.First(button => (TouchValue)button.Tag == TouchValue.A8);
        var d1 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D1);
        var d2 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D2);
        var d3 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D3);
        var d4 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D4);
        var d5 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D5);
        var d6 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D6);
        var d7 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D7);
        var d8 = buttons.First(button => (TouchValue)button.Tag == TouchValue.D8);

        if (enabled)
        {
            d1.Points =
            [
                new Point(-5, -50),
                new Point(205, -50),
                new Point(165, 253),
                new Point(100, 188),
                new Point(35, 253),
            ];

            a1.Points =
            [
                new Point(495, -50),
                new Point(208, 338),
                new Point(145, 338),
                new Point(49, 297),
                new Point(0, 249),
                new Point(42, -55),
            ];
            d2.Points =
            [
                new Point(290, -182),
                new Point(500, -180),
                new Point(500, -5),
                new Point(96, 297),
                new Point(96, 205),
                new Point(0, 205),
            ];
            a2.Points =
            [
                new Point(405, 317),
                new Point(91, 362),
                new Point(42, 314),
                new Point(0, 219),
                new Point(0, 150),
                new Point(405, -150),
            ];
            d3.Points =
            [
                new Point(315, -10),
                new Point(315, 208),
                new Point(0, 165),
                new Point(65, 100),
                new Point(0, 35),
            ];
            a3.Points =
            [
                new Point(406, 520),
                new Point(0, 213),
                new Point(0, 144),
                new Point(41, 48),
                new Point(89, 0),
                new Point(406, 43),
            ];
            d4.Points =
            [
                new Point(500, 309),
                new Point(500, 491),
                new Point(305, 491),
                new Point(0, 92),
                new Point(92, 92),
                new Point(92, 0),
            ];
            a4.Points =
            [
                new Point(45, 400),
                new Point(0, 83),
                new Point(48, 35),
                new Point(144, 0),
                new Point(212, 0),
                new Point(515, 400),
            ];
            d5.Points =
            [
                new Point(208, 317),
                new Point(-10, 317),
                new Point(34, 0),
                new Point(99, 65),
                new Point(164, 0),
            ];

            a5.Points =
            [
                new Point(317, 400),
                new Point(363, 83),
                new Point(316, 35),
                new Point(220, 0),
                new Point(152, 0),
                new Point(-150, 400),
            ];
            d6.Points =
            [
                new Point(-10, 492),
                new Point(-200, 492),
                new Point(-200, 295),
                new Point(199, 0),
                new Point(199, 92),
                new Point(291, 92),
            ];
            a6.Points =
            [
                new Point(-67, 505),
                new Point(333, 214),
                new Point(333, 144),
                new Point(296, 48),
                new Point(248, 0),
                new Point(-67, 45),
            ];

            d7.Points =
            [
                new Point(-60, 207),
                new Point(-60, -7),
                new Point(253, 34),
                new Point(188, 99),
                new Point(253, 164),
            ];

            a7.Points =
            [
                new Point(-65, 320),
                new Point(248, 362),
                new Point(297, 314),
                new Point(333, 219),
                new Point(333, 151),
                new Point(-65, -150),
            ];
            d8.Points =
            [
                new Point(-195, -10),
                new Point(-195, -195),
                new Point(-5, -195),
                new Point(298, 199),
                new Point(200, 199),
                new Point(200, 291),
            ];

            a8.Points =
            [
                new Point(-148, -55),
                new Point(153, 338),
                new Point(215, 338),
                new Point(311, 297),
                new Point(359, 249),
                new Point(318, -55),
            ];
        }
        else
        {
            d1.Points =
            [
                new Point(0, 5),
                new Point(50, 2),
                new Point(100, 0),
                new Point(150, 2),
                new Point(200, 5),
                new Point(165, 253),
                new Point(100, 188),
                new Point(35, 253),
            ];

            a1.Points =
            [
                new Point(150, 28),
                new Point(245, 65),
                new Point(360, 133),
                new Point(208, 338),
                new Point(145, 338),
                new Point(49, 297),
                new Point(0, 249),
                new Point(35, 0),
            ];

            d2.Points =
            [
                new Point(153, 0),
                new Point(187, 32),
                new Point(225, 67),
                new Point(259, 104),
                new Point(295, 147),
                new Point(96, 297),
                new Point(96, 205),
                new Point(0, 205),
            ];

            a2.Points =
            [
                new Point(261, 101),
                new Point(303, 195),
                new Point(339, 327),
                new Point(91, 362),
                new Point(42, 314),
                new Point(0, 219),
                new Point(0, 150),
                new Point(202, 0),
            ];

            d3.Points =
            [
                new Point(248, 0),
                new Point(251, 48),
                new Point(253, 100),
                new Point(251, 150),
                new Point(247, 199),
                new Point(0, 165),
                new Point(65, 100),
                new Point(0, 35),
            ];

            a3.Points =
            [
                new Point(305, 150),
                new Point(269, 246),
                new Point(201, 364),
                new Point(0, 213),
                new Point(0, 144),
                new Point(41, 48),
                new Point(89, 0),
                new Point(337, 34),
            ];

            d4.Points =
            [
                new Point(292, 151),
                new Point(260, 187),
                new Point(225, 225),
                new Point(188, 259),
                new Point(151, 291),
                new Point(0, 92),
                new Point(92, 92),
                new Point(92, 0),
            ];

            a4.Points =
            [
                new Point(260, 259),
                new Point(167, 301),
                new Point(37, 335),
                new Point(0, 83),
                new Point(48, 35),
                new Point(144, 0),
                new Point(212, 0),
                new Point(364, 200),
            ];

            d5.Points =
            [
                new Point(199, 252),
                new Point(151, 255),
                new Point(99, 257),
                new Point(49, 255),
                new Point(0, 252),
                new Point(34, 0),
                new Point(99, 65),
                new Point(164, 0),
            ];

            a5.Points =
            [
                new Point(104, 259),
                new Point(197, 301),
                new Point(327, 335),
                new Point(363, 83),
                new Point(316, 35),
                new Point(220, 0),
                new Point(152, 0),
                new Point(0, 201),
            ];

            d6.Points =
            [
                new Point(140, 292),
                new Point(104, 260),
                new Point(66, 225),
                new Point(32, 188),
                new Point(0, 151),
                new Point(199, 0),
                new Point(199, 92),
                new Point(291, 92),
            ];

            a6.Points =
            [
                new Point(32, 150),
                new Point(68, 246),
                new Point(133, 365),
                new Point(333, 214),
                new Point(333, 144),
                new Point(296, 48),
                new Point(248, 0),
                new Point(0, 35),
            ];

            d7.Points =
            [
                new Point(5, 199),
                new Point(2, 151),
                new Point(0, 99),
                new Point(2, 49),
                new Point(6, 0),
                new Point(253, 34),
                new Point(188, 99),
                new Point(253, 164),
            ];

            a7.Points =
            [
                new Point(78, 101),
                new Point(36, 195),
                new Point(0, 327),
                new Point(248, 362),
                new Point(297, 314),
                new Point(333, 219),
                new Point(333, 151),
                new Point(132, 0),
            ];

            d8.Points =
            [
                new Point(0, 140),
                new Point(32, 104),
                new Point(67, 66),
                new Point(104, 32),
                new Point(145, 0),
                new Point(298, 199),
                new Point(200, 199),
                new Point(200, 291),
            ];

            a8.Points =
            [
                new Point(210, 28),
                new Point(115, 65),
                new Point(0, 138),
                new Point(153, 338),
                new Point(215, 338),
                new Point(311, 297),
                new Point(359, 249),
                new Point(324, 0),
            ];
        }
    }
}