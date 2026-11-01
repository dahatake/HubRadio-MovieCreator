using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Projects;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Rendering;

/// <summary>Existing, version-bound subtitle revisions; not generated editorial text.</summary>
public sealed class StandardSubtitleInput
{
    public const int MaximumCueCount = 256;
    private readonly IReadOnlyList<SubtitleCueHistory> histories;
    private readonly SourceOnlyGeneratedSubtitleRuntimeEvidence? projectionOutput;

    public StandardSubtitleInput(
        IReadOnlyList<SubtitleCueHistory> histories,
        SubtitleDisplayProfile displayProfile,
        AssSubtitleProfile assProfile,
        IReadOnlyList<StandardSubtitleBuildWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(histories);
        DisplayProfile = displayProfile ?? throw new ArgumentNullException(nameof(displayProfile));
        AssProfile = assProfile ?? throw new ArgumentNullException(nameof(assProfile));
        int count = histories.Count;
        if (count > MaximumCueCount)
        {
            throw new ArgumentException("Subtitle cue count exceeds the renderer's explicit 256-cue resource bound.", nameof(histories));
        }

        var copy = new SubtitleCueRevision[count];
        var candidates = new SubtitleCueCandidate[count];
        var historyCopy = new SubtitleCueHistory[count];
        for (int i = 0; i < count; i++)
        {
            SubtitleCueHistory history = histories[i] ?? throw new ArgumentException("Null subtitle history.", nameof(histories));
            copy[i] = history.CurrentRevision;
            candidates[i] = history.SourceCandidate;
            historyCopy[i] = history;
        }

        if (copy.Select(static item => item.Snapshot.Cue.Id).Distinct().Count() != count
            || displayProfile.CueMode != SubtitleCueDisplayMode.Full)
        {
            throw new ArgumentException("Standard subtitles require unique cues and full display.", nameof(histories));
        }

        Revisions = Array.AsReadOnly(copy.OrderBy(static item => item.Snapshot.Cue.Range.Start)
            .ThenBy(static item => item.Snapshot.Cue.Id.ToString(), StringComparer.Ordinal).ToArray());
        Candidates = Array.AsReadOnly(candidates);
        this.histories = Array.AsReadOnly(historyCopy);
        Warnings = Array.AsReadOnly((warnings ?? []).Select(static warning => warning
            ?? throw new ArgumentException("Null subtitle warning.", nameof(warnings))).ToArray());
    }

    private StandardSubtitleInput(StandardSubtitleInput input, SubtitleEditProjectionEvidence proof,
        SourceOnlyGeneratedSubtitleRuntimeEvidence output)
    {
        Revisions = input.Revisions;
        Candidates = input.Candidates;
        DisplayProfile = input.DisplayProfile;
        AssProfile = input.AssProfile;
        Warnings = input.Warnings;
        histories = input.histories;
        Projection = proof;
        projectionOutput = output;
    }

    public IReadOnlyList<SubtitleCueRevision> Revisions { get; }
    public IReadOnlyList<SubtitleCueCandidate> Candidates { get; }
    public SubtitleDisplayProfile DisplayProfile { get; }
    public AssSubtitleProfile AssProfile { get; }
    /// <summary>Retained exclusions and unmeasured policies, not proof of subtitle completeness.</summary>
    public IReadOnlyList<StandardSubtitleBuildWarning> Warnings { get; }

    // Deliberately not public/serialized: attaching native proof must not change
    // SubtitleEditProjection.GeneratedDigest or recursively fingerprint itself.
    internal SubtitleEditProjectionEvidence? Projection { get; }

    /// <summary>Attach only the exact native output, including every complete initial history.</summary>
    internal StandardSubtitleInput WithProjection(SubtitleEditProjectionEvidence proof,
        SourceOnlyGeneratedSubtitleRuntimeEvidence output)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(output);
        proof.ValidateOutput(output);
        SourceOnlyGeneratedSubtitleRuntimeEvidence bound = BindProjectionOutput(output);
        proof.ValidateOutput(bound);
        foreach (SubtitleCueHistory history in histories)
            proof.ValidateInitial(bound.Version, history, CancellationToken.None);
        return new StandardSubtitleInput(this, proof, bound);
    }

    internal StandardSubtitleInput WithProjection(SubtitleEditProjectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return WithProjection(result.Evidence, result.Output);
    }

    internal SubtitleEditProjectionEvidence? ValidateProjection(CompositionVersion version, TimelineMap timeline,
        LayoutSpec layout, RenderProfile generatedProfile, ContentDigest settingsDigest)
    {
        if (Projection is null) return null;
        SourceOnlyGeneratedSubtitleRuntimeEvidence output = projectionOutput
            ?? throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        // Short-form generation and rendering use different timeline IDs. Their
        // complete cuts and bound version must still be identical, not just duration.
        if (timeline.CompositionVersionId != version.Id
            || !timeline.Entries.SequenceEqual(output.Timeline.Entries)
            || !TimelineMapBuilder.Build(output.Timeline.Id, version).Entries.SequenceEqual(timeline.Entries)
            || output.Build.SettingsDigest != settingsDigest)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        try
        {
            Projection.ValidateOutput(BindProjectionOutput(output) with
            {
                Version = version,
                Layout = layout,
                Profile = generatedProfile,
            });
            foreach (SubtitleCueHistory history in histories)
                Projection.ValidateInitial(version, history, CancellationToken.None);
        }
        catch (SubtitleEditProjectionException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SubtitleMismatch);
        }
        return Projection;
    }

    private SourceOnlyGeneratedSubtitleRuntimeEvidence BindProjectionOutput(SourceOnlyGeneratedSubtitleRuntimeEvidence output) =>
        output with
        {
            Build = new StandardSubtitleBuildResult(this, output.Build.Warnings,
                output.Build.SegmentationRuleVersion, output.Build.SettingsDigest),
            Histories = histories,
        };
}

