using System.Diagnostics;
using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Recovery;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class WorkspaceRecoveryTests
{
    private static readonly DateTimeOffset CheckedAt =
        new(2034, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [TestMethod]
    public async Task CheckAsync_EmptyMigratedWorkspaceIsUsableAndIntact()
    {
        using RecoveryFixture fixture = new();

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.AreEqual(CheckedAt, result.CheckedAt);
        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsTrue(result.IsIntact);
        Assert.AreEqual(0, result.Issues.Count);
        Assert.AreEqual(0, result.StagingCandidates.Count);
    }

    [TestMethod]
    public async Task CheckAsync_ReadOnlyDatabaseIsValidatedWithoutMutationOrBackup()
    {
        using RecoveryFixture fixture = new();
        byte[] databaseBefore = File.ReadAllBytes(fixture.DatabasePath);
        DateTime lastWriteBefore = File.GetLastWriteTimeUtc(fixture.DatabasePath);
        int backupsBefore = Directory.GetFiles(fixture.Database.BackupDirectory).Length;
        FileAttributes originalAttributes = File.GetAttributes(fixture.DatabasePath);
        File.SetAttributes(fixture.DatabasePath, originalAttributes | FileAttributes.ReadOnly);

        WorkspaceRecoveryCheckResult result;
        try
        {
            result = await fixture.Recovery.CheckAsync();
        }
        finally
        {
            File.SetAttributes(fixture.DatabasePath, originalAttributes);
        }

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsTrue(result.IsIntact);
        CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(fixture.DatabasePath));
        Assert.AreEqual(lastWriteBefore, File.GetLastWriteTimeUtc(fixture.DatabasePath));
        Assert.AreEqual(
            backupsBefore,
            Directory.GetFiles(fixture.Database.BackupDirectory).Length);
    }

    [TestMethod]
    public async Task CheckAsync_AllEightRegisteredArtifactKindsPassStrictFileValidation()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        foreach (ArtifactKind kind in Enum.GetValues<ArtifactKind>())
        {
            byte[] content = Encoding.UTF8.GetBytes($"registered-{kind}");
            _ = await fixture.PromoteArtifactAsync(
                projectId,
                kind,
                $"nested/{kind}.bin",
                content);
        }

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsTrue(result.IsIntact);
        Assert.AreEqual(0, result.Issues.Count);
        Assert.AreEqual(0, result.StagingCandidates.Count);
        Assert.AreEqual(8L, fixture.Database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM artifacts;"));
    }

    [TestMethod]
    public async Task CheckAsync_ReportsMissingSizeAndDigestArtifactFailuresWithoutMutation()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] missingContent = Encoding.UTF8.GetBytes("missing artifact");
        byte[] sizeContent = Encoding.UTF8.GetBytes("size artifact");
        byte[] digestContent = Encoding.UTF8.GetBytes("digest-one");
        StoredArtifact missing = await fixture.PromoteArtifactAsync(
            projectId,
            ArtifactKind.Original,
            "missing.bin",
            missingContent);
        StoredArtifact size = await fixture.PromoteArtifactAsync(
            projectId,
            ArtifactKind.Proxy,
            "size.bin",
            sizeContent);
        StoredArtifact digest = await fixture.PromoteArtifactAsync(
            projectId,
            ArtifactKind.Analysis,
            "digest.bin",
            digestContent);
        string missingPath = fixture.GetStoragePath(missing.StorageReference);
        string sizePath = fixture.GetStoragePath(size.StorageReference);
        string digestPath = fixture.GetStoragePath(digest.StorageReference);
        File.Delete(missingPath);
        byte[] changedSize = [.. sizeContent, 42];
        File.WriteAllBytes(sizePath, changedSize);
        byte[] changedDigest = Encoding.UTF8.GetBytes("digest-two");
        Assert.AreEqual(digestContent.Length, changedDigest.Length);
        File.WriteAllBytes(digestPath, changedDigest);

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsFalse(result.IsIntact);
        AssertIssue(result, WorkspaceIntegrityIssueKind.ArtifactFileMissing, missing.Id);
        AssertIssue(result, WorkspaceIntegrityIssueKind.ArtifactSizeMismatch, size.Id);
        AssertIssue(result, WorkspaceIntegrityIssueKind.ArtifactDigestMismatch, digest.Id);
        Assert.IsFalse(File.Exists(missingPath));
        CollectionAssert.AreEqual(changedSize, File.ReadAllBytes(sizePath));
        CollectionAssert.AreEqual(changedDigest, File.ReadAllBytes(digestPath));
        Assert.AreEqual(3L, fixture.Database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM artifacts;"));
    }

    [TestMethod]
    public async Task CheckAsync_RegisteredArtifactReparsePointIsUnsafeAndUntouchedWhenSupported()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("registered target");
        StoredArtifact artifact = await fixture.PromoteArtifactAsync(
            projectId,
            ArtifactKind.Evidence,
            "linked.bin",
            content);
        string artifactPath = fixture.GetStoragePath(artifact.StorageReference);
        string targetPath = Path.Combine(fixture.WorkspacePath, "link-target.bin");
        File.WriteAllBytes(targetPath, content);
        File.Delete(artifactPath);
        if (!TryCreateFileSymbolicLink(artifactPath, targetPath))
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
        }

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        AssertIssue(result, WorkspaceIntegrityIssueKind.ArtifactPathUnsafe, artifact.Id);
        Assert.IsTrue(File.Exists(artifactPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(targetPath));
        Assert.AreEqual(1L, fixture.Database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM artifacts;"));
    }

    [TestMethod]
    public async Task CheckAsync_UnregisteredManagedFileIsIssueNotCandidateAndIsNeverDeleted()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string path = fixture.CreateManagedFile(
            projectId,
            "renders",
            "nested/unregistered.bin",
            Encoding.UTF8.GetBytes("unregistered output"));
        string canonicalRelativePath = string.Concat(
            "projects/",
            projectId,
            "/renders/nested/unregistered.bin");

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        WorkspaceIntegrityIssue issue = result.Issues.Single(static issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.UnregisteredArtifactFile);
        Assert.AreEqual(projectId.ToString(), issue.EntityId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(canonicalRelativePath)),
            issue.PathFingerprint);
        Assert.IsNull(issue.ContentDigest);
        Assert.AreEqual(0, result.StagingCandidates.Count);
        Assert.IsTrue(File.Exists(path));
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("unregistered output"),
            File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task CheckAsync_OnlyNoJobSucceededAndCancelledProjectsProduceCandidates()
    {
        using RecoveryFixture fixture = new();
        var expectedCandidates = new List<(EntityId<ProjectIdTag> ProjectId, string Path)>();

        EntityId<ProjectIdTag> noJob = fixture.CreateProject();
        expectedCandidates.Add((noJob, "no-job.bin"));
        fixture.CreateStagingFile(noJob, "no-job.bin", [1]);

        EntityId<ProjectIdTag> succeeded = fixture.CreateProject();
        fixture.CreateJob(succeeded, JobState.Succeeded);
        expectedCandidates.Add((succeeded, "succeeded.bin"));
        fixture.CreateStagingFile(succeeded, "succeeded.bin", [2]);

        EntityId<ProjectIdTag> cancelled = fixture.CreateProject();
        fixture.CreateJob(cancelled, JobState.Cancelled);
        expectedCandidates.Add((cancelled, "cancelled.bin"));
        fixture.CreateStagingFile(cancelled, "cancelled.bin", [3]);

        foreach (JobState protectedState in new[]
        {
            JobState.Queued,
            JobState.Running,
            JobState.Failed,
            JobState.Interrupted,
        })
        {
            EntityId<ProjectIdTag> protectedProject = fixture.CreateProject();
            fixture.CreateJob(
                protectedProject,
                protectedState,
                createdAt: CheckedAt.AddYears(-20),
                appendHeartbeat: false);
            fixture.CreateStagingFile(
                protectedProject,
                $"{protectedState}.bin",
                [(byte)(10 + (int)protectedState)]);
        }

        EntityId<ProjectIdTag> mixedProject = fixture.CreateProject();
        fixture.CreateJob(mixedProject, JobState.Succeeded);
        fixture.CreateJob(mixedProject, JobState.Failed);
        fixture.CreateStagingFile(mixedProject, "mixed.bin", [99]);

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsTrue(result.IsIntact, "Candidates alone must not mark the workspace corrupt.");
        Assert.AreEqual(0, result.Issues.Count);
        CollectionAssert.AreEquivalent(
            expectedCandidates
                .Select(static item => $"{item.ProjectId}:{item.Path}")
                .ToArray(),
            result.StagingCandidates
                .Select(static item => $"{item.ProjectId}:{item.RelativePath}")
                .ToArray());
        Assert.AreEqual(3, result.StagingCandidates.Count);
    }

    [TestMethod]
    public async Task CheckAndCleanup_ZeroByteStagingFileUsesEvidenceWithoutSizeThreshold()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string path = fixture.CreateStagingFile(projectId, "empty.bin", []);

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();
        StagingRecoveryCandidate candidate = result.StagingCandidates.Single();

        Assert.AreEqual(0L, candidate.ByteSize);
        Assert.AreEqual(ContentDigest.Compute([]), candidate.ContentDigest);
        StagingCleanupEvidence evidence = await fixture.Recovery.CleanupAsync(candidate);
        Assert.AreEqual(0L, evidence.ByteSize);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task CleanupAsync_ExactCandidateDeletesOnlySelectedFileAndReturnsPathFreeEvidence()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] selectedContent = Encoding.UTF8.GetBytes("selected candidate");
        byte[] siblingContent = Encoding.UTF8.GetBytes("sibling candidate");
        string selectedPath = fixture.CreateStagingFile(
            projectId,
            "nested/selected.bin",
            selectedContent);
        string siblingPath = fixture.CreateStagingFile(
            projectId,
            "nested/sibling.bin",
            siblingContent);
        WorkspaceRecoveryCheckResult check = await fixture.Recovery.CheckAsync();
        StagingRecoveryCandidate selected = check.StagingCandidates.Single(static item =>
            item.RelativePath == "nested/selected.bin");
        long auditCount = fixture.Database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM audit_events;");

        StagingCleanupEvidence evidence =
            await fixture.Recovery.CleanupAsync(selected);

        Assert.IsFalse(File.Exists(selectedPath));
        Assert.IsTrue(File.Exists(siblingPath));
        CollectionAssert.AreEqual(siblingContent, File.ReadAllBytes(siblingPath));
        Assert.AreEqual(projectId, evidence.ProjectId);
        Assert.AreEqual(selected.CandidateDigest, evidence.CandidateDigest);
        Assert.AreEqual(selected.ContentDigest, evidence.ContentDigest);
        Assert.AreEqual(selected.ByteSize, evidence.ByteSize);
        Assert.AreEqual(CheckedAt, evidence.DeletedAt);
        Assert.IsFalse(typeof(StagingCleanupEvidence)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property => property.Name.Contains("Path", StringComparison.Ordinal)));
        Assert.AreEqual(
            auditCount,
            fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM audit_events;"));
    }

    [TestMethod]
    public async Task CleanupAsync_SameSizeModifiedBytesWithRestoredTimestampFailClosed()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] original = Encoding.UTF8.GetBytes("original-bytes");
        byte[] changed = Encoding.UTF8.GetBytes("modified-bytes");
        Assert.AreEqual(original.Length, changed.Length);
        string path = fixture.CreateStagingFile(projectId, "modified.bin", original);
        StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
            .StagingCandidates.Single();
        File.WriteAllBytes(path, changed);
        File.SetLastWriteTimeUtc(path, candidate.LastWriteTimeUtc.UtcDateTime);
        Assert.AreEqual(
            candidate.LastWriteTimeUtc,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path)));

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => fixture.Recovery.CleanupAsync(candidate));

        AssertSafeCleanupException(exception, fixture, candidate);
        Assert.IsTrue(File.Exists(path));
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task CleanupAsync_ReplacedAndMissingCandidatesFailClosed()
    {
        foreach (string mutation in new[] { "replaced", "missing" })
        {
            using RecoveryFixture fixture = new();
            EntityId<ProjectIdTag> projectId = fixture.CreateProject();
            byte[] original = Encoding.UTF8.GetBytes("candidate-original");
            string path = fixture.CreateStagingFile(projectId, "stale.bin", original);
            StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
                .StagingCandidates.Single();
            File.Delete(path);
            if (mutation == "replaced")
            {
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("candidate-replaced"));
            }

            InvalidOperationException exception =
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => fixture.Recovery.CleanupAsync(candidate));

            AssertSafeCleanupException(exception, fixture, candidate);
            Assert.AreEqual(mutation == "replaced", File.Exists(path));
        }
    }

    [TestMethod]
    public async Task CleanupAsync_ReparseReplacementFailsClosedWhenSupported()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] original = Encoding.UTF8.GetBytes("candidate-link");
        string path = fixture.CreateStagingFile(projectId, "link.bin", original);
        StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
            .StagingCandidates.Single();
        string targetPath = Path.Combine(fixture.WorkspacePath, "replacement-target.bin");
        File.WriteAllBytes(targetPath, original);
        File.Delete(path);
        if (!TryCreateFileSymbolicLink(path, targetPath))
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
        }

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => fixture.Recovery.CleanupAsync(candidate));

        AssertSafeCleanupException(exception, fixture, candidate);
        Assert.IsTrue(File.Exists(path));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(targetPath));
    }

    [TestMethod]
    public async Task CleanupAsync_QueuedJobCreatedAfterCheckProtectsEntireProject()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string path = fixture.CreateStagingFile(projectId, "before-job.bin", [1, 2, 3]);
        StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
            .StagingCandidates.Single();
        fixture.CreateJob(projectId, JobState.Queued);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => fixture.Recovery.CleanupAsync(candidate));

        AssertSafeCleanupException(exception, fixture, candidate);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task CleanupAsync_VerificationHandlePreventsRenameDuringHash()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test observes Windows file-sharing semantics.");
        }

        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string path = fixture.CreateStagingFile(projectId, "locked.bin", [1]);
        SetSparseLength(path, 512L * 1024 * 1024);
        StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
            .StagingCandidates.Single();
        string movedPath = string.Concat(path, ".moved");

        Task<StagingCleanupEvidence> operation =
            fixture.Recovery.CleanupAsync(candidate);
        await WaitForReadLockAsync(path, operation);
        Assert.IsFalse(operation.IsCompleted);
        try
        {
            File.Move(path, movedPath);
            Assert.Fail("The candidate pathname was renamed during verification.");
        }
        catch (IOException)
        {
        }

        StagingCleanupEvidence evidence = await operation;

        Assert.AreEqual(candidate.CandidateDigest, evidence.CandidateDigest);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(movedPath));
    }

    [TestMethod]
    public void StagingCandidateConstructorRejectsTraversalAndAbsolutePaths()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        ContentDigest digest = ContentDigest.Compute([1]);
        foreach (string path in new[]
        {
            "../sibling.bin",
            "nested/../sibling.bin",
            "/absolute.bin",
            @"nested\sibling.bin",
            "C:/absolute.bin",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                new StagingRecoveryCandidate(
                    projectId,
                    path,
                    1,
                    digest,
                    CheckedAt,
                    digest,
                    CheckedAt));
            Assert.AreEqual("relativePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, path);
        }
    }

    [TestMethod]
    public void WorkspacePathSafety_PortablePathsAndContainmentAreStrict()
    {
        using TemporaryWorkspace temporary = new();
        string root = temporary.WorkspacePath;
        string child = Path.Combine(root, "nested", "file.bin");
        string sibling = string.Concat(root, "-sibling", Path.DirectorySeparatorChar, "file.bin");

        Assert.IsTrue(WorkspacePathSafety.IsValidPortableRelativePath("nested/file.bin"));
        foreach (string path in new[]
        {
            string.Empty,
            ".",
            "..",
            "nested//file.bin",
            "nested/../file.bin",
            @"nested\file.bin",
            "C:/file.bin",
            "nested/CON.txt",
            "nested/file.bin:stream",
            "nested/file.bin ",
        })
        {
            Assert.IsFalse(
                WorkspacePathSafety.IsValidPortableRelativePath(path),
                path);
        }

        Assert.IsTrue(WorkspacePathSafety.IsStrictDescendant(root, child));
        Assert.IsFalse(WorkspacePathSafety.IsStrictDescendant(root, root));
        Assert.IsFalse(WorkspacePathSafety.IsStrictDescendant(root, sibling));
        Assert.IsTrue(WorkspacePathSafety.TryResolvePortableRelativePath(
            root,
            "nested/file.bin",
            out string resolved));
        Assert.AreEqual(Path.GetFullPath(child), resolved);
        Assert.IsFalse(WorkspacePathSafety.TryResolvePortableRelativePath(
            root,
            "../escaped.bin",
            out _));
    }

    [TestMethod]
    public async Task CheckAsync_CorruptDatabaseFailsClosedWithoutCandidateOrRepair()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        fixture.CreateStagingFile(projectId, "must-remain.bin", [1, 2, 3]);
        byte[] corruption = Enumerable.Repeat((byte)0xa5, 512).ToArray();
        File.WriteAllBytes(fixture.DatabasePath, corruption);

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        AssertDatabaseInvalid(result);
        CollectionAssert.AreEqual(corruption, File.ReadAllBytes(fixture.DatabasePath));
        Assert.IsTrue(File.Exists(fixture.GetStagingPath(projectId, "must-remain.bin")));
    }

    [TestMethod]
    public async Task CheckAsync_UnmigratedDatabaseFailsClosedWithoutCreatingSchemaOrBackup()
    {
        using TemporaryWorkspace temporary = new();
        string databasePath = Path.Combine(temporary.RootPath, "unmigrated.db");
        File.WriteAllBytes(databasePath, []);
        string stagingPath = Path.Combine(
            temporary.WorkspacePath,
            "projects",
            "11111111-1111-4111-8111-111111111111",
            "staging",
            "must-remain.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        File.WriteAllBytes(stagingPath, [1]);
        var recovery = new WorkspaceRecovery(
            databasePath,
            temporary.WorkspacePath,
            new FixedTimeProvider(CheckedAt));

        WorkspaceRecoveryCheckResult result = await recovery.CheckAsync();

        AssertDatabaseInvalid(result);
        Assert.AreEqual(0L, new FileInfo(databasePath).Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(temporary.RootPath, "backups")));
        Assert.IsTrue(File.Exists(stagingPath));
    }

    [TestMethod]
    public async Task CheckAsync_AlteredSchemaFailsClosedWithoutRepairOrBackup()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string stagingPath = fixture.CreateStagingFile(projectId, "must-remain.bin", [1]);
        int backupsBefore = Directory.GetFiles(fixture.Database.BackupDirectory).Length;
        fixture.Database.ExecuteNonQuery("DROP INDEX idx_artifacts_content_digest;");

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        AssertDatabaseInvalid(result);
        Assert.AreEqual(
            0L,
            fixture.Database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx_artifacts_content_digest';"));
        Assert.AreEqual(
            backupsBefore,
            Directory.GetFiles(fixture.Database.BackupDirectory).Length);
        Assert.IsTrue(File.Exists(stagingPath));
    }

    [TestMethod]
    public async Task CheckAsync_InvalidJobAggregateIsIssueAndProtectsProjectStaging()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        EntityId<JobIdTag> jobId = fixture.CreateJob(projectId, JobState.Queued);
        string stagingPath = fixture.CreateStagingFile(projectId, "protected.bin", [1]);
        fixture.Database.ExecuteNonQuery(
            """
                UPDATE jobs
                SET state = 'succeeded', current_attempt_number = 1
                WHERE id = $id;
                """,
            ("$id", jobId.ToString()));

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsFalse(result.IsIntact);
        WorkspaceIntegrityIssue issue = result.Issues.Single(static issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.JobAggregateInvalid);
        Assert.AreEqual(jobId.ToString(), issue.EntityId);
        Assert.AreEqual(0, result.StagingCandidates.Count);
        Assert.IsTrue(File.Exists(stagingPath));
    }

    [TestMethod]
    public async Task CheckAsync_InvalidArtifactRecordIsReportedAndDestinationIsNotDeleted()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        StoredArtifact artifact = await fixture.PromoteArtifactAsync(
            projectId,
            ArtifactKind.Render,
            "invalid-record.bin",
            Encoding.UTF8.GetBytes("registered bytes"));
        string path = fixture.GetStoragePath(artifact.StorageReference);
        fixture.Database.ExecuteNonQuery(
            "UPDATE artifacts SET storage_reference = $reference WHERE id = $id;",
            ("$reference", $"projects/{projectId}/analysis/wrong-kind.bin"),
            ("$id", artifact.Id.ToString()));

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        WorkspaceIntegrityIssue invalidRecord = result.Issues.Single(issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.ArtifactRecordInvalid &&
            issue.EntityId == artifact.Id.ToString());
        Assert.IsNull(invalidRecord.ContentDigest);
        Assert.IsNull(invalidRecord.PathFingerprint);
        Assert.IsTrue(result.Issues.Any(static issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.UnregisteredArtifactFile));
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(1L, fixture.Database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM artifacts;"));
    }

    [TestMethod]
    public async Task CheckAndCleanup_InvalidArtifactReferenceProtectsAllStaging()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> artifactProjectId = fixture.CreateProject();
        EntityId<ProjectIdTag> stagingProjectId = fixture.CreateProject();
        string stagingPath = fixture.CreateStagingFile(
            stagingProjectId,
            "referenced.bin",
            [1, 2, 3]);
        StoredArtifact artifact = await fixture.PromoteArtifactAsync(
            artifactProjectId,
            ArtifactKind.Render,
            "registered.bin",
            [4, 5, 6]);
        StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
            .StagingCandidates.Single();
        fixture.Database.ExecuteNonQuery(
            "UPDATE artifacts SET storage_reference = $reference WHERE id = $id;",
            ("$reference", $"projects/{stagingProjectId}/staging/referenced.bin"),
            ("$id", artifact.Id.ToString()));

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.ArtifactRecordInvalid &&
            issue.EntityId == artifact.Id.ToString()));
        Assert.AreEqual(0, result.StagingCandidates.Count);
        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => fixture.Recovery.CleanupAsync(candidate));
        AssertSafeCleanupException(exception, fixture, candidate);
        Assert.IsTrue(File.Exists(stagingPath));
    }

    [TestMethod]
    public async Task CheckAsync_InvalidProjectDirectoryLayoutIsUnsafeAndNeverCandidate()
    {
        using RecoveryFixture fixture = new();
        string invalidPath = Path.Combine(
            fixture.WorkspacePath,
            "projects",
            "NOT-A-PROJECT-ID",
            "staging",
            "orphan.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(invalidPath)!);
        File.WriteAllBytes(invalidPath, [1]);

        WorkspaceRecoveryCheckResult result = await fixture.Recovery.CheckAsync();

        Assert.IsTrue(result.DatabaseUsable);
        Assert.IsFalse(result.IsIntact);
        Assert.IsTrue(result.Issues.Any(static issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.StagingPathUnsafe));
        Assert.AreEqual(0, result.StagingCandidates.Count);
        Assert.IsTrue(File.Exists(invalidPath));
    }

    [TestMethod]
    public async Task PublicRecoveryTypesAreSealedImmutableAndCollectionsDeterministicallyReadOnly()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> higherProject = fixture.CreateProject(
            Guid.Parse("22222222-2222-4222-8222-222222222222"));
        EntityId<ProjectIdTag> lowerProject = fixture.CreateProject(
            Guid.Parse("11111111-1111-4111-8111-111111111111"));
        fixture.CreateStagingFile(higherProject, "z.bin", [3]);
        fixture.CreateStagingFile(lowerProject, "z.bin", [2]);
        fixture.CreateStagingFile(lowerProject, "a.bin", [1]);
        fixture.CreateManagedFile(
            higherProject,
            "renders",
            "z.bin",
            [4]);
        fixture.CreateManagedFile(
            lowerProject,
            "analysis",
            "a.bin",
            [5]);

        WorkspaceRecoveryCheckResult first = await fixture.Recovery.CheckAsync();
        WorkspaceRecoveryCheckResult second = await fixture.Recovery.CheckAsync();

        CollectionAssert.AreEqual(
            first.StagingCandidates.Select(CandidateKey).ToArray(),
            second.StagingCandidates.Select(CandidateKey).ToArray());
        CollectionAssert.AreEqual(
            first.Issues.Select(IssueKey).ToArray(),
            second.Issues.Select(IssueKey).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                $"{lowerProject}:a.bin",
                $"{lowerProject}:z.bin",
                $"{higherProject}:z.bin",
            },
            first.StagingCandidates
                .Select(static candidate =>
                    $"{candidate.ProjectId}:{candidate.RelativePath}")
                .ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<WorkspaceIntegrityIssue>)first.Issues).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StagingRecoveryCandidate>)first.StagingCandidates).Clear());

        foreach (Type type in new[]
        {
            typeof(WorkspaceIntegrityIssue),
            typeof(StagingRecoveryCandidate),
            typeof(WorkspaceRecoveryCheckResult),
            typeof(StagingCleanupEvidence),
            typeof(WorkspaceRecovery),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                "DatabaseInvalid",
                "JobAggregateInvalid",
                "ArtifactRecordInvalid",
                "ArtifactFileMissing",
                "ArtifactPathUnsafe",
                "ArtifactSizeMismatch",
                "ArtifactDigestMismatch",
                "ArtifactReadFailed",
                "UnregisteredArtifactFile",
                "StagingPathUnsafe",
            },
            Enum.GetNames<WorkspaceIntegrityIssueKind>());
        Assert.AreEqual(0, typeof(WorkspaceIntegrityIssue).GetConstructors().Length);
        Assert.AreEqual(0, typeof(StagingRecoveryCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(WorkspaceRecoveryCheckResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(StagingCleanupEvidence).GetConstructors().Length);
    }

    [TestMethod]
    public async Task CleanupAsync_MissingCandidateExceptionDoesNotDiscloseAnyPath()
    {
        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string path = fixture.CreateStagingFile(
            projectId,
            "private/subtitle-secret.ass",
            [1]);
        StagingRecoveryCandidate candidate = (await fixture.Recovery.CheckAsync())
            .StagingCandidates.Single();
        File.Delete(path);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => fixture.Recovery.CleanupAsync(candidate));

        AssertSafeCleanupException(exception, fixture, candidate);
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    public async Task CheckAsync_CancellationDuringArtifactHashLeavesFileUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test observes Windows file-sharing semantics.");
        }

        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        StoredArtifact artifact = await fixture.PromoteArtifactAsync(
            projectId,
            ArtifactKind.Analysis,
            "large.bin",
            [1]);
        string path = fixture.GetStoragePath(artifact.StorageReference);
        SetSparseLength(path, 512L * 1024 * 1024);
        fixture.Database.ExecuteNonQuery(
            "UPDATE artifacts SET byte_size = $size WHERE id = $id;",
            ("$size", new FileInfo(path).Length),
            ("$id", artifact.Id.ToString()));
        using CancellationTokenSource cancellation = new();

        Task<WorkspaceRecoveryCheckResult> operation =
            fixture.Recovery.CheckAsync(cancellation.Token);
        await CancelWhenReadLockedAsync(path, operation, cancellation);
        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(512L * 1024 * 1024, new FileInfo(path).Length);
    }

    [TestMethod]
    public async Task CheckAsync_CancellationDuringStagingHashLeavesFileUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test observes Windows file-sharing semantics.");
        }

        using RecoveryFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        string path = fixture.CreateStagingFile(projectId, "large.bin", [1]);
        SetSparseLength(path, 512L * 1024 * 1024);
        using CancellationTokenSource cancellation = new();

        Task<WorkspaceRecoveryCheckResult> operation =
            fixture.Recovery.CheckAsync(cancellation.Token);
        await CancelWhenReadLockedAsync(path, operation, cancellation);
        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(512L * 1024 * 1024, new FileInfo(path).Length);
    }

    [TestMethod]
    public void ConstructorRequiresAbsoluteLocalExistingNonReparseWorkspace()
    {
        using StoreTestDatabase database = new();
        string existingWorkspace = Path.Combine(database.RootPath, "workspace");
        Directory.CreateDirectory(existingWorkspace);
        foreach (string invalidDatabasePath in new[]
        {
            "relative.db",
            @"\\server\share\database.db",
            @"\\?\C:\database.db",
            database.DatabasePath + ":stream",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new WorkspaceRecovery(invalidDatabasePath, existingWorkspace));
            Assert.AreEqual("databasePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidDatabasePath);
        }

        string missingWorkspace = Path.Combine(database.RootPath, "missing-workspace");
        ArgumentException missing = Assert.ThrowsExactly<ArgumentException>(
            () => new WorkspaceRecovery(database.DatabasePath, missingWorkspace));
        Assert.AreEqual("workspacePath", missing.ParamName);
        AssertDoesNotContain(missing.Message, missingWorkspace);

        string fileWorkspace = Path.Combine(database.RootPath, "workspace-file");
        File.WriteAllBytes(fileWorkspace, [1]);
        ArgumentException file = Assert.ThrowsExactly<ArgumentException>(
            () => new WorkspaceRecovery(database.DatabasePath, fileWorkspace));
        Assert.AreEqual("workspacePath", file.ParamName);
        AssertDoesNotContain(file.Message, fileWorkspace);
    }

    private static void AssertDatabaseInvalid(WorkspaceRecoveryCheckResult result)
    {
        Assert.IsFalse(result.DatabaseUsable);
        Assert.IsFalse(result.IsIntact);
        Assert.AreEqual(0, result.StagingCandidates.Count);
        WorkspaceIntegrityIssue issue = result.Issues.Single();
        Assert.AreEqual(WorkspaceIntegrityIssueKind.DatabaseInvalid, issue.Kind);
        Assert.IsNull(issue.EntityId);
        Assert.IsNull(issue.ContentDigest);
        Assert.IsNull(issue.PathFingerprint);
    }

    private static void AssertIssue<TTag>(
        WorkspaceRecoveryCheckResult result,
        WorkspaceIntegrityIssueKind kind,
        EntityId<TTag> entityId)
    {
        WorkspaceIntegrityIssue issue = result.Issues.Single(issue =>
            issue.Kind == kind && issue.EntityId == entityId.ToString());
        Assert.IsNotNull(issue.PathFingerprint);
    }

    private static void AssertSafeCleanupException(
        InvalidOperationException exception,
        RecoveryFixture fixture,
        StagingRecoveryCandidate candidate)
    {
        Assert.AreEqual(
            "The staging cleanup could not be completed safely.",
            exception.Message);
        foreach (string forbidden in new[]
        {
            fixture.DatabasePath,
            fixture.WorkspacePath,
            candidate.RelativePath,
            Path.GetFileName(candidate.RelativePath),
        })
        {
            AssertDoesNotContain(exception.Message, forbidden);
        }
    }

    private static string CandidateKey(StagingRecoveryCandidate candidate) =>
        string.Join(
            '|',
            candidate.ProjectId,
            candidate.RelativePath,
            candidate.ByteSize,
            candidate.ContentDigest.Hex,
            candidate.LastWriteTimeUtc,
            candidate.CandidateDigest.Hex,
            candidate.CheckedAt);

    private static string IssueKey(WorkspaceIntegrityIssue issue) =>
        string.Join(
            '|',
            issue.Kind,
            issue.EntityId,
            issue.ContentDigest?.Hex,
            issue.PathFingerprint?.Hex);

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

    private static void SetSparseLength(string path, long length)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(length);
    }

    private static async Task CancelWhenReadLockedAsync(
        string path,
        Task operation,
        CancellationTokenSource cancellation)
    {
        await WaitForReadLockAsync(path, operation);
        cancellation.Cancel();
    }

    private static async Task WaitForReadLockAsync(string path, Task operation)
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

        Assert.Fail("The hashing read lock was not observed before the operation completed.");
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (forbiddenValue.Length == 0)
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecoveryFixture : IDisposable
    {
        internal RecoveryFixture()
        {
            Database = new StoreTestDatabase();
            WorkspacePath = Path.Combine(Database.RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            Recovery = new WorkspaceRecovery(
                Database.DatabasePath,
                WorkspacePath,
                new FixedTimeProvider(CheckedAt));
        }

        internal StoreTestDatabase Database { get; }

        internal string DatabasePath => Database.DatabasePath;

        internal string WorkspacePath { get; }

        internal WorkspaceRecovery Recovery { get; }

        internal EntityId<ProjectIdTag> CreateProject(Guid? id = null)
        {
            EntityId<ProjectIdTag> projectId = id is Guid value
                ? EntityId<ProjectIdTag>.From(value)
                : EntityId<ProjectIdTag>.New();
            var project = new Project(
                projectId,
                "Recovery test project",
                "Exercise fail-closed workspace recovery.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                project,
                "{\"recovery\":true}",
                CheckedAt.AddDays(-1));
            return projectId;
        }

        internal EntityId<JobIdTag> CreateJob(
            EntityId<ProjectIdTag> projectId,
            JobState state,
            DateTimeOffset? createdAt = null,
            bool appendHeartbeat = false)
        {
            DateTimeOffset queuedAt = createdAt ?? CheckedAt.AddHours(-1);
            var jobStore = new JobStore(DatabasePath);
            EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
            var job = new Job(
                jobId,
                projectId,
                "recovery-test",
                JobState.Queued,
                queuedAt,
                queuedAt,
                currentAttemptNumber: 0);
            jobStore.CreateJob(job);
            if (state == JobState.Queued)
            {
                return jobId;
            }

            if (state == JobState.Cancelled)
            {
                _ = jobStore.CancelQueued(jobId, 0, queuedAt.AddSeconds(1));
                return jobId;
            }

            DateTimeOffset startedAt = queuedAt.AddSeconds(1);
            EntityId<JobAttemptIdTag> attemptId = EntityId<JobAttemptIdTag>.New();
            var running = new JobAttempt(
                attemptId,
                jobId,
                sequenceNumber: 1,
                JobState.Running,
                queuedAt,
                startedAt,
                completedAt: null,
                errorCode: null);
            _ = jobStore.StartAttempt(jobId, 0, running, startedAt);
            if (appendHeartbeat)
            {
                jobStore.AppendHeartbeat(new JobHeartbeat(
                    EntityId<JobHeartbeatIdTag>.New(),
                    jobId,
                    attemptId,
                    sequenceNumber: 1,
                    startedAt));
            }

            if (state == JobState.Running)
            {
                return jobId;
            }

            DateTimeOffset completedAt = startedAt.AddSeconds(1);
            var completed = new JobAttempt(
                attemptId,
                jobId,
                sequenceNumber: 1,
                state,
                queuedAt,
                startedAt,
                completedAt,
                state switch
                {
                    JobState.Failed => "recovery.failed",
                    JobState.Interrupted => "recovery.interrupted",
                    _ => null,
                });
            _ = jobStore.CompleteAttempt(jobId, 1, completed, completedAt);
            return jobId;
        }

        internal async Task<StoredArtifact> PromoteArtifactAsync(
            EntityId<ProjectIdTag> projectId,
            ArtifactKind kind,
            string relativePath,
            byte[] content)
        {
            string stagingPath = CreateStagingFile(
                projectId,
                string.Concat("promotion/", Guid.NewGuid().ToString("N"), ".tmp"),
                content);
            var request = new ArtifactPromotionRequest(
                EntityId<ArtifactIdTag>.New(),
                projectId,
                kind,
                stagingPath,
                relativePath,
                ContentDigest.Compute(content),
                content.LongLength,
                CheckedAt.AddMinutes(-1));
            return await new ArtifactStore(DatabasePath, WorkspacePath)
                .PromoteAsync(request);
        }

        internal string CreateStagingFile(
            EntityId<ProjectIdTag> projectId,
            string relativePath,
            byte[] content)
        {
            string path = GetStagingPath(projectId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        internal string CreateManagedFile(
            EntityId<ProjectIdTag> projectId,
            string folder,
            string relativePath,
            byte[] content)
        {
            string path = Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                folder,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        internal string GetStagingPath(
            EntityId<ProjectIdTag> projectId,
            string relativePath) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                "staging",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal string GetStoragePath(string storageReference) =>
            Path.Combine(
                WorkspacePath,
                storageReference.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose() => Database.Dispose();
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        internal TemporaryWorkspace()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.RecoveryTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}