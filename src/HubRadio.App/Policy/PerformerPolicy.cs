using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Policy;

public enum PerformerLabelKind
{
    Name,
    Role,
    Hidden,
}

[Flags]
public enum PerformerPolicyWarningCode
{
    None = 0,
    NameUnconfirmed = 1 << 0,
    TitleOrAffiliationUnconfirmed = 1 << 1,
    TitleOrAffiliationChangedAfterRecording = 1 << 2,
    StaleProjectSnapshot = 1 << 3,
    StaleAssignmentSnapshot = 1 << 4,
}

public enum PerformerPolicyDisposition
{
    Confirmed,
    WarningRequired,
    Hidden,
}

public enum PerformerPolicyFailureReason
{
    ContextMismatch,
    AssignmentBindingMissing,
    DuplicateConfirmationBinding,
    ProjectAssignmentMismatch,
    HiddenLabelCannotBeConfirmed,
    RecordingContextMismatch,
    InvalidEvidence,
}

public sealed class PerformerConfirmationIdTag
{
}

/// <summary>
/// Captures the exact Project aggregate digest and the selected Performer values.
/// </summary>
public sealed class PerformerProjectSnapshot
{
    internal PerformerProjectSnapshot(
        EntityId<ProjectIdTag> projectId,
        ContentDigest projectDigest,
        EntityId<PerformerIdTag> performerId,
        string displayName,
        string role,
        DateTimeOffset? titleConfirmedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(projectDigest);
        ArgumentNullException.ThrowIfNull(performerId);
        PerformerPolicyContract.ValidateSnapshotText(
            displayName,
            nameof(displayName));
        PerformerPolicyContract.ValidateSnapshotText(role, nameof(role));

        ProjectId = projectId;
        ProjectDigest = projectDigest;
        PerformerId = performerId;
        DisplayName = displayName;
        Role = role;
        TitleConfirmedAt = titleConfirmedAt;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest ProjectDigest { get; }

    public EntityId<PerformerIdTag> PerformerId { get; }

    public string DisplayName { get; }

    public string Role { get; }

    public DateTimeOffset? TitleConfirmedAt { get; }
}

/// <summary>
/// Captures the exact A-011 assignment revision digest and one label binding.
/// </summary>
public sealed class PerformerAssignmentSnapshot
{
    internal PerformerAssignmentSnapshot(
        EntityId<SpeakerAssignmentRevisionIdTag> assignmentRevisionId,
        ContentDigest assignmentDigest,
        EntityId<ProjectIdTag> projectId,
        EntityId<TranscriptRevisionIdTag> transcriptRevisionId,
        ContentDigest transcriptRevisionDigest,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        EntityId<PerformerIdTag> performerId,
        string fullName,
        string surname,
        string role,
        DateTimeOffset? titleConfirmedAt,
        SpeakerDisplayFormat displayFormat)
    {
        ArgumentNullException.ThrowIfNull(assignmentRevisionId);
        ArgumentNullException.ThrowIfNull(assignmentDigest);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(transcriptRevisionId);
        ArgumentNullException.ThrowIfNull(transcriptRevisionDigest);
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(performerId);
        PerformerPolicyContract.ValidateSnapshotText(fullName, nameof(fullName));
        PerformerPolicyContract.ValidateSnapshotText(surname, nameof(surname));
        PerformerPolicyContract.ValidateSnapshotText(role, nameof(role));
        PerformerPolicyContract.ValidateDisplayFormat(displayFormat);

        AssignmentRevisionId = assignmentRevisionId;
        AssignmentDigest = assignmentDigest;
        ProjectId = projectId;
        TranscriptRevisionId = transcriptRevisionId;
        TranscriptRevisionDigest = transcriptRevisionDigest;
        SpeakerId = speakerId;
        PerformerId = performerId;
        FullName = fullName;
        Surname = surname;
        Role = role;
        TitleConfirmedAt = titleConfirmedAt;
        DisplayFormat = displayFormat;
    }

    public EntityId<SpeakerAssignmentRevisionIdTag> AssignmentRevisionId { get; }

