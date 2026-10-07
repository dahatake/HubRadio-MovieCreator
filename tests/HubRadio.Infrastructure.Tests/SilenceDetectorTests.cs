using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class SilenceDetectorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2037, 9, 10, 11, 12, 13, TimeSpan.Zero);

    [TestMethod]
    public void ConfigurationAndProcessRequest_AreFixedReviewOnlyAndShellFree()
    {
        using Fixture fixture = new();
        SilenceDetectionConfiguration configuration = fixture.Detector.Configuration;

        Assert.AreEqual(
            "stage0-2026-08-27.ffmpeg-silencedetect.v1",
            configuration.ThresholdVersion);
        Assert.AreEqual(-50d, configuration.NoiseDb);
        Assert.AreEqual(new RationalTime(2, 1), configuration.MinimumDuration);
        Assert.AreEqual(
            "nearest-to-even-analysis-sample",
            configuration.SampleRoundingRule);
        Assert.IsFalse(configuration.ProductionApproved);
        Assert.IsFalse(configuration.AutomaticDeletionAllowed);

        ProcessRunRequest request = SilenceDetector.CreateProcessRequest(
            fixture.FfmpegPath,
            fixture.AudioPath,
            TimeSpan.FromSeconds(123));
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin", "-hide_banner", "-nostats", "-v", "info",
                "-i", fixture.AudioPath,
                "-map", "0:a:0", "-vn", "-sn", "-dn",
                "-af", "silencedetect=noise=-50.0dB:d=2.0",
                "-f", "null", "NUL",
            },
            request.ArgumentList.ToArray());
        Assert.AreEqual(fixture.FfmpegPath, request.Executable);
        Assert.AreEqual(Path.GetDirectoryName(fixture.AudioPath), request.WorkingDirectory);
        Assert.AreEqual(TimeSpan.FromSeconds(123), request.Timeout);
        Assert.AreEqual(ProcessRunRequest.DefaultMaxCapturedBytes, request.MaxCapturedBytes);
        Assert.IsNull(request.StandardInput);
        Assert.IsFalse(request.ArgumentList.Contains("-y", StringComparer.Ordinal));
    }

    // FR-057: the pause detector is a separate, versioned review-only configuration.
    [TestMethod]
    [TestCategory("FR-057")]
    public void PauseConfigurationAndProcessRequest_AreVersionedReviewOnlyAndShellFree()
    {
        using Fixture fixture = new();
        SilenceDetectionConfiguration configuration = SilenceDetector.PauseDetectionConfiguration;

        Assert.AreEqual("fr057-2026-10-07.ffmpeg-silencedetect-pause.v1", configuration.ThresholdVersion);
        Assert.AreEqual(-32d, configuration.NoiseDb);
        Assert.AreEqual(new RationalTime(2, 5), configuration.MinimumDuration);
        Assert.IsFalse(configuration.ProductionApproved);
        Assert.IsFalse(configuration.AutomaticDeletionAllowed);
        Assert.AreEqual("stage0-2026-08-27.ffmpeg-silencedetect.v1", fixture.Detector.Configuration.ThresholdVersion);

        ProcessRunRequest request = SilenceDetector.CreatePauseProcessRequest(
            fixture.FfmpegPath,
            fixture.AudioPath,
            TimeSpan.FromSeconds(123));
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin", "-hide_banner", "-nostats", "-v", "info",
                "-i", fixture.AudioPath,
                "-map", "0:a:0", "-vn", "-sn", "-dn",
                "-af", "silencedetect=noise=-32.0dB:d=0.4",
                "-f", "null", "NUL",
            },
            request.ArgumentList.ToArray());
        Assert.IsNull(request.StandardInput);
    }

    [TestMethod]
    public void ParseProcessResult_MapsExactSamplesToSourceAndCreatesProposedCandidate()
    {
        using Fixture fixture = new(
            sampleCount: 160000,
            firstDecodedTimestamp: -2112);
        ProcessRunResult processResult = Result(
            standardError:
                "[silencedetect @ 0000000000000001] silence_start: 2\r\n" +
                "[silencedetect @ 0000000000000001] silence_end: 5 | silence_duration: 3\r\n",
            duration: TimeSpan.FromMilliseconds(37));

        SilenceDetectionResult result = SilenceDetector.ParseProcessResult(
            fixture.Timeline,
            fixture.ModelRunId,
            processResult);

        Assert.AreSame(fixture.Timeline, result.Timeline);
        Assert.AreEqual(fixture.ModelRunId, result.ModelRunId);
        Assert.AreEqual(TimeSpan.FromMilliseconds(37), result.ProcessDuration);
        Assert.HasCount(1, result.Candidates);
        SilenceCandidate candidate = result.Candidates[0];
        Assert.AreEqual(32000L, candidate.AnalysisStartSample);
        Assert.AreEqual(80000L, candidate.AnalysisEndSample);
        Assert.AreEqual(new RationalTime(489, 250), candidate.SourceSpan.Range.Start);
        Assert.AreEqual(new RationalTime(3, 1), candidate.SourceSpan.Range.Duration);
        Assert.AreEqual(fixture.Timeline.SourceAssetId, candidate.SourceSpan.AssetId);
        Assert.AreEqual(fixture.Timeline.SourceContentDigest, candidate.SourceSpan.SourceDigest);
        Assert.AreEqual("audio:1", candidate.SourceSpan.StreamId);
        Assert.IsNull(candidate.SourceSpan.TranscriptRevisionId);
        Assert.HasCount(0, candidate.SourceSpan.TranscriptTokenIds);
        Assert.AreEqual(DetectionKind.Silence, candidate.Detection.Kind);
        Assert.AreEqual(SuggestionKind.DeleteRange, candidate.Suggestion.Kind);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
        Assert.AreEqual(candidate.SourceSpan.Id, candidate.Detection.Origin.SourceSpanId);
        Assert.AreEqual(candidate.SourceSpan.Id, candidate.Suggestion.Origin.SourceSpanId);
        Assert.AreEqual(fixture.ModelRunId, candidate.Detection.ModelRunId);
        Assert.AreEqual(fixture.ModelRunId, candidate.Suggestion.ModelRunId);
        Assert.IsNull(candidate.Detection.Confidence);
        Assert.IsNull(candidate.Suggestion.Confidence);
        Assert.AreEqual(
            "The detector does not establish whether the interval is editorially removable.",
            candidate.Suggestion.MissingContext);
        Assert.AreEqual(
            "The threshold is not production approved; listen before deciding.",
            candidate.Suggestion.Warning);
        Assert.AreEqual(SilenceDetector.ThresholdVersion, candidate.ThresholdVersion);
    }

    [TestMethod]
    public void ParseProcessResult_SupportsDerivedStartAndTrailingSilenceWithoutClamping()
    {
        using Fixture fixture = new(sampleCount: 160000);
        ProcessRunResult processResult = Result(
            standardError:
                "[silencedetect @ a] silence_end: 3 | silence_duration: 3\n" +
                "[silencedetect @ a] silence_start: 6\n");

        SilenceDetectionResult result = SilenceDetector.ParseProcessResult(
            fixture.Timeline,
            fixture.ModelRunId,
            processResult);

        Assert.HasCount(2, result.Candidates);
        Assert.AreEqual(0L, result.Candidates[0].AnalysisStartSample);
        Assert.AreEqual(48000L, result.Candidates[0].AnalysisEndSample);
        Assert.AreEqual(96000L, result.Candidates[1].AnalysisStartSample);
        Assert.AreEqual(160000L, result.Candidates[1].AnalysisEndSample);
        Assert.AreEqual(
            new RationalTime(4, 1),
            result.Candidates[1].SourceSpan.Range.Duration);
    }

    [TestMethod]
    public void ParseProcessResult_EmptyObservationIsAValidZeroCandidateResult()
    {
        using Fixture fixture = new();

        SilenceDetectionResult result = SilenceDetector.ParseProcessResult(
            fixture.Timeline,
            fixture.ModelRunId,
            Result(standardError: "Input #0, wav, from managed analysis audio:\n"));

        Assert.HasCount(0, result.Candidates);
        Assert.AreEqual(TimeSpan.Zero, result.ProcessDuration);
        Assert.IsFalse(result.Configuration.AutomaticDeletionAllowed);
    }

    [TestMethod]
    public void ParseProcessResult_RejectsMalformedOrUntrustworthyIntervals()
    {
        using Fixture fixture = new(sampleCount: 160000);
        string[] malformed =
        [
            "[silencedetect @ a] silence_start: 1\n" +
                "[silencedetect @ a] silence_start: 2\n",
            "[silencedetect @ a] silence_start: 5\n" +
                "[silencedetect @ a] silence_end: 3 | silence_duration: 2\n",
            "[silencedetect @ a] silence_start: 2\n" +
                "[silencedetect @ a] silence_end: 5 | silence_duration: 2.9\n",
            "[silencedetect @ a] silence_start: 2\n" +
                "[silencedetect @ a] silence_end: 3 | silence_duration: 1\n",
            "[silencedetect @ a] silence_start: 2\n" +
                "[silencedetect @ a] silence_end: 5 | silence_duration: 3\n" +
                "[silencedetect @ a] silence_start: 4\n" +
                "[silencedetect @ a] silence_end: 7 | silence_duration: 3\n",
            "[silencedetect @ a] silence_start: 9\n" +
                "[silencedetect @ a] silence_end: 11 | silence_duration: 2\n",
            "prefix silence_start: 2 suffix\n",
            "[silencedetect @ a] silence_start: NaN\n",
            "invalid \uFFFD output\n",
        ];

        foreach (string standardError in malformed)
        {
            AssertFailure(
                () => SilenceDetector.ParseProcessResult(
                    fixture.Timeline,
                    fixture.ModelRunId,
                    Result(standardError: standardError)),
                SilenceDetectorFailureReason.OutputMalformed);
        }
    }

    [TestMethod]
    public void ParseProcessResult_RejectsProcessFailureUnexpectedOutputAndTruncation()
    {
        using Fixture fixture = new();
        AssertFailure(
            () => SilenceDetector.ParseProcessResult(
                fixture.Timeline,
                fixture.ModelRunId,
                Result(exitCode: 1, standardError: "private failure")),
            SilenceDetectorFailureReason.ProcessFailed,
            "private failure");
        AssertFailure(
            () => SilenceDetector.ParseProcessResult(
                fixture.Timeline,
                fixture.ModelRunId,
                Result(standardOutput: "unexpected")),
            SilenceDetectorFailureReason.ProcessFailed,
            "unexpected");
        AssertFailure(
            () => SilenceDetector.ParseProcessResult(
                fixture.Timeline,
                fixture.ModelRunId,
                Result(truncated: true, standardError: "private truncated")),
            SilenceDetectorFailureReason.OutputTruncated,
            "private truncated");
    }

    [TestMethod]
    public async Task DetectAsync_VerifiesToolAndRegisteredAudioBeforeAndAfterProcess()
    {
        using Fixture fixture = new(sampleCount: 160000);
        fixture.ProcessResult = Result(
            standardError:
                "[silencedetect @ a] silence_start: 2\n" +
                "[silencedetect @ a] silence_end: 5 | silence_duration: 3\n",
            duration: TimeSpan.FromMilliseconds(8));

        SilenceDetectionResult result = await fixture.DetectAsync();

        Assert.HasCount(1, result.Candidates);
        Assert.HasCount(1, fixture.ProcessRequests);
        Assert.AreEqual(fixture.FfmpegPath, fixture.ProcessRequests[0].Executable);
        Assert.AreEqual(fixture.AudioPath, fixture.ProcessRequests[0].ArgumentList[6]);
        Assert.AreEqual(2, fixture.ToolVerificationCount);
        Assert.AreEqual(2, fixture.RegisteredVerificationCount);
        Assert.AreEqual(1, fixture.RegisteredPathResolutionCount);
        Assert.AreEqual(fixture.OriginalAudioDigest, ContentDigest.Compute(
            await File.ReadAllBytesAsync(fixture.AudioPath)));
    }

    [TestMethod]
    public async Task DetectAsync_FailsClosedForVerificationProcessTimeoutAndCancellation()
    {
        using Fixture toolFailure = new();
        toolFailure.ToolVerificationResultFactory = request =>
            BinaryVerificationResult.Failed(
                request,
                BinaryVerificationFailureReason.DigestMismatch,
                request.ExpectedByteSize,
                new ContentDigest(new string('f', 64)));
        SilenceDetectorException toolException =
            await Assert.ThrowsExactlyAsync<SilenceDetectorException>(
                () => toolFailure.DetectAsync());
        AssertSafeFailure(
            toolException,
            SilenceDetectorFailureReason.ToolVerificationFailed);
        Assert.IsEmpty(toolFailure.ProcessRequests);

        using Fixture registeredFailure = new();
        registeredFailure.RegisteredVerificationResultFactory = request =>
            BinaryVerificationResult.Failed(
                request,
                BinaryVerificationFailureReason.SizeMismatch,
                request.ExpectedByteSize + 1,
                request.ExpectedDigest);
        SilenceDetectorException registeredException =
            await Assert.ThrowsExactlyAsync<SilenceDetectorException>(
                () => registeredFailure.DetectAsync());
        AssertSafeFailure(
            registeredException,
            SilenceDetectorFailureReason.RegisteredAnalysisInvalid);
        Assert.IsEmpty(registeredFailure.ProcessRequests);

        using Fixture timedOut = new();
        timedOut.ProcessHandler = (_, _) =>
            throw new TimeoutException("private timeout");
        SilenceDetectorException timeoutException =
            await Assert.ThrowsExactlyAsync<SilenceDetectorException>(
                () => timedOut.DetectAsync());
        AssertSafeFailure(
            timeoutException,
            SilenceDetectorFailureReason.ProcessTimedOut,
            "private timeout");

        using Fixture cancelled = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            cancelled.DetectAsync(cancellation.Token));
        Assert.IsEmpty(cancelled.ProcessRequests);
        Assert.AreEqual(0, cancelled.ToolVerificationCount);
        Assert.AreEqual(0, cancelled.RegisteredVerificationCount);
    }

    [TestMethod]
    public async Task DetectAsync_CanonicalFfmpegFindsKnownTwoToFiveSecondControlWhenAvailable()
    {
        if (!TryFindCanonicalFfmpeg(out string ffmpegPath))
        {
            Assert.Inconclusive("The pinned LGPL FFmpeg executable is unavailable.");
            return;
        }

        using TemporaryDirectory temporary = new("hubradio-a007-integration-");
        string audioPath = Path.Combine(temporary.Path, "silence-control.wav");
        var runner = new ProcessRunner();
        ProcessRunResult generated = await runner.RunAsync(
            new ProcessRunRequest(
                ffmpegPath,
                [
                    "-nostdin", "-hide_banner", "-v", "error",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=2",
                    "-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=16000:d=3",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=2",
                    "-filter_complex", "[0:a][1:a][2:a]concat=n=3:v=0:a=1[out]",
                    "-map", "[out]", "-map_metadata", "-1",
                    "-c:a", "pcm_s16le",
                    "-fflags", "+bitexact", "-flags:a", "+bitexact",
                    "-n", audioPath,
                ],
                temporary.Path,
                TimeSpan.FromSeconds(30)));
        Assert.AreEqual(0, generated.ExitCode);
        AudioExtraction.WavSnapshot wave = AudioExtraction.ParseWaveFile(audioPath);
        using Fixture fixture = new(
            sampleCount: wave.SampleCount,
            audioPath: audioPath,
            ffmpegPath: ffmpegPath,
            ownsAudioPath: false);
        var detector = new SilenceDetector(
            runner.RunAsync,
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(BinaryVerificationResult.Verified(
                    request,
                    request.ExpectedByteSize,
                    request.ExpectedDigest));
            },
            fixture.VerifyRegisteredAsync,
            _ => audioPath,
            ffmpegPath);

        SilenceDetectionResult result = await detector.DetectAsync(
            fixture.AnalysisAudio,
            fixture.ModelRunId,
            TimeSpan.FromSeconds(30));

        Assert.HasCount(1, result.Candidates);
        SilenceCandidate candidate = result.Candidates[0];
        Assert.IsLessThanOrEqualTo(
            2400L,
            Math.Abs(candidate.AnalysisStartSample - 32000L));
        Assert.IsLessThanOrEqualTo(
            2400L,
            Math.Abs(candidate.AnalysisEndSample - 80000L));
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
        Assert.IsFalse(result.Configuration.ProductionApproved);
        Assert.IsFalse(result.Configuration.AutomaticDeletionAllowed);
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndDoesNotExposeDeletionOrThresholdMutation()
    {
        foreach (Type type in new[]
        {
            typeof(SilenceDetectionConfiguration),
            typeof(SilenceCandidate),
            typeof(SilenceDetectionResult),
            typeof(SilenceDetectorException),
            typeof(SilenceDetector),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(SilenceDetectionConfiguration).GetConstructors().Length);
        Assert.AreEqual(0, typeof(SilenceCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(SilenceDetectionResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(SilenceDetectorException).GetConstructors().Length);
        CollectionAssert.AreEquivalent(
            new[] { "DetectAsync", "DetectPausesAsync" },
            typeof(SilenceDetector)
                .GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(SilenceDetector).GetMethod("Delete"));
        Assert.IsNull(typeof(SilenceDetector).GetMethod("Adopt"));
    }

    private static ProcessRunResult Result(
        int exitCode = 0,
        string standardOutput = "",
        string standardError = "",
        bool truncated = false,
        TimeSpan? duration = null) =>
        new(
            exitCode,
            standardOutput,
            standardError,
            truncated,
            duration ?? TimeSpan.Zero);

    private static SilenceDetectorException AssertFailure(
        Action action,
        SilenceDetectorFailureReason reason,
        params string[] privateValues)
    {
        SilenceDetectorException exception =
            Assert.ThrowsExactly<SilenceDetectorException>(action);
        AssertSafeFailure(exception, reason, privateValues);
        return exception;
    }

    private static void AssertSafeFailure(
        SilenceDetectorException exception,
        SilenceDetectorFailureReason reason,
        params string[] privateValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        foreach (string value in privateValues)
        {
            Assert.IsFalse(exception.ToString().Contains(value, StringComparison.Ordinal));
        }

        Assert.IsFalse(exception.ToString().Contains(
            @"C:\private\customer.wav",
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryFindCanonicalFfmpeg(out string ffmpegPath)
    {
        ffmpegPath = string.Empty;
        try
        {
            string packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Packages");
            if (!Directory.Exists(packagesRoot))
            {
                return false;
            }

            foreach (string packageDirectory in Directory.GetDirectories(
                packagesRoot,
                string.Concat(DecodeValidator.CanonicalPackageId, "*"),
                SearchOption.TopDirectoryOnly))
            {
                foreach (string candidate in Directory.EnumerateFiles(
                    packageDirectory,
                    "ffmpeg.exe",
                    SearchOption.AllDirectories))
                {
                    var info = new FileInfo(candidate);
                    if (info.Length != DecodeValidator.CanonicalFfmpegByteSize)
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(candidate);
                    string digest = Convert.ToHexString(SHA256.HashData(stream))
                        .ToLowerInvariant();
                    if (string.Equals(
                        digest,
                        DecodeValidator.CanonicalFfmpegSha256,
                        StringComparison.Ordinal))
                    {
                        ffmpegPath = Path.GetFullPath(candidate);
                        return true;
                    }
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                CryptographicException or NotSupportedException)
        {
        }

        return false;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary;
        private readonly bool _ownsAudioPath;

        internal Fixture(
            long sampleCount = 160000,
            long firstDecodedTimestamp = 0,
            string? audioPath = null,
            string? ffmpegPath = null,
            bool ownsAudioPath = true)
        {
            _temporary = new TemporaryDirectory("hubradio-a007-");
            _ownsAudioPath = ownsAudioPath;
            AudioPath = audioPath ?? Path.Combine(_temporary.Path, "analysis.wav");
            FfmpegPath = ffmpegPath ?? Path.Combine(_temporary.Path, "ffmpeg.exe");
            if (!File.Exists(FfmpegPath))
            {
                File.WriteAllText(FfmpegPath, "fake ffmpeg", Encoding.ASCII);
            }

            if (audioPath is null)
            {
                File.WriteAllBytes(
                    AudioPath,
                    new byte[checked(44 + (int)(sampleCount * sizeof(short)))]);
            }

            byte[] audioBytes = File.ReadAllBytes(AudioPath);
            OriginalAudioDigest = ContentDigest.Compute(audioBytes);
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            EntityId<ArtifactIdTag> audioArtifactId = EntityId<ArtifactIdTag>.New();
            EntityId<ArtifactIdTag> timelineArtifactId = EntityId<ArtifactIdTag>.New();
            Timeline = new AnalysisAudioTimeline(
                projectId,
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
                audioArtifactId,
                OriginalAudioDigest,
                audioBytes.LongLength,
                sampleCount,
                timelineArtifactId);
            var audioArtifact = new StoredArtifact(
                audioArtifactId,
                projectId,
                ArtifactKind.Analysis,
                $"projects/{projectId}/analysis/{audioArtifactId}.wav",
                OriginalAudioDigest,
                audioBytes.LongLength,
                CreatedAt);
            byte[] timelineBytes = Encoding.UTF8.GetBytes(Timeline.CanonicalJson);
            var timelineArtifact = new StoredArtifact(
                timelineArtifactId,
                projectId,
                ArtifactKind.Analysis,
                $"projects/{projectId}/analysis/{timelineArtifactId}.json",
                ContentDigest.Compute(timelineBytes),
                timelineBytes.LongLength,
                CreatedAt);
            AnalysisAudio = new ExtractedAnalysisAudio(
                audioArtifact,
                timelineArtifact,
                Timeline,
                TimeSpan.Zero);
            ModelRunId = EntityId<ModelRunIdTag>.New();
            ProcessResult = Result();
            ProcessHandler = DefaultProcessAsync;
            ToolVerificationResultFactory = request =>
                BinaryVerificationResult.Verified(
                    request,
                    request.ExpectedByteSize,
                    request.ExpectedDigest);
            RegisteredVerificationResultFactory = request =>
                BinaryVerificationResult.Verified(
                    request,
                    request.ExpectedByteSize,
                    request.ExpectedDigest);
            Detector = CreateDetector();
        }

        internal string AudioPath { get; }

        internal string FfmpegPath { get; }

        internal ContentDigest OriginalAudioDigest { get; }

        internal AnalysisAudioTimeline Timeline { get; }

        internal ExtractedAnalysisAudio AnalysisAudio { get; }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal SilenceDetector Detector { get; }

        internal ProcessRunResult ProcessResult { get; set; }

        internal List<ProcessRunRequest> ProcessRequests { get; } = [];

        internal int ToolVerificationCount { get; private set; }

        internal int RegisteredVerificationCount { get; private set; }

        internal int RegisteredPathResolutionCount { get; private set; }

        internal Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
            ProcessHandler
        { get; set; }

        internal Func<BinaryVerificationRequest, BinaryVerificationResult>
            ToolVerificationResultFactory
        { get; set; }

        internal Func<BinaryVerificationRequest, BinaryVerificationResult>
            RegisteredVerificationResultFactory
        { get; set; }

        internal Task<SilenceDetectionResult> DetectAsync(
            CancellationToken cancellationToken = default) =>
            Detector.DetectAsync(
                AnalysisAudio,
                ModelRunId,
                TimeSpan.FromSeconds(5),
                cancellationToken);

        internal Task<BinaryVerificationResult> VerifyRegisteredAsync(
            EntityId<ArtifactIdTag> artifactId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(AnalysisAudio.AudioArtifact.Id, artifactId);
            BinaryVerificationRequest request = new(
                "artifact.analysis-audio.silence-test",
                AudioPath,
                AnalysisAudio.AudioArtifact.ContentDigest,
                AnalysisAudio.AudioArtifact.ByteSize);
            return Task.FromResult(BinaryVerificationResult.Verified(
                request,
                request.ExpectedByteSize,
                request.ExpectedDigest));
        }

        private SilenceDetector CreateDetector() =>
            new(
                (request, cancellationToken) =>
                    ProcessHandler(request, cancellationToken),
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ToolVerificationCount++;
                    return Task.FromResult(ToolVerificationResultFactory(request));
                },
                (artifactId, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RegisteredVerificationCount++;
                    Assert.AreEqual(AnalysisAudio.AudioArtifact.Id, artifactId);
                    BinaryVerificationRequest request = new(
                        "artifact.analysis-audio.silence-test",
                        AudioPath,
                        AnalysisAudio.AudioArtifact.ContentDigest,
                        AnalysisAudio.AudioArtifact.ByteSize);
                    return Task.FromResult(
                        RegisteredVerificationResultFactory(request));
                },
                artifactId =>
                {
                    RegisteredPathResolutionCount++;
                    Assert.AreEqual(AnalysisAudio.AudioArtifact.Id, artifactId);
                    return AudioPath;
                },
                FfmpegPath);

        private Task<ProcessRunResult> DefaultProcessAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRequests.Add(request);
            return Task.FromResult(ProcessResult);
        }

        public void Dispose()
        {
            if (_ownsAudioPath && File.Exists(AudioPath))
            {
                File.Delete(AudioPath);
            }

            _temporary.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                string.Concat(prefix, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}