using System.Text;
using HubRadio.App.DefaultSettings;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class GlobalSettingsServiceTests
{
    private const string SubtitleKey = "subtitles.enabled";
    private const string CrossfadeKey = "editing.crossfadeEnabled";
    private const string ProfilesDirectory = "src/HubRadio.App/DefaultSettings/Profiles";

    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ReadReturnsBootstrappedSettingsWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings expected = fixture.Store.GetGlobalSettings()!;

        StoredGlobalSettings actual = new GlobalSettingsService(fixture.Store).Read();

        AssertGlobalEqual(expected, actual);
        AssertSnapshotEqual(fixture.Profile.CreateSnapshot(), actual.Snapshot);
        Assert.AreEqual(0, actual.Revision);
        Assert.AreEqual(InitialTime, actual.UpdatedAt);
        AssertGlobalEqual(expected, fixture.Service.Read());
    }

    [TestMethod]
    public void OperationsRejectUninitializedSettingsWithoutInitializing()
    {
        using Fixture fixture = new(initializeGlobal: false);
        DefaultSettingValue value = fixture.ChangedBoolean(SubtitleKey);

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Read());
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Update(SubtitleKey, value, expectedRevision: 0));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Reset(SubtitleKey, expectedRevision: 0));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.ResetAll(expectedRevision: 0));

        Assert.IsNull(new DefaultSettingsStore(fixture.DatabasePath).GetGlobalSettings());
        Assert.AreEqual(1, fixture.Store.ListProfiles().Count);
    }

    [TestMethod]
    public void UpdateChangesOnlySelectedValueAndSurvivesRestart()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        DefaultSettingValue replacement = fixture.ChangedBoolean(SubtitleKey);

        StoredGlobalSettings updated = fixture.Service.Update(
            SubtitleKey,
            replacement,
            initial.Revision);

        Assert.AreEqual(initial.Revision + 1, updated.Revision);
        Assert.AreEqual(fixture.UtcNow, updated.UpdatedAt);
        AssertOnlyValueChanged(initial.Snapshot, updated.Snapshot, SubtitleKey, replacement);
        AssertGlobalEqual(updated, fixture.RestartService().Read());
    }

    [TestMethod]
    public void ResetRestoresProfileDefaultAndPreservesOtherOverrides()
    {
        using Fixture fixture = new();
        StoredGlobalSettings first = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            expectedRevision: 0);
        StoredGlobalSettings customized = fixture.Service.Update(
            CrossfadeKey,
            fixture.ChangedBoolean(CrossfadeKey),
            first.Revision);
        fixture.UtcNow = InitialTime.AddMinutes(2);

        StoredGlobalSettings reset = fixture.Service.Reset(SubtitleKey, customized.Revision);

        Assert.AreEqual(customized.Revision + 1, reset.Revision);
        Assert.AreEqual(fixture.UtcNow, reset.UpdatedAt);
        AssertOnlyValueChanged(
            customized.Snapshot,
            reset.Snapshot,
            SubtitleKey,
            fixture.Profile.GetDefinition(SubtitleKey).DefaultValue);
        AssertGlobalEqual(reset, fixture.RestartService().Read());
    }

    [TestMethod]
    public void ResetAllRestoresProfileDefaultsAndSurvivesRestart()
    {
        using Fixture fixture = new();
        StoredGlobalSettings first = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            expectedRevision: 0);
        StoredGlobalSettings customized = fixture.Service.Update(
            CrossfadeKey,
            fixture.ChangedBoolean(CrossfadeKey),
            first.Revision);
        fixture.UtcNow = InitialTime.AddMinutes(2);

        StoredGlobalSettings reset = fixture.Service.ResetAll(customized.Revision);

        Assert.AreEqual(customized.Revision + 1, reset.Revision);
        Assert.AreEqual(fixture.UtcNow, reset.UpdatedAt);
        AssertSnapshotEqual(fixture.Profile.CreateSnapshot(), reset.Snapshot);
        AssertGlobalEqual(reset, fixture.RestartService().Read());
    }

    [TestMethod]
    public void NoOpMutationsPreserveRevisionAndTimestamp()
    {
        using Fixture fixture = new();
        var noWriteService = new GlobalSettingsService(
            fixture.Store,
            () => throw new AssertFailedException("A no-op must not request a write timestamp."));
        StoredGlobalSettings initial = fixture.Service.Read();

        AssertGlobalEqual(initial, noWriteService.Update(
            SubtitleKey,
            fixture.Profile.GetDefinition(SubtitleKey).DefaultValue,
            initial.Revision));
        AssertGlobalEqual(initial, noWriteService.Reset(SubtitleKey, initial.Revision));
        AssertGlobalEqual(initial, noWriteService.ResetAll(initial.Revision));

        DefaultSettingValue replacement = fixture.ChangedBoolean(SubtitleKey);
        StoredGlobalSettings customized = fixture.Service.Update(
            SubtitleKey,
            replacement,
            initial.Revision);
        AssertGlobalEqual(customized, noWriteService.Update(
            SubtitleKey,
            replacement,
            customized.Revision));
        AssertGlobalEqual(customized, noWriteService.Reset(CrossfadeKey, customized.Revision));

        StoredGlobalSettings restored = fixture.Service.ResetAll(customized.Revision);
        AssertGlobalEqual(restored, noWriteService.ResetAll(restored.Revision));
        AssertGlobalEqual(restored, fixture.RestartService().Read());
    }

    [TestMethod]
    public void StaleRevisionRejectsAllMutationsWithoutMerging()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        StoredGlobalSettings winner = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            initial.Revision);
        Action[] mutations =
        [
            () => fixture.Service.Update(
                CrossfadeKey,
                fixture.ChangedBoolean(CrossfadeKey),
                initial.Revision),
            () => fixture.Service.Reset(SubtitleKey, initial.Revision),
            () => fixture.Service.ResetAll(initial.Revision),
        ];

        foreach (Action mutation in mutations)
        {
            AssertConflict(mutation, initial.Revision, winner.Revision);
            AssertGlobalEqual(winner, fixture.Service.Read());
        }
    }

    [TestMethod]
    public void StaleRevisionRejectsNoOpMutations()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        StoredGlobalSettings changed = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            initial.Revision);
        StoredGlobalSettings restored = fixture.Service.Reset(SubtitleKey, changed.Revision);
        AssertSnapshotEqual(initial.Snapshot, restored.Snapshot);
        Action[] mutations =
        [
            () => fixture.Service.Update(
                SubtitleKey,
                fixture.Profile.GetDefinition(SubtitleKey).DefaultValue,
                initial.Revision),
            () => fixture.Service.Reset(SubtitleKey, initial.Revision),
            () => fixture.Service.ResetAll(initial.Revision),
        ];

        foreach (Action mutation in mutations)
        {
            AssertConflict(mutation, initial.Revision, restored.Revision);
            AssertGlobalEqual(restored, fixture.Service.Read());
        }
    }

    [TestMethod]
    public void StoreDetectsConcurrentWriteBetweenReadAndSave()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        DefaultSettingValue winnerValue = fixture.ChangedBoolean(SubtitleKey);
        var racingService = new GlobalSettingsService(
            new DefaultSettingsStore(fixture.DatabasePath),
            () =>
            {
                _ = fixture.Service.Update(SubtitleKey, winnerValue, initial.Revision);
                return fixture.UtcNow;
            });

        AssertConflict(
            () => racingService.Update(
                CrossfadeKey,
                fixture.ChangedBoolean(CrossfadeKey),
                initial.Revision),
            initial.Revision,
            initial.Revision + 1);

        StoredGlobalSettings winner = fixture.RestartService().Read();
        Assert.AreEqual(initial.Revision + 1, winner.Revision);
        Assert.AreEqual(fixture.UtcNow, winner.UpdatedAt);
        AssertOnlyValueChanged(initial.Snapshot, winner.Snapshot, SubtitleKey, winnerValue);
    }

    [TestMethod]
    public void ClockRollbackDoesNotMoveUpdatedAtBackwards()
    {
        using Fixture fixture = new();
        fixture.UtcNow = InitialTime.AddMinutes(-5).ToOffset(TimeSpan.FromHours(9));
        StoredGlobalSettings updated = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            expectedRevision: 0);
        Assert.AreEqual(InitialTime, updated.UpdatedAt);

        DateTimeOffset later = InitialTime.AddMinutes(10);
        fixture.UtcNow = later.ToOffset(TimeSpan.FromHours(9));
        StoredGlobalSettings reset = fixture.Service.Reset(SubtitleKey, updated.Revision);
        Assert.AreEqual(later, reset.UpdatedAt);

        StoredGlobalSettings customized = fixture.Service.Update(
            CrossfadeKey,
            fixture.ChangedBoolean(CrossfadeKey),
            reset.Revision);
        fixture.UtcNow = InitialTime;
        StoredGlobalSettings resetAll = fixture.Service.ResetAll(customized.Revision);
        Assert.AreEqual(later, resetAll.UpdatedAt);
        Assert.AreEqual(TimeSpan.Zero, resetAll.UpdatedAt.Offset);
        AssertGlobalEqual(resetAll, fixture.RestartService().Read());
    }

    [TestMethod]
    public void RegisteredProfilesDoNotChangeGlobalProfileBinding()
    {
        using Fixture fixture = new();
        DefaultSettingsProfile otherVersion = DefaultSettingsProfile.Create(
            fixture.Profile.Id,
            fixture.Profile.Name,
            new SettingsProfileVersion(fixture.Profile.Version.Major + 1, 0, 0),
            fixture.Profile.Definitions);
        _ = fixture.Store.RegisterProfile(Encoding.UTF8.GetBytes(otherVersion.CanonicalJson));
        Assert.AreEqual(2, fixture.Store.ListProfiles().Count);

        StoredGlobalSettings updated = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            expectedRevision: 0);
        StoredGlobalSettings reset = fixture.Service.Reset(SubtitleKey, updated.Revision);
        StoredGlobalSettings customized = fixture.Service.Update(
            CrossfadeKey,
            fixture.ChangedBoolean(CrossfadeKey),
            reset.Revision);
        StoredGlobalSettings resetAll = fixture.Service.ResetAll(customized.Revision);

        foreach (StoredGlobalSettings result in new[] { updated, reset, customized, resetAll })
        {
            Assert.AreEqual(fixture.Profile.Id, result.Snapshot.ProfileId);
            Assert.AreEqual(fixture.Profile.Version, result.Snapshot.ProfileVersion);
            Assert.AreEqual(fixture.Profile.ContentDigest, result.Snapshot.ProfileDigest);
        }

        AssertSnapshotEqual(fixture.Profile.CreateSnapshot(), resetAll.Snapshot);
        AssertGlobalEqual(resetAll, fixture.RestartService().Read());
    }

    [TestMethod]
    public void GlobalMutationsPreserveProjectInitialCurrentAndRevision()
    {
        using Fixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        DefaultSettingsSnapshot initialSnapshot = ReplaceValue(
            fixture.Profile,
            fixture.Profile.CreateSnapshot(),
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey));
        StoredProjectSettings bound = fixture.Store.BindProjectSettings(
            projectId,
            initialSnapshot,
            InitialTime);
        StoredProjectSettings project = fixture.Store.UpdateProjectSettings(
            projectId,
            ReplaceValue(
                fixture.Profile,
                initialSnapshot,
                CrossfadeKey,
                fixture.ChangedBoolean(CrossfadeKey)),
            bound.Revision,
            InitialTime.AddSeconds(30));
        Assert.AreNotEqual(project.InitialSnapshot.ContentDigest, project.CurrentSnapshot.ContentDigest);
        Func<int, StoredGlobalSettings>[] mutations =
        [
            revision => fixture.Service.Update(SubtitleKey, fixture.ChangedBoolean(SubtitleKey), revision),
            revision => fixture.Service.Update(CrossfadeKey, fixture.ChangedBoolean(CrossfadeKey), revision),
            revision => fixture.Service.Reset(SubtitleKey, revision),
            revision => fixture.Service.ResetAll(revision),
        ];
        StoredGlobalSettings global = fixture.Service.Read();

        foreach (Func<int, StoredGlobalSettings> mutation in mutations)
        {
            global = mutation(global.Revision);
            AssertProjectEqual(project, fixture.Store.GetProjectSettings(projectId).Settings!);
        }

        var restartedStore = new DefaultSettingsStore(fixture.DatabasePath);
        AssertProjectEqual(project, restartedStore.GetProjectSettings(projectId).Settings!);
        AssertSnapshotEqual(fixture.Profile.CreateSnapshot(), global.Snapshot);
    }

    [TestMethod]
    public void ProjectMutationsDoNotInvalidateGlobalRevision()
    {
        using Fixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        StoredProjectSettings bound = fixture.Store.BindProjectSettings(
            projectId,
            fixture.Profile.CreateSnapshot(),
            InitialTime);
        StoredGlobalSettings global = fixture.Service.Read();
        StoredProjectSettings project = fixture.Store.UpdateProjectSettings(
            projectId,
            ReplaceValue(
                fixture.Profile,
                bound.CurrentSnapshot,
                CrossfadeKey,
                fixture.ChangedBoolean(CrossfadeKey)),
            bound.Revision,
            fixture.UtcNow);

        StoredGlobalSettings updated = fixture.Service.Update(
            SubtitleKey,
            fixture.ChangedBoolean(SubtitleKey),
            global.Revision);

        Assert.AreEqual(global.Revision + 1, updated.Revision);
        AssertProjectEqual(project, fixture.Store.GetProjectSettings(projectId).Settings!);
    }

    [TestMethod]
    public void UpdateAndResetRejectUnknownKeysWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        string[] keys = ["unknown.key", "", " ", "Subtitles.enabled", "subtitles.enabled "];

        foreach (string key in keys)
        {
            _ = Assert.ThrowsExactly<KeyNotFoundException>(() => fixture.Service.Update(
                key,
                DefaultSettingValue.FromBoolean(false),
                initial.Revision));
            _ = Assert.ThrowsExactly<KeyNotFoundException>(() =>
                fixture.Service.Reset(key, initial.Revision));
            AssertGlobalEqual(initial, fixture.Service.Read());
        }
    }

    [TestMethod]
    public void UpdateRejectsWrongValueKindsWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        DefaultSettingValue[] values =
        [
            DefaultSettingValue.FromInteger(0),
            DefaultSettingValue.FromRational(1, 1),
            DefaultSettingValue.FromText("false"),
            DefaultSettingValue.FromTextList(["false"]),
        ];

        foreach (DefaultSettingValue value in values)
        {
            _ = Assert.ThrowsExactly<ArgumentException>(() =>
                fixture.Service.Update(SubtitleKey, value, initial.Revision));
            AssertGlobalEqual(initial, fixture.Service.Read());
        }
    }

    [TestMethod]
    public void UpdateRejectsIntegerValuesOutsideProfileBoundsWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        DefaultSettingDefinition definition = fixture.Profile.Definitions.First(
            static item => item.DefaultValue.Kind == DefaultSettingValueKind.Integer &&
                item.Minimum is not null && item.Maximum is not null);
        DefaultSettingValue[] values =
        [
            DefaultSettingValue.FromInteger(checked(definition.Minimum!.IntegerValue!.Value - 1)),
            DefaultSettingValue.FromInteger(checked(definition.Maximum!.IntegerValue!.Value + 1)),
        ];

        foreach (DefaultSettingValue value in values)
        {
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.Update(definition.Key, value, initial.Revision));
            AssertGlobalEqual(initial, fixture.Service.Read());
        }
    }

    [TestMethod]
    public void UpdateRejectsRationalValuesOutsideProfileBoundsWithoutMutation()
    {
        using Fixture fixture = new(initializeGlobal: false);
        var definition = new DefaultSettingDefinition(
            "settings.ratio", DefaultSettingGroup.Editing, false, "Ratio",
            "Explicit bounded test value", DefaultSettingUnit.None,
            DefaultSettingValue.FromRational(1, 2),
            DefaultSettingValue.FromRational(0, 1),
            DefaultSettingValue.FromRational(1, 1),
            allowedTextValues: null,
            evidence: [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")]);
        DefaultSettingsProfile profile = DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(), "Bounded rational test",
            new SettingsProfileVersion(1, 0, 0), [definition]);
        _ = fixture.Store.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
        StoredGlobalSettings initial = fixture.Store.InitializeGlobalSettings(profile.CreateSnapshot(), InitialTime);
        var minimum = definition.Minimum!.RationalValue!.Value;
        var maximum = definition.Maximum!.RationalValue!.Value;
        DefaultSettingValue[] values =
        [
            DefaultSettingValue.FromRational(
                checked(minimum.Numerator - minimum.Denominator), minimum.Denominator),
            DefaultSettingValue.FromRational(
                checked(maximum.Numerator + maximum.Denominator), maximum.Denominator),
        ];

        foreach (DefaultSettingValue value in values)
        {
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.Update(definition.Key, value, initial.Revision));
            AssertGlobalEqual(initial, fixture.Service.Read());
        }
    }

    [TestMethod]
    public void UpdateRejectsValuesOutsideTextAllowlistWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        DefaultSettingDefinition definition = fixture.Profile.Definitions.First(
            static item => item.AllowedTextValues.Count > 0);
        const string invalidText = "not-an-allowed-setting-value";
        Assert.IsFalse(definition.AllowedTextValues.Contains(invalidText, StringComparer.Ordinal));

        _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.Service.Update(
            definition.Key,
            DefaultSettingValue.FromText(invalidText),
            initial.Revision));

        AssertGlobalEqual(initial, fixture.Service.Read());
    }

    [TestMethod]
    public void ConstructorRejectsNullStore()
    {
        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new GlobalSettingsService(null!));

        Assert.AreEqual("store", exception.ParamName);
    }

    [TestMethod]
    public void UpdateAndResetRejectNullArgumentsWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        DefaultSettingValue value = fixture.ChangedBoolean(SubtitleKey);

        ArgumentNullException updateKey = Assert.ThrowsExactly<ArgumentNullException>(() =>
            fixture.Service.Update(null!, value, initial.Revision));
        ArgumentNullException resetKey = Assert.ThrowsExactly<ArgumentNullException>(() =>
            fixture.Service.Reset(null!, initial.Revision));
        ArgumentNullException updateValue = Assert.ThrowsExactly<ArgumentNullException>(() =>
            fixture.Service.Update(SubtitleKey, null!, initial.Revision));

        Assert.AreEqual("key", updateKey.ParamName);
        Assert.AreEqual("key", resetKey.ParamName);
        Assert.AreEqual("value", updateValue.ParamName);
        AssertGlobalEqual(initial, fixture.Service.Read());
    }

    [TestMethod]
    public void MutationsRejectNegativeRevisionsWithoutMutation()
    {
        using Fixture fixture = new();
        StoredGlobalSettings initial = fixture.Service.Read();
        Action[] mutations =
        [
            () => fixture.Service.Update(SubtitleKey, fixture.ChangedBoolean(SubtitleKey), -1),
            () => fixture.Service.Reset(SubtitleKey, -1),
            () => fixture.Service.ResetAll(-1),
        ];

        foreach (Action mutation in mutations)
        {
            ArgumentOutOfRangeException exception =
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(mutation);
            Assert.AreEqual("expectedRevision", exception.ParamName);
            AssertGlobalEqual(initial, fixture.Service.Read());
        }
    }

    private static void AssertConflict(Action mutation, int expectedRevision, int expectedStoredRevision)
    {
        StoreConcurrencyException exception = Assert.ThrowsExactly<StoreConcurrencyException>(mutation);
        Assert.AreEqual("global default settings", exception.EntityKind);
        Assert.AreEqual("singleton", exception.EntityId);
        Assert.AreEqual(expected: expectedRevision, actual: exception.ExpectedRevision);
        Assert.AreEqual(expectedStoredRevision, exception.ActualRevision);
    }

    private static void AssertGlobalEqual(StoredGlobalSettings expected, StoredGlobalSettings actual)
    {
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        AssertSnapshotEqual(expected.Snapshot, actual.Snapshot);
    }

    private static void AssertProjectEqual(StoredProjectSettings expected, StoredProjectSettings actual)
    {
        Assert.AreEqual(expected.ProjectId, actual.ProjectId);
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        AssertSnapshotEqual(expected.InitialSnapshot, actual.InitialSnapshot);
        AssertSnapshotEqual(expected.CurrentSnapshot, actual.CurrentSnapshot);
    }

    private static void AssertSnapshotEqual(DefaultSettingsSnapshot expected, DefaultSettingsSnapshot actual)
    {
        Assert.AreEqual(expected.ProfileId, actual.ProfileId);
        Assert.AreEqual(expected.ProfileVersion, actual.ProfileVersion);
        Assert.AreEqual(expected.ProfileDigest, actual.ProfileDigest);
        Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
        Assert.AreEqual(expected.CanonicalJson, actual.CanonicalJson);
    }

    private static void AssertOnlyValueChanged(
        DefaultSettingsSnapshot before,
        DefaultSettingsSnapshot after,
        string key,
        DefaultSettingValue replacement)
    {
        Assert.AreEqual(before.ProfileId, after.ProfileId);
        Assert.AreEqual(before.ProfileVersion, after.ProfileVersion);
        Assert.AreEqual(before.ProfileDigest, after.ProfileDigest);
        Assert.AreNotEqual(before.ContentDigest, after.ContentDigest);
        CollectionAssert.AreEqual(
            before.Values.Select(static selection => selection.Key).ToArray(),
            after.Values.Select(static selection => selection.Key).ToArray());
        foreach (DefaultSettingSelection selection in before.Values)
        {
            DefaultSettingValue expected = string.Equals(selection.Key, key, StringComparison.Ordinal)
                ? replacement
                : selection.Value;
            DefaultSettingValue actual = after.Values.Single(
                item => string.Equals(item.Key, selection.Key, StringComparison.Ordinal)).Value;
            Assert.AreEqual(expected, actual, selection.Key);
        }
    }

    private static DefaultSettingsSnapshot ReplaceValue(
        DefaultSettingsProfile profile,
        DefaultSettingsSnapshot current,
        string key,
        DefaultSettingValue replacement) =>
        profile.CreateSnapshot(current.Values.Select(selection => new DefaultSettingSelection(
            selection.Key,
            string.Equals(selection.Key, key, StringComparison.Ordinal)
                ? replacement
                : selection.Value)).ToArray());

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;

        internal Fixture(bool initializeGlobal = true)
        {
            root = Path.Combine(Path.GetTempPath(), nameof(GlobalSettingsServiceTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DatabasePath = Path.Combine(root, "settings.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(root, "backups"));
            Store = new DefaultSettingsStore(DatabasePath);
            byte[] profileBytes = File.ReadAllBytes(ResolveRepositoryPath(ProfilesDirectory + "/1.2.0.json"));
            Profile = BundledDefaultSettingsProfileLoader.Load(profileBytes);
            if (initializeGlobal)
            {
                _ = new BundledDefaultSettingsBootstrap(Store).Initialize(
                    ResolveRepositoryPath(ProfilesDirectory),
                    InitialTime);
            }
            else
            {
                _ = Store.RegisterProfile(profileBytes);
            }

            Service = new GlobalSettingsService(Store, () => UtcNow);
        }

        internal string DatabasePath { get; }

        internal DefaultSettingsProfile Profile { get; }

        internal DefaultSettingsStore Store { get; }

        internal GlobalSettingsService Service { get; }

        internal DateTimeOffset UtcNow { get; set; } = InitialTime.AddMinutes(1);

        internal DefaultSettingValue ChangedBoolean(string key) =>
            DefaultSettingValue.FromBoolean(!Profile.GetDefinition(key).DefaultValue.BooleanValue!.Value);

        internal GlobalSettingsService RestartService() =>
            new(new DefaultSettingsStore(DatabasePath), () => UtcNow);

        internal EntityId<ProjectIdTag> CreateProject()
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                projectId,
                "Global settings isolation test",
                "Preserve existing project settings",
                "Internal",
                PublicationScope.InternalOnly,
                null,
                [],
                []);
            new ProjectStore(DatabasePath).CreateProject(project, "{\"project\":true}", InitialTime);
            return projectId;
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}