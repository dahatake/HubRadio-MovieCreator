using System.Globalization;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;

namespace HubRadio.App.Analysis;

public sealed class AsrWorkerConfiguration
{
    public AsrWorkerConfiguration(
        string device,
        string computeType,
        string language,
        string task,
        int beamSize,
        bool wordTimestamps,
        bool vadFilter,
        bool conditionOnPreviousText)
    {
        AsrWorkerValidation.ValidateMetadataText(device, nameof(device));
        AsrWorkerValidation.ValidateMetadataText(computeType, nameof(computeType));
        AsrWorkerValidation.ValidateMetadataText(language, nameof(language));
        AsrWorkerValidation.ValidateMetadataText(task, nameof(task));
        if (beamSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beamSize),
                "The ASR beam size must be positive.");
        }

        Device = device;
        ComputeType = computeType;
        Language = language;
        Task = task;
        BeamSize = beamSize;
        WordTimestamps = wordTimestamps;
        VadFilter = vadFilter;
        ConditionOnPreviousText = conditionOnPreviousText;
    }

    public string Device { get; }

    public string ComputeType { get; }

    public string Language { get; }

    public string Task { get; }

    public int BeamSize { get; }

    public bool WordTimestamps { get; }

    public bool VadFilter { get; }

    public bool ConditionOnPreviousText { get; }
}

public sealed class AsrWorkerToken
{
    public AsrWorkerToken(
        string text,
        double? startSeconds,
        double? endSeconds,
        double recognitionConfidence,
        bool timingRequiresReview)
    {
        AsrWorkerValidation.ValidateRecognizedText(text, nameof(text));
        AsrWorkerValidation.ValidateConfidence(
            recognitionConfidence,
            nameof(recognitionConfidence));

        if (startSeconds.HasValue != endSeconds.HasValue)
        {
            throw new ArgumentException(
                "ASR token timing boundaries must either both be present or both be absent.",
                nameof(startSeconds));
        }

        if (startSeconds is double start && endSeconds is double end)
        {
            AsrWorkerValidation.ValidateNonnegativeFinite(start, nameof(startSeconds));
            AsrWorkerValidation.ValidateNonnegativeFinite(end, nameof(endSeconds));
            if (end < start)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(endSeconds),
                    "An ASR token end must not precede its start.");
            }

            if (end == start && !timingRequiresReview)
            {
                throw new ArgumentException(
                    "A zero-duration ASR token must require timing review.",
                    nameof(timingRequiresReview));
            }
        }
        else if (!timingRequiresReview)
        {
            throw new ArgumentException(
                "An untimed ASR token must require timing review.",
                nameof(timingRequiresReview));
        }

        Text = text;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        RecognitionConfidence = recognitionConfidence;
        TimingRequiresReview = timingRequiresReview;
    }

    public string Text { get; }

    public double? StartSeconds { get; }

    public double? EndSeconds { get; }

    public double RecognitionConfidence { get; }

    public bool TimingRequiresReview { get; }
}

