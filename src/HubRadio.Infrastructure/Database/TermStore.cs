using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Database;

public sealed class TermStore
{
    private const string EntityKind = "term entry";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _databasePath;

    public TermStore(string databasePath)
    {
        _databasePath = SqliteStoreSupport.Initialize(databasePath);
    }

    public void CreateTermEntry(TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Version != 0)
        {
            throw new ArgumentException(
                "A new term entry must have version zero.",
                nameof(entry));
        }

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO term_entries (
                id,
                project_id,
                series_id,
                written_form,
                reading,
                version
            )
            VALUES (
                $id,
                $project_id,
                $series_id,
                $written_form,
                $reading,
                0
            );
            """;
        AddEntryParameters(command, entry);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The term entry was not stored.");
        }

        transaction.Commit();
    }

    public TermEntry? GetTermEntry(EntityId<TermEntryIdTag> entryId)
    {
        ArgumentNullException.ThrowIfNull(entryId);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectColumns + """
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", entryId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        TermEntry entry = ReadTermEntry(reader);
        if (reader.Read() || entry.Id != entryId)
        {
            throw InvalidStoredEntry();
        }

        return entry;
    }

    public IReadOnlyList<TermEntry> ListTermEntries(TermScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = scope.Kind switch
        {
            TermScopeKind.Project => SelectColumns + """
                WHERE project_id = $scope_id AND series_id IS NULL
                ORDER BY
                    CAST(written_form AS BLOB),
                    CAST(reading AS BLOB),
                    id;
                """,
            TermScopeKind.Series => SelectColumns + """
                WHERE project_id IS NULL AND series_id = $scope_id
                ORDER BY
                    CAST(written_form AS BLOB),
                    CAST(reading AS BLOB),
                    id;
                """,
            _ => throw new InvalidOperationException("The term scope kind is invalid."),
        };
        SqliteStoreSupport.AddText(command, "$scope_id", GetScopeId(scope));
        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<TermEntry>();
        while (reader.Read())
        {
            TermEntry entry = ReadTermEntry(reader);
            if (entry.Scope != scope)
            {
                throw InvalidStoredEntry();
            }

            entries.Add(entry);
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    public int UpdateTermEntry(TermEntry entry, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateExpectedVersion(expectedVersion);
        if (entry.Version != expectedVersion)
        {
            throw new ArgumentException(
                "The supplied term-entry version must match the expected version.",
                nameof(entry));
        }

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        TermEntry current = ReadExistingTermEntry(connection, transaction, entry.Id);
        EnsureExpectedVersion(entry.Id, expectedVersion, current.Version);

        int newVersion = checked(current.Version + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE term_entries
            SET project_id = $project_id,
                series_id = $series_id,
                written_form = $written_form,
                reading = $reading,
                version = $new_version
            WHERE id = $id AND version = $expected_version;
            """;
        AddEntryParameters(command, entry);
        SqliteStoreSupport.AddInteger(command, "$new_version", newVersion);
        SqliteStoreSupport.AddInteger(command, "$expected_version", expectedVersion);
        if (command.ExecuteNonQuery() != 1)
        {
            throw InvalidStoredEntry();
        }

        transaction.Commit();
        return newVersion;
    }

    public void DeleteTermEntry(
        EntityId<TermEntryIdTag> entryId,
        int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        ValidateExpectedVersion(expectedVersion);

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(_databasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        TermEntry current = ReadExistingTermEntry(connection, transaction, entryId);
        EnsureExpectedVersion(entryId, expectedVersion, current.Version);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM term_entries
            WHERE id = $id AND version = $expected_version;
            """;
        SqliteStoreSupport.AddText(command, "$id", entryId.ToString());
        SqliteStoreSupport.AddInteger(command, "$expected_version", expectedVersion);
        if (command.ExecuteNonQuery() != 1)
        {
            throw InvalidStoredEntry();
        }

        transaction.Commit();
    }

    private const string SelectColumns = """
        SELECT
            id,
            project_id,
            series_id,
            CAST(written_form AS BLOB),
            CAST(reading AS BLOB),
            version
        FROM term_entries
        """ + "\n";

    private static TermEntry ReadExistingTermEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EntityId<TermEntryIdTag> entryId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + """
            WHERE id = $id;
            """;
        SqliteStoreSupport.AddText(command, "$id", entryId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The term entry does not exist.");
        }

        TermEntry entry = ReadTermEntry(reader);
        if (reader.Read() || entry.Id != entryId)
        {
            throw InvalidStoredEntry();
        }

        return entry;
    }

    private static TermEntry ReadTermEntry(SqliteDataReader reader)
    {
        EntityId<TermEntryIdTag> id =
            SqliteStoreSupport.ReadEntityId<TermEntryIdTag>(reader, 0, EntityKind);
        EntityId<ProjectIdTag>? projectId =
            SqliteStoreSupport.ReadNullableEntityId<ProjectIdTag>(reader, 1, EntityKind);
        EntityId<SeriesIdTag>? seriesId =
            SqliteStoreSupport.ReadNullableEntityId<SeriesIdTag>(reader, 2, EntityKind);
        string writtenForm = ReadStrictText(reader, 3);
        string reading = ReadStrictText(reader, 4);
        int version = SqliteStoreSupport.ReadInt32(reader, 5, EntityKind);
        if (version < 0 || (projectId is null) == (seriesId is null))
        {
            throw InvalidStoredEntry();
        }

        TermScope scope = projectId is not null
            ? TermScope.ForProject(projectId)
            : TermScope.ForSeries(seriesId!);
        try
        {
            return new TermEntry(id, scope, writtenForm, reading, version);
        }
        catch (ArgumentException)
        {
            throw InvalidStoredEntry();
        }
    }

    private static string ReadStrictText(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not byte[] bytes || bytes.Length == 0)
        {
            throw InvalidStoredEntry();
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidStoredEntry();
        }
    }

    private static void AddEntryParameters(SqliteCommand command, TermEntry entry)
    {
        SqliteStoreSupport.AddText(command, "$id", entry.Id.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$project_id",
            entry.Scope.ProjectId?.ToString());
        SqliteStoreSupport.AddNullableText(
            command,
            "$series_id",
            entry.Scope.SeriesId?.ToString());
        SqliteStoreSupport.AddText(command, "$written_form", entry.WrittenForm);
        SqliteStoreSupport.AddText(command, "$reading", entry.Reading);
    }

    private static string GetScopeId(TermScope scope)
    {
        return scope.Kind switch
        {
            TermScopeKind.Project => scope.ProjectId?.ToString()
                ?? throw new InvalidOperationException("The project term scope is invalid."),
            TermScopeKind.Series => scope.SeriesId?.ToString()
                ?? throw new InvalidOperationException("The series term scope is invalid."),
            _ => throw new InvalidOperationException("The term scope kind is invalid."),
        };
    }

    private static void ValidateExpectedVersion(int expectedVersion)
    {
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedVersion),
                expectedVersion,
                "An expected term-entry version must not be negative.");
        }
    }

    private static void EnsureExpectedVersion(
        EntityId<TermEntryIdTag> entryId,
        int expectedVersion,
        int actualVersion)
    {
        if (expectedVersion != actualVersion)
        {
            throw new StoreConcurrencyException(
                EntityKind,
                entryId.ToString(),
                expectedVersion,
                actualVersion);
        }
    }

    private static InvalidOperationException InvalidStoredEntry() =>
        SqliteStoreSupport.InvalidStoredData(EntityKind);
}