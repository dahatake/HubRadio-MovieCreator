using System.Globalization;
using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class DatabaseMigrationResult
{
    internal DatabaseMigrationResult(
        bool applied,
        string? backupPath,
        int schemaVersion,
        ContentDigest schemaDigest)
    {
        Applied = applied;
        BackupPath = backupPath;
        SchemaVersion = schemaVersion;
        SchemaDigest = schemaDigest;
    }

    public bool Applied { get; }

    public string? BackupPath { get; }

    public int SchemaVersion { get; }

    public ContentDigest SchemaDigest { get; }
}

public static class DatabaseMigrator
{
    private const int InitialSchemaVersion = 1;
    private const int DefaultTimeoutSeconds = 5;
    private const string InitialMigrationName = "001_initial";
    private const string InitialMigrationResourceName =
        "HubRadio.Infrastructure.Database.Migrations.001_initial.sql";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static DatabaseMigrationResult Migrate(
        string databasePath,
        string backupDirectory) =>
        Migrate(databasePath, backupDirectory, TimeProvider.System);

    internal static DatabaseMigrationResult Migrate(
        string databasePath,
        string backupDirectory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        string normalizedDatabasePath = ValidateLocalAbsolutePath(
            databasePath,
            nameof(databasePath));
        string normalizedBackupDirectory = ValidateLocalAbsolutePath(
            backupDirectory,
            nameof(backupDirectory));
        MigrationDefinition migration = LoadInitialMigration();

        using SqliteConnection connection = OpenConnection(
            normalizedDatabasePath,
            SqliteOpenMode.ReadWriteCreate);

        SchemaState schemaState;
        try
        {
            schemaState = InspectSchema(connection, migration);
        }
        catch (SqliteException exception)
        {
            CreateValidatedBackup(connection, normalizedBackupDirectory, timeProvider);
            throw new InvalidOperationException(
                "The database migration metadata could not be read safely.",
                exception);
        }

        if (schemaState == SchemaState.Current)
        {
            ValidateDatabase(connection);
            return new DatabaseMigrationResult(
                applied: false,
                backupPath: null,
                InitialSchemaVersion,
                migration.Digest);
        }

        string backupPath = CreateValidatedBackup(
            connection,
            normalizedBackupDirectory,
            timeProvider);

        if (schemaState == SchemaState.Incompatible)
        {
            throw new InvalidOperationException(
                "The database schema is not the expected migration version.");
        }

        ApplyInitialMigration(connection, migration, timeProvider);
        ValidateDatabase(connection);

        return new DatabaseMigrationResult(
            applied: true,
            backupPath,
            InitialSchemaVersion,
            migration.Digest);
    }

    private static MigrationDefinition LoadInitialMigration()
    {
        Assembly assembly = typeof(DatabaseMigrator).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(InitialMigrationResourceName)
            ?? throw new InvalidOperationException(
                "The embedded initial database migration is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                "The embedded initial database migration is empty.");
        }

        string sql;
        try
        {
            sql = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                "The embedded initial database migration is not valid UTF-8.",
                exception);
        }