public sealed record StandardSubtitleRenderFile(
    RenderGraphInput Input, SubtitleExportFile File);

/// <summary>A complete typed graph with the actual source cuts and export evidence.</summary>
public sealed class StandardRenderGraphOutput
{
    internal StandardRenderGraphOutput(
        string role, StandardCompositionPlan plan, RenderGraph graph,
        RenderGraphValidationContext context,
        IReadOnlyList<FfmpegVideoGeometryBinding> geometry,
        IReadOnlyList<StandardSubtitleRenderFile> subtitleFiles,
        IReadOnlyList<SubtitleFileCue> displayCues,
        ContentDigest subtitleConfigurationDigest,
        OutputQcSubtitleEvidence? subtitleEvidence,
        IReadOnlyList<StandardGraphicRenderFile>? graphicFiles = null,
        FfmpegFrameExtraction? frameExtraction = null,
        StandardThumbnailRenderEvidence? thumbnailEvidence = null,
        IReadOnlyList<FfmpegMusicBinding>? musicBindings = null,
        IReadOnlyList<StandardMusicRenderEvidence>? musicEvidence = null)
    {
        Role = role;
        Plan = plan;
        Graph = graph;
        ValidationContext = context;
        VideoGeometry = Array.AsReadOnly(geometry.ToArray());
        SubtitleFiles = Array.AsReadOnly(subtitleFiles.ToArray());
        DisplayCues = Array.AsReadOnly(displayCues.ToArray());
        SubtitleConfigurationDigest = subtitleConfigurationDigest;
        SubtitleEvidence = subtitleEvidence;
        GraphicFiles = Array.AsReadOnly((graphicFiles ?? []).ToArray());
        FrameExtraction = frameExtraction;
        ThumbnailEvidence = thumbnailEvidence;
        MusicBindings = Array.AsReadOnly((musicBindings ?? []).ToArray());
        MusicEvidence = Array.AsReadOnly((musicEvidence ?? []).ToArray());
        GraphDigest = StandardRenderGraphFactory.Digest(graph);
    }

    public string Role { get; }
    public StandardCompositionPlan Plan { get; }
    public RenderGraph Graph { get; }
    public ContentDigest GraphDigest { get; }
    public RenderGraphValidationContext ValidationContext { get; }
    public IReadOnlyList<FfmpegVideoGeometryBinding> VideoGeometry { get; }
    public IReadOnlyList<StandardSubtitleRenderFile> SubtitleFiles { get; }
    public IReadOnlyList<SubtitleFileCue> DisplayCues { get; }
    public ContentDigest SubtitleConfigurationDigest { get; }
    public OutputQcSubtitleEvidence? SubtitleEvidence { get; }
    public IReadOnlyList<StandardGraphicRenderFile> GraphicFiles { get; }
    public FfmpegFrameExtraction? FrameExtraction { get; }
    public StandardThumbnailRenderEvidence? ThumbnailEvidence { get; }
    public IReadOnlyList<FfmpegMusicBinding> MusicBindings { get; }
    public IReadOnlyList<StandardMusicRenderEvidence> MusicEvidence { get; }
    internal SubtitleEditProjectionEvidence? SubtitleProjection { get; init; }

    /// <summary>FR-060: the sound effects this output plays (empty when disabled or before profile 1.2.0).</summary>
    public IReadOnlyList<StandardSoundEffectCue> SoundEffectCues { get; init; } = [];

    /// <summary>FR-137 (6): the standing pictures to lay over this output (empty without the character dialogue template).</summary>
    public IReadOnlyList<StyleTemplates.StyleTemplatePortraitShot> PortraitShots { get; init; } = [];

    /// <summary>FR-143 (c): the placement inspection of a vertical output, carried into its <see cref="OutputQcRequest"/>.</summary>
    public OutputQcPlacementEvidence? PlacementEvidence { get; init; }
}

public enum StandardGenerationFailureReason
{
    BindingMismatch,
    UnsupportedPlanElement,
    UnsupportedMusic,
    UnsupportedSourceTiming,
    SourceMismatch,
    SubtitleMismatch,
    NoDisplayCues,
    ResourceLimit,
    ProcessFailed,
    PromotionFailed,
    PreparationNotCheckpointed,
    UnsupportedSetting,
    LoudnessReportInvalid,
    UnsupportedSourceColor,
}

public sealed class StandardGenerationException : Exception
{
    internal StandardGenerationException(StandardGenerationFailureReason reason)
        : base($"Standard artifact generation stopped: {reason}.") => Reason = reason;

    public StandardGenerationFailureReason Reason { get; }
}

/// <summary>
/// Builds source-body graphs and separately evidenced ASS metadata graphics.
/// Untimed metadata overlays never insert a new opener or alter source cuts.
/// Unsupported extra media fails explicitly. No approval is created here.
/// </summary>
public static class StandardRenderGraphFactory
{
    public const int MaximumCuts = 64;

