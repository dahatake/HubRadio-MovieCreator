using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

/// <summary>
/// Uses the real migrated StoreTestDatabase. Migration 012 must be registered by
/// the migration owner; these tests intentionally do not apply a private schema
/// or bypass migration validation to make an unintegrated store appear ready.
/// </summary>
[TestClass]
public sealed class SubtitleEditStoreTests
{
    [TestMethod]
    public void ReadBranchStateReturnsTheValidatedRootEvenWhenTheJournalIsEmpty()
    {
        using var fixture = new Fixture();
        StoredSubtitleEditBranchState initial = fixture.Store.ReadBranchState(fixture.Root.ProjectId, fixture.Root.Id);
        Assert.AreEqual(fixture.Root.Id, initial.RootVersion.Id);
        Assert.AreEqual(fixture.Root.ProjectId, initial.RootVersion.ProjectId);
        Assert.AreEqual(fixture.Root.ParentVersionId, initial.RootVersion.ParentVersionId);
        Assert.AreEqual(fixture.Root.ContentDigest, initial.RootVersion.ContentDigest);
        Assert.AreEqual(fixture.Root.CreatedAt, initial.RootVersion.CreatedAt);
        Assert.AreEqual(SourceOnlyExecutionTestSeed.VersionPayload(fixture.Root), initial.RootVersion.CanonicalJson);
        Assert.AreEqual(0, initial.Edits.Count);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StoredSubtitleEdit>)initial.Edits).Clear());
        Assert.ThrowsExactly<ArgumentNullException>(() => fixture.Store.ReadBranchState(null!, fixture.Root.Id));
        Assert.ThrowsExactly<ArgumentNullException>(() => fixture.Store.ReadBranchState(fixture.Root.ProjectId, null!));

        SubtitleEditCommit commit = fixture.Edit();
        StoredSubtitleEdit stored = fixture.Store.Commit(commit);
        StoredSubtitleEditBranchState current = fixture.Store.ReadBranchState(fixture.Root.ProjectId, fixture.Root.Id);
        Assert.AreEqual(initial.RootVersion.CanonicalJson, current.RootVersion.CanonicalJson);
        Assert.AreEqual(commit.ExpectedRootPayloadDigest, stored.RootPayloadDigest);
        Assert.AreEqual(CommitPayload(commit), stored.CommitCanonicalJson, "The persisted commit envelope has not changed.");
        Assert.AreEqual(stored.RecordDigest, current.Edits.Single().RecordDigest);
        Assert.AreEqual(0, initial.Edits.Count, "An earlier state must not advance after a commit.");
    }

    [TestMethod]
    [DataRow("shape")]
    [DataRow("created-at")]
    [DataRow("elements")]
    public void ReadBranchStateValidatesUnusedRootPayloadAndBindings(string mutation)
    {
        using var fixture = new Fixture();
        string original = SourceOnlyExecutionTestSeed.VersionPayload(fixture.Root);
        string payload = mutation switch
        {
            "shape" => "{}",
            "created-at" => ChangeJson(original, node => node["createdAt"] = fixture.Root.CreatedAt.AddTicks(1).ToString("O", CultureInfo.InvariantCulture)),
            "elements" => ChangeJson(original, node => node["elements"]![0]!["id"] = Guid.NewGuid().ToString("D")),
            _ => throw new AssertFailedException(),
        };
        fixture.Database.ExecuteNonQuery("UPDATE composition_versions SET payload = $payload WHERE id = $root;",
            ("$payload", Encoding.UTF8.GetBytes(payload)), ("$root", fixture.Root.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranchState(fixture.Root.ProjectId, fixture.Root.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id));
        Assert.AreEqual(0L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("elements-only")]
    [DataRow("metadata")]
    public void FirstCommitRequiresTheExactFullRootPayloadBeforeChildInsert(string expectation)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        ContentDigest expected = expectation switch
        {
            "missing" => null!,
            "elements-only" => fixture.Root.ContentDigest,
            "metadata" => Hash(ChangeJson(SourceOnlyExecutionTestSeed.VersionPayload(fixture.Root),
                node => node["changeReason"] = "Different generated root metadata")),
            _ => throw new AssertFailedException(),
        };
        Assert.AreNotEqual(commit.ExpectedRootPayloadDigest, expected);
        AssertRejectedBeforeChildInsert(fixture, commit with { ExpectedRootPayloadDigest = expected });
        Assert.AreEqual(0L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);
        Assert.AreEqual(commit.ExpectedRootPayloadDigest, fixture.Store.Commit(commit).RootPayloadDigest);
    }

    [TestMethod]
    public void CommitRechecksRootMetadataChangedAfterAnEarlierReadBeforeChildInsert()
    {
        using var fixture = new Fixture();
        StoredSubtitleEditBranchState observed = fixture.Store.ReadBranchState(fixture.Root.ProjectId, fixture.Root.Id);
        SubtitleEditCommit commit = fixture.Edit() with { ExpectedRootPayloadDigest = Hash(observed.RootVersion.CanonicalJson) };
        string changed = ChangeJson(observed.RootVersion.CanonicalJson, node => node["changeReason"] = "Changed after observation");
        fixture.Database.ExecuteNonQuery("UPDATE composition_versions SET payload = $payload WHERE id = $root;",
            ("$payload", Encoding.UTF8.GetBytes(changed)), ("$root", fixture.Root.Id.ToString()));

        StoredSubtitleEditBranchState current = fixture.Store.ReadBranchState(fixture.Root.ProjectId, fixture.Root.Id);
        Assert.AreEqual(changed, current.RootVersion.CanonicalJson);
        Assert.AreEqual(observed.RootVersion.ContentDigest, current.RootVersion.ContentDigest);
        Assert.AreNotEqual(commit.ExpectedRootPayloadDigest, Hash(current.RootVersion.CanonicalJson));
        AssertRejectedBeforeChildInsert(fixture, commit);
        Assert.AreEqual(0L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);

        SubtitleEditCommit currentExpectation = commit with { ExpectedRootPayloadDigest = Hash(current.RootVersion.CanonicalJson) };
        StoredSubtitleEdit recorded = fixture.Store.Commit(currentExpectation);
        Assert.AreEqual(currentExpectation.ExpectedRootPayloadDigest, recorded.RootPayloadDigest);
        _ = fixture.Store.Commit(fixture.Edit(currentExpectation.ChildVersion, 1, recorded.ResultSnapshotDigest)
            with
        { ExpectedRootPayloadDigest = currentExpectation.ExpectedRootPayloadDigest });
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(commit));
        Assert.AreEqual(recorded.RecordDigest, fixture.Store.Commit(currentExpectation).RecordDigest);
        Assert.AreEqual(2L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        Assert.AreEqual(fixture.InitialVersions + 2, fixture.VersionCount);
    }

    [TestMethod]
    public void FixedRuntimeChildReasonAndDifferentUserReasonRoundTripAndReplayExactly()
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        Assert.AreEqual("Apply a typed subtitle cue edit.", commit.ChildVersion.ChangeReason);
        using JsonDocument operation = JsonDocument.Parse(commit.OperationCanonicalJson);
        Assert.AreEqual("Subtitle correction", operation.RootElement.GetProperty("reason").GetString());
        Assert.AreNotEqual(commit.ChildVersion.ChangeReason, operation.RootElement.GetProperty("reason").GetString());

        StoredSubtitleEdit stored = fixture.Store.Commit(commit);
        var restarted = new SubtitleEditStore(fixture.Database.DatabasePath);
        Assert.AreEqual(commit.ChildCanonicalJson, restarted.ReadBranch(commit.ProjectId, commit.RootVersionId)[0].ChildCanonicalJson);
        Assert.AreEqual(commit.OperationCanonicalJson, stored.OperationCanonicalJson);
        Assert.AreEqual(stored.RecordDigest, restarted.Commit(commit).RecordDigest);
        SubtitleEditCommit changedReason = commit with
        {
            OperationCanonicalJson = ChangeJson(commit.OperationCanonicalJson, node => node["reason"] = "Correct a speaker name"),
        };
        InvalidOperationException conflict = Assert.ThrowsExactly<InvalidOperationException>(() => restarted.Commit(changedReason));
        Assert.AreEqual("The subtitle edit conflicts with the immutable branch or request.", conflict.Message);
        Assert.AreEqual(stored.RecordDigest, restarted.Commit(commit).RecordDigest);
        Assert.AreEqual(fixture.InitialVersions + 1, fixture.VersionCount);
        Assert.AreEqual(1L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
    }

    [TestMethod]
    public void TwoEditsSurviveRestartAndOlderExactReplayAfterTheBranchAdvances()
    {
        using var fixture = new Fixture();
        Assert.IsNotNull(fixture.Root.ParentVersionId, "The generated standard root need not be a composition ancestry root.");
        Assert.AreEqual(0, fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id).Count);
        SubtitleEditCommit firstCommit = fixture.Edit();
        StoredSubtitleEdit first = fixture.Store.Commit(firstCommit);
        SubtitleEditCommit secondCommit = fixture.Edit(firstCommit.ChildVersion, 1, first.ResultSnapshotDigest);
        StoredSubtitleEdit second = fixture.Store.Commit(secondCommit);
        var restarted = new SubtitleEditStore(fixture.Database.DatabasePath);
        IReadOnlyList<StoredSubtitleEdit> branch = restarted.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id);

        CollectionAssert.AreEqual(new[] { 1, 2 }, branch.Select(row => row.Sequence).ToArray());
        Assert.AreEqual(0, first.ExpectedSequence);
        Assert.AreEqual(1, second.ExpectedSequence);
        Assert.IsNull(first.PreviousRecordDigest);
        Assert.AreEqual(first.RecordDigest, second.PreviousRecordDigest);
        Assert.AreEqual(first.ChildVersionId, second.ParentVersionId);
        Assert.AreEqual(first.ResultSnapshotDigest, second.ParentSnapshotDigest);
        Assert.AreEqual(first.GeneratedInputDigest, second.GeneratedInputDigest);
        Assert.AreEqual(first.RootPayloadDigest, second.RootPayloadDigest);
        Assert.AreEqual(fixture.Root.ContentDigest, first.ChildContentDigest);
        Assert.AreEqual(first.ChildContentDigest, second.ChildContentDigest,
            "Identical composition elements must not turn real subtitle mutations into NoChange.");
        Assert.AreNotEqual(first.ResultSnapshotDigest, second.ResultSnapshotDigest);
        Assert.AreNotEqual(first.ChildPayloadDigest, first.ChildContentDigest);
        Assert.AreEqual(Hash(firstCommit.ChildCanonicalJson), first.ChildPayloadDigest);
        Assert.AreEqual(Hash(SourceOnlyExecutionTestSeed.VersionPayload(fixture.Root)), first.RootPayloadDigest);
        foreach ((SubtitleEditCommit commit, StoredSubtitleEdit stored) in new[] { (firstCommit, first), (secondCommit, second) })
        {
            Assert.AreEqual(commit.RequestId, stored.RequestId);
            Assert.AreEqual(commit.ChildCanonicalJson, stored.ChildCanonicalJson);
            Assert.AreEqual(commit.OperationCanonicalJson, stored.OperationCanonicalJson);
            Assert.AreEqual(commit.ActorSid, stored.ActorSid);
            Assert.AreEqual(commit.OccurredAt, stored.OccurredAt);
            CollectionAssert.AreEqual(commit.AllocatedIds.ToArray(), stored.AllocatedIds.ToArray());
            StoredSubtitleEdit replay = restarted.Commit(commit);
            Assert.AreEqual(stored.CommitCanonicalJson, replay.CommitCanonicalJson);
            Assert.AreEqual(stored.RecordDigest, replay.RecordDigest);
            Assert.AreEqual(stored.Sequence, replay.Sequence);
        }
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StoredSubtitleEdit>)branch).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<Guid>)branch[0].AllocatedIds).Clear());
        Assert.AreEqual(2L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        Assert.AreEqual(fixture.InitialVersions + 2, fixture.VersionCount);
        SubtitleEditStore.ValidateAll(fixture.Database.DatabasePath);
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("reason")]
    [DataRow("result")]
    [DataRow("generated")]
    [DataRow("parent-snapshot")]
    [DataRow("root-payload")]
    [DataRow("allocation")]
    [DataRow("sequence")]
    [DataRow("time")]
    [DataRow("actor")]
    [DataRow("child")]
    public void RequestIdReplayRequiresEveryOriginalCommitByte(string field)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        StoredSubtitleEdit stored = fixture.Store.Commit(commit);
        SubtitleEditCommit changed = field switch
        {
            "operation" => commit with
            {
                OperationCanonicalJson = ChangeJson(commit.OperationCanonicalJson,
                node => node["edit"]!["text"] = "Different subtitle")
            },
            "reason" => commit with
            {
                OperationCanonicalJson = ChangeJson(commit.OperationCanonicalJson,
                node => node["reason"] = "Different user reason")
            },
            "result" => commit with { ResultSnapshotDigest = Hash("different result") },
            "generated" => commit with { GeneratedInputDigest = Hash("different generation") },
            "parent-snapshot" => commit with { ParentSnapshotDigest = Hash("different parent snapshot") },
            "root-payload" => commit with { ExpectedRootPayloadDigest = Hash("different root payload") },
            "allocation" => commit with { AllocatedIds = [commit.ChildVersion.Id.Value, Guid.NewGuid(), Guid.NewGuid()] },
            "sequence" => commit with { ExpectedSequence = 1 },
            "time" => ReplaceChild(commit, CopyChild(commit.ChildVersion, at: commit.OccurredAt.AddTicks(1)))
                with
            { OccurredAt = commit.OccurredAt.AddTicks(1) },
            "actor" => ReplaceChild(commit, CopyChild(commit.ChildVersion, actor: "S-1-5-21-2000"))
                with
            { ActorSid = "S-1-5-21-2000" },
            "child" => fixture.Edit() with { RequestId = commit.RequestId },
            _ => throw new AssertFailedException(),
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(changed));
        Assert.AreEqual(stored.RecordDigest, fixture.Store.Commit(commit).RecordDigest);
        Assert.AreEqual(fixture.InitialVersions + 1, fixture.VersionCount);
    }

    [TestMethod]
    public void StaleSequenceAndWrongCurrentParentOrDigestsLeaveNoChild()
    {
        using var fixture = new Fixture();
        SubtitleEditCommit first = fixture.Edit();
        StoredSubtitleEdit stored = fixture.Store.Commit(first);
        SubtitleEditCommit next = fixture.Edit(first.ChildVersion, 1, stored.ResultSnapshotDigest);
        StoreConcurrencyException stale = Assert.ThrowsExactly<StoreConcurrencyException>(() =>
            fixture.Store.Commit(next with { ExpectedSequence = 0 }));
        Assert.AreEqual(0, stale.ExpectedRevision);
        Assert.AreEqual(1, stale.ActualRevision);
        foreach (SubtitleEditCommit invalid in new[]
        {
            fixture.Edit(sequence: 1, parentSnapshot: stored.ResultSnapshotDigest),
            next with { ParentSnapshotDigest = Hash("wrong snapshot") },
            next with { GeneratedInputDigest = Hash("wrong generated input") },
            next with { ExpectedRootPayloadDigest = Hash("wrong root payload") },
        })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(invalid));
            Assert.IsNull(new ProjectStore(fixture.Database.DatabasePath).GetCompositionVersion(invalid.ChildVersion.Id));
        }
        Assert.AreEqual(fixture.InitialVersions + 1, fixture.VersionCount);
        Assert.AreEqual(2, fixture.Store.Commit(next).Sequence);
    }

    [TestMethod]
    public void MissingCrossProjectAndKnownSubtitleChildRootsAreNotNewBranches()
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(
            commit with { RootVersionId = EntityId<CompositionVersionIdTag>.New() }));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranchState(fixture.Root.ProjectId, EntityId<CompositionVersionIdTag>.New()));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranch(EntityId<ProjectIdTag>.New(), fixture.Root.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranchState(EntityId<ProjectIdTag>.New(), fixture.Root.Id));
        var other = new SourceOnlyExecutionTestSeed(fixture.Database.DatabasePath);
        _ = other.Finalize();
        CompositionVersion foreign = other.AppendStandardVersion();
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(commit with { RootVersionId = foreign.Id }));
        _ = fixture.Store.Commit(commit);
        SubtitleEditCommit inventedRoot = fixture.Edit(commit.ChildVersion) with { RootVersionId = commit.ChildVersion.Id };
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(inventedRoot));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranch(fixture.Root.ProjectId, commit.ChildVersion.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranchState(fixture.Root.ProjectId, commit.ChildVersion.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(
            fixture.Edit() with { RequestId = commit.RequestId, RootVersionId = foreign.Id }));
        Assert.AreEqual(1L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
    }

    [TestMethod]
    [DataRow("digest")]
    [DataRow("typed-elements")]
    [DataRow("changed-elements")]
    [DataRow("canonical")]
    [DataRow("duplicate")]
    [DataRow("fractional-time")]
    [DataRow("actor")]
    [DataRow("payload-actor")]
    [DataRow("typed-reason")]
    [DataRow("payload-reason")]
    [DataRow("timestamp")]
    [DataRow("non-utc")]
    [DataRow("same-snapshot")]
    public void InvalidCanonicalCoreOrSubtitleOnlyBindingsAreRejectedBeforeWriting(string field)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        CompositionVersion child = commit.ChildVersion;
        CompositionElement original = child.Elements[0];
        CompositionElement altered = new(EntityId<CompositionElementIdTag>.New(), original.OutputRange,
            original.Origin, original.AdoptedSuggestion, original.SourceRange);
        CompositionVersion changedElements = new(child.Id, child.ProjectId, child.ParentVersionId,
            child.CreatedByActorId, child.CreatedAt, child.ChangeReason, child.ContentDigest, [altered]);
        SubtitleEditCommit invalid = field switch
        {
            "digest" => ReplaceChild(commit, CopyChild(child, digest: Hash("forged domain digest"))),
            "typed-elements" => commit with { ChildVersion = changedElements },
            "changed-elements" => RecomputeElementsDigest(ReplaceChild(commit, changedElements)),
            "canonical" => commit with { ChildCanonicalJson = " " + commit.ChildCanonicalJson },
            "duplicate" => commit with { ChildCanonicalJson = commit.ChildCanonicalJson[..^1] + ",\"id\":\"" + child.Id + "\"}" },
            "fractional-time" => commit with
            {
                ChildCanonicalJson = ChangeJson(commit.ChildCanonicalJson,
                node => node["elements"]![0]!["outputRange"]!["duration"]!["value"] = JsonNode.Parse("10.000000000000000000000000000001"))
            },
            "actor" => commit with { ActorSid = "S-1-5-21-2000" },
            "payload-actor" => commit with
            {
                ChildCanonicalJson = ChangeJson(commit.ChildCanonicalJson,
                node => node["createdByActorId"] = "S-1-5-21-2000")
            },
            "typed-reason" => commit with { ChildVersion = CopyChild(child, reason: "Different Core reason") },
            "payload-reason" => commit with
            {
                ChildCanonicalJson = ChangeJson(commit.ChildCanonicalJson,
                node => node["changeReason"] = "Different payload reason")
            },
            "timestamp" => commit with { OccurredAt = commit.OccurredAt.AddTicks(1) },
            "non-utc" => commit with { OccurredAt = commit.OccurredAt.ToOffset(TimeSpan.FromHours(1)) },
            "same-snapshot" => commit with { ResultSnapshotDigest = commit.ParentSnapshotDigest },
            _ => throw new AssertFailedException(),
        };
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(invalid));
        Assert.IsNull(error.InnerException);
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);
        Assert.AreEqual(0, fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id).Count);
    }

    [TestMethod]
    [DataRow("kind")]
    [DataRow("extra")]
    [DataRow("missing")]
    [DataRow("cue-id")]
    [DataRow("revision-id")]
    [DataRow("revision-digest")]
    [DataRow("reason")]
    [DataRow("reason-empty")]
    [DataRow("reason-trim")]
    [DataRow("reason-nul")]
    [DataRow("reason-null")]
    [DataRow("edit-type")]
    [DataRow("duplicate")]
    [DataRow("unicode")]
    [DataRow("noncanonical")]
    [DataRow("oversize")]
    public void OperationEnvelopeIsClosedCanonicalBoundedAndStrictUtf8(string mutation)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        string operation = mutation switch
        {
            "kind" => ChangeJson(commit.OperationCanonicalJson, node => node["kind"] = "editTerop"),
            "extra" => ChangeJson(commit.OperationCanonicalJson, node => node["extra"] = true),
            "missing" => ChangeJson(commit.OperationCanonicalJson, node => node.AsObject().Remove("expectedRevisionId")),
            "cue-id" => ChangeJson(commit.OperationCanonicalJson, node => node["cueId"] = "00000000-0000-0000-0000-000000000000"),
            "revision-id" => ChangeJson(commit.OperationCanonicalJson, node => node["expectedRevisionId"] = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"),
            "revision-digest" => ChangeJson(commit.OperationCanonicalJson, node => node["expectedRevisionDigest"] = new string('A', 64)),
            "reason" => ChangeJson(commit.OperationCanonicalJson, node => node["reason"] = " "),
            "reason-empty" => ChangeJson(commit.OperationCanonicalJson, node => node["reason"] = ""),
            "reason-trim" => ChangeJson(commit.OperationCanonicalJson, node => node["reason"] = " Padded reason "),
            "reason-nul" => ChangeJson(commit.OperationCanonicalJson, node => node["reason"] = "Invalid\0reason"),
            "reason-null" => ChangeJson(commit.OperationCanonicalJson, node => node["reason"] = null),
            "edit-type" => ChangeJson(commit.OperationCanonicalJson, node => node["edit"] = false),
            "duplicate" => commit.OperationCanonicalJson.Replace("\"kind\":\"text\"", "\"kind\":\"text\",\"kind\":\"text\"", StringComparison.Ordinal),
            "unicode" => "{\"x\":\"\ud800\"}",
            "noncanonical" => " " + commit.OperationCanonicalJson,
            "oversize" => ChangeJson(commit.OperationCanonicalJson, node => node["edit"]!["text"] = new string('x', SubtitleEditStore.MaximumOperationUtf8Bytes)),
            _ => throw new AssertFailedException(),
        };
        Assert.AreNotEqual(commit.OperationCanonicalJson, operation);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Store.Commit(commit with { OperationCanonicalJson = operation }));
        Assert.IsNull(error.InnerException);
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);
    }

    [TestMethod]
    [DataRow("first")]
    [DataRow("empty")]
    [DataRow("zero")]
    [DataRow("duplicate")]
    [DataRow("too-many")]
    [DataRow("old-version")]
    public void RuntimeAllocationOrderBoundsAndFreshnessAreEnforced(string mutation)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        Guid[] ids = mutation switch
        {
            "first" => [Guid.NewGuid(), commit.ChildVersion.Id.Value],
            "empty" => [],
            "zero" => [commit.ChildVersion.Id.Value, Guid.Empty],
            "duplicate" => [commit.ChildVersion.Id.Value, commit.ChildVersion.Id.Value],
            "too-many" => [commit.ChildVersion.Id.Value, .. Enumerable.Range(0, 8).Select(_ => Guid.NewGuid())],
            "old-version" => [commit.ChildVersion.Id.Value, fixture.Root.ParentVersionId!.Value],
            _ => throw new AssertFailedException(),
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(commit with { AllocatedIds = ids }));
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);
    }

    [TestMethod]
    public void ActualAllocationsAreDetachedAndCannotBeReusedAndPreexistingChildIsRejected()
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        Guid[] supplied = commit.AllocatedIds.ToArray();
        commit = commit with { AllocatedIds = supplied };
        StoredSubtitleEdit first = fixture.Store.Commit(commit);
        Guid consumed = supplied[1];
        supplied[1] = Guid.NewGuid();
        Assert.AreEqual(consumed, first.AllocatedIds[1]);
        SubtitleEditCommit next = fixture.Edit(commit.ChildVersion, 1, first.ResultSnapshotDigest);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(
            next with { AllocatedIds = [next.ChildVersion.Id.Value, consumed] }));
        new ProjectStore(fixture.Database.DatabasePath).AppendCompositionVersion(next.ChildVersion, next.ChildCanonicalJson);
        long count = fixture.VersionCount;
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(next));
        Assert.AreEqual(count, fixture.VersionCount);
        Assert.AreEqual(1L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
    }

    [TestMethod]
    [DataRow("child")]
    [DataRow("journal")]
    [DataRow("after-write")]
    public void AnyInsertOrPostWriteValidationFailureRollsBackChildAndJournal(string stage)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        if (stage == "after-write")
        {
            fixture.Database.ExecuteNonQuery("""
                DROP TRIGGER trg_subtitle_edits_update;
                CREATE TRIGGER fail_subtitle_test AFTER INSERT ON subtitle_edits
                BEGIN UPDATE subtitle_edits SET commit_digest = printf('%064d', 0) WHERE request_id = NEW.request_id; END;
                """);
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(commit));
        }
        else
        {
            string table = stage == "child" ? "composition_versions" : "subtitle_edits";
            fixture.Database.ExecuteNonQuery($"""
                CREATE TRIGGER fail_subtitle_test BEFORE INSERT ON {table}
                BEGIN SELECT RAISE(ABORT, 'injected subtitle failure'); END;
                """);
            Assert.ThrowsExactly<SqliteException>(() => fixture.Store.Commit(commit));
        }
        Assert.AreEqual(fixture.InitialVersions, fixture.VersionCount);
        Assert.IsNull(new ProjectStore(fixture.Database.DatabasePath).GetCompositionVersion(commit.ChildVersion.Id));
        Assert.AreEqual(0L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        fixture.Database.ExecuteNonQuery("DROP TRIGGER fail_subtitle_test;");
        Assert.AreEqual(1, fixture.Store.Commit(commit).Sequence);
    }

    [TestMethod]
    [DataRow("journal", "update")]
    [DataRow("journal", "delete")]
    [DataRow("journal", "replace")]
    [DataRow("child", "update")]
    [DataRow("child", "delete")]
    [DataRow("child", "replace")]
    [DataRow("root", "update")]
    [DataRow("root", "delete")]
    [DataRow("root", "replace")]
    public void SqlImmutabilityIncludesReplaceWithRecursiveTriggersOff(string target, string action)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        StoredSubtitleEdit stored = fixture.Store.Commit(commit);
        Assert.AreEqual(0L, fixture.Database.ExecuteScalarInt64("PRAGMA recursive_triggers;"));
        string table = target == "journal" ? "subtitle_edits" : "composition_versions";
        string key = target == "journal" ? "request_id" : "id";
        string id = target == "journal" ? stored.RequestId.ToString("D") :
            target == "root" ? fixture.Root.Id.ToString() : stored.ChildVersionId.ToString();
        string sql = action switch
        {
            "update" => $"UPDATE {table} SET {key} = {key} WHERE {key} = $id;",
            "delete" => $"DELETE FROM {table} WHERE {key} = $id;",
            "replace" => $"INSERT OR REPLACE INTO {table} SELECT * FROM {table} WHERE {key} = $id;",
            _ => throw new AssertFailedException(),
        };
        SqliteException exception = Assert.ThrowsExactly<SqliteException>(() => fixture.Database.ExecuteNonQuery(sql, ("$id", id)));
        Assert.AreEqual(1811, exception.SqliteExtendedErrorCode, "The owned immutability trigger must reject the mutation.");
        Assert.AreEqual(stored.RecordDigest, fixture.Store.Commit(commit).RecordDigest);
        Assert.AreEqual(fixture.InitialVersions + 1, fixture.VersionCount);
    }

    [TestMethod]
    [DataRow("first-journal")]
    [DataRow("last-journal")]
    [DataRow("previous-digest")]
    [DataRow("gap")]
    [DataRow("utf8")]
    [DataRow("noncanonical")]
    [DataRow("duplicate")]
    [DataRow("first-child")]
    [DataRow("last-child")]
    [DataRow("root")]
    public void ReadsValidateEveryRecordAndChildEvenWhenSqlGuardsWereRemoved(string mutation)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit firstCommit = fixture.Edit();
        StoredSubtitleEdit first = fixture.Store.Commit(firstCommit);
        StoredSubtitleEdit last = fixture.Store.Commit(fixture.Edit(firstCommit.ChildVersion, 1, first.ResultSnapshotDigest));
        fixture.Database.ExecuteNonQuery("DROP TRIGGER trg_subtitle_edits_update; DROP TRIGGER trg_subtitle_edits_version_update;");
        string sql = mutation switch
        {
            "first-journal" or "last-journal" => "UPDATE subtitle_edits SET commit_digest = $digest WHERE request_id = $request;",
            "previous-digest" => "UPDATE subtitle_edits SET previous_record_digest = $digest WHERE request_id = $request;",
            "gap" => "UPDATE subtitle_edits SET sequence = 3 WHERE request_id = $request;",
            "utf8" or "noncanonical" or "duplicate" => "UPDATE subtitle_edits SET commit_payload = $payload WHERE request_id = $request;",
            "first-child" or "last-child" or "root" => "UPDATE composition_versions SET payload = $payload WHERE id = $version;",
            _ => throw new AssertFailedException(),
        };
        Guid request = mutation == "first-journal" ? first.RequestId : last.RequestId;
        EntityId<CompositionVersionIdTag> versionId = mutation == "root" ? fixture.Root.Id :
            mutation == "first-child" ? first.ChildVersionId : last.ChildVersionId;
        byte[] payload = mutation switch
        {
            "utf8" => [0xff, 0xfe],
            "noncanonical" => Encoding.UTF8.GetBytes(" " + last.CommitCanonicalJson),
            "duplicate" => Encoding.UTF8.GetBytes(last.CommitCanonicalJson[..^1] + ",\"schema\":\"subtitle-edit.commit.v1\"}"),
            _ => Encoding.UTF8.GetBytes("{}"),
        };
        fixture.Database.ExecuteNonQuery(sql, ("$digest", Hash("corrupted").Hex), ("$request", request.ToString("D")),
            ("$payload", payload), ("$version", versionId.ToString()));
        AssertUnreadable(fixture, firstCommit);
    }

    [TestMethod]
    [DataRow("tiny-fraction")]
    [DataRow("extra")]
    [DataRow("operation-kind")]
    [DataRow("allocation-order")]
    [DataRow("parent-snapshot")]
    [DataRow("generated-input")]
    [DataRow("actor")]
    [DataRow("timestamp")]
    public void RecomputedHashesCannotHideInvalidPayloadOrBrokenSemanticLinks(string mutation)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit firstCommit = fixture.Edit();
        StoredSubtitleEdit first = fixture.Store.Commit(firstCommit);
        StoredSubtitleEdit last = fixture.Store.Commit(fixture.Edit(firstCommit.ChildVersion, 1, first.ResultSnapshotDigest));
        fixture.Database.ExecuteNonQuery("DROP TRIGGER trg_subtitle_edits_update;");
        JsonNode body = JsonNode.Parse(last.CommitCanonicalJson)!;
        string? column = null;
        string? value = null;
        switch (mutation)
        {
            case "tiny-fraction": body["expectedSequence"] = JsonNode.Parse("1.000000000000000000000000000001"); break;
            case "extra": body["extra"] = true; break;
            case "operation-kind": body["operation"]!["kind"] = "editTerop"; break;
            case "allocation-order": body["allocatedIds"]![0] = Guid.NewGuid().ToString("D"); break;
            case "parent-snapshot":
                body["parentSnapshotDigest"] = value = Hash("disconnected parent snapshot").Hex;
                column = "parent_snapshot_digest";
                break;
            case "generated-input":
                body["generatedInputDigest"] = value = Hash("different root input").Hex;
                column = "generated_input_digest";
                break;
            case "actor": body["actorSid"] = value = "S-1-5-21-2000"; column = "actor_sid"; break;
            case "timestamp":
                body["occurredAt"] = value = last.OccurredAt.AddTicks(1).ToString("O", CultureInfo.InvariantCulture);
                column = "occurred_at";
                break;
            default: throw new AssertFailedException();
        }
        string canonical = CanonicalJson.Canonicalize(body.ToJsonString());
        ContentDigest commitDigest = Hash(canonical);
        ContentDigest recordDigest = Hash(CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "subtitle-edit.record.v1",
            sequence = last.Sequence,
            commitDigest = commitDigest.Hex,
            rootPayloadDigest = last.RootPayloadDigest.Hex,
            childPayloadDigest = last.ChildPayloadDigest.Hex,
            previousRecordDigest = last.PreviousRecordDigest!.Hex,
        })));
        fixture.Database.ExecuteNonQuery("""
            UPDATE subtitle_edits SET commit_payload = $payload, commit_digest = $commit, record_digest = $record
            WHERE request_id = $request;
            """, ("$payload", Encoding.UTF8.GetBytes(canonical)), ("$commit", commitDigest.Hex),
            ("$record", recordDigest.Hex), ("$request", last.RequestId.ToString("D")));
        if (column is not null)
        {
            fixture.Database.ExecuteNonQuery($"UPDATE subtitle_edits SET {column} = $value WHERE request_id = $request;",
                ("$value", value), ("$request", last.RequestId.ToString("D")));
        }
        AssertUnreadable(fixture, firstCommit);
    }

    [TestMethod]
    public void CanonicalIntegerExponentSequencesAreReadExactlyAndHistoricalRightsAreNotReauthorized()
    {
        using var fixture = new Fixture();
        CompositionVersion parent = fixture.Root;
        ContentDigest snapshot = fixture.InitialSnapshot;
        SubtitleEditCommit? first = null;
        StoredSubtitleEdit? last = null;
        for (int sequence = 0; sequence <= 10; sequence++)
        {
            SubtitleEditCommit commit = fixture.Edit(parent, sequence, snapshot);
            first ??= commit;
            last = fixture.Store.Commit(commit);
            parent = commit.ChildVersion;
            snapshot = last.ResultSnapshotDigest;
        }
        using JsonDocument document = JsonDocument.Parse(last!.CommitCanonicalJson);
        Assert.AreEqual("1e1", document.RootElement.GetProperty("expectedSequence").GetRawText());
        // This store must not reauthorize an old event using the mutable project row.
        fixture.Database.ExecuteNonQuery("UPDATE projects SET publication_scope = 'ChangedAfterAcceptance' WHERE id = $project;",
            ("$project", fixture.Root.ProjectId.ToString()));
        Assert.AreEqual(11, fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id).Count);
        Assert.AreEqual(1, fixture.Store.Commit(first!).Sequence);
        SubtitleEditStore.ValidateAll(fixture.Database.DatabasePath);
    }

    [TestMethod]
    public void BranchEditMaximumAllowsTheLastEditAndExactReplaysButRejectsTheNextBeforeChildInsert()
    {
        using var fixture = new Fixture();
        (SubtitleEditCommit first, SubtitleEditCommit parent) = fixture.SeedBranch(SubtitleEditStore.MaximumBranchEdits - 1);
        Assert.AreEqual(SubtitleEditStore.MaximumBranchEdits - 1,
            fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id).Count);
        SubtitleEditCommit last = fixture.Edit(parent.ChildVersion, SubtitleEditStore.MaximumBranchEdits - 1, parent.ResultSnapshotDigest);
        StoredSubtitleEdit stored = fixture.Store.Commit(last);
        Assert.AreEqual(128, stored.Sequence);
        Assert.IsTrue(stored.Sequence < SourceOnlyExecutionStore.MaximumVersionLineageLength);
        Assert.AreEqual(SubtitleEditStore.MaximumBranchEdits,
            fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id).Count);
        Assert.AreEqual(1, fixture.Store.Commit(first).Sequence);
        Assert.AreEqual(stored.RecordDigest, fixture.Store.Commit(last).RecordDigest);
        SubtitleEditCommit next = fixture.Edit(last.ChildVersion, SubtitleEditStore.MaximumBranchEdits, last.ResultSnapshotDigest);
        AssertRejectedBeforeChildInsert(fixture, next);
        Assert.AreEqual(fixture.InitialVersions + SubtitleEditStore.MaximumBranchEdits, fixture.VersionCount);
        Assert.AreEqual((long)SubtitleEditStore.MaximumBranchEdits,
            fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        SubtitleEditStore.ValidateAll(fixture.Database.DatabasePath);
    }

    [TestMethod]
    public void CumulativePayloadMaximumAllowsExactBytesAndRejectsOneExtraByteBeforeChildInsert()
    {
        using var fixture = new Fixture();
        const int commitBytes = 4 * 1024 * 1024;
        (SubtitleEditCommit first, SubtitleEditCommit parent) = fixture.SeedBranch(7, commitBytes);
        Assert.AreEqual(7L * commitBytes,
            fixture.Database.ExecuteScalarInt64("SELECT SUM(length(commit_payload)) FROM subtitle_edits;"));
        SubtitleEditCommit last = WithCommitPayloadBytes(fixture.Edit(parent.ChildVersion, 7, parent.ResultSnapshotDigest), commitBytes);
        SubtitleEditCommit tooLarge = ReplaceChild(last, CopyChild(last.ChildVersion, reason: last.ChildVersion.ChangeReason + "x"));
        Assert.AreEqual(commitBytes + 1, Encoding.UTF8.GetByteCount(CommitPayload(tooLarge)));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(tooLarge.ChildCanonicalJson) < SubtitleEditStore.MaximumChildUtf8Bytes);
        AssertRejectedBeforeChildInsert(fixture, tooLarge);
        Assert.AreEqual(fixture.InitialVersions + 7, fixture.VersionCount);

        StoredSubtitleEdit stored = fixture.Store.Commit(last);
        Assert.AreEqual(32L * 1024 * 1024,
            fixture.Database.ExecuteScalarInt64("SELECT SUM(length(commit_payload)) FROM subtitle_edits;"));
        var restarted = new SubtitleEditStore(fixture.Database.DatabasePath);
        Assert.AreEqual(8, restarted.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id).Count);
        Assert.AreEqual(1, restarted.Commit(first).Sequence);
        Assert.AreEqual(stored.RecordDigest, restarted.Commit(last).RecordDigest);
        AssertRejectedBeforeChildInsert(fixture, fixture.Edit(last.ChildVersion, 8, last.ResultSnapshotDigest));
        Assert.AreEqual(fixture.InitialVersions + 8, fixture.VersionCount);
        Assert.AreEqual(8L, fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
    }

    [TestMethod]
    [DataRow("count")]
    [DataRow("bytes")]
    public void OtherwiseValidPersistedBranchOneOverEitherMaximumIsNeverPartiallyRead(string budget)
    {
        using var fixture = new Fixture();
        // Use the migrated schema and its real triggers; no limit overrides or
        // invalid JSON/hashes that could mask a missing branch-budget check.
        (SubtitleEditCommit first, _) = budget == "count"
            ? fixture.SeedBranch(SubtitleEditStore.MaximumBranchEdits + 1)
            : fixture.SeedBranch(8, 4 * 1024 * 1024, finalExtraBytes: 1);
        if (budget == "count")
        {
            Assert.AreEqual(SubtitleEditStore.MaximumBranchEdits + 1L,
                fixture.Database.ExecuteScalarInt64("SELECT COUNT(*) FROM subtitle_edits;"));
        }
        else
        {
            Assert.AreEqual(SubtitleEditStore.MaximumBranchPayloadBytes + 1L,
                fixture.Database.ExecuteScalarInt64("SELECT SUM(length(commit_payload)) FROM subtitle_edits;"));
        }
        AssertUnreadable(fixture, first);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(SubtitleEditStore.MaximumCommitUtf8Bytes + 1)]
    public void EveryPersistedCommitLengthRemainsBoundedEvenBelowTheCumulativeMaximum(int length)
    {
        using var fixture = new Fixture();
        SubtitleEditCommit commit = fixture.Edit();
        _ = fixture.Store.Commit(commit);
        fixture.Database.ExecuteNonQuery("""
            DROP TRIGGER trg_subtitle_edits_update;
            PRAGMA ignore_check_constraints = ON;
            UPDATE subtitle_edits SET commit_payload = zeroblob($length);
            """, ("$length", length));
        AssertUnreadable(fixture, commit);
    }

    private static void AssertRejectedBeforeChildInsert(Fixture fixture, SubtitleEditCommit commit)
    {
        // An attempted insert raises SqliteException, so InvalidOperationException
        // proves preflight rejection rather than a later rollback after inserting.
        fixture.Database.ExecuteNonQuery("""
            CREATE TRIGGER fail_subtitle_budget_test BEFORE INSERT ON composition_versions
            BEGIN SELECT RAISE(ABORT, 'budget preflight must run before child insert'); END;
            """);
        try
        {
            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(commit));
            Assert.IsNull(error.InnerException);
            Assert.IsNull(new ProjectStore(fixture.Database.DatabasePath).GetCompositionVersion(commit.ChildVersion.Id));
        }
        finally { fixture.Database.ExecuteNonQuery("DROP TRIGGER fail_subtitle_budget_test;"); }
    }

    private static string CommitPayload(SubtitleEditCommit commit)
    {
        using JsonDocument child = JsonDocument.Parse(commit.ChildCanonicalJson);
        using JsonDocument operation = JsonDocument.Parse(commit.OperationCanonicalJson);
        return CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = "subtitle-edit.commit.v1",
            requestId = commit.RequestId.ToString("D"),
            projectId = commit.ProjectId.ToString(),
            rootVersionId = commit.RootVersionId.ToString(),
            parentVersionId = commit.ParentVersionId.ToString(),
            childVersion = child.RootElement,
            expectedSequence = commit.ExpectedSequence,
            generatedInputDigest = commit.GeneratedInputDigest.Hex,
            parentSnapshotDigest = commit.ParentSnapshotDigest.Hex,
            resultSnapshotDigest = commit.ResultSnapshotDigest.Hex,
            operation = operation.RootElement,
            allocatedIds = commit.AllocatedIds.Select(id => id.ToString("D")).ToArray(),
            actorSid = commit.ActorSid,
            occurredAt = commit.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
        }));
    }

    private static SubtitleEditCommit WithCommitPayloadBytes(SubtitleEditCommit commit, int bytes)
    {
        // A valid synthetic Core reason supplies cheap ASCII padding, avoiding
        // enormous element arrays. Ordinary fixtures use the actual fixed reason.
        int padding = bytes - Encoding.UTF8.GetByteCount(CommitPayload(commit));
        Assert.IsTrue(padding >= 0);
        SubtitleEditCommit padded = ReplaceChild(commit, CopyChild(commit.ChildVersion,
            reason: commit.ChildVersion.ChangeReason + new string('x', padding)));
        Assert.AreEqual(bytes, Encoding.UTF8.GetByteCount(CommitPayload(padded)));
        Assert.IsTrue(bytes <= SubtitleEditStore.MaximumCommitUtf8Bytes);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(padded.ChildCanonicalJson) <= SubtitleEditStore.MaximumChildUtf8Bytes);
        return padded;
    }

    private static void AssertUnreadable(Fixture fixture, SubtitleEditCommit exactOldRequest)
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Store.ReadBranch(fixture.Root.ProjectId, fixture.Root.Id));
        Assert.IsNull(error.InnerException);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.ReadBranchState(fixture.Root.ProjectId, fixture.Root.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => SubtitleEditStore.ValidateAll(fixture.Database.DatabasePath));
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.Commit(exactOldRequest));
    }

    private static CompositionVersion CopyChild(CompositionVersion child, DateTimeOffset? at = null,
        string? actor = null, ContentDigest? digest = null, string? reason = null) => new(child.Id, child.ProjectId, child.ParentVersionId,
        actor ?? child.CreatedByActorId, at ?? child.CreatedAt, reason ?? child.ChangeReason, digest ?? child.ContentDigest, child.Elements);

    private static SubtitleEditCommit ReplaceChild(SubtitleEditCommit commit, CompositionVersion child) => commit with
    {
        ChildVersion = child,
        ChildCanonicalJson = SourceOnlyExecutionTestSeed.VersionPayload(child),
    };

    private static SubtitleEditCommit RecomputeElementsDigest(SubtitleEditCommit commit)
    {
        JsonNode body = JsonNode.Parse(commit.ChildCanonicalJson)!;
        ContentDigest digest = Hash(CanonicalJson.Canonicalize(
            new JsonObject { ["elements"] = body["elements"]!.DeepClone() }.ToJsonString()));
        return ReplaceChild(commit, CopyChild(commit.ChildVersion, digest: digest));
    }

    private static string ChangeJson(string json, Action<JsonNode> mutation)
    {
        JsonNode body = JsonNode.Parse(json)!;
        mutation(body);
        return CanonicalJson.Canonicalize(body.ToJsonString());
    }

    private static ContentDigest Hash(string text) => ContentDigest.Compute(Encoding.UTF8.GetBytes(text));

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Database = new StoreTestDatabase();
            var seed = new SourceOnlyExecutionTestSeed(Database.DatabasePath);
            _ = seed.Finalize();
            Root = seed.AppendStandardVersion();
            Store = new SubtitleEditStore(Database.DatabasePath);
            InitialVersions = VersionCount;
        }

        internal StoreTestDatabase Database { get; }
        internal SubtitleEditStore Store { get; }
        internal CompositionVersion Root { get; }
        internal long InitialVersions { get; }
        internal long VersionCount => Database.ExecuteScalarInt64("SELECT COUNT(*) FROM composition_versions;");
        internal ContentDigest InitialSnapshot { get; } = Hash("initial subtitle snapshot");
        private ContentDigest GeneratedInput { get; } = Hash("generated subtitle input");
        private Guid CueId { get; } = Guid.NewGuid();

        internal SubtitleEditCommit Edit(CompositionVersion? parent = null, int sequence = 0, ContentDigest? parentSnapshot = null)
        {
            parent ??= Root;
            DateTimeOffset at = parent.CreatedAt.AddSeconds(1);
            const string reason = "Subtitle correction";
            var child = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), Root.ProjectId,
                parent.Id, Root.CreatedByActorId, at, "Apply a typed subtitle cue edit.", parent.ContentDigest, parent.Elements);
            string operation = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
            {
                kind = "editSubtitle",
                cueId = CueId.ToString("D"),
                expectedRevisionId = Guid.NewGuid().ToString("D"),
                expectedRevisionDigest = Hash("previous cue revision").Hex,
                reason,
                edit = new { kind = "text", text = $"字幕 {sequence + 1}" },
            }));
            return new(Guid.NewGuid(), Root.ProjectId, Root.Id, parent.Id, child,
                SourceOnlyExecutionTestSeed.VersionPayload(child), sequence, GeneratedInput,
                parentSnapshot ?? InitialSnapshot, Hash($"subtitle result:{child.Id}"), operation,
                new[] { child.Id.Value, Guid.NewGuid(), Guid.NewGuid() }, Root.CreatedByActorId, at,
                ExpectedRootPayloadDigest: Hash(SourceOnlyExecutionTestSeed.VersionPayload(Root)));
        }

        internal (SubtitleEditCommit First, SubtitleEditCommit Last) SeedBranch(int count, int payloadBytes = 0, int finalExtraBytes = 0)
        {
            // Prepare valid immutable records in one transaction instead of
            // repeatedly validating every growing prefix via Store.Commit.
            Assert.IsTrue(count > 0);
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            ContentDigest rootDigest = Hash(SourceOnlyExecutionTestSeed.VersionPayload(Root));
            ContentDigest? previous = null;
            CompositionVersion parent = Root;
            ContentDigest snapshot = InitialSnapshot;
            SubtitleEditCommit? first = null;
            SubtitleEditCommit? last = null;
            for (int sequence = 0; sequence < count; sequence++)
            {
                SubtitleEditCommit commit = Edit(parent, sequence, snapshot);
                if (payloadBytes != 0)
                {
                    commit = WithCommitPayloadBytes(commit, payloadBytes + (sequence == count - 1 ? finalExtraBytes : 0));
                }
                string canonical = CommitPayload(commit);
                ContentDigest commitDigest = Hash(canonical);
                ContentDigest childDigest = Hash(commit.ChildCanonicalJson);
                ContentDigest recordDigest = Hash(CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
                {
                    schema = "subtitle-edit.record.v1",
                    sequence = sequence + 1,
                    commitDigest = commitDigest.Hex,
                    rootPayloadDigest = rootDigest.Hex,
                    childPayloadDigest = childDigest.Hex,
                    previousRecordDigest = previous?.Hex,
                })));
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO composition_versions (id, project_id, parent_version_id, content_digest, created_at, payload)
                    VALUES ($child, $project, $parent, $content, $at, $child_payload);
                    INSERT INTO subtitle_edits (
                        request_id, project_id, root_version_id, parent_version_id, child_version_id, sequence,
                        generated_input_digest, parent_snapshot_digest, result_snapshot_digest, child_content_digest,
                        root_payload_digest, child_payload_digest, actor_sid, occurred_at, commit_payload,
                        commit_digest, previous_record_digest, record_digest)
                    VALUES ($request, $project, $root, $parent, $child, $sequence, $generated, $parent_snapshot,
                        $result_snapshot, $content, $root_payload, $child_digest, $actor, $at, $payload,
                        $commit_digest, $previous, $record_digest);
                    """;
                foreach ((string name, object? value) in new (string, object?)[]
                {
                    ("$child", commit.ChildVersion.Id.ToString()), ("$project", commit.ProjectId.ToString()),
                    ("$root", commit.RootVersionId.ToString()), ("$parent", commit.ParentVersionId.ToString()),
                    ("$content", commit.ChildVersion.ContentDigest.Hex), ("$at", commit.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
                    ("$child_payload", Encoding.UTF8.GetBytes(commit.ChildCanonicalJson)), ("$request", commit.RequestId.ToString("D")),
                    ("$sequence", sequence + 1), ("$generated", commit.GeneratedInputDigest.Hex),
                    ("$parent_snapshot", commit.ParentSnapshotDigest.Hex), ("$result_snapshot", commit.ResultSnapshotDigest.Hex),
                    ("$root_payload", rootDigest.Hex), ("$child_digest", childDigest.Hex), ("$actor", commit.ActorSid),
                    ("$payload", Encoding.UTF8.GetBytes(canonical)), ("$commit_digest", commitDigest.Hex),
                    ("$previous", previous?.Hex), ("$record_digest", recordDigest.Hex),
                })
                {
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                Assert.AreEqual(2, command.ExecuteNonQuery());
                first ??= commit;
                last = commit;
                parent = commit.ChildVersion;
                snapshot = commit.ResultSnapshotDigest;
                previous = recordDigest;
            }
            transaction.Commit();
            return (first!, last!);
        }

        public void Dispose() => Database.Dispose();
    }
}