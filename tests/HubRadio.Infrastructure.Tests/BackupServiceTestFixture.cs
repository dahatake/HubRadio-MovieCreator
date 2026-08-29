using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Audit;
using HubRadio.Infrastructure.Backup;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
internal sealed class BackupServiceTestFixture : IDisposable
{
    internal static readonly DateTimeOffset CreatedAt =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    private const string DigestA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal BackupServiceTestFixture()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.BackupTests",
            Guid.NewGuid().ToString("N"));
        WorkspacePath = Path.Combine(RootPath, "workspace");
        BackupRootPath = Path.Combine(RootPath, "backup-root");
        DatabasePath = Path.Combine(WorkspacePath, "hubradio.db");
        Directory.CreateDirectory(RootPath);
        CurrentSid = GetCurrentSid();
        _ = WorkspaceAcl.CreateNew(WorkspacePath, CurrentSid);
        _ = WorkspaceAcl.CreateNew(BackupRootPath, CurrentSid);
        _ = DatabaseMigrator.Migrate(
            DatabasePath,
            Path.Combine(WorkspacePath, "schema-backups"),
            new FixedTimeProvider(CreatedAt.AddDays(-1)));
        Service = CreateService();
    }

    internal string RootPath { get; }

    internal string WorkspacePath { get; }

    internal string BackupRootPath { get; }

    internal string DatabasePath { get; }

    internal string CurrentSid { get; }

    internal BackupService Service { get; }

    internal BackupService CreateService(Action<string, string>? directoryMove = null) =>
        new(
            DatabasePath,
            WorkspacePath,
            BackupRootPath,
            CurrentSid,
            new FixedTimeProvider(CreatedAt),
            directoryMove);

    internal EntityId<ProjectIdTag> CreateProject()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        var project = new Project(
            projectId,
            "Backup test project",
            "Exercise verified backup and restore.",
            "Test suite",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        new ProjectStore(DatabasePath).CreateProject(
            project,
            "{\"backup\":true}",
            CreatedAt.AddHours(-1));
        return projectId;
    }

    internal EntityId<ProjectIdTag> CreateProjectWithBusinessRows()
    {
        EntityId<ProjectIdTag> projectId = CreateProject();
        string versionId = EntityId<CompositionVersionIdTag>.New().ToString();
        string workingCopyId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        string reviewId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        string approvalId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        string timestamp = CreatedAt.ToString("O", CultureInfo.InvariantCulture);

        using (SqliteConnection connection = BackupServiceTestSupport.OpenConnection(
            DatabasePath))
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Execute(
                connection,
                transaction,
                """
                    INSERT INTO composition_versions (
                        id, project_id, parent_version_id, content_digest, created_at, payload
                    ) VALUES ($id, $project, NULL, $digest, $at, X'7B7D');
                    """,
                ("$id", versionId),
                ("$project", projectId.ToString()),
                ("$digest", DigestA),
                ("$at", timestamp));
            Execute(
                connection,
                transaction,
                """
                    INSERT INTO working_copies (
                        id, project_id, base_version_id, revision, updated_at, payload
                    ) VALUES ($id, $project, $version, 0, $at, X'7B7D');
                    """,
                ("$id", workingCopyId),
                ("$project", projectId.ToString()),
                ("$version", versionId),
                ("$at", timestamp));
            Execute(
                connection,
                transaction,
                """
                    INSERT INTO review_records (
                        id, project_id, composition_version_id,
                        composition_version_digest, state, actor_id, actor_role,
                        recorded_at, reason
                    ) VALUES (
                        $id, $project, $version, $digest, 'Pending',
                        'reviewer', 'Administrator', $at, 'Backup test review'
                    );
                    """,
                ("$id", reviewId),
                ("$project", projectId.ToString()),
                ("$version", versionId),
                ("$digest", DigestA),
                ("$at", timestamp));
            Execute(
                connection,
                transaction,
                """
                    INSERT INTO approvals (
                        id, project_id, composition_version_id,
                        composition_version_digest, actor_id, actor_role,
                        approved_at, reason
                    ) VALUES (
                        $id, $project, $version, $digest,
                        'approver', 'Administrator', $at, 'Backup test approval'
                    );
                    """,
                ("$id", approvalId),
                ("$project", projectId.ToString()),
                ("$version", versionId),
                ("$digest", DigestA),
                ("$at", timestamp));
            Execute(
                connection,
                transaction,
                """
                    INSERT INTO role_assignments (
                        actor_sid, role, assigned_at, assigned_by_sid, reason
                    ) VALUES ($actor, 'Administrator', $at, $actor, 'Backup test role');
                    """,
                ("$actor", CurrentSid),
                ("$at", timestamp));
            transaction.Commit();
        }

        new AuditLog(DatabasePath).Append(new AuditEvent(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ProjectRegistered,
            CurrentSid,
            ReviewActorRole.Administrator,
            CreatedAt,
            "Backup test audit event",
            projectId));
        return projectId;
    }

    internal async Task<IReadOnlyList<StoredArtifact>> CreateArtifactsAsync(
        EntityId<ProjectIdTag> projectId,
        IEnumerable<ArtifactKind> kinds)
    {
        var artifacts = new List<StoredArtifact>();
        int index = 0;
        foreach (ArtifactKind kind in kinds)
        {
            byte[] content = Encoding.UTF8.GetBytes(
                string.Concat("small-backup-artifact-", kind.ToString()));
            string stagingPath = Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                "staging",
                string.Concat(index.ToString("D2", CultureInfo.InvariantCulture), ".tmp"));
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.WriteAllBytes(stagingPath, content);
            var request = new ArtifactPromotionRequest(
                EntityId<ArtifactIdTag>.New(),
                projectId,
                kind,
                stagingPath,
                string.Concat(
                    "nested/",
                    index.ToString("D2", CultureInfo.InvariantCulture),
                    ".bin"),
                ContentDigest.Compute(content),
                content.LongLength,
                CreatedAt.AddMinutes(index));
            StoredArtifact artifact = await new ArtifactStore(DatabasePath, WorkspacePath)
                .PromoteAsync(request);
            artifacts.Add(artifact);
            index++;
        }

        return Array.AsReadOnly(artifacts.ToArray());
    }

    internal void SetProjectPayloadSize(
        EntityId<ProjectIdTag> projectId,
        int byteCount)
    {
        using SqliteConnection connection = BackupServiceTestSupport.OpenConnection(
            DatabasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE projects SET payload = zeroblob($size) WHERE id = $id;";
        command.Parameters.AddWithValue("$size", byteCount);
        command.Parameters.AddWithValue("$id", projectId.ToString());
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    internal long CountArtifacts(string databasePath) =>
        BackupServiceTestSupport.ExecuteScalarInt64(
            databasePath,
            "SELECT COUNT(*) FROM artifacts;");

    internal string GetStoragePath(string storageReference) =>
        Path.Combine(
            WorkspacePath,
            storageReference.Replace('/', Path.DirectorySeparatorChar));

    internal string GetBackupDirectory(BackupResult result) =>
        Path.Combine(BackupRootPath, result.BackupDirectoryName);

    internal string GetManifestPath(BackupResult result) =>
        Path.Combine(GetBackupDirectory(result), BackupManifest.FileName);

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

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    private static string GetCurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new AssertInconclusiveException(
                "The current Windows identity has no SID.");
    }

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
