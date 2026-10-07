using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Security;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Tests;

/// <summary>FR-062 / AC-037.3: settings.workspace.* bridge methods.</summary>
[TestClass]
public sealed class WorkspaceLocationBridgeTests
{
    private string root = string.Empty;
    private string app = string.Empty;
    private string current = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "hr-wsbridge-" + Guid.NewGuid().ToString("N"));
        app = Path.Combine(root, "app");
        current = Path.Combine(root, "current-ws");
        Directory.CreateDirectory(app);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void FR062_AC037_3_ReadReturnsExactlyThreePathsAndNoRestartWhenUnchanged()
    {
        var store = new WorkspaceLocationStore(app);
        store.Save(current);
        var auth = new FakeAuthorization();

        JsonElement result = Run(new(store, auth, current), "settings.workspace.read", new { });

        CollectionAssert.AreEqual(
            new[] { "status", "currentPath", "savedPath", "defaultPath", "restartRequired" },
            result.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(current, result.GetProperty("currentPath").GetString());
        Assert.AreEqual(current, result.GetProperty("savedPath").GetString());
        Assert.AreEqual(store.DefaultPath, result.GetProperty("defaultPath").GetString());
        Assert.IsFalse(result.GetProperty("restartRequired").GetBoolean());
        Assert.AreEqual(1, auth.ReadDemands);
        Assert.AreEqual(0, auth.EditDemands);
    }

    [TestMethod]
    public void FR062_AC037_3_UpdateSavesAndRequiresRestartWhenLocationDiffers()
    {
        var store = new WorkspaceLocationStore(app);
        var auth = new FakeAuthorization();
        string target = Path.Combine(root, "next-ws");

        JsonElement result = Run(new(store, auth, current), "settings.workspace.update", new { path = target });

        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(current, result.GetProperty("currentPath").GetString());
        Assert.AreEqual(target, result.GetProperty("savedPath").GetString());
        Assert.IsTrue(result.GetProperty("restartRequired").GetBoolean());
        Assert.AreEqual(target, store.Resolve().SavedPath);
        Assert.IsFalse(Directory.Exists(target));
        Assert.AreEqual(1, auth.EditDemands);
    }

    [TestMethod]
    public void FR062_AC037_3_UpdateToCurrentLocationNeedsNoRestart()
    {
        var store = new WorkspaceLocationStore(app);

        JsonElement result = Run(
            new(store, new FakeAuthorization(), current), "settings.workspace.update", new { path = current });

        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.IsFalse(result.GetProperty("restartRequired").GetBoolean());
    }

    [TestMethod]
    public void FR062_AC037_3_NullResetsToDefault()
    {
        var store = new WorkspaceLocationStore(app);
        store.Save(Path.Combine(root, "saved-ws"));
        var handler = new WorkspaceLocationBridgeHandler(store, new FakeAuthorization(), current);

        JsonElement result = Run(handler, "settings.workspace.update", new { path = (string?)null });

        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("savedPath").ValueKind);
        Assert.AreEqual(store.DefaultPath, result.GetProperty("defaultPath").GetString());
        Assert.IsTrue(result.GetProperty("restartRequired").GetBoolean());
        Assert.IsFalse(File.Exists(store.ConfigPath));
    }

    [TestMethod]
    public void FR062_AC037_3_RestartNotRequiredWhenResetMatchesCurrentDefault()
    {
        var store = new WorkspaceLocationStore(app);
        var handler = new WorkspaceLocationBridgeHandler(store, new FakeAuthorization(), store.DefaultPath);

        JsonElement result = Run(handler, "settings.workspace.update", new { path = (string?)null });

        Assert.IsFalse(result.GetProperty("restartRequired").GetBoolean());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("relative\\dir")]
    [DataRow("\\\\server\\share\\ws")]
    [DataRow("C:\\a\\..\\ws")]
    public void FR062_AC037_3_InvalidPathReturnsInvalidWithNullsAndKeepsConfig(string path)
    {
        var store = new WorkspaceLocationStore(app);
        string good = Path.Combine(root, "good-ws");
        store.Save(good);

        JsonElement result = Run(
            new(store, new FakeAuthorization(), current), "settings.workspace.update", new { path });

        AssertFailure(result, "invalid");
        Assert.AreEqual(good, store.Resolve().SavedPath);
    }

    [TestMethod]
    public void FR062_AC037_3_ReadOnlyRoleIsForbiddenForUpdateButNotRead()
    {
        var store = new WorkspaceLocationStore(app);
        var auth = new FakeAuthorization { DenyEdit = true };
        var handler = new WorkspaceLocationBridgeHandler(store, auth, current);

        AssertFailure(Run(handler, "settings.workspace.update", new { path = Path.Combine(root, "x") }), "forbidden");
        Assert.IsFalse(File.Exists(store.ConfigPath));
        Assert.AreEqual("available", Run(handler, "settings.workspace.read", new { }).GetProperty("status").GetString());

        auth.DenyRead = true;
        AssertFailure(Run(handler, "settings.workspace.read", new { }), "forbidden");
    }

    [TestMethod]
    public void FR062_AC037_3_BrokenConfigIsUnavailableWithoutLeakingText()
    {
        var store = new WorkspaceLocationStore(app);
        File.WriteAllText(store.ConfigPath, "{ broken");
        var handler = new WorkspaceLocationBridgeHandler(store, new FakeAuthorization(), current);

        string response = handler.Handle(Parse("settings.workspace.read", new { }));

        AssertFailure(JsonDocument.Parse(response).RootElement.GetProperty("result"), "unavailable");
        Assert.IsFalse(response.Contains(app.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FR062_AC037_3_WithoutHandlerTheMethodsAreUnavailable()
    {
        foreach ((string method, object parameters) in new (string, object)[]
        {
            ("settings.workspace.read", new { }),
            ("settings.workspace.update", new { path = (string?)null }),
        })
        {
            BridgeRequest request = Parse(method, parameters);
            string response = WebViewHost.HandleSettingsRequest(
                request, null, null, null, CancellationToken.None);
            AssertFailure(JsonDocument.Parse(response).RootElement.GetProperty("result"), "unavailable");
        }
    }

    [TestMethod]
    public void FR062_AC037_3_HostRoutesToTheHandler()
    {
        var store = new WorkspaceLocationStore(app);
        var handler = new WorkspaceLocationBridgeHandler(store, new FakeAuthorization(), current);

        string response = WebViewHost.HandleSettingsRequest(
            Parse("settings.workspace.read", new { }), null, null, null, CancellationToken.None, handler);

        Assert.AreEqual(
            "available",
            JsonDocument.Parse(response).RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [TestMethod]
    public void FR062_AC037_3_MethodsAreFiniteSettingsMethods()
    {
        foreach (string method in new[] { "settings.workspace.read", "settings.workspace.update" })
        {
            BridgeRequest request = Parse(method, method.EndsWith("read", StringComparison.Ordinal)
                ? new { }
                : new { path = (string?)null });
            Assert.IsTrue(BridgeProtocol.IsSettingsMethod(request.Method));
            Assert.AreEqual(method, BridgeProtocol.GetMethodName(request.Method));
        }
    }

    [TestMethod]
    [DataRow("settings.workspace.read", "{\"path\":null}")]
    [DataRow("settings.workspace.read", "null")]
    [DataRow("settings.workspace.update", "{}")]
    [DataRow("settings.workspace.update", "{\"path\":1}")]
    [DataRow("settings.workspace.update", "{\"path\":false}")]
    [DataRow("settings.workspace.update", "{\"path\":\"C:\\\\a\",\"extra\":1}")]
    [DataRow("settings.workspace.update", "{\"path\":\"C:\\\\a\",\"path\":\"C:\\\\b\"}")]
    public void FR062_AC037_3_ParametersAreExact(string method, string parameters)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{Guid.NewGuid():D}\",\"method\":\"{method}\",\"params\":{parameters}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, message, out BridgeRequest? request, out _));
        Assert.IsNull(request);
    }

    [TestMethod]
    public void FR062_AC037_3_PathLengthIsCappedAt260Characters()
    {
        string Message(int length) =>
            $"{{\"version\":1,\"requestId\":\"{Guid.NewGuid():D}\",\"method\":\"settings.workspace.update\",\"params\":{{\"path\":\"{new string('a', length)}\"}}}}";

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, Message(260), out _, out _));
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, Message(261), out _, out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }

    // AC-037.4 (shared startup helper used by all three runtimes)
    [TestMethod]
    public void FR062_AC037_4_BootstrapUsesTheResolvedLocation()
    {
        string target = Path.Combine(root, "boot-ws");
        new WorkspaceLocationStore(app).Save(target);
        string sid = new WindowsActorProvider().GetCurrentSid();

        string prepared = WorkspaceFolderBootstrap.Prepare(app, sid);

        Assert.AreEqual(target, prepared);
        Assert.IsTrue(Directory.Exists(target));
        Assert.AreEqual(target, WorkspaceFolderBootstrap.Prepare(app, sid));
    }

    private static void AssertFailure(JsonElement result, string status)
    {
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("currentPath").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("savedPath").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("defaultPath").ValueKind);
        Assert.IsFalse(result.GetProperty("restartRequired").GetBoolean());
    }

    private static JsonElement Run(WorkspaceLocationBridgeHandler handler, string method, object parameters)
    {
        BridgeRequest request = Parse(method, parameters);
        using JsonDocument document = JsonDocument.Parse(handler.Handle(request));
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(
            new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.AreEqual(method, root.GetProperty("method").GetString());
        return root.GetProperty("result").Clone();
    }

    private static BridgeRequest Parse(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            requestId = Guid.NewGuid().ToString("D"),
            method,
            @params = parameters,
        });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint, json, out BridgeRequest? request, out BridgeRequestRejection rejection),
            rejection.ToString());
        return request!;
    }

    private sealed class FakeAuthorization : ISettingsBridgeAuthorization
    {
        internal bool DenyRead { get; set; }

        internal bool DenyEdit { get; set; }

        internal int ReadDemands { get; private set; }

        internal int EditDemands { get; private set; }

        public void DemandRead()
        {
            ReadDemands++;
            if (DenyRead)
            {
                throw new AuthorizationException(AuthorizationOperation.ViewProject, null);
            }
        }

        public void DemandEdit()
        {
            EditDemands++;
            if (DenyEdit)
            {
                throw new AuthorizationException(
                    AuthorizationOperation.CreateCompositionVersion, AuthorizationRole.Editor);
            }
        }
    }
}
