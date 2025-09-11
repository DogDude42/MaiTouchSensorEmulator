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
            // Register for touch on child; should succeed
            const uint TWF_FINETOUCH = 0x00000001;
            const uint TWF_WANTPALM = 0x00000002;
            if (!RegisterTouchWindow(_hwnd, TWF_FINETOUCH | TWF_WANTPALM))
            {
                Logger.Warn($"RegisterTouchWindow(child) failed err={Marshal.GetLastWin32Error()}");
            }
            else
            {
                Logger.Info("InputSurfaceHost child window created and registered for touch");
            }
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
        const int WM_TOUCH = 0x0240;
        const int WM_POINTERUPDATE = 0x0245;
        const int WM_POINTERDOWN = 0x0246;
        const int WM_POINTERUP = 0x0247;

        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(HTCLIENT);
        }
        else if (msg == WM_TOUCH)
        {
            int inputCount = (int)(wParam.ToInt64() & 0xFFFF);
            var inputs = new TOUCHINPUT[inputCount];
            if (GetTouchInputInfo(lParam, inputCount, inputs, Marshal.SizeOf(typeof(TOUCHINPUT))))
            {
                for (int i = 0; i < inputCount; i++)
                {
                    var ti = inputs[i];
                    double sx = ti.x / 100.0;
                    double sy = ti.y / 100.0;
                    uint id = ti.dwID;
                    if ((ti.dwFlags & TOUCHEVENTF_DOWN) != 0)
                    {
                        Logger.Info($"[Host] WM_TOUCH DOWN id={id} at=({sx:F1},{sy:F1})");
                        _owner.HostPointerDown(id, sx, sy);
                    }
                    else if ((ti.dwFlags & TOUCHEVENTF_MOVE) != 0)
                        _owner.HostPointerMove(id, sx, sy);
                    else if ((ti.dwFlags & TOUCHEVENTF_UP) != 0)
                    {
                        Logger.Info($"[Host] WM_TOUCH UP id={id} at=({sx:F1},{sy:F1})");
                        _owner.HostPointerUp(id, sx, sy);
                    }
                }
                CloseTouchInputHandle(lParam);
                handled = true;
            }
        }
        else if (msg == WM_POINTERDOWN || msg == WM_POINTERUPDATE || msg == WM_POINTERUP)
        {
            uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
            if (GetPointerInfo(pointerId, out var pi))
            {
                double sx = pi.ptPixelLocation.X;
                double sy = pi.ptPixelLocation.Y;
                if (msg == WM_POINTERDOWN) { Logger.Info($"[Host] WM_POINTER DOWN id={pointerId} at=({sx},{sy})"); _owner.HostPointerDown(pointerId, sx, sy); }
                else if (msg == WM_POINTERUPDATE) { _owner.HostPointerMove(pointerId, sx, sy); }
                else { Logger.Info($"[Host] WM_POINTER UP id={pointerId} at=({sx},{sy})"); _owner.HostPointerUp(pointerId, sx, sy); }
                handled = true;
            }
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
    private static extern bool RegisterTouchWindow(IntPtr hWnd, uint ulFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetTouchInputInfo(IntPtr hTouchInput, int cInputs, [In, Out] TOUCHINPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseTouchInputHandle(IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOUCHINPUT
    {
        public int x;
        public int y;
        public IntPtr hSource;
        public uint dwID;
        public uint dwFlags;
        public uint dwMask;
        public uint dwTime;
        public IntPtr dwExtraInfo;
        public uint cxContact;
        public uint cyContact;
    }

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

    private const uint TOUCHEVENTF_MOVE = 0x0001;
    private const uint TOUCHEVENTF_DOWN = 0x0002;
    private const uint TOUCHEVENTF_UP = 0x0004;

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
