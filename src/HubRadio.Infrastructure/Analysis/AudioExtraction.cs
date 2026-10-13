using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Analysis;

public sealed class AnalysisAudioTimeline
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const int FixedSampleRate = 16000;
    public const string MappingRule =
        "first-decoded-audio-frame-plus-analysis-sample-offset";

    internal AnalysisAudioTimeline(
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> analysisJobId,
        EntityId<AssetIdTag> sourceAssetId,
        ContentDigest sourceContentDigest,
        long sourceByteSize,
        EntityId<ArtifactIdTag> sourceOriginalArtifactId,
        ContentDigest sourceVideoPtsIndexDigest,
        int sourceVideoStreamIndex,
        int sourceAudioStreamIndex,
        RationalTime sourceAudioTimeBase,
        RationalTime? probeReportedAudioStart,
        long firstDecodedAudioFrameTimestamp,
        int firstDecodedAudioFrameSampleCount,
        EntityId<ArtifactIdTag> analysisAudioArtifactId,
        ContentDigest analysisAudioDigest,
        long analysisAudioByteSize,
        long analysisSampleCount,
        EntityId<ArtifactIdTag> timelineArtifactId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(analysisJobId);
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(sourceContentDigest);
        ArgumentNullException.ThrowIfNull(sourceOriginalArtifactId);
        ArgumentNullException.ThrowIfNull(sourceVideoPtsIndexDigest);
        ArgumentNullException.ThrowIfNull(analysisAudioArtifactId);
        ArgumentNullException.ThrowIfNull(analysisAudioDigest);
        ArgumentNullException.ThrowIfNull(timelineArtifactId);
        if (sourceByteSize <= 0 || analysisAudioByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceByteSize),
                "Audio timeline byte sizes must be positive.");
        }

        if (sourceVideoStreamIndex < 0 || sourceAudioStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceAudioStreamIndex),
                "Audio timeline stream indices must not be negative.");
        }

        if (sourceAudioTimeBase <= default(RationalTime) ||
            firstDecodedAudioFrameSampleCount <= 0 ||
            analysisSampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analysisSampleCount),
                "Audio timeline rates and sample counts must be positive.");
        }

        RationalTime decodedStart = ScaleTicks(
            firstDecodedAudioFrameTimestamp,
            sourceAudioTimeBase);
        RationalTime analysisDuration = new(
            analysisSampleCount,
            FixedSampleRate);
        _ = decodedStart + analysisDuration;

        ProjectId = projectId;
        AnalysisJobId = analysisJobId;
        SourceAssetId = sourceAssetId;
        SourceContentDigest = sourceContentDigest;
        SourceByteSize = sourceByteSize;
        SourceOriginalArtifactId = sourceOriginalArtifactId;
        SourceVideoPtsIndexDigest = sourceVideoPtsIndexDigest;
        SourceVideoStreamIndex = sourceVideoStreamIndex;
        SourceAudioStreamIndex = sourceAudioStreamIndex;
        SourceAudioTimeBase = sourceAudioTimeBase;
        ProbeReportedAudioStart = probeReportedAudioStart;
        FirstDecodedAudioFrameTimestamp = firstDecodedAudioFrameTimestamp;
        FirstDecodedAudioFramePresentationTime = decodedStart;
        FirstDecodedAudioFrameSampleCount = firstDecodedAudioFrameSampleCount;
        AnalysisAudioArtifactId = analysisAudioArtifactId;
        AnalysisAudioDigest = analysisAudioDigest;
        AnalysisAudioByteSize = analysisAudioByteSize;
        AnalysisSampleCount = analysisSampleCount;
        AnalysisDuration = analysisDuration;
        TimelineArtifactId = timelineArtifactId;
        CanonicalJson = BuildCanonicalJson();
    }

    public string SchemaVersion => CurrentSchemaVersion;

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<JobIdTag> AnalysisJobId { get; }

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public ContentDigest SourceContentDigest { get; }

    public long SourceByteSize { get; }

    public EntityId<ArtifactIdTag> SourceOriginalArtifactId { get; }

    public ContentDigest SourceVideoPtsIndexDigest { get; }

    public int SourceVideoStreamIndex { get; }

    public int SourceAudioStreamIndex { get; }

    public RationalTime SourceAudioTimeBase { get; }

    public RationalTime? ProbeReportedAudioStart { get; }

    public long FirstDecodedAudioFrameTimestamp { get; }

    public RationalTime FirstDecodedAudioFramePresentationTime { get; }

    public int FirstDecodedAudioFrameSampleCount { get; }

    public EntityId<ArtifactIdTag> AnalysisAudioArtifactId { get; }

    public ContentDigest AnalysisAudioDigest { get; }

    public long AnalysisAudioByteSize { get; }

    public int AnalysisSampleRate => FixedSampleRate;

    public long AnalysisSampleCount { get; }

    public RationalTime AnalysisDuration { get; }

    public EntityId<ArtifactIdTag> TimelineArtifactId { get; }

    public bool ProbeReportedPacketStartApplied => false;

    public string CanonicalJson { get; }

    public RationalTime MapAnalysisSampleToSourceTime(long sampleIndex)
    {
        if (sampleIndex < 0 || sampleIndex > AnalysisSampleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleIndex),
                "An analysis sample index must be within the extracted audio range.");
        }

        return FirstDecodedAudioFramePresentationTime +
            new RationalTime(sampleIndex, FixedSampleRate);
    }

    public RationalTime MapAsrSecondsToSourceTime(
        double seconds,
        RationalTimeRoundingMode roundingMode)
    {
        if (!double.IsFinite(seconds) || seconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                "ASR seconds must be finite and not negative.");
        }

        if (!Enum.IsDefined(roundingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundingMode),
                roundingMode,
                "The ASR sample rounding mode must be defined.");
        }

        double scaled = seconds * FixedSampleRate;
        if (!double.IsFinite(scaled) || scaled > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                "ASR seconds exceed the supported sample range.");
        }

        double rounded = roundingMode switch
        {
            RationalTimeRoundingMode.TowardZero or
            RationalTimeRoundingMode.Floor => Math.Floor(scaled),
            RationalTimeRoundingMode.Ceiling => Math.Ceiling(scaled),
            RationalTimeRoundingMode.NearestAwayFromZero => Math.Round(
                scaled,
                MidpointRounding.AwayFromZero),
            RationalTimeRoundingMode.NearestToEven => Math.Round(
                scaled,
                MidpointRounding.ToEven),
            _ => throw new ArgumentOutOfRangeException(nameof(roundingMode)),
        };
        return MapAnalysisSampleToSourceTime(checked((long)rounded));
    }

    public long MapSourceTimeToAnalysisSample(
        RationalTime sourceTime,
        RationalTimeRoundingMode roundingMode)
    {
        if (!Enum.IsDefined(roundingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundingMode),
                roundingMode,
                "The source sample rounding mode must be defined.");
        }

        RationalTime relative = sourceTime - FirstDecodedAudioFramePresentationTime;
        if (relative < default(RationalTime) || relative > AnalysisDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceTime),
                "The source time is outside the extracted audio range.");
        }

        return ScaleToLong(relative, FixedSampleRate, roundingMode);
    }

    internal static AnalysisAudioTimeline ParseCanonical(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        try
        {
            if (!string.Equals(
                    HubRadio.Core.Serialization.CanonicalJson.Canonicalize(canonicalJson),
                    canonicalJson,
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The analysis-audio timeline must use canonical JSON.");
            }

            using JsonDocument document = JsonDocument.Parse(canonicalJson);
            JsonElement root = document.RootElement;
            var restored = new AnalysisAudioTimeline(
                ReadId<ProjectIdTag>(root, "projectId"),
                ReadId<JobIdTag>(root, "analysisJobId"),
                ReadId<AssetIdTag>(root, "sourceAssetId"),
                ReadDigest(root, "sourceContentDigest"),
                ReadInt64(root, "sourceByteSize"),
                ReadId<ArtifactIdTag>(root, "sourceOriginalArtifactId"),
                ReadDigest(root, "sourceVideoPtsIndexDigest"),
                ReadInt32(root, "sourceVideoStreamIndex"),
                ReadInt32(root, "sourceAudioStreamIndex"),
                ReadRationalTime(root, "sourceAudioTimeBase"),
                ReadNullableRationalTime(root, "probeReportedAudioStart"),
                ReadInt64(root, "firstDecodedAudioFrameTimestamp"),
                ReadInt32(root, "firstDecodedAudioFrameSampleCount"),
                ReadId<ArtifactIdTag>(root, "analysisAudioArtifactId"),
                ReadDigest(root, "analysisAudioDigest"),
                ReadInt64(root, "analysisAudioByteSize"),
                ReadInt64(root, "analysisSampleCount"),
                ReadId<ArtifactIdTag>(root, "timelineArtifactId"));
            if (!string.Equals(
                    restored.CanonicalJson,
                    canonicalJson,
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The analysis-audio timeline is semantically inconsistent.");
            }

            return restored;
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                or KeyNotFoundException
                or ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            throw new FormatException(
                "The analysis-audio timeline is invalid.",
                exception);
        }
    }

    private static EntityId<TTag> ReadId<TTag>(JsonElement root, string propertyName)
    {
        string text = ReadString(root, propertyName);
        if (!EntityId<TTag>.TryParse(text, out EntityId<TTag>? value)
            || !string.Equals(text, value.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException("The analysis-audio timeline ID is invalid.");
        }

        return value;
    }

    private static ContentDigest ReadDigest(JsonElement root, string propertyName)
    {
        string text = ReadString(root, propertyName);
        if (!ContentDigest.TryParse(text, out ContentDigest? value)
            || !string.Equals(text, value.Hex, StringComparison.Ordinal))
        {
            throw new FormatException("The analysis-audio timeline digest is invalid.");
        }

        return value;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new FormatException("The analysis-audio timeline string is invalid.");
        }

        return text;
    }

    private static int ReadInt32(JsonElement root, string propertyName)
    {
        long value = ReadInt64(root, propertyName);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new FormatException("The analysis-audio timeline integer is invalid.");
        }

        return (int)value;
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out decimal number)
            || number != decimal.Truncate(number)
            || number is < long.MinValue or > long.MaxValue)
        {
            throw new FormatException("The analysis-audio timeline integer is invalid.");
        }

        return decimal.ToInt64(number);
    }

    private static RationalTime ReadRationalTime(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        long numerator = ReadInt64(value, "value");
        long rate = ReadInt64(value, "rate");
        var result = new RationalTime(numerator, rate);
        if (result.Value != numerator || result.Rate != rate)
        {
            throw new FormatException(
                "The analysis-audio timeline rational time is not reduced.");
        }

        return result;
    }

    private static RationalTime? ReadNullableRationalTime(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : ReadRationalTime(root, propertyName);
    }

    private string BuildCanonicalJson()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("projectId", ProjectId.ToString());
            writer.WriteString("analysisJobId", AnalysisJobId.ToString());
            writer.WriteString("sourceAssetId", SourceAssetId.ToString());
            writer.WriteString("sourceContentDigest", SourceContentDigest.Hex);
            writer.WriteNumber("sourceByteSize", SourceByteSize);
            writer.WriteString(
                "sourceOriginalArtifactId",
                SourceOriginalArtifactId.ToString());
            writer.WriteString(
                "sourceVideoPtsIndexDigest",
                SourceVideoPtsIndexDigest.Hex);
            writer.WriteNumber("sourceVideoStreamIndex", SourceVideoStreamIndex);
            writer.WriteNumber("sourceAudioStreamIndex", SourceAudioStreamIndex);
            writer.WritePropertyName("sourceAudioTimeBase");
            WriteRationalTime(writer, SourceAudioTimeBase);
            writer.WritePropertyName("probeReportedAudioStart");
            if (ProbeReportedAudioStart is RationalTime reportedStart)
            {
                WriteRationalTime(writer, reportedStart);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteNumber(
                "firstDecodedAudioFrameTimestamp",
                FirstDecodedAudioFrameTimestamp);
            writer.WritePropertyName("firstDecodedAudioFramePresentationTime");
            WriteRationalTime(writer, FirstDecodedAudioFramePresentationTime);
            writer.WriteNumber(
                "firstDecodedAudioFrameSampleCount",
                FirstDecodedAudioFrameSampleCount);
            writer.WriteString(
                "analysisAudioArtifactId",
                AnalysisAudioArtifactId.ToString());
            writer.WriteString("analysisAudioDigest", AnalysisAudioDigest.Hex);
            writer.WriteNumber("analysisAudioByteSize", AnalysisAudioByteSize);
            writer.WriteNumber("analysisSampleRate", AnalysisSampleRate);
            writer.WriteNumber("analysisSampleCount", AnalysisSampleCount);
            writer.WritePropertyName("analysisDuration");
            WriteRationalTime(writer, AnalysisDuration);
            writer.WriteString("timelineArtifactId", TimelineArtifactId.ToString());
            writer.WriteString("mappingRule", MappingRule);
            writer.WriteBoolean(
                "probeReportedPacketStartApplied",
                ProbeReportedPacketStartApplied);
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] canonical = HubRadio.Core.Serialization.CanonicalJson
            .CanonicalizeToUtf8(buffer.WrittenSpan);
        return StrictUtf8.GetString(canonical);
    }

    private static RationalTime ScaleTicks(
        long ticks,
        RationalTime timeBase) =>
        CreateRational(
            (BigInteger)ticks * timeBase.Value,
            timeBase.Rate);

    private static long ScaleToLong(
        RationalTime value,
        long rate,
        RationalTimeRoundingMode mode)
    {
        BigInteger numerator = (BigInteger)value.Value * rate;
        BigInteger quotient = BigInteger.DivRem(
            numerator,
            value.Rate,
            out BigInteger remainder);
        BigInteger rounded = mode switch
        {
            RationalTimeRoundingMode.TowardZero or
            RationalTimeRoundingMode.Floor => quotient,
            RationalTimeRoundingMode.Ceiling => remainder.IsZero
                ? quotient
                : quotient + BigInteger.One,
            RationalTimeRoundingMode.NearestAwayFromZero =>
                RoundNearest(quotient, remainder, value.Rate, tiesToEven: false),
            RationalTimeRoundingMode.NearestToEven =>
                RoundNearest(quotient, remainder, value.Rate, tiesToEven: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (rounded < 0 || rounded > long.MaxValue)
        {
            throw new OverflowException(
                "The analysis sample index cannot be represented.");
        }

        return (long)rounded;
    }

    private static BigInteger RoundNearest(
        BigInteger quotient,
        BigInteger remainder,
        BigInteger denominator,
        bool tiesToEven)
    {
        int comparison = (BigInteger.Abs(remainder) * 2).CompareTo(denominator);
        if (comparison < 0 || comparison == 0 && tiesToEven && quotient.IsEven)
        {
            return quotient;
        }

        return quotient + BigInteger.One;
    }

    private static RationalTime CreateRational(
        BigInteger numerator,
        BigInteger denominator)
    {
        BigInteger divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator < long.MinValue ||
            numerator > long.MaxValue ||
            denominator <= 0 ||
            denominator > long.MaxValue)
        {
            throw new OverflowException(
                "The audio timeline value cannot be represented exactly.");
        }

        return new RationalTime((long)numerator, (long)denominator);
    }

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        RationalTime value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteNumber("rate", value.Rate);
        writer.WriteEndObject();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
}

