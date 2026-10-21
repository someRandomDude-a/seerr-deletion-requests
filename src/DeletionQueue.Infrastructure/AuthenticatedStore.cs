using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using DeletionQueue.Core;
using Microsoft.Data.Sqlite;

namespace DeletionQueue.Infrastructure;

public sealed class IntegrityException(string message) : Exception(message);

/// <summary>
/// Append-only encrypted snapshots: the authenticated journal IS the state, with no mutable SQL projection.
/// The local head seal detects suffix omission, but is deliberately not advertised as independent rollback protection.
/// </summary>
public sealed class AuthenticatedStore : IStateStore, IDisposable
{
    private readonly object sync = new();
    private readonly string directory;
    private readonly FileStream instanceLock;
    private readonly byte[] encryptionKey;
    private readonly byte[] sealKey;
    private readonly Guid installation;
    private readonly string database;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private const long MaximumJournalBytes = 256L * 1024 * 1024;
    private const string JournalSchema = "CREATE TABLE journal (seq INTEGER PRIMARY KEY, command TEXT NOT NULL UNIQUE, previous TEXT NOT NULL, nonce BLOB NOT NULL, tag BLOB NOT NULL, ciphertext BLOB NOT NULL)";

    private sealed record KeyMaterial(Guid Installation, string Key);
    private sealed record Event(Guid Command, string Actor, string Kind, DateTimeOffset At, QueueState State);
    private sealed record Head(long Sequence, string Digest, string Mac);

