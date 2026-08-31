using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Profiles;
using HubRadio.App.Speakers;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Compositions;

public sealed class LongFormPlanIdTag
{
}

public sealed class LongFormElementIdTag
{
}

public enum LongFormElementKind
{
    PreRollMargin,
    Opening,
    ProgramName,
    EpisodeNumber,
    EpisodeTitle,
    PerformerIntroduction,
    PublicationScopeIndicator,
    Body,
    Topic,
    Summary,
    CallToAction,
    Ending,
    PostRollMargin,
}

public enum LongFormDestination
{
    InternalDistribution,
    YouTube,
    YouTubeShorts,
    TikTok,
    LinkedIn,
    VivaEngage,
}

public enum LongFormElementState
{
    Omitted,
    Included,
}

public enum LongFormAssetRole
{
    OpeningMedia,
    KeyVisual,
    Logo,
    Jingle,
    BodyMedia,
    EndingMedia,
}

public enum LongFormAssetSlotState
{
    Omitted,
    Included,
}

public enum LongFormWarningCode
{
    ElementOmitted,
    TimingUnspecified,
    OptionalAssetOmitted,
    AssetRightsUnconfirmed,
    AssetRightsRejected,
    AssetExternalPublicationNotAllowed,
    PerformerTitleOrAffiliationUnconfirmed,
    PublicationScopeNotApproved,
    DevelopmentTemplateNotApproved,
    PlatformSafeZonesUnmeasured,
    ProfileLimitationUnmeasured,
}

public sealed class LongFormAssetReference
{
    public LongFormAssetReference(
        LongFormAssetRole role,
        EntityId<AssetIdTag> assetId,
        ContentDigest assetDigest)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        ArgumentNullException.ThrowIfNull(assetDigest);

        Role = role;
        AssetId = assetId;
        AssetDigest = assetDigest;
    }

    public LongFormAssetRole Role { get; }

    public EntityId<AssetIdTag> AssetId { get; }

    public ContentDigest AssetDigest { get; }
}

public sealed class LongFormElementInput
{
    public LongFormElementInput(
        EntityId<LongFormElementIdTag> id,
        LongFormElementKind kind,
        bool enabled,
        string? text,
        TimeRange? outputRange,
        IReadOnlyList<OriginRef> origins,
        IReadOnlyList<LongFormAssetReference> assets)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(assets);

        Id = id;
        Kind = kind;
        Enabled = enabled;
        Text = text;
        OutputRange = outputRange;
        Origins = Array.AsReadOnly(origins.ToArray());
        Assets = Array.AsReadOnly(assets.ToArray());
    }

    public EntityId<LongFormElementIdTag> Id { get; }

    public LongFormElementKind Kind { get; }

    public bool Enabled { get; }

    public string? Text { get; }

    public TimeRange? OutputRange { get; }

    public IReadOnlyList<OriginRef> Origins { get; }

    public IReadOnlyList<LongFormAssetReference> Assets { get; }
}

public sealed class LongFormTopicInput
{
    public LongFormTopicInput(
        LongFormElementInput element,
        TopicRangeRevision topicRange,
        TopicMetadataRevision topicMetadata)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(topicRange);
        ArgumentNullException.ThrowIfNull(topicMetadata);

        Element = element;
        TopicRange = topicRange;
        TopicMetadata = topicMetadata;
    }

    public LongFormElementInput Element { get; }

    public TopicRangeRevision TopicRange { get; }

    public TopicMetadataRevision TopicMetadata { get; }
}

public sealed class LongFormCallToActionInput
{
    public LongFormCallToActionInput(
        LongFormDestination destination,
        LongFormElementInput element)
    {
        ArgumentNullException.ThrowIfNull(element);

        Destination = destination;
        Element = element;
    }

    public LongFormDestination Destination { get; }

    public LongFormElementInput Element { get; }
}

/// <summary>
/// A caller-verified, request-scoped provenance catalog. Source spans carry their
/// concrete evidence. Because the domain currently has no EditorialRecord
/// aggregate, editorial evidence is intentionally limited to membership in the
/// closed ID catalog bound to this project and transcript revision.
/// </summary>
public sealed class LongFormProvenanceSnapshot
{
    public const string EditorialRecordEvidenceModel =
        "trusted-id-catalog-only";

    public LongFormProvenanceSnapshot(
        EntityId<ProjectIdTag> projectId,
        EntityId<TranscriptRevisionIdTag> transcriptRevisionId,
        ContentDigest transcriptRevisionDigest,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<EntityId<EditorialRecordIdTag>> trustedEditorialRecordIds)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(transcriptRevisionId);
        ArgumentNullException.ThrowIfNull(transcriptRevisionDigest);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(trustedEditorialRecordIds);

        ProjectId = projectId;
        TranscriptRevisionId = transcriptRevisionId;
        TranscriptRevisionDigest = transcriptRevisionDigest;
        SourceSpans = Array.AsReadOnly(sourceSpans.ToArray());
        TrustedEditorialRecordIds = Array.AsReadOnly(
            trustedEditorialRecordIds.ToArray());
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

    public ContentDigest TranscriptRevisionDigest { get; }

    public IReadOnlyList<SourceSpan> SourceSpans { get; }

    public IReadOnlyList<EntityId<EditorialRecordIdTag>>
        TrustedEditorialRecordIds
    { get; }
}

public sealed class LongFormCompositionRequest
{
    public LongFormCompositionRequest(
        EntityId<LongFormPlanIdTag> planId,
        Project project,
        LongFormProvenanceSnapshot provenance,
        AppliedBrandTemplate template,
        BundledRenderProfileDefinition profile,
        LongFormDestination destination,
        SpeakerAssignmentHistory? speakerAssignments,
        LongFormElementInput preRollMargin,
        LongFormElementInput opening,
        LongFormElementInput programName,
        LongFormElementInput episodeNumber,
        LongFormElementInput episodeTitle,
        LongFormElementInput performerIntroduction,
        LongFormElementInput publicationScopeIndicator,
        LongFormElementInput body,
        IReadOnlyList<LongFormTopicInput> topics,
        LongFormElementInput summary,
        IReadOnlyList<LongFormCallToActionInput> callToActions,
        LongFormElementInput ending,
        LongFormElementInput postRollMargin)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preRollMargin);
        ArgumentNullException.ThrowIfNull(opening);
        ArgumentNullException.ThrowIfNull(programName);
        ArgumentNullException.ThrowIfNull(episodeNumber);
        ArgumentNullException.ThrowIfNull(episodeTitle);
        ArgumentNullException.ThrowIfNull(performerIntroduction);
        ArgumentNullException.ThrowIfNull(publicationScopeIndicator);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(callToActions);
        ArgumentNullException.ThrowIfNull(ending);
        ArgumentNullException.ThrowIfNull(postRollMargin);

        PlanId = planId;
        Project = project;
        Provenance = provenance;
        Template = template;
        Profile = profile;
        Destination = destination;
        SpeakerAssignments = speakerAssignments;
        PreRollMargin = preRollMargin;
        Opening = opening;
        ProgramName = programName;
        EpisodeNumber = episodeNumber;
        EpisodeTitle = episodeTitle;
        PerformerIntroduction = performerIntroduction;
        PublicationScopeIndicator = publicationScopeIndicator;
        Body = body;
        Topics = Array.AsReadOnly(topics.ToArray());
        Summary = summary;
        CallToActions = Array.AsReadOnly(callToActions.ToArray());
        Ending = ending;
        PostRollMargin = postRollMargin;
    }

    public EntityId<LongFormPlanIdTag> PlanId { get; }

    public Project Project { get; }

    public LongFormProvenanceSnapshot Provenance { get; }

    public AppliedBrandTemplate Template { get; }

    public BundledRenderProfileDefinition Profile { get; }

    public LongFormDestination Destination { get; }

    public SpeakerAssignmentHistory? SpeakerAssignments { get; }

    public LongFormElementInput PreRollMargin { get; }

    public LongFormElementInput Opening { get; }

    public LongFormElementInput ProgramName { get; }

    public LongFormElementInput EpisodeNumber { get; }

    public LongFormElementInput EpisodeTitle { get; }

    public LongFormElementInput PerformerIntroduction { get; }

    public LongFormElementInput PublicationScopeIndicator { get; }

    public LongFormElementInput Body { get; }

    public IReadOnlyList<LongFormTopicInput> Topics { get; }

    public LongFormElementInput Summary { get; }

    public IReadOnlyList<LongFormCallToActionInput> CallToActions { get; }

    public LongFormElementInput Ending { get; }

    public LongFormElementInput PostRollMargin { get; }
}

public sealed class LongFormAssetSlot
{
    internal LongFormAssetSlot(
        LongFormAssetRole role,
        LongFormAssetSlotState state,
        Asset? asset,
        OriginRef? origin)
    {
        if (!Enum.IsDefined(role)
            || !Enum.IsDefined(state)
            || (state == LongFormAssetSlotState.Included)
                != (asset is not null && origin is not null)
            || origin is not null
                && (origin.Kind != OriginKind.Asset
                    || origin.AssetId != asset?.Id))
        {
            throw new ArgumentException(
                "A long-form asset slot is inconsistent.",
                nameof(state));
        }

        Role = role;
        State = state;
        Asset = asset;
        Origin = origin;
    }

    public LongFormAssetRole Role { get; }

