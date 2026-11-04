using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.Jobs;
using HubRadio.App.Models;
using HubRadio.App.Policy;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Diagnostics;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Projects;

internal enum SourceOnlyApplicationDependency
{
    Ffmpeg,
    Ffprobe,
    Worker,
    Model,
    Font,
    ApplicationEvidence,
    Initialization,
}

/// <summary>Never carries a selected path, an exception message, or an inner exception.</summary>
internal sealed class SourceOnlyApplicationDependencyException(
    SourceOnlyApplicationDependency dependency) : InvalidOperationException(
        "The native source-only dependencies are unavailable or have not passed verification.")
{
    internal SourceOnlyApplicationDependency Dependency { get; } = dependency;
}

/// <summary>
/// Explicit trusted HOST inputs for development/packaging, never renderer JSON, PATH,
/// environment variables, or a search of the repository. Vendor digests are pinned
/// by embedded packaging inputs; application digests identify a build, not approval.
/// </summary>
internal sealed record SourceOnlyApplicationResources(
    string FfmpegPath,
    string FfprobePath,
    string WorkerDirectory,
    string ModelCatalogPath,
    string ModelBundleRootPath,
    string ProfilesDirectory,
    string DefaultSettingsProfilePath,
    string MusicDirectory,
    string ApplicationVersion,
    FfmpegFontBinding? Font = null,
    BinaryVerificationRequest? ApplicationVerification = null,
    string? PackageRoot = null)
{
    internal static SourceOnlyApplicationResources FromPackage(
        string baseDirectory,
        string profilesDirectory,
        string defaultSettingsProfilesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        // prepare-payload.ps1 copies the entire embedded runtime directly to Worker,
        // not Worker/Python. ModelManager resolves Bundles/... relative to ModelCatalog.
        return new(
            Path.Combine(root, "Tools", "ffmpeg.exe"),
            Path.Combine(root, "Tools", "ffprobe.exe"),
            Path.Combine(root, "Worker"),
            Path.Combine(root, "ModelCatalog", "catalog.json"),
            Path.Combine(root, "ModelCatalog"),
            profilesDirectory,
            Path.Combine(defaultSettingsProfilesDirectory, "1.3.0.json"),
            Path.Combine(root, "Music"),
            typeof(SourceOnlyApplicationRuntime).Assembly.GetName().Version!.ToString(), PackageRoot: root);
        // Deliberately path-only: read Fonts/font-manifest.json and build evidence
        // inside ResolveAsync. Missing/invalid resources disable Start, not settings.
    }
}

/// <summary>One native composition root; all participants receive these exact objects.</summary>
internal sealed class SourceOnlyApplicationServices
{
    internal SourceOnlyApplicationServices(
        string databasePath,
        string workspacePath,
        AssetCandidateRegistry candidates,
        DefaultSettingsStore settings)
    {
        DatabasePath = SourceOnlySourceLocator.RequirePath(databasePath, directory: false);
        WorkspacePath = SourceOnlySourceLocator.RequirePath(workspacePath, directory: true);
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Jobs = new JobStore(
            databasePath,
            JobHistoryLog.ForWorkspace(
                WorkspacePath,
                typeof(SourceOnlyApplicationServices).Assembly.GetName().Version?.ToString()));
        Coordinator = new JobCoordinator(Jobs, new LocalJobRunner(Jobs));
        Drafts = new ProjectCreationDraftStore(databasePath);
        Projects = new ProjectStore(databasePath);
        Executions = new SourceOnlyExecutionStore(databasePath);
        Artifacts = new ArtifactStore(databasePath, workspacePath);
        Evaluations = new AutomatedEvaluationStore(databasePath);
        Workflows = new QuickStartWorkflowStore(databasePath);
        Results = new SourceOnlyResultService(Artifacts, Evaluations, Workflows, Projects);
        SubtitleEdits = new DurableSubtitleEditService(new SubtitleEditStore(databasePath));
        Registrations = new SourceOnlyApplicationRegistrations(new TermStore(databasePath), SubtitleEdits);
    }

    internal string DatabasePath { get; }
    internal string WorkspacePath { get; }
    internal AssetCandidateRegistry Candidates { get; }
    internal DefaultSettingsStore Settings { get; }
    internal JobStore Jobs { get; }
    internal JobCoordinator Coordinator { get; }
    internal ProjectCreationDraftStore Drafts { get; }
    internal ProjectStore Projects { get; }
    internal SourceOnlyExecutionStore Executions { get; }
    internal ArtifactStore Artifacts { get; }
    internal AutomatedEvaluationStore Evaluations { get; }
    internal QuickStartWorkflowStore Workflows { get; }
    internal SourceOnlyResultService Results { get; }
    internal DurableSubtitleEditService SubtitleEdits { get; }
    internal SourceOnlyApplicationRegistrations Registrations { get; }
}

/// <summary>
/// Owns the pipeline AND its verified resource handles. The internal delegate constructor
/// is a lifecycle-test seam, not a production evaluator/delivery fallback.
/// </summary>
internal sealed class SourceOnlyApplicationSession : IAsyncDisposable
{
    private readonly IAsyncDisposable owner;
    private readonly IAsyncDisposable? resources;
    private readonly Func<CancellationToken, Task<SourceOnlyRecoveryResult>> recover;

    internal SourceOnlyApplicationSession(SourceOnlyPipeline pipeline, IAsyncDisposable resources)
        : this(pipeline, pipeline, pipeline.RecoverAsync, resources)
    {
        Pipeline = pipeline;
    }

    internal SourceOnlyApplicationSession(
        ISourceOnlyProjectCoordinator coordinator,
        IAsyncDisposable owner,
        Func<CancellationToken, Task<SourceOnlyRecoveryResult>> recover,
        IAsyncDisposable? resources = null)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.recover = recover ?? throw new ArgumentNullException(nameof(recover));
        this.resources = resources;
    }

    internal SourceOnlyPipeline? Pipeline { get; }
    internal ISourceOnlyProjectCoordinator Coordinator { get; }
    internal Task<SourceOnlyRecoveryResult> RecoverAsync(CancellationToken token) => recover(token);

    public async ValueTask DisposeAsync()
    {
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (resources is not null) { await resources.DisposeAsync().ConfigureAwait(false); }
        }
    }
}

/// <summary>
/// Host-owned resource initialization, acceptance and lifetime. A missing native dependency
/// leaves the settings/draft UI usable but cannot take the legacy finalize-only branch.
/// Construction never processes content; recovery is explicitly invoked AFTER WebView Ready.
/// </summary>
internal sealed class SourceOnlyApplicationRuntime : ISourceOnlyProjectCoordinator, IAsyncDisposable
{
    internal const string DependenciesUnavailableCode = "source_only.runtime_dependencies_unavailable";
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SourceOnlySourceLocator locator;
    private readonly SemaphoreSlim repairGate = new(1, 1);
    private readonly Func<CancellationToken, Task<SourceOnlyApplicationSession>>? initialize;
    private SourceOnlyApplicationSession? session;
    private readonly HashSet<Task> operations = [];
    private Task<SourceOnlyRecoveryResult>? recoveryTask;
    private Task? disposalTask;
    private bool readyForAcceptance;
    private bool disposed;

    private SourceOnlyApplicationRuntime(SourceOnlyApplicationServices services,
        SourceOnlyApplicationSession? session, SourceOnlyApplicationDependency? missingDependency,
        Func<CancellationToken, Task<SourceOnlyApplicationSession>>? initialize = null)
    {
        this.initialize = initialize;
        Services = services;
        this.session = session;
        MissingDependency = missingDependency;
        locator = new SourceOnlySourceLocator(services.WorkspacePath);
    }