public sealed class ExtractedAnalysisAudio
{
    internal ExtractedAnalysisAudio(
        StoredArtifact audioArtifact,
        StoredArtifact timelineArtifact,
        AnalysisAudioTimeline timeline,
        TimeSpan extractionElapsed)
    {
        ArgumentNullException.ThrowIfNull(audioArtifact);
        ArgumentNullException.ThrowIfNull(timelineArtifact);
        ArgumentNullException.ThrowIfNull(timeline);
        byte[] timelineBytes = StrictUtf8.GetBytes(timeline.CanonicalJson);
        if (audioArtifact.Kind != ArtifactKind.Analysis ||
            timelineArtifact.Kind != ArtifactKind.Analysis ||
            audioArtifact.ProjectId != timeline.ProjectId ||
            timelineArtifact.ProjectId != timeline.ProjectId ||
            audioArtifact.Id != timeline.AnalysisAudioArtifactId ||
            audioArtifact.ContentDigest != timeline.AnalysisAudioDigest ||
            audioArtifact.ByteSize != timeline.AnalysisAudioByteSize ||
            timelineArtifact.Id != timeline.TimelineArtifactId ||
            timelineArtifact.ContentDigest != ContentDigest.Compute(timelineBytes) ||
            timelineArtifact.ByteSize != timelineBytes.LongLength ||
            extractionElapsed < TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Extracted analysis audio evidence is inconsistent.",
                nameof(audioArtifact));
        }

