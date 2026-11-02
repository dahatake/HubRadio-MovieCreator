using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

// FR-132 / AC-136 / AC-137: concurrent writers on separate connections must wait for the lock (busy timeout)
// instead of failing with SQLITE_BUSY.
[TestClass]
public sealed class SqliteStoreBusyTimeoutTests
{
    [TestMethod]
    public void SecondWriterWaitsForFirstWriterInsteadOfFailingBusy()
    {
        using StoreTestDatabase database = new();
        using SqliteConnection first = SqliteStoreSupport.OpenConnection(database.DatabasePath);
        using SqliteConnection second = SqliteStoreSupport.OpenConnection(database.DatabasePath);
        Assert.IsGreaterThan(5, second.DefaultTimeout);

        SqliteTransaction held = first.BeginTransaction(deferred: false);
        var release = Task.Run(() => { Thread.Sleep(1500); held.Commit(); });

        using SqliteTransaction waiting = second.BeginTransaction(deferred: false);
        waiting.Commit();
        release.Wait();
        held.Dispose();
    }

    // FR-063 / AC-038, FR-140 / AC-165: a Job's later writes failed with "attempt to write a readonly database"
    // while another process briefly held hubradio.db without write sharing (SQLite silently opens read-only).
    [TestMethod]
    public void WriterWaitsForTransientSharingLockInsteadOfOpeningReadOnly()
    {
        using StoreTestDatabase database = new();
        FileStream holder = new(database.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(() => { Thread.Sleep(1000); holder.Dispose(); });

        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(database.DatabasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sharing_probe (a INTEGER); INSERT INTO sharing_probe VALUES (1);";
        _ = command.ExecuteNonQuery();
        release.Wait();
    }

    [TestMethod]
    public void WriterOnReadOnlyAttributeFileFailsPromptlyWithoutMaskingIt()
    {
        using StoreTestDatabase database = new();
        File.SetAttributes(database.DatabasePath, FileAttributes.ReadOnly);
        try
        {
            using SqliteConnection connection = SqliteStoreSupport.OpenConnection(database.DatabasePath);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE sharing_probe (a INTEGER);";
            SqliteException error = Assert.ThrowsExactly<SqliteException>(() => command.ExecuteNonQuery());
            Assert.AreEqual(8, error.SqliteErrorCode);
        }
        finally
        {
            File.SetAttributes(database.DatabasePath, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public void BusyWriterReportsSqliteErrorCodeBusy()
    {
        using StoreTestDatabase database = new();
        using SqliteConnection first = SqliteStoreSupport.OpenConnection(database.DatabasePath);
        using SqliteConnection second = SqliteStoreSupport.OpenConnection(database.DatabasePath);
        second.DefaultTimeout = 1;
        using SqliteTransaction held = first.BeginTransaction(deferred: false);

        SqliteException error = Assert.ThrowsExactly<SqliteException>(() => second.BeginTransaction(deferred: false));
        Assert.AreEqual(5, error.SqliteErrorCode);
    }
}
