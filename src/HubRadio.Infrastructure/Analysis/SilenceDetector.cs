using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Analysis;

public sealed class SilenceDetectionConfiguration
{
    internal SilenceDetectionConfiguration(
        string thresholdVersion,
        double noiseDb,
        RationalTime minimumDuration,
        string sampleRoundingRule,
        bool productionApproved,
        bool automaticDeletionAllowed)
    {
        ArgumentNullException.ThrowIfNull(thresholdVersion);
        ArgumentNullException.ThrowIfNull(sampleRoundingRule);
        if (string.IsNullOrWhiteSpace(thresholdVersion)
            || !string.Equals(
                thresholdVersion,
                thresholdVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A silence threshold version must be nonblank and trimmed.",
                nameof(thresholdVersion));
        }

        if (!double.IsFinite(noiseDb) || noiseDb >= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(noiseDb),
                noiseDb,
                "A silence noise threshold must be a finite negative decibel value.");
        }

        if (minimumDuration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDuration),
                minimumDuration,
                "A silence minimum duration must be positive.");
        }

        if (string.IsNullOrWhiteSpace(sampleRoundingRule)
            || !string.Equals(
                sampleRoundingRule,
                sampleRoundingRule.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A silence sample-rounding rule must be nonblank and trimmed.",
                nameof(sampleRoundingRule));
        }

        if (automaticDeletionAllowed && !productionApproved)
        {
            throw new ArgumentException(
                "An unapproved silence threshold cannot allow automatic deletion.",
                nameof(automaticDeletionAllowed));
        }

        ThresholdVersion = thresholdVersion;
        NoiseDb = noiseDb;
        MinimumDuration = minimumDuration;
        SampleRoundingRule = sampleRoundingRule;
        ProductionApproved = productionApproved;
        AutomaticDeletionAllowed = automaticDeletionAllowed;
    }

    public string ThresholdVersion { get; }

    public double NoiseDb { get; }

    public RationalTime MinimumDuration { get; }

    public string SampleRoundingRule { get; }

    public bool ProductionApproved { get; }

    public bool AutomaticDeletionAllowed { get; }
}

public sealed class SilenceCandidate
{
    internal SilenceCandidate(
        AnalysisAudioTimeline analysisTimeline,
        SourceSpan sourceSpan,
        Detection detection,
        Suggestion suggestion,
        string thresholdVersion,
        long analysisStartSample,
        long analysisEndSample)
    {
        ArgumentNullException.ThrowIfNull(analysisTimeline);
        ArgumentNullException.ThrowIfNull(sourceSpan);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(thresholdVersion);
        if (string.IsNullOrWhiteSpace(thresholdVersion)
            || !string.Equals(
                thresholdVersion,
                thresholdVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A silence candidate threshold version must be nonblank and trimmed.",
                nameof(thresholdVersion));
        }

        if (analysisStartSample < 0 || analysisEndSample <= analysisStartSample)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analysisEndSample),
                "A silence candidate requires an ordered positive sample range.");
        }

        RationalTime expectedStart;
        RationalTime expectedEnd;
        try
        {
            expectedStart = analysisTimeline.MapAnalysisSampleToSourceTime(
                analysisStartSample);
            expectedEnd = analysisTimeline.MapAnalysisSampleToSourceTime(
                analysisEndSample);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analysisEndSample),
                analysisEndSample,
                exception.Message);
        }

        string expectedStreamId = string.Concat(
            "audio:",
            analysisTimeline.SourceAudioStreamIndex.ToString(
                CultureInfo.InvariantCulture));
        if (analysisTimeline.SourceAssetId != sourceSpan.AssetId
            || analysisTimeline.SourceContentDigest != sourceSpan.SourceDigest
            || !string.Equals(
                expectedStreamId,
                sourceSpan.StreamId,
                StringComparison.Ordinal)
            || sourceSpan.Range.Start != expectedStart
            || sourceSpan.Range.End != expectedEnd)
        {
            throw new ArgumentException(
                "A silence candidate must match its analysis-audio timeline.",
                nameof(sourceSpan));
        }

        if (sourceSpan.TranscriptRevisionId is not null
            || sourceSpan.TranscriptTokenIds.Count != 0)
        {
            throw new ArgumentException(
                "An audio-derived silence span must not invent transcript provenance.",
                nameof(sourceSpan));
        }

        OriginRef expectedOrigin = OriginRef.FromSourceSpan(sourceSpan.Id);
        if (detection.Kind != DetectionKind.Silence
            || suggestion.Kind != SuggestionKind.DeleteRange
            || suggestion.Status != SuggestionStatus.Proposed
            || detection.ModelRunId != suggestion.ModelRunId
            || detection.Origin != expectedOrigin
            || suggestion.Origin != expectedOrigin
            || detection.Confidence is not null
            || suggestion.Confidence is not null)
        {
            throw new ArgumentException(
                "Silence detection and suggestion evidence is inconsistent.",
                nameof(suggestion));
        }

        AnalysisTimeline = analysisTimeline;
        SourceSpan = sourceSpan;
        Detection = detection;
        Suggestion = suggestion;
        ThresholdVersion = thresholdVersion;
        AnalysisStartSample = analysisStartSample;
        AnalysisEndSample = analysisEndSample;
    }

    internal AnalysisAudioTimeline AnalysisTimeline { get; }

    public SourceSpan SourceSpan { get; }

    public Detection Detection { get; }

    public Suggestion Suggestion { get; }

    public string ThresholdVersion { get; }

    public long AnalysisStartSample { get; }

    public long AnalysisEndSample { get; }
}

