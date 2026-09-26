using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Pipes.Simulation;
using Silk.NET.OpenGL;

namespace Pipes.Rendering;

/// <summary>
/// The acceleration structure for traced reflections (the Traced reflections setting): a uniform grid over the
/// scene, listing which pieces touch each cell, so a reflection ray in the pipe shader can step through the scene
/// cell by cell and only test the few pieces in the cells it passes through. Rebuilt on the CPU every frame from the
/// same <see cref="PieceLists"/> the renderer draws, and handed to the shader as texture buffers.
/// See docs/RENDERING.md, "Traced reflections".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a uniform grid.</b> Ray tracers usually build a tree of boxes (a BVH) around the geometry. Here the pipes
/// already sit on the integer grid, a piece is at most about a cell long, and every piece changes every frame
/// anyway (growing pipes, and in flight the whole scene streams past), so a plain grid of unit cells is both the
/// natural fit and the cheapest thing to rebuild from scratch each frame.
/// </para>
/// <para>
/// <b>Cells are centred on integer coordinates:</b> cell (i, j, k) covers [i - 0.5, i + 0.5) on each axis. Pipes'
/// centre lines lie on integer coordinates, so a pipe runs down the middle of its cells with room either side: a
/// straight step (face to face through one cell) is listed in just that cell, and a ball joint in 1. With cells
/// starting at integers instead, every pipe would run along cell walls and straddle 4 cells, a joint 8.
/// </para>
/// <para>
/// <b>Layout</b>, built with a counting sort:
/// <list type="bullet">
/// <item><b>Cells</b> (<c>uCells</c>, one RG32UI texel per cell): (first entry, entry count), flattened as
/// <c>x + y*W + z*W*H</c>.</item>
/// <item><b>Entries</b> (<c>uEntries</c>, R32UI): <c>(kind &lt;&lt; 24) | index</c>, where kind is the
/// <see cref="MeshKind"/> and index is the piece's position in its kind's list, which is also its position in that
/// kind's instance buffer.</item>
/// <item><b>Pieces:</b> no separate upload. The renderer's per-kind instance buffers are viewed as RGBA32F texture
/// buffers (20 floats per piece = 5 texels), one texture per traced kind.</item>
/// </list>
/// </para>
/// <para>
/// Teapots aren't traced: they're rare, and a mesh, not something with a closed-form intersection.
/// </para>
/// </remarks>
internal sealed unsafe class ReflectionGrid : IDisposable
{
    /// <summary>
    /// Most cells along any axis. A box scene is ~30 cells across; the fly-through's pieces reach further than 64 as
    /// the path turns, and then only a 64-cell window around the shadow focus (a point ahead of the camera) is kept.
    /// Anything outside it is too far away (and too fogged) to matter in a reflection. 64³ cells is 2 MB of table.
    /// </summary>
    public const int MaxCells = 64;

    /// <summary>The kinds that are traced: cylinder, sphere, elbow, ring (the first four <see cref="MeshKind"/>s).</summary>
    public const int TracedKinds = 4;

    /// <summary>The first texture unit the grid uses. The pipe program already has AO on 0 and the shadow map on 1.</summary>
    public const int FirstUnit = 2;

    /// <summary>The shader's sampler for each traced kind's pieces, in <see cref="MeshKind"/> order.</summary>
    private static readonly string[] PieceSamplers = ["uPiecesCyl", "uPiecesSph", "uPiecesElb", "uPiecesRing"];

    private const int InitialCellCapacity = 32 * 1024;   // cells (8 bytes each)
    private const int InitialEntryCapacity = 64 * 1024;  // entries (4 bytes each)

    private readonly GL _gl;
    private readonly uint[] _instanceVbos;
    private readonly uint _cellBuffer, _cellTex, _entryBuffer, _entryTex;
    private readonly uint[] _pieceTex = new uint[TracedKinds];
    private int _cellCapacity, _entryCapacity;

    // CPU side, reused every frame (grown by doubling, never shrunk), so a warmed-up build allocates nothing.
    private uint[] _cells = [];           // 2 per cell: start, count
    private int[] _cursor = [];           // per cell: next free slot while filling
    private uint[] _entries = [];
    private int[] _ranges = [];           // 6 per piece: its cell range, min xyz, max xyz (inclusive, grid-relative)
    private Vector3[] _boxes = [];        // 2 per piece: world AABB min, max

