using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace HubRadio.App.Web;

internal sealed class WebViewHost : IDisposable
{
    internal const string VirtualHostName = "appassets.example";
    internal const string MediaHostName = "player-media.appassets.example";
    internal const string EntryPoint = "https://appassets.example/index.html";

    private readonly CoreWebView2 coreWebView;
    private readonly string webAssetsDirectory;
    private readonly Func<BridgeRequest, CancellationToken, Task<string>>?
        handleProjectRequestAsync;
    private readonly Func<BridgeRequest, CancellationToken, Task<string>>?
        handleReviewRequestAsync;
    private readonly JobBridgeRequestHandler? jobHandler;
    private readonly TimelineBridgeRequestHandler? timelineHandler;
    private readonly IPlayerBridgeReader playerReader;
    private readonly IPlayerAccessAuthorization playerAuthorization;
    private readonly TranscriptBridgeRequestHandler? transcriptHandler;
    private readonly ThumbnailBridgeRequestHandler? thumbnailHandler;
    private readonly ComparisonBridgeRequestHandler? comparisonHandler;
    private readonly PlayerMediaResponseFactory mediaResponseFactory;
    private readonly CancellationTokenSource mediaCancellation = new();
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
            handleReviewRequestAsync: null,
            jobHandler: null,
            timelineHandler: null,
            UnavailablePlayerBridgeReader.Instance,
            UnavailablePlayerMediaAssetReader.Instance,
            DeniedPlayerAccessAuthorization.Instance,
            transcriptHandler: null,
            thumbnailHandler: null)
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
            handleReviewRequestAsync: null,
            jobHandler: null,
            timelineHandler: null,
            UnavailablePlayerBridgeReader.Instance,
            UnavailablePlayerMediaAssetReader.Instance,
            DeniedPlayerAccessAuthorization.Instance,
            transcriptHandler: null,
            thumbnailHandler: null)
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
            handleReviewRequestAsync: null,
            jobHandler,
            timelineHandler: null,
            UnavailablePlayerBridgeReader.Instance,
            UnavailablePlayerMediaAssetReader.Instance,
            DeniedPlayerAccessAuthorization.Instance,
            transcriptHandler: null,
            thumbnailHandler: null)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        Func<BridgeRequest, CancellationToken, Task<string>>?
            handleProjectRequestAsync,
        JobBridgeRequestHandler? jobHandler)
        : this(
            coreWebView,
            webAssetsDirectory,
            handleProjectRequestAsync,
            handleReviewRequestAsync: null,
            jobHandler,
            timelineHandler: null,
            UnavailablePlayerBridgeReader.Instance,
            UnavailablePlayerMediaAssetReader.Instance,
            DeniedPlayerAccessAuthorization.Instance,
            transcriptHandler: null,
            thumbnailHandler: null)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        IPlayerBridgeReader playerReader,
        IPlayerMediaAssetReader mediaAssetReader,
        IPlayerAccessAuthorization playerAuthorization)
        : this(
            coreWebView,
            webAssetsDirectory,
            handleProjectRequestAsync: null,
            handleReviewRequestAsync: null,
            jobHandler: null,
            timelineHandler: null,
            playerReader,
            mediaAssetReader,
            playerAuthorization,
            transcriptHandler: null,
            thumbnailHandler: null)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        Func<BridgeRequest, CancellationToken, Task<string>>?
            handleProjectRequestAsync,
        JobBridgeRequestHandler? jobHandler,
        IPlayerBridgeReader playerReader,
        IPlayerMediaAssetReader mediaAssetReader,
        IPlayerAccessAuthorization playerAuthorization,
        TranscriptBridgeRequestHandler? transcriptHandler = null,
        ThumbnailBridgeRequestHandler? thumbnailHandler = null,
        ComparisonBridgeRequestHandler? comparisonHandler = null)
        : this(
            coreWebView,
            webAssetsDirectory,
            handleProjectRequestAsync,
            handleReviewRequestAsync: null,
            jobHandler,
            timelineHandler: null,
            playerReader,
            mediaAssetReader,
            playerAuthorization,
            transcriptHandler,
            thumbnailHandler,
            comparisonHandler)
    {
    }

    internal WebViewHost(
        CoreWebView2 coreWebView,
        string webAssetsDirectory,
        Func<BridgeRequest, CancellationToken, Task<string>>?
            handleProjectRequestAsync,
        Func<BridgeRequest, CancellationToken, Task<string>>?
            handleReviewRequestAsync,
        JobBridgeRequestHandler? jobHandler,
        TimelineBridgeRequestHandler? timelineHandler,
        IPlayerBridgeReader playerReader,
        IPlayerMediaAssetReader mediaAssetReader,
        IPlayerAccessAuthorization playerAuthorization,
        TranscriptBridgeRequestHandler? transcriptHandler = null,
        ThumbnailBridgeRequestHandler? thumbnailHandler = null,
        ComparisonBridgeRequestHandler? comparisonHandler = null)
    {
        ArgumentNullException.ThrowIfNull(coreWebView);
        ArgumentException.ThrowIfNullOrWhiteSpace(webAssetsDirectory);
        ArgumentNullException.ThrowIfNull(playerReader);
        ArgumentNullException.ThrowIfNull(mediaAssetReader);
        ArgumentNullException.ThrowIfNull(playerAuthorization);

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
        this.handleReviewRequestAsync = handleReviewRequestAsync;
        this.jobHandler = jobHandler;
        this.timelineHandler = timelineHandler;
        this.playerReader = playerReader;
        this.playerAuthorization = playerAuthorization;
        this.transcriptHandler = transcriptHandler;
        this.thumbnailHandler = thumbnailHandler;
        this.comparisonHandler = comparisonHandler;
        mediaResponseFactory = new PlayerMediaResponseFactory(
            mediaAssetReader,
            playerAuthorization);
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
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
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
                handleReviewRequestAsync,
                jobHandler,
                timelineHandler,
                playerReader,
                playerAuthorization,
                transcriptHandler,
                thumbnailHandler,
                comparisonHandler);
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
        TryParseAllowedOrigin(value, VirtualHostName, out Uri? uri) &&
        uri is not null &&
        string.Equals(uri.AbsolutePath, "/index.html", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsAllowedResourceUri(string? value) => IsAllowedOrigin(value);

    internal static bool IsAllowedMediaAssetUri(string? value) =>
        PlayerMediaAssetReference.IsAllowedValue(value);

    internal static bool IsAllowedOrigin(string? value) =>
        TryParseAllowedOrigin(value, VirtualHostName, out _);

    internal static bool IsAllowedMediaOrigin(string? value) =>
        TryParseAllowedOrigin(value, MediaHostName, out _);

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        mediaCancellation.Cancel();
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
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        coreWebView.ClearVirtualHostNameToFolderMapping(VirtualHostName);
    }

    private static bool TryParseAllowedOrigin(
        string? value,
        string expectedHost,
        out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
            string.Equals(uri.Host, expectedHost, StringComparison.Ordinal) &&
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

        if (IsAllowedMediaOrigin(args.Request.Uri))
        {
            var deferral = args.GetDeferral();
            _ = CompleteMediaRequestAsync(
                args,
                () =>
                {
                    try
                    {
                        deferral.Complete();
                    }
                    finally
                    {
                        deferral.Dispose();
                    }
                });
            return;
        }

        args.Response = CreateEmptyResponse(403, "Forbidden");
    }

    private async Task CompleteMediaRequestAsync(
        CoreWebView2WebResourceRequestedEventArgs args,
        Action completeDeferral)
    {
        Stream? content = null;
        IRandomAccessStream? randomAccessContent = null;
        try
        {
            CoreWebView2HttpRequestHeaders requestHeaders =
                args.Request.Headers;
            string? rangeHeader = requestHeaders.Contains("Range")
                ? requestHeaders.GetHeader("Range")
                : null;
            using PlayerMediaResponse response =
                await mediaResponseFactory.CreateAsync(
                    args.Request.Method,
                    args.Request.Uri,
                    rangeHeader,
                    mediaCancellation.Token);
            content = response.DetachContent();
            if (content is not null)
            {
                randomAccessContent = content.AsRandomAccessStream();
                content = null;
            }

            args.Response = coreWebView.Environment.CreateWebResourceResponse(
                randomAccessContent,
                response.StatusCode,
                response.ReasonPhrase,
                response.Headers);
            randomAccessContent = null;
        }
        catch (Exception)
        {
            randomAccessContent?.Dispose();
            content?.Dispose();
            try
            {
                args.Response = CreateEmptyResponse(
                    503,
                    "Service Unavailable");
            }
            catch (Exception)
            {
                // The WebView may have closed while the deferred read was pending.
            }
        }
        finally
        {
            try
            {
                completeDeferral();
            }
            catch (Exception)
            {
                // The WebView may have closed while the deferred read was pending.
            }
        }
    }

    private CoreWebView2WebResourceResponse CreateEmptyResponse(
        int statusCode,
        string reasonPhrase) =>
        coreWebView.Environment.CreateWebResourceResponse(
            null,
            statusCode,
            reasonPhrase,
            "Content-Length: 0\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff");
}
