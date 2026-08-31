using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Policy;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Serialization;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Policy;

public enum ExportPolicyBoundary
{
    FinalRender,
    Export,
}

public enum ExportPolicyDisposition
{
    Authorized,
    Blocked,
}

[Flags]
public enum ExportPolicyIssueCode
{
    None = 0,
    ActorNotAuthorized = 1 << 0,
    RepositoryEvidenceMissing = 1 << 1,
    RepositoryEvidenceChanged = 1 << 2,
    ContextMismatch = 1 << 3,
    ReviewStateNotReady = 1 << 4,
    ApprovalEvidenceInvalid = 1 << 5,
    RequiredApprovalMissing = 1 << 6,
    PublicationScopeBlocked = 1 << 7,
    DestinationNotAllowed = 1 << 8,
    RightsNotSatisfied = 1 << 9,
    RightsExceptionInvalid = 1 << 10,
    PerformerConfirmationRequired = 1 << 11,
    SensitivityEvidenceMissing = 1 << 12,
    SensitivityScanIncomplete = 1 << 13,
    SensitivityResolutionRequired = 1 << 14,
    UnresolvedWarning = 1 << 15,
    ProvenanceInvalid = 1 << 16,
    ResolutionEvidenceInvalid = 1 << 17,
    RenderQcMismatch = 1 << 18,
    InvalidEvidence = 1 << 19,
}

public enum ExportPolicyWarningApplicability
{
    AllOutputs,
    ExternalOnly,
}

public enum ExportPolicyResolutionKind
{
    TextSensitivity,
    Warning,
}

public enum ExportPolicyFailureReason
{
    DuplicateOperationId,
    AuthorizationNotFound,
    AuthorizationAlreadyConsumed,
    AuthorizationRequestMismatch,
    AuthorizationStale,
    AuthorizationRevoked,
}

public sealed class ExportOperationIdTag
{
}

public sealed class ExportPolicyWarningIdTag
{
}

public sealed class ExportPolicyResolutionIdTag
{
}

/// <summary>
/// Identifies one configured destination without exposing a path or remote address.
/// </summary>
public sealed class ExportDestinationBinding
{
    public ExportDestinationBinding(
        ReviewExportDestination destination,
        ContentDigest bindingDigest)
    {
        if (!Enum.IsDefined(destination))
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        ArgumentNullException.ThrowIfNull(bindingDigest);
        Destination = destination;
        BindingDigest = bindingDigest;
    }

    public ReviewExportDestination Destination { get; }

    public ContentDigest BindingDigest { get; }
}

/// <summary>
/// Carries one warning as a typed, content-addressed policy input without warning text.
/// </summary>
public sealed class ExportPolicyWarningEvidence
{
    public ExportPolicyWarningEvidence(
        EntityId<ExportPolicyWarningIdTag> id,
        ContentDigest evidenceDigest,
        ExportPolicyWarningApplicability applicability)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        if (!Enum.IsDefined(applicability))
        {
            throw new ArgumentOutOfRangeException(nameof(applicability));
        }

        Id = id;
        EvidenceDigest = evidenceDigest;
        Applicability = applicability;
        SubjectDigest = ExportPolicyDigest.ComputeWarningSubjectDigest(
            id,
            evidenceDigest,
            applicability);
    }

    public EntityId<ExportPolicyWarningIdTag> Id { get; }

    public ContentDigest EvidenceDigest { get; }

    public ExportPolicyWarningApplicability Applicability { get; }

    public ContentDigest SubjectDigest { get; }
}

/// <summary>
/// Immutable, approval-backed evidence that one exact warning or sensitivity scan was
/// resolved before the referenced content approval was recorded.
/// </summary>
public sealed class ExportPolicyResolutionEvidence
{
    internal ExportPolicyResolutionEvidence(
        EntityId<ExportPolicyResolutionIdTag> id,
        ExportPolicyResolutionKind kind,
        ContentDigest subjectDigest,
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<ApprovalIdTag> approvalId,
        DateTimeOffset resolvedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(subjectDigest);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        ArgumentNullException.ThrowIfNull(approvalId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Id = id;
        Kind = kind;
        SubjectDigest = subjectDigest;
        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        ApprovalId = approvalId;
        ResolvedAt = resolvedAt;
    }

    public EntityId<ExportPolicyResolutionIdTag> Id { get; }

    public ExportPolicyResolutionKind Kind { get; }

    public ContentDigest SubjectDigest { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<ApprovalIdTag> ApprovalId { get; }

    public DateTimeOffset ResolvedAt { get; }
}

/// <summary>
/// Supplies the exact A-011 assignment history and confirmations that must be re-evaluated.
/// </summary>
public sealed class ExportPerformerEvidence
{
    public ExportPerformerEvidence(
        SpeakerAssignmentHistory assignmentHistory,
        DateTimeOffset recordedAt,
        IReadOnlyList<PerformerConfirmation> confirmations)
    {
        ArgumentNullException.ThrowIfNull(assignmentHistory);
        ArgumentNullException.ThrowIfNull(confirmations);

        var copied = new PerformerConfirmation[confirmations.Count];
        var ids = new HashSet<EntityId<PerformerConfirmationIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            PerformerConfirmation confirmation = confirmations[index]
                ?? throw new ArgumentException(
                    "Performer confirmations must not contain null values.",
                    nameof(confirmations));
            if (!ids.Add(confirmation.Id))
            {
                throw new ArgumentException(
                    "Performer confirmation identities must be unique.",
                    nameof(confirmations));
            }

            copied[index] = confirmation;
        }

        AssignmentHistory = assignmentHistory;
        RecordedAt = recordedAt;
        Confirmations = Array.AsReadOnly(copied);
    }

    public EntityId<SpeakerAssignmentRevisionIdTag> AssignmentRevisionId =>
        AssignmentHistory.CurrentRevision.Id;

    public EntityId<TranscriptRevisionIdTag> TranscriptRevisionId =>
        AssignmentHistory.TranscriptRevisionId;

    public DateTimeOffset RecordedAt { get; }

    public IReadOnlyList<PerformerConfirmation> Confirmations { get; }

    internal SpeakerAssignmentHistory AssignmentHistory { get; }
}

/// <summary>
/// Supplies an exact transcript and dictionary snapshot for a fresh local sensitivity scan.
/// </summary>
public sealed class ExportTextSensitivityEvidence
{
    public ExportTextSensitivityEvidence(
        TranscriptRevision revision,
        IReadOnlyList<TermEntry> dictionaryEntries)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);

        var copied = new TermEntry[dictionaryEntries.Count];
        var ids = new HashSet<EntityId<TermEntryIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            TermEntry entry = dictionaryEntries[index]
                ?? throw new ArgumentException(
                    "Sensitivity dictionary entries must not contain null values.",
                    nameof(dictionaryEntries));
            if (!ids.Add(entry.Id))
            {
                throw new ArgumentException(
                    "Sensitivity dictionary entry identities must be unique.",
                    nameof(dictionaryEntries));
            }

