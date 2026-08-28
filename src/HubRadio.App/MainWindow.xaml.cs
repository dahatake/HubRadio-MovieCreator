using HubRadio.App.Web;
using HubRadio.Infrastructure.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace HubRadio.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly MainWindowStartupLifecycle startupLifecycle = new();
    private WebViewHost? webViewHost;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            startupLifecycle.Close();
            lifetimeCancellation.Cancel();
            webViewHost?.Dispose();
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
        WebViewStartupResult result = await WebViewStartupCoordinator.StartAsync(
            async () =>
            {
                // The install directory can be read-only, so the profile stays under the user's local data.
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HubRadio.MovieCreator",
                    "WebView2");
                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateWithOptionsAsync(
                        string.Empty,
                        userDataFolder,
                        new CoreWebView2EnvironmentOptions());
                await WebApplicationView.EnsureCoreWebView2Async(environment);

                WebViewHost host = new(
                    WebApplicationView.CoreWebView2,
                    Path.Combine(AppContext.BaseDirectory, "WebAssets"));
                try
                {
                    WebApplicationView.Visibility = Visibility.Visible;
                    await host.StartAsync(lifetimeCancellation.Token);
                    if (!startupLifecycle.CanUpdateUi)
                    {
                        host.Dispose();
                    }
                    else
                    {
                        webViewHost = host;
                    }
                }
                catch
                {
                    host.Dispose();
                    throw;
                }
            });

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
                ("WebView2 Runtime を利用できません",
                    "Microsoft Edge WebView2 Runtime をインストールしてから、アプリを再起動してください。"),
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

    private void ShowStartupFailure(
        string title,
        string detail,
        string diagnosticCode)
    {
        StartupTitleText.Text = title;
        StartupDetailText.Text = detail;
        StartupDiagnosticCodeText.Text = $"診断コード: {diagnosticCode}";
        StartupDiagnosticCodeText.Visibility = Visibility.Visible;
    }
}