    public LongFormAssetSlotState State { get; }

    public Asset? Asset { get; }

    public OriginRef? Origin { get; }

    public RightsRecord? Rights => Asset?.Rights;
}

public sealed class LongFormTemplateAssetSnapshot
{
    internal LongFormTemplateAssetSnapshot(
        string parameterName,
        Asset asset)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        ArgumentNullException.ThrowIfNull(asset);

        ParameterName = parameterName;
        Asset = asset;
        Origin = OriginRef.FromAsset(asset.Id);
    }

    public string ParameterName { get; }

    public Asset Asset { get; }

    public OriginRef Origin { get; }

    public RightsRecord Rights => Asset.Rights;
}

public sealed class LongFormElementPlan
{
    internal LongFormElementPlan(
        EntityId<LongFormElementIdTag> id,
        LongFormElementKind kind,
        LongFormElementState state,
        string? text,
        TimeRange? outputRange,
        IReadOnlyList<OriginRef> origins,
        IReadOnlyList<LongFormAssetSlot> assetSlots,
        TopicRangeRevision? topicRange,
        TopicMetadataRevision? topicMetadata)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(assetSlots);
        if (!Enum.IsDefined(kind)
            || !Enum.IsDefined(state)
            || (kind == LongFormElementKind.Topic)
                != (topicRange is not null && topicMetadata is not null))
        {
            throw new ArgumentException(
                "A long-form element plan is inconsistent.",
                nameof(kind));
        }

        Id = id;
        Kind = kind;
        State = state;
        Text = text;
        OutputRange = outputRange;
        Origins = Array.AsReadOnly(origins.ToArray());
        AssetSlots = Array.AsReadOnly(assetSlots.ToArray());
        TopicRange = topicRange;
        TopicMetadata = topicMetadata;
    }

    public EntityId<LongFormElementIdTag> Id { get; }

    public LongFormElementKind Kind { get; }

    public LongFormElementState State { get; }

    public string? Text { get; }

    public TimeRange? OutputRange { get; }

    public IReadOnlyList<OriginRef> Origins { get; }

    public IReadOnlyList<LongFormAssetSlot> AssetSlots { get; }

    public TopicRangeRevision? TopicRange { get; }

    public TopicMetadataRevision? TopicMetadata { get; }
}

public sealed class LongFormWarning
{
    internal LongFormWarning(
        LongFormWarningCode code,
        EntityId<LongFormElementIdTag>? elementId = null,
        EntityId<AssetIdTag>? assetId = null,
        EntityId<PerformerIdTag>? performerId = null,
        LongFormAssetRole? assetRole = null,
        string? limitation = null)
    {
        if (!Enum.IsDefined(code)
            || assetRole is LongFormAssetRole role && !Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
        ElementId = elementId;
        AssetId = assetId;
        PerformerId = performerId;
        AssetRole = assetRole;
        Limitation = limitation;
    }

    public LongFormWarningCode Code { get; }

    public EntityId<LongFormElementIdTag>? ElementId { get; }

    public EntityId<AssetIdTag>? AssetId { get; }

    public EntityId<PerformerIdTag>? PerformerId { get; }

    public LongFormAssetRole? AssetRole { get; }

    public string? Limitation { get; }
}

public sealed class LongFormCompositionPlan
{
    internal LongFormCompositionPlan(
        EntityId<LongFormPlanIdTag> planId,
        Project project,
        LongFormProvenanceSnapshot provenance,
        AppliedBrandTemplate template,
        BundledRenderProfileDefinition profile,
        LongFormDestination destination,
        SpeakerAssignmentHistory? speakerAssignments,
        IReadOnlyList<LongFormElementPlan> elements,
        IReadOnlyList<LongFormTemplateAssetSnapshot> templateAssets,
        IReadOnlyList<Asset> referencedAssets,
        IReadOnlyList<LongFormWarning> warnings,
        bool timingComplete,
        bool externalRightsReady,
        bool externalPublicationReady,
        ContentDigest planDigest)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(templateAssets);
        ArgumentNullException.ThrowIfNull(referencedAssets);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(planDigest);

        PlanId = planId;
        Project = project;
        Provenance = provenance;
        Template = template;
        Profile = profile;
        Destination = destination;
        SpeakerAssignments = speakerAssignments;
        Elements = Array.AsReadOnly(elements.ToArray());
        TemplateAssets = Array.AsReadOnly(templateAssets.ToArray());
        ReferencedAssets = Array.AsReadOnly(referencedAssets.ToArray());
        Warnings = Array.AsReadOnly(warnings.ToArray());
        TimingComplete = timingComplete;
        ExternalRightsReady = externalRightsReady;
        ExternalPublicationReady = externalPublicationReady;
        PlanDigest = planDigest;
    }

    public EntityId<LongFormPlanIdTag> PlanId { get; }

    public Project Project { get; }

    public LongFormProvenanceSnapshot Provenance { get; }

    public AppliedBrandTemplate Template { get; }

    public BundledRenderProfileDefinition Profile { get; }

    public LongFormDestination Destination { get; }

    public SpeakerAssignmentHistory? SpeakerAssignments { get; }

    public IReadOnlyList<LongFormElementPlan> Elements { get; }

    public IReadOnlyList<LongFormTemplateAssetSnapshot> TemplateAssets { get; }

    public IReadOnlyList<Asset> ReferencedAssets { get; }

    public IReadOnlyList<LongFormWarning> Warnings { get; }

    public bool TimingComplete { get; }

    public bool ExternalRightsReady { get; }

    public bool ExternalPublicationReady { get; }

    public ContentDigest PlanDigest { get; }
}

public enum LongFormComposerFailureReason
{
    TemplateMismatch,
    UnsupportedProfile,
    ContextMismatch,
    InvalidElement,
    DuplicateElementId,
    OriginEvidenceMissing,
    AssetBindingMismatch,
    TimingConflict,
    TopicBindingMismatch,
    TopicOrderConflict,
    SpeakerEvidenceMissing,
    InvalidText,
    ArithmeticOverflow,
}

public sealed class LongFormComposerException : Exception
{
    internal LongFormComposerException(LongFormComposerFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public LongFormComposerFailureReason Reason { get; }

    private static string GetStableMessage(
        LongFormComposerFailureReason reason) =>
        reason switch
        {
            LongFormComposerFailureReason.TemplateMismatch =>
                "The long-form brand template is inconsistent.",
            LongFormComposerFailureReason.UnsupportedProfile =>
                "The render profile does not support long-form composition.",
            LongFormComposerFailureReason.ContextMismatch =>
                "The long-form composition context is inconsistent.",
            LongFormComposerFailureReason.InvalidElement =>
                "A long-form element is inconsistent.",
            LongFormComposerFailureReason.DuplicateElementId =>
                "Long-form element IDs must be unique.",
            LongFormComposerFailureReason.OriginEvidenceMissing =>
                "A long-form element is missing required origin evidence.",
            LongFormComposerFailureReason.AssetBindingMismatch =>
                "A long-form asset reference is inconsistent.",
            LongFormComposerFailureReason.TimingConflict =>
                "Long-form timing evidence is inconsistent.",
            LongFormComposerFailureReason.TopicBindingMismatch =>
                "Long-form topic evidence is inconsistent.",
            LongFormComposerFailureReason.TopicOrderConflict =>
                "Long-form topics overlap or are not canonically ordered.",
            LongFormComposerFailureReason.SpeakerEvidenceMissing =>
                "Long-form performer evidence is missing or inconsistent.",
            LongFormComposerFailureReason.InvalidText =>
                "Long-form composition text could not be processed safely.",
            LongFormComposerFailureReason.ArithmeticOverflow =>
                "Long-form time arithmetic exceeds the supported range.",
            _ => "Long-form composition failed.",
        };
}

public static class LongFormComposer
{
    public const string DigestSchemaVersion = "r015-long-form-plan.v2";

    private const string DevelopmentTemplateStatus =
        "development-neutral-placeholder";
    private const string ProgramNameParameter = "program-name";
    private const string EpisodeNumberParameter = "episode-number";
    private const string EpisodeTitleParameter = "episode-title";
    private const string KeyVisualParameter = "key-visual";
    private const string LogoParameter = "logo";
    private const string JingleParameter = "jingle";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions DigestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] RequiredCapabilities =
    [
        "chapter",
        "topic-display",
        "speaker-name",
        "call-to-action",
        "end-card-margin",
        "graphic",
    ];

    private static readonly LongFormAssetRole[] OpeningAssetRoles =
    [
        LongFormAssetRole.OpeningMedia,
        LongFormAssetRole.KeyVisual,
        LongFormAssetRole.Logo,
        LongFormAssetRole.Jingle,
    ];

    private static readonly LongFormAssetRole[] BodyAssetRoles =
    [
        LongFormAssetRole.BodyMedia,
    ];

    private static readonly LongFormAssetRole[] EndingAssetRoles =
    [
        LongFormAssetRole.EndingMedia,
    ];

