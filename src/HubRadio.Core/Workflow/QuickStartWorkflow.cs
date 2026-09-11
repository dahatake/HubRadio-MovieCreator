using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Workflow;

public sealed class QuickStartWorkflowEventIdTag
{
}

public enum QuickStartWorkflowState
{
    Editing,
    AutomatedEvaluation,
    Ready,
    ReadyWithWarnings,
    Failed,
    Exported,
}

public enum QuickStartWorkflowEventKind
{
    StartedEditing,
    EvaluationStarted,
    EvaluationCompleted,
    SelfReviewed,
    ChangesRequested,
    Exported,
    TechnicalReevaluationStarted,
}

public enum QuickStartActorKind
{
    User,
    System,
}

public enum QuickStartSystemActor
{
    AutomatedEvaluation,
    Delivery,
}

public enum QuickStartExportDestinationKind
{
    LocalInternalDelivery,
}

public enum QuickStartExportDenialReason
{
    None,
    StateNotReady,
    WorkflowBindingMismatch,
    StateRevisionMismatch,
    ProjectSnapshotBindingMissing,
    ProjectSnapshotBindingMismatch,
    ScopeNotInternalOnly,
    RightsNotAllowedForInternalUse,
    ExternalDestinationRequested,
    DestinationNotLocalInternal,
    SettingsBindingMismatch,
    EvaluationBindingMissing,
    EvaluationBindingMismatch,
    ArtifactBindingMismatch,
}

/// <summary>
/// Identifies the exact canonical project row used to begin one evaluation attempt.
/// Any project change requires another evaluation attempt before delivery.
/// </summary>
public sealed class QuickStartProjectSnapshot
{
    public QuickStartProjectSnapshot(
        int revision,
        ContentDigest canonicalPayloadDigest)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayloadDigest);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "A project snapshot revision must not be negative.");
        }

        Revision = revision;
        CanonicalPayloadDigest = canonicalPayloadDigest;
    }

    public int Revision { get; }

    public ContentDigest CanonicalPayloadDigest { get; }
}

public sealed class QuickStartActor
{
    public const int MaximumIdLength = 256;

    private const ulong MaximumIdentifierAuthority = (1UL << 48) - 1;
    private const string AutomatedEvaluationActorId = "automated-evaluation-v2";
    private const string DeliveryActorId = "delivery-v2";

    private QuickStartActor(
        QuickStartActorKind kind,
        string id,
        QuickStartSystemActor? systemActor)
    {
        Kind = kind;
        Id = id;
        SystemActor = systemActor;
    }

    public QuickStartActorKind Kind { get; }

    public string Id { get; }

    internal QuickStartSystemActor? SystemActor { get; }

    public static QuickStartActor User(string canonicalWindowsSid)
    {
        string sid = QuickStartWorkflowContract.RequireBoundedText(
            canonicalWindowsSid,
            MaximumIdLength,
            nameof(canonicalWindowsSid),
            "A quick-start user SID");
        ValidateCanonicalWindowsSid(sid, nameof(canonicalWindowsSid));
        return new QuickStartActor(QuickStartActorKind.User, sid, systemActor: null);
    }

    public static QuickStartActor System(QuickStartSystemActor actor)
    {
        QuickStartWorkflowContract.ValidateEnum(actor, nameof(actor));
        string id = actor switch
        {
            QuickStartSystemActor.AutomatedEvaluation =>
                AutomatedEvaluationActorId,
            QuickStartSystemActor.Delivery => DeliveryActorId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(actor),
                actor,
                "Unknown quick-start system actor."),
        };
        return new QuickStartActor(QuickStartActorKind.System, id, actor);
    }

    internal static QuickStartActor ParseCanonical(
        QuickStartActorKind kind,
        string id) => kind switch
        {
            QuickStartActorKind.User => User(id),
            QuickStartActorKind.System when string.Equals(
                id,
                AutomatedEvaluationActorId,
                StringComparison.Ordinal) => System(
                    QuickStartSystemActor.AutomatedEvaluation),
            QuickStartActorKind.System when string.Equals(
                id,
                DeliveryActorId,
                StringComparison.Ordinal) => System(QuickStartSystemActor.Delivery),
            QuickStartActorKind.System => throw new ArgumentException(
                "A quick-start system actor ID is not recognized.",
                nameof(id)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown quick-start actor kind."),
        };

    internal bool IsSystem(QuickStartSystemActor actor) =>
        Kind == QuickStartActorKind.System && SystemActor == actor;

    private static void ValidateCanonicalWindowsSid(
        string value,
        string parameterName)
    {
        string[] segments = value.Split('-', StringSplitOptions.None);
        if (segments.Length is < 4 or > 18 ||
            !string.Equals(segments[0], "S", StringComparison.Ordinal) ||
            !string.Equals(segments[1], "1", StringComparison.Ordinal) ||
            !IsCanonicalIdentifierAuthority(segments[2]))
        {
            throw new ArgumentException(
                "A quick-start user actor must be a canonical Windows SID.",
                parameterName);
        }

        for (int index = 3; index < segments.Length; index++)
        {
            if (!IsCanonicalDecimal(segments[index]) ||
                !uint.TryParse(
                    segments[index],
                    global::System.Globalization.NumberStyles.None,
                    global::System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                throw new ArgumentException(
                    "A quick-start user actor must be a canonical Windows SID.",
                    parameterName);
            }
        }
    }

    private static bool IsCanonicalIdentifierAuthority(string value)
    {
        if (IsCanonicalDecimal(value))
        {
            return uint.TryParse(
                value,
                global::System.Globalization.NumberStyles.None,
                global::System.Globalization.CultureInfo.InvariantCulture,
                out _);
        }

        if (value.Length != 14 ||
            !value.StartsWith("0x", StringComparison.Ordinal) ||
            value[2..].Any(static character =>
                character is not (>= '0' and <= '9') &&
                character is not (>= 'a' and <= 'f')))
        {
            return false;
        }

        return ulong.TryParse(
                value[2..],
                global::System.Globalization.NumberStyles.AllowHexSpecifier,
                global::System.Globalization.CultureInfo.InvariantCulture,
                out ulong authority) &&
            authority > uint.MaxValue &&
            authority <= MaximumIdentifierAuthority;
    }

    private static bool IsCanonicalDecimal(string value) =>
        value.Length > 0 &&
        (value.Length == 1 || value[0] != '0') &&
        value.All(static character => character is >= '0' and <= '9');
}

public sealed class QuickStartWorkflowBinding
{
    public QuickStartWorkflowBinding(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);

        ProjectId = projectId;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }
}

