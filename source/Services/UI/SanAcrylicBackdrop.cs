using System;
using System.Collections.Generic;
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
        /// <summary>The plain accent blur-behind.</summary>
        RoundedBlur = 0,

        /// <summary>The acrylic accent: a nicer material, with a little tint and noise.</summary>
        Acrylic = 1
    }

    /// <summary>
    /// The blur behind an official-SAN card done by Windows itself, so it has no delay.
    ///
    /// The capture engine (<see cref="SanLiveBackdrop"/>) has to grab the game's frame, blur it and
    /// copy it back to the CPU, which is several frames behind by construction. Here the backdrop is
    /// made of ordinary windows with the DWM blur accent switched on: the compositor blurs whatever is
    /// under them in the same frame it composes the game, at the display's refresh rate.
    ///
    /// Windows' blur does not follow a window region (tried: the corners stayed square), so the
    /// rounded shape is built from the windows themselves. The card is cut into non-overlapping
    /// rectangles: a full-width band for the middle, and at each end a stack of strips that get
    /// narrower towards the corner, tracing the quarter circle. They are moved together in one
    /// operation, directly beneath the SAN window, following the card rect the page reports.
    ///
    /// Trade-offs: the blur radius is fixed by Windows, the blur cannot fade (it appears once the card
    /// is mostly visible), the curve is a staircase of about 6 px steps, and while it is on the game
    /// is composed by the desktop compositor rather than flipped straight to the display. UI thread only.
    /// </summary>
    internal sealed class SanAcrylicBackdrop : ISanBackdrop
    {
        private const int WcaAccentPolicy = 19;
        private const int AccentEnableBlurBehind = 3;
        private const int AccentEnableAcrylicBlurBehind = 4;

        // Almost no tint for the plain blur; alpha is not zero because some builds then draw nothing.
        private const int BlurTintAbgr = 0x01000000;

        // Windows' own tint over the acrylic, ABGR: a light dark wash.
        private const int AcrylicTintAbgr = 0x26000000;

        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpHideWindow = 0x0080;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        // Windows fades and scales a window in and out when it is shown or hidden; on these blur
        // windows that reads as a flash of the backdrop as the card appears and goes.
        private const int DwmwaTransitionsForceDisabled = 3;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        // The blur is on/off, so it appears once the card is mostly visible and goes as it fades.
        private const double ShowAtOpacity = 0.35;

        // The page reports where the card was; by the time these windows move and the card is on
        // screen it has moved on. Project each edge forward by this long (plus the age of the
        // report) using the velocity between the last two reports. Reports older than StaleMs mean
        // the animation has stopped, so nothing is projected.
        private const double LeadMs = 28;
        private const double MaxLeadMs = 45;
        private const double StaleMs = 60;

        // Strips per corner: about one per 6 px of radius, at most this many. Tiles = 2 * strips + 1.
        private const int MaxStrips = 6;
        private const double StripHeightPx = 6;

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
        private static extern IntPtr BeginDeferWindowPos(int numWindows);

        [DllImport("user32.dll")]
        private static extern IntPtr DeferWindowPos(
            IntPtr winPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool EndDeferWindowPos(IntPtr winPosInfo);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly Window _san;
        private readonly IntPtr _sanHwnd;
        private readonly SanSystemBlurMode _mode;
        private readonly List<Window> _windows = new List<Window>();
        private readonly List<IntPtr> _hwnds = new List<IntPtr>();
        private readonly System.Drawing.Rectangle[] _tiles = new System.Drawing.Rectangle[MaxStrips * 2 + 1];

        private SanCardReport _report;
        private SanCardReport _prev;
        private long _lastTicks, _prevTicks;
        private bool _hasReport, _hasPrev;
        private int _visibleTiles;
        private bool _disposed;
        private int _placedX = int.MinValue, _placedY, _placedW, _placedH, _placedR;
        private long _updates;
        private int _peakVisible;
        private int _created;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

        private SanAcrylicBackdrop(Window san, IntPtr sanHwnd, SanSystemBlurMode mode)
        {
            _san = san;
            _sanHwnd = sanHwnd;
            _mode = mode;
        }

        /// <summary>
        /// Starts the system blur under <paramref name="sanWindow"/>. Null when Windows refuses the
        /// accent, with <paramref name="skipped"/> saying why; the caller can then fall back to the
        /// capture engine. <paramref name="radiusHintCss"/> is the card's corner radius in CSS pixels
        /// when known (0 otherwise); it only decides how many windows to create up front, because
        /// creating them in the middle of the slide-in would hitch it. UI thread; the SAN window must
        /// already have a handle.
        /// </summary>
        public static SanAcrylicBackdrop TryStart(Window sanWindow, SanSystemBlurMode mode, double radiusHintCss, out string skipped)
        {
            skipped = null;
            SanAcrylicBackdrop backdrop = null;
            try
            {
                var sanHwnd = sanWindow == null ? IntPtr.Zero : new WindowInteropHelper(sanWindow).Handle;
                if (sanHwnd == IntPtr.Zero)
                {
                    skipped = "no SAN window handle";
                    return null;
                }

                var dpi = VisualTreeHelper.GetDpi(sanWindow).DpiScaleX;
                var hintPx = Math.Max(0, radiusHintCss) * (dpi > 0 ? dpi : 1);
                var tilesWanted = TileCountFor(hintPx > 0 ? (int)Math.Round(hintPx) : 24);

                backdrop = new SanAcrylicBackdrop(sanWindow, sanHwnd, mode);
                for (var i = 0; i < tilesWanted; i++)
                {
                    if (!backdrop.AddTile(out skipped))
                    {
                        backdrop.Dispose();
                        return null;
                    }
                }

                CompositionTarget.Rendering += backdrop.OnRendering;
                return backdrop;
            }
            catch (Exception ex)
            {
                skipped = ex.GetType().Name + " " + ex.Message;
                try { backdrop?.Dispose(); } catch { }
                return null;
            }
        }

        private static int StripsFor(int radiusPx)
        {
            return Math.Max(1, Math.Min(MaxStrips, (int)Math.Round(radiusPx / StripHeightPx)));
        }

        private static int TileCountFor(int radiusPx)
        {
            return radiusPx < 3 ? 1 : StripsFor(radiusPx) * 2 + 1;
        }

        /// <summary>Creates one hidden blur window and adds it to the pool.</summary>
        private bool AddTile(out string skipped)
        {
            skipped = null;
            var window = new Window
            {
                Width = 1,
                Height = 1,
                Left = _san.Left,
                Top = _san.Top,
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
            // Create the handle first so the transitions can be switched off before the window is
            // ever shown (its first show would animate too).
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                skipped = "no backdrop window handle";
                window.Close();
                return false;
            }

            var noTransitions = 1;
            DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref noTransitions, sizeof(int));
            window.Show();

            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null)
            {
                // The accent only shows through where WPF paints nothing.
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            var ex = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExNoActivate | WsExToolWindow);

            var acrylic = _mode == SanSystemBlurMode.Acrylic;
            if (!ApplyAccent(hwnd, acrylic ? AccentEnableAcrylicBlurBehind : AccentEnableBlurBehind, acrylic ? AcrylicTintAbgr : BlurTintAbgr))
            {
                skipped = "Windows refused the " + (acrylic ? "acrylic" : "blur") + " accent";
                window.Close();
                return false;
            }

            // Hidden until the page has reported where the card is.
            SetWindowPos(hwnd, _sanHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
            _windows.Add(window);
            _hwnds.Add(hwnd);
            _created++;
            return true;
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
                   $"windows={_created} peakVisible={_peakVisible} placements={_updates} " +
                   $"card={_placedW}x{_placedH} r={_placedR} transitionsOff=true";
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
                var radius = (int)Math.Round(_report.Radius * scale);

                if (w < 1 || h < 1 || _report.Opacity < ShowAtOpacity)
                {
                    HideAll();
                    return;
                }

                if (_visibleTiles > 0 && x == _placedX && y == _placedY && w == _placedW && h == _placedH && radius == _placedR)
                {
                    return;
                }

                var count = Layout(x, y, w, h, radius, _tiles);
                while (_windows.Count < count)
                {
                    // More tiles than were created up front (the radius turned out larger).
                    if (!AddTile(out _))
                    {
                        break;
                    }
                }

                count = Math.Min(count, _windows.Count);
                Place(count);

                _placedX = x;
                _placedY = y;
                _placedW = w;
                _placedH = h;
                _placedR = radius;
                _visibleTiles = count;
                _peakVisible = Math.Max(_peakVisible, count);
                _updates++;
            }
            catch
            {
                // A missed frame keeps the previous position.
            }
        }

        /// <summary>
        /// Cuts the rounded rect into rectangles that do not overlap: a full-width middle band, and
        /// at the top and bottom a stack of strips that are narrower the closer they are to the
        /// corner, each as wide as the quarter circle is at the strip's middle. Returns the count.
        /// </summary>
        private static int Layout(int x, int y, int w, int h, int radius, System.Drawing.Rectangle[] tiles)
        {
            var r = Math.Min(radius, Math.Min(w, h) / 2);
            if (r < 3)
            {
                tiles[0] = new System.Drawing.Rectangle(x, y, w, h);
                return 1;
            }

            var strips = StripsFor(r);
            var n = 0;
            for (var i = 0; i < strips; i++)
            {
                var y0 = i * r / strips;
                var y1 = (i + 1) * r / strips;
                if (y1 <= y0)
                {
                    continue;
                }

                // How far below the circle's centre line the strip's middle is, and so how far the
                // arc has moved in from the card's edge there.
                var dy = r - (y0 + y1) / 2.0;
                var inset = (int)Math.Round(r - Math.Sqrt(Math.Max(0, (double)r * r - dy * dy)));
                var width = w - 2 * inset;
                if (width < 1)
                {
                    continue;
                }

                tiles[n++] = new System.Drawing.Rectangle(x + inset, y + y0, width, y1 - y0);
                tiles[n++] = new System.Drawing.Rectangle(x + inset, y + h - y1, width, y1 - y0);
            }

            if (h - 2 * r > 0)
            {
                tiles[n++] = new System.Drawing.Rectangle(x, y + r, w, h - 2 * r);
            }

            return n;
        }

        /// <summary>
        /// Moves the first <paramref name="count"/> windows to their tiles, hides the rest, and does it
        /// in one operation so the pieces never show in different places. Inserting after the SAN
        /// window keeps them directly beneath it, in the same topmost band.
        /// </summary>
        private void Place(int count)
        {
            var batch = BeginDeferWindowPos(_hwnds.Count);
            for (var i = 0; i < _hwnds.Count; i++)
            {
                var visible = i < count;
                var t = visible ? _tiles[i] : default(System.Drawing.Rectangle);
                var flags = visible
                    ? SwpNoActivate | SwpShowWindow
                    : (i < _visibleTiles ? SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow : 0u);
                if (!visible && flags == 0)
                {
                    continue;
                }

                if (batch != IntPtr.Zero)
                {
                    var next = DeferWindowPos(batch, _hwnds[i], _sanHwnd, t.X, t.Y, t.Width, t.Height, flags);
                    if (next != IntPtr.Zero)
                    {
                        batch = next;
                        continue;
                    }
                }

                // The batch failed (or never started): place this one on its own.
                SetWindowPos(_hwnds[i], _sanHwnd, t.X, t.Y, t.Width, t.Height, flags);
                batch = IntPtr.Zero;
            }

            if (batch != IntPtr.Zero)
            {
                EndDeferWindowPos(batch);
            }
        }

        private void HideAll()
        {
            if (_visibleTiles == 0)
            {
                return;
            }

            for (var i = 0; i < _visibleTiles && i < _hwnds.Count; i++)
            {
                SetWindowPos(_hwnds[i], _sanHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpHideWindow);
            }

            _visibleTiles = 0;
            _placedX = int.MinValue;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CompositionTarget.Rendering -= OnRendering;
            foreach (var window in _windows)
            {
                try { window.Close(); } catch { }
            }

            _windows.Clear();
            _hwnds.Clear();
        }
    }
}
