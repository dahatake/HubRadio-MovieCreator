using System.Text;
using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

/// <summary>FR-035 GUI native boundary (AC-008.11〜AC-008.14). Dialogs and the stitcher are fakes.</summary>
[TestClass]
public sealed class BookendStitchBridgeTests
{
    private const string Stranger = "S-1-5-21-111-222-333-4444";
    private static readonly string[] PickKeys = ["status", "clipId", "fileName"];
    private static readonly string[] RunKeys = ["status", "code", "outputFileName", "manifestFileName", "durationSeconds"];
    private string _temp = null!;

    [TestInitialize]
    public void Initialize() =>
        _temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-stitch-bridge-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, recursive: true);

    [TestMethod]
    public void ProtocolAcceptsOnlyExactClosedParameters()
    {
        string id = Guid.NewGuid().ToString("D");
        string digest = new('a', 64);
        Assert.IsTrue(Parse("stitch.pickClip", "{}", out BridgeRequest? pick));
        Assert.AreEqual(BridgeMethod.StitchPickClip, pick!.Method);
        Assert.IsFalse(Parse("stitch.pickClip", """{"role":"opening"}""", out _));
        string valid = $$"""{"projectId":"{{id}}","executionId":"{{id}}","artifactId":"{{id}}","artifactDigest":"{{digest}}","openingClipId":"{{id}}","closingClipId":null}""";
        Assert.IsTrue(Parse("stitch.run", valid, out BridgeRequest? run));
        Assert.AreEqual(BridgeMethod.StitchRun, run!.Method);
        Assert.IsTrue(Parse("stitch.run", valid.Replace("\"closingClipId\":null", $"\"closingClipId\":\"{id}\"", StringComparison.Ordinal), out _));
        foreach (string invalid in new[]
        {
            valid.Replace(",\"closingClipId\":null", "", StringComparison.Ordinal),
            valid.Replace("}", ",\"path\":\"C:\\\\x.mp4\"}", StringComparison.Ordinal),
            valid.Replace(digest, digest.ToUpperInvariant(), StringComparison.Ordinal),
            valid.Replace($"\"projectId\":\"{id}\"", $"\"projectId\":\"{id.ToUpperInvariant()}\"", StringComparison.Ordinal),
            valid.Replace($"\"openingClipId\":\"{id}\"", "\"openingClipId\":1", StringComparison.Ordinal),
            valid.Replace($"\"openingClipId\":\"{id}\"", "\"openingClipId\":\"\"", StringComparison.Ordinal),
            valid.Replace($"\"artifactId\":\"{id}\"", "\"artifactId\":null", StringComparison.Ordinal),
        })
            Assert.IsFalse(Parse("stitch.run", invalid, out _), invalid);

        string oversized = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"stitch.pickClip","params":{} }""" + new string(' ', 1000);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, oversized, out _, out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
    }

    [TestMethod]
    public void SchemaDefinesClosedStitchEnvelopesAndResults()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RepositoryPath("schemas", "web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        CollectionAssert.IsSubsetOf(new[] { "stitch.pickClip", "stitch.run" },
            definitions.GetProperty("method").GetProperty("enum").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(BookendStitchBridgeProtocol.RunProperties,
            definitions.GetProperty("stitchRunParams").GetProperty("required").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(PickKeys,
            definitions.GetProperty("stitchPickClipResult").GetProperty("required").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(RunKeys,
            definitions.GetProperty("stitchRunResult").GetProperty("required").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "selected", "cancelled", "unsupported", "forbidden", "unavailable" }, definitions
            .GetProperty("stitchPickClipResult").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "completed", "cancelled", "failed", "notFound", "invalid", "forbidden", "busy", "unavailable" }, definitions
            .GetProperty("stitchRunResult").GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(static item => item.GetString()).ToArray());
        string[] codes = definitions.GetProperty("stitchRunResult").GetProperty("properties").GetProperty("code").GetProperty("oneOf")[1]
            .GetProperty("enum").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        CollectionAssert.AreEquivalent(Enum.GetValues<BookendStitchFailure>().Where(static failure => failure != BookendStitchFailure.Usage)
            .Select(BookendStitchException.CodeOf).ToArray(), codes);
        foreach (string name in new[] { "stitchPickClipRequest", "stitchRunRequest", "stitchPickClipResponse", "stitchRunResponse", "stitchRunParams", "stitchPickClipResult", "stitchRunResult" })
            Assert.IsFalse(definitions.GetProperty(name).GetProperty("additionalProperties").GetBoolean(), name);
        JsonElement limits = definitions.GetProperty("stitchProtocolLimits");
        Assert.AreEqual(BridgeProtocol.MaxStitchRequestUtf8Bytes, limits.GetProperty("x-maxRequestUtf8Bytes").GetInt32());
        Assert.AreEqual(BridgeProtocol.MaxStitchResponseUtf8Bytes, limits.GetProperty("x-maxResponseUtf8Bytes").GetInt32());
    }

    [TestMethod]
    public void UnavailableResponsesUseTheClosedResultShapes()
    {
        AssertResult(BookendStitchBridgeJson.Unavailable(Request(BridgeMethod.StitchPickClip, new { })), PickKeys, "unavailable");
        AssertResult(BookendStitchBridgeJson.Unavailable(RunRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), null, null)),
            RunKeys, "unavailable");
    }

    [TestMethod]
    public async Task PickClipReturnsOnlyAnOpaqueIdAndBareFileName()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        string clip = Clip("Opening Jingle.MP4");
        var picker = new FakePicker { Clip = clip };
        BookendStitchBridgeHandler handler = Handler(f, picker);
        string json = await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None);
        JsonElement result = AssertResult(json, PickKeys, "selected");
        Assert.IsTrue(Guid.TryParseExact(result.GetProperty("clipId").GetString(), "D", out _));
        Assert.AreEqual("Opening Jingle.MP4", result.GetProperty("fileName").GetString());
        Assert.IsFalse(json.Contains(_temp.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(Path.GetFileName(_temp), StringComparison.OrdinalIgnoreCase), "No directory crosses the bridge.");

        foreach ((string? path, string status) in new[]
        {
            ((string?)null, "cancelled"), (Clip("notes.txt"), "unsupported"), (@"\\server\share\a.mp4", "unsupported"),
            (Path.Combine(_temp, "missing.mp4"), "unsupported"), (_temp, "unsupported"),
        })
        {
            picker.Clip = path;
            JsonElement other = AssertResult(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None), PickKeys, status);
            Assert.AreEqual(JsonValueKind.Null, other.GetProperty("clipId").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, other.GetProperty("fileName").ValueKind);
        }
    }

    [TestMethod]
    public async Task ForbiddenActorNeverOpensADialog()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        var picker = new FakePicker { Clip = Clip("op.mp4"), Output = Path.Combine(_temp, "out.mp4") };
        int stitches = 0;
        BookendStitchBridgeHandler handler = Handler(f, picker, (_, _) => { stitches++; throw new InvalidOperationException(); }, () => Stranger);
        AssertResult(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None), PickKeys, "forbidden");
        SourceOnlyResultArtifact video = result.Artifacts.First(static a => a.IsVideo);
        AssertResult(await handler.HandleAsync(Run(stored, video, Guid.NewGuid(), null), CancellationToken.None), RunKeys, "forbidden");
        Assert.AreEqual(0, picker.ClipCalls + picker.OutputCalls + stitches);
    }

    [TestMethod]
    public async Task RunStitchesTheRegisteredOutputWithoutChangingTheStore()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact video = result.Artifacts.First(static a => a.IsVideo);
        string mainPath = f.Generation.Store.GetValidatedRegisteredArtifactPath(video.ArtifactId);
        string[] before = Snapshot(f);
        string output = Path.Combine(_temp, "final.mp4");
        var picker = new FakePicker { Output = output };
        var calls = new List<BookendStitchRequest>();
        BookendStitchBridgeHandler handler = Handler(f, picker, (request, _) =>
        {
            calls.Add(request);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllBytes(mainPath, [1]), "The main output is held read-only.");
            return Task.FromResult(Result(request.OutputPath, 5.5000004m));
        });
        picker.Clip = Clip("op.mp4");
        Guid opening = ClipId(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None));
        picker.Clip = Clip("cl.mov");
        Guid closing = ClipId(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None));

        JsonElement completed = AssertResult(await handler.HandleAsync(Run(stored, video, opening, closing), CancellationToken.None), RunKeys, "completed");
        Assert.AreEqual(JsonValueKind.Null, completed.GetProperty("code").ValueKind);
        Assert.AreEqual("final.mp4", completed.GetProperty("outputFileName").GetString());
        Assert.AreEqual("final.stitch.json", completed.GetProperty("manifestFileName").GetString());
        Assert.AreEqual(5.5m, completed.GetProperty("durationSeconds").GetDecimal());
        BookendStitchRequest call = calls.Single();
        Assert.AreEqual(mainPath, call.MainPath);
        Assert.AreEqual(Clip("op.mp4"), call.OpeningPath);
        Assert.AreEqual(Clip("cl.mov"), call.ClosingPath);
        Assert.AreEqual(output, call.OutputPath);
        Assert.AreEqual(Path.Combine(_temp, "tools", "ffmpeg.exe"), call.FfmpegPath);
        Assert.AreEqual(Path.Combine(_temp, "tools", "ffprobe.exe"), call.FfprobePath);
        Assert.AreEqual(TimeSpan.FromSeconds(3600), call.ProcessTimeout);
        Assert.AreEqual(BookendStitchBridgeHandler.SuggestedFileName(video.Role), picker.SuggestedNames.Single());
        CollectionAssert.AreEqual(before, Snapshot(f));
    }

    [TestMethod]
    public void SuggestedFileNamesFollowTheRequirement()
    {
        Assert.AreEqual("hubradio-long-clean-bookend.mp4", BookendStitchBridgeHandler.SuggestedFileName(SourceOnlyResultRole.LandscapeClean));
        Assert.AreEqual("hubradio-long-subtitled-bookend.mp4", BookendStitchBridgeHandler.SuggestedFileName(SourceOnlyResultRole.LandscapeBurnIn));
        Assert.AreEqual("hubradio-short-subtitled-bookend.mp4", BookendStitchBridgeHandler.SuggestedFileName(SourceOnlyResultRole.VerticalBurnIn));
    }

    [TestMethod]
    public async Task UnresolvableMainInvalidClipsAndCancelledSaveNeverStitch()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact video = result.Artifacts.First(static a => a.IsVideo);
        SourceOnlyResultArtifact image = result.Artifacts.First(static a => !a.IsVideo);
        var picker = new FakePicker { Clip = Clip("op.mp4") };
        int stitches = 0;
        BookendStitchBridgeHandler handler = Handler(f, picker, (_, _) => { stitches++; throw new InvalidOperationException(); });
        Guid clip = ClipId(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None));
        Guid project = Guid.Parse(stored.Request.ProjectId.ToString()), execution = Guid.Parse(stored.Request.ExecutionId.ToString());

        AssertResult(await handler.HandleAsync(RunRequest(project, execution, Guid.Parse(video.ArtifactId.ToString()), new string('f', 64), clip, null), CancellationToken.None), RunKeys, "notFound");
        AssertResult(await handler.HandleAsync(RunRequest(project, execution, Guid.NewGuid(), video.ArtifactDigest.Hex, clip, null), CancellationToken.None), RunKeys, "notFound");
        AssertResult(await handler.HandleAsync(Run(stored, image, clip, null), CancellationToken.None), RunKeys, "notFound");
        AssertResult(await handler.HandleAsync(RunRequest(Guid.NewGuid(), execution, Guid.Parse(video.ArtifactId.ToString()), video.ArtifactDigest.Hex, clip, null), CancellationToken.None), RunKeys, "notFound");
        AssertResult(await handler.HandleAsync(Run(stored, video, null, null), CancellationToken.None), RunKeys, "invalid");
        AssertResult(await handler.HandleAsync(Run(stored, video, Guid.NewGuid(), null), CancellationToken.None), RunKeys, "invalid");
        Assert.AreEqual(0, picker.OutputCalls, "No save dialog opens before the main output and clips resolve.");

        picker.Output = null;
        AssertResult(await handler.HandleAsync(Run(stored, video, clip, null), CancellationToken.None), RunKeys, "cancelled");
        Assert.AreEqual(1, picker.OutputCalls);
        Assert.AreEqual(0, stitches);
    }

    [TestMethod]
    public async Task OnlyTheNewestThirtyTwoClipsRemainValid()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        var picker = new FakePicker { Clip = Clip("op.mp4"), Output = null };
        BookendStitchBridgeHandler handler = Handler(f, picker, (_, _) => throw new InvalidOperationException());
        var ids = new List<Guid>();
        for (int index = 0; index < BookendStitchClipRegistry.MaximumClips + 1; index++)
            ids.Add(ClipId(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None)));
        SourceOnlyResultArtifact video = result.Artifacts.First(static a => a.IsVideo);
        AssertResult(await handler.HandleAsync(Run(stored, video, ids[0], null), CancellationToken.None), RunKeys, "invalid");
        AssertResult(await handler.HandleAsync(Run(stored, video, ids[1], null), CancellationToken.None), RunKeys, "cancelled");
    }

    [TestMethod]
    public async Task ConcurrentRunIsBusyAndFailuresReportTheStitchCode()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact video = result.Artifacts.First(static a => a.IsVideo);
        var gate = new TaskCompletionSource<BookendStitchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new FakePicker { Clip = Clip("op.mp4"), Output = Path.Combine(_temp, "a.mp4") };
        BookendStitchException? failure = null;
        BookendStitchBridgeHandler handler = Handler(f, picker, (request, _) =>
            failure is null ? gate.Task : throw failure);
        Guid clip = ClipId(await handler.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None));
        Task<string> first = handler.HandleAsync(Run(stored, video, clip, null), CancellationToken.None);
        AssertResult(await handler.HandleAsync(Run(stored, video, clip, null), CancellationToken.None), RunKeys, "busy");
        gate.SetResult(Result(picker.Output!, 3m));
        AssertResult(await first, RunKeys, "completed");

        foreach (BookendStitchFailure kind in new[] { BookendStitchFailure.OutputExists, BookendStitchFailure.EncodeFailed,
            BookendStitchFailure.Timeout, BookendStitchFailure.OutputVerificationFailed, BookendStitchFailure.ToolUnverified })
        {
            failure = new BookendStitchException(kind, "x");
            JsonElement failed = AssertResult(await handler.HandleAsync(Run(stored, video, clip, null), CancellationToken.None), RunKeys, "failed");
            Assert.AreEqual(BookendStitchException.CodeOf(kind), failed.GetProperty("code").GetString());
            Assert.AreEqual(JsonValueKind.Null, failed.GetProperty("outputFileName").ValueKind);
        }

        failure = new BookendStitchException(BookendStitchFailure.Usage, "x");
        AssertResult(await handler.HandleAsync(Run(stored, video, clip, null), CancellationToken.None), RunKeys, "unavailable");
    }

    [TestMethod]
    public async Task FreshEmptyDialogFileIsReplacedButExistingContentIsNeverOverwritten()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact video = result.Artifacts.First(static a => a.IsVideo);
        string placeholder = Path.Combine(_temp, "placeholder.mp4");
        var picker = new FakePicker { Clip = Clip("op.mp4"), Output = placeholder, CreateEmptyOutput = true };
        bool sawPlaceholder = true;
        BookendStitchBridgeHandler fake = Handler(f, picker, (request, _) =>
        {
            sawPlaceholder = File.Exists(request.OutputPath);
            return Task.FromResult(Result(request.OutputPath, 1m));
        });
        Guid clip = ClipId(await fake.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None));
        AssertResult(await fake.HandleAsync(Run(stored, video, clip, null), CancellationToken.None), RunKeys, "completed");
        Assert.IsFalse(sawPlaceholder, "The empty file created by the dialog is removed before stitching.");

        // The real stitcher refuses existing content before verifying any tool.
        string existing = Path.Combine(_temp, "existing.mp4");
        File.WriteAllBytes(existing, [1, 2, 3]);
        var oldEmpty = Path.Combine(_temp, "old-empty.mp4");
        File.WriteAllBytes(oldEmpty, []);
        File.SetCreationTimeUtc(oldEmpty, DateTime.UtcNow.AddMinutes(-10));
        foreach (string target in new[] { existing, oldEmpty })
        {
            var realPicker = new FakePicker { Clip = Clip("op.mp4"), Output = target };
            BookendStitchBridgeHandler real = Handler(f, realPicker, stitch: null);
            Guid realClip = ClipId(await real.HandleAsync(Request(BridgeMethod.StitchPickClip, new { }), CancellationToken.None));
            byte[] bytes = File.ReadAllBytes(target);
            JsonElement failed = AssertResult(await real.HandleAsync(Run(stored, video, realClip, null), CancellationToken.None), RunKeys, "failed");
            Assert.AreEqual("stitch.output_exists", failed.GetProperty("code").GetString());
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        }
    }

    private BookendStitchBridgeHandler Handler(SourceOnlyEvaluationFixture f, FakePicker picker,
        Func<BookendStitchRequest, CancellationToken, Task<BookendStitchResult>>? stitch = null, Func<string>? actor = null) =>
        new(new SourceOnlyExecutionStore(f.Generation.DatabasePath), f.Service, new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root),
            actor ?? (() => f.Execution.Request.ActorSid), SourceOnlyBridgeTests.AuthorizationFor(f), picker,
            Path.Combine(_temp, "tools", "ffmpeg.exe"), Path.Combine(_temp, "tools", "ffprobe.exe"), stitch);

    private string Clip(string name)
    {
        string path = Path.Combine(_temp, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, [0, 1, 2]);
        return path;
    }

    private static string[] Snapshot(SourceOnlyEvaluationFixture f) =>
        f.Generation.Store.ListProjectArtifacts(f.Generation.Project.Id)
            .Select(static artifact => $"{artifact.Id}:{artifact.Kind}:{artifact.ContentDigest.Hex}:{artifact.ByteSize}:{artifact.StorageReference}")
            .Order(StringComparer.Ordinal).ToArray();

    private static BookendStitchResult Result(string output, decimal duration) =>
        new(output, BookendStitcher.ManifestPathFor(output), 1920, 1080, "30/1", 2, duration, duration, []);

    private static Guid ClipId(string json) =>
        Guid.ParseExact(AssertResult(json, PickKeys, "selected").GetProperty("clipId").GetString()!, "D");

    private static BridgeRequest Run(StoredSourceOnlyExecution stored, SourceOnlyResultArtifact artifact, Guid? opening, Guid? closing) =>
        RunRequest(Guid.Parse(stored.Request.ProjectId.ToString()), Guid.Parse(stored.Request.ExecutionId.ToString()),
            Guid.Parse(artifact.ArtifactId.ToString()), artifact.ArtifactDigest.Hex, opening, closing);

    private static BridgeRequest RunRequest(Guid project, Guid execution, Guid artifact, string digest, Guid? opening, Guid? closing)
    {
        BridgeRequest request = Request(BridgeMethod.StitchRun, new
        {
            projectId = project.ToString("D"),
            executionId = execution.ToString("D"),
            artifactId = artifact.ToString("D"),
            artifactDigest = digest,
            openingClipId = opening?.ToString("D"),
            closingClipId = closing?.ToString("D"),
        });
        Assert.IsTrue(BookendStitchBridgeProtocol.HasValidParameters(request.Method, request.Parameters));
        return request;
    }

    private static BridgeRequest Request(BridgeMethod method, object parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonSerializer.SerializeToElement(parameters));

    private static bool Parse(string method, string parameters, out BridgeRequest? request)
    {
        string json = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""";
        return BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out request, out _);
    }

    private static JsonElement AssertResult(string json, string[] keys, string status)
    {
        Assert.IsLessThanOrEqualTo(BridgeProtocol.MaxStitchResponseUtf8Bytes, Encoding.UTF8.GetByteCount(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" }, root.EnumerateObject().Select(static p => p.Name).ToArray());
        JsonElement result = root.GetProperty("result").Clone();
        CollectionAssert.AreEqual(keys, result.EnumerateObject().Select(static p => p.Name).ToArray());
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

    private sealed class FakePicker : IBookendStitchFilePicker
    {
        public string? Clip { get; set; }
        public string? Output { get; set; }
        public bool CreateEmptyOutput { get; set; }
        public int ClipCalls { get; private set; }
        public int OutputCalls { get; private set; }
        public List<string> SuggestedNames { get; } = [];

        public Task<string?> PickClipAsync(CancellationToken cancellationToken)
        {
            ClipCalls++;
            return Task.FromResult(Clip);
        }

        public Task<string?> PickOutputAsync(string suggestedFileName, CancellationToken cancellationToken)
        {
            OutputCalls++;
            SuggestedNames.Add(suggestedFileName);
            if (CreateEmptyOutput && Output is not null) File.WriteAllBytes(Output, []);
            return Task.FromResult(Output);
        }
    }
}
