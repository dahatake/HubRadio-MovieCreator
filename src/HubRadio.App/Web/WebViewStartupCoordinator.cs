using HubRadio.Infrastructure.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace HubRadio.App.Web;

internal static class WebViewStartupCoordinator
{
    internal static Task<WebViewStartupResult> StartAsync(
        Func<Task> initializeWebView) =>
        StartAsync(
            () => CoreWebView2Environment.GetAvailableBrowserVersionString(),
            initializeWebView);

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