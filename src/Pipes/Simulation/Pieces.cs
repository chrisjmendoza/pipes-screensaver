using System.Numerics;

namespace Pipes.Simulation;

/// <summary>
/// The shapes the renderer knows how to draw. Each kind is one mesh, drawn with a single instanced draw call,
/// so a whole scene costs a handful of draw calls no matter how many pipes it has.
/// </summary>
public enum MeshKind
{
    /// <summary>Capped cylinder from <see cref="PipeInstance.Start"/> along <see cref="PipeInstance.Axis"/>.</summary>
    Cylinder,
    /// <summary>Sphere centred on <see cref="PipeInstance.Start"/>.</summary>
    Sphere,
    /// <summary>Part of a torus: pipe bends. Low resolution along the sweep because bends are only 90 degrees.</summary>
    Elbow,
    /// <summary>Same shader path as <see cref="Elbow"/> but a denser mesh, for full rings (valve wheels, collars).</summary>
    Ring,
    /// <summary>The teapot easter egg, placed with an orientation basis.</summary>
    Teapot,
}

/// <summary>Surface description of a pipe. Colour is linear RGB (not sRGB).</summary>
public readonly record struct PipeMaterial(Vector3 Color, float Metallic, float Roughness);

/// <summary>
/// One drawable piece. The meaning of the vectors depends on the <see cref="MeshKind"/> it's filed under:
/// <list type="bullet">
/// <item>Cylinder: runs from Start to Start + Axis. Radius is its thickness.</item>
/// <item>Sphere: centre Start, radius Radius.</item>
/// <item>Elbow/Ring: torus centre Start. Axis and Side are the two in-plane directions, both with length equal to
/// the bend radius. The tube starts at Start - Side and sweeps <see cref="Sweep"/> radians towards Start + Axis.</item>
/// <item>Teapot: position Start, "up" Axis, "forward" Side (both already scaled to the teapot's size).</item>
/// </list>
/// The layout matches the per-instance vertex attributes in <c>Shaders.PipeVertex</c>, so the renderer can copy
/// these straight into a GPU buffer.
/// </summary>
public struct PipeInstance
{
    public Vector3 Start;
    public Vector3 Axis;
    public Vector3 Side;
    public Vector3 Color;
    public float Radius;
    public float Sweep;
    public float Metallic;
    public float Roughness;
}

/// <summary>A bucket of pieces per <see cref="MeshKind"/>, plus helpers that build common shapes.</summary>
public sealed class PieceLists
{
    public const int KindCount = 5;

    private readonly List<PipeInstance>[] _lists =
        [.. Enumerable.Range(0, KindCount).Select(_ => new List<PipeInstance>())];

    public List<PipeInstance> this[MeshKind kind] => _lists[(int)kind];

    public void Clear()
    {
        foreach (var list in _lists) list.Clear();
    }

    /// <summary>Append a copy of everything in <paramref name="other"/>.</summary>
    public void AddFrom(PieceLists other)
    {
        for (var i = 0; i < KindCount; i++) _lists[i].AddRange(other._lists[i]);
    }

    public void Cylinder(Vector3 from, Vector3 to, float radius, PipeMaterial m) =>
        Add(MeshKind.Cylinder, from, to - from, Vector3.Zero, radius, 0f, m);

    public void Sphere(Vector3 centre, float radius, PipeMaterial m) =>
        Add(MeshKind.Sphere, centre, Vector3.Zero, Vector3.Zero, radius, 0f, m);

    /// <summary>
    /// A bend around <paramref name="centre"/> that starts heading along <paramref name="entryDir"/> and turns
    /// towards <paramref name="exitDir"/>. Both directions are unit vectors; <paramref name="bendRadius"/> is the
    /// distance from the centre to the middle of the tube.
    /// </summary>
    public void Elbow(Vector3 centre, Vector3 entryDir, Vector3 exitDir, float bendRadius, float tubeRadius, float sweep, PipeMaterial m) =>
        Add(MeshKind.Elbow, centre, entryDir * bendRadius, exitDir * bendRadius, tubeRadius, sweep, m);

    /// <summary>A full ring (torus) around <paramref name="axis"/>.</summary>
    public void Ring(Vector3 centre, Vector3 axis, float ringRadius, float tubeRadius, PipeMaterial m)
    {
        var (u, v) = Perpendiculars(axis);
        Add(MeshKind.Ring, centre, u * ringRadius, v * ringRadius, tubeRadius, MathF.Tau, m);
    }

    /// <summary>A mesh placed at <paramref name="position"/> with its +Y along <paramref name="up"/> and +X along <paramref name="forward"/>.</summary>
    public void Oriented(MeshKind kind, Vector3 position, Vector3 up, Vector3 forward, float scale, PipeMaterial m) =>
        Add(kind, position, up * scale, forward * scale, scale, 0f, m);

    private void Add(MeshKind kind, Vector3 start, Vector3 axis, Vector3 side, float radius, float sweep, PipeMaterial m) =>
        _lists[(int)kind].Add(new PipeInstance
        {
            Start = start, Axis = axis, Side = side, Radius = radius, Sweep = sweep,
            Color = m.Color, Metallic = m.Metallic, Roughness = m.Roughness,
        });

    /// <summary>Two unit vectors perpendicular to <paramref name="axis"/> and to each other.</summary>
    public static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var a = Vector3.Normalize(axis);
        // Cross with whichever world axis is least parallel, so the result never degenerates.
        var helper = MathF.Abs(a.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(helper, a));
        return (u, Vector3.Cross(a, u));
    }
}
