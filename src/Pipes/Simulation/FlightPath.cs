using System.Numerics;

namespace Pipes.Simulation;

/// <summary>
/// The camera's route in fly-through mode: straight runs along grid axes, joined by maneuvers (quarter-circle turns,
/// long sweeping bends, snaking meanders and corkscrews), generated as far ahead as needed.
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
/// built. At every turn at least two directions remain allowed, so it never gets stuck. (A meander does wander a few
/// units back and forth sideways, but its overall drift obeys the rule, and a few units is well inside the gap to any
/// older tunnel.)
/// </para>
/// </remarks>
public sealed class FlightPath
{
    /// <summary>The kinds of maneuver joining the straights.</summary>
    public enum Kind
    {
        /// <summary>A tight quarter circle (radius 8) into a new direction: the camera banks hard, like a plane.</summary>
        Turn,

        /// <summary>A long, wide quarter circle (radius 22–30) into a new direction: a lazy, half-banked curve.</summary>
        Sweep,

        /// <summary>A snaking string of shallow arcs, swinging side to side, that ends up heading the same way.</summary>
        Meander,

        /// <summary>A spiral around the direction of travel, like a corkscrew, that ends up heading the same way.</summary>
        Corkscrew,
    }

    /// <summary>
    /// One maneuver: it runs from <see cref="StartS"/> to <see cref="EndS"/> along the path, from travelling along
    /// <see cref="From"/> to travelling along <see cref="To"/> (the same, for a meander). Turns and sweeps are quarter
    /// circles around <see cref="Centre"/>; a corkscrew's shape is its <see cref="Helix"/>. <see cref="Seed"/> is a
    /// random number between 0 and 1 picked for this maneuver, so anything that should vary from one to the next (like
    /// how a "pilot" flies it) can vary, yet come out the same every time it's replayed.
    /// </summary>
    public readonly record struct Maneuver(Kind Kind, float StartS, float EndS, Vector3 Centre, Vector3 From, Vector3 To, float Seed, Helix Helix = default);

    /// <summary>
    /// A corkscrew's shape: the path spirals round a straight <see cref="Axis"/> while flying along it, one full turn
    /// every <see cref="Pitch"/> units along the axis. <see cref="Spin"/> is +1 or −1 for which way it winds.
    /// </summary>
    /// <remarks>
    /// Everything is measured by <c>a</c>, how far along the axis. The spiral's radius opens up smoothly over the first
    /// <see cref="RampLength"/> and closes again over the last. Where the radius is 0 and not changing, the path is
    /// heading straight down the axis, so it joins the straights either side without a kink.
    /// </remarks>
    public readonly record struct Helix(Vector3 Start, Vector3 Axis, Vector3 U, float Spin, float Radius, float Pitch, float Length, float RampLength)
    {
        /// <summary>How far round the axis the path has wound by <paramref name="a"/> (radians, never wrapped).</summary>
        public float Angle(float a) => Spin * MathF.Tau * a / Pitch;

        /// <summary>Unit direction from the axis out to the path: <see cref="U"/> turned round the axis by <see cref="Angle"/>.</summary>
        public Vector3 Outward(float a) => Vector3.Transform(U, Quaternion.CreateFromAxisAngle(Axis, Angle(a)));

        public float RadiusAt(float a) => Radius * Smoothstep(a / RampLength) * Smoothstep((Length - a) / RampLength);

        public Vector3 Point(float a) => Start + Axis * a + Outward(a) * RadiusAt(a);

        /// <summary>How far along the axis <paramref name="p"/> is.</summary>
        public float Along(Vector3 p) => Vector3.Dot(p - Start, Axis);
    }

    public const float Spacing = 0.5f;

    /// <summary>Radius of the turns. Wide enough that a turn feels like banking, not snapping round a corner.</summary>
    private const float TurnRadius = 8f;

