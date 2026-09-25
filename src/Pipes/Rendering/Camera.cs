using System.Numerics;

namespace Pipes.Rendering;

/// <summary>
/// View and projection for the frame, plus a few values the renderer derives from them (fog, depth-of-field focus).
/// </summary>
/// <remarks>
/// Matrices use System.Numerics' row-vector convention (<c>v * View * Projection</c>). Uploading that memory to
/// GLSL unchanged gives the transposed, column-vector form GLSL expects (<c>Projection * View * v</c>), so no
/// transposes are needed anywhere.
/// </remarks>
internal sealed class Camera
{
    public Matrix4x4 View { get; private set; }
    public Matrix4x4 Projection { get; private set; }
    public Vector3 Position { get; private set; }
    public float FogDensity { get; private set; }

    /// <summary>Distance to the sharpest plane for depth of field.</summary>
    public float FocusDistance { get; private set; }

    /// <summary>How quickly things blur away from <see cref="FocusDistance"/> (depth-of-field strength).</summary>
    public float FocusScale { get; private set; }

    /// <summary>
    /// The part of the world that gets shadows: a sphere the renderer fits its shadow map around. The smaller it is,
    /// the sharper the shadows, since the map's texels are spread over less space. A radius of 0 means no shadows.
    /// </summary>
    public Vector3 ShadowCentre { get; private set; }

    /// <inheritdoc cref="ShadowCentre"/>
    public float ShadowRadius { get; private set; }

    public void SetShadowFocus(Vector3 centre, float radius)
    {
        ShadowCentre = centre;
        ShadowRadius = radius;
    }

    /// <summary>
    /// How far the lighting rig (key light, fill light and the reflected light strips) has turned around the vertical
    /// axis, in radians. 0 is the fixed studio lighting. See <c>PipeRenderer.LightRig</c>.
    /// </summary>
    public float LightTurn { get; private set; }

    /// <summary>How far the key light is raised (+) or lowered (-) from its usual height, in radians.</summary>
    public float LightRise { get; private set; }

    public void SetLight(float turn, float rise)
    {
        LightTurn = turn;
        LightRise = rise;
    }

    /// <param name="up">Which way is up on screen. Must not point along the view direction.</param>
    /// <param name="far">Nothing farther than this is drawn.</param>
    /// <param name="fogReference">Distance the fog is scaled to: bigger means thinner fog.</param>
    /// <param name="depthOfFocus">Roughly how far in front of the focus plane something is fully blurred.</param>
    /// <param name="lensBlur">How much depth-of-field blur to use, 0 (none, and the pass is skipped) to 1 (full).</param>
    public void LookAt(Vector3 eye, Vector3 target, Vector3 up, float aspect, float verticalFov, float far, float fogReference, float depthOfFocus,
        float lensBlur = 1f)
    {
        Position = eye;
        View = Matrix4x4.CreateLookAt(eye, target, up);
        var distance = Vector3.Distance(eye, target);
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, aspect, 0.1f, far);
        // Far pipes fade gently into the background; scaled so the effect is similar at any grid size.
        FogDensity = 0.35f / (fogReference * fogReference);

        // Blur grows with |1/focus - 1/depth| (that's how a real lens behaves). Pick the scale so that something
        // depthOfFocus in front of the focus plane is at full blur.
        FocusDistance = distance;
        var nearSharp = MathF.Max(distance - depthOfFocus, 0.5f);
        FocusScale = lensBlur / MathF.Abs(1f / distance - 1f / nearSharp);
    }
}
