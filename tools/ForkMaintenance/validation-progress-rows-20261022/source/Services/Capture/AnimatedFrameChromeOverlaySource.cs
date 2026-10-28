using System.Collections.Generic;
using SharpDX.MediaFoundation;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The screenshot frame's chrome over the opening of a framed unlock clip when the frame
    /// carries animated images: like <see cref="FrameChromeOverlaySource"/> (same interval and
    /// fade), but each video frame shows the chrome drawn at its own time into the frame, so the
    /// images play from their first frame. Every distinct chrome is drawn on demand at the clip's
    /// full frame size and converted once; the converted chromes are kept up to a memory budget
    /// and one evicted is drawn again when it comes back round, which costs export time, never
    /// quality.
    /// </summary>
    internal sealed class AnimatedFrameChromeOverlaySource : IFrameOverlaySource
    {
        private const double OneSecond100ns = 10_000_000.0;
        private const long TicksPerMillisecond = 10_000;

        // Converted chromes are about 5.5 bytes per covered pixel (11 MB for a full 1080p frame);
        // the plugin runs in a 32-bit process, so the cache stays well short of its address space.
        private const long CacheBudgetBytes = 160L * 1024 * 1024;

        private readonly FrameChromeAnimation _animation;
        private readonly FrameChromeImage _fallback;
        private readonly double? _holdSeconds;
        private readonly object _sync = new object();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, StaticNv12Overlay>>> _index =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, StaticNv12Overlay>>>();
        private readonly LinkedList<KeyValuePair<string, StaticNv12Overlay>> _order =
            new LinkedList<KeyValuePair<string, StaticNv12Overlay>>();
        private long _cachedBytes;
        private int _cacheW;
        private int _cacheH;
        private StaticNv12Overlay _fallbackConverted;

        /// <param name="animation">Draws the chrome at a time into the frame.</param>
        /// <param name="fallback">The chrome at the frame's first moment, shown when a draw fails.</param>
        /// <param name="startTicks">Base-clip tick the frame first shows on.</param>
        /// <param name="endTicks">Base-clip tick the frame last shows on (inclusive).</param>
        /// <param name="holdSeconds">How long the frame shows before it has faded; null for no fade.</param>
        public AnimatedFrameChromeOverlaySource(
            FrameChromeAnimation animation, FrameChromeImage fallback, long startTicks, long endTicks, double? holdSeconds)
        {
            _animation = animation;
            _fallback = fallback;
            _holdSeconds = holdSeconds;
            StartTicks = startTicks;
            EndTicks = endTicks;
        }

        public long StartTicks { get; }

        public long EndTicks { get; }

        public IFrameOverlay CreateRenderer(int frameW, int frameH, int stride)
        {
            return new Renderer(this, frameW, frameH, new OverlayCompositor(frameW, frameH, stride));
        }

        /// <summary>The converted chrome showing <paramref name="timeMs"/> into the frame.</summary>
        private StaticNv12Overlay OverlayAt(long timeMs, int frameW, int frameH)
        {
            var state = _animation.StateAt(timeMs);
            lock (_sync)
            {
                if (_cacheW != frameW || _cacheH != frameH)
                {
                    _index.Clear();
                    _order.Clear();
                    _cachedBytes = 0;
                    _fallbackConverted = null;
                    _cacheW = frameW;
                    _cacheH = frameH;
                }

                if (_index.TryGetValue(state, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    return node.Value.Value;
                }
            }

            // Drawn outside the lock: the draw marshals to the UI thread.
            var chrome = _animation.RenderAt(timeMs);
            var converted = chrome != null && chrome.IsValid
                ? StaticNv12Overlay.Create(chrome.Pixels, chrome.Width, chrome.Height, frameW, frameH)
                : null;

            lock (_sync)
            {
                if (converted == null)
                {
                    return _fallbackConverted ?? (_fallbackConverted = StaticNv12Overlay.Create(
                        _fallback?.Pixels, _fallback?.Width ?? 0, _fallback?.Height ?? 0, frameW, frameH));
                }

                if (_index.TryGetValue(state, out var existing))
                {
                    return existing.Value.Value;
                }

                _index[state] = _order.AddFirst(new KeyValuePair<string, StaticNv12Overlay>(state, converted));
                _cachedBytes += converted.SizeInBytes;
                while (_cachedBytes > CacheBudgetBytes && _order.Count > 1)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _index.Remove(last.Value.Key);
                    _cachedBytes -= last.Value.Value.SizeInBytes;
                }

                return converted;
            }
        }

        private sealed class Renderer : IFrameOverlay
        {
            private readonly AnimatedFrameChromeOverlaySource _source;
            private readonly int _frameW;
            private readonly int _frameH;
            private readonly OverlayCompositor _compositor;

            public Renderer(AnimatedFrameChromeOverlaySource source, int frameW, int frameH, OverlayCompositor compositor)
            {
                _source = source;
                _frameW = frameW;
                _frameH = frameH;
                _compositor = compositor;
            }

            public bool TryCompose(Sample frame, long baseTime)
            {
                if (baseTime < _source.StartTicks || baseTime > _source.EndTicks)
                {
                    return false;
                }

                var opacity = FramedClipTiming.Opacity(
                    (baseTime - _source.StartTicks) / OneSecond100ns, _source._holdSeconds);
                if (opacity <= 0)
                {
                    return false;
                }

                var overlay = _source.OverlayAt(
                    (baseTime - _source.StartTicks) / TicksPerMillisecond, _frameW, _frameH);
                if (overlay == null || overlay.Region.IsEmpty)
                {
                    return false;
                }

                return _compositor.ComposeRegion(frame, overlay.Region, (y, uv) => overlay.Blend(y, uv, opacity));
            }
        }
    }
}