internal sealed class QuickStartExportFacts
{
    internal QuickStartExportFacts(
        QuickStartWorkflowBinding workflowBinding,
        QuickStartProjectSnapshot projectSnapshot,
        PublicationScope publicationScope,
        bool rightsAllowedForInternalUse,
        bool externalDestinationRequested,
        QuickStartExportDestinationKind destinationKind,
        QuickStartWorkflowState authorizedState,
        int authorizedRevision,
        ContentDigest settingsSnapshotDigest,
        EntityId<AutomatedEvaluationIdTag> evaluationId,
        AutomatedEvaluationStatus evaluationAggregateStatus,
        ContentDigest evaluationCanonicalDigest,
        EntityId<AutomatedEvaluationArtifactIdTag> artifactId,
        ContentDigest artifactDigest)
    {
        ArgumentNullException.ThrowIfNull(workflowBinding);
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        ArgumentNullException.ThrowIfNull(settingsSnapshotDigest);
        ArgumentNullException.ThrowIfNull(evaluationId);
        ArgumentNullException.ThrowIfNull(evaluationCanonicalDigest);
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(artifactDigest);
        QuickStartWorkflowContract.ValidateEnum(publicationScope, nameof(publicationScope));
        QuickStartWorkflowContract.ValidateEnum(destinationKind, nameof(destinationKind));
        QuickStartWorkflowContract.ValidateEnum(authorizedState, nameof(authorizedState));
        QuickStartWorkflowContract.ValidateEnum(
            evaluationAggregateStatus,
            nameof(evaluationAggregateStatus));
        if (authorizedState is not (
            QuickStartWorkflowState.Ready or
            QuickStartWorkflowState.ReadyWithWarnings))
        {
            throw new ArgumentException(
                "A quick-start export can authorize only a ready workflow state.",
                nameof(authorizedState));
        }

        if (authorizedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authorizedRevision),
                "An authorized workflow revision must be positive.");
        }

        WorkflowBinding = workflowBinding;
        ProjectSnapshot = projectSnapshot;
        PublicationScope = publicationScope;
        RightsAllowedForInternalUse = rightsAllowedForInternalUse;
        ExternalDestinationRequested = externalDestinationRequested;
        DestinationKind = destinationKind;
        AuthorizedState = authorizedState;
        AuthorizedRevision = authorizedRevision;
        SettingsSnapshotDigest = settingsSnapshotDigest;
        EvaluationId = evaluationId;
        EvaluationAggregateStatus = evaluationAggregateStatus;
        EvaluationCanonicalDigest = evaluationCanonicalDigest;
        ArtifactId = artifactId;
        ArtifactDigest = artifactDigest;
    }

    public QuickStartWorkflowBinding WorkflowBinding { get; }

    public QuickStartProjectSnapshot ProjectSnapshot { get; }

    public PublicationScope PublicationScope { get; }

    public bool RightsAllowedForInternalUse { get; }

    public bool ExternalDestinationRequested { get; }

    public QuickStartExportDestinationKind DestinationKind { get; }

    public QuickStartWorkflowState AuthorizedState { get; }

    public int AuthorizedRevision { get; }

    public ContentDigest SettingsSnapshotDigest { get; }

    public EntityId<AutomatedEvaluationIdTag> EvaluationId { get; }

    public AutomatedEvaluationStatus EvaluationAggregateStatus { get; }

    public ContentDigest EvaluationCanonicalDigest { get; }

    public EntityId<AutomatedEvaluationArtifactIdTag> ArtifactId { get; }

    public ContentDigest ArtifactDigest { get; }
}

public sealed class QuickStartExportDecision
{
    internal QuickStartExportDecision(
        bool allowed,
        QuickStartExportDenialReason denialReason)
    {
        Allowed = allowed;
        DenialReason = denialReason;
    }

    public bool Allowed { get; }

    public QuickStartExportDenialReason DenialReason { get; }
}

