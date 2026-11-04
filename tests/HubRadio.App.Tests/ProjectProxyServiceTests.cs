using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

/// <summary>FR-144: the source-video proxy of the settings screen (AC-176, AC-178) and its media / bridge boundary.</summary>
[TestClass]
public sealed class ProjectProxyServiceTests
{
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    // FR-144 AC-176: the ffmpeg arguments scale to the PARAM-011 height (360) and only read the source.
    [TestMethod]
    public void ArgumentsScaleToTheProxyHeightAndWriteOnlyTheOutput()
    {
        string[] args = ProjectProxyService.BuildArguments(@"C:\in.mov", @"C:\out.tmp", "h264_mf").ToArray();
        Assert.AreEqual("scale=-2:360", args[Array.IndexOf(args, "-vf") + 1]);
        Assert.AreEqual(@"C:\in.mov", args[Array.IndexOf(args, "-i") + 1]);
        Assert.AreEqual(@"C:\out.tmp", args[^1]);
        Assert.AreEqual(1, args.Count(a => a == @"C:\in.mov"));
        Assert.AreEqual("mp4", args[Array.IndexOf(args, "-f") + 1]);
    }

    // FR-144 AC-176: after the source is chosen a 360 px proxy is made; once the Project is created it is in the
    // Project folder, and the source video is unchanged.
    [TestMethod]
    public async Task ProxyIsMadeThenMovesIntoTheProjectFolderAndTheSourceIsUnchanged()
    {
        using var f = new Fixture();
        string before = Hash(f.Source);
        DateTime stamp = File.GetLastWriteTimeUtc(f.Source);

        f.Service.Request(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);

        Assert.AreEqual(ProjectProxyStatus.Ready, f.Service.Get(f.DraftId).Status);
        Assert.IsTrue(f.Calls.Single().ArgumentList.Contains("scale=-2:360"));
        string staged = Path.Combine(f.Workspace, "proxies", f.DraftId.ToString(), ProjectProxyService.FileName);
        Assert.IsTrue(File.Exists(staged));

        f.Project = EntityId<ProjectIdTag>.New();
        f.Service.OnFinalized(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);

        string expected = Path.Combine(f.Workspace, "projects", f.Project.ToString(), "proxy", ProjectProxyService.FileName);
        Assert.AreEqual(expected, f.Service.PathFor(f.DraftId));
        Assert.IsTrue(File.Exists(expected));
        Assert.IsFalse(File.Exists(staged));
        Assert.AreEqual(1, f.Calls.Count, "a ready proxy is moved, not made again");
        Assert.AreEqual(before, Hash(f.Source));
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(f.Source));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(expected)!, "*.tmp").Length);
    }

    // FR-144 AC-176: a run still going when the Project is created finishes straight into the Project folder.
    [TestMethod]
    public async Task ARunInProgressWhenTheProjectIsCreatedFinishesInTheProjectFolder()
    {
        using var f = new Fixture();
        var gate = new TaskCompletionSource();
        f.Gate = gate.Task;
        f.Service.Request(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Generating, f.Service.Get(f.DraftId).Status);

        f.Project = EntityId<ProjectIdTag>.New();
        f.Service.OnFinalized(f.DraftId);
        gate.SetResult();
        await f.Service.WhenIdleAsync(f.DraftId);

        Assert.IsTrue(File.Exists(Path.Combine(f.Workspace, "projects", f.Project.ToString(), "proxy", ProjectProxyService.FileName)));
        Assert.AreEqual(1, f.Calls.Count);
    }

    // FR-144 AC-178: not generated / generating show a state and a reason.
    [TestMethod]
    public async Task MissingAndGeneratingStatesCarryAReason()
    {
        using var f = new Fixture();
        ProjectProxyState missing = f.Service.Get(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Missing, missing.Status);
        Assert.AreEqual(ProjectProxyService.ReasonMissing, missing.Reason);

        var gate = new TaskCompletionSource();
        f.Gate = gate.Task;
        ProjectProxyState started = f.Service.Regenerate(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Generating, started.Status);
        Assert.AreEqual(ProjectProxyService.ReasonGenerating, started.Reason);
        Assert.AreEqual(ProjectProxyStatus.Generating, f.Service.Regenerate(f.DraftId).Status, "a running run is kept");
        gate.SetResult();
        await f.Service.WhenIdleAsync(f.DraftId);
        Assert.AreEqual(1, f.Calls.Count);
    }

    // FR-144 AC-178: failures are a state with a cause and never throw; 再生成 makes the proxy again.
    [TestMethod]
    public async Task FailureIsAStateWithACauseAndRegenerateMakesItAgain()
    {
        using var f = new Fixture();
        f.Succeed = false;
        f.Service.Request(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);

        ProjectProxyState failed = f.Service.Get(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Failed, failed.Status);
        Assert.AreEqual(ProjectProxyService.ReasonEncodeFailed, failed.Reason);
        Assert.AreEqual(3, f.Calls.Count, "every encoder is tried before giving up");
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(f.Workspace, "proxies"), "*", SearchOption.AllDirectories).Any());

        f.Succeed = true;
        _ = f.Service.Regenerate(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Ready, f.Service.Get(f.DraftId).Status);
    }

    // FR-144 AC-178: a blocked destination and an unreadable source are failures with their own reason.
    [TestMethod]
    public async Task BlockedDestinationAndMissingSourceReportTheirReasons()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Service.PathFor(f.DraftId));
        _ = f.Service.Regenerate(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        ProjectProxyState blocked = f.Service.Get(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Failed, blocked.Status);
        Assert.AreEqual(ProjectProxyService.ReasonWriteFailed, blocked.Reason);

        Directory.Delete(f.Service.PathFor(f.DraftId));
        File.Delete(f.Source);
        _ = f.Service.Regenerate(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        Assert.AreEqual(ProjectProxyService.ReasonSourceMissing, f.Service.Get(f.DraftId).Reason);

        f.NoSource = true;
        _ = f.Service.Regenerate(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        Assert.AreEqual(ProjectProxyService.ReasonSourceUnavailable, f.Service.Get(f.DraftId).Reason);
    }

    // FR-144 AC-178: a proxy failure never fails Project creation (the hook swallows it) and a deleted Project loses its staged proxy.
    [TestMethod]
    public async Task DeleteRemovesTheStagedProxyAndForgetsTheFailure()
    {
        using var f = new Fixture();
        f.Service.Request(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        f.Service.Delete(f.DraftId);
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Workspace, "proxies", f.DraftId.ToString())));
        Assert.AreEqual(ProjectProxyStatus.Missing, f.Service.Get(f.DraftId).Status);
    }

    // FR-144 AC-177: the media route serves a listed Project's current proxy with ranges (seek) and rejects the rest.
    [TestMethod]
    public async Task MediaRouteServesRangesOfTheCurrentProxyOfAListedProject()
    {
        using var f = new Fixture();
        f.Service.Request(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        string version = f.Service.Get(f.DraftId).Version!;
        string uri = SourceOnlyMediaResponseFactory.ProxyUri(f.DraftId, version);
        Assert.AreEqual($"https://player-media.appassets.example/source-only/proxy/{f.DraftId}/{version}", uri);

        using (PlayerMediaResponse all = SourceOnlyMediaResponseFactory.CreateProxyResponse(uri, null, f.Service, _ => true))
        {
            Assert.AreEqual(200, all.StatusCode);
            StringAssert.Contains(all.Headers, "Content-Type: video/mp4");
            StringAssert.Contains(all.Headers, "Accept-Ranges: bytes");
            using var copy = new MemoryStream();
            all.Content!.CopyTo(copy);
            CollectionAssert.AreEqual(Mp4, copy.ToArray());
        }

        using (PlayerMediaResponse part = SourceOnlyMediaResponseFactory.CreateProxyResponse(uri, "bytes=8-11", f.Service, _ => true))
        {
            Assert.AreEqual(206, part.StatusCode);
            StringAssert.Contains(part.Headers, $"Content-Range: bytes 8-11/{Mp4.Length}");
            using var copy = new MemoryStream();
            part.Content!.CopyTo(copy);
            CollectionAssert.AreEqual(Mp4[8..12], copy.ToArray());
        }

        using (PlayerMediaResponse bad = SourceOnlyMediaResponseFactory.CreateProxyResponse(uri, "bytes=999-", f.Service, _ => true))
        {
            Assert.AreEqual(416, bad.StatusCode);
        }

        string[] rejected =
        [
            uri.ToUpperInvariant(), uri + "/", uri + "?x=1",
            SourceOnlyMediaResponseFactory.ProxyUri(f.DraftId, new string('0', 32)),
            SourceOnlyMediaResponseFactory.ProxyUri(EntityId<ProjectCreationDraftIdTag>.New(), version),
            SourceOnlyMediaResponseFactory.ProxyPrefix + "../" + f.DraftId + "/" + version,
        ];
        foreach (string url in rejected)
        {
            using PlayerMediaResponse response = SourceOnlyMediaResponseFactory.CreateProxyResponse(url, null, f.Service, _ => true);
            Assert.AreEqual(404, response.StatusCode, url);
        }

        using PlayerMediaResponse deleted = SourceOnlyMediaResponseFactory.CreateProxyResponse(uri, null, f.Service, _ => false);
        Assert.AreEqual(404, deleted.StatusCode);
        using PlayerMediaResponse noService = SourceOnlyMediaResponseFactory.CreateProxyResponse(uri, null, null, _ => true);
        Assert.AreEqual(404, noService.StatusCode);
    }

    // FR-144 AC-178: the bridge reports the state (with a reason and, when ready, the media URI) and needs PreviewProject.
    [TestMethod]
    public async Task BridgeReportsStatesAndRegeneratesAndIsClosed()
    {
        using var f = new Fixture();
        bool allowed = true;
        var handler = new ProxyBridgeHandler(f.Service, () => allowed, _ => true);
        string id = f.DraftId.ToString();

        Assert.IsTrue(Parse("proxy.get", $$"""{"draftId":"{{id}}"}""", out BridgeRequest? get));
        Assert.AreEqual(BridgeMethod.ProxyGet, get!.Method);
        Assert.AreEqual("proxy.regenerate", BridgeProtocol.GetMethodName(BridgeMethod.ProxyRegenerate));
        Assert.IsFalse(Parse("proxy.get", "{}", out _));
        Assert.IsFalse(Parse("proxy.get", $$"""{"draftId":"{{id}}","x":1}""", out _));
        Assert.IsFalse(Parse("proxy.regenerate", """{"draftId":"nope"}""", out _));

        JsonElement missing = Result(handler.Handle(get));
        Assert.AreEqual("missing", missing.GetProperty("status").GetString());
        Assert.AreEqual(ProjectProxyService.ReasonMissing, missing.GetProperty("reason").GetString());
        Assert.AreEqual(JsonValueKind.Null, missing.GetProperty("uri").ValueKind);
        Assert.AreEqual(360, missing.GetProperty("height").GetInt32());

        Assert.IsTrue(Parse("proxy.regenerate", $$"""{"draftId":"{{id}}"}""", out BridgeRequest? regenerate));
        string afterRegenerate = Result(handler.Handle(regenerate!)).GetProperty("status").GetString()!;
        Assert.IsTrue(afterRegenerate is "generating" or "missing" or "ready", afterRegenerate);
        await f.Service.WhenIdleAsync(f.DraftId);

        JsonElement ready = Result(handler.Handle(get));
        Assert.AreEqual("ready", ready.GetProperty("status").GetString());
        StringAssert.StartsWith(ready.GetProperty("uri").GetString(), SourceOnlyMediaResponseFactory.ProxyPrefix + id + "/");

        f.Succeed = false;
        _ = handler.Handle(regenerate!);
        await f.Service.WhenIdleAsync(f.DraftId);
        File.Delete(f.Service.PathFor(f.DraftId));
        JsonElement failed = Result(handler.Handle(get));
        Assert.AreEqual("failed", failed.GetProperty("status").GetString());
        Assert.AreEqual(ProjectProxyService.ReasonEncodeFailed, failed.GetProperty("reason").GetString());
        Assert.IsFalse(handler.Handle(get).Contains(f.Workspace, StringComparison.OrdinalIgnoreCase), "no path leaks");

        allowed = false;
        Assert.AreEqual("forbidden", Result(handler.Handle(get)).GetProperty("status").GetString());
        allowed = true;
        var unlisted = new ProxyBridgeHandler(f.Service, () => true, _ => false);
        Assert.AreEqual("unavailable", Result(unlisted.Handle(get)).GetProperty("status").GetString());
    }

    // FR-144: the proxy preview is never a reason to stop; a runner that throws only fails the proxy.
    [TestMethod]
    public async Task AThrowingProcessRunnerOnlyFailsTheProxy()
    {
        using var f = new Fixture { Throw = true };
        f.Service.Request(f.DraftId);
        await f.Service.WhenIdleAsync(f.DraftId);
        Assert.AreEqual(ProjectProxyStatus.Failed, f.Service.Get(f.DraftId).Status);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool Parse(string method, string parameters, out BridgeRequest? request) =>
        BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""",
            out request, out _);

    private static JsonElement Result(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Workspace = Path.Combine(Path.GetTempPath(), "hubradio-proxy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Workspace);
            Source = Path.Combine(Workspace, "source.mov");
            Ffmpeg = Path.Combine(Workspace, "ffmpeg.exe");
            File.WriteAllBytes(Source, [9, 8, 7, 6]);
            File.WriteAllBytes(Ffmpeg, [0]);
            DraftId = EntityId<ProjectCreationDraftIdTag>.New();
            Service = new ProjectProxyService(Workspace,
                (_, _) => Task.FromResult<ProxySourceInfo?>(NoSource ? null : new ProxySourceInfo(Source, Ffmpeg)),
                _ => Project,
                RunAsync);
        }

        internal string Workspace { get; }
        internal string Source { get; }
        internal string Ffmpeg { get; }
        internal EntityId<ProjectCreationDraftIdTag> DraftId { get; }
        internal ProjectProxyService Service { get; }
        internal EntityId<ProjectIdTag>? Project { get; set; }
        internal List<ProcessRunRequest> Calls { get; } = [];
        internal bool Succeed { get; set; } = true;
        internal bool NoSource { get; set; }
        internal bool Throw { get; set; }
        internal Task? Gate { get; set; }

        private async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            if (Throw) { throw new IOException("start failed"); }
            if (Gate is not null) { await Gate.ConfigureAwait(false); }
            if (Succeed) { File.WriteAllBytes(request.ArgumentList[^1], Mp4); }
            return new ProcessRunResult(Succeed ? 0 : 1, "", "", false, TimeSpan.Zero);
        }

        public void Dispose()
        {
            try { Directory.Delete(Workspace, recursive: true); } catch (IOException) { }
        }
    }
}
