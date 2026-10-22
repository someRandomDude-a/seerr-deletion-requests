namespace DeletionQueue.Core;

public sealed record ExecutionPreflight(EvaluationEvidence Evidence, long ProposalRevision);
public sealed record DeletionProof(string Fingerprint, string Evidence);

public interface IExecutionGateway
{
    Task<ExecutionPreflight> PreflightAsync(Proposal proposal, CancellationToken cancellationToken);
    Task<bool> AnchorClaimAsync(ExecutionClaim claim, CancellationToken cancellationToken);
    Task<bool> RecheckBeforeDispatchAsync(Proposal proposal, CancellationToken cancellationToken);
    Task DeleteOnceAsync(Proposal proposal, CancellationToken cancellationToken);
    Task<DeletionProof?> InspectOutcomeAsync(Proposal proposal, CancellationToken cancellationToken);
}

/// <summary>One durable claim per destructive attempt; uncertain outcomes are never resent.</summary>
public sealed class ExecutionCoordinator(IStateStore store, IExecutionGateway gateway, TimeProvider clock)
{
    public async Task ExecuteAsync(Guid id, CancellationToken cancellationToken)
    {
        var proposal = QueueRules.Find(store.Read(), id);
        if (proposal.Claim is not null) throw new QueueException("ClaimAlreadyConsumed");
        var preflight = await gateway.PreflightAsync(proposal, cancellationToken);
        if (preflight.ProposalRevision != proposal.Revision) throw new QueueException("RevisionChanged");
        var claim = store.Write(Guid.NewGuid(), "scheduler", "ExecutionClaimed", state =>
            QueueRules.Claim(state, id, preflight.ProposalRevision, preflight.Evidence, clock.GetUtcNow()));
        try
        {
            proposal = QueueRules.Find(store.Read(), id);
            if (!await gateway.AnchorClaimAsync(claim, cancellationToken) ||
                !await gateway.RecheckBeforeDispatchAsync(proposal, cancellationToken))
                throw new QueueException("DispatchVerificationFailed");
            await gateway.DeleteOnceAsync(proposal, cancellationToken);
            var proof = await gateway.InspectOutcomeAsync(proposal, cancellationToken);
            if (proof is null) throw new QueueException("OutcomeUnverified");
            store.Write(Guid.NewGuid(), "executor", "FilesDeleted", state =>
            { QueueRules.FilesDeleted(state, id, proof.Fingerprint, proof.Evidence, clock.GetUtcNow()); return true; });
        }
        catch
        {
            // A storage/integrity failure here is allowed to propagate; the durable claim still forbids a retry.
            store.Write(Guid.NewGuid(), "executor", "ExecutionUncertain", state =>
            { QueueRules.Uncertain(state, id); return true; });
            return;
        }
        store.Write(Guid.NewGuid(), "executor", "MonitoringCleanupNotApplicable", state =>
        { QueueRules.Complete(state, id); return true; });
    }

    public void RecoverClaims()
    {
        var pending = store.Read().Proposals.Where(p => p.Status == ProposalStatus.Claimed).Select(p => p.Id).ToArray();
        foreach (var id in pending)
            store.Write(Guid.NewGuid(), "recovery", "ExecutionUncertain", state =>
            { QueueRules.Uncertain(state, id); return true; });
    }

    // An explicit admin command, never a monitoring GET. It performs no deletion.
    public async Task ReconcileAsync(Guid id, CancellationToken cancellationToken)
    {
        var p = QueueRules.Find(store.Read(), id);
        if (p.Status != ProposalStatus.Uncertain) throw new QueueException("NotUncertain");
        var proof = await gateway.InspectOutcomeAsync(p, cancellationToken);
        if (proof is null) throw new QueueException("OutcomeUnverified");
        store.Write(Guid.NewGuid(), "reconciliation", "FilesDeleted", state =>
        { QueueRules.FilesDeleted(state, id, proof.Fingerprint, proof.Evidence, clock.GetUtcNow()); return true; });
    }
}
