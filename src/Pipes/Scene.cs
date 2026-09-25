using System.Numerics;
using Pipes.Rendering;
using Pipes.Simulation;

namespace Pipes;

/// <summary>
/// Runs one pipe world at a time and moves the camera. The classic cycle is: fade in, grow, hold, fade out, then a
/// fresh world from a new angle. In fly-through mode the hold ends in a take-off instead: the camera flies into the
/// pipes and on through an endless tunnel, and only fades out after <see cref="FlightSeconds"/>.
/// </summary>
internal sealed class Scene
{
    private const float FadeSeconds = 1.2f;
    private const float HoldSeconds = 2.5f;
    private const float VerticalFov = 0.75f; // ~43 degrees

    /// <summary>How long a flight lasts before fading into a new scene.</summary>
    private const float FlightSeconds = 150f;

    /// <summary>The camera eases from standing still to full speed over this long.</summary>
    private const float TakeOffSeconds = 3f;

    /// <summary>The camera looks at a point this far ahead on the path, so it turns into bends a little early.</summary>
    private const float LookAhead = 5f;

    /// <summary>
    /// How quickly the camera's roll catches up with the banking it's aiming for (see <see cref="FollowRoll"/>), in
    /// "spring swings per 90° roll". Higher is snappier, lower is lazier and swings further past.
    /// </summary>
    private const float RollSnap = 6f;

    /// <summary>
    /// How much the roll spring is slowed down: 1 settles without overshooting, 0 would swing forever. 0.45 swings
    /// about 7° past a 90° bank, drifts a degree or two back the other way, and settles. Like a pendulum with drag.
    /// </summary>
    private const float RollDamping = 0.45f;

    /// <summary>The roll spring is stepped at least this finely, so a slow or hitching frame can't make it unstable.</summary>
    private const float RollStep = 1f / 120f;

    /// <summary>Multiply by this to turn degrees into radians.</summary>
    private const float Degrees = MathF.PI / 180f;

    /// <summary>
    /// How steeply the camera banks into a gentle curve: bank = atan(sideways curvature × this). A curve of radius
    /// 22 banks 45°, radius 30 about 36°. Physically this is the coordinated-turn formula, tan(bank) = speed² ÷
    /// (radius × gravity), with this constant standing in for speed² ÷ gravity.
    /// </summary>
    private const float BankPerCurvature = 22f;

    /// <summary>Gentle curves never bank steeper than this.</summary>
    private const float MaxGentleBank = 60f * Degrees;

    private readonly PipesSettings _settings;
    private readonly Random _rng;

    private PipeWorld _world = null!;
    private BoxSpace _box = null!;
    private float _aspect = 16f / 9f;
    private float _yaw, _pitch, _yawSpeed, _distance, _depth;
    private float _time;
    private Vector4 _phases; // random offsets so every scene's float motion is different
    private Phase _phase;
    private float _phaseTime;

    // Fly-through state (null / unused in the other camera modes).
    private FlightPath? _path;
    private TunnelSpace? _tunnel;
    private float _flightS;       // distance travelled along the path
    private float _sinceTakeOff;  // seconds since take-off
    private Vector3 _up = Vector3.UnitY;
    private float _rollRate;      // how fast the camera is rolling right now (radians/second): its roll momentum

    private enum Phase { FadeIn, Growing, Hold, Flying, FadeOut }

    public Scene(PipesSettings settings, Random rng)
    {
        _settings = settings;
        _rng = rng;
    }

    public Camera Camera { get; } = new();

    public float Fade { get; private set; }

    /// <summary>Everything to draw this frame.</summary>
    public PieceLists Pieces { get; } = new();

    private bool FlyThrough => _settings.Camera == CameraMotion.FlyThrough;

    private bool Airborne => _tunnel is { Flying: true };

    /// <summary>Flight speed in world units (grid cells) per second.</summary>
    private float FlySpeed => _settings.FlightSpeed;

