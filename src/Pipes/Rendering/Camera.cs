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

    /// <param name="depthOfFocus">Roughly how far in front of the focus plane something is fully blurred.</param>
    public void LookAt(Vector3 eye, Vector3 target, float aspect, float verticalFov, float fogReference, float depthOfFocus)
    {
        Position = eye;
        View = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var distance = Vector3.Distance(eye, target);
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, aspect, 0.1f, distance * 4f);
        // Far pipes fade gently into the background; scaled so the effect is similar at any grid size.
        FogDensity = 0.35f / (fogReference * fogReference);

        // Blur grows with |1/focus - 1/depth| (that's how a real lens behaves). Pick the scale so that something
        // depthOfFocus in front of the focus plane is at full blur.
        FocusDistance = distance;
        var nearSharp = MathF.Max(distance - depthOfFocus, 0.5f);
        FocusScale = 1f / MathF.Abs(1f / distance - 1f / nearSharp);
    }
}
