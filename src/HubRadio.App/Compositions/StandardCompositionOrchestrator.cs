using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.App.Projects;

namespace HubRadio.App.Compositions;

public enum StandardCompositionOrchestrationFailureReason
{
    ParentMismatch,
    WorkingCopyMismatch,
    ProfileMismatch,
    SourceEvidenceMismatch,
}

public sealed class StandardCompositionOrchestrationException : Exception
{
    internal StandardCompositionOrchestrationException(
        StandardCompositionOrchestrationFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public StandardCompositionOrchestrationFailureReason Reason { get; }

    private static string GetStableMessage(
        StandardCompositionOrchestrationFailureReason reason) =>
        reason switch
        {
            StandardCompositionOrchestrationFailureReason.ParentMismatch =>
                "The standard-composition parent is inconsistent.",
            StandardCompositionOrchestrationFailureReason.WorkingCopyMismatch =>
                "The standard-composition working copy does not exactly match its parent.",
            StandardCompositionOrchestrationFailureReason.ProfileMismatch =>
                "The standard-composition measured or derived render profile is inconsistent.",
            StandardCompositionOrchestrationFailureReason.SourceEvidenceMismatch =>
                "The standard-composition source-only evidence is inconsistent.",
            _ => "The standard composition could not be prepared.",
        };
}

public sealed class StandardCompositionOrchestrationRequest
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public StandardCompositionOrchestrationRequest(
        CompositionVersion parent,
        WorkingCopy workingCopy,
        int expectedWorkingCopyRevision,
        StandardProjectStoreState projectStoreState,
        DefaultSettingsProfile settingsProfile,
        DefaultSettingsSnapshot settings,
        ReadOnlyMemory<byte> measuredLandscapeProfileJson,
        ReadOnlyMemory<byte> measuredVerticalProfileJson,
        LongFormCompositionRequest longFormRequest,
        EntityId<TimelineMapIdTag> parentTimelineMapId,
        SourceDisplayGeometry sourceDisplayGeometry,
        RationalNumber sourceFrameRate,
        ContentDigest toolDigest,
        ContentDigest modelDigest,
        MusicPlanningResult musicPlanning,
        IReadOnlyList<TopicMetadataRevision> topicMetadataRevisions,
        IReadOnlyList<EmphasisSuggestionCandidate> candidates,
        IReadOnlyList<HighlightSuggestionAdoptionResult> adoptedHighlights,
        string actorSid,
        DateTimeOffset createdAt,
        string changeReason,
        StandardThumbnailEvidence? thumbnailEvidence = null)
    {
        ValidateMeasuredProfile(
            measuredLandscapeProfileJson,
            nameof(measuredLandscapeProfileJson));
        ValidateMeasuredProfile(
            measuredVerticalProfileJson,
            nameof(measuredVerticalProfileJson));
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        WorkingCopy = workingCopy
            ?? throw new ArgumentNullException(nameof(workingCopy));
        ProjectStoreState = projectStoreState
            ?? throw new ArgumentNullException(nameof(projectStoreState));
        SettingsProfile = settingsProfile
            ?? throw new ArgumentNullException(nameof(settingsProfile));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        LongFormRequest = longFormRequest
            ?? throw new ArgumentNullException(nameof(longFormRequest));
        ParentTimelineMapId = parentTimelineMapId
            ?? throw new ArgumentNullException(nameof(parentTimelineMapId));
        SourceDisplayGeometry = sourceDisplayGeometry
            ?? throw new ArgumentNullException(nameof(sourceDisplayGeometry));
        if (sourceFrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFrameRate));
        }

        SourceFrameRate = sourceFrameRate;
        ToolDigest = toolDigest
            ?? throw new ArgumentNullException(nameof(toolDigest));
        ModelDigest = modelDigest
            ?? throw new ArgumentNullException(nameof(modelDigest));
        MusicPlanning = musicPlanning
            ?? throw new ArgumentNullException(nameof(musicPlanning));
        TopicMetadataRevisions = Copy(
            topicMetadataRevisions,
            nameof(topicMetadataRevisions));
        Candidates = Copy(candidates, nameof(candidates));
        AdoptedHighlights = Copy(
            adoptedHighlights,
            nameof(adoptedHighlights));
        ThumbnailEvidence = thumbnailEvidence;
        ActorSid = ValidateEvidenceText(actorSid, nameof(actorSid));
        ChangeReason = ValidateEvidenceText(
            changeReason,
            nameof(changeReason));
        MeasuredLandscapeProfileJson = measuredLandscapeProfileJson.ToArray();
        MeasuredVerticalProfileJson = measuredVerticalProfileJson.ToArray();
        ExpectedWorkingCopyRevision = expectedWorkingCopyRevision;
        CreatedAt = createdAt;
    }

    public CompositionVersion Parent { get; }

    public WorkingCopy WorkingCopy { get; }

    public int ExpectedWorkingCopyRevision { get; }

    public StandardProjectStoreState ProjectStoreState { get; }

    public DefaultSettingsProfile SettingsProfile { get; }

    public DefaultSettingsSnapshot Settings { get; }

    public ReadOnlyMemory<byte> MeasuredLandscapeProfileJson { get; }

    public ReadOnlyMemory<byte> MeasuredVerticalProfileJson { get; }

    public LongFormCompositionRequest LongFormRequest { get; }

    public EntityId<TimelineMapIdTag> ParentTimelineMapId { get; }

    public SourceDisplayGeometry SourceDisplayGeometry { get; }

    public RationalNumber SourceFrameRate { get; }

    public ContentDigest ToolDigest { get; }

    public ContentDigest ModelDigest { get; }

    public MusicPlanningResult MusicPlanning { get; }

    public IReadOnlyList<TopicMetadataRevision> TopicMetadataRevisions { get; }

    public IReadOnlyList<EmphasisSuggestionCandidate> Candidates { get; }

    public IReadOnlyList<HighlightSuggestionAdoptionResult> AdoptedHighlights
    { get; }

    public StandardThumbnailEvidence? ThumbnailEvidence { get; }

    public string ActorSid { get; }

    public DateTimeOffset CreatedAt { get; }

    public string ChangeReason { get; }

    private static IReadOnlyList<T> Copy<T>(
        IReadOnlyList<T> values,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        int count = values.Count;
        StandardCompositionResourceLimits.ValidateEvidenceCollectionCount(
            count,
            parameterName);
        var copied = new T[count];
        for (int index = 0; index < copied.Length; index++)
        {
            copied[index] = values[index] ?? throw new ArgumentException(
                "Standard-composition inputs must not contain null elements.",
                parameterName);
        }

        return Array.AsReadOnly(copied);
    }

    private static string ValidateEvidenceText(
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            if (value.Length
                    > CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes
                || string.IsNullOrWhiteSpace(value)
                || char.IsWhiteSpace(value[0])
                || char.IsWhiteSpace(value[^1])
                || StrictUtf8.GetByteCount(value)
                    > CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes)
            {
                throw new ArgumentException(
                    "Standard-composition evidence text is invalid or exceeds its UTF-8 resource-safety envelope.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Standard-composition evidence text must be valid Unicode.",
                parameterName,
                exception);
        }

        return value;
    }

    private static void ValidateMeasuredProfile(
        ReadOnlyMemory<byte> profileJson,
        string parameterName)
    {
        if (profileJson.IsEmpty
            || profileJson.Length
                > BundledRenderProfileLoader.MaximumUtf8JsonBytes)
        {
            throw new ArgumentException(
                "Measured profile evidence is empty or exceeds its UTF-8 resource-safety envelope.",
                parameterName);
        }
    }
}

