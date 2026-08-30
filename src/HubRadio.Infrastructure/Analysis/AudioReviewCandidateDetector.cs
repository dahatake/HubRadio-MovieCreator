using System.Buffers.Binary;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Infrastructure.Analysis;

public enum AudioReviewEventKind
{
    Cough,
    Noise,
    Wait,
    RecordingQuality,
}

public enum AudioReviewEvidenceMethod
{
    ManualListening,
    FfmpegSilence,
    WaveformLowEnergy,
    WaveformClipping,
    YamnetHeld,
    SileroVadBenchmarkOnly,
}

public sealed class AudioReviewObservation
{
    public AudioReviewObservation(
        AudioReviewEventKind kind,
        AudioReviewEvidenceMethod method,
        long analysisStartSample,
        long analysisEndSample,
        double? observedMetric = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        if (analysisStartSample < 0 || analysisEndSample <= analysisStartSample)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analysisEndSample),
                "An audio review observation requires a positive sample range.");
        }

        if (observedMetric is double metric && !double.IsFinite(metric))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedMetric),
                "An observed audio metric must be finite.");
        }

        bool methodMatchesKind = method switch
        {
            AudioReviewEvidenceMethod.ManualListening =>
                observedMetric is null,
            AudioReviewEvidenceMethod.FfmpegSilence or
            AudioReviewEvidenceMethod.WaveformLowEnergy =>
                kind == AudioReviewEventKind.Wait,
            AudioReviewEvidenceMethod.WaveformClipping =>
                kind == AudioReviewEventKind.RecordingQuality,
            AudioReviewEvidenceMethod.YamnetHeld =>
                kind is AudioReviewEventKind.Cough
                    or AudioReviewEventKind.Noise
                    or AudioReviewEventKind.Wait,
            AudioReviewEvidenceMethod.SileroVadBenchmarkOnly =>
                kind == AudioReviewEventKind.Wait,
            _ => false,
        };
        if (!methodMatchesKind)
        {
            throw new ArgumentException(
                "The audio evidence method does not support the requested event kind.",
                nameof(method));
        }

        Kind = kind;
        Method = method;
        AnalysisStartSample = analysisStartSample;
        AnalysisEndSample = analysisEndSample;
        ObservedMetric = observedMetric;
    }

    public AudioReviewEventKind Kind { get; }

    public AudioReviewEvidenceMethod Method { get; }

    public long AnalysisStartSample { get; }

    public long AnalysisEndSample { get; }

    public double? ObservedMetric { get; }

    public bool ManualListeningConfirmed =>
        Method == AudioReviewEvidenceMethod.ManualListening;
}

public sealed class AudioReviewConfiguration
{
    internal AudioReviewConfiguration()
    {
    }

    public string RuleVersion => AudioReviewCandidateDetector.RuleVersion;

    public int AnalysisSampleRate => AnalysisAudioTimeline.FixedSampleRate;

    public int WindowMilliseconds => 100;

    public int WindowSamples => 1600;

    public int LowEnergyMinimumSamples => 32000;

    public string LowEnergyPowerRule =>
        "mean-square-pcm16-times-100000-less-than-or-equal-full-scale-square";

    public int ClippingMagnitude => 32736;

    public int ClippingMinimumSamplesPerWindow => 3;

    public bool ProductionThresholdsApproved => false;

    public bool AutomaticDeletionAllowed => false;

    public bool YamnetAdopted => false;

    public bool SileroVadAdopted => false;
}

