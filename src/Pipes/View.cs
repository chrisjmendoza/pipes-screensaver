using Pipes.Rendering;

namespace Pipes;

/// <summary>
/// One independent picture inside the window: its own <see cref="Scene"/> (pipes, colours, camera, timing) and its
/// own <see cref="PipeRenderer"/> (render targets sized to it), drawn into one rectangle of the window.
/// </summary>
/// <remarks>
/// Normally there's a single view covering the whole window. In fullscreen with "own scene on each monitor", there's
/// one per monitor, all sharing one window and one OpenGL context. The alternative, a window per monitor, would mean
/// each window waiting for VSync separately, which can divide the frame rate by the number of monitors.
/// </remarks>
internal sealed class View : IDisposable
{
    /// <param name="renderSettings">
    /// What the renderer is built for, when that differs from <paramref name="settings"/>: the "On battery" quality
    /// ceiling holds the graphics down without touching the scene, which keeps picking its pipes and materials from
    /// the user's own settings. Null means the same settings for both.
    /// </param>
    public View(Silk.NET.OpenGL.GL gl, PipesSettings settings, Random rng, int x, int y, int width, int height, PipesSettings? renderSettings = null)
    {
        X = x;
        Y = y;
        Renderer = new PipeRenderer(gl, RenderOptions.From(renderSettings ?? settings));
        Scene = new Scene(settings, rng);
        Resize(width, height);
        Scene.Start(Aspect);
    }

    /// <summary>Left edge in window pixels.</summary>
    public int X { get; }

    /// <summary>Top edge in window pixels (Windows counts down from the top; OpenGL counts up from the bottom).</summary>
    public int Y { get; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Scene Scene { get; }
    public PipeRenderer Renderer { get; private set; }

    private float Aspect => (float)Width / Math.Max(1, Height);

    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Renderer.Resize(Width, Height);
        Scene.SetAspect(Aspect);
    }

    /// <summary>
    /// Draw this view's scene with <paramref name="renderer"/> from now on, disposing the old one. /autotune uses it
    /// to draw the very same scene with each quality preset in turn: render options are fixed for a renderer's
    /// lifetime (they pick which shaders to compile), so a new preset needs a new renderer, but the scene can stay.
    /// </summary>
    public void ReplaceRenderer(PipeRenderer renderer)
    {
        Renderer.Dispose();
        Renderer = renderer;
        Renderer.Resize(Width, Height);
    }

    public void Update(float dt) => Scene.Update(dt);

    /// <summary>Draw into <paramref name="targetFbo"/>, whose total height is <paramref name="targetHeight"/>.</summary>
    public void Render(uint targetFbo, int targetHeight) =>
        // Flip Y: our rectangle is measured from the window's top, OpenGL viewports from the bottom.
        Renderer.Render(Scene.Camera, Scene.Pieces, Scene.Fade, targetFbo, X, targetHeight - (Y + Height));

    public void Dispose() => Renderer.Dispose();
}
