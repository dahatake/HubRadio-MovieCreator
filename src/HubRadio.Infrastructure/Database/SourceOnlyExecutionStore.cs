using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class SourceOnlyExecutionIdTag { }

/// <summary>Immutable subtitle journal prefix captured when regeneration is accepted.</summary>
public sealed record SubtitleEditHeadReference(
    EntityId<CompositionVersionIdTag> RootVersionId, int Sequence, ContentDigest RecordDigest);

public enum SourceOnlyExecutionPhase
{
    Queued,
    Ingesting,
    Analyzing,
    Composing,
    Rendering,
    Evaluating,
    Delivering,
    Completed,
    Failed,
    Canceled,
    ReselectionRequired,
    ReconciliationRequired,
}

/// <summary>Native acceptance identity. Contains no source locator or evidence paths.</summary>
public sealed record SourceOnlyExecutionRequest(
    EntityId<SourceOnlyExecutionIdTag> ExecutionId,
    EntityId<ProjectIdTag> ProjectId,
    EntityId<ProjectCreationDraftIdTag> DraftId,
    EntityId<AssetIdTag> SourceCandidateId,
    EntityId<JobIdTag> RootJobId,
    EntityId<JobIdTag> IngestJobId,
    DefaultSettingsSnapshot SettingsSnapshot,
    string ActorSid,
    DateTimeOffset CreatedAt,
    EntityId<CompositionVersionIdTag>? ParentVersionId = null,
    ContentDigest? ParentDigest = null,
    IReadOnlyList<SubtitleEditHeadReference>? SubtitleEdits = null);

public sealed class StoredSourceOnlyExecution
{
    internal StoredSourceOnlyExecution(
        SourceOnlyExecutionRequest request, string requestJson, ContentDigest requestDigest,
        int settingsRevision, ContentDigest? finalizationDigest, ContentDigest? locatorDigest,
        int revision, SourceOnlyExecutionPhase phase, string? resultJson,
        ContentDigest? resultDigest, EntityId<CompositionVersionIdTag>? versionId,
        ContentDigest? versionDigest, string? failureCode, ContentDigest stateDigest,
        EntityId<SourceOnlyExecutionIdTag>? retryOfExecutionId = null, int? retryOfRevision = null,
        ContentDigest? retryOfStateDigest = null)
    {
        Request = request;
        RequestJson = requestJson;
        RequestDigest = requestDigest;
        SettingsRevision = settingsRevision;
        FinalizationDigest = finalizationDigest;
        LocatorDigest = locatorDigest;
        Revision = revision;
        Phase = phase;
        ResultJson = resultJson;
        ResultDigest = resultDigest;
        VersionId = versionId;
        VersionDigest = versionDigest;
        FailureCode = failureCode;
        StateDigest = stateDigest;
        RetryOfExecutionId = retryOfExecutionId;
        RetryOfRevision = retryOfRevision;
        RetryOfStateDigest = retryOfStateDigest;
    }

    public SourceOnlyExecutionRequest Request { get; }
    public ContentDigest RequestDigest { get; }
    public int SettingsRevision { get; }
    public int Revision { get; }
    public SourceOnlyExecutionPhase Phase { get; }
    // Opaque native result: callers must type-parse and project a path-free Web DTO.
    [JsonIgnore]
    public string? ResultJson { get; }
    public ContentDigest? ResultDigest { get; }
    public EntityId<CompositionVersionIdTag>? VersionId { get; }
    public ContentDigest? VersionDigest { get; }
    public string? FailureCode { get; }
    public ContentDigest StateDigest { get; }
    public EntityId<SourceOnlyExecutionIdTag>? RetryOfExecutionId { get; }
    public int? RetryOfRevision { get; }
    public ContentDigest? RetryOfStateDigest { get; }
    internal string RequestJson { get; }
    internal ContentDigest? FinalizationDigest { get; }
    internal ContentDigest? LocatorDigest { get; }
}

/// <summary>
/// Durable native acceptance/state only; it does not run work or mark Jobs successful.
/// The coordinator owns Job attempts and actual result production. Use an ACL-protected
/// workspace database. Locator bytes, when supplied, must already be DPAPI-protected by
/// the native caller; this store verifies bounds and integrity, not encryption provenance.
/// </summary>
public sealed class SourceOnlyExecutionStore
{
    public const string RootJobKind = "source-only.pipeline.v1";
    public const string IngestJobKind = "media.ingest.v1";
    public const int MaximumRequestUtf8Bytes = 2 * 1024 * 1024;
    public const int MaximumResultUtf8Bytes = 4 * 1024 * 1024;
    public const int MaximumProtectedLocatorBytes = 64 * 1024;
    public const int MaximumListResults = 1000;
    // Includes the result and every ancestor through the root, even for regeneration.
    // Keep the recursive bounds in Migration 011 in sync with this value.
    public const int MaximumVersionLineageLength = 256;
    public const int MaximumRetryLineageLength = 64;
    public const int MaximumSubtitleEditRoots = 4;
    private const string EntityKind = "source-only execution";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _databasePath;

    public SourceOnlyExecutionStore(string databasePath) =>
        _databasePath = SqliteStoreSupport.Initialize(databasePath);