    public ContentDigest AssignmentDigest { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

    public ContentDigest TranscriptRevisionDigest { get; }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public EntityId<PerformerIdTag> PerformerId { get; }

    public string FullName { get; }

    public string Surname { get; }

    public string Role { get; }

    public DateTimeOffset? TitleConfirmedAt { get; }

    public SpeakerDisplayFormat DisplayFormat { get; }
}

/// <summary>
/// Immutable human confirmation of one visible performer label and its exact inputs.
/// </summary>
public sealed class PerformerConfirmation
{
    internal PerformerConfirmation(
        EntityId<PerformerConfirmationIdTag> id,
        PerformerProjectSnapshot projectSnapshot,
        PerformerAssignmentSnapshot assignmentSnapshot,
        DateTimeOffset recordedAt,
        string actorSid,
        AuthorizationRole actingRole,
        DateTimeOffset confirmedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        ArgumentNullException.ThrowIfNull(assignmentSnapshot);
        if (projectSnapshot.ProjectId != assignmentSnapshot.ProjectId
            || projectSnapshot.PerformerId != assignmentSnapshot.PerformerId
            || !string.Equals(
                projectSnapshot.DisplayName,
                assignmentSnapshot.FullName,
                StringComparison.Ordinal)
            || !string.Equals(
                projectSnapshot.Role,
                assignmentSnapshot.Role,
                StringComparison.Ordinal)
            || projectSnapshot.TitleConfirmedAt
                != assignmentSnapshot.TitleConfirmedAt)
        {
            throw new ArgumentException(
                "Performer confirmation snapshots are inconsistent.",
                nameof(assignmentSnapshot));
        }

        PerformerLabelKind labelKind = PerformerPolicy.GetLabelKind(
            assignmentSnapshot.DisplayFormat);
        if (labelKind == PerformerLabelKind.Hidden)
        {
            throw new ArgumentException(
                "A hidden performer label cannot be confirmed.",
                nameof(assignmentSnapshot));
        }

        if (confirmedAt < recordedAt)
        {
            throw new ArgumentException(
                "A performer confirmation cannot predate its recording.",
                nameof(confirmedAt));
        }

        if (labelKind == PerformerLabelKind.Role
            && (assignmentSnapshot.TitleConfirmedAt is null
                || confirmedAt < assignmentSnapshot.TitleConfirmedAt.Value))
        {
            throw new ArgumentException(
                "A role confirmation must follow its title confirmation.",
                nameof(confirmedAt));
        }

        if (actingRole != AuthorizationRole.PerformerReviewer)
        {
            throw new ArgumentException(
                "A performer confirmation must use the performer-reviewer role.",
                nameof(actingRole));
        }

        Id = id;
        ProjectSnapshot = projectSnapshot;
        AssignmentSnapshot = assignmentSnapshot;
        RecordedAt = recordedAt;
        ActorSid = PerformerPolicyContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        ActingRole = actingRole;
        ConfirmedAt = confirmedAt;
        Reason = PerformerPolicyContract.ValidateReason(reason, nameof(reason));
    }

    public EntityId<PerformerConfirmationIdTag> Id { get; }

    public PerformerProjectSnapshot ProjectSnapshot { get; }

    public PerformerAssignmentSnapshot AssignmentSnapshot { get; }

    public DateTimeOffset RecordedAt { get; }

    public string ActorSid { get; }

    public AuthorizationRole ActingRole { get; }

    public DateTimeOffset ConfirmedAt { get; }

    public string Reason { get; }
}

public sealed class PerformerPolicyDecision
{
    internal PerformerPolicyDecision(
        PerformerProjectSnapshot? projectSnapshot,
        PerformerAssignmentSnapshot assignmentSnapshot,
        PerformerConfirmation? confirmation,
        PerformerLabelKind labelKind,
        PerformerPolicyWarningCode warningCodes,
        PerformerPolicyDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(assignmentSnapshot);
        PerformerPolicyContract.ValidateLabelKind(labelKind);
        PerformerPolicyContract.ValidateWarningCodes(warningCodes);
        if (!Enum.IsDefined(disposition)
            || projectSnapshot is not null
                && (projectSnapshot.ProjectId != assignmentSnapshot.ProjectId
                    || projectSnapshot.PerformerId
                        != assignmentSnapshot.PerformerId)
            || confirmation is not null
                && confirmation.AssignmentSnapshot.SpeakerId
                    != assignmentSnapshot.SpeakerId)
        {
            throw new InvalidOperationException(
                "Performer policy decision arguments are inconsistent.");
        }

        PerformerPolicyDisposition expectedDisposition = warningCodes
            != PerformerPolicyWarningCode.None
                ? PerformerPolicyDisposition.WarningRequired
                : labelKind == PerformerLabelKind.Hidden
                    ? PerformerPolicyDisposition.Hidden
                    : PerformerPolicyDisposition.Confirmed;
        if (disposition != expectedDisposition)
        {
            throw new InvalidOperationException(
                "Performer policy decision disposition is inconsistent.");
        }

        ProjectSnapshot = projectSnapshot;
        AssignmentSnapshot = assignmentSnapshot;
        Confirmation = confirmation;
        LabelKind = labelKind;
        WarningCodes = warningCodes;
        Disposition = disposition;
    }

