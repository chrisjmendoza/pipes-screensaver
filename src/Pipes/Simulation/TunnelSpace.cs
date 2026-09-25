using System.Numerics;

namespace Pipes.Simulation;

/// <summary>
/// Fly-through mode's space: the classic box, with a clear corridor bored through it for the camera, continuing as
/// an endless tunnel along a <see cref="FlightPath"/>.
/// </summary>
/// <remarks>
/// <code>
///  before take-off: pipes fill the box       in flight: pipes spawn in a shell around the path ahead
///  ┌─────────────────────┐
///  │ ░░░░░░░░░░░░░░░░░░░ │                     ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
///  │ ░░░░░░░░░░░░░░░░░░░ │           camera ─►   · · · corridor (kept clear) · · ·  ─► turns ahead
///  │ · · · corridor · · ·│─► path              ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
///  │ ░░░░░░░░░░░░░░░░░░░ │                     (shell: InnerRadius..OuterRadius from the path)
///  └─────────────────────┘
/// </code>
/// </remarks>
/// <param name="tunnelStartS">
/// Where the path leaves the box. The tunnel wall only exists beyond it, so no tunnel pipe ever grows between the
/// camera and the box it's looking at.
/// </param>
public sealed class TunnelSpace(FlightPath path, BoxSpace box, float tunnelStartS) : IPipeSpace
{
    /// <summary>Cells closer than this to the path stay empty, so the camera never flies through a pipe.</summary>
    public const float InnerRadius = 2.4f;

    /// <summary>How thick the tunnel's wall of pipes is.</summary>
    public const float OuterRadius = 6.5f;

    /// <summary>How deep the band of new pipes is, along the path.</summary>
    private const float SpawnBandDepth = 28f;

    /// <summary>
    /// While the box builds, the first stretch of tunnel beyond it builds too, as part of the same scene, so at
    /// take-off the tunnel is already there to fly into instead of starting from nothing.
    /// </summary>
    private const float PreBuildLength = 20f;

    /// <summary>
    /// The share of the scene's pipes that start in that first stretch of tunnel rather than in the box. (The scene
    /// gets correspondingly more pipes, so the box stays as full as usual.)
    /// </summary>
    public const float PreBuildShare = 0.3f;

    /// <summary>
    /// Where along the path the spawn band starts. Normally <see cref="SpawnAhead"/> in front of the camera, but at
    /// take-off it starts where the tunnel does and sweeps forward (see <see cref="AdvanceSpawnBand"/>).
    /// </summary>
    private float _bandStart = tunnelStartS;

    /// <summary>Where the path leaves the box and the tunnel begins (see the constructor).</summary>
    public float TunnelStartS { get; } = tunnelStartS;

    // The path never changes where it's already been built, so each cell's nearest-path answer can be remembered.
    private readonly Dictionary<Int3, (float Distance, float S, Vector3 Tangent, int Flow)> _nearest = [];

    /// <summary>
    /// False while the box is being filled (pipes stay in the box, which can fill up). True once the camera has taken
    /// off: pipes spawn ahead of it in the tunnel, endlessly.
    /// </summary>
    public bool Flying { get; set; }

    /// <summary>How far along the path the camera is. Spawning happens ahead of this.</summary>
    public float CameraS { get; set; }

    /// <summary>
    /// New pipes start at least this far ahead of the camera, and up to <see cref="SpawnBandDepth"/> beyond. Faster
    /// flights push it out, so pipes have time to grow before the camera reaches them.
    /// </summary>
    public float SpawnAhead { get; init; } = 14f;

    /// <summary>The far edge of where pipes spawn. The camera's fog and far plane are set to suit it.</summary>
    public float SpawnAheadMax => SpawnAhead + SpawnBandDepth;

    /// <summary>How fast the spawn band may sweep forward to catch up after take-off (units per second).</summary>
    public float CatchUpSpeed { get; init; } = 20f;

    public bool IsBounded => !Flying;

