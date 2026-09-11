using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.Subtitles;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using ProjectSnapshotJson = HubRadio.App.Policy.ProjectSnapshotJson;

namespace HubRadio.App.Tests;

/// <summary>
/// The saved-head wire contract over actual schema-012 transactions and native
/// subtitle evaluation. Fixtures are isolated; no worker, pipeline, or UI runs.
/// </summary>
[TestClass]
public sealed class DurableLayoutContractTests
{
    private const string Actor = "S-1-5-21-1111111111-2222222222-3333333333-8101";

    [TestMethod]
    public void GeneratedRootDiscoveryHasZeroSequenceAndAnExplicitNullJournalDigest()
    {
        using var fixture = new Fixture();
        BridgeRequest read = Read(fixture.Initial, latest: true);
        LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(read, Actor, CancellationToken.None);
        Assert.AreSame(fixture.Initial, Available(result));
        AssertReceipt(read, result, fixture.Initial, fixture.Initial, null);
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreSame(fixture.Initial, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    public void ExactHistoricalReadNeverMovesButLatestReloadFromRootOrKnownChildFindsTheSavedHead()
    {
        using var fixture = new Fixture();
        var states = new List<LayoutSubtitleBridgeSnapshot> { fixture.Initial };
        ContentDigest initialDigest = DurableSubtitleEditService.ComputeSnapshotDigest(fixture.Initial);
        string initialWire = SyntheticWire(fixture.Initial);
        foreach (int cue in new[] { 0, 1, 0 })
        {
            LayoutSubtitleBridgeSnapshot parent = states[^1];
            BridgeRequest edit = Edit(parent, cue, $"Saved correction {states.Count}");
            LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(edit, Actor, CancellationToken.None);
            LayoutSubtitleBridgeSnapshot child = Available(result);
            Assert.AreSame(parent.Subtitles[1 - cue], child.Subtitles[1 - cue]);
            Assert.AreSame(fixture.KnownVersion, child.GetComposition(fixture.KnownVersion.Id));
            AssertReceipt(edit, result, fixture.Initial, child, fixture.Rows[^1]);
            Assert.AreEqual(fixture.Rows[^1].ResultSnapshotDigest, DurableSubtitleEditService.ComputeSnapshotDigest(child));
            states.Add(child);
        }

        LayoutSubtitleBridgeSnapshot head = states[^1];
        StoredSubtitleEdit last = fixture.Rows[^1];
        foreach (LayoutSubtitleBridgeSnapshot known in states)
        {
            BridgeRequest exact = Read(known);
            LayoutSubtitleBridgeOperationResult historical = fixture.Service.Execute(exact, Actor, CancellationToken.None);
            Assert.AreSame(known, Available(historical), "Omitting latestInBranch must preserve the exact version and object.");
            AssertReceipt(exact, historical, fixture.Initial, head, last);
            BridgeRequest latest = Read(known, latest: true);
            LayoutSubtitleBridgeOperationResult discovered = fixture.Service.Execute(latest, Actor, CancellationToken.None);
            Assert.AreSame(head, Available(discovered));
            AssertReceipt(latest, discovered, fixture.Initial, head, last);
        }

        DurableSubtitleEditService restarted = fixture.Restart();
        foreach (LayoutSubtitleBridgeSnapshot known in states)
        {
            BridgeRequest latest = Read(known, latest: true);
            LayoutSubtitleBridgeOperationResult discovered = restarted.Execute(latest, Actor, CancellationToken.None);
            Assert.AreEqual(head.CompositionVersion.Id, Available(discovered).CompositionVersion.Id);
            AssertReceipt(latest, discovered, fixture.Initial, head, last);
            Assert.AreEqual(last.ResultSnapshotDigest, DurableSubtitleEditService.ComputeSnapshotDigest(discovered.Snapshot!));
            Assert.AreSame(fixture.KnownVersion, discovered.Snapshot!.GetComposition(fixture.KnownVersion.Id));
            CollectionAssert.AreEquivalent(head.CompositionVersions.Keys.ToArray(), discovered.Snapshot.CompositionVersions.Keys.ToArray());
            Assert.AreEqual(known.CompositionVersion.Id, Available(restarted.Execute(Read(known), Actor, CancellationToken.None)).CompositionVersion.Id);
        }
        Assert.AreEqual(initialDigest, DurableSubtitleEditService.ComputeSnapshotDigest(fixture.Initial));
        Assert.AreEqual(initialWire, SyntheticWire(fixture.Initial), "Transient head metadata must not alter snapshot-v1 hashing bytes.");
        Assert.AreEqual(3, fixture.Rows.Count);
        Assert.AreEqual(3, head.Subtitles[0].History.Revisions.Count);
        Assert.AreEqual(2, head.Subtitles[1].History.Revisions.Count);
        Assert.AreEqual(1, head.Subtitles[0].History.Inheritance.Count);
    }

    [TestMethod]
    public void TwoGeneratedRootsKeepTheirOwnHeadsAndNeverChangeTheDefaultByReading()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot secondRoot = fixture.RegisterSecondRoot();
        LayoutSubtitleBridgeSnapshot firstHead = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "First branch"), Actor, CancellationToken.None));
        LayoutSubtitleBridgeSnapshot secondHead = Available(fixture.Service.Execute(Edit(secondRoot, 1, "Second branch"), Actor, CancellationToken.None));
        foreach (LayoutSubtitleBridgeSnapshot pinned in new[] { secondRoot, secondHead, fixture.Initial, firstHead, secondRoot })
        {
            bool second = pinned == secondRoot || pinned == secondHead;
            LayoutSubtitleBridgeSnapshot root = second ? secondRoot : fixture.Initial;
            LayoutSubtitleBridgeSnapshot head = second ? secondHead : firstHead;
            BridgeRequest read = Read(pinned, latest: true);
            LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(read, Actor, CancellationToken.None);
            Assert.AreSame(head, Available(result));
            AssertReceipt(read, result, root, head, fixture.Store.ReadBranch(root.ProjectId, root.CompositionVersion.Id).Single());
            Assert.AreSame(firstHead, Available(fixture.Service.Execute(DefaultRead(fixture.Initial), Actor, CancellationToken.None)));
        }
        Assert.AreSame(secondRoot, Available(fixture.Service.Execute(Read(secondRoot), Actor, CancellationToken.None)));
        Assert.AreEqual(fixture.Initial.CompositionVersion.Id, secondRoot.CompositionVersion.ParentVersionId,
            "A generated short-like child remains its own branch root, not its landscape ancestor.");
    }

    [TestMethod]
    [DataRow("digest", "Conflict")]
    [DataRow("childDigest", "Conflict")]
    [DataRow("project", "NotFound")]
    [DataRow("unknownVersion", "NotFound")]
    [DataRow("nativeOnlyVersion", "NotFound")]
    public void LatestReadValidatesRequestedExactPinsBeforeReturningAnyHead(string corruption, string expected)
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot child = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "Saved"), Actor, CancellationToken.None));
        BridgeRequest read = Read(corruption == "childDigest" ? child : fixture.Initial, latest: true);
        JsonObject parameters = JsonNode.Parse(read.Parameters.GetRawText())!.AsObject();
        switch (corruption)
        {
            case "digest":
            case "childDigest": parameters["expectedVersionDigest"] = Hash("not the requested exact content").Hex; break;
            case "project": parameters["projectId"] = EntityId<ProjectIdTag>.New().ToString(); break;
            case "unknownVersion": parameters["expectedVersionId"] = EntityId<CompositionVersionIdTag>.New().ToString(); break;
            case "nativeOnlyVersion":
                parameters["expectedVersionId"] = fixture.KnownVersion.Id.ToString();
                parameters["expectedVersionDigest"] = fixture.KnownVersion.ContentDigest.Hex;
                break;
            default: throw new AssertFailedException();
        }
        BridgeRequest invalid = BridgeRequest.Create(read.RequestId, read.Method, JsonSerializer.SerializeToElement(parameters));
        LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(invalid, Actor, CancellationToken.None);
        Assert.AreEqual(expected, result.Status.ToString());
        AssertNoReceipt(invalid, result);
        Assert.AreEqual(1, fixture.Rows.Count);
        Assert.AreSame(child, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    public void IdempotentReplayReturnsItsRecordedChildAndSequenceNotTheLaterBranchHead()
    {
        using var fixture = new Fixture();
        BridgeRequest first = Edit(fixture.Initial, 0, "First recorded edit");
        LayoutSubtitleBridgeOperationResult firstResult = fixture.Service.Execute(first, Actor, CancellationToken.None);
        LayoutSubtitleBridgeSnapshot firstChild = Available(firstResult);
        string firstAck = LayoutSubtitleBridgeJson.WriteResponse(first, firstResult);
        LayoutSubtitleBridgeSnapshot later = Available(fixture.Service.Execute(Edit(firstChild, 1, "Later recorded edit"), Actor, CancellationToken.None));
        DurableSubtitleEditService restarted = fixture.Restart();
        foreach (DurableSubtitleEditService service in new[] { fixture.Service, restarted })
        {
            LayoutSubtitleBridgeOperationResult replay = service.Execute(first, Actor, CancellationToken.None);
            Assert.AreEqual(firstChild.CompositionVersion.Id, Available(replay).CompositionVersion.Id);
            AssertReceipt(first, replay, fixture.Initial, firstChild, fixture.Rows[0]);
            Assert.AreEqual(firstAck, LayoutSubtitleBridgeJson.WriteResponse(first, replay));
            BridgeRequest exact = Read(firstChild);
            AssertReceipt(exact, service.Execute(exact, Actor, CancellationToken.None), fixture.Initial, later, fixture.Rows[1]);
            Assert.AreEqual(later.CompositionVersion.Id, service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id).CompositionVersion.Id);
        }
        Assert.AreEqual(2, fixture.Rows.Count);
    }

    [TestMethod]
    public void LatestReadRefreshesTheHeadSavedByAnotherService()
    {
        using var fixture = new Fixture();
        DurableSubtitleEditService other = fixture.Restart();
        LayoutSubtitleBridgeSnapshot first = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "Other reader must refresh"), Actor, CancellationToken.None));
        BridgeRequest read = Read(fixture.Initial, latest: true);
        LayoutSubtitleBridgeOperationResult refreshed = other.Execute(read, Actor, CancellationToken.None);
        Assert.AreEqual(first.CompositionVersion.Id, Available(refreshed).CompositionVersion.Id);
        AssertReceipt(read, refreshed, fixture.Initial, first, fixture.Rows[0]);
        LayoutSubtitleBridgeSnapshot second = Available(other.Execute(Edit(refreshed.Snapshot!, 1, "Return to the first reader"), Actor, CancellationToken.None));
        BridgeRequest nextRead = Read(first, latest: true);
        LayoutSubtitleBridgeOperationResult next = fixture.Service.Execute(nextRead, Actor, CancellationToken.None);
        Assert.AreEqual(second.CompositionVersion.Id, Available(next).CompositionVersion.Id);
        AssertReceipt(nextRead, next, fixture.Initial, second, fixture.Rows[1]);
    }

    [TestMethod]
    public void NativeRequestsCannotInjectFlagsOrMetadataEvenOnAnIdempotentApply()
    {
        using var fixture = new Fixture();
        BridgeRequest edit = Edit(fixture.Initial, 0, "Committed request");
        _ = Available(fixture.Service.Execute(edit, Actor, CancellationToken.None));
        foreach (bool flag in new[] { true, false })
        {
            JsonObject parameters = JsonNode.Parse(edit.Parameters.GetRawText())!.AsObject();
            parameters["latestInBranch"] = flag;
            BridgeRequest invalid = BridgeRequest.Create(edit.RequestId, edit.Method, JsonSerializer.SerializeToElement(parameters));
            LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(invalid, Actor, CancellationToken.None);
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Invalid, result.Status);
            AssertNoReceipt(invalid, result);
        }
        foreach (string mutation in new[] { "false", "unpinned", "metadata", "extraKey" })
        {
            BridgeRequest read = Read(fixture.Initial, latest: true);
            JsonObject parameters = JsonNode.Parse(read.Parameters.GetRawText())!.AsObject();
            switch (mutation)
            {
                case "false": parameters["latestInBranch"] = false; break;
                case "unpinned": parameters.Remove("expectedVersionId"); parameters.Remove("expectedVersionDigest"); break;
                case "metadata": parameters["durableSubtitles"] = new JsonObject { ["sequence"] = 1 }; break;
                case "extraKey": parameters["rootVersionId"] = fixture.Initial.CompositionVersion.Id.ToString(); break;
            }
            BridgeRequest invalid = BridgeRequest.Create(read.RequestId, read.Method, JsonSerializer.SerializeToElement(parameters));
            LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(invalid, Actor, CancellationToken.None);
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Invalid, result.Status);
            AssertNoReceipt(invalid, result);
        }
        Assert.AreEqual(1, fixture.Rows.Count);
    }

    [TestMethod]
    [DataRow("composition_versions")]
    [DataRow("subtitle_edits")]
    [DataRow("postInsertValidation")]
    public async Task FailedCommitCannotPublishMetadataOrAChildAndRetryAcknowledgesOnlyTheCommittedRow(string failure)
    {
        using var fixture = new Fixture();
        if (failure == "postInsertValidation")
        {
            fixture.Sql("""
                DROP TRIGGER trg_subtitle_edits_update;
                CREATE TRIGGER durable_contract_failure AFTER INSERT ON subtitle_edits
                BEGIN UPDATE subtitle_edits SET commit_digest = printf('%064d', 0) WHERE request_id = NEW.request_id; END;
                """);
        }
        else
        {
            fixture.Sql($"""
                CREATE TRIGGER durable_contract_failure BEFORE INSERT ON {failure}
                BEGIN SELECT RAISE(ABORT, 'injected contract test failure'); END;
                """);
        }
        BridgeRequest edit = Edit(fixture.Initial, 0, "Not acknowledged before commit");
        var handler = new LayoutSubtitleBridgeRequestHandler(fixture.Service, new Authorization());
        using (JsonDocument response = JsonDocument.Parse(await handler.HandleAsync(edit)))
        {
            JsonElement result = response.RootElement.GetProperty("result");
            Assert.AreEqual("unavailable", result.GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
            Assert.IsFalse(result.TryGetProperty("durableSubtitles", out _));
        }
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreEqual(2L, fixture.VersionCount);
        BridgeRequest read = Read(fixture.Initial, latest: true);
        AssertReceipt(read, fixture.Service.Execute(read, Actor, CancellationToken.None), fixture.Initial, fixture.Initial, null);
        fixture.Sql("DROP TRIGGER durable_contract_failure;");
        LayoutSubtitleBridgeOperationResult committed = fixture.Service.Execute(edit, Actor, CancellationToken.None);
        AssertReceipt(edit, committed, fixture.Initial, Available(committed), fixture.Rows.Single());
        Assert.AreEqual(3L, fixture.VersionCount);
    }

    [TestMethod]
    public async Task ChangedStoredRootFailsClosedWithoutFallingBackOrReturningMetadata()
    {
        using var fixture = new Fixture();
        JsonNode root = JsonNode.Parse(CompositionSnapshotJson.WriteVersion(fixture.Initial.CompositionVersion))!;
        root["changeReason"] = "Different stored root with the same element digest";
        fixture.Sql("UPDATE composition_versions SET payload = $payload WHERE id = $root;",
            ("$payload", Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(root.ToJsonString()))),
            ("$root", fixture.Initial.CompositionVersion.Id.ToString()));
        var handler = new LayoutSubtitleBridgeRequestHandler(fixture.Service, new Authorization());
        using JsonDocument response = JsonDocument.Parse(await handler.HandleAsync(Read(fixture.Initial, latest: true)));
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.AreEqual("unavailable", result.GetProperty("status").GetString());
        Assert.IsFalse(result.TryGetProperty("durableSubtitles", out _));
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
        Assert.AreEqual(0, fixture.Rows.Count);
    }

    [TestMethod]
    public void NoChangeOrConflictNeverAcquiresAPersistenceReceipt()
    {
        using var fixture = new Fixture();
        BridgeRequest noChange = Edit(fixture.Initial, 0, fixture.Initial.Subtitles[0].History.CurrentRevision.Snapshot.Text);
        LayoutSubtitleBridgeOperationResult unchanged = fixture.Service.Execute(noChange, Actor, CancellationToken.None);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NoChange, unchanged.Status);
        AssertNoReceipt(noChange, unchanged);
        _ = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "Advance"), Actor, CancellationToken.None));
        BridgeRequest stale = Edit(fixture.Initial, 1, "Not current");
        LayoutSubtitleBridgeOperationResult conflict = fixture.Service.Execute(stale, Actor, CancellationToken.None);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, conflict.Status);
        AssertNoReceipt(stale, conflict);
        Assert.AreEqual(1, fixture.Rows.Count);
    }

    [TestMethod]
    public void MetadataRequiresConsistentSequenceAndDigestAndOversizedResponsesDropIt()
    {
        LayoutSubtitleBridgeSnapshot snapshot = LayoutSubtitleBridgeTests.Fixture.Create(oversizedTemplateCatalog: true).Snapshot;
        var root = snapshot.CompositionVersion.Id;
        var child = EntityId<CompositionVersionIdTag>.New();
        ContentDigest digest = snapshot.CompositionVersion.ContentDigest;
        Assert.ThrowsExactly<ArgumentException>(() => new DurableSubtitleBranchMetadata(root, child, digest, -1, digest));
        Assert.ThrowsExactly<ArgumentException>(() => new DurableSubtitleBranchMetadata(root, root, digest, 0, digest));
        Assert.ThrowsExactly<ArgumentException>(() => new DurableSubtitleBranchMetadata(root, child, digest, 0, null));
        Assert.ThrowsExactly<ArgumentException>(() => new DurableSubtitleBranchMetadata(root, child, digest, 1, null));
        Assert.ThrowsExactly<ArgumentException>(() => new DurableSubtitleBranchMetadata(root, root, digest, 1, digest));
        var metadata = new DurableSubtitleBranchMetadata(root, root, digest, 0, null);
        BridgeRequest read = Read(snapshot, latest: true);
        var result = LayoutSubtitleBridgeOperationResult.Available(snapshot, metadata);
        Assert.IsFalse(LayoutSubtitleBridgeJson.CanWriteResponse(read, result));
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(read, result));
        Assert.AreEqual("unavailable", response.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.IsFalse(response.RootElement.GetProperty("result").TryGetProperty("durableSubtitles", out _));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(response.RootElement.GetRawText()) <= BridgeProtocol.MaxLayoutResponseUtf8Bytes);
    }

    private static void AssertReceipt(BridgeRequest request, LayoutSubtitleBridgeOperationResult result,
        LayoutSubtitleBridgeSnapshot root, LayoutSubtitleBridgeSnapshot head, StoredSubtitleEdit? row)
    {
        LayoutSubtitleBridgeSnapshot snapshot = Available(result);
        Assert.IsNotNull(result.DurableSubtitles);
        Assert.AreEqual(root.CompositionVersion.Id, result.DurableSubtitles.RootVersionId);
        Assert.AreEqual(head.CompositionVersion.Id, result.DurableSubtitles.HeadVersionId);
        Assert.AreEqual(head.CompositionVersion.ContentDigest, result.DurableSubtitles.HeadVersionDigest);
        Assert.AreEqual(row?.Sequence ?? 0, result.DurableSubtitles.Sequence);
        Assert.AreEqual(row?.RecordDigest, result.DurableSubtitles.JournalDigest);
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(request, result));
        Assert.AreEqual(request.RequestId.ToString("D"), response.RootElement.GetProperty("requestId").GetString());
        Assert.AreEqual(BridgeProtocol.GetMethodName(request.Method), response.RootElement.GetProperty("method").GetString());
        JsonElement wire = response.RootElement.GetProperty("result");
        CollectionAssert.AreEquivalent(new[] { "status", "snapshot", "durableSubtitles" }, wire.EnumerateObject().Select(property => property.Name).ToArray());
        JsonElement metadata = wire.GetProperty("durableSubtitles");
        CollectionAssert.AreEquivalent(new[] { "rootVersionId", "headVersionId", "headVersionDigest", "sequence", "journalDigest" },
            metadata.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(root.CompositionVersion.Id.ToString(), metadata.GetProperty("rootVersionId").GetString());
        Assert.AreEqual(head.CompositionVersion.Id.ToString(), metadata.GetProperty("headVersionId").GetString());
        Assert.AreEqual(head.CompositionVersion.ContentDigest.Hex, metadata.GetProperty("headVersionDigest").GetString());
        Assert.AreEqual(row?.Sequence ?? 0, metadata.GetProperty("sequence").GetInt32());
        Assert.AreEqual(row?.RecordDigest.Hex, metadata.GetProperty("journalDigest").GetString());
        Assert.AreEqual(row is null ? JsonValueKind.Null : JsonValueKind.String, metadata.GetProperty("journalDigest").ValueKind);
        JsonElement json = wire.GetProperty("snapshot");
        Assert.AreEqual(snapshot.ProjectId.ToString(), json.GetProperty("projectId").GetString());
        Assert.AreEqual(snapshot.CompositionVersion.Id.ToString(), json.GetProperty("compositionVersionId").GetString());
        Assert.AreEqual(snapshot.CompositionVersion.ContentDigest.Hex, json.GetProperty("compositionVersionDigest").GetString());
        bool persisted = row is not null && snapshot.CompositionVersion.Id == head.CompositionVersion.Id
            && snapshot.CompositionVersion.ContentDigest == head.CompositionVersion.ContentDigest && snapshot.Revision == row.Sequence;
        Assert.AreEqual(persisted ? "persisted" : "notPersisted", json.GetProperty("persistence").GetString());
        Assert.IsFalse(json.TryGetProperty("durableSubtitles", out _));
        Assert.IsFalse(wire.TryGetProperty("requestedVersionId", out _));
        Assert.IsFalse(response.RootElement.GetRawText().Contains(Actor, StringComparison.Ordinal));
    }

    private static void AssertNoReceipt(BridgeRequest request, LayoutSubtitleBridgeOperationResult result)
    {
        Assert.IsNull(result.Snapshot);
        Assert.IsNull(result.DurableSubtitles);
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(request, result));
        CollectionAssert.AreEquivalent(new[] { "status", "snapshot" }, response.RootElement.GetProperty("result")
            .EnumerateObject().Select(property => property.Name).ToArray());
    }

    private static LayoutSubtitleBridgeSnapshot Available(LayoutSubtitleBridgeOperationResult result)
    {
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, result.Status);
        Assert.IsNotNull(result.Snapshot);
        return result.Snapshot;
    }

    private static BridgeRequest Read(LayoutSubtitleBridgeSnapshot snapshot, bool latest = false)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["projectId"] = snapshot.ProjectId.ToString(),
            ["evaluationTime"] = null,
            ["expectedVersionId"] = snapshot.CompositionVersion.Id.ToString(),
            ["expectedVersionDigest"] = snapshot.CompositionVersion.ContentDigest.Hex,
        };
        if (latest) { parameters["latestInBranch"] = true; }
        return BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutRead, JsonSerializer.SerializeToElement(parameters));
    }

    private static BridgeRequest DefaultRead(LayoutSubtitleBridgeSnapshot snapshot) => BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutRead,
        JsonSerializer.SerializeToElement(new { projectId = snapshot.ProjectId.ToString(), evaluationTime = (object?)null }));

    private static BridgeRequest Edit(LayoutSubtitleBridgeSnapshot snapshot, int cue, string text)
    {
        SubtitleCueHistory history = snapshot.Subtitles[cue].History;
        return BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
        {
            projectId = snapshot.ProjectId.ToString(),
            expectedRevision = snapshot.Revision,
            expectedVersionId = snapshot.CompositionVersion.Id.ToString(),
            expectedVersionDigest = snapshot.CompositionVersion.ContentDigest.Hex,
            operation = new
            {
                kind = "editSubtitle",
                cueId = history.CueId.ToString(),
                expectedRevisionId = history.CurrentRevision.Id.ToString(),
                expectedRevisionDigest = history.CurrentRevision.ContentDigest.Hex,
                edit = new { kind = "text", text },
                reason = "Explicit durable layout contract correction.",
            },
        }));
    }

    private static string SyntheticWire(LayoutSubtitleBridgeSnapshot snapshot)
    {
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(Read(snapshot), LayoutSubtitleBridgeOperationResult.Available(snapshot)));
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.IsFalse(result.TryGetProperty("durableSubtitles", out _));
        Assert.AreEqual("notPersisted", result.GetProperty("snapshot").GetProperty("persistence").GetString());
        return result.GetRawText();
    }

    private static ContentDigest Hash(string text) => ContentDigest.Compute(Encoding.UTF8.GetBytes(text));

    private sealed class Authorization : ILayoutSubtitleBridgeAuthorization
    {
        public string DemandRead() => Actor;
        public string DemandMutation() => Actor;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "HubRadio.DurableLayoutContract", Guid.NewGuid().ToString("N"));
        private readonly ContentDigest inputDigest = Hash("validated generated fixture input");

        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "store.db");
            Assert.IsTrue(DatabaseMigrator.Migrate(DatabasePath, Path.Combine(directory, "backups")).SchemaVersion >= 12);
            LayoutSubtitleBridgeSnapshot initial = LayoutSubtitleBridgeTests.Fixture.Create(includeSecondSubtitle: true).Snapshot;
            Projects = new ProjectStore(DatabasePath);
            var project = new Project(initial.ProjectId, "Contract fixture", "Saved-head protocol", "Editors", PublicationScope.InternalOnly, null, [], []);
            Projects.CreateProject(project, ProjectSnapshotJson.WriteProject(project), initial.CompositionVersion.CreatedAt);
            Projects.AppendCompositionVersion(initial.CompositionVersion, CompositionSnapshotJson.WriteVersion(initial.CompositionVersion));
            KnownVersion = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), initial.ProjectId, initial.CompositionVersion.Id,
                Actor, initial.CompositionVersion.CreatedAt.AddSeconds(1), "Native context, not a journal member",
                initial.CompositionVersion.ContentDigest, initial.CompositionVersion.Elements);
            Projects.AppendCompositionVersion(KnownVersion, CompositionSnapshotJson.WriteVersion(KnownVersion));
            var versions = new Dictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>(initial.CompositionVersions) { [KnownVersion.Id] = KnownVersion };
            Initial = new LayoutSubtitleBridgeSnapshot(initial.ProjectId, 0, initial.CompositionVersion, initial.OutputEnvelope,
                initial.OutputPixelWidth, initial.OutputPixelHeight, initial.Sources, initial.Templates, initial.Subtitles, initial.Terops,
                initial.SubtitleDisplays, compositionVersions: versions);
            Store = new SubtitleEditStore(DatabasePath);
            Service = Restart();
        }

        internal string DatabasePath { get; }
        internal ProjectStore Projects { get; }
        internal SubtitleEditStore Store { get; }
        internal DurableSubtitleEditService Service { get; }
        internal LayoutSubtitleBridgeSnapshot Initial { get; }
        internal CompositionVersion KnownVersion { get; }
        internal IReadOnlyList<StoredSubtitleEdit> Rows => Store.ReadBranch(Initial.ProjectId, Initial.CompositionVersion.Id);

        internal long VersionCount
        {
            get
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM composition_versions WHERE project_id = $project;";
                command.Parameters.AddWithValue("$project", Initial.ProjectId.ToString());
                return (long)command.ExecuteScalar()!;
            }
        }

        internal DurableSubtitleEditService Restart()
        {
            var service = new DurableSubtitleEditService(new SubtitleEditStore(DatabasePath));
            service.RegisterGenerated(Initial, inputDigest);
            return service;
        }

        internal LayoutSubtitleBridgeSnapshot RegisterSecondRoot()
        {
            var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), Initial.ProjectId, Initial.CompositionVersion.Id,
                Actor, Initial.CompositionVersion.CreatedAt.AddSeconds(2), "Independent generated child branch", Initial.CompositionVersion.ContentDigest,
                Initial.CompositionVersion.Elements);
            Projects.AppendCompositionVersion(version, CompositionSnapshotJson.WriteVersion(version));
            LayoutSubtitleCueBinding[] subtitles = Initial.Subtitles.Select(binding => binding.WithHistory(SubtitleService.CreateInitial(
                version, binding.History.SourceCandidate, binding.History.CurrentRevision.Snapshot.LayoutResolution, EntityId<CueIdTag>.New(),
                EntityId<SubtitleCueRevisionIdTag>.New(), binding.History.CurrentRevision.Snapshot.Cue.Range, Actor,
                binding.History.CurrentRevision.CreatedAt, "Second branch's actual initial cue"))).ToArray();
            var snapshot = new LayoutSubtitleBridgeSnapshot(Initial.ProjectId, 0, version, Initial.OutputEnvelope,
                Initial.OutputPixelWidth, Initial.OutputPixelHeight, Initial.Sources, Initial.Templates, subtitles, [],
                [new LayoutSubtitleDisplayBinding(new SubtitleDisplayProfile(Hash("second display"), SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve), [])], compositionVersions: Initial.CompositionVersions);
            Service.RegisterGenerated(snapshot, Hash("second generated input"), makeDefault: false);
            return snapshot;
        }

        internal void Sql(string sql, params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters) { command.Parameters.AddWithValue(name, value); }
            command.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}