internal static class StandardCompositionResourceLimits
{
    private const int MinimumCanonicalEvidenceBytesPerCollectionItem = 256;

    internal const int MaximumEvidenceCollectionItems =
        CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes
        / MinimumCanonicalEvidenceBytesPerCollectionItem;

    internal static void ValidateEvidenceCollectionCount(
        int count,
        string parameterName)
    {
        if (count < 0 || count > MaximumEvidenceCollectionItems)
        {
            throw new ArgumentException(
                "A standard-composition evidence collection exceeds its resource-safety envelope.",
                parameterName);
        }
    }
}

public sealed class StandardCompositionPreparation
{
    internal StandardCompositionPreparation(
        ContentDigest applicationDigest,
        StandardCompositionPlan plan,
        CompositionVersion child,
        WorkingCopy preparedWorkingCopy,
        int expectedWorkingCopyRevision,
        TimelineMap parentTimelineMap,
        TimelineMap childTimelineMap)
    {
        ApplicationDigest = applicationDigest;
        Plan = plan;
        Child = child;
        PreparedWorkingCopy = preparedWorkingCopy;
        ExpectedWorkingCopyRevision = expectedWorkingCopyRevision;
        ParentTimelineMap = parentTimelineMap;
        ChildTimelineMap = childTimelineMap;
    }

