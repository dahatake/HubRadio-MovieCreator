using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Backup;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
internal static class BackupServiceTestSupport
{
    internal static SqliteConnection OpenConnection(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 30,
        };
        var connection = new SqliteConnection(builder.ToString())
        {
            DefaultTimeout = 30,
        };
        connection.Open();
        return connection;
    }

    internal static long ExecuteScalarInt64(string databasePath, string sql)
    {
        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar()
            ?? throw new AssertFailedException("The scalar query returned null."));
    }

    internal static void ExecuteNonQuery(string databasePath, string sql)
    {
        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    internal static IReadOnlyDictionary<string, ContentDigest> SnapshotFileTree(
        string rootPath)
    {
        var snapshot = new SortedDictionary<string, ContentDigest>(StringComparer.Ordinal);
        foreach (string path in Directory
            .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = Path.GetRelativePath(rootPath, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            snapshot.Add(relativePath, ContentDigest.Compute(File.ReadAllBytes(path)));
        }

        return snapshot;
    }

    internal static void AssertFileTreesEqual(
        IReadOnlyDictionary<string, ContentDigest> expected,
        IReadOnlyDictionary<string, ContentDigest> actual)
    {
        CollectionAssert.AreEqual(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach ((string path, ContentDigest digest) in expected)
        {
            Assert.AreEqual(digest, actual[path], path);
        }
    }

    internal static void AssertBusinessRowsRetained(string databasePath)
    {
        foreach (string table in new[]
        {
            "projects",
            "composition_versions",
            "working_copies",
            "review_records",
            "approvals",
            "audit_events",
            "audit_chain_entries",
            "role_assignments",
        })
        {
            Assert.AreEqual(
                1L,
                ExecuteScalarInt64(
                    databasePath,
                    string.Concat("SELECT COUNT(*) FROM ", table, ";")),
                table);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void AddUnexpectedExplicitRule(string path)
    {
        DirectorySecurity security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(path),
            AccessControlSections.Access | AccessControlSections.Owner);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    internal static void AssertSafeException(
        BackupServiceException exception,
        BackupServiceFailureReason reason,
        bool reconciliationRequired,
        BackupServiceTestFixture fixture,
        params string[] additionalForbidden)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        string[] forbiddenValues =
        [
            fixture.RootPath,
            fixture.WorkspacePath,
            fixture.DatabasePath,
            fixture.BackupRootPath,
            fixture.CurrentSid,
            .. additionalForbidden,
        ];
        foreach (string value in forbiddenValues)
        {
            AssertDoesNotContain(exception.ToString(), value);
        }
    }

    internal static void AssertNoPreviousWorkspace(
        BackupServiceTestFixture fixture)
    {
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.previous-*").Any());
    }

    internal static void AssertNoRestoreStaging(
        BackupServiceTestFixture fixture)
    {
        Assert.IsFalse(Directory.EnumerateDirectories(
            fixture.RootPath,
            ".hubradio.restore-*").Any());
    }

    internal static bool IsLeafName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) &&
        !Path.IsPathRooted(value) &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    internal static void RequireWindowsFixedTempDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "S-015 requires Windows ACL and local fixed-drive semantics.");
        }

        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))
            ?? throw new AssertInconclusiveException(
                "The temporary path has no drive root.");
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            Assert.Inconclusive("The temporary path is not on a local fixed drive.");
        }
    }

    internal static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The repository root could not be resolved.");
    }

    internal static void AssertDoesNotContain(string value, string forbidden)
    {
        if (!string.IsNullOrWhiteSpace(forbidden))
        {
            Assert.IsFalse(value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }
}
