using System.Text.Json;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SettingsBridgeTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    // FR-033 AC-081: 設定画面の bridge が profile の項目・現在値・初期値を射影する。
    public void ReadProjectsActualProfileInventoryWithoutHardCodedCount()
    {
        using Fixture fixture = new();
        Assert.AreEqual(127, fixture.Profile.Definitions.Count);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<DefaultSettingValueKind>(),
            fixture.Profile.Definitions
                .Select(static definition => definition.DefaultValue.Kind)
                .Distinct()
                .ToArray());
        string response = fixture.Handle("settings.read", new { projectId = fixture.ProjectId.ToString() });
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement snapshot = document.RootElement.GetProperty("result").GetProperty("snapshot");
        Assert.AreEqual(fixture.Profile.Definitions.Count, snapshot.GetProperty("definitionCount").GetInt32());
        CollectionAssert.AreEqual(
            fixture.Profile.Definitions.Select(static definition => definition.Key).ToArray(),
            snapshot.GetProperty("definitions").EnumerateArray().Select(static definition => definition.GetProperty("key").GetString()).ToArray());
    }

    [TestMethod]
    // FR-033 AC-081: 設定値が Project に対応付けて保存され、初期値へ戻せる。
    public void UpdatePersistsAcrossRestartAndResetReturnsToProjectInitialValue()
    {
        using Fixture fixture = new();
        DefaultSettingDefinition definition = fixture.Profile.GetDefinition("subtitles.enabled");
        Assert.AreEqual(DefaultSettingValueKind.Boolean, definition.DefaultValue.Kind);
        JsonElement initial = fixture.ReadSnapshot();
        string updatedResponse = fixture.Handle(
            "settings.update",
            Mutation(initial, new
            {
                key = definition.Key,
                value = new { kind = "boolean", booleanValue = !definition.DefaultValue.BooleanValue!.Value },
            }));
        JsonElement updated = ReadSnapshot(updatedResponse);
        Assert.AreEqual(1, updated.GetProperty("revision").GetInt32());

        var restartedHandler = new SettingsBridgeRequestHandler(
            new DefaultSettingsStore(fixture.DatabasePath),
            fixture.Authorization,
            () => InitialTime.AddMinutes(2));
        JsonElement persisted = ReadSnapshot(fixture.Handle(restartedHandler, "settings.read", new { projectId = fixture.ProjectId.ToString() }));
        Assert.AreEqual(updated.GetProperty("snapshotDigest").GetString(), persisted.GetProperty("snapshotDigest").GetString());

        JsonElement reset = ReadSnapshot(fixture.Handle(
            restartedHandler,
            "settings.reset",
            Mutation(persisted, new { key = definition.Key })));
        JsonElement selection = reset.GetProperty("values").EnumerateArray().Single(value => value.GetProperty("key").GetString() == definition.Key);
        Assert.AreEqual(definition.DefaultValue.BooleanValue, selection.GetProperty("value").GetProperty("booleanValue").GetBoolean());
    }

    [TestMethod]
    public void StaleSnapshotIdentityConflictsWithoutOverwriting()
    {
        using Fixture fixture = new();
        JsonElement snapshot = fixture.ReadSnapshot();
        var stale = new Dictionary<string, object?>
        {
            ["projectId"] = snapshot.GetProperty("projectId").GetString(),
            ["expectedRevision"] = snapshot.GetProperty("revision").GetInt32(),
            ["expectedProfileId"] = snapshot.GetProperty("profileId").GetString(),
            ["expectedProfileVersion"] = snapshot.GetProperty("profileVersion").GetString(),
            ["expectedProfileDigest"] = snapshot.GetProperty("profileDigest").GetString(),
            ["expectedSnapshotDigest"] = new string('0', 64),
            ["key"] = "subtitles.enabled",
            ["value"] = new { kind = "boolean", booleanValue = false },
        };
        string response = fixture.Handle("settings.update", stale);
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual("conflict", document.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.AreEqual(0, fixture.Store.GetProjectSettings(fixture.ProjectId).Settings!.Revision);
    }

    [TestMethod]
    public void AuthorizationOccursBeforeMissingProjectLookup()
    {
        using Fixture fixture = new();
        fixture.Authorization.Deny = true;
        EntityId<ProjectIdTag> missing = EntityId<ProjectIdTag>.New();
        string response = fixture.Handle("settings.read", new { projectId = missing.ToString() });
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.AreEqual("forbidden", document.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.IsFalse(response.Contains(missing.ToString(), StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResetAllRestoresTheImmutableProjectInitialSnapshot()
    {
        using Fixture fixture = new();
        DefaultSettingDefinition definition = fixture.Profile.GetDefinition(
            "subtitles.enabled");
        JsonElement initial = fixture.ReadSnapshot();
        JsonElement updated = ReadSnapshot(fixture.Handle(
            "settings.update",
            Mutation(initial, new
            {
                key = definition.Key,
                value = new
                {
                    kind = "boolean",
                    booleanValue = !definition.DefaultValue.BooleanValue!.Value,
                },
            })));

        JsonElement reset = ReadSnapshot(fixture.Handle(
            "settings.resetAll",
            Mutation(updated, new { })));

        Assert.AreEqual(2, reset.GetProperty("revision").GetInt32());
        Assert.AreEqual(
            reset.GetProperty("initialSnapshotDigest").GetString(),
            reset.GetProperty("snapshotDigest").GetString());
        JsonElement selection = reset.GetProperty("values")
            .EnumerateArray()
            .Single(value => value.GetProperty("key").GetString() == definition.Key);
        Assert.AreEqual(
            definition.DefaultValue.BooleanValue,
            selection.GetProperty("value").GetProperty("booleanValue").GetBoolean());
    }

    [TestMethod]
    public void MissingUnboundAndInvalidRequestsReturnClosedStatusesWithoutMutation()
    {
        using Fixture fixture = new();
        EntityId<ProjectIdTag> missing = EntityId<ProjectIdTag>.New();
        AssertStatus(
            fixture.Handle("settings.read", new { projectId = missing.ToString() }),
            "notFound");

        EntityId<ProjectIdTag> unbound = EntityId<ProjectIdTag>.New();
        var project = new Project(
            unbound,
            "Unbound settings test",
            "Settings bridge",
            "Internal",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        new ProjectStore(fixture.DatabasePath).CreateProject(
            project,
            "{\"project\":true}",
            InitialTime);
        AssertStatus(
            fixture.Handle("settings.read", new { projectId = unbound.ToString() }),
            "unbound");

        JsonElement snapshot = fixture.ReadSnapshot();
        AssertStatus(
            fixture.Handle(
                "settings.update",
                Mutation(snapshot, new
                {
                    key = "unknown.key",
                    value = new { kind = "boolean", booleanValue = false },
                })),
            "invalid");
        Assert.AreEqual(
            0,
            fixture.Store.GetProjectSettings(fixture.ProjectId).Settings!.Revision);
    }

    private static void AssertStatus(string response, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.AreEqual(expected, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
    }

    private static object Mutation(JsonElement snapshot, object tail)
    {
        var values = new Dictionary<string, object?>
        {
            ["projectId"] = snapshot.GetProperty("projectId").GetString(),
            ["expectedRevision"] = snapshot.GetProperty("revision").GetInt32(),
            ["expectedProfileId"] = snapshot.GetProperty("profileId").GetString(),
            ["expectedProfileVersion"] = snapshot.GetProperty("profileVersion").GetString(),
            ["expectedProfileDigest"] = snapshot.GetProperty("profileDigest").GetString(),
            ["expectedSnapshotDigest"] = snapshot.GetProperty("snapshotDigest").GetString(),
        };
        foreach (JsonProperty property in JsonSerializer.SerializeToElement(tail).EnumerateObject())
        {
            values[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
        }
        return values;
    }

    private static JsonElement ReadSnapshot(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").GetProperty("snapshot").Clone();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        internal Fixture()
        {
            root = Path.Combine(Path.GetTempPath(), nameof(SettingsBridgeTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DatabasePath = Path.Combine(root, "settings.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(root, "backups"));
            Store = new DefaultSettingsStore(DatabasePath);
            byte[] profileBytes = File.ReadAllBytes(ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.2.0.json"));
            Profile = BundledDefaultSettingsProfileLoader.Load(profileBytes);
            _ = Store.RegisterProfile(profileBytes);
            _ = Store.InitializeGlobalSettings(Profile.CreateSnapshot(), InitialTime);
            ProjectId = EntityId<ProjectIdTag>.New();
            var project = new Project(ProjectId, "Settings test", "Settings bridge", "Internal", PublicationScope.InternalOnly, null, [], []);
            new ProjectStore(DatabasePath).CreateProject(project, "{\"project\":true}", InitialTime);
            _ = Store.BindProjectSettings(ProjectId, Profile.CreateSnapshot(), InitialTime);
            Authorization = new FakeAuthorization();
            Handler = new SettingsBridgeRequestHandler(Store, Authorization, () => InitialTime.AddMinutes(1));
        }
        internal string DatabasePath { get; }
        internal EntityId<ProjectIdTag> ProjectId { get; }
        internal DefaultSettingsProfile Profile { get; }
        internal DefaultSettingsStore Store { get; }
        internal FakeAuthorization Authorization { get; }
        private SettingsBridgeRequestHandler Handler { get; }
        internal JsonElement ReadSnapshot() => SettingsBridgeTests.ReadSnapshot(Handle("settings.read", new { projectId = ProjectId.ToString() }));
        internal string Handle(string method, object parameters) => Handle(Handler, method, parameters);
        internal string Handle(SettingsBridgeRequestHandler handler, string method, object parameters)
        {
            string json = JsonSerializer.Serialize(new { version = 1, requestId = Guid.NewGuid().ToString("D"), method, @params = parameters });
            Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
            return handler.Handle(request!);
        }
        public void Dispose() { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private sealed class FakeAuthorization : ISettingsBridgeAuthorization
    {
        internal bool Deny { get; set; }
        internal int ReadDemandCount { get; private set; }
        public void DemandRead() { ReadDemandCount++; if (Deny) throw new HubRadio.App.Security.AuthorizationException(AuthorizationOperation.ViewProject, null); }
        public void DemandEdit() { if (Deny) throw new HubRadio.App.Security.AuthorizationException(AuthorizationOperation.CreateCompositionVersion, AuthorizationRole.Editor); }
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) return Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
