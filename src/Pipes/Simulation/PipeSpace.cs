using System.Numerics;

namespace Pipes.Simulation;

/// <summary>
/// The region pipes are allowed to grow in. <see cref="PipeWorld"/> knows the rules for how pipes grow; a space
/// knows <em>where</em>. Keeping them apart is what lets the same growth rules fill a box (the classic scene) or an
/// endless tunnel (fly-through mode).
/// </summary>
public interface IPipeSpace
{
    /// <summary>May a pipe occupy this cell? (Whether another pipe already does is <see cref="PipeWorld"/>'s business.)</summary>
    bool Contains(Int3 cell);

    /// <summary>A cell worth trying to start a new pipe in. May return one that turns out to be unusable.</summary>
    Int3 SpawnCandidate(Random rng);

    /// <summary>
    /// If pipes around <paramref name="cell"/> should tend to run in one direction, that direction (a unit grid
    /// step). Null for no preference, which gives the classic random look.
    /// </summary>
    Int3? Flow(Int3 cell);

    /// <summary>
    /// True if the space has fixed edges and can therefore fill up. An endless space never "fills": if a spawn
    /// attempt fails, it just tries again next frame.
    /// </summary>
    bool IsBounded { get; }
}

/// <summary>The classic scene: a box of cells from (0,0,0) to <see cref="Size"/> (exclusive).</summary>
public sealed class BoxSpace(Int3 size) : IPipeSpace
{
    public Int3 Size { get; } = size;

    /// <summary>The middle of the box, in world units (cell centres are at whole numbers).</summary>
    public Vector3 Center => new Vector3(Size.X - 1, Size.Y - 1, Size.Z - 1) * 0.5f;

    public bool IsBounded => true;

    public bool Contains(Int3 c) => c.X >= 0 && c.Y >= 0 && c.Z >= 0 && c.X < Size.X && c.Y < Size.Y && c.Z < Size.Z;

    public Int3 SpawnCandidate(Random rng) => new(rng.Next(Size.X), rng.Next(Size.Y), rng.Next(Size.Z));

    public Int3? Flow(Int3 cell) => null;
}
