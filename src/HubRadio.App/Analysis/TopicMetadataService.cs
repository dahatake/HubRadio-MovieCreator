using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Analysis;

public enum TopicMetadataValueOrigin
{
    Unset,
    DeterministicPlaceholder,
    ProjectSnapshot,
    DeterministicTranscriptOverlap,
    ManualEditorial,
}

public enum TopicMetadataField
{
    Initial,
    Title,
    Summary,
    MainSpeaker,
    Keywords,
    ImportantStatements,
    Audience,
    PublicationCautions,
}

public sealed class TopicMainSpeakerSnapshot
{
    internal TopicMainSpeakerSnapshot(
        EntityId<TranscriptSpeakerIdTag> speakerId,
        EntityId<PerformerIdTag>? performerId,
        bool displayVisible,
        string? displayText,
        bool titleOrAffiliationConfirmationRequired,
        RationalTime overlapDuration)
    {
        ArgumentNullException.ThrowIfNull(speakerId);
        if (overlapDuration <= default(RationalTime)
            || displayVisible != (displayText is not null))
        {
            throw new ArgumentException(
                "A topic main-speaker snapshot is inconsistent.",
                nameof(overlapDuration));
        }

        if (displayText is not null)
        {
            TopicMetadataContract.ValidateText(displayText, nameof(displayText));
        }

        SpeakerId = speakerId;
        PerformerId = performerId;
        DisplayVisible = displayVisible;
        DisplayText = displayText;
        TitleOrAffiliationConfirmationRequired =
            titleOrAffiliationConfirmationRequired;
        OverlapDuration = overlapDuration;
    }

    public EntityId<TranscriptSpeakerIdTag> SpeakerId { get; }

    public EntityId<PerformerIdTag>? PerformerId { get; }

    public bool DisplayVisible { get; }

    public string? DisplayText { get; }

    public bool TitleOrAffiliationConfirmationRequired { get; }

    public RationalTime OverlapDuration { get; }
}

public sealed class TopicMetadata
{
    internal TopicMetadata(
        string title,
        TopicMetadataValueOrigin titleOrigin,
        string? summary,
        TopicMetadataValueOrigin summaryOrigin,
        TopicMainSpeakerSnapshot? mainSpeaker,
        TopicMetadataValueOrigin mainSpeakerOrigin,
        bool mainSpeakerTieUnresolved,
        IReadOnlyList<string> keywords,
        TopicMetadataValueOrigin keywordsOrigin,
        IReadOnlyList<EntityId<SourceSpanIdTag>> importantStatementSourceSpanIds,
        TopicMetadataValueOrigin importantStatementsOrigin,
        string audience,
        TopicMetadataValueOrigin audienceOrigin,
        IReadOnlyList<string> publicationCautions,
        TopicMetadataValueOrigin publicationCautionsOrigin)
    {
        TopicMetadataContract.ValidateText(title, nameof(title));
        TopicMetadataContract.ValidateOptionalText(summary, nameof(summary));
        TopicMetadataContract.ValidateText(audience, nameof(audience));
        TopicMetadataContract.ValidateOrigin(titleOrigin, nameof(titleOrigin));
        TopicMetadataContract.ValidateOrigin(summaryOrigin, nameof(summaryOrigin));
        TopicMetadataContract.ValidateOrigin(mainSpeakerOrigin, nameof(mainSpeakerOrigin));
        TopicMetadataContract.ValidateOrigin(keywordsOrigin, nameof(keywordsOrigin));
        TopicMetadataContract.ValidateOrigin(
            importantStatementsOrigin,
            nameof(importantStatementsOrigin));
        TopicMetadataContract.ValidateOrigin(audienceOrigin, nameof(audienceOrigin));
        TopicMetadataContract.ValidateOrigin(
            publicationCautionsOrigin,
            nameof(publicationCautionsOrigin));
        string[] copiedKeywords = TopicMetadataContract.CopyUniqueText(
            keywords,
            nameof(keywords));
        EntityId<SourceSpanIdTag>[] copiedImportantStatements =
            TopicMetadataContract.CopyUniqueIds(
                importantStatementSourceSpanIds,
                nameof(importantStatementSourceSpanIds));
        string[] copiedCautions = TopicMetadataContract.CopyUniqueText(
            publicationCautions,
            nameof(publicationCautions));

        if (titleOrigin is not (
                TopicMetadataValueOrigin.DeterministicPlaceholder
                or TopicMetadataValueOrigin.ManualEditorial)
            || (summary is null) !=
                (summaryOrigin == TopicMetadataValueOrigin.Unset)
            || (mainSpeaker is null) !=
                (mainSpeakerOrigin == TopicMetadataValueOrigin.Unset)
            || mainSpeakerTieUnresolved && mainSpeaker is not null
            || (copiedKeywords.Length == 0) !=
                (keywordsOrigin == TopicMetadataValueOrigin.Unset)
            || (copiedImportantStatements.Length == 0) !=
                (importantStatementsOrigin == TopicMetadataValueOrigin.Unset)
            || audienceOrigin is not (
                TopicMetadataValueOrigin.ProjectSnapshot
                or TopicMetadataValueOrigin.ManualEditorial)
            || (copiedCautions.Length == 0) !=
                (publicationCautionsOrigin == TopicMetadataValueOrigin.Unset))
        {
            throw new ArgumentException(
                "Topic metadata values and origins are inconsistent.",
                nameof(titleOrigin));
        }

        Title = title;
        TitleOrigin = titleOrigin;
        Summary = summary;
        SummaryOrigin = summaryOrigin;
        MainSpeaker = mainSpeaker;
        MainSpeakerOrigin = mainSpeakerOrigin;
        MainSpeakerTieUnresolved = mainSpeakerTieUnresolved;
        Keywords = Array.AsReadOnly(copiedKeywords);
        KeywordsOrigin = keywordsOrigin;
        ImportantStatementSourceSpanIds =
            Array.AsReadOnly(copiedImportantStatements);
        ImportantStatementsOrigin = importantStatementsOrigin;
        Audience = audience;
        AudienceOrigin = audienceOrigin;
        PublicationCautions = Array.AsReadOnly(copiedCautions);
        PublicationCautionsOrigin = publicationCautionsOrigin;
    }