    public AuthenticatedStore(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        instanceLock = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            database = Path.Combine(directory, "queue.sqlite3");
            var keyPath = Path.Combine(directory, "keys.json");
            var marker = Path.Combine(directory, "initialized");
            var fresh = !File.Exists(marker);
            if (fresh)
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != "writer.lock"))
                    throw new IntegrityException("Unrecognized existing state; initialization refused.");
                DurableCreate(marker, Encoding.UTF8.GetBytes("DeletionQueue schema 1\n"));
                DurableCreate(keyPath, JsonSerializer.SerializeToUtf8Bytes(new KeyMaterial(Guid.NewGuid(),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            if (!File.Exists(keyPath)) throw new IntegrityException("Key missing; never regenerate keys for existing state.");
            var material = JsonSerializer.Deserialize<KeyMaterial>(ReadSmallFile(keyPath), Json)
                ?? throw new IntegrityException("Invalid key material.");
            installation = material.Installation;
            var master = Convert.FromBase64String(material.Key);
            if (master.Length != 32 || installation == Guid.Empty) throw new IntegrityException("Invalid key material.");
            encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dq/encryption/v1"u8.ToArray());
            sealKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dq/head/v1"u8.ToArray());
            CryptographicOperations.ZeroMemory(master);
            if (!File.Exists(database))
            {
                if (!fresh) throw new IntegrityException("Partial existing initialization; automatic recovery refused.");
                if (File.Exists(SealPath) || new FileInfo(marker).Length != "DeletionQueue schema 1\n"u8.Length)
                    throw new IntegrityException("Database missing from an existing installation.");
                // A key-only partial initialization is not recoverable automatically.
                // A fresh marker and key created in THIS constructor are required below.
                if (File.Exists(Path.Combine(directory, "database-created")))
                    throw new IntegrityException("Database missing from an existing installation.");
                DurableCreate(Path.Combine(directory, "database-created"), "1"u8.ToArray());
                using var connection = Open(create: true);
                using var schema = connection.CreateCommand();
                schema.CommandText = JournalSchema + "; PRAGMA user_version=1;";
                schema.ExecuteNonQuery();
                WriteInitialHead();
                Write(Guid.NewGuid(), "installation", "Initialized", _ => true);
            }
            _ = Read();
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    private string SealPath => Path.Combine(directory, "head.seal");

    public QueueState Read()
    {
        lock (sync)
        {
            try { using var connection = Open(readOnly: true); return Load(connection).State; }
            catch (Exception e) when (e is not IntegrityException)
            { throw new IntegrityException("State authentication failed; authorization is frozen."); }
        }
    }

    public IReadOnlyList<JournalEntry> History()
    {
        lock (sync) { using var connection = Open(readOnly: true); return Load(connection).History; }
    }

    public T Write<T>(Guid commandId, string actor, string kind, Func<QueueState, T> command)
    {
        if (commandId == Guid.Empty) throw new QueueException("CommandIdRequired");
        lock (sync)
        {
            // Authenticate with a read-only connection before opening any existing database for writes.
            using (var verification = Open(readOnly: true)) { _ = Load(verification); }
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var loaded = Load(connection, transaction);
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT COUNT(*) FROM journal WHERE command=$id";
                check.Parameters.AddWithValue("$id", commandId.ToString("D"));
                if ((long)check.ExecuteScalar()! != 0) throw new QueueException("CommandReplay");
            }
            var result = command(loaded.State);
            var next = loaded.Sequence + 1;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Event(commandId, actor, kind, DateTimeOffset.UtcNow, loaded.State), Json);
            if (plaintext.Length > MaximumPayloadBytes) throw new QueueException("StateCapacityReached");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(encryptionKey, 16))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(next, commandId, loaded.Digest));
            CryptographicOperations.ZeroMemory(plaintext);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO journal VALUES ($seq,$command,$previous,$nonce,$tag,$ciphertext)";
            insert.Parameters.AddWithValue("$seq", next);
            insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", loaded.Digest);
            insert.Parameters.AddWithValue("$nonce", nonce);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.Parameters.AddWithValue("$ciphertext", ciphertext);
            insert.ExecuteNonQuery();
            transaction.Commit();
            // Crash between the database commit and head replacement freezes the store. Never roll the head back.
            SaveHead(next, Digest(next, commandId, loaded.Digest, nonce, tag, ciphertext));
            return result;
        }
    }

    private (QueueState State, long Sequence, string Digest, IReadOnlyList<JournalEntry> History) Load(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        try
        {
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version";
            if ((long)version.ExecuteScalar()! != 1) throw new IntegrityException("Unknown schema; additive migration required.");
            using var schema = connection.CreateCommand(); schema.Transaction = transaction;
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE NOT ((type='table' AND name='journal' AND sql=$schema) OR (type='index' AND name='sqlite_autoindex_journal_1'));";
            schema.Parameters.AddWithValue("$schema", JournalSchema);
            if ((long)schema.ExecuteScalar()! != 0) throw new IntegrityException("Unexpected private database schema; no query or write attempted.");
            var state = new QueueState();
            var history = new Queue<JournalEntry>();
            var replay = Stopwatch.StartNew();
            long sequence = 0;
            var digest = "genesis";
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT seq,command,previous,nonce,tag,ciphertext,length(command),length(previous) FROM journal ORDER BY seq";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (replay.Elapsed > TimeSpan.FromSeconds(2)) throw new IntegrityException("Journal replay budget exceeded; plugin stopped without resetting state.");
                if (reader.GetFieldType(0) != typeof(long) || reader.GetInt64(6) != 36 || reader.GetInt64(7) > 64)
                    throw new IntegrityException("Invalid journal metadata size/type.");
                var next = reader.GetInt64(0);
                var id = Guid.Parse(reader.GetString(1));
                if (next != sequence + 1 || reader.GetString(2) != digest) throw new IntegrityException("Journal omission or reordering.");
                if (reader.GetBytes(3, 0, null, 0, 0) != 12 || reader.GetBytes(4, 0, null, 0, 0) != 16 ||
                    reader.GetBytes(5, 0, null, 0, 0) > MaximumPayloadBytes) throw new IntegrityException("Invalid or excessive journal record size.");
                var nonce = (byte[])reader[3];
                var tag = (byte[])reader[4];
                var ciphertext = (byte[])reader[5];
                var plaintext = new byte[ciphertext.Length];
                using (var aes = new AesGcm(encryptionKey, 16))
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(next, id, digest));
                var evt = JsonSerializer.Deserialize<Event>(plaintext, Json) ?? throw new IntegrityException("Invalid journal event.");
                CryptographicOperations.ZeroMemory(plaintext);
                if (evt.Command != id || evt.State.Schema != 1) throw new IntegrityException("Invalid journal context.");
                state = evt.State;
                if (history.Count == 200) history.Dequeue();
                history.Enqueue(new JournalEntry(next, evt.Command, evt.Actor, evt.Kind, evt.At));
                digest = Digest(next, id, digest, nonce, tag, ciphertext);
                sequence = next;
            }
            var head = JsonSerializer.Deserialize<Head>(ReadSmallFile(SealPath), Json) ?? throw new IntegrityException("Missing head seal.");
            if (head.Sequence != sequence || head.Digest != digest ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(head.Mac), HeadMac(sequence, digest)))
                throw new IntegrityException("Head mismatch: possible truncation, rollback or interrupted commit. No repair performed.");
            return (state, sequence, digest, history.TakeLast(200).ToArray());
        }
        catch (Exception e) when (e is not IntegrityException)
        { throw new IntegrityException("Journal authentication failed; original state preserved."); }
    }

    private SqliteConnection Open(bool create = false, bool readOnly = false)
    {
        RejectLink(directory);
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
        {
            RejectLink(file);
            if (File.Exists(file) && new FileInfo(file).Length > MaximumJournalBytes) throw new IntegrityException("Plugin storage budget exceeded; no state reset performed.");
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = create ? SqliteOpenMode.ReadWriteCreate : readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            connection.Open();
            using var safety = connection.CreateCommand();
            safety.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL;";
            safety.ExecuteNonQuery();
            if (create)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
                pragma.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IntegrityException("Linked plugin state is not supported; no target opened.");
    }
    private static byte[] ReadSmallFile(string path)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > 8192) throw new IntegrityException("Invalid key/seal length.");
        return File.ReadAllBytes(path);
    }

    private byte[] Aad(long sequence, Guid command, string previous) =>
        Encoding.UTF8.GetBytes($"dq/v1|{installation:D}|{sequence}|{command:D}|{previous}");

    private string Digest(long sequence, Guid command, string previous, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Aad(sequence, command, previous));
        hash.AppendData(nonce); hash.AppendData(tag); hash.AppendData(ciphertext);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private byte[] HeadMac(long sequence, string digest) => HMACSHA256.HashData(sealKey,
        Encoding.UTF8.GetBytes($"dq/head/v1|{installation:D}|{sequence}|{digest}"));

    private void WriteInitialHead() => SaveHead(0, "genesis");

    private void SaveHead(long sequence, string digest)
    {
        var temporary = Path.Combine(directory, $"head-{Guid.NewGuid():N}.tmp");
        DurableCreate(temporary, JsonSerializer.SerializeToUtf8Bytes(new Head(sequence, digest,
            Convert.ToHexString(HeadMac(sequence, digest))), Json));
        File.Move(temporary, SealPath, overwrite: true);
    }

    private static void DurableCreate(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        lock (sync)
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(sealKey);
            instanceLock.Dispose();
        }
    }
}