    public static CompositionVersion CreateShortVersion(
        StandardCompositionPreparation preparation,
        EntityId<StandardShortFormPlanIdTag> shortFormId)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        StandardShortFormPlan shortForm = FindShort(preparation.Plan, shortFormId);
        RationalTime cursor = default;
        var elements = new List<CompositionElement>();
        foreach (SourceSpan span in shortForm.SourceSpans)
        {
            elements.Add(new CompositionElement(
                StableId<CompositionElementIdTag>(preparation, $"short-element:{shortFormId}:{span.Id}"),
                new TimeRange(cursor, span.Range.Duration), OriginRef.FromSourceSpan(span.Id), sourceRange: span.Range));
            cursor += span.Range.Duration;
        }

        return new CompositionVersion(
            StableId<CompositionVersionIdTag>(preparation, $"short-version:{shortFormId}"),
            preparation.Plan.ProjectId, preparation.Child.Id,
            preparation.Child.CreatedByActorId, preparation.Child.CreatedAt,
            "Derive the selected standard short source cuts.",
            RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
    }

    public static LayoutSpec GetLayout(
        StandardCompositionPlan plan, EntityId<StandardShortFormPlanIdTag>? shortFormId = null) =>
        shortFormId is null ? plan.LongForm.Profile.Layout : FindShort(plan, shortFormId).Layout.DerivedLayoutSpec;

    public static RenderProfile GetProfile(
        StandardCompositionPlan plan, bool burnedIn,
        EntityId<StandardShortFormPlanIdTag>? shortFormId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RenderProfile source = shortFormId is null ? plan.LongForm.Profile.Profile : plan.ShortFormProfile.Profile;
        LayoutSpec layout = GetLayout(plan, shortFormId);
        if (source.LayoutSpecId == layout.Id && source.SubtitleBurnIn == burnedIn)
        {
            return source;
        }

        ContentDigest digest = Digest(new
        {
            domain = "standard-render-profile-v1",
            source,
            layout = Digest(layout).Hex,
            burnedIn
        });
        return new RenderProfile(IdFromDigest<RenderProfileIdTag>(digest), source.Name + (burnedIn ? " burn-in" : " clean"),
            digest, source.Width, source.Height, source.FrameRateNumerator, source.FrameRateDenominator,
            layout.Id, source.TemplateId, source.TemplateVersionDigest, source.VideoCodec, source.AudioCodec, burnedIn);
    }

    public static StandardRenderGraphOutput Create(
        StandardCompositionPreparation preparation,
        Asset source,
        SourceDisplayGeometry sourceDisplayGeometry,
        bool burnedIn,
        StandardSubtitleInput subtitles,
        RenderFontEvidence font,
        EntityId<StandardShortFormPlanIdTag>? shortFormId = null,
        StandardRenderConfiguration? configuration = null,
        StandardMusicRenderInput? music = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceDisplayGeometry);
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(font);
        ValidatePreparation(preparation);
        ValidateCapturedGeometry(preparation, sourceDisplayGeometry);
        StandardCompositionPlan plan = preparation.Plan;
        if (plan.MusicPlanning.Assignments.Any(static item => item.TrackDigest is not null) && music is null)
            throw Failure(StandardGenerationFailureReason.UnsupportedMusic);
        StandardShortFormPlan? shortForm = shortFormId is null ? null : FindShort(plan, shortFormId);
        IReadOnlyList<(FfmpegMusicBinding Binding, StandardMusicRenderEvidence Evidence)> soundtrack =
            shortForm is null && music is not null ? music.Resolve(preparation) : [];
        CompositionVersion version = shortForm is null ? preparation.Child : CreateShortVersion(preparation, shortForm.Id);
        TimelineMap timeline = shortForm is null ? preparation.ChildTimelineMap : TimelineMapBuilder.Build(
            StableId<TimelineMapIdTag>(preparation, $"short-timeline:{shortForm.Id}"), version);
        IReadOnlyList<SourceSpan> spans = shortForm?.SourceSpans ?? plan.LongForm.Provenance.SourceSpans;
        if (spans.Any(span => span.AssetId != source.Id || span.SourceDigest != source.ContentDigest)
            || !plan.LongForm.Project.Assets.Any(asset => asset.Id == source.Id
                && asset.ContentDigest == source.ContentDigest && asset.ByteSize == source.ByteSize))
        {
            throw Failure(StandardGenerationFailureReason.SourceMismatch);
        }

        if (timeline.Entries.Count is 0 or > MaximumCuts)
        {
            throw Failure(StandardGenerationFailureReason.ResourceLimit);
        }