internal sealed class QuickStartExportPolicy
{
    public QuickStartExportDecision Evaluate(
        QuickStartWorkflow workflow,
        QuickStartExportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(facts);

        if (workflow.State is not (
            QuickStartWorkflowState.Ready or
            QuickStartWorkflowState.ReadyWithWarnings))
        {
            return Denied(QuickStartExportDenialReason.StateNotReady);
        }

        if (!QuickStartWorkflowContract.SameBinding(
                workflow.Binding,
                facts.WorkflowBinding))
        {
            return Denied(QuickStartExportDenialReason.WorkflowBindingMismatch);
        }

        if (facts.AuthorizedRevision != workflow.Revision ||
            facts.AuthorizedState != workflow.State)
        {
            return Denied(QuickStartExportDenialReason.StateRevisionMismatch);
        }

        if (workflow.ProjectSnapshot is null)
        {
            return Denied(
            QuickStartExportDenialReason.ProjectSnapshotBindingMissing);
        }

        if (!QuickStartWorkflowContract.SameProjectSnapshot(
            workflow.ProjectSnapshot,
            facts.ProjectSnapshot))
        {
            return Denied(
            QuickStartExportDenialReason.ProjectSnapshotBindingMismatch);
        }

        if (facts.PublicationScope != PublicationScope.InternalOnly)
        {
            return Denied(QuickStartExportDenialReason.ScopeNotInternalOnly);
        }

        if (!facts.RightsAllowedForInternalUse)
        {
            return Denied(
                QuickStartExportDenialReason.RightsNotAllowedForInternalUse);
        }

        if (facts.ExternalDestinationRequested)
        {
            return Denied(
                QuickStartExportDenialReason.ExternalDestinationRequested);
        }

        if (facts.DestinationKind !=
            QuickStartExportDestinationKind.LocalInternalDelivery)
        {
            return Denied(
                QuickStartExportDenialReason.DestinationNotLocalInternal);
        }

        if (workflow.SettingsSnapshotDigest != facts.SettingsSnapshotDigest)
        {
            return Denied(QuickStartExportDenialReason.SettingsBindingMismatch);
        }

        if (workflow.EvaluationId is null ||
            workflow.EvaluationCanonicalDigest is null)
        {
            return Denied(QuickStartExportDenialReason.EvaluationBindingMissing);
        }

        if (workflow.EvaluationId != facts.EvaluationId ||
            workflow.EvaluationAggregateStatus != facts.EvaluationAggregateStatus ||
            workflow.EvaluationCanonicalDigest != facts.EvaluationCanonicalDigest)
        {
            return Denied(QuickStartExportDenialReason.EvaluationBindingMismatch);
        }

        if (workflow.ArtifactId is null ||
            workflow.ArtifactDigest is null ||
            workflow.ArtifactId != facts.ArtifactId ||
            workflow.ArtifactDigest != facts.ArtifactDigest)
        {
            return Denied(QuickStartExportDenialReason.ArtifactBindingMismatch);
        }

        return new QuickStartExportDecision(
            allowed: true,
            QuickStartExportDenialReason.None);
    }

    private static QuickStartExportDecision Denied(
        QuickStartExportDenialReason reason) =>
        new(allowed: false, reason);
}

public static class QuickStartEvaluationPolicy
{
    private static readonly AutomatedEvaluationRuleId[] BlockingRules =
    [
        AutomatedEvaluationRuleId.Codec,
        AutomatedEvaluationRuleId.FullDecode,
        AutomatedEvaluationRuleId.Duration,
        AutomatedEvaluationRuleId.Provenance,
        AutomatedEvaluationRuleId.SettingsBinding,
    ];

    public static QuickStartWorkflowState DetermineCompletedState(
        AutomatedEvaluationRun evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.ArtifactId is null || evaluation.ArtifactDigest is null)
        {
            return QuickStartWorkflowState.Failed;
        }

        if (evaluation.Rules.Any(static result =>
                result.Status == AutomatedEvaluationStatus.Fail))
        {
            return QuickStartWorkflowState.Failed;
        }

        foreach (AutomatedEvaluationRuleId blockingRule in BlockingRules)
        {
            AutomatedEvaluationRuleResult? result = evaluation.Rules.SingleOrDefault(
                candidate => candidate.RuleId == blockingRule);
            if (result is null || result.Status != AutomatedEvaluationStatus.Pass)
            {
                return QuickStartWorkflowState.Failed;
            }
        }

        return evaluation.Rules.Any(static result =>
            result.Status is AutomatedEvaluationStatus.Warning or
                AutomatedEvaluationStatus.NotEvaluated)
            ? QuickStartWorkflowState.ReadyWithWarnings
            : QuickStartWorkflowState.Ready;
    }
}

public sealed class QuickStartWorkflowEvent
{
    public const int MaximumCanonicalJsonUtf8Bytes = 16 * 1024;