    /// <summary>
    /// How busy the course is, from the "Course complexity" setting: how long the straights between maneuvers are, and
    /// how often each kind of maneuver comes up (<see cref="Turn"/>, <see cref="Sweep"/> and <see cref="Meander"/> are
    /// chances out of 1; corkscrews get whatever's left).
    /// </summary>
    public readonly record struct Complexity(float MinStraight, float MaxStraight, float Turn, float Sweep, float Meander)
    {
        /// <summary>Long cruises and lazy curves, the odd turn, hardly any corkscrews.</summary>
        private static readonly Complexity Zen = new(45f, 90f, 0.15f, 0.40f, 0.40f);

        /// <summary>The original mix: mostly tight turns, gentler curves mixed in, the occasional corkscrew.</summary>
        private static readonly Complexity Balanced = new(18f, 40f, 0.50f, 0.22f, 0.18f);

        /// <summary>Straight from one maneuver into the next, and a corkscrew one time in four.</summary>
        private static readonly Complexity Wild = new(3f, 10f, 0.50f, 0.10f, 0.15f);

        /// <summary>
        /// The style for a slider position from 1 to 10. Level 5 is <see cref="Balanced"/>; below it blends towards
        /// <see cref="Zen"/> (at 1), above it towards <see cref="Wild"/> (at 10).
        /// </summary>
        public static Complexity ForLevel(int level) => level <= 5
            ? Blend(Zen, Balanced, (level - 1) / 4f)
            : Blend(Balanced, Wild, (level - 5) / 5f);

        private static Complexity Blend(Complexity a, Complexity b, float t) => new(
            float.Lerp(a.MinStraight, b.MinStraight, t), float.Lerp(a.MaxStraight, b.MaxStraight, t),
            float.Lerp(a.Turn, b.Turn, t), float.Lerp(a.Sweep, b.Sweep, t), float.Lerp(a.Meander, b.Meander, t));
    }

    /// <summary>Turns up or down are rarer than left or right, which feels more natural.</summary>
    private const float VerticalTurnWeight = 0.35f;

    /// <summary>Points are indexed in cubes this big for fast nearest-point lookups.</summary>
    private const int BucketSize = 8;

    private readonly Random _rng;
    private readonly Complexity _complexity;
    private readonly List<Vector3> _points = [];
    private readonly List<Vector3> _tangents = [];
    private readonly List<int> _flow = [];
    private readonly Dictionary<Int3, List<int>> _buckets = [];
    private readonly HashSet<Int3> _usedDirections = [];
    private readonly List<Maneuver> _maneuvers = [];
    private Int3 _direction;
    private int _currentFlow;

    /// <param name="firstRun">Length of the first straight, before the first turn.</param>
    public FlightPath(Vector3 start, Int3 direction, float firstRun, Complexity complexity, Random rng)
    {
        _rng = rng;
        _complexity = complexity;
        _direction = direction;
        _usedDirections.Add(direction);
        _currentFlow = NextFlow();
        AddPoint(start, direction.ToVector());
        AddStraight(firstRun);
    }

    /// <summary>Length generated so far, in world units.</summary>
    public float Length => (_points.Count - 1) * Spacing;

    /// <summary>Every maneuver generated so far, in order along the path.</summary>
    public IReadOnlyList<Maneuver> Maneuvers => _maneuvers;

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
            AddManeuver();
            AddStraight(float.Lerp(_complexity.MinStraight, _complexity.MaxStraight, _rng.NextSingle()));
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

    private void AddManeuver()
    {
        var roll = _rng.NextSingle();
        if (roll < _complexity.Turn) AddQuarter(Kind.Turn, TurnRadius);
        else if (roll < _complexity.Turn + _complexity.Sweep) AddQuarter(Kind.Sweep, float.Lerp(22f, 30f, _rng.NextSingle()));
        else if (roll < _complexity.Turn + _complexity.Sweep + _complexity.Meander) AddMeander();
        else AddCorkscrew();
    }

    /// <summary>A quarter circle from the current direction into a new, perpendicular one.</summary>
    private void AddQuarter(Kind kind, float radius)
    {
        var next = PickTurn();
        var d1 = _direction.ToVector();
        var d2 = next.ToVector();
        var startS = Length;
        var centre = _points[^1] + d2 * radius;
        _currentFlow = NextFlow(); // each new stretch picks its own flow

        AddArc(d1, d2, radius, MathF.PI * 0.5f);

        _maneuvers.Add(new Maneuver(kind, startS, Length, centre, d1, d2, _rng.NextSingle()));
        _direction = next;
        _usedDirections.Add(next);
    }

