using System.Globalization;
using Pipes.Native;

namespace Pipes;

/// <summary>
/// Screensaver entry point. Windows calls a .scr with:
///   /s            run fullscreen
///   /p &lt;hwnd&gt;    draw into the preview monitor in Screen Saver Settings
///   /c[:hwnd]     show settings (also used when the .scr is double-clicked with no args)
/// Development extras:
///   /w                          run in a normal window (Esc to quit)
///   /shot &lt;file.png&gt; [seconds] [width] [height] [seed]   render one frame offscreen and exit
///   /bench &lt;report.txt&gt; [frames] [width] [height]         time rendering offscreen, write ms/frame
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must happen before any window exists, or Windows bitmap-scales us on high-DPI screens.
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var (command, argument) = Parse(args);
        var settings = PipesSettings.Load();

        try
        {
            switch (command)
            {
                case "s":
                    Run(HostMode.Fullscreen, IntPtr.Zero, settings);
                    break;
                case "p" when argument is { } hwnd:
                    Run(HostMode.Preview, new IntPtr(long.Parse(hwnd, CultureInfo.InvariantCulture)), settings);
                    break;
                case "w":
                    Run(HostMode.Windowed, IntPtr.Zero, settings);
                    break;
                case "shot":
                    Screenshot(args, settings);
                    break;
                case "bench":
                    Benchmark(args, settings);
                    break;
                default: // "c", no arguments, or anything unrecognised
                    ApplicationConfiguration.Initialize();
                    Application.Run(new ConfigForm(settings));
                    break;
            }
            return 0;
        }
        catch (Exception ex) when (command is not ("shot" or "bench"))
        {
            // A screensaver has no console; surface failures instead of silently showing nothing.
            MessageBox.Show(ex.Message, "Pipes screensaver", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Run(HostMode mode, IntPtr parent, PipesSettings settings)
    {
        using var host = new GLHost(mode, parent);
        host.Run(settings);
    }

    private static void Screenshot(string[] args, PipesSettings settings)
    {
        string At(int i, string fallback) => args.Length > i ? args[i] : fallback;
        var path = Path.GetFullPath(At(1, "pipes.png"));
        var seconds = float.Parse(At(2, "20"), CultureInfo.InvariantCulture);
        var width = int.Parse(At(3, "1920"), CultureInfo.InvariantCulture);
        var height = int.Parse(At(4, "1080"), CultureInfo.InvariantCulture);
        var seed = int.Parse(At(5, "1"), CultureInfo.InvariantCulture);

        using var host = GLHost.CreateOffscreen();
        host.Screenshot(settings, width, height, seconds, path, seed);
    }

    private static void Benchmark(string[] args, PipesSettings settings)
    {
        string At(int i, string fallback) => args.Length > i ? args[i] : fallback;
        var path = Path.GetFullPath(At(1, "bench.txt"));
        var frames = int.Parse(At(2, "300"), CultureInfo.InvariantCulture);
        var width = int.Parse(At(3, "1920"), CultureInfo.InvariantCulture);
        var height = int.Parse(At(4, "1080"), CultureInfo.InvariantCulture);

        using var host = GLHost.CreateOffscreen();
        host.Benchmark(settings, width, height, frames, path);
    }

    /// <summary>Accepts "/s", "-S", "/p 1234", "/p:1234", "/c:1234".</summary>
    private static (string Command, string? Argument) Parse(string[] args)
    {
        if (args.Length == 0) return ("c", null);

        var first = args[0].TrimStart('/', '-').ToLowerInvariant();
        string? argument = null;
        var colon = first.IndexOf(':');
        if (colon >= 0)
        {
            argument = first[(colon + 1)..];
            first = first[..colon];
        }
        else if (args.Length > 1)
        {
            argument = args[1];
        }
        return (first, argument);
    }
}
