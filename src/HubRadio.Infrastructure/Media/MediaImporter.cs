using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Media;

public enum MediaImportFailureReason
{
    CandidateUnavailable,
    UnsafeSource,
    SourceMissing,
    SourceNotRegularFile,
    SourceReparsePoint,
    EmptySource,
    DestinationExists,
    CopyFailed,
    PartialCleanupFailed,
}

public sealed class MediaImportException : Exception
{
    internal MediaImportException(MediaImportFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        RecoveryRequired = reason == MediaImportFailureReason.PartialCleanupFailed;
    }

    public MediaImportFailureReason Reason { get; }

    public bool RecoveryRequired { get; }

    private static string GetStableMessage(MediaImportFailureReason reason) =>
        reason switch
        {
            MediaImportFailureReason.CandidateUnavailable =>
                "The media candidate is unavailable.",
            MediaImportFailureReason.UnsafeSource =>
                "The media source is unsafe.",
            MediaImportFailureReason.SourceMissing =>
                "The media source is missing.",
            MediaImportFailureReason.SourceNotRegularFile =>
                "The media source is not a regular file.",
            MediaImportFailureReason.SourceReparsePoint =>
                "The media source path contains a reparse point.",
            MediaImportFailureReason.EmptySource =>
                "The media source is empty.",
            MediaImportFailureReason.DestinationExists =>
                "The media import destination already exists.",
            MediaImportFailureReason.CopyFailed =>
                "The media import copy failed.",
            MediaImportFailureReason.PartialCleanupFailed =>
                "The incomplete media import requires recovery.",
            _ => "The media import failed.",
        };
}

public sealed class StagedMediaImport
{
    internal StagedMediaImport(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        string stagingPath,
        ContentDigest contentDigest,
        long byteSize,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(stagingPath);
        ArgumentNullException.ThrowIfNull(contentDigest);

        if (!IsValidSourceFileName(sourceFileName))
        {
            throw new ArgumentException(
                "The source file name must be a nonblank leaf name without control characters.",
                nameof(sourceFileName));
        }

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                "The staged media byte size must be positive.");
        }

        CandidateId = candidateId;
        ProjectId = projectId;
        JobId = jobId;
        SourceFileName = sourceFileName;
        StagingPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            stagingPath,
            nameof(stagingPath));
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public EntityId<AssetIdTag> CandidateId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<JobIdTag> JobId { get; }

    public string SourceFileName { get; }

    public string StagingPath { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public DateTimeOffset CreatedAt { get; }

    private static bool IsValidSourceFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}

public sealed class MediaImporter
{
    private const int FileBufferSize = 64 * 1024;

    private readonly AssetCandidateRegistry _registry;
    private readonly string _workspacePath;
    private readonly TimeProvider _timeProvider;

    public MediaImporter(AssetCandidateRegistry registry, string workspacePath)
        : this(registry, workspacePath, TimeProvider.System)
    {
    }

