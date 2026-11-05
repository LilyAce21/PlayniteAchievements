using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The live blurred background of an official-SAN notification card.
    ///
    /// A SAN notification is a transparent WebView2 inside a layered window, so there is nothing
    /// behind the card for the page to blur. This puts a second, click-through window directly
    /// beneath the SAN window and draws, in the card's rounded rect, the same live blurred game
    /// picture the native toast uses (<see cref="LiveRegionCapture"/>). The page reports its card's
    /// rect, corner radius and opacity every frame (<see cref="UpdateCard"/>), so the blur follows
    /// the theme's own slide, grow and fade animations; the card's own (translucent) background and
    /// content stay in the SAN window, on top.
    ///
    /// Two windows rather than one WPF layer under the WebView2: a WebView2 is a child HWND, so WPF
    /// content under it would not show through its transparent areas.
    ///
    /// With a known game window the capture is of that window alone, which is the lowest-latency
    /// mode and leaves the SAN card visible to screen recorders. Without one it falls back to the
    /// whole monitor, and then the SAN window and this window are both excluded from the capture;
    /// otherwise the card would be blurred into its own background. UI thread only.
    /// </summary>
    internal sealed class SanLiveBackdrop : ISanBackdrop
    {
        private const uint WdaNone = 0;
        private const uint WdaExcludeFromCapture = 0x11;
        private const uint SwpNoActivate = 0x0010;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const string ReportPrefix = "san-card";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly Window _san;
        private readonly IntPtr _sanHwnd;
        private readonly LiveRegionCapture _capture;
        private readonly double _reachPhys;
        private readonly Window _backdrop;
        private readonly System.Windows.Shapes.Rectangle _card;
        private readonly ImageBrush _brush;
        private IntPtr _backdropHwnd;

        private WriteableBitmap _bitmap;
        private System.Drawing.Rectangle _frameRect;
        private System.Drawing.Rectangle _placed;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private int _tick;
        private int _framesApplied;
        private bool _disposed;

        // The page's last report: the card's box in CSS pixels of the page viewport.
        private bool _hasReport;
        private double _left, _top, _width, _height, _radius, _opacity, _viewW, _viewH;

        private readonly bool _excluded;

        private SanLiveBackdrop(Window san, IntPtr sanHwnd, LiveRegionCapture capture, double reachPhys, bool excluded)
        {
            _san = san;
            _excluded = excluded;
            _sanHwnd = sanHwnd;
            _capture = capture;
            _reachPhys = reachPhys;

            _brush = new ImageBrush
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox
            };
            _card = new System.Windows.Shapes.Rectangle
            {
                Fill = _brush,
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false
            };
            var canvas = new Canvas { IsHitTestVisible = false };
            canvas.Children.Add(_card);

            _backdrop = new Window
            {
                Content = canvas,
                Width = 1,
                Height = 1,
                Left = san.Left,
                Top = san.Top,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                IsHitTestVisible = false,
                Focusable = false
            };
            _backdrop.Show();

            _backdropHwnd = new WindowInteropHelper(_backdrop).Handle;
            if (_backdropHwnd == IntPtr.Zero ||
                (_excluded && !SetWindowDisplayAffinity(_backdropHwnd, WdaExcludeFromCapture)))
            {
                // Without this the backdrop would capture itself.
                var error = Marshal.GetLastWin32Error();
                Dispose();
                throw new InvalidOperationException("the backdrop window could not be excluded from capture (error " + error + ")");
            }

            // Click-through and never activated, like the SAN window above it.
            var ex = GetWindowLong(_backdropHwnd, GwlExStyle);
            SetWindowLong(_backdropHwnd, GwlExStyle, ex | WsExTransparent | WsExNoActivate | WsExToolWindow);

            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Starts the live background for <paramref name="sanWindow"/>, capturing
        /// <paramref name="gameHwnd"/> when it is not zero and the whole monitor otherwise. Null
        /// when capture is unavailable, with <paramref name="skipped"/> saying why; the card then
        /// shows only its own background. <paramref name="strengthPercent"/> is the 0-100 blur
        /// strength. UI thread; the window must already have a handle.
        /// </summary>
        public static SanLiveBackdrop TryStart(Window sanWindow, double strengthPercent, IntPtr gameHwnd, out string skipped)
        {
            skipped = null;
            LiveRegionCapture capture = null;
            var sanHwnd = IntPtr.Zero;
            var excluded = false;
            try
            {
                var reach = ToastBlurPatch.ReachForStrength(strengthPercent);
                if (reach < 1)
                {
                    skipped = "blur strength is 0";
                    return null;
                }

                sanHwnd = sanWindow == null ? IntPtr.Zero : new WindowInteropHelper(sanWindow).Handle;
                if (sanHwnd == IntPtr.Zero)
                {
                    skipped = "no SAN window handle";
                    return null;
                }

                var wholeMonitor = gameHwnd == IntPtr.Zero;
                if (wholeMonitor)
                {
                    if (!SetWindowDisplayAffinity(sanHwnd, WdaExcludeFromCapture))
                    {
                        skipped = "the SAN window could not be excluded from the monitor capture (error " +
                                  Marshal.GetLastWin32Error() + ")";
                        return null;
                    }

                    excluded = true;
                }

                capture = LiveRegionCapture.TryStart(wholeMonitor ? sanHwnd : gameHwnd, wholeMonitor, reach, lowLatency: true);
                if (capture == null)
                {
                    skipped = "window capture is not supported";
                    if (excluded)
                    {
                        SetWindowDisplayAffinity(sanHwnd, WdaNone);
                    }

                    return null;
                }

                return new SanLiveBackdrop(sanWindow, sanHwnd, capture, reach, excluded);
            }
            catch (Exception ex)
            {
                skipped = ex.GetType().Name + " " + ex.Message;
                try { capture?.Dispose(); } catch { }
                if (excluded)
                {
                    try { SetWindowDisplayAffinity(sanHwnd, WdaNone); } catch { }
                }

                return null;
            }
        }

        /// <summary>True for the page's card report, so the caller can route it here.</summary>
        public static bool IsCardReport(string message)
        {
            return message != null && message.StartsWith(ReportPrefix + "|", StringComparison.Ordinal);
        }

        /// <summary>
        /// Takes the page's report, <c>san-card|left|top|width|height|radius|opacity|viewW|viewH</c>:
        /// the visible card's box, corner radius and opacity in CSS pixels of the page viewport.
        /// </summary>
        public void UpdateCard(string message)
        {
            if (_disposed || !IsCardReport(message))
            {
                return;
            }

            var parts = message.Split('|');
            if (parts.Length < 9)
            {
                return;
            }

            var values = new double[8];
            for (var i = 0; i < values.Length; i++)
            {
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                {
                    return;
                }
            }

            _left = values[0];
            _top = values[1];
            _width = values[2];
            _height = values[3];
            _radius = Math.Max(0, values[4]);
            _opacity = Math.Max(0, Math.Min(1, values[5]));
            _viewW = values[6];
            _viewH = values[7];
            _hasReport = true;
        }

        /// <summary>One line for the notification's diagnostics, read after <see cref="Dispose"/>.</summary>
        public string Describe()
        {
            return $"{_capture.Describe()} applied={_framesApplied} reach={_reachPhys:0.#}";
        }

        private void OnRendering(object sender, EventArgs e)
        {
            try
            {
                // WPF can raise Rendering more than once for the same frame.
                if (e is RenderingEventArgs args)
                {
                    if (args.RenderingTime == _lastRenderingTime)
                    {
                        return;
                    }

                    _lastRenderingTime = args.RenderingTime;
                }

                _tick++;
                if (_disposed || !_hasReport || _viewW <= 0 || _viewH <= 0 ||
                    !ToastWindowPlacer.TryGetPhysicalRect(_san, out var win) || win.Width <= 0 || win.Height <= 0)
                {
                    return;
                }

                Place(win);

                // Physical pixels per CSS pixel of the page, whatever the DPI or zoom.
                var scale = win.Width / _viewW;
                var card = new System.Drawing.Rectangle(
                    win.X + (int)Math.Round(_left * scale),
                    win.Y + (int)Math.Round(_top * scale),
                    Math.Max(1, (int)Math.Round(_width * scale)),
                    Math.Max(1, (int)Math.Round(_height * scale)));

                if (_width < 1 || _height < 1 || _opacity < 0.01)
                {
                    _card.Visibility = Visibility.Hidden;
                    return;
                }

                // Same padding rule as the toast: the blur's reach plus a quarter of the card for
                // the motion that happens before the next capture lands.
                var motionPad = Math.Max(card.Width, card.Height) / 4;
                _capture.Request(LiveBackdropMath.RequestFor(card, _reachPhys, motionPad));

                var frame = _capture.TakeLatest();
                if (frame != null)
                {
                    WriteFrame(frame);
                    _framesApplied++;
                }

                if (_bitmap == null)
                {
                    return;
                }

                var viewbox = LiveBackdropMath.Viewbox(card, _frameRect);
                if (viewbox.IsEmpty)
                {
                    return;
                }

                _brush.Viewbox = new Rect(viewbox.X, viewbox.Y, viewbox.Width, viewbox.Height);

                // The backdrop window's own DPI turns physical pixels into its DIPs.
                var dpi = VisualTreeHelper.GetDpi(_backdrop);
                var sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
                var sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
                _card.Width = card.Width / sx;
                _card.Height = card.Height / sy;
                _card.RadiusX = _radius * scale / sx;
                _card.RadiusY = _radius * scale / sy;
                Canvas.SetLeft(_card, (card.X - win.X) / sx);
                Canvas.SetTop(_card, (card.Y - win.Y) / sy);
                _card.Opacity = _opacity;
                _card.Visibility = Visibility.Visible;
            }
            catch
            {
                // A missed frame keeps the previous background.
            }
        }

        /// <summary>
        /// Sizes the backdrop window to the SAN window and keeps it directly beneath it. Inserting
        /// after the SAN window also puts it in the same (topmost) band. Re-asserted now and then
        /// because the SAN window's own topmost guard can raise it.
        /// </summary>
        private void Place(System.Drawing.Rectangle win)
        {
            if (win == _placed && _tick % 20 != 0)
            {
                return;
            }

            _placed = win;
            SetWindowPos(_backdropHwnd, _sanHwnd, win.X, win.Y, win.Width, win.Height, SwpNoActivate);
        }

        private void WriteFrame(LiveRegionCapture.Frame frame)
        {
            if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                _brush.ImageSource = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
            _frameRect = frame.ScreenRect;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CompositionTarget.Rendering -= OnRendering;
            try { _capture.Dispose(); } catch { }
            if (_excluded)
            {
                try { SetWindowDisplayAffinity(_sanHwnd, WdaNone); } catch { }
            }

            try { _backdrop.Close(); } catch { }
        }
    }
}
