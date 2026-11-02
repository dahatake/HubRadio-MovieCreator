using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Policy;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal sealed class AnalysisBridgeRevisionIdTag
{
}

internal enum AnalysisBridgeStatus
{
    Available,
    Forbidden,
    Unavailable,
    StaleBase,
    Conflict,
    NoChange,
    Invalid,
}

internal sealed class AnalysisSpeakerProjection
{
    internal AnalysisSpeakerProjection(
        ResolvedSpeakerDisplay display,
        SourceSpan sourceSpan)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        Display = display;
        SourceSpan = sourceSpan;
    }

    internal ResolvedSpeakerDisplay Display { get; }

    internal SourceSpan SourceSpan { get; }
}

internal sealed class AnalysisTopicBinding
{
    private AnalysisTopicBinding(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        SpeakerAssignmentHistory? speakerHistory,
        TopicRangeRevision topicRange,
        TopicMetadataRevision metadata)
    {
        Project = project;
        Transcript = transcript;
        SourceSpans = sourceSpans;
        SpeakerHistory = speakerHistory;
        TopicRange = topicRange;
        Metadata = metadata;
    }

    internal Project Project { get; }

    internal TranscriptRevision Transcript { get; }

    internal IReadOnlyList<SourceSpan> SourceSpans { get; }

    internal SpeakerAssignmentHistory? SpeakerHistory { get; }

    internal TopicRangeRevision TopicRange { get; }

    internal TopicMetadataRevision Metadata { get; }

    internal static AnalysisTopicBinding Create(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        SpeakerAssignmentHistory? speakerHistory,
        TopicRangeRevision topicRange,
        TopicMetadataRevision metadata)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(topicRange);
        ArgumentNullException.ThrowIfNull(metadata);
        ContentDigest transcriptDigest = AnalysisBridgeSnapshot.ComputeTranscriptDigest(
            transcript);
        if (topicRange.SourceRevisionId != transcript.Id ||
            topicRange.SourceRevisionDigest != transcriptDigest ||
            metadata.ProjectId != project.Id ||
            metadata.TopicRangeId != topicRange.TopicRangeId ||
            metadata.TopicRangeRevisionId != topicRange.Id ||
            metadata.TopicRange != topicRange.Range ||
            metadata.SourceRevisionId != transcript.Id ||
            metadata.SourceRevisionDigest != transcriptDigest)
        {
            throw new ArgumentException(
                "The analysis topic metadata does not match its source context.",
                nameof(metadata));
        }

        return new AnalysisTopicBinding(
            project,
            transcript,
            Array.AsReadOnly(sourceSpans.ToArray()),
            speakerHistory,
            topicRange,
            metadata);
    }

    internal AnalysisTopicBinding Update(
        JsonElement update,
        EntityId<TopicMetadataRevisionIdTag> newRevisionId,
        string actorSid,
        DateTimeOffset createdAt,
        string reason)
    {
        string field = update.GetProperty("field").GetString()
            ?? throw new FormatException("The topic field is invalid.");
        TopicMetadataRevision revised = field switch
        {
            "title" => TopicMetadataService.SetTitle(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetRequiredText(update, "text"),
                actorSid,
                createdAt,
                reason),
            "summary" => TopicMetadataService.SetSummary(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetOptionalText(update, "text"),
                actorSid,
                createdAt,
                reason),
            "mainSpeaker" => TopicMetadataService.SetMainSpeaker(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetOptionalEntity<TranscriptSpeakerIdTag>(
                    update,
                    "speakerId"),
                actorSid,
                createdAt,
                reason),
            "keywords" => TopicMetadataService.SetKeywords(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetTextArray(update, "values"),
                actorSid,
                createdAt,
                reason),
            "importantStatements" => TopicMetadataService.SetImportantStatements(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetEntityArray<SourceSpanIdTag>(update, "sourceSpanIds"),
                actorSid,
                createdAt,
                reason),
            "audience" => TopicMetadataService.SetAudience(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetRequiredText(update, "text"),
                actorSid,
                createdAt,
                reason),
            "publicationCautions" => TopicMetadataService.SetPublicationCautions(
                Project,
                Transcript,
                SourceSpans,
                TopicRange,
                SpeakerHistory,
                Metadata,
                newRevisionId,
                GetTextArray(update, "values"),
                actorSid,
                createdAt,
                reason),
            _ => throw new FormatException("The topic field is invalid."),
        };
        return new AnalysisTopicBinding(
            Project,
            Transcript,
            SourceSpans,
            SpeakerHistory,
            TopicRange,
            revised);
    }

    private static string GetRequiredText(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not string text ||
            string.IsNullOrWhiteSpace(text) ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("The topic text is invalid.");
        }

        return text;
    }

    private static string? GetOptionalText(
        JsonElement value,
        string propertyName) =>
        value.GetProperty(propertyName).ValueKind == JsonValueKind.Null
            ? null
            : GetRequiredText(value, propertyName);

    private static EntityId<TTag>? GetOptionalEntity<TTag>(
        JsonElement value,
        string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null
            ? null
            : AnalysisBridgeRuntime.ParseEntity<TTag>(property);
    }

    private static IReadOnlyList<string> GetTextArray(
        JsonElement value,
        string propertyName) =>
        value.GetProperty(propertyName)
            .EnumerateArray()
            .Select(item => item.GetString()
                ?? throw new FormatException("The topic text list is invalid."))
            .ToArray();

    private static IReadOnlyList<EntityId<TTag>> GetEntityArray<TTag>(
        JsonElement value,
        string propertyName) =>
        value.GetProperty(propertyName)
            .EnumerateArray()
            .Select(AnalysisBridgeRuntime.ParseEntity<TTag>)
            .ToArray();
}

internal abstract class AnalysisAdoptionTarget
{
}

internal sealed class AnalysisTermAdoptionTarget : AnalysisAdoptionTarget
{
    internal AnalysisTermAdoptionTarget(
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry)
    {
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(currentSourceSpans);
        ArgumentNullException.ThrowIfNull(currentTermEntry);
        CurrentRevision = currentRevision;
        CurrentSourceSpans = Array.AsReadOnly(currentSourceSpans.ToArray());
        CurrentTermEntry = currentTermEntry;
    }

    internal TranscriptRevision CurrentRevision { get; }

    internal IReadOnlyList<SourceSpan> CurrentSourceSpans { get; }

    internal TermEntry CurrentTermEntry { get; }
}

internal sealed class AnalysisDeleteAdoptionTarget : AnalysisAdoptionTarget
{
    internal AnalysisDeleteAdoptionTarget(
        EntityId<CompositionElementIdTag> elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        ElementId = elementId;
    }

    internal EntityId<CompositionElementIdTag> ElementId { get; }
}

internal sealed class AnalysisTopicBoundaryAdoptionTarget : AnalysisAdoptionTarget
{
    internal AnalysisTopicBoundaryAdoptionTarget(
        EntityId<CompositionElementIdTag> elementId,
        RationalTime splitOffset,
        EntityId<CompositionElementIdTag> rightElementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        ArgumentNullException.ThrowIfNull(rightElementId);
        if (splitOffset <= default(RationalTime) || elementId == rightElementId)
        {
            throw new ArgumentException(
                "The topic-boundary adoption target is invalid.",
                nameof(splitOffset));
        }

        ElementId = elementId;
        SplitOffset = splitOffset;
        RightElementId = rightElementId;
    }

    internal EntityId<CompositionElementIdTag> ElementId { get; }

    internal RationalTime SplitOffset { get; }

    internal EntityId<CompositionElementIdTag> RightElementId { get; }
}

internal sealed class AnalysisHighlightAdoptionTarget : AnalysisAdoptionTarget
{
    internal AnalysisHighlightAdoptionTarget(
        EntityId<CueIdTag> cueId,
        TimeRange outputRange,
        string? regionName)
    {
        ArgumentNullException.ThrowIfNull(cueId);
        Cue = new Cue(cueId, CueKind.Graphic, outputRange, regionName);
    }

    internal Cue Cue { get; }
}

internal sealed class AnalysisCandidateView
{
    internal AnalysisCandidateView(
        SuggestionCandidateDisplayState display,
        bool metadataBindingCurrent,
        bool canAdopt,
        bool canReject,
        string displayKind)
    {
        Display = display;
        MetadataBindingCurrent = metadataBindingCurrent;
        CanAdopt = canAdopt;
        CanReject = canReject;
        DisplayKind = displayKind;
    }

    internal SuggestionCandidateDisplayState Display { get; }

    internal bool MetadataBindingCurrent { get; }

    internal SuggestionCandidateFreshness Freshness =>
        Display.Freshness == SuggestionCandidateFreshness.Current &&
        MetadataBindingCurrent
            ? SuggestionCandidateFreshness.Current
            : SuggestionCandidateFreshness.Stale;

    internal bool CanAdopt { get; }

    internal bool CanReject { get; }

    internal string DisplayKind { get; }
}

internal sealed class AnalysisCandidateMutation
{
    internal AnalysisCandidateMutation(
        SuggestionCandidate candidate,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        AnalysisStagedChange? stagedChange)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(decisionHistory);
        Candidate = candidate;
        DecisionHistory = decisionHistory;
        StagedChange = stagedChange;
    }

    internal SuggestionCandidate Candidate { get; }

    internal IReadOnlyList<SuggestionDecisionRecord> DecisionHistory { get; }

    internal AnalysisStagedChange? StagedChange { get; }
}

internal sealed class AnalysisCandidateBinding
{
    private AnalysisCandidateBinding(
        SuggestionCandidate candidate,
        SourceSpan sourceSpan,
        ContentDigest currentSourceDigest,
        AnalysisAdoptionTarget? adoptionTarget)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(currentSourceDigest);
        if (candidate.Origin.Kind != OriginKind.SourceSpan ||
            candidate.Origin.SourceSpanId != sourceSpan.Id)
        {
            throw new ArgumentException(
                "The analysis candidate does not match its source span.",
                nameof(sourceSpan));
        }

