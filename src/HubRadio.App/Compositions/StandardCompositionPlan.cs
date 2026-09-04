using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.Compositions;

public sealed class StandardCompositionPlanIdTag
{
}

public sealed class StandardShortFormPlanIdTag
{
}

public sealed class StandardThumbnailPlanIdTag
{
}

public enum StandardPlanInputKind
{
    SourceAsset,
    SourceFrameRate,
    ProjectSnapshot,
    ProjectStoreState,
    TranscriptRevision,
    TimelineMap,
    SettingsProfile,
    SettingsSnapshot,
    AutoDecision,
    LongFormPlan,
    Layout,
    Tool,
    Model,
    MusicCatalog,
    MusicPlanning,
    ThumbnailEvidence,
    EvaluationInput,
}

public sealed class StandardProjectStoreState
{
    public StandardProjectStoreState(
        int revision,
        ContentDigest payloadDigest,
        DateTimeOffset updatedAt)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        PayloadDigest = payloadDigest
            ?? throw new ArgumentNullException(nameof(payloadDigest));
        Revision = revision;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public int Revision { get; }

    public ContentDigest PayloadDigest { get; }

    public DateTimeOffset UpdatedAt { get; }
}

public enum StandardSubtitleVariantKind
{
    BurnedIn,
    SubtitleFree,
    SubRip,
    WebVtt,
}

public enum StandardCallToActionKind
{
    ViewLongForm,
}

public enum StandardCompositionArtifactKind
{
    ShortForm,
    Thumbnail,
}

public enum StandardCompositionOmissionCode
{
    Unspecified,
    NoGroundedCandidate,
    NoAdditionalGroundedCandidate,
    NoMeasuredSourceFrame,
    NoAdditionalMeasuredSourceFrame,
}

public enum StandardCompositionWarningCode
{
    SubjectRegionUnavailable,
    FixedLayoutFallback,
    BlurredBackgroundFallback,
    PlatformUiUnmeasured,
    CandidateLimitApplied,
    ThumbnailQualityNotEvaluated,
}

public enum StandardShortCandidateExclusionCode
{
    NotExplicitlyAdopted,
    StaleEvidence,
    UnmappedSourceRange,
    DurationOutsidePolicy,
    DuplicateSource,
    OverlappingSource,
    CandidateLimitApplied,
}

public sealed class StandardPlanInputDigest
{
    internal StandardPlanInputDigest(
        StandardPlanInputKind kind,
        ContentDigest contentDigest)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(contentDigest);
        Kind = kind;
        ContentDigest = contentDigest;
    }

    public StandardPlanInputKind Kind { get; }

    public ContentDigest ContentDigest { get; }
}

public sealed class StandardSubtitleVariantReference
{
    internal StandardSubtitleVariantReference(
        StandardSubtitleVariantKind kind,
        ContentDigest profileDigest)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(profileDigest);
        Kind = kind;
        ProfileDigest = profileDigest;
    }

    public StandardSubtitleVariantKind Kind { get; }

    public ContentDigest ProfileDigest { get; }
}

public sealed class StandardCallToActionReference
{
    internal StandardCallToActionReference(
        EntityId<LongFormPlanIdTag> targetLongFormPlanId,
        StandardCallToActionKind kind)
    {
        ArgumentNullException.ThrowIfNull(targetLongFormPlanId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        TargetLongFormPlanId = targetLongFormPlanId;
        Kind = kind;
    }

    public EntityId<LongFormPlanIdTag> TargetLongFormPlanId { get; }

    public StandardCallToActionKind Kind { get; }
}

public sealed class StandardShortHookReference
{
    internal StandardShortHookReference(
        SourceSpan sourceSpan,
        SuggestionReference adoptedHighlight)
    {
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(adoptedHighlight);
        if (adoptedHighlight.Origin.Kind != OriginKind.SourceSpan
            || adoptedHighlight.Origin.SourceSpanId != sourceSpan.Id)
        {
            throw new ArgumentException(
                "A short hook must retain the adopted highlight source span.",
                nameof(adoptedHighlight));
        }

        SourceSpan = sourceSpan;
        AdoptedHighlight = adoptedHighlight;
    }

    public SourceSpan SourceSpan { get; }

    public SuggestionReference AdoptedHighlight { get; }
}

public sealed class StandardShortFormPlan
{
    internal static readonly RationalTime MinimumDuration =
        new(15, 1);
    internal static readonly RationalTime MaximumDuration =
        new(60, 1);

    internal StandardShortFormPlan(
        EntityId<StandardShortFormPlanIdTag> id,
        IReadOnlyList<SourceSpan> sourceSpans,
        SuggestionReference adoptedHighlight,
        StandardShortHookReference? hook,
        ManualLayoutPlan layout,
        StandardCallToActionReference callToAction,
        IReadOnlyList<StandardSubtitleVariantReference> subtitleVariants)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(adoptedHighlight);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(callToAction);
        ArgumentNullException.ThrowIfNull(subtitleVariants);

