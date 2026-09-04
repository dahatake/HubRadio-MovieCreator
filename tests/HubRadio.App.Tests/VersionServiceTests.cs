using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HubRadio.App.Compositions;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class VersionServiceTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2035, 4, 5, 14, 15, 16, TimeSpan.FromHours(9));

    [TestMethod]
    public void CheckpointCreatesRootThenLinearChildWithCanonicalStoredSnapshots()
    {
        using ServiceTestDatabase database = new();
        CompositionElement element = CreateAssetElement(
            Id<CompositionElementIdTag>("10000000-0000-4000-8000-000000000001"),
            Id<AssetIdTag>("20000000-0000-4000-8000-000000000001"),
            start: default,
            duration: new RationalTime(2, 1));
        (ProjectStore store, Project project, WorkingCopy draft) =
            CreateStoredDraft(database, [element]);
        var service = new VersionService(store);
        EntityId<CompositionVersionIdTag> rootId =
            Id<CompositionVersionIdTag>("30000000-0000-4000-8000-000000000001");
        DateTimeOffset rootTime = InitialTime.AddMinutes(1);

        CheckpointResult root = service.Checkpoint(
            draft,
            expectedRevision: 0,
            rootId,
            "S-1-5-21-1000",
            rootTime,
            "Create the first composition checkpoint.");

        const string expectedElementsJson =
            "{\"elements\":[{\"id\":\"10000000-0000-4000-8000-000000000001\",\"origin\":{\"assetId\":\"20000000-0000-4000-8000-000000000001\",\"kind\":\"asset\"},\"outputRange\":{\"duration\":{\"rate\":1,\"value\":2},\"start\":{\"rate\":1,\"value\":0}}}]}";
        ContentDigest expectedDigest = ContentDigest.Compute(
            Encoding.UTF8.GetBytes(expectedElementsJson));
        Assert.AreEqual(rootId, root.Version.Id);
        Assert.AreEqual(project.Id, root.Version.ProjectId);
        Assert.IsNull(root.Version.ParentVersionId);
        Assert.AreEqual(expectedDigest, root.Version.ContentDigest);
        Assert.AreEqual(1, root.WorkingCopyRevision);
        Assert.AreEqual(rootId, root.WorkingCopy.BaseVersionId);
        Assert.AreEqual(draft.Id, root.WorkingCopy.Id);
        Assert.AreEqual(draft.CreatedAt, root.WorkingCopy.CreatedAt);
        Assert.AreEqual(rootTime, root.WorkingCopy.UpdatedAt);
        CollectionAssert.AreEqual(
            draft.Elements.ToArray(),
            root.Version.Elements.ToArray());
        CollectionAssert.AreEqual(
            draft.Elements.ToArray(),
            root.WorkingCopy.Elements.ToArray());
        AssertCanonicalAndSchemaShaped(
            root.CanonicalVersionJson,
            "schemas/composition-version.schema.json");
        AssertCanonicalAndSchemaShaped(
            root.CanonicalWorkingCopyJson,
            "schemas/working-copy.schema.json");
        using (JsonDocument rootJson = JsonDocument.Parse(root.CanonicalVersionJson))
        {
            Assert.IsFalse(rootJson.RootElement.TryGetProperty("parentVersionId", out _));
        }

        StoredCompositionVersion storedRoot = store.GetCompositionVersion(rootId)
            ?? throw new AssertFailedException("The root version was not stored.");
        StoredWorkingCopy storedDraft = store.GetWorkingCopy(project.Id)
            ?? throw new AssertFailedException("The updated draft was not stored.");
        Assert.AreEqual(root.CanonicalVersionJson, storedRoot.CanonicalJson);
        Assert.AreEqual(root.CanonicalWorkingCopyJson, storedDraft.CanonicalJson);
        Assert.AreEqual(rootId, storedDraft.BaseVersionId);
        Assert.AreEqual(1, storedDraft.Revision);

        EntityId<CompositionVersionIdTag> childId =
            Id<CompositionVersionIdTag>("30000000-0000-4000-8000-000000000002");
        CheckpointResult child = service.Checkpoint(
            root.WorkingCopy,
            expectedRevision: 1,
            childId,
            "S-1-5-21-2000",
            rootTime.AddMinutes(1),
            "Checkpoint unchanged content with new metadata.");

        Assert.AreEqual(rootId, child.Version.ParentVersionId);
        Assert.AreEqual(expectedDigest, child.Version.ContentDigest);
        Assert.AreEqual(2, child.WorkingCopyRevision);
        Assert.AreEqual(childId, child.WorkingCopy.BaseVersionId);
        Assert.AreEqual(root.CanonicalVersionJson, storedRoot.CanonicalJson);
        Assert.AreEqual(child.CanonicalVersionJson, store.GetCompositionVersion(childId)!.CanonicalJson);
        Assert.AreEqual(child.CanonicalWorkingCopyJson, store.GetWorkingCopy(project.Id)!.CanonicalJson);
    }

    [TestMethod]
    public void ContentDigestDependsOnlyOnCanonicalElements()
    {
        using ServiceTestDatabase database = new();
        CompositionElement original = CreateAssetElement(duration: new RationalTime(1, 1));
        (ProjectStore store, _, WorkingCopy draft) = CreateStoredDraft(database, [original]);
        var service = new VersionService(store);

        CheckpointResult first = service.Checkpoint(
            draft,
            0,
            EntityId<CompositionVersionIdTag>.New(),
            "actor-one",
            InitialTime.AddMinutes(1),
            "First metadata.");
        CheckpointResult second = service.Checkpoint(
            first.WorkingCopy,
            1,
            EntityId<CompositionVersionIdTag>.New(),
            "actor-two",
            InitialTime.AddMinutes(2),
            "Entirely different metadata.");
        CompositionElement changed = CreateAssetElement(
            original.Id,
            original.Origin.AssetId,
            start: default,
            duration: new RationalTime(2, 1));
        var changedDraft = new WorkingCopy(
            second.WorkingCopy.Id,
            second.WorkingCopy.ProjectId,
            second.Version.Id,
            second.WorkingCopy.CreatedAt,
            InitialTime.AddMinutes(3),
            [changed]);
        CheckpointResult third = service.Checkpoint(
            changedDraft,
            2,
            EntityId<CompositionVersionIdTag>.New(),
            "actor-three",
            InitialTime.AddMinutes(3),
            "Change element duration.");

        Assert.AreEqual(first.Version.ContentDigest, second.Version.ContentDigest);
        Assert.AreNotEqual(first.Version.Id, second.Version.Id);
        Assert.AreNotEqual(first.Version.CreatedByActorId, second.Version.CreatedByActorId);
        Assert.AreNotEqual(first.Version.ChangeReason, second.Version.ChangeReason);
        Assert.AreNotEqual(second.Version.ContentDigest, third.Version.ContentDigest);
    }

    [TestMethod]
    public async Task ConcurrentCheckpointsAllowOneWinnerAndLeaveNoOrphanVersion()
    {
        using ServiceTestDatabase database = new();
        (ProjectStore setupStore, Project project, WorkingCopy draft) =
            CreateStoredDraft(database, [CreateAssetElement()]);
        var firstService = new VersionService(new ProjectStore(database.DatabasePath));
        var secondService = new VersionService(new ProjectStore(database.DatabasePath));
        EntityId<CompositionVersionIdTag> firstId = EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionVersionIdTag> secondId = EntityId<CompositionVersionIdTag>.New();
        using var release = new ManualResetEventSlim(initialState: false);

        Task<CheckpointAttempt> first = Task.Run(
            () => TryCheckpoint(firstService, draft, firstId, "first", release));
        Task<CheckpointAttempt> second = Task.Run(
            () => TryCheckpoint(secondService, draft, secondId, "second", release));
        release.Set();
        CheckpointAttempt[] attempts = await Task.WhenAll(first, second);

        CheckpointAttempt winner = attempts.Single(static attempt => attempt.Result is not null);
        CheckpointAttempt loser = attempts.Single(static attempt => attempt.Error is not null);
        Assert.IsNotNull(winner.Result);
        Assert.IsNotNull(loser.Error);
        Assert.AreEqual(typeof(StoreConcurrencyException), loser.Error.GetType());
        var concurrency = (StoreConcurrencyException)loser.Error;
        Assert.AreEqual("working copy", concurrency.EntityKind);
        Assert.AreEqual(0, concurrency.ExpectedRevision);
        Assert.AreEqual(1, concurrency.ActualRevision);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
        Assert.IsNotNull(setupStore.GetCompositionVersion(winner.VersionId));
        Assert.IsNull(setupStore.GetCompositionVersion(loser.VersionId));
        StoredWorkingCopy storedDraft = setupStore.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(1, storedDraft.Revision);
        Assert.AreEqual(winner.VersionId, storedDraft.BaseVersionId);
    }

    [TestMethod]
    public void RollbackAsChildCopiesTargetContentOntoCurrentHeadWithoutApprovalInheritance()
    {
        using ServiceTestDatabase database = new();
        CompositionElement initialElement = CreateAssetElement(duration: new RationalTime(1, 1));
        (ProjectStore store, Project project, WorkingCopy initialDraft) =
            CreateStoredDraft(database, [initialElement]);
        var service = new VersionService(store);
        CheckpointResult first = service.Checkpoint(
            initialDraft,
            0,
            EntityId<CompositionVersionIdTag>.New(),
            "editor",
            InitialTime.AddMinutes(1),
            "Create V1.");
        var reviewStore = new ReviewStore(database.DatabasePath);
        reviewStore.AppendApproval(new Approval(
            EntityId<ApprovalIdTag>.New(),
            first.Version,
            "content-approver",
            ReviewActorRole.ContentApprover,
            InitialTime.AddMinutes(2),
            "Approve V1."));

        CompositionElement changedElement = CreateAssetElement(
            initialElement.Id,
            initialElement.Origin.AssetId,
            default,
            new RationalTime(2, 1));
        var changedDraft = new WorkingCopy(
            first.WorkingCopy.Id,
            project.Id,
            first.Version.Id,
            first.WorkingCopy.CreatedAt,
            InitialTime.AddMinutes(3),
            [changedElement]);
        CheckpointResult second = service.Checkpoint(
            changedDraft,
            1,
            EntityId<CompositionVersionIdTag>.New(),
            "editor",
            InitialTime.AddMinutes(3),
            "Create V2.");
        string firstStoredJson = store.GetCompositionVersion(first.Version.Id)!.CanonicalJson;
        string secondStoredJson = store.GetCompositionVersion(second.Version.Id)!.CanonicalJson;
        EntityId<CompositionVersionIdTag>? currentBaseBefore = second.WorkingCopy.BaseVersionId;
        CompositionElement[] currentElementsBefore = second.WorkingCopy.Elements.ToArray();

        CheckpointResult rollback = service.RollbackAsChild(
            second.WorkingCopy,
            first.Version,
            2,
            EntityId<CompositionVersionIdTag>.New(),
            "editor",
            InitialTime.AddMinutes(4),
            "Restore V1 content as V3.");

        Assert.AreEqual(second.Version.Id, rollback.Version.ParentVersionId);
        Assert.AreEqual(first.Version.ContentDigest, rollback.Version.ContentDigest);
        CollectionAssert.AreEqual(
            first.Version.Elements.ToArray(),
            rollback.Version.Elements.ToArray());
        Assert.AreEqual(rollback.Version.Id, rollback.WorkingCopy.BaseVersionId);
        Assert.AreEqual(3, rollback.WorkingCopyRevision);
        Assert.AreEqual(currentBaseBefore, second.WorkingCopy.BaseVersionId);
        CollectionAssert.AreEqual(currentElementsBefore, second.WorkingCopy.Elements.ToArray());
        CollectionAssert.AreEqual(
            new[] { initialElement },
            first.Version.Elements.ToArray());
        Assert.AreEqual(firstStoredJson, store.GetCompositionVersion(first.Version.Id)!.CanonicalJson);
        Assert.AreEqual(secondStoredJson, store.GetCompositionVersion(second.Version.Id)!.CanonicalJson);
        Assert.AreEqual(3L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));

        StoredVersionEvidence firstEvidence = reviewStore.GetVersionEvidence(first.Version.Id)!;
        StoredVersionEvidence rollbackEvidence = reviewStore.GetVersionEvidence(rollback.Version.Id)!;
        Assert.AreEqual(1, firstEvidence.Approvals.Count);
        Assert.AreEqual(0, rollbackEvidence.Approvals.Count);
        Assert.AreEqual(0, rollbackEvidence.ReviewComments.Count);
        Assert.AreEqual(0, rollbackEvidence.ReviewRecords.Count);
    }

    [TestMethod]
    public void RollbackRejectsForgedStoredTargetBeforeAnyStoreMutation()
    {
        using ServiceTestDatabase database = new();
        (ProjectStore store, Project project, WorkingCopy draft) =
            CreateStoredDraft(database, [CreateAssetElement()]);
        var service = new VersionService(store);
        CheckpointResult stored = service.Checkpoint(
            draft,
            0,
            EntityId<CompositionVersionIdTag>.New(),
            "editor",
            InitialTime.AddMinutes(1),
            "Create current head.");
        var forgedTarget = new CompositionVersion(
            stored.Version.Id,
            project.Id,
            null,
            "other-editor",
            InitialTime,
            "Untrusted imported target.",
            stored.Version.ContentDigest,
            stored.Version.Elements);
        EntityId<CompositionVersionIdTag> rejectedId = EntityId<CompositionVersionIdTag>.New();

        Assert.ThrowsExactly<ArgumentException>(
            () => service.RollbackAsChild(
                stored.WorkingCopy,
                forgedTarget,
                1,
                rejectedId,
                "editor",
                InitialTime.AddMinutes(2),
                "Attempt rollback."));

        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
        Assert.IsNull(store.GetCompositionVersion(rejectedId));
        StoredWorkingCopy persistedDraft = store.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(1, persistedDraft.Revision);
        Assert.AreEqual(forgedTarget.Id, persistedDraft.BaseVersionId);
    }

    [TestMethod]
    public void RollbackRejectsStoredNonancestorBeforeAnyStoreMutation()
    {
        using ServiceTestDatabase database = new();
        (ProjectStore store, Project project, WorkingCopy draft) =
            CreateStoredDraft(database, [CreateAssetElement()]);
        var service = new VersionService(store);
        CheckpointResult head = service.Checkpoint(
            draft,
            0,
            EntityId<CompositionVersionIdTag>.New(),
            "editor",
            InitialTime.AddMinutes(1),
            "Create current head.");
        var unrelated = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            project.Id,
            parentVersionId: null,
            "other-editor",
            InitialTime.AddMinutes(1),
            "Create unrelated stored root.",
            head.Version.ContentDigest,
            head.Version.Elements);
        store.AppendCompositionVersion(
            unrelated,
            CompositionSnapshotJson.WriteVersion(unrelated));
        EntityId<CompositionVersionIdTag> rejectedId = EntityId<CompositionVersionIdTag>.New();

        Assert.ThrowsExactly<ArgumentException>(() => service.RollbackAsChild(
            head.WorkingCopy,
            unrelated,
            1,
            rejectedId,
            "editor",
            InitialTime.AddMinutes(2),
            "Attempt nonancestor rollback."));

        Assert.AreEqual(2L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
        Assert.IsNull(store.GetCompositionVersion(rejectedId));
        Assert.AreEqual(1, store.GetWorkingCopy(project.Id)!.Revision);
    }

    [TestMethod]
    public void EvidenceEnvelopeRejectsNoncanonicalUnicodeAndDigestMismatch()
    {
        CompositionEvidenceEnvelope valid = CompositionEvidenceEnvelope.Create(
            "autoDecision",
            "{\"schemaVersion\":\"v1\"}");
        CompositionEvidenceEnvelope restored = CompositionEvidenceEnvelope.Restore(
            valid.Kind,
            valid.ContentDigest,
            valid.CanonicalJson);
        Assert.AreEqual(valid.Kind, restored.Kind);
        Assert.AreEqual(valid.ContentDigest, restored.ContentDigest);
        Assert.AreEqual(valid.CanonicalJson, restored.CanonicalJson);
        Assert.ThrowsExactly<FormatException>(() =>
            CompositionEvidenceEnvelope.Create("autoDecision", "{ \"a\": 1 }"));
        Assert.ThrowsExactly<FormatException>(() =>
            CompositionEvidenceEnvelope.Create("autoDecision", "{\"a\":\"\uD800\"}"));
        Assert.ThrowsExactly<FormatException>(() =>
            CompositionEvidenceEnvelope.Restore(
                "autoDecision",
                new ContentDigest(new string('f', 64)),
                valid.CanonicalJson));
    }

    [TestMethod]
    public void EvidenceEnvelopeRejectsCanonicalJsonAboveUtf8ByteLimit()
    {
        string oversized = "{\"value\":\"" +
            new string('x', CompositionEvidenceEnvelope.MaximumCanonicalJsonUtf8Bytes) +
            "\"}";

        Assert.ThrowsExactly<FormatException>(() =>
            CompositionEvidenceEnvelope.Create("autoDecision", oversized));
    }

    [TestMethod]
    public void CheckpointRejectsReverseTimeAndDelegatesActorAndReasonValidationToVersion()
    {
        using ServiceTestDatabase database = new();
        (ProjectStore store, _, WorkingCopy draft) =
            CreateStoredDraft(database, [CreateAssetElement()]);
        var service = new VersionService(store);

        ArgumentOutOfRangeException reverse = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => service.Checkpoint(
                draft,
                0,
                EntityId<CompositionVersionIdTag>.New(),
                "actor",
                draft.UpdatedAt.AddTicks(-1),
                "Reason."));
        Assert.AreEqual("createdAt", reverse.ParamName);

        ArgumentException actor = Assert.ThrowsExactly<ArgumentException>(
            () => service.Checkpoint(
                draft,
                0,
                EntityId<CompositionVersionIdTag>.New(),
                " actor",
                draft.UpdatedAt,
                "Reason."));
        Assert.AreEqual("createdByActorId", actor.ParamName);
        ArgumentException reason = Assert.ThrowsExactly<ArgumentException>(
            () => service.Checkpoint(
                draft,
                0,
                EntityId<CompositionVersionIdTag>.New(),
                "actor",
                draft.UpdatedAt,
                "Reason. "));
        Assert.AreEqual("changeReason", reason.ParamName);
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM composition_versions;"));
    }

    [TestMethod]
    public void CompareReturnsTypedChangesInRequiredDeterministicOrder()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        CompositionElement removedFirst = CreateAssetElement(start: new RationalTime(0, 1));
        CompositionElement modifiedBefore = CreateAssetElement(start: new RationalTime(1, 1));
        CompositionElement movedBefore = CreateAssetElement(start: new RationalTime(2, 1));
        CompositionElement removedLast = CreateAssetElement(start: new RationalTime(3, 1));
        CompositionElement addedFirst = CreateAssetElement(start: new RationalTime(0, 1));
        CompositionElement movedAfter = CreateAssetElement(
            movedBefore.Id,
            movedBefore.Origin.AssetId,
            start: new RationalTime(5, 1),
            duration: movedBefore.OutputRange.Duration);
        CompositionElement modifiedAfter = CreateAssetElement(
            modifiedBefore.Id,
            modifiedBefore.Origin.AssetId,
            start: new RationalTime(6, 1),
            duration: new RationalTime(2, 1));
        CompositionElement addedLast = CreateAssetElement(start: new RationalTime(8, 1));
        CompositionVersion from = CreateComparableVersion(
            projectId,
            [removedFirst, modifiedBefore, movedBefore, removedLast]);
        CompositionVersion to = CreateComparableVersion(
            projectId,
            [addedFirst, movedAfter, modifiedAfter, addedLast]);

        CompositionDiff diff = VersionService.Compare(from, to);

        Assert.AreEqual(from.Id, diff.FromVersionId);
        Assert.AreEqual(to.Id, diff.ToVersionId);
        CollectionAssert.AreEqual(
            new[]
            {
                removedFirst.Id,
                removedLast.Id,
                addedFirst.Id,
                addedLast.Id,
                movedBefore.Id,
                modifiedBefore.Id,
            },
            diff.Changes.Select(static change => change.ElementId).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                CompositionChangeKind.Removed,
                CompositionChangeKind.Removed,
                CompositionChangeKind.Added,
                CompositionChangeKind.Added,
                CompositionChangeKind.Moved,
                CompositionChangeKind.Modified,
            },
            diff.Changes.Select(static change => change.Kind).ToArray());

        CompositionChange removed = diff.Changes[0];
        Assert.AreEqual(0, removed.PreviousIndex);
        Assert.IsNull(removed.CurrentIndex);
        Assert.AreSame(removedFirst, removed.PreviousElement);
        Assert.IsNull(removed.CurrentElement);
        CompositionChange added = diff.Changes[2];
        Assert.IsNull(added.PreviousIndex);
        Assert.AreEqual(0, added.CurrentIndex);
        Assert.IsNull(added.PreviousElement);
        Assert.AreSame(addedFirst, added.CurrentElement);
        CompositionChange moved = diff.Changes[4];
        Assert.AreEqual(2, moved.PreviousIndex);
        Assert.AreEqual(1, moved.CurrentIndex);
        Assert.AreSame(movedBefore, moved.PreviousElement);
        Assert.AreSame(movedAfter, moved.CurrentElement);
        CompositionChange modified = diff.Changes[5];
        Assert.AreEqual(1, modified.PreviousIndex);
        Assert.AreEqual(2, modified.CurrentIndex);
        Assert.AreSame(modifiedBefore, modified.PreviousElement);
        Assert.AreSame(modifiedAfter, modified.CurrentElement);
    }

    [TestMethod]
    public void CompareIgnoresOnlyOutputReflowAndDetectsEveryStableContentComponent()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        CompositionElement first = CreateAssetElement(start: default);
        CompositionElement second = CreateAssetElement(start: new RationalTime(1, 1));
        CompositionElement reflowedFirst = CreateAssetElement(
            first.Id,
            first.Origin.AssetId,
            new RationalTime(10, 1),
            first.OutputRange.Duration);
        CompositionElement reflowedSecond = CreateAssetElement(
            second.Id,
            second.Origin.AssetId,
            new RationalTime(11, 1),
            second.OutputRange.Duration);
        CompositionDiff reflowOnly = VersionService.Compare(
            CreateComparableVersion(projectId, [first, second]),
            CreateComparableVersion(projectId, [reflowedFirst, reflowedSecond]));
        Assert.AreEqual(0, reflowOnly.Changes.Count);

        CompositionElement durationBefore = CreateAssetElement();
        CompositionElement durationAfter = CreateAssetElement(
            durationBefore.Id,
            durationBefore.Origin.AssetId,
            default,
            new RationalTime(2, 1));
        CompositionElement originBefore = CreateAssetElement();
        CompositionElement originAfter = CreateAssetElement(
            originBefore.Id,
            EntityId<AssetIdTag>.New());
        EntityId<SourceSpanIdTag> spanId = EntityId<SourceSpanIdTag>.New();
        CompositionElement sourceBefore = CreateSourceElement(
            EntityId<CompositionElementIdTag>.New(),
            spanId,
            new TimeRange(new RationalTime(-2, 1), new RationalTime(1, 1)));
        CompositionElement sourceAfter = CreateSourceElement(
            sourceBefore.Id,
            spanId,
            new TimeRange(new RationalTime(-1, 1), new RationalTime(1, 1)));
        OriginRef suggestionOrigin = OriginRef.FromSourceSpan(
            EntityId<SourceSpanIdTag>.New());
        CompositionElement suggestionBefore = CreateSourceElement(
            EntityId<CompositionElementIdTag>.New(),
            suggestionOrigin.SourceSpanId!,
            new TimeRange(default, new RationalTime(1, 1)),
            CreateSuggestionReference(EntityId<SuggestionIdTag>.New(), suggestionOrigin));
        CompositionElement suggestionAfter = CreateSourceElement(
            suggestionBefore.Id,
            suggestionOrigin.SourceSpanId!,
            new TimeRange(default, new RationalTime(1, 1)),
            CreateSuggestionReference(EntityId<SuggestionIdTag>.New(), suggestionOrigin));

        CompositionDiff stableChanges = VersionService.Compare(
            CreateComparableVersion(
                projectId,
                [durationBefore, originBefore, sourceBefore, suggestionBefore]),
            CreateComparableVersion(
                projectId,
                [durationAfter, originAfter, sourceAfter, suggestionAfter]));

        Assert.AreEqual(4, stableChanges.Changes.Count);
        Assert.IsTrue(stableChanges.Changes.All(
            static change => change.Kind == CompositionChangeKind.Modified));
    }

    [TestMethod]
    public void CompareNoChangeAndValidationArePureAndFailClosed()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        CompositionElement element = CreateAssetElement();
        CompositionVersion first = CreateComparableVersion(projectId, [element]);
        CompositionVersion same = CreateComparableVersion(projectId, [element]);
        CompositionVersion otherProject = CreateComparableVersion(
            EntityId<ProjectIdTag>.New(),
            [element]);
        CompositionElement[] before = first.Elements.ToArray();

        Assert.AreEqual(0, VersionService.Compare(first, same).Changes.Count);
        Assert.ThrowsExactly<ArgumentNullException>(
            () => VersionService.Compare(null!, same));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => VersionService.Compare(first, null!));
        ArgumentException crossProject = Assert.ThrowsExactly<ArgumentException>(
            () => VersionService.Compare(first, otherProject));
        Assert.AreEqual("to", crossProject.ParamName);
        CollectionAssert.AreEqual(before, first.Elements.ToArray());
    }

    [TestMethod]
    public void SnapshotJsonWritesExactUnionShapesCanonicalOrderAndStrictUnicode()
    {
        using ServiceTestDatabase database = new();
        EntityId<SourceSpanIdTag> sourceSpanId =
            Id<SourceSpanIdTag>("40000000-0000-4000-8000-000000000001");
        OriginRef sourceOrigin = OriginRef.FromSourceSpan(sourceSpanId);
        SuggestionReference suggestion = CreateSuggestionReference(
            Id<SuggestionIdTag>("50000000-0000-4000-8000-000000000001"),
            sourceOrigin);
        CompositionElement sourceElement = CreateSourceElement(
            Id<CompositionElementIdTag>("60000000-0000-4000-8000-000000000001"),
            sourceSpanId,
            new TimeRange(new RationalTime(-5, 2), new RationalTime(3, 2)),
            suggestion,
            outputStart: default);
        CompositionElement assetElement = CreateAssetElement(
            Id<CompositionElementIdTag>("60000000-0000-4000-8000-000000000002"),
            Id<AssetIdTag>("70000000-0000-4000-8000-000000000001"),
            new RationalTime(3, 2),
            new RationalTime(1, 1));
        CompositionElement editorialElement = new(
            Id<CompositionElementIdTag>("60000000-0000-4000-8000-000000000003"),
            new TimeRange(new RationalTime(5, 2), new RationalTime(2, 1)),
            OriginRef.FromEditorialRecord(
                Id<EditorialRecordIdTag>("80000000-0000-4000-8000-000000000001")));
        (ProjectStore store, _, WorkingCopy draft) = CreateStoredDraft(
            database,
            [sourceElement, assetElement, editorialElement]);
        var service = new VersionService(store);
        const string actor = "actor-\"line\n😀";
        const string reason = "Reason \\ path\t日本語 😀";

        CheckpointResult result = service.Checkpoint(
            draft,
            0,
            Id<CompositionVersionIdTag>("90000000-0000-4000-8000-000000000001"),
            actor,
            InitialTime.AddMinutes(1),
            reason);

        Assert.AreEqual(
            CanonicalJson.Canonicalize(result.CanonicalVersionJson),
            result.CanonicalVersionJson);
        Assert.AreEqual(
            CanonicalJson.Canonicalize(result.CanonicalWorkingCopyJson),
            result.CanonicalWorkingCopyJson);
        using JsonDocument versionDocument = JsonDocument.Parse(result.CanonicalVersionJson);
        JsonElement root = versionDocument.RootElement;
        AssertPropertyOrder(
            root,
            "changeReason",
            "contentDigest",
            "createdAt",
            "createdByActorId",
            "elements",
            "id",
            "projectId");
        Assert.AreEqual(actor, root.GetProperty("createdByActorId").GetString());
        Assert.AreEqual(reason, root.GetProperty("changeReason").GetString());
        Assert.AreEqual(
            InitialTime.AddMinutes(1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            root.GetProperty("createdAt").GetString());
        Assert.AreEqual(result.Version.ContentDigest.Hex, root.GetProperty("contentDigest").GetString());
        Assert.AreEqual(result.Version.Id.ToString().ToLowerInvariant(), root.GetProperty("id").GetString());

        JsonElement[] elements = root.GetProperty("elements").EnumerateArray().ToArray();
        Assert.AreEqual(3, elements.Length);
        CollectionAssert.AreEqual(
            new[] { sourceElement.Id, assetElement.Id, editorialElement.Id },
            elements.Select(static item =>
                EntityId<CompositionElementIdTag>.Parse(item.GetProperty("id").GetString()!))
                .ToArray());
        AssertPropertyOrder(
            elements[0],
            "adoptedSuggestion",
            "id",
            "origin",
            "outputRange",
            "sourceRange");
        AssertPropertyOrder(elements[1], "id", "origin", "outputRange");
        AssertPropertyOrder(elements[2], "id", "origin", "outputRange");
        AssertPropertyOrder(elements[0].GetProperty("origin"), "kind", "sourceSpanId");
        AssertPropertyOrder(elements[1].GetProperty("origin"), "assetId", "kind");
        AssertPropertyOrder(elements[2].GetProperty("origin"), "editorialRecordId", "kind");
        Assert.AreEqual("sourceSpan", elements[0].GetProperty("origin").GetProperty("kind").GetString());
        Assert.AreEqual("asset", elements[1].GetProperty("origin").GetProperty("kind").GetString());
        Assert.AreEqual("editorialRecord", elements[2].GetProperty("origin").GetProperty("kind").GetString());
        JsonElement sourceRange = elements[0].GetProperty("sourceRange");
        AssertPropertyOrder(sourceRange, "duration", "start");
        AssertPropertyOrder(sourceRange.GetProperty("start"), "rate", "value");
        Assert.AreEqual(-5L, sourceRange.GetProperty("start").GetProperty("value").GetInt64());
        Assert.AreEqual(2L, sourceRange.GetProperty("start").GetProperty("rate").GetInt64());
        JsonElement adopted = elements[0].GetProperty("adoptedSuggestion");
        AssertPropertyOrder(adopted, "origin", "suggestionId");
        Assert.AreEqual(
            elements[0].GetProperty("origin").GetRawText(),
            adopted.GetProperty("origin").GetRawText());
        Assert.IsFalse(elements[1].TryGetProperty("sourceRange", out _));
        Assert.IsFalse(elements[2].TryGetProperty("sourceRange", out _));
        CompositionVersion restored = CompositionSnapshotJson.ReadVersion(
            result.CanonicalVersionJson);
        Assert.AreEqual(result.Version.Id, restored.Id);
        Assert.AreEqual(result.Version.ProjectId, restored.ProjectId);
        Assert.AreEqual(result.Version.ParentVersionId, restored.ParentVersionId);
        Assert.AreEqual(result.Version.CreatedByActorId, restored.CreatedByActorId);
        Assert.AreEqual(result.Version.CreatedAt, restored.CreatedAt);
        Assert.AreEqual(result.Version.ChangeReason, restored.ChangeReason);
        Assert.AreEqual(result.Version.ContentDigest, restored.ContentDigest);
        CollectionAssert.AreEqual(
            result.Version.Elements.ToArray(),
            restored.Elements.ToArray());
        Assert.AreEqual(
            result.CanonicalVersionJson,
            CompositionSnapshotJson.WriteVersion(restored));
        AssertCanonicalAndSchemaShaped(
            result.CanonicalVersionJson,
            "schemas/composition-version.schema.json");
        AssertCanonicalAndSchemaShaped(
            result.CanonicalWorkingCopyJson,
            "schemas/working-copy.schema.json");

        using JsonDocument workingCopyDocument = JsonDocument.Parse(
            result.CanonicalWorkingCopyJson);
        AssertPropertyOrder(
            workingCopyDocument.RootElement,
            "baseVersionId",
            "createdAt",
            "elements",
            "id",
            "projectId",
            "updatedAt");

        EntityId<CompositionVersionIdTag> invalidTextVersionId =
            EntityId<CompositionVersionIdTag>.New();
        ArgumentException invalidUnicode = Assert.ThrowsExactly<ArgumentException>(
            () => service.Checkpoint(
                result.WorkingCopy,
                1,
                invalidTextVersionId,
                "actor-\uD800",
                InitialTime.AddMinutes(2),
                "Invalid Unicode must fail."));
        StringAssert.Contains(invalidUnicode.Message, "valid Unicode");
        Assert.IsNull(store.GetCompositionVersion(invalidTextVersionId));
        Assert.AreEqual(1, store.GetWorkingCopy(result.WorkingCopy.ProjectId)!.Revision);
    }

    [TestMethod]
    public void SnapshotJsonReaderRejectsNoncanonicalAndTamperedStoredVersions()
    {
        using ServiceTestDatabase database = new();
        CompositionElement element = CreateAssetElement(
            Id<CompositionElementIdTag>("60000000-0000-4000-8000-000000000001"),
            Id<AssetIdTag>("70000000-0000-4000-8000-000000000001"),
            start: default,
            duration: new RationalTime(1, 1));
        (ProjectStore store, _, WorkingCopy draft) = CreateStoredDraft(database, [element]);
        CheckpointResult result = new VersionService(store).Checkpoint(
            draft,
            0,
            Id<CompositionVersionIdTag>("90000000-0000-4000-8000-000000000001"),
            "actor",
            InitialTime.AddMinutes(1),
            "Create a strict snapshot.");
        string canonical = result.CanonicalVersionJson;
        string upperVersionId = canonical.Replace(
            result.Version.Id.ToString(),
            $"{result.Version.Id.ToString()[..^1]}A",
            StringComparison.Ordinal);
        string canonicalTimestamp = result.Version.CreatedAt
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
        string nonUtcTimestamp = canonical.Replace(
            JsonSerializer.Serialize(canonicalTimestamp),
            JsonSerializer.Serialize(
                canonicalTimestamp.Replace("+00:00", "Z", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        string unreducedTime = canonical.Replace(
            "\"duration\":{\"rate\":1,\"value\":1}",
            "\"duration\":{\"rate\":2,\"value\":2}",
            StringComparison.Ordinal);
        string wrongDigest = canonical.Replace(
            result.Version.ContentDigest.Hex,
            new string(
                result.Version.ContentDigest.Hex[0] == 'f' ? 'e' : 'f',
                64),
            StringComparison.Ordinal);
        string assetOrigin =
            $"\"origin\":{{\"assetId\":\"{element.Origin.AssetId}\",\"kind\":\"asset\"}}";
        string openUnion = canonical.Replace(
            assetOrigin,
            assetOrigin[..^1] +
                ",\"sourceSpanId\":\"40000000-0000-4000-8000-000000000001\"}",
            StringComparison.Ordinal);
        string unexpectedRootProperty = canonical[..^1] + ",\"unexpected\":true}";

        (string Name, string Json)[] invalid =
        [
            ("leading whitespace", " " + canonical),
            ("uppercase version ID", upperVersionId),
            ("non-canonical UTC timestamp", nonUtcTimestamp),
            ("unreduced rational time", unreducedTime),
            ("mismatched content digest", wrongDigest),
            ("open origin union", openUnion),
            ("unexpected root property", unexpectedRootProperty),
        ];
        foreach ((string name, string candidate) in invalid)
        {
            Assert.AreNotEqual(
                canonical,
                candidate,
                $"The {name} candidate must differ from the canonical snapshot.");
            Assert.ThrowsExactly<FormatException>(
                () => CompositionSnapshotJson.ReadVersion(candidate),
                $"The {name} candidate must be rejected.");
        }

        Assert.ThrowsExactly<ArgumentNullException>(
            () => CompositionSnapshotJson.ReadVersion(null!));
    }

    [TestMethod]
    public void SnapshotJsonReaderAcceptsCanonicalExponentFormIntegerTokens()
    {
        using ServiceTestDatabase database = new();
        CompositionElement element = CreateAssetElement(
            start: new RationalTime(1280, 3),
            duration: new RationalTime(1, 30_000));
        (ProjectStore store, _, WorkingCopy draft) =
            CreateStoredDraft(database, [element]);
        CheckpointResult result = new VersionService(store).Checkpoint(
            draft,
            0,
            EntityId<CompositionVersionIdTag>.New(),
            "actor",
            InitialTime.AddMinutes(1),
            "Exercise canonical exponent integers.");

        StringAssert.Contains(result.CanonicalVersionJson, "\"value\":128e1");
        StringAssert.Contains(result.CanonicalVersionJson, "\"rate\":3e4");

        CompositionVersion restored = CompositionSnapshotJson.ReadVersion(
            result.CanonicalVersionJson);

        Assert.AreEqual(result.Version.ContentDigest, restored.ContentDigest);
        Assert.AreEqual(new RationalTime(1280, 3), restored.Elements[0].OutputRange.Start);
        Assert.AreEqual(new RationalTime(1, 30_000), restored.Elements[0].OutputRange.Duration);
        Assert.AreEqual(
            result.CanonicalVersionJson,
            CompositionSnapshotJson.WriteVersion(restored));
    }

    [TestMethod]
    public void PublicContractsAreReadonlyTypedAndExposeNoApprovalCopyOrGenericDiffSurface()
    {
        CollectionAssert.AreEqual(
            new[] { "Added", "Removed", "Moved", "Modified" },
            Enum.GetNames<CompositionChangeKind>());
        AssertGetOnlyProperties(
            typeof(CompositionChange),
            "Kind",
            "ElementId",
            "PreviousIndex",
            "CurrentIndex",
            "PreviousElement",
            "CurrentElement");
        AssertGetOnlyProperties(
            typeof(CompositionDiff),
            "FromVersionId",
            "ToVersionId",
            "Changes");
        AssertGetOnlyProperties(
            typeof(CheckpointResult),
            "Version",
            "WorkingCopy",
            "WorkingCopyRevision",
            "CanonicalVersionJson",
            "CanonicalWorkingCopyJson");
        Assert.AreEqual(0, typeof(CompositionChange).GetConstructors().Length);
        Assert.AreEqual(1, typeof(CompositionChange).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic).Length);
        Assert.IsFalse(typeof(CompositionChange).IsGenericType);
        Assert.IsFalse(typeof(CompositionDiff).IsGenericType);
        Assert.IsFalse(typeof(CompositionChange).GetProperties().Any(
            static property => property.PropertyType == typeof(object) ||
                property.Name.Contains("Field", StringComparison.Ordinal) ||
                property.Name.Contains("Value", StringComparison.Ordinal)));
        Assert.IsFalse(typeof(VersionService).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(static method =>
                method.Name.Contains("Approval", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Copy", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(typeof(CompositionSnapshotJson).IsPublic);

        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        CompositionDiff diff = VersionService.Compare(
            CreateComparableVersion(projectId, []),
            CreateComparableVersion(projectId, [CreateAssetElement()]));
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<CompositionChange>)diff.Changes).Clear());
    }

    private static CheckpointAttempt TryCheckpoint(
        VersionService service,
        WorkingCopy draft,
        EntityId<CompositionVersionIdTag> versionId,
        string actor,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            CheckpointResult result = service.Checkpoint(
                draft,
                0,
                versionId,
                actor,
                InitialTime.AddMinutes(1),
                "Concurrent checkpoint.");
            return new CheckpointAttempt(versionId, result, Error: null);
        }
        catch (Exception exception)
        {
            return new CheckpointAttempt(versionId, Result: null, exception);
        }
    }

    private static (ProjectStore Store, Project Project, WorkingCopy Draft) CreateStoredDraft(
        ServiceTestDatabase database,
        IReadOnlyList<CompositionElement> elements)
    {
        var store = new ProjectStore(database.DatabasePath);
        var project = new Project(
            EntityId<ProjectIdTag>.New(),
            "R-003 test project",
            "Verify version checkpoint behavior.",
            "Test reviewers",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        store.CreateProject(project, "{\"project\":true}", InitialTime);
        var draft = new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            project.Id,
            baseVersionId: null,
            InitialTime,
            InitialTime,
            elements);
        store.CreateWorkingCopy(draft, CompositionSnapshotJson.WriteWorkingCopy(draft));
        return (store, project, draft);
    }

    private static CompositionVersion CreateComparableVersion(
        EntityId<ProjectIdTag> projectId,
        IReadOnlyList<CompositionElement> elements)
    {
        return new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            projectId,
            parentVersionId: null,
            "compare-actor",
            InitialTime,
            "Compare versions.",
            new ContentDigest(new string('a', 64)),
            elements);
    }

    private static CompositionElement CreateAssetElement(
        EntityId<CompositionElementIdTag>? elementId = null,
        EntityId<AssetIdTag>? assetId = null,
        RationalTime? start = null,
        RationalTime? duration = null)
    {
        return new CompositionElement(
            elementId ?? EntityId<CompositionElementIdTag>.New(),
            new TimeRange(start ?? default, duration ?? new RationalTime(1, 1)),
            OriginRef.FromAsset(assetId ?? EntityId<AssetIdTag>.New()));
    }

    private static CompositionElement CreateSourceElement(
        EntityId<CompositionElementIdTag> elementId,
        EntityId<SourceSpanIdTag> sourceSpanId,
        TimeRange sourceRange,
        SuggestionReference? suggestion = null,
        RationalTime? outputStart = null)
    {
        return new CompositionElement(
            elementId,
            new TimeRange(outputStart ?? default, sourceRange.Duration),
            OriginRef.FromSourceSpan(sourceSpanId),
            suggestion,
            sourceRange);
    }

    private static SuggestionReference CreateSuggestionReference(
        EntityId<SuggestionIdTag> suggestionId,
        OriginRef origin)
    {
        var suggestion = new Suggestion(
            suggestionId,
            EntityId<ModelRunIdTag>.New(),
            SuggestionKind.Highlight,
            SuggestionStatus.Adopted,
            origin,
            "Grounded adopted suggestion.",
            confidence: null,
            missingContext: null,
            warning: null);
        return SuggestionReference.FromAdoptedSuggestion(suggestion);
    }

    private static EntityId<TTag> Id<TTag>(string value) => EntityId<TTag>.Parse(value);

    private static void AssertCanonicalAndSchemaShaped(string json, string schemaPath)
    {
        Assert.AreEqual(CanonicalJson.Canonicalize(json), json);
        using JsonDocument payloadDocument = JsonDocument.Parse(json);
        using JsonDocument schemaDocument = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath(schemaPath)));
        JsonElement payload = payloadDocument.RootElement;
        JsonElement schema = schemaDocument.RootElement;
        HashSet<string> allowed = schema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            Assert.IsTrue(allowed.Contains(property.Name), property.Name);
        }

        foreach (JsonElement requiredName in schema.GetProperty("required").EnumerateArray())
        {
            Assert.IsTrue(payload.TryGetProperty(requiredName.GetString()!, out _));
        }
    }

    private static void AssertPropertyOrder(JsonElement value, params string[] names)
    {
        CollectionAssert.AreEqual(
            names,
            value.EnumerateObject().Select(static property => property.Name).ToArray());
    }

    private static void AssertGetOnlyProperties(Type type, params string[] expectedNames)
    {
        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Expected repository-relative file '{relativePath}'.",
                        path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }

    private sealed record CheckpointAttempt(
        EntityId<CompositionVersionIdTag> VersionId,
        CheckpointResult? Result,
        Exception? Error);

    private sealed class ServiceTestDatabase : IDisposable
    {
        internal ServiceTestDatabase()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.VersionServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal long ExecuteScalarInt64(string sql)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The scalar query returned null."));
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}