public sealed class SilenceDetectionResult
{
    internal SilenceDetectionResult(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        SilenceDetectionConfiguration configuration,
        IReadOnlyList<SilenceCandidate> candidates,
        TimeSpan processDuration)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(candidates);
        if (processDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processDuration),
                processDuration,
                "The silence-detection process duration must not be negative.");
        }

        long previousEndSample = 0;
        long minimumSampleCount = checked(
            configuration.MinimumDuration.Value
            * AnalysisAudioTimeline.FixedSampleRate
            / configuration.MinimumDuration.Rate);
        string expectedStreamId = string.Concat(
            "audio:",
            timeline.SourceAudioStreamIndex.ToString(
                CultureInfo.InvariantCulture));
        SilenceCandidate[] copiedCandidates = new SilenceCandidate[candidates.Count];
        var sourceSpanIds = new HashSet<EntityId<SourceSpanIdTag>>();
        var detectionIds = new HashSet<EntityId<DetectionIdTag>>();
        var suggestionIds = new HashSet<EntityId<SuggestionIdTag>>();
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            SilenceCandidate candidate = candidates[index]
                ?? throw new ArgumentException(
                    "Silence candidates must not contain null elements.",
                    nameof(candidates));
            RationalTime expectedStart = timeline.MapAnalysisSampleToSourceTime(
                candidate.AnalysisStartSample);
            RationalTime expectedEnd = timeline.MapAnalysisSampleToSourceTime(
                candidate.AnalysisEndSample);
            TimeRange expectedRange = new(
                expectedStart,
                expectedEnd - expectedStart);
            if (candidate.AnalysisStartSample < previousEndSample
                || candidate.AnalysisEndSample > timeline.AnalysisSampleCount
                || !string.Equals(
                    candidate.AnalysisTimeline.CanonicalJson,
                    timeline.CanonicalJson,
                    StringComparison.Ordinal)
                || candidate.AnalysisEndSample - candidate.AnalysisStartSample
                    < minimumSampleCount
                || candidate.SourceSpan.AssetId != timeline.SourceAssetId
                || candidate.SourceSpan.SourceDigest != timeline.SourceContentDigest
                || !string.Equals(
                    candidate.SourceSpan.StreamId,
                    expectedStreamId,
                    StringComparison.Ordinal)
                || candidate.SourceSpan.Range != expectedRange
                || candidate.Detection.ModelRunId != modelRunId
                || candidate.Suggestion.ModelRunId != modelRunId
                || !string.Equals(
                    candidate.ThresholdVersion,
                    configuration.ThresholdVersion,
                    StringComparison.Ordinal)
                || !sourceSpanIds.Add(candidate.SourceSpan.Id)
                || !detectionIds.Add(candidate.Detection.Id)
                || !suggestionIds.Add(candidate.Suggestion.Id))
            {
                throw new ArgumentException(
                    "A silence-detection result contains inconsistent candidates.",
                    nameof(candidates));
            }

            previousEndSample = candidate.AnalysisEndSample;
            copiedCandidates[index] = candidate;
        }

        Timeline = timeline;
        ModelRunId = modelRunId;
        Configuration = configuration;
        Candidates = Array.AsReadOnly(copiedCandidates);
        ProcessDuration = processDuration;
    }

    public AnalysisAudioTimeline Timeline { get; }

    public EntityId<ModelRunIdTag> ModelRunId { get; }

    public SilenceDetectionConfiguration Configuration { get; }

    public IReadOnlyList<SilenceCandidate> Candidates { get; }

    public TimeSpan ProcessDuration { get; }
}