    public PerformerProjectSnapshot? ProjectSnapshot { get; }

    public PerformerAssignmentSnapshot AssignmentSnapshot { get; }

    public PerformerConfirmation? Confirmation { get; }

    public PerformerLabelKind LabelKind { get; }

    public PerformerPolicyWarningCode WarningCodes { get; }

    public PerformerPolicyDisposition Disposition { get; }

    public bool RequiresWarning =>
        WarningCodes != PerformerPolicyWarningCode.None;
}

public sealed class PerformerPolicyResult
{
    internal PerformerPolicyResult(
        EntityId<ProjectIdTag> projectId,
        ContentDigest projectSnapshotDigest,
        EntityId<SpeakerAssignmentRevisionIdTag> assignmentRevisionId,
        ContentDigest assignmentSnapshotDigest,
        DateTimeOffset recordedAt,
        IReadOnlyList<PerformerPolicyDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(projectSnapshotDigest);
        ArgumentNullException.ThrowIfNull(assignmentRevisionId);
        ArgumentNullException.ThrowIfNull(assignmentSnapshotDigest);
        ArgumentNullException.ThrowIfNull(decisions);

        var copiedDecisions = new PerformerPolicyDecision[decisions.Count];
        var speakerIds = new HashSet<EntityId<TranscriptSpeakerIdTag>>();
        string? previousSpeakerId = null;
        PerformerPolicyWarningCode aggregateWarnings =
            PerformerPolicyWarningCode.None;
        for (int index = 0; index < copiedDecisions.Length; index++)
        {
            PerformerPolicyDecision decision = decisions[index]
                ?? throw new ArgumentException(
                    "Performer policy decisions must not contain null elements.",
                    nameof(decisions));
            PerformerAssignmentSnapshot assignment = decision.AssignmentSnapshot;
            string currentSpeakerId = assignment.SpeakerId.ToString();
            if (assignment.ProjectId != projectId
                || assignment.AssignmentRevisionId != assignmentRevisionId
                || assignment.AssignmentDigest != assignmentSnapshotDigest
                || decision.ProjectSnapshot is not null
                    && decision.ProjectSnapshot.ProjectDigest
                        != projectSnapshotDigest
                || !speakerIds.Add(assignment.SpeakerId)
                || previousSpeakerId is not null
                    && string.CompareOrdinal(
                        previousSpeakerId,
                        currentSpeakerId) >= 0)
            {
                throw new ArgumentException(
                    "Performer policy decisions must match their result snapshot in canonical order.",
                    nameof(decisions));
            }

            copiedDecisions[index] = decision;
            previousSpeakerId = currentSpeakerId;
            aggregateWarnings |= decision.WarningCodes;
        }

        ProjectId = projectId;
        ProjectSnapshotDigest = projectSnapshotDigest;
        AssignmentRevisionId = assignmentRevisionId;
        AssignmentSnapshotDigest = assignmentSnapshotDigest;
        RecordedAt = recordedAt;
        Decisions = Array.AsReadOnly(copiedDecisions);
        WarningCodes = aggregateWarnings;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest ProjectSnapshotDigest { get; }

    public EntityId<SpeakerAssignmentRevisionIdTag> AssignmentRevisionId { get; }

    public ContentDigest AssignmentSnapshotDigest { get; }

    public DateTimeOffset RecordedAt { get; }

    public IReadOnlyList<PerformerPolicyDecision> Decisions { get; }

    public PerformerPolicyWarningCode WarningCodes { get; }

    public bool RequiresWarning =>
        WarningCodes != PerformerPolicyWarningCode.None;
}

public sealed class PerformerPolicyException : Exception
{
    internal PerformerPolicyException(PerformerPolicyFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public PerformerPolicyFailureReason Reason { get; }

    private static string GetStableMessage(
        PerformerPolicyFailureReason reason) =>
        reason switch
        {
            PerformerPolicyFailureReason.ContextMismatch =>
                "The performer-policy context is inconsistent.",
            PerformerPolicyFailureReason.AssignmentBindingMissing =>
                "A performer confirmation does not bind a current assignment.",
            PerformerPolicyFailureReason.DuplicateConfirmationBinding =>
                "Performer confirmations contain a duplicate binding.",
            PerformerPolicyFailureReason.ProjectAssignmentMismatch =>
                "The project and performer-assignment snapshots are inconsistent.",
            PerformerPolicyFailureReason.HiddenLabelCannotBeConfirmed =>
                "A hidden performer label does not require confirmation.",
            PerformerPolicyFailureReason.RecordingContextMismatch =>
                "The performer confirmation belongs to a different recording context.",
            PerformerPolicyFailureReason.InvalidEvidence =>
                "Performer confirmation evidence could not be processed safely.",
            _ => "Performer policy evaluation failed.",
        };
}

/// <summary>
/// Captures and evaluates exact performer-label confirmation evidence without mutation.
/// </summary>
public static class PerformerPolicy
{
    private const string ProjectDigestDomain =
        "HubRadio.MovieCreator.PerformerPolicy.ProjectSnapshot/v1";
    private const string AssignmentDigestDomain =
        "HubRadio.MovieCreator.PerformerPolicy.AssignmentSnapshot/v1";

