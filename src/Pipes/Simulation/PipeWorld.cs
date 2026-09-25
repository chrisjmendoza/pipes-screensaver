using System.Numerics;

namespace Pipes.Simulation;

/// <summary>
/// The classic pipes rules on a 3D grid: each pipe walks cell to cell, never crossing itself or another pipe,
/// turns at random (or when blocked), and dies when boxed in. A new pipe then starts somewhere free. <em>Where</em>
/// pipes may grow is up to the <see cref="IPipeSpace"/>: a box for the classic scene, a tunnel for fly-through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Steps run face to face.</b> Cells are 1 unit apart. A pipe's head moves through one cell per step, entering
/// through the middle of one face and leaving through the middle of another. That makes every cell one of a few
/// tiles, which is what lets a turn be drawn as a real curved elbow:
/// </para>
/// <code>
///   Straight          Bend (smooth)       Knee (ball joint)
///   ┌───────┐         ┌───────┐           ┌───────┐
///   │       │         │       │           │       │
///   ═══════════       ═══╗    │           ════●   │
///   │       │         │  ║    │           │   ║   │
///   └───────┘         └──║────┘           └───║───┘
/// </code>
/// <para>
/// A pipe's very first step goes from the centre of its spawn cell to a face (<see cref="StepKind.Start"/>), and
/// its last goes from a face to the centre, where it gets an end cap (<see cref="StepKind.End"/>).
/// </para>
/// <para>
/// <b>Direction is decided on entry.</b> When the head reaches a new cell, the pipe immediately decides where it
/// will leave, and reserves that neighbour. Reserving ahead means two pipes can never race into the same cell.
/// Any random choice about the cell (turn, fitting, teapot) is made at that moment and stored on the pipe,
/// because the drawing code runs every frame and has to draw the same thing each time.
/// </para>
/// </remarks>
public sealed class PipeWorld
{
    public const float PipeRadius = 0.2f;

    /// <summary>Ball joints are this much fatter than the pipe, like the original's.</summary>
    private const float BallScale = 1.6f;

    private const float TurnChance = 0.22f;
    private const float Half = 0.5f;

    /// <summary>Chance per straight step of a valve, coupling or flange (when fittings are on).</summary>
    private const float FittingChance = 0.035f;
    /// <summary>Chance per straight step of a tee junction that starts a branch pipe.</summary>
    private const float TeeChance = 0.02f;
    /// <summary>Chance per turn that the joint is a teapot. Rare on purpose: about one per default scene.</summary>
    private const float TeapotChance = 1f / 300f;

    private static readonly Int3[] Directions =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    // Saturated 90s-ish colours (sRGB), converted to linear on use.
    private static readonly Vector3[] Palette =
    [
        new(0.90f, 0.12f, 0.12f), new(0.10f, 0.75f, 0.20f), new(0.15f, 0.35f, 0.95f), new(0.95f, 0.80f, 0.10f),
        new(0.10f, 0.80f, 0.85f), new(0.85f, 0.20f, 0.80f), new(0.98f, 0.50f, 0.08f), new(0.85f, 0.85f, 0.88f),
        new(0.55f, 0.30f, 0.95f), new(0.40f, 0.90f, 0.35f),
    ];

    /// <summary>Thicknesses to pick from when <see cref="PipesSettings.VaryThickness"/> is on. 0.2 is listed twice so it stays the most common.</summary>
    private static readonly float[] Radii = [0.12f, 0.15f, 0.2f, 0.2f, 0.24f];

    /// <summary>Finishes used by <see cref="Finish.Mixed"/>, as (metallic, roughness).</summary>
    private static readonly (float Metallic, float Roughness)[] MixedFinishes =
    [
        (0f, 0.3f),     // glossy plastic
        (0f, 0.6f),     // satin plastic
        (0.85f, 0.18f), // polished metal
        (0.8f, 0.5f),   // brushed metal
    ];

    /// <summary>Bare steel for valve stems and flange bolts.</summary>
    private static readonly PipeMaterial Steel = new(ToLinear(new Vector3(0.72f, 0.73f, 0.76f)), 0.9f, 0.35f);

    /// <summary>Finished geometry is filed in cubes of this many cells per side, so it can be dropped a cube at a time.</summary>
    public const int ChunkSize = 8;

