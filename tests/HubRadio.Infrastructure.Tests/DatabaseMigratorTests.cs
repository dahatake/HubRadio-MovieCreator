using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Audit;
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
    private const string OtherAuditEventId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string ThirdAuditEventId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string ArtifactId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string OtherArtifactId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string SourceSpanId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string TermEntryId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
    private const string OtherTermEntryId = "ffffffff-ffff-4fff-8fff-ffffffffffff";
    private const string SeriesId = "abababab-abab-4bab-8bab-abababababab";
    private const string ProjectCreationDraftId = "cdcdcdcd-cdcd-4dcd-8dcd-cdcdcdcdcdcd";
    private const string OtherProjectCreationDraftId = "dededede-dede-4ede-8ede-dededededede";
    private const string DefaultSettingsProfileId = "a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1";
    private const string OtherDefaultSettingsProfileId = "b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2";
    private const string DigestA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Timestamp = "2030-01-02T03:04:05.6789012Z";
    private const string CanonicalEventTimestamp =
        "2030-01-02T03:04:05.6789012+00:00";

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
        Assert.AreEqual(14, result.SchemaVersion);
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
                "^hubradio-before-schema-014-20300102T0304056789012Z-[0-9a-f]{32}\\.db$",
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
            "audit_chain_entries",
            "audit_events",
            "automated_evaluation_rules",
            "automated_evaluations",
            "composition_versions",
            "default_settings_global",
            "default_settings_profiles",
            "ingest_workflows",
            "job_attempts",
            "job_heartbeats",
            "job_progress",
            "jobs",
            "project_creation_draft_deletions",
            "project_creation_drafts",
            "project_default_settings",
            "project_display_names",
            "projects",
            "quick_start_state_events",
            "quick_start_workflows",
            "review_comments",
            "review_records",
            "role_assignments",
            "schema_migrations",
            "source_only_executions",
            "subtitle_edits",
            "term_entries",
            "working_copies",
        ];
        Assert.AreEqual(29, expectedTables.Length);
        CollectionAssert.AreEqual(expectedTables, GetUserTableNames(connection));

        Dictionary<string, string[]> expectedColumns = new(StringComparer.Ordinal)
        {
            ["schema_migrations"] = ["version", "name", "content_digest", "applied_at"],
            ["subtitle_edits"] =
            [
                "request_id", "project_id", "root_version_id", "parent_version_id", "child_version_id",
                "sequence", "generated_input_digest", "parent_snapshot_digest", "result_snapshot_digest",
                "child_content_digest", "root_payload_digest", "child_payload_digest", "actor_sid",
                "occurred_at", "commit_payload", "commit_digest", "previous_record_digest", "record_digest",
            ],
            ["source_only_executions"] =
            [
                "id", "project_id", "draft_id", "source_candidate_id", "root_job_id", "ingest_job_id",
                "parent_version_id", "parent_digest", "created_at", "request_payload", "request_digest",
                "protected_source_locator", "revision", "phase", "result_payload", "result_digest",
                "version_id", "version_digest", "failure_code", "state_digest", "retry_of_execution_id",
            ],
            ["default_settings_profiles"] =
                ["profile_id", "profile_version", "profile_digest", "profile_payload"],
            ["default_settings_global"] =
            [
                "singleton_id",
                "profile_id",
                "profile_version",
                "profile_digest",
                "snapshot_digest",
                "snapshot_payload",
                "revision",
                "updated_at",
            ],
            ["projects"] =
                ["id", "revision", "publication_scope", "deadline", "payload", "updated_at"],
            ["project_creation_drafts"] =
            [
                "id",
                "revision",
                "payload",
                "updated_at",
                "finalized_project_id",
                "finalized_at",
            ],
            ["project_default_settings"] =
            [
                "project_id",
                "profile_id",
                "profile_version",
                "profile_digest",
                "initial_snapshot_digest",
                "initial_snapshot_payload",
                "current_snapshot_digest",
                "current_snapshot_payload",
                "revision",
                "updated_at",
            ],
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
            ["ingest_workflows"] =
            [
                "job_id",
                "project_id",
                "request_digest",
                "request_schema_version",
                "request_payload",
                "checkpoint_stage",
                "checkpoint_attempt_number",
                "checkpoint_digest",
                "checkpoint_schema_version",
                "checkpoint_payload",
                "revision",
                "updated_at",
            ],
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
            ["role_assignments"] =
                ["actor_sid", "role", "assigned_at", "assigned_by_sid", "reason"],
            ["term_entries"] =
                ["id", "project_id", "series_id", "written_form", "reading", "version"],
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
            ["audit_chain_entries"] =
            [
                "sequence_number",
                "audit_event_id",
                "format_version",
                "previous_hash",
                "event_hash",
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
            ["automated_evaluations"] =
            [
                "evaluation_id",
                "job_id",
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "artifact_id",
                "artifact_digest",
                "settings_profile_id",
                "settings_profile_version",
                "settings_profile_digest",
                "settings_snapshot_digest",
                "rule_set_version",
                "environment_version",
                "environment_digest",
                "tool_versions_digest",
                "model_versions_digest",
                "template_digest",
                "music_digest",
                "started_at",
                "completed_at",
                "evaluated_at",
                "input_digest",
                "output_digest",
                "aggregate_status",
                "pass_count",
                "warning_count",
                "fail_count",
                "not_evaluated_count",
                "canonical_digest",
                "canonical_payload",
            ],
            ["automated_evaluation_rules"] =
            [
                "evaluation_id",
                "rule_id",
                "category",
                "scope",
                "status",
                "reason_code",
                "evidence_digest",
                "target_kind",
                "target_project_id",
                "target_composition_version_id",
                "target_composition_version_digest",
                "target_artifact_id",
                "target_artifact_digest",
                "time_basis",
                "time_value",
                "time_rate",
                "setting_key",
                "editor_kind",
            ],
            ["quick_start_workflows"] =
            [
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "current_state",
                "revision",
                "project_revision",
                "project_canonical_digest",
                "settings_snapshot_digest",
                "evaluation_id",
                "evaluation_aggregate_status",
                "evaluation_canonical_digest",
                "artifact_id",
                "artifact_digest",
                "self_review_count",
                "change_request_count",
                "event_chain_digest",
                "last_event_id",
                "last_event_digest",
                "canonical_digest",
                "canonical_payload",
            ],
            ["quick_start_state_events"] =
            [
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "revision",
                "event_id",
                "kind",
                "actor_kind",
                "actor_id",
                "occurred_at",
                "project_revision",
                "project_canonical_digest",
                "settings_snapshot_digest",
                "evaluation_id",
                "evaluation_evaluated_at",
                "evaluation_aggregate_status",
                "evaluation_canonical_digest",
                "artifact_id",
                "artifact_digest",
                "evaluation_result_state",
                "reason_code",
                "publication_scope",
                "rights_allowed_for_internal_use",
                "external_destination_requested",
                "destination_kind",
                "authorized_state",
                "authorized_revision",
                "canonical_digest",
                "canonical_payload",
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

        CollectionAssert.AreEqual(
            new[] { "profile_id", "profile_version" },
            GetPrimaryKeyColumns(connection, "default_settings_profiles"));
        CollectionAssert.AreEqual(
            new[] { "singleton_id" },
            GetPrimaryKeyColumns(connection, "default_settings_global"));
        CollectionAssert.AreEqual(
            new[] { "project_id" },
            GetPrimaryKeyColumns(connection, "project_default_settings"));
        CollectionAssert.AreEqual(
            new[] { "evaluation_id" },
            GetPrimaryKeyColumns(connection, "automated_evaluations"));
        CollectionAssert.AreEqual(
            new[] { "evaluation_id", "rule_id" },
            GetPrimaryKeyColumns(connection, "automated_evaluation_rules"));
        CollectionAssert.AreEqual(
            new[]
            {
                "project_id",
                "composition_version_id",
                "composition_version_digest",
            },
            GetPrimaryKeyColumns(connection, "quick_start_workflows"));
        CollectionAssert.AreEqual(
            new[]
            {
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "revision",
            },
            GetPrimaryKeyColumns(connection, "quick_start_state_events"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "default_settings_profiles:profile_id->profile_id:RESTRICT:RESTRICT",
                "default_settings_profiles:profile_version->profile_version:RESTRICT:RESTRICT",
                "default_settings_profiles:profile_digest->profile_digest:RESTRICT:RESTRICT",
            },
            GetForeignKeyBindings(connection, "default_settings_global"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "default_settings_profiles:profile_id->profile_id:RESTRICT:RESTRICT",
                "default_settings_profiles:profile_version->profile_version:RESTRICT:RESTRICT",
                "default_settings_profiles:profile_digest->profile_digest:RESTRICT:RESTRICT",
                "projects:project_id->id:RESTRICT:RESTRICT",
            },
            GetForeignKeyBindings(connection, "project_default_settings"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projects:project_id->id:RESTRICT:RESTRICT",
                "jobs:job_id->id:RESTRICT:RESTRICT",
                "jobs:project_id->project_id:RESTRICT:RESTRICT",
                "composition_versions:composition_version_id->id:RESTRICT:RESTRICT",
                "composition_versions:project_id->project_id:RESTRICT:RESTRICT",
                "composition_versions:composition_version_digest->content_digest:RESTRICT:RESTRICT",
                "artifacts:artifact_id->id:RESTRICT:RESTRICT",
                "artifacts:project_id->project_id:RESTRICT:RESTRICT",
                "artifacts:artifact_digest->content_digest:RESTRICT:RESTRICT",
                "default_settings_profiles:settings_profile_id->profile_id:RESTRICT:RESTRICT",
                "default_settings_profiles:settings_profile_version->profile_version:RESTRICT:RESTRICT",
                "default_settings_profiles:settings_profile_digest->profile_digest:RESTRICT:RESTRICT",
            },
            GetForeignKeyBindings(connection, "automated_evaluations"));
        Assert.AreEqual(
            8,
            GetForeignKeyBindings(connection, "automated_evaluation_rules").Length);
        Assert.AreEqual(
            16,
            GetForeignKeyBindings(connection, "quick_start_workflows").Length);
        Assert.AreEqual(
            19,
            GetForeignKeyBindings(connection, "quick_start_state_events").Length);

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
            "idx_artifacts_evaluation_binding",
            "idx_audit_events_project_occurred_at",
            "idx_automated_evaluations_quick_start_binding",
            "idx_automated_evaluations_version_newest",
            "idx_composition_versions_content_digest",
            "idx_composition_versions_project_id",
            "idx_ingest_workflows_project_id",
            "idx_job_attempts_job_id_sequence_number",
            "idx_job_heartbeats_attempt_recorded_at",
            "idx_job_progress_attempt_recorded_at",
            "idx_jobs_id_project_id",
            "idx_jobs_project_id",
            "idx_jobs_state_created_at",
            "idx_project_creation_drafts_updated_at",
            "idx_project_default_settings_profile",
            "idx_quick_start_state_events_version",
            "idx_quick_start_workflows_version",
            "idx_review_comments_project_id",
            "idx_review_comments_version",
            "idx_review_records_project_id",
            "idx_review_records_version",
            "idx_source_only_executions_ingest",
            "idx_source_only_executions_initial_draft",
            "idx_source_only_executions_latest",
            "idx_source_only_executions_result_version",
            "idx_source_only_executions_retry",
            "idx_term_entries_project_written_form_reading",
            "idx_term_entries_series_written_form_reading",
        ];
        CollectionAssert.AreEqual(expectedIndexes, GetExplicitIndexNames(connection));
        CollectionAssert.AreEqual(new[] { "request_id" }, GetPrimaryKeyColumns(connection, "subtitle_edits"));
        Assert.AreEqual(3L, ExecuteScalarInt64(connection,
            "SELECT COUNT(*) FROM pragma_index_list('subtitle_edits');"));
        Assert.AreEqual(3L, ExecuteScalarInt64(connection,
            "SELECT COUNT(*) FROM pragma_index_list('subtitle_edits') WHERE [unique] = 1 AND partial = 0 AND origin IN ('pk', 'u');"));
        CollectionAssert.AreEqual(new[] { "request_id" },
            GetIndexColumns(connection, "sqlite_autoindex_subtitle_edits_1"));
        CollectionAssert.AreEqual(new[] { "child_version_id" },
            GetIndexColumns(connection, "sqlite_autoindex_subtitle_edits_2"));
        CollectionAssert.AreEqual(new[] { "project_id", "root_version_id", "sequence" },
            GetIndexColumns(connection, "sqlite_autoindex_subtitle_edits_3"));
        CollectionAssert.AreEqual(new[] { "version_id" },
            GetIndexColumns(connection, "idx_source_only_executions_result_version"));
        Assert.AreEqual("1:1", ExecuteScalarString(connection, """
            SELECT CAST([unique] AS TEXT) || ':' || CAST(partial AS TEXT)
            FROM pragma_index_list('source_only_executions')
            WHERE name = 'idx_source_only_executions_result_version';
            """));
        CollectionAssert.AreEqual(
            new[] { "project_id", "occurred_at" },
            GetIndexColumns(connection, "idx_audit_events_project_occurred_at"));
        CollectionAssert.AreEqual(
            new[] { "composition_version_id", "project_id", "composition_version_digest" },
            GetIndexColumns(connection, "idx_review_comments_version"));
        CollectionAssert.AreEqual(
            new[] { "profile_id", "profile_version", "profile_digest" },
            GetIndexColumns(connection, "idx_project_default_settings_profile"));
        CollectionAssert.AreEqual(
            new[] { "id", "project_id", "content_digest" },
            GetIndexColumns(connection, "idx_artifacts_evaluation_binding"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                SELECT [unique]
                FROM pragma_index_list('artifacts')
                WHERE name = 'idx_artifacts_evaluation_binding';
                """));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                SELECT [unique]
                FROM pragma_index_list('automated_evaluations')
                WHERE name = 'idx_automated_evaluations_quick_start_binding';
                """));
        CollectionAssert.AreEqual(
            new[]
            {
                "evaluation_id",
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "settings_snapshot_digest",
                "aggregate_status",
                "canonical_digest",
                "artifact_id",
                "artifact_digest",
            },
            GetIndexColumns(
                connection,
                "idx_automated_evaluations_quick_start_binding"));
        CollectionAssert.AreEqual(
            new[]
            {
                "trg_artifact_automated_evaluation_kind_update",
                "trg_automated_evaluation_artifact_insert",
                "trg_automated_evaluation_artifact_update",
                "trg_automated_evaluation_binding_update",
                "trg_automated_evaluation_rule_artifact_insert",
                "trg_automated_evaluation_rule_artifact_update",
                "trg_automated_evaluation_rule_target_insert",
                "trg_automated_evaluation_rule_target_update",
                "trg_project_creation_draft_deletions_delete",
                "trg_project_creation_draft_deletions_update",
                "trg_project_display_names_delete",
                "trg_quick_start_evaluation_immutable_update",
                "trg_quick_start_evaluation_rule_immutable_delete",
                "trg_quick_start_evaluation_rule_immutable_insert",
                "trg_quick_start_evaluation_rule_immutable_update",
                "trg_quick_start_event_evaluation_insert",
                "trg_quick_start_event_immutable_delete",
                "trg_quick_start_event_immutable_update",
                "trg_quick_start_render_artifact_immutable_delete",
                "trg_quick_start_render_artifact_immutable_update",
                "trg_quick_start_workflow_completed_binding_immutable",
                "trg_quick_start_workflow_evaluation_insert",
                "trg_quick_start_workflow_evaluation_update",
                "trg_quick_start_workflow_insert_shape",
                "trg_quick_start_workflow_update_event_match",
                "trg_quick_start_workflow_update_transition",
                "trg_source_only_execution_delete",
                "trg_source_only_execution_insert",
                "trg_source_only_execution_update",
                "trg_subtitle_edits_delete",
                "trg_subtitle_edits_insert",
                "trg_subtitle_edits_update",
                "trg_subtitle_edits_version_delete",
                "trg_subtitle_edits_version_insert",
                "trg_subtitle_edits_version_update",
            },
            GetTriggerNames(connection));
        CollectionAssert.AreEqual(
            new[]
            {
                "project_id",
                "composition_version_id",
                "composition_version_digest",
                "evaluated_at",
                "evaluation_id",
            },
            GetIndexColumns(connection, "idx_automated_evaluations_version_newest"));

        foreach (string initiallyEmptyTable in new[]
                 {
                     "automated_evaluation_rules",
                     "automated_evaluations",
                     "default_settings_profiles",
                     "default_settings_global",
                     "project_default_settings",
                     "project_display_names",
                     "quick_start_state_events",
                     "quick_start_workflows",
                     "source_only_executions",
                     "subtitle_edits",
                 })
        {
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    connection,
                    $"SELECT COUNT(*) FROM {initiallyEmptyTable};"),
                initiallyEmptyTable);
        }

        AssertNoCascadeForeignKeys(connection, expectedTables);

        using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT version, name, content_digest, applied_at
                FROM schema_migrations
                ORDER BY version;
                """;
            using SqliteDataReader reader = metadata.ExecuteReader();
            (long Version, string Name, ContentDigest Digest)[] expectedMigrations =
            [
                (1, "001_initial", ReadEmbeddedMigrationDigest("001_initial")),
                (2, "002_jobs", ReadEmbeddedMigrationDigest("002_jobs")),
                (3, "003_audit_chain", ReadEmbeddedMigrationDigest("003_audit_chain")),
                (4, "004_role_assignments", ReadEmbeddedMigrationDigest("004_role_assignments")),
                (5, "005_term_entries", ReadEmbeddedMigrationDigest("005_term_entries")),
                (6, "006_ingest_workflows", ReadEmbeddedMigrationDigest("006_ingest_workflows")),
                (7, "007_project_creation_drafts", ReadEmbeddedMigrationDigest("007_project_creation_drafts")),
                (8, "008_default_settings", ReadEmbeddedMigrationDigest("008_default_settings")),
                (9, "009_automated_evaluation", ReadEmbeddedMigrationDigest("009_automated_evaluation")),
                (10, "010_quick_start_workflows", ReadEmbeddedMigrationDigest("010_quick_start_workflows")),
                (11, "011_source_only_executions", ReadEmbeddedMigrationDigest("011_source_only_executions")),
                (12, "012_subtitle_edits", ReadEmbeddedMigrationDigest("012_subtitle_edits")),
                (13, "013_project_deletions", ReadEmbeddedMigrationDigest("013_project_deletions")),
                (14, "014_project_display_names", ReadEmbeddedMigrationDigest("014_project_display_names")),
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
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("003_audit_chain"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("004_role_assignments"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("005_term_entries"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("006_ingest_workflows"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("007_project_creation_drafts"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("008_default_settings"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("009_automated_evaluation"), result.SchemaDigest);
        Assert.AreNotEqual(ReadEmbeddedMigrationDigest("010_quick_start_workflows"), result.SchemaDigest);
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migration011PreservesExactLegacyBusinessRowsWithoutInventingExecutions()
    {
        using StoreTestDatabase database = new();
        var seed = new SourceOnlyExecutionTestSeed(database.DatabasePath);
        _ = seed.Finalize();
        seed.CompleteIngest();
        _ = seed.AppendVersion();
        // Reconstruct exact v10 while retaining valid legacy draft/settings/job rows.
        RemoveMigration012(database);
        database.ExecuteNonQuery("DROP TABLE source_only_executions; DELETE FROM schema_migrations WHERE version = 11;");
        string[] tables = ["projects", "project_creation_drafts", "project_default_settings", "default_settings_profiles", "jobs", "job_attempts", "composition_versions"];
        Dictionary<string, string> before;
        using (SqliteConnection connection = database.OpenConnection())
        {
            before = tables.ToDictionary(table => table, table => SnapshotRows(connection, table));
        }
        DatabaseMigrationResult upgraded = DatabaseMigrator.Migrate(database.DatabasePath, database.BackupDirectory);
        Assert.AreEqual(14, upgraded.SchemaVersion);
        Assert.IsTrue(upgraded.Applied);
        using (SqliteConnection connection = database.OpenConnection())
        {
            foreach (string table in tables) { Assert.AreEqual(before[table], SnapshotRows(connection, table), table); }
            AssertDatabaseChecksPass(connection);
        }
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM source_only_executions;"));
        using (SqliteConnection backup = OpenConnection(upgraded.BackupPath!, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(10L, ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(0L, ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'source_only_executions';"));
            foreach (string table in tables) { Assert.AreEqual(before[table], SnapshotRows(backup, table), table); }
        }
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(database.DatabasePath, database.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.AreEqual(upgraded.SchemaDigest, noOp.SchemaDigest);
        SourceOnlyExecutionStore.ValidateAll(database.DatabasePath);
    }

    [TestMethod]
    public void Migration011FailureRollsBackEveryNewObjectAndMetadataThenRetries()
    {
        using StoreTestDatabase database = new();
        RemoveMigration012(database);
        database.ExecuteNonQuery("DROP TABLE source_only_executions; DELETE FROM schema_migrations WHERE version = 11;");
        Assert.ThrowsExactly<InvalidOperationException>(() => DatabaseMigrator.Migrate(
            database.DatabasePath, database.BackupDirectory, new ThrowOnSecondUtcNowTimeProvider(FixedUtcNow)));
        Assert.AreEqual(10L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%source_only%';"));
        Assert.IsTrue(DatabaseMigrator.Migrate(database.DatabasePath, database.BackupDirectory).Applied);
        DatabaseMigrator.ValidateCurrentDatabase(database.DatabasePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Migration012PreservesExactVersionElevenRowsAndBackupWithRetryAndIdempotence(bool failFirst)
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionElevenDatabase(temporary);
        Dictionary<string, string> before;
        using (SqliteConnection legacy = OpenConnection(temporary.DatabasePath))
        {
            InsertCompositionVersion(legacy, VersionId, ProjectId, null, DigestA);
            InsertDefaultSettingsProfile(legacy, DefaultSettingsProfileId, "1.0.0", DigestA, [1]);
            InsertProjectDefaultSettings(legacy, ProjectId, DefaultSettingsProfileId, "1.0.0", DigestA);
            InsertApproval(legacy, ApprovalId, ProjectId, VersionId, DigestA);
            Assert.AreEqual(26, GetUserTableNames(legacy).Length);
            Assert.AreEqual(11L, ExecuteScalarInt64(legacy, "SELECT COUNT(*) FROM schema_migrations;"));
            before = GetUserTableNames(legacy).ToDictionary(table => table, table => SnapshotRows(legacy, table));
        }

        if (failFirst)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => DatabaseMigrator.Migrate(
                temporary.DatabasePath, temporary.BackupDirectory, new ThrowOnSecondUtcNowTimeProvider(FixedUtcNow)));
            using SqliteConnection failed = OpenConnection(temporary.DatabasePath);
            Assert.AreEqual(26, GetUserTableNames(failed).Length);
            Assert.AreEqual(0L, ExecuteScalarInt64(failed,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%subtitle_edits%';"));
            foreach ((string table, string rows) in before)
            {
                Assert.AreEqual(rows, SnapshotRows(failed, table), table);
            }
        }

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(temporary.DatabasePath, temporary.BackupDirectory);
        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        using (SqliteConnection backup = OpenConnection(result.BackupPath!, SqliteOpenMode.ReadOnly))
        {
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), GetUserTableNames(backup));
            Assert.AreEqual(11L, ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            foreach ((string table, string rows) in before)
            {
                Assert.AreEqual(rows, SnapshotRows(backup, table), table);
            }
            AssertDatabaseChecksPass(backup);
        }
        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            CollectionAssert.AreEquivalent(before.Keys.Append("subtitle_edits").Append("project_creation_draft_deletions").Append("project_display_names").ToArray(), GetUserTableNames(upgraded));
            Assert.AreEqual(29, GetUserTableNames(upgraded).Length);
            Assert.AreEqual(14L, ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(0L, ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM subtitle_edits;"));
            foreach ((string table, string rows) in before.Where(pair => pair.Key != "schema_migrations"))
            {
                Assert.AreEqual(rows, SnapshotRows(upgraded, table), table);
            }
            using SqliteConnection backup = OpenConnection(result.BackupPath!, SqliteOpenMode.ReadOnly);
            Assert.AreEqual(
                SnapshotRows(backup, "schema_migrations"),
                SnapshotRows(upgraded, "schema_migrations WHERE version <= 11"));
            AssertDatabaseChecksPass(upgraded);
        }
        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath, temporary.BackupDirectory, new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    private static void RemoveMigration012(StoreTestDatabase database)
    {
        database.ExecuteNonQuery("""
            DROP TRIGGER trg_project_display_names_delete;
            DROP TABLE project_display_names;
            DELETE FROM schema_migrations WHERE version = 14;
            DROP TRIGGER trg_project_creation_draft_deletions_update;
            DROP TRIGGER trg_project_creation_draft_deletions_delete;
            DROP TABLE project_creation_draft_deletions;
            DELETE FROM schema_migrations WHERE version = 13;
            DROP TRIGGER trg_subtitle_edits_version_insert;
            DROP TRIGGER trg_subtitle_edits_version_update;
            DROP TRIGGER trg_subtitle_edits_version_delete;
            DROP TABLE subtitle_edits;
            DELETE FROM schema_migrations WHERE version = 12;
            """);
    }

    private static string SnapshotRows(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new List<string>();
            for (int index = 0; index < reader.FieldCount; index++)
            {
                object value = reader.GetValue(index);
                fields.Add(value is byte[] bytes ? "blob:" + Convert.ToHexString(bytes) :
                    value is DBNull ? "null" : value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            rows.Add(System.Text.Json.JsonSerializer.Serialize(fields));
        }
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    [TestMethod]
    public void Migration010_RequiresEventMatchedTechnicalReevaluationAndPreservesEvaluationHistory()
    {
        const string StartedEventId =
            "10000000-0000-4000-8000-000000000001";
        const string EvaluationStartedEventId =
            "10000000-0000-4000-8000-000000000002";
        const string FirstCompletedEventId =
            "10000000-0000-4000-8000-000000000003";
        const string ReevaluationStartedEventId =
            "10000000-0000-4000-8000-000000000004";
        const string SecondCompletedEventId =
            "10000000-0000-4000-8000-000000000005";
        const string SecondEvaluationId =
            "20000000-0000-4000-8000-000000000001";
        const string FirstEvaluatedAt =
            "2030-01-02T03:04:06.6789012+00:00";
        const string FirstCompletedAt =
            "2030-01-02T03:04:07.6789012+00:00";
        const string ReevaluationStartedAt =
            "2030-01-02T03:04:08.6789012+00:00";
        const string SecondEvaluatedAt =
            "2030-01-02T03:04:09.6789012+00:00";
        const string SecondCompletedAt =
            "2030-01-02T03:04:10.6789012+00:00";

        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        InsertProject(connection, ProjectId);
        InsertCompositionVersion(
            connection,
            VersionId,
            ProjectId,
            parentVersionId: null,
            DigestA);
        InsertDefaultSettingsProfile(
            connection,
            DefaultSettingsProfileId,
            "1.0.0",
            DigestA,
            [1]);

        InsertQuickStartWorkflow(connection, StartedEventId);
        InsertQuickStartWorkflowEvent(
            connection,
            revision: 1,
            StartedEventId,
            "StartedEditing",
            CanonicalEventTimestamp);
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "UPDATE quick_start_workflows SET revision = revision + 1;"));

        InsertQuickStartWorkflowEvent(
            connection,
            revision: 2,
            EvaluationStartedEventId,
            "EvaluationStarted",
            CanonicalEventTimestamp,
            settingsSnapshotDigest: DigestB);
        UpdateQuickStartWorkflow(
            connection,
            "automatedEvaluation",
            revision: 2,
            EvaluationStartedEventId,
            DigestB);

        InsertAutomatedEvaluation(
            connection,
            AuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            evaluatedAt: FirstEvaluatedAt);
        InsertQuickStartWorkflowEvent(
            connection,
            revision: 3,
            FirstCompletedEventId,
            "EvaluationCompleted",
            FirstCompletedAt,
            settingsSnapshotDigest: DigestB,
            evaluationId: AuditEventId,
            evaluationEvaluatedAt: FirstEvaluatedAt,
            evaluationAggregateStatus: "NOT EVALUATED",
            evaluationCanonicalDigest: DigestB,
            evaluationResultState: "failed");
        UpdateQuickStartWorkflow(
            connection,
            "failed",
            revision: 3,
            FirstCompletedEventId,
            DigestB,
            AuditEventId,
            "NOT EVALUATED",
            DigestB);

        InsertQuickStartWorkflowEvent(
            connection,
            revision: 4,
            ReevaluationStartedEventId,
            "TechnicalReevaluationStarted",
            ReevaluationStartedAt,
            settingsSnapshotDigest: DigestB);
        UpdateQuickStartWorkflow(
            connection,
            "automatedEvaluation",
            revision: 4,
            ReevaluationStartedEventId,
            DigestB);
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                connection,
                "SELECT COUNT(*) FROM quick_start_workflows WHERE evaluation_id IS NOT NULL;"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                "SELECT COUNT(*) FROM automated_evaluations WHERE evaluation_id = $id;",
                ("$id", AuditEventId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "DELETE FROM automated_evaluations WHERE evaluation_id = $id;",
            ("$id", AuditEventId)));

        InsertAutomatedEvaluation(
            connection,
            SecondEvaluationId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            evaluatedAt: FirstCompletedAt);
        AssertConstraintViolation(() => InsertQuickStartWorkflowEvent(
            connection,
            revision: 5,
            SecondCompletedEventId,
            "EvaluationCompleted",
            ReevaluationStartedAt,
            settingsSnapshotDigest: DigestB,
            evaluationId: SecondEvaluationId,
            evaluationEvaluatedAt: FirstCompletedAt,
            evaluationAggregateStatus: "NOT EVALUATED",
            evaluationCanonicalDigest: DigestB,
            evaluationResultState: "failed"));
        ExecuteNonQuery(
            connection,
            "DELETE FROM automated_evaluations WHERE evaluation_id = $id;",
            ("$id", SecondEvaluationId));

        InsertAutomatedEvaluation(
            connection,
            SecondEvaluationId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            evaluatedAt: SecondEvaluatedAt,
            startedAt: ReevaluationStartedAt);
        AssertConstraintViolation(() => InsertQuickStartWorkflowEvent(
            connection,
            revision: 5,
            SecondCompletedEventId,
            "EvaluationCompleted",
            SecondCompletedAt,
            settingsSnapshotDigest: DigestB,
            evaluationId: SecondEvaluationId,
            evaluationEvaluatedAt: ReevaluationStartedAt,
            evaluationAggregateStatus: "NOT EVALUATED",
            evaluationCanonicalDigest: DigestB,
            evaluationResultState: "failed"));
        AssertConstraintViolation(() => InsertQuickStartWorkflowEvent(
            connection,
            revision: 5,
            SecondCompletedEventId,
            "EvaluationCompleted",
            ReevaluationStartedAt,
            settingsSnapshotDigest: DigestB,
            evaluationId: SecondEvaluationId,
            evaluationEvaluatedAt: SecondEvaluatedAt,
            evaluationAggregateStatus: "NOT EVALUATED",
            evaluationCanonicalDigest: DigestB,
            evaluationResultState: "failed"));
        InsertQuickStartWorkflowEvent(
            connection,
            revision: 5,
            SecondCompletedEventId,
            "EvaluationCompleted",
            SecondCompletedAt,
            settingsSnapshotDigest: DigestB,
            evaluationId: SecondEvaluationId,
            evaluationEvaluatedAt: SecondEvaluatedAt,
            evaluationAggregateStatus: "NOT EVALUATED",
            evaluationCanonicalDigest: DigestB,
            evaluationResultState: "failed");
        UpdateQuickStartWorkflow(
            connection,
            "failed",
            revision: 5,
            SecondCompletedEventId,
            DigestB,
            SecondEvaluationId,
            "NOT EVALUATED",
            DigestB);

        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            """
            UPDATE automated_evaluations
            SET canonical_payload = X'02'
            WHERE evaluation_id = $id;
            """,
            ("$id", AuditEventId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "UPDATE quick_start_workflows SET revision = revision + 1;"));
        Assert.AreEqual(
            2L,
            ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                SELECT COUNT(*)
                FROM automated_evaluations
                WHERE evaluation_id = $id AND canonical_payload = X'01';
                """,
                ("$id", AuditEventId)));
        Assert.AreEqual(
            5L,
            ExecuteScalarInt64(connection, "SELECT revision FROM quick_start_workflows;"));
        Assert.AreEqual(
            5L,
            ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM quick_start_state_events;"));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migrate_ExactVersionOneDatabaseCreatesOneBackupAndAppliesRemainingVersions()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionOneDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(1)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
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
            new[]
            {
                "default_settings_global",
                "default_settings_profiles",
                "automated_evaluation_rules",
                "automated_evaluations",
                "ingest_workflows",
                "job_attempts",
                "job_heartbeats",
                "job_progress",
                "jobs",
                "project_creation_draft_deletions",
                "project_creation_drafts",
                "project_default_settings",
                "project_display_names",
                "quick_start_state_events",
                "quick_start_workflows",
                "source_only_executions",
                "subtitle_edits",
            },
            GetUserTableNames(upgraded)
                .Except(versionOneTables)
                .Except(["audit_chain_entries", "role_assignments", "term_entries"])
                .ToArray());
        Assert.IsTrue(GetUserTableNames(upgraded).Contains("audit_chain_entries"));
        Assert.IsTrue(GetUserTableNames(upgraded).Contains("role_assignments"));
        Assert.IsTrue(GetUserTableNames(upgraded).Contains("term_entries"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "idx_job_attempts_job_id_sequence_number",
                "idx_job_heartbeats_attempt_recorded_at",
                "idx_job_progress_attempt_recorded_at",
                "idx_artifacts_evaluation_binding",
                "idx_automated_evaluations_quick_start_binding",
                "idx_automated_evaluations_version_newest",
                "idx_ingest_workflows_project_id",
                "idx_jobs_id_project_id",
                "idx_jobs_project_id",
                "idx_jobs_state_created_at",
                "idx_project_creation_drafts_updated_at",
                "idx_project_default_settings_profile",
                "idx_quick_start_state_events_version",
                "idx_quick_start_workflows_version",
                "idx_source_only_executions_ingest",
                "idx_source_only_executions_initial_draft",
                "idx_source_only_executions_latest",
                "idx_source_only_executions_result_version",
                "idx_source_only_executions_retry",
                "idx_term_entries_project_written_form_reading",
                "idx_term_entries_series_written_form_reading",
            },
            GetExplicitIndexNames(upgraded)
                .Except(GetVersionOneExplicitIndexNames())
                .ToArray());
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM projects;"));
        Assert.AreEqual(
            14L,
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
        Assert.AreEqual(
            "003_audit_chain",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 3;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("003_audit_chain").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 3;"));
        Assert.AreEqual(
            "004_role_assignments",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 4;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("004_role_assignments").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 4;"));
        Assert.AreEqual(
            "005_term_entries",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 5;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("005_term_entries").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 5;"));
        Assert.AreEqual(
            "006_ingest_workflows",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 6;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("006_ingest_workflows").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 6;"));
        Assert.AreEqual(
            "007_project_creation_drafts",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 7;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("007_project_creation_drafts").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 7;"));
        Assert.AreEqual(
            "008_default_settings",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 8;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("008_default_settings").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 8;"));
        Assert.AreEqual(
            "009_automated_evaluation",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 9;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("009_automated_evaluation").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 9;"));
        Assert.AreEqual(
            "010_quick_start_workflows",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.AreEqual(
            ReadEmbeddedMigrationDigest("010_quick_start_workflows").Hex,
            ExecuteScalarString(
                upgraded,
                "SELECT content_digest FROM schema_migrations WHERE version = 10;"));
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
                                """
                                SELECT COUNT(*)
                                FROM sqlite_schema
                                WHERE type IN ('view', 'trigger')
                                    AND tbl_name IN ('jobs', 'job_attempts', 'job_progress', 'job_heartbeats');
                                """));
    }

    [TestMethod]
    public void Migration003_DefinesMinimalStrictAuditChainSidecar()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        CollectionAssert.AreEqual(
            new[]
            {
                "sequence_number",
                "audit_event_id",
                "format_version",
                "previous_hash",
                "event_hash",
            },
            GetTableColumns(connection, "audit_chain_entries"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT strict
                    FROM pragma_table_list
                    WHERE schema = 'main'
                      AND type = 'table'
                      AND name = 'audit_chain_entries';
                    """));
        CollectionAssert.AreEqual(
            new[] { "audit_events:audit_event_id->id:RESTRICT:RESTRICT" },
            GetForeignKeyBindings(connection, "audit_chain_entries"));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'index'
                      AND tbl_name = 'audit_chain_entries'
                      AND name NOT LIKE 'sqlite_%';
                    """));

        InsertProject(connection, ProjectId);
        InsertAuditEvent(
            connection,
            AuditEventId,
            ProjectId,
            compositionVersionId: null,
            compositionVersionDigest: null);
        AssertConstraintViolation(
            () => InsertAuditChainEntry(
                connection,
                sequenceNumber: 0,
                AuditEventId,
                formatVersion: 1,
                previousHash: new string('0', 64),
                eventHash: DigestA));
        AssertConstraintViolation(
            () => InsertAuditChainEntry(
                connection,
                sequenceNumber: 1,
                AuditEventId,
                formatVersion: 2,
                previousHash: new string('0', 64),
                eventHash: DigestA));
        AssertConstraintViolation(
            () => InsertAuditChainEntry(
                connection,
                sequenceNumber: 1,
                AuditEventId,
                formatVersion: 1,
                previousHash: new string('A', 64),
                eventHash: DigestA));
        InsertAuditChainEntry(
            connection,
            sequenceNumber: 1,
            AuditEventId,
            formatVersion: 1,
            previousHash: new string('0', 64),
            eventHash: DigestA);
        AssertConstraintViolation(
            () => InsertAuditChainEntry(
                connection,
                sequenceNumber: 2,
                AuditEventId,
                formatVersion: 1,
                previousHash: DigestA,
                eventHash: DigestB));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM audit_events WHERE id = $id;",
                ("$id", AuditEventId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migration004_DefinesOnlyCurrentStrictRoleAssignments()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        CollectionAssert.AreEqual(
            new[] { "actor_sid", "role", "assigned_at", "assigned_by_sid", "reason" },
            GetTableColumns(connection, "role_assignments"));
        CollectionAssert.AreEqual(
            new[] { "actor_sid", "role" },
            GetPrimaryKeyColumns(connection, "role_assignments"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT strict
                    FROM pragma_table_list
                    WHERE schema = 'main'
                      AND type = 'table'
                      AND name = 'role_assignments';
                    """));
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            GetForeignKeyBindings(connection, "role_assignments"));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'index'
                      AND tbl_name = 'role_assignments'
                      AND name NOT LIKE 'sqlite_%';
                    """));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table'
                      AND name IN ('permission_matrix', 'role_permissions', 'operation_permissions');
                    """));

        string[] exactRoles =
        [
            "Viewer",
            "Editor",
            "PerformerReviewer",
            "ContentApprover",
            "BrandApprover",
            "RightsApprover",
            "Administrator",
        ];
        for (int index = 0; index < exactRoles.Length; index++)
        {
            InsertRoleAssignment(
                connection,
                $"S-1-5-21-1111111111-2222222222-3333333333-{1001 + index}",
                exactRoles[index]);
        }

        InsertRoleAssignment(connection, "S-1-5-21-1-2-3-2001", "Viewer");
        InsertRoleAssignment(connection, "S-1-5-21-1-2-3-2001", "Editor");
        AssertConstraintViolation(
            () => InsertRoleAssignment(connection, "S-1-5-21-1-2-3-2001", "Viewer"));

        foreach (string invalidRole in new[]
        {
            string.Empty,
            "viewer",
            "Administrator ",
            "ReviewActorRole.Editor",
            "SuperAdministrator",
        })
        {
            AssertConstraintViolation(
                () => InsertRoleAssignment(
                    connection,
                    "S-1-5-21-1-2-3-3001",
                    invalidRole));
        }

        AssertConstraintViolation(
            () => InsertRoleAssignment(connection, " actor", "Viewer"));
        AssertConstraintViolation(
            () => InsertRoleAssignment(
                connection,
                "S-1-5-21-1-2-3-3002",
                "Viewer",
                assignedAt: " timestamp"));
        AssertConstraintViolation(
            () => InsertRoleAssignment(
                connection,
                "S-1-5-21-1-2-3-3003",
                "Viewer",
                assignedBySid: "assigner "));
        AssertConstraintViolation(
            () => InsertRoleAssignment(
                connection,
                "S-1-5-21-1-2-3-3004",
                "Viewer",
                reason: " "));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migration005_DefinesMinimalStrictVersionedTermEntries()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        CollectionAssert.AreEqual(
            new[] { "id", "project_id", "series_id", "written_form", "reading", "version" },
            GetTableColumns(connection, "term_entries"));
        CollectionAssert.AreEqual(
            new[] { "id" },
            GetPrimaryKeyColumns(connection, "term_entries"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT strict
                    FROM pragma_table_list
                    WHERE schema = 'main'
                      AND type = 'table'
                      AND name = 'term_entries';
                    """));
        CollectionAssert.AreEqual(
            new[] { "projects:project_id->id:RESTRICT:RESTRICT" },
            GetForeignKeyBindings(connection, "term_entries"));
        CollectionAssert.AreEqual(
            new[] { "project_id", "written_form", "reading" },
            GetIndexColumns(connection, "idx_term_entries_project_written_form_reading"));
        CollectionAssert.AreEqual(
            new[] { "series_id", "written_form", "reading" },
            GetIndexColumns(connection, "idx_term_entries_series_written_form_reading"));
        foreach (string index in new[]
        {
            "idx_term_entries_project_written_form_reading",
            "idx_term_entries_series_written_form_reading",
        })
        {
            Assert.AreEqual(
                "1:1",
                ExecuteScalarString(
                    connection,
                    """
                        SELECT CAST([unique] AS TEXT) || ':' || CAST(partial AS TEXT)
                        FROM pragma_index_list('term_entries')
                        WHERE name = $name;
                        """,
                    ("$name", index)),
                index);
        }

        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'series';"));
        InsertProject(connection, ProjectId);

        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                projectId: null,
                seriesId: null));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                ProjectId,
                SeriesId));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                OtherProjectId,
                seriesId: null));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                projectId: null,
                SeriesId.ToUpperInvariant()));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                ProjectId,
                seriesId: null,
                version: -1));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                ProjectId,
                seriesId: null,
                writtenForm: " term"));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                ProjectId,
                seriesId: null,
                reading: "reading "));
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                ProjectId,
                seriesId: null,
                writtenForm: "term\0hidden"));

        InsertTermEntry(connection, TermEntryId, ProjectId, seriesId: null);
        AssertConstraintViolation(
            () => InsertTermEntry(connection, OtherTermEntryId, ProjectId, seriesId: null));
        InsertTermEntry(
            connection,
            OtherTermEntryId,
            ProjectId,
            seriesId: null,
            reading: "alternate");
        InsertTermEntry(
            connection,
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            projectId: null,
            SeriesId);
        AssertConstraintViolation(
            () => InsertTermEntry(
                connection,
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                projectId: null,
                SeriesId));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM projects WHERE id = $id;",
                ("$id", ProjectId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migration006_DefinesStrictBoundedIngestWorkflowCheckpoints()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        CollectionAssert.AreEqual(
            new[]
            {
                "job_id",
                "project_id",
                "request_digest",
                "request_schema_version",
                "request_payload",
                "checkpoint_stage",
                "checkpoint_attempt_number",
                "checkpoint_digest",
                "checkpoint_schema_version",
                "checkpoint_payload",
                "revision",
                "updated_at",
            },
            GetTableColumns(connection, "ingest_workflows"));
        CollectionAssert.AreEqual(
            new[] { "job_id" },
            GetPrimaryKeyColumns(connection, "ingest_workflows"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT strict
                    FROM pragma_table_list
                    WHERE schema = 'main'
                      AND type = 'table'
                      AND name = 'ingest_workflows';
                    """));
        CollectionAssert.AreEqual(
            new[]
            {
                "jobs:job_id->id:RESTRICT:RESTRICT",
                "jobs:project_id->project_id:RESTRICT:RESTRICT",
            },
            GetForeignKeyBindings(connection, "ingest_workflows"));
        CollectionAssert.AreEqual(
            new[] { "project_id" },
            GetIndexColumns(connection, "idx_ingest_workflows_project_id"));
        CollectionAssert.AreEqual(
            new[] { "id", "project_id" },
            GetIndexColumns(connection, "idx_jobs_id_project_id"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT [unique]
                    FROM pragma_index_list('jobs')
                    WHERE name = 'idx_jobs_id_project_id';
                    """));

        const string jobId = "10101010-1010-4010-8010-101010101010";
        const string otherJobId = "20202020-2020-4020-8020-202020202020";
        InsertProject(connection, ProjectId);
        InsertProject(connection, OtherProjectId);
        InsertJob(connection, jobId, ProjectId, "media.ingest.v1", "queued", 0);
        InsertJob(connection, otherJobId, OtherProjectId, "media.ingest.v1", "queued", 0);

        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                OtherProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 0));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: -1,
                checkpointAttemptNumber: 0));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 10,
                checkpointAttemptNumber: 1));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 1));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 1,
                checkpointAttemptNumber: 0));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 0,
                requestDigest: DigestA.ToUpperInvariant()));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 0,
                requestSchemaVersion: " request-v1"));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 0,
                requestPayload: Array.Empty<byte>()));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 0,
                checkpointPayload: "not-a-blob"));

        InsertIngestWorkflow(
            connection,
            jobId,
            ProjectId,
            checkpointStage: 0,
            checkpointAttemptNumber: 0);
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                """
                    UPDATE ingest_workflows
                    SET request_payload = zeroblob($size)
                    WHERE job_id = $job_id;
                    """,
                ("$size", IngestWorkflowStore.MaximumPayloadUtf8Bytes + 1L),
                ("$job_id", jobId)));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                """
                    UPDATE ingest_workflows
                    SET checkpoint_payload = zeroblob($size)
                    WHERE job_id = $job_id;
                    """,
                ("$size", IngestWorkflowStore.MaximumPayloadUtf8Bytes + 1L),
                ("$job_id", jobId)));
        AssertConstraintViolation(
            () => InsertIngestWorkflow(
                connection,
                jobId,
                ProjectId,
                checkpointStage: 0,
                checkpointAttemptNumber: 0));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM jobs WHERE id = $id;",
                ("$id", jobId)));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM projects WHERE id = $id;",
                ("$id", ProjectId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migration007_DefinesStrictBoundedProjectCreationDrafts()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);

        CollectionAssert.AreEqual(
            new[]
            {
                "id",
                "revision",
                "payload",
                "updated_at",
                "finalized_project_id",
                "finalized_at",
            },
            GetTableColumns(connection, "project_creation_drafts"));
        CollectionAssert.AreEqual(
            new[] { "id" },
            GetPrimaryKeyColumns(connection, "project_creation_drafts"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(
                connection,
                """
                    SELECT strict
                    FROM pragma_table_list
                    WHERE schema = 'main'
                      AND type = 'table'
                      AND name = 'project_creation_drafts';
                    """));
        CollectionAssert.AreEqual(
            new[] { "projects:finalized_project_id->id:RESTRICT:RESTRICT" },
            GetForeignKeyBindings(connection, "project_creation_drafts"));
        CollectionAssert.AreEqual(
            new[] { "updated_at", "id" },
            GetIndexColumns(connection, "idx_project_creation_drafts_updated_at"));

        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId.ToUpperInvariant()));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                payload: Array.Empty<byte>()));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                payload: "not-a-blob"));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                revision: -1));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                finalizedProjectId: ProjectId,
                finalizedAt: null));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                finalizedProjectId: null,
                finalizedAt: Timestamp));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                finalizedProjectId: ProjectId,
                finalizedAt: "2030-01-02T03:04:06.6789012Z"));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                ProjectCreationDraftId,
                finalizedProjectId: OtherProjectId,
                finalizedAt: Timestamp));

        InsertProject(connection, ProjectId);
        InsertProjectCreationDraft(connection, ProjectCreationDraftId);
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                """
                    UPDATE project_creation_drafts
                    SET payload = zeroblob($size)
                    WHERE id = $id;
                    """,
                ("$size", ProjectCreationDraftStore.MaximumPayloadUtf8Bytes + 1L),
                ("$id", ProjectCreationDraftId)));
        ExecuteNonQuery(
            connection,
            """
                UPDATE project_creation_drafts
                SET revision = 1,
                    finalized_project_id = $project_id,
                    finalized_at = updated_at
                WHERE id = $id;
                """,
            ("$project_id", ProjectId),
            ("$id", ProjectCreationDraftId));
        AssertConstraintViolation(
            () => InsertProjectCreationDraft(
                connection,
                OtherProjectCreationDraftId,
                finalizedProjectId: ProjectId,
                finalizedAt: Timestamp));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM projects WHERE id = $id;",
                ("$id", ProjectId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Schema_DefaultSettingsEnforcesIdentityBoundsBindingsAndRestrictedDeletes()
    {
        using TemporaryDatabase temporary = CreateMigratedDatabase();
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(connection, "PRAGMA foreign_keys;"));

        InsertDefaultSettingsProfile(
            connection,
            DefaultSettingsProfileId,
            "1.0.0",
            DigestA,
            [1]);
        AssertConstraintViolation(
            () => InsertDefaultSettingsProfile(
                connection,
                DefaultSettingsProfileId.ToUpperInvariant(),
                "1.0.0",
                DigestB,
                [1]));
        AssertConstraintViolation(
            () => InsertDefaultSettingsProfile(
                connection,
                OtherDefaultSettingsProfileId,
                "01.0.0",
                DigestB,
                [1]));
        AssertConstraintViolation(
            () => InsertDefaultSettingsProfile(
                connection,
                OtherDefaultSettingsProfileId,
                "1.0.0",
                DigestB.ToUpperInvariant(),
                [1]));
        AssertConstraintViolation(
            () => InsertDefaultSettingsProfile(
                connection,
                OtherDefaultSettingsProfileId,
                "1.0.0",
                DigestB,
                Array.Empty<byte>()));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                """
                    INSERT INTO default_settings_profiles (
                        profile_id,
                        profile_version,
                        profile_digest,
                        profile_payload
                    )
                    VALUES ($profile_id, '1.0.0', $digest, zeroblob($size));
                    """,
                ("$profile_id", OtherDefaultSettingsProfileId),
                ("$digest", DigestB),
                ("$size", DefaultSettingsStore.MaximumCanonicalJsonUtf8Bytes + 1L)));

        AssertConstraintViolation(
            () => InsertGlobalDefaultSettings(
                connection,
                singletonId: 1,
                DefaultSettingsProfileId,
                "1.0.0",
                DigestB));
        InsertGlobalDefaultSettings(
            connection,
            singletonId: 1,
            DefaultSettingsProfileId,
            "1.0.0",
            DigestA);
        AssertConstraintViolation(
            () => InsertGlobalDefaultSettings(
                connection,
                singletonId: 2,
                DefaultSettingsProfileId,
                "1.0.0",
                DigestA));

        AssertConstraintViolation(
            () => InsertProjectDefaultSettings(
                connection,
                ProjectId,
                DefaultSettingsProfileId,
                "1.0.0",
                DigestA));
        InsertProject(connection, ProjectId);
        AssertConstraintViolation(
            () => InsertProjectDefaultSettings(
                connection,
                ProjectId,
                DefaultSettingsProfileId,
                "1.0.0",
                DigestB));
        InsertProjectDefaultSettings(
            connection,
            ProjectId,
            DefaultSettingsProfileId,
            "1.0.0",
            DigestA);

        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                """
                    UPDATE default_settings_profiles
                    SET profile_id = $replacement_profile_id
                    WHERE profile_id = $profile_id;
                    """,
                ("$replacement_profile_id", OtherDefaultSettingsProfileId),
                ("$profile_id", DefaultSettingsProfileId)));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                """
                    UPDATE projects
                    SET id = $replacement_project_id
                    WHERE id = $project_id;
                    """,
                ("$replacement_project_id", OtherProjectId),
                ("$project_id", ProjectId)));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM default_settings_profiles WHERE profile_id = $profile_id;",
                ("$profile_id", DefaultSettingsProfileId)));
        AssertConstraintViolation(
            () => ExecuteNonQuery(
                connection,
                "DELETE FROM projects WHERE id = $id;",
                ("$id", ProjectId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migration009_EnforcesEvaluationBindingsRuleStatesTargetsAndRestrictedDeletes()
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
        InsertDefaultSettingsProfile(
            connection,
            DefaultSettingsProfileId,
            "1.0.0",
            DigestA,
            [1]);
        const string NonRenderArtifactId = "50505050-5050-4050-8050-505050505050";
        InsertArtifact(
            connection,
            ArtifactId,
            ProjectId,
            "renders/evaluation-a.mp4",
            byteSize: 1,
            digest: DigestA);
        InsertArtifact(
            connection,
            OtherArtifactId,
            ProjectId,
            "renders/evaluation-b.mp4",
            byteSize: 1,
            digest: DigestB);
        InsertArtifact(
            connection,
            NonRenderArtifactId,
            ProjectId,
            "proxies/evaluation-preview.mp4",
            byteSize: 1,
            digest: DigestB,
            kind: "Proxy");
        const string ProjectJobId = "10101010-1010-4010-8010-101010101010";
        const string OtherProjectJobId = "20202020-2020-4020-8020-202020202020";
        const string UnknownJobId = "30303030-3030-4030-8030-303030303030";
        const string UnknownArtifactId = "40404040-4040-4040-8040-404040404040";
        InsertJob(connection, ProjectJobId, ProjectId, "evaluation", "succeeded", 1);
        InsertJob(
            connection,
            OtherProjectJobId,
            OtherProjectId,
            "evaluation",
            "succeeded",
            1);

        InsertAutomatedEvaluation(
            connection,
            AuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null);
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            OtherProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null));
        InsertAutomatedEvaluation(
            connection,
            ThirdAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            DigestA,
            DigestA,
            jobId: ProjectJobId);
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            jobId: OtherProjectJobId));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            jobId: UnknownJobId));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            artifactDigest: null,
            outputDigest: DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            UnknownArtifactId,
            DigestA,
            DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            DigestB,
            DigestB));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            NonRenderArtifactId,
            DigestB,
            DigestB));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            DigestA,
            DigestB));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            settingsProfileId: OtherDefaultSettingsProfileId));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            artifactId: null,
            artifactDigest: null,
            outputDigest: null,
            aggregateStatus: "PASS"));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            DigestA,
            DigestA,
            settingsProfileVersion: "2147483648.0.0"));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            DigestA,
            DigestA,
            ruleSetVersion: "2147483648.0.0"));
        AssertConstraintViolation(() => InsertAutomatedEvaluation(
            connection,
            OtherAuditEventId,
            ProjectId,
            VersionId,
            DigestA,
            ArtifactId,
            DigestA,
            DigestA,
            aggregateStatus: "PASS",
            passCount: 17,
            warningCount: 1,
            notEvaluatedCount: 9));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            """
            UPDATE automated_evaluations
            SET artifact_id = $artifact_id,
                artifact_digest = $artifact_digest,
                output_digest = $artifact_digest
            WHERE evaluation_id = $evaluation_id;
            """,
            ("$artifact_id", NonRenderArtifactId),
            ("$artifact_digest", DigestB),
            ("$evaluation_id", AuditEventId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "UPDATE artifacts SET kind = 'Proxy' WHERE id = $artifact_id;",
            ("$artifact_id", ArtifactId)));

        InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "codec",
            "NOT EVALUATED",
            "evaluation.rule_not_evaluated",
            evidenceDigest: null);
        InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "keyboard",
            "PASS",
            "evaluation.rule_passed",
            DigestA,
            scope: "userInterface");
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "fullDecode",
            "PASS",
            "evaluation.rule_passed",
            DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "fullDecode",
            "PASS",
            "evaluation.rule_passed",
            evidenceDigest: null));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "fullDecode",
            "NOT EVALUATED",
            "evaluation.rule_not_evaluated",
            DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "unknownRule",
            "PASS",
            "evaluation.rule_passed",
            DigestA));
        InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "legalCompliance",
            "NOT EVALUATED",
            "evaluation.human_review_required",
            evidenceDigest: null,
            category: "humanLimitation",
            scope: "humanReview");
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "naturalness",
            "PASS",
            "evaluation.rule_passed",
            DigestA,
            category: "humanLimitation",
            scope: "humanReview"));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "focus",
            "PASS",
            "evaluation.rule_passed",
            DigestA,
            category: "machine",
            scope: "artifact"));

        InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "subtitleTiming",
            "FAIL",
            "evaluation.rule_failed",
            DigestA,
            targetKind: "preview",
            targetProjectId: ProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: ArtifactId,
            targetArtifactDigest: DigestA,
            timeBasis: "output",
            timeValue: 25,
            timeRate: 24);
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "loudness",
            "PASS",
            "evaluation.rule_passed",
            DigestA,
            targetKind: "evidence",
            targetProjectId: ProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: ArtifactId,
            targetArtifactDigest: DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "truePeak",
            "WARNING",
            "evaluation.objective_warning",
            DigestA,
            targetKind: "settings",
            targetProjectId: ProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: ArtifactId,
            targetArtifactDigest: DigestA,
            settingKey: null));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "truePeak",
            "WARNING",
            "evaluation.objective_warning",
            DigestA,
            targetKind: "settings",
            targetProjectId: ProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: ArtifactId,
            targetArtifactDigest: DigestA,
            settingKey: "subtitles.1invalid"));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "provenance",
            "WARNING",
            "evaluation.objective_warning",
            DigestA,
            targetKind: "evidence",
            targetProjectId: OtherProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: ArtifactId,
            targetArtifactDigest: DigestA));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "settingsBinding",
            "WARNING",
            "evaluation.objective_warning",
            DigestA,
            targetKind: "preview",
            targetProjectId: ProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: ArtifactId,
            targetArtifactDigest: DigestA,
            timeBasis: "source",
            timeValue: -1,
            timeRate: 1));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            ThirdAuditEventId,
            "avSync",
            "WARNING",
            "evaluation.objective_warning",
            DigestA,
            targetKind: "evidence",
            targetProjectId: ProjectId,
            targetVersionId: VersionId,
            targetVersionDigest: DigestA,
            targetArtifactId: null,
            targetArtifactDigest: null));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            """
            UPDATE automated_evaluation_rules
            SET target_artifact_id = NULL,
                target_artifact_digest = NULL
            WHERE evaluation_id = $evaluation_id
              AND rule_id = 'subtitleTiming';
            """,
            ("$evaluation_id", ThirdAuditEventId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            """
            UPDATE automated_evaluations
            SET artifact_id = $artifact_id,
                artifact_digest = $artifact_digest,
                output_digest = $artifact_digest
            WHERE evaluation_id = $evaluation_id;
            """,
            ("$artifact_id", OtherArtifactId),
            ("$artifact_digest", DigestB),
            ("$evaluation_id", ThirdAuditEventId)));
        AssertConstraintViolation(() => InsertAutomatedEvaluationRule(
            connection,
            AuditEventId,
            "codec",
            "NOT EVALUATED",
            "evaluation.rule_not_evaluated",
            evidenceDigest: null));

        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "DELETE FROM automated_evaluations WHERE evaluation_id = $evaluation_id;",
            ("$evaluation_id", AuditEventId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "DELETE FROM composition_versions WHERE id = $id;",
            ("$id", VersionId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "DELETE FROM artifacts WHERE id = $id;",
            ("$id", ArtifactId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "DELETE FROM default_settings_profiles WHERE profile_id = $profile_id;",
            ("$profile_id", DefaultSettingsProfileId)));
        AssertConstraintViolation(() => ExecuteNonQuery(
            connection,
            "DELETE FROM projects WHERE id = $id;",
            ("$id", ProjectId)));
        AssertDatabaseChecksPass(connection);
    }

    [TestMethod]
    public void Migrate_ExactVersionThreeAppliesRemainingMigrationsWithBackupDigestAndIdempotence()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionThreeDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(3)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 003 to 012 upgrade must create a backup.");
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(
                3L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'audit_chain_entries';"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'role_assignments';"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'term_entries';"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(
                14L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    upgraded,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'role_assignments';"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    upgraded,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'term_entries';"));
            AssertDatabaseChecksPass(upgraded);
        }

        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    [TestMethod]
    public void Migrate_ExactVersionFourAppliesRemainingMigrationsWithBackupAndIdempotence()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionFourDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(4)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 004 to 012 upgrade must create a backup.");
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(
                4L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'role_assignments';"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'term_entries';"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(
                14L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    upgraded,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'term_entries';"));
            AssertDatabaseChecksPass(upgraded);
        }

        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    [TestMethod]
    public void Migrate_ExactVersionFiveAppliesRemainingWithBackupAndIdempotence()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionFiveDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(5)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 005 to 012 upgrade must create a backup.");
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(
                5L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'ingest_workflows';"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(
                14L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    upgraded,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'ingest_workflows';"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    upgraded,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'project_creation_drafts';"));
            AssertDatabaseChecksPass(upgraded);
        }

        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    [TestMethod]
    public void Migrate_ExactVersionSixAppliesRemainingWithBackupAndIdempotence()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionSixDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(6)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 006 to 012 upgrade must create a backup.");
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(
                6L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'project_creation_drafts';"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(
                14L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    upgraded,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'project_creation_drafts';"));
            AssertDatabaseChecksPass(upgraded);
        }

        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    [TestMethod]
    public void Migrate_ExactVersionSevenAppliesRemainingWithoutBackfillAndIsIdempotent()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionSevenDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(7)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 007 to 012 upgrade must create a backup.");
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(18, GetUserTableNames(backup).Length);
            Assert.AreEqual(
                7L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM projects;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%default_settings%';"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(29, GetUserTableNames(upgraded).Length);
            Assert.AreEqual(
                14L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                "008_default_settings",
                ExecuteScalarString(
                    upgraded,
                    "SELECT name FROM schema_migrations WHERE version = 8;"));
            Assert.AreEqual(
                ReadEmbeddedMigrationDigest("008_default_settings").Hex,
                ExecuteScalarString(
                    upgraded,
                    "SELECT content_digest FROM schema_migrations WHERE version = 8;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM projects;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM default_settings_profiles;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM default_settings_global;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM project_default_settings;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM automated_evaluations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM automated_evaluation_rules;"));
            AssertDatabaseChecksPass(upgraded);
        }

        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    [TestMethod]
    public void Migrate_ExactVersionSevenFailureAfter008SqlRollsBackSchemaAndMetadata()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionSevenDatabase(temporary);
        var timeProvider = new ThrowOnSecondUtcNowTimeProvider(FixedUtcNow);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                timeProvider));

        string[] backupsAfterFailure = GetBackupFiles(temporary);
        Assert.AreEqual(1, backupsAfterFailure.Length);
        using (SqliteConnection backup = OpenConnection(
            backupsAfterFailure[0],
            SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(18, GetUserTableNames(backup).Length);
            Assert.AreEqual(
                7L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection failedSource = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(18, GetUserTableNames(failedSource).Length);
            Assert.AreEqual(
                7L,
                ExecuteScalarInt64(failedSource, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    failedSource,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%default_settings%';"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(failedSource, "SELECT COUNT(*) FROM projects;"));
            AssertDatabaseChecksPass(failedSource);
        }

        DatabaseMigrationResult retry = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(1)));
        Assert.IsTrue(retry.Applied);
        Assert.AreEqual(14, retry.SchemaVersion);
        Assert.AreEqual(2, GetBackupFiles(temporary).Length);
        using SqliteConnection migrated = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(29, GetUserTableNames(migrated).Length);
        Assert.AreEqual(
            14L,
            ExecuteScalarInt64(migrated, "SELECT COUNT(*) FROM schema_migrations;"));
        AssertDatabaseChecksPass(migrated);
    }

    [TestMethod]
    public void Migrate_ExactVersionEightAppliesRemainingWithBackupAndIsIdempotent()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionEightDatabase(temporary);

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(8)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 008 to 012 upgrade must create a backup.");
        Assert.IsTrue(Regex.IsMatch(
            Path.GetFileName(backupPath),
            "^hubradio-before-schema-014-[0-9]{8}T[0-9]{13}Z-[0-9a-f]{32}\\.db$",
            RegexOptions.CultureInvariant));
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(21, GetUserTableNames(backup).Length);
            Assert.AreEqual(
                8L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE 'automated_evaluation%';"));
            AssertDatabaseChecksPass(backup);
        }

        using (SqliteConnection upgraded = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(29, GetUserTableNames(upgraded).Length);
            Assert.AreEqual(
                14L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(
                "009_automated_evaluation",
                ExecuteScalarString(
                    upgraded,
                    "SELECT name FROM schema_migrations WHERE version = 9;"));
            Assert.AreEqual(
                ReadEmbeddedMigrationDigest("009_automated_evaluation").Hex,
                ExecuteScalarString(
                    upgraded,
                    "SELECT content_digest FROM schema_migrations WHERE version = 9;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM automated_evaluations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM automated_evaluation_rules;"));
            AssertDatabaseChecksPass(upgraded);
        }

        string[] backups = GetBackupFiles(temporary);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        Assert.IsNull(noOp.BackupPath);
        Assert.AreEqual(result.SchemaDigest, noOp.SchemaDigest);
        CollectionAssert.AreEqual(backups, GetBackupFiles(temporary));
    }

    [TestMethod]
    public void Migrate_ExactVersionNineAppliesRemainingWithoutReinterpretingLegacyRows()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionNineDatabase(temporary);
        using (SqliteConnection versionNine = OpenConnection(temporary.DatabasePath))
        {
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    versionNine,
                    "SELECT COUNT(*) FROM projects WHERE id = $id;",
                    ("$id", ProjectId)));
            InsertCompositionVersion(
                versionNine,
                VersionId,
                ProjectId,
                parentVersionId: null,
                DigestA);
            InsertDefaultSettingsProfile(
                versionNine,
                DefaultSettingsProfileId,
                "1.0.0",
                DigestA,
                [1]);
            InsertApproval(versionNine, ApprovalId, ProjectId, VersionId, DigestA);
            InsertAutomatedEvaluation(
                versionNine,
                AuditEventId,
                ProjectId,
                VersionId,
                DigestA,
                artifactId: null,
                artifactDigest: null,
                outputDigest: null);
            AssertDatabaseChecksPass(versionNine);
        }

        DatabaseMigrationResult result = DatabaseMigrator.Migrate(
            temporary.DatabasePath,
            temporary.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow.AddMinutes(9)));

        Assert.IsTrue(result.Applied);
        Assert.AreEqual(14, result.SchemaVersion);
        Assert.AreEqual(ComputeExpectedSchemaDigest(), result.SchemaDigest);
        string backupPath = result.BackupPath
            ?? throw new AssertFailedException("The 009 to 012 upgrade must create a backup.");
        using (SqliteConnection backup = OpenConnection(backupPath, SqliteOpenMode.ReadOnly))
        {
            Assert.AreEqual(23, GetUserTableNames(backup).Length);
            Assert.AreEqual(
                9L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.AreEqual(1L, ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM approvals;"));
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(backup, "SELECT COUNT(*) FROM automated_evaluations;"));
            Assert.AreEqual(
                0L,
                ExecuteScalarInt64(
                    backup,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE 'quick_start%';"));
            AssertDatabaseChecksPass(backup);
        }

        using SqliteConnection upgraded = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(29, GetUserTableNames(upgraded).Length);
        Assert.AreEqual(
            14L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(1L, ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM approvals;"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM quick_start_workflows;"));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(upgraded, "SELECT COUNT(*) FROM quick_start_state_events;"));
        Assert.AreEqual(
            "010_quick_start_workflows",
            ExecuteScalarString(
                upgraded,
                "SELECT name FROM schema_migrations WHERE version = 10;"));
        AssertDatabaseChecksPass(upgraded);
    }

    [TestMethod]
    public void Migration003_BackfillsLegacyVersionTwoDeterministicallyWithoutRowIdAuthority()
    {
        using TemporaryDatabase first = new();
        using TemporaryDatabase second = new();
        CreateExactVersionTwoDatabase(first);
        CreateExactVersionTwoDatabase(second);
        SeedLegacyAuditEvents(first, reverseInsertionOrder: false);
        SeedLegacyAuditEvents(second, reverseInsertionOrder: true);

        DatabaseMigrationResult firstResult = DatabaseMigrator.Migrate(
            first.DatabasePath,
            first.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow));
        DatabaseMigrationResult secondResult = DatabaseMigrator.Migrate(
            second.DatabasePath,
            second.BackupDirectory,
            new FixedTimeProvider(FixedUtcNow));

        Assert.IsTrue(firstResult.Applied);
        Assert.IsTrue(secondResult.Applied);
        string[] firstChain = GetAuditChainRows(first.DatabasePath);
        string[] secondChain = GetAuditChainRows(second.DatabasePath);
        CollectionAssert.AreEqual(firstChain, secondChain);
        CollectionAssert.AreEqual(
            new[] { OtherAuditEventId, ThirdAuditEventId, AuditEventId },
            firstChain.Select(static row => row.Split('|')[1]).ToArray());
        new AuditLog(first.DatabasePath).Verify();
        new AuditLog(second.DatabasePath).Verify();

        string[] backups = GetBackupFiles(first);
        DatabaseMigrationResult noOp = DatabaseMigrator.Migrate(
            first.DatabasePath,
            first.BackupDirectory,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noOp.Applied);
        CollectionAssert.AreEqual(backups, GetBackupFiles(first));
        CollectionAssert.AreEqual(firstChain, GetAuditChainRows(first.DatabasePath));
    }

    [TestMethod]
    public void Migration003_MalformedLegacyAuditEventRollsBackTableBackfillAndMetadata()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionTwoDatabase(temporary);
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            InsertAuditEvent(
                connection,
                AuditEventId,
                ProjectId,
                compositionVersionId: null,
                compositionVersionDigest: null,
                actorRole: "UnknownRole",
                occurredAt: CanonicalEventTimestamp);
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow)));

        Assert.AreEqual(1, GetBackupFiles(temporary).Length);
        using SqliteConnection verification = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                verification,
                """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'audit_chain_entries';
                    """));
        Assert.AreEqual(
            2L,
            ExecuteScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(
            1L,
            ExecuteScalarInt64(verification, "SELECT COUNT(*) FROM audit_events;"));
        AssertDatabaseChecksPass(verification);
    }

    [TestMethod]
    public void Migration003_LegacyForeignKeyViolationRollsBackTableBackfillAndMetadata()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionTwoDatabase(temporary);
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");
            InsertAuditEvent(
                connection,
                AuditEventId,
                OtherProjectId,
                compositionVersionId: null,
                compositionVersionDigest: null);
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow)));

        Assert.AreEqual(1, GetBackupFiles(temporary).Length);
        using SqliteConnection verification = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                verification,
                """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'audit_chain_entries';
                    """));
        Assert.AreEqual(
            2L,
            ExecuteScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(
            OtherProjectId,
            ExecuteScalarString(
                verification,
                "SELECT project_id FROM audit_events WHERE id = $id;",
                ("$id", AuditEventId)));
        using SqliteCommand command = verification.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        AssertIntegrityCheckIsExactlyOk(verification);
    }

    [TestMethod]
    public void Migration003_MalformedUtf8LegacyReasonRollsBackTableBackfillAndMetadata()
    {
        using TemporaryDatabase temporary = new();
        CreateExactVersionTwoDatabase(temporary);
        using (SqliteConnection connection = OpenConnection(temporary.DatabasePath))
        {
            InsertAuditEvent(
                connection,
                AuditEventId,
                ProjectId,
                compositionVersionId: null,
                compositionVersionDigest: null);
            ExecuteNonQuery(
                connection,
                """
                    UPDATE audit_events
                    SET reason = CAST(X'80' AS TEXT)
                    WHERE id = $id;
                    """,
                ("$id", AuditEventId));
            Assert.AreEqual(
                "80",
                ExecuteScalarString(
                    connection,
                    "SELECT hex(CAST(reason AS BLOB)) FROM audit_events WHERE id = $id;",
                    ("$id", AuditEventId)));
        }

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => DatabaseMigrator.Migrate(
                temporary.DatabasePath,
                temporary.BackupDirectory,
                new FixedTimeProvider(FixedUtcNow)));
        Assert.AreEqual("Stored audit chain data is invalid.", exception.Message);

        Assert.AreEqual(1, GetBackupFiles(temporary).Length);
        using SqliteConnection verification = OpenConnection(temporary.DatabasePath);
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                verification,
                """
                    SELECT COUNT(*)
                    FROM sqlite_schema
                    WHERE type = 'table' AND name = 'audit_chain_entries';
                    """));
        Assert.AreEqual(
            2L,
            ExecuteScalarInt64(verification, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(
            "80",
            ExecuteScalarString(
                verification,
                "SELECT hex(CAST(reason AS BLOB)) FROM audit_events WHERE id = $id;",
                ("$id", AuditEventId)));
        AssertDatabaseChecksPass(verification);
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
        Assert.AreEqual(14, second.SchemaVersion);
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
                            VALUES (15, '015_unknown', $digest, $applied_at);
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
    [DataRow("audit-chain-table")]
    [DataRow("term-table")]
    [DataRow("term-index")]
    [DataRow("project-draft-table")]
    [DataRow("project-draft-index")]
    [DataRow("project-settings-table")]
    [DataRow("project-settings-index")]
    [DataRow("automated-evaluation-table")]
    [DataRow("automated-evaluation-rule-table")]
    [DataRow("automated-evaluation-index")]
    [DataRow("automated-evaluation-artifact-index")]
    [DataRow("automated-evaluation-trigger")]
    [DataRow("quick-start-workflow-table")]
    [DataRow("quick-start-event-table")]
    [DataRow("quick-start-index")]
    [DataRow("quick-start-evaluation-index")]
    [DataRow("quick-start-trigger")]
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
                    "audit-chain-table" => "DROP TABLE audit_chain_entries;",
                    "term-table" => "DROP TABLE term_entries;",
                    "term-index" =>
                        "DROP INDEX idx_term_entries_project_written_form_reading;",
                    "project-draft-table" => "DROP TABLE source_only_executions; DROP TABLE project_creation_drafts;",
                    "project-draft-index" =>
                        "DROP INDEX idx_project_creation_drafts_updated_at;",
                    "project-settings-table" => "DROP TABLE project_default_settings;",
                    "project-settings-index" =>
                        "DROP INDEX idx_project_default_settings_profile;",
                    "automated-evaluation-table" =>
                        "DROP TABLE automated_evaluation_rules; DROP TABLE quick_start_state_events; DROP TABLE quick_start_workflows; DROP TABLE automated_evaluations;",
                    "automated-evaluation-rule-table" =>
                        "DROP TABLE automated_evaluation_rules;",
                    "automated-evaluation-index" =>
                        "DROP INDEX idx_automated_evaluations_version_newest;",
                    "automated-evaluation-artifact-index" =>
                        "DROP INDEX idx_artifacts_evaluation_binding;",
                    "automated-evaluation-trigger" =>
                        "DROP TRIGGER trg_automated_evaluation_rule_target_insert;",
                    "quick-start-workflow-table" =>
                        "DROP TABLE quick_start_state_events; DROP TABLE quick_start_workflows;",
                    "quick-start-event-table" =>
                        "DROP TABLE quick_start_state_events;",
                    "quick-start-index" =>
                        "DROP INDEX idx_quick_start_state_events_version;",
                    "quick-start-evaluation-index" =>
                        "DROP INDEX idx_automated_evaluations_quick_start_binding;",
                    "quick-start-trigger" =>
                        "DROP TRIGGER trg_quick_start_event_immutable_update;",
                    _ => throw new AssertFailedException("Unknown schema mutation."),
                });
            if (mutation is "automated-evaluation-artifact-index" or
                "quick-start-evaluation-index")
            {
                SqliteException mismatch = Assert.ThrowsExactly<SqliteException>(
                    () => AssertDatabaseChecksPass(connection));
                Assert.AreEqual(1, mismatch.SqliteErrorCode);
            }
            else
            {
                AssertDatabaseChecksPass(connection);
            }
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
        Assert.AreEqual(29, GetUserTableNames(migrated).Length);
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
            14L,
            ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.AreEqual(29, GetUserTableNames(connection).Length);
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

    private static void CreateExactVersionTwoDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionOneDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("002_jobs"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (2, '002_jobs', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("002_jobs").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionThreeDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionTwoDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("003_audit_chain"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (3, '003_audit_chain', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("003_audit_chain").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionFourDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionThreeDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("004_role_assignments"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (4, '004_role_assignments', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("004_role_assignments").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionFiveDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionFourDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("005_term_entries"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (5, '005_term_entries', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("005_term_entries").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionSixDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionFiveDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("006_ingest_workflows"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (6, '006_ingest_workflows', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("006_ingest_workflows").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionSevenDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionSixDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("007_project_creation_drafts"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (7, '007_project_creation_drafts', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("007_project_creation_drafts").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionEightDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionSevenDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("008_default_settings"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (8, '008_default_settings', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("008_default_settings").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionNineDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionEightDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        ExecuteNonQuery(connection, ReadEmbeddedMigrationSql("009_automated_evaluation"));
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES (9, '009_automated_evaluation', $digest, $applied_at);
                """,
            ("$digest", ReadEmbeddedMigrationDigest("009_automated_evaluation").Hex),
            ("$applied_at", Timestamp));
        AssertDatabaseChecksPass(connection);
    }

    private static void CreateExactVersionElevenDatabase(TemporaryDatabase temporary)
    {
        CreateExactVersionNineDatabase(temporary);
        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        foreach ((int version, string name) in new[]
        {
            (10, "010_quick_start_workflows"),
            (11, "011_source_only_executions"),
        })
        {
            ExecuteNonQuery(connection, ReadEmbeddedMigrationSql(name));
            ExecuteNonQuery(connection, """
                INSERT INTO schema_migrations (version, name, content_digest, applied_at)
                VALUES ($version, $name, $digest, $applied_at);
                """, ("$version", version), ("$name", name),
                ("$digest", ReadEmbeddedMigrationDigest(name).Hex), ("$applied_at", Timestamp));
        }
        AssertDatabaseChecksPass(connection);
    }

    private static void SeedLegacyAuditEvents(
        TemporaryDatabase temporary,
        bool reverseInsertionOrder)
    {
        (string Id, string OccurredAt, string Reason)[] events =
        [
            (AuditEventId, "2030-01-02T03:04:06.0000000+00:00", "Later event"),
            (ThirdAuditEventId, CanonicalEventTimestamp, "Same time, later ID"),
            (OtherAuditEventId, CanonicalEventTimestamp, "Same time, earlier ID"),
        ];
        if (reverseInsertionOrder)
        {
            Array.Reverse(events);
        }

        using SqliteConnection connection = OpenConnection(temporary.DatabasePath);
        foreach ((string id, string occurredAt, string reason) in events)
        {
            InsertAuditEvent(
                connection,
                id,
                ProjectId,
                compositionVersionId: null,
                compositionVersionDigest: null,
                occurredAt: occurredAt,
                reason: reason);
        }
    }

    private static string[] GetAuditChainRows(string databasePath)
    {
        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                sequence_number,
                audit_event_id,
                format_version,
                previous_hash,
                event_hash
            FROM audit_chain_entries
            ORDER BY sequence_number;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                '|',
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows.ToArray();
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

    private static string[] GetPrimaryKeyColumns(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM pragma_table_info($table)
            WHERE pk > 0
            ORDER BY pk;
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

    private static string[] GetTriggerNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'trigger'
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

    private static void InsertDefaultSettingsProfile(
        SqliteConnection connection,
        string profileId,
        string profileVersion,
        string profileDigest,
        byte[] profilePayload)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO default_settings_profiles (
                    profile_id,
                    profile_version,
                    profile_digest,
                    profile_payload
                )
                VALUES ($profile_id, $profile_version, $profile_digest, $profile_payload);
                """,
            ("$profile_id", profileId),
            ("$profile_version", profileVersion),
            ("$profile_digest", profileDigest),
            ("$profile_payload", profilePayload));
    }

    private static void InsertGlobalDefaultSettings(
        SqliteConnection connection,
        long singletonId,
        string profileId,
        string profileVersion,
        string profileDigest)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO default_settings_global (
                    singleton_id,
                    profile_id,
                    profile_version,
                    profile_digest,
                    snapshot_digest,
                    snapshot_payload,
                    revision,
                    updated_at
                )
                VALUES (
                    $singleton_id,
                    $profile_id,
                    $profile_version,
                    $profile_digest,
                    $snapshot_digest,
                    $snapshot_payload,
                    0,
                    $updated_at
                );
                """,
            ("$singleton_id", singletonId),
            ("$profile_id", profileId),
            ("$profile_version", profileVersion),
            ("$profile_digest", profileDigest),
            ("$snapshot_digest", DigestB),
            ("$snapshot_payload", new byte[] { 2 }),
            ("$updated_at", Timestamp));
    }

    private static void InsertProjectDefaultSettings(
        SqliteConnection connection,
        string projectId,
        string profileId,
        string profileVersion,
        string profileDigest)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO project_default_settings (
                    project_id,
                    profile_id,
                    profile_version,
                    profile_digest,
                    initial_snapshot_digest,
                    initial_snapshot_payload,
                    current_snapshot_digest,
                    current_snapshot_payload,
                    revision,
                    updated_at
                )
                VALUES (
                    $project_id,
                    $profile_id,
                    $profile_version,
                    $profile_digest,
                    $snapshot_digest,
                    $snapshot_payload,
                    $snapshot_digest,
                    $snapshot_payload,
                    0,
                    $updated_at
                );
                """,
            ("$project_id", projectId),
            ("$profile_id", profileId),
            ("$profile_version", profileVersion),
            ("$profile_digest", profileDigest),
            ("$snapshot_digest", DigestB),
            ("$snapshot_payload", new byte[] { 2 }),
            ("$updated_at", Timestamp));
    }

    private static void InsertAutomatedEvaluation(
        SqliteConnection connection,
        string evaluationId,
        string projectId,
        string compositionVersionId,
        string compositionVersionDigest,
        string? artifactId,
        string? artifactDigest,
        string? outputDigest,
        string? jobId = null,
        string settingsProfileId = DefaultSettingsProfileId,
        string settingsProfileVersion = "1.0.0",
        string settingsProfileDigest = DigestA,
        string ruleSetVersion = "1.0.0",
        string environmentDigest = DigestA,
        string toolVersionsDigest = DigestB,
        string? modelVersionsDigest = null,
        string? templateDigest = null,
        string? musicDigest = null,
        string aggregateStatus = "NOT EVALUATED",
        long passCount = 0,
        long warningCount = 0,
        long failCount = 0,
        long notEvaluatedCount = 27,
        string evaluatedAt = CanonicalEventTimestamp,
        string startedAt = CanonicalEventTimestamp)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO automated_evaluations (
                    evaluation_id,
                    job_id,
                    project_id,
                    composition_version_id,
                    composition_version_digest,
                    artifact_id,
                    artifact_digest,
                    settings_profile_id,
                    settings_profile_version,
                    settings_profile_digest,
                    settings_snapshot_digest,
                    rule_set_version,
                    environment_version,
                    environment_digest,
                    tool_versions_digest,
                    model_versions_digest,
                    template_digest,
                    music_digest,
                    started_at,
                    completed_at,
                    evaluated_at,
                    input_digest,
                    output_digest,
                    aggregate_status,
                    pass_count,
                    warning_count,
                    fail_count,
                    not_evaluated_count,
                    canonical_digest,
                    canonical_payload
                )
                VALUES (
                    $evaluation_id,
                    $job_id,
                    $project_id,
                    $composition_version_id,
                    $composition_version_digest,
                    $artifact_id,
                    $artifact_digest,
                    $settings_profile_id,
                    $settings_profile_version,
                    $settings_profile_digest,
                    $settings_snapshot_digest,
                    $rule_set_version,
                    'test-environment',
                    $environment_digest,
                    $tool_versions_digest,
                    $model_versions_digest,
                    $template_digest,
                    $music_digest,
                    $started_at,
                    $completed_at,
                    $evaluated_at,
                    $input_digest,
                    $output_digest,
                    $aggregate_status,
                    $pass_count,
                    $warning_count,
                    $fail_count,
                    $not_evaluated_count,
                    $canonical_digest,
                    X'01'
                );
                """,
            ("$evaluation_id", evaluationId),
            ("$job_id", jobId),
            ("$project_id", projectId),
            ("$composition_version_id", compositionVersionId),
            ("$composition_version_digest", compositionVersionDigest),
            ("$artifact_id", artifactId),
            ("$artifact_digest", artifactDigest),
            ("$settings_profile_id", settingsProfileId),
            ("$settings_profile_version", settingsProfileVersion),
            ("$settings_profile_digest", settingsProfileDigest),
            ("$settings_snapshot_digest", DigestB),
            ("$rule_set_version", ruleSetVersion),
            ("$environment_digest", environmentDigest),
            ("$tool_versions_digest", toolVersionsDigest),
            ("$model_versions_digest", modelVersionsDigest),
            ("$template_digest", templateDigest),
            ("$music_digest", musicDigest),
            ("$started_at", startedAt),
            ("$completed_at", startedAt),
            ("$evaluated_at", evaluatedAt),
            ("$input_digest", DigestA),
            ("$output_digest", outputDigest),
            ("$aggregate_status", aggregateStatus),
            ("$pass_count", passCount),
            ("$warning_count", warningCount),
            ("$fail_count", failCount),
            ("$not_evaluated_count", notEvaluatedCount),
            ("$canonical_digest", DigestB));
    }

    private static void InsertQuickStartWorkflow(
        SqliteConnection connection,
        string startedEventId)
    {
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO quick_start_workflows (
                project_id,
                composition_version_id,
                composition_version_digest,
                current_state,
                revision,
                project_revision,
                project_canonical_digest,
                settings_snapshot_digest,
                evaluation_id,
                evaluation_aggregate_status,
                evaluation_canonical_digest,
                artifact_id,
                artifact_digest,
                self_review_count,
                change_request_count,
                event_chain_digest,
                last_event_id,
                last_event_digest,
                canonical_digest,
                canonical_payload
            ) VALUES (
                $project_id,
                $version_id,
                $version_digest,
                'editing',
                1,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                0,
                0,
                $digest,
                $event_id,
                $digest,
                $digest,
                X'01'
            );
            """,
            ("$project_id", ProjectId),
            ("$version_id", VersionId),
            ("$version_digest", DigestA),
            ("$event_id", startedEventId),
            ("$digest", DigestA));
    }

    private static void InsertQuickStartWorkflowEvent(
        SqliteConnection connection,
        long revision,
        string eventId,
        string kind,
        string occurredAt,
        string? settingsSnapshotDigest = null,
        string? evaluationId = null,
        string? evaluationEvaluatedAt = null,
        string? evaluationAggregateStatus = null,
        string? evaluationCanonicalDigest = null,
        string? evaluationResultState = null)
    {
        bool userEvent = string.Equals(
            kind,
            "StartedEditing",
            StringComparison.Ordinal);
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO quick_start_state_events (
                project_id,
                composition_version_id,
                composition_version_digest,
                revision,
                event_id,
                kind,
                actor_kind,
                actor_id,
                occurred_at,
                project_revision,
                project_canonical_digest,
                settings_snapshot_digest,
                evaluation_id,
                evaluation_evaluated_at,
                evaluation_aggregate_status,
                evaluation_canonical_digest,
                artifact_id,
                artifact_digest,
                evaluation_result_state,
                reason_code,
                publication_scope,
                rights_allowed_for_internal_use,
                external_destination_requested,
                destination_kind,
                authorized_state,
                authorized_revision,
                canonical_digest,
                canonical_payload
            ) VALUES (
                $project_id,
                $version_id,
                $version_digest,
                $revision,
                $event_id,
                $kind,
                $actor_kind,
                $actor_id,
                $occurred_at,
                $project_revision,
                $project_canonical_digest,
                $settings_digest,
                $evaluation_id,
                $evaluation_evaluated_at,
                $evaluation_status,
                $evaluation_digest,
                NULL,
                NULL,
                $evaluation_result_state,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                $canonical_digest,
                X'01'
            );
            """,
            ("$project_id", ProjectId),
            ("$version_id", VersionId),
            ("$version_digest", DigestA),
            ("$revision", revision),
            ("$event_id", eventId),
            ("$kind", kind),
            ("$actor_kind", userEvent ? "user" : "system"),
            ("$actor_id", userEvent
                ? "S-1-5-21-1000"
                : "automated-evaluation-v2"),
            ("$occurred_at", occurredAt),
            ("$project_revision", userEvent ? null : 0L),
            ("$project_canonical_digest", userEvent ? null : DigestA),
            ("$settings_digest", settingsSnapshotDigest),
            ("$evaluation_id", evaluationId),
            ("$evaluation_evaluated_at", evaluationEvaluatedAt),
            ("$evaluation_status", evaluationAggregateStatus),
            ("$evaluation_digest", evaluationCanonicalDigest),
            ("$evaluation_result_state", evaluationResultState),
            ("$canonical_digest", DigestA));
    }

    private static void UpdateQuickStartWorkflow(
        SqliteConnection connection,
        string state,
        long revision,
        string lastEventId,
        string settingsSnapshotDigest,
        string? evaluationId = null,
        string? evaluationAggregateStatus = null,
        string? evaluationCanonicalDigest = null)
    {
        ExecuteNonQuery(
            connection,
            """
            UPDATE quick_start_workflows
            SET current_state = $state,
                revision = $revision,
                project_revision = 0,
                project_canonical_digest = $project_digest,
                settings_snapshot_digest = $settings_digest,
                evaluation_id = $evaluation_id,
                evaluation_aggregate_status = $evaluation_status,
                evaluation_canonical_digest = $evaluation_digest,
                artifact_id = NULL,
                artifact_digest = NULL,
                self_review_count = 0,
                change_request_count = 0,
                event_chain_digest = $canonical_digest,
                last_event_id = $event_id,
                last_event_digest = $canonical_digest,
                canonical_digest = $canonical_digest,
                canonical_payload = X'01'
            WHERE project_id = $project_id
              AND composition_version_id = $version_id
              AND composition_version_digest = $version_digest;
            """,
            ("$state", state),
            ("$revision", revision),
            ("$project_digest", DigestA),
            ("$settings_digest", settingsSnapshotDigest),
            ("$evaluation_id", evaluationId),
            ("$evaluation_status", evaluationAggregateStatus),
            ("$evaluation_digest", evaluationCanonicalDigest),
            ("$event_id", lastEventId),
            ("$canonical_digest", DigestA),
            ("$project_id", ProjectId),
            ("$version_id", VersionId),
            ("$version_digest", DigestA));
    }

    private static void InsertAutomatedEvaluationRule(
        SqliteConnection connection,
        string evaluationId,
        string ruleId,
        string status,
        string reasonCode,
        string? evidenceDigest,
        string category = "machine",
        string scope = "artifact",
        string? targetKind = null,
        string? targetProjectId = null,
        string? targetVersionId = null,
        string? targetVersionDigest = null,
        string? targetArtifactId = null,
        string? targetArtifactDigest = null,
        string? timeBasis = null,
        long? timeValue = null,
        long? timeRate = null,
        string? settingKey = null,
        string? editorKind = null)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO automated_evaluation_rules (
                    evaluation_id,
                    rule_id,
                    category,
                    scope,
                    status,
                    reason_code,
                    evidence_digest,
                    target_kind,
                    target_project_id,
                    target_composition_version_id,
                    target_composition_version_digest,
                    target_artifact_id,
                    target_artifact_digest,
                    time_basis,
                    time_value,
                    time_rate,
                    setting_key,
                    editor_kind
                )
                VALUES (
                    $evaluation_id,
                    $rule_id,
                    $category,
                    $scope,
                    $status,
                    $reason_code,
                    $evidence_digest,
                    $target_kind,
                    $target_project_id,
                    $target_composition_version_id,
                    $target_composition_version_digest,
                    $target_artifact_id,
                    $target_artifact_digest,
                    $time_basis,
                    $time_value,
                    $time_rate,
                    $setting_key,
                    $editor_kind
                );
                """,
            ("$evaluation_id", evaluationId),
            ("$rule_id", ruleId),
            ("$category", category),
            ("$scope", scope),
            ("$status", status),
            ("$reason_code", reasonCode),
            ("$evidence_digest", evidenceDigest),
            ("$target_kind", targetKind),
            ("$target_project_id", targetProjectId),
            ("$target_composition_version_id", targetVersionId),
            ("$target_composition_version_digest", targetVersionDigest),
            ("$target_artifact_id", targetArtifactId),
            ("$target_artifact_digest", targetArtifactDigest),
            ("$time_basis", timeBasis),
            ("$time_value", timeValue),
            ("$time_rate", timeRate),
            ("$setting_key", settingKey),
            ("$editor_kind", editorKind));
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
        string? compositionVersionDigest,
        string actorRole = "Administrator",
        string occurredAt = Timestamp,
        string reason = "Project registered")
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
            ("$actor_role", actorRole),
            ("$occurred_at", occurredAt),
            ("$reason", reason),
            ("$version_id", compositionVersionId),
            ("$digest", compositionVersionDigest));
    }

    private static void InsertAuditChainEntry(
        SqliteConnection connection,
        long sequenceNumber,
        string auditEventId,
        long formatVersion,
        string previousHash,
        string eventHash)
    {
        ExecuteNonQuery(
            connection,
            """
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
                """,
            ("$sequence_number", sequenceNumber),
            ("$audit_event_id", auditEventId),
            ("$format_version", formatVersion),
            ("$previous_hash", previousHash),
            ("$event_hash", eventHash));
    }

    private static void InsertRoleAssignment(
        SqliteConnection connection,
        string actorSid,
        string role,
        string assignedAt = Timestamp,
        string assignedBySid = "S-1-5-21-1-2-3-5001",
        string reason = "Role assigned")
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO role_assignments (
                    actor_sid,
                    role,
                    assigned_at,
                    assigned_by_sid,
                    reason
                )
                VALUES (
                    $actor_sid,
                    $role,
                    $assigned_at,
                    $assigned_by_sid,
                    $reason
                );
                """,
            ("$actor_sid", actorSid),
            ("$role", role),
            ("$assigned_at", assignedAt),
            ("$assigned_by_sid", assignedBySid),
            ("$reason", reason));
    }

    private static void InsertTermEntry(
        SqliteConnection connection,
        string id,
        string? projectId,
        string? seriesId,
        string writtenForm = "term",
        string reading = "reading",
        long version = 0)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO term_entries (
                    id,
                    project_id,
                    series_id,
                    written_form,
                    reading,
                    version
                )
                VALUES (
                    $id,
                    $project_id,
                    $series_id,
                    $written_form,
                    $reading,
                    $version
                );
                """,
            ("$id", id),
            ("$project_id", projectId),
            ("$series_id", seriesId),
            ("$written_form", writtenForm),
            ("$reading", reading),
            ("$version", version));
    }

    private static void InsertArtifact(
        SqliteConnection connection,
        string id,
        string projectId,
        string storageReference,
        long byteSize,
        string digest = DigestA,
        string kind = "Render")
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
            ("$kind", kind),
            ("$storage_reference", storageReference),
            ("$digest", digest),
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

    private static void InsertIngestWorkflow(
        SqliteConnection connection,
        string jobId,
        string projectId,
        long checkpointStage,
        long checkpointAttemptNumber,
        string requestDigest = DigestA,
        string requestSchemaVersion = "media-ingest-workflow-v1",
        object? requestPayload = null,
        string checkpointDigest = DigestB,
        string checkpointSchemaVersion = "media-ingest-checkpoint-v1",
        object? checkpointPayload = null,
        long revision = 0)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO ingest_workflows (
                    job_id,
                    project_id,
                    request_digest,
                    request_schema_version,
                    request_payload,
                    checkpoint_stage,
                    checkpoint_attempt_number,
                    checkpoint_digest,
                    checkpoint_schema_version,
                    checkpoint_payload,
                    revision,
                    updated_at
                )
                VALUES (
                    $job_id,
                    $project_id,
                    $request_digest,
                    $request_schema_version,
                    $request_payload,
                    $checkpoint_stage,
                    $checkpoint_attempt_number,
                    $checkpoint_digest,
                    $checkpoint_schema_version,
                    $checkpoint_payload,
                    $revision,
                    $updated_at
                );
                """,
            ("$job_id", jobId),
            ("$project_id", projectId),
            ("$request_digest", requestDigest),
            ("$request_schema_version", requestSchemaVersion),
            ("$request_payload", requestPayload ?? Encoding.UTF8.GetBytes("{}")),
            ("$checkpoint_stage", checkpointStage),
            ("$checkpoint_attempt_number", checkpointAttemptNumber),
            ("$checkpoint_digest", checkpointDigest),
            ("$checkpoint_schema_version", checkpointSchemaVersion),
            ("$checkpoint_payload", checkpointPayload ?? Encoding.UTF8.GetBytes("{}")),
            ("$revision", revision),
            ("$updated_at", Timestamp));
    }

    private static void InsertProjectCreationDraft(
        SqliteConnection connection,
        string id,
        long revision = 0,
        object? payload = null,
        string updatedAt = Timestamp,
        string? finalizedProjectId = null,
        string? finalizedAt = null)
    {
        ExecuteNonQuery(
            connection,
            """
                INSERT INTO project_creation_drafts (
                    id,
                    revision,
                    payload,
                    updated_at,
                    finalized_project_id,
                    finalized_at
                )
                VALUES (
                    $id,
                    $revision,
                    $payload,
                    $updated_at,
                    $finalized_project_id,
                    $finalized_at
                );
                """,
            ("$id", id),
            ("$revision", revision),
            ("$payload", payload ?? Encoding.UTF8.GetBytes("{}")),
            ("$updated_at", updatedAt),
            ("$finalized_project_id", finalizedProjectId),
            ("$finalized_at", finalizedAt));
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
            (3, "003_audit_chain", ReadEmbeddedMigrationDigest("003_audit_chain")),
            (4, "004_role_assignments", ReadEmbeddedMigrationDigest("004_role_assignments")),
            (5, "005_term_entries", ReadEmbeddedMigrationDigest("005_term_entries")),
            (6, "006_ingest_workflows", ReadEmbeddedMigrationDigest("006_ingest_workflows")),
            (7, "007_project_creation_drafts", ReadEmbeddedMigrationDigest("007_project_creation_drafts")),
            (8, "008_default_settings", ReadEmbeddedMigrationDigest("008_default_settings")),
            (9, "009_automated_evaluation", ReadEmbeddedMigrationDigest("009_automated_evaluation")),
            (10, "010_quick_start_workflows", ReadEmbeddedMigrationDigest("010_quick_start_workflows")),
            (11, "011_source_only_executions", ReadEmbeddedMigrationDigest("011_source_only_executions")),
            (12, "012_subtitle_edits", ReadEmbeddedMigrationDigest("012_subtitle_edits")),
            (13, "013_project_deletions", ReadEmbeddedMigrationDigest("013_project_deletions")),
            (14, "014_project_display_names", ReadEmbeddedMigrationDigest("014_project_display_names")),
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
