using System.Reflection;
using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ProjectStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void CreateAndGetProjectCanonicalizesPayloadAndPreservesDerivedMetadata()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        DateTimeOffset deadline = new(2030, 2, 3, 12, 0, 0, TimeSpan.FromHours(9));
        Project project = CreateProject(
            projectId,
            PublicationScope.SpecificGroupOnly,
            deadline);

        store.CreateProject(
            project,
            " { \"z\" : 2, \"a\" : { \"d\" : 4, \"c\" : 3 } } ",
            InitialTime);

        StoredProject stored = store.GetProject(projectId)
            ?? throw new AssertFailedException("The project was not loaded.");
        Assert.AreEqual(projectId, stored.Id);
        Assert.AreEqual(0, stored.Revision);
        Assert.AreEqual(PublicationScope.SpecificGroupOnly, stored.PublicationScope);
        Assert.AreEqual(deadline.ToUniversalTime(), stored.Deadline);
        Assert.AreEqual(InitialTime, stored.UpdatedAt);
        Assert.AreEqual("{\"a\":{\"c\":3,\"d\":4},\"z\":2}", stored.CanonicalJson);
        Assert.IsNull(store.GetProject(EntityId<ProjectIdTag>.New()));
    }

    [TestMethod]
    public void PayloadValidationRejectsDeepDuplicatesSurrogatesTrailingContentAndNonObjects()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        string[] invalidPayloads =
        [
            "{\"outer\":{\"key\":1,\"key\":2}}",
            "{\"outer\":[{\"key\":1,\"\\u006bey\":2}]}",
            "{\"value\":\"\uD800\"}",
            "{\"value\":1} trailing",
            "42",
            "[]",
        ];

        foreach (string payload in invalidPayloads)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => store.CreateProject(
                    CreateProject(),
                    payload,
                    InitialTime));
            Assert.AreEqual("payloadJson", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(payload, StringComparison.Ordinal));
        }

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM projects;"));
    }

    [TestMethod]
    public async Task ConcurrentProjectUpdatesAcrossStoresAllowExactlyOneRevisionWinner()
    {
        using StoreTestDatabase database = new();
        var firstStore = new ProjectStore(database.DatabasePath);
        var secondStore = new ProjectStore(database.DatabasePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        firstStore.CreateProject(CreateProject(projectId), "{\"value\":0}", InitialTime);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryUpdateProject(
                firstStore,
                CreateProject(projectId, PublicationScope.ExternalCandidate),
                "{\"value\":1}",
                release));
        Task<bool> second = Task.Run(
            () => TryUpdateProject(
                secondStore,
                CreateProject(projectId, PublicationScope.PublicationForbidden),
                "{\"value\":2}",
                release));
        release.Set();
        bool[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(static won => won));
        StoredProject stored = firstStore.GetProject(projectId)
            ?? throw new AssertFailedException("The project was not loaded.");
        Assert.AreEqual(1, stored.Revision);
        Assert.IsTrue(
            stored.CanonicalJson is "{\"value\":1}" or "{\"value\":2}");

        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => secondStore.UpdateProject(
                CreateProject(projectId),
                "{\"value\":3}",
                expectedRevision: 0,
                InitialTime.AddMinutes(2)));
        Assert.AreEqual("project", stale.EntityKind);
        Assert.AreEqual(projectId.ToString(), stale.EntityId);
        Assert.AreEqual(0, stale.ExpectedRevision);
        Assert.AreEqual(1, stale.ActualRevision);
    }

    [TestMethod]
    public void ReverseProjectTimeRollsBackPayloadMetadataAndRevision()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project original = CreateProject();
        store.CreateProject(original, "{\"safe\":true}", InitialTime);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.UpdateProject(
                CreateProject(original.Id, PublicationScope.ExternalCandidate),
                "{\"safe\":false}",
                expectedRevision: 0,
                InitialTime.AddTicks(-1)));

        StoredProject stored = store.GetProject(original.Id)
            ?? throw new AssertFailedException("The project was not loaded.");
        Assert.AreEqual(0, stored.Revision);
        Assert.AreEqual(PublicationScope.InternalOnly, stored.PublicationScope);
        Assert.AreEqual("{\"safe\":true}", stored.CanonicalJson);
    }

    [TestMethod]
    public void WorkingCopyEnforcesOneDraftPerProjectAndCrossProjectBaseVersionForeignKey()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project firstProject = CreateProject();
        Project secondProject = CreateProject();
        store.CreateProject(firstProject, "{\"project\":1}", InitialTime);
        store.CreateProject(secondProject, "{\"project\":2}", InitialTime);
        CompositionVersion firstVersion = CreateVersion(firstProject.Id);
        store.AppendCompositionVersion(firstVersion, "{\"version\":1}");
        WorkingCopy firstDraft = CreateWorkingCopy(firstProject.Id, firstVersion.Id);
        store.CreateWorkingCopy(firstDraft, "{\"draft\":1}");

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.CreateWorkingCopy(
                CreateWorkingCopy(firstProject.Id, firstVersion.Id),
                "{\"draft\":2}"));
        Assert.AreEqual(19, duplicate.SqliteErrorCode);

        SqliteException crossProject = Assert.ThrowsExactly<SqliteException>(
            () => store.CreateWorkingCopy(
                CreateWorkingCopy(secondProject.Id, firstVersion.Id),
                "{\"draft\":3}"));
        Assert.AreEqual(19, crossProject.SqliteErrorCode);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM working_copies;"));
        Assert.AreEqual("{\"draft\":1}", store.GetWorkingCopy(firstProject.Id)!.CanonicalJson);
        Assert.IsNull(store.GetWorkingCopy(secondProject.Id));
    }

    [TestMethod]
    public void WorkingCopyUpdateUsesRevisionIdentityProjectAndMonotonicTime()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        WorkingCopy workingCopy = CreateWorkingCopy(project.Id, baseVersionId: null);
        store.CreateWorkingCopy(workingCopy, "{\"draft\":0}");
        workingCopy.ReplaceElements([], InitialTime.AddMinutes(1));

        Assert.AreEqual(1, store.UpdateWorkingCopy(workingCopy, "{\"draft\":1}", 0));
        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => store.UpdateWorkingCopy(workingCopy, "{\"draft\":2}", 0));
        Assert.AreEqual(1, stale.ActualRevision);

        WorkingCopy reverse = new(
            workingCopy.Id,
            project.Id,
            null,
            InitialTime.AddMinutes(-1),
            InitialTime,
            []);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.UpdateWorkingCopy(reverse, "{\"draft\":3}", 1));
        StoredWorkingCopy stored = store.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(1, stored.Revision);
        Assert.AreEqual("{\"draft\":1}", stored.CanonicalJson);

        WorkingCopy wrongProject = new(
            workingCopy.Id,
            EntityId<ProjectIdTag>.New(),
            null,
            InitialTime,
            InitialTime.AddMinutes(2),
            []);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.UpdateWorkingCopy(wrongProject, "{\"draft\":4}", 1));
    }

    [TestMethod]
    public void CompositionVersionsAppendAndLoadAsImmutableCanonicalSnapshots()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        CompositionVersion parent = CreateVersion(project.Id);
        CompositionVersion child = CreateVersion(project.Id, parent.Id);
        store.AppendCompositionVersion(parent, "{\"z\":2,\"a\":1}");
        store.AppendCompositionVersion(child, "{\"child\":true}");

        StoredCompositionVersion stored = store.GetCompositionVersion(child.Id)
            ?? throw new AssertFailedException("The composition version was not loaded.");
        Assert.AreEqual(child.Id, stored.Id);
        Assert.AreEqual(project.Id, stored.ProjectId);
        Assert.AreEqual(parent.Id, stored.ParentVersionId);
        Assert.AreEqual(child.ContentDigest, stored.ContentDigest);
        Assert.AreEqual(child.CreatedAt, stored.CreatedAt);
        Assert.AreEqual("{\"child\":true}", stored.CanonicalJson);

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.AppendCompositionVersion(child, "{\"changed\":true}"));
        Assert.AreEqual(19, duplicate.SqliteErrorCode);
        Assert.AreEqual("{\"child\":true}", store.GetCompositionVersion(child.Id)!.CanonicalJson);
    }

    [TestMethod]
    public void CheckpointDuplicateVersionIdRollsBackWorkingCopy()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        CompositionVersion existing = CreateVersion(project.Id);
        store.AppendCompositionVersion(existing, "{\"existing\":true}");
        WorkingCopy originalDraft = CreateWorkingCopy(project.Id, baseVersionId: null);
        store.CreateWorkingCopy(originalDraft, "{\"draft\":0}");
        var updatedDraft = new WorkingCopy(
            originalDraft.Id,
            project.Id,
            existing.Id,
            originalDraft.CreatedAt,
            existing.CreatedAt,
            elements: []);

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(
            () => store.CheckpointWorkingCopy(
                existing,
                "{\"attempt\":true}",
                originalDraft,
                updatedDraft,
                "{\"draft\":1}",
                expectedWorkingCopyRevision: 0));

        Assert.AreEqual(19, duplicate.SqliteErrorCode);
        StoredWorkingCopy storedDraft = store.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(0, storedDraft.Revision);
        Assert.IsNull(storedDraft.BaseVersionId);
        Assert.AreEqual("{\"draft\":0}", storedDraft.CanonicalJson);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
        Assert.AreEqual("{\"existing\":true}", store.GetCompositionVersion(existing.Id)!.CanonicalJson);
    }

    [TestMethod]
    public void CheckpointParentForeignKeyFailureRollsBackWorkingCopy()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        WorkingCopy originalDraft = CreateWorkingCopy(project.Id, baseVersionId: null);
        store.CreateWorkingCopy(originalDraft, "{\"draft\":0}");
        EntityId<CompositionVersionIdTag> missingParentId =
            EntityId<CompositionVersionIdTag>.New();
        using (SqliteConnection connection = database.OpenConnection())
        {
            using SqliteCommand disableForeignKeys = connection.CreateCommand();
            disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            disableForeignKeys.ExecuteNonQuery();
            using SqliteCommand injectMissingBase = connection.CreateCommand();
            injectMissingBase.CommandText = """
                UPDATE working_copies
                SET base_version_id = $base_version_id
                WHERE id = $id;
                """;
            injectMissingBase.Parameters.AddWithValue(
                "$base_version_id",
                missingParentId.ToString());
            injectMissingBase.Parameters.AddWithValue("$id", originalDraft.Id.ToString());
            Assert.AreEqual(1, injectMissingBase.ExecuteNonQuery());
        }

        CompositionVersion version = CreateVersion(project.Id, missingParentId);
        var expectedDraft = new WorkingCopy(
            originalDraft.Id,
            project.Id,
            missingParentId,
            originalDraft.CreatedAt,
            originalDraft.UpdatedAt,
            elements: []);
        var updatedDraft = new WorkingCopy(
            originalDraft.Id,
            project.Id,
            version.Id,
            originalDraft.CreatedAt,
            version.CreatedAt,
            elements: []);

        SqliteException foreignKey = Assert.ThrowsExactly<SqliteException>(
            () => store.CheckpointWorkingCopy(
                version,
                "{\"version\":true}",
                expectedDraft,
                updatedDraft,
                "{\"draft\":1}",
                expectedWorkingCopyRevision: 0));

        Assert.AreEqual(19, foreignKey.SqliteErrorCode);
        StoredWorkingCopy storedDraft = store.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(0, storedDraft.Revision);
        Assert.AreEqual(missingParentId, storedDraft.BaseVersionId);
        Assert.AreEqual("{\"draft\":0}", storedDraft.CanonicalJson);
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
        Assert.IsNull(store.GetCompositionVersion(version.Id));
    }

    [TestMethod]
    public void CheckpointWorkingCopyUpdateFailureRollsBackInsertedVersion()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        WorkingCopy originalDraft = CreateWorkingCopy(project.Id, baseVersionId: null);
        store.CreateWorkingCopy(originalDraft, "{\"draft\":0}");
        CompositionVersion version = CreateVersion(project.Id);
        var updatedDraft = new WorkingCopy(
            originalDraft.Id,
            project.Id,
            version.Id,
            originalDraft.CreatedAt,
            version.CreatedAt,
            elements: []);
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_checkpoint_working_copy_update
            BEFORE UPDATE ON working_copies
            BEGIN
                SELECT RAISE(ABORT, 'injected working-copy update failure');
            END;
            """);

        SqliteException injectedFailure = Assert.ThrowsExactly<SqliteException>(
            () => store.CheckpointWorkingCopy(
                version,
                "{\"z\":2,\"a\":1}",
                originalDraft,
                updatedDraft,
                "{\"draft\":1}",
                expectedWorkingCopyRevision: 0));

        Assert.AreEqual(19, injectedFailure.SqliteErrorCode);
        Assert.IsNull(store.GetCompositionVersion(version.Id));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
        StoredWorkingCopy storedDraft = store.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(0, storedDraft.Revision);
        Assert.IsNull(storedDraft.BaseVersionId);
        Assert.AreEqual("{\"draft\":0}", storedDraft.CanonicalJson);
    }

    [TestMethod]
    public void NonCanonicalOrInvalidStoredPayloadAndTimestampFailClosed()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        store.CreateProject(project, "{\"a\":1}", InitialTime);

        database.ExecuteNonQuery(
            "UPDATE projects SET payload = $payload WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes("{ \"a\" : 1 }")),
            ("$id", project.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetProject(project.Id));

        database.ExecuteNonQuery(
            "UPDATE projects SET payload = $payload, updated_at = $updated_at WHERE id = $id;",
            ("$payload", Encoding.UTF8.GetBytes("{\"a\":1}")),
            ("$updated_at", "2030-01-02T12:04:05.0000000+09:00"),
            ("$id", project.Id.ToString()));
        Assert.AreEqual(
            TimeSpan.FromHours(9),
            store.GetProject(project.Id)!.UpdatedAt.Offset);

        database.ExecuteNonQuery(
            "UPDATE projects SET updated_at = $updated_at WHERE id = $id;",
            ("$updated_at", "2030-02-30T12:04:05.0000000+09:00"),
            ("$id", project.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetProject(project.Id));
    }

    [TestMethod]
    public void QuotedPayloadTextIsBoundAsDataAndCannotChangeSchema()
    {
        using StoreTestDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        Project project = CreateProject();
        const string payload =
            "{\"text\":\"Robert'); DROP TABLE projects;-- \\\"quoted\\\"\"}";

        store.CreateProject(project, payload, InitialTime);

        Assert.AreEqual(
            CanonicalJson.Canonicalize(payload),
            store.GetProject(project.Id)!.CanonicalJson);
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM projects;"));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'projects';"));
    }

    [TestMethod]
    public void ConstructorsRejectUnsafePathsMissingFilesAndMissingRequiredMigrations()
    {
        using StoreTestDatabase database = new();
        string[] invalidPaths =
        [
            "relative.db",
            @"\\server\share\store.db",
            @"\\?\C:\store.db",
            @"C:\temp\store.db:stream",
        ];
        foreach (string path in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ProjectStore(path));
            Assert.AreEqual("databasePath", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(path, StringComparison.Ordinal));
        }

        string missing = Path.Combine(database.RootPath, "missing.db");
        Assert.ThrowsExactly<InvalidOperationException>(() => new ProjectStore(missing));
        Assert.IsFalse(File.Exists(missing));

        string unmigrated = Path.Combine(database.RootPath, "unmigrated.db");
        using (SqliteConnection connection = OpenCreatedConnection(unmigrated))
        {
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => new ProjectStore(unmigrated));
    }

    [TestMethod]
    public void ConstructorsRequireExactlyNamedVersionsOneThroughThirteen()
    {
        using (StoreTestDatabase wrongNameDatabase = new())
        {
            wrongNameDatabase.ExecuteNonQuery(
                "UPDATE schema_migrations SET name = $name WHERE version = 1;",
                ("$name", "001_wrong"));
            Assert.ThrowsExactly<InvalidOperationException>(
                () => new ProjectStore(wrongNameDatabase.DatabasePath));
        }

        using StoreTestDatabase extraVersionDatabase = new();
        extraVersionDatabase.ExecuteNonQuery(
            """
            INSERT INTO schema_migrations (version, name, content_digest, applied_at)
            VALUES (14, $name, $digest, $applied_at);
            """,
            ("$name", "014_unexpected"),
            ("$digest", new string('a', 64)),
            ("$applied_at", "2030-01-02T03:04:05.0000000Z"));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new ProjectStore(extraVersionDatabase.DatabasePath));
    }

    [TestMethod]
    public void PublicStoreSurfaceDoesNotDefineAGenericRepositoryOrInterface()
    {
        Type[] exported = typeof(ProjectStore).Assembly.GetExportedTypes();

        Assert.IsFalse(exported.Any(static type =>
            type.IsInterface && type.Name.Contains("Repository", StringComparison.Ordinal)));
        Assert.IsFalse(exported.Any(static type =>
            type.IsGenericTypeDefinition &&
            type.Name.Contains("Repository", StringComparison.Ordinal)));
        foreach (Type storeType in new[]
        {
            typeof(ProjectStore),
            typeof(JobStore),
            typeof(ReviewStore),
            typeof(RoleAssignmentStore),
            typeof(TermStore),
            typeof(ProjectCreationDraftStore),
            typeof(DefaultSettingsStore),
            typeof(AutomatedEvaluationStore),
            typeof(QuickStartWorkflowStore),
        })
        {
            Assert.IsTrue(storeType.IsPublic);
            Assert.IsTrue(storeType.IsSealed);
            Assert.AreEqual(0, storeType.GetInterfaces().Length);
            Assert.IsFalse(storeType.IsGenericType);
        }

        Assert.IsTrue(typeof(StoreConcurrencyException).IsPublic);
        Assert.IsTrue(typeof(StoreConcurrencyException).IsSealed);
        MethodInfo checkpoint = typeof(ProjectStore).GetMethod(
            nameof(ProjectStore.CheckpointWorkingCopy),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [
                typeof(CompositionVersion),
                typeof(string),
                typeof(WorkingCopy),
                typeof(WorkingCopy),
                typeof(string),
                typeof(int),
                typeof(CancellationToken),
            ],
            modifiers: null)
            ?? throw new AssertFailedException("The specialized checkpoint method is missing.");
        Assert.AreEqual(typeof(int), checkpoint.ReturnType);
        Assert.IsFalse(typeof(ProjectStore).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(static method => method.Name.Contains("Transaction", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(
            new[] { "EntityKind", "EntityId", "ExpectedRevision", "ActualRevision" },
            typeof(StoreConcurrencyException)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
    }

    private static bool TryUpdateProject(
        ProjectStore store,
        Project project,
        string payload,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.UpdateProject(
                project,
                payload,
                expectedRevision: 0,
                InitialTime.AddMinutes(1));
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static Project CreateProject(
        EntityId<ProjectIdTag>? id = null,
        PublicationScope scope = PublicationScope.InternalOnly,
        DateTimeOffset? deadline = null)
    {
        return new Project(
            id ?? EntityId<ProjectIdTag>.New(),
            "Episode \"One\"",
            "Create a safe movie.",
            "Internal reviewers",
            scope,
            deadline,
            assets: [],
            performers: []);
    }

    private static WorkingCopy CreateWorkingCopy(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? baseVersionId)
    {
        return new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            projectId,
            baseVersionId,
            InitialTime,
            InitialTime,
            elements: []);
    }

    private static CompositionVersion CreateVersion(
        EntityId<ProjectIdTag> projectId,
        EntityId<CompositionVersionIdTag>? parentVersionId = null)
    {
        return new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            projectId,
            parentVersionId,
            "S-1-5-21-1000",
            InitialTime,
            "Create immutable version.",
            new ContentDigest(new string(parentVersionId is null ? 'a' : 'b', 64)),
            elements: []);
    }

    private static SqliteConnection OpenCreatedConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }
}