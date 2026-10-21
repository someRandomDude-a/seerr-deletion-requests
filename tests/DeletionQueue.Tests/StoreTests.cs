using System.Text;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DeletionQueue.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dq-tests-" + Guid.NewGuid().ToString("N"));

    [Fact] public void EncryptedStateAndAuthenticatedHistorySurviveRestart()
    {
        using (var store = new AuthenticatedStore(directory))
        {
            store.Write(Guid.NewGuid(), "owner", "ConfigurationChanged", s => { s.Seerr.ApiKey = "very-private-api-key"; return true; });
            Assert.Equal("very-private-api-key", store.Read().Seerr.ApiKey);
            Assert.Equal("ConfigurationChanged", store.History().Last().Kind);
        }
        Assert.DoesNotContain("very-private-api-key", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "queue.sqlite3"))));
        using var reopened = new AuthenticatedStore(directory);
        Assert.Equal("very-private-api-key", reopened.Read().Seerr.ApiKey);
    }

    [Fact] public void CommandsCannotReplay()
    {
        using var store = new AuthenticatedStore(directory); var id = Guid.NewGuid();
        store.Write(id, "owner", "PreferenceChanged", s => { s.Policy.DelayHours = 96; return true; });
        Assert.Throws<QueueException>(() => store.Write(id, "owner", "Replay", s => { s.Policy.DelayHours = 48; return true; }));
        Assert.Equal(96, store.Read().Policy.DelayHours);
    }

    [Fact] public void FailedCommandRollsBackWithoutAppendingHistory()
    {
        using var store = new AuthenticatedStore(directory); var count = store.History().Count;
        Assert.Throws<QueueException>(() => store.Write<bool>(Guid.NewGuid(), "owner", "Failed", state =>
        { state.Policy.AutomaticDeletion = true; throw new QueueException("fail"); }));
        Assert.False(store.Read().Policy.AutomaticDeletion); Assert.Equal(count, store.History().Count);
    }

    [Fact] public void ReadDoesNotAppendOrModifyHead()
    {
        using var store = new AuthenticatedStore(directory);
        var seal = File.ReadAllBytes(Path.Combine(directory, "head.seal")); var count = store.History().Count;
        _ = store.Read(); _ = store.Read();
        Assert.Equal(seal, File.ReadAllBytes(Path.Combine(directory, "head.seal"))); Assert.Equal(count, store.History().Count);
    }

    [Theory]
    [InlineData("UPDATE journal SET ciphertext=zeroblob(length(ciphertext)) WHERE seq=2")]
    [InlineData("DELETE FROM journal WHERE seq=2")]
    [InlineData("DELETE FROM journal WHERE seq=(SELECT MAX(seq) FROM journal)")]
    [InlineData("UPDATE journal SET command='00000000-0000-0000-0000-000000000001' WHERE seq=2")]
    [InlineData("UPDATE journal SET previous='genesis' WHERE seq=3")]
    public void AlterationOmissionTailTruncationAndMetadataTamperingFreezeStore(string sql)
    {
        using var store = new AuthenticatedStore(directory);
        store.Write(Guid.NewGuid(), "a", "First", _ => true); store.Write(Guid.NewGuid(), "b", "Second", _ => true);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.sqlite3")};Pooling=False");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        Assert.Throws<IntegrityException>(() => store.Read());
        Assert.Throws<IntegrityException>(() => store.Write(Guid.NewGuid(), "a", "AttemptedRepair", _ => true));
    }

    [Fact] public void MissingKeyIsNeverRegenerated()
    {
        using (var store = new AuthenticatedStore(directory)) _ = store.Read();
        File.Delete(Path.Combine(directory, "keys.json"));
        Assert.Throws<IntegrityException>(() => new AuthenticatedStore(directory));
        Assert.False(File.Exists(Path.Combine(directory, "keys.json")));
    }

    [Fact] public void MissingDatabaseIsNeverRecreated()
    {
        using (var store = new AuthenticatedStore(directory)) _ = store.Read();
        File.Delete(Path.Combine(directory, "queue.sqlite3"));
        Assert.Throws<IntegrityException>(() => new AuthenticatedStore(directory));
        Assert.False(File.Exists(Path.Combine(directory, "queue.sqlite3")));
    }

    [Fact] public void OldHeadOrIndividualOldDatabaseCannotBeRestored()
    {
        using var store = new AuthenticatedStore(directory);
        var oldHead = File.ReadAllBytes(Path.Combine(directory, "head.seal"));
        store.Write(Guid.NewGuid(), "admin", "OptIn", s => { s.Policy.AutomaticDeletion = true; return true; });
        File.WriteAllBytes(Path.Combine(directory, "head.seal"), oldHead);
        Assert.Throws<IntegrityException>(() => store.Read());
    }

    [Fact] public void UnknownSchemaIsRejectedWithoutReset()
    {
        using var store = new AuthenticatedStore(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.sqlite3")};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery();
        Assert.Throws<IntegrityException>(() => store.Read());
    }

    [Fact] public async Task ConcurrentDuplicatesSerializeToOneActiveProposal()
    {
        using var store = new AuthenticatedStore(directory); var target = QueueRulesTests.Target;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            store.Write(Guid.NewGuid(), "user", "Propose", state => QueueRules.Propose(state,
                new Account(Guid.NewGuid(), true), target, true, QueueRules.Snapshot(state, target, Guid.Empty), QueueRulesTests.Now))))
            .Select(async task => { try { await task; } catch (QueueException) { } }));
        // The stale snapshots above must not insert partial rows on failed commands.
        Assert.Empty(store.Read().Proposals);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            var caller = new Account(Guid.NewGuid(), true);
            store.Write(Guid.NewGuid(), "user", "Propose", state => QueueRules.Propose(state, caller, target,
                true, QueueRules.Snapshot(state, target, caller.Id), QueueRulesTests.Now));
        })));
        var proposal = Assert.Single(store.Read().Proposals); Assert.Equal(12, proposal.Consents.Count);
    }

    public void Dispose()
    {
        // Only the unique synthetic test directory created by this fixture is removed.
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
