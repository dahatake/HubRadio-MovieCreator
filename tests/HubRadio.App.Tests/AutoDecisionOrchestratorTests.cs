using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Provenance;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Processes;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AutoDecisionOrchestratorTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset CreatedAt =
        new(2044, 7, 8, 9, 10, 11, TimeSpan.Zero);

    [TestMethod]
    public void ThreeClosedRulesCreateReversibleChildEvidenceAndExactMap()
    {
        Fixture fixture = new();
        CompositionElement[] parentElements = fixture.Parent.Elements.ToArray();
        ContentDigest sourceDigest = fixture.SourceDigest;

        AutoDecisionPreparation result = AutoDecisionOrchestrator.Prepare(
            fixture.Request(fixture.ClosedCandidates));

        Assert.AreEqual(fixture.Parent.Id, result.Child.ParentVersionId);
        Assert.AreEqual(fixture.Parent.Id, result.PreparedWorkingCopy.BaseVersionId);
        Assert.AreEqual(17, result.ExpectedWorkingCopyRevision);
        Assert.HasCount(4, result.Evidence);
        Assert.AreEqual(1, result.Evidence.Count(item =>
            item.RuleId == AutoDecisionRule.ExplicitCut));
        Assert.AreEqual(1, result.Evidence.Count(item =>
            item.RuleId == AutoDecisionRule.AdjacentExactRepetition));
        Assert.AreEqual(2, result.Evidence.Count(item =>
            item.RuleId == AutoDecisionRule.HeadTailSilence));
        CollectionAssert.AreEqual(
            new[]
            {
                new TimeRange(default, new RationalTime(17, 10)),
                new TimeRange(new RationalTime(83, 10), new RationalTime(17, 10)),
            },
            result.Evidence
                .Where(item => item.RuleId == AutoDecisionRule.HeadTailSilence)
                .OrderBy(static item => item.DeletedSourceRange.Start)
                .Select(static item => item.DeletedSourceRange)
                .ToArray());
        Assert.IsTrue(result.Evidence.All(item =>
            item.ParentVersionId == fixture.Parent.Id
            && item.ParentCompositionDigest == fixture.Parent.ContentDigest
            && item.SettingsSnapshotDigest == fixture.Settings.ContentDigest
            && item.TranscriptRevisionId == fixture.Transcript.Id
            && item.ModelRunId == fixture.ModelRun.Id));
        Assert.HasCount(4, result.DecisionHistory);
        Assert.IsTrue(result.DecisionHistory.All(static record =>
            record.Decision == SuggestionDecisionKind.Adopted));
        Assert.IsTrue(result.Operations.Count > result.Evidence.Count);
        Assert.AreEqual(
            new RationalTime(38, 5),
            result.Child.Elements[^1].OutputRange.End);

        Assert.HasCount(parentElements.Length, fixture.Parent.Elements);
        CollectionAssert.AreEqual(parentElements, fixture.Parent.Elements.ToArray());
        Assert.AreEqual(sourceDigest, fixture.SourceDigest);
        Assert.AreEqual(
            fixture.Parent.ContentDigest,
            ContentDigest.Compute(CompositionSnapshotJson.WriteElementsObjectToUtf8(
                fixture.Parent.Elements)));

        foreach (TimelineMapEntry entry in result.TimelineMap.Entries)
        {
            if (entry.SourceRange is not TimeRange sourceRange)
            {
                continue;
            }

            Assert.IsTrue(result.TimelineMap.TryMapOutputTime(
                entry.OutputRange.Start,
                out TimelineMapPoint point));
            Assert.AreEqual(sourceRange.Start, point.SourceTime);
            CollectionAssert.Contains(
                result.TimelineMap.MapSourceTimeToOutput(
                    entry.Origin.SourceSpanId!,
                    sourceRange.Start).ToArray(),
                entry.OutputRange.Start);
        }
    }

    [TestMethod]
    public void CandidateOrderDoesNotChangeIdsDigestsOperationsOrSource()
    {
        Fixture fixture = new();
        SuggestionCandidate[] reversed = fixture.ClosedCandidates.Reverse().ToArray();
        CompositionElement[] parentElements = fixture.Parent.Elements.ToArray();

        AutoDecisionPreparation first = AutoDecisionOrchestrator.Prepare(
            fixture.Request(fixture.ClosedCandidates));
        AutoDecisionPreparation second = AutoDecisionOrchestrator.Prepare(
            fixture.Request(reversed));

        Assert.AreEqual(first.InputDigest, second.InputDigest);
        Assert.AreEqual(first.DecisionSetDigest, second.DecisionSetDigest);
        Assert.AreEqual(first.Child.Id, second.Child.Id);
        Assert.AreEqual(first.Child.ContentDigest, second.Child.ContentDigest);
        CollectionAssert.AreEqual(
            first.Child.Elements.ToArray(),
            second.Child.Elements.ToArray());
        CollectionAssert.AreEqual(
            first.Evidence.Select(static item => item.DecisionDigest).ToArray(),
            second.Evidence.Select(static item => item.DecisionDigest).ToArray());
        CollectionAssert.AreEqual(parentElements, fixture.Parent.Elements.ToArray());
    }

    [TestMethod]
    public void DuplicateAndPreviouslyDecidedCandidatesFailBeforeMutation()
    {
        Fixture fixture = new();
        SuggestionCandidate candidate = fixture.Editorial;
        CompositionElement[] source = fixture.WorkingCopy.Elements.ToArray();

        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request([candidate, candidate])),
            AutoDecisionFailureReason.DuplicateConflict);

        AutoDecisionPreparation first = AutoDecisionOrchestrator.Prepare(
            fixture.Request([candidate]));
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request([candidate], first.DecisionHistory)),
            AutoDecisionFailureReason.DuplicateConflict);
        CollectionAssert.AreEqual(source, fixture.WorkingCopy.Elements.ToArray());
    }

    [TestMethod]
    public void CancellationLeavesParentDraftAndDecisionHistoryUntouched()
    {
        Fixture fixture = new();
        CompositionElement[] parent = fixture.Parent.Elements.ToArray();
        CompositionElement[] draft = fixture.WorkingCopy.Elements.ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = Assert.ThrowsExactly<OperationCanceledException>(() =>
            AutoDecisionOrchestrator.Prepare(
                fixture.Request(fixture.ClosedCandidates),
                cancellation.Token));

        CollectionAssert.AreEqual(parent, fixture.Parent.Elements.ToArray());
        CollectionAssert.AreEqual(draft, fixture.WorkingCopy.Elements.ToArray());
    }

    [TestMethod]
    public void SettingsTranscriptModelAndSourceEvidenceMustBeCurrentAndComplete()
    {
        Fixture fixture = new();
        DefaultSettingsSnapshot disabled = fixture.Profile.CreateSnapshot(
            fixture.Settings.Values.Select(selection => new DefaultSettingSelection(
                selection.Key,
                selection.Key == "editing.explicitCutAutoExclude"
                    ? DefaultSettingValue.FromBoolean(false)
                    : selection.Value)).ToArray());
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request([fixture.Editorial], settings: disabled)),
            AutoDecisionFailureReason.SettingsMismatch);

        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request(
                    [fixture.Editorial],
                    sourceSpans: fixture.SourceSpans.Where(span =>
                        span.Id != fixture.Transcript.Utterances[0].SourceSpanId).ToArray())),
            AutoDecisionFailureReason.EvidenceMissing);

        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request([fixture.Editorial], modelRuns: [])),
            AutoDecisionFailureReason.ModelRunInvalid);

        TranscriptRevision staleTranscript = new(
            EntityId<TranscriptRevisionIdTag>.New(),
            fixture.Transcript.Id,
            TranscriptChangeSource.Human,
            CreatedAt.AddSeconds(1),
            fixture.Transcript.Utterances);
        SourceSpan[] staleSpans = fixture.SourceSpans.Select(span =>
            span.TranscriptRevisionId is null
                ? span
                : new SourceSpan(
                    span.Id,
                    span.AssetId,
                    span.StreamId,
                    span.Range,
                    span.SourceDigest,
                    staleTranscript.Id,
                    span.TranscriptTokenIds)).ToArray();
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request(
                    [fixture.Editorial],
                    transcript: staleTranscript,
                    sourceSpans: staleSpans)),
            AutoDecisionFailureReason.EvidenceStale);
    }

    [TestMethod]
    public void FourthRuleAndMiddleSilenceFailClosed()
    {
        Fixture fixture = new();
        TopicBoundarySuggestionCandidate fourth = SuggestionService.Register(
            TopicBoundaryService.FindCandidates(
                fixture.Transcript,
                fixture.TranscriptSpans,
                fixture.ModelRun.Id).Candidates[0],
            []);
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(fixture.Request([fourth])),
            AutoDecisionFailureReason.UnknownRule);

        SilenceSuggestionCandidate middle = fixture.CreateSilenceCandidate(
            new TimeRange(new RationalTime(4, 1), new RationalTime(2, 1)));
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request(
                    [middle],
                    sourceSpans: fixture.SourceSpans.Append(
                        middle.Payload.SourceSpan).ToArray())),
            AutoDecisionFailureReason.AmbiguousEvidence);
    }

    [TestMethod]
    public void SilenceSamplesMustMatchCanonicalTimelineAndSourceRange()
    {
        Fixture fixture = new();
        SilenceCandidate tail = fixture.TailSilence.Payload;

        Assert.ThrowsExactly<ArgumentException>(() => new SilenceCandidate(
            tail.AnalysisTimeline,
            tail.SourceSpan,
            tail.Detection,
            tail.Suggestion,
            tail.ThresholdVersion,
            analysisStartSample: 0,
            analysisEndSample: 32000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SilenceCandidate(
            tail.AnalysisTimeline,
            tail.SourceSpan,
            tail.Detection,
            tail.Suggestion,
            tail.ThresholdVersion,
            analysisStartSample: 128000,
            analysisEndSample: 160001));
        Assert.AreEqual(new RationalTime(2, 1), tail.SourceSpan.Range.Duration);
    }

    [TestMethod]
    public void RehashedSilenceSampleTamperingFailsSemanticEvidenceValidation()
    {
        Fixture fixture = new();
        AutoDecisionPreparation preparation = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.HeadSilence]));
        string versionJson = CompositionSnapshotJson.WriteVersion(preparation.Child);
        JsonObject root = JsonNode.Parse(versionJson)!.AsObject();
        JsonObject envelope = root["evidence"]!.AsObject();
        string evidenceJson = envelope["canonicalJson"]!.GetValue<string>();
        string tamperedEvidence = CanonicalJson.Canonicalize(
            evidenceJson.Replace(
                "\"analysisStartSample\":0",
                "\"analysisStartSample\":1",
                StringComparison.Ordinal));
        Assert.AreNotEqual(evidenceJson, tamperedEvidence);
        envelope["canonicalJson"] = tamperedEvidence;
        envelope["contentDigest"] = ContentDigest.Compute(
            Encoding.UTF8.GetBytes(tamperedEvidence)).Hex;
        string tamperedVersionJson = CanonicalJson.Canonicalize(root.ToJsonString());
        CompositionVersion tamperedVersion = CompositionSnapshotJson.ReadVersion(
            tamperedVersionJson);

        Assert.ThrowsExactly<FormatException>(() =>
            AutoDecisionEvidenceCodec.ValidateLoaded(
                tamperedVersion,
                fixture.Parent));
    }

    [TestMethod]
    public void OverlappingAndUnmappableRangesFailBeforeAnyEdit()
    {
        Fixture fixture = new();
        SilenceSuggestionCandidate overlapping = fixture.CreateSilenceCandidate(
            new TimeRange(default, new RationalTime(3, 1)));
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request(
                    [fixture.HeadSilence, overlapping],
                    sourceSpans: fixture.SourceSpans.Append(
                        overlapping.Payload.SourceSpan).ToArray())),
            AutoDecisionFailureReason.OverlapConflict);

        SilenceSuggestionCandidate unmapped = fixture.CreateSilenceCandidate(
            new TimeRange(default, new RationalTime(2, 1)),
            EntityId<AssetIdTag>.New());
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request(
                    [unmapped],
                    sourceSpans: fixture.SourceSpans.Append(
                        unmapped.Payload.SourceSpan).ToArray())),
            AutoDecisionFailureReason.RangeInvalid);
    }

    [TestMethod]
    public void PreparationCarriesCheckpointRevisionAndRollbackNeverRewritesParent()
    {
        Fixture fixture = new();
        AutoDecisionPreparation forward = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.Editorial]));
        var rollback = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            fixture.Parent.ProjectId,
            forward.Child.Id,
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Revert the automatic decision as a new child.",
            fixture.Parent.ContentDigest,
            fixture.Parent.Elements);

        Assert.AreEqual(17, forward.ExpectedWorkingCopyRevision);
        Assert.AreEqual(fixture.Parent.Id, forward.Child.ParentVersionId);
        Assert.AreEqual(forward.Child.Id, rollback.ParentVersionId);
        Assert.AreEqual(fixture.Parent.ContentDigest, rollback.ContentDigest);
        CollectionAssert.AreEqual(
            fixture.Parent.Elements.ToArray(),
            rollback.Elements.ToArray());
    }

    [TestMethod]
    public void ApplyAndRollbackUseAtomicCheckpointAndRejectStaleOrCancelledPersistence()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        var store = new ProjectStore(database.DatabasePath);
        var project = new Project(
            fixture.Parent.ProjectId,
            "V2-007 test project",
            "Verify atomic auto-decision persistence.",
            "Test user",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        store.CreateProject(project, "{\"project\":true}", CreatedAt.AddMinutes(-3));
        store.AppendCompositionVersion(
            fixture.Parent,
            CompositionSnapshotJson.WriteVersion(fixture.Parent));
        var storedDraft = new WorkingCopy(
            fixture.WorkingCopy.Id,
            fixture.WorkingCopy.ProjectId,
            fixture.Parent.Id,
            fixture.WorkingCopy.CreatedAt,
            fixture.WorkingCopy.UpdatedAt,
            fixture.WorkingCopy.Elements);
        store.CreateWorkingCopy(
            storedDraft,
            CompositionSnapshotJson.WriteWorkingCopy(storedDraft));
        var service = new VersionService(store);
        AutoDecisionRequest request = fixture.Request(
            fixture.ClosedCandidates,
            workingCopy: storedDraft,
            expectedWorkingCopyRevision: 0);

        AutoDecisionPreparation cancelledPreparation =
            AutoDecisionOrchestrator.Prepare(request);
        using (var applyCancellation = new CancellationTokenSource())
        {
            applyCancellation.Cancel();
            _ = Assert.ThrowsExactly<OperationCanceledException>(() =>
                AutoDecisionOrchestrator.Apply(
                    service,
                    request,
                    applyCancellation.Token));
        }

        Assert.IsNull(store.GetCompositionVersion(cancelledPreparation.Child.Id));
        Assert.AreEqual(0, store.GetWorkingCopy(project.Id)!.Revision);

        AutoDecisionApplication applied = AutoDecisionOrchestrator.Apply(
            service,
            request);

        Assert.AreEqual(
            applied.Preparation.Child.Id,
            applied.Checkpoint.Version.Id);
        Assert.AreEqual(
            fixture.Parent.Id,
            applied.Checkpoint.Version.ParentVersionId);
        Assert.AreEqual(1, applied.Checkpoint.WorkingCopyRevision);
        Assert.AreEqual(
            applied.Checkpoint.CanonicalVersionJson,
            store.GetCompositionVersion(applied.Checkpoint.Version.Id)!.CanonicalJson);
        Assert.IsNotNull(applied.Checkpoint.Version.Evidence);
        Assert.AreEqual("autoDecision", applied.Checkpoint.Version.Evidence.Kind);
        CompositionVersion restartedChild = new VersionService(
            new ProjectStore(database.DatabasePath)).GetVersion(
                applied.Checkpoint.Version.Id)!;
        Assert.AreEqual(
            applied.Checkpoint.Version.Evidence.ContentDigest,
            restartedChild.Evidence!.ContentDigest);
        Assert.AreEqual(
            applied.Checkpoint.Version.Evidence.CanonicalJson,
            restartedChild.Evidence.CanonicalJson);
        StoredWorkingCopy persistedAfterApply = store.GetWorkingCopy(project.Id)!;
        Assert.AreEqual(1, persistedAfterApply.Revision);
        Assert.AreEqual(applied.Checkpoint.Version.Id, persistedAfterApply.BaseVersionId);

        AutoDecisionApplication duplicate = AutoDecisionOrchestrator.Apply(
            new VersionService(new ProjectStore(database.DatabasePath)),
            request);
        Assert.AreEqual(applied.Checkpoint.Version.Id, duplicate.Checkpoint.Version.Id);
        Assert.AreEqual(
            applied.Checkpoint.CanonicalVersionJson,
            duplicate.Checkpoint.CanonicalVersionJson);
        Assert.AreEqual(1, duplicate.Checkpoint.WorkingCopyRevision);
        Assert.AreEqual(
            applied.Checkpoint.CanonicalVersionJson,
            store.GetCompositionVersion(applied.Checkpoint.Version.Id)!.CanonicalJson);
        Assert.AreEqual(1, store.GetWorkingCopy(project.Id)!.Revision);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _ = Assert.ThrowsExactly<OperationCanceledException>(() =>
            AutoDecisionOrchestrator.RollbackAsChild(
                service,
                applied,
                fixture.Parent,
                expectedWorkingCopyRevision: 1,
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Cancelled rollback.",
                cancellation.Token));
        Assert.AreEqual(1, store.GetWorkingCopy(project.Id)!.Revision);

        AutoDecisionRollback rollback = AutoDecisionOrchestrator.RollbackAsChild(
            new VersionService(new ProjectStore(database.DatabasePath)),
            applied.Checkpoint.Version.Id,
            fixture.Parent.Id,
            expectedWorkingCopyRevision: 1,
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Restore the parent content as a child.");

        Assert.AreEqual(
            applied.Checkpoint.Version.Id,
            rollback.Checkpoint.Version.ParentVersionId);
        Assert.AreEqual(
            fixture.Parent.ContentDigest,
            rollback.Checkpoint.Version.ContentDigest);
        Assert.AreEqual(2, rollback.Checkpoint.WorkingCopyRevision);
        Assert.AreEqual(
            rollback.Checkpoint.Version.Id,
            rollback.TimelineMap.CompositionVersionId);
        Assert.AreEqual(
            fixture.Parent.ContentDigest,
            store.GetCompositionVersion(fixture.Parent.Id)!.ContentDigest);
        Assert.AreEqual(
            applied.Checkpoint.Version.ContentDigest,
            store.GetCompositionVersion(applied.Checkpoint.Version.Id)!.ContentDigest);

        _ = Assert.ThrowsExactly<StoreConcurrencyException>(() =>
            AutoDecisionOrchestrator.Apply(service, request));
        Assert.AreEqual(3, database.CountCompositionVersions());
    }

    [TestMethod]
    public async Task ConcurrentIdenticalApplyReturnsOneExactStoredChildToBothCallers()
    {
        Fixture fixture = new();
        using TemporaryDatabase database = new();
        var setupStore = new ProjectStore(database.DatabasePath);
        var project = new Project(
            fixture.Parent.ProjectId,
            "Concurrent auto-decision",
            "Verify exact duplicate resolution.",
            "Test user",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        setupStore.CreateProject(project, "{\"project\":true}", CreatedAt.AddMinutes(-3));
        setupStore.AppendCompositionVersion(
            fixture.Parent,
            CompositionSnapshotJson.WriteVersion(fixture.Parent));
        var storedDraft = new WorkingCopy(
            fixture.WorkingCopy.Id,
            fixture.WorkingCopy.ProjectId,
            fixture.Parent.Id,
            fixture.WorkingCopy.CreatedAt,
            fixture.WorkingCopy.UpdatedAt,
            fixture.WorkingCopy.Elements);
        setupStore.CreateWorkingCopy(
            storedDraft,
            CompositionSnapshotJson.WriteWorkingCopy(storedDraft));
        AutoDecisionRequest request = fixture.Request(
            fixture.ClosedCandidates,
            workingCopy: storedDraft,
            expectedWorkingCopyRevision: 0);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<AutoDecisionApplication> first = Task.Run(() =>
        {
            release.Wait();
            return AutoDecisionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                request);
        });
        Task<AutoDecisionApplication> second = Task.Run(() =>
        {
            release.Wait();
            return AutoDecisionOrchestrator.Apply(
                new VersionService(new ProjectStore(database.DatabasePath)),
                request);
        });
        release.Set();
        AutoDecisionApplication[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(results[0].Checkpoint.Version.Id, results[1].Checkpoint.Version.Id);
        Assert.AreEqual(
            results[0].Checkpoint.CanonicalVersionJson,
            results[1].Checkpoint.CanonicalVersionJson);
        Assert.IsTrue(results.All(static result =>
            result.Checkpoint.WorkingCopyRevision == 1));
        Assert.AreEqual(2, database.CountCompositionVersions());
        Assert.AreEqual(1, setupStore.GetWorkingCopy(project.Id)!.Revision);
    }

    [TestMethod]
    public void SemanticallyMatchingProfileIdentityIsAcceptedAndRuleChangeIsRejected()
    {
        Fixture fixture = new();
        DefaultSettingsProfile futureProfile = DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(),
            "Future semantically equivalent profile",
            new SettingsProfileVersion(2, 0, 0),
            fixture.Profile.Definitions);
        DefaultSettingsSnapshot equivalent = futureProfile.CreateSnapshot(
            fixture.Settings.Values);

        AutoDecisionPreparation accepted = AutoDecisionOrchestrator.Prepare(
            fixture.Request(
                [fixture.Editorial],
                settingsProfile: futureProfile,
                settings: equivalent));

        Assert.AreEqual(equivalent.ProfileId, accepted.Evidence.Single().SettingsProfileId);
        Assert.AreEqual(
            equivalent.ProfileVersion,
            accepted.Evidence.Single().SettingsProfileVersion);
        using JsonDocument evidence = JsonDocument.Parse(
            accepted.Child.Evidence!.CanonicalJson);
        Assert.AreEqual(
            equivalent.ProfileDigest.Hex,
            evidence.RootElement.GetProperty("input")
                .GetProperty("settingsSnapshot")
                .GetProperty("profileDigest").GetString());

        DefaultSettingsSnapshot changed = futureProfile.CreateSnapshot(
            equivalent.Values.Select(selection => new DefaultSettingSelection(
                selection.Key,
                selection.Key == "editing.silenceThreshold"
                    ? DefaultSettingValue.FromInteger(-49)
                    : selection.Value)).ToArray());
        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.Request(
                    [fixture.Editorial],
                    settingsProfile: futureProfile,
                    settings: changed)),
            AutoDecisionFailureReason.SettingsMismatch);
    }

    [TestMethod]
    public void FullModelRunPrivacyMetadataAndDecisionHistoryAffectIdentityAndEvidence()
    {
        Fixture fixture = new();
        ModelRun firstRun = CreateCloudModelRun(fixture.ModelRun.Id, "japaneast", "session-only");
        ModelRun secondRun = CreateCloudModelRun(fixture.ModelRun.Id, "westus3", "thirty-days");
        AutoDecisionPreparation first = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.TailSilence], modelRuns: [firstRun]));
        AutoDecisionPreparation second = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.TailSilence], modelRuns: [secondRun]));
        Assert.AreNotEqual(first.InputDigest, second.InputDigest);
        Assert.AreNotEqual(
            first.Evidence.Single().ModelRunDigest,
            second.Evidence.Single().ModelRunDigest);
        StringAssert.Contains(first.Child.Evidence!.CanonicalJson, "japaneast");
        StringAssert.Contains(first.Child.Evidence.CanonicalJson, "session-only");

        EntityId<SourceSpanIdTag> extraSpanId = EntityId<SourceSpanIdTag>.New();
        EntityId<AssetIdTag> extraAssetId = EntityId<AssetIdTag>.New();
        SourceSpan firstSource = new(
            extraSpanId,
            extraAssetId,
            "audio:9",
            new TimeRange(default, new RationalTime(1, 1)),
            Digest('4'),
            transcriptRevisionId: null,
            transcriptTokenIds: []);
        SourceSpan secondSource = new(
            extraSpanId,
            extraAssetId,
            "audio:9",
            firstSource.Range,
            Digest('5'),
            transcriptRevisionId: null,
            transcriptTokenIds: []);
        AutoDecisionPreparation firstSourceIdentity = AutoDecisionOrchestrator.Prepare(
            fixture.Request(
                [fixture.TailSilence],
                sourceSpans: fixture.SourceSpans.Append(firstSource).ToArray()));
        AutoDecisionPreparation secondSourceIdentity = AutoDecisionOrchestrator.Prepare(
            fixture.Request(
                [fixture.TailSilence],
                sourceSpans: fixture.SourceSpans.Append(secondSource).ToArray()));
        Assert.AreNotEqual(firstSourceIdentity.InputDigest, secondSourceIdentity.InputDigest);
        Assert.AreNotEqual(
            firstSourceIdentity.Child.Evidence!.ContentDigest,
            secondSourceIdentity.Child.Evidence!.ContentDigest);

        AutoDecisionPreparation prior = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.Editorial]));
        AutoDecisionPreparation withoutHistory = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.TailSilence]));
        AutoDecisionPreparation withHistory = AutoDecisionOrchestrator.Prepare(
            fixture.Request([fixture.TailSilence], prior.DecisionHistory));
        Assert.AreNotEqual(withoutHistory.InputDigest, withHistory.InputDigest);
        Assert.AreNotEqual(withoutHistory.Child.Id, withHistory.Child.Id);
        using JsonDocument historyEvidence = JsonDocument.Parse(
            withHistory.Child.Evidence!.CanonicalJson);
        Assert.HasCount(
            2,
            historyEvidence.RootElement.GetProperty("decisionHistory")
                .EnumerateArray().ToArray());
        StringAssert.Contains(
            withHistory.Child.Evidence.CanonicalJson,
            fixture.SilenceSourceDigest.Hex);
    }

    [TestMethod]
    public void NestedReusedSourceRangeCannotRedefineTailExtent()
    {
        Fixture fixture = new();
        SilenceSuggestionCandidate middle = fixture.CreateSilenceCandidate(
            new TimeRange(new RationalTime(7, 1), new RationalTime(2, 1)));

        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(
                fixture.CreateNestedExtentRequest(middle)),
            AutoDecisionFailureReason.AmbiguousEvidence);
    }

    [TestMethod]
    public void AdjacentExactTokenBlocksDeleteOnlyLaterBlock()
    {
        Fixture fixture = new();
        TokenSequenceCase tokenCase = fixture.CreateTokenSequenceCase(
            TokenSequenceShape.ExactAdjacent);

        AutoDecisionPreparation result = AutoDecisionOrchestrator.Prepare(
            tokenCase.Request);

        Assert.AreEqual(
            new TimeRange(new RationalTime(22, 1), new RationalTime(2, 1)),
            result.Evidence.Single().DeletedSourceRange);
        CollectionAssert.AreEqual(
            tokenCase.ComparisonTokenIds.ToArray(),
            result.Evidence.Single().ComparisonTokenIds.ToArray());
        using JsonDocument document = JsonDocument.Parse(
            result.Child.Evidence!.CanonicalJson);
        Assert.AreEqual(
            RepetitionReasonFeatures.AdjacentExactTokenSequenceRule,
            document.RootElement.GetProperty("decisions")[0]
                .GetProperty("candidate").GetProperty("payload")
                .GetProperty("features").GetProperty("rule").GetString());
    }

    [TestMethod]
    [DataRow(TokenSequenceShape.Fuzzy)]
    [DataRow(TokenSequenceShape.NonAdjacent)]
    [DataRow(TokenSequenceShape.Untimed)]
    public void InvalidTokenBlockShapesFailClosed(TokenSequenceShape shape)
    {
        Fixture fixture = new();
        TokenSequenceCase tokenCase = fixture.CreateTokenSequenceCase(shape);

        AssertFailure(
            () => AutoDecisionOrchestrator.Prepare(tokenCase.Request),
            AutoDecisionFailureReason.AmbiguousEvidence);
    }

    private static AutoDecisionException AssertFailure(
        Action action,
        AutoDecisionFailureReason expected)
    {
        AutoDecisionException exception =
            Assert.ThrowsExactly<AutoDecisionException>(action);
        Assert.AreEqual(expected, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    public enum TokenSequenceShape
    {
        ExactAdjacent,
        Fuzzy,
        NonAdjacent,
        Untimed,
    }

    private sealed record TokenSequenceCase(
        AutoDecisionRequest Request,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> ComparisonTokenIds);

    private sealed class Fixture
    {
        private readonly EntityId<ProjectIdTag> _projectId =
            EntityId<ProjectIdTag>.New();
        private readonly EntityId<AssetIdTag> _transcriptAssetId =
            EntityId<AssetIdTag>.New();
        private readonly EntityId<AssetIdTag> _silenceAssetId =
            EntityId<AssetIdTag>.New();
        private readonly SourceSpan _silenceFullSpan;

        internal Fixture()
        {
            SourceDigest = Digest('a');
            SilenceSourceDigest = Digest('b');
            EntityId<TranscriptRevisionIdTag> revisionId =
                EntityId<TranscriptRevisionIdTag>.New();
            EntityId<TranscriptSpeakerIdTag> firstSpeaker =
                EntityId<TranscriptSpeakerIdTag>.New();
            EntityId<TranscriptSpeakerIdTag> repeatedSpeaker =
                EntityId<TranscriptSpeakerIdTag>.New();
            (Utterance Explicit, SourceSpan ExplicitSpan) explicitEvidence =
                CreateUtterance(
                    revisionId,
                    firstSpeaker,
                    "ここはカット",
                    Range(0, 1));
            (Utterance Earlier, SourceSpan EarlierSpan) earlierEvidence =
                CreateUtterance(
                    revisionId,
                    repeatedSpeaker,
                    "もう一度",
                    Range(2, 1));
            (Utterance Later, SourceSpan LaterSpan) laterEvidence =
                CreateUtterance(
                    revisionId,
                    repeatedSpeaker,
                    "もう一度",
                    Range(3, 1));
            Transcript = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt.AddMinutes(-3),
                [
                    explicitEvidence.Explicit,
                    earlierEvidence.Earlier,
                    laterEvidence.Later,
                ]);
            TranscriptSpans =
            [
                explicitEvidence.ExplicitSpan,
                earlierEvidence.EarlierSpan,
                laterEvidence.LaterSpan,
            ];
            ContentDigest transcriptDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(Transcript)));
            ModelRun = CreateModelRun(EntityId<ModelRunIdTag>.New());

            Editorial = SuggestionService.Register(
                EditorialPhraseDetector.FindCandidates(
                    Transcript,
                    TranscriptSpans,
                    ModelRun.Id).Candidates.Single(),
                []);
            Repetition = CreateRepetition(
                earlierEvidence.Earlier,
                laterEvidence.Later,
                transcriptDigest);

            AnalysisAudioTimeline audioTimeline = CreateAudioTimeline();
            SilenceDetectionResult silence = SilenceDetector.ParseProcessResult(
                audioTimeline,
                ModelRun.Id,
                new ProcessRunResult(
                    0,
                    string.Empty,
                    "[silencedetect @ a] silence_start: 0\n" +
                        "[silencedetect @ a] silence_end: 2 | silence_duration: 2\n" +
                        "[silencedetect @ a] silence_start: 8\n" +
                        "[silencedetect @ a] silence_end: 10 | silence_duration: 2\n",
                    Truncated: false,
                    TimeSpan.FromMilliseconds(1)));
            HeadSilence = SuggestionService.Register(silence.Candidates[0], []);
            TailSilence = SuggestionService.Register(silence.Candidates[1], []);
            _silenceFullSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                _silenceAssetId,
                "audio:1",
                Range(0, 10),
                SilenceSourceDigest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            SourceSpans = TranscriptSpans
                .Concat([
                    Editorial.Payload.SourceSpan,
                    _silenceFullSpan,
                    HeadSilence.Payload.SourceSpan,
                    TailSilence.Payload.SourceSpan,
                ])
                .ToArray();

            CompositionElement[] elements =
            [
                SourceElement(
                    explicitEvidence.ExplicitSpan,
                    new RationalTime(0, 1)),
                SourceElement(
                    earlierEvidence.EarlierSpan,
                    new RationalTime(1, 1)),
                SourceElement(
                    laterEvidence.LaterSpan,
                    new RationalTime(2, 1)),
                SourceElement(_silenceFullSpan, new RationalTime(3, 1)),
            ];
            ContentDigest parentDigest = ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8(elements));
            Parent = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                _projectId,
                parentVersionId: null,
                ActorSid,
                CreatedAt.AddMinutes(-2),
                "Create the source composition.",
                parentDigest,
                elements);
            WorkingCopy = new WorkingCopy(
                EntityId<WorkingCopyIdTag>.New(),
                _projectId,
                Parent.Id,
                CreatedAt.AddMinutes(-1),
                CreatedAt.AddMinutes(-1),
                elements);
            Profile = LoadProfile();
            Settings = Profile.CreateSnapshot();
            ClosedCandidates =
            [
                Editorial,
                Repetition,
                HeadSilence,
                TailSilence,
            ];
        }

        internal ContentDigest SourceDigest { get; }

        internal ContentDigest SilenceSourceDigest { get; }

        internal TranscriptRevision Transcript { get; }

        internal IReadOnlyList<SourceSpan> TranscriptSpans { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal ModelRun ModelRun { get; }

        internal EditorialPhraseSuggestionCandidate Editorial { get; }

        internal RepetitionSuggestionCandidate Repetition { get; }

        internal SilenceSuggestionCandidate HeadSilence { get; }

        internal SilenceSuggestionCandidate TailSilence { get; }

        internal IReadOnlyList<SuggestionCandidate> ClosedCandidates { get; }

        internal CompositionVersion Parent { get; }

        internal WorkingCopy WorkingCopy { get; }

        internal DefaultSettingsProfile Profile { get; }

        internal DefaultSettingsSnapshot Settings { get; }

        internal AutoDecisionRequest Request(
            IReadOnlyList<SuggestionCandidate> candidates,
            IReadOnlyList<SuggestionDecisionRecord>? history = null,
            DefaultSettingsProfile? settingsProfile = null,
            DefaultSettingsSnapshot? settings = null,
            TranscriptRevision? transcript = null,
            IReadOnlyList<SourceSpan>? sourceSpans = null,
            IReadOnlyList<ModelRun>? modelRuns = null,
            WorkingCopy? workingCopy = null,
            int expectedWorkingCopyRevision = 17) =>
            new(
                Parent,
                workingCopy ?? WorkingCopy,
                expectedWorkingCopyRevision,
                settingsProfile ?? Profile,
                settings ?? Settings,
                transcript ?? Transcript,
                sourceSpans ?? SourceSpans,
                modelRuns ?? [ModelRun],
                candidates,
                history ?? [],
                ActorSid,
                CreatedAt,
                "Apply closed automatic decisions.");

        internal SilenceSuggestionCandidate CreateSilenceCandidate(
            TimeRange range,
            EntityId<AssetIdTag>? assetId = null)
        {
            EntityId<AssetIdTag> actualAsset = assetId ?? _silenceAssetId;
            ContentDigest digest = actualAsset == _silenceAssetId
                ? SilenceSourceDigest
                : Digest('c');
            var span = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                actualAsset,
                "audio:1",
                range,
                digest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            OriginRef origin = OriginRef.FromSourceSpan(span.Id);
            var detection = new Detection(
                EntityId<DetectionIdTag>.New(),
                ModelRun.Id,
                DetectionKind.Silence,
                origin,
                "Exact test silence evidence.",
                confidence: null);
            var suggestion = new Suggestion(
                EntityId<SuggestionIdTag>.New(),
                ModelRun.Id,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                origin,
                "Exact test silence candidate.",
                confidence: null,
                "Editorial meaning is not inferred.",
                "Review before manual use.");
            var payload = new SilenceCandidate(
                CreateAudioTimeline(actualAsset, digest),
                span,
                detection,
                suggestion,
                SilenceDetector.ThresholdVersion,
                analysisStartSample: checked(range.Start.Value * 16000 / range.Start.Rate),
                analysisEndSample: checked(range.End.Value * 16000 / range.End.Rate));
            return SuggestionService.Register(payload, []);
        }

        internal AutoDecisionRequest CreateNestedExtentRequest(
            SilenceSuggestionCandidate candidate)
        {
            var nestedSpan = new SourceSpan(
                EntityId<SourceSpanIdTag>.New(),
                _silenceAssetId,
                "audio:1",
                new TimeRange(new RationalTime(8, 1), new RationalTime(1, 1)),
                SilenceSourceDigest,
                transcriptRevisionId: null,
                transcriptTokenIds: []);
            CompositionElement nestedElement = SourceElement(
                nestedSpan,
                Parent.Elements[^1].OutputRange.End);
            CompositionElement[] elements = Parent.Elements.Append(nestedElement).ToArray();
            ContentDigest digest = ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8(elements));
            var parent = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Parent.ProjectId,
                parentVersionId: null,
                ActorSid,
                Parent.CreatedAt,
                "Create nested source extent fixture.",
                digest,
                elements);
            var workingCopy = new WorkingCopy(
                EntityId<WorkingCopyIdTag>.New(),
                parent.ProjectId,
                parent.Id,
                WorkingCopy.CreatedAt,
                WorkingCopy.UpdatedAt,
                elements);
            return new AutoDecisionRequest(
                parent,
                workingCopy,
                expectedWorkingCopyRevision: 0,
                Profile,
                Settings,
                Transcript,
                SourceSpans.Append(candidate.Payload.SourceSpan)
                    .Append(nestedSpan)
                    .ToArray(),
                [ModelRun],
                [candidate],
                [],
                ActorSid,
                CreatedAt,
                "Reject nested middle silence.");
        }

        internal TokenSequenceCase CreateTokenSequenceCase(TokenSequenceShape shape)
        {
            EntityId<TranscriptRevisionIdTag> revisionId =
                EntityId<TranscriptRevisionIdTag>.New();
            EntityId<SourceSpanIdTag> spanId = EntityId<SourceSpanIdTag>.New();
            string[] texts = shape == TokenSequenceShape.Fuzzy
                ? ["A", "B", "A", "C"]
                : shape == TokenSequenceShape.NonAdjacent
                    ? ["A", "B", "gap", "A", "B"]
                    : ["A", "B", "A", "B"];
            var tokens = new List<Token>(texts.Length);
            for (int index = 0; index < texts.Length; index++)
            {
                TimeRange? range = shape == TokenSequenceShape.Untimed && index == 3
                    ? null
                    : new TimeRange(
                        new RationalTime(20 + index, 1),
                        new RationalTime(1, 1));
                tokens.Add(new Token(
                    EntityId<TranscriptTokenIdTag>.New(),
                    texts[index],
                    range,
                    recognitionConfidence: 1d));
            }

            var sentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                string.Concat(texts),
                tokens);
            var utterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                spanId,
                EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker",
                string.Concat(texts),
                new TimeRange(
                    new RationalTime(20, 1),
                    new RationalTime(texts.Length, 1)),
                recognitionConfidence: 1d,
                [sentence]);
            var transcript = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt.AddMinutes(-3),
                [utterance]);
            var span = new SourceSpan(
                spanId,
                _transcriptAssetId,
                "audio:0",
                utterance.Range,
                SourceDigest,
                revisionId,
                tokens.Select(static token => token.Id).ToArray());
            int laterStart = shape == TokenSequenceShape.NonAdjacent ? 3 : 2;
            EntityId<TranscriptTokenIdTag>[] comparisonIds = tokens
                .Take(2).Select(static token => token.Id).ToArray();
            EntityId<TranscriptTokenIdTag>[] laterIds = tokens
                .Skip(laterStart).Take(2).Select(static token => token.Id).ToArray();
            ContentDigest transcriptDigest = ContentDigest.Compute(
                Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(transcript)));
            var features = new RepetitionReasonFeatures(
                RepetitionReasonFeatures.AdjacentExactTokenSequenceRule,
                spanId,
                comparisonIds,
                matchingTokenCount: 2,
                exactNfkcMatch: true,
                structurallyAdjacent: true,
                sameSpeakerRequired: false,
                candidateIsLaterOccurrence: true,
                retentionChoiceRequired: true);
            ContentDigest key = RepetitionSuggestionPayload.ComputeCandidateKey(
                transcript.Id,
                transcriptDigest,
                features.Rule,
                spanId,
                laterIds,
                spanId,
                comparisonIds);
            const string reason = "Adjacent exact token blocks.";
            var payload = new RepetitionSuggestionPayload(
                key,
                ModelRun.Id,
                transcript.Id,
                transcriptDigest,
                spanId,
                laterIds,
                RepetitionSuggestionPayload.CanonicalRuleVersion,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                reason,
                confidence: null,
                automaticDeletionAllowed: false,
                humanReviewRequired: true,
                features);
            var suggestion = new Suggestion(
                EntityId<SuggestionIdTag>.New(),
                ModelRun.Id,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                OriginRef.FromSourceSpan(spanId),
                reason,
                confidence: null,
                missingContext: null,
                warning: null);
            RepetitionSuggestionCandidate candidate = SuggestionService.Register(
                payload,
                suggestion,
                []);
            CompositionElement element = SourceElement(span, default);
            ContentDigest parentDigest = ContentDigest.Compute(
                CompositionSnapshotJson.WriteElementsObjectToUtf8([element]));
            var parent = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Parent.ProjectId,
                parentVersionId: null,
                ActorSid,
                CreatedAt.AddMinutes(-2),
                "Create token repetition fixture.",
                parentDigest,
                [element]);
            var workingCopy = new WorkingCopy(
                EntityId<WorkingCopyIdTag>.New(),
                parent.ProjectId,
                parent.Id,
                CreatedAt.AddMinutes(-1),
                CreatedAt.AddMinutes(-1),
                [element]);
            var request = new AutoDecisionRequest(
                parent,
                workingCopy,
                expectedWorkingCopyRevision: 0,
                Profile,
                Settings,
                transcript,
                [span],
                [ModelRun],
                [candidate],
                [],
                ActorSid,
                CreatedAt,
                "Apply token repetition.");
            return new TokenSequenceCase(request, comparisonIds);
        }

        private RepetitionSuggestionCandidate CreateRepetition(
            Utterance earlier,
            Utterance later,
            ContentDigest transcriptDigest)
        {
            var features = new RepetitionReasonFeatures(
                RepetitionReasonFeatures.AdjacentExactUtteranceRule,
                earlier.SourceSpanId,
                earlier.Tokens.Select(static token => token.Id).ToArray(),
                matchingTokenCount: later.Tokens.Count,
                exactNfkcMatch: true,
                structurallyAdjacent: true,
                sameSpeakerRequired: true,
                candidateIsLaterOccurrence: true,
                retentionChoiceRequired: true);
            ContentDigest key = RepetitionSuggestionPayload.ComputeCandidateKey(
                Transcript.Id,
                transcriptDigest,
                features.Rule,
                later.SourceSpanId,
                later.Tokens.Select(static token => token.Id).ToArray(),
                earlier.SourceSpanId,
                earlier.Tokens.Select(static token => token.Id).ToArray());
            const string reason =
                "Adjacent utterances have the same exact normalized token sequence.";
            var payload = new RepetitionSuggestionPayload(
                key,
                ModelRun.Id,
                Transcript.Id,
                transcriptDigest,
                later.SourceSpanId,
                later.Tokens.Select(static token => token.Id).ToArray(),
                RepetitionSuggestionPayload.CanonicalRuleVersion,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                reason,
                confidence: null,
                automaticDeletionAllowed: false,
                humanReviewRequired: true,
                features);
            var suggestion = new Suggestion(
                EntityId<SuggestionIdTag>.New(),
                ModelRun.Id,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                OriginRef.FromSourceSpan(later.SourceSpanId),
                reason,
                confidence: null,
                missingContext: null,
                warning: null);
            return SuggestionService.Register(payload, suggestion, []);
        }

        private (Utterance Utterance, SourceSpan SourceSpan) CreateUtterance(
            EntityId<TranscriptRevisionIdTag> revisionId,
            EntityId<TranscriptSpeakerIdTag> speakerId,
            string text,
            TimeRange range)
        {
            EntityId<SourceSpanIdTag> spanId = EntityId<SourceSpanIdTag>.New();
            var token = new Token(
                EntityId<TranscriptTokenIdTag>.New(),
                text,
                range,
                recognitionConfidence: 1d);
            var sentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                text,
                [token]);
            var utterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                spanId,
                speakerId,
                "Speaker",
                text,
                range,
                recognitionConfidence: 1d,
                [sentence]);
            var span = new SourceSpan(
                spanId,
                _transcriptAssetId,
                "audio:0",
                range,
                SourceDigest,
                revisionId,
                [token.Id]);
            return (utterance, span);
        }

        private AnalysisAudioTimeline CreateAudioTimeline(
            EntityId<AssetIdTag>? assetId = null,
            ContentDigest? sourceDigest = null) =>
            new(
                _projectId,
                EntityId<JobIdTag>.New(),
                assetId ?? _silenceAssetId,
                sourceDigest ?? SilenceSourceDigest,
                sourceByteSize: 1000,
                EntityId<ArtifactIdTag>.New(),
                Digest('d'),
                sourceVideoStreamIndex: 0,
                sourceAudioStreamIndex: 1,
                new RationalTime(1, 16000),
                probeReportedAudioStart: null,
                firstDecodedAudioFrameTimestamp: 0,
                firstDecodedAudioFrameSampleCount: 1024,
                EntityId<ArtifactIdTag>.New(),
                Digest('e'),
                analysisAudioByteSize: 320044,
                analysisSampleCount: 160000,
                EntityId<ArtifactIdTag>.New());

        private static CompositionElement SourceElement(
            SourceSpan span,
            RationalTime outputStart) =>
            new(
                EntityId<CompositionElementIdTag>.New(),
                new TimeRange(outputStart, span.Range.Duration),
                OriginRef.FromSourceSpan(span.Id),
                adoptedSuggestion: null,
                span.Range);
    }

    private static ModelRun CreateModelRun(EntityId<ModelRunIdTag> id) =>
        new(
            id,
            "local-provider",
            "deterministic-test-model",
            "1.0.0",
            Digest('f'),
            ModelHashAvailability.Available,
            Digest('1'),
            Digest('2'),
            Digest('3'),
            ModelRunOutcome.Succeeded,
            errorCode: null,
            ModelExecutionLocation.Local,
            destination: null,
            region: null,
            sentDataClassification: null,
            retentionPolicy: null,
            CreatedAt.AddMinutes(-5),
            CreatedAt.AddMinutes(-4));

    private static ModelRun CreateCloudModelRun(
        EntityId<ModelRunIdTag> id,
        string region,
        string retentionPolicy) =>
        new(
            id,
            "cloud-provider",
            "deterministic-test-model",
            "2.0.0",
            modelHash: null,
            ModelHashAvailability.NotDisclosed,
            Digest('1'),
            Digest('2'),
            Digest('3'),
            ModelRunOutcome.Succeeded,
            errorCode: null,
            ModelExecutionLocation.Cloud,
            "https://model.example.invalid",
            region,
            "transcript",
            retentionPolicy,
            CreatedAt.AddMinutes(-5),
            CreatedAt.AddMinutes(-4));

    private static DefaultSettingsProfile LoadProfile()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solution = Path.Combine(directory.FullName, "HubRadio.slnx");
            if (File.Exists(solution))
            {
                string path = Path.Combine(
                    directory.FullName,
                    "src",
                    "HubRadio.App",
                    "DefaultSettings",
                    "Profiles",
                    "1.0.0.json");
                return DefaultSettingsProfile.ParseCanonical(File.ReadAllText(path));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private sealed class TemporaryDatabase : IDisposable
    {
        internal TemporaryDatabase()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.AutoDecisionOrchestratorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
        }

        private string RootPath { get; }

        internal string DatabasePath { get; }

        internal long CountCompositionVersions()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM composition_versions;";
            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("Version count was null."));
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}