    public bool Contains(Int3 cell)
    {
        var near = Nearest(cell);
        if (near.Distance < InnerRadius) return false;             // the flight corridor
        if (box.Contains(cell)) return true;                        // the original scene
        if (near.Distance > OuterRadius || near.S < TunnelStartS) return false; // not the tunnel wall
        return Flying
            ? near.S >= CameraS - 4f                                // in flight: anywhere not behind the camera
            : near.S <= TunnelStartS + PreBuildLength;              // before: just its first stretch
    }

    public Int3 SpawnCandidate(Random rng)
    {
        if (!Flying)
        {
            return rng.NextSingle() < PreBuildShare
                ? RingCandidate(TunnelStartS + rng.NextSingle() * PreBuildLength, rng)
                : box.SpawnCandidate(rng);
        }
        return RingCandidate(_bandStart + rng.NextSingle() * SpawnBandDepth, rng);
    }

    /// <summary>
    /// Move the spawn band along with the camera, once per frame in flight.
    /// </summary>
    /// <remarks>
    /// The band belongs <see cref="SpawnAhead"/> in front of the camera, so pipes have time to grow before it arrives.
    /// But at take-off the camera is still in front of the box, and at high speed "SpawnAhead in front" is far past
    /// the end of the box: jumping straight there would leave a stretch of tunnel that never gets any pipes (a gap
    /// you could see). So the band starts where the tunnel does and sweeps forward at <see cref="CatchUpSpeed"/>
    /// (faster than the camera) until it's where it belongs. Every stretch of the tunnel gets its share of pipes.
    /// </remarks>
    public void AdvanceSpawnBand(float dt) =>
        _bandStart = Math.Max(TunnelStartS, Math.Min(CameraS + SpawnAhead, _bandStart + CatchUpSpeed * dt));

    /// <summary>A random cell in the ring of tunnel wall around the path at distance <paramref name="s"/>.</summary>
    private Int3 RingCandidate(float s, Random rng)
    {
        var (centre, tangent) = path.Pose(s);
        var (u, v) = PieceLists.Perpendiculars(tangent);
        var angle = rng.NextSingle() * MathF.Tau;
        // sqrt spreads points evenly over the ring's area rather than bunching them at the inner edge.
        var radius = float.Lerp(InnerRadius + 0.3f, OuterRadius, MathF.Sqrt(rng.NextSingle()));
        var p = centre + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
        return new Int3((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
    }

    public Int3? Flow(Int3 cell)
    {
        var near = Nearest(cell);
        if (near.Distance > OuterRadius + 1f) return null; // deep in the box, away from the tunnel: classic randomness

        // The path's direction snapped to the nearest grid axis, pointing with or against the flight.
        var t = near.Tangent * near.Flow;
        var ax = MathF.Abs(t.X);
        var ay = MathF.Abs(t.Y);
        var az = MathF.Abs(t.Z);
        if (ax >= ay && ax >= az) return new Int3(MathF.Sign(t.X), 0, 0);
        if (ay >= az) return new Int3(0, MathF.Sign(t.Y), 0);
        return new Int3(0, 0, MathF.Sign(t.Z));
    }

    /// <summary>
    /// Drop remembered answers for cells well behind the camera, so memory stays flat. Checking means scanning the
    /// whole cache, so only do it once it has grown to twice what was left last time. (Pruning whenever it's over a
    /// fixed size would rescan it every frame at high flight speeds, where the part still ahead can stay over that
    /// size on its own.)
    /// </summary>
    public void ForgetBehind()
    {
        if (_nearest.Count < _pruneAt) return;
        foreach (var key in _nearest.Where(kv => kv.Value.S < CameraS - 30f).Select(kv => kv.Key).ToList())
            _nearest.Remove(key);
        _pruneAt = Math.Max(50_000, _nearest.Count * 2);
    }

    private int _pruneAt = 50_000;

    private (float Distance, float S, Vector3 Tangent, int Flow) Nearest(Int3 cell)
    {
        if (_nearest.TryGetValue(cell, out var cached)) return cached;
        // Build the path a good way past anything we might ask about, so a cached answer never goes stale.
        path.EnsureLength(CameraS + SpawnAheadMax + 60f);
        return _nearest[cell] = path.Nearest(cell.ToVector());
    }
}
