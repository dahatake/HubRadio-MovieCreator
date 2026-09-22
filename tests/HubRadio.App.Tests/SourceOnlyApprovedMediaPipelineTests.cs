using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Models;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Security;
using HubRadio.App.Transcripts;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Identity;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Security;

namespace HubRadio.App.Tests;

/// <summary>
/// Opt-in acceptance run of the source-only pipeline over the approved external media of
/// requirement-definition.md section 18, not native GUI acceptance (UX041/043).
/// It records the six measurement groups that section 18 demands; a passing run establishes
/// those particular measurements only, never ASR accuracy, perceived quality, human/editorial
/// approval, external publication permission, clean-OS install or release approval.
/// No WebView, network acquisition, user profile, default application directory or production DB.
/// The run root is deliberately retained, including on failure; cleanup must be explicit later.
/// Run ONE method in a dedicated testhost with an external process-tree watchdog, exactly as
/// SourceOnlyRealPipelineTests requires, and never reuse that host.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SourceOnlyApprovedMediaPipelineTests
{
    // Budgets are ESTIMATES scaled from the synthetic four-second scenario, not measurements of
    // this medium. SourceOnlyRealPipelineTests uses 120s per process, 150s preparation, 300s
    // execution and 450s overall for a 4.000s input. The approved medium is 60.093243s
    // (benchmarks/results/initial-pilot.md section 1.3), a factor of 15.02. Decode, ASR and
    // encode scale roughly with duration, so the duration-dependent budgets are scaled by 15 and
    // rounded up for margin.
    // PreparationTimeout is NOT an estimate scaled from duration. A first run on 2026-09-22
    // exceeded 300s inside SourceOnlyApplicationResourceLease.VerifyWorkerAsync, which opens,
    // holds and hashes every one of the 2,697 worker-runtime files (268,991,381 bytes) and walks
    // the tree twice. Preparation is dominated by that duration-independent cost, so the budget
    // is raised to 1800s and the milestones below record where the time is actually spent.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(1800);
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(1800);
    private static readonly TimeSpan ScenarioExecutionTimeout = TimeSpan.FromSeconds(5400);
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(9000);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static int scenarioClaimed;
    private const string ModelId = "asr.faster-whisper-small.cpu-int8";
    private const string ModelManifest = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f";
    private const string FontSha256 = "dff723ba59d57d136764a04b9b2d03205544f7cd785a711442d6d2d085ac5073";
    // requirement-definition.md section 18 (approved 2026-09-21) and benchmarks/results/initial-pilot.md section 1.2.
    private const string ApprovedMediaSha256 = "07904c1dd4c482fa495ea8cd3572c98fff4eaf66b41cf89e396e661f3b8c3e9c";
    private const long ApprovedMediaByteSize = 9_971_886;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("LivePipeline")]
    [TestCategory("ApprovedMedia")]
    public async Task ApprovedMediaSourceOnlyPipeline_GeneratesTheStandardSet_AndRecordsTheAcceptanceMeasurements()
    {
        // Every guard precedes path resolution, directory/DB creation and any process launch.
        if (Environment.GetEnvironmentVariable("HUBRADIO_RUN_LIVE_PIPELINE_TESTS") != "1")
        {
            Assert.Inconclusive("approved_media.opt_in_required.HUBRADIO_RUN_LIVE_PIPELINE_TESTS");
            return;
        }
        string? mediaPath = Environment.GetEnvironmentVariable("HUBRADIO_APPROVED_MEDIA_PATH");
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            Assert.Inconclusive("approved_media.opt_in_required.HUBRADIO_APPROVED_MEDIA_PATH");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("approved_media.windows_acl_required");
            return;
        }

        // A caller attestation, not discovery of the runner's filter. The external runner must
        // select exactly this method, start no native app, and kill the process tree on failure.
        Check(!string.IsNullOrWhiteSpace(TestContext.TestName) &&
            Environment.GetEnvironmentVariable("HUBRADIO_LIVE_PIPELINE_SINGLE_TEST") == TestContext.TestName,
            "approved_media.dedicated_single_test_host_required");
        Check(Interlocked.CompareExchange(ref scenarioClaimed, 1, 0) == 0, "approved_media.testhost_already_used");
        Check(Path.IsPathFullyQualified(mediaPath), "approved_media.path_not_absolute");

        AcceptanceFixture? fixture = null;
        ProcessEnvironmentScope? environment = null;
        string? failure = null;
        var scenarioElapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs>? firstChance = null;
        try
        {
            fixture = new AcceptanceFixture(TestContext, mediaPath);
            // The pipeline converts an unclassified rendering exception into the opaque
            // source_only.reconciliation_required gate, which alone cannot tell an operator which
            // contract the approved medium violated. Record the classification of the generation
            // exceptions only: type name plus a defined enum reason, never a message or path.
            firstChance = (_, args) =>
            {
                string? reason = args.Exception switch
                {
                    StandardGenerationException standard => standard.Reason.ToString(),
                    FfmpegCompilerException compiler => compiler.Reason.ToString(),
                    SubtitleSegmentationException => "SubtitleSegmentation",
                    FormatException format => "Format.inner-" + (format.InnerException?.GetType().Name ?? "none"),
                    ArgumentException => "Argument",
                    InvalidOperationException => "InvalidOperation",
                    _ => null,
                };
                if (reason is null) { return; }
                // A handler that throws would re-enter itself through this same event.
                try { fixture?.Report("generation.exception." + args.Exception.GetType().Name + "." + reason); }
                catch (Exception) { /* Diagnostics must never change the observed run. */ }
            };
            AppDomain.CurrentDomain.FirstChanceException += firstChance;
            Dictionary<string, string?> values = IsolatedEnvironment(fixture.Root);
            foreach (string? value in values.Values)
            {
                if (value is not null && Path.IsPathFullyQualified(value)) { Directory.CreateDirectory(value); }
            }
            environment = new ProcessEnvironmentScope(values);
            environment.Apply(); // Before any subprocess is started.
            await RunPhaseAsync("prep", PreparationTimeout, fixture.PrepareAsync);
            await RunPhaseAsync("scenario.execution", ScenarioExecutionTimeout, fixture.RunAcceptanceAsync);
            _ = RemainingTimeout(scenarioElapsed, ScenarioTimeout);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            failure ??= FailureCode(exception);
        }
        finally
        {
            if (firstChance is not null) { AppDomain.CurrentDomain.FirstChanceException -= firstChance; }
            await deadline.CancelAsync();
            if (fixture is not null)
            {
                try { await fixture.ClosePipelineAsync().WaitAsync(ShutdownTimeout); }
                catch (Exception) { failure ??= "approved_media.shutdown_incomplete"; }
                try { await fixture.ReleaseResourcesAsync().WaitAsync(ShutdownTimeout); }
                catch (Exception) { failure ??= "approved_media.resource_release_incomplete"; }
                fixture.Report(failure is null ? "scenario.completed" : "scenario.failed." + SafeCode(failure));
                TestContext.AddResultFile(fixture.LogPath);
                if (File.Exists(fixture.RecordPath)) { TestContext.AddResultFile(fixture.RecordPath); }
                if (File.Exists(fixture.DiagnosisPath)) { TestContext.AddResultFile(fixture.DiagnosisPath); }
                TestContext.WriteLine("approved_media.record_path=" + fixture.RecordPath);
            }
            environment?.Dispose();
        }
        if (failure is not null) { Assert.Fail(failure); }

        async Task RunPhaseAsync(string stage, TimeSpan timeout, Func<CancellationToken, Task> operation)
        {
            using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var phaseElapsed = Stopwatch.StartNew();
            phaseDeadline.CancelAfter(timeout);
            fixture!.Report(stage + ".started");
            try
            {
                await operation(phaseDeadline.Token);
                _ = RemainingTimeout(phaseElapsed, timeout);
                fixture.Report(stage + ".completed", phaseElapsed.Elapsed);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                string code = phaseElapsed.Elapsed >= timeout || phaseDeadline.IsCancellationRequested
                    ? "approved_media." + stage + ".deadline_exceeded" : FailureCode(exception);
                fixture.Report(stage + ".failed." + SafeCode(code), phaseElapsed.Elapsed);
                throw new AssertFailedException(code);
            }
        }
    }

    private static TimeSpan RemainingTimeout(Stopwatch elapsed, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero) { throw new TimeoutException(); }
        return remaining; // Never renew an allowance from a heartbeat.
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        AssertFailedException assertion => SafeCode(assertion.Message),
        TimeoutException => "approved_media.deadline_exceeded",
        OperationCanceledException => "approved_media.deadline_canceled",
        SourceOnlyPipeline.PipelineFailure pipeline => SafeCode(pipeline.Code),
        SourceOnlySourceException source => SafeCode(source.Code),
        LocalAnalysisWorkerException worker => SafeCode(worker.Code),
        SourceOnlyResultException result => "approved_media.result." + result.Code,
        ModelManagerException model => "approved_media.model." + model.Reason,
        WorkspaceAclException acl => "approved_media.acl." + acl.Reason,
        SourceOnlyApplicationDependencyException dependency => "approved_media.dependency." + dependency.Dependency,
        _ => "approved_media.unexpected_" + SafeCode(exception.GetType().Name),
    };

    private static string SafeCode(string? code) => code is { Length: > 0 and <= 256 } &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? code : "approved_media.unclassified_failure";

    private static void Check([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) { throw new AssertFailedException(code); }
    }

    private static T Required<T>(T? value, string code) where T : class => value ?? throw new AssertFailedException(code);

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HubRadio.slnx"))) { current = current.Parent; }
        return Required(current, "approved_media.repository_missing").FullName;
    }

    private static Dictionary<string, string?> IsolatedEnvironment(string root)
    {
        // Keeps caches, temporary files and offline flags inside the run root. All
        // application/DB/model/music paths stay explicit fixture paths; the App is never started.
        string home = Path.Combine(root, "Home");
        string cache = Path.Combine(root, "Cache");
        string hub = Path.Combine(cache, "huggingface", "hub");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = Path.Combine(root, "Temp"),
            ["TMP"] = Path.Combine(root, "Temp"),
            ["TMPDIR"] = Path.Combine(root, "Temp"),
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local"),
            ["HF_HOME"] = Path.Combine(cache, "huggingface"),
            ["HF_HUB_CACHE"] = hub,
            ["HUGGINGFACE_HUB_CACHE"] = hub,
            ["HF_ASSETS_CACHE"] = Path.Combine(cache, "huggingface", "assets"),
            ["HF_XET_CACHE"] = Path.Combine(cache, "huggingface", "xet"),
            ["TRANSFORMERS_CACHE"] = hub,
            ["XDG_CACHE_HOME"] = cache,
            ["TORCH_HOME"] = Path.Combine(cache, "torch"),
            ["HF_HUB_OFFLINE"] = "1",
            ["HF_DATASETS_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["HF_HUB_DISABLE_TELEMETRY"] = "1",
            ["FFREPORT"] = null,
        };
    }

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> values;
        private readonly Dictionary<string, string?> originals;
        private bool disposed;

        internal ProcessEnvironmentScope(Dictionary<string, string?> values)
        {
            this.values = new(values, StringComparer.OrdinalIgnoreCase);
            originals = values.Keys.ToDictionary(name => name,
                name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process), StringComparer.OrdinalIgnoreCase);
        }

        internal void Apply()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach ((string name, string? value) in values)
            {
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            }
        }

        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            foreach ((string name, string? value) in originals)
            {
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            }
        }
    }

    private sealed class AcceptanceFixture
    {
        private readonly TestContext context;
        private readonly object logGate = new();
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly string repository;
        private readonly string database;
        private readonly string profiles;
        private readonly string settingsProfiles;
        private readonly string approvedMediaPath;
        private readonly SourceOnlyApplicationResourceLease workerLease = new();
        private SourceOnlyPipeline? pipeline;
        private SourceOnlyRuntimeOptions? options;
        private ProjectCreationService? creation;
        private SourceOnlyApplicationServices? services;
        private SourceOnlyDeliveryService? delivery;
        private StageCapture? capture;
        private string ffmpegDigest = string.Empty;
        private string ffprobeDigest = string.Empty;
        private string workerRuntimeName = string.Empty;

        internal AcceptanceFixture(TestContext context, string approvedMediaPath)
        {
            this.context = context;
            this.approvedMediaPath = approvedMediaPath;
            repository = RepositoryRoot();
            string parent = SourceOnlySourceLocator.RequirePath(Path.Combine(repository, "artifacts", "validation"), directory: true);
            Root = Path.Combine(parent, "approved-media-" + Guid.NewGuid().ToString("N"));
            ActorSid = new WindowsActorProvider().GetCurrentSid();
            WorkspaceAclResult acl = WorkspaceAcl.CreateNew(Root, ActorSid);
            Check(acl.OwnerMatchesCurrentActor && acl.Protected && acl.InheritanceConfigured && acl.ExplicitRuleCount == 3,
                "approved_media.acl_invalid");
            LogPath = Path.Combine(Root, "watchdog.log");
            RecordPath = Path.Combine(Root, "acceptance-record.json");
            DiagnosisPath = Path.Combine(Root, "checkpoint-diagnosis.txt");
            context.WriteLine("approved_media.root=" + Path.GetFileName(Root));
            Report("acl.created");
            database = Path.Combine(Root, "hubradio.db");
            profiles = Path.Combine(repository, "src", "HubRadio.App", "Profiles");
            settingsProfiles = Path.Combine(repository, "src", "HubRadio.App", "DefaultSettings", "Profiles");
            // The approved medium is COPIED into the ACL-protected run root. The repository
            // sample file is only read; nothing writes to it or to any user directory.
            SourcePath = Path.Combine(Root, "approved-media.mp4");
        }

        internal string Root { get; }
        internal string LogPath { get; }
        internal string RecordPath { get; }
        internal string DiagnosisPath { get; }
        internal string ActorSid { get; }
        internal string SourcePath { get; }
        internal ContentDigest SourceDigest { get; private set; } = null!;
        internal long SourceSize { get; private set; }

        private SourceOnlyApplicationServices Services => Required(services, "approved_media.services_missing");
        private SourceOnlyPipeline Pipeline => Required(pipeline, "approved_media.pipeline_missing");

        internal void Report(string code) => Report(code, null);

        internal void Report(string code, TimeSpan? phaseDuration)
        {
            lock (logGate)
            {
                TimeSpan observed = elapsed.Elapsed;
                string line = FormattableString.Invariant($"approved_media elapsed_ms={(long)observed.TotalMilliseconds} stage={SafeCode(code)}");
                if (phaseDuration is not null)
                {
                    line += FormattableString.Invariant($" phase_elapsed_ms={phaseDuration.Value.TotalMilliseconds:F3}");
                }
                // Stable codes only: no process stderr, transcript text, metadata, SID or absolute paths.
                context.WriteLine(line);
                File.AppendAllText(LogPath, line + "\n", Utf8);
            }
        }

        internal async Task PrepareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            foreach ((string name, string? value) in IsolatedEnvironment(Root))
            {
                Check(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) == value,
                    "approved_media.environment_changed." + name);
            }
            var verifier = new BinaryVerifier();
            string ffmpeg = Path.Combine(repository, "artifacts", "portable", "osl009-payload", "Tools", "ffmpeg.exe");
            string ffprobe = Path.Combine(repository, "artifacts", "portable", "osl009-payload", "Tools", "ffprobe.exe");
            string worker = Path.Combine(repository, "artifacts", "python", "WorkerRuntime-Live-20260909c");
            string fontPath = Path.Combine(repository, "artifacts", "inputs", "fonts", "verified-2.004-7388bda58dd8", "NotoSansJP-Regular.otf");
            workerRuntimeName = Path.GetFileName(worker);
            ffmpegDigest = DecodeValidator.CanonicalFfmpegSha256;
            ffprobeDigest = FfprobeClient.CanonicalFfprobeSha256;
            var python = new BinaryVerificationRequest("approved-media.python", Path.Combine(worker, "python.exe"),
                new ContentDigest(SourceOnlyApplicationResourceLease.PythonSha256), SourceOnlyApplicationResourceLease.PythonByteSize);
            var fontRequest = new BinaryVerificationRequest("approved-media.font", fontPath, new ContentDigest(FontSha256), 4533028);
            foreach (BinaryVerificationRequest request in new[]
            {
                new BinaryVerificationRequest("approved-media.ffmpeg", ffmpeg, new ContentDigest(DecodeValidator.CanonicalFfmpegSha256), DecodeValidator.CanonicalFfmpegByteSize),
                new BinaryVerificationRequest("approved-media.ffprobe", ffprobe, new ContentDigest(FfprobeClient.CanonicalFfprobeSha256), FfprobeClient.CanonicalFfprobeByteSize),
                python, fontRequest,
            })
            {
                _ = SourceOnlySourceLocator.RequirePath(request.FilePath, directory: false);
                _ = await verifier.VerifyAsync(request, token);
            }
            Report("dependencies.binaries_verified");
            await workerLease.VerifyWorkerAsync(worker, token);
            Report("dependencies.verified");

            string catalogPath = Path.Combine(repository, "packaging", "models", "catalog.json");
            string bundleRoot = Path.Combine(Root, "ModelCatalog");
            byte[] catalogBytes = await File.ReadAllBytesAsync(catalogPath, token);
            Check(ContentDigest.Compute(catalogBytes).Hex == ModelManager.PinnedCatalogSha256, "approved_media.catalog_pin_mismatch");
            using (JsonDocument catalog = JsonDocument.Parse(catalogBytes))
            {
                JsonElement policy = catalog.RootElement.GetProperty("policy");
                Check(policy.GetProperty("localProcessingOnly").GetBoolean() && !policy.GetProperty("networkAcquisitionEnabled").GetBoolean() &&
                    !policy.GetProperty("silentUpgradeEnabled").GetBoolean(), "approved_media.catalog_not_offline");
                JsonElement model = catalog.RootElement.GetProperty("models").EnumerateArray().Single(item => item.GetProperty("modelId").GetString() == ModelId);
                JsonElement bundle = model.GetProperty("bundle");
                string relative = bundle.GetProperty("relativePath").GetString()!;
                Check(bundle.GetProperty("manifestSha256").GetString() == ModelManifest, "approved_media.bundle_contract_changed");
                string destination = Path.Combine(bundleRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(destination);
                foreach (JsonElement file in bundle.GetProperty("files").EnumerateArray())
                {
                    string name = file.GetProperty("name").GetString()!;
                    string source = Path.Combine(repository, "artifacts", "inputs", "models", "faster-whisper-small", name);
                    string copy = Path.Combine(destination, name);
                    var digest = new ContentDigest(file.GetProperty("sha256").GetString()!);
                    long size = file.GetProperty("byteSize").GetInt64();
                    _ = SourceOnlySourceLocator.RequirePath(source, directory: false);
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous))
                    await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                    {
                        Check(input.Length == size, "approved_media.bundle_source_size_mismatch");
                        await input.CopyToAsync(output, token);
                        await output.FlushAsync(token);
                    }
                    _ = await verifier.VerifyAsync(new BinaryVerificationRequest("approved-media.model-file", copy, digest, size), token);
                }
            }
            Report("model.copied");
            ModelManager manager = ModelManager.Load(catalogPath, bundleRoot, Root, verifier);
            string installed = await manager.InstallFromBundleAsync(ModelId, token);
            Check(installed == await manager.RequireOfflinePathAsync(ModelId, token), "approved_media.model_not_ready");
            Check(IsUnderRoot(installed), "approved_media.model_escaped_root");
            Report("model.installed");

            _ = DatabaseMigrator.Migrate(database, Path.Combine(Root, "backups"));
            var settings = new DefaultSettingsStore(database);
            _ = new BundledDefaultSettingsBootstrap(settings).Initialize(settingsProfiles, DateTimeOffset.UtcNow);
            _ = new AuthorizationService(new RoleAssignmentStore(database)).BootstrapInitialRoles(ActorSid);
            string app = typeof(SourceOnlyPipeline).Assembly.Location;
            ContentDigest appDigest;
            await using (var stream = File.OpenRead(app)) { appDigest = ContentDigest.FromBytes(await SHA256.HashDataAsync(stream, token)); }
            options = new SourceOnlyRuntimeOptions(database, Root, ffmpeg, ffprobe, python.FilePath, installed, profiles,
                Path.Combine(settingsProfiles, "1.0.0.json"), new FfmpegFontBinding(new RenderFontEvidence("Noto Sans JP", fontRequest.ExpectedDigest), fontRequest),
                python, manager, typeof(SourceOnlyPipeline).Assembly.GetName().Version!.ToString(), appDigest,
                OperationTimeout, musicDirectory: Path.Combine(Root, "Music"));

            // Copy the approved medium into the protected run root and pin its identity.
            _ = SourceOnlySourceLocator.RequirePath(approvedMediaPath, directory: false);
            await using (var input = new FileStream(approvedMediaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous))
            await using (var output = new FileStream(SourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            await using (var source = File.OpenRead(SourcePath))
            {
                SourceSize = source.Length;
                SourceDigest = ContentDigest.FromBytes(await SHA256.HashDataAsync(source, token));
            }
            Check(SourceDigest.Hex == ApprovedMediaSha256 && SourceSize == ApprovedMediaByteSize, "approved_media.input_is_not_the_approved_medium");
            Report("source.copied_and_verified");
            OpenPipeline();
        }

        private void OpenPipeline()
        {
            Check(pipeline is null, "approved_media.owner_already_open");
            _ = WorkspaceAcl.VerifyExisting(Root, ActorSid);
            SourceOnlyRuntimeOptions runtime = Required(options, "approved_media.options_missing");
            services = new SourceOnlyApplicationServices(database, Root, new AssetCandidateRegistry(), new DefaultSettingsStore(database));
            var qc = new OutputQc(Services.Artifacts, new ProcessRunner(), new BinaryVerifier(), runtime.FfprobePath, runtime.FfmpegPath);
            var evaluation = new SourceOnlyOutputEvaluation(Services.Artifacts, qc, Services.Evaluations, Services.Workflows, Services.Projects);
            delivery = new SourceOnlyDeliveryService(database, Root, Services.Artifacts, Services.Results, Services.Workflows);
            capture = new StageCapture(Services.Registrations.Callbacks, Report);
            pipeline = new SourceOnlyPipeline(runtime, Services.Candidates, evaluation, capture.Callbacks,
                deliverAsync: delivery.DeliverAsync, jobStore: Services.Jobs, jobCoordinator: Services.Coordinator);
            creation = new ProjectCreationService(Services.Drafts, new AuthorizationService(new RoleAssignmentStore(database)),
                Services.Candidates, ProjectCreationCatalog.Load(profiles), Services.Settings, () => ActorSid,
                cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return Task.FromResult<EntityId<AssetIdTag>?>(Services.Candidates.RegisterSelection(SourcePath));
                }, sourceOnlyCoordinator: pipeline);
            Report("pipeline.opened");
        }

        internal async Task RunAcceptanceAsync(CancellationToken token)
        {
            StoredSourceOnlyExecution accepted = await AcceptAsync(token);
            StoredSourceOnlyExecution completed;
            try
            {
                completed = await AwaitExecutionAsync(accepted, token);
            }
            catch (Exception)
            {
                // The pipeline folds an unclassified generation failure into the single opaque
                // source_only.reconciliation_required gate, which cannot tell an operator which
                // contract the approved medium violated. Reproduce the generator's checkpoint
                // comparison read-only so the acceptance record names the failing condition.
                DiagnoseCheckpoint();
                throw;
            }
            // A terminal non-Completed phase is reported as durable state, not as an exception,
            // so the diagnosis has to be triggered by the phase rather than by a throw.
            if (completed.Phase != SourceOnlyExecutionPhase.Completed || completed.FailureCode is not null)
            {
                DiagnoseCheckpoint();
            }
            await RecordAsync(completed, token);
        }

        /// <summary>
        /// Evaluates the same predicates as StandardArtifactGenerator.ValidateCheckpoint against
        /// the preparation the pipeline actually handed to the generator (captured through the
        /// production CompositionReady callback) and the one reloaded from durable evidence.
        /// Read-only, best effort, and never throws: it runs while a failure is propagating.
        /// </summary>
        private void DiagnoseCheckpoint()
        {
            var lines = new List<string>();
            try
            {
                StandardCompositionPreparation? expected = capture?.Composition;
                if (expected is null)
                {
                    Report("checkpoint.diagnosis.no_composition_captured");
                    return;
                }

                // The generator looks the checkpoint up by this id, so a captured preparation is
                // only meaningful evidence when that exact version was persisted.
                bool childPersisted = Services.Projects.GetCompositionVersion(expected.Child.Id) is not null;
                lines.Add("capturedChildId=" + expected.Child.Id);
                lines.Add("capturedChildPersisted=" + childPersisted);

                var versions = new VersionService(Services.Projects);
                StandardCompositionPreparation? stored;
                try
                {
                    stored = versions.GetStandardComposition(expected.Child.Id);
                }
                catch (Exception exception)
                {
                    lines.Add("getStandardComposition.threw=" + exception.GetType().Name);
                    Report("checkpoint.diagnosis.reload_threw." + SafeCode(exception.GetType().Name));
                    return;
                }

                if (stored is null)
                {
                    lines.Add("cond0.storedIsNull=true");
                    Report("checkpoint.mismatch.stored_is_null");
                    return;
                }

                var results = new (string Name, bool Equal)[]
                {
                    ("cond1.childVersionJson", CompositionSnapshotJson.WriteVersion(stored.Child) == CompositionSnapshotJson.WriteVersion(expected.Child)),
                    ("cond2.plan", StandardRenderGraphFactory.Digest(stored.Plan) == StandardRenderGraphFactory.Digest(expected.Plan)),
                    ("cond3.applicationDigest", stored.ApplicationDigest == expected.ApplicationDigest),
                    ("cond4.expectedWorkingCopyRevision", stored.ExpectedWorkingCopyRevision == expected.ExpectedWorkingCopyRevision),
                    ("cond5.preparedWorkingCopy", CompositionSnapshotJson.WriteWorkingCopy(stored.PreparedWorkingCopy) == CompositionSnapshotJson.WriteWorkingCopy(expected.PreparedWorkingCopy)),
                    ("cond6.parentTimelineMap", StandardRenderGraphFactory.Digest(stored.ParentTimelineMap) == StandardRenderGraphFactory.Digest(expected.ParentTimelineMap)),
                    ("cond7.childTimelineMap", StandardRenderGraphFactory.Digest(stored.ChildTimelineMap) == StandardRenderGraphFactory.Digest(expected.ChildTimelineMap)),
                };
                foreach ((string name, bool equal) in results)
                {
                    lines.Add(name + ".equal=" + equal);
                    if (!equal) { Report("checkpoint.mismatch." + name.Split('.')[1]); }
                }
                lines.Add("cond4.stored=" + stored.ExpectedWorkingCopyRevision + " expected=" + expected.ExpectedWorkingCopyRevision);
                if (results.All(item => item.Equal)) { Report("checkpoint.diagnosis.all_conditions_equal"); }

                if (!results.Single(item => item.Name == "cond2.plan").Equal)
                {
                    var differences = new List<string>();
                    using var expectedPlan = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(expected.Plan));
                    using var storedPlan = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(stored.Plan));
                    CollectJsonDifferences(expectedPlan.RootElement, storedPlan.RootElement, "plan", differences, 200);
                    lines.Add("plan.differenceCount=" + differences.Count);
                    lines.AddRange(differences);
                    // Distinguish "different content" from "same content, different order":
                    // the whole-object digest used by ValidateCheckpoint is order sensitive.
                    lines.Add("plan.longFormSourceSpans.sameSetIgnoringOrder=" +
                        SameSetIgnoringOrder(expectedPlan.RootElement, storedPlan.RootElement,
                            ["LongForm", "Provenance", "SourceSpans"]));
                }
            }
            catch (Exception exception)
            {
                lines.Add("diagnosis.threw=" + exception.GetType().Name);
                Report("checkpoint.diagnosis.failed." + SafeCode(exception.GetType().Name));
            }
            finally
            {
                try { File.WriteAllLines(DiagnosisPath, lines, Utf8); }
                catch (Exception) { /* Diagnostics must never change the observed run. */ }
            }
        }

        /// <summary>
        /// Reports differing JSON locations only. String values may carry transcript text, so
        /// their contents are never emitted; only the path and the two lengths are recorded.
        /// </summary>
        private static void CollectJsonDifferences(JsonElement expected, JsonElement stored, string path, List<string> output, int limit)
        {
            if (output.Count >= limit) { return; }
            if (expected.ValueKind != stored.ValueKind)
            {
                output.Add(path + ": kind " + expected.ValueKind + " vs " + stored.ValueKind);
                return;
            }
            switch (expected.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in expected.EnumerateObject())
                    {
                        if (output.Count >= limit) { return; }
                        if (!stored.TryGetProperty(property.Name, out JsonElement other))
                        {
                            output.Add(path + "." + property.Name + ": absent on stored side");
                            continue;
                        }
                        CollectJsonDifferences(property.Value, other, path + "." + property.Name, output, limit);
                    }
                    foreach (JsonProperty property in stored.EnumerateObject())
                    {
                        if (output.Count >= limit) { return; }
                        if (!expected.TryGetProperty(property.Name, out _))
                        {
                            output.Add(path + "." + property.Name + ": absent on expected side");
                        }
                    }
                    return;
                case JsonValueKind.Array:
                    JsonElement[] expectedItems = expected.EnumerateArray().ToArray();
                    JsonElement[] storedItems = stored.EnumerateArray().ToArray();
                    if (expectedItems.Length != storedItems.Length)
                    {
                        output.Add(path + ": length " + expectedItems.Length + " vs " + storedItems.Length);
                        return;
                    }
                    for (int index = 0; index < expectedItems.Length; index++)
                    {
                        if (output.Count >= limit) { return; }
                        CollectJsonDifferences(expectedItems[index], storedItems[index], path + "[" + index + "]", output, limit);
                    }
                    return;
                case JsonValueKind.String:
                    if (!string.Equals(expected.GetString(), stored.GetString(), StringComparison.Ordinal))
                    {
                        output.Add(path + ": string differs, lengths " + (expected.GetString() ?? "").Length + " vs " + (stored.GetString() ?? "").Length);
                    }
                    return;
                case JsonValueKind.Number:
                    if (!string.Equals(expected.GetRawText(), stored.GetRawText(), StringComparison.Ordinal))
                    {
                        output.Add(path + ": number " + expected.GetRawText() + " vs " + stored.GetRawText());
                    }
                    return;
                default:
                    return;
            }
        }

        /// <summary>
        /// True when the array at the given property chain holds the same elements on both
        /// sides but in a different order. The digest used by the generator is order sensitive,
        /// so this separates a genuine content difference from an ordering difference.
        /// </summary>
        private static string SameSetIgnoringOrder(JsonElement expected, JsonElement stored, string[] propertyChain)
        {
            foreach (string name in propertyChain)
            {
                if (expected.ValueKind != JsonValueKind.Object || !expected.TryGetProperty(name, out expected)) { return "unavailable"; }
                if (stored.ValueKind != JsonValueKind.Object || !stored.TryGetProperty(name, out stored)) { return "unavailable"; }
            }
            if (expected.ValueKind != JsonValueKind.Array || stored.ValueKind != JsonValueKind.Array) { return "unavailable"; }
            string[] expectedItems = expected.EnumerateArray().Select(item => item.GetRawText()).Order(StringComparer.Ordinal).ToArray();
            string[] storedItems = stored.EnumerateArray().Select(item => item.GetRawText()).Order(StringComparer.Ordinal).ToArray();
            return expectedItems.SequenceEqual(storedItems, StringComparer.Ordinal) ? "true" : "false";
        }

        private async Task<StoredSourceOnlyExecution> AcceptAsync(CancellationToken token)
        {
            ProjectCreationService service = Required(creation, "approved_media.creation_missing");
            SourceOnlyRecoveryResult empty = await Pipeline.RecoverAsync(token);
            Check(!empty.Canceled && empty.FailureCode is null && empty.Executions.Count == 0, "approved_media.new_root_not_empty");
            ProjectCreationDraftView draft = await service.CreateSourceOnlyDraftAsync(token);
            Check(!draft.IsFinalized && draft.SourceOnly is not null && draft.SourceStatus == "available" && draft.PublicationScope == "internalOnly",
                "approved_media.draft_contract_invalid");
            var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId);
            token.ThrowIfCancellationRequested();
            ProjectCreationDraftView finalized = service.FinalizeDraft(draftId, draft.Revision, FinalizeInput(draft));
            StoredSourceOnlyExecution accepted = Required(Services.Executions.GetByDraft(draftId), "approved_media.acceptance_missing");
            Check(finalized.IsFinalized && finalized.ProjectId == accepted.Request.ProjectId.ToString(), "approved_media.acceptance_binding_invalid");
            Report("project.accepted");
            return accepted;
        }

        private async Task<StoredSourceOnlyExecution> AwaitExecutionAsync(StoredSourceOnlyExecution accepted, CancellationToken token)
        {
            SourceOnlyPipeline owner = Pipeline;
            Task<SourceOnlyPipelineRun> running = owner.Start(accepted.Request.ExecutionId); // Join the task started by acceptance.
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task watchdog = WatchAsync();
            try
            {
                SourceOnlyPipelineRun result = await running.WaitAsync(token);
                Check(result.InfrastructureFailureCode is null, SafeCode(result.InfrastructureFailureCode));
                StoredSourceOnlyExecution execution = Required(result.Execution, "approved_media.execution_missing");
                Report("execution." + execution.Phase);
                if (execution.FailureCode is not null) { Report(execution.FailureCode); }
                return execution;
            }
            catch (Exception)
            {
                Report("execution.cancel_requested");
                _ = await owner.CancelAsync(accepted.Request.ExecutionId).WaitAsync(ShutdownTimeout, CancellationToken.None);
                throw;
            }
            finally
            {
                await watching.CancelAsync();
                try { await watchdog; }
                catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
            }

            async Task WatchAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (await timer.WaitForNextTickAsync(watching.Token))
                {
                    // Read-only durable state. An observation never renews a deadline.
                    StoredSourceOnlyExecution current = Required(Services.Executions.Get(accepted.Request.ExecutionId), "approved_media.watchdog_execution_missing");
                    Report("waiting." + current.Phase);
                }
            }
        }

        /// <summary>
        /// Writes the six measurement groups required by requirement-definition.md section 18.
        /// Everything written here is read back from durable evidence; nothing is asserted to be
        /// acceptable quality, and no human evaluation is represented.
        /// </summary>
        private async Task RecordAsync(StoredSourceOnlyExecution execution, CancellationToken token)
        {
            StageCapture stages = Required(capture, "approved_media.capture_missing");
            Check(execution.Phase == SourceOnlyExecutionPhase.Completed && execution.FailureCode is null,
                execution.FailureCode is null ? "approved_media.execution_not_completed" : SafeCode(execution.FailureCode));
            IngestWorkflowRunResult ingest = Required(stages.Ingest, "approved_media.ingest_callback_missing");
            TranscriptImportResult transcript = Required(stages.Transcript, "approved_media.transcript_callback_missing");
            SourceOnlyGeneratedRuntimeEvidence generated = Required(stages.Generated, "approved_media.generated_callback_missing");
            Project project = Required(stages.Project, "approved_media.project_callback_missing");
            var original = Required(ingest.Original, "approved_media.original_missing");
            SourceOnlyPipelineEvidence evidence = await Pipeline.ReadEvidenceAsync(project.Id, token);
            SourceOnlyResultLoadResult loaded = await Services.Results.LoadAsync(execution, token);
            Check(loaded.Validation.IsValid, "approved_media.persisted_result_invalid");
            SourceOnlyResult result = loaded.Result;
            result.ValidateBinding(execution);
            SourceOnlyPipelineGenerationEvidence generation = Required(evidence.Generation, "approved_media.generation_evidence_missing");
            MediaStreamInfo video = original.MediaInfo.PrimaryVideoStream;
            MediaStreamInfo? audio = original.MediaInfo.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamType.Audio);

            var record = new
            {
                schemaVersion = "1.0.0",
                // Group 1: input digest, settings profile version, tool/model versions.
                input = new
                {
                    sourceSha256 = SourceDigest.Hex,
                    sourceByteSize = SourceSize,
                    matchesApprovedMedium = SourceDigest.Hex == ApprovedMediaSha256 && SourceSize == ApprovedMediaByteSize,
                    containerDurationSeconds = original.MediaInfo.FormatDuration?.ToString(),
                    videoCodec = video.CodecName,
                    videoWidth = video.Width,
                    videoHeight = video.Height,
                    audioCodec = audio?.CodecName,
                    audioSampleRate = audio?.SampleRate,
                    audioChannels = audio?.Channels,
                    settingsSnapshotDigest = execution.Request.SettingsSnapshot.ContentDigest.Hex,
                    settingsProfileVersion = execution.Request.SettingsSnapshot.ProfileVersion,
                    appVersion = typeof(SourceOnlyPipeline).Assembly.GetName().Version!.ToString(),
                    ffmpegSha256 = ffmpegDigest,
                    ffprobeSha256 = ffprobeDigest,
                    workerRuntime = workerRuntimeName,
                    asrModelId = ModelId,
                    asrModelManifestSha256 = transcript.RawWorkerResult.ModelManifestDigest.Hex,
                    asrDevice = transcript.RawWorkerResult.Configuration.Device,
                    asrComputeType = transcript.RawWorkerResult.Configuration.ComputeType,
                    fontSha256 = FontSha256,
                },
                // Group 2: automatic candidates, applied ranges, rejected candidates and warnings.
                candidates = new
                {
                    transcriptUtterances = transcript.Revision.Utterances.Count,
                    transcriptRevisionDigest = evidence.MachineTranscript.ContentDigest.Hex,
                    shortFormCandidates = generated.ShortForms.Count,
                    shortFormsWithSubtitles = generated.ShortForms.Count(item => item.Subtitles.Revisions.Count > 0),
                    landscapeSubtitleCues = generated.LandscapeSubtitles.Subtitles.Revisions.Count,
                    landscapeSubtitleWarnings = generated.LandscapeSubtitles.Warnings
                        .Select(item => new { item.Reason }).ToArray(),
                    generationDisposition = generation.Disposition.ToString(),
                    generationWarnings = result.GenerationWarnings.Select(item => new { item.Role, item.Code }).ToArray(),
                    omissions = result.Omissions.Select(item => new
                    {
                        role = item.Role.ToString(),
                        reason = item.Reason.ToString(),
                        renditionId = item.RenditionId?.ToString(),
                    }).ToArray(),
                },
                // Group 3: subtitle reading speed, line length, line count, sync and safe zone.
                subtitles = result.Artifacts.Where(item => item.Measurements is not null && item.Measurements.Cues.Count > 0)
                    .Select(item => new
                    {
                        role = item.Role.ToString(),
                        cueCount = item.Measurements!.Cues.Count,
                        maximumLineCharacters = item.Measurements.Cues.Max(cue => cue.MaximumLineCharacters),
                        maximumLineCount = item.Measurements.Cues.Max(cue => cue.LineCount),
                        maximumCharactersPerSecond = item.Measurements.Cues
                            .Where(cue => cue.Duration.ToDouble() > 0d)
                            .Select(cue => cue.CharacterCount / cue.Duration.ToDouble())
                            .DefaultIfEmpty(0d).Max(),
                        allCuesWithinOutputDuration = item.Measurements.Cues.All(cue =>
                            cue.Start.CompareTo(item.Measurements.ExpectedDuration) <= 0),
                        safeZoneSatisfiedCount = item.Measurements.Cues.Count(cue => cue.SafeZoneSatisfied),
                        safeZoneViolatedCount = item.Measurements.Cues.Count(cue => !cue.SafeZoneSatisfied),
                    }).ToArray(),
                // Group 4: codec, decode, duration, A/V sync, loudness and true peak.
                technicalQc = result.Artifacts.Where(item => item.IsVideo).Select(item => new
                {
                    role = item.Role.ToString(),
                    mediaType = item.MediaType,
                    decodedSuccessfully = item.Measurements?.DecodedSuccessfully,
                    videoTimingContinuous = item.Measurements?.VideoTimingContinuous,
                    videoFrameCadenceExact = item.Measurements?.VideoFrameCadenceExact,
                    expectedDuration = item.Measurements?.ExpectedDuration.ToString(),
                    videoStart = item.Measurements?.VideoStart.ToString(),
                    videoEnd = item.Measurements?.VideoEnd.ToString(),
                    audioStart = item.Measurements?.AudioStart?.ToString(),
                    audioEnd = item.Measurements?.AudioEnd?.ToString(),
                    videoFrameCount = item.Measurements?.VideoFrameCount,
                    loudnessLufs = item.Measurements?.Loudness?.Value,
                    truePeakDbtp = item.Measurements?.TruePeak?.Value,
                    qcFailureReason = item.QcFailureReason?.ToString(),
                    qcFailureStage = item.QcFailureStage?.ToString(),
                }).ToArray(),
                // Group 5: OriginRef and source digest of every output element.
                outputs = result.Artifacts.Select(item => new
                {
                    role = item.Role.ToString(),
                    artifactId = item.ArtifactId.ToString(),
                    artifactSha256 = item.ArtifactDigest.Hex,
                    byteSize = item.ByteSize,
                    delivered = item.Delivered,
                    versionId = item.VersionId.ToString(),
                    versionDigest = item.VersionDigest.Hex,
                    renditionId = item.RenditionId?.ToString(),
                    thumbnailPlanId = item.ThumbnailPlanId?.ToString(),
                    sourceArtifactId = item.Source.ArtifactId.ToString(),
                    sourceDigest = item.Source.ContentDigest.Hex,
                    graphDigest = item.GraphDigest.Hex,
                    manifestDigest = item.ManifestDigest?.Hex,
                    evidenceKinds = item.Evidence.Select(reference => reference.Kind.ToString()).OrderBy(kind => kind, StringComparer.Ordinal).ToArray(),
                    frameEvidence = item.FrameEvidence is null ? null : new
                    {
                        sourceAssetId = item.FrameEvidence.SourceAssetId.ToString(),
                        sourceDigest = item.FrameEvidence.SourceDigest.Hex,
                        sourceFrameSequence = item.FrameEvidence.SourceFrameSequence,
                        sourcePresentationTime = item.FrameEvidence.SourcePresentationTime.ToString(),
                        width = item.FrameEvidence.Width,
                        height = item.FrameEvidence.Height,
                    },
                }).ToArray(),
                ancillaryArtifacts = result.AncillaryArtifacts.Select(item => new
                {
                    kind = item.Kind.ToString(),
                    artifactId = item.Artifact.ArtifactId.ToString(),
                    sha256 = item.Artifact.ContentDigest.Hex,
                    byteSize = item.Artifact.ByteSize,
                }).ToArray(),
                // Group 6: automated proxy evaluation PASS / WARNING / FAIL / NOT EVALUATED.
                automatedEvaluation = result.Artifacts.Where(item => item.Evaluation is not null).Select(item => new
                {
                    role = item.Role.ToString(),
                    evaluationId = item.Evaluation!.EvaluationId.ToString(),
                    pass = item.Evaluation.StatusCounts.Pass,
                    warning = item.Evaluation.StatusCounts.Warning,
                    fail = item.Evaluation.StatusCounts.Fail,
                    notEvaluated = item.Evaluation.StatusCounts.NotEvaluated,
                    rules = item.Evaluation.Rules.Select(rule => new
                    {
                        ruleId = rule.RuleId.ToString(),
                        status = rule.Status.ToString(),
                        reasonCode = rule.ReasonCode,
                    }).ToArray(),
                }).ToArray(),
                // Standard set accounting for requirement-definition.md section 18 items 1-6.
                standardSet = new
                {
                    landscapeClean = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.LandscapeClean),
                    landscapeBurnIn = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.LandscapeBurnIn),
                    verticalBurnIn = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.VerticalBurnIn),
                    subRip = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.SubRip),
                    webVtt = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.WebVtt),
                    landscapeThumbnail = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.LandscapeThumbnail),
                    verticalThumbnail = result.Artifacts.Count(item => item.Role == SourceOnlyResultRole.VerticalThumbnail),
                    transcript = result.AncillaryArtifacts.Count(item => item.Kind == SourceOnlyResultEvidenceKind.Transcript),
                    usedSourceRanges = result.AncillaryArtifacts.Count(item => item.Kind == SourceOnlyResultEvidenceKind.UsedSourceRanges),
                    automatedEvaluationRecords = result.Artifacts.Count(item => item.Evaluation is not null),
                },
                status = new
                {
                    hasBlockingFailures = result.HasBlockingFailures,
                    reconciliationRequired = result.ReconciliationRequired,
                    stagingCleanupRequired = result.StagingCleanupRequired,
                    allArtifactsDelivered = result.Artifacts.Count > 0 && result.Artifacts.All(item => item.Delivered),
                    failures = result.Failures.Select(item => item.Code.ToString()).ToArray(),
                    resultDigest = result.CanonicalDigest.Hex,
                },
                // Never asserted by this run. Recorded so a reader cannot mistake the scope.
                notEvaluated = new[]
                {
                    "asr-accuracy", "perceived-quality", "human-evaluation", "native-gui-operation",
                    "clean-os-install", "code-signing", "external-publication",
                },
            };
            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(RecordPath, json, Utf8, token);
            Report("acceptance.recorded");
        }

        private bool IsUnderRoot(string path) => Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static JsonElement FinalizeInput(ProjectCreationDraftView draft) => JsonSerializer.SerializeToElement(new
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

        internal async Task ClosePipelineAsync()
        {
            if (pipeline is null) { return; }
            await pipeline.DisposeAsync().AsTask();
            pipeline = null;
            creation = null;
            Report("pipeline.closed");
        }

        internal Task ReleaseResourcesAsync() => workerLease.DisposeAsync().AsTask();
    }

    private sealed class StageCapture
    {
        internal StageCapture(SourceOnlyRuntimeCallbacks registrations, Action<string> report)
        {
            // Invoke the actual host registrations. Observers retain producer objects only; none
            // substitutes ingest, ASR, a compiler, QC, evaluation, delivery or job completion.
            Callbacks = new SourceOnlyRuntimeCallbacks
            {
                IngestReady = async (value, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (registrations.IngestReady is not null) { await registrations.IngestReady(value, token); }
                    Ingest = value; report("callback.ingest");
                },
                TranscriptReady = async (project, value, token) =>
                {
                    await registrations.TranscriptReady!(project, value, token);
                    Project = project; Transcript = value; report("callback.transcript");
                },
                AnalysisReady = async (project, value, evidence, token) =>
                {
                    await registrations.AnalysisReady!(project, value, evidence, token);
                    report("callback.analysis");
                },
                CompositionReady = async (value, import, token) =>
                {
                    await registrations.CompositionReady!(value, import, token);
                    Composition = value; report("callback.composition");
                },
                GeneratedReady = async (value, token) =>
                {
                    await registrations.GeneratedReady!(value, token);
                    Generated = value; report("callback.generated");
                },
            };
        }

        internal SourceOnlyRuntimeCallbacks Callbacks { get; }
        internal IngestWorkflowRunResult? Ingest { get; private set; }
        internal Project? Project { get; private set; }
        internal TranscriptImportResult? Transcript { get; private set; }
        internal StandardCompositionPreparation? Composition { get; private set; }
        internal SourceOnlyGeneratedRuntimeEvidence? Generated { get; private set; }
    }
}
