using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Audit;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoredReviewRecord
{
    internal StoredReviewRecord(
        EntityId<ReviewRecordIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        ReviewState state,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset recordedAt,
        string reason)
    {
        Id = id;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        State = state;
        ActorId = actorId;
        ActorRole = actorRole;
        RecordedAt = recordedAt;
        Reason = reason;
    }

    public EntityId<ReviewRecordIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public ReviewState State { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset RecordedAt { get; }

    public string Reason { get; }
}

public sealed class StoredReviewComment
{
    internal StoredReviewComment(
        EntityId<ReviewCommentIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        RationalTime outputTime,
        EntityId<SourceSpanIdTag> sourceSpanId,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset createdAt,
        string text)
    {
        Id = id;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        OutputTime = outputTime;
        SourceSpanId = sourceSpanId;
        ActorId = actorId;
        ActorRole = actorRole;
        CreatedAt = createdAt;
        Text = text;
    }

    public EntityId<ReviewCommentIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public RationalTime OutputTime { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Text { get; }
}

public sealed class StoredApproval
{
    internal StoredApproval(
        EntityId<ApprovalIdTag> id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset approvedAt,
        string reason)
    {
        Id = id;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        ActorId = actorId;
        ActorRole = actorRole;
        ApprovedAt = approvedAt;
        Reason = reason;
    }

    public EntityId<ApprovalIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset ApprovedAt { get; }

    public string Reason { get; }
}

public sealed class StoredVersionEvidence
{
    internal StoredVersionEvidence(
        EntityId<CompositionVersionIdTag> compositionVersionId,
        EntityId<ProjectIdTag> projectId,
        ContentDigest compositionVersionDigest,
        IReadOnlyList<StoredReviewRecord> reviewRecords,
        IReadOnlyList<StoredReviewComment> reviewComments,
        IReadOnlyList<StoredApproval> approvals)
    {
        CompositionVersionId = compositionVersionId;
        ProjectId = projectId;
        CompositionVersionDigest = compositionVersionDigest;
        ReviewRecords = Array.AsReadOnly(reviewRecords.ToArray());
        ReviewComments = Array.AsReadOnly(reviewComments.ToArray());
        Approvals = Array.AsReadOnly(approvals.ToArray());
    }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public IReadOnlyList<StoredReviewRecord> ReviewRecords { get; }

    public IReadOnlyList<StoredReviewComment> ReviewComments { get; }

    public IReadOnlyList<StoredApproval> Approvals { get; }
}

public sealed class StoredAuditEvent
{
    internal StoredAuditEvent(
        EntityId<AuditEventIdTag> id,
        EntityId<ProjectIdTag> projectId,
        AuditEventKind kind,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset occurredAt,
        string reason,
        EntityId<CompositionVersionIdTag>? compositionVersionId,
        ContentDigest? compositionVersionDigest)
    {
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        ActorId = actorId;
        ActorRole = actorRole;
        OccurredAt = occurredAt;
        Reason = reason;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
    }

    public EntityId<AuditEventIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public AuditEventKind Kind { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset OccurredAt { get; }

    public string Reason { get; }

    public EntityId<CompositionVersionIdTag>? CompositionVersionId { get; }

    public ContentDigest? CompositionVersionDigest { get; }
}

public sealed class ReviewStore
{
    private readonly string _databasePath;
    private readonly AuditLog _auditLog;

    public ReviewStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
        _auditLog = new AuditLog(_databasePath);
    }

    public void AppendReviewRecord(ReviewRecord reviewRecord)
    {
        ArgumentNullException.ThrowIfNull(reviewRecord);
        string recordedAt = SqliteStoreSupport.FormatTimestamp(
            reviewRecord.RecordedAt,
            nameof(reviewRecord));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                $composition_version_id,
                $composition_version_digest,
                $state,
                $actor_id,
                $actor_role,
                $recorded_at,
                $reason
            );
            """;
        AddVersionEvidenceBinding(
            command,
            reviewRecord.Id.ToString(),
            reviewRecord.ProjectId,
            reviewRecord.CompositionVersionId,
            reviewRecord.CompositionVersionDigest);
        SqliteStoreSupport.AddText(command, "$state", reviewRecord.State.ToString());
        SqliteStoreSupport.AddText(command, "$actor_id", reviewRecord.ActorId);
        SqliteStoreSupport.AddText(command, "$actor_role", reviewRecord.ActorRole.ToString());
        SqliteStoreSupport.AddText(command, "$recorded_at", recordedAt);
        SqliteStoreSupport.AddText(command, "$reason", reviewRecord.Reason);
        EnsureSingleMutation(command, "review record");
        transaction.Commit();
    }

    public void AppendReviewComment(ReviewComment reviewComment)
    {
        ArgumentNullException.ThrowIfNull(reviewComment);
        string createdAt = SqliteStoreSupport.FormatTimestamp(
            reviewComment.CreatedAt,
            nameof(reviewComment));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                $composition_version_id,
                $composition_version_digest,
                $output_value,
                $output_rate,
                $source_span_id,
                $actor_id,
                $actor_role,
                $created_at,
                $text
            );
            """;
        AddVersionEvidenceBinding(
            command,
            reviewComment.Id.ToString(),
            reviewComment.ProjectId,
            reviewComment.CompositionVersionId,
            reviewComment.CompositionVersionDigest);
        SqliteStoreSupport.AddInteger(command, "$output_value", reviewComment.OutputTime.Value);
        SqliteStoreSupport.AddInteger(command, "$output_rate", reviewComment.OutputTime.Rate);
        SqliteStoreSupport.AddText(
            command,
            "$source_span_id",
            reviewComment.SourceSpanId.ToString());
        SqliteStoreSupport.AddText(command, "$actor_id", reviewComment.ActorId);
        SqliteStoreSupport.AddText(command, "$actor_role", reviewComment.ActorRole.ToString());
        SqliteStoreSupport.AddText(command, "$created_at", createdAt);
        SqliteStoreSupport.AddText(command, "$text", reviewComment.Text);
        EnsureSingleMutation(command, "review comment");
        transaction.Commit();
    }

    public void AppendApproval(Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        string approvedAt = SqliteStoreSupport.FormatTimestamp(
            approval.ApprovedAt,
            nameof(approval));

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                $composition_version_id,
                $composition_version_digest,
                $actor_id,
                $actor_role,
                $approved_at,
                $reason
            );
            """;
        AddVersionEvidenceBinding(
            command,
            approval.Id.ToString(),
            approval.ProjectId,
            approval.CompositionVersionId,
            approval.CompositionVersionDigest);
        SqliteStoreSupport.AddText(command, "$actor_id", approval.ActorId);
        SqliteStoreSupport.AddText(command, "$actor_role", approval.ActorRole.ToString());
        SqliteStoreSupport.AddText(command, "$approved_at", approvedAt);
        SqliteStoreSupport.AddText(command, "$reason", approval.Reason);
        EnsureSingleMutation(command, "approval");
        transaction.Commit();
    }

    public void AppendAuditEvent(AuditEvent auditEvent)
    {
        _auditLog.Append(auditEvent);
    }

    public StoredVersionEvidence? GetVersionEvidence(
        EntityId<CompositionVersionIdTag> compositionVersionId)
    {
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        StoredVersionBinding? binding = ReadVersionBinding(
            connection,
            transaction,
            compositionVersionId);
        if (binding is null)
        {
            transaction.Commit();
            return null;
        }

        IReadOnlyList<StoredReviewRecord> records = ReadReviewRecords(
            connection,
            transaction,
            binding);
        IReadOnlyList<StoredReviewComment> comments = ReadReviewComments(
            connection,
            transaction,
            binding);
        IReadOnlyList<StoredApproval> approvals = ReadApprovals(
            connection,
            transaction,
            binding);
        transaction.Commit();
        return new StoredVersionEvidence(
            binding.CompositionVersionId,
            binding.ProjectId,
            binding.CompositionVersionDigest,
            records,
            comments,
            approvals);
    }

    public IReadOnlyList<StoredAuditEvent> GetProjectAuditEvents(
        EntityId<ProjectIdTag> projectId)
    {
        return _auditLog.GetProjectEvents(projectId);
    }

    private static void AddVersionEvidenceBinding(
        SqliteCommand command,
        string id,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest)
    {
        SqliteStoreSupport.AddText(command, "$id", id);
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            compositionVersionId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_digest",
            compositionVersionDigest.Hex);
    }

    private static StoredVersionBinding? ReadVersionBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<CompositionVersionIdTag> compositionVersionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, project_id, content_digest
            FROM composition_versions
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", compositionVersionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var binding = new StoredVersionBinding(
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(
                reader,
                0,
                "composition version"),
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(
                reader,
                1,
                "composition version"),
            SqliteStoreSupport.ReadContentDigest(
                reader,
                2,
                "composition version"));
        if (reader.Read() || binding.CompositionVersionId != compositionVersionId)
        {
            throw SqliteStoreSupport.InvalidStoredData("composition version");
        }

        return binding;
    }

    private static IReadOnlyList<StoredReviewRecord> ReadReviewRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredVersionBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                project_id,
                composition_version_id,
                composition_version_digest,
                state,
                actor_id,
                actor_role,
                recorded_at,
                reason
            FROM review_records
            WHERE composition_version_id = $composition_version_id;
            """;
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            binding.CompositionVersionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<StoredReviewRecord>();
        while (reader.Read())
        {
            StoredReviewRecord record = ReadReviewRecord(reader);
            ValidateVersionBinding(
                binding,
                record.ProjectId,
                record.CompositionVersionId,
                record.CompositionVersionDigest,
                "review record");
            records.Add(record);
        }

        return Array.AsReadOnly(
            records
                .OrderBy(static record => record.RecordedAt)
                .ThenBy(static record => record.Id.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private static StoredReviewRecord ReadReviewRecord(SqliteDataReader reader)
    {
        const string entityKind = "review record";
        return new StoredReviewRecord(
            SqliteStoreSupport.ReadEntityId<ReviewRecordIdTag>(reader, 0, entityKind),
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, entityKind),
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 2, entityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 3, entityKind),
            ParseEnum<ReviewState>(
                SqliteStoreSupport.ReadString(reader, 4, entityKind),
                entityKind),
            SqliteStoreSupport.ReadString(reader, 5, entityKind),
            ParseEnum<ReviewActorRole>(
                SqliteStoreSupport.ReadString(reader, 6, entityKind),
                entityKind),
            SqliteStoreSupport.ReadTimestamp(reader, 7, entityKind),
            SqliteStoreSupport.ReadString(reader, 8, entityKind));
    }

    private static IReadOnlyList<StoredReviewComment> ReadReviewComments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredVersionBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
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
            FROM review_comments
            WHERE composition_version_id = $composition_version_id;
            """;
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            binding.CompositionVersionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var comments = new List<StoredReviewComment>();
        while (reader.Read())
        {
            StoredReviewComment comment = ReadReviewComment(reader);
            ValidateVersionBinding(
                binding,
                comment.ProjectId,
                comment.CompositionVersionId,
                comment.CompositionVersionDigest,
                "review comment");
            comments.Add(comment);
        }

        return Array.AsReadOnly(
            comments
                .OrderBy(static comment => comment.CreatedAt)
                .ThenBy(static comment => comment.Id.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private static StoredReviewComment ReadReviewComment(SqliteDataReader reader)
    {
        const string entityKind = "review comment";
        long outputValue = SqliteStoreSupport.ReadInt64(reader, 4, entityKind);
        long outputRate = SqliteStoreSupport.ReadInt64(reader, 5, entityKind);
        RationalTime outputTime;
        try
        {
            outputTime = new RationalTime(outputValue, outputRate);
        }
        catch (ArgumentException)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        if (outputTime.Value != outputValue || outputTime.Rate != outputRate)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return new StoredReviewComment(
            SqliteStoreSupport.ReadEntityId<ReviewCommentIdTag>(reader, 0, entityKind),
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, entityKind),
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 2, entityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 3, entityKind),
            outputTime,
            SqliteStoreSupport.ReadEntityId<SourceSpanIdTag>(reader, 6, entityKind),
            SqliteStoreSupport.ReadString(reader, 7, entityKind),
            ParseEnum<ReviewActorRole>(
                SqliteStoreSupport.ReadString(reader, 8, entityKind),
                entityKind),
            SqliteStoreSupport.ReadTimestamp(reader, 9, entityKind),
            SqliteStoreSupport.ReadString(reader, 10, entityKind));
    }

    private static IReadOnlyList<StoredApproval> ReadApprovals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredVersionBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                project_id,
                composition_version_id,
                composition_version_digest,
                actor_id,
                actor_role,
                approved_at,
                reason
            FROM approvals
            WHERE composition_version_id = $composition_version_id;
            """;
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            binding.CompositionVersionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var approvals = new List<StoredApproval>();
        while (reader.Read())
        {
            StoredApproval approval = ReadApproval(reader);
            ValidateVersionBinding(
                binding,
                approval.ProjectId,
                approval.CompositionVersionId,
                approval.CompositionVersionDigest,
                "approval");
            approvals.Add(approval);
        }

        return Array.AsReadOnly(
            approvals
                .OrderBy(static approval => approval.ApprovedAt)
                .ThenBy(static approval => approval.Id.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private static StoredApproval ReadApproval(SqliteDataReader reader)
    {
        const string entityKind = "approval";
        ReviewActorRole actorRole = ParseEnum<ReviewActorRole>(
            SqliteStoreSupport.ReadString(reader, 5, entityKind),
            entityKind);
        if (actorRole is not (
            ReviewActorRole.ContentApprover or
            ReviewActorRole.BrandApprover or
            ReviewActorRole.RightsApprover))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return new StoredApproval(
            SqliteStoreSupport.ReadEntityId<ApprovalIdTag>(reader, 0, entityKind),
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, entityKind),
            SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 2, entityKind),
            SqliteStoreSupport.ReadContentDigest(reader, 3, entityKind),
            SqliteStoreSupport.ReadString(reader, 4, entityKind),
            actorRole,
            SqliteStoreSupport.ReadTimestamp(reader, 6, entityKind),
            SqliteStoreSupport.ReadString(reader, 7, entityKind));
    }

    private static TEnum ParseEnum<TEnum>(string value, string entityKind)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return parsed;
    }

    private static void ValidateVersionBinding(
        StoredVersionBinding expected,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        string entityKind)
    {
        if (projectId != expected.ProjectId ||
            compositionVersionId != expected.CompositionVersionId ||
            compositionVersionDigest != expected.CompositionVersionDigest)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }
    }

    private static void EnsureSingleMutation(SqliteCommand command, string entityKind)
    {
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"The {entityKind} was not stored.");
        }
    }

    private sealed record StoredVersionBinding(
        EntityId<CompositionVersionIdTag> CompositionVersionId,
        EntityId<ProjectIdTag> ProjectId,
        ContentDigest CompositionVersionDigest);
}