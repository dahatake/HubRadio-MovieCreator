using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ReviewStoreTests
{
    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-3001";
    private const string ReviewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-3002";

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
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM audit_chain_entries;"));
    }

    [TestMethod]
    public void AppendChangeRequestAtomicallyPersistsBoundUnicodeSqlText()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        store.AppendReviewRecord(
            new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                version,
                ReviewState.PerformerReview,
                "performer-reviewer",
                ReviewActorRole.PerformerReviewer,
                currentTime,
                "Begin performer review."));
        const string text = "00:05 を修正: Robert'); DROP TABLE review_records;-- 🎙️";
        DateTimeOffset requestTime = currentTime.AddSeconds(1);
        (ReviewComment comment, ReviewRecord record) = CreateChangeRequest(
            version,
            ReviewActorRole.PerformerReviewer,
            "performer-reviewer",
            requestTime,
            text);

        store.AppendChangeRequest(
            comment,
            record,
            ReviewState.PerformerReview);

        StoredVersionEvidence evidence = store.GetVersionEvidence(version.Id)
            ?? throw new AssertFailedException("The version evidence was not loaded.");
        Assert.AreEqual(2, evidence.ReviewRecords.Count);
        Assert.AreEqual(record.Id, evidence.ReviewRecords[^1].Id);
        Assert.AreEqual(ReviewState.ChangesRequested, evidence.ReviewRecords[^1].State);
        Assert.AreEqual(text, evidence.ReviewRecords[^1].Reason);
        StoredReviewComment storedComment = evidence.ReviewComments.Single();
        Assert.AreEqual(comment.Id, storedComment.Id);
        Assert.AreEqual(comment.OutputTime, storedComment.OutputTime);
        Assert.AreEqual(comment.SourceSpanId, storedComment.SourceSpanId);
        Assert.AreEqual(text, storedComment.Text);
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'review_records';"));
    }

    [TestMethod]
    public void AppendChangeRequestRollsBackCommentWhenRecordInsertFails()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        store.AppendReviewRecord(
            CreateReviewRecord(
                version,
                EntityId<ReviewRecordIdTag>.New(),
                currentTime,
                ReviewState.EditorReview,
                "Begin editor review."));
        database.ExecuteNonQuery(
            """
            CREATE TRIGGER fail_change_request_record
            BEFORE INSERT ON review_records
            WHEN NEW.state = 'ChangesRequested'
            BEGIN
                SELECT RAISE(ABORT, 'injected review-record failure');
            END;
            """);
        (ReviewComment comment, ReviewRecord record) = CreateChangeRequest(
            version,
            ReviewActorRole.Editor,
            "reviewer-'one'",
            currentTime.AddSeconds(1),
            "Injected failure must roll back both rows.");

        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() =>
            store.AppendChangeRequest(comment, record, ReviewState.EditorReview));

        Assert.AreEqual(19, failure.SqliteErrorCode);
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_records;"));
    }

    [TestMethod]
    public void DuplicateChangeRequestRecordIdRollsBackNewComment()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        EntityId<ReviewRecordIdTag> duplicateRecordId = EntityId<ReviewRecordIdTag>.New();
        store.AppendReviewRecord(
            CreateReviewRecord(
                version,
                duplicateRecordId,
                currentTime,
                ReviewState.EditorReview,
                "Begin editor review."));
        ReviewComment comment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            default,
            EntityId<SourceSpanIdTag>.New(),
            "reviewer-'one'",
            ReviewActorRole.Editor,
            currentTime.AddSeconds(1),
            "Duplicate record ID.");
        ReviewRecord duplicateRecord = CreateReviewRecord(
            version,
            duplicateRecordId,
            currentTime.AddSeconds(1),
            ReviewState.ChangesRequested,
            "Duplicate record ID.");

        AssertConstraintViolation(() =>
            store.AppendChangeRequest(
                comment,
                duplicateRecord,
                ReviewState.EditorReview));

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_records;"));
    }

    [TestMethod]
    public void ChangeRequestRejectsMismatchedPairIllegalStateAndStoredDigest()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        store.AppendReviewRecord(
            new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                version,
                ReviewState.ContentReview,
                "content-approver",
                ReviewActorRole.ContentApprover,
                currentTime,
                "Begin content review."));
        DateTimeOffset requestTime = currentTime.AddSeconds(1);
        ReviewComment validComment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            default,
            EntityId<SourceSpanIdTag>.New(),
            "content-approver",
            ReviewActorRole.ContentApprover,
            requestTime,
            "Request correction.");

        CompositionVersion wrongProject = CopyVersion(
            version,
            projectId: EntityId<ProjectIdTag>.New());
        CompositionVersion wrongVersion = CopyVersion(
            version,
            id: EntityId<CompositionVersionIdTag>.New());
        CompositionVersion wrongDigest = CopyVersion(
            version,
            digest: new ContentDigest(new string('f', 64)));
        Action[] inconsistentPairs =
        [
            () => store.AppendChangeRequest(
                validComment,
                CreateChangeRecord(wrongProject, requestTime),
                ReviewState.ContentReview),
            () => store.AppendChangeRequest(
                validComment,
                CreateChangeRecord(wrongVersion, requestTime),
                ReviewState.ContentReview),
            () => store.AppendChangeRequest(
                validComment,
                CreateChangeRecord(wrongDigest, requestTime),
                ReviewState.ContentReview),
            () => store.AppendChangeRequest(
                validComment,
                CreateChangeRecord(version, requestTime.AddTicks(1)),
                ReviewState.ContentReview),
            () => store.AppendChangeRequest(
                validComment,
                CreateChangeRecord(version, requestTime, actorId: "other-approver"),
                ReviewState.ContentReview),
        ];
        foreach (Action inconsistentPair in inconsistentPairs)
        {
            ArgumentException exception =
                Assert.ThrowsExactly<ArgumentException>(inconsistentPair);
            Assert.AreEqual("reviewRecord", exception.ParamName);
        }

        ReviewRecord illegalStateRecord = new(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.ChangesRequested,
            "reviewer-'one'",
            ReviewActorRole.Editor,
            requestTime,
            "Illegal transition.");
        ReviewComment illegalStateComment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            default,
            EntityId<SourceSpanIdTag>.New(),
            "reviewer-'one'",
            ReviewActorRole.Editor,
            requestTime,
            "Illegal transition.");
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.AppendChangeRequest(
                illegalStateComment,
                illegalStateRecord,
                ReviewState.QualityControl));

        (ReviewComment forgedComment, ReviewRecord forgedRecord) =
            CreateChangeRequest(
                wrongDigest,
                ReviewActorRole.ContentApprover,
                "content-approver",
                requestTime,
                "Stored digest mismatch.");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.AppendChangeRequest(
                forgedComment,
                forgedRecord,
                ReviewState.ContentReview));

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(1L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_records;"));
    }

    [TestMethod]
    public void ChangeRequestRejectsReverseTimeAndAmbiguousLatestState()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        store.AppendReviewRecord(
            new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                version,
                ReviewState.ContentReview,
                "content-approver",
                ReviewActorRole.ContentApprover,
                currentTime,
                "First current state."));
        (ReviewComment reverseComment, ReviewRecord reverseRecord) =
            CreateChangeRequest(
                version,
                ReviewActorRole.ContentApprover,
                "content-approver",
                currentTime.AddTicks(-1),
                "Reverse chronology.");

        ArgumentOutOfRangeException reverse =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                store.AppendChangeRequest(
                    reverseComment,
                    reverseRecord,
                    ReviewState.ContentReview));
        Assert.AreEqual("reviewRecord", reverse.ParamName);

        store.AppendReviewRecord(
            new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                version,
                ReviewState.ContentReview,
                "content-approver",
                ReviewActorRole.ContentApprover,
                currentTime,
                "Ambiguous current state."));
        (ReviewComment ambiguousComment, ReviewRecord ambiguousRecord) =
            CreateChangeRequest(
                version,
                ReviewActorRole.ContentApprover,
                "content-approver",
                currentTime.AddSeconds(1),
                "Ambiguous ordering.");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.AppendChangeRequest(
                ambiguousComment,
                ambiguousRecord,
                ReviewState.ContentReview));
        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_comments;"));
        Assert.AreEqual(2L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM review_records;"));
    }

    [TestMethod]
    public async Task ConcurrentChangeRequestsAllowExactlyOneExpectedStateWinner()
    {
        using StoreTestDatabase database = new();
        (ReviewStore firstStore, _, CompositionVersion version) = CreateStores(database);
        var secondStore = new ReviewStore(database.DatabasePath);
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        firstStore.AppendReviewRecord(
            CreateReviewRecord(
                version,
                EntityId<ReviewRecordIdTag>.New(),
                currentTime,
                ReviewState.EditorReview,
                "Begin editor review."));
        using var release = new ManualResetEventSlim(initialState: false);

        Task<bool> first = Task.Run(() => TryAppendChangeRequest(
            firstStore,
            version,
            currentTime.AddSeconds(1),
            "First concurrent request.",
            release));
        Task<bool> second = Task.Run(() => TryAppendChangeRequest(
            secondStore,
            version,
            currentTime.AddSeconds(2),
            "Second concurrent request.",
            release));
        release.Set();
        bool[] outcomes = await Task.WhenAll(first, second);

        Assert.AreEqual(1, outcomes.Count(static succeeded => succeeded));
        StoredVersionEvidence evidence = firstStore.GetVersionEvidence(version.Id)
            ?? throw new AssertFailedException("The version evidence was not loaded.");
        Assert.AreEqual(1, evidence.ReviewComments.Count);
        Assert.AreEqual(2, evidence.ReviewRecords.Count);
        Assert.AreEqual(ReviewState.ChangesRequested, evidence.ReviewRecords[^1].State);
    }

    [TestMethod]
    public void AuthorizedReviewWritesRecheckCurrentRoleInsideWriteTransaction()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Current Windows role assignments require a Windows runner.");
            return;
        }

        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        var assignments = new RoleAssignmentStore(database.DatabasePath);
        Assert.IsTrue(assignments.BootstrapInitialRoles(AdministratorSid));
        assignments.Assign(
            ReviewerSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid,
            "Assign the content approver for transaction-bound review tests.");
        DateTimeOffset currentTime = VersionTime.AddMinutes(1);
        store.AppendReviewRecord(
            new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                version,
                ReviewState.ContentReview,
                ReviewerSid,
                ReviewActorRole.ContentApprover,
                currentTime,
                "Begin content review."));

        assignments.Revoke(
            ReviewerSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid);
        ReviewComment comment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            default,
            EntityId<SourceSpanIdTag>.New(),
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            currentTime.AddSeconds(1),
            "This write follows a revoked role.");
        ReviewRecord record = new(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.ChangesRequested,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            currentTime.AddSeconds(1),
            "This write follows a revoked role.");

        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            store.AppendAuthorizedReviewComment(
                comment,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            store.AppendAuthorizedChangeRequest(
                comment,
                record,
                ReviewState.ContentReview,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview));

        StoredVersionEvidence evidence = store.GetVersionEvidence(version.Id)
            ?? throw new AssertFailedException("The version evidence was not loaded.");
        Assert.AreEqual(0, evidence.ReviewComments.Count);
        Assert.AreEqual(1, evidence.ReviewRecords.Count);
    }

    [TestMethod]
    public void CurrentTargetReviewWritesRejectVersionAfterWorkingCopyAdvances()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Current Windows role assignments require a Windows runner.");
            return;
        }

        using StoreTestDatabase database = new();
        var projectStore = new ProjectStore(database.DatabasePath);
        Project project = CreateProject("Current review target project");
        projectStore.CreateProject(project, "{\"project\":1}", VersionTime);
        var initialWorkingCopy = new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            project.Id,
            baseVersionId: null,
            VersionTime,
            VersionTime,
            elements: []);
        projectStore.CreateWorkingCopy(
            initialWorkingCopy,
            "{\"workingCopy\":1}");
        var reviewedVersion = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            project.Id,
            parentVersionId: null,
            "editor",
            VersionTime.AddMinutes(1),
            "Create the reviewed version.",
            new ContentDigest(new string('a', 64)),
            elements: []);
        projectStore.AppendCompositionVersion(
            reviewedVersion,
            "{\"version\":1}");
        Assert.AreEqual(
            1,
            projectStore.UpdateWorkingCopy(
                new WorkingCopy(
                    initialWorkingCopy.Id,
                    project.Id,
                    reviewedVersion.Id,
                    initialWorkingCopy.CreatedAt,
                    VersionTime.AddMinutes(1),
                    elements: []),
                "{\"workingCopy\":2}",
                expectedRevision: 0));

        var store = new ReviewStore(database.DatabasePath);
        var assignments = new RoleAssignmentStore(database.DatabasePath);
        Assert.IsTrue(assignments.BootstrapInitialRoles(AdministratorSid));
        assignments.Assign(
            ReviewerSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid,
            "Assign content review access for current-target validation.");
        store.AppendReviewRecord(new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            reviewedVersion,
            ReviewState.ContentReview,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(2),
            "Begin content review."));

        var nextVersion = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            project.Id,
            reviewedVersion.Id,
            "editor",
            VersionTime.AddMinutes(3),
            "Advance the working-copy head.",
            new ContentDigest(new string('b', 64)),
            elements: []);
        projectStore.AppendCompositionVersion(nextVersion, "{\"version\":2}");
        Assert.AreEqual(
            2,
            projectStore.UpdateWorkingCopy(
                new WorkingCopy(
                    initialWorkingCopy.Id,
                    project.Id,
                    nextVersion.Id,
                    initialWorkingCopy.CreatedAt,
                    VersionTime.AddMinutes(3),
                    elements: []),
                "{\"workingCopy\":3}",
                expectedRevision: 1));

        DateTimeOffset attemptedAt = VersionTime.AddMinutes(4);
        var comment = new ReviewComment(
            EntityId<ReviewCommentIdTag>.New(),
            reviewedVersion,
            default,
            EntityId<SourceSpanIdTag>.New(),
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            attemptedAt,
            "Reject evidence for the former current target.");
        var changeRecord = new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            reviewedVersion,
            ReviewState.ChangesRequested,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            attemptedAt,
            "Reject evidence for the former current target.");
        var approval = new Approval(
            EntityId<ApprovalIdTag>.New(),
            reviewedVersion,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            attemptedAt,
            "Reject approval for the former current target.");

        Assert.IsFalse(store.TryAppendAuthorizedCurrentReviewComment(
            comment,
            AuthorizationRole.ContentApprover,
            AuthorizationOperation.RecordContentReview));
        Assert.IsFalse(store.TryAppendAuthorizedCurrentChangeRequest(
            comment,
            changeRecord,
            ReviewState.ContentReview,
            AuthorizationRole.ContentApprover,
            AuthorizationOperation.RecordContentReview));
        Assert.IsFalse(store.TryAppendAuthorizedCurrentApproval(
            approval,
            AuthorizationRole.ContentApprover,
            AuthorizationOperation.RecordContentReview));

        StoredVersionEvidence evidence = store.GetVersionEvidence(reviewedVersion.Id)
            ?? throw new AssertFailedException("The reviewed version evidence was not loaded.");
        Assert.AreEqual(0, evidence.ReviewComments.Count);
        Assert.AreEqual(0, evidence.Approvals.Count);
        Assert.AreEqual(1, evidence.ReviewRecords.Count);
        Assert.AreEqual(ReviewState.ContentReview, evidence.ReviewRecords[0].State);
    }

    [TestMethod]
    public void CurrentApprovalDoesNotInventReviewStateEligibility()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Current Windows role assignments require a Windows runner.");
            return;
        }

        using StoreTestDatabase database = new();
        var projectStore = new ProjectStore(database.DatabasePath);
        Project project = CreateProject("Approval state-race project");
        projectStore.CreateProject(project, "{\"project\":1}", VersionTime);
        var workingCopy = new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            project.Id,
            baseVersionId: null,
            VersionTime,
            VersionTime,
            elements: []);
        projectStore.CreateWorkingCopy(workingCopy, "{\"workingCopy\":1}");
        var version = new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            project.Id,
            parentVersionId: null,
            "editor",
            VersionTime.AddMinutes(1),
            "Create the approval target.",
            new ContentDigest(new string('a', 64)),
            elements: []);
        projectStore.AppendCompositionVersion(version, "{\"version\":1}");
        Assert.AreEqual(
            1,
            projectStore.UpdateWorkingCopy(
                new WorkingCopy(
                    workingCopy.Id,
                    project.Id,
                    version.Id,
                    workingCopy.CreatedAt,
                    VersionTime.AddMinutes(1),
                    elements: []),
                "{\"workingCopy\":2}",
                expectedRevision: 0));

        var store = new ReviewStore(database.DatabasePath);
        var assignments = new RoleAssignmentStore(database.DatabasePath);
        Assert.IsTrue(assignments.BootstrapInitialRoles(AdministratorSid));
        assignments.Assign(
            ReviewerSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid,
            "Assign content approval for the state-race test.");
        store.AppendReviewRecord(new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.ContentReview,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(2),
            "The caller reads this eligible state."));
        var approval = new Approval(
            EntityId<ApprovalIdTag>.New(),
            version,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(4),
            "Do not persist after the state changes.");
        store.AppendReviewRecord(new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.ChangesRequested,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(3),
            "A concurrent request makes approval ineligible."));

        Assert.IsTrue(
            store.TryAppendAuthorizedCurrentApproval(
                approval,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview));

        Assert.AreEqual(
            1,
            store.GetVersionEvidence(version.Id)!.Approvals.Count);
    }

    [TestMethod]
    public void AuthorizedReviewCommentRejectsMismatchedRoleOperationMapping()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        ReviewComment comment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            default,
            EntityId<SourceSpanIdTag>.New(),
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "Reject a mismatched authorization mapping.");

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
            store.AppendAuthorizedReviewComment(
                comment,
                AuthorizationRole.Editor,
                AuthorizationOperation.SubmitForReview));

        Assert.AreEqual("authorizationRole", exception.ParamName);
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM review_comments;"));
    }

    [TestMethod]
    public void AuthorizedApprovalRejectsWrongRoleOperationAdministratorAndViewerMappings()
    {
        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        Approval approval = CreateApproval(
            version,
            EntityId<ApprovalIdTag>.New(),
            VersionTime.AddMinutes(1),
            ReviewActorRole.ContentApprover,
            "Require the exact approval authorization mapping.");
        (AuthorizationRole Role, AuthorizationOperation Operation)[] invalidMappings =
        [
            (AuthorizationRole.BrandApprover,
                AuthorizationOperation.RecordBrandReview),
            (AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordBrandReview),
            (AuthorizationRole.Administrator,
                AuthorizationOperation.ManageRoleAssignments),
            (AuthorizationRole.Viewer,
                AuthorizationOperation.ViewProject),
        ];

        foreach ((AuthorizationRole role, AuthorizationOperation operation)
            in invalidMappings)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                store.AppendAuthorizedApproval(approval, role, operation));
            Assert.AreEqual("authorizationRole", exception.ParamName);
        }

        Assert.AreEqual(0L, database.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
    }

    [TestMethod]
    public void AuthorizedApprovalRequiresCurrentAssignmentAndExactStoredVersionBinding()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Current Windows role assignments require a Windows runner.");
            return;
        }

        using StoreTestDatabase database = new();
        (ReviewStore store, _, CompositionVersion version) = CreateStores(database);
        var assignments = new RoleAssignmentStore(database.DatabasePath);
        Assert.IsTrue(assignments.BootstrapInitialRoles(AdministratorSid));
        assignments.Assign(
            ReviewerSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid,
            "Assign content approval for the authorized store test.");
        CompositionVersion forgedDigest = CopyVersion(
            version,
            digest: new ContentDigest(new string('f', 64)));
        var forgedApproval = new Approval(
            EntityId<ApprovalIdTag>.New(),
            forgedDigest,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "Reject a forged digest before storage.");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.AppendAuthorizedApproval(
                forgedApproval,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview));

        var validApproval = new Approval(
            EntityId<ApprovalIdTag>.New(),
            version,
            ReviewerSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(2),
            "Persist the exact stored version binding.");
        store.AppendAuthorizedApproval(
            validApproval,
            AuthorizationRole.ContentApprover,
            AuthorizationOperation.RecordContentReview);

        StoredApproval stored = store.GetVersionEvidence(version.Id)!.Approvals.Single();
        Assert.AreEqual(validApproval.Id, stored.Id);
        Assert.AreEqual(version.ProjectId, stored.ProjectId);
        Assert.AreEqual(version.Id, stored.CompositionVersionId);
        Assert.AreEqual(version.ContentDigest, stored.CompositionVersionDigest);
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
    public void ProjectAuditEventsPreserveOptionalVersionPairAndUseGlobalAppendOrder()
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
            new[] { laterIdProjectEvent.Id, earlierIdVersionEvent.Id },
            events.Select(static item => item.Id).ToArray());
        Assert.IsNull(events[0].CompositionVersionId);
        Assert.IsNull(events[0].CompositionVersionDigest);
        Assert.AreEqual(version.Id, events[1].CompositionVersionId);
        Assert.AreEqual(version.ContentDigest, events[1].CompositionVersionDigest);
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

    private static (ReviewComment Comment, ReviewRecord Record) CreateChangeRequest(
        CompositionVersion version,
        ReviewActorRole actorRole,
        string actorId,
        DateTimeOffset recordedAt,
        string text)
    {
        return (
            new ReviewComment(
                EntityId<ReviewCommentIdTag>.New(),
                version,
                new RationalTime(5, 1),
                EntityId<SourceSpanIdTag>.New(),
                actorId,
                actorRole,
                recordedAt,
                text),
            new ReviewRecord(
                EntityId<ReviewRecordIdTag>.New(),
                version,
                ReviewState.ChangesRequested,
                actorId,
                actorRole,
                recordedAt,
                text));
    }

    private static ReviewRecord CreateChangeRecord(
        CompositionVersion version,
        DateTimeOffset recordedAt,
        string actorId = "content-approver")
    {
        return new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.ChangesRequested,
            actorId,
            ReviewActorRole.ContentApprover,
            recordedAt,
            "Request correction.");
    }

    private static CompositionVersion CopyVersion(
        CompositionVersion version,
        EntityId<CompositionVersionIdTag>? id = null,
        EntityId<ProjectIdTag>? projectId = null,
        ContentDigest? digest = null)
    {
        return new CompositionVersion(
            id ?? version.Id,
            projectId ?? version.ProjectId,
            parentVersionId: null,
            version.CreatedByActorId,
            version.CreatedAt,
            version.ChangeReason,
            digest ?? version.ContentDigest,
            version.Elements);
    }

    private static bool TryAppendChangeRequest(
        ReviewStore store,
        CompositionVersion version,
        DateTimeOffset recordedAt,
        string text,
        ManualResetEventSlim release)
    {
        (ReviewComment comment, ReviewRecord record) = CreateChangeRequest(
            version,
            ReviewActorRole.Editor,
            "reviewer-'one'",
            recordedAt,
            text);
        release.Wait();
        try
        {
            store.AppendChangeRequest(comment, record, ReviewState.EditorReview);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void AssertConstraintViolation(Action action)
    {
        SqliteException exception = Assert.ThrowsExactly<SqliteException>(action);
        Assert.AreEqual(19, exception.SqliteErrorCode);
    }
}