        AudioArtifact = audioArtifact;
        TimelineArtifact = timelineArtifact;
        Timeline = timeline;
        ExtractionElapsed = extractionElapsed;
    }

    public StoredArtifact AudioArtifact { get; }

    public StoredArtifact TimelineArtifact { get; }

    public AnalysisAudioTimeline Timeline { get; }

    public TimeSpan ExtractionElapsed { get; }

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
}

public enum AudioExtractionFailureReason
{
    InputBindingMismatch,
    NoAudioStream,
    RegisteredOriginalInvalid,
    ToolVerificationFailed,
    SourceProbeFailed,
    TimelineProbeFailed,
    ProcessTimedOut,
    StagingConflict,
    StagingUnavailable,
    ExtractionFailed,
    OutputInvalid,
    PromotionFailed,
    RegisteredAnalysisInvalid,
    CleanupFailed,
    ArithmeticOverflow,
}

public sealed class AudioExtractionException : Exception
{
    internal AudioExtractionException(
        AudioExtractionFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public AudioExtractionFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(AudioExtractionFailureReason reason) =>
        reason switch
        {
            AudioExtractionFailureReason.InputBindingMismatch =>
                "The audio extraction inputs do not identify the same original media.",
            AudioExtractionFailureReason.NoAudioStream =>
                "The original media does not contain an audio stream.",
            AudioExtractionFailureReason.RegisteredOriginalInvalid =>
                "The registered original could not be verified for audio extraction.",
            AudioExtractionFailureReason.ToolVerificationFailed =>
                "An audio extraction tool could not be verified.",
            AudioExtractionFailureReason.SourceProbeFailed =>
                "The registered original could not be probed for audio extraction.",
            AudioExtractionFailureReason.TimelineProbeFailed =>
                "The decoded audio timeline could not be established.",
            AudioExtractionFailureReason.ProcessTimedOut =>
                "The audio extraction process exceeded its timeout.",
            AudioExtractionFailureReason.StagingConflict =>
                "The audio extraction staging destination already exists.",
            AudioExtractionFailureReason.StagingUnavailable =>
                "The audio extraction staging destination is unavailable.",
            AudioExtractionFailureReason.ExtractionFailed =>
                "The audio extraction process failed.",
            AudioExtractionFailureReason.OutputInvalid =>
                "The extracted analysis audio is invalid.",
            AudioExtractionFailureReason.PromotionFailed =>
                "The extracted analysis audio could not be promoted.",
            AudioExtractionFailureReason.RegisteredAnalysisInvalid =>
                "The registered analysis audio could not be verified.",
            AudioExtractionFailureReason.CleanupFailed =>
                "Audio extraction staging cleanup requires reconciliation.",
            AudioExtractionFailureReason.ArithmeticOverflow =>
                "The audio timeline cannot be represented exactly.",
            _ => "Audio extraction failed.",
        };
}

public sealed class AudioExtraction
{
    internal const string DecodedFrameShowEntries =
        "frame=stream_index,best_effort_timestamp,nb_samples";
    internal const string CompactOutputFormat =
        "compact=p=0:nk=0:escape=none:s=|";
    internal const string DecodedFrameReadInterval = "%+#16";

    private const int FileBufferSize = 64 * 1024;
    private const string FfmpegComponentIdentity =
        "tool.ffmpeg." +
        DecodeValidator.CanonicalPackageId + "." +
        DecodeValidator.CanonicalPackageVersion + ".analysis-audio";
    private const string FfprobeComponentIdentity =
        "tool.ffprobe." +
        FfprobeClient.CanonicalPackageId + "." +
        FfprobeClient.CanonicalPackageVersion + ".analysis-audio";

    private static readonly ContentDigest CanonicalFfmpegDigest =
        new(DecodeValidator.CanonicalFfmpegSha256);
    private static readonly ContentDigest CanonicalFfprobeDigest =
        new(FfprobeClient.CanonicalFfprobeSha256);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ArtifactStore _artifactStore;
    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
        _runProcessAsync;
    private readonly Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>>
        _verifyBinaryAsync;
    private readonly Func<StagedMediaImport, TimeSpan, CancellationToken, Task<MediaProbeResult>>
        _probeAsync;
    private readonly Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>>
        _verifyRegisteredAsync;
    private readonly Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>
        _promoteAsync;
    private readonly string _workspacePath;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly TimeProvider _timeProvider;

