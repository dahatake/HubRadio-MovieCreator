using HubRadio.App.Projects;
using HubRadio.App.Web;
using HubRadio.Infrastructure.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace HubRadio.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly MainWindowStartupLifecycle startupLifecycle = new();
    private readonly JobBridgeRequestHandler? jobBridgeHandler;
    private WebViewHost? webViewHost;
    private bool isWebViewClosed;

    public MainWindow()
        : this(jobBridgeHandler: null)
    {
    }

    internal MainWindow(JobBridgeRequestHandler? jobBridgeHandler)
    {
        this.jobBridgeHandler = jobBridgeHandler;
        InitializeComponent();
        Closed += (_, _) =>
        {
            // Release the owned control on the UI thread, while lifecycle UI access is still allowed.
            CloseWebApplicationView();
            startupLifecycle.Close();
            lifetimeCancellation.Cancel();
        };
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

                ApplicationBridgeHandlers bridgeHandlers =
                    ProjectApplicationRuntime.Create(
                    this,
                    applicationDataDirectory,
                    Path.Combine(AppContext.BaseDirectory, "Profiles"),
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "DefaultSettings",
                        "Profiles"));
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
                    bridgeHandlers.Settings);
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
        StartupPanel.Visibility = Visibility.Visible;
        StartupTitleText.Text = title;
        StartupDetailText.Text = detail;
        StartupDiagnosticCodeText.Text = $"診断コード: {diagnosticCode}";
        StartupDiagnosticCodeText.Visibility = Visibility.Visible;
    }
}