        LayoutSpec layout = GetLayout(plan, shortFormId);
        RenderProfile profile = GetProfile(plan, burnedIn, shortFormId);
        TemplateReference template = shortForm is null ? plan.LongForm.Profile.TemplateReference : plan.ShortFormProfile.TemplateReference;
        ContentDigest layoutDigest = shortForm is null ? plan.LongForm.Profile.LayoutDigest : Digest(layout);
        ContentDigest timelineDigest = RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
        string role = shortForm is null ? (burnedIn ? "landscape-burn-in" : "landscape-clean") : $"vertical-burn-in-{shortForm.Id}";
        var inputs = new List<RenderGraphInput>();
        var nodes = new List<RenderNode>();
        var audio = new List<EntityId<RenderNodeIdTag>>();
        var geometry = new List<FfmpegVideoGeometryBinding>();
        EntityId<RenderNodeIdTag>? visual = null;
        RationalTime cursor = default;
        string region = shortForm?.Layout.SourcePlacements[0].Destination.Region.Name
            ?? layout.Regions.First(item => item.Role == LayoutRegionRole.Video).Name;
        foreach (TimelineMapEntry entry in timeline.Entries)
        {
            SourceSpan? span = spans.SingleOrDefault(item => item.Id == entry.Origin.SourceSpanId);
            if (entry.Origin.Kind != OriginKind.SourceSpan || span is null
                || entry.SourceRange is not TimeRange range || range.Start < default(RationalTime)
                || range.Start < span.Range.Start || range.End > span.Range.End
                || entry.OutputRange.Start != cursor || range.Duration != entry.OutputRange.Duration)
            {
                throw Failure(StandardGenerationFailureReason.UnsupportedSourceTiming);
            }

            if (!FfmpegCompiler.IsOnOutputGrid(range.Start, profile)
                || !FfmpegCompiler.IsOnOutputGrid(range.Duration, profile)
                || !FfmpegCompiler.IsOnOutputGrid(entry.OutputRange.Start, profile))
            {
                throw Failure(StandardGenerationFailureReason.UnsupportedSourceTiming);
            }

            var videoInput = new RenderGraphInput(EntityId<RenderInputIdTag>.New(), RenderInputKind.Video,
                source.ContentDigest, source.ByteSize, entry.Origin);
            var audioInput = new RenderGraphInput(EntityId<RenderInputIdTag>.New(), RenderInputKind.Audio,
                source.ContentDigest, source.ByteSize, entry.Origin);
            inputs.Add(videoInput);
            inputs.Add(audioInput);
            RenderNode video = RenderNode.Video(EntityId<RenderNodeIdTag>.New(), videoInput.Id,
                entry.OutputRange, entry.Origin, range, region);
            RenderNode sound = RenderNode.Audio(EntityId<RenderNodeIdTag>.New(), audioInput.Id,
                entry.OutputRange, entry.Origin, range);
            nodes.Add(video);
            nodes.Add(sound);
            audio.Add(sound.Id);
            geometry.Add(new FfmpegVideoGeometryBinding(video.Id, sourceDisplayGeometry, shortForm?.Layout));
            if (visual is not null)
            {
                RenderNode cut = RenderNode.TransitionEffect(EntityId<RenderNodeIdTag>.New(),
                    [visual, video.Id], new TimeRange(cursor, default), entry.Origin, Transition.Cut());
                nodes.Add(cut);
                visual = cut.Id;
            }
            else
            {
                visual = video.Id;
            }

            cursor = entry.OutputRange.End;
        }

        var subtitleFiles = new List<StandardSubtitleRenderFile>();
        var exportCues = new List<SubtitleFileCue>();
        OutputQcSubtitleEvidence? subtitleEvidence = null;
        SubtitleDisplayResult display = ValidateSubtitles(plan, version, timeline, layout, profile, subtitles, font, shortFormId);
        var cueTexts = new List<StandardDisplayedCueText>();
        var cueWords = new List<IReadOnlyList<StyleTemplates.StyleTemplateWord>>();
        foreach (SubtitleDisplayedCue displayed in display.DisplayedCues)
        {
            SubtitleCueRevision revision = subtitles.Revisions.Single(item => item.Snapshot.Cue.Id == displayed.Cue.Id);
            SubtitleFileCue exported = SubtitleDisplayMapper.MapForExport(revision.Snapshot, displayed);
            exportCues.Add(exported);
            var cueText = new StandardDisplayedCueText(exported.Cue.Range, exported.Text, revision.Snapshot.TextOrigin);
            cueTexts.Add(cueText);
            cueWords.Add(configuration?.StyleTemplate is null ? [] : TimedWords(configuration, subtitles, revision, cueText));
        }