    internal QuickStartWorkflowEvent(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflowEventKind kind,
        QuickStartWorkflowBinding binding,
        QuickStartActor actor,
        DateTimeOffset occurredAt,
        QuickStartProjectSnapshot? projectSnapshot,
        ContentDigest? settingsSnapshotDigest,
        EntityId<AutomatedEvaluationIdTag>? evaluationId,
        DateTimeOffset? evaluationEvaluatedAt,
        AutomatedEvaluationStatus? evaluationAggregateStatus,
        ContentDigest? evaluationCanonicalDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest,
        QuickStartWorkflowState? evaluationResultState,
        string? reasonCode,
        QuickStartExportFacts? exportFacts)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(actor);
        QuickStartWorkflowContract.ValidateEnum(kind, nameof(kind));
        QuickStartWorkflowContract.ValidateUtcTimestamp(occurredAt, nameof(occurredAt));
        if (evaluationEvaluatedAt is DateTimeOffset evaluatedAt)
        {
            QuickStartWorkflowContract.ValidateUtcTimestamp(
                evaluatedAt,
                nameof(evaluationEvaluatedAt));
            if (occurredAt < evaluatedAt)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(evaluationEvaluatedAt),
                    "An evaluation timestamp cannot follow its completion event.");
            }
        }

        if ((artifactId is null) != (artifactDigest is null))
        {
            throw new ArgumentException(
                "Artifact ID and digest must either both be present or both be absent.",
                nameof(artifactId));
        }

        if (evaluationResultState is not null)
        {
            QuickStartWorkflowContract.ValidateEnum(
                evaluationResultState.Value,
                nameof(evaluationResultState));
        }

        if (evaluationAggregateStatus is not null)
        {
            QuickStartWorkflowContract.ValidateEnum(
                evaluationAggregateStatus.Value,
                nameof(evaluationAggregateStatus));
        }

        if (reasonCode is not null)
        {
            QuickStartWorkflowContract.ValidateReasonCode(
                reasonCode,
                nameof(reasonCode));
        }

        ValidateShape(
            kind,
            actor,
            projectSnapshot,
            settingsSnapshotDigest,
            evaluationId,
            evaluationEvaluatedAt,
            evaluationAggregateStatus,
            evaluationCanonicalDigest,
            artifactId,
            artifactDigest,
            evaluationResultState,
            reasonCode,
            exportFacts,
            binding);

        EventId = eventId;
        Kind = kind;
        Binding = binding;
        Actor = actor;
        OccurredAt = occurredAt;
        ProjectSnapshot = projectSnapshot;
        SettingsSnapshotDigest = settingsSnapshotDigest;
        EvaluationId = evaluationId;
        EvaluationEvaluatedAt = evaluationEvaluatedAt;
        EvaluationAggregateStatus = evaluationAggregateStatus;
        EvaluationCanonicalDigest = evaluationCanonicalDigest;
        ArtifactId = artifactId;
        ArtifactDigest = artifactDigest;
        EvaluationResultState = evaluationResultState;
        ReasonCode = reasonCode;
        ExportFacts = exportFacts;
        CanonicalJson = QuickStartWorkflowJsonCodec.SerializeEvent(this);
        byte[] canonicalUtf8 = QuickStartWorkflowContract.StrictUtf8.GetBytes(
            CanonicalJson);
        if (canonicalUtf8.Length > MaximumCanonicalJsonUtf8Bytes)
        {
            throw new ArgumentException(
                "The quick-start workflow event exceeds the supported byte limit.",
                nameof(reasonCode));
        }

        CanonicalDigest = ContentDigest.Compute(canonicalUtf8);
    }

    public EntityId<QuickStartWorkflowEventIdTag> EventId { get; }

    public QuickStartWorkflowEventKind Kind { get; }

    public QuickStartWorkflowBinding Binding { get; }

    public QuickStartActor Actor { get; }

    public DateTimeOffset OccurredAt { get; }

    public QuickStartProjectSnapshot? ProjectSnapshot { get; }

    public ContentDigest? SettingsSnapshotDigest { get; }

    public EntityId<AutomatedEvaluationIdTag>? EvaluationId { get; }

    public DateTimeOffset? EvaluationEvaluatedAt { get; }

    public AutomatedEvaluationStatus? EvaluationAggregateStatus { get; }

    public ContentDigest? EvaluationCanonicalDigest { get; }

    public EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId { get; }

    public ContentDigest? ArtifactDigest { get; }

    public QuickStartWorkflowState? EvaluationResultState { get; }

    public string? ReasonCode { get; }

    internal QuickStartExportFacts? ExportFacts { get; }

    public string CanonicalJson { get; }

    public ContentDigest CanonicalDigest { get; }

    public static QuickStartWorkflowEvent StartedEditing(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflowBinding binding,
        QuickStartActor actor,
        DateTimeOffset occurredAt) =>
        new(
            eventId,
            QuickStartWorkflowEventKind.StartedEditing,
            binding,
            actor,
            occurredAt,
            projectSnapshot: null,
            settingsSnapshotDigest: null,
            evaluationId: null,
            evaluationEvaluatedAt: null,
            evaluationAggregateStatus: null,
            evaluationCanonicalDigest: null,
            artifactId: null,
            artifactDigest: null,
            evaluationResultState: null,
            reasonCode: null,
            exportFacts: null);

    public static QuickStartWorkflowEvent EvaluationStarted(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflowBinding binding,
        QuickStartActor actor,
        DateTimeOffset occurredAt,
        ContentDigest settingsSnapshotDigest,
        QuickStartProjectSnapshot projectSnapshot) =>
        new(
            eventId,
            QuickStartWorkflowEventKind.EvaluationStarted,
            binding,
            actor,
            occurredAt,
            projectSnapshot,
            settingsSnapshotDigest,
            evaluationId: null,
            evaluationEvaluatedAt: null,
            evaluationAggregateStatus: null,
            evaluationCanonicalDigest: null,
            artifactId: null,
            artifactDigest: null,
            evaluationResultState: null,
            reasonCode: null,
            exportFacts: null);

    /// <summary>
    /// Explicitly supersedes an active (including interrupted) or completed attempt
    /// using the same binding and settings and a freshly provided project snapshot.
    /// Append with a new unique event ID; history is retained without an automatic
    /// retry or a synthetic failure result.
    /// </summary>
    public static QuickStartWorkflowEvent TechnicalReevaluationStarted(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflow workflow,
        QuickStartActor actor,
        DateTimeOffset occurredAt,
        QuickStartProjectSnapshot projectSnapshot)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        if (workflow.State is not (
            QuickStartWorkflowState.AutomatedEvaluation or
                QuickStartWorkflowState.Ready or
                QuickStartWorkflowState.ReadyWithWarnings or
                QuickStartWorkflowState.Failed) ||
            workflow.SettingsSnapshotDigest is not ContentDigest settingsSnapshotDigest)
        {
            throw new InvalidOperationException(
                "A technical reevaluation can start only from an active or completed quick-start evaluation with bound settings.");
        }

        return new QuickStartWorkflowEvent(
            eventId,
            QuickStartWorkflowEventKind.TechnicalReevaluationStarted,
            workflow.Binding,
            actor,
            occurredAt,
            projectSnapshot,
            settingsSnapshotDigest,
            evaluationId: null,
            evaluationEvaluatedAt: null,
            evaluationAggregateStatus: null,
            evaluationCanonicalDigest: null,
            artifactId: null,
            artifactDigest: null,
            evaluationResultState: null,
            reasonCode: null,
            exportFacts: null);
    }

    /// <summary>
    /// Completes only the active evaluation attempt. Producers must set the run's
    /// InputDigest to evaluatingWorkflow.GetEvaluationInputDigest() and StartedAt
    /// to a time at or after that attempt's start event, not an earlier render time.
    /// </summary>
    public static QuickStartWorkflowEvent EvaluationCompleted(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflow evaluatingWorkflow,
        AutomatedEvaluationRun evaluation,
        QuickStartActor actor,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(evaluatingWorkflow);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluatingWorkflow.State !=
                QuickStartWorkflowState.AutomatedEvaluation ||
            evaluatingWorkflow.ProjectSnapshot is null ||
            evaluatingWorkflow.SettingsSnapshotDigest !=
                evaluation.SettingsSnapshotDigest ||
            !QuickStartWorkflowContract.SameBinding(
                evaluatingWorkflow.Binding,
                new QuickStartWorkflowBinding(
                    evaluation.ProjectId,
                    evaluation.CompositionVersionId,
                    evaluation.CompositionVersionDigest)))
        {
            throw new InvalidOperationException(
                "An evaluation can complete only for its exact evaluating workflow snapshot.");
        }

        if (evaluation.InputDigest != evaluatingWorkflow.GetEvaluationInputDigest() ||
            evaluation.StartedAt < evaluatingWorkflow.GetActiveEvaluationStart().OccurredAt)
        {
            throw new InvalidOperationException(
                "An evaluation must bind to the active start event digest and must not start before that event.");
        }

        if (occurredAt < evaluation.EvaluatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAt),
                "A quick-start evaluation-completed event cannot precede the evaluated timestamp.");
        }

        return new QuickStartWorkflowEvent(
            eventId,
            QuickStartWorkflowEventKind.EvaluationCompleted,
            new QuickStartWorkflowBinding(
                evaluation.ProjectId,
                evaluation.CompositionVersionId,
                evaluation.CompositionVersionDigest),
            actor,
            occurredAt,
            evaluatingWorkflow.ProjectSnapshot,
            evaluation.SettingsSnapshotDigest,
            evaluation.EvaluationId,
            evaluation.EvaluatedAt,
            evaluation.AggregateStatus,
            evaluation.CanonicalDigest,
            evaluation.ArtifactId,
            evaluation.ArtifactDigest,
            QuickStartEvaluationPolicy.DetermineCompletedState(evaluation),
            reasonCode: null,
            exportFacts: null);
    }

    public static QuickStartWorkflowEvent SelfReviewed(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflowBinding binding,
        QuickStartActor actor,
        DateTimeOffset occurredAt) =>
        new(
            eventId,
            QuickStartWorkflowEventKind.SelfReviewed,
            binding,
            actor,
            occurredAt,
            projectSnapshot: null,
            settingsSnapshotDigest: null,
            evaluationId: null,
            evaluationEvaluatedAt: null,
            evaluationAggregateStatus: null,
            evaluationCanonicalDigest: null,
            artifactId: null,
            artifactDigest: null,
            evaluationResultState: null,
            reasonCode: null,
            exportFacts: null);

    public static QuickStartWorkflowEvent ChangesRequested(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflowBinding binding,
        QuickStartActor actor,
        DateTimeOffset occurredAt,
        string reasonCode) =>
        new(
            eventId,
            QuickStartWorkflowEventKind.ChangesRequested,
            binding,
            actor,
            occurredAt,
            projectSnapshot: null,
            settingsSnapshotDigest: null,
            evaluationId: null,
            evaluationEvaluatedAt: null,
            evaluationAggregateStatus: null,
            evaluationCanonicalDigest: null,
            artifactId: null,
            artifactDigest: null,
            evaluationResultState: null,
            reasonCode,
            exportFacts: null);

    internal static QuickStartWorkflowEvent Exported(
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        QuickStartWorkflow workflow,
        QuickStartActor actor,
        DateTimeOffset occurredAt,
        QuickStartExportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(facts);
        QuickStartExportDecision decision = new QuickStartExportPolicy().Evaluate(
            workflow,
            facts);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(
                $"Quick-start export was denied: {decision.DenialReason}.");
        }

        return new QuickStartWorkflowEvent(
            eventId,
            QuickStartWorkflowEventKind.Exported,
            workflow.Binding,
            actor,
            occurredAt,
            facts.ProjectSnapshot,
            facts.SettingsSnapshotDigest,
            facts.EvaluationId,
            evaluationEvaluatedAt: null,
            facts.EvaluationAggregateStatus,
            facts.EvaluationCanonicalDigest,
            facts.ArtifactId,
            facts.ArtifactDigest,
            evaluationResultState: null,
            reasonCode: null,
            facts);
    }

    public static QuickStartWorkflowEvent ParseCanonical(string canonicalJson) =>
        QuickStartWorkflowJsonCodec.ParseEvent(canonicalJson, allowExport: false);

    internal static QuickStartWorkflowEvent ParseStoredCanonical(
        string canonicalJson) =>
        QuickStartWorkflowJsonCodec.ParseEvent(canonicalJson, allowExport: true);

    private static void ValidateShape(
        QuickStartWorkflowEventKind kind,
        QuickStartActor actor,
        QuickStartProjectSnapshot? projectSnapshot,
        ContentDigest? settingsSnapshotDigest,
        EntityId<AutomatedEvaluationIdTag>? evaluationId,
        DateTimeOffset? evaluationEvaluatedAt,
        AutomatedEvaluationStatus? evaluationAggregateStatus,
        ContentDigest? evaluationCanonicalDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest,
        QuickStartWorkflowState? evaluationResultState,
        string? reasonCode,
        QuickStartExportFacts? exportFacts,
        QuickStartWorkflowBinding binding)
    {
        bool noEvaluationBinding =
            projectSnapshot is null &&
            settingsSnapshotDigest is null &&
            evaluationId is null &&
            evaluationEvaluatedAt is null &&
            evaluationAggregateStatus is null &&
            evaluationCanonicalDigest is null &&
            artifactId is null &&
            artifactDigest is null &&
            evaluationResultState is null;
        switch (kind)
        {
            case QuickStartWorkflowEventKind.StartedEditing when
                actor.Kind == QuickStartActorKind.User &&
                noEvaluationBinding && reasonCode is null && exportFacts is null:
            case QuickStartWorkflowEventKind.SelfReviewed when
                actor.Kind == QuickStartActorKind.User &&
                noEvaluationBinding && reasonCode is null && exportFacts is null:
                return;
            case QuickStartWorkflowEventKind.ChangesRequested when
                actor.Kind == QuickStartActorKind.User &&
                noEvaluationBinding && reasonCode is not null && exportFacts is null:
                return;
            case QuickStartWorkflowEventKind.EvaluationStarted when
                actor.IsSystem(QuickStartSystemActor.AutomatedEvaluation) &&
                projectSnapshot is not null &&
                settingsSnapshotDigest is not null &&
                evaluationId is null &&
                evaluationEvaluatedAt is null &&
                evaluationAggregateStatus is null &&
                evaluationCanonicalDigest is null &&
                artifactId is null &&
                artifactDigest is null &&
                evaluationResultState is null &&
                reasonCode is null && exportFacts is null:
                return;
            case QuickStartWorkflowEventKind.TechnicalReevaluationStarted when
                actor.IsSystem(QuickStartSystemActor.AutomatedEvaluation) &&
                projectSnapshot is not null &&
                settingsSnapshotDigest is not null &&
                evaluationId is null &&
                evaluationEvaluatedAt is null &&
                evaluationAggregateStatus is null &&
                evaluationCanonicalDigest is null &&
                artifactId is null &&
                artifactDigest is null &&
                evaluationResultState is null &&
                reasonCode is null && exportFacts is null:
                return;
            case QuickStartWorkflowEventKind.EvaluationCompleted when
                actor.IsSystem(QuickStartSystemActor.AutomatedEvaluation) &&
                projectSnapshot is not null &&
                settingsSnapshotDigest is not null &&
                evaluationId is not null &&
                evaluationEvaluatedAt is not null &&
                evaluationAggregateStatus is not null &&
                evaluationCanonicalDigest is not null &&
                IsValidEvaluationResultBinding(
                    evaluationAggregateStatus.Value,
                    evaluationResultState,
                    artifactId is not null) &&
                reasonCode is null && exportFacts is null:
                return;
            case QuickStartWorkflowEventKind.Exported when
                actor.Kind == QuickStartActorKind.User &&
                projectSnapshot is not null &&
                settingsSnapshotDigest is not null &&
                evaluationId is not null &&
                evaluationEvaluatedAt is null &&
                evaluationAggregateStatus is not null &&
                evaluationCanonicalDigest is not null &&
                artifactId is not null &&
                artifactDigest is not null &&
                evaluationResultState is null &&
                reasonCode is null &&
                exportFacts is not null &&
                QuickStartWorkflowContract.SameBinding(
                    binding,
                    exportFacts.WorkflowBinding) &&
                QuickStartWorkflowContract.SameProjectSnapshot(
                    projectSnapshot,
                    exportFacts.ProjectSnapshot) &&
                settingsSnapshotDigest == exportFacts.SettingsSnapshotDigest &&
                evaluationId == exportFacts.EvaluationId &&
                evaluationAggregateStatus ==
                    exportFacts.EvaluationAggregateStatus &&
                evaluationCanonicalDigest == exportFacts.EvaluationCanonicalDigest &&
                artifactId == exportFacts.ArtifactId &&
                artifactDigest == exportFacts.ArtifactDigest:
                return;
            default:
                throw new ArgumentException(
                    "The quick-start event payload does not match its event kind.",
                    nameof(kind));
        }
    }

    private static bool IsValidEvaluationResultBinding(
        AutomatedEvaluationStatus aggregateStatus,
        QuickStartWorkflowState? resultState,
        bool hasArtifact) => resultState switch
        {
            QuickStartWorkflowState.Ready =>
                hasArtifact && aggregateStatus == AutomatedEvaluationStatus.Pass,
            QuickStartWorkflowState.ReadyWithWarnings =>
                hasArtifact && aggregateStatus is (
                    AutomatedEvaluationStatus.Warning or
                    AutomatedEvaluationStatus.NotEvaluated),
            QuickStartWorkflowState.Failed =>
                aggregateStatus is not AutomatedEvaluationStatus.Pass,
            _ => false,
        };
}

