using DeletionQueue.Core;
using DeletionQueue.Infrastructure;

namespace DeletionQueue.Tests;

public sealed class ExecutionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dq-execution-tests-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Gateway : IExecutionGateway
    {
        public int Deletes;
        public bool Timeout;
        public bool Anchored = true;
        public bool Rechecked = true;
        public bool Proof = true;
        public EvaluationEvidence Evidence = QueueRulesTests.Good;
        public Task<ExecutionPreflight> PreflightAsync(Proposal p, CancellationToken ct) => Task.FromResult(new ExecutionPreflight(Evidence, p.Revision));
        public Task<bool> AnchorClaimAsync(ExecutionClaim c, CancellationToken ct) => Task.FromResult(Anchored);
        public Task<bool> RecheckBeforeDispatchAsync(Proposal p, CancellationToken ct) => Task.FromResult(Rechecked);
        public Task DeleteOnceAsync(Proposal p, CancellationToken ct) { Deletes++; if (Timeout) throw new TimeoutException(); return Task.CompletedTask; }
        public Task<DeletionProof?> InspectOutcomeAsync(Proposal p, CancellationToken ct) => Task.FromResult(Proof ? new DeletionProof(p.Target.Fingerprint, "verified-files-absent") : null);
    }

    private static Guid Seed(AuthenticatedStore store)
    {
        return store.Write(Guid.NewGuid(), "fixture", "Seed", state =>
        {
            state.CoverageHealthy = true; state.CoverageSince = QueueRulesTests.Now.AddDays(-30);
            state.LastHeartbeat = QueueRulesTests.Now.AddHours(48); state.Policy.AutomaticDeletion = true;
            state.Policy.SeerrManagedDeletionAcknowledged = true;
            var p = QueueRulesTests.Propose(state); QueueRulesTests.Evaluate(state, p);
            state.LastHeartbeat = QueueRulesTests.Now.AddHours(48); return p.Id;
        });
    }

    [Fact] public async Task SuccessPersistsSeparateFilesDeletedBeforeCompletion()
    {
        using var store = new AuthenticatedStore(directory); var id = Seed(store); var gateway = new Gateway();
        await new ExecutionCoordinator(store, gateway, new Clock(QueueRulesTests.Now.AddHours(48))).ExecuteAsync(id, default);
        Assert.Equal(1, gateway.Deletes); Assert.Equal(ProposalStatus.Completed, QueueRules.Find(store.Read(), id).Status);
        var history = store.History().Select(x => x.Kind).ToArray();
        Assert.True(Array.IndexOf(history, "FilesDeleted") < Array.IndexOf(history, "MonitoringCleanupNotApplicable"));
    }

    [Fact] public async Task TimeoutNeverAutomaticallyResendsEvenAfterRestart()
    {
        var gateway = new Gateway { Timeout = true }; Guid id;
        using (var store = new AuthenticatedStore(directory))
        {
            id = Seed(store); var coordinator = new ExecutionCoordinator(store, gateway, new Clock(QueueRulesTests.Now.AddHours(48)));
            await coordinator.ExecuteAsync(id, default);
            Assert.Equal(ProposalStatus.Uncertain, QueueRules.Find(store.Read(), id).Status);
            await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default));
        }
        using var reopened = new AuthenticatedStore(directory);
        var recovered = new ExecutionCoordinator(reopened, gateway, new Clock(QueueRulesTests.Now.AddHours(48)));
        recovered.RecoverClaims(); await Assert.ThrowsAsync<QueueException>(() => recovered.ExecuteAsync(id, default));
        Assert.Equal(1, gateway.Deletes);
    }

    [Theory] [InlineData(false, true)] [InlineData(true, false)]
    public async Task FailedAnchorOrFinalRecheckConsumesClaimWithoutSending(bool anchor, bool recheck)
    {
        using var store = new AuthenticatedStore(directory); var id = Seed(store);
        var gateway = new Gateway { Anchored = anchor, Rechecked = recheck };
        var coordinator = new ExecutionCoordinator(store, gateway, new Clock(QueueRulesTests.Now.AddHours(48)));
        await coordinator.ExecuteAsync(id, default); Assert.Equal(0, gateway.Deletes);
        Assert.Equal(ProposalStatus.Uncertain, QueueRules.Find(store.Read(), id).Status);
        await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default));
    }

    [Fact] public async Task MissingRemoteCapabilitiesPreventsEvenAClaim()
    {
        using var store = new AuthenticatedStore(directory); var id = Seed(store);
        var gateway = new Gateway { Evidence = QueueRulesTests.Good with { SeerrDelegationVerified = false } };
        await Assert.ThrowsAsync<QueueException>(() => new ExecutionCoordinator(store, gateway, new Clock(QueueRulesTests.Now.AddHours(48))).ExecuteAsync(id, default));
        Assert.Null(QueueRules.Find(store.Read(), id).Claim); Assert.Equal(0, gateway.Deletes);
    }

    [Fact] public async Task UnverifiedSuccessRemainsUncertainAndReconciliationNeverDeletes()
    {
        using var store = new AuthenticatedStore(directory); var id = Seed(store); var gateway = new Gateway { Proof = false };
        var coordinator = new ExecutionCoordinator(store, gateway, new Clock(QueueRulesTests.Now.AddHours(48)));
        await coordinator.ExecuteAsync(id, default); Assert.Equal(ProposalStatus.Uncertain, QueueRules.Find(store.Read(), id).Status);
        gateway.Proof = true; await coordinator.ReconcileAsync(id, default);
        Assert.Equal(ProposalStatus.FilesDeleted, QueueRules.Find(store.Read(), id).Status); Assert.Equal(1, gateway.Deletes);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
