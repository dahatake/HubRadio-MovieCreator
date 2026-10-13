using System.Text;
using System.Text.Json;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

/// <summary>FR-132: saving a Job's videos and thumbnails to a folder the user chose. Stores are replaced by fakes.</summary>
[TestClass]
public sealed class JobExportBridgeTests
{
    private static readonly string[] ResultKeys = ["status", "destinationReason", "savedCount", "files", "failures"];
    private string _temp = null!;
    private string _source = null!;
    private string _dest = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-export-" + Guid.NewGuid().ToString("N"))).FullName;
        _source = Directory.CreateDirectory(Path.Combine(_temp, "source")).FullName;
        _dest = Directory.CreateDirectory(Path.Combine(_temp, "dest")).FullName;
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, recursive: true);

    // FR-132 AC-136 AC-140: closed parameters only; the destination is plain text.
    [TestMethod]
    public void ProtocolAcceptsOnlyExactClosedParameters()
    {
        string id = Guid.NewGuid().ToString("D");
        string valid = $$"""{"projectId":"{{id}}","executionId":"{{id}}","destination":"D:\\out","jobName":"Job 1"}""";
        Assert.IsTrue(Parse(valid, out BridgeRequest? request));
        Assert.AreEqual(BridgeMethod.JobExport, request!.Method);
        Assert.AreEqual("results.export", BridgeProtocol.GetMethodName(request.Method));
        Assert.IsTrue(Parse(valid.Replace("\"Job 1\"", "null", StringComparison.Ordinal), out _));
        foreach (string invalid in new[]
        {
            valid.Replace(",\"jobName\":\"Job 1\"", "", StringComparison.Ordinal),
            valid.Replace("}", ",\"extra\":1}", StringComparison.Ordinal),
            valid.Replace("D:\\\\out", "", StringComparison.Ordinal),
            valid.Replace("D:\\\\out", new string('x', 261), StringComparison.Ordinal),
            valid.Replace("D:\\\\out", "D:\\\\o\\u0000ut", StringComparison.Ordinal),
            valid.Replace("\"Job 1\"", "\"" + new string('j', 41) + "\"", StringComparison.Ordinal),
            valid.Replace("\"Job 1\"", "\"\"", StringComparison.Ordinal),
            valid.Replace("\"Job 1\"", "5", StringComparison.Ordinal),
            valid.Replace($"\"projectId\":\"{id}\"", $"\"projectId\":\"{id.ToUpperInvariant()}\"", StringComparison.Ordinal),
            valid.Replace($"\"executionId\":\"{id}\"", "\"executionId\":null", StringComparison.Ordinal),
        })
            Assert.IsFalse(Parse(invalid, out _), invalid);

        string oversized = valid[..^1] + ",\"pad\":\"" + new string('p', 2100) + "\"}";
        Assert.IsFalse(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint,
            $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"results.export","params":{{oversized}}}""", out _, out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
    }

    [TestMethod]
    public void SchemaDefinesTheClosedExportEnvelopeAndResult()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RepositoryPath("schemas", "web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        CollectionAssert.Contains(definitions.GetProperty("method").GetProperty("enum").EnumerateArray().Select(static i => i.GetString()).ToArray(), "results.export");
        CollectionAssert.AreEqual(ResultKeys, definitions.GetProperty("jobExportResult").GetProperty("required").EnumerateArray().Select(static i => i.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "projectId", "executionId", "destination", "jobName" },
            definitions.GetProperty("jobExportParams").GetProperty("required").EnumerateArray().Select(static i => i.GetString()).ToArray());
        CollectionAssert.AreEqual(Enum.GetValues<JobExportKind>().Select(JobExportBridgeProtocol.WireName).ToArray(),
            definitions.GetProperty("jobExportKind").GetProperty("enum").EnumerateArray().Select(static i => i.GetString()).ToArray());
        CollectionAssert.AreEqual(Enum.GetValues<JobExportFailureReason>().Select(JobExportBridgeProtocol.WireName).ToArray(),
            definitions.GetProperty("jobExportResult").GetProperty("properties").GetProperty("failures").GetProperty("items")
                .GetProperty("properties").GetProperty("reason").GetProperty("enum").EnumerateArray().Select(static i => i.GetString()).ToArray());
        foreach (string name in new[] { "jobExportRequest", "jobExportResponse", "jobExportParams", "jobExportResult" })
            Assert.IsFalse(definitions.GetProperty(name).GetProperty("additionalProperties").GetBoolean(), name);
        JsonElement limits = definitions.GetProperty("jobExportProtocolLimits");
        Assert.AreEqual(BridgeProtocol.MaxJobExportRequestUtf8Bytes, limits.GetProperty("x-maxRequestUtf8Bytes").GetInt32());
        Assert.AreEqual(BridgeProtocol.MaxJobExportResponseUtf8Bytes, limits.GetProperty("x-maxResponseUtf8Bytes").GetInt32());
    }

    // FR-132 AC-136: four files, each name has Project name, Job name and the kind; the originals are not touched.
    [TestMethod]
    public async Task SavesEveryDeliverableWithDescriptiveNamesAndLeavesTheOriginalsUnchanged()
    {
        JobExportItem[] items = Items(out string[] originals);
        string[] hashesBefore = originals.Select(Hash).ToArray();
        DateTime[] timesBefore = originals.Select(File.GetLastWriteTimeUtc).ToArray();
        JobExportBridgeHandler handler = Handler(items);

        JsonElement result = Result(await handler.HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "completed");

        Assert.AreEqual(4, result.GetProperty("savedCount").GetInt32());
        string[] names = Directory.GetFiles(_dest).Select(Path.GetFileName).Select(static n => n!).Order().ToArray();
        Assert.HasCount(4, names);
        foreach (string name in names)
        {
            StringAssert.Contains(name, "デモ案件");
            StringAssert.Contains(name, "Job 1");
        }

        foreach (string kind in new[] { "長尺クリーン", "短尺焼き込み", "サムネイル" })
            Assert.IsTrue(names.Any(n => n.Contains(kind, StringComparison.Ordinal)), kind);
        CollectionAssert.AreEqual(hashesBefore, originals.Select(Hash).ToArray());
        CollectionAssert.AreEqual(timesBefore, originals.Select(File.GetLastWriteTimeUtc).ToArray());
        Assert.AreEqual("video-1", File.ReadAllText(Directory.GetFiles(_dest, "*長尺クリーン*")[0]));
    }

    // FR-132 AC-136: with no Job name the creation time is used.
    [TestMethod]
    public async Task UsesTheCreationTimeWhenTheJobHasNoName()
    {
        DateTimeOffset created = new(2026, 10, 13, 12, 34, 56, TimeSpan.Zero);
        JobExportBridgeHandler handler = Handler(Items(out _), created);

        _ = Result(await handler.HandleAsync(Request(_dest, null), CancellationToken.None), "completed");

        string stamp = created.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(Directory.GetFiles(_dest).All(f => Path.GetFileName(f).Contains(stamp, StringComparison.Ordinal)));
    }

    // FR-132 AC-137: an existing file keeps its content and time; the new file gets a number.
    [TestMethod]
    public async Task NeverOverwritesAnExistingFileAndUsesANumberedName()
    {
        JobExportBridgeHandler handler = Handler(Items(out _));
        _ = Result(await handler.HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "completed");
        var before = Directory.GetFiles(_dest).ToDictionary(static f => f, static f => (Hash: Hash(f), Time: File.GetLastWriteTimeUtc(f)));
        await Task.Delay(1100);

        JsonElement second = Result(await handler.HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "completed");
        JsonElement third = Result(await handler.HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "completed");

        Assert.AreEqual(4, second.GetProperty("savedCount").GetInt32());
        Assert.AreEqual(4, third.GetProperty("savedCount").GetInt32());
        Assert.HasCount(12, Directory.GetFiles(_dest));
        foreach ((string path, (string hash, DateTime time)) in before)
        {
            Assert.AreEqual(hash, Hash(path));
            Assert.AreEqual(time, File.GetLastWriteTimeUtc(path));
        }

        foreach (JsonElement file in second.GetProperty("files").EnumerateArray())
            StringAssert.Contains(file.GetProperty("fileName").GetString(), " (2).");
        foreach (JsonElement file in third.GetProperty("files").EnumerateArray())
            StringAssert.Contains(file.GetProperty("fileName").GetString(), " (3).");
    }

    // FR-132 AC-138: some files fail; the saved ones stay, the count is real, reasons are closed words.
    [TestMethod]
    public async Task ReportsPartialFailuresWithClosedReasonsAndNoInternals()
    {
        string good = Write("good.mp4", "good");
        JobExportItem[] items =
        [
            new(JobExportKind.LongClean, "長尺クリーン", ".mp4", _ => Task.FromResult<Stream?>(File.OpenRead(good))),
            new(JobExportKind.LongBurnIn, "長尺焼き込み", ".mp4", _ => Task.FromResult<Stream?>(null)),
            new(JobExportKind.ShortBurnIn, "短尺焼き込み", ".mp4", _ => Task.FromResult<Stream?>(new FailingStream(new IOException("secret C:\\private\\path S-1-5-21-1", unchecked((int)0x80070070))))),
            new(JobExportKind.ThumbnailLandscape, "サムネイル", ".png", _ => throw new InvalidOperationException("boom S-1-5-21-2")),
            new(JobExportKind.ThumbnailVertical, "サムネイル縦型", ".png", _ => Task.FromResult<Stream?>(new FailingStream(new UnauthorizedAccessException("denied")))),
        ];
        string json = await Handler(items).HandleAsync(Request(_dest, "Job 1"), CancellationToken.None);

        JsonElement result = Result(json, "completed");
        Assert.AreEqual(1, result.GetProperty("savedCount").GetInt32());
        Assert.AreEqual(1, result.GetProperty("files").GetArrayLength());
        var failures = result.GetProperty("failures").EnumerateArray()
            .ToDictionary(static f => f.GetProperty("kind").GetString()!, static f => f.GetProperty("reason").GetString()!);
        Assert.AreEqual("missing", failures["longBurnIn"]);
        Assert.AreEqual("diskFull", failures["shortBurnIn"]);
        Assert.AreEqual("missing", failures["thumbnailLandscape"]);
        Assert.AreEqual("notWritable", failures["thumbnailVertical"]);
        Assert.HasCount(1, Directory.GetFiles(_dest), "failed files leave no partial file behind");
        Assert.AreEqual("good", File.ReadAllText(Directory.GetFiles(_dest)[0]));
        foreach (string leak in new[] { "secret", "private", "S-1-5", "boom", "denied", _temp, "Exception" })
            Assert.DoesNotContain(leak, json, StringComparison.Ordinal);
    }

    // FR-132 AC-138: a Job type without a deliverable is not counted.
    [TestMethod]
    public async Task CountsNothingWhenTheJobHasNoDeliverables()
    {
        JsonElement result = Result(await Handler([]).HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "completed");

        Assert.AreEqual(0, result.GetProperty("savedCount").GetInt32());
        Assert.AreEqual(0, result.GetProperty("failures").GetArrayLength());
        Assert.IsEmpty(Directory.GetFiles(_dest));
    }

    // FR-132 AC-139: without the permission nothing is written and the Job is not even looked up.
    [TestMethod]
    public async Task ForbiddenWritesNothingAndLooksNothingUp()
    {
        int lookups = 0;
        JobExportSourceResolver resolver = (_, _, _) => { lookups++; return Task.FromResult<JobExportSource?>(new JobExportSource("p", DateTimeOffset.UtcNow, Items(out _))); };
        foreach (Func<bool> authorized in new Func<bool>[] { () => false, () => throw new InvalidOperationException("SID S-1-5-21-9 lookup failed") })
        {
            string json = await new JobExportBridgeHandler(authorized, resolver).HandleAsync(Request(_dest, "Job 1"), CancellationToken.None);

            JsonElement result = Result(json, "forbidden");
            Assert.AreEqual(0, result.GetProperty("savedCount").GetInt32());
            Assert.DoesNotContain("S-1-5", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        }

        Assert.AreEqual(0, lookups);
        Assert.IsEmpty(Directory.GetFiles(_dest));
    }

    // FR-132 AC-140: a missing, non-folder or relative destination is refused with its reason before anything is saved.
    [TestMethod]
    public async Task RefusesAnUnusableDestinationWithItsReason()
    {
        string file = Write("a-file.txt", "x");
        int lookups = 0;
        JobExportBridgeHandler handler = new(() => true, (_, _, _) => { lookups++; return Task.FromResult<JobExportSource?>(null); });
        var cases = new (string Destination, string Reason)[]
        {
            (Path.Combine(_temp, "missing"), "notFound"),
            (file, "notFolder"),
            ("relative\\folder", "notFound"),
            (@"\\.\pipe\x", "notFound"),
        };
        foreach ((string destination, string reason) in cases)
        {
            JsonElement result = Result(await handler.HandleAsync(Request(destination, "Job 1"), CancellationToken.None), "destinationInvalid");
            Assert.AreEqual(reason, result.GetProperty("destinationReason").GetString(), destination);
            Assert.AreEqual(0, result.GetProperty("savedCount").GetInt32());
        }

        Assert.AreEqual(0, lookups);
        Assert.IsNull(JobExportWriter.CheckDestination(_dest));
        Assert.IsEmpty(Directory.GetFiles(_dest), "the writability probe leaves nothing behind");
    }

    [TestMethod]
    public async Task ReportsNotFoundForAJobThatCannotBeResolved()
    {
        JobExportBridgeHandler handler = new(() => true, (_, _, _) => Task.FromResult<JobExportSource?>(null));

        _ = Result(await handler.HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "notFound");

        Assert.IsEmpty(Directory.GetFiles(_dest));
    }

    [TestMethod]
    public void MapsWriteErrorsToClosedReasons()
    {
        Assert.AreEqual(JobExportFailureReason.DiskFull, JobExportWriter.Classify(new IOException("x", unchecked((int)0x80070070))));
        Assert.AreEqual(JobExportFailureReason.DiskFull, JobExportWriter.Classify(new IOException("x", unchecked((int)0x80070027))));
        Assert.AreEqual(JobExportFailureReason.NotWritable, JobExportWriter.Classify(new IOException("x")));
        Assert.AreEqual(JobExportFailureReason.NotWritable, JobExportWriter.Classify(new UnauthorizedAccessException()));
        Assert.AreEqual(JobExportFailureReason.Unavailable, JobExportWriter.Classify(new InvalidOperationException()));
    }

    [TestMethod]
    public void BuildsSafeFileNames()
    {
        Assert.AreEqual("案件_Job 2_長尺クリーン", JobExportWriter.BuildBaseName("案件", "Job 2", "長尺クリーン"));
        Assert.AreEqual("a_b_c_Job_x_サムネイル", JobExportWriter.BuildBaseName("a:b*c", "Job?x", "サムネイル"));
        Assert.AreEqual("Project_Job_成果物", JobExportWriter.BuildBaseName(" . ", "", "  "));
        string longName = JobExportWriter.BuildBaseName(new string('あ', 200), new string('j', 100), "短尺焼き込み");
        Assert.IsLessThanOrEqualTo(JobExportWriter.MaximumProjectNameLength + JobExportWriter.MaximumJobLabelLength + 40 + 2, longName.Length);
    }

    // FR-132 AC-141: the export only copies files into the chosen folder. It touches no network type and writes nowhere else.
    [TestMethod]
    public async Task OnlyWritesIntoTheChosenFolderAndUsesNoNetwork()
    {
        JobExportItem[] items = Items(out _);
        string[] sourceBefore = Directory.GetFileSystemEntries(_source, "*", SearchOption.AllDirectories);
        _ = Result(await Handler(items).HandleAsync(Request(_dest, "Job 1"), CancellationToken.None), "completed");

        CollectionAssert.AreEquivalent(sourceBefore, Directory.GetFileSystemEntries(_source, "*", SearchOption.AllDirectories));
        Assert.AreEqual(2, Directory.GetDirectories(_temp).Length, "no other folder is created");
        string code = File.ReadAllText(RepositoryPath("src", "HubRadio.App", "Web", "JobExportBridge.cs"));
        foreach (string network in new[] { "System.Net", "HttpClient", "WebClient", "Socket", "Uri(" })
            Assert.DoesNotContain(network, code, StringComparison.Ordinal);
    }

    private JobExportItem[] Items(out string[] originals)
    {
        string[] paths =
        [
            Write("long.mp4", "video-1"), Write("short.mp4", "video-3"), Write("thumb.png", "image-1"), Write("thumb-v.png", "image-2"),
        ];
        originals = paths;
        string[] p = paths;
        return
        [
            new(JobExportKind.LongClean, "長尺クリーン", ".mp4", _ => Task.FromResult<Stream?>(File.OpenRead(p[0]))),
            new(JobExportKind.ShortBurnIn, "短尺焼き込み", ".mp4", _ => Task.FromResult<Stream?>(File.OpenRead(p[1]))),
            new(JobExportKind.ThumbnailLandscape, "サムネイル", ".png", _ => Task.FromResult<Stream?>(File.OpenRead(p[2]))),
            new(JobExportKind.ThumbnailVertical, "サムネイル縦型", ".png", _ => Task.FromResult<Stream?>(File.OpenRead(p[3]))),
        ];
    }

    private static JobExportBridgeHandler Handler(IReadOnlyList<JobExportItem> items, DateTimeOffset? created = null) =>
        new(() => true, (_, _, _) => Task.FromResult<JobExportSource?>(new JobExportSource("デモ案件", created ?? DateTimeOffset.UtcNow, items)));

    private string Write(string name, string content)
    {
        string path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static BridgeRequest Request(string destination, string? jobName) =>
        BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.JobExport, JsonSerializer.SerializeToElement(new
        {
            projectId = Guid.NewGuid().ToString("D"),
            executionId = Guid.NewGuid().ToString("D"),
            destination,
            jobName,
        }));

    private static bool Parse(string parameters, out BridgeRequest? request)
    {
        string json = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"results.export","params":{{parameters}}}""";
        return BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out request, out _);
    }

    private static JsonElement Result(string json, string status)
    {
        Assert.IsLessThanOrEqualTo(BridgeProtocol.MaxJobExportResponseUtf8Bytes, Encoding.UTF8.GetByteCount(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" }, root.EnumerateObject().Select(static p => p.Name).ToArray());
        JsonElement result = root.GetProperty("result").Clone();
        CollectionAssert.AreEqual(ResultKeys, result.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString(), json);
        return result;
    }

    private static string RepositoryPath(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
                return Path.Combine([directory.FullName, .. parts]);
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw failure;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