public sealed class QuickStartWorkflow
{
    public const int MaximumCanonicalStateJsonUtf8Bytes = 16 * 1024;

    private QuickStartWorkflow(
        QuickStartWorkflowBinding binding,
        QuickStartWorkflowState state,
        int revision,
        QuickStartProjectSnapshot? projectSnapshot,
        ContentDigest? settingsSnapshotDigest,
        EntityId<AutomatedEvaluationIdTag>? evaluationId,
        AutomatedEvaluationStatus? evaluationAggregateStatus,
        ContentDigest? evaluationCanonicalDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest,
        int selfReviewCount,
        int changeRequestCount,
        IReadOnlyList<QuickStartWorkflowEvent> events)
    {
        Binding = binding;
        State = state;
        Revision = revision;
        ProjectSnapshot = projectSnapshot;
        SettingsSnapshotDigest = settingsSnapshotDigest;
        EvaluationId = evaluationId;
        EvaluationAggregateStatus = evaluationAggregateStatus;
        EvaluationCanonicalDigest = evaluationCanonicalDigest;
        ArtifactId = artifactId;
        ArtifactDigest = artifactDigest;
        SelfReviewCount = selfReviewCount;
        ChangeRequestCount = changeRequestCount;
        Events = Array.AsReadOnly(events.ToArray());
        EventChainDigest = QuickStartWorkflowJsonCodec.ComputeEventChainDigest(Events);
        CanonicalJson = QuickStartWorkflowJsonCodec.SerializeSnapshot(this);
        byte[] canonicalUtf8 = QuickStartWorkflowContract.StrictUtf8.GetBytes(
            CanonicalJson);
        if (canonicalUtf8.Length > MaximumCanonicalStateJsonUtf8Bytes)
        {
            throw new InvalidOperationException(
                "The quick-start workflow state exceeds the supported byte limit.");
        }

        CanonicalDigest = ContentDigest.Compute(canonicalUtf8);
    }

