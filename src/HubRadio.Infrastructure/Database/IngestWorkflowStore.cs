using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoredIngestWorkflow
{
    internal StoredIngestWorkflow(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> projectId,
        string requestSchemaVersion,
        ContentDigest requestDigest,
        string requestPayloadJson,
        int checkpointStage,
        int checkpointAttemptNumber,
        string checkpointSchemaVersion,
        ContentDigest checkpointDigest,
        string checkpointPayloadJson,
        int revision,
        DateTimeOffset updatedAt)
    {
        JobId = jobId;
        ProjectId = projectId;
        RequestSchemaVersion = requestSchemaVersion;
        RequestDigest = requestDigest;
        RequestPayloadJson = requestPayloadJson;
        CheckpointStage = checkpointStage;
        CheckpointAttemptNumber = checkpointAttemptNumber;
        CheckpointSchemaVersion = checkpointSchemaVersion;
        CheckpointDigest = checkpointDigest;
        CheckpointPayloadJson = checkpointPayloadJson;
        Revision = revision;
        UpdatedAt = updatedAt;
    }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public string RequestSchemaVersion { get; }

    public ContentDigest RequestDigest { get; }

    public string RequestPayloadJson { get; }

    public int CheckpointStage { get; }

    public int CheckpointAttemptNumber { get; }

    public string CheckpointSchemaVersion { get; }

    public ContentDigest CheckpointDigest { get; }

    public string CheckpointPayloadJson { get; }

    public int Revision { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public sealed class IngestWorkflowStore
{
    // Keep durable workflow JSON within the same bounded recovery envelope as
    // backup manifests. This is a resource-safety limit, not a media-quality gate.
    public const int MaximumPayloadUtf8Bytes = 64 * 1024 * 1024;

    private const string EntityKind = "ingest workflow";
    private const int LastCheckpointStage = 9;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;

    public IngestWorkflowStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public StoredIngestWorkflow RegisterOrGet(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> projectId,
        string requestSchemaVersion,
        ContentDigest requestDigest,
        string requestPayloadJson,
        string checkpointSchemaVersion,
        string initialCheckpointPayloadJson)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(requestDigest);
        ValidateSchemaVersion(requestSchemaVersion, nameof(requestSchemaVersion));
        ValidateSchemaVersion(checkpointSchemaVersion, nameof(checkpointSchemaVersion));

        byte[] requestPayload = CanonicalizeBoundedPayload(
            requestPayloadJson,
            nameof(requestPayloadJson));
        EnsureDigestMatches(
            requestDigest,
            requestPayload,
            nameof(requestDigest));
        string canonicalRequestPayloadJson = StrictUtf8.GetString(requestPayload);
        byte[] checkpointPayload = CanonicalizeBoundedPayload(
            initialCheckpointPayloadJson,
            nameof(initialCheckpointPayloadJson));
        ContentDigest checkpointDigest = ContentDigest.Compute(checkpointPayload);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredIngestWorkflow? existing = ReadWorkflow(
            connection,
            transaction,
            jobId);
        JobBinding job = ReadJobBinding(connection, transaction, jobId);
        EnsureProjectBinding(jobId, projectId, job.ProjectId);

        if (existing is not null)
        {
            EnsureRegistrationMatches(
                existing,
                projectId,
                requestSchemaVersion,
                requestDigest,
                canonicalRequestPayloadJson);
            transaction.Commit();
            return existing;
        }

        if (job.State != JobState.Queued || job.CurrentAttemptNumber != 0)
        {
            throw new InvalidOperationException(
                "A new ingest workflow requires its queued job before the first attempt.");
        }

        string updatedAt = SqliteStoreSupport.FormatTimestamp(
            job.UpdatedAt,
            nameof(jobId));
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                0,
                0,
                $checkpoint_digest,
                $checkpoint_schema_version,
                $checkpoint_payload,
                0,
                $updated_at
            );
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        SqliteStoreSupport.AddText(command, "$request_digest", requestDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$request_schema_version",
            requestSchemaVersion);
        SqliteStoreSupport.AddBlob(command, "$request_payload", requestPayload);
        SqliteStoreSupport.AddText(
            command,
            "$checkpoint_digest",
            checkpointDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$checkpoint_schema_version",
            checkpointSchemaVersion);
        SqliteStoreSupport.AddBlob(
            command,
            "$checkpoint_payload",
            checkpointPayload);
        SqliteStoreSupport.AddText(command, "$updated_at", updatedAt);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The ingest workflow was not registered.");
        }

        StoredIngestWorkflow registered = ReadRequiredWorkflow(
            connection,
            transaction,
            jobId);
        transaction.Commit();
        return registered;
    }

    public StoredIngestWorkflow? Get(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        return ReadWorkflow(connection, transaction: null, jobId);
    }

    public StoredIngestWorkflow AdvanceCheckpoint(
        EntityId<JobIdTag> jobId,
        ContentDigest expectedRequestDigest,
        int expectedRevision,
        int completedStage,
        int attemptNumber,
        string checkpointSchemaVersion,
        string checkpointPayloadJson,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(expectedRequestDigest);
        ValidateExpectedRevision(expectedRevision);
        if (completedStage is < 1 or > LastCheckpointStage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedStage),
                completedStage,
                "A completed ingest checkpoint stage must be between one and nine.");
        }

        if (attemptNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptNumber),
                attemptNumber,
                "An ingest checkpoint attempt number must be positive.");
        }

        ValidateSchemaVersion(checkpointSchemaVersion, nameof(checkpointSchemaVersion));
        byte[] checkpointPayload = CanonicalizeBoundedPayload(
            checkpointPayloadJson,
            nameof(checkpointPayloadJson));
        ContentDigest checkpointDigest = ContentDigest.Compute(checkpointPayload);
        string storedUpdatedAt = SqliteStoreSupport.FormatTimestamp(
            updatedAt,
            nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredIngestWorkflow current = ReadRequiredWorkflow(
            connection,
            transaction,
            jobId);
        EnsureExpectedRevision(jobId, expectedRevision, current.Revision);
        if (current.RequestDigest != expectedRequestDigest)
        {
            throw new ArgumentException(
                "The checkpoint request digest does not match the registered request.",
                nameof(expectedRequestDigest));
        }

        if (completedStage != checked(current.CheckpointStage + 1))
        {
            throw new InvalidOperationException(
                "An ingest checkpoint must advance exactly one stage.");
        }

        if (attemptNumber < current.CheckpointAttemptNumber)
        {
            throw new InvalidOperationException(
                "An ingest checkpoint cannot move to an earlier attempt.");
        }

        if (updatedAt.ToUniversalTime() < current.UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "An ingest checkpoint timestamp cannot move backwards.");
        }

        JobBinding job = ReadJobBinding(connection, transaction, jobId);
        EnsureProjectBinding(jobId, current.ProjectId, job.ProjectId);
        if (job.State != JobState.Running ||
            job.CurrentAttemptNumber != attemptNumber)
        {
            throw new InvalidOperationException(
                "An ingest checkpoint requires the exact running job attempt.");
        }

        int newRevision = checked(current.Revision + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ingest_workflows
            SET checkpoint_stage = $checkpoint_stage,
                checkpoint_attempt_number = $checkpoint_attempt_number,
                checkpoint_digest = $checkpoint_digest,
                checkpoint_schema_version = $checkpoint_schema_version,
                checkpoint_payload = $checkpoint_payload,
                revision = $new_revision,
                updated_at = $updated_at
            WHERE job_id = $job_id
                AND revision = $expected_revision;
            """;
        SqliteStoreSupport.AddInteger(
            command,
            "$checkpoint_stage",
            completedStage);
        SqliteStoreSupport.AddInteger(
            command,
            "$checkpoint_attempt_number",
            attemptNumber);
        SqliteStoreSupport.AddText(
            command,
            "$checkpoint_digest",
            checkpointDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$checkpoint_schema_version",
            checkpointSchemaVersion);
        SqliteStoreSupport.AddBlob(
            command,
            "$checkpoint_payload",
            checkpointPayload);
        SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
        SqliteStoreSupport.AddText(command, "$updated_at", storedUpdatedAt);
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        SqliteStoreSupport.AddInteger(
            command,
            "$expected_revision",
            expectedRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            StoredIngestWorkflow latest = ReadRequiredWorkflow(
                connection,
                transaction,
                jobId);
            EnsureExpectedRevision(jobId, expectedRevision, latest.Revision);
            throw InvalidStoredWorkflow();
        }

        StoredIngestWorkflow advanced = ReadRequiredWorkflow(
            connection,
            transaction,
            jobId);
        if (advanced.Revision != newRevision ||
            advanced.CheckpointStage != completedStage ||
            advanced.CheckpointAttemptNumber != attemptNumber ||
            advanced.CheckpointDigest != checkpointDigest)
        {
            throw InvalidStoredWorkflow();
        }

        transaction.Commit();
        return advanced;
    }

    private static StoredIngestWorkflow? ReadWorkflow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<JobIdTag> jobId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + """
            WHERE job_id = $job_id;
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredIngestWorkflow workflow = ReadStoredWorkflow(reader);
        if (reader.Read() || workflow.JobId != jobId)
        {
            throw InvalidStoredWorkflow();
        }

        return workflow;
    }

    private static StoredIngestWorkflow ReadRequiredWorkflow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId) =>
        ReadWorkflow(connection, transaction, jobId)
            ?? throw new InvalidOperationException(
                "The ingest workflow is not registered.");

    private static StoredIngestWorkflow ReadStoredWorkflow(
        SqliteDataReader reader)
    {
        EntityId<JobIdTag> jobId =
            SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 0, EntityKind);
        EntityId<ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, EntityKind);
        ContentDigest requestDigest =
            SqliteStoreSupport.ReadContentDigest(reader, 2, EntityKind);
        string requestSchemaVersion = ValidateStoredSchemaVersion(
            SqliteStoreSupport.ReadString(reader, 3, EntityKind));
        EnsureStoredPayloadLength(
            SqliteStoreSupport.ReadInt64(reader, 4, EntityKind));
        string requestPayloadJson =
            SqliteStoreSupport.ReadCanonicalPayload(reader, 5, EntityKind);
        int checkpointStage =
            SqliteStoreSupport.ReadInt32(reader, 6, EntityKind);
        int checkpointAttemptNumber =
            SqliteStoreSupport.ReadInt32(reader, 7, EntityKind);
        ContentDigest checkpointDigest =
            SqliteStoreSupport.ReadContentDigest(reader, 8, EntityKind);
        string checkpointSchemaVersion = ValidateStoredSchemaVersion(
            SqliteStoreSupport.ReadString(reader, 9, EntityKind));
        EnsureStoredPayloadLength(
            SqliteStoreSupport.ReadInt64(reader, 10, EntityKind));
        string checkpointPayloadJson =
            SqliteStoreSupport.ReadCanonicalPayload(reader, 11, EntityKind);
        int revision = SqliteStoreSupport.ReadInt32(reader, 12, EntityKind);
        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 13, EntityKind);

        if (checkpointStage is < 0 or > LastCheckpointStage ||
            checkpointAttemptNumber < 0 ||
            revision < 0 ||
            (checkpointStage == 0) != (checkpointAttemptNumber == 0))
        {
            throw InvalidStoredWorkflow();
        }

        EnsureStoredDigestMatches(requestDigest, requestPayloadJson);
        EnsureStoredDigestMatches(checkpointDigest, checkpointPayloadJson);
        return new StoredIngestWorkflow(
            jobId,
            projectId,
            requestSchemaVersion,
            requestDigest,
            requestPayloadJson,
            checkpointStage,
            checkpointAttemptNumber,
            checkpointSchemaVersion,
            checkpointDigest,
            checkpointPayloadJson,
            revision,
            updatedAt);
    }

    private static JobBinding ReadJobBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, state, current_attempt_number, updated_at
            FROM jobs
            WHERE id = $job_id;
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "The ingest workflow job does not exist.");
        }

        EntityId<ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, EntityKind);
        JobState state = ParseJobState(
            SqliteStoreSupport.ReadString(reader, 1, EntityKind));
        int currentAttemptNumber =
            SqliteStoreSupport.ReadInt32(reader, 2, EntityKind);
        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 3, EntityKind);
        if (reader.Read() || currentAttemptNumber < 0)
        {
            throw InvalidStoredWorkflow();
        }

        return new JobBinding(
            projectId,
            state,
            currentAttemptNumber,
            updatedAt);
    }

    private static void EnsureRegistrationMatches(
        StoredIngestWorkflow existing,
        EntityId<ProjectIdTag> projectId,
        string requestSchemaVersion,
        ContentDigest requestDigest,
        string requestPayloadJson)
    {
        if (existing.ProjectId != projectId ||
            !string.Equals(
                existing.RequestSchemaVersion,
                requestSchemaVersion,
                StringComparison.Ordinal) ||
            existing.RequestDigest != requestDigest ||
            !string.Equals(
                existing.RequestPayloadJson,
                requestPayloadJson,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The registered ingest workflow does not match the request.");
        }
    }

    private static void EnsureProjectBinding(
        EntityId<JobIdTag> jobId,
        EntityId<ProjectIdTag> expected,
        EntityId<ProjectIdTag> actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"The ingest workflow job '{jobId}' belongs to a different project.");
        }
    }

    private static void EnsureDigestMatches(
        ContentDigest expected,
        byte[] canonicalPayload,
        string parameterName)
    {
        if (ContentDigest.Compute(canonicalPayload) != expected)
        {
            throw new ArgumentException(
                "The payload digest does not match its canonical JSON.",
                parameterName);
        }
    }

    private static byte[] CanonicalizeBoundedPayload(
        string payloadJson,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(payloadJson, parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(payloadJson) > MaximumPayloadUtf8Bytes)
            {
                throw new ArgumentException(
                    "Durable workflow JSON exceeds the supported byte limit.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "Durable workflow JSON must contain valid Unicode.",
                parameterName);
        }

        byte[] canonical = SqliteStoreSupport.CanonicalizePayload(
            payloadJson,
            parameterName);
        if (canonical.Length > MaximumPayloadUtf8Bytes)
        {
            throw new ArgumentException(
                "Durable workflow JSON exceeds the supported byte limit.",
                parameterName);
        }

        return canonical;
    }

    private static void EnsureStoredPayloadLength(long byteLength)
    {
        if (byteLength is <= 0 or > MaximumPayloadUtf8Bytes)
        {
            throw InvalidStoredWorkflow();
        }
    }

    private static void EnsureStoredDigestMatches(
        ContentDigest expected,
        string canonicalPayloadJson)
    {
        try
        {
            if (ContentDigest.Compute(StrictUtf8.GetBytes(canonicalPayloadJson)) != expected)
            {
                throw InvalidStoredWorkflow();
            }
        }
        catch (EncoderFallbackException)
        {
            throw InvalidStoredWorkflow();
        }
    }

    private static void ValidateSchemaVersion(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A schema version must be nonblank without surrounding whitespace.",
                parameterName);
        }
    }

    private static string ValidateStoredSchemaVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw InvalidStoredWorkflow();
        }

        return value;
    }

    private static void ValidateExpectedRevision(int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected ingest-workflow revision must not be negative.");
        }
    }

    private static void EnsureExpectedRevision(
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        int actualRevision)
    {
        if (expectedRevision != actualRevision)
        {
            throw new StoreConcurrencyException(
                EntityKind,
                jobId.ToString(),
                expectedRevision,
                actualRevision);
        }
    }

    private static JobState ParseJobState(string value) =>
        value switch
        {
            "queued" => JobState.Queued,
            "running" => JobState.Running,
            "succeeded" => JobState.Succeeded,
            "failed" => JobState.Failed,
            "cancelled" => JobState.Cancelled,
            "interrupted" => JobState.Interrupted,
            _ => throw InvalidStoredWorkflow(),
        };

    private const string SelectColumns = """
        SELECT
            job_id,
            project_id,
            request_digest,
            request_schema_version,
            length(CAST(request_payload AS BLOB)),
            CAST(request_payload AS BLOB),
            checkpoint_stage,
            checkpoint_attempt_number,
            checkpoint_digest,
            checkpoint_schema_version,
            length(CAST(checkpoint_payload AS BLOB)),
            CAST(checkpoint_payload AS BLOB),
            revision,
            updated_at
        FROM ingest_workflows
        """ + "\n";

    private static InvalidOperationException InvalidStoredWorkflow() =>
        SqliteStoreSupport.InvalidStoredData(EntityKind);

    private readonly record struct JobBinding(
        EntityId<ProjectIdTag> ProjectId,
        JobState State,
        int CurrentAttemptNumber,
        DateTimeOffset UpdatedAt);
}
