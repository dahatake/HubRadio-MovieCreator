using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Files;

public sealed class ArtifactIdTag
{
}

public enum ArtifactKind
{
    Original,
    Proxy,
    Analysis,
    Composition,
    Preview,
    Render,
    Delivery,
    Evidence,
}

public enum ArtifactStoreFailureReason
{
    StagingMissing,
    UnsafePath,
    NotRegularFile,
    ReparsePoint,
    SizeMismatch,
    DigestMismatch,
    DestinationExists,
    DatabaseRejected,
    RollbackFailed,
}

public sealed class ArtifactStoreException : Exception
{
    internal ArtifactStoreException(
        ArtifactStoreFailureReason reason,
        bool reconciliationRequired)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public ArtifactStoreFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(ArtifactStoreFailureReason reason) =>
        reason switch
        {
            ArtifactStoreFailureReason.StagingMissing =>
                "The staging artifact is missing.",
            ArtifactStoreFailureReason.UnsafePath =>
                "The artifact path is unsafe.",
            ArtifactStoreFailureReason.NotRegularFile =>
                "The staging artifact is not a regular file.",
            ArtifactStoreFailureReason.ReparsePoint =>
                "The artifact path contains a reparse point.",
            ArtifactStoreFailureReason.SizeMismatch =>
                "The artifact size does not match the expected value.",
            ArtifactStoreFailureReason.DigestMismatch =>
                "The artifact digest does not match the expected value.",
            ArtifactStoreFailureReason.DestinationExists =>
                "The artifact destination already exists.",
            ArtifactStoreFailureReason.DatabaseRejected =>
                "The artifact database operation was rejected.",
            ArtifactStoreFailureReason.RollbackFailed =>
                "The artifact promotion requires reconciliation.",
            _ => "The artifact operation failed.",
        };
}

public sealed class ArtifactPromotionRequest
{
    public ArtifactPromotionRequest(
        EntityId<ArtifactIdTag> id,
        EntityId<ProjectIdTag> projectId,
        ArtifactKind kind,
        string stagingPath,
        string relativePath,
        ContentDigest expectedDigest,
        long expectedByteSize,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(stagingPath);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(expectedDigest);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The artifact kind must be defined.");
        }

