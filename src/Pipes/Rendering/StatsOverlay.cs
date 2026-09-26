using System.Diagnostics;
using Pipes.Diagnostics;
using Silk.NET.OpenGL;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace Pipes.Rendering;

/// <summary>
/// The "Show performance stats" overlay: a panel in the top-left corner with frames per second, a live frame-time
/// graph, 1% lows, GPU and CPU time per frame, the GPU time split by render pass, and whatever the hardware reports
/// (GPU load, temperature, clocks, power, memory). See docs/ARCHITECTURE.md, "Stats overlay".
/// </summary>
/// <remarks>
/// Two layers, updated at different rates:
/// <list type="bullet">
/// <item>The <b>panel</b> (text, bars) is painted by <see cref="StatsPanel"/> with GDI+ four times a second, on a
/// background thread, and uploaded as a texture. Numbers that changed every frame would be unreadable anyway.</item>
/// <item>The <b>graph</b> is drawn by a shader every frame, straight from the frame history, so it scrolls
/// smoothly (<see cref="Shaders.OverlayGraphFragment"/>).</item>
/// </list>
/// The overlay measures itself too: its own GPU time shows as "HUD" among the passes, and none of its painting
/// happens on the render thread, so what the graph shows is the pipes, not the graph.
/// </remarks>
internal sealed unsafe class StatsOverlay : IDisposable
{
    /// <summary>How often the panel's numbers change: often enough to feel live, slowly enough to read.</summary>
    private const double PaintSeconds = 0.25;

    /// <summary>"Now" numbers (FPS, GPU and CPU time) average this long, so they react quickly but don't flicker.</summary>
    private const double NowMs = 500;

    /// <summary>Averages and lows cover this long: long enough for a steady 1% low.</summary>
    private const double WindowMs = 10_000;

    /// <summary>Gap between the panel and the corner of the screen, in logical pixels.</summary>
    private const int Margin = 16;

    /// <summary>Frames the graph texture holds: the graph shows the newest one per pixel column, at most this many.</summary>
    private const int GraphFrames = 512;

    /// <summary>Tidy values for the top of the graph, in milliseconds.</summary>
    private static readonly double[] NiceScales = [4, 5, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200, 300, 500, 1000];

    private readonly GL _gl;
    private readonly IntPtr _hwnd;
    private readonly PipesSettings _settings;
    private readonly FrameStats _frames = new();
    private readonly SystemMonitor _system;
    private readonly string _renderer;
    private readonly uint _vao, _panelProgram, _graphProgram, _dataTex;
    private readonly float[] _graphData = new float[GraphFrames * 3];

    // Panel: two bitmaps, so one can be painted while the other's picture is on screen.
    private StatsPanel _panel = null!;
    private readonly Bitmap?[] _bitmaps = new Bitmap?[2];
    private int _nextBitmap;
    private uint _panelTex;
    private Task<(Bitmap Bitmap, int Height, double Scale)>? _painting;
    private int _panelHeight;           // logical pixels in use in the uploaded picture; 0 before the first
    private double _graphScaleMs;       // the scale the uploaded picture's labels were painted for
    private double _nextScaleMs;        // the scale the next picture will use
    private long _lastPaint;
    private float _scale;
    private int _refreshHz;

    // GPU time per pass, added up between paints and averaged for the panel. In the order the passes run.
    private readonly List<(string Label, double Ms)> _passSums = [];
    private int _passFrames;

    public StatsOverlay(GL gl, IntPtr hwnd, PipesSettings settings)
    {
        _gl = gl;
        _hwnd = hwnd;
        _settings = settings;
        _renderer = gl.GetStringS(StringName.Renderer) ?? "Unknown GPU";
        Timer = new GpuTimer(gl);
        Timer.FrameMeasured += OnGpuFrame;
        _system = new SystemMonitor(_renderer);

        _vao = gl.GenVertexArray();
        _panelProgram = Compile(Shaders.FullscreenVertex, Shaders.OverlayPanelFragment);
        _graphProgram = Compile(Shaders.FullscreenVertex, Shaders.OverlayGraphFragment);

        // The graph's frame history: one RGB float texel per frame. Nearest filtering: the shader reads exact texels.
        _dataTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _dataTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb32f, GraphFrames, 1, 0, PixelFormat.Rgb, PixelType.Float, null);
        SetNearest();

