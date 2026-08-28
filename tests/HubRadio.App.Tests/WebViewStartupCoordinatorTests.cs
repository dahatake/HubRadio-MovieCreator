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
    public async Task StartAsync_NullDelegates_AreRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                null!,
                () => Task.CompletedTask));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => WebViewStartupCoordinator.StartAsync(
                () => "123.0.4567.8",
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

    private static void AssertFailure(
        WebViewStartupResult result,
        WebViewStartupStatus expectedStatus,
        string expectedDiagnosticCode)
    {
        Assert.AreEqual(expectedStatus, result.Status);
        Assert.IsNull(result.RuntimeVersion);
        Assert.AreEqual(expectedDiagnosticCode, result.DiagnosticCode);
    }
}