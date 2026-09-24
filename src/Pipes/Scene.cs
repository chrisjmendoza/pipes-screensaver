using System.Numerics;
using Pipes.Rendering;
using Pipes.Simulation;

namespace Pipes;

/// <summary>
/// Runs one pipe world at a time: fade in, grow, hold, fade out, then start a fresh world from a new angle.
/// </summary>
internal sealed class Scene
{
    private const float FadeSeconds = 1.2f;
    private const float HoldSeconds = 2.5f;
    private const float VerticalFov = 0.75f; // ~43 degrees

    private readonly PipesSettings _settings;
    private readonly Random _rng;
    private readonly List<PipeInstance> _cylinders = [];
    private readonly List<PipeInstance> _spheres = [];

    private PipeWorld _world = null!;
    private float _aspect = 16f / 9f;
    private float _yaw, _pitch, _yawSpeed, _distance;
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

    public float Metallic => _settings.Finish == Finish.Metallic ? 0.85f : 0f;

    public List<PipeInstance> Cylinders => _cylinders;
    public List<PipeInstance> Spheres => _spheres;

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

        if (_settings.CameraDrift) _yaw += _yawSpeed * dt;

        Camera.Update(_world.Center, _distance, _yaw, _pitch, _aspect, VerticalFov);
        _world.Collect(_cylinders, _spheres);
    }

    private void NewWorld()
    {
        // Grid shaped to the screen so the pipes fill it, like the original.
        const int height = 12;
        var width = Math.Clamp((int)MathF.Round(height * _aspect), 8, 48);
        var depth = Math.Clamp((int)MathF.Round(height * 1.1f), 8, 20);
        _world = new PipeWorld(new Int3(width, height, depth), _settings, _rng);

        // Fit both the grid's height and width into view (from its front face), viewed mostly head-on.
        var halfTan = MathF.Tan(VerticalFov * 0.5f);
        var fitHeight = height * 0.5f / halfTan;
        var fitWidth = width * 0.5f / (halfTan * _aspect);
        _distance = MathF.Max(fitHeight, fitWidth) * 0.95f + depth * 0.5f + 2f;
        _yaw = (_rng.NextSingle() - 0.5f) * 0.4f;
        _pitch = (_rng.NextSingle() - 0.5f) * 0.25f;
        _yawSpeed = (_rng.Next(2) == 0 ? -1f : 1f) * 0.01f;

        Fade = 0f;
        Enter(Phase.FadeIn);
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseTime = 0f;
    }
}
