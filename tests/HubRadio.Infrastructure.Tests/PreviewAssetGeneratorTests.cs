using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class PreviewAssetGeneratorTests
{
    [TestMethod]
    public async Task GenerateAsync_CreatesFrameAlignedChannelWaveformAndKeyFrameSprite()
    {
        using PreviewFixture fixture = new(includeAudio: true);

        GeneratedPreviewAssets result = await fixture.GenerateAsync();

        Assert.AreEqual(8000, result.Profile.WaveformSamplesPerSecond);
        Assert.AreEqual(160, result.Profile.SpriteCellWidth);
        Assert.AreEqual(90, result.Profile.SpriteCellHeight);
        Assert.AreEqual(100, result.Profile.MaximumSpriteCells);
        Assert.AreEqual(10, result.Profile.MaximumSpriteColumns);
        Assert.AreEqual(2, result.WaveformIndex.ChannelCount);
        Assert.AreEqual(8000, result.WaveformIndex.SampleRate);
        Assert.AreEqual("stereo", result.WaveformIndex.ChannelLayout);
        Assert.AreEqual(4, result.WaveformIndex.Frames.Count);
        for (int index = 0; index < result.WaveformIndex.Frames.Count; index++)
        {
            WaveformFrame frame = result.WaveformIndex.Frames[index];
            PtsIndexEntry expected = fixture.ProxyPtsIndex.Entries[index];
            Assert.AreEqual(expected.Sequence, frame.ProxyFrameSequence);
            Assert.AreEqual(expected.PresentationTime, frame.ProxyPresentationTime);
            Assert.AreEqual(expected.Duration, frame.ProxyDuration);
            Assert.AreEqual(2, frame.Channels.Count);
            Assert.AreEqual(index * 100 - 5, frame.Channels[0].MinimumSample);
            Assert.AreEqual(index * 100 + 5, frame.Channels[0].MaximumSample);
            Assert.AreEqual(index * 100 + 5, frame.Channels[1].MinimumSample);
            Assert.AreEqual(index * 100 + 15, frame.Channels[1].MaximumSample);
        }

        Assert.AreEqual(2, result.SpriteIndex.Cells.Count);
        Assert.AreEqual(2, result.SpriteIndex.Columns);
        Assert.AreEqual(1, result.SpriteIndex.Rows);
        Assert.AreEqual(0, result.SpriteIndex.Cells[0].ProxyFrameSequence);
        Assert.AreEqual(2, result.SpriteIndex.Cells[1].ProxyFrameSequence);
        Assert.AreEqual(Time(0), result.SpriteIndex.Cells[0].ProxyPresentationTime);
        Assert.AreEqual(Time(1001, 15000), result.SpriteIndex.Cells[1].ProxyPresentationTime);
        Assert.AreEqual(0, result.SpriteIndex.Cells[0].Column);
        Assert.AreEqual(1, result.SpriteIndex.Cells[1].Column);
        Assert.AreEqual(0, result.SpriteIndex.Cells[1].Row);

        ContentDigest ptsDigest = ContentDigest.Compute(
            Encoding.UTF8.GetBytes(fixture.ProxyPtsIndex.CanonicalJson));
        Assert.AreEqual(ptsDigest, result.WaveformIndex.ProxyPtsIndexDigest);
        Assert.AreEqual(ptsDigest, result.SpriteIndex.ProxyPtsIndexDigest);
        Assert.AreEqual(
            result.SpriteImageArtifact.ContentDigest,
            result.SpriteIndex.SpriteImageDigest);
        Assert.AreEqual(
            result.SpriteImageArtifact.Id,
            result.SpriteIndex.SpriteImageArtifactId);
        Assert.AreEqual(ArtifactKind.Preview, result.WaveformIndexArtifact.Kind);
        Assert.AreEqual(ArtifactKind.Preview, result.SpriteImageArtifact.Kind);
        Assert.AreEqual(ArtifactKind.Preview, result.SpriteIndexArtifact.Kind);
        Assert.AreEqual(4L, fixture.CountArtifacts());
        Assert.AreEqual(2, fixture.ProcessRequests.Count);
        Assert.IsTrue(fixture.ProcessRequests[0].ArgumentList.Contains(
            "pcm_s16le",
            StringComparer.Ordinal));
        Assert.IsTrue(fixture.ProcessRequests[1].ArgumentList.Contains(
            "png",
            StringComparer.Ordinal));
        Assert.IsFalse(fixture.ProcessRequests.SelectMany(static request =>
            request.ArgumentList).Contains("-y", StringComparer.Ordinal));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        CollectionAssert.AreEqual(
            fixture.ProxyContent,
            File.ReadAllBytes(fixture.ProxyPath));

        AssertArtifactContent(
            fixture,
            result.WaveformIndexArtifact,
            Encoding.UTF8.GetBytes(result.WaveformIndex.CanonicalJson));
        AssertArtifactContent(
            fixture,
            result.SpriteIndexArtifact,
            Encoding.UTF8.GetBytes(result.SpriteIndex.CanonicalJson));
        Assert.AreEqual(
            result.SpriteImageArtifact.ContentDigest,
            ContentDigest.Compute(File.ReadAllBytes(
                fixture.Store.GetValidatedRegisteredArtifactPath(
                    result.SpriteImageArtifact.Id))));
    }

    [TestMethod]
    public async Task GenerateAsync_NoAudioCreatesZeroChannelTimelineWithoutInventingSamples()
    {
        using PreviewFixture fixture = new(includeAudio: false);

        GeneratedPreviewAssets result = await fixture.GenerateAsync();

        Assert.AreEqual(0, result.WaveformIndex.SampleRate);
        Assert.AreEqual(0, result.WaveformIndex.ChannelCount);
        Assert.IsNull(result.WaveformIndex.ChannelLayout);
        Assert.AreEqual(fixture.ProxyPtsIndex.Entries.Count, result.WaveformIndex.Frames.Count);
        Assert.IsTrue(result.WaveformIndex.Frames.All(static frame =>
            frame.Channels.Count == 0));
        Assert.AreEqual(1, fixture.ProcessRequests.Count);
        Assert.IsTrue(fixture.ProcessRequests[0].ArgumentList.Contains(
            "png",
            StringComparer.Ordinal));
        Assert.IsFalse(fixture.ProcessRequests[0].ArgumentList.Contains(
            "pcm_s16le",
            StringComparer.Ordinal));
        Assert.AreEqual(4L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public void ProcessRequests_AreExactDirectAllowlistedAndNoOverwrite()
    {
        using PreviewFixture fixture = new();
        string waveformPath = Path.Combine(fixture.Database.RootPath, "waveform.s16le");
        string spritePath = Path.Combine(fixture.Database.RootPath, "sprite.png");
        TimeSpan timeout = TimeSpan.FromMilliseconds(4321);

        ProcessRunRequest waveform = PreviewAssetGenerator.CreateWaveformProcessRequest(
            fixture.FfmpegPath,
            fixture.ProxyPath,
            waveformPath,
            audioStreamIndex: 1,
            channelCount: 2,
            channelLayout: "stereo",
            sampleCount: fixture.ExpectedSampleCount,
            timeout);
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin",
                "-v",
                "error",
                "-i",
                fixture.ProxyPath,
                "-map",
                "0:1",
                "-vn",
                "-af",
                $"aresample=8000:async=1:first_pts=0,apad=whole_len={fixture.ExpectedSampleCount},atrim=end_sample={fixture.ExpectedSampleCount}",
                "-ac",
                "2",
                "-channel_layout",
                "stereo",
                "-ar",
                "8000",
                "-c:a",
                "pcm_s16le",
                "-f",
                "s16le",
                "-n",
                waveformPath,
            },
            waveform.ArgumentList.ToArray());

        ProcessRunRequest sprite = PreviewAssetGenerator.CreateSpriteProcessRequest(
            fixture.FfmpegPath,
            fixture.ProxyPath,
            spritePath,
            videoStreamIndex: 0,
            [0, 2],
            columns: 2,
            rows: 1,
            timeout);
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin",
                "-v",
                "error",
                "-i",
                fixture.ProxyPath,
                "-map",
                "0:0",
                "-vf",
                "select=eq(n\\,0)+eq(n\\,2),scale=160:90:flags=bilinear,tile=2x1:nb_frames=2:padding=0:margin=0",
                "-frames:v",
                "1",
                "-an",
                "-c:v",
                "png",
                "-f",
                "image2",
                "-n",
                spritePath,
            },
            sprite.ArgumentList.ToArray());

        foreach (ProcessRunRequest request in new[] { waveform, sprite })
        {
            Assert.AreEqual(fixture.FfmpegPath, request.Executable);
            Assert.AreEqual(fixture.Database.RootPath, request.WorkingDirectory);
            Assert.AreEqual(timeout, request.Timeout);
            Assert.AreEqual(
                ProcessRunRequest.DefaultMaxCapturedBytes,
                request.MaxCapturedBytes);
            Assert.IsNull(request.StandardInput);
            Assert.IsFalse(request.ArgumentList.Contains("-y", StringComparer.Ordinal));
            Assert.IsFalse(request.ArgumentList.Any(static argument =>
                argument.Contains(";", StringComparison.Ordinal) ||
                argument.Contains("&&", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void SelectSpriteFrames_UsesOnlyKeyFramesAndBoundsLongInputsDeterministically()
    {
        PtsIndex index = PreviewFixture.CreateProxyPtsIndex(
            entryCount: 201,
            keyFrameSequences: Enumerable.Range(0, 201).ToArray());

        IReadOnlyList<PtsIndexEntry> first =
            PreviewAssetGenerator.SelectSpriteFrames(index);
        IReadOnlyList<PtsIndexEntry> second =
            PreviewAssetGenerator.SelectSpriteFrames(index);

        Assert.AreEqual(100, first.Count);
        Assert.AreEqual(0, first[0].Sequence);
        Assert.AreEqual(200, first[^1].Sequence);
        Assert.IsTrue(first.All(static entry => entry.KeyFrame));
        Assert.IsTrue(first.Zip(first.Skip(1)).All(static pair =>
            pair.First.Sequence < pair.Second.Sequence));
        CollectionAssert.AreEqual(
            first.Select(static entry => entry.Sequence).ToArray(),
            second.Select(static entry => entry.Sequence).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<PtsIndexEntry>)first).Clear());
    }

    [TestMethod]
    public async Task GenerateAsync_ProcessFailureCleansOwnedStagingAndDoesNotRegisterOutput()
    {
        using PreviewFixture fixture = new();
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.ProcessRequests.Add(request);
            return Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                @"C:\private\customer-proxy.mp4",
                Truncated: false,
                TimeSpan.Zero));
        };

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            PreviewAssetGenerationFailureReason.WaveformGenerationFailed,
            reconciliationRequired: false);
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        CollectionAssert.AreEqual(
            fixture.ProxyContent,
            File.ReadAllBytes(fixture.ProxyPath));
    }

    [TestMethod]
    public async Task GenerateAsync_ProcessTimeoutRemainsTypedAndCleansOwnedStaging()
    {
        using PreviewFixture fixture = new();
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.ProcessRequests.Add(request);
            throw new TimeoutException("private preview timeout detail");
        };

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            PreviewAssetGenerationFailureReason.ProcessTimedOut,
            reconciliationRequired: false);
        Assert.IsFalse(exception.ToString().Contains(
            "private preview timeout detail",
            StringComparison.Ordinal));
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task GenerateAsync_CorruptPngCrcIsRejectedBeforeAnyPromotion()
    {
        using PreviewFixture fixture = new();
        fixture.CorruptSpriteCrc = true;

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            PreviewAssetGenerationFailureReason.SpriteOutputInvalid,
            reconciliationRequired: false);
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task GenerateAsync_CrcValidButInvalidCompressedPngIsRejected()
    {
        using PreviewFixture fixture = new();
        fixture.CorruptSpriteImageData = true;

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            PreviewAssetGenerationFailureReason.SpriteOutputInvalid,
            reconciliationRequired: false);
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task GenerateAsync_CrcValidOversizedPngImageDataIsRejected()
    {
        using PreviewFixture fixture = new();
        fixture.OversizedSpriteImageData = true;

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            PreviewAssetGenerationFailureReason.SpriteOutputInvalid,
            reconciliationRequired: false);
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    [DataRow("pcm-size")]
    [DataRow("png-dimensions")]
    public async Task GenerateAsync_RejectsMismatchedGeneratedOutputBeforePromotion(
        string mutation)
    {
        using PreviewFixture fixture = new();
        fixture.TruncateWaveform = mutation == "pcm-size";
        fixture.WrongSpriteDimensions = mutation == "png-dimensions";

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            mutation == "pcm-size"
                ? PreviewAssetGenerationFailureReason.WaveformOutputInvalid
                : PreviewAssetGenerationFailureReason.SpriteOutputInvalid,
            reconciliationRequired: false);
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task GenerateAsync_PartialPromotionRequiresReconciliationAndLeavesCommittedEvidence()
    {
        using PreviewFixture fixture = new();
        int promotionCount = 0;
        fixture.PromoteHandler = async (request, cancellationToken) =>
        {
            promotionCount++;
            if (promotionCount == 2)
            {
                throw new IOException("Injected second promotion failure.");
            }

            return await fixture.Store.PromoteAsync(request, cancellationToken);
        };

        PreviewAssetGenerationException exception =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            PreviewAssetGenerationFailureReason.PromotionFailed,
            reconciliationRequired: true);
        Assert.AreEqual(2, promotionCount);
        Assert.AreEqual(2L, fixture.CountArtifacts());
        Assert.IsNotNull(fixture.Store.GetArtifact(fixture.SpriteImageArtifactId));
        Assert.IsNull(fixture.Store.GetArtifact(fixture.SpriteIndexArtifactId));
        Assert.IsNull(fixture.Store.GetArtifact(fixture.WaveformIndexArtifactId));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task GenerateAsync_CancellationAfterCommitBarrierCompletesAllPromotions()
    {
        using PreviewFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        int promotionCount = 0;
        fixture.PromoteHandler = async (request, token) =>
        {
            Assert.AreEqual(CancellationToken.None, token);
            promotionCount++;
            if (promotionCount == 1)
            {
                cancellation.Cancel();
            }

            return await fixture.Store.PromoteAsync(request, token);
        };

        GeneratedPreviewAssets result = await fixture.GenerateAsync(
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(3, promotionCount);
        Assert.AreEqual(4L, fixture.CountArtifacts());
        Assert.IsNotNull(result.WaveformIndexArtifact);
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
    }

    [TestMethod]
    public async Task GenerateAsync_RejectsDuplicateIdsAndNonzeroOrIrregularProxyTimelineBeforeProcess()
    {
        using PreviewFixture fixture = new();
        PreviewAssetGenerationException duplicate =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.Generator.GenerateAsync(
                    fixture.ProxyArtifact.Id,
                    fixture.SpriteImageArtifactId,
                    fixture.SpriteIndexArtifactId,
                    fixture.GeneratedProxy,
                    TimeSpan.FromSeconds(5)));
        AssertSafeFailure(
            duplicate,
            PreviewAssetGenerationFailureReason.InputBindingMismatch,
            reconciliationRequired: false);

        PtsIndex nonzero = PreviewFixture.CreateProxyPtsIndex(
            presentationOffset: Time(1001, 30000),
            candidateId: fixture.SourceAssetId,
            projectId: fixture.ProjectId,
            jobId: fixture.ProxyJobId,
            contentDigest: fixture.ProxyArtifact.ContentDigest,
            byteSize: fixture.ProxyArtifact.ByteSize);
        GeneratedProxy nonzeroProxy = fixture.CreateGeneratedProxy(nonzero);
        PreviewAssetGenerationException timing =
            await Assert.ThrowsExactlyAsync<PreviewAssetGenerationException>(() =>
                fixture.Generator.GenerateAsync(
                    EntityId<ArtifactIdTag>.New(),
                    EntityId<ArtifactIdTag>.New(),
                    EntityId<ArtifactIdTag>.New(),
                    nonzeroProxy,
                    TimeSpan.FromSeconds(5)));
        AssertSafeFailure(
            timing,
            PreviewAssetGenerationFailureReason.InputBindingMismatch,
            reconciliationRequired: false);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_PreCancellationAndInvalidTimeoutPrecedeToolOrFileWork()
    {
        using PreviewFixture fixture = new();
        ArgumentOutOfRangeException timeout =
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                fixture.Generator.GenerateAsync(
                    fixture.WaveformIndexArtifactId,
                    fixture.SpriteImageArtifactId,
                    fixture.SpriteIndexArtifactId,
                    fixture.GeneratedProxy,
                    TimeSpan.Zero));
        Assert.AreEqual("timeout", timeout.ParamName);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException canceled =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.GenerateAsync(cancellation.Token));
        Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public void PublicSurface_IsMinimalImmutablePathPrivateAndFailuresStable()
    {
        foreach (Type type in new[]
        {
            typeof(PreviewAssetGenerationProfile),
            typeof(WaveformChannelExtrema),
            typeof(WaveformFrame),
            typeof(PreviewWaveformIndex),
            typeof(ThumbnailSpriteCell),
            typeof(ThumbnailSpriteIndex),
            typeof(GeneratedPreviewAssets),
            typeof(PreviewAssetGenerationException),
            typeof(PreviewAssetGenerator),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null),
                type.Name);
        }

        CollectionAssert.AreEqual(
            new[] { "GenerateAsync" },
            typeof(PreviewAssetGenerator)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.AreEqual(0, typeof(PreviewAssetGenerationProfile).GetConstructors().Length);
        Assert.AreEqual(0, typeof(WaveformChannelExtrema).GetConstructors().Length);
        Assert.AreEqual(0, typeof(WaveformFrame).GetConstructors().Length);
        Assert.AreEqual(0, typeof(PreviewWaveformIndex).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ThumbnailSpriteCell).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ThumbnailSpriteIndex).GetConstructors().Length);
        Assert.AreEqual(0, typeof(GeneratedPreviewAssets).GetConstructors().Length);
        Assert.AreEqual(0, typeof(PreviewAssetGenerationException).GetConstructors().Length);
        Assert.IsFalse(typeof(GeneratedPreviewAssets)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property => property.Name.Contains(
                "Path",
                StringComparison.OrdinalIgnoreCase)));

        const string privatePath = @"C:\private\customer-proxy.mp4";
        foreach (PreviewAssetGenerationFailureReason reason in
            Enum.GetValues<PreviewAssetGenerationFailureReason>())
        {
            var exception = new PreviewAssetGenerationException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.ToString().Contains(
                privatePath,
                StringComparison.OrdinalIgnoreCase));
        }

        using PreviewFixture fixture = new();
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new PreviewAssetGenerator(
                fixture.Store,
                null!,
                new BinaryVerifier(),
                fixture.WorkspacePath,
                fixture.FfmpegPath));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new PreviewAssetGenerator(
                fixture.Store,
                new ProcessRunner(),
                null!,
                fixture.WorkspacePath,
                fixture.FfmpegPath));
    }

    [TestMethod]
    public async Task GenerateAsync_ApprovedFfmpegSyntheticIntegrationWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() ||
            !TryFindCanonicalExecutable(
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
            Assert.Inconclusive("The approved FFmpeg package is unavailable.");
            return;
        }

        using StoreTestDatabase database = new();
        string workspacePath = Path.Combine(database.RootPath, "workspace");
        Directory.CreateDirectory(workspacePath);
        ArtifactStore store = new(database.DatabasePath, workspacePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<AssetIdTag> sourceAssetId = EntityId<AssetIdTag>.New();
        EntityId<JobIdTag> proxyJobId = EntityId<JobIdTag>.New();
        EntityId<ArtifactIdTag> proxyArtifactId = EntityId<ArtifactIdTag>.New();
        DateTimeOffset createdAt = new(2038, 9, 10, 11, 12, 13, TimeSpan.Zero);
        var sourceAsset = new Asset(
            sourceAssetId,
            "synthetic-source.mkv",
            "video/x-matroska",
            new ContentDigest(new string('a', 64)),
            byteSize: 1024,
            new RightsRecord(
                RightsStatus.Unconfirmed,
                evidenceReference: null,
                "Internal synthetic control",
                externalPublicationAllowed: false));
        new ProjectStore(database.DatabasePath).CreateProject(
            new Project(
                projectId,
                "M-010 canonical integration",
                "Verify approved preview generation filters.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [sourceAsset],
                performers: []),
            "{\"previewIntegration\":true}",
            createdAt);

        string stagingDirectory = Path.Combine(
            workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "integration");
        Directory.CreateDirectory(stagingDirectory);
        string stagedProxyPath = Path.Combine(stagingDirectory, "proxy.mp4");
        ProcessRunner processRunner = new();
        ProcessRunResult generation = await processRunner.RunAsync(
            new ProcessRunRequest(
                ffmpegPath,
                [
                    "-nostdin",
                    "-v",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=640x360:rate=30000/1001:duration=2",
                    "-f",
                    "lavfi",
                    "-i",
                    "sine=frequency=1000:sample_rate=48000:duration=2",
                    "-map",
                    "0:v:0",
                    "-map",
                    "1:a:0",
                    "-c:v",
                    "libopenh264",
                    "-b:v",
                    "1200k",
                    "-g",
                    "30",
                    "-pix_fmt",
                    "yuv420p",
                    "-c:a",
                    "aac",
                    "-b:a",
                    "96k",
                    "-shortest",
                    "-n",
                    stagedProxyPath,
                ],
                stagingDirectory,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);
        if (generation.ExitCode != 0 ||
            generation.Truncated ||
            generation.StandardOutput.Length != 0 ||
            generation.StandardError.Length != 0)
        {
            Assert.Inconclusive(
                "The approved build cannot create the software-only synthetic proxy control.");
            return;
        }

        byte[] proxyBytes = await File.ReadAllBytesAsync(stagedProxyPath);
        ContentDigest proxyDigest = ContentDigest.Compute(proxyBytes);
        var staged = new StagedMediaImport(
            sourceAssetId,
            projectId,
            proxyJobId,
            "proxy.mp4",
            stagedProxyPath,
            proxyDigest,
            proxyBytes.LongLength,
            createdAt);
        BinaryVerifier verifier = new();
        FfprobeClient probeClient = new(processRunner, verifier, ffprobePath);
        MediaProbeResult probe = await probeClient.ProbeAsync(
            staged,
            TimeSpan.FromSeconds(30));
        PtsIndexer indexer = new(processRunner, verifier, ffprobePath);
        PtsIndex ptsIndex = await indexer.IndexAsync(
            staged,
            probe,
            TimeSpan.FromSeconds(30));
        StoredArtifact proxyArtifact = await store.PromoteAsync(
            new ArtifactPromotionRequest(
                proxyArtifactId,
                projectId,
                ArtifactKind.Proxy,
                stagedProxyPath,
                $"{sourceAssetId}/{proxyArtifactId}.mp4",
                proxyDigest,
                proxyBytes.LongLength,
                createdAt));
        var generatedProxy = new GeneratedProxy(
            sourceAssetId,
            sourceAsset.ContentDigest,
            EntityId<ArtifactIdTag>.New(),
            proxyArtifact,
            probe,
            ptsIndex,
            new ProxyGenerationProfile(),
            generation.Duration);
        var generator = new PreviewAssetGenerator(
            store,
            processRunner,
            verifier,
            workspacePath,
            ffmpegPath);

        GeneratedPreviewAssets result = await generator.GenerateAsync(
            EntityId<ArtifactIdTag>.New(),
            EntityId<ArtifactIdTag>.New(),
            EntityId<ArtifactIdTag>.New(),
            generatedProxy,
            TimeSpan.FromSeconds(30));

        Assert.AreEqual(ptsIndex.Entries.Count, result.WaveformIndex.Frames.Count);
        Assert.AreEqual(1, result.WaveformIndex.ChannelCount);
        Assert.AreEqual(8000, result.WaveformIndex.SampleRate);
        Assert.IsGreaterThanOrEqualTo(2, result.SpriteIndex.Cells.Count);
        Assert.IsTrue(result.SpriteIndex.Cells.All(cell =>
            ptsIndex.Entries[cell.ProxyFrameSequence].KeyFrame &&
            ptsIndex.Entries[cell.ProxyFrameSequence].PresentationTime ==
                cell.ProxyPresentationTime));
        Assert.AreEqual(4L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM artifacts;"));
        Assert.IsFalse(Directory.Exists(Path.Combine(
            workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "preview-assets",
            string.Concat(proxyJobId, "-", result.SpriteImageArtifact.Id))));
    }

    private static void AssertArtifactContent(
        PreviewFixture fixture,
        StoredArtifact artifact,
        byte[] expected)
    {
        string path = fixture.Store.GetValidatedRegisteredArtifactPath(artifact.Id);
        CollectionAssert.AreEqual(expected, File.ReadAllBytes(path));
        Assert.AreEqual(ContentDigest.Compute(expected), artifact.ContentDigest);
        Assert.AreEqual(expected.LongLength, artifact.ByteSize);
    }

    private static void AssertSafeFailure(
        PreviewAssetGenerationException exception,
        PreviewAssetGenerationFailureReason reason,
        bool reconciliationRequired)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\private\customer-proxy.mp4",
            StringComparison.OrdinalIgnoreCase));
    }

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

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

            foreach (string candidate in Directory.EnumerateFiles(
                packagesRoot,
                fileName,
                SearchOption.AllDirectories))
            {
                var file = new FileInfo(candidate);
                if (file.Length != expectedByteSize)
                {
                    continue;
                }

                using FileStream stream = file.OpenRead();
                string actual = Convert.ToHexString(
                    SHA256.HashData(stream)).ToLowerInvariant();
                if (string.Equals(actual, expectedSha256, StringComparison.Ordinal))
                {
                    executablePath = Path.GetFullPath(candidate);
                    return true;
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

    private sealed class PreviewFixture : IDisposable
    {
        private static readonly DateTimeOffset ProxyCreatedAt =
            new(2037, 8, 9, 10, 11, 12, TimeSpan.Zero);

        private static readonly DateTimeOffset PreviewCreatedAt =
            ProxyCreatedAt.AddHours(1);

        private readonly BinaryVerifier _binaryVerifier = new();
        private readonly bool _includeAudio;

        internal PreviewFixture(bool includeAudio = true)
        {
            _includeAudio = includeAudio;
            Database = new StoreTestDatabase();
            WorkspacePath = Path.Combine(Database.RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            Store = new ArtifactStore(Database.DatabasePath, WorkspacePath);
            ProjectId = EntityId<ProjectIdTag>.New();
            SourceAssetId = EntityId<AssetIdTag>.New();
            ProxyJobId = EntityId<JobIdTag>.New();
            ProxyArtifactId = EntityId<ArtifactIdTag>.New();
            WaveformIndexArtifactId = EntityId<ArtifactIdTag>.New();
            SpriteImageArtifactId = EntityId<ArtifactIdTag>.New();
            SpriteIndexArtifactId = EntityId<ArtifactIdTag>.New();
            ProxyContent = new byte[512];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(ProxyContent, 4);
            Encoding.ASCII.GetBytes("verified-proxy").CopyTo(ProxyContent, 16);
            ContentDigest proxyDigest = ContentDigest.Compute(ProxyContent);
            var asset = new Asset(
                SourceAssetId,
                "source.mov",
                "video/quicktime",
                new ContentDigest(new string('a', 64)),
                1024,
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "Internal preview test",
                    externalPublicationAllowed: false));
            new ProjectStore(Database.DatabasePath).CreateProject(
                new Project(
                    ProjectId,
                    "Preview asset generator test",
                    "Verify waveform and sprite generation.",
                    "Test suite",
                    PublicationScope.InternalOnly,
                    deadline: null,
                    assets: [asset],
                    performers: []),
                "{\"previewAssets\":true}",
                ProxyCreatedAt);

            string stagingPath = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "proxy.ready");
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.WriteAllBytes(stagingPath, ProxyContent);
            ProxyArtifact = Store.PromoteAsync(
                new ArtifactPromotionRequest(
                    ProxyArtifactId,
                    ProjectId,
                    ArtifactKind.Proxy,
                    stagingPath,
                    $"{SourceAssetId}/{ProxyArtifactId}.mp4",
                    proxyDigest,
                    ProxyContent.LongLength,
                    ProxyCreatedAt)).GetAwaiter().GetResult();
            ProxyPath = Store.GetValidatedRegisteredArtifactPath(ProxyArtifactId);
            ProxyPtsIndex = CreateProxyPtsIndex(
                candidateId: SourceAssetId,
                projectId: ProjectId,
                jobId: ProxyJobId,
                contentDigest: proxyDigest,
                byteSize: ProxyContent.LongLength);
            GeneratedProxy = CreateGeneratedProxy(ProxyPtsIndex);
            FfmpegPath = Path.Combine(Database.RootPath, "ffmpeg.exe");
            ProcessHandler = DefaultProcessHandler;
            PromoteHandler = Store.PromoteAsync;
            Generator = CreateGenerator();
        }

        internal StoreTestDatabase Database { get; }

        internal string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<AssetIdTag> SourceAssetId { get; }

        internal EntityId<JobIdTag> ProxyJobId { get; }

        internal EntityId<ArtifactIdTag> ProxyArtifactId { get; }

        internal EntityId<ArtifactIdTag> WaveformIndexArtifactId { get; }

        internal EntityId<ArtifactIdTag> SpriteImageArtifactId { get; }

        internal EntityId<ArtifactIdTag> SpriteIndexArtifactId { get; }

        internal byte[] ProxyContent { get; }

        internal StoredArtifact ProxyArtifact { get; }

        internal string ProxyPath { get; }

        internal PtsIndex ProxyPtsIndex { get; }

        internal GeneratedProxy GeneratedProxy { get; }

        internal string FfmpegPath { get; }

        internal PreviewAssetGenerator Generator { get; private set; }

        internal List<ProcessRunRequest> ProcessRequests { get; } = [];

        internal Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>
            ProcessHandler
        { get; set; }

        internal Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>
            PromoteHandler
        { get; set; }

        internal bool CorruptSpriteCrc { get; set; }

        internal bool CorruptSpriteImageData { get; set; }

        internal bool OversizedSpriteImageData { get; set; }

        internal bool TruncateWaveform { get; set; }

        internal bool WrongSpriteDimensions { get; set; }

        internal long ExpectedSampleCount => 1068;

        internal Task<GeneratedPreviewAssets> GenerateAsync(
            CancellationToken cancellationToken = default)
        {
            Generator = CreateGenerator();
            return Generator.GenerateAsync(
                WaveformIndexArtifactId,
                SpriteImageArtifactId,
                SpriteIndexArtifactId,
                GeneratedProxy,
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }

        internal GeneratedProxy CreateGeneratedProxy(PtsIndex index)
        {
            MediaProbeResult probe = CreateProbe(index, _includeAudio);
            return new GeneratedProxy(
                SourceAssetId,
                new ContentDigest(new string('a', 64)),
                EntityId<ArtifactIdTag>.New(),
                ProxyArtifact,
                probe,
                index,
                new ProxyGenerationProfile(),
                TimeSpan.Zero);
        }

        internal string GetStagingDirectory() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "preview-assets",
                string.Concat(
                    ProxyJobId.ToString(),
                    "-",
                    SpriteImageArtifactId.ToString()));

        internal long CountArtifacts() =>
            Database.ExecuteScalarInt64("SELECT COUNT(*) FROM artifacts;");

        internal static PtsIndex CreateProxyPtsIndex(
            int entryCount = 4,
            IReadOnlyList<int>? keyFrameSequences = null,
            RationalTime? presentationOffset = null,
            EntityId<AssetIdTag>? candidateId = null,
            EntityId<ProjectIdTag>? projectId = null,
            EntityId<JobIdTag>? jobId = null,
            ContentDigest? contentDigest = null,
            long byteSize = 512)
        {
            candidateId ??= EntityId<AssetIdTag>.New();
            projectId ??= EntityId<ProjectIdTag>.New();
            jobId ??= EntityId<JobIdTag>.New();
            contentDigest ??= new ContentDigest(new string('b', 64));
            keyFrameSequences ??= [0, 2];
            HashSet<int> keyFrames = keyFrameSequences.ToHashSet();
            RationalTime frameDuration = new(1001, 30000);
            RationalTime offset = presentationOffset ?? default;
            PtsIndexEntry[] entries = Enumerable.Range(0, entryCount)
                .Select(index => new PtsIndexEntry(
                    index,
                    offset + new RationalTime(
                        checked(index * 1001L),
                        30000),
                    frameDuration,
                    keyFrames.Contains(index)))
                .ToArray();
            return new PtsIndex(
                candidateId,
                projectId,
                jobId,
                "proxy.mp4",
                contentDigest,
                byteSize,
                streamIndex: 0,
                frameDuration,
                entries);
        }

        private PreviewAssetGenerator CreateGenerator() =>
            new(
                Store,
                (request, cancellationToken) =>
                    ProcessHandler(request, cancellationToken),
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(BinaryVerificationResult.Verified(
                        request,
                        request.ExpectedByteSize,
                        request.ExpectedDigest));
                },
                (artifactId, cancellationToken) =>
                    Store.VerifyRegisteredAsync(
                        artifactId,
                        _binaryVerifier,
                        cancellationToken),
                (request, cancellationToken) =>
                    PromoteHandler(request, cancellationToken),
                WorkspacePath,
                FfmpegPath,
                new FixedTimeProvider(PreviewCreatedAt));

        private Task<ProcessRunResult> DefaultProcessHandler(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRequests.Add(request);
            string outputPath = request.ArgumentList[^1];
            if (request.ArgumentList.Contains("pcm_s16le", StringComparer.Ordinal))
            {
                WriteWaveformPcm(outputPath);
                if (TruncateWaveform)
                {
                    using FileStream stream = new(
                        outputPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None);
                    stream.SetLength(stream.Length - sizeof(short));
                }
            }
            else
            {
                WritePngHeader(
                    outputPath,
                    width: WrongSpriteDimensions ? 319 : 320,
                    height: 90);
                if (CorruptSpriteCrc)
                {
                    byte[] bytes = File.ReadAllBytes(outputPath);
                    bytes[29] ^= 0x01;
                    File.WriteAllBytes(outputPath, bytes);
                }
                else if (CorruptSpriteImageData)
                {
                    CorruptIdatWithValidCrc(outputPath);
                }
                else if (OversizedSpriteImageData)
                {
                    ExpandIdatWithValidCrc(outputPath);
                }
            }

            return Task.FromResult(new ProcessRunResult(
                0,
                string.Empty,
                string.Empty,
                Truncated: false,
                TimeSpan.FromMilliseconds(7)));
        }

        private void WriteWaveformPcm(string path)
        {
            long[] ends = [266, 533, 800, ExpectedSampleCount];
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
            Span<byte> sampleBytes = stackalloc byte[sizeof(short)];
            long start = 0;
            for (int frame = 0; frame < ends.Length; frame++)
            {
                for (long sample = start; sample < ends[frame]; sample++)
                {
                    short left = checked((short)(frame * 100 +
                        (sample % 2 == 0 ? -5 : 5)));
                    short right = checked((short)(frame * 100 + 10 +
                        (sample % 2 == 0 ? -5 : 5)));
                    BinaryPrimitives.WriteInt16LittleEndian(sampleBytes, left);
                    stream.Write(sampleBytes);
                    BinaryPrimitives.WriteInt16LittleEndian(sampleBytes, right);
                    stream.Write(sampleBytes);
                }

                start = ends[frame];
            }
        }

        private static void WritePngHeader(
            string path,
            int width,
            int height)
        {
            using var png = new MemoryStream();
            png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            byte[] header = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(
                header.AsSpan(0, 4),
                checked((uint)width));
            BinaryPrimitives.WriteUInt32BigEndian(
                header.AsSpan(4, 4),
                checked((uint)height));
            header[8] = 8;
            header[9] = 6;
            WritePngChunk(png, "IHDR"u8, header);

            using var raw = new MemoryStream();
            byte[] row = new byte[checked(1 + (width * 4))];
            for (int rowIndex = 0; rowIndex < height; rowIndex++)
            {
                raw.Write(row);
            }

            raw.Position = 0;
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(
                compressed,
                CompressionLevel.SmallestSize,
                leaveOpen: true))
            {
                raw.CopyTo(zlib);
            }

            WritePngChunk(png, "IDAT"u8, compressed.ToArray());
            WritePngChunk(png, "IEND"u8, []);
            File.WriteAllBytes(path, png.ToArray());
        }

        private static void WritePngChunk(
            Stream destination,
            ReadOnlySpan<byte> type,
            ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(
                length,
                checked((uint)data.Length));
            destination.Write(length);
            destination.Write(type);
            destination.Write(data);
            uint crc = 0xffffffffu;
            crc = UpdateCrc32(crc, type);
            crc = UpdateCrc32(crc, data) ^ 0xffffffffu;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            destination.Write(crcBytes);
        }

        private static void CorruptIdatWithValidCrc(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int offset = 8;
            while (offset < bytes.Length)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(offset, 4)));
                ReadOnlySpan<byte> type = bytes.AsSpan(offset + 4, 4);
                if (type.SequenceEqual("IDAT"u8))
                {
                    if (length == 0)
                    {
                        throw new InvalidOperationException(
                            "The PNG test IDAT chunk is empty.");
                    }

                    int dataOffset = offset + 8;
                    bytes[dataOffset] ^= 0xff;
                    uint crc = 0xffffffffu;
                    crc = UpdateCrc32(crc, type);
                    crc = UpdateCrc32(
                        crc,
                        bytes.AsSpan(dataOffset, length)) ^ 0xffffffffu;
                    BinaryPrimitives.WriteUInt32BigEndian(
                        bytes.AsSpan(dataOffset + length, 4),
                        crc);
                    File.WriteAllBytes(path, bytes);
                    return;
                }

                offset = checked(offset + 12 + length);
            }

            throw new InvalidOperationException("The PNG test IDAT chunk is missing.");
        }

        private static void ExpandIdatWithValidCrc(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int offset = 8;
            while (offset < bytes.Length)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                    bytes.AsSpan(offset, 4)));
                ReadOnlySpan<byte> type = bytes.AsSpan(offset + 4, 4);
                if (type.SequenceEqual("IDAT"u8))
                {
                    byte[] expanded = new byte[checked(length + 200_000)];
                    bytes.AsSpan(offset + 8, length).CopyTo(expanded);
                    using var png = new MemoryStream();
                    png.Write(bytes.AsSpan(0, offset));
                    WritePngChunk(png, "IDAT"u8, expanded);
                    png.Write(bytes.AsSpan(offset + 12 + length));
                    File.WriteAllBytes(path, png.ToArray());
                    return;
                }

                offset = checked(offset + 12 + length);
            }

            throw new InvalidOperationException("The PNG test IDAT chunk is missing.");
        }

        private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^
                        (0xedb88320u & (uint)-(int)(crc & 1));
                }
            }

            return crc;
        }

        private MediaProbeResult CreateProbe(PtsIndex index, bool includeAudio)
        {
            List<ProbedMediaStream> streams =
            [
                new(
                    0,
                    MediaStreamKind.Video,
                    "h264",
                    "High",
                    640,
                    360,
                    "yuv420p",
                    new RationalNumber(30000, 1001),
                    new RationalNumber(30000, 1001),
                    new RationalNumber(1001, 30000),
                    startSeconds: 0,
                    durationSeconds: null,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
            ];
            if (includeAudio)
            {
                streams.Add(new ProbedMediaStream(
                    1,
                    MediaStreamKind.Audio,
                    "aac",
                    "LC",
                    width: null,
                    height: null,
                    pixelFormat: null,
                    nominalFrameRate: null,
                    averageFrameRate: null,
                    new RationalNumber(1, 48000),
                    startSeconds: 0,
                    durationSeconds: null,
                    sampleRate: 48000,
                    channels: 2,
                    channelLayout: "stereo"));
            }

            return new MediaProbeResult(
                SourceAssetId,
                ProjectId,
                ProxyJobId,
                "proxy.mp4",
                ProxyArtifact.ContentDigest,
                ProxyArtifact.ByteSize,
                ContainerSignature.IsoBaseMediaFtyp,
                ["mov", "mp4"],
                "QuickTime / MOV",
                durationSeconds: null,
                declaredByteSize: ProxyArtifact.ByteSize,
                bitRate: null,
                probeScore: null,
                streams,
                includeAudio ? [] : ["audio-track-absent"]);
        }

        public void Dispose() => Database.Dispose();

        private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => value;
        }
    }
}