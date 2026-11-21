using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// The wave's capture surface for a SAN notification: an image that shows the tapped live
    /// card. Hosted as <c>AchievementToastViewModel.CustomCaptureContent</c>, so the toast
    /// pipeline's own sampler (clips) and card renderer (screenshots) read it like any card.
    ///
    /// Time is the sampler's. Each sampler tick calls <see cref="AdvanceTo"/> with its composing
    /// frame time; the first call fixes card time zero, so the first track sample is the card's
    /// first visible frame and the entrance plays from the unlock-moment pop-up, however long
    /// after the live card appeared the wave began. <see cref="Present"/>(0) before that call
    /// puts that first frame on the surface for the render that primes the track.
    ///
    /// Screenshots take the card at rest through <c>Tag</c>, the hook the toast service's
    /// screenshot step already looks for.
    /// </summary>
    internal sealed class SanCaptureHost : Grid, IDisposable
    {
        private readonly SanCaptureTap _tap;
        private readonly Image _image;
        private WriteableBitmap _bitmap;
        private byte[] _buffer;
        private int _bufferIndex = -1;
        private double _baseElapsedMs = double.NaN;

        public SanCaptureHost(SanCaptureTap tap)
        {
            _tap = tap ?? throw new ArgumentNullException(nameof(tap));

            IsHitTestVisible = false;
            SnapsToDevicePixels = true;

            // Sized by ApplySize once the live card is on screen: the tap is created when the
            // unlock is accepted, before the window it records exists.
            _image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            Children.Add(_image);

            Tag = (Func<Task<BitmapSource>>)(() => _tap.GetSettledFrameAsync());
        }

        /// <summary>Lays the surface out at the live window's size. Call once the tap has started.</summary>
        public void ApplySize()
        {
            Width = _tap.WindowDipWidth;
            Height = _tap.WindowDipHeight;
            _image.Width = _tap.WindowDipWidth;
            _image.Height = _tap.WindowDipHeight;
        }

        /// <summary>The theme's transparent margin around the card body, as the placement's card inset.</summary>
        public Thickness CardInset => new Thickness(_tap.InsetDip);

        /// <summary>Whether the live card's corner and edge gap are known.</summary>
        public bool HasLivePlacement => _tap.HasLivePlacement;

        /// <summary>The corner the live card was placed in.</summary>
        public PlayniteAchievements.Models.Settings.ToastScreenCorner LiveCorner => _tap.LiveCorner;

        /// <summary>Monitor-edge reserve (taskbar), physical pixels, horizontally on the corner's side.</summary>
        public double LiveGapPhysX => _tap.LiveGapPhysX;

        /// <summary>Monitor-edge reserve (taskbar), physical pixels, vertically on the corner's side.</summary>
        public double LiveGapPhysY => _tap.LiveGapPhysY;

        /// <summary>How long a wave must sample to cover the whole card.</summary>
        public int TotalDurationMs => _tap.TotalDurationMs;

        /// <summary>Whether the live card produced a first frame to show.</summary>
        public Task<bool> FirstFrameReady => _tap.FirstFrameReady;

        /// <summary>Called by the sampler once per tick, before it renders this card.</summary>
        public void AdvanceTo(double samplerElapsedMs)
        {
            if (double.IsNaN(_baseElapsedMs))
            {
                _baseElapsedMs = samplerElapsedMs;
            }

            Present(samplerElapsedMs - _baseElapsedMs);
        }

        /// <summary>Shows the card as it was <paramref name="cardTimeMs"/> after its first visible frame.</summary>
        public void Present(double cardTimeMs)
        {
            var before = _bufferIndex;
            if (!_tap.TryGetFrameAt(Math.Max(0, cardTimeMs), ref _buffer, ref _bufferIndex, out var width, out var height))
            {
                return;
            }

            if (_bitmap != null && before == _bufferIndex &&
                _bitmap.PixelWidth == width && _bitmap.PixelHeight == height)
            {
                return;
            }

            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                _image.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _buffer, width * 4, 0);
        }

        public void Dispose()
        {
            _image.Source = null;
            _bitmap = null;
            _buffer = null;
            _tap.Dispose();
        }
    }
}
