using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
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

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        InsertReviewRecord(connection, transaction, reviewRecord);
        transaction.Commit();
    }

    public void AppendReviewComment(ReviewComment reviewComment)
    {
        ArgumentNullException.ThrowIfNull(reviewComment);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        InsertReviewComment(connection, transaction, reviewComment);
        transaction.Commit();
    }

    public void AppendAuthorizedReviewComment(
        ReviewComment reviewComment,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        _ = AppendAuthorizedReviewCommentCore(
            reviewComment,
            authorizationRole,
            authorizationOperation,
            requireCurrentTarget: false);
    }

    public bool TryAppendAuthorizedCurrentReviewComment(
        ReviewComment reviewComment,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        return AppendAuthorizedReviewCommentCore(
            reviewComment,
            authorizationRole,
            authorizationOperation,
            requireCurrentTarget: true);
    }

    private bool AppendAuthorizedReviewCommentCore(
        ReviewComment reviewComment,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation,
        bool requireCurrentTarget)
    {
        ArgumentNullException.ThrowIfNull(reviewComment);
        ValidateCommentAuthorization(
            reviewComment.ActorRole,
            authorizationRole,
            authorizationOperation);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        RoleAssignmentStore.DemandCurrentAuthorization(
            connection,
            transaction,
            reviewComment.ActorId,
            authorizationRole,
            authorizationOperation);
        if (requireCurrentTarget)
        {
            StoredVersionBinding binding = ReadVersionBinding(
                connection,
                transaction,
                reviewComment.CompositionVersionId)
                ?? throw new InvalidOperationException(
                    "The review comment composition version does not exist.");
            ValidateIncomingVersionBinding(binding, reviewComment);
            if (!IsCurrentVersionTarget(connection, transaction, binding))
            {
                return false;
            }
        }

        InsertReviewComment(connection, transaction, reviewComment);
        transaction.Commit();
        return true;
    }

    public void AppendChangeRequest(
        ReviewComment reviewComment,
        ReviewRecord reviewRecord,
        ReviewState expectedCurrentState)
    {
        _ = AppendChangeRequestCore(
            reviewComment,
            reviewRecord,
            expectedCurrentState,
            authorizationRole: null,
            authorizationOperation: null,
            requireCurrentTarget: false);
    }

    public void AppendAuthorizedChangeRequest(
        ReviewComment reviewComment,
        ReviewRecord reviewRecord,
        ReviewState expectedCurrentState,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        _ = AppendChangeRequestCore(
            reviewComment,
            reviewRecord,
            expectedCurrentState,
            authorizationRole,
            authorizationOperation,
            requireCurrentTarget: false);
    }

    public bool TryAppendAuthorizedCurrentChangeRequest(
        ReviewComment reviewComment,
        ReviewRecord reviewRecord,
        ReviewState expectedCurrentState,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        return AppendChangeRequestCore(
            reviewComment,
            reviewRecord,
            expectedCurrentState,
            authorizationRole,
            authorizationOperation,
            requireCurrentTarget: true);
    }

    private bool AppendChangeRequestCore(
        ReviewComment reviewComment,
        ReviewRecord reviewRecord,
        ReviewState expectedCurrentState,
        AuthorizationRole? authorizationRole,
        AuthorizationOperation? authorizationOperation,
        bool requireCurrentTarget)
    {
        ArgumentNullException.ThrowIfNull(reviewComment);
        ArgumentNullException.ThrowIfNull(reviewRecord);
        ValidateChangeRequest(
            reviewComment,
            reviewRecord,
            expectedCurrentState,
            authorizationRole,
            authorizationOperation);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        if (authorizationRole is AuthorizationRole role
            && authorizationOperation is AuthorizationOperation operation)
        {
            RoleAssignmentStore.DemandCurrentAuthorization(
                connection,
                transaction,
                reviewRecord.ActorId,
                role,
                operation);
        }

        StoredVersionBinding binding = ReadVersionBinding(
            connection,
            transaction,
            reviewRecord.CompositionVersionId)
            ?? throw new InvalidOperationException(
                "The change request composition version does not exist.");
        ValidateIncomingVersionBinding(binding, reviewComment, reviewRecord);
        if (requireCurrentTarget &&
            !IsCurrentVersionTarget(connection, transaction, binding))
        {
            return false;
        }

        IReadOnlyList<StoredReviewRecord> persistedRecords = ReadReviewRecords(
            connection,
            transaction,
            binding);
        StoredReviewRecord current = GetCurrentReviewRecord(persistedRecords);
        if (current.State != expectedCurrentState)
        {
            throw new InvalidOperationException(
                "The persisted review state does not match the expected current state.");
        }

        if (reviewRecord.RecordedAt <= current.RecordedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reviewRecord),
                "A change request must be recorded after the current review state.");
        }

        InsertReviewComment(connection, transaction, reviewComment);
        InsertReviewRecord(connection, transaction, reviewRecord);
        transaction.Commit();
        return true;
    }

    public void AppendApproval(Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        InsertApproval(connection, transaction, approval);
        transaction.Commit();
    }

    public void AppendAuthorizedApproval(
        Approval approval,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        _ = AppendAuthorizedApprovalCore(
            approval,
            authorizationRole,
            authorizationOperation,
            requireCurrentTarget: false);
    }

    public bool TryAppendAuthorizedCurrentApproval(
        Approval approval,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        return AppendAuthorizedApprovalCore(
            approval,
            authorizationRole,
            authorizationOperation,
            requireCurrentTarget: true);
    }

    private bool AppendAuthorizedApprovalCore(
        Approval approval,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation,
        bool requireCurrentTarget)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ValidateApprovalAuthorization(
            approval.ActorRole,
            authorizationRole,
            authorizationOperation);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        RoleAssignmentStore.DemandCurrentAuthorization(
            connection,
            transaction,
            approval.ActorId,
            authorizationRole,
            authorizationOperation);
        StoredVersionBinding binding = ReadVersionBinding(
            connection,
            transaction,
            approval.CompositionVersionId)
            ?? throw new InvalidOperationException(
                "The approval composition version does not exist.");
        ValidateIncomingVersionBinding(binding, approval);
        if (requireCurrentTarget &&
            !IsCurrentVersionTarget(connection, transaction, binding))
        {
            return false;
        }

        InsertApproval(connection, transaction, approval);
        transaction.Commit();
        return true;
    }

    private static void InsertApproval(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Approval approval)
    {
        string approvedAt = SqliteStoreSupport.FormatTimestamp(
            approval.ApprovedAt,
            nameof(approval));
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

    private static void InsertReviewRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReviewRecord reviewRecord)
    {
        string recordedAt = SqliteStoreSupport.FormatTimestamp(
            reviewRecord.RecordedAt,
            nameof(reviewRecord));
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
    }

    private static void InsertReviewComment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReviewComment reviewComment)
    {
        string createdAt = SqliteStoreSupport.FormatTimestamp(
            reviewComment.CreatedAt,
            nameof(reviewComment));
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
    }

    private static void ValidateChangeRequest(
        ReviewComment reviewComment,
        ReviewRecord reviewRecord,
        ReviewState expectedCurrentState,
        AuthorizationRole? authorizationRole,
        AuthorizationOperation? authorizationOperation)
    {
        if (!Enum.IsDefined(expectedCurrentState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedCurrentState),
                expectedCurrentState,
                "Unknown expected review state.");
        }

        ReviewTransitionDecision decision = ReviewStateMachine.Evaluate(
            expectedCurrentState,
            ReviewTransitionRequest.RequestChanges());
        if (decision.Disposition != ReviewTransitionDisposition.Allowed
            || decision.To != reviewRecord.State
            || decision.RequiredReviewActorRole != reviewRecord.ActorRole
            || (authorizationRole is null) != (authorizationOperation is null)
            || authorizationRole is not null
                && (decision.RequiredAuthorizationRole != authorizationRole
                    || decision.RequiredAuthorizationOperation
                        != authorizationOperation))
        {
            throw new ArgumentException(
                "The review record does not represent the allowed change request.",
                nameof(reviewRecord));
        }

        if (reviewComment.ProjectId != reviewRecord.ProjectId
            || reviewComment.CompositionVersionId != reviewRecord.CompositionVersionId
            || reviewComment.CompositionVersionDigest
                != reviewRecord.CompositionVersionDigest
            || reviewComment.CreatedAt != reviewRecord.RecordedAt
            || !string.Equals(
                reviewComment.ActorId,
                reviewRecord.ActorId,
                StringComparison.Ordinal)
            || reviewComment.ActorRole != reviewRecord.ActorRole)
        {
            throw new ArgumentException(
                "The review comment and review record must have matching version, time, and actor bindings.",
                nameof(reviewRecord));
        }
    }

    private static void ValidateCommentAuthorization(
        ReviewActorRole reviewActorRole,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        bool isExactMapping = (reviewActorRole, authorizationRole,
            authorizationOperation) switch
        {
            (ReviewActorRole.Editor, AuthorizationRole.Editor,
                AuthorizationOperation.SubmitForReview) => true,
            (ReviewActorRole.PerformerReviewer,
                AuthorizationRole.PerformerReviewer,
                AuthorizationOperation.RecordPerformerReview) => true,
            (ReviewActorRole.ContentApprover,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview) => true,
            (ReviewActorRole.BrandApprover,
                AuthorizationRole.BrandApprover,
                AuthorizationOperation.RecordBrandReview) => true,
            (ReviewActorRole.RightsApprover,
                AuthorizationRole.RightsApprover,
                AuthorizationOperation.RecordRightsDecision) => true,
            _ => false,
        };

        if (!isExactMapping)
        {
            throw new ArgumentException(
                "The review actor role and authorization do not form an allowed comment-authoring mapping.",
                nameof(authorizationRole));
        }
    }

    private static void ValidateApprovalAuthorization(
        ReviewActorRole reviewActorRole,
        AuthorizationRole authorizationRole,
        AuthorizationOperation authorizationOperation)
    {
        bool isExactMapping = (reviewActorRole, authorizationRole,
            authorizationOperation) switch
        {
            (ReviewActorRole.ContentApprover,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview) => true,
            (ReviewActorRole.BrandApprover,
                AuthorizationRole.BrandApprover,
                AuthorizationOperation.RecordBrandReview) => true,
            (ReviewActorRole.RightsApprover,
                AuthorizationRole.RightsApprover,
                AuthorizationOperation.RecordRightsDecision) => true,
            _ => false,
        };

        if (!isExactMapping)
        {
            throw new ArgumentException(
                "The approval actor role and authorization do not form an allowed approval mapping.",
                nameof(authorizationRole));
        }
    }

    private static void ValidateIncomingVersionBinding(
        StoredVersionBinding binding,
        ReviewComment reviewComment)
    {
        if (reviewComment.ProjectId != binding.ProjectId
            || reviewComment.CompositionVersionId != binding.CompositionVersionId
            || reviewComment.CompositionVersionDigest != binding.CompositionVersionDigest)
        {
            throw new InvalidOperationException(
                "The review comment does not match the stored composition version.");
        }
    }

    private static void ValidateIncomingVersionBinding(
        StoredVersionBinding binding,
        ReviewComment reviewComment,
        ReviewRecord reviewRecord)
    {
        if (reviewComment.ProjectId != binding.ProjectId
            || reviewComment.CompositionVersionId != binding.CompositionVersionId
            || reviewComment.CompositionVersionDigest != binding.CompositionVersionDigest
            || reviewRecord.ProjectId != binding.ProjectId
            || reviewRecord.CompositionVersionId != binding.CompositionVersionId
            || reviewRecord.CompositionVersionDigest != binding.CompositionVersionDigest)
        {
            throw new InvalidOperationException(
                "The change request does not match the stored composition version.");
        }
    }

    private static void ValidateIncomingVersionBinding(
        StoredVersionBinding binding,
        Approval approval)
    {
        if (approval.ProjectId != binding.ProjectId
            || approval.CompositionVersionId != binding.CompositionVersionId
            || approval.CompositionVersionDigest != binding.CompositionVersionDigest)
        {
            throw new InvalidOperationException(
                "The approval does not match the stored composition version.");
        }
    }

    private static StoredReviewRecord GetCurrentReviewRecord(
        IReadOnlyList<StoredReviewRecord> persistedRecords)
    {
        if (persistedRecords.Count == 0)
        {
            throw new InvalidOperationException(
                "A change request requires a persisted current review state.");
        }

        StoredReviewRecord current = persistedRecords[^1];
        if (persistedRecords.Count > 1
            && persistedRecords[^2].RecordedAt == current.RecordedAt)
        {
            throw new InvalidOperationException(
                "The persisted review state order is ambiguous.");
        }

        return current;
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

    private static bool IsCurrentVersionTarget(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredVersionBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT base_version_id
            FROM working_copies
            WHERE project_id = $project_id;
            """;
        SqliteStoreSupport.AddText(
            command,
            "$project_id",
            binding.ProjectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "The review target project does not have a working copy.");
        }

        EntityId<CompositionVersionIdTag>? currentVersionId =
            SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(
                reader,
                0,
                "working copy");
        if (reader.Read())
        {
            throw SqliteStoreSupport.InvalidStoredData("working copy");
        }

        return currentVersionId == binding.CompositionVersionId;
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