    public QuickStartWorkflowBinding Binding { get; }

    public QuickStartWorkflowState State { get; }

    public int Revision { get; }

    public QuickStartProjectSnapshot? ProjectSnapshot { get; }

    public ContentDigest? SettingsSnapshotDigest { get; }

    public EntityId<AutomatedEvaluationIdTag>? EvaluationId { get; }

    public AutomatedEvaluationStatus? EvaluationAggregateStatus { get; }

    public ContentDigest? EvaluationCanonicalDigest { get; }

    public EntityId<AutomatedEvaluationArtifactIdTag>? ArtifactId { get; }

    public ContentDigest? ArtifactDigest { get; }

    public int SelfReviewCount { get; }

    public int ChangeRequestCount { get; }

    public IReadOnlyList<QuickStartWorkflowEvent> Events { get; }

    public ContentDigest EventChainDigest { get; }

    public string CanonicalJson { get; }

    public ContentDigest CanonicalDigest { get; }

    public QuickStartWorkflow Append(QuickStartWorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        return Reduce([.. Events, workflowEvent]);
    }

    /// <summary>
    /// Returns the exact CanonicalDigest of the latest EvaluationStarted or
    /// TechnicalReevaluationStarted event, only while AutomatedEvaluation is active.
    /// </summary>
    /// <remarks>
    /// After appending the start event, producers must use this value unchanged as
    /// AutomatedEvaluationRequest.InputDigest and start the run at or after the
    /// event's OccurredAt. The digest binds the unique attempt, composition version,
    /// settings, project revision and canonical payload, and start time. A new
    /// attempt requires a new input digest even when the project snapshot is unchanged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The workflow is not in AutomatedEvaluation state.
    /// </exception>
    public ContentDigest GetEvaluationInputDigest() =>
        GetActiveEvaluationStart().CanonicalDigest;

