using System.Numerics;
using System.Runtime.InteropServices;
using GPUTest.App;
using GPUTest.App.Simulation;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GPUTest.Tests;

public sealed class PhysicsTests
{
    [Fact]
    public void SimulationClockContinuesAfterOverloadAndRecovers()
    {
        var clock = new SimulationClock();
        clock.BeginFrame(0.75);
        Assert.True(clock.TryTakeStep(0));
        Assert.False(clock.TryTakeStep(0.1));
        clock.EndFrame();
        Assert.True(clock.IsSlowingDown);
        Assert.InRange(clock.PendingSeconds, 0, PhysicsWorld.Timestep);

        clock.BeginFrame(PhysicsWorld.Timestep);
        Assert.True(clock.TryTakeStep(0));
        Assert.False(clock.TryTakeStep(0.001));
        clock.EndFrame();
        Assert.False(clock.IsSlowingDown);
    }

    [Fact]
    public void SimulationClockPreservesNormalFixedStepsAndFraction()
    {
        var clock = new SimulationClock();
        clock.BeginFrame(PhysicsWorld.Timestep * 2.5);
        Assert.True(clock.TryTakeStep(0));
        Assert.True(clock.TryTakeStep(0.003));
        Assert.False(clock.TryTakeStep(0.006));
        clock.EndFrame();
        Assert.False(clock.IsSlowingDown);
        Assert.Equal(PhysicsWorld.Timestep * 0.5, clock.PendingSeconds, 8);
        clock.BeginFrame(PhysicsWorld.Timestep * 0.5);
        Assert.True(clock.TryTakeStep(0));
        clock.EndFrame();
        Assert.Equal(0, clock.PendingSeconds);
    }

    [Fact]
    public void SimulationClockBoundsCatchUpAndResetsOnPause()
    {
        var clock = new SimulationClock();
        clock.BeginFrame(1);
        for (int step = 0; step < SimulationClock.MaximumStepsPerFrame; step++)
            Assert.True(clock.TryTakeStep(0));
        Assert.False(clock.TryTakeStep(0));
        clock.EndFrame();
        Assert.True(clock.IsSlowingDown);
        clock.Reset();
        Assert.False(clock.IsSlowingDown);
        Assert.Equal(0, clock.PendingSeconds);
        Assert.False(clock.TryTakeStep(0));
    }

    [Fact]
    public void PoseLayoutMatchesNativeAbi()
    {
        Assert.Equal(32, Marshal.SizeOf<BallPose>());
        Assert.Equal(12, Marshal.OffsetOf<BallPose>(nameof(BallPose.Radius)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<BallPose>(nameof(BallPose.Orientation)).ToInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void InvalidBallCountsAreRejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsWorld(count));
    }

    [Fact]
    public void CubeControllerBoundsLinearAndAngularMotion()
    {
        var controller = new CubeController
        {
            TargetPosition = new Vector3(3, 2, 1),
            TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1)
        };
        for (int step = 0; step < 360; step++)
        {
            Vector3 previous = controller.Position;
            Quaternion rotation = controller.Orientation;
            controller.Advance();
            Assert.InRange(Vector3.Distance(controller.Position, previous), 0, CubeController.MaximumLinearSpeed * PhysicsWorld.Timestep + 0.0001f);
            float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rotation, controller.Orientation)), 0, 1));
            Assert.InRange(angle, 0, CubeController.MaximumAngularSpeed * PhysicsWorld.Timestep + 0.001f);
        }
        Assert.True(Vector3.Distance(controller.Position, controller.TargetPosition) < 0.01f);
    }

    [Fact]
    public void CubeControllerTracksQuickDragWithoutLongTail()
    {
        var controller = new CubeController();
        for (int step = 0; step < 96; step++)
        {
            float direction = step < 48 ? 1 : -1;
            controller.TargetPosition += Vector3.UnitX * (direction * 6 * PhysicsWorld.Timestep);
            controller.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (step + 1) * PhysicsWorld.Timestep);
            controller.Advance();
            Assert.InRange(Vector3.Distance(controller.Position, controller.TargetPosition), 0, 0.85f);
            Assert.True(MathF.Abs(Quaternion.Dot(controller.Orientation, controller.TargetOrientation)) > 0.9999f);
        }
        for (int step = 0; step < 12; step++) controller.Advance();
        Assert.InRange(Vector3.Distance(controller.Position, controller.TargetPosition), 0, 0.001f);
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void EmptySceneCanBeSteppedAndRecreated()
    {
        for (int repeat = 0; repeat < 2; repeat++)
        {
            using var world = new PhysicsWorld(0);
            world.Step(Vector3.Zero, Quaternion.Identity);
            world.CopyPoses();
            Assert.Empty(world.Poses);
            Assert.Equal(0, world.Escaped);
        }
    }

    [Theory]
    [InlineData(644)]
    [InlineData(824)]
    [InlineData(1440)]
    public void DragTranslationMatchesScreenPixels(int height)
    {
        float width = height * 16f / 9;
        Vector2 pixels = new(180, -90);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(new Vector3(13, 10, 19), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, width / height, 0.1f, 100);
        foreach (Vector3 position in new[] { Vector3.Zero, new Vector3(2, -1, 3) })
        {
            Vector3 movement = MainWindow.DragTranslation(pixels, height, position);
            Vector4 before = Vector4.Transform(new Vector4(position, 1), viewProjection);
            Vector4 after = Vector4.Transform(new Vector4(position + movement, 1), viewProjection);
            Vector2 screenMovement = new((after.X / after.W - before.X / before.W) * width / 2,
                -(after.Y / after.W - before.Y / before.W) * height / 2);
            Assert.InRange(Vector2.Distance(pixels, screenMovement), 0, 0.01f);
        }
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void FastDragAndReversalRetainsBalls()
    {
        using var world = new PhysicsWorld(10_000);
        var controller = new CubeController();
        for (int step = 0; step < 240; step++)
        {
            controller.TargetPosition = new Vector3(2 * MathF.Sin(step * 0.07f), 0.5f * MathF.Sin(step * 0.09f), 0);
            controller.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f * MathF.Sin(step * 0.06f));
            controller.Advance();
            world.Step(controller.Position, controller.Orientation);
            world.CopyPoses();
            Assert.Equal(0, world.Escaped);
        }
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void SingleBallSettlesOnBottomWall()
    {
        using var world = new PhysicsWorld(1);
        for (int step = 0; step < 120; step++) world.Step(Vector3.Zero, Quaternion.Identity);
        world.CopyPoses();
        Assert.InRange(world.Poses[0].Position.Y, -PhysicsWorld.HalfExtent + PhysicsWorld.Radius - 0.01f, -PhysicsWorld.HalfExtent + PhysicsWorld.Radius + 0.01f);
        Assert.Equal(0, world.Escaped);
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void MovingRotatingCubeTransfersMotionAndRetainsBalls()
    {
        using var world = new PhysicsWorld(1000);
        Vector3 initial = world.Poses[0].Position;
        var controller = new CubeController { TargetPosition = new Vector3(1, 0.3f, 0), TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f) };
        for (int step = 0; step < 180; step++)
        {
            controller.Advance();
            world.Step(controller.Position, controller.Orientation);
            world.CopyPoses();
            Assert.Equal(0, world.Escaped);
        }
        Assert.True(Vector3.Distance(initial, world.Poses[0].Position) > 0.1f);
        Assert.All(world.Poses, pose => Assert.True(float.IsFinite(pose.Position.LengthSquared())));
    }
}