        ValidateTarget(candidate, adoptionTarget);
        Candidate = candidate;
        SourceSpan = sourceSpan;
        CurrentSourceDigest = currentSourceDigest;
        AdoptionTarget = adoptionTarget;
    }

    internal SuggestionCandidate Candidate { get; }

    internal SourceSpan SourceSpan { get; }

    internal ContentDigest CurrentSourceDigest { get; }

    internal AnalysisAdoptionTarget? AdoptionTarget { get; }

    internal static AnalysisCandidateBinding ForTerm(
        TermSuggestionCandidate candidate,
        SourceSpan sourceSpan,
        TranscriptRevision currentRevision,
        IReadOnlyList<SourceSpan> currentSourceSpans,
        TermEntry currentTermEntry) =>
        new(
            candidate,
            sourceSpan,
            candidate.SourceDigest,
            new AnalysisTermAdoptionTarget(
                currentRevision,
                currentSourceSpans,
                currentTermEntry));

    internal static AnalysisCandidateBinding ForDelete(
        DeleteRangeSuggestionCandidate candidate,
        SourceSpan sourceSpan,
        ContentDigest currentSourceDigest,
        EntityId<CompositionElementIdTag> elementId) =>
        new(
            candidate,
            sourceSpan,
            currentSourceDigest,
            new AnalysisDeleteAdoptionTarget(elementId));

    internal static AnalysisCandidateBinding ForTechnicalWarning(
        AudioReviewSuggestionCandidate candidate,
        SourceSpan sourceSpan,
        ContentDigest currentSourceDigest)
    {
        if (!candidate.Payload.TechnicalWarningOnly)
        {
            throw new ArgumentException(
                "Only recording-quality candidates are technical warnings.",
                nameof(candidate));
        }

        return new AnalysisCandidateBinding(
            candidate,
            sourceSpan,
            currentSourceDigest,
            adoptionTarget: null);
    }

    internal static AnalysisCandidateBinding ForTopicBoundary(
        TopicBoundarySuggestionCandidate candidate,
        SourceSpan sourceSpan,
        ContentDigest currentSourceDigest,
        EntityId<CompositionElementIdTag> elementId,
        RationalTime splitOffset,
        EntityId<CompositionElementIdTag> rightElementId) =>
        new(
            candidate,
            sourceSpan,
            currentSourceDigest,
            new AnalysisTopicBoundaryAdoptionTarget(
                elementId,
                splitOffset,
                rightElementId));

    internal static AnalysisCandidateBinding ForHighlight(
        EmphasisSuggestionCandidate candidate,
        SourceSpan sourceSpan,
        ContentDigest currentSourceDigest,
        EntityId<CueIdTag> cueId,
        TimeRange outputRange,
        string? regionName) =>
        new(
            candidate,
            sourceSpan,
            currentSourceDigest,
            new AnalysisHighlightAdoptionTarget(
                cueId,
                outputRange,
                regionName));

    internal AnalysisCandidateView GetView(
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        IReadOnlyList<AnalysisTopicBinding> topics)
    {
        SuggestionCandidateDisplayState display = Candidate switch
        {
            TermSuggestionCandidate term when AdoptionTarget is
                AnalysisTermAdoptionTarget target =>
                SuggestionService.GetDisplayState(
                    term,
                    target.CurrentRevision,
                    target.CurrentSourceSpans,
                    target.CurrentTermEntry,
                    decisionHistory),
            TermSuggestionCandidate => throw new InvalidOperationException(
                "A term candidate requires its typed source context."),
            _ => SuggestionService.GetDisplayState(
                Candidate,
                CurrentSourceDigest,
                decisionHistory),
        };
        bool metadataCurrent = Candidate is not EmphasisSuggestionCandidate emphasis ||
            topics.Any(topic =>
                topic.Metadata.Id == emphasis.Payload.TopicMetadataRevisionId);
        bool canDecide = display.CanDecide && metadataCurrent;
        bool isTechnicalWarning = Candidate is AudioReviewSuggestionCandidate audio &&
            audio.Payload.TechnicalWarningOnly;
        return new AnalysisCandidateView(
            display,
            metadataCurrent,
            canDecide && AdoptionTarget is not null && !isTechnicalWarning,
            canDecide,
            GetDisplayKind(Candidate, isTechnicalWarning));
    }

    internal AnalysisCandidateMutation Reject(
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        SuggestionRejectionResult result = Candidate switch
        {
            TermSuggestionCandidate term when AdoptionTarget is
                AnalysisTermAdoptionTarget target =>
                SuggestionService.RejectTerm(
                    term,
                    target.CurrentRevision,
                    target.CurrentSourceSpans,
                    target.CurrentTermEntry,
                    decisionHistory,
                    decisionId,
                    actorSid,
                    decidedAt,
                    reason,
                    cancellationToken),
            TermSuggestionCandidate => throw new InvalidOperationException(
                "A term candidate requires its typed source context."),
            _ => SuggestionService.Reject(
                Candidate,
                CurrentSourceDigest,
                decisionHistory,
                decisionId,
                actorSid,
                decidedAt,
                reason,
                cancellationToken),
        };
        return new AnalysisCandidateMutation(
            result.DecidedCandidate,
            result.DecisionHistory,
            stagedChange: null);
    }

    internal AnalysisCandidateMutation Adopt(
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        EntityId<SuggestionDecisionIdTag> decisionId,
        EntityId<TranscriptRevisionIdTag> resultingTranscriptRevisionId,
        string actorSid,
        DateTimeOffset decidedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        SuggestionAdoptionResult result = (Candidate, AdoptionTarget) switch
        {
            (TermSuggestionCandidate term, AnalysisTermAdoptionTarget target) =>
                SuggestionService.AdoptTerm(
                    term,
                    target.CurrentRevision,
                    target.CurrentSourceSpans,
                    target.CurrentTermEntry,
                    resultingTranscriptRevisionId,
                    decisionHistory,
                    decisionId,
                    actorSid,
                    decidedAt,
                    reason,
                    cancellationToken),
            (DeleteRangeSuggestionCandidate deletion,
                AnalysisDeleteAdoptionTarget target) =>
                SuggestionService.AdoptDeleteRange(
                    deletion,
                    CurrentSourceDigest,
                    target.ElementId,
                    decisionHistory,
                    decisionId,
                    actorSid,
                    decidedAt,
                    reason,
                    cancellationToken),
            (TopicBoundarySuggestionCandidate boundary,
                AnalysisTopicBoundaryAdoptionTarget target) =>
                SuggestionService.AdoptTopicBoundary(
                    boundary,
                    CurrentSourceDigest,
                    target.ElementId,
                    target.SplitOffset,
                    target.RightElementId,
                    decisionHistory,
                    decisionId,
                    actorSid,
                    decidedAt,
                    reason,
                    cancellationToken),
            (EmphasisSuggestionCandidate emphasis,
                AnalysisHighlightAdoptionTarget target) =>
                SuggestionService.AdoptHighlight(
                    emphasis,
                    CurrentSourceDigest,
                    target.Cue.Id,
                    target.Cue.Range,
                    target.Cue.RegionName,
                    decisionHistory,
                    decisionId,
                    actorSid,
                    decidedAt,
                    reason,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                "The analysis candidate has no compatible native adoption target."),
        };
        return new AnalysisCandidateMutation(
            result.DecidedCandidate,
            result.DecisionHistory,
            AnalysisStagedChange.FromAdoption(result));
    }

    internal AnalysisCandidateBinding WithCandidate(
        SuggestionCandidate candidate) =>
        new(candidate, SourceSpan, CurrentSourceDigest, AdoptionTarget);

    private static void ValidateTarget(
        SuggestionCandidate candidate,
        AnalysisAdoptionTarget? target)
    {
        bool valid = (candidate, target) switch
        {
            (TermSuggestionCandidate, AnalysisTermAdoptionTarget) => true,
            (TopicBoundarySuggestionCandidate,
                AnalysisTopicBoundaryAdoptionTarget) => true,
            (EmphasisSuggestionCandidate, AnalysisHighlightAdoptionTarget) => true,
            (AudioReviewSuggestionCandidate audio, null) =>
                audio.Payload.TechnicalWarningOnly,
            (DeleteRangeSuggestionCandidate, AnalysisDeleteAdoptionTarget) => true,
            (_, null) => true,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The native adoption target does not match the candidate kind.",
                nameof(target));
        }
    }

    private static string GetDisplayKind(
        SuggestionCandidate candidate,
        bool isTechnicalWarning) =>
        isTechnicalWarning
            ? "technicalWarning"
            : candidate.Kind switch
            {
                SuggestionKind.TermCorrection => "termCorrection",
                SuggestionKind.DeleteRange => "deleteCandidate",
                SuggestionKind.TopicBoundary => "topicBoundary",
                SuggestionKind.Highlight => "highlight",
                _ => throw new InvalidOperationException(
                    "The suggestion kind is invalid."),
            };
}

internal abstract class AnalysisStagedChange
{
    private protected AnalysisStagedChange()
    {
    }

    internal bool Persisted => false;

    internal static AnalysisStagedChange FromAdoption(
        SuggestionAdoptionResult result) =>
        result switch
        {
            TermSuggestionAdoptionResult term =>
                new AnalysisStagedTranscriptRevisionChange(
                    term.OriginalCandidate.Id,
                    term.TranscriptEdit.SourceRevision.Id,
                    term.TranscriptEdit.Revision.Id),
            EditSuggestionAdoptionResult edit =>
                new AnalysisStagedEditOperationChange(
                    edit.OriginalCandidate.Id,
                    edit.Operation),
            HighlightSuggestionAdoptionResult highlight =>
                new AnalysisStagedCueChange(
                    highlight.OriginalCandidate.Id,
                    highlight.Cue),
            _ => throw new InvalidOperationException(
                "The analysis adoption result is unsupported."),
        };
}

internal sealed class AnalysisStagedTranscriptRevisionChange :
    AnalysisStagedChange
{
    internal AnalysisStagedTranscriptRevisionChange(
        EntityId<SuggestionIdTag> candidateId,
        EntityId<TranscriptRevisionIdTag> sourceRevisionId,
        EntityId<TranscriptRevisionIdTag> resultingRevisionId)
    {
        CandidateId = candidateId;
        SourceRevisionId = sourceRevisionId;
        ResultingRevisionId = resultingRevisionId;
    }

    internal EntityId<SuggestionIdTag> CandidateId { get; }

    internal EntityId<TranscriptRevisionIdTag> SourceRevisionId { get; }

    internal EntityId<TranscriptRevisionIdTag> ResultingRevisionId { get; }
}

