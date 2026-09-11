using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Projects;

internal sealed record SourceOnlyResultImageExpected(CompositionVersion Version, TimelineMap Timeline,
    RenderProfile Profile, LayoutSpec Layout, SourceSpan Span, RenderGraph Graph);

/// <summary>Image-only technical checks. No video duration, audio measurement or quality approval.</summary>
internal static class SourceOnlyResultImageValidation
{
    internal const long MaximumImageBytes = 64 * 1024 * 1024;

    internal static SourceOnlyResultFrameEvidence? Capture(StandardGeneratedArtifact generated)
    {
        StandardThumbnailRenderEvidence? frame = generated.BindingEvidence.Execution?.Thumbnail;
        return frame is null ? null : new(frame.SourceAssetId, frame.SourceSpanId, frame.SourceDigest,
            frame.SourcePtsIndexDigest, frame.SourceFrameSequence, frame.SourcePresentationTime, frame.SourceFrameDuration,
            frame.SourceGeometry.Width, frame.SourceGeometry.Height, frame.Width, frame.Height, frame.ExtractionRule);
    }

    // Derive the deterministic image identity from the independently stored preparation.
    // Never fabricate a partial PtsIndex with the digest of a real, different index.
    internal static SourceOnlyResultImageExpected Expected(StandardCompositionPreparation preparation,
        SourceOnlyResultArtifact artifact, DefaultSettingsSnapshot settings)
    {
        SourceOnlyResultFrameEvidence frame = artifact.FrameEvidence ?? throw SourceOnlyResultCodec.Invalid();
        StandardCompositionPlan plan = preparation.Plan;
        StandardThumbnailPlan thumbnail = plan.Thumbnails.Single(t => t.Id == artifact.ThumbnailPlanId);
        bool vertical = artifact.Role == SourceOnlyResultRole.VerticalThumbnail;
        SourceOnlyResultCodec.Require(artifact.IsImage && artifact.RenditionId is null &&
            frame.ExtractionRule == "decode-original-select-source-sequence-contain-v1" &&
            thumbnail.SourceAssetId == frame.SourceAssetId && thumbnail.SourceFrameSequence == frame.SourceFrameSequence &&
            thumbnail.SourcePresentationTime == frame.SourcePresentationTime && frame.SourceDigest == artifact.Source.ContentDigest);
        string[] canvas = (settings.Values.Single(s => s.Key == (vertical ? "thumbnails.verticalCanvas" : "thumbnails.landscapeCanvas"))
            .Value.TextValue ?? "").Split('x');
        int width = frame.Width, height = frame.Height;
        SourceOnlyResultCodec.Require(width is >= 2 and <= 4096 && height is >= 2 and <= 4096 && width % 2 == 0 && height % 2 == 0 &&
            canvas.SequenceEqual(new[] { width.ToString(System.Globalization.CultureInfo.InvariantCulture), height.ToString(System.Globalization.CultureInfo.InvariantCulture) }) &&
            (long)width * (vertical ? 16 : 9) == (long)height * (vertical ? 9 : 16));
        StandardRenderGraphFactory.ValidateCapturedGeometry(preparation, new SourceDisplayGeometry(frame.SourceWidth, frame.SourceHeight));
        using JsonDocument child = JsonDocument.Parse(preparation.Child.Evidence!.CanonicalJson);
        JsonElement captured = child.RootElement.GetProperty("input").GetProperty("thumbnailEvidence");
        SourceOnlyResultCodec.Require(captured.GetProperty("sourceAssetId").GetString() == frame.SourceAssetId.ToString() &&
            captured.GetProperty("sourceArtifactId").GetString() == artifact.Source.ArtifactId.ToString() &&
            captured.GetProperty("sourceContentDigest").GetString() == frame.SourceDigest.Hex &&
            captured.GetProperty("sourcePtsIndexDigest").GetString() == frame.SourcePtsIndexDigest.Hex);
        JsonElement selected = captured.GetProperty("frames").EnumerateArray().Single(f =>
            SourceOnlyResultEvidenceStore.Integer(f.GetProperty("sourceFrameSequence")) == frame.SourceFrameSequence);
        SourceOnlyResultCodec.Require(SourceOnlyResultInputValidation.ReadTime(selected.GetProperty("sourcePresentationTime"), native: false) == frame.SourcePresentationTime &&
            SourceOnlyResultInputValidation.ReadTime(selected.GetProperty("sourceDuration"), native: false) == frame.SourceFrameDuration);
        long streamIndex = SourceOnlyResultEvidenceStore.Integer(captured.GetProperty("sourceStreamIndex"));
        SourceOnlyResultCodec.Require(streamIndex is >= 0 and <= int.MaxValue);
        Asset source = plan.LongForm.Project.Assets.Single(a => a.Id == frame.SourceAssetId);
        SourceOnlyResultCodec.Require(source.ContentDigest == frame.SourceDigest && source.ByteSize == artifact.Source.ByteSize);
        EntityId<T> Id<T>(string role) => EntityId<T>.From(new Guid(Convert.FromHexString(StandardRenderGraphFactory.Digest(
            new { plan = plan.PlanDigest.Hex, thumbnail = thumbnail.Id, vertical, width, height, role }).Hex)[..16]));
        var span = new SourceSpan(Id<SourceSpanIdTag>("frame-span"), source.Id, FormattableString.Invariant($"video:{streamIndex}"),
            new TimeRange(frame.SourcePresentationTime, frame.SourceFrameDuration), source.ContentDigest, null, []);
        SourceOnlyResultCodec.Require(span.Id == frame.SourceSpanId);
        OriginRef origin = OriginRef.FromSourceSpan(span.Id);
        var range = new TimeRange(default, frame.SourceFrameDuration);
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
            sourceProfile.FrameRateNumerator, sourceProfile.FrameRateDenominator, layout.Id, template.Id, template.VersionDigest, "h264", "aac", false);
        var input = new RenderGraphInput(Id<RenderInputIdTag>("source"), RenderInputKind.Video, source.ContentDigest, source.ByteSize, origin);
        RenderNode video = RenderNode.Video(Id<RenderNodeIdTag>("video"), input.Id, range, origin, span.Range, "source-frame");
        RenderNode output = RenderNode.Output(Id<RenderNodeIdTag>("output"), [video.Id], range);
        var graph = new RenderGraph(Id<RenderGraphIdTag>("graph"), version.Id, version.ContentDigest, timeline.Id, timelineDigest,
            profile.Id, profile.ProfileDigest, layout.Id, layoutDigest, template, [input], [], [video, output], output.Id);
        SourceOnlyResultCodec.Require(version.Id == artifact.VersionId && version.ContentDigest == artifact.VersionDigest &&
            profile.Id == artifact.ProfileId && profile.ProfileDigest == artifact.ProfileDigest && StandardRenderGraphFactory.Digest(graph) == artifact.GraphDigest);
        return new(version, timeline, profile, layout, span, graph);
    }

    internal static async Task ValidatePngAsync(ArtifactStore store, SourceOnlyResultArtifact artifact,
        EntityId<ProjectIdTag> projectId, CancellationToken ct)
    {
        SourceOnlyResultFrameEvidence frame = artifact.FrameEvidence ?? throw SourceOnlyResultCodec.Invalid();
        SourceOnlyResultCodec.Require(artifact.IsImage && artifact.ByteSize is >= 57 and <= MaximumImageBytes);
        await using ValidatedArtifactReadStream stream = await store.OpenValidatedReadAsync(artifact.ArtifactId, ct).ConfigureAwait(false);
        SourceOnlyResultEvidenceStore.RequireSame(stream, artifact.Artifact, projectId);
        byte[] signature = new byte[8];
        await stream.ReadExactlyAsync(signature, ct).ConfigureAwait(false);
        SourceOnlyResultCodec.Require(signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        long remaining = artifact.ByteSize - 8;
        bool headerSeen = false, dataSeen = false, dataEnded = false, endSeen = false;
        using var data = new MemoryStream();
        byte[] header = new byte[8], crcBytes = new byte[4], buffer = new byte[65536];
        for (int chunks = 0; !endSeen && chunks < 4096; chunks++)
        {
            ct.ThrowIfCancellationRequested();
            SourceOnlyResultCodec.Require(remaining >= 12);
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)));
            SourceOnlyResultCodec.Require(length <= remaining - 12 && header.AsSpan(4).ToArray().All(b => b is >= 65 and <= 90 or >= 97 and <= 122) &&
                header[6] is >= 65 and <= 90);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            SourceOnlyResultCodec.Require(headerSeen || type == "IHDR");
            SourceOnlyResultCodec.Require(type switch
            {
                "IHDR" => !headerSeen && length == 13,
                "IDAT" => headerSeen && !dataEnded,
                "IEND" => dataSeen && length == 0,
                "acTL" or "fcTL" or "fdAT" => false,
                _ => header[4] is >= 97 and <= 122,
            });
            uint crc = Crc(0xffffffffu, header.AsSpan(4));
            byte[]? ihdr = type == "IHDR" ? new byte[13] : null;
            for (int offset = 0; offset < length;)
            {
                int count = Math.Min(buffer.Length, length - offset);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                crc = Crc(crc, buffer.AsSpan(0, count));
                if (ihdr is not null) buffer.AsSpan(0, count).CopyTo(ihdr.AsSpan(offset));
                if (type == "IDAT")
                {
                    SourceOnlyResultCodec.Require(data.Length + count <= MaximumImageBytes);
                    data.Write(buffer, 0, count);
                }
                offset += count;
            }
            await stream.ReadExactlyAsync(crcBytes, ct).ConfigureAwait(false);
            SourceOnlyResultCodec.Require((crc ^ 0xffffffffu) == BinaryPrimitives.ReadUInt32BigEndian(crcBytes));
            if (ihdr is not null)
            {
                SourceOnlyResultCodec.Require(BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0, 4)) == frame.Width &&
                    BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4, 4)) == frame.Height &&
                    ihdr[8] == 8 && ihdr[9] == 2 && ihdr[10] == 0 && ihdr[11] == 0 && ihdr[12] == 0);
                headerSeen = true; // Generator contract: a single non-interlaced rgb24 PNG.
            }
            if (type == "IDAT") dataSeen = true;
            else if (dataSeen) dataEnded = true;
            endSeen = type == "IEND";
            remaining -= length + 12L;
        }
        SourceOnlyResultCodec.Require(endSeen && remaining == 0 && data.Length > 0);
        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress, leaveOpen: true);
        byte[] row = new byte[checked(1 + frame.Width * 3)];
        for (int y = 0; y < frame.Height; y++)
        {
            await zlib.ReadExactlyAsync(row, ct).ConfigureAwait(false);
            SourceOnlyResultCodec.Require(row[0] <= 4);
        }
        SourceOnlyResultCodec.Require(zlib.ReadByte() == -1);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return crc;
    }
}