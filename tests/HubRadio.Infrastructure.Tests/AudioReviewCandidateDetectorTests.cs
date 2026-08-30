using System.Buffers.Binary;
using System.Reflection;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AudioReviewCandidateDetectorTests
{
    [TestMethod]
    public void ConfigurationIsFixedReviewOnlyAndExcludesHeldModels()
    {
        AudioReviewConfiguration configuration =
            AudioReviewCandidateDetector.Configuration;

        Assert.AreEqual(
            "stage0-2026-08-27.audio-review.v1",
            configuration.RuleVersion);
        Assert.AreEqual(16000, configuration.AnalysisSampleRate);
        Assert.AreEqual(100, configuration.WindowMilliseconds);
        Assert.AreEqual(1600, configuration.WindowSamples);
        Assert.AreEqual(32000, configuration.LowEnergyMinimumSamples);
        Assert.AreEqual(
            "mean-square-pcm16-times-100000-less-than-or-equal-full-scale-square",
            configuration.LowEnergyPowerRule);
        Assert.AreEqual(32736, configuration.ClippingMagnitude);
        Assert.AreEqual(3, configuration.ClippingMinimumSamplesPerWindow);
        Assert.IsFalse(configuration.ProductionThresholdsApproved);
        Assert.IsFalse(configuration.AutomaticDeletionAllowed);
        Assert.IsFalse(configuration.YamnetAdopted);
        Assert.IsFalse(configuration.SileroVadAdopted);
    }

    [TestMethod]
    public void AnalyzeWaveformFindsExactLowEnergyAndClippingWindows()
    {
        short[] samples = Enumerable.Repeat((short)10000, 64000).ToArray();
        Array.Clear(samples, 16000, 32000);
        samples[48000] = short.MaxValue;
        samples[48001] = short.MinValue;
        samples[48002] = short.MaxValue;
        byte[] wave = BuildWave(samples);
        AnalysisAudioTimeline timeline = CreateTimeline(wave, samples.Length);

        IReadOnlyList<AudioReviewObservation> observations =
            AudioReviewCandidateDetector.AnalyzeWaveform(timeline, wave);

        Assert.HasCount(2, observations);
        AudioReviewObservation lowEnergy = observations[0];
        Assert.AreEqual(AudioReviewEventKind.Wait, lowEnergy.Kind);
        Assert.AreEqual(
            AudioReviewEvidenceMethod.WaveformLowEnergy,
            lowEnergy.Method);
        Assert.AreEqual(16000L, lowEnergy.AnalysisStartSample);
        Assert.AreEqual(48000L, lowEnergy.AnalysisEndSample);
        Assert.IsNull(lowEnergy.ObservedMetric);
        Assert.IsFalse(lowEnergy.ManualListeningConfirmed);
        AudioReviewObservation clipping = observations[1];
        Assert.AreEqual(AudioReviewEventKind.RecordingQuality, clipping.Kind);
        Assert.AreEqual(
            AudioReviewEvidenceMethod.WaveformClipping,
            clipping.Method);
        Assert.AreEqual(48000L, clipping.AnalysisStartSample);
        Assert.AreEqual(49600L, clipping.AnalysisEndSample);
    }

    [TestMethod]
    public void AnalyzeWaveformUsesExactIntegerLowEnergyBoundary()
    {
        short[] atBoundary = Enumerable.Repeat((short)103, 32000).ToArray();
        short[] aboveBoundary = Enumerable.Repeat((short)104, 32000).ToArray();
        byte[] acceptedWave = BuildWave(atBoundary);
        byte[] rejectedWave = BuildWave(aboveBoundary);

        IReadOnlyList<AudioReviewObservation> accepted =
            AudioReviewCandidateDetector.AnalyzeWaveform(
                CreateTimeline(acceptedWave, atBoundary.Length),
                acceptedWave);
        IReadOnlyList<AudioReviewObservation> rejected =
            AudioReviewCandidateDetector.AnalyzeWaveform(
                CreateTimeline(rejectedWave, aboveBoundary.Length),
                rejectedWave);

        Assert.HasCount(1, accepted);
        Assert.AreEqual(
            AudioReviewEvidenceMethod.WaveformLowEnergy,
            accepted[0].Method);
        Assert.HasCount(0, rejected);
    }

    [TestMethod]
    public void AnalyzeWaveformRejectsDigestSizeFormatAndSampleCountMismatch()
    {
        short[] samples = new short[32000];
        byte[] wave = BuildWave(samples);
        AnalysisAudioTimeline timeline = CreateTimeline(wave, samples.Length);
        byte[] changed = wave.ToArray();
        changed[^1] = 1;
        AssertFailure(
            () => AudioReviewCandidateDetector.AnalyzeWaveform(timeline, changed),
            AudioReviewFailureReason.AnalysisAudioBindingMismatch);
        AssertFailure(
            () => AudioReviewCandidateDetector.AnalyzeWaveform(
                timeline,
                wave.AsMemory(0, wave.Length - 2)),
            AudioReviewFailureReason.AnalysisAudioBindingMismatch);

        byte[] malformed = wave.ToArray();
        malformed[20] = 2;
        AnalysisAudioTimeline malformedTimeline =
            CreateTimeline(malformed, samples.Length);
        AssertFailure(
            () => AudioReviewCandidateDetector.AnalyzeWaveform(
                malformedTimeline,
                malformed),
            AudioReviewFailureReason.WaveformMalformed);

        AnalysisAudioTimeline wrongCount = CreateTimeline(
            wave,
            samples.Length - 1);
        AssertFailure(
            () => AudioReviewCandidateDetector.AnalyzeWaveform(
                wrongCount,
                wave),
            AudioReviewFailureReason.WaveformMalformed);
    }

    [TestMethod]
    public void CreateCandidatesRequiresDeterministicWaitOverlap()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            BuildWave(new short[64000]),
            64000);
        var lowEnergy = new AudioReviewObservation(
            AudioReviewEventKind.Wait,
            AudioReviewEvidenceMethod.WaveformLowEnergy,
            16000,
            48000);

        AudioReviewDetectionResult unsupportedSingle =
            AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                EntityId<ModelRunIdTag>.New(),
                [lowEnergy]);
        AudioReviewDetectionResult nonoverlapping =
            AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                EntityId<ModelRunIdTag>.New(),
                [
                    lowEnergy,
                    new AudioReviewObservation(
                        AudioReviewEventKind.Wait,
                        AudioReviewEvidenceMethod.FfmpegSilence,
                        48000,
                        64000),
                ]);

        Assert.HasCount(0, unsupportedSingle.Candidates);
        Assert.HasCount(0, nonoverlapping.Candidates);
    }

    [TestMethod]
    public void CreateCandidatesMapsConsensusManualEventsAndTechnicalWarningToSource()
    {
        short[] samples = new short[64000];
        byte[] wave = BuildWave(samples);
        AnalysisAudioTimeline timeline = CreateTimeline(
            wave,
            samples.Length,
            firstDecodedTimestamp: -2112);
        EntityId<ModelRunIdTag> modelRunId = EntityId<ModelRunIdTag>.New();
        AudioReviewObservation[] observations =
        [
            new(
                AudioReviewEventKind.Wait,
                AudioReviewEvidenceMethod.WaveformLowEnergy,
                16000,
                48000),
            new(
                AudioReviewEventKind.Wait,
                AudioReviewEvidenceMethod.FfmpegSilence,
                20000,
                50000),
            new(
                AudioReviewEventKind.Wait,
                AudioReviewEvidenceMethod.ManualListening,
                20000,
                48000),
            new(
                AudioReviewEventKind.RecordingQuality,
                AudioReviewEvidenceMethod.WaveformClipping,
                48000,
                49600),
            new(
                AudioReviewEventKind.Cough,
                AudioReviewEvidenceMethod.ManualListening,
                50000,
                52000),
            new(
                AudioReviewEventKind.Noise,
                AudioReviewEvidenceMethod.ManualListening,
                53000,
                54000),
        ];

        AudioReviewDetectionResult result =
            AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                modelRunId,
                observations);

        Assert.AreSame(timeline, result.Timeline);
        Assert.AreEqual(modelRunId, result.ModelRunId);
        Assert.AreSame(AudioReviewCandidateDetector.Configuration, result.Configuration);
        Assert.AreEqual(6, result.ObservationCount);
        Assert.HasCount(4, result.Candidates);
        AudioReviewCandidate wait = result.Candidates[0];
        Assert.AreEqual(AudioReviewEventKind.Wait, wait.Kind);
        Assert.AreEqual(20000L, wait.AnalysisStartSample);
        Assert.AreEqual(48000L, wait.AnalysisEndSample);
        CollectionAssert.AreEqual(
            new[]
            {
                AudioReviewEvidenceMethod.ManualListening,
                AudioReviewEvidenceMethod.FfmpegSilence,
                AudioReviewEvidenceMethod.WaveformLowEnergy,
            },
            wait.EvidenceMethods.ToArray());
        Assert.IsTrue(wait.ManualListeningConfirmed);
        Assert.AreEqual(new RationalTime(603, 500), wait.SourceSpan.Range.Start);
        Assert.AreEqual(new RationalTime(7, 4), wait.SourceSpan.Range.Duration);
        Assert.AreEqual(timeline.SourceAssetId, wait.SourceSpan.AssetId);
        Assert.AreEqual(timeline.SourceContentDigest, wait.SourceSpan.SourceDigest);
        Assert.AreEqual("audio:1", wait.SourceSpan.StreamId);
        Assert.IsNull(wait.SourceSpan.TranscriptRevisionId);
        Assert.HasCount(0, wait.SourceSpan.TranscriptTokenIds);
        Assert.AreEqual(DetectionKind.AudioEvent, wait.Detection.Kind);
        Assert.AreEqual(SuggestionKind.DeleteRange, wait.Suggestion.Kind);
        Assert.AreEqual(SuggestionStatus.Proposed, wait.Suggestion.Status);
        Assert.AreEqual(wait.SourceSpan.Id, wait.Detection.Origin.SourceSpanId);
        Assert.AreEqual(wait.SourceSpan.Id, wait.Suggestion.Origin.SourceSpanId);
        Assert.IsNull(wait.Detection.Confidence);
        Assert.IsNull(wait.Suggestion.Confidence);
        Assert.IsFalse(wait.AutomaticDeletionAllowed);
        Assert.IsTrue(wait.ExplicitHumanDecisionRequired);

        AudioReviewCandidate recording = result.Candidates[1];
        Assert.AreEqual(AudioReviewEventKind.RecordingQuality, recording.Kind);
        Assert.AreEqual(DetectionKind.RecordingProblem, recording.Detection.Kind);
        Assert.IsTrue(recording.TechnicalWarningOnly);
        Assert.IsFalse(recording.ManualListeningConfirmed);
        Assert.AreEqual(
            "This is a technical warning, not proof that the interval should be deleted.",
            recording.Suggestion.Warning);
        Assert.AreEqual(AudioReviewEventKind.Cough, result.Candidates[2].Kind);
        Assert.AreEqual(AudioReviewEventKind.Noise, result.Candidates[3].Kind);
        Assert.IsTrue(result.Candidates.Skip(2).All(
            static candidate => candidate.ManualListeningConfirmed));
        Assert.IsTrue(result.Candidates.All(static candidate =>
            candidate.Suggestion.MissingContext ==
                "The evidence does not establish that the interval is editorially removable."));
    }

    [TestMethod]
    public void ManualObservationsSupportCoughNoiseAndWaitWithoutModelScores()
    {
        foreach (AudioReviewEventKind kind in new[]
        {
            AudioReviewEventKind.Cough,
            AudioReviewEventKind.Noise,
            AudioReviewEventKind.Wait,
        })
        {
            var observation = new AudioReviewObservation(
                kind,
                AudioReviewEvidenceMethod.ManualListening,
                1,
                2);
            Assert.IsTrue(observation.ManualListeningConfirmed);
            Assert.IsNull(observation.ObservedMetric);
        }

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AudioReviewObservation(
                AudioReviewEventKind.Cough,
                AudioReviewEvidenceMethod.WaveformLowEnergy,
                1,
                2));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AudioReviewObservation(
                AudioReviewEventKind.Noise,
                AudioReviewEvidenceMethod.ManualListening,
                1,
                2,
                observedMetric: 0.5));
    }

    [TestMethod]
    public void HeldYamnetAndBenchmarkOnlySileroAreRejectedAtomically()
    {
        byte[] wave = BuildWave(new short[32000]);
        AnalysisAudioTimeline timeline = CreateTimeline(wave, 32000);
        foreach (AudioReviewObservation held in new[]
        {
            new AudioReviewObservation(
                AudioReviewEventKind.Cough,
                AudioReviewEvidenceMethod.YamnetHeld,
                1,
                100,
                observedMetric: 0.4),
            new AudioReviewObservation(
                AudioReviewEventKind.Wait,
                AudioReviewEvidenceMethod.SileroVadBenchmarkOnly,
                1,
                100),
        })
        {
            AssertFailure(
                () => AudioReviewCandidateDetector.CreateCandidates(
                    timeline,
                    EntityId<ModelRunIdTag>.New(),
                    [
                        new AudioReviewObservation(
                            AudioReviewEventKind.Noise,
                            AudioReviewEvidenceMethod.ManualListening,
                            100,
                            200),
                        held,
                    ]),
                AudioReviewFailureReason.MethodNotAdopted);
        }
    }

    [TestMethod]
    public void OutOfRangeAndDuplicateObservationsFailClosed()
    {
        byte[] wave = BuildWave(new short[32000]);
        AnalysisAudioTimeline timeline = CreateTimeline(wave, 32000);
        var observation = new AudioReviewObservation(
            AudioReviewEventKind.Cough,
            AudioReviewEvidenceMethod.ManualListening,
            1,
            100);
        AssertFailure(
            () => AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                EntityId<ModelRunIdTag>.New(),
                [
                    new AudioReviewObservation(
                        AudioReviewEventKind.Cough,
                        AudioReviewEvidenceMethod.ManualListening,
                        31999,
                        32001),
                ]),
            AudioReviewFailureReason.ObservationOutOfRange);
        AssertFailure(
            () => AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                EntityId<ModelRunIdTag>.New(),
                [observation, observation]),
            AudioReviewFailureReason.DuplicateObservation);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AudioReviewObservation(
                AudioReviewEventKind.Cough,
                AudioReviewEvidenceMethod.ManualListening,
                2,
                1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AudioReviewObservation(
                AudioReviewEventKind.Cough,
                AudioReviewEvidenceMethod.YamnetHeld,
                1,
                2,
                double.NaN));
    }

    [TestMethod]
    public void EmptyObservationSetIsAValidZeroCandidateResult()
    {
        byte[] wave = BuildWave(new short[1]);
        AnalysisAudioTimeline timeline = CreateTimeline(wave, 1);

        AudioReviewDetectionResult result =
            AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                EntityId<ModelRunIdTag>.New(),
                []);

        Assert.AreEqual(0, result.ObservationCount);
        Assert.HasCount(0, result.Candidates);
    }

    [TestMethod]
    public void AnalysisAndCandidateCreationHonorPreCancellation()
    {
        byte[] wave = BuildWave(new short[32000]);
        AnalysisAudioTimeline timeline = CreateTimeline(wave, 32000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            AudioReviewCandidateDetector.AnalyzeWaveform(
                timeline,
                wave,
                cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            AudioReviewCandidateDetector.CreateCandidates(
                timeline,
                EntityId<ModelRunIdTag>.New(),
                [],
                cancellation.Token));
    }

    [TestMethod]
    public void PublicSurfaceIsImmutableAndExposesNoDeleteOrModelExecution()
    {
        foreach (Type type in new[]
        {
            typeof(AudioReviewObservation),
            typeof(AudioReviewConfiguration),
            typeof(AudioReviewCandidate),
            typeof(AudioReviewDetectionResult),
            typeof(AudioReviewException),
        })
        {
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(AudioReviewConfiguration).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AudioReviewCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AudioReviewDetectionResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AudioReviewException).GetConstructors().Length);
        CollectionAssert.AreEqual(
            new[] { "AnalyzeWaveform", "CreateCandidates" },
            typeof(AudioReviewCandidateDetector)
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(AudioReviewCandidateDetector).GetMethod("Delete"));
        Assert.IsNull(typeof(AudioReviewCandidateDetector).GetMethod("RunYamnet"));
        Assert.IsNull(typeof(AudioReviewCandidateDetector).GetMethod("RunSilero"));
    }

    private static AudioReviewException AssertFailure(
        Action action,
        AudioReviewFailureReason reason)
    {
        AudioReviewException exception =
            Assert.ThrowsExactly<AudioReviewException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\private\customer.wav",
            StringComparison.OrdinalIgnoreCase));
        return exception;
    }

    private static AnalysisAudioTimeline CreateTimeline(
        byte[] wave,
        long sampleCount,
        long firstDecodedTimestamp = 0) =>
        new(
            EntityId<ProjectIdTag>.New(),
            EntityId<JobIdTag>.New(),
            EntityId<AssetIdTag>.New(),
            new ContentDigest(new string('a', 64)),
            sourceByteSize: 1000,
            EntityId<ArtifactIdTag>.New(),
            new ContentDigest(new string('b', 64)),
            sourceVideoStreamIndex: 0,
            sourceAudioStreamIndex: 1,
            new RationalTime(1, 48000),
            probeReportedAudioStart: null,
            firstDecodedTimestamp,
            firstDecodedAudioFrameSampleCount: 1024,
            EntityId<ArtifactIdTag>.New(),
            ContentDigest.Compute(wave),
            wave.LongLength,
            sampleCount,
            EntityId<ArtifactIdTag>.New());

    private static byte[] BuildWave(IReadOnlyList<short> samples)
    {
        int dataBytes = checked(samples.Count * sizeof(short));
        byte[] wave = new byte[checked(44 + dataBytes)];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(4, 4),
            checked((uint)(wave.Length - 8)));
        "WAVE"u8.CopyTo(wave.AsSpan(8));
        "fmt "u8.CopyTo(wave.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(40, 4),
            checked((uint)dataBytes));
        for (int index = 0; index < samples.Count; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                wave.AsSpan(44 + (index * sizeof(short)), sizeof(short)),
                samples[index]);
        }

        return wave;
    }
}