    internal MediaImporter(
        AssetCandidateRegistry registry,
        string workspacePath,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _workspacePath = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                workspacePath,
                nameof(workspacePath)));

        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_workspacePath);
        }
        catch (WorkspacePathSafetyException)
        {
            throw new ArgumentException(
                "The workspace must be an existing local directory without reparse points.",
                nameof(workspacePath));
        }

        _registry = registry;
        _timeProvider = timeProvider;
    }

    public async Task<StagedMediaImport> CopyToStagingAsync(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryClaim(candidateId, out string? claimedSourcePath))
        {
            throw Failure(MediaImportFailureReason.CandidateUnavailable);
        }

        ImportPaths? paths = null;
        bool partialCreated = false;
        bool readyCreated = false;

        try
        {
            paths = BuildImportPaths(candidateId, projectId, jobId);
            SourceFile sourceFile = ValidateSource(claimedSourcePath, paths.StagingDirectory);
            ValidateWorkspaceChain();
            EnsureStagingDirectoryChain(paths.StagingDirectory, cancellationToken);
            EnsureDestinationAbsent(paths.PartialPath);
            EnsureDestinationAbsent(paths.ReadyPath);
            cancellationToken.ThrowIfCancellationRequested();

            using FileStream source = OpenSource(sourceFile.Path);
            ValidateSourcePathChain(sourceFile.Path);
            ValidateOpenedSource(source);
            long initialSourceLength = GetStreamLength(source);
            DateTime initialSourceLastWriteTimeUtc = GetLastWriteTimeUtc(source);
            if (initialSourceLength <= 0)
            {
                throw Failure(MediaImportFailureReason.EmptySource);
            }

            byte[] buffer = new byte[FileBufferSize];
            long copiedByteSize = 0;
            byte[] digestBytes;
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                FileStream destination = OpenPartial(paths.PartialPath);
                partialCreated = true;
                try
                {
                    ValidateCreatedDestination(paths.PartialPath, destination);
                    while (true)
                    {
                        int bytesRead = await source.ReadAsync(
                            buffer.AsMemory(),
                            cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        hash.AppendData(buffer.AsSpan(0, bytesRead));
                        await destination.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken).ConfigureAwait(false);
                        copiedByteSize = checked(copiedByteSize + bytesRead);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                    cancellationToken.ThrowIfCancellationRequested();

                    ValidateCreatedDestination(paths.PartialPath, destination);
                    ValidateOpenedSource(source);
                    ValidateSourcePathChain(sourceFile.Path);
                    long finalSourceLength = GetStreamLength(source);
                    DateTime finalSourceLastWriteTimeUtc = GetLastWriteTimeUtc(source);
                    long destinationLength = GetStreamLength(destination);
                    if (initialSourceLength != finalSourceLength ||
                        initialSourceLastWriteTimeUtc != finalSourceLastWriteTimeUtc ||
                        copiedByteSize != initialSourceLength ||
                        destinationLength != copiedByteSize)
                    {
                        throw Failure(MediaImportFailureReason.CopyFailed);
                    }

                    digestBytes = hash.GetHashAndReset();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                    await destination.DisposeAsync().ConfigureAwait(false);
                }
            }

            DateTimeOffset createdAt = _timeProvider.GetUtcNow().ToUniversalTime();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationAbsent(paths.ReadyPath);
            ValidateWorkspaceChain();
            MoveToReady(paths.PartialPath, paths.ReadyPath);
            readyCreated = true;
            partialCreated = false;

            cancellationToken.ThrowIfCancellationRequested();
            ValidateReadyDestination(paths.ReadyPath, copiedByteSize);
            ValidateOpenedSource(source);
            ValidateSourcePathChain(sourceFile.Path);
            if (GetStreamLength(source) != initialSourceLength ||
                GetLastWriteTimeUtc(source) != initialSourceLastWriteTimeUtc)
            {
                throw Failure(MediaImportFailureReason.CopyFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new StagedMediaImport(
                candidateId,
                projectId,
                jobId,
                sourceFile.FileName,
                paths.ReadyPath,
                ContentDigest.FromBytes(digestBytes),
                copiedByteSize,
                createdAt);
        }
        catch (Exception exception)
        {
            if (paths is not null &&
                !TryCleanupCreatedFiles(paths, partialCreated, readyCreated))
            {
                throw Failure(MediaImportFailureReason.PartialCleanupFailed);
            }

            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (exception is MediaImportException)
            {
                throw;
            }

            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private ImportPaths BuildImportPaths(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId)
    {
        string stagingDirectory = Path.GetFullPath(Path.Combine(
            ProjectFolders.GetPath(_workspacePath, projectId),
            "staging",
            "imports",
            jobId.ToString()));
        if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, stagingDirectory) ||
            !WorkspacePathSafety.HasSameWindowsDriveRoot(_workspacePath, stagingDirectory))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }

        string fileName = candidateId.ToString();
        return new ImportPaths(
            stagingDirectory,
            Path.Combine(stagingDirectory, string.Concat(fileName, ".partial")),
            Path.Combine(stagingDirectory, string.Concat(fileName, ".ready")));
    }

    private static SourceFile ValidateSource(
        string claimedSourcePath,
        string stagingDirectory)
    {
        string sourcePath;
        try
        {
            sourcePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
                claimedSourcePath,
                nameof(claimedSourcePath));
        }
        catch (ArgumentException)
        {
            throw Failure(MediaImportFailureReason.UnsafeSource);
        }

        ValidateSourcePathChain(sourcePath);
        if (string.Equals(
                sourcePath,
                stagingDirectory,
                WorkspacePathSafety.PathComparison) ||
            WorkspacePathSafety.IsStrictDescendant(stagingDirectory, sourcePath))
        {
            throw Failure(MediaImportFailureReason.UnsafeSource);
        }

        string fileName;
        try
        {
            fileName = Path.GetFileName(sourcePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(MediaImportFailureReason.UnsafeSource);
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains('\\', StringComparison.Ordinal) ||
            fileName.Any(char.IsControl))
        {
            throw Failure(MediaImportFailureReason.UnsafeSource);
        }

        return new SourceFile(sourcePath, fileName);
    }

    private static void ValidateSourcePathChain(string sourcePath)
    {
        string? volumeRoot = Path.GetPathRoot(sourcePath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            throw Failure(MediaImportFailureReason.UnsafeSource);
        }

        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                volumeRoot,
                sourcePath,
                targetIsDirectory: false);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(exception.Failure switch
            {
                WorkspacePathSafetyFailure.Missing =>
                    MediaImportFailureReason.SourceMissing,
                WorkspacePathSafetyFailure.NotRegularFile =>
                    MediaImportFailureReason.SourceNotRegularFile,
                WorkspacePathSafetyFailure.ReparsePoint =>
                    MediaImportFailureReason.SourceReparsePoint,
                _ => MediaImportFailureReason.UnsafeSource,
            });
        }
    }

    private void ValidateWorkspaceChain()
    {
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_workspacePath);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private void EnsureStagingDirectoryChain(
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, stagingDirectory) ||
            !WorkspacePathSafety.HasSameWindowsDriveRoot(_workspacePath, stagingDirectory))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }

        string relativePath = Path.GetRelativePath(_workspacePath, stagingDirectory);
        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = _workspacePath;

        ValidateExistingWorkspaceDirectory(current);
        foreach (string segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateWorkspaceChain();
            ValidateExistingWorkspaceDirectory(current);
            current = Path.Combine(current, segment);

            bool exists;
            try
            {
                exists = WorkspacePathSafety.TryGetAttributes(
                    current,
                    out FileAttributes attributes);
                if (exists)
                {
                    WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
                }
            }
            catch (WorkspacePathSafetyException)
            {
                throw Failure(MediaImportFailureReason.CopyFailed);
            }

            if (!exists)
            {
                try
                {
                    _ = Directory.CreateDirectory(current);
                }
                catch (Exception exception)
                    when (IsFileSystemFailure(exception))
                {
                    throw Failure(MediaImportFailureReason.CopyFailed);
                }
            }

            ValidateWorkspaceChain();
            ValidateExistingWorkspaceDirectory(current);
        }

        if (!string.Equals(
                current,
                stagingDirectory,
                WorkspacePathSafety.PathComparison))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private void ValidateExistingWorkspaceDirectory(string directoryPath)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                directoryPath,
                targetIsDirectory: true);
        }
        catch (WorkspacePathSafetyException)
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private static void EnsureDestinationAbsent(string path)
    {
        if (WorkspacePathSafety.PathEntryExistsConservatively(path))
        {
            throw Failure(MediaImportFailureReason.DestinationExists);
        }
    }

    private static FileStream OpenSource(string sourcePath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            return new FileStream(sourcePath, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Failure(MediaImportFailureReason.SourceMissing);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private static FileStream OpenPartial(string partialPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough,
        };

        try
        {
            return new FileStream(partialPath, options);
        }
        catch (IOException)
        {
            throw Failure(
                WorkspacePathSafety.PathEntryExistsConservatively(partialPath)
                    ? MediaImportFailureReason.DestinationExists
                    : MediaImportFailureReason.CopyFailed);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private void ValidateCreatedDestination(string path, FileStream stream)
    {
        try
        {
            ValidateWorkspaceChain();
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            WorkspacePathSafety.ValidateRegularFileAttributes(
                File.GetAttributes(stream.SafeFileHandle));
            if (!stream.CanWrite || !stream.CanSeek)
            {
                throw Failure(MediaImportFailureReason.CopyFailed);
            }
        }
        catch (MediaImportException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException || IsFileSystemFailure(exception))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private static void ValidateOpenedSource(FileStream stream)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw Failure(MediaImportFailureReason.SourceNotRegularFile);
            }
        }
        catch (MediaImportException)
        {
            throw;
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(exception.Failure == WorkspacePathSafetyFailure.ReparsePoint
                ? MediaImportFailureReason.SourceReparsePoint
                : MediaImportFailureReason.SourceNotRegularFile);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private static long GetStreamLength(FileStream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private static DateTime GetLastWriteTimeUtc(FileStream stream)
    {
        try
        {
            return File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private static void MoveToReady(string partialPath, string readyPath)
    {
        try
        {
            File.Move(partialPath, readyPath, overwrite: false);
        }
        catch (IOException)
        {
            throw Failure(
                WorkspacePathSafety.PathEntryExistsConservatively(readyPath)
                    ? MediaImportFailureReason.DestinationExists
                    : MediaImportFailureReason.CopyFailed);
        }
        catch (Exception exception)
            when (IsFileSystemFailure(exception))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private void ValidateReadyDestination(string readyPath, long expectedLength)
    {
        try
        {
            ValidateWorkspaceChain();
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                readyPath,
                targetIsDirectory: false);
            var options = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 1,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            using FileStream stream = new(readyPath, options);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                readyPath,
                targetIsDirectory: false);
            WorkspacePathSafety.ValidateRegularFileAttributes(
                File.GetAttributes(stream.SafeFileHandle));
            if (!stream.CanRead || !stream.CanSeek ||
                GetStreamLength(stream) != expectedLength)
            {
                throw Failure(MediaImportFailureReason.CopyFailed);
            }
        }
        catch (MediaImportException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is WorkspacePathSafetyException || IsFileSystemFailure(exception))
        {
            throw Failure(MediaImportFailureReason.CopyFailed);
        }
    }

    private bool TryCleanupCreatedFiles(
        ImportPaths paths,
        bool partialCreated,
        bool readyCreated)
    {
        bool cleaned = true;
        if (readyCreated)
        {
            cleaned &= TryDeleteCreatedFile(paths.ReadyPath);
        }

        if (partialCreated)
        {
            cleaned &= TryDeleteCreatedFile(paths.PartialPath);
        }

        return cleaned;
    }

    private bool TryDeleteCreatedFile(string path)
    {
        try
        {
            if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, path) ||
                !WorkspacePathSafety.HasSameWindowsDriveRoot(_workspacePath, path))
            {
                return false;
            }

            ValidateWorkspaceChain();
            if (!WorkspacePathSafety.TryGetAttributes(path, out FileAttributes attributes))
            {
                return true;
            }

            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            File.Delete(path);
            return !WorkspacePathSafety.PathEntryExistsConservatively(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException;

    private static MediaImportException Failure(MediaImportFailureReason reason) =>
        new(reason);

    private sealed record ImportPaths(
        string StagingDirectory,
        string PartialPath,
        string ReadyPath);

    private sealed record SourceFile(string Path, string FileName);
}