public sealed class AsrWorkerSegment
{
    public AsrWorkerSegment(
        int segmentId,
        int seek,
        string text,
        double startSeconds,
        double endSeconds,
        double averageLogProbability,
        double noSpeechProbability,
        IReadOnlyList<AsrWorkerToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (segmentId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentId),
                "An ASR segment ID must not be negative.");
        }

        if (seek < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seek),
                "An ASR seek value must not be negative.");
        }

        AsrWorkerValidation.ValidateRecognizedText(text, nameof(text));
        AsrWorkerValidation.ValidateNonnegativeFinite(startSeconds, nameof(startSeconds));
        AsrWorkerValidation.ValidateNonnegativeFinite(endSeconds, nameof(endSeconds));
        AsrWorkerValidation.ValidateFinite(
            averageLogProbability,
            nameof(averageLogProbability));
        AsrWorkerValidation.ValidateConfidence(
            noSpeechProbability,
            nameof(noSpeechProbability));
        if (endSeconds <= startSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endSeconds),
                "An ASR segment must have a positive duration.");
        }

        if (tokens.Count == 0)
        {
            throw new ArgumentException(
                "An ASR segment must contain at least one token.",
                nameof(tokens));
        }

        AsrWorkerToken[] copiedTokens = new AsrWorkerToken[tokens.Count];
        double? previousTimedStart = null;
        for (int index = 0; index < copiedTokens.Length; index++)
        {
            AsrWorkerToken token = tokens[index]
                ?? throw new ArgumentException(
                    "ASR segment tokens must not contain null elements.",
                    nameof(tokens));
            if (token.StartSeconds is double tokenStart &&
                token.EndSeconds is double tokenEnd)
            {
                if (previousTimedStart is double previous && tokenStart < previous)
                {
                    throw new ArgumentException(
                        "Timed ASR tokens must be ordered by raw start time.",
                        nameof(tokens));
                }

                bool outsideSegment = tokenStart < startSeconds || tokenEnd > endSeconds;
                if (outsideSegment && !token.TimingRequiresReview)
                {
                    throw new ArgumentException(
                        "An ASR token outside its segment must require timing review.",
                        nameof(tokens));
                }

                previousTimedStart = tokenStart;
            }

            copiedTokens[index] = token;
        }

        SegmentId = segmentId;
        Seek = seek;
        Text = text;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        AverageLogProbability = averageLogProbability;
        NoSpeechProbability = noSpeechProbability;
        Tokens = Array.AsReadOnly(copiedTokens);
    }

    public int SegmentId { get; }

    public int Seek { get; }

    public string Text { get; }

    public double StartSeconds { get; }

    public double EndSeconds { get; }

    public double AverageLogProbability { get; }

    public double NoSpeechProbability { get; }

    public IReadOnlyList<AsrWorkerToken> Tokens { get; }
}

public sealed class AsrWorkerResult
{
    public AsrWorkerResult(
        string modelRepository,
        string modelRevision,
        ContentDigest modelManifestDigest,
        AsrWorkerConfiguration configuration,
        ContentDigest inputAudioDigest,
        long inputAudioByteSize,
        double inputAudioDurationSeconds,
        string language,
        double languageConfidence,
        double engineDurationSeconds,
        IReadOnlyList<AsrWorkerSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(modelManifestDigest);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inputAudioDigest);
        ArgumentNullException.ThrowIfNull(segments);
        AsrWorkerValidation.ValidateMetadataText(
            modelRepository,
            nameof(modelRepository));
        AsrWorkerValidation.ValidateMetadataText(modelRevision, nameof(modelRevision));
        if (inputAudioByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputAudioByteSize),
                "The ASR input audio byte size must be positive.");
        }

        AsrWorkerValidation.ValidatePositiveFinite(
            inputAudioDurationSeconds,
            nameof(inputAudioDurationSeconds));
        AsrWorkerValidation.ValidateMetadataText(language, nameof(language));
        AsrWorkerValidation.ValidateConfidence(
            languageConfidence,
            nameof(languageConfidence));
        AsrWorkerValidation.ValidateNonnegativeFinite(
            engineDurationSeconds,
            nameof(engineDurationSeconds));

        AsrWorkerSegment[] copiedSegments = new AsrWorkerSegment[segments.Count];
        HashSet<int> segmentIds = [];
        double? previousSegmentStart = null;
        double? previousTimedTokenStart = null;
        for (int segmentIndex = 0; segmentIndex < copiedSegments.Length; segmentIndex++)
        {
            AsrWorkerSegment segment = segments[segmentIndex]
                ?? throw new ArgumentException(
                    "ASR result segments must not contain null elements.",
                    nameof(segments));
            if (!segmentIds.Add(segment.SegmentId))
            {
                throw new ArgumentException(
                    "ASR result segments must not contain duplicate IDs.",
                    nameof(segments));
            }

            if (previousSegmentStart is double priorSegmentStart &&
                segment.StartSeconds < priorSegmentStart)
            {
                throw new ArgumentException(
                    "ASR result segments must be ordered by raw start time.",
                    nameof(segments));
            }

            foreach (AsrWorkerToken token in segment.Tokens)
            {
                if (token.StartSeconds is not double tokenStart)
                {
                    continue;
                }

                if (previousTimedTokenStart is double priorTokenStart &&
                    tokenStart < priorTokenStart)
                {
                    throw new ArgumentException(
                        "Timed ASR tokens must be globally ordered by raw start time.",
                        nameof(segments));
                }

                previousTimedTokenStart = tokenStart;
            }

            copiedSegments[segmentIndex] = segment;
            previousSegmentStart = segment.StartSeconds;
        }

        ModelRepository = modelRepository;
        ModelRevision = modelRevision;
        ModelManifestDigest = modelManifestDigest;
        Configuration = configuration;
        InputAudioDigest = inputAudioDigest;
        InputAudioByteSize = inputAudioByteSize;
        InputAudioDurationSeconds = inputAudioDurationSeconds;
        Language = language;
        LanguageConfidence = languageConfidence;
        EngineDurationSeconds = engineDurationSeconds;
        Segments = Array.AsReadOnly(copiedSegments);
    }

    public string ModelRepository { get; }

    public string ModelRevision { get; }

    public ContentDigest ModelManifestDigest { get; }

    public AsrWorkerConfiguration Configuration { get; }

    public ContentDigest InputAudioDigest { get; }

    public long InputAudioByteSize { get; }

    public double InputAudioDurationSeconds { get; }

    public string Language { get; }

    public double LanguageConfidence { get; }

    public double EngineDurationSeconds { get; }

    public IReadOnlyList<AsrWorkerSegment> Segments { get; }
}