internal sealed class AnalysisStagedEditOperationChange : AnalysisStagedChange
{
    internal AnalysisStagedEditOperationChange(
        EntityId<SuggestionIdTag> candidateId,
        EditOperation operation)
    {
        CandidateId = candidateId;
        Operation = operation;
    }

    internal EntityId<SuggestionIdTag> CandidateId { get; }

    internal EditOperation Operation { get; }
}

internal sealed class AnalysisStagedCueChange : AnalysisStagedChange
{
    internal AnalysisStagedCueChange(
        EntityId<SuggestionIdTag> candidateId,
        Cue cue)
    {
        CandidateId = candidateId;
        Cue = cue;
    }

    internal EntityId<SuggestionIdTag> CandidateId { get; }

    internal Cue Cue { get; }
}

internal sealed class AnalysisStagedTopicMetadataChange : AnalysisStagedChange
{
    internal AnalysisStagedTopicMetadataChange(
        EntityId<TopicRangeIdTag> topicId,
        TopicMetadataRevision revision)
    {
        TopicId = topicId;
        Revision = revision;
    }

    internal EntityId<TopicRangeIdTag> TopicId { get; }

    internal TopicMetadataRevision Revision { get; }
}

internal sealed class AnalysisBridgeSnapshot
{
    private AnalysisBridgeSnapshot(
        EntityId<AnalysisBridgeRevisionIdTag> revisionId,
        DateTimeOffset createdAt,
        Project project,
        TranscriptRevision transcript,
        ContentDigest sourceRevisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        SpeakerAssignmentHistory? speakerHistory,
        IReadOnlyList<AnalysisTopicBinding> topics,
        IReadOnlyList<AnalysisCandidateBinding> candidates,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        TextSensitivityScanResult sensitivity,
        IReadOnlyList<AnalysisStagedChange> stagedChanges)
    {
        RevisionId = revisionId;
        CreatedAt = createdAt;
        Project = project;
        Transcript = transcript;
        SourceRevisionDigest = sourceRevisionDigest;
        SourceSpans = sourceSpans;
        SpeakerHistory = speakerHistory;
        Topics = topics;
        Candidates = candidates;
        DecisionHistory = decisionHistory;
        Sensitivity = sensitivity;
        StagedChanges = stagedChanges;
        Speakers = CreateSpeakerProjections(
            project,
            transcript,
            sourceSpans,
            speakerHistory);
        RevisionDigest = AnalysisBridgeJson.ComputeSnapshotDigest(this);
    }

    internal EntityId<AnalysisBridgeRevisionIdTag> RevisionId { get; }

    internal ContentDigest RevisionDigest { get; }

    internal DateTimeOffset CreatedAt { get; }

    internal Project Project { get; }

    internal TranscriptRevision Transcript { get; }

    internal ContentDigest SourceRevisionDigest { get; }

    internal IReadOnlyList<SourceSpan> SourceSpans { get; }

    internal SpeakerAssignmentHistory? SpeakerHistory { get; }

    internal IReadOnlyList<AnalysisSpeakerProjection> Speakers { get; }

    internal IReadOnlyList<AnalysisTopicBinding> Topics { get; }

    internal IReadOnlyList<AnalysisCandidateBinding> Candidates { get; }

    internal IReadOnlyList<SuggestionDecisionRecord> DecisionHistory { get; }

    internal TextSensitivityScanResult Sensitivity { get; }

    internal IReadOnlyList<AnalysisStagedChange> StagedChanges { get; }

    internal static AnalysisBridgeSnapshot Create(
        EntityId<AnalysisBridgeRevisionIdTag> revisionId,
        DateTimeOffset createdAt,
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        SpeakerAssignmentHistory? speakerHistory,
        IReadOnlyList<AnalysisTopicBinding> topics,
        IReadOnlyList<AnalysisCandidateBinding> candidates,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        TextSensitivityScanResult sensitivity)
    {
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(decisionHistory);
        ArgumentNullException.ThrowIfNull(sensitivity);
        ContentDigest digest = ComputeTranscriptDigest(transcript);
        SourceSpan[] copiedSpans = ValidateSourceSpans(transcript, sourceSpans);
        ValidateSpeakerHistory(project, transcript, digest, speakerHistory);
        AnalysisTopicBinding[] copiedTopics = ValidateTopics(
            project,
            transcript,
            topics);
        AnalysisCandidateBinding[] copiedCandidates = ValidateCandidates(
            candidates,
            decisionHistory,
            copiedTopics);
        ValidateSensitivity(transcript, copiedSpans, sensitivity);
        return new AnalysisBridgeSnapshot(
            revisionId,
            createdAt,
            project,
            transcript,
            digest,
            Array.AsReadOnly(copiedSpans),
            speakerHistory,
            Array.AsReadOnly(copiedTopics),
            Array.AsReadOnly(copiedCandidates),
            Array.AsReadOnly(decisionHistory.ToArray()),
            sensitivity,
            stagedChanges: Array.Empty<AnalysisStagedChange>());
    }

    internal AnalysisBridgeSnapshot WithCandidateMutation(
        EntityId<AnalysisBridgeRevisionIdTag> revisionId,
        DateTimeOffset createdAt,
        AnalysisCandidateBinding original,
        AnalysisCandidateMutation mutation)
    {
        AnalysisCandidateBinding[] candidates = Candidates
            .Select(candidate => ReferenceEquals(candidate, original)
                ? candidate.WithCandidate(mutation.Candidate)
                : candidate)
            .ToArray();
        AnalysisStagedChange[] staged = mutation.StagedChange is null
            ? StagedChanges.ToArray()
            : [.. StagedChanges, mutation.StagedChange];
        return CreateDerived(
            revisionId,
            createdAt,
            Topics,
            candidates,
            mutation.DecisionHistory,
            staged);
    }

    internal AnalysisBridgeSnapshot WithTopicUpdate(
        EntityId<AnalysisBridgeRevisionIdTag> revisionId,
        DateTimeOffset createdAt,
        AnalysisTopicBinding original,
        AnalysisTopicBinding updated)
    {
        AnalysisTopicBinding[] topics = Topics
            .Select(topic => ReferenceEquals(topic, original) ? updated : topic)
            .ToArray();
        return CreateDerived(
            revisionId,
            createdAt,
            topics,
            Candidates,
            DecisionHistory,
            [.. StagedChanges, new AnalysisStagedTopicMetadataChange(
                updated.TopicRange.TopicRangeId,
                updated.Metadata)]);
    }

    internal static ContentDigest ComputeTranscriptDigest(
        TranscriptRevision transcript) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(
            TranscriptSnapshotJson.WriteRevision(transcript)));

    private AnalysisBridgeSnapshot CreateDerived(
        EntityId<AnalysisBridgeRevisionIdTag> revisionId,
        DateTimeOffset createdAt,
        IReadOnlyList<AnalysisTopicBinding> topics,
        IReadOnlyList<AnalysisCandidateBinding> candidates,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        IReadOnlyList<AnalysisStagedChange> stagedChanges)
    {
        if (revisionId == RevisionId || createdAt < CreatedAt)
        {
            throw new ArgumentException(
                "The analysis revision does not advance the snapshot.",
                nameof(revisionId));
        }

        AnalysisTopicBinding[] validatedTopics = ValidateTopics(
            Project,
            Transcript,
            topics);
        AnalysisCandidateBinding[] validatedCandidates = ValidateCandidates(
            candidates,
            decisionHistory,
            validatedTopics);
        return new AnalysisBridgeSnapshot(
            revisionId,
            createdAt,
            Project,
            Transcript,
            SourceRevisionDigest,
            SourceSpans,
            SpeakerHistory,
            Array.AsReadOnly(validatedTopics),
            Array.AsReadOnly(validatedCandidates),
            Array.AsReadOnly(decisionHistory.ToArray()),
            Sensitivity,
            Array.AsReadOnly(stagedChanges.ToArray()));
    }

    private static SourceSpan[] ValidateSourceSpans(
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count != transcript.Utterances.Count)
        {
            throw new ArgumentException(
                "Analysis source spans must cover the transcript.",
                nameof(sourceSpans));
        }

        SourceSpan[] copied = new SourceSpan[sourceSpans.Count];
        var ids = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw new ArgumentException(
                    "Analysis source spans must not contain null.",
                    nameof(sourceSpans));
            Utterance utterance = transcript.Utterances[index];
            if (!ids.Add(span.Id) ||
                span.Id != utterance.SourceSpanId ||
                span.TranscriptRevisionId != transcript.Id ||
                span.Range != utterance.Range ||
                !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw new ArgumentException(
                    "Analysis source spans do not match the transcript.",
                    nameof(sourceSpans));
            }

            copied[index] = span;
        }

        return copied;
    }

    private static void ValidateSpeakerHistory(
        Project project,
        TranscriptRevision transcript,
        ContentDigest digest,
        SpeakerAssignmentHistory? history)
    {
        if (history is not null &&
            (history.ProjectId != project.Id ||
                history.TranscriptRevisionId != transcript.Id ||
                history.TranscriptRevisionDigest != digest))
        {
            throw new ArgumentException(
                "Analysis speaker history does not match the transcript.",
                nameof(history));
        }
    }

    private static AnalysisTopicBinding[] ValidateTopics(
        Project project,
        TranscriptRevision transcript,
        IReadOnlyList<AnalysisTopicBinding> topics)
    {
        var topicIds = new HashSet<EntityId<TopicRangeIdTag>>();
        var metadataIds = new HashSet<EntityId<TopicMetadataIdTag>>();
        AnalysisTopicBinding[] copied = new AnalysisTopicBinding[topics.Count];
        RationalTime? previousEnd = null;
        for (int index = 0; index < copied.Length; index++)
        {
            AnalysisTopicBinding topic = topics[index]
                ?? throw new ArgumentException(
                    "Analysis topics must not contain null.",
                    nameof(topics));
            if (topic.Project.Id != project.Id ||
                topic.Transcript.Id != transcript.Id ||
                !topicIds.Add(topic.TopicRange.TopicRangeId) ||
                !metadataIds.Add(topic.Metadata.TopicMetadataId) ||
                previousEnd is RationalTime end &&
                    topic.TopicRange.Range.Start < end)
            {
                throw new ArgumentException(
                    "Analysis topics are inconsistent or overlapping.",
                    nameof(topics));
            }

            previousEnd = topic.TopicRange.Range.End;
            copied[index] = topic;
        }

        return copied;
    }

    private static AnalysisCandidateBinding[] ValidateCandidates(
        IReadOnlyList<AnalysisCandidateBinding> candidates,
        IReadOnlyList<SuggestionDecisionRecord> decisionHistory,
        IReadOnlyList<AnalysisTopicBinding> topics)
    {
        var candidateIds = new HashSet<EntityId<SuggestionIdTag>>();
        AnalysisCandidateBinding[] copied = new AnalysisCandidateBinding[
            candidates.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            AnalysisCandidateBinding candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Analysis candidates must not contain null.",
                    nameof(candidates));
            if (!candidateIds.Add(candidate.Candidate.Id))
            {
                throw new ArgumentException(
                    "Analysis candidate IDs must be unique.",
                    nameof(candidates));
            }

            _ = candidate.GetView(decisionHistory, topics);
            copied[index] = candidate;
        }

        return copied;
    }

    private static void ValidateSensitivity(
        TranscriptRevision transcript,
        IReadOnlyList<SourceSpan> sourceSpans,
        TextSensitivityScanResult sensitivity)
    {
        if (sensitivity.RevisionId != transcript.Id ||
            sensitivity.Findings.Any(finding =>
                !sourceSpans.Any(span => span.Id == finding.SourceSpanId)))
        {
            throw new ArgumentException(
                "Sensitivity evidence does not match the transcript.",
                nameof(sensitivity));
        }
    }

    private static IReadOnlyList<AnalysisSpeakerProjection>
        CreateSpeakerProjections(
            Project project,
            TranscriptRevision transcript,
            IReadOnlyList<SourceSpan> sourceSpans,
            SpeakerAssignmentHistory? speakerHistory)
    {
        var seen = new HashSet<EntityId<TranscriptSpeakerIdTag>>();
        var speakers = new List<AnalysisSpeakerProjection>();
        foreach (Utterance utterance in transcript.Utterances)
        {
            if (!seen.Add(utterance.SpeakerId))
            {
                continue;
            }

            SourceSpan sourceSpan = sourceSpans.Single(span =>
                span.Id == utterance.SourceSpanId);
            speakers.Add(new AnalysisSpeakerProjection(
                SpeakerAssignmentService.ResolveDisplay(
                    project,
                    transcript,
                    speakerHistory,
                    utterance.SpeakerId),
                sourceSpan));
        }

        return Array.AsReadOnly(speakers.ToArray());
    }
}

