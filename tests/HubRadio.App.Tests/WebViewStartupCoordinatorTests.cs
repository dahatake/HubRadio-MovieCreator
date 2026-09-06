using HubRadio.App.Web;
using HubRadio.Infrastructure.Diagnostics;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class WebViewStartupCoordinatorTests
{
    [TestMethod]
    public async Task StartAsync_AvailableRuntime_InitializesOnceAndReturnsReady()
    {
        int detectionCount = 0;
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () =>
            {
                detectionCount++;
                return "123.0.4567.8";
            },
            () =>
            {
                initializationCount++;
                return Task.CompletedTask;
            });

        Assert.AreEqual(WebViewStartupStatus.Ready, result.Status);
        Assert.AreEqual("123.0.4567.8", result.RuntimeVersion);
        Assert.IsNull(result.DiagnosticCode);
        Assert.AreEqual(1, detectionCount);
        Assert.AreEqual(1, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_UnavailableRuntime_DoesNotAttemptInitialization()
    {
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => throw new InvalidOperationException("secret C:\\media\\episode.mov"),
            () =>
            {
                initializationCount++;
                return Task.CompletedTask;
            });

        AssertFailure(
            result,
            WebViewStartupStatus.RuntimeUnavailable,
            StableErrorCodes.WebViewRuntimeUnavailable);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_EmptyVersion_IsDetectionFailureAndSkipsInitialization()
    {
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => " \t ",
            () =>
            {
                initializationCount++;
                return Task.CompletedTask;
            });

        AssertFailure(
            result,
            WebViewStartupStatus.RuntimeUnavailable,
            StableErrorCodes.WebViewRuntimeUnavailable);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_InitializationThrows_ReportsFailureWithoutExceptionDetail()
    {
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => "123.0.4567.8",
            () =>
            {
                initializationCount++;
                return Task.FromException(new UnauthorizedAccessException("C:\\private\\profile"));
            });

        AssertFailure(
            result,
            WebViewStartupStatus.InitializationFailed,
            StableErrorCodes.WebViewInitializationFailed);
        Assert.AreEqual(1, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_FixedRuntime_ForwardsExactFolderAndInitializesOnce()
    {
        using RuntimeFolderFixture fixture = new();
        string browserExecutableFolder = Path.Combine(fixture.Folder, ".");
        string? detectedFolder = null;
        int detectionCount = 0;
        int initializationCount = 0;
        int detectionCountAtInitialization = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            browserExecutableFolder,
            folder =>
            {
                detectedFolder = folder;
                detectionCount++;
                return "123.0.4567.8";
            },
            () =>
            {
                detectionCountAtInitialization = detectionCount;
                initializationCount++;
                return Task.CompletedTask;
            });

        Assert.AreEqual(browserExecutableFolder, detectedFolder);
        Assert.AreEqual(WebViewStartupStatus.Ready, result.Status);
        Assert.AreEqual("123.0.4567.8", result.RuntimeVersion);
        Assert.IsNull(result.DiagnosticCode);
        Assert.AreEqual(1, detectionCount);
        Assert.AreEqual(1, initializationCount);
        Assert.AreEqual(1, detectionCountAtInitialization);
    }

    [TestMethod]
    public async Task StartAsync_MissingFixedRuntime_DoesNotAttemptDetectionOrInitialization()
    {
        using RuntimeFolderFixture fixture = new();
        string browserExecutableFolder = Path.Combine(fixture.Folder, "missing");
        int detectionCount = 0;
        int initializationCount = 0;
        Func<Task> initializeWebView = () =>
        {
            initializationCount++;
            return Task.CompletedTask;
        };

        WebViewStartupResult injectedResult = await WebViewStartupCoordinator.StartAsync(
            browserExecutableFolder,
            _ =>
            {
                detectionCount++;
                return "123.0.4567.8";
            },
            initializeWebView);
        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            browserExecutableFolder,
            initializeWebView);

        AssertFailure(
            injectedResult,
            WebViewStartupStatus.RuntimeUnavailable,
            StableErrorCodes.WebViewRuntimeUnavailable);
        AssertFailure(
            result,
            WebViewStartupStatus.RuntimeUnavailable,
            StableErrorCodes.WebViewRuntimeUnavailable);
        Assert.AreEqual(0, detectionCount);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_FixedRuntimeUnavailable_DoesNotAttemptInitialization()
    {
        using RuntimeFolderFixture fixture = new();
        int detectionCount = 0;
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            fixture.Folder,
            folder =>
            {
                detectionCount++;
                throw new InvalidOperationException($"secret {folder}");
            },
            () =>
            {
                initializationCount++;
                return Task.CompletedTask;
            });

        AssertFailure(
            result,
            WebViewStartupStatus.RuntimeUnavailable,
            StableErrorCodes.WebViewRuntimeUnavailable);
        Assert.AreEqual(1, detectionCount);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t ")]
    public async Task StartAsync_FixedRuntimeEmptyVersion_IsDetectionFailureAndSkipsInitialization(
        string? runtimeVersion)
    {
        using RuntimeFolderFixture fixture = new();
        int detectionCount = 0;
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            fixture.Folder,
            _ =>
            {
                detectionCount++;
                return runtimeVersion!;
            },
            () =>
            {
                initializationCount++;
                return Task.CompletedTask;
            });

        AssertFailure(
            result,
            WebViewStartupStatus.RuntimeUnavailable,
            StableErrorCodes.WebViewRuntimeUnavailable);
        Assert.AreEqual(1, detectionCount);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartAsync_FixedRuntimeInitializationThrows_ReportsFailureWithoutExceptionDetail(
        bool throwSynchronously)
    {
        using RuntimeFolderFixture fixture = new();
        int initializationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            fixture.Folder,
            _ => "123.0.4567.8",
            () =>
            {
                initializationCount++;
                var exception = new UnauthorizedAccessException($"secret {fixture.Folder}");
                if (throwSynchronously)
                {
                    throw exception;
                }

                return Task.FromException(exception);
            });

        AssertFailure(
            result,
            WebViewStartupStatus.InitializationFailed,
            StableErrorCodes.WebViewInitializationFailed);
        Assert.AreEqual(1, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_NullFixedRuntimeFolder_IsRejected()
    {
        int detectionCount = 0;
        int initializationCount = 0;
        Func<string, string> getAvailableRuntimeVersion = _ =>
        {
            detectionCount++;
            return "123.0.4567.8";
        };
        Func<Task> initializeWebView = () =>
        {
            initializationCount++;
            return Task.CompletedTask;
        };

        ArgumentNullException exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                (string)null!,
                initializeWebView));
        ArgumentNullException injectedException = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                null!,
                getAvailableRuntimeVersion,
                initializeWebView));

        Assert.AreEqual("browserExecutableFolder", exception.ParamName);
        Assert.AreEqual("browserExecutableFolder", injectedException.ParamName);
        Assert.AreEqual(0, detectionCount);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \t ")]
    [DataRow("WebView2Runtime")]
    [DataRow(@".\WebView2Runtime")]
    [DataRow(@"C:WebView2Runtime")]
    [DataRow(@"\WebView2Runtime")]
    public async Task StartAsync_InvalidFixedRuntimeFolder_IsRejected(string browserExecutableFolder)
    {
        int detectionCount = 0;
        int initializationCount = 0;
        Func<string, string> getAvailableRuntimeVersion = _ =>
        {
            detectionCount++;
            return "123.0.4567.8";
        };
        Func<Task> initializeWebView = () =>
        {
            initializationCount++;
            return Task.CompletedTask;
        };

        ArgumentException exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => WebViewStartupCoordinator.StartAsync(
                browserExecutableFolder,
                initializeWebView));
        ArgumentException injectedException = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => WebViewStartupCoordinator.StartAsync(
                browserExecutableFolder,
                getAvailableRuntimeVersion,
                initializeWebView));

        Assert.AreEqual("browserExecutableFolder", exception.ParamName);
        Assert.AreEqual("browserExecutableFolder", injectedException.ParamName);
        Assert.AreEqual(0, detectionCount);
        Assert.AreEqual(0, initializationCount);
    }

    [TestMethod]
    public async Task StartAsync_NullDelegates_AreRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                (Func<string>)null!,
                () => Task.CompletedTask));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                () => "123.0.4567.8",
                null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                AppContext.BaseDirectory,
                null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                AppContext.BaseDirectory,
                null!,
                () => Task.CompletedTask));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                AppContext.BaseDirectory,
                _ => "123.0.4567.8",
                null!));
    }

    [TestMethod]
    public async Task StartAsync_CurrentMachineRuntime_IsDetectedWhenInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("WebView2 runtime detection requires Windows.");
            return;
        }

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => Task.CompletedTask);

        if (result.Status == WebViewStartupStatus.RuntimeUnavailable)
        {
            Assert.Inconclusive("The WebView2 Runtime is not installed in this test environment.");
            return;
        }

        Assert.AreEqual(WebViewStartupStatus.Ready, result.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.RuntimeVersion));
        Assert.IsNull(result.DiagnosticCode);
    }

    [TestMethod]
    [DataRow(@"C:\HubRadioData\WebView2")]
    [DataRow(@"C:\HubRadio\WebView2Runtime\msedgewebview2.exe")]
    public void VerifyPath_MatchingAbsolutePaths_AreAccepted(string path)
    {
        WebViewStartupCoordinator.VerifyPath(path, path);
    }

    [TestMethod]
    [DataRow(@"c:\hubradiodata\webview2", @"C:\HubRadioData\WebView2")]
    [DataRow(@"C:\HubRadioData\WebView2\", @"C:\HubRadioData\WebView2")]
    [DataRow("C:/HubRadioData/WebView2/", @"C:\HubRadioData\WebView2")]
    [DataRow(@"C:\HubRadioData\temporary\..\WebView2\.", @"C:\HubRadioData\WebView2")]
    [DataRow(@"c:\hubradio\webview2runtime\MSEDGEWEBVIEW2.EXE", @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe")]
    [DataRow(@"C:\HubRadio\old-runtime\..\WebView2Runtime\msedgewebview2.exe", @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe")]
    [DataRow(@"\\SERVER\Share\WebView2\", @"\\server\share\WebView2")]
    [DataRow(@"C:\", "c:/")]
    public void VerifyPath_NormalizedAbsolutePaths_AreAccepted(string actualPath, string expectedPath)
    {
        WebViewStartupCoordinator.VerifyPath(actualPath, expectedPath);
        WebViewStartupCoordinator.VerifyPath(expectedPath, actualPath);
    }

    [TestMethod]
    [DataRow(@"C:\OtherProfile\WebView2")]
    [DataRow(@"C:\HubRadioData\WebView2-copy")]
    [DataRow(@"C:\HubRadioData\WebView2\child")]
    [DataRow(@"D:\HubRadioData\WebView2")]
    public void VerifyPath_DifferentValidUserDataFolders_ThrowGenericFailure(string actualPath)
    {
        const string expectedPath = @"C:\HubRadioData\WebView2";
        Assert.IsTrue(Path.IsPathFullyQualified(actualPath));
        Assert.IsTrue(Path.IsPathFullyQualified(expectedPath));

        AssertPathRejected(actualPath, expectedPath);
        AssertPathRejected(expectedPath, actualPath);
    }

    [TestMethod]
    [DataRow(@"C:\OtherRuntime\msedgewebview2.exe")]
    [DataRow(@"C:\HubRadio\WebView2Runtime-copy\msedgewebview2.exe")]
    [DataRow(@"C:\HubRadio\WebView2Runtime\other.exe")]
    public void VerifyPath_DifferentValidBrowserExecutables_ThrowGenericFailure(string actualPath)
    {
        const string expectedPath = @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe";
        Assert.IsTrue(Path.IsPathFullyQualified(actualPath));
        Assert.IsTrue(Path.IsPathFullyQualified(expectedPath));

        AssertPathRejected(actualPath, expectedPath);
        AssertPathRejected(expectedPath, actualPath);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t ")]
    public void VerifyPath_MissingPath_ThrowsGenericFailure(string? missingPath)
    {
        string[] validPaths =
        [
            @"C:\HubRadioData\WebView2",
            @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe",
        ];
        foreach (string validPath in validPaths)
        {
            AssertPathRejected(missingPath, validPath);
            AssertPathRejected(validPath, missingPath);
        }

        AssertPathRejected(missingPath, missingPath);
    }

    [TestMethod]
    [DataRow("WebView2")]
    [DataRow(@"WebView2Runtime\msedgewebview2.exe")]
    [DataRow(@".\WebView2")]
    [DataRow(@"..\WebView2")]
    [DataRow(@"C:WebView2")]
    [DataRow(@"\WebView2")]
    [DataRow("/WebView2")]
    public void VerifyPath_RelativePath_ThrowsGenericFailure(string relativePath)
    {
        const string validPath = @"C:\HubRadioData\WebView2";
        AssertPathRejected(relativePath, validPath);
        AssertPathRejected(validPath, relativePath);
        // Equal relative inputs must not become valid merely by resolving both against the current directory.
        AssertPathRejected(relativePath, relativePath);
    }

    [TestMethod]
    [DataRow(@"C:\HubRadioData\WebView2")]
    [DataRow(@"C:\HubRadio\WebView2Runtime\msedgewebview2.exe")]
    public void VerifyPath_InvalidAbsolutePath_ThrowsGenericFailure(string validPath)
    {
        string invalidPath = validPath + '\0';
        Assert.IsTrue(Path.IsPathFullyQualified(invalidPath));

        AssertPathRejected(invalidPath, validPath);
        AssertPathRejected(validPath, invalidPath);
        AssertPathRejected(invalidPath, invalidPath);
    }

    [TestMethod]
    public async Task StartAsync_VerifiedPaths_ContinuesInitializationAndReturnsReady()
    {
        int controllerCreationCount = 0;
        int databaseCreationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => "123.0.4567.8",
            () =>
            {
                WebViewStartupCoordinator.VerifyPath(
                    @"c:\hubradiodata\WebView2\",
                    @"C:\HubRadioData\WebView2");
                controllerCreationCount++;
                WebViewStartupCoordinator.VerifyPath(
                    @"c:\hubradio\WebView2Runtime\MSEDGEWEBVIEW2.EXE",
                    @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe");
                databaseCreationCount++;
                return Task.CompletedTask;
            });

        Assert.AreEqual(WebViewStartupStatus.Ready, result.Status);
        Assert.AreEqual("123.0.4567.8", result.RuntimeVersion);
        Assert.IsNull(result.DiagnosticCode);
        Assert.AreEqual(1, controllerCreationCount);
        Assert.AreEqual(1, databaseCreationCount);
    }

    [TestMethod]
    public async Task StartAsync_MismatchedUserDataPath_SkipsControllerAndDatabase()
    {
        int controllerCreationCount = 0;
        int databaseCreationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => "123.0.4567.8",
            () =>
            {
                WebViewStartupCoordinator.VerifyPath(
                    @"C:\OtherProfile\WebView2",
                    @"C:\HubRadioData\WebView2");
                controllerCreationCount++;
                WebViewStartupCoordinator.VerifyPath(
                    @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe",
                    @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe");
                databaseCreationCount++;
                return Task.CompletedTask;
            });

        AssertFailure(
            result,
            WebViewStartupStatus.InitializationFailed,
            StableErrorCodes.WebViewInitializationFailed);
        Assert.AreEqual(0, controllerCreationCount);
        Assert.AreEqual(0, databaseCreationCount);
    }

    [TestMethod]
    public async Task StartAsync_MismatchedBrowserPath_SkipsDatabase()
    {
        int controllerCreationCount = 0;
        int databaseCreationCount = 0;

        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            () => "123.0.4567.8",
            () =>
            {
                WebViewStartupCoordinator.VerifyPath(
                    @"C:\HubRadioData\WebView2",
                    @"C:\HubRadioData\WebView2");
                controllerCreationCount++;
                WebViewStartupCoordinator.VerifyPath(
                    @"C:\OtherRuntime\msedgewebview2.exe",
                    @"C:\HubRadio\WebView2Runtime\msedgewebview2.exe");
                databaseCreationCount++;
                return Task.CompletedTask;
            });

        AssertFailure(
            result,
            WebViewStartupStatus.InitializationFailed,
            StableErrorCodes.WebViewInitializationFailed);
        Assert.AreEqual(1, controllerCreationCount);
        Assert.AreEqual(0, databaseCreationCount);
    }

    private static void AssertPathRejected(string? actualPath, string? expectedPath)
    {
        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => WebViewStartupCoordinator.VerifyPath(actualPath, expectedPath));

        Assert.AreEqual("WebView2 path could not be verified.", exception.Message);
        Assert.IsNull(exception.InnerException);
    }

    private static void AssertFailure(
        WebViewStartupResult result,
        WebViewStartupStatus expectedStatus,
        string expectedDiagnosticCode)
    {
        Assert.AreEqual(expectedStatus, result.Status);
        Assert.IsNull(result.RuntimeVersion);
        Assert.AreEqual(expectedDiagnosticCode, result.DiagnosticCode);
    }

    private sealed class RuntimeFolderFixture : IDisposable
    {
        internal string Folder { get; } =
            Directory.CreateTempSubdirectory("HubRadio Fixed WebView2 テスト ").FullName;

        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}