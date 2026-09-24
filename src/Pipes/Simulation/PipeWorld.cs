using System.Numerics;

namespace Pipes.Simulation;

/// <summary>One drawable piece. Cylinders run from <see cref="Start"/> along <see cref="Axis"/>; spheres sit at <see cref="Start"/>.</summary>
public struct PipeInstance
{
    public Vector3 Start;
    public Vector3 Axis;
    public Vector3 Color; // linear RGB
    public float Radius;
}

/// <summary>
/// The classic pipes rules on a 3D grid: each pipe walks cell to cell, never crossing itself or another pipe,
/// turns at random (or when blocked), and dies when boxed in. A new pipe then starts somewhere free.
/// Unlike the original, a pipe's head grows smoothly between cells instead of snapping.
/// </summary>
public sealed class PipeWorld
{
    public const float PipeRadius = 0.2f;
    private const float ClassicJointRadius = 0.32f;
    private const float TurnChance = 0.22f;

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

    private readonly Random _rng;
    private readonly PipesSettings _settings;
    private readonly bool[,,] _occupied;
    private readonly List<PipeInstance> _cylinders = [];
    private readonly List<PipeInstance> _spheres = [];
    private readonly List<Pipe> _active = [];
    private int _spawned;
    private int _lastColor = -1;

    public PipeWorld(Int3 size, PipesSettings settings, Random rng)
    {
        Size = size;
        _settings = settings;
        _rng = rng;
        _occupied = new bool[size.X, size.Y, size.Z];
        Center = new Vector3(size.X - 1, size.Y - 1, size.Z - 1) * 0.5f;
    }

    public Int3 Size { get; }

    public Vector3 Center { get; }

    /// <summary>True once the scene has drawn its quota of pipes and all have finished.</summary>
    public bool IsFinished => _spawned >= _settings.PipesPerScene && _active.Count == 0;

    public void Update(float dt)
    {
        while (_active.Count < _settings.ConcurrentPipes && _spawned < _settings.PipesPerScene)
        {
            _spawned++;
            if (TrySpawn() is { } p) _active.Add(p);
            else { _spawned = _settings.PipesPerScene; break; } // grid is full
        }

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var pipe = _active[i];
            pipe.Progress += dt * _settings.Speed;
            while (pipe.Alive && pipe.Progress >= 1f)
            {
                pipe.Progress -= 1f;
                Advance(pipe);
            }
            if (!pipe.Alive) _active.RemoveAt(i);
        }
    }

    /// <summary>Completed geometry plus the partially-grown head of each live pipe.</summary>
    public void Collect(List<PipeInstance> cylinders, List<PipeInstance> spheres)
    {
        cylinders.Clear();
        spheres.Clear();
        cylinders.AddRange(_cylinders);
        spheres.AddRange(_spheres);

        foreach (var p in _active)
        {
            var from = p.Position.ToVector();
            cylinders.Add(new PipeInstance { Start = from, Axis = p.Direction.ToVector() * p.Progress, Color = p.Color, Radius = PipeRadius });
            // A pipe-width cap on the head keeps the growing end rounded.
            spheres.Add(new PipeInstance { Start = from + p.Direction.ToVector() * p.Progress, Color = p.Color, Radius = PipeRadius });
        }
    }

    private Pipe? TrySpawn()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var pos = new Int3(_rng.Next(Size.X), _rng.Next(Size.Y), _rng.Next(Size.Z));
            if (IsOccupied(pos)) continue;

            var dirs = FreeDirections(pos, exclude: null);
            if (dirs.Count == 0) continue;

            var color = NextColor();
            _occupied[pos.X, pos.Y, pos.Z] = true;
            _spheres.Add(new PipeInstance { Start = pos.ToVector(), Color = color, Radius = ClassicJointRadius });

            var dir = dirs[_rng.Next(dirs.Count)];
            Reserve(pos + dir);
            // A length budget keeps pipes from wandering forever in big grids, so more colours get a turn.
            var budget = _rng.Next(30, 90);
            return new Pipe { Position = pos, Direction = dir, Color = color, Remaining = budget };
        }
        return null;
    }

    /// <summary>The head reached the next cell: commit the segment and choose where to go next.</summary>
    private void Advance(Pipe pipe)
    {
        _cylinders.Add(new PipeInstance
        {
            Start = pipe.Position.ToVector(), Axis = pipe.Direction.ToVector(), Color = pipe.Color, Radius = PipeRadius,
        });
        pipe.Position += pipe.Direction;

        if (--pipe.Remaining <= 0)
        {
            _spheres.Add(new PipeInstance { Start = pipe.Position.ToVector(), Color = pipe.Color, Radius = ClassicJointRadius });
            pipe.Alive = false;
            return;
        }

        var forward = pipe.Position + pipe.Direction;
        var mustTurn = !InBounds(forward) || IsOccupied(forward);
        var next = pipe.Direction;

        if (mustTurn || _rng.NextSingle() < TurnChance)
        {
            var options = FreeDirections(pipe.Position, exclude: pipe.Direction.Negate());
            options.Remove(pipe.Direction);
            if (options.Count > 0) next = options[_rng.Next(options.Count)];
            else if (mustTurn)
            {
                // Boxed in: finish with an end cap.
                _spheres.Add(new PipeInstance { Start = pipe.Position.ToVector(), Color = pipe.Color, Radius = ClassicJointRadius });
                pipe.Alive = false;
                return;
            }
        }

        if (next != pipe.Direction)
        {
            _spheres.Add(new PipeInstance { Start = pipe.Position.ToVector(), Color = pipe.Color, Radius = JointRadius() });
            pipe.Direction = next;
        }
        Reserve(pipe.Position + pipe.Direction);
    }

    private float JointRadius() => _settings.Joints switch
    {
        JointStyle.Smooth => PipeRadius,
        JointStyle.Mixed => _rng.Next(2) == 0 ? PipeRadius : ClassicJointRadius,
        _ => ClassicJointRadius,
    };

    private List<Int3> FreeDirections(Int3 pos, Int3? exclude)
    {
        var list = new List<Int3>(6);
        foreach (var d in Directions)
        {
            if (exclude is { } ex && d == ex) continue;
            var n = pos + d;
            if (InBounds(n) && !IsOccupied(n)) list.Add(d);
        }
        return list;
    }

    private Vector3 NextColor()
    {
        int idx;
        do idx = _rng.Next(Palette.Length); while (idx == _lastColor);
        _lastColor = idx;
        var c = Palette[idx];
        return new Vector3(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));
    }

    private void Reserve(Int3 p) => _occupied[p.X, p.Y, p.Z] = true;
    private bool IsOccupied(Int3 p) => _occupied[p.X, p.Y, p.Z];
    private bool InBounds(Int3 p) => p.X >= 0 && p.Y >= 0 && p.Z >= 0 && p.X < Size.X && p.Y < Size.Y && p.Z < Size.Z;

    private sealed class Pipe
    {
        public Int3 Position;
        public Int3 Direction;
        public Vector3 Color;
        public float Progress;
        public int Remaining;
        public bool Alive = true;
    }
}

public readonly record struct Int3(int X, int Y, int Z)
{
    public static Int3 operator +(Int3 a, Int3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public Int3 Negate() => new(-X, -Y, -Z);
    public Vector3 ToVector() => new(X, Y, Z);
}
