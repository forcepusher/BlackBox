using System.IO.Compression;
using System.Numerics;
using System.Text;
using BlackBox.Core.Media;
using Xunit;

namespace BlackBox.Core.Tests;

// FBX files, read by BlackBox itself: text and binary (7.4 and 7.5's 64-bit records), axes, skins and animations.
public class FbxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-fbx-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const long Second = 46_186_158_000;

    /// <summary>A text FBX 7.4: a square 2 wide at x 10 in a Z-up scene, sliding 100 along x over its one second.</summary>
    private const string TextFbx = """
        ; FBX 7.4.0 project file
        FBXHeaderExtension:  {
            FBXHeaderVersion: 1003
            FBXVersion: 7400
        }
        GlobalSettings:  {
            Version: 1000
            Properties70:  {
                P: "UpAxis", "int", "Integer", "",2
                P: "UpAxisSign", "int", "Integer", "",1
                P: "FrontAxis", "int", "Integer", "",1
                P: "FrontAxisSign", "int", "Integer", "",-1
                P: "CoordAxis", "int", "Integer", "",0
                P: "CoordAxisSign", "int", "Integer", "",1
                P: "TimeMode", "enum", "", "",6
            }
        }
        Objects:  {
            Geometry: 100, "Geometry::Square", "Mesh" {
                Vertices: *12 {
                    a: -1,-1,0,1,-1,0,
                    1,1,0,-1,1,0
                }
                PolygonVertexIndex: *4 {
                    a: 0,1,2,-4
                }
            }
            Model: 200, "Model::Square", "Mesh" {
                Properties70:  {
                    P: "Lcl Translation", "Lcl Translation", "", "A+",10,0,0
                }
            }
            Material: 300, "Material::Red", "" {
                Properties70:  {
                    P: "DiffuseColor", "Color", "", "A",1,0,0
                }
            }
            AnimationStack: 400, "AnimStack::Slide", "" {
                Properties70:  {
                    P: "LocalStop", "KTime", "Time", "",46186158000
                }
            }
            AnimationLayer: 500, "AnimLayer::Base", "" {
            }
            AnimationCurveNode: 600, "AnimCurveNode::T", "" {
            }
            AnimationCurve: 700, "AnimCurve::", "" {
                KeyTime: *2 {
                    a: 0,46186158000
                }
                KeyValueFloat: *2 {
                    a: 10,110
                }
            }
        }
        Connections:  {
            ;Model::Square, Model::RootNode
            C: "OO",200,0
            C: "OO",100,200
            C: "OO",300,200
            C: "OO",500,400
            C: "OO",600,500
            C: "OP",600,200, "Lcl Translation"
            C: "OP",700,600, "d|X"
        }
        """;

    [Fact]
    public void A_text_fbx_is_read_turned_y_up_and_its_take_slides_it()
    {
        var path = Path.Combine(_dir, "square.fbx");
        File.WriteAllText(path, TextFbx);
        using var mesh = Mesh.Load(path);
        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(new SkiaSharp.SKColor(255, 0, 0), mesh.Materials[mesh.TriangleMaterials[0]].Color);
        // Z up in the file: its y (front -y) becomes -z here, its z up.
        var (min, max) = mesh.Bounds();
        Assert.Equal(new Vector3(9, 0, -1), min);
        Assert.Equal(new Vector3(11, 0, 1), max);
        var take = Assert.Single(mesh.Animations);
        Assert.Equal("Slide", take.Name);
        Assert.Equal(1, take.Duration, 6);
        Assert.Equal(30, take.FrameRate);
        Assert.Equal(31, take.FrameCount);
        Assert.Contains("1 animation (Slide 1.0 s)", mesh.Describe());
        var half = mesh.Pose(take, 0.5);
        Assert.Equal(59, half.Min(p => p.X), 3);
        Assert.Equal(61, half.Max(p => p.X), 3);
        Assert.Equal(111, mesh.Pose(take, 1).Max(p => p.X), 3);
        // Framed through the whole take.
        Assert.Equal(111, mesh.Bounds(take).Max.X, 3);
    }

    [Theory]
    [InlineData(7400)]
    [InlineData(7500)]
    public void A_binary_fbx_with_a_skin_bends_with_its_bone(int version)
    {
        // A strip from y 0 to 2 on two bones: the lower one still, the upper one, at y 1, turning 90° about z in a second.
        var path = Path.Combine(_dir, $"strip{version}.fbx");
        File.WriteAllBytes(path, Binary(version,
            N("FBXHeaderExtension", [], N("FBXVersion", [version])),
            N("Objects", [],
                N("Geometry", [100L, "Strip\0\u0001Geometry", "Mesh"],
                    N("Vertices", [new double[] { 0, 0, 0, 0.1, 0, 0, 0.1, 2, 0, 0, 2, 0 }]),
                    N("PolygonVertexIndex", [new[] { 0, 1, 2, -4 }])),
                N("Model", [200L, "Strip\0\u0001Model", "Mesh"]),
                N("Model", [210L, "Lower\0\u0001Model", "LimbNode"]),
                N("Model", [220L, "Upper\0\u0001Model", "LimbNode"], Props(("Lcl Translation", [0.0, 1.0, 0.0]))),
                N("Deformer", [300L, "Skin\0\u0001Deformer", "Skin"]),
                Cluster(310, [0, 1], Matrix4x4.Identity),
                // The upper bone's cluster: the mesh in its space, one down.
                Cluster(320, [2, 3], Matrix4x4.CreateTranslation(0, -1, 0)),
                N("AnimationStack", [400L, "Bend\0\u0001AnimStack", ""], Props(("LocalStop", [Second]))),
                N("AnimationLayer", [500L, "Base\0\u0001AnimLayer", ""]),
                N("AnimationCurveNode", [600L, "R\0\u0001AnimCurveNode", ""]),
                N("AnimationCurve", [700L, "\0\u0001AnimCurve", ""], N("KeyTime", [new long[] { 0, Second }]), N("KeyValueFloat", [new float[] { 0, 90 }]))),
            N("Connections", [],
                C("OO", 200, 0), C("OO", 210, 0), C("OO", 220, 210), C("OO", 100, 200), C("OO", 300, 100),
                C("OO", 310, 300), C("OO", 320, 300), C("OO", 210, 310), C("OO", 220, 320),
                C("OO", 500, 400), C("OO", 600, 500), C("OP", 600, 220, "Lcl Rotation"), C("OP", 700, 600, "d|Z"))));
        using var mesh = Mesh.Load(path);
        Assert.Equal(2, mesh.TriangleCount);
        var take = Assert.Single(mesh.Animations);
        Assert.Equal("Bend", take.Name);
        Assert.Contains("2 bones", mesh.Describe());
        // At the end the top corners, 1 above the upper bone, are 1 along -x from it: (-1, 1) and (-1, 1.1).
        var bent = mesh.Pose(take, 1);
        var top = bent.Where(p => p.X < -0.5f).ToList();
        Assert.Equal(2, top.Count);
        Assert.All(top, p => Assert.Equal(-1, p.X, 3));
        Assert.Equal([1f, 1.1f], top.Select(p => MathF.Round(p.Y, 3)).Order());
        // The lower corners keep still.
        Assert.Contains(bent, p => p.Length() < 1e-4);
    }

    [Fact]
    public void Old_fbx_files_say_how_to_get_one_that_reads()
    {
        var path = Path.Combine(_dir, "old.fbx");
        File.WriteAllText(path, "FBXHeaderExtension:  {\n FBXVersion: 6100\n}\nObjects:  {\n}\n");
        var ex = Assert.Throws<NotSupportedException>(() => Mesh.Load(path));
        Assert.Contains("FBX 6.1", ex.Message);
    }

    [Fact]
    public void Rotations_follow_fbxs_order_and_pre_rotation()
    {
        // x first, then z (order 1, XZY): +y turned 90° about x goes to +z, which 90° about z leaves.
        var node = new MeshNode { Order = 1 };
        var turned = Vector3.Transform(Vector3.UnitY, node.Local(Vector3.Zero, new Vector3(90, 0, 90), Vector3.One));
        Assert.Equal(1, turned.Z, 4);
        // A pre-rotation comes after the node's own rotation: 90° about z, then about x.
        var pre = new MeshNode { PreRotation = new Vector3(90, 0, 0) };
        var p = Vector3.Transform(Vector3.UnitX, pre.Local(Vector3.Zero, new Vector3(0, 0, 90), Vector3.One));
        Assert.Equal(1, p.Z, 4); // x → y (about z) → z (about x)
    }

    // ---- a small binary FBX writer -----------------------------------------------------------------

    private sealed record FbxNode(string Name, object[] Props, FbxNode[] Children);

    private static FbxNode N(string name, object[] props, params FbxNode[] children) => new(name, props, children);

    private static FbxNode C(string kind, long child, long parent, string? property = null) =>
        N("C", property is null ? [kind, child, parent] : [kind, child, parent, property]);

    private static FbxNode Props(params (string Name, object[] Values)[] props) =>
        N("Properties70", [], props.Select(p => N("P", new object[] { p.Name, "", "", "A" }.Concat(p.Values).ToArray())).ToArray());

    private static FbxNode Cluster(long id, int[] vertices, Matrix4x4 transform) =>
        N("Deformer", [id, "\0\u0001SubDeformer", "Cluster"],
            N("Indexes", [vertices]), N("Weights", [vertices.Select(_ => 1.0).ToArray()]),
            N("Transform", [Array(transform)]), N("TransformLink", [Array(Matrix4x4.Identity)]));

    private static double[] Array(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static byte[] Binary(int version, params FbxNode[] nodes)
    {
        var wide = version >= 7500;
        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write("Kaydara FBX Binary  \0"u8);
        w.Write((byte)0x1A);
        w.Write((byte)0);
        w.Write((uint)version);
        foreach (var node in nodes)
            Write(w, node, wide);
        w.Write(new byte[wide ? 25 : 13]);
        w.Flush();
        return stream.ToArray();
    }

    private static void Write(BinaryWriter w, FbxNode node, bool wide)
    {
        var start = w.BaseStream.Position;
        w.Write(new byte[wide ? 24 : 12]); // filled in below
        w.Write((byte)node.Name.Length);
        w.Write(Encoding.ASCII.GetBytes(node.Name));
        var propsStart = w.BaseStream.Position;
        foreach (var p in node.Props)
            Prop(w, p);
        var propsLength = w.BaseStream.Position - propsStart;
        foreach (var child in node.Children)
            Write(w, child, wide);
        if (node.Children.Length > 0)
            w.Write(new byte[wide ? 25 : 13]);
        var end = w.BaseStream.Position;
        w.BaseStream.Position = start;
        if (wide)
        {
            w.Write((ulong)end);
            w.Write((ulong)node.Props.Length);
            w.Write((ulong)propsLength);
        }
        else
        {
            w.Write((uint)end);
            w.Write((uint)node.Props.Length);
            w.Write((uint)propsLength);
        }
        w.BaseStream.Position = end;
    }

    private static void Prop(BinaryWriter w, object p)
    {
        switch (p)
        {
            case int i: w.Write((byte)'I'); w.Write(i); break;
            case long l: w.Write((byte)'L'); w.Write(l); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case string s:
                var bytes = Encoding.UTF8.GetBytes(s);
                w.Write((byte)'S');
                w.Write(bytes.Length);
                w.Write(bytes);
                break;
            case double[] a: Array(w, 'd', a.SelectMany(BitConverter.GetBytes).ToArray(), a.Length, compress: true); break;
            case float[] a: Array(w, 'f', a.SelectMany(BitConverter.GetBytes).ToArray(), a.Length, compress: false); break;
            case long[] a: Array(w, 'l', a.SelectMany(BitConverter.GetBytes).ToArray(), a.Length, compress: true); break;
            case int[] a: Array(w, 'i', a.SelectMany(BitConverter.GetBytes).ToArray(), a.Length, compress: false); break;
            default: throw new ArgumentException(p.GetType().Name);
        }
    }

    private static void Array(BinaryWriter w, char type, byte[] data, int count, bool compress)
    {
        if (compress)
        {
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(data);
            data = packed.ToArray();
        }
        w.Write((byte)type);
        w.Write(count);
        w.Write(compress ? 1 : 0);
        w.Write(data.Length);
        w.Write(data);
    }
}