        // FR-059 / FR-060: emphasis captions and sound effects follow the accepted settings and the cues
        // this output actually displays; settings without the 1.2.0 keys produce neither.
        IReadOnlyList<StandardEmphasisTelop> telops;
        IReadOnlyList<StandardSoundEffectCue> soundEffects;
        StyleTemplates.StyleTemplatePlan? styled = null;
        StyleTemplates.StyleTemplateRenderRecord? renderRecord = null;
        StyleTemplates.TalkShowRenderPlan? talkShow = null;
        bool verticalOutput = profile.Height > profile.Width;
        if (configuration?.StyleTemplate is { } styleTemplate)
        {
            // FR-136: the template drives the same FR-059 / FR-060 rules (verbatim telops, sounds only when enabled).
            // FR-138: a vertical screen holds at most 20 characters of subtitle.
            var limits = verticalOutput
                ? new StyleTemplates.StyleTemplateSubtitleLimits(1, StyleTemplates.StyleTemplateLayout.VerticalCaptionMaximumCharacters,
                    default, new RationalTime(7, 1))
                : new StyleTemplates.StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
            // FR-140: only a confirmed assignment gives a cue a speaker; FR-142: the words come from the timed ASR tokens.
            StyleTemplates.CueSpeakerAssignmentSet? assignment = configuration.SpeakerAssignments?
                .Resolve(cueTexts.Count, configuration.Characters);
            StyleTemplates.StyleTemplateFreeTelopInput[] freeLines = [.. configuration.DialogueLines,
                .. assignment?.DialogueLines(cueTexts) ?? []];
            var inputCues = cueTexts.Select((cue, index) => new StyleTemplates.StyleTemplateInputCue(cue, cueWords[index])).ToArray();
            styled = StyleTemplates.StyleTemplateApplier.Apply(styleTemplate, inputCues, freeLines,
                new StyleTemplates.StyleTemplateApplyOptions(limits,
                    configuration.EmphasisTelopAmount, configuration.SoundEffectsEnabled, cursor,
                    Characters: configuration.Characters));
            telops = styled.EmphasisTelops.Select(static item => item.Telop).ToArray();
            soundEffects = styled.SoundEffects;
            if (configuration.RecordSink is not null)
            {
                string? directory = configuration.PortraitImageDirectory;
                renderRecord = StyleTemplates.StyleTemplateRenderRecord.Create(styleTemplate, styled, profile.Width, profile.Height, inputCues,
                    assignment?.IsConfirmed == true, shot => directory is null ? null
                        : StyleTemplates.PortraitImageCropper.MeanColor(Path.Combine(directory, Path.GetFileName(shot.ImageFile))));
            }

            // FR-141: the side telops and name bands come only from what the user entered or confirmed.
            if (styleTemplate.Id == StyleTemplates.StyleTemplateCatalog.TalkShowId && configuration.TalkShow is { } talkInput)
            {
                talkShow = StyleTemplates.TalkShowRenderPlanner.Plan(talkInput, (double)cursor.Value / cursor.Rate, profile.Width, profile.Height,
                    "#FFFFFF");
            }
        }
        else
        {
            telops = StandardEmphasisTelops.Select(cueTexts, configuration?.EmphasisTelopAmount, cursor);
            soundEffects = StandardEmphasisTelops.PlanSoundEffects(
                configuration?.SoundEffectsEnabled == true, cueTexts, telops, cursor);
        }
        IReadOnlyList<StandardGraphicRenderFile> graphicFiles = StandardMetadataGraphics.Create(plan,
            shortForm, visual!, profile, cursor, timeline.Entries[0].OutputRange.Duration, font, configuration, telops, styled,
            talkShow, timeline.Entries[0].Origin);
        inputs.AddRange(graphicFiles.Select(static file => file.Input));
        StyleTemplates.StyleTemplateRect? verticalSubtitleBox = null;
        OutputQcPlacementEvidence? placement = null;
        if (renderRecord is not null && configuration?.RecordSink is { } recordSink)
        {
            // FR-143: the record states where each subtitle and telop was burned in and the output inspection of a vertical output.
            if (verticalOutput)
            {
                verticalSubtitleBox = StandardTemplateSubtitles.VerticalBox(profile.Width, profile.Height, null, configuration.SubtitleIntoMargin);
            }

            StyleTemplates.TalkShowBox? subtitleBox = verticalSubtitleBox is { } vertical ? StyleTemplates.TalkShowBox.From(vertical) : null;
            if (subtitleBox is null && subtitles.Revisions.Count > 0)
            {
                (int left, int top, int right, int bottom) = StandardSubtitlePlacement.Bounds(subtitles.AssProfile, subtitles.Revisions[0].Snapshot.LayoutResolution);
                subtitleBox = new(left, top, right - left, bottom - top);
            }

            StyleTemplates.StyleTemplateRenderRecord placed = renderRecord.WithPlacement(talkShow,
                [.. graphicFiles.Select(static file => new StyleTemplates.RecordedTelop(file.Evidence.Role, file.Evidence.RenderedText,
                    (double)file.Evidence.OutputRange.Start.Value / file.Evidence.OutputRange.Start.Rate,
                    (double)file.Evidence.OutputRange.End.Value / file.Evidence.OutputRange.End.Rate,
                    new(file.Evidence.Left, file.Evidence.Top, file.Evidence.Right - file.Evidence.Left, file.Evidence.Bottom - file.Evidence.Top)))],
                subtitleBox, verticalOutput ? StyleTemplates.StyleTemplateMargins.Vertical : null, platformUiConfirmed: false);
            recordSink(placed);
            placement = placed.OutputInspection is { } recorded ? OutputQcPlacement(recorded) : null;
        }

        if (burnedIn)
        {
            if (exportCues.Count == 0)
            {
                throw Failure(StandardGenerationFailureReason.NoDisplayCues);
            }

            foreach (SubtitleFileCue cue in exportCues)
            {
                SubtitleCueRevision revision = subtitles.Revisions.Single(item => item.Snapshot.Cue.Id == cue.Cue.Id);
                SubtitleExportFile file = styled is null
                    ? StandardSubtitlePlacement.Write(cue, subtitles.AssProfile, revision.Snapshot.LayoutResolution)
                    : StandardTemplateSubtitles.Write(cue, subtitles.AssProfile, revision.Snapshot.LayoutResolution, styled, verticalOutput,
                        intoMargin: configuration?.SubtitleIntoMargin == true);
                var input = new RenderGraphInput(EntityId<RenderInputIdTag>.New(), RenderInputKind.Subtitle,
                    file.ContentDigest, file.ByteSize, revision.Snapshot.TextOrigin);
                inputs.Add(input);
                subtitleFiles.Add(new StandardSubtitleRenderFile(input, file));
                RenderNode subtitle = RenderNode.Subtitle(EntityId<RenderNodeIdTag>.New(), input.Id, visual!, cue.Cue, input.Origin);
                nodes.Add(subtitle);
                visual = subtitle.Id;
            }

            subtitleEvidence = new OutputQcSubtitleEvidence(subtitles.Revisions[0].Snapshot.LayoutResolution.ProfileVersionDigest,
                subtitles.DisplayProfile.VersionDigest, display, subtitles.Revisions.Select(item =>
                    new OutputQcSubtitleLayoutEvidence(item.Snapshot.Cue.Id, item.Snapshot.LayoutResolution)).ToArray());
        }