public enum SilenceDetectorFailureReason
{
    InputBindingMismatch,
    ToolVerificationFailed,
    RegisteredAnalysisInvalid,
    ProcessTimedOut,
    ProcessFailed,
    OutputTruncated,
    OutputMalformed,
    ArithmeticOverflow,
}

public sealed class SilenceDetectorException : Exception
{
    internal SilenceDetectorException(SilenceDetectorFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SilenceDetectorFailureReason Reason { get; }

    private static string GetStableMessage(SilenceDetectorFailureReason reason) =>
        reason switch
        {
            SilenceDetectorFailureReason.InputBindingMismatch =>
                "The silence detector input evidence is inconsistent.",
            SilenceDetectorFailureReason.ToolVerificationFailed =>
                "The silence detector tool could not be verified.",
            SilenceDetectorFailureReason.RegisteredAnalysisInvalid =>
                "The registered analysis audio could not be verified.",
            SilenceDetectorFailureReason.ProcessTimedOut =>
                "The silence detector exceeded its timeout.",
            SilenceDetectorFailureReason.ProcessFailed =>
                "The silence detector process failed.",
            SilenceDetectorFailureReason.OutputTruncated =>
                "The silence detector output exceeded its capture limit.",
            SilenceDetectorFailureReason.OutputMalformed =>
                "The silence detector output was malformed.",
            SilenceDetectorFailureReason.ArithmeticOverflow =>
                "The silence candidate time range cannot be represented exactly.",
            _ => "Silence detection failed.",
        };
}

public sealed partial class SilenceDetector
{
    public const string ThresholdVersion =
        "stage0-2026-08-27.ffmpeg-silencedetect.v1";
    public const double NoiseDb = -50d;
    public const int MinimumDurationSeconds = 2;
    public const string SampleRoundingRule = "nearest-to-even-analysis-sample";

    internal const string FilterExpression = "silencedetect=noise=-50.0dB:d=2.0";

    // FR-057: pauses between utterances. Speech recordings keep a room-noise floor well above
    // -50 dB and its noise peaks often exceed -40 dB, so pauses use a separate, versioned -32 dB peak threshold. Candidates are review-only
    // until the application narrows them to the gaps between transcript utterances.
    public const string PauseThresholdVersion =
        "fr057-2026-10-07.ffmpeg-silencedetect-pause.v1";
    public const double PauseNoiseDb = -32d;
    public static readonly RationalTime PauseMinimumDuration = new(2, 5);

    internal const string PauseFilterExpression = "silencedetect=noise=-32.0dB:d=0.4";

