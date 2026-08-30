using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Speakers;

public enum SpeakerDisplayFormat
{
    FullName,
    Surname,
    Role,
    Hidden,
}

public sealed class SpeakerAssignmentRevisionIdTag
{
}

public sealed class SpeakerAssignment
{
    internal SpeakerAssignment(
        EntityId<TranscriptSpeakerIdTag> speakerId,
        string anonymousLabel,
        EntityId<PerformerIdTag> performerId,
        string fullName,
        string surname,
        string role,
        DateTimeOffset? titleConfirmedAt,
        SpeakerDisplayFormat displayFormat)
    {
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(performerId);
        SpeakerAssignmentContract.ValidateText(
            anonymousLabel,
            nameof(anonymousLabel));
        SpeakerAssignmentContract.ValidateText(fullName, nameof(fullName));
        SpeakerAssignmentContract.ValidateText(surname, nameof(surname));
        SpeakerAssignmentContract.ValidateText(role, nameof(role));
        SpeakerAssignmentContract.ValidateDisplayFormat(displayFormat);

        SpeakerId = speakerId;
        AnonymousLabel = anonymousLabel;
        PerformerId = performerId;
        FullName = fullName;
        Surname = surname;
        Role = role;
        TitleConfirmedAt = titleConfirmedAt;
        DisplayFormat = displayFormat;
    }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public string AnonymousLabel { get; }

    public EntityId<PerformerIdTag> PerformerId { get; }

    public string FullName { get; }

    public string Surname { get; }

    public string Role { get; }

    public DateTimeOffset? TitleConfirmedAt { get; }

    public SpeakerDisplayFormat DisplayFormat { get; }

    public bool TitleOrAffiliationConfirmationRequired =>
        TitleConfirmedAt is null;
}

public sealed class SpeakerAssignmentChange
{
    internal SpeakerAssignmentChange(
        SpeakerAssignment? previousAssignment,
        SpeakerAssignment? currentAssignment)
    {
        if (previousAssignment is null && currentAssignment is null)
        {
            throw new ArgumentException(
                "A speaker-assignment change must contain a previous or current value.",
                nameof(currentAssignment));
        }

        if (previousAssignment is not null
            && currentAssignment is not null
            && (previousAssignment.SpeakerId != currentAssignment.SpeakerId
                || SpeakerAssignmentContract.StableEquals(
                    previousAssignment,
                    currentAssignment)))
        {
            throw new ArgumentException(
                "A speaker-assignment replacement must change one speaker assignment.",
                nameof(currentAssignment));
        }

        PreviousAssignment = previousAssignment;
        CurrentAssignment = currentAssignment;
        SpeakerId = (currentAssignment ?? previousAssignment)!.SpeakerId;
    }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public SpeakerAssignment? PreviousAssignment { get; }

    public SpeakerAssignment? CurrentAssignment { get; }
}

public sealed class SpeakerAssignmentRevision
{
    internal SpeakerAssignmentRevision(
        EntityId<SpeakerAssignmentRevisionIdTag> id,
        EntityId<SpeakerAssignmentRevisionIdTag>? parentRevisionId,
        EntityId<ProjectIdTag> projectId,
        EntityId<TranscriptRevisionIdTag> transcriptRevisionId,
        ContentDigest transcriptRevisionDigest,
        DateTimeOffset createdAt,
        string actorSid,
        string reason,
        IReadOnlyList<SpeakerAssignment> assignments,
        SpeakerAssignmentChange change)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(transcriptRevisionId);
        ArgumentNullException.ThrowIfNull(transcriptRevisionDigest);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(change);
        if (id == parentRevisionId)
        {
            throw new ArgumentException(
                "A speaker-assignment revision cannot be its own parent.",
                nameof(parentRevisionId));
        }

