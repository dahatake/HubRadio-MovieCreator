using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Security;
using HubRadio.Core.Settings;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyBridgeTests
{
    [TestMethod]
    public async Task ReadWorksFromDurableStoresWithoutResolvingOrStartingAPipeline()
    {
        using var f = new ControlFixture();
        int providerCalls = 0;
        SourceOnlyBridgeHandler handler = f.Handler(() => { providerCalls++; return null; });
        BridgeRequest request = Request("pipeline.read", new { projectId = f.Project.Id.ToString() });
        JsonElement snapshot = Response(request, await handler.HandleAsync(request), "available").GetProperty("snapshot");
        Assert.AreEqual(0, providerCalls);
        Assert.AreEqual(f.Execution.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual("Queued", snapshot.GetProperty("phase").GetString());
        Assert.AreEqual("none", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("versionId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("result").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("previousResult").ValueKind);
        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);

        BridgeRequest missing = Request("pipeline.read", new { projectId = EntityId<ProjectIdTag>.New().ToString() });
        _ = Response(missing, await handler.HandleAsync(missing), "notFound");
    }

    [TestMethod]
    public async Task VersionListReturnsTheRealPastVersionsWithoutAnyPathOrArtifactContent()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);

        BridgeRequest request = Request("pipeline.versions", new { projectId = stored.Request.ProjectId.ToString() });
        string raw = await HandlerFor(f).HandleAsync(request);
        JsonElement payload = Response(request, raw, "available");
        JsonElement entries = payload.GetProperty("versions");

        Assert.AreEqual(1, entries.GetArrayLength());
        JsonElement entry = entries[0];
        Assert.AreEqual(stored.VersionId!.ToString(), entry.GetProperty("versionId").GetString());
        Assert.AreEqual(stored.VersionDigest!.Hex, entry.GetProperty("versionDigest").GetString());
        Assert.AreEqual(stored.Request.ExecutionId.ToString(), entry.GetProperty("executionId").GetString());
        Assert.AreEqual(stored.Phase.ToString(), entry.GetProperty("phase").GetString());
        Assert.IsTrue(entry.GetProperty("hasResult").GetBoolean());
        Assert.IsTrue(entry.GetProperty("isCurrent").GetBoolean());
        Assert.IsGreaterThanOrEqualTo(0, payload.GetProperty("workingCopyRevision").GetInt32());

        Assert.IsFalse(raw.Contains(f.Generation.Root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(raw.Contains("storageReference", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(raw.Contains("stagingPath", StringComparison.OrdinalIgnoreCase));
        // FR-055 AC-030.1: a viewable output is identified (ID, digest, media type) only inside "outputs", so the
        // list can preview it through the revalidating media route; no other artifact reference is listed.
        Assert.AreEqual(stored.Request.CreatedAt, DateTimeOffset.Parse(entry.GetProperty("createdAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(0, entry.GetProperty("settings").GetArrayLength()); // A single video has nothing that differs.
        string[] listed = entry.GetProperty("outputs").EnumerateArray()
            .Select(static output => output.GetProperty("artifactId").GetString()!).ToArray();
        CollectionAssert.AreEquivalent(result.Artifacts.Where(static artifact => artifact.FailureCode is null
                && artifact.Artifact.Kind == ArtifactKind.Render && artifact.MediaType is "video/mp4" or "image/png")
            .Select(static artifact => artifact.ArtifactId.ToString()).ToArray(), listed);
        foreach (JsonElement output in entry.GetProperty("outputs").EnumerateArray())
        {
            CollectionAssert.AreEquivalent(new[] { "role", "artifactId", "contentDigest", "mediaType" },
                output.EnumerateObject().Select(static property => property.Name).ToArray());
        }

        // Reading the list is not a mutation.
        StoredSourceOnlyExecution after = new SourceOnlyExecutionStore(f.Generation.DatabasePath)
            .Get(stored.Request.ExecutionId)!;
        Assert.AreEqual(stored.StateDigest, after.StateDigest);
        Assert.AreEqual(stored.Revision, after.Revision);
    }

    [TestMethod]
    public async Task VersionListIsReadOnlyAndCarriesNoPathOrArtifactContent()
    {
        using var f = new ControlFixture();
        SourceOnlyBridgeHandler handler = f.Handler(() =>
            throw new AssertFailedException("A read must not resolve a pipeline."));

        BridgeRequest empty = Request("pipeline.versions", new { projectId = f.Project.Id.ToString() });
        string emptyResponse = await handler.HandleAsync(empty);
        // The fixture execution has no composition version yet.
        _ = Response(empty, emptyResponse, "notFound");

        BridgeRequest other = Request("pipeline.versions", new { projectId = EntityId<ProjectIdTag>.New().ToString() });
        _ = Response(other, await handler.HandleAsync(other), "notFound");
        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
        Assert.HasCount(1, f.Executions.ListForProject(f.Project.Id));
        Assert.IsFalse(emptyResponse.Contains(f.DatabasePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(emptyResponse.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RestoreWithoutAPipelineIsUnavailableAndNeverMutatesAcceptedWork()
    {
        using var f = new ControlFixture();
        SourceOnlyBridgeHandler handler = f.Handler(() => null);

        BridgeRequest unavailable = Request("pipeline.restore", new
        {
            projectId = f.Project.Id.ToString(),
            targetVersionId = Guid.NewGuid().ToString("D"),
            targetVersionDigest = new string('a', 64),
            expectedWorkingCopyRevision = 0,
            expectedSettingsRevision = 0,
            expectedSettingsSnapshotDigest = f.Execution.Request.SettingsSnapshot.ContentDigest.Hex,
        });
        _ = Response(unavailable, await handler.HandleAsync(unavailable), "unavailable");

        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
        Assert.HasCount(1, f.Executions.ListForProject(f.Project.Id));
    }

    [TestMethod]
    public async Task RestoreIsDeniedForAViewerAndResolvesNoPipeline()
    {
        using var f = new ControlFixture();
        const string viewer = "S-1-5-21-111-222-333-2002";
        f.Authorization.AssignRole(viewer, AuthorizationRole.Viewer, ControlFixture.Actor, "Read-only fixture actor.");
        SourceOnlyBridgeHandler handler = f.Handler(
            () => throw new AssertFailedException("Denied commands must not resolve a pipeline."),
            () => viewer);

        BridgeRequest restore = Request("pipeline.restore", new
        {
            projectId = f.Project.Id.ToString(),
            targetVersionId = Guid.NewGuid().ToString("D"),
            targetVersionDigest = new string('a', 64),
            expectedWorkingCopyRevision = 0,
            expectedSettingsRevision = 0,
            expectedSettingsSnapshotDigest = f.Execution.Request.SettingsSnapshot.ContentDigest.Hex,
        });
        _ = Response(restore, await handler.HandleAsync(restore), "forbidden");
        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    public async Task MissingPipelineReturnsUnavailableAndNeverMutatesAcceptedWork()
    {
        using var f = new ControlFixture();
        SourceOnlyBridgeHandler handler = f.Handler(() => null);
        foreach (string method in new[] { "pipeline.retry", "pipeline.cancel" })
        {
            BridgeRequest request = Command(method, f.Execution);
            _ = Response(request, await handler.HandleAsync(request), "unavailable");
        }
        BridgeRequest regenerate = Request("pipeline.regenerate", new
        {
            projectId = f.Project.Id.ToString(),
            expectedVersionId = Guid.NewGuid().ToString("D"),
            expectedVersionDigest = new string('a', 64),
            expectedSettingsRevision = 0,
            expectedSettingsSnapshotDigest = f.Execution.Request.SettingsSnapshot.ContentDigest.Hex,
        });
        _ = Response(regenerate, await handler.HandleAsync(regenerate), "unavailable");
        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
        Assert.HasCount(1, f.Executions.ListForProject(f.Project.Id));
    }

    [TestMethod]
    public async Task AuthorizationUsesCurrentNativeActorAndCannotBeBorrowedFromAcceptance()
    {
        using var f = new ControlFixture();
        const string viewer = "S-1-5-21-111-222-333-2002";
        f.Authorization.AssignRole(viewer, AuthorizationRole.Viewer, ControlFixture.Actor, "Read-only fixture actor.");
        string actor = viewer;
        SourceOnlyBridgeHandler handler = f.Handler(() => throw new AssertFailedException("Denied commands must not resolve a pipeline."), () => actor);
        BridgeRequest read = Request("pipeline.read", new { projectId = f.Project.Id.ToString() });
        _ = Response(read, await handler.HandleAsync(read), "available");
        foreach (string method in new[] { "pipeline.retry", "pipeline.cancel" })
        {
            BridgeRequest command = Command(method, f.Execution);
            _ = Response(command, await handler.HandleAsync(command), "forbidden");
        }
        actor = "S-1-5-21-111-222-333-3003";
        _ = Response(read, await handler.HandleAsync(read), "forbidden");
        Assert.AreEqual(ControlFixture.Actor, f.Execution.Request.ActorSid);
    }

    [TestMethod]
    public async Task CancellationAndUnexpectedProviderFailureDoNotLeakPrivateDetails()
    {
        using var f = new ControlFixture();
        int calls = 0;
        SourceOnlyBridgeHandler handler = f.Handler(() => { calls++; throw new IOException(@"C:\private-marker\source.mov"); });
        BridgeRequest command = Command("pipeline.retry", f.Execution);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token));
        Assert.AreEqual(0, calls);
        _ = Response(command, await handler.HandleAsync(command), "unavailable");
        Assert.AreEqual(1, calls);
        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeActorOrPermissionChangeBeforeRetryCannotStartWork(bool revokeRole)
    {
        using var f = new ControlFixture();
        int calls = 0;
        await using var pipeline = f.Pipeline((_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        _ = new JobStore(f.DatabasePath).CancelQueued(f.Execution.Request.RootJobId, 0, ControlFixture.At.AddSeconds(2));
        StoredSourceOnlyExecution failed = f.Executions.UpdateState(f.Execution.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Failed, failureCode: "source_only.test_failed");
        string actor = ControlFixture.Actor;
        SourceOnlyBridgeHandler handler = f.Handler(() =>
        {
            if (revokeRole) { f.Authorization.RevokeRole(ControlFixture.Actor, AuthorizationRole.Editor, ControlFixture.Actor); }
            else { actor = "S-1-5-21-111-222-333-4444"; }
            return pipeline;
        }, () => actor);
        BridgeRequest request = Command("pipeline.retry", failed);
        _ = Response(request, await handler.HandleAsync(request), "forbidden");
        Assert.AreEqual(0, calls);
        Assert.HasCount(1, f.Executions.ListForProject(f.Project.Id));
        Assert.AreEqual(failed.StateDigest, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    public async Task HandlerAlsoRejectsOpenParametersWhenCalledWithoutTransportParser()
    {
        using var f = new ControlFixture();
        BridgeRequest request = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.PipelineRead,
            JsonSerializer.SerializeToElement(new { projectId = f.Project.Id.ToString(), actorSid = ControlFixture.Actor }));
        SourceOnlyBridgeHandler handler = f.Handler(() => throw new AssertFailedException("Invalid request cannot start work."));
        _ = Response(request, await handler.HandleAsync(request), "invalid");
    }

    [TestMethod]
    public async Task RestoredResultIsTheCamelCaseCodecDtoWithNestedEvaluationAndExactBinding()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        BridgeRequest request = Request("pipeline.read", new { projectId = stored.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "available").GetProperty("snapshot");
        AssertSnapshotFields(snapshot);
        Assert.AreEqual(stored.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual(stored.Revision, snapshot.GetProperty("revision").GetInt32());
        Assert.AreEqual(stored.Phase.ToString(), snapshot.GetProperty("phase").GetString());
        Assert.AreEqual("valid", snapshot.GetProperty("resultStatus").GetString());
        JsonElement dto = snapshot.GetProperty("result");
        Assert.IsTrue(JsonElement.DeepEquals(dto, JsonSerializer.SerializeToElement(result.ToWebDto(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
        foreach (JsonElement artifact in dto.GetProperty("artifacts").EnumerateArray())
        {
            Assert.AreEqual(JsonValueKind.Object, artifact.GetProperty("evaluation").ValueKind);
            Assert.AreEqual("NOT EVALUATED", artifact.GetProperty("evaluation").GetProperty("aggregateStatus").GetString());
        }
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("previousResult").ValueKind);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrChangedFilesKeepDiagnosticDescriptorsButNeverAuthorizeMedia(bool missingFile)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        Assert.IsTrue((await f.Service.LoadAsync(stored)).Validation.IsValid);
        string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(result.Artifacts[0].ArtifactId);
        if (missingFile) { File.Delete(path); }
        else
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[0] ^= 1;
            File.WriteAllBytes(path, bytes);
        }
        BridgeRequest request = Request("pipeline.read", new { projectId = stored.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "invalid").GetProperty("snapshot");
        AssertSnapshotFields(snapshot);
        Assert.AreEqual("invalid", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(stored.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual(stored.Revision, snapshot.GetProperty("revision").GetInt32());
        Assert.AreEqual(stored.Phase.ToString(), snapshot.GetProperty("phase").GetString());
        Assert.IsTrue(JsonElement.DeepEquals(snapshot.GetProperty("result"), JsonSerializer.SerializeToElement(result.ToWebDto(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
        SourceOnlyMediaResponseFactory factory = MediaFactoryFor(f);
        // The intact sibling MP4/PNG outputs must also be denied: validation is set-wide.
        foreach (SourceOnlyResultArtifact artifact in result.Artifacts.Where(a => a.IsVideo || a.IsImage))
        {
            var reference = new SourceOnlyMediaAssetReference(stored.Request.ProjectId, stored.Request.ExecutionId,
                artifact.ArtifactId, artifact.ArtifactDigest);
            using PlayerMediaResponse response = await factory.CreateAsync("GET", reference.Value, null);
            Assert.AreEqual(404, response.StatusCode);
            Assert.IsNull(response.Content);
        }
        Assert.AreEqual(stored.ResultDigest, f.Executions.Get(stored.Request.ExecutionId)!.ResultDigest);
        Assert.AreEqual(stored.StateDigest, f.Executions.Get(stored.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    public async Task WriterRetainsDiagnosticSnapshotsWithoutRelabelingThemAvailableForEveryPipelineMethod()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        SourceOnlyBridgeSnapshot valid = await new SourceOnlyBridgeProjection(f.Executions, f.Service).ReadAsync(stored, CancellationToken.None);
        // Exercise envelope consistency, not a new validation/media grant.
        SourceOnlyBridgeSnapshot diagnostic = valid with { ResultStatus = "invalid" };
        foreach (BridgeMethod method in new[] { BridgeMethod.PipelineRead, BridgeMethod.PipelineRegenerate, BridgeMethod.PipelineRetry, BridgeMethod.PipelineCancel })
        {
            BridgeRequest request = BridgeRequest.Create(Guid.NewGuid(), method, JsonSerializer.SerializeToElement(new { }));
            JsonElement snapshot = Response(request, SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid, diagnostic), "invalid")
                .GetProperty("snapshot");
            AssertSnapshotFields(snapshot);
            Assert.AreEqual("invalid", snapshot.GetProperty("resultStatus").GetString());
            Assert.AreEqual(JsonValueKind.Object, snapshot.GetProperty("result").ValueKind);
            JsonElement undecodable = Response(request, SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid,
                diagnostic with { Result = null }), "invalid").GetProperty("snapshot");
            Assert.AreEqual(JsonValueKind.Null, undecodable.GetProperty("result").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, Response(request, SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid), "invalid")
                .GetProperty("snapshot").ValueKind);
            Assert.ThrowsExactly<ArgumentException>(() => SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Available, diagnostic));
            Assert.ThrowsExactly<ArgumentException>(() => SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Available));
            Assert.ThrowsExactly<ArgumentException>(() => SourceOnlyBridgeJson.WriteResponse(request, SourceOnlyBridgeStatus.Invalid, valid));
            foreach (SourceOnlyBridgeStatus denied in new[] { SourceOnlyBridgeStatus.NotFound, SourceOnlyBridgeStatus.Conflict,
                SourceOnlyBridgeStatus.Forbidden, SourceOnlyBridgeStatus.Unavailable })
                Assert.ThrowsExactly<ArgumentException>(() => SourceOnlyBridgeJson.WriteResponse(request, denied, diagnostic));
        }
    }

    [TestMethod]
    public async Task OpaqueStoredResultCannotSmuggleOpenJsonIntoTheRenderer()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        JsonObject altered = JsonNode.Parse(result.CanonicalJson)!.AsObject();
        altered["privateField"] = @"C:\private-marker\source.mov";
        _ = f.Executions.UpdateState(f.Execution.Request.ExecutionId, f.Execution.Revision, SourceOnlyExecutionPhase.Delivering,
            altered.ToJsonString(), result.VersionId, result.VersionDigest);
        BridgeRequest request = Request("pipeline.read", new { projectId = f.Execution.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "invalid").GetProperty("snapshot");
        Assert.AreEqual("invalid", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("result").ValueKind);
    }

    [TestMethod]
    public async Task InvalidResultDescriptorExcludesNativeRecoveryLocatorsEvenWhenTheyArePersisted()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        var pending = new SourceOnlyResultArtifactReference(EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([1]), 1, ArtifactKind.Evidence);
        var result = new SourceOnlyResult(evaluated.Binding, StandardArtifactGenerationDisposition.ReconciliationRequired,
            evaluated.Artifacts, evaluated.Omissions,
            [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.EvidencePromotionReconciliationRequired, evaluated.Artifacts[0].ArtifactId,
                pending, SourceOnlyResultEvidenceKind.Graph)], [pending], evaluated.TranscriptReferences, evaluated.PrimaryWorkflow,
            true, evaluated.GenerationWarnings,
            [new SourceOnlyResultPromotionAttempt(pending, evaluated.Binding.ProjectId,
                Path.Combine(f.Generation.Root, "private-marker.partial"), $"projects/{evaluated.Binding.ProjectId}/evidence/private-marker.json",
                SourceOnlyResultEvidenceKind.Graph)]);
        // Deliberately unregistered reconciliation attempt, not an approval or valid output.
        Assert.IsTrue(result.CanonicalJson.Contains("private-marker", StringComparison.Ordinal));
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        BridgeRequest request = Request("pipeline.read", new { projectId = stored.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "invalid").GetProperty("snapshot");
        Assert.AreEqual("invalid", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual("ReconciliationRequired", snapshot.GetProperty("phase").GetString());
        Assert.AreEqual(result.Artifacts.Count, snapshot.GetProperty("result").GetProperty("artifacts").GetArrayLength());
        Assert.IsFalse(snapshot.GetProperty("result").TryGetProperty("nativeReconciliationAttempts", out _));
    }

    [TestMethod]
    public async Task RegisteredDeliveryReconciliationReferenceIsDiagnosticNotDeliveryOrMediaAuthority()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        byte[] bytes = [1, 2, 3]; // Synthetic local fixture, not a completed delivery.
        string staging = Path.Combine(f.Generation.Root, "projects", f.Generation.Project.Id.ToString(), "staging", "reconciliation.partial");
        await File.WriteAllBytesAsync(staging, bytes);
        StoredArtifact artifact = await f.Generation.Store.PromoteAsync(new ArtifactPromotionRequest(EntityId<ArtifactIdTag>.New(),
            f.Generation.Project.Id, ArtifactKind.Delivery, staging, "diagnostic-delivery.mp4", ContentDigest.Compute(bytes), bytes.Length,
            SourceOnlyEvaluationFixture.Now));
        var pending = new SourceOnlyResultArtifactReference(artifact.Id, artifact.ContentDigest, artifact.ByteSize, artifact.Kind);
        var result = new SourceOnlyResult(evaluated.Binding, evaluated.GenerationDisposition, evaluated.Artifacts, evaluated.Omissions,
            evaluated.Failures, [pending], evaluated.TranscriptReferences, evaluated.PrimaryWorkflow, false, evaluated.GenerationWarnings);
        StoredSourceOnlyExecution stored = f.PersistResult(result);
        Assert.IsTrue((await f.Service.LoadAsync(stored)).Validation.IsValid);
        BridgeRequest request = Request("pipeline.read", new { projectId = stored.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "available").GetProperty("snapshot");
        Assert.AreEqual("ReconciliationRequired", snapshot.GetProperty("phase").GetString());
        Assert.AreEqual("valid", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual("Delivery", snapshot.GetProperty("result").GetProperty("reconciliationArtifacts")[0].GetProperty("kind").GetString());
        Assert.IsTrue(snapshot.GetProperty("result").GetProperty("artifacts").EnumerateArray().All(a => !a.GetProperty("delivered").GetBoolean()));
        var reference = new SourceOnlyMediaAssetReference(stored.Request.ProjectId, stored.Request.ExecutionId, artifact.Id, artifact.ContentDigest);
        using PlayerMediaResponse response = await MediaFactoryFor(f).CreateAsync("GET", reference.Value, null);
        Assert.AreEqual(404, response.StatusCode);
        Assert.IsNull(response.Content);
        Assert.AreEqual(stored.StateDigest, f.Executions.Get(stored.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    public async Task RegenerationRetainsOnlyExplicitEarlierValidatedResultWithoutGuessingCurrentBinding()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution original = f.PersistResult(result);
        StoredSourceOnlyExecution newer = AcceptSuccessor(f, result);
        BridgeRequest request = Request("pipeline.read", new { projectId = newer.Request.ProjectId.ToString() });
        SourceOnlyBridgeHandler handler = HandlerFor(f);
        JsonElement snapshot = Response(request, await handler.HandleAsync(request), "available").GetProperty("snapshot");
        Assert.AreEqual(newer.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual("none", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("result").ValueKind);
        Assert.AreEqual(original.Request.ExecutionId.ToString(), snapshot.GetProperty("previousResult").GetProperty("binding").GetProperty("executionId").GetString());
        Assert.AreEqual(result.VersionId.ToString(), snapshot.GetProperty("parentVersionId").GetString());
        Assert.AreEqual(result.VersionDigest.Hex, snapshot.GetProperty("parentDigest").GetString());
        Assert.AreEqual(result.VersionId.ToString(), snapshot.GetProperty("previousResult").GetProperty("binding").GetProperty("versionId").GetString());
        Assert.AreEqual(result.VersionDigest.Hex, snapshot.GetProperty("previousResult").GetProperty("binding").GetProperty("versionDigest").GetString());
        File.WriteAllBytes(f.Generation.Store.GetValidatedRegisteredArtifactPath(result.Artifacts[0].ArtifactId), [0]);
        JsonElement after = Response(request, await handler.HandleAsync(request), "available").GetProperty("snapshot");
        Assert.AreEqual(JsonValueKind.Null, after.GetProperty("previousResult").ValueKind);
        Assert.AreEqual(original.ResultDigest, f.Executions.Get(original.Request.ExecutionId)!.ResultDigest);
    }

    [TestMethod]
    public async Task PreviousResultSelectsExactParentInsteadOfMoreRecentValidatedResultWithSameDigest()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult original = await f.EvaluateAsync();
        StoredSourceOnlyExecution first = f.PersistResult(original);
        StoredSourceOnlyExecution second = AcceptSuccessor(f, original);
        SourceOnlyResult other = CreateStandardFailureResult(f, second);
        second = f.Executions.UpdateState(second.Request.ExecutionId, second.Revision, SourceOnlyExecutionPhase.Failed,
            other.CanonicalJson, other.VersionId, other.VersionDigest, "source_only.test_no_artifacts");
        Assert.IsTrue((await f.Service.LoadAsync(first)).Validation.IsValid);
        Assert.IsTrue((await f.Service.LoadAsync(second)).Validation.IsValid);
        Assert.AreEqual(original.VersionDigest, other.VersionDigest);
        Assert.AreNotEqual(original.VersionId, other.VersionId);
        StoredSourceOnlyExecution current = AcceptRegeneration(f, original.VersionId, original.VersionDigest, SourceOnlyEvaluationFixture.Now.AddMinutes(3));
        BridgeRequest request = Request("pipeline.read", new { projectId = current.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "available").GetProperty("snapshot");
        JsonElement previous = snapshot.GetProperty("previousResult").GetProperty("binding");
        Assert.AreEqual(current.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual("none", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(first.Request.ProjectId.ToString(), previous.GetProperty("projectId").GetString());
        Assert.AreEqual(first.Request.ExecutionId.ToString(), previous.GetProperty("executionId").GetString());
        Assert.AreEqual(current.Request.ParentVersionId!.ToString(), previous.GetProperty("versionId").GetString());
        Assert.AreEqual(current.Request.ParentDigest!.Hex, previous.GetProperty("versionDigest").GetString());
        Assert.IsTrue(first.Request.CreatedAt < current.Request.CreatedAt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrInvalidExactParentResultNeverFallsBackToOlderValidatedAncestor(bool invalidParentResult)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult original = await f.EvaluateAsync();
        StoredSourceOnlyExecution first = f.PersistResult(original);
        StoredSourceOnlyExecution parent = AcceptSuccessor(f, original);
        SourceOnlyResult parentResult = CreateStandardFailureResult(f, parent);
        JsonObject malformed = JsonNode.Parse(parentResult.CanonicalJson)!.AsObject();
        malformed["privateField"] = "private-marker";
        parent = f.Executions.UpdateState(parent.Request.ExecutionId, parent.Revision, SourceOnlyExecutionPhase.Failed,
            invalidParentResult ? malformed.ToJsonString() : null, parentResult.VersionId, parentResult.VersionDigest, "source_only.test_no_artifacts");
        Assert.IsTrue((await f.Service.LoadAsync(first)).Validation.IsValid);
        Assert.AreEqual(original.VersionDigest, parentResult.VersionDigest);
        Assert.AreNotEqual(original.VersionId, parentResult.VersionId);
        StoredSourceOnlyExecution current = AcceptRegeneration(f, parentResult.VersionId, parentResult.VersionDigest, SourceOnlyEvaluationFixture.Now.AddMinutes(3));
        BridgeRequest request = Request("pipeline.read", new { projectId = current.Request.ProjectId.ToString() });
        JsonElement snapshot = Response(request, await HandlerFor(f).HandleAsync(request), "available").GetProperty("snapshot");
        Assert.AreEqual(current.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual(parent.VersionId!.ToString(), snapshot.GetProperty("parentVersionId").GetString());
        Assert.AreEqual(parent.VersionDigest!.Hex, snapshot.GetProperty("parentDigest").GetString());
        Assert.AreEqual("none", snapshot.GetProperty("resultStatus").GetString());
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("previousResult").ValueKind);
        Assert.AreEqual(first.StateDigest, f.Executions.Get(first.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(parent.StateDigest, f.Executions.Get(parent.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    [DataRow("phase")]
    [DataRow("cleanup")]
    [DataRow("disposition")]
    [DataRow("references")]
    public async Task RegenerateRejectsUnresolvedReconciliationWithoutSideEffectsEvenWithSettledRoot(string reason)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        var result = new SourceOnlyResult(evaluated.Binding,
            reason == "disposition" ? StandardArtifactGenerationDisposition.ReconciliationRequired : evaluated.GenerationDisposition,
            evaluated.Artifacts, evaluated.Omissions, evaluated.Failures, reason == "references" ? [evaluated.Artifacts[0].Artifact] : [],
            evaluated.TranscriptReferences, evaluated.PrimaryWorkflow, reason == "cleanup", evaluated.GenerationWarnings);
        CompleteFixtureIngest(f);
        var jobs = new JobStore(f.Generation.DatabasePath);
        _ = jobs.CancelQueued(f.Execution.Request.RootJobId, 0, SourceOnlyEvaluationFixture.Now);
        StoredSourceOnlyExecution current = f.Executions.UpdateState(f.Execution.Request.ExecutionId, f.Execution.Revision,
            reason == "phase" ? SourceOnlyExecutionPhase.ReconciliationRequired : SourceOnlyExecutionPhase.Canceled,
            reason == "phase" ? null : result.CanonicalJson, result.VersionId, result.VersionDigest,
            reason == "phase" ? "source_only.reconciliation_required" : null);
        // The phase-only case has no result; the other cases have valid file/DB evidence
        // and an otherwise eligible Canceled phase. Each guard must work independently.
        if (reason == "phase") { Assert.IsNull(current.ResultJson); }
        else { Assert.IsTrue((await f.Service.LoadAsync(current)).Validation.IsValid); }
        Assert.IsEmpty(jobs.ListJobs(JobState.Queued).Where(j => j.Job.ProjectId == current.Request.ProjectId).ToArray());
        Assert.IsEmpty(jobs.ListJobs(JobState.Running).Where(j => j.Job.ProjectId == current.Request.ProjectId).ToArray());
        int stageCalls = 0;
        await using var pipeline = new SourceOnlyPipeline(f.Generation.DatabasePath, f.Generation.Root, new AssetCandidateRegistry(),
            (_, _) => { Interlocked.Increment(ref stageCalls); return Task.CompletedTask; });
        SourceOnlyBridgeHandler handler = HandlerFor(f, () => pipeline);
        var settingsStore = new DefaultSettingsStore(f.Generation.DatabasePath);
        StoredProjectSettings settings = settingsStore.GetProjectSettings(current.Request.ProjectId).Settings!;
        StoredWorkingCopy head = f.Projects.GetWorkingCopy(current.Request.ProjectId)!;
        string JobSnapshot() => JsonSerializer.Serialize(Enum.GetValues<JobState>().SelectMany(state => jobs.ListJobs(state))
            .Where(j => j.Job.ProjectId == current.Request.ProjectId).OrderBy(j => j.Job.Id.ToString(), StringComparer.Ordinal));
        string beforeJobs = JobSnapshot();
        string directory = Path.Combine(f.Generation.Root, "projects", current.Request.ProjectId.ToString());
        string leftover = Path.Combine(directory, "staging", "bridge-reconciliation.partial");
        File.WriteAllBytes(leftover, [7, 8, 9]);
        Dictionary<string, ContentDigest> beforeFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => ContentDigest.Compute(File.ReadAllBytes(path)), StringComparer.Ordinal);
        BridgeRequest request = Request("pipeline.regenerate", new
        {
            projectId = current.Request.ProjectId.ToString(),
            expectedVersionId = result.VersionId.ToString(),
            expectedVersionDigest = result.VersionDigest.Hex,
            expectedSettingsRevision = settings.Revision,
            expectedSettingsSnapshotDigest = settings.CurrentSnapshot.ContentDigest.Hex,
        });
        Assert.AreEqual(JsonValueKind.Null, Response(request, await handler.HandleAsync(request), "conflict").GetProperty("snapshot").ValueKind);
        Assert.AreEqual(0, stageCalls);
        Assert.HasCount(1, f.Executions.ListForProject(current.Request.ProjectId));
        Assert.AreEqual(current.StateDigest, f.Executions.GetLatestForProject(current.Request.ProjectId)!.StateDigest);
        Assert.AreEqual(beforeJobs, JobSnapshot());
        Assert.AreEqual(head.Revision, f.Projects.GetWorkingCopy(current.Request.ProjectId)!.Revision);
        Assert.AreEqual(head.CanonicalJson, f.Projects.GetWorkingCopy(current.Request.ProjectId)!.CanonicalJson);
        StoredProjectSettings afterSettings = settingsStore.GetProjectSettings(current.Request.ProjectId).Settings!;
        Assert.AreEqual(settings.Revision, afterSettings.Revision);
        Assert.AreEqual(settings.CurrentSnapshot.CanonicalJson, afterSettings.CurrentSnapshot.CanonicalJson);
        CollectionAssert.AreEquivalent(beforeFiles.Keys.ToArray(), Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        foreach ((string path, ContentDigest digest) in beforeFiles) { Assert.AreEqual(digest, ContentDigest.Compute(File.ReadAllBytes(path))); }
    }

    [TestMethod]
    public async Task RegenerateChecksEveryExpectedBindingThenCallsRealPipelineWithNativeActorAndCurrentSettings()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution original = f.PersistResult(result);
        CompleteFixtureIngest(f);
        var jobs = new JobStore(f.Generation.DatabasePath);
        _ = jobs.CancelQueued(original.Request.RootJobId, 0, SourceOnlyEvaluationFixture.Now);
        _ = f.Executions.UpdateState(original.Request.ExecutionId, original.Revision, SourceOnlyExecutionPhase.Canceled,
            result.CanonicalJson, result.VersionId, result.VersionDigest);
        int stageCalls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = new SourceOnlyPipeline(f.Generation.DatabasePath, f.Generation.Root, new AssetCandidateRegistry(), async (_, token) =>
        {
            Interlocked.Increment(ref stageCalls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token); // Scheduling seam, never a production render.
        });
        SourceOnlyBridgeHandler handler = HandlerFor(f, () => pipeline);
        JsonObject Parameters() => new()
        {
            ["projectId"] = original.Request.ProjectId.ToString(),
            ["expectedVersionId"] = result.VersionId.ToString(),
            ["expectedVersionDigest"] = result.VersionDigest.Hex,
            ["expectedSettingsRevision"] = 0,
            ["expectedSettingsSnapshotDigest"] = original.Request.SettingsSnapshot.ContentDigest.Hex,
        };
        foreach (string field in new[] { "expectedVersionId", "expectedVersionDigest", "expectedSettingsRevision", "expectedSettingsSnapshotDigest" })
        {
            JsonObject values = Parameters();
            values[field] = field == "expectedSettingsRevision" ? JsonValue.Create(1) :
                JsonValue.Create(field == "expectedVersionId" ? Guid.NewGuid().ToString("D") : new string('e', 64));
            BridgeRequest stale = Request("pipeline.regenerate", values);
            _ = Response(stale, await handler.HandleAsync(stale), "conflict");
        }
        Assert.AreEqual(0, stageCalls);
        Assert.HasCount(1, f.Executions.ListForProject(original.Request.ProjectId));
        BridgeRequest request = Request("pipeline.regenerate", Parameters());
        JsonElement snapshot = Response(request, await handler.HandleAsync(request), "available").GetProperty("snapshot");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, stageCalls);
        StoredSourceOnlyExecution accepted = f.Executions.GetLatestForProject(original.Request.ProjectId)!;
        Assert.AreNotEqual(original.Request.ExecutionId, accepted.Request.ExecutionId);
        Assert.AreEqual(original.Request.IngestJobId, accepted.Request.IngestJobId);
        Assert.AreEqual(original.Request.ActorSid, accepted.Request.ActorSid);
        Assert.AreEqual(result.VersionId, accepted.Request.ParentVersionId);
        Assert.AreEqual(result.VersionDigest, accepted.Request.ParentDigest);
        Assert.AreEqual(original.Request.SettingsSnapshot.CanonicalJson, accepted.Request.SettingsSnapshot.CanonicalJson);
        Assert.AreEqual(original.Request.ExecutionId.ToString(), snapshot.GetProperty("previousResult").GetProperty("binding").GetProperty("executionId").GetString());
    }

    [TestMethod]
    public async Task RetryAndCancelRejectCrossProjectAndStaleRevisionThenRetryCreatesANewExecution()
    {
        using var f = new ControlFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = f.Pipeline(async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        _ = new JobStore(f.DatabasePath).CancelQueued(f.Execution.Request.RootJobId, 0, ControlFixture.At.AddSeconds(2));
        StoredSourceOnlyExecution failed = f.Executions.UpdateState(f.Execution.Request.ExecutionId, 0,
            SourceOnlyExecutionPhase.Failed, failureCode: "source_only.test_failed");
        SourceOnlyBridgeHandler handler = f.Handler(() => pipeline);
        foreach (string method in new[] { "pipeline.retry", "pipeline.cancel" })
        {
            BridgeRequest stale = Command(method, f.Execution);
            _ = Response(stale, await handler.HandleAsync(stale), "conflict");
            BridgeRequest foreign = Request(method, new
            {
                projectId = f.Project.Id.ToString(),
                executionId = Guid.NewGuid().ToString("D"),
                expectedRevision = failed.Revision,
            });
            _ = Response(foreign, await handler.HandleAsync(foreign), "notFound");
            BridgeRequest otherProject = Request(method, new
            {
                projectId = Guid.NewGuid().ToString("D"),
                executionId = failed.Request.ExecutionId.ToString(),
                expectedRevision = failed.Revision,
            });
            _ = Response(otherProject, await handler.HandleAsync(otherProject), "notFound");
        }
        Assert.IsFalse(started.Task.IsCompleted);
        BridgeRequest retry = Command("pipeline.retry", failed);
        JsonElement snapshot = Response(retry, await handler.HandleAsync(retry), "available").GetProperty("snapshot");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        StoredSourceOnlyExecution successor = f.Executions.GetLatestForProject(f.Project.Id)!;
        Assert.AreEqual(failed.Request.ExecutionId, successor.RetryOfExecutionId);
        Assert.AreEqual(ControlFixture.Actor, successor.Request.ActorSid);
        Assert.AreEqual(successor.Request.ExecutionId.ToString(), snapshot.GetProperty("executionId").GetString());
        Assert.AreEqual(failed.StateDigest, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
        _ = Response(retry, await handler.HandleAsync(retry), "conflict");
    }

    [TestMethod]
    public async Task CancelAwaitsRealOwnedCleanupAndReturnsOnlyThePersistedOutcome()
    {
        using var f = new ControlFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = f.Pipeline(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                signaled.TrySetResult();
                await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        });
        Task<SourceOnlyPipelineRun> running = pipeline.Start(f.Execution.Request.ExecutionId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BridgeRequest request = Command("pipeline.cancel", f.Execution);
        Task<string> pending = f.Handler(() => pipeline).HandleAsync(request);
        try
        {
            await signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(pending.IsCompleted);
        }
        finally { cleanup.TrySetResult(); }
        JsonElement snapshot = Response(request, await pending, "available").GetProperty("snapshot");
        SourceOnlyPipelineRun outcome = await running;
        Assert.AreEqual("Canceled", snapshot.GetProperty("phase").GetString());
        Assert.AreEqual(outcome.Execution!.Revision, snapshot.GetProperty("revision").GetInt32());
        Assert.AreEqual(outcome.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    public void SchemaRetainsInvalidDiagnosticSnapshotsAndDeliveryReconciliationReferences()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(RepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = schema.RootElement.GetProperty("$defs");
        JsonElement invalid = definitions.GetProperty("sourceOnlyBridgeResult").GetProperty("oneOf")[2].GetProperty("properties");
        Assert.AreEqual("invalid", invalid.GetProperty("status").GetProperty("const").GetString());
        JsonElement snapshots = invalid.GetProperty("snapshot").GetProperty("oneOf");
        Assert.AreEqual("null", snapshots[0].GetProperty("type").GetString());
        Assert.AreEqual("#/$defs/sourceOnlySnapshot", snapshots[1].GetProperty("$ref").GetString());
        Assert.AreEqual("invalid", snapshots[1].GetProperty("properties").GetProperty("resultStatus").GetProperty("const").GetString());
        JsonElement result = definitions.GetProperty("sourceOnlySnapshot").GetProperty("properties").GetProperty("result").GetProperty("oneOf");
        Assert.AreEqual("null", result[0].GetProperty("type").GetString());
        Assert.AreEqual("#/$defs/sourceOnlyResult", result[1].GetProperty("$ref").GetString());
        JsonElement kinds = definitions.GetProperty("sourceOnlyResult").GetProperty("properties").GetProperty("reconciliationArtifacts")
            .GetProperty("items").GetProperty("properties").GetProperty("kind").GetProperty("enum");
        CollectionAssert.AreEquivalent(new[] { "Render", "Evidence", "Delivery" }, kinds.EnumerateArray().Select(k => k.GetString()!).ToArray());
    }

    internal static SourceOnlyBridgeHandler HandlerFor(SourceOnlyEvaluationFixture f, Func<SourceOnlyPipeline?>? provider = null) =>
        new(provider ?? (() => null), new SourceOnlyExecutionStore(f.Generation.DatabasePath),
            new DefaultSettingsStore(f.Generation.DatabasePath), f.Service, f.Projects,
            () => f.Execution.Request.ActorSid, AuthorizationFor(f), new FixedClock(SourceOnlyEvaluationFixture.Now.AddMinutes(2)));

    internal static AuthorizationService AuthorizationFor(SourceOnlyEvaluationFixture f)
    {
        var service = new AuthorizationService(new RoleAssignmentStore(f.Generation.DatabasePath));
        _ = service.BootstrapInitialRoles(f.Execution.Request.ActorSid);
        return service;
    }

    internal static StoredSourceOnlyExecution AcceptSuccessor(SourceOnlyEvaluationFixture f, SourceOnlyResult result)
    {
        CompleteFixtureIngest(f);
        return AcceptRegeneration(f, result.VersionId, result.VersionDigest, SourceOnlyEvaluationFixture.Now.AddMinutes(1));
    }

    private static StoredSourceOnlyExecution AcceptRegeneration(SourceOnlyEvaluationFixture f,
        EntityId<CompositionVersionIdTag> parentId, ContentDigest parentDigest, DateTimeOffset createdAt) =>
        f.Executions.CreateRegeneration(f.Execution.Request with
        {
            ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New(),
            RootJobId = EntityId<JobIdTag>.New(),
            ParentVersionId = parentId,
            ParentDigest = parentDigest,
            CreatedAt = createdAt,
        }, 0);

    private static SourceOnlyResult CreateStandardFailureResult(SourceOnlyEvaluationFixture f, StoredSourceOnlyExecution execution)
    {
        // Persist a genuine standard-composition checkpoint but no rendered outputs.
        // A valid failed-result descriptor is not successful generation or media proof.
        StandardGenerationFixture g = f.Generation;
        WorkingCopyState head = g.Versions.GetCurrentState(g.Project.Id)!;
        CompositionVersion parent = g.Versions.GetVersion(head.WorkingCopy.BaseVersionId!)!;
        var profile = g.Preparation.Plan.LongForm.Profile;
        var template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), profile.TemplateReference, BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
        LongFormElementInput Omitted(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(), kind, false, null, null, [], []);
        var longForm = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), g.Project,
            g.Preparation.Plan.LongForm.Provenance, template, profile, LongFormDestination.InternalDistribution, null,
            Omitted(LongFormElementKind.PreRollMargin), Omitted(LongFormElementKind.Opening), Omitted(LongFormElementKind.ProgramName),
            Omitted(LongFormElementKind.EpisodeNumber), Omitted(LongFormElementKind.EpisodeTitle), Omitted(LongFormElementKind.PerformerIntroduction),
            Omitted(LongFormElementKind.PublicationScopeIndicator), Omitted(LongFormElementKind.Body), [], Omitted(LongFormElementKind.Summary),
            [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omitted(LongFormElementKind.CallToAction))],
            Omitted(LongFormElementKind.Ending), Omitted(LongFormElementKind.PostRollMargin));
        StoredProject project = f.Projects.GetProject(g.Project.Id)!;
        StandardCompositionApplication applied = StandardCompositionOrchestrator.Apply(g.Versions, new StandardCompositionOrchestrationRequest(
            parent, head.WorkingCopy, head.Revision, new StandardProjectStoreState(project.Revision,
                ContentDigest.Compute(Encoding.UTF8.GetBytes(project.CanonicalJson)), project.UpdatedAt), g.SettingsProfile, g.Settings,
            File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")),
            File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json")), longForm,
            EntityId<TimelineMapIdTag>.New(), g.Geometry, new RationalNumber(30, 1), g.ToolEvidence.ExecutableDigest, g.Model.WeightsDigest,
            new MusicPlanningService().Plan(MusicCatalog.CreateUnavailable(MusicLibraryState.Absent, MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default),
            [], [], [], execution.Request.ActorSid, execution.Request.CreatedAt, "Persist a distinct standard checkpoint for parent-selection tests."));
        CompositionVersion version = applied.Checkpoint.Version;
        return new SourceOnlyResult(new SourceOnlyResultBinding(execution.Request.ProjectId, execution.Request.ExecutionId,
            execution.Request.RootJobId, execution.Request.SettingsSnapshot.ContentDigest, version.Id, version.ContentDigest),
            StandardArtifactGenerationDisposition.Failed, [], [], [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.NoArtifacts)], [], [], null, false);
    }

    private static SourceOnlyMediaResponseFactory MediaFactoryFor(SourceOnlyEvaluationFixture f) =>
        new(f.Executions, f.Service, f.Generation.Store, () => f.Execution.Request.ActorSid, AuthorizationFor(f));

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) { directory = directory.Parent; }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CompleteFixtureIngest(SourceOnlyEvaluationFixture f)
    {
        // Synthetic fixture job records only; no media ingest is claimed here.
        var jobs = new JobStore(f.Generation.DatabasePath);
        SourceOnlyExecutionRequest request = f.Execution.Request;
        DateTimeOffset start = request.CreatedAt.AddTicks(1), end = request.CreatedAt.AddTicks(2);
        var id = EntityId<JobAttemptIdTag>.New();
        _ = jobs.StartAttempt(request.IngestJobId, 0, new JobAttempt(id, request.IngestJobId, 1, JobState.Running, request.CreatedAt, start, null, null), start);
        _ = jobs.CompleteAttempt(request.IngestJobId, 1, new JobAttempt(id, request.IngestJobId, 1, JobState.Succeeded, request.CreatedAt, start, end, null), end);
    }

    internal static BridgeRequest Request(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new { version = 1, requestId = Guid.NewGuid().ToString("D"), method, @params = parameters });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
        Assert.IsNotNull(request);
        return request;
    }

    private static BridgeRequest Command(string method, StoredSourceOnlyExecution execution) => Request(method, new
    {
        projectId = execution.Request.ProjectId.ToString(),
        executionId = execution.Request.ExecutionId.ToString(),
        expectedRevision = execution.Revision,
    });

    internal static JsonElement Response(BridgeRequest request, string json, string status)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(request.RequestId.ToString("D"), root.GetProperty("requestId").GetString());
        Assert.AreEqual(BridgeProtocol.GetMethodName(request.Method), root.GetProperty("method").GetString());
        JsonElement result = root.GetProperty("result");
        CollectionAssert.AreEqual(
            request.Method == BridgeMethod.PipelineVersions
                ? ["status", "workingCopyRevision", "versions"]
                : new[] { "status", "snapshot" },
            result.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        foreach (string forbidden in new[] { "nativeReconciliationAttempts", "storageReference", "stagingPath", "actorSid", "requestJson", "private-marker" })
            Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(json) <= BridgeProtocol.MaxSourceOnlyResponseUtf8Bytes);
        return result.Clone();
    }

    private static void AssertSnapshotFields(JsonElement snapshot) => CollectionAssert.AreEqual(new[]
    {
        "projectId", "executionId", "revision", "phase", "rootJobId", "ingestJobId", "settingsSnapshotDigest", "versionId", "versionDigest",
        "parentVersionId", "parentDigest", "resultStatus", "result", "previousResult", "failureCode",
    }, snapshot.EnumerateObject().Select(p => p.Name).ToArray());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControlFixture : IDisposable
    {
        internal const string Actor = "S-1-5-21-111-222-333-1001";
        internal static readonly DateTimeOffset At = new(2042, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.SourceOnlyBridgeTests", Guid.NewGuid().ToString("N"));
        internal string DatabasePath { get; }
        internal Project Project { get; }
        internal ProjectStore Projects { get; }
        internal SourceOnlyExecutionStore Executions { get; }
        internal DefaultSettingsStore Settings { get; }
        internal SourceOnlyResultService Results { get; }
        internal AuthorizationService Authorization { get; }
        internal StoredSourceOnlyExecution Execution { get; }

        internal ControlFixture()
        {
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "fixture.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(Root, "backups"));
            Projects = new(DatabasePath); Executions = new(DatabasePath); Settings = new(DatabasePath);
            var profile = DefaultSettingsProfile.Create(EntityId<DefaultSettingsProfileIdTag>.New(), "control-fixture", new(1, 0, 0),
                [new DefaultSettingDefinition("editing.enabled", DefaultSettingGroup.Editing, false, "Editing", "Control fixture", DefaultSettingUnit.None,
                    DefaultSettingValue.FromBoolean(true), null, null, [], [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")])]);
            _ = Settings.RegisterProfile(Encoding.UTF8.GetBytes(profile.CanonicalJson));
            ContentDigest digest = ContentDigest.Compute([1]);
            var draft = new ProjectCreationDraft(EntityId<ProjectCreationDraftIdTag>.New(), EntityId<AssetIdTag>.New(), "Control fixture", "Control", "Internal",
                PublicationScope.InternalOnly, [new ProjectOutputProfileReference(EntityId<RenderProfileIdTag>.New(), "test", digest)],
                "ja-JP", ["Host"], new ProjectDeadlineSelection(null), new TemplateReference(EntityId<TemplateIdTag>.New(), "test", digest));
            Project = new(EntityId<ProjectIdTag>.New(), draft.ContentName!, draft.Purpose!, draft.Audience!, PublicationScope.InternalOnly, null, [], []);
            var drafts = new ProjectCreationDraftStore(DatabasePath);
            _ = drafts.CreateDraft(draft, "{\"fixture\":true}", At);
            var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), Project.Id, draft.Id, draft.SourceCandidateId!,
                EntityId<JobIdTag>.New(), EntityId<JobIdTag>.New(), profile.CreateSnapshot(), Actor, At.AddSeconds(1));
            Execution = drafts.FinalizeSourceOnlyWithExecution(draft, Project, "{\"fixture\":false}", 0, request.CreatedAt, request);
            Results = new(new ArtifactStore(DatabasePath, Root), new AutomatedEvaluationStore(DatabasePath), new QuickStartWorkflowStore(DatabasePath), Projects);
            Authorization = new(new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(Authorization.BootstrapInitialRoles(Actor));
        }

        internal SourceOnlyBridgeHandler Handler(Func<SourceOnlyPipeline?> provider, Func<string>? actor = null) =>
            new(provider, Executions, Settings, Results, Projects, actor ?? (() => Actor), Authorization, new FixedClock(At.AddMinutes(3)));

        internal SourceOnlyPipeline Pipeline(Func<StoredSourceOnlyExecution, CancellationToken, Task> stage) =>
            new(DatabasePath, Root, new AssetCandidateRegistry(), stage, new FixedClock(At.AddMinutes(3)));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}