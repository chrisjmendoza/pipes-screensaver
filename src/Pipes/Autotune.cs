using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pipes;

/// <summary>
/// How well a preset kept up. Two different questions, and conflating them reads badly: a preset can miss the
/// headroom target and still make every refresh comfortably (12.6 ms against a 60 Hz screen's 16.7 ms is 80 fps,
/// which is not "too slow" by any reading).
/// </summary>
internal enum AutotuneVerdict
{
    /// <summary>Within the target: it keeps up with room to spare, so busier moments have somewhere to go.</summary>
    Comfortable,
    /// <summary>Over the target but still inside the frame budget: it keeps up, with little spare.</summary>
    Tight,
    /// <summary>Over the frame budget: frames miss the refresh, which shows as stutter.</summary>
    TooSlow,
}

/// <summary>One preset's measurement in an <see cref="AutotuneReport"/>.</summary>
/// <param name="AvgMs">Average cost of a frame, in milliseconds.</param>
/// <param name="P90Ms">90th percentile: nine frames in ten took this long or less. What the verdict is judged on.</param>
/// <param name="Verdict">How <paramref name="P90Ms"/> compares to the report's target and budget.</param>
/// <param name="Variant">
/// Null for the ordinary rows, measured with the user's own pipes. "all metal" for the worst-case row: the same
/// preset measured on scenes with the Metallic finish, the dearest pipes for traced reflections.
/// </param>
internal sealed record AutotuneResult(QualityPreset Preset, double AvgMs, double P90Ms, AutotuneVerdict Verdict, string? Variant = null);

/// <summary>
/// What /autotune found, written as JSON for the settings dialog to read back. <see cref="Results"/> holds the
/// presets tried, lightest first; it stops after the first that didn't fit, since heavier ones won't either. A
/// worst-case row (<see cref="AutotuneResult.Variant"/>) may follow. <see cref="TestedWith"/> names the pipes the
/// ordinary rows were measured with, in words, since a different finish or camera costs differently.
/// </summary>
internal sealed record AutotuneReport(
    int RefreshHz,
    double BudgetMs,
    double TargetMs,
    int Width,
    int Height,
    int ViewCount,
    string Renderer,
    CameraMotion Camera,
    string TestedWith,
    double SimulatedSeconds,
    List<AutotuneResult> Results,
    QualityPreset Recommended);

/// <summary>
/// The "Test this PC" check: which quality preset this machine can run at its monitor's refresh rate.
/// </summary>
/// <remarks>
/// <para>
/// The measuring itself is <see cref="GLHost.Autotune"/>, run as <c>Pipes.exe /autotune result.json</c>. This class
/// holds the rules it measures against, the report format, and the dialog's side: <see cref="RunAsync"/> starts
/// that command in a separate process and reads its report.
/// </para>
/// <para>
/// <b>Why a separate process.</b> The test renders flat out, as fast as the card will go, and that's exactly the
/// situation where a graphics driver is most likely to fall over (docs/ROADMAP.md, "Known issues", has a real
/// NVIDIA crash that only happens in flat-out rendering). A driver crash kills the whole process it's in. In a
/// child process it just means a failed test and a message; in the dialog's own process it would take the dialog,
/// and any unsaved changes in it, down with it.
/// </para>
/// </remarks>
internal static class Autotune
{
    /// <summary>
    /// A preset is <see cref="AutotuneVerdict.Comfortable"/> if nine frames in ten take at most this share of the
    /// frame budget (1000 ms ÷ refresh rate). The rest is headroom: the test sees a short, typical stretch, and the
    /// real thing has heavier moments (a denser patch of tunnel, all-metal scenes with traced reflections), a card
    /// that slows down as it heats up, and other programs using the GPU. A frame that misses the refresh shows as a
    /// visible stutter, so it's better to recommend one level too low than one too high.
    /// </summary>
    /// <remarks>
    /// This is the bar for <em>recommending</em> a preset, not for calling one too slow. Missing it means less spare
    /// than we'd like, not that the screen can't keep up: only <see cref="AutotuneVerdict.TooSlow"/> (past the whole
    /// budget) actually drops frames. Reporting the two as one verdict told people running at 80 fps that their
    /// machine couldn't cope.
    /// </remarks>
    public const double TargetShareOfBudget = 0.75;

    /// <summary>
    /// Which verdict a measured 90th-percentile frame time earns against the frame budget and the headroom target.
    /// </summary>
    public static AutotuneVerdict Judge(double p90Ms, double targetMs, double budgetMs) =>
        p90Ms <= targetMs ? AutotuneVerdict.Comfortable
        : p90Ms <= budgetMs ? AutotuneVerdict.Tight
        : AutotuneVerdict.TooSlow;

    /// <summary>
    /// Which frame time decides: the 90th percentile rather than the average, so a preset that's usually fast but
    /// regularly spikes (a stutter every few frames) doesn't pass on its average.
    /// </summary>
    public const double JudgedPercentile = 0.90;

