using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Common;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using static PlayniteAchievements.Services.Capture.NativeInterop;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
using WinRtIDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Keeps a Windows.Graphics.Capture session on one window (or one monitor) and, for each frame,
    /// reads back a small blurred copy of one requested screen region: the source of the live
    /// blurred toast background. The region is copied out of the frame on the GPU, shrunk there by
    /// mip generation, and only the shrunk copy is read back and blurred, so a frame costs a few
    /// kilobytes of readback whatever the game's resolution.
    ///
    /// Thread model: <see cref="Request"/> and <see cref="TakeLatest"/> are called from the UI
    /// thread; frames are processed on the capture's own threads, serialized by one lock that also
    /// guards disposal. The session starts on the thread pool, since the first border-suppression
    /// request can block. Every member is wrapped so a failure only leaves the background empty.
    /// </summary>
    internal sealed class LiveRegionCapture : IDisposable
    {
        /// <summary>The capture rate; motion of the card itself is tracked per rendered frame.</summary>
        public const int FramesPerSecond = 30;

        /// <summary>The low-latency (SAN backdrop) path matches a 60 Hz display.</summary>
        public const int LowLatencyFramesPerSecond = 60;

        private static readonly long MinFrameTicks = Stopwatch.Frequency / FramesPerSecond;
        private static readonly long LowLatencyMinFrameTicks = Stopwatch.Frequency / LowLatencyFramesPerSecond;

        private readonly object _gate = new object();
        private readonly IntPtr _hwnd;
        private readonly bool _isMonitor;
        private readonly double _reachPhys;

        // Opt-in low-latency pipeline (the SAN backdrop uses it; the native toast does not):
        // every source frame is processed rather than every other one, the newest queued frame is
        // taken, the GPU thread priority is raised, and the readback is pipelined so the CPU never
        // waits on the copy it has only just issued behind a busy game.
        private readonly bool _lowLatency;
        private D3D11.Texture2D[] _ring;
        private int _ringWrite;

        // Per staging texture: what it holds and whether that frame has not been shown yet.
        private readonly int[] _slotW = new int[2];
        private readonly int[] _slotH = new int[2];
        private readonly Rectangle[] _slotCovered = new Rectangle[2];
        private readonly double[] _slotCapturedMs = new double[2];
        private readonly bool[] _slotFresh = new bool[2];
        private D3D11.Query _query;
        private long _readyNow, _readyLate, _waitTicksTotal;

        private readonly FastBoxBlur _fastBlur = new FastBoxBlur();
        private readonly System.Collections.Generic.List<byte[]> _bufferPool = new System.Collections.Generic.List<byte[]>();

        /// <summary>
        /// The longest the low-latency path waits for the GPU to finish copying the frame it just
        /// issued before falling back to the previous frame's already-finished copy.
        /// </summary>
        private const int LowLatencyReadbackBudgetMs = 2;

        // Timing for the diagnostics line, written under _gate.
        private long _framesSeen;
        private long _procTicksTotal, _procTicksMax;
        private double _ageMsMax;
        private int _gpuPriority = int.MinValue;

        private D3D11.Device _device;
        private D3D11.DeviceContext _context;
        private WinRtIDirect3DDevice _winrtDevice;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private DirectXPixelFormat _format;
        private SizeInt32 _poolSize;
        private bool _hdr;
        private float _refWhite = 1f;
        private Rectangle _monitorRect;

        private D3D11.Texture2D _region;
        private D3D11.ShaderResourceView _regionView;
        private D3D11.Texture2D _staging;
        private int _mipLevel;

        // The UI thread's request has its own lock: _gate is held through session setup, which can
        // block on the first border-suppression request, and the UI thread must never wait on that.
        private readonly object _requestGate = new object();
        private Rectangle _request;
        private long _lastFrameTicks;
        private byte[] _line;
        private Frame _latest;
        private bool _disposed;

        private readonly long _createdTicks = Stopwatch.GetTimestamp();
        private long _firstFrameTicks;
        private int _framesDelivered;
        private string _failure;

        /// <summary>A blurred, shrunk BGRA copy of a screen region.</summary>
        public sealed class Frame
        {
            public Frame(byte[] bgra, int width, int height, Rectangle screenRect, double capturedMs = 0)
            {
                Bgra = bgra;
                Width = width;
                Height = height;
                ScreenRect = screenRect;
                CapturedMs = capturedMs;
            }

            /// <summary>
            /// When the pixels were captured, in milliseconds on the same clock as
            /// <c>Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency</c>; 0 when unknown. Lets the
            /// consumer measure how old a frame is when it is shown.
            /// </summary>
            public double CapturedMs { get; }

            /// <summary>Opaque BGRA pixels, rows packed at <see cref="Width"/> * 4 bytes.</summary>
            public byte[] Bgra { get; }

            public int Width { get; }

            public int Height { get; }

            /// <summary>The screen rect (physical pixels) the pixels span.</summary>
            public Rectangle ScreenRect { get; }
        }

        private LiveRegionCapture(IntPtr hwnd, bool isMonitor, double reachPhys, bool lowLatency)
        {
            _hwnd = hwnd;
            _isMonitor = isMonitor;
            _reachPhys = reachPhys;
            _lowLatency = lowLatency;
        }

        /// <summary>
        /// Starts capturing <paramref name="hwnd"/> (a game window), or the monitor it sits on when
        /// <paramref name="wholeMonitor"/> is set; <paramref name="reachPhys"/> is the blur's reach in
        /// physical pixels. Null when this Windows has no window capture.
        /// </summary>
        public static LiveRegionCapture TryStart(IntPtr hwnd, bool wholeMonitor, double reachPhys, bool lowLatency = false)
        {
            if (hwnd == IntPtr.Zero || !GraphicsCaptureSession.IsSupported())
            {
                return null;
            }

            var capture = new LiveRegionCapture(hwnd, wholeMonitor, reachPhys, lowLatency);
            Task.Run(() => capture.Start());
            return capture;
        }

        /// <summary>The screen rect (physical pixels) the next frames should cover.</summary>
        public void Request(Rectangle screenRect)
        {
            lock (_requestGate)
            {
                _request = screenRect;
            }
        }

        /// <summary>
        /// One line for the wave's diagnostics: the capture mode, frames delivered, how long the
        /// first one took, and the first failure.
        /// </summary>
        public string Describe()
        {
            var first = Interlocked.Read(ref _firstFrameTicks);
            var firstMs = first == 0
                ? "none"
                : ((first - _createdTicks) * 1000.0 / Stopwatch.Frequency).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            var delivered = Volatile.Read(ref _framesDelivered);
            var procAvg = delivered > 0 ? _procTicksTotal * 1000.0 / Stopwatch.Frequency / delivered : 0;
            var procMax = _procTicksMax * 1000.0 / Stopwatch.Frequency;
            var timing = _lowLatency
                ? $" lowLatency=true seen={Volatile.Read(ref _framesSeen)} procMs(avg/max)={procAvg:0.0}/{procMax:0.0} " +
                  $"frameAgeMsMax={_ageMsMax:0} gpuPrio={(_gpuPriority == int.MinValue ? "n/a" : _gpuPriority.ToString())} " +
                  $"readback(now/late)={_readyNow}/{_readyLate} waitMsAvg={(_readyNow + _readyLate > 0 ? _waitTicksTotal * 1000.0 / Stopwatch.Frequency / (_readyNow + _readyLate) : 0):0.00}"
                : string.Empty;
            return $"mode={(_isMonitor ? "monitor" : "window")} hdr={_hdr} frames={delivered} " +
                   $"firstFrameMs={firstMs} failure={Volatile.Read(ref _failure) ?? "none"}{timing}";
        }

        private void RecordFailure(string stage, Exception ex)
        {
            Interlocked.CompareExchange(ref _failure, stage + ": " + ex.GetType().Name + " " + ex.Message, null);
        }

        /// <summary>The newest frame since the last call, or null when none has arrived.</summary>
        public Frame TakeLatest()
        {
            return Interlocked.Exchange(ref _latest, null);
        }

        /// <summary>
        /// Hands a frame's pixel buffer back once the consumer has copied it out, so the low-latency
        /// path allocates nothing per frame (a few hundred KB a frame, 60 a second, is large-object
        /// garbage and full collections, which show up as stalls). Optional; a no-op otherwise.
        /// </summary>
        public void Recycle(Frame frame)
        {
            if (_lowLatency && frame != null)
            {
                ReturnBuffer(frame.Bgra);
            }
        }

        private byte[] RentBuffer(int length)
        {
            lock (_bufferPool)
            {
                for (var i = 0; i < _bufferPool.Count; i++)
                {
                    var candidate = _bufferPool[i];
                    if (candidate.Length >= length && candidate.Length <= length * 2)
                    {
                        _bufferPool.RemoveAt(i);
                        return candidate;
                    }
                }
            }

            // A little slack, so a region that grows a few pixels still fits the same buffer.
            return new byte[length + length / 4];
        }

        private void ReturnBuffer(byte[] buffer)
        {
            if (buffer == null)
            {
                return;
            }

            lock (_bufferPool)
            {
                if (_bufferPool.Count < 6)
                {
                    _bufferPool.Add(buffer);
                }
            }
        }

        private void ResetSlots()
        {
            _slotFresh[0] = false;
            _slotFresh[1] = false;
        }

        private void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    CreateDevice();
                    var item = _isMonitor ? CreateItemForMonitor() : CreateItem(i => i.CreateForWindow(_hwnd, ref IidItem));
                    if (item == null || item.Size.Width <= 0 || item.Size.Height <= 0)
                    {
                        Interlocked.CompareExchange(ref _failure, "start: no capture item", null);
                        return;
                    }

                    _hdr = HdrDisplayDetector.IsHdrActive(_hwnd);
                    _refWhite = _hdr ? HdrDisplayDetector.GetSdrWhiteScRgb(_hwnd) : 1f;
                    _format = _hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
                    _poolSize = item.Size;
                    _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, _format, 2, _poolSize);
                    _session = _pool.CreateCaptureSession(item);
                    WgcCaptureBorder.SuppressCursor(_session);
                    WgcCaptureBorder.Suppress(_session);
                    WgcCaptureBorder.LimitUpdateRate(_session, _lowLatency ? LowLatencyFramesPerSecond : FramesPerSecond);
                    _pool.FrameArrived += OnFrameArrived;
                    _session.StartCapture();
                }
                catch (Exception ex)
                {
                    RecordFailure("start", ex);
                    ReleaseCapture();
                }
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
        {
            lock (_gate)
            {
                Direct3D11CaptureFrame frame = null;
                try
                {
                    frame = pool.TryGetNextFrame();
                    if (frame == null || _disposed)
                    {
                        return;
                    }

                    if (_lowLatency)
                    {
                        // The queue is oldest-first: skip to the newest frame so a slow frame never
                        // leaves older ones to be shown after it.
                        Direct3D11CaptureFrame newer;
                        while ((newer = pool.TryGetNextFrame()) != null)
                        {
                            frame.Dispose();
                            frame = newer;
                        }

                        _framesSeen++;
                    }

                    // A resized source delivers frames at the new size only after the pool follows.
                    if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
                    {
                        _poolSize = frame.ContentSize;
                        pool.Recreate(_winrtDevice, _format, 2, _poolSize);
                        return;
                    }

                    Rectangle request;
                    lock (_requestGate)
                    {
                        request = _request;
                    }

                    var now = Stopwatch.GetTimestamp();
                    // The source runs a little faster than the target rate, so a gate at the full
                    // interval rejects every other frame and halves the real rate (about 16 fps
                    // measured). The low-latency path gates at half an interval instead.
                    var gate = _lowLatency ? LowLatencyMinFrameTicks / 2 : MinFrameTicks;
                    if (now - _lastFrameTicks < gate || request.IsEmpty)
                    {
                        return;
                    }

                    _lastFrameTicks = now;
                    var processed = Process(frame, request);
                    if (_lowLatency)
                    {
                        var took = Stopwatch.GetTimestamp() - now;
                        _procTicksTotal += took;
                        if (took > _procTicksMax)
                        {
                            _procTicksMax = took;
                        }

                        var ageMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency -
                                    frame.SystemRelativeTime.TotalMilliseconds;
                        if (ageMs > _ageMsMax && ageMs < 60000)
                        {
                            _ageMsMax = ageMs;
                        }
                    }

                    if (processed != null)
                    {
                        var replaced = Interlocked.Exchange(ref _latest, processed);
                        if (_lowLatency && replaced != null)
                        {
                            ReturnBuffer(replaced.Bgra);
                        }

                        Interlocked.CompareExchange(ref _firstFrameTicks, now, 0);
                        Interlocked.Increment(ref _framesDelivered);
                    }
                }
                catch (Exception ex)
                {
                    // A dropped frame leaves the previous background showing.
                    RecordFailure("frame", ex);
                }
                finally
                {
                    frame?.Dispose();
                }
            }
        }

        /// <summary>
        /// Copies the requested region out of the frame, shrinks it by mip generation, reads the
        /// shrunk level back as BGRA and blurs it. Called under the lock.
        /// </summary>
        private Frame Process(Direct3D11CaptureFrame frame, Rectangle request)
        {
            var access = (IDirect3DDxgiInterfaceAccess)(object)frame.Surface;
            var textureIid = IID_ID3D11Texture2D;
            using (var texture = new D3D11.Texture2D(access.GetInterface(ref textureIid)))
            {
                var desc = texture.Description;
                if (!TryResolveMapping(desc.Width, desc.Height, out var origin, out var scale))
                {
                    Interlocked.CompareExchange(ref _failure, $"frame: no mapping for {desc.Width}x{desc.Height}", null);
                    return null;
                }

                var source = LiveBackdropMath.Project(origin, scale, request, desc.Width, desc.Height, out var covered);
                if (source.IsEmpty)
                {
                    Interlocked.CompareExchange(ref _failure, $"frame: {request} is off the capture", null);
                    return null;
                }

                EnsureTextures(source.Width, source.Height, desc.Format, _reachPhys * scale);
                _context.CopySubresourceRegion(
                    texture, 0,
                    new D3D11.ResourceRegion(source.Left, source.Top, 0, source.Right, source.Bottom, 1),
                    _region, 0);
                if (_mipLevel > 0)
                {
                    _context.GenerateMips(_regionView);
                }

                var width = Math.Max(1, source.Width >> _mipLevel);
                var height = Math.Max(1, source.Height >> _mipLevel);
                var capturedMs = frame.SystemRelativeTime.TotalMilliseconds;
                var readTexture = _staging;
                var shownCovered = covered;
                if (_lowLatency)
                {
                    // Copy this frame into one of two staging textures and read it back as soon as the
                    // GPU has finished the copy: normally well inside a millisecond, so what is shown is
                    // this frame rather than the last one. If a game keeps the GPU busy past the short
                    // wait, read the previous frame's copy instead (already finished, as the GPU runs
                    // commands in order) rather than stall the capture behind the game.
                    var write = _ringWrite;
                    _context.CopySubresourceRegion(_region, _mipLevel, null, _ring[write], 0);
                    _slotW[write] = width;
                    _slotH[write] = height;
                    _slotCovered[write] = covered;
                    _slotCapturedMs[write] = capturedMs;
                    _slotFresh[write] = true;

                    if (_query == null)
                    {
                        _query = new D3D11.Query(_device, new D3D11.QueryDescription
                        {
                            Type = D3D11.QueryType.Event,
                            Flags = D3D11.QueryFlags.None
                        });
                    }

                    _context.End(_query);
                    _context.Flush();

                    var waitStart = Stopwatch.GetTimestamp();
                    var budget = Stopwatch.Frequency * LowLatencyReadbackBudgetMs / 1000;
                    var ready = _context.IsDataAvailable(_query);
                    while (!ready && Stopwatch.GetTimestamp() - waitStart < budget)
                    {
                        Thread.SpinWait(50);
                        ready = _context.IsDataAvailable(_query);
                    }

                    _waitTicksTotal += Stopwatch.GetTimestamp() - waitStart;

                    var read = write;
                    if (ready)
                    {
                        _readyNow++;
                        // Anything older than this frame is no longer worth showing.
                        _slotFresh[write ^ 1] = false;
                    }
                    else
                    {
                        _readyLate++;
                        read = write ^ 1;
                    }

                    _ringWrite ^= 1;
                    if (!_slotFresh[read])
                    {
                        return null;
                    }

                    _slotFresh[read] = false;
                    readTexture = _ring[read];
                    width = _slotW[read];
                    height = _slotH[read];
                    shownCovered = _slotCovered[read];
                    capturedMs = _slotCapturedMs[read];
                }
                else
                {
                    _context.CopySubresourceRegion(_region, _mipLevel, null, _staging, 0);
                }

                var pixelBytes = width * height * 4;
                var bgra = _lowLatency ? RentBuffer(pixelBytes) : new byte[pixelBytes];
                var box = _context.MapSubresource(readTexture, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                try
                {
                    if (_lowLatency && !_hdr && box.RowPitch == width * 4)
                    {
                        Marshal.Copy(box.DataPointer, bgra, 0, pixelBytes);
                    }
                    else
                    {
                        for (var y = 0; y < height; y++)
                        {
                            var row = box.DataPointer + y * box.RowPitch;
                            if (_hdr)
                            {
                                HdrToneMap.ToBgraRow(row, width, _refWhite, bgra, y * width * 4);
                            }
                            else
                            {
                                Marshal.Copy(row, bgra, y * width * 4, width * 4);
                            }
                        }
                    }
                }
                finally
                {
                    _context.UnmapSubresource(readTexture, 0);
                }

                if (!_hdr)
                {
                    // Capture alpha carries nothing; the background is opaque.
                    for (var i = 3; i < pixelBytes; i += 4)
                    {
                        bgra[i] = 255;
                    }
                }

                var reachInLevel = _reachPhys * scale / (1 << _mipLevel);
                if (reachInLevel >= 1)
                {
                    if (_lowLatency)
                    {
                        _fastBlur.Apply(bgra, width, height, BoxBlur.RadiusFor(reachInLevel));
                    }
                    else
                    {
                        _line = BoxBlur.Apply(bgra, 0, width, height, width * 4, 4, BoxBlur.RadiusFor(reachInLevel), _line);
                    }
                }

                return new Frame(bgra, width, height, shownCovered, capturedMs);
            }
        }

        /// <summary>
        /// The screen point the capture texture's top-left sits at and the texture pixels per screen
        /// pixel: the window's mapping through <see cref="CaptureCropMath"/>, or the monitor's rect.
        /// </summary>
        private bool TryResolveMapping(int textureW, int textureH, out Point origin, out double scale)
        {
            origin = Point.Empty;
            scale = 0;
            if (_isMonitor)
            {
                if (_monitorRect.Width <= 0)
                {
                    return false;
                }

                origin = _monitorRect.Location;
                scale = (double)textureW / _monitorRect.Width;
                return true;
            }

            var rects = WindowRectangles.Measure(_hwnd);
            var mapping = CaptureCropMath.ResolveMapping(textureW, textureH, rects.FrameBounds, rects.OuterRect);
            if (!mapping.IsValid)
            {
                return false;
            }

            origin = mapping.Origin;
            scale = mapping.Scale;
            return true;
        }

        /// <summary>
        /// Sizes the region texture (with the mip chain the reach calls for) and the staging texture
        /// its read-back level lands in, keeping them while the size and format hold.
        /// </summary>
        private void EnsureTextures(int width, int height, DXGI.Format format, double reachPx)
        {
            var level = LiveBackdropMath.MipLevelFor(reachPx, width, height);
            if (_region != null)
            {
                var current = _region.Description;
                if (current.Width == width && current.Height == height && current.Format == format && _mipLevel == level)
                {
                    return;
                }
            }

            ReleaseTextures();
            _mipLevel = level;
            _region = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = level + 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
                BindFlags = D3D11.BindFlags.ShaderResource | D3D11.BindFlags.RenderTarget,
                OptionFlags = D3D11.ResourceOptionFlags.GenerateMipMaps
            });
            _regionView = new D3D11.ShaderResourceView(_device, _region);
            var stagingDesc = new D3D11.Texture2DDescription
            {
                Width = Math.Max(1, width >> level),
                Height = Math.Max(1, height >> level),
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read
            };
            if (_lowLatency)
            {
                _ring = new[] { new D3D11.Texture2D(_device, stagingDesc), new D3D11.Texture2D(_device, stagingDesc) };
                _ringWrite = 0;
                ResetSlots();
            }
            else
            {
                _staging = new D3D11.Texture2D(_device, stagingDesc);
            }
        }

        private void CreateDevice()
        {
            // BgraSupport is required for WGC interop and for B8G8R8A8 render targets (mip generation).
            _device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
            _context = _device.ImmediateContext;
            using (var dxgiDevice = _device.QueryInterface<DXGI.Device>())
            {
                if (_lowLatency)
                {
                    // A game keeps the GPU saturated; at normal priority this device's small copies
                    // queue behind it. Best effort: the driver or the process may refuse it.
                    try
                    {
                        dxgiDevice.GPUThreadPriority = 7;
                        _gpuPriority = dxgiDevice.GPUThreadPriority;
                    }
                    catch
                    {
                    }
                }

                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable)
                    .CheckWin32("CreateDirect3D11DeviceFromDXGIDevice");
                try
                {
                    _winrtDevice = (WinRtIDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
        }

        private static Guid IidItem = IID_IGraphicsCaptureItem;

        private delegate IntPtr ItemFactory(IGraphicsCaptureItemInterop interop);

        private static GraphicsCaptureItem CreateItem(ItemFactory create)
        {
            var factory = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal
                .GetActivationFactory(typeof(GraphicsCaptureItem));
            var itemPtr = create((IGraphicsCaptureItemInterop)factory);
            try
            {
                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }

        private GraphicsCaptureItem CreateItemForMonitor()
        {
            var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            using (DpiAwarenessScope.PerMonitorV2())
            {
                if (!GetMonitorInfoW(monitor, ref info))
                {
                    return null;
                }
            }

            _monitorRect = new Rectangle(
                info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height);
            return CreateItem(i => i.CreateForMonitor(monitor, ref IidItem));
        }

        private void ReleaseTextures()
        {
            _staging?.Dispose();
            if (_ring != null)
            {
                foreach (var texture in _ring)
                {
                    texture?.Dispose();
                }

                _ring = null;
            }

            ResetSlots();
            _regionView?.Dispose();
            _region?.Dispose();
            _staging = null;
            _regionView = null;
            _region = null;
        }

        private void ReleaseCapture()
        {
            try
            {
                if (_pool != null)
                {
                    _pool.FrameArrived -= OnFrameArrived;
                }

                _session?.Dispose();
                _pool?.Dispose();
                ReleaseTextures();
                _query?.Dispose();
                _query = null;
                _context?.Dispose();
                _device?.Dispose();
            }
            catch
            {
            }

            _session = null;
            _pool = null;
            _context = null;
            _device = null;
            _winrtDevice = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ReleaseCapture();
            }
        }
    }
}