    public string Title { get; }

    public TopicMetadataValueOrigin TitleOrigin { get; }

    public bool TitleRequiresManualReview =>
        TitleOrigin == TopicMetadataValueOrigin.DeterministicPlaceholder;

    public string? Summary { get; }

    public TopicMetadataValueOrigin SummaryOrigin { get; }

    public TopicMainSpeakerSnapshot? MainSpeaker { get; }

    public TopicMetadataValueOrigin MainSpeakerOrigin { get; }

    public bool MainSpeakerTieUnresolved { get; }

    public IReadOnlyList<string> Keywords { get; }

    public TopicMetadataValueOrigin KeywordsOrigin { get; }

    public IReadOnlyList<EntityId<SourceSpanIdTag>>
        ImportantStatementSourceSpanIds
    { get; }

    public TopicMetadataValueOrigin ImportantStatementsOrigin { get; }

    public string Audience { get; }

    public TopicMetadataValueOrigin AudienceOrigin { get; }

    public IReadOnlyList<string> PublicationCautions { get; }

    public TopicMetadataValueOrigin PublicationCautionsOrigin { get; }
}

public sealed class TopicMetadataIdTag
{
}

public sealed class TopicMetadataRevisionIdTag
{
}

public sealed class TopicMetadataRevision
{
    internal TopicMetadataRevision(
        EntityId<TopicMetadataIdTag> topicMetadataId,
        EntityId<TopicMetadataRevisionIdTag> id,
        EntityId<TopicMetadataRevisionIdTag>? parentRevisionId,
        EntityId<ProjectIdTag> projectId,
        EntityId<TopicRangeIdTag> topicRangeId,
        EntityId<TopicRangeRevisionIdTag> topicRangeRevisionId,
        TimeRange topicRange,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        ContentDigest sourceRevisionDigest,
        TopicMetadata metadata,
        TopicMetadataField changedField,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(topicMetadataId);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(topicRangeId);
        ArgumentNullException.ThrowIfNull(topicRangeRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(sourceRevisionDigest);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!Enum.IsDefined(changedField)
            || topicRange.Duration <= default(RationalTime)
            || id == parentRevisionId
            || (parentRevisionId is null) !=
                (changedField == TopicMetadataField.Initial))
        {
            throw new ArgumentException(
                "A topic-metadata revision has inconsistent identity or change evidence.",
                nameof(changedField));
        }

        TopicMetadataId = topicMetadataId;
        Id = id;
        ParentRevisionId = parentRevisionId;
        ProjectId = projectId;
        TopicRangeId = topicRangeId;
        TopicRangeRevisionId = topicRangeRevisionId;
        TopicRange = topicRange;
        SourceRevisionId = sourceRevisionId;
        SourceRevisionDigest = sourceRevisionDigest;
        Metadata = metadata;
        ChangedField = changedField;
        ActorSid = TranscriptEditorContract.ValidateCanonicalSid(
            actorSid,
            nameof(actorSid));
        TranscriptEditorContract.ValidateText(reason, nameof(reason));
        CreatedAt = createdAt;
        Reason = reason;
    }

