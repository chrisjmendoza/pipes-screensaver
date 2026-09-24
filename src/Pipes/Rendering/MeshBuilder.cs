using System.Numerics;

namespace Pipes.Rendering;

/// <summary>
/// Builds the unit meshes that every piece is instanced from. Vertices are interleaved position + normal
/// (6 floats). The vertex shader stretches, rotates and places each instance, so there's one mesh per shape
/// no matter how many pipes are on screen.
/// </summary>
internal static class MeshBuilder
{
    /// <summary>Cylinder of radius 1 along +Y from y=0 to y=1, with flat caps on both ends.</summary>
    /// <remarks>
    /// Caps matter for fittings (a coupling sleeve is fatter than the pipe, so its ends are visible). On plain
    /// pipe segments they're hidden inside the neighbouring piece, and cost only a few triangles.
    /// </remarks>
    public static (float[] Vertices, uint[] Indices) Cylinder(int segments)
    {
        var v = new List<float>();
        var idx = new List<uint>();

        // Side wall: pairs of vertices (bottom, top) around the circle, normals pointing straight out.
        for (var i = 0; i <= segments; i++)
        {
            var a = MathF.Tau * i / segments;
            float x = MathF.Cos(a), z = MathF.Sin(a);
            v.AddRange([x, 0, z, x, 0, z]);
            v.AddRange([x, 1, z, x, 0, z]);
        }
        for (uint i = 0; i < segments; i++)
        {
            uint b = i * 2;
            idx.AddRange([b, b + 1, b + 2, b + 1, b + 3, b + 2]);
        }

        // Caps: a triangle fan per end. They need their own vertices because the normal is different (±Y).
        foreach (var (y, ny) in new[] { (0f, -1f), (1f, 1f) })
        {
            var centre = (uint)(v.Count / 6);
            v.AddRange([0, y, 0, 0, ny, 0]);
            for (var i = 0; i <= segments; i++)
            {
                var a = MathF.Tau * i / segments;
                v.AddRange([MathF.Cos(a), y, MathF.Sin(a), 0, ny, 0]);
            }
            for (uint i = 0; i < segments; i++)
                idx.AddRange([centre, centre + 1 + i, centre + 2 + i]);
        }
        return (v.ToArray(), idx.ToArray());
    }

