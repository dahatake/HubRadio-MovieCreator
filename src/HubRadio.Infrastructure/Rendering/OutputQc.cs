using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Rendering;

public enum OutputQcStage
{
    Evidence,
    Probe,
    VideoTiming,
    AudioTiming,
    SubtitleTiming,
    Decode,
    ScreenLuma,
    AudioLoudness,
}

public enum OutputQcFailureReason
{
    GraphInvalid,
    ManifestBindingMismatch,
    SubtitleEvidenceInvalid,
    RegisteredArtifactInvalid,
    ArtifactBindingMismatch,
    ToolVerificationFailed,
    ProcessTimedOut,
    ProcessFailed,
    OutputTruncated,
    InvalidEncoding,
    ProbeOutputInvalid,
    ContainerMismatch,
    StreamTopologyMismatch,
    StreamMetadataMismatch,
    TimingOutputInvalid,
    NonMonotonicTiming,
    DecodeFailed,
    LumaOutputInvalid,
    LoudnessOutputInvalid,
}

public enum OutputQcRemediationKind
{
    RepeatQualityControl,
    TechnicalRerender,
    ContentRevisionAndReapproval,
}

public enum OutputQcReviewArea
{
    Duration,
    AudioVideoSynchronization,
    SubtitlePresentation,
    ScreenLuma,
    ProgramLoudness,
    TruePeak,
}

public enum OutputQcMeasuredValueKind
{
    Finite,
    NegativeInfinity,
    PositiveInfinity,
}

public sealed class OutputQcException : Exception
{
    internal OutputQcException(
        OutputQcFailureReason reason,
        OutputQcStage stage)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        Stage = stage;
        Remediation = GetRemediation(reason);
    }

    public OutputQcFailureReason Reason { get; }

    public OutputQcStage Stage { get; }

    public OutputQcRemediationKind Remediation { get; }

    private static string GetStableMessage(OutputQcFailureReason reason) =>
        reason switch
        {
            OutputQcFailureReason.GraphInvalid =>
                "The render graph is invalid for output quality control.",
            OutputQcFailureReason.ManifestBindingMismatch =>
                "The render manifest does not match the approved render evidence.",
            OutputQcFailureReason.SubtitleEvidenceInvalid =>
                "The subtitle layout or display evidence is invalid.",
            OutputQcFailureReason.RegisteredArtifactInvalid =>
                "The registered render artifact could not be validated.",
            OutputQcFailureReason.ArtifactBindingMismatch =>
                "The registered render artifact does not match the render manifest.",
            OutputQcFailureReason.ToolVerificationFailed =>
                "An output quality-control tool could not be verified.",
            OutputQcFailureReason.ProcessTimedOut =>
                "An output quality-control process exceeded its timeout.",
            OutputQcFailureReason.ProcessFailed =>
                "An output quality-control process failed.",
            OutputQcFailureReason.OutputTruncated =>
                "Output quality-control evidence exceeded its capture limit.",
            OutputQcFailureReason.InvalidEncoding =>
                "Output quality-control evidence is not valid UTF-8.",
            OutputQcFailureReason.ProbeOutputInvalid =>
                "The final-output media metadata is invalid.",
            OutputQcFailureReason.ContainerMismatch =>
                "The final output is not the required MP4 container.",
            OutputQcFailureReason.StreamTopologyMismatch =>
                "The final-output stream topology does not match the render graph.",
            OutputQcFailureReason.StreamMetadataMismatch =>
                "The final-output stream metadata does not match the render profile.",
            OutputQcFailureReason.TimingOutputInvalid =>
                "Observed final-output timing evidence is invalid.",
            OutputQcFailureReason.NonMonotonicTiming =>
                "Observed final-output presentation timestamps are not strictly increasing.",
            OutputQcFailureReason.DecodeFailed =>
                "The final output did not pass full decode validation.",
            OutputQcFailureReason.LumaOutputInvalid =>
                "The final-output luma measurement is invalid.",
            OutputQcFailureReason.LoudnessOutputInvalid =>
                "The final-output loudness measurement is invalid.",
            _ => "Output quality control failed.",
        };

    private static OutputQcRemediationKind GetRemediation(
        OutputQcFailureReason reason) =>
        reason switch
        {
            OutputQcFailureReason.GraphInvalid or
            OutputQcFailureReason.SubtitleEvidenceInvalid =>
                OutputQcRemediationKind.ContentRevisionAndReapproval,
            OutputQcFailureReason.ManifestBindingMismatch or
            OutputQcFailureReason.RegisteredArtifactInvalid or
            OutputQcFailureReason.ArtifactBindingMismatch or
            OutputQcFailureReason.ProbeOutputInvalid or
            OutputQcFailureReason.ContainerMismatch or
            OutputQcFailureReason.StreamTopologyMismatch or
            OutputQcFailureReason.StreamMetadataMismatch or
            OutputQcFailureReason.TimingOutputInvalid or
            OutputQcFailureReason.NonMonotonicTiming or
            OutputQcFailureReason.DecodeFailed or
            OutputQcFailureReason.LumaOutputInvalid or
            OutputQcFailureReason.LoudnessOutputInvalid =>
                OutputQcRemediationKind.TechnicalRerender,
            _ => OutputQcRemediationKind.RepeatQualityControl,
        };
}

public sealed class OutputQcSubtitleLayoutEvidence
{
    public OutputQcSubtitleLayoutEvidence(
        EntityId<CueIdTag> cueId,
        SubtitleLayoutResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(cueId);
        ArgumentNullException.ThrowIfNull(resolution);

        CueId = cueId;
        Resolution = resolution;
    }

    public EntityId<CueIdTag> CueId { get; }

    public SubtitleLayoutResolution Resolution { get; }
}

public sealed class OutputQcSubtitleEvidence
{
    public OutputQcSubtitleEvidence(
        ContentDigest layoutProfileDigest,
        ContentDigest displayProfileDigest,
        SubtitleDisplayResult displayResult,
        IReadOnlyList<OutputQcSubtitleLayoutEvidence> layouts)
    {
        ArgumentNullException.ThrowIfNull(layoutProfileDigest);
        ArgumentNullException.ThrowIfNull(displayProfileDigest);
        ArgumentNullException.ThrowIfNull(displayResult);
        ArgumentNullException.ThrowIfNull(layouts);

        var cueIds = new HashSet<EntityId<CueIdTag>>();
        var sourceCandidateIds = new HashSet<EntityId<SubtitleCueCandidateIdTag>>();
        var copied = new OutputQcSubtitleLayoutEvidence[layouts.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            OutputQcSubtitleLayoutEvidence evidence = layouts[index]
                ?? throw new ArgumentException(
                    "Subtitle layout evidence must not contain null values.",
                    nameof(layouts));
            if (!cueIds.Add(evidence.CueId) ||
                !sourceCandidateIds.Add(evidence.Resolution.SourceCandidateId))
            {
                throw new ArgumentException(
                    "Subtitle layout evidence must use unique cue and source-candidate identities.",
                    nameof(layouts));
            }

            copied[index] = evidence;
        }

        LayoutProfileDigest = layoutProfileDigest;
        DisplayProfileDigest = displayProfileDigest;
        DisplayResult = displayResult;
        Layouts = Array.AsReadOnly(copied);
    }

    public ContentDigest LayoutProfileDigest { get; }

    public ContentDigest DisplayProfileDigest { get; }

    public SubtitleDisplayResult DisplayResult { get; }

    public IReadOnlyList<OutputQcSubtitleLayoutEvidence> Layouts { get; }
}

public sealed class OutputQcRequest
{
    public OutputQcRequest(
        EntityId<ArtifactIdTag> outputArtifactId,
        RenderManifest renderManifest,
        RenderGraph graph,
        RenderGraphValidationContext validationContext,
        OutputQcSubtitleEvidence? subtitleEvidence,
        TimeSpan processTimeout)
    {
        ArgumentNullException.ThrowIfNull(outputArtifactId);
        ArgumentNullException.ThrowIfNull(renderManifest);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(validationContext);
        if (processTimeout <= TimeSpan.Zero ||
            processTimeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processTimeout),
                "The output quality-control timeout must be positive and supported by the runtime timer.");
        }

        OutputArtifactId = outputArtifactId;
        RenderManifest = renderManifest;
        Graph = graph;
        ValidationContext = validationContext;
        SubtitleEvidence = subtitleEvidence;
        ProcessTimeout = processTimeout;
    }

    public EntityId<ArtifactIdTag> OutputArtifactId { get; }

    public RenderManifest RenderManifest { get; }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }

    public OutputQcSubtitleEvidence? SubtitleEvidence { get; }

    public TimeSpan ProcessTimeout { get; }
}

public sealed class OutputQcToolEvidence
{
    internal OutputQcToolEvidence(
        string name,
        string version,
        ContentDigest executableDigest,
        long executableByteSize)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(executableDigest);
        if (string.IsNullOrWhiteSpace(name) ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A QC tool name must be trimmed and nonblank.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(version) ||
            !string.Equals(version, version.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A QC tool version must be trimmed and nonblank.", nameof(version));
        }

        if (executableByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(executableByteSize));
        }

        Name = name;
        Version = version;
        ExecutableDigest = executableDigest;
        ExecutableByteSize = executableByteSize;
    }

    public string Name { get; }

    public string Version { get; }

    public ContentDigest ExecutableDigest { get; }

    public long ExecutableByteSize { get; }
}