    public EntityId<TopicMetadataIdTag> TopicMetadataId { get; }

    public EntityId<TopicMetadataRevisionIdTag> Id { get; }

    public EntityId<TopicMetadataRevisionIdTag>? ParentRevisionId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<TopicRangeIdTag> TopicRangeId { get; }

    public EntityId<TopicRangeRevisionIdTag> TopicRangeRevisionId { get; }

    public TimeRange TopicRange { get; }

    public EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    public ContentDigest SourceRevisionDigest { get; }

    public TopicMetadata Metadata { get; }

    public TopicMetadataField ChangedField { get; }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Reason { get; }
}

public enum TopicMetadataFailureReason
{
    ContextMismatch,
    SourceSpanBindingMismatch,
    TopicRangeBindingMismatch,
    MainSpeakerNotInTopic,
    ImportantStatementOutsideTopic,
    RevisionConflict,
    NoChange,
    InvalidText,
    ArithmeticOverflow,
}

public sealed class TopicMetadataException : Exception
{
    internal TopicMetadataException(TopicMetadataFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public TopicMetadataFailureReason Reason { get; }

    private static string GetStableMessage(TopicMetadataFailureReason reason) =>
        reason switch
        {
            TopicMetadataFailureReason.ContextMismatch =>
                "The topic-metadata context is inconsistent.",
            TopicMetadataFailureReason.SourceSpanBindingMismatch =>
                "The topic-metadata source-span evidence is inconsistent.",
            TopicMetadataFailureReason.TopicRangeBindingMismatch =>
                "The topic-metadata range does not match its transcript.",
            TopicMetadataFailureReason.MainSpeakerNotInTopic =>
                "The requested main speaker does not occur in the topic range.",
            TopicMetadataFailureReason.ImportantStatementOutsideTopic =>
                "An important statement is not fully contained by the topic range.",
            TopicMetadataFailureReason.RevisionConflict =>
                "The requested topic-metadata revision conflicts with its parent.",
            TopicMetadataFailureReason.NoChange =>
                "The requested topic-metadata edit would not change its parent.",
            TopicMetadataFailureReason.InvalidText =>
                "Topic-metadata text could not be processed safely.",
            TopicMetadataFailureReason.ArithmeticOverflow =>
                "Topic-metadata time arithmetic exceeds the supported range.",
            _ => "Topic-metadata editing failed.",
        };
}

public static class TopicMetadataService
{
    public static TopicMetadataRevision CreateInitial(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        int topicOrdinal,
        EntityId<TopicMetadataIdTag> topicMetadataId,
        EntityId<TopicMetadataRevisionIdTag> revisionId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(topicRange);
        ArgumentNullException.ThrowIfNull(topicMetadataId);
        ArgumentNullException.ThrowIfNull(revisionId);
        if (topicOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topicOrdinal));
        }