[Flags]
public enum TranscriptTimingReviewReason
{
    None = 0,
    WorkerReported = 1 << 0,
    MissingRawTiming = 1 << 1,
    NonPositiveRawDuration = 1 << 2,
    OutsideUtterance = 1 << 3,
    OutsideAnalysisAudio = 1 << 4,
    NonPositiveRoundedDuration = 1 << 5,
    OverlapsPreviousTimedToken = 1 << 6,
}

public sealed class TranscriptTimingEvidence
{
    private const TranscriptTimingReviewReason AllReasons =
        TranscriptTimingReviewReason.WorkerReported |
        TranscriptTimingReviewReason.MissingRawTiming |
        TranscriptTimingReviewReason.NonPositiveRawDuration |
        TranscriptTimingReviewReason.OutsideUtterance |
        TranscriptTimingReviewReason.OutsideAnalysisAudio |
        TranscriptTimingReviewReason.NonPositiveRoundedDuration |
        TranscriptTimingReviewReason.OverlapsPreviousTimedToken;

    internal TranscriptTimingEvidence(
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId,
        EntityId<TranscriptTokenIdTag>? tokenId,
        double? rawStartSeconds,
        double? rawEndSeconds,
        long? roundedStartSample,
        long? roundedEndSample,
        bool startWasRounded,
        bool endWasRounded,
        TranscriptTimingReviewReason reviewReasons)
    {
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        if (rawStartSeconds.HasValue != rawEndSeconds.HasValue ||
            roundedStartSample.HasValue != roundedEndSample.HasValue)
        {
            throw new ArgumentException(
                "Timing evidence boundaries must be paired.",
                nameof(rawStartSeconds));
        }

        if ((reviewReasons & ~AllReasons) != TranscriptTimingReviewReason.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reviewReasons),
                "Timing evidence contains an unknown review reason.");
        }

        if (roundedStartSample is null && (startWasRounded || endWasRounded))
        {
            throw new ArgumentException(
                "Unmapped timing evidence cannot claim sample rounding.",
                nameof(startWasRounded));
        }

        UtteranceId = utteranceId;
        SourceSpanId = sourceSpanId;
        TokenId = tokenId;
        RawStartSeconds = rawStartSeconds;
        RawEndSeconds = rawEndSeconds;
        RoundedStartSample = roundedStartSample;
        RoundedEndSample = roundedEndSample;
        StartWasRounded = startWasRounded;
        EndWasRounded = endWasRounded;
        ReviewReasons = reviewReasons;
    }

    public EntityId<TranscriptUtteranceIdTag> UtteranceId { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public EntityId<TranscriptTokenIdTag>? TokenId { get; }

    public double? RawStartSeconds { get; }

    public double? RawEndSeconds { get; }

    public long? RoundedStartSample { get; }

    public long? RoundedEndSample { get; }

    public bool StartWasRounded { get; }

    public bool EndWasRounded { get; }

    public TranscriptTimingReviewReason ReviewReasons { get; }

    public bool RequiresReview => ReviewReasons != TranscriptTimingReviewReason.None;
}