        RenderNode output = RenderNode.Output(EntityId<RenderNodeIdTag>.New(), [visual!, .. audio], new TimeRange(default, cursor));
        nodes.Add(output);
        RenderFontEvidence[] fonts = burnedIn || graphicFiles.Count > 0 ? [font] : [];
        var graph = new RenderGraph(EntityId<RenderGraphIdTag>.New(), version.Id, version.ContentDigest,
            timeline.Id, timelineDigest, profile.Id, profile.ProfileDigest, layout.Id, layoutDigest,
            template, inputs, fonts, nodes, output.Id);
        SourceSpan[] contextSpans = spans.Concat(plan.LongForm.Provenance.SourceSpans)
            .Concat(subtitles.Candidates.Select(static candidate => candidate.SourceSpan))
            .DistinctBy(static span => span.Id).ToArray();
        // ValidateSubtitles has validated the entire current output, including
        // empty cue sets, before any projected editorial IDs enter this context.
        IReadOnlyList<EntityId<EditorialRecordIdTag>> editorialIds = subtitles.Projection is null
            ? plan.LongForm.Provenance.TrustedEditorialRecordIds
            : plan.LongForm.Provenance.TrustedEditorialRecordIds.Concat(subtitles.Projection.TrustedEditorialRecordIds)
                .Distinct().ToArray();
        var context = new RenderGraphValidationContext(plan.LongForm.Project, version, timeline, timelineDigest,
            profile, layout, layoutDigest, template, contextSpans,
            editorialIds,
            inputs.Select(static input => new RenderArtifactEvidence(input.Id, input.ContentDigest, input.ByteSize)).ToArray(), fonts);
        if (!RenderGraphValidator.Validate(graph, context).IsValid)
        {
            throw Failure(StandardGenerationFailureReason.BindingMismatch);
        }

