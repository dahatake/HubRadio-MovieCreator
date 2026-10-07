using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Policy;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

/// <summary>Host composition/lifetime seams only: no native processes, real ASR, rendering or downloads.</summary>
[TestClass]
public sealed class SourceOnlyApplicationRuntimeTests
{
    private const string Actor = "S-1-5-21-111-222-333-1001";
    private static readonly DateTimeOffset At = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ProductionServicesInjectTheSameSubtitleJournalWhileLegacyRegistrationsRemainSessionOnly()
    {
        using var fixture = new Fixture();
        SourceOnlyApplicationRegistrations production = fixture.Services.Registrations;
        Assert.IsNotNull(fixture.Services.SubtitleEdits);
        Assert.AreSame(fixture.Services.SubtitleEdits, production.DurableSubtitles);
        Assert.IsNotNull(production.Callbacks.GeneratedReady);
        var projectId = EntityId<ProjectIdTag>.New();
        Assert.IsEmpty(production.CaptureSubtitles(projectId, []));
        Assert.ThrowsExactly<KeyNotFoundException>(() => production.CaptureSubtitles(projectId,
            [EntityId<CompositionVersionIdTag>.New()]));

        var legacy = new SourceOnlyApplicationRegistrations(new TermStore(fixture.Services.DatabasePath));
        Assert.IsNull(legacy.DurableSubtitles);
        Assert.ThrowsExactly<InvalidOperationException>(() => legacy.CaptureSubtitles(projectId, []));
        Assert.AreEqual(LayoutSubtitleBridgeStatus.NotFound, legacy.Execute(Request(BridgeMethod.LayoutRead,
            new { projectId = projectId.ToString(), evaluationTime = (object?)null }), Actor, CancellationToken.None).Status);
    }

