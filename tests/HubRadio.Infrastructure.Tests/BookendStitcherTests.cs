using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.Infrastructure.Tests;

/// <summary>
/// FR-035 with the pinned FFmpeg/ffprobe (AC-008.1〜AC-008.4, AC-008.6). Synthetic clips only.
/// Inconclusive when <c>artifacts\portable\osl009-payload\Tools</c> is absent; that is not a PASS.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BookendStitcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static string? s_tools;
    private static string s_clips = null!;
    private static readonly Dictionary<string, (string Sha256, long Size, DateTime Modified)> s_snapshots = new(StringComparer.Ordinal);

    private string _output = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        _ = context;
        string tools = Path.Combine(RepositoryRoot(), "artifacts", "portable", "osl009-payload", "Tools");
        if (!File.Exists(Path.Combine(tools, "ffmpeg.exe")) || !File.Exists(Path.Combine(tools, "ffprobe.exe")))
            return;
        s_tools = tools;
        s_clips = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-stitch-clips-" + Guid.NewGuid().ToString("N"))).FullName;
        await EncodeAsync("op.mp4", "testsrc2=size=1280x720:rate=30:duration=1.5", "sine=frequency=600:sample_rate=44100:duration=1.5", 1);
        await EncodeAsync("main.mp4", "testsrc2=size=1920x1080:rate=30:duration=3", "sine=frequency=440:sample_rate=48000:duration=3", 2);
        await EncodeAsync("cl.mp4", "testsrc2=size=640x480:rate=25:duration=1", null, 0);
        await EncodeAsync("vmain.mp4", "testsrc2=size=1080x1920:rate=30:duration=2", "sine=frequency=500:sample_rate=48000:duration=2", 1);
        await EncodeAsync("audio-only.mp4", null, "sine=frequency=300:sample_rate=48000:duration=1", 2);
        foreach (string path in Directory.EnumerateFiles(s_clips))
            s_snapshots[path] = Snapshot(path);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        if (s_tools is not null && Directory.Exists(s_clips)) Directory.Delete(s_clips, recursive: true);
    }

    [TestInitialize]
    public void Initialize()
    {
        if (s_tools is null) Assert.Inconclusive("bookend_stitch.pinned_ffmpeg_required: artifacts\\portable\\osl009-payload\\Tools");
        _output = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-stitch-out-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (s_tools is null) return;
        foreach ((string path, var expected) in s_snapshots)
            Assert.AreEqual(expected, Snapshot(path), $"Input changed: {Path.GetFileName(path)}");
        Directory.Delete(_output, recursive: true);
    }

    [TestMethod]
    public async Task ThreeSegmentStitchMatchesPlanAndManifest()
    {
        string output = Path.Combine(_output, "final.mp4");
        (int exit, string stdout, string stderr) = await StitchAsync("--main", Clip("main.mp4"), "--opening", Clip("op.mp4"),
            "--closing", Clip("cl.mp4"), "--output", output);
        Assert.AreEqual(0, exit, stderr);
        string manifestPath = Path.Combine(_output, "final.stitch.json");
        Assert.AreEqual($"output={output}\nmanifest={manifestPath}\n", stdout.Replace("\r\n", "\n"));
        CollectionAssert.AreEquivalent(new[] { "final.mp4", "final.stitch.json" },
            Directory.EnumerateFileSystemEntries(_output).Select(Path.GetFileName).ToArray());

        JsonElement probe = await ProbeAsync(output);
        JsonElement video = Stream(probe, "video");
        JsonElement audio = Stream(probe, "audio");
        Assert.AreEqual("h264", video.GetProperty("codec_name").GetString());
        Assert.AreEqual("High", video.GetProperty("profile").GetString());
        Assert.AreEqual(1920, video.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, video.GetProperty("height").GetInt32());
        Assert.AreEqual("30/1", video.GetProperty("avg_frame_rate").GetString());
        Assert.AreEqual("aac", audio.GetProperty("codec_name").GetString());
        Assert.AreEqual("48000", audio.GetProperty("sample_rate").GetString());
        Assert.AreEqual(2, audio.GetProperty("channels").GetInt32());
        AssertDuration(5.5m, probe);

        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        Assert.IsFalse(manifestBytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "No BOM.");
        Assert.IsFalse(manifestBytes.Contains((byte)'\r'), "LF only.");
        using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
        JsonElement root = manifest.RootElement;
        Assert.AreEqual("1.0.0", root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("hubradio.bookendStitch", root.GetProperty("kind").GetString());
        Assert.AreEqual("internalOnly", root.GetProperty("publicationScope").GetString());
        Assert.IsTrue(root.GetProperty("createdAtUtc").GetString()!.EndsWith('Z'));
        Assert.AreEqual(Sha256(Path.Combine(s_tools!, "ffmpeg.exe")), root.GetProperty("tool").GetProperty("ffmpegSha256").GetString());
        Assert.AreEqual(Sha256(Path.Combine(s_tools!, "ffprobe.exe")), root.GetProperty("tool").GetProperty("ffprobeSha256").GetString());
        JsonElement outputInfo = root.GetProperty("output");
        Assert.AreEqual("final.mp4", outputInfo.GetProperty("fileName").GetString());
        Assert.AreEqual(Sha256(output), outputInfo.GetProperty("sha256").GetString());
        Assert.AreEqual(new FileInfo(output).Length, outputInfo.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(1920, outputInfo.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, outputInfo.GetProperty("height").GetInt32());
        Assert.AreEqual("30/1", outputInfo.GetProperty("frameRate").GetString());
        Assert.AreEqual(2, outputInfo.GetProperty("audioChannels").GetInt32());
        Assert.AreEqual(48000, outputInfo.GetProperty("audioSampleRate").GetInt32());
        AssertSegments(root, ("opening", "op.mp4", true, 0m, 1.5m), ("main", "main.mp4", true, 1.5m, 4.5m), ("closing", "cl.mp4", false, 4.5m, 5.5m));

        // The 16:9 opening fills the frame; the 4:3 closing is pillarboxed (240 px black each side), not cropped.
        byte[] opening = await GrayFrameAsync(output, 0.75m, 1920, 1080);
        Assert.IsGreaterThan(30d, Mean(opening, 1920, 0, 1920, 0, 8), "Opening top rows are image, not letterbox.");
        byte[] closing = await GrayFrameAsync(output, 5.0m, 1920, 1080);
        Assert.IsLessThanOrEqualTo(20d, Mean(closing, 1920, 0, 200, 0, 1080), "Closing left pillar is black.");
        Assert.IsLessThanOrEqualTo(20d, Mean(closing, 1920, 1720, 1920, 0, 1080), "Closing right pillar is black.");
        Assert.IsGreaterThan(30d, Mean(closing, 1920, 300, 1620, 0, 1080), "Closing picture is visible.");
    }

    [TestMethod]
    public async Task OpeningOnlyAndClosingOnlyProduceTwoSegments()
    {
        string openingOnly = Path.Combine(_output, "opening-only.mp4");
        (int exit, _, string stderr) = await StitchAsync("--main", Clip("main.mp4"), "--opening", Clip("op.mp4"), "--output", openingOnly);
        Assert.AreEqual(0, exit, stderr);
        AssertDuration(4.5m, await ProbeAsync(openingOnly));
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_output, "opening-only.stitch.json"))))
            AssertSegments(manifest.RootElement, ("opening", "op.mp4", true, 0m, 1.5m), ("main", "main.mp4", true, 1.5m, 4.5m));

        string closingOnly = Path.Combine(_output, "closing-only.mp4");
        (exit, _, stderr) = await StitchAsync("--main", Clip("main.mp4"), "--closing", Clip("cl.mp4"), "--output", closingOnly);
        Assert.AreEqual(0, exit, stderr);
        AssertDuration(4m, await ProbeAsync(closingOnly));
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_output, "closing-only.stitch.json"))))
            AssertSegments(manifest.RootElement, ("main", "main.mp4", true, 0m, 3m), ("closing", "cl.mp4", false, 3m, 4m));
    }

    [TestMethod]
    public async Task VerticalMonoMainLetterboxesLandscapeOpening()
    {
        string output = Path.Combine(_output, "vertical.mp4");
        (int exit, _, string stderr) = await StitchAsync("--main", Clip("vmain.mp4"), "--opening", Clip("op.mp4"), "--output", output);
        Assert.AreEqual(0, exit, stderr);
        JsonElement probe = await ProbeAsync(output);
        Assert.AreEqual(1080, Stream(probe, "video").GetProperty("width").GetInt32());
        Assert.AreEqual(1920, Stream(probe, "video").GetProperty("height").GetInt32());
        Assert.AreEqual(1, Stream(probe, "audio").GetProperty("channels").GetInt32());
        AssertDuration(3.5m, probe);
        // 1280x720 fits as 1080x608 with 656 px black above and below.
        byte[] frame = await GrayFrameAsync(output, 0.5m, 1080, 1920);
        Assert.IsLessThanOrEqualTo(20d, Mean(frame, 1080, 0, 1080, 0, 600), "Top letterbox is black.");
        Assert.IsLessThanOrEqualTo(20d, Mean(frame, 1080, 0, 1080, 1320, 1920), "Bottom letterbox is black.");
        Assert.IsGreaterThan(30d, Mean(frame, 1080, 0, 1080, 700, 1220), "The whole opening picture is visible.");
    }

    [TestMethod]
    public async Task ExistingResultIsNotOverwrittenOnRerun()
    {
        string output = Path.Combine(_output, "final.mp4");
        string[] arguments = ["--main", Clip("main.mp4"), "--closing", Clip("cl.mp4"), "--output", output];
        Assert.AreEqual(0, (await StitchAsync(arguments)).Exit);
        string outputHash = Sha256(output);
        string manifestHash = Sha256(Path.Combine(_output, "final.stitch.json"));
        (int exit, string stdout, string stderr) = await StitchAsync(arguments);
        Assert.AreEqual(4, exit);
        Assert.AreEqual("", stdout);
        Assert.IsTrue(stderr.StartsWith("error stitch.output_exists: ", StringComparison.Ordinal), stderr);
        Assert.AreEqual(outputHash, Sha256(output));
        Assert.AreEqual(manifestHash, Sha256(Path.Combine(_output, "final.stitch.json")));
        Assert.HasCount(2, Directory.EnumerateFileSystemEntries(_output).ToArray());
    }

    [TestMethod]
    public async Task AudioOnlyMp4IsRejectedWithoutOutput()
    {
        (int exit, _, string stderr) = await StitchAsync("--main", Clip("main.mp4"), "--opening", Clip("audio-only.mp4"),
            "--output", Path.Combine(_output, "final.mp4"));
        Assert.AreEqual(3, exit);
        Assert.IsTrue(stderr.StartsWith("error stitch.input_unsupported: ", StringComparison.Ordinal), stderr);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_output).ToArray());
    }

    private static void AssertSegments(JsonElement root, params (string Role, string File, bool Audio, decimal Start, decimal End)[] expected)
    {
        JsonElement[] segments = root.GetProperty("segments").EnumerateArray().ToArray();
        Assert.HasCount(expected.Length, segments);
        for (int index = 0; index < expected.Length; index++)
        {
            JsonElement segment = segments[index];
            (string role, string file, bool audio, decimal start, decimal end) = expected[index];
            Assert.AreEqual(role, segment.GetProperty("role").GetString());
            Assert.AreEqual(file, segment.GetProperty("fileName").GetString());
            Assert.AreEqual(Sha256(Clip(file)), segment.GetProperty("sha256").GetString());
            Assert.AreEqual(new FileInfo(Clip(file)).Length, segment.GetProperty("byteSize").GetInt64());
            Assert.AreEqual(audio, segment.GetProperty("hadAudio").GetBoolean());
            Assert.AreEqual(start, segment.GetProperty("outputStartSeconds").GetDecimal());
            Assert.AreEqual(end, segment.GetProperty("outputEndSeconds").GetDecimal());
            Assert.AreEqual(end - start, segment.GetProperty("sourceDurationSeconds").GetDecimal());
            Assert.AreEqual("unconfirmed", segment.GetProperty("rightsStatus").GetString());
        }
    }

    private static void AssertDuration(decimal expected, JsonElement probe)
    {
        decimal actual = decimal.Parse(probe.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.IsLessThanOrEqualTo(0.2m, Math.Abs(actual - expected), $"duration {actual} vs {expected}");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> StitchAsync(params string[] arguments)
    {
        string[] withTools = [.. arguments, "--ffmpeg", Path.Combine(s_tools!, "ffmpeg.exe"), "--ffprobe", Path.Combine(s_tools!, "ffprobe.exe"),
            "--timeout-seconds", "120"];
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await BookendStitchCommand.RunAsync(withTools, stdout, stderr, s_tools!);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static async Task<JsonElement> ProbeAsync(string path)
    {
        ProcessRunResult result = await new ProcessRunner().RunAsync(new ProcessRunRequest(Path.Combine(s_tools!, "ffprobe.exe"),
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], Path.GetDirectoryName(path)!, Timeout));
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        return JsonDocument.Parse(result.StandardOutput).RootElement.Clone();
    }

    private static JsonElement Stream(JsonElement probe, string type) =>
        probe.GetProperty("streams").EnumerateArray().Single(stream => stream.GetProperty("codec_type").GetString() == type);

    private static async Task<byte[]> GrayFrameAsync(string video, decimal seconds, int width, int height)
    {
        string frame = Path.Combine(Path.GetTempPath(), "hubradio-stitch-frame-" + Guid.NewGuid().ToString("N") + ".gray");
        try
        {
            ProcessRunResult result = await new ProcessRunner().RunAsync(new ProcessRunRequest(Path.Combine(s_tools!, "ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-v", "error", "-i", video, "-ss", seconds.ToString(CultureInfo.InvariantCulture),
                    "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-n", frame], Path.GetTempPath(), Timeout));
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            byte[] bytes = File.ReadAllBytes(frame);
            Assert.HasCount(width * height, bytes);
            return bytes;
        }
        finally
        {
            File.Delete(frame);
        }
    }

    private static double Mean(byte[] frame, int stride, int x0, int x1, int y0, int y1)
    {
        long sum = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                sum += frame[y * stride + x];
        return (double)sum / ((x1 - x0) * (y1 - y0));
    }

    private static async Task EncodeAsync(string name, string? video, string? audio, int channels)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (video is not null) arguments.AddRange(["-f", "lavfi", "-i", video]);
        if (audio is not null) arguments.AddRange(["-f", "lavfi", "-i", audio]);
        if (video is not null) arguments.AddRange(["-c:v", "h264_mf", "-pix_fmt", "yuv420p"]);
        if (audio is not null) arguments.AddRange(["-c:a", "aac", "-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-n", Path.Combine(s_clips, name)]);
        ProcessRunResult result = await new ProcessRunner().RunAsync(new ProcessRunRequest(Path.Combine(s_tools!, "ffmpeg.exe"),
            arguments, s_clips, Timeout));
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    private static string Clip(string name) => Path.Combine(s_clips, name);

    private static (string, long, DateTime) Snapshot(string path) =>
        (Sha256(path), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
