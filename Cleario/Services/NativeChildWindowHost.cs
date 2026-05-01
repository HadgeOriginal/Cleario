using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation;
using WinRT.Interop;

namespace Cleario.Services
{
    /// <summary>
    /// Creates a real Win32 child HWND inside the Cleario window and gives that HWND to mpv via --wid.
    /// The child HWND is subclassed so Cleario can still receive mouse movement/clicks while mpv is
    /// rendering video into the HWND. The subclass also lets the parent WinUI window keep its resize
    /// border hit tests while the native video surface fills the player area.
    /// </summary>
    public sealed class NativeChildWindowHost : IDisposable
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        private const int GWLP_WNDPROC = -4;
        private const int GCLP_HCURSOR = -12;
        private const uint WM_SETCURSOR = 0x0020;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint WM_NCHITTEST = 0x0084;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const int IDC_ARROW = 32512;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int MA_ACTIVATE = 1;
        private const uint WM_NCLBUTTONDOWN = 0x00A1;
        private const int ResizeBorderPixels = 18;
        private const int RGN_DIFF = 4;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int dwExStyle,
            string lpClassName,
            string? lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
        private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll")]
        private static extern bool DestroyCursor(IntPtr hCursor);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private WindowProcDelegate? _windowProcDelegate;
        private readonly Dictionary<IntPtr, IntPtr> _subclassedChildWindows = new();
        private readonly Dictionary<IntPtr, IntPtr> _childOriginalClassCursors = new();
        private bool _refreshingChildCursorHooks;
        private IntPtr _parentHwnd = IntPtr.Zero;
        private IntPtr _lastFocusableChildWindow = IntPtr.Zero;
        private IntPtr _oldWindowProc = IntPtr.Zero;
        private DateTime _lastClickUtc = DateTime.MinValue;
        private bool _cursorHidden;
        private IntPtr _blankCursor = IntPtr.Zero;
        private int _nativeShowCursorHideBalance;

        public sealed class NativeKeyEventArgs : EventArgs
        {
            public NativeKeyEventArgs(int virtualKey)
            {
                VirtualKey = virtualKey;
            }

            public int VirtualKey { get; }
            public bool Handled { get; set; }
        }

        public event EventHandler? PointerActivity;
        public event EventHandler? Tapped;
        public event EventHandler? DoubleTapped;
        public event EventHandler<NativeKeyEventArgs>? KeyDown;

        public IntPtr WindowHandle { get; private set; } = IntPtr.Zero;