    private const string ToolComponentIdentity =
        "tool.ffmpeg." +
        DecodeValidator.CanonicalPackageId + "." +
        DecodeValidator.CanonicalPackageVersion + ".silence-detection";
    private const string DetectionDescription =
        "FFmpeg silencedetect observed an interval below the Stage 0 candidate threshold.";
    private const string SuggestionReason =
        "The interval matches the versioned Stage 0 silence-candidate rule.";
    private const string MissingContext =
        "The detector does not establish whether the interval is editorially removable.";
    private const string ReviewWarning =
        "The threshold is not production approved; listen before deciding.";

    private static readonly ContentDigest CanonicalFfmpegDigest =
        new(DecodeValidator.CanonicalFfmpegSha256);
    private static readonly SilenceDetectionConfiguration FixedConfiguration =
        new(
            ThresholdVersion,
            NoiseDb,
            new RationalTime(MinimumDurationSeconds, 1),
            SampleRoundingRule,
            productionApproved: false,
            automaticDeletionAllowed: false);
    private static readonly SilenceDetectionConfiguration PauseConfiguration =
        new(
            PauseThresholdVersion,
            PauseNoiseDb,
            PauseMinimumDuration,
            SampleRoundingRule,
            productionApproved: false,
            automaticDeletionAllowed: false);
    private static readonly DetectionMode FixedMode = new(
        ThresholdVersion,
        FilterExpression,
        FixedConfiguration,
        checked((long)AnalysisAudioTimeline.FixedSampleRate * MinimumDurationSeconds));
    private static readonly DetectionMode PauseMode = new(
        PauseThresholdVersion,
        PauseFilterExpression,
        PauseConfiguration,
        AnalysisAudioTimeline.FixedSampleRate * PauseMinimumDuration.Value / PauseMinimumDuration.Rate);

    private readonly Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> _runProcessAsync;
    private readonly Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyBinaryAsync;
    private readonly Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyRegisteredAsync;
    private readonly Func<EntityId<ArtifactIdTag>, string> _getRegisteredPath;
    private readonly string _ffmpegPath;

    public SilenceDetector(
        ArtifactStore artifactStore,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string ffmpegPath)
        : this(
            GetProcessDelegate(processRunner),
            GetBinaryVerificationDelegate(binaryVerifier),
            GetRegisteredVerificationDelegate(artifactStore, binaryVerifier),
            GetRegisteredPathDelegate(artifactStore),
            ffmpegPath)
    {
    }

    internal SilenceDetector(
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync,
        Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>> verifyBinaryAsync,
        Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>> verifyRegisteredAsync,
        Func<EntityId<ArtifactIdTag>, string> getRegisteredPath,
        string ffmpegPath)
    {
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);
        ArgumentNullException.ThrowIfNull(verifyRegisteredAsync);
        ArgumentNullException.ThrowIfNull(getRegisteredPath);
        _runProcessAsync = runProcessAsync;
        _verifyBinaryAsync = verifyBinaryAsync;
        _verifyRegisteredAsync = verifyRegisteredAsync;
        _getRegisteredPath = getRegisteredPath;
        _ffmpegPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
    }

    public SilenceDetectionConfiguration Configuration => FixedConfiguration;

    public static SilenceDetectionConfiguration PauseDetectionConfiguration => PauseConfiguration;

