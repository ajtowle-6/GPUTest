using System.Numerics;

namespace GPUTest.App.Simulation;

internal sealed class CubeController
{
    public const float MaximumLinearSpeed = 10;
    public const float MaximumLinearAcceleration = 80;
    public const float MaximumAngularSpeed = 1;

    public Vector3 TargetPosition { get; set; }
    public Quaternion TargetOrientation { get; set; } = Quaternion.Identity;
    public Vector3 Position { get; private set; }
    public Quaternion Orientation { get; private set; } = Quaternion.Identity;
    private Vector3 velocity;

    public void Advance()
    {
        float timestep = PhysicsWorld.Timestep;
        Vector3 displacement = TargetPosition - Position;
        Vector3 desiredVelocity = displacement / timestep;
        desiredVelocity = Limit(desiredVelocity, MaximumLinearSpeed);
        velocity += Limit(desiredVelocity - velocity, MaximumLinearAcceleration * timestep);
        Vector3 movement = velocity * timestep;
        if (movement.LengthSquared() > displacement.LengthSquared())
        {
            Position = TargetPosition;
            velocity = Vector3.Zero;
        }
        else Position += movement;
        float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Orientation, TargetOrientation)), 0, 1));
        Orientation = Quaternion.Normalize(Quaternion.Slerp(Orientation, TargetOrientation, angle < 0.00001f ? 1 : Math.Min(1, MaximumAngularSpeed * timestep / angle)));
    }

    private static Vector3 Limit(Vector3 vector, float maximum)
    {
        float length = vector.Length();
        return length > maximum ? vector * (maximum / length) : vector;
    }
}