        public void EnsureCreated(Window owner, FrameworkElement boundsElement)
        {
            if (WindowHandle != IntPtr.Zero)
            {
                UpdateBounds(boundsElement);
                return;
            }

            var parentHwnd = WindowNative.GetWindowHandle(owner);
            if (parentHwnd == IntPtr.Zero)
                return;

            _parentHwnd = parentHwnd;

            WindowHandle = CreateWindowExW(
                0,
                "STATIC",
                "ClearioMpvVideoHost",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0,
                0,
                1,
                1,
                parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            SubclassWindow();
            UpdateBounds(boundsElement);
        }

        public void UpdateBounds(FrameworkElement boundsElement, double topInsetDip = 0, double bottomInsetDip = 0)
        {
            if (WindowHandle == IntPtr.Zero || boundsElement.XamlRoot == null)
                return;

            try
            {
                var position = boundsElement.TransformToVisual(null).TransformPoint(new Point(0, 0));
                var scale = boundsElement.XamlRoot.RasterizationScale;

                var availableWidthDip = Math.Max(1, boundsElement.ActualWidth);
                var availableHeightDip = Math.Max(1, boundsElement.ActualHeight);

                topInsetDip = Math.Clamp(topInsetDip, 0, Math.Max(0, availableHeightDip - 1));
                bottomInsetDip = Math.Clamp(bottomInsetDip, 0, Math.Max(0, availableHeightDip - topInsetDip - 1));

                var x = (int)Math.Round(position.X * scale);
                var y = (int)Math.Round((position.Y + topInsetDip) * scale);
                var width = Math.Max(1, (int)Math.Round(availableWidthDip * scale));
                var height = Math.Max(1, (int)Math.Round((availableHeightDip - topInsetDip - bottomInsetDip) * scale));

                SetWindowPos(WindowHandle, HWND_TOP, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                RefreshChildCursorHooks();
            }
            catch
            {
            }
        }

        public void UpdateClipExclusions(FrameworkElement boundsElement, IReadOnlyList<Rect> excludedRectsDip)
        {
            if (WindowHandle == IntPtr.Zero || boundsElement.XamlRoot == null)
                return;

            if (excludedRectsDip == null || excludedRectsDip.Count == 0)
            {
                ClearClipRegion();
                return;
            }

            IntPtr visibleRegion = IntPtr.Zero;
            try
            {
                var scale = boundsElement.XamlRoot.RasterizationScale;
                var width = Math.Max(1, (int)Math.Round(Math.Max(1, boundsElement.ActualWidth) * scale));
                var height = Math.Max(1, (int)Math.Round(Math.Max(1, boundsElement.ActualHeight) * scale));

                visibleRegion = CreateRectRgn(0, 0, width, height);
                if (visibleRegion == IntPtr.Zero)
                    return;

                foreach (var rect in excludedRectsDip)
                {
                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;

                    var left = Math.Clamp((int)Math.Floor(rect.X * scale), 0, width);
                    var top = Math.Clamp((int)Math.Floor(rect.Y * scale), 0, height);
                    var right = Math.Clamp((int)Math.Ceiling((rect.X + rect.Width) * scale), 0, width);
                    var bottom = Math.Clamp((int)Math.Ceiling((rect.Y + rect.Height) * scale), 0, height);

                    if (right <= left || bottom <= top)
                        continue;

                    var cutRegion = CreateRectRgn(left, top, right, bottom);
                    if (cutRegion == IntPtr.Zero)
                        continue;

                    try
                    {
                        CombineRgn(visibleRegion, visibleRegion, cutRegion, RGN_DIFF);
                    }
                    finally
                    {
                        DeleteObject(cutRegion);
                    }
                }

                // Windows owns visibleRegion after a successful SetWindowRgn call.
                if (SetWindowRgn(WindowHandle, visibleRegion, true) != 0)
                    visibleRegion = IntPtr.Zero;
            }
            catch
            {
            }
            finally
            {
                if (visibleRegion != IntPtr.Zero)
                    DeleteObject(visibleRegion);
            }
        }

        public void ClearClipRegion()
        {
            if (WindowHandle == IntPtr.Zero)
                return;

            try
            {
                SetWindowRgn(WindowHandle, IntPtr.Zero, true);
            }
            catch
            {
            }
        }

        public void Hide()
        {
            if (WindowHandle != IntPtr.Zero)
                ShowWindow(WindowHandle, 0);
        }

        public void Show()
        {
            if (WindowHandle != IntPtr.Zero)
            {
                ShowWindow(WindowHandle, 5);
                RefreshChildCursorHooks();
            }
        }

        public void FocusNative()
        {
            if (WindowHandle == IntPtr.Zero)
                return;

            try
            {
                // Only move focus inside Cleario. Do not call SetForegroundWindow/SetActiveWindow here:
                // mpv can emit mouse messages from its native child HWND while the user has switched to
                // another app, and forcing the foreground window makes Cleario steal keyboard input.
                RefreshChildCursorHooks();

                if (_lastFocusableChildWindow != IntPtr.Zero && IsWindow(_lastFocusableChildWindow))
                    SetFocus(_lastFocusableChildWindow);
                else
                    SetFocus(WindowHandle);
            }
            catch
            {
            }
        }

        public void SetCursorHidden(bool hidden)
        {
            _cursorHidden = hidden;

            if (hidden)
            {
                RefreshChildCursorHooks();
                ApplyHiddenCursor();
            }
            else
            {
                RestoreChildWindowProcs();
                RestoreChildClassCursors();
                ApplyVisibleCursor();
            }
        }

        private void ApplyCurrentCursor()
        {
            if (_cursorHidden)
                ApplyHiddenCursor();
            else
                ApplyVisibleCursor();
        }

        private void ApplyHiddenCursor()
        {
            try
            {
                RefreshChildCursorHooks();

                var cursor = GetBlankCursor();
                if (cursor != IntPtr.Zero)
                    SetCursor(cursor);

                // mpv owns its own native video window/thread in --wid mode. WinUI's
                // ProtectedCursor hides the pointer for XAML/LibVLC, but mpv can restore
                // the arrow from its child HWND. Force the native cursor counter down on
                // whichever thread is processing WM_SETCURSOR/WM_MOUSEMOVE.
                for (var i = 0; i < 16; i++)
                {
                    var counter = ShowCursor(false);
                    _nativeShowCursorHideBalance++;
                    if (counter < 0)
                        break;
                }
            }
            catch
            {
            }
        }

        private void ApplyVisibleCursor()
        {
            try
            {
                while (_nativeShowCursorHideBalance > 0)
                {
                    ShowCursor(true);
                    _nativeShowCursorHideBalance--;
                }

                var cursor = LoadCursor(IntPtr.Zero, IDC_ARROW);
                if (cursor != IntPtr.Zero)
                    SetCursor(cursor);
            }
            catch
            {
                _nativeShowCursorHideBalance = 0;
            }
        }

        public void Dispose()
        {
            _cursorHidden = false;
            ApplyVisibleCursor();
            RestoreChildClassCursors();
            RestoreChildWindowProcs();
            RestoreWindowProc();

            if (WindowHandle == IntPtr.Zero)
                return;

            ClearClipRegion();

            try
            {
                DestroyWindow(WindowHandle);
            }
            catch
            {
            }

            if (_blankCursor != IntPtr.Zero)
            {
                try { DestroyCursor(_blankCursor); } catch { }
                _blankCursor = IntPtr.Zero;
            }

            WindowHandle = IntPtr.Zero;
            _parentHwnd = IntPtr.Zero;
            _lastFocusableChildWindow = IntPtr.Zero;
        }

        private IntPtr GetBlankCursor()
        {
            if (_blankCursor != IntPtr.Zero)
                return _blankCursor;

            try
            {
                var andMask = System.Linq.Enumerable.Repeat((byte)0xFF, 128).ToArray();
                var xorMask = new byte[128];
                _blankCursor = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
            }
            catch
            {
                _blankCursor = IntPtr.Zero;
            }

            return _blankCursor;
        }

        private void SubclassWindow()
        {
            if (WindowHandle == IntPtr.Zero || _oldWindowProc != IntPtr.Zero)
                return;

            try
            {
                _windowProcDelegate = ChildWindowProc;
                var newProc = Marshal.GetFunctionPointerForDelegate(_windowProcDelegate);
                _oldWindowProc = IntPtr.Size == 8
                    ? SetWindowLongPtr64(WindowHandle, GWLP_WNDPROC, newProc)
                    : new IntPtr(SetWindowLong32(WindowHandle, GWLP_WNDPROC, newProc.ToInt32()));
            }
            catch
            {
                _oldWindowProc = IntPtr.Zero;
                _windowProcDelegate = null;
            }
        }

        private void RestoreWindowProc()
        {
            if (WindowHandle == IntPtr.Zero || _oldWindowProc == IntPtr.Zero)
                return;

            try
            {
                if (IntPtr.Size == 8)
                    SetWindowLongPtr64(WindowHandle, GWLP_WNDPROC, _oldWindowProc);
                else
                    SetWindowLong32(WindowHandle, GWLP_WNDPROC, _oldWindowProc.ToInt32());
            }
            catch
            {
            }

            _oldWindowProc = IntPtr.Zero;
            _windowProcDelegate = null;
        }

        private void RefreshChildCursorHooks()
        {
            if (WindowHandle == IntPtr.Zero || _refreshingChildCursorHooks)
                return;

            _refreshingChildCursorHooks = true;
            try
            {
                EnumChildWindows(WindowHandle, (childHwnd, _) =>
                {
                    if (childHwnd == IntPtr.Zero || childHwnd == WindowHandle)
                        return true;

                    _lastFocusableChildWindow = childHwnd;

                    if (_subclassedChildWindows.ContainsKey(childHwnd))
                        return true;

                    try
                    {
                        if (_cursorHidden)
                            ApplyBlankClassCursor(childHwnd);

                        if (_windowProcDelegate == null)
                            _windowProcDelegate = ChildWindowProc;

                        var newProc = Marshal.GetFunctionPointerForDelegate(_windowProcDelegate);
                        var oldProc = IntPtr.Size == 8
                            ? SetWindowLongPtr64(childHwnd, GWLP_WNDPROC, newProc)
                            : new IntPtr(SetWindowLong32(childHwnd, GWLP_WNDPROC, newProc.ToInt32()));

                        if (oldProc != IntPtr.Zero)
                            _subclassedChildWindows[childHwnd] = oldProc;
                    }
                    catch
                    {
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }
            finally
            {
                _refreshingChildCursorHooks = false;
            }
        }

        private void ApplyBlankClassCursor(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return;

            try
            {
                var blank = GetBlankCursor();
                if (blank == IntPtr.Zero)
                    return;

                var previous = IntPtr.Size == 8
                    ? SetClassLongPtr64(hWnd, GCLP_HCURSOR, blank)
                    : new IntPtr(unchecked((int)SetClassLong32(hWnd, GCLP_HCURSOR, unchecked((uint)blank.ToInt32()))));

                if (previous != IntPtr.Zero && !_childOriginalClassCursors.ContainsKey(hWnd))
                    _childOriginalClassCursors[hWnd] = previous;
            }
            catch
            {
            }
        }

        private void RestoreChildClassCursors()
        {
            if (_childOriginalClassCursors.Count == 0)
                return;

            foreach (var pair in _childOriginalClassCursors.ToArray())
            {
                try
                {
                    if (pair.Key != IntPtr.Zero && pair.Value != IntPtr.Zero && IsWindow(pair.Key))
                    {
                        if (IntPtr.Size == 8)
                            SetClassLongPtr64(pair.Key, GCLP_HCURSOR, pair.Value);
                        else
                            SetClassLong32(pair.Key, GCLP_HCURSOR, unchecked((uint)pair.Value.ToInt32()));
                    }
                }
                catch
                {
                }
            }

            _childOriginalClassCursors.Clear();
        }

        private void RestoreChildWindowProcs()
        {
            if (_subclassedChildWindows.Count == 0)
                return;

            foreach (var pair in _subclassedChildWindows.ToArray())
            {
                try
                {
                    if (pair.Key != IntPtr.Zero && pair.Value != IntPtr.Zero && IsWindow(pair.Key))
                    {
                        if (IntPtr.Size == 8)
                            SetWindowLongPtr64(pair.Key, GWLP_WNDPROC, pair.Value);
                        else
                            SetWindowLong32(pair.Key, GWLP_WNDPROC, pair.Value.ToInt32());
                    }
                }
                catch
                {
                }
            }

            _subclassedChildWindows.Clear();
        }

        private IntPtr GetOldWindowProcFor(IntPtr hWnd)
        {
            if (hWnd == WindowHandle)
                return _oldWindowProc;

            return _subclassedChildWindows.TryGetValue(hWnd, out var oldProc)
                ? oldProc
                : IntPtr.Zero;
        }

        private int GetParentResizeHitTestFromScreenPoint(int x, int y)
        {
            if (_parentHwnd == IntPtr.Zero)
                return HTCLIENT;

            try
            {
                if (!GetWindowRect(_parentHwnd, out var rect))
                    return HTCLIENT;

                var onLeft = x <= rect.Left + ResizeBorderPixels;
                var onRight = x >= rect.Right - ResizeBorderPixels;
                var onBottom = y >= rect.Bottom - ResizeBorderPixels;

                if (onBottom && onLeft)
                    return HTBOTTOMLEFT;
                if (onBottom && onRight)
                    return HTBOTTOMRIGHT;
                if (onBottom)
                    return HTBOTTOM;
                if (onLeft)
                    return HTLEFT;
                if (onRight)
                    return HTRIGHT;

                return HTCLIENT;
            }
            catch
            {
                return HTCLIENT;
            }
        }

        private int GetParentResizeHitTestFromNcHitTestLParam(IntPtr lParam)
        {
            var x = unchecked((short)((long)lParam & 0xFFFF));
            var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            return GetParentResizeHitTestFromScreenPoint(x, y);
        }

        private bool TryBeginParentResizeFromCursor()
        {
            if (_parentHwnd == IntPtr.Zero || !IsWindow(_parentHwnd))
                return false;

            if (!GetCursorPos(out var point))
                return false;

            var hitTest = GetParentResizeHitTestFromScreenPoint(point.X, point.Y);
            if (hitTest == HTCLIENT || hitTest == HTTRANSPARENT)
                return false;

            try
            {
                ReleaseCapture();
                var lParam = new IntPtr(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));
                SendMessageW(_parentHwnd, WM_NCLBUTTONDOWN, new IntPtr(hitTest), lParam);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private IntPtr ChildWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    // The mpv child HWND fills the client area. Keep normal client hit tests here
                    // and explicitly forward resize drags to the parent on mouse down. Returning
                    // HTTRANSPARENT was unreliable on the bottom resize strip with native mpv.
                    return new IntPtr(HTCLIENT);

                case WM_SETCURSOR:
                    ApplyCurrentCursor();
                    return new IntPtr(1);

                case WM_MOUSEACTIVATE:
                    FocusNative();
                    return new IntPtr(MA_ACTIVATE);

                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    var keyArgs = new NativeKeyEventArgs(wParam.ToInt32());
                    KeyDown?.Invoke(this, keyArgs);
                    if (keyArgs.Handled)
                        return IntPtr.Zero;
                    break;

                case WM_MOUSEMOVE:
                    if (_cursorHidden)
                        ApplyHiddenCursor();
                    PointerActivity?.Invoke(this, EventArgs.Empty);
                    break;

                case WM_LBUTTONDOWN:
                    if (TryBeginParentResizeFromCursor())
                        return IntPtr.Zero;

                    FocusNative();
                    PointerActivity?.Invoke(this, EventArgs.Empty);
                    break;

                case WM_LBUTTONDBLCLK:
                    PointerActivity?.Invoke(this, EventArgs.Empty);
                    DoubleTapped?.Invoke(this, EventArgs.Empty);
                    break;

                case WM_LBUTTONUP:
                    PointerActivity?.Invoke(this, EventArgs.Empty);
                    var now = DateTime.UtcNow;
                    if ((now - _lastClickUtc).TotalMilliseconds <= 450)
                    {
                        _lastClickUtc = DateTime.MinValue;
                        DoubleTapped?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        _lastClickUtc = now;
                        Tapped?.Invoke(this, EventArgs.Empty);
                    }
                    break;
            }

            var oldProc = GetOldWindowProcFor(hWnd);
            return oldProc != IntPtr.Zero
                ? CallWindowProc(oldProc, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;
        }
    }
}
