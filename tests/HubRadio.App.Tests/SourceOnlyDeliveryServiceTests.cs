using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Projects;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Reviews;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using ProjectSnapshotJson = HubRadio.App.Policy.ProjectSnapshotJson;

namespace HubRadio.App.Tests;

/// <summary>
/// Real acceptance/store/canonical evaluation rows and real copied file bytes. Media and
/// OutputQc measurements are the existing fixture's explicit synthetic test seams, NOT
/// claims of an FFmpeg run, image quality, human review, or end-to-end media acceptance.
/// </summary>
[TestClass]
public sealed class SourceOnlyDeliveryServiceTests
{
    // FR-031 AC-079: a rename that a scanner briefly blocks is retried instead of leaving the delivery half-moved.
    [TestMethod]
    public async Task TransientRenameBlockIsRetriedButOtherFailuresAndExhaustionStillSurface()
    {
        int calls = 0;
        await SourceOnlyDeliveryService.MoveWithRetryAsync(() => { if (++calls < 3) throw new IOException("blocked"); },
            CancellationToken.None, attempts: 4, delayMilliseconds: 1);
        Assert.AreEqual(3, calls);

        calls = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SourceOnlyDeliveryService.MoveWithRetryAsync(
            () => { calls++; throw new UnauthorizedAccessException(); }, CancellationToken.None, attempts: 3, delayMilliseconds: 1));
        Assert.AreEqual(3, calls);