    internal QuickStartWorkflowEvent GetActiveEvaluationStart()
    {
        if (State != QuickStartWorkflowState.AutomatedEvaluation)
        {
            throw new InvalidOperationException(
                "An evaluation input digest is available only during automated evaluation.");
        }

        return Events.Last(static workflowEvent => workflowEvent.Kind is
            QuickStartWorkflowEventKind.EvaluationStarted or
            QuickStartWorkflowEventKind.TechnicalReevaluationStarted);
    }

    public static QuickStartWorkflow Reduce(
        IReadOnlyList<QuickStartWorkflowEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            throw new ArgumentException(
                "A quick-start workflow requires at least one event.",
                nameof(events));
        }

        QuickStartWorkflowBinding? binding = null;
        QuickStartWorkflowState? state = null;
        QuickStartProjectSnapshot? projectSnapshot = null;
        ContentDigest? settingsSnapshotDigest = null;
        EntityId<AutomatedEvaluationIdTag>? evaluationId = null;
        AutomatedEvaluationStatus? evaluationAggregateStatus = null;
        ContentDigest? evaluationCanonicalDigest = null;
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null;
        ContentDigest? artifactDigest = null;
        DateTimeOffset? previousTime = null;
        DateTimeOffset? evaluationStartedAt = null;
        int selfReviewCount = 0;
        int changeRequestCount = 0;
        HashSet<EntityId<QuickStartWorkflowEventIdTag>> eventIds = [];
        HashSet<EntityId<AutomatedEvaluationIdTag>> completedEvaluationIds = [];