        DisplayChanged();
    }

    /// <summary>The GPU timer the renderers mark their passes on (<see cref="PipeRenderer.Timer"/>).</summary>
    public GpuTimer Timer { get; }

    /// <summary>
    /// Call when the window may have moved to another monitor: picks up that monitor's refresh rate and display
    /// scaling (a new scaling means a new panel size, so the panel is rebuilt).
    /// </summary>
    public void DisplayChanged()
    {
        _refreshHz = DisplayInfo.RefreshRate(_hwnd);
        if (_graphScaleMs == 0) _graphScaleMs = _nextScaleMs = Nice(2000.0 / _refreshHz);
        var scale = DisplayInfo.Scale(_hwnd);
        if (scale == _scale) return;
        _scale = scale;

        _painting?.Wait(); // it may be painting into a bitmap of the old size
        _painting = null;
        _panel?.Dispose();
        foreach (var b in _bitmaps) b?.Dispose();
        _panel = new StatsPanel(scale);
        for (var i = 0; i < _bitmaps.Length; i++) _bitmaps[i] = new Bitmap(_panel.PixelWidth, _panel.PixelHeight, GdiPixelFormat.Format32bppPArgb);

        _gl.DeleteTexture(_panelTex);
        _panelTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _panelTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)_panel.PixelWidth, (uint)_panel.PixelHeight, 0, PixelFormat.Bgra, PixelType.UnsignedByte, null);
        SetNearest();
        _panelHeight = 0;
        _lastPaint = 0;
    }

    /// <summary>Start timing a frame on the GPU. Call before the frame's first draw.</summary>
    public void BeginFrame() => Timer.BeginFrame(_frames.Count);

    /// <summary>Record a frame once it's been handed to the screen.</summary>
    /// <param name="intervalMs">Time since the previous frame was handed over.</param>
    /// <param name="cpuMs">CPU time spent on the frame (simulation plus queueing the drawing).</param>
    public void FrameDone(double intervalMs, double cpuMs) => _frames.Add(intervalMs, cpuMs);

    /// <summary>
    /// Draw the overlay over the finished frame in the window's framebuffer, with its top-left corner at
    /// (<paramref name="left"/>, <paramref name="top"/>) plus the margin, in window pixels from the top-left.
    /// Also ends the frame's GPU timing (the overlay's own drawing counts as "HUD").
    /// </summary>
    public void Draw(int windowWidth, int windowHeight, int left, int top, int viewCount)
    {
        UpdatePanel(windowWidth, windowHeight, viewCount);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha); // premultiplied alpha
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);

        if (_panelHeight > 0)
        {
            var x = left + (int)(Margin * _scale);
            var y = top + (int)(Margin * _scale);
            var width = _panel.PixelWidth;
            var height = (int)MathF.Ceiling(_panelHeight * _scale);
            // OpenGL counts y up from the bottom of the window.
            _gl.Viewport(x, windowHeight - y - height, (uint)width, (uint)height);
            _gl.UseProgram(_panelProgram);
            _gl.BindTexture(TextureTarget.Texture2D, _panelTex);
            _gl.Uniform1(_gl.GetUniformLocation(_panelProgram, "uPanel"), 0);
            _gl.Uniform2(_gl.GetUniformLocation(_panelProgram, "uUsed"), 1f, (float)height / _panel.PixelHeight);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            DrawGraph(x, windowHeight - y, windowWidth);
        }

        _gl.Disable(EnableCap.Blend);
        Timer.Mark("HUD");
        Timer.EndFrame();
    }

    /// <summary>The live graph, in the panel's graph well. <paramref name="panelTop"/> is in OpenGL's bottom-up y.</summary>
    private void DrawGraph(int panelLeft, int panelTop, int windowWidth)
    {
        var rect = StatsPanel.GraphRect;
        var gx = panelLeft + (int)MathF.Round(rect.X * _scale);
        var gw = (int)MathF.Round(rect.Width * _scale);
        var gh = (int)MathF.Round(rect.Height * _scale);
        var gy = panelTop - (int)MathF.Round(rect.Bottom * _scale);
        var count = Math.Min(GraphFrames, gw);

        // Newest frame in the last texel. Frames not yet recorded (just started) and GPU times not yet measured are
        // negative, which the shader skips.
        var available = (int)Math.Min(_frames.Count, FrameStats.Capacity);
        for (var i = 0; i < GraphFrames; i++)
        {
            var age = GraphFrames - 1 - i;
            var o = i * 3;
            if (age >= available)
            {
                _graphData[o] = _graphData[o + 1] = _graphData[o + 2] = -1f;
                continue;
            }
            var (interval, cpu, gpu) = _frames.Recent(age);
            _graphData[o] = interval;
            _graphData[o + 1] = cpu;
            _graphData[o + 2] = float.IsNaN(gpu) ? -1f : gpu;
        }
        _gl.BindTexture(TextureTarget.Texture2D, _dataTex);
        fixed (float* data = _graphData)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, GraphFrames, 1, PixelFormat.Rgb, PixelType.Float, data);

        _gl.Viewport(gx, gy, (uint)gw, (uint)gh);
        _gl.UseProgram(_graphProgram);
        int Loc(string name) => _gl.GetUniformLocation(_graphProgram, name);
        _gl.Uniform1(Loc("uData"), 0);
        _gl.Uniform2(Loc("uOrigin"), (float)gx, gy);
        _gl.Uniform2(Loc("uSize"), (float)gw, gh);
        _gl.Uniform1(Loc("uCount"), count);
        _gl.Uniform1(Loc("uScaleMs"), (float)_graphScaleMs);
        _gl.Uniform1(Loc("uBudgetMs"), 1000f / _refreshHz);
        _gl.Uniform1(Loc("uCorner"), StatsPanel.GraphCorner * _scale);
        SetColour(Loc("uGood"), StatsPanel.Good);
        SetColour(Loc("uWarn"), StatsPanel.Warn);
        SetColour(Loc("uBad"), StatsPanel.Bad);
        SetColour(Loc("uGpu"), StatsPanel.GpuLine);
        SetColour(Loc("uCpu"), StatsPanel.CpuLine);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private void SetColour(int location, Color c) => _gl.Uniform3(location, c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>
    /// Upload a freshly painted panel if one is ready, and start painting the next when it's due. The graph's scale
    /// switches over with the picture, so its labels (painted) and its lines (drawn live) always agree.
    /// </summary>
    private void UpdatePanel(int windowWidth, int windowHeight, int viewCount)
    {
        if (_painting is { IsCompleted: true } done)
        {
            _painting = null;
            if (done.IsCompletedSuccessfully)
            {
                var (bitmap, height, scale) = done.Result;
                var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppPArgb);
                _gl.BindTexture(TextureTarget.Texture2D, _panelTex);
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)bitmap.Width, (uint)bitmap.Height, PixelFormat.Bgra, PixelType.UnsignedByte, (void*)bits.Scan0);
                bitmap.UnlockBits(bits);
                _panelHeight = height;
                _graphScaleMs = scale;
            }
        }

        var now = Stopwatch.GetTimestamp();
        if (_painting != null || Stopwatch.GetElapsedTime(_lastPaint, now).TotalSeconds < PaintSeconds) return;
        _lastPaint = now;

        var summary = _frames.Summarise(NowMs, WindowMs);
        var budget = 1000.0 / _refreshHz;
        _nextScaleMs = GraphScale(summary, budget);
        var data = new PanelData(summary, _refreshHz, _nextScaleMs, AveragePasses(), _system.Latest, _renderer,
            Describe(windowWidth, windowHeight, viewCount));
        var target = _bitmaps[_nextBitmap]!;
        _nextBitmap ^= 1;
        var panel = _panel;
        var scaleMs = _nextScaleMs;
        _painting = Task.Run(() => (target, panel.Paint(target, data), scaleMs));
    }

    /// <summary>
    /// The graph's full height in ms: room for the slow frames (the 99th percentile) and at least 1.5 refreshes, so
    /// the refresh line sits in the lower part with space above for spikes. It grows at once when frames get slower,
    /// but only shrinks once everything would fit in half, so the graph doesn't keep rescaling.
    /// </summary>
    private double GraphScale(FrameStats.Summary s, double budget)
    {
        var needed = Nice(Math.Max(budget * 1.5, s.P99Ms * 1.2));
        return needed > _graphScaleMs || needed <= _graphScaleMs / 2 ? needed : _graphScaleMs;
    }

    private static double Nice(double ms) => NiceScales.FirstOrDefault(n => n >= ms, NiceScales[^1]);

    /// <summary>Each pass's average GPU time since the last paint, then start adding up afresh.</summary>
    private List<(string Label, double Ms)> AveragePasses()
    {
        var averages = _passFrames == 0 ? [] : _passSums.Select(p => (p.Label, p.Ms / _passFrames)).ToList();
        _passSums.Clear();
        _passFrames = 0;
        return averages;
    }

    private void OnGpuFrame(long frame, double totalMs, IReadOnlyList<(string Label, double Ms)> passes)
    {
        _frames.SetGpu(frame, totalMs);
        foreach (var (label, ms) in passes)
        {
            var i = _passSums.FindIndex(p => p.Label == label);
            if (i < 0) _passSums.Add((label, ms));
            else _passSums[i] = (label, _passSums[i].Ms + ms);
        }
        _passFrames++;
    }

    /// <summary>The settings that matter for performance, in a line, so a screenshot of the panel says what was measured.</summary>
    private string Describe(int width, int height, int viewCount)
    {
        var s = _settings;
        var parts = new List<string> { s.Style == GraphicsStyle.Classic ? "Classic" : "Modern", $"{width}×{height}" };
        if (viewCount > 1) parts.Add($"{viewCount} scenes");
        parts.Add(s.Antialiasing > 0 ? $"{s.Antialiasing}× MSAA" : "no MSAA");
        if (s.Style == GraphicsStyle.Modern)
        {
            if (s.SurfaceDetail) parts.Add("surfaces");
            if (s.Shadows) parts.Add("shadows");
            if (s.AmbientOcclusion) parts.Add("AO");
            if (s.Bloom) parts.Add("bloom");
            if (s.DepthOfField) parts.Add("DoF");
            if (s.TracedReflections) parts.Add("reflections");
        }
        if (s.Camera == CameraMotion.FlyThrough) parts.Add($"tunnel {s.TunnelDensity * 10}%");
        return string.Join(" · ", parts);
    }

    private void SetNearest()
    {
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    private uint Compile(string vertexSource, string fragmentSource)
    {
        uint Stage(ShaderType type, string source)
        {
            var shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException($"Overlay {type} compile failed: " + _gl.GetShaderInfoLog(shader));
            return shader;
        }
        var vs = Stage(ShaderType.VertexShader, vertexSource);
        var fs = Stage(ShaderType.FragmentShader, fragmentSource);
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0) throw new InvalidOperationException("Overlay link failed: " + _gl.GetProgramInfoLog(program));
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return program;
    }

    public void Dispose()
    {
        try { _painting?.Wait(); } catch (AggregateException) { }
        _system.Dispose();
        Timer.Dispose();
        _panel.Dispose();
        foreach (var b in _bitmaps) b?.Dispose();
        _gl.DeleteTexture(_panelTex);
        _gl.DeleteTexture(_dataTex);
        _gl.DeleteProgram(_panelProgram);
        _gl.DeleteProgram(_graphProgram);
        _gl.DeleteVertexArray(_vao);
    }
}
