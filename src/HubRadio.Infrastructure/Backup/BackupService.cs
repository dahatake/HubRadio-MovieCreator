using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Audit;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Recovery;
using HubRadio.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Backup;

public sealed class BackupIdTag
{
}

public enum BackupServiceFailureReason
{
    AccessControlInvalid,
    SourceWorkspaceInvalid,
    SourceAuditInvalid,
    BackupCreationFailed,
    ArtifactValidationFailed,
    ManifestInvalid,
    BackupNotFound,
    BackupContentInvalid,
    RestoreValidationFailed,
    CutoverFailed,
    RollbackFailed,
}

public sealed class BackupServiceException : Exception
{
    internal BackupServiceException(
        BackupServiceFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public BackupServiceFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(BackupServiceFailureReason reason) =>
        reason switch
        {
            BackupServiceFailureReason.AccessControlInvalid =>
                "The backup access control settings are invalid.",
            BackupServiceFailureReason.SourceWorkspaceInvalid =>
                "The source workspace is not valid for backup.",
            BackupServiceFailureReason.SourceAuditInvalid =>
                "The source audit chain is invalid.",
            BackupServiceFailureReason.BackupCreationFailed =>
                "The backup could not be created safely.",
            BackupServiceFailureReason.ArtifactValidationFailed =>
                "A backup artifact could not be validated.",
            BackupServiceFailureReason.ManifestInvalid =>
                "The backup manifest is invalid.",
            BackupServiceFailureReason.BackupNotFound =>
                "The completed backup was not found.",
            BackupServiceFailureReason.BackupContentInvalid =>
                "The backup content is invalid.",
            BackupServiceFailureReason.RestoreValidationFailed =>
                "The restored workspace could not be validated.",
            BackupServiceFailureReason.CutoverFailed =>
                "The workspace cutover failed.",
            BackupServiceFailureReason.RollbackFailed =>
                "The workspace cutover requires reconciliation.",
            _ => "The backup operation failed.",
        };
}

public sealed class BackupResult
{
    internal BackupResult(
        EntityId<BackupIdTag> backupId,
        string backupDirectoryName,
        ContentDigest manifestDigest,
        int fileCount)
    {
        BackupId = backupId;
        BackupDirectoryName = backupDirectoryName;
        ManifestDigest = manifestDigest;
        FileCount = fileCount;
    }

    public EntityId<BackupIdTag> BackupId { get; }

    public string BackupDirectoryName { get; }

    public ContentDigest ManifestDigest { get; }

    public int FileCount { get; }
}

public sealed class RestoreResult
{
    internal RestoreResult(
        EntityId<BackupIdTag> backupId,
        string previousWorkspaceDirectoryName,
        int restoredFileCount)
    {
        BackupId = backupId;
        PreviousWorkspaceDirectoryName = previousWorkspaceDirectoryName;
        RestoredFileCount = restoredFileCount;
    }

    public EntityId<BackupIdTag> BackupId { get; }

    public string PreviousWorkspaceDirectoryName { get; }

    public int RestoredFileCount { get; }
}

public sealed class IncompleteRestoreCandidate
{
    internal IncompleteRestoreCandidate(
        EntityId<BackupIdTag> backupId,
        string directoryName)
    {
        ArgumentNullException.ThrowIfNull(backupId);
        ArgumentNullException.ThrowIfNull(directoryName);
        BackupId = backupId;
        DirectoryName = directoryName;
    }

    public EntityId<BackupIdTag> BackupId { get; }

    public string DirectoryName { get; }
}

public sealed class BackupService
{
    private const int DefaultTimeoutSeconds = 5;
    private const int FileBufferSize = 64 * 1024;
    private const string RestoreDirectoryPrefix = ".hubradio.restore-";

    private static readonly SemaphoreSlim OperationGate = new(1, 1);

    private readonly string _databasePath;
    private readonly string _workspacePath;
    private readonly string _backupRootPath;
    private readonly string _currentActorSid;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, string> _directoryMove;

    public BackupService(
        string databasePath,
        string workspacePath,
        string backupRootPath,
        string currentActorSid)
        : this(
            databasePath,
            workspacePath,
            backupRootPath,
            currentActorSid,
            TimeProvider.System)
    {
    }

