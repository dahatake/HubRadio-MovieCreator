using HubRadio.App.Projects;
using HubRadio.App.Web;
using HubRadio.Infrastructure.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace HubRadio.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly MainWindowStartupLifecycle startupLifecycle = new();
    private readonly JobBridgeRequestHandler? jobBridgeHandler;
    private Task<ApplicationBridgeHandlers>? applicationRuntimeCreation;
    private ApplicationBridgeHandlers? applicationBridgeHandlers;
    private Task<string?>? shutdownTask;
    private Task<string?>? closeCompletionTask;
    private WebViewHost? webViewHost;
    private bool isWebViewClosed;
    private bool allowFinalClose;
    private bool isWindowClosed;

    internal Task<string?> ShutdownTask => shutdownTask ?? Task.FromResult<string?>(null);

    internal Task<string?> WindowCloseTask => closeCompletionTask ?? Task.FromResult<string?>(null);

    internal string? NativeRecoveryFailureCode { get; private set; }

    public MainWindow()
        : this(jobBridgeHandler: null)
    {
    }

    internal MainWindow(JobBridgeRequestHandler? jobBridgeHandler)
    {
        this.jobBridgeHandler = jobBridgeHandler;
        InitializeComponent();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            isWindowClosed = true;
            AppWindow.Closing -= OnAppWindowClosing;
            // Release the owned control on the UI thread, while lifecycle UI access is still allowed.
            CloseWebApplicationView();
            startupLifecycle.Close();
            BeginNativeShutdown();
        };
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowFinalClose) { return; }
        // The last Window.Closed terminates WinUI's message loop. Retain the window
        // until native disposal finishes; await it asynchronously, never Wait()/Result.
        args.Cancel = true;
        CloseWebApplicationView();
        startupLifecycle.Close();
        BeginNativeShutdown();
        closeCompletionTask ??= SourceOnlyApplicationShutdown.CloseAfterStopAsync(ShutdownTask, () =>
        {
            if (isWindowClosed) { return; }
            allowFinalClose = true;
            Close();
        });
    }

    private async void OnRootGridLoaded(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        if (!startupLifecycle.TryStart())
        {
            return;
        }

        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception)
        {
            if (startupLifecycle.CanUpdateUi)
            {
                ShowStartupFailure(
                    "Web UI を開始できませんでした",
                    "アプリを再起動してください。問題が続く場合は管理者へ診断コードを伝えてください。",
                    StableErrorCodes.WebViewInitializationFailed);
            }
        }
    }

    private async Task InitializeWebViewAsync()
    {
#if HUBRADIO_PORTABLE
        string browserExecutableFolder = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
#else
        string browserExecutableFolder = string.Empty;
#endif
        Func<Task> initializeAsync =
            async () =>
            {
                // The install directory can be read-only, so the profile stays under the user's local data.
                string applicationDataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HubRadio.MovieCreator");
                string userDataFolder = Path.Combine(
                    applicationDataDirectory,
                    "WebView2");
                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateWithOptionsAsync(
                        browserExecutableFolder,
                        userDataFolder,
                        new CoreWebView2EnvironmentOptions());
                if (!startupLifecycle.CanUpdateUi)
                {
                    return;
                }

#if HUBRADIO_PORTABLE
                // SDK overrides can change the profile folder; reject them before creating a controller.
                WebViewStartupCoordinator.VerifyPath(environment.UserDataFolder, userDataFolder);
#endif
                await WebApplicationView.EnsureCoreWebView2Async(environment);
                if (!startupLifecycle.CanUpdateUi)
                {
                    return;
                }

#if HUBRADIO_PORTABLE
                // SDK overrides can supersede the requested folder; verify before creating the bridge or DB.
                try
                {
                    using System.Diagnostics.Process browserProcess =
                        System.Diagnostics.Process.GetProcessById(
                            checked((int)WebApplicationView.CoreWebView2.BrowserProcessId));
                    string? browserExecutablePath = browserProcess.MainModule?.FileName;
                    WebViewStartupCoordinator.VerifyPath(
                        browserExecutablePath,
                        Path.Combine(browserExecutableFolder, "msedgewebview2.exe"));
                }
                catch (Exception)
                {
                    // Do not expose process paths or access failure details, including inner exceptions.
                    throw new InvalidOperationException("Bundled WebView2 Runtime could not be verified.");
                }
#endif

                // Own the creation task BEFORE awaiting: Closed may run while a
                // verified offline model is being copied. Shutdown awaits and disposes
                // even a runtime that completes after the window has gone away.
                applicationRuntimeCreation = ProjectApplicationRuntime.CreateAsync(
                    this,
                    applicationDataDirectory,
                    Path.Combine(AppContext.BaseDirectory, "Profiles"),
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "DefaultSettings",
                        "Profiles"),
                    lifetimeCancellation.Token);
                ApplicationBridgeHandlers bridgeHandlers = await applicationRuntimeCreation;
                applicationBridgeHandlers = bridgeHandlers;
                if (!startupLifecycle.CanUpdateUi)
                {
                    return;
                }
                PlayerApplicationRuntime playerRuntime =
                    PlayerApplicationRuntime.Create(applicationDataDirectory);
                ThumbnailApplicationRuntime thumbnailRuntime =
                    ThumbnailApplicationRuntime.Create(
                        applicationDataDirectory,
                        Path.Combine(AppContext.BaseDirectory, "Profiles"));
                WebViewHost host = new(
                    WebApplicationView.CoreWebView2,
                    Path.Combine(AppContext.BaseDirectory, "WebAssets"),
                    bridgeHandlers.Project.HandleAsync,
                    bridgeHandlers.Review.HandleAsync,
                    jobBridgeHandler ?? bridgeHandlers.Jobs,
                    bridgeHandlers.Timeline,
                    playerRuntime.BridgeReader,
                    playerRuntime.MediaAssetReader,
                    playerRuntime.Authorization,
                    bridgeHandlers.Transcript,
                    thumbnailRuntime.RequestHandler,
                    bridgeHandlers.Comparison,
                    bridgeHandlers.Delivery,
                    bridgeHandlers.Analysis,
                    bridgeHandlers.LayoutSubtitles,
                    bridgeHandlers.Settings,
                    bridgeHandlers.GlobalSettings,
                    bridgeHandlers.FileSettings,
                    bridgeHandlers.SourceOnly,
                    bridgeHandlers.SourceOnlyMedia)
                {
                    StitchHandler = bridgeHandlers.Stitch,
                    WorkspaceHandler = bridgeHandlers.Workspace,
                    ExportHandler = bridgeHandlers.Export,
                    PortraitHandler = bridgeHandlers.Portraits,
                    DisclosureHandler = bridgeHandlers.Disclosure,
                };
                // Own the host before awaiting so Closed can detach it before closing the control.
                webViewHost = host;
                WebApplicationView.Visibility = Visibility.Visible;
                await host.StartAsync(lifetimeCancellation.Token);
            };