        return new MigrationDefinition(sql, ContentDigest.Compute(bytes));
    }

    private static SchemaState InspectSchema(
        SqliteConnection connection,
        MigrationDefinition migration)
    {
        using SqliteCommand objectCommand = connection.CreateCommand();
        objectCommand.CommandText = """
            SELECT type
            FROM sqlite_schema
            WHERE name = $name;
            """;
        objectCommand.Parameters.AddWithValue("$name", "schema_migrations");
        object? schemaObjectType = objectCommand.ExecuteScalar();

        if (schemaObjectType is null)
        {
            using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table', 'index', 'view', 'trigger');
                """;
            long userObjectCount = Convert.ToInt64(
                countCommand.ExecuteScalar(),
                CultureInfo.InvariantCulture);
            return userObjectCount == 0
                ? SchemaState.Empty
                : SchemaState.Incompatible;
        }

        if (!string.Equals(schemaObjectType as string, "table", StringComparison.Ordinal))
        {
            return SchemaState.Incompatible;
        }

        using SqliteCommand metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText = """
            SELECT version, name, content_digest, applied_at
            FROM schema_migrations
            ORDER BY version;
            """;
        using SqliteDataReader reader = metadataCommand.ExecuteReader();

        if (!reader.Read())
        {
            return SchemaState.Incompatible;
        }

        bool isCurrent =
            reader.GetValue(0) is long version && version == InitialSchemaVersion &&
            reader.GetValue(1) is string name &&
            string.Equals(name, InitialMigrationName, StringComparison.Ordinal) &&
            reader.GetValue(2) is string digest &&
            string.Equals(digest, migration.Digest.Hex, StringComparison.Ordinal) &&
            reader.GetValue(3) is string appliedAt &&
            appliedAt.Length > 0 &&
            string.Equals(appliedAt, appliedAt.Trim(), StringComparison.Ordinal);

        return isCurrent
            && !reader.Read()
            && SchemaMatchesMigration(connection, migration.Sql)
            ? SchemaState.Current
            : SchemaState.Incompatible;
    }

    private static bool SchemaMatchesMigration(
        SqliteConnection current,
        string migrationSql)
    {
        using SqliteConnection expected = OpenConnection(
            ":memory:",
            SqliteOpenMode.ReadWriteCreate);
        using (SqliteCommand command = expected.CreateCommand())
        {
            command.CommandText = migrationSql;
            command.ExecuteNonQuery();
        }

        return ReadSchemaDefinitions(current).SequenceEqual(
            ReadSchemaDefinitions(expected),
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ReadSchemaDefinitions(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, COALESCE(sql, '')
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
              AND type IN ('table', 'index', 'view', 'trigger')
            ORDER BY type, name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var definitions = new List<string>();
        while (reader.Read())
        {
            definitions.Add(string.Join(
                '\0',
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return definitions;
    }

    private static void ApplyInitialMigration(
        SqliteConnection connection,
        MigrationDefinition migration,
        TimeProvider timeProvider)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        using (SqliteCommand migrationCommand = connection.CreateCommand())
        {
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = migration.Sql;
            migrationCommand.ExecuteNonQuery();
        }

        DateTimeOffset appliedAt = timeProvider.GetUtcNow().ToUniversalTime();
        using (SqliteCommand metadataCommand = connection.CreateCommand())
        {
            metadataCommand.Transaction = transaction;
            metadataCommand.CommandText = """
                INSERT INTO schema_migrations (
                    version,
                    name,
                    content_digest,
                    applied_at
                )
                VALUES ($version, $name, $content_digest, $applied_at);
                """;
            metadataCommand.Parameters.AddWithValue("$version", InitialSchemaVersion);
            metadataCommand.Parameters.AddWithValue("$name", InitialMigrationName);
            metadataCommand.Parameters.AddWithValue(
                "$content_digest",
                migration.Digest.Hex);
            metadataCommand.Parameters.AddWithValue(
                "$applied_at",
                appliedAt.UtcDateTime.ToString(
                    TimestampFormat,
                    CultureInfo.InvariantCulture));

            if (metadataCommand.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "The initial database migration metadata was not recorded.");
            }
        }

        transaction.Commit();
    }

    private static string CreateValidatedBackup(
        SqliteConnection source,
        string backupDirectory,
        TimeProvider timeProvider)
    {
        Directory.CreateDirectory(backupDirectory);

        string timestamp = timeProvider
            .GetUtcNow()
            .ToUniversalTime()
            .UtcDateTime
            .ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
        string backupFileName = string.Concat(
            "hubradio-before-schema-001-",
            timestamp,
            "-",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ".db");
        string backupPath = Path.Combine(backupDirectory, backupFileName);

        using (new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None))
        {
        }

        bool isValid = false;
        try
        {
            using (SqliteConnection destination = OpenConnection(
                backupPath,
                SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(destination);
            }

            using (SqliteConnection validation = OpenConnection(
                backupPath,
                SqliteOpenMode.ReadOnly))
            {
                ValidateIntegrity(validation);
            }

            isValid = true;
            return backupPath;
        }
        finally
        {
            if (!isValid)
            {
                TryDeleteInvalidBackup(backupPath);
            }
        }
    }

    private static SqliteConnection OpenConnection(
        string databasePath,
        SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = DefaultTimeoutSeconds,
        };
        var connection = new SqliteConnection(connectionString.ToString())
        {
            DefaultTimeout = DefaultTimeoutSeconds,
        };

        try
        {
            connection.Open();

            using SqliteCommand enableForeignKeys = connection.CreateCommand();
            enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            enableForeignKeys.ExecuteNonQuery();

            using SqliteCommand verifyForeignKeys = connection.CreateCommand();
            verifyForeignKeys.CommandText = "PRAGMA foreign_keys;";
            long foreignKeysEnabled = Convert.ToInt64(
                verifyForeignKeys.ExecuteScalar(),
                CultureInfo.InvariantCulture);
            if (foreignKeysEnabled != 1)
            {
                throw new InvalidOperationException(
                    "SQLite foreign-key enforcement could not be enabled.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ValidateDatabase(SqliteConnection connection)
    {
        using (SqliteCommand foreignKeyCommand = connection.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeyCommand.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidOperationException(
                    "SQLite foreign-key validation failed.");
            }
        }

        ValidateIntegrity(connection);
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read() ||
            reader.GetValue(0) is not string result ||
            !string.Equals(result, "ok", StringComparison.Ordinal) ||
            reader.Read())
        {
            throw new InvalidOperationException(
                "SQLite integrity validation failed.");
        }
    }

    private static string ValidateLocalAbsolutePath(
        string path,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);

        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            HasNetworkOrDevicePrefix(path))
        {
            throw new ArgumentException(
                "The path must be a nonblank absolute local path without surrounding whitespace.",
                parameterName);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "The path must be a valid absolute local path.",
                parameterName);
        }

        if (HasNetworkOrDevicePrefix(fullPath) ||
            (OperatingSystem.IsWindows() && HasWindowsAlternateDataStream(fullPath)))
        {
            throw new ArgumentException(
                "The path must not use a network share, device namespace, or alternate data stream.",
                parameterName);
        }

        return fullPath;
    }

    private static bool HasNetworkOrDevicePrefix(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal) ||
        path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase);

    private static bool HasWindowsAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static void TryDeleteInvalidBackup(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class MigrationDefinition
    {
        public MigrationDefinition(string sql, ContentDigest digest)
        {
            Sql = sql;
            Digest = digest;
        }

        public string Sql { get; }

        public ContentDigest Digest { get; }
    }

    private enum SchemaState
    {
        Empty,
        Current,
        Incompatible,
    }
}