    [TestMethod]
    public void PackagePathsUseTheShippingLayoutWithoutSearchingOrCreatingResources()
    {
        using var fixture = new Fixture();
        SourceOnlyApplicationResources resources = fixture.PackageResources();

        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "Tools", "ffmpeg.exe"), resources.FfmpegPath);
        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "Tools", "ffprobe.exe"), resources.FfprobePath);
        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "Worker"), resources.WorkerDirectory);
        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "ModelCatalog", "catalog.json"), resources.ModelCatalogPath);
        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "ModelCatalog"), resources.ModelBundleRootPath);
        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "DefaultSettings", "Profiles", "1.2.0.json"), resources.DefaultSettingsProfilePath);
        Assert.AreEqual(Path.Combine(fixture.PackageRoot, "Music"), resources.MusicDirectory);
        Assert.AreEqual(fixture.PackageRoot, resources.PackageRoot);
        Assert.IsNull(resources.Font, "Font manifest resolution is deferred until resource initialization.");
        Assert.IsNull(resources.ApplicationVerification, "DLL build identity is read lazily, not self-approved by the path factory.");
        Assert.IsFalse(Directory.Exists(fixture.PackageRoot));
    }

    [TestMethod]
    public async Task MissingPayloadKeepsSettingsAndDpapiDraftButCannotAcceptAProjectOrJobs()
    {
        using var fixture = new Fixture();
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(
            fixture.Services, fixture.PackageResources());
        Assert.IsFalse(runtime.IsAvailable);
        Assert.IsNull(runtime.Pipeline);
        Assert.AreEqual(SourceOnlyApplicationDependency.Ffmpeg, runtime.MissingDependency);
        string settingsBefore = fixture.Settings.GetGlobalSettings()!.Snapshot.CanonicalJson;
        ProjectCreationService service = fixture.Service(runtime);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
        var candidateId = EntityId<AssetIdTag>.Parse(draft.SourceCandidateId!);

        Assert.AreEqual("available", draft.SourceStatus);
        Assert.IsFalse(draft.IsFinalized);
        Assert.IsNotNull(draft.SourceOnly);
        string locatorPath = Path.Combine(fixture.Root, "source-only", "selections", draftId + ".dpapi");
        byte[] ciphertext = File.ReadAllBytes(locatorPath);
        Assert.IsTrue(ciphertext.Length > 0);
        Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains(fixture.SourcePath, StringComparison.Ordinal));
        var selected = new SourceOnlySourceLocator(fixture.Root).ReadSelection(draftId, candidateId);
        Assert.AreEqual(fixture.SourcePath, selected.Path);

        string before = fixture.Services.Drafts.GetDraft(draftId)!.CanonicalJson;
        SourceOnlyApplicationDependencyException failure = Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() =>
            service.FinalizeDraft(draftId, draft.Revision, Input(draft)));
        Assert.AreEqual(SourceOnlyApplicationDependency.Ffmpeg, failure.Dependency);
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.ToString().Contains(fixture.Root, StringComparison.Ordinal));
        string response = await new ProjectBridgeHandler(service).HandleAsync(
            Request(BridgeMethod.FinalizeDraft, new { draftId = draft.DraftId, expectedRevision = draft.Revision, input = Input(draft) }),
            CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.AreEqual("project.runtime_unavailable", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("result", out _));
        Assert.IsFalse(response.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsFalse(response.Contains(Actor, StringComparison.Ordinal));
        Assert.AreEqual(before, fixture.Services.Drafts.GetDraft(draftId)!.CanonicalJson);
        Assert.IsFalse(fixture.Services.Drafts.GetDraft(draftId)!.IsFinalized);
        Assert.IsNull(fixture.Services.Executions.GetByDraft(draftId));
        foreach (JobState state in Enum.GetValues<JobState>()) { Assert.IsEmpty(fixture.Services.Jobs.ListJobs(state)); }
        Assert.IsTrue(fixture.Registry.IsAvailable(candidateId));
        Assert.AreEqual(settingsBefore, fixture.Settings.GetGlobalSettings()!.Snapshot.CanonicalJson);
        SourceOnlyRecoveryResult recovery = await runtime.RecoverAsync();
        Assert.AreEqual(SourceOnlyApplicationRuntime.DependenciesUnavailableCode, recovery.FailureCode);
        Assert.IsEmpty(recovery.Executions);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "cache")));
    }

    [TestMethod]
    public async Task DependencyFailureIsSanitizedAndInitializationDoesNotInvokeRecovery()
    {
        using var fixture = new Fixture();
        await using SourceOnlyApplicationRuntime failed = await SourceOnlyApplicationRuntime.CreateAsync(fixture.Services,
            _ => Task.FromException<SourceOnlyApplicationSession>(new IOException(@"C:\private\payload")));
        Assert.AreEqual(SourceOnlyApplicationDependency.Initialization, failed.MissingDependency);
        Assert.IsNull(failed.ReadyPipeline);

        var coordinator = new LifetimeCoordinator();
        int initialized = 0;
        await using SourceOnlyApplicationRuntime ready = await SourceOnlyApplicationRuntime.CreateAsync(fixture.Services, _ =>
        {
            Interlocked.Increment(ref initialized);
            return Task.FromResult(coordinator.Session());
        });
        Assert.AreEqual(1, initialized);
        Assert.IsTrue(ready.IsAvailable);
        Assert.AreSame(fixture.Services, ready.Services);
        Assert.AreSame(fixture.Registry, ready.Services.Candidates);
        Assert.AreSame(fixture.Settings, ready.Services.Settings);
        Assert.AreSame(fixture.Services.Jobs, ready.Services.Jobs);
        Assert.AreSame(fixture.Services.Coordinator, ready.Services.Coordinator);
        Assert.AreSame(fixture.Services.Results, ready.Services.Results);
        Assert.AreSame(fixture.Services.Registrations.Transcript, ready.Services.Registrations.Transcript);
        _ = fixture.Settings.GetGlobalSettings();
        await ready.RestoreAfterReadAsync(EntityId<ProjectIdTag>.New(), CancellationToken.None);
        Assert.AreEqual(0, coordinator.Recoveries);
        Assert.AreEqual(0, coordinator.Acceptances);
    }

    [TestMethod]
    public async Task RepairReinitializesAFailedRuntimeAndRerunsAnUnsuccessfulRecovery()
    {
        using var fixture = new Fixture();
        int attempts = 0;
        int recoveries = 0;
        var coordinator = new LifetimeCoordinator
        {
            Recover = _ => Task.FromResult(new SourceOnlyRecoveryResult([], false, Interlocked.Increment(ref recoveries) == 1 ? "source_only.recovery_failed" : null)),
        };
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(fixture.Services, _ =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<SourceOnlyApplicationSession>(new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Worker))
                : Task.FromResult(coordinator.Session()));
        Assert.AreEqual(SourceOnlyApplicationDependency.Worker, runtime.MissingDependency);
        Assert.IsFalse(runtime.IsAvailable);

        Assert.IsFalse(await runtime.RepairAsync(), "The first recovery is configured to fail.");
        Assert.IsTrue(runtime.IsAvailable);
        Assert.IsNull(runtime.MissingDependency);
        Assert.IsNull(runtime.ReadyPipeline, "A coordinator-only seam has no pipeline, so readiness is observed through Accept.");
        Assert.IsTrue(await runtime.RepairAsync());
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(2, recoveries);
        Assert.IsTrue(await runtime.RepairAsync(), "A ready runtime is not repaired again.");
        Assert.AreEqual(2, recoveries);
        Assert.ThrowsExactly<NotSupportedException>(() => Accept(runtime, fixture.Resolution()));
    }

    [TestMethod]
    public async Task RepairKeepsTheRuntimeUnavailableAndReportsTheCurrentDependencyWhenInitializationStillFails()
    {
        using var fixture = new Fixture();
        int attempts = 0;
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(fixture.Services, _ =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<SourceOnlyApplicationSession>(new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Ffmpeg))
                : Task.FromException<SourceOnlyApplicationSession>(new IOException(@"C:\private\payload")));
        Assert.AreEqual(SourceOnlyApplicationDependency.Ffmpeg, runtime.MissingDependency);
        Assert.IsFalse(await runtime.RepairAsync());
        Assert.AreEqual(SourceOnlyApplicationDependency.Initialization, runtime.MissingDependency);
        Assert.IsFalse(runtime.IsAvailable);
        SourceOnlyApplicationDependencyException failure = Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(
            () => Accept(runtime, fixture.Resolution()));
        Assert.AreEqual(SourceOnlyApplicationDependency.Initialization, failure.Dependency);
    }

    [TestMethod]
    public async Task StartRepairsTheRuntimeOnceAndRetriesWithoutDoubleAcceptance()
    {
        using var fixture = new Fixture();
        int attempts = 0;
        var coordinator = new LifetimeCoordinator();
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(fixture.Services, _ =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<SourceOnlyApplicationSession>(new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Model))
                : Task.FromResult(coordinator.Session()));
        ProjectCreationService service = fixture.Service(runtime);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        var handler = new ProjectBridgeHandler(service, null, runtime.RepairAsync);

        string response = await handler.HandleAsync(
            Request(BridgeMethod.FinalizeDraft, new { draftId = draft.DraftId, expectedRevision = draft.Revision, input = Input(draft) }),
            CancellationToken.None);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, coordinator.Acceptances, "The repaired runtime accepts exactly once; the seam then refuses by design.");
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.AreEqual("project.operation_failed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StartExplainsTheCauseAndTheRemedyWhenRepairCannotRecoverTheRuntime()
    {
        using var fixture = new Fixture();
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(
            fixture.Services, fixture.PackageResources());
        ProjectCreationService service = fixture.Service(runtime);
        ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync();
        string before = fixture.Services.Drafts.GetDraft(EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId))!.CanonicalJson;
        var handler = new ProjectBridgeHandler(service, null, runtime.RepairAsync);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            string response = await handler.HandleAsync(
                Request(BridgeMethod.FinalizeDraft, new { draftId = draft.DraftId, expectedRevision = draft.Revision, input = Input(draft) }),
                CancellationToken.None);
            using JsonDocument json = JsonDocument.Parse(response);
            JsonElement error = json.RootElement.GetProperty("error");
            Assert.AreEqual("project.runtime_unavailable", error.GetProperty("code").GetString());
            string message = error.GetProperty("message").GetString()!;
            StringAssert.Contains(message, "ffmpeg.exe");
            StringAssert.Contains(message, "自動修復");
            StringAssert.Contains(message, "Release");
            StringAssert.Contains(message, "もう一度");
            Assert.IsFalse(response.Contains(fixture.Root, StringComparison.Ordinal));
        }

        Assert.AreEqual(before, fixture.Services.Drafts.GetDraft(EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId))!.CanonicalJson);
        Assert.IsFalse(fixture.Services.Drafts.GetDraft(EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId))!.IsFinalized);
    }

    [TestMethod]
    public void EveryDependencyHasADistinctCauseAndNeverEmbedsAPath()
    {
        var messages = new HashSet<string>();
        foreach (SourceOnlyApplicationDependency dependency in Enum.GetValues<SourceOnlyApplicationDependency>())
        {
            string message = SourceOnlyDependencyGuidance.Describe(dependency, repairAttempted: true);
            Assert.IsTrue(messages.Add(message), dependency.ToString());
            Assert.IsFalse(message.Contains(@":\\", StringComparison.Ordinal) && message.Contains("Users", StringComparison.Ordinal));
        }
        StringAssert.DoesNotMatch(SourceOnlyDependencyGuidance.Describe(SourceOnlyApplicationDependency.Ffmpeg, repairAttempted: false), new System.Text.RegularExpressions.Regex("自動修復"));
    }

    [TestMethod]
    public async Task RecoveryIsSingleOwnedTaskAndAcceptanceWaitsForItsSuccessfulCompletion()
    {
        using var fixture = new Fixture();
        var started = Signal();
        var finish = new TaskCompletionSource<SourceOnlyRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new LifetimeCoordinator
        {
            Recover = async token =>
            {
                started.TrySetResult();
                return await finish.Task.WaitAsync(token);
            },
        };
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(
            fixture.Services, _ => Task.FromResult(coordinator.Session()));
        SourceOnlyProjectCreationResolution resolution = fixture.Resolution();
        Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() => Accept(runtime, resolution));
        Task<SourceOnlyRecoveryResult> first = runtime.RecoverAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(first, runtime.RecoverAsync());
        Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() => Accept(runtime, resolution));
        Assert.AreEqual(0, coordinator.Acceptances);
        Assert.AreEqual(1, coordinator.Recoveries);

        finish.TrySetResult(new([], false, null));
        Assert.IsNull((await first).FailureCode);
        Assert.ThrowsExactly<NotSupportedException>(() => Accept(runtime, resolution));
        Assert.AreEqual(1, coordinator.Acceptances, "The test coordinator intentionally refuses instead of creating fake production success.");
        Assert.AreSame(first, runtime.RecoverAsync());
    }

    [TestMethod]
    public async Task FailedRecoveryNeverEnablesAcceptanceAndItsErrorDoesNotEscape()
    {
        using var fixture = new Fixture();
        var coordinator = new LifetimeCoordinator
        {
            Recover = _ => Task.FromException<SourceOnlyRecoveryResult>(new IOException(@"C:\private\recovery")),
        };
        await using SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(
            fixture.Services, _ => Task.FromResult(coordinator.Session()));
        Assert.AreEqual("source_only.recovery_failed", (await runtime.RecoverAsync()).FailureCode);
        Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() => Accept(runtime, fixture.Resolution()));
        Assert.AreEqual(0, coordinator.Acceptances);
    }

    [TestMethod]
    public async Task CanceledInitializationDisposesALateAcquiredSessionAndDoesNotReturnUnavailable()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var coordinator = new LifetimeCoordinator();
        Task<SourceOnlyApplicationRuntime> creation = SourceOnlyApplicationRuntime.CreateAsync(fixture.Services, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return coordinator.Session(); // Simulate an initializer returning just after cancellation.
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        release.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => creation);
        Assert.AreEqual(1, coordinator.Disposals);
        Assert.AreEqual(0, coordinator.Recoveries);
        Assert.IsEmpty(fixture.Services.Drafts.ListDrafts());
    }

    [TestMethod]
    public async Task PreCanceledInitializationDoesNotInvokeTheFactory()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => SourceOnlyApplicationRuntime.CreateAsync(fixture.Services,
            _ => { calls++; return Task.FromResult(new LifetimeCoordinator().Session()); }, cancellation.Token));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task DisposeCancelsAndAwaitsCaptureBeforeDisposingTheSessionExactlyOnce()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var canceled = Signal();
        var cleanup = Signal();
        var coordinator = new LifetimeCoordinator
        {
            Capture = async token =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    await cleanup.Task;
                    throw;
                }
            },
        };
        SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(
            fixture.Services, _ => Task.FromResult(coordinator.Session()));
        Task selection = runtime.CaptureSelectionAsync(EntityId<ProjectCreationDraftIdTag>.New(),
            EntityId<AssetIdTag>.New(), fixture.SourcePath, Actor, At, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task first = runtime.DisposeAsync().AsTask();
        Task second = runtime.DisposeAsync().AsTask();
        Assert.AreSame(first, second);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(first.IsCompleted);
        Assert.AreEqual(0, coordinator.Disposals);
        Assert.ThrowsExactly<ObjectDisposedException>(() => runtime.RecoverAsync());
        cleanup.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<OperationCanceledException>(() => selection);
        Assert.AreEqual(1, coordinator.Disposals);
    }

    [TestMethod]
    public async Task DisposeObservesRecoveryCancellationAndPrivateDisposalFailures()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var coordinator = new LifetimeCoordinator
        {
            Recover = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new([], false, null);
            },
            Dispose = () => new ValueTask(Task.FromException(new IOException(@"C:\private\shutdown"))),
        };
        SourceOnlyApplicationRuntime runtime = await SourceOnlyApplicationRuntime.CreateAsync(
            fixture.Services, _ => Task.FromResult(coordinator.Session()));
        Task<SourceOnlyRecoveryResult> recovery = runtime.RecoverAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await recovery).Canceled);
        Assert.AreEqual("source_only.shutdown_failed", runtime.ShutdownFailureCode);
        Assert.AreEqual(1, coordinator.Disposals);
        Assert.IsNull(runtime.ReadyPipeline);
    }

    [TestMethod]
    public async Task ShutdownAwaitsLateCreationAndCleanupWithoutBlockingTheCaller()
    {
        var creation = new TaskCompletionSource<LifetimeCoordinator>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = Signal();
        var release = Signal();
        var coordinator = new LifetimeCoordinator
        {
            Dispose = () =>
            {
                disposing.TrySetResult();
                return new ValueTask(release.Task);
            },
        };
        int cancels = 0;
        Task<string?> shutdown = SourceOnlyApplicationShutdown.StopAsync(creation.Task, () =>
        {
            cancels++;
            return Task.CompletedTask;
        });
        Assert.AreEqual(1, cancels);
        Assert.IsFalse(shutdown.IsCompleted);
        creation.TrySetResult(coordinator);
        await disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(shutdown.IsCompleted);
        release.TrySetResult();
        Assert.IsNull(await shutdown.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, coordinator.Disposals);
    }

    [TestMethod]
    public async Task ShutdownObservesCancellationCallbackAndCreationFailuresWithoutExposingPaths()
    {
        string? result = await SourceOnlyApplicationShutdown.StopAsync(
            Task.FromException<LifetimeCoordinator>(new IOException(@"C:\private\startup")),
            () => Task.FromException(new IOException(@"C:\private\callback")));
        Assert.AreEqual("source_only.shutdown_failed", result);
    }

    [TestMethod]
    public void WorkerTrustAnchorMatchesCommittedPackagingEvidenceAndRejectsSelfReportedSets()
    {
        byte[] source = File.ReadAllBytes(RepositoryPath("packaging/python/runtime-manifest.json"));
        CollectionAssert.AreEqual(source, SourceOnlyResourceManifest.ReadEmbedded(SourceOnlyResourceManifest.PythonManifestResource),
            "Compile the shipping runtime input as the build contract; never duplicate its changing output set in C# constants.");
        using JsonDocument manifest = JsonDocument.Parse(source);
        JsonElement python = manifest.RootElement.GetProperty("runtime").GetProperty("selectedSignedFiles").EnumerateArray()
            .Single(file => file.GetProperty("name").GetString() == "python.exe");
        Assert.AreEqual(SourceOnlyApplicationResourceLease.PythonSha256, python.GetProperty("sha256").GetString());
        Assert.AreEqual(SourceOnlyApplicationResourceLease.PythonByteSize, python.GetProperty("byteSize").GetInt64());
        SourceOnlyApplicationResourceLease.WorkerFile[] unapproved =
        [new("python.exe", 3, ContentDigest.Compute([1, 2, 3]).Hex)];
        Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() => SourceOnlyApplicationResourceLease.ValidateWorkerManifest(unapproved));
        Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() => SourceOnlyApplicationResourceLease.ValidateWorkerManifest([]));
        Assert.ThrowsExactly<SourceOnlyApplicationDependencyException>(() => SourceOnlyApplicationResourceLease.ValidateWorkerManifest(
            [new("../python.exe", 3, ContentDigest.Compute([1, 2, 3]).Hex)]));
    }

    [TestMethod]
    public async Task ActualImportAndSensitivityScannerPopulateTheSameRuntimesAndReplayIsIdempotent()
    {
        using var fixture = new Fixture();
        (Project project, TranscriptImportResult import) = CreateImport();
        SourceOnlyApplicationRegistrations registrations = fixture.Services.Registrations;
        SourceOnlyRuntimeCallbacks callbacks = registrations.Callbacks;
        await callbacks.TranscriptReady!(project, import, CancellationToken.None);
        TranscriptBridgeSnapshot original = registrations.Transcript.GetRequired(project.Id);
        Assert.AreSame(import.Revision, original.Revision);
        Assert.AreEqual(TranscriptChangeSource.Machine, original.Revision.ChangeSource);
        Assert.AreEqual(import.TimingEvidence.Count, original.TimingEvidence.Count);
        Assert.AreEqual(import.TimingEvidence[0].RawStartSeconds, original.TimingEvidence[0].RawStartSeconds);
        await callbacks.TranscriptReady(project, import, CancellationToken.None);
        Assert.AreSame(original, registrations.Transcript.GetRequired(project.Id));

        TopicBoundaryScanResult boundaries = TopicBoundaryService.FindCandidates(import.Revision, import.SourceSpans, EntityId<ModelRunIdTag>.New());
        var evidence = new SourceOnlyAnalysisEvidence(At, boundaries, [], [], [], [], []);
        await callbacks.AnalysisReady!(project, import, evidence, CancellationToken.None);
        AnalysisBridgeSnapshot analysis = registrations.Analysis.GetRequired(project.Id);
        TextSensitivityScanResult actual = TextSensitivityScanner.Scan(import.Revision, []);
        Assert.AreEqual(actual.IsComplete, analysis.Sensitivity.IsComplete);
        Assert.AreEqual(actual.Findings.Count, analysis.Sensitivity.Findings.Count);
        Assert.IsTrue(analysis.Sensitivity.Findings.Any(finding => finding.Kind == TextSensitivityFindingKind.EmailAddress));
        await callbacks.AnalysisReady(project, import, evidence, CancellationToken.None);
        Assert.AreSame(analysis, registrations.Analysis.GetRequired(project.Id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => registrations.LayoutSubtitles.GetRequired(project.Id));
        Assert.IsEmpty(fixture.Services.Jobs.ListJobs(JobState.Queued));
    }

    [TestMethod]
    public void LegacyDeletionNeverBindsAPhraseToAnEntireContainingVideoElement()
    {
        var (_, import) = CreateImport();
        SourceSpan span = import.SourceSpans.Single();
        var video = new CompositionElement(EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, new RationalTime(20, 1)), OriginRef.FromSourceSpan(span.Id),
            sourceRange: new TimeRange(default, new RationalTime(20, 1)));
        Assert.IsFalse(SourceOnlyApplicationRegistrations.CanBindWholeElementDeletion(video, span));
        var exact = new CompositionElement(EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, span.Range.Duration), OriginRef.FromSourceSpan(span.Id), sourceRange: span.Range);
        Assert.IsTrue(SourceOnlyApplicationRegistrations.CanBindWholeElementDeletion(exact, span));
    }

    private static (Project Project, TranscriptImportResult Import) CreateImport()
    {
        var timeline = new AnalysisAudioTimeline(EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(),
            EntityId<AssetIdTag>.New(), ContentDigest.Compute([1, 2, 3]), 100, EntityId<ArtifactIdTag>.New(),
            ContentDigest.Compute([4, 5, 6]), 0, 1, new RationalTime(1, 48000), default,
            0, 1024, EntityId<ArtifactIdTag>.New(), ContentDigest.Compute([7, 8, 9]), 640044, 320000, EntityId<ArtifactIdTag>.New());
        var worker = new AsrWorkerResult("Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120",
            new ContentDigest("4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"),
            new AsrWorkerConfiguration("cpu", "int8", "ja", "transcribe", 5, true, false, true),
            timeline.AnalysisAudioDigest, timeline.AnalysisAudioByteSize, 20, "ja", 0.9, 20,
            [new AsrWorkerSegment(0, 0, "support@example.invalid", 2, 4, -0.1, 0.01,
                [new AsrWorkerToken("support@example.invalid", 2, 4, 0.9, false)])]);
        TranscriptImportResult import = TranscriptImporter.Import(worker, timeline,
            EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), At);
        var source = new Asset(timeline.SourceAssetId, "source.mov", "video/quicktime", timeline.SourceContentDigest,
            timeline.SourceByteSize, new RightsRecord(RightsStatus.Unconfirmed, null, "Synthetic import fixture; no production permission.", false));
        var project = new Project(timeline.ProjectId, "test", "test", "test", PublicationScope.InternalOnly, null, [source], []);
        return (project, import);
    }

    private static void Accept(SourceOnlyApplicationRuntime runtime, SourceOnlyProjectCreationResolution resolution) =>
        runtime.Accept(resolution.Draft, resolution.SettingsSnapshot, SourceOnlyProjectCreationContract.Serialize(resolution), 0, Actor, At);

    private static JsonElement Input(ProjectCreationDraftView draft) => JsonSerializer.SerializeToElement(new
    {
        sourceCandidateId = draft.SourceCandidateId,
        contentName = draft.ContentName,
        purpose = draft.Purpose,
        audience = draft.Audience,
        publicationScope = draft.PublicationScope,
        outputProfileIds = draft.OutputProfileIds,
        language = draft.Language,
        performerNames = draft.PerformerNames,
        deadlineSelection = new { hasDeadline = draft.HasDeadline, value = draft.Deadline },
        brandTemplateId = draft.BrandTemplateId,
    });

    private static BridgeRequest Request(BridgeMethod method, object parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonSerializer.SerializeToElement(parameters));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx"))) { directory = directory.Parent; }
        return Path.Combine((directory ?? throw new DirectoryNotFoundException("Repository fixtures unavailable.")).FullName,
            relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "HubRadio.SourceOnlyApplicationRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string database = Path.Combine(Root, "hubradio.db");
            _ = DatabaseMigrator.Migrate(database, Path.Combine(Root, "backups"));
            Settings = new DefaultSettingsStore(database);
            _ = new BundledDefaultSettingsBootstrap(Settings).Initialize(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles"), At);
            Services = new(database, Root, Registry, Settings);
            Catalog = ProjectCreationCatalog.Load(RepositoryPath("src/HubRadio.App/Profiles"));
            Authorization = new AuthorizationService(new RoleAssignmentStore(database));
            _ = Authorization.BootstrapInitialRoles(Actor);
            SourcePath = Path.Combine(Root, "source.mov");
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4]);
        }

        internal string Root { get; }
        internal string PackageRoot => Path.Combine(Root, "absent-package");
        internal string SourcePath { get; }
        internal AssetCandidateRegistry Registry { get; } = new();
        internal DefaultSettingsStore Settings { get; }
        internal SourceOnlyApplicationServices Services { get; }
        internal ProjectCreationCatalog Catalog { get; }
        internal AuthorizationService Authorization { get; }
        internal SourceOnlyApplicationResources PackageResources() => SourceOnlyApplicationResources.FromPackage(PackageRoot,
            Path.Combine(PackageRoot, "Profiles"), Path.Combine(PackageRoot, "DefaultSettings", "Profiles"));
        internal SourceOnlyProjectCreationResolution Resolution() => SourceOnlyProjectCreationContract.Resolve(
            EntityId<ProjectCreationDraftIdTag>.New(), EntityId<AssetIdTag>.New(), SourcePath, Settings.GetGlobalSettings(), Catalog);
        internal ProjectCreationService Service(ISourceOnlyProjectCoordinator coordinator) => new(Services.Drafts,
            Authorization, Registry, Catalog, Settings, () => Actor,
            token => { token.ThrowIfCancellationRequested(); return Task.FromResult(Registry.RegisterSelection(SourcePath)); },
            new FixedClock(), coordinator);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed class LifetimeCoordinator : ISourceOnlyProjectCoordinator, IAsyncDisposable
    {
        internal int Recoveries;
        internal int Acceptances;
        internal int Disposals;
        internal Func<CancellationToken, Task>? Capture { get; init; }
        internal Func<CancellationToken, Task<SourceOnlyRecoveryResult>> Recover { get; init; } = _ => Task.FromResult(new SourceOnlyRecoveryResult([], false, null));
        internal Func<ValueTask> Dispose { get; init; } = () => ValueTask.CompletedTask;

        internal SourceOnlyApplicationSession Session() => new(this, this, token =>
        {
            Interlocked.Increment(ref Recoveries);
            return Recover(token);
        });

        public Task CaptureSelectionAsync(EntityId<ProjectCreationDraftIdTag> draftId, EntityId<AssetIdTag> candidateId,
            string path, string actorSid, DateTimeOffset selectedAt, CancellationToken cancellationToken) =>
            Capture?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public void ForgetSelection(EntityId<ProjectCreationDraftIdTag> draftId) { }
        public StoredProjectCreationDraft Accept(ProjectCreationDraft draft, DefaultSettingsSnapshot settings,
            string payloadJson, int expectedRevision, string actorSid, DateTimeOffset acceptedAt)
        {
            Interlocked.Increment(ref Acceptances);
            throw new NotSupportedException("This lifecycle seam never accepts production work.");
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            return Dispose();
        }
    }
}