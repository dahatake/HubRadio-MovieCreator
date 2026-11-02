using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace HubRadio.System.Contracts;

/// <summary>
/// Native request/authorization/persistence contract. Uses the current Windows
/// actor and real, isolated role database. No accounts, ACLs, installed settings,
/// UI windows, or application sources are modified. A picker spy observes whether
/// the native dialog boundary was reached, never opens a dialog.
/// </summary>
[TestClass]
public sealed class PermissionContracts
{
    // FR-064 AC-041: forbidden before a picker can open; closed response only.
    [TestMethod]
    public async Task FR064_AC041_UngrantedCurrentActorCannotBrowseOrLeak()
    {
        using Fixture fixture = new();
        string configurationDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "config")).FullName;
        var store = new WorkspaceLocationStore(configurationDirectory);
        store.Save(fixture.Destination);
        byte[] before = File.ReadAllBytes(store.ConfigPath);
        var picker = new PickerSpy(fixture.Destination);
        var handler = new WorkspaceLocationBridgeHandler(
            store, new SettingsBridgeAuthorization(fixture.Authorization, fixture.Actor),
            fixture.Destination, picker);
        BridgeRequest request = Request("settings.workspace.browse", new { });

        Assert.IsEmpty(fixture.Authorization.GetRoles(fixture.Sid));
        string response = await handler.HandleBrowseAsync(request, CancellationToken.None);
        JsonElement result = ClosedResponse(response, request, "status", "path");
        Assert.AreEqual("forbidden", result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("path").ValueKind);
        Assert.AreEqual(0, picker.Calls, "a denied operation must never reach the native dialog");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(store.ConfigPath));
        AssertNoPrivateData(response, fixture);

        // Positive control: proves a functioning picker/request/config fixture,
        // not an always-unavailable handler or an invalid request.
        Assert.IsTrue(fixture.Authorization.BootstrapInitialRoles(fixture.Sid));
        response = await handler.HandleBrowseAsync(request, CancellationToken.None);
        result = ClosedResponse(response, request, "status", "path");
        Assert.AreEqual("selected", result.GetProperty("status").GetString());
        Assert.AreEqual(fixture.Destination, result.GetProperty("path").GetString());
        Assert.AreEqual(1, picker.Calls);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(store.ConfigPath));
    }

    // FR-132 AC-139: no lookup, stream open, file, or private information when
    // the real role service denies the current Windows actor's export.
    [TestMethod]
    public async Task FR132_AC139_UngrantedCurrentActorCannotWriteOrLeak()
    {
        using Fixture fixture = new();
        string sentinel = Path.Combine(fixture.Destination, "existing.txt");
        File.WriteAllText(sentinel, "must remain byte-for-byte unchanged");
        string source = Path.Combine(fixture.Root, "synthetic.mp4");
        byte[] content = [1, 3, 5, 7, 9];
        File.WriteAllBytes(source, content);
        byte[] sentinelBefore = File.ReadAllBytes(sentinel);
        DateTime sentinelTime = File.GetLastWriteTimeUtc(sentinel);
        int lookups = 0, opens = 0;
        var handler = new JobExportBridgeHandler(
            () => fixture.Authorization.CanPerform(fixture.Actor.GetCurrentSid(), AuthorizationOperation.ExportComposition),
            (_, _, _) =>
            {
                lookups++;
                JobExportItem item = new(JobExportKind.LongClean, "長尺クリーン", ".mp4", _ =>
                {
                    opens++;
                    return Task.FromResult<Stream?>(File.OpenRead(source));
                });
                return Task.FromResult<JobExportSource?>(new("Synthetic project", DateTimeOffset.UtcNow, [item]));
            });
        BridgeRequest request = Request("results.export", new
        {
            projectId = Guid.NewGuid().ToString("D"),
            executionId = Guid.NewGuid().ToString("D"),
            destination = fixture.Destination,
            jobName = "Synthetic job",
        });

        Assert.IsEmpty(fixture.Authorization.GetRoles(fixture.Sid));
        string response = await handler.HandleAsync(request, CancellationToken.None);
        JsonElement result = ClosedResponse(response, request, "status", "destinationReason", "savedCount", "files", "failures");
        Assert.AreEqual("forbidden", result.GetProperty("status").GetString());
        Assert.AreEqual(0, result.GetProperty("savedCount").GetInt32());
        Assert.AreEqual(0, result.GetProperty("files").GetArrayLength());
        Assert.AreEqual(0, result.GetProperty("failures").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("destinationReason").ValueKind);
        Assert.AreEqual(0, lookups);
        Assert.AreEqual(0, opens);
        CollectionAssert.AreEqual(new[] { sentinel }, Directory.GetFileSystemEntries(fixture.Destination, "*", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(sentinelBefore, File.ReadAllBytes(sentinel));
        Assert.AreEqual(sentinelTime, File.GetLastWriteTimeUtc(sentinel));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(source));
        AssertNoPrivateData(response, fixture);

        // Positive control: same real authorization, request, resolver and writer
        // become usable after granting app-local roles in the temporary DB.
        Assert.IsTrue(fixture.Authorization.BootstrapInitialRoles(fixture.Sid));
        response = await handler.HandleAsync(request, CancellationToken.None);
        result = ClosedResponse(response, request, "status", "destinationReason", "savedCount", "files", "failures");
        Assert.AreEqual("completed", result.GetProperty("status").GetString());
        Assert.AreEqual(1, result.GetProperty("savedCount").GetInt32());
        Assert.AreEqual(1, lookups);
        Assert.AreEqual(1, opens);
        string exported = Directory.GetFiles(fixture.Destination, "*.mp4").Single();
        CollectionAssert.AreEqual(SHA256.HashData(content), SHA256.HashData(File.ReadAllBytes(exported)));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(sentinelBefore, File.ReadAllBytes(sentinel));
    }

    private static BridgeRequest Request(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1, requestId = Guid.NewGuid().ToString("D"), method, @params = parameters,
        });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out BridgeRequest? request, out _),
            "The public request envelope must be accepted before testing authorization");
        return request!;
    }

    private static JsonElement ClosedResponse(string response, BridgeRequest request, params string[] allowedResultKeys)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement envelope = document.RootElement;
        CollectionAssert.AreEquivalent(new[] { "version", "requestId", "method", "result" },
            envelope.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(1, envelope.GetProperty("version").GetInt32());
        Assert.AreEqual(BridgeProtocol.GetMethodName(request.Method), envelope.GetProperty("method").GetString());
        JsonElement result = envelope.GetProperty("result").Clone();
        CollectionAssert.AreEquivalent(allowedResultKeys, result.EnumerateObject().Select(p => p.Name).ToArray());
        return result;
    }

    private static void AssertNoPrivateData(string response, Fixture fixture)
    {
        // Enumerating every allowed field above also catches arbitrary added
        // exception/path details, beyond these explicit recognizable markers.
        foreach (string marker in new[] { "Exception", "S-1-", fixture.Root, fixture.Sid, "StackTrace" })
            Assert.IsFalse(response.Contains(marker, StringComparison.OrdinalIgnoreCase), $"private marker {marker}");
        using JsonDocument document = JsonDocument.Parse(response);
        foreach (JsonProperty property in document.RootElement.GetProperty("result").EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && property.Name != "status")
                Assert.Fail($"Denied result exposes text in {property.Name}");
    }

    private sealed class PickerSpy(string selected) : IWorkspaceFolderPicker
    {
        internal int Calls { get; private set; }
        public Task<string?> PickFolderAsync(string? startFolder, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<string?>(selected);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "hr-permission-contract-" + Guid.NewGuid().ToString("N"));
        internal string Destination { get; }
        internal WindowsActorProvider Actor { get; } = new();
        internal string Sid { get; }
        internal AuthorizationService Authorization { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Destination = Directory.CreateDirectory(Path.Combine(Root, "destination")).FullName;
            string database = Path.Combine(Root, "roles.db");
            _ = DatabaseMigrator.Migrate(database, Path.Combine(Root, "backups"));
            Authorization = new(new RoleAssignmentStore(database));
            Sid = Actor.GetCurrentSid();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
