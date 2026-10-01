using System.Globalization;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.Infrastructure.Tests;

/// <summary>FR-035 command-line and failure contracts with a fake process runner (AC-008.5〜AC-008.9).</summary>
[TestClass]
public sealed class BookendStitchCommandTests
{
    private string _root = null!;
    private string _main = null!;
    private string _opening = null!;
    private string _closing = null!;
    private string _ffmpeg = null!;
    private string _ffprobe = null!;
    private string _outputDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hubradio-stitch-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _outputDirectory = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        _main = Create("main.mp4", [1, 2, 3]);
        _opening = Create("op.mov", [4, 5]);
        _closing = Create("cl.webm", [6]);
        _ffmpeg = Create("ffmpeg.exe", [7]);
        _ffprobe = Create("ffprobe.exe", [8]);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task HelpPrintsUsageAndSucceeds()
    {
        (int exit, string stdout, string stderr) = await RunAsync(new FakeMedia(), "--main", "x", "--help");
        Assert.AreEqual(0, exit);
        Assert.IsTrue(stdout.Contains("--opening", StringComparison.Ordinal));
        Assert.AreEqual("", stderr);
        Assert.AreEqual(0, (await RunAsync(new FakeMedia(), "-h")).Exit);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("--opening|o.mp4|--output|out.mp4")]
    [DataRow("--main|m.mp4|--opening|o.mp4")]
    [DataRow("--main|m.mp4|--output|out.mp4")]
    [DataRow("--main|m.mp4|--opening|o.mp4|--output|out.mp4|--bogus|1")]
    [DataRow("--main|m.mp4|--main|m.mp4|--opening|o.mp4|--output|out.mp4")]
    [DataRow("--main|m.mp4|--opening|o.mp4|--output")]
    [DataRow("--main|--opening|o.mp4|--output|out.mp4")]
    [DataRow("--main||--opening|o.mp4|--output|out.mp4")]
    [DataRow("--MAIN|m.mp4|--opening|o.mp4|--output|out.mp4")]
    [DataRow("--main|m.mp4|--opening|o.mp4|--output|out.mp4|--timeout-seconds|0")]
    [DataRow("--main|m.mp4|--opening|o.mp4|--output|out.mp4|--timeout-seconds|86401")]
    [DataRow("--main|m.mp4|--opening|o.mp4|--output|out.mp4|--timeout-seconds|abc")]
    [DataRow("--main|m.mp4|--opening|o.mp4|--output|out.mp4|--timeout-seconds|-5")]
    public async Task InvalidArgumentsAreUsageErrors(string joined)
    {
        string[] arguments = joined.Length == 0 ? [] : joined.Split('|');
        var media = new FakeMedia();
        (int exit, string stdout, string stderr) = await RunCoreAsync(media, arguments);
        Assert.AreEqual(2, exit);
        Assert.AreEqual("", stdout);
        Assert.IsTrue(stderr.StartsWith("error stitch.usage: ", StringComparison.Ordinal), stderr);
        Assert.AreEqual(1, stderr.TrimEnd().Split('\n').Length, "Failure output is a single line.");
        Assert.AreEqual(0, media.Calls.Count);
    }

    [TestMethod]
    public void ParseAppliesDefaultsAndExplicitValues()
    {
        BookendStitchRequest defaults = BookendStitchCommand.Parse(["--main", "m.mp4", "--closing", "c.mp4", "--output", "o.mp4"], @"C:\pkg\Tools");
        Assert.AreEqual(@"C:\pkg\Tools\ffmpeg.exe", defaults.FfmpegPath);
        Assert.AreEqual(@"C:\pkg\Tools\ffprobe.exe", defaults.FfprobePath);
        Assert.AreEqual(TimeSpan.FromSeconds(3600), defaults.ProcessTimeout);
        Assert.IsNull(defaults.OpeningPath);
        BookendStitchRequest explicitValues = BookendStitchCommand.Parse(["--timeout-seconds", "86400", "--output", "o.mp4",
            "--opening", "a.mp4", "--main", "m.mp4", "--ffmpeg", "f.exe", "--ffprobe", "p.exe"], @"C:\pkg\Tools");
        Assert.AreEqual(TimeSpan.FromSeconds(86400), explicitValues.ProcessTimeout);
        Assert.AreEqual("f.exe", explicitValues.FfmpegPath);
        Assert.AreEqual("a.mp4", explicitValues.OpeningPath);
        Assert.AreEqual(TimeSpan.FromSeconds(1), BookendStitchCommand.Parse(
            ["--main", "m", "--opening", "a", "--output", "o.mp4", "--timeout-seconds", "1"], "C:\\t").ProcessTimeout);
    }