    public static LongFormCompositionPlan Compose(
        LongFormCompositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ValidateProject(request.Project, cancellationToken);
            ValidatedProvenance provenance = ValidateProvenance(
                request,
                cancellationToken);
            ValidateTemplateAndProfile(request, cancellationToken);
            Dictionary<EntityId<AssetIdTag>, Asset> projectAssets =
                request.Project.Assets.ToDictionary(static asset => asset.Id);
            LongFormTemplateAssetSnapshot[] templateAssets =
                ResolveTemplateAssets(
                    request.Template,
                    projectAssets,
                    cancellationToken);
            LongFormTopicInput[] topics = ValidateAndSortTopics(
                request,
                cancellationToken);
            ValidateSpeakerEvidence(request, topics, cancellationToken);
            LongFormElementInput callToAction = SelectCallToAction(
                request,
                provenance,
                cancellationToken);

            var elements = new List<LongFormElementPlan>(12 + topics.Length);
            elements.Add(BuildElement(
                request.PreRollMargin,
                LongFormElementKind.PreRollMargin,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.Opening,
                LongFormElementKind.Opening,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.ProgramName,
                LongFormElementKind.ProgramName,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.EpisodeNumber,
                LongFormElementKind.EpisodeNumber,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.EpisodeTitle,
                LongFormElementKind.EpisodeTitle,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.PerformerIntroduction,
                LongFormElementKind.PerformerIntroduction,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.PublicationScopeIndicator,
                LongFormElementKind.PublicationScopeIndicator,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            LongFormElementPlan body = BuildElement(
                request.Body,
                LongFormElementKind.Body,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken);
            elements.Add(body);
            foreach (LongFormTopicInput topic in topics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LongFormElementPlan topicElement = BuildElement(
                    topic.Element,
                    LongFormElementKind.Topic,
                    request.Project,
                    projectAssets,
                    request.Template,
                    provenance,
                    topic,
                    cancellationToken);
                ValidateImportantStatementOrigins(topicElement);
                elements.Add(topicElement);
            }

            elements.Add(BuildElement(
                request.Summary,
                LongFormElementKind.Summary,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                callToAction,
                LongFormElementKind.CallToAction,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.Ending,
                LongFormElementKind.Ending,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));
            elements.Add(BuildElement(
                request.PostRollMargin,
                LongFormElementKind.PostRollMargin,
                request.Project,
                projectAssets,
                request.Template,
                provenance,
                topic: null,
                cancellationToken));

            ValidateElementIds(elements);
            ValidateBodyAndTopicState(body, elements);
            ValidateExplicitTiming(elements);

            Asset[] referencedAssets = CollectReferencedAssets(elements);
            LongFormWarning[] warnings = BuildWarnings(
                request,
                elements,
                referencedAssets,
                cancellationToken);
            bool timingComplete = elements.Any(
                    static element =>
                        element.State == LongFormElementState.Included)
                && elements
                    .Where(static element =>
                        element.State == LongFormElementState.Included)
                    .All(static element => element.OutputRange is not null);
            bool externalRightsReady = referencedAssets.All(
                static asset =>
                    asset.Rights.Status == RightsStatus.Approved
                    && asset.Rights.ExternalPublicationAllowed);
            bool profileEvidenceComplete =
                request.Profile.Unmeasured.Count == 0
                && request.Profile.PlatformSafeZonesMeasured
                && !request.Profile.RequiredExternalBrandAssets;
            bool externalPublicationReady =
                request.Project.PublicationScope
                    == PublicationScope.ExternalPublicationApproved
                && externalRightsReady
                && timingComplete
                && profileEvidenceComplete
                && body.State == LongFormElementState.Included;

            cancellationToken.ThrowIfCancellationRequested();
            ContentDigest digest = ComputePlanDigest(
                request,
                elements,
                templateAssets,
                referencedAssets,
                warnings,
                timingComplete,
                externalRightsReady,
                externalPublicationReady);
            return new LongFormCompositionPlan(
                request.PlanId,
                request.Project,
                request.Provenance,
                request.Template,
                request.Profile,
                request.Destination,
                request.SpeakerAssignments,
                elements,
                templateAssets,
                referencedAssets,
                warnings,
                timingComplete,
                externalRightsReady,
                externalPublicationReady,
                digest);
        }
        catch (LongFormComposerException)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw Failure(LongFormComposerFailureReason.ArithmeticOverflow);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(LongFormComposerFailureReason.InvalidText);
        }
        catch (JsonException)
        {
            throw Failure(LongFormComposerFailureReason.InvalidText);
        }
    }

    private static void ValidateProject(
        Project project,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(project.PublicationScope))
        {
            throw Failure(LongFormComposerFailureReason.ContextMismatch);
        }

        ValidateText(project.ContentName);
        ValidateText(project.Purpose);
        ValidateText(project.Audience);
        var assetIds = new HashSet<EntityId<AssetIdTag>>();
        foreach (Asset asset in project.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset is null
                || !assetIds.Add(asset.Id)
                || !Enum.IsDefined(asset.Rights.Status))
            {
                throw Failure(LongFormComposerFailureReason.ContextMismatch);
            }

