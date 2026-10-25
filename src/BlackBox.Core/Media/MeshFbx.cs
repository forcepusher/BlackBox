using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using SkiaSharp;

namespace BlackBox.Core.Media;

public sealed partial class Mesh
{
    /// <summary>
    /// FBX (Autodesk's format, versions 7.x, binary or text), read by BlackBox itself (the user's
    /// choice, 2026-10-26, over Assimp's 15-20 MB and Autodesk's SDK): its meshes with their nodes'
    /// transforms (pivots, pre- and post-rotations and rotation orders as FBX has them), UVs, vertex
    /// colours, materials' colours and diffuse textures (embedded or beside the file), and its
    /// skins, bones and animation takes, to play. Not read: blend shapes, NURBS, cameras and lights.
    /// </summary>
    private static class Fbx
    {
        /// <summary>FBX time: 46,186,158,000 ticks a second.</summary>
        private const double TicksPerSecond = 46_186_158_000.0;

        /// <summary>A node of the file: its name, its values, and the nodes in it.</summary>
        internal sealed class Node(string name)
        {
            public string Name { get; } = name;
            public List<object> Props { get; } = [];
            public List<Node> Children { get; } = [];

            public Node? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

            public IEnumerable<Node> All(string name) => Children.Where(c => c.Name == name);

            /// <summary>The first value of child <paramref name="name"/>: "Vertices", "KeyTime".</summary>
            public object? Value(string name) => Child(name)?.Props.FirstOrDefault();
        }

        public static Mesh Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var root = Parse(bytes);
            return Build(root, path);
        }

        internal static Node Parse(byte[] bytes)
        {
            var magic = "Kaydara FBX Binary  \0"u8;
            if (bytes.Length >= 27 && bytes.AsSpan(0, magic.Length).SequenceEqual(magic))
                return ParseBinary(bytes);
            return ParseText(Encoding.UTF8.GetString(bytes));
        }

        // ---- binary --------------------------------------------------------------------------

