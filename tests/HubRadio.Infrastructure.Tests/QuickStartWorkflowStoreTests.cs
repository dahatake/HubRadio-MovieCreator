using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class QuickStartWorkflowStoreTests
{
    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("11000000-0000-4000-8000-000000000001");

    private static readonly EntityId<CompositionVersionIdTag> VersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "22000000-0000-4000-8000-000000000002");

    private static readonly EntityId<CompositionVersionIdTag> ChildVersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "22000000-0000-4000-8000-000000000003");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag> ArtifactId =
        EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "33000000-0000-4000-8000-000000000003");

    private static readonly EntityId<AssetIdTag> SourceAssetId =
        EntityId<AssetIdTag>.Parse(
            "12000000-0000-4000-8000-000000000001");

    private static readonly EntityId<DefaultSettingsProfileIdTag> SettingsProfileId =
        EntityId<DefaultSettingsProfileIdTag>.Parse(
            "44000000-0000-4000-8000-000000000004");

    private static readonly ContentDigest VersionDigest = Digest('a');
    private static readonly ContentDigest ChildVersionDigest = Digest('b');
    private static readonly byte[] RenderBytes = [1, 2, 3, 4];
    private static readonly byte[] SourceBytes = [5, 6, 7];
    private static readonly ContentDigest ArtifactDigest = ContentDigest.Compute(RenderBytes);
    private static readonly ContentDigest SourceArtifactDigest = ContentDigest.Compute(SourceBytes);
    private static readonly ContentDigest SettingsProfileDigest = Digest('d');
    private static readonly ContentDigest SettingsDigest = Digest('e');
    private static readonly ContentDigest EvidenceDigest = Digest('f');
    private static readonly DateTimeOffset EventTime =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly QuickStartActor UserActor =
        QuickStartActor.User("S-1-5-21-111-222-333-1001");

    private static readonly QuickStartActor SystemActor =
        QuickStartActor.System(QuickStartSystemActor.AutomatedEvaluation);

    [TestMethod]
    public async Task AppendGetRestartAndExportRoundTripNormalizedAndCanonicalEvidence()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflowBinding binding = Binding();
        QuickStartWorkflow ready = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55000000-0000-4000-8000-000000000005"),
            "66000000-0000-4000-8000-");
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await workflowStore.AuthorizeLocalInternalDeliveryAsync(request);
        QuickStartWorkflow exported = await workflowStore.ConsumeLocalInternalDeliveryAsync(
            authorization, request, static (_, _) => Task.CompletedTask);

        // Historical replay must not reinterpret the current project as the old input.
        database.ExecuteNonQuery(
            "UPDATE projects SET payload = X'7B7D' WHERE id = $project_id;",
            ("$project_id", ProjectId.ToString()));
        QuickStartDeliveryException replay = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => workflowStore.ConsumeLocalInternalDeliveryAsync(
                authorization, request, static (_, _) => throw new AssertFailedException("Replayed callback.")));
        Assert.AreEqual(QuickStartDeliveryFailureReason.AuthorizationAlreadyConsumed, replay.Reason);
        Assert.ThrowsExactly<JsonException>(() =>
            QuickStartWorkflowEvent.ParseCanonical(
                exported.Events[^1].CanonicalJson));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            workflowStore.Append(exported.Events[^1], exported.Revision));

        QuickStartWorkflow loaded = workflowStore.Get(binding)
            ?? throw new AssertFailedException("The workflow was not loaded.");
        QuickStartWorkflow restarted =
            new QuickStartWorkflowStore(database.DatabasePath).GetForVersion(
                ProjectId,
                VersionId)
            ?? throw new AssertFailedException(
                "The workflow was not loaded after restart.");

        AssertWorkflowEqual(exported, loaded);
        AssertWorkflowEqual(exported, restarted);
        Assert.AreEqual(QuickStartWorkflowState.Exported, restarted.State);
        Assert.AreEqual(4, restarted.Revision);
        Assert.AreEqual(4L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_workflows;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
    }

    [TestMethod]
    [DataRow("empty-assets")]
    [DataRow("unconfirmed-rights")]
    [DataRow("rejected-rights")]
    [DataRow("missing-evidence")]
    [DataRow("blank-allowed-use")]
    [DataRow("external-scope")]
    [DataRow("tampered-payload")]
    [DataRow("missing-source-artifact")]
    public async Task LocalInternalDeliveryReceiptFailsClosedOnUntrustedProjectFacts(
        string mutation)
    {
        using StoreTestDatabase database = CreateDatabase();
        SeedLegacyRows(
            database,
            "legacy-approver",
            EventTime.ToString("O"));
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55000000-0000-4000-8000-000000000006"),
            "66010000-0000-4000-8000-");
        MutateProject(database, mutation);
        await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            workflowStore.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(ready)));
        Assert.AreEqual(
            QuickStartWorkflowState.Ready,
            workflowStore.Get(ready.Binding)!.State);
        Assert.AreEqual(3L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM approvals;"));
    }

    [TestMethod]
    public async Task LocalInternalDeliveryReceiptUsesStructuredApprovalWithoutInterpretingTextOrExternalFlag()
    {
        using StoreTestDatabase database = CreateDatabase(
            allowedUse: "A deliberately opaque approved use value",
            externalPublicationAllowed: true);
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55000000-0000-4000-8000-000000000011"),
            "66011000-0000-4000-8000-");

        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await workflowStore.AuthorizeLocalInternalDeliveryAsync(request);
        QuickStartWorkflow exported = await workflowStore.ConsumeLocalInternalDeliveryAsync(
            authorization, request, static (_, _) => Task.CompletedTask);

        Assert.AreEqual(QuickStartWorkflowState.Exported, exported.State);
        Assert.AreEqual(4L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
    }

    [TestMethod]
    public async Task LocalInternalDeliveryReceiptRequiresReadyWorkflowAndStoredRenderArtifact()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow editing = workflowStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66020000-0000-4000-8000-000000000001"),
                Binding(),
                UserActor,
                EventTime),
            0);
        await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            workflowStore.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(editing)));
        QuickStartWorkflow evaluating = workflowStore.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                EventId("66020000-0000-4000-8000-000000000003"),
                editing.Binding,
                SystemActor,
                EventTime.AddSeconds(1),
                SettingsDigest,
                workflowStore.GetCurrentProjectSnapshot(ProjectId)),
            editing.Revision);
        AutomatedEvaluationRun evaluation = CreateRun(
            EvaluationId("55000000-0000-4000-8000-000000000007"),
            evaluating.GetEvaluationInputDigest());
        _ = new AutomatedEvaluationStore(database.DatabasePath).Append(evaluation);
        QuickStartWorkflow ready = workflowStore.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                EventId("66020000-0000-4000-8000-000000000004"),
                evaluating,
                evaluation,
                SystemActor,
                EventTime.AddSeconds(2)),
            evaluating.Revision);
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "UPDATE artifacts SET kind = 'Proxy' WHERE id = $id;",
            ("$id", ArtifactId.ToString())));
        File.Delete(ManagedPath(database, ArtifactKind.Render));

        await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            workflowStore.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(ready)));
        Assert.AreEqual(3L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
    }

    [TestMethod]
    public void TechnicalReevaluationPreservesPriorEventsAndReplacesCurrentBinding()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        var evaluationStore = new AutomatedEvaluationStore(database.DatabasePath);
        QuickStartWorkflow ready = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55000000-0000-4000-8000-000000000008"),
            "66030000-0000-4000-8000-");
        string firstCompletion = ready.Events[^1].CanonicalJson;
        MutateProject(database, "project-revision");

        QuickStartWorkflow reevaluating = workflowStore.Append(
            QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                EventId("66030000-0000-4000-8000-000000000004"),
                ready,
                SystemActor,
                EventTime.AddSeconds(3),
                workflowStore.GetCurrentProjectSnapshot(ProjectId)),
            ready.Revision);
        Assert.AreEqual(
            QuickStartWorkflowState.AutomatedEvaluation,
            reevaluating.State);
        Assert.IsNull(reevaluating.EvaluationId);
        Assert.IsNull(reevaluating.ArtifactId);

        AutomatedEvaluationRun second = CreateRun(
            EvaluationId("55000000-0000-4000-8000-000000000009"),
            reevaluating.GetEvaluationInputDigest(),
            settingsDigest: ready.SettingsSnapshotDigest,
            startedAt: EventTime.AddSeconds(3),
            evaluatedAt: EventTime.AddSeconds(3));
        _ = evaluationStore.Append(second);
        QuickStartWorkflow secondReady = workflowStore.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                EventId("66030000-0000-4000-8000-000000000005"),
                reevaluating,
                second,
                SystemActor,
                EventTime.AddSeconds(4)),
            reevaluating.Revision);

        Assert.AreEqual(second.EvaluationId, secondReady.EvaluationId);
        Assert.AreEqual(second.CanonicalDigest, secondReady.EvaluationCanonicalDigest);
        Assert.AreEqual(5, secondReady.Revision);
        Assert.AreEqual(firstCompletion, secondReady.Events[2].CanonicalJson);
        Assert.AreEqual(2L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(5L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
    }

    [TestMethod]
    public void ReferencedArtifactlessEvaluationIsImmutableAfterFailedCompletion()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow failed = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55000000-0000-4000-8000-000000000010"),
            "66040000-0000-4000-8000-", includeArtifact: false);

        Assert.AreEqual(QuickStartWorkflowState.Failed, failed.State);
        Assert.IsNull(failed.ArtifactId);
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            """
            UPDATE automated_evaluations
            SET canonical_payload = X'01'
            WHERE evaluation_id = $evaluation_id;
            """,
            ("$evaluation_id", failed.EvaluationId!.ToString())));
    }

    [TestMethod]
    public void SnapshotRevisionCannotAdvanceWithoutExactInsertedEvent()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        _ = workflowStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66050000-0000-4000-8000-000000000001"),
                Binding(),
                UserActor,
                EventTime),
            0);

        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "UPDATE quick_start_workflows SET revision = revision + 1;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT revision FROM quick_start_workflows;"));
    }

    [TestMethod]
    public void ChangedChildVersionBeginsIndependentEditingAggregate()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow completed = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55100000-0000-4000-8000-000000000005"),
            "66100000-0000-4000-8000-");
        QuickStartWorkflow child = workflowStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66100000-0000-4000-8001-000000000001"),
                Binding(ChildVersionId, ChildVersionDigest),
                UserActor,
                EventTime.AddMinutes(1)),
            expectedRevision: 0);

        Assert.AreEqual(QuickStartWorkflowState.Ready, completed.State);
        Assert.AreEqual(QuickStartWorkflowState.Editing, child.State);
        Assert.AreEqual(1, child.Revision);
        IReadOnlyList<QuickStartWorkflow> listed = workflowStore.ListForProject(
            ProjectId);
        Assert.AreEqual(2, listed.Count);
        CollectionAssert.AreEquivalent(
            new[] { VersionId, ChildVersionId },
            listed.Select(static item => item.Binding.CompositionVersionId).ToArray());
        AssertWorkflowEqual(
            completed,
            workflowStore.Get(Binding())
                ?? throw new AssertFailedException("The original workflow disappeared."));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<QuickStartWorkflow>)listed).Clear());
    }

    [TestMethod]
    public void CompletionRequiresStoredEvaluationAndExactStartedSettingsBinding()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow workflow = workflowStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66200000-0000-4000-8000-000000000001"),
                Binding(),
                UserActor,
                EventTime),
            0);
        workflow = workflowStore.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                EventId("66200000-0000-4000-8000-000000000002"),
                workflow.Binding,
                SystemActor,
                EventTime.AddSeconds(1),
                SettingsDigest,
                workflowStore.GetCurrentProjectSnapshot(ProjectId)),
            workflow.Revision);
        AutomatedEvaluationRun evaluation = CreateRun(
            EvaluationId("55200000-0000-4000-8000-000000000005"),
            workflow.GetEvaluationInputDigest());
        QuickStartWorkflowEvent completed =
            QuickStartWorkflowEvent.EvaluationCompleted(
                EventId("66200000-0000-4000-8000-000000000003"),
                workflow,
                evaluation,
                SystemActor,
                EventTime.AddSeconds(2));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            workflowStore.Append(completed, workflow.Revision));
        Assert.AreEqual(2L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
        Assert.AreEqual(
            QuickStartWorkflowState.AutomatedEvaluation,
            workflowStore.Get(Binding())!.State);

        AutomatedEvaluationRun wrongSettings = CreateRun(
            EvaluationId("55200000-0000-4000-8000-000000000006"),
            workflow.GetEvaluationInputDigest(),
            settingsDigest: Digest('9'));
        _ = new AutomatedEvaluationStore(database.DatabasePath).Append(wrongSettings);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            workflowStore.Append(
                QuickStartWorkflowEvent.EvaluationCompleted(
                    EventId("66200000-0000-4000-8000-000000000004"),
                    workflow,
                    wrongSettings,
                    SystemActor,
                    EventTime.AddSeconds(2)),
                workflow.Revision));
        Assert.AreEqual(2L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));

        _ = new AutomatedEvaluationStore(database.DatabasePath).Append(evaluation);
        QuickStartWorkflow ready = workflowStore.Append(
            completed,
            workflow.Revision);
        Assert.AreEqual(QuickStartWorkflowState.Ready, ready.State);
    }

    [TestMethod]
    public void AppendIsIdempotentByCanonicalBytesAndRejectsConflictsAndStaleRevision()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        EntityId<QuickStartWorkflowEventIdTag> eventId =
            EventId("66300000-0000-4000-8000-000000000001");
        QuickStartWorkflowEvent started = QuickStartWorkflowEvent.StartedEditing(
            eventId,
            Binding(),
            UserActor,
            EventTime);

        QuickStartWorkflow first = workflowStore.Append(started, 0);
        QuickStartWorkflow idempotent = workflowStore.Append(started, 99);
        AssertWorkflowEqual(first, idempotent);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));

        QuickStartWorkflowEvent conflicting = QuickStartWorkflowEvent.StartedEditing(
            eventId,
            Binding(),
            UserActor,
            EventTime.AddTicks(1));
        QuickStartWorkflowEventConflictException conflict =
            Assert.ThrowsExactly<QuickStartWorkflowEventConflictException>(() =>
                workflowStore.Append(conflicting, 1));
        Assert.AreEqual(eventId, conflict.EventId);

        QuickStartWorkflowEvent change = QuickStartWorkflowEvent.ChangesRequested(
            EventId("66300000-0000-4000-8000-000000000002"),
            Binding(),
            UserActor,
            EventTime.AddSeconds(1),
            "editing.stale_revision");
        StoreConcurrencyException stale =
            Assert.ThrowsExactly<StoreConcurrencyException>(() =>
                workflowStore.Append(change, expectedRevision: 0));
        Assert.AreEqual(0, stale.ExpectedRevision);
        Assert.AreEqual(1, stale.ActualRevision);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
    }

    [TestMethod]
    public async Task ConcurrentWritersAllowExactlyOneExpectedRevisionWinner()
    {
        using StoreTestDatabase database = CreateDatabase();
        var firstStore = new QuickStartWorkflowStore(database.DatabasePath);
        var secondStore = new QuickStartWorkflowStore(database.DatabasePath);
        _ = firstStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66400000-0000-4000-8000-000000000001"),
                Binding(),
                UserActor,
                EventTime),
            0);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(() => TryAppendChange(
            firstStore,
            EventId("66400000-0000-4000-8000-000000000002"),
            EventTime.AddSeconds(1),
            release));
        Task<bool> second = Task.Run(() => TryAppendChange(
            secondStore,
            EventId("66400000-0000-4000-8000-000000000003"),
            EventTime.AddSeconds(2),
            release));
        release.Set();
        bool[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(static result => result));
        QuickStartWorkflow stored = firstStore.Get(Binding())!;
        Assert.AreEqual(2, stored.Revision);
        Assert.AreEqual(1, stored.ChangeRequestCount);
        Assert.AreEqual(2L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
    }

    [TestMethod]
    [DataRow("snapshot-digest")]
    [DataRow("event-payload")]
    [DataRow("event-column")]
    [DataRow("missing-event")]
    public void GetFailsClosedOnSnapshotOrEventCorruption(string mutation)
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        _ = workflowStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66500000-0000-4000-8000-000000000001"),
                Binding(),
                UserActor,
                EventTime),
            0);

        switch (mutation)
        {
            case "snapshot-digest":
                database.ExecuteNonQuery(
                    "DROP TRIGGER trg_quick_start_workflow_update_transition;");
                database.ExecuteNonQuery(
                    "DROP TRIGGER trg_quick_start_workflow_update_event_match;");
                database.ExecuteNonQuery(
                    "UPDATE quick_start_workflows SET canonical_digest = $digest;",
                    ("$digest", Digest('9').Hex));
                break;
            case "event-payload":
                database.ExecuteNonQuery(
                    "DROP TRIGGER trg_quick_start_event_immutable_update;");
                database.ExecuteNonQuery(
                    "UPDATE quick_start_state_events SET canonical_payload = X'7B7D';");
                break;
            case "event-column":
                database.ExecuteNonQuery(
                    "DROP TRIGGER trg_quick_start_event_immutable_update;");
                database.ExecuteNonQuery(
                    "UPDATE quick_start_state_events SET actor_id = 'different-user';");
                break;
            case "missing-event":
                database.ExecuteNonQuery(
                    "DROP TRIGGER trg_quick_start_event_immutable_delete;");
                database.ExecuteNonQuery("DELETE FROM quick_start_state_events;");
                break;
            default:
                throw new AssertFailedException("Unknown corruption mutation.");
        }

        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                workflowStore.Get(Binding()));
        Assert.AreEqual("Stored quick-start workflow data is invalid.", exception.Message);
    }

    [TestMethod]
    public void GetFailsClosedWhenStoredEvaluationTimestampDiffersFromCanonicalEvent()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow ready = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55500000-0000-4000-8000-000000000005"),
            "66510000-0000-4000-8000-");
        database.ExecuteNonQuery(
            "DROP TRIGGER trg_quick_start_event_immutable_update;");
        database.ExecuteNonQuery(
            """
            UPDATE quick_start_state_events
            SET evaluation_evaluated_at = $evaluated_at
            WHERE kind = 'EvaluationCompleted';
            """,
            ("$evaluated_at", EventTime.AddSeconds(2).ToString("O")));

        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                workflowStore.Get(ready.Binding));
        Assert.AreEqual(
            "Stored quick-start workflow data is invalid.",
            exception.Message);
    }

    [TestMethod]
    public void ForeignKeysAndTriggersProtectVersionEvaluationAndAppendOnlyEvents()
    {
        using StoreTestDatabase database = CreateDatabase();
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow completed = AppendThroughCompletion(
            database, workflowStore,
            EvaluationId("55600000-0000-4000-8000-000000000005"),
            "66600000-0000-4000-8000-");

        Assert.AreEqual(QuickStartWorkflowState.Ready, completed.State);
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "UPDATE quick_start_state_events SET actor_id = 'tampered';"));
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "DELETE FROM quick_start_state_events;"));
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "DELETE FROM automated_evaluation_rules WHERE evaluation_id = $id;",
            ("$id", completed.EvaluationId!.ToString())));
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "UPDATE automated_evaluation_rules SET reason_code = 'evaluation.changed' WHERE evaluation_id = $id AND rule_id = 'codec';",
            ("$id", completed.EvaluationId!.ToString())));
        SqliteException insertRule = Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery(
            "INSERT INTO automated_evaluation_rules SELECT * FROM automated_evaluation_rules WHERE evaluation_id = $id AND rule_id = 'codec';",
            ("$id", completed.EvaluationId!.ToString())));
        Assert.IsTrue(insertRule.Message.Contains(
            "rules for an evaluation referenced by quick-start history are immutable", StringComparison.Ordinal));
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "DELETE FROM automated_evaluations WHERE evaluation_id = $id;",
            ("$id", completed.EvaluationId!.ToString())));
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "DELETE FROM composition_versions WHERE id = $id;",
            ("$id", VersionId.ToString())));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(3L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));

        string[] publicMethods = typeof(QuickStartWorkflowStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(QuickStartWorkflowStore.Append),
                nameof(QuickStartWorkflowStore.AuthorizeLocalInternalDeliveryAsync),
                nameof(QuickStartWorkflowStore.ConsumeLocalInternalDeliveryAsync),
                nameof(QuickStartWorkflowStore.Get),
                nameof(QuickStartWorkflowStore.GetCurrentProjectSnapshot),
                nameof(QuickStartWorkflowStore.GetForVersion),
                nameof(QuickStartWorkflowStore.ListForProject),
                nameof(QuickStartWorkflowStore.ListLegacyReviewReferences),
            }.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            publicMethods);
        Assert.IsFalse(publicMethods.Any(static name =>
            name.Contains("Delete", StringComparison.Ordinal) ||
            name.Contains("Update", StringComparison.Ordinal)));
        MethodInfo consume = typeof(QuickStartWorkflowStore).GetMethod(
            nameof(QuickStartWorkflowStore.ConsumeLocalInternalDeliveryAsync))!;
        Assert.IsFalse(consume.IsGenericMethod);
        Assert.AreEqual(typeof(Task<QuickStartWorkflow>), consume.ReturnType);
        Assert.AreEqual(
            typeof(Func<QuickStartLocalInternalDeliveryContext, CancellationToken, Task>),
            consume.GetParameters()[2].ParameterType);
        Assert.AreEqual(0, typeof(QuickStartLocalInternalDeliveryAuthorization).GetConstructors().Length);
        Assert.AreEqual(0, typeof(QuickStartLocalInternalDeliveryContext).GetConstructors().Length);
        Assert.IsNull(typeof(QuickStartWorkflowEvent).GetMethod("Exported", BindingFlags.Public | BindingFlags.Static));
        AssertWorkflowEqual(completed, workflowStore.Get(completed.Binding)!);
    }

    [TestMethod]
    public void LegacyReviewHistoryIsReadOnlySeparateAndNeverAuthorizesWorkflow()
    {
        using StoreTestDatabase database = CreateDatabase();
        const string LongLegacyActor =
            "legacy-actor-that-predates-the-new-bounded-actor-contract-" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        string offsetTime = "2032-03-04T14:06:07.0000000+09:00";
        SeedLegacyRows(database, LongLegacyActor, offsetTime);
        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);

        Assert.IsNull(workflowStore.Get(Binding()));
        IReadOnlyList<LegacyReviewReference> history =
            workflowStore.ListLegacyReviewReferences(Binding());

        Assert.AreEqual(3, history.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                LegacyReviewReferenceKind.ReviewState,
                LegacyReviewReferenceKind.Comment,
                LegacyReviewReferenceKind.Approval,
            },
            history.Select(static item => item.Kind).ToArray());
        Assert.IsTrue(history.All(static item =>
            !item.CanAuthorizeQuickStartExport));
        Assert.IsTrue(history.All(item => item.ActorId == LongLegacyActor));
        Assert.IsTrue(history.All(static item =>
            item.OccurredAt.Offset == TimeSpan.FromHours(9)));
        Assert.AreEqual(
            ReviewState.QualityControl,
            history.Single(static item =>
                item.Kind == LegacyReviewReferenceKind.ReviewState).ReviewState);
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_workflows;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));

        QuickStartWorkflow editing = workflowStore.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId("66700000-0000-4000-8000-000000000001"),
                Binding(),
                UserActor,
                EventTime),
            0);
        Assert.AreEqual(QuickStartWorkflowState.Editing, editing.State);
        Assert.AreEqual(1, editing.Revision);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM review_records;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM approvals;"));
        Assert.AreEqual(3, workflowStore.ListLegacyReviewReferences(Binding()).Count);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<LegacyReviewReference>)history).Clear());
    }

    [TestMethod]
    public void LegacyActorWithEmbeddedNullFailsClosed()
    {
        using StoreTestDatabase database = CreateDatabase();
        database.ExecuteNonQuery(
            """
            INSERT INTO review_records (
                id, project_id, composition_version_id,
                composition_version_digest, state, actor_id, actor_role,
                recorded_at, reason
            ) VALUES (
                '77000000-0000-4000-8000-000000000010',
                $project_id, $version_id, $version_digest, 'QualityControl',
                CAST(X'6C6567616379006163746F72' AS TEXT),
                'Editor', $occurred_at, 'Legacy embedded-null actor'
            );
            """,
            ("$project_id", ProjectId.ToString()),
            ("$version_id", VersionId.ToString()),
            ("$version_digest", VersionDigest.Hex),
            ("$occurred_at", EventTime.ToString("O")));

        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                workflowStore.ListLegacyReviewReferences(Binding()));
        Assert.AreEqual(
            "Stored quick-start workflow data is invalid.",
            exception.Message);
    }

    [TestMethod]
    public void LegacyActorWithMalformedUtf8FailsClosed()
    {
        using StoreTestDatabase database = CreateDatabase();
        database.ExecuteNonQuery(
            """
            INSERT INTO review_records (
                id, project_id, composition_version_id,
                composition_version_digest, state, actor_id, actor_role,
                recorded_at, reason
            ) VALUES (
                '77000000-0000-4000-8000-000000000011',
                $project_id, $version_id, $version_digest, 'QualityControl',
                CAST(X'80' AS TEXT),
                'Editor', $occurred_at, 'Legacy malformed UTF-8 actor'
            );
            """,
            ("$project_id", ProjectId.ToString()),
            ("$version_id", VersionId.ToString()),
            ("$version_digest", VersionDigest.Hex),
            ("$occurred_at", EventTime.ToString("O")));

        var workflowStore = new QuickStartWorkflowStore(database.DatabasePath);
        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                workflowStore.ListLegacyReviewReferences(Binding()));
        Assert.AreEqual(
            "Stored quick-start workflow data is invalid.",
            exception.Message);
    }

    [TestMethod]
    public async Task AuthorizationAloneDoesNotDeliverOrAppendReceipt()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);

        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);

        Assert.AreEqual(request.OperationId, authorization.OperationId);
        Assert.AreEqual(request.ReceiptEventId, authorization.ReceiptEventId);
        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
        AssertNoReceipt(database);
        // Authorization owns no lingering handle. A subsequent consume must revalidate.
        using var writable = new FileStream(ManagedPath(database, ArtifactKind.Render),
            FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.IsTrue(writable.CanWrite);
    }

    [TestMethod]
    public async Task ConsumeAwaitsCallbackKeepsHandlesAliveAndExpiresContextAfterSuccess()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        QuickStartLocalInternalDeliveryContext? captured = null;
        ValidatedArtifactReadStream? render = null;
        ValidatedArtifactReadStream? source = null;
        int calls = 0;
        Task<QuickStartWorkflow> consumption = store.ConsumeLocalInternalDeliveryAsync(
            authorization, request, async (context, token) =>
            {
                Interlocked.Increment(ref calls);
                captured = context;
                render = context.RenderArtifact;
                source = context.SourceArtifacts.Single();
                Assert.AreEqual(cancellation.Token, token);
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                Assert.AreSame(render, context.RenderArtifact);
                Assert.IsTrue(source.CanRead);
                using var renderCopy = new MemoryStream();
                await render.CopyToAsync(renderCopy, token);
                CollectionAssert.AreEqual(RenderBytes, renderCopy.ToArray());
                using var sourceCopy = new MemoryStream();
                await source.CopyToAsync(sourceCopy, token);
                CollectionAssert.AreEqual(SourceBytes, sourceCopy.ToArray());
            }, cancellation.Token);
        QuickStartWorkflow receipt;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(consumption.IsCompleted);
            AssertNoReceipt(database);
            AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
            Assert.IsTrue(render!.CanRead);
        }
        finally
        {
            release.TrySetResult();
            receipt = await consumption.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.AreEqual(1, calls);
        Assert.AreEqual(QuickStartWorkflowState.Exported, receipt.State);
        Assert.AreEqual(ready.Revision + 1, receipt.Revision);
        Assert.IsFalse(render!.CanRead);
        Assert.IsFalse(source!.CanRead);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = captured!.RenderArtifact);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = captured!.SourceArtifacts);
        Assert.ThrowsExactly<ObjectDisposedException>(() => render.ReadByte());
        AssertWorkflowEqual(receipt, new QuickStartWorkflowStore(database.DatabasePath).Get(ready.Binding)!);
        Assert.AreEqual(1L, ReceiptCount(database));
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AwaitedCallbackExceptionOrCancellationDoesNotExportAndSpendsAuthorization(bool cancel)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        using var cancellation = new CancellationTokenSource();
        QuickStartLocalInternalDeliveryContext? captured = null;
        ValidatedArtifactReadStream? handle = null;
        int calls = 0;
        var failure = new IOException("Test callback failure after await.");
        Task<QuickStartWorkflow> consumption = store.ConsumeLocalInternalDeliveryAsync(
            authorization, request, async (context, token) =>
            {
                calls++;
                captured = context;
                handle = context.RenderArtifact;
                await Task.Yield();
                Assert.IsTrue(handle.CanRead);
                if (cancel)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                throw failure;
            }, cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => consumption);
        }
        else
        {
            Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<IOException>(() => consumption));
        }

        Assert.AreEqual(1, calls);
        Assert.IsFalse(handle!.CanRead);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = captured!.RenderArtifact);
        AssertNoReceipt(database);
        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
        await AssertConsumed(store, authorization, request);
        QuickStartDeliveryException reused = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.AuthorizeLocalInternalDeliveryAsync(request));
        Assert.AreEqual(QuickStartDeliveryFailureReason.DuplicateOperationId, reused.Reason);
    }

    [TestMethod]
    public async Task CancellationBeforeConsumeSpendsAuthorizationWithoutInvokingCallback()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            store.ConsumeLocalInternalDeliveryAsync(authorization, request,
                static (_, _) => throw new AssertFailedException("Canceled callback invoked."),
                cancellation.Token));
        AssertNoReceipt(database);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    [DataRow("request")]
    [DataRow("callback")]
    [DataRow("task")]
    public async Task InvalidConsumeArgumentsSpendValidAuthorization(string invalid)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        Func<Task> attempt = () => store.ConsumeLocalInternalDeliveryAsync(
            authorization, invalid == "request" ? null! : request,
            invalid == "callback" ? null! : static (_, _) => null!);
        if (invalid == "task")
        {
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(attempt);
            Assert.AreEqual("The quick-start delivery callback must return a task.", failure.Message);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(attempt);
        }

        AssertNoReceipt(database);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    public async Task OperationAndReceiptIdentitiesCannotBeReservedTwiceAndAuthorizationIsInstanceBound()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        var other = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        QuickStartDeliveryException operation = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(ready, operationId: request.OperationId)));
        Assert.AreEqual(QuickStartDeliveryFailureReason.DuplicateOperationId, operation.Reason);
        QuickStartDeliveryException receipt = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(ready, receiptEventId: request.ReceiptEventId)));
        Assert.AreEqual(QuickStartDeliveryFailureReason.DuplicateReceiptEventId, receipt.Reason);
        QuickStartDeliveryException foreign = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => other.ConsumeLocalInternalDeliveryAsync(authorization, request,
                static (_, _) => throw new AssertFailedException("Foreign authorization callback.")));
        Assert.AreEqual(QuickStartDeliveryFailureReason.AuthorizationNotFound, foreign.Reason);
        AssertNoReceipt(database);
        _ = await store.ConsumeLocalInternalDeliveryAsync(authorization, request, static (_, _) => Task.CompletedTask);
        QuickStartDeliveryException persistedReceipt = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => other.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(ready, receiptEventId: request.ReceiptEventId)));
        Assert.AreEqual(QuickStartDeliveryFailureReason.DuplicateReceiptEventId, persistedReceipt.Reason);
    }

    [TestMethod]
    [DataRow("same-values")]
    [DataRow("actor")]
    [DataRow("revision")]
    [DataRow("time")]
    [DataRow("receipt")]
    [DataRow("operation")]
    public async Task RequestSubstitutionIsOneShotAndNeverInvokesCallback(string mutation)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        var substitute = new QuickStartLocalInternalDeliveryRequest(
            mutation == "operation" ? EntityId<QuickStartDeliveryOperationIdTag>.New() : request.OperationId,
            mutation == "receipt" ? EntityId<QuickStartWorkflowEventIdTag>.New() : request.ReceiptEventId,
            request.Binding,
            mutation == "revision" ? request.ExpectedRevision + 1 : request.ExpectedRevision,
            mutation == "actor" ? "S-1-5-21-111-222-333-1002" : UserActor.Id,
            mutation == "time" ? request.ReceiptOccurredAt.AddTicks(1) : request.ReceiptOccurredAt);
        QuickStartDeliveryException mismatch = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.ConsumeLocalInternalDeliveryAsync(authorization, substitute,
                static (_, _) => throw new AssertFailedException("Substituted request callback.")));
        Assert.AreEqual(QuickStartDeliveryFailureReason.AuthorizationRequestMismatch, mismatch.Reason);
        AssertNoReceipt(database);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    [DataRow("missing", false)]
    [DataRow("digest", false)]
    [DataRow("size", false)]
    [DataRow("metadata", false)]
    [DataRow("missing", true)]
    [DataRow("digest", true)]
    [DataRow("size", true)]
    [DataRow("metadata", true)]
    public async Task PhysicalAuthorizationAndConsumeRejectMissingTamperedOrMismatchedFiles(
        string mutation, bool source)
    {
        // Each phase has a fresh database and real managed files; no DB-only positive render.
        foreach (bool afterAuthorization in new[] { false, true })
        {
            using StoreTestDatabase database = CreateDatabase();
            var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
            QuickStartWorkflow ready = CreateReady(database, store);
            QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
            QuickStartLocalInternalDeliveryAuthorization? authorization = afterAuthorization
                ? await store.AuthorizeLocalInternalDeliveryAsync(request) : null;
            ArtifactKind kind = source ? ArtifactKind.Original : ArtifactKind.Render;
            string path = ManagedPath(database, kind);
            switch (mutation)
            {
                case "missing":
                    File.Delete(path);
                    break;
                case "digest":
                    File.WriteAllBytes(path, new byte[source ? SourceBytes.Length : RenderBytes.Length]);
                    break;
                case "size":
                    File.WriteAllBytes(path, [0]);
                    break;
                case "metadata":
                    // Simulate on-disk corruption past the immutability boundary.
                    // Normal render metadata changes are separately tested as SQL rejections.
                    if (!source)
                    {
                        database.ExecuteNonQuery("DROP TRIGGER trg_quick_start_render_artifact_immutable_update;");
                    }

                    database.ExecuteNonQuery(
                        "UPDATE artifacts SET storage_reference = storage_reference || '.missing' WHERE id = $id;",
                        ("$id", source ? SourceAssetId.ToString() : ArtifactId.ToString()));
                    break;
                default:
                    throw new AssertFailedException("Unknown file mutation.");
            }

            QuickStartDeliveryException failure = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
                () => afterAuthorization
                    ? store.ConsumeLocalInternalDeliveryAsync(authorization!, request,
                        static (_, _) => throw new AssertFailedException("Invalid file callback."))
                    : (Task)store.AuthorizeLocalInternalDeliveryAsync(request));
            Assert.AreEqual(afterAuthorization ? QuickStartDeliveryFailureReason.AuthorizationStale
                : QuickStartDeliveryFailureReason.ArtifactValidationFailed, failure.Reason);
            Assert.IsFalse(failure.ReconciliationRequired);
            AssertNoReceipt(database);
            if (afterAuthorization)
            {
                await AssertConsumed(store, authorization!, request);
            }

            QuickStartDeliveryException reused = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
                () => store.AuthorizeLocalInternalDeliveryAsync(request));
            Assert.AreEqual(QuickStartDeliveryFailureReason.DuplicateOperationId, reused.Reason);
        }
    }

    [TestMethod]
    [DataRow("project-revision", QuickStartDeliveryFailureReason.AuthorizationStale)]
    [DataRow("source-digest", QuickStartDeliveryFailureReason.AuthorizationStale)]
    [DataRow("source-size", QuickStartDeliveryFailureReason.AuthorizationStale)]
    [DataRow("unconfirmed-rights", QuickStartDeliveryFailureReason.AuthorizationRevoked)]
    [DataRow("rejected-rights", QuickStartDeliveryFailureReason.AuthorizationRevoked)]
    [DataRow("external-scope", QuickStartDeliveryFailureReason.AuthorizationRevoked)]
    public async Task ProjectOrSourceRightsChangesInvalidateAuthorizedSnapshot(
        string mutation, QuickStartDeliveryFailureReason expected)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        MutateProject(database, mutation);
        // Existing history is valid even though its snapshot no longer authorizes delivery.
        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
        QuickStartDeliveryException stale = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.ConsumeLocalInternalDeliveryAsync(authorization, request,
                static (_, _) => throw new AssertFailedException("Stale project callback.")));
        Assert.AreEqual(expected, stale.Reason);
        AssertNoReceipt(database);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    public async Task WorkflowRevisionChangeInvalidatesAuthorizationEvenWhenStillReady()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        _ = store.Append(QuickStartWorkflowEvent.SelfReviewed(
            EntityId<QuickStartWorkflowEventIdTag>.New(), ready.Binding, UserActor,
            EventTime.AddSeconds(3)), ready.Revision);
        QuickStartDeliveryException stale = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.ConsumeLocalInternalDeliveryAsync(authorization, request,
                static (_, _) => throw new AssertFailedException("Stale revision callback.")));
        Assert.AreEqual(QuickStartDeliveryFailureReason.AuthorizationStale, stale.Reason);
        AssertNoReceipt(database);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    public async Task TwoStoresSimultaneouslyConsumeOneRevisionInvokeExactlyOneCallback()
    {
        using StoreTestDatabase database = CreateDatabase();
        var firstStore = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        var secondStore = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, firstStore);
        QuickStartLocalInternalDeliveryRequest firstRequest = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryRequest secondRequest = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization firstAuthorization =
            await firstStore.AuthorizeLocalInternalDeliveryAsync(firstRequest);
        QuickStartLocalInternalDeliveryAuthorization secondAuthorization =
            await secondStore.AuthorizeLocalInternalDeliveryAsync(secondRequest);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        int callbacks = 0;
        async Task<bool> Consume(QuickStartWorkflowStore store,
            QuickStartLocalInternalDeliveryAuthorization authorization,
            QuickStartLocalInternalDeliveryRequest request)
        {
            await start.Task;
            if (Interlocked.Increment(ref attempts) == 2)
            {
                attempted.TrySetResult();
            }

            try
            {
                _ = await store.ConsumeLocalInternalDeliveryAsync(authorization, request,
                    async (_, token) =>
                    {
                        Interlocked.Increment(ref callbacks);
                        entered.TrySetResult();
                        await finish.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    });
                return true;
            }
            catch (QuickStartDeliveryException exception)
                when (exception.Reason == QuickStartDeliveryFailureReason.AuthorizationStale)
            {
                Assert.IsFalse(exception.ReconciliationRequired);
                return false;
            }
        }

        Task<bool> first = Task.Run(() => Consume(firstStore, firstAuthorization, firstRequest));
        Task<bool> second = Task.Run(() => Consume(secondStore, secondAuthorization, secondRequest));
        start.SetResult();
        bool[] results;
        try
        {
            await Task.WhenAll(attempted.Task, entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            AssertNoReceipt(database);
            // A distinct connection cannot acquire the durable writer lock during the callback.
            using SqliteConnection probe = database.OpenConnection();
            probe.DefaultTimeout = 1;
            SqliteException locked = Assert.ThrowsExactly<SqliteException>(() =>
            {
                using SqliteTransaction unexpected = probe.BeginTransaction(deferred: false);
            });
            Assert.AreEqual(5, locked.SqliteErrorCode); // SQLITE_BUSY
            Assert.AreEqual(1, Volatile.Read(ref callbacks));
        }
        finally
        {
            finish.TrySetResult();
            results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.AreEqual(1, results.Count(static success => success));
        Assert.AreEqual(1, callbacks);
        Assert.AreEqual(1L, ReceiptCount(database));
        Assert.AreEqual(ready.Revision + 1, firstStore.Get(ready.Binding)!.Revision);
        await AssertConsumed(firstStore, firstAuthorization, firstRequest);
        await AssertConsumed(secondStore, secondAuthorization, secondRequest);
    }

    [TestMethod]
    public async Task ReceiptFailureAfterCallbackSuccessRequiresReconciliationWithoutReplay()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        database.ExecuteNonQuery("""
            CREATE TRIGGER trg_test_reject_receipt BEFORE INSERT ON quick_start_state_events
            WHEN NEW.kind = 'Exported'
            BEGIN SELECT RAISE(ABORT, 'test receipt failure'); END;
            """);
        string destination = Path.Combine(database.RootPath, "delivered.bin");
        int calls = 0;
        QuickStartDeliveryException failure = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(
            () => store.ConsumeLocalInternalDeliveryAsync(authorization, request, async (context, token) =>
            {
                calls++;
                await using var output = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await context.RenderArtifact.CopyToAsync(output, token);
            }));
        Assert.AreEqual(QuickStartDeliveryFailureReason.ReceiptPersistenceFailed, failure.Reason);
        Assert.IsTrue(failure.ReconciliationRequired);
        Assert.AreEqual(1, calls);
        CollectionAssert.AreEqual(RenderBytes, File.ReadAllBytes(destination));
        AssertNoReceipt(database);
        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    public async Task CancellationAfterSuccessfulCallbackRequiresReconciliationAndDoesNotExport()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        QuickStartLocalInternalDeliveryRequest request = DeliveryRequest(ready);
        QuickStartLocalInternalDeliveryAuthorization authorization =
            await store.AuthorizeLocalInternalDeliveryAsync(request);
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        QuickStartDeliveryException failure = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            store.ConsumeLocalInternalDeliveryAsync(authorization, request, async (_, _) =>
            {
                calls++;
                await Task.Yield();
                cancellation.Cancel();
                // Successful completion, but the receipt must not silently ignore cancellation.
            }, cancellation.Token));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(QuickStartDeliveryFailureReason.ReceiptPersistenceFailed, failure.Reason);
        Assert.IsTrue(failure.ReconciliationRequired);
        Assert.IsInstanceOfType<OperationCanceledException>(failure.InnerException);
        AssertNoReceipt(database);
        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
        await AssertConsumed(store, authorization, request);
    }

    [TestMethod]
    [DataRow("storage_reference", "projects/11000000-0000-4000-8000-000000000001/renders/other.mp4")]
    [DataRow("byte_size", 99L)]
    [DataRow("created_at", "2032-03-04T05:06:08.0000000+00:00")]
    [DataRow("kind", "Proxy")]
    public void ReferencedRenderMetadataCannotBeUpdated(string column, object value)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow ready = CreateReady(database, store);
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            $"UPDATE artifacts SET {column} = $value WHERE id = $id;",
            ("$value", value), ("$id", ArtifactId.ToString())));
        Assert.Throws<SqliteException>(() => database.ExecuteNonQuery(
            "DELETE FROM artifacts WHERE id = $id;", ("$id", ArtifactId.ToString())));
        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
    }

    [TestMethod]
    [DataRow("digest", false)]
    [DataRow("started-at", false)]
    [DataRow("digest", true)]
    [DataRow("started-at", true)]
    public void CraftedCanonicalCompletionMustBindActualStartOnAppendAndHistoricalReplay(
        string mutation, bool reevaluation)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartWorkflow evaluating;
        ContentDigest wrongInput;
        if (reevaluation)
        {
            QuickStartWorkflow ready = CreateReady(database, store);
            wrongInput = ready.Events[1].CanonicalDigest; // An earlier attempt is not this input.
            evaluating = store.Append(QuickStartWorkflowEvent.TechnicalReevaluationStarted(
                EntityId<QuickStartWorkflowEventIdTag>.New(), ready, SystemActor,
                EventTime.AddSeconds(3), store.GetCurrentProjectSnapshot(ProjectId)), ready.Revision);
        }
        else
        {
            evaluating = AppendEvaluationStart(store, "66800000-0000-4000-8000-");
            wrongInput = Digest('8');
        }

        DateTimeOffset start = evaluating.Events[^1].OccurredAt;
        AutomatedEvaluationRun valid = CreateRun(EntityId<AutomatedEvaluationIdTag>.New(),
            evaluating.GetEvaluationInputDigest(), startedAt: start, evaluatedAt: start.AddSeconds(1));
        AutomatedEvaluationRun forged = CreateRun(EntityId<AutomatedEvaluationIdTag>.New(),
            mutation == "digest" ? wrongInput : evaluating.GetEvaluationInputDigest(),
            startedAt: mutation == "started-at" ? start.AddTicks(-1) : start,
            evaluatedAt: start.AddSeconds(1));
        _ = new AutomatedEvaluationStore(database.DatabasePath).Append(forged);
        QuickStartWorkflowEvent template = QuickStartWorkflowEvent.EvaluationCompleted(
            EntityId<QuickStartWorkflowEventIdTag>.New(), evaluating, valid, SystemActor, start.AddSeconds(2));
        JsonNode node = JsonNode.Parse(template.CanonicalJson)!;
        node["evaluationId"] = forged.EvaluationId.ToString();
        node["evaluationCanonicalDigest"] = forged.CanonicalDigest.Hex;
        QuickStartWorkflowEvent crafted = QuickStartWorkflowEvent.ParseCanonical(
            CanonicalJson.Canonicalize(node.ToJsonString()));
        // This is valid canonical JSON and a valid state transition, not a malformed-byte test.
        QuickStartWorkflow craftedReady = evaluating.Append(crafted);
        Assert.AreEqual(QuickStartWorkflowState.Ready, craftedReady.State);
        const string ExpectedMessage =
            "The stored automated evaluation does not belong to the active evaluation start event.";
        InvalidOperationException appendFailure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.Append(crafted, evaluating.Revision));
        Assert.AreEqual(ExpectedMessage, appendFailure.Message);
        Assert.AreEqual((long)evaluating.Revision, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
        AssertWorkflowEqual(evaluating, store.Get(evaluating.Binding)!);

        // Bypass the store, preserving canonical bytes, hashes, normalized columns and FKs.
        // Read/replay must independently catch semantic input substitution.
        InsertCraftedCompletion(database, craftedReady);
        Assert.AreEqual(ExpectedMessage, Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.Get(evaluating.Binding)).Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetForVersion(ProjectId, VersionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.ListForProject(ProjectId));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Append(crafted, expectedRevision: 999));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaleProjectSnapshotCannotStartOrCompleteEvaluation(bool completing)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow current;
        QuickStartWorkflowEvent next;
        if (completing)
        {
            current = AppendEvaluationStart(store, "66810000-0000-4000-8000-");
            AutomatedEvaluationRun run = CreateRun(EntityId<AutomatedEvaluationIdTag>.New(),
                current.GetEvaluationInputDigest());
            _ = new AutomatedEvaluationStore(database.DatabasePath).Append(run);
            next = QuickStartWorkflowEvent.EvaluationCompleted(EntityId<QuickStartWorkflowEventIdTag>.New(),
                current, run, SystemActor, EventTime.AddSeconds(2));
        }
        else
        {
            current = store.Append(QuickStartWorkflowEvent.StartedEditing(
                EntityId<QuickStartWorkflowEventIdTag>.New(), Binding(), UserActor, EventTime), 0);
            next = QuickStartWorkflowEvent.EvaluationStarted(EntityId<QuickStartWorkflowEventIdTag>.New(),
                Binding(), SystemActor, EventTime.AddSeconds(1), SettingsDigest,
                store.GetCurrentProjectSnapshot(ProjectId));
        }

        MutateProject(database, "project-revision");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Append(next, current.Revision));
        AssertWorkflowEqual(current, store.Get(current.Binding)!);
        Assert.AreEqual((long)current.Revision, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM quick_start_state_events;"));
    }

    [TestMethod]
    public async Task DeliveryRequiresWorkspaceAndCannotAuthorizeArtifactlessFailedEvaluation()
    {
        using StoreTestDatabase database = CreateDatabase();
        var noWorkspace = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow failed = AppendThroughCompletion(database, noWorkspace,
            EntityId<AutomatedEvaluationIdTag>.New(), "66820000-0000-4000-8000-", includeArtifact: false);
        QuickStartDeliveryException unconfigured = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            noWorkspace.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(failed)));
        Assert.AreEqual(QuickStartDeliveryFailureReason.WorkspaceNotConfigured, unconfigured.Reason);
        var configured = new QuickStartWorkflowStore(database.DatabasePath, database.RootPath);
        QuickStartDeliveryException notReady = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            configured.AuthorizeLocalInternalDeliveryAsync(DeliveryRequest(failed)));
        Assert.AreEqual(QuickStartDeliveryFailureReason.EvidenceInvalid, notReady.Reason);
        AssertNoReceipt(database);
        AssertWorkflowEqual(failed, configured.Get(failed.Binding)!);
    }

    [TestMethod]
    [DataRow("project-revision")]
    [DataRow("unconfirmed-rights")]
    public void ChangedProjectDuringEvaluationCanRestartWithoutAcceptingLateOldResult(string mutation)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow evaluating = AppendEvaluationStart(store, "66830000-0000-4000-8000-");
        ContentDigest oldInput = evaluating.GetEvaluationInputDigest();
        AutomatedEvaluationRun oldRun = CreateRun(
            EntityId<AutomatedEvaluationIdTag>.New(), oldInput,
            evaluatedAt: EventTime.AddSeconds(5));
        var evaluations = new AutomatedEvaluationStore(database.DatabasePath);
        _ = evaluations.Append(oldRun);
        QuickStartWorkflowEvent oldCompletion = QuickStartWorkflowEvent.EvaluationCompleted(
            EntityId<QuickStartWorkflowEventIdTag>.New(), evaluating, oldRun,
            SystemActor, EventTime.AddSeconds(6));

        MutateProject(database, mutation);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Append(oldCompletion, evaluating.Revision));
        QuickStartWorkflow recovered = new QuickStartWorkflowStore(database.DatabasePath).Get(evaluating.Binding)!;
        AssertWorkflowEqual(evaluating, recovered);
        QuickStartWorkflow restarted = store.Append(QuickStartWorkflowEvent.TechnicalReevaluationStarted(
            EntityId<QuickStartWorkflowEventIdTag>.New(), recovered, SystemActor,
            EventTime.AddSeconds(3), store.GetCurrentProjectSnapshot(ProjectId)), recovered.Revision);
        Assert.AreEqual(QuickStartWorkflowState.AutomatedEvaluation, restarted.State);
        Assert.AreNotEqual(oldInput, restarted.GetEvaluationInputDigest());
        Assert.AreEqual(evaluating.Events[1].CanonicalJson, restarted.Events[1].CanonicalJson);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Append(oldCompletion, restarted.Revision));
        Assert.ThrowsExactly<InvalidOperationException>(() => QuickStartWorkflowEvent.EvaluationCompleted(
            EntityId<QuickStartWorkflowEventIdTag>.New(), restarted, oldRun,
            SystemActor, EventTime.AddSeconds(6)));

        AutomatedEvaluationRun newRun = CreateRun(
            EntityId<AutomatedEvaluationIdTag>.New(), restarted.GetEvaluationInputDigest(),
            startedAt: EventTime.AddSeconds(3), evaluatedAt: EventTime.AddSeconds(5));
        _ = evaluations.Append(newRun);
        QuickStartWorkflow ready = store.Append(QuickStartWorkflowEvent.EvaluationCompleted(
            EntityId<QuickStartWorkflowEventIdTag>.New(), restarted, newRun,
            SystemActor, EventTime.AddSeconds(6)), restarted.Revision);
        Assert.AreEqual(QuickStartWorkflowState.Ready, ready.State);
        Assert.AreEqual(newRun.EvaluationId, ready.EvaluationId);
        Assert.AreEqual(4, ready.Revision);
        AssertWorkflowEqual(ready, new QuickStartWorkflowStore(database.DatabasePath).Get(ready.Binding)!);
        Assert.IsNotNull(evaluations.Get(oldRun.EvaluationId));
        AssertNoReceipt(database);
    }

    [TestMethod]
    public void EveryColumnOfReferencedEvaluationRejectsUpdatesWithoutDisablingAnyTrigger()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new QuickStartWorkflowStore(database.DatabasePath);
        QuickStartWorkflow ready = CreateReady(database, store);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA table_info(automated_evaluations);";
        var columns = new List<string>();
        using (SqliteDataReader reader = schema.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.IsTrue(columns.Count > 20);
        CollectionAssert.Contains(columns, "input_digest");
        CollectionAssert.Contains(columns, "started_at");
        CollectionAssert.Contains(columns, "completed_at");
        foreach (string column in columns)
        {
            string quoted = string.Concat("\"", column.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
            SqliteException failure = Assert.ThrowsExactly<SqliteException>(() => database.ExecuteNonQuery(
                $"UPDATE automated_evaluations SET {quoted} = {quoted} WHERE evaluation_id = $id;",
                ("$id", ready.EvaluationId!.ToString())));
            StringAssert.Contains(failure.Message,
                "an evaluation referenced by quick-start history is immutable");
        }

        AssertWorkflowEqual(ready, store.Get(ready.Binding)!);
    }

    private static void InsertCraftedCompletion(StoreTestDatabase database, QuickStartWorkflow next)
    {
        QuickStartWorkflowEvent completed = next.Events[^1];
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quick_start_state_events (
                project_id, composition_version_id, composition_version_digest, revision,
                event_id, kind, actor_kind, actor_id, occurred_at, project_revision,
                project_canonical_digest, settings_snapshot_digest, evaluation_id,
                evaluation_evaluated_at, evaluation_aggregate_status, evaluation_canonical_digest,
                artifact_id, artifact_digest, evaluation_result_state, canonical_digest, canonical_payload
            ) VALUES (
                $project, $version, $version_digest, $revision,
                $event, 'EvaluationCompleted', 'system', $actor, $occurred, $project_revision,
                $project_digest, $settings, $evaluation,
                $evaluated, 'PASS', $evaluation_digest,
                $artifact, $artifact_digest, 'ready', $event_digest, $event_payload
            );
            UPDATE quick_start_workflows SET current_state = 'ready', revision = $revision,
                evaluation_id = $evaluation, evaluation_aggregate_status = 'PASS',
                evaluation_canonical_digest = $evaluation_digest,
                artifact_id = $artifact, artifact_digest = $artifact_digest,
                event_chain_digest = $chain_digest, last_event_id = $event,
                last_event_digest = $event_digest, canonical_digest = $snapshot_digest,
                canonical_payload = $snapshot_payload
            WHERE project_id = $project AND composition_version_id = $version
                AND composition_version_digest = $version_digest;
            """;
        command.Parameters.AddWithValue("$project", next.Binding.ProjectId.ToString());
        command.Parameters.AddWithValue("$version", next.Binding.CompositionVersionId.ToString());
        command.Parameters.AddWithValue("$version_digest", next.Binding.CompositionVersionDigest.Hex);
        command.Parameters.AddWithValue("$revision", next.Revision);
        command.Parameters.AddWithValue("$event", completed.EventId.ToString());
        command.Parameters.AddWithValue("$actor", completed.Actor.Id);
        command.Parameters.AddWithValue("$occurred", completed.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$project_revision", completed.ProjectSnapshot!.Revision);
        command.Parameters.AddWithValue("$project_digest", completed.ProjectSnapshot.CanonicalPayloadDigest.Hex);
        command.Parameters.AddWithValue("$settings", completed.SettingsSnapshotDigest!.Hex);
        command.Parameters.AddWithValue("$evaluation", completed.EvaluationId!.ToString());
        command.Parameters.AddWithValue("$evaluated", completed.EvaluationEvaluatedAt!.Value.ToString("O"));
        command.Parameters.AddWithValue("$evaluation_digest", completed.EvaluationCanonicalDigest!.Hex);
        command.Parameters.AddWithValue("$artifact", completed.ArtifactId!.ToString());
        command.Parameters.AddWithValue("$artifact_digest", completed.ArtifactDigest!.Hex);
        command.Parameters.AddWithValue("$event_digest", completed.CanonicalDigest.Hex);
        command.Parameters.AddWithValue("$event_payload", Encoding.UTF8.GetBytes(completed.CanonicalJson));
        command.Parameters.AddWithValue("$chain_digest", next.EventChainDigest.Hex);
        command.Parameters.AddWithValue("$snapshot_digest", next.CanonicalDigest.Hex);
        command.Parameters.AddWithValue("$snapshot_payload", Encoding.UTF8.GetBytes(next.CanonicalJson));
        Assert.AreEqual(2, command.ExecuteNonQuery());
        transaction.Commit();
    }

    private static QuickStartWorkflow CreateReady(StoreTestDatabase database, QuickStartWorkflowStore store) =>
        AppendThroughCompletion(database, store,
            EvaluationId("55900000-0000-4000-8000-000000000001"), "66900000-0000-4000-8000-");

    private static QuickStartLocalInternalDeliveryRequest DeliveryRequest(
        QuickStartWorkflow workflow,
        EntityId<QuickStartDeliveryOperationIdTag>? operationId = null,
        EntityId<QuickStartWorkflowEventIdTag>? receiptEventId = null) =>
        new(operationId ?? EntityId<QuickStartDeliveryOperationIdTag>.New(),
            receiptEventId ?? EntityId<QuickStartWorkflowEventIdTag>.New(),
            workflow.Binding, workflow.Revision, UserActor.Id, EventTime.AddSeconds(10));

    private static long ReceiptCount(StoreTestDatabase database) => database.ExecuteScalarInt64(
        "SELECT COUNT(*) FROM quick_start_state_events WHERE kind = 'Exported';");

    private static void AssertNoReceipt(StoreTestDatabase database) => Assert.AreEqual(0L, ReceiptCount(database));

    private static async Task AssertConsumed(QuickStartWorkflowStore store,
        QuickStartLocalInternalDeliveryAuthorization authorization,
        QuickStartLocalInternalDeliveryRequest request)
    {
        QuickStartDeliveryException replay = await Assert.ThrowsExactlyAsync<QuickStartDeliveryException>(() =>
            store.ConsumeLocalInternalDeliveryAsync(authorization, request,
                static (_, _) => throw new AssertFailedException("One-shot authorization replayed.")));
        Assert.AreEqual(QuickStartDeliveryFailureReason.AuthorizationAlreadyConsumed, replay.Reason);
    }

    private static void PromoteFixture(StoreTestDatabase database, string id,
        ArtifactKind kind, string fileName, byte[] bytes)
    {
        string staging = Path.Combine(database.RootPath, "projects", ProjectId.ToString(), "staging");
        Directory.CreateDirectory(staging);
        string path = Path.Combine(staging, fileName);
        File.WriteAllBytes(path, bytes);
        var artifacts = new ArtifactStore(database.DatabasePath, database.RootPath);
        // Synchronous fixture setup only; the production delivery callback is always awaited.
        StoredArtifact stored = artifacts.PromoteAsync(new ArtifactPromotionRequest(
            EntityId<ArtifactIdTag>.Parse(id), ProjectId, kind, path, fileName,
            ContentDigest.Compute(bytes), bytes.LongLength, EventTime)).GetAwaiter().GetResult();
        Assert.AreEqual(bytes.LongLength, stored.ByteSize);
        Assert.AreEqual(ContentDigest.Compute(bytes), stored.ContentDigest);
        Assert.IsTrue(File.Exists(Path.Combine(database.RootPath, stored.StorageReference)));
    }

    private static string ManagedPath(StoreTestDatabase database, ArtifactKind kind) =>
        Path.Combine(database.RootPath, "projects", ProjectId.ToString(),
            kind == ArtifactKind.Original ? "original" : "renders",
            kind == ArtifactKind.Original ? "source.mov" : "quick-start.mp4");

    private static void MutateProject(StoreTestDatabase database, string mutation)
    {
        if (mutation == "missing-source-artifact")
        {
            database.ExecuteNonQuery("DELETE FROM artifacts WHERE id = $id;", ("$id", SourceAssetId.ToString()));
            return;
        }

        if (mutation == "project-revision")
        {
            database.ExecuteNonQuery("UPDATE projects SET revision = revision + 1 WHERE id = $id;",
                ("$id", ProjectId.ToString()));
            return;
        }

        PublicationScope scope = mutation == "external-scope"
            ? PublicationScope.ExternalCandidate : PublicationScope.InternalOnly;
        string payload = CreateProjectPayload(mutation != "empty-assets",
            mutation == "unconfirmed-rights" ? RightsStatus.Unconfirmed
                : mutation == "rejected-rights" ? RightsStatus.Rejected : RightsStatus.Approved,
            mutation is not ("unconfirmed-rights" or "rejected-rights" or "missing-evidence"),
            mutation == "blank-allowed-use" ? " " : "Internal editing and delivery", scope, false,
            mutation == "tampered-payload" ? EntityId<ProjectIdTag>.New() : ProjectId);
        if (mutation is "source-digest" or "source-size")
        {
            JsonNode node = JsonNode.Parse(payload)!;
            if (mutation == "source-digest")
            {
                node["assets"]![0]!["contentDigest"] = Digest('9').Hex;
            }
            else
            {
                node["assets"]![0]!["byteSize"] = SourceBytes.Length + 1;
            }

            payload = CanonicalJson.Canonicalize(node.ToJsonString());
        }

        database.ExecuteNonQuery("""
            UPDATE projects SET revision = revision + 1, payload = $payload,
                publication_scope = $scope WHERE id = $id;
            """, ("$payload", Encoding.UTF8.GetBytes(payload)),
            ("$scope", scope.ToString()), ("$id", ProjectId.ToString()));
    }

    private static StoreTestDatabase CreateDatabase(
        bool includeAsset = true,
        RightsStatus rightsStatus = RightsStatus.Approved,
        bool includeEvidence = true,
        string allowedUse = "Internal editing and delivery",
        PublicationScope publicationScope = PublicationScope.InternalOnly,
        bool externalPublicationAllowed = false,
        bool includeSourceArtifact = true,
        EntityId<ProjectIdTag>? payloadProjectId = null)
    {
        var database = new StoreTestDatabase();
        try
        {
            string projectPayload = CreateProjectPayload(
                includeAsset,
                rightsStatus,
                includeEvidence,
                allowedUse,
                publicationScope,
                externalPublicationAllowed,
                payloadProjectId ?? ProjectId);
            var assets = new List<Asset>();
            if (includeAsset)
            {
                RightsStatus domainStatus =
                    rightsStatus == RightsStatus.Approved && !includeEvidence
                        ? RightsStatus.Unconfirmed
                        : rightsStatus;
                string domainAllowedUse = string.IsNullOrWhiteSpace(allowedUse)
                    ? "Valid metadata used only to create the project row"
                    : allowedUse;
                string? domainEvidence = domainStatus == RightsStatus.Approved
                    ? "rights-ticket-1"
                    : null;
                assets.Add(new Asset(
                    SourceAssetId,
                    "source.mov",
                    "video/quicktime",
                    SourceArtifactDigest,
                    SourceBytes.Length,
                    new RightsRecord(
                        domainStatus,
                        domainEvidence,
                        domainAllowedUse,
                        domainStatus == RightsStatus.Approved &&
                            externalPublicationAllowed)));
            }

            var project = new Project(
                ProjectId,
                "Quick-start receipt test",
                "Internal production",
                "Internal team",
                publicationScope,
                deadline: null,
                assets,
                Array.Empty<Performer>());
            new ProjectStore(database.DatabasePath).CreateProject(
                project,
                projectPayload,
                EventTime);
            InsertVersion(database, VersionId, VersionDigest, parentVersionId: null);
            InsertVersion(database, ChildVersionId, ChildVersionDigest, VersionId);
            database.ExecuteNonQuery(
                """
                INSERT INTO default_settings_profiles (
                    profile_id, profile_version, profile_digest, profile_payload
                ) VALUES (
                    $profile_id, '1.0.0', $profile_digest, X'01'
                );
                """,
                ("$profile_id", SettingsProfileId.ToString()),
                ("$profile_digest", SettingsProfileDigest.Hex));
            if (includeAsset && includeSourceArtifact)
            {
                PromoteFixture(database, SourceAssetId.ToString(), ArtifactKind.Original,
                    "source.mov", SourceBytes);
            }

            PromoteFixture(database, ArtifactId.ToString(), ArtifactKind.Render,
                "quick-start.mp4", RenderBytes);
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static void InsertVersion(
        StoreTestDatabase database,
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest versionDigest,
        EntityId<CompositionVersionIdTag>? parentVersionId)
    {
        database.ExecuteNonQuery(
            """
            INSERT INTO composition_versions (
                id, project_id, parent_version_id, content_digest, created_at, payload
            ) VALUES (
                $id, $project_id, $parent_id, $digest, $created_at, X'01'
            );
            """,
            ("$id", versionId.ToString()),
            ("$project_id", ProjectId.ToString()),
            ("$parent_id", parentVersionId?.ToString()),
            ("$digest", versionDigest.Hex),
            ("$created_at", EventTime.ToString("O")));
    }

    private static AutomatedEvaluationRun CreateRun(
        EntityId<AutomatedEvaluationIdTag> evaluationId,
        ContentDigest inputDigest,
        EntityId<CompositionVersionIdTag>? versionId = null,
        ContentDigest? versionDigest = null,
        ContentDigest? settingsDigest = null,
        bool includeArtifact = true,
        DateTimeOffset? evaluatedAt = null,
        DateTimeOffset? startedAt = null)
    {
        AutomatedEvaluationRuleObservation[] observations =
            AutomatedEvaluationRules.Machine
                .Where(ruleId => includeArtifact ||
                    AutomatedEvaluationRules.GetDefinition(ruleId).Scope ==
                        AutomatedEvaluationRuleScope.UserInterface)
                .Select(ruleId =>
                    AutomatedEvaluationRuleObservation.ObjectivePass(
                        ruleId,
                        "evaluation.rule_passed",
                        EvidenceDigest))
                .ToArray();
        var request = new AutomatedEvaluationRequest(
            evaluationId,
            jobId: null,
            ProjectId,
            versionId ?? VersionId,
            versionDigest ?? VersionDigest,
            includeArtifact ? ArtifactId : null,
            includeArtifact ? ArtifactDigest : null,
            SettingsProfileId,
            new SettingsProfileVersion(1, 0, 0),
            SettingsProfileDigest,
            settingsDigest ?? SettingsDigest,
            new SettingsProfileVersion(2, 0, 0),
            "quick-start-store-test-environment",
            Digest('1'),
            Digest('2'),
            modelVersionsDigest: null,
            templateDigest: null,
            musicDigest: null,
            startedAt: startedAt ?? EventTime.AddSeconds(1),
            completedAt: startedAt ?? EventTime.AddSeconds(1),
            evaluatedAt: evaluatedAt ?? EventTime.AddSeconds(1),
            inputDigest: inputDigest,
            outputDigest: includeArtifact ? ArtifactDigest : null,
            observations: observations);
        return new AutomatedEvaluationEngine().Evaluate(request);
    }

    private static QuickStartWorkflow AppendThroughCompletion(
        StoreTestDatabase database,
        QuickStartWorkflowStore store,
        EntityId<AutomatedEvaluationIdTag> evaluationId,
        string eventIdPrefix,
        bool includeArtifact = true)
    {
        QuickStartWorkflow workflow = AppendEvaluationStart(store, eventIdPrefix);
        AutomatedEvaluationRun evaluation = CreateRun(
            evaluationId, workflow.GetEvaluationInputDigest(), includeArtifact: includeArtifact);
        Assert.IsTrue(evaluation.StartedAt >= workflow.Events[^1].OccurredAt);
        Assert.AreEqual(workflow.GetEvaluationInputDigest(), evaluation.InputDigest);
        _ = new AutomatedEvaluationStore(database.DatabasePath).Append(evaluation);
        return store.Append(
            QuickStartWorkflowEvent.EvaluationCompleted(
                EventId(string.Concat(eventIdPrefix, "000000000003")),
                workflow,
                evaluation,
                SystemActor,
                EventTime.AddSeconds(2)),
            workflow.Revision);
    }

    private static QuickStartWorkflow AppendEvaluationStart(
        QuickStartWorkflowStore store,
        string eventIdPrefix)
    {
        QuickStartWorkflow workflow = store.Append(
            QuickStartWorkflowEvent.StartedEditing(
                EventId(string.Concat(eventIdPrefix, "000000000001")),
                Binding(),
                UserActor,
                EventTime),
            0);
        return store.Append(
            QuickStartWorkflowEvent.EvaluationStarted(
                EventId(string.Concat(eventIdPrefix, "000000000002")),
                workflow.Binding,
                SystemActor,
                EventTime.AddSeconds(1),
                SettingsDigest,
                store.GetCurrentProjectSnapshot(ProjectId)),
            workflow.Revision);
    }

    private static bool TryAppendChange(
        QuickStartWorkflowStore store,
        EntityId<QuickStartWorkflowEventIdTag> eventId,
        DateTimeOffset occurredAt,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            _ = store.Append(
                QuickStartWorkflowEvent.ChangesRequested(
                    eventId,
                    Binding(),
                    UserActor,
                    occurredAt,
                    "editing.concurrent_change"),
                expectedRevision: 1);
            return true;
        }
        catch (StoreConcurrencyException)
        {
            return false;
        }
    }

    private static void SeedLegacyRows(
        StoreTestDatabase database,
        string actorId,
        string occurredAt)
    {
        database.ExecuteNonQuery(
            """
            INSERT INTO review_records (
                id, project_id, composition_version_id,
                composition_version_digest, state, actor_id, actor_role,
                recorded_at, reason
            ) VALUES (
                '77000000-0000-4000-8000-000000000001',
                $project_id, $version_id, $version_digest, 'QualityControl',
                $actor_id, 'Editor', $occurred_at, 'Legacy review state'
            );
            INSERT INTO review_comments (
                id, project_id, composition_version_id,
                composition_version_digest, output_value, output_rate,
                source_span_id, actor_id, actor_role, created_at, text
            ) VALUES (
                '77000000-0000-4000-8000-000000000002',
                $project_id, $version_id, $version_digest, 0, 1,
                '88000000-0000-4000-8000-000000000001',
                $actor_id, 'Editor', $occurred_at, 'Legacy comment'
            );
            INSERT INTO approvals (
                id, project_id, composition_version_id,
                composition_version_digest, actor_id, actor_role,
                approved_at, reason
            ) VALUES (
                '77000000-0000-4000-8000-000000000003',
                $project_id, $version_id, $version_digest,
                $actor_id, 'RightsApprover', $occurred_at, 'Legacy approval'
            );
            """,
            ("$project_id", ProjectId.ToString()),
            ("$version_id", VersionId.ToString()),
            ("$version_digest", VersionDigest.Hex),
            ("$actor_id", actorId),
            ("$occurred_at", occurredAt));
    }

    private static QuickStartWorkflowBinding Binding(
        EntityId<CompositionVersionIdTag>? versionId = null,
        ContentDigest? versionDigest = null) =>
        new(ProjectId, versionId ?? VersionId, versionDigest ?? VersionDigest);

    private static string CreateProjectPayload(
        bool includeAsset,
        RightsStatus rightsStatus,
        bool includeEvidence,
        string allowedUse,
        PublicationScope publicationScope,
        bool externalPublicationAllowed,
        EntityId<ProjectIdTag> payloadProjectId)
    {
        var assets = new JsonArray();
        if (includeAsset)
        {
            var rights = new JsonObject
            {
                ["status"] = rightsStatus switch
                {
                    RightsStatus.Unconfirmed => "unconfirmed",
                    RightsStatus.Approved => "approved",
                    RightsStatus.Rejected => "rejected",
                    _ => throw new ArgumentOutOfRangeException(nameof(rightsStatus)),
                },
                ["allowedUse"] = allowedUse,
                ["externalPublicationAllowed"] = externalPublicationAllowed,
            };
            if (includeEvidence)
            {
                rights["evidenceReference"] = "rights-ticket-1";
            }

            assets.Add(new JsonObject
            {
                ["id"] = SourceAssetId.ToString(),
                ["fileName"] = "source.mov",
                ["mediaType"] = "video/quicktime",
                ["contentDigest"] = SourceArtifactDigest.Hex,
                ["byteSize"] = SourceBytes.Length,
                ["rights"] = rights,
            });
        }

        var project = new JsonObject
        {
            ["id"] = payloadProjectId.ToString(),
            ["contentName"] = "Quick-start receipt test",
            ["purpose"] = "Internal production",
            ["audience"] = "Internal team",
            ["publicationScope"] = publicationScope switch
            {
                PublicationScope.InternalOnly => "internalOnly",
                PublicationScope.SpecificGroupOnly => "specificGroupOnly",
                PublicationScope.ExternalCandidate => "externalCandidate",
                PublicationScope.ExternalPublicationApproved =>
                    "externalPublicationApproved",
                PublicationScope.PublicationForbidden => "publicationForbidden",
                PublicationScope.PendingDecision => "pendingDecision",
                _ => throw new ArgumentOutOfRangeException(nameof(publicationScope)),
            },
            ["assets"] = assets,
            ["performers"] = new JsonArray(),
        };
        return CanonicalJson.Canonicalize(project.ToJsonString());
    }

    private static void AssertWorkflowEqual(
        QuickStartWorkflow expected,
        QuickStartWorkflow actual)
    {
        Assert.AreEqual(expected.State, actual.State);
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.CanonicalJson, actual.CanonicalJson);
        Assert.AreEqual(expected.CanonicalDigest, actual.CanonicalDigest);
        Assert.AreEqual(expected.EventChainDigest, actual.EventChainDigest);
        Assert.AreEqual(expected.Events.Count, actual.Events.Count);
    }

    private static EntityId<AutomatedEvaluationIdTag> EvaluationId(string value) =>
        EntityId<AutomatedEvaluationIdTag>.Parse(value);

    private static EntityId<QuickStartWorkflowEventIdTag> EventId(string value) =>
        EntityId<QuickStartWorkflowEventIdTag>.Parse(value);

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));
}