    private readonly IPipeSpace _space;
    private readonly Random _rng;
    private readonly PipesSettings _settings;

    // Cells taken by a pipe (or reserved for one about to arrive). A set rather than a 3D array, so the world has no
    // fixed size: fly-through mode keeps growing it ahead of the camera and trimming it behind.
    private readonly HashSet<Int3> _occupied = [];

    // Geometry of completed steps, filed by chunk. Pieces never change once added; whole chunks get dropped when
    // they're far behind the camera (see Recycle).
    private readonly Dictionary<Int3, PieceLists> _chunks = [];

    private readonly List<Pipe> _active = [];
    private int _spawned;
    private int _lastColor = -1;

    public PipeWorld(IPipeSpace space, PipesSettings settings, Random rng)
    {
        _space = space;
        _settings = settings;
        _rng = rng;
        ConcurrentPipes = settings.ConcurrentPipes;
        PipeQuota = settings.PipesPerScene;
    }

    /// <summary>How many pipes grow at once. Starts at the user's setting; fly-through raises it.</summary>
    public int ConcurrentPipes { get; set; }

    /// <summary>How many pipes this world may start in total. <see cref="int.MaxValue"/> for endless.</summary>
    public int PipeQuota { get; set; }

    /// <summary>True once the world has started its quota of pipes and all have finished.</summary>
    public bool IsFinished => AllStarted && _active.Count == 0;

    /// <summary>True once the world has started its quota of pipes (some may still be growing).</summary>
    public bool AllStarted => _spawned >= PipeQuota;

