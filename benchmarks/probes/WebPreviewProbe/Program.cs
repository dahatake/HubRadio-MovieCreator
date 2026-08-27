using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HubRadio.WebPreviewProbe;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 5 || !string.Equals(args[0], "capture", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Usage: HubRadio.WebPreviewProbe capture <html> <manifest> <base-png> <output-directory>");
            return 2;
        }

        try
        {
            string htmlPath = RequireFile(args[1]);
            string manifestPath = RequireFile(args[2]);
            string baseImagePath = RequireFile(args[3]);
            string outputDirectory = Path.GetFullPath(args[4]);
            Directory.CreateDirectory(outputDirectory);

            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement profile = manifest.RootElement.GetProperty("profile");
            int width = profile.GetProperty("width").GetInt32();
            int height = profile.GetProperty("height").GetInt32();
            if (manifest.RootElement.GetProperty("manifestId").GetString() != "st-013-preview-fidelity-v1" ||
                width != 1280 || height != 720)
            {
                throw new InvalidDataException("Unexpected ST-013 manifest identity or profile.");
            }

            ApplicationConfiguration.Initialize();
            using CaptureForm form = new(
                htmlPath,
                manifest.RootElement.Clone(),
                baseImagePath,
                outputDirectory,
                width,
                height);
            Application.Run(form);
            if (form.Failure is not null)
            {
                throw form.Failure;
            }
            Console.WriteLine(JsonSerializer.Serialize(form.Result));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WEBVIEW2_CAPTURE_FAILED: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static string RequireFile(string value)
    {
        string path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required capture input does not exist.");
        }
        return path;
    }

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    internal static partial Regex SafeIdentifierPattern();
}

internal sealed class CaptureForm : Form
{
    private readonly string _htmlPath;
    private readonly JsonElement _manifest;
    private readonly string _baseImagePath;
    private readonly string _outputDirectory;
    private readonly WebView2 _webView;
    private TaskCompletionSource<JsonElement>? _messageCompletion;
    private string? _runtimeVersion;
    private double _hostDpiScale;
    private double _zoomFactor;

    internal CaptureForm(
        string htmlPath,
        JsonElement manifest,
        string baseImagePath,
        string outputDirectory,
        int width,
        int height)
    {
        _htmlPath = htmlPath;
        _manifest = manifest;
        _baseImagePath = baseImagePath;
        _outputDirectory = outputDirectory;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(width, height);
        FormBorderStyle = FormBorderStyle.None;
        Location = new Point(SystemInformation.VirtualScreen.Left, SystemInformation.VirtualScreen.Top);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "ST-013 WebView2 Capture";
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        Shown += OnShownAsync;
    }

    internal object? Result { get; private set; }
    internal Exception? Failure { get; private set; }

    private async void OnShownAsync(object? sender, EventArgs eventArgs)
    {
        try
        {
            Result = await CaptureAsync();
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            Close();
        }
    }

    private async Task<object> CaptureAsync()
    {
        _runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        string userDataDirectory = Path.Combine(_outputDirectory, "webview2-user-data");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataDirectory,
            options: null);
        await _webView.EnsureCoreWebView2Async(environment);
        _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        _webView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.ZoomFactor = 1.0;
        _hostDpiScale = DeviceDpi / 96.0;
        _zoomFactor = _webView.ZoomFactor;

