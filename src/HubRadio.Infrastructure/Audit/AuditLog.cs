using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Audit;

/// <summary>
/// Provides an append-only application API backed by a global, tamper-evident SHA-256 chain.
/// The chain detects many inconsistencies within the database snapshot being read; it does not
/// make SQLite tamper-proof or deletion impossible. Without an externally anchored checkpoint,
/// removal of a valid tail pair, a coordinated full rewrite and rechain, or rollback to an older
/// valid database snapshot cannot be detected.
/// </summary>
public sealed class AuditLog
{
    private readonly string _databasePath;

    public AuditLog(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public void Append(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        string occurredAt = SqliteStoreSupport.FormatTimestamp(
            auditEvent.OccurredAt,
            nameof(auditEvent));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        AuditChainTail tail = ReadTail(connection, transaction);
        long sequenceNumber;
        try
        {
            sequenceNumber = checked(tail.SequenceNumber + 1);
        }
        catch (OverflowException)
        {
            throw InvalidChain();
        }

        InsertAuditEvent(connection, transaction, auditEvent, occurredAt);
        AuditEventSnapshot stored = AuditChainCodec.ReadSingleAuditEvent(
            connection,
            transaction,
            auditEvent.Id);
        byte[] payload = AuditChainCodec.CreateCanonicalPayload(
            sequenceNumber,
            stored.Event);
        byte[] eventHash = AuditChainCodec.ComputeEventHash(tail.EventHash, payload);
        AuditChainCodec.InsertChainEntry(
            connection,
            transaction,
            sequenceNumber,
            stored.Event.Id,
            tail.EventHash,
            eventHash);
        _ = VerifyAndReadEvents(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Verifies the complete global chain in one read snapshot.
    /// </summary>
    public void Verify()
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        _ = VerifyAndReadEvents(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Verifies the complete global chain in one read snapshot, then returns the requested
    /// project's events in global sequence order.
    /// </summary>
    public IReadOnlyList<StoredAuditEvent> GetProjectEvents(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        StoredAuditEvent[] verified = VerifyAndReadEvents(connection, transaction);
        StoredAuditEvent[] selected = verified
            .Where(auditEvent => auditEvent.ProjectId == projectId)
            .ToArray();
        transaction.Commit();
        return Array.AsReadOnly(selected);
    }

    private static AuditChainTail ReadTail(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence_number, CAST(event_hash AS BLOB)
            FROM audit_chain_entries
            ORDER BY sequence_number DESC
            LIMIT 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new AuditChainTail(0, AuditChainCodec.CreateGenesisHash());
        }

        long sequenceNumber = SqliteStoreSupport.ReadInt64(reader, 0, "audit chain");
        byte[] eventHash = AuditChainCodec.ReadHash(reader, 1);
        if (sequenceNumber < 1 || reader.Read())
        {
            throw InvalidChain();
        }

        return new AuditChainTail(sequenceNumber, eventHash);
    }

    private static void InsertAuditEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditEvent auditEvent,
        string occurredAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events (
                id,
                project_id,
                kind,
                actor_id,
                actor_role,
                occurred_at,
                reason,
                composition_version_id,
                composition_version_digest
            )
            VALUES (
                $id,
                $project_id,
                $kind,
                $actor_id,
                $actor_role,
                $occurred_at,
                $reason,
                $composition_version_id,
                $composition_version_digest
            );
            """;
        SqliteStoreSupport.AddText(command, "$id", auditEvent.Id.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", auditEvent.ProjectId.ToString());
        SqliteStoreSupport.AddText(command, "$kind", auditEvent.Kind.ToString());
        SqliteStoreSupport.AddText(command, "$actor_id", auditEvent.ActorId);
        SqliteStoreSupport.AddText(command, "$actor_role", auditEvent.ActorRole.ToString());
        SqliteStoreSupport.AddText(command, "$occurred_at", occurredAt);
        SqliteStoreSupport.AddText(command, "$reason", auditEvent.Reason);
        SqliteStoreSupport.AddNullableText(
            command,
            "$composition_version_id",
            auditEvent.CompositionVersionId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$composition_version_digest",
            auditEvent.CompositionVersionDigest?.Hex);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The audit event was not stored.");
        }
    }

    private static StoredAuditEvent[] VerifyAndReadEvents(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        (long eventCount, long chainCount) = ReadCounts(connection, transaction);
        if (eventCount != chainCount)
        {
            throw InvalidChain();
        }

        VerifyAssociations(connection, transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                chain.sequence_number,
                CAST(chain.audit_event_id AS BLOB),
                chain.format_version,
                CAST(chain.previous_hash AS BLOB),
                CAST(chain.event_hash AS BLOB),
                CAST(event.id AS BLOB),
                CAST(event.project_id AS BLOB),
                CAST(event.kind AS BLOB),
                CAST(event.actor_id AS BLOB),
                CAST(event.actor_role AS BLOB),
                CAST(event.occurred_at AS BLOB),
                CAST(event.reason AS BLOB),
                CAST(event.composition_version_id AS BLOB),
                CAST(event.composition_version_digest AS BLOB)
            FROM audit_chain_entries AS chain
            LEFT JOIN audit_events AS event
                ON event.id = chain.audit_event_id
            ORDER BY chain.sequence_number;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<StoredAuditEvent>();
        long expectedSequence = 1;
        byte[] expectedPreviousHash = AuditChainCodec.CreateGenesisHash();
        while (reader.Read())
        {
            long sequenceNumber = SqliteStoreSupport.ReadInt64(reader, 0, "audit chain");
            EntityId<AuditEventIdTag> linkedEventId =
                AuditChainCodec.ReadEntityId<AuditEventIdTag>(reader, 1);
            int formatVersion = SqliteStoreSupport.ReadInt32(reader, 2, "audit chain");
            byte[] previousHash = AuditChainCodec.ReadHash(reader, 3);
            byte[] storedEventHash = AuditChainCodec.ReadHash(reader, 4);
            AuditEventSnapshot auditEvent = AuditChainCodec.ReadAuditEvent(reader, 5);

            if (sequenceNumber != expectedSequence ||
                formatVersion != AuditChainCodec.FormatVersion ||
                linkedEventId != auditEvent.Event.Id ||
                !CryptographicOperations.FixedTimeEquals(
                    expectedPreviousHash,
                    previousHash))
            {
                throw InvalidChain();
            }

            byte[] payload = AuditChainCodec.CreateCanonicalPayload(
                sequenceNumber,
                auditEvent.Event);
            byte[] computedEventHash = AuditChainCodec.ComputeEventHash(
                previousHash,
                payload);
            if (!CryptographicOperations.FixedTimeEquals(
                    storedEventHash,
                    computedEventHash))
            {
                throw InvalidChain();
            }

            events.Add(auditEvent.Event);
            expectedPreviousHash = storedEventHash;
            try
            {
                expectedSequence = checked(expectedSequence + 1);
            }
            catch (OverflowException)
            {
                throw InvalidChain();
            }
        }

        if (events.Count != eventCount)
        {
            throw InvalidChain();
        }

        return events.ToArray();
    }

    private static (long EventCount, long ChainCount) ReadCounts(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM audit_events),
                (SELECT COUNT(*) FROM audit_chain_entries);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            reader.GetValue(0) is not long eventCount ||
            reader.GetValue(1) is not long chainCount ||
            eventCount < 0 ||
            chainCount < 0 ||
            reader.Read())
        {
            throw InvalidChain();
        }

        return (eventCount, chainCount);
    }

    private static void VerifyAssociations(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM audit_events AS event
                    LEFT JOIN audit_chain_entries AS chain
                        ON chain.audit_event_id = event.id
                    WHERE chain.audit_event_id IS NULL
                ),
                EXISTS (
                    SELECT 1
                    FROM audit_chain_entries AS chain
                    LEFT JOIN audit_events AS event
                        ON event.id = chain.audit_event_id
                    WHERE event.id IS NULL
                );
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            reader.GetValue(0) is not long unchained ||
            reader.GetValue(1) is not long orphaned ||
            unchained != 0 ||
            orphaned != 0 ||
            reader.Read())
        {
            throw InvalidChain();
        }
    }

    private static InvalidOperationException InvalidChain() =>
        SqliteStoreSupport.InvalidStoredData("audit chain");

    private sealed record AuditChainTail(long SequenceNumber, byte[] EventHash);
}

internal static class AuditChainCodec
{
    internal const int FormatVersion = 1;
    internal const string Domain = "HubRadio.MovieCreator.AuditChainEntry/v1";

    private const string EntityKind = "audit chain";
    private const int Sha256Length = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static byte[] CreateGenesisHash() => new byte[Sha256Length];

    internal static byte[] CreateCanonicalPayload(
        long sequenceNumber,
        StoredAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "An audit-chain sequence number must be positive.");
        }

        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteNumber("sequenceNumber", sequenceNumber);
            writer.WriteString("eventId", auditEvent.Id.ToString());
            writer.WriteString("projectId", auditEvent.ProjectId.ToString());
            writer.WriteString("kind", auditEvent.Kind.ToString());
            writer.WriteString("actorId", auditEvent.ActorId);
            writer.WriteString("actorRole", auditEvent.ActorRole.ToString());
            writer.WriteString(
                "occurredAt",
                SqliteStoreSupport.FormatTimestamp(
                    auditEvent.OccurredAt,
                    nameof(auditEvent)));
            writer.WriteString("reason", auditEvent.Reason);
            writer.WritePropertyName("compositionVersionId");
            if (auditEvent.CompositionVersionId is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(auditEvent.CompositionVersionId.ToString());
            }

            writer.WritePropertyName("compositionVersionDigest");
            if (auditEvent.CompositionVersionDigest is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(auditEvent.CompositionVersionDigest.Hex);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    internal static byte[] ComputeEventHash(
        ReadOnlySpan<byte> previousHash,
        ReadOnlySpan<byte> canonicalPayload)
    {
        if (previousHash.Length != Sha256Length)
        {
            throw new ArgumentException(
                "An audit-chain hash must contain exactly 32 bytes.",
                nameof(previousHash));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, StrictUtf8.GetBytes(Domain));
        AppendLengthPrefixed(hash, previousHash);
        AppendLengthPrefixed(hash, canonicalPayload);
        return hash.GetHashAndReset();
    }

    internal static void Backfill(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        List<AuditEventSnapshot> events = ReadAllAuditEvents(connection, transaction);
        events.Sort(static (left, right) =>
        {
            int timestampComparison = StringComparer.Ordinal.Compare(
                left.OccurredAtText,
                right.OccurredAtText);
            return timestampComparison != 0
                ? timestampComparison
                : StringComparer.Ordinal.Compare(
                    left.Event.Id.ToString(),
                    right.Event.Id.ToString());
        });

        long sequenceNumber = 1;
        byte[] previousHash = CreateGenesisHash();
        foreach (AuditEventSnapshot auditEvent in events)
        {
            byte[] payload = CreateCanonicalPayload(sequenceNumber, auditEvent.Event);
            byte[] eventHash = ComputeEventHash(previousHash, payload);
            InsertChainEntry(
                connection,
                transaction,
                sequenceNumber,
                auditEvent.Event.Id,
                previousHash,
                eventHash);
            previousHash = eventHash;
            sequenceNumber = checked(sequenceNumber + 1);
        }
    }

    internal static AuditEventSnapshot ReadSingleAuditEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<AuditEventIdTag> auditEventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(id AS BLOB),
                CAST(project_id AS BLOB),
                CAST(kind AS BLOB),
                CAST(actor_id AS BLOB),
                CAST(actor_role AS BLOB),
                CAST(occurred_at AS BLOB),
                CAST(reason AS BLOB),
                CAST(composition_version_id AS BLOB),
                CAST(composition_version_digest AS BLOB)
            FROM audit_events
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", auditEventId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        AuditEventSnapshot auditEvent = ReadAuditEvent(reader, 0);
        if (reader.Read() || auditEvent.Event.Id != auditEventId)
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        return auditEvent;
    }

    internal static AuditEventSnapshot ReadAuditEvent(
        SqliteDataReader reader,
        int firstOrdinal)
    {
        EntityId<AuditEventIdTag> id =
            ReadEntityId<AuditEventIdTag>(reader, firstOrdinal);
        EntityId<ProjectIdTag> projectId =
            ReadEntityId<ProjectIdTag>(reader, firstOrdinal + 1);
        AuditEventKind kind = ParseEnum<AuditEventKind>(
            ReadUtf8Text(reader, firstOrdinal + 2));
        string actorId = ReadCanonicalText(reader, firstOrdinal + 3);
        ReviewActorRole actorRole = ParseEnum<ReviewActorRole>(
            ReadUtf8Text(reader, firstOrdinal + 4));
        string occurredAtText = ReadUtf8Text(reader, firstOrdinal + 5);
        DateTimeOffset occurredAt = ParseTimestamp(occurredAtText);
        if (!string.Equals(
                occurredAtText,
                SqliteStoreSupport.FormatTimestamp(occurredAt, nameof(occurredAt)),
                StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        string reason = ReadCanonicalText(reader, firstOrdinal + 6);
        EntityId<CompositionVersionIdTag>? compositionVersionId =
            ReadNullableEntityId<CompositionVersionIdTag>(reader, firstOrdinal + 7);
        ContentDigest? compositionVersionDigest =
            ReadNullableContentDigest(reader, firstOrdinal + 8);
        if ((compositionVersionId is null) != (compositionVersionDigest is null))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        var stored = new StoredAuditEvent(
            id,
            projectId,
            kind,
            actorId,
            actorRole,
            occurredAt,
            reason,
            compositionVersionId,
            compositionVersionDigest);
        return new AuditEventSnapshot(stored, occurredAtText);
    }

    internal static EntityId<TTag> ReadEntityId<TTag>(
        SqliteDataReader reader,
        int ordinal)
    {
        string value = ReadUtf8Text(reader, ordinal);
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        return id;
    }

    internal static byte[] ReadHash(SqliteDataReader reader, int ordinal)
    {
        string value = ReadUtf8Text(reader, ordinal);
        if (value.Length != Sha256Length * 2)
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw SqliteStoreSupport.InvalidStoredData(EntityKind);
            }
        }

        return Convert.FromHexString(value);
    }

    internal static void InsertChainEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sequenceNumber,
        EntityId<AuditEventIdTag> auditEventId,
        ReadOnlySpan<byte> previousHash,
        ReadOnlySpan<byte> eventHash)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_chain_entries (
                sequence_number,
                audit_event_id,
                format_version,
                previous_hash,
                event_hash
            )
            VALUES (
                $sequence_number,
                $audit_event_id,
                $format_version,
                $previous_hash,
                $event_hash
            );
            """;
        SqliteStoreSupport.AddInteger(command, "$sequence_number", sequenceNumber);
        SqliteStoreSupport.AddText(command, "$audit_event_id", auditEventId.ToString());
        SqliteStoreSupport.AddInteger(command, "$format_version", FormatVersion);
        SqliteStoreSupport.AddText(
            command,
            "$previous_hash",
            ContentDigest.FromBytes(previousHash).Hex);
        SqliteStoreSupport.AddText(
            command,
            "$event_hash",
            ContentDigest.FromBytes(eventHash).Hex);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The audit-chain entry was not stored.");
        }
    }

    private static List<AuditEventSnapshot> ReadAllAuditEvents(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(id AS BLOB),
                CAST(project_id AS BLOB),
                CAST(kind AS BLOB),
                CAST(actor_id AS BLOB),
                CAST(actor_role AS BLOB),
                CAST(occurred_at AS BLOB),
                CAST(reason AS BLOB),
                CAST(composition_version_id AS BLOB),
                CAST(composition_version_digest AS BLOB)
            FROM audit_events;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<AuditEventSnapshot>();
        while (reader.Read())
        {
            events.Add(ReadAuditEvent(reader, 0));
        }

        return events;
    }

    private static string ReadCanonicalText(
        SqliteDataReader reader,
        int ordinal)
    {
        string value = ReadUtf8Text(reader, ordinal);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        return value;
    }

    private static string ReadUtf8Text(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not byte[] bytes)
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }
    }

    private static EntityId<TTag>? ReadNullableEntityId<TTag>(
        SqliteDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ReadEntityId<TTag>(reader, ordinal);
    }

    private static ContentDigest? ReadNullableContentDigest(
        SqliteDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string value = ReadUtf8Text(reader, ordinal);
        if (!ContentDigest.TryParse(value, out ContentDigest? digest) ||
            !string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        return digest;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        return timestamp;
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(EntityKind);
        }

        return parsed;
    }

    private static void AppendLengthPrefixed(
        IncrementalHash hash,
        ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

internal sealed record AuditEventSnapshot(
    StoredAuditEvent Event,
    string OccurredAtText);