public sealed class OutputQcTimingEntry
{
    internal OutputQcTimingEntry(
        int sequence,
        long presentationTimestampTicks,
        long durationTicks)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (durationTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationTicks));
        }

        Sequence = sequence;
        PresentationTimestampTicks = presentationTimestampTicks;
        DurationTicks = durationTicks;
    }

    public int Sequence { get; }

    public long PresentationTimestampTicks { get; }

    public long DurationTicks { get; }
}

public sealed class OutputQcStreamTiming
{
    internal OutputQcStreamTiming(
        int streamIndex,
        IReadOnlyList<OutputQcTimingEntry> entries,
        RationalTime timeBase,
        RationalTime presentationStart,
        RationalTime presentationEnd,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex));
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "Observed stream timing must contain at least one entry.",
                nameof(entries));
        }

        if (timeBase <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(nameof(timeBase));
        }

        if (presentationEnd <= presentationStart)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationEnd));
        }

        var copied = new OutputQcTimingEntry[entries.Count];
        long? previousTimestamp = null;
        for (int index = 0; index < copied.Length; index++)
        {
            OutputQcTimingEntry entry = entries[index]
                ?? throw new ArgumentException(
                    "Observed stream timing entries must not contain null values.",
                    nameof(entries));
            if (entry.Sequence != index ||
                previousTimestamp is not null &&
                entry.PresentationTimestampTicks <= previousTimestamp.Value)
            {
                throw new ArgumentException(
                    "Observed stream timing entries must be ordered and strictly increasing.",
                    nameof(entries));
            }

            copied[index] = entry;
            previousTimestamp = entry.PresentationTimestampTicks;
        }

        StreamIndex = streamIndex;
        Entries = Array.AsReadOnly(copied);
        TimeBase = timeBase;
        PresentationStart = presentationStart;
        PresentationEnd = presentationEnd;
        EvidenceDigest = evidenceDigest;
    }

    public int StreamIndex { get; }

    public IReadOnlyList<OutputQcTimingEntry> Entries { get; }

    public int ObservedFrameCount => Entries.Count;

    public RationalTime TimeBase { get; }

    public RationalTime PresentationStart { get; }

    public RationalTime PresentationEnd { get; }

    public RationalTime ObservedDuration => PresentationEnd - PresentationStart;

    public ContentDigest EvidenceDigest { get; }
}

public sealed class OutputQcSubtitleStreamTiming
{
    internal OutputQcSubtitleStreamTiming(
        int streamIndex,
        IReadOnlyList<TimeRange> cueRanges,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(cueRanges);
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex));
        }

        if (cueRanges.Count == 0)
        {
            throw new ArgumentException(
                "Observed subtitle timing must contain at least one cue.",
                nameof(cueRanges));
        }

        var copied = new TimeRange[cueRanges.Count];
        TimeRange? previous = null;
        for (int index = 0; index < copied.Length; index++)
        {
            TimeRange range = cueRanges[index];
            if (range.Duration <= default(RationalTime) ||
                previous is not null && range.Start < previous.Value.End)
            {
                throw new ArgumentException(
                    "Observed subtitle cue ranges must be positive, ordered, and nonoverlapping.",
                    nameof(cueRanges));
            }

            copied[index] = range;
            previous = range;
        }

        StreamIndex = streamIndex;
        CueRanges = Array.AsReadOnly(copied);
        EvidenceDigest = evidenceDigest;
    }

    public int StreamIndex { get; }

    public IReadOnlyList<TimeRange> CueRanges { get; }

    public int ObservedCueCount => CueRanges.Count;

    public ContentDigest EvidenceDigest { get; }
}

public sealed class OutputQcDurationMeasurements
{
    internal OutputQcDurationMeasurements(
        RationalTime expectedDuration,
        OutputQcStreamTiming video,
        OutputQcStreamTiming? audio)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (expectedDuration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDuration));
        }

        ExpectedDuration = expectedDuration;
        VideoDurationDelta = video.ObservedDuration - expectedDuration;
        AudioDurationDelta = audio is null
            ? null
            : audio.ObservedDuration - expectedDuration;
        AudioVideoStartOffset = audio is null
            ? null
            : audio.PresentationStart - video.PresentationStart;
        AudioVideoEndOffset = audio is null
            ? null
            : audio.PresentationEnd - video.PresentationEnd;
    }

    public RationalTime ExpectedDuration { get; }

    public RationalTime VideoDurationDelta { get; }

    public RationalTime? AudioDurationDelta { get; }

    public RationalTime? AudioVideoStartOffset { get; }

    public RationalTime? AudioVideoEndOffset { get; }
}

public sealed class OutputQcLumaFrameMeasurement
{
    internal OutputQcLumaFrameMeasurement(
        int frameNumber,
        long presentationTimestampTicks,
        decimal frameAverage)
    {
        if (frameNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameNumber));
        }

        if (frameAverage < 0m || frameAverage > 255m)
        {
            throw new ArgumentOutOfRangeException(nameof(frameAverage));
        }

        FrameNumber = frameNumber;
        PresentationTimestampTicks = presentationTimestampTicks;
        FrameAverage = frameAverage;
    }

    public int FrameNumber { get; }

    public long PresentationTimestampTicks { get; }

    public decimal FrameAverage { get; }
}

public sealed class OutputQcLumaMeasurements
{
    internal OutputQcLumaMeasurements(
        IReadOnlyList<OutputQcLumaFrameMeasurement> frames,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        if (frames.Count == 0)
        {
            throw new ArgumentException(
                "Luma measurements must contain at least one frame.",
                nameof(frames));
        }

        var copied = new OutputQcLumaFrameMeasurement[frames.Count];
        long? previousTimestamp = null;
        decimal sum = 0m;
        for (int index = 0; index < copied.Length; index++)
        {
            OutputQcLumaFrameMeasurement frame = frames[index]
                ?? throw new ArgumentException(
                    "Luma measurements must not contain null frames.",
                    nameof(frames));
            if (frame.FrameNumber != index ||
                previousTimestamp is not null &&
                frame.PresentationTimestampTicks <= previousTimestamp.Value)
            {
                throw new ArgumentException(
                    "Luma frames must be ordered and have strictly increasing timestamps.",
                    nameof(frames));
            }

            sum = checked(sum + frame.FrameAverage);
            copied[index] = frame;
            previousTimestamp = frame.PresentationTimestampTicks;
        }

        Frames = Array.AsReadOnly(copied);
        MinimumFrameAverage = copied.Min(static frame => frame.FrameAverage);
        MaximumFrameAverage = copied.Max(static frame => frame.FrameAverage);
        MeanFrameAverage = sum / copied.Length;
        EvidenceDigest = evidenceDigest;
    }

    public IReadOnlyList<OutputQcLumaFrameMeasurement> Frames { get; }

    public int MeasuredFrameCount => Frames.Count;

    public decimal MinimumFrameAverage { get; }

    public decimal MaximumFrameAverage { get; }

    public decimal MeanFrameAverage { get; }

    public ContentDigest EvidenceDigest { get; }
}

public sealed class OutputQcMeasuredValue
{
    private OutputQcMeasuredValue(
        OutputQcMeasuredValueKind kind,
        decimal? finiteValue)
    {
        if (!Enum.IsDefined(kind) ||
            (kind == OutputQcMeasuredValueKind.Finite) != (finiteValue is not null))
        {
            throw new ArgumentException("A measured value has an inconsistent kind.", nameof(kind));
        }

        Kind = kind;
        FiniteValue = finiteValue;
    }

    public OutputQcMeasuredValueKind Kind { get; }

    public decimal? FiniteValue { get; }

    internal static OutputQcMeasuredValue Finite(decimal value) =>
        new(OutputQcMeasuredValueKind.Finite, value);

    internal static OutputQcMeasuredValue NegativeInfinity { get; } =
        new(OutputQcMeasuredValueKind.NegativeInfinity, null);

    internal static OutputQcMeasuredValue PositiveInfinity { get; } =
        new(OutputQcMeasuredValueKind.PositiveInfinity, null);
}

public sealed class OutputQcAudioMeasurements
{
    internal OutputQcAudioMeasurements(
        OutputQcMeasuredValue integratedLoudnessLufs,
        OutputQcMeasuredValue truePeakDbfs,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(integratedLoudnessLufs);
        ArgumentNullException.ThrowIfNull(truePeakDbfs);
        ArgumentNullException.ThrowIfNull(evidenceDigest);

        IntegratedLoudnessLufs = integratedLoudnessLufs;
        TruePeakDbfs = truePeakDbfs;
        EvidenceDigest = evidenceDigest;
    }

    public OutputQcMeasuredValue IntegratedLoudnessLufs { get; }

    public OutputQcMeasuredValue TruePeakDbfs { get; }

    public ContentDigest EvidenceDigest { get; }
}

public sealed class OutputQcDecodeEvidence
{
    internal OutputQcDecodeEvidence(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        Elapsed = elapsed;
    }

    public bool DecodedSuccessfully => true;

    public TimeSpan Elapsed { get; }
}

public sealed class OutputQcRemediationPolicy
{
    internal OutputQcRemediationPolicy()
    {
    }

    public OutputQcRemediationKind TechnicalCorrection =>
        OutputQcRemediationKind.TechnicalRerender;

    public bool TechnicalRerenderPreservesCompositionVersion => true;

    public bool TechnicalRerenderRequiresRenewedApproval => false;

    public OutputQcRemediationKind ContentCorrection =>
        OutputQcRemediationKind.ContentRevisionAndReapproval;

    public bool ContentCorrectionRequiresChildCompositionVersion => true;