        SpeakerAssignment[] copiedAssignments =
            new SpeakerAssignment[assignments.Count];
        var speakerIds = new HashSet<EntityId<TranscriptSpeakerIdTag>>();
        string? previousSpeakerId = null;
        for (int index = 0; index < copiedAssignments.Length; index++)
        {
            SpeakerAssignment assignment = assignments[index]
                ?? throw new ArgumentException(
                    "Speaker assignments must not contain null elements.",
                    nameof(assignments));
            string currentSpeakerId = assignment.SpeakerId.ToString();
            if (!speakerIds.Add(assignment.SpeakerId)
                || previousSpeakerId is not null
                    && string.CompareOrdinal(previousSpeakerId, currentSpeakerId) >= 0)
            {
                throw new ArgumentException(
                    "Speaker assignments must use unique IDs in canonical order.",
                    nameof(assignments));
            }

            previousSpeakerId = currentSpeakerId;
            copiedAssignments[index] = assignment;
        }

        SpeakerAssignment? changedAssignment = copiedAssignments.SingleOrDefault(
            assignment => assignment.SpeakerId == change.SpeakerId);
        if (change.CurrentAssignment is null
            ? changedAssignment is not null
            : changedAssignment is null
                || !SpeakerAssignmentContract.StableEquals(
                    change.CurrentAssignment,
                    changedAssignment))
        {
            throw new ArgumentException(
                "The speaker-assignment change does not match the revision snapshot.",
                nameof(change));
        }

        Id = id;
        ParentRevisionId = parentRevisionId;
        ProjectId = projectId;
        TranscriptRevisionId = transcriptRevisionId;
        TranscriptRevisionDigest = transcriptRevisionDigest;
        CreatedAt = createdAt;
        ActorSid = SpeakerAssignmentContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        SpeakerAssignmentContract.ValidateText(reason, nameof(reason));
        Reason = reason;
        Assignments = Array.AsReadOnly(copiedAssignments);
        Change = change;
    }

    public EntityId<SpeakerAssignmentRevisionIdTag> Id { get; }

    public EntityId<SpeakerAssignmentRevisionIdTag>? ParentRevisionId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

    public ContentDigest TranscriptRevisionDigest { get; }

    public DateTimeOffset CreatedAt { get; }

    public string ActorSid { get; }

    public string Reason { get; }

    public IReadOnlyList<SpeakerAssignment> Assignments { get; }

    public SpeakerAssignmentChange Change { get; }
}

