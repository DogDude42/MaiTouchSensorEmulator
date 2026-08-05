using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpfMaiTouchEmulator;

internal sealed class InputSurfaceHost : HwndHost
{
    private readonly TouchPanel _owner;
    private IntPtr _hwnd;
    private HwndSourceHook? _hook; // not used, but kept for parity

    public InputSurfaceHost(TouchPanel owner)
    {
        _owner = owner;
        Focusable = false;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var wc = new WNDCLASS();
        wc.style = 0;
        wc.lpfnWndProc = _wndProcDelegate;
        wc.cbClsExtra = 0;
        wc.cbWndExtra = 0;
        wc.hInstance = IntPtr.Zero;
        wc.hIcon = IntPtr.Zero;
        wc.hCursor = IntPtr.Zero;
        wc.hbrBackground = IntPtr.Zero;
        wc.lpszMenuName = null;
        wc.lpszClassName = "InputSurfaceHostWnd";
        ushort classAtom = RegisterClass(ref wc);
        if (classAtom == 0)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Warn($"RegisterClass failed err={err}");
        }

        const int WS_CHILD = 0x40000000;
        const int WS_VISIBLE = 0x10000000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_NOPARENTNOTIFY = 0x00000004;
        const int WS_EX_TRANSPARENT = 0x00000020; // paint-transparent; still receives input

        _hwnd = CreateWindowEx(
            WS_EX_NOACTIVATE | WS_EX_NOPARENTNOTIFY | WS_EX_TRANSPARENT,
            wc.lpszClassName,
            string.Empty,
            WS_CHILD | WS_VISIBLE,
            0, 0,
            0, 0,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Logger.Error("CreateWindowEx for InputSurfaceHost failed");
        }
        else
        {
            // WM_POINTER fires by default on Win8+; no registration needed.
            // Do NOT call RegisterTouchWindow — it enables WM_TOUCH which duplicates
            // contacts with different IDs than WM_POINTER, causing desync/phantom touches.
            Logger.Info("InputSurfaceHost child window created (WM_POINTER only)");
        }

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int WM_POINTERUPDATE = 0x0245;
        const int WM_POINTERDOWN = 0x0246;
        const int WM_POINTERUP = 0x0247;
        // WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC - disable system gestures (tap, hold, flick, etc.)
        const int WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
        // Tablet gesture disable flags (per tpcshrd.h)
        const uint TABLET_DISABLE_PRESSANDHOLD = 0x00000001;
        const uint TABLET_DISABLE_PENTAPFEEDBACK = 0x00000008;
        const uint TABLET_DISABLE_PENBARRELFEEDBACK = 0x00000010;
        const uint TABLET_DISABLE_TOUCHSWITCH = 0x00008000; // disables edge swipe (virtual desktop switch)
        // NOTE: Do NOT set TOUCHUIFORCEON (0x100) or TOUCHUIFORCEOFF (0x200) - breaks touch input
        const uint TABLET_DISABLE_FLICKS = 0x00010000;
        const uint TABLET_DISABLE_SMOOTHSCROLLING = 0x00080000;
        // Only disable gestures that interfere with gameplay - keep touch input functional
        const uint TABLET_DISABLE_GAME_GESTURES = TABLET_DISABLE_PRESSANDHOLD | 
                                                   TABLET_DISABLE_PENTAPFEEDBACK | 
                                                   TABLET_DISABLE_PENBARRELFEEDBACK | 
                                                   TABLET_DISABLE_TOUCHSWITCH | 
                                                   TABLET_DISABLE_FLICKS | 
                                                   TABLET_DISABLE_SMOOTHSCROLLING;

        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(HTCLIENT);
        }
        else if (msg == WM_TABLET_QUERYSYSTEMGESTURESTATUS)
        {
            // Disable only game-interfering gestures, keep touch input functional
            handled = true;
            return new IntPtr(TABLET_DISABLE_GAME_GESTURES);
        }
        else if (msg == WM_POINTERDOWN || msg == WM_POINTERUPDATE || msg == WM_POINTERUP)
        {
            uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
            if (GetPointerInfo(pointerId, out var pi))
            {
                double sx = pi.ptPixelLocation.X;
                double sy = pi.ptPixelLocation.Y;
                if (msg == WM_POINTERDOWN)
                {
                    InputTracer.Event("POINTER", $"DOWN id={pointerId} at=({sx},{sy}) flags=0x{pi.pointerFlags:X}");
                    _owner.HostPointerDown(pointerId, sx, sy);
                }
                else if (msg == WM_POINTERUPDATE)
                {
                    InputTracer.Event("POINTER", $"MOVE id={pointerId} at=({sx},{sy}) flags=0x{pi.pointerFlags:X}");
                    _owner.HostPointerMove(pointerId, sx, sy);
                }
                else
                {
                    InputTracer.Event("POINTER", $"UP   id={pointerId} at=({sx},{sy}) flags=0x{pi.pointerFlags:X}{(  (pi.pointerFlags & 0x8) != 0 ? " [CANCELED]" : "")}");
                    _owner.HostPointerUp(pointerId, sx, sy);
                }
            }
            // CRITICAL: Return 0 (don't call DefWindowProc) to prevent mouse message promotion
            // and WM_LBUTTONDBLCLK generation from rapid taps
            handled = true;
            return IntPtr.Zero;
        }
        else if (msg == 0x0014) // WM_ERASEBKGND
        {
            handled = true; // prevent background erase (keeps content behind visible)
            return new IntPtr(1);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_hwnd != IntPtr.Zero)
        {
            MoveWindow(_hwnd, 0, 0, (int)rcBoundingBox.Width, (int)rcBoundingBox.Height, true);
        }
    }

    // P/Invoke
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProcDelegate _wndProcDelegate = DefWindowProc;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}