    [TestMethod]
    public async Task SuccessfulRunPrintsTwoLinesAndProtectsEveryInput()
    {
        var media = new FakeMedia();
        string output = Path.Combine(_outputDirectory, "final.mp4");
        (int exit, string stdout, string stderr) = await RunAsync(media, "--main", _main, "--opening", _opening,
            "--closing", _closing, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe);
        Assert.AreEqual(0, exit, stderr);
        Assert.AreEqual($"output={output}\nmanifest={Path.Combine(_outputDirectory, "final.stitch.json")}\n", stdout.Replace("\r\n", "\n"));
        CollectionAssert.AreEquivalent(new[] { "final.mp4", "final.stitch.json" }, OutputFiles());
        ProcessRunRequest encode = media.Calls.Single(call => call.Executable == _ffmpeg);
        IReadOnlyList<string> arguments = encode.ArgumentList;
        int[] inputs = Enumerable.Range(0, arguments.Count).Where(index => arguments[index] == "-i").ToArray();
        Assert.HasCount(3, inputs);
        CollectionAssert.AreEqual(new[] { _opening, _main, _closing }, inputs.Select(index => arguments[index + 1]).ToArray());
        foreach (int index in inputs)
        {
            Assert.AreEqual("-protocol_whitelist", arguments[index - 2]);
            Assert.AreEqual("file", arguments[index - 1]);
        }

        foreach (ProcessRunRequest probe in media.Calls.Where(call => call.Executable == _ffprobe))
            CollectionAssert.IsSubsetOf(new[] { "-protocol_whitelist", "file" }, probe.ArgumentList.ToArray());
        Assert.AreEqual("-n", arguments[^2]);
        Assert.AreEqual(TimeSpan.FromSeconds(3600), encode.Timeout);
        string graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.IsTrue(graph.Contains("concat=n=3:v=1:a=1", StringComparison.Ordinal), graph);
        Assert.IsTrue(graph.Contains("anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=1", StringComparison.Ordinal),
            "The closing input without audio becomes silence.");
        Assert.IsTrue(graph.Contains("force_original_aspect_ratio=decrease", StringComparison.Ordinal));
        Assert.IsTrue(graph.Contains("pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black", StringComparison.Ordinal));
        CollectionAssert.IsSubsetOf(new[] { "h264_mf", "8000000", "384000", "30/1", "+faststart" }, arguments.ToArray());
        Assert.IsFalse(arguments.Contains("12000000"));
    }

