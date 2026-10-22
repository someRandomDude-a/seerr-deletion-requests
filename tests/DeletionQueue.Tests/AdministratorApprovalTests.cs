using System.Text.Json;
using DeletionQueue.Core;

namespace DeletionQueue.Tests;

public sealed class AdministratorApprovalTests
{
    private static readonly Account Administrator = new(Guid.NewGuid(), true, true);
    private static readonly EvaluationEvidence Good = QueueRulesTests.Good with { AdministratorAuthorized = true };
    private static (QueueState State, Proposal Proposal) Pending()
    {
        var s = QueueRulesTests.State(); s.Policy.RequireAdministratorApproval = true;
        return (s, QueueRulesTests.Propose(s));
    }
    private static void Approve(QueueState s, Proposal p, bool approved = true) => QueueRules.AdministratorDecision(s,
        p.Id, Administrator, p.Revision, QueueRules.AdministratorSnapshot(s, p), approved, true, QueueRulesTests.Now);

    [Fact] public void ApprovalIsOptionalAndDefaultsOff()
    {
        var s = QueueRulesTests.State(); Assert.False(s.Policy.RequireAdministratorApproval);
        var p = QueueRulesTests.Propose(s); QueueRulesTests.Evaluate(s, p);
        Assert.Equal(ProposalStatus.Waiting, p.Status); Assert.Null(p.AdministratorApproval);
    }
    [Fact] public void RequiredApprovalBlocksAndThenStartsTheFullDelay()
    {
        var (s, p) = Pending(); QueueRulesTests.Evaluate(s, p, evidence: Good);
        Assert.Equal("AdministratorApprovalRequired", p.Reason); Assert.Null(p.EligibleSince);
        Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        Assert.Equal(QueueRulesTests.Now.AddHours(48), p.NotBefore);
    }
    [Theory] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, false)]
    public void OnlyAnEnabledVisibleAdministratorMayApprove(bool enabled, bool administrator, bool accessible)
    {
        var (s, p) = Pending(); var before = JsonSerializer.Serialize(s);
        Assert.Throws<QueueException>(() => QueueRules.AdministratorDecision(s, p.Id,
            new(Administrator.Id, enabled, administrator), p.Revision, QueueRules.AdministratorSnapshot(s, p), true, accessible, QueueRulesTests.Now));
        Assert.Equal(before, JsonSerializer.Serialize(s));
    }
    [Fact] public void ActivityAndPolicyChangesInvalidateApprovalWithoutAdvancingDeadlines()
    {
        var (s, p) = Pending(); Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        var deadline = p.NotBefore;
        QueueRules.RecordActivity(s, QueueRulesTests.Viewer.Id, p.Target.ActivityKey, null, false, QueueRulesTests.Now);
        Assert.False(QueueRules.AdministratorApprovalCurrent(s, p)); Assert.Equal(deadline, p.NotBefore);
        Approve(s, p);
        QueueRules.UpdatePolicy(s, new Policy { RequireAdministratorApproval = true }, s.Policy.Revision);
        Assert.False(QueueRules.AdministratorApprovalCurrent(s, p)); Assert.Equal(deadline, p.NotBefore);
    }
    [Fact] public void ApprovalDoesNotSupplyViewerConsentOrSurviveDemotion()
    {
        var (s, p) = Pending();
        QueueRules.RecordActivity(s, QueueRulesTests.Viewer.Id, p.Target.ActivityKey, null, false, QueueRulesTests.Now);
        Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        Assert.Equal("ConsentRequired", p.Reason); Assert.False(p.Consents.ContainsKey(Administrator.Id));
        QueueRules.Consent(s, p.Id, QueueRulesTests.Viewer, p.Revision, QueueRules.Snapshot(s, p.Target, QueueRulesTests.Viewer.Id), Answer.Yes, true, QueueRulesTests.Now);
        QueueRulesTests.Evaluate(s, p); Assert.Equal("AdministratorApprovalRequired", p.Reason);
    }
    [Fact] public void StaleDecisionsAndPostClaimDecisionsAreRejected()
    {
        var (s, p) = Pending(); var snapshot = QueueRules.AdministratorSnapshot(s, p);
        QueueRules.RecordActivity(s, Guid.NewGuid(), p.Target.ActivityKey, null, false, QueueRulesTests.Now.AddDays(-30));
        Assert.Throws<QueueException>(() => QueueRules.AdministratorDecision(s, p.Id, Administrator, p.Revision, snapshot, true, true, QueueRulesTests.Now));
        Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        s.Policy.AutomaticDeletion = true; s.Policy.SeerrManagedDeletionAcknowledged = true;
        s.LastHeartbeat = QueueRulesTests.Now.AddHours(48);
        QueueRules.Claim(s, p.Id, p.Revision, Good, s.LastHeartbeat.Value);
        Assert.Throws<QueueException>(() => Approve(s, p, false));
    }
    [Fact] public void RevocationAndReapprovalRestartDelayAndKeepApprovalIdentitiesPrivate()
    {
        var (s, p) = Pending(); Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        var deadline = p.NotBefore; Approve(s, p, false); Assert.Null(p.EligibleSince);
        Approve(s, p); QueueRulesTests.Evaluate(s, p, QueueRulesTests.Now.AddHours(1), Good);
        Assert.True(p.NotBefore >= deadline); Assert.Equal(QueueRulesTests.Now.AddHours(49), p.NotBefore);
        var json = JsonSerializer.Serialize(QueueRules.Project(s, p, QueueRulesTests.Owner.Id, QueueRulesTests.Now));
        Assert.DoesNotContain(Administrator.Id.ToString(), json); Assert.DoesNotContain("AdministratorApproval", json);
    }
    [Fact] public void DisablingRequirementAndResettingUsersNeverAdvanceDeadlineFloors()
    {
        var (s, p) = Pending(); Approve(s, p); QueueRulesTests.Evaluate(s, p, evidence: Good);
        var deadline = p.NotBefore;
        QueueRules.ResetUser(s, p.OwnerId, s.Policy.Revision, 0);
        Assert.True(s.Policy.RequireAdministratorApproval); Assert.Equal(deadline, p.NotBefore);
        QueueRules.UpdatePolicy(s, new Policy { RequireAdministratorApproval = false }, s.Policy.Revision);
        QueueRulesTests.Evaluate(s, p, QueueRulesTests.Now.AddHours(1));
        Assert.True(p.NotBefore >= deadline);
    }
}