        Uri pageUri = new UriBuilder(new Uri(_htmlPath)) { Query = "scoutTheme=light" }.Uri;
        _webView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (!string.Equals(args.Uri, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs> navigationCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _webView.CoreWebView2.NavigationCompleted += NavigationCompleted;
        _messageCompletion = NewMessageCompletion();
        _webView.CoreWebView2.Navigate(pageUri.AbsoluteUri);
        CoreWebView2NavigationCompletedEventArgs navigation =
            await navigationCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!navigation.IsSuccess)
        {
            throw new InvalidOperationException($"WebView2 navigation failed: {navigation.WebErrorStatus}.");
        }
        JsonElement ready = await _messageCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (ready.GetProperty("type").GetString() != "preview-ready")
        {
            throw new InvalidDataException("The preview page did not report readiness.");
        }

        string baseImageDataUrl = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(_baseImagePath));
        List<object> captures = [];
        foreach (string mode in _manifest.GetProperty("comparison").GetProperty("webRenderModes").EnumerateArray().Select(value => value.GetString()!))
        {
            if (mode is not ("canvas" or "svg"))
            {
                throw new InvalidDataException("The manifest contains an unsupported web render mode.");
            }
            foreach (JsonElement sample in _manifest.GetProperty("samples").EnumerateArray())
            {
                string sampleId = sample.GetProperty("sampleId").GetString()!;
                if (!Program.SafeIdentifierPattern().IsMatch(sampleId))
                {
                    throw new InvalidDataException("A sample identifier is unsafe.");
                }
                JsonElement time = sample.GetProperty("outputTime");
                double outputTimeSeconds = (double)time.GetProperty("value").GetInt64() /
                    time.GetProperty("rate").GetInt64();
                bool cueExpected = sample.GetProperty("cueExpected").GetBoolean();
                _messageCompletion = NewMessageCompletion();
                _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    mode,
                    sampleId,
                    outputTimeSeconds,
                    manifest = _manifest,
                    baseImageDataUrl,
                }));
                JsonElement renderResult = await _messageCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (renderResult.GetProperty("type").GetString() == "render-error")
                {
                    string detail = renderResult.TryGetProperty("message", out JsonElement message)
                        ? message.GetString() ?? "No detail"
                        : "No detail";
                    throw new InvalidOperationException(
                        $"Preview page render failed: {renderResult.GetProperty("code").GetString()}: {detail}.");
                }
                if (renderResult.GetProperty("type").GetString() != "render-complete" ||
                    renderResult.GetProperty("mode").GetString() != mode ||
                    renderResult.GetProperty("sampleId").GetString() != sampleId ||
                    renderResult.GetProperty("cueVisible").GetBoolean() != cueExpected ||
                    renderResult.GetProperty("sourcePixelsIncludedInMessage").GetBoolean() is not true ||
                    renderResult.GetProperty("sourcePixelsIncludedInHostResult").GetBoolean() is not false)
                {
                    throw new InvalidDataException("Preview page returned inconsistent render metadata.");
                }

                string fileName = $"webview2-{mode}-{sampleId}.png";
                string capturePath = Path.Combine(_outputDirectory, fileName);
                await using (FileStream stream = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                using Image image = Image.FromFile(capturePath);
                FileInfo file = new(capturePath);
                captures.Add(new
                {
                    mode,
                    sampleId,
                    outputTime = new
                    {
                        value = time.GetProperty("value").GetInt64(),
                        rate = time.GetProperty("rate").GetInt64(),
                    },
                    cueExpected,
                    cueVisible = renderResult.GetProperty("cueVisible").GetBoolean(),
                    viewport = renderResult.GetProperty("viewport").Clone(),
                    devicePixelRatio = renderResult.GetProperty("devicePixelRatio").GetDouble(),
                    rectangles = renderResult.GetProperty("rectangles").Clone(),
                    requestedFontsAvailable = renderResult.GetProperty("requestedFontsAvailable").Clone(),
                    capture = new
                    {
                        fileName,
                        byteSize = file.Length,
                        sha256 = await FileSha256Async(capturePath),
                        width = image.Width,
                        height = image.Height,
                    },
                    sourcePixelsIncludedInHostResult = false,
                });
            }
        }
        _webView.CoreWebView2.NavigationCompleted -= NavigationCompleted;
        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        TaskCompletionSource<bool> browserExitCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += BrowserProcessExited;
        _webView.Dispose();
        await browserExitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        environment.BrowserProcessExited -= BrowserProcessExited;
        if (Directory.Exists(userDataDirectory))
        {
            Directory.Delete(userDataDirectory, recursive: true);
        }
        bool userDataDirectoryRemoved = !Directory.Exists(userDataDirectory);
        if (!userDataDirectoryRemoved)
        {
            throw new IOException("The dedicated WebView2 user-data directory was not removed.");
        }
        return new
        {
            schemaVersion = "1.0.0",
            actualWebView2Executed = true,
            captureApi = "CoreWebView2.CapturePreviewAsync",
            runtimeVersion = _runtimeVersion,
            environmentBrowserVersion = environment.BrowserVersionString,
            sdkAssemblyVersion = typeof(CoreWebView2).Assembly.GetName().Version?.ToString(),
            hostDpiScale = _hostDpiScale,
            zoomFactor = _zoomFactor,
            captureCount = captures.Count,
            captures,
            externalNavigationAllowed = false,
            dynamicDataSentViaPostWebMessageAsJson = true,
            sourcePixelsTransferredToLocalWebView2 = true,
            browserProcessExitObservedBeforeHostResult = true,
            userDataDirectoryRemoved,
            sourcePixelsIncludedInResult = false,
        };

        void NavigationCompleted(object? _, CoreWebView2NavigationCompletedEventArgs args)
        {
            navigationCompletion.TrySetResult(args);
        }

        void BrowserProcessExited(object? _, CoreWebView2BrowserProcessExitedEventArgs args)
        {
            _ = args;
            browserExitCompletion.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<JsonElement> NewMessageCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(args.WebMessageAsJson);
            _messageCompletion?.TrySetResult(document.RootElement.Clone());
        }
        catch (Exception exception)
        {
            _messageCompletion?.TrySetException(exception);
        }
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}