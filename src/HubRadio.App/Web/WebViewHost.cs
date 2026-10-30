using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace HubRadio.App.Web;

internal sealed class WebViewHost : IDisposable
{
    internal const string VirtualHostName = "appassets.example";
    internal const string MediaHostName = "player-media.appassets.example";
    internal const string EntryPoint = "https://appassets.example/index.html";
    internal const int StartupTimeoutMilliseconds = 30_000;

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
    private readonly DeliveryBridgeRequestHandler? deliveryHandler;
    private readonly AnalysisBridgeRequestHandler? analysisHandler;
    private readonly LayoutSubtitleBridgeRequestHandler?
        layoutSubtitleHandler;
    private readonly SettingsBridgeRequestHandler? settingsHandler;
    private readonly GlobalSettingsBridgeRequestHandler? globalSettingsHandler;
    private readonly SettingsFileBridgeRequestHandler? fileSettingsHandler;
    private readonly PlayerMediaResponseFactory mediaResponseFactory;
    private readonly SourceOnlyBridgeHandler? sourceOnlyHandler;
    private readonly SourceOnlyMediaResponseFactory? sourceOnlyMedia;
    private readonly CancellationTokenSource mediaCancellation = new();
    private Bridge? bridge;

    /// <summary>FR-035 GUI handler, forwarded to the bridge at start.</summary>
    internal BookendStitchBridgeHandler? StitchHandler { get; init; }

    internal WorkspaceLocationBridgeHandler? WorkspaceHandler { get; init; }

    internal JobExportBridgeHandler? ExportHandler { get; init; }