    public static PerformerConfirmation CaptureConfirmation(
        AuthorizationService authorizationService,
        Project project,
        SpeakerAssignmentHistory assignmentHistory,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        EntityId<PerformerConfirmationIdTag> confirmationId,
        DateTimeOffset recordedAt,
        string actorSid,
        AuthorizationRole actingRole,
        DateTimeOffset confirmedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assignmentHistory);
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(confirmationId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            authorizationService.DemandAs(
                actorSid,
                actingRole,
                AuthorizationOperation.RecordPerformerReview);
            ValidateContext(project, assignmentHistory);
            SpeakerAssignment assignment = assignmentHistory.CurrentRevision.Assignments
                .SingleOrDefault(candidate => candidate.SpeakerId == speakerId)
                ?? throw Failure(
                    PerformerPolicyFailureReason.AssignmentBindingMissing);
            PerformerLabelKind labelKind = GetLabelKind(
                assignment.DisplayFormat);
            if (labelKind == PerformerLabelKind.Hidden)
            {
                throw Failure(
                    PerformerPolicyFailureReason.HiddenLabelCannotBeConfirmed);
            }

            Performer performer = project.Performers.SingleOrDefault(
                candidate => candidate.Id == assignment.PerformerId)
                ?? throw Failure(
                    PerformerPolicyFailureReason.ProjectAssignmentMismatch);
            if (!AssignmentMatchesPerformer(assignment, performer))
            {
                throw Failure(
                    PerformerPolicyFailureReason.ProjectAssignmentMismatch);
            }

            ContentDigest projectDigest = ComputeProjectDigest(
                project,
                cancellationToken);
            ContentDigest assignmentDigest = ComputeAssignmentDigest(
                assignmentHistory,
                cancellationToken);
            PerformerProjectSnapshot projectSnapshot = CreateProjectSnapshot(
                project,
                performer,
                projectDigest);
            PerformerAssignmentSnapshot assignmentSnapshot =
                CreateAssignmentSnapshot(
                    assignmentHistory,
                    assignment,
                    assignmentDigest);
            return new PerformerConfirmation(
                confirmationId,
                projectSnapshot,
                assignmentSnapshot,
                recordedAt,
                actorSid,
                actingRole,
                confirmedAt,
                reason);
        }
        catch (PerformerPolicyException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw Failure(PerformerPolicyFailureReason.InvalidEvidence);
        }
    }

    public static PerformerPolicyResult Evaluate(
        Project project,
        SpeakerAssignmentHistory assignmentHistory,
        DateTimeOffset recordedAt,
        IReadOnlyList<PerformerConfirmation> confirmations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assignmentHistory);
        ArgumentNullException.ThrowIfNull(confirmations);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ValidateContext(project, assignmentHistory);
            ContentDigest projectDigest = ComputeProjectDigest(
                project,
                cancellationToken);
            ContentDigest assignmentDigest = ComputeAssignmentDigest(
                assignmentHistory,
                cancellationToken);
            Dictionary<EntityId<TranscriptSpeakerIdTag>, SpeakerAssignment>
                assignmentsBySpeaker = assignmentHistory.CurrentRevision.Assignments
                    .ToDictionary(static assignment => assignment.SpeakerId);
            Dictionary<EntityId<TranscriptSpeakerIdTag>, PerformerConfirmation>
                confirmationsBySpeaker = ValidateConfirmations(
                    confirmations,
                    assignmentsBySpeaker,
                    recordedAt,
                    cancellationToken);

