using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class WebViewHostTests
{
    [TestMethod]
    public void BundledDocument_UsesStrictLocalOnlyContentSecurityPolicy()
    {
        string html = File.ReadAllText(
            ResolveRepositoryPath("src/HubRadio.Web/index.html"));

        StringAssert.Contains(html, "default-src 'none'");
        StringAssert.Contains(html, "script-src 'self'");
        StringAssert.Contains(html, "style-src 'self'");
        StringAssert.Contains(html, "connect-src 'none'");
        StringAssert.Contains(html, "font-src 'self'");
        StringAssert.Contains(
            html,
            "img-src 'self' data: https://player-media.appassets.example");
        StringAssert.Contains(
            html,
            "media-src https://player-media.appassets.example");
        StringAssert.Contains(html, "object-src 'none'");
        StringAssert.Contains(html, "base-uri 'none'");
        StringAssert.Contains(html, "form-action 'none'");
        StringAssert.Contains(html, "frame-src 'none'");
        StringAssert.Contains(html, "worker-src 'none'");
        Assert.IsFalse(html.Contains("'unsafe-inline'", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("'unsafe-eval'", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("media-src 'self'", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("http://", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TopLevelPolicy_AllowsOnlyExactBundledEntryPoint()
    {
        Assert.IsTrue(WebViewHost.IsAllowedTopLevelUri(WebViewHost.EntryPoint));

        string[] rejected =
        [
            "file:///C:/private/media.mp4",
            "http://appassets.example/index.html",
            "https://example.com/index.html",
            "https://appassets.example/",
            "https://appassets.example/assets/index.js",
            "https://appassets.example/index.html?next=https://example.com",
            "https://appassets.example/index.html#route",
            "https://user@appassets.example/index.html",
            "https://appassets.example:444/index.html",
            "javascript:alert(1)",
            "data:text/html,untrusted",
            "not-a-uri",
            "",
        ];

        foreach (string uri in rejected)
        {
            Assert.IsFalse(WebViewHost.IsAllowedTopLevelUri(uri), uri);
        }
    }

    [TestMethod]
    public void ResourcePolicy_AllowsOnlyBundledHttpsOrigin()
    {
        string[] allowed =
        [
            "https://appassets.example/index.html",
            "https://appassets.example/assets/index.js",
            "https://appassets.example/assets/index.css",
        ];
        foreach (string uri in allowed)
        {
            Assert.IsTrue(WebViewHost.IsAllowedResourceUri(uri), uri);
        }

        string[] rejected =
        [
            "file:///C:/private/media.mp4",
            "http://appassets.example/assets/index.js",
            "https://appassets.example.evil.test/assets/index.js",
            "https://example.com/remote.js",
            "https://user@appassets.example/assets/index.js",
            "https://appassets.example:444/assets/index.js",
            "javascript:alert(1)",
            "data:text/javascript,alert(1)",
            "",
        ];
        foreach (string uri in rejected)
        {
            Assert.IsFalse(WebViewHost.IsAllowedResourceUri(uri), uri);
        }
    }

    [TestMethod]
    public void MediaOriginPolicy_IsDistinctReservedAndHttpsOnly()
    {
        string mediaUri =
            "https://player-media.appassets.example/media/v1/" +
            "11111111-1111-4111-8111-111111111111/proxy/" +
            "22222222-2222-4222-8222-222222222222/" +
            new string('a', 64);
        Assert.IsTrue(WebViewHost.IsAllowedMediaOrigin(mediaUri));
        Assert.IsTrue(WebViewHost.IsAllowedMediaAssetUri(mediaUri));
        Assert.IsFalse(WebViewHost.IsAllowedOrigin(mediaUri));
        Assert.IsFalse(WebViewHost.IsAllowedResourceUri(mediaUri));

        string[] rejectedOrigins =
        [
            mediaUri.Replace("https://", "http://", StringComparison.Ordinal),
            mediaUri.Replace(
                WebViewHost.MediaHostName,
                WebViewHost.VirtualHostName,
                StringComparison.Ordinal),
            mediaUri.Replace(
                WebViewHost.MediaHostName,
                string.Concat(WebViewHost.MediaHostName, ".evil.test"),
                StringComparison.Ordinal),
            mediaUri.Replace(
                WebViewHost.MediaHostName,
                string.Concat("user@", WebViewHost.MediaHostName),
                StringComparison.Ordinal),
            mediaUri.Replace(
                WebViewHost.MediaHostName,
                string.Concat(WebViewHost.MediaHostName, ":444"),
                StringComparison.Ordinal),
        ];
        foreach (string uri in rejectedOrigins)
        {
            Assert.IsFalse(WebViewHost.IsAllowedMediaOrigin(uri), uri);
            Assert.IsFalse(WebViewHost.IsAllowedMediaAssetUri(uri), uri);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Startup_StartsBridgeBeforeNavigationAndRequiresBothSignals(
        bool pingFirst)
    {
        TaskCompletionSource navigation = NewCompletion();
        TaskCompletionSource ping = NewCompletion();
        (pingFirst ? ping : navigation).SetResult();
        List<string> calls = [];

        Task startup = WebViewHost.StartDocumentAsync(
            () => calls.Add("bridge"),
            () => calls.Add("navigate"),
            navigation.Task,
            ping.Task,
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "bridge", "navigate" }, calls);
        Assert.IsFalse(startup.IsCompleted);
        (pingFirst ? navigation : ping).SetResult();
        await startup.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Startup_TimesOutIfNavigationOrAutomaticPingIsMissing(
        bool navigationMissing)
    {
        TaskCompletionSource missing = NewCompletion();
        Task startup = WebViewHost.StartDocumentAsync(
            () => { },
            () => { },
            navigationMissing ? missing.Task : Task.CompletedTask,
            navigationMissing ? Task.CompletedTask : missing.Task,
            CancellationToken.None,
            timeoutMilliseconds: 0);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => startup);
        Assert.IsFalse(startup.IsCompletedSuccessfully);
        missing.SetResult();
    }

    [TestMethod]
    public async Task Startup_NavigationFailureDoesNotWaitForPing()
    {
        InvalidOperationException failure = new("Navigation failed.");
        Task startup = WebViewHost.StartDocumentAsync(
            () => { },
            () => { },
            Task.FromException(failure),
            NewCompletion().Task,
            CancellationToken.None,
            timeoutMilliseconds: 0);

        Assert.AreSame(failure,
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => startup));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Startup_CancellationStopsWaiting(bool navigationPending)
    {
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource pending = NewCompletion();
        Task startup = WebViewHost.StartDocumentAsync(
            () => { },
            () => { },
            navigationPending ? pending.Task : Task.CompletedTask,
            navigationPending ? Task.CompletedTask : pending.Task,
            lifetime.Token);

        Assert.IsFalse(startup.IsCompleted);
        lifetime.Cancel();
        OperationCanceledException failure =
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => startup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(lifetime.Token, failure.CancellationToken);
        pending.SetCanceled(lifetime.Token);
    }

    [TestMethod]
    public async Task Startup_PreCanceledDoesNotInvokeDelegates()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        int calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            WebViewHost.StartDocumentAsync(
                () => calls++,
                () => calls++,
                Task.CompletedTask,
                Task.CompletedTask,
                cancellation.Token));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Startup_BridgeSetupFailureDoesNotNavigate()
    {
        InvalidOperationException failure = new("Bridge setup failed.");
        bool navigated = false;

        Assert.AreSame(failure,
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                WebViewHost.StartDocumentAsync(
                    () => throw failure,
                    () => navigated = true,
                    Task.CompletedTask,
                    Task.CompletedTask,
                    CancellationToken.None)));
        Assert.IsFalse(navigated);
    }

    [TestMethod]
    public void ApplicationHandlersRequireConcreteGlobalAndFileSettings()
    {
        Type type = typeof(ApplicationBridgeHandlers);
        ConstructorInfo constructor = type.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic).Single();
        ParameterInfo[] parameters = constructor.GetParameters();
        // FR-035 added the optional trailing stitch handler; FR-062 the workspace location handler; FR-132 the export handler;
        // FR-137 (6) the standing-picture handler; NFR-BR-005 the AI disclosure handler; FR-144 the proxy preview handler.
        Assert.AreEqual(21, parameters.Length);
        CollectionAssert.AreEqual(
            new[] { typeof(SettingsBridgeRequestHandler), typeof(GlobalSettingsBridgeRequestHandler), typeof(SettingsFileBridgeRequestHandler) },
            parameters.Skip(9).Take(3).Select(static parameter => parameter.ParameterType).ToArray());
        Assert.IsTrue(parameters.Take(12).All(static parameter => !parameter.HasDefaultValue));
        CollectionAssert.AreEqual(new[] { "sourceOnlyRuntime", "sourceOnly", "sourceOnlyMedia", "stitch", "workspace", "export", "portraits", "disclosure", "proxy" },
            parameters.Skip(12).Select(static parameter => parameter.Name).ToArray());
        Assert.IsTrue(parameters.Skip(12).All(static parameter => parameter.HasDefaultValue && parameter.DefaultValue is null));
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.AreEqual(23, properties.Length);
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
        Assert.AreEqual(typeof(GlobalSettingsBridgeRequestHandler),
            properties.Single(static property => property.Name == "GlobalSettings").PropertyType);
        Assert.AreEqual(typeof(SettingsFileBridgeRequestHandler),
            properties.Single(static property => property.Name == "FileSettings").PropertyType);

        // Older host overloads remain available; each settings-aware overload forwards both additions.
        ConstructorInfo[] hosts = typeof(WebViewHost).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsTrue(hosts.Any(static candidate => candidate.GetParameters().Length == 2));
        ParameterInfo[][] settingsHosts = hosts.Select(static candidate => candidate.GetParameters())
            .Where(static candidate => candidate.Any(static parameter => parameter.Name == "settingsHandler")).ToArray();
        Assert.AreEqual(2, settingsHosts.Length);
        foreach (ParameterInfo[] hostParameters in settingsHosts)
        {
            CollectionAssert.AreEqual(
                new[] { typeof(GlobalSettingsBridgeRequestHandler), typeof(SettingsFileBridgeRequestHandler) },
                hostParameters.Where(static parameter => parameter.Name is "globalSettingsHandler" or "fileSettingsHandler")
                    .Select(static parameter => parameter.ParameterType).ToArray());
        }
    }

    [TestMethod]
    public void SourceOnlyHostAdditionsAreOptionalTrailingParametersWithExplicitNativeConstructors()
    {
        ParameterInfo[][] hosts = typeof(WebViewHost).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(c => c.GetParameters()).Where(p => p.Any(item => item.Name == "sourceOnlyHandler")).ToArray();
        Assert.AreEqual(2, hosts.Length);
        foreach (ParameterInfo[] parameters in hosts)
        {
            CollectionAssert.AreEqual(new[] { "sourceOnlyHandler", "sourceOnlyMedia" }, parameters.TakeLast(2).Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(new[] { typeof(SourceOnlyBridgeHandler), typeof(SourceOnlyMediaResponseFactory) }, parameters.TakeLast(2).Select(p => p.ParameterType).ToArray());
            Assert.IsTrue(parameters.TakeLast(2).All(p => p.HasDefaultValue && p.DefaultValue is null));
        }
        ParameterInfo[] handler = typeof(SourceOnlyBridgeHandler).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().GetParameters();
        CollectionAssert.AreEqual(new[] { "pipelineProvider", "executionStore", "settingsStore", "resultService", "projectStore", "currentActorSid", "authorization", "timeProvider" }, handler.Select(p => p.Name).ToArray());
        Assert.AreEqual(typeof(Func<SourceOnlyPipeline>), handler[0].ParameterType);
        Assert.AreEqual(typeof(AuthorizationService), handler[6].ParameterType);
        ParameterInfo[] media = typeof(SourceOnlyMediaResponseFactory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().GetParameters();
        CollectionAssert.AreEqual(new[] { "executionStore", "resultService", "artifactStore", "currentActorSid", "authorization" }, media.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task MissingSourceOnlyHandlerUsesClosedResultEnvelopeForEveryMethod()
    {
        foreach (string method in new[] { "pipeline.read", "pipeline.regenerate", "pipeline.retry", "pipeline.cancel" })
        {
            BridgeRequest request = BridgeRequest.Create(Guid.NewGuid(), BridgeProtocol.ParseMethodName(method), JsonSerializer.SerializeToElement(new { }));
            _ = SourceOnlyBridgeTests.Response(request, await WebViewHost.HandleSourceOnlyRequestAsync(request, null, CancellationToken.None), "unavailable");
        }
        BridgeRequest other = new(Guid.NewGuid(), "bridge.ping");
        Assert.ThrowsExactly<ArgumentException>(() => WebViewHost.HandleSourceOnlyRequestAsync(other, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task SourceOnlyMediaPrefixIsInterceptedBeforeLegacyFactoryEvenWhenUnconfiguredOrMalformed()
    {
        var authorization = new CountingMediaAuthorization();
        var legacy = new PlayerMediaResponseFactory(UnavailablePlayerMediaAssetReader.Instance, authorization);
        string value = SourceOnlyMediaAssetReference.Prefix +
            "11111111-1111-4111-8111-111111111111/22222222-2222-4222-8222-222222222222/33333333-3333-4333-8333-333333333333/" + new string('a', 64);
        Assert.IsTrue(WebViewHost.IsAllowedMediaAssetUri(value));
        Assert.IsFalse(WebViewHost.IsAllowedTopLevelUri(value));
        foreach (string uri in new[]
        {
            value, value + "?path=private", SourceOnlyMediaAssetReference.Prefix + "malformed",
            SourceOnlyMediaAssetReference.Prefix + "../media/v1/legacy",
        })
        {
            using PlayerMediaResponse response = await WebViewHost.CreateMediaResponseAsync("GET", uri, null, legacy, null, CancellationToken.None);
            Assert.AreEqual(503, response.StatusCode);
            Assert.IsNull(response.Content);
        }
        Assert.AreEqual(0, authorization.Calls);
        using PlayerMediaResponse old = await WebViewHost.CreateMediaResponseAsync("GET",
            "https://player-media.appassets.example/media/v1/11111111-1111-4111-8111-111111111111/proxy/33333333-3333-4333-8333-333333333333/" + new string('a', 64),
            null, legacy, null, CancellationToken.None);
        Assert.AreEqual(404, old.StatusCode);
        Assert.AreEqual(1, authorization.Calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => WebViewHost.CreateMediaResponseAsync("GET", value, null, legacy, null, cancellation.Token));
    }

    private sealed class CountingMediaAuthorization : IPlayerAccessAuthorization
    {
        internal int Calls { get; private set; }
        public void DemandPreview() => Calls++;
    }

    [TestMethod]
    public void StartupSettingsConstructionDoesNotReopenStoreOrRecreateMissingRoot()
    {
        using SettingsFixture fixture = new();
        var authorization = new SettingsBridgeAuthorization(
            new AuthorizationService(new RoleAssignmentStore(fixture.DatabasePath)),
            () => throw new AssertFailedException("Startup must not authorize a settings request."));
        Directory.Move(fixture.ApplicationRoot, Path.Combine(fixture.RootPath, "offline-application"));

        var handlers = ProjectApplicationRuntime.CreateSettingsHandlers(
            fixture.Store, authorization, fixture.ApplicationRoot, fixture.ActorSid);

        Assert.IsNotNull(handlers.Settings);
        Assert.IsNotNull(handlers.GlobalSettings);
        Assert.IsNotNull(handlers.FileSettings);
        Assert.IsFalse(Directory.Exists(fixture.ApplicationRoot));
        Assert.IsFalse(File.Exists(fixture.DatabasePath));
    }

    [TestMethod]
    public void SettingsRoutingRecognizesAllMethodsAndPreservesAuthorizationFailures()
    {
        using SettingsFixture fixture = new();
        JsonElement saved = fixture.ReadSnapshot();
        JsonElement project = fixture.ReadSnapshot(projectScope: true);
        fixture.AuthorizationSid = "S-1-5-21-1111111111-2222222222-3333333333-6102";
        foreach (string method in new[]
        {
            "settings.read", "settings.update", "settings.reset", "settings.resetAll",
            "settings.global.read", "settings.global.update", "settings.global.reset", "settings.global.resetAll",
            "settings.file.export", "settings.file.preview", "settings.file.apply",
        })
        {
            bool global = method.StartsWith("settings.global.", StringComparison.Ordinal);
            bool file = method.StartsWith("settings.file.", StringComparison.Ordinal);
            BridgeRequest request = ParseSettingsRequest(method, RequestParameters(method, global || file ? saved : project));

            JsonElement result = AssertSettingsResponse(request, fixture.Dispatch(request), "forbidden");

            Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
        }

        Assert.IsFalse(File.Exists(fixture.SettingsPath));
        fixture.AuthorizationSid = fixture.ActorSid;
        Assert.AreEqual(saved.GetRawText(), fixture.ReadSnapshot().GetRawText());
        Assert.AreEqual(project.GetRawText(), fixture.ReadSnapshot(projectScope: true).GetRawText());
    }

    [TestMethod]
    public void SettingsRoutingMutatesTheSharedStoreWithoutChangingTheOtherScopeOrCreatingAFile()
    {
        using SettingsFixture fixture = new();
        JsonElement globalBefore = fixture.ReadSnapshot();
        JsonElement projectBefore = fixture.ReadSnapshot(projectScope: true);
        string key = "subtitles.enabled";
        bool initial = Selection(globalBefore, key).GetProperty("booleanValue").GetBoolean();
        var update = RequestParameters("settings.global.update", globalBefore);
        update["value"] = new { kind = "boolean", booleanValue = !initial };
        BridgeRequest request = ParseSettingsRequest("settings.global.update", update);

        JsonElement globalUpdated = AssertSettingsResponse(request, fixture.Dispatch(request), "available").GetProperty("snapshot");

        Assert.AreEqual(globalBefore.GetProperty("revision").GetInt32() + 1, globalUpdated.GetProperty("revision").GetInt32());
        Assert.AreEqual(!initial, Selection(globalUpdated, key).GetProperty("booleanValue").GetBoolean());
        Assert.AreEqual(globalUpdated.GetProperty("snapshotDigest").GetString(), fixture.Store.GetGlobalSettings()!.Snapshot.ContentDigest.Hex);
        Assert.AreEqual(projectBefore.GetRawText(), fixture.ReadSnapshot(projectScope: true).GetRawText());

        var projectUpdate = RequestParameters("settings.update", projectBefore);
        projectUpdate["value"] = new { kind = "boolean", booleanValue = !initial };
        request = ParseSettingsRequest("settings.update", projectUpdate);
        JsonElement projectUpdated = AssertSettingsResponse(request, fixture.Dispatch(request), "available").GetProperty("snapshot");
        Assert.AreEqual(projectBefore.GetProperty("revision").GetInt32() + 1, projectUpdated.GetProperty("revision").GetInt32());
        Assert.AreEqual(globalUpdated.GetRawText(), fixture.ReadSnapshot().GetRawText());
        Assert.AreEqual(projectBefore.GetProperty("initialSnapshotDigest").GetString(),
            projectUpdated.GetProperty("initialSnapshotDigest").GetString());

        foreach (string method in new[] { "settings.global.reset", "settings.global.resetAll" })
        {
            request = ParseSettingsRequest(method, RequestParameters(method, fixture.ReadSnapshot()));
            JsonElement reset = AssertSettingsResponse(request, fixture.Dispatch(request), "available").GetProperty("snapshot");
            Assert.AreEqual(initial, Selection(reset, key).GetProperty("booleanValue").GetBoolean());
        }

        Assert.IsFalse(File.Exists(fixture.SettingsPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("invalid")]
    [DataRow("directory")]
    public async Task StartupSettingsDoNotReadOrCreateFileAndDoNotBlockReady(string state)
    {
        using SettingsFixture fixture = new();
        byte[] marker = [0xff, 0x00, 0xfe];
        if (state == "invalid")
        {
            File.WriteAllBytes(fixture.SettingsPath, marker);
        }
        else if (state == "directory")
        {
            Directory.CreateDirectory(fixture.SettingsPath);
        }

        byte[] database = File.ReadAllBytes(fixture.DatabasePath);
        var handlers = ProjectApplicationRuntime.CreateSettingsHandlers(
            fixture.Store, fixture.Authorization, fixture.ApplicationRoot, fixture.ActorSid);
        TaskCompletionSource posted = NewCompletion();
        BridgeRequest ping = ParseSettingsRequest("bridge.ping", new { });

        await WebViewHost.StartDocumentAsync(
            () => Bridge.PostPingResponse(ping, _ => { }, posted),
            () => { }, Task.CompletedTask, posted.Task, CancellationToken.None);
        BridgeRequest read = ParseSettingsRequest("settings.global.read", new { });
        JsonElement saved = AssertSettingsResponse(read, WebViewHost.HandleSettingsRequest(
            read, handlers.Settings, handlers.GlobalSettings, handlers.FileSettings, CancellationToken.None), "available")
            .GetProperty("snapshot");

        CollectionAssert.AreEqual(database, File.ReadAllBytes(fixture.DatabasePath));
        Assert.AreEqual(state == "invalid", File.Exists(fixture.SettingsPath));
        Assert.AreEqual(state == "directory", Directory.Exists(fixture.SettingsPath));
        if (state == "invalid")
        {
            CollectionAssert.AreEqual(marker, File.ReadAllBytes(fixture.SettingsPath));
        }

        if (state == "missing")
        {
            foreach (string method in new[] { "settings.file.preview", "settings.file.apply" })
            {
                BridgeRequest request = ParseSettingsRequest(method, RequestParameters(method, saved));
                _ = AssertSettingsResponse(request, WebViewHost.HandleSettingsRequest(
                    request, handlers.Settings, handlers.GlobalSettings, handlers.FileSettings, CancellationToken.None), "fileNotFound");
            }

            Assert.IsFalse(File.Exists(fixture.SettingsPath));
            CollectionAssert.AreEqual(database, File.ReadAllBytes(fixture.DatabasePath));
        }
    }

    [TestMethod]
    public void UnsafeFileRootFailsOnlyFileRequestsAndCanRecoverWithoutNewRuntime()
    {
        using SettingsFixture fixture = new();
        JsonElement saved = fixture.ReadSnapshot();
        JsonElement project = fixture.ReadSnapshot(projectScope: true);
        string unsafeRoot = Path.Combine(fixture.RootPath, "file-instead-of-directory");
        byte[] marker = [1, 2, 3];
        File.WriteAllBytes(unsafeRoot, marker);
        var handlers = ProjectApplicationRuntime.CreateSettingsHandlers(
            fixture.Store, fixture.Authorization, unsafeRoot, fixture.ActorSid);

        foreach (string method in new[] { "settings.file.export", "settings.file.preview", "settings.file.apply" })
        {
            BridgeRequest request = ParseSettingsRequest(method, RequestParameters(method, saved));
            _ = AssertSettingsResponse(request, WebViewHost.HandleSettingsRequest(
                request, handlers.Settings, handlers.GlobalSettings, handlers.FileSettings, CancellationToken.None), "unavailable");
        }

        CollectionAssert.AreEqual(marker, File.ReadAllBytes(unsafeRoot));
        foreach (bool projectScope in new[] { false, true })
        {
            string method = projectScope ? "settings.read" : "settings.global.read";
            JsonElement expected = projectScope ? project : saved;
            BridgeRequest read = ParseSettingsRequest(method, RequestParameters(method, expected));
            Assert.AreEqual(expected.GetRawText(), AssertSettingsResponse(read, WebViewHost.HandleSettingsRequest(
                read, handlers.Settings, handlers.GlobalSettings, handlers.FileSettings, CancellationToken.None), "available")
                .GetProperty("snapshot").GetRawText());
        }

        File.Delete(unsafeRoot);
        Directory.CreateDirectory(unsafeRoot);
        BridgeRequest preview = ParseSettingsRequest("settings.file.preview", RequestParameters("settings.file.preview", saved));
        _ = AssertSettingsResponse(preview, WebViewHost.HandleSettingsRequest(
            preview, handlers.Settings, handlers.GlobalSettings, handlers.FileSettings, CancellationToken.None), "fileNotFound");
        Assert.IsEmpty(Directory.GetFileSystemEntries(unsafeRoot));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsFileRequestsUseOnlyNativeApplicationRootAndSavedTarget(bool projectScope)
    {
        using SettingsFixture fixture = new();
        JsonElement saved = fixture.ReadSnapshot(projectScope);
        BridgeRequest export = ParseSettingsRequest("settings.file.export", RequestParameters("settings.file.export", saved));
        JsonElement exported = AssertSettingsResponse(export, fixture.Dispatch(export), "available");

        Assert.AreEqual(saved.GetRawText(), exported.GetProperty("snapshot").GetRawText());
        Assert.IsTrue(File.Exists(fixture.SettingsPath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.ApplicationRoot, "Workspace", SettingsTextFile.FileName)));
        string fileHash = ContentDigest.Compute(File.ReadAllBytes(fixture.SettingsPath)).Hex;
        Assert.AreEqual(fileHash, exported.GetProperty("fileSha256").GetString());
        foreach (string method in new[] { "settings.file.preview", "settings.file.apply" })
        {
            var parameters = RequestParameters(method, saved);
            if (method == "settings.file.apply")
            {
                parameters["expectedFileSha256"] = fileHash;
            }

            BridgeRequest request = ParseSettingsRequest(method, parameters);
            JsonElement result = AssertSettingsResponse(request, fixture.Dispatch(request), "available");
            Assert.AreEqual(saved.GetRawText(), result.GetProperty("snapshot").GetRawText());
            Assert.AreEqual(fileHash, result.GetProperty("fileSha256").GetString());
        }

        Assert.AreEqual(saved.GetRawText(), fixture.ReadSnapshot(projectScope).GetRawText());
    }

    [TestMethod]
    public void DeferredFileFactoryRunsOnlyAfterAuthorizationIdentityAndCancellationChecks()
    {
        using SettingsFixture fixture = new();
        JsonElement saved = fixture.ReadSnapshot();
        int calls = 0;
        var fileHandler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.Authorization, () =>
        {
            calls++;
            throw new IOException("ux011-private-file-detail");
        });
        Assert.AreEqual(0, calls);
        BridgeRequest preview = ParseSettingsRequest("settings.file.preview", RequestParameters("settings.file.preview", saved));
        fixture.AuthorizationSid = "S-1-5-21-1111111111-2222222222-3333333333-6102";
        _ = AssertSettingsResponse(preview, WebViewHost.HandleSettingsRequest(preview,
            fixture.Settings, fixture.GlobalSettings, fileHandler, CancellationToken.None), "forbidden");
        Assert.AreEqual(0, calls);
        fixture.AuthorizationSid = fixture.ActorSid;
        var stale = RequestParameters("settings.file.preview", saved);
        stale["expectedRevision"] = saved.GetProperty("revision").GetInt32() + 1;
        BridgeRequest conflict = ParseSettingsRequest("settings.file.preview", stale);
        _ = AssertSettingsResponse(conflict, WebViewHost.HandleSettingsRequest(conflict,
            fixture.Settings, fixture.GlobalSettings, fileHandler, CancellationToken.None), "conflict");
        Assert.AreEqual(0, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => WebViewHost.HandleSettingsRequest(preview,
            fixture.Settings, fixture.GlobalSettings, fileHandler, cancellation.Token));
        Assert.AreEqual(0, calls);

        _ = AssertSettingsResponse(preview, WebViewHost.HandleSettingsRequest(preview,
            fixture.Settings, fixture.GlobalSettings, fileHandler, CancellationToken.None), "unavailable");
        Assert.AreEqual(1, calls);
        Assert.IsFalse(File.Exists(fixture.SettingsPath));
    }

    [TestMethod]
    public void UnconfiguredSettingsHandlersFailClosedWithoutFallingBackToLegacy()
    {
        using SettingsFixture fixture = new();
        JsonElement saved = fixture.ReadSnapshot();
        JsonElement project = fixture.ReadSnapshot(projectScope: true);
        foreach (string method in new[]
        {
            "settings.read", "settings.update", "settings.reset", "settings.resetAll",
            "settings.global.read", "settings.global.update", "settings.global.reset", "settings.global.resetAll",
            "settings.file.export", "settings.file.preview", "settings.file.apply",
        })
        {
            bool legacy = !method.StartsWith("settings.global.", StringComparison.Ordinal) &&
                !method.StartsWith("settings.file.", StringComparison.Ordinal);
            BridgeRequest request = ParseSettingsRequest(method, RequestParameters(method, legacy ? project : saved));
            _ = AssertSettingsResponse(request, WebViewHost.HandleSettingsRequest(request,
                legacy ? null : fixture.Settings, globalSettingsHandler: null, fileSettingsHandler: null,
                CancellationToken.None), "unavailable");
        }

        foreach (string method in new[] { "bridge.ping", "project.list" })
        {
            BridgeRequest request = ParseSettingsRequest(method, new { });
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Dispatch(request));
        }

        foreach (string method in new[] { "settings.global.unknown", "settings.file.unknown" })
        {
            string json = JsonSerializer.Serialize(new { version = 1, requestId = Guid.NewGuid().ToString("D"), method, @params = new { } });
            Assert.IsFalse(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out _, out BridgeRequestRejection rejection));
            Assert.AreEqual(BridgeRequestRejection.MethodNotAllowed, rejection);
        }
    }

    private static Dictionary<string, object?> RequestParameters(string method, JsonElement saved)
    {
        if (method == "settings.global.read")
        {
            return [];
        }

        if (method == "settings.read")
        {
            return new() { ["projectId"] = saved.GetProperty("projectId").GetString() };
        }

        var parameters = new Dictionary<string, object?>
        {
            ["expectedRevision"] = saved.GetProperty("revision").GetInt32(),
            ["expectedProfileId"] = saved.GetProperty("profileId").GetString(),
            ["expectedProfileVersion"] = saved.GetProperty("profileVersion").GetString(),
            ["expectedProfileDigest"] = saved.GetProperty("profileDigest").GetString(),
            ["expectedSnapshotDigest"] = saved.GetProperty("snapshotDigest").GetString(),
        };
        if (!method.StartsWith("settings.global.", StringComparison.Ordinal))
        {
            parameters["projectId"] = saved.GetProperty("projectId").GetString();
        }

        if (method.EndsWith(".update", StringComparison.Ordinal) || method.EndsWith(".reset", StringComparison.Ordinal))
        {
            parameters["key"] = "subtitles.enabled";
        }

        if (method.EndsWith(".update", StringComparison.Ordinal))
        {
            parameters["value"] = new { kind = "boolean", booleanValue = true };
        }

        if (method == "settings.file.export")
        {
            parameters["expectedFileSha256"] = null;
        }
        else if (method == "settings.file.apply")
        {
            parameters["expectedFileSha256"] = new string('a', 64);
        }

        return parameters;
    }

    private static BridgeRequest ParseSettingsRequest(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            requestId = Guid.NewGuid().ToString("D"),
            method,
            @params = parameters,
        });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json,
            out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
        Assert.IsNotNull(request);
        return request;
    }

    private static JsonElement AssertSettingsResponse(BridgeRequest request, string response, string status)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(request.RequestId.ToString("D"), root.GetProperty("requestId").GetString());
        string method = BridgeProtocol.GetMethodName(request.Method);
        Assert.AreEqual(method, root.GetProperty("method").GetString());
        JsonElement result = root.GetProperty("result");
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        bool file = method.StartsWith("settings.file.", StringComparison.Ordinal);
        CollectionAssert.AreEqual(file ? new[] { "status", "snapshot", "fileSha256" } : new[] { "status", "snapshot" },
            result.EnumerateObject().Select(static property => property.Name).ToArray());
        if (status != "available")
        {
            Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
            if (file)
            {
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("fileSha256").ValueKind);
            }
        }

        Assert.IsFalse(response.Contains("ux011-private-file-detail", StringComparison.Ordinal));
        Assert.IsFalse(response.Contains("setting.txt", StringComparison.Ordinal));
        return result.Clone();
    }

    private static JsonElement Selection(JsonElement snapshot, string key) => snapshot.GetProperty("values")
        .EnumerateArray().Single(item => item.GetProperty("key").GetString() == key).GetProperty("value");

    private sealed class SettingsFixture : IDisposable
    {
        internal SettingsFixture()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            ActorSid = identity.User?.Value ?? throw new AssertInconclusiveException("A Windows user SID is required.");
            RootPath = Path.Combine(Path.GetTempPath(), nameof(WebViewHostTests), Guid.NewGuid().ToString("N"));
            ApplicationRoot = Path.Combine(RootPath, "application");
            string workspace = Path.Combine(ApplicationRoot, "Workspace");
            Directory.CreateDirectory(workspace);
            DatabasePath = Path.Combine(workspace, "hubradio.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(workspace, "backups"));
            Store = new DefaultSettingsStore(DatabasePath);
            var profile = Store.RegisterProfile(File.ReadAllBytes(
                ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.1.0.json")));
            DateTimeOffset now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
            _ = Store.InitializeGlobalSettings(profile.CreateSnapshot(), now);
            ProjectId = EntityId<ProjectIdTag>.New();
            var project = new Project(ProjectId, "UX011 isolated wiring", "Settings", "Internal",
                PublicationScope.InternalOnly, null, [], []);
            new ProjectStore(DatabasePath).CreateProject(project, "{\"project\":true}", now);
            _ = Store.BindProjectSettings(ProjectId, profile.CreateSnapshot(), now);
            var service = new AuthorizationService(new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(service.BootstrapInitialRoles(ActorSid));
            AuthorizationSid = ActorSid;
            Authorization = new SettingsBridgeAuthorization(service, () => AuthorizationSid);
            var handlers = ProjectApplicationRuntime.CreateSettingsHandlers(Store, Authorization, ApplicationRoot, ActorSid);
            Settings = handlers.Settings;
            GlobalSettings = handlers.GlobalSettings;
            FileSettings = handlers.FileSettings;
        }

        internal string RootPath { get; }
        internal string ApplicationRoot { get; }
        internal string DatabasePath { get; }
        internal string SettingsPath => Path.Combine(ApplicationRoot, SettingsTextFile.FileName);
        internal string ActorSid { get; }
        internal string AuthorizationSid { get; set; }
        internal EntityId<ProjectIdTag> ProjectId { get; }
        internal DefaultSettingsStore Store { get; }
        internal SettingsBridgeAuthorization Authorization { get; }
        internal SettingsBridgeRequestHandler Settings { get; }
        internal GlobalSettingsBridgeRequestHandler GlobalSettings { get; }
        internal SettingsFileBridgeRequestHandler FileSettings { get; }

        internal string Dispatch(BridgeRequest request) => WebViewHost.HandleSettingsRequest(
            request, Settings, GlobalSettings, FileSettings, CancellationToken.None);

        internal JsonElement ReadSnapshot(bool projectScope = false)
        {
            string method = projectScope ? "settings.read" : "settings.global.read";
            object parameters = projectScope ? new { projectId = ProjectId.ToString() } : new { };
            BridgeRequest request = ParseSettingsRequest(method, parameters);
            return AssertSettingsResponse(request, Dispatch(request), "available").GetProperty("snapshot");
        }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}
