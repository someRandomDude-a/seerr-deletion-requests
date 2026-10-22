using System.Security.Cryptography;
using System.Text;

namespace DeletionQueue.Core;

public static class QueueRules
{
    public static ProposalAuthority Authority(QueueState state, Guid user) =>
        state.Policy.Accounts.GetValueOrDefault(user, state.Policy.DefaultAuthority);

    public static void RequireProposalAuthority(QueueState state, Account caller, VerifiedTarget target, bool accessible)
    {
        if (!caller.Enabled || !accessible || !target.MappingVerified || !Enum.IsDefined(target.Kind) ||
            target.SeerrMediaId <= 0 || target.JellyfinItemId == Guid.Empty || string.IsNullOrWhiteSpace(target.Key) ||
            string.IsNullOrWhiteSpace(target.Fingerprint) || string.IsNullOrWhiteSpace(target.ActivityKey))
            throw new QueueException("TargetUnavailable");
        var authority = Authority(state, caller.Id);
        if (authority == ProposalAuthority.Denied ||
            (authority == ProposalAuthority.OwnRequests && !target.OwnRequestVerified))
            throw new QueueException("ProposalNotPermitted");
    }

    public static Proposal Propose(QueueState state, Account caller, VerifiedTarget target,
        bool accessible, string expectedSnapshot, DateTimeOffset now)
    {
        RequireProposalAuthority(state, caller, target, accessible);
        var proposal = state.Proposals.SingleOrDefault(p => !p.Terminal && (p.Target.Key == target.Key ||
            (target.DestructiveScopeKey is not null && p.Target.DestructiveScopeKey == target.DestructiveScopeKey)));
        if (proposal?.Claim is not null) throw new QueueException("AlreadyClaimed");
        if (proposal is not null && proposal.Target.Fingerprint != target.Fingerprint)
            throw new QueueException("TargetChanged");
        if (Snapshot(state, target, caller.Id) != expectedSnapshot) throw new QueueException("SnapshotChanged");
        if (proposal is null)
        {
            if (state.Proposals.Count >= 1000) throw new QueueException("ProposalCapacityReached");
            proposal = new Proposal { OwnerId = caller.Id, Target = target, CreatedAt = now };
            state.Proposals.Add(proposal);
        }
        if (proposal.Consents.Count >= 1000 && !proposal.Consents.ContainsKey(caller.Id)) throw new QueueException("ConsentCapacityReached");
        proposal.Consents[caller.Id] = new Consent(Answer.Yes, expectedSnapshot, now);
        ExtendDelay(state, proposal, caller.Id);
        ExtendDeadline(proposal);
        proposal.Revision++;
        return proposal;
    }

    public static void Consent(QueueState state, Guid id, Account caller, long revision,
        string snapshot, Answer answer, bool accessible, DateTimeOffset now)
    {
        if (!caller.Enabled || !accessible) throw new QueueException("TargetUnavailable");
        var proposal = Find(state, id);
        RequireMutable(proposal, revision);
        if (Snapshot(state, proposal.Target, caller.Id) != snapshot) throw new QueueException("SnapshotChanged");
        if (proposal.Consents.Count >= 1000 && !proposal.Consents.ContainsKey(caller.Id)) throw new QueueException("ConsentCapacityReached");
        proposal.Consents[caller.Id] = new Consent(answer, snapshot, now);
        ExtendDelay(state, proposal, caller.Id);
        // Conservatively break the epoch on No even before the next evaluator pass.
        if (answer == Answer.No && IsRecent(state, proposal.Target, caller.Id, now)) Block(proposal, "ConsentRequired");
        proposal.Revision++;
    }

    public static void Undo(QueueState state, Guid id, Guid caller, long revision)
    {
        var proposal = Find(state, id);
        if (proposal.OwnerId != caller) throw new QueueException("OwnerOnly");
        RequireMutable(proposal, revision);
        proposal.Status = ProposalStatus.Cancelled;
        proposal.Reason = "Cancelled";
        proposal.EligibleSince = null;
        proposal.Revision++;
    }

