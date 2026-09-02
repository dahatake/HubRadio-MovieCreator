using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class DefaultSettingsStoreTests
{
    private const string CorruptDigest =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static readonly EntityId<DefaultSettingsProfileIdTag> ProfileId =
        EntityId<DefaultSettingsProfileIdTag>.From(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

    private static readonly EntityId<DefaultSettingsProfileIdTag> SecondProfileId =
        EntityId<DefaultSettingsProfileIdTag>.From(
            Guid.Parse("11112233-4455-6677-8899-aabbccddeeff"));

    private static readonly DateTimeOffset InitialTime =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void ProfilesRoundTripExactCanonicalBytesAcrossRestartInSemanticOrder()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile versionTwo = CreateProfile(
            ProfileId,
            new SettingsProfileVersion(1, 2, 0));
        DefaultSettingsProfile versionTen = CreateProfile(
            ProfileId,
            new SettingsProfileVersion(1, 10, 0));
        DefaultSettingsProfile secondId = CreateProfile(
            SecondProfileId,
            new SettingsProfileVersion(1, 0, 0));
        byte[] sourceBytes = Encoding.UTF8.GetBytes(versionTwo.CanonicalJson);

        DefaultSettingsProfile registered = store.RegisterProfile(sourceBytes);
        sourceBytes[0] = (byte)' ';
        _ = store.RegisterProfile(Encoding.UTF8.GetBytes(versionTen.CanonicalJson));
        _ = store.RegisterProfile(Encoding.UTF8.GetBytes(secondId.CanonicalJson));
        DefaultSettingsProfile idempotent = store.RegisterProfile(
            Encoding.UTF8.GetBytes(versionTwo.CanonicalJson));

        AssertProfileEqual(versionTwo, registered);
        AssertProfileEqual(versionTwo, idempotent);
        Assert.AreEqual(
            3L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM default_settings_profiles;"));

        var restarted = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile loaded = restarted.GetProfile(
            versionTwo.Id,
            versionTwo.Version)
            ?? throw new AssertFailedException("The registered profile was not loaded.");
        AssertProfileEqual(versionTwo, loaded);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(versionTwo.CanonicalJson),
            Encoding.UTF8.GetBytes(loaded.CanonicalJson));
        Assert.IsNull(restarted.GetProfile(
            versionTwo.Id,
            new SettingsProfileVersion(9, 0, 0)));

        IReadOnlyList<DefaultSettingsProfile> profiles = restarted.ListProfiles();
        CollectionAssert.AreEqual(
            new[]
            {
                $"{ProfileId}:1.2.0",
                $"{ProfileId}:1.10.0",
                $"{SecondProfileId}:1.0.0",
            },
            profiles
                .Select(static profile => $"{profile.Id}:{profile.Version}")
                .ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<DefaultSettingsProfile>)profiles).Clear());
    }

    [TestMethod]
    public void RegisterProfileRejectsSameIdAndVersionWithDifferentContent()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile original = CreateProfile();
        DefaultSettingsProfile conflict = CreateProfile(
            profileName: "Different profile content");
        _ = store.RegisterProfile(Encoding.UTF8.GetBytes(original.CanonicalJson));

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => store.RegisterProfile(Encoding.UTF8.GetBytes(conflict.CanonicalJson)));

        Assert.IsFalse(exception.Message.Contains(conflict.CanonicalJson, StringComparison.Ordinal));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM default_settings_profiles;"));
        AssertProfileEqual(
            original,
            store.GetProfile(original.Id, original.Version)
                ?? throw new AssertFailedException("The original profile was not preserved."));
    }

    [TestMethod]
    public void RegisterProfileRejectsNullEmptyMalformedNonCanonicalAndOversizedUtf8()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = CreateProfile();
        string domainInvalidCanonicalJson = profile.CanonicalJson.Replace(
            "\"key\":\"editing.crossfadeDuration\"",
            "\"key\":\"Editing.crossfadeDuration\"",
            StringComparison.Ordinal);
        byte[][] invalidPayloads =
        [
            Array.Empty<byte>(),
            new byte[] { 0xc3, 0x28 },
            Encoding.UTF8.GetBytes(" " + profile.CanonicalJson),
            Encoding.UTF8.GetBytes("{}"),
            Encoding.UTF8.GetBytes(domainInvalidCanonicalJson),
            Enumerable.Repeat(
                (byte)'x',
                DefaultSettingsStore.MaximumCanonicalJsonUtf8Bytes + 1).ToArray(),
        ];

        Assert.ThrowsExactly<ArgumentNullException>(
            () => store.RegisterProfile(null!));
        foreach (byte[] invalidPayload in invalidPayloads)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => store.RegisterProfile(invalidPayload));
            Assert.AreEqual("canonicalProfileUtf8", exception.ParamName);
        }

        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM default_settings_profiles;"));
    }

    [TestMethod]
    public void GlobalSettingsPersistAndRejectMissingProfileStaleRevisionAndReverseTime()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(store);
        DefaultSettingsSnapshot defaults = profile.CreateSnapshot();
        Assert.IsNull(store.GetGlobalSettings());

        StoredGlobalSettings initialized = store.InitializeGlobalSettings(
            defaults,
            InitialTime);
        Assert.AreEqual(0, initialized.Revision);
        Assert.AreEqual(InitialTime, initialized.UpdatedAt);
        AssertSnapshotEqual(defaults, initialized.Snapshot);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.InitializeGlobalSettings(defaults, InitialTime.AddMinutes(1)));

        DefaultSettingsProfile unregistered = CreateProfile(
            SecondProfileId,
            new SettingsProfileVersion(1, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.UpdateGlobalSettings(
                unregistered.CreateSnapshot(),
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));
        AssertGlobalState(store, defaults, revision: 0, InitialTime);

        DefaultSettingsSnapshot customized = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "pendingDecision");
        StoredGlobalSettings updated = store.UpdateGlobalSettings(
            customized,
            expectedRevision: 0,
            InitialTime.AddMinutes(2));
        Assert.AreEqual(1, updated.Revision);
        Assert.AreEqual(InitialTime.AddMinutes(2), updated.UpdatedAt);
        AssertSnapshotEqual(customized, updated.Snapshot);

        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => store.UpdateGlobalSettings(
                defaults,
                expectedRevision: 0,
                InitialTime.AddMinutes(3)));
        AssertConcurrency(
            stale,
            "global default settings",
            "singleton",
            expectedRevision: 0,
            conflictingRevision: 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.UpdateGlobalSettings(
                defaults,
                expectedRevision: 1,
                InitialTime.AddMinutes(1)));

        var restarted = new DefaultSettingsStore(database.DatabasePath);
        AssertGlobalState(
            restarted,
            customized,
            revision: 1,
            InitialTime.AddMinutes(2));
    }

    [TestMethod]
    public async Task ConcurrentGlobalUpdatesAcrossStoresAllowExactlyOneRevisionWinner()
    {
        using StoreTestDatabase database = new();
        var firstStore = new DefaultSettingsStore(database.DatabasePath);
        var secondStore = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(firstStore);
        firstStore.InitializeGlobalSettings(profile.CreateSnapshot(), InitialTime);
        DefaultSettingsSnapshot firstCandidate = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "internalOnly");
        DefaultSettingsSnapshot secondCandidate = CreateSnapshot(
            profile,
            crossfadeDuration: 500,
            publicationScope: "pendingDecision");
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryUpdateGlobal(firstStore, firstCandidate, release));
        Task<bool> second = Task.Run(
            () => TryUpdateGlobal(secondStore, secondCandidate, release));
        release.Set();
        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, outcomes.Count(static won => won));
        StoredGlobalSettings stored = firstStore.GetGlobalSettings()
            ?? throw new AssertFailedException("Global settings were not loaded.");
        Assert.AreEqual(1, stored.Revision);
        Assert.IsTrue(
            stored.Snapshot.ContentDigest == firstCandidate.ContentDigest ||
            stored.Snapshot.ContentDigest == secondCandidate.ContentDigest);
        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => secondStore.UpdateGlobalSettings(
                profile.CreateSnapshot(),
                expectedRevision: 0,
                InitialTime.AddMinutes(2)));
        AssertConcurrency(
            stale,
            "global default settings",
            "singleton",
            expectedRevision: 0,
            conflictingRevision: 1);
    }

    [TestMethod]
    public void ExistingProjectRemainsTypedUnboundUntilExplicitValidBinding()
    {
        using StoreTestDatabase database = new();
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        var store = new DefaultSettingsStore(database.DatabasePath);

        ProjectSettingsResult unbound = store.GetProjectSettings(projectId);
        Assert.AreEqual(projectId, unbound.ProjectId);
        Assert.AreEqual(ProjectSettingsBindingState.Unbound, unbound.BindingState);
        Assert.IsFalse(unbound.IsBound);
        Assert.IsNull(unbound.Settings);

        DefaultSettingsProfile unregistered = CreateProfile();
        DefaultSettingsSnapshot snapshot = unregistered.CreateSnapshot();
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.InitializeGlobalSettings(snapshot, InitialTime));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.BindProjectSettings(projectId, snapshot, InitialTime));
        Assert.IsNull(store.GetGlobalSettings());
        Assert.IsFalse(store.GetProjectSettings(projectId).IsBound);

        _ = store.RegisterProfile(Encoding.UTF8.GetBytes(unregistered.CanonicalJson));
        EntityId<ProjectIdTag> missingProjectId = EntityId<ProjectIdTag>.New();
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.GetProjectSettings(missingProjectId));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.BindProjectSettings(missingProjectId, snapshot, InitialTime));

        var restarted = new DefaultSettingsStore(database.DatabasePath);
        Assert.IsFalse(restarted.GetProjectSettings(projectId).IsBound);
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM default_settings_global;"));
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_default_settings;"));
    }

    [TestMethod]
    public void ProjectInitialBindingIsImmutableAndGlobalChangesAreNotRetroactive()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile firstProfile = RegisterProfile(store);
        DefaultSettingsProfile secondProfile = CreateProfile(
            SecondProfileId,
            new SettingsProfileVersion(2, 0, 0));
        _ = store.RegisterProfile(Encoding.UTF8.GetBytes(secondProfile.CanonicalJson));
        store.InitializeGlobalSettings(firstProfile.CreateSnapshot(), InitialTime);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot initial = CreateSnapshot(
            firstProfile,
            crossfadeDuration: 250,
            publicationScope: "pendingDecision");

        StoredProjectSettings bound = store.BindProjectSettings(
            projectId,
            initial,
            InitialTime.AddMinutes(1));
        Assert.AreEqual(0, bound.Revision);
        AssertSnapshotEqual(initial, bound.InitialSnapshot);
        AssertSnapshotEqual(initial, bound.CurrentSnapshot);

        StoredProjectSettings idempotent = store.BindProjectSettings(
            projectId,
            initial,
            InitialTime.AddMinutes(9));
        Assert.AreEqual(InitialTime.AddMinutes(1), idempotent.UpdatedAt);
        Assert.AreEqual(0, idempotent.Revision);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_default_settings;"));

        DefaultSettingsSnapshot differentInitial = CreateSnapshot(
            firstProfile,
            crossfadeDuration: 500,
            publicationScope: "pendingDecision");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.BindProjectSettings(
                projectId,
                differentInitial,
                InitialTime.AddMinutes(2)));

        DefaultSettingsSnapshot newGlobal = secondProfile.CreateSnapshot();
        _ = store.UpdateGlobalSettings(
            newGlobal,
            expectedRevision: 0,
            InitialTime.AddMinutes(2));
        AssertGlobalState(
            store,
            newGlobal,
            revision: 1,
            InitialTime.AddMinutes(2));

        StoredProjectSettings unchanged = GetBoundProject(store, projectId);
        Assert.AreEqual(0, unchanged.Revision);
        AssertSnapshotEqual(initial, unchanged.InitialSnapshot);
        AssertSnapshotEqual(initial, unchanged.CurrentSnapshot);
        Assert.ThrowsExactly<ArgumentException>(
            () => store.UpdateProjectSettings(
                projectId,
                newGlobal,
                expectedRevision: 0,
                InitialTime.AddMinutes(3)));
        StoredProjectSettings stillUnchanged = GetBoundProject(store, projectId);
        Assert.AreEqual(0, stillUnchanged.Revision);
        AssertSnapshotEqual(initial, stillUnchanged.InitialSnapshot);
        AssertSnapshotEqual(initial, stillUnchanged.CurrentSnapshot);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalProjectBindingsAcrossStoresAreIdempotent()
    {
        using StoreTestDatabase database = new();
        var firstStore = new DefaultSettingsStore(database.DatabasePath);
        var secondStore = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(firstStore);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot snapshot = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "pendingDecision");
        using var release = new ManualResetEventSlim(initialState: false);

        Task<StoredProjectSettings> first = Task.Run(() =>
        {
            release.Wait();
            return firstStore.BindProjectSettings(projectId, snapshot, InitialTime);
        });
        Task<StoredProjectSettings> second = Task.Run(() =>
        {
            release.Wait();
            return secondStore.BindProjectSettings(projectId, snapshot, InitialTime);
        });
        release.Set();
        StoredProjectSettings[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(2, results.Length);
        foreach (StoredProjectSettings result in results)
        {
            Assert.AreEqual(projectId, result.ProjectId);
            Assert.AreEqual(0, result.Revision);
            Assert.AreEqual(InitialTime, result.UpdatedAt);
            AssertSnapshotEqual(snapshot, result.InitialSnapshot);
            AssertSnapshotEqual(snapshot, result.CurrentSnapshot);
        }

        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_default_settings;"));
    }

    [TestMethod]
    public async Task ConcurrentDifferentProjectBindingsAcrossStoresRejectExactlyOne()
    {
        using StoreTestDatabase database = new();
        var firstStore = new DefaultSettingsStore(database.DatabasePath);
        var secondStore = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(firstStore);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot firstSnapshot = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "internalOnly");
        DefaultSettingsSnapshot secondSnapshot = CreateSnapshot(
            profile,
            crossfadeDuration: 500,
            publicationScope: "pendingDecision");
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryBindProjectSettings(
                firstStore,
                projectId,
                firstSnapshot,
                InitialTime,
                release));
        Task<bool> second = Task.Run(
            () => TryBindProjectSettings(
                secondStore,
                projectId,
                secondSnapshot,
                InitialTime.AddMinutes(1),
                release));
        release.Set();
        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, outcomes.Count(static bound => bound));
        StoredProjectSettings stored = GetBoundProject(firstStore, projectId);
        Assert.AreEqual(0, stored.Revision);
        Assert.IsTrue(
            stored.InitialSnapshot.ContentDigest == firstSnapshot.ContentDigest ||
            stored.InitialSnapshot.ContentDigest == secondSnapshot.ContentDigest);
        AssertSnapshotEqual(stored.InitialSnapshot, stored.CurrentSnapshot);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_default_settings;"));
    }

    [TestMethod]
    public void ProjectUpdateAndResetsUseImmutableProjectInitialSnapshot()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(store);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot initial = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "pendingDecision");
        store.BindProjectSettings(projectId, initial, InitialTime);
        DefaultSettingsSnapshot customized = CreateSnapshot(
            profile,
            crossfadeDuration: 500,
            publicationScope: "internalOnly");

        StoredProjectSettings updated = store.UpdateProjectSettings(
            projectId,
            customized,
            expectedRevision: 0,
            InitialTime.AddMinutes(1));
        Assert.AreEqual(1, updated.Revision);
        AssertSnapshotEqual(initial, updated.InitialSnapshot);
        AssertSnapshotEqual(customized, updated.CurrentSnapshot);

        StoredProjectSettings oneReset = store.ResetProjectSetting(
            projectId,
            "editing.crossfadeDuration",
            expectedRevision: 1,
            InitialTime.AddMinutes(2));
        Assert.AreEqual(2, oneReset.Revision);
        AssertSnapshotEqual(initial, oneReset.InitialSnapshot);
        Assert.AreEqual(
            250L,
            ReadInteger(oneReset.CurrentSnapshot, "editing.crossfadeDuration"));
        Assert.AreEqual(
            "internalOnly",
            ReadText(oneReset.CurrentSnapshot, "project.publicationScope"));

        Assert.ThrowsExactly<KeyNotFoundException>(
            () => store.ResetProjectSetting(
                projectId,
                "editing.unknown",
                expectedRevision: 2,
                InitialTime.AddMinutes(3)));
        Assert.AreEqual(2, GetBoundProject(store, projectId).Revision);

        StoredProjectSettings allReset = store.ResetAllProjectSettings(
            projectId,
            expectedRevision: 2,
            InitialTime.AddMinutes(3));
        Assert.AreEqual(3, allReset.Revision);
        AssertSnapshotEqual(initial, allReset.InitialSnapshot);
        AssertSnapshotEqual(initial, allReset.CurrentSnapshot);

        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => store.ResetAllProjectSettings(
                projectId,
                expectedRevision: 2,
                InitialTime.AddMinutes(4)));
        AssertConcurrency(
            stale,
            "project default settings",
            projectId.ToString(),
            expectedRevision: 2,
            conflictingRevision: 3);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => store.ResetProjectSetting(
                projectId,
                "editing.crossfadeDuration",
                expectedRevision: 3,
                InitialTime.AddMinutes(2)));
        Assert.AreEqual(3, GetBoundProject(store, projectId).Revision);
    }

    [TestMethod]
    public async Task ConcurrentProjectUpdatesAcrossStoresAllowExactlyOneRevisionWinner()
    {
        using StoreTestDatabase database = new();
        var firstStore = new DefaultSettingsStore(database.DatabasePath);
        var secondStore = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(firstStore);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot initial = profile.CreateSnapshot();
        firstStore.BindProjectSettings(projectId, initial, InitialTime);
        DefaultSettingsSnapshot firstCandidate = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "internalOnly");
        DefaultSettingsSnapshot secondCandidate = CreateSnapshot(
            profile,
            crossfadeDuration: 500,
            publicationScope: "pendingDecision");
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(
            () => TryUpdateProject(
                firstStore,
                projectId,
                firstCandidate,
                release));
        Task<bool> second = Task.Run(
            () => TryUpdateProject(
                secondStore,
                projectId,
                secondCandidate,
                release));
        release.Set();
        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, outcomes.Count(static won => won));
        StoredProjectSettings stored = GetBoundProject(firstStore, projectId);
        Assert.AreEqual(1, stored.Revision);
        AssertSnapshotEqual(initial, stored.InitialSnapshot);
        Assert.IsTrue(
            stored.CurrentSnapshot.ContentDigest == firstCandidate.ContentDigest ||
            stored.CurrentSnapshot.ContentDigest == secondCandidate.ContentDigest);
        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(
            () => secondStore.UpdateProjectSettings(
                projectId,
                initial,
                expectedRevision: 0,
                InitialTime.AddMinutes(2)));
        AssertConcurrency(
            stale,
            "project default settings",
            projectId.ToString(),
            expectedRevision: 0,
            conflictingRevision: 1);
    }

    [TestMethod]
    public void QuotedProfileContentIsBoundAsDataWithoutChangingSchema()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        const string quotedName =
            "Robert'); DROP TABLE default_settings_profiles;-- \"quoted\"";
        DefaultSettingsProfile profile = CreateProfile(profileName: quotedName);
        _ = store.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
        _ = store.InitializeGlobalSettings(profile.CreateSnapshot(), InitialTime);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        _ = store.BindProjectSettings(projectId, profile.CreateSnapshot(), InitialTime);

        Assert.AreEqual(
            quotedName,
            store.GetProfile(profile.Id, profile.Version)!.Name);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'default_settings_profiles';"));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM default_settings_profiles;"));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM default_settings_global;"));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_default_settings;"));
    }

    [TestMethod]
    public void StoredProfileSnapshotDigestPayloadAndTimestampCorruptionFailClosed()
    {
        using (StoreTestDatabase profileDatabase = new())
        {
            var profileStore = new DefaultSettingsStore(profileDatabase.DatabasePath);
            DefaultSettingsProfile profile = RegisterProfile(profileStore);
            DefaultSettingsProfile tampered = CreateProfile(profileName: "Tampered content");
            profileDatabase.ExecuteNonQuery(
                """
                UPDATE default_settings_profiles
                SET profile_payload = $payload
                WHERE profile_id = $profile_id AND profile_version = $profile_version;
                """,
                ("$payload", Encoding.UTF8.GetBytes(tampered.CanonicalJson)),
                ("$profile_id", profile.Id.ToString()),
                ("$profile_version", profile.Version.ToString()));
            AssertStoredInvalid(
                () => profileStore.GetProfile(profile.Id, profile.Version),
                "default-settings profile");

            string domainInvalidCanonicalJson = profile.CanonicalJson.Replace(
                "\"key\":\"editing.crossfadeDuration\"",
                "\"key\":\"Editing.crossfadeDuration\"",
                StringComparison.Ordinal);
            profileDatabase.ExecuteNonQuery(
                """
                UPDATE default_settings_profiles
                SET profile_payload = $payload
                WHERE profile_id = $profile_id AND profile_version = $profile_version;
                """,
                ("$payload", Encoding.UTF8.GetBytes(domainInvalidCanonicalJson)),
                ("$profile_id", profile.Id.ToString()),
                ("$profile_version", profile.Version.ToString()));
            AssertStoredInvalid(
                () => profileStore.GetProfile(profile.Id, profile.Version),
                "default-settings profile");
        }

        using (StoreTestDatabase globalDatabase = new())
        {
            var globalStore = new DefaultSettingsStore(globalDatabase.DatabasePath);
            DefaultSettingsProfile profile = RegisterProfile(globalStore);
            DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
            globalStore.InitializeGlobalSettings(snapshot, InitialTime);

            globalDatabase.ExecuteNonQuery(
                "UPDATE default_settings_global SET snapshot_digest = $digest;",
                ("$digest", CorruptDigest));
            AssertStoredInvalid(
                () => globalStore.GetGlobalSettings(),
                "global default settings");

            globalDatabase.ExecuteNonQuery(
                """
                UPDATE default_settings_global
                SET snapshot_digest = $digest,
                    snapshot_payload = $payload;
                """,
                ("$digest", snapshot.ContentDigest.Hex),
                ("$payload", Encoding.UTF8.GetBytes(" " + snapshot.CanonicalJson)));
            AssertStoredInvalid(
                () => globalStore.GetGlobalSettings(),
                "global default settings");

            string domainInvalidCanonicalJson = snapshot.CanonicalJson.Replace(
                "\"text\":\"internalOnly\"",
                "\"text\":\"\"",
                StringComparison.Ordinal);
            globalDatabase.ExecuteNonQuery(
                """
                UPDATE default_settings_global
                SET snapshot_payload = $payload;
                """,
                ("$payload", Encoding.UTF8.GetBytes(domainInvalidCanonicalJson)));
            AssertStoredInvalid(
                () => globalStore.GetGlobalSettings(),
                "global default settings");

            globalDatabase.ExecuteNonQuery(
                """
                UPDATE default_settings_global
                SET snapshot_payload = $payload,
                    updated_at = 'not-a-timestamp';
                """,
                ("$payload", Encoding.UTF8.GetBytes(snapshot.CanonicalJson)));
            AssertStoredInvalid(
                () => globalStore.GetGlobalSettings(),
                "global default settings");
        }

        using (StoreTestDatabase projectDatabase = new())
        {
            var projectStore = new DefaultSettingsStore(projectDatabase.DatabasePath);
            DefaultSettingsProfile profile = RegisterProfile(projectStore);
            EntityId<ProjectIdTag> projectId = CreateStoredProject(projectDatabase);
            DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
            projectStore.BindProjectSettings(projectId, snapshot, InitialTime);

            projectDatabase.ExecuteNonQuery(
                """
                UPDATE project_default_settings
                SET current_snapshot_digest = $digest
                WHERE project_id = $project_id;
                """,
                ("$digest", CorruptDigest),
                ("$project_id", projectId.ToString()));
            AssertStoredInvalid(
                () => projectStore.GetProjectSettings(projectId),
                "project default settings");

            projectDatabase.ExecuteNonQuery(
                """
                UPDATE project_default_settings
                SET current_snapshot_digest = $digest,
                    initial_snapshot_payload = $payload
                WHERE project_id = $project_id;
                """,
                ("$digest", snapshot.ContentDigest.Hex),
                ("$payload", Encoding.UTF8.GetBytes(" " + snapshot.CanonicalJson)),
                ("$project_id", projectId.ToString()));
            AssertStoredInvalid(
                () => projectStore.GetProjectSettings(projectId),
                "project default settings");

            projectDatabase.ExecuteNonQuery(
                """
                UPDATE project_default_settings
                SET initial_snapshot_payload = $payload,
                    updated_at = 'not-a-timestamp'
                WHERE project_id = $project_id;
                """,
                ("$payload", Encoding.UTF8.GetBytes(snapshot.CanonicalJson)),
                ("$project_id", projectId.ToString()));
            AssertStoredInvalid(
                () => projectStore.GetProjectSettings(projectId),
                "project default settings");
        }
    }

    [TestMethod]
    public void ProjectRevisionZeroWithDifferentValidSnapshotsFailsClosed()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(store);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot initial = profile.CreateSnapshot();
        DefaultSettingsSnapshot differentCurrent = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "pendingDecision");
        store.BindProjectSettings(projectId, initial, InitialTime);

        database.ExecuteNonQuery(
            """
            UPDATE project_default_settings
            SET current_snapshot_digest = $digest,
                current_snapshot_payload = $payload
            WHERE project_id = $project_id;
            """,
            ("$digest", differentCurrent.ContentDigest.Hex),
            ("$payload", Encoding.UTF8.GetBytes(differentCurrent.CanonicalJson)),
            ("$project_id", projectId.ToString()));

        AssertStoredInvalid(
            () => store.GetProjectSettings(projectId),
            "project default settings");
    }

    [TestMethod]
    public void RevisionOverflowFailsClosedWithoutMutatingStoredSettings()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(store);
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
        store.InitializeGlobalSettings(snapshot, InitialTime);
        database.ExecuteNonQuery(
            "UPDATE default_settings_global SET revision = $revision;",
            ("$revision", int.MaxValue));

        Assert.ThrowsExactly<OverflowException>(
            () => store.UpdateGlobalSettings(
                snapshot,
                expectedRevision: int.MaxValue,
                InitialTime.AddMinutes(1)));

        StoredGlobalSettings stored = store.GetGlobalSettings()
            ?? throw new AssertFailedException("Global settings were not loaded.");
        Assert.AreEqual(int.MaxValue, stored.Revision);
        Assert.AreEqual(InitialTime, stored.UpdatedAt);
        AssertSnapshotEqual(snapshot, stored.Snapshot);
    }

    [TestMethod]
    public void InjectedProjectUpdateFailureRollsBackCurrentSnapshotRevisionAndTimestamp()
    {
        using StoreTestDatabase database = new();
        var store = new DefaultSettingsStore(database.DatabasePath);
        DefaultSettingsProfile profile = RegisterProfile(store);
        EntityId<ProjectIdTag> projectId = CreateStoredProject(database);
        DefaultSettingsSnapshot initial = profile.CreateSnapshot();
        store.BindProjectSettings(projectId, initial, InitialTime);
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_project_default_settings_update
            BEFORE UPDATE ON project_default_settings
            BEGIN
                SELECT RAISE(ABORT, 'injected project-settings failure');
            END;
            """);
        DefaultSettingsSnapshot replacement = CreateSnapshot(
            profile,
            crossfadeDuration: 250,
            publicationScope: "pendingDecision");

        SqliteException failure = Assert.ThrowsExactly<SqliteException>(
            () => store.UpdateProjectSettings(
                projectId,
                replacement,
                expectedRevision: 0,
                InitialTime.AddMinutes(1)));

        Assert.AreEqual(19, failure.SqliteErrorCode);
        StoredProjectSettings stored = GetBoundProject(store, projectId);
        Assert.AreEqual(0, stored.Revision);
        Assert.AreEqual(InitialTime, stored.UpdatedAt);
        AssertSnapshotEqual(initial, stored.InitialSnapshot);
        AssertSnapshotEqual(initial, stored.CurrentSnapshot);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM project_default_settings;"));
    }

    private static DefaultSettingsProfile RegisterProfile(DefaultSettingsStore store)
    {
        DefaultSettingsProfile profile = CreateProfile();
        return store.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
    }

    private static DefaultSettingsProfile CreateProfile(
        EntityId<DefaultSettingsProfileIdTag>? profileId = null,
        SettingsProfileVersion? version = null,
        string profileName = "HubRadio focused persistence defaults")
    {
        return DefaultSettingsProfile.Create(
            profileId ?? ProfileId,
            profileName,
            version ?? new SettingsProfileVersion(1, 0, 0),
            [PublicationScopeDefinition(), CrossfadeDurationDefinition()]);
    }

    private static DefaultSettingDefinition PublicationScopeDefinition() =>
        new(
            "project.publicationScope",
            DefaultSettingGroup.ProjectAndOutput,
            isAdvanced: false,
            "Publication scope",
            "Used only by focused default-settings persistence tests.",
            DefaultSettingUnit.None,
            DefaultSettingValue.FromText("internalOnly"),
            minimum: null,
            maximum: null,
            allowedTextValues: ["internalOnly", "pendingDecision"],
            evidence:
            [
                new DefaultSettingEvidence(
                    DefaultSettingEvidenceKind.ProductDecision,
                    "productDecision"),
            ]);

    private static DefaultSettingDefinition CrossfadeDurationDefinition() =>
        new(
            "editing.crossfadeDuration",
            DefaultSettingGroup.Editing,
            isAdvanced: false,
            "Crossfade duration",
            "Used only by focused default-settings persistence tests.",
            DefaultSettingUnit.Milliseconds,
            DefaultSettingValue.FromInteger(0),
            DefaultSettingValue.FromInteger(0),
            DefaultSettingValue.FromInteger(5000),
            allowedTextValues: null,
            evidence:
            [
                new DefaultSettingEvidence(
                    DefaultSettingEvidenceKind.ProductDecision,
                    "productDecision"),
            ]);

    private static DefaultSettingsSnapshot CreateSnapshot(
        DefaultSettingsProfile profile,
        long crossfadeDuration,
        string publicationScope) =>
        profile.CreateSnapshot(
        [
            new DefaultSettingSelection(
                "editing.crossfadeDuration",
                DefaultSettingValue.FromInteger(crossfadeDuration)),
            new DefaultSettingSelection(
                "project.publicationScope",
                DefaultSettingValue.FromText(publicationScope)),
        ]);

    private static EntityId<ProjectIdTag> CreateStoredProject(StoreTestDatabase database)
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        var project = new Project(
            projectId,
            "Default settings persistence test",
            "Exercise project settings persistence.",
            "Internal test audience",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        var projectStore = new ProjectStore(database.DatabasePath);
        projectStore.CreateProject(project, "{\"project\":true}", InitialTime);
        return projectId;
    }

    private static StoredProjectSettings GetBoundProject(
        DefaultSettingsStore store,
        EntityId<ProjectIdTag> projectId)
    {
        ProjectSettingsResult result = store.GetProjectSettings(projectId);
        Assert.AreEqual(ProjectSettingsBindingState.Bound, result.BindingState);
        Assert.IsTrue(result.IsBound);
        return result.Settings
            ?? throw new AssertFailedException("Bound project settings were not returned.");
    }

    private static bool TryUpdateGlobal(
        DefaultSettingsStore store,
        DefaultSettingsSnapshot snapshot,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.UpdateGlobalSettings(
                snapshot,
                expectedRevision: 0,
                InitialTime.AddMinutes(1));
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static bool TryUpdateProject(
        DefaultSettingsStore store,
        EntityId<ProjectIdTag> projectId,
        DefaultSettingsSnapshot snapshot,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.UpdateProjectSettings(
                projectId,
                snapshot,
                expectedRevision: 0,
                InitialTime.AddMinutes(1));
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static bool TryBindProjectSettings(
        DefaultSettingsStore store,
        EntityId<ProjectIdTag> projectId,
        DefaultSettingsSnapshot snapshot,
        DateTimeOffset updatedAt,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.BindProjectSettings(projectId, snapshot, updatedAt);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
            Assert.AreEqual(
                "The project is already bound to different initial default settings.",
                exception.Message);
            return false;
        }
    }

    private static void AssertGlobalState(
        DefaultSettingsStore store,
        DefaultSettingsSnapshot expectedSnapshot,
        int revision,
        DateTimeOffset updatedAt)
    {
        StoredGlobalSettings stored = store.GetGlobalSettings()
            ?? throw new AssertFailedException("Global settings were not loaded.");
        Assert.AreEqual(revision, stored.Revision);
        Assert.AreEqual(updatedAt, stored.UpdatedAt);
        AssertSnapshotEqual(expectedSnapshot, stored.Snapshot);
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

    private static void AssertConcurrency(
        StoreConcurrencyException exception,
        string entityKind,
        string entityId,
        int expectedRevision,
        int conflictingRevision)
    {
        Assert.AreEqual(entityKind, exception.EntityKind);
        Assert.AreEqual(entityId, exception.EntityId);
        Assert.AreEqual(expectedRevision, exception.ExpectedRevision);
        Assert.AreEqual(conflictingRevision, exception.ActualRevision);
    }

    private static long ReadInteger(DefaultSettingsSnapshot snapshot, string key) =>
        snapshot.Values.Single(value =>
            string.Equals(value.Key, key, StringComparison.Ordinal)).Value.IntegerValue
            ?? throw new AssertFailedException("The selected setting is not an integer.");

    private static string ReadText(DefaultSettingsSnapshot snapshot, string key) =>
        snapshot.Values.Single(value =>
            string.Equals(value.Key, key, StringComparison.Ordinal)).Value.TextValue
            ?? throw new AssertFailedException("The selected setting is not text.");

    private static void AssertStoredInvalid(Action read, string entityKind)
    {
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(read);
        Assert.AreEqual($"Stored {entityKind} data is invalid.", exception.Message);
    }
}
