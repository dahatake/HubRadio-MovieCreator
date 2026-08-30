using System.Reflection;
using HubRadio.App.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TranscriptImporterTests
{
    private const string ModelRepository = "Systran/faster-whisper-small";
    private const string ModelRevision =
        "536b0662742c02347bc0e980a01041f333bce120";
    private const string ModelManifestSha256 =
        "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f";

    private static readonly DateTimeOffset CreatedAt =
        new(2037, 8, 9, 10, 11, 12, TimeSpan.Zero);

    [TestMethod]
    public void Import_MapsRawSecondsFromFirstDecodedFrameAndBuildsProvenance()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            firstDecodedTimestamp: -2112,
            sourceAudioTimeBase: new RationalTime(1, 48000),
            sampleCount: 32000);
        AsrWorkerToken[] workerTokens =
        [
            new(" こんにちは", 0.1, 0.2, 0.9, timingRequiresReview: false),
            new("世界 ", 0.2, 0.6, 0.8, timingRequiresReview: false),
        ];
        AsrWorkerSegment segment = new(
            segmentId: 7,
            seek: 160,
            text: " こんにちは 世界 ",
            startSeconds: 0.1,
            endSeconds: 0.6,
            averageLogProbability: -0.25,
            noSpeechProbability: 0.01,
            workerTokens);
        AsrWorkerResult workerResult = CreateWorkerResult(timeline, [segment]);
        var revisionId = EntityId<TranscriptRevisionIdTag>.New();
        var speakerId = EntityId<TranscriptSpeakerIdTag>.New();

        TranscriptImportResult result = TranscriptImporter.Import(
            workerResult,
            timeline,
            revisionId,
            speakerId,
            CreatedAt);

        Assert.AreSame(workerResult, result.RawWorkerResult);
        Assert.AreSame(timeline, result.AudioTimeline);
        Assert.AreEqual(revisionId, result.Revision.Id);
        Assert.IsNull(result.Revision.ParentRevisionId);
        Assert.AreEqual(TranscriptChangeSource.Machine, result.Revision.ChangeSource);
        Assert.AreEqual(CreatedAt, result.Revision.CreatedAt);
        Assert.HasCount(1, result.Revision.Utterances);

        Utterance utterance = result.Revision.Utterances[0];
        Assert.AreEqual(speakerId, utterance.SpeakerId);
        Assert.AreEqual(TranscriptImporter.AnonymousSpeakerLabel, utterance.SpeakerLabel);
        Assert.AreEqual("こんにちは 世界", utterance.Text);
        Assert.AreEqual(new RationalTime(7, 125), utterance.Range.Start);
        Assert.AreEqual(new RationalTime(1, 2), utterance.Range.Duration);
        Assert.IsNull(utterance.RecognitionConfidence);
        Assert.HasCount(1, utterance.Sentences);
        Assert.AreEqual("こんにちは 世界", utterance.Sentences[0].Text);
        Assert.HasCount(2, utterance.Tokens);
        Assert.AreEqual("こんにちは", utterance.Tokens[0].Text);
        Assert.AreEqual(new RationalTime(7, 125), utterance.Tokens[0].Range?.Start);
        Assert.AreEqual(new RationalTime(1, 10), utterance.Tokens[0].Range?.Duration);
        Assert.AreEqual(0.9, utterance.Tokens[0].RecognitionConfidence);
        Assert.AreEqual("世界", utterance.Tokens[1].Text);
        Assert.AreEqual(new RationalTime(39, 250), utterance.Tokens[1].Range?.Start);
        Assert.AreEqual(new RationalTime(2, 5), utterance.Tokens[1].Range?.Duration);

        Assert.HasCount(1, result.SourceSpans);
        SourceSpan sourceSpan = result.SourceSpans[0];
        Assert.AreEqual(utterance.SourceSpanId, sourceSpan.Id);
        Assert.AreEqual(timeline.SourceAssetId, sourceSpan.AssetId);
        Assert.AreEqual("audio:1", sourceSpan.StreamId);
        Assert.AreEqual(utterance.Range, sourceSpan.Range);
        Assert.AreEqual(timeline.SourceContentDigest, sourceSpan.SourceDigest);
        Assert.AreEqual(revisionId, sourceSpan.TranscriptRevisionId);
        CollectionAssert.AreEqual(
            utterance.Tokens.Select(static token => token.Id).ToArray(),
            sourceSpan.TranscriptTokenIds.ToArray());

        Assert.HasCount(3, result.TimingEvidence);
        TranscriptTimingEvidence segmentEvidence = result.TimingEvidence[0];
        Assert.IsNull(segmentEvidence.TokenId);
        Assert.AreEqual(1600L, segmentEvidence.RoundedStartSample);
        Assert.AreEqual(9600L, segmentEvidence.RoundedEndSample);
        Assert.IsFalse(segmentEvidence.RequiresReview);
        AssertRawDouble(0.1, segmentEvidence.RawStartSeconds);
        AssertRawDouble(0.6, segmentEvidence.RawEndSeconds);

        TranscriptTimingEvidence tokenEvidence = result.TimingEvidence[1];
        Assert.AreEqual(utterance.Tokens[0].Id, tokenEvidence.TokenId);
        Assert.AreEqual(1600L, tokenEvidence.RoundedStartSample);
        Assert.AreEqual(3200L, tokenEvidence.RoundedEndSample);
        Assert.IsFalse(tokenEvidence.StartWasRounded);
        Assert.IsFalse(tokenEvidence.EndWasRounded);
        Assert.IsFalse(tokenEvidence.RequiresReview);
        Assert.AreEqual(7, result.RawWorkerResult.Segments[0].SegmentId);
        Assert.AreEqual(160, result.RawWorkerResult.Segments[0].Seek);
        Assert.AreEqual(-0.25, result.RawWorkerResult.Segments[0].AverageLogProbability);
        Assert.AreEqual(0.01, result.RawWorkerResult.Segments[0].NoSpeechProbability);
    }

    [TestMethod]
    public void Import_UsesExplicitNearestToEvenAndPreservesLossyRawEvidence()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            firstDecodedTimestamp: 48000,
            sourceAudioTimeBase: new RationalTime(1, 48000),
            sampleCount: 16);
        double halfSample = 0.5 / AnalysisAudioTimeline.FixedSampleRate;
        double oneAndHalfSamples = 1.5 / AnalysisAudioTimeline.FixedSampleRate;
        AsrWorkerSegment segment = new(
            segmentId: 0,
            seek: 0,
            text: "tie",
            startSeconds: 0d,
            endSeconds: 2d / AnalysisAudioTimeline.FixedSampleRate,
            averageLogProbability: -0.1,
            noSpeechProbability: 0,
            [new AsrWorkerToken(
                "tie",
                halfSample,
                oneAndHalfSamples,
                1,
                timingRequiresReview: false)]);

        TranscriptImportResult result = TranscriptImporter.Import(
            CreateWorkerResult(timeline, [segment]),
            timeline,
            EntityId<TranscriptRevisionIdTag>.New(),
            EntityId<TranscriptSpeakerIdTag>.New(),
            CreatedAt);

        Assert.AreEqual(
            RationalTimeRoundingMode.NearestToEven,
            TranscriptImporter.TimingRoundingMode);
        Assert.AreEqual(
            "nearest-to-even-analysis-sample",
            TranscriptImporter.TimingRoundingRule);
        Token token = result.Revision.Utterances[0].Tokens[0];
        Assert.AreEqual(new RationalTime(1, 1), token.Range?.Start);
        Assert.AreEqual(new RationalTime(1, 8000), token.Range?.Duration);

        TranscriptTimingEvidence evidence = result.TimingEvidence[1];
        AssertRawDouble(halfSample, evidence.RawStartSeconds);
        AssertRawDouble(oneAndHalfSamples, evidence.RawEndSeconds);
        Assert.AreEqual(0L, evidence.RoundedStartSample);
        Assert.AreEqual(2L, evidence.RoundedEndSample);
        Assert.IsTrue(evidence.StartWasRounded);
        Assert.IsTrue(evidence.EndWasRounded);
        Assert.IsFalse(evidence.RequiresReview);
    }

    [TestMethod]
    public void Import_PreservesDelayedFirstDecodedFrameAsSourceTimeOrigin()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            firstDecodedTimestamp: 48000,
            sourceAudioTimeBase: new RationalTime(1, 48000),
            sampleCount: 16000);
        AsrWorkerSegment segment = new(
            segmentId: 0,
            seek: 0,
            text: "delayed",
            startSeconds: 0,
            endSeconds: 0.5,
            averageLogProbability: -0.1,
            noSpeechProbability: 0,
            [new AsrWorkerToken(
                "delayed",
                0,
                0.5,
                1,
                timingRequiresReview: false)]);

        TranscriptImportResult result = TranscriptImporter.Import(
            CreateWorkerResult(timeline, [segment]),
            timeline,
            EntityId<TranscriptRevisionIdTag>.New(),
            EntityId<TranscriptSpeakerIdTag>.New(),
            CreatedAt);

        Utterance utterance = result.Revision.Utterances[0];
        Assert.AreEqual(new RationalTime(1, 1), utterance.Range.Start);
        Assert.AreEqual(new RationalTime(1, 2), utterance.Range.Duration);
        Assert.AreEqual(new RationalTime(1, 1), utterance.Tokens[0].Range?.Start);
        Assert.AreEqual(new RationalTime(3, 2), utterance.Tokens[0].Range?.End);
        Assert.AreEqual(utterance.Range, result.SourceSpans[0].Range);
    }

    [TestMethod]
    public void Import_ReviewTokensKeepRawEvidenceWithoutInventingCanonicalRanges()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 320);
        double subSampleEnd = 0.005 + (0.25 / AnalysisAudioTimeline.FixedSampleRate);
        AsrWorkerToken[] workerTokens =
        [
            new("normal", 0, 0.002, 1, timingRequiresReview: false),
            new("overlap", 0.001, 0.003, 0.9, timingRequiresReview: false),
            new("zero", 0.004, 0.004, 0.8, timingRequiresReview: true),
            new("rounded", 0.005, subSampleEnd, 0.7, timingRequiresReview: false),
            new("missing", null, null, 0.6, timingRequiresReview: true),
            new("outside", 0.011, 0.012, 0.5, timingRequiresReview: true),
        ];
        AsrWorkerSegment segment = new(
            segmentId: 0,
            seek: 0,
            text: "review evidence",
            startSeconds: 0,
            endSeconds: 0.01,
            averageLogProbability: -0.2,
            noSpeechProbability: 0.1,
            workerTokens);

        TranscriptImportResult result = TranscriptImporter.Import(
            CreateWorkerResult(timeline, [segment]),
            timeline,
            EntityId<TranscriptRevisionIdTag>.New(),
            EntityId<TranscriptSpeakerIdTag>.New(),
            CreatedAt);

        IReadOnlyList<Token> tokens = result.Revision.Utterances[0].Tokens;
        Assert.IsNotNull(tokens[0].Range);
        for (int index = 1; index < tokens.Count; index++)
        {
            Assert.IsNull(tokens[index].Range, tokens[index].Text);
        }

        IReadOnlyList<TranscriptTimingEvidence> evidence = result.TimingEvidence;
        Assert.AreEqual(
            TranscriptTimingReviewReason.OverlapsPreviousTimedToken,
            evidence[2].ReviewReasons);
        Assert.AreEqual(
            TranscriptTimingReviewReason.WorkerReported |
            TranscriptTimingReviewReason.NonPositiveRawDuration |
            TranscriptTimingReviewReason.NonPositiveRoundedDuration,
            evidence[3].ReviewReasons);
        Assert.AreEqual(
            TranscriptTimingReviewReason.NonPositiveRoundedDuration,
            evidence[4].ReviewReasons);
        Assert.AreEqual(
            TranscriptTimingReviewReason.WorkerReported |
            TranscriptTimingReviewReason.MissingRawTiming,
            evidence[5].ReviewReasons);
        Assert.AreEqual(
            TranscriptTimingReviewReason.WorkerReported |
            TranscriptTimingReviewReason.OutsideUtterance,
            evidence[6].ReviewReasons);
        Assert.IsNull(evidence[5].RawStartSeconds);
        Assert.IsNull(evidence[5].RoundedStartSample);
        AssertRawDouble(subSampleEnd, evidence[4].RawEndSeconds);
        CollectionAssert.AreEqual(
            tokens.Select(static token => token.Id).ToArray(),
            result.SourceSpans[0].TranscriptTokenIds.ToArray());
    }

    [TestMethod]
    public void Import_OutOfAudioReviewTokenIsNotClampedOrMapped()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 160);
        AsrWorkerSegment segment = new(
            segmentId: 0,
            seek: 0,
            text: "outside",
            startSeconds: 0,
            endSeconds: 0.01,
            averageLogProbability: -0.2,
            noSpeechProbability: 0.1,
            [new AsrWorkerToken(
                "outside",
                0.009,
                0.011,
                0.5,
                timingRequiresReview: true)]);

        TranscriptImportResult result = TranscriptImporter.Import(
            CreateWorkerResult(timeline, [segment]),
            timeline,
            EntityId<TranscriptRevisionIdTag>.New(),
            EntityId<TranscriptSpeakerIdTag>.New(),
            CreatedAt);

        Assert.IsNull(result.Revision.Utterances[0].Tokens[0].Range);
        TranscriptTimingEvidence evidence = result.TimingEvidence[1];
        Assert.AreEqual(
            TranscriptTimingReviewReason.WorkerReported |
            TranscriptTimingReviewReason.OutsideUtterance |
            TranscriptTimingReviewReason.OutsideAnalysisAudio,
            evidence.ReviewReasons);
        AssertRawDouble(0.009, evidence.RawStartSeconds);
        AssertRawDouble(0.011, evidence.RawEndSeconds);
        Assert.IsNull(evidence.RoundedStartSample);
        Assert.IsNull(evidence.RoundedEndSample);
    }

    [TestMethod]
    public void Import_SilenceProducesAnEmptyMachineRevision()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 16000);
        AsrWorkerResult workerResult = CreateWorkerResult(
            timeline,
            Array.Empty<AsrWorkerSegment>());

        TranscriptImportResult result = TranscriptImporter.Import(
            workerResult,
            timeline,
            EntityId<TranscriptRevisionIdTag>.New(),
            EntityId<TranscriptSpeakerIdTag>.New(),
            CreatedAt);

        Assert.IsEmpty(result.Revision.Utterances);
        Assert.IsEmpty(result.SourceSpans);
        Assert.IsEmpty(result.TimingEvidence);
        Assert.AreEqual(TranscriptChangeSource.Machine, result.Revision.ChangeSource);
    }

    [TestMethod]
    public void Import_RejectsEveryInputAudioBindingMismatch()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 16000);
        AsrWorkerResult[] invalidResults =
        [
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                inputDigest: new ContentDigest(new string('d', 64))),
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                inputByteSize: timeline.AnalysisAudioByteSize + 1),
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                inputDurationSeconds: 0.5),
        ];

        foreach (AsrWorkerResult invalid in invalidResults)
        {
            TranscriptImportException exception =
                Assert.ThrowsExactly<TranscriptImportException>(() =>
                    TranscriptImporter.Import(
                        invalid,
                        timeline,
                        EntityId<TranscriptRevisionIdTag>.New(),
                        EntityId<TranscriptSpeakerIdTag>.New(),
                        CreatedAt));
            AssertSafeFailure(
                exception,
                TranscriptImportFailureReason.InputBindingMismatch);
        }
    }

    [TestMethod]
    public void Import_RejectsUnapprovedModelOrWorkerConfiguration()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 16000);
        AsrWorkerConfiguration wrongConfiguration = new(
            device: "cuda",
            computeType: "int8",
            language: "ja",
            task: "transcribe",
            beamSize: 5,
            wordTimestamps: true,
            vadFilter: false,
            conditionOnPreviousText: true);
        AsrWorkerResult[] invalidResults =
        [
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                modelRepository: "private/model"),
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                modelRevision: new string('0', 40)),
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                modelManifestDigest: new ContentDigest(new string('d', 64))),
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                configuration: wrongConfiguration),
            CreateWorkerResult(
                timeline,
                Array.Empty<AsrWorkerSegment>(),
                language: "en"),
        ];

        foreach (AsrWorkerResult invalid in invalidResults)
        {
            TranscriptImportException exception =
                Assert.ThrowsExactly<TranscriptImportException>(() =>
                    TranscriptImporter.Import(
                        invalid,
                        timeline,
                        EntityId<TranscriptRevisionIdTag>.New(),
                        EntityId<TranscriptSpeakerIdTag>.New(),
                        CreatedAt));
            AssertSafeFailure(
                exception,
                TranscriptImportFailureReason.WorkerIdentityMismatch);
        }
    }

    [TestMethod]
    public void Import_SegmentOutsideAudioFailsClosedWithoutLeakingPayload()
    {
        const string privatePayload = @"Bearer secret-token C:\Users\Alice\customer.wav S-1-5-21";
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 16000);
        AsrWorkerSegment segment = new(
            segmentId: 0,
            seek: 0,
            text: privatePayload,
            startSeconds: 0.9,
            endSeconds: 1.1,
            averageLogProbability: -0.2,
            noSpeechProbability: 0.1,
            [new AsrWorkerToken(
                privatePayload,
                0.9,
                1.1,
                0.5,
                timingRequiresReview: false)]);

        TranscriptImportException exception =
            Assert.ThrowsExactly<TranscriptImportException>(() =>
                TranscriptImporter.Import(
                    CreateWorkerResult(timeline, [segment]),
                    timeline,
                    EntityId<TranscriptRevisionIdTag>.New(),
                    EntityId<TranscriptSpeakerIdTag>.New(),
                    CreatedAt));

        AssertSafeFailure(exception, TranscriptImportFailureReason.InvalidTiming);
        Assert.IsFalse(exception.ToString().Contains(
            privatePayload,
            StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(
            "Alice",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(
            "S-1-5-21",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void WorkerContractsRejectMalformedNumbersOrderingAndReviewClaims()
    {
        foreach (double invalid in new[]
        {
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            -double.Epsilon,
        })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new AsrWorkerToken(
                    "token",
                    invalid,
                    1,
                    0.5,
                    timingRequiresReview: true));
        }

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AsrWorkerToken(
                "token",
                null,
                1,
                0.5,
                timingRequiresReview: true));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AsrWorkerToken(
                "token",
                null,
                null,
                0.5,
                timingRequiresReview: false));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AsrWorkerToken(
                "token",
                1,
                1,
                0.5,
                timingRequiresReview: false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AsrWorkerToken(
                "token",
                0,
                1,
                1.01,
                timingRequiresReview: false));

        AsrWorkerToken outsideWithoutReview = new(
            "outside",
            0,
            2,
            0.5,
            timingRequiresReview: false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            CreateSegment(0, 0, 1, [outsideWithoutReview]));

        AsrWorkerSegment later = CreateSegment(
            segmentId: 1,
            start: 1,
            end: 2,
            [new AsrWorkerToken("later", 1, 2, 0, false)]);
        AsrWorkerSegment earlier = CreateSegment(
            segmentId: 2,
            start: 0,
            end: 1,
            [new AsrWorkerToken("earlier", 0, 1, 1, false)]);
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 48000);
        Assert.ThrowsExactly<ArgumentException>(() =>
            CreateWorkerResult(timeline, [later, earlier]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CreateWorkerResult(timeline, [earlier, earlier]));
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndFailuresHaveStableMessages()
    {
        Type[] immutableTypes =
        [
            typeof(AsrWorkerConfiguration),
            typeof(AsrWorkerToken),
            typeof(AsrWorkerSegment),
            typeof(AsrWorkerResult),
            typeof(TranscriptTimingEvidence),
            typeof(TranscriptImportResult),
        ];
        foreach (Type type in immutableTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(TranscriptTimingEvidence).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptImportResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptImportException).GetConstructors().Length);
        Assert.IsTrue(typeof(TranscriptImporter).IsAbstract);
        Assert.IsTrue(typeof(TranscriptImporter).IsSealed);

        foreach (TranscriptImportFailureReason reason in
            Enum.GetValues<TranscriptImportFailureReason>())
        {
            ConstructorInfo constructor = typeof(TranscriptImportException)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();
            var exception = (TranscriptImportException)constructor.Invoke([reason]);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.Message.Contains(
                "path",
                StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(exception.Message.Contains(
                "token",
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static AnalysisAudioTimeline CreateTimeline(
        long firstDecodedTimestamp = 0,
        RationalTime? sourceAudioTimeBase = null,
        long sampleCount = 16000)
    {
        return new AnalysisAudioTimeline(
            EntityId<ProjectIdTag>.New(),
            EntityId<JobIdTag>.New(),
            EntityId<AssetIdTag>.New(),
            new ContentDigest(new string('a', 64)),
            sourceByteSize: 100,
            EntityId<ArtifactIdTag>.New(),
            new ContentDigest(new string('b', 64)),
            sourceVideoStreamIndex: 0,
            sourceAudioStreamIndex: 1,
            sourceAudioTimeBase ?? new RationalTime(1, 48000),
            probeReportedAudioStart: new RationalTime(-11, 250),
            firstDecodedTimestamp,
            firstDecodedAudioFrameSampleCount: 1024,
            EntityId<ArtifactIdTag>.New(),
            new ContentDigest(new string('c', 64)),
            analysisAudioByteSize: checked(44 + (sampleCount * sizeof(short))),
            sampleCount,
            EntityId<ArtifactIdTag>.New());
    }

    private static AsrWorkerResult CreateWorkerResult(
        AnalysisAudioTimeline timeline,
        IReadOnlyList<AsrWorkerSegment> segments,
        string modelRepository = ModelRepository,
        string modelRevision = ModelRevision,
        ContentDigest? modelManifestDigest = null,
        AsrWorkerConfiguration? configuration = null,
        ContentDigest? inputDigest = null,
        long? inputByteSize = null,
        double? inputDurationSeconds = null,
        string language = "ja")
    {
        return new AsrWorkerResult(
            modelRepository,
            modelRevision,
            modelManifestDigest ?? new ContentDigest(ModelManifestSha256),
            configuration ?? CreateConfiguration(),
            inputDigest ?? timeline.AnalysisAudioDigest,
            inputByteSize ?? timeline.AnalysisAudioByteSize,
            inputDurationSeconds ??
                (double)timeline.AnalysisSampleCount /
                AnalysisAudioTimeline.FixedSampleRate,
            language,
            languageConfidence: 0.99,
            engineDurationSeconds: 0.5,
            segments);
    }

    private static AsrWorkerConfiguration CreateConfiguration() =>
        new(
            device: "cpu",
            computeType: "int8",
            language: "ja",
            task: "transcribe",
            beamSize: 5,
            wordTimestamps: true,
            vadFilter: false,
            conditionOnPreviousText: true);

    private static AsrWorkerSegment CreateSegment(
        int segmentId,
        double start,
        double end,
        IReadOnlyList<AsrWorkerToken> tokens) =>
        new(
            segmentId,
            seek: 0,
            text: "segment",
            start,
            end,
            averageLogProbability: -0.1,
            noSpeechProbability: 0,
            tokens);

    private static void AssertRawDouble(double expected, double? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(expected),
            BitConverter.DoubleToInt64Bits(actual.Value));
    }

    private static void AssertSafeFailure(
        TranscriptImportException exception,
        TranscriptImportFailureReason expectedReason)
    {
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\Users\Alice",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(
            "secret-token",
            StringComparison.OrdinalIgnoreCase));
    }
}