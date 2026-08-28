using System.Globalization;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoredJob
{
    internal StoredJob(Job job, int revision)
    {
        Job = job;
        Revision = revision;
    }

    public Job Job { get; }

    public int Revision { get; }
}

public sealed class StoredJobAggregate
{
    internal StoredJobAggregate(
        Job job,
        int revision,
        IReadOnlyList<JobAttempt> attempts,
        IReadOnlyList<JobProgress> progress,
        IReadOnlyList<JobHeartbeat> heartbeats)
    {
        Job = job;
        Revision = revision;
        Attempts = Array.AsReadOnly(attempts.ToArray());
        Progress = Array.AsReadOnly(progress.ToArray());
        Heartbeats = Array.AsReadOnly(heartbeats.ToArray());
    }

    public Job Job { get; }

    public int Revision { get; }

    public IReadOnlyList<JobAttempt> Attempts { get; }

    public IReadOnlyList<JobProgress> Progress { get; }

    public IReadOnlyList<JobHeartbeat> Heartbeats { get; }
}

public sealed class JobStore
{
    private readonly string _databasePath;

    public JobStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public void CreateJob(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State != JobState.Queued || job.CurrentAttemptNumber != 0)
        {
            throw new ArgumentException(
                "A newly stored job must be queued before its first attempt.",
                nameof(job));
        }