            var decisions = new List<PerformerPolicyDecision>(
                assignmentHistory.CurrentRevision.Assignments.Count);
            foreach (SpeakerAssignment assignment in
                assignmentHistory.CurrentRevision.Assignments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Performer? performer = project.Performers.SingleOrDefault(
                    candidate => candidate.Id == assignment.PerformerId);
                PerformerProjectSnapshot? projectSnapshot = performer is null
                    ? null
                    : CreateProjectSnapshot(project, performer, projectDigest);
                PerformerAssignmentSnapshot assignmentSnapshot =
                    CreateAssignmentSnapshot(
                        assignmentHistory,
                        assignment,
                        assignmentDigest);
                confirmationsBySpeaker.TryGetValue(
                    assignment.SpeakerId,
                    out PerformerConfirmation? confirmation);
                decisions.Add(CreateDecision(
                    projectSnapshot,
                    assignmentSnapshot,
                    confirmation,
                    recordedAt));
            }

            return new PerformerPolicyResult(
                project.Id,
                projectDigest,
                assignmentHistory.CurrentRevision.Id,
                assignmentDigest,
                recordedAt,
                decisions);
        }
        catch (PerformerPolicyException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw Failure(PerformerPolicyFailureReason.InvalidEvidence);
        }
    }

    internal static PerformerLabelKind GetLabelKind(
        SpeakerDisplayFormat displayFormat) =>
        displayFormat switch
        {
            SpeakerDisplayFormat.FullName or SpeakerDisplayFormat.Surname =>
                PerformerLabelKind.Name,
            SpeakerDisplayFormat.Role => PerformerLabelKind.Role,
            SpeakerDisplayFormat.Hidden => PerformerLabelKind.Hidden,
            _ => throw new ArgumentOutOfRangeException(
                nameof(displayFormat),
                displayFormat,
                "The performer display format must be defined."),
        };

    private static PerformerPolicyDecision CreateDecision(
        PerformerProjectSnapshot? projectSnapshot,
        PerformerAssignmentSnapshot assignmentSnapshot,
        PerformerConfirmation? confirmation,
        DateTimeOffset recordedAt)
    {
        PerformerLabelKind labelKind = GetLabelKind(
            assignmentSnapshot.DisplayFormat);
        if (labelKind == PerformerLabelKind.Hidden)
        {
            return new PerformerPolicyDecision(
                projectSnapshot,
                assignmentSnapshot,
                confirmation: null,
                labelKind,
                PerformerPolicyWarningCode.None,
                PerformerPolicyDisposition.Hidden);
        }

        PerformerPolicyWarningCode warnings =
            PerformerPolicyWarningCode.None;

        bool projectMatchesAssignment = projectSnapshot is not null
            && ProjectMatchesAssignment(projectSnapshot, assignmentSnapshot);
        if (!projectMatchesAssignment)
        {
            warnings |= PerformerPolicyWarningCode.StaleProjectSnapshot;
        }

        if (confirmation is not null)
        {
            if (!ConfirmationMatchesProject(
                    confirmation,
                    projectSnapshot))
            {
                warnings |= PerformerPolicyWarningCode.StaleProjectSnapshot;
            }

            if (!ConfirmationMatchesAssignment(
                    confirmation,
                    assignmentSnapshot))
            {
                warnings |= PerformerPolicyWarningCode.StaleAssignmentSnapshot;
            }
        }

        bool confirmationIsCurrent = confirmation is not null
            && projectMatchesAssignment
            && (warnings & (
                PerformerPolicyWarningCode.StaleProjectSnapshot
                | PerformerPolicyWarningCode.StaleAssignmentSnapshot))
                == PerformerPolicyWarningCode.None;
        if (labelKind == PerformerLabelKind.Name && !confirmationIsCurrent)
        {
            warnings |= PerformerPolicyWarningCode.NameUnconfirmed;
        }
        else if (labelKind == PerformerLabelKind.Role)
        {
            if (!confirmationIsCurrent
                || assignmentSnapshot.TitleConfirmedAt is null)
            {
                warnings |=
                    PerformerPolicyWarningCode.TitleOrAffiliationUnconfirmed;
            }

            if (assignmentSnapshot.TitleConfirmedAt is DateTimeOffset titleTime
                && titleTime > recordedAt)
            {
                warnings |= PerformerPolicyWarningCode
                    .TitleOrAffiliationChangedAfterRecording;
            }
        }

        PerformerPolicyDisposition disposition = warnings
            != PerformerPolicyWarningCode.None
                ? PerformerPolicyDisposition.WarningRequired
                : PerformerPolicyDisposition.Confirmed;
        return new PerformerPolicyDecision(
            projectSnapshot,
            assignmentSnapshot,
            confirmation,
            labelKind,
            warnings,
            disposition);
    }

