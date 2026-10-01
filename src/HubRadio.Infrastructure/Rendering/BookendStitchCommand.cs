using System.Globalization;

namespace HubRadio.Infrastructure.Rendering;

/// <summary>FR-035.1/FR-035.10 command-line contract of the bookend stitch CLI.</summary>
public static class BookendStitchCommand
{
    public const int DefaultTimeoutSeconds = 3600;
    public const int MaximumTimeoutSeconds = 86400;

    public const string Usage =
        """
        HubRadio.Stitch - Opening -> main -> Closing を結合した新しいMP4を作成します。

        使い方:
          HubRadio.Stitch --main <本編> [--opening <動画>] [--closing <動画>] --output <新規.mp4>
                          [--ffmpeg <ffmpeg.exe>] [--ffprobe <ffprobe.exe>] [--timeout-seconds <1-86400>]

          --opening と --closing の少なくとも一方が必要です。
          既存ファイルは上書きしません。出力と同じ場所に <名前>.stitch.json を作成します。
          入力: .mp4 .m4v .mov .mkv .webm   既定のツール: <実行ファイルの場所>\Tools\ffmpeg.exe, ffprobe.exe
          終了コード: 0 成功, 2 引数, 3 入力, 4 出力先, 5 ツール, 6 処理, 1 予期しないエラー
        """;

    private static readonly string[] Options =
        ["--main", "--opening", "--closing", "--output", "--ffmpeg", "--ffprobe", "--timeout-seconds"];

    public static Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter standardOutput, TextWriter standardError,
        string defaultToolDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(arguments, standardOutput, standardError, defaultToolDirectory, new BookendStitcher(), cancellationToken);

    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter standardOutput, TextWriter standardError,
        string defaultToolDirectory, BookendStitcher stitcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (arguments.Any(static argument => argument is "--help" or "-h"))
        {
            await standardOutput.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        try
        {
            BookendStitchRequest request = Parse(arguments, defaultToolDirectory);
            BookendStitchResult result = await stitcher.StitchAsync(request, cancellationToken).ConfigureAwait(false);
            await standardOutput.WriteLineAsync("output=" + result.OutputPath).ConfigureAwait(false);
            await standardOutput.WriteLineAsync("manifest=" + result.ManifestPath).ConfigureAwait(false);
            return 0;
        }
        catch (BookendStitchException exception)
        {
            await standardError.WriteLineAsync($"error {exception.Code}: {exception.Message}").ConfigureAwait(false);
            return exception.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync(
                $"error {BookendStitchException.CodeOf(BookendStitchFailure.Cancelled)}: The operation was cancelled.").ConfigureAwait(false);
            return BookendStitchException.ExitCodeOf(BookendStitchFailure.Cancelled);
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync(
                $"error {BookendStitchException.CodeOf(BookendStitchFailure.InternalError)}: Unexpected {exception.GetType().Name}.")
                .ConfigureAwait(false);
            return BookendStitchException.ExitCodeOf(BookendStitchFailure.InternalError);
        }
    }

    internal static BookendStitchRequest Parse(IReadOnlyList<string> arguments, string defaultToolDirectory)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (!Options.Contains(option, StringComparer.Ordinal))
                throw UsageError(option.StartsWith('-') && option.Length <= 32 ? $"Unknown option: {option}" : "Unexpected argument.");
            if (values.ContainsKey(option))
                throw UsageError($"Duplicate option: {option}");
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw UsageError($"Missing value for {option}");
            string value = arguments[++index];
            if (value.Length == 0)
                throw UsageError($"Empty value for {option}");
            values[option] = value;
        }

        if (!values.TryGetValue("--main", out string? main))
            throw UsageError("--main is required.");
        if (!values.TryGetValue("--output", out string? output))
            throw UsageError("--output is required.");
        values.TryGetValue("--opening", out string? opening);
        values.TryGetValue("--closing", out string? closing);
        if (opening is null && closing is null)
            throw UsageError("At least one of --opening or --closing is required.");

        int timeout = DefaultTimeoutSeconds;
        if (values.TryGetValue("--timeout-seconds", out string? timeoutText)
            && (timeoutText.Any(static c => c is < '0' or > '9')
                || !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeout)
                || timeout is < 1 or > MaximumTimeoutSeconds))
            throw UsageError("--timeout-seconds must be an integer from 1 to 86400.");

        string ffmpeg = values.TryGetValue("--ffmpeg", out string? ffmpegPath) ? ffmpegPath : Path.Combine(defaultToolDirectory, "ffmpeg.exe");
        string ffprobe = values.TryGetValue("--ffprobe", out string? ffprobePath) ? ffprobePath : Path.Combine(defaultToolDirectory, "ffprobe.exe");
        return new BookendStitchRequest(main, opening, closing, output, ffmpeg, ffprobe, TimeSpan.FromSeconds(timeout));
    }

    private static BookendStitchException UsageError(string message) => new(BookendStitchFailure.Usage, message);
}
