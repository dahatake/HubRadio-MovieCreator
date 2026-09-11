using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

/// <summary>
/// Real schema-012 transactions and native runtime evaluation; no private schema,
/// fake store, worker, rendering, or approval. Positive tests deliberately retain
/// ordinary user reasons: they expose the store's ChangeReason/reason mismatch
/// until that contract is corrected by its owner, rather than disguising it.
/// </summary>
[TestClass]
public sealed class DurableSubtitleEditServiceTests
{
    private const string Actor = "S-1-5-21-111-222-333-1001";
    private const string OtherActor = "S-1-5-21-111-222-333-1002";
    private const string Reason = "Correct the selected subtitle from editor review.";
    private static readonly DateTimeOffset At = new(2038, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [TestMethod]
    public void ConstructorRequiresTheBoundStore()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new DurableSubtitleEditService(null!));
    }

    [TestMethod]
    public void SiblingAThenBThenASurvivesRestartWithExactIdsTimesChangesAndKnownVersions()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot initial = fixture.Initial;
        var states = new List<LayoutSubtitleBridgeSnapshot> { initial };
        var requests = new List<BridgeRequest>();
        foreach (int cue in new[] { 0, 1, 0 })
        {
            LayoutSubtitleBridgeSnapshot before = states[^1];
            BridgeRequest request = Edit(before, cue, $"編集 {states.Count}");
            LayoutSubtitleBridgeSnapshot after = Available(fixture.Service.Execute(request, Actor, CancellationToken.None));
            Assert.AreEqual(before.CompositionVersion.Id, after.CompositionVersion.ParentVersionId);
            Assert.AreEqual(before.Revision + 1, after.Revision);
            Assert.AreSame(before.Subtitles[1 - cue], after.Subtitles[1 - cue]);
            Assert.AreSame(before.Subtitles[cue].History.CurrentRevision.Snapshot,
                after.Subtitles[cue].History.CurrentRevision.Change.Previous);
            Assert.AreSame(fixture.KnownVersion, after.GetComposition(fixture.KnownVersion.Id));
            Assert.AreSame(before, Available(fixture.Service.Execute(Read(before), Actor, CancellationToken.None)));
            Assert.AreEqual(initial.CompositionVersion.Id, fixture.Service.GetBranchRoot(initial.ProjectId, after.CompositionVersion.Id));
            states.Add(after);
            requests.Add(request);
        }

        LayoutSubtitleBridgeSnapshot head = states[^1];
        Assert.AreEqual(3, head.Subtitles[0].History.Revisions.Count);
        Assert.AreEqual(2, head.Subtitles[1].History.Revisions.Count);
        CollectionAssert.AreEqual(new[] { states[0].CompositionVersion.Id, states[1].CompositionVersion.Id },
            head.Subtitles[1].History.Inheritance.Single().Versions.Select(version => version.Id).ToArray());
        CollectionAssert.AreEqual(new[] { states[1].CompositionVersion.Id, states[2].CompositionVersion.Id },
            head.Subtitles[0].History.Inheritance.Single().Versions.Select(version => version.Id).ToArray());
        Assert.AreEqual(3, fixture.Rows.Count);
        for (int index = 0; index < fixture.Rows.Count; index++)
        {
            StoredSubtitleEdit row = fixture.Rows[index];
            LayoutSubtitleBridgeSnapshot child = states[index + 1];
            int cue = index == 1 ? 1 : 0;
            SubtitleCueRevision revision = child.Subtitles[cue].History.CurrentRevision;
            Assert.AreEqual(requests[index].RequestId, row.RequestId);
            Assert.AreEqual(fixture.InputDigest, row.GeneratedInputDigest);
            Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(states[index]), row.ParentSnapshotDigest);
            Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(child), row.ResultSnapshotDigest);
            Assert.AreEqual(CompositionSnapshotJson.WriteVersion(child.CompositionVersion), row.ChildCanonicalJson);
            Assert.AreEqual(row.ChildCanonicalJson, fixture.Projects.GetCompositionVersion(row.ChildVersionId)!.CanonicalJson);
            Assert.AreEqual(row.OccurredAt, child.CompositionVersion.CreatedAt);
            Assert.AreEqual(row.OccurredAt, revision.CreatedAt);
            Assert.AreEqual(TimeSpan.Zero, row.OccurredAt.Offset);
            Assert.IsTrue(row.OccurredAt >= states[index].CompositionVersion.CreatedAt);
            Assert.IsTrue(row.OccurredAt >= states[index].Subtitles.Max(binding => binding.History.CurrentRevision.CreatedAt));
            CollectionAssert.AreEqual(new[] { child.CompositionVersion.Id.Value, revision.Id.Value, revision.Change.Origin.EditorialRecordId!.Value },
                row.AllocatedIds.ToArray());
            using JsonDocument operation = JsonDocument.Parse(row.OperationCanonicalJson);
            Assert.AreEqual(Reason, operation.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(Reason, revision.Reason);
            Assert.AreNotEqual(Reason, child.CompositionVersion.ChangeReason,
                "The service must not fake the operation reason to satisfy an incorrect store restriction.");
        }

        fixture.Service.RegisterGenerated(fixture.Copy(initial), fixture.InputDigest);
        Assert.AreSame(head, fixture.Service.GetLatest(initial.ProjectId, initial.CompositionVersion.Id));
        Assert.AreSame(initial, Available(fixture.Service.Execute(Read(initial), Actor, CancellationToken.None)));
        var restoredInitial = fixture.Copy(initial);
        var restarted = fixture.Restart(restoredInitial);
        for (int index = 0; index < states.Count; index++)
        {
            LayoutSubtitleBridgeSnapshot restored = Available(restarted.Execute(Read(states[index]), Actor, CancellationToken.None));
            Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(states[index]), DurableSubtitleEditService.ComputeSnapshotDigest(restored));
            Assert.AreEqual(CompositionSnapshotJson.WriteVersion(states[index].CompositionVersion), CompositionSnapshotJson.WriteVersion(restored.CompositionVersion));
            Assert.AreSame(fixture.KnownVersion, restored.GetComposition(fixture.KnownVersion.Id));
        }
        Assert.AreSame(restoredInitial, Available(restarted.Execute(Read(initial), Actor, CancellationToken.None)));
        LayoutSubtitleBridgeSnapshot restoredHead = restarted.GetLatest(initial.ProjectId, initial.CompositionVersion.Id);
        Assert.AreSame(restoredHead, Available(restarted.Execute(DefaultRead(initial.ProjectId), Actor, CancellationToken.None)));
        LayoutSubtitleBridgeSnapshot originalReply = Available(restarted.Execute(requests[0], Actor, CancellationToken.None));
        Assert.AreEqual(states[1].CompositionVersion.Id, originalReply.CompositionVersion.Id);
        Assert.AreSame(restoredHead, restarted.GetLatest(initial.ProjectId, initial.CompositionVersion.Id));
        Assert.AreEqual(3, fixture.Rows.Count);
    }

    [TestMethod]
    public void TextMayReturnToItsOriginalValueWithoutCollapsingTheJournalOrHistory()
    {
        using var fixture = new Fixture();
        string original = fixture.Initial.Subtitles[0].History.CurrentRevision.Snapshot.Text;
        LayoutSubtitleBridgeSnapshot changed = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "B"), Actor, CancellationToken.None));
        LayoutSubtitleBridgeSnapshot restored = Available(fixture.Service.Execute(Edit(changed, 0, original), Actor, CancellationToken.None));
        Assert.AreEqual(original, restored.Subtitles[0].History.CurrentRevision.Snapshot.Text);
        Assert.AreEqual(fixture.Initial.CompositionVersion.ContentDigest, restored.CompositionVersion.ContentDigest);
        Assert.AreEqual(3, restored.Subtitles[0].History.Revisions.Count);
        Assert.AreEqual(2, fixture.Rows.Count);
        Assert.AreNotEqual(fixture.Rows[0].ResultSnapshotDigest, fixture.Rows[1].ResultSnapshotDigest);
        Assert.AreNotEqual(fixture.Rows[0].ParentSnapshotDigest, fixture.Rows[1].ResultSnapshotDigest);
    }

    [TestMethod]
    public void SecondServiceReadsAuthoritativeHeadBeforeEvaluatingAnOldCachedParent()
    {
        using var fixture = new Fixture();
        DurableSubtitleEditService other = fixture.Restart(fixture.Initial);
        LayoutSubtitleBridgeSnapshot first = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "First writer"), Actor, CancellationToken.None));
        LayoutSubtitleBridgeOperationResult stale = other.Execute(Edit(fixture.Initial, 1, "Stale writer"), Actor, CancellationToken.None);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, stale.Status);
        Assert.IsNull(stale.Snapshot);
        Assert.AreEqual(1, fixture.Rows.Count);
        LayoutSubtitleBridgeSnapshot refreshed = other.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id);
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(first), DurableSubtitleEditService.ComputeSnapshotDigest(refreshed));
        LayoutSubtitleBridgeSnapshot second = Available(other.Execute(Edit(refreshed, 1, "Fresh writer"), Actor, CancellationToken.None));
        Assert.AreEqual(first.CompositionVersion.Id, second.CompositionVersion.ParentVersionId);
        Assert.AreEqual(second.CompositionVersion.Id, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id).CompositionVersion.Id);
        Assert.AreEqual(2, fixture.Rows.Count);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("reason")]
    [DataRow("expectedRevision")]
    [DataRow("expectedVersionId")]
    [DataRow("expectedVersionDigest")]
    [DataRow("cueId")]
    [DataRow("expectedRevisionId")]
    [DataRow("expectedRevisionDigest")]
    [DataRow("unpinned")]
    [DataRow("actor")]
    public void DuplicateIdRequiresTheOriginalOperationActorAndEveryExpectedField(string field)
    {
        using var fixture = new Fixture();
        BridgeRequest request = Edit(fixture.Initial, 0, "Recorded correction");
        LayoutSubtitleBridgeSnapshot originalReply = Available(fixture.Service.Execute(request, Actor, CancellationToken.None));
        LayoutSubtitleBridgeSnapshot head = Available(fixture.Service.Execute(Edit(originalReply, 1, "Later correction"), Actor, CancellationToken.None));
        JsonObject parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
        switch (field)
        {
            case "text": parameters["operation"]!["edit"]!["text"] = "Forged text"; break;
            case "reason": parameters["operation"]!["reason"] = "Forged reason"; break;
            case "expectedRevision": parameters["expectedRevision"] = 2; break;
            case "expectedVersionId": parameters["expectedVersionId"] = head.CompositionVersion.Id.ToString(); break;
            case "expectedVersionDigest": parameters["expectedVersionDigest"] = Hash("wrong version").Hex; break;
            case "cueId": parameters["operation"]!["cueId"] = fixture.Initial.Subtitles[1].History.CueId.ToString(); break;
            case "expectedRevisionId": parameters["operation"]!["expectedRevisionId"] = Guid.NewGuid().ToString("D"); break;
            case "expectedRevisionDigest": parameters["operation"]!["expectedRevisionDigest"] = Hash("wrong cue").Hex; break;
            case "unpinned": parameters.Remove("expectedVersionId"); parameters.Remove("expectedVersionDigest"); break;
            case "actor": break;
            default: throw new AssertFailedException();
        }
        BridgeRequest forged = BridgeRequest.Create(request.RequestId, request.Method, JsonSerializer.SerializeToElement(parameters));
        LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(forged, field == "actor" ? OtherActor : Actor, CancellationToken.None);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, result.Status);
        Assert.IsNull(result.Snapshot);
        Assert.AreSame(originalReply, Available(fixture.Service.Execute(request, Actor, CancellationToken.None)));
        Assert.AreSame(head, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
        Assert.AreEqual(2, fixture.Rows.Count);
    }

    [TestMethod]
    [DataRow("expectedRevision")]
    [DataRow("expectedVersionDigest")]
    [DataRow("expectedRevisionId")]
    [DataRow("expectedRevisionDigest")]
    public void FreshRequestMustMatchBothHeadAndCueEvidenceBeforeSaving(string field)
    {
        using var fixture = new Fixture();
        long versions = fixture.VersionCount;
        BridgeRequest request = Edit(fixture.Initial, 0, "Rejected optimistic expectation");
        JsonObject parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
        switch (field)
        {
            case "expectedRevision": parameters[field] = 1; break;
            case "expectedVersionDigest": parameters[field] = Hash("not this head").Hex; break;
            case "expectedRevisionId": parameters["operation"]![field] = Guid.NewGuid().ToString("D"); break;
            case "expectedRevisionDigest": parameters["operation"]![field] = Hash("not this cue").Hex; break;
            default: throw new AssertFailedException();
        }
        LayoutSubtitleBridgeOperationResult result = fixture.Service.Execute(BridgeRequest.Create(request.RequestId, request.Method,
            JsonSerializer.SerializeToElement(parameters)), Actor, CancellationToken.None);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, result.Status);
        Assert.IsNull(result.Snapshot);
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreEqual(versions, fixture.VersionCount);
        Assert.AreSame(fixture.Initial, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    public void BranchesKeepPinnedVersionsIndependentAndDefaultReadsFollowOnlyTheDefaultHead()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot secondRoot = fixture.CreateGenerated();
        fixture.Service.RegisterGenerated(secondRoot, Hash("second generated key"), makeDefault: false);
        LayoutSubtitleBridgeSnapshot firstHead = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "Landscape correction"), Actor, CancellationToken.None));
        LayoutSubtitleBridgeSnapshot secondHead = Available(fixture.Service.Execute(Edit(secondRoot, 1, "Short correction"), Actor, CancellationToken.None));
        Assert.AreSame(firstHead, Available(fixture.Service.Execute(DefaultRead(fixture.Initial.ProjectId), Actor, CancellationToken.None)));
        Assert.AreSame(secondRoot, Available(fixture.Service.Execute(Read(secondRoot), Actor, CancellationToken.None)));
        Assert.AreSame(secondHead, Available(fixture.Service.Execute(Read(secondHead), Actor, CancellationToken.None)));
        Assert.AreEqual(secondRoot.CompositionVersion.Id, fixture.Service.GetBranchRoot(secondRoot.ProjectId, secondHead.CompositionVersion.Id));
        fixture.Service.RegisterGenerated(secondRoot, Hash("second generated key"), makeDefault: true);
        Assert.AreSame(secondHead, Available(fixture.Service.Execute(DefaultRead(secondRoot.ProjectId), Actor, CancellationToken.None)));

        JsonObject unpinned = JsonNode.Parse(Edit(secondHead, 0, "No explicit version").Parameters.GetRawText())!.AsObject();
        unpinned.Remove("expectedVersionId");
        unpinned.Remove("expectedVersionDigest");
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Conflict, fixture.Service.Execute(BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply,
            JsonSerializer.SerializeToElement(unpinned)), Actor, CancellationToken.None).Status);
    }

    [TestMethod]
    public void CapturesAreReadOnlyAndDoNotAdvanceWhenTheirBranchAdvances()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot secondRoot = fixture.CreateGenerated();
        fixture.Service.RegisterGenerated(secondRoot, Hash("short input"), makeDefault: false);
        IReadOnlyList<SubtitleEditCapture> capture = fixture.Service.Capture(fixture.Initial.ProjectId,
            new[] { fixture.Initial.CompositionVersion.Id, secondRoot.CompositionVersion.Id });
        Assert.AreEqual(2, capture.Count);
        Assert.AreSame(fixture.Initial, capture[0].Head);
        Assert.AreEqual(fixture.InputDigest, capture[0].GeneratedInputDigest);
        Assert.AreEqual(capture[0].InitialSnapshotDigest, capture[0].HeadSnapshotDigest);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SubtitleEditCapture>)capture).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StoredSubtitleEdit>)capture[0].Edits).Clear());
        LayoutSubtitleBridgeSnapshot head = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "After capture"), Actor, CancellationToken.None));
        Assert.AreSame(fixture.Initial, capture[0].Head);
        Assert.AreEqual(0, capture[0].Sequence);
        SubtitleEditCapture later = fixture.Service.Capture(fixture.Initial.ProjectId, [fixture.Initial.CompositionVersion.Id]).Single();
        Assert.AreSame(head, later.Head);
        Assert.AreEqual(1, later.Sequence);
        Assert.AreEqual(fixture.Rows.Single().ResultSnapshotDigest, later.HeadSnapshotDigest);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Service.Capture(fixture.Initial.ProjectId,
            [fixture.Initial.CompositionVersion.Id, fixture.Initial.CompositionVersion.Id]));
    }

    [TestMethod]
    public void PrefixCapturedAfterLaterEditsAndRestartStillHasTheAcceptedHeadAndOnlyItsVersions()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot initial = fixture.Initial;
        LayoutSubtitleBridgeSnapshot accepted = Available(fixture.Service.Execute(Edit(initial, 0, "Accepted text"), Actor, CancellationToken.None));
        StoredSubtitleEdit first = fixture.Rows.Single();
        LayoutSubtitleBridgeSnapshot later = Available(fixture.Service.Execute(Edit(accepted, 1, "Later text"), Actor, CancellationToken.None));
        DurableSubtitleEditService restarted = fixture.Restart(fixture.Copy(initial));

        foreach (DurableSubtitleEditService service in new[] { fixture.Service, restarted })
        {
            SubtitleEditCapture prefix = service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 1, first.RecordDigest);
            Assert.AreEqual(1, prefix.Sequence);
            Assert.AreEqual(accepted.CompositionVersion.Id, prefix.HeadVersionId);
            Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(accepted), prefix.HeadSnapshotDigest);
            Assert.AreEqual(first.RecordDigest, prefix.Edits.Single().RecordDigest);
            Assert.AreEqual(DurableSubtitleEditService.ComputeInitialDigest(initial), prefix.InitialSnapshotDigest);
            Assert.IsNull(prefix.InitialProjection);
            Assert.IsFalse(prefix.Head.CompositionVersions.ContainsKey(later.CompositionVersion.Id));
            Assert.AreSame(fixture.KnownVersion, prefix.Head.GetComposition(fixture.KnownVersion.Id));
            Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StoredSubtitleEdit>)prefix.Edits).Clear());
            SubtitleEditCapture empty = service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 0, null);
            Assert.AreEqual(0, empty.Sequence);
            Assert.AreEqual(initial.CompositionVersion.Id, empty.HeadVersionId);
            Assert.AreEqual(empty.InitialSnapshotDigest, empty.HeadSnapshotDigest);
            Assert.AreEqual(later.CompositionVersion.Id, service.GetLatest(initial.ProjectId, initial.CompositionVersion.Id).CompositionVersion.Id);
            Assert.AreEqual(2, service.Capture(initial.ProjectId, [initial.CompositionVersion.Id]).Single().Sequence);
        }
        Assert.AreEqual(2, fixture.Rows.Count);
    }

    [TestMethod]
    public void PrefixRequiresExactSequenceDigestPairAndHonorsCancellation()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot initial = fixture.Initial;
        _ = Available(fixture.Service.Execute(Edit(initial, 0, "Pinned record"), Actor, CancellationToken.None));
        ContentDigest digest = fixture.Rows.Single().RecordDigest;
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 0, digest));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 1, null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, -1, null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id,
            SubtitleEditStore.MaximumBranchEdits + 1, digest));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 2, digest));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 1, Hash("wrong prefix")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id,
            1, digest, cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Service.Capture(initial.ProjectId, [initial.CompositionVersion.Id], cancellation.Token));
        Assert.AreEqual(1, fixture.Rows.Count);
    }

    [TestMethod]
    public void PrefixStoreVerifiesTheFullBranchButDoesNotSemanticallyReplayUnacceptedLaterEdits()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot initial = fixture.Initial;
        LayoutSubtitleBridgeSnapshot accepted = Available(fixture.Service.Execute(Edit(initial, 0, "Accepted"), Actor, CancellationToken.None));
        ContentDigest digest = fixture.Rows.Single().RecordDigest;
        _ = Available(fixture.Service.Execute(Edit(accepted, 1, "Unaccepted later operation"), Actor, CancellationToken.None));
        fixture.RewriteCommitWithValidHashes(fixture.Rows[1], "operation");
        Assert.AreEqual(2, fixture.Rows.Count, "The entire stored branch still passes store integrity checks.");
        SubtitleEditCapture prefix = fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 1, digest);
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(accepted), prefix.HeadSnapshotDigest);
        Assert.AreEqual(1, prefix.Sequence);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Capture(initial.ProjectId, [initial.CompositionVersion.Id]));
        fixture.Sql("UPDATE subtitle_edits SET commit_digest = $digest WHERE sequence = 2;", ("$digest", Hash("damaged later storage").Hex));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.CapturePrefix(initial.ProjectId, initial.CompositionVersion.Id, 1, digest));
    }

    [TestMethod]
    [DataRow("composition_versions")]
    [DataRow("subtitle_edits")]
    public async Task FailureBeforeCommitCannotPublishAChildOrAnAvailableAck(string table)
    {
        using var fixture = new Fixture();
        long versions = fixture.VersionCount;
        fixture.Sql($"""
            CREATE TRIGGER durable_subtitle_test_failure BEFORE INSERT ON {table}
            BEGIN SELECT RAISE(ABORT, 'injected durable subtitle transaction failure'); END;
            """);
        BridgeRequest request = Edit(fixture.Initial, 0, "Must not be acknowledged yet");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Service.Execute(request, Actor, CancellationToken.None));
        var handler = new LayoutSubtitleBridgeRequestHandler(fixture.Service, new Authorization());
        using JsonDocument response = JsonDocument.Parse(await handler.HandleAsync(request));
        AssertUnavailable(response);
        Assert.AreSame(fixture.Initial, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreEqual(versions, fixture.VersionCount);
        fixture.Sql("DROP TRIGGER durable_subtitle_test_failure;");
        LayoutSubtitleBridgeSnapshot committed = Available(fixture.Service.Execute(request, Actor, CancellationToken.None));
        Assert.AreSame(committed, Available(fixture.Service.Execute(request, Actor, CancellationToken.None)));
        Assert.AreEqual(1, fixture.Rows.Count);
        Assert.AreEqual(versions + 1, fixture.VersionCount);
    }

    [TestMethod]
    public async Task PostInsertValidationFailureRollsBackAndLeavesTheOriginalSnapshot()
    {
        using var fixture = new Fixture();
        long versions = fixture.VersionCount;
        fixture.Sql("""
            DROP TRIGGER trg_subtitle_edits_update;
            CREATE TRIGGER durable_subtitle_test_corruption AFTER INSERT ON subtitle_edits
            BEGIN UPDATE subtitle_edits SET commit_digest = printf('%064d', 0) WHERE request_id = NEW.request_id; END;
            """);
        BridgeRequest request = Edit(fixture.Initial, 0, "Rollback after insert");
        var handler = new LayoutSubtitleBridgeRequestHandler(fixture.Service, new Authorization());
        using JsonDocument response = JsonDocument.Parse(await handler.HandleAsync(request));
        AssertUnavailable(response);
        Assert.AreSame(fixture.Initial, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreEqual(versions, fixture.VersionCount);
        fixture.Sql("DROP TRIGGER durable_subtitle_test_corruption;");
        _ = Available(fixture.Service.Execute(request, Actor, CancellationToken.None));
        Assert.AreEqual(1, fixture.Rows.Count);
    }

    [TestMethod]
    public void NoChangeInvalidCueAndUnrelatedMutationsNeverWrite()
    {
        using var fixture = new Fixture();
        string original = fixture.Initial.Subtitles[0].History.CurrentRevision.Snapshot.Text;
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NoChange,
            fixture.Service.Execute(Edit(fixture.Initial, 0, original), Actor, CancellationToken.None).Status);
        JsonObject wrongCue = JsonNode.Parse(Edit(fixture.Initial, 0, "Unknown cue").Parameters.GetRawText())!.AsObject();
        wrongCue["operation"]!["cueId"] = Guid.NewGuid().ToString("D");
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Invalid, fixture.Service.Execute(BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply,
            JsonSerializer.SerializeToElement(wrongCue)), Actor, CancellationToken.None).Status);
        wrongCue["operation"]!["kind"] = "editTerop";
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Unavailable, fixture.Service.Execute(BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply,
            JsonSerializer.SerializeToElement(wrongCue)), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Unavailable, fixture.Service.Execute(BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.Ping,
            JsonSerializer.SerializeToElement(new { })), Actor, CancellationToken.None).Status);
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreSame(fixture.Initial, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    public void CancellationBeforeRegistrationOrExecutionDoesNotPublishOrPersistAnything()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var unregistered = new DurableSubtitleEditService(fixture.Store);
        Assert.ThrowsExactly<OperationCanceledException>(() => unregistered.RegisterGenerated(fixture.Initial, fixture.InputDigest, cancellation.Token));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, unregistered.Execute(Read(fixture.Initial), Actor, CancellationToken.None).Status);
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Service.Execute(Edit(fixture.Initial, 0, "Canceled"), Actor, cancellation.Token));
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreSame(fixture.Initial, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    public void RegistrationRequiresAnExistingRootAndExactGeneratedInputWithoutResettingTheHead()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot missing = fixture.CreateGenerated(persist: false);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.RegisterGenerated(missing, fixture.InputDigest));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, fixture.Service.Execute(Read(missing), Actor, CancellationToken.None).Status);
        LayoutSubtitleBridgeSnapshot head = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "Keep this child"), Actor, CancellationToken.None));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.RegisterGenerated(fixture.Initial, Hash("wrong input")));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.RegisterGenerated(head, fixture.InputDigest));
        fixture.Service.RegisterGenerated(fixture.Initial, fixture.InputDigest);
        Assert.AreSame(head, fixture.Service.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id));
    }

    [TestMethod]
    [DataRow("reason")]
    [DataRow("actor")]
    [DataRow("timestamp")]
    public void RegistrationRejectsRootMetadataMismatchBeforeAnyJournalOrChild(string metadata)
    {
        using var fixture = new Fixture();
        CompositionVersion root = fixture.Initial.CompositionVersion;
        var changed = new CompositionVersion(root.Id, root.ProjectId, root.ParentVersionId,
            metadata == "actor" ? OtherActor : root.CreatedByActorId,
            metadata == "timestamp" ? root.CreatedAt.AddTicks(1) : root.CreatedAt,
            metadata == "reason" ? "Different generated root metadata" : root.ChangeReason,
            root.ContentDigest, root.Elements);
        LayoutSubtitleBridgeSnapshot supplied = fixture.Copy(fixture.Initial, compositionVersion: changed);
        Assert.AreEqual(root.ContentDigest, changed.ContentDigest);
        Assert.AreNotEqual(CompositionSnapshotJson.WriteVersion(root), CompositionSnapshotJson.WriteVersion(changed));
        long versions = fixture.VersionCount;
        var unregistered = new DurableSubtitleEditService(new SubtitleEditStore(fixture.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => unregistered.RegisterGenerated(supplied, fixture.InputDigest));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, unregistered.Execute(Read(supplied), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, unregistered.Execute(Edit(supplied, 0, "Must not commit"), Actor, CancellationToken.None).Status);
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreEqual(versions, fixture.VersionCount);

        unregistered.RegisterGenerated(fixture.Initial, fixture.InputDigest);
        _ = Available(unregistered.Execute(Edit(fixture.Initial, 0, "Validated root"), Actor, CancellationToken.None));
        Assert.AreEqual(Hash(CompositionSnapshotJson.WriteVersion(root)), fixture.Rows.Single().RootPayloadDigest);
        Assert.AreEqual(versions + 1, fixture.VersionCount);
    }

    [TestMethod]
    public async Task StoredRootMetadataChangedAfterRegistrationCannotReceiveAnAvailableAck()
    {
        using var fixture = new Fixture();
        long versions = fixture.VersionCount;
        JsonNode root = JsonNode.Parse(CompositionSnapshotJson.WriteVersion(fixture.Initial.CompositionVersion))!;
        root["changeReason"] = "Root metadata changed after registration";
        string changed = CanonicalJson.Canonicalize(root.ToJsonString());
        fixture.Sql("UPDATE composition_versions SET payload = $payload WHERE id = $root;",
            ("$payload", Encoding.UTF8.GetBytes(changed)), ("$root", fixture.Initial.CompositionVersion.Id.ToString()));
        Assert.AreEqual(changed, fixture.Store.ReadBranchState(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id).RootVersion.CanonicalJson);
        BridgeRequest request = Edit(fixture.Initial, 0, "Do not ACK a different root");
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Execute(request, Actor, CancellationToken.None));
        var handler = new LayoutSubtitleBridgeRequestHandler(fixture.Service, new Authorization());
        using JsonDocument response = JsonDocument.Parse(await handler.HandleAsync(request));
        AssertUnavailable(response);
        Assert.AreEqual(0, fixture.Rows.Count);
        Assert.AreEqual(versions, fixture.VersionCount);
    }

    [TestMethod]
    [DataRow("remove")]
    [DataRow("metadata")]
    [DataRow("key")]
    public void UnreferencedNativeCompositionChangesAreRejectedOnRegistrationAndRestartAfterCommit(string mutation)
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot initial = fixture.Initial;
        CompositionVersion known = fixture.KnownVersion;
        Assert.IsFalse(initial.Subtitles.SelectMany(binding => binding.History.Revisions)
            .Any(revision => revision.CompositionVersionId == known.Id));
        Assert.AreEqual(0, initial.Terops.Count);
        LayoutSubtitleBridgeSnapshot head = Available(fixture.Service.Execute(Edit(initial, 0, "Committed with complete native context"), Actor, CancellationToken.None));
        long count = fixture.VersionCount;
        var versions = new Dictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>(initial.CompositionVersions);
        switch (mutation)
        {
            case "remove": Assert.IsTrue(versions.Remove(known.Id)); break;
            case "metadata":
                versions[known.Id] = new CompositionVersion(known.Id, known.ProjectId, known.ParentVersionId,
                    known.CreatedByActorId, known.CreatedAt, "Different unreferenced version metadata", known.ContentDigest, known.Elements);
                Assert.AreEqual(known.ContentDigest, versions[known.Id].ContentDigest);
                break;
            case "key":
                Assert.IsTrue(versions.Remove(known.Id));
                versions.Add(EntityId<CompositionVersionIdTag>.New(), known);
                break;
            default: throw new AssertFailedException();
        }
        LayoutSubtitleBridgeSnapshot supplied = fixture.Copy(initial, compositionVersions: versions);
        Assert.AreEqual(WireResult(initial), WireResult(supplied));
        Assert.AreNotEqual(DurableSubtitleEditService.ComputeSnapshotDigest(initial), DurableSubtitleEditService.ComputeSnapshotDigest(supplied));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.RegisterGenerated(supplied, fixture.InputDigest));
        var restarted = new DurableSubtitleEditService(new SubtitleEditStore(fixture.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.RegisterGenerated(supplied, fixture.InputDigest));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, restarted.Execute(Read(initial), Actor, CancellationToken.None).Status);
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, restarted.Execute(Edit(supplied, 0, "Must not commit"), Actor, CancellationToken.None).Status);
        Assert.AreSame(head, fixture.Service.GetLatest(initial.ProjectId, initial.CompositionVersion.Id));
        Assert.AreSame(known, head.GetComposition(known.Id));
        Assert.AreEqual(1, fixture.Rows.Count);
        Assert.AreEqual(count, fixture.VersionCount);

        restarted.RegisterGenerated(fixture.Copy(initial), fixture.InputDigest);
        LayoutSubtitleBridgeSnapshot restored = restarted.GetLatest(initial.ProjectId, initial.CompositionVersion.Id);
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(head), DurableSubtitleEditService.ComputeSnapshotDigest(restored));
        Assert.AreSame(known, restored.GetComposition(known.Id));
    }

    [TestMethod]
    public void NativeCompositionProjectionIsReadOnlyAndItsDigestIgnoresDictionaryInsertionOrder()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot initial = fixture.Initial;
        IReadOnlyDictionary<EntityId<CompositionVersionIdTag>, CompositionVersion> projection = initial.CompositionVersions;
        Assert.AreEqual(3, projection.Count);
        Assert.AreSame(fixture.KnownVersion, projection[fixture.KnownVersion.Id]);
        var writable = (IDictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>)projection;
        Assert.ThrowsExactly<NotSupportedException>(() => writable.Remove(fixture.KnownVersion.Id));
        Assert.ThrowsExactly<NotSupportedException>(() => writable[fixture.KnownVersion.Id] = initial.CompositionVersion);
        var reversed = projection.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        LayoutSubtitleBridgeSnapshot reordered = fixture.Copy(initial, compositionVersions: reversed);
        reversed.Clear();
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(initial), DurableSubtitleEditService.ComputeSnapshotDigest(reordered));
        fixture.Service.RegisterGenerated(reordered, fixture.InputDigest);
        Assert.AreSame(initial, fixture.Service.GetLatest(initial.ProjectId, initial.CompositionVersion.Id));

        LayoutSubtitleBridgeSnapshot head = Available(fixture.Service.Execute(Edit(initial, 0, "Advance without mutating the projection"), Actor, CancellationToken.None));
        Assert.AreEqual(3, projection.Count);
        Assert.IsFalse(projection.ContainsKey(head.CompositionVersion.Id));
        Assert.IsTrue(head.CompositionVersions.ContainsKey(head.CompositionVersion.Id));
        LayoutSubtitleBridgeSnapshot restored = fixture.Restart(reordered).GetLatest(initial.ProjectId, initial.CompositionVersion.Id);
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(head), DurableSubtitleEditService.ComputeSnapshotDigest(restored));
    }

    [TestMethod]
    public void SnapshotDigestCoversHiddenHistoryReasonsAndNativeLayoutContext()
    {
        using var fixture = new Fixture();
        LayoutSubtitleCueBinding binding = fixture.Initial.Subtitles[0];
        SubtitleCueRevision revision = binding.History.CurrentRevision;
        SubtitleCueHistory changed = SubtitleService.CreateInitial(fixture.Initial.CompositionVersion, binding.History.SourceCandidate,
            revision.Snapshot.LayoutResolution, binding.History.CueId, revision.Id, revision.Snapshot.Cue.Range,
            Actor, revision.CreatedAt, "Different initial history reason");
        LayoutSubtitleBridgeSnapshot differentHistory = fixture.Copy(fixture.Initial,
            [binding.WithHistory(changed), fixture.Initial.Subtitles[1]]);
        Assert.AreEqual(WireResult(fixture.Initial), WireResult(differentHistory));
        Assert.AreNotEqual(DurableSubtitleEditService.ComputeSnapshotDigest(fixture.Initial), DurableSubtitleEditService.ComputeSnapshotDigest(differentHistory));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.RegisterGenerated(differentHistory, fixture.InputDigest));

        var differentContext = new LayoutSubtitleCueBinding(binding.History, binding.LayoutSpec, binding.LayoutProfile,
            [new SubtitleProhibitedRegion("native-region", FullRect())], []);
        LayoutSubtitleBridgeSnapshot context = fixture.Copy(fixture.Initial, [differentContext, fixture.Initial.Subtitles[1]]);
        Assert.AreEqual(WireResult(fixture.Initial), WireResult(context));
        Assert.AreNotEqual(DurableSubtitleEditService.ComputeSnapshotDigest(fixture.Initial), DurableSubtitleEditService.ComputeSnapshotDigest(context));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.RegisterGenerated(context, fixture.InputDigest));
    }

    [TestMethod]
    public void SnapshotDigestIncludesNativeInheritanceEvenWhenWireAndPublicHistoryAreIdentical()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot head = Available(fixture.Service.Execute(Edit(fixture.Initial, 0, "Advance A"), Actor, CancellationToken.None));
        LayoutSubtitleCueBinding b = head.Subtitles[1];
        SubtitleCueHistory inherited = head.WithInheritedComposition(b.History);
        LayoutSubtitleBridgeSnapshot withProof = fixture.Copy(head, [head.Subtitles[0], b.WithHistory(inherited)]);
        Assert.AreEqual(JsonSerializer.Serialize(b.History), JsonSerializer.Serialize(inherited));
        Assert.AreEqual(WireResult(head), WireResult(withProof));
        Assert.AreNotEqual(DurableSubtitleEditService.ComputeSnapshotDigest(head), DurableSubtitleEditService.ComputeSnapshotDigest(withProof));
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("allocation")]
    [DataRow("unusedAllocation")]
    [DataRow("result")]
    [DataRow("parent")]
    [DataRow("generated")]
    public async Task RehashedJournalForgeryStillFailsDeterministicReplayAndNeverReturnsAnAck(string corruption)
    {
        using var fixture = new Fixture();
        BridgeRequest request = Edit(fixture.Initial, 0, "Original journal text");
        _ = Available(fixture.Service.Execute(request, Actor, CancellationToken.None));
        StoredSubtitleEdit original = fixture.Rows.Single();
        fixture.RewriteCommitWithValidHashes(original, corruption);
        Assert.AreEqual(1, fixture.Rows.Count, "The store validates shape/hashes; the native service must additionally prove semantics.");
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Execute(request, Actor, CancellationToken.None));
        var restarted = new DurableSubtitleEditService(new SubtitleEditStore(fixture.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => restarted.RegisterGenerated(fixture.Initial, fixture.InputDigest));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, restarted.Execute(Read(fixture.Initial), Actor, CancellationToken.None).Status);
        var handler = new LayoutSubtitleBridgeRequestHandler(fixture.Service, new Authorization());
        using JsonDocument response = JsonDocument.Parse(await handler.HandleAsync(Read(fixture.Initial)));
        AssertUnavailable(response);
    }

    [TestMethod]
    public void CorruptionOfAnOlderRecordIsNotHiddenByCachedHeadOrExactRequestId()
    {
        using var fixture = new Fixture();
        BridgeRequest first = Edit(fixture.Initial, 0, "First");
        LayoutSubtitleBridgeSnapshot child = Available(fixture.Service.Execute(first, Actor, CancellationToken.None));
        _ = Available(fixture.Service.Execute(Edit(child, 1, "Second"), Actor, CancellationToken.None));
        fixture.Sql("DROP TRIGGER trg_subtitle_edits_update;");
        fixture.Sql("UPDATE subtitle_edits SET commit_digest = $digest WHERE request_id = $request;",
            ("$digest", Hash("corrupted older record").Hex), ("$request", first.RequestId.ToString("D")));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Execute(first, Actor, CancellationToken.None));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Capture(fixture.Initial.ProjectId, [fixture.Initial.CompositionVersion.Id]));
    }

    [TestMethod]
    public void CanonicalExponentRevisionReplaysWithoutChangingTheOriginalExpectedValue()
    {
        using var fixture = new Fixture();
        LayoutSubtitleBridgeSnapshot current = fixture.Initial;
        for (int index = 0; index < 11; index++)
        {
            BridgeRequest request = Edit(current, 0, $"Revision {index + 1}");
            using JsonDocument parameters = JsonDocument.Parse(CanonicalJson.Canonicalize(request.Parameters.GetRawText()));
            if (index == 10) { Assert.AreEqual("1e1", parameters.RootElement.GetProperty("expectedRevision").GetRawText()); }
            current = Available(fixture.Service.Execute(BridgeRequest.Create(request.RequestId, request.Method,
                parameters.RootElement.Clone()), Actor, CancellationToken.None));
        }
        DurableSubtitleEditService restarted = fixture.Restart(fixture.Initial);
        Assert.AreEqual(11, fixture.Rows.Count);
        Assert.AreEqual(DurableSubtitleEditService.ComputeSnapshotDigest(current), DurableSubtitleEditService.ComputeSnapshotDigest(
            restarted.GetLatest(fixture.Initial.ProjectId, fixture.Initial.CompositionVersion.Id)));
    }

    private static LayoutSubtitleBridgeSnapshot Available(LayoutSubtitleBridgeOperationResult result)
    {
        Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, result.Status);
        Assert.IsNotNull(result.Snapshot);
        return result.Snapshot;
    }

    private static void AssertUnavailable(JsonDocument response)
    {
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.AreEqual("unavailable", result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
    }

    private static BridgeRequest Edit(LayoutSubtitleBridgeSnapshot snapshot, int cueIndex, string text)
    {
        SubtitleCueHistory history = snapshot.Subtitles[cueIndex].History;
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
                reason = Reason,
                edit = new { kind = "text", text },
            },
        }));
    }

    private static BridgeRequest Read(LayoutSubtitleBridgeSnapshot snapshot) => BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutRead,
        JsonSerializer.SerializeToElement(new
        {
            projectId = snapshot.ProjectId.ToString(),
            evaluationTime = (object?)null,
            expectedVersionId = snapshot.CompositionVersion.Id.ToString(),
            expectedVersionDigest = snapshot.CompositionVersion.ContentDigest.Hex,
        }));

    private static BridgeRequest DefaultRead(EntityId<ProjectIdTag> project) => BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutRead,
        JsonSerializer.SerializeToElement(new { projectId = project.ToString(), evaluationTime = (object?)null }));

    private static string WireResult(LayoutSubtitleBridgeSnapshot snapshot)
    {
        using JsonDocument response = JsonDocument.Parse(LayoutSubtitleBridgeJson.WriteResponse(Read(snapshot), LayoutSubtitleBridgeOperationResult.Available(snapshot)));
        return CanonicalJson.Canonicalize(response.RootElement.GetProperty("result").GetRawText());
    }

    private static ContentDigest Hash(string value) => ContentDigest.Compute(Encoding.UTF8.GetBytes(value));
    private static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));
    private static NormalizedRect FullRect() => new(default, default, new RationalNumber(1, 1), new RationalNumber(1, 1));

    private sealed class Authorization : ILayoutSubtitleBridgeAuthorization
    {
        public string DemandRead() => Actor;
        public string DemandMutation() => Actor;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory;
        private readonly CompositionVersion ancestor;
        private readonly SubtitleCueCandidate candidate;
        private readonly LayoutSpec layout;
        private readonly SubtitleLayoutProfile profile;
        private readonly SubtitleLayoutResolution resolution;
        private readonly EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();

        internal Fixture()
        {
            directory = Path.Combine(Path.GetTempPath(), "HubRadio.DurableSubtitleEdits", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "store.db");
            DatabaseMigrationResult migration = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(directory, "backups"));
            Assert.IsTrue(migration.SchemaVersion >= 12, "The production schema-012 migration must be registered; do not fabricate a private test schema.");
            Projects = new ProjectStore(DatabasePath);
            var project = new Project(EntityId<ProjectIdTag>.New(), "Durable subtitle fixture", "Verify native replay", "Editors",
                PublicationScope.InternalOnly, null, [], []);
            Projects.CreateProject(project, HubRadio.App.Policy.ProjectSnapshotJson.WriteProject(project), At);
            var element = new CompositionElement(EntityId<CompositionElementIdTag>.New(), Range(0, 4), OriginRef.FromAsset(assetId));
            ancestor = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), project.Id, null, Actor, At,
                "Source composition fixture", ContentDigest.Compute(CompositionSnapshotJson.WriteElementsObjectToUtf8([element])), [element]);
            Projects.AppendCompositionVersion(ancestor, CompositionSnapshotJson.WriteVersion(ancestor));
            KnownVersion = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), project.Id, ancestor.Id, Actor, At.AddSeconds(1),
                "Known native version outside the edited cue history", ancestor.ContentDigest, ancestor.Elements);
            Projects.AppendCompositionVersion(KnownVersion, CompositionSnapshotJson.WriteVersion(KnownVersion));

            EntityId<TranscriptRevisionIdTag> transcriptId = EntityId<TranscriptRevisionIdTag>.New();
            EntityId<SourceSpanIdTag> spanId = EntityId<SourceSpanIdTag>.New();
            var token = new Token(EntityId<TranscriptTokenIdTag>.New(), "初期字幕。", Range(0, 2), null);
            var sentence = new Sentence(EntityId<TranscriptSentenceIdTag>.New(), token.Text, [token]);
            var utterance = new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), spanId, EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker", token.Text, Range(0, 2), null, [sentence]);
            var transcript = new TranscriptRevision(transcriptId, EntityId<TranscriptRevisionIdTag>.New(), TranscriptChangeSource.Human, At, [utterance]);
            var source = new SourceSpan(spanId, assetId, "video:0", Range(0, 2), Hash("synthetic source"), transcriptId, [token.Id]);
            candidate = SubtitleSegmenter.Segment(transcript, Hash(TranscriptSnapshotJson.WriteRevision(transcript)), [source]).Candidates.Single();
            layout = new LayoutSpec(EntityId<LayoutSpecIdTag>.New(), 9, 16,
                [new LayoutRegion("subtitle", new NormalizedRect(default, new RationalNumber(1, 2), new RationalNumber(1, 1), new RationalNumber(1, 2)), LayoutRegionRole.Subtitle)],
                [new SafeZone("output-frame-bounds", FullRect())]);
            profile = new SubtitleLayoutProfile(layout.Id, Hash("subtitle profile"), ["output-frame-bounds"],
                Enum.GetValues<SubtitleSoftConstraintKind>().Select(kind => new SubtitleSoftConstraintWeight(kind, 0)).ToArray());
            var placement = new SubtitlePlacementCandidate(EntityId<SubtitlePlacementCandidateIdTag>.New(), "subtitle",
                new SubtitleLayoutRect(default, new RationalNumber(3, 4), new RationalNumber(1, 1), new RationalNumber(1, 4)));
            resolution = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(candidate, layout, profile, [placement], [], [], null));
            Store = new SubtitleEditStore(DatabasePath);
            Initial = CreateGenerated();
            Service = new DurableSubtitleEditService(Store);
            Service.RegisterGenerated(Initial, InputDigest);
        }

        internal string DatabasePath { get; }
        internal ProjectStore Projects { get; }
        internal SubtitleEditStore Store { get; }
        internal DurableSubtitleEditService Service { get; }
        internal LayoutSubtitleBridgeSnapshot Initial { get; }
        internal CompositionVersion KnownVersion { get; }
        internal ContentDigest InputDigest { get; } = Hash("caller-validated generated input key");
        internal IReadOnlyList<StoredSubtitleEdit> Rows => Store.ReadBranch(Initial.ProjectId, Initial.CompositionVersion.Id);
        internal long VersionCount
        {
            get
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM composition_versions;";
                return (long)command.ExecuteScalar()!;
            }
        }

        internal LayoutSubtitleBridgeSnapshot CreateGenerated(bool persist = true)
        {
            var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), ancestor.ProjectId, ancestor.Id,
                Actor, At.AddSeconds(2), "Generated subtitle root fixture", ancestor.ContentDigest, ancestor.Elements);
            if (persist) { Projects.AppendCompositionVersion(version, CompositionSnapshotJson.WriteVersion(version)); }
            LayoutSubtitleCueBinding[] subtitles = Enumerable.Range(0, 2).Select(index => new LayoutSubtitleCueBinding(
                SubtitleService.CreateInitial(version, candidate, resolution, EntityId<CueIdTag>.New(), EntityId<SubtitleCueRevisionIdTag>.New(),
                    Range(index + 1, 1), Actor, At.AddMinutes(index + 1), "Actual initial cue fixture"), layout, profile, [], [])).ToArray();
            return new LayoutSubtitleBridgeSnapshot(version.ProjectId, 0, version, layout, 1080, 1920,
                [new LayoutSubtitleSourceBinding(assetId, OriginRef.FromAsset(assetId), new SourceDisplayGeometry(1080, 1920))], [], subtitles, [],
                [new LayoutSubtitleDisplayBinding(new SubtitleDisplayProfile(profile.VersionDigest, SubtitleCueDisplayMode.Full, SubtitlePunctuationDisplay.Preserve), [])],
                compositionVersions: new Dictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>
                {
                    [ancestor.Id] = ancestor,
                    [KnownVersion.Id] = KnownVersion,
                    [version.Id] = version,
                });
        }

        internal LayoutSubtitleBridgeSnapshot Copy(LayoutSubtitleBridgeSnapshot snapshot, IReadOnlyList<LayoutSubtitleCueBinding>? subtitles = null,
            CompositionVersion? compositionVersion = null,
            IReadOnlyDictionary<EntityId<CompositionVersionIdTag>, CompositionVersion>? compositionVersions = null)
        {
            return new LayoutSubtitleBridgeSnapshot(snapshot.ProjectId, snapshot.Revision, compositionVersion ?? snapshot.CompositionVersion, snapshot.OutputEnvelope,
                snapshot.OutputPixelWidth, snapshot.OutputPixelHeight, snapshot.Sources, snapshot.Templates, subtitles ?? snapshot.Subtitles,
                snapshot.Terops, snapshot.SubtitleDisplays, snapshot.ManualSelection, snapshot.Keyframes, snapshot.VariantPair,
                compositionVersions ?? snapshot.CompositionVersions, snapshot.SubtitleOnly);
        }

        internal DurableSubtitleEditService Restart(LayoutSubtitleBridgeSnapshot initial)
        {
            var restarted = new DurableSubtitleEditService(new SubtitleEditStore(DatabasePath));
            restarted.RegisterGenerated(initial, InputDigest);
            return restarted;
        }

        internal void RewriteCommitWithValidHashes(StoredSubtitleEdit row, string corruption)
        {
            JsonObject body = JsonNode.Parse(row.CommitCanonicalJson)!.AsObject();
            string? column = null;
            string? value = null;
            switch (corruption)
            {
                case "operation": body["operation"]!["edit"]!["text"] = "Rehashed but unexecuted operation"; break;
                case "allocation": body["allocatedIds"]![1] = Guid.NewGuid().ToString("D"); break;
                case "unusedAllocation": body["allocatedIds"]!.AsArray().Add(Guid.NewGuid().ToString("D")); break;
                case "result": body["resultSnapshotDigest"] = value = Hash("fake result").Hex; column = "result_snapshot_digest"; break;
                case "parent": body["parentSnapshotDigest"] = value = Hash("fake parent").Hex; column = "parent_snapshot_digest"; break;
                case "generated": body["generatedInputDigest"] = value = Hash("fake input").Hex; column = "generated_input_digest"; break;
                default: throw new AssertFailedException();
            }
            string canonical = CanonicalJson.Canonicalize(body.ToJsonString());
            ContentDigest commitDigest = Hash(canonical);
            ContentDigest recordDigest = Hash(CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                schema = "subtitle-edit.record.v1",
                sequence = row.Sequence,
                commitDigest = commitDigest.Hex,
                rootPayloadDigest = row.RootPayloadDigest.Hex,
                childPayloadDigest = row.ChildPayloadDigest.Hex,
                previousRecordDigest = row.PreviousRecordDigest?.Hex,
            })));
            Sql("DROP TRIGGER trg_subtitle_edits_update;");
            Sql("UPDATE subtitle_edits SET commit_payload = $payload, commit_digest = $commit, record_digest = $record WHERE request_id = $request;",
                ("$payload", Encoding.UTF8.GetBytes(canonical)), ("$commit", commitDigest.Hex), ("$record", recordDigest.Hex), ("$request", row.RequestId.ToString("D")));
            if (column is not null)
            {
                Sql($"UPDATE subtitle_edits SET {column} = $value WHERE request_id = $request;", ("$value", value!), ("$request", row.RequestId.ToString("D")));
            }
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