    internal PortraitBridgeHandler? PortraitHandler { get; init; }
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
        ComparisonBridgeRequestHandler? comparisonHandler = null,
        DeliveryBridgeRequestHandler? deliveryHandler = null,
        AnalysisBridgeRequestHandler? analysisHandler = null,
        LayoutSubtitleBridgeRequestHandler? layoutSubtitleHandler = null,
        SettingsBridgeRequestHandler? settingsHandler = null,
        GlobalSettingsBridgeRequestHandler? globalSettingsHandler = null,
        SettingsFileBridgeRequestHandler? fileSettingsHandler = null,
        SourceOnlyBridgeHandler? sourceOnlyHandler = null,
        SourceOnlyMediaResponseFactory? sourceOnlyMedia = null)
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
            comparisonHandler,
            deliveryHandler,
            analysisHandler,
            layoutSubtitleHandler,
            settingsHandler,
            globalSettingsHandler,
            fileSettingsHandler,
            sourceOnlyHandler,
            sourceOnlyMedia)
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
        ComparisonBridgeRequestHandler? comparisonHandler = null,
        DeliveryBridgeRequestHandler? deliveryHandler = null,
        AnalysisBridgeRequestHandler? analysisHandler = null,
        LayoutSubtitleBridgeRequestHandler? layoutSubtitleHandler = null,
        SettingsBridgeRequestHandler? settingsHandler = null,
        GlobalSettingsBridgeRequestHandler? globalSettingsHandler = null,
        SettingsFileBridgeRequestHandler? fileSettingsHandler = null,
        SourceOnlyBridgeHandler? sourceOnlyHandler = null,
        SourceOnlyMediaResponseFactory? sourceOnlyMedia = null)
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
        this.deliveryHandler = deliveryHandler;
        this.analysisHandler = analysisHandler;
        this.layoutSubtitleHandler = layoutSubtitleHandler;
        this.settingsHandler = settingsHandler;
        this.globalSettingsHandler = globalSettingsHandler;
        this.fileSettingsHandler = fileSettingsHandler;
        this.sourceOnlyHandler = sourceOnlyHandler;
        this.sourceOnlyMedia = sourceOnlyMedia;
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

        try
        {
            using CancellationTokenSource startupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    mediaCancellation.Token);
            coreWebView.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                webAssetsDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            coreWebView.Settings.AreDevToolsEnabled = false;
            coreWebView.Settings.AreDefaultContextMenusEnabled = false;

            coreWebView.NavigationStarting += OnNavigationStarting;
            coreWebView.NavigationCompleted += OnNavigationCompleted;
            coreWebView.NewWindowRequested += OnNewWindowRequested;
            coreWebView.DownloadStarting += OnDownloadStarting;
            coreWebView.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            coreWebView.WebResourceRequested += OnWebResourceRequested;

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
                comparisonHandler,
                deliveryHandler,
                analysisHandler,
                layoutSubtitleHandler,
                settingsHandler,
                globalSettingsHandler,
                fileSettingsHandler,
                sourceOnlyHandler)
            {
                StitchHandler = StitchHandler,
                WorkspaceHandler = WorkspaceHandler,
                ExportHandler = ExportHandler,
                PortraitHandler = PortraitHandler,
            };
            bridge = startedBridge;
            await StartDocumentAsync(
                startedBridge.Start,
                () => coreWebView.Navigate(EntryPoint),
                navigation.Task,
                startedBridge.FirstPingResponsePosted,
                startupCancellation.Token);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static Task<string> HandleSourceOnlyRequestAsync(
        BridgeRequest request,
        SourceOnlyBridgeHandler? sourceOnlyHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!BridgeProtocol.IsSourceOnlyMethod(request.Method))
        {
            throw new ArgumentException("A source-only pipeline request is required.", nameof(request));
        }

        return sourceOnlyHandler is null
            ? Task.FromResult(SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Unavailable))
            : sourceOnlyHandler.HandleAsync(request, cancellationToken);
    }

    internal static string HandleSettingsRequest(
        BridgeRequest request,
        SettingsBridgeRequestHandler? settingsHandler,
        GlobalSettingsBridgeRequestHandler? globalSettingsHandler,
        SettingsFileBridgeRequestHandler? fileSettingsHandler,
        CancellationToken cancellationToken,
        WorkspaceLocationBridgeHandler? workspaceHandler = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == BridgeMethod.SettingsWorkspaceBrowse)
        {
            return WorkspaceLocationBridgeHandler.WriteBrowseFailure(request, "unavailable");
        }

        if (request.Method is BridgeMethod.SettingsWorkspaceRead or BridgeMethod.SettingsWorkspaceUpdate)
        {
            return workspaceHandler?.Handle(request) ??
                WorkspaceLocationBridgeHandler.WriteFailure(request, "unavailable");
        }

        string? response = request.Method switch
        {
            BridgeMethod.SettingsRead or
            BridgeMethod.SettingsUpdate or
            BridgeMethod.SettingsReset or
            BridgeMethod.SettingsResetAll => settingsHandler?.Handle(request),
            BridgeMethod.SettingsGlobalRead or
            BridgeMethod.SettingsGlobalUpdate or
            BridgeMethod.SettingsGlobalReset or
            BridgeMethod.SettingsGlobalResetAll => globalSettingsHandler?.Handle(request),
            BridgeMethod.SettingsFileExport or
            BridgeMethod.SettingsFilePreview or
            BridgeMethod.SettingsFileApply => fileSettingsHandler?.Handle(request, cancellationToken),
            _ => throw new ArgumentException("A settings request is required.", nameof(request)),
        };
        return response ?? BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", "unavailable");
            writer.WriteNull("snapshot");
            if (request.Method is BridgeMethod.SettingsFileExport or
                BridgeMethod.SettingsFilePreview or BridgeMethod.SettingsFileApply)
            {
                writer.WriteNull("fileSha256");
            }

            writer.WriteEndObject();
        });
    }

    internal static async Task StartDocumentAsync(
        Action startBridge,
        Action navigate,
        Task navigation,
        Task firstPingResponsePosted,
        CancellationToken cancellationToken,
        int timeoutMilliseconds = StartupTimeoutMilliseconds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startBridge();
        navigate();
        await WaitForDocumentAsync().WaitAsync(
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        async Task WaitForDocumentAsync()
        {
            // One deadline covers both signals; navigation failure is immediate.
            await navigation;
            await firstPingResponsePosted;
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
        PlayerMediaAssetReference.IsAllowedValue(value) || SourceOnlyMediaAssetReference.TryParse(value, out _);

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
        initialNavigation?.TrySetCanceled(mediaCancellation.Token);
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
            if (!IsAllowedTopLevelUri(sender.Source))
            {
                args.Response = CreateEmptyResponse(403, "Forbidden");
                return;
            }

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
                await CreateMediaResponseAsync(
                    args.Request.Method,
                    args.Request.Uri,
                    rangeHeader,
                    mediaResponseFactory,
                    sourceOnlyMedia,
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

    internal static Task<PlayerMediaResponse> CreateMediaResponseAsync(
        string? method,
        string? uri,
        string? rangeHeader,
        PlayerMediaResponseFactory mediaResponseFactory,
        SourceOnlyMediaResponseFactory? sourceOnlyMedia,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SourceOnlyMediaAssetReference.IsSourceOnlyRequest(uri))
        {
            return sourceOnlyMedia is null
                ? Task.FromResult(SourceOnlyMediaResponseFactory.Unavailable())
                : sourceOnlyMedia.CreateAsync(method, uri, rangeHeader, cancellationToken);
        }

        return mediaResponseFactory.CreateAsync(method, uri, rangeHeader, cancellationToken);
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