    /// <summary>
    /// Seconds of untimed rendering before the first preset. An idle GPU runs at a low clock and takes a while to
    /// speed up; timed too early, the same settings measured anywhere from 2.5 to 13 ms a frame (see
    /// <see cref="GLHost.Benchmark"/>). Also covers the first shader compiles and driver caches.
    /// </summary>
    public const double FirstWarmUpSeconds = 1.5;

    /// <summary>
    /// Seconds of untimed rendering before each later preset: the GPU is already at speed, but a new preset
    /// compiles new shaders and allocates new buffers, and the first frames with them are slow.
    /// </summary>
    public const double LaterWarmUpSeconds = 0.3;

    /// <summary>How long each preset is timed for, and the fewest frames that's allowed to be (for a slow machine).</summary>
    public const double MeasureSeconds = 1.0;
    public const int MinMeasuredFrames = 45;

    /// <summary>
    /// Fly-through is measured in flight, which is much busier than the box being built: the test simulates until
    /// every view's camera has taken off, then this much longer so the tunnel has filled in ahead of it.
    /// </summary>
    public const float SecondsAfterTakeOff = 5f;

    /// <summary>Simulated seconds of scene before measuring, for the other camera modes (as /bench does).</summary>
    public const float SceneSeconds = 12f;

    /// <summary>Give up waiting for take-off after this much simulated time (a huge scene with slow growth).</summary>
    public const float MaxSimulatedSeconds = 120f;

    /// <summary>How long the dialog waits for the test before killing it. A normal run takes 10-25 seconds.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>Enum names rather than numbers in the JSON, and camelCase property names.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(AutotuneReport report, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));

    public static AutotuneReport? Read(string path) =>
        JsonSerializer.Deserialize<AutotuneReport>(File.ReadAllText(path), JsonOptions);

    /// <summary>
    /// The frame time at <paramref name="percentile"/> (0..1) of <paramref name="frameTimes"/>: the smallest time
    /// that at least that share of frames came in under. Nearest-rank, so it's always a real measured frame.
    /// </summary>
    public static double Percentile(List<double> frameTimes, double percentile)
    {
        var sorted = frameTimes.Order().ToList();
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>
    /// The test's graphics card is probably not the one the user thinks: an Intel integrated GPU (on a laptop with
    /// NVIDIA Optimus or AMD switchable graphics, Windows often gives a small program like this one the integrated
    /// chip), or Windows' software fallback, which has no GPU at all.
    /// </summary>
    public static bool LooksIntegratedOrSoftware(string renderer) =>
        renderer.Contains("Intel", StringComparison.OrdinalIgnoreCase)
        || renderer.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase)
        || renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Run <c>/autotune</c> in a child process with <paramref name="settings"/> (the dialog's current, unsaved state)
    /// and return its report. Throws <see cref="AutotuneFailedException"/> if the test crashed, timed out or wrote
    /// nothing usable, and <see cref="OperationCanceledException"/> if <paramref name="cancel"/> fired.
    /// </summary>
    /// <remarks>
    /// The settings reach the child through a temporary JSON file and the PIPES_SETTINGS environment variable, the
    /// same override the development commands use (<see cref="PipesSettings.FilePath"/>), so the user's real
    /// settings file is neither read nor written. Both temporary files are deleted afterwards, whatever happens.
    /// </remarks>
    public static async Task<AutotuneReport> RunAsync(PipesSettings settings, CancellationToken cancel)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"pipes-autotune-{Guid.NewGuid():N}");
        var settingsPath = stem + "-settings.json";
        var resultPath = stem + "-result.json";
        try
        {
            await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings), cancel);

            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, // needed for Environment and redirection
                CreateNoWindow = true,
                RedirectStandardError = true, // an unhandled exception's message ends up here
            };
            start.ArgumentList.Add("/autotune");
            start.ArgumentList.Add(resultPath);
            start.Environment["PIPES_SETTINGS"] = settingsPath;

            using var process = Process.Start(start) ?? throw new AutotuneFailedException("The test couldn't be started.");
            // Read stderr while waiting: if nobody drains the pipe and the child writes a lot to it, the child blocks.
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelled or timed out: either way the child is still rendering flat out, so stop it.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                cancel.ThrowIfCancellationRequested(); // the user's cancel
                throw new AutotuneFailedException($"The test didn't finish within {Timeout.TotalSeconds:F0} seconds.");
            }

            if (process.ExitCode != 0)
            {
                // 0xC0000409 and friends: exit codes from a crash are NTSTATUS values, which read best in hex.
                var detail = FirstLine(await errors);
                throw new AutotuneFailedException(
                    $"The test stopped with exit code 0x{process.ExitCode:X8}." + (detail is null ? "" : $"\n\n{detail}"));
            }

            try
            {
                return Read(resultPath) is { Results.Count: > 0 } report
                    ? report
                    : throw new AutotuneFailedException("The test finished but reported no results.");
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                throw new AutotuneFailedException("The test finished but its results couldn't be read.");
            }
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(resultPath);
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line
            ? line.Length > 300 ? line[..300] + "…" : line
            : null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The test couldn't produce a result. The message says why, in words for the user.</summary>
internal sealed class AutotuneFailedException(string message) : Exception(message);