    public void Update(float dt)
    {
        while (_active.Count < ConcurrentPipes && _spawned < PipeQuota)
        {
            _spawned++;
            if (TrySpawn() is { } p) _active.Add(p);
            else
            {
                // No room found. A box is full, so stop for good. An endless space just has no room right here,
                // right now: try again next frame.
                if (_space.IsBounded) _spawned = PipeQuota;
                else _spawned--;
                break;
            }
        }

        // Backwards so finished pipes can be removed in place. Branches started by a tee are appended to the end
        // of the list, so this loop won't reach them until next frame.
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var pipe = _active[i];
            // Travel is measured in world units, so pipes grow at a constant speed whether the step is a
            // full cell, a half cell, or a (shorter) curved bend. At high speed one frame can finish several steps.
            pipe.Travel += dt * _settings.Speed;
            while (pipe.Alive && pipe.Travel >= StepLength(pipe.Step))
            {
                pipe.Travel -= StepLength(pipe.Step);
                CompleteStep(pipe);
            }
            if (!pipe.Alive) _active.RemoveAt(i);
        }
    }

    /// <summary>Completed geometry plus the partially grown step of each live pipe.</summary>
    public void Collect(PieceLists into)
    {
        into.Clear();
        foreach (var chunk in _chunks.Values) into.AddFrom(chunk);
        foreach (var p in _active)
            EmitStep(p, p.Travel / StepLength(p.Step), into, withHead: true);
    }

    /// <summary>
    /// Forget every chunk for which <paramref name="drop"/> (given the chunk's centre) returns true: its geometry,
    /// the cells it had occupied, and any pipe still growing inside it. Fly-through uses this to throw away what's
    /// behind the camera, so memory and drawing cost stay flat however long it flies.
    /// </summary>
    public void Recycle(Func<Vector3, bool> drop)
    {
        List<Int3>? dropped = null;
        foreach (var key in _chunks.Keys)
        {
            var centre = (key.ToVector() + new Vector3(0.5f)) * ChunkSize;
            if (drop(centre)) (dropped ??= []).Add(key);
        }
        if (dropped == null) return;

        foreach (var key in dropped)
        {
            _chunks.Remove(key);
            for (var x = 0; x < ChunkSize; x++)
            for (var y = 0; y < ChunkSize; y++)
            for (var z = 0; z < ChunkSize; z++)
                _occupied.Remove(new Int3(key.X * ChunkSize + x, key.Y * ChunkSize + y, key.Z * ChunkSize + z));
        }
        foreach (var pipe in _active)
            if (dropped.Contains(ChunkOf(pipe.Cell))) pipe.Alive = false;
        _active.RemoveAll(p => !p.Alive);
    }

    private static Int3 ChunkOf(Int3 cell) => new(FloorDiv(cell.X), FloorDiv(cell.Y), FloorDiv(cell.Z));

    /// <summary>Division that rounds down for negative numbers too (C#'s / rounds towards zero: -1 / 8 == 0).</summary>
    private static int FloorDiv(int v) => v >= 0 ? v / ChunkSize : (v - (ChunkSize - 1)) / ChunkSize;

    /// <summary>The finished-geometry list for the chunk containing <paramref name="cell"/>.</summary>
    private PieceLists ChunkFor(Int3 cell)
    {
        var key = ChunkOf(cell);
        if (!_chunks.TryGetValue(key, out var chunk)) _chunks[key] = chunk = new PieceLists();
        return chunk;
    }

    private static float StepLength(StepKind step) => step switch
    {
        StepKind.Start or StepKind.End => Half,
        StepKind.Bend => MathF.PI * Half * 0.5f, // quarter circle of radius 0.5
        _ => 1f,
    };

    private Pipe? TrySpawn()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var pos = _space.SpawnCandidate(_rng);
            if (!IsFree(pos)) continue;

            var dirs = FreeDirections(pos, exclude: null);
            if (dirs.Count == 0) continue;

            var pipe = new Pipe
            {
                Cell = pos,
                Radius = NextRadius(),
                Material = NextMaterial(),
                // A length budget keeps pipes from wandering forever in big grids, so more colours get a turn.
                Remaining = _rng.Next(30, 90),
            };
            Reserve(pos);
            ChunkFor(pos).Sphere(pos.ToVector(), pipe.Radius * BallScale, pipe.Material);

            var dir = dirs[_rng.Next(dirs.Count)];
            pipe.Step = StepKind.Start;
            pipe.In = pipe.Out = dir;
            Reserve(pos + dir);
            return pipe;
        }
        return null;
    }

    /// <summary>A new pipe growing out of the side of a tee, in the same colour and thickness.</summary>
    private Pipe Branch(Pipe parent) => new()
    {
        Cell = parent.Cell,
        Step = StepKind.Start,
        In = parent.BranchDir,
        Out = parent.BranchDir,
        Radius = parent.Radius,
        Material = parent.Material,
        Remaining = _rng.Next(12, 40),
    };

    /// <summary>The head reached the end of its step: keep the finished geometry and move into the next cell.</summary>
    private void CompleteStep(Pipe pipe)
    {
        EmitStep(pipe, 1f, ChunkFor(pipe.Cell), withHead: false);
        if (pipe.Step == StepKind.End)
        {
            pipe.Alive = false;
            return;
        }
        if (pipe.Joint == Joint.Tee) _active.Add(Branch(pipe));
        BeginStep(pipe, pipe.Cell + pipe.Out, pipe.Out);
    }

    /// <summary>The head has just entered <paramref name="cell"/> moving along <paramref name="dirIn"/>: decide how to leave it.</summary>
    private void BeginStep(Pipe pipe, Int3 cell, Int3 dirIn)
    {
        pipe.Cell = cell;
        pipe.In = dirIn;
        pipe.Out = dirIn;
        pipe.Joint = Joint.None;

        if (--pipe.Remaining <= 0)
        {
            EndPipe(pipe);
            return;
        }

        var ahead = cell + dirIn;
        var blocked = !IsFree(ahead);

        // Where the space has a flow (fly-through tunnels), pipes going with it run long and straight, and pipes going
        // across it soon turn into it. That's what lines the tunnel with pipes running along it. With no flow, this is
        // the classic 22% chance.
        var flow = _space.Flow(cell);
        var turnChance = flow is { } f ? (dirIn == f ? TurnChance * 0.35f : 0.55f) : TurnChance;

        if (blocked || _rng.NextSingle() < turnChance)
        {
            var turns = FreeDirections(cell, exclude: dirIn.Negate());
            turns.Remove(dirIn);
            if (turns.Count > 0)
            {
                pipe.Out = flow is { } along && turns.Contains(along) && _rng.NextSingle() < 0.75f
                    ? along
                    : turns[_rng.Next(turns.Count)];
                Reserve(cell + pipe.Out);
                ChooseTurn(pipe);
                return;
            }
            if (blocked)
            {
                // Boxed in: stop in the middle of this cell with an end cap.
                EndPipe(pipe);
                return;
            }
        }

        pipe.Step = StepKind.Straight;
        Reserve(ahead);
        if (_settings.Fittings) ChooseFitting(pipe);
    }

    private void ChooseTurn(Pipe pipe)
    {
        if (_settings.Teapots && _rng.NextSingle() < TeapotChance)
        {
            pipe.Step = StepKind.Knee;
            pipe.Joint = Joint.Teapot;
            // Teapots stay upright; only their heading is random.
            var heading = _rng.NextSingle() * MathF.Tau;
            pipe.FittingAxis = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading));
            return;
        }

        var smooth = _settings.Joints switch
        {
            JointStyle.Smooth => true,
            JointStyle.Mixed => _rng.Next(2) == 0,
            _ => false,
        };
        pipe.Step = smooth ? StepKind.Bend : StepKind.Knee;
        pipe.Joint = smooth ? Joint.None : Joint.Ball;
    }

    /// <summary>Maybe decorate a straight step with a fitting or a tee.</summary>
    private void ChooseFitting(Pipe pipe)
    {
        var roll = _rng.NextSingle();
        if (roll < TeeChance)
        {
            // A tee needs a free side to branch into, and the branch counts towards the scene's pipe quota.
            if (_spawned >= PipeQuota) return;
            var sides = FreeDirections(pipe.Cell, exclude: pipe.In.Negate());
            sides.Remove(pipe.In);
            if (sides.Count == 0) return;
            pipe.BranchDir = sides[_rng.Next(sides.Count)];
            Reserve(pipe.Cell + pipe.BranchDir);
            _spawned++;
            pipe.Joint = Joint.Tee;
        }
        else if (roll < TeeChance + FittingChance)
        {
            pipe.Joint = _rng.Next(3) switch { 0 => Joint.Valve, 1 => Joint.Coupling, _ => Joint.Flange };
            // Valve handwheels stick out sideways: one of the four directions perpendicular to the pipe,
            // biased towards up so most valves look the right way round.
            var (u, v) = PieceLists.Perpendiculars(pipe.In.ToVector());
            Vector3[] sides = [u, -u, v, -v];
            pipe.FittingAxis = sides.MaxBy(s => s.Y + _rng.NextSingle() * 1.2f);
            pipe.Accent = new PipeMaterial(OtherColor(pipe.Material.Color), 0f, 0.3f);
        }
    }

    private static void EndPipe(Pipe pipe)
    {
        pipe.Step = StepKind.End;
        pipe.Joint = Joint.EndCap;
    }

    /// <summary>
    /// Adds the geometry for <paramref name="fraction"/> (0..1) of the pipe's current step. With
    /// <paramref name="withHead"/>, the growing end also gets a rounded cap.
    /// </summary>
    private static void EmitStep(Pipe p, float fraction, PieceLists into, bool withHead)
    {
        var centre = p.Cell.ToVector();
        var dirIn = p.In.ToVector();
        var dirOut = p.Out.ToVector();
        var entry = centre - dirIn * Half; // middle of the face the pipe came in through
        var m = p.Material;
        Vector3 tip;

        switch (p.Step)
        {
            case StepKind.Start: // centre -> exit face
                tip = centre + dirOut * (Half * fraction);
                into.Cylinder(centre, tip, p.Radius, m);
                break;

            case StepKind.End: // entry face -> centre
                tip = entry + dirIn * (Half * fraction);
                into.Cylinder(entry, tip, p.Radius, m);
                break;

            case StepKind.Straight:
                tip = entry + dirIn * fraction;
                into.Cylinder(entry, tip, p.Radius, m);
                break;

            case StepKind.Knee: // straight in to the centre, then straight out: a sharp corner under a ball
                if (fraction <= 0.5f)
                {
                    tip = entry + dirIn * fraction;
                    into.Cylinder(entry, tip, p.Radius, m);
                }
                else
                {
                    into.Cylinder(entry, centre, p.Radius, m);
                    tip = centre + dirOut * (fraction - 0.5f);
                    into.Cylinder(centre, tip, p.Radius, m);
                }
                break;

            case StepKind.Bend:
            {
                // A quarter circle of radius 0.5 from the entry face to the exit face. Its centre is half a cell
                // back along dirIn and half a cell out along dirOut, i.e. the middle of the cell edge the pipe
                // curves around.
                var arcCentre = centre + (dirOut - dirIn) * Half;
                var sweep = fraction * MathF.PI * 0.5f;
                into.Elbow(arcCentre, dirIn, dirOut, Half, p.Radius, sweep, m);
                tip = arcCentre + (dirIn * MathF.Sin(sweep) - dirOut * MathF.Cos(sweep)) * Half;
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown step {p.Step}");
        }

        // Joints and fittings sit at the cell centre and pop in once the head gets there (end caps once the
        // step completes).
        var jointAt = p.Step == StepKind.End ? 1f : 0.5f;
        if (fraction >= jointAt) EmitJoint(p, centre, into);

        // A pipe-width sphere on the head keeps the growing end rounded.
        if (withHead) into.Sphere(tip, p.Radius, m);
    }

    private static void EmitJoint(Pipe p, Vector3 centre, PieceLists into)
    {
        var r = p.Radius;
        var m = p.Material;
        var along = p.In.ToVector(); // for fittings on straight steps: the pipe's direction

        switch (p.Joint)
        {
            case Joint.Ball:
            case Joint.EndCap:
                into.Sphere(centre, r * BallScale, m);
                break;

            case Joint.Tee:
                into.Sphere(centre, r * 1.5f, m);
                break;

            case Joint.Teapot:
                // Scaled so the pot's body is clearly wider than a ball joint (see MeshBuilder.Teapot for its size). The
                // spout and handle poke a little past the cell, which is part of the charm.
                into.Oriented(MeshKind.Teapot, centre, Vector3.UnitY, p.FittingAxis, MathF.Min(r * 1.55f, 0.3f), m);
                break;

            case Joint.Valve:
            {
                // Round body, a steel stem out the side, and a painted handwheel with two spokes.
                var bodyRadius = r * 1.55f;
                var stemLength = MathF.Min(bodyRadius + 0.13f, 0.47f); // stay inside the cell
                var wheelCentre = centre + p.FittingAxis * stemLength;
                var wheelRadius = r * 1.7f;
                into.Sphere(centre, bodyRadius, m);
                into.Cylinder(centre, wheelCentre, r * 0.25f, Steel);
                into.Ring(wheelCentre, p.FittingAxis, wheelRadius, r * 0.2f, p.Accent);
                var (u, v) = PieceLists.Perpendiculars(p.FittingAxis);
                into.Cylinder(wheelCentre - u * wheelRadius, wheelCentre + u * wheelRadius, r * 0.1f, p.Accent);
                into.Cylinder(wheelCentre - v * wheelRadius, wheelCentre + v * wheelRadius, r * 0.1f, p.Accent);
                into.Sphere(wheelCentre, r * 0.3f, Steel);
                break;
            }

            case Joint.Coupling:
            {
                // A fatter sleeve. A thin ring (torus) at each end rounds off its edges: the ring's outside lines up
                // with the sleeve's radius, and its far side with the sleeve's full length.
                const float halfLength = 0.14f;
                var sleeve = r * 1.3f;
                var edge = r * 0.12f;
                var a = centre - along * (halfLength - edge);
                var b = centre + along * (halfLength - edge);
                into.Cylinder(a, b, sleeve, m);
                into.Ring(a, along, sleeve - edge, edge, m);
                into.Ring(b, along, sleeve - edge, edge, m);
                break;
            }

            case Joint.Flange:
            {
                // Two discs with a small gap between them, and six steel bolts through both.
                var discRadius = MathF.Min(r * 2.1f, 0.44f);
                const float gap = 0.012f, thickness = 0.05f;
                into.Cylinder(centre - along * (gap + thickness), centre - along * gap, discRadius, m);
                into.Cylinder(centre + along * gap, centre + along * (gap + thickness), discRadius, m);
                var (u, v) = PieceLists.Perpendiculars(along);
                for (var k = 0; k < 6; k++)
                {
                    var angle = MathF.Tau * k / 6f;
                    var bolt = centre + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * (discRadius * 0.78f);
                    into.Cylinder(bolt - along * 0.09f, bolt + along * 0.09f, discRadius * 0.085f, Steel);
                }
                break;
            }
        }
    }

    private List<Int3> FreeDirections(Int3 pos, Int3? exclude)
    {
        var list = new List<Int3>(6);
        foreach (var d in Directions)
        {
            if (exclude is { } ex && d == ex) continue;
            if (IsFree(pos + d)) list.Add(d);
        }
        return list;
    }

    private float NextRadius() => _settings.VaryThickness ? Radii[_rng.Next(Radii.Length)] : PipeRadius;

    private PipeMaterial NextMaterial()
    {
        var (metallic, roughness) = _settings.Finish switch
        {
            Finish.Metallic => (0.85f, 0.18f),
            Finish.Mixed => MixedFinishes[_rng.Next(MixedFinishes.Length)],
            _ => (0f, 0.3f),
        };
        return new PipeMaterial(NextColor(), metallic, roughness);
    }

    private Vector3 NextColor()
    {
        int idx;
        do idx = _rng.Next(Palette.Length); while (idx == _lastColor);
        _lastColor = idx;
        return ToLinear(Palette[idx]);
    }

    /// <summary>A palette colour different from <paramref name="linear"/>, for accents like valve wheels.</summary>
    private Vector3 OtherColor(Vector3 linear)
    {
        Vector3 c;
        do c = ToLinear(Palette[_rng.Next(Palette.Length)]); while (c == linear);
        return c;
    }

    /// <summary>sRGB to linear (close-enough gamma 2.2). Lighting maths must happen in linear space.</summary>
    private static Vector3 ToLinear(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    private void Reserve(Int3 p) => _occupied.Add(p);

    /// <summary>Inside the space, and not taken by any pipe.</summary>
    private bool IsFree(Int3 p) => _space.Contains(p) && !_occupied.Contains(p);

    /// <summary>The shape of the path through one cell.</summary>
    private enum StepKind
    {
        /// <summary>Spawn cell: centre to exit face.</summary>
        Start,
        /// <summary>Face to opposite face.</summary>
        Straight,
        /// <summary>Face to side face along a quarter circle (smooth elbow).</summary>
        Bend,
        /// <summary>Face to centre to side face (sharp corner, hidden by a ball joint or teapot).</summary>
        Knee,
        /// <summary>Face to centre, then the pipe ends.</summary>
        End,
    }

    /// <summary>Decoration drawn at the centre of the current cell.</summary>
    private enum Joint
    {
        None,
        /// <summary>Classic ball on a sharp corner.</summary>
        Ball,
        /// <summary>Ball where a pipe ends.</summary>
        EndCap,
        /// <summary>The easter egg, in place of a ball.</summary>
        Teapot,
        /// <summary>Ball where a branch pipe splits off.</summary>
        Tee,
        Valve,
        Coupling,
        Flange,
    }

    private sealed class Pipe
    {
        /// <summary>Cell the head is currently passing through.</summary>
        public Int3 Cell;
        /// <summary>Direction the head was travelling when it entered <see cref="Cell"/>.</summary>
        public Int3 In;
        /// <summary>Direction it will leave <see cref="Cell"/> (already reserved).</summary>
        public Int3 Out;
        public StepKind Step;
        public Joint Joint;
        /// <summary>Valves: which way the handwheel points. Teapots: which way the spout faces.</summary>
        public Vector3 FittingAxis;
        /// <summary>Tees: direction the branch leaves in (already reserved).</summary>
        public Int3 BranchDir;
        /// <summary>Second colour for fittings, e.g. a valve's handwheel.</summary>
        public PipeMaterial Accent;
        /// <summary>Distance travelled into the current step, in world units.</summary>
        public float Travel;
        /// <summary>Steps left in this pipe's length budget.</summary>
        public int Remaining;
        public float Radius;
        public PipeMaterial Material;
        public bool Alive = true;
    }
}

public readonly record struct Int3(int X, int Y, int Z)
{
    public static Int3 operator +(Int3 a, Int3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public Int3 Negate() => new(-X, -Y, -Z);
    public Vector3 ToVector() => new(X, Y, Z);
}