    internal BackupService(
        string databasePath,
        string workspacePath,
        string backupRootPath,
        string currentActorSid,
        TimeProvider timeProvider,
        Action<string, string>? directoryMove = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(currentActorSid);

        _databasePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            databasePath,
            nameof(databasePath));
        _workspacePath = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                workspacePath,
                nameof(workspacePath)));
        _backupRootPath = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                backupRootPath,
                nameof(backupRootPath)));
        _currentActorSid = currentActorSid.Trim();
        _timeProvider = timeProvider;
        _directoryMove = directoryMove ?? Directory.Move;

        string expectedDatabasePath = Path.GetFullPath(
            Path.Combine(_workspacePath, "hubradio.db"));
        if (!string.Equals(
                _databasePath,
                expectedDatabasePath,
                WorkspacePathSafety.PathComparison))
        {
            throw new ArgumentException(
                "The database path must identify hubradio.db directly under the workspace root.",
                nameof(databasePath));
        }

        if (IsSameOrDescendant(_workspacePath, _backupRootPath) ||
            IsSameOrDescendant(_backupRootPath, _workspacePath))
        {
            throw new ArgumentException(
                "The workspace and backup roots must be disjoint.",
                nameof(backupRootPath));
        }

        VerifyProtectedRoots();
    }

    public async Task<BackupResult> CreateBackupAsync(
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateBackupCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    public async Task<RestoreResult> RestoreBackupAsync(
        EntityId<BackupIdTag> backupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupId);
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RestoreBackupCoreAsync(backupId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    public async Task<IReadOnlyList<IncompleteRestoreCandidate>>
        ListIncompleteRestoresAsync(
            CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ListIncompleteRestoresCore(cancellationToken);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    public async Task DiscardIncompleteRestoreAsync(
        IncompleteRestoreCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DiscardIncompleteRestoreCore(candidate, cancellationToken);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    private async Task<BackupResult> CreateBackupCoreAsync(
        CancellationToken cancellationToken)
    {
        string? stagingPath = null;
        bool stagingAttempted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyProtectedRoots();
            await ValidateSourceWorkspaceAsync(cancellationToken).ConfigureAwait(false);

            EntityId<BackupIdTag> backupId = EntityId<BackupIdTag>.New();
            string finalDirectoryName = string.Concat("backup-", backupId.ToString());
            string stagingDirectoryName = string.Concat(
                ".backup-",
                backupId.ToString(),
                ".staging");
            string finalPath = Path.Combine(_backupRootPath, finalDirectoryName);
            stagingPath = Path.Combine(_backupRootPath, stagingDirectoryName);
            EnsurePathAbsent(finalPath);
            EnsurePathAbsent(stagingPath);

            stagingAttempted = true;
            Directory.CreateDirectory(stagingPath);
            WorkspaceAcl.VerifyInheritedDirectory(
                stagingPath,
                _backupRootPath,
                _currentActorSid);
            EnsureDirectoryEmpty(stagingPath);

            string payloadRoot = Path.Combine(stagingPath, "payload");
            CreateDirectoryChain(stagingPath, payloadRoot);
            string backupDatabasePath = Path.Combine(payloadRoot, "hubradio.db");
            CreateOnlineDatabaseSnapshot(backupDatabasePath);
            PruneExcludedArtifactRows(backupDatabasePath);
            ValidateDatabaseAndAudit(backupDatabasePath);

            FileSnapshot databaseSnapshot = await ReadStableSnapshotAsync(
                stagingPath,
                backupDatabasePath,
                FileShare.Read,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<StoredArtifact> storedArtifacts = ReadStoredArtifacts(
                backupDatabasePath);
            var liveStore = new ArtifactStore(_databasePath, _workspacePath);
            var manifestArtifacts = new List<BackupManifestArtifact>(storedArtifacts.Count);

            foreach (StoredArtifact storedArtifact in storedArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StoredArtifact current = liveStore.GetArtifact(storedArtifact.Id)
                    ?? throw Failure(
                        BackupServiceFailureReason.ArtifactValidationFailed,
                        reconciliationRequired: true);
                if (!HasSameMetadata(storedArtifact, current))
                {
                    throw Failure(
                        BackupServiceFailureReason.ArtifactValidationFailed,
                        reconciliationRequired: true);
                }

                string sourcePath = liveStore.GetValidatedRegisteredArtifactPath(
                    storedArtifact.Id);
                string backupRelativePath = string.Concat(
                    "payload/",
                    storedArtifact.StorageReference);
                if (!WorkspacePathSafety.TryResolvePortableRelativePath(
                        stagingPath,
                        backupRelativePath,
                        out string destinationPath))
                {
                    throw Failure(
                        BackupServiceFailureReason.ArtifactValidationFailed,
                        reconciliationRequired: true);
                }

                await CopyVerifiedFileAsync(
                    _workspacePath,
                    sourcePath,
                    stagingPath,
                    destinationPath,
                    storedArtifact.ByteSize,
                    storedArtifact.ContentDigest,
                    cancellationToken).ConfigureAwait(false);

                StoredArtifact afterCopy = liveStore.GetArtifact(storedArtifact.Id)
                    ?? throw Failure(
                        BackupServiceFailureReason.ArtifactValidationFailed,
                        reconciliationRequired: true);
                string pathAfterCopy = liveStore.GetValidatedRegisteredArtifactPath(
                    storedArtifact.Id);
                if (!HasSameMetadata(storedArtifact, afterCopy) ||
                    !string.Equals(
                        sourcePath,
                        pathAfterCopy,
                        WorkspacePathSafety.PathComparison))
                {
                    throw Failure(
                        BackupServiceFailureReason.ArtifactValidationFailed,
                        reconciliationRequired: true);
                }

                manifestArtifacts.Add(new BackupManifestArtifact(
                    storedArtifact.Id,
                    storedArtifact.ProjectId,
                    storedArtifact.Kind,
                    storedArtifact.StorageReference,
                    backupRelativePath,
                    storedArtifact.ByteSize,
                    storedArtifact.ContentDigest,
                    storedArtifact.CreatedAt));
            }

            var databaseRecord = new BackupManifestDatabase(
                BackupManifest.DatabaseRelativePath,
                databaseSnapshot.ByteSize,
                databaseSnapshot.Sha256);
            BackupManifest manifest = BackupManifest.Create(
                backupId,
                _timeProvider.GetUtcNow(),
                databaseRecord,
                manifestArtifacts);
            string[] expectedPayloadFiles = GetBackupPayloadPaths(manifest);
            ValidateExactTree(stagingPath, expectedPayloadFiles);

            string manifestPath = Path.Combine(stagingPath, BackupManifest.FileName);
            await WriteNewFileAsync(
                manifestPath,
                manifest.Serialize(),
                cancellationToken).ConfigureAwait(false);
            BackupManifest writtenManifest = BackupManifest.Parse(
                await ReadFileBytesAsync(
                    stagingPath,
                    manifestPath,
                    BackupManifest.MaximumUtf8Bytes,
                    cancellationToken).ConfigureAwait(false));
            if (writtenManifest.BackupId != backupId ||
                writtenManifest.ManifestDigest != manifest.ManifestDigest)
            {
                throw Failure(
                    BackupServiceFailureReason.ManifestInvalid,
                    reconciliationRequired: true);
            }

            ValidateExactTree(
                stagingPath,
                [.. expectedPayloadFiles, BackupManifest.FileName]);
            WorkspaceAcl.VerifyInheritedDirectory(
                stagingPath,
                _backupRootPath,
                _currentActorSid);
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePathAbsent(finalPath);
            _directoryMove(stagingPath, finalPath);

            try
            {
                WorkspaceAcl.VerifyInheritedDirectory(
                    finalPath,
                    _backupRootPath,
                    _currentActorSid);
                ValidateExactTree(
                    finalPath,
                    [.. expectedPayloadFiles, BackupManifest.FileName]);
            }
            catch
            {
                if (!TryMoveDirectory(finalPath, stagingPath))
                {
                    throw Failure(
                        BackupServiceFailureReason.BackupCreationFailed,
                        reconciliationRequired: true);
                }

                throw Failure(
                    BackupServiceFailureReason.AccessControlInvalid,
                    reconciliationRequired: true);
            }

            return new BackupResult(
                backupId,
                finalDirectoryName,
                manifest.ManifestDigest,
                manifest.PayloadFileCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupServiceException)
        {
            throw;
        }
        catch (WorkspaceAclException)
        {
            throw Failure(
                BackupServiceFailureReason.AccessControlInvalid,
                stagingAttempted);
        }
        catch (ArtifactStoreException)
        {
            throw Failure(
                BackupServiceFailureReason.ArtifactValidationFailed,
                stagingAttempted);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(
                BackupServiceFailureReason.BackupCreationFailed,
                stagingAttempted);
        }
    }

    private async Task<RestoreResult> RestoreBackupCoreAsync(
        EntityId<BackupIdTag> backupId,
        CancellationToken cancellationToken)
    {
        string? restorePath = null;
        bool restoreStagingCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyProtectedRoots();
            string backupDirectory = ResolveCompletedBackupDirectory(backupId);
            string manifestPath = Path.Combine(
                backupDirectory,
                BackupManifest.FileName);
            byte[] manifestBytes = await ReadFileBytesAsync(
                backupDirectory,
                manifestPath,
                BackupManifest.MaximumUtf8Bytes,
                cancellationToken).ConfigureAwait(false);

            BackupManifest manifest;
            try
            {
                manifest = BackupManifest.Parse(manifestBytes);
            }
            catch (InvalidDataException)
            {
                throw Failure(BackupServiceFailureReason.ManifestInvalid);
            }

            if (manifest.BackupId != backupId)
            {
                throw Failure(BackupServiceFailureReason.ManifestInvalid);
            }

            string[] backupPayloadPaths = GetBackupPayloadPaths(manifest);
            ValidateExactTree(
                backupDirectory,
                [.. backupPayloadPaths, BackupManifest.FileName]);
            await ValidateBackupPayloadAsync(
                backupDirectory,
                manifest,
                cancellationToken).ConfigureAwait(false);
            string backupDatabasePath = ResolveRequiredRelativePath(
                backupDirectory,
                manifest.Database.RelativePath);
            ValidateBackupDatabaseAndCatalog(backupDatabasePath, manifest);

            cancellationToken.ThrowIfCancellationRequested();
            string workspaceParent = Path.GetDirectoryName(_workspacePath)
                ?? throw Failure(BackupServiceFailureReason.RestoreValidationFailed);
            string restoreDirectoryName = CreateRestoreDirectoryName(backupId);
            restorePath = Path.Combine(workspaceParent, restoreDirectoryName);
            EnsurePathAbsent(restorePath);
            _ = WorkspaceAcl.CreateNew(restorePath, _currentActorSid);
            restoreStagingCreated = true;

            string restoredDatabasePath = Path.Combine(restorePath, "hubradio.db");
            await CopyVerifiedFileAsync(
                backupDirectory,
                backupDatabasePath,
                restorePath,
                restoredDatabasePath,
                manifest.Database.ByteSize,
                manifest.Database.Sha256,
                cancellationToken).ConfigureAwait(false);

            foreach (BackupManifestArtifact artifact in manifest.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = ResolveRequiredRelativePath(
                    backupDirectory,
                    artifact.BackupRelativePath);
                string destinationPath = ResolveRequiredRelativePath(
                    restorePath,
                    artifact.SourceStorageReference);
                await CopyVerifiedFileAsync(
                    backupDirectory,
                    sourcePath,
                    restorePath,
                    destinationPath,
                    artifact.ByteSize,
                    artifact.Sha256,
                    cancellationToken).ConfigureAwait(false);
            }

            string[] restoredPaths =
            [
                "hubradio.db",
                .. manifest.Artifacts.Select(static artifact =>
                    artifact.SourceStorageReference),
            ];
            ValidateExactTree(restorePath, restoredPaths);
            ValidateDatabaseAndAudit(restoredDatabasePath);
            ValidateDatabaseCatalog(restoredDatabasePath, manifest);
            WorkspaceRecoveryCheckResult recovery = await new WorkspaceRecovery(
                    restoredDatabasePath,
                    restorePath,
                    _timeProvider)
                .CheckAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!recovery.DatabaseUsable ||
                !recovery.IsIntact ||
                recovery.Issues.Count != 0 ||
                recovery.StagingCandidates.Count != 0)
            {
                throw Failure(
                    BackupServiceFailureReason.RestoreValidationFailed,
                    reconciliationRequired: true);
            }

            _ = WorkspaceAcl.VerifyExisting(restorePath, _currentActorSid);
            VerifyProtectedRoots();
            WorkspaceAcl.VerifyInheritedDirectory(
                backupDirectory,
                _backupRootPath,
                _currentActorSid);
            cancellationToken.ThrowIfCancellationRequested();

            return Cutover(
                backupId,
                restorePath,
                manifest.PayloadFileCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupServiceException exception)
        {
            if (restoreStagingCreated && !exception.ReconciliationRequired)
            {
                throw Failure(exception.Reason, reconciliationRequired: true);
            }

            throw;
        }
        catch (WorkspaceAclException)
        {
            throw Failure(
                BackupServiceFailureReason.AccessControlInvalid,
                restoreStagingCreated);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(
                restoreStagingCreated
                    ? BackupServiceFailureReason.RestoreValidationFailed
                    : BackupServiceFailureReason.BackupContentInvalid,
                restoreStagingCreated);
        }
    }

    private async Task ValidateSourceWorkspaceAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                _databasePath,
                targetIsDirectory: false);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(BackupServiceFailureReason.SourceWorkspaceInvalid);
        }

        WorkspaceRecoveryCheckResult recovery;
        try
        {
            recovery = await new WorkspaceRecovery(
                    _databasePath,
                    _workspacePath,
                    _timeProvider)
                .CheckAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(BackupServiceFailureReason.SourceWorkspaceInvalid);
        }

        if (!recovery.DatabaseUsable || !recovery.IsIntact)
        {
            throw Failure(BackupServiceFailureReason.SourceWorkspaceInvalid);
        }

        try
        {
            new AuditLog(_databasePath).Verify();
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(BackupServiceFailureReason.SourceAuditInvalid);
        }
    }

    private IReadOnlyList<IncompleteRestoreCandidate> ListIncompleteRestoresCore(
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyProtectedRoots();
            string workspaceParent = GetWorkspaceParent();
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(
                workspaceParent);

            string[] entries = Directory
                .EnumerateFileSystemEntries(
                    workspaceParent,
                    string.Concat(RestoreDirectoryPrefix, "*"),
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0,
                        MatchCasing = MatchCasing.CaseInsensitive,
                        MatchType = MatchType.Simple,
                    })
                .OrderBy(static path => path, WorkspacePathSafety.PathComparer)
                .ToArray();
            var candidates = new List<IncompleteRestoreCandidate>(entries.Length);
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directoryName = Path.GetFileName(entry);
                if (!TryParseRestoreDirectoryName(
                        directoryName,
                        out EntityId<BackupIdTag>? backupId))
                {
                    throw Failure(
                        BackupServiceFailureReason.RestoreValidationFailed,
                        reconciliationRequired: true);
                }

                FileAttributes attributes =
                    WorkspacePathSafety.GetRequiredAttributes(entry);
                WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
                WorkspacePathSafety.ValidateExistingPathChain(
                    workspaceParent,
                    entry,
                    targetIsDirectory: true);
                _ = WorkspaceAcl.VerifyExisting(entry, _currentActorSid);
                candidates.Add(new IncompleteRestoreCandidate(
                    backupId,
                    directoryName));
            }

            return Array.AsReadOnly(candidates.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupServiceException)
        {
            throw;
        }
        catch (WorkspaceAclException)
        {
            throw Failure(
                BackupServiceFailureReason.AccessControlInvalid,
                reconciliationRequired: true);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(
                BackupServiceFailureReason.RestoreValidationFailed,
                reconciliationRequired: true);
        }
    }

    private void DiscardIncompleteRestoreCore(
        IncompleteRestoreCandidate candidate,
        CancellationToken cancellationToken)
    {
        string? isolatedPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseRestoreDirectoryName(
                    candidate.DirectoryName,
                    out EntityId<BackupIdTag>? parsedBackupId) ||
                parsedBackupId != candidate.BackupId)
            {
                throw Failure(
                    BackupServiceFailureReason.RestoreValidationFailed);
            }

            IReadOnlyList<IncompleteRestoreCandidate> current =
                ListIncompleteRestoresCore(cancellationToken);
            if (!current.Any(item =>
                    item.BackupId == candidate.BackupId &&
                    string.Equals(
                        item.DirectoryName,
                        candidate.DirectoryName,
                        StringComparison.Ordinal)))
            {
                throw Failure(
                    BackupServiceFailureReason.RestoreValidationFailed);
            }

            string workspaceParent = GetWorkspaceParent();
            string candidatePath = Path.Combine(
                workspaceParent,
                candidate.DirectoryName);
            ValidateDirectoryTreeForDeletion(candidatePath, cancellationToken);

            string isolatedDirectoryName = CreateRestoreDirectoryName(
                candidate.BackupId);
            isolatedPath = Path.Combine(workspaceParent, isolatedDirectoryName);
            EnsurePathAbsent(isolatedPath);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(candidatePath, isolatedPath);
            if (WorkspacePathSafety.PathEntryExistsConservatively(candidatePath) ||
                !WorkspacePathSafety.PathEntryExistsConservatively(isolatedPath))
            {
                throw Failure(
                    BackupServiceFailureReason.RestoreValidationFailed,
                    reconciliationRequired: true);
            }

            _ = WorkspaceAcl.VerifyExisting(isolatedPath, _currentActorSid);
            ValidateDirectoryTreeForDeletion(isolatedPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(isolatedPath, recursive: true);
            if (WorkspacePathSafety.PathEntryExistsConservatively(isolatedPath))
            {
                throw Failure(
                    BackupServiceFailureReason.RestoreValidationFailed,
                    reconciliationRequired: true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupServiceException)
        {
            throw;
        }
        catch (WorkspaceAclException)
        {
            throw Failure(
                BackupServiceFailureReason.AccessControlInvalid,
                reconciliationRequired: isolatedPath is not null);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(
                BackupServiceFailureReason.RestoreValidationFailed,
                reconciliationRequired: isolatedPath is not null);
        }
    }

    private static void ValidateDirectoryTreeForDeletion(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            WorkspacePathSafety.ValidateExistingPathChain(
                rootPath,
                directory,
                targetIsDirectory: true);
            string[] entries = Directory
                .EnumerateFileSystemEntries(
                    directory,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0,
                    })
                .OrderBy(static path => path, WorkspacePathSafety.PathComparer)
                .ToArray();
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes =
                    WorkspacePathSafety.GetRequiredAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
                    WorkspacePathSafety.ValidateExistingPathChain(
                        rootPath,
                        entry,
                        targetIsDirectory: true);
                    pending.Push(entry);
                }
                else
                {
                    WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
                    WorkspacePathSafety.ValidateExistingPathChain(
                        rootPath,
                        entry,
                        targetIsDirectory: false);
                }
            }
        }
    }

    private void VerifyProtectedRoots()
    {
        try
        {
            _ = WorkspaceAcl.VerifyExisting(_workspacePath, _currentActorSid);
            _ = WorkspaceAcl.VerifyExisting(_backupRootPath, _currentActorSid);
        }
        catch (WorkspaceAclException)
        {
            throw Failure(BackupServiceFailureReason.AccessControlInvalid);
        }
    }

    private string ResolveCompletedBackupDirectory(
        EntityId<BackupIdTag> backupId)
    {
        string directoryName = string.Concat("backup-", backupId.ToString());
        string path = Path.Combine(_backupRootPath, directoryName);
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _backupRootPath,
                path,
                targetIsDirectory: true);
            WorkspaceAcl.VerifyInheritedDirectory(
                path,
                _backupRootPath,
                _currentActorSid);
            return path;
        }
        catch (WorkspacePathSafetyException exception)
            when (exception.Failure == WorkspacePathSafetyFailure.Missing)
        {
            throw Failure(BackupServiceFailureReason.BackupNotFound);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(BackupServiceFailureReason.BackupContentInvalid);
        }
    }

    private RestoreResult Cutover(
        EntityId<BackupIdTag> backupId,
        string restorePath,
        int restoredFileCount)
    {
        string workspaceParent = GetWorkspaceParent();
        string previousDirectoryName = string.Concat(
            ".hubradio.previous-",
            backupId.ToString(),
            "-",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string previousPath = Path.Combine(workspaceParent, previousDirectoryName);
        EnsurePathAbsent(previousPath);

        bool previousMoved = false;
        bool restoredMoved = false;
        try
        {
            _directoryMove(_workspacePath, previousPath);
            previousMoved = true;
            _ = WorkspaceAcl.VerifyExisting(previousPath, _currentActorSid);

            _directoryMove(restorePath, _workspacePath);
            restoredMoved = true;
            _ = WorkspaceAcl.VerifyExisting(_workspacePath, _currentActorSid);
            return new RestoreResult(
                backupId,
                previousDirectoryName,
                restoredFileCount);
        }
        catch (Exception exception) when (IsCutoverFailure(exception))
        {
            if (!previousMoved)
            {
                try
                {
                    _ = WorkspaceAcl.VerifyExisting(
                        _workspacePath,
                        _currentActorSid);
                    if (WorkspacePathSafety.PathEntryExistsConservatively(previousPath))
                    {
                        throw Failure(
                            BackupServiceFailureReason.RollbackFailed,
                            reconciliationRequired: true);
                    }
                }
                catch (BackupServiceException)
                {
                    throw;
                }
                catch (Exception verificationException)
                    when (IsCutoverFailure(verificationException))
                {
                    throw Failure(
                        BackupServiceFailureReason.RollbackFailed,
                        reconciliationRequired: true);
                }

                throw Failure(
                    BackupServiceFailureReason.CutoverFailed,
                    reconciliationRequired: true);
            }

            if (!TryRollbackCutover(
                    previousPath,
                    restorePath,
                    restoredMoved))
            {
                throw Failure(
                    BackupServiceFailureReason.RollbackFailed,
                    reconciliationRequired: true);
            }

            throw Failure(
                BackupServiceFailureReason.CutoverFailed,
                reconciliationRequired: true);
        }
    }

    private bool TryRollbackCutover(
        string previousPath,
        string restorePath,
        bool restoredMoved)
    {
        try
        {
            if (restoredMoved)
            {
                if (WorkspacePathSafety.PathEntryExistsConservatively(restorePath) ||
                    !WorkspacePathSafety.PathEntryExistsConservatively(_workspacePath))
                {
                    return false;
                }

                _directoryMove(_workspacePath, restorePath);
                _ = WorkspaceAcl.VerifyExisting(restorePath, _currentActorSid);
            }

            if (WorkspacePathSafety.PathEntryExistsConservatively(_workspacePath) ||
                !WorkspacePathSafety.PathEntryExistsConservatively(previousPath))
            {
                return false;
            }

            _directoryMove(previousPath, _workspacePath);
            _ = WorkspaceAcl.VerifyExisting(_workspacePath, _currentActorSid);
            return true;
        }
        catch (Exception exception) when (IsCutoverFailure(exception))
        {
            return false;
        }
    }

    private void CreateOnlineDatabaseSnapshot(string destinationPath)
    {
        WorkspacePathSafety.ValidateExistingPathChain(
            _workspacePath,
            _databasePath,
            targetIsDirectory: false);
        using (var reserved = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            reserved.Flush(flushToDisk: true);
        }

        using SqliteConnection source = OpenRawConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteConnection destination = OpenRawConnection(
            destinationPath,
            SqliteOpenMode.ReadWrite);
        source.BackupDatabase(destination);
    }

    private static void PruneExcludedArtifactRows(string databasePath)
    {
        using SqliteConnection connection = OpenRawConnection(
            databasePath,
            SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM artifacts WHERE kind IN ('Proxy', 'Preview');";
        _ = command.ExecuteNonQuery();
        transaction.Commit();
        ValidateSqliteChecks(databasePath);
    }

    private static void ValidateDatabaseAndAudit(string databasePath)
    {
        DatabaseMigrator.ValidateCurrentDatabase(databasePath);
        ValidateSqliteChecks(databasePath);
        new AuditLog(databasePath).Verify();
    }

    private static void ValidateBackupDatabaseAndCatalog(
        string databasePath,
        BackupManifest manifest)
    {
        try
        {
            ValidateDatabaseAndAudit(databasePath);
            ValidateDatabaseCatalog(databasePath, manifest);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw Failure(BackupServiceFailureReason.BackupContentInvalid);
        }
    }

    private static void ValidateSqliteChecks(string databasePath)
    {
        using SqliteConnection connection = OpenRawConnection(
            databasePath,
            SqliteOpenMode.ReadOnly);
        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidOperationException(
                    "SQLite foreign-key validation failed.");
            }
        }

        using SqliteCommand integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        using SqliteDataReader integrityReader = integrity.ExecuteReader();
        if (!integrityReader.Read() ||
            integrityReader.GetValue(0) is not string result ||
            !string.Equals(result, "ok", StringComparison.Ordinal) ||
            integrityReader.Read())
        {
            throw new InvalidOperationException(
                "SQLite integrity validation failed.");
        }
    }

    private static IReadOnlyList<StoredArtifact> ReadStoredArtifacts(
        string databasePath)
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        var rawIds = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM artifacts ORDER BY id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetValue(0) is not string rawId)
                {
                    throw SqliteStoreSupport.InvalidStoredData("artifact");
                }

                rawIds.Add(rawId);
            }
        }

        var artifacts = new List<StoredArtifact>(rawIds.Count);
        foreach (string rawId in rawIds)
        {
            if (!EntityId<ArtifactIdTag>.TryParse(
                    rawId,
                    out EntityId<ArtifactIdTag>? artifactId) ||
                !string.Equals(rawId, artifactId.ToString(), StringComparison.Ordinal))
            {
                throw SqliteStoreSupport.InvalidStoredData("artifact");
            }

            StoredArtifact artifact = ArtifactStore.ReadArtifact(
                    connection,
                    transaction,
                    artifactId)
                ?? throw SqliteStoreSupport.InvalidStoredData("artifact");
            if (!BackupManifest.IsIncludedKind(artifact.Kind))
            {
                throw SqliteStoreSupport.InvalidStoredData("artifact");
            }

            artifacts.Add(artifact);
        }

        transaction.Commit();
        return Array.AsReadOnly(artifacts.ToArray());
    }

    private static void ValidateDatabaseCatalog(
        string databasePath,
        BackupManifest manifest)
    {
        IReadOnlyList<StoredArtifact> stored = ReadStoredArtifacts(databasePath);
        if (stored.Count != manifest.Artifacts.Count)
        {
            throw new InvalidOperationException(
                "The backup artifact catalog is inconsistent.");
        }

        for (int index = 0; index < stored.Count; index++)
        {
            StoredArtifact artifact = stored[index];
            BackupManifestArtifact expected = manifest.Artifacts[index];
            if (artifact.Id != expected.Id ||
                artifact.ProjectId != expected.ProjectId ||
                artifact.Kind != expected.Kind ||
                !string.Equals(
                    artifact.StorageReference,
                    expected.SourceStorageReference,
                    StringComparison.Ordinal) ||
                artifact.ContentDigest != expected.Sha256 ||
                artifact.ByteSize != expected.ByteSize ||
                artifact.CreatedAt.ToUniversalTime() != expected.CreatedAt)
            {
                throw new InvalidOperationException(
                    "The backup artifact catalog is inconsistent.");
            }
        }

        using SqliteConnection connection = OpenRawConnection(
            databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand excluded = connection.CreateCommand();
        excluded.CommandText =
            "SELECT COUNT(*) FROM artifacts WHERE kind IN ('Proxy', 'Preview');";
        if (excluded.ExecuteScalar() is not long excludedCount || excludedCount != 0)
        {
            throw new InvalidOperationException(
                "The backup contains excluded artifacts.");
        }
    }

    private static async Task ValidateBackupPayloadAsync(
        string backupDirectory,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        string databasePath = ResolveRequiredRelativePath(
            backupDirectory,
            manifest.Database.RelativePath);
        FileSnapshot database = await ReadStableSnapshotAsync(
            backupDirectory,
            databasePath,
            FileShare.Read,
            cancellationToken).ConfigureAwait(false);
        EnsureSnapshotMatches(
            database,
            manifest.Database.ByteSize,
            manifest.Database.Sha256);

        foreach (BackupManifestArtifact artifact in manifest.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = ResolveRequiredRelativePath(
                backupDirectory,
                artifact.BackupRelativePath);
            FileSnapshot snapshot = await ReadStableSnapshotAsync(
                backupDirectory,
                path,
                FileShare.Read,
                cancellationToken).ConfigureAwait(false);
            EnsureSnapshotMatches(snapshot, artifact.ByteSize, artifact.Sha256);
        }
    }

    private static async Task CopyVerifiedFileAsync(
        string sourceRoot,
        string sourcePath,
        string destinationRoot,
        string destinationPath,
        long expectedByteSize,
        ContentDigest expectedDigest,
        CancellationToken cancellationToken)
    {
        WorkspacePathSafety.ValidateExistingPathChain(
            sourceRoot,
            sourcePath,
            targetIsDirectory: false);
        string destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new IOException("The destination directory is unavailable.");
        CreateDirectoryChain(destinationRoot, destinationDirectory);
        EnsurePathAbsent(destinationPath);

        var sourceOptions = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        var destinationOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        using FileStream source = new(sourcePath, sourceOptions);
        ValidateOpenedRegularFile(source, requireReadable: true);
        if (source.Length != expectedByteSize)
        {
            throw new InvalidDataException("The source file size is invalid.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
        try
        {
            await using (var destination = new FileStream(
                destinationPath,
                destinationOptions))
            {
                while (true)
                {
                    int read = await source.ReadAsync(
                        buffer.AsMemory(0, FileBufferSize),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateOpenedRegularFile(source, requireReadable: true);
        if (source.Length != expectedByteSize ||
            !FixedTimeEquals(
                ContentDigest.FromBytes(hash.GetHashAndReset()),
                expectedDigest))
        {
            throw new InvalidDataException("The source file content is invalid.");
        }

        WorkspacePathSafety.ValidateExistingPathChain(
            sourceRoot,
            sourcePath,
            targetIsDirectory: false);
        FileSnapshot copied = await ReadStableSnapshotAsync(
            destinationRoot,
            destinationPath,
            FileShare.Read,
            cancellationToken).ConfigureAwait(false);
        EnsureSnapshotMatches(copied, expectedByteSize, expectedDigest);
    }

    private static async Task<FileSnapshot> ReadStableSnapshotAsync(
        string rootPath,
        string filePath,
        FileShare share,
        CancellationToken cancellationToken)
    {
        WorkspacePathSafety.ValidateExistingPathChain(
            rootPath,
            filePath,
            targetIsDirectory: false);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = share,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        using FileStream stream = new(filePath, options);
        ValidateOpenedRegularFile(stream, requireReadable: true);
        long initialLength = stream.Length;
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOpenedRegularFile(stream, requireReadable: true);
        if (stream.Length != initialLength)
        {
            throw new InvalidDataException("The file changed during verification.");
        }

        WorkspacePathSafety.ValidateExistingPathChain(
            rootPath,
            filePath,
            targetIsDirectory: false);
        return new FileSnapshot(initialLength, ContentDigest.FromBytes(digest));
    }

    private static async Task<byte[]> ReadFileBytesAsync(
        string rootPath,
        string filePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        WorkspacePathSafety.ValidateExistingPathChain(
            rootPath,
            filePath,
            targetIsDirectory: false);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        using FileStream stream = new(filePath, options);
        ValidateOpenedRegularFile(stream, requireReadable: true);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The file length is invalid.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        ValidateOpenedRegularFile(stream, requireReadable: true);
        if (stream.Length != bytes.LongLength)
        {
            throw new InvalidDataException("The file changed during verification.");
        }

        WorkspacePathSafety.ValidateExistingPathChain(
            rootPath,
            filePath,
            targetIsDirectory: false);
        return bytes;
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateExactTree(
        string rootPath,
        IEnumerable<string> expectedRelativeFiles)
    {
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (string relativePath in expectedRelativeFiles)
        {
            if (!WorkspacePathSafety.IsValidPortableRelativePath(relativePath) ||
                !expectedFiles.Add(relativePath))
            {
                throw new InvalidDataException("The expected backup tree is invalid.");
            }

            int separator = relativePath.LastIndexOf('/');
            while (separator > 0)
            {
                string directory = relativePath[..separator];
                expectedDirectories.Add(directory);
                separator = directory.LastIndexOf('/');
            }
        }

        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            WorkspacePathSafety.ValidateExistingPathChain(
                rootPath,
                directory,
                targetIsDirectory: true);
            string[] entries = Directory
                .EnumerateFileSystemEntries(
                    directory,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0,
                    })
                .OrderBy(static entry => entry, WorkspacePathSafety.PathComparer)
                .ToArray();
            foreach (string entry in entries)
            {
                FileAttributes attributes = WorkspacePathSafety.GetRequiredAttributes(entry);
                if (!WorkspacePathSafety.TryGetPortableRelativePath(
                        rootPath,
                        entry,
                        out string relativePath))
                {
                    throw new InvalidDataException("The backup tree path is invalid.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
                    if (!expectedDirectories.Contains(relativePath) ||
                        !actualDirectories.Add(relativePath))
                    {
                        throw new InvalidDataException("The backup tree is inconsistent.");
                    }

                    pending.Push(entry);
                }
                else
                {
                    WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
                    if (!expectedFiles.Contains(relativePath) ||
                        !actualFiles.Add(relativePath))
                    {
                        throw new InvalidDataException("The backup tree is inconsistent.");
                    }
                }
            }

            WorkspacePathSafety.ValidateExistingPathChain(
                rootPath,
                directory,
                targetIsDirectory: true);
        }

        if (!expectedFiles.SetEquals(actualFiles) ||
            !expectedDirectories.SetEquals(actualDirectories))
        {
            throw new InvalidDataException("The backup tree is incomplete.");
        }
    }

    private static void CreateDirectoryChain(
        string rootPath,
        string destinationDirectory)
    {
        if (string.Equals(
                rootPath,
                destinationDirectory,
                WorkspacePathSafety.PathComparison))
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                rootPath,
                rootPath,
                targetIsDirectory: true);
            return;
        }

        if (!WorkspacePathSafety.IsStrictDescendant(rootPath, destinationDirectory) ||
            !WorkspacePathSafety.HasSameWindowsDriveRoot(rootPath, destinationDirectory))
        {
            throw new IOException("The destination directory is unsafe.");
        }

        string relative = Path.GetRelativePath(rootPath, destinationDirectory);
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = rootPath;
        WorkspacePathSafety.ValidateExistingPathChain(
            rootPath,
            current,
            targetIsDirectory: true);
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!WorkspacePathSafety.TryGetAttributes(current, out _))
            {
                Directory.CreateDirectory(current);
            }

            WorkspacePathSafety.ValidateExistingPathChain(
                rootPath,
                current,
                targetIsDirectory: true);
        }

        if (!string.Equals(
                current,
                destinationDirectory,
                WorkspacePathSafety.PathComparison))
        {
            throw new IOException("The destination directory is unsafe.");
        }
    }

    private static SqliteConnection OpenRawConnection(
        string databasePath,
        SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = DefaultTimeoutSeconds,
        };
        var connection = new SqliteConnection(builder.ToString())
        {
            DefaultTimeout = DefaultTimeoutSeconds,
        };
        try
        {
            connection.Open();
            using SqliteCommand foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string[] GetBackupPayloadPaths(BackupManifest manifest) =>
    [
        manifest.Database.RelativePath,
        .. manifest.Artifacts.Select(static artifact => artifact.BackupRelativePath),
    ];

    private string GetWorkspaceParent() =>
        Path.GetDirectoryName(_workspacePath)
        ?? throw Failure(
            BackupServiceFailureReason.RestoreValidationFailed,
            reconciliationRequired: true);

    private static string CreateRestoreDirectoryName(
        EntityId<BackupIdTag> backupId) =>
        string.Concat(
            RestoreDirectoryPrefix,
            backupId.ToString(),
            "-",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private static bool TryParseRestoreDirectoryName(
        string directoryName,
        [NotNullWhen(true)] out EntityId<BackupIdTag>? backupId)
    {
        backupId = null;
        const int entityIdLength = 36;
        const int attemptIdLength = 32;
        int expectedLength = RestoreDirectoryPrefix.Length +
            entityIdLength + 1 + attemptIdLength;
        if (directoryName.Length != expectedLength ||
            !directoryName.StartsWith(
                RestoreDirectoryPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        string backupIdText = directoryName.Substring(
            RestoreDirectoryPrefix.Length,
            entityIdLength);
        if (!EntityId<BackupIdTag>.TryParse(
                backupIdText,
                out EntityId<BackupIdTag>? parsedBackupId) ||
            !string.Equals(
                backupIdText,
                parsedBackupId.ToString(),
                StringComparison.Ordinal) ||
            directoryName[RestoreDirectoryPrefix.Length + entityIdLength] != '-')
        {
            return false;
        }

        string attemptIdText = directoryName[^attemptIdLength..];
        if (!Guid.TryParseExact(attemptIdText, "N", out Guid attemptId) ||
            attemptId == Guid.Empty ||
            !string.Equals(
                attemptIdText,
                attemptId.ToString("N", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return false;
        }

        backupId = parsedBackupId;
        return true;
    }

    private static string ResolveRequiredRelativePath(
        string rootPath,
        string relativePath)
    {
        if (!WorkspacePathSafety.TryResolvePortableRelativePath(
                rootPath,
                relativePath,
                out string fullPath))
        {
            throw new InvalidDataException("The relative path is invalid.");
        }

        return fullPath;
    }

    private static void EnsureSnapshotMatches(
        FileSnapshot snapshot,
        long expectedByteSize,
        ContentDigest expectedDigest)
    {
        if (snapshot.ByteSize != expectedByteSize ||
            !FixedTimeEquals(snapshot.Sha256, expectedDigest))
        {
            throw new InvalidDataException("The file verification failed.");
        }
    }

    private static void ValidateOpenedRegularFile(
        FileStream stream,
        bool requireReadable)
    {
        FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
        WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        if (!stream.CanSeek || (requireReadable && !stream.CanRead))
        {
            throw new InvalidDataException("The file handle is invalid.");
        }
    }

    private static void EnsureDirectoryEmpty(string path)
    {
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException("The staging directory is not empty.");
        }
    }

    private static void EnsurePathAbsent(string path)
    {
        if (WorkspacePathSafety.PathEntryExistsConservatively(path))
        {
            throw new IOException("The destination path already exists.");
        }
    }

    private static bool TryMoveDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            if (!WorkspacePathSafety.PathEntryExistsConservatively(sourcePath) ||
                WorkspacePathSafety.PathEntryExistsConservatively(destinationPath))
            {
                return false;
            }

            Directory.Move(sourcePath, destinationPath);
            return !WorkspacePathSafety.PathEntryExistsConservatively(sourcePath) &&
                WorkspacePathSafety.PathEntryExistsConservatively(destinationPath);
        }
        catch (Exception exception) when (IsCutoverFailure(exception))
        {
            return false;
        }
    }

    private static bool HasSameMetadata(
        StoredArtifact expected,
        StoredArtifact actual) =>
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

    private static bool IsSameOrDescendant(string rootPath, string candidatePath) =>
        string.Equals(
            rootPath,
            candidatePath,
            WorkspacePathSafety.PathComparison) ||
        WorkspacePathSafety.IsStrictDescendant(rootPath, candidatePath);

    private static bool FixedTimeEquals(
        ContentDigest left,
        ContentDigest right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left.Hex),
            Convert.FromHexString(right.Hex));

    private static bool IsOperationalFailure(Exception exception) =>
        exception is InvalidDataException or InvalidOperationException or
            SqliteException or IOException or UnauthorizedAccessException or
            SecurityException or NotSupportedException or ArgumentException or
            CryptographicException or WorkspacePathSafetyException;

    private static bool IsCutoverFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or InvalidOperationException or WorkspaceAclException or
            WorkspacePathSafetyException;

    private static BackupServiceException Failure(
        BackupServiceFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record FileSnapshot(long ByteSize, ContentDigest Sha256);
}