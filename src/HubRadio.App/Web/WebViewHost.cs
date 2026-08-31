using Microsoft.Web.WebView2.Core;

namespace HubRadio.App.Web;

internal sealed class WebViewHost : IDisposable
{
    internal const string VirtualHostName = "appassets.example";
    internal const string EntryPoint = "https://appassets.example/index.html";

    private readonly CoreWebView2 coreWebView;
    private readonly string webAssetsDirectory;
    private readonly Func<BridgeRequest, CancellationToken, Task<string>>?
        handleProjectRequestAsync;
    private readonly JobBridgeRequestHandler? jobHandler;
    private Bridge? bridge;
    private TaskCompletionSource? initialNavigation;
    private bool isStarted;
    private bool isDisposed;

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory)
        : this(
            coreWebView,
            webAssetsDirectory,
            handleProjectRequestAsync: null,
            jobHandler: null)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        Func<BridgeRequest, CancellationToken, Task<string>>
            handleProjectRequestAsync)
        : this(
            coreWebView,
            webAssetsDirectory,
            handleProjectRequestAsync,
            jobHandler: null)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        JobBridgeRequestHandler? jobHandler)
        : this(
            coreWebView,
            webAssetsDirectory,
            handleProjectRequestAsync: null,
            jobHandler)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        Func<BridgeRequest, CancellationToken, Task<string>>?
            handleProjectRequestAsync,
        JobBridgeRequestHandler? jobHandler)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        ArgumentException.ThrowIfNullOrWhiteSpace(webAssetsDirectory);

        string fullPath = Path.GetFullPath(webAssetsDirectory);
        if (!Path.IsPathFullyQualified(fullPath) ||
            !Directory.Exists(fullPath) ||
            !File.Exists(Path.Combine(fullPath, "index.html")))
        {
            throw new DirectoryNotFoundException(
                "The bundled web application is unavailable.");
        }

        this.coreWebView = coreWebView;
        this.webAssetsDirectory = fullPath;
        this.handleProjectRequestAsync = handleProjectRequestAsync;
        this.jobHandler = jobHandler;
    }

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (isStarted)
        {
            throw new InvalidOperationException("The WebView host is already started.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        isStarted = true;
        TaskCompletionSource navigation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        initialNavigation = navigation;

        coreWebView.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            webAssetsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        coreWebView.Settings.AreDevToolsEnabled = false;
        coreWebView.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView.Settings.IsWebMessageEnabled = false;

        coreWebView.NavigationStarting += OnNavigationStarting;
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.NewWindowRequested += OnNewWindowRequested;
        coreWebView.DownloadStarting += OnDownloadStarting;
        coreWebView.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
        coreWebView.WebResourceRequested += OnWebResourceRequested;

        try
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () => navigation.TrySetCanceled(cancellationToken));
            coreWebView.Navigate(EntryPoint);
            await navigation.Task;
            cancellationToken.ThrowIfCancellationRequested();

            Bridge startedBridge = new(
                coreWebView,
                handleProjectRequestAsync,
                jobHandler);
            startedBridge.Start();
            bridge = startedBridge;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static bool IsAllowedTopLevelUri(string? value) =>
        TryParseAllowedOrigin(value, out Uri? uri) &&
        uri is not null &&
        string.Equals(uri.AbsolutePath, "/index.html", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsAllowedResourceUri(string? value) => IsAllowedOrigin(value);

    internal static bool IsAllowedOrigin(string? value) =>
        TryParseAllowedOrigin(value, out _);

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        bridge?.Dispose();
        bridge = null;
        if (!isStarted)
        {
            return;
        }

        coreWebView.NavigationStarting -= OnNavigationStarting;
        coreWebView.NavigationCompleted -= OnNavigationCompleted;
        coreWebView.NewWindowRequested -= OnNewWindowRequested;
        coreWebView.DownloadStarting -= OnDownloadStarting;
        coreWebView.WebResourceRequested -= OnWebResourceRequested;
        coreWebView.RemoveWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
        coreWebView.ClearVirtualHostNameToFolderMapping(VirtualHostName);
    }

    private static bool TryParseAllowedOrigin(
        string? value,
        out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
            string.Equals(uri.Host, VirtualHostName, StringComparison.Ordinal) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    private void OnNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsAllowedTopLevelUri(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void OnNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        TaskCompletionSource? completion = initialNavigation;
        if (completion is null || completion.Task.IsCompleted)
        {
            return;
        }

        if (args.IsSuccess && IsAllowedTopLevelUri(sender.Source))
        {
            completion.TrySetResult();
            return;
        }

        completion.TrySetException(
            new InvalidOperationException("The bundled web application could not be loaded."));
    }

    private static void OnNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private static void OnDownloadStarting(
        CoreWebView2 sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
    }

    private void OnWebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (IsAllowedResourceUri(args.Request.Uri))
        {
            return;
        }

        args.Response = coreWebView.Environment.CreateWebResourceResponse(
            null,
            403,
            "Forbidden",
            "Content-Type: text/plain\r\nCache-Control: no-store");
    }
}