    private static Dictionary<
        EntityId<TranscriptSpeakerIdTag>,
        PerformerConfirmation> ValidateConfirmations(
        IReadOnlyList<PerformerConfirmation> confirmations,
        IReadOnlyDictionary<
            EntityId<TranscriptSpeakerIdTag>,
            SpeakerAssignment> assignmentsBySpeaker,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        var confirmationsBySpeaker = new Dictionary<
            EntityId<TranscriptSpeakerIdTag>,
            PerformerConfirmation>();
        var confirmationIds =
            new HashSet<EntityId<PerformerConfirmationIdTag>>();
        for (int index = 0; index < confirmations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PerformerConfirmation confirmation = confirmations[index]
                ?? throw Failure(
                    PerformerPolicyFailureReason.InvalidEvidence);
            EntityId<TranscriptSpeakerIdTag> speakerId =
                confirmation.AssignmentSnapshot.SpeakerId;
            if (!confirmationIds.Add(confirmation.Id)
                || !confirmationsBySpeaker.TryAdd(speakerId, confirmation))
            {
                throw Failure(
                    PerformerPolicyFailureReason.DuplicateConfirmationBinding);
            }

            if (!assignmentsBySpeaker.ContainsKey(speakerId))
            {
                throw Failure(
                    PerformerPolicyFailureReason.AssignmentBindingMissing);
            }

            if (!confirmation.RecordedAt.EqualsExact(recordedAt))
            {
                throw Failure(
                    PerformerPolicyFailureReason.RecordingContextMismatch);
            }
        }

        return confirmationsBySpeaker;
    }

    private static void ValidateContext(
        Project project,
        SpeakerAssignmentHistory assignmentHistory)
    {
        if (assignmentHistory.ProjectId != project.Id)
        {
            throw Failure(PerformerPolicyFailureReason.ContextMismatch);
        }
    }

    private static PerformerProjectSnapshot CreateProjectSnapshot(
        Project project,
        Performer performer,
        ContentDigest projectDigest) =>
        new(
            project.Id,
            projectDigest,
            performer.Id,
            performer.DisplayName,
            performer.Role,
            performer.TitleConfirmedAt);

    private static PerformerAssignmentSnapshot CreateAssignmentSnapshot(
        SpeakerAssignmentHistory assignmentHistory,
        SpeakerAssignment assignment,
        ContentDigest assignmentDigest) =>
        new(
            assignmentHistory.CurrentRevision.Id,
            assignmentDigest,
            assignmentHistory.ProjectId,
            assignmentHistory.TranscriptRevisionId,
            assignmentHistory.TranscriptRevisionDigest,
            assignment.SpeakerId,
            assignment.PerformerId,
            assignment.FullName,
            assignment.Surname,
            assignment.Role,
            assignment.TitleConfirmedAt,
            assignment.DisplayFormat);

    private static bool AssignmentMatchesPerformer(
        SpeakerAssignment assignment,
        Performer performer) =>
        assignment.PerformerId == performer.Id
        && string.Equals(
            assignment.FullName,
            performer.DisplayName,
            StringComparison.Ordinal)
        && string.Equals(
            assignment.Role,
            performer.Role,
            StringComparison.Ordinal)
        && assignment.TitleConfirmedAt == performer.TitleConfirmedAt;

    private static bool ProjectMatchesAssignment(
        PerformerProjectSnapshot project,
        PerformerAssignmentSnapshot assignment) =>
        project.ProjectId == assignment.ProjectId
        && project.PerformerId == assignment.PerformerId
        && string.Equals(
            project.DisplayName,
            assignment.FullName,
            StringComparison.Ordinal)
        && string.Equals(project.Role, assignment.Role, StringComparison.Ordinal)
        && project.TitleConfirmedAt == assignment.TitleConfirmedAt;

    private static bool ConfirmationMatchesProject(
        PerformerConfirmation confirmation,
        PerformerProjectSnapshot? current) =>
        current is not null
        && confirmation.ProjectSnapshot.ProjectId == current.ProjectId
        && confirmation.ProjectSnapshot.ProjectDigest == current.ProjectDigest
        && confirmation.ProjectSnapshot.PerformerId == current.PerformerId
        && string.Equals(
            confirmation.ProjectSnapshot.DisplayName,
            current.DisplayName,
            StringComparison.Ordinal)
        && string.Equals(
            confirmation.ProjectSnapshot.Role,
            current.Role,
            StringComparison.Ordinal)
        && confirmation.ProjectSnapshot.TitleConfirmedAt
            == current.TitleConfirmedAt;

