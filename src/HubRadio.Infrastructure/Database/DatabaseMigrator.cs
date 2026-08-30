using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Audit;
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
    private const int LatestSchemaVersion = 5;
    private const int DefaultTimeoutSeconds = 5;
    private const string SchemaDigestDomain =
        "HubRadio.MovieCreator.DatabaseMigrationSet/v1";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly MigrationResource[] MigrationResources =
    [
        new(
            1,
            "001_initial",
            "HubRadio.Infrastructure.Database.Migrations.001_initial.sql"),
        new(
            2,
            "002_jobs",
            "HubRadio.Infrastructure.Database.Migrations.002_jobs.sql"),
        new(
            3,
            "003_audit_chain",
            "HubRadio.Infrastructure.Database.Migrations.003_audit_chain.sql"),
        new(
            4,
            "004_role_assignments",
            "HubRadio.Infrastructure.Database.Migrations.004_role_assignments.sql"),
        new(
            5,
            "005_term_entries",
            "HubRadio.Infrastructure.Database.Migrations.005_term_entries.sql"),
    ];

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
        IReadOnlyList<MigrationDefinition> migrations = LoadMigrations();
        ContentDigest schemaDigest = ComputeSchemaDigest(migrations);

        using SqliteConnection connection = OpenConnection(
            normalizedDatabasePath,
            SqliteOpenMode.ReadWriteCreate);

        SchemaState schemaState;
        try
        {
            schemaState = InspectSchema(connection, migrations);
        }
        catch (SqliteException exception)
        {
            CreateValidatedBackup(connection, normalizedBackupDirectory, timeProvider);
            throw new InvalidOperationException(
                "The database migration metadata could not be read safely.",
                exception);
        }

        if (schemaState.IsCompatible &&
            schemaState.CurrentVersion == LatestSchemaVersion)
        {
            ValidateDatabase(connection);
            return new DatabaseMigrationResult(
                applied: false,
                backupPath: null,
                LatestSchemaVersion,
                schemaDigest);
        }

        string backupPath = CreateValidatedBackup(
            connection,
            normalizedBackupDirectory,
            timeProvider);

        if (!schemaState.IsCompatible)
        {
            throw new InvalidOperationException(
                "The database schema is not the expected migration version.");
        }

        foreach (MigrationDefinition migration in migrations.Skip(schemaState.CurrentVersion))
        {
            ApplyMigration(connection, migration, timeProvider);
        }

        SchemaState migratedState = InspectSchema(connection, migrations);
        if (!migratedState.IsCompatible ||
            migratedState.CurrentVersion != LatestSchemaVersion)
        {
            throw new InvalidOperationException(
                "The database schema did not reach the expected migration version.");
        }

        ValidateDatabase(connection);

        return new DatabaseMigrationResult(
            applied: true,
            backupPath,
            LatestSchemaVersion,
            schemaDigest);
    }

    internal static void ValidateCurrentDatabase(string databasePath)
    {
        string normalizedDatabasePath = ValidateLocalAbsolutePath(
            databasePath,
            nameof(databasePath));
        IReadOnlyList<MigrationDefinition> migrations = LoadMigrations();

        using SqliteConnection connection = OpenConnection(
            normalizedDatabasePath,
            SqliteOpenMode.ReadOnly);
        SchemaState schemaState = InspectSchema(connection, migrations);
        if (!schemaState.IsCompatible ||
            schemaState.CurrentVersion != LatestSchemaVersion)
        {
            throw new InvalidOperationException(
                "The database schema is not the expected migration version.");
        }

        ValidateDatabase(connection);
    }

    private static IReadOnlyList<MigrationDefinition> LoadMigrations()
    {
        Assembly assembly = typeof(DatabaseMigrator).Assembly;
        var migrations = new MigrationDefinition[MigrationResources.Length];

        for (int index = 0; index < MigrationResources.Length; index++)
        {
            MigrationResource resource = MigrationResources[index];
            if (resource.Version != index + 1)
            {
                throw new InvalidOperationException(
                    "Embedded database migrations must form a contiguous ordered sequence.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resource.ResourceName)
                ?? throw new InvalidOperationException(
                    $"The embedded database migration {resource.Name} is unavailable.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();

            if (bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The embedded database migration {resource.Name} is empty.");
            }

            string sql;
            try
            {
                sql = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException(
                    $"The embedded database migration {resource.Name} is not valid UTF-8.",
                    exception);
            }

            migrations[index] = new MigrationDefinition(
                resource.Version,
                resource.Name,
                sql,
                ContentDigest.Compute(bytes));
        }

        return migrations;
    }

    private static SchemaState InspectSchema(
        SqliteConnection connection,
        IReadOnlyList<MigrationDefinition> migrations)
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
                ? SchemaState.Compatible(version: 0)
                : SchemaState.Incompatible;
        }

        if (!string.Equals(schemaObjectType as string, "table", StringComparison.Ordinal))
        {
            return SchemaState.Incompatible;
        }

        int migrationCount = 0;
        using (SqliteCommand metadataCommand = connection.CreateCommand())
        {
            metadataCommand.CommandText = """
                SELECT version, name, content_digest, applied_at
                FROM schema_migrations
                ORDER BY version;
                """;
            using SqliteDataReader reader = metadataCommand.ExecuteReader();

            while (reader.Read())
            {
                if (migrationCount >= migrations.Count)
                {
                    return SchemaState.Incompatible;
                }

                MigrationDefinition expected = migrations[migrationCount];
                bool isExactPrefixRow =
                    reader.GetValue(0) is long version && version == expected.Version &&
                    reader.GetValue(1) is string name &&
                    string.Equals(name, expected.Name, StringComparison.Ordinal) &&
                    reader.GetValue(2) is string digest &&
                    string.Equals(digest, expected.Digest.Hex, StringComparison.Ordinal) &&
                    reader.GetValue(3) is string appliedAt &&
                    appliedAt.Length > 0 &&
                    string.Equals(appliedAt, appliedAt.Trim(), StringComparison.Ordinal);

                if (!isExactPrefixRow)
                {
                    return SchemaState.Incompatible;
                }

                migrationCount++;
            }
        }

        if (migrationCount == 0 ||
            !SchemaMatchesMigrations(connection, migrations.Take(migrationCount)))
        {
            return SchemaState.Incompatible;
        }

        return SchemaState.Compatible(migrationCount);
    }

    private static bool SchemaMatchesMigrations(
        SqliteConnection current,
        IEnumerable<MigrationDefinition> migrations)
    {
        using SqliteConnection expected = OpenConnection(
            ":memory:",
            SqliteOpenMode.ReadWriteCreate);
        foreach (MigrationDefinition migration in migrations)
        {
            using SqliteCommand command = expected.CreateCommand();
            command.CommandText = migration.Sql;
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

    private static ContentDigest ComputeSchemaDigest(
        IReadOnlyList<MigrationDefinition> migrations)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8Field(hash, tag: 1, SchemaDigestDomain);
        AppendInt32Field(hash, tag: 2, migrations.Count);

        foreach (MigrationDefinition migration in migrations)
        {
            AppendInt32Field(hash, tag: 3, migration.Version);
            AppendUtf8Field(hash, tag: 4, migration.Name);
            AppendField(hash, tag: 5, Convert.FromHexString(migration.Digest.Hex));
        }

        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    private static void AppendUtf8Field(IncrementalHash hash, byte tag, string value) =>
        AppendField(hash, tag, StrictUtf8.GetBytes(value));

    private static void AppendInt32Field(IncrementalHash hash, byte tag, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
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

    private static void ApplyMigration(
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

        ApplyDataMigration(connection, transaction, migration);
        ValidateForeignKeys(connection, transaction);

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
            metadataCommand.Parameters.AddWithValue("$version", migration.Version);
            metadataCommand.Parameters.AddWithValue("$name", migration.Name);
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
                    $"Database migration metadata was not recorded for {migration.Name}.");
            }
        }

        transaction.Commit();
    }

    private static void ApplyDataMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationDefinition migration)
    {
        if (migration.Version == 3)
        {
            AuditChainCodec.Backfill(connection, transaction);
        }
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
            "hubradio-before-schema-005-",
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
        ValidateForeignKeys(connection, transaction: null);
        ValidateIntegrity(connection);
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidOperationException(
                "SQLite foreign-key validation failed.");
        }
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
        public MigrationDefinition(
            int version,
            string name,
            string sql,
            ContentDigest digest)
        {
            Version = version;
            Name = name;
            Sql = sql;
            Digest = digest;
        }

        public int Version { get; }

        public string Name { get; }

        public string Sql { get; }

        public ContentDigest Digest { get; }
    }

    private sealed class MigrationResource
    {
        public MigrationResource(int version, string name, string resourceName)
        {
            Version = version;
            Name = name;
            ResourceName = resourceName;
        }

        public int Version { get; }

        public string Name { get; }

        public string ResourceName { get; }
    }

    private readonly record struct SchemaState(bool IsCompatible, int CurrentVersion)
    {
        public static SchemaState Incompatible => new(false, 0);

        public static SchemaState Compatible(int version) => new(true, version);
    }
}