    /// <summary>
    /// A meander: swing off to one side by a shallow angle, then back and forth across the original line a few times,
    /// and finally straighten up, heading the way it started. Every swing is a circular arc, and they add up to no
    /// turn at all:
    /// <code>
    ///   +θ, −2θ, +2θ, ..., then ±θ to straighten up
    /// </code>
    /// The swings happen in the plane of the travel direction and a sideways direction the path is allowed to drift
    /// towards (picked like a turn), so the few units it wanders sideways are mostly in an allowed direction.
    /// </summary>
    private void AddMeander()
    {
        var forward = _direction.ToVector();
        var side = PickTurn();
        var across = side.ToVector();
        var radius = float.Lerp(18f, 26f, _rng.NextSingle());
        var swing = float.Lerp(20f, 30f, _rng.NextSingle()) * MathF.PI / 180f;
        var swings = 1 + _rng.Next(3); // full side-to-side swings in the middle
        var startS = Length;

        // heading = forward·cos(angle) + across·sin(angle): angle is how far it has swung off the original line.
        var angle = 0f;
        void SwingTo(float target)
        {
            var heading = forward * MathF.Cos(angle) + across * MathF.Sin(angle);
            // Perpendicular to the heading, in the swing plane, on the side it's turning towards.
            var toward = (-forward * MathF.Sin(angle) + across * MathF.Cos(angle)) * MathF.Sign(target - angle);
            AddArc(heading, toward, radius, MathF.Abs(target - angle));
            angle = target;
        }

        SwingTo(swing);
        for (var i = 0; i < swings; i++) SwingTo(-angle);
        SwingTo(0f);

        _maneuvers.Add(new Maneuver(Kind.Meander, startS, Length, Vector3.Zero, forward, forward, _rng.NextSingle()));
        _usedDirections.Add(side); // it drifted that way, so it may never head back the other way
    }

    /// <summary>
    /// A corkscrew: 1, 2 or 3 turns of spiral (radius 3.5–5, a turn every 32–40 units), the first half turn opening
    /// up and the last half turn closing down. Each turn is one barrel roll, and three in a row is a lot, so shorter
    /// ones come up more often: one roll 45% of the time, two 35%, three 20%. Coils that close together would merge
    /// their tunnels, but one turn apart they're a whole pitch (32+ units) apart, and half a turn apart about 20,
    /// both well clear.
    /// </summary>
    private void AddCorkscrew()
    {
        var axis = _direction.ToVector();
        var pitch = float.Lerp(32f, 40f, _rng.NextSingle());
        var turns = _rng.NextSingle() switch { < 0.45f => 1, < 0.8f => 2, _ => 3 };
        var helix = new Helix(
            Start: _points[^1],
            Axis: axis,
            U: PickTurn().ToVector(), // any direction perpendicular to the axis will do
            Spin: _rng.Next(2) == 0 ? -1f : 1f,
            Radius: float.Lerp(3.5f, 5f, _rng.NextSingle()),
            Pitch: pitch,
            Length: turns * pitch,
            RampLength: pitch * 0.5f);
        var startS = Length;

        // The spiral is defined by distance along the axis, but path points must be evenly spaced along the path
        // itself. So walk along the axis in tiny steps, measure the distance actually covered, and drop a point every
        // time it passes another Spacing.
        const float step = 0.02f;
        var steps = (int)(helix.Length / step);
        var previous = helix.Point(0f);
        var covered = 0f;
        var nextPoint = Spacing;
        for (var i = 1; i <= steps; i++)
        {
            var a = i * step;
            var p = helix.Point(a);
            var d = Vector3.Distance(previous, p);
            while (covered + d >= nextPoint)
            {
                var t = (nextPoint - covered) / d;
                var at = a - step + t * step;
                AddPoint(Vector3.Lerp(previous, p, t), Vector3.Normalize(helix.Point(at + step) - helix.Point(at - step)));
                nextPoint += Spacing;
            }
            covered += d;
            previous = p;
        }

        _maneuvers.Add(new Maneuver(Kind.Corkscrew, startS, Length, helix.Start, axis, axis, _rng.NextSingle(), helix));
    }

    /// <summary>
    /// A circular arc from the path's end: starting along <paramref name="heading"/>, curving towards
    /// <paramref name="toward"/> (perpendicular to it) by <paramref name="angle"/> radians.
    /// </summary>
    private void AddArc(Vector3 heading, Vector3 toward, float radius, float angle)
    {
        // Same maths as a pipe elbow: from the centre, the arc starts at -toward and sweeps round towards +heading.
        var centre = _points[^1] + toward * radius;
        var n = Math.Max(1, (int)MathF.Round(angle * radius / Spacing));
        for (var i = 1; i <= n; i++)
        {
            var theta = angle * i / n;
            var (sin, cos) = MathF.SinCos(theta);
            AddPoint(centre + (-toward * cos + heading * sin) * radius, heading * cos + toward * sin);
        }
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

    /// <summary>Smoothstep: 0 to 1 with a gentle start and finish (flat at both ends).</summary>
    private static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Int3 BucketOf(Vector3 p) =>
        new((int)MathF.Floor(p.X / BucketSize), (int)MathF.Floor(p.Y / BucketSize), (int)MathF.Floor(p.Z / BucketSize));

    private static int Dot(Int3 a, Int3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