    private static bool ConfirmationMatchesAssignment(
        PerformerConfirmation confirmation,
        PerformerAssignmentSnapshot current) =>
        confirmation.AssignmentSnapshot.AssignmentRevisionId
            == current.AssignmentRevisionId
        && confirmation.AssignmentSnapshot.AssignmentDigest
            == current.AssignmentDigest
        && confirmation.AssignmentSnapshot.ProjectId == current.ProjectId
        && confirmation.AssignmentSnapshot.TranscriptRevisionId
            == current.TranscriptRevisionId
        && confirmation.AssignmentSnapshot.TranscriptRevisionDigest
            == current.TranscriptRevisionDigest
        && confirmation.AssignmentSnapshot.SpeakerId == current.SpeakerId
        && confirmation.AssignmentSnapshot.PerformerId == current.PerformerId
        && string.Equals(
            confirmation.AssignmentSnapshot.FullName,
            current.FullName,
            StringComparison.Ordinal)
        && string.Equals(
            confirmation.AssignmentSnapshot.Surname,
            current.Surname,
            StringComparison.Ordinal)
        && string.Equals(
            confirmation.AssignmentSnapshot.Role,
            current.Role,
            StringComparison.Ordinal)
        && confirmation.AssignmentSnapshot.TitleConfirmedAt
            == current.TitleConfirmedAt
        && confirmation.AssignmentSnapshot.DisplayFormat
            == current.DisplayFormat;

    private static ContentDigest ComputeProjectDigest(
        Project project,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, 1, ProjectDigestDomain);
        AppendId(hash, 2, project.Id);
        AppendText(hash, 3, project.ContentName);
        AppendText(hash, 4, project.Purpose);
        AppendText(hash, 5, project.Audience);
        AppendInt32(hash, 6, checked((int)project.PublicationScope));
        AppendNullableDateTime(hash, 7, project.Deadline);

        Asset[] assets = project.Assets
            .OrderBy(static asset => asset.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, 8, assets.Length);
        foreach (Asset asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendId(hash, 20, asset.Id);
            AppendText(hash, 21, asset.FileName);
            AppendText(hash, 22, asset.MediaType);
            AppendDigest(hash, 23, asset.ContentDigest);
            AppendInt64(hash, 24, asset.ByteSize);
            AppendInt32(hash, 25, checked((int)asset.Rights.Status));
            AppendNullableText(hash, 26, asset.Rights.EvidenceReference);
            AppendText(hash, 27, asset.Rights.AllowedUse);
            AppendBoolean(hash, 28, asset.Rights.ExternalPublicationAllowed);
        }