    internal SourceOnlyApplicationServices Services { get; }
    internal SourceOnlyPipeline? Pipeline => session?.Pipeline;
    internal SourceOnlyPipeline? ReadyPipeline
    {
        get { lock (gate) { return !disposed && readyForAcceptance ? session?.Pipeline : null; } }
    }
    internal bool IsAvailable => session is not null;
    internal SourceOnlyApplicationDependency? MissingDependency { get; private set; }
    internal string? ShutdownFailureCode { get; private set; }

    internal static Task<SourceOnlyApplicationRuntime> CreateAsync(
        SourceOnlyApplicationServices services,
        SourceOnlyApplicationResources resources,
        CancellationToken cancellationToken = default) =>
        CreateAsync(services, token => CreateProductionSessionAsync(services, resources, token), cancellationToken);

    internal static async Task<SourceOnlyApplicationRuntime> CreateAsync(
        SourceOnlyApplicationServices services,
        Func<CancellationToken, Task<SourceOnlyApplicationSession>> initialize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(initialize);
        cancellationToken.ThrowIfCancellationRequested();
        SourceOnlyApplicationSession? acquired = null;
        try
        {
            // Hashing, ModelManager.Load, and bundle copying must not occupy the UI
            // dispatcher. Initialization is cancelable and the creation task is owned.
            acquired = await Task.Run(() => initialize(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (acquired is null) { throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Initialization); }
            return new(services, acquired, null, initialize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (acquired is not null) { await acquired.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (acquired is not null) { await acquired.DisposeAsync().ConfigureAwait(false); }
            return new(services, null, exception is SourceOnlyApplicationDependencyException dependency
                ? dependency.Dependency : SourceOnlyApplicationDependency.Initialization, initialize);
        }
    }

    private static async Task<SourceOnlyApplicationSession> CreateProductionSessionAsync(
        SourceOnlyApplicationServices services, SourceOnlyApplicationResources resources, CancellationToken token)
    {
        var held = new SourceOnlyApplicationResourceLease();
        try
        {
            SourceOnlyRuntimeOptions options = await held.ResolveAsync(services, resources, token).ConfigureAwait(false);
            var qc = new OutputQc(services.Artifacts, new ProcessRunner(), new BinaryVerifier(), options.FfprobePath, options.FfmpegPath);
            var evaluation = new SourceOnlyOutputEvaluation(services.Artifacts, qc, services.Evaluations, services.Workflows, services.Projects);
            var delivery = new SourceOnlyDeliveryService(services.DatabasePath, services.WorkspacePath,
                services.Artifacts, services.Results, services.Workflows);
            // Integration contract for the pipeline owner: both production overloads
            // must accept jobStore/jobCoordinator and retain these exact instances.
            // Do not revert to internally constructed, merely same-database objects.
            var pipeline = new SourceOnlyPipeline(options, services.Candidates, evaluation,
                services.Registrations.Callbacks, deliverAsync: delivery.DeliverAsync,
                jobStore: services.Jobs, jobCoordinator: services.Coordinator);
            return new SourceOnlyApplicationSession(pipeline, held);
        }
        catch
        {
            await held.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task<SourceOnlyRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            // A failed dependency check must not settle, retry or accept any job.
            if (session is null) { return Task.FromResult(new SourceOnlyRecoveryResult([], false, DependenciesUnavailableCode)); }
            return recoveryTask ??= Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                try
                {
                    SourceOnlyRecoveryResult result = await session.RecoverAsync(linked.Token).ConfigureAwait(false);
                    lock (gate) { readyForAcceptance = !disposed && !result.Canceled && result.FailureCode is null; }
                    return result;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { return new([], true, null); }
                catch (Exception) { return new([], false, "source_only.recovery_failed"); }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Self-repair for a runtime that is not ready: re-runs the verified initialization when it failed
    /// (a missing or locked file may have been fixed since startup) and re-runs recovery when it did not
    /// finish successfully. Returns true only when acceptance is now enabled. Never exposes paths.
    /// </summary>
    internal async Task<bool> RepairAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (readyForAcceptance) { return true; }
        }
        await repairGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            bool needsSession;
            lock (gate) { ThrowIfDisposed(); needsSession = session is null; }
            if (needsSession)
            {
                if (initialize is null) { return false; }
                SourceOnlyApplicationSession? created = null;
                try
                {
                    created = await Task.Run(() => initialize(linked.Token), linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (created is null) { throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Initialization); }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    if (created is not null) { await created.DisposeAsync().ConfigureAwait(false); }
                    throw;
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    if (created is not null) { await created.DisposeAsync().ConfigureAwait(false); }
                    lock (gate)
                    {
                        MissingDependency = exception is SourceOnlyApplicationDependencyException dependency
                            ? dependency.Dependency : SourceOnlyApplicationDependency.Initialization;
                    }
                    return false;
                }
                bool discard;
                lock (gate)
                {
                    discard = disposed;
                    if (!discard) { session = created; MissingDependency = null; recoveryTask = null; }
                }
                if (discard)
                {
                    await created.DisposeAsync().ConfigureAwait(false);
                    return false;
                }
            }
            lock (gate)
            {
                if (readyForAcceptance) { return true; }
                // A pending recovery is awaited as is; a finished unsuccessful one is replaced.
                if (recoveryTask is { IsCompleted: true }) { recoveryTask = null; }
            }
            _ = await RecoverAsync(CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
            lock (gate) { return readyForAcceptance; }
        }
        finally { _ = repairGate.Release(); }
    }

    public Task CaptureSelectionAsync(EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> candidateId, string path, string actorSid, DateTimeOffset selectedAt,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            Task selection = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                if (session is null)
                {
                    await locator.CaptureAsync(draftId, candidateId, path, actorSid, selectedAt, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    await session.Coordinator.CaptureSelectionAsync(draftId, candidateId, path, actorSid, selectedAt, linked.Token).ConfigureAwait(false);
                }
                if (linked.IsCancellationRequested)
                {
                    locator.ForgetUnacceptedSelection(draftId);
                    linked.Token.ThrowIfCancellationRequested();
                }
            }, CancellationToken.None);
            operations.Add(selection);
            _ = ObserveOperationAsync(selection);
            return selection;
        }
    }

    Task<bool> ISourceOnlyProjectCoordinator.EnsurePosterAsync(EntityId<ProjectCreationDraftIdTag> draftId,
        CancellationToken cancellationToken) =>
        // FR-053: without a ready pipeline there is no verified ffmpeg; the list then shows "サムネイルなし".
        RunTracked(coordinator => coordinator.EnsurePosterAsync(draftId, cancellationToken), false);

    Task<SourceMediaFacts?> ISourceOnlyProjectCoordinator.ProbeSourceAsync(string sourcePath, CancellationToken cancellationToken) =>
        RunTracked(coordinator => coordinator.ProbeSourceAsync(sourcePath, cancellationToken), null);

    /// <summary>
    /// Takes only the session under the gate, runs the work on the thread pool outside it, and
    /// registers it so disposal waits for it, like a captured selection.
    /// </summary>
    private Task<T> RunTracked<T>(Func<ISourceOnlyProjectCoordinator, Task<T>> work, T unavailable)
    {
        lock (gate)
        {
            if (session is null || lifetime.IsCancellationRequested)
            {
                return Task.FromResult(unavailable);
            }

            ISourceOnlyProjectCoordinator coordinator = session.Coordinator;
            Task<T> operation = Task.Run(() => work(coordinator), CancellationToken.None);
            operations.Add(operation);
            _ = ObserveOperationAsync(operation);
            return operation;
        }
    }

    private async Task ObserveOperationAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception) { /* The requesting service receives the original exception. */ }
        finally { lock (gate) { operations.Remove(operation); } }
    }