        ValidatedContext context = ValidateContext(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments);
        TopicMetadata metadata;
        try
        {
            TopicMainSpeakerSnapshot? mainSpeaker = ResolveDeterministicMainSpeaker(
                context,
                out bool tied);
            metadata = new TopicMetadata(
                string.Concat(
                    "Topic ",
                    topicOrdinal.ToString(CultureInfo.InvariantCulture)),
                TopicMetadataValueOrigin.DeterministicPlaceholder,
                summary: null,
                TopicMetadataValueOrigin.Unset,
                mainSpeaker,
                mainSpeaker is null
                    ? TopicMetadataValueOrigin.Unset
                    : TopicMetadataValueOrigin.DeterministicTranscriptOverlap,
                tied,
                keywords: [],
                TopicMetadataValueOrigin.Unset,
                importantStatementSourceSpanIds: [],
                TopicMetadataValueOrigin.Unset,
                project.Audience,
                TopicMetadataValueOrigin.ProjectSnapshot,
                publicationCautions: [],
                TopicMetadataValueOrigin.Unset);
            return NewRevision(
                context,
                topicMetadataId,
                revisionId,
                parentRevisionId: null,
                metadata,
                TopicMetadataField.Initial,
                actorSid,
                createdAt,
                reason);
        }
        catch (ArgumentException)
        {
            throw Failure(TopicMetadataFailureReason.InvalidText);
        }
        catch (OverflowException)
        {
            throw Failure(TopicMetadataFailureReason.ArithmeticOverflow);
        }
    }

    public static TopicMetadataRevision SetTitle(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        string title,
        string actorSid,
        DateTimeOffset createdAt,
        string reason) =>
        Revise(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments,
            current,
            newRevisionId,
            TopicMetadataField.Title,
            metadata => Copy(
                metadata,
                title: ValidateManualText(title),
                titleOrigin: TopicMetadataValueOrigin.ManualEditorial),
            actorSid,
            createdAt,
            reason);

    public static TopicMetadataRevision SetSummary(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        string? summary,
        string actorSid,
        DateTimeOffset createdAt,
        string reason) =>
        Revise(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments,
            current,
            newRevisionId,
            TopicMetadataField.Summary,
            metadata => Copy(
                metadata,
                summary: ValidateManualOptionalText(summary),
                summaryOrigin: summary is null
                    ? TopicMetadataValueOrigin.Unset
                    : TopicMetadataValueOrigin.ManualEditorial),
            actorSid,
            createdAt,
            reason);

    public static TopicMetadataRevision SetMainSpeaker(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        EntityId<TranscriptSpeakerIdTag>? speakerId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidatedContext context = ValidateContext(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments);
        TopicMainSpeakerSnapshot? speaker;
        try
        {
            speaker = speakerId is null
                ? null
                : ResolveSpeaker(context, speakerId, requireTopicOverlap: true);
        }
        catch (OverflowException)
        {
            throw Failure(TopicMetadataFailureReason.ArithmeticOverflow);
        }
        return ReviseValidated(
            context,
            current,
            newRevisionId,
            TopicMetadataField.MainSpeaker,
            Copy(
                current.Metadata,
                mainSpeaker: speaker,
                mainSpeakerOrigin: speaker is null
                    ? TopicMetadataValueOrigin.Unset
                    : TopicMetadataValueOrigin.ManualEditorial,
                mainSpeakerTieUnresolved: false),
            actorSid,
            createdAt,
            reason);
    }

    public static TopicMetadataRevision SetKeywords(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        IReadOnlyList<string> keywords,
        string actorSid,
        DateTimeOffset createdAt,
        string reason) =>
        Revise(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments,
            current,
            newRevisionId,
            TopicMetadataField.Keywords,
            metadata =>
            {
                string[] values = ValidateManualTextList(keywords);
                return Copy(
                    metadata,
                    keywords: values,
                    keywordsOrigin: values.Length == 0
                        ? TopicMetadataValueOrigin.Unset
                        : TopicMetadataValueOrigin.ManualEditorial);
            },
            actorSid,
            createdAt,
            reason);

    public static TopicMetadataRevision SetImportantStatements(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        IReadOnlyList<EntityId<SourceSpanIdTag>> importantStatementSourceSpanIds,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidatedContext context = ValidateContext(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments);
        EntityId<SourceSpanIdTag>[] values =
            TopicMetadataContract.CopyUniqueIds(
                importantStatementSourceSpanIds,
                nameof(importantStatementSourceSpanIds));
        foreach (EntityId<SourceSpanIdTag> sourceSpanId in values)
        {
            SourceSpan? sourceSpan = context.SourceSpans.SingleOrDefault(
                candidate => candidate.Id == sourceSpanId);
            if (sourceSpan is null
                || sourceSpan.Range.Start < topicRange.Range.Start
                || sourceSpan.Range.End > topicRange.Range.End)
            {
                throw Failure(
                    TopicMetadataFailureReason.ImportantStatementOutsideTopic);
            }
        }

        return ReviseValidated(
            context,
            current,
            newRevisionId,
            TopicMetadataField.ImportantStatements,
            Copy(
                current.Metadata,
                importantStatementSourceSpanIds: values,
                importantStatementsOrigin: values.Length == 0
                    ? TopicMetadataValueOrigin.Unset
                    : TopicMetadataValueOrigin.ManualEditorial),
            actorSid,
            createdAt,
            reason);
    }

    public static TopicMetadataRevision SetAudience(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        string audience,
        string actorSid,
        DateTimeOffset createdAt,
        string reason) =>
        Revise(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments,
            current,
            newRevisionId,
            TopicMetadataField.Audience,
            metadata => Copy(
                metadata,
                audience: ValidateManualText(audience),
                audienceOrigin: TopicMetadataValueOrigin.ManualEditorial),
            actorSid,
            createdAt,
            reason);

    public static TopicMetadataRevision SetPublicationCautions(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        IReadOnlyList<string> publicationCautions,
        string actorSid,
        DateTimeOffset createdAt,
        string reason) =>
        Revise(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments,
            current,
            newRevisionId,
            TopicMetadataField.PublicationCautions,
            metadata =>
            {
                string[] values = ValidateManualTextList(publicationCautions);
                return Copy(
                    metadata,
                    publicationCautions: values,
                    publicationCautionsOrigin: values.Length == 0
                        ? TopicMetadataValueOrigin.Unset
                        : TopicMetadataValueOrigin.ManualEditorial);
            },
            actorSid,
            createdAt,
            reason);

    private static TopicMetadataRevision Revise(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        TopicMetadataField changedField,
        Func<TopicMetadata, TopicMetadata> edit,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(current);
        ValidatedContext context = ValidateContext(
            project,
            transcript,
            sourceSpans,
            topicRange,
            speakerAssignments);
        return ReviseValidated(
            context,
            current,
            newRevisionId,
            changedField,
            edit(current.Metadata),
            actorSid,
            createdAt,
            reason);
    }

    private static TopicMetadataRevision ReviseValidated(
        ValidatedContext context,
        TopicMetadataRevision current,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        TopicMetadataField changedField,
        TopicMetadata metadata,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(newRevisionId);
        if (current.ProjectId != context.Project.Id
            || current.TopicRangeId != context.TopicRange.TopicRangeId
            || current.TopicRangeRevisionId != context.TopicRange.Id
            || current.TopicRange != context.TopicRange.Range
            || current.SourceRevisionId != context.Transcript.Id
            || current.SourceRevisionDigest != context.RevisionDigest)
        {
            throw Failure(TopicMetadataFailureReason.ContextMismatch);
        }

        if (current.Id == newRevisionId || createdAt < current.CreatedAt)
        {
            throw Failure(TopicMetadataFailureReason.RevisionConflict);
        }

        if (TopicMetadataContract.StableEquals(current.Metadata, metadata))
        {
            throw Failure(TopicMetadataFailureReason.NoChange);
        }

        if (!TopicMetadataContract.OnlyFieldChanged(
                current.Metadata,
                metadata,
                changedField))
        {
            throw new InvalidOperationException(
                "A topic-metadata edit changed an unexpected field.");
        }

        return NewRevision(
            context,
            current.TopicMetadataId,
            newRevisionId,
            current.Id,
            metadata,
            changedField,
            actorSid,
            createdAt,
            reason);
    }

    private static TopicMetadataRevision NewRevision(
        ValidatedContext context,
        EntityId<TopicMetadataIdTag> topicMetadataId,
        EntityId<TopicMetadataRevisionIdTag> revisionId,
        EntityId<TopicMetadataRevisionIdTag>? parentRevisionId,
        TopicMetadata metadata,
        TopicMetadataField changedField,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        try
        {
            return new TopicMetadataRevision(
                topicMetadataId,
                revisionId,
                parentRevisionId,
                context.Project.Id,
                context.TopicRange.TopicRangeId,
                context.TopicRange.Id,
                context.TopicRange.Range,
                context.Transcript.Id,
                context.RevisionDigest,
                metadata,
                changedField,
                actorSid,
                createdAt,
                reason);
        }
        catch (ArgumentException)
        {
            throw Failure(TopicMetadataFailureReason.InvalidText);
        }
    }

    private static TopicMetadata Copy(
        TopicMetadata source,
        string? title = null,
        TopicMetadataValueOrigin? titleOrigin = null,
        OptionalText summary = default,
        TopicMetadataValueOrigin? summaryOrigin = null,
        OptionalSpeaker mainSpeaker = default,
        TopicMetadataValueOrigin? mainSpeakerOrigin = null,
        bool? mainSpeakerTieUnresolved = null,
        IReadOnlyList<string>? keywords = null,
        TopicMetadataValueOrigin? keywordsOrigin = null,
        IReadOnlyList<EntityId<SourceSpanIdTag>>?
            importantStatementSourceSpanIds = null,
        TopicMetadataValueOrigin? importantStatementsOrigin = null,
        string? audience = null,
        TopicMetadataValueOrigin? audienceOrigin = null,
        IReadOnlyList<string>? publicationCautions = null,
        TopicMetadataValueOrigin? publicationCautionsOrigin = null) =>
        new(
            title ?? source.Title,
            titleOrigin ?? source.TitleOrigin,
            summary.Specified ? summary.Value : source.Summary,
            summaryOrigin ?? source.SummaryOrigin,
            mainSpeaker.Specified ? mainSpeaker.Value : source.MainSpeaker,
            mainSpeakerOrigin ?? source.MainSpeakerOrigin,
            mainSpeakerTieUnresolved ?? source.MainSpeakerTieUnresolved,
            keywords ?? source.Keywords,
            keywordsOrigin ?? source.KeywordsOrigin,
            importantStatementSourceSpanIds
                ?? source.ImportantStatementSourceSpanIds,
            importantStatementsOrigin ?? source.ImportantStatementsOrigin,
            audience ?? source.Audience,
            audienceOrigin ?? source.AudienceOrigin,
            publicationCautions ?? source.PublicationCautions,
            publicationCautionsOrigin ?? source.PublicationCautionsOrigin);

    private static TopicMainSpeakerSnapshot? ResolveDeterministicMainSpeaker(
        ValidatedContext context,
        out bool tied)
    {
        Dictionary<EntityId<TranscriptSpeakerIdTag>, RationalTime> durations = [];
        foreach (Utterance utterance in context.Transcript.Utterances)
        {
            RationalTime overlap = GetOverlapDuration(
                utterance.Range,
                context.TopicRange.Range);
            if (overlap <= default(RationalTime))
            {
                continue;
            }

            durations.TryGetValue(
                utterance.SpeakerId,
                out RationalTime current);
            durations[utterance.SpeakerId] = current + overlap;
        }

        if (durations.Count == 0)
        {
            tied = false;
            return null;
        }

        RationalTime maximum = durations.Values.Max();
        EntityId<TranscriptSpeakerIdTag>[] winners = durations
            .Where(pair => pair.Value == maximum)
            .Select(static pair => pair.Key)
            .ToArray();
        tied = winners.Length > 1;
        return tied
            ? null
            : ResolveSpeaker(
                context,
                winners[0],
                requireTopicOverlap: true,
                overlapDuration: maximum);
    }

    private static TopicMainSpeakerSnapshot ResolveSpeaker(
        ValidatedContext context,
        EntityId<TranscriptSpeakerIdTag> speakerId,
        bool requireTopicOverlap,
        RationalTime? overlapDuration = null)
    {
        RationalTime duration = overlapDuration ?? context.Transcript.Utterances
            .Where(utterance => utterance.SpeakerId == speakerId)
            .Select(utterance => GetOverlapDuration(
                utterance.Range,
                context.TopicRange.Range))
            .Aggregate(default(RationalTime), static (sum, value) => sum + value);
        if (requireTopicOverlap && duration <= default(RationalTime))
        {
            throw Failure(TopicMetadataFailureReason.MainSpeakerNotInTopic);
        }

        ResolvedSpeakerDisplay display = SpeakerAssignmentService.ResolveDisplay(
            context.Project,
            context.Transcript,
            context.SpeakerAssignments,
            speakerId);
        return new TopicMainSpeakerSnapshot(
            speakerId,
            display.PerformerId,
            display.IsVisible,
            display.Text,
            display.TitleOrAffiliationConfirmationRequired,
            duration);
    }

    private static RationalTime GetOverlapDuration(
        TimeRange left,
        TimeRange right)
    {
        RationalTime start = left.Start >= right.Start ? left.Start : right.Start;
        RationalTime end = left.End <= right.End ? left.End : right.End;
        return end > start ? end - start : default;
    }

    private static ValidatedContext ValidateContext(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TopicRangeRevision topicRange,
        SpeakerAssignmentHistory? speakerAssignments)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(topicRange);
        ContentDigest revisionDigest = ComputeRevisionDigest(transcript);
        if (topicRange.SourceRevisionId != transcript.Id
            || topicRange.SourceRevisionDigest != revisionDigest)
        {
            throw Failure(TopicMetadataFailureReason.TopicRangeBindingMismatch);
        }

        if (speakerAssignments is not null
            && (speakerAssignments.ProjectId != project.Id
                || speakerAssignments.TranscriptRevisionId != transcript.Id
                || speakerAssignments.TranscriptRevisionDigest != revisionDigest))
        {
            throw Failure(TopicMetadataFailureReason.ContextMismatch);
        }

        if (sourceSpans.Count != transcript.Utterances.Count)
        {
            throw Failure(TopicMetadataFailureReason.SourceSpanBindingMismatch);
        }

        var copiedSpans = new SourceSpan[sourceSpans.Count];
        var spanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copiedSpans.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw Failure(
                    TopicMetadataFailureReason.SourceSpanBindingMismatch);
            Utterance utterance = transcript.Utterances[index];
            if (!spanIds.Add(span.Id)
                || span.Id != utterance.SourceSpanId
                || span.TranscriptRevisionId != transcript.Id
                || span.Range != utterance.Range
                || !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw Failure(
                    TopicMetadataFailureReason.SourceSpanBindingMismatch);
            }

            copiedSpans[index] = span;
        }

        return new ValidatedContext(
            project,
            transcript,
            copiedSpans,
            topicRange,
            speakerAssignments,
            revisionDigest);
    }

    private static ContentDigest ComputeRevisionDigest(
        TranscriptRevision transcript)
    {
        try
        {
            string canonical = TranscriptSnapshotJson.WriteRevision(transcript);
            return ContentDigest.Compute(
                TopicMetadataContract.StrictUtf8.GetBytes(canonical));
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(TopicMetadataFailureReason.InvalidText);
        }
    }

    private static string ValidateManualText(string value)
    {
        try
        {
            TopicMetadataContract.ValidateText(value, nameof(value));
            return value;
        }
        catch (ArgumentException)
        {
            throw Failure(TopicMetadataFailureReason.InvalidText);
        }
    }

    private static string? ValidateManualOptionalText(string? value)
    {
        try
        {
            TopicMetadataContract.ValidateOptionalText(value, nameof(value));
            return value;
        }
        catch (ArgumentException)
        {
            throw Failure(TopicMetadataFailureReason.InvalidText);
        }
    }

    private static string[] ValidateManualTextList(
        IReadOnlyList<string> values)
    {
        try
        {
            return TopicMetadataContract.CopyUniqueText(values, nameof(values));
        }
        catch (ArgumentException)
        {
            throw Failure(TopicMetadataFailureReason.InvalidText);
        }
    }

    private static TopicMetadataException Failure(
        TopicMetadataFailureReason reason) =>
        new(reason);

    private readonly record struct OptionalText(bool Specified, string? Value)
    {
        public static implicit operator OptionalText(string? value) =>
            new(true, value);
    }

    private readonly record struct OptionalSpeaker(
        bool Specified,
        TopicMainSpeakerSnapshot? Value)
    {
        public static implicit operator OptionalSpeaker(
            TopicMainSpeakerSnapshot? value) =>
            new(true, value);
    }

    private sealed record ValidatedContext(
        Project Project,
        TranscriptRevision Transcript,
        IReadOnlyList<SourceSpan> SourceSpans,
        TopicRangeRevision TopicRange,
        SpeakerAssignmentHistory? SpeakerAssignments,
        ContentDigest RevisionDigest);
}