    public ContentDigest ApplicationDigest { get; }

    public StandardCompositionPlan Plan { get; }

    public CompositionVersion Child { get; }

    public WorkingCopy PreparedWorkingCopy { get; }

    public int ExpectedWorkingCopyRevision { get; }

    public TimelineMap ParentTimelineMap { get; }

    public TimelineMap ChildTimelineMap { get; }
}

public sealed class StandardCompositionApplication
{
    internal StandardCompositionApplication(
        StandardCompositionPreparation preparation,
        CheckpointResult checkpoint)
    {
        Preparation = preparation;
        Checkpoint = checkpoint;
    }

    public StandardCompositionPreparation Preparation { get; }

    public CheckpointResult Checkpoint { get; }
}

public static class StandardCompositionOrchestrator
{
    internal const string EvidenceSchemaVersion =
        "hubradio.standard-composition-evidence.v1";

    public static StandardCompositionPreparation Prepare(
        StandardCompositionOrchestrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateParentAndWorkingCopy(request);

        BundledRenderProfileDefinition measuredLandscape;
        BundledRenderProfileDefinition measuredVertical;
        BundledRenderProfileDefinition landscapeProfile;
        BundledRenderProfileDefinition verticalProfile;
        try
        {
            measuredLandscape = BundledRenderProfileLoader.Load(
                request.MeasuredLandscapeProfileJson.Span);
            measuredVertical = BundledRenderProfileLoader.Load(
                request.MeasuredVerticalProfileJson.Span);
            landscapeProfile = StandardRenderProfileFactory.CreateLandscape(
                request.SettingsProfile,
                request.Settings,
                measuredLandscape,
                request.SourceFrameRate);
            verticalProfile = StandardRenderProfileFactory.CreateVerticalShort(
                request.SettingsProfile,
                request.Settings,
                measuredVertical,
                request.SourceFrameRate);
        }
        catch (Exception exception)
            when (exception is ArgumentException or JsonException)
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason.ProfileMismatch);
        }

        if (StandardCompositionCanonical.ComputeProfileBindingDigest(
                request.LongFormRequest.Profile)
            != StandardCompositionCanonical.ComputeProfileBindingDigest(
                landscapeProfile))
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason.ProfileMismatch);
        }

        LongFormCompositionRequest replayRequest = WithSafeDefaults(
            request,
            landscapeProfile,
            cancellationToken);
        LongFormCompositionPlan longForm = LongFormComposer.Compose(
            replayRequest,
            cancellationToken);
        TimelineMap parentTimelineMap;
        try
        {
            parentTimelineMap = TimelineMapBuilder.Build(
                request.ParentTimelineMapId,
                request.Parent);
        }
        catch (ArgumentException)
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason.ParentMismatch);
        }

        StandardCompositionPlan plan = StandardCompositionPlanner.Create(
            new StandardCompositionPlanningRequest(
                request.Parent,
                parentTimelineMap,
                longForm,
                verticalProfile,
                request.SourceDisplayGeometry,
                request.SourceFrameRate,
                request.ProjectStoreState,
                request.SettingsProfile,
                request.Settings,
                request.ToolDigest,
                request.ModelDigest,
                request.MusicPlanning,
                request.TopicMetadataRevisions,
                request.Candidates,
                request.AdoptedHighlights,
                request.ThumbnailEvidence),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        ContentDigest applicationDigest =
            StandardCompositionCanonical.ComputeDigest(
                "hubradio.standard-composition-application.v1",
                [
                    request.Parent.Id.ToString(),
                    request.Parent.ContentDigest.Hex,
                    plan.Id.ToString(),
                    plan.PlanDigest.Hex,
                    request.WorkingCopy.Id.ToString(),
                    request.WorkingCopy.ProjectId.ToString(),
                    request.WorkingCopy.BaseVersionId?.ToString() ?? string.Empty,
                    request.WorkingCopy.CreatedAt.ToUniversalTime().ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    request.WorkingCopy.UpdatedAt.ToUniversalTime().ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    request.ExpectedWorkingCopyRevision.ToString(
                        CultureInfo.InvariantCulture),
                    request.ActorSid,
                    request.CreatedAt.ToUniversalTime().ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    request.ChangeReason,
                ]);
        EntityId<CompositionVersionIdTag> childId =
            StandardCompositionCanonical.DeterministicId<
                CompositionVersionIdTag>(applicationDigest, "child");
        var childWithoutEvidence = new CompositionVersion(
            childId,
            request.Parent.ProjectId,
            request.Parent.Id,
            request.ActorSid,
            request.CreatedAt,
            request.ChangeReason,
            request.Parent.ContentDigest,
            request.Parent.Elements);
        TimelineMap childTimelineMap = TimelineMapBuilder.Build(
            StandardCompositionCanonical.DeterministicId<TimelineMapIdTag>(
                applicationDigest,
                "child-timeline-map"),
            childWithoutEvidence);
        string evidenceJson = StandardCompositionEvidenceCodec.WriteEvidence(
            request,
            replayRequest,
            plan,
            applicationDigest,
            childWithoutEvidence,
            parentTimelineMap,
            childTimelineMap);
        CompositionEvidenceEnvelope envelope = CompositionEvidenceEnvelope.Create(
            CompositionEvidenceEnvelope.StandardCompositionPlanKind,
            evidenceJson);
        var child = new CompositionVersion(
            childWithoutEvidence.Id,
            childWithoutEvidence.ProjectId,
            childWithoutEvidence.ParentVersionId,
            childWithoutEvidence.CreatedByActorId,
            childWithoutEvidence.CreatedAt,
            childWithoutEvidence.ChangeReason,
            childWithoutEvidence.ContentDigest,
            childWithoutEvidence.Elements,
            envelope);
        var preparedWorkingCopy = new WorkingCopy(
            request.WorkingCopy.Id,
            request.WorkingCopy.ProjectId,
            request.Parent.Id,
            request.WorkingCopy.CreatedAt,
            request.WorkingCopy.UpdatedAt,
            request.Parent.Elements);
        return new StandardCompositionPreparation(
            applicationDigest,
            plan,
            child,
            preparedWorkingCopy,
            request.ExpectedWorkingCopyRevision,
            parentTimelineMap,
            childTimelineMap);
    }

    public static StandardCompositionApplication Apply(
        VersionService versionService,
        StandardCompositionOrchestrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionService);
        cancellationToken.ThrowIfCancellationRequested();
        versionService.ValidateStoredEvidenceParent(request.Parent);
        cancellationToken.ThrowIfCancellationRequested();
        StandardCompositionPreparation preparation = Prepare(
            request,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        CheckpointResult checkpoint;
        try
        {
            ValidatedStandardCompositionEvidence evidence =
                StandardCompositionEvidenceCodec.ValidateForCheckpoint(
                    preparation.Child,
                    request.Parent);
            checkpoint = versionService.CheckpointStandardComposition(
                request.WorkingCopy,
                request.Parent,
                request.LongFormRequest.Project,
                request.ProjectStoreState,
                preparation.Child.Elements,
                preparation.ExpectedWorkingCopyRevision,
                preparation.Child.Id,
                request.ActorSid,
                request.CreatedAt,
                request.ChangeReason,
                preparation.Child.ContentDigest,
                evidence,
                cancellationToken);
        }
        catch (StoreConcurrencyException exception)
            when (string.Equals(
                exception.EntityKind,
                "working copy",
                StringComparison.Ordinal))
        {
            WorkingCopy expectedWorkingCopy = CreateCheckpointWorkingCopy(
                preparation,
                request.CreatedAt);
            if (!versionService.TryResolveExactCheckpoint(
                    preparation.Child,
                    expectedWorkingCopy,
                    preparation.ExpectedWorkingCopyRevision,
                    out CheckpointResult? existing)
                || existing is null)
            {
                throw;
            }

            checkpoint = existing;
        }

        if (checkpoint.Version.Id != preparation.Child.Id
            || checkpoint.Version.ParentVersionId != request.Parent.Id
            || checkpoint.Version.ContentDigest
                != preparation.Child.ContentDigest
            || checkpoint.Version.Evidence?.ContentDigest
                != preparation.Child.Evidence?.ContentDigest
            || !checkpoint.Version.Elements.SequenceEqual(
                preparation.Child.Elements))
        {
            throw new InvalidOperationException(
                "The stored standard-composition child does not match its validated preparation.");
        }

        return new StandardCompositionApplication(preparation, checkpoint);
    }

    private static void ValidateParentAndWorkingCopy(
        StandardCompositionOrchestrationRequest request)
    {
        ContentDigest actualParentDigest = ContentDigest.Compute(
            CompositionSnapshotJson.WriteElementsObjectToUtf8(
                request.Parent.Elements));
        if (actualParentDigest != request.Parent.ContentDigest
            || request.Parent.ProjectId != request.LongFormRequest.Project.Id
            || request.Parent.Id == request.Parent.ParentVersionId)
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason.ParentMismatch);
        }

        if (request.ExpectedWorkingCopyRevision < 0
            || request.WorkingCopy.ProjectId != request.Parent.ProjectId
            || request.WorkingCopy.BaseVersionId != request.Parent.Id
            || request.WorkingCopy.Elements.Count != request.Parent.Elements.Count
            || !request.WorkingCopy.Elements.SequenceEqual(
                request.Parent.Elements)
            || request.CreatedAt < request.WorkingCopy.UpdatedAt)
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason.WorkingCopyMismatch);
        }
    }

    private static LongFormCompositionRequest WithSafeDefaults(
        StandardCompositionOrchestrationRequest orchestration,
        BundledRenderProfileDefinition profile,
        CancellationToken cancellationToken)
    {
        LongFormCompositionRequest request = orchestration.LongFormRequest;
        (Asset sourceAsset, SourceSpan[] sourceSpans) =
            ResolveSourceOnlyEvidence(orchestration, cancellationToken);
        string sourceTitle;
        try
        {
            sourceTitle = SourceOnlyProjectCreationContract.DeriveContentName(
                sourceAsset.FileName);
        }
        catch (SourceOnlyProjectCreationException)
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason
                    .SourceEvidenceMismatch);
        }

        if (!string.Equals(
                sourceTitle,
                request.Project.ContentName,
                StringComparison.Ordinal))
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason
                    .SourceEvidenceMismatch);
        }

        TimeRange bodyRange = new(
            default,
            orchestration.Parent.Elements[^1].OutputRange.End);
        OriginRef[] bodyOrigins = sourceSpans
            .Select(static span => OriginRef.FromSourceSpan(span.Id))
            .Prepend(OriginRef.FromAsset(sourceAsset.Id))
            .ToArray();
        var title = new LongFormElementInput(
            request.EpisodeTitle.Id,
            LongFormElementKind.EpisodeTitle,
            enabled: true,
            sourceTitle,
            outputRange: null,
            [OriginRef.FromAsset(sourceAsset.Id)],
            assets: []);
        var body = new LongFormElementInput(
            request.Body.Id,
            LongFormElementKind.Body,
            enabled: true,
            text: null,
            bodyRange,
            bodyOrigins,
            [new LongFormAssetReference(
                LongFormAssetRole.BodyMedia,
                sourceAsset.Id,
                sourceAsset.ContentDigest)]);

        return new LongFormCompositionRequest(
            request.PlanId,
            request.Project,
            request.Provenance,
            request.Template,
            profile,
            request.Destination,
            request.SpeakerAssignments,
            request.PreRollMargin,
            Omit(request.Opening, LongFormElementKind.Opening),
            KeepGroundedTextMetadata(
                request,
                request.ProgramName,
                LongFormElementKind.ProgramName),
            KeepGroundedTextMetadata(
                request,
                request.EpisodeNumber,
                LongFormElementKind.EpisodeNumber),
            title,
            request.PerformerIntroduction,
            request.PublicationScopeIndicator,
            body,
            request.Topics,
            KeepGroundedTextMetadata(
                request,
                request.Summary,
                LongFormElementKind.Summary),
            request.CallToActions
                .Select(callToAction => new LongFormCallToActionInput(
                    callToAction.Destination,
                    KeepGroundedTextMetadata(
                        request,
                        callToAction.Element,
                        LongFormElementKind.CallToAction)))
                .ToArray(),
            request.Ending,
            request.PostRollMargin);
    }

    private static LongFormElementInput KeepGroundedTextMetadata(
        LongFormCompositionRequest request,
        LongFormElementInput input,
        LongFormElementKind kind)
    {
        if (!input.Enabled)
        {
            return input;
        }

        OriginRef[] editorialOrigins = input.Origins
            .Where(origin =>
                origin.Kind == OriginKind.EditorialRecord
                && origin.EditorialRecordId is
                    EntityId<EditorialRecordIdTag> editorialRecordId
                && request.Provenance.TrustedEditorialRecordIds.Contains(
                    editorialRecordId))
            .OrderBy(static origin => origin.EditorialRecordId!.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        return editorialOrigins.Length == 0
            ? Omit(input, kind)
            : new LongFormElementInput(
                input.Id,
                kind,
                enabled: true,
                input.Text,
                input.OutputRange,
                editorialOrigins,
                input.Assets);
    }

    private static (Asset SourceAsset, SourceSpan[] SourceSpans)
        ResolveSourceOnlyEvidence(
            StandardCompositionOrchestrationRequest request,
            CancellationToken cancellationToken)
    {
        var provenanceById = new Dictionary<
            EntityId<SourceSpanIdTag>,
            SourceSpan>();
        foreach (SourceSpan span in request.LongFormRequest.Provenance.SourceSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provenanceById.TryAdd(span.Id, span))
            {
                throw Failure(
                    StandardCompositionOrchestrationFailureReason
                        .SourceEvidenceMismatch);
            }
        }

        var selected = new List<SourceSpan>();
        var selectedIds = new HashSet<EntityId<SourceSpanIdTag>>();
        foreach (CompositionElement element in request.Parent.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Origin.Kind != OriginKind.SourceSpan
                || element.Origin.SourceSpanId is not EntityId<SourceSpanIdTag>
                    sourceSpanId
                || !provenanceById.TryGetValue(
                    sourceSpanId,
                    out SourceSpan? span)
                || element.SourceRange is not TimeRange sourceRange
                || sourceRange.Start < span.Range.Start
                || sourceRange.End > span.Range.End)
            {
                throw Failure(
                    StandardCompositionOrchestrationFailureReason
                        .SourceEvidenceMismatch);
            }

            if (selectedIds.Add(span.Id))
            {
                selected.Add(span);
            }
        }

        SourceSpan[] sourceSpans = selected
            .OrderBy(static span => span.Range.Start)
            .ThenBy(static span => span.Range.End)
            .ThenBy(static span => span.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (sourceSpans.Length == 0
            || sourceSpans.Any(span =>
                span.AssetId != sourceSpans[0].AssetId
                || span.SourceDigest != sourceSpans[0].SourceDigest))
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason
                    .SourceEvidenceMismatch);
        }

        Asset[] sourceAssets = request.LongFormRequest.Project.Assets
            .Where(asset =>
                asset.Id == sourceSpans[0].AssetId
                && asset.ContentDigest == sourceSpans[0].SourceDigest)
            .ToArray();
        if (sourceAssets.Length != 1)
        {
            throw Failure(
                StandardCompositionOrchestrationFailureReason
                    .SourceEvidenceMismatch);
        }

        return (sourceAssets[0], sourceSpans);
    }

    private static LongFormElementInput Omit(
        LongFormElementInput input,
        LongFormElementKind kind) =>
        new(
            input.Id,
            kind,
            enabled: false,
            text: null,
            outputRange: null,
            origins: [],
            assets: []);

    private static WorkingCopy CreateCheckpointWorkingCopy(
        StandardCompositionPreparation preparation,
        DateTimeOffset updatedAt) =>
        new(
            preparation.PreparedWorkingCopy.Id,
            preparation.PreparedWorkingCopy.ProjectId,
            preparation.Child.Id,
            preparation.PreparedWorkingCopy.CreatedAt,
            updatedAt,
            preparation.Child.Elements);

    private static StandardCompositionOrchestrationException Failure(
        StandardCompositionOrchestrationFailureReason reason) => new(reason);
}
