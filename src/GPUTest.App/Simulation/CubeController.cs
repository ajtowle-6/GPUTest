using System.Numerics;

namespace GPUTest.App.Simulation;

internal sealed class CubeController
{
    public Vector3 TargetPosition { get; set; }
    public Quaternion TargetOrientation { get; set; } = Quaternion.Identity;
    public Vector3 Position { get; private set; }
    public Quaternion Orientation { get; private set; } = Quaternion.Identity;

    public void Advance()
    {
        Position = TargetPosition;
        Orientation = Quaternion.Normalize(TargetOrientation);
    }
}