internal sealed class AnalysisBridgeOperationResult
{
    private AnalysisBridgeOperationResult(
        AnalysisBridgeStatus status,
        AnalysisBridgeSnapshot? snapshot)
    {
        if ((status == AnalysisBridgeStatus.Available) != (snapshot is not null))
        {
            throw new ArgumentException(
                "The analysis bridge result is inconsistent.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
    }

    internal AnalysisBridgeStatus Status { get; }

    internal AnalysisBridgeSnapshot? Snapshot { get; }

    internal static AnalysisBridgeOperationResult Available(
        AnalysisBridgeSnapshot snapshot) =>
        new(AnalysisBridgeStatus.Available, snapshot);

    internal static AnalysisBridgeOperationResult Forbidden() =>
        new(AnalysisBridgeStatus.Forbidden, snapshot: null);

    internal static AnalysisBridgeOperationResult Unavailable() =>
        new(AnalysisBridgeStatus.Unavailable, snapshot: null);

    internal static AnalysisBridgeOperationResult StaleBase() =>
        new(AnalysisBridgeStatus.StaleBase, snapshot: null);

    internal static AnalysisBridgeOperationResult Conflict() =>
        new(AnalysisBridgeStatus.Conflict, snapshot: null);

    internal static AnalysisBridgeOperationResult NoChange() =>
        new(AnalysisBridgeStatus.NoChange, snapshot: null);

    internal static AnalysisBridgeOperationResult Invalid() =>
        new(AnalysisBridgeStatus.Invalid, snapshot: null);
}

internal interface IAnalysisBridgeRuntime
{
    AnalysisBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken);
}

internal sealed class AnalysisBridgeRuntime : IAnalysisBridgeRuntime
{
    private readonly object gate = new();
    private readonly Dictionary<
        EntityId<ProjectIdTag>,
        AnalysisBridgeSnapshot> snapshots = [];
    private readonly Func<DateTimeOffset> getCurrentTime;
    private readonly Func<Guid> createId;

    internal AnalysisBridgeRuntime()
        : this(
            static () => DateTimeOffset.UtcNow,
            static () => Guid.NewGuid())
    {
    }

    internal AnalysisBridgeRuntime(
        Func<DateTimeOffset> getCurrentTime,
        Func<Guid> createId)
    {
        ArgumentNullException.ThrowIfNull(getCurrentTime);
        ArgumentNullException.ThrowIfNull(createId);
        this.getCurrentTime = getCurrentTime;
        this.createId = createId;
    }

    internal void Register(AnalysisBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            snapshots[snapshot.Project.Id] = snapshot;
        }
    }

    internal AnalysisBridgeSnapshot GetRequired(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        lock (gate)
        {
            return snapshots.TryGetValue(projectId, out AnalysisBridgeSnapshot? snapshot)
                ? snapshot
                : throw new KeyNotFoundException(
                    "No analysis snapshot is registered for the project.");
        }
    }

    public AnalysisBridgeOperationResult Execute(
        BridgeRequest request,
        string actorSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EntityId<ProjectIdTag> projectId = ParseEntity<ProjectIdTag>(
                request.Parameters.GetProperty("projectId"));
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!snapshots.TryGetValue(projectId, out AnalysisBridgeSnapshot? snapshot))
                {
                    return AnalysisBridgeOperationResult.Unavailable();
                }

                AnalysisBridgeOperationResult result = request.Method switch
                {
                    BridgeMethod.AnalysisRead =>
                        AnalysisBridgeOperationResult.Available(snapshot),
                    BridgeMethod.AnalysisReject => DecideCandidate(
                        request,
                        snapshot,
                        actorSid,
                        adopt: false,
                        cancellationToken),
                    BridgeMethod.AnalysisAdopt => DecideCandidate(
                        request,
                        snapshot,
                        actorSid,
                        adopt: true,
                        cancellationToken),
                    BridgeMethod.AnalysisUpdateTopic => UpdateTopic(
                        request,
                        snapshot,
                        actorSid),
                    _ => AnalysisBridgeOperationResult.Invalid(),
                };
                if (result.Snapshot is not null)
                {
                    if (!AnalysisBridgeJson.CanWriteResponse(request, result))
                    {
                        return AnalysisBridgeOperationResult.Unavailable();
                    }

                    snapshots[projectId] = result.Snapshot;
                }

                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SuggestionServiceException exception)
        {
            return exception.Reason switch
            {
                SuggestionServiceFailureReason.CandidateStale =>
                    AnalysisBridgeOperationResult.StaleBase(),
                SuggestionServiceFailureReason.CandidateAlreadyDecided or
                SuggestionServiceFailureReason.DuplicateDecision =>
                    AnalysisBridgeOperationResult.Conflict(),
                _ => AnalysisBridgeOperationResult.Invalid(),
            };
        }
        catch (TopicMetadataException exception)
        {
            return exception.Reason switch
            {
                TopicMetadataFailureReason.RevisionConflict or
                TopicMetadataFailureReason.ContextMismatch =>
                    AnalysisBridgeOperationResult.Conflict(),
                TopicMetadataFailureReason.NoChange =>
                    AnalysisBridgeOperationResult.NoChange(),
                _ => AnalysisBridgeOperationResult.Invalid(),
            };
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or
                InvalidOperationException or KeyNotFoundException or
                OverflowException or JsonException)
        {
            return AnalysisBridgeOperationResult.Invalid();
        }
    }

    private AnalysisBridgeOperationResult DecideCandidate(
        BridgeRequest request,
        AnalysisBridgeSnapshot snapshot,
        string actorSid,
        bool adopt,
        CancellationToken cancellationToken)
    {
        AnalysisBridgeOperationResult? mismatch = CheckOcc(
            request.Parameters,
            snapshot);
        if (mismatch is not null)
        {
            return mismatch;
        }

        EntityId<SuggestionIdTag> candidateId = ParseEntity<SuggestionIdTag>(
            request.Parameters.GetProperty("candidateId"));
        AnalysisCandidateBinding? binding = snapshot.Candidates.SingleOrDefault(
            item => item.Candidate.Id == candidateId);
        if (binding is null)
        {
            return AnalysisBridgeOperationResult.Conflict();
        }

        AnalysisCandidateView view = binding.GetView(
            snapshot.DecisionHistory,
            snapshot.Topics);
        if (adopt ? !view.CanAdopt : !view.CanReject)
        {
            return view.Freshness == SuggestionCandidateFreshness.Stale
                ? AnalysisBridgeOperationResult.StaleBase()
                : AnalysisBridgeOperationResult.Conflict();
        }

        DateTimeOffset now = getCurrentTime();
        EntityId<SuggestionDecisionIdTag> decisionId =
            CreateEntityId<SuggestionDecisionIdTag>();
        string reason = GetRequiredText(request.Parameters, "reason");
        AnalysisCandidateMutation mutation = adopt
            ? binding.Adopt(
                snapshot.DecisionHistory,
                decisionId,
                CreateEntityId<TranscriptRevisionIdTag>(),
                actorSid,
                now,
                reason,
                cancellationToken)
            : binding.Reject(
                snapshot.DecisionHistory,
                decisionId,
                actorSid,
                now,
                reason,
                cancellationToken);
        AnalysisBridgeSnapshot updated = snapshot.WithCandidateMutation(
            CreateEntityId<AnalysisBridgeRevisionIdTag>(),
            now,
            binding,
            mutation);
        return AnalysisBridgeOperationResult.Available(updated);
    }

    private AnalysisBridgeOperationResult UpdateTopic(
        BridgeRequest request,
        AnalysisBridgeSnapshot snapshot,
        string actorSid)
    {
        AnalysisBridgeOperationResult? mismatch = CheckOcc(
            request.Parameters,
            snapshot);
        if (mismatch is not null)
        {
            return mismatch;
        }

        EntityId<TopicRangeIdTag> topicId = ParseEntity<TopicRangeIdTag>(
            request.Parameters.GetProperty("topicId"));
        EntityId<TopicMetadataRevisionIdTag> expectedRevision =
            ParseEntity<TopicMetadataRevisionIdTag>(
                request.Parameters.GetProperty("expectedTopicRevisionId"));
        AnalysisTopicBinding? topic = snapshot.Topics.SingleOrDefault(
            item => item.TopicRange.TopicRangeId == topicId);
        if (topic is null || topic.Metadata.Id != expectedRevision)
        {
            return AnalysisBridgeOperationResult.Conflict();
        }

        DateTimeOffset now = getCurrentTime();
        AnalysisTopicBinding updated = topic.Update(
            request.Parameters.GetProperty("update"),
            CreateEntityId<TopicMetadataRevisionIdTag>(),
            actorSid,
            now,
            GetRequiredText(request.Parameters, "reason"));
        return AnalysisBridgeOperationResult.Available(
            snapshot.WithTopicUpdate(
                CreateEntityId<AnalysisBridgeRevisionIdTag>(),
                now,
                topic,
                updated));
    }

    internal static EntityId<TTag> ParseEntity<TTag>(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !EntityId<TTag>.TryParse(value.GetString(), out EntityId<TTag>? entityId) ||
            !string.Equals(
                value.GetString(),
                entityId.ToString(),
                StringComparison.Ordinal))
        {
            throw new FormatException("The analysis entity ID is invalid.");
        }

        return entityId;
    }

    private static AnalysisBridgeOperationResult? CheckOcc(
        JsonElement parameters,
        AnalysisBridgeSnapshot snapshot)
    {
        EntityId<AnalysisBridgeRevisionIdTag> revisionId =
            ParseEntity<AnalysisBridgeRevisionIdTag>(
                parameters.GetProperty("revisionId"));
        ContentDigest digest = ParseDigest(parameters.GetProperty("digest"));
        return revisionId != snapshot.RevisionId ||
            digest != snapshot.RevisionDigest
                ? AnalysisBridgeOperationResult.StaleBase()
                : null;
    }

    private EntityId<TTag> CreateEntityId<TTag>()
    {
        Guid value = createId();
        if (value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The generated analysis entity ID must not be empty.");
        }

        return EntityId<TTag>.Parse(value.ToString("D"));
    }

    private static ContentDigest ParseDigest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !ContentDigest.TryParse(value.GetString(), out ContentDigest? digest) ||
            !string.Equals(value.GetString(), digest.Hex, StringComparison.Ordinal))
        {
            throw new FormatException("The analysis digest is invalid.");
        }

        return digest;
    }

    private static string GetRequiredText(
        JsonElement parameters,
        string propertyName)
    {
        JsonElement value = parameters.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text ||
            string.IsNullOrWhiteSpace(text) ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("The analysis text is invalid.");
        }

        return text;
    }
}