    public AudioExtraction(
        ArtifactStore artifactStore,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        FfprobeClient ffprobeClient,
        string workspacePath,
        string ffmpegPath,
        string ffprobePath)
        : this(
            artifactStore,
            GetProcessDelegate(processRunner),
            GetBinaryVerificationDelegate(binaryVerifier),
            GetProbeDelegate(ffprobeClient),
            GetRegisteredVerificationDelegate(artifactStore, binaryVerifier),
            GetPromotionDelegate(artifactStore),
            workspacePath,
            ffmpegPath,
            ffprobePath,
            TimeProvider.System)
    {
    }

    internal AudioExtraction(
        ArtifactStore artifactStore,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync,
        Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>> verifyBinaryAsync,
        Func<StagedMediaImport, TimeSpan, CancellationToken, Task<MediaProbeResult>> probeAsync,
        Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>> verifyRegisteredAsync,
        Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>> promoteAsync,
        string workspacePath,
        string ffmpegPath,
        string ffprobePath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);
        ArgumentNullException.ThrowIfNull(probeAsync);
        ArgumentNullException.ThrowIfNull(verifyRegisteredAsync);
        ArgumentNullException.ThrowIfNull(promoteAsync);
        _artifactStore = artifactStore;
        _runProcessAsync = runProcessAsync;
        _verifyBinaryAsync = verifyBinaryAsync;
        _probeAsync = probeAsync;
        _verifyRegisteredAsync = verifyRegisteredAsync;
        _promoteAsync = promoteAsync;
        _workspacePath = ValidateWorkspacePath(workspacePath);
        _ffmpegPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        _ffprobePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffprobePath,
            nameof(ffprobePath));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExtractedAnalysisAudio> ExtractAsync(
        EntityId<ArtifactIdTag> audioArtifactId,
        EntityId<ArtifactIdTag> timelineArtifactId,
        Asset sourceAsset,
        StoredArtifact sourceOriginal,
        PtsIndex sourceVideoPtsIndex,
        EntityId<JobIdTag> analysisJobId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioArtifactId);
        ArgumentNullException.ThrowIfNull(timelineArtifactId);
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(sourceOriginal);
        ArgumentNullException.ThrowIfNull(sourceVideoPtsIndex);
        ArgumentNullException.ThrowIfNull(analysisJobId);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(timeout);
        if (audioArtifactId == timelineArtifactId ||
            audioArtifactId == sourceOriginal.Id ||
            timelineArtifactId == sourceOriginal.Id)
        {
            throw Failure(AudioExtractionFailureReason.InputBindingMismatch);
        }

        ValidateInputBinding(sourceAsset, sourceOriginal, sourceVideoPtsIndex);
        EnsureOutputIdsAvailable(audioArtifactId, timelineArtifactId);
        string sourcePath = GetRegisteredOriginalPath(sourceOriginal);
        using FileStream sourceStream = OpenRegisteredOriginal(sourcePath);
        HeldFileSnapshot sourceSnapshot = CaptureHeldSource(
            sourceStream,
            sourceOriginal.ByteSize);
        await VerifyRegisteredExactAsync(
            sourceOriginal,
            AudioExtractionFailureReason.RegisteredOriginalInvalid,
            reconciliationRequired: true,
            cancellationToken).ConfigureAwait(false);
        ValidateHeldSource(sourceStream, sourceSnapshot);

        var sourceMedia = new StagedMediaImport(
            sourceAsset.Id,
            sourceOriginal.ProjectId,
            sourceVideoPtsIndex.JobId,
            sourceAsset.FileName,
            sourcePath,
            sourceAsset.ContentDigest,
            sourceAsset.ByteSize,
            sourceOriginal.CreatedAt);
        MediaProbeResult sourceProbe = await ProbeSourceAsync(
            sourceMedia,
            timeout,
            cancellationToken).ConfigureAwait(false);
        ProbedMediaStream sourceAudio = ValidateSourceEvidence(
            sourceMedia,
            sourceProbe,
            sourceVideoPtsIndex);
        await VerifyToolsAsync(cancellationToken).ConfigureAwait(false);
        ValidateHeldSource(sourceStream, sourceSnapshot);

        DecodedAudioStart decodedStart = await ProbeDecodedAudioStartAsync(
            sourcePath,
            sourceAudio.Index,
            timeout,
            cancellationToken).ConfigureAwait(false);
        ValidateHeldSource(sourceStream, sourceSnapshot);