public sealed class TranscriptImportResult
{
    internal TranscriptImportResult(
        TranscriptRevision revision,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<TranscriptTimingEvidence> timingEvidence,
        AsrWorkerResult rawWorkerResult,
        AnalysisAudioTimeline audioTimeline)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentNullException.ThrowIfNull(timingEvidence);
        ArgumentNullException.ThrowIfNull(rawWorkerResult);
        ArgumentNullException.ThrowIfNull(audioTimeline);
        if (sourceSpans.Count != revision.Utterances.Count)
        {
            throw new ArgumentException(
                "Each transcript utterance must have one source span.",
                nameof(sourceSpans));
        }

        SourceSpan[] copiedSpans = new SourceSpan[sourceSpans.Count];
        HashSet<EntityId<SourceSpanIdTag>> spanIds = [];
        for (int index = 0; index < copiedSpans.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw new ArgumentException(
                    "Transcript source spans must not contain null elements.",
                    nameof(sourceSpans));
            Utterance utterance = revision.Utterances[index];
            if (!spanIds.Add(span.Id) ||
                span.Id != utterance.SourceSpanId ||
                span.AssetId != audioTimeline.SourceAssetId ||
                span.SourceDigest != audioTimeline.SourceContentDigest ||
                span.TranscriptRevisionId != revision.Id ||
                !span.TranscriptTokenIds.SequenceEqual(
                    utterance.Tokens.Select(static token => token.Id)))
            {
                throw new ArgumentException(
                    "Transcript source-span evidence is inconsistent.",
                    nameof(sourceSpans));
            }

            copiedSpans[index] = span;
        }

        TranscriptTimingEvidence[] copiedTiming =
            new TranscriptTimingEvidence[timingEvidence.Count];
        for (int index = 0; index < copiedTiming.Length; index++)
        {
            copiedTiming[index] = timingEvidence[index]
                ?? throw new ArgumentException(
                    "Transcript timing evidence must not contain null elements.",
                    nameof(timingEvidence));
        }

        Revision = revision;
        SourceSpans = Array.AsReadOnly(copiedSpans);
        TimingEvidence = Array.AsReadOnly(copiedTiming);
        RawWorkerResult = rawWorkerResult;
        AudioTimeline = audioTimeline;
    }

    public TranscriptRevision Revision { get; }

    public IReadOnlyList<SourceSpan> SourceSpans { get; }

    public IReadOnlyList<TranscriptTimingEvidence> TimingEvidence { get; }

    public AsrWorkerResult RawWorkerResult { get; }

    public AnalysisAudioTimeline AudioTimeline { get; }
}

public enum TranscriptImportFailureReason
{
    WorkerIdentityMismatch,
    InputBindingMismatch,
    InvalidTiming,
    InvalidWorkerResult,
    ArithmeticOverflow,
}

public sealed class TranscriptImportException : Exception
{
    internal TranscriptImportException(TranscriptImportFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public TranscriptImportFailureReason Reason { get; }

    private static string GetStableMessage(TranscriptImportFailureReason reason) =>
        reason switch
        {
            TranscriptImportFailureReason.WorkerIdentityMismatch =>
                "The ASR worker identity or configuration is not approved.",
            TranscriptImportFailureReason.InputBindingMismatch =>
                "The ASR result does not match the extracted analysis audio.",
            TranscriptImportFailureReason.InvalidTiming =>
                "The ASR timing cannot be normalized without changing its evidence.",
            TranscriptImportFailureReason.InvalidWorkerResult =>
                "The ASR result cannot be represented as a transcript revision.",
            TranscriptImportFailureReason.ArithmeticOverflow =>
                "The ASR timing exceeds the supported rational range.",
            _ => "The ASR result could not be imported.",
        };
}

public static class TranscriptImporter
{
    public const string AnonymousSpeakerLabel = "Speaker 1";

    private const string PinnedModelRepository = "Systran/faster-whisper-small";
    private const string PinnedModelRevision =
        "536b0662742c02347bc0e980a01041f333bce120";
    private const string PinnedModelManifestSha256 =
        "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f";

    private static readonly ContentDigest PinnedModelManifestDigest =
        new(PinnedModelManifestSha256);

    public static RationalTimeRoundingMode TimingRoundingMode =>
        RationalTimeRoundingMode.NearestToEven;

    public static string TimingRoundingRule { get; } =
        "nearest-to-even-analysis-sample";