public sealed class SpeakerAssignmentHistory
{
    internal SpeakerAssignmentHistory(
        IReadOnlyList<SpeakerAssignmentRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if (revisions.Count == 0)
        {
            throw new ArgumentException(
                "A speaker-assignment history must contain a revision.",
                nameof(revisions));
        }

        SpeakerAssignmentRevision[] copiedRevisions =
            new SpeakerAssignmentRevision[revisions.Count];
        var revisionIds =
            new HashSet<EntityId<SpeakerAssignmentRevisionIdTag>>();
        SpeakerAssignmentRevision? previous = null;
        for (int index = 0; index < copiedRevisions.Length; index++)
        {
            SpeakerAssignmentRevision revision = revisions[index]
                ?? throw new ArgumentException(
                    "Speaker-assignment revisions must not contain null elements.",
                    nameof(revisions));
            bool chainIsInvalid = previous is null
                ? revision.ParentRevisionId is not null
                : revision.ParentRevisionId != previous.Id
                        || revision.ProjectId != previous.ProjectId
                        || revision.TranscriptRevisionId != previous.TranscriptRevisionId
                        || revision.TranscriptRevisionDigest !=
                            previous.TranscriptRevisionDigest
                        || revision.CreatedAt < previous.CreatedAt
                        || !SpeakerAssignmentContract.ChangeApplies(
                            previous.Assignments,
                            revision.Assignments,
                            revision.Change);
            if (!revisionIds.Add(revision.Id) || chainIsInvalid)
            {
                throw new ArgumentException(
                    "The speaker-assignment revision chain is inconsistent.",
                    nameof(revisions));
            }

            if (previous is null
                && !SpeakerAssignmentContract.ChangeApplies(
                    [],
                    revision.Assignments,
                    revision.Change))
            {
                throw new ArgumentException(
                    "The first speaker-assignment revision is inconsistent.",
                    nameof(revisions));
            }

            copiedRevisions[index] = revision;
            previous = revision;
        }

        Revisions = Array.AsReadOnly(copiedRevisions);
        CurrentRevision = copiedRevisions[^1];
        ProjectId = copiedRevisions[0].ProjectId;
        TranscriptRevisionId = copiedRevisions[0].TranscriptRevisionId;
        TranscriptRevisionDigest = copiedRevisions[0].TranscriptRevisionDigest;
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

    public ContentDigest TranscriptRevisionDigest { get; }

    public SpeakerAssignmentRevision CurrentRevision { get; }

    public IReadOnlyList<SpeakerAssignmentRevision> Revisions { get; }
}

public sealed class ResolvedSpeakerDisplay
{
    internal ResolvedSpeakerDisplay(
        EntityId<TranscriptSpeakerIdTag> speakerId,
        EntityId<PerformerIdTag>? performerId,
        SpeakerDisplayFormat? format,
        bool isVisible,
        string? text,
        DateTimeOffset? titleConfirmedAt,
        bool titleOrAffiliationConfirmationRequired)
    {
        ArgumentNullException.ThrowIfNull(speakerId);
        bool valid = performerId is null
            ? format is null
                && isVisible
                && text is not null
                && titleConfirmedAt is null
                && !titleOrAffiliationConfirmationRequired
            : format is SpeakerDisplayFormat assignedFormat
                && Enum.IsDefined(assignedFormat)
                && (assignedFormat == SpeakerDisplayFormat.Hidden
                    ? !isVisible && text is null
                    : isVisible && text is not null)
                && titleOrAffiliationConfirmationRequired ==
                    (titleConfirmedAt is null);
        if (!valid)
        {
            throw new ArgumentException(
                "The resolved speaker display is inconsistent.",
                nameof(format));
        }

        if (text is not null)
        {
            SpeakerAssignmentContract.ValidateText(text, nameof(text));
        }

        SpeakerId = speakerId;
        PerformerId = performerId;
        Format = format;
        IsVisible = isVisible;
        Text = text;
        TitleConfirmedAt = titleConfirmedAt;
        TitleOrAffiliationConfirmationRequired =
            titleOrAffiliationConfirmationRequired;
    }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public EntityId<PerformerIdTag>? PerformerId { get; }

    public SpeakerDisplayFormat? Format { get; }

    public bool IsAssigned => PerformerId is not null;

    public bool IsVisible { get; }

    public string? Text { get; }

    public DateTimeOffset? TitleConfirmedAt { get; }

