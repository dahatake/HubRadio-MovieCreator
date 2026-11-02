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
