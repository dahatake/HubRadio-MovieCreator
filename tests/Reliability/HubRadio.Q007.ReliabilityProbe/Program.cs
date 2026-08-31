using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Audit;
using HubRadio.Infrastructure.Backup;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Recovery;
using HubRadio.Infrastructure.Security;

namespace HubRadio.Q007.ReliabilityProbe;

internal static class Program
{
    private const int DiskFullSourceByteSize = 8 * 1024 * 1024;
    private const int FileBufferSize = 64 * 1024;
    private const int LinuxNoSpaceLeftOnDeviceErrorCode = 28;
    private const long PartialSourceByteSize = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("A reliability probe mode is required.");
            }

            return args[0] switch
            {
                "disk-full-import" => await RunDiskFullImportAsync(
                    RequireArgument(args, 1),
                    RequireArgument(args, 2),
                    RequireArgument(args, 3),
                    RequireArgument(args, 4)),
                "partial-host" => await RunPartialHostAsync(
                    RequireArgument(args, 1),
                    RequireArgument(args, 2),
                    RequireArgument(args, 3)),
                "partial-recover" => await RunPartialRecoveryAsync(
                    RequireArgument(args, 1)),
                "backup-restore" when OperatingSystem.IsWindows() =>
                    await RunBackupRestoreAsync(RequireArgument(args, 1)),
                _ => throw new ArgumentException("The reliability probe mode is unknown."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"{exception.GetType().Name}: The reliability probe failed.");
            return 1;
        }
    }

    private static async Task<int> RunDiskFullImportAsync(
        string caseRoot,
        string workspacePath,
        string observedPath,
        string continuePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The disk-full import probe requires Linux.");
        }

        string normalizedCaseRoot = RequireExistingDirectory(caseRoot);
        string normalizedWorkspace = RequireExistingDirectory(workspacePath);
        string normalizedObservedPath = RequireAbsolutePath(observedPath);
        string normalizedContinuePath = RequireAbsolutePath(continuePath);
        EnsurePathAbsent(normalizedObservedPath);
        EnsurePathAbsent(normalizedContinuePath);

        string sourcePath = Path.Combine(normalizedCaseRoot, "disk-full-source.bin");
        EnsurePathAbsent(sourcePath);
        await CreateDeterministicFileAsync(sourcePath, DiskFullSourceByteSize)
            .ConfigureAwait(false);
        byte[] sourceDigestBefore = await ComputeDigestAsync(sourcePath)
            .ConfigureAwait(false);
        DateTime sourceLastWriteBefore = File.GetLastWriteTimeUtc(sourcePath);
        long sourceLengthBefore = new FileInfo(sourcePath).Length;

        byte[] existingContent = [0x51, 0x30, 0x30, 0x37];
        string existingPath = Path.Combine(normalizedWorkspace, "existing-approved.bin");
        File.WriteAllBytes(existingPath, existingContent);
        byte[] existingDigestBefore = await ComputeDigestAsync(existingPath)
            .ConfigureAwait(false);

        var registry = new AssetCandidateRegistry();
        EntityId<AssetIdTag> candidateId = registry.RegisterSelection(sourcePath)
            ?? throw new InvalidOperationException("The source candidate was not registered.");
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        var importer = new MediaImporter(registry, normalizedWorkspace);
        var observedErrorCodes = new ConcurrentQueue<int>();
        int firstDiskFullObserved = 0;
        int continuationObserved = 0;

        EventHandler<FirstChanceExceptionEventArgs> handler = (_, eventArgs) =>
        {
            if (eventArgs.Exception is not IOException exception)
            {
                return;
            }

            int nativeHResult = exception.HResult;
            int nativeCode = exception.HResult & 0xffff;
            if (nativeCode != LinuxNoSpaceLeftOnDeviceErrorCode)
            {
                return;
            }

            observedErrorCodes.Enqueue(nativeCode);
            if (Interlocked.CompareExchange(ref firstDiskFullObserved, 1, 0) != 0)
            {
                return;
            }

            WriteJsonAtomically(
                normalizedObservedPath,
                new
                {
                    schemaVersion = "1.0.0",
                    processId = Environment.ProcessId,
                    nativeHResult,
                    nativeErrorCode = nativeCode,
                });

            Stopwatch timeout = Stopwatch.StartNew();
            while (!File.Exists(normalizedContinuePath) &&
                timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(5);
            }

            if (File.Exists(normalizedContinuePath))
            {
                Interlocked.Exchange(ref continuationObserved, 1);
            }
        };

        MediaImportException? importFailure = null;
        Stopwatch elapsed = Stopwatch.StartNew();
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            _ = await importer.CopyToStagingAsync(
                candidateId,
                projectId,
                jobId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (MediaImportException exception)
        {
            importFailure = exception;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
            elapsed.Stop();
        }

        byte[] sourceDigestAfter = await ComputeDigestAsync(sourcePath)
            .ConfigureAwait(false);
        byte[] existingDigestAfter = await ComputeDigestAsync(existingPath)
            .ConfigureAwait(false);
        bool sourceDigestUnchanged = CryptographicOperations.FixedTimeEquals(
            sourceDigestBefore,
            sourceDigestAfter);
        bool sourceMetadataUnchanged =
            sourceLengthBefore == new FileInfo(sourcePath).Length &&
            sourceLastWriteBefore == File.GetLastWriteTimeUtc(sourcePath);
        bool existingDestinationUnchanged = CryptographicOperations.FixedTimeEquals(
            existingDigestBefore,
            existingDigestAfter);
        int partialFileCount = Directory
            .EnumerateFiles(normalizedWorkspace, "*.partial", SearchOption.AllDirectories)
            .Count();
        int readyFileCount = Directory
            .EnumerateFiles(normalizedWorkspace, "*.ready", SearchOption.AllDirectories)
            .Count();
        int[] nativeErrorCodes = observedErrorCodes
            .Distinct()
            .Order()
            .ToArray();
        bool passed = importFailure?.Reason == MediaImportFailureReason.CopyFailed &&
            !importFailure.RecoveryRequired &&
            Volatile.Read(ref firstDiskFullObserved) == 1 &&
            Volatile.Read(ref continuationObserved) == 1 &&
            nativeErrorCodes.Length == 1 &&
            nativeErrorCodes[0] == LinuxNoSpaceLeftOnDeviceErrorCode &&
            sourceDigestUnchanged &&
            sourceMetadataUnchanged &&
            existingDestinationUnchanged &&
            partialFileCount == 0 &&
            readyFileCount == 0;

        WriteResult(new
        {
            schemaVersion = "1.0.0",
            scenario = "disk-full-media-import",
            implementationAssembly = typeof(MediaImporter).Assembly.GetName().Name,
            operatingSystem = OperatingSystem.IsLinux() ? "linux" : "other",
            sourceByteSize = sourceLengthBefore,
            exceptionType = importFailure?.GetType().FullName,
            failureReason = importFailure?.Reason.ToString(),
            recoveryRequired = importFailure?.RecoveryRequired,
            observedNativeErrorCodes = nativeErrorCodes,
            sourceDigestUnchanged,
            sourceMetadataUnchanged,
            existingDestinationUnchanged,
            partialFileCount,
            readyFileCount,
            elapsedMilliseconds = Math.Round(elapsed.Elapsed.TotalMilliseconds, 3),
            passed,
        });
        return passed ? 0 : 1;
    }

    private static async Task<int> RunPartialHostAsync(
        string caseRoot,
        string readyPath,
        string goPath)
    {
        string normalizedCaseRoot = RequireExistingDirectory(caseRoot);
        string normalizedReadyPath = RequireAbsolutePath(readyPath);
        string normalizedGoPath = RequireAbsolutePath(goPath);
        EnsurePathAbsent(normalizedReadyPath);
        EnsurePathAbsent(normalizedGoPath);

        string workspacePath = Path.Combine(normalizedCaseRoot, "workspace");
        string databasePath = Path.Combine(workspacePath, "hubradio.db");
        string sourcePath = Path.Combine(normalizedCaseRoot, "partial-source.bin");
        Directory.CreateDirectory(workspacePath);
        _ = DatabaseMigrator.Migrate(
            databasePath,
            Path.Combine(normalizedCaseRoot, "schema-backups"));
        EntityId<ProjectIdTag> projectId = CreateProject(databasePath);
        StoredArtifact approvedArtifact = await PromoteArtifactAsync(
            databasePath,
            workspacePath,
            projectId,
            ArtifactKind.Render,
            "approved/baseline.bin",
            [0x51, 0x30, 0x30, 0x37]).ConfigureAwait(false);

        CreateSparseSource(sourcePath, PartialSourceByteSize);
        byte[] sourceDigest = await ComputeDigestAsync(sourcePath).ConfigureAwait(false);
        DateTime sourceLastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
        var registry = new AssetCandidateRegistry();
        EntityId<AssetIdTag> candidateId = registry.RegisterSelection(sourcePath)
            ?? throw new InvalidOperationException("The source candidate was not registered.");
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();
        var state = new PartialState(
            projectId.ToString(),
            jobId.ToString(),
            candidateId.ToString(),
            Convert.ToHexStringLower(sourceDigest),
            PartialSourceByteSize,
            sourceLastWriteUtc,
            approvedArtifact.Id.ToString(),
            approvedArtifact.Kind.ToString(),
            approvedArtifact.StorageReference,
            approvedArtifact.ContentDigest.Hex,
            approvedArtifact.ByteSize,
            approvedArtifact.CreatedAt);
        WriteJsonAtomically(
            Path.Combine(normalizedCaseRoot, "partial-state.json"),
            state);
        WriteJsonAtomically(
            normalizedReadyPath,
            new
            {
                schemaVersion = "1.0.0",
                processId = Environment.ProcessId,
            });
        await WaitForFileAsync(normalizedGoPath, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        var importer = new MediaImporter(registry, workspacePath);
        try
        {
            StagedMediaImport completed = await importer.CopyToStagingAsync(
                candidateId,
                projectId,
                jobId,
                CancellationToken.None).ConfigureAwait(false);
            WriteResult(new
            {
                schemaVersion = "1.0.0",
                scenario = "partial-host",
                unexpectedlyCompleted = true,
                completed.ByteSize,
                passed = false,
            });
        }
        catch (MediaImportException exception)
        {
            WriteResult(new
            {
                schemaVersion = "1.0.0",
                scenario = "partial-host",
                unexpectedlyCompleted = false,
                failureReason = exception.Reason.ToString(),
                passed = false,
            });
        }

        return 1;
    }

    private static async Task<int> RunPartialRecoveryAsync(string caseRoot)
    {
        string normalizedCaseRoot = RequireExistingDirectory(caseRoot);
        string workspacePath = Path.Combine(normalizedCaseRoot, "workspace");
        string databasePath = Path.Combine(workspacePath, "hubradio.db");
        string sourcePath = Path.Combine(normalizedCaseRoot, "partial-source.bin");
        PartialState state = ReadJson<PartialState>(
            Path.Combine(normalizedCaseRoot, "partial-state.json"));
        EntityId<ProjectIdTag> projectId = ParseEntityId<ProjectIdTag>(state.ProjectId);
        EntityId<JobIdTag> jobId = ParseEntityId<JobIdTag>(state.JobId);
        EntityId<AssetIdTag> candidateId = ParseEntityId<AssetIdTag>(state.CandidateId);
        EntityId<ArtifactIdTag> approvedArtifactId =
            ParseEntityId<ArtifactIdTag>(state.ApprovedArtifactId);
        ContentDigest approvedDigest = ParseDigest(state.ApprovedArtifactDigest);
        byte[] expectedSourceDigest = Convert.FromHexString(state.SourceDigest);
        string expectedRelativePath = string.Concat(
            "imports/",
            jobId.ToString(),
            "/",
            candidateId.ToString(),
            ".partial");

        byte[] sourceDigestAfterKill = await ComputeDigestAsync(sourcePath)
            .ConfigureAwait(false);
        bool sourceUnchanged = state.SourceByteSize == new FileInfo(sourcePath).Length &&
            state.SourceLastWriteUtc == File.GetLastWriteTimeUtc(sourcePath) &&
            CryptographicOperations.FixedTimeEquals(
                expectedSourceDigest,
                sourceDigestAfterKill);
        var artifactStore = new ArtifactStore(databasePath, workspacePath);
        StoredArtifact approvedBefore = await artifactStore.ValidateRegisteredAsync(
            approvedArtifactId).ConfigureAwait(false);
        bool approvedBeforeUnchanged = ArtifactMatchesPartialState(
                approvedBefore,
                state) &&
            approvedBefore.ContentDigest == approvedDigest;

        var recovery = new WorkspaceRecovery(databasePath, workspacePath);
        Stopwatch detection = Stopwatch.StartNew();
        WorkspaceRecoveryCheckResult detected = await recovery.CheckAsync()
            .ConfigureAwait(false);
        detection.Stop();
        StagingRecoveryCandidate[] matchingCandidates = detected.StagingCandidates
            .Where(candidate =>
                candidate.ProjectId == projectId &&
                string.Equals(
                    candidate.RelativePath,
                    expectedRelativePath,
                    StringComparison.Ordinal))
            .ToArray();
        bool exactCandidateDetected = matchingCandidates.Length == 1 &&
            detected.StagingCandidates.Count == 1;
        long recoveredPartialByteSize = exactCandidateDetected
            ? matchingCandidates[0].ByteSize
            : -1;

        StagingCleanupEvidence? cleanupEvidence = null;
        Stopwatch cleanup = Stopwatch.StartNew();
        if (exactCandidateDetected)
        {
            cleanupEvidence = await recovery.CleanupAsync(matchingCandidates[0])
                .ConfigureAwait(false);
        }
        cleanup.Stop();

        bool cleanupEvidenceProduced = cleanupEvidence is not null &&
            cleanupEvidence.ProjectId == matchingCandidates[0].ProjectId &&
            cleanupEvidence.CandidateDigest == matchingCandidates[0].CandidateDigest &&
            cleanupEvidence.ContentDigest == matchingCandidates[0].ContentDigest &&
            cleanupEvidence.ByteSize == matchingCandidates[0].ByteSize;

        WorkspaceRecoveryCheckResult afterCleanup = await recovery.CheckAsync()
            .ConfigureAwait(false);
        StoredArtifact approvedAfter = await artifactStore.ValidateRegisteredAsync(
            approvedArtifactId).ConfigureAwait(false);
        bool approvedAfterUnchanged = ArtifactMatchesPartialState(
                approvedAfter,
                state) &&
            ArtifactsMatch(approvedBefore, approvedAfter) &&
            approvedAfter.ContentDigest == approvedDigest;
        string partialPath = Path.Combine(
            workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "imports",
            jobId.ToString(),
            string.Concat(candidateId.ToString(), ".partial"));
        string readyPath = Path.ChangeExtension(partialPath, ".ready");
        bool passed = sourceUnchanged &&
            approvedBeforeUnchanged &&
            approvedAfterUnchanged &&
            detected.DatabaseUsable &&
            detected.IsIntact &&
            detected.Issues.Count == 0 &&
            exactCandidateDetected &&
            cleanupEvidenceProduced &&
            recoveredPartialByteSize > 0 &&
            recoveredPartialByteSize < state.SourceByteSize &&
            !File.Exists(partialPath) &&
            !File.Exists(readyPath) &&
            afterCleanup.DatabaseUsable &&
            afterCleanup.IsIntact &&
            afterCleanup.Issues.Count == 0 &&
            afterCleanup.StagingCandidates.Count == 0;

        WriteResult(new
        {
            schemaVersion = "1.0.0",
            scenario = "partial-staging-manual-recovery",
            implementationAssembly = typeof(WorkspaceRecovery).Assembly.GetName().Name,
            sourceByteSize = state.SourceByteSize,
            recoveredPartialByteSize,
            sourceUnchanged,
            approvedArtifactUnchanged =
                approvedBeforeUnchanged && approvedAfterUnchanged,
            candidateCount = detected.StagingCandidates.Count,
            exactCandidateDetected,
            cleanupEvidenceProduced,
            candidateCountAfterCleanup = afterCleanup.StagingCandidates.Count,
            detectionMilliseconds = Math.Round(detection.Elapsed.TotalMilliseconds, 3),
            cleanupMilliseconds = Math.Round(cleanup.Elapsed.TotalMilliseconds, 3),
            manualRecovery = "verified-candidate-cleanup",
            passed,
        });
        return passed ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunBackupRestoreAsync(string caseRoot)
    {
        string normalizedCaseRoot = RequireExistingDirectory(caseRoot);
        string workspacePath = Path.Combine(normalizedCaseRoot, "workspace");
        string backupRootPath = Path.Combine(normalizedCaseRoot, "backup-root");
        string databasePath = Path.Combine(workspacePath, "hubradio.db");
        EnsurePathAbsent(workspacePath);
        EnsurePathAbsent(backupRootPath);
        string currentSid = GetCurrentSid();
        _ = WorkspaceAcl.CreateNew(workspacePath, currentSid);
        _ = WorkspaceAcl.CreateNew(backupRootPath, currentSid);
        _ = DatabaseMigrator.Migrate(
            databasePath,
            Path.Combine(workspacePath, "schema-backups"));

        EntityId<ProjectIdTag> projectId = CreateProject(databasePath);
        StoredProject projectBaseline = new ProjectStore(databasePath)
            .GetProject(projectId)
            ?? throw new InvalidOperationException(
                "The recovery project baseline was not stored.");
        byte[] originalContent = CreateDeterministicBytes(1024 * 1024, 13);
        byte[] approvedContent = CreateDeterministicBytes(512 * 1024, 29);
        StoredArtifact original = await PromoteArtifactAsync(
            databasePath,
            workspacePath,
            projectId,
            ArtifactKind.Original,
            "source/original.bin",
            originalContent).ConfigureAwait(false);
        StoredArtifact approvedRender = await PromoteArtifactAsync(
            databasePath,
            workspacePath,
            projectId,
            ArtifactKind.Render,
            "approved/final.bin",
            approvedContent).ConfigureAwait(false);
        DateTimeOffset recordedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var version = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            projectId,
            parentVersionId: null,
            "q007-editor",
            recordedAt,
            "Q-007 approved recovery baseline",
            approvedRender.ContentDigest,
            []);
        var projectStore = new ProjectStore(databasePath);
        projectStore.AppendCompositionVersion(version, "{\"q007\":true}");
        StoredCompositionVersion compositionBaseline = projectStore
            .GetCompositionVersion(version.Id)
            ?? throw new InvalidOperationException(
                "The recovery composition baseline was not stored.");
        var reviewStore = new ReviewStore(databasePath);
        reviewStore.AppendApproval(new Approval(
            EntityId<ApprovalIdTag>.New(),
            version,
            "q007-approver",
            ReviewActorRole.ContentApprover,
            recordedAt.AddSeconds(1),
            "Q-007 approved restore baseline"));
        var auditLog = new AuditLog(databasePath);
        auditLog.Append(new AuditEvent(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ProjectRegistered,
            "q007-operator",
            ReviewActorRole.Administrator,
            recordedAt.AddSeconds(2),
            "Q-007 recovery project registered",
            projectId));
        auditLog.Append(new AuditEvent(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ApprovalGranted,
            "q007-approver",
            ReviewActorRole.ContentApprover,
            recordedAt.AddSeconds(3),
            "Q-007 recovery approval recorded",
            projectId,
            version));
        StoredVersionEvidence reviewBaseline = reviewStore.GetVersionEvidence(version.Id)
            ?? throw new InvalidOperationException(
                "The recovery review baseline was not stored.");
        StoredAuditEvent[] auditBaseline = auditLog
            .GetProjectEvents(projectId)
            .ToArray();
        string originalPath = ResolveStoragePath(workspacePath, original.StorageReference);
        string approvedPath = ResolveStoragePath(
            workspacePath,
            approvedRender.StorageReference);
        ContentDigest originalBaseline = await ComputeContentDigestAsync(originalPath)
            .ConfigureAwait(false);
        ContentDigest approvedBaseline = await ComputeContentDigestAsync(approvedPath)
            .ConfigureAwait(false);
        bool baselineContentMatchesRegistrations =
            originalBaseline == original.ContentDigest &&
            approvedBaseline == approvedRender.ContentDigest;

        var service = new BackupService(
            databasePath,
            workspacePath,
            backupRootPath,
            currentSid);
        Stopwatch backupElapsed = Stopwatch.StartNew();
        BackupResult backup = await service.CreateBackupAsync().ConfigureAwait(false);
        backupElapsed.Stop();
        DateTimeOffset backupCompletedAt = DateTimeOffset.UtcNow;

        byte[] corruptedApproved = (byte[])approvedContent.Clone();
        corruptedApproved[corruptedApproved.Length / 2] ^= 0xff;
        ContentDigest corruptedApprovedDigest = ContentDigest.Compute(corruptedApproved);
        DateTimeOffset artifactFaultAt = DateTimeOffset.UtcNow;
        WriteDurably(approvedPath, corruptedApproved);
        Stopwatch artifactRecoveryElapsed = Stopwatch.StartNew();
        Stopwatch artifactDetectionElapsed = Stopwatch.StartNew();
        WorkspaceRecoveryCheckResult artifactFault = await new WorkspaceRecovery(
            databasePath,
            workspacePath).CheckAsync().ConfigureAwait(false);
        artifactDetectionElapsed.Stop();
        bool artifactMismatchDetected = artifactFault.Issues.Any(issue =>
            issue.Kind == WorkspaceIntegrityIssueKind.ArtifactDigestMismatch &&
            string.Equals(
                issue.EntityId,
                approvedRender.Id.ToString(),
                StringComparison.Ordinal));
        RestoreResult artifactRestore = await service.RestoreBackupAsync(backup.BackupId)
            .ConfigureAwait(false);
        BaselineVerification artifactRestored = await VerifyBaselineAsync(
            databasePath,
            workspacePath,
            original,
            approvedRender,
            originalBaseline,
            approvedBaseline,
            version,
            projectBaseline,
            compositionBaseline,
            reviewBaseline,
            auditBaseline).ConfigureAwait(false);
        artifactRecoveryElapsed.Stop();
        string artifactPreviousPath = Path.Combine(
            normalizedCaseRoot,
            artifactRestore.PreviousWorkspaceDirectoryName);
        bool artifactPreviousWorkspaceRetained = Directory.Exists(artifactPreviousPath) &&
            (await ComputeContentDigestAsync(ResolveStoragePath(
                artifactPreviousPath,
                approvedRender.StorageReference)).ConfigureAwait(false)) ==
                    corruptedApprovedDigest;
        bool artifactRestoreMatchedBackup =
            artifactRestore.BackupId == backup.BackupId &&
            artifactRestore.RestoredFileCount == backup.FileCount;

        DateTimeOffset databaseFaultAt = DateTimeOffset.UtcNow;
        byte[] databaseCorruption = Enumerable.Repeat((byte)0xa5, 4096).ToArray();
        ContentDigest databaseCorruptionDigest = ContentDigest.Compute(databaseCorruption);
        WriteDurably(databasePath, databaseCorruption);
        Stopwatch databaseRecoveryElapsed = Stopwatch.StartNew();
        Stopwatch databaseDetectionElapsed = Stopwatch.StartNew();
        WorkspaceRecoveryCheckResult databaseFault = await new WorkspaceRecovery(
            databasePath,
            workspacePath).CheckAsync().ConfigureAwait(false);
        databaseDetectionElapsed.Stop();
        bool databaseInvalidDetected = !databaseFault.DatabaseUsable &&
            databaseFault.Issues.Count == 1 &&
            databaseFault.Issues[0].Kind == WorkspaceIntegrityIssueKind.DatabaseInvalid;
        var databaseRestoreService = new BackupService(
            databasePath,
            workspacePath,
            backupRootPath,
            currentSid);
        RestoreResult databaseRestore = await databaseRestoreService
            .RestoreBackupAsync(backup.BackupId).ConfigureAwait(false);
        BaselineVerification databaseRestored = await VerifyBaselineAsync(
            databasePath,
            workspacePath,
            original,
            approvedRender,
            originalBaseline,
            approvedBaseline,
            version,
            projectBaseline,
            compositionBaseline,
            reviewBaseline,
            auditBaseline).ConfigureAwait(false);
        databaseRecoveryElapsed.Stop();
        string databasePreviousPath = Path.Combine(
            normalizedCaseRoot,
            databaseRestore.PreviousWorkspaceDirectoryName);
        string previousDatabasePath = Path.Combine(databasePreviousPath, "hubradio.db");
        bool databasePreviousWorkspaceRetained =
            Directory.Exists(databasePreviousPath) &&
            File.Exists(previousDatabasePath) &&
            new FileInfo(previousDatabasePath).Length == databaseCorruption.LongLength &&
            await ComputeContentDigestAsync(previousDatabasePath).ConfigureAwait(false) ==
                databaseCorruptionDigest;
        bool databaseRestoreMatchedBackup =
            databaseRestore.BackupId == backup.BackupId &&
            databaseRestore.RestoredFileCount == backup.FileCount;

        bool projectUnchanged = artifactRestored.ProjectMatches &&
            databaseRestored.ProjectMatches;
        bool originalUnchanged = artifactRestored.OriginalMatches &&
            databaseRestored.OriginalMatches;
        bool approvedArtifactUnchanged = artifactRestored.ApprovedMatches &&
            databaseRestored.ApprovedMatches;
        bool compositionDigestUnchanged = artifactRestored.CompositionDigestMatches &&
            databaseRestored.CompositionDigestMatches;
        bool compositionVersionUnchanged = artifactRestored.CompositionVersionMatches &&
            databaseRestored.CompositionVersionMatches;
        bool approvalRecordUnchanged = artifactRestored.ApprovalMatches &&
            databaseRestored.ApprovalMatches;
        bool auditChainUnchanged = artifactRestored.AuditMatches &&
            databaseRestored.AuditMatches;
        bool passed = baselineContentMatchesRegistrations &&
            backup.FileCount == 3 &&
            artifactMismatchDetected &&
            artifactPreviousWorkspaceRetained &&
            artifactRestoreMatchedBackup &&
            artifactRestored.IsIntact &&
            databaseInvalidDetected &&
            databasePreviousWorkspaceRetained &&
            databaseRestoreMatchedBackup &&
            databaseRestored.IsIntact &&
            projectUnchanged &&
            originalUnchanged &&
            approvedArtifactUnchanged &&
            compositionDigestUnchanged &&
            compositionVersionUnchanged &&
            approvalRecordUnchanged &&
            auditChainUnchanged;

        WriteResult(new
        {
            schemaVersion = "1.0.0",
            scenario = "verified-backup-artifact-and-database-restore",
            implementationAssembly = typeof(BackupService).Assembly.GetName().Name,
            backupFileCount = backup.FileCount,
            backupMilliseconds = Math.Round(backupElapsed.Elapsed.TotalMilliseconds, 3),
            baselineContentMatchesRegistrations,
            artifact = new
            {
                mismatchDetected = artifactMismatchDetected,
                previousWorkspaceRetained = artifactPreviousWorkspaceRetained,
                restoreMatchedBackup = artifactRestoreMatchedBackup,
                detectionMilliseconds = Math.Round(
                    artifactDetectionElapsed.Elapsed.TotalMilliseconds,
                    3),
                detectionThroughVerifiedRecoveryMilliseconds = Math.Round(
                    artifactRecoveryElapsed.Elapsed.TotalMilliseconds,
                    3),
                backupAgeAtFaultMilliseconds = Math.Round(
                    Math.Max(
                        0,
                        (artifactFaultAt - backupCompletedAt).TotalMilliseconds),
                    3),
            },
            database = new
            {
                invalidDetected = databaseInvalidDetected,
                previousWorkspaceRetained = databasePreviousWorkspaceRetained,
                restoreMatchedBackup = databaseRestoreMatchedBackup,
                detectionMilliseconds = Math.Round(
                    databaseDetectionElapsed.Elapsed.TotalMilliseconds,
                    3),
                detectionThroughVerifiedRecoveryMilliseconds = Math.Round(
                    databaseRecoveryElapsed.Elapsed.TotalMilliseconds,
                    3),
                backupAgeAtFaultMilliseconds = Math.Round(
                    Math.Max(
                        0,
                        (databaseFaultAt - backupCompletedAt).TotalMilliseconds),
                    3),
            },
            projectUnchanged,
            originalUnchanged,
            approvedArtifactUnchanged,
            compositionDigestUnchanged,
            compositionVersionUnchanged,
            approvalRecordUnchanged,
            auditChainUnchanged,
            approvalCount = reviewBaseline.Approvals.Count,
            auditEventCount = auditBaseline.Length,
            lostOriginalBytes = originalUnchanged ? 0 : original.ByteSize,
            lostApprovedBytes = approvedArtifactUnchanged ? 0 : approvedRender.ByteSize,
            lostApprovalRecords = approvalRecordUnchanged ? 0 : reviewBaseline.Approvals.Count,
            lostAuditEvents = auditChainUnchanged ? 0 : auditBaseline.Length,
            passed,
        });
        return passed ? 0 : 1;
    }

    private static EntityId<ProjectIdTag> CreateProject(string databasePath)
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        var project = new Project(
            projectId,
            "Q-007 synthetic recovery project",
            "Exercise crash-safe production storage services.",
            "Q-007 test operator",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        new ProjectStore(databasePath).CreateProject(
            project,
            "{\"q007\":true}",
            DateTimeOffset.UtcNow);
        return projectId;
    }

    private static async Task<StoredArtifact> PromoteArtifactAsync(
        string databasePath,
        string workspacePath,
        EntityId<ProjectIdTag> projectId,
        ArtifactKind kind,
        string relativePath,
        byte[] content)
    {
        string stagingPath = Path.Combine(
            workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            string.Concat(Guid.NewGuid().ToString("N"), ".tmp"));
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        WriteDurably(stagingPath, content);
        var request = new ArtifactPromotionRequest(
            EntityId<ArtifactIdTag>.New(),
            projectId,
            kind,
            stagingPath,
            relativePath,
            ContentDigest.Compute(content),
            content.LongLength,
            DateTimeOffset.UtcNow);
        return await new ArtifactStore(databasePath, workspacePath)
            .PromoteAsync(request).ConfigureAwait(false);
    }

    private static async Task<BaselineVerification> VerifyBaselineAsync(
        string databasePath,
        string workspacePath,
        StoredArtifact original,
        StoredArtifact approved,
        ContentDigest originalDigest,
        ContentDigest approvedDigest,
        CompositionVersion version,
        StoredProject projectBaseline,
        StoredCompositionVersion compositionBaseline,
        StoredVersionEvidence reviewBaseline,
        IReadOnlyList<StoredAuditEvent> auditBaseline)
    {
        var artifactStore = new ArtifactStore(databasePath, workspacePath);
        StoredArtifact currentOriginal = await artifactStore.ValidateRegisteredAsync(
            original.Id).ConfigureAwait(false);
        StoredArtifact currentApproved = await artifactStore.ValidateRegisteredAsync(
            approved.Id).ConfigureAwait(false);
        StoredVersionEvidence? evidence = new ReviewStore(databasePath)
            .GetVersionEvidence(version.Id);
        var projectStore = new ProjectStore(databasePath);
        StoredProject? currentProject = projectStore.GetProject(version.ProjectId);
        StoredCompositionVersion? currentComposition = projectStore
            .GetCompositionVersion(version.Id);
        var auditLog = new AuditLog(databasePath);
        IReadOnlyList<StoredAuditEvent> auditEvents = auditLog.GetProjectEvents(
            version.ProjectId);
        WorkspaceRecoveryCheckResult recovery = await new WorkspaceRecovery(
            databasePath,
            workspacePath).CheckAsync().ConfigureAwait(false);
        bool originalMatches = ArtifactsMatch(original, currentOriginal) &&
            currentOriginal.ContentDigest == originalDigest &&
            await ComputeContentDigestAsync(ResolveStoragePath(
                workspacePath,
                currentOriginal.StorageReference)).ConfigureAwait(false) == originalDigest;
        bool approvedMatches = ArtifactsMatch(approved, currentApproved) &&
            currentApproved.ContentDigest == approvedDigest &&
            await ComputeContentDigestAsync(ResolveStoragePath(
                workspacePath,
                currentApproved.StorageReference)).ConfigureAwait(false) == approvedDigest;
        bool projectMatches = currentProject is not null &&
            ProjectsMatch(projectBaseline, currentProject);
        bool compositionDigestMatches = evidence is not null &&
            evidence.CompositionVersionDigest == approvedDigest &&
            evidence.CompositionVersionDigest == reviewBaseline.CompositionVersionDigest;
        bool compositionVersionMatches = currentComposition is not null &&
            CompositionVersionsMatch(compositionBaseline, currentComposition);
        bool approvalMatches = evidence is not null &&
            VersionEvidenceMatches(reviewBaseline, evidence);
        bool auditMatches = AuditEventsMatch(auditBaseline, auditEvents);
        return new BaselineVerification(
            projectMatches,
            originalMatches,
            approvedMatches,
            compositionDigestMatches,
            compositionVersionMatches,
            approvalMatches,
            auditMatches,
            recovery.DatabaseUsable &&
                recovery.IsIntact &&
                recovery.Issues.Count == 0 &&
                recovery.StagingCandidates.Count == 0);
    }

    private static bool ProjectsMatch(StoredProject expected, StoredProject actual) =>
        expected.Id == actual.Id &&
        expected.Revision == actual.Revision &&
        expected.PublicationScope == actual.PublicationScope &&
        expected.Deadline == actual.Deadline &&
        expected.UpdatedAt == actual.UpdatedAt &&
        string.Equals(
            expected.CanonicalJson,
            actual.CanonicalJson,
            StringComparison.Ordinal);

    private static bool ArtifactsMatch(StoredArtifact expected, StoredArtifact actual) =>
        expected.Id == actual.Id &&
        expected.ProjectId == actual.ProjectId &&
        expected.Kind == actual.Kind &&
        string.Equals(
            expected.StorageReference,
            actual.StorageReference,
            StringComparison.Ordinal) &&
        expected.ContentDigest == actual.ContentDigest &&
        expected.ByteSize == actual.ByteSize &&
        expected.CreatedAt == actual.CreatedAt;

    private static bool ArtifactMatchesPartialState(
        StoredArtifact artifact,
        PartialState state) =>
        string.Equals(
            artifact.Id.ToString(),
            state.ApprovedArtifactId,
            StringComparison.Ordinal) &&
        string.Equals(
            artifact.ProjectId.ToString(),
            state.ProjectId,
            StringComparison.Ordinal) &&
        string.Equals(
            artifact.Kind.ToString(),
            state.ApprovedArtifactKind,
            StringComparison.Ordinal) &&
        string.Equals(
            artifact.StorageReference,
            state.ApprovedArtifactStorageReference,
            StringComparison.Ordinal) &&
        string.Equals(
            artifact.ContentDigest.Hex,
            state.ApprovedArtifactDigest,
            StringComparison.Ordinal) &&
        artifact.ByteSize == state.ApprovedArtifactByteSize &&
        artifact.CreatedAt == state.ApprovedArtifactCreatedAt;

    private static bool CompositionVersionsMatch(
        StoredCompositionVersion expected,
        StoredCompositionVersion actual) =>
        expected.Id == actual.Id &&
        expected.ProjectId == actual.ProjectId &&
        expected.ParentVersionId == actual.ParentVersionId &&
        expected.ContentDigest == actual.ContentDigest &&
        expected.CreatedAt == actual.CreatedAt &&
        string.Equals(
            expected.CanonicalJson,
            actual.CanonicalJson,
            StringComparison.Ordinal);

    private static bool ApprovalsMatch(
        IReadOnlyList<StoredApproval> expected,
        IReadOnlyList<StoredApproval> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            StoredApproval left = expected[index];
            StoredApproval right = actual[index];
            if (left.Id != right.Id ||
                left.ProjectId != right.ProjectId ||
                left.CompositionVersionId != right.CompositionVersionId ||
                left.CompositionVersionDigest != right.CompositionVersionDigest ||
                !string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) ||
                left.ActorRole != right.ActorRole ||
                left.ApprovedAt != right.ApprovedAt ||
                !string.Equals(left.Reason, right.Reason, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool VersionEvidenceMatches(
        StoredVersionEvidence expected,
        StoredVersionEvidence actual) =>
        expected.CompositionVersionId == actual.CompositionVersionId &&
        expected.ProjectId == actual.ProjectId &&
        expected.CompositionVersionDigest == actual.CompositionVersionDigest &&
        expected.ReviewRecords.Count == 0 &&
        actual.ReviewRecords.Count == 0 &&
        expected.ReviewComments.Count == 0 &&
        actual.ReviewComments.Count == 0 &&
        ApprovalsMatch(expected.Approvals, actual.Approvals);

    private static bool AuditEventsMatch(
        IReadOnlyList<StoredAuditEvent> expected,
        IReadOnlyList<StoredAuditEvent> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            StoredAuditEvent left = expected[index];
            StoredAuditEvent right = actual[index];
            if (left.Id != right.Id ||
                left.ProjectId != right.ProjectId ||
                left.Kind != right.Kind ||
                !string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) ||
                left.ActorRole != right.ActorRole ||
                left.OccurredAt != right.OccurredAt ||
                !string.Equals(left.Reason, right.Reason, StringComparison.Ordinal) ||
                left.CompositionVersionId != right.CompositionVersionId ||
                left.CompositionVersionDigest != right.CompositionVersionDigest)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task CreateDeterministicFileAsync(string path, int byteSize)
    {
        byte[] buffer = new byte[FileBufferSize];
        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)((index * 31 + 17) % 251);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        await using FileStream stream = new(path, options);
        int remaining = byteSize;
        while (remaining > 0)
        {
            int count = Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await stream.FlushAsync().ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        CryptographicOperations.ZeroMemory(buffer);
    }

    private static void CreateSparseSource(string path, long byteSize)
    {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.WriteThrough | FileOptions.RandomAccess);
        stream.SetLength(byteSize);
        stream.Position = 0;
        stream.Write([0x51, 0x30, 0x30, 0x37]);
        stream.Position = byteSize - 4;
        stream.Write([0x37, 0x30, 0x30, 0x51]);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] CreateDeterministicBytes(int byteSize, int seed)
    {
        byte[] content = new byte[byteSize];
        for (int index = 0; index < content.Length; index++)
        {
            content[index] = (byte)((index * 31 + seed) % 251);
        }

        return content;
    }

    private static void WriteDurably(string path, ReadOnlySpan<byte> content)
    {
        using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.WriteThrough | FileOptions.SequentialScan);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ComputeDigestAsync(string path)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream).ConfigureAwait(false);
    }

    private static async Task<ContentDigest> ComputeContentDigestAsync(string path) =>
        ContentDigest.FromBytes(await ComputeDigestAsync(path).ConfigureAwait(false));

    private static string ResolveStoragePath(
        string workspacePath,
        string storageReference) =>
        Path.Combine(
            workspacePath,
            storageReference.Replace('/', Path.DirectorySeparatorChar));

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(
                    "A reliability probe coordination file was not observed.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static T ReadJson<T>(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException("Reliability probe state is invalid.");
    }

    private static EntityId<TTag> ParseEntityId<TTag>(string value) =>
        EntityId<TTag>.TryParse(value, out EntityId<TTag>? parsed) &&
        string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException("Reliability probe identity is invalid.");

    private static ContentDigest ParseDigest(string value) =>
        ContentDigest.TryParse(value, out ContentDigest? parsed) &&
        string.Equals(value, parsed.Hex, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException("Reliability probe digest is invalid.");

    [SupportedOSPlatform("windows")]
    private static string GetCurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException(
                "The current Windows identity has no user SID.");
    }

    private static string RequireExistingDirectory(string path)
    {
        string normalized = RequireAbsolutePath(path);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(
                "A required reliability probe directory is unavailable.");
        }

        return normalized;
    }

    private static string RequireAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A reliability probe path is invalid.");
        }

        return Path.GetFullPath(path);
    }

    private static void EnsurePathAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("A reliability probe output already exists.");
        }
    }

    private static string RequireArgument(string[] args, int index) =>
        args.Length > index
            ? args[index]
            : throw new ArgumentException("A required reliability probe argument is missing.");

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException("A reliability probe output directory is unavailable.");
        }

        Directory.CreateDirectory(directory);
        string stagingPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            File.WriteAllText(
                stagingPath,
                JsonSerializer.Serialize(value, JsonOptions));
            File.Move(stagingPath, path, overwrite: false);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    private static void WriteResult<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private sealed record PartialState(
        string ProjectId,
        string JobId,
        string CandidateId,
        string SourceDigest,
        long SourceByteSize,
        DateTime SourceLastWriteUtc,
        string ApprovedArtifactId,
        string ApprovedArtifactKind,
        string ApprovedArtifactStorageReference,
        string ApprovedArtifactDigest,
        long ApprovedArtifactByteSize,
        DateTimeOffset ApprovedArtifactCreatedAt);

    private sealed record BaselineVerification(
        bool ProjectMatches,
        bool OriginalMatches,
        bool ApprovedMatches,
        bool CompositionDigestMatches,
        bool CompositionVersionMatches,
        bool ApprovalMatches,
        bool AuditMatches,
        bool IsIntact);
}