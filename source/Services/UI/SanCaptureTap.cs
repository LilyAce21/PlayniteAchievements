using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Records the live OfficialSAN notification (the WebView2 the player actually sees) from the
    /// moment its page reports that it drew the card, so the screenshot and clip pipelines can
    /// use that exact card instead of re-rendering a native one.
    ///
    /// This is a producer only. It never touches the toast pipeline's timing: frames are stored
    /// against their own clock (zero = the card's first visible frame) in the same compressed
    /// <see cref="ToastOverlayTrack"/> format the toast recorder uses, and a <see cref="SanCaptureHost"/>
    /// replays them into the wave's capture surface, which the existing sampler and screenshot
    /// renderer then read exactly as they read a native card. Because the replay clock is the
    /// sampler's own, the card's entrance lands on the track's first sample however long the wave
    /// took to start after the live card appeared.
    ///
    /// Cost model: one <c>CapturePreviewAsync</c> in flight at a time on the UI thread (the browser
    /// does the PNG encode), everything else — PNG decode, dedup, XOR delta, Deflate — on one
    /// below-normal background thread. Identical consecutive frames are never stored, so a card that
    /// holds still costs almost nothing. Memory is capped; past the cap the card freezes on its last
    /// stored frame, like the toast recorder's own cap.
    /// </summary>
    internal sealed class SanCaptureTap : IDisposable
    {
        /// <summary>Target gap between capture starts; the clip samples at about this cadence.</summary>
        private const int CaptureIntervalMs = 33;

        /// <summary>Encoded frames allowed to wait for the worker before capture skips a tick.</summary>
        private const int MaxPendingJobs = 6;

        private const long CompressedCapBytes = 48L * 1024 * 1024;
        private const int KeyframeIntervalFrames = 60;

        /// <summary>How long recording continues past the page's display time (exit animation plus slack).</summary>
        private const int RecordPastDisplayMs = 3500;

        /// <summary>A tap nobody claimed, or whose wave never ran, is released after this long.</summary>
        private const int ExpirySeconds = 180;

        // ---- registry ---------------------------------------------------------------------

        private static readonly object RegistryLock = new object();
        private static readonly Dictionary<string, SanCaptureTap> Registry =
            new Dictionary<string, SanCaptureTap>(StringComparer.Ordinal);

        private static string MakeKey(Guid gameId, string achievementName)
        {
            return gameId.ToString("N") + "|" + (achievementName ?? string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Registers a pending tap for an unlock about to be shown. Created before the card is on
        /// screen so the toast pipeline, which learns of the unlock immediately, can bind to it.
        /// </summary>
        public static SanCaptureTap Create(Guid gameId, string achievementName, ILogger logger)
        {
            var tap = new SanCaptureTap(logger, achievementName, MakeKey(gameId, achievementName));
            SanCaptureTap replaced;
            lock (RegistryLock)
            {
                Registry.TryGetValue(tap._key, out replaced);
                Registry[tap._key] = tap;
            }

            replaced?.Dispose();
            tap._expiry = new System.Threading.Timer(
                _ => tap.Expire(), null, ExpirySeconds * 1000, Timeout.Infinite);
            return tap;
        }

        /// <summary>Takes the pending tap for an unlock out of the registry; null when there is none.</summary>
        public static SanCaptureTap Claim(Guid gameId, string achievementName)
        {
            lock (RegistryLock)
            {
                var key = MakeKey(gameId, achievementName);
                if (Registry.TryGetValue(key, out var tap))
                {
                    Registry.Remove(key);
                    return tap;
                }
            }

            return null;
        }

        // ---- instance state ---------------------------------------------------------------

        private readonly ILogger _logger;
        private readonly string _name;
        private readonly string _key;
        private System.Threading.Timer _expiry;

        private readonly TaskCompletionSource<bool> _firstFrame =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private WebView2 _view;
        private readonly Stopwatch _clock = new Stopwatch();
        private CancellationTokenSource _cts;
        private volatile bool _started;
        private volatile bool _finished;
        private volatile bool _disposed;

        // Stored frames. Guarded by _trackLock; the worker appends, the UI thread reads.
        private readonly object _trackLock = new object();
        private readonly ToastOverlayTrack _track = new ToastOverlayTrack();
        private long _compressedBytes;
        private bool _capped;
        private bool _capLogged;
        private volatile int _processedUpToMs = -1;

        // Worker.
        private readonly object _queueLock = new object();
        private readonly Queue<Job> _pending = new Queue<Job>();
        private int _pendingJobs;
        private bool _workerRunning;
        private bool _workerStop;
        private byte[] _lastRaw;
        private byte[] _scratch;
        private byte[] _xorScratch;
        private int _framesSinceKeyframe;

        // Diagnostics.
        private int _captured;
        private int _skippedBusy;
        private int _identical;
        private int _stored;
        private double _captureMsTotal;
        private double _captureMsMax;
        private double _processMsTotal;
        private double _processMsMax;
        private int _failures;

        private struct Job
        {
            public MemoryStream Png;
            public double TMs;
        }

        private SanCaptureTap(ILogger logger, string achievementName, string key)
        {
            _logger = logger;
            _name = achievementName;
            _key = key;
        }

        /// <summary>Completes true once the first frame is stored, false when the card will never record.</summary>
        public Task<bool> FirstFrameReady => _firstFrame.Task;

        public bool HasStarted => _started;

        /// <summary>The page's display time, ms.</summary>
        public double DisplayMs { get; private set; }

        /// <summary>When (card time, ms) the entrance has finished and the card is at rest.</summary>
        public double SettleMs { get; private set; }

        /// <summary>The live window's size in DIPs; the capture surface is laid out at this size.</summary>
        public double WindowDipWidth { get; private set; }

        public double WindowDipHeight { get; private set; }

        /// <summary>The theme's transparent margin around the card body, in DIPs.</summary>
        public double InsetDip { get; private set; }

        /// <summary>How long a wave must keep sampling to cover the whole card: display, exit, slack.</summary>
        public int TotalDurationMs => (int)Math.Ceiling(DisplayMs + 700 + 300);

        /// <summary>
        /// Begins recording. UI thread, called when the page reports it drew the card — the card's
        /// first visible frame, which is the clip's unlock-moment pop-up.
        /// </summary>
        public void Start(
            WebView2 view, double windowDipWidth, double windowDipHeight, double insetDip,
            double displayMs, double settleMs)
        {
            if (_started || _disposed || view == null)
            {
                return;
            }

            _started = true;
            _view = view;
            WindowDipWidth = windowDipWidth;
            WindowDipHeight = windowDipHeight;
            InsetDip = Math.Max(0, insetDip);
            DisplayMs = Math.Max(1000, displayMs);
            SettleMs = Math.Max(0, Math.Min(settleMs, DisplayMs * 0.5));
            _cts = new CancellationTokenSource();
            _clock.Start();
            _ = RunCaptureLoopAsync(_cts.Token);
        }

        /// <summary>Marks a card that will never record (the SAN renderer failed), so a waiting wave falls back.</summary>
        public void MarkFailed()
        {
            _firstFrame.TrySetResult(false);
        }

        /// <summary>Stops recording (the live window closed). Stored frames stay available for replay.</summary>
        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private void Expire()
        {
            lock (RegistryLock)
            {
                if (Registry.TryGetValue(_key, out var current) && ReferenceEquals(current, this))
                {
                    Registry.Remove(_key);
                }
            }

            Dispose();
        }

        // ---- capture loop (UI thread) -----------------------------------------------------

        private async Task RunCaptureLoopAsync(CancellationToken token)
        {
            var maxMs = DisplayMs + RecordPastDisplayMs;
            try
            {
                while (!token.IsCancellationRequested && !_disposed && _clock.Elapsed.TotalMilliseconds < maxMs)
                {
                    var startedAt = _clock.Elapsed;
                    var core = _view?.CoreWebView2;
                    if (core == null)
                    {
                        break;
                    }

                    bool busy;
                    lock (_queueLock)
                    {
                        busy = _pendingJobs >= MaxPendingJobs;
                    }

                    if (_capped)
                    {
                        // The track is full; the card freezes on its last frame. Nothing more to record.
                        break;
                    }

                    if (busy)
                    {
                        _skippedBusy++;
                    }
                    else
                    {
                        var png = new MemoryStream();
                        var tMs = startedAt.TotalMilliseconds;
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
                        var tookMs = (_clock.Elapsed - startedAt).TotalMilliseconds;
                        _captured++;
                        _captureMsTotal += tookMs;
                        if (tookMs > _captureMsMax)
                        {
                            _captureMsMax = tookMs;
                        }

                        Enqueue(png, tMs);
                    }

                    var waitMs = CaptureIntervalMs - (_clock.Elapsed - startedAt).TotalMilliseconds;
                    if (waitMs > 2)
                    {
                        await Task.Delay((int)waitMs, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The window closing under an in-flight capture lands here; the frames already
                // stored are what the clip gets.
                _logger?.Debug(ex, $"[SanTap] Capture loop for '{_name}' ended.");
            }
            finally
            {
                _finished = true;
                lock (_queueLock)
                {
                    _workerStop = true;
                    Monitor.PulseAll(_queueLock);
                    if (!_workerRunning)
                    {
                        LogSummary();
                    }
                }

                // A card that never produced a frame must not leave a wave waiting.
                _firstFrame.TrySetResult(_stored > 0);
            }
        }

        // ---- worker -----------------------------------------------------------------------

        private void Enqueue(MemoryStream png, double tMs)
        {
            lock (_queueLock)
            {
                if (_disposed)
                {
                    png.Dispose();
                    return;
                }

                _pending.Enqueue(new Job { Png = png, TMs = tMs });
                _pendingJobs++;
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    var thread = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "SanCaptureTap",
                    };
                    thread.Start();
                }

                Monitor.Pulse(_queueLock);
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                Job job;
                lock (_queueLock)
                {
                    while (_pending.Count == 0)
                    {
                        if (_workerStop)
                        {
                            _workerRunning = false;
                            LogSummary();
                            return;
                        }

                        Monitor.Wait(_queueLock);
                    }

                    job = _pending.Dequeue();
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    if (!_disposed)
                    {
                        Process(job);
                    }
                }
                catch (Exception ex)
                {
                    _failures++;
                    _logger?.Debug(ex, $"[SanTap] Frame for '{_name}' could not be processed.");
                }
                finally
                {
                    job.Png.Dispose();
                    var ms = sw.Elapsed.TotalMilliseconds;
                    _processMsTotal += ms;
                    if (ms > _processMsMax)
                    {
                        _processMsMax = ms;
                    }

                    _processedUpToMs = (int)Math.Round(job.TMs);
                    lock (_queueLock)
                    {
                        _pendingJobs--;
                    }
                }
            }
        }

        private void Process(Job job)
        {
            if (!TryDecode(job.Png, ref _scratch, out var width, out var height))
            {
                _failures++;
                return;
            }

            if (_lastRaw != null && RawEquals(_lastRaw, _scratch))
            {
                _identical++;
                return;
            }

            var canDelta = _lastRaw != null &&
                _lastRaw.Length == _scratch.Length &&
                _framesSinceKeyframe < KeyframeIntervalFrames;
            byte[] payload = _scratch;
            if (canDelta)
            {
                if (_xorScratch == null || _xorScratch.Length != _scratch.Length)
                {
                    _xorScratch = new byte[_scratch.Length];
                }

                for (var i = 0; i < _scratch.Length; i++)
                {
                    _xorScratch[i] = (byte)(_scratch[i] ^ _lastRaw[i]);
                }

                payload = _xorScratch;
            }

            var frame = ToastOverlayTrack.Frame.Compress(payload, width, height, canDelta);
            lock (_trackLock)
            {
                if (_disposed)
                {
                    return;
                }

                _track.Frames.Add(frame);
                _track.Samples.Add(new ToastOverlayTrack.Sample
                {
                    ElapsedMs = (int)Math.Round(job.TMs),
                    FrameIndex = _track.Frames.Count - 1,
                });
                _compressedBytes += frame.Deflated.Length;
                if (_compressedBytes >= CompressedCapBytes)
                {
                    _capped = true;
                    if (!_capLogged)
                    {
                        _capLogged = true;
                        _logger?.Warn($"[SanTap] '{_name}' reached the memory cap; the card freezes at its last frame.");
                    }
                }
            }

            _framesSinceKeyframe = canDelta ? _framesSinceKeyframe + 1 : 0;
            _stored++;

            // Keep the frame just stored as the dedup/delta reference and reuse the old one as scratch.
            var previous = _lastRaw;
            _lastRaw = _scratch;
            _scratch = previous != null && previous.Length == _lastRaw.Length ? previous : null;

            if (_stored == 1)
            {
                _firstFrame.TrySetResult(true);
            }
        }

        /// <summary>
        /// PNG to premultiplied BGRA — the track's pixel format — into <paramref name="buffer"/>,
        /// reallocated only when the size changes. Premultiplication happens in the format
        /// conversion, so low-alpha edges (glows, shadows) stay exact.
        /// </summary>
        private static bool TryDecode(MemoryStream png, ref byte[] buffer, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (png == null || png.Length == 0)
            {
                return false;
            }

            png.Position = 0;
            var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            if (source.Format != PixelFormats.Pbgra32)
            {
                source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            }

            width = source.PixelWidth;
            height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var length = width * height * 4;
            if (buffer == null || buffer.Length != length)
            {
                buffer = new byte[length];
            }

            source.CopyPixels(buffer, width * 4, 0);
            return true;
        }

        private static bool RawEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ---- replay (UI thread) -----------------------------------------------------------

        /// <summary>
        /// Materializes the stored frame in effect at card time <paramref name="tMs"/> into
        /// <paramref name="buffer"/>. <paramref name="bufferIndex"/> is the stored-frame index the
        /// buffer currently holds, so a replay that advances frame by frame only inflates one delta.
        /// False when nothing is stored yet or the chain is broken (the caller keeps what it shows).
        /// </summary>
        public bool TryGetFrameAt(double tMs, ref byte[] buffer, ref int bufferIndex, out int width, out int height)
        {
            width = 0;
            height = 0;
            lock (_trackLock)
            {
                if (_disposed)
                {
                    return false;
                }

                var samples = _track.Samples;
                if (samples.Count == 0)
                {
                    return false;
                }

                // Last sample at or before tMs; the first frame stands in before the first sample.
                int lo = 0, hi = samples.Count - 1, found = 0;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    if (samples[mid].ElapsedMs <= tMs)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                var frameIndex = samples[found].FrameIndex;
                if (!_track.TryReconstructFrame(frameIndex, ref buffer, ref bufferIndex))
                {
                    return false;
                }

                var frame = _track.Frames[frameIndex];
                width = frame.Width;
                height = frame.Height;
                return true;
            }
        }

        /// <summary>
        /// The card at rest, for the with-notification screenshot: waits (bounded) until the
        /// recording has reached the settle time, then returns that frame. The screenshot's base
        /// frame is still the buffered frame at the unlock moment; only the card comes from here.
        /// </summary>
        public async Task<BitmapSource> GetSettledFrameAsync()
        {
            var wait = Stopwatch.StartNew();
            while (!_disposed && !_finished && _processedUpToMs < SettleMs && wait.ElapsedMilliseconds < 4000)
            {
                await Task.Delay(40).ConfigureAwait(true);
            }

            byte[] buffer = null;
            var index = -1;
            if (!TryGetFrameAt(SettleMs, ref buffer, ref index, out var width, out var height))
            {
                return null;
            }

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Pbgra32, null, buffer, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        // ---- teardown ---------------------------------------------------------------------

        private void LogSummary()
        {
            if (_captured == 0)
            {
                return;
            }

            long bytes;
            lock (_trackLock)
            {
                bytes = _compressedBytes;
            }

            _logger?.Info(
                $"[SanTap] '{_name}': {_captured} captures over {_clock.Elapsed.TotalSeconds:0.0}s " +
                $"(capture avg {_captureMsTotal / _captured:0.0} ms, max {_captureMsMax:0.0} ms), " +
                $"{_stored} stored, {_identical} identical, {_skippedBusy} skipped (worker busy), {_failures} failed; " +
                $"worker avg {(_processMsTotal / Math.Max(1, _captured - _skippedBusy)):0.0} ms, max {_processMsMax:0.0} ms; " +
                $"stored {bytes / 1024} KB compressed.");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _expiry?.Dispose(); } catch { }
            try { _cts?.Cancel(); } catch { }
            lock (_queueLock)
            {
                _workerStop = true;
                while (_pending.Count > 0)
                {
                    _pending.Dequeue().Png?.Dispose();
                }

                _pendingJobs = 0;
                Monitor.PulseAll(_queueLock);
            }

            lock (_trackLock)
            {
                _track.Frames.Clear();
                _track.Samples.Clear();
            }

            lock (RegistryLock)
            {
                if (Registry.TryGetValue(_key, out var current) && ReferenceEquals(current, this))
                {
                    Registry.Remove(_key);
                }
            }

            _firstFrame.TrySetResult(false);
            _view = null;
        }
    }
}
