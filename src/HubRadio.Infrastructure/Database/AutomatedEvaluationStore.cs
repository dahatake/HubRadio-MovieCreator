using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class AutomatedEvaluationConflictException : InvalidOperationException
{
    public AutomatedEvaluationConflictException(
        EntityId<AutomatedEvaluationIdTag> evaluationId)
        : base(FormattableString.Invariant(
            $"Automated evaluation '{evaluationId}' is already stored with different canonical evidence."))
    {
        ArgumentNullException.ThrowIfNull(evaluationId);
        EvaluationId = evaluationId;
    }

    public EntityId<AutomatedEvaluationIdTag> EvaluationId { get; }
}

public sealed class AutomatedEvaluationStore
{
    public const int MaximumListResults = 1000;

    private const string EntityKind = "automated evaluation";
    private const string TimestampFormat = "O";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;

    public AutomatedEvaluationStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public AutomatedEvaluationRun Append(AutomatedEvaluationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        PreparedRun prepared = Prepare(run, nameof(run));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        AutomatedEvaluationRun? existing = Read(
            connection,
            transaction,
            prepared.Run.EvaluationId);
        if (existing is not null)
        {
            if (!prepared.CanonicalUtf8.AsSpan().SequenceEqual(
                    StrictUtf8.GetBytes(existing.CanonicalJson)))
            {
                throw new AutomatedEvaluationConflictException(
                    prepared.Run.EvaluationId);
            }

            transaction.Commit();
            return existing;
        }

        InsertRun(connection, transaction, prepared);
        foreach (AutomatedEvaluationRuleResult rule in prepared.Run.Rules)
        {
            InsertRule(connection, transaction, prepared.Run.EvaluationId, rule);
        }

        foreach (AutomatedEvaluationRuleResult limitation in prepared.Run.Limitations)
        {
            InsertRule(connection, transaction, prepared.Run.EvaluationId, limitation);
        }

        AutomatedEvaluationRun stored = Read(
            connection,
            transaction,
            prepared.Run.EvaluationId)
            ?? throw InvalidStoredData();
        if (!prepared.CanonicalUtf8.AsSpan().SequenceEqual(
                StrictUtf8.GetBytes(stored.CanonicalJson)))
        {
            throw InvalidStoredData();
        }

        transaction.Commit();
        return stored;
    }

