using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class QueueRuntime : IDisposable
{
    private readonly AuthenticatedStore? store;
    private int failed;
    private int collectorFailed;
    public bool Available => store is not null && Volatile.Read(ref failed) == 0;
    public bool CollectorHealthy => Volatile.Read(ref collectorFailed) == 0;
    public void MarkCollectorFailed() => Interlocked.Exchange(ref collectorFailed, 1);
    public const string ReleaseMode = "Safety preview — stock Seerr cannot pin file-set preconditions; independent rollback witness not configured. No deletion is sent.";

    public QueueRuntime(IApplicationPaths paths, ILogger<QueueRuntime> logger)
    {
        try { store = new AuthenticatedStore(Path.Combine(paths.DataPath, "deletion-queue")); }
        catch (Exception)
        {
            // Do not include file paths, keys or decrypted authorization records in logs.
            try { logger.LogError("Deletion queue storage unavailable or failed integrity validation. Original state preserved; no reset performed."); } catch (Exception) { }
        }
    }

    public QueueState Read() => Guard(() => RequireStore().Read());
    public IReadOnlyList<JournalEntry> History() => Guard(() => RequireStore().History());
    public T Write<T>(Guid command, string actor, string kind, Func<QueueState, T> update) =>
        Guard(() => RequireStore().Write(command, actor, kind, state =>
        {
            var revisions = state.Proposals.ToDictionary(p => p.Id, p => p.Revision);
            var result = update(state);
            // Notices and proposal changes commit together, including policy/coverage/deadline changes.
            NoticeRules.Publish(state, state.Proposals.Where(p => !revisions.TryGetValue(p.Id, out var revision) || p.Revision != revision));
            return result;
        }));
    private AuthenticatedStore RequireStore() => Available ? store! : throw new IntegrityException("Storage unavailable; authorization frozen.");
    private T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (QueueException e)
        {
            if (e.Code.EndsWith("CapacityReached", StringComparison.Ordinal)) Interlocked.Exchange(ref failed, 1);
            throw;
        }
        catch (Exception) { Interlocked.Exchange(ref failed, 1); throw new IntegrityException("Plugin unavailable; original state preserved."); }
    }
    public void Dispose() { try { store?.Dispose(); } catch (Exception) { Interlocked.Exchange(ref failed, 1); } }
}