    public Task<SilenceDetectionResult> DetectAsync(
        ExtractedAnalysisAudio analysisAudio,
        EntityId<ModelRunIdTag> modelRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        DetectCoreAsync(analysisAudio, modelRunId, timeout, FixedMode, cancellationToken);

    /// <summary>FR-057: every interval below <see cref="PauseNoiseDb"/> lasting at least <see cref="PauseMinimumDuration"/>.</summary>
    public Task<SilenceDetectionResult> DetectPausesAsync(
        ExtractedAnalysisAudio analysisAudio,
        EntityId<ModelRunIdTag> modelRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        DetectCoreAsync(analysisAudio, modelRunId, timeout, PauseMode, cancellationToken);

    /// <summary>
    /// FR-057: keeps only the given analysis-sample sub-ranges of detected pauses (for example the part of a pause
    /// that lies between two utterances). Each range must lie inside one detected pause and be at least
    /// <see cref="PauseMinimumDuration"/> long; the evidence chain (timeline, model run, version) is unchanged.
    /// </summary>
    public static SilenceDetectionResult NarrowPauses(
        SilenceDetectionResult detected,
        IReadOnlyList<(long StartSample, long EndSample)> ranges)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(ranges);
        if (!string.Equals(detected.Configuration.ThresholdVersion, PauseThresholdVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only pause detections can be narrowed.", nameof(detected));
        }

        string streamId = string.Concat(
            "audio:",
            detected.Timeline.SourceAudioStreamIndex.ToString(CultureInfo.InvariantCulture));
        var candidates = new List<SilenceCandidate>(ranges.Count);
        foreach ((long start, long end) in ranges.OrderBy(static item => item.StartSample))
        {
            if (end - start < PauseMode.MinimumSamples
                || !detected.Candidates.Any(item => item.AnalysisStartSample <= start && end <= item.AnalysisEndSample))
            {
                throw new ArgumentException("A narrowed pause must lie inside one detected pause.", nameof(ranges));
            }

            candidates.Add(CreateCandidate(detected.Timeline, detected.ModelRunId, streamId, PauseThresholdVersion,
                new SampleRange(start, end)));
        }

        return new SilenceDetectionResult(
            detected.Timeline,
            detected.ModelRunId,
            detected.Configuration,
            candidates,
            detected.ProcessDuration);
    }

    private async Task<SilenceDetectionResult> DetectCoreAsync(
        ExtractedAnalysisAudio analysisAudio,
        EntityId<ModelRunIdTag> modelRunId,
        TimeSpan timeout,
        DetectionMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysisAudio);
        ArgumentNullException.ThrowIfNull(modelRunId);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(timeout);
        ValidateInputBinding(analysisAudio);

        StoredArtifact audioArtifact = analysisAudio.AudioArtifact;
        string audioPath;
        try
        {
            audioPath = _getRegisteredPath(audioArtifact.Id);
        }
        catch (Exception)
        {
            throw Failure(SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
        }

        using FileStream heldAudio = OpenHeldAnalysisAudio(
            audioPath,
            audioArtifact.ByteSize);
        await VerifyRegisteredExactAsync(
            audioArtifact,
            cancellationToken).ConfigureAwait(false);
        await VerifyToolAsync(cancellationToken).ConfigureAwait(false);
        ValidateHeldAnalysisAudio(heldAudio, audioArtifact.ByteSize);

        ProcessRunRequest request = CreateProcessRequest(
            _ffmpegPath,
            audioPath,
            timeout,
            mode.Filter);
        ProcessRunResult processResult;
        try
        {
            processResult = await _runProcessAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(SilenceDetectorFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(SilenceDetectorFailureReason.ProcessFailed);
        }

        ValidateHeldAnalysisAudio(heldAudio, audioArtifact.ByteSize);
        await VerifyRegisteredExactAsync(
            audioArtifact,
            cancellationToken).ConfigureAwait(false);
        await VerifyToolAsync(cancellationToken).ConfigureAwait(false);
        ValidateHeldAnalysisAudio(heldAudio, audioArtifact.ByteSize);
        return ParseProcessResult(
            analysisAudio.Timeline,
            modelRunId,
            processResult,
            mode);
    }

    internal static ProcessRunRequest CreateProcessRequest(
        string ffmpegPath,
        string analysisAudioPath,
        TimeSpan timeout) =>
        CreateProcessRequest(ffmpegPath, analysisAudioPath, timeout, FilterExpression);

    internal static ProcessRunRequest CreatePauseProcessRequest(
        string ffmpegPath,
        string analysisAudioPath,
        TimeSpan timeout) =>
        CreateProcessRequest(ffmpegPath, analysisAudioPath, timeout, PauseFilterExpression);

    private static ProcessRunRequest CreateProcessRequest(
        string ffmpegPath,
        string analysisAudioPath,
        TimeSpan timeout,
        string filterExpression)
    {
        string executable = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        string source = SqliteStoreSupport.ValidateLocalAbsolutePath(
            analysisAudioPath,
            nameof(analysisAudioPath));
        ValidateTimeout(timeout);
        string workingDirectory = Path.GetDirectoryName(source)
            ?? throw new ArgumentException(
                "The analysis audio path must have a parent directory.",
                nameof(analysisAudioPath));
        return new ProcessRunRequest(
            executable,
            [
                "-nostdin",
                "-hide_banner",
                "-nostats",
                "-v",
                "info",
                "-i",
                source,
                "-map",
                "0:a:0",
                "-vn",
                "-sn",
                "-dn",
                "-af",
                filterExpression,
                "-f",
                "null",
                "NUL",
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    internal static SilenceDetectionResult ParseProcessResult(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        ProcessRunResult processResult) =>
        ParseProcessResult(timeline, modelRunId, processResult, FixedMode);

    internal static SilenceDetectionResult ParsePauseProcessResult(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        ProcessRunResult processResult) =>
        ParseProcessResult(timeline, modelRunId, processResult, PauseMode);

    private static SilenceDetectionResult ParseProcessResult(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        ProcessRunResult processResult,
        DetectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(modelRunId);
        ArgumentNullException.ThrowIfNull(processResult);
        if (processResult.Truncated)
        {
            throw Failure(SilenceDetectorFailureReason.OutputTruncated);
        }

        if (processResult.ExitCode != 0
            || processResult.Duration < TimeSpan.Zero
            || processResult.StandardOutput is null
            || processResult.StandardError is null
            || processResult.StandardOutput.Length != 0)
        {
            throw Failure(SilenceDetectorFailureReason.ProcessFailed);
        }

        if (processResult.StandardError.Contains('\0', StringComparison.Ordinal)
            || processResult.StandardError.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }

        try
        {
            List<SampleRange> ranges = ParseSampleRanges(
                processResult.StandardError,
                timeline.AnalysisSampleCount,
                mode.MinimumSamples);
            var candidates = new List<SilenceCandidate>(ranges.Count);
            string streamId = string.Concat(
                "audio:",
                timeline.SourceAudioStreamIndex.ToString(
                    CultureInfo.InvariantCulture));
            foreach (SampleRange range in ranges)
            {
                candidates.Add(CreateCandidate(timeline, modelRunId, streamId, mode.Version, range));
            }

            return new SilenceDetectionResult(
                timeline,
                modelRunId,
                mode.Configuration,
                candidates,
                processResult.Duration);
        }
        catch (SilenceDetectorException)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw Failure(SilenceDetectorFailureReason.ArithmeticOverflow);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }
        catch (ArgumentException)
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }
    }

    private static SilenceCandidate CreateCandidate(
        AnalysisAudioTimeline timeline,
        EntityId<ModelRunIdTag> modelRunId,
        string streamId,
        string version,
        SampleRange range)
    {
        RationalTime sourceStart = timeline.MapAnalysisSampleToSourceTime(
            range.StartSample);
        RationalTime sourceEnd = timeline.MapAnalysisSampleToSourceTime(
            range.EndSample);
        var sourceSpan = new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            timeline.SourceAssetId,
            streamId,
            new TimeRange(sourceStart, sourceEnd - sourceStart),
            timeline.SourceContentDigest,
            transcriptRevisionId: null,
            transcriptTokenIds: []);
        OriginRef origin = OriginRef.FromSourceSpan(sourceSpan.Id);
        var detection = new Detection(
            EntityId<DetectionIdTag>.New(),
            modelRunId,
            DetectionKind.Silence,
            origin,
            DetectionDescription,
            confidence: null);
        var suggestion = new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            modelRunId,
            SuggestionKind.DeleteRange,
            SuggestionStatus.Proposed,
            origin,
            SuggestionReason,
            confidence: null,
            MissingContext,
            ReviewWarning);
        return new SilenceCandidate(
            timeline,
            sourceSpan,
            detection,
            suggestion,
            version,
            range.StartSample,
            range.EndSample);
    }

    private static List<SampleRange> ParseSampleRanges(
        string standardError,
        long analysisSampleCount,
        long minimumSamples)
    {
        var ranges = new List<SampleRange>();
        long? pendingStart = null;
        long previousEnd = 0;
        foreach (string rawLine in standardError.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            Match startMatch = SilenceStartPattern().Match(line);
            if (startMatch.Success)
            {
                if (pendingStart is not null)
                {
                    throw Failure(SilenceDetectorFailureReason.OutputMalformed);
                }

                pendingStart = ParseSampleBoundary(
                    startMatch.Groups["value"].Value,
                    analysisSampleCount);
                continue;
            }

            Match endMatch = SilenceEndPattern().Match(line);
            if (endMatch.Success)
            {
                long end = ParseSampleBoundary(
                    endMatch.Groups["end"].Value,
                    analysisSampleCount);
                long duration = ParsePositiveSampleDuration(
                    endMatch.Groups["duration"].Value);
                long start = pendingStart ?? checked(end - duration);
                ValidateRange(start, end, duration, previousEnd, analysisSampleCount, minimumSamples);
                ranges.Add(new SampleRange(start, end));
                previousEnd = end;
                pendingStart = null;
                continue;
            }

            if (line.Contains("silence_start:", StringComparison.Ordinal)
                || line.Contains("silence_end:", StringComparison.Ordinal)
                || line.Contains("silence_duration:", StringComparison.Ordinal))
            {
                throw Failure(SilenceDetectorFailureReason.OutputMalformed);
            }
        }

        if (pendingStart is long trailingStart)
        {
            long duration = checked(analysisSampleCount - trailingStart);
            ValidateRange(
                trailingStart,
                analysisSampleCount,
                duration,
                previousEnd,
                analysisSampleCount,
                minimumSamples);
            ranges.Add(new SampleRange(trailingStart, analysisSampleCount));
        }

        return ranges;
    }

    private static void ValidateRange(
        long start,
        long end,
        long reportedDuration,
        long previousEnd,
        long analysisSampleCount,
        long minimumSamples)
    {
        long actualDuration = checked(end - start);
        if (start < previousEnd
            || start < 0
            || end > analysisSampleCount
            || actualDuration <= 0
            || actualDuration != reportedDuration
            || actualDuration < minimumSamples)
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }
    }

    private static long ParseSampleBoundary(
        string value,
        long analysisSampleCount)
    {
        long sample = ParseSampleValue(value);
        if (sample < 0 || sample > analysisSampleCount)
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }

        return sample;
    }