    public static TranscriptImportResult Import(
        AsrWorkerResult workerResult,
        AnalysisAudioTimeline audioTimeline,
        EntityId<TranscriptRevisionIdTag> revisionId,
        EntityId<TranscriptSpeakerIdTag> anonymousSpeakerId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(workerResult);
        ArgumentNullException.ThrowIfNull(audioTimeline);
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(anonymousSpeakerId);

        try
        {
            ValidatePinnedWorker(workerResult);
            ValidateInputBinding(workerResult, audioTimeline);

            var utterances = new List<Utterance>(workerResult.Segments.Count);
            var sourceSpans = new List<SourceSpan>(workerResult.Segments.Count);
            var timingEvidence = new List<TranscriptTimingEvidence>();
            string streamId = string.Concat(
                "audio:",
                audioTimeline.SourceAudioStreamIndex.ToString(
                    CultureInfo.InvariantCulture));

            foreach (AsrWorkerSegment segment in workerResult.Segments)
            {
                ImportSegment(
                    segment,
                    workerResult.InputAudioDurationSeconds,
                    audioTimeline,
                    revisionId,
                    anonymousSpeakerId,
                    streamId,
                    utterances,
                    sourceSpans,
                    timingEvidence);
            }

            var revision = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                createdAt,
                utterances);
            return new TranscriptImportResult(
                revision,
                sourceSpans,
                timingEvidence,
                workerResult,
                audioTimeline);
        }
        catch (TranscriptImportException)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw Failure(TranscriptImportFailureReason.ArithmeticOverflow);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(TranscriptImportFailureReason.InvalidTiming);
        }
        catch (ArgumentException)
        {
            throw Failure(TranscriptImportFailureReason.InvalidWorkerResult);
        }
    }

    private static void ImportSegment(
        AsrWorkerSegment segment,
        double inputDurationSeconds,
        AnalysisAudioTimeline audioTimeline,
        EntityId<TranscriptRevisionIdTag> revisionId,
        EntityId<TranscriptSpeakerIdTag> anonymousSpeakerId,
        string streamId,
        ICollection<Utterance> utterances,
        ICollection<SourceSpan> sourceSpans,
        ICollection<TranscriptTimingEvidence> timingEvidence)
    {
        if (segment.EndSeconds > inputDurationSeconds)
        {
            throw Failure(TranscriptImportFailureReason.InvalidTiming);
        }

        MappedBoundary segmentStart = MapBoundary(
            audioTimeline,
            segment.StartSeconds);
        MappedBoundary segmentEnd = MapBoundary(
            audioTimeline,
            segment.EndSeconds);
        if (segmentEnd.SampleIndex <= segmentStart.SampleIndex)
        {
            throw Failure(TranscriptImportFailureReason.InvalidTiming);
        }

        var utteranceId = EntityId<TranscriptUtteranceIdTag>.New();
        var sourceSpanId = EntityId<SourceSpanIdTag>.New();
        TimeRange utteranceRange = new(
            segmentStart.SourceTime,
            segmentEnd.SourceTime - segmentStart.SourceTime);
        var importedTokens = new List<Token>(segment.Tokens.Count);
        var tokenIds = new List<EntityId<TranscriptTokenIdTag>>(segment.Tokens.Count);
        RationalTime? previousTimedEnd = null;
        timingEvidence.Add(new TranscriptTimingEvidence(
            utteranceId,
            sourceSpanId,
            tokenId: null,
            segment.StartSeconds,
            segment.EndSeconds,
            segmentStart.SampleIndex,
            segmentEnd.SampleIndex,
            segmentStart.WasRounded,
            segmentEnd.WasRounded,
            TranscriptTimingReviewReason.None));

        foreach (AsrWorkerToken workerToken in segment.Tokens)
        {
            var tokenId = EntityId<TranscriptTokenIdTag>.New();
            ImportedToken imported = ImportToken(
                workerToken,
                segment,
                inputDurationSeconds,
                audioTimeline,
                utteranceRange,
                previousTimedEnd,
                tokenId,
                utteranceId,
                sourceSpanId);
            importedTokens.Add(imported.Token);
            tokenIds.Add(tokenId);
            timingEvidence.Add(imported.Evidence);
            if (imported.Token.Range is TimeRange timedRange)
            {
                previousTimedEnd = timedRange.End;
            }
        }

        var sentence = new Sentence(
            EntityId<TranscriptSentenceIdTag>.New(),
            NormalizeRecognizedText(segment.Text),
            importedTokens);
        var utterance = new Utterance(
            utteranceId,
            sourceSpanId,
            anonymousSpeakerId,
            AnonymousSpeakerLabel,
            NormalizeRecognizedText(segment.Text),
            utteranceRange,
            recognitionConfidence: null,
            [sentence]);
        var sourceSpan = new SourceSpan(
            sourceSpanId,
            audioTimeline.SourceAssetId,
            streamId,
            utteranceRange,
            audioTimeline.SourceContentDigest,
            revisionId,
            tokenIds);
        utterances.Add(utterance);
        sourceSpans.Add(sourceSpan);
    }

    private static ImportedToken ImportToken(
        AsrWorkerToken workerToken,
        AsrWorkerSegment segment,
        double inputDurationSeconds,
        AnalysisAudioTimeline audioTimeline,
        TimeRange utteranceRange,
        RationalTime? previousTimedEnd,
        EntityId<TranscriptTokenIdTag> tokenId,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId)
    {
        TranscriptTimingReviewReason reasons = workerToken.TimingRequiresReview
            ? TranscriptTimingReviewReason.WorkerReported
            : TranscriptTimingReviewReason.None;
        MappedBoundary? tokenStart = null;
        MappedBoundary? tokenEnd = null;

        if (workerToken.StartSeconds is not double rawStart ||
            workerToken.EndSeconds is not double rawEnd)
        {
            reasons |= TranscriptTimingReviewReason.MissingRawTiming;
        }
        else
        {
            if (rawEnd <= rawStart)
            {
                reasons |= TranscriptTimingReviewReason.NonPositiveRawDuration;
            }

            if (rawStart < segment.StartSeconds || rawEnd > segment.EndSeconds)
            {
                reasons |= TranscriptTimingReviewReason.OutsideUtterance;
            }

            if (rawStart > inputDurationSeconds || rawEnd > inputDurationSeconds)
            {
                reasons |= TranscriptTimingReviewReason.OutsideAnalysisAudio;
            }

            if ((reasons & TranscriptTimingReviewReason.OutsideAnalysisAudio) == 0)
            {
                tokenStart = MapBoundary(audioTimeline, rawStart);
                tokenEnd = MapBoundary(audioTimeline, rawEnd);
                if (tokenEnd.Value.SampleIndex <= tokenStart.Value.SampleIndex)
                {
                    reasons |= TranscriptTimingReviewReason.NonPositiveRoundedDuration;
                }
                else
                {
                    TimeRange mappedRange = new(
                        tokenStart.Value.SourceTime,
                        tokenEnd.Value.SourceTime - tokenStart.Value.SourceTime);
                    if (mappedRange.Start < utteranceRange.Start ||
                        mappedRange.End > utteranceRange.End)
                    {
                        reasons |= TranscriptTimingReviewReason.OutsideUtterance;
                    }

                    if (previousTimedEnd is RationalTime priorEnd &&
                        mappedRange.Start < priorEnd)
                    {
                        reasons |=
                            TranscriptTimingReviewReason.OverlapsPreviousTimedToken;
                    }
                }
            }
        }

        TimeRange? persistedRange = null;
        if (reasons == TranscriptTimingReviewReason.None &&
            tokenStart is MappedBoundary mappedStart &&
            tokenEnd is MappedBoundary mappedEnd)
        {
            persistedRange = new TimeRange(
                mappedStart.SourceTime,
                mappedEnd.SourceTime - mappedStart.SourceTime);
        }

        var token = new Token(
            tokenId,
            NormalizeRecognizedText(workerToken.Text),
            persistedRange,
            workerToken.RecognitionConfidence);
        var evidence = new TranscriptTimingEvidence(
            utteranceId,
            sourceSpanId,
            tokenId,
            workerToken.StartSeconds,
            workerToken.EndSeconds,
            tokenStart?.SampleIndex,
            tokenEnd?.SampleIndex,
            tokenStart?.WasRounded ?? false,
            tokenEnd?.WasRounded ?? false,
            reasons);
        return new ImportedToken(token, evidence);
    }

    private static MappedBoundary MapBoundary(
        AnalysisAudioTimeline audioTimeline,
        double seconds)
    {
        RationalTime sourceTime;
        long sampleIndex;
        try
        {
            sourceTime = audioTimeline.MapAsrSecondsToSourceTime(
                seconds,
                TimingRoundingMode);
            sampleIndex = audioTimeline.MapSourceTimeToAnalysisSample(
                sourceTime,
                TimingRoundingMode);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(TranscriptImportFailureReason.InvalidTiming);
        }

        double roundedSeconds =
            (double)sampleIndex / AnalysisAudioTimeline.FixedSampleRate;
        return new MappedBoundary(
            sampleIndex,
            sourceTime,
            roundedSeconds != seconds);
    }

    private static void ValidatePinnedWorker(AsrWorkerResult workerResult)
    {
        AsrWorkerConfiguration configuration = workerResult.Configuration;
        if (!string.Equals(
                workerResult.ModelRepository,
                PinnedModelRepository,
                StringComparison.Ordinal) ||
            !string.Equals(
                workerResult.ModelRevision,
                PinnedModelRevision,
                StringComparison.Ordinal) ||
            workerResult.ModelManifestDigest != PinnedModelManifestDigest ||
            !string.Equals(configuration.Device, "cpu", StringComparison.Ordinal) ||
            !string.Equals(configuration.ComputeType, "int8", StringComparison.Ordinal) ||
            configuration.Language is not ("ja" or LocalAnalysisWorker.AutoLanguagePolicy) ||
            !string.Equals(configuration.Task, "transcribe", StringComparison.Ordinal) ||
            configuration.BeamSize != 5 ||
            !configuration.WordTimestamps ||
            configuration.VadFilter ||
            !configuration.ConditionOnPreviousText ||
            (configuration.Language == "ja"
                ? !string.Equals(workerResult.Language, "ja", StringComparison.Ordinal)
                : !LocalAnalysisWorker.IsSupportedLanguage(workerResult.Language)))
        {
            throw Failure(TranscriptImportFailureReason.WorkerIdentityMismatch);
        }
    }

    private static void ValidateInputBinding(
        AsrWorkerResult workerResult,
        AnalysisAudioTimeline audioTimeline)
    {
        double expectedDuration =
            (double)audioTimeline.AnalysisSampleCount /
            AnalysisAudioTimeline.FixedSampleRate;
        if (workerResult.InputAudioDigest != audioTimeline.AnalysisAudioDigest ||
            workerResult.InputAudioByteSize != audioTimeline.AnalysisAudioByteSize ||
            workerResult.InputAudioDurationSeconds != expectedDuration)
        {
            throw Failure(TranscriptImportFailureReason.InputBindingMismatch);
        }

        MappedBoundary workerEnd = MapBoundary(
            audioTimeline,
            workerResult.InputAudioDurationSeconds);
        if (workerEnd.SampleIndex != audioTimeline.AnalysisSampleCount ||
            workerEnd.SourceTime != audioTimeline.MapAnalysisSampleToSourceTime(
                audioTimeline.AnalysisSampleCount))
        {
            throw Failure(TranscriptImportFailureReason.InputBindingMismatch);
        }
    }

    private static string NormalizeRecognizedText(string value) => value.Trim();

    private static TranscriptImportException Failure(
        TranscriptImportFailureReason reason) => new(reason);

    private readonly record struct MappedBoundary(
        long SampleIndex,
        RationalTime SourceTime,
        bool WasRounded);

    private sealed record ImportedToken(
        Token Token,
        TranscriptTimingEvidence Evidence);
}

internal static class AsrWorkerValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void ValidateMetadataText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ASR metadata must be nonblank and have no surrounding whitespace.",
                parameterName);
        }

        ValidateUnicode(value, parameterName);
    }

    internal static void ValidateRecognizedText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "ASR recognized text must not be blank.",
                parameterName);
        }

        ValidateUnicode(value, parameterName);
    }

    internal static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "ASR numeric evidence must be finite.");
        }
    }

    internal static void ValidateNonnegativeFinite(double value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value < 0d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "ASR numeric evidence must not be negative.");
        }
    }

    internal static void ValidatePositiveFinite(double value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "ASR numeric evidence must be positive.");
        }
    }

    internal static void ValidateConfidence(double value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value < 0d || value > 1d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "ASR confidence evidence must be between zero and one.");
        }
    }

    private static void ValidateUnicode(string value, string parameterName)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "ASR text must be valid Unicode.",
                parameterName,
                exception);
        }
    }
}