using System.Numerics;
using Pipes.Rendering;
using Pipes.Simulation;

namespace Pipes;

/// <summary>
/// Runs one pipe world at a time: fade in, grow, hold, fade out, then start a fresh world from a new angle.
/// Also moves the camera.
/// </summary>
internal sealed class Scene
{
    private const float FadeSeconds = 1.2f;
    private const float HoldSeconds = 2.5f;
    private const float VerticalFov = 0.75f; // ~43 degrees

    private readonly PipesSettings _settings;
    private readonly Random _rng;

    private PipeWorld _world = null!;
    private float _aspect = 16f / 9f;
    private float _yaw, _pitch, _yawSpeed, _distance, _depth;
    private float _time;
    private Vector4 _phases; // random offsets so every scene's float motion is different
    private Phase _phase;
    private float _phaseTime;

    private enum Phase { FadeIn, Growing, Hold, FadeOut }

    public Scene(PipesSettings settings, Random rng)
    {
        _settings = settings;
        _rng = rng;
    }

    public Camera Camera { get; } = new();

    public float Fade { get; private set; }

    /// <summary>Everything to draw this frame.</summary>
    public PieceLists Pieces { get; } = new();

    public void Start(float aspect)
    {
        _aspect = aspect;
        NewWorld();
    }

    public void SetAspect(float aspect) => _aspect = aspect;

    public void Update(float dt)
    {
        _phaseTime += dt;
        switch (_phase)
        {
            case Phase.FadeIn:
                Fade = Math.Min(1f, _phaseTime / FadeSeconds);
                _world.Update(dt);
                if (_phaseTime >= FadeSeconds) Enter(Phase.Growing);
                break;
            case Phase.Growing:
                Fade = 1f;
                _world.Update(dt);
                if (_world.IsFinished) Enter(Phase.Hold);
                break;
            case Phase.Hold:
                if (_phaseTime >= HoldSeconds) Enter(Phase.FadeOut);
                break;
            case Phase.FadeOut:
                Fade = Math.Max(0f, 1f - _phaseTime / FadeSeconds);
                if (_phaseTime >= FadeSeconds) NewWorld();
                break;
        }

        UpdateCamera(dt);
        _world.Collect(Pieces);
    }

    private void UpdateCamera(float dt)
    {
        _time += dt;
        var target = _world.Center;
        var pitch = _pitch;
        var distance = _distance;

        if (_settings.Camera != CameraMotion.Still) _yaw += _yawSpeed * dt;

        if (_settings.Camera == CameraMotion.Float)
        {
            // Several slow sine waves at unrelated speeds never quite repeat, which reads as organic drifting
            // rather than a mechanical loop. Kept small so the grid stays framed.
            pitch += 0.10f * MathF.Sin(_time * 0.21f + _phases.X);
            distance *= 1f + 0.07f * MathF.Sin(_time * 0.13f + _phases.Y);
            target += new Vector3(
                0.9f * MathF.Sin(_time * 0.11f + _phases.Z),
                0.5f * MathF.Sin(_time * 0.17f + _phases.W),
                0f);
        }

        var eye = target + new Vector3(MathF.Sin(_yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(_yaw) * MathF.Cos(pitch)) * distance;
        // Depth of field: fully blurred at the grid's front and back faces, sharp through the middle.
        Camera.LookAt(eye, target, _aspect, VerticalFov, fogReference: _distance, depthOfFocus: _depth * 0.5f + 1f);
    }

    private void NewWorld()
    {
        // Grid shaped to the screen so the pipes fill it, like the original: 12 cells across the screen's shorter
        // side, and as many as fit along the longer one. So a landscape screen gets a wide grid and a portrait
        // (rotated) monitor a tall one.
        const int shortSide = 12;
        int width, height;
        if (_aspect >= 1f)
        {
            height = shortSide;
            width = Math.Clamp((int)MathF.Round(shortSide * _aspect), shortSide, 48);
        }
        else
        {
            width = shortSide;
            height = Math.Clamp((int)MathF.Round(shortSide / _aspect), shortSide, 48);
        }
        var depth = Math.Clamp((int)MathF.Round(shortSide * 1.1f), 8, 20);
        _world = new PipeWorld(new Int3(width, height, depth), _settings, _rng);
        _depth = depth;

        // Fit both the grid's height and width into view (from its front face), viewed mostly head-on.
        var halfTan = MathF.Tan(VerticalFov * 0.5f);
        var fitHeight = height * 0.5f / halfTan;
        var fitWidth = width * 0.5f / (halfTan * _aspect);
        _distance = MathF.Max(fitHeight, fitWidth) * 0.95f + depth * 0.5f + 2f;
        _yaw = (_rng.NextSingle() - 0.5f) * 0.4f;
        _pitch = (_rng.NextSingle() - 0.5f) * 0.25f;
        _yawSpeed = (_rng.Next(2) == 0 ? -1f : 1f) * (_settings.Camera == CameraMotion.Float ? 0.015f : 0.01f);
        _time = 0f;
        _phases = new Vector4(_rng.NextSingle(), _rng.NextSingle(), _rng.NextSingle(), _rng.NextSingle()) * MathF.Tau;

        Fade = 0f;
        Enter(Phase.FadeIn);
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseTime = 0f;
    }
}
