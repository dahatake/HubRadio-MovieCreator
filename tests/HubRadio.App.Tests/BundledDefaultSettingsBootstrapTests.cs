using System.Text;
using System.Text.Json;
using HubRadio.App.DefaultSettings;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BundledDefaultSettingsBootstrapTests
{
    private const string RelativeProfilePath =
        "src/HubRadio.App/DefaultSettings/Profiles/1.3.0.json";
    private const string RelativePreviousProfilePath =
        "src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json";
    private const string RelativeProfilesDirectory =
        "src/HubRadio.App/DefaultSettings/Profiles";
    private const string CorruptDigest =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static readonly DateTimeOffset InitialTime =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void InitializeRegistersExactBundledBytesAndCreatesTypedGlobalDefaults()
    {
        using BootstrapFixture fixture = new();
        byte[] bundledBytes = ReadBundledProfileBytes();
        var bootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);

        DefaultSettingsBootstrapResult result = bootstrap.Initialize(
            ResolveRepositoryPath(RelativeProfilesDirectory, expectDirectory: true),
            InitialTime);

        Assert.AreEqual(DefaultSettingsBootstrapState.Initialized, result.State);
        Assert.AreEqual("1.3.0", result.Profile.Version.ToString());
        Assert.AreEqual(0, result.GlobalSettings.Revision);
        Assert.AreEqual(InitialTime, result.GlobalSettings.UpdatedAt);
        AssertSnapshotEqual(
            result.Profile.CreateSnapshot(),
            result.GlobalSettings.Snapshot);
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(1L, fixture.CountRows("default_settings_global"));
        Assert.AreEqual(0L, fixture.CountRows("project_default_settings"));

        var restartedStore = new DefaultSettingsStore(fixture.DatabasePath);
        DefaultSettingsProfile storedProfile = restartedStore.GetProfile(
            result.Profile.Id,
            result.Profile.Version)
            ?? throw new AssertFailedException("The bundled profile was not stored.");
        AssertProfileEqual(result.Profile, storedProfile);
        CollectionAssert.AreEqual(
            bundledBytes,
            new UTF8Encoding(false, true).GetBytes(storedProfile.CanonicalJson));
        CollectionAssert.AreEqual(
            bundledBytes,
            fixture.ReadBlob(
                "SELECT profile_payload FROM default_settings_profiles;"));
    }

    [TestMethod]
    public void RestartWithMatchingProfilePreservesOverridesRevisionAndTimestamp()
    {
        using BootstrapFixture fixture = new();
        string profilesDirectory = ResolveRepositoryPath(
            RelativeProfilesDirectory,
            expectDirectory: true);
        var firstBootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);
        DefaultSettingsBootstrapResult initialized = firstBootstrap.Initialize(
            profilesDirectory,
            InitialTime);
        DefaultSettingsSnapshot customized = ReplaceValue(
            initialized.Profile,
            initialized.GlobalSettings.Snapshot,
            "editing.crossfadeEnabled",
            DefaultSettingValue.FromBoolean(true));
        DateTimeOffset overrideTime = InitialTime.AddMinutes(2);
        StoredGlobalSettings updated = fixture.Store.UpdateGlobalSettings(
            customized,
            expectedRevision: 0,
            overrideTime);

        var restartedStore = new DefaultSettingsStore(fixture.DatabasePath);
        var restartedBootstrap = new BundledDefaultSettingsBootstrap(restartedStore);
        DefaultSettingsBootstrapResult existing = restartedBootstrap.Initialize(
            profilesDirectory,
            InitialTime.AddDays(1));

        Assert.AreEqual(DefaultSettingsBootstrapState.Existing, existing.State);
        AssertProfileEqual(initialized.Profile, existing.Profile);
        Assert.AreEqual(updated.Revision, existing.GlobalSettings.Revision);
        Assert.AreEqual(updated.UpdatedAt, existing.GlobalSettings.UpdatedAt);
        AssertSnapshotEqual(customized, existing.GlobalSettings.Snapshot);
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(1L, fixture.CountRows("default_settings_global"));
    }

    [TestMethod]
    public void GlobalSettingsOnThePreviousProfileMoveToTheCurrentProfileKeepingEditedValues()
    {
        using BootstrapFixture fixture = new();
        DefaultSettingsProfile previous = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(ResolveRepositoryPath(RelativePreviousProfilePath)));
        _ = fixture.Store.RegisterProfile(Encoding.UTF8.GetBytes(previous.CanonicalJson));
        DefaultSettingsSnapshot edited = ReplaceValue(
            previous,
            previous.CreateSnapshot(),
            "editing.crossfadeEnabled",
            DefaultSettingValue.FromBoolean(true));
        _ = fixture.Store.InitializeGlobalSettings(edited, InitialTime);

        DefaultSettingsBootstrapResult result = new BundledDefaultSettingsBootstrap(fixture.Store)
            .Initialize(
                ResolveRepositoryPath(RelativeProfilesDirectory, expectDirectory: true),
                InitialTime.AddMinutes(1));

        Assert.AreEqual(DefaultSettingsBootstrapState.Existing, result.State);
        Assert.AreEqual("1.3.0", result.GlobalSettings.Snapshot.ProfileVersion.ToString());
        Assert.AreEqual(1, result.GlobalSettings.Revision);
        Assert.AreEqual(
            true,
            result.GlobalSettings.Snapshot.Values
                .Single(static value => value.Key == "editing.crossfadeEnabled")
                .Value.BooleanValue);
        Assert.AreEqual(2L, fixture.CountRows("default_settings_profiles"));
    }

    [TestMethod]
    public void DifferentGlobalProfileFailsClosedWithoutRegisteringOrMutatingBundle()
    {
        using BootstrapFixture fixture = new();
        DefaultSettingsProfile approved = LoadBundledProfile();
        DefaultSettingsProfile other = DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(),
            "A different explicit global profile",
            new SettingsProfileVersion(2, 0, 0),
            approved.Definitions);
        _ = fixture.Store.RegisterProfile(Encoding.UTF8.GetBytes(other.CanonicalJson));
        DefaultSettingsSnapshot otherSnapshot = other.CreateSnapshot();
        StoredGlobalSettings existing = fixture.Store.InitializeGlobalSettings(
            otherSnapshot,
            InitialTime);
        byte[] existingProfileBytes = fixture.ReadBlob(
            "SELECT profile_payload FROM default_settings_profiles;");
        byte[] existingSnapshotBytes = fixture.ReadBlob(
            "SELECT snapshot_payload FROM default_settings_global;");

        var bootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);
        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() => bootstrap.Initialize(
                ResolveRepositoryPath(
                    RelativeProfilesDirectory,
                    expectDirectory: true),
                InitialTime.AddMinutes(1)));

        StringAssert.Contains(exception.Message, "not in the bundled catalog");
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.IsNull(fixture.Store.GetProfile(approved.Id, approved.Version));
        StoredGlobalSettings unchanged = fixture.Store.GetGlobalSettings()
            ?? throw new AssertFailedException("The existing global settings were removed.");
        Assert.AreEqual(existing.Revision, unchanged.Revision);
        Assert.AreEqual(existing.UpdatedAt, unchanged.UpdatedAt);
        AssertSnapshotEqual(otherSnapshot, unchanged.Snapshot);
        CollectionAssert.AreEqual(existingProfileBytes, fixture.ReadBlob(
            "SELECT profile_payload FROM default_settings_profiles;"));
        CollectionAssert.AreEqual(existingSnapshotBytes, fixture.ReadBlob(
            "SELECT snapshot_payload FROM default_settings_global;"));
    }

    [TestMethod]
    public void SameIdentityVersionStoreDriftFailsClosedWithoutInitializingGlobal()
    {
        using BootstrapFixture fixture = new();
        DefaultSettingsProfile approved = LoadBundledProfile();
        DefaultSettingsProfile altered = CreateAlteredProfile(approved);
        byte[] alteredBytes = Encoding.UTF8.GetBytes(altered.CanonicalJson);
        _ = fixture.Store.RegisterProfile(alteredBytes);

        var bootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);
        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() => bootstrap.Initialize(
                ResolveRepositoryPath(
                    RelativeProfilesDirectory,
                    expectDirectory: true),
                InitialTime));

        StringAssert.Contains(exception.Message, "different default-settings profile");
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(0L, fixture.CountRows("default_settings_global"));
        DefaultSettingsProfile preserved = fixture.Store.GetProfile(
            altered.Id,
            altered.Version)
            ?? throw new AssertFailedException("The conflicting profile was removed.");
        AssertProfileEqual(altered, preserved);
        CollectionAssert.AreEqual(alteredBytes, fixture.ReadBlob(
            "SELECT profile_payload FROM default_settings_profiles;"));
    }

    [TestMethod]
    public void BundleAndCatalogValidationFailuresLeaveDatabaseUnchanged()
    {
        using BootstrapFixture fixture = new();
        DefaultSettingsProfile approved = LoadBundledProfile();
        DefaultSettingsProfile altered = CreateAlteredProfile(approved);
        string alteredDirectory = fixture.CreateProfilesDirectory(
            "altered",
            Encoding.UTF8.GetBytes(altered.CanonicalJson));
        var bootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);

        JsonException drift = Assert.Throws<JsonException>(() => bootstrap.Initialize(
            alteredDirectory,
            InitialTime));
        StringAssert.Contains(
            drift.Message,
            "differs from the approved content for its version");
        Assert.AreEqual(0L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(0L, fixture.CountRows("default_settings_global"));

        string extraEntryDirectory = fixture.CreateProfilesDirectory(
            "extra-entry",
            ReadBundledProfileBytes());
        File.WriteAllBytes(
            Path.Combine(extraEntryDirectory, "unexpected.json"),
            ReadBundledProfileBytes());
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => bootstrap.Initialize(
            extraEntryDirectory,
            InitialTime));
        Assert.AreEqual(0L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(0L, fixture.CountRows("default_settings_global"));
    }

    [TestMethod]
    public void CorruptGlobalValidationFailsBeforeAnyBootstrapMutation()
    {
        using BootstrapFixture fixture = new();
        string profilesDirectory = ResolveRepositoryPath(
            RelativeProfilesDirectory,
            expectDirectory: true);
        var bootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);
        _ = bootstrap.Initialize(profilesDirectory, InitialTime);
        byte[] profileBytes = fixture.ReadBlob(
            "SELECT profile_payload FROM default_settings_profiles;");
        byte[] snapshotBytes = fixture.ReadBlob(
            "SELECT snapshot_payload FROM default_settings_global;");
        fixture.ExecuteNonQuery(
            "UPDATE default_settings_global SET snapshot_digest = $digest;",
            ("$digest", CorruptDigest));

        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() => bootstrap.Initialize(
                profilesDirectory,
                InitialTime.AddMinutes(1)));

        Assert.AreEqual(
            "Stored global default settings data is invalid.",
            exception.Message);
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(1L, fixture.CountRows("default_settings_global"));
        Assert.AreEqual(
            CorruptDigest,
            fixture.ReadText(
                "SELECT snapshot_digest FROM default_settings_global;"));
        CollectionAssert.AreEqual(profileBytes, fixture.ReadBlob(
            "SELECT profile_payload FROM default_settings_profiles;"));
        CollectionAssert.AreEqual(snapshotBytes, fixture.ReadBlob(
            "SELECT snapshot_payload FROM default_settings_global;"));
    }

    [TestMethod]
    public void VersionSevenUpgradeAndBootstrapLeaveExistingProjectUnbound()
    {
        using BootstrapFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        fixture.DowngradeToExactVersionSeven();
        Assert.AreEqual(7L, fixture.CountRows("schema_migrations"));
        Assert.AreEqual(
            0L,
            fixture.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE '%default_settings%';"));

        DatabaseMigrationResult migration = DatabaseMigrator.Migrate(
            fixture.DatabasePath,
            fixture.BackupDirectory);
        var upgradedStore = new DefaultSettingsStore(fixture.DatabasePath);
        var bootstrap = new BundledDefaultSettingsBootstrap(upgradedStore);
        DefaultSettingsBootstrapResult result = bootstrap.Initialize(
            ResolveRepositoryPath(
                RelativeProfilesDirectory,
                expectDirectory: true),
            InitialTime);

        Assert.IsTrue(migration.Applied);
        Assert.AreEqual(14, migration.SchemaVersion);
        Assert.AreEqual(DefaultSettingsBootstrapState.Initialized, result.State);
        ProjectSettingsResult projectSettings = upgradedStore.GetProjectSettings(projectId);
        Assert.AreEqual(ProjectSettingsBindingState.Unbound, projectSettings.BindingState);
        Assert.IsFalse(projectSettings.IsBound);
        Assert.IsNull(projectSettings.Settings);
        Assert.AreEqual(1L, fixture.CountRows("projects"));
        Assert.AreEqual(0L, fixture.CountRows("project_default_settings"));
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(1L, fixture.CountRows("default_settings_global"));
    }

    [TestMethod]
    public void GlobalInsertFailureLeavesExactProfileAndNoPartialGlobalThenRetriesSafely()
    {
        using BootstrapFixture fixture = new();
        fixture.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_default_settings_global_insert
            BEFORE INSERT ON default_settings_global
            BEGIN
                SELECT RAISE(ABORT, 'injected global-settings failure');
            END;
            """);
        string profilesDirectory = ResolveRepositoryPath(
            RelativeProfilesDirectory,
            expectDirectory: true);
        var bootstrap = new BundledDefaultSettingsBootstrap(fixture.Store);

        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() =>
            bootstrap.Initialize(profilesDirectory, InitialTime));

        Assert.AreEqual(19, failure.SqliteErrorCode);
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(0L, fixture.CountRows("default_settings_global"));
        CollectionAssert.AreEqual(ReadBundledProfileBytes(), fixture.ReadBlob(
            "SELECT profile_payload FROM default_settings_profiles;"));

        fixture.ExecuteNonQuery("DROP TRIGGER fail_default_settings_global_insert;");
        DateTimeOffset retryTime = InitialTime.AddMinutes(1);
        DefaultSettingsBootstrapResult retried = bootstrap.Initialize(
            profilesDirectory,
            retryTime);
        Assert.AreEqual(DefaultSettingsBootstrapState.Initialized, retried.State);
        Assert.AreEqual(0, retried.GlobalSettings.Revision);
        Assert.AreEqual(retryTime, retried.GlobalSettings.UpdatedAt);
        Assert.AreEqual(1L, fixture.CountRows("default_settings_profiles"));
        Assert.AreEqual(1L, fixture.CountRows("default_settings_global"));
    }

    private static DefaultSettingsProfile LoadBundledProfile() =>
        BundledDefaultSettingsProfileLoader.Load(ReadBundledProfileBytes());

    private static byte[] ReadBundledProfileBytes() =>
        File.ReadAllBytes(ResolveRepositoryPath(RelativeProfilePath));

    private static DefaultSettingsProfile CreateAlteredProfile(
        DefaultSettingsProfile approved)
    {
        DefaultSettingDefinition original = approved.Definitions[0];
        var alteredDefinition = new DefaultSettingDefinition(
            original.Key,
            original.Group,
            original.IsAdvanced,
            original.DisplayName,
            original.Description + " Reproducible drift.",
            original.Unit,
            original.DefaultValue,
            original.Minimum,
            original.Maximum,
            original.AllowedTextValues,
            original.Evidence);
        DefaultSettingDefinition[] alteredDefinitions = approved.Definitions
            .Select(definition => string.Equals(
                    definition.Key,
                    original.Key,
                    StringComparison.Ordinal)
                ? alteredDefinition
                : definition)
            .ToArray();
        DefaultSettingsProfile altered = DefaultSettingsProfile.Create(
            approved.Id,
            approved.Name,
            approved.Version,
            alteredDefinitions);
        Assert.AreNotEqual(approved.ContentDigest, altered.ContentDigest);
        return altered;
    }

    private static DefaultSettingsSnapshot ReplaceValue(
        DefaultSettingsProfile profile,
        DefaultSettingsSnapshot current,
        string key,
        DefaultSettingValue replacement)
    {
        return profile.CreateSnapshot(current.Values
            .Select(value => new DefaultSettingSelection(
                value.Key,
                string.Equals(value.Key, key, StringComparison.Ordinal)
                    ? replacement
                    : value.Value))
            .ToArray());
    }

    private static void AssertProfileEqual(
        DefaultSettingsProfile expected,
        DefaultSettingsProfile actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Version, actual.Version);
        Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
        Assert.AreEqual(expected.CanonicalJson, actual.CanonicalJson);
    }

    private static void AssertSnapshotEqual(
        DefaultSettingsSnapshot expected,
        DefaultSettingsSnapshot actual)
    {
        Assert.AreEqual(expected.ProfileId, actual.ProfileId);
        Assert.AreEqual(expected.ProfileVersion, actual.ProfileVersion);
        Assert.AreEqual(expected.ProfileDigest, actual.ProfileDigest);
        Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
        Assert.AreEqual(expected.CanonicalJson, actual.CanonicalJson);
    }

    private static string ResolveRepositoryPath(
        string relativePath,
        bool expectDirectory = false)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                bool exists = expectDirectory ? Directory.Exists(path) : File.Exists(path);
                if (!exists)
                {
                    throw new FileNotFoundException(relativePath, path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }

    private sealed class BootstrapFixture : IDisposable
    {
        internal BootstrapFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.BundledDefaultSettingsBootstrapTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            BackupDirectory = Path.Combine(RootPath, "backups");
            _ = DatabaseMigrator.Migrate(DatabasePath, BackupDirectory);
            Store = new DefaultSettingsStore(DatabasePath);
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal string BackupDirectory { get; }

        internal DefaultSettingsStore Store { get; }

        internal long CountRows(string tableName) =>
            ExecuteScalarInt64($"SELECT COUNT(*) FROM {tableName};");

        internal EntityId<ProjectIdTag> CreateProject()
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                projectId,
                "Pre-existing version-seven project",
                "Verify settings bootstrap migration behavior.",
                "Internal test audience",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            var projectStore = new ProjectStore(DatabasePath);
            projectStore.CreateProject(project, "{\"project\":true}", InitialTime);
            return projectId;
        }

        internal void DowngradeToExactVersionSeven()
        {
            ExecuteNonQuery(
                """
                DROP TRIGGER trg_project_display_names_delete;
                DROP TABLE project_display_names;
                DROP TRIGGER trg_project_creation_draft_deletions_update;
                DROP TRIGGER trg_project_creation_draft_deletions_delete;
                DROP TABLE project_creation_draft_deletions;
                DROP TRIGGER trg_subtitle_edits_version_insert;
                DROP TRIGGER trg_subtitle_edits_version_update;
                DROP TRIGGER trg_subtitle_edits_version_delete;
                DROP TABLE subtitle_edits;
                DROP TABLE source_only_executions;
                DROP TRIGGER trg_quick_start_evaluation_immutable_update;
                DROP TRIGGER trg_quick_start_evaluation_rule_immutable_insert;
                DROP TRIGGER trg_quick_start_evaluation_rule_immutable_update;
                DROP TRIGGER trg_quick_start_evaluation_rule_immutable_delete;
                DROP TRIGGER trg_quick_start_render_artifact_immutable_update;
                DROP TRIGGER trg_quick_start_render_artifact_immutable_delete;
                DROP TABLE quick_start_state_events;
                DROP TABLE quick_start_workflows;
                DROP INDEX idx_automated_evaluations_quick_start_binding;
                DROP TRIGGER trg_artifact_automated_evaluation_kind_update;
                DROP TABLE automated_evaluation_rules;
                DROP TABLE automated_evaluations;
                DROP INDEX idx_artifacts_evaluation_binding;
                DROP TABLE project_default_settings;
                DROP TABLE default_settings_global;
                DROP TABLE default_settings_profiles;
                DELETE FROM schema_migrations WHERE version >= 8;
                """);
        }

        internal string CreateProfilesDirectory(string name, byte[] profileBytes)
        {
            string path = Path.Combine(RootPath, name);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(
                Path.Combine(path, "1.0.0.json"),
                File.ReadAllBytes(ResolveRepositoryPath(RelativePreviousProfilePath)));
            File.WriteAllBytes(
                Path.Combine(path, "1.1.0.json"),
                File.ReadAllBytes(ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.1.0.json")));
            File.WriteAllBytes(
                Path.Combine(path, "1.2.0.json"),
                File.ReadAllBytes(ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.2.0.json")));
            File.WriteAllBytes(Path.Combine(path, "1.3.0.json"), profileBytes);
            return path;
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
            object value = ExecuteScalar(sql, parameters);
            return value is long result
                ? result
                : throw new AssertFailedException("The scalar value was not an Int64.");
        }

        internal string ReadText(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            object value = ExecuteScalar(sql, parameters);
            return value as string
                ?? throw new AssertFailedException("The scalar value was not text.");
        }

        internal byte[] ReadBlob(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            object value = ExecuteScalar(sql, parameters);
            return value as byte[]
                ?? throw new AssertFailedException("The scalar value was not a blob.");
        }

        private object ExecuteScalar(
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

            return command.ExecuteScalar()
                ?? throw new AssertFailedException("The scalar query returned null.");
        }

        private SqliteConnection OpenConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
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
}