    public bool TitleOrAffiliationConfirmationRequired { get; }
}

public enum SpeakerAssignmentFailureReason
{
    ContextMismatch,
    SpeakerNotFound,
    PerformerNotFound,
    AssignmentNotFound,
    NoChange,
    RevisionConflict,
    InvalidText,
}

public sealed class SpeakerAssignmentException : Exception
{
    internal SpeakerAssignmentException(SpeakerAssignmentFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SpeakerAssignmentFailureReason Reason { get; }

    private static string GetStableMessage(
        SpeakerAssignmentFailureReason reason) =>
        reason switch
        {
            SpeakerAssignmentFailureReason.ContextMismatch =>
                "The speaker-assignment context is inconsistent.",
            SpeakerAssignmentFailureReason.SpeakerNotFound =>
                "The requested anonymous speaker does not exist.",
            SpeakerAssignmentFailureReason.PerformerNotFound =>
                "The requested performer does not exist.",
            SpeakerAssignmentFailureReason.AssignmentNotFound =>
                "The requested speaker assignment does not exist.",
            SpeakerAssignmentFailureReason.NoChange =>
                "The requested speaker assignment would not change the current revision.",
            SpeakerAssignmentFailureReason.RevisionConflict =>
                "The requested speaker-assignment revision conflicts with its history.",
            SpeakerAssignmentFailureReason.InvalidText =>
                "Speaker-assignment text could not be processed safely.",
            _ => "The speaker-assignment operation failed.",
        };
}

public static class SpeakerAssignmentService
{
    /// <summary>
    /// Records a manual mapping without parsing or inferring a surname from a display name.
    /// The supplied surname is the actor-confirmed value retained in the new revision.
    /// </summary>
    public static SpeakerAssignmentHistory SetAssignment(
        Project project,
        TranscriptRevision transcript,
        SpeakerAssignmentHistory? history,
        EntityId<SpeakerAssignmentRevisionIdTag> newRevisionId,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        EntityId<PerformerIdTag> performerId,
        string surname,
        SpeakerDisplayFormat displayFormat,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(newRevisionId);
        ArgumentNullException.ThrowIfNull(speakerId);
        ArgumentNullException.ThrowIfNull(performerId);
        ValidateRevisionRequest(history, newRevisionId, createdAt);
        ContentDigest transcriptDigest = ComputeTranscriptDigest(transcript);
        ValidateContext(project, transcript, transcriptDigest, history);
        string anonymousLabel = GetAnonymousLabel(transcript, speakerId);
        Performer performer = GetPerformer(project, performerId);
        ValidateOperationText(surname, actorSid, reason);
        SpeakerAssignmentContract.ValidateDisplayFormat(displayFormat);

        var currentAssignments = history?.CurrentRevision.Assignments
            ?? Array.Empty<SpeakerAssignment>();
        SpeakerAssignment? previousAssignment = currentAssignments.SingleOrDefault(
            assignment => assignment.SpeakerId == speakerId);
        SpeakerAssignment currentAssignment;
        try
        {
            currentAssignment = new SpeakerAssignment(
                speakerId,
                anonymousLabel,
                performer.Id,
                performer.DisplayName,
                surname,
                performer.Role,
                performer.TitleConfirmedAt,
                displayFormat);
        }
        catch (ArgumentException)
        {
            throw Failure(SpeakerAssignmentFailureReason.InvalidText);
        }

        if (previousAssignment is not null
            && SpeakerAssignmentContract.StableEquals(
                previousAssignment,
                currentAssignment))
        {
            throw Failure(SpeakerAssignmentFailureReason.NoChange);
        }

        SpeakerAssignment[] updatedAssignments = currentAssignments
            .Where(assignment => assignment.SpeakerId != speakerId)
            .Append(currentAssignment)
            .OrderBy(
                static assignment => assignment.SpeakerId.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        return AppendRevision(
            project,
            transcript,
            transcriptDigest,
            history,
            newRevisionId,
            createdAt,
            actorSid,
            reason,
            updatedAssignments,
            new SpeakerAssignmentChange(
                previousAssignment,
                currentAssignment));
    }

    public static SpeakerAssignmentHistory RemoveAssignment(
        Project project,
        TranscriptRevision transcript,
        SpeakerAssignmentHistory history,
        EntityId<SpeakerAssignmentRevisionIdTag> newRevisionId,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(newRevisionId);
        ArgumentNullException.ThrowIfNull(speakerId);
        ValidateRevisionRequest(history, newRevisionId, createdAt);
        ContentDigest transcriptDigest = ComputeTranscriptDigest(transcript);
        ValidateContext(project, transcript, transcriptDigest, history);
        _ = GetAnonymousLabel(transcript, speakerId);
        ValidateOperationText(actorSid: actorSid, reason: reason);

        SpeakerAssignment? previousAssignment =
            history.CurrentRevision.Assignments.SingleOrDefault(
                assignment => assignment.SpeakerId == speakerId);
        if (previousAssignment is null)
        {
            throw Failure(SpeakerAssignmentFailureReason.AssignmentNotFound);
        }

        SpeakerAssignment[] updatedAssignments = history.CurrentRevision.Assignments
            .Where(assignment => assignment.SpeakerId != speakerId)
            .ToArray();
        return AppendRevision(
            project,
            transcript,
            transcriptDigest,
            history,
            newRevisionId,
            createdAt,
            actorSid,
            reason,
            updatedAssignments,
            new SpeakerAssignmentChange(previousAssignment, currentAssignment: null));
    }

    public static ResolvedSpeakerDisplay ResolveDisplay(
        Project project,
        TranscriptRevision transcript,
        SpeakerAssignmentHistory? history,
        EntityId<TranscriptSpeakerIdTag> speakerId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(speakerId);
        ContentDigest transcriptDigest = ComputeTranscriptDigest(transcript);
        ValidateContext(project, transcript, transcriptDigest, history);
        string anonymousLabel = GetAnonymousLabel(transcript, speakerId);
        SpeakerAssignment? assignment = history?.CurrentRevision.Assignments
            .SingleOrDefault(candidate => candidate.SpeakerId == speakerId);
        if (assignment is null)
        {
            return new ResolvedSpeakerDisplay(
                speakerId,
                performerId: null,
                format: null,
                isVisible: true,
                anonymousLabel,
                titleConfirmedAt: null,
                titleOrAffiliationConfirmationRequired: false);
        }

        string? text = assignment.DisplayFormat switch
        {
            SpeakerDisplayFormat.FullName => assignment.FullName,
            SpeakerDisplayFormat.Surname => assignment.Surname,
            SpeakerDisplayFormat.Role => assignment.Role,
            SpeakerDisplayFormat.Hidden => null,
            _ => throw new InvalidOperationException(
                "The stored speaker display format is invalid."),
        };
        return new ResolvedSpeakerDisplay(
            speakerId,
            assignment.PerformerId,
            assignment.DisplayFormat,
            isVisible: assignment.DisplayFormat != SpeakerDisplayFormat.Hidden,
            text,
            assignment.TitleConfirmedAt,
            assignment.TitleOrAffiliationConfirmationRequired);
    }

    private static SpeakerAssignmentHistory AppendRevision(
        Project project,
        TranscriptRevision transcript,
        ContentDigest transcriptDigest,
        SpeakerAssignmentHistory? history,
        EntityId<SpeakerAssignmentRevisionIdTag> newRevisionId,
        DateTimeOffset createdAt,
        string actorSid,
        string reason,
        IReadOnlyList<SpeakerAssignment> assignments,
        SpeakerAssignmentChange change)
    {
        SpeakerAssignmentRevision revision;
        try
        {
            revision = new SpeakerAssignmentRevision(
                newRevisionId,
                history?.CurrentRevision.Id,
                project.Id,
                transcript.Id,
                transcriptDigest,
                createdAt,
                actorSid,
                reason,
                assignments,
                change);
        }
        catch (ArgumentException)
        {
            throw Failure(SpeakerAssignmentFailureReason.InvalidText);
        }

        SpeakerAssignmentRevision[] revisions = history is null
            ? [revision]
            : [.. history.Revisions, revision];
        return new SpeakerAssignmentHistory(revisions);
    }

    private static void ValidateRevisionRequest(
        SpeakerAssignmentHistory? history,
        EntityId<SpeakerAssignmentRevisionIdTag> newRevisionId,
        DateTimeOffset createdAt)
    {
        if (history is null)
        {
            return;
        }

        if (createdAt < history.CurrentRevision.CreatedAt
            || history.Revisions.Any(revision => revision.Id == newRevisionId))
        {
            throw Failure(SpeakerAssignmentFailureReason.RevisionConflict);
        }
    }

    private static void ValidateContext(
        Project project,
        TranscriptRevision transcript,
        ContentDigest transcriptDigest,
        SpeakerAssignmentHistory? history)
    {
        if (history is not null
            && (history.ProjectId != project.Id
                || history.TranscriptRevisionId != transcript.Id
                || history.TranscriptRevisionDigest != transcriptDigest))
        {
            throw Failure(SpeakerAssignmentFailureReason.ContextMismatch);
        }
    }

    private static string GetAnonymousLabel(
        TranscriptRevision transcript,
        EntityId<TranscriptSpeakerIdTag> speakerId)
    {
        Utterance? utterance = transcript.Utterances.FirstOrDefault(
            candidate => candidate.SpeakerId == speakerId);
        if (utterance is null)
        {
            throw Failure(SpeakerAssignmentFailureReason.SpeakerNotFound);
        }

        return utterance.SpeakerLabel;
    }

    private static Performer GetPerformer(
        Project project,
        EntityId<PerformerIdTag> performerId)
    {
        Performer? performer = project.Performers.SingleOrDefault(
            candidate => candidate.Id == performerId);
        return performer
            ?? throw Failure(SpeakerAssignmentFailureReason.PerformerNotFound);
    }

    private static ContentDigest ComputeTranscriptDigest(
        TranscriptRevision transcript)
    {
        try
        {
            string canonical = TranscriptSnapshotJson.WriteRevision(transcript);
            return ContentDigest.Compute(
                SpeakerAssignmentContract.StrictUtf8.GetBytes(canonical));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(SpeakerAssignmentFailureReason.InvalidText);
        }
    }

    private static void ValidateOperationText(
        string? surname = null,
        string? actorSid = null,
        string? reason = null)
    {
        try
        {
            if (surname is not null)
            {
                SpeakerAssignmentContract.ValidateText(surname, nameof(surname));
            }

            if (actorSid is not null)
            {
                _ = SpeakerAssignmentContract.ValidateCanonicalSid(
                    actorSid,
                    nameof(actorSid));
            }

            if (reason is not null)
            {
                SpeakerAssignmentContract.ValidateText(reason, nameof(reason));
            }
        }
        catch (ArgumentException)
        {
            throw Failure(SpeakerAssignmentFailureReason.InvalidText);
        }
    }

    private static SpeakerAssignmentException Failure(
        SpeakerAssignmentFailureReason reason) =>
        new(reason);
}

internal static class SpeakerAssignmentContract
{
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Speaker-assignment text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Speaker-assignment text must be valid Unicode.",
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
                "A speaker-assignment actor SID must be canonical.",
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
                "A speaker-assignment actor SID must be canonical.",
                parameterName,
                exception);
        }

