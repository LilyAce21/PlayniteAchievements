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
    /// One model to draw: its element boxes, how its face textures resolve, and where it sits (an
    /// item definition's composite part transformation, in block units).
    /// </summary>
    internal sealed class MinecraftRenderPart
    {
        public JArray Elements { get; set; }

        /// <summary>
        /// Maps a face's texture reference (such as <c>#side</c>) to its decoded texture and whether
        /// it takes the foliage tint.
        /// </summary>
        public Func<string, (MinecraftTexture Texture, bool Foliage)?> ResolveTexture { get; set; }

        /// <summary>The part's transformation (translation, rotations, scale), or null for none.</summary>
        public JObject Transformation { get; set; }
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
        /// BGRA pixels of a <paramref name="size"/>-square icon of one model, or null when no face
        /// could be textured.
        /// </summary>
        public static byte[] Render(
            JArray elements,
            Func<string, (MinecraftTexture Texture, bool Foliage)?> resolveTexture,
            double rotationX,
            double rotationY,
            int size)
        {
            return Render(new[] { new MinecraftRenderPart { Elements = elements, ResolveTexture = resolveTexture } }, rotationX, rotationY, size);
        }

        /// <summary>
        /// BGRA pixels of a <paramref name="size"/>-square icon of every part drawn together (a bed's
        /// head and foot), or null when no face could be textured. A drawing larger than one block
        /// is scaled down and centred to fit; a smaller one keeps the full-block scale, as a slab
        /// does in the inventory.
        /// </summary>
        public static byte[] Render(IReadOnlyList<MinecraftRenderPart> parts, double rotationX, double rotationY, int size)
        {
            if (parts == null || size <= 0)
            {
                return null;
            }

            var faces = new List<ProjectedFace>();
            foreach (var part in parts.Where(part => part?.Elements != null && part.ResolveTexture != null))
            {
                foreach (var element in part.Elements.OfType<JObject>())
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

                        var projected = ProjectFace(element, faceName, face, from.Value, to.Value, part.Transformation, rotationX, rotationY, part.ResolveTexture);
                        if (projected != null)
                        {
                            faces.Add(projected);
                        }
                    }
                }
            }

            if (faces.Count == 0)
            {
                return null;
            }

            var scale = size * 0.94 / CubeExtent;
            var centerX = 0.0;
            var centerY = 0.0;
            var corners = faces.SelectMany(face => new[]
            {
                face.Topleft,
                face.TopRight,
                face.BottomLeft,
                new Vec(face.TopRight.X + face.BottomLeft.X - face.Topleft.X, face.TopRight.Y + face.BottomLeft.Y - face.Topleft.Y, 0)
            }).ToList();
            var minX = corners.Min(corner => corner.X);
            var maxX = corners.Max(corner => corner.X);
            var minY = corners.Min(corner => corner.Y);
            var maxY = corners.Max(corner => corner.Y);
            var limit = CubeExtent / 2 + 0.01;
            if (minX < -limit || maxX > limit || minY < -limit || maxY > limit)
            {
                scale = size * 0.94 / Math.Max(maxX - minX, maxY - minY);
                centerX = (minX + maxX) / 2;
                centerY = (minY + maxY) / 2;
            }

            var pixels = new byte[size * size * 4];
            foreach (var face in faces.OrderBy(face => face.Depth))
            {
                Rasterize(face, pixels, size, scale, centerX, centerY);
            }

            return pixels.Where((value, index) => index % 4 == 3).Any(alpha => alpha > 0) ? pixels : null;
        }

        private static ProjectedFace ProjectFace(
            JObject element,
            string faceName,
            JObject face,
            Vec from,
            Vec to,
            JObject transformation,
            double rotationX,
            double rotationY,
            Func<string, (MinecraftTexture Texture, bool Foliage)?> resolveTexture)
        {
            var (topLeft, topRight, bottomLeft, normal) = FaceCorners(faceName, from, to);
            var elementRotation = element["rotation"] as JObject;
            Vec Place(Vec point) => View(Transform(RotateElement(point, elementRotation), transformation, isDirection: false), rotationX, rotationY);
            topLeft = Place(topLeft);
            topRight = Place(topRight);
            bottomLeft = Place(bottomLeft);
            var viewNormal = View(
                Transform(RotateElement(normal, elementRotation, aboutOrigin: false), transformation, isDirection: true),
                rotationX,
                rotationY,
                centered: false);

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
        /// Applies an item definition's part transformation, in block units: right rotation, then
        /// scale, then left rotation, then translation (rotations are quaternions x, y, z, w). A
        /// direction skips scale and translation.
        /// </summary>
        private static Vec Transform(Vec point, JObject transformation, bool isDirection)
        {
            if (transformation == null)
            {
                return point;
            }

            var v = isDirection ? point : new Vec(point.X / 16, point.Y / 16, point.Z / 16);
            v = Rotate(v, ReadQuaternion(transformation["right_rotation"]));
            if (!isDirection)
            {
                var scale = ReadVec(transformation["scale"]) ?? new Vec(1, 1, 1);
                v = new Vec(v.X * scale.X, v.Y * scale.Y, v.Z * scale.Z);
            }

            v = Rotate(v, ReadQuaternion(transformation["left_rotation"]));
            if (isDirection)
            {
                return v;
            }

            var translation = ReadVec(transformation["translation"]) ?? new Vec(0, 0, 0);
            return new Vec((v.X + translation.X) * 16, (v.Y + translation.Y) * 16, (v.Z + translation.Z) * 16);
        }

        private static (double X, double Y, double Z, double W) ReadQuaternion(JToken token)
        {
            var values = (token as JArray)?.Select(value => value.Type == JTokenType.Integer || value.Type == JTokenType.Float ? (double)value : double.NaN).ToList();
            return values != null && values.Count == 4 && values.All(value => !double.IsNaN(value))
                ? (values[0], values[1], values[2], values[3])
                : (0, 0, 0, 1);
        }

        private static Vec Rotate(Vec v, (double X, double Y, double Z, double W) q)
        {
            // v' = q v q*, expanded.
            var tx = 2 * (q.Y * v.Z - q.Z * v.Y);
            var ty = 2 * (q.Z * v.X - q.X * v.Z);
            var tz = 2 * (q.X * v.Y - q.Y * v.X);
            return new Vec(
                v.X + q.W * tx + (q.Y * tz - q.Z * ty),
                v.Y + q.W * ty + (q.Z * tx - q.X * tz),
                v.Z + q.W * tz + (q.X * ty - q.Y * tx));
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

        private static void Rasterize(ProjectedFace face, byte[] pixels, int size, double scale, double centerX, double centerY)
        {
            // Screen space: x right, y down; (centerX, centerY) in view units lands mid-icon.
            var mid = size / 2.0;
            double Sx(Vec v) => mid + (v.X - centerX) * scale;
            double Sy(Vec v) => mid - (v.Y - centerY) * scale;
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
