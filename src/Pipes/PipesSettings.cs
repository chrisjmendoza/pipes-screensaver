using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pipes;

public enum JointStyle
{
    /// <summary>Big ball at every bend, like the original.</summary>
    Classic,
    /// <summary>Curved elbows: the pipe bends along a quarter circle.</summary>
    Smooth,
    /// <summary>Random per bend.</summary>
    Mixed,
}

public enum Finish
{
    /// <summary>Glossy plastic, closest to the 90s look.</summary>
    Plastic,
    /// <summary>Polished metal with coloured reflections.</summary>
    Metallic,
    /// <summary>Each pipe gets its own finish: glossy or satin plastic, polished or brushed metal.</summary>
    Mixed,
}

public enum CameraMotion
{
    /// <summary>Fixed viewpoint for the whole scene.</summary>
    Still,
    /// <summary>Slow orbit around the scene.</summary>
    Orbit,
    /// <summary>Orbit plus a gentle bob, sway and push in/out, like a camera on a slow drone.</summary>
    Float,
    /// <summary>Build a scene, then fly into it and on through an endless tunnel of pipes growing ahead.</summary>
    FlyThrough,
}

public enum GraphicsStyle
{
    /// <summary>HDR lighting, per-pixel shading and the optional effects (AO, bloom, depth of field).</summary>
    Modern,
    /// <summary>
    /// "Lite": renders like the 90s original. Per-vertex lighting on low-poly meshes, black background, no effects.
    /// Far cheaper to run.
    /// </summary>
    Classic,
}

/// <summary>
/// User settings, saved as JSON. Every property has a default, so a missing or older file still loads, and
/// <see cref="Clamped"/> keeps hand-edited values in range.
/// </summary>
public sealed class PipesSettings
{
    // ---- Animation ----

    /// <summary>Pipes growing at the same time.</summary>
    public int ConcurrentPipes { get; set; } = 3;

    /// <summary>Pipes drawn before the scene fades and restarts.</summary>
    public int PipesPerScene { get; set; } = 18;

    /// <summary>Grid cells per second, per pipe.</summary>
    public float Speed { get; set; } = 9f;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CameraMotion Camera { get; set; } = CameraMotion.Float;

    /// <summary>
    /// With several monitors, give each its own scene (framed for its shape) instead of one scene stretched across
    /// the whole desktop. Also skips rendering the parts of the desktop no monitor shows.
    /// </summary>
    public bool SeparateMonitors { get; set; } = true;

    /// <summary>Fly-through only: how fast the camera flies, in grid cells per second.</summary>
    public float FlightSpeed { get; set; } = 5f;

    /// <summary>
    /// Fly-through only: how busy the tunnel's course is, from 1 (zen: long straights and lazy curves) to 10 (wild:
    /// maneuver after maneuver, lots of corkscrews). 5 is balanced.
    /// </summary>
    public int CourseComplexity { get; set; } = 5;

    /// <summary>
    /// Fly-through only: how full the tunnel walls are, in tenths (6 = 60%). This is what sets how many pipes there
    /// are to draw in flight, so turning it down is the way to make flying cheaper for a slower graphics card.
    /// (<see cref="ConcurrentPipes"/> and <see cref="PipesPerScene"/> are about building the box scene.)
    /// </summary>
    public int TunnelDensity { get; set; } = 6;

    /// <summary>
    /// Fly-through only: the "pilot" speeds up and slows down around <see cref="FlightSpeed"/>: drifting, easing off
    /// into tight turns and corkscrews, opening up on long straights.
    /// </summary>
    public bool VaryFlightSpeed { get; set; } = true;

    // ---- Pipes ----

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JointStyle Joints { get; set; } = JointStyle.Classic;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Finish Finish { get; set; } = Finish.Plastic;

    /// <summary>Give each pipe its own thickness instead of all the same.</summary>
    public bool VaryThickness { get; set; } = true;

    /// <summary>Occasional valves, couplings, flanges, and tee junctions that branch into a new pipe.</summary>
    public bool Fittings { get; set; } = true;

    /// <summary>The original's easter egg: very rarely, a joint is a teapot.</summary>
    public bool Teapots { get; set; } = true;

    // ---- Graphics ----

    /// <summary>Modern or classic (lite) rendering. Classic ignores the effect switches below.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GraphicsStyle Style { get; set; } = GraphicsStyle.Modern;

    /// <summary>MSAA samples (0, 2, 4, 8).</summary>
    public int Antialiasing { get; set; } = 4;

    /// <summary>Screen-space ambient occlusion: soft contact shadows where pipes are close together.</summary>
    public bool AmbientOcclusion { get; set; } = true;

    /// <summary>A soft glow around bright highlights.</summary>
    public bool Bloom { get; set; } = true;

    /// <summary>Blur pipes that are nearer or farther than the middle of the scene, like a camera lens.</summary>
    public bool DepthOfField { get; set; } = false;

    /// <summary>The main light casts shadows: pipes shade the pipes behind them.</summary>
    public bool Shadows { get; set; } = true;

    /// <summary>
    /// Settings files written before <see cref="Camera"/> existed had a <c>"CameraDrift": true/false</c> switch.
    /// This reads it (so "off" stays off) and is never written back out.
    /// </summary>
    [JsonPropertyName("CameraDrift")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyCameraDrift
    {
        get => null;
        set { if (value == false) Camera = CameraMotion.Still; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Normally %LOCALAPPDATA%\PipesScreensaver\settings.json. For development, the PIPES_SETTINGS environment
    /// variable can point somewhere else, so test renders don't touch your real settings.
    /// </summary>
    public static string FilePath { get; } = Environment.GetEnvironmentVariable("PIPES_SETTINGS") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipesScreensaver", "settings.json");

    public static PipesSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<PipesSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new()).Clamped();
        }
        catch (JsonException) { /* fall back to defaults */ }
        catch (IOException) { }
        return new PipesSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Clamped(), JsonOptions));
    }

    public const int MaxConcurrentPipes = 20;
    public const int MaxPipesPerScene = 200;
    public const int MaxSpeed = 80;
    public const int MaxFlightSpeed = 20;
    public const int MaxCourseComplexity = 10;
    public const int MaxTunnelDensity = 10;

    public PipesSettings Clamped()
    {
        ConcurrentPipes = Math.Clamp(ConcurrentPipes, 1, MaxConcurrentPipes);
        PipesPerScene = Math.Clamp(PipesPerScene, ConcurrentPipes, MaxPipesPerScene);
        Speed = Math.Clamp(Speed, 1f, MaxSpeed);
        FlightSpeed = Math.Clamp(FlightSpeed, 1f, MaxFlightSpeed);
        CourseComplexity = Math.Clamp(CourseComplexity, 1, MaxCourseComplexity);
        TunnelDensity = Math.Clamp(TunnelDensity, 1, MaxTunnelDensity);
        Antialiasing = Antialiasing switch { <= 0 => 0, <= 2 => 2, <= 4 => 4, _ => 8 };
        return this;
    }
}
