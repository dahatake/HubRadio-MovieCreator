using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace HubRadio.Infrastructure.Recovery;

public enum WorkspaceIntegrityIssueKind
{
    DatabaseInvalid,
    JobAggregateInvalid,
    ArtifactRecordInvalid,
    ArtifactFileMissing,
    ArtifactPathUnsafe,
    ArtifactSizeMismatch,
    ArtifactDigestMismatch,
    ArtifactReadFailed,
    UnregisteredArtifactFile,
    StagingPathUnsafe,
}

public sealed class WorkspaceIntegrityIssue
{
    internal WorkspaceIntegrityIssue(
        WorkspaceIntegrityIssueKind kind,
        string? entityId,
        ContentDigest? contentDigest,
        ContentDigest? pathFingerprint)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The workspace integrity issue kind must be defined.");
        }

        if (entityId is not null && !IsCanonicalEntityId(entityId))
        {
            throw new ArgumentException(
                "An integrity issue entity ID must be a canonical non-empty UUID.",
                nameof(entityId));
        }

        Kind = kind;
        EntityId = entityId;
        ContentDigest = contentDigest;
        PathFingerprint = pathFingerprint;
    }

    public WorkspaceIntegrityIssueKind Kind { get; }

    public string? EntityId { get; }

    public ContentDigest? ContentDigest { get; }

    public ContentDigest? PathFingerprint { get; }

    private static bool IsCanonicalEntityId(string value) =>
        Guid.TryParseExact(value, "D", out Guid parsed) &&
        parsed != Guid.Empty &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);
}

public sealed class StagingRecoveryCandidate
{
    internal StagingRecoveryCandidate(
        EntityId<ProjectIdTag> projectId,
        string relativePath,
        long byteSize,
        ContentDigest contentDigest,
        DateTimeOffset lastWriteTimeUtc,
        ContentDigest candidateDigest,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(candidateDigest);

        if (!WorkspacePathSafety.IsValidPortableRelativePath(relativePath))
        {
            throw new ArgumentException(
                "A staging candidate relative path must be a safe portable relative path.",
                nameof(relativePath));
        }

        if (byteSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                byteSize,
                "A staging candidate byte size must not be negative.");
        }

        ProjectId = projectId;
        RelativePath = relativePath;
        ByteSize = byteSize;
        ContentDigest = contentDigest;
        LastWriteTimeUtc = lastWriteTimeUtc.ToUniversalTime();
        CandidateDigest = candidateDigest;
        CheckedAt = checkedAt.ToUniversalTime();
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public string RelativePath { get; }

    public long ByteSize { get; }

    public ContentDigest ContentDigest { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public ContentDigest CandidateDigest { get; }

    public DateTimeOffset CheckedAt { get; }
}

public sealed class WorkspaceRecoveryCheckResult
{
    internal WorkspaceRecoveryCheckResult(
        DateTimeOffset checkedAt,
        bool databaseUsable,
        IReadOnlyList<WorkspaceIntegrityIssue> issues,
        IReadOnlyList<StagingRecoveryCandidate> stagingCandidates)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(stagingCandidates);

        CheckedAt = checkedAt.ToUniversalTime();
        DatabaseUsable = databaseUsable;
        Issues = Array.AsReadOnly(issues.ToArray());
        StagingCandidates = Array.AsReadOnly(stagingCandidates.ToArray());
    }

    public DateTimeOffset CheckedAt { get; }

    public bool DatabaseUsable { get; }

    public bool IsIntact => DatabaseUsable && Issues.Count == 0;

    public IReadOnlyList<WorkspaceIntegrityIssue> Issues { get; }

    public IReadOnlyList<StagingRecoveryCandidate> StagingCandidates { get; }
}

public sealed class StagingCleanupEvidence
{
    internal StagingCleanupEvidence(
        EntityId<ProjectIdTag> projectId,
        ContentDigest candidateDigest,
        ContentDigest contentDigest,
        long byteSize,
        DateTimeOffset deletedAt)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(candidateDigest);
        ArgumentNullException.ThrowIfNull(contentDigest);

        if (byteSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                byteSize,
                "A cleanup evidence byte size must not be negative.");
        }

        ProjectId = projectId;
        CandidateDigest = candidateDigest;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        DeletedAt = deletedAt.ToUniversalTime();
    }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public ContentDigest CandidateDigest { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public DateTimeOffset DeletedAt { get; }
}

public sealed class WorkspaceRecovery
{
    private const int FileBufferSize = 64 * 1024;
    private const string CandidateDigestDomain =
        "HubRadio.MovieCreator.StagingRecoveryCandidate/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] ManagedArtifactFolders =
        Enum.GetValues<ArtifactKind>()
            .Select(ArtifactStore.GetKindFolder)
            .OrderBy(static folder => folder, StringComparer.Ordinal)
            .ToArray();

    private readonly string _databasePath;
    private readonly string _workspacePath;
    private readonly TimeProvider _timeProvider;

