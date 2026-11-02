using HubRadio.App.Compositions;
using HubRadio.App.Policy;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AC004_Security_ExternalMisoutput
{
    private const string AdministratorSid =
        "S-1-5-21-4004-2000-2000-3000";
    private const string EditorSid =
        "S-1-5-21-4004-2000-2000-3001";
    private const string ApproverSid =
        "S-1-5-21-4004-2000-2000-3002";

    private static readonly DateTimeOffset CreatedAt =
        new(2046, 4, 4, 5, 6, 7, TimeSpan.Zero);

    [TestMethod]
    // NFR-SEC-001 AC-004: 社内限定の成果物は外部 destination への出力・投稿の前に拒否される。
    public void InternalOnlyAndExternalRightsBlockExternalAuthorizationBeforePosting()
    {
        using var fixture = new ExternalPolicyFixture(
            PublicationScope.InternalOnly,
            RightsStatus.Approved,
            externalPublicationAllowed: false);

        ExportPolicyDecision decision = fixture.Policy.EvaluateAndAuthorize(
            fixture.CreateExternalExportRequest());

        Assert.IsFalse(decision.IsAuthorized);
        Assert.IsNull(decision.Authorization);
        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.DestinationNotAllowed));
        Assert.IsTrue(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.RightsNotSatisfied));
        Assert.IsFalse(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.ActorNotAuthorized));
        Assert.IsFalse(decision.IssueCodes.HasFlag(
            ExportPolicyIssueCode.RequiredApprovalMissing));

        // Posting runs only inside Consume, which requires the authorization that was not issued.
        int externalSideEffects = 0;
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            fixture.Policy.Consume(
                decision.Authorization!,
                fixture.CreateExternalExportRequest(),
                _ => ++externalSideEffects));
        Assert.AreEqual(0, externalSideEffects);

        // Positive control: the same fixture is authorized once external scope and rights are approved.
        using var approvedFixture = new ExternalPolicyFixture(
            PublicationScope.ExternalPublicationApproved,
            RightsStatus.Approved,
            externalPublicationAllowed: true);
        ExportPolicyDecision approved = approvedFixture.Policy.EvaluateAndAuthorize(
            approvedFixture.CreateExternalExportRequest());
        Assert.AreEqual(ExportPolicyIssueCode.None, approved.IssueCodes);
        Assert.IsNotNull(approved.Authorization);

        var evidence = new AcceptanceEvidence(
            "Q-004",
            new ScopeEvidence(
                PublicationScope.InternalOnly.ToString(),
                ExternalPublicationAllowed: false,
                AuthorizationIssued: decision.Authorization is not null,
                ExternalSideEffectCount: externalSideEffects));
        Assert.AreEqual("Q-004", evidence.Requirement);
        Assert.AreEqual(PublicationScope.InternalOnly.ToString(), evidence.Scope.Scope);
        Assert.IsFalse(evidence.Scope.ExternalPublicationAllowed);
        Assert.IsFalse(evidence.Scope.AuthorizationIssued);
        Assert.AreEqual(0, evidence.Scope.ExternalSideEffectCount);
    }

    private sealed class ExternalPolicyFixture : IDisposable
    {
        private readonly string _rootPath;
        private readonly ArtifactStore _artifactStore;
        private ExportRenderQcBinding? _renderQcBinding;

        internal ExternalPolicyFixture(
            PublicationScope publicationScope,
            RightsStatus rightsStatus,
            bool externalPublicationAllowed)
        {
            _rootPath = CreateIsolatedRoot(nameof(AC004_Security_ExternalMisoutput));
            string databasePath = Path.Combine(_rootPath, "store.db");
            string backupDirectory = Path.Combine(_rootPath, "backups");
            Directory.CreateDirectory(backupDirectory);
            _ = DatabaseMigrator.Migrate(
                databasePath,
                backupDirectory);

            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            ContentDigest assetDigest = Digest('1');
            Asset = new Asset(
                assetId,
                "internal-only-input.mp4",
                "video/mp4",
                assetDigest,
                128,
                new RightsRecord(
                    rightsStatus,
                    rightsStatus == RightsStatus.Approved
                        ? "approved-internal-use-only"
                        : null,
                    "AC004 internal-only fixture rights.",
                    externalPublicationAllowed));
            Project = new Project(
                EntityId<ProjectIdTag>.New(),
                "AC004 internal-only project",
                "External output must remain blocked.",
                "Acceptance operators",
                publicationScope,
                deadline: null,
                [Asset],
                performers: []);

            var projectStore = new ProjectStore(databasePath);
            projectStore.CreateProject(
                Project,
                ProjectSnapshotJson.WriteProject(Project),
                CreatedAt);

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
                EditorSid,
                CreatedAt.AddMinutes(1),
                "Create AC004 external-output fixture.",
                RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                [element]);
            projectStore.AppendCompositionVersion(
                Version,
                CompositionSnapshotJson.WriteVersion(Version));

            var timeline = new TimelineMap(
                EntityId<TimelineMapIdTag>.New(),
                Version.Id,
                [new TimelineMapEntry(range, origin, sourceRange: null)]);
            ContentDigest timelineDigest =
                RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
            var template = new TemplateReference(
                EntityId<TemplateIdTag>.New(),
                "AC004 template",
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
                "AC004 profile",
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
                Asset.ByteSize,
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
                [new RenderArtifactEvidence(inputId, assetDigest, Asset.ByteSize)],
                fonts: []);

            ReviewStore reviewStore = new(databasePath);
            reviewStore.AppendReviewRecord(new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                Version,
                ReviewState.QualityControl,
                EditorSid,
                ReviewActorRole.Editor,
                CreatedAt.AddMinutes(2),
                "Persist approved review state for AC004 export gate."));
            reviewStore.AppendApproval(new Approval(
                EntityId<ApprovalIdTag>.New(),
                Version,
                ApproverSid,
                ReviewActorRole.ContentApprover,
                CreatedAt.AddMinutes(3),
                "Approve the exact AC004 fixture composition."));

            var authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AdministratorSid));
            authorization.AssignRole(
                EditorSid,
                AuthorizationRole.Editor,
                AdministratorSid,
                "Assign editor for AC004 export-policy evaluation.");

            _artifactStore = new ArtifactStore(databasePath, _rootPath);
            Policy = new ExportPolicy(
                projectStore,
                reviewStore,
                new ApprovalService(reviewStore, authorization),
                authorization,
                _artifactStore);
        }

        internal Asset Asset { get; }

        internal Project Project { get; }

        internal CompositionVersion Version { get; }

        internal RenderGraph Graph { get; }

        internal RenderGraphValidationContext ValidationContext { get; }

        internal ExportPolicy Policy { get; }

        internal ExportPolicyRequest CreateExternalExportRequest() =>
            new(
                EntityId<ExportOperationIdTag>.New(),
                ExportPolicyBoundary.Export,
                EditorSid,
                AuthorizationRole.Editor,
                Project,
                Version,
                new ExportDestinationBinding(
                    ReviewExportDestination.External,
                    Digest('5')),
                Graph,
                ValidationContext,
                rightsExceptions: [],
                performerEvidence: [],
                sensitivityEvidence: [],
                warnings: [],
                resolutions: [],
                CreateRegisteredRenderQcBinding());

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }

        private ExportRenderQcBinding CreateRegisteredRenderQcBinding()
        {
            if (_renderQcBinding is not null)
            {
                return _renderQcBinding;
            }

            byte[] bytes = [1, 4, 9, 16];
            ContentDigest digest = ContentDigest.Compute(bytes);
            string stagingDirectory = Path.Combine(
                _rootPath,
                "projects",
                Project.Id.ToString(),
                "staging");
            Directory.CreateDirectory(stagingDirectory);
            string stagingPath = Path.Combine(stagingDirectory, "render.mp4");
            File.WriteAllBytes(stagingPath, bytes);
            EntityId<ArtifactIdTag> artifactId = EntityId<ArtifactIdTag>.New();
            StoredArtifact stored = _artifactStore.PromoteAsync(
                    new ArtifactPromotionRequest(
                        artifactId,
                        Project.Id,
                        ArtifactKind.Render,
                        stagingPath,
                        "render.mp4",
                        digest,
                        bytes.LongLength,
                        CreatedAt.AddMinutes(4)))
                .GetAwaiter()
                .GetResult();
            Assert.AreEqual(digest, stored.ContentDigest);
            _renderQcBinding = new ExportRenderQcBinding(
                artifactId,
                Version,
                Graph,
                digest,
                bytes.LongLength,
                digest,
                Digest('b'));
            return _renderQcBinding;
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
        string Scope,
        bool ExternalPublicationAllowed,
        bool AuthorizationIssued,
        int ExternalSideEffectCount);
}
