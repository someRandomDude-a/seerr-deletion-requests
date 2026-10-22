using System.Net;
using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DeletionQueue.Tests;

public sealed class HardeningTests
{
    [Fact] public void BundledSqliteIncludesTheCve20256965Fix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)query.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), $"Loaded SQLite {version} is affected by CVE-2025-6965.");
    }

    [Fact] public void NoticeReadsArePrivateExplicitAndDoNotChangeConsentOrActivity()
    {
        var state = QueueRulesTests.State(); var proposal = QueueRulesTests.Propose(state);
        var user = QueueRulesTests.Viewer.Id;
        QueueRules.UpdatePreferences(state, user, 0, 48, true);
        QueueRules.Subscription(state, proposal.Id, user, 1, true);
        NoticeRules.Publish(state, proposal); NoticeRules.Publish(state, proposal);
        var notice = Assert.Single(state.Notices, n => n.UserId == user); Assert.Null(notice.ReadAt);
        var before = JsonSerializer.Serialize(state);
        _ = state.Notices.Where(n => n.UserId == user).ToArray();
        Assert.Equal(before, JsonSerializer.Serialize(state));
        Assert.Throws<QueueException>(() => NoticeRules.MarkRead(state, notice.Id, Guid.NewGuid(), QueueRulesTests.Now));
        Assert.Null(state.Notices[0].ReadAt);
        NoticeRules.MarkRead(state, notice.Id, user, QueueRulesTests.Now);
        Assert.NotNull(state.Notices.Single(n => n.Id == notice.Id).ReadAt); Assert.Empty(state.Activity); Assert.Single(proposal.Consents);
    }

    [Fact] public void ExistingNoticesRemainUnreadWithoutChangingHistoricalRecords()
    {
        var legacy = JsonSerializer.Deserialize<Notice>("""{"Id":"11111111-1111-1111-1111-111111111111","UserId":"22222222-2222-2222-2222-222222222222","ProposalId":"33333333-3333-3333-3333-333333333333","Text":"Legacy notice"}""");
        Assert.NotNull(legacy); Assert.Null(legacy.ReadAt);
    }

    [Fact] public void RelevantNoticesAreAutomaticWithOptOutAndOptionalExtraSubscriptions()
    {
        var s = QueueRulesTests.State(); var p = QueueRulesTests.Propose(s); var user = Guid.NewGuid();
        NoticeRules.Publish(s, p); Assert.Equal(p.OwnerId, Assert.Single(s.Notices).UserId);
        QueueRules.UpdatePreferences(s, user, 0, 48, true); NoticeRules.Publish(s, p); Assert.Single(s.Notices);
        QueueRules.Subscription(s, p.Id, user, 1, true); NoticeRules.Publish(s, p); Assert.Equal(2, s.Notices.Count);
        QueueRules.UpdatePreferences(s, p.OwnerId, 0, 48, false);
        s.Notices.Clear(); NoticeRules.Publish(s, p); Assert.Equal(user, Assert.Single(s.Notices).UserId);
    }

    [Fact] public void NotificationCapacityCannotBlockOwnerUndoOrChangeConsent()
    {
        var s = QueueRulesTests.State(); var p = QueueRulesTests.Propose(s); var user = Guid.NewGuid();
        QueueRules.UpdatePreferences(s, user, 0, 48, true); QueueRules.Subscription(s, p.Id, user, 1, true);
        for (var i = 0; i < 10000; i++) s.Notices.Add(new Notice(Guid.NewGuid(), Guid.NewGuid(), p.Id, "Synthetic retained notice"));
        QueueRules.Undo(s, p.Id, p.OwnerId, p.Revision); NoticeRules.Publish(s, p);
        Assert.Equal(ProposalStatus.Cancelled, p.Status); Assert.True(s.NoticeDeliveryIncomplete);
        Assert.Equal(10000, s.Notices.Count); Assert.Single(p.Consents);
    }

    [Theory]
    [InlineData("169.254.169.254", false)] [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("0.0.0.0", false)] [InlineData("::", false)] [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)] [InlineData("ff02::1", false)]
    [InlineData("127.0.0.1", true)] [InlineData("192.168.1.20", true)] [InlineData("172.20.0.2", true)]
    public void ConfiguredServiceDestinationsDoNotReachMetadataOrMulticast(string address, bool allowed) =>
        Assert.Equal(allowed, EndpointSafety.Allowed(IPAddress.Parse(address)));

    [Fact] public void MalformedAdminPolicyDoesNotMutateState()
    {
        var s = QueueRulesTests.State(); var before = JsonSerializer.Serialize(s);
        Assert.Throws<QueueException>(() => QueueRules.UpdatePolicy(s, new Policy { Accounts = null! }, 0));
        Assert.Throws<QueueException>(() => QueueRules.UpdatePolicy(s, new Policy { DefaultAuthority = (ProposalAuthority)999 }, 0));
        Assert.Equal(before, JsonSerializer.Serialize(s));
    }

    [Fact] public void WrongDatabaseCannotBeMutatedDuringVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "dq-hardening-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var initial = new AuthenticatedStore(root)) _ = initial.Read();
            var database = Path.Combine(root, "queue.sqlite3");
            File.Delete(database);
            using (var unrelated = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                unrelated.Open(); using var sql = unrelated.CreateCommand();
                sql.CommandText = "CREATE TABLE unrelated (id INTEGER); INSERT INTO unrelated VALUES (42);"; sql.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(database);
            Assert.Throws<IntegrityException>(() => new AuthenticatedStore(root));
            Assert.Equal(bytes, File.ReadAllBytes(database));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact] public void UnexpectedTriggersCannotRunDuringAuthenticatedWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "dq-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AuthenticatedStore(root);
            using (var attacker = new SqliteConnection($"Data Source={Path.Combine(root, "queue.sqlite3")};Pooling=False"))
            {
                attacker.Open(); using var sql = attacker.CreateCommand();
                sql.CommandText = "CREATE TABLE sentinel (id INTEGER); INSERT INTO sentinel VALUES (42); CREATE TRIGGER hostile AFTER INSERT ON journal BEGIN DELETE FROM sentinel; END;";
                sql.ExecuteNonQuery();
            }
            Assert.Throws<IntegrityException>(() => store.Write(Guid.NewGuid(), "test", "Rejected", _ => true));
            using var check = new SqliteConnection($"Data Source={Path.Combine(root, "queue.sqlite3")};Mode=ReadOnly;Pooling=False");
            check.Open(); using var count = check.CreateCommand(); count.CommandText = "SELECT id FROM sentinel";
            Assert.Equal(42L, count.ExecuteScalar());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
