using System.Globalization;
using System.Numerics;

namespace BlackBox.Core.Media;

/// <summary>
/// An animation a 3D model carries (an FBX file's take): how its nodes (bones, and objects that move
/// on their own) turn, move and scale over time. <see cref="Mesh.Pose"/> gives the model's vertices at
/// a time of it.
/// </summary>
public sealed class MeshAnimation
{
    public required string Name { get; init; }

    /// <summary>How long it lasts, in seconds.</summary>
    public required double Duration { get; init; }

    /// <summary>The frames a second it was made at (the file's), for stepping a frame at a time.</summary>
    public double FrameRate { get; init; } = 30;

    /// <summary>Where it starts, in the file's seconds: its keys are told from there.</summary>
    internal double Start { get; init; }

    /// <summary>The curves of each node it moves, by the node's index in <see cref="Mesh.Nodes"/>.</summary>
    internal Dictionary<int, NodeCurves> Curves { get; } = [];

    public int FrameCount => Math.Max(1, (int)Math.Round(Duration * FrameRate) + 1);
}

/// <summary>A node's curves: translation, rotation (degrees) and scale, each x, y and z; null where it doesn't move.</summary>
internal sealed class NodeCurves
{
    public readonly MeshCurve?[] Translation = new MeshCurve?[3];
    public readonly MeshCurve?[] Rotation = new MeshCurve?[3];
    public readonly MeshCurve?[] Scale = new MeshCurve?[3];
}

/// <summary>Keys of one value over time, in seconds; linear between them, held before the first and after the last.</summary>
internal sealed class MeshCurve(double[] times, float[] values, bool[]? constant = null)
{
    public double[] Times { get; } = times;
    public float[] Values { get; } = values;

    public float At(double t)
    {
        var n = Math.Min(Times.Length, Values.Length);
        if (n == 0)
            return 0;
        if (t <= Times[0])
            return Values[0];
        if (t >= Times[n - 1])
            return Values[n - 1];
        var i = Array.BinarySearch(Times, 0, n, t);
        if (i >= 0)
            return Values[i];
        i = ~i; // the first key after t
        var (t0, t1) = (Times[i - 1], Times[i]);
        if (constant is not null && constant[i - 1] || t1 <= t0)
            return Values[i - 1];
        return (float)(Values[i - 1] + (Values[i] - Values[i - 1]) * (t - t0) / (t1 - t0));
    }
}

/// <summary>
/// A node of a model's tree (an FBX Model: a mesh, a bone, an empty) and how its own transform is
/// made, as FBX makes one: T · Roff · Rp · Rpre · R · Rpost⁻¹ · Rp⁻¹ · Soff · Sp · S · Sp⁻¹, its rotation
/// in degrees about x, y and z in <see cref="Order"/>.
/// </summary>
internal sealed class MeshNode
{
    public string Name = "";
    public int Parent = -1;
    public Vector3 Translation, Rotation, Scale = Vector3.One;
    public Vector3 PreRotation, PostRotation, RotationOffset, RotationPivot, ScalingOffset, ScalingPivot;

    /// <summary>FBX's rotation order: 0 XYZ (x first), 1 XZY, 2 YZX, 3 YXZ, 4 ZXY, 5 ZYX.</summary>
    public int Order;

    /// <summary>The node's own transform with <paramref name="t"/>, <paramref name="r"/> and <paramref name="s"/> in place of its own; for row vectors (v · M), as System.Numerics has them.</summary>
    public Matrix4x4 Local(Vector3 t, Vector3 r, Vector3 s)
    {
        var m = Matrix4x4.CreateTranslation(-ScalingPivot) * Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(ScalingPivot)
                * Matrix4x4.CreateTranslation(ScalingOffset) * Matrix4x4.CreateTranslation(-RotationPivot);
        if (PostRotation != Vector3.Zero)
            m *= Matrix4x4.Transpose(Euler(PostRotation, 0)); // its inverse: a rotation's is its transpose
        m *= Euler(r, Order);
        if (PreRotation != Vector3.Zero)
            m *= Euler(PreRotation, 0);
        return m * Matrix4x4.CreateTranslation(RotationPivot) * Matrix4x4.CreateTranslation(RotationOffset) * Matrix4x4.CreateTranslation(t);
    }

    public Matrix4x4 Rest => Local(Translation, Rotation, Scale);

    /// <summary>A rotation of <paramref name="degrees"/> about x, y and z, applied in FBX's <paramref name="order"/>.</summary>
    public static Matrix4x4 Euler(Vector3 degrees, int order)
    {
        var x = Matrix4x4.CreateRotationX(degrees.X * MathF.PI / 180);
        var y = Matrix4x4.CreateRotationY(degrees.Y * MathF.PI / 180);
        var z = Matrix4x4.CreateRotationZ(degrees.Z * MathF.PI / 180);
        // For row vectors the first rotation applied comes first.
        return order switch
        {
            1 => x * z * y,
            2 => y * z * x,
            3 => y * x * z,
            4 => z * x * y,
            5 => z * y * x,
            _ => x * y * z,
        };
    }
}

