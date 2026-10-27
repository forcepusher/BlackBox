using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>A surface material: a colour, and a picture painted on through texture coordinates.</summary>
public sealed class MeshMaterial
{
    public SKColor Color { get; init; } = new(200, 200, 200);
    public SKBitmap? Texture { get; init; }
}

/// <summary>
/// A 3D model's triangles, read from glTF/GLB, OBJ, PLY, STL or FBX, enough to draw it: positions (with
/// the file's node transforms applied), texture coordinates, vertex colours and materials; an FBX
/// file's animations too, to pose it at any time of them (see <see cref="Pose"/>).
/// </summary>
public sealed partial class Mesh : IDisposable
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector2> Uvs { get; } = [];
    public List<SKColor> Colors { get; } = [];
    public List<int> Indices { get; } = [];

    /// <summary>The material of each triangle (an index into <see cref="Materials"/>).</summary>
    public List<int> TriangleMaterials { get; } = [];

    public List<MeshMaterial> Materials { get; } = [];

    /// <summary>What couldn't be read (a compressed mesh, a missing texture file).</summary>
    public List<string> Notes { get; } = [];

    public bool HasVertexColors { get; private set; }

    public int TriangleCount => Indices.Count / 3;

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Positions.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    /// <summary>"3D model: 125,402 triangles, 64,113 vertices, textured, 1.20 × 0.80 × 1.00 in size".</summary>
    public string Describe()
    {
        var (min, max) = Bounds();
        var size = max - min;
        var look = Materials.Any(m => m.Texture is not null) ? "textured" : HasVertexColors ? "with vertex colours" : Materials.Count > 1 ? $"{Materials.Count} materials" : "untextured";
        var text = $"3D model: {TriangleCount:N0} triangles, {Positions.Count:N0} vertices, {look}, " +
                   $"{size.X.ToString("0.###", CultureInfo.InvariantCulture)} × {size.Y.ToString("0.###", CultureInfo.InvariantCulture)} × {size.Z.ToString("0.###", CultureInfo.InvariantCulture)} in size (x × y × z)";
        if (DescribeMotion() is { } motion)
            text += ", " + motion;
        return Notes.Count > 0 ? text + " (" + string.Join("; ", Notes.Distinct()) + ")" : text;
    }

    public void Dispose()
    {
        foreach (var m in Materials)
            m.Texture?.Dispose();
    }

    /// <summary>Reads a .glb, .gltf, .obj, .ply, .stl or .fbx file.</summary>
    public static Mesh Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".glb" or ".gltf" => Gltf.Load(path),
        ".obj" => Obj.Load(path),
        ".ply" => Ply.Load(path),
        ".stl" => Stl.Load(path),
        ".fbx" => Fbx.Load(path),
        var ext => throw new NotSupportedException($"{ext} models can't be read here; .glb, .gltf, .obj, .ply, .stl and .fbx can."),
    };

    private int AddMaterial(MeshMaterial material)
    {
        Materials.Add(material);
        return Materials.Count - 1;
    }

    private void AddTriangle(int a, int b, int c, int material)
    {
        Indices.Add(a);
        Indices.Add(b);
        Indices.Add(c);
        TriangleMaterials.Add(material);
    }

    private int AddVertex(Vector3 position, Vector2 uv, SKColor? color)
    {
        Positions.Add(position);
        Uvs.Add(uv);
        Colors.Add(color ?? SKColors.White);
        if (color is not null) HasVertexColors = true;
        return Positions.Count - 1;
    }

    // ---- glTF / GLB -------------------------------------------------------------------------

    private static class Gltf
    {
        public static Mesh Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            JsonObject json;
            byte[]? bin = null;
            if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546C67)
            {
                var at = 12;
                json = [];
                while (at + 8 <= bytes.Length)
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                    var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                    if (at + 8 + length > bytes.Length) break;
                    if (type == 0x4E4F534A)
                        json = JsonNode.Parse(bytes.AsSpan(at + 8, length)) as JsonObject ?? throw new InvalidDataException("The GLB's JSON is empty.");
                    else if (type == 0x004E4942)
                        bin = bytes.AsSpan(at + 8, length).ToArray();
                    at += 8 + length;
                }
            }
            else
                json = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Not a glTF file.");

            var dir = Path.GetDirectoryName(path)!;
            var buffers = (json["buffers"] as JsonArray ?? []).Select((b, i) => LoadBuffer(b as JsonObject, i == 0 ? bin : null, dir)).ToList();
            var mesh = new Mesh();
            var materials = new Dictionary<int, int>();
            var textures = new Dictionary<int, SKBitmap?>();

            var scene = json["scenes"]?[(int?)json["scene"] ?? 0] as JsonObject;
            var roots = scene?["nodes"] is JsonArray r ? r.Select(n => (int?)n ?? -1).ToList()
                : Enumerable.Range(0, (json["nodes"] as JsonArray)?.Count ?? 0).ToList();
            // A node tree that loops back on itself, each node with two children, would be walked
            // 2^64 times within the depth limit: the walk ends once it has seen every node a few times.
            var visits = 0;
            var maxVisits = Math.Max(100_000, 4 * ((json["nodes"] as JsonArray)?.Count ?? 0));
            if (json["nodes"] is null && json["meshes"] is JsonArray meshesOnly)
                for (var m = 0; m < meshesOnly.Count; m++)
                    AddMesh(m, Matrix4x4.Identity);
            else
                foreach (var root in roots)
                    Visit(root, Matrix4x4.Identity, 0);
            return mesh;

            void Visit(int index, Matrix4x4 parent, int depth)
            {
                if (depth > 64 || index < 0 || ++visits > maxVisits || json["nodes"] is not JsonArray nodes || index >= nodes.Count || nodes[index] is not JsonObject node)
                    return;
                var world = LocalMatrix(node) * parent;
                if ((int?)node["mesh"] is { } m)
                    AddMesh(m, world);
                foreach (var child in node["children"] as JsonArray ?? [])
                    Visit((int?)child ?? -1, world, depth + 1);
            }

            void AddMesh(int index, Matrix4x4 world)
            {
                foreach (var p in (json["meshes"]?[index]?["primitives"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (p["extensions"]?["KHR_draco_mesh_compression"] is not null)
                    {
                        mesh.Notes.Add("part of it is Draco-compressed and isn't drawn");
                        continue;
                    }
                    var mode = (int?)p["mode"] ?? 4;
                    if (mode is not (4 or 5 or 6))
                        continue;
                    var attributes = p["attributes"] as JsonObject;
                    if ((int?)attributes?["POSITION"] is not { } positionAccessor)
                        continue;
                    var positions = ReadAccessor(positionAccessor);
                    (float[] Values, int Count, int Width)? uvs = (int?)attributes?["TEXCOORD_0"] is { } uvAccessor ? ReadAccessor(uvAccessor) : null;
                    (float[] Values, int Count, int Width)? colors = (int?)attributes?["COLOR_0"] is { } colorAccessor ? ReadAccessor(colorAccessor) : null;
                    var material = Material((int?)p["material"]);
                    var first = mesh.Positions.Count;
                    var colorWidth = colors is null ? 0 : colors.Value.Width;
                    for (var i = 0; i < positions.Count; i++)
                    {
                        var v = Vector3.Transform(new Vector3(positions.Values[i * 3], positions.Values[i * 3 + 1], positions.Values[i * 3 + 2]), world);
                        var uv = uvs is { } u ? new Vector2(u.Values[i * 2], u.Values[i * 2 + 1]) : Vector2.Zero;
                        SKColor? c = colors is { } col
                            ? new SKColor(Byte(col.Values[i * colorWidth]), Byte(col.Values[i * colorWidth + 1]), Byte(col.Values[i * colorWidth + 2]))
                            : null;
                        mesh.AddVertex(v, uv, c);
                    }
                    var indices = (int?)p["indices"] is { } indexAccessor
                        ? ReadAccessor(indexAccessor).Values.Select(x => (int)x).ToList()
                        : Enumerable.Range(0, positions.Count).ToList();
                    if (mode == 4)
                        for (var i = 0; i + 2 < indices.Count; i += 3)
                            mesh.AddTriangle(first + indices[i], first + indices[i + 1], first + indices[i + 2], material);
                    else if (mode == 5)
                        for (var i = 0; i + 2 < indices.Count; i++)
                            mesh.AddTriangle(first + indices[i], first + indices[i + 1 + i % 2], first + indices[i + 2 - i % 2], material);
                    else
                        for (var i = 1; i + 1 < indices.Count; i++)
                            mesh.AddTriangle(first + indices[0], first + indices[i], first + indices[i + 1], material);
                }
            }

            int Material(int? index)
            {
                var key = index ?? -1;
                if (materials.TryGetValue(key, out var known))
                    return known;
                var color = new SKColor(200, 200, 200);
                SKBitmap? texture = null;
                if (index is { } i && json["materials"]?[i] is JsonObject m)
                {
                    var pbr = m["pbrMetallicRoughness"] as JsonObject;
                    if (pbr?["baseColorFactor"] is JsonArray { Count: >= 3 } f)
                        color = new SKColor(Byte((float)(double?)f[0]!), Byte((float)(double?)f[1]!), Byte((float)(double?)f[2]!));
                    if ((int?)pbr?["baseColorTexture"]?["index"] is { } t)
                        texture = Texture(t);
                    else if (color == new SKColor(200, 200, 200) && (int?)m["extensions"]?["KHR_materials_pbrSpecularGlossiness"]?["diffuseTexture"]?["index"] is { } t2)
                        texture = Texture(t2);
                }
                return materials[key] = mesh.AddMaterial(new MeshMaterial { Color = color, Texture = texture });
            }

            SKBitmap? Texture(int index)
            {
                if (textures.TryGetValue(index, out var known))
                    return known;
                SKBitmap? bitmap = null;
                try
                {
                    var tex = json["textures"]?[index] as JsonObject;
                    var source = (int?)tex?["source"] ?? (int?)tex?["extensions"]?["EXT_texture_webp"]?["source"];
                    if (source is { } s && json["images"]?[s] is JsonObject image)
                    {
                        byte[]? data = null;
                        if ((int?)image["bufferView"] is { } view)
                            data = View(view).ToArray();
                        else if ((string?)image["uri"] is { } uri)
                            data = Uri(uri, dir);
                        if (data is not null)
                            bitmap = SKBitmap.Decode(data);
                    }
                }
                catch (Exception ex) when (ex is IOException or FormatException or ArgumentException) { }
                if (bitmap is null)
                    mesh.Notes.Add("a texture couldn't be read");
                // Big textures are scaled down: only colours are looked up, at preview sizes.
                if (bitmap is not null && Math.Max(bitmap.Width, bitmap.Height) > 1024)
                {
                    var scale = 1024.0 / Math.Max(bitmap.Width, bitmap.Height);
                    var smaller = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), SKFilterQuality.Medium);
                    bitmap.Dispose();
                    bitmap = smaller;
                }
                return textures[index] = bitmap;
            }

            ReadOnlySpan<byte> View(int index)
            {
                var view = json["bufferViews"]?[index] as JsonObject ?? throw new InvalidDataException($"bufferView {index} is missing");
                var buffer = buffers[(int?)view["buffer"] ?? 0] ?? throw new InvalidDataException("a buffer couldn't be read");
                var offset = (int?)view["byteOffset"] ?? 0;
                var length = (int?)view["byteLength"] ?? buffer.Length - offset;
                return buffer.AsSpan(offset, length);
            }

            (float[] Values, int Count, int Width) ReadAccessor(int index)
            {
                var a = json["accessors"]?[index] as JsonObject ?? throw new InvalidDataException($"accessor {index} is missing");
                var count = (int?)a["count"] ?? 0;
                var width = (string?)a["type"] switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 1 };
                var component = (int?)a["componentType"] ?? 5126;
                var normalized = (bool?)a["normalized"] ?? false;
                var size = component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
                var values = new float[count * width];
                if ((int?)a["bufferView"] is not { } viewIndex)
                    return (values, count, width); // all zeros (sparse accessors aren't read)
                var view = View(viewIndex);
                var stride = (int?)json["bufferViews"]?[viewIndex]?["byteStride"] is { } s && s > 0 ? s : size * width;
                var offset = (int?)a["byteOffset"] ?? 0;
                for (var i = 0; i < count; i++)
                    for (var c = 0; c < width; c++)
                    {
                        var at = offset + i * stride + c * size;
                        if (at + size > view.Length) break;
                        var span = view[at..];
                        values[i * width + c] = component switch
                        {
                            5126 => BinaryPrimitives.ReadSingleLittleEndian(span),
                            5125 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(span) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(span),
                            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(span) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(span),
                            5121 => normalized ? span[0] / 255f : span[0],
                            5120 => normalized ? Math.Max((sbyte)span[0] / 127f, -1) : (sbyte)span[0],
                            _ => 0,
                        };
                    }
                return (values, count, width);
            }
        }

        private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v <= 1.0001f ? v * 255 : v), 0, 255);

        private static byte[]? LoadBuffer(JsonObject? buffer, byte[]? glbBinary, string dir)
        {
            if ((string?)buffer?["uri"] is { } uri)
                return Uri(uri, dir);
            return glbBinary;
        }

        private static byte[] Uri(string uri, string dir)
        {
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            return File.ReadAllBytes(Path.Combine(dir, System.Uri.UnescapeDataString(uri)));
        }

        private static Matrix4x4 LocalMatrix(JsonObject node)
        {
            if (node["matrix"] is JsonArray { Count: 16 } m)
            {
                var v = m.Select(x => (float)((double?)x ?? 0)).ToArray();
                // glTF stores the matrix column by column; System.Numerics multiplies row vectors.
                return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            }
            var t = node["translation"] is JsonArray { Count: 3 } tr ? new Vector3((float)(double)tr[0]!, (float)(double)tr[1]!, (float)(double)tr[2]!) : Vector3.Zero;
            var r = node["rotation"] is JsonArray { Count: 4 } ro ? new Quaternion((float)(double)ro[0]!, (float)(double)ro[1]!, (float)(double)ro[2]!, (float)(double)ro[3]!) : Quaternion.Identity;
            var s = node["scale"] is JsonArray { Count: 3 } sc ? new Vector3((float)(double)sc[0]!, (float)(double)sc[1]!, (float)(double)sc[2]!) : Vector3.One;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }
    }

    // ---- OBJ --------------------------------------------------------------------------------

    private static class Obj
    {
        public static Mesh Load(string path)
        {
            var mesh = new Mesh();
            var positions = new List<Vector3>();
            var colors = new List<SKColor?>();
            var uvs = new List<Vector2>();
            var materials = new Dictionary<string, int>(StringComparer.Ordinal);
            var library = new Dictionary<string, (SKColor Color, string? Texture)>(StringComparer.Ordinal);
            var dir = Path.GetDirectoryName(path)!;
            var current = -1;
            var vertices = new Dictionary<(int, int), int>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "v" when parts.Length >= 4:
                        positions.Add(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])));
                        colors.Add(parts.Length >= 7 ? new SKColor(B(F(parts[4])), B(F(parts[5])), B(F(parts[6]))) : null);
                        break;
                    case "vt" when parts.Length >= 3:
                        uvs.Add(new Vector2(F(parts[1]), 1 - F(parts[2])));
                        break;
                    case "mtllib" when parts.Length >= 2:
                        ReadMtl(Path.Combine(dir, line[(line.IndexOf(' ') + 1)..].Trim()), library);
                        break;
                    case "usemtl" when parts.Length >= 2:
                        var name = parts[1];
                        if (!materials.TryGetValue(name, out current))
                        {
                            var (color, texture) = library.TryGetValue(name, out var m) ? m : (new SKColor(200, 200, 200), null);
                            SKBitmap? bitmap = null;
                            if (texture is not null)
                            {
                                try { bitmap = PictureFile.Decode(Path.Combine(dir, texture)); }
                                catch (Exception ex) when (ex is IOException or ArgumentException) { }
                                if (bitmap is null) mesh.Notes.Add($"texture {texture} couldn't be read");
                            }
                            current = materials[name] = mesh.AddMaterial(new MeshMaterial { Color = color, Texture = bitmap });
                        }
                        break;
                    case "f" when parts.Length >= 4:
                        if (current < 0)
                            current = materials["\0"] = mesh.AddMaterial(new MeshMaterial());
                        var corners = parts.Skip(1).Select(Corner).ToList();
                        for (var i = 1; i + 1 < corners.Count; i++)
                            mesh.AddTriangle(corners[0], corners[i], corners[i + 1], current);
                        break;
                }
            }
            return mesh;

            int Corner(string token)
            {
                var bits = token.Split('/');
                var v = Index(bits[0], positions.Count);
                var t = bits.Length > 1 && bits[1].Length > 0 ? Index(bits[1], uvs.Count) : -1;
                if (vertices.TryGetValue((v, t), out var known))
                    return known;
                return vertices[(v, t)] = mesh.AddVertex(positions[v], t >= 0 ? uvs[t] : Vector2.Zero, colors[v]);
            }
        }

        private static int Index(string s, int count)
        {
            var i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;
        }

        private static void ReadMtl(string file, Dictionary<string, (SKColor, string?)> library)
        {
            if (!File.Exists(file)) return;
            string? name = null;
            foreach (var raw in File.ReadLines(file))
            {
                var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                switch (parts[0])
                {
                    case "newmtl":
                        name = parts[1];
                        library[name] = (new SKColor(200, 200, 200), null);
                        break;
                    case "Kd" when name is not null && parts.Length >= 4:
                        library[name] = (new SKColor(B(F(parts[1])), B(F(parts[2])), B(F(parts[3]))), library[name].Item2);
                        break;
                    case "map_Kd" when name is not null:
                        library[name] = (library[name].Item1, parts[^1]);
                        break;
                }
            }
        }
    }

    // ---- PLY --------------------------------------------------------------------------------

    private static class Ply
    {
        public static Mesh Load(string path)
        {
            using var stream = File.OpenRead(path);
            var header = new List<string>();
            var line = new StringBuilder();
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0) throw new InvalidDataException("The PLY header never ends.");
                if (b == '\n')
                {
                    var text = line.ToString().Trim();
                    header.Add(text);
                    line.Clear();
                    if (text == "end_header") break;
                }
                else line.Append((char)b);
            }
            var format = header.FirstOrDefault(h => h.StartsWith("format ", StringComparison.Ordinal))?.Split(' ')[1] ?? "ascii";
            var elements = new List<(string Name, int Count, List<(string Name, string Type, string? CountType)> Props)>();
            foreach (var h in header)
            {
                var p = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3 && p[0] == "element")
                    elements.Add((p[1], int.Parse(p[2], CultureInfo.InvariantCulture), []));
                else if (p.Length >= 3 && p[0] == "property" && elements.Count > 0)
                    elements[^1].Props.Add(p[1] == "list" && p.Length >= 5 ? (p[4], p[3], p[2]) : (p[2], p[1], null));
            }
            var mesh = new Mesh();
            var material = mesh.AddMaterial(new MeshMaterial());
            var reader = new PlyReader(stream, format);
            foreach (var (name, count, props) in elements)
            {
                for (var i = 0; i < count; i++)
                {
                    if (name == "vertex")
                    {
                        float x = 0, y = 0, z = 0, r = -1, g = -1, bl = -1, u = 0, v = 0;
                        foreach (var (prop, type, countType) in props)
                        {
                            if (countType is not null) { reader.SkipList(countType, type); continue; }
                            var value = reader.Read(type);
                            switch (prop)
                            {
                                case "x": x = (float)value; break;
                                case "y": y = (float)value; break;
                                case "z": z = (float)value; break;
                                case "red" or "r": r = (float)(Bytes(type) ? value / 255 : value); break;
                                case "green" or "g": g = (float)(Bytes(type) ? value / 255 : value); break;
                                case "blue" or "b": bl = (float)(Bytes(type) ? value / 255 : value); break;
                                case "s" or "u" or "texture_u": u = (float)value; break;
                                case "t" or "v" or "texture_v": v = (float)value; break;
                            }
                        }
                        mesh.AddVertex(new Vector3(x, y, z), new Vector2(u, 1 - v), r >= 0 ? new SKColor(B(r), B(g), B(bl)) : null);
                    }
                    else if (name == "face")
                    {
                        foreach (var (prop, type, countType) in props)
                        {
                            if (countType is null) { reader.Read(type); continue; }
                            var n = (int)reader.Read(countType);
                            var corners = new int[n];
                            for (var k = 0; k < n; k++) corners[k] = (int)reader.Read(type);
                            if (prop is "vertex_indices" or "vertex_index")
                                for (var k = 1; k + 1 < n; k++)
                                    mesh.AddTriangle(corners[0], corners[k], corners[k + 1], material);
                        }
                    }
                    else
                        foreach (var (_, type, countType) in props)
                            if (countType is not null) reader.SkipList(countType, type); else reader.Read(type);
                }
            }
            return mesh;

            static bool Bytes(string type) => type is "uchar" or "char" or "uint8" or "int8";
        }

        private sealed class PlyReader(Stream stream, string format)
        {
            private readonly BinaryReader _binary = new(stream);
            private readonly Queue<string> _tokens = new();
            private readonly StreamReader _text = new(stream);

            public double Read(string type)
            {
                if (format == "ascii")
                {
                    while (_tokens.Count == 0)
                    {
                        var line = _text.ReadLine() ?? throw new InvalidDataException("The PLY file ends early.");
                        foreach (var t in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) _tokens.Enqueue(t);
                    }
                    return double.Parse(_tokens.Dequeue(), CultureInfo.InvariantCulture);
                }
                var big = format == "binary_big_endian";
                Span<byte> b = stackalloc byte[8];
                switch (type)
                {
                    case "char" or "int8": return (sbyte)_binary.ReadByte();
                    case "uchar" or "uint8": return _binary.ReadByte();
                    case "short" or "int16": Fill(b[..2]); return big ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b);
                    case "ushort" or "uint16": Fill(b[..2]); return big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
                    case "int" or "int32": Fill(b[..4]); return big ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
                    case "uint" or "uint32": Fill(b[..4]); return big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
                    case "float" or "float32": Fill(b[..4]); return big ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b);
                    case "double" or "float64": Fill(b[..8]); return big ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b);
                    default: throw new InvalidDataException($"PLY type {type} isn't known.");
                }
            }

            public void SkipList(string countType, string type)
            {
                var n = (int)Read(countType);
                for (var i = 0; i < n; i++) Read(type);
            }

            private void Fill(Span<byte> span)
            {
                if (_binary.Read(span) != span.Length) throw new InvalidDataException("The PLY file ends early.");
            }
        }
    }

    // ---- STL --------------------------------------------------------------------------------

    private static class Stl
    {
        public static Mesh Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var mesh = new Mesh();
            var material = mesh.AddMaterial(new MeshMaterial());
            if (bytes.Length >= 84 && 84 + 50L * BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80)) == bytes.Length)
            {
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80));
                for (var i = 0; i < count; i++)
                {
                    var at = 84 + i * 50 + 12;
                    var corners = new int[3];
                    for (var k = 0; k < 3; k++)
                    {
                        var s = bytes.AsSpan(at + k * 12);
                        corners[k] = mesh.AddVertex(new Vector3(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..])), Vector2.Zero, null);
                    }
                    mesh.AddTriangle(corners[0], corners[1], corners[2], material);
                }
                return mesh;
            }
            var corner = new List<int>();
            foreach (var raw in Encoding.ASCII.GetString(bytes).Split('\n'))
            {
                var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && parts[0] == "vertex")
                {
                    corner.Add(mesh.AddVertex(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])), Vector2.Zero, null));
                    if (corner.Count == 3)
                    {
                        mesh.AddTriangle(corner[0], corner[1], corner[2], material);
                        corner.Clear();
                    }
                }
                else if (parts.Length > 0 && parts[0] == "endfacet")
                    corner.Clear();
            }
            return mesh;
        }
    }

    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
}