    public WorkspaceRecovery(string databasePath, string workspacePath)
        : this(databasePath, workspacePath, TimeProvider.System)
    {
    }

    internal WorkspaceRecovery(
        string databasePath,
        string workspacePath,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _databasePath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            databasePath,
            nameof(databasePath));
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

        _timeProvider = timeProvider;
    }

    public async Task<WorkspaceRecoveryCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset checkedAt = GetUtcNow();
        var issues = new List<WorkspaceIntegrityIssue>();

        DatabaseSnapshot snapshot;
        try
        {
            DatabaseMigrator.ValidateCurrentDatabase(_databasePath);
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = ReadDatabaseSnapshot(issues, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsDatabaseInspectionFailure(exception))
        {
            return DatabaseInvalidResult(checkedAt);
        }

        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_workspacePath);
        }
        catch (WorkspacePathSafetyException)
        {
            issues.Add(Issue(WorkspaceIntegrityIssueKind.ArtifactPathUnsafe));
            issues.Add(Issue(WorkspaceIntegrityIssueKind.StagingPathUnsafe));
            return CreateResult(checkedAt, issues, []);
        }

        var registeredPaths = new HashSet<string>(WorkspacePathSafety.PathComparer);
        foreach (StoredArtifact artifact in snapshot.Artifacts
            .OrderBy(static artifact => artifact.Id.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            registeredPaths.Add(artifact.StorageReference);
            await InspectArtifactAsync(
                artifact,
                issues,
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ProjectDirectory> projectDirectories = DiscoverProjectDirectories(
            snapshot.ProjectIds,
            issues,
            cancellationToken);
        ScanManagedArtifactDirectories(
            projectDirectories,
            registeredPaths,
            issues,
            cancellationToken);
        IReadOnlyList<StagingRecoveryCandidate> candidates =
            await ScanStagingDirectoriesAsync(
                projectDirectories,
                snapshot.ProtectedProjectIds,
                snapshot.ProtectAllProjects,
                checkedAt,
                issues,
                cancellationToken).ConfigureAwait(false);

        return CreateResult(checkedAt, issues, candidates);
    }

    public async Task<StagingCleanupEvidence> CleanupAsync(
        StagingRecoveryCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using SqliteConnection connection =
                SqliteStoreSupport.OpenConnection(_databasePath);
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            DatabaseMigrator.ValidateCurrentDatabase(_databasePath);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCleanupProjectState(
                connection,
                transaction,
                candidate.ProjectId,
                cancellationToken);
            ValidateCleanupArtifactRecords(
                connection,
                transaction,
                cancellationToken);

            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_workspacePath);
            string stagingRoot = Path.GetFullPath(Path.Combine(
                _workspacePath,
                "projects",
                candidate.ProjectId.ToString(),
                "staging"));
            if (!WorkspacePathSafety.TryResolvePortableRelativePath(
                    stagingRoot,
                    candidate.RelativePath,
                    out string candidatePath))
            {
                throw CleanupFailure();
            }

            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                candidatePath,
                targetIsDirectory: false);
            DateTimeOffset deletedAt;
            using (FileStream stream = OpenCleanupFile(candidatePath))
            {
                FileSnapshot snapshot = await ReadStableFileSnapshotAsync(
                    stream,
                    candidatePath,
                    cancellationToken).ConfigureAwait(false);

                if (snapshot.ByteSize != candidate.ByteSize ||
                    snapshot.LastWriteTimeUtc != candidate.LastWriteTimeUtc ||
                    !FixedTimeEquals(snapshot.ContentDigest, candidate.ContentDigest))
                {
                    throw CleanupFailure();
                }

                ContentDigest candidateDigest = ComputeCandidateDigest(
                    candidate.ProjectId,
                    candidate.RelativePath,
                    snapshot.ByteSize,
                    snapshot.ContentDigest,
                    snapshot.LastWriteTimeUtc,
                    candidate.CheckedAt);
                if (!FixedTimeEquals(candidateDigest, candidate.CandidateDigest))
                {
                    throw CleanupFailure();
                }

                WorkspacePathSafety.ValidateExistingPathChain(
                    _workspacePath,
                    candidatePath,
                    targetIsDirectory: false);
                ValidateOpenedRegularFile(stream);
                cancellationToken.ThrowIfCancellationRequested();
                deletedAt = GetUtcNow();
                MarkFileForDeletion(stream.SafeFileHandle);
            }

            var evidence = new StagingCleanupEvidence(
                candidate.ProjectId,
                candidate.CandidateDigest,
                candidate.ContentDigest,
                candidate.ByteSize,
                deletedAt);
            try
            {
                transaction.Commit();
            }
            catch (SqliteException)
            {
                // The immediate transaction performs no writes. Once the exact
                // verified handle has been deleted, failure to release this
                // coordination lock must not erase the path-free cleanup evidence.
            }

            return evidence;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsCleanupInspectionFailure(exception))
        {
            throw CleanupFailure();
        }
    }

    private DatabaseSnapshot ReadDatabaseSnapshot(
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(
            _databasePath,
            SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        HashSet<EntityId<ProjectIdTag>> projectIds = ReadProjectIds(
            connection,
            transaction,
            cancellationToken);
        (HashSet<EntityId<ProjectIdTag>> protectedProjects, bool protectAll) =
            ReadAndValidateJobs(
                connection,
                transaction,
                issues,
                cancellationToken);
        ArtifactReadResult artifactResult = ReadAndValidateArtifacts(
            connection,
            transaction,
            projectIds,
            issues,
            cancellationToken);

        transaction.Commit();
        return new DatabaseSnapshot(
            projectIds,
            protectedProjects,
            protectAll || artifactResult.ProtectAllStaging,
            artifactResult.Artifacts);
    }

    private static HashSet<EntityId<ProjectIdTag>> ReadProjectIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM projects ORDER BY id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var projectIds = new HashSet<EntityId<ProjectIdTag>>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetValue(0) is not string value ||
                !TryParseCanonicalEntityId(value, out EntityId<ProjectIdTag>? projectId) ||
                !projectIds.Add(projectId))
            {
                throw new InvalidOperationException(
                    "Stored project identity data is invalid.");
            }
        }

        return projectIds;
    }

    private static (HashSet<EntityId<ProjectIdTag>> ProtectedProjects, bool ProtectAll)
        ReadAndValidateJobs(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<WorkspaceIntegrityIssue> issues,
            CancellationToken cancellationToken)
    {
        List<JobRow> rows = ReadJobRows(connection, transaction, cancellationToken);
        var protectedProjects = new HashSet<EntityId<ProjectIdTag>>();
        bool protectAll = false;

        foreach (JobRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool jobIdParsed = EntityId<JobIdTag>.TryParse(
                row.Id,
                out EntityId<JobIdTag>? parsedJobId);
            string? safeJobId = jobIdParsed ? parsedJobId!.ToString() : null;
            bool jobIdCanonical = jobIdParsed &&
                string.Equals(row.Id, safeJobId, StringComparison.Ordinal);
            bool projectIdParsed = EntityId<ProjectIdTag>.TryParse(
                row.ProjectId,
                out EntityId<ProjectIdTag>? projectId);
            bool projectIdCanonical = projectIdParsed &&
                string.Equals(row.ProjectId, projectId!.ToString(), StringComparison.Ordinal);
            bool stateParsed = TryParseJobState(row.State, out JobState state);

            if (projectIdParsed &&
                (!stateParsed || IsProtectedState(state)))
            {
                protectedProjects.Add(projectId!);
            }

            if (!jobIdCanonical || !projectIdCanonical || !stateParsed)
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.JobAggregateInvalid,
                    safeJobId));
                if (projectIdParsed)
                {
                    protectedProjects.Add(projectId!);
                }
                else
                {
                    protectAll = true;
                }

                continue;
            }

            EntityId<JobIdTag> canonicalJobId = parsedJobId!;
            EntityId<ProjectIdTag> canonicalProjectId = projectId!;

            try
            {
                StoredJobAggregate? aggregate = JobStore.ReadJobAggregate(
                    connection,
                    transaction,
                    canonicalJobId);
                if (aggregate is null ||
                    aggregate.Job.ProjectId != canonicalProjectId ||
                    aggregate.Job.State != state)
                {
                    throw new InvalidOperationException(
                        "Stored job aggregate data is invalid.");
                }
            }
            catch (InvalidOperationException)
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.JobAggregateInvalid,
                    safeJobId));
                protectedProjects.Add(canonicalProjectId);
            }
        }

        return (protectedProjects, protectAll);
    }

    private static List<JobRow> ReadJobRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, project_id, state
            FROM jobs
            ORDER BY id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<JobRow>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new JobRow(
                ReadRawString(reader, 0),
                ReadRawString(reader, 1),
                ReadRawString(reader, 2)));
        }

        return rows;
    }

    private static ArtifactReadResult ReadAndValidateArtifacts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlySet<EntityId<ProjectIdTag>> projectIds,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        List<string> rawIds = ReadArtifactIds(connection, transaction, cancellationToken);
        var artifacts = new List<StoredArtifact>();
        bool protectAllStaging = false;

        foreach (string rawId in rawIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool parsed = EntityId<ArtifactIdTag>.TryParse(
                rawId,
                out EntityId<ArtifactIdTag>? artifactId);
            string? safeArtifactId = parsed ? artifactId!.ToString() : null;
            if (!parsed ||
                !string.Equals(rawId, safeArtifactId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.ArtifactRecordInvalid,
                    safeArtifactId));
                protectAllStaging = true;
                continue;
            }

            EntityId<ArtifactIdTag> canonicalArtifactId = artifactId!;

            try
            {
                StoredArtifact? artifact = ArtifactStore.ReadArtifact(
                    connection,
                    transaction,
                    canonicalArtifactId);
                if (artifact is null || !projectIds.Contains(artifact.ProjectId))
                {
                    throw new InvalidOperationException(
                        "Stored artifact data is invalid.");
                }

                artifacts.Add(artifact);
            }
            catch (InvalidOperationException)
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.ArtifactRecordInvalid,
                    safeArtifactId));
                protectAllStaging = true;
            }
        }

        return new ArtifactReadResult(
            Array.AsReadOnly(artifacts.ToArray()),
            protectAllStaging);
    }

    private static List<string> ReadArtifactIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM artifacts ORDER BY id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ids.Add(ReadRawString(reader, 0));
        }

        return ids;
    }

    private async Task InspectArtifactAsync(
        StoredArtifact artifact,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        string artifactId = artifact.Id.ToString();
        ContentDigest? pathFingerprint = ComputePathFingerprint(artifact.StorageReference);
        if (!WorkspacePathSafety.TryResolvePortableRelativePath(
                _workspacePath,
                artifact.StorageReference,
                out string artifactPath))
        {
            issues.Add(Issue(
                WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                artifactId,
                artifact.ContentDigest,
                pathFingerprint));
            return;
        }

        FileSnapshot snapshot;
        try
        {
            snapshot = await ReadStableFileSnapshotAsync(
                artifactPath,
                FileShare.Read,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileInspectionException exception)
        {
            WorkspaceIntegrityIssueKind kind = exception.Failure switch
            {
                FileInspectionFailure.Missing =>
                    WorkspaceIntegrityIssueKind.ArtifactFileMissing,
                FileInspectionFailure.UnsafePath =>
                    WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                _ => WorkspaceIntegrityIssueKind.ArtifactReadFailed,
            };
            issues.Add(Issue(
                kind,
                artifactId,
                artifact.ContentDigest,
                pathFingerprint));
            return;
        }

        if (snapshot.ByteSize != artifact.ByteSize)
        {
            issues.Add(Issue(
                WorkspaceIntegrityIssueKind.ArtifactSizeMismatch,
                artifactId,
                artifact.ContentDigest,
                pathFingerprint));
        }

        if (!FixedTimeEquals(snapshot.ContentDigest, artifact.ContentDigest))
        {
            issues.Add(Issue(
                WorkspaceIntegrityIssueKind.ArtifactDigestMismatch,
                artifactId,
                artifact.ContentDigest,
                pathFingerprint));
        }
    }

    private IReadOnlyList<ProjectDirectory> DiscoverProjectDirectories(
        IReadOnlySet<EntityId<ProjectIdTag>> registeredProjectIds,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        string projectsRoot = Path.GetFullPath(Path.Combine(_workspacePath, "projects"));
        if (!TryValidateOptionalDirectory(projectsRoot, out bool exists))
        {
            issues.Add(Issue(
                WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                pathFingerprint: ComputePathFingerprint("projects")));
            return [];
        }

        if (!exists)
        {
            return [];
        }

        if (!TryValidateRequiredDirectory(projectsRoot) ||
            !TryEnumerateEntries(projectsRoot, out string[] entries) ||
            !TryValidateRequiredDirectory(projectsRoot))
        {
            issues.Add(Issue(
                WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                pathFingerprint: ComputePathFingerprint("projects")));
            return [];
        }

        var directories = new List<ProjectDirectory>();
        foreach (string entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContentDigest? fingerprint = ComputePathFingerprintForAbsolutePath(entry);
            if (!TryGetSafeAttributes(entry, out FileAttributes attributes) ||
                !IsSafeDirectory(attributes))
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                    pathFingerprint: fingerprint));
                continue;
            }

            string directoryName = Path.GetFileName(entry);
            if (!TryParseCanonicalEntityId(
                    directoryName,
                    out EntityId<ProjectIdTag>? projectId))
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                    pathFingerprint: fingerprint));
                continue;
            }

            bool isRegistered = registeredProjectIds.Contains(projectId);
            if (!isRegistered)
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                    projectId.ToString(),
                    pathFingerprint: fingerprint));
            }

            directories.Add(new ProjectDirectory(projectId, entry, isRegistered));
        }

        return Array.AsReadOnly(directories
            .OrderBy(static item => item.ProjectId.ToString(), StringComparer.Ordinal)
            .ToArray());
    }

    private void ScanManagedArtifactDirectories(
        IReadOnlyList<ProjectDirectory> projects,
        IReadOnlySet<string> registeredPaths,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (ProjectDirectory project in projects)
        {
            foreach (string folder in ManagedArtifactFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string folderPath = Path.GetFullPath(Path.Combine(project.Path, folder));
                if (!TryValidateOptionalDirectory(folderPath, out bool exists))
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: ComputePathFingerprintForAbsolutePath(folderPath)));
                    continue;
                }

                if (!exists)
                {
                    continue;
                }

                ScanManagedTree(
                    project,
                    folderPath,
                    registeredPaths,
                    issues,
                    cancellationToken);
            }
        }
    }

    private void ScanManagedTree(
        ProjectDirectory project,
        string rootPath,
        IReadOnlySet<string> registeredPaths,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (!TryValidateRequiredDirectory(directory) ||
                !TryEnumerateEntries(directory, out string[] entries) ||
                !TryValidateRequiredDirectory(directory))
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                    project.ProjectId.ToString(),
                    pathFingerprint: ComputePathFingerprintForAbsolutePath(directory)));
                continue;
            }

            foreach (string entry in entries.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContentDigest? fingerprint = ComputePathFingerprintForAbsolutePath(entry);
                if (!TryGetSafeAttributes(entry, out FileAttributes attributes))
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Device) != 0)
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!WorkspacePathSafety.TryGetPortableRelativePath(
                            rootPath,
                            entry,
                            out _))
                    {
                        issues.Add(Issue(
                            WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                            project.ProjectId.ToString(),
                            pathFingerprint: fingerprint));
                        continue;
                    }

                    pending.Push(entry);
                    continue;
                }

                if (!WorkspacePathSafety.TryGetPortableRelativePath(
                        _workspacePath,
                        entry,
                        out string workspaceRelativePath))
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                try
                {
                    WorkspacePathSafety.ValidateExistingPathChain(
                        _workspacePath,
                        entry,
                        targetIsDirectory: false);
                }
                catch (WorkspacePathSafetyException)
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.ArtifactPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                if (!registeredPaths.Contains(workspaceRelativePath))
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.UnregisteredArtifactFile,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                }
            }
        }
    }

    private async Task<IReadOnlyList<StagingRecoveryCandidate>> ScanStagingDirectoriesAsync(
        IReadOnlyList<ProjectDirectory> projects,
        IReadOnlySet<EntityId<ProjectIdTag>> protectedProjectIds,
        bool protectAllProjects,
        DateTimeOffset checkedAt,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        var candidates = new List<StagingRecoveryCandidate>();
        foreach (ProjectDirectory project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.IsRegistered)
            {
                continue;
            }

            string stagingRoot = Path.GetFullPath(Path.Combine(project.Path, "staging"));
            if (!TryValidateOptionalDirectory(stagingRoot, out bool exists))
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                    project.ProjectId.ToString(),
                    pathFingerprint: ComputePathFingerprintForAbsolutePath(stagingRoot)));
                continue;
            }

            if (!exists)
            {
                continue;
            }

            await ScanStagingTreeAsync(
                project,
                stagingRoot,
                protectAllProjects || protectedProjectIds.Contains(project.ProjectId),
                checkedAt,
                candidates,
                issues,
                cancellationToken).ConfigureAwait(false);
        }

        return Array.AsReadOnly(candidates
            .OrderBy(static candidate => candidate.ProjectId.ToString(), StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.CandidateDigest.Hex, StringComparer.Ordinal)
            .ToArray());
    }

    private async Task ScanStagingTreeAsync(
        ProjectDirectory project,
        string stagingRoot,
        bool projectProtected,
        DateTimeOffset checkedAt,
        List<StagingRecoveryCandidate> candidates,
        List<WorkspaceIntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(stagingRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (!TryValidateRequiredDirectory(directory) ||
                !TryEnumerateEntries(directory, out string[] entries) ||
                !TryValidateRequiredDirectory(directory))
            {
                issues.Add(Issue(
                    WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                    project.ProjectId.ToString(),
                    pathFingerprint: ComputePathFingerprintForAbsolutePath(directory)));
                continue;
            }

            foreach (string entry in entries.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContentDigest? fingerprint = ComputePathFingerprintForAbsolutePath(entry);
                if (!TryGetSafeAttributes(entry, out FileAttributes attributes) ||
                    (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!WorkspacePathSafety.TryGetPortableRelativePath(
                            stagingRoot,
                            entry,
                            out _))
                    {
                        issues.Add(Issue(
                            WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                            project.ProjectId.ToString(),
                            pathFingerprint: fingerprint));
                        continue;
                    }

                    pending.Push(entry);
                    continue;
                }

                if (!WorkspacePathSafety.TryGetPortableRelativePath(
                        stagingRoot,
                        entry,
                        out string relativePath))
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                try
                {
                    WorkspacePathSafety.ValidateExistingPathChain(
                        _workspacePath,
                        entry,
                        targetIsDirectory: false);
                }
                catch (WorkspacePathSafetyException)
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                if (projectProtected)
                {
                    continue;
                }

                FileSnapshot snapshot;
                try
                {
                    snapshot = await ReadStableFileSnapshotAsync(
                        entry,
                        FileShare.Read,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FileInspectionException)
                {
                    issues.Add(Issue(
                        WorkspaceIntegrityIssueKind.StagingPathUnsafe,
                        project.ProjectId.ToString(),
                        pathFingerprint: fingerprint));
                    continue;
                }

                ContentDigest candidateDigest = ComputeCandidateDigest(
                    project.ProjectId,
                    relativePath,
                    snapshot.ByteSize,
                    snapshot.ContentDigest,
                    snapshot.LastWriteTimeUtc,
                    checkedAt);
                candidates.Add(new StagingRecoveryCandidate(
                    project.ProjectId,
                    relativePath,
                    snapshot.ByteSize,
                    snapshot.ContentDigest,
                    snapshot.LastWriteTimeUtc,
                    candidateDigest,
                    checkedAt));
            }
        }
    }

    private async Task<FileSnapshot> ReadStableFileSnapshotAsync(
        string path,
        FileShare share,
        CancellationToken cancellationToken)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw FileInspectionFailureException(
                exception.Failure == WorkspacePathSafetyFailure.Missing
                    ? FileInspectionFailure.Missing
                    : FileInspectionFailure.UnsafePath);
        }

        using FileStream stream = OpenInspectionFile(path, share);
        return await ReadStableFileSnapshotAsync(
            stream,
            path,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSnapshot> ReadStableFileSnapshotAsync(
        FileStream stream,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            ValidateOpenedRegularFile(stream);
            long initialLength = stream.Length;
            DateTimeOffset initialLastWrite = GetOpenedLastWriteTimeUtc(stream);
            stream.Position = 0;
            byte[] digestBytes = await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateOpenedRegularFile(stream);
            long finalLength = stream.Length;
            DateTimeOffset finalLastWrite = GetOpenedLastWriteTimeUtc(stream);
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: false);
            if (initialLength != finalLength || initialLastWrite != finalLastWrite)
            {
                throw FileInspectionFailureException(FileInspectionFailure.Changed);
            }

            return new FileSnapshot(
                finalLength,
                ContentDigest.FromBytes(digestBytes),
                finalLastWrite);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileInspectionException)
        {
            throw;
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw FileInspectionFailureException(
                exception.Failure == WorkspacePathSafetyFailure.Missing
                    ? FileInspectionFailure.Missing
                    : FileInspectionFailure.UnsafePath);
        }
        catch (Exception exception) when (IsFileSystemInspectionFailure(exception))
        {
            throw FileInspectionFailureException(FileInspectionFailure.ReadFailed);
        }
    }

    private static FileStream OpenInspectionFile(string path, FileShare share)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = share,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            return new FileStream(path, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw FileInspectionFailureException(FileInspectionFailure.Missing);
        }
        catch (Exception exception) when (IsFileSystemInspectionFailure(exception))
        {
            throw FileInspectionFailureException(FileInspectionFailure.ReadFailed);
        }
    }

    private static FileStream OpenCleanupFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw CleanupFailure();
        }

        SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.DeleteAccess,
            (uint)FileShare.Read,
            IntPtr.Zero,
            (uint)FileMode.Open,
            NativeMethods.FileAttributeNormal |
                NativeMethods.FileFlagOverlapped |
                NativeMethods.FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw CleanupFailure();
        }

        try
        {
            return new FileStream(
                handle,
                FileAccess.Read,
                FileBufferSize,
                isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void MarkFileForDeletion(SafeFileHandle handle)
    {
        var disposition = new NativeMethods.FileDispositionInfo
        {
            DeleteFile = true,
        };
        if (!NativeMethods.SetFileInformationByHandle(
                handle,
                NativeMethods.FileInformationClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<NativeMethods.FileDispositionInfo>()))
        {
            throw CleanupFailure();
        }
    }

    private static void ValidateOpenedRegularFile(FileStream stream)
    {
        FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
        WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw FileInspectionFailureException(FileInspectionFailure.UnsafePath);
        }
    }

    private static DateTimeOffset GetOpenedLastWriteTimeUtc(FileStream stream)
    {
        DateTime value = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static void ValidateCleanupProjectState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<ProjectIdTag> projectId,
        CancellationToken cancellationToken)
    {
        using (SqliteCommand projectCommand = connection.CreateCommand())
        {
            projectCommand.Transaction = transaction;
            projectCommand.CommandText = "SELECT id FROM projects WHERE id = $id;";
            SqliteStoreSupport.AddText(projectCommand, "$id", projectId.ToString());
            object? value = projectCommand.ExecuteScalar();
            if (value is not string storedId ||
                !string.Equals(storedId, projectId.ToString(), StringComparison.Ordinal))
            {
                throw CleanupFailure();
            }
        }

        var jobs = new List<(EntityId<JobIdTag> Id, JobState State)>();
        using (SqliteCommand jobsCommand = connection.CreateCommand())
        {
            jobsCommand.Transaction = transaction;
            jobsCommand.CommandText = """
                SELECT id, state
                FROM jobs
                WHERE project_id = $project_id
                ORDER BY id;
                """;
            SqliteStoreSupport.AddText(
                jobsCommand,
                "$project_id",
                projectId.ToString());
            using SqliteDataReader reader = jobsCommand.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawId = ReadRawString(reader, 0);
                string rawState = ReadRawString(reader, 1);
                if (!TryParseCanonicalEntityId(rawId, out EntityId<JobIdTag>? jobId) ||
                    !TryParseJobState(rawState, out JobState state) ||
                    IsProtectedState(state))
                {
                    throw CleanupFailure();
                }

                jobs.Add((jobId, state));
            }
        }

        foreach ((EntityId<JobIdTag> jobId, JobState state) in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredJobAggregate? aggregate = JobStore.ReadJobAggregate(
                connection,
                transaction,
                jobId);
            if (aggregate is null ||
                aggregate.Job.ProjectId != projectId ||
                aggregate.Job.State != state)
            {
                throw CleanupFailure();
            }
        }
    }

    private static void ValidateCleanupArtifactRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        HashSet<EntityId<ProjectIdTag>> projectIds = ReadProjectIds(
            connection,
            transaction,
            cancellationToken);
        var issues = new List<WorkspaceIntegrityIssue>();
        ArtifactReadResult result = ReadAndValidateArtifacts(
            connection,
            transaction,
            projectIds,
            issues,
            cancellationToken);
        if (result.ProtectAllStaging || issues.Count != 0)
        {
            throw CleanupFailure();
        }
    }

    private bool TryValidateOptionalDirectory(string path, out bool exists)
    {
        exists = false;
        try
        {
            if (!WorkspacePathSafety.TryGetAttributes(path, out _))
            {
                return true;
            }

            exists = true;
            WorkspacePathSafety.ValidateExistingPathChain(
                _workspacePath,
                path,
                targetIsDirectory: true);
            return true;
        }
        catch (WorkspacePathSafetyException)
        {
            return false;
        }
    }

    private bool TryValidateRequiredDirectory(string path) =>
        TryValidateOptionalDirectory(path, out bool exists) && exists;

    private static bool TryEnumerateEntries(string path, out string[] entries)
    {
        try
        {
            entries = Directory
                .EnumerateFileSystemEntries(
                    path,
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
            return true;
        }
        catch (Exception exception) when (IsFileSystemInspectionFailure(exception))
        {
            entries = [];
            return false;
        }
    }

    private static bool TryGetSafeAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            return WorkspacePathSafety.TryGetAttributes(path, out attributes);
        }
        catch (WorkspacePathSafetyException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsSafeDirectory(FileAttributes attributes) =>
        (attributes & FileAttributes.Directory) != 0 &&
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    private ContentDigest? ComputePathFingerprintForAbsolutePath(string fullPath)
    {
        if (!WorkspacePathSafety.IsStrictDescendant(_workspacePath, fullPath) ||
            !WorkspacePathSafety.HasSameWindowsDriveRoot(_workspacePath, fullPath))
        {
            return null;
        }

        try
        {
            string relativePath = Path.GetRelativePath(_workspacePath, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (relativePath.Length == 0 ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
            {
                return null;
            }

            return ComputePathFingerprint(relativePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static ContentDigest? ComputePathFingerprint(string relativePath)
    {
        try
        {
            return ContentDigest.Compute(StrictUtf8.GetBytes(relativePath));
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static ContentDigest ComputeCandidateDigest(
        EntityId<ProjectIdTag> projectId,
        string relativePath,
        long byteSize,
        ContentDigest contentDigest,
        DateTimeOffset lastWriteTimeUtc,
        DateTimeOffset checkedAt)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8Field(hash, tag: 1, CandidateDigestDomain);
        AppendUtf8Field(hash, tag: 2, projectId.ToString());
        AppendUtf8Field(hash, tag: 3, relativePath);
        AppendInt64Field(hash, tag: 4, byteSize);
        AppendField(hash, tag: 5, Convert.FromHexString(contentDigest.Hex));
        AppendInt64Field(hash, tag: 6, lastWriteTimeUtc.UtcDateTime.Ticks);
        AppendInt64Field(hash, tag: 7, checkedAt.UtcDateTime.Ticks);
        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static void AppendUtf8Field(IncrementalHash hash, byte tag, string value) =>
        AppendField(hash, tag, StrictUtf8.GetBytes(value));

    private static void AppendInt64Field(IncrementalHash hash, byte tag, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        AppendField(hash, tag, bytes);
    }

    private static void AppendField(
        IncrementalHash hash,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[sizeof(byte) + sizeof(int)];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], value.Length);
        hash.AppendData(header);
        hash.AppendData(value);
    }

    private static bool FixedTimeEquals(ContentDigest first, ContentDigest second) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(first.Hex),
            Convert.FromHexString(second.Hex));

    private static WorkspaceRecoveryCheckResult CreateResult(
        DateTimeOffset checkedAt,
        IEnumerable<WorkspaceIntegrityIssue> issues,
        IEnumerable<StagingRecoveryCandidate> candidates)
    {
        WorkspaceIntegrityIssue[] orderedIssues = issues
            .OrderBy(static issue => issue.EntityId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Kind)
            .ThenBy(
                static issue => issue.PathFingerprint?.Hex ?? string.Empty,
                StringComparer.Ordinal)
            .ThenBy(
                static issue => issue.ContentDigest?.Hex ?? string.Empty,
                StringComparer.Ordinal)
            .ToArray();
        StagingRecoveryCandidate[] orderedCandidates = candidates
            .OrderBy(static candidate => candidate.ProjectId.ToString(), StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.CandidateDigest.Hex, StringComparer.Ordinal)
            .ToArray();
        return new WorkspaceRecoveryCheckResult(
            checkedAt,
            databaseUsable: true,
            orderedIssues,
            orderedCandidates);
    }

    private static WorkspaceRecoveryCheckResult DatabaseInvalidResult(
        DateTimeOffset checkedAt) =>
        new(
            checkedAt,
            databaseUsable: false,
            [Issue(WorkspaceIntegrityIssueKind.DatabaseInvalid)],
            []);

    private static WorkspaceIntegrityIssue Issue(
        WorkspaceIntegrityIssueKind kind,
        string? entityId = null,
        ContentDigest? contentDigest = null,
        ContentDigest? pathFingerprint = null) =>
        new(kind, entityId, contentDigest, pathFingerprint);

    private DateTimeOffset GetUtcNow() =>
        _timeProvider.GetUtcNow().ToUniversalTime();

    private static string ReadRawString(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not string value)
        {
            throw new InvalidOperationException("Stored database text is invalid.");
        }

        return value;
    }

    private static bool TryParseCanonicalEntityId<TTag>(
        string value,
        [NotNullWhen(true)] out EntityId<TTag>? entityId)
    {
        return EntityId<TTag>.TryParse(value, out entityId) &&
            string.Equals(value, entityId.ToString(), StringComparison.Ordinal);
    }

    private static bool TryParseJobState(string value, out JobState state)
    {
        state = value switch
        {
            "queued" => JobState.Queued,
            "running" => JobState.Running,
            "succeeded" => JobState.Succeeded,
            "failed" => JobState.Failed,
            "cancelled" => JobState.Cancelled,
            "interrupted" => JobState.Interrupted,
            _ => (JobState)(-1),
        };
        return Enum.IsDefined(state);
    }

    private static bool IsProtectedState(JobState state) =>
        state is JobState.Queued or
            JobState.Running or
            JobState.Failed or
            JobState.Interrupted;

    private static bool IsDatabaseInspectionFailure(Exception exception) =>
        exception is SqliteException or InvalidOperationException or IOException or
            UnauthorizedAccessException or SecurityException or NotSupportedException or
            ArgumentException;

    private static bool IsFileSystemInspectionFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException or CryptographicException;

    private static bool IsCleanupInspectionFailure(Exception exception) =>
        exception is SqliteException or WorkspacePathSafetyException or
            FileInspectionException or InvalidOperationException or IOException or
            UnauthorizedAccessException or SecurityException or NotSupportedException or
            ArgumentException or CryptographicException;

    private static InvalidOperationException CleanupFailure() =>
        new("The staging cleanup could not be completed safely.");

    private static FileInspectionException FileInspectionFailureException(
        FileInspectionFailure failure) =>
        new(failure);

    private sealed record DatabaseSnapshot(
        IReadOnlySet<EntityId<ProjectIdTag>> ProjectIds,
        IReadOnlySet<EntityId<ProjectIdTag>> ProtectedProjectIds,
        bool ProtectAllProjects,
        IReadOnlyList<StoredArtifact> Artifacts);

    private sealed record ArtifactReadResult(
        IReadOnlyList<StoredArtifact> Artifacts,
        bool ProtectAllStaging);

    private sealed record JobRow(string Id, string ProjectId, string State);

    private sealed record ProjectDirectory(
        EntityId<ProjectIdTag> ProjectId,
        string Path,
        bool IsRegistered);

    private sealed record FileSnapshot(
        long ByteSize,
        ContentDigest ContentDigest,
        DateTimeOffset LastWriteTimeUtc);

    private enum FileInspectionFailure
    {
        Missing,
        UnsafePath,
        ReadFailed,
        Changed,
    }

    private sealed class FileInspectionException : Exception
    {
        internal FileInspectionException(FileInspectionFailure failure)
            : base("The workspace file could not be inspected safely.")
        {
            Failure = failure;
        }

        internal FileInspectionFailure Failure { get; }
    }

    private static class NativeMethods
    {
        internal const uint DeleteAccess = 0x00010000;
        internal const uint GenericRead = 0x80000000;
        internal const uint FileAttributeNormal = 0x00000080;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const uint FileFlagSequentialScan = 0x08000000;

        internal enum FileInformationClass
        {
            FileDispositionInfo = 4,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.U1)]
            internal bool DeleteFile;
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInformationClass fileInformationClass,
            ref FileDispositionInfo fileInformation,
            uint bufferSize);
    }
}