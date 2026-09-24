using System.Numerics;

namespace Pipes.Rendering;

internal sealed class Camera
{
    public Matrix4x4 View { get; private set; }
    public Matrix4x4 Projection { get; private set; }
    public Vector3 Position { get; private set; }
    public float FogDensity { get; private set; }

    /// <summary>Orbits <paramref name="target"/> at <paramref name="distance"/>, yaw/pitch in radians.</summary>
    public void Update(Vector3 target, float distance, float yaw, float pitch, float aspect, float verticalFov)
    {
        var offset = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)) * distance;
        Position = target + offset;
        View = Matrix4x4.CreateLookAt(Position, target, Vector3.UnitY);
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, aspect, 0.1f, distance * 4f);
        // Far pipes fade gently into the background; scaled so the effect is similar at any grid size.
        FogDensity = 0.35f / (distance * distance);
    }
}
