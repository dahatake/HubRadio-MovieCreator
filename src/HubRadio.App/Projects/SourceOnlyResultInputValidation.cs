using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Projects;

internal sealed record SourceOnlyResultAssBytes(JsonElement Input, string Format, int CueCount,
    string TimestampRuleVersion, string ContentDigest, long ByteSize, string[] Base64Chunks, string AssProfileDigest);
internal sealed record SourceOnlyRenderingInputsPayload(string SchemaVersion, string PreparedVersionCanonicalDigest,
    string PlanDigest, JsonElement Version, JsonElement Timeline, JsonElement Profile, JsonElement Layout,
    JsonElement SourceSpans, JsonElement EditorialRecordIds, JsonElement Fonts, JsonElement BindingEvidence,
    JsonElement DisplayCues, JsonElement? SubtitleEvidence, SourceOnlyResultAssBytes[] SubtitleFiles,
    SourceOnlyResultAssBytes[] GraphicFiles, string SourceCueMappingDigest, SourceOnlyResultReferenceDto? GenerationCheckpoint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubtitleEditProjection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubtitleEditProjectionDigest = null);

/// <summary>
/// Independent renderer inputs, not QC's own statistics. Actual ASS bytes are retained
/// in bounded chunks after staging is deleted. Replays use the immutable prepared child
/// and exact generated file bytes; get-only rendering objects are never deserialized.
/// </summary>
internal static class SourceOnlyResultInputValidation
{
    private const string Schema = "source-only.rendering-inputs.v2";
    private const string ProjectedSchema = "source-only.rendering-inputs.v3";
    private const int MaximumAssBytes = 256 * 1024;

