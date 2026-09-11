using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class QuickStartWorkflowEventConflictException : InvalidOperationException
{
    public QuickStartWorkflowEventConflictException(
        EntityId<QuickStartWorkflowEventIdTag> eventId)
        : base(FormattableString.Invariant(
            $"Quick-start event '{eventId}' is already stored with different canonical evidence."))
    {
        ArgumentNullException.ThrowIfNull(eventId);
        EventId = eventId;
    }

    public EntityId<QuickStartWorkflowEventIdTag> EventId { get; }
}

public sealed class QuickStartDeliveryOperationIdTag
{
}

public enum QuickStartDeliveryFailureReason
{
    WorkspaceNotConfigured,
    DuplicateOperationId,
    DuplicateReceiptEventId,
    AuthorizationNotFound,
    AuthorizationAlreadyConsumed,
    AuthorizationRequestMismatch,
    AuthorizationStale,
    AuthorizationRevoked,
    EvidenceInvalid,
    ArtifactValidationFailed,
    ReceiptPersistenceFailed,
}

public sealed class QuickStartDeliveryException : Exception
{
    internal QuickStartDeliveryException(
        QuickStartDeliveryFailureReason reason,
        bool reconciliationRequired = false,
        Exception? innerException = null)
        : base(GetStableMessage(reason), innerException)
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public QuickStartDeliveryFailureReason Reason { get; }

    /// <summary>
    /// True only when the caller's awaited delivery operation succeeded but the
    /// durable Exported receipt could not be recorded. The caller must reconcile the
    /// external file-system result with the workflow before retrying any delivery.
    /// </summary>
    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(
        QuickStartDeliveryFailureReason reason) => reason switch
        {
            QuickStartDeliveryFailureReason.WorkspaceNotConfigured =>
                "A workspace is required for quick-start delivery authorization.",
            QuickStartDeliveryFailureReason.DuplicateOperationId =>
                "The quick-start delivery operation identity has already been used.",
            QuickStartDeliveryFailureReason.DuplicateReceiptEventId =>
                "The quick-start delivery receipt identity has already been used.",
            QuickStartDeliveryFailureReason.AuthorizationNotFound =>
                "The quick-start delivery authorization is not active.",
            QuickStartDeliveryFailureReason.AuthorizationAlreadyConsumed =>
                "The quick-start delivery authorization has already been consumed.",
            QuickStartDeliveryFailureReason.AuthorizationRequestMismatch =>
                "The quick-start delivery authorization does not match this request.",
            QuickStartDeliveryFailureReason.AuthorizationStale =>
                "The quick-start delivery evidence changed before delivery began.",
            QuickStartDeliveryFailureReason.AuthorizationRevoked =>
                "The quick-start local-internal delivery is no longer authorized.",
            QuickStartDeliveryFailureReason.EvidenceInvalid =>
                "The quick-start delivery evidence is invalid.",
            QuickStartDeliveryFailureReason.ArtifactValidationFailed =>
                "A required quick-start delivery artifact failed validation.",
            QuickStartDeliveryFailureReason.ReceiptPersistenceFailed =>
                "The completed quick-start delivery requires receipt reconciliation.",
            _ => "The quick-start delivery operation failed.",
        };
}

public sealed class QuickStartLocalInternalDeliveryRequest
{
    public QuickStartLocalInternalDeliveryRequest(
        EntityId<QuickStartDeliveryOperationIdTag> operationId,
        EntityId<QuickStartWorkflowEventIdTag> receiptEventId,
        QuickStartWorkflowBinding binding,
        int expectedRevision,
        string currentUserSid,
        DateTimeOffset receiptOccurredAt)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(receiptEventId);
        ArgumentNullException.ThrowIfNull(binding);
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "The expected ready-workflow revision must be positive.");
        }

        QuickStartActor actor = QuickStartActor.User(currentUserSid);
        QuickStartWorkflowContract.ValidateUtcTimestamp(
            receiptOccurredAt,
            nameof(receiptOccurredAt));

        OperationId = operationId;
        ReceiptEventId = receiptEventId;
        Binding = binding;
        ExpectedRevision = expectedRevision;
        ReceiptOccurredAt = receiptOccurredAt;
        Actor = actor;
    }

    public EntityId<QuickStartDeliveryOperationIdTag> OperationId { get; }

    public EntityId<QuickStartWorkflowEventIdTag> ReceiptEventId { get; }

    public QuickStartWorkflowBinding Binding { get; }

    public int ExpectedRevision { get; }

    public DateTimeOffset ReceiptOccurredAt { get; }

    internal QuickStartActor Actor { get; }
}

public sealed class QuickStartLocalInternalDeliveryAuthorization
{
    internal QuickStartLocalInternalDeliveryAuthorization(
        QuickStartLocalInternalDeliveryRequest request,
        ContentDigest requestDigest,
        ContentDigest evidenceDigest)
    {
        OperationId = request.OperationId;
        ReceiptEventId = request.ReceiptEventId;
        RequestDigest = requestDigest;
        EvidenceDigest = evidenceDigest;
    }

    public EntityId<QuickStartDeliveryOperationIdTag> OperationId { get; }

    public EntityId<QuickStartWorkflowEventIdTag> ReceiptEventId { get; }

    public ContentDigest EvidenceDigest { get; }

    internal ContentDigest RequestDigest { get; }
}

/// <summary>
/// Opaque, short-lived access to physically validated source and render handles.
/// The handles remain open until the awaited callback completes and expose no path.
/// </summary>
public sealed class QuickStartLocalInternalDeliveryContext
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<ValidatedArtifactReadStream> _sourceArtifacts;
    private readonly ValidatedArtifactReadStream _renderArtifact;
    private bool _active = true;

    internal QuickStartLocalInternalDeliveryContext(
        QuickStartLocalInternalDeliveryAuthorization authorization,
        QuickStartWorkflow workflow,
        IReadOnlyList<ValidatedArtifactReadStream> sourceArtifacts,
        ValidatedArtifactReadStream renderArtifact)
    {
        Authorization = authorization;
        Workflow = workflow;
        _sourceArtifacts = sourceArtifacts;
        _renderArtifact = renderArtifact;
    }

    public QuickStartLocalInternalDeliveryAuthorization Authorization { get; }

    public QuickStartWorkflow Workflow { get; }

    public IReadOnlyList<ValidatedArtifactReadStream> SourceArtifacts
    {
        get
        {
            EnsureActive();
            return _sourceArtifacts;
        }
    }

    public ValidatedArtifactReadStream RenderArtifact
    {
        get
        {
            EnsureActive();
            return _renderArtifact;
        }
    }

    internal void Expire()
    {
        lock (_sync)
        {
            _active = false;
        }
    }

    private void EnsureActive()
    {
        lock (_sync)
        {
            if (!_active)
            {
                throw new InvalidOperationException(
                    "The quick-start delivery context is no longer active.");
            }
        }
    }
}

public sealed class QuickStartWorkflowStore
{
    public const int MaximumListResults = 1000;

    private const string EntityKind = "quick-start workflow";
    private const string TimestampFormat = "O";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;
    private readonly ArtifactStore? _artifactStore;
    private readonly object _authorizationSync = new();
    private readonly HashSet<EntityId<QuickStartDeliveryOperationIdTag>>
        _usedOperationIds = [];
    private readonly HashSet<EntityId<QuickStartWorkflowEventIdTag>>
        _reservedReceiptEventIds = [];
    private readonly Dictionary<
        EntityId<QuickStartDeliveryOperationIdTag>,
        DeliveryAuthorizationState> _authorizations = [];

    public QuickStartWorkflowStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public QuickStartWorkflowStore(string databasePath, string workspacePath)
        : this(databasePath)
    {
        _artifactStore = new ArtifactStore(_databasePath, workspacePath);
    }