    internal Task RestoreAfterReadAsync(EntityId<ProjectIdTag> projectId, CancellationToken token)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            SourceOnlyPipeline? pipeline = ReadyPipeline;
            if (pipeline is null || Services.Executions.GetLatestForProject(projectId) is null)
            {
                return Task.CompletedTask;
            }
            Task restore = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                await pipeline.RestoreRuntimeAsync(projectId, linked.Token).ConfigureAwait(false);
            }, CancellationToken.None);
            operations.Add(restore);
            _ = ObserveOperationAsync(restore);
            return restore;
        }
    }

    public void ForgetSelection(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        lock (gate)
        {
            // The requesting service may unwind AFTER close canceled its capture.
            // Cleanup remains allowed, but must never remove an accepted draft's locator.
            if (Services.Drafts.GetDraft(draftId)?.IsFinalized != true)
            {
                locator.ForgetUnacceptedSelection(draftId);
            }
        }
    }

    public bool RestoreSelection(EntityId<ProjectCreationDraftIdTag> draftId, EntityId<AssetIdTag> candidateId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (session is not null) { return session.Coordinator.RestoreSelection(draftId, candidateId); }
            StoredProjectCreationDraft? draft = Services.Drafts.GetDraft(draftId);
            if (draft is null || draft.IsFinalized || !SourceOnlyProjectCreationContract.IsSourceOnlyPayload(draft.CanonicalJson)) { return false; }
            using JsonDocument payload = JsonDocument.Parse(draft.CanonicalJson);
            if (payload.RootElement.GetProperty("sourceCandidateId").GetString() != candidateId.ToString()) { return false; }
            try
            {
                SourceOnlySourceLocator.LocatedSource selected = locator.ReadSelection(draftId, candidateId);
                // This legacy synchronous selection API uses only ConfigureAwait(false)
                // inside the locator. No dispatcher callback is awaited here.
                using FileStream source = locator.OpenVerifiedAsync(selected, CancellationToken.None).GetAwaiter().GetResult();
                return Services.Candidates.RestoreVerifiedSelectionForDraft(candidateId, selected.Path);
            }
            catch (SourceOnlySourceException) { return false; }
        }
    }

    public StoredProjectCreationDraft Accept(ProjectCreationDraft draft, DefaultSettingsSnapshot settings,
        string payloadJson, int expectedRevision, string actorSid, DateTimeOffset acceptedAt)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (session is not null && !readyForAcceptance)
            {
                throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Initialization);
            }
            return (session?.Coordinator ?? throw new SourceOnlyApplicationDependencyException(
                MissingDependency ?? SourceOnlyApplicationDependency.Initialization))
                .Accept(draft, settings, payloadJson, expectedRevision, actorSid, acceptedAt);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposalTask is not null) { return new(disposalTask); }
            disposed = true;
            disposalTask = Task.Run(DisposeCoreAsync);
            return new(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { ShutdownFailureCode = "source_only.shutdown_failed"; }
            Task[] pendingOperations;
            lock (gate) { pendingOperations = operations.ToArray(); }
            try { await Task.WhenAll(pendingOperations).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { ShutdownFailureCode = "source_only.shutdown_failed"; }
            // Pipeline disposal cancels owned stages; no UI dispatch occurs in callbacks.
            try { if (session is not null) { await session.DisposeAsync().ConfigureAwait(false); } }
            catch (Exception) { ShutdownFailureCode = "source_only.shutdown_failed"; }
            if (recoveryTask is not null) { _ = await recoveryTask.ConfigureAwait(false); }
        }
        catch (Exception) { ShutdownFailureCode = "source_only.shutdown_failed"; }
        finally { lifetime.Dispose(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

/// <summary>
/// Holds the resource trees while they can execute. Worker output is bound to the
/// packaging input embedded by THIS App build, including locked dependency evidence.
/// Self-extraction need not preserve ReadOnly; held handles and exact hashes matter.
/// </summary>
internal sealed class SourceOnlyApplicationResourceLease : IAsyncDisposable
{
    internal const string PythonSha256 = "4d6f5f81a4bca11191c4c7c6b43632694d0a4ce74e068619d8fdc161d469859a";
    internal const long PythonByteSize = 104952;
    private readonly List<FileStream> held = [];
    private readonly Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly BinaryVerifier verifier = new();

    internal async Task<SourceOnlyRuntimeOptions> ResolveAsync(SourceOnlyApplicationServices services,
        SourceOnlyApplicationResources resources, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        token.ThrowIfCancellationRequested();
        RequireFile(resources.FfmpegPath, SourceOnlyApplicationDependency.Ffmpeg);
        RequireFile(resources.FfprobePath, SourceOnlyApplicationDependency.Ffprobe);
        RequireFile(Path.Combine(resources.WorkerDirectory, "python.exe"), SourceOnlyApplicationDependency.Worker);
        RequireFile(resources.ModelCatalogPath, SourceOnlyApplicationDependency.Model);
        FfmpegFontBinding font = await ResolveFontAsync(resources, token).ConfigureAwait(false);
        BinaryVerificationRequest application = await ResolveApplicationBuildAsync(resources, token).ConfigureAwait(false);
        await VerifyAsync(new("source-only.ffmpeg", resources.FfmpegPath,
            new ContentDigest(DecodeValidator.CanonicalFfmpegSha256), DecodeValidator.CanonicalFfmpegByteSize),
            SourceOnlyApplicationDependency.Ffmpeg, token).ConfigureAwait(false);
        await VerifyAsync(new("source-only.ffprobe", resources.FfprobePath,
            new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize),
            SourceOnlyApplicationDependency.Ffprobe, token).ConfigureAwait(false);
        var python = new BinaryVerificationRequest("source-only.python", Path.Combine(resources.WorkerDirectory, "python.exe"),
            new ContentDigest(PythonSha256), PythonByteSize);
        await VerifyAsync(python, SourceOnlyApplicationDependency.Worker, token).ConfigureAwait(false);
        await VerifyWorkerAsync(resources.WorkerDirectory, token).ConfigureAwait(false);
        ModelManager manager;
        string modelDirectory;
        try
        {
            manager = ModelManager.Load(resources.ModelCatalogPath, resources.ModelBundleRootPath, services.WorkspacePath, verifier);
            // Offline only; ready caches are reverified and corrupt caches are not replaced.
            modelDirectory = await manager.InstallFromBundleAsync(ModelManager.AdoptedAsrModelId, token).ConfigureAwait(false);
            modelDirectory = await manager.RequireOfflinePathAsync(ModelManager.AdoptedAsrModelId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Model); }
        return new SourceOnlyRuntimeOptions(services.DatabasePath, services.WorkspacePath, resources.FfmpegPath,
            resources.FfprobePath, python.FilePath, modelDirectory, resources.ProfilesDirectory, resources.DefaultSettingsProfilePath,
            font, python, manager, resources.ApplicationVersion, application.ExpectedDigest, musicDirectory: resources.MusicDirectory);
    }

    private static void RequireFile(string path, SourceOnlyApplicationDependency dependency)
    {
        try { _ = SourceOnlySourceLocator.RequirePath(path, directory: false); }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(dependency); }
    }

    internal async Task<BinaryVerificationRequest> ResolveApplicationBuildAsync(SourceOnlyApplicationResources resources, CancellationToken token)
    {
        const SourceOnlyApplicationDependency dependency = SourceOnlyApplicationDependency.ApplicationEvidence;
        try
        {
            token.ThrowIfCancellationRequested();
            BinaryVerificationRequest request = resources.ApplicationVerification ?? SourceOnlyResourceManifest.ReadApplicationBuild(
                ReadHeldManifest(Path.Combine(resources.PackageRoot ?? throw new InvalidDataException(),
                    SourceOnlyResourceManifest.ApplicationBuildPath.Replace('/', Path.DirectorySeparatorChar))), resources.PackageRoot!, resources.ApplicationVersion);
            await VerifyAsync(request, dependency, token).ConfigureAwait(false);
            if (resources.ApplicationVerification is null &&
                System.Reflection.AssemblyName.GetAssemblyName(request.FilePath).Version?.ToString() != resources.ApplicationVersion)
            {
                throw new InvalidDataException();
            }
            return request;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(dependency); }
    }

    internal async Task<FfmpegFontBinding> ResolveFontAsync(SourceOnlyApplicationResources resources, CancellationToken token)
    {
        const SourceOnlyApplicationDependency dependency = SourceOnlyApplicationDependency.Font;
        try
        {
            token.ThrowIfCancellationRequested();
            if (resources.Font is FfmpegFontBinding supplied)
            {
                await VerifyAsync(supplied.VerificationRequest, dependency, token).ConfigureAwait(false);
                return supplied;
            }
            string root = resources.PackageRoot ?? throw new InvalidDataException();
            SourceOnlyResourceManifest.FontContract font = SourceOnlyResourceManifest.ReadFont(
                ReadHeldManifest(Path.Combine(root, SourceOnlyResourceManifest.FontManifestPath.Replace('/', Path.DirectorySeparatorChar))),
                SourceOnlyResourceManifest.ReadEmbedded(SourceOnlyResourceManifest.FontManifestResource), root);
            FileStream stream = await VerifyAsync(font.Binding.VerificationRequest, dependency, token).ConfigureAwait(false);
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            SourceOnlyResourceManifest.ValidateFontNames(bytes);
            await VerifyAsync(font.License, dependency, token).ConfigureAwait(false);
            ValidateTree(Path.Combine(root, "Fonts"), [SourceOnlyResourceManifest.FontFileName, "LICENSE", "font-manifest.json"], token);
            return font.Binding;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(dependency); }
    }

    private async Task<FileStream> VerifyAsync(BinaryVerificationRequest request, SourceOnlyApplicationDependency dependency, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            HoldDirectories(Path.GetDirectoryName(request.FilePath)!);
            FileStream stream = SourceOnlyResourceManifest.OpenFile(request.FilePath);
            held.Add(stream);
            if (stream.Length != request.ExpectedByteSize || ContentDigest.FromBytes(
                await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != request.ExpectedDigest ||
                stream.Length != request.ExpectedByteSize) { throw new InvalidDataException(); }
            _ = SourceOnlySourceLocator.RequirePath(request.FilePath, directory: false);
            stream.Position = 0;
            return stream;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(dependency); }
    }

    internal async Task VerifyWorkerAsync(string directory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            directory = SourceOnlySourceLocator.RequirePath(directory, directory: true);
            string manifestPath = Path.Combine(directory, "runtime-files.json");
            WorkerFile[] records = SourceOnlyResourceManifest.ReadWorker(ReadHeldManifest(manifestPath),
                SourceOnlyResourceManifest.ReadEmbedded(SourceOnlyResourceManifest.PythonManifestResource));
            string[] names = records.Select(item => item.Path).Append("runtime-files.json").ToArray();
            ValidateTree(directory, names, token);
            foreach (WorkerFile file in records)
            {
                await VerifyAsync(new BinaryVerificationRequest("source-only.worker-file",
                    Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)),
                    new ContentDigest(file.Sha256), file.ByteSize), SourceOnlyApplicationDependency.Worker, token).ConfigureAwait(false);
            }
            // Recheck the tree after holding every verified file.
            ValidateTree(directory, names, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Worker); }
    }

    internal static void ValidateWorkerManifest(IReadOnlyList<WorkerFile> records)
    {
        try
        {
            using JsonDocument source = SourceOnlyResourceManifest.Parse(
                SourceOnlyResourceManifest.ReadEmbedded(SourceOnlyResourceManifest.PythonManifestResource));
            SourceOnlyResourceManifest.ValidateWorkerRecords(records, source.RootElement.GetProperty("output"));
        }
        catch (Exception) { throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Worker); }
    }

    private byte[] ReadHeldManifest(string path)
    {
        HoldDirectories(Path.GetDirectoryName(path)!);
        FileStream stream = SourceOnlyResourceManifest.OpenFile(path);
        held.Add(stream);
        if (stream.Length is <= 0 or > SourceOnlyResourceManifest.MaximumManifestBytes) { throw new InvalidDataException(); }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        _ = SourceOnlySourceLocator.RequirePath(path, directory: false);
        return bytes;
    }

    private void HoldDirectories(string directory)
    {
        var chain = new Stack<string>();
        for (string? path = directory; path is not null; path = Path.GetDirectoryName(path)) { chain.Push(path); }
        foreach (string path in chain)
        {
            if (!directories.ContainsKey(path)) { directories.Add(path, SourceOnlyResourceManifest.OpenDirectory(path)); }
        }
    }

    internal void ValidateTree(string directory, IReadOnlyList<string> names, CancellationToken token)
    {
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            token.ThrowIfCancellationRequested();
            HoldDirectories(current);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) { throw new InvalidDataException(); }
                if (attributes.HasFlag(FileAttributes.Directory)) { pending.Push(entry); continue; }
                // Extraction changes attributes, not the approved byte inventory.
                if (!remaining.Remove(Path.GetRelativePath(directory, entry).Replace('\\', '/'))) { throw new InvalidDataException(); }
            }
        }
        if (remaining.Count != 0) { throw new InvalidDataException(); }
    }

    public async ValueTask DisposeAsync()
    {
        bool failed = false;
        foreach (FileStream stream in held)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { failed = true; }
        }
        held.Clear();
        foreach (var handle in directories.Values.Reverse())
        {
            try { handle.Dispose(); }
            catch (Exception) { failed = true; }
        }
        directories.Clear();
        if (failed) { throw new SourceOnlyApplicationDependencyException(SourceOnlyApplicationDependency.Initialization); }
    }

    internal sealed record WorkerFile(string Path, long ByteSize, string Sha256);
}

