using HubRadio.App.Web;

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
