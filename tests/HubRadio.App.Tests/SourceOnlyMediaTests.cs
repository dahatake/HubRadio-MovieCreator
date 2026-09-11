using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyMediaTests
{
    [TestMethod]
    public void ReferenceIsExactlyTheBoundedIdOnlyReservedHttpsUrl()
    {
        var reference = new SourceOnlyMediaAssetReference(
            EntityId<ProjectIdTag>.Parse("11111111-1111-4111-8111-111111111111"),
            EntityId<SourceOnlyExecutionIdTag>.Parse("22222222-2222-4222-8222-222222222222"),
            EntityId<ArtifactIdTag>.Parse("abcdefab-cdef-4abc-8def-abcdefabcdef"), new ContentDigest(new string('a', 64)));
        Assert.AreEqual(226, reference.Value.Length);
        Assert.AreEqual(SourceOnlyMediaAssetReference.CanonicalUriLength, reference.Value.Length);
        Assert.AreEqual($"https://player-media.appassets.example/source-only/{reference.ProjectId}/{reference.ExecutionId}/{reference.ArtifactId}/{reference.ContentDigest.Hex}", reference.Value);
        Assert.IsTrue(SourceOnlyMediaAssetReference.TryParse(reference.Value, out SourceOnlyMediaAssetReference? parsed));
        Assert.AreEqual(reference.Value, parsed!.Value);
        Assert.IsTrue(WebViewHost.IsAllowedMediaAssetUri(reference.Value));
        Assert.IsFalse(WebViewHost.IsAllowedResourceUri(reference.Value));
        Assert.IsFalse(WebViewHost.IsAllowedOrigin(reference.Value));

        string[] rejected =
        [
            reference.Value.Replace("https://", "http://", StringComparison.Ordinal),
            reference.Value.Replace("player-media", "PLAYER-MEDIA", StringComparison.Ordinal),
            reference.Value.Replace(WebViewHost.MediaHostName, "user@" + WebViewHost.MediaHostName, StringComparison.Ordinal),
            reference.Value.Replace(WebViewHost.MediaHostName, WebViewHost.MediaHostName + ":443", StringComparison.Ordinal),
            reference.Value.Replace(WebViewHost.MediaHostName, WebViewHost.MediaHostName + ".evil.test", StringComparison.Ordinal),
            reference.Value.Replace(WebViewHost.MediaHostName, WebViewHost.VirtualHostName, StringComparison.Ordinal),
            reference.Value.Replace(reference.ArtifactId.ToString(), reference.ArtifactId.ToString().ToUpperInvariant(), StringComparison.Ordinal),
            reference.Value.Replace(reference.ContentDigest.Hex, reference.ContentDigest.Hex.ToUpperInvariant(), StringComparison.Ordinal),
            reference.Value.Replace("/source-only/", "/source-only/./", StringComparison.Ordinal),
            reference.Value.Replace("/source-only/", "/source-only/%2e%2e/", StringComparison.Ordinal),
            reference.Value.Replace("/source-only/", "/source-only%2f", StringComparison.Ordinal),
            reference.Value.Replace("/source-only/", "/source-only\\", StringComparison.Ordinal),
            reference.Value.Replace(reference.ProjectId.ToString(), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            reference.Value.Replace(reference.ExecutionId.ToString(), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            reference.Value.Replace(reference.ArtifactId.ToString(), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            reference.Value + "?path=C:/private-marker", reference.Value + "#fragment", reference.Value + "/", reference.Value + "\n",
            " " + reference.Value, reference.Value[..^1], reference.Value + new string('a', 4096),
            @"C:\private-marker\output.mp4", "file:///C:/private-marker/output.mp4", "",
        ];
        foreach (string uri in rejected)
        {
            Assert.IsFalse(SourceOnlyMediaAssetReference.TryParse(uri, out _), uri);
            Assert.IsFalse(WebViewHost.IsAllowedMediaAssetUri(uri), uri);
        }
    }

    [TestMethod]
    public async Task ActualStoredOutputsUseHeldValidatedReadOnlyStreamsForMp4AndPng()
    {
        // Real files/stores/result validation; the existing generator/QC fixture is
        // synthetic. This test does not claim playback or real FFmpeg execution.
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyMediaResponseFactory factory = Factory(f);
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts.Where(a => a.IsVideo || a.IsImage))
        {
            string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(artifact.ArtifactId);
            byte[] expected = await File.ReadAllBytesAsync(path);
            using PlayerMediaResponse response = await factory.CreateAsync("GET", Reference(stored, artifact).Value, null);
            Assert.AreEqual(200, response.StatusCode);
            Header(response, "Content-Type: " + artifact.MediaType);
            Header(response, "Content-Length: " + expected.LongLength);
            Header(response, "Accept-Ranges: bytes");
            Header(response, "Cache-Control: no-store");
            Header(response, "X-Content-Type-Options: nosniff");
            Header(response, "Cross-Origin-Resource-Policy: same-site");
            Stream content = response.Content!;
            Assert.IsNotNull(content);
            Assert.IsTrue(content.CanRead);
            Assert.IsTrue(content.CanSeek);
            Assert.IsFalse(content.CanWrite);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllBytes(path, expected));
            byte[] actual = new byte[expected.Length];
            await content.ReadExactlyAsync(actual);
            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(-1, content.ReadByte());
            response.Dispose();
            Assert.IsFalse(content.CanRead);
        }
    }

    [TestMethod]
    public async Task RangeResponsesReuseSingleRangeRulesAndKeepSeekingInsideTheResponse()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact artifact = result.Artifacts.First(a => a.IsVideo);
        byte[] expected = await File.ReadAllBytesAsync(f.Generation.Store.GetValidatedRegisteredArtifactPath(artifact.ArtifactId));
        SourceOnlyMediaResponseFactory factory = Factory(f);
        foreach ((string range, int start, int length) in new[]
        {
            ("bytes=2-5", 2, 4), ("bytes=6-", 6, expected.Length - 6),
            ("bytes=-3", expected.Length - 3, 3), ("bytes=0-999999", 0, expected.Length),
        })
        {
            using PlayerMediaResponse response = await factory.CreateAsync("GET", Reference(stored, artifact).Value, range);
            Assert.AreEqual(206, response.StatusCode);
            Header(response, $"Content-Range: bytes {start}-{start + length - 1}/{expected.Length}");
            Header(response, $"Content-Length: {length}");
            Stream content = response.Content!;
            byte[] actual = new byte[length];
            await content.ReadExactlyAsync(actual);
            CollectionAssert.AreEqual(expected[start..(start + length)], actual);
            Assert.AreEqual(-1, content.ReadByte());
            Assert.ThrowsExactly<IOException>(() => content.Seek(1, SeekOrigin.End));
            Assert.ThrowsExactly<IOException>(() => content.Seek(-1, SeekOrigin.Begin));
            Assert.AreEqual(0L, content.Seek(0, SeekOrigin.Begin));
            Assert.AreEqual(expected[start], content.ReadByte());
        }

        foreach (string range in new[] { "", "bytes=", "bytes=0-1,3-4", "bytes=8-7", "bytes=-0", "bytes=9223372036854775808-", "bytes= 0-1", new string('0', 129) })
        {
            using PlayerMediaResponse response = await factory.CreateAsync("GET", Reference(stored, artifact).Value, range);
            Assert.AreEqual(416, response.StatusCode, range);
            Assert.IsNull(response.Content);
            Header(response, $"Content-Range: bytes */{expected.Length}");
        }
    }

    [TestMethod]
    public async Task OnlyAnOutputListedInTheExactExecutionMayBeReadNotSourcesEvidenceOrSidecars()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact artifact = result.Artifacts[0];
        SourceOnlyMediaAssetReference valid = Reference(stored, artifact);
        SourceOnlyMediaResponseFactory factory = Factory(f);
        foreach (string uri in new[]
        {
            new SourceOnlyMediaAssetReference(EntityId<ProjectIdTag>.New(), valid.ExecutionId, valid.ArtifactId, valid.ContentDigest).Value,
            new SourceOnlyMediaAssetReference(valid.ProjectId, EntityId<SourceOnlyExecutionIdTag>.New(), valid.ArtifactId, valid.ContentDigest).Value,
            new SourceOnlyMediaAssetReference(valid.ProjectId, valid.ExecutionId, EntityId<ArtifactIdTag>.New(), valid.ContentDigest).Value,
            new SourceOnlyMediaAssetReference(valid.ProjectId, valid.ExecutionId, valid.ArtifactId, new ContentDigest(new string('f', 64))).Value,
            new SourceOnlyMediaAssetReference(valid.ProjectId, valid.ExecutionId, artifact.Source.ArtifactId, artifact.Source.ContentDigest).Value,
            new SourceOnlyMediaAssetReference(valid.ProjectId, valid.ExecutionId, artifact.Evidence[0].Artifact.ArtifactId, artifact.Evidence[0].Artifact.ContentDigest).Value,
            Reference(stored, result.Artifacts.First(a => !a.IsVideo && !a.IsImage)).Value,
            valid.Value + "?approved=true", valid.Value + "/../private-marker",
        })
        {
            using PlayerMediaResponse response = await factory.CreateAsync("GET", uri, null);
            Denied(response);
        }
        using PlayerMediaResponse method = await factory.CreateAsync("HEAD", valid.Value, null);
        Assert.AreEqual(405, method.StatusCode);
        Assert.IsNull(method.Content);
        Header(method, "Allow: GET");
    }

    [TestMethod]
    public async Task EarlierResultRemainsReadableDuringRegenerationButCannotBeReboundToTheNewExecution()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution original = f.PersistResult(result);
        StoredSourceOnlyExecution successor = SourceOnlyBridgeTests.AcceptSuccessor(f, result);
        SourceOnlyMediaResponseFactory factory = Factory(f);
        using PlayerMediaResponse previous = await factory.CreateAsync("GET", Reference(original, result.Artifacts[0]).Value, null);
        Assert.AreEqual(200, previous.StatusCode);
        using PlayerMediaResponse rebound = await factory.CreateAsync("GET", Reference(successor, result.Artifacts[0]).Value, null);
        Denied(rebound);
    }

    [TestMethod]
    public async Task ChangedSiblingFileBlocksMediaEvenWhenRequestedFileStillMatchesItsDigest()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        File.WriteAllBytes(f.Generation.Store.GetValidatedRegisteredArtifactPath(result.Artifacts[1].ArtifactId), [0]);
        using PlayerMediaResponse response = await Factory(f).CreateAsync("GET", Reference(stored, result.Artifacts[0]).Value, null);
        Denied(response);
        BridgeRequest request = SourceOnlyBridgeTests.Request("pipeline.read", new { projectId = stored.Request.ProjectId.ToString() });
        JsonElement snapshot = SourceOnlyBridgeTests.Response(request, await SourceOnlyBridgeTests.HandlerFor(f).HandleAsync(request), "invalid")
            .GetProperty("snapshot");
        Assert.AreEqual("invalid", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(result.Artifacts.Count, snapshot.GetProperty("result").GetProperty("artifacts").GetArrayLength());
    }

    [TestMethod]
    public async Task ChangedDatabaseEvidenceBlocksMediaWithoutChangingTheStoredResult()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        StoredProject project = f.Projects.GetProject(stored.Request.ProjectId)!;
        _ = f.Projects.UpdateProject(f.Generation.Project, project.CanonicalJson, project.Revision, SourceOnlyEvaluationFixture.Now.AddMinutes(1));
        using PlayerMediaResponse response = await Factory(f).CreateAsync("GET", Reference(stored, result.Artifacts[0]).Value, null);
        Denied(response);
        Assert.AreEqual(result.CanonicalDigest, f.Executions.Get(stored.Request.ExecutionId)!.ResultDigest);
    }

    [TestMethod]
    public async Task AReplacementCannotChangeAnAlreadyHeldResponseAndANewRequestRevalidates()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyResultArtifact artifact = result.Artifacts[0];
        SourceOnlyMediaResponseFactory factory = Factory(f);
        string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(artifact.ArtifactId);
        byte[] expected = await File.ReadAllBytesAsync(path);
        using PlayerMediaResponse response = await factory.CreateAsync("GET", Reference(stored, artifact).Value, null);
        Assert.AreEqual(200, response.StatusCode);
        File.Move(path, path + ".old");
        File.WriteAllBytes(path, [1, 2, 3]);
        byte[] actual = new byte[expected.Length];
        await response.Content!.ReadExactlyAsync(actual);
        CollectionAssert.AreEqual(expected, actual);
        using PlayerMediaResponse next = await factory.CreateAsync("GET", Reference(stored, artifact).Value, null);
        Denied(next);
        Stream detached = response.DetachContent()!;
        response.Dispose();
        Assert.IsTrue(detached.CanRead);
        detached.Dispose();
        Assert.IsFalse(detached.CanRead);
    }

    [TestMethod]
    public async Task AuthorizationIsRecheckedForEveryRequestAndAfterTheHeldRead()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        string actor = f.Execution.Request.ActorSid;
        const string stranger = "S-1-5-21-111-222-333-4444";
        SourceOnlyMediaResponseFactory factory = Factory(f, () => actor);
        string uri = Reference(stored, result.Artifacts[0]).Value;
        using (PlayerMediaResponse allowed = await factory.CreateAsync("GET", uri, null)) { Assert.AreEqual(200, allowed.StatusCode); }
        actor = stranger;
        using (PlayerMediaResponse denied = await factory.CreateAsync("GET", uri, null)) { Denied(denied); }
        int calls = 0;
        factory = Factory(f, () => ++calls == 1 ? f.Execution.Request.ActorSid : stranger);
        using PlayerMediaResponse changedActor = await factory.CreateAsync("GET", uri, null);
        Denied(changedActor);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task PreCanceledRequestAndPrivateActorFailureNeverExposeDetails()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        int calls = 0;
        SourceOnlyMediaResponseFactory factory = Factory(f, () => { calls++; throw new IOException(@"C:\private-marker\actor"); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => factory.CreateAsync("GET", null, null, cancellation.Token));
        Assert.AreEqual(0, calls);
        using PlayerMediaResponse response = await factory.CreateAsync("GET", null, null);
        Denied(response);
        Assert.AreEqual(1, calls);
    }

    private static SourceOnlyMediaResponseFactory Factory(SourceOnlyEvaluationFixture f, Func<string>? actor = null) =>
        new(new SourceOnlyExecutionStore(f.Generation.DatabasePath), f.Service, new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root),
            actor ?? (() => f.Execution.Request.ActorSid), SourceOnlyBridgeTests.AuthorizationFor(f));

    private static SourceOnlyMediaAssetReference Reference(StoredSourceOnlyExecution execution, SourceOnlyResultArtifact artifact) =>
        new(execution.Request.ProjectId, execution.Request.ExecutionId, artifact.ArtifactId, artifact.ArtifactDigest);

    private static void Header(PlayerMediaResponse response, string header) => CollectionAssert.Contains(response.Headers.Split("\r\n"), header);

    private static void Denied(PlayerMediaResponse response)
    {
        Assert.AreEqual(404, response.StatusCode);
        Assert.IsNull(response.Content);
        Header(response, "Content-Length: 0");
        Assert.IsFalse(response.Headers.Contains("private-marker", StringComparison.Ordinal));
        Assert.IsFalse(response.Headers.Contains("storageReference", StringComparison.OrdinalIgnoreCase));
    }
}