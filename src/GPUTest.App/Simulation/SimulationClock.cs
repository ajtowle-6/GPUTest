namespace GPUTest.App.Simulation;

internal sealed class SimulationClock
{
    public const double FrameBudgetSeconds = 0.008;
    public const int MaximumStepsPerFrame = 8;
    public double PendingSeconds { get; private set; }
    public bool IsSlowingDown { get; private set; }
    private int steps;

    public void BeginFrame(double elapsedSeconds)
    {
        PendingSeconds += elapsedSeconds;
        steps = 0;
    }

    public bool TryTakeStep(double workSeconds)
    {
        if (PendingSeconds < PhysicsWorld.Timestep || steps >= MaximumStepsPerFrame
            || (steps > 0 && workSeconds >= FrameBudgetSeconds)) return false;
        PendingSeconds -= PhysicsWorld.Timestep;
        steps++;
        return true;
    }

    public void EndFrame()
    {
        IsSlowingDown = PendingSeconds >= PhysicsWorld.Timestep;
        if (IsSlowingDown) PendingSeconds %= PhysicsWorld.Timestep;
    }

    public void Reset()
    {
        PendingSeconds = 0;
        steps = 0;
        IsSlowingDown = false;
    }
}