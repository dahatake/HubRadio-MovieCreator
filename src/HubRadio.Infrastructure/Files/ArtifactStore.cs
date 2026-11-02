using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Integrity;
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
    RegisteredArtifactMissing,
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
            ArtifactStoreFailureReason.RegisteredArtifactMissing =>
                "The registered artifact does not exist.",
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
        => WorkspacePathSafety.IsValidPortableRelativePath(relativePath);
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

public sealed class ValidatedArtifactReadStream : Stream
{
    private FileStream? _stream;

    internal ValidatedArtifactReadStream(
        StoredArtifact artifact,
        FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(stream);
        RegisteredArtifact = artifact;
        _stream = stream;
    }

    public EntityId<ArtifactIdTag> Id => RegisteredArtifact.Id;

    public EntityId<ProjectIdTag> ProjectId => RegisteredArtifact.ProjectId;

    public ArtifactKind Kind => RegisteredArtifact.Kind;

    public ContentDigest ContentDigest => RegisteredArtifact.ContentDigest;

    public long ByteSize => RegisteredArtifact.ByteSize;

    internal StoredArtifact RegisteredArtifact { get; }

    public override bool CanRead => _stream?.CanRead == true;

    public override bool CanSeek => _stream?.CanSeek == true;

    public override bool CanWrite => false;

    public override long Length => GetStream().Length;