        string createdAt = SqliteStoreSupport.FormatTimestamp(job.CreatedAt, nameof(job));
        string updatedAt = SqliteStoreSupport.FormatTimestamp(job.UpdatedAt, nameof(job));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                0,
                0
            );
            """;
        SqliteStoreSupport.AddText(command, "$id", job.Id.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", job.ProjectId.ToString());
        SqliteStoreSupport.AddText(command, "$kind", job.Kind);
        SqliteStoreSupport.AddText(command, "$state", FormatJobState(job.State));
        SqliteStoreSupport.AddText(command, "$created_at", createdAt);
        SqliteStoreSupport.AddText(command, "$updated_at", updatedAt);
        EnsureSingleMutation(command, "job");
        transaction.Commit();
    }

    public StoredJob? GetJob(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = CreateJobSelect(connection, transaction: null, jobId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredJob stored = ReadStoredJob(reader);
        if (reader.Read() || stored.Job.Id != jobId)
        {
            throw SqliteStoreSupport.InvalidStoredData("job");
        }

        return stored;
    }

    public StoredJob StartAttempt(
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        JobAttempt runningAttempt,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(runningAttempt);
        ValidateExpectedRevision(expectedRevision);
        if (runningAttempt.JobId != jobId)
        {
            throw new ArgumentException(
                "The running attempt must belong to the supplied job.",
                nameof(runningAttempt));
        }

        if (runningAttempt.State != JobState.Running)
        {
            throw new ArgumentException(
                "A started attempt must be in the running state.",
                nameof(runningAttempt));
        }

        DateTimeOffset startedAt = runningAttempt.StartedAt
            ?? throw new ArgumentException(
                "A started attempt requires a start timestamp.",
                nameof(runningAttempt));
        if (updatedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "A job update timestamp cannot precede the attempt start.");
        }

        string storedUpdatedAt =
            SqliteStoreSupport.FormatTimestamp(updatedAt, nameof(updatedAt));
        string queuedAt = SqliteStoreSupport.FormatTimestamp(
            runningAttempt.QueuedAt,
            nameof(runningAttempt));
        string storedStartedAt =
            SqliteStoreSupport.FormatTimestamp(startedAt, nameof(runningAttempt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredJob current = ReadRequiredJob(connection, transaction, jobId);
        EnsureExpectedRevision(
            jobId,
            expectedRevision,
            current.Revision);
        JobStateTransitions.EnsureCanTransition(current.Job.State, JobState.Running);

        int nextAttemptNumber = checked(current.Job.CurrentAttemptNumber + 1);
        if (runningAttempt.SequenceNumber != nextAttemptNumber)
        {
            throw new ArgumentException(
                "A running attempt must use the next job-attempt sequence number.",
                nameof(runningAttempt));
        }

        if (runningAttempt.QueuedAt < current.Job.CreatedAt)
        {
            throw new ArgumentException(
                "A job attempt cannot be queued before its job was created.",
                nameof(runningAttempt));
        }

        EnsureTimestampDoesNotMoveBackwards(updatedAt, current.Job.UpdatedAt);
        InsertAttempt(
            connection,
            transaction,
            runningAttempt,
            queuedAt,
            storedStartedAt);

        int newRevision = checked(current.Revision + 1);
        UpdateJobState(
            connection,
            transaction,
            jobId,
            expectedRevision,
            newRevision,
            JobState.Running,
            nextAttemptNumber,
            storedUpdatedAt);
        StoredJob updated = ReadTransitionResult(
            connection,
            transaction,
            jobId,
            newRevision,
            JobState.Running,
            nextAttemptNumber,
            updatedAt);
        transaction.Commit();
        return updated;
    }

    public StoredJob CompleteAttempt(
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        JobAttempt completedAttempt,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(completedAttempt);
        ValidateExpectedRevision(expectedRevision);
        if (completedAttempt.JobId != jobId)
        {
            throw new ArgumentException(
                "The completed attempt must belong to the supplied job.",
                nameof(completedAttempt));
        }

        if (completedAttempt.State is not (
            JobState.Succeeded or
            JobState.Failed or
            JobState.Cancelled or
            JobState.Interrupted))
        {
            throw new ArgumentException(
                "A completed attempt must be in a terminal state.",
                nameof(completedAttempt));
        }

        DateTimeOffset completedAt = completedAttempt.CompletedAt
            ?? throw new ArgumentException(
                "A completed attempt requires a completion timestamp.",
                nameof(completedAttempt));
        if (updatedAt < completedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "A job update timestamp cannot precede attempt completion.");
        }

        string storedUpdatedAt =
            SqliteStoreSupport.FormatTimestamp(updatedAt, nameof(updatedAt));
        string storedCompletedAt =
            SqliteStoreSupport.FormatTimestamp(completedAt, nameof(completedAttempt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredJob current = ReadRequiredJob(connection, transaction, jobId);
        EnsureExpectedRevision(jobId, expectedRevision, current.Revision);
        JobStateTransitions.EnsureCanTransition(current.Job.State, completedAttempt.State);
        if (completedAttempt.SequenceNumber != current.Job.CurrentAttemptNumber)
        {
            throw new ArgumentException(
                "The completed attempt must be the job's current attempt.",
                nameof(completedAttempt));
        }

        JobAttempt storedAttempt = ReadRequiredAttempt(
            connection,
            transaction,
            jobId,
            current.Job.CurrentAttemptNumber);
        if (storedAttempt.State != JobState.Running ||
            storedAttempt.Id != completedAttempt.Id ||
            storedAttempt.SequenceNumber != completedAttempt.SequenceNumber ||
            storedAttempt.QueuedAt != completedAttempt.QueuedAt ||
            storedAttempt.StartedAt != completedAttempt.StartedAt)
        {
            throw new ArgumentException(
                "The completed attempt does not match the immutable running attempt.",
                nameof(completedAttempt));
        }

        JobAttemptStateTransitions.EnsureCanTransition(
            storedAttempt.State,
            completedAttempt.State);
        EnsureEvidenceDoesNotFollowCompletion(
            connection,
            transaction,
            completedAttempt.Id,
            completedAt);
        EnsureTimestampDoesNotMoveBackwards(updatedAt, current.Job.UpdatedAt);
        UpdateAttemptCompletion(
            connection,
            transaction,
            completedAttempt,
            storedCompletedAt);

        int newRevision = checked(current.Revision + 1);
        UpdateJobState(
            connection,
            transaction,
            jobId,
            expectedRevision,
            newRevision,
            completedAttempt.State,
            current.Job.CurrentAttemptNumber,
            storedUpdatedAt);
        StoredJob updated = ReadTransitionResult(
            connection,
            transaction,
            jobId,
            newRevision,
            completedAttempt.State,
            current.Job.CurrentAttemptNumber,
            updatedAt);
        transaction.Commit();
        return updated;
    }

    public int QueueRetry(
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ValidateExpectedRevision(expectedRevision);
        string storedUpdatedAt =
            SqliteStoreSupport.FormatTimestamp(updatedAt, nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredJob current = ReadRequiredJob(connection, transaction, jobId);
        EnsureExpectedRevision(jobId, expectedRevision, current.Revision);
        JobStateTransitions.EnsureCanTransition(current.Job.State, JobState.Queued);
        EnsureTimestampDoesNotMoveBackwards(updatedAt, current.Job.UpdatedAt);

        JobAttempt currentAttempt = ReadRequiredAttempt(
            connection,
            transaction,
            jobId,
            current.Job.CurrentAttemptNumber);
        if (currentAttempt.State != current.Job.State)
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }

        int newRevision = checked(current.Revision + 1);
        UpdateJobState(
            connection,
            transaction,
            jobId,
            expectedRevision,
            newRevision,
            JobState.Queued,
            current.Job.CurrentAttemptNumber,
            storedUpdatedAt);
        transaction.Commit();
        return newRevision;
    }

    public int CancelQueued(
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ValidateExpectedRevision(expectedRevision);
        string storedUpdatedAt =
            SqliteStoreSupport.FormatTimestamp(updatedAt, nameof(updatedAt));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredJob current = ReadRequiredJob(connection, transaction, jobId);
        EnsureExpectedRevision(jobId, expectedRevision, current.Revision);
        JobStateTransitions.EnsureCanTransition(current.Job.State, JobState.Cancelled);
        EnsureTimestampDoesNotMoveBackwards(updatedAt, current.Job.UpdatedAt);

        if (current.Job.CurrentAttemptNumber > 0)
        {
            JobAttempt currentAttempt = ReadRequiredAttempt(
                connection,
                transaction,
                jobId,
                current.Job.CurrentAttemptNumber);
            if (currentAttempt.State is not (JobState.Failed or JobState.Interrupted))
            {
                throw SqliteStoreSupport.InvalidStoredData("job aggregate");
            }
        }

        int newRevision = checked(current.Revision + 1);
        UpdateJobState(
            connection,
            transaction,
            jobId,
            expectedRevision,
            newRevision,
            JobState.Cancelled,
            current.Job.CurrentAttemptNumber,
            storedUpdatedAt);
        transaction.Commit();
        return newRevision;
    }

    public void AppendProgress(JobProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string recordedAt = SqliteStoreSupport.FormatTimestamp(
            progress.RecordedAt,
            nameof(progress));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        JobAttempt currentAttempt = ValidateCurrentRunningAttempt(
            connection,
            transaction,
            progress.JobId,
            progress.JobAttemptId,
            progress.SequenceNumber,
            progress.RecordedAt,
            nameof(progress));
        if (currentAttempt.StartedAt is null)
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
            """;
        SqliteStoreSupport.AddText(command, "$id", progress.Id.ToString());
        SqliteStoreSupport.AddText(command, "$job_id", progress.JobId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$job_attempt_id",
            progress.JobAttemptId.ToString());
        SqliteStoreSupport.AddInteger(command, "$sequence_number", progress.SequenceNumber);
        SqliteStoreSupport.AddText(command, "$recorded_at", recordedAt);
        SqliteStoreSupport.AddInteger(command, "$completed_units", progress.CompletedUnits);
        AddNullableInteger(command, "$total_units", progress.TotalUnits);
        SqliteStoreSupport.AddNullableText(command, "$phase", progress.Phase);
        SqliteStoreSupport.AddNullableText(command, "$message", progress.Message);
        EnsureSingleMutation(command, "job progress");
        transaction.Commit();
    }

    public void AppendHeartbeat(JobHeartbeat heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        string recordedAt = SqliteStoreSupport.FormatTimestamp(
            heartbeat.RecordedAt,
            nameof(heartbeat));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        _ = ValidateCurrentRunningAttempt(
            connection,
            transaction,
            heartbeat.JobId,
            heartbeat.JobAttemptId,
            heartbeat.SequenceNumber,
            heartbeat.RecordedAt,
            nameof(heartbeat));

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
            """;
        SqliteStoreSupport.AddText(command, "$id", heartbeat.Id.ToString());
        SqliteStoreSupport.AddText(command, "$job_id", heartbeat.JobId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$job_attempt_id",
            heartbeat.JobAttemptId.ToString());
        SqliteStoreSupport.AddInteger(command, "$sequence_number", heartbeat.SequenceNumber);
        SqliteStoreSupport.AddText(command, "$recorded_at", recordedAt);
        EnsureSingleMutation(command, "job heartbeat");
        transaction.Commit();
    }

    public StoredJobAggregate? GetJobAggregate(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        StoredJob? storedJob = ReadOptionalJob(connection, transaction, jobId);
        if (storedJob is null)
        {
            transaction.Commit();
            return null;
        }

        IReadOnlyList<JobAttempt> attempts = ReadAttempts(connection, transaction, jobId);
        IReadOnlyList<JobProgress> progress = ReadProgress(connection, transaction, jobId);
        IReadOnlyList<JobHeartbeat> heartbeats = ReadHeartbeats(connection, transaction, jobId);
        ValidateAggregate(storedJob.Job, attempts, progress, heartbeats);
        transaction.Commit();
        return new StoredJobAggregate(
            storedJob.Job,
            storedJob.Revision,
            attempts,
            progress,
            heartbeats);
    }

    private static SqliteCommand CreateJobSelect(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<JobIdTag> jobId)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                project_id,
                kind,
                state,
                created_at,
                updated_at,
                current_attempt_number,
                revision
            FROM jobs
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", jobId.ToString());
        return command;
    }

    private static StoredJob ReadStoredJob(SqliteDataReader reader)
    {
        const string entityKind = "job";
        EntityId<JobIdTag> id =
            SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 0, entityKind);
        EntityId<Core.Projects.ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<Core.Projects.ProjectIdTag>(reader, 1, entityKind);
        string kind = SqliteStoreSupport.ReadString(reader, 2, entityKind);
        JobState state = ParseJobState(
            SqliteStoreSupport.ReadString(reader, 3, entityKind),
            entityKind);
        DateTimeOffset createdAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        DateTimeOffset updatedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 5, entityKind);
        int currentAttemptNumber =
            SqliteStoreSupport.ReadInt32(reader, 6, entityKind);
        int revision = SqliteStoreSupport.ReadInt32(reader, 7, entityKind);
        if (revision < 0)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        try
        {
            return new StoredJob(
                new Job(
                    id,
                    projectId,
                    kind,
                    state,
                    createdAt,
                    updatedAt,
                    currentAttemptNumber),
                revision);
        }
        catch (ArgumentException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static StoredJob? ReadOptionalJob(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId)
    {
        using SqliteCommand command = CreateJobSelect(connection, transaction, jobId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        StoredJob stored = ReadStoredJob(reader);
        if (reader.Read() || stored.Job.Id != jobId)
        {
            throw SqliteStoreSupport.InvalidStoredData("job");
        }

        return stored;
    }

    private static StoredJob ReadRequiredJob(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId)
    {
        return ReadOptionalJob(connection, transaction, jobId)
            ?? throw new InvalidOperationException("The job does not exist.");
    }

    private static void InsertAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobAttempt attempt,
        string queuedAt,
        string startedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                NULL,
                NULL
            );
            """;
        SqliteStoreSupport.AddText(command, "$id", attempt.Id.ToString());
        SqliteStoreSupport.AddText(command, "$job_id", attempt.JobId.ToString());
        SqliteStoreSupport.AddInteger(command, "$sequence_number", attempt.SequenceNumber);
        SqliteStoreSupport.AddText(command, "$state", FormatJobState(attempt.State));
        SqliteStoreSupport.AddText(command, "$queued_at", queuedAt);
        SqliteStoreSupport.AddText(command, "$started_at", startedAt);
        EnsureSingleMutation(command, "job attempt");
    }

    private static void UpdateAttemptCompletion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobAttempt attempt,
        string completedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_attempts
            SET state = $state,
                completed_at = $completed_at,
                error_code = $error_code
            WHERE id = $id
              AND job_id = $job_id
              AND sequence_number = $sequence_number
              AND state = 'running';
            """;
        SqliteStoreSupport.AddText(command, "$state", FormatJobState(attempt.State));
        SqliteStoreSupport.AddText(command, "$completed_at", completedAt);
        SqliteStoreSupport.AddNullableText(command, "$error_code", attempt.ErrorCode);
        SqliteStoreSupport.AddText(command, "$id", attempt.Id.ToString());
        SqliteStoreSupport.AddText(command, "$job_id", attempt.JobId.ToString());
        SqliteStoreSupport.AddInteger(command, "$sequence_number", attempt.SequenceNumber);
        EnsureSingleMutation(command, "job attempt");
    }

    private static void UpdateJobState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        int newRevision,
        JobState state,
        int currentAttemptNumber,
        string updatedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = $state,
                updated_at = $updated_at,
                current_attempt_number = $current_attempt_number,
                revision = $new_revision
            WHERE id = $id AND revision = $expected_revision;
            """;
        SqliteStoreSupport.AddText(command, "$state", FormatJobState(state));
        SqliteStoreSupport.AddText(command, "$updated_at", updatedAt);
        SqliteStoreSupport.AddInteger(
            command,
            "$current_attempt_number",
            currentAttemptNumber);
        SqliteStoreSupport.AddInteger(command, "$new_revision", newRevision);
        SqliteStoreSupport.AddText(command, "$id", jobId.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected_revision", expectedRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw SqliteStoreSupport.InvalidStoredData("job");
        }
    }

    private static JobAttempt ValidateCurrentRunningAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> attemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt,
        string parameterName)
    {
        StoredJob current = ReadRequiredJob(connection, transaction, jobId);
        if (current.Job.State != JobState.Running ||
            current.Job.CurrentAttemptNumber != sequenceNumber)
        {
            throw new InvalidOperationException(
                "Progress and heartbeats require the current running job attempt.");
        }

        JobAttempt attempt = ReadRequiredAttempt(
            connection,
            transaction,
            jobId,
            sequenceNumber);
        if (attempt.State != JobState.Running || attempt.Id != attemptId)
        {
            throw new InvalidOperationException(
                "Progress and heartbeats require the current running job attempt.");
        }

        if (attempt.StartedAt is not DateTimeOffset startedAt)
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }

        if (recordedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A progress or heartbeat timestamp cannot precede attempt start.");
        }

        return attempt;
    }

    private static JobAttempt ReadRequiredAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId,
        int sequenceNumber)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                job_id,
                sequence_number,
                state,
                queued_at,
                started_at,
                completed_at,
                error_code
            FROM job_attempts
            WHERE job_id = $job_id AND sequence_number = $sequence_number;
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        SqliteStoreSupport.AddInteger(command, "$sequence_number", sequenceNumber);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }

        JobAttempt attempt = ReadAttempt(reader);
        if (reader.Read() ||
            attempt.JobId != jobId ||
            attempt.SequenceNumber != sequenceNumber)
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }

        return attempt;
    }

    private static IReadOnlyList<JobAttempt> ReadAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                job_id,
                sequence_number,
                state,
                queued_at,
                started_at,
                completed_at,
                error_code
            FROM job_attempts
            WHERE job_id = $job_id;
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var attempts = new List<JobAttempt>();
        while (reader.Read())
        {
            attempts.Add(ReadAttempt(reader));
        }

        return Array.AsReadOnly(
            attempts
                .OrderBy(static attempt => attempt.SequenceNumber)
                .ThenBy(static attempt => attempt.Id.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private static JobAttempt ReadAttempt(SqliteDataReader reader)
    {
        const string entityKind = "job attempt";
        EntityId<JobAttemptIdTag> id =
            SqliteStoreSupport.ReadEntityId<JobAttemptIdTag>(reader, 0, entityKind);
        EntityId<JobIdTag> jobId =
            SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 1, entityKind);
        int sequenceNumber = SqliteStoreSupport.ReadInt32(reader, 2, entityKind);
        JobState state = ParseJobState(
            SqliteStoreSupport.ReadString(reader, 3, entityKind),
            entityKind);
        DateTimeOffset queuedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        DateTimeOffset? startedAt =
            SqliteStoreSupport.ReadNullableTimestamp(reader, 5, entityKind);
        DateTimeOffset? completedAt =
            SqliteStoreSupport.ReadNullableTimestamp(reader, 6, entityKind);
        string? errorCode =
            SqliteStoreSupport.ReadNullableString(reader, 7, entityKind);
        try
        {
            return new JobAttempt(
                id,
                jobId,
                sequenceNumber,
                state,
                queuedAt,
                startedAt,
                completedAt,
                errorCode);
        }
        catch (ArgumentException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static IReadOnlyList<JobProgress> ReadProgress(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                job_id,
                job_attempt_id,
                sequence_number,
                recorded_at,
                completed_units,
                total_units,
                phase,
                message
            FROM job_progress
            WHERE job_id = $job_id;
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var progress = new List<JobProgress>();
        while (reader.Read())
        {
            progress.Add(ReadProgressItem(reader));
        }

        return Array.AsReadOnly(
            progress
                .OrderBy(static item => item.RecordedAt)
                .ThenBy(static item => item.Id.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private static JobProgress ReadProgressItem(SqliteDataReader reader)
    {
        const string entityKind = "job progress";
        EntityId<JobProgressIdTag> id =
            SqliteStoreSupport.ReadEntityId<JobProgressIdTag>(reader, 0, entityKind);
        EntityId<JobIdTag> jobId =
            SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 1, entityKind);
        EntityId<JobAttemptIdTag> attemptId =
            SqliteStoreSupport.ReadEntityId<JobAttemptIdTag>(reader, 2, entityKind);
        int sequenceNumber = SqliteStoreSupport.ReadInt32(reader, 3, entityKind);
        DateTimeOffset recordedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        long completedUnits =
            SqliteStoreSupport.ReadInt64(reader, 5, entityKind);
        long? totalUnits = reader.IsDBNull(6)
            ? null
            : SqliteStoreSupport.ReadInt64(reader, 6, entityKind);
        string? phase =
            SqliteStoreSupport.ReadNullableString(reader, 7, entityKind);
        string? message =
            SqliteStoreSupport.ReadNullableString(reader, 8, entityKind);
        try
        {
            return new JobProgress(
                id,
                jobId,
                attemptId,
                sequenceNumber,
                recordedAt,
                completedUnits,
                totalUnits,
                phase,
                message);
        }
        catch (ArgumentException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static IReadOnlyList<JobHeartbeat> ReadHeartbeats(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                job_id,
                job_attempt_id,
                sequence_number,
                recorded_at
            FROM job_heartbeats
            WHERE job_id = $job_id;
            """;
        SqliteStoreSupport.AddText(command, "$job_id", jobId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var heartbeats = new List<JobHeartbeat>();
        while (reader.Read())
        {
            heartbeats.Add(ReadHeartbeat(reader));
        }

        return Array.AsReadOnly(
            heartbeats
                .OrderBy(static item => item.RecordedAt)
                .ThenBy(static item => item.Id.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private static JobHeartbeat ReadHeartbeat(SqliteDataReader reader)
    {
        const string entityKind = "job heartbeat";
        EntityId<JobHeartbeatIdTag> id =
            SqliteStoreSupport.ReadEntityId<JobHeartbeatIdTag>(reader, 0, entityKind);
        EntityId<JobIdTag> jobId =
            SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 1, entityKind);
        EntityId<JobAttemptIdTag> attemptId =
            SqliteStoreSupport.ReadEntityId<JobAttemptIdTag>(reader, 2, entityKind);
        int sequenceNumber = SqliteStoreSupport.ReadInt32(reader, 3, entityKind);
        DateTimeOffset recordedAt =
            SqliteStoreSupport.ReadTimestamp(reader, 4, entityKind);
        try
        {
            return new JobHeartbeat(id, jobId, attemptId, sequenceNumber, recordedAt);
        }
        catch (ArgumentException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static void ValidateAggregate(
        Job job,
        IReadOnlyList<JobAttempt> attempts,
        IReadOnlyList<JobProgress> progress,
        IReadOnlyList<JobHeartbeat> heartbeats)
    {
        if (attempts.Count != job.CurrentAttemptNumber)
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }

        for (int index = 0; index < attempts.Count; index++)
        {
            JobAttempt attempt = attempts[index];
            if (attempt.JobId != job.Id || attempt.SequenceNumber != index + 1)
            {
                throw SqliteStoreSupport.InvalidStoredData("job aggregate");
            }

            if (index < attempts.Count - 1 &&
                attempt.State is not (JobState.Failed or JobState.Interrupted))
            {
                throw SqliteStoreSupport.InvalidStoredData("job aggregate");
            }
        }

        ValidateCurrentAttemptState(job, attempts);
        Dictionary<int, JobAttempt> attemptsBySequence = attempts.ToDictionary(
            static attempt => attempt.SequenceNumber);
        foreach (JobProgress item in progress)
        {
            ValidateRecordedItem(
                job,
                attemptsBySequence,
                item.JobId,
                item.JobAttemptId,
                item.SequenceNumber,
                item.RecordedAt);
        }

        foreach (JobHeartbeat item in heartbeats)
        {
            ValidateRecordedItem(
                job,
                attemptsBySequence,
                item.JobId,
                item.JobAttemptId,
                item.SequenceNumber,
                item.RecordedAt);
        }
    }

    private static StoredJob ReadTransitionResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobIdTag> jobId,
        int expectedRevision,
        JobState expectedState,
        int expectedAttemptNumber,
        DateTimeOffset expectedUpdatedAt)
    {
        StoredJob updated = ReadRequiredJob(connection, transaction, jobId);
        if (updated.Revision != expectedRevision ||
            updated.Job.State != expectedState ||
            updated.Job.CurrentAttemptNumber != expectedAttemptNumber ||
            updated.Job.UpdatedAt != expectedUpdatedAt)
        {
            throw SqliteStoreSupport.InvalidStoredData("job");
        }

        return updated;
    }

    private static void ValidateCurrentAttemptState(
        Job job,
        IReadOnlyList<JobAttempt> attempts)
    {
        if (job.CurrentAttemptNumber == 0)
        {
            if (job.State is not (JobState.Queued or JobState.Cancelled))
            {
                throw SqliteStoreSupport.InvalidStoredData("job aggregate");
            }

            return;
        }

        JobState attemptState = attempts[^1].State;
        bool valid = job.State switch
        {
            JobState.Queued => attemptState is JobState.Failed or JobState.Interrupted,
            JobState.Running => attemptState == JobState.Running,
            JobState.Succeeded => attemptState == JobState.Succeeded,
            JobState.Failed => attemptState == JobState.Failed,
            JobState.Interrupted => attemptState == JobState.Interrupted,
            JobState.Cancelled => attemptState is
                JobState.Cancelled or JobState.Failed or JobState.Interrupted,
            _ => false,
        };
        if (!valid)
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }
    }

    private static void ValidateRecordedItem(
        Job job,
        IReadOnlyDictionary<int, JobAttempt> attemptsBySequence,
        EntityId<JobIdTag> itemJobId,
        EntityId<JobAttemptIdTag> itemAttemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt)
    {
        if (itemJobId != job.Id ||
            !attemptsBySequence.TryGetValue(sequenceNumber, out JobAttempt? attempt) ||
            attempt.Id != itemAttemptId ||
            attempt.StartedAt is not DateTimeOffset startedAt ||
            recordedAt < startedAt ||
            (attempt.CompletedAt is DateTimeOffset completedAt
                && recordedAt > completedAt))
        {
            throw SqliteStoreSupport.InvalidStoredData("job aggregate");
        }
    }

    private static void EnsureEvidenceDoesNotFollowCompletion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<JobAttemptIdTag> attemptId,
        DateTimeOffset completedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT recorded_at
            FROM (
                SELECT recorded_at
                FROM job_progress
                WHERE job_attempt_id = $attempt_id
                UNION ALL
                SELECT recorded_at
                FROM job_heartbeats
                WHERE job_attempt_id = $attempt_id
            )
            ORDER BY recorded_at DESC
            LIMIT 1;
            """;
        SqliteStoreSupport.AddText(command, "$attempt_id", attemptId.ToString());
        object? value = command.ExecuteScalar();
        if (value is null)
        {
            return;
        }

        if (value is not string timestamp ||
            !DateTimeOffset.TryParseExact(
                timestamp,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset latestRecordedAt))
        {
            throw SqliteStoreSupport.InvalidStoredData("job evidence");
        }

        if (latestRecordedAt > completedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "An attempt cannot complete before its latest progress or heartbeat.");
        }
    }

    private static string FormatJobState(JobState state) =>
        state switch
        {
            JobState.Queued => "queued",
            JobState.Running => "running",
            JobState.Succeeded => "succeeded",
            JobState.Failed => "failed",
            JobState.Cancelled => "cancelled",
            JobState.Interrupted => "interrupted",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown job state."),
        };

    private static JobState ParseJobState(string value, string entityKind) =>
        value switch
        {
            "queued" => JobState.Queued,
            "running" => JobState.Running,
            "succeeded" => JobState.Succeeded,
            "failed" => JobState.Failed,
            "cancelled" => JobState.Cancelled,
            "interrupted" => JobState.Interrupted,
            _ => throw SqliteStoreSupport.InvalidStoredData(entityKind),
        };

    private static void AddNullableInteger(
        SqliteCommand command,
        string name,
        long? value)
    {
        command.Parameters.Add(name, SqliteType.Integer).Value = value ?? (object)DBNull.Value;
    }

    private static void ValidateExpectedRevision(int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected revision must not be negative.");
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
                "job",
                jobId.ToString(),
                expectedRevision,
                actualRevision);
        }
    }

    private static void EnsureTimestampDoesNotMoveBackwards(
        DateTimeOffset updatedAt,
        DateTimeOffset currentUpdatedAt)
    {
        if (updatedAt < currentUpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "A job update timestamp must not move backwards.");
        }
    }

    private static void EnsureSingleMutation(SqliteCommand command, string entityKind)
    {
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"The {entityKind} was not stored.");
        }
    }
}