public sealed class AudioReviewCandidate
{
    internal AudioReviewCandidate(
        SourceSpan sourceSpan,
        Detection detection,
        Suggestion suggestion,
        AudioReviewEventKind kind,
        IReadOnlyList<AudioReviewEvidenceMethod> evidenceMethods,
        long analysisStartSample,
        long analysisEndSample,
        bool manualListeningConfirmed,
        bool technicalWarningOnly)
    {
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(evidenceMethods);
        if (!Enum.IsDefined(kind)
            || analysisStartSample < 0
            || analysisEndSample <= analysisStartSample
            || sourceSpan.TranscriptRevisionId is not null
            || sourceSpan.TranscriptTokenIds.Count != 0)
        {
            throw new ArgumentException(
                "An audio review candidate contains invalid range evidence.",
                nameof(analysisEndSample));
        }

        AudioReviewEvidenceMethod[] copiedMethods = evidenceMethods.ToArray();
        if (copiedMethods.Length == 0
            || copiedMethods.Any(static method => !Enum.IsDefined(method))
            || copiedMethods.Distinct().Count() != copiedMethods.Length
            || !copiedMethods.SequenceEqual(copiedMethods.Order()))
        {
            throw new ArgumentException(
                "Audio review evidence methods must be unique and canonical.",
                nameof(evidenceMethods));
        }

        OriginRef expectedOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
        DetectionKind expectedDetectionKind = kind == AudioReviewEventKind.RecordingQuality
            ? DetectionKind.RecordingProblem
            : DetectionKind.AudioEvent;
        if (detection.Kind != expectedDetectionKind
            || suggestion.Kind != SuggestionKind.DeleteRange
            || suggestion.Status != SuggestionStatus.Proposed
            || detection.ModelRunId != suggestion.ModelRunId
            || detection.Origin != expectedOrigin
            || suggestion.Origin != expectedOrigin
            || detection.Confidence is not null
            || suggestion.Confidence is not null
            || manualListeningConfirmed != copiedMethods.Contains(
                AudioReviewEvidenceMethod.ManualListening)
            || technicalWarningOnly !=
                (kind == AudioReviewEventKind.RecordingQuality))
        {
            throw new ArgumentException(
                "Audio review detection and suggestion evidence is inconsistent.",
                nameof(suggestion));
        }

        SourceSpan = sourceSpan;
        Detection = detection;
        Suggestion = suggestion;
        Kind = kind;
        EvidenceMethods = Array.AsReadOnly(copiedMethods);
        AnalysisStartSample = analysisStartSample;
        AnalysisEndSample = analysisEndSample;
        ManualListeningConfirmed = manualListeningConfirmed;
        TechnicalWarningOnly = technicalWarningOnly;
    }

    public SourceSpan SourceSpan { get; }

    public Detection Detection { get; }

    public Suggestion Suggestion { get; }

    public AudioReviewEventKind Kind { get; }

    public IReadOnlyList<AudioReviewEvidenceMethod> EvidenceMethods { get; }

    public long AnalysisStartSample { get; }

    public long AnalysisEndSample { get; }

    public bool ManualListeningConfirmed { get; }

    public bool TechnicalWarningOnly { get; }

    public bool AutomaticDeletionAllowed => false;

    public bool ExplicitHumanDecisionRequired => true;
}

public sealed class AudioReviewDetectionResult
{
    internal AudioReviewDetectionResult(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        AudioReviewConfiguration configuration,
        int observationCount,
        IReadOnlyList<AudioReviewCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(candidates);
        if (observationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observationCount));
        }

        AudioReviewCandidate[] copiedCandidates =
            new AudioReviewCandidate[candidates.Count];
        var sourceSpanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        var detectionIds = new HashSet<EntityId<DetectionIdTag>>();
        var suggestionIds = new HashSet<EntityId<SuggestionIdTag>>();
        (long Start, long End, AudioReviewEventKind Kind)? previous = null;
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            AudioReviewCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Audio review candidates must not contain null elements.",
                    nameof(candidates));
            var key = (
                candidate.AnalysisStartSample,
                candidate.AnalysisEndSample,
                candidate.Kind);
            if (candidate.Detection.ModelRunId != modelRunId
                || candidate.Suggestion.ModelRunId != modelRunId
                || candidate.SourceSpan.AssetId != timeline.SourceAssetId
                || candidate.SourceSpan.SourceDigest != timeline.SourceContentDigest
                || !sourceSpanIds.Add(candidate.SourceSpan.Id)
                || !detectionIds.Add(candidate.Detection.Id)
                || !suggestionIds.Add(candidate.Suggestion.Id)
                || previous is { } prior && Compare(prior, key) >= 0)
            {
                throw new ArgumentException(
                    "An audio review result contains inconsistent candidates.",
                    nameof(candidates));
            }

            previous = key;
            copiedCandidates[index] = candidate;
        }

        Timeline = timeline;
        ModelRunId = modelRunId;
        Configuration = configuration;
        ObservationCount = observationCount;
        Candidates = Array.AsReadOnly(copiedCandidates);
    }

    public AnalysisAudioTimeline Timeline { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public AudioReviewConfiguration Configuration { get; }

    public int ObservationCount { get; }

    public IReadOnlyList<AudioReviewCandidate> Candidates { get; }

    private static int Compare(
        (long Start, long End, AudioReviewEventKind Kind) left,
        (long Start, long End, AudioReviewEventKind Kind) right)
    {
        int start = left.Start.CompareTo(right.Start);
        if (start != 0)
        {
            return start;
        }

        int end = left.End.CompareTo(right.End);
        return end != 0 ? end : left.Kind.CompareTo(right.Kind);
    }
}