            copied[index] = entry;
        }

        Revision = revision;
        DictionaryEntries = Array.AsReadOnly(copied);
        TranscriptDigest = ExportPolicyDigest.ComputeTranscriptEvidenceDigest(
            revision);
        CanonicalTranscriptDigest = TryComputeCanonicalTranscriptDigest(revision);
    }

    public EntityId<TranscriptRevisionIdTag> RevisionId => Revision.Id;

    public ContentDigest TranscriptDigest { get; }

    public IReadOnlyList<TermEntry> DictionaryEntries { get; }

    internal TranscriptRevision Revision { get; }

    internal ContentDigest? CanonicalTranscriptDigest { get; }

    private static ContentDigest? TryComputeCanonicalTranscriptDigest(
        TranscriptRevision revision)
    {
        try
        {
            return ExportPolicyDigest.ComputeStrictUtf8Digest(
                TranscriptSnapshotJson.WriteRevision(revision));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Binds optional QC evidence to one registered final-render artifact and graph snapshot.
/// </summary>
public sealed class ExportRenderQcBinding
{
    public ExportRenderQcBinding(
        EntityId<ArtifactIdTag> renderArtifactId,
        CompositionVersion compositionVersion,
        RenderGraph graph,
        ContentDigest renderContentDigest,
        long renderByteSize,
        ContentDigest qcVerifiedRenderDigest,
        ContentDigest qcEvidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(renderArtifactId);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(renderContentDigest);
        ArgumentNullException.ThrowIfNull(qcVerifiedRenderDigest);
        ArgumentNullException.ThrowIfNull(qcEvidenceDigest);
        if (renderByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(renderByteSize));
        }

        RenderArtifactId = renderArtifactId;
        ProjectId = compositionVersion.ProjectId;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        RenderGraphId = graph.Id;
        RenderGraphDigest = ExportPolicyDigest.ComputeRenderGraphDigest(graph);
        RenderContentDigest = renderContentDigest;
        RenderByteSize = renderByteSize;
        QcVerifiedRenderDigest = qcVerifiedRenderDigest;
        QcEvidenceDigest = qcEvidenceDigest;
    }

    public EntityId<ArtifactIdTag> RenderArtifactId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<RenderGraphIdTag> RenderGraphId { get; }

    public ContentDigest RenderGraphDigest { get; }

    public ContentDigest RenderContentDigest { get; }

    public long RenderByteSize { get; }

    public ContentDigest QcVerifiedRenderDigest { get; }

    public ContentDigest QcEvidenceDigest { get; }
}

public sealed class ExportPolicyRequest
{
    public ExportPolicyRequest(
        EntityId<ExportOperationIdTag> operationId,
        ExportPolicyBoundary boundary,
        string actorSid,
        AuthorizationRole actingRole,
        Project project,
        CompositionVersion compositionVersion,
        ExportDestinationBinding destination,
        RenderGraph graph,
        RenderGraphValidationContext validationContext,
        IReadOnlyList<RecordedRightsException> rightsExceptions,
        IReadOnlyList<ExportPerformerEvidence> performerEvidence,
        IReadOnlyList<ExportTextSensitivityEvidence> sensitivityEvidence,
        IReadOnlyList<ExportPolicyWarningEvidence> warnings,
        IReadOnlyList<ExportPolicyResolutionEvidence> resolutions,
        ExportRenderQcBinding? renderQcBinding)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(actorSid);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(validationContext);
        if (!Enum.IsDefined(boundary))
        {
            throw new ArgumentOutOfRangeException(nameof(boundary));
        }

        if (!Enum.IsDefined(actingRole))
        {
            throw new ArgumentOutOfRangeException(nameof(actingRole));
        }

        if (string.IsNullOrWhiteSpace(actorSid)
            || !string.Equals(actorSid, actorSid.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An export-policy actor identifier must be nonblank and canonical.",
                nameof(actorSid));
        }

        OperationId = operationId;
        Boundary = boundary;
        ActorSid = actorSid;
        ActingRole = actingRole;
        Project = project;
        CompositionVersion = compositionVersion;
        Destination = destination;
        Graph = graph;
        ValidationContext = validationContext;
        RightsExceptions = Array.AsReadOnly(CopyRightsExceptions(rightsExceptions));
        PerformerEvidence = Array.AsReadOnly(CopyPerformerEvidence(performerEvidence));
        SensitivityEvidence = Array.AsReadOnly(CopySensitivityEvidence(sensitivityEvidence));
        Warnings = Array.AsReadOnly(CopyWarnings(warnings));
        Resolutions = Array.AsReadOnly(CopyResolutions(resolutions));
        RenderQcBinding = renderQcBinding;
    }

    public EntityId<ExportOperationIdTag> OperationId { get; }

    public ExportPolicyBoundary Boundary { get; }

    public AuthorizationRole ActingRole { get; }

    public Project Project { get; }

    public CompositionVersion CompositionVersion { get; }

    public ExportDestinationBinding Destination { get; }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }

    public IReadOnlyList<RecordedRightsException> RightsExceptions { get; }

    public IReadOnlyList<ExportPerformerEvidence> PerformerEvidence { get; }

    public IReadOnlyList<ExportTextSensitivityEvidence> SensitivityEvidence { get; }

    public IReadOnlyList<ExportPolicyWarningEvidence> Warnings { get; }

    public IReadOnlyList<ExportPolicyResolutionEvidence> Resolutions { get; }

    public ExportRenderQcBinding? RenderQcBinding { get; }

    internal string ActorSid { get; }

    private static RecordedRightsException[] CopyRightsExceptions(
        IReadOnlyList<RecordedRightsException> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new RecordedRightsException[values.Count];
        var ids = new HashSet<EntityId<RightsExceptionIdTag>>();
        var assetIds = new HashSet<EntityId<AssetIdTag>>();
        var auditEventIds = new HashSet<EntityId<AuditEventIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            RecordedRightsException value = values[index]
                ?? throw new ArgumentException(
                    "Rights exceptions must not contain null values.",
                    nameof(values));
            if (!ids.Add(value.Id)
                || !assetIds.Add(value.AssetId)
                || !auditEventIds.Add(value.AuditEventId))
            {
                throw new ArgumentException(
                    "Rights exception, asset, and audit-event identities must be unique.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    private static ExportPerformerEvidence[] CopyPerformerEvidence(
        IReadOnlyList<ExportPerformerEvidence> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new ExportPerformerEvidence[values.Count];
        var revisionIds = new HashSet<EntityId<SpeakerAssignmentRevisionIdTag>>();
        var transcriptRevisionIds =
            new HashSet<EntityId<TranscriptRevisionIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            ExportPerformerEvidence value = values[index]
                ?? throw new ArgumentException(
                    "Performer evidence must not contain null values.",
                    nameof(values));
            if (!revisionIds.Add(value.AssignmentRevisionId)
                || !transcriptRevisionIds.Add(value.TranscriptRevisionId))
            {
                throw new ArgumentException(
                    "Performer assignment and transcript-revision identities must be unique.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    private static ExportTextSensitivityEvidence[] CopySensitivityEvidence(
        IReadOnlyList<ExportTextSensitivityEvidence> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new ExportTextSensitivityEvidence[values.Count];
        var revisionIds = new HashSet<EntityId<TranscriptRevisionIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            ExportTextSensitivityEvidence value = values[index]
                ?? throw new ArgumentException(
                    "Sensitivity evidence must not contain null values.",
                    nameof(values));
            if (!revisionIds.Add(value.RevisionId))
            {
                throw new ArgumentException(
                    "Sensitivity revision identities must be unique.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    private static ExportPolicyWarningEvidence[] CopyWarnings(
        IReadOnlyList<ExportPolicyWarningEvidence> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new ExportPolicyWarningEvidence[values.Count];
        var ids = new HashSet<EntityId<ExportPolicyWarningIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            ExportPolicyWarningEvidence value = values[index]
                ?? throw new ArgumentException(
                    "Export warnings must not contain null values.",
                    nameof(values));
            if (!ids.Add(value.Id))
            {
                throw new ArgumentException(
                    "Export warning identities must be unique.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    private static ExportPolicyResolutionEvidence[] CopyResolutions(
        IReadOnlyList<ExportPolicyResolutionEvidence> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copied = new ExportPolicyResolutionEvidence[values.Count];
        var ids = new HashSet<EntityId<ExportPolicyResolutionIdTag>>();
        var subjects = new HashSet<ResolutionKey>();
        for (int index = 0; index < copied.Length; index++)
        {
            ExportPolicyResolutionEvidence value = values[index]
                ?? throw new ArgumentException(
                    "Export resolutions must not contain null values.",
                    nameof(values));
            var key = new ResolutionKey(value.Kind, value.SubjectDigest.Hex);
            if (!ids.Add(value.Id) || !subjects.Add(key))
            {
                throw new ArgumentException(
                    "Export resolution identities and subjects must be unique.",
                    nameof(values));
            }

            copied[index] = value;
        }

        return copied;
    }

    internal readonly record struct ResolutionKey(
        ExportPolicyResolutionKind Kind,
        string SubjectDigest);
}

public sealed class ExportPolicyResolutionRequirement
{
    internal ExportPolicyResolutionRequirement(
        ExportPolicyResolutionKind kind,
        ContentDigest subjectDigest)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(subjectDigest);
        Kind = kind;
        SubjectDigest = subjectDigest;
    }

    public ExportPolicyResolutionKind Kind { get; }

    public ContentDigest SubjectDigest { get; }
}

public sealed class ExportPolicyAuthorization
{
    internal ExportPolicyAuthorization(
        EntityId<ExportOperationIdTag> operationId,
        ExportPolicyBoundary boundary,
        ReviewExportDestination destination,
        ContentDigest evidenceDigest)
    {
        OperationId = operationId;
        Boundary = boundary;
        Destination = destination;
        EvidenceDigest = evidenceDigest;
    }

    public EntityId<ExportOperationIdTag> OperationId { get; }

    public ExportPolicyBoundary Boundary { get; }

    public ReviewExportDestination Destination { get; }

    public ContentDigest EvidenceDigest { get; }
}

public sealed class ExportPolicyDecision
{
    internal ExportPolicyDecision(
        ExportPolicyIssueCode issueCodes,
        ContentDigest evidenceDigest,
        ReviewGateStatus satisfiedGateStatus,
        IReadOnlyList<ExportPolicyResolutionRequirement> resolutionRequirements,
        ExportPolicyAuthorization? authorization)
    {
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        ArgumentNullException.ThrowIfNull(resolutionRequirements);
        ExportPolicyContract.ValidateIssueCodes(issueCodes);

        bool authorized = issueCodes == ExportPolicyIssueCode.None;
        if (authorized != (authorization is not null)
            || authorized != (satisfiedGateStatus != ReviewGateStatus.None))
        {
            throw new InvalidOperationException(
                "The export-policy decision is inconsistent.");
        }

        IssueCodes = issueCodes;
        EvidenceDigest = evidenceDigest;
        SatisfiedGateStatus = satisfiedGateStatus;
        ResolutionRequirements = Array.AsReadOnly(
            resolutionRequirements.ToArray());
        Authorization = authorization;
        Disposition = authorized
            ? ExportPolicyDisposition.Authorized
            : ExportPolicyDisposition.Blocked;
    }

    public ExportPolicyDisposition Disposition { get; }

    public ExportPolicyIssueCode IssueCodes { get; }

    public ContentDigest EvidenceDigest { get; }

    public ReviewGateStatus SatisfiedGateStatus { get; }

    public IReadOnlyList<ExportPolicyResolutionRequirement>
        ResolutionRequirements
    { get; }

    public ExportPolicyAuthorization? Authorization { get; }

    public bool IsAuthorized => Disposition == ExportPolicyDisposition.Authorized;
}

/// <summary>
/// Receipt passed only to the immediate operation after a successful fresh recheck.
/// </summary>
public sealed class ExportPolicyConsumption
{
    private readonly object _sync = new();
    private readonly ExportPolicyRequest _request;
    private FinalRenderPolicySnapshot? _finalRenderSnapshot;
    private bool _active = true;

    internal ExportPolicyConsumption(
        ExportPolicyAuthorization authorization,
        ReviewGateStatus satisfiedGateStatus,
        ExportPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (authorization.OperationId != request.OperationId
            || authorization.Boundary != request.Boundary
            || authorization.Destination != request.Destination.Destination)
        {
            throw new InvalidOperationException(
                "The export-policy consumption request is inconsistent.");
        }

        _request = request;
        OperationId = authorization.OperationId;
        Boundary = authorization.Boundary;
        Destination = authorization.Destination;
        EvidenceDigest = authorization.EvidenceDigest;
        SatisfiedGateStatus = satisfiedGateStatus;
    }

    public EntityId<ExportOperationIdTag> OperationId { get; }

    public ExportPolicyBoundary Boundary { get; }

    public ReviewExportDestination Destination { get; }

    public ContentDigest EvidenceDigest { get; }

    public ReviewGateStatus SatisfiedGateStatus { get; }

    public FinalRenderPolicySnapshot CreateFinalRenderPolicySnapshot()
    {
        lock (_sync)
        {
            if (Boundary != ExportPolicyBoundary.FinalRender)
            {
                throw new InvalidOperationException(
                    "Only a final-render policy consumption can create a final-render snapshot.");
            }

            if (!_active || _finalRenderSnapshot is not null)
            {
                throw new InvalidOperationException(
                    "The final-render policy handoff is no longer available.");
            }

            _finalRenderSnapshot = new FinalRenderPolicySnapshot(
                EvidenceDigest,
                EvidenceDigest,
                _request.Project.Id,
                _request.CompositionVersion,
                _request.Graph,
                _request.ValidationContext);
            return _finalRenderSnapshot;
        }
    }

    internal void Expire()
    {
        lock (_sync)
        {
            _active = false;
            _finalRenderSnapshot?.ExpireIfUnclaimed();
        }
    }
}

public sealed class ExportPolicyException : Exception
{
    internal ExportPolicyException(ExportPolicyFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public ExportPolicyFailureReason Reason { get; }

    private static string GetStableMessage(ExportPolicyFailureReason reason) =>
        reason switch
        {
            ExportPolicyFailureReason.DuplicateOperationId =>
                "The export-policy operation identity has already been used.",
            ExportPolicyFailureReason.AuthorizationNotFound =>
                "The export-policy authorization is not active.",
            ExportPolicyFailureReason.AuthorizationAlreadyConsumed =>
                "The export-policy authorization has already been consumed.",
            ExportPolicyFailureReason.AuthorizationRequestMismatch =>
                "The export-policy authorization does not match this operation.",
            ExportPolicyFailureReason.AuthorizationStale =>
                "The export-policy evidence changed before the operation started.",
            ExportPolicyFailureReason.AuthorizationRevoked =>
                "The export-policy actor is no longer authorized.",
            _ => "The export-policy operation failed.",
        };
}

/// <summary>
/// Re-evaluates final render and export evidence and issues an in-process, one-shot
/// authorization. The authorization must be consumed by the immediate synchronous callback.
/// </summary>
/// <remarks>
/// Consumption repeats repository and authorization checks and detects changes to the bound
/// snapshots. It intentionally does not claim to eliminate file-system or operating-system
/// changes after the callback has started; artifact execution still performs its own exact
/// verification and reconciliation.
/// </remarks>
public sealed class ExportPolicy
{
    private readonly ProjectStore _projectStore;
    private readonly ReviewStore _reviewStore;
    private readonly ApprovalService _approvalService;
    private readonly AuthorizationService _authorizationService;
    private readonly ArtifactStore _artifactStore;
    private readonly object _authorizationSync = new();
    private readonly Dictionary<
        EntityId<ExportOperationIdTag>,
        AuthorizationState> _authorizations = [];

    public ExportPolicy(
        ProjectStore projectStore,
        ReviewStore reviewStore,
        ApprovalService approvalService,
        AuthorizationService authorizationService,
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(reviewStore);
        ArgumentNullException.ThrowIfNull(approvalService);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(artifactStore);

        _projectStore = projectStore;
        _reviewStore = reviewStore;
        _approvalService = approvalService;
        _authorizationService = authorizationService;
        _artifactStore = artifactStore;
    }

    public ExportPolicyDecision EvaluateAndAuthorize(
        ExportPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOperationIdAvailable(request.OperationId);

        PolicyEvaluation evaluation = EvaluateCore(request, cancellationToken);
        if (evaluation.IssueCodes != ExportPolicyIssueCode.None)
        {
            return new ExportPolicyDecision(
                evaluation.IssueCodes,
                evaluation.EvidenceDigest,
                ReviewGateStatus.None,
                evaluation.ResolutionRequirements,
                authorization: null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var authorization = new ExportPolicyAuthorization(
            request.OperationId,
            request.Boundary,
            request.Destination.Destination,
            evaluation.EvidenceDigest);
        lock (_authorizationSync)
        {
            if (_authorizations.ContainsKey(request.OperationId))
            {
                throw Failure(ExportPolicyFailureReason.DuplicateOperationId);
            }

            _authorizations.Add(
                request.OperationId,
                new AuthorizationState(
                    authorization,
                    request,
                    AuthorizationLifecycle.Active));
        }

        return new ExportPolicyDecision(
            ExportPolicyIssueCode.None,
            evaluation.EvidenceDigest,
            evaluation.SatisfiedGateStatus,
            evaluation.ResolutionRequirements,
            authorization);
    }

    /// <summary>
    /// Rechecks and consumes one authorization, then invokes the supplied operation on the
    /// same synchronous call stack. A failed or cancelled consumption cannot be retried.
    /// </summary>
    public TResult Consume<TResult>(
        ExportPolicyAuthorization authorization,
        ExportPolicyRequest request,
        Func<ExportPolicyConsumption, TResult> immediateOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(immediateOperation);

        AuthorizationState state;
        lock (_authorizationSync)
        {
            if (!_authorizations.TryGetValue(
                    authorization.OperationId,
                    out state!))
            {
                throw Failure(ExportPolicyFailureReason.AuthorizationNotFound);
            }

            if (!ReferenceEquals(state.Authorization, authorization))
            {
                throw Failure(ExportPolicyFailureReason.AuthorizationNotFound);
            }

            if (state.Lifecycle != AuthorizationLifecycle.Active)
            {
                throw Failure(
                    ExportPolicyFailureReason.AuthorizationAlreadyConsumed);
            }

            if (!ReferenceEquals(state.Request, request))
            {
                state.Lifecycle = AuthorizationLifecycle.Consumed;
                throw Failure(
                    ExportPolicyFailureReason.AuthorizationRequestMismatch);
            }

            state.Lifecycle = AuthorizationLifecycle.Consuming;
        }

        PolicyEvaluation current;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = EvaluateCore(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            MarkConsumed(state);
            throw;
        }

        if (current.IssueCodes != ExportPolicyIssueCode.None
            || current.EvidenceDigest != authorization.EvidenceDigest)
        {
            MarkConsumed(state);
            ExportPolicyFailureReason reason =
                current.IssueCodes == ExportPolicyIssueCode.ActorNotAuthorized
                    ? ExportPolicyFailureReason.AuthorizationRevoked
                    : ExportPolicyFailureReason.AuthorizationStale;
            throw Failure(reason);
        }

        var consumption = new ExportPolicyConsumption(
            authorization,
            current.SatisfiedGateStatus,
            request);
        MarkConsumed(state);
        try
        {
            return immediateOperation(consumption);
        }
        finally
        {
            consumption.Expire();
        }
    }

    private PolicyEvaluation EvaluateCore(
        ExportPolicyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return EvaluateCoreUnsafe(request, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            return PolicyEvaluation.Blocked(
                ExportPolicyIssueCode.InvalidEvidence,
                ExportPolicyDigest.ComputeFallbackEvidenceDigest(request));
        }
    }

    private PolicyEvaluation EvaluateCoreUnsafe(
        ExportPolicyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RepositorySnapshot? before = CaptureRepositorySnapshot(request);
        if (before is null)
        {
            return PolicyEvaluation.Blocked(
                ExportPolicyIssueCode.RepositoryEvidenceMissing,
                ExportPolicyDigest.ComputeFallbackEvidenceDigest(request));
        }

        IReadOnlyList<AuthorizationRole> rolesBefore =
            _authorizationService.GetRoles(request.ActorSid);
        ExportPolicyIssueCode issues = ExportPolicyIssueCode.None;
        var requirements = new List<ExportPolicyResolutionRequirement>();
        var performerResults = new List<PerformerPolicyResult>();
        var sensitivitySubjects = new List<ContentDigest>();

        bool actorAuthorized = IsActorAuthorized(request, rolesBefore);
        if (!actorAuthorized)
        {
            issues |= ExportPolicyIssueCode.ActorNotAuthorized;
        }

        ValidateRepositoryAndRequestContext(request, before, ref issues);
        ReviewState? currentReviewState = ValidateReviewState(
            request,
            before.ReviewEvidence,
            ref issues);

        IReadOnlyList<StoredApproval> validApprovals = ValidateApprovals(
            request,
            before.ReviewEvidence,
            ref issues);
        var validContentApprovals = validApprovals
            .Where(static approval =>
                approval.ActorRole == ReviewActorRole.ContentApprover)
            .ToDictionary(static approval => approval.Id);
        ValidateRequiredApprovalRoles(
            before.ReviewEvidence,
            validApprovals,
            ref issues);
        ValidatePublication(request, ref issues);

        cancellationToken.ThrowIfCancellationRequested();
        ContentDigest graphDigest =
            ExportPolicyDigest.ComputeRenderGraphDigest(request.Graph);
        UsedOriginEvidence origins = ResolveUsedOrigins(request);
        if (!origins.IsValid)
        {
            issues |= ExportPolicyIssueCode.ProvenanceInvalid;
        }

        bool rightsSatisfied = ValidateRights(
            request,
            before,
            origins.AssetIds,
            ref issues);
        ValidateRenderGraph(
            request,
            rightsSatisfied,
            cancellationToken,
            ref issues);
        ValidatePerformerEvidence(
            request,
            origins.SourceSpans,
            performerResults,
            cancellationToken,
            ref issues);

        var validResolutions = ValidateResolutions(
            request,
            validContentApprovals,
            ref issues);
        var recognizedResolutionSubjects = new HashSet<
            ExportPolicyRequest.ResolutionKey>();
        ValidateSensitivity(
            request,
            origins.SourceSpans,
            validResolutions,
            recognizedResolutionSubjects,
            sensitivitySubjects,
            requirements,
            cancellationToken,
            ref issues);
        ValidateWarnings(
            request,
            validResolutions,
            recognizedResolutionSubjects,
            requirements,
            ref issues);
        ValidateNoExtraResolutions(
            validResolutions,
            recognizedResolutionSubjects,
            ref issues);
        ValidateRenderQcBinding(
            request,
            before.RenderArtifact,
            graphDigest,
            cancellationToken,
            ref issues);

        cancellationToken.ThrowIfCancellationRequested();
        RepositorySnapshot? after = CaptureRepositorySnapshot(request);
        IReadOnlyList<AuthorizationRole> rolesAfter =
            _authorizationService.GetRoles(request.ActorSid);
        if (after is null)
        {
            issues |= ExportPolicyIssueCode.RepositoryEvidenceMissing;
        }
        else if (before.Digest != after.Digest
            || !RolesEqual(rolesBefore, rolesAfter))
        {
            issues |= ExportPolicyIssueCode.RepositoryEvidenceChanged;
        }

        ContentDigest evidenceDigest = ExportPolicyDigest.ComputeEvidenceDigest(
            request,
            before.Digest,
            rolesBefore,
            currentReviewState,
            graphDigest,
            performerResults,
            sensitivitySubjects);
        ExportPolicyResolutionRequirement[] canonicalRequirements = requirements
            .GroupBy(static requirement =>
                new ExportPolicyRequest.ResolutionKey(
                    requirement.Kind,
                    requirement.SubjectDigest.Hex))
            .Select(static group => group.First())
            .OrderBy(static requirement => requirement.Kind)
            .ThenBy(
                static requirement => requirement.SubjectDigest.Hex,
                StringComparer.Ordinal)
            .ToArray();

        ReviewGateStatus gateStatus = issues == ExportPolicyIssueCode.None
            ? GetSatisfiedGateStatus(request)
            : ReviewGateStatus.None;
        return new PolicyEvaluation(
            issues,
            evidenceDigest,
            gateStatus,
            canonicalRequirements);
    }

    private RepositorySnapshot? CaptureRepositorySnapshot(
        ExportPolicyRequest request)
    {
        StoredProject? project = _projectStore.GetProject(request.Project.Id);
        StoredCompositionVersion? version = _projectStore.GetCompositionVersion(
            request.CompositionVersion.Id);
        StoredVersionEvidence? review = _reviewStore.GetVersionEvidence(
            request.CompositionVersion.Id);
        if (project is null || version is null || review is null)
        {
            return null;
        }

        HashSet<EntityId<AuditEventIdTag>> requiredAuditIds = request.RightsExceptions
            .Select(static exception => exception.AuditEventId)
            .ToHashSet();
        StoredAuditEvent[] auditEvents = requiredAuditIds.Count == 0
            ? []
            : _reviewStore.GetProjectAuditEvents(request.Project.Id)
                .Where(auditEvent => requiredAuditIds.Contains(auditEvent.Id))
                .OrderBy(static auditEvent => auditEvent.Id.ToString(), StringComparer.Ordinal)
                .ToArray();
        StoredArtifact? renderArtifact = request.RenderQcBinding is null
            ? null
            : _artifactStore.GetArtifact(
                request.RenderQcBinding.RenderArtifactId);
        ContentDigest digest = ExportPolicyDigest.ComputeRepositoryDigest(
            project,
            version,
            review,
            auditEvents,
            renderArtifact);
        return new RepositorySnapshot(
            project,
            version,
            review,
            auditEvents,
            renderArtifact,
            digest);
    }

    private static bool IsActorAuthorized(
        ExportPolicyRequest request,
        IReadOnlyList<AuthorizationRole> roles)
    {
        AuthorizationOperation operation = request.Boundary switch
        {
            ExportPolicyBoundary.FinalRender =>
                AuthorizationOperation.StartApprovedRender,
            ExportPolicyBoundary.Export =>
                AuthorizationOperation.ExportComposition,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return request.ActingRole == AuthorizationRole.Editor
            && roles.Contains(request.ActingRole)
            && PermissionMatrix.CanPerform(request.ActingRole, operation);
    }

    private static void ValidateRepositoryAndRequestContext(
        ExportPolicyRequest request,
        RepositorySnapshot repository,
        ref ExportPolicyIssueCode issues)
    {
        CompositionVersion version = request.CompositionVersion;
        RenderGraphValidationContext context = request.ValidationContext;
        ContentDigest requestProjectDigest =
            ExportPolicyDigest.ComputeProjectDigest(request.Project);
        ContentDigest contextProjectDigest =
            ExportPolicyDigest.ComputeProjectDigest(context.Project);

        bool matches = version.ProjectId == request.Project.Id
            && repository.Project.Id == request.Project.Id
            && repository.Project.PublicationScope
                == request.Project.PublicationScope
            && repository.Project.Deadline == request.Project.Deadline
            && string.Equals(
                repository.Project.CanonicalJson,
                ProjectSnapshotJson.WriteProject(request.Project),
                StringComparison.Ordinal)
            && repository.Version.Id == version.Id
            && repository.Version.ProjectId == version.ProjectId
            && repository.Version.ParentVersionId == version.ParentVersionId
            && repository.Version.ContentDigest == version.ContentDigest
            && repository.Version.CreatedAt.EqualsExact(version.CreatedAt)
            && string.Equals(
                repository.Version.CanonicalJson,
                CompositionSnapshotJson.WriteVersion(version),
                StringComparison.Ordinal)
            && repository.ReviewEvidence.ProjectId == version.ProjectId
            && repository.ReviewEvidence.CompositionVersionId == version.Id
            && repository.ReviewEvidence.CompositionVersionDigest
                == version.ContentDigest
            && contextProjectDigest == requestProjectDigest
            && context.CompositionVersion.Id == version.Id
            && context.CompositionVersion.ProjectId == version.ProjectId
            && context.CompositionVersion.ContentDigest == version.ContentDigest
            && string.Equals(
                repository.Version.CanonicalJson,
                CompositionSnapshotJson.WriteVersion(
                    context.CompositionVersion),
                StringComparison.Ordinal)
            && request.Graph.CompositionVersionId == version.Id
            && request.Graph.CompositionVersionDigest == version.ContentDigest;
        if (!matches)
        {
            issues |= ExportPolicyIssueCode.ContextMismatch;
        }

        try
        {
            if (version.ContentDigest !=
                RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                    version.Elements))
            {
                issues |= ExportPolicyIssueCode.ContextMismatch;
            }
        }
        catch
        {
            issues |= ExportPolicyIssueCode.InvalidEvidence;
        }
    }

    private static ReviewState? ValidateReviewState(
        ExportPolicyRequest request,
        StoredVersionEvidence evidence,
        ref ExportPolicyIssueCode issues)
    {
        if (evidence.ReviewRecords.Count == 0)
        {
            issues |= ExportPolicyIssueCode.ReviewStateNotReady;
            return null;
        }

        ReviewState current = evidence.ReviewRecords[^1].State;
        bool allowed = request.Boundary switch
        {
            ExportPolicyBoundary.FinalRender =>
                current is ReviewState.Approved or ReviewState.FinalRendering,
            ExportPolicyBoundary.Export => current == ReviewState.QualityControl,
            _ => false,
        };
        if (!allowed)
        {
            issues |= ExportPolicyIssueCode.ReviewStateNotReady;
        }

        return current;
    }

    private IReadOnlyList<StoredApproval> ValidateApprovals(
        ExportPolicyRequest request,
        StoredVersionEvidence evidence,
        ref ExportPolicyIssueCode issues)
    {
        try
        {
            ApprovalValidityResult result = _approvalService.EvaluateValidity(
                request.CompositionVersion,
                evidence.Approvals);
            if (result.StaleApprovals.Count != 0
                || result.ValidApprovals.Count != evidence.Approvals.Count)
            {
                issues |= ExportPolicyIssueCode.ApprovalEvidenceInvalid;
            }

            return result.ValidApprovals;
        }
        catch
        {
            issues |= ExportPolicyIssueCode.ApprovalEvidenceInvalid;
            return [];
        }
    }

    private static void ValidateRequiredApprovalRoles(
        StoredVersionEvidence evidence,
        IReadOnlyList<StoredApproval> validApprovals,
        ref ExportPolicyIssueCode issues)
    {
        bool contentApproved = validApprovals.Any(static approval =>
            approval.ActorRole == ReviewActorRole.ContentApprover);
        bool brandRequired = evidence.ReviewRecords.Any(static record =>
            record.State == ReviewState.BrandReview);
        bool brandApproved = validApprovals.Any(static approval =>
            approval.ActorRole == ReviewActorRole.BrandApprover);
        if (!contentApproved || brandRequired && !brandApproved)
        {
            issues |= ExportPolicyIssueCode.RequiredApprovalMissing;
        }
    }

    private static void ValidatePublication(
        ExportPolicyRequest request,
        ref ExportPolicyIssueCode issues)
    {
        PublicationScope scope = request.Project.PublicationScope;
        if (scope is PublicationScope.PublicationForbidden
            or PublicationScope.PendingDecision)
        {
            issues |= ExportPolicyIssueCode.PublicationScopeBlocked;
            return;
        }

        if (request.Destination.Destination == ReviewExportDestination.External
            && scope != PublicationScope.ExternalPublicationApproved)
        {
            issues |= ExportPolicyIssueCode.DestinationNotAllowed;
        }
    }

    private static UsedOriginEvidence ResolveUsedOrigins(
        ExportPolicyRequest request)
    {
        var assetIds = new HashSet<EntityId<AssetIdTag>>();
        var sourceSpans = new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> availableSpans =
            request.ValidationContext.SourceSpans.ToDictionary(
                static span => span.Id);
        HashSet<EntityId<EditorialRecordIdTag>> editorialIds =
            request.ValidationContext.EditorialRecordIds.ToHashSet();
        Dictionary<EntityId<AssetIdTag>, Asset> assets = request.Project.Assets
            .ToDictionary(static asset => asset.Id);
        bool valid = true;

        void AddOrigin(OriginRef origin)
        {
            switch (origin.Kind)
            {
                case OriginKind.Asset
                    when origin.AssetId is EntityId<AssetIdTag> assetId
                        && assets.ContainsKey(assetId):
                    _ = assetIds.Add(assetId);
                    break;
                case OriginKind.SourceSpan
                    when origin.SourceSpanId is EntityId<SourceSpanIdTag> spanId
                        && availableSpans.TryGetValue(spanId, out SourceSpan? span)
                        && assets.TryGetValue(span.AssetId, out Asset? sourceAsset)
                        && sourceAsset.ContentDigest == span.SourceDigest:
                    _ = assetIds.Add(span.AssetId);
                    sourceSpans.TryAdd(span.Id, span);
                    break;
                case OriginKind.EditorialRecord
                    when origin.EditorialRecordId is
                        EntityId<EditorialRecordIdTag> editorialId
                        && editorialIds.Contains(editorialId):
                    break;
                default:
                    valid = false;
                    break;
            }
        }

        foreach (CompositionElement element in request.CompositionVersion.Elements)
        {
            AddOrigin(element.Origin);
        }

        foreach (TimelineMapEntry entry in request.ValidationContext.TimelineMap.Entries)
        {
            AddOrigin(entry.Origin);
        }

        foreach (RenderGraphInput input in request.Graph.Inputs)
        {
            AddOrigin(input.Origin);
        }

        foreach (RenderNode node in request.Graph.Nodes)
        {
            if (node.Origin is not null)
            {
                AddOrigin(node.Origin);
            }
        }

        return new UsedOriginEvidence(
            assetIds,
            sourceSpans.Values
                .OrderBy(static span => span.Id.ToString(), StringComparer.Ordinal)
                .ToArray(),
            valid);
    }

    private static bool ValidateRights(
        ExportPolicyRequest request,
        RepositorySnapshot repository,
        IReadOnlySet<EntityId<AssetIdTag>> usedAssetIds,
        ref ExportPolicyIssueCode issues)
    {
        Dictionary<EntityId<AssetIdTag>, RecordedRightsException> exceptions =
            request.RightsExceptions.ToDictionary(
                static exception => exception.AssetId);
        bool external = request.Destination.Destination
            == ReviewExportDestination.External;
        bool satisfied = true;

        foreach (EntityId<AssetIdTag> assetId in usedAssetIds)
        {
            Asset? asset = request.Project.Assets.SingleOrDefault(candidate =>
                candidate.Id == assetId);
            if (asset is null)
            {
                issues |= ExportPolicyIssueCode.RightsNotSatisfied;
                satisfied = false;
                continue;
            }

            exceptions.TryGetValue(assetId, out RecordedRightsException? exception);
            if (!external)
            {
                if (asset.Rights.Status == RightsStatus.Rejected)
                {
                    issues |= ExportPolicyIssueCode.RightsNotSatisfied;
                    satisfied = false;
                }

                if (exception is not null)
                {
                    issues |= ExportPolicyIssueCode.RightsExceptionInvalid;
                    satisfied = false;
                }

                continue;
            }

            try
            {
                RightsPolicyDecision decision = RightsPolicy.EvaluateExternalUse(
                    asset,
                    request.CompositionVersion,
                    request.ValidationContext.Profile,
                    exception);
                if (decision.HoldExternalOutput)
                {
                    issues |= ExportPolicyIssueCode.RightsNotSatisfied;
                    satisfied = false;
                }
                else if (decision.AppliedException is not null
                    && !HasMatchingRightsAuditEvent(
                        decision.AppliedException,
                        repository.AuditEvents))
                {
                    issues |= ExportPolicyIssueCode.RightsExceptionInvalid;
                    satisfied = false;
                }
            }
            catch
            {
                issues |= ExportPolicyIssueCode.RightsExceptionInvalid;
                satisfied = false;
            }
        }

        if (exceptions.Keys.Any(assetId => !usedAssetIds.Contains(assetId)))
        {
            issues |= ExportPolicyIssueCode.RightsExceptionInvalid;
            satisfied = false;
        }

        return satisfied;
    }

    private static bool HasMatchingRightsAuditEvent(
        RecordedRightsException exception,
        IReadOnlyList<StoredAuditEvent> auditEvents)
    {
        StoredAuditEvent? audit = auditEvents.SingleOrDefault(candidate =>
            candidate.Id == exception.AuditEventId);
        return audit is not null
            && audit.ProjectId == exception.ProjectId
            && audit.Kind == AuditEventKind.RightsDecisionRecorded
            && string.Equals(
                audit.ActorId,
                exception.ActorId,
                StringComparison.Ordinal)
            && audit.ActorRole == ReviewActorRole.RightsApprover
            && audit.OccurredAt.EqualsExact(exception.DecidedAt)
            && string.Equals(
                audit.Reason,
                exception.Reason,
                StringComparison.Ordinal)
            && audit.CompositionVersionId == exception.CompositionVersionId
            && audit.CompositionVersionDigest
                == exception.CompositionVersionDigest;
    }

    private static void ValidateRenderGraph(
        ExportPolicyRequest request,
        bool rightsSatisfied,
        CancellationToken cancellationToken,
        ref ExportPolicyIssueCode issues)
    {
        try
        {
            RenderGraphValidationResult result = RenderGraphValidator.Validate(
                request.Graph,
                request.ValidationContext,
                cancellationToken);
            bool hasStructuralIssue = result.Issues.Any(issue =>
                issue.Kind is not RenderGraphValidationIssueKind
                    .ExternalRightsNotApproved
                    and not RenderGraphValidationIssueKind.RightsRejected);
            if (hasStructuralIssue)
            {
                issues |= ExportPolicyIssueCode.ProvenanceInvalid;
            }

            if (result.Issues.Any(static issue =>
                    issue.Kind == RenderGraphValidationIssueKind.RightsRejected)
                || result.Issues.Any(static issue =>
                    issue.Kind == RenderGraphValidationIssueKind
                        .ExternalRightsNotApproved)
                    && !rightsSatisfied)
            {
                issues |= ExportPolicyIssueCode.RightsNotSatisfied;
            }
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            issues |= ExportPolicyIssueCode.ProvenanceInvalid;
        }
    }

    private static void ValidatePerformerEvidence(
        ExportPolicyRequest request,
        IReadOnlyList<SourceSpan> usedSourceSpans,
        ICollection<PerformerPolicyResult> results,
        CancellationToken cancellationToken,
        ref ExportPolicyIssueCode issues)
    {
        HashSet<EntityId<TranscriptRevisionIdTag>> usedRevisionIds =
            usedSourceSpans
                .Where(static span => span.TranscriptRevisionId is not null)
                .Select(static span => span.TranscriptRevisionId!)
                .ToHashSet();
        HashSet<EntityId<TranscriptRevisionIdTag>> suppliedRevisionIds =
            request.PerformerEvidence
                .Select(static evidence => evidence.TranscriptRevisionId)
                .ToHashSet();
        if (request.Project.Performers.Count != 0
            && usedRevisionIds.Any(id => !suppliedRevisionIds.Contains(id)))
        {
            issues |= ExportPolicyIssueCode.PerformerConfirmationRequired;
        }

        if (suppliedRevisionIds.Any(id => !usedRevisionIds.Contains(id)))
        {
            issues |= ExportPolicyIssueCode.ContextMismatch;
        }

        Dictionary<EntityId<TranscriptRevisionIdTag>, ExportTextSensitivityEvidence>
            sensitivityByRevision = request.SensitivityEvidence.ToDictionary(
                static evidence => evidence.RevisionId);
        foreach (ExportPerformerEvidence evidence in request.PerformerEvidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PerformerPolicyResult result = PerformerPolicy.Evaluate(
                    request.Project,
                    evidence.AssignmentHistory,
                    evidence.RecordedAt,
                    evidence.Confirmations,
                    cancellationToken);
                results.Add(result);
                if (result.RequiresWarning)
                {
                    issues |= ExportPolicyIssueCode.PerformerConfirmationRequired;
                }

                if (sensitivityByRevision.TryGetValue(
                        evidence.TranscriptRevisionId,
                        out ExportTextSensitivityEvidence? transcript)
                    && (transcript.CanonicalTranscriptDigest is null
                        || transcript.CanonicalTranscriptDigest
                            != evidence.AssignmentHistory.TranscriptRevisionDigest))
                {
                    issues |= ExportPolicyIssueCode.ContextMismatch;
                }
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested
                    && exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch
            {
                issues |= ExportPolicyIssueCode.PerformerConfirmationRequired;
            }
        }
    }

    private static IReadOnlyDictionary<
        ExportPolicyRequest.ResolutionKey,
        ExportPolicyResolutionEvidence> ValidateResolutions(
        ExportPolicyRequest request,
        IReadOnlyDictionary<EntityId<ApprovalIdTag>, StoredApproval>
            validContentApprovals,
        ref ExportPolicyIssueCode issues)
    {
        var valid = new Dictionary<
            ExportPolicyRequest.ResolutionKey,
            ExportPolicyResolutionEvidence>();
        foreach (ExportPolicyResolutionEvidence resolution in request.Resolutions)
        {
            var key = new ExportPolicyRequest.ResolutionKey(
                resolution.Kind,
                resolution.SubjectDigest.Hex);
            bool matches = resolution.ProjectId == request.Project.Id
                && resolution.CompositionVersionId
                    == request.CompositionVersion.Id
                && resolution.CompositionVersionDigest
                    == request.CompositionVersion.ContentDigest
                && validContentApprovals.TryGetValue(
                    resolution.ApprovalId,
                    out StoredApproval? approval)
                && approval.ApprovedAt >= resolution.ResolvedAt;
            if (!matches)
            {
                issues |= ExportPolicyIssueCode.ResolutionEvidenceInvalid;
                continue;
            }

            valid.Add(key, resolution);
        }

        return valid;
    }

    private static void ValidateSensitivity(
        ExportPolicyRequest request,
        IReadOnlyList<SourceSpan> usedSourceSpans,
        IReadOnlyDictionary<
            ExportPolicyRequest.ResolutionKey,
            ExportPolicyResolutionEvidence> validResolutions,
        ISet<ExportPolicyRequest.ResolutionKey> recognizedResolutionSubjects,
        ICollection<ContentDigest> sensitivitySubjects,
        ICollection<ExportPolicyResolutionRequirement> requirements,
        CancellationToken cancellationToken,
        ref ExportPolicyIssueCode issues)
    {
        bool external = request.Destination.Destination
            == ReviewExportDestination.External;
        EntityId<TranscriptRevisionIdTag>[] usedRevisionIds = usedSourceSpans
            .Where(static span => span.TranscriptRevisionId is not null)
            .Select(static span => span.TranscriptRevisionId!)
            .Distinct()
            .OrderBy(static id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        Dictionary<EntityId<TranscriptRevisionIdTag>, ExportTextSensitivityEvidence>
            supplied = request.SensitivityEvidence.ToDictionary(
                static evidence => evidence.RevisionId);

        if (request.SensitivityEvidence.Any(evidence =>
            !usedRevisionIds.Contains(evidence.RevisionId)))
        {
            issues |= ExportPolicyIssueCode.ContextMismatch;
        }

        if (external && usedRevisionIds.Any(id => !supplied.ContainsKey(id)))
        {
            issues |= ExportPolicyIssueCode.SensitivityEvidenceMissing;
        }

        foreach (EntityId<TranscriptRevisionIdTag> revisionId in usedRevisionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!supplied.TryGetValue(
                    revisionId,
                    out ExportTextSensitivityEvidence? evidence))
            {
                continue;
            }

            SourceSpan[] revisionSpans = usedSourceSpans
                .Where(span => span.TranscriptRevisionId == revisionId)
                .ToArray();
            if (!SpansMatchTranscript(revisionSpans, evidence.Revision))
            {
                issues |= ExportPolicyIssueCode.ProvenanceInvalid;
                continue;
            }

            TextSensitivityScanResult scan = TextSensitivityScanner.Scan(
                evidence.Revision,
                evidence.DictionaryEntries);
            cancellationToken.ThrowIfCancellationRequested();
            ContentDigest subject =
                ExportPolicyDigest.ComputeSensitivitySubjectDigest(
                    evidence,
                    scan);
            sensitivitySubjects.Add(subject);

            if (!external)
            {
                continue;
            }

            if (!scan.IsComplete)
            {
                issues |= ExportPolicyIssueCode.SensitivityScanIncomplete;
                continue;
            }

            if (scan.Findings.Count == 0)
            {
                continue;
            }

            var key = new ExportPolicyRequest.ResolutionKey(
                ExportPolicyResolutionKind.TextSensitivity,
                subject.Hex);
            _ = recognizedResolutionSubjects.Add(key);
            if (!validResolutions.ContainsKey(key))
            {
                issues |= ExportPolicyIssueCode.SensitivityResolutionRequired;
                requirements.Add(new ExportPolicyResolutionRequirement(
                    ExportPolicyResolutionKind.TextSensitivity,
                    subject));
            }
        }
    }

    private static bool SpansMatchTranscript(
        IReadOnlyList<SourceSpan> spans,
        TranscriptRevision revision)
    {
        Dictionary<
            EntityId<SourceSpanIdTag>,
            HashSet<EntityId<TranscriptTokenIdTag>>> tokenIdsBySourceSpan =
            revision.Utterances
                .GroupBy(static utterance => utterance.SourceSpanId)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .SelectMany(static utterance => utterance.Tokens)
                        .Select(static token => token.Id)
                        .ToHashSet());
        foreach (SourceSpan span in spans)
        {
            if (span.TranscriptRevisionId != revision.Id
                || !tokenIdsBySourceSpan.TryGetValue(
                    span.Id,
                    out HashSet<EntityId<TranscriptTokenIdTag>>? tokenIds)
                || span.TranscriptTokenIds.Any(id => !tokenIds.Contains(id)))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateWarnings(
        ExportPolicyRequest request,
        IReadOnlyDictionary<
            ExportPolicyRequest.ResolutionKey,
            ExportPolicyResolutionEvidence> validResolutions,
        ISet<ExportPolicyRequest.ResolutionKey> recognizedResolutionSubjects,
        ICollection<ExportPolicyResolutionRequirement> requirements,
        ref ExportPolicyIssueCode issues)
    {
        bool external = request.Destination.Destination
            == ReviewExportDestination.External;
        foreach (ExportPolicyWarningEvidence warning in request.Warnings)
        {
            var key = new ExportPolicyRequest.ResolutionKey(
                ExportPolicyResolutionKind.Warning,
                warning.SubjectDigest.Hex);
            _ = recognizedResolutionSubjects.Add(key);
            bool applies = warning.Applicability
                == ExportPolicyWarningApplicability.AllOutputs
                || external;
            if (applies && !validResolutions.ContainsKey(key))
            {
                issues |= ExportPolicyIssueCode.UnresolvedWarning;
                requirements.Add(new ExportPolicyResolutionRequirement(
                    ExportPolicyResolutionKind.Warning,
                    warning.SubjectDigest));
            }
        }
    }

    private static void ValidateNoExtraResolutions(
        IReadOnlyDictionary<
            ExportPolicyRequest.ResolutionKey,
            ExportPolicyResolutionEvidence> validResolutions,
        IReadOnlySet<ExportPolicyRequest.ResolutionKey> recognizedSubjects,
        ref ExportPolicyIssueCode issues)
    {
        if (validResolutions.Keys.Any(key => !recognizedSubjects.Contains(key)))
        {
            issues |= ExportPolicyIssueCode.ResolutionEvidenceInvalid;
        }
    }

    private void ValidateRenderQcBinding(
        ExportPolicyRequest request,
        StoredArtifact? artifact,
        ContentDigest graphDigest,
        CancellationToken cancellationToken,
        ref ExportPolicyIssueCode issues)
    {
        ExportRenderQcBinding? binding = request.RenderQcBinding;
        if (binding is null)
        {
            if (request.Boundary == ExportPolicyBoundary.Export)
            {
                issues |= ExportPolicyIssueCode.RenderQcMismatch;
            }

            return;
        }

        if (request.Boundary != ExportPolicyBoundary.Export)
        {
            issues |= ExportPolicyIssueCode.RenderQcMismatch;
            return;
        }

        bool matches = binding.ProjectId == request.Project.Id
            && binding.CompositionVersionId == request.CompositionVersion.Id
            && binding.CompositionVersionDigest
                == request.CompositionVersion.ContentDigest
            && binding.RenderGraphId == request.Graph.Id
            && binding.RenderGraphDigest == graphDigest
            && binding.QcVerifiedRenderDigest == binding.RenderContentDigest
            && artifact is not null
            && artifact.Id == binding.RenderArtifactId
            && artifact.ProjectId == request.Project.Id
            && artifact.Kind == ArtifactKind.Render
            && artifact.ContentDigest == binding.RenderContentDigest
            && artifact.ByteSize == binding.RenderByteSize;
        if (!matches)
        {
            issues |= ExportPolicyIssueCode.RenderQcMismatch;
            return;
        }

        try
        {
            StoredArtifact verified = _artifactStore.ValidateRegisteredAsync(
                    binding.RenderArtifactId,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (verified.Id != artifact!.Id
                || verified.ProjectId != artifact.ProjectId
                || verified.Kind != artifact.Kind
                || verified.ContentDigest != artifact.ContentDigest
                || verified.ByteSize != artifact.ByteSize
                || verified.CreatedAt != artifact.CreatedAt
                || !string.Equals(
                    verified.StorageReference,
                    artifact.StorageReference,
                    StringComparison.Ordinal))
            {
                issues |= ExportPolicyIssueCode.RenderQcMismatch;
            }
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            issues |= ExportPolicyIssueCode.RenderQcMismatch;
        }
    }

    private static bool RolesEqual(
        IReadOnlyList<AuthorizationRole> first,
        IReadOnlyList<AuthorizationRole> second) =>
        first.Order().SequenceEqual(second.Order());

    private static ReviewGateStatus GetSatisfiedGateStatus(
        ExportPolicyRequest request)
    {
        ReviewGateStatus common =
            ReviewGateStatus.RequiredApprovalEvidenceRecorded
            | ReviewGateStatus.ApprovedContentDigestMatched;
        if (request.Boundary == ExportPolicyBoundary.FinalRender)
        {
            return common
                | ReviewGateStatus.RightsPolicySatisfied
                | ReviewGateStatus.FinalRenderPolicySatisfied;
        }

        if (request.Destination.Destination == ReviewExportDestination.External)
        {
            common |= ReviewGateStatus.RightsPolicySatisfied;
        }

        return common | ReviewGateStatus.ExportPolicySatisfied;
    }

    private void EnsureOperationIdAvailable(
        EntityId<ExportOperationIdTag> operationId)
    {
        lock (_authorizationSync)
        {
            if (_authorizations.ContainsKey(operationId))
            {
                throw Failure(ExportPolicyFailureReason.DuplicateOperationId);
            }
        }
    }

    private void MarkConsumed(AuthorizationState state)
    {
        lock (_authorizationSync)
        {
            state.Lifecycle = AuthorizationLifecycle.Consumed;
        }
    }

    private static ExportPolicyException Failure(
        ExportPolicyFailureReason reason) =>
        new(reason);

    private enum AuthorizationLifecycle
    {
        Active,
        Consuming,
        Consumed,
    }

    private sealed class AuthorizationState
    {
        internal AuthorizationState(
            ExportPolicyAuthorization authorization,
            ExportPolicyRequest request,
            AuthorizationLifecycle lifecycle)
        {
            Authorization = authorization;
            Request = request;
            Lifecycle = lifecycle;
        }

        internal ExportPolicyAuthorization Authorization { get; }

        internal ExportPolicyRequest Request { get; }

        internal AuthorizationLifecycle Lifecycle { get; set; }
    }

    private sealed record RepositorySnapshot(
        StoredProject Project,
        StoredCompositionVersion Version,
        StoredVersionEvidence ReviewEvidence,
        IReadOnlyList<StoredAuditEvent> AuditEvents,
        StoredArtifact? RenderArtifact,
        ContentDigest Digest);

    private sealed record UsedOriginEvidence(
        IReadOnlySet<EntityId<AssetIdTag>> AssetIds,
        IReadOnlyList<SourceSpan> SourceSpans,
        bool IsValid);

    private sealed record PolicyEvaluation(
        ExportPolicyIssueCode IssueCodes,
        ContentDigest EvidenceDigest,
        ReviewGateStatus SatisfiedGateStatus,
        IReadOnlyList<ExportPolicyResolutionRequirement> ResolutionRequirements)
    {
        internal static PolicyEvaluation Blocked(
            ExportPolicyIssueCode issueCode,
            ContentDigest evidenceDigest) =>
            new(
                issueCode,
                evidenceDigest,
                ReviewGateStatus.None,
                Array.Empty<ExportPolicyResolutionRequirement>());
    }
}

internal static class ProjectSnapshotJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string WriteProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            WriteString(writer, "id", project.Id.ToString());
            WriteString(writer, "contentName", project.ContentName);
            WriteString(writer, "purpose", project.Purpose);
            WriteString(writer, "audience", project.Audience);
            WriteString(
                writer,
                "publicationScope",
                GetPublicationScopeText(project.PublicationScope));
            if (project.Deadline is DateTimeOffset deadline)
            {
                WriteTimestamp(writer, "deadline", deadline);
            }

            writer.WritePropertyName("assets");
            writer.WriteStartArray();
            foreach (Asset asset in project.Assets.OrderBy(
                static item => item.Id.ToString(),
                StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteString(writer, "id", asset.Id.ToString());
                WriteString(writer, "fileName", asset.FileName);
                WriteString(writer, "mediaType", asset.MediaType);
                WriteString(writer, "contentDigest", asset.ContentDigest.Hex);
                writer.WriteNumber("byteSize", asset.ByteSize);
                writer.WritePropertyName("rights");
                writer.WriteStartObject();
                WriteString(
                    writer,
                    "status",
                    GetRightsStatusText(asset.Rights.Status));
                if (asset.Rights.EvidenceReference is not null)
                {
                    WriteString(
                        writer,
                        "evidenceReference",
                        asset.Rights.EvidenceReference);
                }

                WriteString(writer, "allowedUse", asset.Rights.AllowedUse);
                writer.WriteBoolean(
                    "externalPublicationAllowed",
                    asset.Rights.ExternalPublicationAllowed);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("performers");
            writer.WriteStartArray();
            foreach (Performer performer in project.Performers.OrderBy(
                static item => item.Id.ToString(),
                StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteString(writer, "id", performer.Id.ToString());
                WriteString(writer, "displayName", performer.DisplayName);
                WriteString(writer, "role", performer.Role);
                if (performer.TitleConfirmedAt is DateTimeOffset confirmedAt)
                {
                    WriteTimestamp(writer, "titleConfirmedAt", confirmedAt);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return StrictUtf8.GetString(
            CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    private static string GetPublicationScopeText(PublicationScope scope) =>
        scope switch
        {
            PublicationScope.InternalOnly => "internalOnly",
            PublicationScope.SpecificGroupOnly => "specificGroupOnly",
            PublicationScope.ExternalCandidate => "externalCandidate",
            PublicationScope.ExternalPublicationApproved =>
                "externalPublicationApproved",
            PublicationScope.PublicationForbidden => "publicationForbidden",
            PublicationScope.PendingDecision => "pendingDecision",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static string GetRightsStatusText(RightsStatus status) =>
        status switch
        {
            RightsStatus.Unconfirmed => "unconfirmed",
            RightsStatus.Approved => "approved",
            RightsStatus.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value) =>
        WriteString(
            writer,
            propertyName,
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static void WriteString(
        Utf8JsonWriter writer,
        string propertyName,
        string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Project snapshot text must be valid Unicode.",
                exception);
        }

        writer.WriteString(propertyName, value);
    }
}

internal static class ExportPolicyContract
{
    internal static void ValidateIssueCodes(ExportPolicyIssueCode issueCodes)
    {
        const ExportPolicyIssueCode allCodes =
            ExportPolicyIssueCode.ActorNotAuthorized
            | ExportPolicyIssueCode.RepositoryEvidenceMissing
            | ExportPolicyIssueCode.RepositoryEvidenceChanged
            | ExportPolicyIssueCode.ContextMismatch
            | ExportPolicyIssueCode.ReviewStateNotReady
            | ExportPolicyIssueCode.ApprovalEvidenceInvalid
            | ExportPolicyIssueCode.RequiredApprovalMissing
            | ExportPolicyIssueCode.PublicationScopeBlocked
            | ExportPolicyIssueCode.DestinationNotAllowed
            | ExportPolicyIssueCode.RightsNotSatisfied
            | ExportPolicyIssueCode.RightsExceptionInvalid
            | ExportPolicyIssueCode.PerformerConfirmationRequired
            | ExportPolicyIssueCode.SensitivityEvidenceMissing
            | ExportPolicyIssueCode.SensitivityScanIncomplete
            | ExportPolicyIssueCode.SensitivityResolutionRequired
            | ExportPolicyIssueCode.UnresolvedWarning
            | ExportPolicyIssueCode.ProvenanceInvalid
            | ExportPolicyIssueCode.ResolutionEvidenceInvalid
            | ExportPolicyIssueCode.RenderQcMismatch
            | ExportPolicyIssueCode.InvalidEvidence;
        if ((issueCodes & ~allCodes) != ExportPolicyIssueCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(issueCodes));
        }
    }
}

internal static class ExportPolicyDigest
{
    private const string EvidenceDomain =
        "HubRadio.MovieCreator.ExportPolicy.Evidence/v1";
    private const string RepositoryDomain =
        "HubRadio.MovieCreator.ExportPolicy.Repository/v1";
    private const string GraphDomain =
        "HubRadio.MovieCreator.ExportPolicy.RenderGraph/v1";
    private const string ProjectDomain =
        "HubRadio.MovieCreator.ExportPolicy.Project/v1";
    private const string TranscriptDomain =
        "HubRadio.MovieCreator.ExportPolicy.Transcript/v1";
    private const string SensitivityDomain =
        "HubRadio.MovieCreator.ExportPolicy.Sensitivity/v1";
    private const string WarningDomain =
        "HubRadio.MovieCreator.ExportPolicy.Warning/v1";

    internal static ContentDigest ComputeWarningSubjectDigest(
        EntityId<ExportPolicyWarningIdTag> id,
        ContentDigest evidenceDigest,
        ExportPolicyWarningApplicability applicability)
    {
        using var hash = new StableHash(WarningDomain);
        hash.Add("id", id.ToString());
        hash.Add("evidenceDigest", evidenceDigest);
        hash.Add("applicability", checked((int)applicability));
        return hash.Finish();
    }

    internal static ContentDigest ComputeSensitivitySubjectDigest(
        ExportTextSensitivityEvidence evidence,
        TextSensitivityScanResult scan)
    {
        using var hash = new StableHash(SensitivityDomain);
        hash.Add("revisionId", evidence.RevisionId.ToString());
        hash.Add("transcriptDigest", evidence.TranscriptDigest);
        hash.Add("ruleSetVersion", scan.RuleSetVersion);
        hash.Add("isComplete", scan.IsComplete);
        hash.Add("dictionaryCount", evidence.DictionaryEntries.Count);
        foreach (TermEntry entry in evidence.DictionaryEntries
            .OrderBy(static entry => entry.Id.ToString(), StringComparer.Ordinal))
        {
            hash.Add("termId", entry.Id.ToString());
            hash.Add("termScope", checked((int)entry.Scope.Kind));
            hash.Add("termProjectId", entry.Scope.ProjectId?.ToString());
            hash.Add("termSeriesId", entry.Scope.SeriesId?.ToString());
            hash.Add("termWritten", entry.WrittenForm);
            hash.Add("termReading", entry.Reading);
            hash.Add("termVersion", entry.Version);
        }

        hash.Add("findingCount", scan.Findings.Count);
        foreach (TextSensitivityFinding finding in scan.Findings)
        {
            hash.Add("findingRevisionId", finding.RevisionId.ToString());
            hash.Add("findingUtteranceId", finding.UtteranceId.ToString());
            hash.Add("findingSourceSpanId", finding.SourceSpanId.ToString());
            hash.Add("findingKind", checked((int)finding.Kind));
            hash.Add("findingOffset", finding.Utf16Offset);
            hash.Add("findingLength", finding.Utf16Length);
        }

        return hash.Finish();
    }

    internal static ContentDigest ComputeTranscriptEvidenceDigest(
        TranscriptRevision revision)
    {
        using var hash = new StableHash(TranscriptDomain);
        hash.Add("id", revision.Id.ToString());
        hash.Add("parentId", revision.ParentRevisionId?.ToString());
        hash.Add("changeSource", checked((int)revision.ChangeSource));
        hash.Add("createdAt", revision.CreatedAt);
        hash.Add("utteranceCount", revision.Utterances.Count);
        foreach (Utterance utterance in revision.Utterances)
        {
            hash.Add("utteranceId", utterance.Id.ToString());
            hash.Add("sourceSpanId", utterance.SourceSpanId.ToString());
            hash.Add("speakerId", utterance.SpeakerId.ToString());
            hash.Add("speakerLabel", utterance.SpeakerLabel);
            hash.Add("utteranceText", utterance.Text);
            AddRange(hash, "utteranceRange", utterance.Range);
            hash.Add("utteranceConfidence", utterance.RecognitionConfidence);
            hash.Add("sentenceCount", utterance.Sentences.Count);
            foreach (Sentence sentence in utterance.Sentences)
            {
                hash.Add("sentenceId", sentence.Id.ToString());
                hash.Add("sentenceText", sentence.Text);
                hash.Add("tokenCount", sentence.Tokens.Count);
                foreach (Token token in sentence.Tokens)
                {
                    hash.Add("tokenId", token.Id.ToString());
                    hash.Add("tokenText", token.Text);
                    hash.Add("tokenHasRange", token.Range.HasValue);
                    if (token.Range is TimeRange tokenRange)
                    {
                        AddRange(hash, "tokenRange", tokenRange);
                    }

                    hash.Add("tokenConfidence", token.RecognitionConfidence);
                    hash.Add(
                        "tokenManualTiming",
                        token.ManualTimingAdjustmentRequired);
                }
            }
        }

        return hash.Finish();
    }

    internal static ContentDigest ComputeStrictUtf8Digest(string value)
    {
        var encoding = new System.Text.UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        return ContentDigest.Compute(encoding.GetBytes(value));
    }

    internal static ContentDigest ComputeProjectDigest(Project project)
    {
        using var hash = new StableHash(ProjectDomain);
        hash.Add("id", project.Id.ToString());
        hash.Add("contentName", project.ContentName);
        hash.Add("purpose", project.Purpose);
        hash.Add("audience", project.Audience);
        hash.Add("publicationScope", checked((int)project.PublicationScope));
        hash.Add("deadline", project.Deadline);
        hash.Add("assetCount", project.Assets.Count);
        foreach (Asset asset in project.Assets
            .OrderBy(static asset => asset.Id.ToString(), StringComparer.Ordinal))
        {
            hash.Add("assetId", asset.Id.ToString());
            hash.Add("assetFileName", asset.FileName);
            hash.Add("assetMediaType", asset.MediaType);
            hash.Add("assetDigest", asset.ContentDigest);
            hash.Add("assetSize", asset.ByteSize);
            hash.Add("rightsStatus", checked((int)asset.Rights.Status));
            hash.Add("rightsReference", asset.Rights.EvidenceReference);
            hash.Add("rightsUse", asset.Rights.AllowedUse);
            hash.Add(
                "rightsExternal",
                asset.Rights.ExternalPublicationAllowed);
        }

        hash.Add("performerCount", project.Performers.Count);
        foreach (Performer performer in project.Performers
            .OrderBy(
                static performer => performer.Id.ToString(),
                StringComparer.Ordinal))
        {
            hash.Add("performerId", performer.Id.ToString());
            hash.Add("performerName", performer.DisplayName);
            hash.Add("performerRole", performer.Role);
            hash.Add("performerTitleAt", performer.TitleConfirmedAt);
        }

        return hash.Finish();
    }

    internal static ContentDigest ComputeRenderGraphDigest(RenderGraph graph)
    {
        using var hash = new StableHash(GraphDomain);
        hash.Add("id", graph.Id.ToString());
        hash.Add("compositionId", graph.CompositionVersionId.ToString());
        hash.Add("compositionDigest", graph.CompositionVersionDigest);
        hash.Add("timelineId", graph.TimelineMapId.ToString());
        hash.Add("timelineDigest", graph.TimelineMapDigest);
        hash.Add("profileId", graph.ProfileId.ToString());
        hash.Add("profileDigest", graph.ProfileDigest);
        hash.Add("layoutId", graph.LayoutSpecId.ToString());
        hash.Add("layoutDigest", graph.LayoutSpecDigest);
        hash.Add("templateId", graph.Template.Id.ToString());
        hash.Add("templateDigest", graph.Template.VersionDigest);
        hash.Add("inputCount", graph.Inputs.Count);
        foreach (RenderGraphInput input in graph.Inputs)
        {
            hash.Add("inputId", input.Id.ToString());
            hash.Add("inputKind", checked((int)input.Kind));
            hash.Add("inputDigest", input.ContentDigest);
            hash.Add("inputSize", input.ByteSize);
            AddOrigin(hash, "inputOrigin", input.Origin);
        }

        hash.Add("fontCount", graph.Fonts.Count);
        foreach (RenderFontEvidence font in graph.Fonts)
        {
            hash.Add("fontName", font.Name);
            hash.Add("fontDigest", font.FileDigest);
        }

        hash.Add("nodeCount", graph.Nodes.Count);
        foreach (RenderNode node in graph.Nodes)
        {
            hash.Add("nodeId", node.Id.ToString());
            hash.Add("nodeKind", checked((int)node.Kind));
            hash.Add("nodeInputCount", node.InputNodeIds.Count);
            foreach (EntityId<RenderNodeIdTag> inputId in node.InputNodeIds)
            {
                hash.Add("nodeInputId", inputId.ToString());
            }

            hash.Add("artifactInputId", node.ArtifactInputId?.ToString());
            AddRange(hash, "nodeOutput", node.OutputRange);
            hash.Add("hasSourceRange", node.SourceRange.HasValue);
            if (node.SourceRange is TimeRange sourceRange)
            {
                AddRange(hash, "nodeSource", sourceRange);
            }

            hash.Add("hasOrigin", node.Origin is not null);
            if (node.Origin is not null)
            {
                AddOrigin(hash, "nodeOrigin", node.Origin);
            }

            hash.Add("region", node.RegionName);
            hash.Add("hasCue", node.Cue is not null);
            if (node.Cue is not null)
            {
                hash.Add("cueId", node.Cue.Id.ToString());
                hash.Add("cueKind", checked((int)node.Cue.Kind));
                AddRange(hash, "cueRange", node.Cue.Range);
                hash.Add("cueRegion", node.Cue.RegionName);
            }

            hash.Add("hasTransition", node.Transition is not null);
            if (node.Transition is not null)
            {
                hash.Add(
                    "transitionKind",
                    checked((int)node.Transition.Kind));
                AddTime(hash, "transitionDuration", node.Transition.Duration);
                hash.Add(
                    "transitionDirection",
                    node.Transition.Direction is null
                        ? null
                        : checked((int)node.Transition.Direction.Value));
            }
        }

        hash.Add("outputNodeId", graph.OutputNodeId.ToString());
        return hash.Finish();
    }

    internal static ContentDigest ComputeRepositoryDigest(
        StoredProject project,
        StoredCompositionVersion version,
        StoredVersionEvidence review,
        IReadOnlyList<StoredAuditEvent> auditEvents,
        StoredArtifact? artifact)
    {
        using var hash = new StableHash(RepositoryDomain);
        hash.Add("projectId", project.Id.ToString());
        hash.Add("projectRevision", project.Revision);
        hash.Add("projectScope", checked((int)project.PublicationScope));
        hash.Add("projectDeadline", project.Deadline);
        hash.Add("projectUpdatedAt", project.UpdatedAt);
        hash.Add("projectPayload", project.CanonicalJson);
        hash.Add("versionId", version.Id.ToString());
        hash.Add("versionProjectId", version.ProjectId.ToString());
        hash.Add("versionParentId", version.ParentVersionId?.ToString());
        hash.Add("versionDigest", version.ContentDigest);
        hash.Add("versionCreatedAt", version.CreatedAt);
        hash.Add("versionPayload", version.CanonicalJson);

        hash.Add("reviewRecordCount", review.ReviewRecords.Count);
        foreach (StoredReviewRecord record in review.ReviewRecords)
        {
            hash.Add("reviewId", record.Id.ToString());
            hash.Add("reviewState", checked((int)record.State));
            hash.Add("reviewActor", record.ActorId);
            hash.Add("reviewActorRole", checked((int)record.ActorRole));
            hash.Add("reviewAt", record.RecordedAt);
            hash.Add("reviewReason", record.Reason);
        }

        hash.Add("commentCount", review.ReviewComments.Count);
        foreach (StoredReviewComment comment in review.ReviewComments)
        {
            hash.Add("commentId", comment.Id.ToString());
            AddTime(hash, "commentOutput", comment.OutputTime);
            hash.Add("commentSpanId", comment.SourceSpanId.ToString());
            hash.Add("commentActor", comment.ActorId);
            hash.Add("commentActorRole", checked((int)comment.ActorRole));
            hash.Add("commentAt", comment.CreatedAt);
            hash.Add("commentText", comment.Text);
        }

        hash.Add("approvalCount", review.Approvals.Count);
        foreach (StoredApproval approval in review.Approvals)
        {
            hash.Add("approvalId", approval.Id.ToString());
            hash.Add("approvalActor", approval.ActorId);
            hash.Add("approvalRole", checked((int)approval.ActorRole));
            hash.Add("approvalAt", approval.ApprovedAt);
            hash.Add("approvalReason", approval.Reason);
        }

        hash.Add("auditCount", auditEvents.Count);
        foreach (StoredAuditEvent audit in auditEvents)
        {
            hash.Add("auditId", audit.Id.ToString());
            hash.Add("auditKind", checked((int)audit.Kind));
            hash.Add("auditActor", audit.ActorId);
            hash.Add("auditRole", checked((int)audit.ActorRole));
            hash.Add("auditAt", audit.OccurredAt);
            hash.Add("auditReason", audit.Reason);
            hash.Add("auditVersionId", audit.CompositionVersionId?.ToString());
            hash.Add("auditVersionDigest", audit.CompositionVersionDigest);
        }

        hash.Add("hasArtifact", artifact is not null);
        if (artifact is not null)
        {
            hash.Add("artifactId", artifact.Id.ToString());
            hash.Add("artifactProjectId", artifact.ProjectId.ToString());
            hash.Add("artifactKind", checked((int)artifact.Kind));
            hash.Add("artifactReference", artifact.StorageReference);
            hash.Add("artifactDigest", artifact.ContentDigest);
            hash.Add("artifactSize", artifact.ByteSize);
            hash.Add("artifactCreatedAt", artifact.CreatedAt);
        }

        return hash.Finish();
    }

    internal static ContentDigest ComputeEvidenceDigest(
        ExportPolicyRequest request,
        ContentDigest repositoryDigest,
        IReadOnlyList<AuthorizationRole> roles,
        ReviewState? currentReviewState,
        ContentDigest graphDigest,
        IReadOnlyList<PerformerPolicyResult> performerResults,
        IReadOnlyList<ContentDigest> sensitivitySubjects)
    {
        using var hash = new StableHash(EvidenceDomain);
        hash.Add("operationId", request.OperationId.ToString());
        hash.Add("boundary", checked((int)request.Boundary));
        hash.Add("actor", request.ActorSid);
        hash.Add("actingRole", checked((int)request.ActingRole));
        hash.Add("destination", checked((int)request.Destination.Destination));
        hash.Add("destinationDigest", request.Destination.BindingDigest);
        hash.Add("repositoryDigest", repositoryDigest);
        hash.Add("projectDigest", ComputeProjectDigest(request.Project));
        hash.Add("compositionId", request.CompositionVersion.Id.ToString());
        hash.Add("compositionDigest", request.CompositionVersion.ContentDigest);
        hash.Add("graphDigest", graphDigest);
        hash.Add(
            "timelineDigest",
            RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                request.ValidationContext.TimelineMap));
        hash.Add("profileId", request.ValidationContext.Profile.Id.ToString());
        hash.Add(
            "profileDigest",
            request.ValidationContext.Profile.ProfileDigest);
        hash.Add("layoutId", request.ValidationContext.LayoutSpec.Id.ToString());
        hash.Add("layoutDigest", request.ValidationContext.LayoutSpecDigest);
        hash.Add("reviewState", currentReviewState is null
            ? null
            : checked((int)currentReviewState.Value));

        AuthorizationRole[] orderedRoles = roles.Order().ToArray();
        hash.Add("roleCount", orderedRoles.Length);
        foreach (AuthorizationRole role in orderedRoles)
        {
            hash.Add("role", checked((int)role));
        }

        hash.Add("rightsExceptionCount", request.RightsExceptions.Count);
        foreach (RecordedRightsException exception in request.RightsExceptions
            .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal))
        {
            hash.Add("rightsExceptionId", exception.Id.ToString());
            hash.Add("rightsAssetId", exception.AssetId.ToString());
            hash.Add("rightsAssetDigest", exception.AssetContentDigest);
            hash.Add("rightsFingerprint", exception.RightsRecordFingerprint);
            hash.Add("rightsAuditId", exception.AuditEventId.ToString());
            hash.Add("rightsVersionId", exception.CompositionVersionId.ToString());
            hash.Add("rightsVersionDigest", exception.CompositionVersionDigest);
            hash.Add("rightsProfileId", exception.RenderProfileId.ToString());
            hash.Add("rightsProfileDigest", exception.RenderProfileDigest);
        }

        hash.Add("performerResultCount", performerResults.Count);
        foreach (PerformerPolicyResult result in performerResults
            .OrderBy(
                static item => item.AssignmentRevisionId.ToString(),
                StringComparer.Ordinal))
        {
            hash.Add("performerProjectDigest", result.ProjectSnapshotDigest);
            hash.Add(
                "performerAssignmentId",
                result.AssignmentRevisionId.ToString());
            hash.Add(
                "performerAssignmentDigest",
                result.AssignmentSnapshotDigest);
            hash.Add("performerRecordedAt", result.RecordedAt);
            hash.Add("performerWarnings", checked((int)result.WarningCodes));
        }

        hash.Add("sensitivityCount", sensitivitySubjects.Count);
        foreach (ContentDigest subject in sensitivitySubjects
            .OrderBy(static item => item.Hex, StringComparer.Ordinal))
        {
            hash.Add("sensitivitySubject", subject);
        }

        hash.Add("warningCount", request.Warnings.Count);
        foreach (ExportPolicyWarningEvidence warning in request.Warnings
            .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal))
        {
            hash.Add("warningSubject", warning.SubjectDigest);
        }

        hash.Add("resolutionCount", request.Resolutions.Count);
        foreach (ExportPolicyResolutionEvidence resolution in request.Resolutions
            .OrderBy(static item => item.Id.ToString(), StringComparer.Ordinal))
        {
            hash.Add("resolutionId", resolution.Id.ToString());
            hash.Add("resolutionKind", checked((int)resolution.Kind));
            hash.Add("resolutionSubject", resolution.SubjectDigest);
            hash.Add("resolutionApprovalId", resolution.ApprovalId.ToString());
            hash.Add("resolutionAt", resolution.ResolvedAt);
        }

        hash.Add("hasRenderQc", request.RenderQcBinding is not null);
        if (request.RenderQcBinding is ExportRenderQcBinding binding)
        {
            hash.Add("renderArtifactId", binding.RenderArtifactId.ToString());
            hash.Add("renderDigest", binding.RenderContentDigest);
            hash.Add("renderSize", binding.RenderByteSize);
            hash.Add("qcRenderDigest", binding.QcVerifiedRenderDigest);
            hash.Add("qcEvidenceDigest", binding.QcEvidenceDigest);
        }

        return hash.Finish();
    }

    internal static ContentDigest ComputeFallbackEvidenceDigest(
        ExportPolicyRequest request)
    {
        using var hash = new StableHash(EvidenceDomain);
        hash.Add("operationId", request.OperationId.ToString());
        hash.Add("boundary", checked((int)request.Boundary));
        hash.Add("destination", checked((int)request.Destination.Destination));
        hash.Add("destinationDigest", request.Destination.BindingDigest);
        hash.Add("projectId", request.Project.Id.ToString());
        hash.Add("compositionId", request.CompositionVersion.Id.ToString());
        hash.Add("compositionDigest", request.CompositionVersion.ContentDigest);
        hash.Add("graphId", request.Graph.Id.ToString());
        return hash.Finish();
    }

    private static void AddOrigin(
        StableHash hash,
        string name,
        OriginRef origin)
    {
        hash.Add(name + ".kind", checked((int)origin.Kind));
        hash.Add(name + ".sourceSpanId", origin.SourceSpanId?.ToString());
        hash.Add(name + ".assetId", origin.AssetId?.ToString());
        hash.Add(
            name + ".editorialRecordId",
            origin.EditorialRecordId?.ToString());
    }

    private static void AddRange(
        StableHash hash,
        string name,
        TimeRange range)
    {
        AddTime(hash, name + ".start", range.Start);
        AddTime(hash, name + ".duration", range.Duration);
    }

    private static void AddTime(
        StableHash hash,
        string name,
        RationalTime time)
    {
        hash.Add(name + ".value", time.Value);
        hash.Add(name + ".rate", time.Rate);
    }

    private sealed class StableHash : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        private bool _finished;

        internal StableHash(string domain)
        {
            Add("domain", domain);
        }

        internal void Add(string name, ContentDigest? value) =>
            Add(name, value?.Hex);

        internal void Add(string name, bool value) =>
            AddBytes(name, [(byte)(value ? 1 : 0)]);

        internal void Add(string name, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            AddBytes(name, bytes);
        }

        internal void Add(string name, long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            AddBytes(name, bytes);
        }

        internal void Add(string name, double? value)
        {
            if (value is null)
            {
                Add(name, (string?)null);
                return;
            }

            Add(name, BitConverter.DoubleToInt64Bits(value.Value));
        }

        internal void Add(string name, DateTimeOffset value)
        {
            Add(name + ".utcTicks", value.UtcDateTime.Ticks);
            Add(name + ".offsetTicks", value.Offset.Ticks);
        }

        internal void Add(string name, DateTimeOffset? value)
        {
            Add(name + ".present", value.HasValue);
            if (value.HasValue)
            {
                Add(name, value.Value);
            }
        }

        internal void Add(string name, string? value)
        {
            AddBytes(name + ".present", [(byte)(value is null ? 0 : 1)]);
            if (value is null)
            {
                return;
            }

            byte[] bytes = new byte[checked(value.Length * sizeof(char))];
            for (int index = 0; index < value.Length; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    bytes.AsSpan(index * sizeof(char), sizeof(char)),
                    value[index]);
            }

            AddBytes(name, bytes);
        }

        internal ContentDigest Finish()
        {
            if (_finished)
            {
                throw new InvalidOperationException(
                    "An export-policy digest can be finalized only once.");
            }

            _finished = true;
            return ContentDigest.FromBytes(_hash.GetHashAndReset());
        }

        public void Dispose()
        {
            _hash.Dispose();
        }

        private void AddBytes(string name, ReadOnlySpan<byte> value)
        {
            AppendLengthPrefixed(CreateNameBytes(name));
            AppendLengthPrefixed(value);
        }

        private static byte[] CreateNameBytes(string value)
        {
            byte[] bytes = new byte[checked(value.Length * sizeof(char))];
            for (int index = 0; index < value.Length; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    bytes.AsSpan(index * sizeof(char), sizeof(char)),
                    value[index]);
            }

            return bytes;
        }

        private void AppendLengthPrefixed(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            _hash.AppendData(length);
            _hash.AppendData(value);
        }
    }
}