    /// <summary>The integer coordinate of cell (0, 0, 0).</summary>
    public (int X, int Y, int Z) Origin { get; private set; }

    /// <summary>Cells along each axis. All zero when there's nothing to trace.</summary>
    public (int X, int Y, int Z) Size { get; private set; }

    /// <summary>
    /// Total CPU time spent in <see cref="Build"/> (including its upload calls), and how many builds, for /bench's
    /// report. Since the last <see cref="ResetStats"/>.
    /// </summary>
    public double BuildMilliseconds { get; private set; }

    /// <inheritdoc cref="BuildMilliseconds"/>
    public int Builds { get; private set; }

    /// <summary>
    /// Start the statistics afresh. /bench calls it after its warm-up, so the first builds (just-in-time compiling,
    /// growing the arrays) don't count.
    /// </summary>
    public void ResetStats()
    {
        BuildMilliseconds = 0;
        Builds = 0;
    }

    /// <param name="instanceVbos">The renderer's instance buffers, indexed by <see cref="MeshKind"/>.</param>
    public ReflectionGrid(GL gl, uint[] instanceVbos)
    {
        _gl = gl;
        _instanceVbos = instanceVbos;
        (_cellBuffer, _cellTex) = (_gl.GenBuffer(), _gl.GenTexture());
        (_entryBuffer, _entryTex) = (_gl.GenBuffer(), _gl.GenTexture());
        for (var k = 0; k < TracedKinds; k++) _pieceTex[k] = _gl.GenTexture();
        _cellCapacity = InitialCellCapacity;
        _entryCapacity = InitialEntryCapacity;
        Allocate(_cellBuffer, _cellTex, SizedInternalFormat.RG32ui, _cellCapacity * 2);
        Allocate(_entryBuffer, _entryTex, SizedInternalFormat.R32ui, _entryCapacity);
    }

    /// <summary>
    /// Rebuild the grid from this frame's pieces and upload it. <paramref name="focus"/> is where to centre the
    /// 64-cell window if the scene is bigger than that (<see cref="Camera.ShadowCentre"/>).
    /// </summary>
    public void Build(PieceLists pieces, Vector3 focus)
    {
        var started = Stopwatch.GetTimestamp();

        var total = 0;
        for (var k = 0; k < TracedKinds; k++) total += pieces[(MeshKind)k].Count;
        if (_ranges.Length < total * 6)
        {
            _ranges = new int[Math.Max(total * 6, _ranges.Length * 2)];
            _boxes = new Vector3[Math.Max(total * 2, _boxes.Length * 2)];
        }

        // 1. Every piece's bounding box, and the box around them all.
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var n = 0;
        for (var k = 0; k < TracedKinds; k++)
        {
            foreach (ref var p in CollectionsMarshal.AsSpan(pieces[(MeshKind)k]))
            {
                var (min, max) = Bounds((MeshKind)k, in p);
                _boxes[n * 2] = min;
                _boxes[n * 2 + 1] = max;
                lo = Vector3.Min(lo, min);
                hi = Vector3.Max(hi, max);
                n++;
            }
        }
        if (n == 0)
        {
            Size = (0, 0, 0);
            Finish(started, 0, 0);
            return;
        }

        // 2. The grid: the cells the scene covers, but at most MaxCells along each axis.
        var (ox, sx) = Window(lo.X, hi.X, focus.X);
        var (oy, sy) = Window(lo.Y, hi.Y, focus.Y);
        var (oz, sz) = Window(lo.Z, hi.Z, focus.Z);
        Origin = (ox, oy, oz);
        Size = (sx, sy, sz);
        var cellCount = sx * sy * sz;
        if (_cursor.Length < cellCount)
        {
            _cursor = new int[Math.Max(cellCount, _cursor.Length * 2)];
            _cells = new uint[_cursor.Length * 2];
        }
        var counts = _cursor.AsSpan(0, cellCount);
        counts.Clear();

        // 3. Counting sort, first half: which cells each piece's box overlaps (clipped to the grid), and how many
        //    pieces land in each cell.
        var entryCount = 0;
        for (var i = 0; i < n; i++)
        {
            var min = _boxes[i * 2];
            var max = _boxes[i * 2 + 1];
            // Cell i covers [i - 0.5, i + 0.5), so a coordinate x is in cell floor(x + 0.5).
            // The box is shrunk by a hair first: a pipe ending exactly on a cell wall (they all do) only touches the
            // next cell in a plane, and doesn't need listing there.
            var x0 = Math.Max(Cell(min.X + Hair) - ox, 0); var x1 = Math.Min(Cell(max.X - Hair) - ox, sx - 1);
            var y0 = Math.Max(Cell(min.Y + Hair) - oy, 0); var y1 = Math.Min(Cell(max.Y - Hair) - oy, sy - 1);
            var z0 = Math.Max(Cell(min.Z + Hair) - oz, 0); var z1 = Math.Min(Cell(max.Z - Hair) - oz, sz - 1);
            var r = _ranges.AsSpan(i * 6, 6);
            r[0] = x0; r[1] = y0; r[2] = z0; r[3] = x1; r[4] = y1; r[5] = z1;
            // Outside the window: x0 > x1 (or y, z), and the loops below never run.
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        counts[x + sx * (y + sy * z)]++;
                        entryCount++;
                    }
        }

