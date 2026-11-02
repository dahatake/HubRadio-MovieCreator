using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Workflow;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class QuickStartWorkflowTests
{
    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<CompositionVersionIdTag> VersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "22222222-2222-4222-8222-222222222222");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag> ArtifactId =
        EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "33333333-3333-4333-8333-333333333333");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag>
        SecondArtifactId = EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "33333333-3333-4333-8333-333333333334");

    private static readonly EntityId<DefaultSettingsProfileIdTag> SettingsProfileId =
        EntityId<DefaultSettingsProfileIdTag>.Parse(
            "44444444-4444-4444-8444-444444444444");

    private static readonly ContentDigest VersionDigest = Digest('a');
    private static readonly ContentDigest ArtifactDigest = Digest('b');
    private static readonly ContentDigest SecondArtifactDigest = Digest('6');
    private static readonly ContentDigest SettingsDigest = Digest('c');
    private static readonly ContentDigest EvidenceDigest = Digest('d');
    private static readonly QuickStartProjectSnapshot ProjectSnapshot =
        new(0, Digest('5'));
    private static readonly DateTimeOffset EventTime =
        new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static readonly QuickStartActor UserActor =
        QuickStartActor.User("S-1-5-21-1000");

    private static readonly QuickStartActor SystemActor =
        QuickStartActor.System(QuickStartSystemActor.AutomatedEvaluation);

    [TestMethod]
    // FR-029 AC-077: 自動代理評価の結果（PASS・WARNING・FAIL・未評価）が閉じた集合で区別される。
    public void EvaluationPolicyClosesPassWarningNotEvaluatedAndFailureOutcomes()
    {
        AutomatedEvaluationRun pass = CreateRun();
        AutomatedEvaluationRun warning = CreateRun(
            (AutomatedEvaluationRuleId.Loudness, AutomatedEvaluationStatus.Warning));
        AutomatedEvaluationRun nonblockingNotEvaluated = CreateRun(
            (AutomatedEvaluationRuleId.Keyboard,
                AutomatedEvaluationStatus.NotEvaluated));
        AutomatedEvaluationRun blockingWarning = CreateRun(
            (AutomatedEvaluationRuleId.Codec, AutomatedEvaluationStatus.Warning));
        AutomatedEvaluationRun blockingNotEvaluated = CreateRun(
            (AutomatedEvaluationRuleId.SettingsBinding,
                AutomatedEvaluationStatus.NotEvaluated));
        AutomatedEvaluationRun nonblockingFailure = CreateRun(
            (AutomatedEvaluationRuleId.Loudness, AutomatedEvaluationStatus.Fail));
        AutomatedEvaluationRun avWarning = CreateRun(
            (AutomatedEvaluationRuleId.AvSync, AutomatedEvaluationStatus.Warning));
        AutomatedEvaluationRun avNotEvaluated = CreateRun(
            (AutomatedEvaluationRuleId.AvSync,
                AutomatedEvaluationStatus.NotEvaluated));
        AutomatedEvaluationRun avFailure = CreateRun(
            (AutomatedEvaluationRuleId.AvSync, AutomatedEvaluationStatus.Fail));
        AutomatedEvaluationRun artifactless = CreateRun(includeArtifact: false);

        Assert.AreEqual(
            AutomatedEvaluationStatus.Pass,
            pass.AggregateStatus,
            "All machine rules must actually pass in the Ready fixture.");
        Assert.IsTrue(pass.Limitations.All(static limitation =>
            limitation.Status == AutomatedEvaluationStatus.NotEvaluated));
        Assert.AreEqual(
            QuickStartWorkflowState.Ready,
            QuickStartEvaluationPolicy.DetermineCompletedState(pass));
        Assert.AreEqual(
            QuickStartWorkflowState.ReadyWithWarnings,
            QuickStartEvaluationPolicy.DetermineCompletedState(warning));
        Assert.AreEqual(
            AutomatedEvaluationStatus.NotEvaluated,
            nonblockingNotEvaluated.AggregateStatus);
        Assert.AreEqual(
            QuickStartWorkflowState.ReadyWithWarnings,
            QuickStartEvaluationPolicy.DetermineCompletedState(
                nonblockingNotEvaluated));
        Assert.AreEqual(
            QuickStartWorkflowState.Failed,
            QuickStartEvaluationPolicy.DetermineCompletedState(blockingWarning));
        Assert.AreEqual(
            QuickStartWorkflowState.Failed,
            QuickStartEvaluationPolicy.DetermineCompletedState(
                blockingNotEvaluated));
        Assert.AreEqual(
            QuickStartWorkflowState.Failed,
            QuickStartEvaluationPolicy.DetermineCompletedState(nonblockingFailure));
        Assert.AreEqual(
            QuickStartWorkflowState.ReadyWithWarnings,
            QuickStartEvaluationPolicy.DetermineCompletedState(avWarning));
        Assert.AreEqual(
            QuickStartWorkflowState.ReadyWithWarnings,
            QuickStartEvaluationPolicy.DetermineCompletedState(avNotEvaluated));
        Assert.AreEqual(
            QuickStartWorkflowState.Failed,
            QuickStartEvaluationPolicy.DetermineCompletedState(avFailure));
        Assert.AreEqual(
            QuickStartWorkflowState.Failed,
            QuickStartEvaluationPolicy.DetermineCompletedState(artifactless));
    }

    [TestMethod]
    // FR-029 AC-077: 任意の注記を記録でき、出力の権限（承認）を生まない。
    public void LifecycleAllowsOptionalAnnotationsWithoutCreatingAnExportAuthority()
    {
        AutomatedEvaluationRun run = CreateRun(
            (AutomatedEvaluationRuleId.TruePeak,
                AutomatedEvaluationStatus.Warning));
        QuickStartWorkflow workflow = Start(run);
        workflow = workflow.Append(QuickStartWorkflowEvent.ChangesRequested(
            NewEventId(),
            workflow.Binding,
            UserActor,
            EventTime.AddSeconds(1),
            "editing.subtitle_change"));
        Assert.AreEqual(QuickStartWorkflowState.Editing, workflow.State);
        Assert.AreEqual(1, workflow.ChangeRequestCount);
        workflow = workflow.Append(QuickStartWorkflowEvent.EvaluationStarted(
            NewEventId(),
            workflow.Binding,
            SystemActor,
            EventTime.AddSeconds(2),
            run.SettingsSnapshotDigest,
            ProjectSnapshot));
        run = BindPositiveFixtureToAttempt(run, workflow);
        workflow = workflow.Append(QuickStartWorkflowEvent.EvaluationCompleted(
            NewEventId(),
            workflow,
            run,
            SystemActor,
            EventTime.AddSeconds(3)));

        Assert.AreEqual(QuickStartWorkflowState.ReadyWithWarnings, workflow.State);
        Assert.AreEqual(1, workflow.ChangeRequestCount);
        Assert.AreEqual(0, workflow.SelfReviewCount);
        Assert.AreEqual(4, workflow.Revision);
    }

    [TestMethod]
    // FR-029 AC-077: self-review と修正依頼は任意の注記であり、新しい承認状態ではない。
    public void SelfReviewAndPostEvaluationChangeRequestAreAnnotationsNotNewStates()
    {
        QuickStartWorkflow ready = Complete(CreateRun());
        QuickStartWorkflow selfReviewed = ready.Append(
            QuickStartWorkflowEvent.SelfReviewed(
                NewEventId(),
                ready.Binding,
                UserActor,
                EventTime.AddSeconds(4)));
        QuickStartWorkflow annotated = selfReviewed.Append(
            QuickStartWorkflowEvent.ChangesRequested(
                NewEventId(),
                ready.Binding,
                UserActor,
                EventTime.AddSeconds(5),
                "review.revision_requested"));

        Assert.AreEqual(QuickStartWorkflowState.Ready, annotated.State);
        Assert.AreEqual(1, annotated.SelfReviewCount);
        Assert.AreEqual(1, annotated.ChangeRequestCount);
        QuickStartWorkflow reevaluating = annotated.Append(
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                annotated,
                SystemActor,
                EventTime.AddSeconds(6),
                ProjectSnapshot));
        Assert.AreEqual(
            QuickStartWorkflowState.AutomatedEvaluation,
            reevaluating.State);
        Assert.IsNull(reevaluating.EvaluationId);
        Assert.IsNull(reevaluating.ArtifactId);
        Assert.AreEqual(1, reevaluating.SelfReviewCount);
        Assert.AreEqual(1, reevaluating.ChangeRequestCount);
    }

    [TestMethod]
    public void TechnicalReevaluationPreservesHistoryAndReplacesCurrentBinding()
    {
        AutomatedEvaluationRun firstRun = CreateRun();
        QuickStartWorkflow firstReady = Complete(firstRun);
        QuickStartWorkflowEvent firstCompletion = firstReady.Events[^1];

        QuickStartWorkflow reevaluating = firstReady.Append(
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                firstReady,
                SystemActor,
                EventTime.AddSeconds(3),
                ProjectSnapshot));

        AutomatedEvaluationRun secondRun = CreateRun(
            settingsDigest: firstRun.SettingsSnapshotDigest,
            artifactId: SecondArtifactId,
            artifactDigest: SecondArtifactDigest,
            evaluatedAt: EventTime.AddSeconds(3),
            inputDigest: reevaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(3));

        Assert.AreEqual(
            QuickStartWorkflowState.AutomatedEvaluation,
            reevaluating.State);
        Assert.AreEqual(
            QuickStartWorkflowEventKind.TechnicalReevaluationStarted,
            reevaluating.Events[^1].Kind);
        Assert.AreEqual(
            firstReady.Binding.ProjectId,
            reevaluating.Events[^1].Binding.ProjectId);
        Assert.AreEqual(
            firstReady.Binding.CompositionVersionId,
            reevaluating.Events[^1].Binding.CompositionVersionId);
        Assert.AreEqual(
            firstReady.Binding.CompositionVersionDigest,
            reevaluating.Events[^1].Binding.CompositionVersionDigest);
        Assert.AreEqual(
            firstRun.SettingsSnapshotDigest,
            reevaluating.Events[^1].SettingsSnapshotDigest);
        Assert.AreEqual(
            EventTime.AddSeconds(3),
            reevaluating.Events[^1].OccurredAt);
        Assert.IsNull(reevaluating.EvaluationId);
        Assert.IsNull(reevaluating.EvaluationCanonicalDigest);
        Assert.IsNull(reevaluating.ArtifactId);
        Assert.IsTrue(reevaluating.Events.Contains(firstCompletion));

        AutomatedEvaluationRun staleCompletion = CreateRun(
            settingsDigest: Digest('8'),
            evaluatedAt: EventTime.AddSeconds(3),
            inputDigest: reevaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(3));
        Assert.ThrowsExactly<InvalidOperationException>(() => reevaluating.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                reevaluating,
                staleCompletion,
                SystemActor,
                EventTime.AddSeconds(4))));
        QuickStartWorkflow secondReady = reevaluating.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                reevaluating,
                secondRun,
                SystemActor,
                EventTime.AddSeconds(4)));

        Assert.AreEqual(QuickStartWorkflowState.Ready, secondReady.State);
        Assert.AreEqual(secondRun.EvaluationId, secondReady.EvaluationId);
        Assert.AreEqual(
            secondRun.CanonicalDigest,
            secondReady.EvaluationCanonicalDigest);
        Assert.AreEqual(SecondArtifactId, secondReady.ArtifactId);
        Assert.AreEqual(SecondArtifactDigest, secondReady.ArtifactDigest);
        Assert.AreEqual(ArtifactId, firstCompletion.ArtifactId);
        Assert.AreEqual(ArtifactDigest, firstCompletion.ArtifactDigest);
        Assert.AreEqual(5, secondReady.Revision);
        Assert.IsTrue(secondReady.Events.Contains(firstCompletion));
        Assert.AreEqual(
            2,
            secondReady.Events.Count(static item =>
                item.Kind == QuickStartWorkflowEventKind.EvaluationCompleted));
    }

    [TestMethod]
    public void EveryEvaluationAttemptRequiresFreshTimeAndUnusedEvaluationId()
    {
        QuickStartWorkflow initialEvaluating = CompleteToEvaluating(CreateRun());
        AutomatedEvaluationRun staleInitialRun = CreateRun(
            evaluatedAt: EventTime,
            inputDigest: initialEvaluating.GetEvaluationInputDigest(),
            startedAt: EventTime);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                initialEvaluating,
                staleInitialRun,
                SystemActor,
                EventTime.AddSeconds(2)));

        // Preserve the reducer's old evaluated-time coverage independently of the
        // producer factory's new attempt/start-time checks.
        AutomatedEvaluationRun validInitialRun = CreateRun(
            inputDigest: initialEvaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(1));
        QuickStartWorkflowEvent validInitialCompletion =
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                initialEvaluating,
                validInitialRun,
                SystemActor,
                EventTime.AddSeconds(2));
        QuickStartWorkflowEvent staleInitialCompletion =
            QuickStartWorkflowEvent.ParseCanonical(Mutate(
                validInitialCompletion.CanonicalJson,
                root => root["evaluationEvaluatedAt"] =
                    staleInitialRun.EvaluatedAt.ToString(
                        "O", global::System.Globalization.CultureInfo.InvariantCulture)));

        Assert.AreEqual(
            staleInitialRun.EvaluatedAt,
            staleInitialCompletion.EvaluationEvaluatedAt);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            initialEvaluating.Append(staleInitialCompletion));

        AutomatedEvaluationRun firstRun = CreateRun();
        QuickStartWorkflow firstReady = Complete(firstRun);
        QuickStartWorkflow reevaluating = firstReady.Append(
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                firstReady,
                SystemActor,
                EventTime.AddSeconds(3),
                ProjectSnapshot));

        AutomatedEvaluationRun staleReevaluation = CreateRun(
            settingsDigest: firstRun.SettingsSnapshotDigest,
            evaluatedAt: EventTime.AddSeconds(2),
            inputDigest: reevaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(2));
        Assert.ThrowsExactly<InvalidOperationException>(() => reevaluating.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                reevaluating,
                staleReevaluation,
                SystemActor,
                EventTime.AddSeconds(4))));

        DateTimeOffset freshEvaluationTime = EventTime.AddSeconds(3).AddTicks(1);
        AutomatedEvaluationRun reusedEvaluation = CreateRun(
            settingsDigest: firstRun.SettingsSnapshotDigest,
            evaluationId: firstRun.EvaluationId,
            evaluatedAt: freshEvaluationTime,
            inputDigest: reevaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(3));
        QuickStartWorkflowEvent reusedCompletion =
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                reevaluating,
                reusedEvaluation,
                SystemActor,
                EventTime.AddSeconds(4));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            reevaluating.Append(reusedCompletion));

        AutomatedEvaluationRun freshReevaluation = CreateRun(
            settingsDigest: firstRun.SettingsSnapshotDigest,
            artifactId: SecondArtifactId,
            artifactDigest: SecondArtifactDigest,
            evaluatedAt: freshEvaluationTime,
            inputDigest: reevaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(3));
        QuickStartWorkflow secondReady = reevaluating.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                reevaluating,
                freshReevaluation,
                SystemActor,
                EventTime.AddSeconds(4)));

        Assert.AreEqual(QuickStartWorkflowState.Ready, secondReady.State);
        Assert.AreEqual(freshReevaluation.EvaluationId, secondReady.EvaluationId);
        Assert.AreNotEqual(firstRun.EvaluationId, secondReady.EvaluationId);
        Assert.AreEqual(
            freshEvaluationTime,
            secondReady.Events[^1].EvaluationEvaluatedAt);
    }

    [TestMethod]
    [DataRow(1, '7')]
    [DataRow(1, '5')]
    [DataRow(0, '7')]
    public void LateOldSnapshotEvaluationIsRejectedEvenWithFreshEvaluatedAt(
        int newRevision,
        char newPayloadDigest)
    {
        QuickStartWorkflow firstAttempt = CompleteToEvaluating(CreateRun());
        AutomatedEvaluationRun firstRun = CreateRun(
            inputDigest: firstAttempt.GetEvaluationInputDigest(),
            startedAt: firstAttempt.Events[^1].OccurredAt);
        AutomatedEvaluationRun lateOldRun = CreateRun(
            evaluatedAt: EventTime.AddSeconds(4),
            inputDigest: firstAttempt.GetEvaluationInputDigest(),
            startedAt: firstAttempt.Events[^1].OccurredAt);
        QuickStartWorkflow ready = firstAttempt.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), firstAttempt, firstRun, SystemActor,
                EventTime.AddSeconds(2)));
        var newSnapshot = new QuickStartProjectSnapshot(
            newRevision, Digest(newPayloadDigest));
        QuickStartWorkflow newerAttempt = ready.Append(
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(), ready, SystemActor, EventTime.AddSeconds(3),
                newSnapshot));

        Assert.AreNotEqual(firstRun.EvaluationId, lateOldRun.EvaluationId);
        Assert.AreEqual(newerAttempt.SettingsSnapshotDigest, lateOldRun.SettingsSnapshotDigest);
        Assert.IsTrue(lateOldRun.EvaluatedAt > newerAttempt.Events[^1].OccurredAt);
        Assert.AreNotEqual(newerAttempt.GetEvaluationInputDigest(), lateOldRun.InputDigest);
        // The delayed run is valid for its original attempt, never for the newer row.
        QuickStartWorkflowEvent originalCompletion =
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), firstAttempt, lateOldRun, SystemActor,
                EventTime.AddSeconds(4));
        Assert.AreSame(ProjectSnapshot, originalCompletion.ProjectSnapshot);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), newerAttempt, lateOldRun, SystemActor,
                EventTime.AddSeconds(4)));
        Assert.AreSame(newSnapshot, newerAttempt.ProjectSnapshot);
        Assert.IsNull(newerAttempt.EvaluationId);
    }

    [TestMethod]
    [DataRow(1, '7')]
    [DataRow(1, '5')]
    [DataRow(0, '7')]
    [DataRow(0, '5')]
    public void ActiveEvaluationCanRestartWithFreshSnapshotWithoutRewritingHistory(
        int newRevision,
        char newPayloadDigest)
    {
        QuickStartWorkflow firstAttempt = CreateEvaluatingAttempt(technical: true);
        QuickStartWorkflowEvent previousCompletion = firstAttempt.Events[^2];
        QuickStartWorkflowEvent interruptedStart = firstAttempt.Events[^1];
        string[] originalHistory = firstAttempt.Events
            .Select(static item => item.CanonicalJson).ToArray();
        var freshSnapshot = new QuickStartProjectSnapshot(
            newRevision, Digest(newPayloadDigest));
        DateTimeOffset restartTime = EventTime.AddSeconds(4);
        QuickStartWorkflowEvent restart =
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(), firstAttempt, SystemActor, restartTime, freshSnapshot);
        QuickStartWorkflow restarted = firstAttempt.Append(restart);

        Assert.AreEqual(QuickStartWorkflowState.AutomatedEvaluation, restarted.State);
        Assert.AreSame(firstAttempt.Binding, restart.Binding);
        Assert.AreEqual(firstAttempt.SettingsSnapshotDigest, restart.SettingsSnapshotDigest);
        Assert.AreSame(freshSnapshot, restarted.ProjectSnapshot);
        Assert.AreNotSame(firstAttempt.ProjectSnapshot, restarted.ProjectSnapshot);
        Assert.AreNotEqual(interruptedStart.EventId, restart.EventId);
        Assert.AreSame(restart.CanonicalDigest, restarted.GetEvaluationInputDigest());
        Assert.AreNotEqual(firstAttempt.GetEvaluationInputDigest(), restarted.GetEvaluationInputDigest());
        Assert.IsNull(restarted.EvaluationId);
        Assert.IsNull(restarted.EvaluationAggregateStatus);
        Assert.IsNull(restarted.EvaluationCanonicalDigest);
        Assert.IsNull(restarted.ArtifactId);
        Assert.IsNull(restarted.ArtifactDigest);
        Assert.AreSame(previousCompletion, restarted.Events[^3]);
        Assert.AreSame(interruptedStart, restarted.Events[^2]);
        CollectionAssert.AreEqual(originalHistory, restarted.Events
            .Take(firstAttempt.Revision).Select(static item => item.CanonicalJson).ToArray());
        Assert.AreEqual(1, restarted.Events.Count(static item =>
            item.Kind == QuickStartWorkflowEventKind.EvaluationCompleted));
        Assert.ThrowsExactly<ArgumentException>(() => restarted.Append(
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                restart.EventId, restarted, SystemActor, restartTime, freshSnapshot)));

        // Time and settings satisfy the new attempt; only the old input digest
        // prevents this delayed result from attaching, even for the same snapshot.
        AutomatedEvaluationRun delayedOldRun = CreateRun(
            evaluatedAt: restartTime.AddSeconds(1),
            inputDigest: firstAttempt.GetEvaluationInputDigest(),
            startedAt: restartTime);
        QuickStartWorkflowEvent oldCompletion = QuickStartWorkflowEvent.EvaluationCompleted(
            NewEventId(), firstAttempt, delayedOldRun, SystemActor, restartTime.AddSeconds(1));
        Assert.AreSame(firstAttempt.ProjectSnapshot, oldCompletion.ProjectSnapshot);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), restarted, delayedOldRun, SystemActor, restartTime.AddSeconds(1)));

        QuickStartWorkflow replayed = QuickStartWorkflow.Reduce(restarted.Events
            .Select(static item => QuickStartWorkflowEvent.ParseCanonical(item.CanonicalJson)).ToArray());
        Assert.AreEqual(restart.CanonicalDigest, replayed.GetEvaluationInputDigest());
        Assert.AreEqual(freshSnapshot.Revision, replayed.ProjectSnapshot!.Revision);
        Assert.AreEqual(freshSnapshot.CanonicalPayloadDigest, replayed.ProjectSnapshot.CanonicalPayloadDigest);
        AutomatedEvaluationRun newRun = CreateRun(
            artifactId: SecondArtifactId,
            artifactDigest: SecondArtifactDigest,
            evaluatedAt: restartTime.AddSeconds(1),
            inputDigest: replayed.GetEvaluationInputDigest(),
            startedAt: restartTime);
        QuickStartWorkflow completed = replayed.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), replayed, newRun, SystemActor, restartTime.AddSeconds(1)));

        Assert.AreEqual(QuickStartWorkflowState.Ready, completed.State);
        Assert.AreEqual(newRun.EvaluationId, completed.EvaluationId);
        Assert.AreEqual(newRun.CanonicalDigest, completed.EvaluationCanonicalDigest);
        Assert.AreEqual(SecondArtifactId, completed.ArtifactId);
        Assert.AreEqual(SecondArtifactDigest, completed.ArtifactDigest);
        Assert.AreEqual(firstAttempt.Revision + 2, completed.Revision);
        CollectionAssert.AreEqual(originalHistory, completed.Events
            .Take(firstAttempt.Revision).Select(static item => item.CanonicalJson).ToArray());
        Assert.AreEqual(previousCompletion.EvaluationId, completed.Events[^4].EvaluationId);
        Assert.AreEqual(2, completed.Events.Count(static item =>
            item.Kind == QuickStartWorkflowEventKind.EvaluationCompleted));
        Assert.IsFalse(completed.Events.Any(item => item.EvaluationId == delayedOldRun.EvaluationId));
        Assert.AreEqual(QuickStartWorkflowState.AutomatedEvaluation, firstAttempt.State);
        Assert.AreEqual(interruptedStart.CanonicalDigest, firstAttempt.GetEvaluationInputDigest());
    }

    [TestMethod]
    public void SameSnapshotAndTimeWithDifferentAttemptIdRejectsOtherAttemptRun()
    {
        QuickStartWorkflow ready = Complete(CreateRun());
        DateTimeOffset startTime = EventTime.AddSeconds(3);
        QuickStartWorkflowEvent firstStart =
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(), ready, SystemActor, startTime, ProjectSnapshot);
        QuickStartWorkflowEvent otherStart =
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(), ready, SystemActor, startTime, ProjectSnapshot);
        QuickStartWorkflow firstAttempt = ready.Append(firstStart);
        QuickStartWorkflow otherAttempt = ready.Append(otherStart);
        AutomatedEvaluationRun run = CreateRun(
            evaluatedAt: startTime.AddTicks(1),
            inputDigest: firstAttempt.GetEvaluationInputDigest(),
            startedAt: startTime);

        Assert.AreNotEqual(firstStart.EventId, otherStart.EventId);
        Assert.AreEqual(firstStart.CanonicalJson, Mutate(
            otherStart.CanonicalJson,
            root => root["eventId"] = firstStart.EventId.ToString()),
            "The start events must differ ONLY by attempt event ID.");
        Assert.AreNotEqual(firstAttempt.GetEvaluationInputDigest(), otherAttempt.GetEvaluationInputDigest());
        Assert.AreEqual(otherStart.OccurredAt, run.StartedAt);
        QuickStartWorkflow accepted = firstAttempt.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), firstAttempt, run, SystemActor, startTime.AddTicks(1)));
        Assert.AreEqual(run.CanonicalDigest, accepted.EvaluationCanonicalDigest);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), otherAttempt, run, SystemActor, startTime.AddTicks(1)));
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    public void ExactActiveAttemptDigestAcceptsRunStartingAtOrAfterStart(
        bool technical,
        int startOffsetTicks)
    {
        QuickStartWorkflow evaluating = CreateEvaluatingAttempt(technical);
        QuickStartWorkflowEvent start = evaluating.Events[^1];
        Assert.AreEqual(
            technical
                ? QuickStartWorkflowEventKind.TechnicalReevaluationStarted
                : QuickStartWorkflowEventKind.EvaluationStarted,
            start.Kind);
        Assert.AreSame(start.CanonicalDigest, evaluating.GetEvaluationInputDigest());
        if (technical)
        {
            Assert.AreNotEqual(
                evaluating.Events[1].CanonicalDigest,
                evaluating.GetEvaluationInputDigest());
        }

        QuickStartWorkflow replayed = QuickStartWorkflow.Reduce(
            evaluating.Events.Select(static item =>
                QuickStartWorkflowEvent.ParseCanonical(item.CanonicalJson)).ToArray());
        Assert.AreEqual(start.CanonicalDigest, replayed.GetEvaluationInputDigest());
        DateTimeOffset evaluatedAt = start.OccurredAt.AddSeconds(1);
        AutomatedEvaluationRun run = CreateRun(
            evaluatedAt: evaluatedAt,
            inputDigest: replayed.GetEvaluationInputDigest(),
            startedAt: start.OccurredAt.AddTicks(startOffsetTicks));
        QuickStartWorkflowEvent completion = QuickStartWorkflowEvent.EvaluationCompleted(
            NewEventId(), replayed, run, SystemActor, evaluatedAt);
        QuickStartWorkflow completed = replayed.Append(completion);

        Assert.AreEqual(QuickStartWorkflowState.Ready, completed.State);
        Assert.AreEqual(run.EvaluationId, completed.EvaluationId);
        Assert.AreEqual(run.CanonicalDigest, completed.EvaluationCanonicalDigest);
        Assert.AreSame(replayed.ProjectSnapshot, completion.ProjectSnapshot);
        Assert.AreEqual(start.CanonicalDigest, evaluating.GetEvaluationInputDigest());
        Assert.ThrowsExactly<InvalidOperationException>(() => completed.GetEvaluationInputDigest());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExactAttemptDigestStillRejectsEarlierStartedAtWithFreshEvaluatedAt(
        bool technical)
    {
        QuickStartWorkflow evaluating = CreateEvaluatingAttempt(technical);
        DateTimeOffset startTime = evaluating.Events[^1].OccurredAt;
        AutomatedEvaluationRun staleStart = CreateRun(
            evaluatedAt: startTime.AddSeconds(1),
            inputDigest: evaluating.GetEvaluationInputDigest(),
            startedAt: startTime.AddTicks(-1));

        Assert.AreEqual(evaluating.GetEvaluationInputDigest(), staleStart.InputDigest);
        Assert.IsTrue(staleStart.EvaluatedAt > startTime);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), evaluating, staleStart, SystemActor,
                startTime.AddSeconds(1)));
    }

    [TestMethod]
    [DataRow(AutomatedEvaluationStatus.Pass, QuickStartWorkflowState.Ready)]
    [DataRow(AutomatedEvaluationStatus.Warning, QuickStartWorkflowState.ReadyWithWarnings)]
    [DataRow(AutomatedEvaluationStatus.Fail, QuickStartWorkflowState.Failed)]
    public void EvaluationInputDigestIsUnavailableBeforeAndAfterEvaluating(
        AutomatedEvaluationStatus status,
        QuickStartWorkflowState completedState)
    {
        QuickStartWorkflow editing = Start(CreateRun());
        Assert.ThrowsExactly<InvalidOperationException>(() => editing.GetEvaluationInputDigest());
        QuickStartWorkflow evaluating = editing.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                NewEventId(), editing.Binding, SystemActor, EventTime.AddSeconds(1),
                SettingsDigest, ProjectSnapshot));
        AutomatedEvaluationRun run = CreateRun(
            (AutomatedEvaluationRuleId.TruePeak, status),
            inputDigest: evaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(1));
        QuickStartWorkflow completed = evaluating.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), evaluating, run, SystemActor, EventTime.AddSeconds(2)));

        Assert.AreEqual(completedState, completed.State);
        Assert.ThrowsExactly<InvalidOperationException>(() => completed.GetEvaluationInputDigest());
    }

    [TestMethod]
    public void TechnicalReevaluationAllowsEveryCompletedStateOnlyForSameSettings()
    {
        (AutomatedEvaluationRun Initial,
            AutomatedEvaluationRun Reevaluation,
            QuickStartWorkflowState Expected)[] scenarios =
        [
            (
                CreateRun(),
                CreateRun(evaluatedAt: EventTime.AddSeconds(3)),
                QuickStartWorkflowState.Ready),
            (
                CreateRun((AutomatedEvaluationRuleId.TruePeak,
                    AutomatedEvaluationStatus.Warning)),
                CreateRun((AutomatedEvaluationRuleId.AvSync,
                    AutomatedEvaluationStatus.Warning),
                    evaluatedAt: EventTime.AddSeconds(3)),
                QuickStartWorkflowState.ReadyWithWarnings),
            (
                CreateRun((AutomatedEvaluationRuleId.Codec,
                    AutomatedEvaluationStatus.Fail)),
                CreateRun((AutomatedEvaluationRuleId.AvSync,
                    AutomatedEvaluationStatus.Fail),
                    evaluatedAt: EventTime.AddSeconds(3)),
                QuickStartWorkflowState.Failed),
        ];

        foreach (var scenario in scenarios)
        {
            QuickStartWorkflow completed = Complete(scenario.Initial);
            Assert.AreEqual(scenario.Expected, completed.State);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                completed.GetEvaluationInputDigest());
            QuickStartWorkflow reevaluating = completed.Append(
                QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                    NewEventId(),
                    completed,
                    SystemActor,
                    EventTime.AddSeconds(3),
                    ProjectSnapshot));
            Assert.AreEqual(
                QuickStartWorkflowState.AutomatedEvaluation,
                reevaluating.State);

            AutomatedEvaluationRun reevaluation = BindPositiveFixtureToAttempt(
                scenario.Reevaluation, reevaluating);
            QuickStartWorkflow reevaluated = reevaluating.Append(
                QuickStartWorkflowEvent.EvaluationCompleted(
                    NewEventId(),
                    reevaluating,
                    reevaluation,
                    SystemActor,
                    EventTime.AddSeconds(4)));
            Assert.AreEqual(scenario.Expected, reevaluated.State);
            Assert.AreEqual(
                scenario.Reevaluation.EvaluationId,
                reevaluated.EvaluationId);
            Assert.AreEqual(5, reevaluated.Revision);

            QuickStartWorkflow otherSettings = Complete(CreateRun(
                settingsDigest: Digest('8')));
            QuickStartWorkflowEvent wrongSettings =
                QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                    NewEventId(),
                    otherSettings,
                    SystemActor,
                    EventTime.AddSeconds(3),
                    ProjectSnapshot);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                completed.Append(wrongSettings));
            Assert.ThrowsExactly<InvalidOperationException>(() => completed.Append(
                QuickStartWorkflowEvent.EvaluationStarted(
                    NewEventId(),
                    completed.Binding,
                    SystemActor,
                    EventTime.AddSeconds(3),
                    scenario.Initial.SettingsSnapshotDigest,
                    ProjectSnapshot)));
        }
    }

    [TestMethod]
    public void ChildVersionBeginsIndependentEditingAggregateWithoutRewritingPrior()
    {
        QuickStartWorkflow completed = Complete(CreateRun());
        var childBinding = new QuickStartWorkflowBinding(
            ProjectId,
            EntityId<CompositionVersionIdTag>.Parse(
                "22222222-2222-4222-8222-222222222223"),
            Digest('e'));
        QuickStartWorkflow child = QuickStartWorkflow.Reduce(
        [
            QuickStartWorkflowEvent.StartedEditing(
                NewEventId(),
                childBinding,
                UserActor,
                EventTime.AddSeconds(10)),
        ]);

        Assert.AreEqual(QuickStartWorkflowState.Ready, completed.State);
        Assert.AreEqual(3, completed.Revision);
        Assert.AreEqual(VersionId, completed.Binding.CompositionVersionId);
        Assert.AreEqual(QuickStartWorkflowState.Editing, child.State);
        Assert.AreEqual(1, child.Revision);
        Assert.AreNotEqual(
            completed.Binding.CompositionVersionId,
            child.Binding.CompositionVersionId);
    }

    [TestMethod]
    public void ReducerRejectsSkipsRepeatsReverseTimeAndStaleBindings()
    {
        AutomatedEvaluationRun run = CreateRun();
        QuickStartWorkflow editing = Start(run);
        Assert.AreEqual(QuickStartWorkflowState.Editing, editing.State);
        QuickStartWorkflow evaluating = editing.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                NewEventId(),
                editing.Binding,
                SystemActor,
                EventTime.AddSeconds(1),
                run.SettingsSnapshotDigest,
                ProjectSnapshot));
        Assert.AreEqual(
            QuickStartWorkflowState.AutomatedEvaluation,
            evaluating.State);
        run = BindPositiveFixtureToAttempt(run, evaluating);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                editing,
                SystemActor,
                EventTime.AddSeconds(1),
                ProjectSnapshot));
        QuickStartWorkflowEvent technicalStart =
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                evaluating,
                SystemActor,
                EventTime.AddSeconds(3),
                ProjectSnapshot);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflow.Reduce([technicalStart]));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            editing.Append(technicalStart));
        Assert.ThrowsExactly<InvalidOperationException>(() => editing.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                editing,
                run,
                SystemActor,
                EventTime.AddSeconds(2))));
        Assert.ThrowsExactly<InvalidOperationException>(() => editing.Append(
            QuickStartWorkflowEvent.StartedEditing(
                NewEventId(),
                editing.Binding,
                UserActor,
                EventTime.AddSeconds(1))));
        Assert.ThrowsExactly<InvalidOperationException>(() => editing.Append(
            QuickStartWorkflowEvent.SelfReviewed(
                NewEventId(),
                editing.Binding,
                UserActor,
                EventTime.AddSeconds(1))));
        Assert.ThrowsExactly<InvalidOperationException>(() => evaluating.Append(
            QuickStartWorkflowEvent.ChangesRequested(
                NewEventId(),
                evaluating.Binding,
                UserActor,
                EventTime.AddSeconds(2),
                "evaluation.change_requested")));
        QuickStartWorkflow superseded = evaluating.Append(technicalStart);
        Assert.AreEqual(QuickStartWorkflowState.AutomatedEvaluation, superseded.State);
        Assert.AreSame(technicalStart.CanonicalDigest, superseded.GetEvaluationInputDigest());
        Assert.AreNotEqual(evaluating.GetEvaluationInputDigest(), superseded.GetEvaluationInputDigest());
        AutomatedEvaluationRun delayedRun = CreateRun(
            evaluatedAt: EventTime.AddSeconds(4),
            inputDigest: evaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(3));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(), superseded, delayedRun, SystemActor, EventTime.AddSeconds(4)));
        Assert.ThrowsExactly<InvalidOperationException>(() => evaluating.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                NewEventId(), evaluating.Binding, SystemActor, EventTime.AddSeconds(3),
                run.SettingsSnapshotDigest, ProjectSnapshot)));
        Assert.ThrowsExactly<InvalidOperationException>(() => evaluating.Append(
            QuickStartWorkflowEvent.ParseCanonical(Mutate(
                technicalStart.CanonicalJson,
                static root => root["settingsSnapshotDigest"] = Digest('8').Hex))));

        AutomatedEvaluationRun staleSettings = CreateRun(
            settingsDigest: Digest('f'),
            inputDigest: evaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => evaluating.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                evaluating,
                staleSettings,
                SystemActor,
                EventTime.AddSeconds(2))));

        QuickStartWorkflowEvent completion = QuickStartWorkflowEvent.EvaluationCompleted(
            NewEventId(), evaluating, run, SystemActor, EventTime.AddSeconds(2));
        QuickStartWorkflow ready = evaluating.Append(completion);
        Assert.AreEqual(QuickStartWorkflowState.Ready, ready.State);
        Assert.ThrowsExactly<ArgumentException>(() => ready.Append(completion));
        QuickStartWorkflowEvent repeatedCompletion = QuickStartWorkflowEvent.EvaluationCompleted(
            NewEventId(), evaluating, run, SystemActor, EventTime.AddSeconds(3));
        Assert.ThrowsExactly<InvalidOperationException>(() => ready.Append(repeatedCompletion));
        Assert.ThrowsExactly<InvalidOperationException>(() => ready.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                NewEventId(),
                ready.Binding,
                SystemActor,
                EventTime.AddSeconds(3),
                run.SettingsSnapshotDigest,
                ProjectSnapshot)));

        Assert.ThrowsExactly<InvalidOperationException>(() => editing.Append(
            QuickStartWorkflowEvent.ChangesRequested(
                NewEventId(),
                editing.Binding,
                UserActor,
                EventTime.AddTicks(-1),
                "editing.reverse_time")));

        var otherBinding = new QuickStartWorkflowBinding(
            ProjectId,
            EntityId<CompositionVersionIdTag>.New(),
            Digest('9'));
        Assert.ThrowsExactly<InvalidOperationException>(() => editing.Append(
            QuickStartWorkflowEvent.ChangesRequested(
                NewEventId(),
                otherBinding,
                UserActor,
                EventTime.AddSeconds(1),
                "editing.other_version")));
    }

    [TestMethod]
    public void ActorAndExportConstructionExposeOnlyClosedTrustedFactories()
    {
        Assert.AreEqual(
            0,
            typeof(QuickStartActor).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance).Length);
        Assert.AreEqual(
            "S-1-5-21-1000",
            QuickStartActor.User("S-1-5-21-1000").Id);
        Assert.AreEqual(
            "S-1-0-0",
            QuickStartActor.User("S-1-0-0").Id);
        Assert.AreEqual(
            "S-1-4294967295-0",
            QuickStartActor.User(
                "S-1-4294967295-0").Id);
        Assert.AreEqual(
            "S-1-0x000100000000-0",
            QuickStartActor.User("S-1-0x000100000000-0").Id);
        Assert.AreEqual(
            "S-1-0xffffffffffff-4294967295",
            QuickStartActor.User(
                "S-1-0xffffffffffff-4294967295").Id);
        string maximumSubauthoritySid = string.Concat(
            "S-1-5-",
            string.Join("-", Enumerable.Repeat("0", 15)));
        Assert.AreEqual(
            maximumSubauthoritySid,
            QuickStartActor.User(maximumSubauthoritySid).Id);
        string tooManySubauthorities = string.Concat(
            "S-1-5-",
            string.Join("-", Enumerable.Repeat("0", 16)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartActor.User(tooManySubauthorities));
        Assert.AreEqual(
            "automated-evaluation-v2",
            QuickStartActor.System(
                QuickStartSystemActor.AutomatedEvaluation).Id);
        Assert.AreEqual(
            "delivery-v2",
            QuickStartActor.System(QuickStartSystemActor.Delivery).Id);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            QuickStartActor.System((QuickStartSystemActor)999));

        Type assemblyType = typeof(QuickStartWorkflowEvent);
        Type exportFacts = assemblyType.Assembly.GetType(
            "HubRadio.Core.Workflow.QuickStartExportFacts",
            throwOnError: true)!;
        Type exportPolicy = assemblyType.Assembly.GetType(
            "HubRadio.Core.Workflow.QuickStartExportPolicy",
            throwOnError: true)!;
        Assert.IsFalse(exportFacts.IsPublic);
        Assert.IsFalse(exportPolicy.IsPublic);
        Assert.AreEqual(
            0,
            exportFacts.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Length);
        ConstructorInfo exportFactsConstructor = exportFacts.GetConstructors(
                BindingFlags.NonPublic | BindingFlags.Instance)
            .Single();
        Assert.IsTrue(exportFactsConstructor.IsAssembly);
        Assert.IsNull(typeof(QuickStartWorkflowEvent).GetMethod(
            "Exported",
            BindingFlags.Public | BindingFlags.Static));
        MethodInfo exportedFactory = typeof(QuickStartWorkflowEvent).GetMethod(
                "Exported",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException(
                "The trusted internal export factory is missing.");
        Assert.IsTrue(exportedFactory.IsAssembly);
        Assert.IsNull(typeof(QuickStartWorkflowEvent).GetProperty(
            "ExportFacts",
            BindingFlags.Public | BindingFlags.Instance));
        PropertyInfo exportFactsProperty =
            typeof(QuickStartWorkflowEvent).GetProperty(
                "ExportFacts",
                BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AssertFailedException(
                "The trusted internal export facts property is missing.");
        Assert.IsTrue(exportFactsProperty.GetMethod!.IsAssembly);
        CollectionAssert.AreEqual(
            new[] { "AutomatedEvaluation", "Delivery" },
            Enum.GetNames<QuickStartSystemActor>());
    }

    [TestMethod]
    [DataRow("user@example.com")]
    [DataRow("secret")]
    [DataRow("S-2-5-21-1000")]
    [DataRow("s-1-5-21-1000")]
    [DataRow("S-01-5-21-1000")]
    [DataRow("S-1-05-21-1000")]
    [DataRow("S-1-5-021-1000")]
    [DataRow("S-1-+5-21-1000")]
    [DataRow("S-1-5-21-1000 ")]
    [DataRow("S-1-4294967296-1")]
    [DataRow("S-1-281474976710655-1")]
    [DataRow("S-1-281474976710656-1")]
    [DataRow("S-1-0x0000ffffffff-1")]
    [DataRow("S-1-0X000100000000-1")]
    [DataRow("S-1-0x00010000000A-1")]
    [DataRow("S-1-0x100000000-1")]
    [DataRow("S-1-0x0000100000000-1")]
    [DataRow("S-1-0x00010000000g-1")]
    [DataRow("S-1-5-4294967296")]
    [DataRow("S-1-5")]
    public void UserActorRejectsSecretsAndNoncanonicalSids(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => QuickStartActor.User(value));
    }

    [TestMethod]
    public void EventFactoriesEnforceUserAndSystemActorRoles()
    {
        QuickStartWorkflow ready = Complete(CreateRun());
        QuickStartWorkflow evaluating = CompleteToEvaluating(CreateRun());
        AutomatedEvaluationRun run = CreateRun(
            inputDigest: evaluating.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartWorkflowEvent.StartedEditing(
                NewEventId(),
                Binding(),
                SystemActor,
                EventTime));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartWorkflowEvent.EvaluationStarted(
                NewEventId(),
                Binding(),
                UserActor,
                EventTime,
                SettingsDigest,
                ProjectSnapshot));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                ready,
                UserActor,
                EventTime.AddSeconds(3),
                ProjectSnapshot));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                ready,
                QuickStartActor.System(QuickStartSystemActor.Delivery),
                EventTime.AddSeconds(3),
                ProjectSnapshot));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                evaluating,
                run,
                UserActor,
                EventTime.AddSeconds(2)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            QuickStartWorkflowEvent.SelfReviewed(
                NewEventId(),
                Binding(),
                QuickStartActor.System(QuickStartSystemActor.Delivery),
                EventTime));
    }

    [TestMethod]
    public void EvaluationCompletedCannotPredateStoredEvaluation()
    {
        AutomatedEvaluationRun run = CreateRun();
        QuickStartWorkflow evaluating = CompleteToEvaluating(run);
        run = BindPositiveFixtureToAttempt(run, evaluating);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                evaluating,
                run,
                SystemActor,
                run.EvaluatedAt.AddTicks(-1)));

        QuickStartWorkflowEvent completed =
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                evaluating,
                run,
                SystemActor,
                run.EvaluatedAt);
        Assert.AreEqual(run.EvaluatedAt, completed.OccurredAt);
        Assert.AreEqual(run.EvaluatedAt, completed.EvaluationEvaluatedAt);
    }

    [TestMethod]
    public void CanonicalEventsRoundTripAndRejectUnknownMissingOrNoncanonicalEvidence()
    {
        QuickStartWorkflowEvent started = QuickStartWorkflowEvent.StartedEditing(
            NewEventId(),
            Binding(),
            UserActor,
            EventTime);

        QuickStartWorkflowEvent parsed = QuickStartWorkflowEvent.ParseCanonical(
            started.CanonicalJson);
        Assert.AreEqual(started.CanonicalJson, parsed.CanonicalJson);
        Assert.AreEqual(started.CanonicalDigest, parsed.CanonicalDigest);
        Assert.IsNull(parsed.EvaluationEvaluatedAt);

        QuickStartWorkflow ready = Complete(CreateRun());
        QuickStartWorkflowEvent technicalReevaluation =
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(),
                ready,
                SystemActor,
                EventTime.AddSeconds(3),
                ProjectSnapshot);
        using (JsonDocument document = JsonDocument.Parse(
            technicalReevaluation.CanonicalJson))
        {
            Assert.AreEqual(
                "TechnicalReevaluationStarted",
                document.RootElement.GetProperty("kind").GetString());
        }

        QuickStartWorkflowEvent parsedTechnicalReevaluation =
            QuickStartWorkflowEvent.ParseCanonical(
                technicalReevaluation.CanonicalJson);
        Assert.AreEqual(
            QuickStartWorkflowEventKind.TechnicalReevaluationStarted,
            parsedTechnicalReevaluation.Kind);
        Assert.IsNull(parsedTechnicalReevaluation.EvaluationEvaluatedAt);
        Assert.AreEqual(
            QuickStartWorkflowState.AutomatedEvaluation,
            ready.Append(parsedTechnicalReevaluation).State);
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            string.Concat(" ", started.CanonicalJson)));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(started.CanonicalJson, static root => root["unknown"] = true)));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(started.CanonicalJson, static root => root.Remove("actorId"))));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                started.CanonicalJson,
                static root => root["actorKind"] = "system")));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                started.CanonicalJson,
                static root => root["actorId"] = "user@example.com")));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                technicalReevaluation.CanonicalJson,
                static root => root["kind"] =
                    "technicalReevaluationStarted")));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                technicalReevaluation.CanonicalJson,
                static root => root["actorKind"] = "user")));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                technicalReevaluation.CanonicalJson,
                static root => root["actorId"] =
                    "Automated-Evaluation-v2")));

        AutomatedEvaluationRun warningRun = CreateRun(
            (AutomatedEvaluationRuleId.Loudness,
                AutomatedEvaluationStatus.Warning));
        QuickStartWorkflow warningEvaluating = CompleteToEvaluating(warningRun);
        warningRun = BindPositiveFixtureToAttempt(warningRun, warningEvaluating);
        QuickStartWorkflowEvent warningCompleted =
            QuickStartWorkflowEvent.EvaluationCompleted(
                NewEventId(),
                warningEvaluating,
                warningRun,
                SystemActor,
                EventTime.AddSeconds(2));
        Assert.AreEqual(
            warningRun.EvaluatedAt,
            QuickStartWorkflowEvent.ParseCanonical(warningCompleted.CanonicalJson)
                .EvaluationEvaluatedAt);
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                warningCompleted.CanonicalJson,
                static root => root["evaluationAggregateStatus"] = "PASS")));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                warningCompleted.CanonicalJson,
                static root => root["evaluationEvaluatedAt"] = null)));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                warningCompleted.CanonicalJson,
                static root =>
                {
                    root["artifactId"] = null;
                    root["artifactDigest"] = null;
                })));
        Assert.Throws<JsonException>(() => QuickStartWorkflowEvent.ParseCanonical(
            Mutate(
                warningCompleted.CanonicalJson,
                static root => root["actorId"] = "delivery-v2")));
    }

    [TestMethod]
    public void PublicCanonicalParserRejectsValidTrustedStoredExport()
    {
        QuickStartWorkflow ready = Complete(CreateRun());
        string storedExportCanonical = Mutate(
            ready.Events[^1].CanonicalJson,
            root =>
            {
                root["eventId"] = NewEventId().ToString();
                root["kind"] = "Exported";
                root["actorKind"] = "user";
                root["actorId"] = UserActor.Id;
                root["evaluationEvaluatedAt"] = null;
                root["evaluationResultState"] = null;
                root["export"] = new JsonObject
                {
                    ["publicationScope"] = "InternalOnly",
                    ["rightsAllowedForInternalUse"] = true,
                    ["externalDestinationRequested"] = false,
                    ["destinationKind"] = "localInternalDelivery",
                    ["authorizedState"] = "ready",
                    ["authorizedRevision"] = ready.Revision,
                };
            });

        Assert.Throws<JsonException>(() =>
            QuickStartWorkflowEvent.ParseCanonical(storedExportCanonical));
        Assert.IsNull(typeof(QuickStartWorkflowEvent).GetMethod(
            "ParseStoredCanonical",
            BindingFlags.Public | BindingFlags.Static));
        MethodInfo trustedParser = typeof(QuickStartWorkflowEvent).GetMethod(
                "ParseStoredCanonical",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException(
                "The trusted stored-event parser is missing.");
        Assert.IsTrue(trustedParser.IsAssembly);

        QuickStartWorkflowEvent parsed =
            trustedParser.Invoke(null, new object?[] { storedExportCanonical })
                as QuickStartWorkflowEvent
            ?? throw new AssertFailedException(
                "The trusted stored-event parser returned no event.");
        Assert.AreEqual(QuickStartWorkflowEventKind.Exported, parsed.Kind);
        Assert.AreEqual(storedExportCanonical, parsed.CanonicalJson);
        Assert.IsNull(parsed.EvaluationEvaluatedAt);
        QuickStartWorkflow exported = ready.Append(parsed);
        Assert.AreEqual(QuickStartWorkflowState.Exported, exported.State);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            exported.GetEvaluationInputDigest());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(), exported, SystemActor, EventTime.AddSeconds(3), ProjectSnapshot));
        QuickStartWorkflowEvent technicalStart =
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                NewEventId(), ready, SystemActor, EventTime.AddSeconds(3), ProjectSnapshot);
        Assert.ThrowsExactly<InvalidOperationException>(() => exported.Append(technicalStart));
        Assert.ThrowsExactly<InvalidOperationException>(() => exported.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                NewEventId(), exported.Binding, SystemActor, EventTime.AddSeconds(3),
                SettingsDigest, ProjectSnapshot)));
    }

    private static QuickStartWorkflow Start(AutomatedEvaluationRun run) =>
        QuickStartWorkflow.Reduce(
        [
            QuickStartWorkflowEvent.StartedEditing(
                NewEventId(),
                new QuickStartWorkflowBinding(
                    run.ProjectId,
                    run.CompositionVersionId,
                    run.CompositionVersionDigest),
                UserActor,
                EventTime),
        ]);

    private static QuickStartWorkflow Complete(AutomatedEvaluationRun run)
    {
        QuickStartWorkflow workflow = CompleteToEvaluating(run);
        run = BindPositiveFixtureToAttempt(run, workflow);
        return workflow.Append(QuickStartWorkflowEvent.EvaluationCompleted(
            NewEventId(),
            workflow,
            run,
            SystemActor,
            EventTime.AddSeconds(2)));
    }

    private static QuickStartWorkflow CompleteToEvaluating(
        AutomatedEvaluationRun run)
    {
        QuickStartWorkflow workflow = Start(run);
        return workflow.Append(QuickStartWorkflowEvent.EvaluationStarted(
            NewEventId(),
            workflow.Binding,
            SystemActor,
            EventTime.AddSeconds(1),
            run.SettingsSnapshotDigest,
            ProjectSnapshot));
    }

    private static QuickStartWorkflowBinding Binding() =>
        new(ProjectId, VersionId, VersionDigest);

    private static QuickStartWorkflow CreateEvaluatingAttempt(bool technical)
    {
        if (!technical)
        {
            return CompleteToEvaluating(CreateRun());
        }

        QuickStartWorkflow ready = Complete(CreateRun());
        return ready.Append(QuickStartWorkflowEvent.TechnicalReevaluationStarted(
            NewEventId(), ready, SystemActor, EventTime.AddSeconds(3), ProjectSnapshot));
    }

    // Positive setup only: keep rule outcomes, IDs, settings and EvaluatedAt, but
    // produce a new run bound AFTER the actual start. Never use for stale cases.
    private static AutomatedEvaluationRun BindPositiveFixtureToAttempt(
        AutomatedEvaluationRun template,
        QuickStartWorkflow evaluating) =>
        AutomatedEvaluationRun.ParseCanonical(Mutate(
            template.CanonicalJson,
            root =>
            {
                root["inputDigest"] = evaluating.GetEvaluationInputDigest().Hex;
                string start = evaluating.Events[^1].OccurredAt.ToString(
                    "O", global::System.Globalization.CultureInfo.InvariantCulture);
                root["startedAt"] = start;
                root["completedAt"] = start;
            }));

    private static AutomatedEvaluationRun CreateRun(
        (AutomatedEvaluationRuleId RuleId, AutomatedEvaluationStatus Status)?
            replacement = null,
        bool includeArtifact = true,
        ContentDigest? settingsDigest = null,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null,
        EntityId<AutomatedEvaluationIdTag>? evaluationId = null,
        DateTimeOffset? evaluatedAt = null,
        ContentDigest? inputDigest = null,
        DateTimeOffset? startedAt = null)
    {
        EntityId<AutomatedEvaluationArtifactIdTag> selectedArtifactId =
            artifactId ?? ArtifactId;
        ContentDigest selectedArtifactDigest = artifactDigest ?? ArtifactDigest;
        AutomatedEvaluationRuleObservation[] observations =
            AutomatedEvaluationRules.Machine
                .Where(ruleId => includeArtifact ||
                    AutomatedEvaluationRules.GetDefinition(ruleId).Scope ==
                        AutomatedEvaluationRuleScope.UserInterface)
                .Select(ruleId => replacement is { } selected &&
                        selected.RuleId == ruleId
                    ? Observation(ruleId, selected.Status)
                    : Pass(ruleId))
                .ToArray();
        var request = new AutomatedEvaluationRequest(
            evaluationId ?? EntityId<AutomatedEvaluationIdTag>.New(),
            jobId: null,
            ProjectId,
            VersionId,
            VersionDigest,
            includeArtifact ? selectedArtifactId : null,
            includeArtifact ? selectedArtifactDigest : null,
            SettingsProfileId,
            new SettingsProfileVersion(1, 0, 0),
            Digest('1'),
            settingsDigest ?? SettingsDigest,
            new SettingsProfileVersion(2, 0, 0),
            "quick-start-test-environment",
            Digest('2'),
            Digest('3'),
            modelVersionsDigest: null,
            templateDigest: null,
            musicDigest: null,
            startedAt ?? EventTime.AddMinutes(-1),
            startedAt ?? EventTime.AddSeconds(-10),
            evaluatedAt ?? EventTime.AddSeconds(2),
            inputDigest ?? Digest('4'),
            includeArtifact ? selectedArtifactDigest : null,
            observations);
        return new AutomatedEvaluationEngine().Evaluate(request);
    }

    private static AutomatedEvaluationRuleObservation Observation(
        AutomatedEvaluationRuleId ruleId,
        AutomatedEvaluationStatus status) => status switch
        {
            AutomatedEvaluationStatus.Pass => Pass(ruleId),
            AutomatedEvaluationStatus.Warning =>
                AutomatedEvaluationRuleObservation.ObjectiveWarning(
                    ruleId,
                    "evaluation.objective_warning",
                    EvidenceDigest),
            AutomatedEvaluationStatus.Fail =>
                AutomatedEvaluationRuleObservation.ObjectiveFail(
                    ruleId,
                    "evaluation.rule_failed",
                    EvidenceDigest),
            AutomatedEvaluationStatus.NotEvaluated =>
                AutomatedEvaluationRuleObservation.NotEvaluated(
                    ruleId,
                    "evaluation.rule_not_evaluated"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static AutomatedEvaluationRuleObservation Pass(
        AutomatedEvaluationRuleId ruleId) =>
        AutomatedEvaluationRuleObservation.ObjectivePass(
            ruleId,
            "evaluation.rule_passed",
            EvidenceDigest);

    private static EntityId<QuickStartWorkflowEventIdTag> NewEventId() =>
        EntityId<QuickStartWorkflowEventIdTag>.New();

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private static string Mutate(
        string canonicalJson,
        Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(canonicalJson)!.AsObject();
        mutation(root);
        return CanonicalJson.Canonicalize(root.ToJsonString());
    }
}