public enum AudioReviewFailureReason
{
    AnalysisAudioBindingMismatch,
    WaveformMalformed,
    ObservationOutOfRange,
    DuplicateObservation,
    MethodNotAdopted,
    ArithmeticOverflow,
}

public sealed class AudioReviewException : Exception
{
    internal AudioReviewException(AudioReviewFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public AudioReviewFailureReason Reason { get; }

    private static string GetStableMessage(AudioReviewFailureReason reason) =>
        reason switch
        {
            AudioReviewFailureReason.AnalysisAudioBindingMismatch =>
                "The analysis audio does not match its timeline evidence.",
            AudioReviewFailureReason.WaveformMalformed =>
                "The analysis waveform is malformed.",
            AudioReviewFailureReason.ObservationOutOfRange =>
                "An audio review observation is outside the analysis timeline.",
            AudioReviewFailureReason.DuplicateObservation =>
                "Audio review observations contain a duplicate.",
            AudioReviewFailureReason.MethodNotAdopted =>
                "An audio review method is not adopted for runtime use.",
            AudioReviewFailureReason.ArithmeticOverflow =>
                "Audio review arithmetic exceeds the supported range.",
            _ => "Audio review candidate detection failed.",
        };
}

public static class AudioReviewCandidateDetector
{
    public const string RuleVersion = "stage0-2026-08-27.audio-review.v1";

    private const int WindowSamples = 1600;
    private const long FullScaleSquare = 32768L * 32768L;
    private const long LowEnergyPowerScale = 100000L;
    private const int LowEnergyMinimumSamples = 32000;
    private const int ClippingMagnitude = 32736;
    private const int ClippingMinimumSamplesPerWindow = 3;

    private const string WaitDetectionDescription =
        "Two adopted deterministic audio methods overlap on a possible wait interval.";
    private const string ManualDetectionDescription =
        "A human listening observation identified an audio interval for review.";
    private const string RecordingDetectionDescription =
        "A deterministic waveform rule identified a recording-quality interval.";
    private const string WaitReason =
        "Overlapping deterministic low-level observations indicate a possible wait interval.";
    private const string ManualCoughReason =
        "A listener marked this interval as a possible cough.";
    private const string ManualNoiseReason =
        "A listener marked this interval as possible noise.";
    private const string ManualWaitReason =
        "A listener marked this interval as a possible operation wait.";
    private const string RecordingReason =
        "A deterministic waveform observation requires recording-quality review.";
    private const string UnconfirmedDeletionContext =
        "The evidence does not establish that the interval is editorially removable.";
    private const string WaitWarning =
        "Stage 0 thresholds are not production approved; listen before deciding.";
    private const string ManualWarning =
        "A listening observation still requires an explicit deletion decision.";
    private const string RecordingWarning =
        "This is a technical warning, not proof that the interval should be deleted.";

    public static AudioReviewConfiguration Configuration { get; } = new();

    public static IReadOnlyList<AudioReviewObservation> AnalyzeWaveform(
        AnalysisAudioTimeline timeline,
        ReadOnlyMemory<byte> analysisWaveBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] snapshot = analysisWaveBytes.ToArray();
        if (snapshot.LongLength != timeline.AnalysisAudioByteSize
            || ContentDigest.Compute(snapshot) != timeline.AnalysisAudioDigest)
        {
            throw Failure(AudioReviewFailureReason.AnalysisAudioBindingMismatch);
        }

