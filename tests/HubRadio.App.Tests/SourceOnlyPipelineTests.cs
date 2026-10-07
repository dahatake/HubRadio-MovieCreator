using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Jobs;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Provenance;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using ProjectSnapshotJson = HubRadio.App.Policy.ProjectSnapshotJson;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyPipelineTests
{
    private const string ActorSid = "S-1-5-21-111-222-333-1001";
    private static readonly DateTimeOffset At = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SharedJobsRequireAPairRetainExactInstancesAndPreserveStandaloneConstruction()
    {
        using var files = new SourceFixture();
        string database = Path.Combine(files.Root, "jobs.db");
        _ = DatabaseMigrator.Migrate(database, Path.Combine(files.Root, "backups"));
        var jobs = new JobStore(database);
        var coordinator = new JobCoordinator(jobs, new LocalJobRunner(jobs));
        var candidates = new AssetCandidateRegistry();
        Assert.ThrowsExactly<ArgumentException>(() => new SourceOnlyPipeline(database, files.Root, candidates,
            (_, _) => Task.CompletedTask, jobStore: jobs));
        Assert.ThrowsExactly<ArgumentException>(() => new SourceOnlyPipeline(database, files.Root, candidates,
            (_, _) => Task.CompletedTask, jobCoordinator: coordinator));
        await using (var pipeline = new SourceOnlyPipeline(database, files.Root, candidates,
            (_, _) => Task.CompletedTask, jobStore: jobs, jobCoordinator: coordinator))
        {
            Assert.AreSame(jobs, pipeline.Jobs);
            Assert.AreSame(coordinator, pipeline.Coordinator);
        }
        await using (var standalone = new SourceOnlyPipeline(database, files.Root, candidates, (_, _) => Task.CompletedTask))
        {
            Assert.AreNotSame(jobs, standalone.Jobs);
            Assert.AreNotSame(coordinator, standalone.Coordinator);
        }
        // Both public overloads must keep the host's named OPTIONAL parameters LAST.
        var constructors = typeof(SourceOnlyPipeline).GetConstructors();
        Assert.HasCount(2, constructors);
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            Assert.AreEqual("jobStore", parameters[^2].Name);
            Assert.AreEqual(typeof(JobStore), parameters[^2].ParameterType);
            Assert.AreEqual("jobCoordinator", parameters[^1].Name);
            Assert.AreEqual(typeof(JobCoordinator), parameters[^1].ParameterType);
            Assert.IsTrue(parameters[^2].IsOptional && parameters[^1].IsOptional);
            Assert.IsNull(parameters[^2].DefaultValue);
            Assert.IsNull(parameters[^1].DefaultValue);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task NativeRegenerationRejectsReconciliationPhaseResultOrStagingCleanupWithoutAcceptingWork(
        bool reconciliationPhase, bool reconciliationResult, bool cleanup)
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync();
        SourceOnlyResult? result = reconciliationPhase ? null : f.FailureResult(reconciliationResult, cleanup);
        StoredSourceOnlyExecution failed = f.Fail(bindVersion: result is not null, result,
            reconciliationPhase ? SourceOnlyExecutionPhase.ReconciliationRequired : SourceOnlyExecutionPhase.Failed);
        ContentDigest before = failed.StateDigest;
        SourceOnlyPipeline.PipelineFailure failure = Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() => f.Pipeline.Regenerate(
            f.Project.Id, f.Preparation.Child.Id, f.Preparation.Child.ContentDigest, f.Inputs.Settings, 0, ActorSid, At.AddMinutes(1)));
        Assert.AreEqual("source_only.manual_resolution_required", failure.Code);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.HasCount(1, f.Executions.ListForProject(f.Project.Id));
        Assert.AreEqual(before, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(0, f.WorkCalls);
    }

    [TestMethod]
    public async Task StagingCleanupIsRejectedBeforeDeliveryOrQcValidation()
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync();
        SourceOnlyResult result = f.FailureResult(reconciliation: false, cleanup: true);
        int calls = 0;
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.DeliverResultAsync(f.Execution, result, (_, value, _) => { calls++; return Task.FromResult(value); },
                (_, _) => throw new AssertFailedException("Manual resolution must precede delivery validation."), CancellationToken.None));
        Assert.AreEqual("source_only.manual_resolution_required", failure.Code);
        Assert.AreEqual(0, calls);
        // An ordinary pre-result failure is not itself a reconciliation flag.
        SourceOnlyPipeline.EnsureManuallyResolved(f.Fail(bindVersion: false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedStageCheckpointRestoresActualSubtitlesWithoutAiOrStateWrites(bool bindFailedEvaluation)
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(includeShort: true);
        SourceOnlyGeneratedRuntimeEvidence produced = f.BuildGenerated();
        await f.Pipeline.CheckpointGeneratedRuntimeAsync(f.Execution, produced, CancellationToken.None);
        StoredSourceOnlyExecution failed = f.Fail(bindFailedEvaluation,
            bindFailedEvaluation ? f.FailureResult(false, false) : null);
        var order = new List<string>();
        SourceOnlyGeneratedRuntimeEvidence? restored = null;
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks
        {
            TranscriptReady = (_, _, _) => { order.Add("transcript"); return Task.CompletedTask; },
            AnalysisReady = (_, _, _, _) => { order.Add("analysis"); return Task.CompletedTask; },
            CompositionReady = (_, _, _) => { order.Add("composition"); return Task.CompletedTask; },
            GeneratedReady = (value, _) => { order.Add("generated"); restored = value; return Task.CompletedTask; },
        });
        WorkingCopyState before = f.Versions.GetCurrentState(f.Project.Id)!;
        var jobBefore = f.Pipeline.Jobs.GetJobAggregate(failed.Request.RootJobId)!;
        // This seam starts AFTER production's ingest/ASR validation. It uses actual
        // canonical planner/subtitle/checkpoint code, not fake completed media evidence.
        await f.Pipeline.RestoreRuntimeStagesAsync(failed, f.Project, f.Inputs.Import, CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            f.Pipeline.RestoreRuntimeStagesAsync(failed, f.Project, f.Inputs.Import, CancellationToken.None)));
        CollectionAssert.AreEqual(new[] { "transcript", "analysis", "composition", "generated" }, order.ToArray());
        Assert.IsNotNull(restored);
        Assert.AreEqual(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(failed, produced),
            SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(failed, restored));
        Assert.AreEqual(TranscriptChangeSource.Machine, restored.Import.Revision.ChangeSource);
        Assert.AreEqual(failed.StateDigest, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, f.Executions.Get(failed.Request.ExecutionId)!.Phase);
        Assert.AreEqual(before.Revision, f.Versions.GetCurrentState(f.Project.Id)!.Revision);
        Assert.AreEqual(CompositionSnapshotJson.WriteWorkingCopy(before.WorkingCopy),
            CompositionSnapshotJson.WriteWorkingCopy(f.Versions.GetCurrentState(f.Project.Id)!.WorkingCopy));
        Assert.AreEqual(jobBefore.Revision, f.Pipeline.Jobs.GetJobAggregate(failed.Request.RootJobId)!.Revision);
        Assert.AreEqual(0, f.WorkCalls);
        Assert.AreEqual(RightsStatus.Unconfirmed, f.Project.Assets.Single().Rights.Status);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task PartialRestoreRegistersOnlyStagesWhoseCheckpointExists(int availableStage)
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(
            saveAnalysis: availableStage >= 2, saveComposition: availableStage >= 3);
        StoredSourceOnlyExecution failed = f.Fail(bindVersion: false);
        var order = new List<string>();
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks
        {
            TranscriptReady = (_, _, _) => { order.Add("transcript"); return Task.CompletedTask; },
            AnalysisReady = (_, _, _, _) => { order.Add("analysis"); return Task.CompletedTask; },
            CompositionReady = (_, _, _) => { order.Add("composition"); return Task.CompletedTask; },
            GeneratedReady = (_, _) => throw new AssertFailedException("Missing generated subtitles must not be invented."),
        });
        await f.Pipeline.RestoreRuntimeStagesAsync(failed, f.Project, f.Inputs.Import, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "transcript", "analysis", "composition" }.Take(availableStage).ToArray(), order.ToArray());
        Assert.AreEqual(failed.StateDigest, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(0, f.WorkCalls);
    }

    [TestMethod]
    [DataRow("composition-checkpoint")]
    [DataRow("analysis-evidence")]
    [DataRow("generated-runtime")]
    public async Task ExistingInvalidCheckpointFailsClosedBeforeRuntimeRegistration(string corruptStage)
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(
            saveAnalysis: corruptStage != "analysis-evidence", saveComposition: corruptStage != "composition-checkpoint");
        string json = "{\"schema\":\"invalid-but-hash-valid-checkpoint\"}";
        if (corruptStage == "generated-runtime")
        {
            JsonNode document = JsonNode.Parse(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(f.Execution, f.BuildGenerated()))!;
            document["productionVersionId"] = EntityId<CompositionVersionIdTag>.New().ToString();
            json = CanonicalJson.Canonicalize(document.ToJsonString());
        }
        await f.SaveEvidenceAsync(corruptStage, json);
        StoredSourceOnlyExecution failed = f.Fail(bindVersion: false);
        int registrations = 0;
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks
        {
            TranscriptReady = (_, _, _) => { registrations++; return Task.CompletedTask; },
        });
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            f.Pipeline.RestoreRuntimeStagesAsync(failed, f.Project, f.Inputs.Import, CancellationToken.None));
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.AreEqual(0, registrations);
        Assert.AreEqual(failed.StateDigest, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(0, f.WorkCalls);
    }

    // FR-051 AC-026.4: a run that carries subtitle edits keeps its parent's rule (edits are bound to the parent's exact
    // cues); a fresh run uses v3; an unknown parent rule fails closed.
    [TestMethod]
    public void NewRunKeepsTheParentRuleOnlyWhenCarryingSubtitleEdits()
    {
        Assert.AreEqual(StandardSubtitleBuildPolicy.FullCoverage, SourceOnlyPipeline.SelectBuilderPolicy(false, null));
        Assert.AreEqual(StandardSubtitleBuildPolicy.FullCoverage, SourceOnlyPipeline.SelectBuilderPolicy(false, StandardSubtitleBuilder.RuleVersion));
        Assert.AreEqual(StandardSubtitleBuildPolicy.ExactSourceTokens, SourceOnlyPipeline.SelectBuilderPolicy(true, StandardSubtitleBuilder.RuleVersion));
        Assert.AreEqual(StandardSubtitleBuildPolicy.FullCoverage, SourceOnlyPipeline.SelectBuilderPolicy(true, StandardSubtitleBuilder.FullCoverageRuleVersion));
        foreach (string? unknown in new[] { null, "", "standard-subtitle-builder.unknown.v9" })
        {
            Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(
                () => SourceOnlyPipeline.SelectBuilderPolicy(true, unknown)).Phase);
        }
    }

    // FR-051 AC-026.4: a result saved before FR-051 (rule v2) is replayed with v2, so it stays readable byte-for-byte.
    [TestMethod]
    [DataRow(StandardSubtitleBuildPolicy.ExactSourceTokens)]
    [DataRow(StandardSubtitleBuildPolicy.FullCoverage)]
    public async Task GeneratedEvidenceReplaysWithTheRuleItWasSavedWith(StandardSubtitleBuildPolicy policy)
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(false);
        SourceOnlyGeneratedRuntimeEvidence saved = f.BuildGenerated(policy);
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks());
        await f.Pipeline.CheckpointGeneratedRuntimeAsync(f.Execution, saved, CancellationToken.None);

        SourceOnlyGeneratedRuntimeEvidence? persisted = await f.Pipeline.ReadGeneratedRuntimeEvidenceAsync(
            f.Execution, f.Preparation, f.Inputs.Import, CancellationToken.None);

        Assert.IsNotNull(persisted);
        Assert.AreEqual(StandardSubtitleBuilder.GetRuleVersion(policy), persisted.BuilderRule);
        Assert.AreEqual(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(f.Execution, saved),
            SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(f.Execution, persisted));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GeneratedCallbackIsAwaitedAfterPersistenceWithEveryActualProfileCueAndHistory(bool includeShort)
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(includeShort);
        var entered = Signal();
        var release = Signal();
        int calls = 0;
        SourceOnlyGeneratedRuntimeEvidence produced = f.BuildGenerated();
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks
        {
            GeneratedReady = async (value, token) =>
            {
                calls++;
                Assert.AreSame(produced, value);
                SourceOnlyGeneratedRuntimeEvidence? persisted = await f.Pipeline.ReadGeneratedRuntimeEvidenceAsync(
                    f.Execution, f.Preparation, f.Inputs.Import, token);
                Assert.IsNotNull(persisted);
                Assert.AreEqual(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(f.Execution, value),
                    SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(f.Execution, persisted));
                // A Read from this very callback must validate, not deadlock or invoke itself again.
                await f.Pipeline.RestoreRuntimeStagesAsync(f.Execution, f.Project, f.Inputs.Import, token);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            },
        });
        Task checkpoint = f.Pipeline.CheckpointGeneratedRuntimeAsync(f.Execution, produced, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(checkpoint.IsCompleted);
        release.TrySetResult(true);
        await checkpoint;
        await f.Pipeline.RestoreRuntimeStagesAsync(f.Execution, f.Project, f.Inputs.Import, CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.HasCount(includeShort ? 1 : 0, produced.ShortForms);
        Assert.HasCount(1 + produced.ShortForms.Count, produced.Versions);
        foreach (SourceOnlyGeneratedSubtitleRuntimeEvidence item in produced.Versions.Values)
        {
            Assert.AreEqual(item.Version.Id, item.Timeline.CompositionVersionId);
            Assert.AreEqual(item.Profile.LayoutSpecId, item.Layout.Id);
            Assert.AreEqual(item.Profile.Width, item.Build.Subtitles.AssProfile.PlayResolutionWidth);
            Assert.AreEqual(item.Profile.Height, item.Build.Subtitles.AssProfile.PlayResolutionHeight);
            Assert.AreEqual(item.Version.Id == produced.Preparation.Child.Id ? 1920 : 1080, item.Profile.Width);
            Assert.IsNotEmpty(item.Histories);
            CollectionAssert.AreEqual(item.Build.Subtitles.Revisions.Select(cue => cue.Id).ToArray(),
                item.Histories.Select(history => history.CurrentRevision.Id).ToArray());
            foreach (SubtitleCueHistory history in item.Histories)
            {
                Assert.HasCount(1, history.Revisions);
                Assert.AreSame(item.Build.Subtitles.Revisions.Single(cue => cue.Id == history.CurrentRevision.Id), history.CurrentRevision);
                Assert.AreEqual(item.Version.Id, history.CurrentRevision.CompositionVersionId);
                Assert.AreEqual(item.Version.ContentDigest, history.CurrentRevision.CompositionVersionDigest);
                Assert.AreEqual(history.SourceCandidate.Text, history.CurrentRevision.Snapshot.Text);
                Assert.AreEqual(produced.CreatedAt, history.CurrentRevision.CreatedAt);
            }
        }
        foreach (StandardShortArtifactInput input in produced.ShortForms)
        {
            var version = StandardRenderGraphFactory.CreateShortVersion(produced.Preparation, input.ShortFormId);
            Assert.AreSame(input.Subtitles, produced.Versions[version.Id].Build.Subtitles);
        }
        Assert.AreEqual(0, f.WorkCalls);
    }

    [TestMethod]
    public async Task RuntimeRegistrationGateJoinsOwnReadSerializesConcurrentCallsAndRetriesFailedRegistration()
    {
        var gate = new SourceOnlyRuntimeRegistrationGate();
        ContentDigest digest = ContentDigest.Compute([1]);
        int calls = 0;
        var entered = Signal();
        var release = Signal();
        Task first = gate.RegisterAsync("project:generated", digest, async token =>
        {
            calls++;
            await gate.RegisterAsync("project:generated", digest, _ => throw new AssertFailedException("No double registration."), token);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task[] duplicates = Enumerable.Range(0, 8).Select(_ => gate.RegisterAsync("project:generated", digest,
            _ => throw new AssertFailedException("The same persisted input must register once."), CancellationToken.None)).ToArray();
        release.TrySetResult(true);
        await first;
        await Task.WhenAll(duplicates);
        Assert.AreEqual(1, calls);
        ContentDigest changed = ContentDigest.Compute([2]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => gate.RegisterAsync("project:generated", changed,
            _ => throw new InvalidOperationException("registration failed"), CancellationToken.None));
        await gate.RegisterAsync("project:generated", changed, _ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ReadCanRestoreWhileThePipelineOwnsAnActiveRunWithoutRegisteringGeneratedCuesTwice()
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync();
        var ready = Signal();
        var release = Signal();
        int calls = 0;
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks
        {
            GeneratedReady = (_, _) => { calls++; return Task.CompletedTask; },
        }, async (execution, token) =>
        {
            await f.Pipeline.CheckpointGeneratedRuntimeAsync(execution, f.BuildGenerated(), token);
            ready.TrySetResult(true);
            await release.Task.WaitAsync(token);
        });
        Task<SourceOnlyPipelineRun> run = f.Pipeline.Start(f.Execution.Request.ExecutionId);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(run.IsCompleted);
            Assert.AreSame(run, f.Pipeline.Start(f.Execution.Request.ExecutionId));
            await f.Pipeline.RestoreRuntimeStagesAsync(f.Execution, f.Project, f.Inputs.Import, CancellationToken.None);
            Assert.AreEqual(1, calls);
            Assert.IsFalse(run.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        SourceOnlyPipelineRun completed = await run;
        // The execution seam cannot claim a production result, even after publishing
        // valid subtitle inputs. Restoring them is never evidence of render success.
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, completed.Execution!.Phase);
        Assert.IsNull(completed.Execution.ResultJson);
    }

    [TestMethod]
    public async Task EmptyMachineImportRestoresEmptyGeneratedCuesAndExplicitWarningsNotInventedSubtitles()
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(empty: true);
        SourceOnlyGeneratedRuntimeEvidence original = f.BuildGenerated();
        Assert.IsEmpty(original.LandscapeSubtitles.Subtitles.Revisions);
        Assert.IsTrue(original.LandscapeSubtitles.Warnings.Any(warning => warning.Reason == "no-real-asr-cues"));
        await f.Pipeline.CheckpointGeneratedRuntimeAsync(f.Execution, original, CancellationToken.None);
        StoredSourceOnlyExecution failed = f.Fail(bindVersion: false);
        SourceOnlyGeneratedRuntimeEvidence? restored = null;
        await f.RestartAsync(new SourceOnlyRuntimeCallbacks
        {
            GeneratedReady = (evidence, _) => { restored = evidence; return Task.CompletedTask; },
        });
        await f.Pipeline.RestoreRuntimeStagesAsync(failed, f.Project, f.Inputs.Import, CancellationToken.None);
        Assert.IsNotNull(restored);
        Assert.IsEmpty(restored.Import.Revision.Utterances);
        Assert.IsEmpty(restored.Versions.Single().Value.Histories);
        Assert.IsEmpty(restored.ShortForms);
        Assert.AreEqual(SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(failed, original),
            SourceOnlyPipeline.WriteGeneratedRuntimeEvidence(failed, restored));
        Assert.AreEqual(0, f.WorkCalls);
        Assert.AreEqual(SourceOnlyExecutionPhase.Failed, f.Executions.Get(failed.Request.ExecutionId)!.Phase);
    }

    [TestMethod]
    public async Task ReconciliationRecoveryAlsoRestoresAvailableStagesWithoutClaimingCompletion()
    {
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync();
        StoredSourceOnlyExecution failed = f.Fail(bindVersion: false, phase: SourceOnlyExecutionPhase.ReconciliationRequired);
        int restores = 0;
        StoredSourceOnlyExecution result = await SourceOnlyPipeline.ReconcileAndRestoreRuntimeAsync(failed,
            (_, _) => Task.FromResult(failed), async (project, token) =>
            {
                restores++;
                Assert.AreEqual(f.Project.Id, project);
                await f.Pipeline.RestoreRuntimeStagesAsync(failed, f.Project, f.Inputs.Import, token);
            }, CancellationToken.None);
        Assert.AreSame(failed, result);
        Assert.AreEqual(1, restores);
        Assert.AreEqual(failed.StateDigest, f.Executions.Get(failed.Request.ExecutionId)!.StateDigest);
        Assert.AreEqual(0, f.WorkCalls);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task GenerationMetadataRereadPreservesEveryExactArtifactVersionAndProfile(bool includeShort, bool includeThumbnail)
    {
        // Existing explicit synthetic renderer fixture; this tests persisted metadata,
        // not source permission, AI, physical media quality or delivered success.
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort, includeThumbnail);
        foreach (CompositionVersion version in f.Generated.CompositionVersions)
        {
            if (f.Projects.GetCompositionVersion(version.Id) is null)
            {
                f.Projects.AppendCompositionVersion(version, CompositionSnapshotJson.WriteVersion(version));
            }
        }
        await f.SaveGeneratorCheckpointAsync();
        await using var pipeline = new SourceOnlyPipeline(f.Generation.DatabasePath, f.Generation.Root, new AssetCandidateRegistry(),
            (_, _) => throw new AssertFailedException("Metadata reads must not rerun work."));
        AnalysisAudioTimeline audio = GenerationMetadataAudio(f);
        SourceOnlyPipelineGenerationEvidence? evidence = await pipeline.ReadGenerationEvidenceAsync(f.Execution, f.Generation.Preparation, audio, CancellationToken.None);
        Assert.IsNotNull(evidence);
        Assert.HasCount(f.Generated.Artifacts.Count, evidence.Outputs);
        foreach (StandardGeneratedArtifact produced in f.Generated.Artifacts)
        {
            SourceOnlyGeneratedArtifactEvidence restored = evidence.Outputs.Single(item => item.Artifact.ArtifactId == produced.Artifact.Id);
            Assert.AreEqual(produced.Artifact.ContentDigest, restored.Artifact.ContentDigest);
            Assert.AreEqual(produced.Artifact.ByteSize, restored.Artifact.ByteSize);
            Assert.AreEqual(CompositionSnapshotJson.WriteVersion(produced.Context.CompositionVersion), CompositionSnapshotJson.WriteVersion(restored.Version));
            Assert.AreEqual(produced.Context.Profile.Id, restored.ProfileId);
            Assert.AreEqual(produced.Context.Profile.ProfileDigest, restored.ProfileDigest);
            Assert.AreEqual(produced.BindingEvidence.GraphDigest, restored.GraphDigest);
            Assert.AreEqual(StandardRenderGraphFactory.Digest(produced.Manifest), restored.ManifestDigest);
        }
        Assert.HasCount(includeShort ? 1 : 0, evidence.Outputs.Where(item => item.Role == SourceOnlyResultRole.VerticalBurnIn).ToArray());
        Assert.HasCount(includeThumbnail ? 2 : 0, evidence.Outputs.Where(item => item.ThumbnailPlanId is not null).ToArray());
        Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
        Assert.IsNull(f.Executions.Get(f.Execution.Request.ExecutionId)!.ResultJson);
        Assert.IsEmpty(pipeline.Jobs.GetJobAggregate(f.Execution.Request.RootJobId)!.Attempts);
    }

    [TestMethod]
    public async Task ExistingGenerationArtifactTamperingCannotBeHiddenByUnchangedCheckpointMetadata()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        await f.SaveGeneratorCheckpointAsync();
        StandardGeneratedArtifact artifact = f.Generated.Artifacts.First();
        string path = f.Generation.Store.GetValidatedRegisteredArtifactPath(artifact.Artifact.Id);
        FileAttributes attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            await File.WriteAllBytesAsync(path, [9, 8, 7]);
            await using var pipeline = new SourceOnlyPipeline(f.Generation.DatabasePath, f.Generation.Root, new AssetCandidateRegistry(),
                (_, _) => throw new AssertFailedException("Invalid evidence must not trigger regeneration."));
            SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
                pipeline.ReadGenerationEvidenceAsync(f.Execution, f.Generation.Preparation, GenerationMetadataAudio(f), CancellationToken.None));
            Assert.AreEqual("source_only.generation_checkpoint_invalid", failure.Code);
            Assert.AreEqual(f.Execution.StateDigest, f.Executions.Get(f.Execution.Request.ExecutionId)!.StateDigest);
        }
        finally { File.SetAttributes(path, attributes); }
    }

    private static AnalysisAudioTimeline GenerationMetadataAudio(SourceOnlyEvaluationFixture f) => new(
        f.Execution.Request.ProjectId, f.Execution.Request.RootJobId, f.Generation.Asset.Id,
        f.Generation.Asset.ContentDigest, f.Generation.Asset.ByteSize, f.Generation.Original.Artifact.Id,
        ContentDigest.Compute(Encoding.UTF8.GetBytes(f.Generation.Index.CanonicalJson)), 0, 1, new RationalTime(1, 48000),
        default, 0, 1024, EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([1]), 640044, 320000, EntityId<ArtifactIdTag>.New());

    [TestMethod]
    public void MusicDefaultUsesValidatedRepositoryOrInstallRootAndDoesNotCreateALibrary()
    {
        using var fixture = new SourceFixture();
        string installProfiles = Path.Combine(fixture.Root, "Profiles");
        Directory.CreateDirectory(installProfiles);
        string music = Path.Combine(fixture.Root, "Music");
        Assert.AreEqual(music, SourceOnlyRuntimeOptions.ResolveMusicDirectory(installProfiles, null));
        Assert.IsFalse(Directory.Exists(music));
        string repoProfiles = Path.Combine(fixture.Root, "src", "HubRadio.App", "Profiles");
        Directory.CreateDirectory(repoProfiles);
        Assert.AreEqual("source_only.path_unavailable", Assert.ThrowsExactly<SourceOnlySourceException>(() =>
            SourceOnlyRuntimeOptions.ResolveMusicDirectory(repoProfiles, null)).Code);
        File.WriteAllText(Path.Combine(fixture.Root, "HubRadio.slnx"), "<Solution />");
        Assert.AreEqual(music, SourceOnlyRuntimeOptions.ResolveMusicDirectory(repoProfiles, null));
        Assert.ThrowsExactly<ArgumentException>(() => SourceOnlyRuntimeOptions.ResolveMusicDirectory(installProfiles, fixture.Root));
        // Only null selects the default. Empty/whitespace and a UNC share root have
        // no Music leaf name, so the argument guard runs before path validation.
        Assert.AreEqual(string.Empty, Path.GetFileName(@"\\server\Music"));
        foreach (string invalidArgument in new[] { "", " ", "\t", @"\\server\Music" })
        {
            Assert.AreEqual("musicDirectory", Assert.ThrowsExactly<ArgumentException>(() =>
                SourceOnlyRuntimeOptions.ResolveMusicDirectory(installProfiles, invalidArgument)).ParamName);
        }
        // These have the required leaf name but unsafe syntax: domain failures,
        // never a fallback to the otherwise valid default library location.
        foreach (string unsafePath in new[] { @"\\server\share\Music", @"\\?\C:\private\Music", @"relative\Music" })
        {
            Assert.AreEqual("source_only.path_invalid", Assert.ThrowsExactly<SourceOnlySourceException>(() =>
                SourceOnlyRuntimeOptions.ResolveMusicDirectory(installProfiles, unsafePath)).Code);
        }
        Assert.IsFalse(Directory.Exists(music));
        File.WriteAllBytes(music, [1]);
        Assert.AreEqual("source_only.path_invalid", Assert.ThrowsExactly<SourceOnlySourceException>(() =>
            SourceOnlyRuntimeOptions.ResolveMusicDirectory(installProfiles, music)).Code);
    }

    [TestMethod]
    public async Task MusicCuesUseOnlyExplicitNonSpeechCardsAndRealCatalogSelection()
    {
        var f = new AutomaticFixture();
        LongFormCompositionPlan overlay = f.LongForm([
            f.Card(LongFormElementKind.EpisodeTitle, null), f.Card(LongFormElementKind.Ending, null)]);
        Assert.IsEmpty(SourceOnlyPipeline.BuildMusicCards(overlay, f.Timeline, f.Import.Revision, f.Settings));
        LongFormCompositionPlan timed = f.LongForm([
            f.Card(LongFormElementKind.EpisodeTitle, new TimeRange(default, new RationalTime(1, 1))),
            f.Card(LongFormElementKind.EpisodeTitle, new TimeRange(new RationalTime(2, 1), new RationalTime(1, 1))),
            f.Card(LongFormElementKind.Ending, new TimeRange(new RationalTime(19, 1), new RationalTime(1, 1)))]);
        IReadOnlyList<StandardMusicCardBinding> cards = SourceOnlyPipeline.BuildMusicCards(timed, f.Timeline, f.Import.Revision, f.Settings);
        // The speech-overlapped middle card is not eligible. Default policy permits title/topic/end cards only.
        Assert.HasCount(2, cards);
        Assert.IsTrue(cards.All(item => !item.Cue.HasForegroundSpeech));
        using var source = new SourceFixture();
        string music = Path.Combine(source.Root, "Music");
        MusicCatalog absent = await new MusicCatalogScanner(music, (_, _) => throw new AssertFailedException("Absent library must not probe.")).ScanAsync();
        Assert.AreEqual(MusicLibraryState.Absent, absent.State);
        Directory.CreateDirectory(music);
        File.WriteAllBytes(Path.Combine(music, "actual-test-track.mp3"), [5, 4, 3, 2, 1]);
        int probes = 0;
        MusicCatalog scanned = await new MusicCatalogScanner(music, (_, _) =>
        {
            probes++;
            return Task.FromResult(new MusicAudioProbeResult(30m, "mp3", 48000, 2));
        }).ScanAsync();
        Assert.AreEqual(1, probes);
        Assert.AreEqual(MusicLibraryState.Available, scanned.State);
        MusicPlanningResult plan = new MusicPlanningService().Plan(scanned, cards.Select(item => item.Cue).ToArray(), MusicPlanningOptions.Default);
        Assert.IsTrue(plan.Assignments.All(item => item.TrackDigest == scanned.Tracks.Single().ContentDigest && !item.Loop && !item.Stretch));
        Assert.IsEmpty(new MusicPlanningService().Plan(scanned, [], MusicPlanningOptions.Default).Assignments);
    }

    [TestMethod]
    public void AutomaticSilenceUsesMeasuredHeadTailWithApprovedPaddingAndRetainsMiddleSilence()
    {
        var f = new AutomaticFixture();
        SilenceSuggestionCandidate[] silence = f.Silence("silence_start: 0\nsilence_end: 2 | silence_duration: 2\n" +
            "silence_start: 8\nsilence_end: 10 | silence_duration: 2\n" +
            "silence_start: 18\nsilence_end: 20 | silence_duration: 2\n");
        AutoDecisionRequest request = f.Request(silence);
        SourceOnlyAutomaticSelection selected = SourceOnlyPipeline.SelectAutomaticCandidates(request, f.Import, f.Index, f.Render.Profile, 48000);
        Assert.HasCount(2, selected.Candidates);
        Assert.AreEqual(silence[1].Id, selected.Skipped.Single().CandidateId);
        AutoDecisionPreparation applied = AutoDecisionOrchestrator.Prepare(f.Request(selected.Candidates));
        CollectionAssert.AreEqual(new[]
        {
            new TimeRange(default, new RationalTime(17, 10)),
            new TimeRange(new RationalTime(183, 10), new RationalTime(17, 10)),
        }, applied.Evidence.OrderBy(item => item.DeletedSourceRange.Start).Select(item => item.DeletedSourceRange).ToArray());
        Assert.AreEqual(new TimeRange(new RationalTime(17, 10), new RationalTime(166, 10)), applied.Child.Elements.Single().SourceRange);
        Assert.AreEqual(new RationalTime(20, 1), f.Parent.Elements.Single().SourceRange!.Value.Duration);
        Assert.IsTrue(applied.Evidence.All(item => item.SettingsSnapshotDigest == f.Settings.ContentDigest));
    }

    [TestMethod]
    public void EmptyAsrNeverCreatesSilenceProofAndWholeSourceSilenceIsNotDeleted()
    {
        var f = new AutomaticFixture(empty: true);
        SourceOnlyAutomaticSelection noEvidence = SourceOnlyPipeline.SelectAutomaticCandidates(f.Request([]), f.Import, f.Index, f.Render.Profile, 48000);
        Assert.IsEmpty(noEvidence.Candidates);
        Assert.IsEmpty(noEvidence.Skipped);
        SilenceSuggestionCandidate[] full = f.Silence("silence_start: 0\nsilence_end: 20 | silence_duration: 20\n");
        SourceOnlyAutomaticSelection undecidable = SourceOnlyPipeline.SelectAutomaticCandidates(f.Request(full), f.Import, f.Index, f.Render.Profile, 48000);
        Assert.IsEmpty(undecidable.Candidates);
        Assert.HasCount(1, undecidable.Skipped);
        Assert.AreEqual(new RationalTime(20, 1), f.Parent.Elements.Single().OutputRange.End);
    }

    [TestMethod]
    public void AutomaticSilenceRejectsSpeechOverlapAndAnUnapprovedThreshold()
    {
        var f = new AutomaticFixture();
        SilenceSuggestionCandidate[] overlapping = f.Silence("silence_start: 0\nsilence_end: 5 | silence_duration: 5\n");
        SourceOnlyAutomaticSelection retained = SourceOnlyPipeline.SelectAutomaticCandidates(f.Request(overlapping), f.Import, f.Index, f.Render.Profile, 48000);
        Assert.IsEmpty(retained.Candidates);
        Assert.AreEqual("source_only.silence_speech_overlap_retained", retained.Skipped.Single().Reason);
        DefaultSettingsSnapshot changed = f.Profile.CreateSnapshot(f.Settings.Values.Select(item => item.Key == "editing.silenceThreshold"
            ? new DefaultSettingSelection(item.Key, DefaultSettingValue.FromInteger(-49)) : item).ToArray());
        SourceOnlyAutomaticSelection unapproved = SourceOnlyPipeline.SelectAutomaticCandidates(f.Request(f.Silence(
            "silence_start: 0\nsilence_end: 2 | silence_duration: 2\n"), changed), f.Import, f.Index, f.Render.Profile, 48000);
        Assert.IsEmpty(unapproved.Candidates);
        Assert.AreEqual("source_only.auto_settings_not_approved_retained", unapproved.Skipped.Single().Reason);
    }

    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public void AdjacentTokenRepetitionCutsOnlyTheExactTimedLaterBlock(bool untimed, bool offGrid, bool accepted)
    {
        var f = new AutomaticFixture(repeated: true, untimed: untimed, offGrid: offGrid);
        RepetitionSuggestionCandidate candidate = f.Repetition();
        SourceOnlyAutomaticSelection selected = SourceOnlyPipeline.SelectAutomaticCandidates(f.Request([candidate]), f.Import, f.Index, f.Render.Profile, 48000);
        Assert.AreEqual(accepted ? 1 : 0, selected.Candidates.Count);
        Assert.AreEqual(accepted ? 0 : 1, selected.Skipped.Count);
        Assert.IsFalse(candidate.Payload.AutomaticDeletionAllowed);
        Assert.IsTrue(candidate.Payload.HumanReviewRequired);
        if (accepted)
        {
            AutoDecisionPreparation applied = AutoDecisionOrchestrator.Prepare(f.Request(selected.Candidates));
            Assert.AreEqual(new TimeRange(new RationalTime(4, 1), new RationalTime(2, 1)), applied.Evidence.Single().DeletedSourceRange);
            Assert.IsTrue(applied.Child.Elements.Any(item => item.SourceRange!.Value.Start <= new RationalTime(2, 1)
                && item.SourceRange.Value.End >= new RationalTime(4, 1)));
        }
        Assert.AreEqual(new RationalTime(20, 1), f.Parent.Elements.Single().OutputRange.End);
    }

    [TestMethod]
    public void SourcePreflightAcceptsDefaultGeometryAndExactEndAndRejectsFractionalFrameEnd()
    {
        var f = new AutomaticFixture();
        MediaInfo media = f.Media();
        SourceDisplayGeometry geometry = StandardSourceGeometry.Parse(media,
            "{\"streams\":[{\"index\":0,\"width\":1920,\"height\":1080,\"sample_aspect_ratio\":\"1:1\"}]}");
        Assert.AreEqual(new SourceDisplayGeometry(1920, 1080), geometry);
        SourceOnlyPipeline.ValidateSourceTiming(media, f.Index, f.Render.Profile, f.Import.AudioTimeline);
        Assert.IsTrue(SourceOnlyPipeline.IsExactSourceRange(f.Index.PresentationRange, f.Index, f.Render.Profile, 48000));
        var shortEnd = new PtsIndex(f.Index.CandidateId, f.Index.ProjectId, f.Index.JobId, f.Index.SourceFileName,
            f.Index.ContentDigest, f.Index.ByteSize, 0, new RationalTime(1, 30000),
            f.Index.Entries.Select(item => item.Sequence == f.Index.Entries.Count - 1
                ? new PtsIndexEntry(item.Sequence, item.PresentationTime, new RationalTime(999, 30000), item.KeyFrame) : item).ToArray());
        SourceOnlyPipeline.PipelineFailure failure = Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.ValidateSourceTiming(media, shortEnd, f.Render.Profile, f.Import.AudioTimeline));
        Assert.AreEqual("source_only.unsupported_source_timing", failure.Code);
        Assert.IsFalse(SourceOnlyPipeline.IsExactSourceRange(new TimeRange(new RationalTime(1, 100), new RationalTime(1, 1)), f.Index, f.Render.Profile, 48000));
    }

    // FR-047.1 AC-022.1: a source audio stream (or its analysis audio) that ends at most
    // 250 ms before the last video frame passes the preflight; a longer shortfall does not.
    [TestMethod]
    [DataRow(23L, 750L, true)]
    [DataRow(1L, 4L, true)]
    [DataRow(251L, 1000L, false)]
    public void SourcePreflightAcceptsOnlyABoundedShortAudioTail(long shortfallValue, long shortfallRate, bool accepted)
    {
        var f = new AutomaticFixture();
        RationalTime audioEnd = f.Index.PresentationEnd - new RationalTime(shortfallValue, shortfallRate);
        AnalysisAudioTimeline original = f.Import.AudioTimeline;
        var shortAnalysis = new AnalysisAudioTimeline(original.ProjectId, original.AnalysisJobId, original.SourceAssetId,
            original.SourceContentDigest, original.SourceByteSize, original.SourceOriginalArtifactId, original.SourceVideoPtsIndexDigest,
            original.SourceVideoStreamIndex, original.SourceAudioStreamIndex, original.SourceAudioTimeBase, original.ProbeReportedAudioStart,
            original.FirstDecodedAudioFrameTimestamp, original.FirstDecodedAudioFrameSampleCount, original.AnalysisAudioArtifactId,
            original.AnalysisAudioDigest, original.AnalysisAudioByteSize,
            audioEnd.Value * AnalysisAudioTimeline.FixedSampleRate / audioEnd.Rate, original.TimelineArtifactId);
        Assert.IsTrue(shortAnalysis.AnalysisDuration <= audioEnd &&
            shortAnalysis.AnalysisDuration > audioEnd - new RationalTime(1, AnalysisAudioTimeline.FixedSampleRate));
        foreach ((MediaInfo media, AnalysisAudioTimeline analysis) in new[]
        {
            (f.Media(audioEnd), original),
            (f.Media(), shortAnalysis),
        })
        {
            if (accepted)
            {
                SourceOnlyPipeline.ValidateSourceTiming(media, f.Index, f.Render.Profile, analysis);
            }
            else
            {
                Assert.AreEqual("source_only.unsupported_source_timing", Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() =>
                    SourceOnlyPipeline.ValidateSourceTiming(media, f.Index, f.Render.Profile, analysis)).Code);
            }
        }
    }

    [TestMethod]
    public async Task NonblockingWarningsReachAwaitedDeliveryUnchangedButNoCallbackNeverMeansDelivered()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        Assert.IsFalse(evaluated.HasBlockingFailures);
        Assert.IsTrue(evaluated.HasWarnings);
        SourceOnlyResult rebound = SourceOnlyPipeline.BindTranscriptResult(evaluated, evaluated.TranscriptReferences.Single());
        CollectionAssert.AreEqual(evaluated.GenerationWarnings.ToArray(), rebound.GenerationWarnings.ToArray());
        Assert.AreEqual(evaluated.CanonicalJson, rebound.CanonicalJson);
        SourceOnlyPipeline.PipelineFailure unavailable = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.DeliverResultAsync(f.Execution, evaluated, null, (_, _) => Task.FromResult(new SourceOnlyResultValidation([])), CancellationToken.None));
        Assert.AreEqual("source_only.delivery_unavailable", unavailable.Code);
        Assert.IsTrue(evaluated.Artifacts.All(item => !item.Delivered));

        var started = Signal();
        var release = Signal();
        Task<SourceOnlyResult> delivery = SourceOnlyPipeline.DeliverResultAsync(f.Execution, rebound, async (execution, result, token) =>
        {
            Assert.AreSame(f.Execution, execution);
            Assert.AreSame(rebound, result);
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return result; // A callback returning unchanged evaluation is NOT delivery.
        }, (_, _) => Task.FromResult(new SourceOnlyResultValidation([])), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(delivery.IsCompleted);
        release.TrySetResult(true);
        SourceOnlyPipeline.PipelineFailure invalid = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() => delivery);
        Assert.AreEqual("source_only.delivery_not_validated", invalid.Code);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, invalid.Phase);
        Assert.IsTrue(rebound.Artifacts.All(item => !item.Delivered));
    }

    [TestMethod]
    public async Task DeliveryCancellationBeforeInvocationHasNoEffectsButAfterInvocationRequiresReconciliation()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        int calls = 0;
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => SourceOnlyPipeline.DeliverResultAsync(f.Execution, result,
            (_, value, _) => { calls++; return Task.FromResult(value); }, (_, _) => Task.FromResult(new SourceOnlyResultValidation([])), before.Token));
        Assert.AreEqual(0, calls);
        using var during = new CancellationTokenSource();
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.DeliverResultAsync(f.Execution, result, (_, _, token) =>
            {
                calls++;
                during.Cancel();
                return Task.FromCanceled<SourceOnlyResult>(token);
            }, (_, _) => Task.FromResult(new SourceOnlyResultValidation([])), during.Token));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, failure.Phase);
        Assert.AreEqual("source_only.delivery_reconciliation_required", failure.Code);
    }

    [TestMethod]
    public async Task DeliveryReturnsOnlyAfterReceiptValidationAndPreservesAllWarnings()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        SourceOnlyResult delivered = SyntheticDelivery(evaluated);
        var validationStarted = Signal();
        var release = Signal();
        int validations = 0;
        Task<SourceOnlyResult> pending = SourceOnlyPipeline.DeliverResultAsync(f.Execution, evaluated,
            (_, _, _) => Task.FromResult(delivered), async (value, token) =>
            {
                validations++;
                if (ReferenceEquals(value, delivered))
                {
                    validationStarted.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                }
                return new SourceOnlyResultValidation([]); // Boundary seam, NOT production delivery verification.
            }, CancellationToken.None);
        await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(pending.IsCompleted);
        release.TrySetResult(true);
        Assert.AreSame(delivered, await pending);
        Assert.AreEqual(2, validations);
        Assert.IsTrue(delivered.HasWarnings);
        CollectionAssert.AreEqual(evaluated.GenerationWarnings.ToArray(), delivered.GenerationWarnings.ToArray());

        SourceOnlyPipeline.PipelineFailure refused = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.DeliverResultAsync(f.Execution, evaluated, (_, _, _) => Task.FromResult(delivered),
                (value, _) => Task.FromResult(new SourceOnlyResultValidation(ReferenceEquals(value, delivered)
                    ? [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.DeliveryNotValidated)] : [])), CancellationToken.None));
        Assert.AreEqual("source_only.delivery_not_validated", refused.Code);
        Assert.AreEqual(SourceOnlyExecutionPhase.ReconciliationRequired, refused.Phase);
    }

    [TestMethod]
    public async Task BlockingQcNeverInvokesDelivery()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult blocked = await f.EvaluateAsync((_, _) => throw new OutputQcException(OutputQcFailureReason.DecodeFailed, OutputQcStage.Decode));
        int calls = 0;
        SourceOnlyPipeline.PipelineFailure failure = await Assert.ThrowsExactlyAsync<SourceOnlyPipeline.PipelineFailure>(() =>
            SourceOnlyPipeline.DeliverResultAsync(f.Execution, blocked, (_, result, _) => { calls++; return Task.FromResult(result); },
                (_, _) => throw new AssertFailedException("Blocking QC must fail before delivery validation."), CancellationToken.None));
        Assert.AreEqual("source_only.output_evaluation_failed", failure.Code);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task ReconciledCompletionAwaitsRuntimeRestoreBeforeReturningInTheSameRecoveryStep()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyResult result = await f.EvaluateAsync();
        StoredSourceOnlyExecution delivering = f.WithResult(result);
        // Synthetic receipt transition: this unit test checks scheduling, not physical recovery proof.
        var completed = new StoredSourceOnlyExecution(delivering.Request, delivering.RequestJson, delivering.RequestDigest,
            delivering.SettingsRevision, null, null, delivering.Revision + 1, SourceOnlyExecutionPhase.Completed,
            result.CanonicalJson, result.CanonicalDigest, result.VersionId, result.VersionDigest, null, delivering.StateDigest);
        var restoreStarted = Signal();
        var release = Signal();
        var order = new List<string>();
        Task<StoredSourceOnlyExecution> pending = SourceOnlyPipeline.ReconcileAndRestoreRuntimeAsync(delivering,
            (_, _) => { order.Add("receipt"); return Task.FromResult(completed); }, async (project, token) =>
            {
                Assert.AreEqual(completed.Request.ProjectId, project);
                order.Add("restore-start");
                restoreStarted.TrySetResult(true);
                await release.Task.WaitAsync(token);
                order.Add("restore-finish");
            }, CancellationToken.None);
        await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(pending.IsCompleted);
        release.TrySetResult(true);
        Assert.AreSame(completed, await pending);
        CollectionAssert.AreEqual(new[] { "receipt", "restore-start", "restore-finish" }, order.ToArray());
        Assert.AreSame(delivering, await SourceOnlyPipeline.ReconcileAndRestoreRuntimeAsync(delivering,
            (_, _) => Task.FromResult(delivering), (_, _) => throw new AssertFailedException("Uncompleted receipt must not restore."), CancellationToken.None));
    }

    // Explicit synthetic delivery records for the INTERNAL scheduling seam only.
    // The production path always uses SourceOnlyResultService, which must reject
    // unregistered files and receipts like these.
    private static SourceOnlyResult SyntheticDelivery(SourceOnlyResult source) => new(source.Binding, source.GenerationDisposition,
        source.Artifacts.Select(item => new SourceOnlyResultArtifact(item.Role, item.RenditionId, item.Artifact, item.VersionId,
            item.VersionDigest, item.Source, item.GraphDigest, item.ManifestDigest, item.Evidence, item.Evaluation,
            item.Measurements, item.FailureCode, item.QcFailureReason, item.QcFailureStage,
            new SourceOnlyResultDelivery(new SourceOnlyResultArtifactReference(EntityId<ArtifactIdTag>.New(), item.ArtifactDigest, item.ByteSize, ArtifactKind.Delivery),
                new SourceOnlyResultEvidenceReference(SourceOnlyResultEvidenceKind.DeliveryReceipt,
                    new SourceOnlyResultArtifactReference(EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([5]), 1, ArtifactKind.Evidence))),
            item.ThumbnailPlanId, item.FrameEvidence, item.ProfileId, item.ProfileDigest)).ToArray(),
        source.Omissions, source.Failures, source.ReconciliationArtifacts, source.TranscriptReferences, source.PrimaryWorkflow,
        source.StagingCleanupRequired, source.GenerationWarnings, source.NativeReconciliationAttempts);

    [TestMethod]
    public async Task ConcurrentStartsShareOneOwnedTaskAndBlockASecondProjectRoot()
    {
        await using var owner = new SourceOnlyTaskOwner();
        var id = EntityId<SourceOnlyExecutionIdTag>.New();
        var project = EntityId<ProjectIdTag>.New();
        var started = Signal();
        var release = Signal();
        int calls = 0;
        async Task<SourceOnlyPipelineRun> Work(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return new(id, null, "test.no_production_result");
        }
        StartHandle[] starts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            new StartHandle(owner.Start(id, project, Work)))));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, calls);
        Assert.IsTrue(starts.All(item => ReferenceEquals(starts[0].Task, item.Task)));
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Start(
            EntityId<SourceOnlyExecutionIdTag>.New(), project, Work));
        release.TrySetResult(true);
        await starts[0].Task;
        Assert.AreEqual(0, owner.ActiveCount);
    }

    [TestMethod]
    public async Task CancellationWaitsForCooperativeCleanupAndObservesCallbackFailures()
    {
        await using var owner = new SourceOnlyTaskOwner();
        var id = EntityId<SourceOnlyExecutionIdTag>.New();
        var started = Signal();
        var canceled = Signal();
        var cleaned = Signal();
        Task<SourceOnlyPipelineRun> run = owner.Start(id, EntityId<ProjectIdTag>.New(), async token =>
        {
            using var throwingCallback = token.Register(() => throw new InvalidOperationException("private callback data"));
            started.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult(true);
                await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return new(id, null, "test.canceled");
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<SourceOnlyPipelineRun?> cancellation = owner.CancelAsync(id);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(cancellation.IsCompleted);
        cleaned.TrySetResult(true);
        Assert.AreSame(await run, await cancellation);
    }

    [TestMethod]
    public async Task DisposeCancelsAndAwaitsOwnedTasksAndPreventsLaterStart()
    {
        var owner = new SourceOnlyTaskOwner();
        var id = EntityId<SourceOnlyExecutionIdTag>.New();
        var started = Signal();
        var canceled = Signal();
        var cleaned = Signal();
        _ = owner.Start(id, EntityId<ProjectIdTag>.New(), async token =>
        {
            started.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult(true);
                await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return new(id, null, "test.disposed");
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task disposal = owner.DisposeAsync().AsTask();
        Task duplicateDisposal = owner.DisposeAsync().AsTask();
        Assert.AreSame(disposal, duplicateDisposal);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(disposal.IsCompleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => owner.Start(id, EntityId<ProjectIdTag>.New(),
            _ => Task.FromResult(new SourceOnlyPipelineRun(id, null, null))));
        cleaned.TrySetResult(true);
        await disposal;
    }

    [TestMethod]
    public async Task SchedulerDoesNotExposeAnUnobservedExceptionOrPrivateExceptionText()
    {
        await using var owner = new SourceOnlyTaskOwner();
        var id = EntityId<SourceOnlyExecutionIdTag>.New();
        SourceOnlyPipelineRun result = await owner.Start(id, EntityId<ProjectIdTag>.New(),
            _ => Task.FromException<SourceOnlyPipelineRun>(new IOException(@"C:\private\selected.mov")));
        Assert.AreEqual("source_only.scheduler_failed", result.InfrastructureFailureCode);
        Assert.IsNull(result.Execution);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task IngestCompletesBeforeRootCanAcquireTheSingleHeavySlot()
    {
        using var singleSlot = new SemaphoreSlim(1, 1);
        var started = Signal();
        var release = Signal();
        var order = new List<string>();
        Task<int> run = SourceOnlyPipeline.IngestBeforeRootAsync(async token =>
        {
            await singleSlot.WaitAsync(token);
            try
            {
                order.Add("ingest-start");
                started.TrySetResult(true);
                await release.Task.WaitAsync(token);
                order.Add("ingest-complete");
            }
            finally { singleSlot.Release(); }
        }, async token =>
        {
            await singleSlot.WaitAsync(token);
            try { order.Add("root-start"); return 7; }
            finally { singleSlot.Release(); }
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[] { "ingest-start" }, order.ToArray());
        release.TrySetResult(true);
        Assert.AreEqual(7, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        CollectionAssert.AreEqual(new[] { "ingest-start", "ingest-complete", "root-start" }, order.ToArray());
    }

    [TestMethod]
    public async Task IngestFailureOrCancellationNeverStartsRoot()
    {
        int rootStarts = 0;
        Task<int> Root(CancellationToken _) { rootStarts++; return Task.FromResult(0); }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SourceOnlyPipeline.IngestBeforeRootAsync(
            _ => Task.FromException(new InvalidOperationException("classified failure")), Root, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => SourceOnlyPipeline.IngestBeforeRootAsync(
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, Root, cancellation.Token));
        Assert.AreEqual(0, rootStarts);
    }

    [TestMethod]
    public async Task DpapiLocatorReopensExactSelectionAndRecordsAnExplicitInternalAttestation()
    {
        using var fixture = new SourceFixture();
        var locator = new SourceOnlySourceLocator(fixture.Root);
        var draft = EntityId<ProjectCreationDraftIdTag>.New();
        var candidate = EntityId<AssetIdTag>.New();
        await locator.CaptureAsync(draft, candidate, fixture.Source, ActorSid, At);
        var selected = locator.ReadSelection(draft, candidate);
        Assert.AreEqual("selected.mov", selected.FileName);
        Assert.AreEqual(ContentDigest.Compute(fixture.Bytes), selected.Digest);
        Assert.AreEqual(fixture.Bytes.LongLength, selected.ByteSize);
        byte[] protectedLocator = locator.Accept(selected, ActorSid, At.AddSeconds(1), EntityId<ArtifactIdTag>.New());
        Assert.IsFalse(Encoding.UTF8.GetString(protectedLocator).Contains(fixture.Source, StringComparison.Ordinal));

        var restarted = new SourceOnlySourceLocator(fixture.Root);
        var accepted = restarted.ReadAccepted(protectedLocator, draft, candidate);
        using FileStream held = await restarted.OpenVerifiedAsync(accepted, CancellationToken.None);
        Assert.AreEqual(fixture.Bytes.LongLength, held.Length);
        Assert.ThrowsExactly<IOException>(() => File.WriteAllBytes(fixture.Source, [9]));
        using JsonDocument rights = JsonDocument.Parse(accepted.RightsEvidenceJson);
        Assert.AreEqual(ActorSid, rights.RootElement.GetProperty("actorSid").GetString());
        Assert.AreEqual(candidate.ToString(), rights.RootElement.GetProperty("originalArtifactId").GetString());
        Assert.IsTrue(rights.RootElement.GetProperty("internalEditingPermissionAttested").GetBoolean());
        Assert.IsFalse(rights.RootElement.GetProperty("externalPublicationAllowed").GetBoolean());
        Assert.IsFalse(accepted.RightsEvidenceJson.Contains(fixture.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DpapiLocatorRejectsTamperingCrossDraftCrossWorkspaceAndWrongCandidate()
    {
        using var fixture = new SourceFixture();
        using var other = new SourceFixture();
        var locator = new SourceOnlySourceLocator(fixture.Root);
        var draft = EntityId<ProjectCreationDraftIdTag>.New();
        var candidate = EntityId<AssetIdTag>.New();
        await locator.CaptureAsync(draft, candidate, fixture.Source, ActorSid, At);
        byte[] bytes = locator.Accept(locator.ReadSelection(draft, candidate), ActorSid, At, EntityId<ArtifactIdTag>.New());
        Assert.ThrowsExactly<SourceOnlySourceException>(() => locator.ReadAccepted(bytes, draft, EntityId<AssetIdTag>.New()));
        Assert.ThrowsExactly<SourceOnlySourceException>(() => locator.ReadAccepted(bytes, EntityId<ProjectCreationDraftIdTag>.New(), candidate));
        Assert.ThrowsExactly<SourceOnlySourceException>(() => new SourceOnlySourceLocator(other.Root).ReadAccepted(bytes, draft, candidate));
        bytes[^1] ^= 1;
        SourceOnlySourceException failure = Assert.ThrowsExactly<SourceOnlySourceException>(() => locator.ReadAccepted(bytes, draft, candidate));
        Assert.IsFalse(failure.ToString().Contains(fixture.Source, StringComparison.Ordinal));
        Assert.IsNull(failure.InnerException);
    }

    [TestMethod]
    public async Task SelectionCannotBeChangedBySameSizeBytesOrSameBytesAtAnotherFileIdentity()
    {
        using var fixture = new SourceFixture();
        var locator = new SourceOnlySourceLocator(fixture.Root);
        var draft = EntityId<ProjectCreationDraftIdTag>.New();
        var candidate = EntityId<AssetIdTag>.New();
        await locator.CaptureAsync(draft, candidate, fixture.Source, ActorSid, At);
        var selected = locator.ReadSelection(draft, candidate);
        File.WriteAllBytes(fixture.Source, [9, 8, 7, 6]);
        await Assert.ThrowsExactlyAsync<SourceOnlySourceException>(() => locator.OpenVerifiedAsync(selected, CancellationToken.None));
        string replacement = Path.Combine(fixture.Root, "replacement.mov");
        File.WriteAllBytes(replacement, fixture.Bytes);
        File.Move(replacement, fixture.Source, overwrite: true);
        await Assert.ThrowsExactlyAsync<SourceOnlySourceException>(() => locator.OpenVerifiedAsync(selected, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(@"\\server\private\source.mov")]
    [DataRow(@"\\?\C:\private\source.mov")]
    [DataRow(@"C:\private\source.mov:stream")]
    [DataRow(@"C:\private.\source.mov")]
    [DataRow(@"relative\source.mov")]
    public void LocatorRejectsUnsafeSyntaxWithoutProbingTheSource(string path)
    {
        SourceOnlySourceException exception = Assert.ThrowsExactly<SourceOnlySourceException>(() =>
            SourceOnlySourceLocator.RequirePath(path, directory: false));
        Assert.AreEqual("source_only.path_invalid", exception.Code);
        Assert.IsFalse(exception.Message.Contains(path, StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocatorRejectsAnAncestorReparseBeforeInspectingAnyDescendant()
    {
        var probed = new List<string>();
        SourceOnlySourceException exception = Assert.ThrowsExactly<SourceOnlySourceException>(() =>
            SourceOnlySourceLocator.RequirePath(@"C:\redirect\private\source.mov", false, path =>
            {
                probed.Add(path);
                return path == @"C:\" ? FileAttributes.Directory
                    : path == @"C:\redirect" ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : throw new AssertFailedException("A refused ancestor must prevent descendant metadata access.");
            }));
        Assert.AreEqual("source_only.path_invalid", exception.Code);
        CollectionAssert.AreEqual(new[] { @"C:\", @"C:\redirect" }, probed.ToArray());
    }

    [TestMethod]
    public void LocatorRejectsOversizedCiphertextBeforeCallingDpapi()
    {
        using var fixture = new SourceFixture();
        var locator = new SourceOnlySourceLocator(fixture.Root);
        SourceOnlySourceException exception = Assert.ThrowsExactly<SourceOnlySourceException>(() =>
            locator.ReadAccepted(new byte[SourceOnlyExecutionStore.MaximumProtectedLocatorBytes + 1],
                EntityId<ProjectCreationDraftIdTag>.New(), EntityId<AssetIdTag>.New()));
        Assert.AreEqual("source_only.locator_invalid", exception.Code);
    }

    [TestMethod]
    public async Task CanceledSelectionDoesNotCreateARecordedLocator()
    {
        using var fixture = new SourceFixture();
        var locator = new SourceOnlySourceLocator(fixture.Root);
        var draft = EntityId<ProjectCreationDraftIdTag>.New();
        var candidate = EntityId<AssetIdTag>.New();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => locator.CaptureAsync(
            draft, candidate, fixture.Source, ActorSid, At, cancellation.Token));
        Assert.ThrowsExactly<SourceOnlySourceException>(() => locator.ReadSelection(draft, candidate));
    }

    [TestMethod]
    public void ReimportHasStableIdsAndPreservesRawTimingAndMachineIdentity()
    {
        var (timeline, worker) = CreateAnalysis(empty: false);
        var revision = EntityId<TranscriptRevisionIdTag>.New();
        var speaker = EntityId<TranscriptSpeakerIdTag>.New();
        TranscriptImportResult first = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, timeline, revision, speaker, At));
        TranscriptImportResult second = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, timeline, revision, speaker, At));
        Assert.AreEqual(TranscriptSnapshotJson.WriteRevision(first.Revision), TranscriptSnapshotJson.WriteRevision(second.Revision));
        Assert.AreEqual(TranscriptChangeSource.Machine, second.Revision.ChangeSource);
        Assert.AreSame(worker, second.RawWorkerResult);
        Assert.AreEqual(first.SourceSpans[0].Id, second.SourceSpans[0].Id);
        for (int i = 0; i < first.TimingEvidence.Count; i++)
        {
            Assert.AreEqual(first.TimingEvidence[i].TokenId, second.TimingEvidence[i].TokenId);
            Assert.AreEqual(first.TimingEvidence[i].RawStartSeconds, second.TimingEvidence[i].RawStartSeconds);
            Assert.AreEqual(first.TimingEvidence[i].ReviewReasons, second.TimingEvidence[i].ReviewReasons);
        }
        Assert.IsNull(second.Revision.Utterances[0].Tokens[1].Range);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BaselineSpansEverySourcePtsIncludingSilenceInsteadOfConcatenatingSpeech(bool empty)
    {
        var (timeline, worker) = CreateAnalysis(empty);
        TranscriptImportResult import = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, timeline,
            EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), At));
        var index = new PtsIndex(timeline.SourceAssetId, timeline.ProjectId, EntityId<JobIdTag>.New(), "source.mov",
            timeline.SourceContentDigest, timeline.SourceByteSize, 0, new RationalTime(1, 1),
            [new PtsIndexEntry(0, default, new RationalTime(10, 1), true),
                new PtsIndexEntry(1, new RationalTime(10, 1), new RationalTime(10, 1), true)]);
        SourceSpan full = SourceOnlyPipeline.CreateFullSourceSpan(index, import);
        Assert.AreEqual(default(RationalTime), full.Range.Start);
        Assert.AreEqual(new RationalTime(20, 1), full.Range.Duration);
        CollectionAssert.AreEqual(import.Revision.Utterances.SelectMany(item => item.Tokens).Select(item => item.Id).ToArray(),
            full.TranscriptTokenIds.ToArray());
        Assert.AreEqual(TranscriptChangeSource.Machine, import.Revision.ChangeSource);
        Assert.AreEqual(empty ? 0 : 1, import.Revision.Utterances.Count);
        Assert.AreEqual(full.Id, SourceOnlyPipeline.CreateFullSourceSpan(index, import).Id);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void SubtitleProjectionPreservesWholeVideoAndBindsOnlyActualCuesToTheProductionVersion(bool empty, bool allTimingValid)
    {
        var (audio, worker) = CreateAnalysis(empty, allTimingValid);
        TranscriptImportResult import = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, audio,
            EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), At));
        var pts = new PtsIndex(audio.SourceAssetId, audio.ProjectId, EntityId<JobIdTag>.New(), "source.mov",
            audio.SourceContentDigest, audio.SourceByteSize, 0, new RationalTime(1, 1),
            [new PtsIndexEntry(0, default, new RationalTime(20, 1), true)]);
        SourceSpan full = SourceOnlyPipeline.CreateFullSourceSpan(pts, import);
        var element = new CompositionElement(EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, full.Range.Duration), OriginRef.FromSourceSpan(full.Id), sourceRange: full.Range);
        var version = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), audio.ProjectId, null,
            ActorSid, At, "Full PTS test composition", RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]), [element]);
        TimelineMap timeline = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version);
        string before = CompositionSnapshotJson.WriteVersion(version);
        DefaultSettingsProfile profile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(
            RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
        DefaultSettingsSnapshot settings = profile.CreateSnapshot();
        BundledRenderProfileDefinition measured = BundledRenderProfileLoader.Load(File.ReadAllBytes(
            RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")));
        BundledRenderProfileDefinition render = StandardRenderProfileFactory.CreateLandscape(profile, settings,
            measured, new HubRadio.Core.Layout.RationalNumber(30, 1));
        var font = new RenderFontEvidence("Noto Sans JP", ContentDigest.Compute([1, 2]));

        StandardSubtitleBuildResult result = SourceOnlyPipeline.BuildProductionSubtitles(version, timeline,
            render.Layout, render.Profile, import, ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(import.Revision))),
            [.. import.SourceSpans, full], profile, settings, font, ActorSid, At);

        Assert.AreEqual(before, CompositionSnapshotJson.WriteVersion(version));
        Assert.AreEqual(new RationalTime(20, 1), version.Elements.Single().OutputRange.End);
        // FR-051 AC-026.6: the replay-only v2 rule keeps excluding a partially untimed utterance; v3 (default for
        // new runs) shows its timed part and still reports the untimed tail as unresolved.
        StandardSubtitleBuildResult exact = SourceOnlyPipeline.BuildProductionSubtitles(version, timeline,
            render.Layout, render.Profile, import, ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(import.Revision))),
            [.. import.SourceSpans, full], profile, settings, font, ActorSid, At, policy: StandardSubtitleBuildPolicy.ExactSourceTokens);
        Assert.HasCount(empty || !allTimingValid ? 0 : 1, exact.Subtitles.Revisions);
        Assert.AreEqual(!allTimingValid, exact.HasUnresolvedCues);
        Assert.AreEqual(!allTimingValid, result.HasUnresolvedCues, string.Join(",", result.Warnings.Select(item => item.Reason)));
        CollectionAssert.AreEqual(result.Warnings.ToArray(), result.Subtitles.Warnings.ToArray());
        Assert.HasCount(empty ? 0 : 1, result.Subtitles.Revisions);
        Assert.AreEqual(TranscriptChangeSource.Machine, import.Revision.ChangeSource);
        if (!empty && allTimingValid)
        {
            var cue = result.Subtitles.Revisions.Single();
            Assert.AreEqual(version.Id, cue.CompositionVersionId);
            Assert.AreEqual(version.ContentDigest, cue.CompositionVersionDigest);
            Assert.AreEqual(new TimeRange(new RationalTime(2, 1), new RationalTime(2, 1)), cue.Snapshot.Cue.Range);
            Assert.AreEqual("実測。", cue.Snapshot.Text);
            Assert.AreEqual(import.Revision.Id, cue.SourceRevisionId);
        }
    }

    private static string RepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static (AnalysisAudioTimeline Timeline, AsrWorkerResult Worker) CreateAnalysis(bool empty, bool allTimingValid = false,
        bool shortEligible = false)
    {
        var timeline = new AnalysisAudioTimeline(EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(),
            EntityId<AssetIdTag>.New(), ContentDigest.Compute([1, 2, 3]), 100, EntityId<ArtifactIdTag>.New(),
            ContentDigest.Compute([4, 5, 6]), 0, 1, new RationalTime(1, 48000), default(RationalTime),
            0, 1024, EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([7, 8, 9]), 640044, 320000, EntityId<ArtifactIdTag>.New());
        AsrWorkerSegment[] segments = empty ? [] : shortEligible ?
        [new AsrWorkerSegment(0, 0, "実測。 確認。 結論。", 2, 17, -0.1, 0.01,
            [new AsrWorkerToken("実測。", 2, 4, 0.9, false), new AsrWorkerToken("確認。", 8, 10, 0.9, false),
                new AsrWorkerToken("結論。", 15, 17, 0.9, false)])] : allTimingValid ?
        [new AsrWorkerSegment(0, 0, "実測。", 2, 4, -0.1, 0.01,
            [new AsrWorkerToken("実測。", 2, 4, 0.9, false)])] :
        [new AsrWorkerSegment(0, 0, "実測 要確認", 2, 3, -0.1, 0.01,
            [new AsrWorkerToken("実測", 2, 2.5, 0.9, false), new AsrWorkerToken("要確認", null, null, 0.6, true)])];
        var result = new AsrWorkerResult("Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120",
            new ContentDigest("4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"),
            new AsrWorkerConfiguration("cpu", "int8", "ja", "transcribe", 5, true, false, true),
            timeline.AnalysisAudioDigest, timeline.AnalysisAudioByteSize, 20, "ja", 0.9, 20, segments);
        return (timeline, result);
    }

    private sealed class AutomaticFixture
    {
        internal AutomaticFixture(bool empty = false, bool repeated = false, bool untimed = false, bool offGrid = false, bool shortEligible = false)
        {
            var (audio, original) = CreateAnalysis(empty, allTimingValid: true, shortEligible);
            AsrWorkerResult worker = original;
            if (repeated)
            {
                double shift = offGrid ? 0.01 : 0;
                AsrWorkerToken[] words = new[] { "実測", "確認", "実測", "確認" }.Select((text, i) =>
                    new AsrWorkerToken(text, untimed && i == 3 ? null : 2 + i + shift,
                        untimed && i == 3 ? null : 3 + i + shift, 0.9, untimed && i == 3)).ToArray();
                worker = new AsrWorkerResult(original.ModelRepository, original.ModelRevision, original.ModelManifestDigest,
                    original.Configuration, original.InputAudioDigest, original.InputAudioByteSize, original.InputAudioDurationSeconds,
                    original.Language, original.LanguageConfidence, original.EngineDurationSeconds,
                    [new AsrWorkerSegment(0, 0, "実測確認実測確認", 2 + shift, 6 + shift, -0.1, 0.01, words)]);
            }
            Import = SourceOnlyPipeline.RebindImportedIds(TranscriptImporter.Import(worker, audio,
                EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), At));
            Index = new PtsIndex(audio.SourceAssetId, audio.ProjectId, EntityId<JobIdTag>.New(), "source.mov", audio.SourceContentDigest,
                audio.SourceByteSize, 0, new RationalTime(1, 30), Enumerable.Range(0, 600)
                    .Select(i => new PtsIndexEntry(i, new RationalTime(i, 30), new RationalTime(1, 30), i == 0)).ToArray());
            Full = SourceOnlyPipeline.CreateFullSourceSpan(Index, Import);
            var element = new CompositionElement(EntityId<CompositionElementIdTag>.New(), new TimeRange(default, Full.Range.Duration),
                OriginRef.FromSourceSpan(Full.Id), sourceRange: Full.Range);
            Parent = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), audio.ProjectId, null, ActorSid, At,
                "Coordinator policy fixture, not media verification", RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]), [element]);
            Copy = new WorkingCopy(EntityId<WorkingCopyIdTag>.New(), Parent.ProjectId, Parent.Id, At, At, Parent.Elements);
            Timeline = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), Parent);
            Profile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            Settings = Profile.CreateSnapshot();
            Render = StandardRenderProfileFactory.CreateLandscape(Profile, Settings, BundledRenderProfileLoader.Load(File.ReadAllBytes(
                RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json"))), new RationalNumber(30, 1));
            Model = new ModelRun(EntityId<ModelRunIdTag>.New(), "test", "synthetic-policy-input", "1", ContentDigest.Compute([1]),
                ModelHashAvailability.Available, ContentDigest.Compute([2]), ContentDigest.Compute([3]), ContentDigest.Compute([4]),
                ModelRunOutcome.Succeeded, null, ModelExecutionLocation.Local, null, null, null, null, At, At);
        }
        internal TranscriptImportResult Import { get; }
        internal PtsIndex Index { get; }
        internal SourceSpan Full { get; }
        internal CompositionVersion Parent { get; }
        internal WorkingCopy Copy { get; }
        internal TimelineMap Timeline { get; }
        internal DefaultSettingsProfile Profile { get; }
        internal DefaultSettingsSnapshot Settings { get; }
        internal BundledRenderProfileDefinition Render { get; }
        internal ModelRun Model { get; }
        internal AutoDecisionRequest Request(IReadOnlyList<SuggestionCandidate> candidates, DefaultSettingsSnapshot? settings = null) => new(
            Parent, Copy, 0, Profile, settings ?? Settings, Import.Revision,
            Import.SourceSpans.Append(Full).Concat(candidates.OfType<SilenceSuggestionCandidate>().Select(item => item.Payload.SourceSpan)).ToArray(),
            [Model], candidates, [], ActorSid, At, "Apply actual fixture candidates only.");
        internal SilenceSuggestionCandidate[] Silence(string stdout) => SilenceDetector.ParseProcessResult(Import.AudioTimeline, Model.Id,
            new ProcessRunResult(0, "", stdout, false, TimeSpan.Zero)).Candidates.Select(item => SuggestionService.Register(item, [])).ToArray();
        internal RepetitionSuggestionCandidate Repetition()
        {
            Utterance utterance = Import.Revision.Utterances.Single();
            var earlier = utterance.Tokens.Take(2).Select(item => item.Id).ToArray();
            var later = utterance.Tokens.Skip(2).Select(item => item.Id).ToArray();
            var digest = ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(Import.Revision)));
            const string rule = RepetitionReasonFeatures.AdjacentExactTokenSequenceRule;
            var key = RepetitionSuggestionPayload.ComputeCandidateKey(Import.Revision.Id, digest, rule, utterance.SourceSpanId, later, utterance.SourceSpanId, earlier);
            var payload = new RepetitionSuggestionPayload(key, Model.Id, Import.Revision.Id, digest, utterance.SourceSpanId, later,
                RepetitionSuggestionPayload.CanonicalRuleVersion, SuggestionKind.DeleteRange, SuggestionStatus.Proposed, "Adjacent exact token blocks.",
                null, false, true, new RepetitionReasonFeatures(rule, utterance.SourceSpanId, earlier, 2, true, true, false, true, true));
            return SuggestionService.Register(payload, new Suggestion(EntityId<SuggestionIdTag>.New(), Model.Id, SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed, OriginRef.FromSourceSpan(utterance.SourceSpanId), payload.Reason, null, null, null), []);
        }
        internal MediaInfo Media(RationalTime? audioDuration = null) => new(Index.CandidateId, Index.ProjectId, Index.JobId, Index.SourceFileName, Index.ContentDigest, Index.ByteSize,
            MediaContainerClassification.IsoBaseMediaFtyp, ["mov"], null, new RationalTime(20, 1),
            [new MediaStreamInfo(0, MediaStreamType.Video, "h264", null, 1920, 1080, "yuv420p", new RationalNumber(30, 1), new RationalNumber(30, 1),
                new RationalTime(1, 30), default(RationalTime), new RationalTime(20, 1), null, null, null),
             new MediaStreamInfo(1, MediaStreamType.Audio, "aac", null, null, null, null, null, null, new RationalTime(1, 48000),
                default(RationalTime), audioDuration ?? new RationalTime(20, 1), 48000, 2, "stereo")], null, null,
             audioDuration is RationalTime differing && differing != new RationalTime(20, 1)
                ? [new MediaInfoWarning(MediaInfoWarningCode.DurationFieldsDiffer, new DurationFieldsDifferWarningEvidence(
                    [new(MediaDurationFieldKind.FormatDuration, null, new RationalTime(20, 1)),
                     new(MediaDurationFieldKind.StreamEndOffset, 0, new RationalTime(20, 1)),
                     new(MediaDurationFieldKind.StreamEndOffset, 1, differing)]))]
                : []);
        internal LongFormElementPlan Card(LongFormElementKind kind, TimeRange? range) => new(EntityId<LongFormElementIdTag>.New(), kind,
            LongFormElementState.Included, "Fixture card", range, [OriginRef.FromSourceSpan(Full.Id)], [], null, null);
        internal LongFormCompositionPlan LongForm(IReadOnlyList<LongFormElementPlan> elements)
        {
            var asset = new Asset(Index.CandidateId, "source.mov", "video/quicktime", Index.ContentDigest, Index.ByteSize,
                new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic fixture only", false));
            var project = new Project(Parent.ProjectId, "source", "test", "test", PublicationScope.InternalOnly, null, [asset], []);
            var provenance = new LongFormProvenanceSnapshot(project.Id, Import.Revision.Id,
                ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(Import.Revision))), [Full], []);
            var template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), Render.TemplateReference, BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
            return new LongFormCompositionPlan(EntityId<LongFormPlanIdTag>.New(), project, provenance, template, Render,
                LongFormDestination.InternalDistribution, null, elements, [], [asset], [], false, false, false, ContentDigest.Compute([9]));
        }
    }

    // Stage-only fixture: real stores, planner, subtitle builder and checkpoint bytes;
    // synthetic ASR timing remains explicit and the source stays UNCONFIRMED. No test
    // calls the production ingest/AI/render runner or manufactures an approved source.
    private sealed class RuntimeCheckpointFixture : IAsyncDisposable
    {
        private readonly SourceFixture files = new();
        private RuntimeCheckpointFixture(bool includeShort, bool empty)
        {
            Inputs = new AutomaticFixture(empty: empty, shortEligible: includeShort);
            DatabasePath = Path.Combine(files.Root, "runtime.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(files.Root, "backups"));
            Projects = new ProjectStore(DatabasePath);
            Executions = new SourceOnlyExecutionStore(DatabasePath);
            Artifacts = new ArtifactStore(DatabasePath, files.Root);
            Versions = new VersionService(Projects);
            var settings = new DefaultSettingsStore(DatabasePath);
            _ = settings.RegisterProfile(Encoding.UTF8.GetBytes(Inputs.Profile.CanonicalJson));
            var emptyProject = new Project(Inputs.Parent.ProjectId, "source", "test", "test", PublicationScope.InternalOnly, null, [], []);
            var draft = new ProjectCreationDraft(EntityId<ProjectCreationDraftIdTag>.New(), Inputs.Index.CandidateId,
                "source", "test", "test", PublicationScope.InternalOnly,
                [new ProjectOutputProfileReference(Inputs.Render.Profile.Id, "long-form", Inputs.Render.Profile.ProfileDigest)],
                "ja-JP", ["Host"], new ProjectDeadlineSelection(null), Inputs.Render.TemplateReference);
            string json = CanonicalJson.Canonicalize(JsonSerializer.Serialize(draft));
            var drafts = new ProjectCreationDraftStore(DatabasePath);
            _ = drafts.CreateDraft(draft, json, At);
            var request = new SourceOnlyExecutionRequest(EntityId<SourceOnlyExecutionIdTag>.New(), emptyProject.Id, draft.Id,
                Inputs.Index.CandidateId, Inputs.Import.AudioTimeline.AnalysisJobId, Inputs.Index.JobId, Inputs.Settings, ActorSid, At);
            Execution = drafts.FinalizeSourceOnlyWithExecution(draft, emptyProject, json, 0, At, request);
            var source = new Asset(Inputs.Index.CandidateId, "source.mov", "video/quicktime", Inputs.Index.ContentDigest, Inputs.Index.ByteSize,
                new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic stage-only fixture, not permission to render or deliver", false));
            Project = new Project(emptyProject.Id, "source", "test", "test", PublicationScope.InternalOnly, null, [source], []);
            string projectJson = ProjectSnapshotJson.WriteProject(Project);
            _ = Projects.UpdateProject(Project, projectJson, 0, At);
            Projects.AppendCompositionVersion(Inputs.Parent, CompositionSnapshotJson.WriteVersion(Inputs.Parent));
            Projects.CreateWorkingCopy(Inputs.Copy, CompositionSnapshotJson.WriteWorkingCopy(Inputs.Copy));
            Pipeline = NewPipeline(null);
            Analysis = Pipeline.BuildAnalysis(Project, Inputs.Import, Execution, At.AddSeconds(1));
            var template = new BrandTemplateVersion(EntityId<SeriesIdTag>.New(), Inputs.Render.TemplateReference,
                BrandTemplateKind.LongForm, []).ApplyEditorOverrides([]);
            LongFormElementInput Omitted(LongFormElementKind kind) => new(EntityId<LongFormElementIdTag>.New(), kind, false, null, null, [], []);
            var longForm = new LongFormCompositionRequest(EntityId<LongFormPlanIdTag>.New(), Project,
                new LongFormProvenanceSnapshot(Project.Id, Inputs.Import.Revision.Id,
                    ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(Inputs.Import.Revision))),
                    Inputs.Import.SourceSpans.Append(Inputs.Full).ToArray(), []), template, Inputs.Render,
                LongFormDestination.InternalDistribution, null, Omitted(LongFormElementKind.PreRollMargin), Omitted(LongFormElementKind.Opening),
                Omitted(LongFormElementKind.ProgramName), Omitted(LongFormElementKind.EpisodeNumber), Omitted(LongFormElementKind.EpisodeTitle),
                Omitted(LongFormElementKind.PerformerIntroduction), Omitted(LongFormElementKind.PublicationScopeIndicator), Omitted(LongFormElementKind.Body), [],
                Omitted(LongFormElementKind.Summary), [new LongFormCallToActionInput(LongFormDestination.InternalDistribution, Omitted(LongFormElementKind.CallToAction))],
                Omitted(LongFormElementKind.Ending), Omitted(LongFormElementKind.PostRollMargin));
            EmphasisSuggestionCandidate[] candidates = [];
            if (includeShort)
            {
                SourceSpan span = Inputs.Import.SourceSpans.Single();
                var suggestion = new Suggestion(EntityId<SuggestionIdTag>.New(), EntityId<ModelRunIdTag>.New(), SuggestionKind.Highlight,
                    SuggestionStatus.Proposed, OriginRef.FromSourceSpan(span.Id), "Synthetic candidate using the actual fixture utterance span", null, null, null);
                candidates = [SuggestionService.Register(new EmphasisCandidate(suggestion, span, Inputs.Import.Revision.Id,
                    longForm.Provenance.TranscriptRevisionDigest, Analysis.TopicMetadata.Single().Id, EmphasisCandidateService.RuleVersion,
                    Inputs.Import.Revision.Utterances.Single().Text, EmphasisTextSourceKind.OriginalTranscript, [EmphasisReasonKind.ImportantStatement], []), [])];
            }
            Composition = StandardCompositionOrchestrator.Apply(Versions, new StandardCompositionOrchestrationRequest(
                Inputs.Parent, Inputs.Copy, 0, new StandardProjectStoreState(1, ContentDigest.Compute(Encoding.UTF8.GetBytes(projectJson)), At),
                Inputs.Profile, Inputs.Settings, File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")),
                File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json")), longForm, Inputs.Timeline.Id,
                new SourceDisplayGeometry(1920, 1080), new RationalNumber(30, 1), ContentDigest.Compute([10]), ContentDigest.Compute([11]),
                new MusicPlanningService().Plan(MusicCatalog.CreateUnavailable(MusicLibraryState.Absent, MusicCatalogWarning.LibraryAbsent), [], MusicPlanningOptions.Default),
                Analysis.TopicMetadata, candidates, [], ActorSid, At.AddSeconds(2), "Real planner replay on unapproved synthetic input"));
        }
        internal string DatabasePath { get; }
        internal AutomaticFixture Inputs { get; }
        internal Project Project { get; }
        internal ProjectStore Projects { get; }
        internal SourceOnlyExecutionStore Executions { get; }
        internal ArtifactStore Artifacts { get; }
        internal VersionService Versions { get; }
        internal StoredSourceOnlyExecution Execution { get; private set; }
        internal SourceOnlyPipeline Pipeline { get; private set; }
        internal StandardCompositionApplication Composition { get; }
        internal StandardCompositionPreparation Preparation => Composition.Preparation;
        internal SourceOnlyAnalysisEvidence Analysis { get; }
        internal int WorkCalls { get; private set; }
        internal static async Task<RuntimeCheckpointFixture> CreateAsync(bool includeShort = false, bool saveAnalysis = true,
            bool saveComposition = true, bool empty = false)
        {
            var fixture = new RuntimeCheckpointFixture(includeShort, empty);
            if (saveAnalysis) { await fixture.SaveEvidenceAsync("analysis-evidence", JsonSerializer.Serialize(fixture.Analysis)); }
            if (saveComposition) { await fixture.Pipeline.SaveCompositionStageAsync(fixture.Execution, "composition-checkpoint", fixture.Composition.Checkpoint, CancellationToken.None); }
            return fixture;
        }
        private SourceOnlyPipeline NewPipeline(SourceOnlyRuntimeCallbacks? callbacks,
            Func<StoredSourceOnlyExecution, CancellationToken, Task>? operation = null) => new(DatabasePath, files.Root, new AssetCandidateRegistry(),
            operation ?? ((_, _) => { WorkCalls++; throw new AssertFailedException("Restore must not rerun underlying work."); }), callbacks: callbacks);
        internal async Task RestartAsync(SourceOnlyRuntimeCallbacks callbacks,
            Func<StoredSourceOnlyExecution, CancellationToken, Task>? operation = null)
        {
            await Pipeline.DisposeAsync();
            Pipeline = NewPipeline(callbacks, operation);
        }
        internal SourceOnlyGeneratedRuntimeEvidence BuildGenerated(
            StandardSubtitleBuildPolicy policy = StandardSubtitleBuildPolicy.FullCoverage) => SourceOnlyPipeline.BuildGeneratedRuntimeEvidence(Preparation, Inputs.Import,
            Inputs.Profile, Inputs.Settings, new RenderFontEvidence("Noto Sans JP", ContentDigest.Compute([8, 9])), ActorSid, At.AddSeconds(3),
            policy: policy);
        internal SourceOnlyResult FailureResult(bool reconciliation, bool cleanup) => new(
            new SourceOnlyResultBinding(Project.Id, Execution.Request.ExecutionId, Execution.Request.RootJobId, Inputs.Settings.ContentDigest,
                Preparation.Child.Id, Preparation.Child.ContentDigest),
            reconciliation ? StandardArtifactGenerationDisposition.ReconciliationRequired : StandardArtifactGenerationDisposition.Failed,
            [], [], [new SourceOnlyResultFailure(reconciliation ? SourceOnlyResultFailureCode.GenerationReconciliationRequired : SourceOnlyResultFailureCode.GenerationFailed)],
            [], [], null, cleanup);
        internal StoredSourceOnlyExecution Fail(bool bindVersion, SourceOnlyResult? result = null, SourceOnlyExecutionPhase phase = SourceOnlyExecutionPhase.Failed)
        {
            var jobs = Pipeline.Jobs;
            var running = new JobAttempt(EntityId<JobAttemptIdTag>.New(), Execution.Request.RootJobId, 1, JobState.Running,
                At, At.AddSeconds(4), null, null);
            _ = jobs.StartAttempt(Execution.Request.RootJobId, 0, running, At.AddSeconds(4));
            _ = jobs.CompleteAttempt(Execution.Request.RootJobId, 1, new JobAttempt(running.Id, running.JobId, 1, JobState.Failed,
                At, running.StartedAt, At.AddSeconds(5), "job.execution_failed"), At.AddSeconds(5));
            _ = jobs.CancelQueued(Execution.Request.IngestJobId, 0, At.AddSeconds(5));
            Execution = Executions.UpdateState(Execution.Request.ExecutionId, Execution.Revision, SourceOnlyExecutionPhase.Evaluating,
                versionId: bindVersion ? Preparation.Child.Id : null, digest: bindVersion ? Preparation.Child.ContentDigest : null);
            Execution = Executions.UpdateState(Execution.Request.ExecutionId, Execution.Revision, phase, result?.CanonicalJson,
                bindVersion ? Preparation.Child.Id : null, bindVersion ? Preparation.Child.ContentDigest : null, "source_only.test_stage_failed");
            return Execution;
        }
        internal async Task SaveEvidenceAsync(string stage, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(json));
            string directory = Path.Combine(files.Root, "projects", Project.Id.ToString(), "staging");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllBytesAsync(path, bytes);
            var id = SourceOnlyPipeline.StableId<ArtifactIdTag>(Execution.Request.ExecutionId.ToString(), stage);
            _ = await Artifacts.PromoteAsync(new ArtifactPromotionRequest(id, Project.Id, ArtifactKind.Analysis, path,
                "source-only/" + id + "." + stage + ".json", ContentDigest.Compute(bytes), bytes.LongLength, At.AddSeconds(3)));
        }
        public async ValueTask DisposeAsync()
        {
            await Pipeline.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            files.Dispose();
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record StartHandle(Task<SourceOnlyPipelineRun> Task);
    private sealed class SourceFixture : IDisposable
    {
        internal SourceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "HubRadio.SourceOnlyPipelineTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "selected.mov");
            File.WriteAllBytes(Source, Bytes);
        }
        internal string Root { get; }
        internal string Source { get; }
        internal byte[] Bytes { get; } = [1, 2, 3, 4];
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}