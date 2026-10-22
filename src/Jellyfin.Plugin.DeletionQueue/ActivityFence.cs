namespace Jellyfin.Plugin.DeletionQueue;

// Event callbacks invalidate in-flight preflights before deferred journal processing catches up.
public sealed class ActivityFence
{
    private long generation;
    private int pending;
    public long Generation => Interlocked.Read(ref generation);
    public bool Quiet => Volatile.Read(ref pending) == 0;
    public void Capturing() { Interlocked.Increment(ref pending); Interlocked.Increment(ref generation); }
    public void Captured() => Interlocked.Decrement(ref pending);
}
