namespace Pipes.Rendering;

/// <summary>Interleaved position+normal meshes. Both are unit-sized and placed per instance in the vertex shader.</summary>
internal static class MeshBuilder
{
    /// <summary>Open cylinder of radius 1 along +Y from y=0 to y=1.</summary>
    public static (float[] Vertices, uint[] Indices) Cylinder(int segments)
    {
        var v = new List<float>();
        var idx = new List<uint>();
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
}