    public override long Position
    {
        get => GetStream().Position;
        set => GetStream().Position = value;
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        GetStream().Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => GetStream().Read(buffer);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        GetStream().ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        GetStream().ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        GetStream().Seek(offset, origin);

    public override void SetLength(long value) =>
        throw new NotSupportedException("The validated artifact stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The validated artifact stream is read-only.");

    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("The validated artifact stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (disposing)
        {
            stream?.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private FileStream GetStream() =>
        _stream ?? throw new ObjectDisposedException(
            nameof(ValidatedArtifactReadStream));
}

public sealed class ArtifactStore
{
    private const int FileBufferSize = 64 * 1024;
    private const int Sha256ByteLength = 32;

    private static readonly StringComparison PathComparison =
        WorkspacePathSafety.PathComparison;

    private readonly string _databasePath;
    private readonly string _workspacePath;

    internal string LocalDatabasePath => _databasePath;
    internal string LocalWorkspacePath => _workspacePath;

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
            // Directory creation is idempotent file I/O: do it BEFORE taking the database write lock so a slow
            // file system (anti-virus scan) never holds BEGIN IMMEDIATE against heartbeats/progress (E2E-006).
            EnsureDestinationDirectoryChain(
                paths.DestinationDirectory,
                createMissing: true,
                cancellationToken);
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
                createMissing: false,
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

    /// <summary>Prevents a caller's delivery lock from protecting a different store/workspace.</summary>
    internal void RequireLocalBinding(string databasePath, string workspacePath)
    {
        if (!string.Equals(_databasePath, SqliteStoreSupport.ValidateLocalAbsolutePath(databasePath, nameof(databasePath)), PathComparison) ||
            !string.Equals(_workspacePath, Path.TrimEndingDirectorySeparator(
                SqliteStoreSupport.ValidateLocalAbsolutePath(workspacePath, nameof(workspacePath))), PathComparison))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }
        ValidateWorkspaceChain(_workspacePath);
    }

    /// <summary>
    /// Registers ONLY an execution-owned local delivery set already moved out of staging.
    /// The caller holds this database's immediate transaction through validation and commit.
    /// This does not commit, move, overwrite, approve, or accept arbitrary existing paths.
    /// Returned non-delete-shared handles must remain held through the caller's commit.
    /// A failed INSERT leaves filesystem reconciliation to the owner; never retry a move here.
    /// </summary>
    internal async Task<IReadOnlyList<ValidatedArtifactReadStream>> RegisterLocalDeliverySetAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        EntityId<SourceOnlyExecutionIdTag> executionId, EntityId<JobIdTag> rootJobId,
        IReadOnlyList<ArtifactPromotionRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(executionId); ArgumentNullException.ThrowIfNull(rootJobId);
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        requests = requests.ToArray(); // Freeze the batch before any awaited file validation.
        if (!ReferenceEquals(transaction.Connection, connection) ||
            !string.Equals(Path.GetFullPath(connection.DataSource), _databasePath, PathComparison) ||
            requests.Count is < 2 or > 17 || requests.Any(r => r is null) ||
            requests.Select(r => r.Id).Distinct().Count() != requests.Count)
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);

        EntityId<ProjectIdTag> projectId = requests[0].ProjectId;
        string relativeRoot = $"source-only/{executionId}/{rootJobId}";
        string stagingRoot = Path.Combine(ProjectFolders.GetPath(_workspacePath, projectId), "staging",
            $"source-only-delivery-{executionId}-{rootJobId}");
        string deliveryRoot = Path.Combine(ProjectFolders.GetPath(_workspacePath, projectId), "deliveries",
            "source-only", executionId.ToString(), rootJobId.ToString());
        var paths = new List<PromotionPaths>(requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            ArtifactPromotionRequest request = requests[i];
            bool receipt = i == requests.Count - 1;
            string extension = Path.GetExtension(request.RelativePath);
            string name = request.Id + extension;
            string expectedRelative = receipt ? $"source-only/{executionId}/{rootJobId}.delivery-receipt.json" : relativeRoot + "/" + name;
            string expectedStaging = receipt ? Path.Combine(stagingRoot, "receipt.json") : Path.Combine(stagingRoot, "files", name);
            if (request.ProjectId != projectId || request.CreatedAt != requests[0].CreatedAt ||
                request.Kind != (receipt ? ArtifactKind.Evidence : ArtifactKind.Delivery) ||
                (!receipt && extension is not (".mp4" or ".png" or ".srt" or ".vtt")) ||
                request.RelativePath != expectedRelative || !string.Equals(request.StagingPath, expectedStaging, PathComparison))
                throw Failure(ArtifactStoreFailureReason.UnsafePath);
            paths.Add(BuildPromotionPaths(request));
        }
        using (SqliteCommand owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT COUNT(*) FROM source_only_executions WHERE id = $id AND project_id = $project AND root_job_id = $job AND phase = 'Delivering';";
            SqliteStoreSupport.AddText(owner, "$id", executionId.ToString());
            SqliteStoreSupport.AddText(owner, "$project", projectId.ToString());
            SqliteStoreSupport.AddText(owner, "$job", rootJobId.ToString());
            if (owner.ExecuteScalar() is not long count || count != 1)
                throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }

        var opened = new List<ValidatedArtifactReadStream>(requests.Count);
        try
        {
            EnsureProjectExists(connection, transaction, projectId);
            EnsureDestinationDirectoryChain(deliveryRoot, createMissing: false, cancellationToken);
            var expectedFiles = paths.Take(requests.Count - 1).Select(p => p.DestinationPath).ToHashSet(WorkspacePathSafety.PathComparer);
            if (!expectedFiles.SetEquals(Directory.EnumerateFileSystemEntries(deliveryRoot)))
                throw Failure(ArtifactStoreFailureReason.UnsafePath);
            for (int i = 0; i < requests.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArtifactPromotionRequest request = requests[i];
                PromotionPaths path = paths[i];
                if (ReadArtifact(connection, transaction, request.Id) is not null || PathEntryExistsWithoutThrowing(path.StagingPath))
                    throw Failure(ArtifactStoreFailureReason.DestinationExists);
                EnsureStorageReferenceAbsent(connection, transaction, projectId, path.StorageReference);
                ValidateRegisteredArtifactPath(path.DestinationPath);
                FileStream? stream = new(path.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                try
                {
                    ValidateRegisteredArtifactPath(path.DestinationPath);
                    ValidateOpenedFile(stream);
                    await VerifyStoredContentAsync(stream, request.ExpectedDigest, request.ExpectedByteSize, cancellationToken).ConfigureAwait(false);
                    ResetOpenedFile(stream);
                    var artifact = new StoredArtifact(request.Id, projectId, request.Kind, path.StorageReference,
                        request.ExpectedDigest, request.ExpectedByteSize, request.CreatedAt.ToUniversalTime());
                    opened.Add(new ValidatedArtifactReadStream(artifact, stream));
                    stream = null;
                }
                finally { stream?.Dispose(); }
            }
            // Do not insert any row until EVERY final file (including the receipt) was hashed.
            for (int i = 0; i < requests.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InsertArtifact(connection, transaction, requests[i], paths[i].StorageReference, requests[i].ExpectedDigest,
                    SqliteStoreSupport.FormatTimestamp(requests[i].CreatedAt, nameof(requests)));
            }
            foreach (ValidatedArtifactReadStream stream in opened)
            {
                StoredArtifact stored = ReadArtifact(connection, transaction, stream.Id)
                    ?? throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
                if (!HasSameMetadata(stream.RegisteredArtifact, stored))
                    throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
            }
            if (!expectedFiles.SetEquals(Directory.EnumerateFileSystemEntries(deliveryRoot)))
                throw Failure(ArtifactStoreFailureReason.UnsafePath);
            return Array.AsReadOnly(opened.ToArray());
        }
        catch
        {
            foreach (ValidatedArtifactReadStream stream in opened) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public StoredArtifact? GetArtifact(EntityId<ArtifactIdTag> artifactId)
    {
        ArgumentNullException.ThrowIfNull(artifactId);

        try
        {
            using SqliteConnection connection =
                SqliteStoreSupport.OpenConnection(_databasePath);
            return ReadArtifact(connection, transaction: null, artifactId);
        }
        catch (Exception exception)
            when (IsDatabaseFailure(exception))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }
    }

    public async Task<StoredArtifact> ValidateRegisteredAsync(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        cancellationToken.ThrowIfCancellationRequested();

        await using ValidatedArtifactReadStream stream =
            await OpenValidatedReadAsync(artifactId, cancellationToken)
                .ConfigureAwait(false);
        return stream.RegisteredArtifact;
    }

    public async Task<ValidatedArtifactReadStream> OpenValidatedReadAsync(
        EntityId<ArtifactIdTag> artifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        cancellationToken.ThrowIfCancellationRequested();

        StoredArtifact artifact = GetArtifact(artifactId)
            ?? throw Failure(ArtifactStoreFailureReason.RegisteredArtifactMissing);
        string artifactPath = ResolveRegisteredArtifactPath(artifact);
        ValidateRegisteredArtifactPath(artifactPath);
        cancellationToken.ThrowIfCancellationRequested();

        FileStream? stream = null;
        try
        {
            stream = OpenStagingFile(artifactPath);
            ValidateRegisteredArtifactPath(artifactPath);
            ValidateOpenedFile(stream);
            await VerifyStoredContentAsync(
                stream,
                artifact.ContentDigest,
                artifact.ByteSize,
                cancellationToken).ConfigureAwait(false);
            ResetOpenedFile(stream);
            ValidateRegisteredArtifactPath(artifactPath);

            StoredArtifact current = GetArtifact(artifactId)
                ?? throw Failure(
                    ArtifactStoreFailureReason.RegisteredArtifactMissing);
            if (!HasSameMetadata(artifact, current))
            {
                throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
            }

            ValidateOpenedFile(stream);
            ValidatedArtifactReadStream result = new(current, stream);
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal string GetValidatedRegisteredArtifactPath(
        EntityId<ArtifactIdTag> artifactId)
    {
        ArgumentNullException.ThrowIfNull(artifactId);

        StoredArtifact artifact = GetArtifact(artifactId)
            ?? throw Failure(ArtifactStoreFailureReason.RegisteredArtifactMissing);
        string artifactPath = ResolveRegisteredArtifactPath(artifact);
        ValidateRegisteredArtifactPath(artifactPath);

        StoredArtifact current = GetArtifact(artifactId)
            ?? throw Failure(ArtifactStoreFailureReason.RegisteredArtifactMissing);
        if (!HasSameMetadata(artifact, current))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }

        return artifactPath;
    }

    public async Task<BinaryVerificationResult> VerifyRegisteredAsync(
        EntityId<ArtifactIdTag> artifactId,
        BinaryVerifier binaryVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        cancellationToken.ThrowIfCancellationRequested();

        StoredArtifact artifact = GetArtifact(artifactId)
            ?? throw Failure(ArtifactStoreFailureReason.RegisteredArtifactMissing);
        string artifactPath = ResolveRegisteredArtifactPath(artifact);
        ValidateRegisteredArtifactPath(artifactPath);
        cancellationToken.ThrowIfCancellationRequested();

        BinaryVerificationResult result = await binaryVerifier.VerifyAsync(
            new BinaryVerificationRequest(
                string.Concat("artifact.", artifact.Id.ToString()),
                artifactPath,
                artifact.ContentDigest,
                artifact.ByteSize),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateRegisteredArtifactPath(artifactPath);
        StoredArtifact current = GetArtifact(artifactId)
            ?? throw Failure(ArtifactStoreFailureReason.RegisteredArtifactMissing);
        if (!HasSameMetadata(artifact, current))
        {
            throw Failure(ArtifactStoreFailureReason.DatabaseRejected);
        }

        return result;
    }

    internal static StoredArtifact? ReadArtifact(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EntityId<ArtifactIdTag> artifactId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(artifactId);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
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
            Path.Combine(ProjectFolders.GetPath(_workspacePath, projectId), "staging"));
        if (!IsStrictDescendant(stagingRoot, request.StagingPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        string kindRoot = Path.GetFullPath(
            Path.Combine(ProjectFolders.GetPath(_workspacePath, projectId), folder));
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

    private string ResolveRegisteredArtifactPath(StoredArtifact artifact)
    {
        string prefix = string.Concat(
            "projects/",
            artifact.ProjectId.ToString(),
            "/",
            GetKindFolder(artifact.Kind),
            "/");
        if (!artifact.StorageReference.StartsWith(prefix, StringComparison.Ordinal) ||
            !ArtifactPromotionRequest.IsValidRelativePath(
                artifact.StorageReference[prefix.Length..]) ||
            !WorkspacePathSafety.TryResolvePortableRelativePath(
                _workspacePath,
                artifact.StorageReference,
                out string artifactPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        return artifactPath;
    }

    private void ValidateRegisteredArtifactPath(string artifactPath)
    {
        ValidateWorkspaceChain(_workspacePath);
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                artifactPath,
                targetIsDirectory: false);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                ArtifactStoreFailureReason.StagingMissing));
        }
    }

    private static void ValidateWorkspaceChain(string workspacePath)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(workspacePath);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                ArtifactStoreFailureReason.UnsafePath));
        }
    }

