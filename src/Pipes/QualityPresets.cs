using System.Diagnostics;
using System.Text.Json;

namespace Pipes;

/// <summary>
/// One-click graphics quality levels, lightest first. The order matters: the "Test this PC" check (/autotune) tries
/// them in this order and stops at the first one that's too slow, which only makes sense if each costs more than
/// the one before.
/// </summary>
public enum QualityPreset
{
    Lite,
    Low,
    Medium,
    High,
    Ultra,
}

/// <summary>
/// What each <see cref="QualityPreset"/> sets. A preset only touches the settings that decide how much work the
/// graphics card does per frame: <see cref="PipesSettings.Style"/>, <see cref="PipesSettings.Antialiasing"/>,
/// <see cref="PipesSettings.SurfaceDetail"/>, <see cref="PipesSettings.Shadows"/>,
/// <see cref="PipesSettings.AmbientOcclusion"/>, <see cref="PipesSettings.Bloom"/> and
/// <see cref="PipesSettings.TracedReflections"/>. It never changes what the pipes are or how they move (finish,
/// joints, camera, depth of field, moving light, tunnel density...): those are taste, not speed, and choosing
/// "Low" shouldn't turn your fly-through back into an orbit.
/// </summary>
/// <remarks>
/// <para>
/// Costs, from <c>/bench</c> on an RTX 3080 at 1920×1080 (docs/RENDERING.md and CHANGELOG.md have the details; a
/// weaker card pays the same shares of a bigger number):
/// </para>
/// <list type="bullet">
/// <item>Classic style: about a tenth of the modern style (0.35 against 1.5 ms a frame in a box scene).</item>
/// <item>MSAA: every extra sample is more memory to fill and resolve; 8× across three monitors is where the modern
/// style stopped holding 60 fps.</item>
/// <item>Surface detail: +0.4-0.7 ms in flight (noise functions per pixel, on every layer of the tunnel).</item>
/// <item>Shadows: +0.2-0.45 ms (a shadow map drawn every frame).</item>
/// <item>Ambient occlusion: a second, depth-and-normals drawing of every piece plus two full-screen passes.</item>
/// <item>Bloom: cheap (its chain starts at half size and shrinks), and a large part of the modern look, so every
/// modern preset keeps it.</item>
/// <item>Traced reflections: +0.7 ms in a box scene, +1.1-1.3 ms in the usual flight, up to +3.8 ms in an all-metal
/// tunnel. The single most expensive switch.</item>
/// </list>
/// </remarks>
internal static class QualityPresets
{
    /// <summary>
    /// High must stay equal to the defaults in <see cref="PipesSettings"/>: a fresh install (or "Reset to defaults")
    /// should read "High" in the dialog, not "Custom". Checked here in Debug builds, so changing a default without
    /// changing High (or the other way round) trips it the first time the class is used.
    /// </summary>
    static QualityPresets() =>
        Debug.Assert(Match(new PipesSettings()) == QualityPreset.High, "QualityPreset.High must equal the default settings.");

    /// <summary>Set the graphics settings for <paramref name="preset"/> on <paramref name="settings"/>.</summary>
    public static void Apply(QualityPreset preset, PipesSettings settings)
    {
        switch (preset)
        {
            case QualityPreset.Lite:
                // The 90s look: one pass, per-vertex lighting on low-poly meshes, no effects. The effect switches are
                // left as they are, since the classic style ignores them, so going back to Modern by hand finds them
                // as the user left them. 4× MSAA costs little here (an 8-bit buffer, no HDR) and the thin pipes
                // shimmer without it.
                settings.Style = GraphicsStyle.Classic;
                settings.Antialiasing = 4;
                break;
            case QualityPreset.Low:
                // The modern look at its cheapest: HDR lighting and bloom, but flat colours (no procedural
                // surfaces), no shadow map, no AO, and 2× MSAA, which keeps edges smooth enough at half the samples.
                SetModern(settings, samples: 2, surfaces: false, shadows: false, ao: false, traced: false);
                break;
            case QualityPreset.Medium:
                // Surfaces and shadows, which change the look the most. Drops AO: of the remaining effects it costs
                // the most for the least visible difference (soft darkening where pipes cross).
                SetModern(settings, samples: 4, surfaces: true, shadows: true, ao: false, traced: false);
                break;
            case QualityPreset.High:
                // Everything but traced reflections. These are the defaults (see the static constructor).
                SetModern(settings, samples: 4, surfaces: true, shadows: true, ao: true, traced: false);
                break;
            case QualityPreset.Ultra:
                // Traced reflections and 8× MSAA on top. For a fast card, or a single 60 Hz screen.
                SetModern(settings, samples: 8, surfaces: true, shadows: true, ao: true, traced: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
        }
    }

    /// <summary>
    /// A copy of <paramref name="settings"/> with <paramref name="preset"/> applied, leaving the original alone.
    /// The copy goes through JSON, the same way the settings are saved and loaded, so it can't miss a property.
    /// </summary>
    public static PipesSettings With(QualityPreset preset, PipesSettings settings)
    {
        var copy = JsonSerializer.Deserialize<PipesSettings>(JsonSerializer.Serialize(settings))!;
        Apply(preset, copy);
        return copy;
    }

    /// <summary>
    /// <paramref name="settings"/> held down to at most <paramref name="cap"/>: the same settings back if they are
    /// already that light, otherwise a copy with <paramref name="cap"/> applied. Used for
    /// <see cref="PipesSettings.BatteryQuality"/>.
    /// </summary>
    /// <remarks>
    /// The presets are declared lightest first, so comparing them as numbers is comparing cost. Settings that match
    /// no preset ("Custom") can't be ranked against the cap, so they get it applied: a custom mix can be arbitrarily
    /// expensive, and on battery the safe reading of "at most Low" is to mean it.
    /// </remarks>
    public static PipesSettings LimitedTo(QualityPreset cap, PipesSettings settings) =>
        Match(settings) is { } current && current <= cap ? settings : With(cap, settings);

    /// <summary>
    /// Which preset <paramref name="settings"/> amounts to, or null if none ("Custom"). Any classic-style settings
    /// count as Lite, whatever their anti-aliasing: Lite is "the classic style", and the dialog shouldn't say Custom
    /// just because someone picked 2× for it. A modern preset must match every setting it sets.
    /// </summary>
    public static QualityPreset? Match(PipesSettings settings)
    {
        if (settings.Style == GraphicsStyle.Classic) return QualityPreset.Lite;
        foreach (var preset in Enum.GetValues<QualityPreset>())
        {
            if (preset == QualityPreset.Lite) continue;
            var expected = new PipesSettings();
            Apply(preset, expected);
            if (settings.Antialiasing == expected.Antialiasing
                && settings.SurfaceDetail == expected.SurfaceDetail
                && settings.Shadows == expected.Shadows
                && settings.AmbientOcclusion == expected.AmbientOcclusion
                && settings.Bloom == expected.Bloom
                && settings.TracedReflections == expected.TracedReflections)
                return preset;
        }
        return null;
    }

    private static void SetModern(PipesSettings s, int samples, bool surfaces, bool shadows, bool ao, bool traced)
    {
        s.Style = GraphicsStyle.Modern;
        s.Antialiasing = samples;
        s.SurfaceDetail = surfaces;
        s.Shadows = shadows;
        s.AmbientOcclusion = ao;
        s.Bloom = true;
        s.TracedReflections = traced;
    }
}
