using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class DatabaseMigratorTests
{
    private const string ProjectId = "11111111-1111-4111-8111-111111111111";
    private const string OtherProjectId = "22222222-2222-4222-8222-222222222222";
    private const string VersionId = "33333333-3333-4333-8333-333333333333";
    private const string OtherVersionId = "44444444-4444-4444-8444-444444444444";
    private const string WorkingCopyId = "55555555-5555-4555-8555-555555555555";
    private const string OtherWorkingCopyId = "66666666-6666-4666-8666-666666666666";
    private const string ReviewRecordId = "77777777-7777-4777-8777-777777777777";
    private const string ReviewCommentId = "88888888-8888-4888-8888-888888888888";
    private const string ApprovalId = "99999999-9999-4999-8999-999999999999";
    private const string AuditEventId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string ArtifactId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string OtherArtifactId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string SourceSpanId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string DigestA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Timestamp = "2030-01-02T03:04:05.6789012Z";

    private static readonly DateTimeOffset FixedUtcNow =
        new(2030, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

    [TestMethod]
    public void Migrate_EmptyDatabaseCreatesExactStrictSchemaBackupAndMetadata()
    {
        using TemporaryDatabase temporary = new();
        var timeProvider = new FixedTimeProvider(FixedUtcNow.AddTicks(9012));

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            timeProvider);

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(2, result.SchemaVersion);
        Assert.AreEqual(64, result.SchemaDigest.Hex.Length);
        Assert.AreEqual(result.SchemaDigest.Hex, result.SchemaDigest.Hex.ToLowerInvariant());
        Assert.IsTrue(typeof(DatabaseMigrationResult).IsSealed);
        Assert.IsTrue(
            typeof(DatabaseMigrationResult)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .All(property => property.SetMethod is null));

        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("An applied migration must return its backup path.");
        Assert.AreEqual(Path.GetFullPath(temporary.BackupDirectory), Path.GetDirectoryName(backupPath));
        Assert.IsTrue(File.Exists(backupPath));
        Assert.IsTrue(
            Regex.IsMatch(
                Path.GetFileName(backupPath),
                "^hubradio-before-schema-002-20300102T0304056789012Z-[0-9a-f]{32}\\.db$",
                RegexOptions.CultureInvariant));
        Assert.AreEqual(1, GetBackupFiles(temporary).Length);

        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), GetUserTableNames(backup));
            AssertDatabaseChecksPass(backup);
        }

        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        string[] expectedTables =
        [
            "approvals",
            "artifacts",
            "audit_events",
            "composition_versions",
            "job_attempts",
            "job_heartbeats",
            "job_progress",
            "jobs",
            "projects",
            "review_comments",
            "review_records",
            "schema_migrations",
            "working_copies",
        ];
        CollectionAssert.AreEqual(expectedTables, GetUserTableNames(connection));

        Dictionary<string, string[]> expectedColumns = new(StringComparer.Ordinal)
        {
            ["schema_migrations"] = ["version", "name", "content_digest", "applied_at"],
            ["projects"] =
                ["id", "revision", "publication_scope", "deadline", "payload", "updated_at"],
            ["composition_versions"] =
                ["id", "project_id", "parent_version_id", "content_digest", "created_at", "payload"],
            ["jobs"] =
            [
                "id",
                "project_id",
                "kind",
                "state",
                "created_at",
                "updated_at",
                "current_attempt_number",
                "revision",
            ],
            ["job_attempts"] =
            [
                "id",
                "job_id",
                "sequence_number",
                "state",
                "queued_at",
                "started_at",
                "completed_at",
                "error_code",
            ],
            ["job_progress"] =
            [
                "id",
                "job_id",
                "job_attempt_id",
                "sequence_number",
                "recorded_at",
                "completed_units",
                "total_units",
                "phase",
                "message",
            ],
            ["job_heartbeats"] =
                ["id", "job_id", "job_attempt_id", "sequence_number", "recorded_at"],
            ["working_copies"] =
                ["id", "project_id", "base_version_id", "revision", "updated_at", "payload"],
            ["review_records"] =
            [
                "id",
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "state",
                "actor_id",
                "actor_role",
                "recorded_at",
                "reason",
            ],
            ["review_comments"] =
            [
                "id",
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "output_value",
                "output_rate",
                "source_span_id",
                "actor_id",
                "actor_role",
                "created_at",
                "text",
            ],
            ["approvals"] =
            [
                "id",
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "actor_id",
                "actor_role",
                "approved_at",
                "reason",
            ],
            ["audit_events"] =
            [
                "id",
                "project_id",
                "kind",
                "actor_id",
                "actor_role",
                "occurred_at",
                "reason",
                "composition_version_id",
                "composition_version_digest",
            ],
            ["artifacts"] =
            [
                "id",
                "project_id",
                "kind",
                "storage_reference",
                "content_digest",
                "byte_size",
                "created_at",
            ],
        };

        foreach ((string table, string[] columns) in expectedColumns)
        {
            CollectionAssert.AreEqual(columns, GetTableColumns(connection, table), table);
        }

        using (SqliteCommand strictCommand = connection.CreateCommand())
        {
            strictCommand.CommandText = """
                SELECT name, strict
                FROM pragma_table_list
                WHERE schema = 'main'
                  AND type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;
            using SqliteDataReader reader = strictCommand.ExecuteReader();
            var strictTables = new List<string>();
            while (reader.Read())
            {
                strictTables.Add(reader.GetString(0));
                Assert.AreEqual(1L, reader.GetInt64(1), reader.GetString(0));
            }

            CollectionAssert.AreEqual(expectedTables, strictTables.ToArray());
        }

        string[] expectedIndexes =
        [
            "idx_approvals_project_id",
            "idx_approvals_version",
            "idx_artifacts_content_digest",
            "idx_audit_events_project_occurred_at",
            "idx_composition_versions_content_digest",
            "idx_composition_versions_project_id",
            "idx_job_attempts_job_id_sequence_number",
            "idx_job_heartbeats_attempt_recorded_at",
            "idx_job_progress_attempt_recorded_at",
            "idx_jobs_project_id",
            "idx_jobs_state_created_at",
            "idx_review_comments_project_id",
            "idx_review_comments_version",
            "idx_review_records_project_id",
            "idx_review_records_version",
        ];
        CollectionAssert.AreEqual(expectedIndexes, GetExplicitIndexNames(connection));
        CollectionAssert.AreEqual(
            new[] { "project_id", "occurred_at" },
            GetIndexColumns(connection, "idx_audit_events_project_occurred_at"));
        CollectionAssert.AreEqual(
            new[] { "composition_version_id", "project_id", "composition_version_digest" },
            GetIndexColumns(connection, "idx_review_comments_version"));

        AssertNoCascadeForeignKeys(connection, expectedTables);

        using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT version, name, content_digest, applied_at
                FROM schema_migrations;
                """;
            using SqliteDataReader reader = metadata.ExecuteReader();
            (long Version, string Name, ContentDigest Digest)[] expectedMigrations =
            [
                (1, "001_initial", ReadEmbeddedMigrationDigest("001_initial")),
                (2, "002_jobs", ReadEmbeddedMigrationDigest("002_jobs")),
            ];
            foreach ((long version, string name, ContentDigest digest) in expectedMigrations)
            {
                Assert.IsTrue(reader.Read());
                Assert.AreEqual(version, reader.GetInt64(0));
                Assert.AreEqual(name, reader.GetString(1));
                Assert.AreEqual(digest.Hex, reader.GetString(2));
                Assert.AreEqual(Timestamp, reader.GetString(3));
            }

            Assert.IsFalse(reader.Read());
        }

        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("001_initial"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("002_jobs"), result.SchemaDigest);
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migrate_ExactVersionOneDatabaseCreatesOneBackupAndAppliesOnlyVersionTwo()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionOneDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(1)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(2, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);

        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("An upgrade must return its backup path.");
        CollectionAssert.AreEqual(new[] { backupPath }, GetBackupFiles(temporary));

        string[] versionOneTables =
        [
            "approvals",
            "artifacts",
            "audit_events",
            "composition_versions",
            "projects",
            "review_comments",
            "review_records",
            "schema_migrations",
            "working_copies",
        ];
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            CollectionAssert.AreEqual(versionOneTables, GetUserTableNames(backup));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM projects;"));
            Assert.AreEqual(
                Timestamp,
                ExecuteScalarString(
                    backup,
                    "SELECT applied_at FROM schema_migrations WHERE version = 1;"));
            AssertDatabaseChecksPass(backup);
        }

        using SqliteConnection upgraded = OpenConnection(temporary.DatabasePath);
        CollectionAssert.AreEquivalent(
            new[] { "job_attempts", "job_heartbeats", "job_progress", "jobs" },
            GetUserTableNames(upgraded).Except(versionOneTables).ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "idx_job_attempts_job_id_sequence_number",
                "idx_job_heartbeats_attempt_recorded_at",
                "idx_job_progress_attempt_recorded_at",
                "idx_jobs_project_id",
                "idx_jobs_state_created_at",
            },
            GetExplicitIndexNames(upgraded)
                .Except(GetVersionOneExplicitIndexNames())
                .ToArray());
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM projects;"));
        Assert.AreEqual(
            2L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(
            "001_initial",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("001_initial").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 1;"));
        Assert.AreEqual(
            Timestamp,
            ExecuteScalarString(
                upgraded,
                "SELECT applied_at FROM schema_migrations WHERE version = 1;"));
        Assert.AreEqual(
            "002_jobs",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 2;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("002_jobs").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 2;"));
        AssertDatabaseChecksPass(upgraded);
    }

    [TestMethod]
    public void Migration002_DefinesOnlyTheRequiredStrictTablesColumnsForeignKeysAndIndexes()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        Dictionary<string, string[]> expectedColumns = new(StringComparer.Ordinal)
        {
            ["jobs"] =
            [
                "id",
                "project_id",
                "kind",
                "state",
                "created_at",
                "updated_at",
                "current_attempt_number",
                "revision",
            ],
            ["job_attempts"] =
            [
                "id",
                "job_id",
                "sequence_number",
                "state",
                "queued_at",
                "started_at",
                "completed_at",
                "error_code",
            ],
            ["job_progress"] =
            [
                "id",
                "job_id",
                "job_attempt_id",
                "sequence_number",
                "recorded_at",
                "completed_units",
                "total_units",
                "phase",
                "message",
            ],
            ["job_heartbeats"] =
                ["id", "job_id", "job_attempt_id", "sequence_number", "recorded_at"],
        };

        foreach ((string table, string[] columns) in expectedColumns)
        {
            CollectionAssert.AreEqual(columns, GetTableColumns(connection, table), table);
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    connection,
                    """
                        SELECT strict
                        FROM pragma_table_list
                        WHERE schema = 'main' AND type = 'table' AND name = $table;
                        """,
                    ("$table", table)),
                table);
        }

        CollectionAssert.AreEqual(
            new[] { "projects:project_id->id:RESTRICT:RESTRICT" },
            GetForeignKeyBindings(connection, "jobs"));
        CollectionAssert.AreEqual(
            new[] { "jobs:job_id->id:RESTRICT:RESTRICT" },
            GetForeignKeyBindings(connection, "job_attempts"));
        CollectionAssert.AreEqual(
            new[]
            {
                "job_attempts:job_id->job_id:RESTRICT:RESTRICT",
                "job_attempts:job_attempt_id->id:RESTRICT:RESTRICT",
                "job_attempts:sequence_number->sequence_number:RESTRICT:RESTRICT",
            },
            GetForeignKeyBindings(connection, "job_progress"));
        CollectionAssert.AreEqual(
            new[]
            {
                "job_attempts:job_id->job_id:RESTRICT:RESTRICT",
                "job_attempts:job_attempt_id->id:RESTRICT:RESTRICT",
                "job_attempts:sequence_number->sequence_number:RESTRICT:RESTRICT",
            },
            GetForeignKeyBindings(connection, "job_heartbeats"));

        Dictionary<string, string[]> expectedIndexes = new(StringComparer.Ordinal)
        {
            ["idx_jobs_state_created_at"] = ["state", "created_at"],
            ["idx_jobs_project_id"] = ["project_id"],
            ["idx_job_attempts_job_id_sequence_number"] = ["job_id", "sequence_number"],
            ["idx_job_progress_attempt_recorded_at"] = ["job_attempt_id", "recorded_at"],
            ["idx_job_heartbeats_attempt_recorded_at"] = ["job_attempt_id", "recorded_at"],
        };
        foreach ((string index, string[] columns) in expectedIndexes)
        {
            CollectionAssert.AreEqual(columns, GetIndexColumns(connection, index), index);
        }

        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT [unique]
                    FROM pragma_index_list('job_attempts')
                    WHERE name = 'idx_job_attempts_job_id_sequence_number';
                    """));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('view', 'trigger');"));
    }

    [TestMethod]
    public void Migration002_ChecksMirrorJobSnapshotsAndCompositeAttemptBindings()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        InsertProject(connection, ProjectId);
        InsertProject(connection, OtherProjectId);

        string jobId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        string otherJobId = "ffffffff-ffff-4fff-8fff-ffffffffffff";
        AssertConstraintViolation(
            () => InsertJob(connection, jobId, OtherVersionId, "kind", "queued", 0));
        AssertConstraintViolation(
            () => InsertJob(connection, jobId, ProjectId, " kind", "queued", 0));
        AssertConstraintViolation(
            () => InsertJob(connection, jobId, ProjectId, "kind", "unknown", 0));
        AssertConstraintViolation(
            () => InsertJob(connection, jobId, ProjectId, "kind", "queued", -1));
        AssertConstraintViolation(
            () => InsertJob(connection, jobId, ProjectId, "kind", "running", 0));
        InsertJob(
            connection,
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            ProjectId,
            "cancelled-before-start",
            "cancelled",
            0);
        AssertConstraintViolation(
            () => InsertJob(
                connection,
                jobId,
                ProjectId,
                "kind",
                "queued",
                0,
                updatedAt: "2029-01-02T03:04:05.6789012Z"));
        AssertConstraintViolation(
            () => InsertJob(connection, jobId, ProjectId, "kind", "queued", 0, revision: -1));

        InsertJob(connection, jobId, ProjectId, "kind", "queued", 0);
        InsertJob(connection, otherJobId, OtherProjectId, "kind", "running", 1);

        (string State, string? StartedAt, string? CompletedAt, string? ErrorCode)[] snapshots =
        [
            ("queued", null, null, null),
            ("running", Timestamp, null, null),
            ("succeeded", Timestamp, Timestamp, null),
            ("failed", Timestamp, Timestamp, "worker.process_failed"),
            ("cancelled", null, Timestamp, null),
            ("interrupted", Timestamp, Timestamp, "worker.process_interrupted"),
        ];
        var attemptIds = new List<string>();
        for (int index = 0; index < snapshots.Length; index++)
        {
            string attemptId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            attemptIds.Add(attemptId);
            (string state, string? startedAt, string? completedAt, string? errorCode) =
                snapshots[index];
            InsertJobAttempt(
                connection,
                attemptId,
                jobId,
                index + 1,
                state,
                startedAt,
                completedAt,
                errorCode);
        }

        string invalidAttemptId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 0, "queued", null, null, null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "queued", Timestamp, null, null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "running", null, null, null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "succeeded", Timestamp, null, null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "failed", Timestamp, Timestamp, null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "failed", Timestamp, Timestamp, "Worker.failed"));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "failed", Timestamp, Timestamp, "worker..failed"));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "failed", Timestamp, Timestamp, "worker.run__failed"));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection, invalidAttemptId, jobId, 7, "cancelled", null, null, null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection,
                invalidAttemptId,
                jobId,
                7,
                "interrupted",
                null,
                Timestamp,
                "worker.interrupted"));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection,
                invalidAttemptId,
                jobId,
                7,
                "running",
                "2029-01-02T03:04:05.6789012Z",
                null,
                null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection,
                invalidAttemptId,
                jobId,
                7,
                "succeeded",
                Timestamp,
                "2029-01-02T03:04:05.6789012Z",
                null));
        AssertConstraintViolation(
            () => InsertJobAttempt(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                1,
                "queued",
                null,
                null,
                null));

        string progressId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        InsertJobProgress(connection, progressId, jobId, attemptIds[1], 2, 3, 10, "decode", "Working");
        AssertConstraintViolation(
            () => InsertJobProgress(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                otherJobId,
                attemptIds[1],
                2,
                0,
                null,
                null,
                null));
        AssertConstraintViolation(
            () => InsertJobProgress(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                attemptIds[1],
                3,
                0,
                null,
                null,
                null));
        AssertConstraintViolation(
            () => InsertJobProgress(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                attemptIds[1],
                2,
                -1,
                null,
                null,
                null));
        AssertConstraintViolation(
            () => InsertJobProgress(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                attemptIds[1],
                2,
                2,
                1,
                null,
                null));
        AssertConstraintViolation(
            () => InsertJobProgress(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                attemptIds[1],
                2,
                0,
                0,
                null,
                null));
        AssertConstraintViolation(
            () => InsertJobProgress(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                attemptIds[1],
                2,
                0,
                null,
                " phase",
                null));

        string heartbeatId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        InsertJobHeartbeat(connection, heartbeatId, jobId, attemptIds[1], 2);
        AssertConstraintViolation(
            () => InsertJobHeartbeat(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                otherJobId,
                attemptIds[1],
                2));
        AssertConstraintViolation(
            () => InsertJobHeartbeat(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                jobId,
                attemptIds[1],
                1));

        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM job_attempts WHERE id = $id;",
                ("$id", attemptIds[1])));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM jobs WHERE id = $id;",
                ("$id", jobId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migrate_CurrentSchemaIsNoOpAndPreservesAppliedAtAndBackupCount()
    {
        using TemporaryDatabase temporary = new();
        DatabaseMigrationResult first = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddTicks(9012)));
        string originalAppliedAt;
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            originalAppliedAt = ExecuteScalarString(
                connection,
                "SELECT applied_at FROM schema_migrations WHERE version = 1;");
        }

        string[] originalBackups = GetBackupFiles(temporary);
        DatabaseMigrationResult second = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());

        Assert.IsFalse(second.Applied);
        Assert.IsNull(second.BackupPath);
        Assert.AreEqual(2, second.SchemaVersion);
        Assert.AreEqual(first.SchemaDigest, second.SchemaDigest);
        CollectionAssert.AreEqual(originalBackups, GetBackupFiles(temporary));

        using SqliteConnection verification = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(
            originalAppliedAt,
            ExecuteScalarString(
                verification,
                "SELECT applied_at FROM schema_migrations WHERE version = 1;"));
        AssertDatabaseChecksPass(verification);
    }

    [TestMethod]
    public void Migrate_CurrentSchemaWithLatentForeignKeyViolationFailsValidationWithoutBackup()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");
            InsertCompositionVersion(
                connection,
                VersionId,
                ProjectId,
                parentVersionId: null,
                DigestA);
        }

        string[] backupsBeforeValidation = GetBackupFiles(temporary);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new AlwaysThrowingTimeProvider()));
        CollectionAssert.AreEqual(backupsBeforeValidation, GetBackupFiles(temporary));

        using SqliteConnection verification = OpenConnection(temporary.DatabasePath);
        using SqliteCommand command = verification.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
    }

    [TestMethod]
    public void Schema_RejectsForeignKeyViolationsAndRestrictedDeletes()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        AssertConstraintViolation(
            () => InsertCompositionVersion(
                connection,
                VersionId,
                ProjectId,
                parentVersionId: null,
                DigestA));

        InsertProject(connection, ProjectId);
        InsertCompositionVersion(
            connection,
            VersionId,
            ProjectId,
            parentVersionId: null,
            DigestA);

        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM projects WHERE id = $id;",
                ("$id", ProjectId)));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                "SELECT COUNT(*) FROM projects WHERE id = $id;",
                ("$id", ProjectId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Schema_RejectsInvalidIdsDigestsPayloadsAndSelfParents()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        AssertConstraintViolation(() => InsertProject(connection, string.Empty));
        AssertConstraintViolation(() => InsertProject(connection, AuditEventId.ToUpperInvariant()));
        AssertConstraintViolation(
            () => InsertProject(connection, ProjectId, payload: Array.Empty<byte>()));
        AssertConstraintViolation(
            () => InsertProject(connection, ProjectId, payload: "not-a-blob"));

        InsertProject(connection, ProjectId);
        AssertConstraintViolation(
            () => InsertCompositionVersion(
                connection,
                VersionId,
                ProjectId,
                parentVersionId: null,
                DigestA.ToUpperInvariant()));
        AssertConstraintViolation(
            () => InsertCompositionVersion(
                connection,
                VersionId,
                ProjectId,
                parentVersionId: null,
                new string('g', 64)));
        AssertConstraintViolation(
            () => InsertCompositionVersion(
                connection,
                VersionId,
                ProjectId,
                VersionId,
                DigestA));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Schema_ReviewBindingsRationalRateAndAuditOptionalBindingAreEnforced()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        InsertProject(connection, ProjectId);
        InsertProject(connection, OtherProjectId);
        InsertCompositionVersion(
            connection,
            VersionId,
            ProjectId,
            parentVersionId: null,
            DigestA);

        AssertConstraintViolation(
            () => InsertReviewRecord(connection, ReviewRecordId, OtherProjectId, VersionId, DigestA));
        AssertConstraintViolation(
            () => InsertReviewRecord(connection, ReviewRecordId, ProjectId, VersionId, DigestB));
        InsertReviewRecord(connection, ReviewRecordId, ProjectId, VersionId, DigestA);

        AssertConstraintViolation(
            () => InsertReviewComment(
                connection,
                ReviewCommentId,
                ProjectId,
                VersionId,
                DigestA,
                outputValue: 0,
                outputRate: 0));
        InsertReviewComment(
            connection,
            ReviewCommentId,
            ProjectId,
            VersionId,
            DigestA,
            outputValue: 0,
            outputRate: 1);

        AssertConstraintViolation(
            () => InsertAuditEvent(
                connection,
                AuditEventId,
                ProjectId,
                VersionId,
                compositionVersionDigest: null));
        AssertConstraintViolation(
            () => InsertAuditEvent(
                connection,
                AuditEventId,
                ProjectId,
                compositionVersionId: null,
                DigestA));
        AssertConstraintViolation(
            () => InsertAuditEvent(
                connection,
                AuditEventId,
                ProjectId,
                VersionId,
                DigestB));
        InsertAuditEvent(
            connection,
            AuditEventId,
            ProjectId,
            compositionVersionId: null,
            compositionVersionDigest: null);
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Schema_WorkingCopyArtifactAndReviewUniquenessChecksAreEnforced()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        InsertProject(connection, ProjectId);
        InsertCompositionVersion(
            connection,
            VersionId,
            ProjectId,
            parentVersionId: null,
            DigestA);

        InsertWorkingCopy(connection, WorkingCopyId, ProjectId, VersionId);
        AssertConstraintViolation(
            () => InsertWorkingCopy(connection, OtherWorkingCopyId, ProjectId, VersionId));

        InsertApproval(connection, ApprovalId, ProjectId, VersionId, DigestA);
        AssertConstraintViolation(
            () => InsertApproval(connection, ApprovalId, ProjectId, VersionId, DigestA));

        AssertConstraintViolation(
            () => InsertArtifact(
                connection,
                ArtifactId,
                ProjectId,
                "renders/final.mp4",
                byteSize: 0));
        InsertArtifact(
            connection,
            ArtifactId,
            ProjectId,
            "renders/final.mp4",
            byteSize: 1);
        AssertConstraintViolation(
            () => InsertArtifact(
                connection,
                OtherArtifactId,
                ProjectId,
                "renders/final.mp4",
                byteSize: 2));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Schema_RejectsCrossProjectParentAndWorkingCopyBaseVersions()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        InsertProject(connection, ProjectId);
        InsertProject(connection, OtherProjectId);
        InsertCompositionVersion(
            connection,
            VersionId,
            ProjectId,
            parentVersionId: null,
            DigestA);

        AssertConstraintViolation(
            () => InsertCompositionVersion(
                connection,
                OtherVersionId,
                OtherProjectId,
                VersionId,
                DigestB));
        AssertConstraintViolation(
            () => InsertWorkingCopy(
                connection,
                WorkingCopyId,
                OtherProjectId,
                VersionId));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migrate_UnmanagedDatabaseCreatesValidBackupThenFailsClosed()
    {
        using TemporaryDatabase temporary = new();
        using (SqliteConnection connection = OpenConnection(
            temporary.DatabasePath,
            SqliteOpenMode.ReadWriteCreate))
        {
            ExecuteNonQuery(
                connection,
                "CREATE TABLE unmanaged_data (id INTEGER NOT NULL PRIMARY KEY) STRICT;");
            ExecuteNonQuery(connection, "INSERT INTO unmanaged_data (id) VALUES (1);");
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow)));

        string[] backups = GetBackupFiles(temporary);
        Assert.AreEqual(1, backups.Length);
        using (SqliteConnection backup = OpenConnection(backups[0], SqliteOpenMode.ReadOnly))
        {
            CollectionAssert.AreEqual(new[] { "unmanaged_data" }, GetUserTableNames(backup));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM unmanaged_data;"));
            AssertDatabaseChecksPass(backup);
        }

        using SqliteConnection source = OpenConnection(temporary.DatabasePath);
        CollectionAssert.AreEqual(new[] { "unmanaged_data" }, GetUserTableNames(source));
        AssertDatabaseChecksPass(source);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("digest")]
    [DataRow("missing")]
    [DataRow("gap")]
    [DataRow("future")]
    public void Migrate_TamperedGapOrUnknownMetadataCreatesBackupThenFailsClosed(string mutation)
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            switch (mutation)
            {
                case "name":
                    ExecuteNonQuery(
                        connection,
                        "UPDATE schema_migrations SET name = $name WHERE version = 2;",
                        ("$name", "001_changed"));
                    break;
                case "digest":
                    ExecuteNonQuery(
                        connection,
                        "UPDATE schema_migrations SET content_digest = $digest WHERE version = 2;",
                        ("$digest", DigestB));
                    break;
                case "missing":
                    ExecuteNonQuery(connection, "DELETE FROM schema_migrations;");
                    break;
                case "gap":
                    ExecuteNonQuery(
                        connection,
                        "DELETE FROM schema_migrations WHERE version = 1;");
                    break;
                case "future":
                    ExecuteNonQuery(
                        connection,
                        """
                            INSERT INTO schema_migrations (
                                version,
                                name,
                                content_digest,
                                applied_at
                            )
                            VALUES (3, '003_unknown', $digest, $applied_at);
                            """,
                        ("$digest", DigestA),
                        ("$applied_at", Timestamp));
                    break;
                default:
                    throw new AssertFailedException("Unknown test mutation.");
            }
        }

        int originalBackupCount = GetBackupFiles(temporary).Length;
        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow.AddMinutes(1))));

        string[] backups = GetBackupFiles(temporary);
        Assert.AreEqual(originalBackupCount + 1, backups.Length);
        foreach (string backupPath in backups)
        {
            using SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly);
            AssertIntegrityCheckIsExactlyOk(backup);
        }

        using SqliteConnection source = OpenConnection(temporary.DatabasePath);
        AssertDatabaseChecksPass(source);
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("index")]
    [DataRow("jobs-table")]
    [DataRow("jobs-index")]
    public void Migrate_CurrentMetadataWithAlteredSchemaCreatesBackupThenFailsClosed(
        string mutation)
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            ExecuteNonQuery(
                connection,
                mutation switch
                {
                    "table" => "DROP TABLE artifacts;",
                    "index" => "DROP INDEX idx_artifacts_content_digest;",
                    "jobs-table" => "DROP TABLE job_heartbeats;",
                    "jobs-index" => "DROP INDEX idx_jobs_state_created_at;",
                    _ => throw new AssertFailedException("Unknown schema mutation."),
                });
            AssertDatabaseChecksPass(connection);
        }

        int originalBackupCount = GetBackupFiles(temporary).Length;
        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow.AddMinutes(1))));

        Assert.AreEqual(
            originalBackupCount + 1,
            GetBackupFiles(temporary).Length);
    }

    [TestMethod]
    public void Migrate_RepeatedMetadataFailureNeverOverwritesExistingBackup()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            ExecuteNonQuery(
                connection,
                "UPDATE schema_migrations SET content_digest = $digest;",
                ("$digest", DigestB));
        }

        string[] before = GetBackupFiles(temporary);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => DatabaseMigrator.Migrate(
                    temporary.DatabasePath,
                    temporary.BackupDirectory,
                    new FixedTimeProvider(FixedUtcNow)));
        }

        string[] after = GetBackupFiles(temporary);
        Assert.AreEqual(before.Length + 2, after.Length);
        Assert.AreEqual(after.Length, after.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        CollectionAssert.IsSubsetOf(before, after);
        Assert.IsTrue(after.All(path => new FileInfo(path).Length > 0));
    }

    [TestMethod]
    public void Migrate_MalformedMigrationTableCreatesBackupThenFailsClosed()
    {
        using TemporaryDatabase temporary = new();
        using (SqliteConnection connection = OpenConnection(
            temporary.DatabasePath,
            SqliteOpenMode.ReadWriteCreate))
        {
            ExecuteNonQuery(
                connection,
                "CREATE TABLE schema_migrations (unexpected INTEGER NOT NULL) STRICT;");
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow)));

        string[] backups = GetBackupFiles(temporary);
        Assert.AreEqual(1, backups.Length);
        using SqliteConnection backup = OpenConnection(backups[0], SqliteOpenMode.ReadOnly);
        CollectionAssert.AreEqual(new[] { "schema_migrations" }, GetUserTableNames(backup));
        AssertIntegrityCheckIsExactlyOk(backup);
    }

    [TestMethod]
    public void Migrate_FailureAfterSchemaSqlRollsBackAndPreservesBackup()
    {
        using TemporaryDatabase temporary = new();
        var timeProvider = new ThrowOnSecondUtcNowTimeProvider(FixedUtcNow);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                timeProvider));

        string[] backupsAfterFailure = GetBackupFiles(temporary);
        Assert.AreEqual(1, backupsAfterFailure.Length);
        using (SqliteConnection failedSource = OpenConnection(temporary.DatabasePath))
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), GetUserTableNames(failedSource));
            AssertDatabaseChecksPass(failedSource);
        }

        using (SqliteConnection backup = OpenConnection(
            backupsAfterFailure[0],
            SqliteOpenMode.ReadOnly))
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), GetUserTableNames(backup));
            AssertDatabaseChecksPass(backup);
        }

        DatabaseMigrationResult retry = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(1)));
        Assert.IsTrue(retry.Applied);
        Assert.AreEqual(2, GetBackupFiles(temporary).Length);
        using SqliteConnection migrated = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(13, GetUserTableNames(migrated).Length);
        AssertDatabaseChecksPass(migrated);
    }

    [TestMethod]
    public void Schema_FailedInsertTransactionRollsBackWithoutChangingSchema()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            InsertProject(connection, ProjectId, transaction);
            AssertConstraintViolation(
                () => InsertProject(connection, ProjectId.ToUpperInvariant(), transaction));
        }

        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM projects;"));
        Assert.AreEqual(
            2L,
            ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(13, GetUserTableNames(connection).Length);
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migrate_RelativeUncAndDevicePathsAreRejectedBeforeIo()
    {
        using TemporaryDatabase temporary = new();
        string untouchedDatabasePath = Path.Combine(temporary.RootPath, "untouched.db");
        string untouchedBackupDirectory = Path.Combine(temporary.RootPath, "untouched-backups");
        string[] invalidPaths =
        [
            "relative.db",
            ".\\relative.db",
            "..\\relative.db",
            @"\\server\share\hubradio.db",
            @"\\?\C:\hubradio\hubradio.db",
            @"\\.\C:\hubradio\hubradio.db",
        ];

        foreach (string invalidDatabasePath in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => DatabaseMigrator.Migrate(
                    invalidDatabasePath,
                    untouchedBackupDirectory));
            Assert.AreEqual("databasePath", exception.ParamName);
            Assert.IsFalse(Directory.Exists(untouchedBackupDirectory));
        }

        foreach (string invalidBackupDirectory in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => DatabaseMigrator.Migrate(
                    untouchedDatabasePath,
                    invalidBackupDirectory));
            Assert.AreEqual("backupDirectory", exception.ParamName);
            Assert.IsFalse(File.Exists(untouchedDatabasePath));
        }
    }

    private static TemporaryDatabase CreateMigratedDatabase()
    {
        var temporary = new TemporaryDatabase();
        try
        {
            DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow.AddTicks(9012)));
            return temporary;
        }
        catch
        {
            temporary.Dispose();
            throw;
        }
    }

    private static void CreateExactVersionOneDatabase(TemporaryDatabase temporary)
    {
        using SqliteConnection connection = OpenConnection(
            temporary.DatabasePath,
            SqliteOpenMode.ReadWriteCreate);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("001_initial"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (1, '001_initial', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("001_initial").Hex),
            ("$applied_at", Timestamp));
        InsertProject(connection, ProjectId);
        AssertDatabaseChecksPass(connection);
    }

    private static SqliteConnection OpenConnection(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static string[] GetUserTableNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static string[] GetTableColumns(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM pragma_table_info($table)
            ORDER BY cid;
            """;
        command.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static string[] GetExplicitIndexNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static string[] GetIndexColumns(SqliteConnection connection, string index)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM pragma_index_info($index)
            ORDER BY seqno;
            """;
        command.Parameters.AddWithValue("$index", index);
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static string[] GetForeignKeyBindings(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT [table], [from], [to], on_update, on_delete
            FROM pragma_foreign_key_list($table)
            ORDER BY id, seq;
            """;
        command.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = command.ExecuteReader();
        var bindings = new List<string>();
        while (reader.Read())
        {
            bindings.Add(string.Concat(
                reader.GetString(0),
                ":",
                reader.GetString(1),
                "->",
                reader.GetString(2),
                ":",
                reader.GetString(3),
                ":",
                reader.GetString(4)));
        }

        return bindings.ToArray();
    }

    private static string[] GetVersionOneExplicitIndexNames() =>
    [
        "idx_approvals_project_id",
        "idx_approvals_version",
        "idx_artifacts_content_digest",
        "idx_audit_events_project_occurred_at",
        "idx_composition_versions_content_digest",
        "idx_composition_versions_project_id",
        "idx_review_comments_project_id",
        "idx_review_comments_version",
        "idx_review_records_project_id",
        "idx_review_records_version",
    ];

    private static void AssertNoCascadeForeignKeys(
        SqliteConnection connection,
        IEnumerable<string> tables)
    {
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT on_update, on_delete
                FROM pragma_foreign_key_list($table);
                """;
            command.Parameters.AddWithValue("$table", table);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                Assert.AreEqual("RESTRICT", reader.GetString(0), table);
                Assert.AreEqual("RESTRICT", reader.GetString(1), table);
            }
        }
    }

    private static void AssertDatabaseChecksPass(SqliteConnection connection)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.IsFalse(reader.Read());
        }

        AssertIntegrityCheckIsExactlyOk(connection);
    }

    private static void AssertIntegrityCheckIsExactlyOk(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("ok", reader.GetString(0));
        Assert.IsFalse(reader.Read());
    }

    private static void AssertConstraintViolation(Action action)
    {
        SqliteException exception = Assert.ThrowsExactly<SqliteException>(action);
        Assert.AreEqual(19, exception.SqliteErrorCode);
    }

    private static void InsertProject(
        SqliteConnection connection,
        string id,
        SqliteTransaction? transaction = null,
        object? payload = null)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO projects (
                    id,
                    revision,
                    publication_scope,
                    deadline,
                    payload,
                    updated_at
                )
                VALUES ($id, 0, $publication_scope, NULL, $payload, $updated_at);
                """,
            transaction,
            ("$id", id),
            ("$publication_scope", "InternalOnly"),
            ("$payload", payload ?? new byte[] { 1 }),
            ("$updated_at", Timestamp));
    }

    private static void InsertCompositionVersion(
        SqliteConnection connection,
        string id,
        string projectId,
        string? parentVersionId,
        string digest)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO composition_versions (
                    id,
                    project_id,
                    parent_version_id,
                    content_digest,
                    created_at,
                    payload
                )
                VALUES ($id, $project_id, $parent_version_id, $digest, $created_at, $payload);
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$parent_version_id", parentVersionId),
            ("$digest", digest),
            ("$created_at", Timestamp),
            ("$payload", new byte[] { 1 }));
    }

    private static void InsertWorkingCopy(
        SqliteConnection connection,
        string id,
        string projectId,
        string? baseVersionId)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO working_copies (
                    id,
                    project_id,
                    base_version_id,
                    revision,
                    updated_at,
                    payload
                )
                VALUES ($id, $project_id, $base_version_id, 0, $updated_at, $payload);
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$base_version_id", baseVersionId),
            ("$updated_at", Timestamp),
            ("$payload", new byte[] { 1 }));
    }

    private static void InsertReviewRecord(
        SqliteConnection connection,
        string id,
        string projectId,
        string versionId,
        string digest)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO review_records (
                    id,
                    project_id,
                    composition_version_id,
                    composition_version_digest,
                    state,
                    actor_id,
                    actor_role,
                    recorded_at,
                    reason
                )
                VALUES (
                    $id,
                    $project_id,
                    $version_id,
                    $digest,
                    $state,
                    $actor_id,
                    $actor_role,
                    $recorded_at,
                    $reason
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$version_id", versionId),
            ("$digest", digest),
            ("$state", "EditorReview"),
            ("$actor_id", "S-1-5-21-1000"),
            ("$actor_role", "Editor"),
            ("$recorded_at", Timestamp),
            ("$reason", "Ready for review"));
    }

    private static void InsertReviewComment(
        SqliteConnection connection,
        string id,
        string projectId,
        string versionId,
        string digest,
        long outputValue,
        long outputRate)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO review_comments (
                    id,
                    project_id,
                    composition_version_id,
                    composition_version_digest,
                    output_value,
                    output_rate,
                    source_span_id,
                    actor_id,
                    actor_role,
                    created_at,
                    text
                )
                VALUES (
                    $id,
                    $project_id,
                    $version_id,
                    $digest,
                    $output_value,
                    $output_rate,
                    $source_span_id,
                    $actor_id,
                    $actor_role,
                    $created_at,
                    $text
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$version_id", versionId),
            ("$digest", digest),
            ("$output_value", outputValue),
            ("$output_rate", outputRate),
            ("$source_span_id", SourceSpanId),
            ("$actor_id", "S-1-5-21-1000"),
            ("$actor_role", "Editor"),
            ("$created_at", Timestamp),
            ("$text", "Please shorten this section"));
    }

    private static void InsertApproval(
        SqliteConnection connection,
        string id,
        string projectId,
        string versionId,
        string digest)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO approvals (
                    id,
                    project_id,
                    composition_version_id,
                    composition_version_digest,
                    actor_id,
                    actor_role,
                    approved_at,
                    reason
                )
                VALUES (
                    $id,
                    $project_id,
                    $version_id,
                    $digest,
                    $actor_id,
                    $actor_role,
                    $approved_at,
                    $reason
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$version_id", versionId),
            ("$digest", digest),
            ("$actor_id", "S-1-5-21-1000"),
            ("$actor_role", "ContentApprover"),
            ("$approved_at", Timestamp),
            ("$reason", "Content approved"));
    }

    private static void InsertAuditEvent(
        SqliteConnection connection,
        string id,
        string projectId,
        string? compositionVersionId,
        string? compositionVersionDigest)
    {
        ExecuteNonQuery(
            connection,
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
                    $version_id,
                    $digest
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$kind", "ProjectRegistered"),
            ("$actor_id", "S-1-5-21-1000"),
            ("$actor_role", "Administrator"),
            ("$occurred_at", Timestamp),
            ("$reason", "Project registered"),
            ("$version_id", compositionVersionId),
            ("$digest", compositionVersionDigest));
    }

    private static void InsertArtifact(
        SqliteConnection connection,
        string id,
        string projectId,
        string storageReference,
        long byteSize)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO artifacts (
                    id,
                    project_id,
                    kind,
                    storage_reference,
                    content_digest,
                    byte_size,
                    created_at
                )
                VALUES (
                    $id,
                    $project_id,
                    $kind,
                    $storage_reference,
                    $digest,
                    $byte_size,
                    $created_at
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$kind", "FinalRender"),
            ("$storage_reference", storageReference),
            ("$digest", DigestA),
            ("$byte_size", byteSize),
            ("$created_at", Timestamp));
    }

    private static void InsertJob(
        SqliteConnection connection,
        string id,
        string projectId,
        string kind,
        string state,
        long currentAttemptNumber,
        string updatedAt = Timestamp,
        long revision = 0)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO jobs (
                    id,
                    project_id,
                    kind,
                    state,
                    created_at,
                    updated_at,
                    current_attempt_number,
                    revision
                )
                VALUES (
                    $id,
                    $project_id,
                    $kind,
                    $state,
                    $created_at,
                    $updated_at,
                    $current_attempt_number,
                    $revision
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$kind", kind),
            ("$state", state),
            ("$created_at", Timestamp),
            ("$updated_at", updatedAt),
            ("$current_attempt_number", currentAttemptNumber),
            ("$revision", revision));
    }

    private static void InsertJobAttempt(
        SqliteConnection connection,
        string id,
        string jobId,
        long sequenceNumber,
        string state,
        string? startedAt,
        string? completedAt,
        string? errorCode)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO job_attempts (
                    id,
                    job_id,
                    sequence_number,
                    state,
                    queued_at,
                    started_at,
                    completed_at,
                    error_code
                )
                VALUES (
                    $id,
                    $job_id,
                    $sequence_number,
                    $state,
                    $queued_at,
                    $started_at,
                    $completed_at,
                    $error_code
                );
                """,
            ("$id", id),
            ("$job_id", jobId),
            ("$sequence_number", sequenceNumber),
            ("$state", state),
            ("$queued_at", Timestamp),
            ("$started_at", startedAt),
            ("$completed_at", completedAt),
            ("$error_code", errorCode));
    }

    private static void InsertJobProgress(
        SqliteConnection connection,
        string id,
        string jobId,
        string jobAttemptId,
        long sequenceNumber,
        long completedUnits,
        long? totalUnits,
        string? phase,
        string? message)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO job_progress (
                    id,
                    job_id,
                    job_attempt_id,
                    sequence_number,
                    recorded_at,
                    completed_units,
                    total_units,
                    phase,
                    message
                )
                VALUES (
                    $id,
                    $job_id,
                    $job_attempt_id,
                    $sequence_number,
                    $recorded_at,
                    $completed_units,
                    $total_units,
                    $phase,
                    $message
                );
                """,
            ("$id", id),
            ("$job_id", jobId),
            ("$job_attempt_id", jobAttemptId),
            ("$sequence_number", sequenceNumber),
            ("$recorded_at", Timestamp),
            ("$completed_units", completedUnits),
            ("$total_units", totalUnits),
            ("$phase", phase),
            ("$message", message));
    }

    private static void InsertJobHeartbeat(
        SqliteConnection connection,
        string id,
        string jobId,
        string jobAttemptId,
        long sequenceNumber)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO job_heartbeats (
                    id,
                    job_id,
                    job_attempt_id,
                    sequence_number,
                    recorded_at
                )
                VALUES (
                    $id,
                    $job_id,
                    $job_attempt_id,
                    $sequence_number,
                    $recorded_at
                );
                """,
            ("$id", id),
            ("$job_id", jobId),
            ("$job_attempt_id", jobAttemptId),
            ("$sequence_number", sequenceNumber),
            ("$recorded_at", Timestamp));
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters) =>
        ExecuteNonQuery(connection, sql, transaction: null, parameters);

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static long ExecuteScalarInt64(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ExecuteScalarString(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new AssertFailedException("The scalar query returned null.");
    }

    private static ContentDigest ReadEmbeddedMigrationDigest(string migrationName)
    {
        using Stream resource = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(
            $"HubRadio.Infrastructure.Database.Migrations.{migrationName}.sql")
            ?? throw new AssertFailedException(
                $"The {migrationName} migration resource was not embedded.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return ContentDigest.Compute(bytes.ToArray());
    }

    private static string ReadEmbeddedMigrationSql(string migrationName)
    {
        using Stream resource = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(
            $"HubRadio.Infrastructure.Database.Migrations.{migrationName}.sql")
            ?? throw new AssertFailedException(
                $"The {migrationName} migration resource was not embedded.");
        using var reader = new StreamReader(
            resource,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static ContentDigest ComputeExpectedSchemaDigest()
    {
        const string domain = "HubRadio.MovieCreator.DatabaseMigrationSet/v1";
        (int Version, string Name, ContentDigest Digest)[] migrations =
        [
            (1, "001_initial", ReadEmbeddedMigrationDigest("001_initial")),
            (2, "002_jobs", ReadEmbeddedMigrationDigest("002_jobs")),
        ];

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, tag: 1, Encoding.UTF8.GetBytes(domain));
        AppendInt32Field(hash, tag: 2, migrations.Length);
        foreach ((int version, string name, ContentDigest digest) in migrations)
        {
            AppendInt32Field(hash, tag: 3, version);
            AppendField(hash, tag: 4, Encoding.UTF8.GetBytes(name));
            AppendField(hash, tag: 5, Convert.FromHexString(digest.Hex));
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static void AppendInt32Field(IncrementalHash hash, byte tag, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        AppendField(hash, tag, bytes);
    }

    private static void AppendField(
        IncrementalHash hash,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[sizeof(byte) + sizeof(int)];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], value.Length);
        hash.AppendData(header);
        hash.AppendData(value);
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static string[] GetBackupFiles(TemporaryDatabase temporary) =>
        Directory.Exists(temporary.BackupDirectory)
            ? Directory.GetFiles(temporary.BackupDirectory, "*.db").Order().ToArray()
            : Array.Empty<string>();

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"hubradio-database-migrator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "hubradio.db");
            BackupDirectory = Path.Combine(RootPath, "backups");
        }

        public string RootPath { get; }

        public string DatabasePath { get; }

        public string BackupDirectory { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AlwaysThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("Time must not be read on a no-op migration.");
    }

    private sealed class ThrowOnSecondUtcNowTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset firstValue;
        private int invocationCount;

        public ThrowOnSecondUtcNowTimeProvider(DateTimeOffset firstValue)
        {
            this.firstValue = firstValue;
        }

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                return firstValue;
            }

            throw new InvalidOperationException("Injected migration metadata failure.");
        }
    }
}