    public bool ContentCorrectionRequiresRenewedApproval => true;
}

public sealed class OutputQcResult
{
    internal OutputQcResult(
        StoredArtifact artifact,
        OutputQcRequest request,
        ContentDigest probeEvidenceDigest,
        int videoStreamIndex,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        OutputQcStreamTiming videoTiming,
        OutputQcStreamTiming? audioTiming,
        OutputQcSubtitleStreamTiming? subtitleTiming,
        OutputQcDecodeEvidence decode,
        OutputQcLumaMeasurements luma,
        OutputQcAudioMeasurements? audioMeasurements)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(probeEvidenceDigest);
        ArgumentNullException.ThrowIfNull(videoTiming);
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(luma);
        if ((subtitleStreamIndex is null) != (subtitleTiming is null) ||
            subtitleTiming is not null &&
            subtitleTiming.StreamIndex != subtitleStreamIndex)
        {
            throw new ArgumentException(
                "Subtitle stream identity and timing evidence must be supplied together.",
                nameof(subtitleTiming));
        }

        ArtifactId = artifact.Id;
        ProjectId = artifact.ProjectId;
        ContentDigest = artifact.ContentDigest;
        ByteSize = artifact.ByteSize;
        RenderManifestId = request.RenderManifest.Id;
        RenderGraphId = request.Graph.Id;
        CompositionVersionId = request.RenderManifest.CompositionVersionId;
        CompositionVersionDigest = request.RenderManifest.CompositionVersionDigest;
        ProfileId = request.RenderManifest.ProfileId;
        ProfileDigest = request.RenderManifest.ProfileDigest;
        LayoutSpecId = request.Graph.LayoutSpecId;
        LayoutSpecDigest = request.Graph.LayoutSpecDigest;
        TemplateId = request.RenderManifest.TemplateId;
        TemplateVersionDigest = request.RenderManifest.TemplateVersionDigest;
        SubtitleLayoutProfileDigest = request.SubtitleEvidence?.LayoutProfileDigest;
        SubtitleDisplayProfileDigest = request.SubtitleEvidence?.DisplayProfileDigest;
        ProbeEvidenceDigest = probeEvidenceDigest;
        VideoStreamIndex = videoStreamIndex;
        AudioStreamIndex = audioStreamIndex;
        SubtitleStreamIndex = subtitleStreamIndex;
        VideoTiming = videoTiming;
        AudioTiming = audioTiming;
        SubtitleTiming = subtitleTiming;
        DurationMeasurements = new OutputQcDurationMeasurements(
            request.Graph.Nodes.Single(node => node.Id == request.Graph.OutputNodeId)
                .OutputRange.Duration,
            videoTiming,
            audioTiming);
        Decode = decode;
        LumaMeasurements = luma;
        AudioMeasurements = audioMeasurements;
        Tools = Array.AsReadOnly(new[]
        {
            new OutputQcToolEvidence(
                "ffprobe",
                FfprobeClient.CanonicalPackageVersion,
                new ContentDigest(FfprobeClient.CanonicalFfprobeSha256),
                FfprobeClient.CanonicalFfprobeByteSize),
            new OutputQcToolEvidence(
                "ffmpeg",
                DecodeValidator.CanonicalPackageVersion,
                new ContentDigest(DecodeValidator.CanonicalFfmpegSha256),
                DecodeValidator.CanonicalFfmpegByteSize),
        });
        var reviewAreas = new List<OutputQcReviewArea>
        {
            OutputQcReviewArea.Duration,
            OutputQcReviewArea.ScreenLuma,
        };
        if (request.Graph.Nodes.Any(static node =>
            node.Kind == RenderNodeKind.Subtitle))
        {
            reviewAreas.Add(OutputQcReviewArea.SubtitlePresentation);
        }

        if (audioTiming is not null)
        {
            reviewAreas.Add(OutputQcReviewArea.AudioVideoSynchronization);
            reviewAreas.Add(OutputQcReviewArea.ProgramLoudness);
            reviewAreas.Add(OutputQcReviewArea.TruePeak);
        }

        RequiredHumanReviewAreas = Array.AsReadOnly(
            reviewAreas.OrderBy(static area => area).ToArray());
        RemediationPolicy = new OutputQcRemediationPolicy();
    }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public EntityId<RenderManifestIdTag> RenderManifestId { get; }

    public EntityId<RenderGraphIdTag> RenderGraphId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<RenderProfileIdTag> ProfileId { get; }

    public ContentDigest ProfileDigest { get; }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest LayoutSpecDigest { get; }

    public EntityId<TemplateIdTag> TemplateId { get; }

    public ContentDigest TemplateVersionDigest { get; }

    public ContentDigest? SubtitleLayoutProfileDigest { get; }

    public ContentDigest? SubtitleDisplayProfileDigest { get; }

    public ContentDigest ProbeEvidenceDigest { get; }

    public int VideoStreamIndex { get; }

    public int? AudioStreamIndex { get; }

    public int? SubtitleStreamIndex { get; }

    public OutputQcStreamTiming VideoTiming { get; }

    public OutputQcStreamTiming? AudioTiming { get; }

    public OutputQcSubtitleStreamTiming? SubtitleTiming { get; }

    public OutputQcDurationMeasurements DurationMeasurements { get; }

    public OutputQcDecodeEvidence Decode { get; }

    public OutputQcLumaMeasurements LumaMeasurements { get; }

    public OutputQcAudioMeasurements? AudioMeasurements { get; }

    public IReadOnlyList<OutputQcToolEvidence> Tools { get; }

    public IReadOnlyList<OutputQcReviewArea> RequiredHumanReviewAreas { get; }

    public OutputQcRemediationPolicy RemediationPolicy { get; }

    public bool MachineChecksPassed => true;

    public bool HumanReviewRequired => true;

    public bool ApprovedNumericThresholdsApplied => false;

    public bool CanProceedToDelivery => false;
}

public sealed class OutputQc
{
    internal const string TimingShowEntries =
        "frame=stream_index,best_effort_timestamp,duration";
    internal const string SubtitleTimingShowEntries =
        "frame=pts,start_display_time,end_display_time,num_rects";
    internal const string LumaFilter =
        "signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-";
    internal const string LoudnessFilter = "loudnorm=print_format=json";

    private const int FileBufferSize = 64 * 1024;
    private const string StreamIndexField = "stream_index";
    private const string PresentationTimestampField = "best_effort_timestamp";
    private const string DurationField = "duration";
    private const string LumaPrefix = "lavfi.signalstats.YAVG=";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ContentDigest CanonicalFfprobeDigest =
        new(FfprobeClient.CanonicalFfprobeSha256);
    private static readonly ContentDigest CanonicalFfmpegDigest =
        new(DecodeValidator.CanonicalFfmpegSha256);
    private static readonly HashSet<string> TimingFields =
    [
        StreamIndexField,
        PresentationTimestampField,
        DurationField,
    ];
    private static readonly HashSet<string> SubtitleTimingFields =
    [
        "media_type",
        "pts",
        "pts_time",
        "format",
        "start_display_time",
        "end_display_time",
        "num_rects",
    ];
    private static readonly HashSet<string> LoudnessFields =
    [
        "input_i",
        "input_tp",
        "input_lra",
        "input_thresh",
        "output_i",
        "output_tp",
        "output_lra",
        "output_thresh",
        "normalization_type",
        "target_offset",
    ];
    private static readonly string[] LoudnessNumericFields =
    [
        "input_i",
        "input_tp",
        "input_lra",
        "input_thresh",
        "output_i",
        "output_tp",
        "output_lra",
        "output_thresh",
        "target_offset",
    ];

    private readonly ValidateArtifactDelegate _validateArtifactAsync;
    private readonly GetArtifactPathDelegate _getArtifactPath;
    private readonly VerifyBinaryDelegate _verifyBinaryAsync;
    private readonly RunProcessDelegate _runProcessAsync;
    private readonly string _ffprobePath;
    private readonly string _ffmpegPath;

    public OutputQc(
        ArtifactStore artifactStore,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string ffprobePath,
        string ffmpegPath)
        : this(
            GetValidateArtifactDelegate(artifactStore),
            GetArtifactPathDelegateFor(artifactStore),
            GetVerifyBinaryDelegate(binaryVerifier),
            GetRunProcessDelegate(processRunner),
            ffprobePath,
            ffmpegPath)
    {
    }

    internal OutputQc(
        ValidateArtifactDelegate validateArtifactAsync,
        GetArtifactPathDelegate getArtifactPath,
        VerifyBinaryDelegate verifyBinaryAsync,
        RunProcessDelegate runProcessAsync,
        string ffprobePath,
        string ffmpegPath)
    {
        ArgumentNullException.ThrowIfNull(validateArtifactAsync);
        ArgumentNullException.ThrowIfNull(getArtifactPath);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);
        ArgumentNullException.ThrowIfNull(runProcessAsync);