    private static long ParsePositiveSampleDuration(string value)
    {
        long sample = ParseSampleValue(value);
        if (sample <= 0)
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }

        return sample;
    }

    private static long ParseSampleValue(string value)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign
                    | NumberStyles.AllowDecimalPoint
                    | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out decimal seconds))
        {
            throw Failure(SilenceDetectorFailureReason.OutputMalformed);
        }

        decimal scaled = checked(
            seconds * AnalysisAudioTimeline.FixedSampleRate);
        decimal rounded = decimal.Round(
            scaled,
            decimals: 0,
            MidpointRounding.ToEven);
        if (rounded < long.MinValue || rounded > long.MaxValue)
        {
            throw new OverflowException();
        }

        return decimal.ToInt64(rounded);
    }

    private async Task VerifyToolAsync(CancellationToken cancellationToken)
    {
        BinaryVerificationRequest request = new(
            ToolComponentIdentity,
            _ffmpegPath,
            CanonicalFfmpegDigest,
            DecodeValidator.CanonicalFfmpegByteSize);
        BinaryVerificationResult result;
        try
        {
            result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SilenceDetectorFailureReason.ToolVerificationFailed);
        }

        if (!IsExactVerification(
                result,
                request.ExpectedDigest,
                request.ExpectedByteSize))
        {
            throw Failure(SilenceDetectorFailureReason.ToolVerificationFailed);
        }
    }

    private async Task VerifyRegisteredExactAsync(
        StoredArtifact audioArtifact,
        CancellationToken cancellationToken)
    {
        BinaryVerificationResult result;
        try
        {
            result = await _verifyRegisteredAsync(
                audioArtifact.Id,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
        }

        if (!IsExactVerification(
                result,
                audioArtifact.ContentDigest,
                audioArtifact.ByteSize))
        {
            throw Failure(SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
        }
    }

    private static bool IsExactVerification(
        BinaryVerificationResult result,
        ContentDigest expectedDigest,
        long expectedByteSize) =>
        result.IsVerified
        && result.ExpectedDigest == expectedDigest
        && result.ActualDigest == expectedDigest
        && result.ExpectedByteSize == expectedByteSize
        && result.ActualByteSize == expectedByteSize;

    private static void ValidateInputBinding(ExtractedAnalysisAudio analysisAudio)
    {
        AnalysisAudioTimeline timeline = analysisAudio.Timeline;
        StoredArtifact audioArtifact = analysisAudio.AudioArtifact;
        if (audioArtifact.Kind != ArtifactKind.Analysis
            || audioArtifact.Id != timeline.AnalysisAudioArtifactId
            || audioArtifact.ProjectId != timeline.ProjectId
            || audioArtifact.ContentDigest != timeline.AnalysisAudioDigest
            || audioArtifact.ByteSize != timeline.AnalysisAudioByteSize
            || timeline.AnalysisSampleRate != AnalysisAudioTimeline.FixedSampleRate
            || timeline.AnalysisSampleCount <= 0)
        {
            throw Failure(SilenceDetectorFailureReason.InputBindingMismatch);
        }
    }

    private static FileStream OpenHeldAnalysisAudio(string path, long expectedByteSize)
    {
        try
        {
            var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.SequentialScan,
                });
            try
            {
                ValidateHeldAnalysisAudio(stream, expectedByteSize);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (SilenceDetectorException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
        }
    }

    private static void ValidateHeldAnalysisAudio(
        FileStream stream,
        long expectedByteSize)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes & (
                    FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0
                || !stream.CanRead
                || !stream.CanSeek
                || stream.Length != expectedByteSize)
            {
                throw Failure(
                    SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
            }
        }
        catch (SilenceDetectorException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The silence detector timeout must be positive and supported.");
        }
    }

    private static Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> GetProcessDelegate(ProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        return processRunner.RunAsync;
    }

    private static Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> GetBinaryVerificationDelegate(
        BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return binaryVerifier.VerifyAsync;
    }

    private static Func<
        EntityId<ArtifactIdTag>,
        CancellationToken,
        Task<BinaryVerificationResult>> GetRegisteredVerificationDelegate(
        ArtifactStore artifactStore,
        BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        return (artifactId, cancellationToken) =>
            artifactStore.VerifyRegisteredAsync(
                artifactId,
                binaryVerifier,
                cancellationToken);
    }

    private static Func<EntityId<ArtifactIdTag>, string> GetRegisteredPathDelegate(
        ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        return artifactStore.GetValidatedRegisteredArtifactPath;
    }

    private static SilenceDetectorException Failure(
        SilenceDetectorFailureReason reason) =>
        new(reason);

    [GeneratedRegex(
        @"^(?:\[[^\]\r\n]+\]\s+)?silence_start:\s*(?<value>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SilenceStartPattern();

    [GeneratedRegex(
        @"^(?:\[[^\]\r\n]+\]\s+)?silence_end:\s*(?<end>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*\|\s*silence_duration:\s*(?<duration>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SilenceEndPattern();

    private readonly record struct SampleRange(long StartSample, long EndSample);

    private sealed record DetectionMode(
        string Version,
        string Filter,
        SilenceDetectionConfiguration Configuration,
        long MinimumSamples);
}