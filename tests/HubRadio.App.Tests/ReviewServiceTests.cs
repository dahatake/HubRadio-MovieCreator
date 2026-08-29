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
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ReviewServiceTests
{
    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-2001";
    private const string ReviewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-2002";
    private const string ViewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-2003";

    private static readonly DateTimeOffset VersionTime =
        new(2032, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [TestMethod]
    public void AddTimecodeCommentDerivesSourceSpanAndPreservesSqlUnicodeText()
    {
        using ReviewFixture fixture = new();
        fixture.Assign(ReviewerSid, AuthorizationRole.PerformerReviewer);
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        TimelineMap timelineMap = CreateSourceTimelineMap(
            fixture.Version.Id,
            sourceSpanId);
        EntityId<ReviewCommentIdTag> commentId = EntityId<ReviewCommentIdTag>.New();
        RationalTime outputTime = new(3, 2);
        const string text = "字幕を修正: Robert'); DROP TABLE review_comments;-- 🎬";

        ReviewComment comment = fixture.Service.AddTimecodeComment(
            commentId,
            fixture.Version,
            timelineMap,
            outputTime,
            ReviewerSid,
            ReviewActorRole.PerformerReviewer,
            VersionTime.AddMinutes(1),
            text);

        Assert.AreEqual(commentId, comment.Id);
        Assert.AreEqual(fixture.Version.Id, comment.CompositionVersionId);
        Assert.AreEqual(fixture.Version.ContentDigest, comment.CompositionVersionDigest);
        Assert.AreEqual(outputTime, comment.OutputTime);
        Assert.AreEqual(sourceSpanId, comment.SourceSpanId);
        StoredReviewComment stored = fixture.GetEvidence().ReviewComments.Single();
        Assert.AreEqual(sourceSpanId, stored.SourceSpanId);
        Assert.AreEqual(text, stored.Text);
        Assert.AreEqual(
            1L,
            fixture.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'review_comments';"));
    }

    [TestMethod]
    public void AddTimecodeCommentRejectsAssetEditorialOutOfRangeAndDifferentVersionMap()
    {
        using ReviewFixture fixture = new();
        fixture.Assign(ReviewerSid, AuthorizationRole.PerformerReviewer);

        OriginRef[] unsupportedOrigins =
        [
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()),
            OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New()),
        ];
        foreach (OriginRef origin in unsupportedOrigins)
        {
            TimelineMap unsupportedMap = CreateNonSourceTimelineMap(
                fixture.Version.Id,
                origin);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                fixture.Service.AddTimecodeComment(
                    EntityId<ReviewCommentIdTag>.New(),
                    fixture.Version,
                    unsupportedMap,
                    default,
                    ReviewerSid,
                    ReviewActorRole.PerformerReviewer,
                    VersionTime.AddMinutes(1),
                    "Unsupported origin."));
        }

        TimelineMap sourceMap = CreateSourceTimelineMap(
            fixture.Version.Id,
            EntityId<SourceSpanIdTag>.New());
        ArgumentOutOfRangeException outside =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.AddTimecodeComment(
                    EntityId<ReviewCommentIdTag>.New(),
                    fixture.Version,
                    sourceMap,
                    new RationalTime(10, 1),
                    ReviewerSid,
                    ReviewActorRole.PerformerReviewer,
                    VersionTime.AddMinutes(1),
                    "End boundary is outside."));
        Assert.AreEqual("outputTime", outside.ParamName);

        TimelineMap otherVersionMap = CreateSourceTimelineMap(
            EntityId<CompositionVersionIdTag>.New(),
            EntityId<SourceSpanIdTag>.New());
        ArgumentException wrongVersion = Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Service.AddTimecodeComment(
                EntityId<ReviewCommentIdTag>.New(),
                fixture.Version,
                otherVersionMap,
                default,
                ReviewerSid,
                ReviewActorRole.PerformerReviewer,
                VersionTime.AddMinutes(1),
                "Wrong version map."));
        Assert.AreEqual("timelineMap", wrongVersion.ParamName);
        Assert.AreEqual(0, fixture.GetEvidence().ReviewComments.Count);
    }

    [TestMethod]
    public void AddTimecodeCommentRequiresCurrentReviewRoleAndRejectsViewerOrAdminBypass()
    {
        using ReviewFixture fixture = new();
        fixture.Assign(ViewerSid, AuthorizationRole.Viewer);
        TimelineMap timelineMap = CreateSourceTimelineMap(
            fixture.Version.Id,
            EntityId<SourceSpanIdTag>.New());

        AuthorizationException viewer = Assert.ThrowsExactly<AuthorizationException>(() =>
            fixture.Service.AddTimecodeComment(
                EntityId<ReviewCommentIdTag>.New(),
                fixture.Version,
                timelineMap,
                default,
                ViewerSid,
                ReviewActorRole.PerformerReviewer,
                VersionTime.AddMinutes(1),
                "A Viewer cannot impersonate a reviewer."));
        Assert.AreEqual(
            AuthorizationOperation.RecordPerformerReview,
            viewer.Operation);
        Assert.AreEqual(AuthorizationRole.PerformerReviewer, viewer.ActingRole);

        ArgumentException administrator = Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Service.AddTimecodeComment(
                EntityId<ReviewCommentIdTag>.New(),
                fixture.Version,
                timelineMap,
                default,
                AdministratorSid,
                ReviewActorRole.Administrator,
                VersionTime.AddMinutes(1),
                "Administrator is not a review role."));
        Assert.AreEqual("actorRole", administrator.ParamName);
        Assert.AreEqual(0, fixture.GetEvidence().ReviewComments.Count);
    }

    [TestMethod]
    public void AddTimecodeCommentAllowsExplicitRightsReviewerAssignment()
    {
        using ReviewFixture fixture = new();
        fixture.Assign(ReviewerSid, AuthorizationRole.RightsApprover);
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();

        ReviewComment comment = fixture.Service.AddTimecodeComment(
            EntityId<ReviewCommentIdTag>.New(),
            fixture.Version,
            CreateSourceTimelineMap(fixture.Version.Id, sourceSpanId),
            default,
            ReviewerSid,
            ReviewActorRole.RightsApprover,
            VersionTime.AddMinutes(1),
            "権利確認が必要です。");

        Assert.AreEqual(ReviewActorRole.RightsApprover, comment.ActorRole);
        Assert.AreEqual(sourceSpanId, fixture.GetEvidence().ReviewComments.Single().SourceSpanId);
    }

    [TestMethod]
    public void RequestChangesPersistsCommentAndStateRecordWithExactDecisionRole()
    {
        using ReviewFixture fixture = new();
        fixture.Assign(ReviewerSid, AuthorizationRole.ContentApprover);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        fixture.ReviewStore.AppendReviewRecord(
            CreateStateRecord(
                fixture.Version,
                ReviewState.ContentReview,
                ReviewActorRole.ContentApprover,
                currentTime));
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        EntityId<ReviewCommentIdTag> commentId = EntityId<ReviewCommentIdTag>.New();
        EntityId<ReviewRecordIdTag> recordId = EntityId<ReviewRecordIdTag>.New();
        DateTimeOffset requestTime = currentTime.AddSeconds(1);
        const string text = "00:03 の固有名詞を「ハブラジオ」に修正してください。";

        ReviewRecord record = fixture.Service.RequestChanges(
            commentId,
            recordId,
            fixture.Version,
            CreateSourceTimelineMap(fixture.Version.Id, sourceSpanId),
            ReviewState.ContentReview,
            new RationalTime(3, 1),
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            requestTime,
            text);

        Assert.AreEqual(recordId, record.Id);
        Assert.AreEqual(ReviewState.ChangesRequested, record.State);
        Assert.AreEqual(fixture.Version.ContentDigest, record.CompositionVersionDigest);
        StoredVersionEvidence evidence = fixture.GetEvidence();
        Assert.AreEqual(2, evidence.ReviewRecords.Count);
        Assert.AreEqual(ReviewState.ChangesRequested, evidence.ReviewRecords[^1].State);
        StoredReviewComment comment = evidence.ReviewComments.Single();
        Assert.AreEqual(commentId, comment.Id);
        Assert.AreEqual(sourceSpanId, comment.SourceSpanId);
        Assert.AreEqual(requestTime, comment.CreatedAt);
        Assert.AreEqual(ReviewerSid, comment.ActorId);
        Assert.AreEqual(text, comment.Text);
    }

    [TestMethod]
    public void RequestChangesRejectsIllegalStateAndMissingExactAssignment()
    {
        using (ReviewFixture illegalStateFixture = new())
        {
            DateTimeOffset currentTime = VersionTime.AddMinutes(1);
            illegalStateFixture.ReviewStore.AppendReviewRecord(
                CreateStateRecord(
                    illegalStateFixture.Version,
                    ReviewState.QualityControl,
                    ReviewActorRole.Editor,
                    currentTime));

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                illegalStateFixture.Service.RequestChanges(
                    EntityId<ReviewCommentIdTag>.New(),
                    EntityId<ReviewRecordIdTag>.New(),
                    illegalStateFixture.Version,
                    CreateSourceTimelineMap(
                        illegalStateFixture.Version.Id,
                        EntityId<SourceSpanIdTag>.New()),
                    ReviewState.QualityControl,
                    default,
                    AdministratorSid,
                    ReviewActorRole.Editor,
                    currentTime.AddSeconds(1),
                    "RequestChanges is illegal from quality control."));
            Assert.AreEqual(0, illegalStateFixture.GetEvidence().ReviewComments.Count);
            Assert.AreEqual(1, illegalStateFixture.GetEvidence().ReviewRecords.Count);
        }

        using ReviewFixture missingRoleFixture = new();
        missingRoleFixture.Assign(ReviewerSid, AuthorizationRole.BrandApprover);
        DateTimeOffset contentReviewTime = VersionTime.AddMinutes(1);
        missingRoleFixture.ReviewStore.AppendReviewRecord(
            CreateStateRecord(
                missingRoleFixture.Version,
                ReviewState.ContentReview,
                ReviewActorRole.ContentApprover,
                contentReviewTime));

        AuthorizationException missingRole =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                missingRoleFixture.Service.RequestChanges(
                    EntityId<ReviewCommentIdTag>.New(),
                    EntityId<ReviewRecordIdTag>.New(),
                    missingRoleFixture.Version,
                    CreateSourceTimelineMap(
                        missingRoleFixture.Version.Id,
                        EntityId<SourceSpanIdTag>.New()),
                    ReviewState.ContentReview,
                    default,
                    ReviewerSid,
                    ReviewActorRole.BrandApprover,
                    contentReviewTime.AddSeconds(1),
                    "A brand role cannot act as content approver."));
        Assert.AreEqual(AuthorizationOperation.RecordContentReview, missingRole.Operation);
        Assert.AreEqual(AuthorizationRole.ContentApprover, missingRole.ActingRole);
        Assert.AreEqual(0, missingRoleFixture.GetEvidence().ReviewComments.Count);
        Assert.AreEqual(1, missingRoleFixture.GetEvidence().ReviewRecords.Count);
    }

    [TestMethod]
    public void StoredVersionDigestMismatchRejectsStandaloneAndAtomicWrites()
    {
        using ReviewFixture fixture = new();
        fixture.Assign(ReviewerSid, AuthorizationRole.ContentApprover);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        fixture.ReviewStore.AppendReviewRecord(
            CreateStateRecord(
                fixture.Version,
                ReviewState.ContentReview,
                ReviewActorRole.ContentApprover,
                currentTime));
        CompositionVersion forgedVersion = new(
            fixture.Version.Id,
            fixture.Version.ProjectId,
            fixture.Version.ParentVersionId,
            fixture.Version.CreatedByActorId,
            fixture.Version.CreatedAt,
            fixture.Version.ChangeReason,
            new ContentDigest(new string('f', 64)),
            fixture.Version.Elements);
        TimelineMap timelineMap = CreateSourceTimelineMap(
            forgedVersion.Id,
            EntityId<SourceSpanIdTag>.New());

        SqliteException standalone = Assert.ThrowsExactly<SqliteException>(() =>
            fixture.Service.AddTimecodeComment(
                EntityId<ReviewCommentIdTag>.New(),
                forgedVersion,
                timelineMap,
                default,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                currentTime.AddSeconds(1),
                "Digest mismatch."));
        Assert.AreEqual(19, standalone.SqliteErrorCode);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.RequestChanges(
                EntityId<ReviewCommentIdTag>.New(),
                EntityId<ReviewRecordIdTag>.New(),
                forgedVersion,
                timelineMap,
                ReviewState.ContentReview,
                default,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                currentTime.AddSeconds(2),
                "Atomic digest mismatch."));

        StoredVersionEvidence evidence = fixture.GetEvidence();
        Assert.AreEqual(0, evidence.ReviewComments.Count);
        Assert.AreEqual(1, evidence.ReviewRecords.Count);
    }

    private static ReviewRecord CreateStateRecord(
        CompositionVersion version,
        ReviewState state,
        ReviewActorRole actorRole,
        DateTimeOffset recordedAt)
    {
        return new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            state,
            AdministratorSid,
            actorRole,
            recordedAt,
            "Persist current review state.");
    }

    private static TimelineMap CreateSourceTimelineMap(
        EntityId<CompositionVersionIdTag> versionId,
        EntityId<SourceSpanIdTag> sourceSpanId)
    {
        return new TimelineMap(
            EntityId<TimelineMapIdTag>.New(),
            versionId,
            [
                new TimelineMapEntry(
                    new TimeRange(default, new RationalTime(10, 1)),
                    OriginRef.FromSourceSpan(sourceSpanId),
                    new TimeRange(
                        new RationalTime(20, 1),
                        new RationalTime(10, 1))),
            ]);
    }

    private static TimelineMap CreateNonSourceTimelineMap(
        EntityId<CompositionVersionIdTag> versionId,
        OriginRef origin)
    {
        return new TimelineMap(
            EntityId<TimelineMapIdTag>.New(),
            versionId,
            [
                new TimelineMapEntry(
                    new TimeRange(default, new RationalTime(10, 1)),
                    origin,
                    sourceRange: null),
            ]);
    }

    private sealed class ReviewFixture : IDisposable
    {
        internal ReviewFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ReviewServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));

            var projectStore = new ProjectStore(DatabasePath);
            Project project = new(
                EntityId<ProjectIdTag>.New(),
                "Review service project",
                "Verify S-009.",
                "Reviewers",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            projectStore.CreateProject(project, "{\"project\":1}", VersionTime);
            Version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                project.Id,
                parentVersionId: null,
                AdministratorSid,
                VersionTime,
                "Create review candidate.",
                new ContentDigest(new string('a', 64)),
                elements: []);
            projectStore.AppendCompositionVersion(Version, "{\"version\":1}");

            ReviewStore = new ReviewStore(DatabasePath);
            var authorization = new AuthorizationService(
                new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AdministratorSid));
            Authorization = authorization;
            Service = new ReviewService(ReviewStore, authorization);
        }

        private string RootPath { get; }

        private string DatabasePath { get; }

        internal CompositionVersion Version { get; }

        internal ReviewStore ReviewStore { get; }

        internal AuthorizationService Authorization { get; }

        internal ReviewService Service { get; }

        internal void Assign(string actorSid, AuthorizationRole role)
        {
            Authorization.AssignRole(
                actorSid,
                role,
                AdministratorSid,
                $"Assign {role} for the review-service test.");
        }

        internal StoredVersionEvidence GetEvidence() =>
            ReviewStore.GetVersionEvidence(Version.Id)
                ?? throw new AssertFailedException("The version evidence was not loaded.");

        internal long ExecuteScalarInt64(string sql)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The scalar query returned null."));
        }

        private SqliteConnection OpenConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            var connection = new SqliteConnection(builder.ToString())
            {
                DefaultTimeout = 5,
            };
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}