        _validateArtifactAsync = validateArtifactAsync;
        _getArtifactPath = getArtifactPath;
        _verifyBinaryAsync = verifyBinaryAsync;
        _runProcessAsync = runProcessAsync;
        _ffprobePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffprobePath,
            nameof(ffprobePath));
        _ffmpegPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
    }

    public async Task<OutputQcResult> RunAsync(
        OutputQcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateEvidence(request, cancellationToken);
        string artifactPath = GetArtifactPath(request.OutputArtifactId);
        using FileStream heldArtifact = OpenHeldArtifact(artifactPath);
        ValidateHeldArtifact(heldArtifact, expectedByteSize: null);

        StoredArtifact artifact = await ValidateRegisteredArtifactAsync(
            request.OutputArtifactId,
            cancellationToken).ConfigureAwait(false);
        ValidateArtifactBinding(request, artifact);
        ValidateHeldArtifact(heldArtifact, artifact.ByteSize);
        using FileStream heldFfprobe = OpenHeldTool(_ffprobePath);
        using FileStream heldFfmpeg = OpenHeldTool(_ffmpegPath);
        ValidateHeldTool(heldFfprobe, FfprobeClient.CanonicalFfprobeByteSize);
        ValidateHeldTool(heldFfmpeg, DecodeValidator.CanonicalFfmpegByteSize);

        await VerifyToolAsync(
            new BinaryVerificationRequest(
                "output-qc.ffprobe",
                _ffprobePath,
                CanonicalFfprobeDigest,
                FfprobeClient.CanonicalFfprobeByteSize),
            cancellationToken).ConfigureAwait(false);
        await VerifyToolAsync(
            new BinaryVerificationRequest(
                "output-qc.ffmpeg",
                _ffmpegPath,
                CanonicalFfmpegDigest,
                DecodeValidator.CanonicalFfmpegByteSize),
            cancellationToken).ConfigureAwait(false);

        ContainerSignature signature = await DetectSignatureAsync(
            heldArtifact,
            cancellationToken).ConfigureAwait(false);
        ProcessRunResult probeProcess = await RunProcessAsync(
            FfprobeClient.CreateProcessRequest(
                _ffprobePath,
                artifactPath,
                request.ProcessTimeout),
            OutputQcStage.Probe,
            cancellationToken).ConfigureAwait(false);
        MediaProbeResult probe = ParseProbeProcess(
            artifact,
            artifactPath,
            signature,
            probeProcess);
        (ProbedMediaStream video, ProbedMediaStream? audio, ProbedMediaStream? subtitle) =
            ValidateOutputStructure(request, probe);

        ProcessRunResult videoTimingProcess = await RunProcessAsync(
            CreateTimingProcessRequest(
                _ffprobePath,
                artifactPath,
                video.Index,
                request.ProcessTimeout),
            OutputQcStage.VideoTiming,
            cancellationToken).ConfigureAwait(false);
        OutputQcStreamTiming videoTiming = ParseTimingProcessResult(
            video,
            videoTimingProcess,
            OutputQcStage.VideoTiming);

        OutputQcStreamTiming? audioTiming = null;
        if (audio is not null)
        {
            ProcessRunResult audioTimingProcess = await RunProcessAsync(
                CreateTimingProcessRequest(
                    _ffprobePath,
                    artifactPath,
                    audio.Index,
                    request.ProcessTimeout),
                OutputQcStage.AudioTiming,
                cancellationToken).ConfigureAwait(false);
            audioTiming = ParseTimingProcessResult(
                audio,
                audioTimingProcess,
                OutputQcStage.AudioTiming);
        }

        OutputQcSubtitleStreamTiming? subtitleTiming = null;
        if (subtitle is not null)
        {
            ProcessRunResult subtitleTimingProcess = await RunProcessAsync(
                CreateSubtitleTimingProcessRequest(
                    _ffprobePath,
                    artifactPath,
                    subtitle.Index,
                    request.ProcessTimeout),
                OutputQcStage.SubtitleTiming,
                cancellationToken).ConfigureAwait(false);
            subtitleTiming = ParseSubtitleTimingProcessResult(
                subtitle,
                subtitleTimingProcess);
            TimeRange[] expectedCueRanges = request.Graph.Nodes
                .Where(static node => node.Kind == RenderNodeKind.Subtitle)
                .Select(static node => node.Cue!)
                .OrderBy(static cue => cue.Range.Start)
                .ThenBy(static cue => cue.Range.End)
                .ThenBy(
                    static cue => cue.Id.ToString(),
                    StringComparer.Ordinal)
                .Select(static cue => cue.Range)
                .ToArray();
            if (!subtitleTiming.CueRanges.SequenceEqual(expectedCueRanges))
            {
                throw Failure(
                    OutputQcFailureReason.TimingOutputInvalid,
                    OutputQcStage.SubtitleTiming);
            }
        }

        ProcessRunResult decodeProcess = await RunProcessAsync(
            CreateDecodeProcessRequest(
                _ffmpegPath,
                artifactPath,
                video.Index,
                audio?.Index,
                request.ProcessTimeout),
            OutputQcStage.Decode,
            cancellationToken).ConfigureAwait(false);
        OutputQcDecodeEvidence decode = ParseDecodeProcessResult(decodeProcess);

        ProcessRunResult lumaProcess = await RunProcessAsync(
            CreateLumaProcessRequest(
                _ffmpegPath,
                artifactPath,
                video.Index,
                request.ProcessTimeout),
            OutputQcStage.ScreenLuma,
            cancellationToken).ConfigureAwait(false);
        OutputQcLumaMeasurements luma = ParseLumaProcessResult(lumaProcess);
        if (luma.MeasuredFrameCount != videoTiming.ObservedFrameCount ||
            !luma.Frames.Select(static frame => frame.PresentationTimestampTicks)
                .SequenceEqual(videoTiming.Entries.Select(
                    static entry => entry.PresentationTimestampTicks)))
        {
            throw Failure(
                OutputQcFailureReason.LumaOutputInvalid,
                OutputQcStage.ScreenLuma);
        }

        OutputQcAudioMeasurements? audioMeasurements = null;
        if (audio is not null)
        {
            ProcessRunResult loudnessProcess = await RunProcessAsync(
                CreateLoudnessProcessRequest(
                    _ffmpegPath,
                    artifactPath,
                    audio.Index,
                    request.ProcessTimeout),
                OutputQcStage.AudioLoudness,
                cancellationToken).ConfigureAwait(false);
            audioMeasurements = ParseLoudnessProcessResult(loudnessProcess);
        }

        ValidateHeldArtifact(heldArtifact, artifact.ByteSize);
        ValidateHeldTool(heldFfprobe, FfprobeClient.CanonicalFfprobeByteSize);
        ValidateHeldTool(heldFfmpeg, DecodeValidator.CanonicalFfmpegByteSize);
        StoredArtifact finalArtifact = await ValidateRegisteredArtifactAsync(
            request.OutputArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (!HasSameArtifact(artifact, finalArtifact) ||
            !string.Equals(
                artifactPath,
                GetArtifactPath(request.OutputArtifactId),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }

        return new OutputQcResult(
            finalArtifact,
            request,
            ComputeEvidenceDigest(probeProcess.StandardOutput, OutputQcStage.Probe),
            video.Index,
            audio?.Index,
            subtitle?.Index,
            videoTiming,
            audioTiming,
            subtitleTiming,
            decode,
            luma,
            audioMeasurements);
    }

    internal static void ValidateEvidence(
        OutputQcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        RenderGraphValidationResult graphValidation;
        try
        {
            graphValidation = RenderGraphValidator.Validate(
                request.Graph,
                request.ValidationContext,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(OutputQcFailureReason.GraphInvalid, OutputQcStage.Evidence);
        }

        if (!graphValidation.IsValid)
        {
            throw Failure(OutputQcFailureReason.GraphInvalid, OutputQcStage.Evidence);
        }

        RenderManifest manifest = request.RenderManifest;
        RenderGraph graph = request.Graph;
        RenderGraphValidationContext context = request.ValidationContext;
        bool manifestMatches =
            manifest.ProjectId == context.Project.Id &&
            manifest.CompositionVersionId == graph.CompositionVersionId &&
            manifest.CompositionVersionDigest == graph.CompositionVersionDigest &&
            manifest.TimelineMapId == graph.TimelineMapId &&
            manifest.TimelineMapDigest == graph.TimelineMapDigest &&
            manifest.ProfileId == graph.ProfileId &&
            manifest.ProfileDigest == graph.ProfileDigest &&
            manifest.TemplateId == graph.Template.Id &&
            manifest.TemplateVersionDigest == graph.Template.VersionDigest &&
            HaveSameDigests(
                manifest.Inputs.Select(static input => input.ContentDigest),
                graph.Inputs.Select(static input => input.ContentDigest)) &&
            HaveSameFonts(manifest.Fonts, graph.Fonts) &&
            HasCanonicalRenderTool(manifest.Tools);
        if (!manifestMatches)
        {
            throw Failure(
                OutputQcFailureReason.ManifestBindingMismatch,
                OutputQcStage.Evidence);
        }

        ValidateSubtitleEvidence(request);
    }

    internal static ProcessRunRequest CreateTimingProcessRequest(
        string ffprobePath,
        string artifactPath,
        int streamIndex,
        TimeSpan timeout)
    {
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex));
        }

        (string tool, string artifact, string workingDirectory) =
            ValidateProcessPaths(ffprobePath, artifactPath);
        return new ProcessRunRequest(
            tool,
            [
                "-v",
                "error",
                "-select_streams",
                streamIndex.ToString(CultureInfo.InvariantCulture),
                "-show_frames",
                "-show_entries",
                TimingShowEntries,
                "-of",
                PtsIndexer.CompactOutputFormat,
                artifact,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.MaximumMaxCapturedBytes);
    }

    internal static ProcessRunRequest CreateDecodeProcessRequest(
        string ffmpegPath,
        string artifactPath,
        int videoStreamIndex,
        int? audioStreamIndex,
        TimeSpan timeout) =>
        DecodeValidator.CreateProcessRequest(
            ffmpegPath,
            artifactPath,
            videoStreamIndex,
            audioStreamIndex,
            timeout);

    internal static ProcessRunRequest CreateSubtitleTimingProcessRequest(
        string ffprobePath,
        string artifactPath,
        int streamIndex,
        TimeSpan timeout)
    {
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex));
        }

        (string tool, string artifact, string workingDirectory) =
            ValidateProcessPaths(ffprobePath, artifactPath);
        return new ProcessRunRequest(
            tool,
            [
                "-v",
                "error",
                "-select_streams",
                streamIndex.ToString(CultureInfo.InvariantCulture),
                "-show_frames",
                "-show_entries",
                SubtitleTimingShowEntries,
                "-of",
                PtsIndexer.CompactOutputFormat,
                artifact,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.MaximumMaxCapturedBytes);
    }

    internal static ProcessRunRequest CreateLumaProcessRequest(
        string ffmpegPath,
        string artifactPath,
        int videoStreamIndex,
        TimeSpan timeout)
    {
        if (videoStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(videoStreamIndex));
        }

        (string tool, string artifact, string workingDirectory) =
            ValidateProcessPaths(ffmpegPath, artifactPath);
        return new ProcessRunRequest(
            tool,
            [
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-v",
                "error",
                "-i",
                artifact,
                "-map",
                string.Concat(
                    "0:",
                    videoStreamIndex.ToString(CultureInfo.InvariantCulture)),
                "-an",
                "-sn",
                "-vf",
                LumaFilter,
                "-f",
                "null",
                "NUL",
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.MaximumMaxCapturedBytes);
    }

    internal static ProcessRunRequest CreateLoudnessProcessRequest(
        string ffmpegPath,
        string artifactPath,
        int audioStreamIndex,
        TimeSpan timeout)
    {
        if (audioStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        (string tool, string artifact, string workingDirectory) =
            ValidateProcessPaths(ffmpegPath, artifactPath);
        return new ProcessRunRequest(
            tool,
            [
                "-hide_banner",
                "-nostdin",
                "-nostats",
                "-v",
                "info",
                "-i",
                artifact,
                "-map",
                string.Concat(
                    "0:",
                    audioStreamIndex.ToString(CultureInfo.InvariantCulture)),
                "-vn",
                "-sn",
                "-af",
                LoudnessFilter,
                "-f",
                "null",
                "NUL",
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.MaximumMaxCapturedBytes);
    }

    internal static OutputQcStreamTiming ParseTimingProcessResult(
        ProbedMediaStream stream,
        ProcessRunResult processResult,
        OutputQcStage stage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateStructuredStandardOutput(
            processResult,
            stage,
            OutputQcFailureReason.TimingOutputInvalid);
        return ParseTimingOutput(stream, processResult.StandardOutput, stage);
    }

    internal static OutputQcStreamTiming ParseTimingOutput(
        ProbedMediaStream stream,
        string standardOutput,
        OutputQcStage stage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ValidateStrictEncoding(standardOutput, stage);
        string[] lines = SplitStrictLines(
            standardOutput,
            OutputQcFailureReason.TimingOutputInvalid,
            stage);
        RationalTime timeBase = new(
            stream.TimeBase.Numerator,
            stream.TimeBase.Denominator);
        RationalTime? first = null;
        RationalTime? previous = null;
        RationalTime? previousEnd = null;
        RationalTime? lastEnd = null;
        var entries = new List<OutputQcTimingEntry>(lines.Length);
        for (int sequence = 0; sequence < lines.Length; sequence++)
        {
            Dictionary<string, string> fields = ParseFields(
                lines[sequence],
                TimingFields,
                stage);
            int parsedStreamIndex = ParseCanonicalNonnegativeInt32(
                ReadRequiredField(fields, StreamIndexField, stage),
                stage);
            if (parsedStreamIndex != stream.Index)
            {
                throw Failure(
                    OutputQcFailureReason.TimingOutputInvalid,
                    stage);
            }

            long timestamp = ParseCanonicalSignedInt64(
                ReadRequiredField(fields, PresentationTimestampField, stage),
                stage);
            long durationTicks = ParseCanonicalPositiveInt64(
                ReadRequiredField(fields, DurationField, stage),
                stage);
            RationalTime presentationTime = ConvertTicks(timestamp, timeBase, stage);
            RationalTime duration = ConvertTicks(durationTicks, timeBase, stage);
            RationalTime end;
            try
            {
                end = presentationTime + duration;
            }
            catch (OverflowException)
            {
                throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
            }

            if (previous is not null &&
                (presentationTime <= previous.Value ||
                 presentationTime < previousEnd!.Value))
            {
                throw Failure(OutputQcFailureReason.NonMonotonicTiming, stage);
            }

            first ??= presentationTime;
            previous = presentationTime;
            previousEnd = end;
            lastEnd = end;
            entries.Add(new OutputQcTimingEntry(
                sequence,
                timestamp,
                durationTicks));
        }

        try
        {
            return new OutputQcStreamTiming(
                stream.Index,
                entries,
                timeBase,
                first!.Value,
                lastEnd!.Value,
                ComputeEvidenceDigest(standardOutput, stage));
        }
        catch (ArgumentException)
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }
    }

    internal static OutputQcLumaMeasurements ParseLumaProcessResult(
        ProcessRunResult processResult)
    {
        ValidateStructuredStandardOutput(
            processResult,
            OutputQcStage.ScreenLuma,
            OutputQcFailureReason.LumaOutputInvalid);
        return ParseLumaOutput(processResult.StandardOutput);
    }

    internal static OutputQcSubtitleStreamTiming ParseSubtitleTimingProcessResult(
        ProbedMediaStream stream,
        ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateStructuredStandardOutput(
            processResult,
            OutputQcStage.SubtitleTiming,
            OutputQcFailureReason.TimingOutputInvalid);
        return ParseSubtitleTimingOutput(stream, processResult.StandardOutput);
    }

    internal static OutputQcSubtitleStreamTiming ParseSubtitleTimingOutput(
        ProbedMediaStream stream,
        string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(standardOutput);
        const OutputQcStage stage = OutputQcStage.SubtitleTiming;
        ValidateStrictEncoding(standardOutput, stage);
        string[] lines = SplitStrictLines(
            standardOutput,
            OutputQcFailureReason.TimingOutputInvalid,
            stage);
        RationalTime timeBase = new(
            stream.TimeBase.Numerator,
            stream.TimeBase.Denominator);
        var ranges = new List<TimeRange>(lines.Length);
        TimeRange? previous = null;
        foreach (string line in lines)
        {
            Dictionary<string, string> fields = ParseFields(
                line,
                SubtitleTimingFields,
                stage);
            if (!string.Equals(
                    ReadRequiredField(fields, "media_type", stage),
                    "subtitle",
                    StringComparison.Ordinal) ||
                !IsCanonicalDecimal(ReadRequiredField(fields, "pts_time", stage)))
            {
                throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
            }

            _ = ParseCanonicalNonnegativeInt32(
                ReadRequiredField(fields, "format", stage),
                stage);
            long presentationTimestamp = ParseCanonicalSignedInt64(
                ReadRequiredField(fields, "pts", stage),
                stage);
            long startDisplayMilliseconds = ParseCanonicalNonnegativeInt64(
                ReadRequiredField(fields, "start_display_time", stage),
                stage);
            long endDisplayMilliseconds = ParseCanonicalPositiveInt64(
                ReadRequiredField(fields, "end_display_time", stage),
                stage);
            _ = ParseCanonicalPositiveInt64(
                ReadRequiredField(fields, "num_rects", stage),
                stage);
            if (endDisplayMilliseconds <= startDisplayMilliseconds)
            {
                throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
            }

            try
            {
                RationalTime baseTime = ConvertTicks(
                    presentationTimestamp,
                    timeBase,
                    stage);
                RationalTime start = baseTime + new RationalTime(
                    startDisplayMilliseconds,
                    1000);
                RationalTime end = baseTime + new RationalTime(
                    endDisplayMilliseconds,
                    1000);
                var range = new TimeRange(start, end - start);
                if (previous is not null && range.Start < previous.Value.End)
                {
                    throw Failure(
                        OutputQcFailureReason.NonMonotonicTiming,
                        stage);
                }

                ranges.Add(range);
                previous = range;
            }
            catch (OutputQcException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is ArgumentException or OverflowException)
            {
                throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
            }
        }

        try
        {
            return new OutputQcSubtitleStreamTiming(
                stream.Index,
                ranges,
                ComputeEvidenceDigest(standardOutput, stage));
        }
        catch (ArgumentException)
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }
    }

    internal static OutputQcLumaMeasurements ParseLumaOutput(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        const OutputQcStage stage = OutputQcStage.ScreenLuma;
        ValidateStrictEncoding(standardOutput, stage);
        string[] lines = SplitStrictLines(
            standardOutput,
            OutputQcFailureReason.LumaOutputInvalid,
            stage);
        if (lines.Length % 2 != 0)
        {
            throw Failure(OutputQcFailureReason.LumaOutputInvalid, stage);
        }

        int frameCount = lines.Length / 2;
        var frames = new List<OutputQcLumaFrameMeasurement>(frameCount);
        long? previousTimestamp = null;
        for (int frame = 0; frame < frameCount; frame++)
        {
            long presentationTimestamp = ParseLumaHeader(
                lines[frame * 2],
                frame);
            if (previousTimestamp is not null &&
                presentationTimestamp <= previousTimestamp.Value)
            {
                throw Failure(OutputQcFailureReason.LumaOutputInvalid, stage);
            }

            string valueLine = lines[(frame * 2) + 1];
            if (!valueLine.StartsWith(LumaPrefix, StringComparison.Ordinal))
            {
                throw Failure(OutputQcFailureReason.LumaOutputInvalid, stage);
            }

            string rawValue = valueLine[LumaPrefix.Length..];
            if (!IsCanonicalDecimal(rawValue) ||
                !decimal.TryParse(
                    rawValue,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal value) ||
                value < 0m || value > 255m)
            {
                throw Failure(OutputQcFailureReason.LumaOutputInvalid, stage);
            }

            frames.Add(new OutputQcLumaFrameMeasurement(
                frame,
                presentationTimestamp,
                value));
            previousTimestamp = presentationTimestamp;
        }

        try
        {
            return new OutputQcLumaMeasurements(
                frames,
                ComputeEvidenceDigest(standardOutput, stage));
        }
        catch (ArgumentException)
        {
            throw Failure(OutputQcFailureReason.LumaOutputInvalid, stage);
        }
    }

    internal static OutputQcAudioMeasurements ParseLoudnessProcessResult(
        ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(processResult);
        const OutputQcStage stage = OutputQcStage.AudioLoudness;
        if (processResult.Truncated)
        {
            throw Failure(OutputQcFailureReason.OutputTruncated, stage);
        }

        if (processResult.ExitCode != 0 ||
            processResult.StandardOutput is null ||
            processResult.StandardError is null ||
            processResult.StandardOutput.Length != 0 ||
            processResult.StandardError.Length == 0 ||
            processResult.Duration < TimeSpan.Zero)
        {
            throw Failure(OutputQcFailureReason.ProcessFailed, stage);
        }

        ValidateStrictEncoding(processResult.StandardError, stage);
        return ParseLoudnessOutput(processResult.StandardError);
    }

    internal static OutputQcAudioMeasurements ParseLoudnessOutput(
        string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        const OutputQcStage stage = OutputQcStage.AudioLoudness;
        ValidateStrictEncoding(standardError, stage);
        int end = standardError.LastIndexOf('}');
        int start = end < 0
            ? -1
            : standardError.LastIndexOf('{', end);
        if (start < 0 || end <= start)
        {
            throw Failure(OutputQcFailureReason.LoudnessOutputInvalid, stage);
        }

        string json = standardError[start..(end + 1)];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Failure(OutputQcFailureReason.LoudnessOutputInvalid, stage);
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!LoudnessFields.Contains(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    !values.TryAdd(property.Name, property.Value.GetString()!))
                {
                    throw Failure(OutputQcFailureReason.LoudnessOutputInvalid, stage);
                }
            }

            if (!LoudnessFields.SetEquals(values.Keys))
            {
                throw Failure(OutputQcFailureReason.LoudnessOutputInvalid, stage);
            }

            var measuredValues = new Dictionary<string, OutputQcMeasuredValue>(
                StringComparer.Ordinal);
            foreach (string field in LoudnessNumericFields)
            {
                measuredValues.Add(field, ParseMeasuredValue(values[field]));
            }

            if (values["normalization_type"] is not ("linear" or "dynamic"))
            {
                throw Failure(OutputQcFailureReason.LoudnessOutputInvalid, stage);
            }

            return new OutputQcAudioMeasurements(
                measuredValues["input_i"],
                measuredValues["input_tp"],
                ComputeEvidenceDigest(json, stage));
        }
        catch (OutputQcException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(OutputQcFailureReason.LoudnessOutputInvalid, stage);
        }
    }

    private static void ValidateSubtitleEvidence(OutputQcRequest request)
    {
        RenderNode[] subtitleNodes = request.Graph.Nodes
            .Where(static node => node.Kind == RenderNodeKind.Subtitle)
            .ToArray();
        OutputQcSubtitleEvidence? evidence = request.SubtitleEvidence;
        if (subtitleNodes.Length == 0)
        {
            if (evidence is not null)
            {
                throw Failure(
                    OutputQcFailureReason.SubtitleEvidenceInvalid,
                    OutputQcStage.Evidence);
            }

            return;
        }

        if (evidence is null ||
            evidence.DisplayResult.ProfileVersionDigest != evidence.DisplayProfileDigest ||
            evidence.DisplayResult.SourceCueCount != subtitleNodes.Length ||
            evidence.Layouts.Count != subtitleNodes.Length ||
            evidence.DisplayResult.DisplayedCues.Count != subtitleNodes.Length ||
            evidence.DisplayResult.OmittedCueIds.Count != 0)
        {
            throw Failure(
                OutputQcFailureReason.SubtitleEvidenceInvalid,
                OutputQcStage.Evidence);
        }

        Dictionary<EntityId<CueIdTag>, RenderNode> nodesByCue = subtitleNodes
            .ToDictionary(static node => node.Cue!.Id);
        if (!evidence.DisplayResult.DisplayedCues.All(displayed =>
            nodesByCue.TryGetValue(displayed.Cue.Id, out RenderNode? node) &&
            displayed.Cue.Kind == CueKind.Subtitle &&
            displayed.Cue.Range == node.Cue!.Range &&
            string.Equals(
                displayed.Cue.RegionName,
                node.Cue.RegionName,
                StringComparison.Ordinal)))
        {
            throw Failure(
                OutputQcFailureReason.SubtitleEvidenceInvalid,
                OutputQcStage.Evidence);
        }

        foreach (OutputQcSubtitleLayoutEvidence layout in evidence.Layouts)
        {
            if (!nodesByCue.TryGetValue(layout.CueId, out RenderNode? node) ||
                layout.Resolution.Status != SubtitleLayoutResolutionStatus.Resolved ||
                layout.Resolution.Plan is null ||
                layout.Resolution.LayoutSpecId != request.Graph.LayoutSpecId ||
                layout.Resolution.ProfileVersionDigest != evidence.LayoutProfileDigest ||
                !string.Equals(
                    layout.Resolution.Plan.Placement.RegionName,
                    node.Cue!.RegionName,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    OutputQcFailureReason.SubtitleEvidenceInvalid,
                    OutputQcStage.Evidence);
            }
        }
    }

    private static void ValidateArtifactBinding(
        OutputQcRequest request,
        StoredArtifact artifact)
    {
        ManifestArtifact[] videoOutputs = request.RenderManifest.Outputs
            .Where(static output =>
                string.Equals(output.ArtifactType, "video", StringComparison.Ordinal))
            .ToArray();
        if (artifact.Id != request.OutputArtifactId ||
            artifact.ProjectId != request.RenderManifest.ProjectId ||
            artifact.Kind != ArtifactKind.Render ||
            videoOutputs.Length != 1 ||
            !string.Equals(
                videoOutputs[0].MediaType,
                "video/mp4",
                StringComparison.Ordinal) ||
            videoOutputs[0].Sha256 != artifact.ContentDigest ||
            videoOutputs[0].ByteSize != artifact.ByteSize ||
            !string.Equals(
                videoOutputs[0].StorageReference,
                artifact.StorageReference,
                StringComparison.Ordinal))
        {
            throw Failure(
                OutputQcFailureReason.ArtifactBindingMismatch,
                OutputQcStage.Evidence);
        }
    }

    private static (
        ProbedMediaStream Video,
        ProbedMediaStream? Audio,
        ProbedMediaStream? Subtitle) ValidateOutputStructure(
        OutputQcRequest request,
        MediaProbeResult probe)
    {
        if (probe.ContainerSignature != ContainerSignature.IsoBaseMediaFtyp ||
            !probe.FormatNames.Contains("mp4", StringComparer.Ordinal) ||
            probe.DeclaredByteSize != probe.ByteSize)
        {
            throw Failure(
                OutputQcFailureReason.ContainerMismatch,
                OutputQcStage.Probe);
        }

        Dictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes =
            request.Graph.Nodes.ToDictionary(static node => node.Id);
        RenderNode output = nodes[request.Graph.OutputNodeId];
        RenderNode[] terminals = output.InputNodeIds.Select(id => nodes[id]).ToArray();
        bool expectsAudio = terminals.Any(static node => node.Kind == RenderNodeKind.Audio);
        bool expectsSubtitleStream =
            !request.ValidationContext.Profile.SubtitleBurnIn &&
            terminals.Any(static node => node.Kind == RenderNodeKind.Subtitle);

        ProbedMediaStream[] videoStreams = probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .ToArray();
        ProbedMediaStream[] audioStreams = probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .ToArray();
        ProbedMediaStream[] otherStreams = probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Other)
            .ToArray();
        bool topologyMatches =
            videoStreams.Length == 1 &&
            audioStreams.Length == (expectsAudio ? 1 : 0) &&
            otherStreams.Length == (expectsSubtitleStream ? 1 : 0) &&
            (!expectsSubtitleStream ||
                string.Equals(
                    otherStreams[0].CodecName,
                    "mov_text",
                    StringComparison.Ordinal));
        if (!topologyMatches)
        {
            throw Failure(
                OutputQcFailureReason.StreamTopologyMismatch,
                OutputQcStage.Probe);
        }

        RenderProfile profile = request.ValidationContext.Profile;
        ProbedMediaStream video = videoStreams[0];
        bool videoMatches =
            string.Equals(video.CodecName, profile.VideoCodec, StringComparison.Ordinal) &&
            string.Equals(video.PixelFormat, "yuv420p", StringComparison.Ordinal) &&
            video.Width == profile.Width &&
            video.Height == profile.Height &&
            video.NominalFrameRate == profile.FrameRate &&
            video.AverageFrameRate == profile.FrameRate;
        bool audioMatches = !expectsAudio ||
            string.Equals(
                audioStreams[0].CodecName,
                profile.AudioCodec,
                StringComparison.Ordinal);
        if (!videoMatches || !audioMatches)
        {
            throw Failure(
                OutputQcFailureReason.StreamMetadataMismatch,
                OutputQcStage.Probe);
        }

        return (
            video,
            expectsAudio ? audioStreams[0] : null,
            expectsSubtitleStream ? otherStreams[0] : null);
    }

    private static MediaProbeResult ParseProbeProcess(
        StoredArtifact artifact,
        string artifactPath,
        ContainerSignature signature,
        ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(artifactPath);
        ArgumentNullException.ThrowIfNull(processResult);
        if (processResult.StandardOutput is null ||
            processResult.StandardError is null ||
            processResult.Duration < TimeSpan.Zero)
        {
            throw Failure(
                OutputQcFailureReason.ProcessFailed,
                OutputQcStage.Probe);
        }

        try
        {
            var staged = new StagedMediaImport(
                EntityId<AssetIdTag>.Parse(artifact.Id.ToString()),
                artifact.ProjectId,
                EntityId<JobIdTag>.Parse(artifact.Id.ToString()),
                Path.GetFileName(artifactPath),
                artifactPath,
                artifact.ContentDigest,
                artifact.ByteSize,
                artifact.CreatedAt);
            return FfprobeClient.ParseProcessResult(staged, signature, processResult);
        }
        catch (MediaProbeException exception)
        {
            OutputQcFailureReason reason = exception.Reason switch
            {
                MediaProbeFailureReason.OutputTruncated =>
                    OutputQcFailureReason.OutputTruncated,
                MediaProbeFailureReason.ProcessFailed =>
                    OutputQcFailureReason.ProcessFailed,
                MediaProbeFailureReason.NoVideoStream =>
                    OutputQcFailureReason.StreamTopologyMismatch,
                _ => OutputQcFailureReason.ProbeOutputInvalid,
            };
            throw Failure(reason, OutputQcStage.Probe);
        }
        catch (Exception)
        {
            throw Failure(
                OutputQcFailureReason.ProbeOutputInvalid,
                OutputQcStage.Probe);
        }
    }

    private async Task<StoredArtifact> ValidateRegisteredArtifactAsync(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken)
    {
        try
        {
            StoredArtifact? artifact = await _validateArtifactAsync(
                artifactId,
                cancellationToken).ConfigureAwait(false);
            return artifact ?? throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }
    }

    private string GetArtifactPath(EntityId<ArtifactIdTag> artifactId)
    {
        try
        {
            return _getArtifactPath(artifactId);
        }
        catch (Exception)
        {
            throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }
    }

    private async Task VerifyToolAsync(
        BinaryVerificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified ||
                result.Reason is not null ||
                !string.Equals(
                    result.ComponentIdentity,
                    request.ComponentIdentity,
                    StringComparison.Ordinal) ||
                result.ExpectedDigest != request.ExpectedDigest ||
                result.ExpectedByteSize != request.ExpectedByteSize ||
                result.ActualDigest != request.ExpectedDigest ||
                result.ActualByteSize != request.ExpectedByteSize)
            {
                throw Failure(
                    OutputQcFailureReason.ToolVerificationFailed,
                    OutputQcStage.Evidence);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutputQcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                OutputQcFailureReason.ToolVerificationFailed,
                OutputQcStage.Evidence);
        }
    }

    private async Task<ProcessRunResult> RunProcessAsync(
        ProcessRunRequest request,
        OutputQcStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessRunResult? result = await _runProcessAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            return result ?? throw Failure(
                OutputQcFailureReason.ProcessFailed,
                stage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(OutputQcFailureReason.ProcessTimedOut, stage);
        }
        catch (Exception)
        {
            throw Failure(OutputQcFailureReason.ProcessFailed, stage);
        }
    }

    private static OutputQcDecodeEvidence ParseDecodeProcessResult(
        ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(processResult);
        const OutputQcStage stage = OutputQcStage.Decode;
        if (processResult.Truncated)
        {
            throw Failure(OutputQcFailureReason.OutputTruncated, stage);
        }

        if (processResult.StandardOutput is null ||
            processResult.StandardError is null ||
            processResult.Duration < TimeSpan.Zero)
        {
            throw Failure(OutputQcFailureReason.ProcessFailed, stage);
        }

        ValidateStrictEncoding(processResult.StandardOutput, stage);
        ValidateStrictEncoding(processResult.StandardError, stage);
        if (processResult.ExitCode != 0 ||
            processResult.StandardOutput.Length != 0 ||
            processResult.StandardError.Length != 0)
        {
            throw Failure(OutputQcFailureReason.DecodeFailed, stage);
        }

        return new OutputQcDecodeEvidence(processResult.Duration);
    }

    private static void ValidateStructuredStandardOutput(
        ProcessRunResult processResult,
        OutputQcStage stage,
        OutputQcFailureReason invalidOutputReason)
    {
        ArgumentNullException.ThrowIfNull(processResult);
        if (processResult.Truncated)
        {
            throw Failure(OutputQcFailureReason.OutputTruncated, stage);
        }

        if (processResult.ExitCode != 0 ||
            processResult.StandardOutput is null ||
            processResult.StandardError is null ||
            processResult.StandardError.Length != 0 ||
            processResult.Duration < TimeSpan.Zero)
        {
            throw Failure(OutputQcFailureReason.ProcessFailed, stage);
        }

        if (processResult.StandardOutput.Length == 0)
        {
            throw Failure(invalidOutputReason, stage);
        }
    }

    private static FileStream OpenHeldArtifact(string artifactPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        try
        {
            return new FileStream(artifactPath, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException or
                IOException or UnauthorizedAccessException or SecurityException or
                ArgumentException or NotSupportedException)
        {
            throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }
    }

    private static FileStream OpenHeldTool(string toolPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        try
        {
            return new FileStream(toolPath, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException or
                IOException or UnauthorizedAccessException or SecurityException or
                ArgumentException or NotSupportedException)
        {
            throw Failure(
                OutputQcFailureReason.ToolVerificationFailed,
                OutputQcStage.Evidence);
        }
    }

    private static void ValidateHeldArtifact(
        FileStream stream,
        long? expectedByteSize)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes & (FileAttributes.Directory |
                               FileAttributes.Device |
                               FileAttributes.ReparsePoint)) != 0 ||
                !stream.CanRead ||
                !stream.CanSeek ||
                stream.Length <= 0 ||
                expectedByteSize is not null && stream.Length != expectedByteSize.Value)
            {
                throw Failure(
                    OutputQcFailureReason.RegisteredArtifactInvalid,
                    OutputQcStage.Evidence);
            }
        }
        catch (OutputQcException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }
    }

    private static void ValidateHeldTool(FileStream stream, long expectedByteSize)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes & (FileAttributes.Directory |
                               FileAttributes.Device |
                               FileAttributes.ReparsePoint)) != 0 ||
                !stream.CanRead ||
                !stream.CanSeek ||
                stream.Length != expectedByteSize)
            {
                throw Failure(
                    OutputQcFailureReason.ToolVerificationFailed,
                    OutputQcStage.Evidence);
            }
        }
        catch (OutputQcException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(
                OutputQcFailureReason.ToolVerificationFailed,
                OutputQcStage.Evidence);
        }
    }

    private static async Task<ContainerSignature> DetectSignatureAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[377];
        int bytesRead = 0;
        try
        {
            stream.Position = 0;
            while (bytesRead < prefix.Length)
            {
                int read = await stream.ReadAsync(
                    prefix.AsMemory(bytesRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            long byteSize = stream.Length;
            stream.Position = 0;
            return FfprobeClient.DetectContainerSignature(
                prefix.AsSpan(0, bytesRead),
                byteSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence);
        }
    }

    private static long ParseLumaHeader(string header, int expectedFrame)
    {
        string[] fields = header.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 ||
            !fields[0].StartsWith("frame:", StringComparison.Ordinal) ||
            !fields[1].StartsWith("pts:", StringComparison.Ordinal) ||
            !fields[2].StartsWith("pts_time:", StringComparison.Ordinal))
        {
            throw Failure(
                OutputQcFailureReason.LumaOutputInvalid,
                OutputQcStage.ScreenLuma);
        }

        string frameValue = fields[0]["frame:".Length..];
        string timestampValue = fields[1]["pts:".Length..];
        string timeValue = fields[2]["pts_time:".Length..];
        if (!IsCanonicalUnsignedInteger(frameValue) ||
            !int.TryParse(
                frameValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int frame) ||
            frame != expectedFrame ||
            !IsCanonicalSignedInteger(timestampValue) ||
            !long.TryParse(
                timestampValue,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long presentationTimestamp) ||
            !IsCanonicalDecimal(timeValue))
        {
            throw Failure(
                OutputQcFailureReason.LumaOutputInvalid,
                OutputQcStage.ScreenLuma);
        }

        return presentationTimestamp;
    }

    private static Dictionary<string, string> ParseFields(
        string line,
        IReadOnlySet<string> knownFields,
        OutputQcStage stage)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string column in line.Split('|', StringSplitOptions.None))
        {
            int separator = column.IndexOf('=');
            if (separator <= 0 || separator != column.LastIndexOf('='))
            {
                throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
            }

            string name = column[..separator];
            string value = column[(separator + 1)..];
            if (!knownFields.Contains(name) || !fields.TryAdd(name, value))
            {
                throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
            }
        }

        return fields;
    }

    private static string ReadRequiredField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        OutputQcStage stage)
    {
        if (!fields.TryGetValue(name, out string? value) ||
            value.Length == 0 ||
            string.Equals(value, "N/A", StringComparison.Ordinal))
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }

        return value;
    }

    private static int ParseCanonicalNonnegativeInt32(
        string value,
        OutputQcStage stage)
    {
        if (!IsCanonicalUnsignedInteger(value) ||
            !int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }

        return parsed;
    }

    private static long ParseCanonicalSignedInt64(
        string value,
        OutputQcStage stage)
    {
        if (!IsCanonicalSignedInteger(value) ||
            !long.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long parsed))
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }

        return parsed;
    }

    private static long ParseCanonicalPositiveInt64(
        string value,
        OutputQcStage stage)
    {
        if (!IsCanonicalUnsignedInteger(value) ||
            !long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsed) ||
            parsed <= 0)
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }

        return parsed;
    }

    private static long ParseCanonicalNonnegativeInt64(
        string value,
        OutputQcStage stage)
    {
        if (!IsCanonicalUnsignedInteger(value) ||
            !long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsed))
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }

        return parsed;
    }

    private static RationalTime ConvertTicks(
        long ticks,
        RationalTime timeBase,
        OutputQcStage stage)
    {
        BigInteger numerator = (BigInteger)ticks * timeBase.Value;
        BigInteger denominator = timeBase.Rate;
        BigInteger divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);
        BigInteger reducedNumerator = numerator / divisor;
        BigInteger reducedDenominator = denominator / divisor;
        if (reducedNumerator < long.MinValue ||
            reducedNumerator > long.MaxValue ||
            reducedDenominator > long.MaxValue)
        {
            throw Failure(OutputQcFailureReason.TimingOutputInvalid, stage);
        }

        return new RationalTime(
            (long)reducedNumerator,
            (long)reducedDenominator);
    }

    private static string[] SplitStrictLines(
        string value,
        OutputQcFailureReason invalidReason,
        OutputQcStage stage)
    {
        if (value.Length == 0)
        {
            throw Failure(invalidReason, stage);
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\r' &&
                (index + 1 >= value.Length || value[index + 1] != '\n'))
            {
                throw Failure(invalidReason, stage);
            }
        }

        int bodyLength = value.Length;
        if (value[^1] == '\n')
        {
            bodyLength--;
            if (bodyLength > 0 && value[bodyLength - 1] == '\r')
            {
                bodyLength--;
            }
        }

        if (bodyLength == 0)
        {
            throw Failure(invalidReason, stage);
        }

        string[] lines = value[..bodyLength].Split('\n', StringSplitOptions.None);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (string.IsNullOrWhiteSpace(line) ||
                !string.Equals(line, line.Trim(), StringComparison.Ordinal) ||
                line.Any(char.IsControl))
            {
                throw Failure(invalidReason, stage);
            }

            lines[index] = line;
        }

        return lines;
    }

    private static OutputQcMeasuredValue ParseMeasuredValue(string value)
    {
        if (string.Equals(value, "-inf", StringComparison.Ordinal))
        {
            return OutputQcMeasuredValue.NegativeInfinity;
        }

        if (string.Equals(value, "inf", StringComparison.Ordinal))
        {
            return OutputQcMeasuredValue.PositiveInfinity;
        }

        if (!IsCanonicalDecimal(value) ||
            !decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal parsed))
        {
            throw Failure(
                OutputQcFailureReason.LoudnessOutputInvalid,
                OutputQcStage.AudioLoudness);
        }

        return OutputQcMeasuredValue.Finite(parsed);
    }

    private static bool IsCanonicalDecimal(string value)
    {
        int digitStart = value.Length > 0 && value[0] == '-' ? 1 : 0;
        if (digitStart == value.Length)
        {
            return false;
        }

        int decimalPoint = value.IndexOf('.', digitStart);
        int integerEnd = decimalPoint < 0 ? value.Length : decimalPoint;
        if (integerEnd - digitStart > 1 && value[digitStart] == '0')
        {
            return false;
        }

        if (decimalPoint < 0)
        {
            return value.AsSpan(digitStart).IndexOfAnyExceptInRange('0', '9') < 0;
        }

        if (decimalPoint != value.LastIndexOf('.') ||
            decimalPoint == digitStart ||
            decimalPoint == value.Length - 1)
        {
            return false;
        }

        return value.AsSpan(digitStart, decimalPoint - digitStart)
                .IndexOfAnyExceptInRange('0', '9') < 0 &&
            value.AsSpan(decimalPoint + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsCanonicalUnsignedInteger(string value)
    {
        if (value.Length == 0 || value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        return value.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsCanonicalSignedInteger(string value)
    {
        int digitStart = value.Length > 0 && value[0] == '-' ? 1 : 0;
        if (digitStart == value.Length ||
            digitStart == 1 && value[digitStart] == '0' ||
            value.Length - digitStart > 1 && value[digitStart] == '0')
        {
            return false;
        }

        return value.AsSpan(digitStart).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static ContentDigest ComputeEvidenceDigest(
        string value,
        OutputQcStage stage)
    {
        ValidateStrictEncoding(value, stage);
        try
        {
            return ContentDigest.Compute(StrictUtf8.GetBytes(value));
        }
        catch (EncoderFallbackException)
        {
            throw Failure(OutputQcFailureReason.InvalidEncoding, stage);
        }
    }

    private static void ValidateStrictEncoding(string value, OutputQcStage stage)
    {
        if (value.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw Failure(OutputQcFailureReason.InvalidEncoding, stage);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(OutputQcFailureReason.InvalidEncoding, stage);
        }
    }

    private static bool HaveSameDigests(
        IEnumerable<ContentDigest> left,
        IEnumerable<ContentDigest> right) =>
        left.GroupBy(static digest => digest)
            .ToDictionary(static group => group.Key, static group => group.Count())
            .OrderBy(static pair => pair.Key.Hex, StringComparer.Ordinal)
            .SequenceEqual(
                right.GroupBy(static digest => digest)
                    .ToDictionary(static group => group.Key, static group => group.Count())
                    .OrderBy(static pair => pair.Key.Hex, StringComparer.Ordinal));

    private static bool HaveSameFonts(
        IReadOnlyList<RenderFontEvidence> left,
        IReadOnlyList<RenderFontEvidence> right) =>
        left.Count == right.Count &&
        left.All(font => right.Contains(font));

    private static bool HasCanonicalRenderTool(
        IReadOnlyList<RenderToolEvidence> tools)
    {
        RenderToolEvidence[] ffmpeg = tools
            .Where(static tool =>
                string.Equals(tool.Name, "ffmpeg", StringComparison.Ordinal))
            .ToArray();
        return ffmpeg.Length == 1 &&
            string.Equals(
                ffmpeg[0].Version,
                DecodeValidator.CanonicalPackageVersion,
                StringComparison.Ordinal) &&
            ffmpeg[0].ExecutableDigest == CanonicalFfmpegDigest &&
            ffmpeg[0].ContainerImageDigest is null;
    }

    private static bool HasSameArtifact(StoredArtifact left, StoredArtifact right) =>
        left.Id == right.Id &&
        left.ProjectId == right.ProjectId &&
        left.Kind == right.Kind &&
        string.Equals(
            left.StorageReference,
            right.StorageReference,
            StringComparison.Ordinal) &&
        left.ContentDigest == right.ContentDigest &&
        left.ByteSize == right.ByteSize &&
        left.CreatedAt == right.CreatedAt;

    private static (
        string Tool,
        string Artifact,
        string WorkingDirectory) ValidateProcessPaths(
        string toolPath,
        string artifactPath)
    {
        string tool = SqliteStoreSupport.ValidateLocalAbsolutePath(
            toolPath,
            nameof(toolPath));
        string artifact = SqliteStoreSupport.ValidateLocalAbsolutePath(
            artifactPath,
            nameof(artifactPath));
        string? workingDirectory = Path.GetDirectoryName(artifact);
        if (string.IsNullOrEmpty(workingDirectory))
        {
            throw new ArgumentException(
                "The output artifact path must have a parent directory.",
                nameof(artifactPath));
        }

        return (tool, artifact, workingDirectory);
    }

    private static ValidateArtifactDelegate GetValidateArtifactDelegate(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.ValidateRegisteredAsync;
    }

    private static GetArtifactPathDelegate GetArtifactPathDelegateFor(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.GetValidatedRegisteredArtifactPath;
    }

    private static VerifyBinaryDelegate GetVerifyBinaryDelegate(
        BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return binaryVerifier.VerifyAsync;
    }

    private static RunProcessDelegate GetRunProcessDelegate(
        ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static OutputQcException Failure(
        OutputQcFailureReason reason,
        OutputQcStage stage) =>
        new(reason, stage);

    internal delegate Task<StoredArtifact> ValidateArtifactDelegate(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken);

    internal delegate string GetArtifactPathDelegate(
        EntityId<ArtifactIdTag> artifactId);

    internal delegate Task<BinaryVerificationResult> VerifyBinaryDelegate(
        BinaryVerificationRequest request,
        CancellationToken cancellationToken);

    internal delegate Task<ProcessRunResult> RunProcessDelegate(
        ProcessRunRequest request,
        CancellationToken cancellationToken);
}