        private static Node ParseBinary(byte[] b)
        {
            var version = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(23));
            var root = new Node("");
            root.Props.Add((long)version);
            var at = 27L;
            // From 7.5 on a node's offsets and counts are 64-bit.
            var wide = version >= 7500;
            while (at < b.Length && ReadNode(b, ref at, wide) is { } node)
                root.Children.Add(node);
            return root;
        }

        private static Node? ReadNode(byte[] b, ref long at, bool wide)
        {
            var head = wide ? 25 : 13;
            if (at + head > b.Length)
            {
                at = b.Length;
                return null;
            }
            var span = b.AsSpan((int)at);
            long end, count, length;
            if (wide)
                (end, count, length) = ((long)BinaryPrimitives.ReadUInt64LittleEndian(span), (long)BinaryPrimitives.ReadUInt64LittleEndian(span[8..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(span[16..]));
            else
                (end, count, length) = (BinaryPrimitives.ReadUInt32LittleEndian(span), BinaryPrimitives.ReadUInt32LittleEndian(span[4..]), BinaryPrimitives.ReadUInt32LittleEndian(span[8..]));
            int nameLength = span[head - 1];
            at += head;
            if (end == 0)
                return null; // the empty record that ends a list of nodes
            if (end > b.Length || end < at)
                throw new InvalidDataException("The FBX file is damaged: a node runs past its end.");
            var node = new Node(Encoding.ASCII.GetString(b, (int)at, nameLength));
            at += nameLength;
            var propsEnd = at + length;
            for (var i = 0; i < count && at < propsEnd; i++)
                node.Props.Add(ReadProp(b, ref at));
            at = propsEnd;
            while (at < end && ReadNode(b, ref at, wide) is { } child)
                node.Children.Add(child);
            at = end;
            return node;
        }

        private static object ReadProp(byte[] b, ref long at)
        {
            var type = (char)b[at];
            var s = b.AsSpan((int)at + 1);
            at += 1;
            switch (type)
            {
                case 'Y': at += 2; return (long)BinaryPrimitives.ReadInt16LittleEndian(s);
                case 'C': at += 1; return s[0] != 0;
                case 'I': at += 4; return (long)BinaryPrimitives.ReadInt32LittleEndian(s);
                case 'F': at += 4; return (double)BinaryPrimitives.ReadSingleLittleEndian(s);
                case 'D': at += 8; return BinaryPrimitives.ReadDoubleLittleEndian(s);
                case 'L': at += 8; return BinaryPrimitives.ReadInt64LittleEndian(s);
                case 'S' or 'R':
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(s);
                    at += 4 + length;
                    var data = s.Slice(4, length);
                    return type == 'S' ? Encoding.UTF8.GetString(data) : data.ToArray();
                }
                case 'f' or 'd' or 'l' or 'i' or 'b':
                {
                    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(s);
                    var encoding = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
                    var stored = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
                    at += 12 + stored;
                    var size = type is 'd' or 'l' ? 8 : type == 'b' ? 1 : 4;
                    var raw = s.Slice(12, stored).ToArray();
                    if (encoding == 1)
                    {
                        using var inflate = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                        raw = new byte[count * size];
                        inflate.ReadExactly(raw);
                    }
                    if (raw.Length < count * size)
                        throw new InvalidDataException("The FBX file is damaged: an array is short.");
                    switch (type)
                    {
                        case 'f':
                        {
                            var a = new double[count];
                            for (var i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
                            return a;
                        }
                        case 'd':
                        {
                            var a = new double[count];
                            for (var i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8));
                            return a;
                        }
                        case 'l':
                        {
                            var a = new long[count];
                            for (var i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(i * 8));
                            return a;
                        }
                        case 'i':
                        {
                            var a = new int[count];
                            for (var i = 0; i < count; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
                            return a;
                        }
                        default:
                            return raw.Take(count).Select(x => x != 0).ToArray();
                    }
                }
                default:
                    throw new InvalidDataException($"The FBX file has a value of a kind it doesn't know ('{type}').");
            }
        }

        // ---- text ----------------------------------------------------------------------------

        /// <summary>
        /// FBX's text form: "Name: value, value {" … "}", ";" starting a comment, and arrays as
        /// "*24 { a: 1,2,3 }". Its numbers are read as doubles, which hold FBX's times exactly.
        /// </summary>
        private static Node ParseText(string text)
        {
            var reader = new TextReader(text);
            var root = new Node("");
            reader.Nodes(root, top: true);
            var version = root.Child("FBXHeaderExtension")?.Value("FBXVersion");
            root.Props.Add(version is null ? 0L : (long)Num(version));
            return root;
        }

        private sealed class TextReader(string s)
        {
            private int _at;

            private void SkipSpace(bool lines)
            {
                while (_at < s.Length)
                {
                    var c = s[_at];
                    if (c == ';')
                        while (_at < s.Length && s[_at] != '\n') _at++;
                    else if (c is ' ' or '\t' or '\r' || lines && c == '\n')
                        _at++;
                    else
                        return;
                }
            }

            /// <summary>The nodes up to the "}" that closes them (taken), or the end.</summary>
            public void Nodes(Node parent, bool top)
            {
                while (true)
                {
                    SkipSpace(lines: true);
                    if (_at >= s.Length)
                        return;
                    if (s[_at] == '}')
                    {
                        _at++;
                        if (top) continue; // a stray one: read on
                        return;
                    }
                    var start = _at;
                    while (_at < s.Length && s[_at] != ':' && s[_at] != '\n' && s[_at] != '{' && s[_at] != '}') _at++;
                    if (_at >= s.Length || s[_at] != ':')
                        continue; // not a node: a line of something else
                    var node = new Node(s[start.._at].Trim());
                    _at++;
                    Values(node);
                    parent.Children.Add(node);
                }
            }

            private void Values(Node node)
            {
                while (true)
                {
                    SkipSpace(lines: false);
                    if (_at >= s.Length)
                        return;
                    var c = s[_at];
                    if (c == '\n')
                    {
                        _at++;
                        return;
                    }
                    if (c == '{')
                    {
                        _at++;
                        Nodes(node, top: false);
                        return;
                    }
                    if (c == '}')
                        return;
                    if (c == ',')
                    {
                        _at++;
                        SkipSpace(lines: true); // a list of values may go on on the next line
                        continue;
                    }
                    if (c == '*')
                    {
                        // An array: its count, then { a: values }.
                        _at++;
                        while (_at < s.Length && char.IsAsciiDigit(s[_at])) _at++;
                        SkipSpace(lines: true);
                        if (_at < s.Length && s[_at] == '{')
                        {
                            _at++;
                            var block = new Node("");
                            Nodes(block, top: false);
                            node.Props.Add(block.Child("a")?.Props.Select(Num).ToArray() ?? []);
                        }
                        continue;
                    }
                    if (c == '"')
                    {
                        var end = s.IndexOf('"', _at + 1);
                        if (end < 0) end = s.Length;
                        node.Props.Add(s[(_at + 1)..end].Replace("&quot;", "\"").Replace("&cr;", "\r").Replace("&lf;", "\n"));
                        _at = Math.Min(s.Length, end + 1);
                        continue;
                    }
                    var from = _at;
                    while (_at < s.Length && s[_at] is not (',' or '\n' or '\r' or '{' or '}' or ';')) _at++;
                    var word = s[from.._at].Trim();
                    node.Props.Add(double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : word);
                }
            }
        }

        // ---- values --------------------------------------------------------------------------

        private static double Num(object? value) => value switch
        {
            double d => d,
            long l => l,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };

        private static double[] Doubles(object? value) => value switch
        {
            double[] d => d,
            long[] l => l.Select(x => (double)x).ToArray(),
            int[] i => i.Select(x => (double)x).ToArray(),
            null => [],
            _ => [Num(value)],
        };

        private static int[] Ints(object? value) => value switch
        {
            int[] i => i,
            long[] l => l.Select(x => (int)x).ToArray(),
            double[] d => d.Select(x => (int)x).ToArray(),
            null => [],
            _ => [(int)Num(value)],
        };

        private static long Id(Node node) => node.Props.Count > 0 ? (long)Num(node.Props[0]) : 0;

        /// <summary>"Cube" from the binary form's "Cube\0\x01Model" or the text form's "Model::Cube".</summary>
        private static string NameOf(Node node)
        {
            var name = node.Props.Count > 1 ? node.Props[1] as string ?? "" : "";
            var cut = name.IndexOf("\0\u0001", StringComparison.Ordinal);
            if (cut >= 0)
                return name[..cut];
            cut = name.IndexOf("::", StringComparison.Ordinal);
            return cut >= 0 ? name[(cut + 2)..] : name;
        }

        private static string ClassOf(Node node) => node.Props.Count > 2 ? node.Props[2] as string ?? "" : "";

        /// <summary>A node's Properties70: each P's name and its values (after its type, label and flags).</summary>
        private static Dictionary<string, List<object>> Properties(Node? node)
        {
            var props = new Dictionary<string, List<object>>();
            foreach (var p in node?.Child("Properties70")?.All("P") ?? [])
                if (p.Props.Count > 0 && p.Props[0] is string name)
                    props[name] = p.Props.Skip(4).ToList();
            return props;
        }

        private static Vector3 Vec(Dictionary<string, List<object>> props, string name, Vector3 otherwise) =>
            props.TryGetValue(name, out var v) && v.Count >= 3 ? new Vector3((float)Num(v[0]), (float)Num(v[1]), (float)Num(v[2])) : otherwise;

        private static double Scalar(Dictionary<string, List<object>> props, string name, double otherwise) =>
            props.TryGetValue(name, out var v) && v.Count >= 1 ? Num(v[0]) : otherwise;

        private static Matrix4x4 Matrix(object? value)
        {
            var d = Doubles(value);
            if (d.Length < 16)
                return Matrix4x4.Identity;
            // FBX keeps a matrix column by column for column vectors: row by row for System.Numerics' row vectors.
            return new Matrix4x4((float)d[0], (float)d[1], (float)d[2], (float)d[3], (float)d[4], (float)d[5], (float)d[6], (float)d[7],
                (float)d[8], (float)d[9], (float)d[10], (float)d[11], (float)d[12], (float)d[13], (float)d[14], (float)d[15]);
        }

        // ---- the scene -----------------------------------------------------------------------

        private static Mesh Build(Node root, string path)
        {
            var version = root.Props.Count > 0 ? (long)Num(root.Props[0]) : 0;
            if (version is > 0 and < 7000)
                throw new NotSupportedException($"FBX {version / 1000.0:0.0} files can't be read here; FBX 7 (2011 on) can. Saving it again from a 3D program writes FBX 7.");
            var objects = new Dictionary<long, Node>();
            foreach (var o in root.Child("Objects")?.Children ?? [])
                objects[Id(o)] = o;
            // What is connected to what: a child to its parent, through one of the parent's properties for "OP".
            var children = new Dictionary<long, List<(long Child, string? Property)>>();
            var parents = new Dictionary<long, List<(long Parent, string? Property)>>();
            foreach (var c in root.Child("Connections")?.All("C") ?? [])
            {
                if (c.Props.Count < 3)
                    continue;
                var (child, parent) = ((long)Num(c.Props[1]), (long)Num(c.Props[2]));
                var property = c.Props.Count > 3 ? c.Props[3] as string : null;
                (children.TryGetValue(parent, out var cl) ? cl : children[parent] = []).Add((child, property));
                (parents.TryGetValue(child, out var pl) ? pl : parents[child] = []).Add((parent, property));
            }
            IEnumerable<Node> ChildrenOf(long id, string kind, string? property = null) =>
                (children.TryGetValue(id, out var list) ? list : []).Where(c => property is null || c.Property == property)
                    .Select(c => objects.GetValueOrDefault(c.Child)).OfType<Node>().Where(n => n.Name == kind);
            IEnumerable<Node> ParentsOf(long id, string kind) =>
                (parents.TryGetValue(id, out var list) ? list : []).Select(p => objects.GetValueOrDefault(p.Parent)).OfType<Node>().Where(n => n.Name == kind);

            var mesh = new Mesh();
            var settings = Properties(root.Child("GlobalSettings"));
            mesh.Axes = Axes(settings);

            // The tree of nodes.
            var nodeOf = new Dictionary<long, int>();
            foreach (var model in objects.Values.Where(o => o.Name == "Model"))
            {
                var p = Properties(model);
                nodeOf[Id(model)] = mesh.Nodes.Count;
                mesh.Nodes.Add(new MeshNode
                {
                    Name = NameOf(model),
                    Translation = Vec(p, "Lcl Translation", Vector3.Zero),
                    Rotation = Vec(p, "Lcl Rotation", Vector3.Zero),
                    Scale = Vec(p, "Lcl Scaling", Vector3.One),
                    PreRotation = Vec(p, "PreRotation", Vector3.Zero),
                    PostRotation = Vec(p, "PostRotation", Vector3.Zero),
                    RotationOffset = Vec(p, "RotationOffset", Vector3.Zero),
                    RotationPivot = Vec(p, "RotationPivot", Vector3.Zero),
                    ScalingOffset = Vec(p, "ScalingOffset", Vector3.Zero),
                    ScalingPivot = Vec(p, "ScalingPivot", Vector3.Zero),
                    Order = (int)Scalar(p, "RotationOrder", 0),
                });
            }
            foreach (var (id, index) in nodeOf)
                if (ParentsOf(id, "Model").FirstOrDefault() is { } parent && nodeOf.TryGetValue(Id(parent), out var p) && p != index)
                    mesh.Nodes[index].Parent = p;
            // A loop of parents (a damaged file) would never end: broken at the node that closes it.
            for (var i = 0; i < mesh.Nodes.Count; i++)
            {
                var seen = new HashSet<int> { i };
                for (var at = mesh.Nodes[i].Parent; at >= 0; at = mesh.Nodes[at].Parent)
                    if (!seen.Add(at))
                    {
                        mesh.Nodes[i].Parent = -1;
                        break;
                    }
            }

            var materials = new Dictionary<long, int>();
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var geometry in objects.Values.Where(o => o.Name == "Geometry" && ClassOf(o) == "Mesh"))
                foreach (var model in ParentsOf(Id(geometry), "Model"))
                {
                    var modelMaterials = ChildrenOf(Id(model), "Material").Select(m => Material(mesh, m, materials, ChildrenOf, dir, path)).ToList();
                    AddGeometry(mesh, geometry, model, nodeOf[Id(model)], modelMaterials, ChildrenOf, nodeOf);
                }
            if (objects.Values.Any(o => o.Name == "Deformer" && ClassOf(o) == "BlendShape"))
                mesh.Notes.Add("its blend shapes aren't played");

            // At rest: each vertex through its node.
            var rest = mesh.NodeWorlds(null, 0);
            for (var i = 0; i < mesh.SourcePositions.Count; i++)
                mesh.Positions[i] = Vector3.Transform(mesh.SourcePositions[i], rest[mesh.VertexNodes[i]]);

            ReadAnimations(mesh, objects.Values, ChildrenOf, nodeOf, FrameRate(settings));
            return mesh;
        }

        /// <summary>From the file's axes (GlobalSettings' UpAxis, FrontAxis, CoordAxis and their signs) to y up, +z toward the viewer, +x right.</summary>
        private static Matrix4x4 Axes(Dictionary<string, List<object>> s)
        {
            if (!s.ContainsKey("UpAxis"))
                return Matrix4x4.Identity;
            var (up, upSign) = ((int)Scalar(s, "UpAxis", 1), (float)Scalar(s, "UpAxisSign", 1));
            var (front, frontSign) = ((int)Scalar(s, "FrontAxis", 2), (float)Scalar(s, "FrontAxisSign", 1));
            var (coord, coordSign) = ((int)Scalar(s, "CoordAxis", 0), (float)Scalar(s, "CoordAxisSign", 1));
            if (up is < 0 or > 2 || front is < 0 or > 2 || coord is < 0 or > 2 || up == front || up == coord || front == coord)
                return Matrix4x4.Identity;
            var m = new float[4, 4];
            m[coord, 0] = coordSign;
            m[up, 1] = upSign;
            m[front, 2] = frontSign;
            m[3, 3] = 1;
            return new Matrix4x4(m[0, 0], m[0, 1], m[0, 2], 0, m[1, 0], m[1, 1], m[1, 2], 0, m[2, 0], m[2, 1], m[2, 2], 0, 0, 0, 0, 1);
        }

        /// <summary>The frames a second the file was made at: GlobalSettings' TimeMode, or its CustomFrameRate.</summary>
        private static double FrameRate(Dictionary<string, List<object>> s)
        {
            double[] modes = [30, 120, 100, 60, 50, 48, 30, 30, 29.97, 29.97, 25, 24, 1000, 23.976, 0, 96, 72, 59.94];
            var mode = (int)Scalar(s, "TimeMode", 0);
            var rate = mode == 14 || mode < 0 || mode >= modes.Length ? Scalar(s, "CustomFrameRate", 30) : modes[mode];
            return rate is > 0 and <= 1000 ? rate : 30;
        }

        private static int Material(Mesh mesh, Node material, Dictionary<long, int> known, Func<long, string, string?, IEnumerable<Node>> childrenOf, string dir, string path)
        {
            if (known.TryGetValue(Id(material), out var index))
                return index;
            var p = Properties(material);
            var color = Vec(p, "DiffuseColor", Vec(p, "Diffuse", new Vector3(0.78f, 0.78f, 0.78f)));
            SKBitmap? texture = null;
            var textures = childrenOf(Id(material), "Texture", "DiffuseColor").Concat(childrenOf(Id(material), "Texture", "Diffuse")).Concat(childrenOf(Id(material), "Texture", "Base Color"));
            if (textures.FirstOrDefault() is { } t)
            {
                texture = Texture(t, childrenOf, dir, path);
                if (texture is null)
                    mesh.Notes.Add("a texture couldn't be read");
                else
                    color = Vector3.One; // the picture is the colour
            }
            static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
            return known[Id(material)] = mesh.AddMaterial(new MeshMaterial { Color = new SKColor(B(color.X), B(color.Y), B(color.Z)), Texture = texture });
        }

        /// <summary>A texture's picture: embedded in the file (its Video's Content), or the file it names, beside the FBX or in its .fbm folder.</summary>
        private static SKBitmap? Texture(Node texture, Func<long, string, string?, IEnumerable<Node>> childrenOf, string dir, string path)
        {
            SKBitmap? bitmap = null;
            foreach (var video in childrenOf(Id(texture), "Video", null))
            {
                if (video.Child("Content") is not { } content)
                    continue;
                var bytes = content.Props.FirstOrDefault() as byte[];
                if (bytes is null && content.Props.OfType<string>().Any())
                    try { bytes = Convert.FromBase64String(string.Concat(content.Props.OfType<string>())); }
                    catch (FormatException) { }
                if (bytes is { Length: > 0 } && SKBitmap.Decode(bytes) is { } decoded)
                {
                    bitmap = decoded;
                    break;
                }
            }
            if (bitmap is null)
            {
                var names = new[] { texture.Value("RelativeFilename") as string, texture.Value("FileName") as string }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Replace('\\', Path.DirectorySeparatorChar));
                var tries = names.SelectMany(n => new[]
                {
                    Path.IsPathRooted(n) ? n : Path.Combine(dir, n),
                    Path.Combine(dir, Path.GetFileName(n)),
                    Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".fbm", Path.GetFileName(n)),
                });
                foreach (var file in tries.Distinct())
                    try
                    {
                        if (File.Exists(file) && PictureFile.Decode(file) is { } decoded)
                        {
                            bitmap = decoded;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            // Big textures are scaled down: only colours are looked up, at preview sizes.
            if (bitmap is not null && Math.Max(bitmap.Width, bitmap.Height) > 1024)
            {
                var scale = 1024.0 / Math.Max(bitmap.Width, bitmap.Height);
                var smaller = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))), SKFilterQuality.Medium);
                bitmap.Dispose();
                bitmap = smaller;
            }
            return bitmap;
        }

        /// <summary>A layer of values a mesh has per corner, per vertex, per polygon or one for all (UVs, colours, materials), and how they are looked up.</summary>
        private sealed record Layer(string Mapping, bool Indexed, double[] Data, int[] Index)
        {
            public static Layer? Of(Node geometry, string element, string data, string index)
            {
                var e = geometry.Child(element);
                if (e is null)
                    return null;
                var mapping = e.Value("MappingInformationType") as string ?? "ByPolygonVertex";
                var reference = e.Value("ReferenceInformationType") as string ?? "Direct";
                return new Layer(mapping, reference is "IndexToDirect" or "Index", Doubles(e.Value(data)), Ints(e.Value(index)));
            }

            /// <summary>Where the values for a corner are: its place among all corners, its vertex, its polygon.</summary>
            public int At(int corner, int vertex, int polygon)
            {
                var i = Mapping switch
                {
                    "ByPolygonVertex" => corner,
                    "ByVertice" or "ByVertex" or "ByControlPoint" => vertex,
                    "ByPolygon" => polygon,
                    _ => 0, // AllSame
                };
                if (Indexed && Index.Length > 0)
                    i = i < Index.Length ? Index[i] : -1;
                return i;
            }
        }

        private static void AddGeometry(Mesh mesh, Node geometry, Node model, int node, List<int> materials,
            Func<long, string, string?, IEnumerable<Node>> childrenOf, Dictionary<long, int> nodeOf)
        {
            var vertices = Doubles(geometry.Value("Vertices"));
            var polygons = Ints(geometry.Value("PolygonVertexIndex"));
            if (vertices.Length < 3 || polygons.Length < 3)
                return;
            var p = Properties(model);
            // The mesh's own transform within its node, which its children don't take.
            var geometric = Matrix4x4.CreateScale(Vec(p, "GeometricScaling", Vector3.One)) * MeshNode.Euler(Vec(p, "GeometricRotation", Vector3.Zero), 0)
                            * Matrix4x4.CreateTranslation(Vec(p, "GeometricTranslation", Vector3.Zero));
            var uvs = Layer.Of(geometry, "LayerElementUV", "UV", "UVIndex");
            var colors = Layer.Of(geometry, "LayerElementColor", "Colors", "ColorIndex");
            var mats = Layer.Of(geometry, "LayerElementMaterial", "Materials", "");
            var fallback = materials.Count == 0 ? mesh.AddMaterial(new MeshMaterial()) : materials[0];

            // Each vertex's bones, from the skin's clusters.
            var count = vertices.Length / 3;
            List<(int Bone, float Weight)>[]? weights = null;
            foreach (var skin in childrenOf(Id(geometry), "Deformer", null).Where(d => ClassOf(d) == "Skin"))
                foreach (var cluster in childrenOf(Id(skin), "Deformer", null).Where(d => ClassOf(d) == "Cluster"))
                {
                    if (childrenOf(Id(cluster), "Model", null).FirstOrDefault() is not { } bone || !nodeOf.TryGetValue(Id(bone), out var boneNode))
                        continue;
                    var indexes = Ints(cluster.Value("Indexes"));
                    var w = Doubles(cluster.Value("Weights"));
                    if (indexes.Length == 0)
                        continue;
                    // The file keeps Transform in the bone's space (the FBX SDK hands it out as the mesh's
                    // world, TransformLink times it): from the mesh into the bone at the bind pose, as is.
                    var boneIndex = mesh.Bones.Count;
                    mesh.Bones.Add(new MeshBone(boneNode, Matrix(cluster.Value("Transform"))));
                    weights ??= new List<(int, float)>[count];
                    for (var i = 0; i < indexes.Length; i++)
                        if (indexes[i] >= 0 && indexes[i] < count && (i < w.Length ? w[i] : 0) is var weight and > 0)
                            (weights[indexes[i]] ??= []).Add((boneIndex, (float)weight));
                }

            var corner = 0;
            var polygon = 0;
            var corners = new List<int>();
            for (var i = 0; i < polygons.Length; i++)
            {
                var raw = polygons[i];
                var last = raw < 0;
                var v = last ? ~raw : raw;
                if (v < count)
                {
                    var position = new Vector3((float)vertices[v * 3], (float)vertices[v * 3 + 1], (float)vertices[v * 3 + 2]);
                    var uv = Vector2.Zero;
                    if (uvs is not null && uvs.At(corner, v, polygon) is var u and >= 0 && u * 2 + 1 < uvs.Data.Length)
                        uv = new Vector2((float)uvs.Data[u * 2], 1 - (float)uvs.Data[u * 2 + 1]); // FBX's v goes up the picture
                    SKColor? color = null;
                    if (colors is not null && colors.At(corner, v, polygon) is var c and >= 0 && c * 4 + 3 < colors.Data.Length)
                        color = new SKColor((byte)Math.Clamp(colors.Data[c * 4] * 255, 0, 255), (byte)Math.Clamp(colors.Data[c * 4 + 1] * 255, 0, 255), (byte)Math.Clamp(colors.Data[c * 4 + 2] * 255, 0, 255));
                    corners.Add(mesh.AddVertex(Vector3.Zero, uv, color));
                    mesh.SourcePositions.Add(Vector3.Transform(position, geometric));
                    mesh.VertexNodes.Add(node);
                    mesh.VertexBones.Add(weights?[v]?.ToArray());
                }
                corner++;
                if (!last)
                    continue;
                var material = fallback;
                if (mats is not null && mats.At(corner - 1, v, polygon) is var m and >= 0 && m < mats.Data.Length && (int)mats.Data[m] is var slot && slot >= 0 && slot < materials.Count)
                    material = materials[slot];
                for (var k = 1; k + 1 < corners.Count; k++)
                    mesh.AddTriangle(corners[0], corners[k], corners[k + 1], material);
                corners.Clear();
                polygon++;
            }
        }

        /// <summary>
        /// Each take (AnimationStack) with its first layer's curves: a curve node tied to a node's
        /// "Lcl Translation", "Lcl Rotation" or "Lcl Scaling", its curves to its "d|X", "d|Y" and "d|Z".
        /// </summary>
        private static void ReadAnimations(Mesh mesh, IEnumerable<Node> objects, Func<long, string, string?, IEnumerable<Node>> childrenOf, Dictionary<long, int> nodeOf, double fps)
        {
            var curveNodeTarget = new Dictionary<long, (int Node, string Property)>();
            foreach (var model in objects.Where(o => o.Name == "Model"))
                foreach (var channel in new[] { "Lcl Translation", "Lcl Rotation", "Lcl Scaling" })
                    foreach (var curveNode in childrenOf(Id(model), "AnimationCurveNode", channel))
                        curveNodeTarget[Id(curveNode)] = (nodeOf[Id(model)], channel);
            foreach (var stack in objects.Where(o => o.Name == "AnimationStack"))
            {
                var layer = childrenOf(Id(stack), "AnimationLayer", null).FirstOrDefault();
                if (layer is null)
                    continue;
                var p = Properties(stack);
                var start = Scalar(p, "LocalStart", Scalar(p, "ReferenceStart", 0)) / TicksPerSecond;
                var stop = Scalar(p, "LocalStop", Scalar(p, "ReferenceStop", 0)) / TicksPerSecond;
                var byNode = new Dictionary<int, NodeCurves>();
                double first = double.MaxValue, last = double.MinValue;
                foreach (var curveNode in childrenOf(Id(layer), "AnimationCurveNode", null))
                {
                    if (!curveNodeTarget.TryGetValue(Id(curveNode), out var target))
                        continue;
                    if (!byNode.TryGetValue(target.Node, out var curves))
                        byNode[target.Node] = curves = new NodeCurves();
                    var slots = target.Property switch { "Lcl Translation" => curves.Translation, "Lcl Rotation" => curves.Rotation, _ => curves.Scale };
                    foreach (var (axis, i) in new[] { ("d|X", 0), ("d|Y", 1), ("d|Z", 2) })
                        if (childrenOf(Id(curveNode), "AnimationCurve", axis).FirstOrDefault() is { } curve && Curve(curve) is { } read)
                        {
                            slots[i] = read;
                            first = Math.Min(first, read.Times[0]);
                            last = Math.Max(last, read.Times[^1]);
                        }
                }
                if (first > last)
                    continue;
                if (stop <= start)
                    (start, stop) = (first, last);
                var animation = new MeshAnimation { Name = NameOf(stack) is { Length: > 0 } name ? name : "Take", Duration = Math.Max(0, stop - start), FrameRate = fps, Start = start };
                foreach (var (node, curves) in byNode)
                    animation.Curves[node] = curves;
                mesh.Animations.Add(animation);
            }
        }

        private static MeshCurve? Curve(Node curve)
        {
            var times = Doubles(curve.Value("KeyTime")).Select(t => t / TicksPerSecond).ToArray();
            var values = Doubles(curve.Value("KeyValueFloat")).Select(v => (float)v).ToArray();
            if (times.Length == 0 || values.Length == 0)
                return null;
            // Interpolation, a flag for each run of keys (KeyAttrRefCount): constant holds a key's value to the next.
            var flags = Ints(curve.Value("KeyAttrFlags"));
            var runs = Ints(curve.Value("KeyAttrRefCount"));
            bool[]? constant = null;
            if (flags.Length > 0)
            {
                constant = new bool[times.Length];
                var key = 0;
                for (var f = 0; f < flags.Length && key < times.Length; f++)
                    for (var r = 0; r < (f < runs.Length ? Math.Max(1, runs[f]) : times.Length) && key < times.Length; r++)
                        constant[key++] = (flags[f] & 0x2) != 0;
            }
            return new MeshCurve(times, values, constant);
        }
    }
}