    public QuickStartWorkflow Append(
        QuickStartWorkflowEvent workflowEvent,
        int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        if (workflowEvent.Kind == QuickStartWorkflowEventKind.Exported)
        {
            throw new InvalidOperationException(
                "Exported events can be appended only as a trusted local-internal delivery receipt.");
        }

        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "The expected revision must not be negative.");
        }

        PreparedEvent prepared = Prepare(workflowEvent);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        StoredEventIdentity? existing = ReadEventIdentity(
            connection,
            transaction,
            prepared.Event.EventId);
        if (existing is not null)
        {
            if (!prepared.CanonicalUtf8.AsSpan().SequenceEqual(existing.CanonicalPayload))
            {
                throw new QuickStartWorkflowEventConflictException(
                    prepared.Event.EventId);
            }

            QuickStartWorkflow idempotent = Read(
                connection,
                transaction,
                existing.Binding)
                ?? throw InvalidStoredData();
            transaction.Commit();
            return idempotent;
        }

        QuickStartWorkflow? current = Read(
            connection,
            transaction,
            prepared.Event.Binding);
        int actualRevision = current?.Revision ?? 0;
        if (actualRevision != expectedRevision)
        {
            throw new StoreConcurrencyException(
                EntityKind,
                prepared.Event.Binding.CompositionVersionId.ToString(),
                expectedRevision,
                actualRevision);
        }

        QuickStartWorkflow stored = AppendCore(
            connection,
            transaction,
            prepared,
            current,
            expectedRevision);
        transaction.Commit();
        return stored;
    }

    /// <summary>
    /// Captures fresh repository evidence, physically validates every current Original
    /// source and the evaluated Render, and issues an in-process one-shot authorization.
    /// No legacy approval record participates in this quick-start decision.
    /// </summary>
    public async Task<QuickStartLocalInternalDeliveryAuthorization>
        AuthorizeLocalInternalDeliveryAsync(
            QuickStartLocalInternalDeliveryRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureArtifactStoreConfigured();
        ReserveDeliveryIdentities(request);

        ContentDigest requestDigest = ComputeDeliveryRequestDigest(request);
        await using OpenedDeliveryArtifacts opened =
            await EvaluateAndOpenDeliveryArtifactsAsync(
                request,
                consuming: false,
                cancellationToken).ConfigureAwait(false);
        var authorization = new QuickStartLocalInternalDeliveryAuthorization(
            request,
            requestDigest,
            opened.Evidence.EvidenceDigest);
        lock (_authorizationSync)
        {
            _authorizations.Add(
                request.OperationId,
                new DeliveryAuthorizationState(
                    authorization,
                    request,
                    requestDigest,
                    DeliveryAuthorizationLifecycle.Active));
        }

        return authorization;
    }

    /// <summary>
    /// Marks an authorization one-shot, repeats repository and physical validation,
    /// and awaits the callback while all validated read handles remain open. An immediate
    /// SQLite write transaction serializes the final evidence check, callback and receipt
    /// across store instances and processes. Only a successful callback produces a receipt.
    /// </summary>
    /// <remarks>
    /// File-system delivery and SQLite receipt persistence are not one atomic transaction.
    /// The callback is trusted app-local code: it must await all file operations, must not
    /// write to this database (including ArtifactStore promotion), and must not reenter
    /// delivery consumption. Those operations would contend with the held write lock.
    /// Cancellation or callback failure may leave partial file-system effects; the caller
    /// owns their cleanup/reconciliation. A process crash or receipt failure can also leave
    /// delivered files without a receipt: this is not crash-safe exactly-once file delivery.
    /// If receipt persistence fails after callback success, the thrown exception has
    /// <see cref="QuickStartDeliveryException.ReconciliationRequired"/> set to true and
    /// the caller must reconcile the completed file-system operation before any retry.
    /// </remarks>
    public async Task<QuickStartWorkflow> ConsumeLocalInternalDeliveryAsync(
        QuickStartLocalInternalDeliveryAuthorization authorization,
        QuickStartLocalInternalDeliveryRequest request,
        Func<QuickStartLocalInternalDeliveryContext, CancellationToken, Task> immediateOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        DeliveryAuthorizationState state;
        lock (_authorizationSync)
        {
            if (!_authorizations.TryGetValue(
                    authorization.OperationId,
                    out state!) ||
                !ReferenceEquals(state.Authorization, authorization))
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.AuthorizationNotFound);
            }

            if (state.Lifecycle != DeliveryAuthorizationLifecycle.Active)
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.AuthorizationAlreadyConsumed);
            }

            // Even malformed attempts spend an otherwise valid authorization.
            state.Lifecycle = DeliveryAuthorizationLifecycle.Consumed;
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(immediateOperation);
            ContentDigest suppliedRequestDigest = ComputeDeliveryRequestDigest(request);
            if (!ReferenceEquals(state.Request, request) ||
                state.RequestDigest != suppliedRequestDigest ||
                authorization.RequestDigest != suppliedRequestDigest)
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.AuthorizationRequestMismatch);
            }

            state.Lifecycle = DeliveryAuthorizationLifecycle.Consuming;
        }

        OpenedDeliveryArtifacts? opened = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            opened = await EvaluateAndOpenDeliveryArtifactsAsync(
                request,
                consuming: true,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.Evidence.EvidenceDigest != authorization.EvidenceDigest)
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.AuthorizationStale);
            }

            // Do not use a deferred read transaction: two consumers must not both
            // pass the ready/revision check before either performs its side effect.
            using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
                _databasePath);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            cancellationToken.ThrowIfCancellationRequested();
            DeliveryEvidence finalEvidence = CaptureDeliveryEvidenceChecked(
                connection, transaction, request, consuming: true);
            if (finalEvidence.EvidenceDigest != authorization.EvidenceDigest)
            {
                throw Failure(QuickStartDeliveryFailureReason.AuthorizationStale);
            }

            var context = new QuickStartLocalInternalDeliveryContext(
                authorization,
                finalEvidence.Workflow,
                opened.SourceArtifacts,
                opened.RenderArtifact);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task operation = immediateOperation(context, cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The quick-start delivery callback must return a task.");
                await operation.ConfigureAwait(false);
            }
            finally
            {
                context.Expire();
            }

            QuickStartWorkflow receipt;
            try
            {
                // A successful callback followed by cancellation still needs reconciliation.
                cancellationToken.ThrowIfCancellationRequested();
                receipt = AppendAuthorizedDeliveryReceipt(
                    connection,
                    transaction,
                    request,
                    authorization.EvidenceDigest);
                transaction.Commit();
            }
            catch (Exception exception)
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.ReceiptPersistenceFailed,
                    reconciliationRequired: true,
                    exception);
            }

            return receipt;
        }
        finally
        {
            MarkDeliveryAuthorizationConsumed(state);
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public QuickStartProjectSnapshot GetCurrentProjectSnapshot(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        ProjectEvidence project = ReadExactProject(
            connection,
            transaction,
            projectId);
        transaction.Commit();
        return project.Snapshot;
    }

    private static QuickStartWorkflow AppendCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedEvent prepared,
        QuickStartWorkflow? current,
        int expectedRevision)
    {
        ValidateCurrentProjectSnapshot(connection, transaction, prepared.Event);
        ValidateStoredEvaluation(
            connection, transaction, prepared.Event, current?.Events[^1]);
        QuickStartWorkflow next = current is null
            ? QuickStartWorkflow.Reduce([prepared.Event])
            : current.Append(prepared.Event);

        if (current is null)
        {
            InsertSnapshot(connection, transaction, next);
            InsertEvent(connection, transaction, prepared, next.Revision);
        }
        else
        {
            InsertEvent(connection, transaction, prepared, next.Revision);
            UpdateSnapshot(connection, transaction, next, expectedRevision);
        }

        QuickStartWorkflow stored = Read(
            connection,
            transaction,
            next.Binding)
            ?? throw InvalidStoredData();
        if (!string.Equals(
                stored.CanonicalJson,
                next.CanonicalJson,
                StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        return stored;
    }

    public QuickStartWorkflow? Get(QuickStartWorkflowBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        QuickStartWorkflow? workflow = Read(connection, transaction, binding);
        transaction.Commit();
        return workflow;
    }

    public QuickStartWorkflow? GetForVersion(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        QuickStartWorkflowBinding? binding = ReadVersionBinding(
            connection,
            transaction,
            projectId,
            compositionVersionId);
        QuickStartWorkflow? workflow = binding is null
            ? null
            : Read(connection, transaction, binding);
        transaction.Commit();
        return workflow;
    }

    public IReadOnlyList<QuickStartWorkflow> ListForProject(
        EntityId<ProjectIdTag> projectId,
        int maximumCount = 100)
    {
        ArgumentNullException.ThrowIfNull(projectId);
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
        var bindings = new List<QuickStartWorkflowBinding>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    CAST(project_id AS BLOB),
                    CAST(composition_version_id AS BLOB),
                    CAST(composition_version_digest AS BLOB)
                FROM quick_start_workflows
                WHERE project_id = $project_id
                ORDER BY composition_version_id, composition_version_digest
                LIMIT $maximum_count;
                """;
            SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
            SqliteStoreSupport.AddInteger(command, "$maximum_count", maximumCount);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                bindings.Add(new QuickStartWorkflowBinding(
                    ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 0)),
                    ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 1)),
                    ParseDigest(ReadRequiredText(reader, 2))));
            }
        }

        var workflows = new List<QuickStartWorkflow>(bindings.Count);
        foreach (QuickStartWorkflowBinding binding in bindings)
        {
            QuickStartWorkflow workflow = Read(connection, transaction, binding)
                ?? throw InvalidStoredData();
            if (workflow.Binding.ProjectId != projectId)
            {
                throw InvalidStoredData();
            }

            workflows.Add(workflow);
        }

        transaction.Commit();
        return Array.AsReadOnly(workflows.ToArray());
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
        var bindings = new List<QuickStartWorkflowBinding>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    CAST(project_id AS BLOB),
                    CAST(composition_version_id AS BLOB),
                    CAST(composition_version_digest AS BLOB)
                FROM quick_start_workflows
                ORDER BY project_id, composition_version_id, composition_version_digest;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                bindings.Add(new QuickStartWorkflowBinding(
                    ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 0)),
                    ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 1)),
                    ParseDigest(ReadRequiredText(reader, 2))));
            }
        }

        long expectedEventCount = 0;
        foreach (QuickStartWorkflowBinding binding in bindings)
        {
            QuickStartWorkflow workflow = Read(connection, transaction, binding)
                ?? throw InvalidStoredData();
            expectedEventCount = checked(expectedEventCount + workflow.Events.Count);
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(CASE
                        WHEN workflow.project_id IS NULL THEN 1
                        ELSE 0
                    END), 0)
                FROM quick_start_state_events AS event
                LEFT JOIN quick_start_workflows AS workflow
                  ON workflow.project_id = event.project_id
                 AND workflow.composition_version_id = event.composition_version_id
                 AND workflow.composition_version_digest = event.composition_version_digest;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                reader.GetValue(0) is not long actualEventCount ||
                reader.GetValue(1) is not long orphanEventCount ||
                actualEventCount != expectedEventCount ||
                orphanEventCount != 0 ||
                reader.Read())
            {
                throw InvalidStoredData();
            }
        }

        transaction.Commit();
    }

    public IReadOnlyList<LegacyReviewReference> ListLegacyReviewReferences(
        QuickStartWorkflowBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        EnsureVersionBindingExists(connection, transaction, binding);

        var references = new List<LegacyReviewReference>();
        ReadLegacyReviewStates(connection, transaction, binding, references);
        ReadLegacyComments(connection, transaction, binding, references);
        ReadLegacyApprovals(connection, transaction, binding, references);
        transaction.Commit();
        return Array.AsReadOnly(references
            .OrderBy(static reference => reference.OccurredAt)
            .ThenBy(static reference => reference.SourceId, StringComparer.Ordinal)
            .ToArray());
    }

    private ArtifactStore EnsureArtifactStoreConfigured() =>
        _artifactStore ?? throw Failure(
            QuickStartDeliveryFailureReason.WorkspaceNotConfigured);

    private void ReserveDeliveryIdentities(
        QuickStartLocalInternalDeliveryRequest request)
    {
        lock (_authorizationSync)
        {
            if (!_usedOperationIds.Add(request.OperationId))
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.DuplicateOperationId);
            }

            if (!_reservedReceiptEventIds.Add(request.ReceiptEventId))
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.DuplicateReceiptEventId);
            }
        }
    }

    private void MarkDeliveryAuthorizationConsumed(
        DeliveryAuthorizationState state)
    {
        lock (_authorizationSync)
        {
            state.Lifecycle = DeliveryAuthorizationLifecycle.Consumed;
        }
    }

    private async Task<OpenedDeliveryArtifacts>
        EvaluateAndOpenDeliveryArtifactsAsync(
            QuickStartLocalInternalDeliveryRequest request,
            bool consuming,
            CancellationToken cancellationToken)
    {
        ArtifactStore artifactStore = EnsureArtifactStoreConfigured();
        DeliveryEvidence before = CaptureDeliveryEvidence(request, consuming);
        var sourceStreams = new List<ValidatedArtifactReadStream>(
            before.SourceArtifacts.Count);
        ValidatedArtifactReadStream? renderStream = null;
        try
        {
            foreach (StoredArtifact sourceArtifact in before.SourceArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatedArtifactReadStream sourceStream =
                    await artifactStore.OpenValidatedReadAsync(
                        sourceArtifact.Id,
                        cancellationToken).ConfigureAwait(false);
                if (!HasSameArtifact(sourceArtifact, sourceStream.RegisteredArtifact))
                {
                    await sourceStream.DisposeAsync().ConfigureAwait(false);
                    throw Failure(consuming
                        ? QuickStartDeliveryFailureReason.AuthorizationStale
                        : QuickStartDeliveryFailureReason.ArtifactValidationFailed);
                }

                sourceStreams.Add(sourceStream);
            }

            cancellationToken.ThrowIfCancellationRequested();
            renderStream = await artifactStore.OpenValidatedReadAsync(
                before.RenderArtifact.Id,
                cancellationToken).ConfigureAwait(false);
            if (!HasSameArtifact(
                    before.RenderArtifact,
                    renderStream.RegisteredArtifact))
            {
                throw Failure(consuming
                    ? QuickStartDeliveryFailureReason.AuthorizationStale
                    : QuickStartDeliveryFailureReason.ArtifactValidationFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeliveryEvidence after = CaptureDeliveryEvidence(request, consuming);
            if (before.EvidenceDigest != after.EvidenceDigest ||
                sourceStreams.Count != after.SourceArtifacts.Count ||
                sourceStreams.Where((stream, index) =>
                    !HasSameArtifact(
                        after.SourceArtifacts[index],
                        stream.RegisteredArtifact)).Any() ||
                !HasSameArtifact(
                    after.RenderArtifact,
                    renderStream.RegisteredArtifact))
            {
                throw Failure(
                    QuickStartDeliveryFailureReason.AuthorizationStale);
            }

            var opened = new OpenedDeliveryArtifacts(
                after,
                Array.AsReadOnly(sourceStreams.ToArray()),
                renderStream);
            renderStream = null;
            sourceStreams.Clear();
            return opened;
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                consuming
                    ? QuickStartDeliveryFailureReason.AuthorizationStale
                    : QuickStartDeliveryFailureReason.ArtifactValidationFailed,
                innerException: exception);
        }
        finally
        {
            if (renderStream is not null)
            {
                await renderStream.DisposeAsync().ConfigureAwait(false);
            }

            foreach (ValidatedArtifactReadStream sourceStream in sourceStreams)
            {
                await sourceStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private DeliveryEvidence CaptureDeliveryEvidence(
        QuickStartLocalInternalDeliveryRequest request,
        bool consuming)
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        DeliveryEvidence evidence = CaptureDeliveryEvidenceChecked(
            connection, transaction, request, consuming);
        transaction.Commit();
        return evidence;
    }

    private static DeliveryEvidence CaptureDeliveryEvidenceChecked(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartLocalInternalDeliveryRequest request,
        bool consuming)
    {
        try
        {
            return CaptureDeliveryEvidence(
                connection,
                transaction,
                request);
        }
        catch (DeliveryEvidenceException exception)
        {
            throw Failure(
                exception.Kind switch
                {
                    DeliveryEvidenceFailureKind.DuplicateReceipt =>
                        QuickStartDeliveryFailureReason.DuplicateReceiptEventId,
                    DeliveryEvidenceFailureKind.Revoked when consuming =>
                        QuickStartDeliveryFailureReason.AuthorizationRevoked,
                    DeliveryEvidenceFailureKind.Stale when consuming =>
                        QuickStartDeliveryFailureReason.AuthorizationStale,
                    DeliveryEvidenceFailureKind.Artifact when consuming =>
                        QuickStartDeliveryFailureReason.AuthorizationStale,
                    DeliveryEvidenceFailureKind.Artifact =>
                        QuickStartDeliveryFailureReason.ArtifactValidationFailed,
                    _ => QuickStartDeliveryFailureReason.EvidenceInvalid,
                },
                innerException: exception);
        }
        catch (QuickStartDeliveryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure(
                consuming
                    ? QuickStartDeliveryFailureReason.AuthorizationStale
                    : QuickStartDeliveryFailureReason.EvidenceInvalid,
                innerException: exception);
        }
    }

    private static DeliveryEvidence CaptureDeliveryEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartLocalInternalDeliveryRequest request)
    {
        if (ReadEventIdentity(connection, transaction, request.ReceiptEventId) is not null)
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.DuplicateReceipt);
        }

        QuickStartWorkflow workflow = Read(
            connection,
            transaction,
            request.Binding)
            ?? throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        if (!SameBinding(workflow.Binding, request.Binding) ||
            workflow.Revision != request.ExpectedRevision ||
            workflow.State is not (
                QuickStartWorkflowState.Ready or
                QuickStartWorkflowState.ReadyWithWarnings) ||
            request.ReceiptOccurredAt < workflow.Events[^1].OccurredAt)
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        }

        ProjectEvidence project = ReadExactProject(
            connection,
            transaction,
            workflow.Binding.ProjectId);
        if (project.Project.PublicationScope != PublicationScope.InternalOnly ||
            project.Project.Assets.Count == 0 ||
            project.Project.Assets.Any(static asset =>
                asset.Rights.Status != RightsStatus.Approved ||
                string.IsNullOrWhiteSpace(asset.Rights.EvidenceReference) ||
                asset.Rights.EvidenceReference.Contains('\0') ||
                string.IsNullOrWhiteSpace(asset.Rights.AllowedUse) ||
                asset.Rights.AllowedUse.Contains('\0')))
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Revoked);
        }

        if (!QuickStartWorkflowContract.SameProjectSnapshot(
                workflow.ProjectSnapshot,
                project.Snapshot))
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        }

        if (workflow.SettingsSnapshotDigest is null ||
            workflow.EvaluationId is null ||
            workflow.EvaluationAggregateStatus is null ||
            workflow.EvaluationCanonicalDigest is null ||
            workflow.ArtifactId is null ||
            workflow.ArtifactDigest is null)
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        }

        AutomatedEvaluationRun evaluation = AutomatedEvaluationStore.Read(
            connection,
            transaction,
            workflow.EvaluationId)
            ?? throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        if (evaluation.ProjectId != workflow.Binding.ProjectId ||
            evaluation.CompositionVersionId != workflow.Binding.CompositionVersionId ||
            evaluation.CompositionVersionDigest !=
                workflow.Binding.CompositionVersionDigest ||
            evaluation.SettingsSnapshotDigest != workflow.SettingsSnapshotDigest ||
            evaluation.EvaluationId != workflow.EvaluationId ||
            evaluation.AggregateStatus != workflow.EvaluationAggregateStatus ||
            evaluation.CanonicalDigest != workflow.EvaluationCanonicalDigest ||
            evaluation.ArtifactId != workflow.ArtifactId ||
            evaluation.ArtifactDigest != workflow.ArtifactDigest ||
            evaluation.OutputDigest != workflow.ArtifactDigest ||
            QuickStartEvaluationPolicy.DetermineCompletedState(evaluation) !=
                workflow.State)
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        }

        var sourceArtifacts = new List<StoredArtifact>(
            project.Project.Assets.Count);
        foreach (Asset asset in project.Project.Assets)
        {
            EntityId<ArtifactIdTag> sourceId =
                EntityId<ArtifactIdTag>.Parse(asset.Id.ToString());
            StoredArtifact source = ArtifactStore.ReadArtifact(
                connection,
                transaction,
                sourceId)
                ?? throw new DeliveryEvidenceException(
                    DeliveryEvidenceFailureKind.Artifact);
            if (source.Id != sourceId ||
                source.ProjectId != workflow.Binding.ProjectId ||
                source.Kind != ArtifactKind.Original ||
                source.ContentDigest != asset.ContentDigest ||
                source.ByteSize != asset.ByteSize)
            {
                throw new DeliveryEvidenceException(
                    DeliveryEvidenceFailureKind.Artifact);
            }

            sourceArtifacts.Add(source);
        }

        EntityId<ArtifactIdTag> renderId =
            EntityId<ArtifactIdTag>.Parse(workflow.ArtifactId.ToString());
        StoredArtifact render = ArtifactStore.ReadArtifact(
            connection,
            transaction,
            renderId)
            ?? throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Artifact);
        if (render.Id != renderId ||
            render.ProjectId != workflow.Binding.ProjectId ||
            render.Kind != ArtifactKind.Render ||
            render.ContentDigest != workflow.ArtifactDigest)
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Artifact);
        }

        ContentDigest requestDigest = ComputeDeliveryRequestDigest(request);
        ContentDigest evidenceDigest = ComputeDeliveryEvidenceDigest(
            requestDigest,
            workflow,
            project,
            evaluation,
            sourceArtifacts,
            render);
        return new DeliveryEvidence(
            workflow,
            project,
            evaluation,
            Array.AsReadOnly(sourceArtifacts.ToArray()),
            render,
            evidenceDigest);
    }

    private static QuickStartWorkflow AppendAuthorizedDeliveryReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartLocalInternalDeliveryRequest request,
        ContentDigest expectedEvidenceDigest)
    {
        DeliveryEvidence evidence = CaptureDeliveryEvidence(
            connection,
            transaction,
            request);
        if (evidence.EvidenceDigest != expectedEvidenceDigest)
        {
            throw new DeliveryEvidenceException(
                DeliveryEvidenceFailureKind.Stale);
        }

        QuickStartWorkflow workflow = evidence.Workflow;
        var facts = new QuickStartExportFacts(
            workflow.Binding,
            evidence.Project.Snapshot,
            evidence.Project.Project.PublicationScope,
            rightsAllowedForInternalUse: true,
            externalDestinationRequested: false,
            QuickStartExportDestinationKind.LocalInternalDelivery,
            workflow.State,
            workflow.Revision,
            workflow.SettingsSnapshotDigest!,
            workflow.EvaluationId!,
            workflow.EvaluationAggregateStatus!.Value,
            workflow.EvaluationCanonicalDigest!,
            workflow.ArtifactId!,
            workflow.ArtifactDigest!);
        PreparedEvent prepared = Prepare(QuickStartWorkflowEvent.Exported(
            request.ReceiptEventId,
            workflow,
            request.Actor,
            request.ReceiptOccurredAt,
            facts));
        QuickStartWorkflow receipt = AppendCore(
            connection,
            transaction,
            prepared,
            workflow,
            request.ExpectedRevision);
        return receipt;
    }

    private static ContentDigest ComputeDeliveryRequestDigest(
        QuickStartLocalInternalDeliveryRequest request)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendHashValue(hash, "hubradio.quick-start.delivery-request.v1");
        AppendHashValue(hash, request.OperationId.ToString());
        AppendHashValue(hash, request.ReceiptEventId.ToString());
        AppendHashValue(hash, request.Binding.ProjectId.ToString());
        AppendHashValue(hash, request.Binding.CompositionVersionId.ToString());
        AppendHashValue(hash, request.Binding.CompositionVersionDigest.Hex);
        AppendHashValue(
            hash,
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
        AppendHashValue(hash, request.Actor.Id);
        AppendHashValue(hash, FormatTimestamp(request.ReceiptOccurredAt));
        return ParseDigest(Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant());
    }

    private static ContentDigest ComputeDeliveryEvidenceDigest(
        ContentDigest requestDigest,
        QuickStartWorkflow workflow,
        ProjectEvidence project,
        AutomatedEvaluationRun evaluation,
        IReadOnlyList<StoredArtifact> sourceArtifacts,
        StoredArtifact renderArtifact)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendHashValue(hash, "hubradio.quick-start.delivery-evidence.v1");
        AppendHashValue(hash, requestDigest.Hex);
        AppendHashValue(hash, workflow.CanonicalJson);
        AppendHashValue(hash, project.CanonicalJson);
        AppendHashValue(hash, evaluation.CanonicalJson);
        foreach (StoredArtifact sourceArtifact in sourceArtifacts)
        {
            AppendArtifactEvidence(hash, sourceArtifact);
        }

        AppendArtifactEvidence(hash, renderArtifact);
        return ParseDigest(Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant());
    }

    private static void AppendArtifactEvidence(
        IncrementalHash hash,
        StoredArtifact artifact)
    {
        AppendHashValue(hash, artifact.Id.ToString());
        AppendHashValue(hash, artifact.ProjectId.ToString());
        AppendHashValue(hash, artifact.Kind.ToString());
        AppendHashValue(hash, artifact.StorageReference);
        AppendHashValue(hash, artifact.ContentDigest.Hex);
        AppendHashValue(
            hash,
            artifact.ByteSize.ToString(CultureInfo.InvariantCulture));
        AppendHashValue(hash, FormatTimestamp(artifact.CreatedAt));
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        byte[] utf8 = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, utf8.Length);
        hash.AppendData(length);
        hash.AppendData(utf8);
    }

    private static bool HasSameArtifact(
        StoredArtifact left,
        StoredArtifact right) =>
        left.Id == right.Id &&
        left.ProjectId == right.ProjectId &&
        left.Kind == right.Kind &&
        string.Equals(
            left.StorageReference,
            right.StorageReference,
            StringComparison.Ordinal) &&
        left.ContentDigest == right.ContentDigest &&
        left.ByteSize == right.ByteSize &&
        left.CreatedAt == right.CreatedAt;

    private static ProjectEvidence ReadExactProject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(id AS BLOB),
                revision,
                CAST(publication_scope AS BLOB),
                CAST(deadline AS BLOB),
                CAST(updated_at AS BLOB),
                CAST(payload AS BLOB)
            FROM projects
            WHERE id = $project_id;
            """;
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "A local-internal export receipt requires a stored project.");
        }

        EntityId<ProjectIdTag> storedId = ParseEntityId<ProjectIdTag>(
            ReadRequiredText(reader, 0));
        int revision = ReadNonnegativeInt32(reader, 1);
        string scopeText = ReadRequiredText(reader, 2);
        if (!Enum.TryParse(
                scopeText,
                ignoreCase: false,
                out PublicationScope storedScope) ||
            !Enum.IsDefined(storedScope) ||
            !string.Equals(scopeText, storedScope.ToString(), StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        DateTimeOffset? storedDeadline = ReadOptionalText(reader, 3) is string deadlineText
            ? ParseTimestamp(deadlineText)
            : null;
        _ = ParseTimestamp(ReadRequiredText(reader, 4));
        string canonicalJson = SqliteStoreSupport.ReadCanonicalPayload(
            reader,
            5,
            "project");
        if (reader.Read())
        {
            throw InvalidStoredData();
        }

        Project project = ParseProjectSnapshot(canonicalJson);
        if (storedId != projectId ||
            project.Id != storedId ||
            project.PublicationScope != storedScope ||
            project.Deadline != storedDeadline ||
            !string.Equals(
                ProjectSnapshotJson.WriteProject(project),
                canonicalJson,
                StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        return new ProjectEvidence(
            project,
            new QuickStartProjectSnapshot(
                revision,
                ContentDigest.Compute(StrictUtf8.GetBytes(canonicalJson))),
            canonicalJson);
    }

    private static Project ParseProjectSnapshot(string canonicalJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(canonicalJson);
            JsonElement root = document.RootElement;
            RequireExactJsonObject(
                root,
                "project snapshot",
                [
                    "id",
                    "contentName",
                    "purpose",
                    "audience",
                    "publicationScope",
                    "assets",
                    "performers",
                ],
                optionalProperty: "deadline");
            EntityId<ProjectIdTag> projectId = ReadJsonEntityId<ProjectIdTag>(
                root,
                "id",
                "project snapshot");
            PublicationScope publicationScope = ReadProjectPublicationScope(root);
            DateTimeOffset? deadline = root.TryGetProperty(
                "deadline",
                out JsonElement deadlineElement)
                ? ReadJsonTimestamp(deadlineElement, "project snapshot.deadline")
                : null;
            Asset[] assets = ReadProjectAssets(
                RequireJsonProperty(root, "assets", "project snapshot"));
            Performer[] performers = ReadProjectPerformers(
                RequireJsonProperty(root, "performers", "project snapshot"));
            return new Project(
                projectId,
                ReadJsonString(root, "contentName", "project snapshot"),
                ReadJsonString(root, "purpose", "project snapshot"),
                ReadJsonString(root, "audience", "project snapshot"),
                publicationScope,
                deadline,
                assets,
                performers);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or OverflowException)
        {
            throw InvalidStoredData(exception);
        }
    }

    private static Asset[] ReadProjectAssets(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The project snapshot assets value must be an array.");
        }

        var assets = new List<Asset>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            const string Context = "project snapshot.assets[]";
            RequireExactJsonObject(
                item,
                Context,
                ["id", "fileName", "mediaType", "contentDigest", "byteSize", "rights"]);
            JsonElement rightsElement = RequireJsonProperty(item, "rights", Context);
            RequireExactJsonObject(
                rightsElement,
                string.Concat(Context, ".rights"),
                ["status", "allowedUse", "externalPublicationAllowed"],
                optionalProperty: "evidenceReference");
            string rightsContext = string.Concat(Context, ".rights");
            RightsStatus rightsStatus = ReadRightsStatus(rightsElement, rightsContext);
            string? evidenceReference = rightsElement.TryGetProperty(
                "evidenceReference",
                out JsonElement evidenceElement)
                ? ReadJsonString(evidenceElement, string.Concat(rightsContext, ".evidenceReference"))
                : null;
            var rights = new RightsRecord(
                rightsStatus,
                evidenceReference,
                ReadJsonString(rightsElement, "allowedUse", rightsContext),
                ReadJsonBoolean(
                    rightsElement,
                    "externalPublicationAllowed",
                    rightsContext));
            assets.Add(new Asset(
                ReadJsonEntityId<AssetIdTag>(item, "id", Context),
                ReadJsonString(item, "fileName", Context),
                ReadJsonString(item, "mediaType", Context),
                ReadJsonDigest(item, "contentDigest", Context),
                ReadJsonPositiveInt64(item, "byteSize", Context),
                rights));
        }

        return assets.ToArray();
    }

    private static Performer[] ReadProjectPerformers(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The project snapshot performers value must be an array.");
        }

        var performers = new List<Performer>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            const string Context = "project snapshot.performers[]";
            RequireExactJsonObject(
                item,
                Context,
                ["id", "displayName", "role"],
                optionalProperty: "titleConfirmedAt");
            DateTimeOffset? titleConfirmedAt = item.TryGetProperty(
                "titleConfirmedAt",
                out JsonElement confirmedElement)
                ? ReadJsonTimestamp(
                    confirmedElement,
                    string.Concat(Context, ".titleConfirmedAt"))
                : null;
            performers.Add(new Performer(
                ReadJsonEntityId<PerformerIdTag>(item, "id", Context),
                ReadJsonString(item, "displayName", Context),
                ReadJsonString(item, "role", Context),
                titleConfirmedAt));
        }

        return performers.ToArray();
    }

    private static PublicationScope ReadProjectPublicationScope(JsonElement root) =>
        ReadJsonString(root, "publicationScope", "project snapshot") switch
        {
            "internalOnly" => PublicationScope.InternalOnly,
            "specificGroupOnly" => PublicationScope.SpecificGroupOnly,
            "externalCandidate" => PublicationScope.ExternalCandidate,
            "externalPublicationApproved" =>
                PublicationScope.ExternalPublicationApproved,
            "publicationForbidden" => PublicationScope.PublicationForbidden,
            "pendingDecision" => PublicationScope.PendingDecision,
            _ => throw new JsonException(
                "The project snapshot publication scope is invalid."),
        };

    private static RightsStatus ReadRightsStatus(
        JsonElement rights,
        string context) => ReadJsonString(rights, "status", context) switch
        {
            "unconfirmed" => RightsStatus.Unconfirmed,
            "approved" => RightsStatus.Approved,
            "rejected" => RightsStatus.Rejected,
            _ => throw new JsonException("The project asset rights status is invalid."),
        };

    private static void ValidateCurrentProjectSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowEvent workflowEvent)
    {
        if (workflowEvent.ProjectSnapshot is null)
        {
            return;
        }

        ProjectEvidence current = ReadExactProject(
            connection,
            transaction,
            workflowEvent.Binding.ProjectId);
        if (!QuickStartWorkflowContract.SameProjectSnapshot(
                workflowEvent.ProjectSnapshot,
                current.Snapshot))
        {
            throw new InvalidOperationException(
                "The quick-start event project snapshot does not match the current project row.");
        }
    }

    private static void RequireExactJsonObject(
        JsonElement value,
        string context,
        IReadOnlyCollection<string> requiredProperties,
        string? optionalProperty = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The {context} value must be an object.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!observed.Add(property.Name) ||
                !requiredProperties.Contains(property.Name) &&
                !string.Equals(property.Name, optionalProperty, StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Property '{property.Name}' is not allowed in {context}.");
            }
        }

        if (requiredProperties.Any(property => !observed.Contains(property)) ||
            observed.Count != requiredProperties.Count +
                (optionalProperty is not null && observed.Contains(optionalProperty) ? 1 : 0))
        {
            throw new JsonException($"The {context} object is incomplete.");
        }
    }

    private static JsonElement RequireJsonProperty(
        JsonElement parent,
        string propertyName,
        string context) => parent.TryGetProperty(propertyName, out JsonElement value)
        ? value
        : throw new JsonException(
            $"The {context} object is missing property '{propertyName}'.");

    private static string ReadJsonString(
        JsonElement parent,
        string propertyName,
        string context) => ReadJsonString(
            RequireJsonProperty(parent, propertyName, context),
            string.Concat(context, ".", propertyName));

    private static string ReadJsonString(JsonElement value, string context) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is string text
            ? text
            : throw new JsonException($"The {context} value must be a string.");

    private static bool ReadJsonBoolean(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireJsonProperty(parent, propertyName, context);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new JsonException(
                $"The {context}.{propertyName} value must be Boolean.");
    }

    private static EntityId<TTag> ReadJsonEntityId<TTag>(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadJsonString(parent, propertyName, context);
        return EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) &&
            string.Equals(text, id.ToString(), StringComparison.Ordinal)
            ? id
            : throw new JsonException(
                $"The {context}.{propertyName} value must be a canonical entity ID.");
    }

    private static ContentDigest ReadJsonDigest(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadJsonString(parent, propertyName, context);
        return ContentDigest.TryParse(text, out ContentDigest? digest) &&
            string.Equals(text, digest.Hex, StringComparison.Ordinal)
            ? digest
            : throw new JsonException(
                $"The {context}.{propertyName} value must be a canonical digest.");
    }

    private static long ReadJsonPositiveInt64(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireJsonProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed <= 0 ||
            parsed > long.MaxValue)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a positive Int64.");
        }

        return decimal.ToInt64(parsed);
    }

    private static DateTimeOffset ReadJsonTimestamp(
        JsonElement value,
        string context)
    {
        string text = ReadJsonString(value, context);
        if (!DateTimeOffset.TryParseExact(
                text,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp) ||
            timestamp.Offset != TimeSpan.Zero ||
            !string.Equals(text, FormatTimestamp(timestamp), StringComparison.Ordinal))
        {
            throw new JsonException($"The {context} value must be a canonical UTC timestamp.");
        }

        return timestamp;
    }

    private static PreparedEvent Prepare(QuickStartWorkflowEvent workflowEvent)
    {
        byte[] canonicalUtf8;
        try
        {
            canonicalUtf8 = StrictUtf8.GetBytes(workflowEvent.CanonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "The quick-start workflow event must contain valid Unicode.",
                nameof(workflowEvent),
                exception);
        }

        QuickStartWorkflowEvent parsed;
        try
        {
            parsed = QuickStartWorkflowEvent.ParseStoredCanonical(
                workflowEvent.CanonicalJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The quick-start workflow event is invalid.",
                nameof(workflowEvent),
                exception);
        }

        if (parsed.CanonicalDigest != workflowEvent.CanonicalDigest ||
            !canonicalUtf8.AsSpan().SequenceEqual(
                StrictUtf8.GetBytes(parsed.CanonicalJson)))
        {
            throw new ArgumentException(
                "The quick-start workflow event digest is invalid.",
                nameof(workflowEvent));
        }

        return new PreparedEvent(parsed, canonicalUtf8);
    }

    private static void InsertSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflow workflow)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                $composition_version_id,
                $composition_version_digest,
                $current_state,
                $revision,
                $project_revision,
                $project_canonical_digest,
                $settings_snapshot_digest,
                $evaluation_id,
                $evaluation_aggregate_status,
                $evaluation_canonical_digest,
                $artifact_id,
                $artifact_digest,
                $self_review_count,
                $change_request_count,
                $event_chain_digest,
                $last_event_id,
                $last_event_digest,
                $canonical_digest,
                $canonical_payload
            );
            """;
        AddSnapshotParameters(command, workflow);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The quick-start workflow snapshot was not inserted.");
        }
    }

    private static void UpdateSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflow workflow,
        int expectedRevision)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE quick_start_workflows
            SET current_state = $current_state,
                revision = $revision,
                project_revision = $project_revision,
                project_canonical_digest = $project_canonical_digest,
                settings_snapshot_digest = $settings_snapshot_digest,
                evaluation_id = $evaluation_id,
                evaluation_aggregate_status = $evaluation_aggregate_status,
                evaluation_canonical_digest = $evaluation_canonical_digest,
                artifact_id = $artifact_id,
                artifact_digest = $artifact_digest,
                self_review_count = $self_review_count,
                change_request_count = $change_request_count,
                event_chain_digest = $event_chain_digest,
                last_event_id = $last_event_id,
                last_event_digest = $last_event_digest,
                canonical_digest = $canonical_digest,
                canonical_payload = $canonical_payload
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest
              AND revision = $expected_revision;
            """;
        AddSnapshotParameters(command, workflow);
        SqliteStoreSupport.AddInteger(command, "$expected_revision", expectedRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new StoreConcurrencyException(
                EntityKind,
                workflow.Binding.CompositionVersionId.ToString(),
                expectedRevision,
                ReadCurrentRevision(connection, transaction, workflow.Binding));
        }
    }

    private static void AddSnapshotParameters(
        SqliteCommand command,
        QuickStartWorkflow workflow)
    {
        AddBindingParameters(command, workflow.Binding);
        SqliteStoreSupport.AddText(command, "$current_state", ToWireName(workflow.State));
        SqliteStoreSupport.AddInteger(command, "$revision", workflow.Revision);
        AddNullableInteger(
            command,
            "$project_revision",
            workflow.ProjectSnapshot?.Revision);
        SqliteStoreSupport.AddNullableText(
            command,
            "$project_canonical_digest",
            workflow.ProjectSnapshot?.CanonicalPayloadDigest.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$settings_snapshot_digest",
            workflow.SettingsSnapshotDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_id",
            workflow.EvaluationId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_aggregate_status",
            workflow.EvaluationAggregateStatus is null
                ? null
                : ToWireName(workflow.EvaluationAggregateStatus.Value));
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_canonical_digest",
            workflow.EvaluationCanonicalDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$artifact_id",
            workflow.ArtifactId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$artifact_digest",
            workflow.ArtifactDigest?.Hex);
        SqliteStoreSupport.AddInteger(command, "$self_review_count", workflow.SelfReviewCount);
        SqliteStoreSupport.AddInteger(
            command,
            "$change_request_count",
            workflow.ChangeRequestCount);
        SqliteStoreSupport.AddText(
            command,
            "$event_chain_digest",
            workflow.EventChainDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$last_event_id",
            workflow.Events[^1].EventId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$last_event_digest",
            workflow.Events[^1].CanonicalDigest.Hex);
        SqliteStoreSupport.AddText(
            command,
            "$canonical_digest",
            workflow.CanonicalDigest.Hex);
        SqliteStoreSupport.AddBlob(
            command,
            "$canonical_payload",
            StrictUtf8.GetBytes(workflow.CanonicalJson));
    }

    private static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedEvent prepared,
        int revision)
    {
        QuickStartWorkflowEvent workflowEvent = prepared.Event;
        QuickStartExportFacts? export = workflowEvent.ExportFacts;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                $composition_version_id,
                $composition_version_digest,
                $revision,
                $event_id,
                $kind,
                $actor_kind,
                $actor_id,
                $occurred_at,
                $project_revision,
                $project_canonical_digest,
                $settings_snapshot_digest,
                $evaluation_id,
                $evaluation_evaluated_at,
                $evaluation_aggregate_status,
                $evaluation_canonical_digest,
                $artifact_id,
                $artifact_digest,
                $evaluation_result_state,
                $reason_code,
                $publication_scope,
                $rights_allowed_for_internal_use,
                $external_destination_requested,
                $destination_kind,
                $authorized_state,
                $authorized_revision,
                $canonical_digest,
                $canonical_payload
            );
            """;
        AddBindingParameters(command, workflowEvent.Binding);
        SqliteStoreSupport.AddInteger(command, "$revision", revision);
        SqliteStoreSupport.AddText(command, "$event_id", workflowEvent.EventId.ToString());
        SqliteStoreSupport.AddText(command, "$kind", workflowEvent.Kind.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$actor_kind",
            workflowEvent.Actor.Kind == QuickStartActorKind.User ? "user" : "system");
        SqliteStoreSupport.AddText(command, "$actor_id", workflowEvent.Actor.Id);
        SqliteStoreSupport.AddText(
            command,
            "$occurred_at",
            FormatTimestamp(workflowEvent.OccurredAt));
        AddNullableInteger(
            command,
            "$project_revision",
            workflowEvent.ProjectSnapshot?.Revision);
        SqliteStoreSupport.AddNullableText(
            command,
            "$project_canonical_digest",
            workflowEvent.ProjectSnapshot?.CanonicalPayloadDigest.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$settings_snapshot_digest",
            workflowEvent.SettingsSnapshotDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_id",
            workflowEvent.EvaluationId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_evaluated_at",
            workflowEvent.EvaluationEvaluatedAt is DateTimeOffset evaluatedAt
                ? FormatTimestamp(evaluatedAt)
                : null);
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_aggregate_status",
            workflowEvent.EvaluationAggregateStatus is null
                ? null
                : ToWireName(workflowEvent.EvaluationAggregateStatus.Value));
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_canonical_digest",
            workflowEvent.EvaluationCanonicalDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$artifact_id",
            workflowEvent.ArtifactId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$artifact_digest",
            workflowEvent.ArtifactDigest?.Hex);
        SqliteStoreSupport.AddNullableText(
            command,
            "$evaluation_result_state",
            workflowEvent.EvaluationResultState is null
                ? null
                : ToWireName(workflowEvent.EvaluationResultState.Value));
        SqliteStoreSupport.AddNullableText(command, "$reason_code", workflowEvent.ReasonCode);
        SqliteStoreSupport.AddNullableText(
            command,
            "$publication_scope",
            export?.PublicationScope.ToString());
        AddNullableInteger(
            command,
            "$rights_allowed_for_internal_use",
            export is null ? null : export.RightsAllowedForInternalUse ? 1 : 0);
        AddNullableInteger(
            command,
            "$external_destination_requested",
            export is null ? null : export.ExternalDestinationRequested ? 1 : 0);
        SqliteStoreSupport.AddNullableText(
            command,
            "$destination_kind",
            export is null ? null : "localInternalDelivery");
        SqliteStoreSupport.AddNullableText(
            command,
            "$authorized_state",
            export is null ? null : ToWireName(export.AuthorizedState));
        AddNullableInteger(command, "$authorized_revision", export?.AuthorizedRevision);
        SqliteStoreSupport.AddText(
            command,
            "$canonical_digest",
            workflowEvent.CanonicalDigest.Hex);
        SqliteStoreSupport.AddBlob(command, "$canonical_payload", prepared.CanonicalUtf8);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The quick-start workflow event was not appended.");
        }
    }

    private static QuickStartWorkflow? Read(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding)
    {
        StoredSnapshot? snapshot = ReadSnapshot(connection, transaction, binding);
        if (snapshot is null)
        {
            return null;
        }

        IReadOnlyList<QuickStartWorkflowEvent> events = ReadEvents(
            connection,
            transaction,
            binding);
        QuickStartWorkflow workflow;
        try
        {
            workflow = QuickStartWorkflow.Reduce(events);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw InvalidStoredData(exception);
        }

        if (!SameBinding(snapshot.Binding, workflow.Binding) ||
            snapshot.State != workflow.State ||
            snapshot.Revision != workflow.Revision ||
            !SameProjectSnapshot(snapshot.ProjectSnapshot, workflow.ProjectSnapshot) ||
            snapshot.SettingsSnapshotDigest != workflow.SettingsSnapshotDigest ||
            snapshot.EvaluationId != workflow.EvaluationId ||
            snapshot.EvaluationAggregateStatus !=
                workflow.EvaluationAggregateStatus ||
            snapshot.EvaluationCanonicalDigest != workflow.EvaluationCanonicalDigest ||
            snapshot.ArtifactId != workflow.ArtifactId ||
            snapshot.ArtifactDigest != workflow.ArtifactDigest ||
            snapshot.SelfReviewCount != workflow.SelfReviewCount ||
            snapshot.ChangeRequestCount != workflow.ChangeRequestCount ||
            snapshot.EventChainDigest != workflow.EventChainDigest ||
            snapshot.LastEventId != workflow.Events[^1].EventId ||
            snapshot.LastEventDigest != workflow.Events[^1].CanonicalDigest ||
            snapshot.CanonicalDigest != workflow.CanonicalDigest ||
            snapshot.CanonicalDigest != ContentDigest.Compute(snapshot.CanonicalPayload) ||
            !snapshot.CanonicalPayload.AsSpan().SequenceEqual(
                StrictUtf8.GetBytes(workflow.CanonicalJson)))
        {
            throw InvalidStoredData();
        }

        for (int index = 0; index < events.Count; index++)
        {
            // Historical validation uses the historical start, never today's project row.
            ValidateStoredEvaluation(
                connection, transaction, events[index], index == 0 ? null : events[index - 1]);
        }

        return workflow;
    }

    private static StoredSnapshot? ReadSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(project_id AS BLOB),
                CAST(composition_version_id AS BLOB),
                CAST(composition_version_digest AS BLOB),
                CAST(current_state AS BLOB),
                revision,
                project_revision,
                CAST(project_canonical_digest AS BLOB),
                CAST(settings_snapshot_digest AS BLOB),
                CAST(evaluation_id AS BLOB),
                CAST(evaluation_aggregate_status AS BLOB),
                CAST(evaluation_canonical_digest AS BLOB),
                CAST(artifact_id AS BLOB),
                CAST(artifact_digest AS BLOB),
                self_review_count,
                change_request_count,
                CAST(event_chain_digest AS BLOB),
                CAST(last_event_id AS BLOB),
                CAST(last_event_digest AS BLOB),
                CAST(canonical_digest AS BLOB),
                length(CAST(canonical_payload AS BLOB)),
                CAST(canonical_payload AS BLOB)
            FROM quick_start_workflows
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest;
            """;
        AddBindingParameters(command, binding);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var snapshot = new StoredSnapshot(
            new QuickStartWorkflowBinding(
                ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 0)),
                ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 1)),
                ParseDigest(ReadRequiredText(reader, 2))),
            ParseState(ReadRequiredText(reader, 3)),
            ReadPositiveInt32(reader, 4),
            ReadOptionalProjectSnapshot(reader, 5, 6),
            ParseOptionalDigest(ReadOptionalText(reader, 7)),
            ParseOptionalEntityId<AutomatedEvaluationIdTag>(ReadOptionalText(reader, 8)),
            ParseOptionalEvaluationStatus(ReadOptionalText(reader, 9)),
            ParseOptionalDigest(ReadOptionalText(reader, 10)),
            ParseOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                ReadOptionalText(reader, 11)),
            ParseOptionalDigest(ReadOptionalText(reader, 12)),
            ReadNonnegativeInt32(reader, 13),
            ReadNonnegativeInt32(reader, 14),
            ParseDigest(ReadRequiredText(reader, 15)),
            ParseEntityId<QuickStartWorkflowEventIdTag>(ReadRequiredText(reader, 16)),
            ParseDigest(ReadRequiredText(reader, 17)),
            ParseDigest(ReadRequiredText(reader, 18)),
            ReadPayload(reader, 19, 20, QuickStartWorkflow.MaximumCanonicalStateJsonUtf8Bytes));
        if (reader.Read() || !SameBinding(snapshot.Binding, binding))
        {
            throw InvalidStoredData();
        }

        return snapshot;
    }

    private static IReadOnlyList<QuickStartWorkflowEvent> ReadEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(project_id AS BLOB),
                CAST(composition_version_id AS BLOB),
                CAST(composition_version_digest AS BLOB),
                revision,
                CAST(event_id AS BLOB),
                CAST(kind AS BLOB),
                CAST(actor_kind AS BLOB),
                CAST(actor_id AS BLOB),
                CAST(occurred_at AS BLOB),
                project_revision,
                CAST(project_canonical_digest AS BLOB),
                CAST(settings_snapshot_digest AS BLOB),
                CAST(evaluation_id AS BLOB),
                CAST(evaluation_evaluated_at AS BLOB),
                CAST(evaluation_aggregate_status AS BLOB),
                CAST(evaluation_canonical_digest AS BLOB),
                CAST(artifact_id AS BLOB),
                CAST(artifact_digest AS BLOB),
                CAST(evaluation_result_state AS BLOB),
                CAST(reason_code AS BLOB),
                CAST(publication_scope AS BLOB),
                rights_allowed_for_internal_use,
                external_destination_requested,
                CAST(destination_kind AS BLOB),
                CAST(authorized_state AS BLOB),
                authorized_revision,
                CAST(canonical_digest AS BLOB),
                length(CAST(canonical_payload AS BLOB)),
                CAST(canonical_payload AS BLOB)
            FROM quick_start_state_events
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest
            ORDER BY revision;
            """;
        AddBindingParameters(command, binding);
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<QuickStartWorkflowEvent>();
        while (reader.Read())
        {
            int revision = ReadPositiveInt32(reader, 3);
            byte[] canonicalPayload = ReadPayload(
                reader,
                27,
                28,
                QuickStartWorkflowEvent.MaximumCanonicalJsonUtf8Bytes);
            QuickStartWorkflowEvent workflowEvent;
            try
            {
                workflowEvent = QuickStartWorkflowEvent.ParseStoredCanonical(
                    StrictUtf8.GetString(canonicalPayload));
            }
            catch (Exception exception) when (
                exception is DecoderFallbackException or JsonException or ArgumentException)
            {
                throw InvalidStoredData(exception);
            }

            if (revision != events.Count + 1 ||
                !SameBinding(binding, workflowEvent.Binding) ||
                ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 0)) !=
                    workflowEvent.Binding.ProjectId ||
                ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 1)) !=
                    workflowEvent.Binding.CompositionVersionId ||
                ParseDigest(ReadRequiredText(reader, 2)) !=
                    workflowEvent.Binding.CompositionVersionDigest ||
                !string.Equals(
                    ReadRequiredText(reader, 4),
                    workflowEvent.EventId.ToString(),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadRequiredText(reader, 5),
                    workflowEvent.Kind.ToString(),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadRequiredText(reader, 6),
                    workflowEvent.Actor.Kind == QuickStartActorKind.User
                        ? "user"
                        : "system",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadRequiredText(reader, 7),
                    workflowEvent.Actor.Id,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadRequiredText(reader, 8),
                    FormatTimestamp(workflowEvent.OccurredAt),
                    StringComparison.Ordinal) ||
                !SameProjectSnapshot(
                    ReadOptionalProjectSnapshot(reader, 9, 10),
                    workflowEvent.ProjectSnapshot) ||
                ParseOptionalDigest(ReadOptionalText(reader, 11)) !=
                    workflowEvent.SettingsSnapshotDigest ||
                ParseOptionalEntityId<AutomatedEvaluationIdTag>(
                    ReadOptionalText(reader, 12)) != workflowEvent.EvaluationId ||
                ParseOptionalTimestamp(ReadOptionalText(reader, 13)) !=
                    workflowEvent.EvaluationEvaluatedAt ||
                ParseOptionalEvaluationStatus(ReadOptionalText(reader, 14)) !=
                    workflowEvent.EvaluationAggregateStatus ||
                ParseOptionalDigest(ReadOptionalText(reader, 15)) !=
                    workflowEvent.EvaluationCanonicalDigest ||
                ParseOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                    ReadOptionalText(reader, 16)) != workflowEvent.ArtifactId ||
                ParseOptionalDigest(ReadOptionalText(reader, 17)) !=
                    workflowEvent.ArtifactDigest ||
                !string.Equals(
                    ReadOptionalText(reader, 18),
                    workflowEvent.EvaluationResultState is null
                        ? null
                        : ToWireName(workflowEvent.EvaluationResultState.Value),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadOptionalText(reader, 19),
                    workflowEvent.ReasonCode,
                    StringComparison.Ordinal) ||
                !EventExportColumnsMatch(reader, workflowEvent.ExportFacts) ||
                ParseDigest(ReadRequiredText(reader, 26)) !=
                    workflowEvent.CanonicalDigest ||
                workflowEvent.CanonicalDigest != ContentDigest.Compute(canonicalPayload))
            {
                throw InvalidStoredData();
            }

            events.Add(workflowEvent);
        }

        if (events.Count == 0)
        {
            throw InvalidStoredData();
        }

        return Array.AsReadOnly(events.ToArray());
    }

    private static bool EventExportColumnsMatch(
        SqliteDataReader reader,
        QuickStartExportFacts? export)
    {
        string? scope = ReadOptionalText(reader, 20);
        long? rights = ReadOptionalInt64(reader, 21);
        long? external = ReadOptionalInt64(reader, 22);
        string? destination = ReadOptionalText(reader, 23);
        string? state = ReadOptionalText(reader, 24);
        long? revision = ReadOptionalInt64(reader, 25);
        return export is null
            ? scope is null && rights is null && external is null &&
                destination is null && state is null && revision is null
            : string.Equals(scope, export.PublicationScope.ToString(), StringComparison.Ordinal) &&
                rights == (export.RightsAllowedForInternalUse ? 1 : 0) &&
                external == (export.ExternalDestinationRequested ? 1 : 0) &&
                string.Equals(destination, "localInternalDelivery", StringComparison.Ordinal) &&
                string.Equals(state, ToWireName(export.AuthorizedState), StringComparison.Ordinal) &&
                revision == export.AuthorizedRevision;
    }

    private static void ValidateStoredEvaluation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowEvent workflowEvent,
        QuickStartWorkflowEvent? precedingEvent)
    {
        if (workflowEvent.Kind is not (
            QuickStartWorkflowEventKind.EvaluationCompleted or
            QuickStartWorkflowEventKind.Exported))
        {
            return;
        }

        if (workflowEvent.EvaluationId is null ||
            workflowEvent.EvaluationAggregateStatus is null ||
            workflowEvent.EvaluationCanonicalDigest is null ||
            workflowEvent.SettingsSnapshotDigest is null)
        {
            throw InvalidStoredData();
        }

        AutomatedEvaluationRun evaluation = AutomatedEvaluationStore.Read(
            connection,
            transaction,
            workflowEvent.EvaluationId)
            ?? throw new InvalidOperationException(
                "A completed quick-start event requires a stored automated evaluation.");
        if (workflowEvent.Kind == QuickStartWorkflowEventKind.EvaluationCompleted &&
            (precedingEvent is null ||
                precedingEvent.Kind is not (
                    QuickStartWorkflowEventKind.EvaluationStarted or
                    QuickStartWorkflowEventKind.TechnicalReevaluationStarted) ||
                !SameBinding(precedingEvent.Binding, workflowEvent.Binding) ||
                !SameProjectSnapshot(precedingEvent.ProjectSnapshot, workflowEvent.ProjectSnapshot) ||
                precedingEvent.SettingsSnapshotDigest != workflowEvent.SettingsSnapshotDigest ||
                evaluation.InputDigest != precedingEvent.CanonicalDigest ||
                evaluation.StartedAt < precedingEvent.OccurredAt))
        {
            throw new InvalidOperationException(
                "The stored automated evaluation does not belong to the active evaluation start event.");
        }

        if (evaluation.EvaluationId != workflowEvent.EvaluationId ||
            evaluation.ProjectId != workflowEvent.Binding.ProjectId ||
            evaluation.CompositionVersionId !=
                workflowEvent.Binding.CompositionVersionId ||
            evaluation.CompositionVersionDigest !=
                workflowEvent.Binding.CompositionVersionDigest ||
            evaluation.SettingsSnapshotDigest !=
                workflowEvent.SettingsSnapshotDigest ||
            evaluation.AggregateStatus !=
                workflowEvent.EvaluationAggregateStatus ||
            evaluation.CanonicalDigest !=
                workflowEvent.EvaluationCanonicalDigest ||
            evaluation.ArtifactId != workflowEvent.ArtifactId ||
            evaluation.ArtifactDigest != workflowEvent.ArtifactDigest)
        {
            throw new InvalidOperationException(
                "The quick-start event does not match the stored automated evaluation.");
        }

        if (workflowEvent.Kind == QuickStartWorkflowEventKind.EvaluationCompleted &&
            workflowEvent.EvaluationEvaluatedAt != evaluation.EvaluatedAt)
        {
            throw new InvalidOperationException(
                "The quick-start event evaluated timestamp does not match the stored automated evaluation.");
        }

        if (workflowEvent.Kind == QuickStartWorkflowEventKind.EvaluationCompleted &&
            QuickStartEvaluationPolicy.DetermineCompletedState(evaluation) !=
                workflowEvent.EvaluationResultState)
        {
            throw new InvalidOperationException(
                "The quick-start evaluation result does not match its closed policy.");
        }

        if (workflowEvent.Kind == QuickStartWorkflowEventKind.EvaluationCompleted &&
            workflowEvent.OccurredAt < evaluation.EvaluatedAt)
        {
            throw new InvalidOperationException(
                "The quick-start evaluation completion predates its stored evaluation.");
        }
    }

    private static StoredEventIdentity? ReadEventIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<QuickStartWorkflowEventIdTag> eventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(project_id AS BLOB),
                CAST(composition_version_id AS BLOB),
                CAST(composition_version_digest AS BLOB),
                length(CAST(canonical_payload AS BLOB)),
                CAST(canonical_payload AS BLOB)
            FROM quick_start_state_events
            WHERE event_id = $event_id;
            """;
        SqliteStoreSupport.AddText(command, "$event_id", eventId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var identity = new StoredEventIdentity(
            new QuickStartWorkflowBinding(
                ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 0)),
                ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 1)),
                ParseDigest(ReadRequiredText(reader, 2))),
            ReadPayload(
                reader,
                3,
                4,
                QuickStartWorkflowEvent.MaximumCanonicalJsonUtf8Bytes));
        if (reader.Read())
        {
            throw InvalidStoredData();
        }

        return identity;
    }

    private static QuickStartWorkflowBinding? ReadVersionBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CAST(project_id AS BLOB),
                CAST(id AS BLOB),
                CAST(content_digest AS BLOB)
            FROM composition_versions
            WHERE project_id = $project_id AND id = $composition_version_id;
            """;
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            compositionVersionId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var binding = new QuickStartWorkflowBinding(
            ParseEntityId<ProjectIdTag>(ReadRequiredText(reader, 0)),
            ParseEntityId<CompositionVersionIdTag>(ReadRequiredText(reader, 1)),
            ParseDigest(ReadRequiredText(reader, 2)));
        if (reader.Read() || binding.ProjectId != projectId ||
            binding.CompositionVersionId != compositionVersionId)
        {
            throw InvalidStoredData();
        }

        return binding;
    }

    private static void EnsureVersionBindingExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding)
    {
        QuickStartWorkflowBinding? stored = ReadVersionBinding(
            connection,
            transaction,
            binding.ProjectId,
            binding.CompositionVersionId);
        if (stored is null || !SameBinding(stored, binding))
        {
            throw new InvalidOperationException(
                "The legacy review target does not match a stored composition version.");
        }
    }

    private static void ReadLegacyReviewStates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding,
        List<LegacyReviewReference> destination)
    {
        using SqliteCommand command = CreateLegacyCommand(
            connection,
            transaction,
            binding,
            """
            SELECT
                CAST(id AS BLOB),
                CAST(state AS BLOB),
                CAST(actor_id AS BLOB),
                CAST(actor_role AS BLOB),
                CAST(recorded_at AS BLOB)
            FROM review_records
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            destination.Add(new LegacyReviewReference(
                ReadCanonicalUuid(reader, 0, "legacy review record"),
                binding,
                LegacyReviewReferenceKind.ReviewState,
                ParseEnum<ReviewState>(ReadRequiredText(reader, 1)),
                ReadLegacyTimestamp(reader, 4, "legacy review record"),
                ReadLegacyActorId(reader, 2),
                ParseEnum<ReviewActorRole>(ReadRequiredText(reader, 3))));
        }
    }

    private static void ReadLegacyComments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding,
        List<LegacyReviewReference> destination)
    {
        using SqliteCommand command = CreateLegacyCommand(
            connection,
            transaction,
            binding,
            """
            SELECT
                CAST(id AS BLOB),
                CAST(actor_id AS BLOB),
                CAST(actor_role AS BLOB),
                CAST(created_at AS BLOB)
            FROM review_comments
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            destination.Add(new LegacyReviewReference(
                ReadCanonicalUuid(reader, 0, "legacy review comment"),
                binding,
                LegacyReviewReferenceKind.Comment,
                reviewState: null,
                ReadLegacyTimestamp(reader, 3, "legacy review comment"),
                ReadLegacyActorId(reader, 1),
                ParseEnum<ReviewActorRole>(ReadRequiredText(reader, 2))));
        }
    }

    private static void ReadLegacyApprovals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding,
        List<LegacyReviewReference> destination)
    {
        using SqliteCommand command = CreateLegacyCommand(
            connection,
            transaction,
            binding,
            """
            SELECT
                CAST(id AS BLOB),
                CAST(actor_id AS BLOB),
                CAST(actor_role AS BLOB),
                CAST(approved_at AS BLOB)
            FROM approvals
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            destination.Add(new LegacyReviewReference(
                ReadCanonicalUuid(reader, 0, "legacy approval"),
                binding,
                LegacyReviewReferenceKind.Approval,
                reviewState: null,
                ReadLegacyTimestamp(reader, 3, "legacy approval"),
                ReadLegacyActorId(reader, 1),
                ParseEnum<ReviewActorRole>(ReadRequiredText(reader, 2))));
        }
    }

    private static SqliteCommand CreateLegacyCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding,
        string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddBindingParameters(command, binding);
        return command;
    }

    private static string ReadCanonicalUuid(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        string value = ReadRequiredText(reader, ordinal);
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return value;
    }

    private static string ReadLegacyActorId(
        SqliteDataReader reader,
        int ordinal)
    {
        string value = ReadRequiredText(reader, ordinal);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\0'))
        {
            throw InvalidStoredData();
        }

        return value;
    }

    private static DateTimeOffset ReadLegacyTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        string value = ReadRequiredText(reader, ordinal);
        if (!DateTimeOffset.TryParseExact(
                value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        return parsed;
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        return parsed;
    }

    private static int ReadCurrentRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuickStartWorkflowBinding binding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM quick_start_workflows
            WHERE project_id = $project_id
              AND composition_version_id = $composition_version_id
              AND composition_version_digest = $composition_version_digest;
            """;
        AddBindingParameters(command, binding);
        object? value = command.ExecuteScalar();
        if (value is null)
        {
            return 0;
        }

        if (value is not long revision || revision < 0 || revision > int.MaxValue)
        {
            throw InvalidStoredData();
        }

        return (int)revision;
    }

    private static void AddBindingParameters(
        SqliteCommand command,
        QuickStartWorkflowBinding binding)
    {
        SqliteStoreSupport.AddText(command, "$project_id", binding.ProjectId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_id",
            binding.CompositionVersionId.ToString());
        SqliteStoreSupport.AddText(
            command,
            "$composition_version_digest",
            binding.CompositionVersionDigest.Hex);
    }

    private static void AddNullableInteger(
        SqliteCommand command,
        string name,
        long? value)
    {
        command.Parameters.Add(name, SqliteType.Integer).Value =
            value ?? (object)DBNull.Value;
    }

    private static byte[] ReadPayload(
        SqliteDataReader reader,
        int lengthOrdinal,
        int payloadOrdinal,
        int maximumLength)
    {
        if (reader.GetValue(lengthOrdinal) is not long length ||
            length is <= 0 ||
            length > maximumLength ||
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

    private static string? ReadOptionalText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadRequiredText(reader, ordinal);

    private static long? ReadOptionalInt64(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) is long value
            ? value
            : throw InvalidStoredData();
    }

    private static int ReadPositiveInt32(SqliteDataReader reader, int ordinal)
    {
        int value = ReadNonnegativeInt32(reader, ordinal);
        return value > 0 ? value : throw InvalidStoredData();
    }

    private static int ReadNonnegativeInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long value ||
            value < 0 || value > int.MaxValue)
        {
            throw InvalidStoredData();
        }

        return (int)value;
    }

    private static QuickStartProjectSnapshot? ReadOptionalProjectSnapshot(
        SqliteDataReader reader,
        int revisionOrdinal,
        int digestOrdinal)
    {
        long? revision = ReadOptionalInt64(reader, revisionOrdinal);
        string? digest = ReadOptionalText(reader, digestOrdinal);
        if ((revision is null) != (digest is null))
        {
            throw InvalidStoredData();
        }

        if (revision is null)
        {
            return null;
        }

        if (revision < 0 || revision > int.MaxValue)
        {
            throw InvalidStoredData();
        }

        return new QuickStartProjectSnapshot(
            (int)revision.Value,
            ParseDigest(digest!));
    }

    private static EntityId<TTag> ParseEntityId<TTag>(string value)
    {
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        return id;
    }

    private static EntityId<TTag>? ParseOptionalEntityId<TTag>(string? value) =>
        value is null ? null : ParseEntityId<TTag>(value);

    private static ContentDigest ParseDigest(string value)
    {
        if (!ContentDigest.TryParse(value, out ContentDigest? digest) ||
            !string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        return digest;
    }

    private static ContentDigest? ParseOptionalDigest(string? value) =>
        value is null ? null : ParseDigest(value);

    private static DateTimeOffset? ParseOptionalTimestamp(string? value) =>
        value is null ? null : ParseTimestamp(value);

    private static AutomatedEvaluationStatus? ParseOptionalEvaluationStatus(
        string? value) => value is null ? null : value switch
        {
            "PASS" => AutomatedEvaluationStatus.Pass,
            "WARNING" => AutomatedEvaluationStatus.Warning,
            "FAIL" => AutomatedEvaluationStatus.Fail,
            "NOT EVALUATED" => AutomatedEvaluationStatus.NotEvaluated,
            _ => throw InvalidStoredData(),
        };

    private static QuickStartWorkflowState ParseState(string value) => value switch
    {
        "editing" => QuickStartWorkflowState.Editing,
        "automatedEvaluation" => QuickStartWorkflowState.AutomatedEvaluation,
        "ready" => QuickStartWorkflowState.Ready,
        "readyWithWarnings" => QuickStartWorkflowState.ReadyWithWarnings,
        "failed" => QuickStartWorkflowState.Failed,
        "exported" => QuickStartWorkflowState.Exported,
        _ => throw InvalidStoredData(),
    };

    private static string ToWireName(QuickStartWorkflowState value) => value switch
    {
        QuickStartWorkflowState.Editing => "editing",
        QuickStartWorkflowState.AutomatedEvaluation => "automatedEvaluation",
        QuickStartWorkflowState.Ready => "ready",
        QuickStartWorkflowState.ReadyWithWarnings => "readyWithWarnings",
        QuickStartWorkflowState.Failed => "failed",
        QuickStartWorkflowState.Exported => "exported",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown state."),
    };

    private static string ToWireName(AutomatedEvaluationStatus value) => value switch
    {
        AutomatedEvaluationStatus.Pass => "PASS",
        AutomatedEvaluationStatus.Warning => "WARNING",
        AutomatedEvaluationStatus.Fail => "FAIL",
        AutomatedEvaluationStatus.NotEvaluated => "NOT EVALUATED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Unknown evaluation status."),
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero ||
            !string.Equals(value, FormatTimestamp(parsed), StringComparison.Ordinal))
        {
            throw InvalidStoredData();
        }

        return parsed;
    }

    private static bool SameBinding(
        QuickStartWorkflowBinding left,
        QuickStartWorkflowBinding right) =>
        left.ProjectId == right.ProjectId &&
        left.CompositionVersionId == right.CompositionVersionId &&
        left.CompositionVersionDigest == right.CompositionVersionDigest;

    private static bool SameProjectSnapshot(
        QuickStartProjectSnapshot? left,
        QuickStartProjectSnapshot? right) =>
        left is null && right is null ||
        left is not null && right is not null &&
        QuickStartWorkflowContract.SameProjectSnapshot(left, right);

    private static QuickStartDeliveryException Failure(
        QuickStartDeliveryFailureReason reason,
        bool reconciliationRequired = false,
        Exception? innerException = null) =>
        new(reason, reconciliationRequired, innerException);

    private static InvalidOperationException InvalidStoredData() =>
        SqliteStoreSupport.InvalidStoredData(EntityKind);

    private static InvalidOperationException InvalidStoredData(Exception exception) =>
        new("Stored quick-start workflow data is invalid.", exception);

    private sealed record PreparedEvent(
        QuickStartWorkflowEvent Event,
        byte[] CanonicalUtf8);

    private sealed record StoredEventIdentity(
        QuickStartWorkflowBinding Binding,
        byte[] CanonicalPayload);

    private sealed record ProjectEvidence(
        Project Project,
        QuickStartProjectSnapshot Snapshot,
        string CanonicalJson);

    private sealed record DeliveryEvidence(
        QuickStartWorkflow Workflow,
        ProjectEvidence Project,
        AutomatedEvaluationRun Evaluation,
        IReadOnlyList<StoredArtifact> SourceArtifacts,
        StoredArtifact RenderArtifact,
        ContentDigest EvidenceDigest);

    private enum DeliveryAuthorizationLifecycle
    {
        Active,
        Consuming,
        Consumed,
    }

    private sealed class DeliveryAuthorizationState
    {
        public DeliveryAuthorizationState(
            QuickStartLocalInternalDeliveryAuthorization authorization,
            QuickStartLocalInternalDeliveryRequest request,
            ContentDigest requestDigest,
            DeliveryAuthorizationLifecycle lifecycle)
        {
            Authorization = authorization;
            Request = request;
            RequestDigest = requestDigest;
            Lifecycle = lifecycle;
        }

        public QuickStartLocalInternalDeliveryAuthorization Authorization { get; }

        public QuickStartLocalInternalDeliveryRequest Request { get; }

        public ContentDigest RequestDigest { get; }

        public DeliveryAuthorizationLifecycle Lifecycle { get; set; }
    }

    private enum DeliveryEvidenceFailureKind
    {
        DuplicateReceipt,
        Revoked,
        Stale,
        Artifact,
    }

    private sealed class DeliveryEvidenceException : Exception
    {
        public DeliveryEvidenceException(DeliveryEvidenceFailureKind kind)
            : base("Quick-start delivery evidence validation failed.")
        {
            Kind = kind;
        }

        public DeliveryEvidenceFailureKind Kind { get; }
    }

    private sealed class OpenedDeliveryArtifacts : IAsyncDisposable
    {
        public OpenedDeliveryArtifacts(
            DeliveryEvidence evidence,
            IReadOnlyList<ValidatedArtifactReadStream> sourceArtifacts,
            ValidatedArtifactReadStream renderArtifact)
        {
            Evidence = evidence;
            SourceArtifacts = sourceArtifacts;
            RenderArtifact = renderArtifact;
        }

        public DeliveryEvidence Evidence { get; }

        public IReadOnlyList<ValidatedArtifactReadStream> SourceArtifacts { get; }

        public ValidatedArtifactReadStream RenderArtifact { get; }

        public async ValueTask DisposeAsync()
        {
            await RenderArtifact.DisposeAsync().ConfigureAwait(false);
            foreach (ValidatedArtifactReadStream sourceArtifact in SourceArtifacts)
            {
                await sourceArtifact.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed record StoredSnapshot(
        QuickStartWorkflowBinding Binding,
        QuickStartWorkflowState State,
        int Revision,
        QuickStartProjectSnapshot? ProjectSnapshot,
        ContentDigest? SettingsSnapshotDigest,
        EntityId<AutomatedEvaluationIdTag>? EvaluationId,
        AutomatedEvaluationStatus? EvaluationAggregateStatus,
        ContentDigest? EvaluationCanonicalDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId,
        ContentDigest? ArtifactDigest,
        int SelfReviewCount,
        int ChangeRequestCount,
        ContentDigest EventChainDigest,
        EntityId<QuickStartWorkflowEventIdTag> LastEventId,
        ContentDigest LastEventDigest,
        ContentDigest CanonicalDigest,
        byte[] CanonicalPayload);
}