    public AutomatedEvaluationRun? Get(
        EntityId<AutomatedEvaluationIdTag> evaluationId)
    {
        ArgumentNullException.ThrowIfNull(evaluationId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        AutomatedEvaluationRun? run = Read(connection, transaction, evaluationId);
        transaction.Commit();
        return run;
    }

    public IReadOnlyList<AutomatedEvaluationRun> ListNewestForVersion(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        int maximumCount = 100)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        if (maximumCount is <= 0 or > MaximumListResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                $"The maximum result count must be between 1 and {MaximumListResults}.");
        }

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        var ids = new List<EntityId<AutomatedEvaluationIdTag>>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT CAST(evaluation_id AS BLOB)
                FROM automated_evaluations
                WHERE project_id = $project_id
                  AND composition_version_id = $composition_version_id
                  AND composition_version_digest = $composition_version_digest
                ORDER BY evaluated_at DESC, evaluation_id
                LIMIT $maximum_count;
                """;
            SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
            SqliteStoreSupport.AddText(
                command,
                "$composition_version_id",
                compositionVersionId.ToString());
            SqliteStoreSupport.AddText(
                command,
                "$composition_version_digest",
                compositionVersionDigest.Hex);
            SqliteStoreSupport.AddInteger(command, "$maximum_count", maximumCount);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(ParseEntityId<AutomatedEvaluationIdTag>(
                    ReadRequiredText(reader, 0),
                    EntityKind));
            }
        }

        var runs = new List<AutomatedEvaluationRun>(ids.Count);
        foreach (EntityId<AutomatedEvaluationIdTag> id in ids)
        {
            AutomatedEvaluationRun run = Read(connection, transaction, id)
                ?? throw InvalidStoredData();
            if (run.ProjectId != projectId ||
                run.CompositionVersionId != compositionVersionId ||
                run.CompositionVersionDigest != compositionVersionDigest)
            {
                throw InvalidStoredData();
            }

            runs.Add(run);
        }

        transaction.Commit();
        return Array.AsReadOnly(runs.ToArray());
    }

    internal static void ValidateAll(string databasePath)
    {
        string normalizedPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            databasePath,
            nameof(databasePath));
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            normalizedPath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        var evaluationIds = new List<EntityId<AutomatedEvaluationIdTag>>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT CAST(evaluation_id AS BLOB)
                FROM automated_evaluations
                ORDER BY evaluation_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                evaluationIds.Add(ParseEntityId<AutomatedEvaluationIdTag>(
                    ReadRequiredText(reader, 0),
                    EntityKind));
            }
        }

        long expectedRuleCount = 0;
        foreach (EntityId<AutomatedEvaluationIdTag> evaluationId in evaluationIds)
        {
            AutomatedEvaluationRun run = Read(connection, transaction, evaluationId)
                ?? throw InvalidStoredData();
            expectedRuleCount = checked(
                expectedRuleCount + run.Rules.Count + run.Limitations.Count);
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    COUNT(*),
                    COUNT(*) FILTER (WHERE evaluation.evaluation_id IS NULL)
                FROM automated_evaluation_rules AS rule
                LEFT JOIN automated_evaluations AS evaluation
                  ON evaluation.evaluation_id = rule.evaluation_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                reader.GetValue(0) is not long actualRuleCount ||
                reader.GetValue(1) is not long orphanRuleCount ||
                actualRuleCount != expectedRuleCount ||
                orphanRuleCount != 0 ||
                reader.Read())
            {
                throw InvalidStoredData();
            }
        }

        transaction.Commit();
    }

    private static PreparedRun Prepare(
        AutomatedEvaluationRun run,
        string parameterName)
    {
        byte[] canonicalUtf8;
        try
        {
            canonicalUtf8 = StrictUtf8.GetBytes(run.CanonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "The automated-evaluation evidence must contain valid Unicode.",
                parameterName,
                exception);
        }

        if (canonicalUtf8.Length is <= 0 or > AutomatedEvaluationRun.MaximumCanonicalJsonUtf8Bytes)
        {
            throw new ArgumentException(
                "The automated-evaluation evidence exceeds the supported byte limit.",
                parameterName);
        }

        AutomatedEvaluationRun parsed;
        try
        {
            parsed = AutomatedEvaluationRun.ParseCanonical(run.CanonicalJson);
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The automated-evaluation evidence is invalid.",
                parameterName,
                exception);
        }

        if (parsed.CanonicalDigest != run.CanonicalDigest ||
            !canonicalUtf8.AsSpan().SequenceEqual(StrictUtf8.GetBytes(parsed.CanonicalJson)))
        {
            throw new ArgumentException(
                "The automated-evaluation evidence digest is invalid.",
                parameterName);
        }

        return new PreparedRun(parsed, canonicalUtf8);
    }

    private static void InsertRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedRun prepared)
    {
        AutomatedEvaluationRun run = prepared.Run;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                $environment_version,
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
                $canonical_payload
            );
            """;
        SqliteStoreSupport.AddText(command, "$evaluation_id", run.EvaluationId.ToString());
        SqliteStoreSupport.AddNullableText(command, "$job_id", run.JobId?.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", run.ProjectId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            run.CompositionVersionId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_digest",
            run.CompositionVersionDigest.Hex);
        SqliteStoreSupport.AddNullableText(command, "$artifact_id", run.ArtifactId?.ToString());
        SqliteStoreSupport.AddNullableText(command, "$artifact_digest", run.ArtifactDigest?.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$settings_profile_id",
            run.SettingsProfileId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$settings_profile_version",
            run.SettingsProfileVersion.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$settings_profile_digest",
            run.SettingsProfileDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$settings_snapshot_digest",
            run.SettingsSnapshotDigest.Hex);
        SqliteStoreSupport.AddText(command, "$rule_set_version", run.RuleSetVersion.ToString());
        SqliteStoreSupport.AddText(command, "$environment_version", run.EnvironmentVersion);
        SqliteStoreSupport.AddText(command, "$environment_digest", run.EnvironmentDigest.Hex);
        SqliteStoreSupport.AddText(command, "$tool_versions_digest", run.ToolVersionsDigest.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$model_versions_digest",
            run.ModelVersionsDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$template_digest",
            run.TemplateDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$music_digest",
            run.MusicDigest?.Hex);
        SqliteStoreSupport.AddText(command, "$started_at", FormatTimestamp(run.StartedAt));
        SqliteStoreSupport.AddText(command, "$completed_at", FormatTimestamp(run.CompletedAt));
        SqliteStoreSupport.AddText(command, "$evaluated_at", FormatTimestamp(run.EvaluatedAt));
        SqliteStoreSupport.AddText(command, "$input_digest", run.InputDigest.Hex);
        SqliteStoreSupport.AddNullableText(command, "$output_digest", run.OutputDigest?.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$aggregate_status",
            AutomatedEvaluationContract.ToWireName(run.AggregateStatus));
        SqliteStoreSupport.AddInteger(command, "$pass_count", run.StatusCounts.Pass);
        SqliteStoreSupport.AddInteger(command, "$warning_count", run.StatusCounts.Warning);
        SqliteStoreSupport.AddInteger(command, "$fail_count", run.StatusCounts.Fail);
        SqliteStoreSupport.AddInteger(
            command,
            "$not_evaluated_count",
            run.StatusCounts.NotEvaluated);
        SqliteStoreSupport.AddText(command, "$canonical_digest", run.CanonicalDigest.Hex);
        SqliteStoreSupport.AddBlob(command, "$canonical_payload", prepared.CanonicalUtf8);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The automated evaluation was not appended.");
        }
    }

    private static void InsertRule(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<AutomatedEvaluationIdTag> evaluationId,
        AutomatedEvaluationRuleResult rule)
    {
        AutomatedEvaluationTarget? target = rule.Target;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
            """;
        SqliteStoreSupport.AddText(command, "$evaluation_id", evaluationId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$rule_id",
            AutomatedEvaluationContract.ToWireName(rule.RuleId));
        SqliteStoreSupport.AddText(
            command,
            "$category",
            AutomatedEvaluationContract.ToWireName(rule.Category));
        SqliteStoreSupport.AddText(
            command,
            "$scope",
            AutomatedEvaluationContract.ToWireName(rule.Scope));
        SqliteStoreSupport.AddText(
            command,
            "$status",
            AutomatedEvaluationContract.ToWireName(rule.Status));
        SqliteStoreSupport.AddText(command, "$reason_code", rule.ReasonCode);
        SqliteStoreSupport.AddNullableText(
            command,
            "$evidence_digest",
            rule.EvidenceDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$target_kind",
            target is null
                ? null
                : AutomatedEvaluationContract.ToWireName(target.Kind));
        SqliteStoreSupport.AddNullableText(
            command,
            "$target_project_id",
            target?.ProjectId.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$target_composition_version_id",
            target?.CompositionVersionId.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$target_composition_version_digest",
            target?.CompositionVersionDigest.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$target_artifact_id",
            target?.ArtifactId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$target_artifact_digest",
            target?.ArtifactDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$time_basis",
            target?.TimeBasis is null
                ? null
                : AutomatedEvaluationContract.ToWireName(target.TimeBasis.Value));
        AddNullableInteger(command, "$time_value", target?.Time?.Value);
        AddNullableInteger(command, "$time_rate", target?.Time?.Rate);
        SqliteStoreSupport.AddNullableText(command, "$setting_key", target?.SettingKey);
        SqliteStoreSupport.AddNullableText(
            command,
            "$editor_kind",
            target?.EditorKind is null
                ? null
                : AutomatedEvaluationContract.ToWireName(target.EditorKind.Value));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "An automated-evaluation rule was not appended.");
        }
    }

    internal static AutomatedEvaluationRun? Read(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<AutomatedEvaluationIdTag> evaluationId)
    {
        StoredRunRow? row;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    CAST(evaluation_id AS BLOB),
                    CAST(job_id AS BLOB),
                    CAST(project_id AS BLOB),
                    CAST(composition_version_id AS BLOB),
                    CAST(composition_version_digest AS BLOB),
                    CAST(artifact_id AS BLOB),
                    CAST(artifact_digest AS BLOB),
                    CAST(settings_profile_id AS BLOB),
                    CAST(settings_profile_version AS BLOB),
                    CAST(settings_profile_digest AS BLOB),
                    CAST(settings_snapshot_digest AS BLOB),
                    CAST(rule_set_version AS BLOB),
                    CAST(environment_version AS BLOB),
                    CAST(environment_digest AS BLOB),
                    CAST(tool_versions_digest AS BLOB),
                    CAST(model_versions_digest AS BLOB),
                    CAST(template_digest AS BLOB),
                    CAST(music_digest AS BLOB),
                    CAST(started_at AS BLOB),
                    CAST(completed_at AS BLOB),
                    CAST(evaluated_at AS BLOB),
                    CAST(input_digest AS BLOB),
                    CAST(output_digest AS BLOB),
                    CAST(aggregate_status AS BLOB),
                    pass_count,
                    warning_count,
                    fail_count,
                    not_evaluated_count,
                    CAST(canonical_digest AS BLOB),
                    length(CAST(canonical_payload AS BLOB)),
                    CAST(canonical_payload AS BLOB)
                FROM automated_evaluations
                WHERE evaluation_id = $evaluation_id;
                """;
            SqliteStoreSupport.AddText(command, "$evaluation_id", evaluationId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            row = new StoredRunRow(
                ParseEntityId<AutomatedEvaluationIdTag>(ReadRequiredText(reader, 0), EntityKind),
                ParseOptionalEntityId<JobIdTag>(ReadOptionalText(reader, 1), EntityKind),
                ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 2), EntityKind),
                ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 3), EntityKind),
                ParseDigest(ReadRequiredText(reader, 4), EntityKind),
                ParseOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                    ReadOptionalText(reader, 5),
                    EntityKind),
                ParseOptionalDigest(ReadOptionalText(reader, 6), EntityKind),
                ParseEntityId<DefaultSettingsProfileIdTag>(
                    ReadRequiredText(reader, 7),
                    EntityKind),
                ParseVersion(ReadRequiredText(reader, 8), EntityKind),
                ParseDigest(ReadRequiredText(reader, 9), EntityKind),
                ParseDigest(ReadRequiredText(reader, 10), EntityKind),
                ParseVersion(ReadRequiredText(reader, 11), EntityKind),
                ReadRequiredText(reader, 12),
                ParseDigest(ReadRequiredText(reader, 13), EntityKind),
                ParseDigest(ReadRequiredText(reader, 14), EntityKind),
                ParseOptionalDigest(ReadOptionalText(reader, 15), EntityKind),
                ParseOptionalDigest(ReadOptionalText(reader, 16), EntityKind),
                ParseOptionalDigest(ReadOptionalText(reader, 17), EntityKind),
                ParseTimestamp(ReadRequiredText(reader, 18), EntityKind),
                ParseTimestamp(ReadRequiredText(reader, 19), EntityKind),
                ParseTimestamp(ReadRequiredText(reader, 20), EntityKind),
                ParseDigest(ReadRequiredText(reader, 21), EntityKind),
                ParseOptionalDigest(ReadOptionalText(reader, 22), EntityKind),
                ParseStatus(ReadRequiredText(reader, 23), EntityKind),
                ReadRequiredInt32(reader, 24),
                ReadRequiredInt32(reader, 25),
                ReadRequiredInt32(reader, 26),
                ReadRequiredInt32(reader, 27),
                ParseDigest(ReadRequiredText(reader, 28), EntityKind),
                ReadPayload(reader, 29, 30));
            if (reader.Read())
            {
                throw InvalidStoredData();
            }
        }

        AutomatedEvaluationRun run;
        try
        {
            run = AutomatedEvaluationRun.ParseCanonical(
                StrictUtf8.GetString(row.CanonicalPayload));
        }
        catch (Exception exception)
            when (exception is DecoderFallbackException or JsonException or ArgumentException)
        {
            throw InvalidStoredData(exception);
        }

        if (row.EvaluationId != evaluationId ||
            row.EvaluationId != run.EvaluationId ||
            row.JobId != run.JobId ||
            row.ProjectId != run.ProjectId ||
            row.CompositionVersionId != run.CompositionVersionId ||
            row.CompositionVersionDigest != run.CompositionVersionDigest ||
            row.ArtifactId != run.ArtifactId ||
            row.ArtifactDigest != run.ArtifactDigest ||
            row.SettingsProfileId != run.SettingsProfileId ||
            row.SettingsProfileVersion != run.SettingsProfileVersion ||
            row.SettingsProfileDigest != run.SettingsProfileDigest ||
            row.SettingsSnapshotDigest != run.SettingsSnapshotDigest ||
            row.RuleSetVersion != run.RuleSetVersion ||
            !string.Equals(
                row.EnvironmentVersion,
                run.EnvironmentVersion,
                StringComparison.Ordinal) ||
            row.EnvironmentDigest != run.EnvironmentDigest ||
            row.ToolVersionsDigest != run.ToolVersionsDigest ||
            row.ModelVersionsDigest != run.ModelVersionsDigest ||
            row.TemplateDigest != run.TemplateDigest ||
            row.MusicDigest != run.MusicDigest ||
            row.StartedAt != run.StartedAt ||
            row.CompletedAt != run.CompletedAt ||
            row.EvaluatedAt != run.EvaluatedAt ||
            row.InputDigest != run.InputDigest ||
            row.OutputDigest != run.OutputDigest ||
            row.AggregateStatus != run.AggregateStatus ||
            row.PassCount != run.StatusCounts.Pass ||
            row.WarningCount != run.StatusCounts.Warning ||
            row.FailCount != run.StatusCounts.Fail ||
            row.NotEvaluatedCount != run.StatusCounts.NotEvaluated ||
            row.CanonicalDigest != run.CanonicalDigest ||
            row.CanonicalDigest != ContentDigest.Compute(row.CanonicalPayload))
        {
            throw InvalidStoredData();
        }

        ValidateRuleRows(connection, transaction, run);
        return run;
    }

    private static void ValidateRuleRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AutomatedEvaluationRun run)
    {
        Dictionary<string, AutomatedEvaluationRuleResult> expected = run.Rules
            .Concat(run.Limitations)
            .ToDictionary(
            static rule => AutomatedEvaluationContract.ToWireName(rule.RuleId),
            StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(evaluation_id AS BLOB),
                CAST(rule_id AS BLOB),
                CAST(category AS BLOB),
                CAST(scope AS BLOB),
                CAST(status AS BLOB),
                CAST(reason_code AS BLOB),
                CAST(evidence_digest AS BLOB),
                CAST(target_kind AS BLOB),
                CAST(target_project_id AS BLOB),
                CAST(target_composition_version_id AS BLOB),
                CAST(target_composition_version_digest AS BLOB),
                CAST(target_artifact_id AS BLOB),
                CAST(target_artifact_digest AS BLOB),
                CAST(time_basis AS BLOB),
                time_value,
                time_rate,
                CAST(setting_key AS BLOB),
                CAST(editor_kind AS BLOB)
            FROM automated_evaluation_rules
            WHERE evaluation_id = $evaluation_id
            ORDER BY rule_id;
            """;
        SqliteStoreSupport.AddText(command, "$evaluation_id", run.EvaluationId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            EntityId<AutomatedEvaluationIdTag> storedEvaluationId =
                ParseEntityId<AutomatedEvaluationIdTag>(
                    ReadRequiredText(reader, 0),
                    EntityKind);
            string ruleId = ReadRequiredText(reader, 1);
            if (storedEvaluationId != run.EvaluationId ||
                !observed.Add(ruleId) ||
                !expected.TryGetValue(ruleId, out AutomatedEvaluationRuleResult? rule))
            {
                throw InvalidStoredData();
            }

            string category = ReadRequiredText(reader, 2);
            string scope = ReadRequiredText(reader, 3);
            AutomatedEvaluationStatus status = ParseStatus(
                ReadRequiredText(reader, 4),
                EntityKind);
            string reasonCode = ReadRequiredText(reader, 5);
            ContentDigest? evidenceDigest = ParseOptionalDigest(
                ReadOptionalText(reader, 6),
                EntityKind);
            AutomatedEvaluationTarget? target = rule.Target;
            string? targetKind = ReadOptionalText(reader, 7);
            EntityId<ProjectIdTag>? targetProjectId = ParseOptionalEntityId<ProjectIdTag>(
                ReadOptionalText(reader, 8),
                EntityKind);
            EntityId<CompositionVersionIdTag>? targetVersionId =
                ParseOptionalEntityId<CompositionVersionIdTag>(
                    ReadOptionalText(reader, 9),
                    EntityKind);
            ContentDigest? targetVersionDigest = ParseOptionalDigest(
                ReadOptionalText(reader, 10),
                EntityKind);
            EntityId<AutomatedEvaluationArtifactIdTag>? targetArtifactId =
                ParseOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                    ReadOptionalText(reader, 11),
                    EntityKind);
            ContentDigest? targetArtifactDigest = ParseOptionalDigest(
                ReadOptionalText(reader, 12),
                EntityKind);
            string? timeBasis = ReadOptionalText(reader, 13);
            long? timeValue = ReadOptionalInt64(reader, 14);
            long? timeRate = ReadOptionalInt64(reader, 15);
            string? settingKey = ReadOptionalText(reader, 16);
            string? editorKind = ReadOptionalText(reader, 17);

            if (!string.Equals(
                    category,
                    AutomatedEvaluationContract.ToWireName(rule.Category),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    scope,
                    AutomatedEvaluationContract.ToWireName(rule.Scope),
                    StringComparison.Ordinal) ||
                status != rule.Status ||
                !string.Equals(reasonCode, rule.ReasonCode, StringComparison.Ordinal) ||
                evidenceDigest != rule.EvidenceDigest ||
                !string.Equals(
                    targetKind,
                    target is null
                        ? null
                        : AutomatedEvaluationContract.ToWireName(target.Kind),
                    StringComparison.Ordinal) ||
                targetProjectId != target?.ProjectId ||
                targetVersionId != target?.CompositionVersionId ||
                targetVersionDigest != target?.CompositionVersionDigest ||
                targetArtifactId != target?.ArtifactId ||
                targetArtifactDigest != target?.ArtifactDigest ||
                !string.Equals(
                    timeBasis,
                    target?.TimeBasis is null
                        ? null
                        : AutomatedEvaluationContract.ToWireName(target.TimeBasis.Value),
                    StringComparison.Ordinal) ||
                timeValue != target?.Time?.Value ||
                timeRate != target?.Time?.Rate ||
                !string.Equals(settingKey, target?.SettingKey, StringComparison.Ordinal) ||
                !string.Equals(
                    editorKind,
                    target?.EditorKind is null
                        ? null
                        : AutomatedEvaluationContract.ToWireName(target.EditorKind.Value),
                    StringComparison.Ordinal))
            {
                throw InvalidStoredData();
            }
        }

        if (observed.Count != AutomatedEvaluationRules.All.Count ||
            observed.Count != expected.Count)
        {
            throw InvalidStoredData();
        }
    }

    private static byte[] ReadPayload(
        SqliteDataReader reader,
        int lengthOrdinal,
        int payloadOrdinal)
    {
        if (reader.GetValue(lengthOrdinal) is not long length ||
            length is <= 0 or > AutomatedEvaluationRun.MaximumCanonicalJsonUtf8Bytes ||
            reader.GetValue(payloadOrdinal) is not byte[] payload ||
            payload.LongLength != length)
        {
            throw InvalidStoredData();
        }

        return payload;
    }

    private static string ReadRequiredText(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not byte[] bytes || bytes.Length == 0)
        {
            throw InvalidStoredData();
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidStoredData(exception);
        }
    }

    private static string? ReadOptionalText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ReadRequiredText(reader, ordinal);
    }

    private static long? ReadOptionalInt64(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is not long value)
        {
            throw InvalidStoredData();
        }

        return value;
    }

    private static int ReadRequiredInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long value ||
            value < int.MinValue ||
            value > int.MaxValue)
        {
            throw InvalidStoredData();
        }

        return (int)value;
    }

    private static EntityId<TTag> ParseEntityId<TTag>(string value, string entityKind)
    {
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return id;
    }

    private static EntityId<TTag>? ParseOptionalEntityId<TTag>(
        string? value,
        string entityKind) =>
        value is null ? null : ParseEntityId<TTag>(value, entityKind);

    private static ContentDigest ParseDigest(string value, string entityKind)
    {
        if (!ContentDigest.TryParse(value, out ContentDigest? digest) ||
            !string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return digest;
    }

    private static ContentDigest? ParseOptionalDigest(string? value, string entityKind) =>
        value is null ? null : ParseDigest(value, entityKind);

    private static SettingsProfileVersion ParseVersion(string value, string entityKind)
    {
        if (!SettingsProfileVersion.TryParse(value, out SettingsProfileVersion? version) ||
            !string.Equals(value, version.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return version;
    }

    private static DateTimeOffset ParseTimestamp(string value, string entityKind)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp) ||
            timestamp.Offset != TimeSpan.Zero ||
            !string.Equals(value, FormatTimestamp(timestamp), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return timestamp;
    }

    private static AutomatedEvaluationStatus ParseStatus(string value, string entityKind)
    {
        try
        {
            return AutomatedEvaluationContract.ParseStatus(value);
        }
        catch (JsonException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void AddNullableInteger(
        SqliteCommand command,
        string name,
        long? value)
    {
        command.Parameters.Add(name, SqliteType.Integer).Value =
            value ?? (object)DBNull.Value;
    }

    private static InvalidOperationException InvalidStoredData() =>
        SqliteStoreSupport.InvalidStoredData(EntityKind);

    private static InvalidOperationException InvalidStoredData(Exception innerException) =>
        new("Stored automated evaluation data is invalid.", innerException);

    private sealed record PreparedRun(
        AutomatedEvaluationRun Run,
        byte[] CanonicalUtf8);

    private sealed record StoredRunRow(
        EntityId<AutomatedEvaluationIdTag> EvaluationId,
        EntityId<JobIdTag>? JobId,
        EntityId<ProjectIdTag> ProjectId,
        EntityId<CompositionVersionIdTag> CompositionVersionId,
        ContentDigest CompositionVersionDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId,
        ContentDigest? ArtifactDigest,
        EntityId<DefaultSettingsProfileIdTag> SettingsProfileId,
        SettingsProfileVersion SettingsProfileVersion,
        ContentDigest SettingsProfileDigest,
        ContentDigest SettingsSnapshotDigest,
        SettingsProfileVersion RuleSetVersion,
        string EnvironmentVersion,
        ContentDigest EnvironmentDigest,
        ContentDigest ToolVersionsDigest,
        ContentDigest? ModelVersionsDigest,
        ContentDigest? TemplateDigest,
        ContentDigest? MusicDigest,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        DateTimeOffset EvaluatedAt,
        ContentDigest InputDigest,
        ContentDigest? OutputDigest,
        AutomatedEvaluationStatus AggregateStatus,
        int PassCount,
        int WarningCount,
        int FailCount,
        int NotEvaluatedCount,
        ContentDigest CanonicalDigest,
        byte[] CanonicalPayload);
}