/// <summary>
/// Editor projections of the pipeline's durable stage checkpoints. Production subtitle
/// edits use the injected journal; the TermStore-only constructor remains session-only.
/// Restoring checkpoints calls these SAME hooks and never starts content processing.
/// Manual layout/terop editing is available only through the legacy session runtime.
/// </summary>
internal sealed class SourceOnlyApplicationRegistrations : ILayoutSubtitleBridgeRuntime
{
    private readonly object gate = new();
    private readonly TermStore terms;
    private readonly Dictionary<EntityId<ProjectIdTag>, ContentDigest> transcriptKeys = [];
    private readonly Dictionary<EntityId<ProjectIdTag>, TranscriptBridgeSnapshot> machineTranscripts = [];
    private readonly Dictionary<EntityId<ProjectIdTag>, AnalysisInput> analysisInputs = [];
    private readonly Dictionary<EntityId<ProjectIdTag>, StandardCompositionPreparation> compositions = [];
    private readonly Dictionary<(EntityId<ProjectIdTag> Project, EntityId<CompositionVersionIdTag> Version), ContentDigest> generatedKeys = [];

    internal SourceOnlyApplicationRegistrations(TermStore terms)
        : this(terms, null)
    {
    }

    internal SourceOnlyApplicationRegistrations(TermStore terms, DurableSubtitleEditService? durableSubtitles)
    {
        this.terms = terms ?? throw new ArgumentNullException(nameof(terms));
        DurableSubtitles = durableSubtitles;
        Callbacks = new SourceOnlyRuntimeCallbacks
        {
            TranscriptReady = RegisterTranscriptAsync,
            AnalysisReady = RegisterAnalysisAsync,
            CompositionReady = RegisterCompositionAsync,
            GeneratedReady = RegisterGeneratedAsync,
        };
    }