        Performer[] performers = project.Performers
            .OrderBy(
                static performer => performer.Id.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, 9, performers.Length);
        foreach (Performer performer in performers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendId(hash, 40, performer.Id);
            AppendText(hash, 41, performer.DisplayName);
            AppendText(hash, 42, performer.Role);
            AppendNullableDateTime(hash, 43, performer.TitleConfirmedAt);
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static ContentDigest ComputeAssignmentDigest(
        SpeakerAssignmentHistory assignmentHistory,
        CancellationToken cancellationToken)
    {
        SpeakerAssignmentRevision revision =
            assignmentHistory.CurrentRevision;
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, 1, AssignmentDigestDomain);
        AppendId(hash, 2, revision.Id);
        AppendNullableId(hash, 3, revision.ParentRevisionId);
        AppendId(hash, 4, revision.ProjectId);
        AppendId(hash, 5, revision.TranscriptRevisionId);
        AppendDigest(hash, 6, revision.TranscriptRevisionDigest);
        AppendDateTime(hash, 7, revision.CreatedAt);
        AppendText(hash, 8, revision.ActorSid);
        AppendText(hash, 9, revision.Reason);
        AppendInt32(hash, 10, revision.Assignments.Count);
        foreach (SpeakerAssignment assignment in revision.Assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendId(hash, 20, assignment.SpeakerId);
            AppendText(hash, 21, assignment.AnonymousLabel);
            AppendId(hash, 22, assignment.PerformerId);
            AppendText(hash, 23, assignment.FullName);
            AppendText(hash, 24, assignment.Surname);
            AppendText(hash, 25, assignment.Role);
            AppendNullableDateTime(hash, 26, assignment.TitleConfirmedAt);
            AppendInt32(hash, 27, checked((int)assignment.DisplayFormat));
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static void AppendId<TTag>(
        IncrementalHash hash,
        byte tag,
        EntityId<TTag> id) =>
        AppendText(hash, tag, id.ToString());

    private static void AppendNullableId<TTag>(
        IncrementalHash hash,
        byte tag,
        EntityId<TTag>? id)
    {
        AppendBoolean(hash, tag, id is not null);
        if (id is not null)
        {
            AppendId(hash, tag, id);
        }
    }

    private static void AppendDigest(
        IncrementalHash hash,
        byte tag,
        ContentDigest digest) =>
        AppendText(hash, tag, digest.Hex);

    private static void AppendNullableText(
        IncrementalHash hash,
        byte tag,
        string? value)
    {
        AppendBoolean(hash, tag, value is not null);
        if (value is not null)
        {
            AppendText(hash, tag, value);
        }
    }

    private static void AppendText(
        IncrementalHash hash,
        byte tag,
        string value) =>
        AppendBytes(
            hash,
            tag,
            PerformerPolicyContract.StrictUtf8.GetBytes(value));

    private static void AppendBoolean(
        IncrementalHash hash,
        byte tag,
        bool value) =>
        AppendBytes(hash, tag, [value ? (byte)1 : (byte)0]);

    private static void AppendInt32(
        IncrementalHash hash,
        byte tag,
        int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        AppendBytes(hash, tag, bytes);
    }

    private static void AppendInt64(
        IncrementalHash hash,
        byte tag,
        long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        AppendBytes(hash, tag, bytes);
    }

    private static void AppendNullableDateTime(
        IncrementalHash hash,
        byte tag,
        DateTimeOffset? value)
    {
        AppendBoolean(hash, tag, value.HasValue);
        if (value.HasValue)
        {
            AppendDateTime(hash, tag, value.Value);
        }
    }

    private static void AppendDateTime(
        IncrementalHash hash,
        byte tag,
        DateTimeOffset value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64BigEndian(
            bytes,
            value.UtcDateTime.Ticks);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes[sizeof(long)..],
            value.Offset.Ticks);
        AppendBytes(hash, tag, bytes);
    }

    private static void AppendBytes(
        IncrementalHash hash,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], value.Length);
        hash.AppendData(header);
        hash.AppendData(value);
    }

    private static PerformerPolicyException Failure(
        PerformerPolicyFailureReason reason) =>
        new(reason);
}

internal static class PerformerPolicyContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateSnapshotText(
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Performer snapshot text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Performer snapshot text must be valid Unicode.",
                parameterName,
                exception);
        }
    }

    internal static string ValidateCanonicalSid(
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A performer confirmation actor SID must be canonical.",
                parameterName);
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A performer confirmation actor SID must be canonical.",
                parameterName,
                exception);
        }

        if (!string.Equals(sid.Value, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A performer confirmation actor SID must be canonical.",
                parameterName);
        }

        return sid.Value;
    }

    internal static string ValidateReason(
        string value,
        string parameterName)
    {
        ValidateSnapshotText(value, parameterName);
        if (!ContainsSubstantiveUnicodeScalar(value))
        {
            throw new ArgumentException(
                "A performer confirmation reason must contain substantive text.",
                parameterName);
        }

        return value;
    }

    internal static void ValidateDisplayFormat(
        SpeakerDisplayFormat displayFormat)
    {
        if (!Enum.IsDefined(displayFormat))
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayFormat),
                displayFormat,
                "The performer display format must be defined.");
        }
    }

    internal static void ValidateLabelKind(PerformerLabelKind labelKind)
    {
        if (!Enum.IsDefined(labelKind))
        {
            throw new ArgumentOutOfRangeException(nameof(labelKind));
        }
    }

    internal static void ValidateWarningCodes(
        PerformerPolicyWarningCode warningCodes)
    {
        const PerformerPolicyWarningCode allCodes =
            PerformerPolicyWarningCode.NameUnconfirmed
            | PerformerPolicyWarningCode.TitleOrAffiliationUnconfirmed
            | PerformerPolicyWarningCode.TitleOrAffiliationChangedAfterRecording
            | PerformerPolicyWarningCode.StaleProjectSnapshot
            | PerformerPolicyWarningCode.StaleAssignmentSnapshot;
        if ((warningCodes & ~allCodes) != PerformerPolicyWarningCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(warningCodes));
        }
    }

    private static bool ContainsSubstantiveUnicodeScalar(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (!Rune.IsWhiteSpace(rune)
                && !IsNonSubstantiveSpecialScalar(rune)
                && category is not (UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonSubstantiveSpecialScalar(Rune rune) =>
        rune.Value switch
        {
            0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0 => true,
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xE0100 and <= 0xE01EF => true,
            _ => false,
        };
}