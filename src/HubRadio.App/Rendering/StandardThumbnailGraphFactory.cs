using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Rendering;

public sealed record StandardThumbnailRenderEvidence(EntityId<StandardThumbnailPlanIdTag> ThumbnailPlanId,
    EntityId<AssetIdTag> SourceAssetId, EntityId<SourceSpanIdTag> SourceSpanId,
    ContentDigest SourceDigest, ContentDigest SourcePtsIndexDigest, int SourceFrameSequence,
    RationalTime SourcePresentationTime, RationalTime SourceFrameDuration, SourceDisplayGeometry SourceGeometry,
    int Width, int Height, string ExtractionRule = "decode-original-select-source-sequence-contain-v1");

internal static class StandardThumbnailGraphFactory
{
    internal static StandardRenderGraphOutput Create(StandardCompositionPreparation preparation,
        Asset source, SourceDisplayGeometry geometry, PtsIndex index, StandardThumbnailPlan thumbnail,
        StandardRenderConfiguration configuration, bool vertical, FfmpegSourceColor? sourceColor = null)
    {
        StandardRenderGraphFactory.ValidatePreparation(preparation);
        StandardCompositionPlan plan = preparation.Plan;
        if (!plan.Thumbnails.Contains(thumbnail) || thumbnail.SourceAssetId != source.Id
            || index.CandidateId != source.Id || index.ContentDigest != source.ContentDigest
            || index.ByteSize != source.ByteSize || index.ProjectId != plan.ProjectId
            || (uint)thumbnail.SourceFrameSequence >= (uint)index.Entries.Count)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        PtsIndexEntry frame = index.Entries[thumbnail.SourceFrameSequence];
        if (frame.Sequence != thumbnail.SourceFrameSequence || frame.PresentationTime != thumbnail.SourcePresentationTime)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        ValidateCapturedIndex(preparation, index, frame);
        if (configuration.SettingsSnapshotDigest != StandardRenderGraphFactory.InputDigest(plan, StandardPlanInputKind.SettingsSnapshot))
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
        int width = vertical ? configuration.VerticalThumbnailWidth : configuration.LandscapeThumbnailWidth;
        int height = vertical ? configuration.VerticalThumbnailHeight : configuration.LandscapeThumbnailHeight;
        EntityId<T> Id<T>(string role) => EntityId<T>.From(new Guid(Convert.FromHexString(StandardRenderGraphFactory.Digest(
            new { plan = plan.PlanDigest.Hex, thumbnail = thumbnail.Id, vertical, width, height, role }).Hex)[..16]));
        var span = new SourceSpan(Id<SourceSpanIdTag>("frame-span"), source.Id,
            FormattableString.Invariant($"video:{index.StreamIndex}"), new TimeRange(frame.PresentationTime, frame.Duration),
            source.ContentDigest, null, []);
        OriginRef origin = OriginRef.FromSourceSpan(span.Id);
        var range = new TimeRange(default, frame.Duration);
        var element = new CompositionElement(Id<CompositionElementIdTag>("element"), range, origin, sourceRange: span.Range);
        var version = new CompositionVersion(Id<CompositionVersionIdTag>("version"), plan.ProjectId, preparation.Child.Id,
            preparation.Child.CreatedByActorId, preparation.Child.CreatedAt, "Extract the exact planned source frame.",
            RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]), [element]);
        TimelineMap timeline = TimelineMapBuilder.Build(Id<TimelineMapIdTag>("timeline"), version);
        ContentDigest timelineDigest = RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
        var full = new NormalizedRect(default, default, new RationalNumber(1, 1), new RationalNumber(1, 1));
        var layout = new LayoutSpec(Id<LayoutSpecIdTag>("layout"), vertical ? 9 : 16, vertical ? 16 : 9,
            [new LayoutRegion("source-frame", full, LayoutRegionRole.Video)], [new SafeZone("output-frame-bounds", full)]);
        ContentDigest layoutDigest = StandardRenderGraphFactory.Digest(layout);
        RenderProfile sourceProfile = plan.LongForm.Profile.Profile;
        TemplateReference template = plan.LongForm.Profile.TemplateReference;
        var profile = new RenderProfile(Id<RenderProfileIdTag>("profile"), "standard-source-thumbnail",
            StandardRenderGraphFactory.Digest(new { width, height, sourceProfile.FrameRate, layoutDigest }), width, height,
            sourceProfile.FrameRateNumerator, sourceProfile.FrameRateDenominator, layout.Id, template.Id,
            template.VersionDigest, "h264", "aac", false);
        var input = new RenderGraphInput(Id<RenderInputIdTag>("source"), RenderInputKind.Video, source.ContentDigest, source.ByteSize, origin);
        RenderNode video = RenderNode.Video(Id<RenderNodeIdTag>("video"), input.Id, range, origin, span.Range, "source-frame");
        RenderNode output = RenderNode.Output(Id<RenderNodeIdTag>("output"), [video.Id], range);
        var graph = new RenderGraph(Id<RenderGraphIdTag>("graph"), version.Id, version.ContentDigest,
            timeline.Id, timelineDigest, profile.Id, profile.ProfileDigest, layout.Id, layoutDigest, template,
            [input], [], [video, output], output.Id);
        var context = new RenderGraphValidationContext(plan.LongForm.Project, version, timeline, timelineDigest,
            profile, layout, layoutDigest, template, [span], [], [new RenderArtifactEvidence(input.Id, input.ContentDigest, input.ByteSize)], []);
        if (!RenderGraphValidator.Validate(graph, context).IsValid)
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.BindingMismatch);
        var evidence = new StandardThumbnailRenderEvidence(thumbnail.Id, source.Id, span.Id, source.ContentDigest,
            ContentDigest.Compute(Encoding.UTF8.GetBytes(index.CanonicalJson)), frame.Sequence, frame.PresentationTime,
            frame.Duration, geometry, width, height);
        return new StandardRenderGraphOutput($"thumbnail-{(vertical ? "vertical" : "landscape")}-{thumbnail.Id}",
            plan, graph, context, [new FfmpegVideoGeometryBinding(video.Id, geometry)], [], [],
            StandardRenderGraphFactory.Digest(new { domain = "source-frame-no-subtitles" }), null,
            frameExtraction: new FfmpegFrameExtraction(video.Id, frame.Sequence, frame.PresentationTime, sourceColor), thumbnailEvidence: evidence);
    }

    internal static void ValidateCapturedIndex(StandardCompositionPreparation preparation, PtsIndex index,
        PtsIndexEntry? selected = null, EntityId<ArtifactIdTag>? sourceArtifactId = null)
    {
        if (preparation.Plan.Thumbnails.Count == 0) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(preparation.Child.Evidence!.CanonicalJson);
            JsonElement evidence = document.RootElement.GetProperty("input").GetProperty("thumbnailEvidence");
            if (evidence.GetProperty("sourcePtsIndexDigest").GetString() != ContentDigest.Compute(Encoding.UTF8.GetBytes(index.CanonicalJson)).Hex
                || evidence.GetProperty("sourceContentDigest").GetString() != index.ContentDigest.Hex
                || evidence.GetProperty("sourceAssetId").GetString() != index.CandidateId.ToString()
                || StandardSourceGeometry.ReadMathematicalInt32(evidence.GetProperty("sourceStreamIndex")) != index.StreamIndex
                || sourceArtifactId is not null && evidence.GetProperty("sourceArtifactId").GetString() != sourceArtifactId.ToString())
                throw new FormatException();
            if (selected is not null)
            {
                JsonElement[] frames = evidence.GetProperty("frames").EnumerateArray().Where(frame =>
                    StandardSourceGeometry.ReadMathematicalInt32(frame.GetProperty("sourceFrameSequence")) == selected.Sequence).ToArray();
                RationalTime ReadTime(JsonElement time) => new(
                    long.Parse(time.GetProperty("value").GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
                    long.Parse(time.GetProperty("rate").GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture));
                if (frames.Length != 1 || ReadTime(frames[0].GetProperty("sourcePresentationTime")) != selected.PresentationTime
                    || ReadTime(frames[0].GetProperty("sourceDuration")) != selected.Duration
                    || frames[0].GetProperty("sourceKeyFrame").GetBoolean() != selected.KeyFrame)
                    throw new FormatException();
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw StandardRenderGraphFactory.Failure(StandardGenerationFailureReason.SourceMismatch);
        }
    }
}