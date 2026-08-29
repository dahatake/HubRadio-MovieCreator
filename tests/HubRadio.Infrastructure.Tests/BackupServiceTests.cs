using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Audit;
using HubRadio.Infrastructure.Backup;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Recovery;
using HubRadio.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using static HubRadio.Infrastructure.Tests.BackupServiceTestSupport;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class BackupServiceTests
{
    private const string SchemaRelativePath = "schemas/backup-manifest.schema.json";

    [TestMethod]
    public async Task CreateBackupAsync_UsesFilteredSnapshotCanonicalManifestAndLeavesSourceUnchanged()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProjectWithBusinessRows();
        IReadOnlyList<StoredArtifact> sourceArtifacts = await fixture.CreateArtifactsAsync(
            projectId,
            Enum.GetValues<ArtifactKind>());
        IReadOnlyDictionary<string, ContentDigest> sourceTreeBefore =
            SnapshotFileTree(fixture.WorkspacePath);

        BackupResult result = await fixture.Service.CreateBackupAsync();

        Assert.AreEqual(
            string.Concat("backup-", result.BackupId.ToString()),
            result.BackupDirectoryName);
        Assert.AreEqual(7, result.FileCount);
        Assert.IsTrue(IsLeafName(result.BackupDirectoryName));
        string backupDirectory = fixture.GetBackupDirectory(result);
        Assert.IsTrue(Directory.Exists(backupDirectory));
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.BackupRootPath,
            ".backup-*.staging").Any());
        WorkspaceAcl.VerifyInheritedDirectory(
            backupDirectory,
            fixture.BackupRootPath,
            fixture.CurrentSid);

        string manifestPath = Path.Combine(backupDirectory, BackupManifest.FileName);
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        BackupManifest manifest = BackupManifest.Parse(manifestBytes);
        CollectionAssert.AreEqual(manifestBytes, manifest.Serialize());
        Assert.AreEqual(result.BackupId, manifest.BackupId);
        Assert.AreEqual(result.ManifestDigest, manifest.ManifestDigest);
        Assert.AreEqual(manifest.ComputeUnsignedDigest(), manifest.ManifestDigest);
        Assert.AreEqual(BackupManifest.CompletionValue, manifest.CompletionMarker);
        Assert.AreEqual(BackupServiceTestFixture.CreatedAt, manifest.CreatedAt);
        Assert.AreEqual(6, manifest.Artifacts.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                ArtifactKind.Original,
                ArtifactKind.Analysis,
                ArtifactKind.Composition,
                ArtifactKind.Render,
                ArtifactKind.Delivery,
                ArtifactKind.Evidence,
            },
            manifest.IncludedKinds.ToArray());
        CollectionAssert.AreEqual(
            new[] { ArtifactKind.Proxy, ArtifactKind.Preview },
            manifest.ExcludedKinds.ToArray());
        CollectionAssert.AreEquivalent(
            Enum.GetValues<ArtifactKind>(),
            manifest.IncludedKinds.Concat(manifest.ExcludedKinds).ToArray());
        Assert.AreEqual(
            Enum.GetValues<ArtifactKind>().Length,
            manifest.IncludedKinds.Concat(manifest.ExcludedKinds).Distinct().Count());
        Assert.IsTrue(manifest.Artifacts
            .Select(static artifact => artifact.Id.ToString())
            .SequenceEqual(
                manifest.Artifacts
                    .Select(static artifact => artifact.Id.ToString())
                    .OrderBy(static id => id, StringComparer.Ordinal)));
        Assert.IsTrue(manifest.Artifacts.All(static artifact =>
            BackupManifest.IsIncludedKind(artifact.Kind) &&
            artifact.BackupRelativePath ==
                string.Concat("payload/", artifact.SourceStorageReference)));
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<BackupManifestArtifact>)manifest.Artifacts).Clear());

        string snapshotDatabasePath = Path.Combine(
            backupDirectory,
            "payload",
            "hubradio.db");
        Assert.AreEqual(
            manifest.Database.ByteSize,
            new FileInfo(snapshotDatabasePath).Length);
        Assert.AreEqual(
            manifest.Database.Sha256,
            ContentDigest.Compute(File.ReadAllBytes(snapshotDatabasePath)));
        Assert.AreEqual(6L, fixture.CountArtifacts(snapshotDatabasePath));
        Assert.AreEqual(
            0L,
            ExecuteScalarInt64(
                snapshotDatabasePath,
                "SELECT COUNT(*) FROM artifacts WHERE kind IN ('Proxy', 'Preview');"));
        AssertBusinessRowsRetained(snapshotDatabasePath);
        DatabaseMigrator.ValidateCurrentDatabase(snapshotDatabasePath);
        new AuditLog(snapshotDatabasePath).Verify();

        foreach (StoredArtifact artifact in sourceArtifacts)
        {
            string backupArtifactPath = Path.Combine(
                backupDirectory,
                "payload",
                artifact.StorageReference.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Assert.AreEqual(
                BackupManifest.IsIncludedKind(artifact.Kind),
                File.Exists(backupArtifactPath),
                artifact.Kind.ToString());
            Assert.IsTrue(File.Exists(fixture.GetStoragePath(artifact.StorageReference)));
        }

        AssertFileTreesEqual(
            sourceTreeBefore,
            SnapshotFileTree(fixture.WorkspacePath));
        Assert.AreEqual(8L, fixture.CountArtifacts(fixture.DatabasePath));
        AssertBusinessRowsRetained(fixture.DatabasePath);
        new AuditLog(fixture.DatabasePath).Verify();
    }

    [TestMethod]
    public async Task RestoreBackupAsync_ValidatesThenCutsOverAndRetainsPreviousWorkspace()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProjectWithBusinessRows();
        IReadOnlyList<StoredArtifact> artifacts = await fixture.CreateArtifactsAsync(
            projectId,
            Enum.GetValues<ArtifactKind>());
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string currentOnlyPath = Path.Combine(fixture.WorkspacePath, "current-only.bin");
        File.WriteAllBytes(currentOnlyPath, [9, 8, 7]);
        IReadOnlyDictionary<string, ContentDigest> previousTree =
            SnapshotFileTree(fixture.WorkspacePath);

        RestoreResult restored = await fixture.Service.RestoreBackupAsync(backup.BackupId);

        Assert.AreEqual(backup.BackupId, restored.BackupId);
        Assert.AreEqual(7, restored.RestoredFileCount);
        Assert.IsTrue(IsLeafName(restored.PreviousWorkspaceDirectoryName));
        Assert.IsTrue(restored.PreviousWorkspaceDirectoryName.StartsWith(
            string.Concat(".hubradio.previous-", backup.BackupId, "-"),
            StringComparison.Ordinal));
        string previousPath = Path.Combine(
            Path.GetDirectoryName(fixture.WorkspacePath)!,
            restored.PreviousWorkspaceDirectoryName);
        Assert.IsTrue(Directory.Exists(previousPath));
        AssertFileTreesEqual(previousTree, SnapshotFileTree(previousPath));
        Assert.IsTrue(File.Exists(Path.Combine(previousPath, "current-only.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.WorkspacePath, "current-only.bin")));

        WorkspaceAclResult workspaceAcl = WorkspaceAcl.VerifyExisting(
            fixture.WorkspacePath,
            fixture.CurrentSid);
        Assert.IsTrue(workspaceAcl.Protected);
        Assert.AreEqual(3, workspaceAcl.ExplicitRuleCount);
        _ = WorkspaceAcl.VerifyExisting(previousPath, fixture.CurrentSid);
        DatabaseMigrator.ValidateCurrentDatabase(fixture.DatabasePath);
        new AuditLog(fixture.DatabasePath).Verify();
        WorkspaceRecoveryCheckResult recovery = await new WorkspaceRecovery(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            new BackupServiceTestFixture.FixedTimeProvider(
                BackupServiceTestFixture.CreatedAt)).CheckAsync();
        Assert.IsTrue(recovery.DatabaseUsable);
        Assert.IsTrue(recovery.IsIntact);
        Assert.AreEqual(0, recovery.Issues.Count);
        Assert.AreEqual(0, recovery.StagingCandidates.Count);
        Assert.AreEqual(6L, fixture.CountArtifacts(fixture.DatabasePath));
        AssertBusinessRowsRetained(fixture.DatabasePath);

        foreach (StoredArtifact artifact in artifacts)
        {
            Assert.AreEqual(
                BackupManifest.IsIncludedKind(artifact.Kind),
                File.Exists(fixture.GetStoragePath(artifact.StorageReference)),
                artifact.Kind.ToString());
        }

        Assert.IsTrue(Directory.Exists(fixture.GetBackupDirectory(backup)));
        string ordinaryStagingPath = Path.Combine(
            fixture.WorkspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "later-work.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(ordinaryStagingPath)!);
        File.WriteAllBytes(ordinaryStagingPath, [1, 2, 3]);
        Assert.AreEqual(
            0,
            (await fixture.Service.ListIncompleteCutoversAsync()).Count);
    }

    [TestMethod]
    public async Task CreateBackupAsync_WithRealConcurrentWriterProducesConsistentSnapshot()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        fixture.SetProjectPayloadSize(projectId, 4 * 1024 * 1024);
        using CancellationTokenSource stopWriter = new();
        var firstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int committedWrites = 0;

        Task writer = Task.Run(async () =>
        {
            using SqliteConnection connection = OpenConnection(fixture.DatabasePath);
            int sequence = 0;
            try
            {
                while (!stopWriter.IsCancellationRequested)
                {
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO role_assignments (
                            actor_sid, role, assigned_at, assigned_by_sid, reason
                        ) VALUES ($actor, 'Viewer', $at, $by, 'Concurrent writer');
                        """;
                    command.Parameters.AddWithValue(
                        "$actor",
                        string.Concat(
                            "concurrent-actor-",
                            sequence.ToString("D8", CultureInfo.InvariantCulture)));
                    command.Parameters.AddWithValue(
                        "$at",
                        BackupServiceTestFixture.CreatedAt.ToString(
                            "O",
                            CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$by", fixture.CurrentSid);
                    await command.ExecuteNonQueryAsync(stopWriter.Token);
                    Interlocked.Increment(ref committedWrites);
                    firstWrite.TrySetResult();
                    sequence++;
                    await Task.Delay(TimeSpan.FromMilliseconds(2), stopWriter.Token);
                }
            }
            catch (OperationCanceledException) when (stopWriter.IsCancellationRequested)
            {
            }
        });

        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BackupResult result;
        try
        {
            result = await Task.Run(() => fixture.Service.CreateBackupAsync())
                .WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            stopWriter.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
        }

        int sourceWriteCount = Volatile.Read(ref committedWrites);
        string snapshotDatabasePath = Path.Combine(
            fixture.GetBackupDirectory(result),
            "payload",
            "hubradio.db");
        long snapshotWriteCount = ExecuteScalarInt64(
            snapshotDatabasePath,
            "SELECT COUNT(*) FROM role_assignments WHERE actor_sid LIKE 'concurrent-actor-%';");
        long finalSourceWriteCount = ExecuteScalarInt64(
            fixture.DatabasePath,
            "SELECT COUNT(*) FROM role_assignments WHERE actor_sid LIKE 'concurrent-actor-%';");
        Assert.IsGreaterThan(0, sourceWriteCount);
        Assert.IsGreaterThanOrEqualTo(1L, snapshotWriteCount);
        Assert.IsLessThanOrEqualTo(finalSourceWriteCount, snapshotWriteCount);
        Assert.AreEqual(sourceWriteCount, finalSourceWriteCount);
        DatabaseMigrator.ValidateCurrentDatabase(snapshotDatabasePath);
        new AuditLog(snapshotDatabasePath).Verify();
    }

    [TestMethod]
    [DataRow("digest")]
    [DataRow("noncanonical")]
    [DataRow("duplicate")]
    [DataRow("extra")]
    public async Task RestoreBackupAsync_RejectsInvalidManifestBeforeCutover(
        string mutation)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string manifestPath = fixture.GetManifestPath(backup);
        byte[] bytes = File.ReadAllBytes(manifestPath);
        switch (mutation)
        {
            case "digest":
                RewriteManifest(
                    manifestPath,
                    root => root["manifestDigest"] = new string('f', 64),
                    resign: false);
                break;
            case "noncanonical":
                File.WriteAllBytes(
                    manifestPath,
                    [.. Encoding.UTF8.GetBytes(" "), .. bytes]);
                break;
            case "duplicate":
                File.WriteAllText(
                    manifestPath,
                    string.Concat(
                        "{\"version\":1,",
                        Encoding.UTF8.GetString(bytes).AsSpan(1)),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                break;
            case "extra":
                RewriteManifest(
                    manifestPath,
                    root => root["zzExtra"] = true,
                    resign: false);
                break;
            default:
                throw new AssertFailedException("Unknown manifest mutation.");
        }

        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.ManifestInvalid,
            reconciliationRequired: false,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        AssertNoPreviousWorkspace(fixture);
        AssertNoRestoreStaging(fixture);
    }

    [TestMethod]
    [DataRow("traversal")]
    [DataRow("id-mismatch")]
    [DataRow("staging-only")]
    public async Task RestoreBackupAsync_RejectsUnsafeIdentityOrIncompleteDirectory(
        string mutation)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        _ = await fixture.CreateArtifactsAsync(projectId, [ArtifactKind.Original]);
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        if (mutation == "traversal")
        {
            RewriteManifest(
                fixture.GetManifestPath(backup),
                root =>
                {
                    JsonArray artifacts = root["artifacts"]!.AsArray();
                    artifacts[0]!["sourceStorageReference"] = "../outside.bin";
                },
                resign: true);
        }
        else if (mutation == "id-mismatch")
        {
            RewriteManifest(
                fixture.GetManifestPath(backup),
                root => root["backupId"] = EntityId<BackupIdTag>.New().ToString(),
                resign: true);
        }
        else
        {
            string finalPath = fixture.GetBackupDirectory(backup);
            string stagingPath = Path.Combine(
                fixture.BackupRootPath,
                string.Concat(".backup-", backup.BackupId, ".staging"));
            Directory.Move(finalPath, stagingPath);
        }

        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            mutation == "staging-only"
                ? BackupServiceFailureReason.BackupNotFound
                : BackupServiceFailureReason.ManifestInvalid,
            reconciliationRequired: false,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        AssertNoPreviousWorkspace(fixture);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("hash")]
    [DataRow("size")]
    public async Task RestoreBackupAsync_RejectsIncompleteOrChangedPayloadBeforeCutover(
        string mutation)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        StoredArtifact artifact = (await fixture.CreateArtifactsAsync(
            projectId,
            [ArtifactKind.Original])).Single();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string backupDirectory = fixture.GetBackupDirectory(backup);
        string artifactPath = Path.Combine(
            backupDirectory,
            "payload",
            artifact.StorageReference.Replace('/', Path.DirectorySeparatorChar));
        switch (mutation)
        {
            case "missing":
                File.Delete(artifactPath);
                break;
            case "extra":
                File.WriteAllBytes(Path.Combine(backupDirectory, "unexpected.bin"), [1]);
                break;
            case "hash":
                byte[] changed = File.ReadAllBytes(artifactPath);
                changed[0] ^= 0xff;
                File.WriteAllBytes(artifactPath, changed);
                break;
            case "size":
                using (FileStream stream = new(
                    artifactPath,
                    FileMode.Append,
                    FileAccess.Write))
                {
                    stream.WriteByte(0xff);
                }

                break;
            default:
                throw new AssertFailedException("Unknown payload mutation.");
        }

        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.BackupContentInvalid,
            reconciliationRequired: false,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        AssertNoPreviousWorkspace(fixture);
        AssertNoRestoreStaging(fixture);
    }

    [TestMethod]
    [DataRow("bytes")]
    [DataRow("schema")]
    [DataRow("audit")]
    public async Task RestoreBackupAsync_RejectsCorruptDatabaseAfterHashVerification(
        string mutation)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProjectWithBusinessRows();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string databasePath = Path.Combine(
            fixture.GetBackupDirectory(backup),
            "payload",
            "hubradio.db");
        switch (mutation)
        {
            case "bytes":
                File.WriteAllBytes(
                    databasePath,
                    Enumerable.Repeat((byte)0xa5, 4096).ToArray());
                break;
            case "schema":
                ExecuteNonQuery(
                    databasePath,
                    "DROP INDEX idx_artifacts_content_digest;");
                break;
            case "audit":
                ExecuteNonQuery(
                    databasePath,
                    "UPDATE audit_events SET reason = 'Changed after backup';");
                break;
            default:
                throw new AssertFailedException("Unknown database mutation.");
        }

        ResignDatabaseRecord(fixture.GetManifestPath(backup), databasePath);
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.BackupContentInvalid,
            reconciliationRequired: false,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        AssertNoPreviousWorkspace(fixture);
        AssertNoRestoreStaging(fixture);
    }

    [TestMethod]
    [DataRow("database")]
    [DataRow("audit")]
    [DataRow("missing-artifact")]
    [DataRow("unregistered")]
    [DataRow("acl")]
    public async Task CreateBackupAsync_RejectsInvalidSourceWithoutFinalDirectory(
        string mutation)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProjectWithBusinessRows();
        StoredArtifact artifact = (await fixture.CreateArtifactsAsync(
            projectId,
            [ArtifactKind.Original])).Single();
        switch (mutation)
        {
            case "database":
                ExecuteNonQuery(
                    fixture.DatabasePath,
                    "DROP INDEX idx_artifacts_content_digest;");
                break;
            case "audit":
                ExecuteNonQuery(
                    fixture.DatabasePath,
                    "UPDATE audit_events SET reason = 'Changed after append';");
                break;
            case "missing-artifact":
                File.Delete(fixture.GetStoragePath(artifact.StorageReference));
                break;
            case "unregistered":
                string unregistered = Path.Combine(
                    fixture.WorkspacePath,
                    "projects",
                    projectId.ToString(),
                    "renders",
                    "unregistered.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(unregistered)!);
                File.WriteAllBytes(unregistered, [1, 2, 3]);
                break;
            case "acl":
                AddUnexpectedExplicitRule(fixture.BackupRootPath);
                break;
            default:
                throw new AssertFailedException("Unknown source mutation.");
        }

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.CreateBackupAsync());

        BackupServiceFailureReason expected = mutation switch
        {
            "acl" => BackupServiceFailureReason.AccessControlInvalid,
            "audit" => BackupServiceFailureReason.SourceAuditInvalid,
            _ => BackupServiceFailureReason.SourceWorkspaceInvalid,
        };
        AssertSafeException(
            exception,
            expected,
            reconciliationRequired: false,
            fixture);
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.BackupRootPath,
            "backup-*").Any());
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.BackupRootPath,
            ".backup-*.staging").Any());
    }

    [TestMethod]
    public async Task CreateBackupAsync_PreCancellationCreatesNoBackupDirectory()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Service.CreateBackupAsync(cancellation.Token));

        Assert.IsFalse(Directory.EnumerateDirectories(fixture.BackupRootPath).Any());
    }

    [TestMethod]
    public async Task CreateBackupAsync_CancellationAfterPayloadCopyLeavesOnlyHiddenStaging()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        _ = await fixture.CreateArtifactsAsync(projectId, [ArtifactKind.Original]);
        IReadOnlyDictionary<string, ContentDigest> sourceBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        using CancellationTokenSource cancellation = new();
        var timeProvider = new CancelOnCallTimeProvider(
            BackupServiceTestFixture.CreatedAt,
            cancellation,
            cancelOnCall: 2);
        var service = new BackupService(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            fixture.BackupRootPath,
            fixture.CurrentSid,
            timeProvider);

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CreateBackupAsync(cancellation.Token));

        Assert.AreEqual(2, timeProvider.CallCount);
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.BackupRootPath,
            "backup-*").Any());
        string stagingPath = Directory.EnumerateDirectories(
            fixture.BackupRootPath,
            ".backup-*.staging").Single();
        Assert.IsTrue(File.Exists(Path.Combine(stagingPath, "payload", "hubradio.db")));
        AssertFileTreesEqual(sourceBefore, SnapshotFileTree(fixture.WorkspacePath));
    }

    [TestMethod]
    public async Task CreateBackupAsync_CancellationAfterFinalRenameReturnsCompletedBackup()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        using CancellationTokenSource cancellation = new();
        void MoveThenCancel(string source, string destination)
        {
            Directory.Move(source, destination);
            cancellation.Cancel();
        }

        BackupService service = fixture.CreateService(MoveThenCancel);
        BackupResult result = await service.CreateBackupAsync(cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(Directory.Exists(fixture.GetBackupDirectory(result)));
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.BackupRootPath,
            ".backup-*.staging").Any());
        BackupManifest manifest = BackupManifest.Parse(
            File.ReadAllBytes(fixture.GetManifestPath(result)));
        Assert.AreEqual(result.BackupId, manifest.BackupId);
        Assert.AreEqual(result.ManifestDigest, manifest.ManifestDigest);
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 3)]
    public async Task RestoreBackupAsync_CutoverFailureKeepsOrRollsBackCurrentWorkspace(
        int failingMove,
        int expectedMoveCount)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string markerPath = Path.Combine(fixture.WorkspacePath, "current-marker.bin");
        File.WriteAllBytes(markerPath, [4, 3, 2, 1]);
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        int moveCount = 0;
        void MoveWithFailure(string source, string destination)
        {
            moveCount++;
            if (moveCount == failingMove)
            {
                throw new IOException("Injected cutover move failure.");
            }

            Directory.Move(source, destination);
        }

        BackupService service = fixture.CreateService(MoveWithFailure);
        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => service.RestoreBackupAsync(backup.BackupId));

        Assert.AreEqual(expectedMoveCount, moveCount);
        AssertSafeException(
            exception,
            BackupServiceFailureReason.CutoverFailed,
            reconciliationRequired: true,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        Assert.IsTrue(File.Exists(markerPath));
        _ = WorkspaceAcl.VerifyExisting(fixture.WorkspacePath, fixture.CurrentSid);
        AssertNoPreviousWorkspace(fixture);
        Assert.IsTrue(Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.restore-*").Any());
    }

    [TestMethod]
    public async Task RestoreBackupAsync_RollbackMoveFailureRequiresReconciliation()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string markerPath = Path.Combine(fixture.WorkspacePath, "current-marker.bin");
        File.WriteAllBytes(markerPath, [4, 3, 2, 1]);
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        int moveCount = 0;
        void MoveWithRestoreAndRollbackFailure(string source, string destination)
        {
            moveCount++;
            if (moveCount is 2 or 3)
            {
                throw new IOException("Injected restore or rollback move failure.");
            }

            Directory.Move(source, destination);
        }

        BackupService service = fixture.CreateService(
            MoveWithRestoreAndRollbackFailure);
        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => service.RestoreBackupAsync(backup.BackupId));

        Assert.AreEqual(3, moveCount);
        AssertSafeException(
            exception,
            BackupServiceFailureReason.RollbackFailed,
            reconciliationRequired: true,
            fixture);
        Assert.IsFalse(Directory.Exists(fixture.WorkspacePath));
        string previousPath = Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.previous-*").Single();
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(previousPath));
        Assert.IsTrue(File.Exists(Path.Combine(previousPath, "current-marker.bin")));
        string stagingPath = Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.restore-*").Single();
        Assert.IsTrue(File.Exists(Path.Combine(stagingPath, "hubradio.db")));

        var recoveryService = new BackupService(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            fixture.BackupRootPath,
            fixture.CurrentSid);
        IncompleteCutoverCandidate candidate =
            (await recoveryService.ListIncompleteCutoversAsync()).Single();
        Assert.AreEqual(backup.BackupId, candidate.BackupId);
        Assert.AreEqual(Path.GetFileName(previousPath),
            candidate.PreviousWorkspaceDirectoryName);
        Assert.AreEqual(Path.GetFileName(stagingPath), candidate.RestoreDirectoryName);
        Assert.IsFalse(candidate.RestoredWorkspaceIsActive);
        Assert.IsFalse(Path.IsPathFullyQualified(
            candidate.PreviousWorkspaceDirectoryName));
        Assert.IsFalse(Path.IsPathFullyQualified(candidate.RestoreDirectoryName));

        await recoveryService.RollbackIncompleteCutoverAsync(candidate);

        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        Assert.IsFalse(Directory.Exists(previousPath));
        Assert.IsTrue(Directory.Exists(stagingPath));
        Assert.AreEqual(0,
            (await recoveryService.ListIncompleteCutoversAsync()).Count);
        IncompleteRestoreCandidate restoreCandidate =
            (await recoveryService.ListIncompleteRestoresAsync()).Single();
        await recoveryService.DiscardIncompleteRestoreAsync(restoreCandidate);
        Assert.IsFalse(Directory.Exists(stagingPath));
    }

    [TestMethod]
    public async Task RestoreBackupAsync_PostCutoverIntegrityFailureRollsBackCurrent()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string markerPath = Path.Combine(fixture.WorkspacePath, "current-marker.bin");
        File.WriteAllBytes(markerPath, [4, 3, 2, 1]);
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        int moveCount = 0;
        void MoveAndCorruptRestoredWorkspace(string source, string destination)
        {
            moveCount++;
            Directory.Move(source, destination);
            if (moveCount == 2)
            {
                File.WriteAllBytes(
                    Path.Combine(destination, "hubradio.db"),
                    [0xde, 0xad, 0xbe, 0xef]);
            }
        }

        BackupService service = fixture.CreateService(
            MoveAndCorruptRestoredWorkspace);
        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => service.RestoreBackupAsync(backup.BackupId));

        Assert.AreEqual(4, moveCount);
        AssertSafeException(
            exception,
            BackupServiceFailureReason.CutoverFailed,
            reconciliationRequired: true,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        Assert.IsTrue(File.Exists(markerPath));
        AssertNoPreviousWorkspace(fixture);
        Assert.AreEqual(
            0,
            (await fixture.Service.ListIncompleteCutoversAsync()).Count);
        Assert.AreEqual(
            1,
            (await fixture.Service.ListIncompleteRestoresAsync()).Count);
    }

    [TestMethod]
    public async Task IncompleteCutover_ActiveInvalidWorkspaceCanRollbackToPrevious()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string markerPath = Path.Combine(fixture.WorkspacePath, "current-marker.bin");
        File.WriteAllBytes(markerPath, [4, 3, 2, 1]);
        IReadOnlyDictionary<string, ContentDigest> previousTree =
            SnapshotFileTree(fixture.WorkspacePath);

        RestoreResult restored =
            await fixture.Service.RestoreBackupAsync(backup.BackupId);
        string previousPath = Path.Combine(
            fixture.RootPath,
            restored.PreviousWorkspaceDirectoryName);
        File.WriteAllBytes(fixture.DatabasePath, [0xde, 0xad, 0xbe, 0xef]);

        IncompleteCutoverCandidate candidate =
            (await fixture.Service.ListIncompleteCutoversAsync()).Single();

        Assert.AreEqual(backup.BackupId, candidate.BackupId);
        Assert.AreEqual(
            restored.PreviousWorkspaceDirectoryName,
            candidate.PreviousWorkspaceDirectoryName);
        Assert.IsTrue(candidate.RestoredWorkspaceIsActive);
        string isolatedRestoredPath = Path.Combine(
            fixture.RootPath,
            candidate.RestoreDirectoryName);
        Assert.IsFalse(Directory.Exists(isolatedRestoredPath));

        await fixture.Service.RollbackIncompleteCutoverAsync(candidate);

        AssertFileTreesEqual(previousTree, SnapshotFileTree(fixture.WorkspacePath));
        Assert.IsTrue(File.Exists(markerPath));
        Assert.IsFalse(Directory.Exists(previousPath));
        Assert.IsTrue(Directory.Exists(isolatedRestoredPath));
        Assert.AreEqual(
            0,
            (await fixture.Service.ListIncompleteCutoversAsync()).Count);
        IncompleteRestoreCandidate isolated =
            (await fixture.Service.ListIncompleteRestoresAsync()).Single();
        Assert.AreEqual(candidate.RestoreDirectoryName, isolated.DirectoryName);
        await fixture.Service.DiscardIncompleteRestoreAsync(isolated);
        Assert.IsFalse(Directory.Exists(isolatedRestoredPath));
    }

    [TestMethod]
    public async Task RestoreBackupAsync_ValidationFailureAfterStagingRetainsCurrentAndRequiresReconciliation()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string markerPath = Path.Combine(fixture.WorkspacePath, "current-marker.bin");
        File.WriteAllBytes(markerPath, [4, 3, 2, 1]);
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        var service = new BackupService(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            fixture.BackupRootPath,
            fixture.CurrentSid,
            new ThrowingTimeProvider());

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.RestoreValidationFailed,
            reconciliationRequired: true,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        Assert.IsTrue(File.Exists(markerPath));
        AssertNoPreviousWorkspace(fixture);
        string stagingPath = Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.restore-*").Single();
        Assert.IsTrue(File.Exists(Path.Combine(stagingPath, "hubradio.db")));
        IReadOnlyList<IncompleteRestoreCandidate> candidates =
            await fixture.Service.ListIncompleteRestoresAsync();
        IncompleteRestoreCandidate candidate = candidates.Single();
        Assert.AreEqual(backup.BackupId, candidate.BackupId);
        Assert.AreEqual(Path.GetFileName(stagingPath), candidate.DirectoryName);
        Assert.IsFalse(Path.IsPathFullyQualified(candidate.DirectoryName));
    }

    [TestMethod]
    public async Task RestoreBackupAsync_RootAclFailureAfterStagingRequiresReconciliation()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        var service = new BackupService(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            fixture.BackupRootPath,
            fixture.CurrentSid,
            new ActionTimeProvider(
                BackupServiceTestFixture.CreatedAt,
                () => AddUnexpectedExplicitRule(fixture.BackupRootPath)));

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.AccessControlInvalid,
            reconciliationRequired: true,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        AssertNoPreviousWorkspace(fixture);
        string stagingPath = Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.restore-*").Single();
        Assert.IsTrue(File.Exists(Path.Combine(stagingPath, "hubradio.db")));
    }

    [TestMethod]
    public async Task RestoreBackupAsync_CancellationAfterStagingRetainsCurrentWithoutCutover()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        string markerPath = Path.Combine(fixture.WorkspacePath, "current-marker.bin");
        File.WriteAllBytes(markerPath, [4, 3, 2, 1]);
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);
        using CancellationTokenSource cancellation = new();
        var service = new BackupService(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            fixture.BackupRootPath,
            fixture.CurrentSid,
            new CancelOnCallTimeProvider(
                BackupServiceTestFixture.CreatedAt,
                cancellation,
                cancelOnCall: 1));

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RestoreBackupAsync(
                backup.BackupId,
                cancellation.Token));

        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        Assert.IsTrue(File.Exists(markerPath));
        AssertNoPreviousWorkspace(fixture);
        string stagingPath = Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.restore-*").Single();
        Assert.IsTrue(File.Exists(Path.Combine(stagingPath, "hubradio.db")));
    }

    [TestMethod]
    public async Task IncompleteRestoreCandidate_CanBeListedAndExplicitlyDiscarded()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<BackupIdTag> backupId = EntityId<BackupIdTag>.New();
        string directoryName = CreateIncompleteRestoreDirectoryName(backupId);
        string candidatePath = Path.Combine(fixture.RootPath, directoryName);
        _ = WorkspaceAcl.CreateNew(candidatePath, fixture.CurrentSid);
        string nestedPath = Path.Combine(candidatePath, "nested");
        Directory.CreateDirectory(nestedPath);
        File.WriteAllBytes(Path.Combine(nestedPath, "partial.bin"), [1, 2, 3]);

        IReadOnlyList<IncompleteRestoreCandidate> candidates =
            await fixture.Service.ListIncompleteRestoresAsync();

        IncompleteRestoreCandidate candidate = candidates.Single();
        Assert.AreEqual(backupId, candidate.BackupId);
        Assert.AreEqual(directoryName, candidate.DirectoryName);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<IncompleteRestoreCandidate>)candidates).Clear());

        await fixture.Service.DiscardIncompleteRestoreAsync(candidate);

        Assert.IsFalse(Directory.Exists(candidatePath));
        Assert.AreEqual(
            0,
            (await fixture.Service.ListIncompleteRestoresAsync()).Count);
    }

    [TestMethod]
    public async Task IncompleteRestoreCanBeDiscardedWhenBackupRootIsUnavailable()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<BackupIdTag> backupId = EntityId<BackupIdTag>.New();
        string directoryName = CreateIncompleteRestoreDirectoryName(backupId);
        string candidatePath = Path.Combine(fixture.RootPath, directoryName);
        _ = WorkspaceAcl.CreateNew(candidatePath, fixture.CurrentSid);
        File.WriteAllBytes(Path.Combine(candidatePath, "partial.bin"), [1, 2, 3]);
        Directory.Delete(fixture.BackupRootPath, recursive: true);

        IncompleteRestoreCandidate candidate =
            (await fixture.Service.ListIncompleteRestoresAsync()).Single();
        await fixture.Service.DiscardIncompleteRestoreAsync(candidate);

        Assert.IsFalse(Directory.Exists(candidatePath));
    }

    [TestMethod]
    [DataRow("noncanonical-name")]
    [DataRow("file")]
    [DataRow("acl")]
    public async Task ListIncompleteRestoresAsync_RejectsSpoofedOrUnsafeEntries(
        string mutation)
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<BackupIdTag> backupId = EntityId<BackupIdTag>.New();
        string directoryName = mutation == "noncanonical-name"
            ? string.Concat(".hubradio.restore-", backupId, "-NOT-CANONICAL")
            : CreateIncompleteRestoreDirectoryName(backupId);
        string candidatePath = Path.Combine(fixture.RootPath, directoryName);
        switch (mutation)
        {
            case "noncanonical-name":
                Directory.CreateDirectory(candidatePath);
                break;
            case "file":
                File.WriteAllBytes(candidatePath, [1]);
                break;
            case "acl":
                Directory.CreateDirectory(candidatePath);
                break;
            default:
                throw new AssertFailedException("Unknown candidate mutation.");
        }

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.ListIncompleteRestoresAsync());

        AssertSafeException(
            exception,
            mutation == "acl"
                ? BackupServiceFailureReason.AccessControlInvalid
                : BackupServiceFailureReason.RestoreValidationFailed,
            reconciliationRequired: true,
            fixture);
        Assert.IsTrue(WorkspacePathSafety.PathEntryExistsConservatively(candidatePath));
    }

    [TestMethod]
    public async Task DiscardIncompleteRestoreAsync_RejectsStaleCandidateWithoutDeletingReplacement()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        EntityId<BackupIdTag> backupId = EntityId<BackupIdTag>.New();
        string originalName = CreateIncompleteRestoreDirectoryName(backupId);
        string originalPath = Path.Combine(fixture.RootPath, originalName);
        _ = WorkspaceAcl.CreateNew(originalPath, fixture.CurrentSid);
        IncompleteRestoreCandidate candidate =
            (await fixture.Service.ListIncompleteRestoresAsync()).Single();
        string replacementName = CreateIncompleteRestoreDirectoryName(backupId);
        string replacementPath = Path.Combine(fixture.RootPath, replacementName);
        Directory.Move(originalPath, replacementPath);

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.DiscardIncompleteRestoreAsync(candidate));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.RestoreValidationFailed,
            reconciliationRequired: false,
            fixture);
        Assert.IsFalse(Directory.Exists(originalPath));
        Assert.IsTrue(Directory.Exists(replacementPath));
        Assert.AreEqual(
            replacementName,
            (await fixture.Service.ListIncompleteRestoresAsync())
                .Single()
                .DirectoryName);
    }

    [TestMethod]
    public async Task RestoreBackupAsync_RejectsTamperedInheritedBackupAcl()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        _ = fixture.CreateProject();
        BackupResult backup = await fixture.Service.CreateBackupAsync();
        AddUnexpectedExplicitRule(fixture.GetBackupDirectory(backup));
        IReadOnlyDictionary<string, ContentDigest> currentBefore =
            SnapshotFileTree(fixture.WorkspacePath);

        BackupServiceException exception =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(
                () => fixture.Service.RestoreBackupAsync(backup.BackupId));

        AssertSafeException(
            exception,
            BackupServiceFailureReason.AccessControlInvalid,
            reconciliationRequired: false,
            fixture);
        AssertFileTreesEqual(currentBefore, SnapshotFileTree(fixture.WorkspacePath));
        AssertNoPreviousWorkspace(fixture);
    }

    [TestMethod]
    public async Task ConstructorRequiresExactDisjointPathsAndOperationsVerifyRoots()
    {
        RequireWindowsFixedTempDrive();
        using BackupServiceTestFixture fixture = new();
        string wrongDatabasePath = Path.Combine(fixture.WorkspacePath, "other.db");
        ArgumentException wrongDatabase = Assert.ThrowsExactly<ArgumentException>(() =>
            new BackupService(
                wrongDatabasePath,
                fixture.WorkspacePath,
                fixture.BackupRootPath,
                fixture.CurrentSid));
        Assert.AreEqual("databasePath", wrongDatabase.ParamName);

        string nestedRoot = Path.Combine(fixture.WorkspacePath, "nested-backups");
        _ = WorkspaceAcl.CreateNew(nestedRoot, fixture.CurrentSid);
        ArgumentException nested = Assert.ThrowsExactly<ArgumentException>(() =>
            new BackupService(
                fixture.DatabasePath,
                fixture.WorkspacePath,
                nestedRoot,
                fixture.CurrentSid));
        Assert.AreEqual("backupRootPath", nested.ParamName);

        string missingRoot = Path.Combine(fixture.RootPath, "missing-backups");
        var missingRootService = new BackupService(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            missingRoot,
            fixture.CurrentSid);
        BackupServiceException missing =
            await Assert.ThrowsExactlyAsync<BackupServiceException>(() =>
                missingRootService.CreateBackupAsync());
        AssertSafeException(
            missing,
            BackupServiceFailureReason.AccessControlInvalid,
            reconciliationRequired: false,
            fixture,
            missingRoot);
    }

    [TestMethod]
    public void PublicSurfaceIsMinimalImmutableAndPathSidErrorPrivate()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "AccessControlInvalid",
                "SourceWorkspaceInvalid",
                "SourceAuditInvalid",
                "BackupCreationFailed",
                "ArtifactValidationFailed",
                "ManifestInvalid",
                "BackupNotFound",
                "BackupContentInvalid",
                "RestoreValidationFailed",
                "CutoverFailed",
                "RollbackFailed",
            },
            Enum.GetNames<BackupServiceFailureReason>());

        Assert.IsTrue(typeof(BackupService).IsPublic);
        Assert.IsTrue(typeof(BackupService).IsSealed);
        MethodInfo[] methods = typeof(BackupService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "CreateBackupAsync",
                "RestoreBackupAsync",
                "ListIncompleteRestoresAsync",
                "DiscardIncompleteRestoreAsync",
                "ListIncompleteCutoversAsync",
                "RollbackIncompleteCutoverAsync",
            },
            methods.Select(static method => method.Name).ToArray());
        Assert.IsFalse(methods.Any(static method =>
            method.Name.Contains("Delete", StringComparison.Ordinal) ||
            method.Name.Contains("Retention", StringComparison.Ordinal) ||
            method.Name.Contains("Schedule", StringComparison.Ordinal) ||
            method.Name.Contains("Cloud", StringComparison.Ordinal) ||
            method.Name.Contains("Encrypt", StringComparison.Ordinal)));

        AssertImmutablePublicType(
            typeof(BackupResult),
            "BackupId",
            "BackupDirectoryName",
            "ManifestDigest",
            "FileCount");
        AssertImmutablePublicType(
            typeof(RestoreResult),
            "BackupId",
            "PreviousWorkspaceDirectoryName",
            "RestoredFileCount");
        AssertImmutablePublicType(
            typeof(IncompleteRestoreCandidate),
            "BackupId",
            "DirectoryName");
        AssertImmutablePublicType(
            typeof(IncompleteCutoverCandidate),
            "BackupId",
            "PreviousWorkspaceDirectoryName",
            "RestoreDirectoryName",
            "RestoredWorkspaceIsActive");
        AssertImmutablePublicType(
            typeof(BackupServiceException),
            "Reason",
            "ReconciliationRequired");
        Assert.AreEqual(0, typeof(BackupResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(RestoreResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(IncompleteRestoreCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(IncompleteCutoverCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(BackupServiceException).GetConstructors().Length);

        const string privatePath = @"C:\private\customer-workspace";
        const string privateSid = "S-1-5-21-111-222-333-444";
        foreach (BackupServiceFailureReason reason in
            Enum.GetValues<BackupServiceFailureReason>())
        {
            var exception = new BackupServiceException(
                reason,
                reconciliationRequired: true);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsTrue(exception.ReconciliationRequired);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privatePath);
            AssertDoesNotContain(exception.ToString(), privateSid);
        }
    }

    [TestMethod]
    public void BackupManifestSchemaIsClosedDraft202012AndMatchesRuntimeContract()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath(SchemaRelativePath)));
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");
        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Assert.AreEqual("BackupManifest", root.GetProperty("title").GetString());
        Assert.AreEqual(
            JsonValueKind.False,
            root.GetProperty("additionalProperties").ValueKind);
        Assert.AreEqual(9, root.GetProperty("maxProperties").GetInt32());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "version",
                "backupId",
                "createdAt",
                "database",
                "artifacts",
                "includedKinds",
                "excludedKinds",
                "manifestDigest",
                "completionMarker",
            },
            GetStrings(root.GetProperty("required")));
        Assert.AreEqual(
            BackupManifest.CurrentVersion,
            root.GetProperty("properties")
                .GetProperty("version")
                .GetProperty("const")
                .GetInt32());
        Assert.AreEqual(
            BackupManifest.CompletionValue,
            root.GetProperty("properties")
                .GetProperty("completionMarker")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            BackupManifest.DatabaseRelativePath,
            definitions.GetProperty("databaseFile")
                .GetProperty("properties")
                .GetProperty("relativePath")
                .GetProperty("const")
                .GetString());

        foreach (string closedDefinition in new[] { "databaseFile", "artifactFile" })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(closedDefinition)
                    .GetProperty("additionalProperties")
                    .ValueKind);
        }

        string idPattern = definitions.GetProperty("entityId")
            .GetProperty("pattern")
            .GetString()!;
        string digestPattern = definitions.GetProperty("contentDigest")
            .GetProperty("pattern")
            .GetString()!;
        Assert.IsTrue(Regex.IsMatch(
            "00112233-4455-4677-8899-aabbccddeeff",
            idPattern));
        Assert.IsFalse(Regex.IsMatch(
            "00112233-4455-4677-8899-AABBCCDDEEFF",
            idPattern));
        Assert.IsTrue(Regex.IsMatch(new string('a', 64), digestPattern));
        Assert.IsFalse(Regex.IsMatch(new string('A', 64), digestPattern));

        CollectionAssert.AreEqual(
            new[]
            {
                "Original",
                "Analysis",
                "Composition",
                "Render",
                "Delivery",
                "Evidence",
            },
            definitions.GetProperty("artifactFile")
                .GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray());
        string comment = root.GetProperty("$comment").GetString()!;
        StringAssert.Contains(comment, "canonical JSON");
        StringAssert.Contains(comment, "duplicate properties");
        StringAssert.Contains(comment, "manifestDigest omitted");
    }

    private static void ResignDatabaseRecord(
        string manifestPath,
        string databasePath)
    {
        BackupManifest manifest = BackupManifest.Parse(File.ReadAllBytes(manifestPath));
        byte[] database = File.ReadAllBytes(databasePath);
        BackupManifest resigned = BackupManifest.Create(
            manifest.BackupId,
            manifest.CreatedAt,
            new BackupManifestDatabase(
                BackupManifest.DatabaseRelativePath,
                database.LongLength,
                ContentDigest.Compute(database)),
            manifest.Artifacts);
        File.WriteAllBytes(manifestPath, resigned.Serialize());
    }

    private static void RewriteManifest(
        string manifestPath,
        Action<JsonObject> mutation,
        bool resign)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllBytes(manifestPath))?.AsObject()
            ?? throw new AssertFailedException(
                "The manifest JSON root was not an object.");
        mutation(root);
        if (resign)
        {
            _ = root.Remove("manifestDigest");
            byte[] unsigned = CanonicalJson.CanonicalizeToUtf8(
                Encoding.UTF8.GetBytes(root.ToJsonString()));
            root["manifestDigest"] = ContentDigest.Compute(unsigned).Hex;
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(
            Encoding.UTF8.GetBytes(root.ToJsonString()));
        File.WriteAllBytes(manifestPath, canonical);
    }

    private static void AssertImmutablePublicType(
        Type type,
        params string[] properties)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        PropertyInfo[] actual = type.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            properties,
            actual.Select(static property => property.Name).ToArray());
        Assert.IsTrue(actual.All(static property => property.SetMethod is null));
    }

    private static string[] GetStrings(JsonElement array) =>
        array.EnumerateArray().Select(static item => item.GetString()!).ToArray();

    private static string CreateIncompleteRestoreDirectoryName(
        EntityId<BackupIdTag> backupId) =>
        string.Concat(
            ".hubradio.restore-",
            backupId.ToString(),
            "-",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private sealed class CancelOnCallTimeProvider(
        DateTimeOffset value,
        CancellationTokenSource cancellation,
        int cancelOnCall) : TimeProvider
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _callCount) == cancelOnCall)
            {
                cancellation.Cancel();
            }

            return value;
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new IOException("Injected restore validation failure.");
    }

    private sealed class ActionTimeProvider(
        DateTimeOffset value,
        Action action) : TimeProvider
    {
        private int _invoked;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0)
            {
                action();
            }

            return value;
        }
    }
}