    public StoredSourceOnlyExecution? Get(EntityId<SourceOnlyExecutionIdTag> executionId)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        return Query("id = $key", executionId.ToString(), 1).SingleOrDefault();
    }

    /// <summary>Returns the original finalize execution, never a regeneration or retry.</summary>
    public StoredSourceOnlyExecution? GetByDraft(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        return Query("draft_id = $key AND parent_version_id IS NULL AND retry_of_execution_id IS NULL", draftId.ToString(), 1)
            .SingleOrDefault();
    }

    /// <summary>For a reused ingest Job, returns the latest associated execution.</summary>
    public StoredSourceOnlyExecution? GetByJob(EntityId<JobIdTag> jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return Query("root_job_id = $key OR ingest_job_id = $key", jobId.ToString(), 1)
            .SingleOrDefault();
    }

    public StoredSourceOnlyExecution? GetLatestForProject(EntityId<ProjectIdTag> projectId) =>
        ListForProject(projectId, 1).SingleOrDefault();

    public IReadOnlyList<StoredSourceOnlyExecution> ListForProject(
        EntityId<ProjectIdTag> projectId, int maximumCount = 100)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (maximumCount is <= 0 or > MaximumListResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        return Query("project_id = $key", projectId.ToString(), maximumCount);
    }

    /// <summary>Returns a detached copy for native decryption, never for Web serialization.</summary>
    public byte[]? GetProtectedSourceLocator(EntityId<SourceOnlyExecutionIdTag> executionId)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        _ = Read(connection, transaction, executionId) ?? throw Missing();
        using SqliteCommand command = Command(connection, transaction,
            "SELECT protected_source_locator FROM source_only_executions WHERE id = $id;");
        SqliteStoreSupport.AddText(command, "$id", executionId.ToString());
        object? value = command.ExecuteScalar();
        byte[]? bytes = value is byte[] blob ? blob.ToArray() : null;
        transaction.Commit();
        return bytes;
    }

    /// <summary>
    /// Accepts a new root Job against the exact current settings and parent version.
    /// Reuses a successful ingest from the original acceptance or an explicit initial
    /// retry; never schedules an additional ingest/ASR. Acceptance replays use the exact
    /// request/settings revision. Retrying failed work uses CreateRetry instead.
    /// </summary>
    public StoredSourceOnlyExecution CreateRegeneration(
        SourceOnlyExecutionRequest request, int expectedSettingsRevision)
    {
        ValidateRequest(request);
        // Do not validate one mutable caller list and serialize a different capture.
        request = request with { SubtitleEdits = request.SubtitleEdits is null ? null : Array.AsReadOnly(request.SubtitleEdits.ToArray()) };
        ValidateRequest(request);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSettingsRevision);
        if (request.ParentVersionId is null)
        {
            throw new ArgumentException("Regeneration requires a parent version and digest.", nameof(request));
        }

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredSourceOnlyExecution original = ReadByDraft(connection, transaction, request.DraftId)
            ?? throw new InvalidOperationException("Regeneration requires a recorded initial execution.");
        if (original.Request.ProjectId != request.ProjectId ||
            original.Request.SourceCandidateId != request.SourceCandidateId ||
            request.CreatedAt < original.Request.CreatedAt)
        {
            throw Conflict();
        }

        byte[] payload = PrepareRequest(request, expectedSettingsRevision, null, null);
        StoredSourceOnlyExecution? existing = Read(connection, transaction, request.ExecutionId);
        if (existing is not null)
        {
            EnsureSameRequest(existing, payload);
            transaction.Commit();
            return existing;
        }

        ValidateSettings(connection, transaction, request, expectedSettingsRevision, requireCurrent: true);
        ValidateParentVersion(connection, transaction, request);
        IReadOnlyList<SubtitleEditHeadReference> heads = ReadSubtitleEditHeads(connection, transaction,
            request.ProjectId, request.ParentVersionId);
        if (!heads.SequenceEqual(OrderedSubtitleEdits(request))) { throw Conflict(); }
        ValidateSubtitleEditCapture(connection, transaction, request);
        ValidateIngestAcceptance(connection, transaction, request);
        StoredJobAggregate ingest = RequireJob(connection, transaction, request.IngestJobId,
            request.ProjectId, IngestJobKind);
        if (ingest.Job.State != JobState.Succeeded || ingest.Job.UpdatedAt > request.CreatedAt)
        {
            throw new InvalidOperationException("Regeneration requires a previously completed ingest Job.");
        }

        JobStore.CreateJob(connection, transaction, NewJob(request, ingest: false));
        Insert(connection, transaction, request, payload, protectedLocator: null);
        StoredSourceOnlyExecution stored = Read(connection, transaction, request.ExecutionId) ?? throw Invalid();
        transaction.Commit();
        return stored;
    }

    /// <summary>
    /// Captures all edited roots belonging to the parent or its direct composition children
    /// in one read snapshot. This is not acceptance: CreateRegeneration rechecks the exact set.
    /// </summary>
    public IReadOnlyList<SubtitleEditHeadReference> CaptureSubtitleEditHeads(
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> parentVersionId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(parentVersionId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceOnlyVersionLineageNode parent = ReadVersion(connection, transaction, projectId, parentVersionId);
        _ = ReadVersionLineage(connection, transaction, projectId, parentVersionId, parent.ContentDigest);
        IReadOnlyList<SubtitleEditHeadReference> heads = ReadSubtitleEditHeads(connection, transaction, projectId, parentVersionId);
        transaction.Commit();
        return heads;
    }

    private static IReadOnlyList<SubtitleEditHeadReference> ReadSubtitleEditHeads(
        SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> parentVersionId)
    {
        var heads = new List<SubtitleEditHeadReference>();
        using (SqliteCommand command = Command(connection, transaction, """
            SELECT e.root_version_id, e.sequence, e.record_digest
            FROM subtitle_edits e LEFT JOIN composition_versions v ON v.id = e.root_version_id
            WHERE (e.root_version_id = $parent OR v.parent_version_id = $parent)
                AND NOT EXISTS (SELECT 1 FROM subtitle_edits newer
                    WHERE newer.root_version_id = e.root_version_id AND newer.sequence > e.sequence)
            ORDER BY e.root_version_id LIMIT $limit;
            """))
        {
            SqliteStoreSupport.AddText(command, "$parent", parentVersionId.ToString());
            SqliteStoreSupport.AddInteger(command, "$limit", MaximumSubtitleEditRoots + 1);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                heads.Add(new(SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 0, EntityKind),
                    SqliteStoreSupport.ReadInt32(reader, 1, EntityKind),
                    SqliteStoreSupport.ReadContentDigest(reader, 2, EntityKind)));
            }
        }
        if (heads.Count > MaximumSubtitleEditRoots || heads.Select(head => head.RootVersionId).Distinct().Count() != heads.Count)
        { throw Invalid(); }
        foreach (SubtitleEditHeadReference head in heads)
        {
            IReadOnlyList<StoredSubtitleEdit> prefix = SubtitleEditStore.ReadCapturedPrefix(
                connection, transaction, projectId, head.RootVersionId, head.Sequence);
            if (prefix[^1].RecordDigest != head.RecordDigest) { throw Invalid(); }
        }
        return heads.AsReadOnly();
    }

    private static void ValidateSubtitleEditCapture(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request)
    {
        foreach (SubtitleEditHeadReference head in request.SubtitleEdits ?? [])
        {
            SourceOnlyVersionLineageNode root = ReadVersion(connection, transaction, request.ProjectId, head.RootVersionId);
            if (root.Id != request.ParentVersionId && root.ParentVersionId != request.ParentVersionId) { throw Invalid(); }
            _ = ReadVersionLineage(connection, transaction, request.ProjectId, root.Id, root.ContentDigest, request.CreatedAt);
            IReadOnlyList<StoredSubtitleEdit> prefix = SubtitleEditStore.ReadCapturedPrefix(
                connection, transaction, request.ProjectId, head.RootVersionId, head.Sequence);
            if (prefix[^1].RecordDigest != head.RecordDigest || prefix.Any(edit => edit.OccurredAt > request.CreatedAt))
            { throw Invalid(); }
        }
    }

    /// <summary>
    /// Explicit pre-result retry acceptance, NOT an in-place Job retry. JobStore's
    /// current transition/history contract cannot requeue Cancelled jobs and its public
    /// QueueRetry opens a separate transaction. Instead this method atomically creates
    /// one successor execution and a fresh queued root via JobStore.CreateJob, leaving
    /// the source execution/revision and all old attempts unchanged.
    /// </summary>
    /// <remarks>
    /// Only Failed/Canceled executions with no bound version/result are eligible, after
    /// the coordinator has settled the old root (Failed/Cancelled/Interrupted). Running
    /// work, Completed, ReselectionRequired and ReconciliationRequired are rejected;
    /// locator reselection and artifact reconciliation are NOT performed here.
    /// For a pre-dispatch failure with a still-queued root, the native caller must first
    /// settle it with JobStore.CancelQueued; that separate transition is not part of this
    /// transaction. retriedAt must be strictly later than the source acceptance so latest
    /// ordering cannot return the old execution on an equal-time random-ID tie.
    /// Settings (including their revision), project/draft/source, parent binding and
    /// protected locator bytes are copied exactly; actor/time/new job identities and the
    /// retry origin are recorded in the new immutable request. Succeeded ingest is reused
    /// without rerunning it; an untouched queued ingest is also reused. A failed/cancelled/
    /// interrupted ingest gets a fresh job, with its old history preserved.
    /// Same source/revision/actor/time replays return the same successor even after it
    /// advances. A different request for that source conflicts; to retry a successor,
    /// pass its identity/revision. Use the returned Request.RootJobId with the coordinator,
    /// and inspect its IngestJobId state before running ingest. A new initial composition
    /// must be created at/after the returned Request.CreatedAt. Bound-result recovery needs
    /// a separate reviewed reconciliation contract, not rebinding the old result to a new ID.
    /// Only database acceptance is atomic: this method neither runs nor reconciles file/AI/
    /// render/delivery side effects. The native caller remains responsible for authorization.
    /// </remarks>
    public StoredSourceOnlyExecution CreateRetry(
        EntityId<SourceOnlyExecutionIdTag> executionId, int expectedRevision,
        string actorSid, DateTimeOffset retriedAt)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ValidateActorSid(actorSid);
        if (retriedAt.Offset != TimeSpan.Zero) { throw new ArgumentException("Retry time must be UTC.", nameof(retriedAt)); }
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredSourceOnlyExecution source = Read(connection, transaction, executionId) ?? throw Missing();
        if (source.Revision != expectedRevision)
        {
            throw new StoreConcurrencyException(EntityKind, executionId.ToString(), expectedRevision, source.Revision);
        }
        if (!CanCreateRetry(source))
        {
            throw new InvalidOperationException("Retry requires a failed or canceled execution with no bound result; reconciliation and reselection are separate native operations.");
        }

        using (SqliteCommand lookup = Command(connection, transaction,
            "SELECT id FROM source_only_executions WHERE retry_of_execution_id = $source;"))
        {
            SqliteStoreSupport.AddText(lookup, "$source", executionId.ToString());
            object? value = lookup.ExecuteScalar();
            if (value is not null)
            {
                StoredSourceOnlyExecution existing = Read(connection, transaction, ParseId<SourceOnlyExecutionIdTag>((string)value)) ?? throw Invalid();
                if (existing.RetryOfRevision != expectedRevision || existing.RetryOfStateDigest != source.StateDigest ||
                    existing.Request.ActorSid != actorSid || existing.Request.CreatedAt != retriedAt) { throw Conflict(); }
                transaction.Commit();
                return existing;
            }
        }

        StoredJobAggregate root = RequireJob(connection, transaction, source.Request.RootJobId,
            source.Request.ProjectId, RootJobKind);
        StoredJobAggregate ingest = RequireJob(connection, transaction, source.Request.IngestJobId,
            source.Request.ProjectId, IngestJobKind);
        bool reuseIngest = ingest.Job.State == JobState.Succeeded ||
            (ingest.Job.State == JobState.Queued && ingest.Job.CurrentAttemptNumber == 0 && ingest.Revision == 0);
        if (!IsSettledUnsuccessfulJob(root.Job.State) ||
            (!reuseIngest && !IsSettledUnsuccessfulJob(ingest.Job.State)) ||
            retriedAt <= source.Request.CreatedAt || retriedAt < root.Job.UpdatedAt || retriedAt < ingest.Job.UpdatedAt)
        {
            throw new InvalidOperationException("Retry requires settled jobs and a timestamp not preceding their updates.");
        }

        SourceOnlyExecutionRequest request = source.Request with
        {
            ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New(),
            RootJobId = EntityId<JobIdTag>.New(),
            IngestJobId = reuseIngest ? source.Request.IngestJobId : EntityId<JobIdTag>.New(),
            ActorSid = actorSid,
            CreatedAt = retriedAt,
        };
        ValidateRequest(request);
        byte[]? protectedLocator;
        using (SqliteCommand locator = Command(connection, transaction,
            "SELECT protected_source_locator FROM source_only_executions WHERE id = $id;"))
        {
            SqliteStoreSupport.AddText(locator, "$id", executionId.ToString());
            protectedLocator = locator.ExecuteScalar() is byte[] bytes ? bytes.ToArray() : null;
        }
        byte[] payload = PrepareRequest(request, source.SettingsRevision, source.FinalizationDigest,
            source.LocatorDigest, executionId, source.Revision, source.StateDigest);
        JobStore.CreateJob(connection, transaction, NewJob(request, ingest: false));
        if (!reuseIngest) { JobStore.CreateJob(connection, transaction, NewJob(request, ingest: true)); }
        Insert(connection, transaction, request, payload, protectedLocator, executionId);
        StoredSourceOnlyExecution accepted = Read(connection, transaction, request.ExecutionId) ?? throw Invalid();
        transaction.Commit();
        return accepted;
    }

    /// <summary>
    /// Full replacement of mutable result/version/failure fields (null clears optional
    /// fields). Once a version is bound it cannot be cleared/replaced. Same-value writes
    /// at the current revision are no-ops; stale revisions always conflict, even no-ops.
    /// Terminal states cannot change; eligible pre-result failures use CreateRetry.
    /// Bind only the final standard composition, not an intermediate baseline/auto
    /// version. Initial results must trace to a new root; regeneration must descend from
    /// the exact accepted parent. All links are revalidated through the root on reads.
    /// Result JSON requires a binding object with projectId, executionId, rootJobId,
    /// settingsSnapshotDigest, versionId and versionDigest; the remaining body is opaque.
    /// </summary>
    public StoredSourceOnlyExecution UpdateState(
        EntityId<SourceOnlyExecutionIdTag> executionId, int expectedRevision,
        SourceOnlyExecutionPhase phase, string? resultJson = null,
        EntityId<CompositionVersionIdTag>? versionId = null, ContentDigest? digest = null,
        string? failureCode = null)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        byte[]? result = resultJson is null ? null : BoundedJson(resultJson, MaximumResultUtf8Bytes);
        string? canonical = result is null ? null : Utf8.GetString(result);
        ContentDigest? resultDigest = result is null ? null : ContentDigest.Compute(result);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        StoredSourceOnlyExecution current = Read(connection, transaction, executionId) ?? throw Missing();
        if (current.Revision != expectedRevision)
        {
            throw new StoreConcurrencyException(EntityKind, executionId.ToString(), expectedRevision, current.Revision);
        }

        ValidateState(current.Request, phase, canonical, versionId, digest, failureCode);
        if (current.Phase == phase && current.ResultJson == canonical && current.VersionId == versionId &&
            current.VersionDigest == digest && current.FailureCode == failureCode)
        {
            transaction.Commit();
            return current;
        }

        if (IsTerminal(current.Phase) || !CanTransition(current.Phase, phase) ||
            (current.Request.ParentVersionId is not null && phase == SourceOnlyExecutionPhase.Ingesting) ||
            (current.VersionId is not null && (current.VersionId != versionId || current.VersionDigest != digest)))
        {
            throw new InvalidOperationException("Invalid source-only execution transition.");
        }

        if (versionId is not null)
        {
            ValidateResultVersion(connection, transaction, current.Request, versionId, digest!);
        }
        if (phase == SourceOnlyExecutionPhase.Completed &&
            RequireJob(connection, transaction, current.Request.IngestJobId,
                current.Request.ProjectId, IngestJobKind).Job.State != JobState.Succeeded)
        {
            throw new InvalidOperationException("Completion requires a successful ingest Job.");
        }

        int revision = checked(current.Revision + 1);
        ContentDigest stateDigest = ComputeStateDigest(current.RequestDigest, revision, phase,
            resultDigest, versionId, digest, failureCode);
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE source_only_executions SET revision = $revision, phase = $phase,
                result_payload = $result, result_digest = $result_digest, version_id = $version,
                version_digest = $version_digest, failure_code = $failure, state_digest = $state_digest
            WHERE id = $id AND revision = $expected;
            """);
        SqliteStoreSupport.AddInteger(command, "$revision", revision);
        SqliteStoreSupport.AddText(command, "$phase", phase.ToString());
        command.Parameters.Add("$result", SqliteType.Blob).Value = result ?? (object)DBNull.Value;
        SqliteStoreSupport.AddNullableText(command, "$result_digest", resultDigest?.Hex);
        SqliteStoreSupport.AddNullableText(command, "$version", versionId?.ToString());
        SqliteStoreSupport.AddNullableText(command, "$version_digest", digest?.Hex);
        SqliteStoreSupport.AddNullableText(command, "$failure", failureCode);
        SqliteStoreSupport.AddText(command, "$state_digest", stateDigest.Hex);
        SqliteStoreSupport.AddText(command, "$id", executionId.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected", expectedRevision);
        if (command.ExecuteNonQuery() != 1) { throw Invalid(); }
        StoredSourceOnlyExecution updated = Read(connection, transaction, executionId) ?? throw Invalid();
        transaction.Commit();
        return updated;
    }

    internal static byte[] PrepareInitial(
        SourceOnlyExecutionRequest request, ContentDigest finalizationDigest, byte[]? protectedLocator)
    {
        ValidateRequest(request);
        if (request.ParentVersionId is not null) { throw Conflict(); }
        ValidateLocator(protectedLocator);
        return PrepareRequest(request, 0, finalizationDigest,
            protectedLocator is null ? null : ContentDigest.Compute(protectedLocator));
    }

    internal static void InsertInitial(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request, byte[] payload, byte[]? protectedLocator)
    {
        ValidateSettings(connection, transaction, request, 0, requireCurrent: true);
        JobStore.CreateJob(connection, transaction, NewJob(request, ingest: false));
        JobStore.CreateJob(connection, transaction, NewJob(request, ingest: true));
        Insert(connection, transaction, request, payload, protectedLocator);
        _ = Read(connection, transaction, request.ExecutionId) ?? throw Invalid();
    }

    internal static StoredSourceOnlyExecution? ReadByDraft(
        SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectCreationDraftIdTag> draftId)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT id FROM source_only_executions WHERE draft_id = $draft AND parent_version_id IS NULL AND retry_of_execution_id IS NULL;");
        SqliteStoreSupport.AddText(command, "$draft", draftId.ToString());
        object? value = command.ExecuteScalar();
        return value is null ? null : Read(connection, transaction, ParseId<SourceOnlyExecutionIdTag>((string)value));
    }

    internal static void EnsureSameRequest(StoredSourceOnlyExecution existing, byte[] expected)
    {
        if (!Utf8.GetBytes(existing.RequestJson).AsSpan().SequenceEqual(expected)) { throw Conflict(); }
    }

    internal static void ValidateAll(string databasePath)
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        // Stream one identity at a time; each read validates all bound rows in this snapshot.
        using SqliteCommand command = Command(connection, transaction, "SELECT id FROM source_only_executions ORDER BY id;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            EntityId<SourceOnlyExecutionIdTag> id = SqliteStoreSupport.ReadEntityId<SourceOnlyExecutionIdTag>(reader, 0, EntityKind);
            _ = Read(connection, transaction, id) ?? throw Invalid();
        }
        transaction.Commit();
    }

    private IReadOnlyList<StoredSourceOnlyExecution> Query(string predicate, string key, int limit)
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        var ids = new List<EntityId<SourceOnlyExecutionIdTag>>();
        using (SqliteCommand command = Command(connection, transaction,
            $"SELECT id FROM source_only_executions WHERE {predicate} ORDER BY created_at DESC, id DESC LIMIT $limit;"))
        {
            SqliteStoreSupport.AddText(command, "$key", key);
            SqliteStoreSupport.AddInteger(command, "$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { ids.Add(SqliteStoreSupport.ReadEntityId<SourceOnlyExecutionIdTag>(reader, 0, EntityKind)); }
        }
        StoredSourceOnlyExecution[] rows = ids.Select(id => Read(connection, transaction, id) ?? throw Invalid()).ToArray();
        transaction.Commit();
        return Array.AsReadOnly(rows);
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request, byte[] payload, byte[]? protectedLocator,
        EntityId<SourceOnlyExecutionIdTag>? retryOfExecutionId = null)
    {
        ContentDigest requestDigest = ContentDigest.Compute(payload);
        using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO source_only_executions (
                id, project_id, draft_id, source_candidate_id, root_job_id, ingest_job_id,
                parent_version_id, parent_digest, created_at, request_payload, request_digest,
                protected_source_locator, revision, phase, state_digest, retry_of_execution_id
            ) VALUES ($id, $project, $draft, $source, $root, $ingest, $parent, $parent_digest,
                $created, $request, $request_digest, $locator, 0, 'Queued', $state_digest, $retry_of);
            """);
        SqliteStoreSupport.AddText(command, "$id", request.ExecutionId.ToString());
        SqliteStoreSupport.AddText(command, "$project", request.ProjectId.ToString());
        SqliteStoreSupport.AddText(command, "$draft", request.DraftId.ToString());
        SqliteStoreSupport.AddText(command, "$source", request.SourceCandidateId.ToString());
        SqliteStoreSupport.AddText(command, "$root", request.RootJobId.ToString());
        SqliteStoreSupport.AddText(command, "$ingest", request.IngestJobId.ToString());
        SqliteStoreSupport.AddNullableText(command, "$parent", request.ParentVersionId?.ToString());
        SqliteStoreSupport.AddNullableText(command, "$parent_digest", request.ParentDigest?.Hex);
        SqliteStoreSupport.AddText(command, "$created", Timestamp(request.CreatedAt));
        SqliteStoreSupport.AddBlob(command, "$request", payload);
        SqliteStoreSupport.AddText(command, "$request_digest", requestDigest.Hex);
        command.Parameters.Add("$locator", SqliteType.Blob).Value = protectedLocator ?? (object)DBNull.Value;
        SqliteStoreSupport.AddText(command, "$state_digest", ComputeStateDigest(requestDigest, 0,
            SourceOnlyExecutionPhase.Queued, null, null, null, null).Hex);
        SqliteStoreSupport.AddNullableText(command, "$retry_of", retryOfExecutionId?.ToString());
        if (command.ExecuteNonQuery() != 1) { throw Invalid(); }
    }

    private static StoredSourceOnlyExecution? Read(SqliteConnection connection,
        SqliteTransaction transaction, EntityId<SourceOnlyExecutionIdTag> id, bool requireInitial = false)
    {
        try
        {
            StoredSourceOnlyExecution? stored = ReadCore(connection, transaction, id, checkParent: requireInitial);
            if (stored is null) { return null; }
            var visited = new HashSet<EntityId<SourceOnlyExecutionIdTag>> { id };
            StoredSourceOnlyExecution child = stored;
            while (child.RetryOfExecutionId is EntityId<SourceOnlyExecutionIdTag> previousId)
            {
                if (visited.Count == MaximumRetryLineageLength || !visited.Add(previousId)) { throw Invalid(); }
                StoredSourceOnlyExecution previous = ReadCore(connection, transaction, previousId,
                    checkParent: true, expectedParent: child.Request.ParentVersionId) ?? throw Invalid();
                ValidateRetryLink(connection, transaction, child, previous);
                child = previous;
            }
            return stored;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException or OverflowException or KeyNotFoundException)
        {
            // Never include raw payloads, locators or parser exceptions in public diagnostics.
            throw Invalid();
        }
    }

    private static StoredSourceOnlyExecution? ReadCore(SqliteConnection connection,
        SqliteTransaction transaction, EntityId<SourceOnlyExecutionIdTag> id,
        bool checkParent = false, EntityId<CompositionVersionIdTag>? expectedParent = null)
    {
        StoredSourceOnlyExecution stored;
        using (SqliteCommand command = Command(connection, transaction, """
            SELECT id, project_id, draft_id, source_candidate_id, root_job_id, ingest_job_id,
                parent_version_id, parent_digest, created_at,
                length(request_payload), request_payload, request_digest,
                length(protected_source_locator), protected_source_locator,
                revision, phase, length(result_payload), result_payload, result_digest,
                version_id, version_digest, failure_code, state_digest, retry_of_execution_id
            FROM source_only_executions WHERE id = $id;
            """))
        {
            SqliteStoreSupport.AddText(command, "$id", id.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) { return null; }
            EntityId<CompositionVersionIdTag>? storedParent = SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(reader, 6, EntityKind);
            // Check before following any ingest acceptance. An initial retry cannot
            // point to regeneration and recursively re-enter its own ingestion proof.
            if (checkParent && storedParent != expectedParent) { throw Invalid(); }
            string requestJson = ReadJson(reader, 9, 10, MaximumRequestUtf8Bytes);
            ContentDigest requestDigest = SqliteStoreSupport.ReadContentDigest(reader, 11, EntityKind);
            if (ContentDigest.Compute(Utf8.GetBytes(requestJson)) != requestDigest) { throw Invalid(); }
            using JsonDocument document = JsonDocument.Parse(requestJson);
            JsonElement root = document.RootElement;
            string snapshotJson = root.GetProperty("settingsSnapshot").GetRawText();
            DefaultSettingsSnapshot snapshot = ParseSnapshot(connection, transaction, snapshotJson);
            var request = new SourceOnlyExecutionRequest(
                SqliteStoreSupport.ReadEntityId<SourceOnlyExecutionIdTag>(reader, 0, EntityKind),
                SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, EntityKind),
                SqliteStoreSupport.ReadEntityId<ProjectCreationDraftIdTag>(reader, 2, EntityKind),
                SqliteStoreSupport.ReadEntityId<AssetIdTag>(reader, 3, EntityKind),
                SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 4, EntityKind),
                SqliteStoreSupport.ReadEntityId<JobIdTag>(reader, 5, EntityKind), snapshot,
                Text(root, "actorSid"), SqliteStoreSupport.ReadTimestamp(reader, 8, EntityKind),
                storedParent,
                SqliteStoreSupport.ReadNullableContentDigest(reader, 7, EntityKind), ParseSubtitleEdits(root));
            int settingsRevision = JsonInt(root.GetProperty("settingsRevision"));
            ContentDigest? finalizationDigest = OptionalDigest(root, "finalizationDigest");
            ContentDigest? locatorDigest = OptionalDigest(root, "protectedLocatorDigest");
            EntityId<SourceOnlyExecutionIdTag>? retryOf = SqliteStoreSupport.ReadNullableEntityId<SourceOnlyExecutionIdTag>(reader, 23, EntityKind);
            int? retryRevision = retryOf is null ? null : JsonInt(root.GetProperty("retryOfRevision"));
            ContentDigest? retryState = retryOf is null ? null : ParseDigest(Text(root, "retryOfStateDigest"));
            if (reader.IsDBNull(12) != (locatorDigest is null)) { throw Invalid(); }
            if (locatorDigest is not null)
            {
                long length = SqliteStoreSupport.ReadInt64(reader, 12, EntityKind);
                if (length is <= 0 or > MaximumProtectedLocatorBytes || reader.GetValue(13) is not byte[] bytes ||
                    ContentDigest.Compute(bytes) != locatorDigest) { throw Invalid(); }
            }
            ValidateRequest(request);
            if (request.ExecutionId != id || settingsRevision < 0 ||
                (request.ParentVersionId is null) != (finalizationDigest is not null) ||
                (request.ParentVersionId is null && settingsRevision != 0) ||
                (request.ParentVersionId is not null && locatorDigest is not null) ||
                requestJson != Utf8.GetString(PrepareRequest(request, settingsRevision, finalizationDigest, locatorDigest,
                    retryOf, retryRevision, retryState)))
            { throw Invalid(); }

            int revision = SqliteStoreSupport.ReadInt32(reader, 14, EntityKind);
            string phaseText = SqliteStoreSupport.ReadString(reader, 15, EntityKind);
            if (!Enum.TryParse(phaseText, out SourceOnlyExecutionPhase phase) ||
                !Enum.IsDefined(phase) || phase.ToString() != phaseText || revision < 0) { throw Invalid(); }
            string? result = reader.IsDBNull(16) ? null : ReadJson(reader, 16, 17, MaximumResultUtf8Bytes);
            ContentDigest? resultDigest = SqliteStoreSupport.ReadNullableContentDigest(reader, 18, EntityKind);
            EntityId<CompositionVersionIdTag>? versionId = SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(reader, 19, EntityKind);
            ContentDigest? versionDigest = SqliteStoreSupport.ReadNullableContentDigest(reader, 20, EntityKind);
            string? failure = SqliteStoreSupport.ReadNullableString(reader, 21, EntityKind);
            ContentDigest stateDigest = SqliteStoreSupport.ReadContentDigest(reader, 22, EntityKind);
            if ((result is null) != (resultDigest is null) ||
                (result is not null && ContentDigest.Compute(Utf8.GetBytes(result)) != resultDigest) ||
                (revision == 0 && (phase != SourceOnlyExecutionPhase.Queued || result is not null || versionId is not null || failure is not null)) ||
                (request.ParentVersionId is not null && phase == SourceOnlyExecutionPhase.Ingesting) ||
                stateDigest != ComputeStateDigest(requestDigest, revision, phase, resultDigest, versionId, versionDigest, failure))
            { throw Invalid(); }
            ValidateState(request, phase, result, versionId, versionDigest, failure);
            stored = new(request, requestJson, requestDigest, settingsRevision, finalizationDigest,
                locatorDigest, revision, phase, result, resultDigest, versionId, versionDigest, failure, stateDigest,
                retryOf, retryRevision, retryState);
            if (reader.Read()) { throw Invalid(); }
        }

        SourceOnlyExecutionRequest accepted = stored.Request;
        StoredJobAggregate rootJob = RequireJob(connection, transaction, accepted.RootJobId, accepted.ProjectId, RootJobKind);
        StoredJobAggregate ingestJob = RequireJob(connection, transaction, accepted.IngestJobId, accepted.ProjectId, IngestJobKind);
        if (rootJob.Job.CreatedAt != accepted.CreatedAt || ingestJob.Job.CreatedAt > accepted.CreatedAt ||
            (accepted.ParentVersionId is null && stored.RetryOfExecutionId is null && ingestJob.Job.CreatedAt != accepted.CreatedAt) ||
            ((accepted.ParentVersionId is not null || stored.Phase == SourceOnlyExecutionPhase.Completed) &&
                ingestJob.Job.State != JobState.Succeeded)) { throw Invalid(); }
        ValidateSettings(connection, transaction, accepted, stored.SettingsRevision, requireCurrent: false);
        using (SqliteCommand command = Command(connection, transaction,
            "SELECT finalized_project_id, finalized_at FROM project_creation_drafts WHERE id = $draft;"))
        {
            SqliteStoreSupport.AddText(command, "$draft", accepted.DraftId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() || SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 0, EntityKind) != accepted.ProjectId ||
                SqliteStoreSupport.ReadTimestamp(reader, 1, EntityKind) > accepted.CreatedAt) { throw Invalid(); }
        }
        if (accepted.ParentVersionId is not null)
        {
            ValidateParentVersion(connection, transaction, accepted);
            // Only the captured prefix is immutable input; newer journal rows are not.
            ValidateSubtitleEditCapture(connection, transaction, accepted);
            ValidateIngestAcceptance(connection, transaction, accepted);
        }
        if (stored.VersionId is not null)
        {
            ValidateResultVersion(connection, transaction, accepted, stored.VersionId, stored.VersionDigest!);
        }
        return stored;
    }

    private static void ValidateIngestAcceptance(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request)
    {
        // Initial retries may replace an unsuccessful ingest. Regeneration may reuse
        // only an ingest already associated with this draft/source's initial lineage.
        using SqliteCommand command = Command(connection, transaction, """
            SELECT id FROM source_only_executions
            WHERE draft_id = $draft AND parent_version_id IS NULL AND project_id = $project
                AND source_candidate_id = $source AND ingest_job_id = $ingest AND created_at <= $created
            ORDER BY created_at, id LIMIT 1;
            """);
        SqliteStoreSupport.AddText(command, "$draft", request.DraftId.ToString());
        SqliteStoreSupport.AddText(command, "$project", request.ProjectId.ToString());
        SqliteStoreSupport.AddText(command, "$source", request.SourceCandidateId.ToString());
        SqliteStoreSupport.AddText(command, "$ingest", request.IngestJobId.ToString());
        SqliteStoreSupport.AddText(command, "$created", Timestamp(request.CreatedAt));
        if (command.ExecuteScalar() is not string id) { throw Invalid(); }
        // A matching row is not proof until its payload, jobs and bounded retry origin
        // chain have also been validated. Initial reads never look up an ingest origin.
        _ = Read(connection, transaction, ParseId<SourceOnlyExecutionIdTag>(id), requireInitial: true) ?? throw Invalid();
    }

    private static bool CanCreateRetry(StoredSourceOnlyExecution source) =>
        (source.Phase is SourceOnlyExecutionPhase.Failed or SourceOnlyExecutionPhase.Canceled) &&
        source.VersionId is null && source.ResultJson is null;

    private static bool IsSettledUnsuccessfulJob(JobState state) =>
        state is JobState.Failed or JobState.Cancelled or JobState.Interrupted;

    private static void ValidateRetryLink(SqliteConnection connection, SqliteTransaction transaction,
        StoredSourceOnlyExecution child, StoredSourceOnlyExecution previous)
    {
        SourceOnlyExecutionRequest request = child.Request;
        SourceOnlyExecutionRequest source = previous.Request;
        if (!CanCreateRetry(previous) || child.RetryOfExecutionId != source.ExecutionId ||
            child.RetryOfRevision != previous.Revision || child.RetryOfStateDigest != previous.StateDigest ||
            request.ProjectId != source.ProjectId || request.DraftId != source.DraftId ||
            request.SourceCandidateId != source.SourceCandidateId || request.RootJobId == source.RootJobId ||
            request.ParentVersionId != source.ParentVersionId || request.ParentDigest != source.ParentDigest ||
            !OrderedSubtitleEdits(request).SequenceEqual(OrderedSubtitleEdits(source)) ||
            request.SettingsSnapshot.CanonicalJson != source.SettingsSnapshot.CanonicalJson ||
            child.SettingsRevision != previous.SettingsRevision || child.FinalizationDigest != previous.FinalizationDigest ||
            child.LocatorDigest != previous.LocatorDigest || request.CreatedAt <= source.CreatedAt) { throw Invalid(); }
        StoredJobAggregate oldRoot = RequireJob(connection, transaction, source.RootJobId, source.ProjectId, RootJobKind);
        if (!IsSettledUnsuccessfulJob(oldRoot.Job.State) || oldRoot.Job.UpdatedAt > request.CreatedAt) { throw Invalid(); }
        if (request.IngestJobId != source.IngestJobId)
        {
            StoredJobAggregate oldIngest = RequireJob(connection, transaction, source.IngestJobId, source.ProjectId, IngestJobKind);
            StoredJobAggregate newIngest = RequireJob(connection, transaction, request.IngestJobId, request.ProjectId, IngestJobKind);
            if (!IsSettledUnsuccessfulJob(oldIngest.Job.State) || oldIngest.Job.UpdatedAt > request.CreatedAt ||
                newIngest.Job.CreatedAt != request.CreatedAt) { throw Invalid(); }
        }
        // A damaged database must not turn a retry into a fork, even without the index.
        using SqliteCommand command = Command(connection, transaction,
            "SELECT COUNT(*) FROM source_only_executions WHERE retry_of_execution_id = $source;");
        SqliteStoreSupport.AddText(command, "$source", source.ExecutionId.ToString());
        if (command.ExecuteScalar() is not long count || count != 1) { throw Invalid(); }
    }

    private static void ValidateSettings(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request, int expectedRevision, bool requireCurrent)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT revision, length(initial_snapshot_payload), initial_snapshot_payload, initial_snapshot_digest,
                length(current_snapshot_payload), current_snapshot_payload, current_snapshot_digest,
                profile_id, profile_version, profile_digest, updated_at
            FROM project_default_settings WHERE project_id = $project;
            """);
        SqliteStoreSupport.AddText(command, "$project", request.ProjectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { throw Invalid(); }
        int revision = SqliteStoreSupport.ReadInt32(reader, 0, EntityKind);
        if (requireCurrent && revision != expectedRevision)
        {
            throw new StoreConcurrencyException("project default settings", request.ProjectId.ToString(), expectedRevision, revision);
        }
        DefaultSettingsSnapshot initial = ParseSnapshot(connection, transaction,
            ReadJson(reader, 1, 2, DefaultSettingsStore.MaximumCanonicalJsonUtf8Bytes));
        DefaultSettingsSnapshot current = ParseSnapshot(connection, transaction,
            ReadJson(reader, 4, 5, DefaultSettingsStore.MaximumCanonicalJsonUtf8Bytes));
        if (revision < expectedRevision || initial.ContentDigest != SqliteStoreSupport.ReadContentDigest(reader, 3, EntityKind) ||
            current.ContentDigest != SqliteStoreSupport.ReadContentDigest(reader, 6, EntityKind) ||
            initial.ProfileId.ToString() != SqliteStoreSupport.ReadString(reader, 7, EntityKind) ||
            initial.ProfileVersion.ToString() != SqliteStoreSupport.ReadString(reader, 8, EntityKind) ||
            initial.ProfileDigest != SqliteStoreSupport.ReadContentDigest(reader, 9, EntityKind) ||
            initial.ProfileId != request.SettingsSnapshot.ProfileId || initial.ProfileVersion != request.SettingsSnapshot.ProfileVersion ||
            initial.ProfileDigest != request.SettingsSnapshot.ProfileDigest ||
            current.ProfileId != initial.ProfileId || current.ProfileVersion != initial.ProfileVersion || current.ProfileDigest != initial.ProfileDigest ||
            (revision == 0 && initial.ContentDigest != current.ContentDigest) ||
            (request.ParentVersionId is null && initial.CanonicalJson != request.SettingsSnapshot.CanonicalJson)) { throw Invalid(); }
        if (requireCurrent)
        {
            if (current.CanonicalJson != request.SettingsSnapshot.CanonicalJson ||
                SqliteStoreSupport.ReadTimestamp(reader, 10, EntityKind) > request.CreatedAt) { throw Conflict(); }
        }
    }

    private static DefaultSettingsSnapshot ParseSnapshot(SqliteConnection connection,
        SqliteTransaction transaction, string snapshotJson)
    {
        using JsonDocument snapshot = JsonDocument.Parse(snapshotJson);
        JsonElement root = snapshot.RootElement;
        string profileId = ParseId<DefaultSettingsProfileIdTag>(Text(root, "profileId")).ToString();
        using SqliteCommand command = Command(connection, transaction, """
            SELECT length(profile_payload), profile_payload, profile_digest
            FROM default_settings_profiles WHERE profile_id = $id AND profile_version = $version;
            """);
        SqliteStoreSupport.AddText(command, "$id", profileId);
        SqliteStoreSupport.AddText(command, "$version", Text(root, "profileVersion"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { throw Invalid(); }
        DefaultSettingsProfile profile = DefaultSettingsProfile.ParseCanonical(
            ReadJson(reader, 0, 1, DefaultSettingsStore.MaximumCanonicalJsonUtf8Bytes));
        if (profile.Id.ToString() != profileId || profile.Version.ToString() != Text(root, "profileVersion") ||
            profile.ContentDigest != SqliteStoreSupport.ReadContentDigest(reader, 2, EntityKind)) { throw Invalid(); }
        return DefaultSettingsSnapshot.ParseCanonical(snapshotJson, profile);
    }

    private static void ValidateParentVersion(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request)
    {
        IReadOnlyList<SourceOnlyVersionLineageNode> lineage = ReadVersionLineage(connection, transaction,
            request.ProjectId, request.ParentVersionId!, request.ParentDigest!, latestCreatedAt: request.CreatedAt);
        // A proper descendant needs at least one more node within the same bound.
        if (lineage.Count == MaximumVersionLineageLength) { throw Invalid(); }
    }

    private static void ValidateResultVersion(SqliteConnection connection, SqliteTransaction transaction,
        SourceOnlyExecutionRequest request, EntityId<CompositionVersionIdTag> versionId, ContentDigest digest)
    {
        if (versionId == request.ParentVersionId) { throw Invalid(); }
        IReadOnlyList<SourceOnlyVersionLineageNode> lineage = ReadVersionLineage(
            connection, transaction, request.ProjectId, versionId, digest);
        int producedCount = lineage.Count;
        if (request.ParentVersionId is not null)
        {
            producedCount = -1;
            for (int index = 1; index < lineage.Count; index++)
            {
                if (lineage[index].Id != request.ParentVersionId) { continue; }
                if (lineage[index].ContentDigest != request.ParentDigest ||
                    lineage[index].CreatedAt > request.CreatedAt) { throw Invalid(); }
                producedCount = index;
                break;
            }
            if (producedCount < 1) { throw Invalid(); }
        }
        if (lineage.Take(producedCount).Any(node => node.CreatedAt < request.CreatedAt)) { throw Invalid(); }

        // An execution owns its produced suffix, not the historical parent used for
        // regeneration. Reject shared intermediates as well as duplicate final results.
        // Recheck this on reads, even when an index/trigger has been lost or rows damaged.
        using SqliteCommand command = Command(connection, transaction, """
            WITH RECURSIVE claimed(id, stop_id, depth, path) AS (
                SELECT version_id, parent_version_id, 1, '|' || version_id || '|'
                FROM source_only_executions
                WHERE id <> $execution AND version_id IS NOT NULL
                UNION ALL
                SELECT v.parent_version_id, c.stop_id, c.depth + 1, c.path || v.parent_version_id || '|'
                FROM claimed c JOIN composition_versions v ON v.id = c.id
                WHERE v.parent_version_id IS NOT NULL AND v.parent_version_id IS NOT c.stop_id
                    AND c.depth < $maximum AND instr(c.path, '|' || v.parent_version_id || '|') = 0
            )
            SELECT EXISTS (SELECT 1 FROM claimed JOIN json_each($produced) p ON p.value = claimed.id)
                OR EXISTS (
                    SELECT 1 FROM claimed c LEFT JOIN composition_versions v ON v.id = c.id
                    WHERE v.id IS NULL OR (v.parent_version_id IS NOT NULL AND v.parent_version_id IS NOT c.stop_id
                        AND (c.depth = $maximum OR instr(c.path, '|' || v.parent_version_id || '|') <> 0))
                );
            """);
        SqliteStoreSupport.AddText(command, "$execution", request.ExecutionId.ToString());
        SqliteStoreSupport.AddInteger(command, "$maximum", MaximumVersionLineageLength);
        SqliteStoreSupport.AddText(command, "$produced", JsonSerializer.Serialize(
            lineage.Take(producedCount).Select(node => node.Id.ToString()).ToArray()));
        if (command.ExecuteScalar() is not long count || count != 0) { throw Invalid(); }
    }

    private static IReadOnlyList<SourceOnlyVersionLineageNode> ReadVersionLineage(
        SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> versionId,
        ContentDigest digest, DateTimeOffset? latestCreatedAt = null)
    {
        var lineage = new List<SourceOnlyVersionLineageNode>();
        var visited = new HashSet<EntityId<CompositionVersionIdTag>>();
        EntityId<CompositionVersionIdTag>? next = versionId;
        while (next is not null)
        {
            if (lineage.Count == MaximumVersionLineageLength || !visited.Add(next)) { throw Invalid(); }
            SourceOnlyVersionLineageNode node = ReadVersion(connection, transaction, projectId, next);
            if ((lineage.Count == 0 && node.ContentDigest != digest) ||
                (latestCreatedAt is not null && node.CreatedAt > latestCreatedAt.Value)) { throw Invalid(); }
            lineage.Add(node);
            next = node.ParentVersionId;
            // No parent may come from its child's future. This is a persisted timestamp
            // ordering check, not a comparison to the reader's wall clock.
            latestCreatedAt = node.CreatedAt;
        }
        return lineage;
    }

    private static SourceOnlyVersionLineageNode ReadVersion(SqliteConnection connection, SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId, EntityId<CompositionVersionIdTag> versionId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT id, project_id, content_digest, parent_version_id, payload, created_at
            FROM composition_versions WHERE id = $id;
            """);
        SqliteStoreSupport.AddText(command, "$id", versionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || SqliteStoreSupport.ReadEntityId<CompositionVersionIdTag>(reader, 0, EntityKind) != versionId ||
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, EntityKind) != projectId) { throw Invalid(); }
        ContentDigest digest = SqliteStoreSupport.ReadContentDigest(reader, 2, EntityKind);
        EntityId<CompositionVersionIdTag>? parent = SqliteStoreSupport.ReadNullableEntityId<CompositionVersionIdTag>(reader, 3, EntityKind);
        DateTimeOffset createdAt = SqliteStoreSupport.ReadTimestamp(reader, 5, EntityKind);
        if (parent == versionId || createdAt.Offset != TimeSpan.Zero ||
            Timestamp(createdAt) != SqliteStoreSupport.ReadString(reader, 5, EntityKind)) { throw Invalid(); }
        // CompositionVersion.ContentDigest is the domain composition digest, NOT the
        // hash of the full metadata snapshot. Do not replace it with a payload hash.
        return SourceOnlyCompositionVersionReader.Validate(
            SqliteStoreSupport.ReadCanonicalPayload(reader, 4, EntityKind),
            versionId, projectId, parent, digest, createdAt);
    }

    private static StoredJobAggregate RequireJob(SqliteConnection connection, SqliteTransaction transaction,
        EntityId<JobIdTag> jobId, EntityId<ProjectIdTag> projectId, string kind)
    {
        StoredJobAggregate job = JobStore.ReadJobAggregate(connection, transaction, jobId) ?? throw Invalid();
        if (job.Job.ProjectId != projectId || job.Job.Kind != kind) { throw Invalid(); }
        return job;
    }

    private static Job NewJob(SourceOnlyExecutionRequest request, bool ingest) => new(
        ingest ? request.IngestJobId : request.RootJobId, request.ProjectId,
        ingest ? IngestJobKind : RootJobKind, JobState.Queued, request.CreatedAt, request.CreatedAt, 0);

    private static void ValidateRequest(SourceOnlyExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExecutionId);
        ArgumentNullException.ThrowIfNull(request.ProjectId);
        ArgumentNullException.ThrowIfNull(request.DraftId);
        ArgumentNullException.ThrowIfNull(request.SourceCandidateId);
        ArgumentNullException.ThrowIfNull(request.RootJobId);
        ArgumentNullException.ThrowIfNull(request.IngestJobId);
        ArgumentNullException.ThrowIfNull(request.SettingsSnapshot);
        ValidateActorSid(request.ActorSid);
        if (request.SubtitleEdits is { } edits &&
            (edits.Count > MaximumSubtitleEditRoots || (edits.Count != 0 && request.ParentVersionId is null) ||
                edits.Any(head => head is null || head.RootVersionId is null || head.RecordDigest is null ||
                    head.Sequence is < 1 or > SubtitleEditStore.MaximumBranchEdits) ||
                edits.Select(head => head.RootVersionId).Distinct().Count() != edits.Count))
        { throw new ArgumentException("Invalid subtitle edit capture.", nameof(request)); }
        if (request.RootJobId == request.IngestJobId ||
            (request.ParentVersionId is null) != (request.ParentDigest is null) ||
            request.CreatedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Invalid source-only execution request.", nameof(request));
        }
        _ = Timestamp(request.CreatedAt);
    }

    private static void ValidateActorSid(string actorSid)
    {
        ArgumentNullException.ThrowIfNull(actorSid);
        if (actorSid.Length is < 5 or > 256 || !actorSid.StartsWith("S-1-", StringComparison.Ordinal) ||
            actorSid.AsSpan(4).ContainsAnyExcept("0123456789-"))
        { throw new ArgumentException("Invalid actor SID.", nameof(actorSid)); }
        string[] sidParts = actorSid[4..].Split('-');
        if (sidParts.Length < 2 || sidParts.Any(part => part.Length == 0 ||
            (part.Length > 1 && part[0] == '0') || !ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        { throw new ArgumentException("Invalid actor SID.", nameof(actorSid)); }
    }

    private static void ValidateState(SourceOnlyExecutionRequest request, SourceOnlyExecutionPhase phase,
        string? result, EntityId<CompositionVersionIdTag>? version, ContentDigest? digest, string? failure)
    {
        bool needsFailure = phase is SourceOnlyExecutionPhase.Failed or SourceOnlyExecutionPhase.ReselectionRequired or SourceOnlyExecutionPhase.ReconciliationRequired;
        if (!Enum.IsDefined(phase) || (version is null) != (digest is null) ||
            (result is not null && version is null) || needsFailure != (failure is not null) ||
            (failure is not null && (failure.Length is < 1 or > 128 || failure.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('_' or '.' or '-')))) ||
            (phase == SourceOnlyExecutionPhase.Completed && (result is null || version is null)))
        { throw new ArgumentException("Invalid source-only state binding."); }
        if (result is null) { return; }
        using JsonDocument document = JsonDocument.Parse(result);
        if (!document.RootElement.TryGetProperty("binding", out JsonElement binding) ||
            binding.ValueKind != JsonValueKind.Object || binding.EnumerateObject().Count() != 6 ||
            !MatchesText(binding, "projectId", request.ProjectId.ToString()) ||
            !MatchesText(binding, "executionId", request.ExecutionId.ToString()) ||
            !MatchesText(binding, "rootJobId", request.RootJobId.ToString()) ||
            !MatchesText(binding, "settingsSnapshotDigest", request.SettingsSnapshot.ContentDigest.Hex) ||
            !MatchesText(binding, "versionId", version!.ToString()) || !MatchesText(binding, "versionDigest", digest!.Hex))
        { throw new ArgumentException("Result does not match the accepted execution binding."); }
    }

    private static bool MatchesText(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool IsTerminal(SourceOnlyExecutionPhase phase) => phase >= SourceOnlyExecutionPhase.Completed;
    private static bool CanTransition(SourceOnlyExecutionPhase from, SourceOnlyExecutionPhase to) =>
        to is SourceOnlyExecutionPhase.Failed or SourceOnlyExecutionPhase.Canceled or
            SourceOnlyExecutionPhase.ReselectionRequired or SourceOnlyExecutionPhase.ReconciliationRequired ||
        (to == SourceOnlyExecutionPhase.Completed ? from == SourceOnlyExecutionPhase.Delivering : to >= from);

    private static byte[] PrepareRequest(SourceOnlyExecutionRequest request, int settingsRevision,
        ContentDigest? finalizationDigest, ContentDigest? locatorDigest,
        EntityId<SourceOnlyExecutionIdTag>? retryOfExecutionId = null, int? retryOfRevision = null,
        ContentDigest? retryOfStateDigest = null)
    {
        using JsonDocument snapshot = JsonDocument.Parse(request.SettingsSnapshot.CanonicalJson);
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = retryOfExecutionId is null ? "source-only.execution.v1" : "source-only.execution-retry.v1",
            ["executionId"] = request.ExecutionId.ToString(),
            ["projectId"] = request.ProjectId.ToString(),
            ["draftId"] = request.DraftId.ToString(),
            ["sourceCandidateId"] = request.SourceCandidateId.ToString(),
            ["rootJobId"] = request.RootJobId.ToString(),
            ["ingestJobId"] = request.IngestJobId.ToString(),
            ["settingsSnapshot"] = snapshot.RootElement,
            ["settingsSnapshotDigest"] = request.SettingsSnapshot.ContentDigest.Hex,
            ["actorSid"] = request.ActorSid,
            ["createdAt"] = Timestamp(request.CreatedAt),
            ["parentVersionId"] = request.ParentVersionId?.ToString(),
            ["parentDigest"] = request.ParentDigest?.Hex,
            ["settingsRevision"] = settingsRevision,
            ["finalizationDigest"] = finalizationDigest?.Hex,
            ["protectedLocatorDigest"] = locatorDigest?.Hex,
        };
        if (retryOfExecutionId is not null)
        {
            payload["retryOfExecutionId"] = retryOfExecutionId.ToString();
            payload["retryOfRevision"] = retryOfRevision;
            payload["retryOfStateDigest"] = retryOfStateDigest?.Hex;
        }
        // Omit entirely for legacy/empty captures: existing request bytes and digests stay unchanged.
        if (request.SubtitleEdits is { Count: > 0 })
        {
            payload["subtitleEdits"] = OrderedSubtitleEdits(request).Select(head => new
            { rootVersionId = head.RootVersionId.ToString(), sequence = head.Sequence, recordDigest = head.RecordDigest.Hex }).ToArray();
        }
        return BoundedJson(JsonSerializer.Serialize(payload), MaximumRequestUtf8Bytes);
    }

    private static IEnumerable<SubtitleEditHeadReference> OrderedSubtitleEdits(SourceOnlyExecutionRequest request) =>
        (request.SubtitleEdits ?? []).OrderBy(head => head.RootVersionId.ToString(), StringComparer.Ordinal);

    private static IReadOnlyList<SubtitleEditHeadReference>? ParseSubtitleEdits(JsonElement root)
    {
        if (!root.TryGetProperty("subtitleEdits", out JsonElement edits)) { return null; }
        if (edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() is < 1 or > MaximumSubtitleEditRoots)
        { throw Invalid(); }
        var heads = new List<SubtitleEditHeadReference>();
        foreach (JsonElement edit in edits.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Count() != 3) { throw Invalid(); }
            JsonElement sequence = edit.GetProperty("sequence");
            // Integer parsing must not round fractional tails through decimal.
            if (sequence.ValueKind != JsonValueKind.Number || !int.TryParse(sequence.GetRawText(),
                NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out int number) || number is < 1 or > SubtitleEditStore.MaximumBranchEdits)
            { throw Invalid(); }
            heads.Add(new(ParseId<CompositionVersionIdTag>(Text(edit, "rootVersionId")), number,
                ParseDigest(Text(edit, "recordDigest"))));
        }
        return heads.AsReadOnly();
    }

    private static ContentDigest ComputeStateDigest(ContentDigest requestDigest, int revision,
        SourceOnlyExecutionPhase phase, ContentDigest? resultDigest,
        EntityId<CompositionVersionIdTag>? versionId, ContentDigest? versionDigest, string? failureCode) =>
        ContentDigest.Compute(BoundedJson(JsonSerializer.Serialize(new
        {
            schema = "source-only.state.v1",
            requestDigest = requestDigest.Hex,
            revision,
            phase = phase.ToString(),
            resultDigest = resultDigest?.Hex,
            versionId = versionId?.ToString(),
            versionDigest = versionDigest?.Hex,
            failureCode,
        }), MaximumRequestUtf8Bytes));

    private static byte[] BoundedJson(string json, int maximum)
    {
        try
        {
            if (Utf8.GetByteCount(json) > maximum) { throw new ArgumentException("Execution JSON exceeds its byte limit."); }
            byte[] bytes = SqliteStoreSupport.CanonicalizePayload(json, nameof(json));
            if (bytes.Length > maximum) { throw new ArgumentException("Execution JSON exceeds its byte limit."); }
            return bytes;
        }
        catch (EncoderFallbackException) { throw new ArgumentException("Execution JSON contains invalid Unicode."); }
    }

    private static void ValidateLocator(byte[]? bytes)
    {
        if (bytes is not null && bytes.Length is <= 0 or > MaximumProtectedLocatorBytes)
        { throw new ArgumentException("Protected source locator exceeds its byte limits.", nameof(bytes)); }
    }

    private static string ReadJson(SqliteDataReader reader, int lengthOrdinal, int payloadOrdinal, int maximum)
    {
        long length = SqliteStoreSupport.ReadInt64(reader, lengthOrdinal, EntityKind);
        if (length <= 0 || length > maximum) { throw Invalid(); }
        return SqliteStoreSupport.ReadCanonicalPayload(reader, payloadOrdinal, EntityKind);
    }

    private static int JsonInt(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !decimal.TryParse(value.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number) ||
            number < 0 || number > int.MaxValue || number != decimal.Truncate(number)) { throw Invalid(); }
        return (int)number;
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw Invalid();
    private static ContentDigest? OptionalDigest(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : ParseDigest(Text(root, name));
    private static ContentDigest ParseDigest(string text) =>
        ContentDigest.TryParse(text, out ContentDigest? digest) && digest.Hex == text ? digest : throw Invalid();
    private static EntityId<T> ParseId<T>(string text) =>
        EntityId<T>.TryParse(text, out EntityId<T>? id) && id.ToString() == text ? id : throw Invalid();
    private static string Timestamp(DateTimeOffset value) => SqliteStoreSupport.FormatTimestamp(value, nameof(value));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
    private static InvalidOperationException Invalid() => SqliteStoreSupport.InvalidStoredData(EntityKind);
    private static InvalidOperationException Missing() => new("The source-only execution does not exist.");
    private static InvalidOperationException Conflict() => new("The source-only execution request conflicts with the recorded acceptance.");
}