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
    public View(Silk.NET.OpenGL.GL gl, PipesSettings settings, Random rng, int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Renderer = new PipeRenderer(gl, RenderOptions.From(settings));
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
    public PipeRenderer Renderer { get; }

    private float Aspect => (float)Width / Math.Max(1, Height);

    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Renderer.Resize(Width, Height);
        Scene.SetAspect(Aspect);
    }

    public void Update(float dt) => Scene.Update(dt);

    /// <summary>Draw into <paramref name="targetFbo"/>, whose total height is <paramref name="targetHeight"/>.</summary>
    public void Render(uint targetFbo, int targetHeight) =>
        // Flip Y: our rectangle is measured from the window's top, OpenGL viewports from the bottom.
        Renderer.Render(Scene.Camera, Scene.Pieces, Scene.Fade, targetFbo, X, targetHeight - (Y + Height));

    public void Dispose() => Renderer.Dispose();
}
