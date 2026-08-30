using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class TermStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void CreateGetAndListPreserveTypedScopesTextVersionAndDeterministicOrder()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        EntityId<SeriesIdTag> seriesId = EntityId<SeriesIdTag>.New();
        TermEntry secondProjectEntry = CreateEntry(
            TermScope.ForProject(projectId),
            "Beta",
            "ベータ");
        TermEntry firstProjectEntry = CreateEntry(
            TermScope.ForProject(projectId),
            "Alpha",
            "アルファ");
        TermEntry seriesEntry = CreateEntry(
            TermScope.ForSeries(seriesId),
            "Alpha",
            "アルファ");

        store.CreateTermEntry(secondProjectEntry);
        store.CreateTermEntry(seriesEntry);
        store.CreateTermEntry(firstProjectEntry);

        TermEntry loaded = store.GetTermEntry(firstProjectEntry.Id)
            ?? throw new AssertFailedException("The term entry was not loaded.");
        Assert.AreEqual(firstProjectEntry.Id, loaded.Id);
        Assert.AreEqual(TermScope.ForProject(projectId), loaded.Scope);
        Assert.AreEqual("Alpha", loaded.WrittenForm);
        Assert.AreEqual("アルファ", loaded.Reading);
        Assert.AreEqual(0, loaded.Version);
        Assert.IsNull(store.GetTermEntry(EntityId<TermEntryIdTag>.New()));

        IReadOnlyList<TermEntry> projectEntries = store.ListTermEntries(
            TermScope.ForProject(projectId));
        CollectionAssert.AreEqual(
            new[] { firstProjectEntry.Id, secondProjectEntry.Id },
            projectEntries.Select(static entry => entry.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { seriesEntry.Id },
            store.ListTermEntries(TermScope.ForSeries(seriesId))
                .Select(static entry => entry.Id)
                .ToArray());
        Assert.AreEqual(
            0,
            store.ListTermEntries(TermScope.ForSeries(EntityId<SeriesIdTag>.New())).Count);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<TermEntry>)projectEntries).Add(seriesEntry));
    }

    [TestMethod]
    public void CreateRequiresVersionZeroAndRejectsNullBeforeMutation()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);

        Assert.ThrowsExactly<ArgumentNullException>(() => store.CreateTermEntry(null!));
        ArgumentException nonzero = Assert.ThrowsExactly<ArgumentException>(
            () => store.CreateTermEntry(CreateEntry(
                TermScope.ForProject(projectId),
                "表記",
                "よみ",
                version: 1)));
        Assert.AreEqual("entry", nonzero.ParamName);
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM term_entries;"));
    }

    [TestMethod]
    public void DuplicateRuleIsExactAndLocalToTypedScope()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> firstProjectId = CreateProject(database);
        EntityId<ProjectIdTag> secondProjectId = CreateProject(database);
        Guid sharedScopeValue = Guid.NewGuid();
        EntityId<ProjectIdTag> sharedProjectId = EntityId<ProjectIdTag>.From(sharedScopeValue);
        CreateProject(database, sharedProjectId);
        EntityId<SeriesIdTag> sharedSeriesId = EntityId<SeriesIdTag>.From(sharedScopeValue);

        store.CreateTermEntry(CreateEntry(
            TermScope.ForProject(firstProjectId),
            "HubRadio",
            "ハブラジオ"));
        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.CreateTermEntry(CreateEntry(
                TermScope.ForProject(firstProjectId),
                "HubRadio",
                "ハブラジオ")));
        Assert.AreEqual(19, duplicate.SqliteErrorCode);

        store.CreateTermEntry(CreateEntry(
            TermScope.ForProject(firstProjectId),
            "HubRadio",
            "ハブレディオ"));
        store.CreateTermEntry(CreateEntry(
            TermScope.ForProject(secondProjectId),
            "HubRadio",
            "ハブラジオ"));
        store.CreateTermEntry(CreateEntry(
            TermScope.ForProject(sharedProjectId),
            "HubRadio",
            "ハブラジオ"));
        store.CreateTermEntry(CreateEntry(
            TermScope.ForSeries(sharedSeriesId),
            "HubRadio",
            "ハブラジオ"));

        Assert.AreEqual(5L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM term_entries;"));
    }

    [TestMethod]
    public void ProjectScopeRequiresExistingProjectAndRestrictsProjectDeletion()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> missingProjectId = EntityId<ProjectIdTag>.New();

        SqliteException missingProject = Assert.ThrowsExactly<SqliteException>(
            () => store.CreateTermEntry(CreateEntry(
                TermScope.ForProject(missingProjectId),
                "表記",
                "よみ")));
        Assert.AreEqual(19, missingProject.SqliteErrorCode);

        EntityId<ProjectIdTag> projectId = CreateProject(database);
        store.CreateTermEntry(CreateEntry(
            TermScope.ForProject(projectId),
            "表記",
            "よみ"));
        SqliteException restrictedDelete = Assert.ThrowsExactly<SqliteException>(
            () => database.ExecuteNonQuery(
                "DELETE FROM projects WHERE id = $id;",
                ("$id", projectId.ToString())));
        Assert.AreEqual(19, restrictedDelete.SqliteErrorCode);

        EntityId<SeriesIdTag> seriesId = EntityId<SeriesIdTag>.New();
        store.CreateTermEntry(CreateEntry(
            TermScope.ForSeries(seriesId),
            "シリーズ語",
            "しりーずご"));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM term_entries;"));
    }

    [TestMethod]
    public void UpdateChangesValidatedFieldsAndScopeWhileIncrementingExactlyOneVersion()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermEntry original = CreateEntry(
            TermScope.ForProject(projectId),
            "旧表記",
            "きゅうひょうき");
        store.CreateTermEntry(original);
        TermScope newScope = TermScope.ForSeries(EntityId<SeriesIdTag>.New());
        var replacement = new TermEntry(
            original.Id,
            newScope,
            "新表記",
            "しんひょうき",
            version: 0);

        int version = store.UpdateTermEntry(replacement, expectedVersion: 0);

        Assert.AreEqual(1, version);
        TermEntry stored = store.GetTermEntry(original.Id)!;
        Assert.AreEqual(newScope, stored.Scope);
        Assert.AreEqual("新表記", stored.WrittenForm);
        Assert.AreEqual("しんひょうき", stored.Reading);
        Assert.AreEqual(1, stored.Version);
        Assert.AreEqual(0, store.ListTermEntries(TermScope.ForProject(projectId)).Count);
    }

    [TestMethod]
    public void UpdateValidatesSuppliedAndExpectedVersionsBeforeWriting()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermEntry original = CreateEntry(TermScope.ForProject(projectId), "表記", "よみ");
        store.CreateTermEntry(original);

        ArgumentOutOfRangeException negative =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => store.UpdateTermEntry(original, expectedVersion: -1));
        Assert.AreEqual("expectedVersion", negative.ParamName);
        ArgumentException mismatch = Assert.ThrowsExactly<ArgumentException>(
            () => store.UpdateTermEntry(
                CreateEntry(
                    original.Scope,
                    "変更",
                    "へんこう",
                    version: 1,
                    id: original.Id),
                expectedVersion: 0));
        Assert.AreEqual("entry", mismatch.ParamName);

        Assert.AreEqual(0, store.GetTermEntry(original.Id)!.Version);
        Assert.AreEqual("表記", store.GetTermEntry(original.Id)!.WrittenForm);
    }

    [TestMethod]
    public void StaleUpdateAndDeleteExposeTypedConflictWithoutMutatingWinner()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermEntry original = CreateEntry(TermScope.ForProject(projectId), "表記", "よみ");
        store.CreateTermEntry(original);
        var firstUpdate = new TermEntry(
            original.Id,
            original.Scope,
            "表記1",
            "よみいち",
            version: 0);
        Assert.AreEqual(1, store.UpdateTermEntry(firstUpdate, expectedVersion: 0));

        StoreConcurrencyException staleUpdate =
            Assert.ThrowsExactly<StoreConcurrencyException>(
                () => store.UpdateTermEntry(
                    new TermEntry(
                        original.Id,
                        original.Scope,
                        "表記2",
                        "よみに",
                        version: 0),
                    expectedVersion: 0));
        AssertConflict(staleUpdate, original.Id, expected: 0, expectedActual: 1);
        StoreConcurrencyException staleDelete =
            Assert.ThrowsExactly<StoreConcurrencyException>(
                () => store.DeleteTermEntry(original.Id, expectedVersion: 0));
        AssertConflict(staleDelete, original.Id, expected: 0, expectedActual: 1);

        TermEntry winner = store.GetTermEntry(original.Id)!;
        Assert.AreEqual("表記1", winner.WrittenForm);
        Assert.AreEqual(1, winner.Version);
    }

    [TestMethod]
    public async Task ConcurrentUpdatesAcrossStoresAllowExactlyOneVersionWinner()
    {
        using StoreTestDatabase database = new();
        var firstStore = new TermStore(database.DatabasePath);
        var secondStore = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermEntry original = CreateEntry(TermScope.ForProject(projectId), "表記", "よみ");
        firstStore.CreateTermEntry(original);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryUpdate(
                firstStore,
                new TermEntry(
                    original.Id,
                    original.Scope,
                    "第一候補",
                    "だいいちこうほ",
                    version: 0),
                release));
        Task<bool> second = Task.Run(
            () => TryUpdate(
                secondStore,
                new TermEntry(
                    original.Id,
                    original.Scope,
                    "第二候補",
                    "だいにこうほ",
                    version: 0),
                release));
        release.Set();

        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, outcomes.Count(static won => won));
        TermEntry stored = firstStore.GetTermEntry(original.Id)!;
        Assert.AreEqual(1, stored.Version);
        Assert.IsTrue(stored.WrittenForm is "第一候補" or "第二候補");
    }

    [TestMethod]
    public void DuplicateUpdateRollsBackAllFieldsAndVersion()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermScope scope = TermScope.ForProject(projectId);
        TermEntry first = CreateEntry(scope, "第一", "だいいち");
        TermEntry second = CreateEntry(scope, "第二", "だいに");
        store.CreateTermEntry(first);
        store.CreateTermEntry(second);

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.UpdateTermEntry(
                new TermEntry(
                    second.Id,
                    scope,
                    first.WrittenForm,
                    first.Reading,
                    version: 0),
                expectedVersion: 0));

        Assert.AreEqual(19, duplicate.SqliteErrorCode);
        TermEntry unchanged = store.GetTermEntry(second.Id)!;
        Assert.AreEqual("第二", unchanged.WrittenForm);
        Assert.AreEqual("だいに", unchanged.Reading);
        Assert.AreEqual(0, unchanged.Version);
    }

    [TestMethod]
    public void DeleteUsesVersionAndRemovesOnlyTheRequestedEntry()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermScope scope = TermScope.ForProject(projectId);
        TermEntry removed = CreateEntry(scope, "削除", "さくじょ");
        TermEntry retained = CreateEntry(scope, "保持", "ほじ");
        store.CreateTermEntry(removed);
        store.CreateTermEntry(retained);

        store.DeleteTermEntry(removed.Id, expectedVersion: 0);

        Assert.IsNull(store.GetTermEntry(removed.Id));
        CollectionAssert.AreEqual(
            new[] { retained.Id },
            store.ListTermEntries(scope).Select(static entry => entry.Id).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.DeleteTermEntry(removed.Id, expectedVersion: 0));
        ArgumentOutOfRangeException negative =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => store.DeleteTermEntry(retained.Id, expectedVersion: -1));
        Assert.AreEqual("expectedVersion", negative.ParamName);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM term_entries;"));
    }

    [TestMethod]
    public void InvalidStoredUtf8TextVersionOrScopeFailsClosed()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        TermEntry entry = CreateEntry(TermScope.ForProject(projectId), "表記", "よみ");
        store.CreateTermEntry(entry);

        InjectIgnoringChecks(
            database,
            "UPDATE term_entries SET written_form = CAST(X'80' AS TEXT) WHERE id = $id;",
            entry.Id);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetTermEntry(entry.Id));

        InjectIgnoringChecks(
            database,
            "UPDATE term_entries SET written_form = '表記', version = 2147483648 WHERE id = $id;",
            entry.Id);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetTermEntry(entry.Id));

        InjectIgnoringChecks(
            database,
            "UPDATE term_entries SET version = 0, project_id = NULL, series_id = NULL WHERE id = $id;",
            entry.Id);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetTermEntry(entry.Id));
    }

    [TestMethod]
    public void QuotedTermsAreBoundAsDataAndCannotChangeSchema()
    {
        using StoreTestDatabase database = new();
        var store = new TermStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = CreateProject(database);
        const string payload = "Robert'); DROP TABLE term_entries;--";
        TermEntry entry = CreateEntry(
            TermScope.ForProject(projectId),
            payload,
            "ろばーと");

        store.CreateTermEntry(entry);

        Assert.AreEqual(payload, store.GetTermEntry(entry.Id)!.WrittenForm);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM term_entries;"));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'term_entries';"));
    }

    [TestMethod]
    public void ConstructorRejectsUnsafeMissingAndIncorrectlyMigratedDatabases()
    {
        using StoreTestDatabase database = new();
        string[] invalidPaths =
        [
            "relative.db",
            @"\\server\share\terms.db",
            @"\\?\C:\terms.db",
            @"C:\temp\terms.db:stream",
        ];
        foreach (string path in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new TermStore(path));
            Assert.AreEqual("databasePath", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(path, StringComparison.Ordinal));
        }

        string missing = Path.Combine(database.RootPath, "missing-terms.db");
        Assert.ThrowsExactly<InvalidOperationException>(() => new TermStore(missing));
        Assert.IsFalse(File.Exists(missing));

        database.ExecuteNonQuery("DELETE FROM schema_migrations WHERE version = 5;");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new TermStore(database.DatabasePath));
    }

    private static bool TryUpdate(
        TermStore store,
        TermEntry entry,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.UpdateTermEntry(entry, expectedVersion: 0);
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static void AssertConflict(
        StoreConcurrencyException exception,
        EntityId<TermEntryIdTag> entryId,
        int expected,
        int expectedActual)
    {
        Assert.AreEqual("term entry", exception.EntityKind);
        Assert.AreEqual(entryId.ToString(), exception.EntityId);
        Assert.AreEqual(expected, exception.ExpectedRevision);
        Assert.AreEqual(expectedActual, exception.ActualRevision);
    }

    private static EntityId<ProjectIdTag> CreateProject(
        StoreTestDatabase database,
        EntityId<ProjectIdTag>? projectId = null)
    {
        EntityId<ProjectIdTag> id = projectId ?? EntityId<ProjectIdTag>.New();
        var project = new Project(
            id,
            "Term dictionary test",
            "Verify term storage.",
            "Test runner",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        new ProjectStore(database.DatabasePath).CreateProject(project, "{}", InitialTime);
        return id;
    }

    private static TermEntry CreateEntry(
        TermScope scope,
        string writtenForm,
        string reading,
        int version = 0,
        EntityId<TermEntryIdTag>? id = null)
    {
        return new TermEntry(
            id ?? EntityId<TermEntryIdTag>.New(),
            scope,
            writtenForm,
            reading,
            version);
    }

    private static void InjectIgnoringChecks(
        StoreTestDatabase database,
        string sql,
        EntityId<TermEntryIdTag> entryId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using (SqliteCommand ignoreChecks = connection.CreateCommand())
        {
            ignoreChecks.CommandText = "PRAGMA ignore_check_constraints = ON;";
            ignoreChecks.ExecuteNonQuery();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", entryId.ToString());
        Assert.AreEqual(1, command.ExecuteNonQuery());
    }
}