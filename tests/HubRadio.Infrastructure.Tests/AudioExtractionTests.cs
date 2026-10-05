using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AudioExtractionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2037, 8, 9, 10, 11, 12, TimeSpan.Zero);

    [TestMethod]
    public void Timeline_MapsSamplesAndAsrSecondsFromDecodedFrameNotPacketStart()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            firstDecodedTimestamp: 4800,
            sourceAudioTimeBase: new RationalTime(1, 48000),
            probeStart: new RationalTime(-11, 250),
            sampleCount: 32000);

        Assert.AreEqual(new RationalTime(1, 10), timeline.FirstDecodedAudioFramePresentationTime);
        Assert.AreEqual(new RationalTime(-11, 250), timeline.ProbeReportedAudioStart);
        Assert.IsFalse(timeline.ProbeReportedPacketStartApplied);
        Assert.AreEqual(new RationalTime(1, 10), timeline.MapAnalysisSampleToSourceTime(0));
        Assert.AreEqual(new RationalTime(8, 5), timeline.MapAnalysisSampleToSourceTime(24000));
        Assert.AreEqual(
            new RationalTime(8, 5),
            timeline.MapAsrSecondsToSourceTime(
                1.5,
                RationalTimeRoundingMode.NearestToEven));
        Assert.AreEqual(
            24000L,
            timeline.MapSourceTimeToAnalysisSample(
                new RationalTime(8, 5),
                RationalTimeRoundingMode.NearestToEven));
        Assert.AreEqual(32000L, timeline.MapSourceTimeToAnalysisSample(
            new RationalTime(21, 10),
            RationalTimeRoundingMode.Ceiling));
    }

    [TestMethod]
    public void Timeline_PreservesNegativeDecodedStartAcrossBothMappingDirections()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            firstDecodedTimestamp: -2112,
            sourceAudioTimeBase: new RationalTime(1, 48000),
            probeStart: new RationalTime(-11, 250),
            sampleCount: 1600);

        Assert.AreEqual(
            new RationalTime(-11, 250),
            timeline.FirstDecodedAudioFramePresentationTime);
        Assert.AreEqual(
            new RationalTime(-11, 250),
            timeline.MapAsrSecondsToSourceTime(
                0d,
                RationalTimeRoundingMode.NearestToEven));
        Assert.AreEqual(
            default(RationalTime),
            timeline.MapAnalysisSampleToSourceTime(704));
        Assert.AreEqual(
            704L,
            timeline.MapSourceTimeToAnalysisSample(
                default,
                RationalTimeRoundingMode.NearestToEven));
    }

    [TestMethod]
    public void Timeline_RequiresExplicitSampleRoundingAndRejectsOutsideRange()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(sampleCount: 10);
        double halfSample = 0.5 / AnalysisAudioTimeline.FixedSampleRate;

        Assert.AreEqual(
            default(RationalTime),
            timeline.MapAsrSecondsToSourceTime(
                halfSample,
                RationalTimeRoundingMode.NearestToEven));
        Assert.AreEqual(
            new RationalTime(1, AnalysisAudioTimeline.FixedSampleRate),
            timeline.MapAsrSecondsToSourceTime(
                halfSample,
                RationalTimeRoundingMode.NearestAwayFromZero));
        Assert.AreEqual(
            default(RationalTime),
            timeline.MapAsrSecondsToSourceTime(
                halfSample,
                RationalTimeRoundingMode.Floor));
        Assert.AreEqual(
            new RationalTime(1, AnalysisAudioTimeline.FixedSampleRate),
            timeline.MapAsrSecondsToSourceTime(
                halfSample,
                RationalTimeRoundingMode.Ceiling));

        foreach (double invalid in new[]
        {
            -double.Epsilon,
            double.NaN,
            double.PositiveInfinity,
            1d,
        })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                timeline.MapAsrSecondsToSourceTime(
                    invalid,
                    RationalTimeRoundingMode.NearestToEven));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            timeline.MapAnalysisSampleToSourceTime(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            timeline.MapAnalysisSampleToSourceTime(11));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            timeline.MapSourceTimeToAnalysisSample(
                new RationalTime(-1, 16000),
                RationalTimeRoundingMode.Floor));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            timeline.MapSourceTimeToAnalysisSample(
                new RationalTime(11, 16000),
                RationalTimeRoundingMode.Ceiling));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            timeline.MapAsrSecondsToSourceTime(0, (RationalTimeRoundingMode)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            timeline.MapSourceTimeToAnalysisSample(
                default,
                (RationalTimeRoundingMode)99));
    }

    [TestMethod]
    public void Timeline_CanonicalJsonIsClosedPathFreeAndBindsEveryIdentity()
    {
        AnalysisAudioTimeline timeline = CreateTimeline(
            firstDecodedTimestamp: 0,
            sourceAudioTimeBase: new RationalTime(1, 48000),
            probeStart: new RationalTime(-11, 250),
            sampleCount: 1600);

        using JsonDocument document = JsonDocument.Parse(timeline.CanonicalJson);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "projectId",
                "analysisJobId",
                "sourceAssetId",
                "sourceContentDigest",
                "sourceByteSize",
                "sourceOriginalArtifactId",
                "sourceVideoPtsIndexDigest",
                "sourceVideoStreamIndex",
                "sourceAudioStreamIndex",
                "sourceAudioTimeBase",
                "probeReportedAudioStart",
                "firstDecodedAudioFrameTimestamp",
                "firstDecodedAudioFramePresentationTime",
                "firstDecodedAudioFrameSampleCount",
                "analysisAudioArtifactId",
                "analysisAudioDigest",
                "analysisAudioByteSize",
                "analysisSampleRate",
                "analysisSampleCount",
                "analysisDuration",
                "timelineArtifactId",
                "mappingRule",
                "probeReportedPacketStartApplied",
            },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("1.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(16000d, root.GetProperty("analysisSampleRate").GetDouble());
        Assert.AreEqual(1600d, root.GetProperty("analysisSampleCount").GetDouble());
        Assert.AreEqual(-11d, root
            .GetProperty("probeReportedAudioStart")
            .GetProperty("value")
            .GetDouble());
        Assert.AreEqual(250d, root
            .GetProperty("probeReportedAudioStart")
            .GetProperty("rate")
            .GetDouble());
        Assert.IsFalse(root.GetProperty("probeReportedPacketStartApplied").GetBoolean());
        Assert.IsFalse(timeline.CanonicalJson.Contains(
            @"C:\",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(timeline.CanonicalJson.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessRequests_UseExactPinnedStreamAndBitExactPcmArguments()
    {
        using TemporaryDirectory temporary = new();
        string ffmpeg = Path.Combine(temporary.Path, "ffmpeg.exe");
        string ffprobe = Path.Combine(temporary.Path, "ffprobe.exe");
        string source = Path.Combine(temporary.Path, "source.mov");
        string output = Path.Combine(temporary.Path, "audio.partial.wav");
        TimeSpan timeout = TimeSpan.FromSeconds(123);

        ProcessRunRequest extraction = AudioExtraction.CreateExtractionProcessRequest(
            ffmpeg,
            source,
            output,
            audioStreamIndex: 4,
            timeout);
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin", "-v", "error", "-i", Path.GetFullPath(source),
                "-map", "0:4", "-vn", "-map_metadata", "-1",
                "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                "-fflags", "+bitexact", "-flags:a", "+bitexact",
                "-n", Path.GetFullPath(output),
            },
            extraction.ArgumentList.ToArray());
        Assert.AreEqual(Path.GetFullPath(ffmpeg), extraction.Executable);
        Assert.AreEqual(temporary.Path, extraction.WorkingDirectory);
        Assert.AreEqual(timeout, extraction.Timeout);
        Assert.IsFalse(extraction.ArgumentList.Contains("-y", StringComparer.Ordinal));
        Assert.IsFalse(extraction.ArgumentList.Contains("-async", StringComparer.Ordinal));

        ProcessRunRequest probe = AudioExtraction.CreateDecodedFrameProbeRequest(
            ffprobe,
            source,
            audioStreamIndex: 4,
            timeout);
        CollectionAssert.AreEqual(
            new[]
            {
                "-v", "error", "-select_streams", "4", "-read_intervals", "%+#16",
                "-show_frames", "-show_entries",
                "frame=stream_index,best_effort_timestamp,nb_samples",
                "-of", "compact=p=0:nk=0:escape=none:s=|",
                Path.GetFullPath(source),
            },
            probe.ArgumentList.ToArray());
        Assert.AreEqual(Path.GetFullPath(ffprobe), probe.Executable);
        Assert.AreEqual(temporary.Path, probe.WorkingDirectory);
    }

    [TestMethod]
    public void DecodedStartParser_PreservesNegativeOrZeroTimestampAndRejectsMalformedOutput()
    {
        AudioExtraction.DecodedAudioStart negative =
            AudioExtraction.ParseDecodedAudioStart(
                Result(
                    "stream_index=1|best_effort_timestamp=-2112|nb_samples=1024\n" +
                    "stream_index=1|best_effort_timestamp=-1088|nb_samples=1024\n"),
                expectedStreamIndex: 1);
        Assert.AreEqual(-2112L, negative.Timestamp);
        Assert.AreEqual(1024, negative.SampleCount);

        AudioExtraction.DecodedAudioStart zero =
            AudioExtraction.ParseDecodedAudioStart(
                Result("stream_index=1|best_effort_timestamp=0|nb_samples=1024\n"),
                expectedStreamIndex: 1);
        Assert.AreEqual(0L, zero.Timestamp);

        string[] invalid =
        [
            string.Empty,
            "stream_index=1|best_effort_timestamp=0|nb_samples=1024",
            "stream_index=1|best_effort_timestamp=0|nb_samples=0\n",
            "stream_index=2|best_effort_timestamp=0|nb_samples=1024\n",
            "stream_index=1|best_effort_timestamp=N/A|nb_samples=1024\n",
            "stream_index=1|best_effort_timestamp=0|nb_samples=1024|extra=1\n",
            "stream_index=1|best_effort_timestamp=0|nb_samples=1024\n" +
                "stream_index=1|best_effort_timestamp=0|nb_samples=1024\n",
            "stream_index=1|best_effort_timestamp=0\r|nb_samples=1024\n",
        ];
        foreach (string output in invalid)
        {
            AudioExtractionException exception =
                Assert.ThrowsExactly<AudioExtractionException>(() =>
                    AudioExtraction.ParseDecodedAudioStart(
                        Result(output),
                        expectedStreamIndex: 1));
            Assert.AreEqual(
                AudioExtractionFailureReason.TimelineProbeFailed,
                exception.Reason);
        }

        AudioExtraction.DecodedAudioStart crlf =
            AudioExtraction.ParseDecodedAudioStart(
                Result("stream_index=1|best_effort_timestamp=0|nb_samples=1024\r\n"),
                expectedStreamIndex: 1);
        Assert.AreEqual(0L, crlf.Timestamp);

        foreach (ProcessRunResult invalidResult in new[]
        {
            Result("x\n", exitCode: 1),
            Result("x\n", standardError: "private"),
            Result("x\n", truncated: true),
        })
        {
            Assert.ThrowsExactly<AudioExtractionException>(() =>
                AudioExtraction.ParseDecodedAudioStart(invalidResult, 1));
        }
    }

    [TestMethod]
    public void WaveParser_AcceptsOnlyExactNonemptyMono16kPcmS16leRiff()
    {
        using TemporaryDirectory temporary = new();
        string valid = Path.Combine(temporary.Path, "valid.wav");
        WriteWave(valid, sampleCount: 1600);

        AudioExtraction.WavSnapshot snapshot = AudioExtraction.ParseWaveFile(valid);

        Assert.AreEqual(new FileInfo(valid).Length, snapshot.ByteSize);
        Assert.AreEqual(1600L, snapshot.SampleCount);

        string[] invalid =
        [
            WriteWave(Path.Combine(temporary.Path, "stereo.wav"), 1600, channels: 2),
            WriteWave(Path.Combine(temporary.Path, "8k.wav"), 1600, sampleRate: 8000),
            WriteWave(Path.Combine(temporary.Path, "8bit.wav"), 1600, bitsPerSample: 8),
            WriteWave(Path.Combine(temporary.Path, "empty.wav"), 0),
            WriteWave(Path.Combine(temporary.Path, "extra.wav"), 1600, appendExtraChunk: true),
        ];
        string truncated = Path.Combine(temporary.Path, "truncated.wav");
        byte[] truncatedBytes = File.ReadAllBytes(valid)[..^1];
        File.WriteAllBytes(truncated, truncatedBytes);
        foreach (string path in invalid.Append(truncated))
        {
            AudioExtractionException exception =
                Assert.ThrowsExactly<AudioExtractionException>(() =>
                    AudioExtraction.ParseWaveFile(path));
            Assert.AreEqual(AudioExtractionFailureReason.OutputInvalid, exception.Reason);
            Assert.IsFalse(exception.ToString().Contains(path, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task ExtractAsync_PromotesBoundAudioAndTimelineAndCleansStaging()
    {
        using ExtractionFixture fixture = new();

        ExtractedAnalysisAudio result = await fixture.ExtractAsync();

        Assert.AreEqual(fixture.AudioArtifactId, result.AudioArtifact.Id);
        Assert.AreEqual(fixture.TimelineArtifactId, result.TimelineArtifact.Id);
        Assert.AreEqual(ArtifactKind.Analysis, result.AudioArtifact.Kind);
        Assert.AreEqual(ArtifactKind.Analysis, result.TimelineArtifact.Kind);
        Assert.AreEqual(fixture.SourceAsset.Id, result.Timeline.SourceAssetId);
        Assert.AreEqual(fixture.SourceOriginal.Id, result.Timeline.SourceOriginalArtifactId);
        Assert.AreEqual(fixture.AnalysisJobId, result.Timeline.AnalysisJobId);
        Assert.AreEqual(1, result.Timeline.SourceAudioStreamIndex);
        Assert.AreEqual(new RationalTime(1, 48000), result.Timeline.SourceAudioTimeBase);
        Assert.AreEqual(new RationalTime(-11, 250), result.Timeline.ProbeReportedAudioStart);
        Assert.AreEqual(default(RationalTime), result.Timeline.FirstDecodedAudioFramePresentationTime);
        Assert.AreEqual(1024, result.Timeline.FirstDecodedAudioFrameSampleCount);
        Assert.AreEqual(1600L, result.Timeline.AnalysisSampleCount);
        Assert.AreEqual(new RationalTime(1, 10), result.Timeline.AnalysisDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), result.ExtractionElapsed);
        Assert.AreEqual(3L, fixture.CountArtifacts());
        Assert.IsTrue(File.Exists(fixture.GetFinalAudioPath()));
        Assert.IsTrue(File.Exists(fixture.GetFinalTimelinePath()));
        Assert.AreEqual(
            result.Timeline.CanonicalJson,
            File.ReadAllText(fixture.GetFinalTimelinePath(), Encoding.UTF8));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        CollectionAssert.AreEqual(
            fixture.SourceContent,
            File.ReadAllBytes(fixture.SourcePath));
        Assert.HasCount(2, fixture.ProcessRequests);
        Assert.AreEqual(fixture.FfprobePath, fixture.ProcessRequests[0].Executable);
        Assert.AreEqual(fixture.FfmpegPath, fixture.ProcessRequests[1].Executable);
    }

    [TestMethod]
    public async Task ExtractAsync_NoAudioProcessFailureAndTimeoutFailClosedAndClean()
    {
        using ExtractionFixture noAudio = new(includeAudio: false);
        AudioExtractionException noAudioFailure =
            await Assert.ThrowsExactlyAsync<AudioExtractionException>(
                () => noAudio.ExtractAsync());
        AssertSafeFailure(noAudioFailure, AudioExtractionFailureReason.NoAudioStream, false);
        Assert.IsEmpty(noAudio.ProcessRequests);
        Assert.AreEqual(1L, noAudio.CountArtifacts());

        using ExtractionFixture failed = new();
        failed.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            failed.ProcessRequests.Add(request);
            if (request.Executable == failed.FfprobePath)
            {
                return Task.FromResult(Result(
                    "stream_index=1|best_effort_timestamp=0|nb_samples=1024\n"));
            }

            File.WriteAllText(request.ArgumentList[^1], "private partial");
            return Task.FromResult(Result(
                string.Empty,
                exitCode: 1,
                standardError: failed.SourcePath));
        };
        AudioExtractionException processFailure =
            await Assert.ThrowsExactlyAsync<AudioExtractionException>(
                () => failed.ExtractAsync());
        AssertSafeFailure(
            processFailure,
            AudioExtractionFailureReason.ExtractionFailed,
            false,
            failed.SourcePath);
        Assert.IsFalse(Directory.Exists(failed.GetStagingDirectory()));
        Assert.AreEqual(1L, failed.CountArtifacts());

        using ExtractionFixture timedOut = new();
        timedOut.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            timedOut.ProcessRequests.Add(request);
            if (request.Executable == timedOut.FfprobePath)
            {
                return Task.FromResult(Result(
                    "stream_index=1|best_effort_timestamp=0|nb_samples=1024\n"));
            }

            throw new TimeoutException("private timeout");
        };
        AudioExtractionException timeoutFailure =
            await Assert.ThrowsExactlyAsync<AudioExtractionException>(
                () => timedOut.ExtractAsync());
        AssertSafeFailure(
            timeoutFailure,
            AudioExtractionFailureReason.ProcessTimedOut,
            false,
            "private timeout");
        Assert.IsFalse(Directory.Exists(timedOut.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task ExtractAsync_SecondPromotionFailureRequiresReconciliation()
    {
        using ExtractionFixture fixture = new();
        int promotions = 0;
        fixture.PromotionHandler = async (request, cancellationToken) =>
        {
            promotions++;
            if (promotions == 2)
            {
                throw new InvalidOperationException("private database failure");
            }

            return await fixture.Store.PromoteAsync(request, cancellationToken);
        };

        AudioExtractionException exception =
            await Assert.ThrowsExactlyAsync<AudioExtractionException>(
                () => fixture.ExtractAsync());

        AssertSafeFailure(
            exception,
            AudioExtractionFailureReason.PromotionFailed,
            true,
            "private database failure");
        Assert.AreEqual(2L, fixture.CountArtifacts());
        Assert.IsNotNull(fixture.Store.GetArtifact(fixture.AudioArtifactId));
        Assert.IsNull(fixture.Store.GetArtifact(fixture.TimelineArtifactId));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task ExtractAsync_ApprovedFfmpegSyntheticIntegrationWhenAvailable()
    {
        if (!TryFindCanonicalExecutable(
                "ffmpeg.exe",
                DecodeValidator.CanonicalFfmpegByteSize,
                DecodeValidator.CanonicalFfmpegSha256,
                out string ffmpegPath) ||
            !TryFindCanonicalExecutable(
                "ffprobe.exe",
                FfprobeClient.CanonicalFfprobeByteSize,
                FfprobeClient.CanonicalFfprobeSha256,
                out string ffprobePath))
        {
            Assert.Inconclusive("The pinned LGPL FFmpeg tools are unavailable.");
            return;
        }

        using StoreTestDatabase database = new();
        string workspace = Path.Combine(database.RootPath, "workspace");
        Directory.CreateDirectory(workspace);
        var store = new ArtifactStore(database.DatabasePath, workspace);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> sourceJobId = EntityId<JobIdTag>.New();
        EntityId<JobIdTag> analysisJobId = EntityId<JobIdTag>.New();
        EntityId<ArtifactIdTag> sourceArtifactId = EntityId<ArtifactIdTag>.New();
        EntityId<ArtifactIdTag> audioArtifactId = EntityId<ArtifactIdTag>.New();
        EntityId<ArtifactIdTag> timelineArtifactId = EntityId<ArtifactIdTag>.New();
        string sourceDirectory = Path.Combine(
            workspace,
            "projects",
            projectId.ToString(),
            "staging",
            "imports",
            sourceJobId.ToString());
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "source.mkv");
        var processRunner = new ProcessRunner();
        ProcessRunResult generated = await processRunner.RunAsync(
            new ProcessRunRequest(
                ffmpegPath,
                [
                    "-nostdin", "-v", "error",
                    "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=2",
                    "-itsoffset", "1",
                    "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=1",
                    "-map", "0:v:0", "-map", "1:a:0",
                    "-c:v", "ffv1", "-pix_fmt", "yuv420p",
                    "-c:a", "pcm_s16le", "-shortest", "-n", sourcePath,
                ],
                sourceDirectory,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);
        Assert.AreEqual(0, generated.ExitCode);
        Assert.AreEqual(string.Empty, generated.StandardOutput);
        Assert.AreEqual(string.Empty, generated.StandardError);

        byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        Asset sourceAsset = CreateAsset(
            EntityId<AssetIdTag>.New(),
            "source.mkv",
            ContentDigest.Compute(sourceBytes),
            sourceBytes.LongLength);
        var project = new Project(
            projectId,
            "Canonical audio extraction",
            "Verify A-002 with pinned tools.",
            "Test suite",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [sourceAsset],
            performers: []);
        new ProjectStore(database.DatabasePath).CreateProject(
            project,
            "{\"canonicalAudioExtraction\":true}",
            CreatedAt);
        var staged = new StagedMediaImport(
            sourceAsset.Id,
            projectId,
            sourceJobId,
            sourceAsset.FileName,
            sourcePath,
            sourceAsset.ContentDigest,
            sourceAsset.ByteSize,
            CreatedAt);
        var verifier = new BinaryVerifier();
        var ffprobe = new FfprobeClient(processRunner, verifier, ffprobePath);
        MediaProbeResult probe = await ffprobe.ProbeAsync(
            staged,
            TimeSpan.FromSeconds(30));
        var ptsIndexer = new PtsIndexer(processRunner, verifier, ffprobePath);
        PtsIndex pts = await ptsIndexer.IndexAsync(
            staged,
            probe,
            TimeSpan.FromSeconds(30));
        StoredArtifact original = await store.PromoteAsync(
            new ArtifactPromotionRequest(
                sourceArtifactId,
                projectId,
                ArtifactKind.Original,
                sourcePath,
                $"{sourceAsset.Id}/{sourceAsset.ContentDigest.Hex}.original",
                sourceAsset.ContentDigest,
                sourceAsset.ByteSize,
                CreatedAt));
        var extraction = new AudioExtraction(
            store,
            processRunner,
            verifier,
            ffprobe,
            workspace,
            ffmpegPath,
            ffprobePath);

        ExtractedAnalysisAudio result = await extraction.ExtractAsync(
            audioArtifactId,
            timelineArtifactId,
            sourceAsset,
            original,
            pts,
            analysisJobId,
            TimeSpan.FromSeconds(30));

        Assert.AreEqual(16000, result.Timeline.AnalysisSampleRate);
        Assert.IsGreaterThan(15000, result.Timeline.AnalysisSampleCount);
        Assert.IsLessThanOrEqualTo(16000, result.Timeline.AnalysisSampleCount);
        Assert.AreEqual(
            new RationalTime(1, 1),
            result.Timeline.FirstDecodedAudioFramePresentationTime);
        Assert.AreEqual(
            new RationalTime(1, 1),
            result.Timeline.MapAnalysisSampleToSourceTime(0));
        Assert.AreEqual(3L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM artifacts;"));
        Assert.AreEqual(
            sourceAsset.ContentDigest,
            ContentDigest.Compute(await File.ReadAllBytesAsync(
                store.GetValidatedRegisteredArtifactPath(sourceArtifactId))));
    }

    [TestMethod]
    public void Schema_IsClosedPathFreeAndMatchesCanonicalRuntimeShape()
    {
        string schemaPath = ResolveRepositoryPath(
            "schemas/analysis-audio-timeline.schema.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        JsonElement root = document.RootElement;
        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual("AnalysisAudioTimeline", root.GetProperty("title").GetString());
        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        string[] required = root.GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        string[] properties = root.GetProperty("properties")
            .EnumerateObject()
            .Select(static value => value.Name)
            .ToArray();
        CollectionAssert.AreEquivalent(required, properties);
        Assert.AreEqual(24, required.Length);
        Assert.IsFalse(properties.Any(static name =>
            name.Contains("path", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(16000, root
            .GetProperty("properties")
            .GetProperty("analysisSampleRate")
            .GetProperty("const")
            .GetInt32());
        Assert.IsFalse(root
            .GetProperty("properties")
            .GetProperty("probeReportedPacketStartApplied")
            .GetProperty("const")
            .GetBoolean());
    }

    [TestMethod]
    public void PublicSurface_IsImmutablePathPrivateAndMinimal()
    {
        foreach (Type type in new[]
        {
            typeof(AnalysisAudioTimeline),
            typeof(ExtractedAnalysisAudio),
            typeof(AudioExtractionException),
            typeof(AudioExtraction),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null));
        }

        Assert.AreEqual(0, typeof(AnalysisAudioTimeline).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ExtractedAnalysisAudio).GetConstructors().Length);
        CollectionAssert.AreEqual(
            new[] { "ExtractAsync" },
            typeof(AudioExtraction)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsFalse(typeof(AnalysisAudioTimeline)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)));

        foreach (AudioExtractionFailureReason reason in
            Enum.GetValues<AudioExtractionFailureReason>())
        {
            var exception = new AudioExtractionException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.ToString().Contains(
                @"C:\private\customer.mov",
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static AnalysisAudioTimeline CreateTimeline(
        long firstDecodedTimestamp = 0,
        RationalTime? sourceAudioTimeBase = null,
        RationalTime? probeStart = null,
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
            probeStart,
            firstDecodedTimestamp,
            firstDecodedAudioFrameSampleCount: 1024,
            EntityId<ArtifactIdTag>.New(),
            new ContentDigest(new string('c', 64)),
            analysisAudioByteSize: checked(44 + (sampleCount * sizeof(short))),
            sampleCount,
            EntityId<ArtifactIdTag>.New());
    }

    private static ProcessRunResult Result(
        string output,
        int exitCode = 0,
        string standardError = "",
        bool truncated = false,
        TimeSpan? duration = null) =>
        new(
            exitCode,
            output,
            standardError,
            truncated,
            duration ?? TimeSpan.Zero);

    private static string WriteWave(
        string path,
        int sampleCount,
        int channels = 1,
        int sampleRate = 16000,
        int bitsPerSample = 16,
        bool appendExtraChunk = false)
    {
        int bytesPerSample = bitsPerSample / 8;
        int dataSize = checked(sampleCount * channels * bytesPerSample);
        int extraSize = appendExtraChunk ? 8 : 0;
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write("RIFF"u8);
        writer.Write(checked((uint)(36 + dataSize + extraSize)));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * channels * bytesPerSample));
        writer.Write((ushort)(channels * bytesPerSample));
        writer.Write((ushort)bitsPerSample);
        writer.Write("data"u8);
        writer.Write((uint)dataSize);
        writer.Write(new byte[dataSize]);
        if (appendExtraChunk)
        {
            writer.Write("JUNK"u8);
            writer.Write(0u);
        }

        return path;
    }

    private static void AssertSafeFailure(
        AudioExtractionException exception,
        AudioExtractionFailureReason reason,
        bool reconciliationRequired,
        params string[] forbidden)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string value in forbidden)
        {
            Assert.IsFalse(exception.ToString().Contains(
                value,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static bool TryFindCanonicalExecutable(
        string fileName,
        long expectedByteSize,
        string expectedSha256,
        out string executablePath)
    {
        executablePath = string.Empty;
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
                    fileName,
                    SearchOption.AllDirectories))
                {
                    var info = new FileInfo(candidate);
                    if (info.Length != expectedByteSize)
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(candidate);
                    string digest = Convert.ToHexString(SHA256.HashData(stream))
                        .ToLowerInvariant();
                    if (string.Equals(digest, expectedSha256, StringComparison.Ordinal))
                    {
                        executablePath = Path.GetFullPath(candidate);
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

    private sealed class ExtractionFixture : IDisposable
    {
        private readonly FixedTimeProvider _timeProvider = new(CreatedAt);

        internal ExtractionFixture(bool includeAudio = true)
        {
            Database = new StoreTestDatabase();
            WorkspacePath = Path.Combine(Database.RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            Store = new ArtifactStore(Database.DatabasePath, WorkspacePath);
            ProjectId = EntityId<ProjectIdTag>.New();
            SourceJobId = EntityId<JobIdTag>.New();
            AnalysisJobId = EntityId<JobIdTag>.New();
            SourceArtifactId = EntityId<ArtifactIdTag>.New();
            AudioArtifactId = EntityId<ArtifactIdTag>.New();
            TimelineArtifactId = EntityId<ArtifactIdTag>.New();
            SourceContent = Encoding.ASCII.GetBytes("source-original-content");
            SourceAsset = CreateAsset(
                EntityId<AssetIdTag>.New(),
                "source.mov",
                ContentDigest.Compute(SourceContent),
                SourceContent.LongLength);
            var project = new Project(
                ProjectId,
                "Audio extraction test",
                "Verify exact decoded audio mapping.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [SourceAsset],
                performers: []);
            new ProjectStore(Database.DatabasePath).CreateProject(
                project,
                "{\"audioExtraction\":true}",
                CreatedAt);
            string stagedPath = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "imports",
                SourceJobId.ToString(),
                "source.ready");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            File.WriteAllBytes(stagedPath, SourceContent);
            SourceOriginal = Store.PromoteAsync(
                new ArtifactPromotionRequest(
                    SourceArtifactId,
                    ProjectId,
                    ArtifactKind.Original,
                    stagedPath,
                    $"{SourceAsset.Id}/{SourceAsset.ContentDigest.Hex}.original",
                    SourceAsset.ContentDigest,
                    SourceAsset.ByteSize,
                    CreatedAt),
                CancellationToken.None).GetAwaiter().GetResult();
            SourcePath = Store.GetValidatedRegisteredArtifactPath(SourceArtifactId);
            SourcePtsIndex = new PtsIndex(
                SourceAsset.Id,
                ProjectId,
                SourceJobId,
                SourceAsset.FileName,
                SourceAsset.ContentDigest,
                SourceAsset.ByteSize,
                streamIndex: 0,
                new RationalTime(1, 1000),
                [
                    new PtsIndexEntry(0, default, new RationalTime(1, 50), keyFrame: true),
                    new PtsIndexEntry(1, new RationalTime(1, 50), new RationalTime(1, 50), keyFrame: false),
                ]);
            SourceProbe = CreateProbe(includeAudio);
            FfmpegPath = Path.Combine(Database.RootPath, "ffmpeg.exe");
            FfprobePath = Path.Combine(Database.RootPath, "ffprobe.exe");
            ProcessHandler = DefaultProcessAsync;
            PromotionHandler = Store.PromoteAsync;
        }

        internal StoreTestDatabase Database { get; }

        internal string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<JobIdTag> SourceJobId { get; }

        internal EntityId<JobIdTag> AnalysisJobId { get; }

        internal EntityId<ArtifactIdTag> SourceArtifactId { get; }

        internal EntityId<ArtifactIdTag> AudioArtifactId { get; }

        internal EntityId<ArtifactIdTag> TimelineArtifactId { get; }

        internal Asset SourceAsset { get; }

        internal StoredArtifact SourceOriginal { get; }

        internal PtsIndex SourcePtsIndex { get; }

        internal MediaProbeResult SourceProbe { get; }

        internal byte[] SourceContent { get; }

        internal string SourcePath { get; }

        internal string FfmpegPath { get; }

        internal string FfprobePath { get; }

        internal List<ProcessRunRequest> ProcessRequests { get; } = [];

        internal Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
            ProcessHandler
        { get; set; }

        internal Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>
            PromotionHandler
        { get; set; }

        internal Task<ExtractedAnalysisAudio> ExtractAsync(
            CancellationToken cancellationToken = default) =>
            CreateExtraction().ExtractAsync(
                AudioArtifactId,
                TimelineArtifactId,
                SourceAsset,
                SourceOriginal,
                SourcePtsIndex,
                AnalysisJobId,
                TimeSpan.FromSeconds(5),
                cancellationToken);

        internal string GetStagingDirectory() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "analysis-audio",
                AudioArtifactId.ToString());

        internal string GetFinalAudioPath() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "analysis",
                SourceAsset.Id.ToString(),
                $"{AudioArtifactId}.analysis.wav");

        internal string GetFinalTimelinePath() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "analysis",
                SourceAsset.Id.ToString(),
                $"{TimelineArtifactId}.audio-timeline.json");

        internal long CountArtifacts() =>
            Database.ExecuteScalarInt64("SELECT COUNT(*) FROM artifacts;");

        private AudioExtraction CreateExtraction() =>
            new(
                Store,
                ProcessHandler,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(BinaryVerificationResult.Verified(
                        request,
                        request.ExpectedByteSize,
                        request.ExpectedDigest));
                },
                (media, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assert.AreEqual(SourcePath, media.StagingPath);
                    return Task.FromResult(SourceProbe);
                },
                (artifactId, cancellationToken) =>
                    Store.VerifyRegisteredAsync(
                        artifactId,
                        new BinaryVerifier(),
                        cancellationToken),
                PromotionHandler,
                WorkspacePath,
                FfmpegPath,
                FfprobePath,
                _timeProvider);

        private Task<ProcessRunResult> DefaultProcessAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRequests.Add(request);
            if (request.Executable == FfprobePath)
            {
                return Task.FromResult(Result(
                    "stream_index=1|best_effort_timestamp=0|nb_samples=1024\n" +
                    "stream_index=1|best_effort_timestamp=1024|nb_samples=1024\n"));
            }

            WriteWave(request.ArgumentList[^1], sampleCount: 1600);
            return Task.FromResult(Result(
                string.Empty,
                duration: TimeSpan.FromMilliseconds(7)));
        }

        private MediaProbeResult CreateProbe(bool includeAudio)
        {
            var video = new ProbedMediaStream(
                0,
                MediaStreamKind.Video,
                "h264",
                "Main",
                640,
                360,
                "yuv420p",
                new RationalNumber(50, 1),
                new RationalNumber(50, 1),
                new RationalNumber(1, 1000),
                0m,
                2m,
                null,
                null,
                null);
            List<ProbedMediaStream> streams = [video];
            List<string> warnings = [];
            if (includeAudio)
            {
                streams.Add(new ProbedMediaStream(
                    1,
                    MediaStreamKind.Audio,
                    "aac",
                    "LC",
                    null,
                    null,
                    null,
                    null,
                    null,
                    new RationalNumber(1, 48000),
                    -0.044m,
                    2m,
                    48000,
                    2,
                    "stereo"));
            }
            else
            {
                warnings.Add("audio-track-absent");
            }

            return new MediaProbeResult(
                SourceAsset.Id,
                ProjectId,
                SourceJobId,
                SourceAsset.FileName,
                SourceAsset.ContentDigest,
                SourceAsset.ByteSize,
                ContainerSignature.IsoBaseMediaFtyp,
                ["mov"],
                "QuickTime",
                2m,
                SourceAsset.ByteSize,
                null,
                null,
                streams,
                warnings);
        }

        public void Dispose() => Database.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                string.Concat("hubradio-a002-", Guid.NewGuid().ToString("N")));
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

    private static Asset CreateAsset(
        EntityId<AssetIdTag> id,
        string fileName,
        ContentDigest digest,
        long byteSize) =>
        new(
            id,
            fileName,
            "video/quicktime",
            digest,
            byteSize,
            new RightsRecord(
                RightsStatus.Unconfirmed,
                evidenceReference: null,
                "Internal test processing",
                externalPublicationAllowed: false));
}