    private void ValidateStagingPath(string stagingPath, string stagingRoot)
    {
        if (!IsStrictDescendant(stagingRoot, stagingPath))
        {
            throw Failure(ArtifactStoreFailureReason.UnsafePath);
        }

        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                stagingPath,
                targetIsDirectory: false);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                ArtifactStoreFailureReason.StagingMissing));
        }
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
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return new FileStream(stagingPath, options);
                }
                catch (Exception transient) when (attempt < MoveRetryAttempts && IsTransientSharing(transient))
                {
                    Thread.Sleep(MoveRetryDelayMilliseconds * attempt);
                }
            }
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

    private static async Task VerifyStoredContentAsync(
        FileStream stream,
        ContentDigest expectedDigest,
        long expectedByteSize,
        CancellationToken cancellationToken)
    {
        if (GetOpenedFileLength(stream) != expectedByteSize)
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
        if (GetOpenedFileLength(stream) != expectedByteSize)
        {
            throw Failure(ArtifactStoreFailureReason.SizeMismatch);
        }

        byte[] expectedDigestBytes = Convert.FromHexString(expectedDigest.Hex);
        if (!CryptographicOperations.FixedTimeEquals(
                expectedDigestBytes,
                actualDigestBytes))
        {
            throw Failure(ArtifactStoreFailureReason.DigestMismatch);
        }
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

    private static void ResetOpenedFile(FileStream stream)
    {
        try
        {
            stream.Position = 0;
        }
        catch (Exception exception)
            when (exception is IOException or NotSupportedException or
                ObjectDisposedException)
        {
            throw Failure(ArtifactStoreFailureReason.NotRegularFile);
        }
    }

    private const int MoveRetryAttempts = 6;
    private const int MoveRetryDelayMilliseconds = 150;

    /// <summary>Sharing violation (0x80070020/0x80070021) or access denied on a path that still exists.</summary>
    internal static bool IsTransientSharing(Exception exception) =>
        exception is UnauthorizedAccessException ||
        (exception is IOException io && exception is not (FileNotFoundException or DirectoryNotFoundException) &&
            ((io.HResult & 0xFFFF) is 0x20 or 0x21 or 0x5 || io.GetType() == typeof(IOException)));

    private static void MoveToDestination(string stagingPath, string destinationPath)
    {
        try
        {
            // A just-written file can be held briefly by a scanner/indexer (sharing violation, HResult 0x20/0x21 or
            // access denied). Nothing has moved when Move throws, so repeating the identical rename is safe.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(stagingPath, destinationPath, overwrite: false);
                    break;
                }
                catch (Exception transient) when (attempt < MoveRetryAttempts && IsTransientSharing(transient)
                    && File.Exists(stagingPath) && !File.Exists(destinationPath))
                {
                    Thread.Sleep(MoveRetryDelayMilliseconds * attempt);
                }
            }
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

    internal static StoredArtifact ReadStoredArtifact(SqliteDataReader reader)
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
        try
        {
            return WorkspacePathSafety.GetRequiredAttributes(path);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(exception.Failure, missingReason));
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            return WorkspacePathSafety.TryGetAttributes(path, out attributes);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                ArtifactStoreFailureReason.UnsafePath));
        }
    }

    private static void ValidateDirectoryAttributes(FileAttributes attributes)
    {
        try
        {
            WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                ArtifactStoreFailureReason.UnsafePath));
        }
    }

    private static void ValidateRegularFileAttributes(FileAttributes attributes)
    {
        try
        {
            WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                ArtifactStoreFailureReason.NotRegularFile));
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
        => WorkspacePathSafety.PathEntryExistsConservatively(path);

    private static bool IsStrictDescendant(string root, string candidate)
        => WorkspacePathSafety.IsStrictDescendant(root, candidate);

    private static bool HasSameWindowsDriveRoot(string first, string second)
        => WorkspacePathSafety.HasSameWindowsDriveRoot(first, second);

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

    internal static string GetKindFolder(ArtifactKind kind) =>
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

    private static ArtifactStoreFailureReason MapPathFailure(
        WorkspacePathSafetyFailure failure,
        ArtifactStoreFailureReason missingReason) =>
        failure switch
        {
            WorkspacePathSafetyFailure.Missing => missingReason,
            WorkspacePathSafetyFailure.ReparsePoint => ArtifactStoreFailureReason.ReparsePoint,
            WorkspacePathSafetyFailure.NotRegularFile => ArtifactStoreFailureReason.NotRegularFile,
            _ => ArtifactStoreFailureReason.UnsafePath,
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