        SourceSpan[] copiedSpans = CopySourceSpans(sourceSpans);
        ValidateTotalDuration(copiedSpans, nameof(sourceSpans));
        if (adoptedHighlight.Origin.Kind != OriginKind.SourceSpan
            || adoptedHighlight.Origin.SourceSpanId is null
            || copiedSpans.All(span =>
                span.Id != adoptedHighlight.Origin.SourceSpanId))
        {
            throw new ArgumentException(
                "A standard short form must retain its adopted highlight source reference.",
                nameof(adoptedHighlight));
        }

        if (hook is not null
            && (hook.AdoptedHighlight.SuggestionId
                    != adoptedHighlight.SuggestionId
                || hook.AdoptedHighlight.Origin != adoptedHighlight.Origin
                || copiedSpans.All(span =>
                    !SourceSpanContentEquals(span, hook.SourceSpan))))
        {
            throw new ArgumentException(
                "A short hook must be grounded by the selected short-form evidence.",
                nameof(hook));
        }

        StandardSubtitleVariantReference[] copiedSubtitles =
            CopySubtitleVariants(subtitleVariants);
        if (copiedSubtitles.Length != 1
            || copiedSubtitles[0].Kind != StandardSubtitleVariantKind.BurnedIn)
        {
            throw new ArgumentException(
                "A standard short form requires exactly one burned-in subtitle reference.",
                nameof(subtitleVariants));
        }

        if (!Enum.IsDefined(layout.Mode)
            || (long)layout.DerivedLayoutSpec.OutputAspectWidth * 16
                != (long)layout.DerivedLayoutSpec.OutputAspectHeight * 9)
        {
            throw new ArgumentException(
                "A standard short-form layout must be a defined 9:16 layout.",
                nameof(layout));
        }

        Id = id;
        SourceSpans = Array.AsReadOnly(copiedSpans);
        AdoptedHighlight = adoptedHighlight;
        Hook = hook;
        Layout = layout;
        CallToAction = callToAction;
        SubtitleVariants = Array.AsReadOnly(copiedSubtitles);
    }

    public EntityId<StandardShortFormPlanIdTag> Id { get; }

    public IReadOnlyList<SourceSpan> SourceSpans { get; }

    public SuggestionReference AdoptedHighlight { get; }

    public StandardShortHookReference? Hook { get; }

    public ManualLayoutPlan Layout { get; }

    public StandardCallToActionReference CallToAction { get; }

    public IReadOnlyList<StandardSubtitleVariantReference> SubtitleVariants
    { get; }

    private static SourceSpan[] CopySourceSpans(
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        if (sourceSpans.Count == 0)
        {
            throw new ArgumentException(
                "A standard short form requires source-span evidence.",
                nameof(sourceSpans));
        }

        SourceSpan[] copied = new SourceSpan[sourceSpans.Count];
        var ids = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw new ArgumentException(
                    "Short-form source spans must not contain null elements.",
                    nameof(sourceSpans));
            if (!ids.Add(span.Id)
                || span.Range.Duration <= default(RationalTime))
            {
                throw new ArgumentException(
                    "Short-form source spans must be unique and have positive duration.",
                    nameof(sourceSpans));
            }

            copied[index] = span;
        }

        Array.Sort(copied, CompareSourceSpans);
        SourceSpan first = copied[0];
        RationalTime previousEnd = first.Range.End;
        for (int index = 1; index < copied.Length; index++)
        {
            SourceSpan current = copied[index];
            if (current.AssetId != first.AssetId
                || !string.Equals(
                    current.StreamId,
                    first.StreamId,
                    StringComparison.Ordinal)
                || current.SourceDigest != first.SourceDigest
                || current.TranscriptRevisionId != first.TranscriptRevisionId
                || current.Range.Start < previousEnd)
            {
                throw new ArgumentException(
                    "Short-form source spans must be ordered non-overlapping evidence from one source revision.",
                    nameof(sourceSpans));
            }

            previousEnd = current.Range.End;
        }

        return copied;
    }

    private static int CompareSourceSpans(SourceSpan left, SourceSpan right)
    {
        int result = left.Range.Start.CompareTo(right.Range.Start);
        if (result == 0)
        {
            result = left.Range.End.CompareTo(right.Range.End);
        }

        return result != 0
            ? result
            : StringComparer.Ordinal.Compare(
                left.Id.ToString(),
                right.Id.ToString());
    }

    private static void ValidateTotalDuration(
        IReadOnlyList<SourceSpan> sourceSpans,
        string parameterName)
    {
        RationalTime total = default;
        try
        {
            foreach (SourceSpan span in sourceSpans)
            {
                if (span.Range.Duration > MaximumDuration)
                {
                    throw new ArgumentException(
                        "A standard short form must stay within the 15-to-60-second product range.",
                        parameterName);
                }

                total += span.Range.Duration;
            }
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "A standard short-form duration must be representable.",
                parameterName,
                exception);
        }

        if (total < MinimumDuration || total > MaximumDuration)
        {
            throw new ArgumentException(
                "A standard short form must stay within the 15-to-60-second product range.",
                parameterName);
        }
    }

    private static StandardSubtitleVariantReference[] CopySubtitleVariants(
        IReadOnlyList<StandardSubtitleVariantReference> subtitleVariants)
    {
        StandardSubtitleVariantReference[] copied =
            new StandardSubtitleVariantReference[subtitleVariants.Count];
        var kinds = new HashSet<StandardSubtitleVariantKind>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardSubtitleVariantReference reference =
                subtitleVariants[index]
                ?? throw new ArgumentException(
                    "Subtitle references must not contain null elements.",
                    nameof(subtitleVariants));
            if (!Enum.IsDefined(reference.Kind) || !kinds.Add(reference.Kind))
            {
                throw new ArgumentException(
                    "Subtitle reference kinds must be defined and unique.",
                    nameof(subtitleVariants));
            }

            copied[index] = reference;
        }

        Array.Sort(
            copied,
            static (left, right) => left.Kind.CompareTo(right.Kind));
        return copied;
    }

    private static bool SourceSpanContentEquals(
        SourceSpan left,
        SourceSpan right) =>
        left.Id == right.Id
        && left.AssetId == right.AssetId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.Range == right.Range
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && left.TranscriptTokenIds.SequenceEqual(right.TranscriptTokenIds);
}