internal static class TopicMetadataContract
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
                "Topic metadata text must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Topic metadata text must be valid Unicode.",
                parameterName,
                exception);
        }
    }

    internal static void ValidateOptionalText(
        string? value,
        string parameterName)
    {
        if (value is not null)
        {
            ValidateText(value, parameterName);
        }
    }

    internal static void ValidateOrigin(
        TopicMetadataValueOrigin value,
        string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    internal static string[] CopyUniqueText(
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copied = new string[values.Count];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copied.Length; index++)
        {
            string value = values[index]
                ?? throw new ArgumentException(
                    "Topic metadata text lists must not contain null elements.",
                    parameterName);
            ValidateText(value, parameterName);
            if (!unique.Add(value))
            {
                throw new ArgumentException(
                    "Topic metadata text lists must not contain duplicates.",
                    parameterName);
            }

            copied[index] = value;
        }

        return copied;
    }

    internal static EntityId<SourceSpanIdTag>[] CopyUniqueIds(
        IReadOnlyList<EntityId<SourceSpanIdTag>> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copied = new EntityId<SourceSpanIdTag>[values.Count];
        var unique = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            EntityId<SourceSpanIdTag> value = values[index]
                ?? throw new ArgumentException(
                    "Important-statement IDs must not contain null elements.",
                    parameterName);
            if (!unique.Add(value))
            {
                throw new ArgumentException(
                    "Important-statement IDs must not contain duplicates.",
                    parameterName);
            }

            copied[index] = value;
        }

        return copied;
    }

    internal static bool StableEquals(TopicMetadata left, TopicMetadata right) =>
        EqualTitle(left, right)
        && EqualSummary(left, right)
        && EqualMainSpeaker(left, right)
        && EqualKeywords(left, right)
        && EqualImportantStatements(left, right)
        && EqualAudience(left, right)
        && EqualPublicationCautions(left, right);

    internal static bool OnlyFieldChanged(
        TopicMetadata left,
        TopicMetadata right,
        TopicMetadataField field) =>
        field != TopicMetadataField.Initial
        && (field == TopicMetadataField.Title || EqualTitle(left, right))
        && (field == TopicMetadataField.Summary || EqualSummary(left, right))
        && (field == TopicMetadataField.MainSpeaker || EqualMainSpeaker(left, right))
        && (field == TopicMetadataField.Keywords || EqualKeywords(left, right))
        && (field == TopicMetadataField.ImportantStatements
            || EqualImportantStatements(left, right))
        && (field == TopicMetadataField.Audience || EqualAudience(left, right))
        && (field == TopicMetadataField.PublicationCautions
            || EqualPublicationCautions(left, right));

    private static bool EqualTitle(TopicMetadata left, TopicMetadata right) =>
        string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && left.TitleOrigin == right.TitleOrigin;

    private static bool EqualSummary(TopicMetadata left, TopicMetadata right) =>
        string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
        && left.SummaryOrigin == right.SummaryOrigin;

    private static bool EqualMainSpeaker(TopicMetadata left, TopicMetadata right) =>
        EqualMainSpeakerSnapshot(left.MainSpeaker, right.MainSpeaker)
        && left.MainSpeakerOrigin == right.MainSpeakerOrigin
        && left.MainSpeakerTieUnresolved == right.MainSpeakerTieUnresolved;

    private static bool EqualMainSpeakerSnapshot(
        TopicMainSpeakerSnapshot? left,
        TopicMainSpeakerSnapshot? right) =>
        left is null
            ? right is null
            : right is not null
                && left.SpeakerId == right.SpeakerId
                && left.PerformerId == right.PerformerId
                && left.DisplayVisible == right.DisplayVisible
                && string.Equals(
                    left.DisplayText,
                    right.DisplayText,
                    StringComparison.Ordinal)
                && left.TitleOrAffiliationConfirmationRequired ==
                    right.TitleOrAffiliationConfirmationRequired
                && left.OverlapDuration == right.OverlapDuration;

    private static bool EqualKeywords(TopicMetadata left, TopicMetadata right) =>
        left.KeywordsOrigin == right.KeywordsOrigin
        && left.Keywords.SequenceEqual(right.Keywords, StringComparer.Ordinal);

    private static bool EqualImportantStatements(
        TopicMetadata left,
        TopicMetadata right) =>
        left.ImportantStatementsOrigin == right.ImportantStatementsOrigin
        && left.ImportantStatementSourceSpanIds.SequenceEqual(
            right.ImportantStatementSourceSpanIds);

    private static bool EqualAudience(TopicMetadata left, TopicMetadata right) =>
        string.Equals(left.Audience, right.Audience, StringComparison.Ordinal)
        && left.AudienceOrigin == right.AudienceOrigin;

    private static bool EqualPublicationCautions(
        TopicMetadata left,
        TopicMetadata right) =>
        left.PublicationCautionsOrigin == right.PublicationCautionsOrigin
        && left.PublicationCautions.SequenceEqual(
            right.PublicationCautions,
            StringComparer.Ordinal);
}