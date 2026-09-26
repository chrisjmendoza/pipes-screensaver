using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Pipes.Diagnostics;

/// <summary>Everything the stats panel shows, captured on the render thread and handed to <see cref="StatsPanel.Paint"/>.</summary>
internal sealed record PanelData(
    FrameStats.Summary Frames,
    int RefreshHz,
    double GraphScaleMs,
    IReadOnlyList<(string Label, double Ms)> Passes,
    SystemSnapshot System,
    string Renderer,
    string Settings);

/// <summary>
/// Paints the stats overlay's panel (background, text, bars; everything except the live frame graph) into a bitmap
/// with GDI+, the same 2D library WinForms draws with. <see cref="Rendering.StatsOverlay"/> uploads the bitmap as a
/// texture and draws the frame graph over the hole left for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why GDI+ and not text in OpenGL.</b> OpenGL has no text at all: a game would bake a font into a texture atlas
/// and lay glyphs out itself. GDI+ already does good anti-aliased text, rounded shapes and measuring, and the panel
/// only changes four times a second, so drawing it as a picture and uploading that is far simpler.
/// </para>
/// <para>
/// <b>Off the render thread.</b> Painting takes a few milliseconds, which on the render thread would put a spike in
/// the frame graph four times a second, measuring the overlay instead of the pipes. So it's painted on a thread-pool
/// thread from a <see cref="PanelData"/> snapshot, and the render thread only uploads the finished bitmap.
/// </para>
/// <para>
/// All coordinates here are in "logical" pixels, as at 100% display scaling; one <c>ScaleTransform</c> scales the
/// whole panel for the monitor's scaling, so it's as legible on a 4K laptop at 250% as on a 1080p desktop.
/// </para>
/// </remarks>
internal sealed class StatsPanel : IDisposable
{
    // ---- Layout (logical pixels) -----------------------------------------------------------------------------------

    public const int Width = 300;
    /// <summary>Tallest the panel can get; the bitmap is this big, and only the used part is drawn.</summary>
    public const int MaxHeight = 560;
    private const int Pad = 14;
    private const int Inner = Width - 2 * Pad;

    /// <summary>Where the live frame graph goes, drawn by the GPU on top of the panel.</summary>
    public static readonly RectangleF GraphRect = new(Pad, 90, Inner, 68);
    public const float GraphCorner = 6f;

    // ---- Palette -------------------------------------------------------------------------------------------------
    // A dark glass panel: near-black, slightly blue, and nearly opaque. Any more see-through and a bright pipe
    // behind it shows as a coloured blob in the middle of the numbers.

    private static readonly Color PanelFill = Color.FromArgb(242, 12, 15, 21);
    private static readonly Color PanelEdge = Color.FromArgb(34, 255, 255, 255);
    private static readonly Color Well = Color.FromArgb(12, 255, 255, 255); // graph and bar backgrounds
    private static readonly Color Rule = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color Text = Color.FromArgb(236, 240, 245);
    private static readonly Color Muted = Color.FromArgb(150, 160, 175);
    private static readonly Color Dim = Color.FromArgb(98, 108, 122);

    /// <summary>Frame-time status colours: holding the refresh rate, slipping, and well behind.</summary>
    public static readonly Color Good = Color.FromArgb(74, 222, 128), Warn = Color.FromArgb(251, 191, 36), Bad = Color.FromArgb(248, 113, 113);

    /// <summary>The graph's GPU and CPU lines (and their legend here).</summary>
    public static readonly Color GpuLine = Color.FromArgb(129, 140, 248), CpuLine = Color.FromArgb(251, 146, 60);

    /// <summary>One colour per render pass, for the stacked bar and its legend.</summary>
    private static Color PassColor(string label) => label switch
    {
        "Shadows" => Color.FromArgb(167, 139, 250),
        "Prepass" => Color.FromArgb(244, 114, 182),
        "AO" => Color.FromArgb(45, 212, 191),
        "Scene" => Color.FromArgb(96, 165, 250),
        "DoF" => Color.FromArgb(251, 146, 60),
        "Bloom" => Color.FromArgb(253, 224, 71),
        "Post" => Color.FromArgb(148, 163, 184),
        _ => Color.FromArgb(100, 116, 139), // the overlay itself, anything new
    };