        StagingPaths staging = PrepareStaging(
            sourceOriginal.ProjectId,
            audioArtifactId,
            cancellationToken);
        int promotedCount = 0;
        try
        {
            ProcessRunRequest extractionRequest = CreateExtractionProcessRequest(
                _ffmpegPath,
                sourcePath,
                staging.AudioPath,
                sourceAudio.Index,
                timeout);
            ProcessRunResult processResult = await RunProcessAsync(
                extractionRequest,
                AudioExtractionFailureReason.ExtractionFailed,
                cancellationToken).ConfigureAwait(false);
            ValidateCleanProcessResult(
                processResult,
                AudioExtractionFailureReason.ExtractionFailed);
            ValidateHeldSource(sourceStream, sourceSnapshot);

            WavSnapshot wav = ParseWaveFile(staging.AudioPath);
            ContentDigest audioDigest = await ComputeDigestAsync(
                staging.AudioPath,
                cancellationToken).ConfigureAwait(false);
            ContentDigest ptsDigest = ContentDigest.Compute(
                StrictUtf8.GetBytes(sourceVideoPtsIndex.CanonicalJson));
            RationalTime audioTimeBase = new(
                sourceAudio.TimeBase.Numerator,
                sourceAudio.TimeBase.Denominator);
            RationalTime? probeStart = ConvertDecimalSeconds(
                sourceAudio.StartSeconds);
            var timeline = new AnalysisAudioTimeline(
                sourceOriginal.ProjectId,
                analysisJobId,
                sourceAsset.Id,
                sourceAsset.ContentDigest,
                sourceAsset.ByteSize,
                sourceOriginal.Id,
                ptsDigest,
                sourceVideoPtsIndex.StreamIndex,
                sourceAudio.Index,
                audioTimeBase,
                probeStart,
                decodedStart.Timestamp,
                decodedStart.SampleCount,
                audioArtifactId,
                audioDigest,
                wav.ByteSize,
                wav.SampleCount,
                timelineArtifactId);
            FileSnapshot timelineSnapshot = await WriteTimelineAsync(
                staging.TimelinePath,
                timeline.CanonicalJson,
                cancellationToken).ConfigureAwait(false);

            ValidateHeldSource(sourceStream, sourceSnapshot);
            await VerifyRegisteredExactAsync(
                sourceOriginal,
                AudioExtractionFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true,
                cancellationToken).ConfigureAwait(false);
            ValidateHeldSource(sourceStream, sourceSnapshot);
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset createdAt = _timeProvider.GetUtcNow().ToUniversalTime();
            string relativeRoot = string.Concat(sourceAsset.Id.ToString(), "/");
            StoredArtifact audioArtifact = await PromoteAsync(
                new ArtifactPromotionRequest(
                    audioArtifactId,
                    sourceOriginal.ProjectId,
                    ArtifactKind.Analysis,
                    staging.AudioPath,
                    string.Concat(
                        relativeRoot,
                        audioArtifactId.ToString(),
                        ".analysis.wav"),
                    audioDigest,
                    wav.ByteSize,
                    createdAt),
                cancellationToken).ConfigureAwait(false);
            promotedCount++;
            StoredArtifact timelineArtifact = await PromoteAsync(
                new ArtifactPromotionRequest(
                    timelineArtifactId,
                    sourceOriginal.ProjectId,
                    ArtifactKind.Analysis,
                    staging.TimelinePath,
                    string.Concat(
                        relativeRoot,
                        timelineArtifactId.ToString(),
                        ".audio-timeline.json"),
                    timelineSnapshot.Digest,
                    timelineSnapshot.ByteSize,
                    createdAt),
                CancellationToken.None).ConfigureAwait(false);
            promotedCount++;

            if (!TryCleanupOwnedStaging(staging))
            {
                throw Failure(
                    AudioExtractionFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            ValidatePromotedMetadata(
                audioArtifact,
                timelineArtifact,
                timeline,
                createdAt);
            await VerifyRegisteredExactAsync(
                audioArtifact,
                AudioExtractionFailureReason.RegisteredAnalysisInvalid,
                reconciliationRequired: true,
                CancellationToken.None).ConfigureAwait(false);
            await VerifyRegisteredExactAsync(
                timelineArtifact,
                AudioExtractionFailureReason.RegisteredAnalysisInvalid,
                reconciliationRequired: true,
                CancellationToken.None).ConfigureAwait(false);
            ValidateHeldSource(sourceStream, sourceSnapshot);
            return new ExtractedAnalysisAudio(
                audioArtifact,
                timelineArtifact,
                timeline,
                processResult.Duration);
        }
        catch (Exception exception)
        {
            bool cleaned = TryCleanupOwnedStaging(staging);
            if (!cleaned)
            {
                throw Failure(
                    AudioExtractionFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (promotedCount > 0)
            {
                if (exception is AudioExtractionException extractionException &&
                    extractionException.ReconciliationRequired)
                {
                    throw;
                }

                throw Failure(
                    AudioExtractionFailureReason.PromotionFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is AudioExtractionException)
            {
                throw;
            }

            if (exception is OverflowException)
            {
                throw Failure(AudioExtractionFailureReason.ArithmeticOverflow);
            }

            throw Failure(AudioExtractionFailureReason.ExtractionFailed);
        }
    }

    internal static ProcessRunRequest CreateExtractionProcessRequest(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        int audioStreamIndex,
        TimeSpan timeout)
    {
        if (audioStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        string executable = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));
        string source = SqliteStoreSupport.ValidateLocalAbsolutePath(
            sourcePath,
            nameof(sourcePath));
        string destination = SqliteStoreSupport.ValidateLocalAbsolutePath(
            outputPath,
            nameof(outputPath));
        ValidateTimeout(timeout);
        string workingDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException(
                "The analysis audio output must have a parent directory.",
                nameof(outputPath));
        return new ProcessRunRequest(
            executable,
            [
                "-nostdin",
                "-v",
                "error",
                "-i",
                source,
                "-map",
                string.Concat(
                    "0:",
                    audioStreamIndex.ToString(CultureInfo.InvariantCulture)),
                "-vn",
                "-map_metadata",
                "-1",
                "-ac",
                "1",
                "-ar",
                AnalysisAudioTimeline.FixedSampleRate.ToString(
                    CultureInfo.InvariantCulture),
                "-c:a",
                "pcm_s16le",
                "-fflags",
                "+bitexact",
                "-flags:a",
                "+bitexact",
                "-n",
                destination,
            ],
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    internal static ProcessRunRequest CreateDecodedFrameProbeRequest(
        string ffprobePath,
        string sourcePath,
        int audioStreamIndex,
        TimeSpan timeout)
    {
        if (audioStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        string executable = SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffprobePath,
            nameof(ffprobePath));
        string source = SqliteStoreSupport.ValidateLocalAbsolutePath(
            sourcePath,
            nameof(sourcePath));
        ValidateTimeout(timeout);
        return new ProcessRunRequest(
            executable,
            [
                "-v",
                "error",
                "-select_streams",
                audioStreamIndex.ToString(CultureInfo.InvariantCulture),
                "-read_intervals",
                DecodedFrameReadInterval,
                "-show_frames",
                "-show_entries",
                DecodedFrameShowEntries,
                "-of",
                CompactOutputFormat,
                source,
            ],
            Path.GetDirectoryName(source)!,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    internal static DecodedAudioStart ParseDecodedAudioStart(
        ProcessRunResult result,
        int expectedStreamIndex)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (expectedStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedStreamIndex));
        }

        ValidateCleanProcessResult(
            result,
            AudioExtractionFailureReason.TimelineProbeFailed,
            requireOutput: true);
        string[] lines = SplitLines(result.StandardOutput);
        long? firstTimestamp = null;
        long? previousTimestamp = null;
        int firstSampleCount = 0;
        foreach (string line in lines)
        {
            Dictionary<string, string> fields = ParseFields(line);
            int streamIndex = ParseNonnegativeInt32(fields["stream_index"]);
            long timestamp = ParseSignedInt64(fields["best_effort_timestamp"]);
            int sampleCount = ParsePositiveInt32(fields["nb_samples"]);
            if (streamIndex != expectedStreamIndex ||
                previousTimestamp is not null && timestamp <= previousTimestamp.Value)
            {
                throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
            }

            if (firstTimestamp is null)
            {
                firstTimestamp = timestamp;
                firstSampleCount = sampleCount;
            }

            previousTimestamp = timestamp;
        }

        return new DecodedAudioStart(
            firstTimestamp
                ?? throw Failure(AudioExtractionFailureReason.TimelineProbeFailed),
            firstSampleCount);
    }

    internal static WavSnapshot ParseWaveFile(string path)
    {
        string normalized = SqliteStoreSupport.ValidateLocalAbsolutePath(
            path,
            nameof(path));
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                Path.GetDirectoryName(normalized)!,
                normalized,
                targetIsDirectory: false);
            using FileStream stream = new(
                normalized,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.SequentialScan);
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            using BinaryReader reader = new(
                stream,
                Encoding.ASCII,
                leaveOpen: true);
            if (stream.Length < 44 ||
                !reader.ReadBytes(4).AsSpan().SequenceEqual("RIFF"u8) ||
                reader.ReadUInt32() != checked((uint)(stream.Length - 8)) ||
                !reader.ReadBytes(4).AsSpan().SequenceEqual("WAVE"u8))
            {
                throw Failure(AudioExtractionFailureReason.OutputInvalid);
            }

            bool formatSeen = false;
            bool dataSeen = false;
            long sampleCount = 0;
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8)
                {
                    throw Failure(AudioExtractionFailureReason.OutputInvalid);
                }

                string chunkName = Encoding.ASCII.GetString(reader.ReadBytes(4));
                uint chunkSize = reader.ReadUInt32();
                long chunkEnd = checked(stream.Position + chunkSize);
                if (chunkEnd > stream.Length || chunkName is not ("fmt " or "data"))
                {
                    throw Failure(AudioExtractionFailureReason.OutputInvalid);
                }

                if (chunkName == "fmt ")
                {
                    if (formatSeen || dataSeen || chunkSize != 16 ||
                        reader.ReadUInt16() != 1 ||
                        reader.ReadUInt16() != 1 ||
                        reader.ReadUInt32() != AnalysisAudioTimeline.FixedSampleRate ||
                        reader.ReadUInt32() != AnalysisAudioTimeline.FixedSampleRate * sizeof(short) ||
                        reader.ReadUInt16() != sizeof(short) ||
                        reader.ReadUInt16() != sizeof(short) * 8)
                    {
                        throw Failure(AudioExtractionFailureReason.OutputInvalid);
                    }

                    formatSeen = true;
                }
                else
                {
                    if (!formatSeen || dataSeen || chunkSize == 0 ||
                        chunkSize % sizeof(short) != 0)
                    {
                        throw Failure(AudioExtractionFailureReason.OutputInvalid);
                    }

                    dataSeen = true;
                    sampleCount = chunkSize / sizeof(short);
                    stream.Position = chunkEnd;
                }

                if (stream.Position != chunkEnd)
                {
                    throw Failure(AudioExtractionFailureReason.OutputInvalid);
                }

                if ((chunkSize & 1) != 0)
                {
                    if (stream.ReadByte() < 0)
                    {
                        throw Failure(AudioExtractionFailureReason.OutputInvalid);
                    }
                }
            }

            if (!formatSeen || !dataSeen || stream.Position != stream.Length)
            {
                throw Failure(AudioExtractionFailureReason.OutputInvalid);
            }

            return new WavSnapshot(stream.Length, sampleCount);
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or EndOfStreamException or OverflowException or
                WorkspacePathSafetyException)
        {
            throw Failure(AudioExtractionFailureReason.OutputInvalid);
        }
    }