    internal TranscriptBridgeRuntime Transcript { get; } = new();
    internal AnalysisBridgeRuntime Analysis { get; } = new();
    internal LayoutSubtitleBridgeRuntime LayoutSubtitles { get; } = new();
    internal DurableSubtitleEditService? DurableSubtitles { get; }
    internal SourceOnlyRuntimeCallbacks Callbacks { get; }

    /// <summary>Trusted coordinator inputs only; no capture or root selection is exposed through web JSON.</summary>
    internal IReadOnlyList<SubtitleEditCapture> CaptureSubtitles(EntityId<ProjectIdTag> projectId,
        IReadOnlyList<EntityId<CompositionVersionIdTag>> rootVersionIds)
    {
        lock (gate)
        {
            return (DurableSubtitles ?? throw new InvalidOperationException(
                "Session-only subtitle registrations cannot supply durable captures.")).Capture(projectId, rootVersionIds);
        }
    }

    private Task RegisterTranscriptAsync(Project project, TranscriptImportResult import, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (import.AudioTimeline.ProjectId != project.Id || import.Revision.ChangeSource != TranscriptChangeSource.Machine ||
            !project.Assets.Any(asset => asset.Id == import.AudioTimeline.SourceAssetId &&
                asset.ContentDigest == import.AudioTimeline.SourceContentDigest && asset.ByteSize == import.AudioTimeline.SourceByteSize))
        {
            throw new InvalidOperationException("The native transcript registration does not match its source.");
        }
        TermScope scope = TermScope.ForProject(project.Id);
        IReadOnlyList<TermEntry> dictionary = terms.ListTermEntries(scope);
        ContentDigest key = StandardRenderGraphFactory.Digest(new
        {
            revision = TranscriptSnapshotJson.WriteRevision(import.Revision),
            dictionary,
        });
        lock (gate)
        {
            if (transcriptKeys.GetValueOrDefault(project.Id) == key) { return Task.CompletedTask; }
            // Create actually invokes TermCandidateService.FindCandidates against the
            // current dictionary. This ID identifies that real scan, not the ASR run.
            TranscriptBridgeSnapshot snapshot = TranscriptBridgeSnapshot.Create(project, import.Revision,
                import.SourceSpans, import.TimingEvidence, dictionary, scope, EntityId<ModelRunIdTag>.New(), speakerHistory: null);
            token.ThrowIfCancellationRequested();
            Transcript.Register(snapshot);
            machineTranscripts[project.Id] = snapshot;
            transcriptKeys[project.Id] = key;
        }
        return Task.CompletedTask;
    }