        if (!string.Equals(sid.Value, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A speaker-assignment actor SID must be canonical.",
                parameterName);
        }

        return sid.Value;
    }

    internal static void ValidateDisplayFormat(SpeakerDisplayFormat displayFormat)
    {
        if (!Enum.IsDefined(displayFormat))
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayFormat),
                displayFormat,
                "The speaker display format must be defined.");
        }
    }

    internal static bool StableEquals(
        SpeakerAssignment left,
        SpeakerAssignment right) =>
        left.SpeakerId == right.SpeakerId
        && string.Equals(
            left.AnonymousLabel,
            right.AnonymousLabel,
            StringComparison.Ordinal)
        && left.PerformerId == right.PerformerId
        && string.Equals(left.FullName, right.FullName, StringComparison.Ordinal)
        && string.Equals(left.Surname, right.Surname, StringComparison.Ordinal)
        && string.Equals(left.Role, right.Role, StringComparison.Ordinal)
        && left.TitleConfirmedAt == right.TitleConfirmedAt
        && left.DisplayFormat == right.DisplayFormat;

    internal static bool ChangeApplies(
        IReadOnlyList<SpeakerAssignment> previousAssignments,
        IReadOnlyList<SpeakerAssignment> currentAssignments,
        SpeakerAssignmentChange change)
    {
        var expected = previousAssignments.ToDictionary(
            static assignment => assignment.SpeakerId);
        if (change.PreviousAssignment is null)
        {
            if (expected.ContainsKey(change.SpeakerId))
            {
                return false;
            }
        }
        else if (!expected.TryGetValue(change.SpeakerId, out SpeakerAssignment? prior)
            || !StableEquals(prior, change.PreviousAssignment))
        {
            return false;
        }

        if (change.CurrentAssignment is null)
        {
            if (!expected.Remove(change.SpeakerId))
            {
                return false;
            }
        }
        else
        {
            expected[change.SpeakerId] = change.CurrentAssignment;
        }

        if (expected.Count != currentAssignments.Count)
        {
            return false;
        }

        foreach (SpeakerAssignment current in currentAssignments)
        {
            if (!expected.TryGetValue(current.SpeakerId, out SpeakerAssignment? expectedValue)
                || !StableEquals(expectedValue, current))
            {
                return false;
            }
        }

        return true;
    }
}