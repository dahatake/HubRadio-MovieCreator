using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

internal sealed class StoreTestDatabase : IDisposable
{
    internal StoreTestDatabase()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.StoreTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        DatabasePath = Path.Combine(RootPath, "store.db");
        BackupDirectory = Path.Combine(RootPath, "backups");
        _ = Database.DatabaseMigrator.Migrate(DatabasePath, BackupDirectory);
    }

    internal string RootPath { get; }

    internal string DatabasePath { get; }

    internal string BackupDirectory { get; }

    internal SqliteConnection OpenConnection(
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString())
        {
            DefaultTimeout = 5,
        };
        connection.Open();
        return connection;
    }

    internal int ExecuteNonQuery(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    internal long ExecuteScalarInt64(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return (long)(command.ExecuteScalar()
            ?? throw new InvalidOperationException("The scalar query returned null."));
    }

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
}