        if (!IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "The artifact relative path is invalid.",
                nameof(relativePath));
        }

        if (expectedByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedByteSize),
                "The expected artifact byte size must be positive.");
        }

        _ = SqliteStoreSupport.FormatTimestamp(createdAt, nameof(createdAt));

        Id = id;
        ProjectId = projectId;
        Kind = kind;
        StagingPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            stagingPath,
            nameof(stagingPath));
        RelativePath = relativePath;
        ExpectedDigest = expectedDigest;
        ExpectedByteSize = expectedByteSize;
        CreatedAt = createdAt;
    }

    public EntityId<ArtifactIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ArtifactKind Kind { get; }

    public string StagingPath { get; }

    public string RelativePath { get; }

    public ContentDigest ExpectedDigest { get; }

    public long ExpectedByteSize { get; }

    public DateTimeOffset CreatedAt { get; }

    internal static bool IsValidRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment[^1] is '.' or ' ' ||
                HasInvalidWindowsFileNameCharacter(segment) ||
                IsReservedWindowsDeviceSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasInvalidWindowsFileNameCharacter(string segment) =>
        segment.Any(static character => character is '<' or '>' or '"' or '|' or '?' or '*');

    private static bool IsReservedWindowsDeviceSegment(string segment)
    {
        int extensionIndex = segment.IndexOf('.');
        string deviceName = extensionIndex < 0
            ? segment
            : segment[..extensionIndex];

        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (deviceName.Length != 4 ||
            (!deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                !deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return deviceName[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }
}

public sealed class StoredArtifact
{
    internal StoredArtifact(
        EntityId<ArtifactIdTag> id,
        EntityId<ProjectIdTag> projectId,
        ArtifactKind kind,
        string storageReference,
        ContentDigest contentDigest,
        long byteSize,
        DateTimeOffset createdAt)
    {
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        StorageReference = storageReference;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        CreatedAt = createdAt;
    }

    public EntityId<ArtifactIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ArtifactKind Kind { get; }

    public string StorageReference { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public DateTimeOffset CreatedAt { get; }
}

public sealed class ArtifactStore
{
    private const int FileBufferSize = 64 * 1024;
    private const int Sha256ByteLength = 32;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly string _databasePath;
    private readonly string _workspacePath;

    public ArtifactStore(string databasePath, string workspacePath)
    {
        try
        {
            _databasePath = SqliteStoreSupport.Initialize(databasePath);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsDatabaseFailure(exception))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }

        _workspacePath = Path.TrimEndingDirectorySeparator(
            SqliteStoreSupport.ValidateLocalAbsolutePath(
                workspacePath,
                nameof(workspacePath)));
        ValidateWorkspaceChain(_workspacePath);
    }

    public async Task<StoredArtifact> PromoteAsync(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        PromotionPaths paths = BuildPromotionPaths(request);
        ValidateWorkspaceChain(_workspacePath);
        ValidateStagingPath(paths.StagingPath, paths.StagingRoot);
        cancellationToken.ThrowIfCancellationRequested();

        using FileStream stream = OpenStagingFile(paths.StagingPath);
        ValidateStagingPath(paths.StagingPath, paths.StagingRoot);
        ValidateOpenedFile(stream);
        ContentDigest actualDigest = await VerifyContentAsync(
            stream,
            request,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string createdAt = SqliteStoreSupport.FormatTimestamp(
            request.CreatedAt,
            nameof(request));
        bool moved = false;
        bool committed = false;

        try
        {
            using SqliteConnection connection =
                SqliteStoreSupport.OpenConnection(_databasePath);
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);

            EnsureProjectExists(connection, transaction, request.ProjectId);
            EnsureStorageReferenceAbsent(
                connection,
                transaction,
                request.ProjectId,
                paths.StorageReference);
            EnsureDestinationDirectoryChain(
                paths.DestinationDirectory,
                createMissing: true,
                cancellationToken);
            EnsurePathAbsent(paths.DestinationPath);
            ValidateStagingPath(paths.StagingPath, paths.StagingRoot);
            ValidateOpenedFile(stream);
            cancellationToken.ThrowIfCancellationRequested();

            MoveToDestination(paths.StagingPath, paths.DestinationPath);
            moved = true;

            cancellationToken.ThrowIfCancellationRequested();
            EnsureDestinationDirectoryChain(
                paths.DestinationDirectory,
                createMissing: false,
                cancellationToken);
            ValidateOpenedFile(stream);
            if (GetOpenedFileLength(stream) != request.ExpectedByteSize)
            {
                throw Failure(ArtifactStoreFailureReason.SizeMismatch);
            }

            ValidatePromotedDestination(paths.StagingPath, paths.DestinationPath);
            InsertArtifact(
                connection,
                transaction,
                request,
                paths.StorageReference,
                actualDigest,
                createdAt);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            committed = true;

            return new StoredArtifact(
                request.Id,
                request.ProjectId,
                request.Kind,
                paths.StorageReference,
                actualDigest,
                request.ExpectedByteSize,
                request.CreatedAt.ToUniversalTime());
        }
        catch (Exception exception)
        {
            if (moved && !committed)
            {
                if (!TryRollbackMove(paths.DestinationPath, paths.StagingPath))
                {
                    throw Failure(
                        ArtifactStoreFailureReason.RollbackFailed,
                        reconciliationRequired: true);
                }

                if (exception is ArtifactStoreException or OperationCanceledException)
                {
                    throw;
                }

                throw Failure(ClassifyFailure(exception));
            }

            if (exception is ArtifactStoreException or OperationCanceledException)
            {
                throw;
            }

            throw Failure(ClassifyFailure(exception));
        }
    }

    public StoredArtifact? GetArtifact(EntityId<ArtifactIdTag> artifactId)
    {
        ArgumentNullException.ThrowIfNull(artifactId);

        try
        {
            using SqliteConnection connection =
                SqliteStoreSupport.OpenConnection(_databasePath);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    project_id,
                    kind,
                    storage_reference,
                    content_digest,
                    byte_size,
                    created_at
                FROM artifacts
                WHERE id = $id;
                """;
            SqliteStoreSupport.AddText(command, "$id", artifactId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            StoredArtifact stored = ReadStoredArtifact(reader);
            if (reader.Read() || stored.Id != artifactId)
            {
                throw SqliteStoreSupport.InvalidStoredData("artifact");
            }

            return stored;
        }
        catch (Exception exception)
            when (IsDatabaseFailure(exception))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }
    }

    public IReadOnlyList<StoredArtifact> ListProjectArtifacts(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);

        try
        {
            using SqliteConnection connection =
                SqliteStoreSupport.OpenConnection(_databasePath);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    project_id,
                    kind,
                    storage_reference,
                    content_digest,
                    byte_size,
                    created_at
                FROM artifacts
                WHERE project_id = $project_id
                ORDER BY created_at, id;
                """;
            SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
            using SqliteDataReader reader = command.ExecuteReader();
            var artifacts = new List<StoredArtifact>();
            while (reader.Read())
            {
                StoredArtifact artifact = ReadStoredArtifact(reader);
                if (artifact.ProjectId != projectId)
                {
                    throw SqliteStoreSupport.InvalidStoredData("artifact");
                }

                artifacts.Add(artifact);
            }

            return Array.AsReadOnly(artifacts.ToArray());
        }
        catch (Exception exception)
            when (IsDatabaseFailure(exception))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }
    }

    private PromotionPaths BuildPromotionPaths(ArtifactPromotionRequest request)
    {
        string projectId = request.ProjectId.ToString();
        string folder = GetKindFolder(request.Kind);
        string stagingRoot = Path.GetFullPath(
            Path.Combine(_workspacePath, "projects", projectId, "staging"));
        if (!IsStrictDescendant(stagingRoot, request.StagingPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        string kindRoot = Path.GetFullPath(
            Path.Combine(_workspacePath, "projects", projectId, folder));
        string destinationPath;
        try
        {
            destinationPath = Path.GetFullPath(
                Path.Combine(
                    kindRoot,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        if (!IsStrictDescendant(kindRoot, destinationPath) ||
            !HasSameWindowsDriveRoot(_workspacePath, request.StagingPath) ||
            !HasSameWindowsDriveRoot(_workspacePath, destinationPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        string destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw Failure(ArtifactStoreFailureReason.UnsafePath);
        string storageReference = string.Concat(
            "projects/",
            projectId,
            "/",
            folder,
            "/",
            request.RelativePath);
        return new PromotionPaths(
            request.StagingPath,
            stagingRoot,
            destinationPath,
            destinationDirectory,
            storageReference);
    }

    private static void ValidateWorkspaceChain(string workspacePath)
    {
        string? current = workspacePath;
        while (current is not null)
        {
            FileAttributes attributes = GetRequiredAttributes(
                current,
                ArtifactStoreFailureReason.UnsafePath);
            ValidateDirectoryAttributes(attributes);

            DirectoryInfo? parent;
            try
            {
                parent = Directory.GetParent(current);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                throw Failure(ArtifactStoreFailureReason.UnsafePath);
            }

            current = parent?.FullName;
        }
    }

    private void ValidateStagingPath(string stagingPath, string stagingRoot)
    {
        if (!IsStrictDescendant(stagingRoot, stagingPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        string? current = stagingPath;
        bool isFile = true;
        while (current is not null)
        {
            FileAttributes attributes = GetRequiredAttributes(
                current,
                ArtifactStoreFailureReason.StagingMissing);
            if (isFile)
            {
                ValidateRegularFileAttributes(attributes);
            }
            else
            {
                ValidateDirectoryAttributes(attributes);
            }

            if (string.Equals(current, _workspacePath, PathComparison))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
            isFile = false;
        }

        throw Failure(ArtifactStoreFailureReason.UnsafePath);
    }

    private void EnsureDestinationDirectoryChain(
        string destinationDirectory,
        bool createMissing,
        CancellationToken cancellationToken)
    {
        if (!IsStrictDescendant(_workspacePath, destinationDirectory))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        string relativeDirectory = Path.GetRelativePath(
            _workspacePath,
            destinationDirectory);
        string[] segments = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = _workspacePath;
        ValidateDirectoryAttributes(GetRequiredAttributes(
            current,
            ArtifactStoreFailureReason.UnsafePath));

        foreach (string segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDirectoryAttributes(GetRequiredAttributes(
                current,
                ArtifactStoreFailureReason.UnsafePath));
            current = Path.Combine(current, segment);

            if (TryGetAttributes(current, out FileAttributes attributes))
            {
                ValidateDirectoryAttributes(attributes);
            }
            else if (createMissing)
            {
                try
                {
                    _ = Directory.CreateDirectory(current);
                }
                catch (Exception exception)
                    when (IsFileFailure(exception))
                {
                    throw Failure(ArtifactStoreFailureReason.UnsafePath);
                }
            }
            else
            {
                throw Failure(ArtifactStoreFailureReason.UnsafePath);
            }

            ValidateDirectoryAttributes(GetRequiredAttributes(
                current,
                ArtifactStoreFailureReason.UnsafePath));
        }

        if (!string.Equals(current, destinationDirectory, PathComparison))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }
    }

    private static FileStream OpenStagingFile(string stagingPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            return new FileStream(stagingPath, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Failure(ArtifactStoreFailureReason.StagingMissing);
        }
        catch (Exception exception)
            when (IsFileFailure(exception))
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }
    }

    private static async Task<ContentDigest> VerifyContentAsync(
        FileStream stream,
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken)
    {
        long initialLength = GetOpenedFileLength(stream);
        if (initialLength != request.ExpectedByteSize)
        {
            throw Failure(ArtifactStoreFailureReason.SizeMismatch);
        }

        byte[] actualDigestBytes = new byte[Sha256ByteLength];
        try
        {
            int written = await SHA256.HashDataAsync(
                stream,
                actualDigestBytes,
                cancellationToken).ConfigureAwait(false);
            if (written != Sha256ByteLength)
            {
                throw Failure(ArtifactStoreFailureReason.DigestMismatch);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArtifactStoreException)
        {
            throw;
        }
        catch (Exception exception)
            when (IsFileFailure(exception) || exception is CryptographicException)
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }

        ValidateOpenedFile(stream);
        long finalLength = GetOpenedFileLength(stream);
        if (finalLength != request.ExpectedByteSize)
        {
            throw Failure(ArtifactStoreFailureReason.SizeMismatch);
        }

        byte[] expectedDigestBytes = Convert.FromHexString(request.ExpectedDigest.Hex);
        if (!CryptographicOperations.FixedTimeEquals(
            expectedDigestBytes,
            actualDigestBytes))
        {
            throw Failure(ArtifactStoreFailureReason.DigestMismatch);
        }

        return ContentDigest.FromBytes(actualDigestBytes);
    }

    private static void ValidateOpenedFile(FileStream stream)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(stream.SafeFileHandle);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }

        ValidateRegularFileAttributes(attributes);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }
    }

    private static long GetOpenedFileLength(FileStream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }
    }

    private static void MoveToDestination(string stagingPath, string destinationPath)
    {
        try
        {
            File.Move(stagingPath, destinationPath, overwrite: false);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Failure(ArtifactStoreFailureReason.StagingMissing);
        }
        catch (IOException)
        {
            if (PathEntryExistsWithoutThrowing(destinationPath))
            {
                throw Failure(ArtifactStoreFailureReason.DestinationExists);
            }

            if (!PathEntryExistsWithoutThrowing(stagingPath))
            {
                throw Failure(ArtifactStoreFailureReason.StagingMissing);
            }

            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }
    }

    private static void ValidatePromotedDestination(
        string stagingPath,
        string destinationPath)
    {
        if (PathEntryExistsWithoutThrowing(stagingPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        FileAttributes destinationAttributes = GetRequiredAttributes(
            destinationPath,
            ArtifactStoreFailureReason.UnsafePath);
        ValidateRegularFileAttributes(destinationAttributes);
    }

    private static void EnsureProjectExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM projects
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", projectId.ToString());
        object? value = command.ExecuteScalar();
        if (value is not string storedId ||
            !string.Equals(storedId, projectId.ToString(), StringComparison.Ordinal))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }
    }

    private static void EnsureStorageReferenceAbsent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId,
        string storageReference)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM artifacts
            WHERE project_id = $project_id
              AND storage_reference = $storage_reference
            LIMIT 1;
            """;
        SqliteStoreSupport.AddText(command, "$project_id", projectId.ToString());
        SqliteStoreSupport.AddText(command, "$storage_reference", storageReference);
        if (command.ExecuteScalar() is not null)
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }
    }

    private static void InsertArtifact(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactPromotionRequest request,
        string storageReference,
        ContentDigest contentDigest,
        string createdAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO artifacts (
                id,
                project_id,
                kind,
                storage_reference,
                content_digest,
                byte_size,
                created_at
            )
            VALUES (
                $id,
                $project_id,
                $kind,
                $storage_reference,
                $content_digest,
                $byte_size,
                $created_at
            );
            """;
        SqliteStoreSupport.AddText(command, "$id", request.Id.ToString());
        SqliteStoreSupport.AddText(command, "$project_id", request.ProjectId.ToString());
        SqliteStoreSupport.AddText(command, "$kind", request.Kind.ToString());
        SqliteStoreSupport.AddText(command, "$storage_reference", storageReference);
        SqliteStoreSupport.AddText(command, "$content_digest", contentDigest.Hex);
        SqliteStoreSupport.AddInteger(command, "$byte_size", request.ExpectedByteSize);
        SqliteStoreSupport.AddText(command, "$created_at", createdAt);
        if (command.ExecuteNonQuery() != 1)
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }
    }

    private static StoredArtifact ReadStoredArtifact(SqliteDataReader reader)
    {
        const string entityKind = "artifact";
        EntityId<ArtifactIdTag> id =
            SqliteStoreSupport.ReadEntityId<ArtifactIdTag>(reader, 0, entityKind);
        EntityId<ProjectIdTag> projectId =
            SqliteStoreSupport.ReadEntityId<ProjectIdTag>(reader, 1, entityKind);
        string kindText = SqliteStoreSupport.ReadString(reader, 2, entityKind);
        if (!Enum.TryParse(kindText, ignoreCase: false, out ArtifactKind kind) ||
            !Enum.IsDefined(kind) ||
            !string.Equals(kindText, kind.ToString(), StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        string storageReference = SqliteStoreSupport.ReadString(reader, 3, entityKind);
        string prefix = string.Concat(
            "projects/",
            projectId.ToString(),
            "/",
            GetKindFolder(kind),
            "/");
        if (!storageReference.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        string relativePath = storageReference[prefix.Length..];
        if (!ArtifactPromotionRequest.IsValidRelativePath(relativePath) ||
            !string.Equals(
                storageReference,
                string.Concat(prefix, relativePath),
                StringComparison.Ordinal))
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        ContentDigest contentDigest =
            SqliteStoreSupport.ReadContentDigest(reader, 4, entityKind);
        long byteSize = SqliteStoreSupport.ReadInt64(reader, 5, entityKind);
        if (byteSize <= 0)
        {
            throw SqliteStoreSupport.InvalidStoredData(entityKind);
        }

        DateTimeOffset createdAt =
            SqliteStoreSupport.ReadTimestamp(reader, 6, entityKind);
        return new StoredArtifact(
            id,
            projectId,
            kind,
            storageReference,
            contentDigest,
            byteSize,
            createdAt);
    }

    private static void EnsurePathAbsent(string path)
    {
        if (TryGetAttributes(path, out _))
        {
            throw Failure(ArtifactStoreFailureReason.DestinationExists);
        }
    }

    private static FileAttributes GetRequiredAttributes(
        string path,
        ArtifactStoreFailureReason missingReason)
    {
        if (TryGetAttributes(path, out FileAttributes attributes))
        {
            return attributes;
        }

        throw Failure(missingReason);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception)
            when (IsFileFailure(exception))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }
    }

    private static void ValidateDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(ArtifactStoreFailureReason.ReparsePoint);
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.Device) != 0)
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }
    }

    private static void ValidateRegularFileAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(ArtifactStoreFailureReason.ReparsePoint);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }
    }

    private static bool TryRollbackMove(string destinationPath, string stagingPath)
    {
        try
        {
            if (PathEntryExistsWithoutThrowing(stagingPath) ||
                !PathEntryExistsWithoutThrowing(destinationPath))
            {
                return false;
            }

            File.Move(destinationPath, stagingPath, overwrite: false);
            if (PathEntryExistsWithoutThrowing(destinationPath))
            {
                return false;
            }

            FileAttributes attributes = File.GetAttributes(stagingPath);
            return (attributes &
                (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception)
            when (IsFileFailure(exception))
        {
            return false;
        }
    }

    private static bool PathEntryExistsWithoutThrowing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception)
            when (IsFileFailure(exception))
        {
            return true;
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return false;
        }

        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : string.Concat(root, Path.DirectorySeparatorChar);
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool HasSameWindowsDriveRoot(string first, string second)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string? firstRoot = Path.GetPathRoot(first);
        string? secondRoot = Path.GetPathRoot(second);
        return !string.IsNullOrEmpty(firstRoot) &&
            !string.IsNullOrEmpty(secondRoot) &&
            string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetKindFolder(ArtifactKind kind) =>
        kind switch
        {
            ArtifactKind.Original => "original",
            ArtifactKind.Proxy => "proxy",
            ArtifactKind.Analysis => "analysis",
            ArtifactKind.Composition => "compositions",
            ArtifactKind.Preview => "previews",
            ArtifactKind.Render => "renders",
            ArtifactKind.Delivery => "deliveries",
            ArtifactKind.Evidence => "evidence",
            _ => throw Failure(ArtifactStoreFailureReason.DatabaseRejected),
        };

    private static ArtifactStoreFailureReason ClassifyFailure(Exception exception) =>
        exception switch
        {
            ArtifactStoreException storeException => storeException.Reason,
            OperationCanceledException => ArtifactStoreFailureReason.DatabaseRejected,
            SqliteException => ArtifactStoreFailureReason.DatabaseRejected,
            InvalidOperationException => ArtifactStoreFailureReason.DatabaseRejected,
            FileNotFoundException => ArtifactStoreFailureReason.StagingMissing,
            DirectoryNotFoundException => ArtifactStoreFailureReason.StagingMissing,
            IOException => ArtifactStoreFailureReason.UnsafePath,
            UnauthorizedAccessException => ArtifactStoreFailureReason.UnsafePath,
            SecurityException => ArtifactStoreFailureReason.UnsafePath,
            _ => ArtifactStoreFailureReason.DatabaseRejected,
        };

    private static bool IsDatabaseFailure(Exception exception) =>
        exception is SqliteException or InvalidOperationException or IOException or
            UnauthorizedAccessException or SecurityException;

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException;

    private static ArtifactStoreException Failure(
        ArtifactStoreFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private sealed record PromotionPaths(
        string StagingPath,
        string StagingRoot,
        string DestinationPath,
        string DestinationDirectory,
        string StorageReference);
}