using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ReviewStoreTests
{
    private static readonly DateTimeOffset VersionTime =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [TestMethod]
    public void AppendAndLoadAllVersionEvidenceReturnsTypedTimestampAndIdOrder()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset timestamp = VersionTime.AddMinutes(1);
        ReviewRecord laterIdRecord = CreateReviewRecord(
            version,
            EntityId<ReviewRecordIdTag>.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            timestamp,
            ReviewState.ChangesRequested,
            "Later ID record");
        ReviewRecord earlierIdRecord = CreateReviewRecord(
            version,
            EntityId<ReviewRecordIdTag>.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            timestamp,
            ReviewState.EditorReview,
            "Earlier ID record");
        ReviewComment laterComment = CreateReviewComment(
            version,
            EntityId<ReviewCommentIdTag>.New(),
            timestamp.AddSeconds(2),
            new RationalTime(15, 2),
            "Later comment");
        ReviewComment earlierComment = CreateReviewComment(
            version,
            EntityId<ReviewCommentIdTag>.New(),
            timestamp.AddSeconds(1),
            new RationalTime(3, 2),
            "Earlier comment");
        Approval laterApproval = CreateApproval(
            version,
            EntityId<ApprovalIdTag>.New(),
            timestamp.AddSeconds(4),
            ReviewActorRole.BrandApprover,
            "Brand approved");
        Approval earlierApproval = CreateApproval(
            version,
            EntityId<ApprovalIdTag>.New(),
            timestamp.AddSeconds(3),
            ReviewActorRole.ContentApprover,
            "Content approved");
        store.AppendReviewRecord(laterIdRecord);
        store.AppendReviewRecord(earlierIdRecord);
        store.AppendReviewComment(laterComment);
        store.AppendReviewComment(earlierComment);
        store.AppendApproval(laterApproval);
        store.AppendApproval(earlierApproval);

        StoredVersionEvidence evidence = store.GetVersionEvidence(version.Id)
            ?? throw new AssertFailedException("The version evidence was not loaded.");
        Assert.AreEqual(version.Id, evidence.CompositionVersionId);
        Assert.AreEqual(version.ProjectId, evidence.ProjectId);
        Assert.AreEqual(version.ContentDigest, evidence.CompositionVersionDigest);
        CollectionAssert.AreEqual(
            new[] { earlierIdRecord.Id, laterIdRecord.Id },
            evidence.ReviewRecords.Select(static item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { earlierComment.Id, laterComment.Id },
            evidence.ReviewComments.Select(static item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { earlierApproval.Id, laterApproval.Id },
            evidence.Approvals.Select(static item => item.Id).ToArray());

        StoredReviewRecord storedRecord = evidence.ReviewRecords[0];
        Assert.AreEqual(ReviewState.EditorReview, storedRecord.State);
        Assert.AreEqual("reviewer-'one'", storedRecord.ActorId);
        Assert.AreEqual(ReviewActorRole.Editor, storedRecord.ActorRole);
        Assert.AreEqual("Earlier ID record", storedRecord.Reason);
        StoredReviewComment storedComment = evidence.ReviewComments[0];
        Assert.AreEqual(new RationalTime(3, 2), storedComment.OutputTime);
        Assert.AreEqual(ReviewActorRole.PerformerReviewer, storedComment.ActorRole);
        Assert.AreEqual("Earlier comment", storedComment.Text);
        StoredApproval storedApproval = evidence.Approvals[0];
        Assert.AreEqual(ReviewActorRole.ContentApprover, storedApproval.ActorRole);
        Assert.AreEqual("Content approved", storedApproval.Reason);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredReviewRecord>)evidence.ReviewRecords).Clear());
        Assert.IsNull(store.GetVersionEvidence(EntityId<CompositionVersionIdTag>.New()));
    }

    [TestMethod]
    public void QuotedUserTextIsBoundExactlyAndCannotExecuteSql()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        const string quoted = "Robert'); DROP TABLE approvals;-- \"quoted\"";
        ReviewRecord record = new(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.EditorReview,
            quoted,
            ReviewActorRole.Editor,
            VersionTime.AddMinutes(1),
            quoted);
        ReviewComment comment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            default,
            EntityId<SourceSpanIdTag>.New(),
            quoted,
            ReviewActorRole.Editor,
            VersionTime.AddMinutes(2),
            quoted);
        Approval approval = new(
            EntityId<ApprovalIdTag>.New(),
            version,
            quoted,
            ReviewActorRole.RightsApprover,
            VersionTime.AddMinutes(3),
            quoted);
        AuditEvent auditEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ApprovalGranted,
            quoted,
            ReviewActorRole.RightsApprover,
            VersionTime.AddMinutes(4),
            quoted,
            version.ProjectId,
            version);
        store.AppendReviewRecord(record);
        store.AppendReviewComment(comment);
        store.AppendApproval(approval);
        store.AppendAuditEvent(auditEvent);

        StoredVersionEvidence evidence = store.GetVersionEvidence(version.Id)!;
        Assert.AreEqual(quoted, evidence.ReviewRecords[0].ActorId);
        Assert.AreEqual(quoted, evidence.ReviewRecords[0].Reason);
        Assert.AreEqual(quoted, evidence.ReviewComments[0].Text);
        Assert.AreEqual(quoted, evidence.Approvals[0].Reason);
        Assert.AreEqual(quoted, store.GetProjectAuditEvents(version.ProjectId)[0].Reason);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'approvals';"));
    }

    [TestMethod]
    public void DuplicateEvidenceIdsRollBackEachAppendWithoutReplacingRows()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        ReviewRecord record = CreateReviewRecord(
            version,
            EntityId<ReviewRecordIdTag>.New(),
            VersionTime.AddMinutes(1),
            ReviewState.Editing,
            "Editing");
        ReviewComment comment = CreateReviewComment(
            version,
            EntityId<ReviewCommentIdTag>.New(),
            VersionTime.AddMinutes(2),
            default,
            "Comment");
        Approval approval = CreateApproval(
            version,
            EntityId<ApprovalIdTag>.New(),
            VersionTime.AddMinutes(3),
            ReviewActorRole.ContentApprover,
            "Approved");
        AuditEvent auditEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ProjectRegistered,
            "auditor",
            ReviewActorRole.Administrator,
            VersionTime.AddMinutes(4),
            "Registered",
            version.ProjectId);
        store.AppendReviewRecord(record);
        store.AppendReviewComment(comment);
        store.AppendApproval(approval);
        store.AppendAuditEvent(auditEvent);

        AssertConstraintViolation(() => store.AppendReviewRecord(record));
        AssertConstraintViolation(() => store.AppendReviewComment(comment));
        AssertConstraintViolation(() => store.AppendApproval(approval));
        AssertConstraintViolation(() => store.AppendAuditEvent(auditEvent));

        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_records;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM audit_events;"));
    }

    [TestMethod]
    public void AllVersionBoundEvidenceRequiresExactProjectVersionAndDigest()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, ProjectStore projectStore, CompositionVersion storedVersion) =
            CreateStores(database);
        Project otherProject = CreateProject("Other evidence project");
        projectStore.CreateProject(otherProject, "{\"project\":2}", VersionTime);
        CompositionVersion wrongProjectVersion = new(
            storedVersion.Id,
            otherProject.Id,
            null,
            "actor",
            VersionTime,
            "Wrong project binding",
            storedVersion.ContentDigest,
            elements: []);
        CompositionVersion wrongDigestVersion = new(
            storedVersion.Id,
            storedVersion.ProjectId,
            null,
            "actor",
            VersionTime,
            "Wrong digest binding",
            new ContentDigest(new string('f', 64)),
            elements: []);

        foreach (CompositionVersion invalidVersion in new[]
        {
            wrongProjectVersion,
            wrongDigestVersion,
        })
        {
            AssertConstraintViolation(
                () => store.AppendReviewRecord(
                    CreateReviewRecord(
                        invalidVersion,
                        EntityId<ReviewRecordIdTag>.New(),
                        VersionTime.AddMinutes(1),
                        ReviewState.EditorReview,
                        "Invalid binding")));
            AssertConstraintViolation(
                () => store.AppendReviewComment(
                    CreateReviewComment(
                        invalidVersion,
                        EntityId<ReviewCommentIdTag>.New(),
                        VersionTime.AddMinutes(2),
                        default,
                        "Invalid binding")));
            AssertConstraintViolation(
                () => store.AppendApproval(
                    CreateApproval(
                        invalidVersion,
                        EntityId<ApprovalIdTag>.New(),
                        VersionTime.AddMinutes(3),
                        ReviewActorRole.ContentApprover,
                        "Invalid binding")));
            AssertConstraintViolation(
                () => store.AppendAuditEvent(
                    new AuditEvent(
                        EntityId<AuditEventIdTag>.New(),
                        AuditEventKind.CompositionExported,
                        "actor",
                        ReviewActorRole.Editor,
                        VersionTime.AddMinutes(4),
                        "Invalid binding",
                        invalidVersion.ProjectId,
                        invalidVersion)));
        }

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_records;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM audit_events;"));
    }

    [TestMethod]
    public void ProjectAuditEventsPreserveOptionalVersionPairAndSortByTimestampThenId()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset sameTime = VersionTime.AddHours(1);
        AuditEvent laterIdProjectEvent = new(
            EntityId<AuditEventIdTag>.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            AuditEventKind.ProjectRegistered,
            "administrator",
            ReviewActorRole.Administrator,
            sameTime,
            "Project only",
            version.ProjectId);
        AuditEvent earlierIdVersionEvent = new(
            EntityId<AuditEventIdTag>.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            AuditEventKind.CompositionExported,
            "editor",
            ReviewActorRole.Editor,
            sameTime,
            "Version bound",
            version.ProjectId,
            version);
        store.AppendAuditEvent(laterIdProjectEvent);
        store.AppendAuditEvent(earlierIdVersionEvent);

        IReadOnlyList<StoredAuditEvent> events =
            store.GetProjectAuditEvents(version.ProjectId);
        CollectionAssert.AreEqual(
            new[] { earlierIdVersionEvent.Id, laterIdProjectEvent.Id },
            events.Select(static item => item.Id).ToArray());
        Assert.AreEqual(version.Id, events[0].CompositionVersionId);
        Assert.AreEqual(version.ContentDigest, events[0].CompositionVersionDigest);
        Assert.IsNull(events[1].CompositionVersionId);
        Assert.IsNull(events[1].CompositionVersionDigest);
        Assert.AreEqual(0, store.GetProjectAuditEvents(EntityId<ProjectIdTag>.New()).Count);
    }

    [TestMethod]
    public void EvidenceLoadsNonUtcOffsetsAndRejectsInvalidTypedValues()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        ReviewRecord record = CreateReviewRecord(
            version,
            EntityId<ReviewRecordIdTag>.New(),
            VersionTime.AddMinutes(1),
            ReviewState.EditorReview,
            "Review");
        store.AppendReviewRecord(record);
        database.ExecuteNonQuery(
            "UPDATE review_records SET recorded_at = $recorded_at WHERE id = $id;",
            ("$recorded_at", "2032-03-04T14:07:07.0000000+09:00"),
            ("$id", record.Id.ToString()));

        Assert.AreEqual(
            TimeSpan.FromHours(9),
            store.GetVersionEvidence(version.Id)!.ReviewRecords[0].RecordedAt.Offset);

        database.ExecuteNonQuery(
            "UPDATE review_records SET state = $state WHERE id = $id;",
            ("$state", "not-a-review-state"),
            ("$id", record.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.GetVersionEvidence(version.Id));

        database.ExecuteNonQuery(
            "UPDATE review_records SET state = $state, recorded_at = $recorded_at WHERE id = $id;",
            ("$state", ReviewState.EditorReview.ToString()),
            ("$recorded_at", "2032-02-30T14:07:07.0000000+09:00"),
            ("$id", record.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.GetVersionEvidence(version.Id));
    }

    [TestMethod]
    public void NonCanonicalStoredRationalAndIncompleteAuditVersionPairFailClosed()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        ReviewComment comment = CreateReviewComment(
            version,
            EntityId<ReviewCommentIdTag>.New(),
            VersionTime.AddMinutes(1),
            new RationalTime(1, 2),
            "Comment");
        AuditEvent auditEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.CompositionExported,
            "actor",
            ReviewActorRole.Editor,
            VersionTime.AddMinutes(2),
            "Exported",
            version.ProjectId,
            version);
        store.AppendReviewComment(comment);
        store.AppendAuditEvent(auditEvent);
        database.ExecuteNonQuery(
            "UPDATE review_comments SET output_value = 2, output_rate = 4 WHERE id = $id;",
            ("$id", comment.Id.ToString()));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.GetVersionEvidence(version.Id));

        using (SqliteConnection connection = database.OpenConnection())
        {
            using SqliteCommand disableChecks = connection.CreateCommand();
            disableChecks.CommandText = "PRAGMA ignore_check_constraints = ON;";
            disableChecks.ExecuteNonQuery();
            using SqliteCommand corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                UPDATE audit_events
                SET composition_version_digest = NULL
                WHERE id = $id;
                """;
            corrupt.Parameters.AddWithValue("$id", auditEvent.Id.ToString());
            Assert.AreEqual(1, corrupt.ExecuteNonQuery());
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.GetProjectAuditEvents(version.ProjectId));
    }

    [TestMethod]
    public void StoredEvidenceTypesAreNarrowSnapshotsWithoutCompositionVersionConstruction()
    {
        foreach (Type type in new[]
        {
            typeof(StoredReviewRecord),
            typeof(StoredReviewComment),
            typeof(StoredApproval),
            typeof(StoredAuditEvent),
            typeof(StoredVersionEvidence),
        })
        {
            Assert.IsTrue(type.IsPublic);
            Assert.IsTrue(type.IsSealed);
            Assert.IsFalse(type.GetProperties().Any(
                static property => property.PropertyType == typeof(CompositionVersion)));
            Assert.IsFalse(type.GetConstructors().Any(
                static constructor => constructor.GetParameters().Any(
                    static parameter => parameter.ParameterType == typeof(CompositionVersion))));
        }
    }

    private static (ReviewStore ReviewStore, ProjectStore ProjectStore, CompositionVersion Version)
        CreateStores(StoreTestDatabase database)
    {
        var projectStore = new ProjectStore(database.DatabasePath);
        Project project = CreateProject("Review evidence project");
        projectStore.CreateProject(project, "{\"project\":1}", VersionTime);
        CompositionVersion version = new(
            EntityId<CompositionVersionIdTag>.New(),
            project.Id,
            null,
            "S-1-5-21-1000",
            VersionTime,
            "Create review candidate",
            new ContentDigest(new string('a', 64)),
            elements: []);
        projectStore.AppendCompositionVersion(version, "{\"version\":1}");
        return (new ReviewStore(database.DatabasePath), projectStore, version);
    }

    private static Project CreateProject(string contentName)
    {
        return new Project(
            EntityId<ProjectIdTag>.New(),
            contentName,
            "Review durable evidence.",
            "Reviewers",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
    }

    private static ReviewRecord CreateReviewRecord(
        CompositionVersion version,
        EntityId<ReviewRecordIdTag> id,
        DateTimeOffset recordedAt,
        ReviewState state,
        string reason)
    {
        return new ReviewRecord(
            id,
            version,
            state,
            "reviewer-'one'",
            ReviewActorRole.Editor,
            recordedAt,
            reason);
    }

    private static ReviewComment CreateReviewComment(
        CompositionVersion version,
        EntityId<ReviewCommentIdTag> id,
        DateTimeOffset createdAt,
        RationalTime outputTime,
        string text)
    {
        return new ReviewComment(
            id,
            version,
            outputTime,
            EntityId<SourceSpanIdTag>.New(),
            "performer-reviewer",
            ReviewActorRole.PerformerReviewer,
            createdAt,
            text);
    }

    private static Approval CreateApproval(
        CompositionVersion version,
        EntityId<ApprovalIdTag> id,
        DateTimeOffset approvedAt,
        ReviewActorRole actorRole,
        string reason)
    {
        return new Approval(
            id,
            version,
            "approver",
            actorRole,
            approvedAt,
            reason);
    }

    private static void AssertConstraintViolation(Action action)
    {
        SqliteException exception = Assert.ThrowsExactly<SqliteException>(action);
        Assert.AreEqual(19, exception.SqliteErrorCode);
    }
}