        // Second half: a running total turns the counts into each cell's first slot. The same array then serves as
        // each cell's "next free slot" while the entries are filled in.
        var start = 0;
        for (var c = 0; c < cellCount; c++)
        {
            var count = counts[c];
            _cells[c * 2] = (uint)start;
            _cells[c * 2 + 1] = (uint)count;
            counts[c] = start;
            start += count;
        }

        if (_entries.Length < entryCount) _entries = new uint[Math.Max(entryCount, _entries.Length * 2)];
        n = 0;
        for (var k = 0; k < TracedKinds; k++)
        {
            var count = pieces[(MeshKind)k].Count;
            // An entry's top 8 bits hold the kind, leaving 24 for the index: 16.7 million pieces of one kind.
            Debug.Assert(count < 1 << 24, "Too many pieces of one kind for a grid entry's 24-bit index");
            for (var index = 0; index < count; index++, n++)
            {
                var entry = (uint)k << 24 | (uint)index;
                var r = _ranges.AsSpan(n * 6, 6);
                for (var z = r[2]; z <= r[5]; z++)
                    for (var y = r[1]; y <= r[4]; y++)
                        for (var x = r[0]; x <= r[3]; x++)
                            _entries[counts[x + sx * (y + sy * z)]++] = entry;
            }
        }