            ValidateText(asset.FileName);
            ValidateText(asset.MediaType);
            ValidateText(asset.Rights.AllowedUse);
            ValidateOptionalText(asset.Rights.EvidenceReference);
        }

        var performerIds = new HashSet<EntityId<PerformerIdTag>>();
        foreach (Performer performer in project.Performers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (performer is null || !performerIds.Add(performer.Id))
            {
                throw Failure(LongFormComposerFailureReason.ContextMismatch);
            }

            ValidateText(performer.DisplayName);
            ValidateText(performer.Role);
        }
    }

    private static ValidatedProvenance ValidateProvenance(
        LongFormCompositionRequest request,
        CancellationToken cancellationToken)
    {
        LongFormProvenanceSnapshot snapshot = request.Provenance;
        if (snapshot.ProjectId != request.Project.Id)
        {
            throw Failure(LongFormComposerFailureReason.ContextMismatch);
        }

        Dictionary<EntityId<AssetIdTag>, Asset> projectAssets =
            request.Project.Assets.ToDictionary(static asset => asset.Id);
        var sourceSpans =
            new Dictionary<EntityId<SourceSpanIdTag>, SourceSpan>();
        foreach (SourceSpan span in snapshot.SourceSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (span is null
                || !sourceSpans.TryAdd(span.Id, span)
                || span.TranscriptRevisionId != snapshot.TranscriptRevisionId
                || !projectAssets.TryGetValue(span.AssetId, out Asset? asset)
                || span.SourceDigest != asset.ContentDigest)
            {
                throw Failure(LongFormComposerFailureReason.ContextMismatch);
            }

            ValidateText(span.StreamId);
        }

        var editorialRecordIds =
            new HashSet<EntityId<EditorialRecordIdTag>>();
        foreach (EntityId<EditorialRecordIdTag> editorialRecordId in
            snapshot.TrustedEditorialRecordIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (editorialRecordId is null
                || !editorialRecordIds.Add(editorialRecordId))
            {
                throw Failure(LongFormComposerFailureReason.ContextMismatch);
            }
        }

        return new ValidatedProvenance(sourceSpans, editorialRecordIds);
    }

    private static LongFormElementInput SelectCallToAction(
        LongFormCompositionRequest request,
        ValidatedProvenance provenance,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Destination))
        {
            throw Failure(LongFormComposerFailureReason.InvalidElement);
        }

        var destinations = new HashSet<LongFormDestination>();
        LongFormElementInput? selected = null;
        foreach (LongFormCallToActionInput callToAction in
            request.CallToActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (callToAction is null
                || !Enum.IsDefined(callToAction.Destination)
                || !destinations.Add(callToAction.Destination)
                || callToAction.Element.Kind
                    != LongFormElementKind.CallToAction)
            {
                throw Failure(LongFormComposerFailureReason.InvalidElement);
            }

            ValidateAlternativeCallToAction(
                callToAction.Element,
                provenance);
            if (callToAction.Destination == request.Destination)
            {
                selected = callToAction.Element;
            }
        }

        return selected
            ?? throw Failure(LongFormComposerFailureReason.InvalidElement);
    }

    private static void ValidateAlternativeCallToAction(
        LongFormElementInput element,
        ValidatedProvenance provenance)
    {
        if (!element.Enabled)
        {
            if (element.Text is not null
                || element.OutputRange is not null
                || element.Origins.Count != 0
                || element.Assets.Count != 0)
            {
                throw Failure(LongFormComposerFailureReason.InvalidElement);
            }

            return;
        }

        if (element.Text is null || element.Assets.Count != 0)
        {
            throw Failure(LongFormComposerFailureReason.InvalidElement);
        }

        ValidateText(element.Text);
        if (element.OutputRange is TimeRange range
            && (range.Start < default(RationalTime)
                || range.Duration <= default(RationalTime)))
        {
            throw Failure(LongFormComposerFailureReason.TimingConflict);
        }

        if (ValidateAndSortOrigins(element.Origins, provenance).Length == 0)
        {
            throw Failure(LongFormComposerFailureReason.OriginEvidenceMissing);
        }
    }

    private static void ValidateTemplateAndProfile(
        LongFormCompositionRequest request,
        CancellationToken cancellationToken)
    {
        AppliedBrandTemplate template = request.Template;
        BundledRenderProfileDefinition definition = request.Profile;
        if (!Enum.IsDefined(template.Kind)
            || template.Kind != BrandTemplateKind.LongForm)
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        if (definition.Profile.TemplateId != template.Reference.Id
            || definition.Profile.TemplateVersionDigest
                != template.Reference.VersionDigest)
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        if (definition.Layout.Id != definition.Profile.LayoutSpecId
            || definition.Layout.OutputAspectWidth != 16
            || definition.Layout.OutputAspectHeight != 9
            || (long)definition.Profile.Width * 9
                != (long)definition.Profile.Height * 16)
        {
            throw Failure(LongFormComposerFailureReason.UnsupportedProfile);
        }

        ValidateText(template.Reference.Name);
        ValidateText(definition.Profile.Name);
        ValidateText(definition.Profile.VideoCodec);
        ValidateText(definition.Profile.AudioCodec);
        ValidateText(definition.PixelFormatEvidence);
        ValidateText(definition.EvidenceSource);
        ValidateText(definition.TemplateStatus);

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (string capability in definition.Capabilities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateText(capability);
            if (!capabilities.Add(capability))
            {
                throw Failure(LongFormComposerFailureReason.UnsupportedProfile);
            }
        }

        if (RequiredCapabilities.Any(capability => !capabilities.Contains(capability)))
        {
            throw Failure(LongFormComposerFailureReason.UnsupportedProfile);
        }

        foreach (string limitation in definition.Unmeasured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateText(limitation);
        }

        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (TemplateParameterAssignment parameter in template.Parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parameter is null || !parameterNames.Add(parameter.Name))
            {
                throw Failure(LongFormComposerFailureReason.TemplateMismatch);
            }

            ValidateText(parameter.Name);
            ValidateTemplateValue(parameter.Value);
            ValidateKnownTemplateParameter(parameter);
        }
    }

    private static void ValidateKnownTemplateParameter(
        TemplateParameterAssignment parameter)
    {
        TemplateParameterValueKind? expectedKind = parameter.Name switch
        {
            ProgramNameParameter or EpisodeTitleParameter =>
                TemplateParameterValueKind.Text,
            EpisodeNumberParameter => TemplateParameterValueKind.Integer,
            KeyVisualParameter or LogoParameter or JingleParameter =>
                TemplateParameterValueKind.AssetReference,
            _ => null,
        };
        if (expectedKind is TemplateParameterValueKind expected
            && parameter.Value.Kind != expected)
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }
    }

    private static void ValidateTemplateValue(TemplateParameterValue value)
    {
        if (value is null || !Enum.IsDefined(value.Kind))
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        bool valid = value.Kind switch
        {
            TemplateParameterValueKind.Text =>
                value.TextValue is not null
                && value.IntegerValue is null
                && value.BooleanValue is null
                && value.RationalValue is null
                && value.AssetId is null
                && value.AssetDigest is null,
            TemplateParameterValueKind.Integer =>
                value.TextValue is null
                && value.IntegerValue is not null
                && value.BooleanValue is null
                && value.RationalValue is null
                && value.AssetId is null
                && value.AssetDigest is null,
            TemplateParameterValueKind.Boolean =>
                value.TextValue is null
                && value.IntegerValue is null
                && value.BooleanValue is not null
                && value.RationalValue is null
                && value.AssetId is null
                && value.AssetDigest is null,
            TemplateParameterValueKind.Rational =>
                value.TextValue is null
                && value.IntegerValue is null
                && value.BooleanValue is null
                && value.RationalValue is not null
                && value.AssetId is null
                && value.AssetDigest is null,
            TemplateParameterValueKind.AssetReference =>
                value.TextValue is null
                && value.IntegerValue is null
                && value.BooleanValue is null
                && value.RationalValue is null
                && value.AssetId is not null
                && value.AssetDigest is not null,
            _ => false,
        };
        if (!valid)
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        if (value.TextValue is not null)
        {
            ValidateText(value.TextValue);
        }
    }

    private static LongFormTemplateAssetSnapshot[] ResolveTemplateAssets(
        AppliedBrandTemplate template,
        IReadOnlyDictionary<EntityId<AssetIdTag>, Asset> projectAssets,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<LongFormTemplateAssetSnapshot>();
        foreach (TemplateParameterAssignment parameter in template.Parameters
            .OrderBy(static parameter => parameter.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parameter.Value.Kind != TemplateParameterValueKind.AssetReference)
            {
                continue;
            }

            EntityId<AssetIdTag> assetId = parameter.Value.AssetId
                ?? throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
            ContentDigest digest = parameter.Value.AssetDigest
                ?? throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
            if (!projectAssets.TryGetValue(assetId, out Asset? asset)
                || asset.ContentDigest != digest)
            {
                throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
            }

            snapshots.Add(new LongFormTemplateAssetSnapshot(
                parameter.Name,
                asset));
        }

        return snapshots.ToArray();
    }

    private static LongFormTopicInput[] ValidateAndSortTopics(
        LongFormCompositionRequest request,
        CancellationToken cancellationToken)
    {
        var topicRangeIds = new HashSet<EntityId<TopicRangeIdTag>>();
        var metadataIds = new HashSet<EntityId<TopicMetadataIdTag>>();
        var topics = new LongFormTopicInput[request.Topics.Count];
        EntityId<TranscriptRevisionIdTag>? sourceRevisionId = null;
        ContentDigest? sourceRevisionDigest = null;
        for (int index = 0; index < topics.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LongFormTopicInput topic = request.Topics[index]
                ?? throw Failure(LongFormComposerFailureReason.TopicBindingMismatch);
            TopicRangeRevision range = topic.TopicRange;
            TopicMetadataRevision metadata = topic.TopicMetadata;
            if (!topicRangeIds.Add(range.TopicRangeId)
                || !metadataIds.Add(metadata.TopicMetadataId)
                || range.Range.Duration <= default(RationalTime)
                || metadata.ProjectId != request.Project.Id
                || metadata.TopicRangeId != range.TopicRangeId
                || metadata.TopicRangeRevisionId != range.Id
                || metadata.TopicRange != range.Range
                || metadata.SourceRevisionId != range.SourceRevisionId
                || metadata.SourceRevisionDigest != range.SourceRevisionDigest
                || range.SourceRevisionId
                    != request.Provenance.TranscriptRevisionId
                || range.SourceRevisionDigest
                    != request.Provenance.TranscriptRevisionDigest)
            {
                throw Failure(LongFormComposerFailureReason.TopicBindingMismatch);
            }

            if (sourceRevisionId is null)
            {
                sourceRevisionId = range.SourceRevisionId;
                sourceRevisionDigest = range.SourceRevisionDigest;
            }
            else if (sourceRevisionId != range.SourceRevisionId
                || sourceRevisionDigest != range.SourceRevisionDigest)
            {
                throw Failure(LongFormComposerFailureReason.TopicBindingMismatch);
            }

            ValidateTopicSnapshot(topic);
            topics[index] = topic;
        }

        Array.Sort(
            topics,
            static (left, right) =>
            {
                int start = left.TopicRange.Range.Start.CompareTo(
                    right.TopicRange.Range.Start);
                if (start != 0)
                {
                    return start;
                }

                int end = left.TopicRange.Range.End.CompareTo(
                    right.TopicRange.Range.End);
                return end != 0
                    ? end
                    : StringComparer.Ordinal.Compare(
                        left.TopicRange.TopicRangeId.ToString(),
                        right.TopicRange.TopicRangeId.ToString());
            });

        for (int index = 1; index < topics.Length; index++)
        {
            if (topics[index].TopicRange.Range.Start
                < topics[index - 1].TopicRange.Range.End)
            {
                throw Failure(LongFormComposerFailureReason.TopicOrderConflict);
            }
        }

        return topics;
    }

    private static void ValidateTopicSnapshot(LongFormTopicInput topic)
    {
        TopicRangeRevision range = topic.TopicRange;
        TopicMetadataRevision revision = topic.TopicMetadata;
        TopicMetadata metadata = revision.Metadata;
        if (!Enum.IsDefined(revision.ChangedField)
            || !Enum.IsDefined(metadata.TitleOrigin)
            || !Enum.IsDefined(metadata.SummaryOrigin)
            || !Enum.IsDefined(metadata.MainSpeakerOrigin)
            || !Enum.IsDefined(metadata.KeywordsOrigin)
            || !Enum.IsDefined(metadata.ImportantStatementsOrigin)
            || !Enum.IsDefined(metadata.AudienceOrigin)
            || !Enum.IsDefined(metadata.PublicationCautionsOrigin))
        {
            throw Failure(LongFormComposerFailureReason.TopicBindingMismatch);
        }

        ValidateText(range.ActorSid);
        ValidateText(range.Reason);
        ValidateText(revision.ActorSid);
        ValidateText(revision.Reason);
        ValidateText(metadata.Title);
        ValidateOptionalText(metadata.Summary);
        ValidateText(metadata.Audience);
        foreach (string keyword in metadata.Keywords)
        {
            ValidateText(keyword);
        }

        foreach (string caution in metadata.PublicationCautions)
        {
            ValidateText(caution);
        }

        if (metadata.MainSpeaker is TopicMainSpeakerSnapshot speaker)
        {
            ValidateOptionalText(speaker.DisplayText);
        }
    }

    private static void ValidateSpeakerEvidence(
        LongFormCompositionRequest request,
        IReadOnlyList<LongFormTopicInput> topics,
        CancellationToken cancellationToken)
    {
        SpeakerAssignmentHistory? history = request.SpeakerAssignments;
        if (history is null)
        {
            if (request.PerformerIntroduction.Enabled)
            {
                throw Failure(
                    LongFormComposerFailureReason.SpeakerEvidenceMissing);
            }

            foreach (LongFormTopicInput topic in topics)
            {
                ValidateTopicMainSpeaker(
                    topic.TopicMetadata.Metadata.MainSpeaker,
                    history: null);
            }

            return;
        }

        if (history.ProjectId != request.Project.Id
            || history.TranscriptRevisionId
                != request.Provenance.TranscriptRevisionId
            || history.TranscriptRevisionDigest
                != request.Provenance.TranscriptRevisionDigest)
        {
            throw Failure(LongFormComposerFailureReason.ContextMismatch);
        }

        if (request.PerformerIntroduction.Enabled
            && history.CurrentRevision.Assignments.Count == 0)
        {
            throw Failure(LongFormComposerFailureReason.SpeakerEvidenceMissing);
        }

        foreach (SpeakerAssignmentRevision revision in history.Revisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateText(revision.ActorSid);
            ValidateText(revision.Reason);
            foreach (SpeakerAssignment assignment in revision.Assignments)
            {
                if (!Enum.IsDefined(assignment.DisplayFormat))
                {
                    throw Failure(
                        LongFormComposerFailureReason.SpeakerEvidenceMissing);
                }

                ValidateText(assignment.AnonymousLabel);
                ValidateText(assignment.FullName);
                ValidateText(assignment.Surname);
                ValidateText(assignment.Role);
            }
        }

        foreach (SpeakerAssignment assignment in
            history.CurrentRevision.Assignments)
        {
            Performer? performer = request.Project.Performers.SingleOrDefault(
                candidate => candidate.Id == assignment.PerformerId);
            if (performer is null
                || !string.Equals(
                    performer.DisplayName,
                    assignment.FullName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    performer.Role,
                    assignment.Role,
                    StringComparison.Ordinal)
                || performer.TitleConfirmedAt != assignment.TitleConfirmedAt)
            {
                throw Failure(
                    LongFormComposerFailureReason.SpeakerEvidenceMissing);
            }
        }

        foreach (LongFormTopicInput topic in topics)
        {
            ValidateTopicMainSpeaker(
                topic.TopicMetadata.Metadata.MainSpeaker,
                history);
        }
    }

    private static void ValidateTopicMainSpeaker(
        TopicMainSpeakerSnapshot? snapshot,
        SpeakerAssignmentHistory? history)
    {
        if (snapshot is null)
        {
            return;
        }

        SpeakerAssignment? assignment = history?.CurrentRevision.Assignments
            .SingleOrDefault(candidate =>
                candidate.SpeakerId == snapshot.SpeakerId);
        if (assignment is null)
        {
            if (snapshot.PerformerId is not null
                || !snapshot.DisplayVisible
                || snapshot.DisplayText is null
                || snapshot.TitleOrAffiliationConfirmationRequired)
            {
                throw Failure(
                    LongFormComposerFailureReason.SpeakerEvidenceMissing);
            }

            return;
        }

        string? expectedText = assignment.DisplayFormat switch
        {
            SpeakerDisplayFormat.FullName => assignment.FullName,
            SpeakerDisplayFormat.Surname => assignment.Surname,
            SpeakerDisplayFormat.Role => assignment.Role,
            SpeakerDisplayFormat.Hidden => null,
            _ => throw Failure(
                LongFormComposerFailureReason.SpeakerEvidenceMissing),
        };
        bool expectedVisible =
            assignment.DisplayFormat != SpeakerDisplayFormat.Hidden;
        if (snapshot.PerformerId != assignment.PerformerId
            || snapshot.DisplayVisible != expectedVisible
            || !string.Equals(
                snapshot.DisplayText,
                expectedText,
                StringComparison.Ordinal)
            || snapshot.TitleOrAffiliationConfirmationRequired
                != assignment.TitleOrAffiliationConfirmationRequired)
        {
            throw Failure(
                LongFormComposerFailureReason.SpeakerEvidenceMissing);
        }
    }

    private static LongFormElementPlan BuildElement(
        LongFormElementInput input,
        LongFormElementKind expectedKind,
        Project project,
        IReadOnlyDictionary<EntityId<AssetIdTag>, Asset> projectAssets,
        AppliedBrandTemplate template,
        ValidatedProvenance provenance,
        LongFormTopicInput? topic,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(input.Kind) || input.Kind != expectedKind)
        {
            throw Failure(LongFormComposerFailureReason.InvalidElement);
        }

        LongFormAssetRole[] expectedRoles = GetExpectedAssetRoles(expectedKind);
        if (!input.Enabled)
        {
            if (input.Text is not null
                || input.OutputRange is not null
                || input.Origins.Count != 0
                || input.Assets.Count != 0)
            {
                throw Failure(LongFormComposerFailureReason.InvalidElement);
            }

            return new LongFormElementPlan(
                input.Id,
                expectedKind,
                LongFormElementState.Omitted,
                text: null,
                outputRange: null,
                origins: [],
                CreateOmittedAssetSlots(expectedRoles),
                topic?.TopicRange,
                topic?.TopicMetadata);
        }

        if (input.OutputRange is TimeRange outputRange
            && (outputRange.Start < default(RationalTime)
                || outputRange.Duration <= default(RationalTime)))
        {
            throw Failure(LongFormComposerFailureReason.TimingConflict);
        }

        OriginRef[] origins = ValidateAndSortOrigins(
            input.Origins,
            provenance);
        if (origins.Length == 0)
        {
            throw Failure(LongFormComposerFailureReason.OriginEvidenceMissing);
        }

        LongFormAssetSlot[] assetSlots = ResolveElementAssets(
            input,
            expectedRoles,
            projectAssets,
            template,
            origins,
            cancellationToken);
        if (expectedKind == LongFormElementKind.Body
            && assetSlots.Single().State != LongFormAssetSlotState.Included)
        {
            throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
        }

        string? text = ResolveElementText(
            input,
            expectedKind,
            project,
            template,
            topic);
        return new LongFormElementPlan(
            input.Id,
            expectedKind,
            LongFormElementState.Included,
            text,
            input.OutputRange,
            origins,
            assetSlots,
            topic?.TopicRange,
            topic?.TopicMetadata);
    }

    private static string? ResolveElementText(
        LongFormElementInput input,
        LongFormElementKind kind,
        Project project,
        AppliedBrandTemplate template,
        LongFormTopicInput? topic)
    {
        string? resolved = kind switch
        {
            LongFormElementKind.ProgramName => ResolveTemplateText(
                template,
                ProgramNameParameter,
                input.Text,
                input.Text),
            LongFormElementKind.EpisodeNumber => ResolveEpisodeNumber(
                template,
                input.Text),
            LongFormElementKind.EpisodeTitle => ResolveTemplateText(
                template,
                EpisodeTitleParameter,
                input.Text,
                project.ContentName),
            LongFormElementKind.Summary
                or LongFormElementKind.CallToAction =>
                input.Text
                ?? throw Failure(LongFormComposerFailureReason.InvalidElement),
            LongFormElementKind.Topic => topic?.TopicMetadata.Metadata.Title
                ?? throw Failure(
                    LongFormComposerFailureReason.TopicBindingMismatch),
            _ => null,
        };

        if (kind == LongFormElementKind.Topic
                && input.Text is not null
                && !string.Equals(input.Text, resolved, StringComparison.Ordinal)
            || kind is not (
                    LongFormElementKind.ProgramName
                    or LongFormElementKind.EpisodeNumber
                    or LongFormElementKind.Summary
                    or LongFormElementKind.CallToAction
                    or LongFormElementKind.EpisodeTitle
                    or LongFormElementKind.Topic)
                && input.Text is not null)
        {
            throw Failure(LongFormComposerFailureReason.InvalidElement);
        }

        if (resolved is not null)
        {
            ValidateText(resolved);
        }

        return resolved;
    }

    private static string ResolveTemplateText(
        AppliedBrandTemplate template,
        string parameterName,
        string? suppliedText,
        string? fallback)
    {
        TemplateParameterAssignment? parameter = FindTemplateParameter(
            template,
            parameterName);
        if (parameter is null)
        {
            return fallback
                ?? throw Failure(LongFormComposerFailureReason.InvalidElement);
        }

        if (parameter.Value.Kind != TemplateParameterValueKind.Text
            || parameter.Value.TextValue is not string templateText
            || suppliedText is not null
                && !string.Equals(
                    suppliedText,
                    templateText,
                    StringComparison.Ordinal))
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        return templateText;
    }

    private static string ResolveEpisodeNumber(
        AppliedBrandTemplate template,
        string? suppliedText)
    {
        TemplateParameterAssignment? parameter = FindTemplateParameter(
            template,
            EpisodeNumberParameter);
        if (parameter?.Value.Kind != TemplateParameterValueKind.Integer
            || parameter.Value.IntegerValue is not long episodeNumber)
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        string resolved = episodeNumber.ToString(CultureInfo.InvariantCulture);
        if (suppliedText is not null
            && !string.Equals(
                suppliedText,
                resolved,
                StringComparison.Ordinal))
        {
            throw Failure(LongFormComposerFailureReason.TemplateMismatch);
        }

        return resolved;
    }

    private static TemplateParameterAssignment? FindTemplateParameter(
        AppliedBrandTemplate template,
        string parameterName) =>
        template.Parameters.SingleOrDefault(parameter =>
            string.Equals(
                parameter.Name,
                parameterName,
                StringComparison.Ordinal));

    private static OriginRef[] ValidateAndSortOrigins(
        IReadOnlyList<OriginRef> values,
        ValidatedProvenance provenance)
    {
        var unique = new HashSet<OriginRef>();
        var origins = new OriginRef[values.Count];
        for (int index = 0; index < origins.Length; index++)
        {
            OriginRef origin = values[index]
                ?? throw Failure(
                    LongFormComposerFailureReason.OriginEvidenceMissing);
            bool shapeIsValid = Enum.IsDefined(origin.Kind)
                && origin.Kind switch
                {
                    OriginKind.SourceSpan =>
                        origin.SourceSpanId is EntityId<SourceSpanIdTag>
                            sourceSpanId
                        && provenance.SourceSpans.ContainsKey(sourceSpanId)
                        && origin.AssetId is null
                        && origin.EditorialRecordId is null,
                    OriginKind.Asset =>
                        origin.SourceSpanId is null
                        && origin.AssetId is not null
                        && origin.EditorialRecordId is null,
                    OriginKind.EditorialRecord =>
                        origin.SourceSpanId is null
                        && origin.AssetId is null
                        && origin.EditorialRecordId is
                            EntityId<EditorialRecordIdTag> editorialRecordId
                        && provenance.EditorialRecordIds.Contains(
                            editorialRecordId),
                    _ => false,
                };
            if (!shapeIsValid || !unique.Add(origin))
            {
                throw Failure(
                    LongFormComposerFailureReason.OriginEvidenceMissing);
            }

            origins[index] = origin;
        }

        Array.Sort(
            origins,
            static (left, right) =>
            {
                int kind = left.Kind.CompareTo(right.Kind);
                return kind != 0
                    ? kind
                    : StringComparer.Ordinal.Compare(
                        GetOriginId(left),
                        GetOriginId(right));
            });
        return origins;
    }

    private static LongFormAssetSlot[] ResolveElementAssets(
        LongFormElementInput input,
        IReadOnlyList<LongFormAssetRole> expectedRoles,
        IReadOnlyDictionary<EntityId<AssetIdTag>, Asset> projectAssets,
        AppliedBrandTemplate template,
        IReadOnlyList<OriginRef> origins,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<LongFormAssetRole, LongFormAssetReference>();
        foreach (LongFormAssetReference reference in input.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference is null
                || !Enum.IsDefined(reference.Role)
                || !expectedRoles.Contains(reference.Role)
                || !references.TryAdd(reference.Role, reference))
            {
                throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
            }
        }

        foreach (LongFormAssetRole role in expectedRoles)
        {
            string? parameterName = GetTemplateAssetParameterName(role);
            TemplateParameterAssignment? parameter = parameterName is null
                ? null
                : FindTemplateParameter(template, parameterName);
            if (parameter is null)
            {
                continue;
            }

            if (parameter.Value.Kind
                    != TemplateParameterValueKind.AssetReference
                || parameter.Value.AssetId is not EntityId<AssetIdTag>
                    templateAssetId
                || parameter.Value.AssetDigest is not ContentDigest
                    templateAssetDigest
                || !references.TryGetValue(
                    role,
                    out LongFormAssetReference? reference)
                || reference.AssetId != templateAssetId
                || reference.AssetDigest != templateAssetDigest)
            {
                throw Failure(LongFormComposerFailureReason.TemplateMismatch);
            }
        }

        foreach (OriginRef origin in origins)
        {
            if (origin.Kind == OriginKind.Asset
                && !references.Values.Any(reference =>
                    reference.AssetId == origin.AssetId))
            {
                throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
            }
        }

        var slots = new LongFormAssetSlot[expectedRoles.Count];
        for (int index = 0; index < expectedRoles.Count; index++)
        {
            LongFormAssetRole role = expectedRoles[index];
            if (!references.TryGetValue(role, out LongFormAssetReference? reference))
            {
                slots[index] = new LongFormAssetSlot(
                    role,
                    LongFormAssetSlotState.Omitted,
                    asset: null,
                    origin: null);
                continue;
            }

            if (!projectAssets.TryGetValue(reference.AssetId, out Asset? asset)
                || asset.ContentDigest != reference.AssetDigest)
            {
                throw Failure(LongFormComposerFailureReason.AssetBindingMismatch);
            }

            OriginRef? origin = origins.SingleOrDefault(candidate =>
                candidate.Kind == OriginKind.Asset
                && candidate.AssetId == reference.AssetId);
            if (origin is null)
            {
                throw Failure(LongFormComposerFailureReason.OriginEvidenceMissing);
            }

            slots[index] = new LongFormAssetSlot(
                role,
                LongFormAssetSlotState.Included,
                asset,
                origin);
        }

        return slots;
    }

    private static string? GetTemplateAssetParameterName(
        LongFormAssetRole role) =>
        role switch
        {
            LongFormAssetRole.KeyVisual => KeyVisualParameter,
            LongFormAssetRole.Logo => LogoParameter,
            LongFormAssetRole.Jingle => JingleParameter,
            _ => null,
        };

    private static LongFormAssetSlot[] CreateOmittedAssetSlots(
        IReadOnlyList<LongFormAssetRole> roles) =>
        roles.Select(static role => new LongFormAssetSlot(
                role,
                LongFormAssetSlotState.Omitted,
                asset: null,
                origin: null))
            .ToArray();

    private static LongFormAssetRole[] GetExpectedAssetRoles(
        LongFormElementKind kind) =>
        kind switch
        {
            LongFormElementKind.Opening => OpeningAssetRoles,
            LongFormElementKind.Body => BodyAssetRoles,
            LongFormElementKind.Ending => EndingAssetRoles,
            _ => [],
        };

    private static void ValidateImportantStatementOrigins(
        LongFormElementPlan element)
    {
        if (element.State != LongFormElementState.Included
            || element.TopicMetadata is not TopicMetadataRevision metadata)
        {
            return;
        }

        foreach (EntityId<SourceSpanIdTag> sourceSpanId in
            metadata.Metadata.ImportantStatementSourceSpanIds)
        {
            if (!element.Origins.Any(origin =>
                origin.Kind == OriginKind.SourceSpan
                && origin.SourceSpanId == sourceSpanId))
            {
                throw Failure(
                    LongFormComposerFailureReason.OriginEvidenceMissing);
            }
        }
    }

    private static void ValidateElementIds(
        IReadOnlyList<LongFormElementPlan> elements)
    {
        var ids = new HashSet<EntityId<LongFormElementIdTag>>();
        if (elements.Any(element => !ids.Add(element.Id)))
        {
            throw Failure(LongFormComposerFailureReason.DuplicateElementId);
        }
    }

    private static void ValidateBodyAndTopicState(
        LongFormElementPlan body,
        IReadOnlyList<LongFormElementPlan> elements)
    {
        if (body.State == LongFormElementState.Omitted
            && elements.Any(element =>
                element.Kind == LongFormElementKind.Topic
                && element.State == LongFormElementState.Included))
        {
            throw Failure(LongFormComposerFailureReason.InvalidElement);
        }
    }

    private static void ValidateExplicitTiming(
        IReadOnlyList<LongFormElementPlan> elements)
    {
        RationalTime? previousEnd = null;
        foreach (LongFormElementPlan element in elements.Where(
            static candidate => candidate.Kind != LongFormElementKind.Topic))
        {
            if (element.State != LongFormElementState.Included
                || element.OutputRange is not TimeRange range)
            {
                continue;
            }

            if (range.Start < default(RationalTime)
                || range.Duration <= default(RationalTime)
                || previousEnd is RationalTime previous
                    && range.Start < previous)
            {
                throw Failure(LongFormComposerFailureReason.TimingConflict);
            }

            previousEnd = range.End;
        }

        LongFormElementPlan body = elements.Single(
            static element => element.Kind == LongFormElementKind.Body);
        RationalTime? previousTopicEnd = null;
        foreach (LongFormElementPlan topic in elements.Where(
            static element =>
                element.Kind == LongFormElementKind.Topic
                && element.State == LongFormElementState.Included))
        {
            if (topic.OutputRange is not TimeRange range)
            {
                continue;
            }

            if (previousTopicEnd is RationalTime previous && range.Start < previous
                || body.OutputRange is TimeRange bodyRange
                    && (range.Start < bodyRange.Start || range.End > bodyRange.End))
            {
                throw Failure(LongFormComposerFailureReason.TimingConflict);
            }

            previousTopicEnd = range.End;
        }
    }

    private static Asset[] CollectReferencedAssets(
        IReadOnlyList<LongFormElementPlan> elements)
    {
        var assets = new Dictionary<EntityId<AssetIdTag>, Asset>();
        foreach (LongFormElementPlan element in elements)
        {
            foreach (LongFormAssetSlot slot in element.AssetSlots)
            {
                if (slot.Asset is Asset asset)
                {
                    assets[asset.Id] = asset;
                }
            }
        }

        return assets.Values
            .OrderBy(static asset => asset.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static LongFormWarning[] BuildWarnings(
        LongFormCompositionRequest request,
        IReadOnlyList<LongFormElementPlan> elements,
        IReadOnlyList<Asset> referencedAssets,
        CancellationToken cancellationToken)
    {
        var warnings = new List<LongFormWarning>();
        foreach (LongFormElementPlan element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.State == LongFormElementState.Omitted)
            {
                warnings.Add(new LongFormWarning(
                    LongFormWarningCode.ElementOmitted,
                    element.Id));
                continue;
            }

            if (element.OutputRange is null)
            {
                warnings.Add(new LongFormWarning(
                    LongFormWarningCode.TimingUnspecified,
                    element.Id));
            }

            foreach (LongFormAssetSlot slot in element.AssetSlots)
            {
                if (slot.State == LongFormAssetSlotState.Omitted
                    && slot.Role != LongFormAssetRole.BodyMedia)
                {
                    warnings.Add(new LongFormWarning(
                        LongFormWarningCode.OptionalAssetOmitted,
                        element.Id,
                        assetRole: slot.Role));
                }
            }
        }

        foreach (Asset asset in referencedAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (asset.Rights.Status)
            {
                case RightsStatus.Unconfirmed:
                    warnings.Add(new LongFormWarning(
                        LongFormWarningCode.AssetRightsUnconfirmed,
                        assetId: asset.Id));
                    break;
                case RightsStatus.Rejected:
                    warnings.Add(new LongFormWarning(
                        LongFormWarningCode.AssetRightsRejected,
                        assetId: asset.Id));
                    break;
                case RightsStatus.Approved:
                    break;
                default:
                    throw Failure(LongFormComposerFailureReason.ContextMismatch);
            }

            if (!asset.Rights.ExternalPublicationAllowed)
            {
                warnings.Add(new LongFormWarning(
                    LongFormWarningCode.AssetExternalPublicationNotAllowed,
                    assetId: asset.Id));
            }
        }

        if (request.SpeakerAssignments is SpeakerAssignmentHistory history)
        {
            foreach (SpeakerAssignment assignment in
                history.CurrentRevision.Assignments)
            {
                if (assignment.TitleOrAffiliationConfirmationRequired)
                {
                    warnings.Add(new LongFormWarning(
                        LongFormWarningCode
                            .PerformerTitleOrAffiliationUnconfirmed,
                        performerId: assignment.PerformerId));
                }
            }
        }

        if (request.Project.PublicationScope
            != PublicationScope.ExternalPublicationApproved)
        {
            warnings.Add(new LongFormWarning(
                LongFormWarningCode.PublicationScopeNotApproved));
        }

        if (string.Equals(
            request.Profile.TemplateStatus,
            DevelopmentTemplateStatus,
            StringComparison.Ordinal))
        {
            warnings.Add(new LongFormWarning(
                LongFormWarningCode.DevelopmentTemplateNotApproved));
        }

        if (!request.Profile.PlatformSafeZonesMeasured)
        {
            warnings.Add(new LongFormWarning(
                LongFormWarningCode.PlatformSafeZonesUnmeasured));
        }

        foreach (string limitation in request.Profile.Unmeasured)
        {
            warnings.Add(new LongFormWarning(
                LongFormWarningCode.ProfileLimitationUnmeasured,
                limitation: limitation));
        }

        return warnings.ToArray();
    }

    private static ContentDigest ComputePlanDigest(
        LongFormCompositionRequest request,
        IReadOnlyList<LongFormElementPlan> elements,
        IReadOnlyList<LongFormTemplateAssetSnapshot> templateAssets,
        IReadOnlyList<Asset> referencedAssets,
        IReadOnlyList<LongFormWarning> warnings,
        bool timingComplete,
        bool externalRightsReady,
        bool externalPublicationReady)
    {
        object payload = new
        {
            schemaVersion = DigestSchemaVersion,
            planId = request.PlanId.ToString(),
            project = ProjectProjection(request.Project),
            provenance = ProvenanceProjection(request.Provenance),
            template = TemplateProjection(request.Template),
            profile = ProfileProjection(request.Profile),
            destination = (int)request.Destination,
            speakerAssignments = SpeakerHistoryProjection(
                request.SpeakerAssignments),
            elements = elements.Select(ElementProjection).ToArray(),
            templateAssets = templateAssets.Select(asset => new
            {
                asset.ParameterName,
                asset = AssetProjection(asset.Asset),
                origin = OriginProjection(asset.Origin),
            }).ToArray(),
            referencedAssets = referencedAssets
                .Select(AssetProjection)
                .ToArray(),
            warnings = warnings.Select(WarningProjection).ToArray(),
            timingComplete,
            externalRightsReady,
            externalPublicationReady,
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            DigestJsonOptions);
        return CanonicalJson.ComputeDigest(json);
    }

    private static object ProvenanceProjection(
        LongFormProvenanceSnapshot provenance) => new
        {
            projectId = provenance.ProjectId.ToString(),
            transcriptRevisionId =
                provenance.TranscriptRevisionId.ToString(),
            transcriptRevisionDigest = provenance.TranscriptRevisionDigest.Hex,
            editorialRecordEvidenceModel =
                LongFormProvenanceSnapshot.EditorialRecordEvidenceModel,
            sourceSpans = provenance.SourceSpans
                .OrderBy(
                    static span => span.Id.ToString(),
                    StringComparer.Ordinal)
                .Select(SourceSpanProjection)
                .ToArray(),
            trustedEditorialRecordIds = provenance.TrustedEditorialRecordIds
                .OrderBy(
                    static id => id.ToString(),
                    StringComparer.Ordinal)
                .Select(static id => id.ToString())
                .ToArray(),
        };

    private static object SourceSpanProjection(SourceSpan sourceSpan) => new
    {
        id = sourceSpan.Id.ToString(),
        assetId = sourceSpan.AssetId.ToString(),
        sourceSpan.StreamId,
        range = TimeRangeProjection(sourceSpan.Range),
        sourceDigest = sourceSpan.SourceDigest.Hex,
        transcriptRevisionId =
            sourceSpan.TranscriptRevisionId?.ToString(),
        transcriptTokenIds = sourceSpan.TranscriptTokenIds
            .Select(static id => id.ToString())
            .ToArray(),
    };

    private static object ProjectProjection(Project project) => new
    {
        id = project.Id.ToString(),
        project.ContentName,
        project.Purpose,
        project.Audience,
        publicationScope = (int)project.PublicationScope,
        deadline = FormatTimestamp(project.Deadline),
        assets = project.Assets
            .OrderBy(static asset => asset.Id.ToString(), StringComparer.Ordinal)
            .Select(AssetProjection)
            .ToArray(),
        performers = project.Performers
            .OrderBy(
                static performer => performer.Id.ToString(),
                StringComparer.Ordinal)
            .Select(static performer => new
            {
                id = performer.Id.ToString(),
                performer.DisplayName,
                performer.Role,
                titleConfirmedAt = FormatTimestamp(
                    performer.TitleConfirmedAt),
            })
            .ToArray(),
    };

    private static object AssetProjection(Asset asset) => new
    {
        id = asset.Id.ToString(),
        asset.FileName,
        asset.MediaType,
        contentDigest = asset.ContentDigest.Hex,
        asset.ByteSize,
        rights = new
        {
            status = (int)asset.Rights.Status,
            asset.Rights.EvidenceReference,
            asset.Rights.AllowedUse,
            asset.Rights.ExternalPublicationAllowed,
        },
    };

    private static object TemplateProjection(AppliedBrandTemplate template) =>
        new
        {
            seriesId = template.SeriesId.ToString(),
            reference = new
            {
                id = template.Reference.Id.ToString(),
                template.Reference.Name,
                versionDigest = template.Reference.VersionDigest.Hex,
            },
            kind = (int)template.Kind,
            parameters = template.Parameters
                .OrderBy(
                    static parameter => parameter.Name,
                    StringComparer.Ordinal)
                .Select(parameter => new
                {
                    parameter.Name,
                    value = TemplateValueProjection(parameter.Value),
                })
                .ToArray(),
        };

    private static object TemplateValueProjection(
        TemplateParameterValue value) =>
        value.Kind switch
        {
            TemplateParameterValueKind.Text => new
            {
                kind = (int)value.Kind,
                value = (object?)value.TextValue,
            },
            TemplateParameterValueKind.Integer => new
            {
                kind = (int)value.Kind,
                value = (object?)value.IntegerValue,
            },
            TemplateParameterValueKind.Boolean => new
            {
                kind = (int)value.Kind,
                value = (object?)value.BooleanValue,
            },
            TemplateParameterValueKind.Rational => new
            {
                kind = (int)value.Kind,
                value = RationalProjection(value.RationalValue!.Value),
            },
            TemplateParameterValueKind.AssetReference => new
            {
                kind = (int)value.Kind,
                value = new
                {
                    assetId = value.AssetId!.ToString(),
                    assetDigest = value.AssetDigest!.Hex,
                },
            },
            _ => throw Failure(LongFormComposerFailureReason.TemplateMismatch),
        };

    private static object ProfileProjection(
        BundledRenderProfileDefinition definition) =>
        new
        {
            profile = new
            {
                id = definition.Profile.Id.ToString(),
                definition.Profile.Name,
                profileDigest = definition.Profile.ProfileDigest.Hex,
                definition.Profile.Width,
                definition.Profile.Height,
                frameRate = RationalProjection(definition.Profile.FrameRate),
                layoutSpecId = definition.Profile.LayoutSpecId.ToString(),
                templateId = definition.Profile.TemplateId.ToString(),
                templateVersionDigest =
                    definition.Profile.TemplateVersionDigest.Hex,
                definition.Profile.VideoCodec,
                definition.Profile.AudioCodec,
                definition.Profile.SubtitleBurnIn,
            },
            layout = new
            {
                id = definition.Layout.Id.ToString(),
                definition.Layout.OutputAspectWidth,
                definition.Layout.OutputAspectHeight,
                regions = definition.Layout.Regions.Select(region => new
                {
                    region.Name,
                    role = (int)region.Role,
                    rect = RectProjection(region.Rect),
                }).ToArray(),
                safeZones = definition.Layout.SafeZones.Select(zone => new
                {
                    zone.Name,
                    rect = RectProjection(zone.Rect),
                }).ToArray(),
            },
            layoutDigest = definition.LayoutDigest.Hex,
            definition.PixelFormatEvidence,
            capabilities = definition.Capabilities.ToArray(),
            definition.PlatformSafeZonesMeasured,
            definition.EvidenceSource,
            definition.TemplateStatus,
            definition.RequiredExternalBrandAssets,
            unmeasured = definition.Unmeasured.ToArray(),
        };

    private static object? SpeakerHistoryProjection(
        SpeakerAssignmentHistory? history) =>
        history is null
            ? null
            : new
            {
                projectId = history.ProjectId.ToString(),
                transcriptRevisionId = history.TranscriptRevisionId.ToString(),
                transcriptRevisionDigest = history.TranscriptRevisionDigest.Hex,
                revisions = history.Revisions.Select(revision => new
                {
                    id = revision.Id.ToString(),
                    parentRevisionId = revision.ParentRevisionId?.ToString(),
                    projectId = revision.ProjectId.ToString(),
                    transcriptRevisionId =
                        revision.TranscriptRevisionId.ToString(),
                    transcriptRevisionDigest =
                        revision.TranscriptRevisionDigest.Hex,
                    createdAt = FormatTimestamp(revision.CreatedAt),
                    revision.ActorSid,
                    revision.Reason,
                    assignments = revision.Assignments
                        .Select(SpeakerAssignmentProjection)
                        .ToArray(),
                    change = new
                    {
                        speakerId = revision.Change.SpeakerId.ToString(),
                        previous = revision.Change.PreviousAssignment is null
                            ? null
                            : SpeakerAssignmentProjection(
                                revision.Change.PreviousAssignment),
                        current = revision.Change.CurrentAssignment is null
                            ? null
                            : SpeakerAssignmentProjection(
                                revision.Change.CurrentAssignment),
                    },
                }).ToArray(),
            };

    private static object SpeakerAssignmentProjection(
        SpeakerAssignment assignment) => new
        {
            speakerId = assignment.SpeakerId.ToString(),
            assignment.AnonymousLabel,
            performerId = assignment.PerformerId.ToString(),
            assignment.FullName,
            assignment.Surname,
            assignment.Role,
            titleConfirmedAt = FormatTimestamp(assignment.TitleConfirmedAt),
            displayFormat = (int)assignment.DisplayFormat,
        };

    private static object ElementProjection(LongFormElementPlan element) => new
    {
        id = element.Id.ToString(),
        kind = (int)element.Kind,
        state = (int)element.State,
        element.Text,
        outputRange = TimeRangeProjection(element.OutputRange),
        origins = element.Origins.Select(OriginProjection).ToArray(),
        assetSlots = element.AssetSlots.Select(slot => new
        {
            role = (int)slot.Role,
            state = (int)slot.State,
            asset = slot.Asset is null ? null : AssetProjection(slot.Asset),
            origin = slot.Origin is null ? null : OriginProjection(slot.Origin),
        }).ToArray(),
        topicRange = element.TopicRange is null
            ? null
            : TopicRangeProjection(element.TopicRange),
        topicMetadata = element.TopicMetadata is null
            ? null
            : TopicMetadataProjection(element.TopicMetadata),
    };

    private static object TopicRangeProjection(TopicRangeRevision range) => new
    {
        topicRangeId = range.TopicRangeId.ToString(),
        id = range.Id.ToString(),
        parentRevisionId = range.ParentRevisionId?.ToString(),
        sourceRevisionId = range.SourceRevisionId.ToString(),
        sourceRevisionDigest = range.SourceRevisionDigest.Hex,
        startBoundarySuggestionId = range.StartBoundarySuggestionId?.ToString(),
        endBoundarySuggestionId = range.EndBoundarySuggestionId?.ToString(),
        range = TimeRangeProjection(range.Range),
        previousRange = TimeRangeProjection(range.PreviousRange),
        range.ActorSid,
        createdAt = FormatTimestamp(range.CreatedAt),
        range.Reason,
    };

    private static object TopicMetadataProjection(
        TopicMetadataRevision revision) => new
        {
            topicMetadataId = revision.TopicMetadataId.ToString(),
            id = revision.Id.ToString(),
            parentRevisionId = revision.ParentRevisionId?.ToString(),
            projectId = revision.ProjectId.ToString(),
            topicRangeId = revision.TopicRangeId.ToString(),
            topicRangeRevisionId = revision.TopicRangeRevisionId.ToString(),
            topicRange = TimeRangeProjection(revision.TopicRange),
            sourceRevisionId = revision.SourceRevisionId.ToString(),
            sourceRevisionDigest = revision.SourceRevisionDigest.Hex,
            metadata = new
            {
                revision.Metadata.Title,
                titleOrigin = (int)revision.Metadata.TitleOrigin,
                revision.Metadata.TitleRequiresManualReview,
                revision.Metadata.Summary,
                summaryOrigin = (int)revision.Metadata.SummaryOrigin,
                mainSpeaker = revision.Metadata.MainSpeaker is null
                    ? null
                    : new
                    {
                        speakerId =
                            revision.Metadata.MainSpeaker.SpeakerId.ToString(),
                        performerId = revision.Metadata.MainSpeaker.PerformerId
                            ?.ToString(),
                        revision.Metadata.MainSpeaker.DisplayVisible,
                        revision.Metadata.MainSpeaker.DisplayText,
                        revision.Metadata.MainSpeaker
                            .TitleOrAffiliationConfirmationRequired,
                        overlapDuration = RationalTimeProjection(
                            revision.Metadata.MainSpeaker.OverlapDuration),
                    },
                mainSpeakerOrigin =
                    (int)revision.Metadata.MainSpeakerOrigin,
                revision.Metadata.MainSpeakerTieUnresolved,
                keywords = revision.Metadata.Keywords.ToArray(),
                keywordsOrigin = (int)revision.Metadata.KeywordsOrigin,
                importantStatementSourceSpanIds = revision.Metadata
                    .ImportantStatementSourceSpanIds
                    .Select(static id => id.ToString())
                    .ToArray(),
                importantStatementsOrigin =
                    (int)revision.Metadata.ImportantStatementsOrigin,
                revision.Metadata.Audience,
                audienceOrigin = (int)revision.Metadata.AudienceOrigin,
                publicationCautions =
                    revision.Metadata.PublicationCautions.ToArray(),
                publicationCautionsOrigin =
                    (int)revision.Metadata.PublicationCautionsOrigin,
            },
            changedField = (int)revision.ChangedField,
            revision.ActorSid,
            createdAt = FormatTimestamp(revision.CreatedAt),
            revision.Reason,
        };

    private static object WarningProjection(LongFormWarning warning) => new
    {
        code = (int)warning.Code,
        elementId = warning.ElementId?.ToString(),
        assetId = warning.AssetId?.ToString(),
        performerId = warning.PerformerId?.ToString(),
        assetRole = warning.AssetRole is LongFormAssetRole role
            ? (int?)role
            : null,
        warning.Limitation,
    };

    private static object OriginProjection(OriginRef origin) => new
    {
        kind = (int)origin.Kind,
        id = GetOriginId(origin),
    };

    private static string GetOriginId(OriginRef origin) =>
        origin.Kind switch
        {
            OriginKind.SourceSpan => origin.SourceSpanId!.ToString(),
            OriginKind.Asset => origin.AssetId!.ToString(),
            OriginKind.EditorialRecord => origin.EditorialRecordId!.ToString(),
            _ => throw Failure(
                LongFormComposerFailureReason.OriginEvidenceMissing),
        };

    private static object RectProjection(NormalizedRect rect) => new
    {
        x = RationalProjection(rect.X),
        y = RationalProjection(rect.Y),
        width = RationalProjection(rect.Width),
        height = RationalProjection(rect.Height),
    };

    private static object RationalProjection(RationalNumber value) => new
    {
        value.Numerator,
        value.Denominator,
    };

    private static object RationalTimeProjection(RationalTime value) => new
    {
        value.Value,
        value.Rate,
    };

    private static object? TimeRangeProjection(TimeRange? range) =>
        range is TimeRange value
            ? new
            {
                start = RationalTimeProjection(value.Start),
                duration = RationalTimeProjection(value.Duration),
            }
            : null;

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(LongFormComposerFailureReason.InvalidText);
        }

        _ = StrictUtf8.GetByteCount(value);
    }

    private static void ValidateOptionalText(string? value)
    {
        if (value is not null)
        {
            ValidateText(value);
        }
    }

    private static LongFormComposerException Failure(
        LongFormComposerFailureReason reason) =>
        new(reason);

    private sealed record ValidatedProvenance(
        IReadOnlyDictionary<EntityId<SourceSpanIdTag>, SourceSpan> SourceSpans,
        IReadOnlySet<EntityId<EditorialRecordIdTag>> EditorialRecordIds);
}