#if HUBRADIO_PORTABLE
        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            browserExecutableFolder,
            initializeAsync);
#else
        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(initializeAsync);
#endif

        if (!startupLifecycle.CanUpdateUi)
        {
            return;
        }

        if (result.Status == WebViewStartupStatus.Ready)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                WebApplicationView,
                "Bundled Web UI ready");
            StartupPanel.Visibility = Visibility.Collapsed;
            if (applicationBridgeHandlers is not null)
            {
                // Only the owned pipeline may recover its accepted jobs, and only
                // after the real WebView handshake. Never run generic workspace/job
                // recovery or start processing on behalf of hidden editor views.
                try
                {
                    SourceOnlyRecoveryResult recovery = await applicationBridgeHandlers.RecoverSourceOnlyAsync(lifetimeCancellation.Token);
                    NativeRecoveryFailureCode = recovery.FailureCode;
                }
                catch (OperationCanceledException) when (!startupLifecycle.CanUpdateUi) { }
                catch (Exception) { NativeRecoveryFailureCode = "source_only.recovery_failed"; }
            }
            return;
        }

        (string title, string detail) = result.Status switch
        {
            WebViewStartupStatus.RuntimeUnavailable =>
#if HUBRADIO_PORTABLE
                ("アプリの配布ファイルが不完全です",
                    "アプリを再取得してから、起動し直してください。"),
#else
                ("WebView2 Runtime を利用できません",
                    "Microsoft Edge WebView2 Runtime をインストールしてから、アプリを再起動してください。"),
#endif
            WebViewStartupStatus.InitializationFailed =>
                ("Web UI を開始できませんでした",
                    "アプリを再起動してください。問題が続く場合は管理者へ診断コードを伝えてください。"),
            _ =>
                ("Web UI を開始できませんでした",
                    "アプリを再起動してください。問題が続く場合は管理者へ診断コードを伝えてください。"),
        };

        ShowStartupFailure(
            title,
            detail,
            result.DiagnosticCode ?? StableErrorCodes.WebViewInitializationFailed);
    }

    private void BeginNativeShutdown()
    {
        // This task observes cancellation/initialization/disposal failures itself.
        // Keep it available to the application/test host; do not block the dispatcher
        // or put an unobserved async-void delegate on Closed.
        shutdownTask ??= StopNativeAsync();
    }

    private async Task<string?> StopNativeAsync()
    {
        try
        {
            string? failure = await SourceOnlyApplicationShutdown.StopAsync(
                applicationRuntimeCreation, () => lifetimeCancellation.CancelAsync()).ConfigureAwait(false);
            return failure ?? (applicationRuntimeCreation is { IsCompletedSuccessfully: true }
                ? applicationRuntimeCreation.Result.SourceOnlyRuntime?.ShutdownFailureCode : null);
        }
        catch (Exception) { return "source_only.shutdown_failed"; }
        finally { lifetimeCancellation.Dispose(); }
    }

    private void CloseWebApplicationView()
    {
        if (!startupLifecycle.CanUpdateUi || isWebViewClosed)
        {
            return;
        }

        isWebViewClosed = true;
        WebViewHost? host = webViewHost;
        webViewHost = null;
        try
        {
            host?.Dispose();
        }
        catch (Exception)
        {
            // A partially initialized host must not prevent closing the owned control.
        }

        try
        {
            try
            {
                WebApplicationView.Visibility = Visibility.Collapsed;
            }
            finally
            {
                WebApplicationView.Close();
            }
        }
        catch (Exception)
        {
            // Cleanup errors must not replace the stable native startup failure with internal details.
        }
    }

    private void ShowStartupFailure(
        string title,
        string detail,
        string diagnosticCode)
    {
        if (!startupLifecycle.CanUpdateUi)
        {
            return;
        }

        CloseWebApplicationView();
        BeginNativeShutdown();
        StartupPanel.Visibility = Visibility.Visible;
        StartupTitleText.Text = title;
        StartupDetailText.Text = detail;
        StartupDiagnosticCodeText.Text = $"診断コード: {diagnosticCode}";
        StartupDiagnosticCodeText.Visibility = Visibility.Visible;
    }
}