    /// <summary>UV sphere of radius 1.</summary>
    public static (float[] Vertices, uint[] Indices) Sphere(int rings, int segments)
    {
        var v = new List<float>();
        var idx = new List<uint>();
        for (var r = 0; r <= rings; r++)
        {
            var phi = MathF.PI * r / rings;
            for (var s = 0; s <= segments; s++)
            {
                var theta = MathF.Tau * s / segments;
                float x = MathF.Sin(phi) * MathF.Cos(theta), y = MathF.Cos(phi), z = MathF.Sin(phi) * MathF.Sin(theta);
                v.AddRange([x, y, z, x, y, z]);
            }
        }
        var stride = (uint)segments + 1;
        for (uint r = 0; r < rings; r++)
        {
            for (uint s = 0; s < segments; s++)
            {
                uint a = r * stride + s, b = a + stride;
                idx.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }
        return (v.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// A torus section, stored as <em>parameters</em> rather than positions: each vertex is
    /// (t, cos φ, sin φ), where t in 0..1 is how far along the sweep it is and φ is the angle around the tube.
    /// The vertex shader turns those into a position using each instance's centre, bend radius, tube radius and
    /// sweep angle. That way one mesh serves every elbow, including the partly grown ones.
    /// </summary>
    public static (float[] Vertices, uint[] Indices) TorusSection(int sweepSegments, int tubeSegments)
    {
        var v = new List<float>();
        var idx = new List<uint>();
        for (var i = 0; i <= sweepSegments; i++)
        {
            var t = (float)i / sweepSegments;
            for (var j = 0; j <= tubeSegments; j++)
            {
                var phi = MathF.Tau * j / tubeSegments;
                v.AddRange([t, MathF.Cos(phi), MathF.Sin(phi), 0, 0, 0]); // normal slot unused
            }
        }
        var stride = (uint)tubeSegments + 1;
        for (uint i = 0; i < sweepSegments; i++)
        {
            for (uint j = 0; j < tubeSegments; j++)
            {
                uint a = i * stride + j, b = a + stride;
                idx.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }
        return (v.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// A teapot built from simple parts: a lathed body and lid (a 2D profile spun around the Y axis), plus a
    /// spout and a handle made by sweeping a circle along a curve. Not the real Utah teapot data, but it reads the
    /// same at pipe-joint size. The spout points along +X. The body is about 2.5 units wide and it is centred
    /// on the origin.
    /// </summary>
    public static (float[] Vertices, uint[] Indices) Teapot()
    {
        var v = new List<float>();
        var idx = new List<uint>();
        const float lift = -0.8f; // move the body's middle to y = 0

        // Profile as (radius, height), bottom centre up to the tip of the knob on the lid.
        Vector2[] profile =
        [
            new(0.00f, 0.00f), new(0.80f, 0.00f), new(1.00f, 0.08f), new(1.17f, 0.32f), new(1.25f, 0.60f),
            new(1.20f, 0.86f), new(1.06f, 1.06f), new(0.90f, 1.18f), new(0.84f, 1.22f), // body up to rim
            new(0.74f, 1.27f), new(0.45f, 1.36f), new(0.16f, 1.43f), // lid
            new(0.12f, 1.47f), new(0.19f, 1.54f), new(0.14f, 1.61f), new(0.00f, 1.63f), // knob
        ];
        Lathe(v, idx, Smooth(profile, 4), 36, lift);

        // Spout: a tapering tube curving up and out on the +X side, starting inside the body.
        Sweep(v, idx, t => CubicBezier(new(0.95f, 0.35f), new(1.60f, 0.30f), new(1.45f, 0.95f), new(1.90f, 1.18f), t),
            t => 0.30f - 0.19f * t, 20, 16, lift);

        // Handle: a loop on the -X side, both ends buried in the body.
        Sweep(v, idx, t => CubicBezier(new(-0.92f, 1.02f), new(-1.85f, 1.15f), new(-1.85f, 0.30f), new(-1.05f, 0.40f), t),
            _ => 0.09f, 24, 12, lift);

        return (v.ToArray(), idx.ToArray());
    }

    /// <summary>Spin a (radius, height) profile around Y. Normals come from the profile's slope.</summary>
    private static void Lathe(List<float> v, List<uint> idx, Vector2[] profile, int segments, float lift)
    {
        var baseIndex = (uint)(v.Count / 6);
        for (var i = 0; i < profile.Length; i++)
        {
            // Tangent along the profile; the outward normal is that tangent turned 90 degrees.
            var tangent = profile[Math.Min(i + 1, profile.Length - 1)] - profile[Math.Max(i - 1, 0)];
            var n2 = Vector2.Normalize(new Vector2(tangent.Y, -tangent.X));
            for (var s = 0; s <= segments; s++)
            {
                var a = MathF.Tau * s / segments;
                float c = MathF.Cos(a), sn = MathF.Sin(a);
                v.AddRange([profile[i].X * c, profile[i].Y + lift, profile[i].X * sn, n2.X * c, n2.Y, n2.X * sn]);
            }
        }
        AddGrid(idx, baseIndex, profile.Length - 1, segments);
    }

    /// <summary>Sweep a circle along a curve in the XY plane.</summary>
    private static void Sweep(List<float> v, List<uint> idx, Func<float, Vector2> path, Func<float, float> radius,
        int lengthSegments, int tubeSegments, float lift)
    {
        var baseIndex = (uint)(v.Count / 6);
        for (var i = 0; i <= lengthSegments; i++)
        {
            var t = (float)i / lengthSegments;
            var p = path(t);
            var tangent = Vector2.Normalize(path(Math.Min(t + 0.01f, 1f)) - path(Math.Max(t - 0.01f, 0f)));
            // The curve is flat, so the tube's cross-section is spanned by the in-plane normal and +Z.
            var inPlane = new Vector3(-tangent.Y, tangent.X, 0);
            for (var j = 0; j <= tubeSegments; j++)
            {
                var phi = MathF.Tau * j / tubeSegments;
                var n = inPlane * MathF.Cos(phi) + Vector3.UnitZ * MathF.Sin(phi);
                var pos = new Vector3(p.X, p.Y + lift, 0) + n * radius(t);
                v.AddRange([pos.X, pos.Y, pos.Z, n.X, n.Y, n.Z]);
            }
        }
        AddGrid(idx, baseIndex, lengthSegments, tubeSegments);
    }

    /// <summary>Triangles for a (rows+1) x (cols+1) grid of vertices.</summary>
    private static void AddGrid(List<uint> idx, uint baseIndex, int rows, int cols)
    {
        var stride = (uint)cols + 1;
        for (uint r = 0; r < rows; r++)
        {
            for (uint c = 0; c < cols; c++)
            {
                uint a = baseIndex + r * stride + c, b = a + stride;
                idx.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }
    }

    /// <summary>Catmull-Rom subdivision: a smooth curve through every control point.</summary>
    private static Vector2[] Smooth(Vector2[] points, int stepsPerSegment)
    {
        var result = new List<Vector2>();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Length - 1)];
            for (var s = 0; s < stepsPerSegment; s++)
            {
                var t = (float)s / stepsPerSegment;
                float t2 = t * t, t3 = t2 * t;
                result.Add(0.5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (3 * p1 - p0 - 3 * p2 + p3) * t3));
            }
        }
        result.Add(points[^1]);
        return [.. result];
    }

    private static Vector2 CubicBezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        var u = 1 - t;
        return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
    }
}
