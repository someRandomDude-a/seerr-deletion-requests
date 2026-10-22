using DeletionQueue.Core;
using DeletionQueue.Infrastructure;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class SeerrExecutionGateway(QueueRuntime runtime, TargetResolver targets, LiveIdentity identity,
    ActivityInventory inventory, ActivityFence fence, TimeProvider clock, ISeerrClientFactory seerrClients) : IExecutionGateway
{
    private long observedGeneration;
    public async Task<ExecutionPreflight> PreflightAsync(Proposal proposal, CancellationToken ct)
    {
        observedGeneration = fence.Generation;
        var owner = identity.Account(proposal.OwnerId);
        var resolved = await targets.ResolveAsync(owner, new TargetSelection(proposal.Target.JellyfinItemId, null, proposal.Target.Is4K), ct);
        var state = runtime.Read();
        var authority = QueueRules.Authority(state, owner.Id);
        var authorized = owner.Enabled && targets.Accessible(owner.Id, resolved.JellyfinItemId) &&
            (authority == ProposalAuthority.AnyAccessibleItem || authority == ProposalAuthority.OwnRequests && resolved.OwnRequestVerified);
        var scan = fence.Quiet && inventory.Scan(resolved) && fence.Quiet && observedGeneration == fence.Generation && runtime.CollectorHealthy;
        var latest = runtime.Read();
        var current = QueueRules.Find(latest, proposal.Id);
        return new(new EvaluationEvidence(authorized, resolved.Fingerprint == proposal.Target.Fingerprint, scan, runtime.Available, true,
            AdministratorAuthorized(latest, current)), current.Revision);
    }

    public Task<bool> AnchorClaimAsync(ExecutionClaim claim, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Write returned only after the authenticated DB transaction and local head seal were persisted.
        return Task.FromResult(runtime.Read().Proposals.Any(p => p.Claim == claim && p.Status == ProposalStatus.Claimed));
    }

    public async Task<bool> RecheckBeforeDispatchAsync(Proposal proposal, CancellationToken ct)
    {
        if (!fence.Quiet || observedGeneration != fence.Generation || !runtime.CollectorHealthy) return false;
        var current = await PreflightAsync(proposal, ct);
        var state = runtime.Read(); var p = QueueRules.Find(state, proposal.Id);
        // Evaluate a detached copy as pre-claim, without making a durable claim reusable.
        p.Claim = null;
        QueueRules.Evaluate(state, p, current.Evidence, clock.GetUtcNow());
        return p.Status == ProposalStatus.Ready && state.Policy.AutomaticDeletion && state.Policy.SeerrManagedDeletionAcknowledged &&
            current.ProposalRevision == proposal.Revision && fence.Quiet && observedGeneration == fence.Generation;
    }

    public async Task DeleteOnceAsync(Proposal proposal, CancellationToken ct)
    {
        if (!fence.Quiet || observedGeneration != fence.Generation) throw new QueueException("ActivityChangedBeforeDispatch");
        var state = runtime.Read(); var p = QueueRules.Find(state, proposal.Id);
        var owner = identity.Account(p.OwnerId);
        var authority = QueueRules.Authority(state, owner.Id);
        if (!owner.Enabled || !targets.Accessible(owner.Id, p.Target.JellyfinItemId) || authority == ProposalAuthority.Denied ||
            authority == ProposalAuthority.OwnRequests && !p.Target.OwnRequestVerified) throw new QueueException("OwnerNotAuthorized");
        if (p.Claim?.Id != proposal.Claim?.Id || p.Status != ProposalStatus.Claimed || !state.Policy.AutomaticDeletion ||
            !state.Policy.SeerrManagedDeletionAcknowledged || state.Policy.Suspended ||
            state.Seerr.Revision != p.Target.ConfigurationRevision) throw new QueueException("DispatchVerificationFailed");
        using var seerr = seerrClients.Create(state.Seerr);
        if (state.Policy.RequireAdministratorApproval && !AdministratorAuthorized(state, p)) throw new QueueException("AdministratorApprovalRequired");
        if (!fence.Quiet || observedGeneration != fence.Generation || !runtime.CollectorHealthy) throw new QueueException("ActivityChangedBeforeDispatch");
        await seerr.DeleteFilesOnceAsync(p.Target.SeerrMediaId, p.Target.Is4K, ct);
    }

    private bool AdministratorAuthorized(QueueState state, Proposal proposal)
    {
        if (!QueueRules.AdministratorApprovalCurrent(state, proposal) || proposal.AdministratorApproval is not { } approval) return false;
        var account = identity.Account(approval.UserId);
        return account.Enabled && account.Administrator && targets.Accessible(account.Id, proposal.Target.JellyfinItemId);
    }

    public async Task<DeletionProof?> InspectOutcomeAsync(Proposal proposal, CancellationToken ct)
    {
        var state = runtime.Read();
        if (state.Seerr.Revision != proposal.Target.ConfigurationRevision || proposal.Claim is null) return null;
        using var seerr = seerrClients.Create(state.Seerr);
        var media = await seerr.MediaRecordAsync(proposal.Target.SeerrMediaId, ct);
        if (!media.TryGetProperty("id", out var id) || !id.TryGetInt32(out var mediaId) || mediaId != proposal.Target.SeerrMediaId ||
            !media.TryGetProperty("tmdbId", out var tmdb) || !tmdb.TryGetInt32(out var tmdbId) ||
            proposal.Target.ActivityKey != $"{(proposal.Target.Kind == MediaKind.Series ? "tv" : "movie")}:{tmdbId}") return null;
        if (!media.TryGetProperty("status" + (proposal.Target.Is4K ? "4k" : ""), out var status) || !status.TryGetInt32(out var code) || code != 7) return null;
        return new(proposal.Target.Fingerprint, "Trusted Seerr reports DELETED for the claimed media/quality; Arr record and file removal delegated to Seerr. Request history retained.");
    }
}