        calls = 0;
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => SourceOnlyDeliveryService.MoveWithRetryAsync(
            () => { calls++; throw new DirectoryNotFoundException(); }, CancellationToken.None, attempts: 3, delayMilliseconds: 1));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task PipelineDelegateCopiesEveryVideoSidecarAndImageAndRestartValidatesOneExactReceipt()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true, includeThumbnail: true);
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        StoredArtifact[] before = f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId).ToArray();
        QuickStartWorkflow workflow = f.Workflows.Get(WorkflowBinding(basis))!;
        Assert.IsEmpty(f.Workflows.ListLegacyReviewReferences(workflow.Binding));
        // Same delegate/validation contract as the coordinator, without running native media stages.
        SourceOnlyResult delivered = await SourceOnlyPipeline.DeliverResultAsync(execution, basis, Service(f).DeliverAsync,
            (value, ct) => f.Service.ValidateAsync(value, execution, ct), CancellationToken.None);
        Assert.HasCount(8, delivered.Artifacts);
        Assert.IsFalse(delivered.HasBlockingFailures);
        Assert.IsTrue(delivered.HasWarnings);
        Assert.AreEqual(basis.CanonicalJson, SourceOnlyDeliveryReceipt.WithoutDelivery(delivered).CanonicalJson);
        Assert.IsEmpty(delivered.NativeReconciliationAttempts);
        Assert.IsEmpty(delivered.ReconciliationArtifacts);
        Assert.HasCount(1, delivered.Artifacts.Select(a => a.Delivery!.Receipt).Distinct().ToArray());

        foreach (SourceOnlyResultArtifact artifact in delivered.Artifacts)
        {
            SourceOnlyResultArtifact original = basis.Artifacts.Single(a => a.ArtifactId == artifact.ArtifactId);
            Assert.IsTrue(artifact.Delivered);
            Assert.AreEqual(original.Evaluation!.CanonicalJson, artifact.Evaluation!.CanonicalJson);
            Assert.AreEqual(original.VersionId, artifact.VersionId);
            Assert.AreEqual(original.Role, artifact.Role);
            Assert.IsTrue(artifact.Evaluation.Limitations.All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
            Assert.IsTrue(artifact.Evaluation.Rules.Where(r => r.Scope == AutomatedEvaluationRuleScope.UserInterface)
                .All(r => r.Status == AutomatedEvaluationStatus.NotEvaluated));
            StoredArtifact copy = await f.Generation.Store.ValidateRegisteredAsync(artifact.Delivery!.Artifact.ArtifactId);
            Assert.AreEqual(ArtifactKind.Delivery, copy.Kind);
            Assert.AreNotEqual(artifact.ArtifactId, copy.Id);
            StringAssert.StartsWith(copy.StorageReference, $"projects/{basis.Binding.ProjectId}/deliveries/source-only/{basis.Binding.ExecutionId}/{basis.Binding.RootJobId}/");
            CollectionAssert.AreEqual(await BytesAsync(f, artifact.ArtifactId), await BytesAsync(f, copy.Id));
            Assert.AreEqual(artifact.ArtifactDigest, ContentDigest.Compute(await BytesAsync(f, copy.Id)));
        }
        foreach (StoredArtifact original in before)
            Assert.AreEqual(original.ContentDigest, (await f.Generation.Store.ValidateRegisteredAsync(original.Id)).ContentDigest);
        SourceOnlyDeliveryReceipt receipt = await ReceiptAsync(f, delivered);
        Assert.AreEqual(basis.CanonicalDigest.Hex, receipt.BaseResultDigest);
        Assert.AreEqual(execution.RequestDigest.Hex, receipt.ExecutionRequestDigest);
        Assert.AreEqual(workflow.Revision, receipt.Snapshot.WorkflowRevision);
        Assert.AreEqual(workflow.CanonicalDigest.Hex, receipt.Snapshot.WorkflowCanonicalDigest);
        CollectionAssert.AreEqual(basis.Artifacts.Select(a => a.ArtifactId.ToString()).Order(StringComparer.Ordinal).ToArray(),
            receipt.Deliveries.Select(a => a.Input.Artifact.ArtifactId).ToArray());
        foreach (SourceOnlyDeliveredItem item in receipt.Deliveries)
        {
            Assert.AreEqual(ContentDigest.Compute(Encoding.UTF8.GetBytes(SourceOnlyResultCodec.Write(item.Input))).Hex, item.IdentityDigest);
            SourceOnlyResultArtifact original = basis.Artifacts.Single(a => a.ArtifactId.ToString() == item.Input.Artifact.ArtifactId);
            Assert.AreEqual(original.Evaluation!.EvaluationId.ToString(), item.Input.EvaluationId);
            Assert.AreEqual(original.Evaluation.CanonicalDigest.Hex, item.Input.EvaluationDigest);
            Assert.AreEqual(original.GraphDigest.Hex, item.Input.GraphDigest);
            Assert.AreEqual(original.ManifestDigest!.Hex, item.Input.ManifestDigest);
        }
        StoredArtifact receiptFile = f.Generation.Store.GetArtifact(receipt.Reference.ArtifactId)!;
        Assert.AreEqual(ArtifactKind.Evidence, receiptFile.Kind);
        StringAssert.StartsWith(receiptFile.StorageReference, $"projects/{basis.Binding.ProjectId}/evidence/source-only/");
        Assert.HasCount(before.Length + delivered.Artifacts.Count + 1, f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId));
        Assert.IsFalse(Directory.Exists(Staging(f, basis)));
        Assert.AreEqual(workflow.CanonicalJson, f.Workflows.Get(workflow.Binding)!.CanonicalJson); // No self-asserted Exported.
        Assert.AreEqual(execution.StateDigest, f.Executions.Get(execution.Request.ExecutionId)!.StateDigest); // Coordinator owns completion.

        StoredSourceOnlyExecution persisted = f.PersistResult(delivered);
        SourceOnlyResultLoadResult reopened = await RestartedResults(f).LoadAsync(new SourceOnlyExecutionStore(f.Generation.DatabasePath).Get(persisted.Request.ExecutionId)!);
        Assert.IsTrue(reopened.Validation.IsValid, Failures(reopened.Validation));
        Assert.AreEqual(delivered.CanonicalJson, reopened.Result.CanonicalJson);
        string web = JsonSerializer.Serialize(reopened.Result.ToWebDto());
        Assert.IsFalse(web.Contains("storageReference", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(web.Contains("stagingPath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(web.Contains(f.Generation.Root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    // NFR-BR-005 AC-126: 公開先ラベルがあるとき ai-disclosure.txt が納品ファイル一式に入り、復元検証も通る。
    public async Task DestinationLabelAddsAiDisclosureFileToTheDeliverySetAndRecoveryStillValidates()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        var disclosure = new HubRadio.App.Audit.AiGenerationDisclosureService(new ReviewStore(f.Generation.DatabasePath));
        DateTimeOffset at = new(2033, 4, 5, 6, 7, 8, TimeSpan.Zero);
        ContentDigest D(char c) => new(new string(c, 64));
        var run = new ModelRun(EntityId<ModelRunIdTag>.New(), "local-provider", "local-model", "1.2.3", D('a'),
            ModelHashAvailability.Available, D('b'), D('c'), D('d'), ModelRunOutcome.Succeeded, null,
            ModelExecutionLocation.Local, null, null, null, null, at.AddMinutes(-2), at.AddMinutes(-1));
        _ = disclosure.RecordAiElement(EntityId<AuditEventIdTag>.New(), basis.Binding.ProjectId,
            HubRadio.App.Audit.AiGeneratedElementKind.AutoSubtitle, "subtitle-track", run, "S-1-5-21-1000",
            HubRadio.Core.Reviews.ReviewActorRole.Editor, at);
        _ = disclosure.AddDestinationLabel(EntityId<AuditEventIdTag>.New(), basis.Binding.ProjectId, "local-internal",
            "AI 生成の字幕を含みます", "S-1-5-21-1000", HubRadio.Core.Reviews.ReviewActorRole.Editor, at.AddMinutes(1));

        SourceOnlyResult delivered = await Service(f).DeliverAsync(execution, basis);
        string directory = Path.GetDirectoryName(DeliveryPaths(f, delivered)[0])!;
        string text = File.ReadAllText(Path.Combine(directory, "ai-disclosure.txt"));
        StringAssert.Contains(text, "AI 生成の字幕を含みます");
        StringAssert.Contains(text, "AutoSubtitle subtitle-track local-model 1.2.3");
        Assert.HasCount(basis.Artifacts.Count + 1, Directory.GetFileSystemEntries(directory));
        Assert.IsTrue((await f.Service.ValidateAsync(delivered, execution)).IsValid);
    }

    [TestMethod]
    public async Task CommittedReceiptRecoversLostResponseAndRepeatedCallsDoNotCopyOrRewriteAnything()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        SourceOnlyResult delivered = await Service(f).DeliverAsync(execution, basis);
        string[] paths = DeliveryPaths(f, delivered);
        DateTime[] timestamps = paths.Select(File.GetLastWriteTimeUtc).ToArray();
        int count = f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId).Count;
        var store = new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root);
        var restarted = new SourceOnlyDeliveryService(f.Generation.DatabasePath, f.Generation.Root, store, RestartedResults(f),
            new QuickStartWorkflowStore(f.Generation.DatabasePath), (_, _, _) => throw new AssertFailedException("Replay must not copy."));
        // Leave the evaluated base in the execution row, modeling exit after COMMIT but before returning/persisting Delivery.
        Assert.AreEqual(basis.CanonicalJson, f.Executions.Get(execution.Request.ExecutionId)!.ResultJson);
        SourceOnlyResult recovered = await restarted.DeliverAsync(execution, basis);
        Assert.AreEqual(delivered.CanonicalJson, recovered.CanonicalJson);
        StoredSourceOnlyExecution persisted = f.PersistResult(recovered);
        SourceOnlyResult repeat = await restarted.DeliverAsync(persisted, recovered);
        Assert.AreEqual(delivered.CanonicalJson, repeat.CanonicalJson);
        CollectionAssert.AreEqual(timestamps, paths.Select(File.GetLastWriteTimeUtc).ToArray());
        Assert.HasCount(count, store.ListProjectArtifacts(basis.Binding.ProjectId));
        Assert.AreEqual(persisted.StateDigest, f.Executions.Get(persisted.Request.ExecutionId)!.StateDigest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CoordinatorMayBindTheBaseThroughItsImmutableEvaluationReceiptInsteadOfMutableResultJson(bool savedCheckpoint)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        await f.SaveGeneratorCheckpointAsync();
        SourceOnlyResult basis = await f.EvaluateAsync();
        StoredSourceOnlyExecution execution = f.Executions.UpdateState(f.Execution.Request.ExecutionId, f.Execution.Revision,
            SourceOnlyExecutionPhase.Delivering, versionId: basis.VersionId, digest: basis.VersionDigest);
        Assert.IsNull(execution.ResultJson);
        if (savedCheckpoint)
        {
            EntityId<ArtifactIdTag> id = SourceOnlyPipeline.StableId<ArtifactIdTag>(basis.Binding.ExecutionId.ToString(), "evaluation-receipt");
            byte[] bytes = Encoding.UTF8.GetBytes(basis.CanonicalJson);
            string path = Path.Combine(f.Generation.Root, "projects", basis.Binding.ProjectId.ToString(), "staging", "evaluation-receipt-test.json");
            await File.WriteAllBytesAsync(path, bytes);
            _ = await f.Generation.Store.PromoteAsync(new ArtifactPromotionRequest(id, basis.Binding.ProjectId, ArtifactKind.Analysis,
                path, $"source-only/{id}.evaluation-receipt.json", basis.CanonicalDigest, bytes.Length, SourceOnlyEvaluationFixture.Now));
            var service = new SourceOnlyDeliveryService(f.Generation.DatabasePath, f.Generation.Root, f.Generation.Store, f.Service, f.Workflows);
            SourceOnlyResult delivered = await service.DeliverAsync(execution, basis);
            Assert.IsTrue(delivered.Artifacts.All(a => a.Delivered));
            Assert.IsTrue((await f.Service.ValidateAsync(delivered, execution)).IsValid);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis));
            AssertNoRegisteredDelivery(f, basis);
            Assert.IsFalse(Directory.Exists(Staging(f, basis)));
        }
    }

    [TestMethod]
    public async Task TwoStoreInstancesSerializeOneRealCopyAndReturnTheSameDurableReceipt()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int copies = 0;
        Task<SourceOnlyResult> first = Task.Run(() => Service(f, async (input, output, ct) =>
        {
            Interlocked.Increment(ref copies);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            await input.CopyToAsync(output, ct);
        }).DeliverAsync(execution, basis));
        Task<SourceOnlyResult>? second = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            second = Task.Run(() => Service(f, (_, _, _) => throw new AssertFailedException("Only the first invocation may copy."))
                .DeliverAsync(execution, basis));
        }
        finally { release.TrySetResult(); }
        SourceOnlyResult[] results = await Task.WhenAll(first, second!).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(basis.Artifacts.Count, copies);
        Assert.AreEqual(results[0].CanonicalJson, results[1].CanonicalJson);
        Assert.HasCount(basis.Artifacts.Count, f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId).Where(a => a.Kind == ArtifactKind.Delivery).ToArray());
    }

    [TestMethod]
    public async Task UnknownPreexistingDestinationIsNeverOverwrittenOrRemoved()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        Directory.CreateDirectory(FinalDirectory(f, basis));
        string sentinel = Path.Combine(FinalDirectory(f, basis), "not-owned.txt");
        await File.WriteAllTextAsync(sentinel, "keep these original bytes");
        int copies = 0;
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            Service(f, (_, _, _) => { copies++; throw new AssertFailedException("An unknown directory must block delivery."); }).DeliverAsync(execution, basis));
        Assert.AreEqual(0, copies);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.AreEqual("keep these original bytes", await File.ReadAllTextAsync(sentinel));
        Assert.HasCount(1, Directory.GetFiles(FinalDirectory(f, basis)));
        AssertNoRegisteredDelivery(f, basis);
    }

    [TestMethod]
    [DataRow("render", false)]
    [DataRow("render", true)]
    [DataRow("source", false)]
    [DataRow("source", true)]
    [DataRow("Graph", false)]
    [DataRow("Graph", true)]
    [DataRow("Manifest", false)]
    [DataRow("Manifest", true)]
    [DataRow("RenderingInputs", false)]
    [DataRow("RenderingInputs", true)]
    [DataRow("QualityControl", false)]
    [DataRow("QualityControl", true)]
    [DataRow("checkpoint", false)]
    [DataRow("checkpoint", true)]
    public async Task EveryMissingOrTamperedInputBlocksBeforeAnyCopy(string target, bool missing)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        SourceOnlyResultArtifact other = basis.Artifacts.Single(a => a.Role == SourceOnlyResultRole.VerticalBurnIn);
        EntityId<ArtifactIdTag> id = target switch
        {
            "render" => other.ArtifactId,
            "source" => other.Source.ArtifactId,
            "checkpoint" => SourceOnlyPipeline.StableId<ArtifactIdTag>(basis.Binding.ExecutionId.ToString(), "generation-evidence"),
            _ => other.Evidence.Single(e => e.Kind.ToString() == target).Artifact.ArtifactId,
        };
        await DamageFileAsync(f.Generation.Store.GetValidatedRegisteredArtifactPath(id), missing);
        int copies = 0;
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            Service(f, (_, _, _) => { copies++; throw new AssertFailedException("Invalid input must not reach copying."); }).DeliverAsync(execution, basis));
        Assert.AreEqual(0, copies);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, failure.Phase);
        Assert.AreEqual(basis.CanonicalJson, failure.Result!.CanonicalJson);
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsFalse(Directory.Exists(Staging(f, basis)));
    }

    [TestMethod]
    public async Task IndependentNonprimaryHardFailureBlocksTheEntireSetDespiteReadyPrimaryAndAllowedWarnings()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult basis = await f.EvaluateAsync((r, _) => Task.FromResult(f.Qc(r,
            loudness: r.OutputArtifactId == f.Generated.Artifacts.Single(a => a.BindingEvidence.Role == "landscape-burn-in").Artifact.Id ? -30 : -16)));
        StoredSourceOnlyExecution execution = f.PersistResult(basis);
        Assert.AreEqual(QuickStartWorkflowState.ReadyWithWarnings, f.Workflows.Get(WorkflowBinding(basis))!.State);
        Assert.AreEqual(AutomatedEvaluationStatus.Fail, basis.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeBurnIn)
            .Evaluation!.Rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.Loudness).Status);
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis));
        Assert.AreEqual("source_only.delivery_blocked", failure.Code);
        Assert.IsTrue(failure.Result!.Artifacts.All(a => !a.Delivered));
        AssertNoRegisteredDelivery(f, basis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrCorruptIndependentStoredEvaluationCannotBorrowThePrimaryRun(bool missing)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        EntityId<AutomatedEvaluationIdTag> id = basis.Artifacts.Single(a => a.Role == SourceOnlyResultRole.LandscapeBurnIn).Evaluation!.EvaluationId;
        using (SqliteConnection connection = SqliteStoreSupport.OpenConnection(f.Generation.DatabasePath))
        {
            RemoveTestTriggers(connection, "automated_evaluation_rules");
            if (missing) RemoveTestTriggers(connection, "automated_evaluations");
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = missing
                ? "DELETE FROM automated_evaluation_rules WHERE evaluation_id = $id; DELETE FROM automated_evaluations WHERE evaluation_id = $id;"
                : "UPDATE automated_evaluation_rules SET reason_code = 'test.corrupted' WHERE evaluation_id = $id AND rule_id = 'fullDecode';";
            command.Parameters.AddWithValue("$id", id.ToString());
            Assert.IsTrue(command.ExecuteNonQuery() > 0);
        }
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis));
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsFalse(Directory.Exists(Staging(f, basis)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LatestSelfReviewOrChangeNoteDoesNotFreezeSavedEvaluationRevision(bool changeNote)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        QuickStartWorkflow old = f.Workflows.Get(WorkflowBinding(basis))!;
        QuickStartWorkflowEvent note = changeNote
            ? QuickStartWorkflowEvent.ChangesRequested(EntityId<QuickStartWorkflowEventIdTag>.New(), old.Binding,
                QuickStartActor.User(execution.Request.ActorSid), SourceOnlyEvaluationFixture.Now.AddMinutes(1), "test.editorial_note")
            : QuickStartWorkflowEvent.SelfReviewed(EntityId<QuickStartWorkflowEventIdTag>.New(), old.Binding,
                QuickStartActor.User(execution.Request.ActorSid), SourceOnlyEvaluationFixture.Now.AddMinutes(1));
        QuickStartWorkflow current = f.Workflows.Append(note, old.Revision);
        Assert.AreEqual(old.Revision, basis.PrimaryWorkflow!.Revision);
        SourceOnlyResult delivered = await Service(f).DeliverAsync(execution, basis);
        SourceOnlyDeliveryReceipt receipt = await ReceiptAsync(f, delivered);
        Assert.AreEqual(current.Revision, receipt.Snapshot.WorkflowRevision);
        Assert.AreEqual(current.CanonicalDigest.Hex, receipt.Snapshot.WorkflowCanonicalDigest);
        Assert.AreEqual(old.Revision, delivered.PrimaryWorkflow!.Revision); // Historical result is not rewritten.
        _ = f.Workflows.Append(QuickStartWorkflowEvent.SelfReviewed(EntityId<QuickStartWorkflowEventIdTag>.New(), current.Binding,
            QuickStartActor.User(execution.Request.ActorSid), SourceOnlyEvaluationFixture.Now.AddMinutes(3)), current.Revision);
        Assert.IsTrue((await RestartedResults(f).ValidateAsync(delivered, execution)).IsValid);
    }

    [TestMethod]
    public async Task LaterTechnicalAttemptBlocksEvenThoughHistoricalResultStillValidates()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        QuickStartWorkflow old = f.Workflows.Get(WorkflowBinding(basis))!;
        _ = f.Workflows.Append(QuickStartWorkflowEvent.TechnicalReevaluationStarted(EntityId<QuickStartWorkflowEventIdTag>.New(), old,
            QuickStartActor.System(QuickStartSystemActor.AutomatedEvaluation), SourceOnlyEvaluationFixture.Now.AddMinutes(1),
            f.Workflows.GetCurrentProjectSnapshot(basis.Binding.ProjectId)), old.Revision);
        Assert.IsTrue((await f.Service.ValidateAsync(basis, execution)).IsValid);
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis));
        AssertNoRegisteredDelivery(f, basis);
    }

    [TestMethod]
    [DataRow("rights")]
    [DataRow("scope")]
    [DataRow("revision")]
    public async Task CurrentRightsScopeOrSnapshotDriftCannotUseSavedReadyResult(string change)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        Project original = f.Generation.Project;
        Asset asset = f.Generation.Asset;
        Asset changedAsset = change == "rights" ? new(asset.Id, asset.FileName, asset.MediaType, asset.ContentDigest, asset.ByteSize,
            new RightsRecord(RightsStatus.Rejected, "test:revoked", "Internal use revoked", false)) : asset;
        var project = new Project(original.Id, original.ContentName, original.Purpose, original.Audience,
            change == "scope" ? PublicationScope.PublicationForbidden : original.PublicationScope,
            original.Deadline, [changedAsset], original.Performers);
        StoredProject stored = f.Projects.GetProject(project.Id)!;
        _ = f.Projects.UpdateProject(project, ProjectSnapshotJson.WriteProject(project), stored.Revision,
            SourceOnlyEvaluationFixture.Now.AddMinutes(1));
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis));
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsFalse(Directory.Exists(Staging(f, basis)));
    }

    [TestMethod]
    public async Task PruningEvenPersistedResultCannotHideAnotherEvaluatedArtifactWithoutGeneratorCheckpoint()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        SourceOnlyResult subset = WithArtifacts(evaluated, evaluated.Artifacts.Where(a => a.Role != SourceOnlyResultRole.WebVtt).ToArray());
        StoredSourceOnlyExecution execution = f.PersistResult(subset); // Opaque store accepts it; delivery must not.
        Assert.IsTrue((await f.Service.ValidateAsync(subset, execution)).IsValid);
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, subset));
        AssertNoRegisteredDelivery(f, evaluated);
        Assert.IsFalse(Directory.Exists(Staging(f, evaluated)));
    }

    [TestMethod]
    public async Task CanonicalSetFromAnotherExecutionAndReorderedBaseAreRejectedWithoutSideEffects()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        using SourceOnlyEvaluationFixture other = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        (SourceOnlyResult foreign, StoredSourceOnlyExecution otherExecution) = await PrepareAsync(other);
        SourceOnlyResult delivered = await Service(f).DeliverAsync(execution, basis);
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(other).DeliverAsync(otherExecution, delivered));
        SourceOnlyResult reordered = WithArtifacts(foreign, foreign.Artifacts.Reverse().ToArray());
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(other).DeliverAsync(otherExecution, reordered));
        AssertNoRegisteredDelivery(other, foreign);
    }

    [TestMethod]
    [DataRow("copy", false)]
    [DataRow("copy", true)]
    [DataRow("receipt", false)]
    [DataRow("receipt", true)]
    public async Task MissingOrChangedDeliveredBytesAndReceiptNeverPassRestartOrCauseOverwrite(string target, bool missing)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        SourceOnlyResult delivered = await Service(f).DeliverAsync(execution, basis);
        EntityId<ArtifactIdTag> id = target == "copy" ? delivered.Artifacts[^1].Delivery!.Artifact.ArtifactId : delivered.Artifacts[0].Delivery!.Receipt.Artifact.ArtifactId;
        string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(id);
        await DamageFileAsync(path, missing);
        SourceOnlyResultValidation validation = await RestartedResults(f).ValidateAsync(delivered, execution);
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Failures.Any(failure => failure.Code == SourceOnlyResultFailureCode.DeliveryNotValidated));
        int count = f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId).Count;
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            Service(f, (_, _, _) => throw new AssertFailedException("Damaged delivery must not be overwritten.")).DeliverAsync(execution, basis));
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.IsTrue(failure.Result!.Artifacts.All(a => !a.Delivered));
        Assert.HasCount(count, f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId));
        Assert.AreEqual(!missing, File.Exists(path));
    }

    [TestMethod]
    [DataRow("wrong-set")]
    [DataRow("order")]
    [DataRow("evaluation")]
    [DataRow("base")]
    [DataRow("rights")]
    [DataRow("binding")]
    [DataRow("role")]
    [DataRow("identity")]
    [DataRow("delivery")]
    public async Task RehashedReceiptAndRepairedArtifactMetadataStillCannotForgeExactSetBinding(string attack)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        SourceOnlyResult delivered = await Service(f).DeliverAsync(execution, basis);
        SourceOnlyDeliveryReceipt original = await ReceiptAsync(f, delivered);
        JsonObject document = JsonNode.Parse(original.CanonicalJson)!.AsObject();
        JsonArray items = document["deliveries"]!.AsArray();
        switch (attack)
        {
            case "wrong-set": items.RemoveAt(items.Count - 1); break;
            case "order":
                JsonNode first = items[0]!.DeepClone();
                items[0] = items[1]!.DeepClone(); items[1] = first;
                break;
            case "evaluation": items[0]!["input"]!["evaluationDigest"] = new string('0', 64); break;
            case "base": document["baseResultDigest"] = new string('0', 64); break;
            case "rights": document["snapshot"]!["rightsSnapshotDigest"] = new string('0', 64); break;
            case "binding": document["binding"]!["rootJobId"] = Guid.NewGuid().ToString("D"); break;
            case "role": items[0]!["input"]!["role"] = "not-the-original-role"; break;
            case "identity": items[0]!["identityDigest"] = new string('0', 64); break;
            case "delivery": items[0]!["delivery"]!["artifactId"] = EntityId<ArtifactIdTag>.New().ToString(); break;
        }
        if (attack is "evaluation" or "role")
            items[0]!["identityDigest"] = ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(items[0]!["input"]!.ToJsonString()))).Hex;
        document.Remove("setDigest");
        document["setDigest"] = ContentDigest.Compute(Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(document.ToJsonString()))).Hex;
        string canonical = CanonicalJson.Canonicalize(document.ToJsonString());
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        var changed = new SourceOnlyResultArtifactReference(original.Reference.ArtifactId, ContentDigest.Compute(bytes), bytes.Length, ArtifactKind.Evidence);
        await File.WriteAllBytesAsync(f.Generation.Store.GetValidatedRegisteredArtifactPath(changed.ArtifactId), bytes);
        using (SqliteConnection connection = SqliteStoreSupport.OpenConnection(f.Generation.DatabasePath))
        {
            RemoveTestTriggers(connection, "artifacts");
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE artifacts SET content_digest = $digest, byte_size = $size WHERE id = $id;";
            command.Parameters.AddWithValue("$digest", changed.ContentDigest.Hex);
            command.Parameters.AddWithValue("$size", changed.ByteSize);
            command.Parameters.AddWithValue("$id", changed.ArtifactId.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }
        SourceOnlyResult forged = WithArtifacts(delivered, delivered.Artifacts.Select(a => SourceOnlyDeliveryReceipt.CopyArtifact(a,
            new(a.Delivery!.Artifact, new(SourceOnlyResultEvidenceKind.DeliveryReceipt, changed)))).ToArray());
        await using (ValidatedArtifactReadStream stream = await f.Generation.Store.OpenValidatedReadAsync(changed.ArtifactId))
            Assert.AreEqual(canonical, (await SourceOnlyDeliveryReceipt.ReadAsync(stream, CancellationToken.None)).CanonicalJson);
        // The file hash, metadata, canonical JSON and set hash now all agree. Binding still MUST fail.
        Assert.IsFalse((await RestartedResults(f).ValidateAsync(forged, execution)).IsValid);
    }

    [TestMethod]
    public async Task ActualInputHandlesAndImmediateDatabaseLockRemainHeldThroughLastCopy()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        var expected = basis.Artifacts.SelectMany(a => a.Evidence.Select(e => e.Artifact).Append(a.Artifact).Append(a.Source))
            .DistinctBy(a => a.ArtifactId).ToArray();
        int copies = 0;
        SourceOnlyResult delivered = await Service(f, async (input, output, ct) =>
        {
            copies++;
            Assert.IsInstanceOfType<ValidatedArtifactReadStream>(input);
            Assert.IsFalse(input.CanWrite);
            foreach (SourceOnlyResultArtifactReference reference in expected)
            {
                string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(reference.ArtifactId);
                Assert.ThrowsExactly<IOException>(() =>
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                });
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = f.Generation.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString()))
            {
                connection.Open();
                SqliteException locked = Assert.ThrowsExactly<SqliteException>(() =>
                {
                    using SqliteTransaction competing = connection.BeginTransaction(deferred: false);
                });
                Assert.AreEqual(5, locked.SqliteErrorCode); // SQLITE_BUSY: another writer cannot alter rights/evals mid-copy.
            }
            await input.CopyToAsync(output, ct);
        }).DeliverAsync(execution, basis);
        Assert.AreEqual(basis.Artifacts.Count, copies);
        Assert.IsTrue(delivered.Artifacts.All(a => a.Delivered));
    }

    [TestMethod]
    public async Task PartialCopyFailurePreservesEveryAttemptAndDoesNotImplicitlyRetryOrMarkCompleted()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        int calls = 0;
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.DeliverResultAsync(execution, basis, Service(f, async (input, output, ct) =>
            {
                if (++calls == 2)
                {
                    await output.WriteAsync(new byte[] { 1 }, ct);
                    throw new IOException(@"private C:\sensitive\output.mp4");
                }
                await input.CopyToAsync(output, ct);
            }).DeliverAsync, (value, ct) => f.Service.ValidateAsync(value, execution, ct), CancellationToken.None));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        SourceOnlyResult partial = failure.Result!;
        Assert.IsTrue(partial.ReconciliationRequired);
        Assert.IsTrue(partial.HasBlockingFailures);
        Assert.IsTrue(partial.Artifacts.All(a => !a.Delivered));
        Assert.HasCount(basis.Artifacts.Count + 1, partial.NativeReconciliationAttempts);
        Assert.IsTrue(partial.NativeReconciliationAttempts.Any(a => a.EvidenceKind == SourceOnlyResultEvidenceKind.DeliveryReceipt));
        Assert.IsTrue(partial.NativeReconciliationAttempts.Any(a => File.Exists(a.StagingPath)));
        Assert.AreEqual(execution.StateDigest, f.Executions.Get(execution.Request.ExecutionId)!.StateDigest);
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsFalse(Directory.Exists(FinalDirectory(f, basis)));
        partial.ValidateBinding(execution); // The coordinator can persist this exact additive failure result.
        StoredSourceOnlyExecution persisted = f.PersistResult(partial);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, persisted.Phase);
        SourceOnlyResult restored = SourceOnlyResult.ParseCanonical(persisted.ResultJson!, persisted.ResultDigest);
        CollectionAssert.AreEqual(partial.NativeReconciliationAttempts.ToArray(), restored.NativeReconciliationAttempts.ToArray());
        SourceOnlyPipeline.PipelineFailure repeated = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(persisted, restored));
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, repeated.Phase);
        Assert.AreEqual(partial.CanonicalJson, repeated.Result!.CanonicalJson);
        Assert.IsFalse(partial.CanonicalJson.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
        string web = JsonSerializer.Serialize(partial.ToWebDto());
        Assert.IsFalse(web.Contains("stagingPath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(web.Contains("storageReference", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationDuringCopyOrAfterSetRenameIsReconciliationNotFullSetDelivery(bool afterRename)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        using var cancellation = new CancellationTokenSource();
        SourceOnlyDeliveryService service = Service(f, async (input, output, ct) =>
        {
            if (!afterRename)
            {
                await output.WriteAsync(new byte[] { 7 }, ct);
                cancellation.Cancel(); ct.ThrowIfCancellationRequested();
            }
            await input.CopyToAsync(output, ct);
        }, afterRename ? () => cancellation.Cancel() : null);
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => service.DeliverAsync(execution, basis, cancellation.Token));
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.IsTrue(failure.Result!.Artifacts.All(a => !a.Delivered));
        Assert.HasCount(basis.Artifacts.Count + 1, failure.Result.NativeReconciliationAttempts);
        Assert.AreEqual(afterRename, Directory.Exists(FinalDirectory(f, basis)));
        AssertNoRegisteredDelivery(f, basis);
        int calls = 0;
        SourceOnlyPipeline.PipelineFailure repeated = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            Service(f, (_, _, _) => { calls++; throw new AssertFailedException("Partial delivery must not implicitly retry."); }).DeliverAsync(execution, basis));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, repeated.Phase);
        Assert.IsTrue(repeated.Result!.NativeReconciliationAttempts.Any(a => a.EvidenceKind == SourceOnlyResultEvidenceKind.DeliveryReceipt));
    }

    [TestMethod]
    public async Task CancellationBeforeDeliveryLeavesNoOwnedDirectoryOrRegistration()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis, cancellation.Token));
        Assert.AreEqual(SourceOnlyExecutionPhase.Canceled, failure.Phase);
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsFalse(Directory.Exists(Staging(f, basis)));
        Assert.IsFalse(Directory.Exists(FinalDirectory(f, basis)));
    }

    [TestMethod]
    [DataRow("second-copy-row")]
    [DataRow("receipt-row")]
    [DataRow("extra-file")]
    public async Task BatchFailureRollsBackAllRowsButRetainsFinalFilesForExplicitReconciliation(string fault)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        if (fault != "extra-file")
        {
            using SqliteConnection connection = SqliteStoreSupport.OpenConnection(f.Generation.DatabasePath);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = fault == "second-copy-row" ? """
                CREATE TRIGGER reject_delivery_test BEFORE INSERT ON artifacts
                WHEN NEW.kind = 'Delivery' AND (SELECT COUNT(*) FROM artifacts WHERE kind = 'Delivery') > 0
                BEGIN SELECT RAISE(ABORT, 'test copy registration failure'); END;
                """ : """
                CREATE TRIGGER reject_delivery_receipt_test BEFORE INSERT ON artifacts
                WHEN NEW.kind = 'Evidence' AND NEW.storage_reference LIKE '%.delivery-receipt.json'
                BEGIN SELECT RAISE(ABORT, 'test receipt registration failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            Service(f, afterFilesFinalized: fault == "extra-file" ? () => File.WriteAllText(Path.Combine(FinalDirectory(f, basis), "unknown.txt"), "do not delete") : null)
                .DeliverAsync(execution, basis));
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsTrue(failure.Result!.Artifacts.All(a => !a.Delivered));
        foreach (SourceOnlyResultPromotionAttempt attempt in failure.Result.NativeReconciliationAttempts)
        {
            string final = Path.Combine(f.Generation.Root, attempt.StorageReference.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(final));
            Assert.AreEqual(attempt.Artifact.ContentDigest, ContentDigest.Compute(await File.ReadAllBytesAsync(final)));
        }
        if (fault == "extra-file") Assert.AreEqual("do not delete", await File.ReadAllTextAsync(Path.Combine(FinalDirectory(f, basis), "unknown.txt")));
        // Even all final files + a complete JSON cannot pass when the DB transaction did not commit.
        Assert.IsFalse((await f.Service.ValidateAsync(failure.Result, execution)).IsValid);
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => Service(f).DeliverAsync(execution, basis));
        AssertNoRegisteredDelivery(f, basis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FinalSetRehashAcceptsUnchangedBytesAndRejectsPostRenameByteChange(bool changeOutput)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true, includeThumbnail: true);
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        // Damage the last output in registration order: earlier valid files cannot authorize a partial set.
        SourceOnlyResultArtifact target = basis.Artifacts.OrderBy(a => a.ArtifactId.ToString(), StringComparer.Ordinal).Last();
        string finalPath = Path.Combine(FinalDirectory(f, basis), SourceOnlyDeliveryReceipt.FileName(basis.Binding, target));
        byte[] original = await BytesAsync(f, target.ArtifactId);
        Assert.IsTrue(original.Length > 0);
        bool finalized = false;
        SourceOnlyDeliveryService service = Service(f, afterFilesFinalized: () =>
        {
            Assert.IsTrue(Directory.Exists(FinalDirectory(f, basis)));
            Assert.IsFalse(Directory.Exists(Path.Combine(Staging(f, basis), "files")));
            AssertNoRegisteredDelivery(f, basis);
            byte[] bytes = File.ReadAllBytes(finalPath);
            CollectionAssert.AreEqual(original, bytes);
            if (changeOutput)
            {
                bytes[0] ^= 1; // Same length; only the final hash verification can detect this change.
                File.WriteAllBytes(finalPath, bytes);
            }
            finalized = true;
        });
        if (!changeOutput)
        {
            SourceOnlyResult delivered = await service.DeliverAsync(execution, basis);
            Assert.IsTrue(finalized);
            Assert.IsTrue(delivered.Artifacts.All(a => a.Delivered));
            Assert.IsTrue((await f.Service.ValidateAsync(delivered, execution)).IsValid);
            foreach (SourceOnlyResultArtifact artifact in delivered.Artifacts)
                CollectionAssert.AreEqual(await BytesAsync(f, artifact.ArtifactId), await BytesAsync(f, artifact.Delivery!.Artifact.ArtifactId));
            _ = await ReceiptAsync(f, delivered);
            return;
        }

        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => service.DeliverAsync(execution, basis));
        Assert.IsTrue(finalized, "The injected byte change must finish AFTER the real directory rename.");
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.AreEqual("source_only.delivery_reconciliation_required", failure.Code);
        SourceOnlyResult partial = failure.Result!;
        Assert.IsTrue(partial.ReconciliationRequired);
        Assert.IsTrue(partial.HasBlockingFailures);
        Assert.IsTrue(partial.Artifacts.All(a => !a.Delivered));
        Assert.HasCount(basis.Artifacts.Count + 1, partial.NativeReconciliationAttempts);
        Assert.IsTrue(partial.NativeReconciliationAttempts.Any(a => a.EvidenceKind == SourceOnlyResultEvidenceKind.DeliveryReceipt));
        foreach (SourceOnlyResultPromotionAttempt attempt in partial.NativeReconciliationAttempts)
            Assert.IsTrue(File.Exists(Path.Combine(f.Generation.Root, attempt.StorageReference.Replace('/', Path.DirectorySeparatorChar))));
        byte[] retained = await File.ReadAllBytesAsync(finalPath);
        Assert.AreEqual(original.Length, retained.Length);
        Assert.AreNotEqual(target.ArtifactDigest, ContentDigest.Compute(retained));
        CollectionAssert.AreEqual(original, await BytesAsync(f, target.ArtifactId));
        AssertNoRegisteredDelivery(f, basis);
        Assert.AreEqual(execution.StateDigest, f.Executions.Get(execution.Request.ExecutionId)!.StateDigest);
        Assert.IsFalse((await f.Service.ValidateAsync(partial, execution)).IsValid);
        StoredSourceOnlyExecution persisted = f.PersistResult(partial);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, persisted.Phase);
        SourceOnlyResult restored = SourceOnlyResult.ParseCanonical(persisted.ResultJson!, persisted.ResultDigest);
        Assert.AreEqual(partial.CanonicalJson, restored.CanonicalJson);
        Assert.IsTrue(restored.Artifacts.All(a => !a.Delivered));
    }

    [TestMethod]
    public async Task WrongCopiedBytesCannotBeRegisteredEvenWhenTheCopyDelegateReturnsSuccessfully()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            Service(f, async (input, output, ct) =>
            {
                using var buffer = new MemoryStream(); await input.CopyToAsync(buffer, ct);
                byte[] bytes = buffer.ToArray(); bytes[0] ^= 1;
                await output.WriteAsync(bytes, ct);
            }).DeliverAsync(execution, basis));
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        AssertNoRegisteredDelivery(f, basis);
        Assert.IsFalse(Directory.Exists(FinalDirectory(f, basis)));
    }

    [TestMethod]
    [DataRow("kind")]
    [DataRow("relative-path")]
    [DataRow("staging")]
    public async Task InternalRegistrationRejectsArbitraryKindsPathsAndStaging(string mutation)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        (SourceOnlyResult basis, _) = await PrepareAsync(f);
        EntityId<ArtifactIdTag> id = EntityId<ArtifactIdTag>.New();
        string staging = Staging(f, basis);
        var copy = new ArtifactPromotionRequest(id, basis.Binding.ProjectId, mutation == "kind" ? ArtifactKind.Original : ArtifactKind.Delivery,
            mutation == "staging" ? Path.Combine(f.Generation.Root, "arbitrary.mp4") : Path.Combine(staging, "files", id + ".mp4"),
            mutation == "relative-path" ? "arbitrary.mp4" : SourceOnlyDeliveryReceipt.DeliveryRelativeRoot(basis.Binding) + "/" + id + ".mp4",
            basis.Artifacts[0].ArtifactDigest, basis.Artifacts[0].ByteSize, SourceOnlyEvaluationFixture.Now);
        var receipt = new ArtifactPromotionRequest(EntityId<ArtifactIdTag>.New(), basis.Binding.ProjectId, ArtifactKind.Evidence,
            Path.Combine(staging, "receipt.json"), SourceOnlyDeliveryReceipt.ReceiptRelativePath(basis.Binding),
            StandardGenerationFixture.Hash("untrusted"), 1, SourceOnlyEvaluationFixture.Now);
        using SqliteConnection connection = SqliteStoreSupport.OpenConnection(f.Generation.DatabasePath);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ArtifactStoreException failure = await Assert.ThrowsExactlyAsync<ArtifactStoreException>(() => f.Generation.Store.RegisterLocalDeliverySetAsync(
            connection, transaction, basis.Binding.ExecutionId, basis.Binding.RootJobId, [copy, receipt]));
        Assert.AreEqual(ArtifactStoreFailureReason.UnsafePath, failure.Reason);
        transaction.Rollback();
        AssertNoRegisteredDelivery(f, basis);
    }

    [TestMethod]
    public async Task DifferentDatabaseOrWorkspaceCannotProvideAFalseProtectingLock()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        using SourceOnlyEvaluationFixture other = await SourceOnlyEvaluationFixture.CreateAsync();
        Assert.ThrowsExactly<ArtifactStoreException>(() => new SourceOnlyDeliveryService(other.Generation.DatabasePath, f.Generation.Root,
            f.Generation.Store, f.Service, f.Workflows));
        Assert.ThrowsExactly<ArtifactStoreException>(() => new SourceOnlyDeliveryService(f.Generation.DatabasePath, other.Generation.Root,
            f.Generation.Store, f.Service, f.Workflows));
        (SourceOnlyResult basis, StoredSourceOnlyExecution execution) = await PrepareAsync(f);
        await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => new SourceOnlyDeliveryService(f.Generation.DatabasePath,
            f.Generation.Root, f.Generation.Store, other.Service, other.Workflows).DeliverAsync(execution, basis));
        AssertNoRegisteredDelivery(f, basis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReferencedNativeStartAttestationIsActuallyReadAndCannotBeSelfAsserted(bool forged)
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        EntityId<ArtifactIdTag> id = SourceOnlyPipeline.StableId<ArtifactIdTag>(f.Execution.Request.DraftId.ToString(), "rights-attestation");
        Asset original = f.Generation.Asset;
        var asset = new Asset(original.Id, original.FileName, original.MediaType, original.ContentDigest, original.ByteSize,
            new RightsRecord(RightsStatus.Approved, "artifact://" + id, "Explicitly attested internal editing permission only.", false));
        Project p = f.Generation.Project;
        var project = new Project(p.Id, p.ContentName, p.Purpose, p.Audience, p.PublicationScope, p.Deadline, [asset], p.Performers);
        string at = f.Execution.Request.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        var attestation = new SourceOnlyDeliveryAttestation(SourceOnlySourceLocator.RightsStatementVersion, asset.Id.ToString(),
            asset.Id.ToString(), asset.FileName, asset.ContentDigest.Hex, asset.ByteSize, f.Execution.Request.ActorSid, at,
            f.Execution.Request.ActorSid, at, !forged, false);
        byte[] bytes = Encoding.UTF8.GetBytes(SourceOnlyResultCodec.Write(attestation));
        string staging = Path.Combine(f.Generation.Root, "projects", project.Id.ToString(), "staging", "attestation-test.json");
        await File.WriteAllBytesAsync(staging, bytes);
        _ = await f.Generation.Store.PromoteAsync(new ArtifactPromotionRequest(id, project.Id, ArtifactKind.Analysis, staging,
            $"source-only/{id}.rights-attestation.json", ContentDigest.Compute(bytes), bytes.Length, f.Execution.Request.CreatedAt));
        await using var held = new SourceOnlyDeliveryHandles();
        if (forged)
            await Assert.ThrowsExactlyAsync<JsonException>(() => SourceOnlyDeliveryReceipt.ReadRightsAsync(f.Generation.Store, f.Executions,
                f.Execution, project, held, CancellationToken.None));
        else
        {
            IReadOnlyList<SourceOnlyResultArtifactReference> verified = await SourceOnlyDeliveryReceipt.ReadRightsAsync(f.Generation.Store, f.Executions,
                f.Execution, project, held, CancellationToken.None);
            Assert.AreEqual(id, verified.Single().ArtifactId);
            Assert.AreEqual(ContentDigest.Compute(bytes), verified.Single().ContentDigest);
            Assert.IsTrue(held.Files.Single().CanRead);
        }
        // This is the attestation reader's real-file contract, not a replacement of the fixture's immutable composition snapshot.
    }

    private static async Task<(SourceOnlyResult, StoredSourceOnlyExecution)> PrepareAsync(SourceOnlyEvaluationFixture f)
    {
        await f.SaveGeneratorCheckpointAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        Assert.IsFalse(result.HasBlockingFailures);
        return (result, f.PersistResult(result));
    }
    private static SourceOnlyDeliveryService Service(SourceOnlyEvaluationFixture f,
        Func<Stream, Stream, CancellationToken, Task>? copy = null, Action? afterFilesFinalized = null) => new(
        f.Generation.DatabasePath, f.Generation.Root, f.Generation.Store, f.Service, f.Workflows,
        copy ?? (static (input, output, ct) => input.CopyToAsync(output, ct)), afterFilesFinalized,
        () => SourceOnlyEvaluationFixture.Now.AddMinutes(2));
    private static SourceOnlyResultService RestartedResults(SourceOnlyEvaluationFixture f) => new(
        new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root), new AutomatedEvaluationStore(f.Generation.DatabasePath),
        new QuickStartWorkflowStore(f.Generation.DatabasePath), new ProjectStore(f.Generation.DatabasePath));
    private static SourceOnlyResult WithArtifacts(SourceOnlyResult basis, IReadOnlyList<SourceOnlyResultArtifact> artifacts) => new(
        basis.Binding, basis.GenerationDisposition, artifacts, basis.Omissions, basis.Failures, basis.ReconciliationArtifacts,
        basis.TranscriptReferences, basis.PrimaryWorkflow, basis.StagingCleanupRequired, basis.GenerationWarnings, basis.NativeReconciliationAttempts);
    private static QuickStartWorkflowBinding WorkflowBinding(SourceOnlyResult result) => new(result.Binding.ProjectId, result.VersionId, result.VersionDigest);
    private static Task<byte[]> BytesAsync(SourceOnlyEvaluationFixture f, EntityId<ArtifactIdTag> id) =>
        File.ReadAllBytesAsync(f.Generation.Store.GetValidatedRegisteredArtifactPath(id));
    private static string Staging(SourceOnlyEvaluationFixture f, SourceOnlyResult basis) => Path.Combine(f.Generation.Root, "projects",
        basis.Binding.ProjectId.ToString(), "staging", SourceOnlyDeliveryReceipt.StagingName(basis.Binding));
    private static string FinalDirectory(SourceOnlyEvaluationFixture f, SourceOnlyResult basis) => Path.Combine(f.Generation.Root, "projects",
        basis.Binding.ProjectId.ToString(), "deliveries", SourceOnlyDeliveryReceipt.DeliveryRelativeRoot(basis.Binding).Replace('/', Path.DirectorySeparatorChar));
    private static string[] DeliveryPaths(SourceOnlyEvaluationFixture f, SourceOnlyResult delivered) => delivered.Artifacts
        .Select(a => a.Delivery!.Artifact.ArtifactId).Append(delivered.Artifacts[0].Delivery!.Receipt.Artifact.ArtifactId)
        .Select(f.Generation.Store.GetValidatedRegisteredArtifactPath).ToArray();
    private static async Task<SourceOnlyDeliveryReceipt> ReceiptAsync(SourceOnlyEvaluationFixture f, SourceOnlyResult result)
    {
        await using ValidatedArtifactReadStream stream = await f.Generation.Store.OpenValidatedReadAsync(result.Artifacts[0].Delivery!.Receipt.Artifact.ArtifactId);
        return await SourceOnlyDeliveryReceipt.ReadAsync(stream, CancellationToken.None);
    }
    private static string Failures(SourceOnlyResultValidation validation) => string.Join(",", validation.Failures.Select(f => f.Code));
    private static void AssertNoRegisteredDelivery(SourceOnlyEvaluationFixture f, SourceOnlyResult basis)
    {
        Assert.IsFalse(f.Generation.Store.ListProjectArtifacts(basis.Binding.ProjectId).Any(a => a.Kind == ArtifactKind.Delivery));
        Assert.IsNull(f.Generation.Store.GetArtifact(SourceOnlyDeliveryReceipt.ReceiptId(basis.Binding)));
    }
    private static async Task DamageFileAsync(string path, bool missing)
    {
        if (missing) File.Delete(path);
        else
        {
            byte[] bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 1;
            await File.WriteAllBytesAsync(path, bytes);
        }
    }
    private static void RemoveTestTriggers(SqliteConnection connection, string table)
    {
        // Deliberate corruption of ONLY this disposable fixture database, not a production recovery operation.
        var names = new List<string>();
        using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = $table;";
            query.Parameters.AddWithValue("$table", table);
            using SqliteDataReader reader = query.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        foreach (string name in names)
        {
            using SqliteCommand drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            drop.ExecuteNonQuery();
        }
    }
}