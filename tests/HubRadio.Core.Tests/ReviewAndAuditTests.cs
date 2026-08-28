using System.Reflection;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ReviewAndAuditTests
{
    private const string ActorId = "S-1-5-21-1000";

    [TestMethod]
    public void ReviewAndAuditEnumsDefineExactlyTheRequiredValues()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ReviewState.Editing,
                ReviewState.EditorReview,
                ReviewState.PerformerReview,
                ReviewState.ContentReview,
                ReviewState.BrandReview,
                ReviewState.ChangesRequested,
                ReviewState.Approved,
                ReviewState.PublicationBlocked,
                ReviewState.FinalRendering,
                ReviewState.QualityControl,
                ReviewState.Exported,
                ReviewState.Published,
            },
            Enum.GetValues<ReviewState>());
        CollectionAssert.AreEqual(
            new[]
            {
                ReviewActorRole.Editor,
                ReviewActorRole.PerformerReviewer,
                ReviewActorRole.ContentApprover,
                ReviewActorRole.BrandApprover,
                ReviewActorRole.RightsApprover,
                ReviewActorRole.Administrator,
            },
            Enum.GetValues<ReviewActorRole>());
        CollectionAssert.AreEqual(
            new[]
            {
                AuditEventKind.ProjectRegistered,
                AuditEventKind.TranscriptCorrected,
                AuditEventKind.PublicationClassificationChanged,
                AuditEventKind.ReviewStateRecorded,
                AuditEventKind.CommentAdded,
                AuditEventKind.ApprovalGranted,
                AuditEventKind.CompositionExported,
                AuditEventKind.PublicationRecorded,
                AuditEventKind.SuggestionDecisionRecorded,
            },
            Enum.GetValues<AuditEventKind>());
    }

    [TestMethod]
    public void MarkerTagsAndDomainTypesHaveTheRequiredImmutableShape()
    {
        foreach (Type markerType in new[]
        {
            typeof(ReviewRecordIdTag),
            typeof(ReviewCommentIdTag),
            typeof(ApprovalIdTag),
            typeof(AuditEventIdTag),
        })
        {
            Assert.IsTrue(markerType.IsPublic);
            Assert.IsTrue(markerType.IsSealed);
        }

        AssertPublicGetOnlyProperties(
            typeof(ReviewRecord),
            "Id",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "State",
            "ActorId",
            "ActorRole",
            "RecordedAt",
            "Reason");
        AssertPublicGetOnlyProperties(
            typeof(Approval),
            "Id",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "ActorId",
            "ActorRole",
            "ApprovedAt",
            "Reason");
        AssertPublicGetOnlyProperties(
            typeof(ReviewComment),
            "Id",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "OutputTime",
            "SourceSpanId",
            "ActorId",
            "ActorRole",
            "CreatedAt",
            "Text");
        AssertPublicGetOnlyProperties(
            typeof(AuditEvent),
            "Id",
            "Kind",
            "ActorId",
            "ActorRole",
            "OccurredAt",
            "Reason",
            "ProjectId",
            "CompositionVersionId",
            "CompositionVersionDigest");
    }

    [TestMethod]
    public void ReviewAndAuditConstructorsBindVersionIdAndDigestFromCompositionVersion()
    {
        CompositionVersion version = CreateVersion();
        DateTimeOffset occurredAt = new(2026, 8, 28, 9, 30, 0, TimeSpan.Zero);
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();

        ReviewRecord record = new(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.ChangesRequested,
            ActorId,
            ReviewActorRole.Editor,
            occurredAt,
            "Clarify the opening statement.");
        Approval approval = new(
            EntityId<ApprovalIdTag>.New(),
            version,
            ActorId,
            ReviewActorRole.ContentApprover,
            occurredAt,
            "Content is accurate and ready.");
        ReviewComment comment = new(
            EntityId<ReviewCommentIdTag>.New(),
            version,
            new RationalTime(15, 2),
            sourceSpanId,
            ActorId,
            ReviewActorRole.PerformerReviewer,
            occurredAt,
            "Please confirm this title.");
        AuditEvent auditEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ReviewStateRecorded,
            ActorId,
            ReviewActorRole.Editor,
            occurredAt,
            "Recorded a requested content change.",
            version.ProjectId,
            version);

        foreach ((EntityId<CompositionVersionIdTag> VersionId, ContentDigest Digest) binding in
            new[]
            {
                (record.CompositionVersionId, record.CompositionVersionDigest),
                (approval.CompositionVersionId, approval.CompositionVersionDigest),
                (comment.CompositionVersionId, comment.CompositionVersionDigest),
                (auditEvent.CompositionVersionId!, auditEvent.CompositionVersionDigest!),
            })
        {
            Assert.AreSame(version.Id, binding.VersionId);
            Assert.AreSame(version.ContentDigest, binding.Digest);
        }

        Assert.AreEqual(ReviewState.ChangesRequested, record.State);
        Assert.AreEqual(ReviewActorRole.ContentApprover, approval.ActorRole);
        Assert.AreEqual(new RationalTime(15, 2), comment.OutputTime);
        Assert.AreSame(sourceSpanId, comment.SourceSpanId);
        Assert.AreSame(version.ProjectId, auditEvent.ProjectId);
        Assert.AreEqual(occurredAt, record.RecordedAt);
        Assert.AreEqual(occurredAt, approval.ApprovedAt);
        Assert.AreEqual(occurredAt, comment.CreatedAt);
        Assert.AreEqual(occurredAt, auditEvent.OccurredAt);
    }

    [TestMethod]
    public void ConstructorsDoNotAcceptIndependentVersionIdsOrDigests()
    {
        foreach (Type contractType in new[]
        {
            typeof(ReviewRecord),
            typeof(Approval),
            typeof(ReviewComment),
            typeof(AuditEvent),
        })
        {
            ParameterInfo[] parameters = contractType
                .GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters())
                .ToArray();

            Assert.IsTrue(parameters.Any(
                static parameter => parameter.ParameterType == typeof(CompositionVersion)));
            Assert.IsFalse(parameters.Any(
                static parameter =>
                    parameter.ParameterType == typeof(EntityId<CompositionVersionIdTag>)));
            Assert.IsFalse(parameters.Any(
                static parameter => parameter.ParameterType == typeof(ContentDigest)));
        }
    }

    [TestMethod]
    public void AuditEventKeepsOptionalVersionIdAndDigestTogether()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        AuditEvent projectEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.ProjectRegistered,
            ActorId,
            ReviewActorRole.Editor,
            new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero),
            "Registered the local project.",
            projectId);

        Assert.AreSame(projectId, projectEvent.ProjectId);
        Assert.IsNull(projectEvent.CompositionVersionId);
        Assert.IsNull(projectEvent.CompositionVersionDigest);

        CompositionVersion version = CreateVersion(projectId);
        AuditEvent versionEvent = new(
            EntityId<AuditEventIdTag>.New(),
            AuditEventKind.CompositionExported,
            ActorId,
            ReviewActorRole.Editor,
            new DateTimeOffset(2026, 8, 28, 10, 1, 0, TimeSpan.Zero),
            "Recorded the composition export.",
            projectId,
            version);

        Assert.AreSame(version.Id, versionEvent.CompositionVersionId);
        Assert.AreSame(version.ContentDigest, versionEvent.CompositionVersionDigest);
    }

    [TestMethod]
    public void AuditEventRejectsCompositionVersionFromAnotherProject()
    {
        EntityId<ProjectIdTag> recordedProjectId = EntityId<ProjectIdTag>.New();
        CompositionVersion otherProjectVersion = CreateVersion();

        AssertParamName<ArgumentException>(
            "compositionVersion",
            () => new AuditEvent(
                EntityId<AuditEventIdTag>.New(),
                AuditEventKind.CompositionExported,
                ActorId,
                ReviewActorRole.Editor,
                new DateTimeOffset(2026, 8, 28, 10, 2, 0, TimeSpan.Zero),
                "Recorded the composition export.",
                recordedProjectId,
                otherProjectVersion));
    }

    [TestMethod]
    public void ApprovalAcceptsOnlyExplicitApproverRoles()
    {
        ReviewActorRole[] allowedRoles =
        [
            ReviewActorRole.ContentApprover,
            ReviewActorRole.BrandApprover,
            ReviewActorRole.RightsApprover,
        ];
        ReviewActorRole[] rejectedRoles =
        [
            ReviewActorRole.Editor,
            ReviewActorRole.PerformerReviewer,
            ReviewActorRole.Administrator,
            (ReviewActorRole)int.MaxValue,
        ];

        foreach (ReviewActorRole role in allowedRoles)
        {
            Approval approval = CreateApproval(role);

            Assert.AreEqual(role, approval.ActorRole);
        }

        foreach (ReviewActorRole role in rejectedRoles)
        {
            AssertParamName<ArgumentOutOfRangeException>(
                "actorRole",
                () => CreateApproval(role));
        }
    }

    [TestMethod]
    public void ContractsRejectUnknownEnumValues()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "state",
            () => CreateReviewRecord(state: (ReviewState)int.MaxValue));
        AssertParamName<ArgumentOutOfRangeException>(
            "actorRole",
            () => CreateReviewRecord(actorRole: (ReviewActorRole)int.MaxValue));
        AssertParamName<ArgumentOutOfRangeException>(
            "actorRole",
            () => CreateReviewComment(actorRole: (ReviewActorRole)int.MaxValue));
        AssertParamName<ArgumentOutOfRangeException>(
            "kind",
            () => CreateAuditEvent(kind: (AuditEventKind)int.MaxValue));
        AssertParamName<ArgumentOutOfRangeException>(
            "actorRole",
            () => CreateAuditEvent(actorRole: (ReviewActorRole)int.MaxValue));
    }

    [TestMethod]
    public void ContractsRejectBlankOrUntrimmedText()
    {
        string[] invalidValues =
        [
            string.Empty,
            " ",
            "\t",
            " value",
            "value ",
            "\r\nvalue",
            "value\r\n",
        ];

        foreach (string invalidValue in invalidValues)
        {
            AssertParamName<ArgumentException>(
                "actorId",
                () => CreateReviewRecord(actorId: invalidValue));
            AssertParamName<ArgumentException>(
                "reason",
                () => CreateReviewRecord(reason: invalidValue));
            AssertParamName<ArgumentException>(
                "actorId",
                () => CreateApproval(actorId: invalidValue));
            AssertParamName<ArgumentException>(
                "reason",
                () => CreateApproval(reason: invalidValue));
            AssertParamName<ArgumentException>(
                "actorId",
                () => CreateReviewComment(actorId: invalidValue));
            AssertParamName<ArgumentException>(
                "text",
                () => CreateReviewComment(text: invalidValue));
            AssertParamName<ArgumentException>(
                "actorId",
                () => CreateAuditEvent(actorId: invalidValue));
            AssertParamName<ArgumentException>(
                "reason",
                () => CreateAuditEvent(reason: invalidValue));
        }
    }

    [TestMethod]
    public void ContractsRejectNullRequiredReferences()
    {
        CompositionVersion version = CreateVersion();
        EntityId<ReviewRecordIdTag> recordId = EntityId<ReviewRecordIdTag>.New();
        EntityId<ApprovalIdTag> approvalId = EntityId<ApprovalIdTag>.New();
        EntityId<ReviewCommentIdTag> commentId = EntityId<ReviewCommentIdTag>.New();
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        EntityId<AuditEventIdTag> auditEventId = EntityId<AuditEventIdTag>.New();
        DateTimeOffset timestamp = new(2026, 8, 28, 11, 0, 0, TimeSpan.Zero);

        AssertParamName<ArgumentNullException>(
            "id",
            () => new ReviewRecord(
                null!, version, ReviewState.Editing, ActorId, ReviewActorRole.Editor,
                timestamp, "Started editing."));
        AssertParamName<ArgumentNullException>(
            "compositionVersion",
            () => new ReviewRecord(
                recordId, null!, ReviewState.Editing, ActorId, ReviewActorRole.Editor,
                timestamp, "Started editing."));
        AssertParamName<ArgumentNullException>(
            "actorId",
            () => new ReviewRecord(
                recordId, version, ReviewState.Editing, null!, ReviewActorRole.Editor,
                timestamp, "Started editing."));
        AssertParamName<ArgumentNullException>(
            "reason",
            () => new ReviewRecord(
                recordId, version, ReviewState.Editing, ActorId, ReviewActorRole.Editor,
                timestamp, null!));

        AssertParamName<ArgumentNullException>(
            "id",
            () => new Approval(
                null!, version, ActorId, ReviewActorRole.ContentApprover,
                timestamp, "Approved."));
        AssertParamName<ArgumentNullException>(
            "compositionVersion",
            () => new Approval(
                approvalId, null!, ActorId, ReviewActorRole.ContentApprover,
                timestamp, "Approved."));
        AssertParamName<ArgumentNullException>(
            "actorId",
            () => new Approval(
                approvalId, version, null!, ReviewActorRole.ContentApprover,
                timestamp, "Approved."));
        AssertParamName<ArgumentNullException>(
            "reason",
            () => new Approval(
                approvalId, version, ActorId, ReviewActorRole.ContentApprover,
                timestamp, null!));

        AssertParamName<ArgumentNullException>(
            "id",
            () => new ReviewComment(
                null!, version, default, sourceSpanId, ActorId, ReviewActorRole.Editor,
                timestamp, "Comment."));
        AssertParamName<ArgumentNullException>(
            "compositionVersion",
            () => new ReviewComment(
                commentId, null!, default, sourceSpanId, ActorId, ReviewActorRole.Editor,
                timestamp, "Comment."));
        AssertParamName<ArgumentNullException>(
            "sourceSpanId",
            () => new ReviewComment(
                commentId, version, default, null!, ActorId, ReviewActorRole.Editor,
                timestamp, "Comment."));
        AssertParamName<ArgumentNullException>(
            "actorId",
            () => new ReviewComment(
                commentId, version, default, sourceSpanId, null!, ReviewActorRole.Editor,
                timestamp, "Comment."));
        AssertParamName<ArgumentNullException>(
            "text",
            () => new ReviewComment(
                commentId, version, default, sourceSpanId, ActorId, ReviewActorRole.Editor,
                timestamp, null!));

        AssertParamName<ArgumentNullException>(
            "id",
            () => new AuditEvent(
                null!, AuditEventKind.ProjectRegistered, ActorId, ReviewActorRole.Editor,
                timestamp, "Registered.", version.ProjectId));
        AssertParamName<ArgumentNullException>(
            "actorId",
            () => new AuditEvent(
                auditEventId, AuditEventKind.ProjectRegistered, null!, ReviewActorRole.Editor,
                timestamp, "Registered.", version.ProjectId));
        AssertParamName<ArgumentNullException>(
            "reason",
            () => new AuditEvent(
                auditEventId, AuditEventKind.ProjectRegistered, ActorId,
                ReviewActorRole.Editor, timestamp, null!, version.ProjectId));
        AssertParamName<ArgumentNullException>(
            "projectId",
            () => new AuditEvent(
                auditEventId, AuditEventKind.ProjectRegistered, ActorId,
                ReviewActorRole.Editor, timestamp, "Registered.", null!));
    }

    [TestMethod]
    public void ReviewCommentAcceptsZeroAndRejectsNegativeOutputTime()
    {
        ReviewComment atStart = CreateReviewComment(outputTime: default);

        Assert.AreEqual(default(RationalTime), atStart.OutputTime);
        AssertParamName<ArgumentOutOfRangeException>(
            "outputTime",
            () => CreateReviewComment(outputTime: new RationalTime(-1, 30)));
    }

    [TestMethod]
    public void CompositionVersionDoesNotOwnReviewOrApprovalState()
    {
        string[] forbiddenProperties =
        [
            "ReviewState",
            "Approval",
            "Approvals",
            "ApprovalStatus",
            "IsApproved",
        ];

        foreach (string propertyName in forbiddenProperties)
        {
            Assert.IsNull(typeof(CompositionVersion).GetProperty(propertyName));
        }
    }

    [TestMethod]
    public void AuditEventDoesNotExposeHashChainDetailsOrMutationApis()
    {
        string[] forbiddenProperties =
        [
            "Hash",
            "EventHash",
            "PreviousHash",
            "Details",
        ];
        foreach (string propertyName in forbiddenProperties)
        {
            Assert.IsNull(typeof(AuditEvent).GetProperty(propertyName));
        }

        MethodInfo[] publicMethods = typeof(AuditEvent).GetMethods(
            BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly);
        Assert.IsFalse(publicMethods.Any(
            static method => !method.IsSpecialName
                && (method.Name.Contains("Update", StringComparison.Ordinal)
                    || method.Name.Contains("Delete", StringComparison.Ordinal))));
    }

    private static ReviewRecord CreateReviewRecord(
        ReviewState state = ReviewState.Editing,
        string actorId = ActorId,
        ReviewActorRole actorRole = ReviewActorRole.Editor,
        string reason = "Started editing.")
    {
        return new ReviewRecord(
            EntityId<ReviewRecordIdTag>.New(),
            CreateVersion(),
            state,
            actorId,
            actorRole,
            new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
            reason);
    }

    private static Approval CreateApproval(
        ReviewActorRole actorRole = ReviewActorRole.ContentApprover,
        string actorId = ActorId,
        string reason = "Approved for content.")
    {
        return new Approval(
            EntityId<ApprovalIdTag>.New(),
            CreateVersion(),
            actorId,
            actorRole,
            new DateTimeOffset(2026, 8, 28, 12, 1, 0, TimeSpan.Zero),
            reason);
    }

    private static ReviewComment CreateReviewComment(
        RationalTime outputTime = default,
        string actorId = ActorId,
        ReviewActorRole actorRole = ReviewActorRole.Editor,
        string text = "Review comment.")
    {
        return new ReviewComment(
            EntityId<ReviewCommentIdTag>.New(),
            CreateVersion(),
            outputTime,
            EntityId<SourceSpanIdTag>.New(),
            actorId,
            actorRole,
            new DateTimeOffset(2026, 8, 28, 12, 2, 0, TimeSpan.Zero),
            text);
    }

    private static AuditEvent CreateAuditEvent(
        AuditEventKind kind = AuditEventKind.ProjectRegistered,
        string actorId = ActorId,
        ReviewActorRole actorRole = ReviewActorRole.Editor,
        string reason = "Registered the project.")
    {
        return new AuditEvent(
            EntityId<AuditEventIdTag>.New(),
            kind,
            actorId,
            actorRole,
            new DateTimeOffset(2026, 8, 28, 12, 3, 0, TimeSpan.Zero),
            reason,
            EntityId<ProjectIdTag>.New());
    }

    private static CompositionVersion CreateVersion(EntityId<ProjectIdTag>? projectId = null)
    {
        return new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            projectId ?? EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            ActorId,
            new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero),
            "Create review candidate.",
            new ContentDigest(new string('a', 64)),
            elements: []);
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);

        PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}