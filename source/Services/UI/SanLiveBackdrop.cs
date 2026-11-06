using System;
using System.Windows.Threading;
using System.Threading;
using System.Diagnostics;
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
    internal sealed class SanLiveBackdrop : IDisposable
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

        // The page's last two reports (the card's box in CSS pixels of the page viewport) and when
        // each arrived, for projecting the card forward.
        private SanCardReport _report;
        private SanCardReport _prev;
        private long _lastTicks, _prevTicks;
        private bool _hasReport, _hasPrev;

        // Capture padding around the card, sized from how fast the card moves; see MotionPad.
        private System.Drawing.Rectangle _lastCard;
        private long _lastCardTicks;
        private int _pad = MinMotionPad;
        private int _padCalm;

        // The card's last placed rect (physical px), for re-aiming the brush when a frame is uploaded
        // between render ticks; and the pending-upload flag for OnFrameReady.
        private System.Drawing.Rectangle _cardRect;
        private bool _hasCardRect;
        private int _uploadQueued;
        private long _uploads;

        // How old a frame is when it is handed to WPF, for the diagnostics line.
        private double _ageTotalMs, _ageMaxMs;
        private long _ageCount;

        // The page reports where the card was; by the time this frame is on screen it has moved on.
        // Project each edge forward by this long (plus the age of the report) from the velocity
        // between the last two reports. A report older than StaleMs means the animation stopped.
        // The blur fades in over this long from its first picture, whatever the card is doing, so it
        // can never snap on part-way through the card's entrance.
        private const double FadeInMs = 160;
        private long _fadeStartTicks;

        private const double LeadMs = 20;
        private const double MaxLeadMs = 40;
        private const double StaleMs = 60;

        // Padding around the card in the capture, in physical pixels: the blur's reach plus room for
        // the card to move before the next frame lands. Sized from the card's speed, in steps, and
        // slow to shrink, because a change in padding changes the capture's size and reallocates
        // its textures. (A fixed quarter of the card's size, as the toast uses, is 345 px a side on a
        // wide card: about four times more pixels than needed.)
        private const int MinMotionPad = 32;
        private const int MaxMotionPad = 256;
        private const int PadStep = 32;
        private const double MotionLookaheadMs = 90;

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
            _capture.FrameReady += OnFrameReady;
        }

        /// <summary>
        /// A capture that has been set up (GPU device, capture session) but not yet given a window.
        /// Setting it up is the slow part (tens of milliseconds, more when a game owns the GPU), so
        /// <see cref="PrepareCapture"/> runs on a background thread; <see cref="Attach"/> then does
        /// the quick part on the UI thread. Dispose it if it is never attached.
        /// </summary>
        public sealed class PreparedCapture : IDisposable
        {
            internal LiveRegionCapture Capture;
            internal double Reach;
            internal bool Excluded;
            internal IntPtr SanHwnd;

            public void Dispose()
            {
                try { Capture?.Dispose(); } catch { }
                if (Excluded)
                {
                    try { SetWindowDisplayAffinity(SanHwnd, WdaNone); } catch { }
                    Excluded = false;
                }
            }
        }

        /// <summary>
        /// Sets up the capture for the SAN window <paramref name="sanHwnd"/>: the game window
        /// <paramref name="gameHwnd"/> when it is not zero, the whole monitor otherwise.
        /// <paramref name="strengthPercent"/> is the 0-100 blur strength. Null when capture is
        /// unavailable, with <paramref name="skipped"/> saying why. Safe on any thread.
        /// </summary>
        public static PreparedCapture PrepareCapture(IntPtr sanHwnd, double strengthPercent, IntPtr gameHwnd, out string skipped)
        {
            skipped = null;
            LiveRegionCapture capture = null;
            var excluded = false;
            try
            {
                var reach = ToastBlurPatch.ReachForStrength(strengthPercent);
                if (reach < 1)
                {
                    skipped = "blur strength is 0";
                    return null;
                }

                if (sanHwnd == IntPtr.Zero)
                {
                    skipped = "no SAN window handle";
                    return null;
                }

                var wholeMonitor = gameHwnd == IntPtr.Zero;
                if (wholeMonitor)
                {
                    // The card would otherwise be blurred into its own background.
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

                return new PreparedCapture { Capture = capture, Reach = reach, Excluded = excluded, SanHwnd = sanHwnd };
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

        /// <summary>
        /// Gives a prepared capture its window under <paramref name="sanWindow"/> and starts showing it.
        /// Takes ownership of <paramref name="prepared"/> (disposing it when this fails). UI thread.
        /// </summary>
        public static SanLiveBackdrop Attach(Window sanWindow, PreparedCapture prepared, out string skipped)
        {
            skipped = null;
            try
            {
                return new SanLiveBackdrop(sanWindow, prepared.SanHwnd, prepared.Capture, prepared.Reach, prepared.Excluded);
            }
            catch (Exception ex)
            {
                skipped = ex.GetType().Name + " " + ex.Message;
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
        }

        /// <summary>One line for the notification's diagnostics, read after <see cref="Dispose"/>.</summary>
        public string Describe()
        {
            var age = _ageCount > 0 ? _ageTotalMs / _ageCount : 0;
            return $"{_capture.Describe()} applied={_framesApplied} uploadsOnArrival={_uploads} ticks={_tick} reach={_reachPhys:0.#} pad={_pad} " +
                   $"presentAgeMs(avg/max)={age:0.0}/{_ageMaxMs:0}";
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
                var now = Stopwatch.GetTimestamp();
                if (_disposed || !_hasReport || _report.ViewWidth <= 0 || _report.ViewHeight <= 0 ||
                    !ToastWindowPlacer.TryGetPhysicalRect(_san, out var win) || win.Width <= 0 || win.Height <= 0)
                {
                    return;
                }

                Place(win);

                // Where the card is now: the page's last report, projected forward by the card's own
                // velocity.
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
                var card = new System.Drawing.Rectangle(
                    win.X + (int)Math.Round(left * scale),
                    win.Y + (int)Math.Round(top * scale),
                    Math.Max(1, (int)Math.Round((right - left) * scale)),
                    Math.Max(1, (int)Math.Round((bottom - top) * scale)));

                if (_report.Width < 1 || _report.Height < 1 || _report.Opacity < 0.01)
                {
                    _card.Visibility = Visibility.Hidden;
                    return;
                }

                _cardRect = card;
                _hasCardRect = true;
                _capture.Request(LiveBackdropMath.RequestFor(card, _reachPhys, MotionPad(card, now)));

                // Normally the frame was already taken by UploadLatest the moment it arrived; this
                // catches one that landed after the last upload ran.
                var frame = _capture.TakeLatest();
                if (frame != null)
                {
                    WriteFrame(frame);
                    _capture.Recycle(frame);
                    _framesApplied++;
                }

                if (!ApplyViewbox(card))
                {
                    return;
                }

                // The backdrop window's own DPI turns physical pixels into its DIPs.
                var dpi = VisualTreeHelper.GetDpi(_backdrop);
                var sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
                var sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
                _card.Width = card.Width / sx;
                _card.Height = card.Height / sy;
                _card.RadiusX = _report.Radius * scale / sx;
                _card.RadiusY = _report.Radius * scale / sy;
                Canvas.SetLeft(_card, (card.X - win.X) / sx);
                Canvas.SetTop(_card, (card.Y - win.Y) / sy);
                if (_fadeStartTicks == 0)
                {
                    _fadeStartTicks = now;
                }

                var t = Math.Min(1.0, (now - _fadeStartTicks) * 1000.0 / Stopwatch.Frequency / FadeInMs);
                _card.Opacity = _report.Opacity * (t * t * (3 - 2 * t));
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

        /// <summary>
        /// Runs on the capture thread each time a frame is ready. Queues one upload on the UI thread
        /// (coalesced: while one is pending, further frames just replace the stored one), so a frame
        /// reaches WPF as soon as it exists rather than at the next render tick: on average half a tick
        /// sooner, and no frame is skipped just because two arrived between ticks.
        /// </summary>
        private void OnFrameReady()
        {
            if (_disposed || Interlocked.Exchange(ref _uploadQueued, 1) == 1)
            {
                return;
            }

            try
            {
                _backdrop.Dispatcher.BeginInvoke(new Action(UploadLatest), DispatcherPriority.Normal);
            }
            catch
            {
                Interlocked.Exchange(ref _uploadQueued, 0);
            }
        }

        private void UploadLatest()
        {
            Interlocked.Exchange(ref _uploadQueued, 0);
            if (_disposed)
            {
                return;
            }

            try
            {
                var frame = _capture.TakeLatest();
                if (frame == null)
                {
                    return;
                }

                WriteFrame(frame);
                _capture.Recycle(frame);
                _framesApplied++;
                _uploads++;

                // Re-aim the brush at the card with the new frame, using where the card was last placed.
                if (_hasCardRect)
                {
                    ApplyViewbox(_cardRect);
                }
            }
            catch
            {
                // A missed upload keeps the previous picture until the next one.
            }
        }

        /// <summary>
        /// Points the brush at the part of the current frame that lies behind <paramref name="card"/>.
        /// False when there is no frame yet or the card is outside it.
        /// </summary>
        private bool ApplyViewbox(System.Drawing.Rectangle card)
        {
            if (_bitmap == null)
            {
                return false;
            }

            var viewbox = LiveBackdropMath.Viewbox(card, _frameRect);
            if (viewbox.IsEmpty)
            {
                return false;
            }

            _brush.Viewbox = new Rect(viewbox.X, viewbox.Y, viewbox.Width, viewbox.Height);
            return true;
        }

        /// <summary>
        /// The padding to capture around the card. Grows at once when the card speeds up and shrinks
        /// only after it has been calm for a while, in steps, so the capture's size stays put.
        /// </summary>
        private int MotionPad(System.Drawing.Rectangle card, long now)
        {
            var needed = MinMotionPad;
            if (_lastCardTicks != 0)
            {
                var dtMs = (now - _lastCardTicks) * 1000.0 / Stopwatch.Frequency;
                if (dtMs > 0.5 && dtMs < 100)
                {
                    var move = Math.Max(
                        Math.Max(Math.Abs(card.Left - _lastCard.Left), Math.Abs(card.Right - _lastCard.Right)),
                        Math.Max(Math.Abs(card.Top - _lastCard.Top), Math.Abs(card.Bottom - _lastCard.Bottom)));
                    needed = (int)Math.Min(MaxMotionPad, MinMotionPad + move / dtMs * MotionLookaheadMs);
                }
            }

            _lastCard = card;
            _lastCardTicks = now;

            needed = (needed + PadStep - 1) / PadStep * PadStep;
            if (needed > _pad)
            {
                _pad = needed;
                _padCalm = 0;
            }
            else if (needed < _pad)
            {
                if (++_padCalm > 90)
                {
                    _pad = needed;
                    _padCalm = 0;
                }
            }
            else
            {
                _padCalm = 0;
            }

            return _pad;
        }

        private void WriteFrame(LiveRegionCapture.Frame frame)
        {
            if (frame.CapturedMs > 0)
            {
                var ageMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency - frame.CapturedMs;
                if (ageMs >= 0 && ageMs < 5000)
                {
                    _ageTotalMs += ageMs;
                    _ageCount++;
                    if (ageMs > _ageMaxMs)
                    {
                        _ageMaxMs = ageMs;
                    }
                }
            }

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
            _capture.FrameReady -= OnFrameReady;
            try { _capture.Dispose(); } catch { }
            if (_excluded)
            {
                try { SetWindowDisplayAffinity(_sanHwnd, WdaNone); } catch { }
            }

            try { _backdrop.Close(); } catch { }
        }
    }
}
