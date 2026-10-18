using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The screenshot frame's chrome for a framed unlock clip whose frame carries animated
    /// images: the timelines of those images, each looping from its first frame from the moment
    /// the frame first shows, and a renderer that draws the chrome at a time into that. Two times
    /// whose images all show the same frames draw the same chrome, which is what
    /// <see cref="StateAt"/> identifies.
    /// </summary>
    internal sealed class FrameChromeAnimation
    {
        private readonly Func<long, FrameChromeImage> _renderAt;

        /// <param name="timelines">Frame delays in milliseconds, one list per animated image.</param>
        /// <param name="renderAt">
        /// Draws the chrome at a time in milliseconds into the frame, at the clip's frame size;
        /// null on failure. Called from the export thread.
        /// </param>
        public FrameChromeAnimation(IReadOnlyList<IReadOnlyList<int>> timelines, Func<long, FrameChromeImage> renderAt)
        {
            Timelines = timelines ?? throw new ArgumentNullException(nameof(timelines));
            _renderAt = renderAt ?? throw new ArgumentNullException(nameof(renderAt));
        }

        public IReadOnlyList<IReadOnlyList<int>> Timelines { get; }

        /// <summary>
        /// Identifies what the chrome shows <paramref name="timeMs"/> into the frame: the frame
        /// index of every image at that point of its own loop.
        /// </summary>
        public string StateAt(long timeMs)
        {
            var time = Math.Max(0, timeMs);
            var parts = new string[Timelines.Count];
            for (var i = 0; i < Timelines.Count; i++)
            {
                var delays = Timelines[i];
                var duration = 0L;
                foreach (var delay in delays)
                {
                    duration += delay;
                }

                var index = duration > 0 ? AnimatedImageKeyframe.FrameIndexAt(delays, (int)(time % duration)) : 0;
                parts[i] = index.ToString();
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// The chrome at <paramref name="timeMs"/>, or null when it cannot be drawn. After one
        /// failure (an unavailable UI thread times the draw out) every later call returns null at
        /// once, so the export falls back to the still instead of waiting out each draw.
        /// </summary>
        public FrameChromeImage RenderAt(long timeMs)
        {
            if (_failed)
            {
                return null;
            }

            FrameChromeImage chrome = null;
            try
            {
                chrome = _renderAt(Math.Max(0, timeMs));
            }
            catch
            {
                chrome = null;
            }

            if (chrome == null || !chrome.IsValid)
            {
                _failed = true;
                return null;
            }

            return chrome;
        }

        private volatile bool _failed;
    }
}
