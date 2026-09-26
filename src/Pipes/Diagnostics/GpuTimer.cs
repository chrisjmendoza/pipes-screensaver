using Silk.NET.OpenGL;

namespace Pipes.Diagnostics;

/// <summary>
/// Measures how long the GPU spends on each render pass, for the stats overlay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the CPU clock can't do this.</b> OpenGL calls return long before the GPU has done the work: the driver just
/// queues commands. Timing a pass with a Stopwatch measures how long it took to <em>queue</em> it, not to draw it.
/// (<c>/bench</c> gets real numbers by calling <c>glFinish</c>, which waits for the GPU to catch up every frame, but
/// that would wreck the frame rate we're trying to show.)
/// </para>
/// <para>
/// <b>Timestamp queries.</b> <c>glQueryCounter(GL_TIMESTAMP)</c> drops a marker into the GPU's command stream. When
/// the GPU reaches it, it writes down its own clock (in nanoseconds). A marker between each pass gives the time every
/// pass took on the GPU itself. The catch is that the answer isn't ready until the GPU gets there, a frame or two
/// after we queued it. Asking early would stall the CPU until the GPU catches up, so each frame's markers get their
/// own set of query objects, in a ring of <see cref="RingFrames"/> frames, and a frame's results are read only once
/// the GPU says they're available. The overlay therefore shows GPU times a few frames late, which nobody can see.
/// </para>
/// <para>
/// <b>Labels.</b> <see cref="Mark"/> ends the stretch since the previous marker and names it ("Shadows", "Scene"...).
/// A marker with no label (<see cref="Start"/>) ends a stretch that isn't GPU work we asked for: the gap while the CPU
/// was busy between views, say. Those gaps are left out of the total, so the total is the GPU's busy time.
/// </para>
/// </remarks>
internal sealed class GpuTimer : IDisposable
{
    /// <summary>
    /// Frames in flight: how many frames' markers can be waiting for the GPU at once. The driver usually runs at most
    /// two or three frames ahead; one more than that means we never have to wait for a result.
    /// </summary>
    private const int RingFrames = 5;

    /// <summary>
    /// Most markers per frame: a few per pass, times the number of views (one per monitor). A frame that tries to
    /// add more just stops recording (its later passes are lumped into the last stretch).
    /// </summary>
    private const int MaxMarks = 64;

    private readonly GL _gl;
    private readonly uint[][] _queries = new uint[RingFrames][];
    private readonly string?[][] _labels = new string?[RingFrames][];
    private readonly int[] _markCount = new int[RingFrames];
    private readonly long[] _frameIndex = new long[RingFrames];
    private readonly bool[] _pending = new bool[RingFrames];
    private int _slot = -1; // the ring slot the current frame records into; -1 outside BeginFrame/EndFrame
    private int _next;      // the slot the next frame will use

    public GpuTimer(GL gl)
    {
        _gl = gl;
        for (var i = 0; i < RingFrames; i++)
        {
            _queries[i] = new uint[MaxMarks];
            _labels[i] = new string?[MaxMarks];
            for (var j = 0; j < MaxMarks; j++) _queries[i][j] = _gl.GenQuery();
        }
    }

    /// <summary>
    /// Called with every finished frame's results, oldest first: the frame's index (as given to
    /// <see cref="BeginFrame"/>), its total GPU busy time in milliseconds, and the time per label. The breakdown list
    /// is reused for the next result, so copy anything you keep.
    /// </summary>
    public event Action<long, double, IReadOnlyList<(string Label, double Ms)>>? FrameMeasured;

    private readonly List<(string Label, double Ms)> _breakdown = [];

    /// <summary>Start recording a frame. Also collects any earlier frames the GPU has finished.</summary>
    public void BeginFrame(long frameIndex)
    {
        Collect();
        _slot = _next;
        _next = (_next + 1) % RingFrames;
        // If the GPU is so far behind that this slot's last frame still hasn't finished (it would take five queued
        // frames), drop that frame's results rather than wait for them.
        _pending[_slot] = false;
        _markCount[_slot] = 0;
        _frameIndex[_slot] = frameIndex;
        Start();
    }

    /// <summary>A marker that ends a stretch of time that shouldn't count (a gap, not a pass).</summary>
    public void Start() => Add(null);

    /// <summary>A marker that ends the pass named <paramref name="label"/>, which began at the previous marker.</summary>
    public void Mark(string label) => Add(label);

    private void Add(string? label)
    {
        if (_slot < 0) return; // not recording (the renderer calls Mark whether or not a frame is being timed)
        var n = _markCount[_slot];
        if (n >= MaxMarks) return;
        _gl.QueryCounter(_queries[_slot][n], QueryCounterTarget.Timestamp);
        _labels[_slot][n] = label;
        _markCount[_slot] = n + 1;
    }

    /// <summary>Stop recording the current frame. Its results arrive through <see cref="FrameMeasured"/> later.</summary>
    public void EndFrame()
    {
        if (_slot < 0) return;
        _pending[_slot] = _markCount[_slot] > 1;
        _slot = -1;
    }

    /// <summary>Read every finished frame, oldest first, without ever waiting for the GPU.</summary>
    private void Collect()
    {
        for (var k = 0; k < RingFrames; k++)
        {
            var slot = (_next + k) % RingFrames; // _next is the oldest slot
            if (!_pending[slot]) continue;
            var count = _markCount[slot];
            var queries = _queries[slot];
            // The markers finish in order, so once the last one is available, all of them are.
            _gl.GetQueryObject(queries[count - 1], QueryObjectParameterName.ResultAvailable, out int available);
            if (available == 0) break; // later frames can't be done either
            _pending[slot] = false;

            _breakdown.Clear();
            double total = 0;
            _gl.GetQueryObject(queries[0], QueryObjectParameterName.Result, out ulong previous);
            for (var i = 1; i < count; i++)
            {
                _gl.GetQueryObject(queries[i], QueryObjectParameterName.Result, out ulong now);
                var ms = (now - previous) / 1_000_000.0;
                previous = now;
                if (_labels[slot][i] is not { } label) continue; // a gap: not counted
                total += ms;
                // Several views (one per monitor) run the same passes: add them up under one label.
                var found = false;
                for (var j = 0; j < _breakdown.Count; j++)
                {
                    if (!ReferenceEquals(_breakdown[j].Label, label) && _breakdown[j].Label != label) continue;
                    _breakdown[j] = (label, _breakdown[j].Ms + ms);
                    found = true;
                    break;
                }
                if (!found) _breakdown.Add((label, ms));
            }
            FrameMeasured?.Invoke(_frameIndex[slot], total, _breakdown);
        }
    }

    public void Dispose()
    {
        foreach (var set in _queries)
            foreach (var q in set)
                _gl.DeleteQuery(q);
    }
}
