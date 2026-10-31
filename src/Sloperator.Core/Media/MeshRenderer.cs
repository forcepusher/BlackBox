using System.Numerics;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// Draws a <see cref="Mesh"/> in software: perspective, a depth buffer, flat shading lit from the
/// front, texture or vertex colours. For looks at a model (the agent's, and the chat's viewer);
/// no GPU or 3D library is needed.
/// </summary>
public static class MeshRenderer
{
    private static readonly SKColor DefaultGray = new(200, 200, 200);

    /// <summary>
    /// The mesh seen from <paramref name="yawDegrees"/> around it (0: from the front, +Z; 90: from
    /// its right, +X) and <paramref name="pitchDegrees"/> above, filling most of the picture.
    /// </summary>
    /// <param name="zoom">1 fits the whole model; more comes closer.</param>
    /// <param name="supersample">Pixels drawn per output pixel along each side, for smooth edges.</param>
    /// <param name="panX">How far the model is moved right on the picture, in parts of its shorter side.</param>
    /// <param name="panY">How far it is moved down, the same.</param>
    /// <param name="positions">Its vertices posed at a time of an animation (<see cref="Mesh.Pose"/>), in place of those at rest.</param>
    /// <param name="bounds">The box it is framed by: one it stays in through an animation (<see cref="Mesh.Bounds(MeshAnimation)"/>), so it doesn't jump about as it moves.</param>
    public static SKBitmap Render(Mesh mesh, int width, int height, double yawDegrees, double pitchDegrees, double zoom = 1, int supersample = 2, double panX = 0, double panY = 0,
        IReadOnlyList<Vector3>? positions = null, (Vector3 Min, Vector3 Max)? bounds = null)
    {
        var ss = Math.Clamp(supersample, 1, 3);
        var (w, h) = (width * ss, height * ss);
        var color = new uint[w * h];
        var depth = new float[w * h]; // 1/distance: larger is nearer, 0 is empty

        // A dark gradient behind it, lighter at the top.
        for (var y = 0; y < h; y++)
        {
            var shade = (byte)(58 - 30 * y / h);
            var c = 0xFF000000u | (uint)(shade << 16) | (uint)(shade << 8) | (uint)(shade + 6);
            Array.Fill(color, c, y * w, w);
        }

        if (mesh.TriangleCount > 0)
            Draw(mesh, positions is { } posed && posed.Count == mesh.Positions.Count ? posed : mesh.Positions, bounds ?? mesh.Bounds(),
                w, h, yawDegrees, pitchDegrees, zoom, color, depth, (float)(panX * Math.Min(w, h)), (float)(panY * Math.Min(w, h)));

        var full = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy((int[])(object)color, 0, full.GetPixels(), color.Length);
        if (ss == 1)
            return full;
        using (full)
            return full.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), Sampling.High(w, width));
    }

    private static void Draw(Mesh mesh, IReadOnlyList<Vector3> positions, (Vector3 Min, Vector3 Max) bounds, int w, int h, double yawDegrees, double pitchDegrees, double zoom,
        uint[] color, float[] depth, float panX, float panY)
    {
        var (min, max) = bounds;
        var center = (min + max) / 2;
        // As far out as the box's corners: what is in the box stays in view (the farthest vertex at rest, without a box of its own).
        var radius = 0f;
        if (bounds == mesh.Bounds())
            foreach (var p in mesh.Positions)
                radius = MathF.Max(radius, Vector3.DistanceSquared(p, center));
        else
            radius = Vector3.DistanceSquared(max, center);
        radius = MathF.Max(MathF.Sqrt(radius), 1e-6f);

        var rotation = Matrix4x4.CreateRotationY((float)(-yawDegrees * Math.PI / 180)) * Matrix4x4.CreateRotationX((float)(pitchDegrees * Math.PI / 180));
        var distance = 3f * radius;
        var focal = (float)(0.47 * Math.Min(w, h) * zoom * distance / radius);
        var light = Vector3.Normalize(new Vector3(-0.35f, 0.55f, 1f));

        // Every vertex in view space once: x, y on screen, and its distance from the eye.
        var count = positions.Count;
        var sx = new float[count];
        var sy = new float[count];
        var view = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var q = Vector3.Transform(positions[i] - center, rotation);
            view[i] = q;
            var d = distance - q.Z;
            sx[i] = w / 2f + panX + focal * q.X / d;
            sy[i] = h / 2f + panY - focal * q.Y / d;
        }

        // Textures as plain colour arrays, read once.
        var textures = mesh.Materials.Select(m => m.Texture is { } t ? (Pixels: t.Pixels, W: t.Width, H: t.Height) : default).ToList();

        var idx = mesh.Indices;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
            float da = distance - view[a].Z, db = distance - view[b].Z, dc = distance - view[c].Z;
            if (da <= 1e-4f || db <= 1e-4f || dc <= 1e-4f)
                continue;
            float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b], cx = sx[c], cy = sy[c];
            var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (MathF.Abs(area) < 1e-9f)
                continue;
            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            if (minX > maxX || minY > maxY)
                continue;

            // Flat shading, both sides lit: models often have their faces wound either way.
            var normal = Vector3.Cross(view[b] - view[a], view[c] - view[a]);
            var lengthSq = normal.LengthSquared();
            var lit = lengthSq > 0 ? MathF.Abs(Vector3.Dot(normal / MathF.Sqrt(lengthSq), light)) : 1f;
            var intensity = 0.28f + 0.72f * lit;

            var material = t < mesh.TriangleMaterials.Count ? mesh.TriangleMaterials[t] : 0;
            var mat = material < mesh.Materials.Count ? mesh.Materials[material] : null;
            var texture = material < textures.Count ? textures[material] : default;
            var baseColor = mat?.Color ?? DefaultGray;
            if (mesh.HasVertexColors && texture.Pixels is null && baseColor == DefaultGray)
                baseColor = SKColors.White;
            var useVertexColors = mesh.HasVertexColors;
            var (ia, ib, ic) = (1f / da, 1f / db, 1f / dc);

            for (var y = minY; y <= maxY; y++)
            {
                var py = y + 0.5f;
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5f;
                    var w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                    var w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f)
                        continue;
                    var inv = w0 * ia + w1 * ib + w2 * ic;
                    var at = y * w + x;
                    if (inv <= depth[at])
                        continue;
                    depth[at] = inv;

                    float r = baseColor.Red, g = baseColor.Green, bl = baseColor.Blue;
                    if (texture.Pixels is not null || useVertexColors)
                    {
                        // Perspective-correct weights for what is painted on the surface.
                        var (p0, p1, p2) = (w0 * ia / inv, w1 * ib / inv, w2 * ic / inv);
                        if (texture.Pixels is not null)
                        {
                            var uv = mesh.Uvs[a] * p0 + mesh.Uvs[b] * p1 + mesh.Uvs[c] * p2;
                            var u = uv.X - MathF.Floor(uv.X);
                            var v = uv.Y - MathF.Floor(uv.Y);
                            var texel = texture.Pixels[Math.Min(texture.H - 1, (int)(v * texture.H)) * texture.W + Math.Min(texture.W - 1, (int)(u * texture.W))];
                            r = r * texel.Red / 255f;
                            g = g * texel.Green / 255f;
                            bl = bl * texel.Blue / 255f;
                        }
                        if (useVertexColors)
                        {
                            var (ca, cb, cc) = (mesh.Colors[a], mesh.Colors[b], mesh.Colors[c]);
                            r = r * (ca.Red * p0 + cb.Red * p1 + cc.Red * p2) / 255f;
                            g = g * (ca.Green * p0 + cb.Green * p1 + cc.Green * p2) / 255f;
                            bl = bl * (ca.Blue * p0 + cb.Blue * p1 + cc.Blue * p2) / 255f;
                        }
                    }
                    var R = (uint)Math.Clamp(r * intensity, 0, 255);
                    var G = (uint)Math.Clamp(g * intensity, 0, 255);
                    var B = (uint)Math.Clamp(bl * intensity, 0, 255);
                    color[at] = 0xFF000000u | (R << 16) | (G << 8) | B;
                }
            }
        }
    }
}