public sealed class StandardThumbnailPlan
{
    internal StandardThumbnailPlan(
        EntityId<StandardThumbnailPlanIdTag> id,
        OriginRef origin,
        int sourceFrameSequence,
        RationalTime sourcePresentationTime)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Kind != OriginKind.Asset || origin.AssetId is null)
        {
            throw new ArgumentException(
                "A thumbnail frame must identify its exact source asset.",
                nameof(origin));
        }

        if (sourceFrameSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFrameSequence));
        }

        if (sourcePresentationTime < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePresentationTime));
        }

        Id = id;
        Origin = origin;
        SourceFrameSequence = sourceFrameSequence;
        SourcePresentationTime = sourcePresentationTime;
    }

    public EntityId<StandardThumbnailPlanIdTag> Id { get; }

    public OriginRef Origin { get; }

    public EntityId<AssetIdTag> SourceAssetId => Origin.AssetId!;

    public int SourceFrameSequence { get; }

    public RationalTime SourcePresentationTime { get; }
}

public sealed class StandardShortCandidateExclusion
{
    internal StandardShortCandidateExclusion(
        EntityId<SuggestionIdTag> candidateId,
        StandardShortCandidateExclusionCode code)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        CandidateId = candidateId;
        Code = code;
    }

    public EntityId<SuggestionIdTag> CandidateId { get; }

    public StandardShortCandidateExclusionCode Code { get; }
}

public sealed class StandardArtifactOmission
{
    internal StandardArtifactOmission(
        StandardCompositionArtifactKind artifactKind,
        StandardCompositionOmissionCode code)
    {
        if (!Enum.IsDefined(artifactKind))
        {
            throw new ArgumentOutOfRangeException(nameof(artifactKind));
        }

        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        bool valid = artifactKind switch
        {
            StandardCompositionArtifactKind.ShortForm =>
                code is StandardCompositionOmissionCode.NoGroundedCandidate
                    or StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
            StandardCompositionArtifactKind.Thumbnail =>
                code is StandardCompositionOmissionCode.NoMeasuredSourceFrame
                    or StandardCompositionOmissionCode
                        .NoAdditionalMeasuredSourceFrame,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The artifact omission code does not match its artifact kind.",
                nameof(code));
        }

        ArtifactKind = artifactKind;
        Code = code;
    }

    public StandardCompositionArtifactKind ArtifactKind { get; }

    public StandardCompositionOmissionCode Code { get; }
}

public sealed class StandardCompositionWarning
{
    internal StandardCompositionWarning(StandardCompositionWarningCode code)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
    }

    public StandardCompositionWarningCode Code { get; }
}

public sealed class StandardCompositionPlan
{
    public const int MaximumShortFormCount = 3;
    public const int MaximumThumbnailCount = 3;

    internal StandardCompositionPlan(
        EntityId<StandardCompositionPlanIdTag> id,
        LongFormCompositionPlan longForm,
        BundledRenderProfileDefinition shortFormProfile,
        StandardProjectStoreState projectStoreState,
        MusicPlanningResult musicPlanning,
        IReadOnlyList<StandardShortFormPlan> shortForms,
        IReadOnlyList<StandardShortCandidateExclusion>
            shortCandidateExclusions,
        IReadOnlyList<StandardThumbnailPlan> thumbnails,
        IReadOnlyList<StandardSubtitleVariantReference>
            longFormSubtitleVariants,
        IReadOnlyList<StandardArtifactOmission> omissions,
        IReadOnlyList<StandardCompositionWarning> warnings,
        IReadOnlyList<StandardPlanInputDigest> inputDigests,
        ContentDigest planDigest)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(longForm);
        ArgumentNullException.ThrowIfNull(shortFormProfile);
        ArgumentNullException.ThrowIfNull(projectStoreState);
        ArgumentNullException.ThrowIfNull(musicPlanning);
        ArgumentNullException.ThrowIfNull(shortForms);
        ArgumentNullException.ThrowIfNull(shortCandidateExclusions);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(longFormSubtitleVariants);
        ArgumentNullException.ThrowIfNull(omissions);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(inputDigests);
        ArgumentNullException.ThrowIfNull(planDigest);

