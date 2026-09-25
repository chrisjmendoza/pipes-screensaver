using System.Numerics;

namespace Pipes.Simulation;

/// <summary>
/// The camera's route in fly-through mode: straight runs along grid axes, joined by wide quarter-circle turns,
/// generated as far ahead as needed.
/// </summary>
/// <remarks>
/// <para>
/// The path is stored as points every <see cref="Spacing"/> units, so "how far along" (<c>s</c>, in world units) maps
/// straight to an index. That makes both questions the tunnel asks cheap: "where is the camera at distance s?" and
/// "how far is this cell from the path, and where along it?"
/// </para>
/// <para>
/// <b>It never doubles back.</b> Once the path has moved in some direction (say +X), it may never move in the
/// opposite one (−X). So along every axis it only ever advances, and it can't loop round into the tunnel it already
/// built. At every turn at least two directions remain allowed, so it never gets stuck.
/// </para>
/// </remarks>
public sealed class FlightPath
{
    /// <summary>
    /// One quarter-circle turn: it runs from <see cref="StartS"/> to <see cref="EndS"/> along the path, around
    /// <see cref="Centre"/>, from travelling along <see cref="From"/> to travelling along <see cref="To"/>.
    /// <see cref="Seed"/> is a random number between 0 and 1 picked for this turn, so anything that should vary from
    /// turn to turn (like how a "pilot" flies it) can vary, yet come out the same every time the turn is replayed.
    /// </summary>
    public readonly record struct Turn(float StartS, float EndS, Vector3 Centre, Vector3 From, Vector3 To, float Seed);

    public const float Spacing = 0.5f;

    /// <summary>Radius of the turns. Wide enough that a turn feels like banking, not snapping round a corner.</summary>
    private const float TurnRadius = 8f;

    /// <summary>Turns up or down are rarer than left or right, which feels more natural.</summary>
    private const float VerticalTurnWeight = 0.35f;

    /// <summary>Points are indexed in cubes this big for fast nearest-point lookups.</summary>
    private const int BucketSize = 8;

    private readonly Random _rng;
    private readonly List<Vector3> _points = [];
    private readonly List<Vector3> _tangents = [];
    private readonly List<int> _flow = [];
    private readonly Dictionary<Int3, List<int>> _buckets = [];
    private readonly HashSet<Int3> _usedDirections = [];
    private readonly List<Turn> _turns = [];
    private Int3 _direction;
    private int _currentFlow;

    /// <param name="firstRun">Length of the first straight, before the first turn.</param>
    public FlightPath(Vector3 start, Int3 direction, float firstRun, Random rng)
    {
        _rng = rng;
        _direction = direction;
        _usedDirections.Add(direction);
        _currentFlow = NextFlow();
        AddPoint(start, direction.ToVector());
        AddStraight(firstRun);
    }

    /// <summary>Length generated so far, in world units.</summary>
    public float Length => (_points.Count - 1) * Spacing;

    /// <summary>Every turn generated so far, in order along the path.</summary>
    public IReadOnlyList<Turn> Turns => _turns;

    /// <summary>Position and direction of travel at distance <paramref name="s"/> along the path.</summary>
    public (Vector3 Position, Vector3 Tangent) Pose(float s)
    {
        EnsureLength(s + 1f);
        s = Math.Max(s, 0f);
        var f = s / Spacing;
        var i = (int)f;
        var t = f - i;
        return (Vector3.Lerp(_points[i], _points[i + 1], t), Vector3.Normalize(Vector3.Lerp(_tangents[i], _tangents[i + 1], t)));
    }

    /// <summary>Make sure the path exists at least as far as <paramref name="s"/>.</summary>
    public void EnsureLength(float s)
    {
        while (Length < s)
        {
            AddTurn();
            AddStraight(18f + _rng.NextSingle() * 22f);
        }
    }

    /// <summary>
    /// The path point nearest to <paramref name="p"/>: how far away it is, how far along the path it is, the path's
    /// direction there, and that stretch's flow (+1: pipes run the way the camera flies, -1: against it). Only looks
    /// within about one bucket, so anything farther than that reports <see cref="float.MaxValue"/>.
    /// </summary>
    public (float Distance, float S, Vector3 Tangent, int Flow) Nearest(Vector3 p)
    {
        var key = BucketOf(p);
        var best = -1;
        var bestD2 = float.MaxValue;
        for (var x = -1; x <= 1; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -1; z <= 1; z++)
        {
            if (!_buckets.TryGetValue(new Int3(key.X + x, key.Y + y, key.Z + z), out var indices)) continue;
            foreach (var i in indices)
            {
                var d2 = Vector3.DistanceSquared(p, _points[i]);
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
        }
        return best < 0
            ? (float.MaxValue, 0f, Vector3.Zero, 0)
            : (MathF.Sqrt(bestD2), best * Spacing, _tangents[best], _flow[best]);
    }

    private void AddStraight(float length)
    {
        var d = _direction.ToVector();
        var start = _points[^1];
        var n = (int)MathF.Round(length / Spacing);
        for (var i = 1; i <= n; i++) AddPoint(start + d * (i * Spacing), d);
    }

    /// <summary>A quarter circle from the current direction into a new, perpendicular one.</summary>
    private void AddTurn()
    {
        var next = PickTurn();
        var d1 = _direction.ToVector();
        var d2 = next.ToVector();
        var start = _points[^1];
        var startS = Length;
        var centre = start + d2 * TurnRadius;
        _currentFlow = NextFlow(); // each new stretch picks its own flow

        // Same maths as a pipe elbow: from the centre, the arc starts at -d2 and sweeps round to +d1.
        var arcLength = MathF.PI * 0.5f * TurnRadius;
        var n = (int)MathF.Round(arcLength / Spacing);
        for (var i = 1; i <= n; i++)
        {
            var theta = MathF.PI * 0.5f * i / n;
            var p = centre + (-d2 * MathF.Cos(theta) + d1 * MathF.Sin(theta)) * TurnRadius;
            AddPoint(p, d1 * MathF.Cos(theta) + d2 * MathF.Sin(theta));
        }

        _turns.Add(new Turn(startS, Length, centre, d1, d2, _rng.NextSingle()));
        _direction = next;
        _usedDirections.Add(next);
    }

    private Int3 PickTurn()
    {
        // Perpendicular to the current direction, and never the reverse of any direction already taken.
        Int3[] all = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        var options = all.Where(d => Dot(d, _direction) == 0 && !_usedDirections.Contains(d.Negate())).ToList();
        var weights = options.Select(d => d.Y != 0 ? VerticalTurnWeight : 1f).ToList();
        var roll = _rng.NextSingle() * weights.Sum();
        for (var i = 0; i < options.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return options[i];
        }
        return options[^1];
    }

    private int NextFlow() => _rng.Next(2) == 0 ? 1 : -1;

    private void AddPoint(Vector3 p, Vector3 tangent)
    {
        var index = _points.Count;
        _points.Add(p);
        _tangents.Add(tangent);
        _flow.Add(_currentFlow);
        var key = BucketOf(p);
        if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = [];
        list.Add(index);
    }

    private static Int3 BucketOf(Vector3 p) =>
        new((int)MathF.Floor(p.X / BucketSize), (int)MathF.Floor(p.Y / BucketSize), (int)MathF.Floor(p.Z / BucketSize));

    private static int Dot(Int3 a, Int3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