internal interface IAnalysisBridgeAuthorization
{
    string DemandRead();

    string DemandMutation();
}

internal sealed class AnalysisBridgeAuthorization : IAnalysisBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal AnalysisBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(
            authorizationService,
            (actorProvider ?? throw new ArgumentNullException(nameof(actorProvider)))
                .GetCurrentSid)
    {
    }

    internal AnalysisBridgeAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public string DemandRead()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.ViewProject);
        return actorSid;
    }

    public string DemandMutation()
    {
        string actorSid = getCurrentSid();
        authorizationService.Demand(
            actorSid,
            AuthorizationOperation.DecideAnalysisSuggestion);
        return actorSid;
    }
}

internal sealed class AnalysisBridgeRequestHandler
{
    private readonly IAnalysisBridgeRuntime runtime;
    private readonly IAnalysisBridgeAuthorization authorization;

    internal AnalysisBridgeRequestHandler(
        IAnalysisBridgeRuntime runtime,
        IAnalysisBridgeAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(authorization);
        this.runtime = runtime;
        this.authorization = authorization;
    }

    internal Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisBridgeOperationResult result;
        try
        {
            string actorSid = request.Method == BridgeMethod.AnalysisRead
                ? authorization.DemandRead()
                : authorization.DemandMutation();
            result = runtime.Execute(request, actorSid, cancellationToken) ??
                AnalysisBridgeOperationResult.Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthorizationException)
        {
            result = AnalysisBridgeOperationResult.Forbidden();
        }
        catch (Exception)
        {
            result = AnalysisBridgeOperationResult.Unavailable();
        }

        return Task.FromResult(AnalysisBridgeJson.WriteResponse(request, result));
    }
}