        if (longForm.Project.PublicationScope != PublicationScope.InternalOnly
            || longForm.Destination
                != LongFormDestination.InternalDistribution
            || longForm.ExternalPublicationReady)
        {
            throw new ArgumentException(
                "A standard composition plan must retain an internal-only long form.",
                nameof(longForm));
        }

        if (longForm.Profile.Profile.Width != 1920
            || longForm.Profile.Profile.Height != 1080
            || (long)longForm.Profile.Layout.OutputAspectWidth * 9
            != (long)longForm.Profile.Layout.OutputAspectHeight * 16)
        {
            throw new ArgumentException(
                "A standard composition plan requires a 1920x1080 long-form profile.",
                nameof(longForm));
        }

        if (shortFormProfile.Profile.Width != 1080
            || shortFormProfile.Profile.Height != 1920
            || !shortFormProfile.Profile.SubtitleBurnIn
            || shortFormProfile.Profile.FrameRate
                != longForm.Profile.Profile.FrameRate
            || shortFormProfile.Layout.Id
                != shortFormProfile.Profile.LayoutSpecId
            || (long)shortFormProfile.Layout.OutputAspectWidth * 16
                != (long)shortFormProfile.Layout.OutputAspectHeight * 9)
        {
            throw new ArgumentException(
                "A standard composition plan requires a 1080x1920 burned-in short-form profile.",
                nameof(shortFormProfile));
        }

        ValidateLongFormContext(longForm);

        StandardShortFormPlan[] copiedShorts = CopyShortForms(shortForms);
        StandardShortCandidateExclusion[] copiedExclusions =
            CopyShortCandidateExclusions(shortCandidateExclusions);
        StandardThumbnailPlan[] copiedThumbnails = CopyThumbnails(thumbnails);
        MusicPlanningResult copiedMusicPlanning = CopyMusicPlanning(
            musicPlanning);
        StandardSubtitleVariantReference[] copiedSubtitles =
            CopyLongFormSubtitles(longFormSubtitleVariants);
        StandardArtifactOmission[] copiedOmissions = CopyOmissions(omissions);
        StandardCompositionWarning[] copiedWarnings = CopyWarnings(warnings);
        StandardPlanInputDigest[] copiedInputs = CopyInputs(inputDigests);

        ValidateOutputBindings(longForm, copiedShorts, copiedThumbnails);
        ValidateInputBindings(
            longForm,
            projectStoreState,
            copiedMusicPlanning,
            copiedInputs);

        StandardArtifactOmission? shortOmission = copiedOmissions.SingleOrDefault(
            static omission =>
            omission.ArtifactKind
                == StandardCompositionArtifactKind.ShortForm);
        StandardArtifactOmission? thumbnailOmission = copiedOmissions
            .SingleOrDefault(static omission =>
                omission.ArtifactKind
                    == StandardCompositionArtifactKind.Thumbnail);
        StandardCompositionOmissionCode? requiredShortOmission =
            copiedShorts.Length switch
            {
                0 => StandardCompositionOmissionCode.NoGroundedCandidate,
                < MaximumShortFormCount =>
                    StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
                _ => null,
            };
        StandardCompositionOmissionCode? requiredThumbnailOmission =
            copiedThumbnails.Length switch
            {
                0 => StandardCompositionOmissionCode.NoMeasuredSourceFrame,
                < MaximumThumbnailCount => StandardCompositionOmissionCode
                    .NoAdditionalMeasuredSourceFrame,
                _ => null,
            };
        if (shortOmission?.Code != requiredShortOmission
            || thumbnailOmission?.Code != requiredThumbnailOmission
            || copiedShorts.Any(shortForm =>
                shortForm.CallToAction.Kind
                    != StandardCallToActionKind.ViewLongForm
                || shortForm.CallToAction.TargetLongFormPlanId
                    != longForm.PlanId))
        {
            throw new ArgumentException(
                "Standard composition outputs, omissions, and long-form references are inconsistent.",
                nameof(omissions));
        }

        ContentDigest expectedPlanDigest =
            StandardCompositionCanonical.ComputePlanDigest(
            id,
            longForm,
            shortFormProfile,
            copiedShorts,
            copiedExclusions,
            copiedThumbnails,
            copiedSubtitles,
            copiedOmissions,
            copiedWarnings,
            copiedInputs);
        if (planDigest != expectedPlanDigest)
        {
            throw new ArgumentException(
                "The standard composition plan digest does not match its canonical content.",
                nameof(planDigest));
        }

