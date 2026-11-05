using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The blur behind an official-SAN card done by Windows itself, so it has no delay.
    ///
    /// The capture engine (<see cref="SanLiveBackdrop"/>) has to grab the game's frame, blur it and
    /// copy it back to the CPU, which is several frames behind by construction. Here the backdrop is
    /// an ordinary, non-layered window exactly the size of the card with the DWM acrylic accent
    /// switched on: the compositor blurs whatever is under it in the same frame it composes the game,
    /// at the display's refresh rate. The window is clipped to the card's rounded rect with a window
    /// region and kept directly beneath the SAN window, following the card rect the page reports.
    ///
    /// Trade-offs: the blur radius is fixed by Windows, the blur cannot fade (it appears once the card
    /// is mostly visible), and while it is on the game is composed by the desktop compositor rather
    /// than flipped straight to the display. UI thread only.
    /// </summary>
    internal sealed class SanAcrylicBackdrop : ISanBackdrop
    {
        private const int WcaAccentPolicy = 19;
        private const int AccentEnableAcrylicBlurBehind = 4;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpHideWindow = 0x0080;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        // Windows' own tint over the blur, ABGR: a light dark wash so bright scenery does not wash
        // out white text. Alpha must not be zero, which some builds treat as "no blur at all".
        private const int AcrylicTintAbgr = 0x26000000;

        // The blur is on/off, so it appears once the card is mostly visible and goes as it fades.
        private const double ShowAtOpacity = 0.35;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly Window _san;
        private readonly IntPtr _sanHwnd;
        private readonly Window _window;
        private readonly IntPtr _hwnd;

        private SanCardReport _report;
        private bool _hasReport;
        private bool _shown;
        private bool _disposed;
        private int _regionW = -1, _regionH = -1, _regionR = -1;
        private long _updates;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

        private SanAcrylicBackdrop(Window san, IntPtr sanHwnd, Window window, IntPtr hwnd)
        {
            _san = san;
            _sanHwnd = sanHwnd;
            _window = window;
            _hwnd = hwnd;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Starts the acrylic backdrop under <paramref name="sanWindow"/>. Null when Windows refuses
        /// the accent, with <paramref name="skipped"/> saying why; the caller can then fall back to the
        /// capture engine. UI thread; the SAN window must already have a handle.
        /// </summary>
        public static SanAcrylicBackdrop TryStart(Window sanWindow, out string skipped)
        {
            skipped = null;
            Window window = null;
            try
            {
                var sanHwnd = sanWindow == null ? IntPtr.Zero : new WindowInteropHelper(sanWindow).Handle;
                if (sanHwnd == IntPtr.Zero)
                {
                    skipped = "no SAN window handle";
                    return null;
                }

                window = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = sanWindow.Left,
                    Top = sanWindow.Top,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    // Not layered: the acrylic accent does not work on layered windows.
                    AllowsTransparency = false,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    IsHitTestVisible = false,
                    Focusable = false
                };
                window.Show();

                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    skipped = "no backdrop window handle";
                    window.Close();
                    return null;
                }

                var source = HwndSource.FromHwnd(hwnd);
                if (source?.CompositionTarget != null)
                {
                    // The accent only shows through where WPF paints nothing.
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }

                var ex = GetWindowLong(hwnd, GwlExStyle);
                SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExNoActivate | WsExToolWindow);

                if (!ApplyAcrylic(hwnd))
                {
                    skipped = "Windows refused the acrylic accent";
                    window.Close();
                    return null;
                }

                // Hidden until the page has reported where the card is.
                SetWindowPos(hwnd, sanHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
                return new SanAcrylicBackdrop(sanWindow, sanHwnd, window, hwnd);
            }
            catch (Exception ex)
            {
                skipped = ex.GetType().Name + " " + ex.Message;
                try { window?.Close(); } catch { }
                return null;
            }
        }

        private static bool ApplyAcrylic(IntPtr hwnd)
        {
            var policy = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 0,
                GradientColor = AcrylicTintAbgr,
                AnimationId = 0
            };

            var size = Marshal.SizeOf(typeof(AccentPolicy));
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = memory,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        public void UpdateCard(string message)
        {
            if (!_disposed && SanCardReport.TryParse(message, out var report))
            {
                _report = report;
                _hasReport = true;
            }
        }

        public string Describe()
        {
            return $"engine=acrylic shown={_shown} updates={_updates}";
        }

        private void OnRendering(object sender, EventArgs e)
        {
            try
            {
                if (e is RenderingEventArgs args)
                {
                    if (args.RenderingTime == _lastRenderingTime)
                    {
                        return;
                    }

                    _lastRenderingTime = args.RenderingTime;
                }

                if (_disposed || !_hasReport || _report.ViewWidth <= 0 || _report.ViewHeight <= 0 ||
                    !ToastWindowPlacer.TryGetPhysicalRect(_san, out var win) || win.Width <= 0)
                {
                    return;
                }

                // Physical pixels per CSS pixel of the page, whatever the DPI or zoom.
                var scale = win.Width / _report.ViewWidth;
                var x = win.X + (int)Math.Round(_report.Left * scale);
                var y = win.Y + (int)Math.Round(_report.Top * scale);
                var w = (int)Math.Round(_report.Width * scale);
                var h = (int)Math.Round(_report.Height * scale);

                if (w < 1 || h < 1 || _report.Opacity < ShowAtOpacity)
                {
                    if (_shown)
                    {
                        SetWindowPos(_hwnd, _sanHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
                        _shown = false;
                    }

                    return;
                }

                // Inserting after the SAN window keeps this one directly beneath it, in the same
                // topmost band, every frame.
                SetWindowPos(_hwnd, _sanHwnd, x, y, w, h, SwpNoActivate | (_shown ? 0u : SwpShowWindow));
                _shown = true;
                _updates++;

                var radius = (int)Math.Round(_report.Radius * scale);
                if (w != _regionW || h != _regionH || radius != _regionR)
                {
                    _regionW = w;
                    _regionH = h;
                    _regionR = radius;

                    // The window region clips the blur to the card's rounded corners. On success the
                    // system owns the region; on failure it must be freed.
                    var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2);
                    if (region != IntPtr.Zero && SetWindowRgn(_hwnd, region, true) == 0)
                    {
                        DeleteObject(region);
                    }
                }
            }
            catch
            {
                // A missed frame keeps the previous position.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CompositionTarget.Rendering -= OnRendering;
            try { _window.Close(); } catch { }
        }
    }
}
