using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>Which of Windows' own blurs the system backdrop uses.</summary>
    internal enum SanSystemBlurMode
    {
        /// <summary>The accent blur-behind (not acrylic) with a rounded window region, to follow the card's corners.</summary>
        RoundedBlur = 0,

        /// <summary>The acrylic accent: nicer material, but its corners stay square.</summary>
        Acrylic = 1
    }

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
        private const int AccentEnableBlurBehind = 3;
        private const int AccentEnableAcrylicBlurBehind = 4;

        // Almost no tint for the plain blur; alpha is not zero because some builds then draw nothing.
        private const int BlurTintAbgr = 0x01000000;
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

        // The page reports where the card was; by the time this window moves and the card is on
        // screen it has moved on. Project each edge forward by this long (plus the age of the
        // report) using the velocity between the last two reports. Reports older than StaleMs mean
        // the animation has stopped, so nothing is projected.
        private const double LeadMs = 28;
        private const double MaxLeadMs = 45;
        private const double StaleMs = 60;


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

        private readonly SanSystemBlurMode _mode;
        private SanCardReport _report;
        private SanCardReport _prev;
        private long _lastTicks, _prevTicks;
        private bool _hasReport, _hasPrev;
        private bool _shown;
        private bool _disposed;
        private int _regionW = -1, _regionH = -1, _regionR = -1;
        private int _placedX = int.MinValue, _placedY, _placedW, _placedH;
        private long _updates;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

        private SanAcrylicBackdrop(Window san, IntPtr sanHwnd, Window window, IntPtr hwnd, SanSystemBlurMode mode)
        {
            _san = san;
            _sanHwnd = sanHwnd;
            _window = window;
            _hwnd = hwnd;
            _mode = mode;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Starts the acrylic backdrop under <paramref name="sanWindow"/>. Null when Windows refuses
        /// the accent, with <paramref name="skipped"/> saying why; the caller can then fall back to the
        /// capture engine. UI thread; the SAN window must already have a handle.
        /// </summary>
        public static SanAcrylicBackdrop TryStart(Window sanWindow, SanSystemBlurMode mode, out string skipped)
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
                    // Not layered: the accent blur does not work on layered windows.
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

                var acrylic = mode == SanSystemBlurMode.Acrylic;
                if (!ApplyAccent(hwnd, acrylic ? AccentEnableAcrylicBlurBehind : AccentEnableBlurBehind, acrylic ? AcrylicTintAbgr : BlurTintAbgr))
                {
                    skipped = "Windows refused the " + (acrylic ? "acrylic" : "blur") + " accent";
                    window.Close();
                    return null;
                }

                // Hidden until the page has reported where the card is.
                SetWindowPos(hwnd, sanHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
                return new SanAcrylicBackdrop(sanWindow, sanHwnd, window, hwnd, mode);
            }
            catch (Exception ex)
            {
                skipped = ex.GetType().Name + " " + ex.Message;
                try { window?.Close(); } catch { }
                return null;
            }
        }

        private static bool ApplyAccent(IntPtr hwnd, int accentState, int gradientAbgr)
        {
            var policy = new AccentPolicy
            {
                AccentState = accentState,
                AccentFlags = 0,
                GradientColor = gradientAbgr,
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
            if (_disposed || !SanCardReport.TryParse(message, out var report))
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (_hasReport)
            {
                _prev = _report;
                _prevTicks = _lastTicks;
                _hasPrev = true;
            }

            _report = report;
            _lastTicks = now;
            _hasReport = true;

            // Move now rather than at the next render tick: that tick can be a whole frame away.
            ApplyPlacement(now);
        }

        public string Describe()
        {
            return $"engine={(_mode == SanSystemBlurMode.RoundedBlur ? "blur-behind" : "acrylic")} " +
                   $"shown={_shown} updates={_updates} placed={_placedW}x{_placedH}@{_placedX},{_placedY} region={_regionW}x{_regionH}r{_regionR}";
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (e is RenderingEventArgs args)
            {
                if (args.RenderingTime == _lastRenderingTime)
                {
                    return;
                }

                _lastRenderingTime = args.RenderingTime;
            }

            // Keeps the card's projected position moving between the page's reports.
            ApplyPlacement(Stopwatch.GetTimestamp());
        }

        private void ApplyPlacement(long now)
        {
            try
            {
                if (_disposed || !_hasReport || _report.ViewWidth <= 0 || _report.ViewHeight <= 0 ||
                    !ToastWindowPlacer.TryGetPhysicalRect(_san, out var win) || win.Width <= 0)
                {
                    return;
                }

                // Where the card is now: the last report, projected forward by its own velocity.
                var sinceMs = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
                double left = _report.Left, top = _report.Top;
                double right = _report.Left + _report.Width, bottom = _report.Top + _report.Height;
                if (_hasPrev)
                {
                    var dtMs = (_lastTicks - _prevTicks) * 1000.0 / Stopwatch.Frequency;
                    if (dtMs > 0.5 && dtMs < 80 && sinceMs < StaleMs)
                    {
                        var lead = Math.Min(LeadMs + sinceMs, MaxLeadMs);
                        left += (_report.Left - _prev.Left) / dtMs * lead;
                        top += (_report.Top - _prev.Top) / dtMs * lead;
                        right += ((_report.Left + _report.Width) - (_prev.Left + _prev.Width)) / dtMs * lead;
                        bottom += ((_report.Top + _report.Height) - (_prev.Top + _prev.Height)) / dtMs * lead;
                    }
                }

                // Physical pixels per CSS pixel of the page, whatever the DPI or zoom.
                var scale = win.Width / _report.ViewWidth;
                var x = win.X + (int)Math.Round(left * scale);
                var y = win.Y + (int)Math.Round(top * scale);
                var w = (int)Math.Round((right - left) * scale);
                var h = (int)Math.Round((bottom - top) * scale);

                if (w < 1 || h < 1 || _report.Opacity < ShowAtOpacity)
                {
                    if (_shown)
                    {
                        SetWindowPos(_hwnd, _sanHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
                        _shown = false;
                        _placedX = int.MinValue;
                    }

                    return;
                }

                if (_shown && x == _placedX && y == _placedY && w == _placedW && h == _placedH)
                {
                    return;
                }

                // Inserting after the SAN window keeps this one directly beneath it, in the same
                // topmost band, every time it moves.
                SetWindowPos(_hwnd, _sanHwnd, x, y, w, h, SwpNoActivate | (_shown ? 0u : SwpShowWindow));
                _shown = true;
                _placedX = x;
                _placedY = y;
                _placedW = w;
                _placedH = h;
                _updates++;

                var radius = (int)Math.Round(_report.Radius * scale);
                if (w != _regionW || h != _regionH || radius != _regionR)
                {
                    _regionW = w;
                    _regionH = h;
                    _regionR = radius;
                    ApplyShape(w, h, radius);
                }
            }
            catch
            {
                // A missed frame keeps the previous position.
            }
        }

        /// <summary>Clips the window, and so the blur, to the card's rounded rect.</summary>
        private void ApplyShape(int w, int h, int radius)
        {
            var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2);
            if (region == IntPtr.Zero)
            {
                return;
            }

            // The window region. On success the system owns it; on failure it must be freed.
            if (SetWindowRgn(_hwnd, region, true) == 0)
            {
                DeleteObject(region);
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
