using System;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The blur for the live SAN backdrop, which runs on every captured frame (60 a second), so it is
    /// written for speed. It gives the same result as <see cref="BoxBlur"/> (repeated running-sum box
    /// passes, edges clamped) but is several times cheaper:
    /// <list type="bullet">
    /// <item>it blurs the three colour channels and leaves alpha alone (the caller makes it opaque),</item>
    /// <item>the horizontal pass works on a padded copy of each row, so the inner loop has no edge
    /// clamping and no calls,</item>
    /// <item>the vertical pass keeps one running sum per column and walks rows, which is sequential
    /// in memory, instead of walking each column down the image.</item>
    /// </list>
    /// Instances keep their scratch buffers, so the steady state allocates nothing. One thread at a time.
    /// </summary>
    internal sealed class FastBoxBlur
    {
        private byte[] _line = new byte[0];
        private byte[] _copy = new byte[0];
        private int[] _columns = new int[0];

        /// <summary>Box passes per blur; three are close to a gaussian.</summary>
        public const int Passes = BoxBlur.Passes;

        /// <summary>
        /// Blurs <paramref name="bgra"/> in place: <paramref name="width"/> x <paramref name="height"/>
        /// pixels of 4 bytes, rows packed at width * 4. <paramref name="radius"/> is the per-pass box
        /// radius (see <see cref="BoxBlur.RadiusFor"/>). The array may be longer than the image.
        /// </summary>
        public void Apply(byte[] bgra, int width, int height, int radius)
        {
            if (bgra == null || width <= 0 || height <= 0 || radius <= 0 || bgra.Length < width * height * 4)
            {
                return;
            }

            // A box wider than the image would only average the clamped edge with itself.
            radius = Math.Min(radius, Math.Max(width, height));
            var span = radius * 2 + 1;
            var multiplier = (1 << 16) / span;

            EnsureCapacity(width, height, radius);
            for (var pass = 0; pass < Passes; pass++)
            {
                Horizontal(bgra, width, height, radius, span, multiplier);
                Vertical(bgra, width, height, radius, multiplier);
            }
        }

        private void EnsureCapacity(int width, int height, int radius)
        {
            var line = (width + 2 * radius) * 3;
            if (_line.Length < line)
            {
                _line = new byte[line];
            }

            var plane = width * height * 4;
            if (_copy.Length < plane)
            {
                _copy = new byte[plane];
            }

            var columns = width * 3;
            if (_columns.Length < columns)
            {
                _columns = new int[columns];
            }
        }

        private void Horizontal(byte[] px, int width, int height, int radius, int span, int multiplier)
        {
            var line = _line;
            var stride = width * 4;
            for (var row = 0; row < height; row++)
            {
                var start = row * stride;

                // The row as BGR triples, padded by the edge pixel on both sides, so the window for
                // output x is simply line[x .. x + span - 1].
                var b0 = px[start];
                var g0 = px[start + 1];
                var r0 = px[start + 2];
                for (var i = 0; i < radius; i++)
                {
                    var q = i * 3;
                    line[q] = b0;
                    line[q + 1] = g0;
                    line[q + 2] = r0;
                }

                for (var x = 0; x < width; x++)
                {
                    var src = start + x * 4;
                    var q = (radius + x) * 3;
                    line[q] = px[src];
                    line[q + 1] = px[src + 1];
                    line[q + 2] = px[src + 2];
                }

                var last = start + (width - 1) * 4;
                var bl = px[last];
                var gl = px[last + 1];
                var rl = px[last + 2];
                for (var i = 0; i < radius; i++)
                {
                    var q = (radius + width + i) * 3;
                    line[q] = bl;
                    line[q + 1] = gl;
                    line[q + 2] = rl;
                }

                int sumB = 0, sumG = 0, sumR = 0;
                for (var i = 0; i < span; i++)
                {
                    var q = i * 3;
                    sumB += line[q];
                    sumG += line[q + 1];
                    sumR += line[q + 2];
                }

                var lastX = width - 1;
                for (var x = 0; x < width; x++)
                {
                    var o = start + x * 4;
                    px[o] = (byte)((sumB * multiplier + 32768) >> 16);
                    px[o + 1] = (byte)((sumG * multiplier + 32768) >> 16);
                    px[o + 2] = (byte)((sumR * multiplier + 32768) >> 16);
                    if (x < lastX)
                    {
                        var add = (x + span) * 3;
                        var sub = x * 3;
                        sumB += line[add] - line[sub];
                        sumG += line[add + 1] - line[sub + 1];
                        sumR += line[add + 2] - line[sub + 2];
                    }
                }
            }
        }

        private void Vertical(byte[] px, int width, int height, int radius, int multiplier)
        {
            var copy = _copy;
            var columns = _columns;
            var stride = width * 4;
            var cells = width * 3;

            // The rows being read must not change while the output is written.
            Buffer.BlockCopy(px, 0, copy, 0, stride * height);

            Array.Clear(columns, 0, cells);
            for (var k = -radius; k <= radius; k++)
            {
                var rowStart = Clamp(k, height - 1) * stride;
                for (var x = 0; x < width; x++)
                {
                    var src = rowStart + x * 4;
                    var c = x * 3;
                    columns[c] += copy[src];
                    columns[c + 1] += copy[src + 1];
                    columns[c + 2] += copy[src + 2];
                }
            }

            var lastRow = height - 1;
            for (var y = 0; y < height; y++)
            {
                var outStart = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var o = outStart + x * 4;
                    var c = x * 3;
                    px[o] = (byte)((columns[c] * multiplier + 32768) >> 16);
                    px[o + 1] = (byte)((columns[c + 1] * multiplier + 32768) >> 16);
                    px[o + 2] = (byte)((columns[c + 2] * multiplier + 32768) >> 16);
                }

                if (y < lastRow)
                {
                    var addStart = Math.Min(y + radius + 1, lastRow) * stride;
                    var subStart = Math.Max(y - radius, 0) * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var a = addStart + x * 4;
                        var s = subStart + x * 4;
                        var c = x * 3;
                        columns[c] += copy[a] - copy[s];
                        columns[c + 1] += copy[a + 1] - copy[s + 1];
                        columns[c + 2] += copy[a + 2] - copy[s + 2];
                    }
                }
            }
        }

        private static int Clamp(int value, int max)
        {
            return value < 0 ? 0 : (value > max ? max : value);
        }
    }
}
