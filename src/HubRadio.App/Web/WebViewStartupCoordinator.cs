using HubRadio.Infrastructure.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace HubRadio.App.Web;

internal static class WebViewStartupCoordinator
{
    internal static void VerifyPath(string? actualPath, string? expectedPath)
    {
        try
        {
            // Check the original inputs first: relative paths must not be resolved against the process directory.
            if (!string.IsNullOrWhiteSpace(actualPath)
                && !string.IsNullOrWhiteSpace(expectedPath)
                && Path.IsPathFullyQualified(actualPath)
                && Path.IsPathFullyQualified(expectedPath)
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        catch (Exception)
        {
            // Invalid paths are unverified too; do not retain path-bearing normalization exceptions.
        }

        throw new InvalidOperationException("WebView2 path could not be verified.");
    }

    internal static Task<WebViewStartupResult> StartAsync(
        Func<Task> initializeWebView) =>
        StartAsync(
            () => CoreWebView2Environment.GetAvailableBrowserVersionString(),
            initializeWebView);

    // The initializer must use this same folder when creating its WebView2 environment.
    internal static Task<WebViewStartupResult> StartAsync(
        string browserExecutableFolder,
        Func<Task> initializeWebView) =>
        StartAsync(
            browserExecutableFolder,
            folder => CoreWebView2Environment.GetAvailableBrowserVersionString(folder),
            initializeWebView);

    internal static Task<WebViewStartupResult> StartAsync(
        string browserExecutableFolder,
        Func<string, string> getAvailableRuntimeVersion,
        Func<Task> initializeWebView)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserExecutableFolder);
        ArgumentNullException.ThrowIfNull(getAvailableRuntimeVersion);
        ArgumentNullException.ThrowIfNull(initializeWebView);

        if (!Path.IsPathFullyQualified(browserExecutableFolder))
        {
            throw new ArgumentException(
                "The WebView2 runtime folder must be an absolute path.",
                nameof(browserExecutableFolder));
        }

        if (!Directory.Exists(browserExecutableFolder))
        {
            return Task.FromResult(WebViewStartupResult.RuntimeUnavailable());
        }

        return StartAsync(
            () => getAvailableRuntimeVersion(browserExecutableFolder),
            initializeWebView);
    }

    internal static async Task<WebViewStartupResult> StartAsync(
        Func<string> getAvailableRuntimeVersion,
        Func<Task> initializeWebView)
    {
        ArgumentNullException.ThrowIfNull(getAvailableRuntimeVersion);
        ArgumentNullException.ThrowIfNull(initializeWebView);

        string runtimeVersion;
        try
        {
            runtimeVersion = getAvailableRuntimeVersion();
        }
        catch (Exception)
        {
            return WebViewStartupResult.RuntimeUnavailable();
        }

        if (string.IsNullOrWhiteSpace(runtimeVersion))
        {
            return WebViewStartupResult.RuntimeUnavailable();
        }

        try
        {
            await initializeWebView();
        }
        catch (Exception)
        {
            return WebViewStartupResult.InitializationFailed();
        }

        return WebViewStartupResult.Ready(runtimeVersion);
    }
}

internal enum WebViewStartupStatus
{
    Ready,
    RuntimeUnavailable,
    InitializationFailed,
}

internal sealed class WebViewStartupResult
{
    private WebViewStartupResult(
        WebViewStartupStatus status,
        string? runtimeVersion,
        string? diagnosticCode)
    {
        Status = status;
        RuntimeVersion = runtimeVersion;
        DiagnosticCode = diagnosticCode;
    }

    internal WebViewStartupStatus Status { get; }

    internal string? RuntimeVersion { get; }

    internal string? DiagnosticCode { get; }

    internal static WebViewStartupResult Ready(string runtimeVersion) =>
        new(WebViewStartupStatus.Ready, runtimeVersion, diagnosticCode: null);

    internal static WebViewStartupResult RuntimeUnavailable() =>
        new(
            WebViewStartupStatus.RuntimeUnavailable,
            runtimeVersion: null,
            StableErrorCodes.WebViewRuntimeUnavailable);

    internal static WebViewStartupResult InitializationFailed() =>
        new(
            WebViewStartupStatus.InitializationFailed,
            runtimeVersion: null,
            StableErrorCodes.WebViewInitializationFailed);
}