        try
        {
            WaveData data = ParseWave(snapshot, timeline.AnalysisSampleCount);
            var lowEnergyFlags = new bool[data.WindowCount];
            var clippingFlags = new bool[data.WindowCount];
            ReadOnlySpan<byte> samples = snapshot.AsSpan(
                data.DataOffset,
                data.DataByteCount);
            for (int window = 0; window < data.WindowCount; window++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int startSample = window * WindowSamples;
                int sampleCount = Math.Min(
                    WindowSamples,
                    data.SampleCount - startSample);
                long squareSum = 0;
                int clippingCount = 0;
                for (int offset = 0; offset < sampleCount; offset++)
                {
                    int sample = BinaryPrimitives.ReadInt16LittleEndian(
                        samples.Slice((startSample + offset) * sizeof(short), sizeof(short)));
                    long magnitude = Math.Abs((long)sample);
                    squareSum = checked(squareSum + (magnitude * magnitude));
                    if (magnitude >= ClippingMagnitude)
                    {
                        clippingCount++;
                    }
                }

                lowEnergyFlags[window] = checked(squareSum * LowEnergyPowerScale)
                    <= checked((long)sampleCount * FullScaleSquare);
                clippingFlags[window] =
                    clippingCount >= ClippingMinimumSamplesPerWindow;
            }

            var observations = new List<AudioReviewObservation>();
            AppendFlaggedRanges(
                observations,
                lowEnergyFlags,
                data.SampleCount,
                AudioReviewEventKind.Wait,
                AudioReviewEvidenceMethod.WaveformLowEnergy,
                LowEnergyMinimumSamples);
            AppendFlaggedRanges(
                observations,
                clippingFlags,
                data.SampleCount,
                AudioReviewEventKind.RecordingQuality,
                AudioReviewEvidenceMethod.WaveformClipping,
                minimumSamples: 1);
            observations.Sort(CompareObservations);
            return Array.AsReadOnly(observations.ToArray());
        }
        catch (AudioReviewException)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw Failure(AudioReviewFailureReason.ArithmeticOverflow);
        }
        catch (Exception exception)
            when (exception is ArgumentException or IndexOutOfRangeException)
        {
            throw Failure(AudioReviewFailureReason.WaveformMalformed);
        }
    }

    public static AudioReviewDetectionResult CreateCandidates(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        IReadOnlyList<AudioReviewObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        AudioReviewObservation[] validated = ValidateObservations(
            timeline,
            observations);
        var specifications = new List<CandidateSpecification>();
        foreach (AudioReviewObservation observation in validated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.Method is AudioReviewEvidenceMethod.YamnetHeld
                or AudioReviewEvidenceMethod.SileroVadBenchmarkOnly)
            {
                throw Failure(AudioReviewFailureReason.MethodNotAdopted);
            }

            if (observation.ManualListeningConfirmed
                || observation.Kind == AudioReviewEventKind.RecordingQuality)
            {
                specifications.Add(new CandidateSpecification(
                    observation.Kind,
                    observation.AnalysisStartSample,
                    observation.AnalysisEndSample,
                    [observation.Method]));
            }
        }

        AudioReviewObservation[] deterministicWaits = validated
            .Where(static observation =>
                observation.Kind == AudioReviewEventKind.Wait
                && observation.Method is AudioReviewEvidenceMethod.FfmpegSilence
                    or AudioReviewEvidenceMethod.WaveformLowEnergy)
            .ToArray();
        for (int leftIndex = 0; leftIndex < deterministicWaits.Length; leftIndex++)
        {
            for (int rightIndex = leftIndex + 1;
                rightIndex < deterministicWaits.Length;
                rightIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AudioReviewObservation left = deterministicWaits[leftIndex];
                AudioReviewObservation right = deterministicWaits[rightIndex];
                if (left.Method == right.Method)
                {
                    continue;
                }

                long start = Math.Max(
                    left.AnalysisStartSample,
                    right.AnalysisStartSample);
                long end = Math.Min(
                    left.AnalysisEndSample,
                    right.AnalysisEndSample);
                if (end <= start)
                {
                    continue;
                }

                specifications.Add(new CandidateSpecification(
                    AudioReviewEventKind.Wait,
                    start,
                    end,
                    new[] { left.Method, right.Method }.Order().ToArray()));
            }
        }

        CandidateSpecification[] distinctSpecifications = specifications
            .GroupBy(static specification => new
            {
                specification.Kind,
                specification.StartSample,
                specification.EndSample,
            })
            .Select(static group => new CandidateSpecification(
                group.Key.Kind,
                group.Key.StartSample,
                group.Key.EndSample,
                group.SelectMany(static specification => specification.Methods)
                    .Distinct()
                    .Order()
                    .ToArray()))
            .OrderBy(static specification => specification.StartSample)
            .ThenBy(static specification => specification.EndSample)
            .ThenBy(static specification => specification.Kind)
            .ToArray();
        var candidates = new List<AudioReviewCandidate>(
            distinctSpecifications.Length);
        foreach (CandidateSpecification specification in distinctSpecifications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(CreateCandidate(timeline, modelRunId, specification));
        }

        return new AudioReviewDetectionResult(
            timeline,
            modelRunId,
            Configuration,
            validated.Length,
            candidates);
    }

    private static AudioReviewCandidate CreateCandidate(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        CandidateSpecification specification)
    {
        try
        {
            RationalTime sourceStart = timeline.MapAnalysisSampleToSourceTime(
                specification.StartSample);
            RationalTime sourceEnd = timeline.MapAnalysisSampleToSourceTime(
                specification.EndSample);
            var sourceSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                timeline.SourceAssetId,
                string.Concat("audio:", timeline.SourceAudioStreamIndex),
                new TimeRange(sourceStart, sourceEnd - sourceStart),
                timeline.SourceContentDigest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            OriginRef origin = OriginRef.FromSourceSpan(sourceSpan.Id);
            bool manual = specification.Methods.Contains(
                AudioReviewEvidenceMethod.ManualListening);
            bool technical =
                specification.Kind == AudioReviewEventKind.RecordingQuality;
            string description = technical
                ? RecordingDetectionDescription
                : manual
                    ? ManualDetectionDescription
                    : WaitDetectionDescription;
            var detection = new Detection(
                EntityId<DetectionIdTag>.New(),
                modelRunId,
                technical
                    ? DetectionKind.RecordingProblem
                    : DetectionKind.AudioEvent,
                origin,
                description,
                confidence: null);
            var suggestion = new Suggestion(
                EntityId<SuggestionIdTag>.New(),
                modelRunId,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                origin,
                GetReason(specification.Kind, technical, manual),
                confidence: null,
                UnconfirmedDeletionContext,
                technical
                    ? RecordingWarning
                    : manual
                        ? ManualWarning
                        : WaitWarning);
            return new AudioReviewCandidate(
                sourceSpan,
                detection,
                suggestion,
                specification.Kind,
                specification.Methods,
                specification.StartSample,
                specification.EndSample,
                manual,
                technical);
        }
        catch (OverflowException)
        {
            throw Failure(AudioReviewFailureReason.ArithmeticOverflow);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(AudioReviewFailureReason.ObservationOutOfRange);
        }
    }

    private static string GetReason(
        AudioReviewEventKind kind,
        bool technical,
        bool manual) =>
        technical
            ? RecordingReason
            : !manual
                ? WaitReason
                : kind switch
                {
                    AudioReviewEventKind.Cough => ManualCoughReason,
                    AudioReviewEventKind.Noise => ManualNoiseReason,
                    AudioReviewEventKind.Wait => ManualWaitReason,
                    _ => throw new InvalidOperationException(
                        "The manual audio review kind is invalid."),
                };

    private static AudioReviewObservation[] ValidateObservations(
        AnalysisAudioTimeline timeline,
        IReadOnlyList<AudioReviewObservation> observations)
    {
        var copied = new AudioReviewObservation[observations.Count];
        var keys = new HashSet<ObservationKey>();
        for (int index = 0; index < copied.Length; index++)
        {
            AudioReviewObservation observation = observations[index]
                ?? throw new ArgumentException(
                    "Audio review observations must not contain null elements.",
                    nameof(observations));
            if (observation.AnalysisEndSample > timeline.AnalysisSampleCount)
            {
                throw Failure(AudioReviewFailureReason.ObservationOutOfRange);
            }

            var key = new ObservationKey(
                observation.Kind,
                observation.Method,
                observation.AnalysisStartSample,
                observation.AnalysisEndSample,
                observation.ObservedMetric);
            if (!keys.Add(key))
            {
                throw Failure(AudioReviewFailureReason.DuplicateObservation);
            }

            copied[index] = observation;
        }

        Array.Sort(copied, CompareObservations);
        return copied;
    }

    private static WaveData ParseWave(byte[] bytes, long expectedSampleCount)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length < 44
            || !data[..4].SequenceEqual("RIFF"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) !=
                checked((uint)(data.Length - 8))
            || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw Failure(AudioReviewFailureReason.WaveformMalformed);
        }

        bool formatSeen = false;
        bool audioSeen = false;
        int dataOffset = 0;
        int dataByteCount = 0;
        int offset = 12;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8)
            {
                throw Failure(AudioReviewFailureReason.WaveformMalformed);
            }

            ReadOnlySpan<byte> chunkName = data.Slice(offset, 4);
            uint chunkByteCount = BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(offset + 4, 4));
            int payloadOffset = checked(offset + 8);
            int payloadEnd = checked(payloadOffset + (int)chunkByteCount);
            if (payloadEnd > data.Length)
            {
                throw Failure(AudioReviewFailureReason.WaveformMalformed);
            }

            if (chunkName.SequenceEqual("fmt "u8))
            {
                if (formatSeen
                    || audioSeen
                    || chunkByteCount != 16
                    || BinaryPrimitives.ReadUInt16LittleEndian(
                        data.Slice(payloadOffset, 2)) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(
                        data.Slice(payloadOffset + 2, 2)) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(
                        data.Slice(payloadOffset + 4, 4)) !=
                        AnalysisAudioTimeline.FixedSampleRate
                    || BinaryPrimitives.ReadUInt32LittleEndian(
                        data.Slice(payloadOffset + 8, 4)) !=
                        AnalysisAudioTimeline.FixedSampleRate * sizeof(short)
                    || BinaryPrimitives.ReadUInt16LittleEndian(
                        data.Slice(payloadOffset + 12, 2)) != sizeof(short)
                    || BinaryPrimitives.ReadUInt16LittleEndian(
                        data.Slice(payloadOffset + 14, 2)) != sizeof(short) * 8)
                {
                    throw Failure(AudioReviewFailureReason.WaveformMalformed);
                }

                formatSeen = true;
            }
            else if (chunkName.SequenceEqual("data"u8))
            {
                if (!formatSeen
                    || audioSeen
                    || chunkByteCount == 0
                    || chunkByteCount % sizeof(short) != 0)
                {
                    throw Failure(AudioReviewFailureReason.WaveformMalformed);
                }

                audioSeen = true;
                dataOffset = payloadOffset;
                dataByteCount = checked((int)chunkByteCount);
            }
            else
            {
                throw Failure(AudioReviewFailureReason.WaveformMalformed);
            }

            offset = checked(payloadEnd + ((chunkByteCount & 1) == 0 ? 0 : 1));
        }

        int sampleCount = dataByteCount / sizeof(short);
        if (!formatSeen
            || !audioSeen
            || offset != data.Length
            || sampleCount != expectedSampleCount)
        {
            throw Failure(AudioReviewFailureReason.WaveformMalformed);
        }

        return new WaveData(
            dataOffset,
            dataByteCount,
            sampleCount,
            checked((sampleCount + WindowSamples - 1) / WindowSamples));
    }

    private static void AppendFlaggedRanges(
        List<AudioReviewObservation> observations,
        IReadOnlyList<bool> flags,
        int sampleCount,
        AudioReviewEventKind kind,
        AudioReviewEvidenceMethod method,
        int minimumSamples)
    {
        int? startWindow = null;
        for (int index = 0; index <= flags.Count; index++)
        {
            bool flagged = index < flags.Count && flags[index];
            if (flagged && startWindow is null)
            {
                startWindow = index;
            }
            else if (!flagged && startWindow is int firstWindow)
            {
                int start = checked(firstWindow * WindowSamples);
                int end = Math.Min(checked(index * WindowSamples), sampleCount);
                if (end - start >= minimumSamples)
                {
                    observations.Add(new AudioReviewObservation(
                        kind,
                        method,
                        start,
                        end));
                }

                startWindow = null;
            }
        }
    }

    private static int CompareObservations(
        AudioReviewObservation left,
        AudioReviewObservation right)
    {
        int start = left.AnalysisStartSample.CompareTo(right.AnalysisStartSample);
        if (start != 0)
        {
            return start;
        }

        int end = left.AnalysisEndSample.CompareTo(right.AnalysisEndSample);
        if (end != 0)
        {
            return end;
        }

        int kind = left.Kind.CompareTo(right.Kind);
        return kind != 0 ? kind : left.Method.CompareTo(right.Method);
    }

    private static AudioReviewException Failure(
        AudioReviewFailureReason reason) =>
        new(reason);

    private sealed record WaveData(
        int DataOffset,
        int DataByteCount,
        int SampleCount,
        int WindowCount);

    private sealed record CandidateSpecification(
        AudioReviewEventKind Kind,
        long StartSample,
        long EndSample,
        IReadOnlyList<AudioReviewEvidenceMethod> Methods);

    private sealed record ObservationKey(
        AudioReviewEventKind Kind,
        AudioReviewEvidenceMethod Method,
        long StartSample,
        long EndSample,
        double? ObservedMetric);

}