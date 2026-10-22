using System.Text.Json;
using DeletionQueue.Core;

namespace DeletionQueue.Tests;

public sealed class QueueRulesTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    internal static readonly Account Owner = new(Guid.NewGuid(), true);
    internal static readonly Account Viewer = new(Guid.NewGuid(), true);
    internal static VerifiedTarget Target => new("instance|movie:1|false", "movie:1", "pinned-v1", Guid.NewGuid(),
        "A movie", MediaKind.Movie, 42, false, true, true, true);
    internal static readonly EvaluationEvidence Good = new(true, true, true, true, true);

    internal static QueueState State() => new()
    { CoverageSince = Now.AddDays(-30), LastHeartbeat = Now, CoverageHealthy = true };

    internal static Proposal Propose(QueueState state, VerifiedTarget? target = null, Account? caller = null)
    {
        target ??= Target; caller ??= Owner;
        return QueueRules.Propose(state, caller, target, true, QueueRules.Snapshot(state, target, caller.Id), Now);
    }

    internal static void Evaluate(QueueState state, Proposal p, DateTimeOffset? time = null, EvaluationEvidence? evidence = null)
    {
        var now = time ?? Now;
        state.LastHeartbeat = now;
        QueueRules.Evaluate(state, p, evidence ?? Good, now);
    }

    [Fact] public void DefaultsAreOwn14Days48HoursAndExecutionOff()
    {
        var s = State();
        Assert.Equal(ProposalAuthority.OwnRequests, s.Policy.DefaultAuthority);
        Assert.Equal(14, s.Policy.ActivityDays); Assert.Equal(48, s.Policy.DelayHours); Assert.False(s.Policy.AutomaticDeletion);
    }

    [Theory]
    [InlineData(ProposalAuthority.Denied, true, false)]
    [InlineData(ProposalAuthority.OwnRequests, true, true)]
    [InlineData(ProposalAuthority.OwnRequests, false, false)]
    [InlineData(ProposalAuthority.AnyAccessibleItem, false, true)]
    public void ProposalPermissionMatrix(ProposalAuthority authority, bool owned, bool allowed)
    {
        var s = State(); s.Policy.DefaultAuthority = authority;
        if (allowed) _ = Propose(s, Target with { OwnRequestVerified = owned });
        else Assert.Throws<QueueException>(() => Propose(s, Target with { OwnRequestVerified = owned }));
    }

    [Fact] public void ElevationDoesNotBypassAccessOrDisable()
    {
        var s = State(); s.Policy.DefaultAuthority = ProposalAuthority.AnyAccessibleItem;
        var admin = Owner with { Administrator = true };
        Assert.Throws<QueueException>(() => QueueRules.Propose(s, admin, Target, false, "", Now));
        Assert.Throws<QueueException>(() => Propose(s, caller: admin with { Enabled = false }));
    }

    [Fact] public void AccountOverrideReplacesGlobalDefault()
    {
        var s = State(); s.Policy.DefaultAuthority = ProposalAuthority.Denied;
        s.Policy.Accounts[Owner.Id] = ProposalAuthority.AnyAccessibleItem;
        _ = Propose(s, Target with { OwnRequestVerified = false });
    }

    [Fact] public void DuplicateJoinsExistingOwnerWithOwnSnapshotApproval()
    {
        var s = State(); var p = Propose(s);
        var joined = Propose(s, p.Target, Viewer);
        Assert.Same(p, joined); Assert.Single(s.Proposals); Assert.Equal(Owner.Id, p.OwnerId);
        Assert.Equal(Answer.Yes, p.Consents[Viewer.Id].Answer); Assert.Equal(2, p.Consents.Count);
    }

    [Fact] public void ChangedTargetCannotAcquireApprovalThroughDuplicate()
    {
        var s = State(); var p = Propose(s);
        Assert.Throws<QueueException>(() => Propose(s, p.Target with { Fingerprint = "changed" }, Viewer));
        Assert.Single(p.Consents);
    }

    [Fact] public void OverlappingArrScopeCannotCreateASecondProposalUsingAnotherKey()
    {
        var s = State(); var target = Target with { DestructiveScopeKey = "radarr|0|123" };
        _ = Propose(s, target);
        Assert.Throws<QueueException>(() => Propose(s, target with { Key = "alternate-quality-key", Fingerprint = "different" }, Viewer));
        Assert.Single(s.Proposals);
    }

    [Fact] public void JoiningWithALongerPreferenceExtendsDeadlineImmediately()
    {
        var s = State(); var p = Propose(s); Evaluate(s, p);
        QueueRules.UpdatePreferences(s, Viewer.Id, 0, 144, false);
        _ = Propose(s, p.Target, Viewer);
        Assert.Equal(Now.AddHours(144), p.NotBefore);
    }

    [Fact] public void AuthorityRevocationAndRestorationBreaksEligibilityEvenBetweenSchedulerPasses()
    {
        var s = State(); var p = Propose(s); Evaluate(s, p);
        QueueRules.UpdatePolicy(s, new Policy { DefaultAuthority = ProposalAuthority.Denied }, 0);
        QueueRules.UpdatePolicy(s, new Policy(), 1);
        Assert.Null(p.EligibleSince);
        Evaluate(s, p, Now.AddHours(2)); Assert.Equal(Now.AddHours(50), p.NotBefore);
    }

    [Fact] public void OnlyOwnerCanUndoEvenIfOtherCallerIsAdministrator()
    {
        var s = State(); var p = Propose(s);
        Assert.Throws<QueueException>(() => QueueRules.Undo(s, p.Id, Viewer.Id, p.Revision));
        QueueRules.Undo(s, p.Id, Owner.Id, p.Revision);
        Assert.Equal(ProposalStatus.Cancelled, p.Status); Assert.Single(s.Proposals);
    }

    [Fact] public void DelayStartsWhenLastViewerApprovesNotAtProposalCreation()
    {
        var s = State(); var p = Propose(s);
        QueueRules.RecordActivity(s, Viewer.Id, p.Target.ActivityKey, null, false, Now);
        Evaluate(s, p); Assert.Equal(ProposalStatus.Blocked, p.Status); Assert.Null(p.NotBefore);
        var approvalAt = Now.AddDays(3);
        QueueRules.Consent(s, p.Id, Viewer, p.Revision, QueueRules.Snapshot(s, p.Target, Viewer.Id), Answer.Yes, true, approvalAt);
        Evaluate(s, p, approvalAt);
        Assert.Equal(approvalAt.AddHours(48), p.NotBefore);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void SilenceOrNoBlocksUntilActivityAgesOut(bool explicitNo)
    {
        var s = State(); var p = Propose(s);
        QueueRules.RecordActivity(s, Viewer.Id, p.Target.ActivityKey, null, false, Now);
        if (explicitNo) QueueRules.Consent(s, p.Id, Viewer, p.Revision, QueueRules.Snapshot(s, p.Target, Viewer.Id), Answer.No, true, Now);
        Evaluate(s, p); Assert.Equal(ProposalStatus.Blocked, p.Status);
        Evaluate(s, p, Now.AddDays(14)); Assert.Equal(ProposalStatus.Blocked, p.Status); // Inclusive boundary.
        var expired = Now.AddDays(14).AddTicks(1); Evaluate(s, p, expired);
        Assert.Equal(expired.AddHours(48), p.NotBefore);
    }

    [Fact] public void NewActivityInvalidatesConsentAndRestartsFullDelay()
    {
        var s = State(); var p = Propose(s);
        Evaluate(s, p); var previous = p.NotBefore;
        var activityAt = Now.AddHours(47);
        QueueRules.RecordActivity(s, Owner.Id, p.Target.ActivityKey, null, false, activityAt);
        Evaluate(s, p, activityAt); Assert.Equal(ProposalStatus.Blocked, p.Status);
        Assert.Null(QueueRules.Project(s, p, Owner.Id, activityAt).OwnAnswer);
        QueueRules.Consent(s, p.Id, Owner, p.Revision, QueueRules.Snapshot(s, p.Target, Owner.Id), Answer.Yes, true, activityAt);
        Evaluate(s, p, activityAt); Assert.Equal(activityAt.AddHours(48), p.NotBefore); Assert.True(p.NotBefore > previous);
    }

    [Fact] public void StaleSnapshotAndStaleRevisionAreRejected()
    {
        var s = State(); var p = Propose(s); var snapshot = QueueRules.Snapshot(s, p.Target, Owner.Id);
        var revision = p.Revision; QueueRules.RecordActivity(s, Owner.Id, p.Target.ActivityKey, null, false, Now);
        Assert.Throws<QueueException>(() => QueueRules.Consent(s, p.Id, Owner, revision, snapshot, Answer.Yes, true, Now));
        Assert.Throws<QueueException>(() => QueueRules.Consent(s, p.Id, Owner, p.Revision, snapshot, Answer.Yes, true, Now));
    }

    [Fact] public void ActivePlaybackProtectsBeyondWindowUntilStopped()
    {
        var s = State(); var p = Propose(s);
        QueueRules.RecordActivity(s, Viewer.Id, p.Target.ActivityKey, "session", false, Now.AddDays(-20));
        Evaluate(s, p); Assert.Equal("PlaybackActive", p.Reason);
        QueueRules.RecordActivity(s, Viewer.Id, p.Target.ActivityKey, "session", true, Now);
        Evaluate(s, p); Assert.Equal("ConsentRequired", p.Reason);
    }

    [Fact] public void PersonalDelayCannotBeShortenedByPreferenceReductionOrViewerExpiry()
    {
        var s = State(); QueueRules.UpdatePreferences(s, Owner.Id, 0, 240, false);
        var p = Propose(s); Evaluate(s, p); Assert.Equal(Now.AddHours(240), p.NotBefore);
        QueueRules.UpdatePreferences(s, Owner.Id, 1, 48, false); Evaluate(s, p);
        Assert.Equal(Now.AddHours(240), p.NotBefore);
        var policy = new Policy { DelayHours = 96 }; QueueRules.UpdatePolicy(s, policy, 0); Evaluate(s, p);
        Assert.Equal(Now.AddHours(240), p.NotBefore);
    }

    [Fact] public void IncreasedPersonalDelayExtendsExistingDeadlineImmediately()
    {
        var s = State(); var p = Propose(s); Evaluate(s, p);
        QueueRules.UpdatePreferences(s, Owner.Id, 0, 120, false);
        Assert.Equal(Now.AddHours(120), p.NotBefore);
    }

    [Fact] public void WindowChangesNeverMoveAnExistingDeadlineEarlier()
    {
        var s = State(); var p = Propose(s); Evaluate(s, p); var old = p.NotBefore;
        QueueRules.UpdatePolicy(s, new Policy { ActivityDays = 7 }, 0);
        Evaluate(s, p, Now.AddHours(5)); Assert.True(p.NotBefore >= old); Assert.Equal(Now.AddHours(53), p.NotBefore);
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void IncompleteEvidenceAlwaysBlocks(bool owner, bool target, bool scan, bool checkpoint, bool remote)
    {
        var s = State(); var p = Propose(s);
        Evaluate(s, p, evidence: new(owner, target, scan, checkpoint, remote));
        Assert.Equal(ProposalStatus.Blocked, p.Status); Assert.Null(p.EligibleSince);
    }

    [Fact] public void TrustedSeerrDelegationDoesNotRequireAtomicFilePinning()
    {
        var s = State(); var p = Propose(s, Target with { FilesPinned = false }); Evaluate(s, p);
        Assert.Equal(ProposalStatus.Waiting, p.Status); Assert.NotNull(p.NotBefore);
    }

    [Fact] public void LostCoverageRestartsDelayAndRetainsDeadlineFloor()
    {
        var s = State(); var p = Propose(s); Evaluate(s, p);
        QueueRules.BreakCoverage(s, "gap", Now.AddHours(1)); Evaluate(s, p, Now.AddHours(1));
        Assert.Equal(ProposalStatus.Blocked, p.Status);
        s.CoverageSince = Now.AddDays(-30); s.CoverageHealthy = true;
        Evaluate(s, p, Now.AddHours(2)); Assert.Equal(Now.AddHours(50), p.NotBefore);
    }

    [Fact] public void ReadsArePureAndAnonymous()
    {
        var s = State(); var p = Propose(s); var before = JsonSerializer.Serialize(s);
        var view = QueueRules.Project(s, p, Viewer.Id, Now);
        Assert.Equal(before, JsonSerializer.Serialize(s)); Assert.Equal("Anonymous", view.Ownership); Assert.False(view.CanUndo);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(Owner.Id.ToString(), json); Assert.DoesNotContain("SeerrMediaId", json);
        Assert.DoesNotContain("Consents", json); Assert.DoesNotContain("Fingerprint", json);
    }

    [Fact] public void ProposingOrAnsweringDoesNotSubscribeOrRecordActivity()
    {
        var s = State(); var p = Propose(s);
        QueueRules.Consent(s, p.Id, Viewer, p.Revision, QueueRules.Snapshot(s, p.Target, Viewer.Id), Answer.Yes, true, Now);
        Assert.Empty(s.Activity); Assert.Empty(s.Preferences); Assert.Empty(s.Notices);
    }

    [Fact] public void ClaimRequiresExplicitOptInAndCannotBeUndoneOrClaimedAgain()
    {
        var s = State(); var p = Propose(s); Evaluate(s, p); var due = Now.AddHours(48);
        s.LastHeartbeat = due;
        Assert.Throws<QueueException>(() => QueueRules.Claim(s, p.Id, p.Revision, Good, due));
        s.Policy.AutomaticDeletion = true;
        Assert.Throws<QueueException>(() => QueueRules.Claim(s, p.Id, p.Revision, Good, due));
        s.Policy.SeerrManagedDeletionAcknowledged = true;
        _ = QueueRules.Claim(s, p.Id, p.Revision, Good, due);
        Assert.Throws<QueueException>(() => QueueRules.Undo(s, p.Id, Owner.Id, p.Revision));
        Assert.Throws<QueueException>(() => QueueRules.Claim(s, p.Id, p.Revision, Good, due));
    }

    [Fact] public void CompletionRequiresSeparateFilesDeletedCheckpoint()
    {
        var s = State(); var p = Propose(s); s.Policy.AutomaticDeletion = true; s.Policy.SeerrManagedDeletionAcknowledged = true; Evaluate(s, p);
        s.LastHeartbeat = Now.AddHours(48); _ = QueueRules.Claim(s, p.Id, p.Revision, Good, s.LastHeartbeat.Value);
        Assert.Throws<QueueException>(() => QueueRules.Complete(s, p.Id));
        QueueRules.Uncertain(s, p.Id);
        Assert.Throws<QueueException>(() => QueueRules.FilesDeleted(s, p.Id, "different", "proof", Now));
        QueueRules.FilesDeleted(s, p.Id, p.Target.Fingerprint, "verified-pinned-files-absent", Now);
        Assert.Equal(ProposalStatus.FilesDeleted, p.Status);
        QueueRules.Complete(s, p.Id); Assert.Equal(ProposalStatus.Completed, p.Status); Assert.Single(s.Proposals);
    }

    [Fact] public void OldActivityRemainsProtectiveWithoutAViewerAccountLookup()
    {
        var s = State(); var p = Propose(s);
        // No account list exists in the evaluator: disabling/deleting a viewer cannot filter this away.
        QueueRules.RecordActivity(s, Guid.NewGuid(), p.Target.ActivityKey, null, false, Now);
        Evaluate(s, p); Assert.Equal("ConsentRequired", p.Reason);
    }
}