    /// <summary>1 at the default flight speed, bigger when flying faster. Scales things that should keep up with it.</summary>
    private float SpeedFactor => MathF.Max(1f, FlySpeed / 5f);

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
                // A shorter pause before take-off: the pause is for admiring the finished scene, and in fly-through
                // mode you're about to see it from the inside.
                if (_phaseTime >= (FlyThrough ? 0.8f : HoldSeconds))
                {
                    if (FlyThrough) TakeOff();
                    else Enter(Phase.FadeOut);
                }
                break;
            case Phase.Flying:
                _world.Update(dt);
                if (_sinceTakeOff >= FlightSeconds) Enter(Phase.FadeOut);
                break;
            case Phase.FadeOut:
                Fade = Math.Max(0f, 1f - _phaseTime / FadeSeconds);
                if (Airborne) _world.Update(dt); // keep the tunnel growing while the picture fades
                if (_phaseTime >= FadeSeconds) NewWorld();
                break;
        }

        if (FlyThrough) UpdateFlyingCamera(dt);
        else UpdateOrbitCamera(dt);
        _world.Collect(Pieces);
    }

    private void UpdateOrbitCamera(float dt)
    {
        _time += dt;
        var target = _box.Center;
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
        Camera.LookAt(eye, target, Vector3.UnitY, _aspect, VerticalFov, far: distance * 4f,
            fogReference: _distance, depthOfFocus: _depth * 0.5f + 1f);
    }

    /// <summary>
    /// Fly-through camera. Before take-off it sits at the start of the path, looking head-on at the box (the path
    /// runs straight through the middle of it). After take-off it moves along the path.
    /// </summary>
    private void UpdateFlyingCamera(float dt)
    {
        var path = _path!;
        var tunnel = _tunnel!;
        _time += dt;

        // 0 on the ground, easing to 1 at full speed. Smoothstep makes the start and end of the ease gentle.
        var ease = 0f;
        if (Airborne)
        {
            _sinceTakeOff += dt;
            var t = Math.Clamp(_sinceTakeOff / TakeOffSeconds, 0f, 1f);
            ease = t * t * (3f - 2f * t);
            _flightS += FlySpeed * ease * dt;
        }

        var (position, _) = path.Pose(_flightS);
        var (ahead, _) = path.Pose(_flightS + LookAhead);
        var forward = Vector3.Normalize(ahead - position);

        // A hand on the stick is never perfectly still: a slow, faint roll wobble (two unrelated sine waves, so it
        // never visibly repeats) on top of the banking. It goes through the spring with everything else.
        var wobble = ease * Degrees * (1.2f * MathF.Sin(_time * 0.53f + _phases.Z) + 0.8f * MathF.Sin(_time * 0.87f + _phases.W));
        _up = FollowRoll(Rotate(BankedUp(position, forward), forward, wobble), forward, dt);

        // A gentle bob and sway while flying, well inside the corridor so it never brushes a pipe.
        var side = Vector3.Cross(forward, _up);
        var eye = position + ease * (
            side * (0.3f * MathF.Sin(_time * 0.37f + _phases.X)) +
            _up * (0.25f * MathF.Sin(_time * 0.29f + _phases.Y)));

        // Before take-off, frame the box like the other modes. In flight, focus close and let fog hide the far end
        // of the tunnel, where new pipes are still appearing.
        var focus = float.Lerp(_distance, 10f, ease);
        Camera.LookAt(eye, eye + forward * focus, _up, _aspect, VerticalFov,
            far: float.Lerp(_distance * 4f, tunnel.SpawnAheadMax + 28f, ease),
            fogReference: float.Lerp(_distance, 26f * SpeedFactor, ease),
            depthOfFocus: float.Lerp(_depth * 0.5f + 1f, 6f, ease));

        tunnel.CameraS = _flightS;
        if (Airborne)
        {
            // Throw away chunks well behind the camera: out of sight, and they'd otherwise pile up forever.
            _world.Recycle(centre => Vector3.Dot(centre - eye, forward) < -14f);
            tunnel.ForgetBehind();
        }
    }

    /// <summary>
    /// Which way is up for the camera at the current point of the flight: it banks through turns like a plane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plane doesn't skid sideways into a turn. It <b>rolls</b> until the turn is "overhead", pulls back on the
    /// stick, and rolls level again afterwards. Here that's one rule: during a turn, the camera's up points at the
    /// turn's centre. Everything else follows from it:
    /// </para>
    /// <list type="bullet">
    /// <item>Right or left turn: roll 90° towards it, pull round, roll back level.</item>
    /// <item>Turn upwards: the centre is already overhead, so no roll, just pull up.</item>
    /// <item>Turn downwards: roll 180° onto your back so the dive is overhead, then pull through it.</item>
    /// </list>
    /// <para>
    /// Each turn therefore has three parts along the path: a roll-in just before the arc (only if the camera isn't
    /// already facing the turn), the arc itself (up locked onto the centre), and sometimes a roll-out.
    /// </para>
    /// <para>
    /// <b>Flown by a good pilot, not a machine.</b> Done exactly, that's roll to 90°, hold it perfectly, roll back:
    /// three separate moves. Two habits of real pilots join them into one gesture:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Overbanking:</b> the roll-in goes a few degrees past what the turn needs (the camera is already
    /// facing the centre), and it holds there, a bit steep, through the corner.</item>
    /// <item><b>Leading the roll-out:</b> pilots start rolling out <i>before</i> reaching the new heading (the rule of
    /// thumb is half the bank angle early), so they arrive level instead of overshooting. Here the roll-out starts
    /// 25–45% of the way before the end of the arc, and takes the overbank back out along with it.</item>
    /// </list>
    /// <para>
    /// How steep, how early, and how quickly each roll-in happens varies a little with each turn's
    /// <see cref="FlightPath.Maneuver.Seed"/>, so no two turns are flown quite the same.
    /// </para>
    /// <para>
    /// <b>Sweeps and meanders</b> are gentler and banked differently: see <see cref="GentleCurve"/>.
    /// </para>
    /// <para>
    /// <b>There's no horizon</b>, so it's treated like flying through space: whatever attitude a turn ends with is
    /// the new "level", upside down included, and nothing rolls back afterwards. The one exception is a left or
    /// right turn, which leaves the camera on its side. That levels out, like a plane, to upright or inverted,
    /// whichever it was flying before the turn.
    /// </para>
    /// <para>
    /// This is a pure function of the distance flown, not something accumulated frame by frame: each call replays the
    /// maneuvers from the start of the flight (a few dozen at most). So it can't drift, and the same point of the flight
    /// always looks the same. It's the attitude the camera is <i>aiming</i> for: <see cref="FollowRoll"/> then adds
    /// the momentum on top.
    /// </para>
    /// </remarks>
    private Vector3 BankedUp(Vector3 position, Vector3 forward)
    {
        var s = _flightS;
        var level = Vector3.UnitY; // the attitude on the first straight, facing the box
        _path!.EnsureLength(s + 20f); // the gentle curves look a little ahead; make sure the path is there

        foreach (var maneuver in _path.Maneuvers)
        {
            var (up, after) = maneuver.Kind == FlightPath.Kind.Turn
                ? TightTurn(maneuver, level, s, position, forward)
                : GentleCurve(maneuver, level, s, forward);
            if (up is { } flying) return flying;     // in the middle of this one
            if (after is not { } done) break;        // not started yet (and nor has any later one)
            level = done;                            // already flown: the attitude it left is the new level
        }

        // On a straight, between maneuvers.
        return Perpendicular(level, forward);
    }

    /// <summary>
    /// One tight turn, flown as described on <see cref="BankedUp"/>. Returns the camera's up if
    /// <paramref name="s"/> is within the turn (roll-in and roll-out included), otherwise the level it leaves the
    /// camera in if it's already over, otherwise neither.
    /// </summary>
    private (Vector3? Up, Vector3? After) TightTurn(FlightPath.Maneuver turn, Vector3 levelBefore, float s, Vector3 position, Vector3 forward)
    {
        var levelAfter = LevelAfter(turn, levelBefore);
        // On the approach, the turn's centre lies exactly along turn.To; on the way out, exactly behind (-From).
        var rollIn = SignedAngle(levelBefore, turn.To, turn.From);
        var rollOut = SignedAngle(-turn.From, levelAfter, turn.To);

        // This turn's pilot quirks. Overbank only happens when there's a roll-in to carry on past.
        var overbank = rollIn == 0f ? 0f : MathF.Sign(rollIn) * float.Lerp(3f, 8f, Quirk(turn.Seed, 1)) * Degrees;
        var rollInLength = MathF.Min(RollDistance(rollIn, SpeedFactor) * float.Lerp(0.85f, 1.15f, Quirk(turn.Seed, 2)), 8.5f);
        var lead = float.Lerp(0.25f, 0.45f, Quirk(turn.Seed, 3)) * (turn.EndS - turn.StartS);

        var rollInStart = turn.StartS - rollInLength;
        var rollOutStart = turn.EndS - lead;
        var rollOutEnd = rollOutStart + RollDistance(rollOut, SpeedFactor);

        if (s < rollInStart) return (null, null);

        if (s < turn.StartS) // rolling in, and a little past
            return (Rotate(levelBefore, forward, (rollIn + overbank) * Ease((s - rollInStart) / rollInLength)), null);

        if (s <= turn.EndS || s < rollOutEnd)
        {
            // In the turn, up is measured from the centre; after it, from where the centre ended up (-From).
            // The two meet at the end of the arc, so it's seamless. On top of that: the overbank, which the
            // roll-out gradually swaps for the roll back to level.
            var centre = s <= turn.EndS ? turn.Centre - position : -turn.From;
            var rollingOut = Ease((s - rollOutStart) / (rollOutEnd - rollOutStart));
            return (Rotate(Perpendicular(centre, forward), forward, float.Lerp(overbank, rollOut, rollingOut)), null);
        }

        return (null, levelAfter);
    }

    /// <summary>
    /// A sweep or a meander: a long, gentle curve, banked only partly, the way a plane banks in a lazy turn.
    /// Same return convention as <see cref="TightTurn"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rather than "up points at the centre", the camera keeps its level and banks <i>by how sharply the path is curving
    /// sideways</i>: bank = atan(sideways curvature × <see cref="BankPerCurvature"/>), which is how a real coordinated
    /// turn works (a tighter turn needs a steeper bank). A curve straight up or down relative to the camera has no
    /// sideways part, so it doesn't roll at all, just pitches, like a plane cresting a hill.
    /// </para>
    /// <para>
    /// <b>Curvature</b> is how fast the direction of travel changes per unit flown. Averaging it over a stretch of
    /// path is easy: it's just (direction at the far end − direction at the near end) ÷ length. Here the stretch runs
    /// a few units either side of the camera, which does two nice things: the bank eases in and out instead of
    /// jumping when a curve starts, and it starts a moment <i>before</i> the curve, as a pilot anticipates it.
    /// </para>
    /// <para>
    /// <b>Level</b> is carried through the curve by the smallest rotation that turns the old direction of travel into
    /// the current one. For a curve that stays in one plane, that's exactly how the camera's up would move if it
    /// didn't roll at all (called <i>parallel transport</i>). So a sideways sweep keeps the camera upright, and a sweep
    /// upwards tips its up over backwards, like the pull of a tight turn.
    /// </para>
    /// </remarks>
    private (Vector3? Up, Vector3? After) GentleCurve(FlightPath.Maneuver curve, Vector3 levelBefore, float s, Vector3 forward)
    {
        var reach = MathF.Min(4f * SpeedFactor, 8f); // how far either side the curvature is averaged
        if (s < curve.StartS - reach) return (null, null);
        if (s > curve.EndS + reach) return (null, Perpendicular(Carry(levelBefore, curve.From, curve.To), curve.To));

        var level = Perpendicular(Carry(levelBefore, curve.From, _path!.Pose(s).Tangent), forward);
        var curvature = (_path.Pose(s + reach).Tangent - _path.Pose(s - reach).Tangent) / (2f * reach);
        var right = Vector3.Cross(forward, level);
        var bank = Math.Clamp(MathF.Atan(Vector3.Dot(curvature, right) * BankPerCurvature), -MaxGentleBank, MaxGentleBank);
        return (Rotate(level, forward, bank), null);
    }

    /// <summary>
    /// <paramref name="v"/> turned by the smallest rotation that takes direction <paramref name="from"/> to
    /// <paramref name="to"/>: around the axis perpendicular to both, by the angle between them.
    /// </summary>
    private static Vector3 Carry(Vector3 v, Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        var sin = axis.Length();
        if (sin < 1e-5f) return v; // same direction (or exactly opposite, which never happens here)
        var angle = MathF.Atan2(sin, Vector3.Dot(from, to));
        return Vector3.Transform(v, Quaternion.CreateFromAxisAngle(axis / sin, angle));
    }

    /// <summary>
    /// Rolls the camera towards <paramref name="target"/> (the banking <see cref="BankedUp"/> asks for) with momentum,
    /// so it swings a little past each bank and each levelling-out, then eases back, like a pendulum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On its own, <see cref="BankedUp"/> rolls exactly as far as needed and stops dead, which looks like the camera
    /// is on rails. A real aircraft has rotational inertia: it takes a moment to start rolling, and once rolling it
    /// carries on a touch past where the pilot wanted it. That's a <b>damped spring</b>: the further the roll is from
    /// the target, the harder it's pulled towards it (stiffness), and the faster it's already rolling, the more that's
    /// resisted (damping). Damped below "critical" (<see cref="RollDamping"/> &lt; 1), it arrives with a bit of speed
    /// left over, overshoots, and swings back. The target itself moves smoothly (smoothstep), so the overshoot is small.
    /// </para>
    /// <para>
    /// Only the <b>roll</b> goes through the spring. As the camera pitches and yaws round a turn, the previous up is
    /// carried along exactly (see below) and the spring just closes the remaining roll angle, so the camera never
    /// lags behind the path itself, only in how far it's tipped.
    /// </para>
    /// <para>
    /// The spring's speed is set relative to how long a 90° roll takes to fly at the current speed, so the swing
    /// looks the same at every flight speed instead of lagging badly on fast flights.
    /// </para>
    /// <para>
    /// The cost is a handful of multiplies per frame. It's stepped in fixed small steps (semi-implicit Euler: update
    /// the speed first, then move with the new speed), which stays stable and behaves the same at 60 or 144 Hz.
    /// </para>
    /// </remarks>
    private Vector3 FollowRoll(Vector3 target, Vector3 forward, float dt)
    {
        // Carry last frame's up along as forward swings round. Dropping the part of it that now points along forward
        // turns it by exactly the camera's pitch, and leaves it alone for yaw: it moves with the camera, adding no roll.
        var up = Perpendicular(_up, forward);

        // How far the roll is from where it's aiming. The spring is stepped on "offset from the target", which the
        // target doesn't change during one frame.
        var toTarget = SignedAngle(up, target, forward);
        var offset = -toTarget;

        // Natural frequency (radians per second) from the time a 90° roll takes at this flight speed.
        var rollSeconds = RollDistance(MathF.PI * 0.5f, SpeedFactor) / FlySpeed;
        var omega = RollSnap / rollSeconds;

        dt = MathF.Min(dt, 0.1f); // after a long stall, don't try to catch up in one go
        var steps = Math.Max(1, (int)MathF.Ceiling(dt / RollStep));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            // Spring pull back towards the target, minus drag on the current roll rate.
            var accel = -omega * omega * offset - 2f * RollDamping * omega * _rollRate;
            _rollRate += accel * h;
            offset += _rollRate * h;
        }

        // Roll by however far the offset moved this frame.
        return Rotate(up, forward, offset + toTarget);
    }

    /// <summary>
    /// The attitude a turn leaves the camera in. It pulls through with up facing the turn's centre, which at the end
    /// of the turn lies straight back along the old direction (<c>-From</c>), and that simply becomes the new level.
    /// Except after a left/right turn in horizontal flight, which leaves the camera on its side. That levels out to
    /// upright or inverted, whichever it was flying before.
    /// </summary>
    private static Vector3 LevelAfter(FlightPath.Maneuver turn, Vector3 levelBefore)
    {
        var pulled = -turn.From;
        var onItsSide = MathF.Abs(turn.To.Y) < 0.5f && MathF.Abs(pulled.Y) < 0.5f;
        if (!onItsSide) return pulled;
        return levelBefore.Y < 0f ? -Vector3.UnitY : Vector3.UnitY;
    }

    /// <summary>
    /// How far along the path a roll takes: longer for bigger rolls, so a 180° roll is quicker per degree but still
    /// smooth. Faster flights stretch it out a little (<paramref name="speedFactor"/>), so a roll doesn't become a
    /// snap. Capped at 8.5 units each side of a turn: straights are at least 18 long, so neighbouring turns' rolls
    /// never overlap.
    /// </summary>
    private static float RollDistance(float angle, float speedFactor) =>
        MathF.Min((5f + 3f * MathF.Abs(angle) / MathF.PI) * speedFactor, 8.5f);

    /// <summary>
    /// The angle to roll (around <paramref name="axis"/>) to turn <paramref name="from"/> into <paramref name="to"/>.
    /// Positive rolls to the right. A half turn could go either way, and always goes right, so it's consistent.
    /// </summary>
    private static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
    {
        var angle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(from, to)), Vector3.Dot(from, to));
        return MathF.Abs(angle) > MathF.PI - 0.01f ? MathF.PI : angle;
    }

    /// <summary>Rotate <paramref name="v"/> by <paramref name="angle"/> around <paramref name="axis"/> (Rodrigues' formula).</summary>
    private static Vector3 Rotate(Vector3 v, Vector3 axis, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        var rotated = v * cos + Vector3.Cross(axis, v) * sin + axis * (Vector3.Dot(axis, v) * (1f - cos));
        return Perpendicular(rotated, axis);
    }

    /// <summary><paramref name="v"/> with any part along <paramref name="forward"/> removed, as a unit vector.</summary>
    private static Vector3 Perpendicular(Vector3 v, Vector3 forward) =>
        Vector3.Normalize(v - forward * Vector3.Dot(v, forward));

    /// <summary>
    /// A repeatable "random" number between 0 and 1 from a turn's seed, a different one for each <paramref name="k"/>.
    /// It's the classic shader hash: multiply a sine by a big number and keep the fraction, which scrambles nearby
    /// inputs into unrelated outputs. Not good randomness, but plenty for varying how a turn is flown.
    /// </summary>
    private static float Quirk(float seed, int k)
    {
        var x = MathF.Sin(seed * 12.9898f + k * 78.233f) * 43758.547f;
        return x - MathF.Floor(x);
    }

    /// <summary>Smoothstep: 0 to 1 with a gentle start and finish.</summary>
    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private void TakeOff()
    {
        _tunnel!.Flying = true;
        _world.PipeQuota = int.MaxValue;
        _world.ConcurrentPipes = FlightConcurrency();
        _sinceTakeOff = 0f;
        Enter(Phase.Flying);
    }

    /// <summary>
    /// Enough pipes growing at once to keep the tunnel walls filling up as fast as the camera flies into them.
    /// Each second the camera uncovers a slice of wall (ring area × flight speed), and each pipe grows <c>Speed</c>
    /// cells a second, so roughly (slice ÷ speed) pipes would fill it completely. Aiming for about 60% leaves gaps to
    /// see through: a completely full tunnel looks like a wall. Slow-growing pipes need more of them. Never fewer than
    /// the user's setting.
    /// </summary>
    private int FlightConcurrency()
    {
        var ringArea = MathF.PI * (TunnelSpace.OuterRadius * TunnelSpace.OuterRadius - TunnelSpace.InnerRadius * TunnelSpace.InnerRadius);
        var needed = 0.6f * ringArea * FlySpeed / _settings.Speed;
        return Math.Max(_settings.ConcurrentPipes, Math.Min((int)MathF.Ceiling(needed), 80));
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
        _box = new BoxSpace(new Int3(width, height, depth));
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

        if (FlyThrough)
        {
            // The camera starts straight in front of the box, and the path runs from there through the box's middle
            // and out the far side before its first turn. The tunnel space keeps a corridor clear along it.
            var start = _box.Center + new Vector3(0f, 0f, _distance);
            _path = new FlightPath(start, new Int3(0, 0, -1), firstRun: _distance + depth * 0.5f + 14f, _rng);
            // Spawn far enough ahead that pipes get about 2.5 seconds to grow before the camera arrives.
            _tunnel = new TunnelSpace(_path, _box) { SpawnAhead = MathF.Max(14f, FlySpeed * 2.5f) };
            _world = new PipeWorld(_tunnel, _settings, _rng);
            _flightS = 0f;
            _sinceTakeOff = 0f;
            _up = Vector3.UnitY;
            _rollRate = 0f;
        }
        else
        {
            _path = null;
            _tunnel = null;
            _world = new PipeWorld(_box, _settings, _rng);
        }

        Fade = 0f;
        Enter(Phase.FadeIn);
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseTime = 0f;
    }
}