/// <summary>A bone of a skin: the node it follows, and the matrix from the mesh's space into the bone's at the bind pose.</summary>
internal readonly record struct MeshBone(int Node, Matrix4x4 Bind);

public sealed partial class Mesh
{
    /// <summary>The model's tree of nodes, for a model that moves (an FBX file); empty otherwise.</summary>
    internal List<MeshNode> Nodes { get; } = [];

    /// <summary>The bones vertices are weighted to.</summary>
    internal List<MeshBone> Bones { get; } = [];

    /// <summary>
    /// Each vertex where its own node has it (with the node's geometric transform), the node, and for
    /// a skinned one its bones and weights; set for a model that moves, so <see cref="Pose"/> can move
    /// them. <see cref="Positions"/> holds them as they are at rest.
    /// </summary>
    internal List<Vector3> SourcePositions { get; } = [];

    internal List<int> VertexNodes { get; } = [];

    internal List<(int Bone, float Weight)[]?> VertexBones { get; } = [];

    /// <summary>Turns the whole model from the file's axes into BlackBox's: y up, the front toward +z.</summary>
    internal Matrix4x4 Axes { get; set; } = Matrix4x4.Identity;

    /// <summary>The animations the file has (its takes); empty for a model that keeps still.</summary>
    public List<MeshAnimation> Animations { get; } = [];

    public bool CanMove => Animations.Count > 0 && SourcePositions.Count == Positions.Count;

    /// <summary>
    /// The vertices at <paramref name="seconds"/> into <paramref name="animation"/> (looping over its
    /// length), each skinned vertex moved by its bones and each other by its node; <see cref="Positions"/>
    /// for a model that doesn't move.
    /// </summary>
    public Vector3[] Pose(MeshAnimation animation, double seconds)
    {
        if (!CanMove)
            return [.. Positions];
        var world = NodeWorlds(animation, animation.Start + Math.Clamp(seconds, 0, animation.Duration));
        var bones = Bones.Select(b => b.Bind * world[b.Node]).ToArray();
        var posed = new Vector3[Positions.Count];
        for (var i = 0; i < posed.Length; i++)
        {
            var source = SourcePositions[i];
            if (VertexBones[i] is { Length: > 0 } influences)
            {
                var sum = Vector3.Zero;
                var weights = 0f;
                foreach (var (bone, weight) in influences)
                {
                    sum += Vector3.Transform(source, bones[bone]) * weight;
                    weights += weight;
                }
                if (weights > 0)
                {
                    posed[i] = sum / weights;
                    continue;
                }
            }
            posed[i] = Vector3.Transform(source, world[VertexNodes[i]]);
        }
        return posed;
    }

    /// <summary>Every node's world transform (with the axes) at <paramref name="time"/> of <paramref name="animation"/>, in the file's seconds; at rest with none.</summary>
    internal Matrix4x4[] NodeWorlds(MeshAnimation? animation, double time)
    {
        var world = new Matrix4x4[Nodes.Count];
        var done = new bool[Nodes.Count];
        Matrix4x4 Of(int i)
        {
            if (done[i])
                return world[i];
            var node = Nodes[i];
            var local = node.Rest;
            if (animation is not null && animation.Curves.TryGetValue(i, out var curves))
            {
                static Vector3 Value(MeshCurve?[] c, Vector3 rest, double t) =>
                    new(c[0]?.At(t) ?? rest.X, c[1]?.At(t) ?? rest.Y, c[2]?.At(t) ?? rest.Z);
                local = node.Local(Value(curves.Translation, node.Translation, time), Value(curves.Rotation, node.Rotation, time), Value(curves.Scale, node.Scale, time));
            }
            world[i] = node.Parent >= 0 && node.Parent != i ? local * Of(node.Parent) : local * Axes;
            done[i] = true;
            return world[i];
        }
        for (var i = 0; i < world.Length; i++)
            Of(i);
        return world;
    }

    /// <summary>The box the model stays in through <paramref name="animation"/> (sampled at up to 60 times), so the view keeps it framed as it moves.</summary>
    public (Vector3 Min, Vector3 Max) Bounds(MeshAnimation animation)
    {
        var (min, max) = Bounds();
        if (!CanMove)
            return (min, max);
        var samples = Math.Clamp(animation.FrameCount, 2, 60);
        for (var s = 0; s < samples; s++)
            foreach (var p in Pose(animation, animation.Duration * s / (samples - 1)))
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        return (min, max);
    }

    /// <summary>"2 animations (Bend 1.0 s, Bounce 1.0 s), 2 bones", for <see cref="Describe"/>; null for a model that keeps still.</summary>
    private string? DescribeMotion()
    {
        if (Animations.Count == 0)
            return Bones.Count > 0 ? $"a skeleton of {Bones.Count:N0} bones" : null;
        var takes = string.Join(", ", Animations.Take(6).Select(a => $"{a.Name} {a.Duration.ToString("0.0#", CultureInfo.InvariantCulture)} s")) + (Animations.Count > 6 ? $" and {Animations.Count - 6} more" : "");
        var text = Animations.Count == 1 ? $"1 animation ({takes})" : $"{Animations.Count} animations ({takes})";
        return Bones.Count > 0 ? $"{text}, {Bones.Count:N0} bones" : text;
    }
}