        var revisions = subtitles.Revisions.Select(static item => new
        {
            item.Id,
            item.ContentDigest,
            item.SourceCandidateId,
            item.SourceRevisionId,
            item.SourceRevisionDigest,
        }).ToArray();
        ContentDigest subtitleConfigurationDigest = subtitles.Projection is null ? Digest(new
        {
            domain = "standard-subtitle-input-v2",
            subtitles.DisplayProfile,
            subtitles.AssProfile,
            subtitles.Warnings,
            revisions,
        }) : Digest(new
        {
            domain = "standard-subtitle-input-v3",
            subtitles.DisplayProfile,
            subtitles.AssProfile,
            subtitles.Warnings,
            revisions,
            SubtitleEditProjection = subtitles.Projection.CanonicalJson,
            SubtitleEditProjectionDigest = subtitles.Projection.ContentDigest,
        });
        return new StandardRenderGraphOutput(role, plan, graph, context, geometry, subtitleFiles, exportCues,
            subtitleConfigurationDigest, subtitleEvidence, graphicFiles,
            musicBindings: soundtrack.Select(static item => item.Binding).ToArray(),
            musicEvidence: soundtrack.Select(static item => item.Evidence).ToArray())
        {
            SubtitleProjection = subtitles.Projection,
            SoundEffectCues = soundEffects,
            PortraitShots = styled?.Portraits ?? [],
            PlacementEvidence = placement,
        };
    }

    /// <summary>FR-143 (c): the OutputQc form of the placement inspection the render record states.</summary>
    internal static OutputQcPlacementEvidence OutputQcPlacement(StyleTemplates.RecordedInspection inspection) => new(
        inspection.Rule, inspection.Width, inspection.Height,
        inspection.Margins is { } margins ? new OutputQcPlacementMargins(margins.Top, margins.Bottom, margins.Left, margins.Right) : null,
        new(inspection.Region.X, inspection.Region.Y, inspection.Region.Width, inspection.Region.Height),
        [.. inspection.Items.Select(static item => new OutputQcPlacementItem(item.Id,
            new(item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height), item.InsideMargins, item.InsideRegion, item.Ok))],
        inspection.Warnings);

    internal static void ValidateSubtitleProjection(StandardCompositionPreparation preparation,
        StandardSubtitleInput subtitles, EntityId<StandardShortFormPlanIdTag>? shortFormId = null)
    {
        if (subtitles.Projection is null) return;
        CompositionVersion version = shortFormId is null ? preparation.Child : CreateShortVersion(preparation, shortFormId);
        TimelineMap timeline = shortFormId is null ? preparation.ChildTimelineMap : TimelineMapBuilder.Build(
            StableId<TimelineMapIdTag>(preparation, $"short-timeline:{shortFormId}"), version);
        _ = subtitles.ValidateProjection(version, timeline, GetLayout(preparation.Plan, shortFormId),
            GetProfile(preparation.Plan, shortFormId is not null, shortFormId),
            InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot));
    }

    internal static void ValidatePreparation(StandardCompositionPreparation preparation)
    {
        StandardCompositionPlan plan = preparation.Plan;
        if (plan.ProjectId != preparation.Child.ProjectId
            || preparation.ChildTimelineMap.Entries.Count == 0
            || plan.LongForm.Project.PublicationScope != PublicationScope.InternalOnly
            || preparation.Child.ContentDigest != RenderGraphCanonicalEvidence.ComputeCompositionDigest(preparation.Child.Elements)
            || preparation.ChildTimelineMap.CompositionVersionId != preparation.Child.Id
            || !preparation.ChildTimelineMap.Entries.SequenceEqual(preparation.ParentTimelineMap.Entries)
            || !TimelineMapBuilder.Build(preparation.ChildTimelineMap.Id, preparation.Child).Entries
                .SequenceEqual(preparation.ChildTimelineMap.Entries)
            || InputDigest(plan, StandardPlanInputKind.TimelineMap)
                != RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(preparation.ParentTimelineMap))
        {
            throw Failure(StandardGenerationFailureReason.BindingMismatch);
        }

        if (plan.LongForm.TemplateAssets.Count > 0)
            throw Failure(StandardGenerationFailureReason.UnsupportedPlanElement);

        foreach (LongFormElementPlan element in plan.LongForm.Elements.Where(static item => item.State == LongFormElementState.Included))
        {
            if (element.Kind == LongFormElementKind.Body)
            {
                if (element.OutputRange != new TimeRange(default, preparation.ChildTimelineMap.Entries[^1].OutputRange.End))
                {
                    throw Failure(StandardGenerationFailureReason.BindingMismatch);
                }
            }
            else if (element.AssetSlots.Any(static slot => slot.State == LongFormAssetSlotState.Included)
                || element.Text is null
                || element.Kind is not (LongFormElementKind.EpisodeTitle or LongFormElementKind.ProgramName
                    or LongFormElementKind.EpisodeNumber or LongFormElementKind.Topic
                    or LongFormElementKind.Summary or LongFormElementKind.CallToAction)
                || element.OutputRange is TimeRange range && (range.Start < default(RationalTime)
                    || range.End > preparation.ChildTimelineMap.Entries[^1].OutputRange.End)
                || element.Kind == LongFormElementKind.Topic && element.OutputRange is null)
            {
                throw Failure(StandardGenerationFailureReason.UnsupportedPlanElement);
            }
        }
    }

    internal static void ValidateCapturedGeometry(StandardCompositionPreparation preparation, SourceDisplayGeometry geometry)
    {
        // This field is retained even when there are no short forms. The generator
        // separately requires VersionService's semantic replay before touching output files.
        try
        {
            if (preparation.Child.Evidence?.Kind != CompositionEvidenceEnvelope.StandardCompositionPlanKind)
            {
                throw Failure(StandardGenerationFailureReason.BindingMismatch);
            }

            using JsonDocument document = JsonDocument.Parse(preparation.Child.Evidence.CanonicalJson);
            JsonElement captured = document.RootElement.GetProperty("input").GetProperty("sourceDisplayGeometry");
            if (StandardSourceGeometry.ReadMathematicalInt32(captured.GetProperty("width")) != geometry.Width
                || StandardSourceGeometry.ReadMathematicalInt32(captured.GetProperty("height")) != geometry.Height)
            {
                throw Failure(StandardGenerationFailureReason.SourceMismatch);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw Failure(StandardGenerationFailureReason.BindingMismatch);
        }
    }

    /// <summary>
    /// FR-142: the timed words of a displayed cue, from the ASR tokens of the candidate it came from, moved onto the output timeline by
    /// the same offset as the cue. When the tokens are unknown, untimed or do not make up the cue text (an edited cue is realigned
    /// to the words it still matches), nothing is returned and the cue is shown as a whole (with a warning from the template).
    /// </summary>
    private static IReadOnlyList<StyleTemplates.StyleTemplateWord> TimedWords(StandardRenderConfiguration configuration,
        StandardSubtitleInput subtitles, SubtitleCueRevision revision, StandardDisplayedCueText cue)
    {
        if (configuration.WordTokens is not { } tokens)
        {
            return [];
        }

        SubtitleCueCandidate candidate = subtitles.Candidates.Single(item => item.Id == revision.SourceCandidateId);
        RationalTime offset = revision.Snapshot.Cue.Range.Start - candidate.SourceRange.Start;
        var words = new List<StyleTemplates.StyleTemplateWord>();
        foreach (var id in candidate.EvidenceTokenIds)
        {
            if (!tokens.TryGetValue(id, out HubRadio.Core.Transcripts.Token? token) || token.Range is not TimeRange range)
            {
                return [];
            }

            words.Add(new(token.Text, new TimeRange(range.Start + offset, range.Duration)));
        }

        StyleTemplates.CueWordTimingResult result = string.Concat(words.Select(static word => word.Text)) == cue.Text
            ? StyleTemplates.CueWordTiming.For(cue, words)
            : StyleTemplates.CueWordTiming.Realign(cue, words, cue.Text);
        return result.Words;
    }

    private static SubtitleDisplayResult ValidateSubtitles(
        StandardCompositionPlan plan, CompositionVersion version, TimelineMap timeline,
        LayoutSpec layout, RenderProfile profile, StandardSubtitleInput subtitles, RenderFontEvidence font,
        EntityId<StandardShortFormPlanIdTag>? shortFormId)
    {
        SubtitleEditProjectionEvidence? projection = subtitles.ValidateProjection(version, timeline, layout,
            GetProfile(plan, shortFormId is not null, shortFormId), InputDigest(plan, StandardPlanInputKind.SettingsSnapshot));
        if (subtitles.AssProfile.PlayResolutionWidth != profile.Width
            || subtitles.AssProfile.PlayResolutionHeight != profile.Height
            || subtitles.AssProfile.Styles.Any(style => !string.Equals(style.FontName, font.Name, StringComparison.Ordinal)))
        {
            throw Failure(StandardGenerationFailureReason.SubtitleMismatch);
        }

        foreach (SubtitleCueRevision revision in subtitles.Revisions)
        {
            SubtitleCueSnapshot snapshot = revision.Snapshot;
            StandardSubtitlePlacement.Validate(subtitles.AssProfile, snapshot.LayoutResolution);
            SubtitleCueCandidate candidate = subtitles.Candidates.Single(item => item.Id == revision.SourceCandidateId);
            SourceSpan span = candidate.SourceSpan;
            SourceSpan[] catalog = plan.LongForm.Provenance.SourceSpans
                .Concat(plan.ShortForms.SelectMany(static item => item.SourceSpans)).ToArray();
            SourceSpan? sameId = catalog.FirstOrDefault(item => item.Id == span.Id);
            bool grounded = catalog.Any(item => item.AssetId == span.AssetId && item.SourceDigest == span.SourceDigest
                && item.StreamId == span.StreamId && item.TranscriptRevisionId == span.TranscriptRevisionId
                && item.Range.Start <= span.Range.Start && item.Range.End >= span.Range.End
                && span.TranscriptTokenIds.Count > 0
                && Enumerable.Range(0, Math.Max(0, item.TranscriptTokenIds.Count - span.TranscriptTokenIds.Count + 1))
                    .Any(start => item.TranscriptTokenIds.Skip(start).Take(span.TranscriptTokenIds.Count).SequenceEqual(span.TranscriptTokenIds)));
            bool mapped = timeline.Entries.Any(entry => entry.SourceRange is TimeRange range
                && range.Start <= span.Range.Start && range.End >= span.Range.End
                && snapshot.Cue.Range == new TimeRange(entry.OutputRange.Start + span.Range.Start - range.Start, span.Range.Duration));
            if (projection is not null)
            {
                // The proof already pins the exact manual time/text/style/placement.
                // Still require one real source cut of the same source identity AND
                // containment in that cut's output; a proof is not a mappedness bypass.
                mapped = timeline.Entries.Count(entry => entry.Origin.Kind == OriginKind.SourceSpan
                    && entry.SourceRange is TimeRange range && range.Start <= span.Range.Start && range.End >= span.Range.End
                    && entry.OutputRange.Start <= snapshot.Cue.Range.Start && entry.OutputRange.End >= snapshot.Cue.Range.End
                    && catalog.Any(cut => cut.Id == entry.Origin.SourceSpanId && cut.AssetId == span.AssetId
                        && cut.SourceDigest == span.SourceDigest && cut.StreamId == span.StreamId
                        && cut.TranscriptRevisionId == span.TranscriptRevisionId)) == 1;
            }
            if (revision.ProjectId != version.ProjectId || revision.CompositionVersionId != version.Id
                || revision.CompositionVersionDigest != version.ContentDigest
                || revision.SourceRevisionId != plan.LongForm.Provenance.TranscriptRevisionId
                || revision.SourceRevisionDigest != plan.LongForm.Provenance.TranscriptRevisionDigest
                || revision.ContentDigest != SubtitleSnapshotJson.ComputeContentDigest(snapshot)
                || snapshot.LayoutResolution.Status != SubtitleLayoutResolutionStatus.Resolved
                || snapshot.LayoutResolution.LayoutSpecId != layout.Id
                || snapshot.LayoutResolution.SourceCandidateId != revision.SourceCandidateId
                || !grounded || !mapped
                || sameId is not null && Digest(sameId) != Digest(span)
                || revision.SourceOrigin != candidate.Origin
                || revision.SourceRevisionId != candidate.SourceRevisionId
                || revision.SourceRevisionDigest != candidate.SourceRevisionDigest
                || (snapshot.TextOrigin.Kind == OriginKind.SourceSpan
                    ? snapshot.TextOrigin != candidate.Origin || snapshot.Text != candidate.Text
                    : !plan.LongForm.Provenance.TrustedEditorialRecordIds.Contains(snapshot.TextOrigin.EditorialRecordId!)
                        && projection?.TrustedEditorialRecordIds.Contains(snapshot.TextOrigin.EditorialRecordId!) != true)
                || snapshot.Cue.Range.Start < default(RationalTime) || snapshot.Cue.Range.Duration <= default(RationalTime)
                || !timeline.Entries.Any(entry => entry.OutputRange.Start <= snapshot.Cue.Range.Start
                    && entry.OutputRange.End >= snapshot.Cue.Range.End)
                || subtitles.Revisions.Any(other => other.Snapshot.LayoutResolution.ProfileVersionDigest
                    != snapshot.LayoutResolution.ProfileVersionDigest))
            {
                throw Failure(StandardGenerationFailureReason.SubtitleMismatch);
            }
        }

        SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(subtitles.DisplayProfile,
            subtitles.Revisions.Select(static revision => SubtitleDisplayMapper.MapSource(revision.Snapshot)).ToArray(), []);
        if (display.OmittedCueIds.Count > 0)
        {
            throw Failure(StandardGenerationFailureReason.SubtitleMismatch);
        }

        return display;
    }

    internal static ContentDigest InputDigest(StandardCompositionPlan plan, StandardPlanInputKind kind) =>
        plan.InputDigests.Single(input => input.Kind == kind).ContentDigest;

    internal static ContentDigest Digest<T>(T value) =>
        ContentDigest.Compute(CanonicalJson.CanonicalizeToUtf8(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static EntityId<T> StableId<T>(StandardCompositionPreparation preparation, string role) =>
        IdFromDigest<T>(Digest(new { plan = preparation.Plan.PlanDigest.Hex, version = preparation.Child.Id.ToString(), role }));

    private static EntityId<T> IdFromDigest<T>(ContentDigest digest) =>
        EntityId<T>.From(new Guid(Convert.FromHexString(digest.Hex)[..16]));

    private static StandardShortFormPlan FindShort(StandardCompositionPlan plan, EntityId<StandardShortFormPlanIdTag> id) =>
        plan.ShortForms.SingleOrDefault(shortForm => shortForm.Id == id)
            ?? throw Failure(StandardGenerationFailureReason.BindingMismatch);

    internal static StandardGenerationException Failure(StandardGenerationFailureReason reason) => new(reason);
}