    [TestMethod]
    public async Task HighFrameRateMonoMainSelectsRatesFromProfileDefaults()
    {
        var media = new FakeMedia { MainJson = FakeMedia.Json(1080, 1920, "60/1", "2.000000", audioChannels: 1, sar: "1:1") };
        media.ExpectedWidth = 1080;
        media.ExpectedHeight = 1920;
        media.ExpectedChannels = 1;
        media.ExpectedDuration = 3.5m;
        (int exit, _, string stderr) = await RunAsync(media, "--main", _main, "--opening", _opening,
            "--output", Path.Combine(_outputDirectory, "v.mp4"), "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe);
        Assert.AreEqual(0, exit, stderr);
        string[] arguments = media.Calls.Single(call => call.Executable == _ffmpeg).ArgumentList.ToArray();
        CollectionAssert.IsSubsetOf(new[] { "12000000", "128000", "60/1" }, arguments);
        Assert.AreEqual("1", arguments[Array.IndexOf(arguments, "-ac") + 1]);
        Assert.IsTrue(arguments[Array.IndexOf(arguments, "-filter_complex") + 1].Contains("channel_layouts=mono", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CanvasAppliesSampleAspectRatioAndEvenRounding()
    {
        Assert.AreEqual((1920, 1080), BookendStitcher.Canvas(Video(1440, 1080, (4, 3))));
        Assert.AreEqual((1278, 720), BookendStitcher.Canvas(Video(1279, 721, null)));
        Assert.AreEqual((16, 16), BookendStitcher.Canvas(Video(17, 16, (1, 1))));
        Assert.AreEqual((4096, 4096), BookendStitcher.Canvas(Video(4097, 4096, null)));
        foreach (ProbedVideoStream invalid in new[] { Video(15, 100, null), Video(100, 4098, null), Video(3000, 100, (2, 1)) })
            Assert.AreEqual(BookendStitchFailure.UnsupportedGeometry,
                Assert.ThrowsExactly<BookendStitchException>(() => BookendStitcher.Canvas(invalid)).Failure);
    }

    [TestMethod]
    public void FrameRateUsesAverageThenRealAndRejectsAbove120()
    {
        Assert.AreEqual((30000L, 1001L), BookendStitcher.FrameRate(Video(16, 16, null) with { AverageFrameRate = (30000, 1001), RealFrameRate = (60, 1) }));
        Assert.AreEqual((25L, 1L), BookendStitcher.FrameRate(Video(16, 16, null) with { AverageFrameRate = null, RealFrameRate = (25, 1) }));
        Assert.AreEqual((120L, 1L), BookendStitcher.FrameRate(Video(16, 16, null) with { AverageFrameRate = (120, 1) }));
        Assert.AreEqual(BookendStitchFailure.UnsupportedFrameRate, Assert.ThrowsExactly<BookendStitchException>(() =>
            BookendStitcher.FrameRate(Video(16, 16, null) with { AverageFrameRate = (241, 2) })).Failure);
        Assert.AreEqual(BookendStitchFailure.UnsupportedFrameRate, Assert.ThrowsExactly<BookendStitchException>(() =>
            BookendStitcher.FrameRate(Video(16, 16, null) with { AverageFrameRate = null, RealFrameRate = null })).Failure);
        Assert.IsNull(ProbedMedia.Ratio("0/0", '/'));
    }

    [TestMethod]
    public void SecondsRoundHalfAwayFromZeroToMicroseconds()
    {
        Assert.AreEqual(1.000001m, BookendStitcher.RoundSeconds(1.0000005m));
        Assert.AreEqual(1.5m, BookendStitcher.RoundSeconds(1.500000m));
        Assert.AreEqual("3", BookendStitcher.RoundSeconds(3.000000m).ToString(CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task OutputPathErrorsAreReportedBeforeInputs()
    {
        await AssertFailureAsync(new FakeMedia(), 4, "stitch.output_path_invalid", "--main", "missing.mp4", "--opening", _opening,
            "--output", Path.Combine(_outputDirectory, "final.mov"));
        await AssertFailureAsync(new FakeMedia(), 4, "stitch.output_path_invalid", "--main", _main, "--opening", _opening,
            "--output", @"\\server\share\final.mp4");
        await AssertFailureAsync(new FakeMedia(), 4, "stitch.output_directory_missing", "--main", _main, "--opening", _opening,
            "--output", Path.Combine(_root, "missing", "final.mp4"));
    }

    [TestMethod]
    public async Task ExistingOutputOrManifestIsNeverOverwritten()
    {
        string output = Path.Combine(_outputDirectory, "final.mp4");
        File.WriteAllBytes(output, [9, 9]);
        await AssertFailureAsync(new FakeMedia(), 4, "stitch.output_exists", Arguments(output));
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, File.ReadAllBytes(output));
        File.Delete(output);
        string manifest = Path.Combine(_outputDirectory, "final.stitch.json");
        File.WriteAllText(manifest, "{}");
        await AssertFailureAsync(new FakeMedia(), 4, "stitch.output_exists", Arguments(output));
        Assert.AreEqual("{}", File.ReadAllText(manifest));
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task InputPathErrorsAreReported()
    {
        string output = Path.Combine(_outputDirectory, "final.mp4");
        await AssertFailureAsync(new FakeMedia(), 3, "stitch.input_not_found", "--main", Path.Combine(_root, "none.mp4"),
            "--opening", _opening, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe);
        await AssertFailureAsync(new FakeMedia(), 3, "stitch.input_not_found", "--main", _main,
            "--opening", _outputDirectory, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe);
        await AssertFailureAsync(new FakeMedia(), 3, "stitch.input_unsupported", "--main", Create("notes.txt", [1]),
            "--opening", _opening, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe);
        await AssertFailureAsync(new FakeMedia(), 3, "stitch.input_path_invalid", "--main", @"\\server\share\a.mp4",
            "--opening", _opening, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe);
    }

    [TestMethod]
    public async Task UnverifiedOrMissingToolsNeverStartAProcess()
    {
        var media = new FakeMedia();
        string output = Path.Combine(_outputDirectory, "final.mp4");
        var stitcher = new BookendStitcher(media.RunAsync,
            async (request, token) => (await new BinaryVerifier().VerifyAsync(request, token)).IsVerified, () => DateTimeOffset.UnixEpoch);
        (int exit, _, string stderr) = await RunWithAsync(stitcher, Arguments(output));
        Assert.AreEqual(5, exit);
        Assert.IsTrue(stderr.StartsWith("error stitch.tool_unverified: ", StringComparison.Ordinal), stderr);
        (exit, _, stderr) = await RunWithAsync(new BookendStitcher(media.RunAsync, (_, _) => Task.FromResult(true), () => DateTimeOffset.UnixEpoch),
            "--main", _main, "--opening", _opening, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", Path.Combine(_root, "none.exe"));
        Assert.AreEqual(5, exit);
        Assert.IsTrue(stderr.StartsWith("error stitch.tool_unverified: ", StringComparison.Ordinal), stderr);
        Assert.AreEqual(0, media.Calls.Count);
        Assert.AreEqual(0, OutputFiles().Length);
    }

    [TestMethod]
    public async Task InputContentErrorsAreReported()
    {
        await AssertContentFailureAsync(new FakeMedia { MainJson = FakeMedia.Json(1920, 1080, "30/1", "3.000000", video: false) },
            3, "stitch.input_unsupported");
        await AssertContentFailureAsync(new FakeMedia { MainJson = FakeMedia.Json(1920, 1080, "30/1", "3.000000", format: "image2") },
            3, "stitch.input_unsupported");
        await AssertContentFailureAsync(new FakeMedia { ProbeExitCode = 1 }, 3, "stitch.input_unsupported");
        await AssertContentFailureAsync(new FakeMedia { MainJson = "not json" }, 3, "stitch.input_unsupported");
        await AssertContentFailureAsync(new FakeMedia { MainJson = FakeMedia.Json(1920, 1080, "30/1", "N/A", formatDuration: "N/A") },
            3, "stitch.input_invalid_duration");
        await AssertContentFailureAsync(new FakeMedia { MainJson = FakeMedia.Json(8192, 1080, "30/1", "3.000000") },
            3, "stitch.unsupported_geometry");
        await AssertContentFailureAsync(new FakeMedia { MainJson = FakeMedia.Json(1920, 1080, "240/1", "3.000000") },
            3, "stitch.unsupported_frame_rate");
    }

    [TestMethod]
    public async Task ProcessingFailuresLeaveNoFiles()
    {
        await AssertContentFailureAsync(new FakeMedia { EncodeExitCode = 1 }, 6, "stitch.encode_failed");
        await AssertContentFailureAsync(new FakeMedia { EncodeException = new TimeoutException() }, 6, "stitch.timeout");
        await AssertContentFailureAsync(new FakeMedia { MeasuredDurationOffset = 0.25m }, 6, "stitch.output_verification_failed");
        await AssertContentFailureAsync(new FakeMedia { MeasuredDurationOffset = -0.21m }, 6, "stitch.output_verification_failed");
        await AssertContentFailureAsync(new FakeMedia { MeasuredWidth = 1918 }, 6, "stitch.output_verification_failed");
        await AssertContentFailureAsync(new FakeMedia { MeasuredChannels = 1 }, 6, "stitch.output_verification_failed");
        await AssertContentFailureAsync(new FakeMedia { MeasuredVideoCodec = "hevc" }, 6, "stitch.output_verification_failed");
        var tolerated = new FakeMedia { MeasuredDurationOffset = 0.2m };
        (int exit, _, string stderr) = await RunAsync(tolerated, Arguments(Path.Combine(_outputDirectory, "edge.mp4")));
        Assert.AreEqual(0, exit, stderr);
    }

    [TestMethod]
    public async Task CancellationStopsAndCleansUp()
    {
        using var cancellation = new CancellationTokenSource();
        var media = new FakeMedia { OnEncode = cancellation.Cancel, Paths = (_main, _opening, _closing, _ffmpeg, _ffprobe) };
        var stitcher = new BookendStitcher(media.RunAsync, (_, _) => Task.FromResult(true), () => DateTimeOffset.UnixEpoch);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await BookendStitchCommand.RunAsync(Arguments(Path.Combine(_outputDirectory, "final.mp4")), stdout, stderr,
            _root, stitcher, cancellation.Token);
        Assert.AreEqual(6, exit);
        Assert.IsTrue(stderr.ToString().StartsWith("error stitch.cancelled: ", StringComparison.Ordinal), stderr.ToString());
        Assert.AreEqual(0, OutputFiles().Length);
    }

    private async Task AssertContentFailureAsync(FakeMedia media, int expectedExit, string expectedCode)
    {
        byte[] before = File.ReadAllBytes(_main);
        await AssertFailureAsync(media, expectedExit, expectedCode, Arguments(Path.Combine(_outputDirectory, "final.mp4")));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_main));
    }

    private async Task AssertFailureAsync(FakeMedia media, int expectedExit, string expectedCode, params string[] arguments)
    {
        string[] existing = OutputFiles();
        (int exit, string stdout, string stderr) = await RunCoreAsync(media, arguments);
        Assert.AreEqual(expectedExit, exit, stderr);
        Assert.AreEqual("", stdout);
        Assert.IsTrue(stderr.StartsWith($"error {expectedCode}: ", StringComparison.Ordinal), stderr);
        CollectionAssert.AreEquivalent(existing, OutputFiles(), "No output, manifest or temporary file may remain.");
    }

    private string[] Arguments(string output) =>
        ["--main", _main, "--opening", _opening, "--closing", _closing, "--output", output, "--ffmpeg", _ffmpeg, "--ffprobe", _ffprobe];

    private Task<(int Exit, string Stdout, string Stderr)> RunAsync(FakeMedia media, params string[] arguments) => RunCoreAsync(media, arguments);

    private Task<(int Exit, string Stdout, string Stderr)> RunCoreAsync(FakeMedia media, string[] arguments)
    {
        media.Paths = (_main, _opening, _closing, _ffmpeg, _ffprobe);
        return RunWithAsync(new BookendStitcher(media.RunAsync, (_, _) => Task.FromResult(true), () => DateTimeOffset.UnixEpoch), arguments);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunWithAsync(BookendStitcher stitcher, params string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await BookendStitchCommand.RunAsync(arguments, stdout, stderr, _root, stitcher, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string[] OutputFiles() =>
        Directory.EnumerateFileSystemEntries(_outputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private string Create(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static ProbedVideoStream Video(int width, int height, (long, long)? sar) =>
        new(width, height, sar, (30, 1), (30, 1), 1m, "h264");

    /// <summary>Plays ffprobe/ffmpeg: inputs by path, the encoded temporary output by its measured values.</summary>
    private sealed class FakeMedia
    {
        public List<ProcessRunRequest> Calls { get; } = [];
        public (string Main, string Opening, string Closing, string Ffmpeg, string Ffprobe) Paths { get; set; }
        public string MainJson { get; set; } = Json(1920, 1080, "30/1", "3.000000", audioChannels: 2);
        public int ProbeExitCode { get; set; }
        public int EncodeExitCode { get; set; }
        public Exception? EncodeException { get; set; }
        public Action? OnEncode { get; set; }
        public decimal MeasuredDurationOffset { get; set; }
        public int? MeasuredWidth { get; set; }
        public int? MeasuredChannels { get; set; }
        public string MeasuredVideoCodec { get; set; } = "h264";
        public int ExpectedWidth { get; set; } = 1920;
        public int ExpectedHeight { get; set; } = 1080;
        public int ExpectedChannels { get; set; } = 2;
        public decimal ExpectedDuration { get; set; } = 5.5m;

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request);
            string target = request.ArgumentList[^1];
            if (request.Executable == Paths.Ffmpeg)
            {
                OnEncode?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (EncodeException is not null) throw EncodeException;
                if (EncodeExitCode == 0) File.WriteAllBytes(target, [0, 0, 0, 1]);
                return Task.FromResult(new ProcessRunResult(EncodeExitCode, "", "", false, TimeSpan.Zero));
            }

            string json = target == Paths.Main ? MainJson
                : target == Paths.Opening ? Json(1280, 720, "30/1", "1.500000", audioChannels: 1)
                : target == Paths.Closing ? Json(640, 480, "25/1", "1.000000", audioChannels: null)
                : Json(MeasuredWidth ?? ExpectedWidth, ExpectedHeight, "30/1",
                    (ExpectedDuration + MeasuredDurationOffset).ToString(CultureInfo.InvariantCulture),
                    audioChannels: MeasuredChannels ?? ExpectedChannels, videoCodec: MeasuredVideoCodec);
            return Task.FromResult(new ProcessRunResult(ProbeExitCode, json, "", false, TimeSpan.Zero));
        }

        public static string Json(int width, int height, string rate, string duration, int? audioChannels = 2, bool video = true,
            string format = "mov,mp4,m4a,3gp,3g2,mj2", string? formatDuration = null, string sar = "1:1", string videoCodec = "h264")
        {
            var streams = new List<string>();
            if (video)
                streams.Add($$"""{"codec_type":"video","codec_name":"{{videoCodec}}","width":{{width}},"height":{{height}},"sample_aspect_ratio":"{{sar}}","avg_frame_rate":"{{rate}}","r_frame_rate":"{{rate}}","duration":"{{duration}}","disposition":{"attached_pic":0} }""");
            if (audioChannels is int channels)
                streams.Add($$"""{"codec_type":"audio","codec_name":"aac","channels":{{channels}},"sample_rate":"48000"}""");
            return $$"""{"streams":[{{string.Join(",", streams)}}],"format":{"format_name":"{{format}}","duration":"{{formatDuration ?? duration}}"} }""";
        }
    }
}