        for (int index = 0; index < events.Count; index++)
        {
            QuickStartWorkflowEvent workflowEvent = events[index]
                ?? throw new ArgumentException(
                    "Quick-start workflow events must not contain null elements.",
                    nameof(events));
            if (!eventIds.Add(workflowEvent.EventId))
            {
                throw new ArgumentException(
                    "Quick-start workflow event IDs must be unique.",
                    nameof(events));
            }

            binding ??= workflowEvent.Binding;
            if (!QuickStartWorkflowContract.SameBinding(
                    binding,
                    workflowEvent.Binding))
            {
                throw new InvalidOperationException(
                    "Every quick-start event must bind to the same composition version.");
            }

            if (previousTime is not null && workflowEvent.OccurredAt < previousTime)
            {
                throw new InvalidOperationException(
                    "Quick-start workflow event time must not move backwards.");
            }

            previousTime = workflowEvent.OccurredAt;
            switch (workflowEvent.Kind)
            {
                case QuickStartWorkflowEventKind.StartedEditing:
                    if (index != 0 || state is not null)
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    state = QuickStartWorkflowState.Editing;
                    break;
                case QuickStartWorkflowEventKind.EvaluationStarted:
                    if (state != QuickStartWorkflowState.Editing ||
                        settingsSnapshotDigest is not null)
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    settingsSnapshotDigest = workflowEvent.SettingsSnapshotDigest;
                    projectSnapshot = workflowEvent.ProjectSnapshot;
                    evaluationId = null;
                    evaluationAggregateStatus = null;
                    evaluationCanonicalDigest = null;
                    artifactId = null;
                    artifactDigest = null;
                    evaluationStartedAt = workflowEvent.OccurredAt;
                    state = QuickStartWorkflowState.AutomatedEvaluation;
                    break;
                case QuickStartWorkflowEventKind.TechnicalReevaluationStarted:
                    if (state is not (
                            QuickStartWorkflowState.AutomatedEvaluation or
                            QuickStartWorkflowState.Ready or
                            QuickStartWorkflowState.ReadyWithWarnings or
                            QuickStartWorkflowState.Failed) ||
                        settingsSnapshotDigest !=
                                workflowEvent.SettingsSnapshotDigest ||
                            workflowEvent.ProjectSnapshot is null)
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    projectSnapshot = workflowEvent.ProjectSnapshot;
                    evaluationId = null;
                    evaluationAggregateStatus = null;
                    evaluationCanonicalDigest = null;
                    artifactId = null;
                    artifactDigest = null;
                    evaluationStartedAt = workflowEvent.OccurredAt;
                    state = QuickStartWorkflowState.AutomatedEvaluation;
                    break;
                case QuickStartWorkflowEventKind.EvaluationCompleted:
                    if (state != QuickStartWorkflowState.AutomatedEvaluation ||
                        settingsSnapshotDigest !=
                            workflowEvent.SettingsSnapshotDigest ||
                        !QuickStartWorkflowContract.SameProjectSnapshot(
                            projectSnapshot,
                            workflowEvent.ProjectSnapshot) ||
                        workflowEvent.EvaluationId is not
                            EntityId<AutomatedEvaluationIdTag> completedEvaluationId ||
                        workflowEvent.EvaluationEvaluatedAt is not
                            DateTimeOffset evaluatedAt ||
                        evaluationStartedAt is not
                            DateTimeOffset currentEvaluationStartedAt ||
                        evaluatedAt < currentEvaluationStartedAt ||
                        completedEvaluationIds.Contains(completedEvaluationId) ||
                        workflowEvent.EvaluationAggregateStatus is null ||
                        workflowEvent.EvaluationCanonicalDigest is null ||
                        workflowEvent.EvaluationResultState is null)
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    _ = completedEvaluationIds.Add(completedEvaluationId);
                    evaluationId = completedEvaluationId;
                    evaluationAggregateStatus =
                        workflowEvent.EvaluationAggregateStatus;
                    evaluationCanonicalDigest =
                        workflowEvent.EvaluationCanonicalDigest;
                    artifactId = workflowEvent.ArtifactId;
                    artifactDigest = workflowEvent.ArtifactDigest;
                    state = workflowEvent.EvaluationResultState.Value;
                    break;
                case QuickStartWorkflowEventKind.SelfReviewed:
                    if (state is not (
                        QuickStartWorkflowState.Ready or
                        QuickStartWorkflowState.ReadyWithWarnings or
                        QuickStartWorkflowState.Failed))
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    selfReviewCount++;
                    break;
                case QuickStartWorkflowEventKind.ChangesRequested:
                    if (state is not (
                        QuickStartWorkflowState.Editing or
                        QuickStartWorkflowState.Ready or
                        QuickStartWorkflowState.ReadyWithWarnings or
                        QuickStartWorkflowState.Failed))
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    changeRequestCount++;
                    break;
                case QuickStartWorkflowEventKind.Exported:
                    if (state is not (
                            QuickStartWorkflowState.Ready or
                            QuickStartWorkflowState.ReadyWithWarnings) ||
                        workflowEvent.ExportFacts is not QuickStartExportFacts facts ||
                        facts.AuthorizedRevision != index ||
                        facts.AuthorizedState != state ||
                        settingsSnapshotDigest != facts.SettingsSnapshotDigest ||
                        evaluationId != facts.EvaluationId ||
                        evaluationAggregateStatus !=
                            facts.EvaluationAggregateStatus ||
                        evaluationCanonicalDigest !=
                            facts.EvaluationCanonicalDigest ||
                        artifactId != facts.ArtifactId ||
                        artifactDigest != facts.ArtifactDigest ||
                        !QuickStartWorkflowContract.SameProjectSnapshot(
                            projectSnapshot,
                            facts.ProjectSnapshot) ||
                        facts.PublicationScope != PublicationScope.InternalOnly ||
                        !facts.RightsAllowedForInternalUse ||
                        facts.ExternalDestinationRequested ||
                        facts.DestinationKind !=
                            QuickStartExportDestinationKind.LocalInternalDelivery)
                    {
                        throw InvalidTransition(workflowEvent.Kind, state);
                    }

                    state = QuickStartWorkflowState.Exported;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(events),
                        workflowEvent.Kind,
                        "Unknown quick-start event kind.");
            }
        }

        return new QuickStartWorkflow(
            binding!,
            state ?? throw new InvalidOperationException(
                "The quick-start workflow has no state."),
            events.Count,
            projectSnapshot,
            settingsSnapshotDigest,
            evaluationId,
            evaluationAggregateStatus,
            evaluationCanonicalDigest,
            artifactId,
            artifactDigest,
            selfReviewCount,
            changeRequestCount,
            events);
    }

    private static InvalidOperationException InvalidTransition(
        QuickStartWorkflowEventKind eventKind,
        QuickStartWorkflowState? state) =>
        new(FormattableString.Invariant(
            $"Quick-start event '{eventKind}' is not valid from state '{state?.ToString() ?? "none"}'."));
}

internal static class QuickStartWorkflowContract
{
    internal const string SchemaVersion = "1.0.0";
    internal const int MaximumReasonCodeLength = 128;

    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Unknown quick-start workflow value.");
        }
    }

    internal static void ValidateUtcTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A quick-start workflow timestamp must use an explicit UTC offset.",
                parameterName);
        }
    }

    internal static string RequireBoundedText(
        string value,
        int maximumLength,
        string parameterName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 ||
            value.Length > maximumLength ||
            string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\0'))
        {
            throw new ArgumentException(
                $"{description} must be bounded nonblank text without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{description} must contain valid Unicode.",
                parameterName,
                exception);
        }

        return value;
    }

    internal static void ValidateReasonCode(string value, string parameterName)
    {
        _ = RequireBoundedText(
            value,
            MaximumReasonCodeLength,
            parameterName,
            "A quick-start reason code");
        string[] segments = value.Split('.', StringSplitOptions.None);
        if (segments.Length < 2 || segments.Any(static segment =>
                segment.Length == 0 ||
                segment[0] is not (>= 'a' and <= 'z') ||
                segment.Any(static character =>
                    character is not (>= 'a' and <= 'z') &&
                    character is not (>= '0' and <= '9') &&
                    character != '_') ||
                segment.EndsWith('_') ||
                segment.Contains("__", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A quick-start reason code must use canonical lowercase dot-separated segments.",
                parameterName);
        }
    }

    internal static bool SameBinding(
        QuickStartWorkflowBinding left,
        QuickStartWorkflowBinding right) =>
        left.ProjectId == right.ProjectId &&
        left.CompositionVersionId == right.CompositionVersionId &&
        left.CompositionVersionDigest == right.CompositionVersionDigest;

    internal static bool SameProjectSnapshot(
        QuickStartProjectSnapshot? left,
        QuickStartProjectSnapshot? right) =>
        ReferenceEquals(left, right) ||
        (left is not null &&
            right is not null &&
            left.Revision == right.Revision &&
            left.CanonicalPayloadDigest == right.CanonicalPayloadDigest);
}