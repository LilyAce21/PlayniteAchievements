using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Minecraft
{
    /// <summary>A decoded texture: BGRA pixels of its first animation frame.</summary>
    internal sealed class MinecraftTexture
    {
        public byte[] Pixels { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// Draws a block model as the game shows it in the inventory: its element boxes seen through
    /// the model's gui display rotation (30°, 225° for ordinary blocks), visible faces textured with
    /// nearest-neighbour sampling, shaded like inventory lighting and drawn back to front. Pure pixel
    /// arithmetic, so it runs on any thread.
    /// </summary>
    internal static class MinecraftBlockRenderer
    {
        // Default grass and foliage colours the inventory uses for tinted faces.
        private const uint GrassTint = 0x91BD59;
        private const uint FoliageTint = 0x48B518;

        // A full cube at 30°/225° spans about 25.2 units vertically; this fills the icon with a margin.
        private const double CubeExtent = 25.2;

        private static readonly string[] FaceNames = { "down", "up", "north", "south", "west", "east" };

        private sealed class ProjectedFace
        {
            public Vec Topleft;
            public Vec TopRight;
            public Vec BottomLeft;
            public double Depth;
            public double Shade;
            public MinecraftTexture Texture;
            public double U1, V1, U2, V2;
            public int Rotation;
            public uint? Tint;
        }

        private struct Vec
        {
            public double X, Y, Z;

            public Vec(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        /// <summary>
        /// BGRA pixels of a <paramref name="size"/>-square icon, or null when no face could be
        /// textured. <paramref name="resolveTexture"/> maps a face's texture reference (such as
        /// <c>#side</c>) and returns its decoded texture and whether it should take the foliage tint.
        /// </summary>
        public static byte[] Render(
            JArray elements,
            Func<string, (MinecraftTexture Texture, bool Foliage)?> resolveTexture,
            double rotationX,
            double rotationY,
            int size)
        {
            if (elements == null || resolveTexture == null || size <= 0)
            {
                return null;
            }

            var faces = new List<ProjectedFace>();
            foreach (var element in elements.OfType<JObject>())
            {
                var from = ReadVec(element["from"]);
                var to = ReadVec(element["to"]);
                if (!from.HasValue || !to.HasValue || !(element["faces"] is JObject elementFaces))
                {
                    continue;
                }

                foreach (var faceName in FaceNames)
                {
                    if (!(elementFaces[faceName] is JObject face))
                    {
                        continue;
                    }

                    var projected = ProjectFace(element, faceName, face, from.Value, to.Value, rotationX, rotationY, resolveTexture);
                    if (projected != null)
                    {
                        faces.Add(projected);
                    }
                }
            }

            if (faces.Count == 0)
            {
                return null;
            }

            var pixels = new byte[size * size * 4];
            var scale = size * 0.94 / CubeExtent;
            var center = size / 2.0;
            foreach (var face in faces.OrderBy(face => face.Depth))
            {
                Rasterize(face, pixels, size, scale, center);
            }

            return pixels.Where((value, index) => index % 4 == 3).Any(alpha => alpha > 0) ? pixels : null;
        }

        private static ProjectedFace ProjectFace(
            JObject element,
            string faceName,
            JObject face,
            Vec from,
            Vec to,
            double rotationX,
            double rotationY,
            Func<string, (MinecraftTexture Texture, bool Foliage)?> resolveTexture)
        {
            var (topLeft, topRight, bottomLeft, normal) = FaceCorners(faceName, from, to);
            var elementRotation = element["rotation"] as JObject;
            topLeft = View(RotateElement(topLeft, elementRotation), rotationX, rotationY);
            topRight = View(RotateElement(topRight, elementRotation), rotationX, rotationY);
            bottomLeft = View(RotateElement(bottomLeft, elementRotation), rotationX, rotationY);
            var viewNormal = View(RotateElement(normal, elementRotation, aboutOrigin: false), rotationX, rotationY, centered: false);

            // Faces turned away from the viewer are hidden.
            if (viewNormal.Z <= 1e-6)
            {
                return null;
            }

            var resolved = resolveTexture((string)(face["texture"] as JValue));
            if (!resolved.HasValue || resolved.Value.Texture == null)
            {
                return null;
            }

            var uv = ReadUv(face["uv"]) ?? DefaultUv(faceName, from, to);
            var bottomRight = new Vec(topRight.X + bottomLeft.X - topLeft.X, topRight.Y + bottomLeft.Y - topLeft.Y, topRight.Z + bottomLeft.Z - topLeft.Z);
            return new ProjectedFace
            {
                Topleft = topLeft,
                TopRight = topRight,
                BottomLeft = bottomLeft,
                Depth = (topLeft.Z + bottomRight.Z) / 2,
                Shade = viewNormal.Y > 0.5 ? 1.0 : viewNormal.X < 0 ? 0.8 : 0.6,
                Texture = resolved.Value.Texture,
                U1 = uv[0],
                V1 = uv[1],
                U2 = uv[2],
                V2 = uv[3],
                Rotation = face["rotation"]?.Type == JTokenType.Integer ? (int)face["rotation"] : 0,
                Tint = face["tintindex"] != null ? (resolved.Value.Foliage ? FoliageTint : GrassTint) : (uint?)null
            };
        }

        /// <summary>
        /// The face's texture corners in block space (top-left, top-right, bottom-left as the face is
        /// seen from outside) and its outward normal.
        /// </summary>
        private static (Vec TopLeft, Vec TopRight, Vec BottomLeft, Vec Normal) FaceCorners(string face, Vec a, Vec b)
        {
            switch (face)
            {
                case "north": return (new Vec(b.X, b.Y, a.Z), new Vec(a.X, b.Y, a.Z), new Vec(b.X, a.Y, a.Z), new Vec(0, 0, -1));
                case "south": return (new Vec(a.X, b.Y, b.Z), new Vec(b.X, b.Y, b.Z), new Vec(a.X, a.Y, b.Z), new Vec(0, 0, 1));
                case "east": return (new Vec(b.X, b.Y, b.Z), new Vec(b.X, b.Y, a.Z), new Vec(b.X, a.Y, b.Z), new Vec(1, 0, 0));
                case "west": return (new Vec(a.X, b.Y, a.Z), new Vec(a.X, b.Y, b.Z), new Vec(a.X, a.Y, a.Z), new Vec(-1, 0, 0));
                case "up": return (new Vec(a.X, b.Y, a.Z), new Vec(b.X, b.Y, a.Z), new Vec(a.X, b.Y, b.Z), new Vec(0, 1, 0));
                default: return (new Vec(a.X, a.Y, b.Z), new Vec(b.X, a.Y, b.Z), new Vec(a.X, a.Y, a.Z), new Vec(0, -1, 0));
            }
        }

        /// <summary>The game's default face UVs (0-16) for an element box.</summary>
        private static double[] DefaultUv(string face, Vec a, Vec b)
        {
            switch (face)
            {
                case "down": return new[] { a.X, 16 - b.Z, b.X, 16 - a.Z };
                case "up": return new[] { a.X, a.Z, b.X, b.Z };
                case "north": return new[] { 16 - b.X, 16 - b.Y, 16 - a.X, 16 - a.Y };
                case "south": return new[] { a.X, 16 - b.Y, b.X, 16 - a.Y };
                case "west": return new[] { a.Z, 16 - b.Y, b.Z, 16 - a.Y };
                default: return new[] { 16 - b.Z, 16 - b.Y, 16 - a.Z, 16 - a.Y };
            }
        }

        /// <summary>An element's own rotation (one axis, about its origin), as the model declares it.</summary>
        private static Vec RotateElement(Vec point, JObject rotation, bool aboutOrigin = true)
        {
            if (rotation == null || rotation["angle"] == null)
            {
                return point;
            }

            var angle = (double)rotation["angle"] * Math.PI / 180;
            var origin = aboutOrigin ? (ReadVec(rotation["origin"]) ?? new Vec(8, 8, 8)) : new Vec(0, 0, 0);
            var x = point.X - origin.X;
            var y = point.Y - origin.Y;
            var z = point.Z - origin.Z;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            switch ((string)(rotation["axis"] as JValue))
            {
                case "x": (y, z) = (y * cos - z * sin, y * sin + z * cos); break;
                case "y": (x, z) = (x * cos + z * sin, -x * sin + z * cos); break;
                case "z": (x, y) = (x * cos - y * sin, x * sin + y * cos); break;
            }

            return new Vec(x + origin.X, y + origin.Y, z + origin.Z);
        }

        /// <summary>
        /// The gui view: about the block centre, rotate around Y then X as the game's display
        /// transform does; X is screen right, Y screen up, Z toward the viewer.
        /// </summary>
        private static Vec View(Vec point, double rotationX, double rotationY, bool centered = true)
        {
            var x = centered ? point.X - 8 : point.X;
            var y = centered ? point.Y - 8 : point.Y;
            var z = centered ? point.Z - 8 : point.Z;
            var ry = rotationY * Math.PI / 180;
            var rx = rotationX * Math.PI / 180;
            var x1 = x * Math.Cos(ry) + z * Math.Sin(ry);
            var z1 = -x * Math.Sin(ry) + z * Math.Cos(ry);
            var y2 = y * Math.Cos(rx) - z1 * Math.Sin(rx);
            var z2 = y * Math.Sin(rx) + z1 * Math.Cos(rx);
            return new Vec(x1, y2, z2);
        }

        private static void Rasterize(ProjectedFace face, byte[] pixels, int size, double scale, double center)
        {
            // Screen space: x right, y down.
            double Sx(Vec v) => center + v.X * scale;
            double Sy(Vec v) => center - v.Y * scale;
            var p0x = Sx(face.Topleft);
            var p0y = Sy(face.Topleft);
            var e1x = Sx(face.TopRight) - p0x;
            var e1y = Sy(face.TopRight) - p0y;
            var e2x = Sx(face.BottomLeft) - p0x;
            var e2y = Sy(face.BottomLeft) - p0y;
            var det = e1x * e2y - e1y * e2x;
            if (Math.Abs(det) < 1e-6)
            {
                return;
            }

            var xs = new[] { p0x, p0x + e1x, p0x + e2x, p0x + e1x + e2x };
            var ys = new[] { p0y, p0y + e1y, p0y + e2y, p0y + e1y + e2y };
            var minX = Math.Max(0, (int)Math.Floor(xs.Min()));
            var maxX = Math.Min(size - 1, (int)Math.Ceiling(xs.Max()));
            var minY = Math.Max(0, (int)Math.Floor(ys.Min()));
            var maxY = Math.Min(size - 1, (int)Math.Ceiling(ys.Max()));
            var texture = face.Texture;
            for (var py = minY; py <= maxY; py++)
            {
                for (var px = minX; px <= maxX; px++)
                {
                    var qx = px + 0.5 - p0x;
                    var qy = py + 0.5 - p0y;
                    var s = (qx * e2y - qy * e2x) / det;
                    var t = (e1x * qy - e1y * qx) / det;
                    if (s < 0 || s >= 1 || t < 0 || t >= 1)
                    {
                        continue;
                    }

                    double rs = s, rt = t;
                    switch (((face.Rotation % 360) + 360) % 360)
                    {
                        case 90: (rs, rt) = (t, 1 - s); break;
                        case 180: (rs, rt) = (1 - s, 1 - t); break;
                        case 270: (rs, rt) = (1 - t, s); break;
                    }

                    var u = face.U1 + rs * (face.U2 - face.U1);
                    var v = face.V1 + rt * (face.V2 - face.V1);
                    var tx = Clamp((int)Math.Floor(u / 16 * texture.Width), texture.Width);
                    var ty = Clamp((int)Math.Floor(v / 16 * texture.Height), texture.Height);
                    var source = (ty * texture.Width + tx) * 4;
                    var alpha = texture.Pixels[source + 3] / 255.0;
                    if (alpha <= 0)
                    {
                        continue;
                    }

                    var b = texture.Pixels[source] * face.Shade;
                    var g = texture.Pixels[source + 1] * face.Shade;
                    var r = texture.Pixels[source + 2] * face.Shade;
                    if (face.Tint.HasValue)
                    {
                        r *= ((face.Tint.Value >> 16) & 0xFF) / 255.0;
                        g *= ((face.Tint.Value >> 8) & 0xFF) / 255.0;
                        b *= (face.Tint.Value & 0xFF) / 255.0;
                    }

                    var target = (py * size + px) * 4;
                    var below = pixels[target + 3] / 255.0;
                    var outAlpha = alpha + below * (1 - alpha);
                    pixels[target] = Blend(b, pixels[target], alpha, below, outAlpha);
                    pixels[target + 1] = Blend(g, pixels[target + 1], alpha, below, outAlpha);
                    pixels[target + 2] = Blend(r, pixels[target + 2], alpha, below, outAlpha);
                    pixels[target + 3] = (byte)Math.Round(outAlpha * 255);
                }
            }
        }

        private static byte Blend(double top, byte bottom, double alpha, double belowAlpha, double outAlpha)
        {
            if (outAlpha <= 0)
            {
                return 0;
            }

            var value = (top * alpha + bottom * belowAlpha * (1 - alpha)) / outAlpha;
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
        }

        private static int Clamp(int value, int length) => value < 0 ? 0 : value >= length ? length - 1 : value;

        private static Vec? ReadVec(JToken token)
        {
            var values = (token as JArray)?.Select(value => value.Type == JTokenType.Integer || value.Type == JTokenType.Float ? (double)value : double.NaN).ToList();
            return values != null && values.Count == 3 && values.All(value => !double.IsNaN(value))
                ? new Vec(values[0], values[1], values[2])
                : (Vec?)null;
        }

        private static double[] ReadUv(JToken token)
        {
            var values = (token as JArray)?.Select(value => value.Type == JTokenType.Integer || value.Type == JTokenType.Float ? (double)value : double.NaN).ToArray();
            return values != null && values.Length == 4 && values.All(value => !double.IsNaN(value)) ? values : null;
        }
    }
}
