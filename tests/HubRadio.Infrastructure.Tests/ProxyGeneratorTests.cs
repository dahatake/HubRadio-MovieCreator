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
public sealed class ProxyGeneratorTests
{
    private static readonly DateTimeOffset SourceCreatedAt =
        new(2036, 7, 8, 9, 10, 11, TimeSpan.Zero);

    private static readonly DateTimeOffset ProxyCreatedAt =
        SourceCreatedAt.AddHours(1);

    [TestMethod]
    public void CreateProcessRequest_UsesExactMeasuredNvencArgumentsWithoutOverwriteOrFallback()
    {
        using TemporaryDirectory temporary = new();
        string ffmpegPath = Path.Combine(temporary.Path, "ffmpeg.exe");
        string sourcePath = Path.Combine(temporary.Path, "source.mov");
        string outputDirectory = Path.Combine(temporary.Path, "output");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, "proxy.partial.mp4");
        TimeSpan timeout = TimeSpan.FromMilliseconds(4321);

        ProcessRunRequest request = ProxyGenerator.CreateProcessRequest(
            ffmpegPath,
            sourcePath,
            outputPath,
            videoStreamIndex: 2,
            audioStreamIndex: 4,
            timeout);

        Assert.AreEqual(Path.GetFullPath(ffmpegPath), request.Executable);
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin",
                "-v",
                "error",
                "-i",
                Path.GetFullPath(sourcePath),
                "-map",
                "0:2",
                "-map",
                "0:4",
                "-vf",
                "scale=640:360:flags=bilinear,fps=30000/1001,format=yuv420p",
                "-c:v",
                "h264_nvenc",
                "-preset",
                "p4",
                "-cq",
                "28",
                "-b:v",
                "0",
                "-g",
                "30",
                "-c:a",
                "aac",
                "-b:a",
                "96k",
                "-map_metadata",
                "-1",
                "-movflags",
                "+faststart",
                "-n",
                Path.GetFullPath(outputPath),
            },
            request.ArgumentList.ToArray());
        Assert.AreEqual(outputDirectory, request.WorkingDirectory);
        Assert.AreEqual(timeout, request.Timeout);
        Assert.AreEqual(
            ProcessRunRequest.DefaultMaxCapturedBytes,
            request.MaxCapturedBytes);
        Assert.IsNull(request.StandardInput);
        Assert.IsFalse(request.ArgumentList.Contains("-y", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("h264_mf", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("libx264", StringComparer.Ordinal));

        ProcessRunRequest noAudio = ProxyGenerator.CreateProcessRequest(
            ffmpegPath,
            sourcePath,
            outputPath,
            videoStreamIndex: 2,
            audioStreamIndex: null,
            timeout);
        Assert.IsFalse(noAudio.ArgumentList.Contains("-c:a", StringComparer.Ordinal));
        Assert.IsFalse(noAudio.ArgumentList.Contains("-b:a", StringComparer.Ordinal));
        Assert.AreEqual(1, noAudio.ArgumentList.Count(
            static argument => string.Equals(argument, "-map", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GenerateAsync_ProducesVerifiedImmutableTypedProxyAndCleansStaging()
    {
        using ProxyFixture fixture = new();

        GeneratedProxy result = await fixture.GenerateAsync();

        Assert.AreEqual(fixture.SourceAsset.Id, result.SourceAssetId);
        Assert.AreEqual(fixture.SourceAsset.ContentDigest, result.SourceContentDigest);
        Assert.AreEqual(fixture.SourceOriginal.Id, result.SourceOriginalArtifactId);
        Assert.AreEqual(fixture.ProxyArtifactId, result.ProxyArtifact.Id);
        Assert.AreEqual(fixture.ProjectId, result.ProxyArtifact.ProjectId);
        Assert.AreEqual(ArtifactKind.Proxy, result.ProxyArtifact.Kind);
        Assert.AreEqual(
            $"projects/{fixture.ProjectId}/proxy/{fixture.SourceAsset.Id}/{fixture.ProxyArtifactId}.mp4",
            result.ProxyArtifact.StorageReference);
        Assert.AreEqual(ContentDigest.Compute(fixture.ProxyContent), result.ProxyArtifact.ContentDigest);
        Assert.AreEqual(fixture.ProxyContent.LongLength, result.ProxyArtifact.ByteSize);
        Assert.AreEqual(ProxyCreatedAt, result.ProxyArtifact.CreatedAt);
        Assert.AreEqual(fixture.SourceAsset.Id, result.ProxyProbeResult.CandidateId);
        Assert.AreEqual(fixture.ProxyJobId, result.ProxyProbeResult.JobId);
        Assert.AreEqual(fixture.SourceAsset.Id, result.ProxyPtsIndex.CandidateId);
        Assert.AreEqual(fixture.ProxyJobId, result.ProxyPtsIndex.JobId);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), result.GenerationElapsed);
        AssertProfile(result.Profile);

        Assert.HasCount(1, fixture.ProcessRequests);
        Assert.IsFalse(fixture.ProcessRequests[0].ArgumentList.Contains(
            "h264_mf",
            StringComparer.Ordinal));
        Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        Assert.IsTrue(File.Exists(fixture.GetFinalProxyPath()));
        CollectionAssert.AreEqual(
            fixture.ProxyContent,
            File.ReadAllBytes(fixture.GetFinalProxyPath()));
        CollectionAssert.AreEqual(
            fixture.SourceContent,
            File.ReadAllBytes(fixture.SourcePath));
        Assert.AreEqual(2L, fixture.CountArtifacts());

        StoredArtifact registered = fixture.Store.GetArtifact(fixture.ProxyArtifactId)
            ?? throw new AssertFailedException("The proxy artifact was not registered.");
        Assert.AreEqual(result.ProxyArtifact.ContentDigest, registered.ContentDigest);
        Assert.AreEqual(result.ProxyArtifact.ByteSize, registered.ByteSize);
    }

    [TestMethod]
    public void ValidateProcessResult_RequiresCleanUntruncatedExitZero()
    {
        const string privateText = @"C:\private\customer-source.mov";
        ProcessRunResult[] invalidResults =
        [
            new(1, string.Empty, privateText, false, TimeSpan.Zero),
            new(0, string.Empty, string.Empty, true, TimeSpan.Zero),
            new(0, privateText, string.Empty, false, TimeSpan.Zero),
            new(0, string.Empty, privateText, false, TimeSpan.Zero),
            new(0, null!, string.Empty, false, TimeSpan.Zero),
            new(0, string.Empty, null!, false, TimeSpan.Zero),
            new(0, string.Empty, string.Empty, false, TimeSpan.FromTicks(-1)),
        ];

        foreach (ProcessRunResult processResult in invalidResults)
        {
            ProxyGenerationException exception =
                Assert.ThrowsExactly<ProxyGenerationException>(() =>
                    ProxyGenerator.ValidateProcessResult(processResult));
            Assert.AreEqual(ProxyGenerationFailureReason.GenerationFailed, exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privateText);
        }

        ProxyGenerator.ValidateProcessResult(new ProcessRunResult(
            0,
            string.Empty,
            string.Empty,
            false,
            TimeSpan.Zero));
    }

    [TestMethod]
    public async Task GenerateAsync_NvencFailureDoesNotFallbackAndCleansOnlyOwnedStaging()
    {
        using ProxyFixture fixture = new();
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.ProcessRequests.Add(request);
            File.WriteAllBytes(request.ArgumentList[^1], fixture.ProxyContent);
            return Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                fixture.SourcePath,
                false,
                TimeSpan.FromMilliseconds(3)));
        };

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(
                () => fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.GenerationFailed,
            reconciliationRequired: false,
            fixture.SourcePath,
            fixture.GetStagingOutputPath());
        Assert.HasCount(1, fixture.ProcessRequests);
        Assert.IsTrue(fixture.ProcessRequests[0].ArgumentList.Contains(
            "h264_nvenc",
            StringComparer.Ordinal));
        Assert.IsFalse(fixture.ProcessRequests[0].ArgumentList.Contains(
            "h264_mf",
            StringComparer.Ordinal));
        Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        Assert.IsFalse(File.Exists(fixture.GetFinalProxyPath()));
        CollectionAssert.AreEqual(
            fixture.SourceContent,
            File.ReadAllBytes(fixture.SourcePath));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public void ValidateInputBinding_RejectsEveryAssetOriginalAndPtsMismatch()
    {
        using ProxyFixture fixture = new();
        ContentDigest otherDigest = new(new string('b', 64));
        Asset otherAsset = CreateAsset(
            EntityId<AssetIdTag>.New(),
            fixture.SourceAsset.FileName,
            fixture.SourceAsset.ContentDigest,
            fixture.SourceAsset.ByteSize);
        StoredArtifact[] badOriginals =
        [
            new(
                fixture.SourceOriginal.Id,
                fixture.SourceOriginal.ProjectId,
                ArtifactKind.Proxy,
                fixture.SourceOriginal.StorageReference,
                fixture.SourceOriginal.ContentDigest,
                fixture.SourceOriginal.ByteSize,
                fixture.SourceOriginal.CreatedAt),
            new(
                fixture.SourceOriginal.Id,
                EntityId<ProjectIdTag>.New(),
                ArtifactKind.Original,
                fixture.SourceOriginal.StorageReference,
                fixture.SourceOriginal.ContentDigest,
                fixture.SourceOriginal.ByteSize,
                fixture.SourceOriginal.CreatedAt),
            new(
                fixture.SourceOriginal.Id,
                fixture.SourceOriginal.ProjectId,
                ArtifactKind.Original,
                "projects/00000000-0000-4000-8000-000000000000/original/other/original",
                fixture.SourceOriginal.ContentDigest,
                fixture.SourceOriginal.ByteSize,
                fixture.SourceOriginal.CreatedAt),
            new(
                fixture.SourceOriginal.Id,
                fixture.SourceOriginal.ProjectId,
                ArtifactKind.Original,
                fixture.SourceOriginal.StorageReference,
                otherDigest,
                fixture.SourceOriginal.ByteSize,
                fixture.SourceOriginal.CreatedAt),
            new(
                fixture.SourceOriginal.Id,
                fixture.SourceOriginal.ProjectId,
                ArtifactKind.Original,
                fixture.SourceOriginal.StorageReference,
                fixture.SourceOriginal.ContentDigest,
                fixture.SourceOriginal.ByteSize + 1,
                fixture.SourceOriginal.CreatedAt),
        ];
        PtsIndex[] badIndices =
        [
            fixture.CreateSourcePts(candidateId: EntityId<AssetIdTag>.New()),
            fixture.CreateSourcePts(projectId: EntityId<ProjectIdTag>.New()),
            fixture.CreateSourcePts(sourceFileName: "other.mov"),
            fixture.CreateSourcePts(contentDigest: otherDigest),
            fixture.CreateSourcePts(byteSize: fixture.SourceAsset.ByteSize + 1),
        ];

        AssertBindingMismatch(() => ProxyGenerator.ValidateInputBinding(
            otherAsset,
            fixture.SourceOriginal,
            fixture.SourcePtsIndex));
        foreach (StoredArtifact original in badOriginals)
        {
            AssertBindingMismatch(() => ProxyGenerator.ValidateInputBinding(
                fixture.SourceAsset,
                original,
                fixture.SourcePtsIndex));
        }

        foreach (PtsIndex index in badIndices)
        {
            AssertBindingMismatch(() => ProxyGenerator.ValidateInputBinding(
                fixture.SourceAsset,
                fixture.SourceOriginal,
                index));
        }
    }

    [TestMethod]
    public async Task GenerateAsync_RegisteredSnapshotMismatchStopsBeforeEncoding()
    {
        using ProxyFixture fixture = new();
        StoredArtifact staleOriginal = new(
            fixture.SourceOriginal.Id,
            fixture.SourceOriginal.ProjectId,
            ArtifactKind.Original,
            fixture.SourceOriginal.StorageReference,
            fixture.SourceOriginal.ContentDigest,
            fixture.SourceOriginal.ByteSize,
            fixture.SourceOriginal.CreatedAt.AddTicks(1));

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(() =>
                fixture.GenerateAsync(sourceOriginal: staleOriginal));

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.InputBindingMismatch,
            reconciliationRequired: false,
            fixture.SourcePath);
        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_RejectsPtsStreamOrTimebaseThatDoesNotMatchFreshSourceProbe()
    {
        using ProxyFixture fixture = new();
        PtsIndex[] mismatches =
        [
            fixture.CreateSourcePts(streamIndex: 7),
            fixture.CreateSourcePts(timeBase: new RationalTime(1, 2000)),
        ];

        foreach (PtsIndex mismatch in mismatches)
        {
            ProxyGenerationException exception =
                await Assert.ThrowsExactlyAsync<ProxyGenerationException>(() =>
                    fixture.GenerateAsync(sourcePtsIndex: mismatch));
            AssertSafeFailure(
                exception,
                ProxyGenerationFailureReason.InputBindingMismatch,
                reconciliationRequired: false,
                fixture.SourcePath);
        }

        Assert.IsEmpty(fixture.ProcessRequests);
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public void ValidateOutputEvidence_RejectsEveryRequiredVideoAndContainerFieldMismatch()
    {
        using ProxyFixture fixture = new();
        StagedMediaImport staged = fixture.CreateValidationProxyMedia();
        (MediaProbeResult Probe, PtsIndex Index)[] mismatches =
        [
            fixture.CreateOutputEvidence(staged, videoCodec: "hevc"),
            fixture.CreateOutputEvidence(staged, width: 641),
            fixture.CreateOutputEvidence(staged, height: 361),
            fixture.CreateOutputEvidence(staged, pixelFormat: "yuv422p"),
            fixture.CreateOutputEvidence(
                staged,
                nominalFrameRate: new RationalNumber(30, 1)),
            fixture.CreateOutputEvidence(
                staged,
                averageFrameRate: new RationalNumber(30, 1)),
            fixture.CreateOutputEvidence(
                staged,
                containerSignature: ContainerSignature.Ebml),
            fixture.CreateOutputEvidence(
                staged,
                omitDeclaredByteSize: true),
            fixture.CreateOutputEvidence(staged, formatNames: ["mp4"]),
            fixture.CreateOutputEvidence(staged, formatNames: ["mov"]),
            fixture.CreateOutputEvidence(
                staged,
                videoIndex: 1,
                audioStartIndex: 0),
            fixture.CreateOutputEvidence(staged, additionalVideo: true),
            fixture.CreateOutputEvidence(staged, includeOtherStream: true),
            (
                fixture.CreateOutputEvidence(staged).Probe,
                fixture.CreateOutputPts(staged, streamIndex: 7)),
            fixture.CreateOutputEvidence(
                staged,
                ptsTimeBase: new RationalTime(1, 30000)),
        ];

        foreach ((MediaProbeResult probe, PtsIndex index) in mismatches)
        {
            ProxyGenerationException exception =
                Assert.ThrowsExactly<ProxyGenerationException>(() =>
                    ProxyGenerator.ValidateOutputEvidence(
                        staged,
                        probe,
                        index,
                        sourceHasAudio: true));
            Assert.AreEqual(
                ProxyGenerationFailureReason.OutputProfileMismatch,
                exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
        }

        (MediaProbeResult validProbe, PtsIndex validIndex) =
            fixture.CreateOutputEvidence(staged);
        ProxyGenerator.ValidateOutputEvidence(
            staged,
            validProbe,
            validIndex,
            sourceHasAudio: true);
    }

    [TestMethod]
    public void ValidateOutputEvidence_RejectsEveryEvidenceIdentityMismatch()
    {
        using ProxyFixture fixture = new();
        StagedMediaImport staged = fixture.CreateValidationProxyMedia();
        (MediaProbeResult probe, PtsIndex index) =
            fixture.CreateOutputEvidence(staged);
        ContentDigest otherDigest = new(new string('b', 64));
        StagedMediaImport[] mismatches =
        [
            new(
                EntityId<AssetIdTag>.New(),
                staged.ProjectId,
                staged.JobId,
                staged.SourceFileName,
                staged.StagingPath,
                staged.ContentDigest,
                staged.ByteSize,
                staged.CreatedAt),
            new(
                staged.CandidateId,
                EntityId<ProjectIdTag>.New(),
                staged.JobId,
                staged.SourceFileName,
                staged.StagingPath,
                staged.ContentDigest,
                staged.ByteSize,
                staged.CreatedAt),
            new(
                staged.CandidateId,
                staged.ProjectId,
                EntityId<JobIdTag>.New(),
                staged.SourceFileName,
                staged.StagingPath,
                staged.ContentDigest,
                staged.ByteSize,
                staged.CreatedAt),
            new(
                staged.CandidateId,
                staged.ProjectId,
                staged.JobId,
                "other.mp4",
                staged.StagingPath,
                staged.ContentDigest,
                staged.ByteSize,
                staged.CreatedAt),
            new(
                staged.CandidateId,
                staged.ProjectId,
                staged.JobId,
                staged.SourceFileName,
                staged.StagingPath,
                otherDigest,
                staged.ByteSize,
                staged.CreatedAt),
            new(
                staged.CandidateId,
                staged.ProjectId,
                staged.JobId,
                staged.SourceFileName,
                staged.StagingPath,
                staged.ContentDigest,
                staged.ByteSize + 1,
                staged.CreatedAt),
        ];

        foreach (StagedMediaImport mismatch in mismatches)
        {
            AssertOutputProfileMismatch(() => ProxyGenerator.ValidateOutputEvidence(
                mismatch,
                probe,
                index,
                sourceHasAudio: true));
        }
    }

    [TestMethod]
    public void ValidateOutputEvidence_RejectsClaimedCfrWithIrregularObservedPts()
    {
        using ProxyFixture fixture = new();
        StagedMediaImport staged = fixture.CreateValidationProxyMedia();
        MediaProbeResult probe = fixture.CreateOutputEvidence(staged).Probe;
        RationalTime frame = new(1001, 30000);
        PtsIndex irregularSpacing = new(
            staged.CandidateId,
            staged.ProjectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            streamIndex: 0,
            frame,
            [
                new PtsIndexEntry(0, new RationalTime(0, 1), frame, keyFrame: true),
                new PtsIndexEntry(1, new RationalTime(1001, 15000), frame, keyFrame: false),
                new PtsIndexEntry(2, new RationalTime(1001, 10000), frame, keyFrame: false),
            ]);
        PtsIndex irregularDuration = new(
            staged.CandidateId,
            staged.ProjectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            streamIndex: 0,
            frame,
            [
                new PtsIndexEntry(
                    0,
                    new RationalTime(0, 1),
                    new RationalTime(1001, 15000),
                    keyFrame: true),
                new PtsIndexEntry(1, frame, frame, keyFrame: false),
                new PtsIndexEntry(2, new RationalTime(1001, 15000), frame, keyFrame: false),
            ]);

        foreach (PtsIndex index in new[] { irregularSpacing, irregularDuration })
        {
            AssertOutputProfileMismatch(() => ProxyGenerator.ValidateOutputEvidence(
                staged,
                probe,
                index,
                sourceHasAudio: true));
        }
    }

    [TestMethod]
    public void ValidateOutputEvidence_RequiresAudioParityAndAacWhenSourceHasAudio()
    {
        using ProxyFixture fixture = new();
        StagedMediaImport staged = fixture.CreateValidationProxyMedia();
        (MediaProbeResult Probe, PtsIndex Index)[] sourceAudioFailures =
        [
            fixture.CreateOutputEvidence(staged, audioCount: 0),
            fixture.CreateOutputEvidence(staged, audioCodec: "opus"),
            fixture.CreateOutputEvidence(staged, audioCount: 2),
            fixture.CreateOutputEvidence(
                staged,
                warnings: ["multiple-video-streams"]),
        ];

        foreach ((MediaProbeResult probe, PtsIndex index) in sourceAudioFailures)
        {
            AssertOutputProfileMismatch(() => ProxyGenerator.ValidateOutputEvidence(
                staged,
                probe,
                index,
                sourceHasAudio: true));
        }

        (MediaProbeResult unexpectedAudio, PtsIndex unexpectedAudioIndex) =
            fixture.CreateOutputEvidence(staged);
        AssertOutputProfileMismatch(() => ProxyGenerator.ValidateOutputEvidence(
            staged,
            unexpectedAudio,
            unexpectedAudioIndex,
            sourceHasAudio: false));

        (MediaProbeResult missingWarning, PtsIndex missingWarningIndex) =
            fixture.CreateOutputEvidence(
                staged,
                audioCount: 0,
                warnings: []);
        AssertOutputProfileMismatch(() => ProxyGenerator.ValidateOutputEvidence(
            staged,
            missingWarning,
            missingWarningIndex,
            sourceHasAudio: false));

        (MediaProbeResult noAudio, PtsIndex noAudioIndex) =
            fixture.CreateOutputEvidence(staged, audioCount: 0);
        ProxyGenerator.ValidateOutputEvidence(
            staged,
            noAudio,
            noAudioIndex,
            sourceHasAudio: false);
    }

    [TestMethod]
    public void ValidateGop_RequiresFirstKeyFrameAndAtMostThirtyFrameSpacing()
    {
        using ProxyFixture fixture = new();
        StagedMediaImport staged = fixture.CreateValidationProxyMedia();
        PtsIndex valid = fixture.CreateOutputPts(
            staged,
            entryCount: 60,
            keyFrameSequences: [0, 30]);
        ProxyGenerator.ValidateGop(valid);

        PtsIndex firstNotKey = fixture.CreateOutputPts(
            staged,
            entryCount: 10,
            keyFrameSequences: [1]);
        PtsIndex internalGap = fixture.CreateOutputPts(
            staged,
            entryCount: 62,
            keyFrameSequences: [0, 31, 61]);
        PtsIndex finalGap = fixture.CreateOutputPts(
            staged,
            entryCount: 31,
            keyFrameSequences: [0]);

        foreach (PtsIndex invalid in new[] { firstNotKey, internalGap, finalGap })
        {
            ProxyGenerationException exception =
                Assert.ThrowsExactly<ProxyGenerationException>(() =>
                    ProxyGenerator.ValidateGop(invalid));
            Assert.AreEqual(
                ProxyGenerationFailureReason.GopValidationFailed,
                exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
        }
    }

    [TestMethod]
    public async Task GenerateAsync_NoAudioOmitsAudioArgumentsAndRequiresAbsentOutputAudio()
    {
        using ProxyFixture fixture = new(includeSourceAudio: false);

        GeneratedProxy result = await fixture.GenerateAsync();

        ProcessRunRequest request = fixture.ProcessRequests.Single();
        Assert.IsFalse(request.ArgumentList.Contains("-c:a", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("-b:a", StringComparer.Ordinal));
        Assert.AreEqual(1, request.ArgumentList.Count(
            static argument => string.Equals(argument, "-map", StringComparison.Ordinal)));
        Assert.IsFalse(result.ProxyProbeResult.Streams.Any(
            static stream => stream.Kind == MediaStreamKind.Audio));
        CollectionAssert.Contains(
            result.ProxyProbeResult.Warnings.ToArray(),
            "audio-track-absent");
    }

    [TestMethod]
    public async Task GenerateAsync_HoldsOriginalReadShareLockAcrossEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file-share enforcement is required.");
            return;
        }

        using ProxyFixture fixture = new();
        bool writeBlocked = false;
        bool renameBlocked = false;
        string renamedPath = fixture.SourcePath + ".renamed";
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.ProcessRequests.Add(request);
            try
            {
                using FileStream _ = new(
                    fixture.SourcePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                writeBlocked = true;
            }

            try
            {
                File.Move(fixture.SourcePath, renamedPath);
            }
            catch (IOException)
            {
                renameBlocked = true;
            }

            File.WriteAllBytes(request.ArgumentList[^1], fixture.ProxyContent);
            return Task.FromResult(ProxyFixture.SuccessfulProcessResult());
        };

        _ = await fixture.GenerateAsync();

        Assert.IsTrue(writeBlocked);
        Assert.IsTrue(renameBlocked);
        Assert.IsTrue(File.Exists(fixture.SourcePath));
        Assert.IsFalse(File.Exists(renamedPath));
        CollectionAssert.AreEqual(
            fixture.SourceContent,
            File.ReadAllBytes(fixture.SourcePath));
    }

    [TestMethod]
    public async Task GenerateAsync_DuplicateArtifactIdStopsWithoutEncodingOrOverwrite()
    {
        using ProxyFixture fixture = new();

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(() =>
                fixture.GenerateAsync(proxyArtifactId: fixture.SourceOriginal.Id));

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.PromotionFailed,
            reconciliationRequired: false,
            fixture.SourcePath);
        Assert.IsEmpty(fixture.ProcessRequests);
        CollectionAssert.AreEqual(
            fixture.SourceContent,
            File.ReadAllBytes(fixture.SourcePath));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_ExistingDestinationIsNotOverwrittenAndOwnedStagingIsCleaned()
    {
        using ProxyFixture fixture = new();
        byte[] existingContent = Encoding.UTF8.GetBytes("existing proxy owner");
        string destinationPath = fixture.GetFinalProxyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, existingContent);

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(
                () => fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.PromotionFailed,
            reconciliationRequired: false,
            destinationPath,
            fixture.GetStagingOutputPath());
        CollectionAssert.AreEqual(existingContent, File.ReadAllBytes(destinationPath));
        Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_ExistingJobStagingIsNotClaimedOrModified()
    {
        using ProxyFixture fixture = new();
        string stagingDirectory = fixture.GetStagingDirectory();
        Directory.CreateDirectory(stagingDirectory);
        string sentinelPath = Path.Combine(stagingDirectory, "existing-owner.txt");
        byte[] sentinelContent = Encoding.UTF8.GetBytes("existing staging owner");
        File.WriteAllBytes(sentinelPath, sentinelContent);

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(() =>
                fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.StagingConflict,
            reconciliationRequired: false,
            stagingDirectory,
            sentinelPath);
        Assert.IsEmpty(fixture.ProcessRequests);
        CollectionAssert.AreEqual(sentinelContent, File.ReadAllBytes(sentinelPath));
        Assert.IsFalse(File.Exists(fixture.GetFinalProxyPath()));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_MissingOrEmptyOutputIsRejectedAndCleaned()
    {
        foreach (bool createEmptyOutput in new[] { false, true })
        {
            using ProxyFixture fixture = new();
            fixture.ProcessHandler = (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                fixture.ProcessRequests.Add(request);
                if (createEmptyOutput)
                {
                    File.WriteAllBytes(request.ArgumentList[^1], []);
                }

                return Task.FromResult(ProxyFixture.SuccessfulProcessResult());
            };

            ProxyGenerationException exception =
                await Assert.ThrowsExactlyAsync<ProxyGenerationException>(() =>
                    fixture.GenerateAsync());

            AssertSafeFailure(
                exception,
                ProxyGenerationFailureReason.OutputInvalid,
                reconciliationRequired: false,
                fixture.GetStagingOutputPath());
            Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
            Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
            Assert.IsFalse(File.Exists(fixture.GetFinalProxyPath()));
            CollectionAssert.AreEqual(
                fixture.SourceContent,
                File.ReadAllBytes(fixture.SourcePath));
            Assert.AreEqual(1L, fixture.CountArtifacts());
        }
    }

    [TestMethod]
    public async Task GenerateAsync_UnprovableOutputOwnershipReturnsStableReconciliationFailure()
    {
        using ProxyFixture fixture = new();
        fixture.ProcessHandler = (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.ProcessRequests.Add(request);
            Directory.CreateDirectory(request.ArgumentList[^1]);
            return Task.FromResult(new ProcessRunResult(
                1,
                string.Empty,
                string.Empty,
                false,
                TimeSpan.Zero));
        };

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(
                () => fixture.GenerateAsync());

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.CleanupFailed,
            reconciliationRequired: true,
            fixture.GetStagingOutputPath());
        Assert.IsTrue(Directory.Exists(fixture.GetStagingOutputPath()));
        CollectionAssert.AreEqual(
            fixture.SourceContent,
            File.ReadAllBytes(fixture.SourcePath));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_PostCommitCancellationReturnsStableReconciliationFailure()
    {
        using ProxyFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        fixture.RegisteredVerificationHandler = async (artifactId, token) =>
        {
            if (artifactId == fixture.ProxyArtifactId)
            {
                cancellation.Cancel();
                return await Task.FromCanceled<BinaryVerificationResult>(token);
            }

            return await fixture.VerifyRegisteredWithRealVerifierAsync(
                artifactId,
                token);
        };

        ProxyGenerationException exception =
            await Assert.ThrowsExactlyAsync<ProxyGenerationException>(() =>
                fixture.GenerateAsync(cancellationToken: cancellation.Token));

        AssertSafeFailure(
            exception,
            ProxyGenerationFailureReason.RegisteredProxyInvalid,
            reconciliationRequired: true,
            fixture.SourcePath,
            fixture.GetStagingOutputPath(),
            fixture.GetFinalProxyPath());
        Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        Assert.IsTrue(File.Exists(fixture.GetFinalProxyPath()));
        Assert.IsNotNull(fixture.Store.GetArtifact(fixture.ProxyArtifactId));
        Assert.AreEqual(2L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task GenerateAsync_CanonicalPipelineControlUsesSoftwareOnlyInsideTestDelegate()
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

        using CanonicalPipelineFixture fixture =
            await CanonicalPipelineFixture.CreateAsync(ffmpegPath, ffprobePath);
        ProcessRunner processRunner = new();
        BinaryVerifier binaryVerifier = new();
        FfprobeClient ffprobeClient = new(
            processRunner,
            binaryVerifier,
            ffprobePath);
        PtsIndexer ptsIndexer = new(
            processRunner,
            binaryVerifier,
            ffprobePath);
        List<ProcessRunRequest> productionRequests = [];
        ProxyGenerator generator = new(
            fixture.Store,
            async (request, cancellationToken) =>
            {
                productionRequests.Add(request);
                AssertProductionProxyRequest(request, fixture.SourcePath);
                ProcessRunRequest softwareControlRequest =
                    fixture.CreateSoftwareControlRequest(request);
                return await processRunner.RunAsync(
                    softwareControlRequest,
                    cancellationToken);
            },
            binaryVerifier.VerifyAsync,
            ffprobeClient.ProbeAsync,
            ptsIndexer.IndexAsync,
            (artifactId, cancellationToken) =>
                fixture.Store.VerifyRegisteredAsync(
                    artifactId,
                    binaryVerifier,
                    cancellationToken),
            fixture.WorkspacePath,
            ffmpegPath,
            TimeProvider.System);

        GeneratedProxy result = await generator.GenerateAsync(
            fixture.ProxyArtifactId,
            fixture.SourceAsset,
            fixture.SourceOriginal,
            fixture.SourcePtsIndex,
            fixture.ProxyJobId,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.HasCount(1, productionRequests);
        AssertCanonicalGeneratedProxy(fixture, result);
    }

    [TestMethod]
    public async Task GenerateAsync_CanonicalNvencSyntheticIntegrationOrReportsEnvironmentBlocker()
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

        using CanonicalPipelineFixture fixture =
            await CanonicalPipelineFixture.CreateAsync(ffmpegPath, ffprobePath);
        ProcessRunner processRunner = new();
        BinaryVerifier binaryVerifier = new();
        FfprobeClient ffprobeClient = new(
            processRunner,
            binaryVerifier,
            ffprobePath);
        PtsIndexer ptsIndexer = new(
            processRunner,
            binaryVerifier,
            ffprobePath);
        ProxyGenerator generator = new(
            fixture.Store,
            processRunner,
            binaryVerifier,
            ffprobeClient,
            ptsIndexer,
            fixture.WorkspacePath,
            ffmpegPath);

        GeneratedProxy result;
        try
        {
            result = await generator.GenerateAsync(
                fixture.ProxyArtifactId,
                fixture.SourceAsset,
                fixture.SourceOriginal,
                fixture.SourcePtsIndex,
                fixture.ProxyJobId,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
        }
        catch (ProxyGenerationException exception)
            when (exception.Reason == ProxyGenerationFailureReason.GenerationFailed)
        {
            Assert.IsFalse(exception.ReconciliationRequired);
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
            Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
            Assert.IsFalse(File.Exists(fixture.GetFinalProxyPath()));
            Assert.IsNotNull(fixture.Store.GetArtifact(fixture.SourceOriginal.Id));
            Assert.IsNull(fixture.Store.GetArtifact(fixture.ProxyArtifactId));
            Assert.AreEqual(1L, fixture.CountArtifacts());
            Assert.Inconclusive(
                "The approved h264_nvenc encoder cannot run in this environment; no fallback was attempted.");
            return;
        }

        AssertCanonicalGeneratedProxy(fixture, result);
    }

    [TestMethod]
    public void PublicSurfaceIsImmutablePathPrivateAndContainsOnlyTypedEvidence()
    {
        foreach (Type type in new[]
        {
            typeof(ProxyGenerationException),
            typeof(ProxyGenerationProfile),
            typeof(GeneratedProxy),
            typeof(ProxyGenerator),
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
            new[] { "Reason", "ReconciliationRequired" },
            typeof(ProxyGenerationException)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "SourceAssetId",
                "SourceContentDigest",
                "SourceOriginalArtifactId",
                "ProxyArtifact",
                "ProxyProbeResult",
                "ProxyPtsIndex",
                "Profile",
                "GenerationElapsed",
            },
            typeof(GeneratedProxy)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        Assert.IsFalse(typeof(GeneratedProxy)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("Message", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, typeof(GeneratedProxy).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ProxyGenerationProfile).GetConstructors().Length);
        CollectionAssert.AreEqual(
            new[] { "GenerateAsync" },
            typeof(ProxyGenerator)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsFalse(typeof(ArtifactStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Any(static method =>
                method.ReturnType == typeof(string) &&
                method.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)));

        const string privatePath = @"C:\private\customer-secret.mov";
        foreach (ProxyGenerationFailureReason reason in
            Enum.GetValues<ProxyGenerationFailureReason>())
        {
            var exception = new ProxyGenerationException(
                reason,
                reconciliationRequired: false);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privatePath);
        }
    }

    private static void AssertProductionProxyRequest(
        ProcessRunRequest request,
        string expectedSourcePath)
    {
        Assert.AreEqual(Path.GetFullPath(expectedSourcePath), request.ArgumentList[4]);
        Assert.AreEqual(1, request.ArgumentList.Count(
            static argument => string.Equals(
                argument,
                "h264_nvenc",
                StringComparison.Ordinal)));
        Assert.IsTrue(request.ArgumentList.Contains("-n", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("-y", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("h264_mf", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("libopenh264", StringComparer.Ordinal));
        Assert.IsFalse(request.ArgumentList.Contains("libx264", StringComparer.Ordinal));
        Assert.AreEqual(
            ProcessRunRequest.DefaultMaxCapturedBytes,
            request.MaxCapturedBytes);
        Assert.IsNull(request.StandardInput);
    }

    private static void AssertCanonicalGeneratedProxy(
        CanonicalPipelineFixture fixture,
        GeneratedProxy result)
    {
        Assert.AreEqual(fixture.SourceAsset.Id, result.SourceAssetId);
        Assert.AreEqual(fixture.SourceAsset.ContentDigest, result.SourceContentDigest);
        Assert.AreEqual(fixture.SourceOriginal.Id, result.SourceOriginalArtifactId);
        Assert.AreEqual(fixture.ProxyArtifactId, result.ProxyArtifact.Id);
        Assert.AreEqual(ArtifactKind.Proxy, result.ProxyArtifact.Kind);
        Assert.AreEqual(
            $"projects/{fixture.ProjectId}/proxy/{fixture.SourceAsset.Id}/{fixture.ProxyArtifactId}.mp4",
            result.ProxyArtifact.StorageReference);
        AssertProfile(result.Profile);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, result.GenerationElapsed);
        Assert.AreEqual(
            ContainerSignature.IsoBaseMediaFtyp,
            result.ProxyProbeResult.ContainerSignature);
        CollectionAssert.Contains(
            result.ProxyProbeResult.FormatNames.ToArray(),
            "mov");
        CollectionAssert.Contains(
            result.ProxyProbeResult.FormatNames.ToArray(),
            "mp4");
        ProbedMediaStream video = result.ProxyProbeResult.Streams.Single(
            static stream => stream.Kind == MediaStreamKind.Video);
        ProbedMediaStream audio = result.ProxyProbeResult.Streams.Single(
            static stream => stream.Kind == MediaStreamKind.Audio);
        Assert.AreEqual("h264", video.CodecName);
        Assert.AreEqual(640, video.Width);
        Assert.AreEqual(360, video.Height);
        Assert.AreEqual("yuv420p", video.PixelFormat);
        Assert.AreEqual(new RationalNumber(30000, 1001), video.NominalFrameRate);
        Assert.AreEqual(new RationalNumber(30000, 1001), video.AverageFrameRate);
        Assert.AreEqual("aac", audio.CodecName);
        Assert.IsGreaterThan(30, result.ProxyPtsIndex.Entries.Count);
        ProxyGenerator.ValidateGop(result.ProxyPtsIndex);
        Assert.IsTrue(File.Exists(fixture.GetFinalProxyPath()));
        Assert.IsFalse(File.Exists(fixture.GetStagingOutputPath()));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory()));
        Assert.AreEqual(
            result.ProxyArtifact.ContentDigest,
            ContentDigest.Compute(File.ReadAllBytes(fixture.GetFinalProxyPath())));
        Assert.AreEqual(
            fixture.SourceAsset.ContentDigest,
            ContentDigest.Compute(File.ReadAllBytes(fixture.SourcePath)));
        Assert.AreEqual(2L, fixture.CountArtifacts());
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

            string[] packageDirectories = Directory.GetDirectories(
                packagesRoot,
                string.Concat(DecodeValidator.CanonicalPackageId, "*"),
                SearchOption.TopDirectoryOnly);
            foreach (string packageDirectory in packageDirectories)
            {
                foreach (string candidate in Directory.EnumerateFiles(
                    packageDirectory,
                    fileName,
                    SearchOption.AllDirectories))
                {
                    var fileInfo = new FileInfo(candidate);
                    if (fileInfo.Length != expectedByteSize)
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(candidate);
                    string actualSha256 = Convert.ToHexString(
                        SHA256.HashData(stream)).ToLowerInvariant();
                    if (string.Equals(
                        actualSha256,
                        expectedSha256,
                        StringComparison.Ordinal))
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

    private static Asset CreateAsset(
        EntityId<AssetIdTag> id,
        string fileName,
        ContentDigest contentDigest,
        long byteSize) =>
        new(
            id,
            fileName,
            "video/quicktime",
            contentDigest,
            byteSize,
            new RightsRecord(
                RightsStatus.Unconfirmed,
                evidenceReference: null,
                "Internal test processing",
                externalPublicationAllowed: false));

    private static void AssertProfile(ProxyGenerationProfile profile)
    {
        Assert.AreEqual("stage0-proxy-640x360-v1", profile.EvidenceLabel);
        Assert.AreEqual(640, profile.Width);
        Assert.AreEqual(360, profile.Height);
        Assert.AreEqual(new RationalNumber(30000, 1001), profile.FrameRate);
        Assert.AreEqual("bilinear", profile.ScaleFlags);
        Assert.AreEqual("yuv420p", profile.PixelFormat);
        Assert.AreEqual("h264_nvenc", profile.VideoEncoder);
        Assert.AreEqual("p4", profile.EncoderPreset);
        Assert.AreEqual(28, profile.ConstantQuality);
        Assert.AreEqual("0", profile.VideoBitRate);
        Assert.AreEqual(30, profile.GopSize);
        Assert.AreEqual("aac", profile.AudioCodec);
        Assert.AreEqual("96k", profile.AudioBitRate);
        Assert.IsTrue(profile.MetadataStripped);
        Assert.IsTrue(profile.FastStart);
    }

    private static void AssertBindingMismatch(Action action)
    {
        ProxyGenerationException exception =
            Assert.ThrowsExactly<ProxyGenerationException>(action);
        Assert.AreEqual(ProxyGenerationFailureReason.InputBindingMismatch, exception.Reason);
        Assert.IsFalse(exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
    }

    private static void AssertOutputProfileMismatch(Action action)
    {
        ProxyGenerationException exception =
            Assert.ThrowsExactly<ProxyGenerationException>(action);
        Assert.AreEqual(
            ProxyGenerationFailureReason.OutputProfileMismatch,
            exception.Reason);
        Assert.IsFalse(exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
    }

    private static void AssertSafeFailure(
        ProxyGenerationException exception,
        ProxyGenerationFailureReason reason,
        bool reconciliationRequired,
        params string[] forbiddenValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string forbiddenValue in forbiddenValues)
        {
            AssertDoesNotContain(exception.ToString(), forbiddenValue);
        }
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (string.IsNullOrWhiteSpace(forbiddenValue))
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CanonicalPipelineFixture : IDisposable
    {
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;

        private CanonicalPipelineFixture(
            string ffmpegPath,
            string ffprobePath)
        {
            _ffmpegPath = ffmpegPath;
            _ffprobePath = ffprobePath;
            Database = new StoreTestDatabase();
            WorkspacePath = Path.Combine(Database.RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            Store = new ArtifactStore(Database.DatabasePath, WorkspacePath);
            ProjectId = EntityId<ProjectIdTag>.New();
            SourceJobId = EntityId<JobIdTag>.New();
            ProxyJobId = EntityId<JobIdTag>.New();
            SourceArtifactId = EntityId<ArtifactIdTag>.New();
            ProxyArtifactId = EntityId<ArtifactIdTag>.New();
        }

        internal StoreTestDatabase Database { get; }

        internal string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<JobIdTag> SourceJobId { get; }

        internal EntityId<JobIdTag> ProxyJobId { get; }

        internal EntityId<ArtifactIdTag> SourceArtifactId { get; }

        internal EntityId<ArtifactIdTag> ProxyArtifactId { get; }

        internal Asset SourceAsset { get; private set; } = null!;

        internal StoredArtifact SourceOriginal { get; private set; } = null!;

        internal MediaProbeResult SourceProbe { get; private set; } = null!;

        internal PtsIndex SourcePtsIndex { get; private set; } = null!;

        internal string SourcePath { get; private set; } = string.Empty;

        internal static async Task<CanonicalPipelineFixture> CreateAsync(
            string ffmpegPath,
            string ffprobePath)
        {
            var fixture = new CanonicalPipelineFixture(ffmpegPath, ffprobePath);
            try
            {
                await fixture.InitializeAsync();
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal ProcessRunRequest CreateSoftwareControlRequest(
            ProcessRunRequest productionRequest)
        {
            ProbedMediaStream video = SourceProbe.Streams
                .Where(static stream => stream.Kind == MediaStreamKind.Video)
                .MinBy(static stream => stream.Index)
                ?? throw new AssertFailedException("The source video stream is missing.");
            ProbedMediaStream? audio = SourceProbe.Streams
                .Where(static stream => stream.Kind == MediaStreamKind.Audio)
                .MinBy(static stream => stream.Index);
            string outputPath = productionRequest.ArgumentList[^1];
            Assert.AreEqual(SourcePath, productionRequest.ArgumentList[4]);
            Assert.IsTrue(productionRequest.ArgumentList.Contains(
                $"0:{video.Index}",
                StringComparer.Ordinal));
            if (audio is not null)
            {
                Assert.IsTrue(productionRequest.ArgumentList.Contains(
                    $"0:{audio.Index}",
                    StringComparer.Ordinal));
            }

            List<string> arguments =
            [
                "-nostdin",
                "-v",
                "error",
                "-i",
                SourcePath,
                "-map",
                $"0:{video.Index}",
            ];
            if (audio is not null)
            {
                arguments.Add("-map");
                arguments.Add($"0:{audio.Index}");
            }

            arguments.Add("-vf");
            arguments.Add(
                "scale=640:360:flags=bilinear,fps=30000/1001,format=yuv420p");
            arguments.Add("-c:v");
            arguments.Add("libopenh264");
            arguments.Add("-b:v");
            arguments.Add("1200k");
            arguments.Add("-g");
            arguments.Add("30");
            if (audio is not null)
            {
                arguments.Add("-c:a");
                arguments.Add("aac");
                arguments.Add("-b:a");
                arguments.Add("96k");
            }

            arguments.Add("-map_metadata");
            arguments.Add("-1");
            arguments.Add("-movflags");
            arguments.Add("+faststart");
            arguments.Add("-n");
            arguments.Add(outputPath);

            return new ProcessRunRequest(
                _ffmpegPath,
                arguments,
                productionRequest.WorkingDirectory,
                productionRequest.Timeout,
                productionRequest.MaxCapturedBytes,
                standardInput: null);
        }

        internal string GetStagingDirectory() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "proxies",
                ProxyJobId.ToString());

        internal string GetStagingOutputPath() =>
            Path.Combine(
                GetStagingDirectory(),
                string.Concat(ProxyArtifactId.ToString(), ".partial.mp4"));

        internal string GetFinalProxyPath() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "proxy",
                SourceAsset.Id.ToString(),
                string.Concat(ProxyArtifactId.ToString(), ".mp4"));

        internal long CountArtifacts() =>
            Database.ExecuteScalarInt64("SELECT COUNT(*) FROM artifacts;");

        private async Task InitializeAsync()
        {
            string sourceDirectory = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "imports",
                SourceJobId.ToString());
            Directory.CreateDirectory(sourceDirectory);
            string stagedPath = Path.Combine(sourceDirectory, "canonical-source.mkv");
            ProcessRunner processRunner = new();
            ProcessRunResult generationResult = await processRunner.RunAsync(
                new ProcessRunRequest(
                    _ffmpegPath,
                    [
                        "-nostdin",
                        "-v",
                        "error",
                        "-f",
                        "lavfi",
                        "-i",
                        "testsrc2=size=1280x720:rate=24000/1001:duration=2",
                        "-f",
                        "lavfi",
                        "-i",
                        "sine=frequency=1000:sample_rate=48000:duration=2",
                        "-map",
                        "0:v:0",
                        "-map",
                        "1:a:0",
                        "-c:v",
                        "ffv1",
                        "-pix_fmt",
                        "yuv420p",
                        "-c:a",
                        "pcm_s16le",
                        "-shortest",
                        "-n",
                        stagedPath,
                    ],
                    sourceDirectory,
                    TimeSpan.FromSeconds(30)),
                CancellationToken.None);
            Assert.AreEqual(0, generationResult.ExitCode);
            Assert.AreEqual(string.Empty, generationResult.StandardOutput);
            Assert.AreEqual(string.Empty, generationResult.StandardError);
            Assert.IsFalse(generationResult.Truncated);

            byte[] content = await File.ReadAllBytesAsync(stagedPath);
            Assert.IsGreaterThan(0, content.Length);
            SourceAsset = CreateAsset(
                EntityId<AssetIdTag>.New(),
                "canonical-source.mkv",
                ContentDigest.Compute(content),
                content.LongLength);
            var project = new Project(
                ProjectId,
                "Canonical proxy pipeline test",
                "Verify M-008 with approved media tools.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [SourceAsset],
                performers: []);
            new ProjectStore(Database.DatabasePath).CreateProject(
                project,
                "{\"canonicalProxyPipeline\":true}",
                SourceCreatedAt);

            var staged = new StagedMediaImport(
                SourceAsset.Id,
                ProjectId,
                SourceJobId,
                SourceAsset.FileName,
                stagedPath,
                SourceAsset.ContentDigest,
                SourceAsset.ByteSize,
                SourceCreatedAt);
            BinaryVerifier binaryVerifier = new();
            FfprobeClient ffprobeClient = new(
                processRunner,
                binaryVerifier,
                _ffprobePath);
            SourceProbe = await ffprobeClient.ProbeAsync(
                staged,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            PtsIndexer ptsIndexer = new(
                processRunner,
                binaryVerifier,
                _ffprobePath);
            SourcePtsIndex = await ptsIndexer.IndexAsync(
                staged,
                SourceProbe,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            SourceOriginal = await Store.PromoteAsync(
                new ArtifactPromotionRequest(
                    SourceArtifactId,
                    ProjectId,
                    ArtifactKind.Original,
                    stagedPath,
                    $"{SourceAsset.Id}/{SourceAsset.ContentDigest.Hex}.original",
                    SourceAsset.ContentDigest,
                    SourceAsset.ByteSize,
                    SourceCreatedAt),
                CancellationToken.None);
            SourcePath = Store.GetValidatedRegisteredArtifactPath(SourceArtifactId);
        }

        public void Dispose() => Database.Dispose();
    }

    private sealed class ProxyFixture : IDisposable
    {
        private readonly BinaryVerifier _binaryVerifier = new();
        private readonly FixedTimeProvider _timeProvider = new(ProxyCreatedAt);
        private readonly bool _includeSourceAudio;

        internal ProxyFixture(bool includeSourceAudio = true)
        {
            _includeSourceAudio = includeSourceAudio;
            Database = new StoreTestDatabase();
            WorkspacePath = Path.Combine(Database.RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            Store = new ArtifactStore(Database.DatabasePath, WorkspacePath);
            ProjectId = EntityId<ProjectIdTag>.New();
            SourceJobId = EntityId<JobIdTag>.New();
            ProxyJobId = EntityId<JobIdTag>.New();
            SourceArtifactId = EntityId<ArtifactIdTag>.New();
            ProxyArtifactId = EntityId<ArtifactIdTag>.New();
            SourceContent = new byte[128];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(SourceContent, 4);
            Encoding.ASCII.GetBytes("source-original").CopyTo(SourceContent, 16);
            ProxyContent = new byte[512];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(ProxyContent, 4);
            Encoding.ASCII.GetBytes("generated-proxy").CopyTo(ProxyContent, 16);
            SourceAsset = CreateAsset(
                EntityId<AssetIdTag>.New(),
                "source.mov",
                ContentDigest.Compute(SourceContent),
                SourceContent.LongLength);
            var project = new Project(
                ProjectId,
                "Proxy generator test",
                "Verify M-008 proxy generation.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [SourceAsset],
                performers: []);
            new ProjectStore(Database.DatabasePath).CreateProject(
                project,
                "{\"proxyGenerator\":true}",
                SourceCreatedAt);

            string stagingPath = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "imports",
                SourceJobId.ToString(),
                "source.ready");
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.WriteAllBytes(stagingPath, SourceContent);
            SourceOriginal = Store.PromoteAsync(new ArtifactPromotionRequest(
                SourceArtifactId,
                ProjectId,
                ArtifactKind.Original,
                stagingPath,
                $"{SourceAsset.Id}/{SourceAsset.ContentDigest.Hex}.original",
                SourceAsset.ContentDigest,
                SourceAsset.ByteSize,
                SourceCreatedAt)).GetAwaiter().GetResult();
            SourcePath = Store.GetValidatedRegisteredArtifactPath(SourceArtifactId);
            SourcePtsIndex = CreateSourcePts();
            FfmpegPath = Path.Combine(Database.RootPath, "ffmpeg.exe");

            ProcessHandler = DefaultProcessAsync;
            ProbeHandler = DefaultProbeAsync;
            IndexHandler = DefaultIndexAsync;
            RegisteredVerificationHandler = VerifyRegisteredWithRealVerifierAsync;
        }

        internal StoreTestDatabase Database { get; }

        internal string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<JobIdTag> SourceJobId { get; }

        internal EntityId<JobIdTag> ProxyJobId { get; }

        internal EntityId<ArtifactIdTag> SourceArtifactId { get; }

        internal EntityId<ArtifactIdTag> ProxyArtifactId { get; }

        internal Asset SourceAsset { get; }

        internal StoredArtifact SourceOriginal { get; }

        internal PtsIndex SourcePtsIndex { get; }

        internal byte[] SourceContent { get; }

        internal byte[] ProxyContent { get; }

        internal string SourcePath { get; }

        internal string FfmpegPath { get; }

        internal List<ProcessRunRequest> ProcessRequests { get; } = [];

        internal Func<
            ProcessRunRequest,
            CancellationToken,
            Task<ProcessRunResult>>
            ProcessHandler
        { get; set; }

        internal Func<
            StagedMediaImport,
            TimeSpan,
            CancellationToken,
            Task<MediaProbeResult>>
            ProbeHandler
        { get; set; }

        internal Func<
            StagedMediaImport,
            MediaProbeResult,
            TimeSpan,
            CancellationToken,
            Task<PtsIndex>>
            IndexHandler
        { get; set; }

        internal Func<
            EntityId<ArtifactIdTag>,
            CancellationToken,
            Task<BinaryVerificationResult>>
            RegisteredVerificationHandler
        { get; set; }

        internal Task<GeneratedProxy> GenerateAsync(
            EntityId<ArtifactIdTag>? proxyArtifactId = null,
            StoredArtifact? sourceOriginal = null,
            PtsIndex? sourcePtsIndex = null,
            CancellationToken cancellationToken = default) =>
            CreateGenerator().GenerateAsync(
                proxyArtifactId ?? ProxyArtifactId,
                SourceAsset,
                sourceOriginal ?? SourceOriginal,
                sourcePtsIndex ?? SourcePtsIndex,
                ProxyJobId,
                TimeSpan.FromSeconds(5),
                cancellationToken);

        internal PtsIndex CreateSourcePts(
            EntityId<AssetIdTag>? candidateId = null,
            EntityId<ProjectIdTag>? projectId = null,
            string? sourceFileName = null,
            ContentDigest? contentDigest = null,
            long? byteSize = null,
            int streamIndex = 2,
            RationalTime? timeBase = null) =>
            new(
                candidateId ?? SourceAsset.Id,
                projectId ?? ProjectId,
                SourceJobId,
                sourceFileName ?? SourceAsset.FileName,
                contentDigest ?? SourceAsset.ContentDigest,
                byteSize ?? SourceAsset.ByteSize,
                streamIndex,
                timeBase ?? new RationalTime(1, 1000),
                [
                    new PtsIndexEntry(
                        0,
                        new RationalTime(0, 1),
                        new RationalTime(1, 50),
                        keyFrame: true),
                    new PtsIndexEntry(
                        1,
                        new RationalTime(1, 50),
                        new RationalTime(1, 50),
                        keyFrame: false),
                    new PtsIndexEntry(
                        2,
                        new RationalTime(1, 25),
                        new RationalTime(1, 50),
                        keyFrame: false),
                ]);

        internal StagedMediaImport CreateValidationProxyMedia()
        {
            string path = Path.Combine(
                Database.RootPath,
                string.Concat(ProxyArtifactId.ToString(), ".mp4"));
            File.WriteAllBytes(path, ProxyContent);
            return new StagedMediaImport(
                SourceAsset.Id,
                ProjectId,
                ProxyJobId,
                Path.GetFileName(path),
                path,
                ContentDigest.Compute(ProxyContent),
                ProxyContent.LongLength,
                ProxyCreatedAt);
        }

        internal (MediaProbeResult Probe, PtsIndex Index) CreateOutputEvidence(
            StagedMediaImport staged,
            string videoCodec = "h264",
            int width = 640,
            int height = 360,
            string pixelFormat = "yuv420p",
            RationalNumber? nominalFrameRate = null,
            RationalNumber? averageFrameRate = null,
            IReadOnlyList<string>? formatNames = null,
            int videoIndex = 0,
            int audioStartIndex = 1,
            int audioCount = 1,
            string audioCodec = "aac",
            bool additionalVideo = false,
            bool includeOtherStream = false,
            IReadOnlyList<string>? warnings = null,
            ContainerSignature containerSignature = ContainerSignature.IsoBaseMediaFtyp,
            bool omitDeclaredByteSize = false,
            RationalTime? ptsTimeBase = null)
        {
            MediaProbeResult probe = CreateOutputProbe(
                staged,
                videoCodec,
                width,
                height,
                pixelFormat,
                nominalFrameRate,
                averageFrameRate,
                formatNames,
                videoIndex,
                audioStartIndex,
                audioCount,
                audioCodec,
                additionalVideo,
                includeOtherStream,
                warnings,
                containerSignature,
                omitDeclaredByteSize);
            RationalTime timeBase = ptsTimeBase ?? new RationalTime(1001, 30000);
            PtsIndex index = CreateOutputPts(
                staged,
                streamIndex: videoIndex,
                timeBase: timeBase);
            return (probe, index);
        }

        internal PtsIndex CreateOutputPts(
            StagedMediaImport staged,
            int entryCount = 60,
            IReadOnlyList<int>? keyFrameSequences = null,
            int streamIndex = 0,
            RationalTime? timeBase = null)
        {
            keyFrameSequences ??= [0, 30];
            HashSet<int> keyFrames = keyFrameSequences.ToHashSet();
            RationalTime actualTimeBase = timeBase ?? new RationalTime(1001, 30000);
            PtsIndexEntry[] entries = Enumerable.Range(0, entryCount)
                .Select(index => new PtsIndexEntry(
                    index,
                    new RationalTime(checked(index * 1001L), 30000),
                    new RationalTime(1001, 30000),
                    keyFrames.Contains(index)))
                .ToArray();
            return new PtsIndex(
                staged.CandidateId,
                staged.ProjectId,
                staged.JobId,
                staged.SourceFileName,
                staged.ContentDigest,
                staged.ByteSize,
                streamIndex,
                actualTimeBase,
                entries);
        }

        internal string GetStagingDirectory() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "proxies",
                ProxyJobId.ToString());

        internal string GetStagingOutputPath() =>
            Path.Combine(
                GetStagingDirectory(),
                string.Concat(ProxyArtifactId.ToString(), ".partial.mp4"));

        internal string GetFinalProxyPath() =>
            Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "proxy",
                SourceAsset.Id.ToString(),
                string.Concat(ProxyArtifactId.ToString(), ".mp4"));

        internal long CountArtifacts() =>
            Database.ExecuteScalarInt64("SELECT COUNT(*) FROM artifacts;");

        internal Task<BinaryVerificationResult> VerifyRegisteredWithRealVerifierAsync(
            EntityId<ArtifactIdTag> artifactId,
            CancellationToken cancellationToken) =>
            Store.VerifyRegisteredAsync(
                artifactId,
                _binaryVerifier,
                cancellationToken);

        internal static ProcessRunResult SuccessfulProcessResult() =>
            new(
                0,
                string.Empty,
                string.Empty,
                false,
                TimeSpan.FromMilliseconds(7));

        private ProxyGenerator CreateGenerator() =>
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
                (staged, timeout, cancellationToken) =>
                    ProbeHandler(staged, timeout, cancellationToken),
                (staged, probe, timeout, cancellationToken) =>
                    IndexHandler(staged, probe, timeout, cancellationToken),
                (artifactId, cancellationToken) =>
                    RegisteredVerificationHandler(artifactId, cancellationToken),
                WorkspacePath,
                FfmpegPath,
                _timeProvider);

        private Task<ProcessRunResult> DefaultProcessAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRequests.Add(request);
            File.WriteAllBytes(request.ArgumentList[^1], ProxyContent);
            return Task.FromResult(SuccessfulProcessResult());
        }

        private Task<MediaProbeResult> DefaultProbeAsync(
            StagedMediaImport staged,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(TimeSpan.FromSeconds(5), timeout);
            MediaProbeResult result = string.Equals(
                staged.StagingPath,
                SourcePath,
                StringComparison.OrdinalIgnoreCase)
                ? CreateSourceProbe(staged)
                : CreateOutputProbe(staged, audioCount: _includeSourceAudio ? 1 : 0);
            return Task.FromResult(result);
        }

        private Task<PtsIndex> DefaultIndexAsync(
            StagedMediaImport staged,
            MediaProbeResult probe,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(TimeSpan.FromSeconds(5), timeout);
            Assert.AreEqual(staged.ContentDigest, probe.ContentDigest);
            return Task.FromResult(CreateOutputPts(staged));
        }

        private MediaProbeResult CreateSourceProbe(StagedMediaImport staged)
        {
            List<ProbedMediaStream> streams =
            [
                CreateVideoStream(
                    index: 7,
                    codecName: "hevc",
                    width: 1920,
                    height: 1080,
                    nominalFrameRate: new RationalNumber(60000, 1001),
                    averageFrameRate: new RationalNumber(24000, 1001),
                    timeBase: new RationalNumber(1, 90000)),
            ];
            if (_includeSourceAudio)
            {
                streams.Add(CreateAudioStream(index: 4, codecName: "aac"));
            }

            streams.Add(CreateVideoStream(
                index: 2,
                codecName: "hevc",
                width: 1920,
                height: 1080,
                nominalFrameRate: new RationalNumber(60000, 1001),
                averageFrameRate: new RationalNumber(24000, 1001),
                timeBase: new RationalNumber(1, 1000)));
            List<string> warnings = ["multiple-video-streams", "variable-rate-fields-differ"];
            if (!_includeSourceAudio)
            {
                warnings.Add("audio-track-absent");
            }

            return new MediaProbeResult(
                staged.CandidateId,
                staged.ProjectId,
                staged.JobId,
                staged.SourceFileName,
                staged.ContentDigest,
                staged.ByteSize,
                ContainerSignature.IsoBaseMediaFtyp,
                ["mov", "mp4"],
                "QuickTime / MOV",
                durationSeconds: 1m,
                declaredByteSize: staged.ByteSize,
                bitRate: null,
                probeScore: 100,
                streams,
                warnings);
        }

        private static MediaProbeResult CreateOutputProbe(
            StagedMediaImport staged,
            string videoCodec = "h264",
            int width = 640,
            int height = 360,
            string pixelFormat = "yuv420p",
            RationalNumber? nominalFrameRate = null,
            RationalNumber? averageFrameRate = null,
            IReadOnlyList<string>? formatNames = null,
            int videoIndex = 0,
            int audioStartIndex = 1,
            int audioCount = 1,
            string audioCodec = "aac",
            bool additionalVideo = false,
            bool includeOtherStream = false,
            IReadOnlyList<string>? warnings = null,
            ContainerSignature containerSignature = ContainerSignature.IsoBaseMediaFtyp,
            bool omitDeclaredByteSize = false)
        {
            List<ProbedMediaStream> streams =
            [
                CreateVideoStream(
                    videoIndex,
                    videoCodec,
                    width,
                    height,
                    nominalFrameRate ?? new RationalNumber(30000, 1001),
                    averageFrameRate ?? new RationalNumber(30000, 1001),
                    new RationalNumber(1001, 30000),
                    pixelFormat),
            ];
            for (int index = 0; index < audioCount; index++)
            {
                streams.Add(CreateAudioStream(audioStartIndex + index, audioCodec));
            }

            if (additionalVideo)
            {
                streams.Add(CreateVideoStream(
                    index: 7,
                    codecName: "h264",
                    width: 640,
                    height: 360,
                    nominalFrameRate: new RationalNumber(30000, 1001),
                    averageFrameRate: new RationalNumber(30000, 1001),
                    timeBase: new RationalNumber(1001, 30000)));
            }

            if (includeOtherStream)
            {
                streams.Add(new ProbedMediaStream(
                    9,
                    MediaStreamKind.Other,
                    "data",
                    profile: null,
                    width: null,
                    height: null,
                    pixelFormat: null,
                    nominalFrameRate: null,
                    averageFrameRate: null,
                    new RationalNumber(1, 1000),
                    startSeconds: null,
                    durationSeconds: null,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null));
            }

            IReadOnlyList<string> actualWarnings = warnings ??
                (audioCount == 0 ? ["audio-track-absent"] : []);
            return new MediaProbeResult(
                staged.CandidateId,
                staged.ProjectId,
                staged.JobId,
                staged.SourceFileName,
                staged.ContentDigest,
                staged.ByteSize,
                containerSignature,
                formatNames ?? ["mov", "mp4"],
                "QuickTime / MOV",
                durationSeconds: 1m,
                declaredByteSize: omitDeclaredByteSize ? null : staged.ByteSize,
                bitRate: null,
                probeScore: 100,
                streams,
                actualWarnings);
        }

        private static ProbedMediaStream CreateVideoStream(
            int index,
            string codecName,
            int width,
            int height,
            RationalNumber nominalFrameRate,
            RationalNumber averageFrameRate,
            RationalNumber timeBase,
            string pixelFormat = "yuv420p") =>
            new(
                index,
                MediaStreamKind.Video,
                codecName,
                "High",
                width,
                height,
                pixelFormat,
                nominalFrameRate,
                averageFrameRate,
                timeBase,
                startSeconds: 0m,
                durationSeconds: 1m,
                sampleRate: null,
                channels: null,
                channelLayout: null);

        private static ProbedMediaStream CreateAudioStream(
            int index,
            string codecName) =>
            new(
                index,
                MediaStreamKind.Audio,
                codecName,
                "LC",
                width: null,
                height: null,
                pixelFormat: null,
                nominalFrameRate: null,
                averageFrameRate: null,
                new RationalNumber(1, 48000),
                startSeconds: 0m,
                durationSeconds: 1m,
                sampleRate: 48000,
                channels: 2,
                channelLayout: "stereo");

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
                "HubRadio.ProxyGeneratorTests",
                Guid.NewGuid().ToString("N"));
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