    public static string AdministratorSnapshot(QueueState state, Proposal proposal)
    {
        var activity = string.Join('|', state.Activity.Where(a => a.Key == proposal.Target.ActivityKey)
            .OrderBy(a => a.UserId).Select(a => $"{a.UserId:D}:{a.Revision}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{proposal.Id:D}|{proposal.Target.Fingerprint}|{state.Policy.Revision}|{activity}")));
    }

    public static bool AdministratorApprovalCurrent(QueueState state, Proposal proposal) =>
        proposal.AdministratorApproval is { } approval && approval.Snapshot == AdministratorSnapshot(state, proposal);

    public static void AdministratorDecision(QueueState state, Guid id, Account caller, long revision,
        string snapshot, bool approved, bool accessible, DateTimeOffset now)
    {
        if (!caller.Enabled || !caller.Administrator || !accessible) throw new QueueException("AccessDenied");
        if (!state.Policy.RequireAdministratorApproval) throw new QueueException("AdministratorApprovalNotRequired");
        var p = Find(state, id);
        RequireMutable(p, revision);
        if (snapshot != AdministratorSnapshot(state, p)) throw new QueueException("SnapshotChanged");
        p.AdministratorApproval = approved ? new(caller.Id, snapshot, now) : null;
        Block(p, approved ? "VerificationRequired" : "AdministratorApprovalRequired");
        p.Revision++;
    }

    public static void RecordActivity(QueueState state, Guid user, string key, string? session,
        bool stopped, DateTimeOffset now)
    {
        var activity = state.Activity.SingleOrDefault(a => a.UserId == user && a.Key == key);
        if (activity is null)
        {
            if (state.Activity.Count >= 10000) throw new QueueException("ActivityCapacityReached");
            activity = new Activity { UserId = user, Key = key };
            state.Activity.Add(activity);
        }
        activity.Revision++;
        activity.At = now;
        if (!string.IsNullOrEmpty(session))
        {
            if (stopped) activity.Sessions.Remove(session);
            else { if (activity.Sessions.Count >= 1024 && !activity.Sessions.Contains(session)) throw new QueueException("SessionCapacityReached"); activity.Sessions.Add(session); }
        }
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null && p.Target.ActivityKey == key))
        {
            Block(p, "ConsentRequired");
            p.Revision++;
        }
    }

    public static string Snapshot(QueueState state, VerifiedTarget target, Guid user)
    {
        var activity = state.Activity.SingleOrDefault(a => a.UserId == user && a.Key == target.ActivityKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{target.Fingerprint}|{user:D}|{activity?.Revision ?? 0}")));
    }

    public static void Evaluate(QueueState state, Proposal proposal, EvaluationEvidence evidence, DateTimeOffset now)
    {
        if (proposal.Terminal || proposal.Claim is not null) return;
        var reason = !state.ClockHealthy ? "ClockUnverified"
            : state.Policy.Suspended ? "Suspended"
            : !evidence.OwnerAuthorized ? "OwnerNotAuthorized"
            : !evidence.TargetVerified ? "TargetVerificationRequired"
            : !CoverageComplete(state, now) || !evidence.CompleteActivityScan ? "ActivityCoverageIncomplete"
            : !evidence.LocalCheckpointVerified ? "LocalCheckpointRequired"
            : !evidence.SeerrDelegationVerified ? "SeerrDelegationRequired" : null;
        var viewers = state.Activity.Where(a => a.Key == proposal.Target.ActivityKey &&
            (a.At >= now.AddDays(-state.Policy.ActivityDays) || a.Sessions.Count > 0)).ToList();
        foreach (var viewer in viewers) ExtendDelay(state, proposal, viewer.UserId);
        foreach (var participant in proposal.Consents.Keys) ExtendDelay(state, proposal, participant);
        proposal.DelayFloorHours = Math.Max(proposal.DelayFloorHours, state.Policy.DelayHours);
        reason ??= viewers.Any(v => v.Sessions.Count > 0) ? "PlaybackActive" : null;
        reason ??= viewers.Any(v => !proposal.Consents.TryGetValue(v.UserId, out var consent) ||
            consent.Answer != Answer.Yes || consent.Snapshot != Snapshot(state, proposal.Target, v.UserId))
             ? "ConsentRequired" : null;
        reason ??= state.Policy.RequireAdministratorApproval &&
            (!AdministratorApprovalCurrent(state, proposal) || !evidence.AdministratorAuthorized)
            ? "AdministratorApprovalRequired" : null;
        if (reason is not null) { Block(proposal, reason); return; }
        proposal.EligibleSince ??= now;
        var deadline = proposal.EligibleSince.Value.AddHours(proposal.DelayFloorHours);
        if (proposal.NotBefore is null || deadline > proposal.NotBefore) proposal.NotBefore = deadline;
        proposal.Status = now >= proposal.NotBefore ? ProposalStatus.Ready : ProposalStatus.Waiting;
        proposal.Reason = proposal.Status == ProposalStatus.Ready ? "Ready" : "DelayRequired";
    }

    public static ExecutionClaim Claim(QueueState state, Guid id, long revision, EvaluationEvidence evidence, DateTimeOffset now)
    {
        var proposal = Find(state, id);
        RequireMutable(proposal, revision);
        Evaluate(state, proposal, evidence, now);
        if (!state.Policy.AutomaticDeletion || !state.Policy.SeerrManagedDeletionAcknowledged || proposal.Status != ProposalStatus.Ready)
            throw new QueueException("ExecutionBlocked");
        proposal.Claim = new ExecutionClaim(Guid.NewGuid(), now, proposal.Target.Fingerprint, proposal.Revision);
        proposal.Status = ProposalStatus.Claimed;
        proposal.Reason = "Claimed";
        proposal.Revision++;
        return proposal.Claim;
    }

    public static void Uncertain(QueueState state, Guid id)
    {
        var p = Find(state, id);
        if (p.Claim is null || p.Terminal || p.FilesDeletedAt is not null) throw new QueueException("InvalidCheckpoint");
        p.Status = ProposalStatus.Uncertain;
        p.Reason = "OutcomeUncertain";
        p.Revision++;
    }

    public static void FilesDeleted(QueueState state, Guid id, string fingerprint, string evidence, DateTimeOffset now)
    {
        var p = Find(state, id);
        if (p.Claim is null || p.Terminal || p.Claim.TargetFingerprint != fingerprint || string.IsNullOrWhiteSpace(evidence))
            throw new QueueException("InvalidCheckpoint");
        if (p.FilesDeletedAt is not null) throw new QueueException("CheckpointAlreadyRecorded");
        p.FilesDeletedAt = now;
        p.DeletionEvidence = evidence;
        p.Status = ProposalStatus.FilesDeleted;
        p.Reason = "FilesDeleted";
        p.Revision++;
    }

    public static void Complete(QueueState state, Guid id)
    {
        var p = Find(state, id);
        if (p.FilesDeletedAt is null || p.Status != ProposalStatus.FilesDeleted) throw new QueueException("FilesDeletedCheckpointRequired");
        p.CleanupOutcome = "NotApplicableArrRecordRemovedBySeerr";
        p.Status = ProposalStatus.Completed;
        p.Reason = "Completed";
        p.Revision++;
    }

    public static void UpdatePolicy(QueueState state, Policy policy, long expectedRevision)
    {
        if (state.Policy.Revision != expectedRevision) throw new QueueException("RevisionChanged");
        if (policy is null || policy.Accounts is null || policy.Accounts.Count > 1000 || policy.Accounts.ContainsKey(Guid.Empty) ||
            policy.ActivityDays is < 1 or > 365 || policy.DelayHours is < 48 or > 8760 ||
            !Enum.IsDefined(policy.DefaultAuthority) || policy.Accounts.Values.Any(v => !Enum.IsDefined(v)))
            throw new QueueException("InvalidPolicy");
        var oldPolicy = state.Policy;
        var windowChanged = oldPolicy.ActivityDays != policy.ActivityDays;
        policy.Revision = expectedRevision + 1;
        state.Policy = policy;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null))
        {
            p.DelayFloorHours = Math.Max(p.DelayFloorHours, policy.DelayHours);
            var oldAuthority = oldPolicy.Accounts.GetValueOrDefault(p.OwnerId, oldPolicy.DefaultAuthority);
            if (windowChanged || policy.Suspended || policy.RequireAdministratorApproval || oldAuthority != Authority(state, p.OwnerId)) Block(p, "PolicyChanged");
            else ExtendDeadline(p);
            p.Revision++;
        }
    }

    public static void UpdatePreferences(QueueState state, Guid user, long revision, int hours, bool notifications)
    {
        if (hours is < 48 or > 8760) throw new QueueException("InvalidDelay");
        var preferences = state.Preferences.GetValueOrDefault(user) ?? new Preferences();
        if (preferences.Revision != revision) throw new QueueException("RevisionChanged");
        preferences.DelayHours = hours;
        preferences.Notifications = notifications;
        preferences.Revision++;
        state.Preferences[user] = preferences;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null &&
                     (p.OwnerId == user || p.Consents.ContainsKey(user) || state.Activity.Any(a => a.UserId == user && a.Key == p.Target.ActivityKey))))
        {
            ExtendDelay(state, p, user);
            ExtendDeadline(p);
            p.Revision++;
        }
    }

    public static void Subscription(QueueState state, Guid id, Guid user, long revision, bool subscribed)
    {
        _ = Find(state, id);
        var pref = state.Preferences.GetValueOrDefault(user) ?? new Preferences();
        if (pref.Revision != revision) throw new QueueException("RevisionChanged");
        if (subscribed && pref.Subscriptions.Count >= 1000 && !pref.Subscriptions.Contains(id)) throw new QueueException("SubscriptionCapacityReached");
        if (subscribed) pref.Subscriptions.Add(id); else pref.Subscriptions.Remove(id);
        pref.Revision++;
        state.Preferences[user] = pref;
    }

    public static void ResetUser(QueueState state, Guid user, long policyRevision, long preferencesRevision)
    {
        if (user == Guid.Empty) throw new QueueException("NotFound");
        if ((state.Preferences.GetValueOrDefault(user)?.Revision ?? 0) != preferencesRevision)
            throw new QueueException("RevisionChanged");
        var old = state.Policy;
        var accounts = new Dictionary<Guid, ProposalAuthority>(old.Accounts);
        accounts.Remove(user);
        UpdatePolicy(state, new Policy { Accounts = accounts, DefaultAuthority = old.DefaultAuthority,
            ActivityDays = old.ActivityDays, DelayHours = old.DelayHours,
             AutomaticDeletion = old.AutomaticDeletion, SeerrManagedDeletionAcknowledged = old.SeerrManagedDeletionAcknowledged,
            RequireAdministratorApproval = old.RequireAdministratorApproval,
            Suspended = old.Suspended }, policyRevision);
        UpdatePreferences(state, user, preferencesRevision, 48, true);
        state.Preferences[user].Subscriptions.Clear();
    }

    public static UserProposal Project(QueueState state, Proposal p, Guid caller, DateTimeOffset now)
    {
        var snapshot = Snapshot(state, p.Target, caller);
        p.Consents.TryGetValue(caller, out var consent);
        var answer = consent?.Snapshot == snapshot ? consent.Answer : (Answer?)null;
        var ownActivity = state.Activity.SingleOrDefault(a => a.UserId == caller && a.Key == p.Target.ActivityKey);
        var action = !p.Terminal && p.Claim is null && IsRecent(state, p.Target, caller, now) && answer != Answer.Yes
            ? "ApproveCurrentSnapshot" : "None";
        return new UserProposal(p.Id, p.Target.Title, p.OwnerId == caller ? "Yours" : "Anonymous",
            p.Status.ToString(), p.Reason, p.Revision, p.NotBefore, p.OwnerId == caller && !p.Terminal && p.Claim is null,
            snapshot, answer, ownActivity?.At, action);
    }

    public static bool CoverageComplete(QueueState state, DateTimeOffset now) => state.CoverageHealthy &&
        state.CoverageSince <= now.AddDays(-state.Policy.ActivityDays) && state.LastHeartbeat >= now.AddMinutes(-2);

    public static Proposal Find(QueueState state, Guid id) => state.Proposals.SingleOrDefault(p => p.Id == id)
        ?? throw new QueueException("NotFound");

    public static void BreakCoverage(QueueState state, string reason, DateTimeOffset now)
    {
        state.CoverageSince = now;
        state.CoverageHealthy = false;
        state.CoverageProblem = reason;
        foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null))
        { Block(p, "ActivityCoverageIncomplete"); p.Revision++; }
    }

    private static bool IsRecent(QueueState state, VerifiedTarget target, Guid user, DateTimeOffset now) =>
        state.Activity.Any(a => a.UserId == user && a.Key == target.ActivityKey &&
            (a.At >= now.AddDays(-state.Policy.ActivityDays) || a.Sessions.Count > 0));

    private static void RequireMutable(Proposal p, long revision)
    {
        if (p.Revision != revision) throw new QueueException("RevisionChanged");
        if (p.Terminal || p.Claim is not null) throw new QueueException("AlreadyClaimedOrClosed");
    }

    private static void ExtendDelay(QueueState state, Proposal p, Guid user) => p.DelayFloorHours = Math.Max(
        p.DelayFloorHours, Math.Max(state.Policy.DelayHours, state.Preferences.GetValueOrDefault(user)?.DelayHours ?? 48));

    private static void ExtendDeadline(Proposal p)
    {
        if (p.EligibleSince is not { } since) return;
        var next = since.AddHours(p.DelayFloorHours);
        if (p.NotBefore is null || next > p.NotBefore) p.NotBefore = next;
    }

    private static void Block(Proposal p, string reason)
    { p.Status = ProposalStatus.Blocked; p.Reason = reason; p.EligibleSince = null; }
}