    internal static void ValidateInputBinding(
        Asset sourceAsset,
        StoredArtifact sourceOriginal,
        PtsIndex sourceVideoPtsIndex)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentNullException.ThrowIfNull(sourceOriginal);
        ArgumentNullException.ThrowIfNull(sourceVideoPtsIndex);
        string expectedReference = string.Concat(
            "projects/",
            sourceOriginal.ProjectId.ToString(),
            "/original/",
            sourceAsset.Id.ToString(),
            "/",
            sourceAsset.ContentDigest.Hex,
            ".original");
        if (sourceOriginal.Kind != ArtifactKind.Original ||
            sourceAsset.Id != sourceVideoPtsIndex.CandidateId ||
            !string.Equals(
                sourceAsset.FileName,
                sourceVideoPtsIndex.SourceFileName,
                StringComparison.Ordinal) ||
            sourceAsset.ContentDigest != sourceVideoPtsIndex.ContentDigest ||
            sourceAsset.ByteSize != sourceVideoPtsIndex.ByteSize ||
            sourceOriginal.ProjectId != sourceVideoPtsIndex.ProjectId ||
            !string.Equals(
                sourceOriginal.StorageReference,
                expectedReference,
                StringComparison.Ordinal) ||
            sourceOriginal.ContentDigest != sourceAsset.ContentDigest ||
            sourceOriginal.ByteSize != sourceAsset.ByteSize)
        {
            throw Failure(AudioExtractionFailureReason.InputBindingMismatch);
        }
    }

    private static ProbedMediaStream ValidateSourceEvidence(
        StagedMediaImport sourceMedia,
        MediaProbeResult probe,
        PtsIndex sourceVideoPtsIndex)
    {
        if (probe.CandidateId != sourceMedia.CandidateId ||
            probe.ProjectId != sourceMedia.ProjectId ||
            probe.JobId != sourceMedia.JobId ||
            !string.Equals(
                probe.SourceFileName,
                sourceMedia.SourceFileName,
                StringComparison.Ordinal) ||
            probe.ContentDigest != sourceMedia.ContentDigest ||
            probe.ByteSize != sourceMedia.ByteSize)
        {
            throw Failure(AudioExtractionFailureReason.InputBindingMismatch);
        }

        ProbedMediaStream video = probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .MinBy(static stream => stream.Index)
            ?? throw Failure(AudioExtractionFailureReason.SourceProbeFailed);
        if (video.Index != sourceVideoPtsIndex.StreamIndex ||
            new RationalTime(
                video.TimeBase.Numerator,
                video.TimeBase.Denominator) != sourceVideoPtsIndex.TimeBase)
        {
            throw Failure(AudioExtractionFailureReason.InputBindingMismatch);
        }

        return probe.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .MinBy(static stream => stream.Index)
            ?? throw Failure(AudioExtractionFailureReason.NoAudioStream);
    }

    private async Task<MediaProbeResult> ProbeSourceAsync(
        StagedMediaImport media,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probeAsync(media, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is TimeoutException or MediaProbeException
            {
                Reason: MediaProbeFailureReason.ProcessTimedOut,
            })
        {
            throw Failure(AudioExtractionFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(AudioExtractionFailureReason.SourceProbeFailed);
        }
    }

    private async Task<DecodedAudioStart> ProbeDecodedAudioStartAsync(
        string sourcePath,
        int streamIndex,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            CreateDecodedFrameProbeRequest(
                _ffprobePath,
                sourcePath,
                streamIndex,
                timeout),
            AudioExtractionFailureReason.TimelineProbeFailed,
            cancellationToken).ConfigureAwait(false);
        return ParseDecodedAudioStart(result, streamIndex);
    }

    private async Task VerifyToolsAsync(CancellationToken cancellationToken)
    {
        await VerifyBinaryAsync(
            new BinaryVerificationRequest(
                FfmpegComponentIdentity,
                _ffmpegPath,
                CanonicalFfmpegDigest,
                DecodeValidator.CanonicalFfmpegByteSize),
            cancellationToken).ConfigureAwait(false);
        await VerifyBinaryAsync(
            new BinaryVerificationRequest(
                FfprobeComponentIdentity,
                _ffprobePath,
                CanonicalFfprobeDigest,
                FfprobeClient.CanonicalFfprobeByteSize),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyBinaryAsync(
        BinaryVerificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!IsExactVerification(result, request))
            {
                throw Failure(AudioExtractionFailureReason.ToolVerificationFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(AudioExtractionFailureReason.ToolVerificationFailed);
        }
    }

    private async Task VerifyRegisteredExactAsync(
        StoredArtifact expected,
        AudioExtractionFailureReason reason,
        bool reconciliationRequired,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _verifyRegisteredAsync(
                expected.Id,
                cancellationToken).ConfigureAwait(false);
            StoredArtifact current = _artifactStore.GetArtifact(expected.Id)
                ?? throw Failure(reason, reconciliationRequired);
            if (!result.IsVerified ||
                result.ExpectedDigest != expected.ContentDigest ||
                result.ActualDigest != expected.ContentDigest ||
                result.ExpectedByteSize != expected.ByteSize ||
                result.ActualByteSize != expected.ByteSize ||
                !HasSameMetadata(expected, current))
            {
                throw Failure(reason, reconciliationRequired);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(reason, reconciliationRequired);
        }
    }

    private string GetRegisteredOriginalPath(StoredArtifact expected)
    {
        try
        {
            StoredArtifact current = _artifactStore.GetArtifact(expected.Id)
                ?? throw Failure(
                    AudioExtractionFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            if (!HasSameMetadata(expected, current))
            {
                throw Failure(AudioExtractionFailureReason.InputBindingMismatch);
            }

            return _artifactStore.GetValidatedRegisteredArtifactPath(expected.Id);
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AudioExtractionFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private void EnsureOutputIdsAvailable(params EntityId<ArtifactIdTag>[] ids)
    {
        try
        {
            if (ids.Any(id => _artifactStore.GetArtifact(id) is not null))
            {
                throw Failure(AudioExtractionFailureReason.PromotionFailed);
            }
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                AudioExtractionFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private StagingPaths PrepareStaging(
        EntityId<ProjectIdTag> projectId,
        EntityId<ArtifactIdTag> audioArtifactId,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(Path.Combine(
            ProjectFolders.GetPath(_workspacePath, projectId),
            "staging",
            "analysis-audio"));
        EnsureDirectoryChain(root, cancellationToken);
        string directory = Path.Combine(
            root,
            audioArtifactId.ToString());
        if (WorkspacePathSafety.PathEntryExistsConservatively(directory))
        {
            throw Failure(AudioExtractionFailureReason.StagingConflict);
        }

        string claim = Path.Combine(
            root,
            string.Concat(".", Guid.NewGuid().ToString("N"), ".claim"));
        bool created = false;
        try
        {
            Directory.CreateDirectory(claim);
            created = true;
            ValidateWorkspaceDirectory(claim);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(claim, directory);
            created = false;
            ValidateWorkspaceDirectory(directory);
            return new StagingPaths(
                directory,
                Path.Combine(directory, "audio.partial.wav"),
                Path.Combine(directory, "timeline.partial.json"));
        }
        catch (Exception exception)
        {
            if (created && !TryDeleteOwnedEmptyDirectory(claim))
            {
                throw Failure(
                    AudioExtractionFailureReason.CleanupFailed,
                    reconciliationRequired: true);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is AudioExtractionException)
            {
                throw;
            }

            throw Failure(
                WorkspacePathSafety.PathEntryExistsConservatively(directory)
                    ? AudioExtractionFailureReason.StagingConflict
                    : AudioExtractionFailureReason.StagingUnavailable);
        }
    }

    private void EnsureDirectoryChain(
        string destination,
        CancellationToken cancellationToken)
    {
        if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, destination))
        {
            throw Failure(AudioExtractionFailureReason.StagingUnavailable);
        }

        string relative = Path.GetRelativePath(_workspacePath, destination);
        string current = _workspacePath;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWorkspaceDirectory(current);
            current = Path.Combine(current, segment);
            if (!WorkspacePathSafety.TryGetAttributes(current, out _))
            {
                Directory.CreateDirectory(current);
            }

            ValidateWorkspaceDirectory(current);
        }
    }

    private bool TryCleanupOwnedStaging(StagingPaths paths)
    {
        try
        {
            foreach (string path in new[] { paths.AudioPath, paths.TimelinePath })
            {
                if (!WorkspacePathSafety.TryGetAttributes(path, out FileAttributes attributes))
                {
                    continue;
                }

                WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
                WorkspacePathSafety.ValidateExistingPathChain(
                    paths.DirectoryPath,
                    path,
                    targetIsDirectory: false);
                File.Delete(path);
                if (WorkspacePathSafety.PathEntryExistsConservatively(path))
                {
                    return false;
                }
            }

            return TryDeleteOwnedEmptyDirectory(paths.DirectoryPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryDeleteOwnedEmptyDirectory(string path)
    {
        try
        {
            if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, path))
            {
                return false;
            }

            if (!WorkspacePathSafety.TryGetAttributes(path, out FileAttributes attributes))
            {
                return true;
            }

            WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: true);
            Directory.Delete(path, recursive: false);
            return !WorkspacePathSafety.PathEntryExistsConservatively(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ValidateWorkspaceDirectory(string path)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: true);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(AudioExtractionFailureReason.StagingUnavailable);
        }
    }

    private async Task<StoredArtifact> PromoteAsync(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _promoteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArtifactStoreException exception)
        {
            throw Failure(
                AudioExtractionFailureReason.PromotionFailed,
                exception.ReconciliationRequired);
        }
        catch (Exception)
        {
            throw Failure(
                AudioExtractionFailureReason.PromotionFailed,
                reconciliationRequired: true);
        }
    }

    private async Task<ProcessRunResult> RunProcessAsync(
        ProcessRunRequest request,
        AudioExtractionFailureReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runProcessAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(AudioExtractionFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(reason);
        }
    }

    private static void ValidateCleanProcessResult(
        ProcessRunResult result,
        AudioExtractionFailureReason reason,
        bool requireOutput = false)
    {
        if (result.ExitCode != 0 ||
            result.Truncated ||
            result.StandardOutput is null ||
            result.StandardError is null ||
            result.StandardError.Length != 0 ||
            (!requireOutput && result.StandardOutput.Length != 0) ||
            (requireOutput && result.StandardOutput.Length == 0) ||
            result.Duration < TimeSpan.Zero)
        {
            throw Failure(reason);
        }
    }

    private static string[] SplitLines(string output)
    {
        if (output.Contains('\uFFFD', StringComparison.Ordinal) ||
            !output.EndsWith('\n'))
        {
            throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
        }

        for (int index = 0; index < output.Length; index++)
        {
            if (output[index] == '\r' &&
                (index + 1 >= output.Length || output[index + 1] != '\n'))
            {
                throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
            }
        }

        int bodyLength = output.Length - 1;
        if (bodyLength > 0 && output[bodyLength - 1] == '\r')
        {
            bodyLength--;
        }

        if (bodyLength == 0)
        {
            throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
        }

        string[] lines = output[..bodyLength].Split('\n', StringSplitOptions.None);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (line.Length == 0 ||
                !string.Equals(line, line.Trim(), StringComparison.Ordinal) ||
                line.Any(char.IsControl))
            {
                throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
            }

            lines[index] = line;
        }

        return lines;
    }

    private static Dictionary<string, string> ParseFields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string field in line.Split('|', StringSplitOptions.None))
        {
            int separator = field.IndexOf('=');
            if (separator <= 0 || separator != field.LastIndexOf('='))
            {
                throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
            }

            string name = field[..separator];
            string value = field[(separator + 1)..];
            if (name is not ("stream_index" or "best_effort_timestamp" or "nb_samples") ||
                value.Length == 0 ||
                !fields.TryAdd(name, value))
            {
                throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
            }
        }

        if (fields.Count != 3 ||
            !fields.ContainsKey("stream_index") ||
            !fields.ContainsKey("best_effort_timestamp") ||
            !fields.ContainsKey("nb_samples"))
        {
            throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
        }

        return fields;
    }

    private static int ParseNonnegativeInt32(string value)
    {
        if (!IsCanonicalUnsigned(value) ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result))
        {
            throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
        }

        return result;
    }

    private static int ParsePositiveInt32(string value)
    {
        int result = ParseNonnegativeInt32(value);
        if (result <= 0)
        {
            throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
        }

        return result;
    }

    private static long ParseSignedInt64(string value)
    {
        int start = value.StartsWith("-", StringComparison.Ordinal) ? 1 : 0;
        if (start == value.Length ||
            start == 1 && value[1] == '0' ||
            value.Length - start > 1 && value[start] == '0' ||
            value.AsSpan(start).IndexOfAnyExceptInRange('0', '9') >= 0 ||
            !long.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out long result))
        {
            throw Failure(AudioExtractionFailureReason.TimelineProbeFailed);
        }

        return result;
    }

    private static bool IsCanonicalUnsigned(string value) =>
        value.Length > 0 &&
        (value.Length == 1 || value[0] != '0') &&
        value.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    private static async Task<ContentDigest> ComputeDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = new byte[32];
        int written = await SHA256.HashDataAsync(
            stream,
            digest,
            cancellationToken).ConfigureAwait(false);
        if (written != digest.Length)
        {
            throw Failure(AudioExtractionFailureReason.OutputInvalid);
        }

        return ContentDigest.FromBytes(digest);
    }

    private static async Task<FileSnapshot> WriteTimelineAsync(
        string path,
        string canonicalJson,
        CancellationToken cancellationToken)
    {
        byte[] bytes = StrictUtf8.GetBytes(canonicalJson);
        await using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        return new FileSnapshot(bytes.LongLength, ContentDigest.Compute(bytes));
    }

    private static RationalTime? ConvertDecimalSeconds(decimal? seconds)
    {
        if (seconds is null)
        {
            return null;
        }

        int[] bits = decimal.GetBits(seconds.Value);
        BigInteger numerator = (uint)bits[0] |
            (BigInteger)(uint)bits[1] << 32 |
            (BigInteger)(uint)bits[2] << 64;
        int scale = (bits[3] >> 16) & 0x7F;
        if ((bits[3] & int.MinValue) != 0)
        {
            numerator = -numerator;
        }

        BigInteger denominator = BigInteger.Pow(10, scale);
        BigInteger divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator < long.MinValue ||
            numerator > long.MaxValue ||
            denominator > long.MaxValue)
        {
            throw Failure(AudioExtractionFailureReason.ArithmeticOverflow);
        }

        return new RationalTime((long)numerator, (long)denominator);
    }

    private static void ValidatePromotedMetadata(
        StoredArtifact audio,
        StoredArtifact timelineArtifact,
        AnalysisAudioTimeline timeline,
        DateTimeOffset createdAt)
    {
        byte[] timelineBytes = StrictUtf8.GetBytes(timeline.CanonicalJson);
        if (audio.Id != timeline.AnalysisAudioArtifactId ||
            audio.ProjectId != timeline.ProjectId ||
            audio.Kind != ArtifactKind.Analysis ||
            audio.ContentDigest != timeline.AnalysisAudioDigest ||
            audio.ByteSize != timeline.AnalysisAudioByteSize ||
            audio.CreatedAt != createdAt ||
            timelineArtifact.Id != timeline.TimelineArtifactId ||
            timelineArtifact.ProjectId != timeline.ProjectId ||
            timelineArtifact.Kind != ArtifactKind.Analysis ||
            timelineArtifact.ContentDigest != ContentDigest.Compute(timelineBytes) ||
            timelineArtifact.ByteSize != timelineBytes.LongLength ||
            timelineArtifact.CreatedAt != createdAt)
        {
            throw Failure(
                AudioExtractionFailureReason.RegisteredAnalysisInvalid,
                reconciliationRequired: true);
        }
    }

    private static bool IsExactVerification(
        BinaryVerificationResult result,
        BinaryVerificationRequest request) =>
        result.IsVerified &&
        result.ExpectedDigest == request.ExpectedDigest &&
        result.ActualDigest == request.ExpectedDigest &&
        result.ExpectedByteSize == request.ExpectedByteSize &&
        result.ActualByteSize == request.ExpectedByteSize;

    private static bool HasSameMetadata(StoredArtifact left, StoredArtifact right) =>
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

    private static FileStream OpenRegisteredOriginal(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(
                AudioExtractionFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private static HeldFileSnapshot CaptureHeldSource(
        FileStream stream,
        long expectedLength)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead ||
                !stream.CanSeek ||
                stream.Length != expectedLength ||
                expectedLength <= 0)
            {
                throw Failure(
                    AudioExtractionFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            }

            return new HeldFileSnapshot(
                stream.Length,
                attributes,
                File.GetLastWriteTimeUtc(stream.SafeFileHandle));
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            throw Failure(
                AudioExtractionFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private static void ValidateHeldSource(
        FileStream stream,
        HeldFileSnapshot expected)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead ||
                !stream.CanSeek ||
                stream.Length != expected.Length ||
                attributes != expected.Attributes ||
                File.GetLastWriteTimeUtc(stream.SafeFileHandle) != expected.LastWriteTimeUtc)
            {
                throw Failure(
                    AudioExtractionFailureReason.RegisteredOriginalInvalid,
                    reconciliationRequired: true);
            }
        }
        catch (AudioExtractionException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException ||
                IsFileSystemFailure(exception))
        {
            throw Failure(
                AudioExtractionFailureReason.RegisteredOriginalInvalid,
                reconciliationRequired: true);
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException;

    private static string ValidateWorkspacePath(string workspacePath)
    {
        string normalized = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                workspacePath,
                nameof(workspacePath)));
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(normalized);
        }
        catch (WorkspacePathSafetyException)
        {
            throw new ArgumentException(
                "The workspace must be an existing local directory without reparse points.",
                nameof(workspacePath));
        }

        return normalized;
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The audio extraction timeout must be positive and supported.");
        }
    }

    private static Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
        GetProcessDelegate(ProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync;
    }

    private static Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>>
        GetBinaryVerificationDelegate(BinaryVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return verifier.VerifyAsync;
    }

    private static Func<StagedMediaImport, TimeSpan, CancellationToken, Task<MediaProbeResult>>
        GetProbeDelegate(FfprobeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.ProbeAsync;
    }

    private static Func<EntityId<ArtifactIdTag>, CancellationToken, Task<BinaryVerificationResult>>
        GetRegisteredVerificationDelegate(
            ArtifactStore store,
            BinaryVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(verifier);
        return (id, cancellationToken) =>
            store.VerifyRegisteredAsync(id, verifier, cancellationToken);
    }

    private static Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>
        GetPromotionDelegate(ArtifactStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.PromoteAsync;
    }

    private static AudioExtractionException Failure(
        AudioExtractionFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    internal readonly record struct DecodedAudioStart(long Timestamp, int SampleCount);

    internal readonly record struct WavSnapshot(long ByteSize, long SampleCount);

    private readonly record struct FileSnapshot(long ByteSize, ContentDigest Digest);

    private readonly record struct HeldFileSnapshot(
        long Length,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc);

    private sealed record StagingPaths(
        string DirectoryPath,
        string AudioPath,
        string TimelinePath);
}