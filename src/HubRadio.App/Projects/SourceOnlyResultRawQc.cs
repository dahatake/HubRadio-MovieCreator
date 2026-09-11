using System.Globalization;
using System.Text.Json;
using HubRadio.Core.Layout;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Projects;

/// <summary>Recomputes summaries from all retained OutputQc observations, not from summary statistics.</summary>
internal static class SourceOnlyResultRawQc
{
    internal static void Validate(JsonElement raw, SourceOnlyResultArtifact artifact, JsonElement graph, JsonElement manifest,
        SourceOnlyRenderingInputsPayload inputs, JsonElement qcTools, IReadOnlyList<SourceOnlyResultSubtitleMeasurement> cues)
    {
        SourceOnlyResultMeasurements m = artifact.Measurements ?? throw SourceOnlyResultCodec.Invalid();
        SourceOnlyResultCodec.Require(artifact.IsVideo &&
            SourceOnlyResultEvidenceStore.NativeId(raw, "ArtifactId") == artifact.ArtifactId.ToString() &&
            SourceOnlyResultEvidenceStore.NativeId(raw, "ProjectId") == SourceOnlyResultEvidenceStore.NativeId(manifest, "ProjectId") &&
            SourceOnlyResultEvidenceStore.NativeDigest(raw, "ContentDigest") == artifact.ArtifactDigest.Hex &&
            Integer(raw, "ByteSize") == artifact.ByteSize &&
            SourceOnlyResultEvidenceStore.NativeId(raw, "RenderManifestId") == SourceOnlyResultEvidenceStore.NativeId(manifest, "Id") &&
            SourceOnlyResultEvidenceStore.NativeId(raw, "RenderGraphId") == SourceOnlyResultEvidenceStore.NativeId(graph, "Id") &&
            SourceOnlyResultEvidenceStore.NativeDigest(raw, "ProbeEvidenceDigest") == m.ProbeDigest.Hex);
        foreach (string key in new[] { "CompositionVersionId", "CompositionVersionDigest", "ProfileId", "ProfileDigest", "LayoutSpecId", "LayoutSpecDigest" })
            SourceOnlyResultInputValidation.Same(raw.GetProperty(key), graph.GetProperty(key));
        foreach (string key in new[] { "TemplateId", "TemplateVersionDigest" })
            SourceOnlyResultInputValidation.Same(raw.GetProperty(key), manifest.GetProperty(key));
        foreach ((string key, string source) in new[] { ("SubtitleLayoutProfileDigest", "LayoutProfileDigest"), ("SubtitleDisplayProfileDigest", "DisplayProfileDigest") })
        {
            if (inputs.SubtitleEvidence is JsonElement subtitle)
                SourceOnlyResultInputValidation.Same(raw.GetProperty(key), subtitle.GetProperty(source));
            else SourceOnlyResultCodec.Require(raw.GetProperty(key).ValueKind == JsonValueKind.Null);
        }
        SourceOnlyResultInputValidation.Same(raw.GetProperty("Tools"), qcTools);
        OutputQcStreamTiming video = Timing(raw.GetProperty("VideoTiming"));
        OutputQcStreamTiming? audio = raw.GetProperty("AudioTiming").ValueKind == JsonValueKind.Null ? null : Timing(raw.GetProperty("AudioTiming"));
        SourceOnlyResultCodec.Require(video.StreamIndex == Integer(raw, "VideoStreamIndex") &&
            (audio is null ? raw.GetProperty("AudioStreamIndex").ValueKind == JsonValueKind.Null : Integer(raw, "AudioStreamIndex") == audio.StreamIndex) &&
            (audio is not null) == graph.GetProperty("Nodes").EnumerateArray().Any(n => Integer(n, "Kind") == (int)HubRadio.Core.Rendering.RenderNodeKind.Audio));
        bool continuous = SourceOnlyResultObservationBuilder.ValidateTiming(video);
        if (audio is not null) _ = SourceOnlyResultObservationBuilder.ValidateTiming(audio);
        RationalNumber frameRate = new(Integer(inputs.Profile.GetProperty("FrameRate"), "Numerator"), Integer(inputs.Profile.GetProperty("FrameRate"), "Denominator"));
        SourceOnlyResultCodec.Require(video.PresentationStart == m.VideoStart && video.PresentationEnd == m.VideoEnd &&
            video.ObservedFrameCount == m.VideoFrameCount && video.TimeBase == m.VideoTimeBase && video.EvidenceDigest == m.VideoTimingDigest &&
            audio?.PresentationStart == m.AudioStart && audio?.PresentationEnd == m.AudioEnd && audio?.ObservedFrameCount == m.AudioFrameCount &&
            audio?.TimeBase == m.AudioTimeBase && audio?.EvidenceDigest == m.AudioTimingDigest && m.VideoTimingContinuous == continuous &&
            m.VideoFrameCadenceExact == SourceOnlyResultObservationBuilder.HasExactCadence(video, frameRate));
        JsonElement duration = raw.GetProperty("DurationMeasurements");
        string outputId = SourceOnlyResultEvidenceStore.NativeId(graph, "OutputNodeId");
        RationalTime expected = SourceOnlyResultInputValidation.ReadTime(graph.GetProperty("Nodes").EnumerateArray()
            .Single(n => SourceOnlyResultEvidenceStore.NativeId(n, "Id") == outputId).GetProperty("OutputRange").GetProperty("Duration"));
        SourceOnlyResultCodec.Require(SourceOnlyResultInputValidation.ReadTime(duration.GetProperty("ExpectedDuration")) == expected && m.ExpectedDuration == expected &&
            SourceOnlyResultInputValidation.ReadTime(duration.GetProperty("VideoDurationDelta")) == m.VideoDuration - expected &&
            OptionalTime(duration.GetProperty("AudioDurationDelta")) == m.AudioDuration - expected &&
            OptionalTime(duration.GetProperty("AudioVideoStartOffset")) == m.AudioVideoStartOffset &&
            OptionalTime(duration.GetProperty("AudioVideoEndOffset")) == m.AudioVideoEndOffset);
        JsonElement decode = raw.GetProperty("Decode");
        SourceOnlyResultCodec.Require(decode.GetProperty("DecodedSuccessfully").GetBoolean() && m.DecodedSuccessfully &&
            TimeSpan.TryParseExact(decode.GetProperty("Elapsed").GetString(), "c", CultureInfo.InvariantCulture, out TimeSpan elapsed) && elapsed >= TimeSpan.Zero);
        JsonElement luma = raw.GetProperty("LumaMeasurements");
        JsonElement[] frames = luma.GetProperty("Frames").EnumerateArray().ToArray();
        SourceOnlyResultCodec.Require(frames.Length == video.ObservedFrameCount && Integer(luma, "MeasuredFrameCount") == frames.Length &&
            SourceOnlyResultEvidenceStore.NativeDigest(luma, "EvidenceDigest") == m.LumaDigest.Hex);
        decimal sum = 0, min = 255, max = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            decimal average = Number(frames[i].GetProperty("FrameAverage"));
            SourceOnlyResultCodec.Require(Integer(frames[i], "FrameNumber") == i &&
                Integer(frames[i], "PresentationTimestampTicks") == video.Entries[i].PresentationTimestampTicks && average is >= 0 and <= 255);
            sum = checked(sum + average); min = Math.Min(min, average); max = Math.Max(max, average);
        }
        SourceOnlyResultCodec.Require(min == m.MinimumLuma && max == m.MaximumLuma && sum / frames.Length == m.MeanLuma &&
            Number(luma.GetProperty("MinimumFrameAverage")) == min && Number(luma.GetProperty("MaximumFrameAverage")) == max &&
            Number(luma.GetProperty("MeanFrameAverage")) == m.MeanLuma);
        JsonElement loudness = raw.GetProperty("AudioMeasurements");
        if (audio is null) SourceOnlyResultCodec.Require(loudness.ValueKind == JsonValueKind.Null && m.AudioDigest is null);
        else SourceOnlyResultCodec.Require(SourceOnlyResultEvidenceStore.NativeDigest(loudness, "EvidenceDigest") == m.AudioDigest?.Hex &&
            Value(loudness.GetProperty("IntegratedLoudnessLufs")) == m.Loudness && Value(loudness.GetProperty("TruePeakDbfs")) == m.TruePeak);
        // Standard outputs have burned-in or absent subtitles, not an unverified muxed stream.
        SourceOnlyResultCodec.Require(raw.GetProperty("SubtitleStreamIndex").ValueKind == JsonValueKind.Null &&
            raw.GetProperty("SubtitleTiming").ValueKind == JsonValueKind.Null && m.SubtitleTimingDigest is null &&
            SourceOnlyResultCodec.Write(SourceOnlyOutputEvaluation.CuesDto(cues)) == SourceOnlyResultCodec.Write(SourceOnlyOutputEvaluation.CuesDto(m.Cues)));
    }

    private static OutputQcStreamTiming Timing(JsonElement value)
    {
        JsonElement[] entries = value.GetProperty("Entries").EnumerateArray().ToArray();
        SourceOnlyResultCodec.Require(entries.Length is > 0 and <= SourceOnlyResultEvidenceStore.MaximumNativeArrayLength);
        var timing = new OutputQcStreamTiming(checked((int)Integer(value, "StreamIndex")), entries.Select(e => new OutputQcTimingEntry(
            checked((int)Integer(e, "Sequence")), Integer(e, "PresentationTimestampTicks"), Integer(e, "DurationTicks"))).ToArray(),
            SourceOnlyResultInputValidation.ReadTime(value.GetProperty("TimeBase")),
            SourceOnlyResultInputValidation.ReadTime(value.GetProperty("PresentationStart")),
            SourceOnlyResultInputValidation.ReadTime(value.GetProperty("PresentationEnd")),
            SourceOnlyResultInputValidation.Digest(value, "EvidenceDigest"));
        SourceOnlyResultCodec.Require(Integer(value, "ObservedFrameCount") == timing.ObservedFrameCount &&
            SourceOnlyResultInputValidation.ReadTime(value.GetProperty("ObservedDuration")) == timing.ObservedDuration);
        return timing;
    }
    private static SourceOnlyResultMeasuredValue Value(JsonElement v) => new((OutputQcMeasuredValueKind)Integer(v, "Kind"),
        v.GetProperty("FiniteValue").ValueKind == JsonValueKind.Null ? null : Number(v.GetProperty("FiniteValue")));
    private static RationalTime? OptionalTime(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : SourceOnlyResultInputValidation.ReadTime(v);
    private static decimal Number(JsonElement v) => JsonSerializer.Deserialize<decimal>(v, SourceOnlyResultCodec.Options);
    private static long Integer(JsonElement value, string key) => SourceOnlyResultEvidenceStore.Integer(value.GetProperty(key));
}