        Id = id;
        ProjectId = longForm.Project.Id;
        LongForm = longForm;
        ShortFormProfile = shortFormProfile;
        ProjectStoreState = projectStoreState;
        MusicPlanning = copiedMusicPlanning;
        ShortForms = Array.AsReadOnly(copiedShorts);
        ShortCandidateExclusions = Array.AsReadOnly(copiedExclusions);
        Thumbnails = Array.AsReadOnly(copiedThumbnails);
        LongFormSubtitleVariants = Array.AsReadOnly(copiedSubtitles);
        Omissions = Array.AsReadOnly(copiedOmissions);
        Warnings = Array.AsReadOnly(copiedWarnings);
        InputDigests = Array.AsReadOnly(copiedInputs);
        PlanDigest = planDigest;
    }

    public EntityId<StandardCompositionPlanIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public LongFormCompositionPlan LongForm { get; }

    public BundledRenderProfileDefinition ShortFormProfile { get; }

    public StandardProjectStoreState ProjectStoreState { get; }

    public MusicPlanningResult MusicPlanning { get; }

    public IReadOnlyList<StandardShortFormPlan> ShortForms { get; }

    public IReadOnlyList<StandardShortCandidateExclusion>
        ShortCandidateExclusions
    { get; }

    public IReadOnlyList<StandardThumbnailPlan> Thumbnails { get; }

    public IReadOnlyList<StandardSubtitleVariantReference>
        LongFormSubtitleVariants
    { get; }

    public IReadOnlyList<StandardArtifactOmission> Omissions { get; }

    public IReadOnlyList<StandardCompositionWarning> Warnings { get; }

    public IReadOnlyList<StandardPlanInputDigest> InputDigests { get; }

    public ContentDigest PlanDigest { get; }

    private static void ValidateLongFormContext(
        LongFormCompositionPlan longForm)
    {
        if (longForm.Provenance.ProjectId != longForm.Project.Id)
        {
            throw new ArgumentException(
                "The long-form project and provenance snapshot must match.",
                nameof(longForm));
        }

        Dictionary<EntityId<AssetIdTag>, Asset> projectAssets =
            longForm.Project.Assets.ToDictionary(static asset => asset.Id);
        var sourceSpanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        foreach (SourceSpan span in longForm.Provenance.SourceSpans)
        {
            if (span is null
                || !sourceSpanIds.Add(span.Id)
                || !projectAssets.TryGetValue(span.AssetId, out Asset? asset)
                || asset.ContentDigest != span.SourceDigest
                || span.TranscriptRevisionId is not null
                    && span.TranscriptRevisionId
                        != longForm.Provenance.TranscriptRevisionId)
            {
                throw new ArgumentException(
                    "The long-form provenance snapshot is inconsistent with its project and transcript.",
                    nameof(longForm));
            }
        }
    }

    private static void ValidateOutputBindings(
        LongFormCompositionPlan longForm,
        IReadOnlyList<StandardShortFormPlan> shortForms,
        IReadOnlyList<StandardThumbnailPlan> thumbnails)
    {
        Dictionary<EntityId<SourceSpanIdTag>, SourceSpan> provenanceById =
            longForm.Provenance.SourceSpans.ToDictionary(static span => span.Id);
        var selectedSpans = new List<SelectedSourceSpan>();
        for (int shortIndex = 0; shortIndex < shortForms.Count; shortIndex++)
        {
            StandardShortFormPlan shortForm = shortForms[shortIndex];
            var selectedIds = new HashSet<EntityId<SourceSpanIdTag>>();
            var selectedAssetIds = new HashSet<EntityId<AssetIdTag>>();
            foreach (SourceSpan span in shortForm.SourceSpans)
            {
                bool grounded = provenanceById.TryGetValue(
                    span.Id,
                    out SourceSpan? exactEvidence)
                        ? SourceSpanContentEquals(span, exactEvidence)
                        : longForm.Provenance.SourceSpans.Any(evidence =>
                            SourceSpanContains(evidence, span));
                if (!grounded)
                {
                    throw new ArgumentException(
                        "Every short-form source span must be fully grounded by long-form provenance.",
                        nameof(shortForms));
                }

                selectedIds.Add(span.Id);
                selectedAssetIds.Add(span.AssetId);
                selectedSpans.Add(new SelectedSourceSpan(shortIndex, span));
            }

            foreach (ManualSourcePlacement placement in shortForm.Layout.SourcePlacements)
            {
                bool originMatches = placement.Origin.Kind switch
                {
                    OriginKind.Asset =>
                        placement.Origin.AssetId == placement.SourceAssetId,
                    OriginKind.SourceSpan =>
                        placement.Origin.SourceSpanId is not null
                        && selectedIds.Contains(placement.Origin.SourceSpanId),
                    _ => false,
                };
                if (!selectedAssetIds.Contains(placement.SourceAssetId)
                    || !originMatches)
                {
                    throw new ArgumentException(
                        "Every short-form layout placement must use its selected source evidence.",
                        nameof(shortForms));
                }
            }
        }

        for (int leftIndex = 0; leftIndex < selectedSpans.Count; leftIndex++)
        {
            SelectedSourceSpan left = selectedSpans[leftIndex];
            for (int rightIndex = leftIndex + 1;
                rightIndex < selectedSpans.Count;
                rightIndex++)
            {
                SelectedSourceSpan right = selectedSpans[rightIndex];
                if (left.ShortIndex == right.ShortIndex)
                {
                    continue;
                }

                if (SourceSpanContentEquals(left.Span, right.Span)
                    || HaveOverlappingSourceRange(left.Span, right.Span))
                {
                    throw new ArgumentException(
                        "Standard short forms must not duplicate or overlap source evidence.",
                        nameof(shortForms));
                }
            }
        }

        HashSet<EntityId<AssetIdTag>> projectAssetIds =
            longForm.Project.Assets.Select(static asset => asset.Id).ToHashSet();
        HashSet<EntityId<AssetIdTag>> provenanceAssetIds = longForm.Provenance
            .SourceSpans.Select(static span => span.AssetId).ToHashSet();
        foreach (StandardThumbnailPlan thumbnail in thumbnails)
        {
            if (!projectAssetIds.Contains(thumbnail.SourceAssetId)
                || !provenanceAssetIds.Contains(thumbnail.SourceAssetId))
            {
                throw new ArgumentException(
                    "Every thumbnail must identify source media in long-form provenance.",
                    nameof(thumbnails));
            }
        }
    }

    private static void ValidateInputBindings(
        LongFormCompositionPlan longForm,
        StandardProjectStoreState projectStoreState,
        MusicPlanningResult musicPlanning,
        IReadOnlyList<StandardPlanInputDigest> inputs)
    {
        StandardPlanInputKind[] requiredKinds =
            Enum.GetValues<StandardPlanInputKind>();
        if (inputs.Count != requiredKinds.Length
            || requiredKinds.Any(kind => inputs.All(input => input.Kind != kind)))
        {
            throw new ArgumentException(
                "A standard composition plan requires exactly one digest for every input kind.",
                nameof(inputs));
        }

        Dictionary<StandardPlanInputKind, ContentDigest> byKind =
            inputs.ToDictionary(
                static input => input.Kind,
                static input => input.ContentDigest);
        ContentDigest[] sourceDigests = longForm.Provenance.SourceSpans
            .Select(static span => span.SourceDigest)
            .Distinct()
            .ToArray();
        if (sourceDigests.Length != 1
            || byKind[StandardPlanInputKind.SourceAsset] != sourceDigests[0]
            || byKind[StandardPlanInputKind.SourceFrameRate]
                != StandardCompositionCanonical.ComputeDigest(
                    "hubradio.standard-source-frame-rate.v1",
                    [
                        longForm.Profile.Profile.FrameRateNumerator.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        longForm.Profile.Profile.FrameRateDenominator.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ])
            || byKind[StandardPlanInputKind.ProjectSnapshot]
                != StandardCompositionCanonical.ComputeProjectSnapshotDigest(
                    longForm.Project)
            || byKind[StandardPlanInputKind.ProjectStoreState]
                != StandardCompositionCanonical.ComputeProjectStoreStateDigest(
                    longForm.Project,
                    projectStoreState)
            || byKind[StandardPlanInputKind.TranscriptRevision]
                != longForm.Provenance.TranscriptRevisionDigest
            || byKind[StandardPlanInputKind.LongFormPlan]
                != longForm.PlanDigest
            || byKind[StandardPlanInputKind.MusicCatalog]
                != musicPlanning.CatalogDigest
            || byKind[StandardPlanInputKind.MusicPlanning]
                != StandardCompositionCanonical.ComputeMusicPlanningDigest(
                    musicPlanning))
        {
            throw new ArgumentException(
                "Standard plan input digests must match the long-form source, transcript, and plan.",
                nameof(inputs));
        }
    }

    private static bool SourceSpanContentEquals(
        SourceSpan left,
        SourceSpan right) =>
        left.AssetId == right.AssetId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.Range == right.Range
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && left.TranscriptTokenIds.SequenceEqual(right.TranscriptTokenIds);

    private static bool HaveOverlappingSourceRange(
        SourceSpan left,
        SourceSpan right) =>
        left.AssetId == right.AssetId
        && string.Equals(left.StreamId, right.StreamId, StringComparison.Ordinal)
        && left.SourceDigest == right.SourceDigest
        && left.TranscriptRevisionId == right.TranscriptRevisionId
        && left.Range.Start < right.Range.End
        && right.Range.Start < left.Range.End;

    private static bool SourceSpanContains(
        SourceSpan evidence,
        SourceSpan selected) =>
        selected.Id != evidence.Id
        &&
        evidence.AssetId == selected.AssetId
        && string.Equals(
            evidence.StreamId,
            selected.StreamId,
            StringComparison.Ordinal)
        && evidence.SourceDigest == selected.SourceDigest
        && evidence.TranscriptRevisionId == selected.TranscriptRevisionId
        && selected.Range.Start >= evidence.Range.Start
        && selected.Range.End <= evidence.Range.End
        && ContainsContiguousTokens(
            evidence.TranscriptTokenIds,
            selected.TranscriptTokenIds);

    private static bool ContainsContiguousTokens<T>(
        IReadOnlyList<T> evidence,
        IReadOnlyList<T> selected)
        where T : IEquatable<T>
    {
        if (selected.Count == 0 || selected.Count > evidence.Count)
        {
            return false;
        }

        for (int start = 0; start + selected.Count <= evidence.Count; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < selected.Count; offset++)
            {
                if (!evidence[start + offset].Equals(selected[offset]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static StandardShortFormPlan[] CopyShortForms(
        IReadOnlyList<StandardShortFormPlan> shortForms)
    {
        if (shortForms.Count > MaximumShortFormCount)
        {
            throw new ArgumentException(
                "A standard composition plan supports at most three short forms.",
                nameof(shortForms));
        }

        StandardShortFormPlan[] copied = new StandardShortFormPlan[
            shortForms.Count];
        var ids = new HashSet<EntityId<StandardShortFormPlanIdTag>>();
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copied.Length; index++)
        {
            StandardShortFormPlan shortForm = shortForms[index]
                ?? throw new ArgumentException(
                    "Short forms must not contain null elements.",
                    nameof(shortForms));
            string sourceKey = string.Join(
                "\0",
                shortForm.SourceSpans.Select(static span => span.Id.ToString()));
            if (!ids.Add(shortForm.Id) || !sourceKeys.Add(sourceKey))
            {
                throw new ArgumentException(
                    "Short forms must have unique identities and source evidence.",
                    nameof(shortForms));
            }

            copied[index] = shortForm;
        }

        Array.Sort(
            copied,
            static (left, right) =>
            {
                SourceSpan leftFirst = left.SourceSpans[0];
                SourceSpan rightFirst = right.SourceSpans[0];
                int result = leftFirst.Range.Start.CompareTo(
                    rightFirst.Range.Start);
                if (result == 0)
                {
                    result = left.SourceSpans[^1].Range.End.CompareTo(
                        right.SourceSpans[^1].Range.End);
                }

                return result != 0
                    ? result
                    : StringComparer.Ordinal.Compare(
                        left.Id.ToString(),
                        right.Id.ToString());
            });
        return copied;
    }

    private static StandardShortCandidateExclusion[]
        CopyShortCandidateExclusions(
            IReadOnlyList<StandardShortCandidateExclusion> exclusions)
    {
        StandardShortCandidateExclusion[] copied =
            new StandardShortCandidateExclusion[exclusions.Count];
        var candidateIds = new HashSet<EntityId<SuggestionIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardShortCandidateExclusion exclusion = exclusions[index]
                ?? throw new ArgumentException(
                    "Short-candidate exclusions must not contain null elements.",
                    nameof(exclusions));
            if (!Enum.IsDefined(exclusion.Code)
                || !candidateIds.Add(exclusion.CandidateId))
            {
                throw new ArgumentException(
                    "Short-candidate exclusions must have unique candidate identities and defined codes.",
                    nameof(exclusions));
            }

            copied[index] = exclusion;
        }

        Array.Sort(
            copied,
            static (left, right) => StringComparer.Ordinal.Compare(
                left.CandidateId.ToString(),
                right.CandidateId.ToString()));
        return copied;
    }

    private static StandardThumbnailPlan[] CopyThumbnails(
        IReadOnlyList<StandardThumbnailPlan> thumbnails)
    {
        if (thumbnails.Count > MaximumThumbnailCount)
        {
            throw new ArgumentException(
                "A standard composition plan supports at most three thumbnails.",
                nameof(thumbnails));
        }

        StandardThumbnailPlan[] copied = new StandardThumbnailPlan[
            thumbnails.Count];
        var ids = new HashSet<EntityId<StandardThumbnailPlanIdTag>>();
        var sourceFrames = new HashSet<ThumbnailSourceFrame>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardThumbnailPlan thumbnail = thumbnails[index]
                ?? throw new ArgumentException(
                    "Thumbnails must not contain null elements.",
                    nameof(thumbnails));
            var sourceFrame = new ThumbnailSourceFrame(
                thumbnail.SourceAssetId,
                thumbnail.SourceFrameSequence,
                thumbnail.SourcePresentationTime);
            if (!ids.Add(thumbnail.Id) || !sourceFrames.Add(sourceFrame))
            {
                throw new ArgumentException(
                    "Thumbnails must have unique identities and source frames.",
                    nameof(thumbnails));
            }

            copied[index] = thumbnail;
        }

        Array.Sort(
            copied,
            static (left, right) =>
            {
                int result = left.SourcePresentationTime.CompareTo(
                    right.SourcePresentationTime);
                if (result == 0)
                {
                    result = left.SourceFrameSequence.CompareTo(
                        right.SourceFrameSequence);
                }

                return result != 0
                    ? result
                    : StringComparer.Ordinal.Compare(
                        left.Id.ToString(),
                        right.Id.ToString());
            });
        return copied;
    }

    private static MusicPlanningResult CopyMusicPlanning(
        MusicPlanningResult musicPlanning)
    {
        MusicCueAssignment[] assignments = musicPlanning.Assignments
            .OrderBy(static assignment => assignment.CueDigest.Hex,
                StringComparer.Ordinal)
            .ToArray();
        MusicCatalogWarning[] warnings = musicPlanning.Warnings
            .Order()
            .ToArray();
        MusicPolicyEvaluation[] evaluations = musicPlanning.PolicyEvaluations
            .OrderBy(static evaluation => evaluation.Domain)
            .ToArray();
        if (assignments.Any(static assignment => assignment is null)
            || assignments.Select(static assignment => assignment.CueDigest)
                .Distinct().Count() != assignments.Length
            || warnings.Any(static warning => !Enum.IsDefined(warning))
            || warnings.Distinct().Count() != warnings.Length
            || evaluations.Any(static evaluation => evaluation is null)
            || evaluations.Select(static evaluation => evaluation.Domain)
                .Distinct().Count() != evaluations.Length
            || evaluations.Length != Enum.GetValues<MusicPolicyDomain>().Length
            || evaluations.Any(static evaluation =>
                !Enum.IsDefined(evaluation.Domain)
                || evaluation.Status
                    != MusicPolicyEvaluationStatus.NotEvaluated))
        {
            throw new ArgumentException(
                "The music-planning result is inconsistent.",
                nameof(musicPlanning));
        }

        return new MusicPlanningResult(
            musicPlanning.CatalogDigest,
            Array.AsReadOnly(assignments),
            Array.AsReadOnly(warnings),
            Array.AsReadOnly(evaluations));
    }

    private static StandardSubtitleVariantReference[] CopyLongFormSubtitles(
        IReadOnlyList<StandardSubtitleVariantReference> subtitleVariants)
    {
        StandardSubtitleVariantReference[] copied =
            new StandardSubtitleVariantReference[subtitleVariants.Count];
        var kinds = new HashSet<StandardSubtitleVariantKind>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardSubtitleVariantReference reference =
                subtitleVariants[index]
                ?? throw new ArgumentException(
                    "Long-form subtitle references must not contain null elements.",
                    nameof(subtitleVariants));
            if (!Enum.IsDefined(reference.Kind) || !kinds.Add(reference.Kind))
            {
                throw new ArgumentException(
                    "Long-form subtitle reference kinds must be defined and unique.",
                    nameof(subtitleVariants));
            }

            copied[index] = reference;
        }

        if (copied.Length != 4
            || !kinds.Contains(StandardSubtitleVariantKind.BurnedIn)
            || !kinds.Contains(StandardSubtitleVariantKind.SubtitleFree)
            || !kinds.Contains(StandardSubtitleVariantKind.SubRip)
            || !kinds.Contains(StandardSubtitleVariantKind.WebVtt))
        {
            throw new ArgumentException(
                "A standard long form requires burned-in, subtitle-free, SubRip, and WebVTT subtitle references.",
                nameof(subtitleVariants));
        }

        Array.Sort(
            copied,
            static (left, right) => left.Kind.CompareTo(right.Kind));
        return copied;
    }

    private static StandardArtifactOmission[] CopyOmissions(
        IReadOnlyList<StandardArtifactOmission> omissions)
    {
        StandardArtifactOmission[] copied = new StandardArtifactOmission[
            omissions.Count];
        var artifactKinds = new HashSet<StandardCompositionArtifactKind>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardArtifactOmission omission = omissions[index]
                ?? throw new ArgumentException(
                    "Artifact omissions must not contain null elements.",
                    nameof(omissions));
            if (!Enum.IsDefined(omission.ArtifactKind)
                || !Enum.IsDefined(omission.Code)
                || omission.Code == StandardCompositionOmissionCode.Unspecified
                || !artifactKinds.Add(omission.ArtifactKind))
            {
                throw new ArgumentException(
                    "Artifact omissions must be defined and unique by artifact kind.",
                    nameof(omissions));
            }

            copied[index] = omission;
        }

        Array.Sort(
            copied,
            static (left, right) => left.ArtifactKind.CompareTo(
                right.ArtifactKind));
        return copied;
    }

    private static StandardCompositionWarning[] CopyWarnings(
        IReadOnlyList<StandardCompositionWarning> warnings)
    {
        StandardCompositionWarning[] copied =
            new StandardCompositionWarning[warnings.Count];
        var codes = new HashSet<StandardCompositionWarningCode>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardCompositionWarning warning = warnings[index]
                ?? throw new ArgumentException(
                    "Standard composition warnings must not contain null elements.",
                    nameof(warnings));
            if (!Enum.IsDefined(warning.Code) || !codes.Add(warning.Code))
            {
                throw new ArgumentException(
                    "Standard composition warning codes must be defined and unique.",
                    nameof(warnings));
            }

            copied[index] = warning;
        }

        Array.Sort(
            copied,
            static (left, right) => left.Code.CompareTo(right.Code));
        return copied;
    }

    private static StandardPlanInputDigest[] CopyInputs(
        IReadOnlyList<StandardPlanInputDigest> inputDigests)
    {
        if (inputDigests.Count == 0)
        {
            throw new ArgumentException(
                "A standard composition plan requires input digests.",
                nameof(inputDigests));
        }

        StandardPlanInputDigest[] copied = new StandardPlanInputDigest[
            inputDigests.Count];
        var kinds = new HashSet<StandardPlanInputKind>();
        for (int index = 0; index < copied.Length; index++)
        {
            StandardPlanInputDigest input = inputDigests[index]
                ?? throw new ArgumentException(
                    "Standard plan input digests must not contain null elements.",
                    nameof(inputDigests));
            if (!Enum.IsDefined(input.Kind) || !kinds.Add(input.Kind))
            {
                throw new ArgumentException(
                    "Standard plan input digest kinds must be defined and unique.",
                    nameof(inputDigests));
            }

            copied[index] = input;
        }

        Array.Sort(
            copied,
            static (left, right) => left.Kind.CompareTo(right.Kind));
        return copied;
    }

    private readonly record struct ThumbnailSourceFrame(
        EntityId<AssetIdTag> AssetId,
        int FrameSequence,
        RationalTime PresentationTime);

    private readonly record struct SelectedSourceSpan(
        int ShortIndex,
        SourceSpan Span);
}