    private Task RegisterAnalysisAsync(Project project, TranscriptImportResult import, SourceOnlyAnalysisEvidence evidence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            TranscriptBridgeSnapshot transcript = machineTranscripts[project.Id];
            if (transcript.Revision.Id != import.Revision.Id ||
                evidence.TopicBoundaries.SourceRevisionId != import.Revision.Id ||
                evidence.TopicBoundaries.SourceRevisionDigest != transcript.RevisionDigest)
            {
                throw new InvalidOperationException("The native analysis registration does not match its transcript.");
            }
            ContentDigest key = StandardRenderGraphFactory.Digest(evidence);
            if (analysisInputs.TryGetValue(project.Id, out AnalysisInput? previous) &&
                previous.Key == key && ReferenceEquals(previous.Transcript, transcript)) { return Task.CompletedTask; }
            // Do not construct an empty 'complete' sensitivity result in lieu of a scan.
            TextSensitivityScanResult sensitivity = TextSensitivityScanner.Scan(import.Revision, transcript.DictionaryEntries);
            var input = new AnalysisInput(project, import, evidence, transcript, sensitivity, key);
            token.ThrowIfCancellationRequested();
            Analysis.Register(BuildAnalysis(input, null, token));
            analysisInputs[project.Id] = input;
            // A changed analysis/dictionary must be rebound even when its eventual
            // composition identity is unchanged. Identical replay above preserves edits.
            compositions.Remove(project.Id);
        }
        return Task.CompletedTask;
    }

    private Task RegisterCompositionAsync(StandardCompositionPreparation preparation, TranscriptImportResult import, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            EntityId<ProjectIdTag> projectId = preparation.Child.ProjectId;
            if (preparation.Plan.LongForm.Provenance.TranscriptRevisionId != import.Revision.Id ||
                preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest != machineTranscripts[projectId].RevisionDigest)
            {
                throw new InvalidOperationException("The native composition registration does not match its transcript.");
            }
            if (compositions.TryGetValue(projectId, out StandardCompositionPreparation? prior) &&
                prior.Child.Id == preparation.Child.Id && prior.Child.ContentDigest == preparation.Child.ContentDigest) { return Task.CompletedTask; }
            AnalysisBridgeSnapshot analysis = BuildAnalysis(analysisInputs[projectId], preparation, token);
            token.ThrowIfCancellationRequested();
            Analysis.Register(analysis);
            // Cue histories do not exist yet. Only GeneratedReady may publish a
            // subtitle workspace; neither an empty short nor a new cue is a substitute.
            compositions[projectId] = preparation;
        }
        return Task.CompletedTask;
    }

    private Task RegisterGeneratedAsync(SourceOnlyGeneratedRuntimeEvidence evidence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            StandardCompositionPreparation preparation = evidence.Preparation;
            EntityId<ProjectIdTag> projectId = preparation.Child.ProjectId;
            if (!compositions.TryGetValue(projectId, out StandardCompositionPreparation? registered) ||
                registered.ApplicationDigest != preparation.ApplicationDigest || registered.Plan.PlanDigest != preparation.Plan.PlanDigest ||
                CompositionSnapshotJson.WriteVersion(registered.Child) != CompositionSnapshotJson.WriteVersion(preparation.Child) ||
                !machineTranscripts.TryGetValue(projectId, out TranscriptBridgeSnapshot? transcript) ||
                evidence.Import.AudioTimeline.ProjectId != projectId || evidence.Import.Revision.ChangeSource != TranscriptChangeSource.Machine ||
                !preparation.Plan.LongForm.Project.Assets.Any(asset => asset.Id == evidence.Import.AudioTimeline.SourceAssetId &&
                    asset.ContentDigest == evidence.Import.AudioTimeline.SourceContentDigest && asset.ByteSize == evidence.Import.AudioTimeline.SourceByteSize) ||
                evidence.Import.Revision.Id != transcript.Revision.Id || preparation.Plan.LongForm.Provenance.TranscriptRevisionId != transcript.Revision.Id ||
                ContentDigest.Compute(Encoding.UTF8.GetBytes(TranscriptSnapshotJson.WriteRevision(evidence.Import.Revision))) != transcript.RevisionDigest ||
                preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest != transcript.RevisionDigest ||
                evidence.CreatedAt < preparation.Child.CreatedAt || evidence.CreatedAt < evidence.Import.Revision.CreatedAt)
            {
                throw new InvalidOperationException("The generated subtitles do not match the registered composition and transcript.");
            }

            var expected = new Dictionary<EntityId<CompositionVersionIdTag>, StandardShortFormPlan?> { [preparation.Child.Id] = null };
            foreach (StandardShortFormPlan shortForm in preparation.Plan.ShortForms)
            {
                expected.Add(StandardRenderGraphFactory.CreateShortVersion(preparation, shortForm.Id).Id, shortForm);
            }
            if (evidence.Versions.Count != expected.Count || evidence.Versions.Keys.Any(id => !expected.ContainsKey(id)) ||
                evidence.ShortForms.Count != preparation.Plan.ShortForms.Count ||
                evidence.ShortForms.Select(item => item.ShortFormId).Distinct().Count() != evidence.ShortForms.Count ||
                evidence.ShortForms.Any(item => !preparation.Plan.ShortForms.Any(plan => plan.Id == item.ShortFormId)))
            {
                throw new InvalidOperationException("The generated subtitles must contain the landscape and every exact short version.");
            }

            // Validate the entire set before publishing any member. The cache key
            // includes actual revision IDs, timestamps and profiles, not just the
            // composition's element digest (which multiple versions may share).
            var pending = new List<(LayoutSubtitleBridgeSnapshot Snapshot, ContentDigest Key)>();
            foreach (var (versionId, shortForm) in expected)
            {
                token.ThrowIfCancellationRequested();
                SourceOnlyGeneratedSubtitleRuntimeEvidence item = evidence.Versions[versionId];
                ValidateGeneratedVersion(evidence, item, shortForm);
                ContentDigest key = GeneratedKey(evidence, item);
                if (generatedKeys.TryGetValue((projectId, versionId), out ContentDigest? previous))
                {
                    if (previous != key) { throw new InvalidOperationException("A generated subtitle revision cannot be replaced with different evidence."); }
                }
                pending.Add((CreateLayout(evidence, item, shortForm), key));
            }
            token.ThrowIfCancellationRequested();
            foreach (var (snapshot, key) in pending)
            {
                // The pipeline checkpoints every actual root (including shorts) before
                // this callback. Let the service validate storage and replay EVERY time,
                // even for a cached key; registration must not reset a committed child.
                DurableSubtitles?.RegisterGenerated(snapshot, key, token,
                    makeDefault: snapshot.CompositionVersion.Id == preparation.Child.Id,
                    projection: evidence.Versions[snapshot.CompositionVersion.Id].SubtitleProjection);
                // This independent legacy projection remains explicitly session-only.
                // Its idempotent Register preserves any existing manual layout draft.
                LayoutSubtitles.Register(snapshot, makeDefault: snapshot.CompositionVersion.Id == preparation.Child.Id);
                generatedKeys[(projectId, snapshot.CompositionVersion.Id)] = key;
            }
        }
        return Task.CompletedTask;
    }

    // Keep the established full builder key unchanged, including for legacy v1
    // checkpoints. A projection proof is separately validated, never a key substitute.
    internal static ContentDigest GeneratedKey(SourceOnlyGeneratedRuntimeEvidence evidence,
        SourceOnlyGeneratedSubtitleRuntimeEvidence item) => StandardRenderGraphFactory.Digest(new
        {
            evidence.Preparation.ApplicationDigest,
            evidence.Preparation.Plan.PlanDigest,
            evidence.CreatedAt,
            evidence.Font,
            version = CompositionSnapshotJson.WriteVersion(item.Version),
            item.Timeline,
            item.Layout,
            item.Profile,
            item.Build,
            item.Histories,
        });

    private static void ValidateGeneratedVersion(SourceOnlyGeneratedRuntimeEvidence evidence,
        SourceOnlyGeneratedSubtitleRuntimeEvidence item, StandardShortFormPlan? shortForm)
    {
        StandardCompositionPreparation preparation = evidence.Preparation;
        CompositionVersion version = shortForm is null ? preparation.Child
            : StandardRenderGraphFactory.CreateShortVersion(preparation, shortForm.Id);
        LayoutSpec layout = StandardRenderGraphFactory.GetLayout(preparation.Plan, shortForm?.Id);
        RenderProfile profile = StandardRenderGraphFactory.GetProfile(preparation.Plan, shortForm is not null, shortForm?.Id);
        StandardSubtitleInput input = shortForm is null ? evidence.LandscapeSubtitles.Subtitles
            : evidence.ShortForms.Single(candidate => candidate.ShortFormId == shortForm.Id).Subtitles;
        if (CompositionSnapshotJson.WriteVersion(item.Version) != CompositionSnapshotJson.WriteVersion(version) ||
            item.Timeline.CompositionVersionId != version.Id ||
            !TimelineMapBuilder.Build(item.Timeline.Id, version).Entries.SequenceEqual(item.Timeline.Entries) ||
            StandardRenderGraphFactory.Digest(item.Layout) != StandardRenderGraphFactory.Digest(layout) ||
            StandardRenderGraphFactory.Digest(item.Profile) != StandardRenderGraphFactory.Digest(profile) ||
            StandardRenderGraphFactory.Digest(item.Build.Subtitles) != StandardRenderGraphFactory.Digest(input) ||
            (shortForm is null && StandardRenderGraphFactory.Digest(item.Build) != StandardRenderGraphFactory.Digest(evidence.LandscapeSubtitles)) ||
            item.Build.SettingsDigest != StandardRenderGraphFactory.InputDigest(preparation.Plan, StandardPlanInputKind.SettingsSnapshot) ||
            input.AssProfile.PlayResolutionWidth != profile.Width || input.AssProfile.PlayResolutionHeight != profile.Height ||
            item.Histories.Count != input.Revisions.Count || item.Histories.Select(history => history.CueId).Distinct().Count() != item.Histories.Count)
        {
            throw new InvalidOperationException("The generated subtitle version, timeline, layout or profile binding is invalid.");
        }
        item.SubtitleProjection?.ValidateOutput(item);
        foreach (HubRadio.App.Subtitles.SubtitleCueHistory history in item.Histories)
        {
            if (item.SubtitleProjection is { } projection)
            {
                SubtitleService.ValidateMaterializedInitial(item.Version, history, projection);
            }
            var revision = history.CurrentRevision;
            var candidate = input.Candidates.SingleOrDefault(candidate => candidate.Id == history.SourceCandidateId);
            var built = input.Revisions.SingleOrDefault(cue => cue.Id == revision.Id);
            if (history.Revisions.Count != 1 || history.ProjectId != version.ProjectId ||
                revision.CompositionVersionId != version.Id || revision.CompositionVersionDigest != version.ContentDigest ||
                revision.ParentCompositionVersionId != version.ParentVersionId || revision.CreatedAt != evidence.CreatedAt ||
                revision.SourceRevisionId != evidence.Import.Revision.Id ||
                revision.SourceRevisionDigest != preparation.Plan.LongForm.Provenance.TranscriptRevisionDigest ||
                revision.Snapshot.LayoutResolution.LayoutSpecId != layout.Id ||
                revision.Snapshot.LayoutResolution.ProfileVersionDigest != input.DisplayProfile.VersionDigest ||
                candidate is null || built is null ||
                StandardRenderGraphFactory.Digest(history.SourceCandidate) != StandardRenderGraphFactory.Digest(candidate) ||
                StandardRenderGraphFactory.Digest(revision) != StandardRenderGraphFactory.Digest(built))
            {
                throw new InvalidOperationException("The editor requires the actual initial generated cue history, not a reconstructed substitute.");
            }
        }
    }

    private static AnalysisBridgeSnapshot BuildAnalysis(AnalysisInput input, StandardCompositionPreparation? preparation, CancellationToken token)
    {
        SourceOnlyAnalysisEvidence evidence = input.Evidence;
        var topics = evidence.TopicRanges.OrderBy(range => range.Range.Start).Select(range => AnalysisTopicBinding.Create(
            input.Project, input.Import.Revision, input.Import.SourceSpans, null, range,
            evidence.TopicMetadata.Single(metadata => metadata.TopicRangeRevisionId == range.Id))).ToArray();
        var bindings = new List<AnalysisCandidateBinding>();
        foreach (TermCandidate term in input.Transcript.TermCandidates)
        {
            token.ThrowIfCancellationRequested();
            bindings.Add(AnalysisCandidateBinding.ForTerm(SuggestionService.Register(term, []),
                input.Import.SourceSpans.Single(span => span.Id == term.SourceSpanId), input.Import.Revision, input.Import.SourceSpans,
                input.Transcript.DictionaryEntries.Single(entry => entry.Id == term.TermEntryId && entry.Version == term.TermEntryVersion)));
        }
        if (preparation is not null)
        {
            CompositionElement? FindElement(SourceSpan source) => preparation.Child.Elements.SingleOrDefault(element =>
                element.SourceRange is TimeRange range && range.Start <= source.Range.Start && range.End >= source.Range.End &&
                preparation.Plan.LongForm.Provenance.SourceSpans.Any(span => span.Id == element.Origin.SourceSpanId &&
                    span.AssetId == source.AssetId && span.SourceDigest == source.SourceDigest));
            foreach (EditorialPhraseSuggestionCandidate candidate in evidence.EditorialCandidates)
            {
                token.ThrowIfCancellationRequested();
                if (FindElement(candidate.Payload.SourceSpan) is CompositionElement element &&
                    CanBindWholeElementDeletion(element, candidate.Payload.SourceSpan))
                {
                    // The legacy adoption API emits EditOperation.Delete(elementId),
                    // not a partial-range cut. Never offer the entire video for a phrase.
                    bindings.Add(AnalysisCandidateBinding.ForDelete(candidate, candidate.Payload.SourceSpan, candidate.SourceDigest, element.Id));
                }
            }
            foreach (TopicBoundaryCandidate boundary in evidence.TopicBoundaries.Candidates)
            {
                token.ThrowIfCancellationRequested();
                if (FindElement(boundary.SourceSpan) is CompositionElement element && element.SourceRange is TimeRange range &&
                    boundary.SuggestedBoundary > range.Start && boundary.SuggestedBoundary < range.End)
                {
                    TopicBoundarySuggestionCandidate candidate = SuggestionService.Register(boundary, []);
                    bindings.Add(AnalysisCandidateBinding.ForTopicBoundary(candidate, boundary.SourceSpan, candidate.SourceDigest,
                        element.Id, boundary.SuggestedBoundary - range.Start,
                        StableId<CompositionElementIdTag>(preparation.Child.Id.ToString(), candidate.Id.ToString())));
                }
            }
            foreach (EmphasisSuggestionCandidate candidate in evidence.EmphasisCandidates)
            {
                token.ThrowIfCancellationRequested();
                SourceSpan span = candidate.Payload.SourceSpan;
                if (FindElement(span) is CompositionElement element && element.SourceRange is TimeRange range)
                {
                    bindings.Add(AnalysisCandidateBinding.ForHighlight(candidate, span, candidate.SourceDigest,
                        StableId<CueIdTag>(preparation.Child.Id.ToString(), candidate.Id.ToString()),
                        new TimeRange(element.OutputRange.Start + span.Range.Start - range.Start, span.Range.Duration), regionName: null));
                }
            }
        }
        return AnalysisBridgeSnapshot.Create(StableId<AnalysisBridgeRevisionIdTag>(input.Key.Hex,
            preparation?.Child.Id.ToString() ?? "analysis"), evidence.CreatedAt, input.Project, input.Import.Revision,
            input.Import.SourceSpans, null, topics, bindings, [], input.Sensitivity);
    }

    internal static bool CanBindWholeElementDeletion(CompositionElement element, SourceSpan candidate) =>
        element.SourceRange == candidate.Range && element.OutputRange.Duration == candidate.Range.Duration;

    internal static LayoutSubtitleBridgeSnapshot CreateLayout(SourceOnlyGeneratedRuntimeEvidence evidence,
        SourceOnlyGeneratedSubtitleRuntimeEvidence item, StandardShortFormPlan? shortForm)
    {
        // This is exactly StandardSubtitleBuilder's generated layout context. Keep
        // its real histories; restoring the context must not mint Cue IDs or dates.
        var profile = new HubRadio.Core.Subtitles.SubtitleLayoutProfile(item.Layout.Id,
            item.Build.Subtitles.DisplayProfile.VersionDigest, item.Layout.SafeZones.Select(zone => zone.Name).ToArray(),
            Enum.GetValues<HubRadio.Core.Subtitles.SubtitleSoftConstraintKind>()
                .Select(kind => new HubRadio.Core.Subtitles.SubtitleSoftConstraintWeight(kind, 0)).ToArray());
        LayoutSubtitleCueBinding[] subtitles = item.Histories.Select(history =>
            new LayoutSubtitleCueBinding(history, item.Layout, profile, [], [])).ToArray();
        LayoutSubtitleDisplayBinding[] displays = [new(item.Build.Subtitles.DisplayProfile, [])];
        StandardCompositionPreparation preparation = evidence.Preparation;
        if (shortForm is null)
        {
            // Use the measured source geometry captured by the real orchestrator,
            // even when there were no shorts. The output dimensions are not the source.
            using JsonDocument document = JsonDocument.Parse(preparation.Child.Evidence!.CanonicalJson);
            JsonElement captured = document.RootElement.GetProperty("input").GetProperty("sourceDisplayGeometry");
            var geometry = new SourceDisplayGeometry(StandardSourceGeometry.ReadMathematicalInt32(captured.GetProperty("width")),
                StandardSourceGeometry.ReadMathematicalInt32(captured.GetProperty("height")));
            StandardRenderGraphFactory.ValidateCapturedGeometry(preparation, geometry);
            EntityId<AssetIdTag> assetId = evidence.Import.AudioTimeline.SourceAssetId;
            return new LayoutSubtitleBridgeSnapshot(item.Version.ProjectId, 0, item.Version, item.Layout,
                item.Profile.Width, item.Profile.Height, [new(assetId, OriginRef.FromAsset(assetId), geometry)],
                [preparation.Plan.LongForm.Profile.TemplateReference], subtitles, [], displays);
            // No fabricated vertical manual selection; this is a subtitle-only 16:9 workspace.
        }
        ManualLayoutPlan plan = shortForm.Layout;
        LayoutSpec layout = plan.DerivedLayoutSpec;
        try { _ = LayoutResolver.Resolve(layout, item.Profile.Width, item.Profile.Height); }
        catch (InvalidOperationException)
        {
            // E.g. a 16:9 foreground can have fractional destination pixels inside
            // 1080x1920. Keep the real plan unchanged and explicitly expose only
            // the subtitle projection; never round geometry or disable all cues.
            return new LayoutSubtitleBridgeSnapshot(item.Version.ProjectId, 0, item.Version, item.Layout,
                item.Profile.Width, item.Profile.Height,
                plan.SourcePlacements.DistinctBy(placement => placement.SourceAssetId).Select(placement =>
                    new LayoutSubtitleSourceBinding(placement.SourceAssetId, placement.Origin, placement.SourceDisplayGeometry)).ToArray(),
                [preparation.Plan.ShortFormProfile.TemplateReference], subtitles, [], displays, subtitleOnly: true);
        }
        var envelope = new LayoutSpec(StableId<LayoutSpecIdTag>(layout.Id.ToString(), "editor-envelope"),
            layout.OutputAspectWidth, layout.OutputAspectHeight,
            layout.Regions.Where(region => region.Role is LayoutRegionRole.Subtitle or LayoutRegionRole.Graphic).ToArray(), layout.SafeZones);
        ManualLayoutSource[] sources = plan.SourcePlacements.Select(placement => new ManualLayoutSource(
            placement.SourceAssetId, placement.Origin, placement.SourceDisplayGeometry, placement.Crop, placement.SubjectRegion)).ToArray();
        ManualDestinationGeometry[] destinations = plan.SourcePlacements.Select(placement => new ManualDestinationGeometry(placement.Destination.Region.Rect)).ToArray();
        const ManualStaticCropDecision decision = ManualStaticCropDecision.SingleStaticCropSufficient;
        ManualLayoutRequest request = plan.Mode switch
        {
            ManualLayoutMode.SingleSubjectCrop => ManualLayoutRequest.SingleSubjectCrop(layout.Id, envelope, decision, sources[0], destinations[0]),
            ManualLayoutMode.TwoSpeakerStack => ManualLayoutRequest.TwoSpeakerStack(layout.Id, envelope, decision, sources[0], destinations[0], sources[1], destinations[1]),
            ManualLayoutMode.SpeakerMaterialStack => ManualLayoutRequest.SpeakerMaterialStack(layout.Id, envelope, decision, sources[0], destinations[0], sources[1], destinations[1]),
            ManualLayoutMode.MaterialWithSpeakerInset => ManualLayoutRequest.MaterialWithSpeakerInset(layout.Id, envelope, decision, sources[0], destinations[0], sources[1], destinations[1]),
            ManualLayoutMode.ImportantRegionZoom => ManualLayoutRequest.ImportantRegionZoom(layout.Id, envelope, decision, sources[0], destinations[0]),
            ManualLayoutMode.BlurredSourceBackground => ManualLayoutRequest.BlurredSourceBackground(layout.Id, envelope, decision, sources[0], destinations[0], sources[1], destinations[1]),
            ManualLayoutMode.FixedTemplatePlacement => ManualLayoutRequest.FixedTemplatePlacement(layout.Id, envelope, decision, sources[0], destinations[0], plan.TemplateReference!),
            _ => throw new InvalidOperationException("The generated layout mode is not supported."),
        };
        ManualLayoutResolution resolution = ManualLayoutResolver.Resolve(request);
        if (!plan.Equals(resolution.Plan)) { throw new InvalidOperationException("The generated layout could not be restored exactly."); }
        return new LayoutSubtitleBridgeSnapshot(preparation.Child.ProjectId, 0,
            item.Version, envelope, item.Profile.Width, item.Profile.Height,
            sources.DistinctBy(source => source.SourceAssetId).Select(source => new LayoutSubtitleSourceBinding(
                source.SourceAssetId, source.Origin, source.DisplayGeometry)).ToArray(),
            [preparation.Plan.ShortFormProfile.TemplateReference], subtitles, [], displays,
            new LayoutSubtitleManualSelection(resolution, sources, destinations, plan.TemplateReference));
    }

    public LayoutSubtitleBridgeOperationResult Execute(BridgeRequest request, string actorSid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (DurableSubtitles is null) { return LayoutSubtitles.Execute(request, actorSid, cancellationToken); }

            // Production supports generated subtitles only. The service enforces
            // pins/replay/commit-before-ACK and rejects other mutations. Never fall
            // back to a stale session draft after a durable write (or a failed write).
            // evaluationTime belongs to legacy keyframe reads, not this projection.
            LayoutSubtitleBridgeOperationResult result = DurableSubtitles.Execute(request, actorSid, cancellationToken);
            if (result.Snapshot is not { SubtitleOnly: false } snapshot) { return result; }

            // UI capability projection only: registration, journal digests and trusted
            // captures retain the complete actual native layout. Do not advertise
            // manual editing. Preserve the service's subtitle-only persistence receipt;
            // this wrapper must not manufacture one or strip a committed ACK's proof.
            LayoutSpec layout = snapshot.ManualSelection?.Resolution.Plan?.DerivedLayoutSpec ?? snapshot.OutputEnvelope;
            return LayoutSubtitleBridgeOperationResult.Available(new LayoutSubtitleBridgeSnapshot(
                snapshot.ProjectId, snapshot.Revision, snapshot.CompositionVersion, layout,
                snapshot.OutputPixelWidth, snapshot.OutputPixelHeight, snapshot.Sources, snapshot.Templates,
                snapshot.Subtitles, snapshot.Terops, snapshot.SubtitleDisplays, variantPair: snapshot.VariantPair,
                compositionVersions: snapshot.CompositionVersions, subtitleOnly: true), result.DurableSubtitles);
        }
    }

    private static EntityId<T> StableId<T>(string input, string role) => EntityId<T>.From(
        new Guid(Convert.FromHexString(StandardRenderGraphFactory.Digest(new { input, role }).Hex)[..16]));

    private sealed record AnalysisInput(Project Project, TranscriptImportResult Import, SourceOnlyAnalysisEvidence Evidence,
        TranscriptBridgeSnapshot Transcript, TextSensitivityScanResult Sensitivity, ContentDigest Key);
}

/// <summary>Await late initialization and cleanup without a dispatcher wait or an async-void close handler.</summary>
internal static class SourceOnlyApplicationShutdown
{
    internal static async Task<string?> CloseAfterStopAsync(Task<string?> shutdown, Action close)
    {
        // Always return from AppWindow.Closing before a programmatic Close, including
        // when disposal was synchronous. Keep the caller's UI context for Close only.
        await Task.Yield();
        string? failure;
        try { failure = await shutdown; }
        catch (Exception) { failure = "source_only.shutdown_failed"; }
        try { close(); }
        catch (Exception) { failure = "source_only.window_close_failed"; }
        return failure;
    }

    internal static async Task<string?> StopAsync<T>(Task<T>? creation, Func<Task> cancel)
        where T : IAsyncDisposable
    {
        string? failure = null;
        try { await cancel().ConfigureAwait(false); }
        catch (Exception) { failure = "source_only.shutdown_failed"; }
        if (creation is not null)
        {
            T owned;
            try
            {
                owned = await creation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return failure; }
            catch (Exception) { return "source_only.shutdown_failed"; }
            try { await owned.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { failure = "source_only.shutdown_failed"; }
        }
        return failure;
    }
}