    internal static SourceOnlyRenderingInputsPayload Capture(StandardGeneratedArtifact item, StandardCompositionPreparation preparation)
    {
        JsonElement cues = SourceOnlyResultEvidenceStore.Snapshot(item.DisplayCues);
        JsonElement? subtitle = item.SubtitleEvidence is null ? null : SourceOnlyResultEvidenceStore.Snapshot(item.SubtitleEvidence);
        JsonElement spans = SourceOnlyResultEvidenceStore.Snapshot(item.ValidationContext.SourceSpans);
        var payload = new SourceOnlyRenderingInputsPayload(item.SubtitleProjection is null ? Schema : ProjectedSchema,
            ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(CompositionSnapshotJson.WriteVersion(preparation.Child))).Hex,
            preparation.Plan.PlanDigest.Hex, SourceOnlyResultCodec.Element(CompositionSnapshotJson.WriteVersion(item.Context.CompositionVersion)),
            SourceOnlyResultEvidenceStore.Snapshot(item.Context.TimelineMap), SourceOnlyResultEvidenceStore.Snapshot(item.Context.Profile),
            SourceOnlyResultEvidenceStore.Snapshot(item.Context.LayoutSpec), spans,
            SourceOnlyResultEvidenceStore.Snapshot(item.Context.EditorialRecordIds), SourceOnlyResultEvidenceStore.Snapshot(item.Context.Fonts),
            SourceOnlyResultEvidenceStore.Snapshot(item.BindingEvidence), cues, subtitle,
            item.SubtitleFiles.Select(f => File(f.Input, f.File)).ToArray(),
            (item.GraphicFiles ?? []).Select(f => File(f.Input, f.File)).ToArray(),
            MappingDigest(cues, subtitle, spans).Hex,
            SubtitleEditProjection: item.SubtitleProjection?.CanonicalJson,
            SubtitleEditProjectionDigest: item.SubtitleProjection?.ContentDigest.Hex);
        if (item.SubtitleProjection is not null)
        {
            SourceOnlyResultRoleIdentity role = SourceOnlyOutputEvaluation.ParseRole(item.BindingEvidence.Role);
            _ = ValidateProjection(payload, preparation, item.Context.CompositionVersion, item.Context.TimelineMap,
                item.Context.LayoutSpec, item.BindingEvidence.SettingsSnapshotDigest, role.Role, role.RenditionId, GetLiveSubtitles(item));
        }
        return payload;
    }

    // The holder comes from this artifact's native capability, never a caller's JSON
    // or the current mutable generated runtime. The getter validates its immutable output.
    internal static SourceOnlyGeneratedSubtitleRuntimeEvidence? GetLiveSubtitles(StandardGeneratedArtifact item) =>
        item.SubtitleProjection is { } projection
            ? projection.GetValidatedOutput() with { SubtitleProjection = projection } : null;

    private static SourceOnlyResultAssBytes File(RenderGraphInput input, SubtitleExportFile file)
    {
        SourceOnlyResultCodec.Require(file.ByteSize is > 0 and <= MaximumAssBytes);
        byte[] bytes = file.GetUtf8Bytes();
        SourceOnlyResultCodec.Require(bytes.LongLength == file.ByteSize && ContentDigest.Compute(bytes) == file.ContentDigest);
        return new(SourceOnlyResultEvidenceStore.Snapshot(input), file.Format.ToString(), file.CueCount,
            file.TimestampRuleVersion, file.ContentDigest.Hex, file.ByteSize,
            bytes.Chunk(16 * 1024).Select(c => Convert.ToBase64String(c)).ToArray(),
            StandardRenderGraphFactory.Digest(ReadAssProfile(bytes)).Hex);
    }

    private static ContentDigest MappingDigest(JsonElement cues, JsonElement? evidence, JsonElement spans) =>
        SourceOnlyResultEvidenceStore.PayloadDigest(SourceOnlyResultCodec.Element(SourceOnlyResultCodec.Write(
            new { domain = "source-only.subtitle-mapping.v2", displayCues = cues, subtitleEvidence = evidence, sourceSpans = spans },
            SourceOnlyResultEvidenceStore.MaximumEvidenceBytes)));

    internal static SourceOnlyRenderingInputsPayload Read(JsonElement payload)
    {
        var value = JsonSerializer.Deserialize<SourceOnlyRenderingInputsPayload>(payload, SourceOnlyResultCodec.Options)
            ?? throw SourceOnlyResultCodec.Invalid();
        ValidateSchema(value);
        SourceOnlyResultCodec.Require(SourceOnlyResultCodec.Write(value,
            SourceOnlyResultEvidenceStore.MaximumEvidenceBytes) == HubRadio.Core.Serialization.CanonicalJson.Canonicalize(payload.GetRawText()));
        return value;
    }

    private static void ValidateSchema(SourceOnlyRenderingInputsPayload value)
    {
        SourceOnlyResultCodec.Require(value.SubtitleEditProjection is null
            ? value.SchemaVersion == Schema && value.SubtitleEditProjectionDigest is null
            : value.SchemaVersion == ProjectedSchema && value.SubtitleEditProjectionDigest is not null);
        if (value.SubtitleEditProjection is string canonical)
        {
            SourceOnlyResultCodec.Require(canonical.Length is > 0 and <= SubtitleEditProjection.MaximumCanonicalUtf8Bytes &&
                SourceOnlyResultCodec.Utf8.GetByteCount(canonical) <= SubtitleEditProjection.MaximumCanonicalUtf8Bytes);
            using JsonDocument document = JsonDocument.Parse(canonical, SubtitleEditProjection.EvidenceDocumentOptions);
            SourceOnlyResultCodec.Require(document.RootElement.ValueKind == JsonValueKind.Object &&
                SourceOnlyResultCodec.Utf8.GetString(HubRadio.Core.Serialization.CanonicalJson.Write(document.RootElement)) == canonical &&
                ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(canonical)) == SourceOnlyResultCodec.Digest(value.SubtitleEditProjectionDigest!));
        }
    }

    internal static IReadOnlyList<SourceOnlyResultSubtitleMeasurement> Validate(SourceOnlyRenderingInputsPayload proof,
        SourceOnlyResultArtifact artifact, StandardCompositionPreparation preparation, DefaultSettingsSnapshot settings,
        JsonElement graphJson, JsonElement manifestJson, SourceOnlyGeneratedSubtitleRuntimeEvidence? regeneratedSubtitles = null)
    {
        ValidateSchema(proof);
        SourceOnlyResultCodec.Require(proof.PlanDigest == preparation.Plan.PlanDigest.Hex &&
            proof.PreparedVersionCanonicalDigest == ContentDigest.Compute(SourceOnlyResultCodec.Utf8.GetBytes(
                CompositionSnapshotJson.WriteVersion(preparation.Child))).Hex &&
            proof.DisplayCues.ValueKind == JsonValueKind.Array && proof.DisplayCues.GetArrayLength() <= StandardSubtitleInput.MaximumCueCount &&
            proof.SubtitleFiles.Length <= StandardSubtitleInput.MaximumCueCount && proof.GraphicFiles.Length <= 256 &&
            proof.SourceCueMappingDigest == MappingDigest(proof.DisplayCues, proof.SubtitleEvidence, proof.SourceSpans).Hex);
        CompositionVersion version;
        TimelineMap timeline;
        RenderProfile profile;
        LayoutSpec layout;
        IReadOnlyList<SourceSpan> expectedSpans;
        if (artifact.IsImage)
        {
            SourceOnlyResultImageExpected expected = SourceOnlyResultImageValidation.Expected(preparation, artifact, settings);
            (version, timeline, profile, layout, expectedSpans) = (expected.Version, expected.Timeline, expected.Profile, expected.Layout, new[] { expected.Span });
            Same(graphJson, SourceOnlyResultEvidenceStore.Snapshot(expected.Graph));
        }
        else
        {
            version = artifact.Role == SourceOnlyResultRole.VerticalBurnIn
                ? StandardRenderGraphFactory.CreateShortVersion(preparation, artifact.RenditionId!) : preparation.Child;
            timeline = artifact.Role == SourceOnlyResultRole.VerticalBurnIn
                ? TimelineMapBuilder.Build(NativeId<TimelineMapIdTag>(graphJson, "TimelineMapId"), version) : preparation.ChildTimelineMap;
            profile = StandardRenderGraphFactory.GetProfile(preparation.Plan,
                artifact.Role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn, artifact.RenditionId);
            layout = StandardRenderGraphFactory.GetLayout(preparation.Plan, artifact.RenditionId);
            expectedSpans = preparation.Plan.LongForm.Provenance.SourceSpans.Concat(
                preparation.Plan.ShortForms.SelectMany(s => s.SourceSpans)).DistinctBy(s => s.Id).ToArray();
        }
        SourceOnlyResultCodec.Require(HubRadio.Core.Serialization.CanonicalJson.Canonicalize(proof.Version.GetRawText()) == CompositionSnapshotJson.WriteVersion(version));
        Same(proof.Timeline, SourceOnlyResultEvidenceStore.Snapshot(timeline));
        Same(proof.Profile, SourceOnlyResultEvidenceStore.Snapshot(profile));
        Same(proof.Layout, SourceOnlyResultEvidenceStore.Snapshot(layout));
        SubtitleEditProjectionEvidence? projection = ValidateProjection(proof, preparation, version, timeline, layout,
            settings.ContentDigest, artifact.Role, artifact.RenditionId, regeneratedSubtitles);
        IReadOnlyList<EntityId<EditorialRecordIdTag>> trustedEditorialIds = artifact.IsImage ? [] : projection is null
            ? preparation.Plan.LongForm.Provenance.TrustedEditorialRecordIds
            : preparation.Plan.LongForm.Provenance.TrustedEditorialRecordIds.Concat(projection.TrustedEditorialRecordIds).Distinct().ToArray();
        Same(proof.EditorialRecordIds, SourceOnlyResultEvidenceStore.Snapshot(trustedEditorialIds));
        SourceSpan[] spans = proof.SourceSpans.EnumerateArray().Select(ReadSpan).ToArray();
        SourceOnlyResultCodec.Require(spans.Length <= 1024);
        foreach (SourceSpan span in spans)
        {
            SourceSpan? sameId = expectedSpans.SingleOrDefault(s => s.Id == span.Id);
            if (sameId is not null) SourceOnlyResultCodec.Require(StandardRenderGraphFactory.Digest(sameId) == StandardRenderGraphFactory.Digest(span));
            else SourceOnlyResultCodec.Require(!artifact.IsImage && expectedSpans.Any(s => s.AssetId == span.AssetId &&
                s.SourceDigest == span.SourceDigest && s.StreamId == span.StreamId && s.TranscriptRevisionId == span.TranscriptRevisionId &&
                s.Range.Start <= span.Range.Start && s.Range.End >= span.Range.End && span.TranscriptTokenIds.Count > 0 &&
                Enumerable.Range(0, Math.Max(0, s.TranscriptTokenIds.Count - span.TranscriptTokenIds.Count + 1))
                    .Any(i => s.TranscriptTokenIds.Skip(i).Take(span.TranscriptTokenIds.Count).SequenceEqual(span.TranscriptTokenIds))));
        }
        RenderFontEvidence[] fonts = proof.Fonts.EnumerateArray().Select(f => new RenderFontEvidence(
            f.GetProperty("Name").GetString()!, Digest(f, "FileDigest"))).ToArray();
        RenderGraph graph = ReadGraph(graphJson);
        ContentDigest layoutDigest = artifact.IsImage || artifact.Role == SourceOnlyResultRole.VerticalBurnIn
            ? StandardRenderGraphFactory.Digest(layout) : preparation.Plan.LongForm.Profile.LayoutDigest;
        var context = new RenderGraphValidationContext(preparation.Plan.LongForm.Project, version, timeline,
            RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline), profile, layout, layoutDigest,
            artifact.Role == SourceOnlyResultRole.VerticalBurnIn ? preparation.Plan.ShortFormProfile.TemplateReference : preparation.Plan.LongForm.Profile.TemplateReference,
            spans, trustedEditorialIds,
            graph.Inputs.Select(i => new RenderArtifactEvidence(i.Id, i.ContentDigest, i.ByteSize)).ToArray(), fonts);
        SourceOnlyResultCodec.Require(RenderGraphValidator.Validate(graph, context).IsValid && artifact.ProfileId == profile.Id && artifact.ProfileDigest == profile.ProfileDigest);
        JsonElement b = proof.BindingEvidence;
        SourceOnlyResultCodec.Require(SourceOnlyResultEvidenceStore.NativeId(b, "PlanId") == preparation.Plan.Id.ToString() &&
            Digest(b, "PlanDigest") == preparation.Plan.PlanDigest && Digest(b, "GraphDigest") == artifact.GraphDigest &&
            NativeId<RenderGraphIdTag>(b, "GraphId") == graph.Id &&
            NativeId<HubRadio.Infrastructure.Files.ArtifactIdTag>(b, "ArtifactId") == artifact.ArtifactId &&
            NativeId<HubRadio.Infrastructure.Files.ArtifactIdTag>(b, "SourceArtifactId") == artifact.Source.ArtifactId &&
            NativeId<CompositionVersionIdTag>(b, "VersionId") == artifact.VersionId && Digest(b, "VersionDigest") == artifact.VersionDigest &&
            Digest(b, "OutputDigest") == artifact.ArtifactDigest && Integer(b.GetProperty("OutputByteSize")) == artifact.ByteSize &&
            Digest(b, "SettingsSnapshotDigest") == settings.ContentDigest &&
            SourceOnlyResultEvidenceStore.PayloadDigest(b) == Digest(manifestJson, "ConfigurationDigest") &&
            SourceOnlyResultEvidenceStore.PayloadDigest(proof.DisplayCues) == Digest(b, "DisplayCueDigest"));
        SourceOnlyResultRoleIdentity identity = SourceOnlyOutputEvaluation.ParseRole(b.GetProperty("Role").GetString()!);
        SourceOnlyResultCodec.Require(identity.Role == artifact.Role && identity.RenditionId == artifact.RenditionId && identity.ThumbnailPlanId == artifact.ThumbnailPlanId);
        if (artifact.IsImage)
        {
            SourceOnlyResultFrameEvidence frame = artifact.FrameEvidence!;
            JsonElement execution = b.GetProperty("Execution");
            var expectedFrame = new StandardThumbnailRenderEvidence(artifact.ThumbnailPlanId!, frame.SourceAssetId,
                frame.SourceSpanId, frame.SourceDigest, frame.SourcePtsIndexDigest, frame.SourceFrameSequence,
                frame.SourcePresentationTime, frame.SourceFrameDuration, new SourceDisplayGeometry(frame.SourceWidth, frame.SourceHeight), frame.Width, frame.Height);
            Same(execution.GetProperty("Thumbnail"), SourceOnlyResultEvidenceStore.Snapshot(expectedFrame));
            SourceOnlyResultCodec.Require(execution.GetProperty("EncoderSettings").ValueKind == JsonValueKind.Null &&
                execution.GetProperty("Loudness").ValueKind == JsonValueKind.Null &&
                Digest(b, "SourcePtsIndexDigest") == frame.SourcePtsIndexDigest &&
                Digest(b, "SubtitleConfigurationDigest") == StandardRenderGraphFactory.Digest(new { domain = "source-frame-no-subtitles" }));
        }
        ValidateManifestGraph(manifestJson, graphJson);
        ValidateFiles(proof.SubtitleFiles, RenderInputKind.Subtitle, graph);
        ValidateFiles(proof.GraphicFiles, RenderInputKind.Graphic, graph);
        SubtitleFileCue[] cues = proof.DisplayCues.EnumerateArray().Select(ReadFileCue).ToArray();
        SourceOnlyResultCodec.Require(cues.Select(c => c.Cue.Id).Distinct().Count() == cues.Length);
        bool burn = artifact.Role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn;
        SourceOnlyResultCodec.Require(burn == (proof.SubtitleEvidence is not null) && (burn || proof.SubtitleFiles.Length == 0) &&
            (!artifact.IsImage || cues.Length == 0 && proof.GraphicFiles.Length == 0));
        var measured = new List<SourceOnlyResultSubtitleMeasurement>();
        JsonElement? display = proof.SubtitleEvidence?.GetProperty("DisplayResult");
        if (burn)
        {
            JsonElement evidence = proof.SubtitleEvidence!.Value;
            SourceOnlyResultCodec.Require(Integer(display!.Value.GetProperty("SourceCueCount")) == cues.Length &&
                display.Value.GetProperty("DisplayedCues").GetArrayLength() == cues.Length &&
                display.Value.GetProperty("OmittedCueIds").GetArrayLength() == 0 &&
                evidence.GetProperty("Layouts").GetArrayLength() == cues.Length && proof.SubtitleFiles.Length == cues.Length &&
                Digest(display.Value, "ProfileVersionDigest") == Digest(evidence, "DisplayProfileDigest"));
        }
        ContentDigest? assProfileDigest = null;
        foreach (SubtitleFileCue cue in cues.OrderBy(c => c.Cue.Range.Start).ThenBy(c => c.Cue.Id.ToString(), StringComparer.Ordinal))
        {
            SourceOnlyResultCodec.Require(timeline.Entries.Any(e => e.OutputRange.Start <= cue.Cue.Range.Start && e.OutputRange.End >= cue.Cue.Range.End));
            bool safe = false;
            if (burn)
            {
                JsonElement displayed = display!.Value.GetProperty("DisplayedCues").EnumerateArray().Single(c => NativeId<CueIdTag>(c.GetProperty("Cue"), "Id") == cue.Cue.Id);
                Same(displayed.GetProperty("Cue"), SourceOnlyResultEvidenceStore.Snapshot(cue.Cue));
                SourceOnlyResultCodec.Require(displayed.GetProperty("DisplayText").GetString() == cue.Text);
                _ = Digest(displayed, "SourceTextDigest"); // Immutable source mapping, not regenerated from changed display text.
                RenderNode node = graph.Nodes.Single(n => n.Kind == RenderNodeKind.Subtitle && n.Cue!.Id == cue.Cue.Id);
                SourceOnlyResultCodec.Require(node.Cue!.Equals(cue.Cue));
                JsonElement resolution = proof.SubtitleEvidence!.Value.GetProperty("Layouts").EnumerateArray()
                    .Single(l => NativeId<CueIdTag>(l, "CueId") == cue.Cue.Id).GetProperty("Resolution");
                SourceOnlyResultCodec.Require(Integer(resolution.GetProperty("Status")) == (int)SubtitleLayoutResolutionStatus.Resolved &&
                    NativeId<LayoutSpecIdTag>(resolution, "LayoutSpecId") == layout.Id &&
                    Digest(resolution, "ProfileVersionDigest") == Digest(proof.SubtitleEvidence.Value, "LayoutProfileDigest"));
                JsonElement placement = resolution.GetProperty("Plan").GetProperty("Placement");
                SourceOnlyResultCodec.Require(placement.GetProperty("RegionName").GetString() == cue.Cue.RegionName);
                SubtitleLayoutRect rect = ReadRect(placement.GetProperty("Rect"));
                LayoutRegion region = layout.Regions.Single(r => r.Name == cue.Cue.RegionName && r.Role == LayoutRegionRole.Subtitle);
                safe = SubtitleLayoutGeometry.IsWithinOutputFrame(rect) && SubtitleLayoutGeometry.Contains(region.Rect, rect) &&
                    layout.SafeZones.Count > 0 && layout.SafeZones.All(z => SubtitleLayoutGeometry.Contains(z.Rect, rect));
                SourceOnlyResultAssBytes file = proof.SubtitleFiles.Single(f => NativeId<RenderInputIdTag>(f.Input, "Id") == node.ArtifactInputId);
                byte[] actual = Bytes(file);
                AssSubtitleProfile ass = ReadAssProfile(actual);
                if (projection is not null)
                {
                    StandardSubtitleInput expectedInput = regeneratedSubtitles!.Build.Subtitles;
                    SubtitleCueSnapshot expectedCue = expectedInput.Revisions.Single(r => r.Snapshot.Cue.Id == cue.Cue.Id).Snapshot;
                    Same(SourceOnlyResultEvidenceStore.Snapshot(ass), SourceOnlyResultEvidenceStore.Snapshot(expectedInput.AssProfile));
                    Same(file.Input.GetProperty("Origin"), SourceOnlyResultEvidenceStore.Snapshot(expectedCue.TextOrigin));
                    SourceOnlyResultCodec.Require(node.Origin == expectedCue.TextOrigin && actual.AsSpan().SequenceEqual(
                        StandardSubtitlePlacement.Write(cue, expectedInput.AssProfile, expectedCue.LayoutResolution).GetUtf8Bytes()));
                }
                SourceOnlyResultCodec.Require(ass.PlayResolutionWidth == profile.Width && ass.PlayResolutionHeight == profile.Height &&
                    ass.Styles.All(s => fonts.Any(f => f.Name == s.FontName)) &&
                    StandardRenderGraphFactory.Digest(ass).Hex == file.AssProfileDigest);
                ContentDigest current = StandardRenderGraphFactory.Digest(ass);
                SourceOnlyResultCodec.Require(assProfileDigest is null || assProfileDigest == current);
                assProfileDigest = current;
                int left = Pixel(rect.X, profile.Width), top = Pixel(rect.Y, profile.Height);
                byte[] expected = StandardSubtitlePlacement.WriteAtBounds(cue, ass, left, top,
                    checked(left + Pixel(rect.Width, profile.Width)), checked(top + Pixel(rect.Height, profile.Height))).GetUtf8Bytes();
                // Entire bytes, including the parsed Dialogue start/end/style/text and placement.
                SourceOnlyResultCodec.Require(file.CueCount == 1 && actual.AsSpan().SequenceEqual(expected));
            }
            measured.Add(SourceOnlyResultObservationBuilder.MeasureCue(cue, safe));
        }
        if (!artifact.IsVideo && !artifact.IsImage)
        {
            SubtitleExportFile file = artifact.Role == SourceOnlyResultRole.SubRip ? SubRipSubtitleWriter.Write(cues) : WebVttSubtitleWriter.Write(cues);
            SourceOnlyResultCodec.Require(cues.Length > 0 && file.ContentDigest == artifact.ArtifactDigest && file.ByteSize == artifact.ByteSize);
        }
        return Array.AsReadOnly(measured.ToArray());
    }

    private static SubtitleEditProjectionEvidence? ValidateProjection(SourceOnlyRenderingInputsPayload payload,
        StandardCompositionPreparation preparation, CompositionVersion version, TimelineMap timeline, LayoutSpec layout,
        ContentDigest settingsDigest, SourceOnlyResultRole role, EntityId<StandardShortFormPlanIdTag>? renditionId,
        SourceOnlyGeneratedSubtitleRuntimeEvidence? regenerated)
    {
        bool image = role is SourceOnlyResultRole.LandscapeThumbnail or SourceOnlyResultRole.VerticalThumbnail;
        SourceOnlyResultCodec.Require(!image || payload.SubtitleEditProjection is null && regenerated?.SubtitleProjection is null);
        if (payload.SubtitleEditProjection is null)
        {
            SourceOnlyResultCodec.Require(regenerated?.SubtitleProjection is null);
            return null;
        }
        SourceOnlyResultCodec.Require(regenerated?.SubtitleProjection is not null);
        SubtitleEditProjectionEvidence projection = regenerated.SubtitleProjection;
        projection.ValidateOutput(regenerated);
        SourceOnlyResultCodec.Require(payload.SubtitleEditProjection == projection.CanonicalJson &&
            payload.SubtitleEditProjectionDigest == projection.ContentDigest.Hex &&
            CompositionSnapshotJson.WriteVersion(regenerated.Version) == CompositionSnapshotJson.WriteVersion(version) &&
            CompositionSnapshotJson.WriteVersion(version) == CompositionSnapshotJson.WriteVersion(renditionId is null
                ? preparation.Child : StandardRenderGraphFactory.CreateShortVersion(preparation, renditionId)) &&
            regenerated.Build.SettingsDigest == settingsDigest && timeline.CompositionVersionId == version.Id &&
            regenerated.Timeline.CompositionVersionId == version.Id &&
            (renditionId is not null || regenerated.Timeline.Id == timeline.Id) &&
            regenerated.Timeline.Entries.SequenceEqual(timeline.Entries) &&
            TimelineMapBuilder.Build(regenerated.Timeline.Id, version).Entries.SequenceEqual(regenerated.Timeline.Entries));
        Same(SourceOnlyResultEvidenceStore.Snapshot(regenerated.Profile), SourceOnlyResultEvidenceStore.Snapshot(
            StandardRenderGraphFactory.GetProfile(preparation.Plan, renditionId is not null, renditionId)));
        Same(SourceOnlyResultEvidenceStore.Snapshot(regenerated.Layout), SourceOnlyResultEvidenceStore.Snapshot(layout));
        Same(SourceOnlyResultEvidenceStore.Snapshot(layout), SourceOnlyResultEvidenceStore.Snapshot(StandardRenderGraphFactory.GetLayout(preparation.Plan, renditionId)));
        StandardSubtitleInput input = regenerated.Build.Subtitles;
        SourceSpan[] catalog = preparation.Plan.LongForm.Provenance.SourceSpans.Concat(
            preparation.Plan.ShortForms.SelectMany(s => s.SourceSpans)).ToArray();
        foreach (SubtitleCueHistory history in regenerated.Histories)
        {
            projection.ValidateInitial(version, history, CancellationToken.None);
            SubtitleCueRevision revision = history.CurrentRevision;
            SubtitleCueCandidate candidate = history.SourceCandidate;
            SourceSpan span = candidate.SourceSpan;
            SourceOnlyResultCodec.Require(revision.ProjectId == version.ProjectId && revision.CompositionVersionId == version.Id &&
                revision.CompositionVersionDigest == version.ContentDigest &&
                revision.SourceRevisionId == preparation.Plan.LongForm.Provenance.TranscriptRevisionId &&
                revision.SourceRevisionDigest == preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest &&
                candidate.SourceRevisionId == revision.SourceRevisionId && candidate.SourceRevisionDigest == revision.SourceRevisionDigest &&
                revision.SourceOrigin == candidate.Origin && span.TranscriptRevisionId == candidate.SourceRevisionId &&
                candidate.EvidenceTokenIds.SequenceEqual(span.TranscriptTokenIds) &&
                revision.ContentDigest == SubtitleSnapshotJson.ComputeContentDigest(revision.Snapshot) &&
                timeline.Entries.Count(entry => entry.Origin.Kind == OriginKind.SourceSpan && entry.SourceRange is TimeRange range &&
                    range.Start <= span.Range.Start && range.End >= span.Range.End &&
                    entry.OutputRange.Start <= revision.Snapshot.Cue.Range.Start && entry.OutputRange.End >= revision.Snapshot.Cue.Range.End &&
                    catalog.Any(cut => cut.Id == entry.Origin.SourceSpanId && cut.AssetId == span.AssetId &&
                        cut.SourceDigest == span.SourceDigest && cut.StreamId == span.StreamId && cut.TranscriptRevisionId == span.TranscriptRevisionId)) == 1);
            Same(payload.SourceSpans.EnumerateArray().Single(s => NativeId<SourceSpanIdTag>(s, "Id") == span.Id),
                SourceOnlyResultEvidenceStore.Snapshot(span));
        }
        SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(input.DisplayProfile,
            input.Revisions.Select(r => SubtitleDisplayMapper.MapSource(r.Snapshot)).ToArray(), []);
        SourceOnlyResultCodec.Require(display.OmittedCueIds.Count == 0 && display.DisplayedCues.Count == input.Revisions.Count);
        SubtitleFileCue[] expectedCues = display.DisplayedCues.Select(c => SubtitleDisplayMapper.MapForExport(
            input.Revisions.Single(r => r.Snapshot.Cue.Id == c.Cue.Id).Snapshot, c)).ToArray();
        Same(payload.DisplayCues, SourceOnlyResultEvidenceStore.Snapshot(expectedCues));
        bool burn = role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn;
        SourceOnlyResultCodec.Require(burn == (payload.SubtitleEvidence is not null) && (burn || payload.SubtitleFiles.Length == 0));
        if (burn)
        {
            SourceOnlyResultCodec.Require(input.Revisions.Count > 0 && payload.SubtitleFiles.Length == expectedCues.Length);
            var expected = new OutputQcSubtitleEvidence(input.Revisions[0].Snapshot.LayoutResolution.ProfileVersionDigest,
                input.DisplayProfile.VersionDigest, display, input.Revisions.Select(r =>
                    new OutputQcSubtitleLayoutEvidence(r.Snapshot.Cue.Id, r.Snapshot.LayoutResolution)).ToArray());
            Same(payload.SubtitleEvidence!.Value, SourceOnlyResultEvidenceStore.Snapshot(expected));
            SourceOnlyResultCodec.Require(payload.SubtitleFiles.All(f => f.AssProfileDigest == StandardRenderGraphFactory.Digest(input.AssProfile).Hex));
        }
        // Native/Pascal-case renderer contract, including ContentDigest { Hex },
        // not the camel-case persistence DTO or a digest supplied by JSON.
        ContentDigest configuration = StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-subtitle-input-v3",
            input.DisplayProfile,
            input.AssProfile,
            input.Warnings,
            revisions = input.Revisions.Select(r => new { r.Id, r.ContentDigest, r.SourceCandidateId, r.SourceRevisionId, r.SourceRevisionDigest }).ToArray(),
            SubtitleEditProjection = projection.CanonicalJson,
            SubtitleEditProjectionDigest = projection.ContentDigest,
        });
        SourceOnlyResultCodec.Require(Digest(payload.BindingEvidence, "SubtitleConfigurationDigest") == configuration &&
            NativeId<CompositionVersionIdTag>(payload.BindingEvidence, "VersionId") == version.Id &&
            Digest(payload.BindingEvidence, "VersionDigest") == version.ContentDigest &&
            Digest(payload.BindingEvidence, "DisplayCueDigest") == StandardRenderGraphFactory.Digest(expectedCues));
        return projection;
    }

    private static void ValidateFiles(SourceOnlyResultAssBytes[] files, RenderInputKind kind, RenderGraph graph)
    {
        RenderGraphInput[] inputs = graph.Inputs.Where(i => i.Kind == kind).ToArray();
        SourceOnlyResultCodec.Require(inputs.Length == files.Length && files.Select(f => NativeId<RenderInputIdTag>(f.Input, "Id")).Distinct().Count() == files.Length);
        foreach (SourceOnlyResultAssBytes file in files)
        {
            RenderGraphInput input = inputs.Single(i => i.Id == NativeId<RenderInputIdTag>(file.Input, "Id"));
            Same(file.Input, SourceOnlyResultEvidenceStore.Snapshot(input));
            byte[] bytes = Bytes(file);
            SourceOnlyResultCodec.Require(file.Format == nameof(SubtitleFileFormat.AdvancedSubStationAlpha) &&
                file.TimestampRuleVersion == "r013.nearest-to-even-target-units.v1" &&
                ContentDigest.Compute(bytes) == input.ContentDigest && bytes.LongLength == input.ByteSize &&
                file.AssProfileDigest == StandardRenderGraphFactory.Digest(ReadAssProfile(bytes)).Hex);
        }
    }

    private static byte[] Bytes(SourceOnlyResultAssBytes file)
    {
        SourceOnlyResultCodec.Require(file.ByteSize is > 0 and <= MaximumAssBytes && file.Base64Chunks.Length is > 0 and <= 16);
        using var stream = new MemoryStream();
        for (int i = 0; i < file.Base64Chunks.Length; i++)
        {
            string chunk = file.Base64Chunks[i];
            SourceOnlyResultCodec.Require(chunk.Length <= 21848);
            byte[] bytes = Convert.FromBase64String(chunk);
            SourceOnlyResultCodec.Require(Convert.ToBase64String(bytes) == chunk && bytes.Length is > 0 and <= 16384 &&
                (i == file.Base64Chunks.Length - 1 || bytes.Length == 16384) && stream.Length + bytes.Length <= file.ByteSize);
            stream.Write(bytes);
        }
        byte[] result = stream.ToArray();
        SourceOnlyResultCodec.Require(result.LongLength == file.ByteSize && ContentDigest.Compute(result).Hex == file.ContentDigest);
        _ = SourceOnlyResultCodec.Utf8.GetString(result);
        return result;
    }

    private static AssSubtitleProfile ReadAssProfile(byte[] bytes)
    {
        string[] lines = SourceOnlyResultCodec.Utf8.GetString(bytes).Split('\n');
        int Number(string text) => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int width = Number(lines.Single(l => l.StartsWith("PlayResX: ", StringComparison.Ordinal))[10..]);
        int height = Number(lines.Single(l => l.StartsWith("PlayResY: ", StringComparison.Ordinal))[10..]);
        AssColor Color(string text)
        {
            SourceOnlyResultCodec.Require(text.Length == 10 && text.StartsWith("&H", StringComparison.Ordinal));
            byte[] color = Convert.FromHexString(text[2..]);
            return new(color[0], color[3], color[2], color[1]);
        }
        bool Flag(string text) { SourceOnlyResultCodec.Require(text is "0" or "-1"); return text == "-1"; }
        AssStyleDefinition[] styles = lines.Where(l => l.StartsWith("Style: ", StringComparison.Ordinal)).Select(l =>
        {
            string[] f = l[7..].Split(',');
            SourceOnlyResultCodec.Require(f.Length == 23);
            return new AssStyleDefinition(SourceOnlyResultCodec.EnumValue<SubtitleFileCueStyle>(f[0]), f[1], Number(f[2]),
                Color(f[3]), Color(f[4]), Color(f[5]), Color(f[6]), Flag(f[7]), Flag(f[8]), Flag(f[9]), Flag(f[10]),
                Number(f[11]), Number(f[12]), Number(f[13]), Number(f[14]), (AssBorderStyle)Number(f[15]), Number(f[16]), Number(f[17]),
                (AssAlignment)Number(f[18]), new AssMargins(Number(f[19]), Number(f[20]), Number(f[21])), Number(f[22]));
        }).ToArray();
        SourceOnlyResultCodec.Require(lines.Count(l => l.StartsWith("Dialogue: ", StringComparison.Ordinal)) == 1);
        return new(width, height, styles);
    }

    private static int Pixel(RationalNumber value, int extent)
    {
        BigInteger pixel = BigInteger.DivRem((BigInteger)value.Numerator * extent, value.Denominator, out BigInteger remainder);
        SourceOnlyResultCodec.Require(remainder.IsZero && pixel >= 0 && pixel <= extent);
        return (int)pixel;
    }
    private static SubtitleLayoutRect ReadRect(JsonElement rect) => new(ReadNumber(rect.GetProperty("X")), ReadNumber(rect.GetProperty("Y")),
        ReadNumber(rect.GetProperty("Width")), ReadNumber(rect.GetProperty("Height")));
    private static RationalNumber ReadNumber(JsonElement number) => new(Integer(number.GetProperty("Numerator")), Integer(number.GetProperty("Denominator")));
    internal static RationalTime ReadTime(JsonElement value, bool native = true) => SourceOnlyResultCodec.Time(new(
        Integer(value.GetProperty(native ? "Value" : "value")), Integer(value.GetProperty(native ? "Rate" : "rate"))));
    private static TimeRange ReadRange(JsonElement value) => new(ReadTime(value.GetProperty("Start")), ReadTime(value.GetProperty("Duration")));
    private static Cue ReadCue(JsonElement value) => new(NativeId<CueIdTag>(value, "Id"), (CueKind)Integer(value.GetProperty("Kind")),
        ReadRange(value.GetProperty("Range")), value.GetProperty("RegionName").GetString());
    private static SubtitleFileCue ReadFileCue(JsonElement value)
    {
        var cue = new SubtitleFileCue(ReadCue(value.GetProperty("Cue")), value.GetProperty("Text").GetString()!,
            (SubtitleFileCueStyle)Integer(value.GetProperty("Style")));
        Same(value, SourceOnlyResultEvidenceStore.Snapshot(cue));
        return cue;
    }
    private static SourceSpan ReadSpan(JsonElement value)
    {
        var span = new SourceSpan(NativeId<SourceSpanIdTag>(value, "Id"), NativeId<AssetIdTag>(value, "AssetId"),
            value.GetProperty("StreamId").GetString()!, ReadRange(value.GetProperty("Range")), Digest(value, "SourceDigest"),
            value.GetProperty("TranscriptRevisionId").ValueKind == JsonValueKind.Null ? null : NativeId<TranscriptRevisionIdTag>(value, "TranscriptRevisionId"),
            value.GetProperty("TranscriptTokenIds").EnumerateArray().Select(v => SourceOnlyResultCodec.Id<TranscriptTokenIdTag>(v.GetProperty("Value").GetString()!)).ToArray());
        Same(value, SourceOnlyResultEvidenceStore.Snapshot(span));
        return span;
    }
    private static OriginRef ReadOrigin(JsonElement value)
    {
        OriginRef origin = (OriginKind)Integer(value.GetProperty("Kind")) switch
        {
            OriginKind.SourceSpan => OriginRef.FromSourceSpan(NativeId<SourceSpanIdTag>(value, "SourceSpanId")),
            OriginKind.Asset => OriginRef.FromAsset(NativeId<AssetIdTag>(value, "AssetId")),
            OriginKind.EditorialRecord => OriginRef.FromEditorialRecord(NativeId<EditorialRecordIdTag>(value, "EditorialRecordId")),
            _ => throw SourceOnlyResultCodec.Invalid(),
        };
        Same(value, SourceOnlyResultEvidenceStore.Snapshot(origin));
        return origin;
    }
    internal static RenderGraph ReadGraph(JsonElement value)
    {
        RenderGraphInput[] inputs = value.GetProperty("Inputs").EnumerateArray().Select(i => new RenderGraphInput(
            NativeId<RenderInputIdTag>(i, "Id"), (RenderInputKind)Integer(i.GetProperty("Kind")), Digest(i, "ContentDigest"),
            Integer(i.GetProperty("ByteSize")), ReadOrigin(i.GetProperty("Origin")))).ToArray();
        RenderNode[] nodes = value.GetProperty("Nodes").EnumerateArray().Select(n =>
        {
            EntityId<RenderNodeIdTag> id = NativeId<RenderNodeIdTag>(n, "Id");
            EntityId<RenderNodeIdTag>[] incoming = n.GetProperty("InputNodeIds").EnumerateArray()
                .Select(i => SourceOnlyResultCodec.Id<RenderNodeIdTag>(i.GetProperty("Value").GetString()!)).ToArray();
            TimeRange range = ReadRange(n.GetProperty("OutputRange"));
            RenderNode node = (RenderNodeKind)Integer(n.GetProperty("Kind")) switch
            {
                RenderNodeKind.Video => RenderNode.Video(id, NativeId<RenderInputIdTag>(n, "ArtifactInputId"), range,
                    ReadOrigin(n.GetProperty("Origin")), ReadRange(n.GetProperty("SourceRange")), n.GetProperty("RegionName").GetString()!),
                RenderNodeKind.Audio => RenderNode.Audio(id, NativeId<RenderInputIdTag>(n, "ArtifactInputId"), range,
                    ReadOrigin(n.GetProperty("Origin")), ReadRange(n.GetProperty("SourceRange"))),
                RenderNodeKind.Subtitle => RenderNode.Subtitle(id, NativeId<RenderInputIdTag>(n, "ArtifactInputId"), incoming.Single(),
                    ReadCue(n.GetProperty("Cue")), ReadOrigin(n.GetProperty("Origin"))),
                RenderNodeKind.Graphic => RenderNode.Graphic(id, NativeId<RenderInputIdTag>(n, "ArtifactInputId"), incoming.Single(),
                    ReadCue(n.GetProperty("Cue")), ReadOrigin(n.GetProperty("Origin"))),
                RenderNodeKind.Transition => RenderNode.TransitionEffect(id, incoming, range, ReadOrigin(n.GetProperty("Origin")), Transition.Cut()),
                RenderNodeKind.Output => RenderNode.Output(id, incoming, range),
                _ => throw SourceOnlyResultCodec.Invalid(),
            };
            Same(n, SourceOnlyResultEvidenceStore.Snapshot(node));
            return node;
        }).ToArray();
        JsonElement template = value.GetProperty("Template");
        var result = new RenderGraph(NativeId<RenderGraphIdTag>(value, "Id"), NativeId<CompositionVersionIdTag>(value, "CompositionVersionId"),
            Digest(value, "CompositionVersionDigest"), NativeId<TimelineMapIdTag>(value, "TimelineMapId"), Digest(value, "TimelineMapDigest"),
            NativeId<RenderProfileIdTag>(value, "ProfileId"), Digest(value, "ProfileDigest"), NativeId<LayoutSpecIdTag>(value, "LayoutSpecId"),
            Digest(value, "LayoutSpecDigest"), new TemplateReference(NativeId<TemplateIdTag>(template, "Id"), template.GetProperty("Name").GetString()!, Digest(template, "VersionDigest")),
            inputs, value.GetProperty("Fonts").EnumerateArray().Select(f => new RenderFontEvidence(f.GetProperty("Name").GetString()!, Digest(f, "FileDigest"))).ToArray(),
            nodes, NativeId<RenderNodeIdTag>(value, "OutputNodeId"));
        Same(value, SourceOnlyResultEvidenceStore.Snapshot(result));
        return result;
    }
    internal static void ValidateManifestGraph(JsonElement manifest, JsonElement graph)
    {
        foreach (string key in new[] { "CompositionVersionId", "CompositionVersionDigest", "TimelineMapId", "TimelineMapDigest", "ProfileId", "ProfileDigest", "Fonts" })
            Same(manifest.GetProperty(key), graph.GetProperty(key));
        Same(manifest.GetProperty("TemplateId"), graph.GetProperty("Template").GetProperty("Id"));
        Same(manifest.GetProperty("TemplateVersionDigest"), graph.GetProperty("Template").GetProperty("VersionDigest"));
        SourceOnlyResultCodec.Require(manifest.GetProperty("Inputs").EnumerateArray().Select(i => Digest(i, "ContentDigest").Hex).Order(StringComparer.Ordinal)
            .SequenceEqual(graph.GetProperty("Inputs").EnumerateArray().Select(i => Digest(i, "ContentDigest").Hex).Order(StringComparer.Ordinal)));
    }
    internal static void Same(JsonElement left, JsonElement right) => SourceOnlyResultCodec.Require(
        SourceOnlyResultEvidenceStore.PayloadDigest(left) == SourceOnlyResultEvidenceStore.PayloadDigest(right));
    internal static long Integer(JsonElement value) => SourceOnlyResultEvidenceStore.Integer(value);
    internal static ContentDigest Digest(JsonElement value, string key) => SourceOnlyResultCodec.Digest(SourceOnlyResultEvidenceStore.NativeDigest(value, key));
    internal static EntityId<T> NativeId<T>(JsonElement value, string key) => SourceOnlyResultCodec.Id<T>(SourceOnlyResultEvidenceStore.NativeId(value, key));
}