        Finish(started, cellCount, entryCount);
    }

    /// <summary>Upload, and note the time taken.</summary>
    private void Finish(long started, int cellCount, int entryCount)
    {
        // The same streaming pattern as PipeRenderer.UploadInstances: a fixed, generous size, "orphaned" each
        // frame (BufferData with no data) and then filled with BufferSubData.
        if (cellCount > _cellCapacity)
        {
            _cellCapacity = Math.Max(cellCount, _cellCapacity * 2);
            Allocate(_cellBuffer, _cellTex, SizedInternalFormat.RG32ui, _cellCapacity * 2);
        }
        if (entryCount > _entryCapacity)
        {
            _entryCapacity = Math.Max(entryCount, _entryCapacity * 2);
            Allocate(_entryBuffer, _entryTex, SizedInternalFormat.R32ui, _entryCapacity);
        }
        Stream(_cellBuffer, _cellCapacity * 2, _cells, cellCount * 2);
        Stream(_entryBuffer, _entryCapacity, _entries, entryCount);

        BuildMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Builds++;
    }

    /// <summary>
    /// Bind the grid for the pipe program: texture units 2..7 and the uniforms that describe the grid. The piece
    /// textures view the instance buffers, which the renderer has just refilled for this frame.
    /// </summary>
    public void Bind(uint program)
    {
        _gl.Uniform3(_gl.GetUniformLocation(program, "uGridOrigin"), Origin.X, Origin.Y, Origin.Z);
        _gl.Uniform3(_gl.GetUniformLocation(program, "uGridSize"), Size.X, Size.Y, Size.Z);
        BindUnit(program, "uCells", FirstUnit, _cellTex);
        BindUnit(program, "uEntries", FirstUnit + 1, _entryTex);
        for (var k = 0; k < TracedKinds; k++)
        {
            // TexBuffer refers to the buffer by name, so it follows the orphaning by itself. Attaching again every
            // frame costs nothing and covers a buffer that had no storage yet the first time (no pieces of that
            // kind so far).
            // (Bound to its own unit first: binding changes whichever unit is active.)
            BindUnit(program, PieceSamplers[k], FirstUnit + 2 + k, _pieceTex[k]);
            _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.Rgba32f, _instanceVbos[k]);
        }
    }

    private void BindUnit(uint program, string name, int unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.TextureBuffer, texture);
        _gl.Uniform1(_gl.GetUniformLocation(program, name), unit);
    }

    /// <summary>
    /// A piece's world-space bounding box. For a bend: the quarter arc lies in the quadrant between Start - Side
    /// (where it begins) and Start + Axis (where a full 90 degrees ends), both bend-radius vectors, so the box of those
    /// two points and the centre holds the whole tube's centre line, plus the tube radius. A partly grown bend
    /// sweeps less, and stays inside the same box.
    /// </summary>
    private static (Vector3 Min, Vector3 Max) Bounds(MeshKind kind, in PipeInstance p)
    {
        var r = new Vector3(p.Radius);
        switch (kind)
        {
            case MeshKind.Cylinder:
            {
                // The ends are flat discs, so along each world axis the cylinder only reaches past its end points
                // by the disc's extent on that axis: radius * sqrt(1 - a²), a being the axis direction's component.
                // A pipe running along x doesn't stick out along x at all, and a straight step (face to face through
                // one cell) stays in that one cell. Padding by the radius on every axis would put it in 3.
                var end = p.Start + p.Axis;
                var length = p.Axis.Length();
                var a = length > 1e-6f ? p.Axis / length : Vector3.Zero;
                var reach = Vector3.SquareRoot(Vector3.Max(Vector3.Zero, Vector3.One - a * a)) * p.Radius;
                return (Vector3.Min(p.Start, end) - reach, Vector3.Max(p.Start, end) + reach);
            }
            case MeshKind.Elbow:
            {
                var from = p.Start - p.Side;
                var to = p.Start + p.Axis;
                return (Vector3.Min(p.Start, Vector3.Min(from, to)) - r, Vector3.Max(p.Start, Vector3.Max(from, to)) + r);
            }
            case MeshKind.Ring:
            {
                var reach = new Vector3(p.Axis.Length() + p.Radius);
                return (p.Start - reach, p.Start + reach);
            }
            default: // sphere
                return (p.Start - r, p.Start + r);
        }
    }

    /// <summary>A little less than any real gap between pieces (world units). See where pieces are filed in <see cref="Build"/>.</summary>
    private const float Hair = 1e-3f;

    /// <summary>The cell a coordinate falls in: cells are centred on integers.</summary>
    private static int Cell(float x) => (int)MathF.Floor(x + 0.5f);

    /// <summary>
    /// One axis of the grid: the cells from <paramref name="lo"/> to <paramref name="hi"/>, or, if that's more than
    /// <see cref="MaxCells"/>, a window of that many centred on <paramref name="focus"/> (kept inside the scene).
    /// </summary>
    private static (int Origin, int Size) Window(float lo, float hi, float focus)
    {
        int first = Cell(lo), last = Cell(hi);
        if (last - first + 1 <= MaxCells) return (first, last - first + 1);
        var origin = Math.Clamp(Cell(focus) - MaxCells / 2, first, last - MaxCells + 1);
        return (origin, MaxCells);
    }

    /// <summary>Give a buffer room for <paramref name="values"/> 32-bit values and view it through a buffer texture.</summary>
    private void Allocate(uint buffer, uint texture, SizedInternalFormat format, int values)
    {
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, buffer);
        _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)(values * sizeof(uint)), null, BufferUsageARB.StreamDraw);
        _gl.BindTexture(TextureTarget.TextureBuffer, texture);
        _gl.TexBuffer(TextureTarget.TextureBuffer, format, buffer);
    }

    private void Stream(uint buffer, int capacityValues, uint[] data, int values)
    {
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, buffer);
        _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)(capacityValues * sizeof(uint)), null, BufferUsageARB.StreamDraw);
        if (values == 0) return;
        fixed (uint* p = data)
            _gl.BufferSubData(BufferTargetARB.TextureBuffer, 0, (nuint)(values * sizeof(uint)), p);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_cellBuffer);
        _gl.DeleteBuffer(_entryBuffer);
        _gl.DeleteTexture(_cellTex);
        _gl.DeleteTexture(_entryTex);
        foreach (var t in _pieceTex) _gl.DeleteTexture(t);
    }
}
