using HubRadio.App.Compositions;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ReviewWorkspaceServiceTests
{
    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6001";
    private const string ReviewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6002";
    private const string ViewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6003";
    private const string UnassignedSid =
        "S-1-5-21-1111111111-2222222222-3333333333-6004";

    private static readonly DateTimeOffset InitialTime =
        new(2036, 7, 8, 9, 10, 11, TimeSpan.Zero);

    [TestMethod]
    public void SnapshotReturnsDirectParentDiffAndKeepsRollbackAncestorApprovalStale()
    {
        using ReviewWorkspaceFixture fixture = new();

        ReviewWorkspaceSnapshot snapshot = fixture.Service.GetSnapshot(
            fixture.CurrentVersion.Id);

        Assert.IsTrue(snapshot.IsCurrentTarget);
        Assert.AreEqual(64, snapshot.Version.ContentDigest.Hex.Length);
        Assert.AreEqual(
            ReviewState.ContentReview,
            snapshot.CurrentReviewRecord?.State);
        ReviewApprovalView stale = snapshot.Approvals.Single();
        Assert.IsFalse(stale.IsValid);
        Assert.AreEqual(fixture.ParentVersion.Id, stale.Approval.CompositionVersionId);
        Assert.IsNotNull(snapshot.DirectParentDiff);
        Assert.AreEqual(
            fixture.ParentVersion.Id,
            snapshot.DirectParentDiff.FromVersionId);
        Assert.AreEqual(
            fixture.CurrentVersion.Id,
            snapshot.DirectParentDiff.ToVersionId);
        Assert.AreEqual(
            CompositionChangeKind.Modified,
            snapshot.DirectParentDiff.Changes.Single().Kind);
        Assert.IsTrue(snapshot.Actions.All(
            static action => action.Status == ReviewWorkspaceActionStatus.Authorized));
        Assert.IsTrue(snapshot.Actions.All(static action =>
            action.ActorRoles.SequenceEqual([ReviewActorRole.ContentApprover])));
    }

    [TestMethod]
    public void CommentChangeRequestAndApprovalUseNativeRoleAndPreventDuplicateApproval()
    {
        using ReviewWorkspaceFixture fixture = new();
        RationalTime commentTime = new(1, 2);

        ReviewWorkspaceSnapshot commented = fixture.Service.AddComment(
            fixture.CurrentVersion.Id,
            fixture.CurrentVersion.ContentDigest,
            ReviewActorRole.ContentApprover,
            commentTime,
            "字幕の語尾を確認してください。 🎬");

        StoredReviewComment comment = commented.Comments.Single();
        Assert.AreEqual(commentTime, comment.OutputTime);
        Assert.AreEqual(fixture.SourceSpanId, comment.SourceSpanId);
        Assert.AreEqual(ReviewActorRole.ContentApprover, comment.ActorRole);
        Assert.AreEqual(ReviewerSid, comment.ActorId);

        ReviewWorkspaceSnapshot approved = fixture.Service.RecordApproval(
            fixture.CurrentVersion.Id,
            fixture.CurrentVersion.ContentDigest,
            ReviewActorRole.ContentApprover,
            "内容を確認し承認します。");
        ReviewApprovalView valid = approved.Approvals.Single(
            static candidate => candidate.IsValid);
        Assert.AreEqual(ReviewActorRole.ContentApprover, valid.Approval.ActorRole);
        Assert.AreEqual(ReviewerSid, valid.Approval.ActorId);

        ReviewWorkspaceException duplicate =
            Assert.ThrowsExactly<ReviewWorkspaceException>(() =>
                fixture.Service.RecordApproval(
                    fixture.CurrentVersion.Id,
                    fixture.CurrentVersion.ContentDigest,
                    ReviewActorRole.ContentApprover,
                    "二重承認は記録しません。"));
        Assert.AreEqual(
            ReviewWorkspaceFailureReason.Conflict,
            duplicate.Reason);
        Assert.AreEqual(
            1,
            fixture.ReviewStore.GetVersionEvidence(
                fixture.CurrentVersion.Id)!.Approvals.Count);

        ReviewWorkspaceSnapshot changed = fixture.Service.RequestChanges(
            fixture.CurrentVersion.Id,
            fixture.CurrentVersion.ContentDigest,
            ReviewState.ContentReview,
            new RationalTime(1, 1),
            "00:01 の表記を修正してください。");
        Assert.AreEqual(
            ReviewState.ChangesRequested,
            changed.CurrentReviewRecord?.State);
        Assert.AreEqual(2, changed.Comments.Count);
        Assert.AreEqual(
            fixture.SourceSpanId,
            changed.Comments.Single(item =>
                item.Text == "00:01 の表記を修正してください。").SourceSpanId);
    }

    [TestMethod]
    public void MultiRoleActorRecordsEachNativeApprovalRoleAsASeparateEvent()
    {
        using ReviewWorkspaceFixture fixture = new();
        fixture.Assign(ReviewerSid, AuthorizationRole.RightsApprover);

        ReviewWorkspaceSnapshot contentApproved = fixture.Service.RecordApproval(
            fixture.CurrentVersion.Id,
            fixture.CurrentVersion.ContentDigest,
            ReviewActorRole.ContentApprover,
            "内容承認担当として承認します。");
        Assert.AreEqual(
            ReviewActorRole.ContentApprover,
            contentApproved.Approvals.Single(static candidate =>
                candidate.IsValid).Approval.ActorRole);

        ReviewWorkspaceSnapshot rightsApproved = fixture.Service.RecordApproval(
            fixture.CurrentVersion.Id,
            fixture.CurrentVersion.ContentDigest,
            ReviewActorRole.RightsApprover,
            "権利承認担当として承認します。");

        ReviewActorRole[] currentRoles = rightsApproved.Approvals
            .Where(candidate =>
                candidate.IsValid &&
                candidate.Approval.CompositionVersionId == fixture.CurrentVersion.Id &&
                candidate.Approval.ActorId == ReviewerSid)
            .Select(static candidate => candidate.Approval.ActorRole)
            .Order()
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                ReviewActorRole.ContentApprover,
                ReviewActorRole.RightsApprover,
            },
            currentRoles);

        ReviewWorkspaceException duplicate =
            Assert.ThrowsExactly<ReviewWorkspaceException>(() =>
                fixture.Service.RecordApproval(
                    fixture.CurrentVersion.Id,
                    fixture.CurrentVersion.ContentDigest,
                    ReviewActorRole.ContentApprover,
                    "記録済みの役割では再承認しません。"));
        Assert.AreEqual(ReviewWorkspaceFailureReason.Conflict, duplicate.Reason);
        Assert.AreEqual(
            2,
            fixture.ReviewStore.GetVersionEvidence(
                fixture.CurrentVersion.Id)!.Approvals.Count);
    }

    [TestMethod]
    public void AuthorizationPrecedesExistenceAndMutationsRejectNoncurrentVersion()
    {
        using ReviewWorkspaceFixture fixture = new();
        fixture.CurrentActorSid = UnassignedSid;

        ReviewWorkspaceException unauthorized =
            Assert.ThrowsExactly<ReviewWorkspaceException>(() =>
                fixture.Service.GetSnapshot(
                    EntityId<CompositionVersionIdTag>.New()));
        Assert.AreEqual(
            ReviewWorkspaceFailureReason.NotAuthorized,
            unauthorized.Reason);

        fixture.Assign(ViewerSid, AuthorizationRole.Viewer);
        fixture.CurrentActorSid = ViewerSid;
        ReviewWorkspaceException stale =
            Assert.ThrowsExactly<ReviewWorkspaceException>(() =>
                fixture.Service.AddComment(
                    fixture.ParentVersion.Id,
                    fixture.ParentVersion.ContentDigest,
                    ReviewActorRole.Editor,
                    default,
                    "古い版へは書き込みません。"));
        Assert.AreEqual(
            ReviewWorkspaceFailureReason.StaleVersion,
            stale.Reason);
        Assert.AreEqual(
            0,
            fixture.ReviewStore.GetVersionEvidence(
                fixture.ParentVersion.Id)!.ReviewComments.Count);
    }

    [TestMethod]
    public void UnrecordedAndPublicationBlockedStatesDoNotInventApprovalEligibility()
    {
        using ReviewWorkspaceFixture fixture = new(appendCurrentState: false);
        ReviewWorkspaceSnapshot unrecorded = fixture.Service.GetSnapshot(
            fixture.CurrentVersion.Id);

        Assert.IsNull(unrecorded.CurrentReviewRecord);
        Assert.AreEqual(
            ReviewWorkspaceActionStatus.ReviewStateUnrecorded,
            unrecorded.Actions.Single(action =>
                action.Kind == ReviewWorkspaceActionKind.RequestChanges).Status);
        Assert.AreEqual(
            ReviewWorkspaceActionStatus.Authorized,
            unrecorded.Actions.Single(action =>
                action.Kind == ReviewWorkspaceActionKind.RecordApproval).Status);

        fixture.AppendCurrentState(ReviewState.PublicationBlocked);
        ReviewWorkspaceSnapshot blocked = fixture.Service.GetSnapshot(
            fixture.CurrentVersion.Id);

        Assert.AreEqual(
            ReviewWorkspaceActionStatus.PublicationBlocked,
            blocked.Actions.Single(action =>
                action.Kind == ReviewWorkspaceActionKind.RequestChanges).Status);
        Assert.AreEqual(
            ReviewWorkspaceActionStatus.Authorized,
            blocked.Actions.Single(action =>
                action.Kind == ReviewWorkspaceActionKind.RecordApproval).Status);
        Assert.AreEqual(
            ReviewWorkspaceActionStatus.Authorized,
            blocked.Actions.Single(action =>
                action.Kind == ReviewWorkspaceActionKind.AddComment).Status);
    }

    private sealed class ReviewWorkspaceFixture : IDisposable
    {
        private readonly string rootPath;
        private readonly AuthorizationService authorization;
        private readonly ManualTimeProvider timeProvider =
            new(InitialTime.AddHours(1));

        internal ReviewWorkspaceFixture(bool appendCurrentState = true)
        {
            rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ReviewWorkspaceServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            string databasePath = Path.Combine(rootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(rootPath, "backups"));

            var projectStore = new ProjectStore(databasePath);
            var project = new Project(
                EntityId<ProjectIdTag>.New(),
                "Review workspace project",
                "Verify U-012 review orchestration.",
                "Reviewers",
                PublicationScope.ExternalCandidate,
                deadline: null,
                assets: [],
                performers: []);
            projectStore.CreateProject(project, "{\"project\":true}", InitialTime);
            SourceSpanId = EntityId<SourceSpanIdTag>.New();
            CompositionElement originalElement = CreateSourceElement(
                SourceSpanId,
                new RationalTime(2, 1));
            var workingCopy = new WorkingCopy(
                EntityId<WorkingCopyIdTag>.New(),
                project.Id,
                baseVersionId: null,
                InitialTime,
                InitialTime,
                [originalElement]);
            projectStore.CreateWorkingCopy(
                workingCopy,
                CompositionSnapshotJson.WriteWorkingCopy(workingCopy));
            var versionService = new VersionService(projectStore);
            CheckpointResult parent = versionService.Checkpoint(
                workingCopy,
                expectedRevision: 0,
                EntityId<CompositionVersionIdTag>.New(),
                AdministratorSid,
                InitialTime.AddMinutes(1),
                "Create parent review version.");
            ParentVersion = parent.Version;

            ReviewStore = new ReviewStore(databasePath);
            authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AdministratorSid));
            Assign(ReviewerSid, AuthorizationRole.ContentApprover);
            _ = new ApprovalService(ReviewStore, authorization).RecordApproval(
                EntityId<ApprovalIdTag>.New(),
                ParentVersion,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                InitialTime.AddMinutes(2),
                "Approve parent content.");

            CompositionElement changedElement = new(
                originalElement.Id,
                new TimeRange(default, new RationalTime(3, 1)),
                originalElement.Origin,
                adoptedSuggestion: null,
                new TimeRange(new RationalTime(10, 1), new RationalTime(3, 1)));
            var changedWorkingCopy = new WorkingCopy(
                parent.WorkingCopy.Id,
                project.Id,
                ParentVersion.Id,
                parent.WorkingCopy.CreatedAt,
                InitialTime.AddMinutes(3),
                [changedElement]);
            CheckpointResult current = versionService.Checkpoint(
                changedWorkingCopy,
                expectedRevision: 1,
                EntityId<CompositionVersionIdTag>.New(),
                AdministratorSid,
                InitialTime.AddMinutes(3),
                "Change source-backed content.");
            CurrentVersion = current.Version;
            if (appendCurrentState)
            {
                AppendCurrentState(ReviewState.ContentReview);
            }

            CurrentActorSid = ReviewerSid;
            Service = new ReviewWorkspaceService(
                projectStore,
                ReviewStore,
                authorization,
                () => CurrentActorSid,
                timeProvider);
        }

        internal string CurrentActorSid { get; set; }

        internal EntityId<SourceSpanIdTag> SourceSpanId { get; }

        internal CompositionVersion ParentVersion { get; }

        internal CompositionVersion CurrentVersion { get; }

        internal ReviewStore ReviewStore { get; }

        internal ReviewWorkspaceService Service { get; }

        internal void Assign(string actorSid, AuthorizationRole role)
        {
            authorization.AssignRole(
                actorSid,
                role,
                AdministratorSid,
                $"Assign {role} for review workspace tests.");
        }

        internal void AppendCurrentState(ReviewState state)
        {
            ReviewStore.AppendReviewRecord(new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                CurrentVersion,
                state,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                InitialTime.AddMinutes(4),
                $"Record {state} for the review workspace test."));
        }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        private static CompositionElement CreateSourceElement(
            EntityId<SourceSpanIdTag> sourceSpanId,
            RationalTime duration) =>
            new(
                EntityId<CompositionElementIdTag>.New(),
                new TimeRange(default, duration),
                OriginRef.FromSourceSpan(sourceSpanId),
                adoptedSuggestion: null,
                new TimeRange(new RationalTime(10, 1), duration));
    }

    private sealed class ManualTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