internal static class AnalysisBridgeJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static ContentDigest ComputeSnapshotDigest(
        AnalysisBridgeSnapshot snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSnapshot(writer, snapshot, includeRevisionDigest: false);
        }

        return ContentDigest.Compute(buffer.WrittenSpan);
    }

    internal static string WriteResponse(
        BridgeRequest request,
        AnalysisBridgeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return WriteResponseCore(request, result);
        }
        catch (InvalidOperationException)
            when (result.Status == AnalysisBridgeStatus.Available)
        {
            return WriteResponseCore(
                request,
                AnalysisBridgeOperationResult.Unavailable());
        }
    }

    internal static bool CanWriteResponse(
        BridgeRequest request,
        AnalysisBridgeOperationResult result)
    {
        try
        {
            _ = WriteResponseCore(request, result);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string WriteResponseCore(
        BridgeRequest request,
        AnalysisBridgeOperationResult result)
    {
        var buffer = new PlayerBridgeJson.BoundedBufferWriter(
            BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("version", BridgeProtocol.Version);
        writer.WriteString("requestId", request.RequestId.ToString("D"));
        writer.WriteString("method", BridgeProtocol.GetMethodName(request.Method));
        writer.WriteStartObject("result");
        writer.WriteString("status", GetStatusText(result.Status));
        writer.WritePropertyName("snapshot");
        if (result.Snapshot is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteSnapshot(writer, result.Snapshot, includeRevisionDigest: true);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        AnalysisBridgeSnapshot snapshot,
        bool includeRevisionDigest)
    {
        writer.WriteStartObject();
        writer.WriteString("projectId", snapshot.Project.Id.ToString());
        writer.WriteString("revisionId", snapshot.RevisionId.ToString());
        if (includeRevisionDigest)
        {
            writer.WriteString("revisionDigest", snapshot.RevisionDigest.Hex);
        }

        writer.WriteString("createdAt", FormatTimestamp(snapshot.CreatedAt));
        writer.WriteString("sourceRevisionId", snapshot.Transcript.Id.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            snapshot.SourceRevisionDigest.Hex);
        WriteSpeakers(writer, snapshot.Speakers);
        WriteTopics(writer, snapshot.Topics);
        writer.WriteStartArray("candidates");
        foreach (AnalysisCandidateBinding binding in snapshot.Candidates)
        {
            WriteCandidate(writer, snapshot, binding);
        }

        writer.WriteEndArray();
        WriteSensitivity(writer, snapshot);
        WriteStagedChanges(writer, snapshot.StagedChanges);
        writer.WriteString("adoptionPersistence", "notPersisted");
        writer.WriteEndObject();
    }

    private static void WriteSpeakers(
        Utf8JsonWriter writer,
        IReadOnlyList<AnalysisSpeakerProjection> speakers)
    {
        writer.WriteStartArray("speakers");
        foreach (AnalysisSpeakerProjection speaker in speakers)
        {
            ResolvedSpeakerDisplay display = speaker.Display;
            writer.WriteStartObject();
            writer.WriteString("speakerId", display.SpeakerId.ToString());
            WriteNullableId(writer, "performerId", display.PerformerId);
            writer.WritePropertyName("format");
            if (display.Format is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(GetSpeakerFormat(display.Format.Value));
            }

            writer.WriteBoolean("isAssigned", display.IsAssigned);
            writer.WriteBoolean("isVisible", display.IsVisible);
            WriteNullableText(writer, "text", display.Text);
            WriteNullableTimestamp(
                writer,
                "titleConfirmedAt",
                display.TitleConfirmedAt);
            writer.WriteBoolean(
                "titleOrAffiliationConfirmationRequired",
                display.TitleOrAffiliationConfirmationRequired);
            writer.WritePropertyName("source");
            WriteSource(writer, speaker.SourceSpan);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTopics(
        Utf8JsonWriter writer,
        IReadOnlyList<AnalysisTopicBinding> topics)
    {
        writer.WriteStartArray("topics");
        foreach (AnalysisTopicBinding topic in topics)
        {
            TopicMetadataRevision revision = topic.Metadata;
            TopicMetadata metadata = revision.Metadata;
            writer.WriteStartObject();
            writer.WriteString("topicId", topic.TopicRange.TopicRangeId.ToString());
            writer.WriteString(
                "topicRangeRevisionId",
                topic.TopicRange.Id.ToString());
            writer.WriteString("metadataId", revision.TopicMetadataId.ToString());
            writer.WriteString("metadataRevisionId", revision.Id.ToString());
            WriteNullableId(
                writer,
                "parentMetadataRevisionId",
                revision.ParentRevisionId);
            writer.WritePropertyName("range");
            WriteTimeRange(writer, topic.TopicRange.Range);
            WriteNullableId(
                writer,
                "startBoundaryCandidateId",
                topic.TopicRange.StartBoundarySuggestionId);
            WriteNullableId(
                writer,
                "endBoundaryCandidateId",
                topic.TopicRange.EndBoundarySuggestionId);
            writer.WriteString("title", metadata.Title);
            writer.WriteString("titleOrigin", GetMetadataOrigin(metadata.TitleOrigin));
            writer.WriteBoolean(
                "titleRequiresManualReview",
                metadata.TitleRequiresManualReview);
            WriteNullableText(writer, "summary", metadata.Summary);
            writer.WriteString(
                "summaryOrigin",
                GetMetadataOrigin(metadata.SummaryOrigin));
            writer.WritePropertyName("mainSpeaker");
            if (metadata.MainSpeaker is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteTopicMainSpeaker(writer, metadata.MainSpeaker);
            }

            writer.WriteString(
                "mainSpeakerOrigin",
                GetMetadataOrigin(metadata.MainSpeakerOrigin));
            writer.WriteBoolean(
                "mainSpeakerTieUnresolved",
                metadata.MainSpeakerTieUnresolved);
            WriteStringArray(writer, "keywords", metadata.Keywords);
            writer.WriteString(
                "keywordsOrigin",
                GetMetadataOrigin(metadata.KeywordsOrigin));
            WriteIdArray(
                writer,
                "importantStatementSourceSpanIds",
                metadata.ImportantStatementSourceSpanIds);
            writer.WriteString(
                "importantStatementsOrigin",
                GetMetadataOrigin(metadata.ImportantStatementsOrigin));
            writer.WriteString("audience", metadata.Audience);
            writer.WriteString(
                "audienceOrigin",
                GetMetadataOrigin(metadata.AudienceOrigin));
            WriteStringArray(
                writer,
                "publicationCautions",
                metadata.PublicationCautions);
            writer.WriteString(
                "publicationCautionsOrigin",
                GetMetadataOrigin(metadata.PublicationCautionsOrigin));
            writer.WriteString("changedField", GetMetadataField(revision.ChangedField));
            writer.WriteString("createdAt", FormatTimestamp(revision.CreatedAt));
            writer.WriteString("reason", revision.Reason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTopicMainSpeaker(
        Utf8JsonWriter writer,
        TopicMainSpeakerSnapshot speaker)
    {
        writer.WriteStartObject();
        writer.WriteString("speakerId", speaker.SpeakerId.ToString());
        WriteNullableId(writer, "performerId", speaker.PerformerId);
        writer.WriteBoolean("displayVisible", speaker.DisplayVisible);
        WriteNullableText(writer, "displayText", speaker.DisplayText);
        writer.WriteBoolean(
            "titleOrAffiliationConfirmationRequired",
            speaker.TitleOrAffiliationConfirmationRequired);
        writer.WritePropertyName("overlapDuration");
        WriteRationalTime(writer, speaker.OverlapDuration);
        writer.WriteEndObject();
    }

    private static void WriteCandidate(
        Utf8JsonWriter writer,
        AnalysisBridgeSnapshot snapshot,
        AnalysisCandidateBinding binding)
    {
        AnalysisCandidateView view = binding.GetView(
            snapshot.DecisionHistory,
            snapshot.Topics);
        SuggestionCandidate candidate = binding.Candidate;
        Suggestion suggestion = candidate.Suggestion;
        writer.WriteStartObject();
        writer.WriteString("candidateId", candidate.Id.ToString());
        writer.WriteString("modelRunId", suggestion.ModelRunId.ToString());
        writer.WriteString("producer", GetProducer(candidate.Producer));
        writer.WriteString("suggestionKind", GetSuggestionKind(candidate.Kind));
        writer.WriteString("displayKind", view.DisplayKind);
        writer.WriteString("status", GetSuggestionStatus(view.Display.Status));
        writer.WriteString("freshness", view.Freshness ==
            SuggestionCandidateFreshness.Current ? "current" : "stale");
        writer.WriteBoolean("canAdopt", view.CanAdopt);
        writer.WriteBoolean("canReject", view.CanReject);
        writer.WriteString(
            "sourceDigest",
            view.Display.CurrentSourceDigest.Hex);
        writer.WritePropertyName("source");
        WriteSource(writer, binding.SourceSpan);
        writer.WriteString("reason", suggestion.Reason);
        WriteNullableNumber(writer, "confidence", suggestion.Confidence);
        WriteNullableText(writer, "missingContext", suggestion.MissingContext);
        WriteNullableText(writer, "warning", suggestion.Warning);
        WriteNullableId(
            writer,
            "parentCandidateId",
            candidate.ParentSuggestionId);
        WriteNullableId(
            writer,
            "replacementCandidateId",
            candidate.ReplacementSuggestionId);
        writer.WritePropertyName("decision");
        if (view.Display.Decision is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteDecision(writer, view.Display.Decision);
        }

        writer.WritePropertyName("payload");
        WriteProducerPayload(writer, candidate);
        writer.WriteEndObject();
    }

    private static void WriteDecision(
        Utf8JsonWriter writer,
        SuggestionDecisionRecord decision)
    {
        writer.WriteStartObject();
        writer.WriteString("decisionId", decision.Id.ToString());
        writer.WriteString("candidateId", decision.CandidateId.ToString());
        writer.WriteString("producer", GetProducer(decision.Producer));
        writer.WriteString(
            "suggestionKind",
            GetSuggestionKind(decision.SuggestionKind));
        writer.WriteString(
            "decision",
            decision.Decision switch
            {
                SuggestionDecisionKind.Rejected => "rejected",
                SuggestionDecisionKind.Modified => "modified",
                SuggestionDecisionKind.Adopted => "adopted",
                _ => throw new InvalidOperationException(
                    "The analysis decision kind is invalid."),
            });
        writer.WriteString(
            "statusAfterDecision",
            GetSuggestionStatus(decision.StatusAfterDecision));
        writer.WriteString("sourceDigest", decision.SourceDigest.Hex);
        writer.WriteString("decidedAt", FormatTimestamp(decision.DecidedAt));
        writer.WriteString("reason", decision.Reason);
        WriteNullableId(
            writer,
            "replacementCandidateId",
            decision.ReplacementSuggestionId);
        WriteNullableId(
            writer,
            "resultingTranscriptRevisionId",
            decision.ResultingTranscriptRevisionId);
        writer.WritePropertyName("adoptedReference");
        if (decision.AdoptedReference is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString(
                "candidateId",
                decision.AdoptedReference.SuggestionId.ToString());
            writer.WriteString(
                "sourceSpanId",
                decision.AdoptedReference.Origin.SourceSpanId!.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteProducerPayload(
        Utf8JsonWriter writer,
        SuggestionCandidate candidate)
    {
        switch (candidate)
        {
            case TermSuggestionCandidate term:
                WriteTermPayload(writer, term.Payload);
                break;
            case SilenceSuggestionCandidate silence:
                WriteSilencePayload(writer, silence.Payload);
                break;
            case EditorialPhraseSuggestionCandidate editorial:
                WriteEditorialPayload(writer, editorial.Payload);
                break;
            case RepetitionSuggestionCandidate repetition:
                WriteRepetitionPayload(writer, repetition.Payload);
                break;
            case TopicBoundarySuggestionCandidate boundary:
                WriteTopicBoundaryPayload(writer, boundary.Payload);
                break;
            case AudioReviewSuggestionCandidate audio:
                WriteAudioPayload(writer, audio.Payload);
                break;
            case ContextDeviationSuggestionCandidate context:
                WriteContextPayload(writer, context.Payload);
                break;
            case EmphasisSuggestionCandidate emphasis:
                WriteEmphasisPayload(writer, emphasis.Payload);
                break;
            default:
                throw new InvalidOperationException(
                    "The analysis producer payload is unsupported.");
        }
    }

    private static void WriteTermPayload(
        Utf8JsonWriter writer,
        TermCandidate payload)
    {
        writer.WriteStartObject();
        writer.WriteString("producer", "termCorrection");
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            payload.SourceRevisionDigest.Hex);
        writer.WriteString("sourceSpanId", payload.SourceSpanId.ToString());
        writer.WriteString("tokenId", payload.TokenId.ToString());
        writer.WriteString(
            "termScopeKind",
            payload.TermScope.Kind == TermScopeKind.Project
                ? "project"
                : "series");
        writer.WriteString(
            "termScopeId",
            (payload.TermScope.ProjectId?.ToString() ??
                payload.TermScope.SeriesId?.ToString())!);
        writer.WriteString("termEntryId", payload.TermEntryId.ToString());
        writer.WriteNumber("termEntryVersion", payload.TermEntryVersion);
        writer.WriteString("dictionaryReading", payload.DictionaryReading);
        writer.WriteString("observedText", payload.ObservedText);
        writer.WriteString("proposedText", payload.ProposedText);
        writer.WriteString(
            "matchKind",
            payload.MatchKind switch
            {
                TermCandidateMatchKind.WrittenFormCompatibilityVariant =>
                    "writtenFormCompatibilityVariant",
                TermCandidateMatchKind.WrittenFormCaseVariant =>
                    "writtenFormCaseVariant",
                TermCandidateMatchKind.DictionaryReading => "dictionaryReading",
                _ => throw new InvalidOperationException(
                    "The term match kind is invalid."),
            });
        writer.WriteEndObject();
    }

    private static void WriteSilencePayload(
        Utf8JsonWriter writer,
        SilenceCandidate payload)
    {
        writer.WriteStartObject();
        writer.WriteString("producer", "silence");
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WriteString("thresholdVersion", payload.ThresholdVersion);
        writer.WriteString(
            "analysisStartSample",
            payload.AnalysisStartSample.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "analysisEndSample",
            payload.AnalysisEndSample.ToString(CultureInfo.InvariantCulture));
        writer.WriteNumber("noiseDb", SilenceDetector.NoiseDb);
        writer.WritePropertyName("minimumDuration");
        WriteRationalTime(
            writer,
            new RationalTime(SilenceDetector.MinimumDurationSeconds, 1));
        writer.WriteString(
            "sampleRoundingRule",
            SilenceDetector.SampleRoundingRule);
        writer.WriteBoolean("productionApproved", false);
        writer.WriteBoolean("automaticDeletionAllowed", false);
        writer.WriteEndObject();
    }

    private static void WriteEditorialPayload(
        Utf8JsonWriter writer,
        EditorialPhraseCandidate payload)
    {
        writer.WriteStartObject();
        writer.WriteString("producer", "editorialPhrase");
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            payload.SourceRevisionDigest.Hex);
        writer.WriteString("dictionaryVersion", payload.DictionaryVersion);
        writer.WriteString("matchedPhrase", payload.MatchedPhrase);
        writer.WriteNumber(
            "normalizedSentenceStart",
            payload.NormalizedSentenceStart);
        writer.WriteNumber("normalizedMatchLength", payload.NormalizedMatchLength);
        writer.WriteBoolean(
            "usesUtteranceTimingFallback",
            payload.UsesUtteranceTimingFallback);
        writer.WriteBoolean(
            "usesPartialTokenCoverage",
            payload.UsesPartialTokenCoverage);
        writer.WriteEndObject();
    }

    private static void WriteRepetitionPayload(
        Utf8JsonWriter writer,
        RepetitionSuggestionPayload payload)
    {
        writer.WriteStartObject();
        writer.WriteString("producer", "repetition");
        writer.WriteString("candidateKey", payload.CandidateKey.Hex);
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            payload.SourceRevisionDigest.Hex);
        writer.WriteString("sourceSpanId", payload.SourceSpanId.ToString());
        WriteIdArray(writer, "evidenceTokenIds", payload.EvidenceTokenIds);
        writer.WriteString("ruleVersion", payload.RuleVersion);
        writer.WriteBoolean(
            "automaticDeletionAllowed",
            payload.AutomaticDeletionAllowed);
        writer.WriteBoolean("humanReviewRequired", payload.HumanReviewRequired);
        writer.WriteStartObject("features");
        writer.WriteString("rule", payload.Features.Rule);
        writer.WriteString(
            "comparisonSourceSpanId",
            payload.Features.ComparisonSourceSpanId.ToString());
        WriteIdArray(
            writer,
            "comparisonTokenIds",
            payload.Features.ComparisonTokenIds);
        writer.WriteNumber("matchingTokenCount", payload.Features.MatchingTokenCount);
        writer.WriteBoolean("exactNfkcMatch", payload.Features.ExactNfkcMatch);
        writer.WriteBoolean(
            "structurallyAdjacent",
            payload.Features.StructurallyAdjacent);
        writer.WriteBoolean(
            "sameSpeakerRequired",
            payload.Features.SameSpeakerRequired);
        writer.WriteBoolean(
            "candidateIsLaterOccurrence",
            payload.Features.CandidateIsLaterOccurrence);
        writer.WriteBoolean(
            "retentionChoiceRequired",
            payload.Features.RetentionChoiceRequired);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTopicBoundaryPayload(
        Utf8JsonWriter writer,
        TopicBoundaryCandidate payload)
    {
        TopicBoundaryFeatures features = payload.Features;
        writer.WriteStartObject();
        writer.WriteString("producer", "topicBoundary");
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            payload.SourceRevisionDigest.Hex);
        writer.WritePropertyName("suggestedBoundary");
        WriteRationalTime(writer, payload.SuggestedBoundary);
        writer.WriteString("ruleVersion", payload.RuleVersion);
        writer.WriteStartObject("features");
        writer.WriteString(
            "previousUtteranceId",
            features.PreviousUtteranceId.ToString());
        writer.WriteString(
            "currentUtteranceId",
            features.CurrentUtteranceId.ToString());
        writer.WriteString(
            "previousSourceSpanId",
            features.PreviousSourceSpanId.ToString());
        writer.WriteString(
            "currentSourceSpanId",
            features.CurrentSourceSpanId.ToString());
        writer.WriteString(
            "previousSpeakerId",
            features.PreviousSpeakerId.ToString());
        writer.WriteString(
            "currentSpeakerId",
            features.CurrentSpeakerId.ToString());
        writer.WritePropertyName("previousRange");
        WriteTimeRange(writer, features.PreviousRange);
        writer.WritePropertyName("currentRange");
        WriteTimeRange(writer, features.CurrentRange);
        writer.WritePropertyName("observedGap");
        WriteRationalTime(writer, features.ObservedGap);
        writer.WriteBoolean("speakerChanged", features.SpeakerChanged);
        writer.WriteBoolean(
            "structurallyAdjacent",
            features.StructurallyAdjacent);
        writer.WriteBoolean(
            "semanticClassificationPerformed",
            features.SemanticClassificationPerformed);
        writer.WriteBoolean(
            "approvedPauseOrSimilarityThresholdApplied",
            features.ApprovedPauseOrSimilarityThresholdApplied);
        writer.WriteBoolean(
            "optionalShotBoundaryUsed",
            features.OptionalShotBoundaryUsed);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteAudioPayload(
        Utf8JsonWriter writer,
        AudioReviewCandidate payload)
    {
        writer.WriteStartObject();
        writer.WriteString("producer", "audioReview");
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WriteString(
            "eventKind",
            payload.Kind switch
            {
                AudioReviewEventKind.Cough => "cough",
                AudioReviewEventKind.Noise => "noise",
                AudioReviewEventKind.Wait => "wait",
                AudioReviewEventKind.RecordingQuality => "recordingQuality",
                _ => throw new InvalidOperationException(
                    "The audio review kind is invalid."),
            });
        writer.WriteStartArray("evidenceMethods");
        foreach (AudioReviewEvidenceMethod method in payload.EvidenceMethods)
        {
            writer.WriteStringValue(method switch
            {
                AudioReviewEvidenceMethod.ManualListening => "manualListening",
                AudioReviewEvidenceMethod.FfmpegSilence => "ffmpegSilence",
                AudioReviewEvidenceMethod.WaveformLowEnergy => "waveformLowEnergy",
                AudioReviewEvidenceMethod.WaveformClipping => "waveformClipping",
                AudioReviewEvidenceMethod.YamnetHeld => "yamnetHeld",
                AudioReviewEvidenceMethod.SileroVadBenchmarkOnly =>
                    "sileroVadBenchmarkOnly",
                _ => throw new InvalidOperationException(
                    "The audio review method is invalid."),
            });
        }

        writer.WriteEndArray();
        writer.WriteString(
            "analysisStartSample",
            payload.AnalysisStartSample.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "analysisEndSample",
            payload.AnalysisEndSample.ToString(CultureInfo.InvariantCulture));
        writer.WriteBoolean(
            "manualListeningConfirmed",
            payload.ManualListeningConfirmed);
        writer.WriteBoolean("technicalWarningOnly", payload.TechnicalWarningOnly);
        writer.WriteBoolean(
            "automaticDeletionAllowed",
            payload.AutomaticDeletionAllowed);
        writer.WriteBoolean(
            "explicitHumanDecisionRequired",
            payload.ExplicitHumanDecisionRequired);
        writer.WriteEndObject();
    }

    private static void WriteContextPayload(
        Utf8JsonWriter writer,
        ContextDeviationCandidate payload)
    {
        ContextDeviationFeatures features = payload.Features;
        writer.WriteStartObject();
        writer.WriteString("producer", "contextDeviation");
        writer.WritePropertyName("detection");
        WriteDetection(writer, payload.Detection);
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            payload.SourceRevisionDigest.Hex);
        writer.WriteString("ruleVersion", payload.RuleVersion);
        writer.WriteStartObject("features");
        writer.WriteString(
            "position",
            features.Position switch
            {
                ContextDeviationPosition.LeadingBeforeFirstTopic =>
                    "leadingBeforeFirstTopic",
                ContextDeviationPosition.BetweenTopicRanges =>
                    "betweenTopicRanges",
                ContextDeviationPosition.TrailingAfterLastTopic =>
                    "trailingAfterLastTopic",
                _ => throw new InvalidOperationException(
                    "The context position is invalid."),
            });
        writer.WriteString("utteranceId", features.UtteranceId.ToString());
        writer.WriteString("sourceSpanId", features.SourceSpanId.ToString());
        writer.WriteString("speakerId", features.SpeakerId.ToString());
        writer.WritePropertyName("utteranceRange");
        WriteTimeRange(writer, features.UtteranceRange);
        WriteNullableId(
            writer,
            "previousTopicId",
            features.PreviousTopicRangeId);
        WriteNullableId(writer, "nextTopicId", features.NextTopicRangeId);
        writer.WritePropertyName("distanceToNearestTopicRange");
        WriteRationalTime(writer, features.DistanceToNearestTopicRange);
        writer.WriteBoolean(
            "fullyOutsideExplicitTopicRanges",
            features.FullyOutsideExplicitTopicRanges);
        writer.WriteBoolean(
            "semanticDistanceComputed",
            features.SemanticDistanceComputed);
        writer.WriteBoolean(
            "timeThresholdApplied",
            features.TimeThresholdApplied);
        writer.WriteBoolean(
            "automaticDeletionAllowed",
            features.AutomaticDeletionAllowed);
        writer.WriteEndObject();
        writer.WriteBoolean(
            "explicitHumanDecisionRequired",
            payload.ExplicitHumanDecisionRequired);
        writer.WriteEndObject();
    }

    private static void WriteEmphasisPayload(
        Utf8JsonWriter writer,
        EmphasisCandidate payload)
    {
        writer.WriteStartObject();
        writer.WriteString("producer", "emphasis");
        writer.WriteString("sourceRevisionId", payload.SourceRevisionId.ToString());
        writer.WriteString(
            "sourceRevisionDigest",
            payload.SourceRevisionDigest.Hex);
        writer.WriteString(
            "topicMetadataRevisionId",
            payload.TopicMetadataRevisionId.ToString());
        writer.WriteString("ruleVersion", payload.RuleVersion);
        writer.WriteString("text", payload.Text);
        writer.WriteString(
            "textSource",
            payload.TextSource == EmphasisTextSourceKind.OriginalTranscript
                ? "originalTranscript"
                : "manualSummary");
        writer.WriteBoolean("isExactOriginalText", payload.IsExactOriginalText);
        writer.WriteStartArray("reasons");
        foreach (EmphasisReasonKind reason in payload.Reasons)
        {
            writer.WriteStringValue(reason switch
            {
                EmphasisReasonKind.ImportantStatement => "importantStatement",
                EmphasisReasonKind.Number => "number",
                EmphasisReasonKind.ProductName => "productName",
                EmphasisReasonKind.PublicationCaution => "publicationCaution",
                EmphasisReasonKind.Keyword => "keyword",
                EmphasisReasonKind.AnswerAfterExplicitQuestion =>
                    "answerAfterExplicitQuestion",
                EmphasisReasonKind.TopicTransitionSummary =>
                    "topicTransitionSummary",
                EmphasisReasonKind.SpeechSegment => "speechSegment",
                _ => throw new InvalidOperationException(
                    "The emphasis reason is invalid."),
            });
        }

        writer.WriteEndArray();
        writer.WriteStartArray("termEvidence");
        foreach (EmphasisTermEvidence term in payload.TermEvidence)
        {
            writer.WriteStartObject();
            writer.WriteString("termEntryId", term.TermEntryId.ToString());
            writer.WriteNumber("termEntryVersion", term.TermEntryVersion);
            writer.WriteString("writtenForm", term.WrittenForm);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean(
            "automaticAdoptionAllowed",
            payload.AutomaticAdoptionAllowed);
        writer.WriteEndObject();
    }

    private static void WriteDetection(
        Utf8JsonWriter writer,
        Detection detection)
    {
        writer.WriteStartObject();
        writer.WriteString("detectionId", detection.Id.ToString());
        writer.WriteString(
            "kind",
            detection.Kind switch
            {
                DetectionKind.Silence => "silence",
                DetectionKind.Repetition => "repetition",
                DetectionKind.PreparationConversation =>
                    "preparationConversation",
                DetectionKind.RecordingProblem => "recordingProblem",
                DetectionKind.ContextDeviation => "contextDeviation",
                DetectionKind.AudioEvent => "audioEvent",
                DetectionKind.EditorialInstruction => "editorialInstruction",
                DetectionKind.TopicBoundary => "topicBoundary",
                _ => throw new InvalidOperationException(
                    "The detection kind is invalid."),
            });
        writer.WriteString("description", detection.Description);
        WriteNullableNumber(writer, "confidence", detection.Confidence);
        writer.WriteEndObject();
    }

    private static void WriteSensitivity(
        Utf8JsonWriter writer,
        AnalysisBridgeSnapshot snapshot)
    {
        TextSensitivityScanResult sensitivity = snapshot.Sensitivity;
        writer.WriteStartObject("sensitivity");
        writer.WriteString("revisionId", sensitivity.RevisionId.ToString());
        writer.WriteString("ruleSetVersion", sensitivity.RuleSetVersion);
        writer.WriteBoolean("isComplete", sensitivity.IsComplete);
        writer.WriteBoolean(
            "requiresExternalOutputWarning",
            sensitivity.RequiresExternalOutputWarning);
        writer.WriteStartArray("findings");
        foreach (TextSensitivityFinding finding in sensitivity.Findings)
        {
            SourceSpan sourceSpan = snapshot.SourceSpans.Single(span =>
                span.Id == finding.SourceSpanId);
            writer.WriteStartObject();
            writer.WriteString("utteranceId", finding.UtteranceId.ToString());
            writer.WriteString("sourceSpanId", finding.SourceSpanId.ToString());
            writer.WriteString(
                "kind",
                finding.Kind switch
                {
                    TextSensitivityFindingKind.EmailAddress => "emailAddress",
                    TextSensitivityFindingKind.InternalNetworkLocation =>
                        "internalNetworkLocation",
                    TextSensitivityFindingKind.CredentialPattern =>
                        "credentialPattern",
                    TextSensitivityFindingKind.DictionaryTerm => "dictionaryTerm",
                    _ => throw new InvalidOperationException(
                        "The sensitivity kind is invalid."),
                });
            writer.WriteString("reasonCode", finding.ReasonCode);
            writer.WritePropertyName("sourceRange");
            WriteTimeRange(writer, sourceSpan.Range);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStagedChanges(
        Utf8JsonWriter writer,
        IReadOnlyList<AnalysisStagedChange> changes)
    {
        writer.WriteStartArray("stagedChanges");
        foreach (AnalysisStagedChange change in changes)
        {
            writer.WriteStartObject();
            switch (change)
            {
                case AnalysisStagedTranscriptRevisionChange transcript:
                    writer.WriteString("kind", "transcriptRevision");
                    writer.WriteString(
                        "candidateId",
                        transcript.CandidateId.ToString());
                    writer.WriteString(
                        "sourceRevisionId",
                        transcript.SourceRevisionId.ToString());
                    writer.WriteString(
                        "resultingRevisionId",
                        transcript.ResultingRevisionId.ToString());
                    break;
                case AnalysisStagedEditOperationChange edit:
                    writer.WriteString("kind", "editOperation");
                    writer.WriteString("candidateId", edit.CandidateId.ToString());
                    writer.WritePropertyName("operation");
                    WriteEditOperation(writer, edit.Operation);
                    break;
                case AnalysisStagedCueChange cue:
                    writer.WriteString("kind", "cue");
                    writer.WriteString("candidateId", cue.CandidateId.ToString());
                    writer.WritePropertyName("cue");
                    WriteCue(writer, cue.Cue);
                    break;
                case AnalysisStagedTopicMetadataChange topic:
                    writer.WriteString("kind", "topicMetadataRevision");
                    writer.WriteString("topicId", topic.TopicId.ToString());
                    writer.WriteString(
                        "revisionId",
                        topic.Revision.Id.ToString());
                    writer.WriteString(
                        "parentRevisionId",
                        topic.Revision.ParentRevisionId!.ToString());
                    writer.WriteString(
                        "field",
                        GetMetadataField(topic.Revision.ChangedField));
                    break;
                default:
                    throw new InvalidOperationException(
                        "The staged analysis change is unsupported.");
            }

            writer.WriteBoolean("persisted", change.Persisted);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteEditOperation(
        Utf8JsonWriter writer,
        EditOperation operation)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "kind",
            operation.Kind switch
            {
                EditOperationKind.Delete => "delete",
                EditOperationKind.Split => "split",
                _ => throw new InvalidOperationException(
                    "Only staged delete and split operations are supported."),
            });
        writer.WriteString("elementId", operation.ElementId.ToString());
        writer.WritePropertyName("splitOffset");
        if (operation.SplitOffset is RationalTime splitOffset)
        {
            WriteRationalTime(writer, splitOffset);
        }
        else
        {
            writer.WriteNullValue();
        }

        WriteNullableId(writer, "rightElementId", operation.RightElementId);
        writer.WriteEndObject();
    }

    private static void WriteCue(Utf8JsonWriter writer, Cue cue)
    {
        writer.WriteStartObject();
        writer.WriteString("cueId", cue.Id.ToString());
        writer.WriteString("kind", "graphic");
        writer.WritePropertyName("outputRange");
        WriteTimeRange(writer, cue.Range);
        WriteNullableText(writer, "regionName", cue.RegionName);
        writer.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter writer, SourceSpan sourceSpan)
    {
        writer.WriteStartObject();
        writer.WriteString("sourceSpanId", sourceSpan.Id.ToString());
        writer.WritePropertyName("range");
        WriteTimeRange(writer, sourceSpan.Range);
        writer.WriteEndObject();
    }

    private static void WriteTimeRange(Utf8JsonWriter writer, TimeRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WriteRationalTime(writer, range.Start);
        writer.WritePropertyName("duration");
        WriteRationalTime(writer, range.Duration);
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteIdArray<TTag>(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<EntityId<TTag>> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (EntityId<TTag> value in values)
        {
            writer.WriteStringValue(value.ToString());
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableId<TTag>(
        Utf8JsonWriter writer,
        string propertyName,
        EntityId<TTag>? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.ToString());
        }
    }

    private static void WriteNullableText(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        double? value)
    {
        if (value is double number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is DateTimeOffset timestamp)
        {
            writer.WriteString(propertyName, FormatTimestamp(timestamp));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string GetStatusText(AnalysisBridgeStatus status) =>
        status switch
        {
            AnalysisBridgeStatus.Available => "available",
            AnalysisBridgeStatus.Forbidden => "forbidden",
            AnalysisBridgeStatus.Unavailable => "unavailable",
            AnalysisBridgeStatus.StaleBase => "staleBase",
            AnalysisBridgeStatus.Conflict => "conflict",
            AnalysisBridgeStatus.NoChange => "noChange",
            AnalysisBridgeStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static string GetProducer(SuggestionProducerKind producer) =>
        producer switch
        {
            SuggestionProducerKind.TermCorrection => "termCorrection",
            SuggestionProducerKind.Silence => "silence",
            SuggestionProducerKind.EditorialPhrase => "editorialPhrase",
            SuggestionProducerKind.Repetition => "repetition",
            SuggestionProducerKind.TopicBoundary => "topicBoundary",
            SuggestionProducerKind.AudioReview => "audioReview",
            SuggestionProducerKind.ContextDeviation => "contextDeviation",
            SuggestionProducerKind.Emphasis => "emphasis",
            _ => throw new ArgumentOutOfRangeException(nameof(producer)),
        };

    private static string GetSuggestionKind(SuggestionKind kind) =>
        kind switch
        {
            SuggestionKind.DeleteRange => "deleteRange",
            SuggestionKind.TopicBoundary => "topicBoundary",
            SuggestionKind.Highlight => "highlight",
            SuggestionKind.TermCorrection => "termCorrection",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string GetSuggestionStatus(SuggestionStatus status) =>
        status switch
        {
            SuggestionStatus.Proposed => "proposed",
            SuggestionStatus.Reviewed => "reviewed",
            SuggestionStatus.Rejected => "rejected",
            SuggestionStatus.Adopted => "adopted",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static string GetSpeakerFormat(SpeakerDisplayFormat format) =>
        format switch
        {
            SpeakerDisplayFormat.FullName => "fullName",
            SpeakerDisplayFormat.Surname => "surname",
            SpeakerDisplayFormat.Role => "role",
            SpeakerDisplayFormat.Hidden => "hidden",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static string GetMetadataOrigin(TopicMetadataValueOrigin origin) =>
        origin switch
        {
            TopicMetadataValueOrigin.Unset => "unset",
            TopicMetadataValueOrigin.DeterministicPlaceholder =>
                "deterministicPlaceholder",
            TopicMetadataValueOrigin.ProjectSnapshot => "projectSnapshot",
            TopicMetadataValueOrigin.DeterministicTranscriptOverlap =>
                "deterministicTranscriptOverlap",
            TopicMetadataValueOrigin.ManualEditorial => "manualEditorial",
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

    private static string GetMetadataField(TopicMetadataField field) =>
        field switch
        {
            TopicMetadataField.Initial => "initial",
            TopicMetadataField.Title => "title",
            TopicMetadataField.Summary => "summary",
            TopicMetadataField.MainSpeaker => "mainSpeaker",
            TopicMetadataField.Keywords => "keywords",
            TopicMetadataField.ImportantStatements => "importantStatements",
            TopicMetadataField.Audience => "audience",
            TopicMetadataField.PublicationCautions => "publicationCautions",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
}