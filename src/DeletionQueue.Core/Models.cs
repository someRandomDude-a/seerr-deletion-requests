using System.Text.Json.Serialization;

namespace DeletionQueue.Core;

public enum ProposalAuthority { Denied, OwnRequests, AnyAccessibleItem }
public enum MediaKind { Movie, Series }
public enum ProposalStatus { Blocked, Waiting, Ready, Claimed, Uncertain, FilesDeleted, Completed, Cancelled }
public enum Answer { Yes, No }

public sealed class QueueException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record Account(Guid Id, bool Enabled, bool Administrator = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Policy
{
    public long Revision { get; set; }
    public ProposalAuthority DefaultAuthority { get; set; } = ProposalAuthority.OwnRequests;
    public Dictionary<Guid, ProposalAuthority> Accounts { get; set; } = [];
    public int ActivityDays { get; set; } = 14;
    public int DelayHours { get; set; } = 48;
    public bool AutomaticDeletion { get; set; }
    public bool Suspended { get; set; }
}

public sealed class Preferences
{
    public long Revision { get; set; }
    public int DelayHours { get; set; } = 48;
    public bool Notifications { get; set; }
    public HashSet<Guid> Subscriptions { get; set; } = [];
}

// Constructed by a trusted adapter, never bound from an HTTP request body.
public sealed record VerifiedTarget(
    string Key, string ActivityKey, string Fingerprint, Guid JellyfinItemId,
    string Title, MediaKind Kind, int SeerrMediaId, bool Is4K,
    bool MappingVerified, bool OwnRequestVerified, bool FilesPinned, long ConfigurationRevision = 0,
    string? DestructiveScopeKey = null);

public sealed class Activity
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    public long Revision { get; set; }
    public DateTimeOffset At { get; set; }
    public HashSet<string> Sessions { get; set; } = [];
}

public sealed record Consent(Answer Answer, string Snapshot, DateTimeOffset At);
public sealed record ExecutionClaim(Guid Id, DateTimeOffset At, string TargetFingerprint, long ProposalRevision);

public sealed class Proposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public VerifiedTarget Target { get; set; } = null!;
    public long Revision { get; set; } = 1;
    public ProposalStatus Status { get; set; } = ProposalStatus.Blocked;
    public string Reason { get; set; } = "VerificationRequired";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EligibleSince { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public int DelayFloorHours { get; set; } = 48;
    public Dictionary<Guid, Consent> Consents { get; set; } = [];
    public ExecutionClaim? Claim { get; set; }
    public DateTimeOffset? FilesDeletedAt { get; set; }
    public string? DeletionEvidence { get; set; }
    public string? CleanupOutcome { get; set; }
    public bool Terminal => Status is ProposalStatus.Completed or ProposalStatus.Cancelled;
}

public sealed class QueueState
{
    public int Schema { get; set; } = 1;
    public Policy Policy { get; set; } = new();
    public SeerrSettings Seerr { get; set; } = new();
    public Dictionary<Guid, Preferences> Preferences { get; set; } = [];
    public List<Proposal> Proposals { get; set; } = [];
    public List<Activity> Activity { get; set; } = [];
    public List<Notice> Notices { get; set; } = [];
    public bool NoticeDeliveryIncomplete { get; set; }
    public DateTimeOffset? CoverageSince { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public bool CoverageHealthy { get; set; }
    public bool ClockHealthy { get; set; } = true;
    public string CoverageProblem { get; set; } = "CollectorNotStarted";
}

public sealed class SeerrSettings
{
    public long Revision { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string JellyfinServerId { get; set; } = "";
}

public sealed record Notice(Guid Id, Guid UserId, Guid ProposalId, string Text, DateTimeOffset? ReadAt = null);
public sealed record JournalEntry(long Sequence, Guid CommandId, string Actor, string Kind, DateTimeOffset At);

public sealed record EvaluationEvidence(
    bool OwnerAuthorized, bool TargetVerified, bool CompleteActivityScan,
    bool IndependentCheckpointVerified, bool RemotePreconditionsSupported);

// Deliberately does not contain another user's identity, vote, activity, paths or request IDs.
public sealed record UserProposal(
    Guid Id, string Title, string Ownership, string Status, string Reason,
    long Revision, DateTimeOffset? NotBefore, bool CanUndo,
    string OwnSnapshot, Answer? OwnAnswer, DateTimeOffset? OwnActivityAt, string RequiredAction);

public interface IStateStore
{
    QueueState Read();
    T Write<T>(Guid commandId, string actor, string kind, Func<QueueState, T> command);
}
