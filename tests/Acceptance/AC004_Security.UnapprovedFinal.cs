using HubRadio.App.Jobs;
using HubRadio.App.Rendering;
using HubRadio.App.Reviews;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AC004_Security_UnapprovedFinal
{
    private static readonly DateTimeOffset CreatedAt =
        new(2046, 4, 4, 6, 7, 8, TimeSpan.Zero);

    [TestMethod]
    public async Task MissingApprovalOrDigestMismatchStopsBeforeRenderProcessAndPromotion()
    {
        (string Name, Func<FinalRenderFixture, FinalRenderRequest> Create)[] cases =
        [
            (
                "missing approval",
                fixture => fixture.CreateRequest()),
            (
                "digest mismatch",
                fixture => fixture.CreateRequest(
                    approvedDigest: Digest('f'),
                    approvalEvidence: [fixture.CreateApproval()])),
        ];

        foreach ((string name, Func<FinalRenderFixture, FinalRenderRequest> create) in cases)
        {
            using FinalRenderFixture fixture = new();
            FinalRenderRunResult result = await fixture.Service.RunAsync(
                create(fixture));

            Assert.AreEqual(FinalRenderDisposition.Failed, result.Disposition, name);
            Assert.AreEqual(FinalRenderFailureReason.ApprovalMismatch, result.FailureReason, name);
            Assert.AreEqual(FinalRenderStage.NotStarted, result.LastCompletedStage, name);
            Assert.AreEqual(JobState.Failed, result.Job.Snapshot.State, name);
            Assert.AreEqual(0, fixture.CompileCount, name);
            Assert.AreEqual(0, fixture.ProcessCount, name);
            Assert.AreEqual(0, fixture.PromotionCount, name);

            var evidence = new AcceptanceEvidence(
                "Q-004",
                new ScopeEvidence(
                    name,
                    result.FailureReason?.ToString() ?? string.Empty,
                    fixture.CompileCount,
                    fixture.ProcessCount,
                    fixture.PromotionCount));
            Assert.AreEqual("Q-004", evidence.Requirement);
            Assert.AreEqual(
                FinalRenderFailureReason.ApprovalMismatch.ToString(),
                evidence.Scope.FailureReason);
            Assert.AreEqual(0, evidence.Scope.CompileSideEffectCount);
            Assert.AreEqual(0, evidence.Scope.ProcessSideEffectCount);
            Assert.AreEqual(0, evidence.Scope.PromotionSideEffectCount);
        }
    }

    private sealed class FinalRenderFixture : IDisposable
    {
        private int _compileCount;
        private int _processCount;
        private int _promotionCount;

        internal FinalRenderFixture()
        {
            RootPath = CreateIsolatedRoot(nameof(AC004_Security_UnapprovedFinal));
            string databasePath = Path.Combine(RootPath, "store.db");
            string backupDirectory = Path.Combine(RootPath, "backups");
            Directory.CreateDirectory(backupDirectory);
            _ = DatabaseMigrator.Migrate(
                databasePath,
                backupDirectory);

            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            ContentDigest assetDigest = Digest('1');
            var asset = new Asset(
                assetId,
                "final-input.mp4",
                "video/mp4",
                assetDigest,
                128,
                new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    "AC004 internal final-render fixture only.",
                    externalPublicationAllowed: false));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "AC004 final-render project",
                "Unapproved finals must not render.",
                "Acceptance operators",
                PublicationScope.InternalOnly,
                deadline: null,
                [asset],
                performers: []);
            var projectStore = new ProjectStore(databasePath);
            projectStore.CreateProject(Project, "{\"ac004\":true}", CreatedAt);
            var range = new TimeRange(default, new RationalTime(1, 1));
            OriginRef origin = OriginRef.FromAsset(assetId);
            var element = new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                range,
                origin);
            Version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                Project.Id,
                parentVersionId: null,
                "S-1-5-21-4004-3000-2000-3001",
                CreatedAt.AddMinutes(1),
                "Create AC004 final-render fixture version.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            projectStore.AppendCompositionVersion(Version, "{\"ac004\":true}");

            var timeline = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                Version.Id,
                [new TimelineMapEntry(range, origin, sourceRange: null)]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            var template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "AC004 final template",
                Digest('2'));
            var layout = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [
                    new LayoutRegion(
                        "main",
                        new NormalizedRect(
                            default,
                            default,
                            new RationalNumber(1, 1),
                            new RationalNumber(1, 1)),
                        LayoutRegionRole.Video),
                ],
                safeZones: []);
            ContentDigest layoutDigest = Digest('3');
            var profile = new RenderProfile(
                EntityId<RenderProfileIdTag>.New(),
                "AC004 final profile",
                Digest('4'),
                1920,
                1080,
                30,
                1,
                layout.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: false);
            EntityId<RenderInputIdTag> inputId = EntityId<RenderInputIdTag>.New();
            var graphInput = new RenderGraphInput(
                inputId,
                RenderInputKind.Video,
                assetDigest,
                asset.ByteSize,
                origin);
            RenderNode videoNode = RenderNode.Video(
                EntityId<RenderNodeIdTag>.New(),
                inputId,
                range,
                origin,
                sourceRange: null,
                "main");
            RenderNode outputNode = RenderNode.Output(
                EntityId<RenderNodeIdTag>.New(),
                [videoNode.Id],
                range);
            Graph = new RenderGraph(
                EntityId<RenderGraphIdTag>.New(),
                Version.Id,
                Version.ContentDigest,
                timeline.Id,
                timelineDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutDigest,
                template,
                [graphInput],
                fonts: [],
                [videoNode, outputNode],
                outputNode.Id);
            ValidationContext = new RenderGraphValidationContext(
                Project,
                Version,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                sourceSpans: [],
                editorialRecordIds: [],
                [new RenderArtifactEvidence(inputId, assetDigest, asset.ByteSize)],
                fonts: []);

            ToolBinding = new FfmpegToolBinding(
                new BinaryVerificationRequest(
                    "ffmpeg",
                    Path.Combine(RootPath, "ffmpeg.exe"),
                    new ContentDigest(FfmpegCompiler.FixedFfmpegSha256),
                    FfmpegCompiler.FixedFfmpegByteSize));
            InputBinding = new FfmpegInputBinding(
                inputId,
                new BinaryVerificationRequest(
                    "input.video",
                    Path.Combine(RootPath, "input.mp4"),
                    assetDigest,
                    asset.ByteSize));
            string stagingDirectory = Path.Combine(
                RootPath,
                "projects",
                Project.Id.ToString(),
                "staging");
            Directory.CreateDirectory(stagingDirectory);
            StagingOutputPath = Path.Combine(stagingDirectory, "final.mp4");
            OutputArtifactId = EntityId<ArtifactIdTag>.New();
            PolicyDigest = Digest('6');

            var jobStore = new JobStore(databasePath);
            var timeProvider = new IncrementingTimeProvider(CreatedAt.AddHours(1));
            Job = new Job(
                EntityId<JobIdTag>.New(),
                Project.Id,
                FinalRenderService.JobKind,
                JobState.Queued,
                CreatedAt.AddMinutes(3),
                CreatedAt.AddMinutes(3),
                currentAttemptNumber: 0);
            jobStore.CreateJob(Job);
            var runner = new LocalJobRunner(jobStore, timeProvider);
            var coordinator = new JobCoordinator(jobStore, runner, timeProvider);
            Service = new FinalRenderService(
                coordinator,
                EvaluateApprovals,
                RenderGraphValidator.Validate,
                CompileAsync,
                RunProcessAsync,
                VerifyOutputAsync,
                _ => null,
                ValidateArtifactAsync,
                PromoteAsync);
        }

        internal string RootPath { get; }

        internal Project Project { get; }

        internal CompositionVersion Version { get; }

        internal RenderGraph Graph { get; }

        internal RenderGraphValidationContext ValidationContext { get; }

        internal FfmpegToolBinding ToolBinding { get; }

        internal FfmpegInputBinding InputBinding { get; }

        internal string StagingOutputPath { get; }

        internal EntityId<ArtifactIdTag> OutputArtifactId { get; }

        internal ContentDigest PolicyDigest { get; }

        internal Job Job { get; }

        internal FinalRenderService Service { get; }

        internal int CompileCount => Volatile.Read(ref _compileCount);

        internal int ProcessCount => Volatile.Read(ref _processCount);

        internal int PromotionCount => Volatile.Read(ref _promotionCount);

        internal FinalRenderRequest CreateRequest(
            ContentDigest? approvedDigest = null,
            IReadOnlyList<StoredApproval>? approvalEvidence = null) =>
            new(
                Job.Id,
                Project.Id,
                OutputArtifactId,
                Version,
                approvedDigest ?? Version.ContentDigest,
                approvalEvidence ?? [],
                CreatePolicySnapshot(),
                Graph,
                ValidationContext,
                ToolBinding,
                [InputBinding],
                StagingOutputPath,
                "final.mp4",
                RootPath,
                TimeSpan.FromMinutes(5),
                Digest('5'),
                expectedOutputByteSize: 256,
                CreatedAt.AddMinutes(4));

        internal StoredApproval CreateApproval() =>
            new(
                EntityId<ApprovalIdTag>.New(),
                Project.Id,
                Version.Id,
                Version.ContentDigest,
                "S-1-5-21-1000-1000-1000-1004",
                ReviewActorRole.ContentApprover,
                CreatedAt.AddMinutes(2),
                "Synthetic AC004 fixture; not a human decision.");

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private FinalRenderPolicySnapshot CreatePolicySnapshot() =>
            new(
                PolicyDigest,
                PolicyDigest,
                Project.Id,
                Version,
                Graph,
                ValidationContext);

        private static ApprovalValidityResult EvaluateApprovals(
            CompositionVersion compositionVersion,
            IReadOnlyList<StoredApproval> approvalEvidence) =>
            new(validApprovals: approvalEvidence, staleApprovals: []);

        private Task<FfmpegCompilation> CompileAsync(
            FfmpegCompileRequest request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _compileCount);
            return Task.FromException<FfmpegCompilation>(
                new InvalidOperationException(
                    "The AC004 unapproved final must fail before compilation."));
        }

        private Task<ProcessRunResult> RunProcessAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _processCount);
            return Task.FromResult(new ProcessRunResult(
                ExitCode: 0,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                Truncated: false,
                Duration: TimeSpan.Zero));
        }

        private static Task VerifyOutputAsync(
            string outputPath,
            ContentDigest expectedDigest,
            long expectedByteSize,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private static Task<StoredArtifact> ValidateArtifactAsync(
            EntityId<ArtifactIdTag> artifactId,
            CancellationToken cancellationToken) =>
            Task.FromException<StoredArtifact>(
                new InvalidOperationException(
                    "The AC004 unapproved final must not validate artifacts."));

        private Task<StoredArtifact> PromoteAsync(
            ArtifactPromotionRequest request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _promotionCount);
            return Task.FromException<StoredArtifact>(
                new InvalidOperationException(
                    "The AC004 unapproved final must not promote artifacts."));
        }
    }

    private sealed class IncrementingTimeProvider(DateTimeOffset initialValue)
        : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _value = initialValue;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                _value = _value.AddTicks(1);
                return _value;
            }
        }
    }

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private static string CreateIsolatedRoot(string testName)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.Q004.Acceptance",
            testName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record AcceptanceEvidence(
        string Requirement,
        ScopeEvidence Scope);

    private sealed record ScopeEvidence(
        string Scenario,
        string FailureReason,
        int CompileSideEffectCount,
        int ProcessSideEffectCount,
        int PromotionSideEffectCount);
}