    /// <summary>
    /// How a frame time compares with the refresh interval: within 10% is holding the rate (timing noise), up to
    /// 60% over is dropping some frames, beyond that is dropping lots.
    /// </summary>
    public static Color Status(double ms, double budgetMs) => ms <= budgetMs * 1.1 ? Good : ms <= budgetMs * 1.6 ? Warn : Bad;

    // ---- Resources -----------------------------------------------------------------------------------------------

    private readonly float _scale;
    private readonly Font _big, _medium, _value, _small, _label, _tiny;
    private readonly StringFormat _format;

    public StatsPanel(float scale)
    {
        _scale = scale;
        // Sizes in pixels, so they scale with the transform rather than with Windows' own DPI handling. Segoe UI's
        // digits are all the same width, so numbers don't jiggle as they change.
        _big = new Font("Segoe UI Semibold", 34f, FontStyle.Regular, GraphicsUnit.Pixel);
        _medium = new Font("Segoe UI Semibold", 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        _value = new Font("Segoe UI Semibold", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _small = new Font("Segoe UI", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _label = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _tiny = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        // "Typographic" drops the padding GDI+ normally adds around text, so measured widths are the ink's width
        // and right-aligned numbers line up exactly.
        _format = (StringFormat)StringFormat.GenericTypographic.Clone();
        _format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
    }

    public int PixelWidth => (int)MathF.Ceiling(Width * _scale);
    public int PixelHeight => (int)MathF.Ceiling(MaxHeight * _scale);

    /// <summary>Paint the panel into <paramref name="bitmap"/> (premultiplied ARGB, <see cref="PixelWidth"/> x
    /// <see cref="PixelHeight"/>). Returns the used height in logical pixels.</summary>
    public int Paint(Bitmap bitmap, PanelData d)
    {
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Greyscale anti-aliasing: ClearType's coloured fringes assume an opaque background of a known colour.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(_scale, _scale);

        var f = d.Frames;
        var budget = 1000.0 / d.RefreshHz;
        var status = f.Frames == 0 ? Muted : Status(f.FrameMs, budget);

        // Measure the content first (it varies: hardware rows, a wrapped warning), then paint the panel behind it.
        var hardware = HardwareRows(d);
        var warning = Warning(d);
        var footerTop = 342 + (hardware.Count + 1) / 2 * 17 + (hardware.Count > 0 ? 12 : 0);
        var settingsHeight = Measure(g, d.Settings, _tiny, Inner).Height;
        var warningHeight = warning is null ? 0 : Measure(g, warning, _tiny, Inner - 12).Height + 14;
        var height = (int)MathF.Ceiling(footerTop + 18 + settingsHeight + warningHeight + 12);

        using (var panel = RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, height - 1), 10f))
        {
            using var fill = new SolidBrush(PanelFill);
            using var edge = new Pen(PanelEdge, 1f);
            g.FillPath(fill, panel);
            g.DrawPath(edge, panel);
        }

        // Header: a status dot, the title, and the refresh rate frames are paced to.
        Dot(g, Pad + 3.5f, 19f, 3.5f, status);
        DrawText(g, "PERFORMANCE", _label, Muted, Pad + 12, 13);
        DrawText(g, $"{d.RefreshHz} Hz · VSync", _label, Muted, Width - Pad, 13, right: true);

        // The headline: frames per second, big, in the status colour; frame time beside it.
        var fps = f.Frames == 0 ? "–" : f.Fps.ToString("0");
        var fpsWidth = DrawText(g, fps, _big, status, Pad - 2, 28);
        DrawText(g, "FPS", _label, Muted, Pad - 2 + fpsWidth + 5, 55);
        DrawText(g, f.Frames == 0 ? "–" : $"{f.FrameMs:0.00} ms", _medium, Text, Width - Pad, 34, right: true);
        DrawText(g, "frame time", _tiny, Dim, Width - Pad, 55, right: true);

        // Frame graph: a well for the GPU to draw into, with a legend above and scale labels inside.
        DrawText(g, "FRAME TIMES", _label, Dim, Pad, 75);
        float legendX = Width - Pad;
        legendX = Legend(g, "CPU", CpuLine, legendX);
        legendX = Legend(g, "GPU", GpuLine, legendX - 10);
        Legend(g, "frame", status == Muted ? Good : status, legendX - 10);
        using (var well = RoundedRect(GraphRect, GraphCorner))
        using (var wellBrush = new SolidBrush(Well))
            g.FillPath(wellBrush, well);
        DrawText(g, $"{d.GraphScaleMs:0.#} ms", _tiny, Dim, GraphRect.Left + 5, GraphRect.Top + 3);
        var budgetY = GraphRect.Bottom - (float)(budget / d.GraphScaleMs) * GraphRect.Height;
        DrawText(g, $"{budget:0.0} ms", _tiny, Muted, GraphRect.Right - 5, budgetY - 15, right: true);

        // Averages and lows over the last ten seconds.
        // The lows are coloured like the headline: a 1% low in green means even the slowest frames made the refresh.
        Color Rated(double ms) => f.Frames == 0 ? Text : Status(ms, budget);
        var columns = new (string Label, string Value, Color Colour)[]
        {
            ("AVG FPS", f.Frames == 0 ? "–" : f.AverageFps.ToString("0"), Text),
            ("1% LOW", f.Frames == 0 ? "–" : f.Low1Fps.ToString("0"), Rated(1000 / f.Low1Fps)),
            ("0.1% LOW", f.Frames == 0 ? "–" : f.Low01Fps.ToString("0"), Rated(1000 / f.Low01Fps)),
            ("WORST", f.Frames == 0 ? "–" : $"{f.WorstMs:0.0} ms", Rated(f.WorstMs)),
        };
        for (var i = 0; i < columns.Length; i++)
        {
            var x = Pad + i * (Inner / 4f);
            DrawText(g, columns[i].Label, _label, Dim, x, 168);
            DrawText(g, columns[i].Value, _value, columns[i].Colour, x, 181);
        }

        HorizontalRule(g, 208);

        // Where the time goes: GPU and CPU per frame, as bars against the refresh interval.
        LoadBar(g, "GPU", f.GpuMs, budget, 218);
        LoadBar(g, "CPU", f.Frames == 0 ? null : f.CpuMs, budget, 240);

        // The GPU time split by render pass: a stacked bar and a legend.
        DrawText(g, "GPU PASSES", _label, Dim, Pad, 264);
        var passTotal = d.Passes.Sum(p => p.Ms);
        var stack = new RectangleF(Pad, 280, Inner, 7);
        using (var track = RoundedRect(stack, 3.5f))
        {
            using var trackBrush = new SolidBrush(Well);
            g.FillPath(trackBrush, track);
            if (passTotal > 0)
            {
                var clip = g.Clip;
                g.SetClip(track);
                var x = stack.Left;
                foreach (var (label, ms) in d.Passes)
                {
                    var w = (float)(ms / passTotal) * stack.Width;
                    using var brush = new SolidBrush(PassColor(label));
                    // A hairline gap between segments, so neighbouring colours stay distinct.
                    g.FillRectangle(brush, x, stack.Top, Math.Max(0f, w - 1f), stack.Height);
                    x += w;
                }
                g.Clip = clip;
            }
        }
        for (var i = 0; i < d.Passes.Count && i < 9; i++)
        {
            var (label, ms) = d.Passes[i];
            var x = Pad + i % 3 * (Inner / 3f);
            var y = 294f + i / 3 * 15;
            using (var brush = new SolidBrush(PassColor(label)))
                g.FillRectangle(brush, x, y + 4, 6, 6);
            var labelWidth = DrawText(g, label, _tiny, Muted, x + 10, y);
            DrawText(g, ms.ToString("0.00"), _tiny, Text, x + 10 + labelWidth + 4, y);
        }
        if (d.Passes.Count == 0) DrawText(g, "waiting for the GPU…", _tiny, Dim, Pad, 294);

        // Hardware: whatever this machine can report, two to a row.
        if (hardware.Count > 0)
        {
            HorizontalRule(g, 342);
            for (var i = 0; i < hardware.Count; i++)
            {
                // Two columns with a gap between; each value right-aligned in its column, so the numbers line up
                // down the column whatever the length of their labels.
                const float gap = 18;
                var columnWidth = (Inner - gap) / 2;
                var x = Pad + i % 2 * (columnWidth + gap);
                var y = 352f + i / 2 * 17;
                var (label, value, colour) = hardware[i];
                DrawText(g, label, _tiny, Dim, x, y + 1);
                DrawText(g, value, _small, colour ?? Text, x + columnWidth, y, right: true);
            }
        }

        // Footer: which GPU OpenGL is really drawing on, and the settings being measured.
        HorizontalRule(g, footerTop);
        DrawText(g, Ellipsize(g, d.Renderer, _tiny, Inner), _tiny, Muted, Pad, footerTop + 8);
        using (var brush = new SolidBrush(Dim))
            g.DrawString(d.Settings, _tiny, brush, new RectangleF(Pad, footerTop + 24, Inner, settingsHeight + 2));

        if (warning is not null)
        {
            var box = new RectangleF(Pad, footerTop + 24 + settingsHeight + 8, Inner, warningHeight - 8);
            using var path = RoundedRect(box, 5f);
            using var fill = new SolidBrush(Color.FromArgb(34, Warn));
            using var brush = new SolidBrush(Warn);
            g.FillPath(fill, path);
            g.DrawString(warning, _tiny, brush, new RectangleF(box.Left + 6, box.Top + 4, box.Width - 12, box.Height - 6));
        }

        return height;
    }

    /// <summary>Hardware readings that exist on this machine, as (label, value, colour or null) in display order.</summary>
    private static List<(string Label, string Value, Color? Colour)> HardwareRows(PanelData d)
    {
        var s = d.System;
        var rows = new List<(string, string, Color?)>();
        if (s.GpuLoad is { } load)
            rows.Add(("GPU load", s.OwnGpuLoad is { } own ? $"{load:0}% (Pipes {own:0}%)" : $"{load:0}%", null));
        if (s.GpuTemperature is { } temp)
            rows.Add(("GPU temp", $"{temp:0} °C", temp >= 87 ? Bad : temp >= 80 ? Warn : null));
        if (s.GpuClockMhz is { } clock)
            rows.Add(("Core clock", $"{clock:0} MHz", null));
        if (s.MemoryClockMhz is { } memoryClock)
            rows.Add(("Mem clock", $"{memoryClock:0} MHz", null));
        if (s.PowerWatts is { } power)
            rows.Add(("Power", s.PowerLimitWatts is { } limit ? $"{power:0} / {limit:0} W" : $"{power:0} W", null));
        if (s.VramUsedBytes is { } used && s.VramTotalBytes is { } total)
            rows.Add(("VRAM", $"{used / 1e9:0.0} / {total / 1e9:0.0} GB", null));
        if (s.PState is { } pstate)
            rows.Add(("P-state", $"P{pstate}", null));
        if (s.FanPercent is { } fan)
            rows.Add(("Fan", $"{fan:0}%", null));
        if (s.SystemCpu is { } cpu)
            rows.Add(("CPU", $"{cpu:0}%", null));
        if (s.ProcessCpu is { } mine)
            rows.Add(("CPU Pipes", $"{mine:0.0}%", null));
        if (s.WorkingSetBytes is { } ram)
            rows.Add(("RAM Pipes", $"{ram / 1e6:0} MB", null));
        return rows;
    }

    /// <summary>
    /// A warning worth shouting about. The big one: a laptop drawing on its slow integrated GPU while a fast NVIDIA
    /// one sits idle (Optimus picks per app, and a screensaver isn't on its list of games).
    /// </summary>
    private static string? Warning(PanelData d)
    {
        var r = d.Renderer;
        if (r.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase) || r.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
            return "No graphics driver in use: OpenGL is running in software. Install or update the GPU driver.";
        // Only when there's a faster GPU to switch to: on a machine with just the integrated one, it's no news.
        if (r.Contains("Intel", StringComparison.OrdinalIgnoreCase) && d.System.NvidiaName is { } nvidia)
            return $"Drawing on the integrated Intel GPU while the {nvidia} sits idle. Set Pipes to \"High " +
                   "performance\" in Windows Settings > System > Display > Graphics (add Pipes.scr there).";
        return null;
    }

    // ---- Drawing helpers -----------------------------------------------------------------------------------------

    /// <summary>A labelled bar: time per frame against the refresh interval (full bar = one refresh).</summary>
    private void LoadBar(Graphics g, string label, double? ms, double budget, float y)
    {
        DrawText(g, label, _label, Muted, Pad, y + 2);
        var bar = new RectangleF(Pad + 34, y + 6, Inner - 34 - 74, 6);
        using (var track = RoundedRect(bar, 3f))
        using (var trackBrush = new SolidBrush(Well))
            g.FillPath(trackBrush, track);
        if (ms is not { } value)
        {
            DrawText(g, "–", _value, Dim, Width - Pad, y - 1, right: true);
            return;
        }
        var fraction = (float)Math.Clamp(value / budget, 0, 1);
        if (fraction > 0.01f)
        {
            using var fill = RoundedRect(new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * fraction), bar.Height), 3f);
            using var brush = new SolidBrush(Status(value, budget));
            g.FillPath(brush, fill);
        }
        var percent = $"{value / budget * 100:0}%";
        var valueWidth = DrawText(g, $"{value:0.00} ms", _value, Text, Width - Pad, y - 1, right: true);
        DrawText(g, percent, _tiny, Dim, Width - Pad - valueWidth - 6, y + 1, right: true);
    }

    /// <summary>A legend entry ending at <paramref name="right"/>: a short line in the colour, then the name.
    /// Returns where the entry starts, for the next one to end before.</summary>
    private float Legend(Graphics g, string text, Color colour, float right)
    {
        var width = DrawText(g, text, _tiny, Muted, right, 75, right: true);
        using var pen = new Pen(colour, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = right - width - 5;
        g.DrawLine(pen, x - 9, 82, x, 82);
        return x - 9;
    }

    private static void Dot(Graphics g, float cx, float cy, float r, Color colour)
    {
        using var glow = new SolidBrush(Color.FromArgb(60, colour));
        using var brush = new SolidBrush(colour);
        g.FillEllipse(glow, cx - r * 2, cy - r * 2, r * 4, r * 4);
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
    }

    private static void HorizontalRule(Graphics g, float y)
    {
        using var pen = new Pen(Rule, 1f);
        g.DrawLine(pen, Pad, y, Width - Pad, y);
    }

    /// <summary>Draw one line of text with its top-left (or top-right) at (x, y). Returns its width.</summary>
    private float DrawText(Graphics g, string text, Font font, Color colour, float x, float y, bool right = false)
    {
        var width = g.MeasureString(text, font, PointF.Empty, _format).Width;
        using var brush = new SolidBrush(colour);
        g.DrawString(text, font, brush, right ? x - width : x, y, _format);
        return width;
    }

    private static SizeF Measure(Graphics g, string text, Font font, float width) => g.MeasureString(text, font, (int)width);

    /// <summary>Shorten <paramref name="text"/> with an ellipsis until it fits <paramref name="width"/>.</summary>
    private string Ellipsize(Graphics g, string text, Font font, float width)
    {
        if (g.MeasureString(text, font, PointF.Empty, _format).Width <= width) return text;
        while (text.Length > 1 && g.MeasureString(text + "…", font, PointF.Empty, _format).Width > width) text = text[..^1];
        return text + "…";
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        foreach (var font in new[] { _big, _medium, _value, _small, _label, _tiny }) font.Dispose();
        _format.Dispose();
    }
}
