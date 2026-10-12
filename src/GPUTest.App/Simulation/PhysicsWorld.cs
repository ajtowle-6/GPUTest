using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GPUTest.App.Simulation;

[StructLayout(LayoutKind.Sequential)]
internal struct BallPose
{
    public Vector3 Position;
    public float Radius;
    public Quaternion Orientation;
}

internal interface IPhysicsWorld : IDisposable
{
    BallPose[] Poses { get; }
    int Escaped { get; }
    void Step(Vector3 position, Quaternion orientation);
    void CopyPoses();
}

internal sealed partial class PhysicsWorld : SafeHandleZeroOrMinusOneIsInvalid, IPhysicsWorld
{
    public const float Timestep = 1f / 120f;
    public const float HalfExtent = 5f;
    public const float Radius = 0.08f;
    public BallPose[] Poses { get; }
    public int Escaped { get; private set; }
    public bool CubeIsSleeping => CubeSleeping(this) != 0;

    public PhysicsWorld(int count) : base(true)
    {
        if (count is < 0 or > 100_000) throw new ArgumentOutOfRangeException(nameof(count));
        if (Marshal.SizeOf<BallPose>() != 32) throw new InvalidOperationException("Native pose layout mismatch.");
        Poses = new BallPose[count];
        SetHandle(Create(count, HalfExtent, Radius));
        if (IsInvalid) throw NativeError();
        try { CopyPoses(); }
        catch { Dispose(); throw; }
    }

    public unsafe void Step(Vector3 position, Quaternion orientation)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        float* target = stackalloc float[] { position.X, position.Y, position.Z, orientation.X, orientation.Y, orientation.Z, orientation.W };
        if (StepNative(this, Timestep, target) != 0) throw NativeError();
    }

    public unsafe void CopyPoses()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        fixed (BallPose* output = Poses)
        {
            if (Copy(this, output, Poses.Length, out int escaped) != 0) throw NativeError();
            Escaped = escaped;
        }
    }

    protected override bool ReleaseHandle() { Destroy(handle); return true; }
    private static Exception NativeError() => new InvalidOperationException(Marshal.PtrToStringUTF8(LastError()) ?? "Native physics error.");

    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_create")]
    private static partial nint Create(int count, float halfExtent, float radius);
    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_destroy")]
    private static partial void Destroy(nint world);
    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_last_error")]
    private static partial nint LastError();
    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_step")]
    private static unsafe partial int StepNative(PhysicsWorld world, float timestep, float* target);
    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_cube_sleeping")]
    private static partial int CubeSleeping(PhysicsWorld world);
    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_copy")]
    private static unsafe partial int Copy(PhysicsWorld world, BallPose* output, int capacity, out int escaped);
}