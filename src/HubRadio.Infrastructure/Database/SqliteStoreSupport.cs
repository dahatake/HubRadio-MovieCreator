using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class StoreConcurrencyException : InvalidOperationException
{
    public StoreConcurrencyException(
        string entityKind,
        string entityId,
        int expectedRevision,
        int actualRevision)
        : base(FormattableString.Invariant(
            $"{entityKind} '{entityId}' revision conflict: expected {expectedRevision}, actual {actualRevision}."))
    {
        EntityKind = entityKind;
        EntityId = entityId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string EntityKind { get; }

    public string EntityId { get; }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}

internal static class SqliteStoreSupport
{
    private const int DefaultTimeoutSeconds = 5;
    private const string TimestampFormat = "O";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string Initialize(string databasePath)
    {
        string normalizedPath = ValidateLocalAbsolutePath(databasePath, nameof(databasePath));
        using SqliteConnection connection = OpenConnection(normalizedPath);
        return normalizedPath;
    }

    internal static SqliteConnection OpenConnection(string databasePath)
        => OpenConnection(databasePath, SqliteOpenMode.ReadWrite);

    internal static SqliteConnection OpenConnection(
        string databasePath,
        SqliteOpenMode mode)
    {
        if (mode is not (SqliteOpenMode.ReadWrite or SqliteOpenMode.ReadOnly))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "A store connection must be read-only or read-write.");
        }

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

            using (SqliteCommand enableForeignKeys = connection.CreateCommand())
            {
                enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                enableForeignKeys.ExecuteNonQuery();
            }

            using (SqliteCommand verifyForeignKeys = connection.CreateCommand())
            {
                verifyForeignKeys.CommandText = "PRAGMA foreign_keys;";
                if (verifyForeignKeys.ExecuteScalar() is not long enabled || enabled != 1)
                {
                    throw new InvalidOperationException(
                        "SQLite foreign-key enforcement could not be enabled.");
                }
            }

            ValidateRequiredMigrations(connection);
            return connection;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                "The store database is unavailable or does not have the required schema.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static byte[] CanonicalizePayload(string payloadJson, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(payloadJson, parameterName);

        try
        {
            byte[] utf8 = StrictUtf8.GetBytes(payloadJson);
            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
            using JsonDocument document = JsonDocument.Parse(canonical);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            return canonical;
        }
        catch (Exception exception)
            when (exception is EncoderFallbackException or JsonException)
        {
            throw new ArgumentException(
                "Payload JSON must be a canonicalizable JSON object.",
                parameterName);
        }
    }

    internal static string ReadCanonicalPayload(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        if (reader.GetValue(ordinal) is not byte[] storedBytes || storedBytes.Length == 0)
        {
            throw InvalidStoredData(entityKind);
        }

        try
        {
            _ = StrictUtf8.GetString(storedBytes);
            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(storedBytes);
            using JsonDocument document = JsonDocument.Parse(canonical);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !storedBytes.AsSpan().SequenceEqual(canonical))
            {
                throw new JsonException();
            }

            return StrictUtf8.GetString(canonical);
        }
        catch (Exception exception)
            when (exception is DecoderFallbackException or JsonException)
        {
            throw InvalidStoredData(entityKind);
        }
    }

    internal static string FormatTimestamp(
        DateTimeOffset value,
        string parameterName)
    {
        try
        {
            return value
                .ToUniversalTime()
                .ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The timestamp cannot be represented in UTC.");
        }
    }

    internal static DateTimeOffset ReadTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        string value = ReadString(reader, ordinal, entityKind);
        if (!DateTimeOffset.TryParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp))
        {
            throw InvalidStoredData(entityKind);
        }

        return timestamp;
    }

    internal static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ReadTimestamp(reader, ordinal, entityKind);
    }

    internal static EntityId<TTag> ReadEntityId<TTag>(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        string value = ReadString(reader, ordinal, entityKind);
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw InvalidStoredData(entityKind);
        }

        return id;
    }

    internal static EntityId<TTag>? ReadNullableEntityId<TTag>(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ReadEntityId<TTag>(reader, ordinal, entityKind);
    }

    internal static ContentDigest ReadContentDigest(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        string value = ReadString(reader, ordinal, entityKind);
        if (!ContentDigest.TryParse(value, out ContentDigest? digest) ||
            !string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw InvalidStoredData(entityKind);
        }

        return digest;
    }

    internal static ContentDigest? ReadNullableContentDigest(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ReadContentDigest(reader, ordinal, entityKind);
    }

    internal static string ReadString(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        if (reader.GetValue(ordinal) is not string value)
        {
            throw InvalidStoredData(entityKind);
        }

        return value;
    }

    internal static string? ReadNullableString(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ReadString(reader, ordinal, entityKind);
    }

    internal static int ReadInt32(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        if (reader.GetValue(ordinal) is not long value ||
            value < int.MinValue ||
            value > int.MaxValue)
        {
            throw InvalidStoredData(entityKind);
        }

        return (int)value;
    }

    internal static long ReadInt64(
        SqliteDataReader reader,
        int ordinal,
        string entityKind)
    {
        if (reader.GetValue(ordinal) is not long value)
        {
            throw InvalidStoredData(entityKind);
        }

        return value;
    }

    internal static InvalidOperationException InvalidStoredData(string entityKind) =>
        new($"Stored {entityKind} data is invalid.");

    internal static void AddText(SqliteCommand command, string name, string value)
    {
        command.Parameters.Add(name, SqliteType.Text).Value = value;
    }

    internal static void AddNullableText(
        SqliteCommand command,
        string name,
        string? value)
    {
        command.Parameters.Add(name, SqliteType.Text).Value = value ?? (object)DBNull.Value;
    }

    internal static void AddInteger(SqliteCommand command, string name, long value)
    {
        command.Parameters.Add(name, SqliteType.Integer).Value = value;
    }

    internal static void AddBlob(SqliteCommand command, string name, byte[] value)
    {
        command.Parameters.Add(name, SqliteType.Blob).Value = value;
    }

    private static void ValidateRequiredMigrations(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, name
            FROM schema_migrations
            ORDER BY version;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!ReadExpectedMigration(reader, 1, "001_initial") ||
            !ReadExpectedMigration(reader, 2, "002_jobs") ||
            !ReadExpectedMigration(reader, 3, "003_audit_chain") ||
            !ReadExpectedMigration(reader, 4, "004_role_assignments") ||
            !ReadExpectedMigration(reader, 5, "005_term_entries") ||
            !ReadExpectedMigration(reader, 6, "006_ingest_workflows") ||
            !ReadExpectedMigration(reader, 7, "007_project_creation_drafts") ||
            !ReadExpectedMigration(reader, 8, "008_default_settings") ||
            !ReadExpectedMigration(reader, 9, "009_automated_evaluation") ||
            !ReadExpectedMigration(reader, 10, "010_quick_start_workflows") ||
            !ReadExpectedMigration(reader, 11, "011_source_only_executions") ||
            !ReadExpectedMigration(reader, 12, "012_subtitle_edits") ||
            !ReadExpectedMigration(reader, 13, "013_project_deletions") ||
            !ReadExpectedMigration(reader, 14, "014_project_display_names") ||
            reader.Read())
        {
            throw new InvalidOperationException(
                "The store database does not have the required schema migrations.");
        }
    }

    private static bool ReadExpectedMigration(
        SqliteDataReader reader,
        long expectedVersion,
        string expectedName)
    {
        return reader.Read() &&
            reader.GetValue(0) is long version &&
            version == expectedVersion &&
            reader.GetValue(1) is string name &&
            string.Equals(name, expectedName, StringComparison.Ordinal);
    }

    internal static string ValidateLocalAbsolutePath(
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
}