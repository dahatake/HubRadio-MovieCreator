using System.Diagnostics;
using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Media;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class MediaImporterTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.FromHours(9));

    [TestMethod]
    public async Task CopyToStagingAsync_CopiesExactBytesAndReturnsCanonicalMetadata()
    {
        using ImportFixture fixture = new();
        byte[] content = Enumerable.Range(0, (64 * 1024 * 2) + 137)
            .Select(static index => (byte)(index % 251))
            .ToArray();
        string sourcePath = fixture.CreateSource("meeting-source.mov", content);
        File.SetLastWriteTimeUtc(
            sourcePath,
            new DateTime(2033, 4, 5, 6, 7, 8, DateTimeKind.Utc));
        DateTime sourceLastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);
        ContentDigest sourceDigestBefore = ContentDigest.Compute(File.ReadAllBytes(sourcePath));
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        StagedMediaImport staged = await fixture.Importer.CopyToStagingAsync(
            candidateId,
            projectId,
            jobId,
            CancellationToken.None);

        string expectedReadyPath = fixture.GetReadyPath(candidateId, projectId, jobId);
        Assert.AreEqual(candidateId, staged.CandidateId);
        Assert.AreEqual(projectId, staged.ProjectId);
        Assert.AreEqual(jobId, staged.JobId);
        Assert.AreEqual("meeting-source.mov", staged.SourceFileName);
        Assert.AreEqual(Path.GetFullPath(expectedReadyPath), staged.StagingPath);
        Assert.IsTrue(Path.IsPathFullyQualified(staged.StagingPath));
        Assert.AreEqual(ContentDigest.Compute(content), staged.ContentDigest);
        Assert.AreEqual(content.LongLength, staged.ByteSize);
        Assert.AreEqual(CreatedAt.ToUniversalTime(), staged.CreatedAt);
        Assert.IsTrue(File.Exists(expectedReadyPath));
        Assert.IsFalse(File.Exists(fixture.GetPartialPath(candidateId, projectId, jobId)));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(expectedReadyPath));
        Assert.AreEqual(
            ContentDigest.Compute(content),
            ContentDigest.Compute(File.ReadAllBytes(expectedReadyPath)));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(sourcePath));
        Assert.AreEqual(sourceDigestBefore, ContentDigest.Compute(File.ReadAllBytes(sourcePath)));
        Assert.AreEqual(sourceLastWriteBefore, File.GetLastWriteTimeUtc(sourcePath));
        Assert.AreEqual(content.LongLength, new FileInfo(sourcePath).Length);
        Assert.AreEqual(0, fixture.Registry.PendingCount);
    }

    [TestMethod]
    public async Task CopyToStagingAsync_ClaimsCandidateExactlyOnce()
    {
        using ImportFixture fixture = new();
        byte[] content = Encoding.UTF8.GetBytes("single-use candidate bytes");
        string sourcePath = fixture.CreateSource("single-use.mp4", content);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        StagedMediaImport first = await fixture.Importer.CopyToStagingAsync(
            candidateId,
            projectId,
            jobId,
            CancellationToken.None);
        byte[] stagedBefore = File.ReadAllBytes(first.StagingPath);

        MediaImportException second = await Assert.ThrowsExactlyAsync<MediaImportException>(
            () => fixture.Importer.CopyToStagingAsync(
                candidateId,
                projectId,
                jobId,
                CancellationToken.None));

        AssertSafeFailure(
            second,
            MediaImportFailureReason.CandidateUnavailable,
            sourcePath,
            fixture.WorkspacePath,
            Path.GetFileName(sourcePath));
        CollectionAssert.AreEqual(stagedBefore, File.ReadAllBytes(first.StagingPath));
        Assert.AreEqual(0, fixture.Registry.PendingCount);
    }

    [TestMethod]
    public async Task CopyToStagingAsync_ConcurrentClaimHasExactlyOneWinner()
    {
        using ImportFixture fixture = new();
        string sourcePath = fixture.CreateSource(
            "concurrent.mov",
            Encoding.UTF8.GetBytes("concurrent claim bytes"));
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        using ManualResetEventSlim release = new(initialState: false);

        Task<ImportOutcome>[] attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () =>
            {
                release.Wait();
                try
                {
                    return new ImportOutcome(
                        await fixture.Importer.CopyToStagingAsync(
                            candidateId,
                            projectId,
                            jobId,
                            CancellationToken.None),
                        Exception: null);
                }
                catch (MediaImportException exception)
                {
                    return new ImportOutcome(Result: null, exception);
                }
            }))
            .ToArray();
        release.Set();

        ImportOutcome[] outcomes = await Task.WhenAll(attempts);

        ImportOutcome winner = outcomes.Single(static outcome => outcome.Result is not null);
        Assert.AreEqual(15, outcomes.Count(static outcome => outcome.Exception is not null));
        Assert.IsTrue(outcomes
            .Where(static outcome => outcome.Exception is not null)
            .All(static outcome =>
                outcome.Exception!.Reason == MediaImportFailureReason.CandidateUnavailable));
        Assert.IsTrue(File.Exists(winner.Result!.StagingPath));
        Assert.IsFalse(File.Exists(fixture.GetPartialPath(candidateId, projectId, jobId)));
        Assert.AreEqual(0, fixture.Registry.PendingCount);
    }

    [TestMethod]
    public async Task CopyToStagingAsync_PreCancelledTokenLeavesCandidateClaimable()
    {
        using ImportFixture fixture = new();
        string sourcePath = fixture.CreateSource("pre-cancel.mov", [1, 2, 3]);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => fixture.Importer.CopyToStagingAsync(
                    candidateId,
                    projectId,
                    jobId,
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(1, fixture.Registry.PendingCount);
        Assert.IsTrue(fixture.Registry.TryClaim(candidateId, out string? claimedPath));
        Assert.AreEqual(sourcePath, claimedPath);
        Assert.AreEqual(0, fixture.Registry.PendingCount);
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(projectId, jobId)));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_PostClaimCancellationCleansFilesAndSourceLockBlocksMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file-sharing semantics are required for this test.");
        }

        const long sourceLength = 512L * 1024 * 1024;
        using ImportFixture fixture = new();
        string sourcePath = fixture.CreateSource("cancel-large.mov", [0x5a]);
        SetSparseLength(sourcePath, sourceLength);
        DateTime sourceLastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);
        byte[] sourceMarkersBefore = ReadFirstAndLastByte(sourcePath);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        using CancellationTokenSource cancellation = new();

        Task<StagedMediaImport> operation = fixture.Importer.CopyToStagingAsync(
            candidateId,
            projectId,
            jobId,
            cancellation.Token);
        await WaitForSourceWriteLockAsync(sourcePath, operation);

        Assert.ThrowsExactly<IOException>(() =>
            new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete).Dispose());
        Assert.ThrowsExactly<IOException>(() => File.Delete(sourcePath));
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsFalse(File.Exists(fixture.GetPartialPath(candidateId, projectId, jobId)));
        Assert.IsFalse(File.Exists(fixture.GetReadyPath(candidateId, projectId, jobId)));
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.AreEqual(sourceLength, new FileInfo(sourcePath).Length);
        Assert.AreEqual(sourceLastWriteBefore, File.GetLastWriteTimeUtc(sourcePath));
        CollectionAssert.AreEqual(sourceMarkersBefore, ReadFirstAndLastByte(sourcePath));
        Assert.IsFalse(fixture.Registry.TryClaim(candidateId, out _));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_MissingSourceFailsSafelyAndConsumesCandidate()
    {
        using ImportFixture fixture = new();
        string sourcePath = Path.Combine(fixture.SourceDirectory, "missing-private.mov");
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        MediaImportException exception = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.SourceMissing,
            sourcePath);

        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(projectId, jobId)));
        Assert.IsFalse(fixture.Registry.TryClaim(candidateId, out _));
        Assert.IsFalse(exception.RecoveryRequired);
    }

    [TestMethod]
    public async Task CopyToStagingAsync_DirectorySourceFailsAsNonRegularFile()
    {
        using ImportFixture fixture = new();
        string sourcePath = Path.Combine(fixture.SourceDirectory, "private-directory.mov");
        Directory.CreateDirectory(sourcePath);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.SourceNotRegularFile,
            sourcePath);

        Assert.IsTrue(Directory.Exists(sourcePath));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(projectId, jobId)));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_SourceFileReparsePointIsRejectedWhenAvailable()
    {
        using ImportFixture fixture = new();
        byte[] content = Encoding.UTF8.GetBytes("source link target bytes");
        string targetPath = fixture.CreateSource("source-link-target.mov", content);
        string linkPath = Path.Combine(fixture.SourceDirectory, "private-source-link.mov");
        if (!TryCreateFileSymbolicLink(linkPath, targetPath))
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
        }

        EntityId<AssetIdTag> candidateId = fixture.Register(linkPath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.SourceReparsePoint,
            linkPath,
            targetPath);

        Assert.IsTrue(File.Exists(linkPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(targetPath));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(projectId, jobId)));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_SourceAncestorReparsePointIsRejectedWhenAvailable()
    {
        using ImportFixture fixture = new();
        string targetDirectory = Path.Combine(fixture.RootPath, "source-target-directory");
        Directory.CreateDirectory(targetDirectory);
        byte[] content = Encoding.UTF8.GetBytes("source ancestor target bytes");
        string targetPath = Path.Combine(targetDirectory, "private-ancestor.mov");
        File.WriteAllBytes(targetPath, content);
        string linkDirectory = Path.Combine(fixture.SourceDirectory, "source-directory-link");
        if (!TryCreateDirectorySymbolicLink(linkDirectory, targetDirectory))
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
        }

        string sourcePath = Path.Combine(linkDirectory, "private-ancestor.mov");
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.SourceReparsePoint,
            sourcePath,
            targetPath);

        CollectionAssert.AreEqual(content, File.ReadAllBytes(targetPath));
        Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(projectId, jobId)));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_RelativeNetworkDeviceAndAdsSourcesAreRejected()
    {
        using ImportFixture fixture = new();
        string ordinaryPath = fixture.CreateSource("ads-base.mov", [1]);
        string[] unsafePaths =
        [
            "relative-private.mov",
            @"\\server\share\private.mov",
            @"\\.\pipe\hubradio-media-import-private",
            ordinaryPath + ":private-stream",
        ];

        foreach (string sourcePath in unsafePaths)
        {
            EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

            _ = await AssertImportFailure(
                fixture,
                candidateId,
                projectId,
                jobId,
                MediaImportFailureReason.UnsafeSource,
                sourcePath);

            Assert.IsFalse(Directory.Exists(fixture.GetStagingDirectory(projectId, jobId)));
        }
    }

    [TestMethod]
    public async Task CopyToStagingAsync_EmptySourceIsRejectedWithoutStagingFile()
    {
        using ImportFixture fixture = new();
        string sourcePath = fixture.CreateSource("empty-private.mov", []);
        DateTime lastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.EmptySource,
            sourcePath);

        Assert.IsFalse(File.Exists(fixture.GetPartialPath(candidateId, projectId, jobId)));
        Assert.IsFalse(File.Exists(fixture.GetReadyPath(candidateId, projectId, jobId)));
        Assert.AreEqual(0L, new FileInfo(sourcePath).Length);
        Assert.AreEqual(lastWriteBefore, File.GetLastWriteTimeUtc(sourcePath));
    }

    [TestMethod]
    [DataRow("partial")]
    [DataRow("ready")]
    public async Task CopyToStagingAsync_PreexistingDestinationIsPreserved(
        string destinationKind)
    {
        using ImportFixture fixture = new();
        byte[] sourceContent = Encoding.UTF8.GetBytes("new private source bytes");
        byte[] existingContent = Encoding.UTF8.GetBytes("preexisting destination bytes");
        string sourcePath = fixture.CreateSource("destination-collision.mov", sourceContent);
        DateTime sourceLastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);
        ContentDigest sourceDigestBefore = ContentDigest.Compute(File.ReadAllBytes(sourcePath));
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        string partialPath = fixture.GetPartialPath(candidateId, projectId, jobId);
        string readyPath = fixture.GetReadyPath(candidateId, projectId, jobId);
        string existingPath = destinationKind == "partial" ? partialPath : readyPath;
        Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
        File.WriteAllBytes(existingPath, existingContent);

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.DestinationExists,
            sourcePath,
            existingPath,
            Encoding.UTF8.GetString(sourceContent));

        CollectionAssert.AreEqual(existingContent, File.ReadAllBytes(existingPath));
        Assert.AreEqual(destinationKind == "partial", File.Exists(partialPath));
        Assert.AreEqual(destinationKind == "ready", File.Exists(readyPath));
        Assert.AreEqual(sourceDigestBefore, ContentDigest.Compute(File.ReadAllBytes(sourcePath)));
        Assert.AreEqual(sourceLastWriteBefore, File.GetLastWriteTimeUtc(sourcePath));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_DifferentCandidatesForSameSourceCreateIndependentReadyFiles()
    {
        using ImportFixture fixture = new();
        byte[] content = Encoding.UTF8.GetBytes("same source independent imports");
        string sourcePath = fixture.CreateSource("same-source.mov", content);
        DateTime sourceLastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);
        EntityId<AssetIdTag> firstCandidate = fixture.Register(sourcePath);
        EntityId<AssetIdTag> secondCandidate = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        StagedMediaImport first = await fixture.Importer.CopyToStagingAsync(
            firstCandidate,
            projectId,
            jobId,
            CancellationToken.None);
        StagedMediaImport second = await fixture.Importer.CopyToStagingAsync(
            secondCandidate,
            projectId,
            jobId,
            CancellationToken.None);

        Assert.AreNotEqual(first.StagingPath, second.StagingPath);
        Assert.AreEqual(first.ContentDigest, second.ContentDigest);
        Assert.AreEqual(first.ByteSize, second.ByteSize);
        CollectionAssert.AreEqual(content, File.ReadAllBytes(first.StagingPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(second.StagingPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(sourcePath));
        Assert.AreEqual(sourceLastWriteBefore, File.GetLastWriteTimeUtc(sourcePath));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_SourceInsideDestinationDirectoryIsRejected()
    {
        using ImportFixture fixture = new();
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        string stagingDirectory = fixture.GetStagingDirectory(projectId, jobId);
        Directory.CreateDirectory(stagingDirectory);
        byte[] content = Encoding.UTF8.GetBytes("must not self-import");
        string sourcePath = Path.Combine(stagingDirectory, "source-inside-staging.mov");
        File.WriteAllBytes(sourcePath, content);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.UnsafeSource,
            sourcePath);

        CollectionAssert.AreEqual(content, File.ReadAllBytes(sourcePath));
        Assert.IsFalse(File.Exists(fixture.GetPartialPath(candidateId, projectId, jobId)));
        Assert.IsFalse(File.Exists(fixture.GetReadyPath(candidateId, projectId, jobId)));
    }

    [TestMethod]
    public async Task CopyToStagingAsync_StagingAncestorReparsePointIsRejectedWhenAvailable()
    {
        using ImportFixture fixture = new();
        string sourcePath = fixture.CreateSource("safe-source.mov", [1, 2, 3]);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        string outsideTarget = Path.Combine(fixture.RootPath, "outside-projects-target");
        Directory.CreateDirectory(outsideTarget);
        string projectsLink = Path.Combine(fixture.WorkspacePath, "projects");
        if (!TryCreateDirectorySymbolicLink(projectsLink, outsideTarget))
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
        }

        _ = await AssertImportFailure(
            fixture,
            candidateId,
            projectId,
            jobId,
            MediaImportFailureReason.CopyFailed,
            sourcePath,
            projectsLink,
            outsideTarget);

        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(outsideTarget).Count());
        Assert.IsFalse(File.Exists(fixture.GetPartialPath(candidateId, projectId, jobId)));
        Assert.IsFalse(File.Exists(fixture.GetReadyPath(candidateId, projectId, jobId)));
    }

    [TestMethod]
    public void ConstructorRequiresExistingAbsoluteLocalDirectoryWithoutReparsePoints()
    {
        using ImportFixture fixture = new();
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new MediaImporter(null!, fixture.WorkspacePath));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new MediaImporter(
                fixture.Registry,
                fixture.WorkspacePath,
                timeProvider: null!));

        string[] invalidPaths =
        [
            "relative-workspace",
            @"\\server\share\workspace",
            @"\\?\C:\private-workspace",
            fixture.WorkspacePath + ":private-stream",
        ];
        foreach (string invalidPath in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new MediaImporter(fixture.Registry, invalidPath));
            Assert.AreEqual("workspacePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }

        string missingPath = Path.Combine(fixture.RootPath, "missing-private-workspace");
        ArgumentException missing = Assert.ThrowsExactly<ArgumentException>(
            () => new MediaImporter(fixture.Registry, missingPath));
        Assert.AreEqual("workspacePath", missing.ParamName);
        AssertDoesNotContain(missing.Message, missingPath);

        string filePath = Path.Combine(fixture.RootPath, "private-workspace-file");
        File.WriteAllBytes(filePath, [1]);
        ArgumentException file = Assert.ThrowsExactly<ArgumentException>(
            () => new MediaImporter(fixture.Registry, filePath));
        Assert.AreEqual("workspacePath", file.ParamName);
        AssertDoesNotContain(file.Message, filePath);
    }

    [TestMethod]
    public void ConstructorRejectsReparsePointWorkspaceWhenAvailable()
    {
        using ImportFixture fixture = new();
        string targetPath = Path.Combine(fixture.RootPath, "workspace-link-target");
        Directory.CreateDirectory(targetPath);
        string linkPath = Path.Combine(fixture.RootPath, "private-workspace-link");
        if (!TryCreateDirectorySymbolicLink(linkPath, targetPath))
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
        }

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new MediaImporter(fixture.Registry, linkPath));

        Assert.AreEqual("workspacePath", exception.ParamName);
        AssertDoesNotContain(exception.Message, linkPath);
        AssertDoesNotContain(exception.Message, targetPath);
    }

    [TestMethod]
    public async Task CopyToStagingAsync_NullArgumentsDoNotConsumeCandidate()
    {
        using ImportFixture fixture = new();
        string sourcePath = fixture.CreateSource("null-arguments.mov", [1]);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            fixture.Importer.CopyToStagingAsync(
                null!,
                projectId,
                jobId,
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            fixture.Importer.CopyToStagingAsync(
                candidateId,
                null!,
                jobId,
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            fixture.Importer.CopyToStagingAsync(
                candidateId,
                projectId,
                null!,
                CancellationToken.None));

        Assert.AreEqual(1, fixture.Registry.PendingCount);
        Assert.IsTrue(fixture.Registry.TryClaim(candidateId, out string? claimedPath));
        Assert.AreEqual(sourcePath, claimedPath);
    }

    [TestMethod]
    public void PublicContractsAreSealedImmutablePathSafeAndUseStableMessages()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "CandidateUnavailable",
                "UnsafeSource",
                "SourceMissing",
                "SourceNotRegularFile",
                "SourceReparsePoint",
                "EmptySource",
                "DestinationExists",
                "CopyFailed",
                "PartialCleanupFailed",
            },
            Enum.GetNames<MediaImportFailureReason>());

        Assert.IsTrue(typeof(MediaImportException).IsPublic);
        Assert.IsTrue(typeof(MediaImportException).IsSealed);
        Assert.IsTrue(typeof(StagedMediaImport).IsPublic);
        Assert.IsTrue(typeof(StagedMediaImport).IsSealed);
        Assert.IsTrue(typeof(MediaImporter).IsPublic);
        Assert.IsTrue(typeof(MediaImporter).IsSealed);

        PropertyInfo[] stagedProperties = typeof(StagedMediaImport)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.IsTrue(stagedProperties.All(static property => property.SetMethod is null));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "CandidateId",
                "ProjectId",
                "JobId",
                "SourceFileName",
                "StagingPath",
                "ContentDigest",
                "ByteSize",
                "CreatedAt",
            },
            stagedProperties.Select(static property => property.Name).ToArray());
        Assert.IsFalse(stagedProperties.Any(static property =>
            property.Name.Equals("SourcePath", StringComparison.OrdinalIgnoreCase)));

        PropertyInfo[] exceptionProperties = typeof(MediaImportException)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.IsTrue(exceptionProperties.All(static property => property.SetMethod is null));
        CollectionAssert.AreEquivalent(
            new[] { "Reason", "RecoveryRequired" },
            exceptionProperties.Select(static property => property.Name).ToArray());

        const string privateSourcePath = @"C:\private\customer-secret-recording.mov";
        const string privateWorkspacePath = @"C:\private\workspace";
        const string privateFileName = "customer-secret-recording.mov";
        foreach (MediaImportFailureReason reason in Enum.GetValues<MediaImportFailureReason>())
        {
            var exception = new MediaImportException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.AreEqual(
                reason == MediaImportFailureReason.PartialCleanupFailed,
                exception.RecoveryRequired);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.Message, privateSourcePath);
            AssertDoesNotContain(exception.Message, privateWorkspacePath);
            AssertDoesNotContain(exception.Message, privateFileName);
        }
    }

    [TestMethod]
    public void StagedMediaImportConstructorRejectsInvalidLeafNamesAndByteSizes()
    {
        using ImportFixture fixture = new();
        EntityId<AssetIdTag> candidateId = EntityId<AssetIdTag>.New();
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        ContentDigest digest = ContentDigest.Compute([1]);
        string stagingPath = fixture.GetReadyPath(candidateId, projectId, jobId);
        foreach (string invalidName in new[]
        {
            string.Empty,
            " ",
            "nested/source.mov",
            @"nested\source.mov",
            "source\r\nforged.mov",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                new StagedMediaImport(
                    candidateId,
                    projectId,
                    jobId,
                    invalidName,
                    stagingPath,
                    digest,
                    1,
                    CreatedAt));
            Assert.AreEqual("sourceFileName", exception.ParamName);
            if (!string.IsNullOrWhiteSpace(invalidName))
            {
                AssertDoesNotContain(exception.Message, invalidName);
            }
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StagedMediaImport(
                candidateId,
                projectId,
                jobId,
                "source.mov",
                stagingPath,
                digest,
                0,
                CreatedAt));
    }

    private static async Task<MediaImportException> AssertImportFailure(
        ImportFixture fixture,
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        MediaImportFailureReason reason,
        params string[] forbiddenValues)
    {
        MediaImportException exception =
            await Assert.ThrowsExactlyAsync<MediaImportException>(() =>
                fixture.Importer.CopyToStagingAsync(
                    candidateId,
                    projectId,
                    jobId,
                    CancellationToken.None));
        AssertSafeFailure(
            exception,
            reason,
            [fixture.WorkspacePath, .. forbiddenValues]);
        return exception;
    }

    private static void AssertSafeFailure(
        MediaImportException exception,
        MediaImportFailureReason reason,
        params string[] forbiddenValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(
            reason == MediaImportFailureReason.PartialCleanupFailed,
            exception.RecoveryRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string forbiddenValue in forbiddenValues)
        {
            AssertDoesNotContain(exception.Message, forbiddenValue);
            AssertDoesNotContain(exception.ToString(), forbiddenValue);
        }
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (forbiddenValue.Length == 0)
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetSparseLength(string path, long length)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
        stream.SetLength(length);
    }

    private static byte[] ReadFirstAndLastByte(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int first = stream.ReadByte();
        stream.Position = stream.Length - 1;
        int last = stream.ReadByte();
        return [(byte)first, (byte)last];
    }

    private static async Task WaitForSourceWriteLockAsync(string path, Task operation)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!operation.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                using FileStream writer = new(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail("The media import source lock was not observed before completion.");
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed record ImportOutcome(
        StagedMediaImport? Result,
        MediaImportException? Exception);

    private sealed class ImportFixture : IDisposable
    {
        internal ImportFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.MediaImporterTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            SourceDirectory = Path.Combine(RootPath, "sources");
            Directory.CreateDirectory(WorkspacePath);
            Directory.CreateDirectory(SourceDirectory);
            Registry = new AssetCandidateRegistry();
            Importer = new MediaImporter(
                Registry,
                WorkspacePath,
                new FixedTimeProvider(CreatedAt));
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal string SourceDirectory { get; }

        internal AssetCandidateRegistry Registry { get; }

        internal MediaImporter Importer { get; }

        internal string CreateSource(string fileName, byte[] content)
        {
            string path = Path.Combine(SourceDirectory, fileName);
            File.WriteAllBytes(path, content);
            return path;
        }

        internal EntityId<AssetIdTag> Register(string path) =>
            Registry.RegisterSelection(path)
            ?? throw new AssertFailedException("A source path must create a candidate.");

        internal string GetStagingDirectory(
            EntityId<ProjectIdTag> projectId,
            EntityId<JobIdTag> jobId) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                "staging",
                "imports",
                jobId.ToString());

        internal string GetPartialPath(
            EntityId<AssetIdTag> candidateId,
            EntityId<ProjectIdTag> projectId,
            EntityId<JobIdTag> jobId) =>
            Path.Combine(
                GetStagingDirectory(projectId, jobId),
                string.Concat(candidateId.ToString(), ".partial"));

        internal string GetReadyPath(
            EntityId<AssetIdTag> candidateId,
            EntityId<ProjectIdTag> projectId,
            EntityId<JobIdTag> jobId) =>
            Path.Combine(
                GetStagingDirectory(projectId, jobId),
                string.Concat(candidateId.ToString(), ".ready"));

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}