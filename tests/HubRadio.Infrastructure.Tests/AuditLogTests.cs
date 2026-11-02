using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Audit;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AuditLogTests
{
    private static readonly DateTimeOffset AuditTime =
        new(2033, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [TestMethod]
    public void EmptyDatabaseVerifiesAndFirstAppendUsesGenesis()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Empty audit project");
        var log = new AuditLog(database.DatabasePath);

        log.Verify();
        Assert.AreEqual(0, log.GetProjectEvents(project.Id).Count);

        AuditEvent auditEvent = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "Created the first audit event.");
        log.Append(auditEvent);
        log.Verify();

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence_number, audit_event_id, format_version, previous_hash
            FROM audit_chain_entries;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(auditEvent.Id.ToString(), reader.GetString(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
        Assert.AreEqual(new string('0', 64), reader.GetString(3));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    // NFR-AUD-001 AC-118: 操作履歴（監査イベント）が改ざん検知つきで追記・取得できる。
    public void MultipleAppendsRoundTripTypedFieldsAndReturnDefensiveCollection()
    {
        using StoreTestDatabase database = new();
        (Project project, CompositionVersion version) = CreateProjectAndVersion(database);
        var log = new AuditLog(database.DatabasePath);
        AuditEvent projectEvent = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "Project event.",
            actorId: "legacy-local-account");
        AuditEvent versionEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.CompositionExported,
            "legacy-local-account",
            ReviewActorRole.Editor,
            AuditTime.AddMinutes(1),
            "Version event.",
            project.Id,
            version);

        log.Append(projectEvent);
        log.Append(versionEvent);

        IReadOnlyList<StoredAuditEvent> events = log.GetProjectEvents(project.Id);
        CollectionAssert.AreEqual(
            new[] { projectEvent.Id, versionEvent.Id },
            events.Select(static auditEvent => auditEvent.Id).ToArray());
        Assert.AreEqual("legacy-local-account", events[0].ActorId);
        Assert.AreEqual(AuditEventKind.CompositionExported, events[1].Kind);
        Assert.AreEqual(version.Id, events[1].CompositionVersionId);
        Assert.AreEqual(version.ContentDigest, events[1].CompositionVersionDigest);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredAuditEvent>)events).Clear());
    }

    [TestMethod]
    public void InterleavedProjectsAreFilteredInGlobalSequenceOrder()
    {
        using StoreTestDatabase database = new();
        Project firstProject = CreateProject(database, "First project");
        Project secondProject = CreateProject(database, "Second project");
        var log = new AuditLog(database.DatabasePath);
        AuditEvent firstOne = CreateAuditEvent(
            firstProject.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(3),
            "First project, first append.");
        AuditEvent secondOne = CreateAuditEvent(
            secondProject.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(1),
            "Second project, first append.");
        AuditEvent firstTwo = CreateAuditEvent(
            firstProject.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "First project, second append.");

        log.Append(firstOne);
        log.Append(secondOne);
        log.Append(firstTwo);

        CollectionAssert.AreEqual(
            new[] { firstOne.Id, firstTwo.Id },
            log.GetProjectEvents(firstProject.Id)
                .Select(static auditEvent => auditEvent.Id)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { secondOne.Id },
            log.GetProjectEvents(secondProject.Id)
                .Select(static auditEvent => auditEvent.Id)
                .ToArray());
    }

    [TestMethod]
    public async Task ConcurrentAppendsProduceOneUniqueContiguousGlobalChain()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Concurrent audit project");
        AuditEvent[] auditEvents = Enumerable.Range(0, 24)
            .Select(index => CreateAuditEvent(
                project.Id,
                EntityId<AuditEventIdTag>.New(),
                AuditTime.AddTicks(index),
                FormattableString.Invariant($"Concurrent append {index}.")))
            .ToArray();
        using var release = new ManualResetEventSlim(initialState: false);
        Task[] appends = auditEvents
            .Select(auditEvent => Task.Run(() =>
            {
                release.Wait();
                new AuditLog(database.DatabasePath).Append(auditEvent);
            }))
            .ToArray();

        release.Set();
        await Task.WhenAll(appends);

        var log = new AuditLog(database.DatabasePath);
        log.Verify();
        Assert.AreEqual(auditEvents.Length, log.GetProjectEvents(project.Id).Count);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                MIN(sequence_number),
                MAX(sequence_number),
                COUNT(DISTINCT audit_event_id),
                COUNT(DISTINCT event_hash)
            FROM audit_chain_entries;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual((long)auditEvents.Length, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual((long)auditEvents.Length, reader.GetInt64(2));
        Assert.AreEqual((long)auditEvents.Length, reader.GetInt64(3));
        Assert.AreEqual((long)auditEvents.Length, reader.GetInt64(4));
    }

    [TestMethod]
    public void ChainInsertFailureRollsBackTheAuditEventInsert()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Rollback audit project");
        var log = new AuditLog(database.DatabasePath);
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER reject_audit_chain_insert
            BEFORE INSERT ON audit_chain_entries
            BEGIN
                SELECT RAISE(ABORT, 'injected chain failure');
            END;
            """);

        Assert.ThrowsExactly<SqliteException>(
            () => log.Append(CreateAuditEvent(
                project.Id,
                EntityId<AuditEventIdTag>.New(),
                AuditTime,
                "This append must roll back.")));

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM audit_events;"));
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM audit_chain_entries;"));
    }

    [TestMethod]
    public void AppendHashesValuesReadBackFromStorage()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Stored value audit project");
        var log = new AuditLog(database.DatabasePath);
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER normalize_audit_reason
            AFTER INSERT ON audit_events
            BEGIN
                UPDATE audit_events
                SET reason = 'Stored trigger value.'
                WHERE id = NEW.id;
            END;
            """);

        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "Caller value."));

        log.Verify();
        Assert.AreEqual(
            "Stored trigger value.",
            log.GetProjectEvents(project.Id)[0].Reason);
    }

    [TestMethod]
    [DataRow("actor_id")]
    [DataRow("reason")]
    public void MalformedUtf8InHashedEventTextIsRejectedWithoutReplacement(string column)
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Malformed UTF-8 audit project");
        var log = new AuditLog(database.DatabasePath);
        string actorId = column == "actor_id" ? "\uFFFD" : "legacy-actor";
        string reason = column == "reason" ? "\uFFFD" : "Valid reason.";
        AuditEvent auditEvent = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            reason,
            actorId);

        log.Append(auditEvent);
        log.Verify();
        StoredAuditEvent stored = log.GetProjectEvents(project.Id).Single();
        Assert.AreEqual(actorId, stored.ActorId);
        Assert.AreEqual(reason, stored.Reason);

        string mutation = column switch
        {
            "actor_id" =>
                "UPDATE audit_events SET actor_id = CAST(X'80' AS TEXT) WHERE id = $id;",
            "reason" =>
                "UPDATE audit_events SET reason = CAST(X'80' AS TEXT) WHERE id = $id;",
            _ => throw new AssertFailedException("Unknown malformed UTF-8 column."),
        };
        ExecuteUncheckedMutation(
            database,
            mutation,
            ("$id", auditEvent.Id.ToString()));

        AssertInvalidChain(log);
    }

    [TestMethod]
    public void ValidUnicodeRoundTripsAndPassesHashVerification()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Unicode audit project");
        var log = new AuditLog(database.DatabasePath);
        const string actorId = "監査担当者-😀";
        const string reason = "承認理由：正常です。\uFFFD";
        AuditEvent auditEvent = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            reason,
            actorId);

        log.Append(auditEvent);
        log.Verify();

        StoredAuditEvent stored = log.GetProjectEvents(project.Id).Single();
        Assert.AreEqual(actorId, stored.ActorId);
        Assert.AreEqual(reason, stored.Reason);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(actor_id AS BLOB), CAST(reason AS BLOB)
            FROM audit_events
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", auditEvent.Id.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(actorId), (byte[])reader.GetValue(0));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(reason), (byte[])reader.GetValue(1));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("id")]
    [DataRow("project_id")]
    [DataRow("kind")]
    [DataRow("actor_id")]
    [DataRow("actor_role")]
    [DataRow("occurred_at")]
    [DataRow("reason")]
    [DataRow("composition_version_id")]
    [DataRow("composition_version_digest")]
    public void AnyCanonicalPayloadFieldMutationIsDetected(string column)
    {
        using StoreTestDatabase database = new();
        (Project project, CompositionVersion version) = CreateProjectAndVersion(database);
        Project otherProject = CreateProject(database, "Other tamper project");
        var log = new AuditLog(database.DatabasePath);
        AuditEvent auditEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.CompositionExported,
            "legacy-actor",
            ReviewActorRole.Editor,
            AuditTime,
            "Original reason.",
            project.Id,
            version);
        log.Append(auditEvent);

        object replacement = column switch
        {
            "id" => EntityId<AuditEventIdTag>.New().ToString(),
            "project_id" => otherProject.Id.ToString(),
            "kind" => AuditEventKind.PublicationRecorded.ToString(),
            "actor_id" => "different-legacy-actor",
            "actor_role" => ReviewActorRole.Administrator.ToString(),
            "occurred_at" => AuditTime.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture),
            "reason" => "Changed reason.",
            "composition_version_id" => EntityId<CompositionVersionIdTag>.New().ToString(),
            "composition_version_digest" => new string('f', 64),
            _ => throw new AssertFailedException("Unknown payload column."),
        };
        ExecuteUncheckedMutation(
            database,
            FormattableString.Invariant(
                $"UPDATE audit_events SET {column} = $value WHERE id = $id;"),
            ("$value", replacement),
            ("$id", auditEvent.Id.ToString()));

        AssertInvalidChain(log);
    }

    [TestMethod]
    [DataRow("event_hash")]
    [DataRow("previous_hash")]
    [DataRow("sequence_number")]
    [DataRow("format_version")]
    public void HashLinkSequenceOrFormatMutationIsDetected(string mutation)
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Chain tamper project");
        var log = new AuditLog(database.DatabasePath);
        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "First event."));
        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(1),
            "Second event."));

        (string Sql, object Value) change = mutation switch
        {
            "event_hash" => (
                "UPDATE audit_chain_entries SET event_hash = $value WHERE sequence_number = 1;",
                new string('f', 64)),
            "previous_hash" => (
                "UPDATE audit_chain_entries SET previous_hash = $value WHERE sequence_number = 2;",
                new string('e', 64)),
            "sequence_number" => (
                "UPDATE audit_chain_entries SET sequence_number = $value WHERE sequence_number = 2;",
                3L),
            "format_version" => (
                "UPDATE audit_chain_entries SET format_version = $value WHERE sequence_number = 2;",
                2L),
            _ => throw new AssertFailedException("Unknown chain mutation."),
        };
        ExecuteUncheckedMutation(database, change.Sql, ("$value", change.Value));

        AssertInvalidChain(log);
    }

    [TestMethod]
    public void DeletingMiddleChainAndEventRowsLeavesAGapThatIsDetected()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Middle deletion project");
        var log = new AuditLog(database.DatabasePath);
        foreach (int index in Enumerable.Range(0, 3))
        {
            log.Append(CreateAuditEvent(
                project.Id,
                EntityId<AuditEventIdTag>.New(),
                AuditTime.AddMinutes(index),
                FormattableString.Invariant($"Event {index}.")));
        }

        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            string eventId;
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT audit_event_id
                    FROM audit_chain_entries
                    WHERE sequence_number = 2;
                    """;
                eventId = (string)(read.ExecuteScalar()
                    ?? throw new AssertFailedException("The middle event was not found."));
            }

            ExecuteNonQuery(
                connection,
                transaction,
                "DELETE FROM audit_chain_entries WHERE sequence_number = 2;");
            ExecuteNonQuery(
                connection,
                transaction,
                "DELETE FROM audit_events WHERE id = $id;",
                ("$id", eventId));
            transaction.Commit();
        }

        AssertInvalidChain(log);
    }

    [TestMethod]
    public void RawUnchainedAuditEventInsertIsDetected()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Unchained event project");
        var log = new AuditLog(database.DatabasePath);
        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "Chained event."));
        InsertRawAuditEvent(
            database,
            EntityId<AuditEventIdTag>.New(),
            project.Id,
            AuditTime.AddMinutes(1));

        AssertInvalidChain(log);
    }

    [TestMethod]
    public void AppendToUnchainedStateRollsBackWithoutChangingCountsOrTail()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Unchained append project");
        var log = new AuditLog(database.DatabasePath);
        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "Chained event."));
        InsertRawAuditEvent(
            database,
            EntityId<AuditEventIdTag>.New(),
            project.Id,
            AuditTime.AddMinutes(1));
        AuditStorageState before = ReadAuditStorageState(database);
        AuditEvent attempted = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(2),
            "This append must roll back.");

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => log.Append(attempted));
        Assert.AreEqual("Stored audit chain data is invalid.", exception.Message);

        Assert.AreEqual(before, ReadAuditStorageState(database));
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM audit_events WHERE id = $id;",
                ("$id", attempted.Id.ToString())));
    }

    [TestMethod]
    public void AppendToTamperedChainRollsBackWithoutChangingCountsOrTail()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Tampered append project");
        var log = new AuditLog(database.DatabasePath);
        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "First event."));
        log.Append(CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(1),
            "Second event."));
        ExecuteUncheckedMutation(
            database,
            "UPDATE audit_chain_entries SET event_hash = $hash WHERE sequence_number = 1;",
            ("$hash", new string('f', 64)));
        AuditStorageState before = ReadAuditStorageState(database);
        AuditEvent attempted = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(2),
            "This append must roll back.");

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => log.Append(attempted));
        Assert.AreEqual("Stored audit chain data is invalid.", exception.Message);

        Assert.AreEqual(before, ReadAuditStorageState(database));
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM audit_events WHERE id = $id;",
                ("$id", attempted.Id.ToString())));
    }

    [TestMethod]
    public void BalancedUnchainedEventAndOrphanChainEntryAreDetected()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Orphan chain project");
        EntityId<AuditEventIdTag> unchainedId = EntityId<AuditEventIdTag>.New();
        EntityId<AuditEventIdTag> orphanId = EntityId<AuditEventIdTag>.New();
        InsertRawAuditEvent(database, unchainedId, project.Id, AuditTime);
        ExecuteUncheckedMutation(
            database,
            """
            INSERT INTO audit_chain_entries (
                sequence_number,
                audit_event_id,
                format_version,
                previous_hash,
                event_hash
            )
            VALUES (1, $audit_event_id, 1, $previous_hash, $event_hash);
            """,
            ("$audit_event_id", orphanId.ToString()),
            ("$previous_hash", new string('0', 64)),
            ("$event_hash", new string('f', 64)));

        AssertInvalidChain(new AuditLog(database.DatabasePath));
    }

    [TestMethod]
    public void CanonicalPayloadAndLengthPrefixedHashMatchKnownVector()
    {
        var stored = new StoredAuditEvent(
            EntityId<AuditEventIdTag>.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            EntityId<ProjectIdTag>.Parse("11111111-1111-4111-8111-111111111111"),
            AuditEventKind.ProjectRegistered,
            "legacy-actor",
            ReviewActorRole.Administrator,
            new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
            "Known vector.",
            compositionVersionId: null,
            compositionVersionDigest: null);
        byte[] payload = AuditChainCodec.CreateCanonicalPayload(1, stored);
        const string expectedPayload =
            "{\"actorId\":\"legacy-actor\",\"actorRole\":\"Administrator\",\"compositionVersionDigest\":null,\"compositionVersionId\":null,\"eventId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"formatVersion\":1,\"kind\":\"ProjectRegistered\",\"occurredAt\":\"2030-01-02T03:04:05.0000000\\u002B00:00\",\"projectId\":\"11111111-1111-4111-8111-111111111111\",\"reason\":\"Known vector.\",\"sequenceNumber\":1}";
        Assert.AreEqual(expectedPayload, Encoding.UTF8.GetString(payload));

        byte[] previousHash = new byte[32];
        byte[] actual = AuditChainCodec.ComputeEventHash(previousHash, payload);
        byte[] independentlyFramed = ComputeLengthPrefixedHash(
            AuditChainCodec.Domain,
            previousHash,
            payload);
        CollectionAssert.AreEqual(independentlyFramed, actual);
        Assert.AreEqual(
            "cfa0ad6af852fceaea40b3c366fe5ef5d704ea59eb3146ba8fbf77e5d3ad6b17",
            Convert.ToHexString(actual).ToLowerInvariant());
    }

    [TestMethod]
    public void RemovingAValidTailPairProducesAnotherInternallyValidSnapshot()
    {
        using StoreTestDatabase database = new();
        Project project = CreateProject(database, "Tail limitation project");
        var log = new AuditLog(database.DatabasePath);
        AuditEvent retained = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime,
            "Retained event.");
        AuditEvent removed = CreateAuditEvent(
            project.Id,
            EntityId<AuditEventIdTag>.New(),
            AuditTime.AddMinutes(1),
            "Removed tail event.");
        log.Append(retained);
        log.Append(removed);

        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "DELETE FROM audit_chain_entries WHERE sequence_number = 2;");
            ExecuteNonQuery(
                connection,
                transaction,
                "DELETE FROM audit_events WHERE id = $id;",
                ("$id", removed.Id.ToString()));
            transaction.Commit();
        }

        log.Verify();
        CollectionAssert.AreEqual(
            new[] { retained.Id },
            log.GetProjectEvents(project.Id)
                .Select(static auditEvent => auditEvent.Id)
                .ToArray());
    }

    [TestMethod]
    public void PublicApiIsAppendAndReadOnlyWithoutUpdateOrDeleteSurface()
    {
        Assert.IsTrue(typeof(AuditLog).IsPublic);
        Assert.IsTrue(typeof(AuditLog).IsSealed);
        Assert.IsFalse(typeof(AuditLog).IsGenericType);
        Assert.AreEqual(0, typeof(AuditLog).GetInterfaces().Length);
        ConstructorInfo[] constructors = typeof(AuditLog).GetConstructors();
        Assert.AreEqual(1, constructors.Length);
        CollectionAssert.AreEqual(
            new[] { typeof(string) },
            constructors[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        MethodInfo[] methods = typeof(AuditLog).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[] { nameof(AuditLog.Append), nameof(AuditLog.GetProjectEvents), nameof(AuditLog.Verify) },
            methods.Select(static method => method.Name).ToArray());
        Assert.IsFalse(methods.Any(static method =>
            method.Name.Contains("Update", StringComparison.Ordinal) ||
            method.Name.Contains("Delete", StringComparison.Ordinal)));
        Assert.IsFalse(typeof(AuditLog).GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any());
    }

    private static Project CreateProject(StoreTestDatabase database, string contentName)
    {
        var project = new Project(
            EntityId<ProjectIdTag>.New(),
            contentName,
            "Audit log verification.",
            "Auditors",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        new ProjectStore(database.DatabasePath).CreateProject(
            project,
            "{\"project\":1}",
            AuditTime);
        return project;
    }

    private static (Project Project, CompositionVersion Version) CreateProjectAndVersion(
        StoreTestDatabase database)
    {
        Project project = CreateProject(database, "Version audit project");
        var version = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            project.Id,
            parentVersionId: null,
            "legacy-version-author",
            AuditTime,
            "Create audit version.",
            new ContentDigest(new string('a', 64)),
            elements: []);
        new ProjectStore(database.DatabasePath).AppendCompositionVersion(
            version,
            "{\"version\":1}");
        return (project, version);
    }

    private static AuditEvent CreateAuditEvent(
        EntityId<ProjectIdTag> projectId,
        EntityId<AuditEventIdTag> id,
        DateTimeOffset occurredAt,
        string reason,
        string actorId = "legacy-actor")
    {
        return new AuditEvent(
            id,
            AuditEventKind.ProjectRegistered,
            actorId,
            ReviewActorRole.Administrator,
            occurredAt,
            reason,
            projectId);
    }

    private static void InsertRawAuditEvent(
        StoreTestDatabase database,
        EntityId<AuditEventIdTag> id,
        EntityId<ProjectIdTag> projectId,
        DateTimeOffset occurredAt)
    {
        database.ExecuteNonQuery(
            """
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
                NULL,
                NULL
            );
            """,
            ("$id", id.ToString()),
            ("$project_id", projectId.ToString()),
            ("$kind", AuditEventKind.ProjectRegistered.ToString()),
            ("$actor_id", "legacy-actor"),
            ("$actor_role", ReviewActorRole.Administrator.ToString()),
            ("$occurred_at", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("$reason", "Raw audit event."));
    }

    private static void ExecuteUncheckedMutation(
        StoreTestDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = database.OpenConnection();
        ExecuteNonQuery(connection, transaction: null, "PRAGMA foreign_keys = OFF;");
        ExecuteNonQuery(
            connection,
            transaction: null,
            "PRAGMA ignore_check_constraints = ON;");
        ExecuteNonQuery(connection, transaction: null, sql, parameters);
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static void AssertInvalidChain(AuditLog log)
    {
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            log.Verify);
        Assert.AreEqual("Stored audit chain data is invalid.", exception.Message);
    }

    private static AuditStorageState ReadAuditStorageState(StoreTestDatabase database)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM audit_events),
                (SELECT COUNT(*) FROM audit_chain_entries),
                COALESCE((
                    SELECT sequence_number
                    FROM audit_chain_entries
                    ORDER BY sequence_number DESC
                    LIMIT 1
                ), 0),
                COALESCE((
                    SELECT event_hash
                    FROM audit_chain_entries
                    ORDER BY sequence_number DESC
                    LIMIT 1
                ), '');
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new AssertFailedException("The audit storage state was not returned.");
        }

        var state = new AuditStorageState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3));
        Assert.IsFalse(reader.Read());
        return state;
    }

    private static byte[] ComputeLengthPrefixedHash(
        string domain,
        ReadOnlySpan<byte> previousHash,
        ReadOnlySpan<byte> payload)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(domain));
        AppendLengthPrefixed(hash, previousHash);
        AppendLengthPrefixed(hash, payload);
        return hash.GetHashAndReset();
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

    private sealed record AuditStorageState(
        long EventCount,
        long ChainCount,
        long TailSequenceNumber,
        string TailHash);
}
