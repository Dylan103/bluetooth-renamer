using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BluetoothRenamer
{
    /// <summary>
    /// Keeps an ordinary WPF window reachable after work-area or display changes.
    /// WPF itself owns DPI scaling and WM_DPICHANGED; this helper never handles it.
    /// </summary>
    public static class DpiSupport
    {
        public static IDisposable Attach(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            window.VerifyAccess();
            return new WindowMonitor(window);
        }

        /// <summary>Pure geometry helper. Both rectangles use the same units.</summary>
        public static Rect ClampBounds(Rect candidate, Rect workArea)
        {
            RequireFiniteRectangle(candidate, "candidate");
            RequireFiniteRectangle(workArea, "workArea");
            double width = Math.Min(candidate.Width, workArea.Width);
            double height = Math.Min(candidate.Height, workArea.Height);
            double left = Math.Max(workArea.Left, Math.Min(candidate.Left, workArea.Right - width));
            double top = Math.Max(workArea.Top, Math.Min(candidate.Top, workArea.Bottom - height));
            return new Rect(left, top, width, height);
        }

        public static string Diagnostics(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            window.VerifyAccess();
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return "Window handle has not been created.";
            uint dpi = ReadDpi(handle);
            string awareness = "Unknown";
            try
            {
                IntPtr context = GetWindowDpiAwarenessContext(handle);
                if (AreDpiAwarenessContextsEqual(context, new IntPtr(-4))) awareness = "PerMonitorV2";
                else if (AreDpiAwarenessContextsEqual(context, new IntPtr(-3))) awareness = "PerMonitor";
                else if (AreDpiAwarenessContextsEqual(context, new IntPtr(-2))) awareness = "System";
                else if (AreDpiAwarenessContextsEqual(context, new IntPtr(-1))) awareness = "Unaware";
            }
            catch (EntryPointNotFoundException) { awareness = "Legacy Windows"; }

            MonitorInfo info = CreateMonitorInfo();
            IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (!GetMonitorInfo(monitor, ref info))
                return String.Format(CultureInfo.InvariantCulture, "DPI={0}; Awareness={1}", dpi, awareness);
            return String.Format(CultureInfo.InvariantCulture,
                "DPI={0}; Scale={1:0.##}%; Awareness={2}; WorkAreaPixels={3},{4},{5},{6}",
                dpi, dpi * 100.0 / 96.0, awareness, info.Work.Left, info.Work.Top,
                info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        }

        private static void RequireFiniteRectangle(Rect value, string parameter)
        {
            if (value.IsEmpty || !IsFinite(value.X) || !IsFinite(value.Y) ||
                !IsFinite(value.Width) || !IsFinite(value.Height) ||
                value.Width <= 0 || value.Height <= 0 ||
                !IsFinite(value.Right) || !IsFinite(value.Bottom))
                throw new ArgumentException("Use a finite rectangle with positive dimensions.", parameter);
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private sealed class WindowMonitor : IDisposable
        {
            private readonly Window window;
            private readonly double requestedMinWidth;
            private readonly double requestedMinHeight;
            private HwndSource source;
            private IntPtr handle;
            private bool disposed;
            private bool queued;
            private bool moving;

            public WindowMonitor(Window value)
            {
                window = value;
                requestedMinWidth = value.MinWidth;
                requestedMinHeight = value.MinHeight;
                window.SourceInitialized += OnSourceInitialized;
                window.ContentRendered += OnContentRendered;
                window.StateChanged += OnStateChanged;
                window.Closed += OnClosed;
                if (new WindowInteropHelper(window).Handle != IntPtr.Zero) AttachSource();
            }

            private void OnSourceInitialized(object sender, EventArgs args) { AttachSource(); }
            private void OnContentRendered(object sender, EventArgs args) { QueueCheck(); }
            private void OnStateChanged(object sender, EventArgs args) { QueueCheck(); }
            private void OnClosed(object sender, EventArgs args) { Dispose(); }

            private void AttachSource()
            {
                if (disposed || source != null) return;
                handle = new WindowInteropHelper(window).Handle;
                source = HwndSource.FromHwnd(handle);
                if (source != null) source.AddHook(WindowProcedure);
                QueueCheck();
            }

            private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam,
                IntPtr lParam, ref bool handled)
            {
                switch (message)
                {
                    case 0x0231: // WM_ENTERSIZEMOVE
                        moving = true;
                        break;
                    case 0x0232: // WM_EXITSIZEMOVE
                        moving = false;
                        QueueCheck();
                        break;
                    case 0x007E: // WM_DISPLAYCHANGE
                    case 0x001A: // WM_SETTINGCHANGE, including taskbar/work-area changes
                    case 0x02E0: // WM_DPICHANGED: allow WPF to apply its suggested rectangle first.
                        QueueCheck();
                        break;
                }
                return IntPtr.Zero;
            }

            private void QueueCheck()
            {
                if (disposed || queued || window.Dispatcher.HasShutdownStarted) return;
                queued = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    queued = false;
                    EnsureReachable();
                }));
            }

            private void EnsureReachable()
            {
                if (disposed || moving || handle == IntPtr.Zero ||
                    window.WindowState == WindowState.Minimized) return;

                MonitorInfo info = CreateMonitorInfo();
                IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
                int workWidth = info.Work.Right - info.Work.Left;
                int workHeight = info.Work.Bottom - info.Work.Top;
                if (workWidth <= 0 || workHeight <= 0) return;

                double dpi = ReadDpi(handle);
                window.MinWidth = Math.Min(requestedMinWidth, workWidth * 96.0 / dpi);
                window.MinHeight = Math.Min(requestedMinHeight, workHeight * 96.0 / dpi);
                if (window.WindowState != WindowState.Normal) return;

                NativeRect nativeBounds;
                if (!GetWindowRect(handle, out nativeBounds)) return;
                int width = nativeBounds.Right - nativeBounds.Left;
                int height = nativeBounds.Bottom - nativeBounds.Top;
                if (width <= 0 || height <= 0) return;
                Rect candidate = new Rect(nativeBounds.Left, nativeBounds.Top, width, height);
                Rect workArea = new Rect(info.Work.Left, info.Work.Top, workWidth, workHeight);
                Rect clamped = ClampBounds(candidate, workArea);
                if (clamped == candidate) return;

                SetWindowPos(handle, IntPtr.Zero,
                    (int)Math.Round(clamped.X), (int)Math.Round(clamped.Y),
                    (int)Math.Round(clamped.Width), (int)Math.Round(clamped.Height),
                    0x0004 | 0x0010 | 0x0200); // NOZORDER | NOACTIVATE | NOOWNERZORDER
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                window.SourceInitialized -= OnSourceInitialized;
                window.ContentRendered -= OnContentRendered;
                window.StateChanged -= OnStateChanged;
                window.Closed -= OnClosed;
                if (source != null)
                {
                    source.RemoveHook(WindowProcedure);
                    source = null;
                }
            }
        }

        private const uint MonitorDefaultToNearest = 2;

        private static uint ReadDpi(IntPtr handle)
        {
            try
            {
                uint value = GetDpiForWindow(handle);
                return value == 0 ? 96u : value;
            }
            catch (EntryPointNotFoundException) { return 96u; }
        }

        private static MonitorInfo CreateMonitorInfo()
        {
            MonitorInfo value = new MonitorInfo();
            value.Size = Marshal.SizeOf(typeof(MonitorInfo));
            return value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
            int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    }
}
