using System.Reflection;
using HubRadio.App.Policy;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class PerformerPolicyTests
{
    private const string AdministratorSid =
        "S-1-5-21-100-200-300-1000";
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private const string ViewerSid = "S-1-5-21-100-200-300-1002";
    private const string ConfirmationReason =
        "Confirm the exact performer label for this recording.";

    private static readonly DateTimeOffset RecordedAt =
        new(2040, 1, 10, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ConfirmedNameBindsExactProjectAndAssignmentSnapshots()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);

        PerformerPolicyResult result = PerformerPolicy.Evaluate(
            fixture.Project,
            history,
            RecordedAt,
            [confirmation]);
        PerformerPolicyResult repeated = PerformerPolicy.Evaluate(
            fixture.Project,
            history,
            RecordedAt,
            [confirmation]);

        Assert.AreSame(fixture.Project.Id, result.ProjectId);
        Assert.AreSame(history.CurrentRevision.Id, result.AssignmentRevisionId);
        Assert.AreEqual(
            result.ProjectSnapshotDigest,
            repeated.ProjectSnapshotDigest);
        Assert.AreEqual(
            result.AssignmentSnapshotDigest,
            repeated.AssignmentSnapshotDigest);
        Assert.AreEqual(RecordedAt, result.RecordedAt);
        Assert.AreEqual(PerformerPolicyWarningCode.None, result.WarningCodes);
        Assert.IsFalse(result.RequiresWarning);

        PerformerPolicyDecision decision = result.Decisions.Single();
        Assert.AreEqual(PerformerLabelKind.Name, decision.LabelKind);
        Assert.AreEqual(
            PerformerPolicyDisposition.Confirmed,
            decision.Disposition);
        Assert.AreEqual(PerformerPolicyWarningCode.None, decision.WarningCodes);
        Assert.AreSame(confirmation, decision.Confirmation);
        Assert.IsNotNull(decision.ProjectSnapshot);
        Assert.AreEqual(
            result.ProjectSnapshotDigest,
            decision.ProjectSnapshot.ProjectDigest);
        Assert.AreEqual(
            result.AssignmentSnapshotDigest,
            decision.AssignmentSnapshot.AssignmentDigest);

        Assert.AreEqual(ActorSid, confirmation.ActorSid);
        Assert.AreEqual(
            AuthorizationRole.PerformerReviewer,
            confirmation.ActingRole);
        Assert.AreEqual(ConfirmationReason, confirmation.Reason);
        Assert.AreEqual(RecordedAt, confirmation.RecordedAt);
        Assert.AreEqual(
            fixture.ConfirmedPerformer.DisplayName,
            confirmation.ProjectSnapshot.DisplayName);
        Assert.AreEqual(
            fixture.ConfirmedPerformer.Role,
            confirmation.ProjectSnapshot.Role);
        Assert.AreEqual(
            fixture.ConfirmedPerformer.TitleConfirmedAt,
            confirmation.ProjectSnapshot.TitleConfirmedAt);
        Assert.AreEqual(
            SpeakerDisplayFormat.FullName,
            confirmation.AssignmentSnapshot.DisplayFormat);
        Assert.AreEqual(
            history.TranscriptRevisionDigest,
            confirmation.AssignmentSnapshot.TranscriptRevisionDigest);
    }

    [TestMethod]
    public void MissingNameAndTitleEvidenceReturnDistinctTypedWarnings()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory nameHistory = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.UnconfirmedPerformer.Id,
            "Beta",
            SpeakerDisplayFormat.FullName);
        SpeakerAssignmentHistory roleHistory = fixture.Set(
            history: null,
            revisionNumber: 2,
            fixture.SecondSpeakerId,
            fixture.UnconfirmedPerformer.Id,
            "Beta",
            SpeakerDisplayFormat.Role);

        PerformerPolicyDecision nameDecision = PerformerPolicy.Evaluate(
            fixture.Project,
            nameHistory,
            RecordedAt,
            confirmations: []).Decisions.Single();
        PerformerPolicyDecision roleDecision = PerformerPolicy.Evaluate(
            fixture.Project,
            roleHistory,
            RecordedAt,
            confirmations: []).Decisions.Single();

        Assert.AreEqual(PerformerLabelKind.Name, nameDecision.LabelKind);
        Assert.AreEqual(
            PerformerPolicyWarningCode.NameUnconfirmed,
            nameDecision.WarningCodes);
        Assert.AreEqual(PerformerLabelKind.Role, roleDecision.LabelKind);
        Assert.AreEqual(
            PerformerPolicyWarningCode.TitleOrAffiliationUnconfirmed,
            roleDecision.WarningCodes);
        Assert.AreEqual(
            PerformerPolicyDisposition.WarningRequired,
            nameDecision.Disposition);
        Assert.AreEqual(
            PerformerPolicyDisposition.WarningRequired,
            roleDecision.Disposition);
    }

    [TestMethod]
    public void ExplicitRoleDecisionConfirmedBeforeRecordingNeedsNoWarning()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.Role);
        PerformerConfirmation confirmation = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);

        PerformerPolicyDecision decision = PerformerPolicy.Evaluate(
            fixture.Project,
            history,
            RecordedAt,
            [confirmation]).Decisions.Single();

        Assert.AreEqual(PerformerLabelKind.Role, decision.LabelKind);
        Assert.AreEqual(
            SpeakerDisplayFormat.Role,
            decision.AssignmentSnapshot.DisplayFormat);
        Assert.AreEqual(PerformerPolicyWarningCode.None, decision.WarningCodes);
        Assert.AreEqual(
            PerformerPolicyDisposition.Confirmed,
            decision.Disposition);
    }

    [TestMethod]
    public void TitleConfirmedAfterRecordingRemainsAWarningEvenWhenCurrent()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.PostRecordingPerformer.Id,
            "Gamma",
            SpeakerDisplayFormat.Role);
        PerformerConfirmation confirmation = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1,
            confirmedAt: RecordedAt.AddHours(2));

        PerformerPolicyDecision decision = PerformerPolicy.Evaluate(
            fixture.Project,
            history,
            RecordedAt,
            [confirmation]).Decisions.Single();

        Assert.AreEqual(
            PerformerPolicyWarningCode.TitleOrAffiliationChangedAfterRecording,
            decision.WarningCodes);
        Assert.AreEqual(
            PerformerPolicyDisposition.WarningRequired,
            decision.Disposition);
        Assert.AreSame(confirmation, decision.Confirmation);
    }

    [TestMethod]
    public void HiddenLabelReturnsTypedHiddenResultAndRejectsConfirmation()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.UnconfirmedPerformer.Id,
            "Beta",
            SpeakerDisplayFormat.Hidden);

        PerformerPolicyDecision decision = PerformerPolicy.Evaluate(
            fixture.Project,
            history,
            RecordedAt,
            confirmations: []).Decisions.Single();

        Assert.AreEqual(PerformerLabelKind.Hidden, decision.LabelKind);
        Assert.AreEqual(
            PerformerPolicyDisposition.Hidden,
            decision.Disposition);
        Assert.AreEqual(PerformerPolicyWarningCode.None, decision.WarningCodes);
        Assert.IsNull(decision.Confirmation);
        AssertFailure(
            () => fixture.Capture(
                history,
                fixture.FirstSpeakerId,
                confirmationNumber: 1),
            PerformerPolicyFailureReason.HiddenLabelCannotBeConfirmed);
    }

    [TestMethod]
    public void VisibleConfirmationIsNotCarriedIntoCurrentHiddenDecision()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory visible = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            visible,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);
        SpeakerAssignmentHistory hidden = fixture.Set(
            visible,
            revisionNumber: 2,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.Hidden);

        PerformerPolicyDecision decision = PerformerPolicy.Evaluate(
            fixture.Project,
            hidden,
            RecordedAt,
            [confirmation]).Decisions.Single();

        Assert.AreEqual(PerformerLabelKind.Hidden, decision.LabelKind);
        Assert.AreEqual(
            PerformerPolicyDisposition.Hidden,
            decision.Disposition);
        Assert.AreEqual(PerformerPolicyWarningCode.None, decision.WarningCodes);
        Assert.IsFalse(decision.RequiresWarning);
        Assert.IsNull(decision.Confirmation);
        Assert.AreNotEqual(
            confirmation.AssignmentSnapshot.AssignmentDigest,
            decision.AssignmentSnapshot.AssignmentDigest);
    }

    [TestMethod]
    public void ChangedProjectDigestMakesExistingEvidenceStale()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);
        Project changedProject = fixture.CreateProject(
            contentName: "Changed project snapshot");

        PerformerPolicyResult result = PerformerPolicy.Evaluate(
            changedProject,
            history,
            RecordedAt,
            [confirmation]);
        PerformerPolicyDecision decision = result.Decisions.Single();

        Assert.AreNotEqual(
            confirmation.ProjectSnapshot.ProjectDigest,
            result.ProjectSnapshotDigest);
        Assert.AreEqual(
            PerformerPolicyWarningCode.StaleProjectSnapshot
                | PerformerPolicyWarningCode.NameUnconfirmed,
            decision.WarningCodes);
        Assert.IsFalse(decision.WarningCodes.HasFlag(
            PerformerPolicyWarningCode.StaleAssignmentSnapshot));
    }

    [TestMethod]
    public void ChangedAssignmentRevisionMakesExistingEvidenceStale()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory original = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            original,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);
        SpeakerAssignmentHistory changed = fixture.Set(
            original,
            revisionNumber: 2,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.Surname);

        PerformerPolicyResult result = PerformerPolicy.Evaluate(
            fixture.Project,
            changed,
            RecordedAt,
            [confirmation]);
        PerformerPolicyDecision decision = result.Decisions.Single();

        Assert.AreNotEqual(
            confirmation.AssignmentSnapshot.AssignmentDigest,
            result.AssignmentSnapshotDigest);
        Assert.AreEqual(
            PerformerPolicyWarningCode.StaleAssignmentSnapshot
                | PerformerPolicyWarningCode.NameUnconfirmed,
            decision.WarningCodes);
        Assert.IsFalse(decision.WarningCodes.HasFlag(
            PerformerPolicyWarningCode.StaleProjectSnapshot));
    }

    [TestMethod]
    public void DuplicateAndDanglingConfirmationBindingsFailClosed()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory assigned = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            assigned,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);

        AssertFailure(
            () => PerformerPolicy.Evaluate(
                fixture.Project,
                assigned,
                RecordedAt,
                [confirmation, confirmation]),
            PerformerPolicyFailureReason.DuplicateConfirmationBinding);

        SpeakerAssignmentHistory removed = SpeakerAssignmentService.RemoveAssignment(
            fixture.Project,
            fixture.Transcript,
            assigned,
            Fixture.RevisionId(2),
            fixture.FirstSpeakerId,
            ActorSid,
            Fixture.AssignmentAt.AddMinutes(2),
            "Remove the first binding.");
        SpeakerAssignmentHistory rebound = fixture.Set(
            removed,
            revisionNumber: 3,
            fixture.SecondSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);

        AssertFailure(
            () => PerformerPolicy.Evaluate(
                fixture.Project,
                rebound,
                RecordedAt,
                [confirmation]),
            PerformerPolicyFailureReason.AssignmentBindingMissing);
        Assert.HasCount(1, assigned.CurrentRevision.Assignments);
        Assert.HasCount(1, rebound.CurrentRevision.Assignments);
    }

    [TestMethod]
    public void ConfirmationMustMatchTheExactRecordingContext()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);

        foreach (DateTimeOffset differentRecording in new[]
        {
            RecordedAt.AddTicks(1),
            RecordedAt.ToOffset(TimeSpan.FromHours(9)),
        })
        {
            AssertFailure(
                () => PerformerPolicy.Evaluate(
                    fixture.Project,
                    history,
                    differentRecording,
                    [confirmation]),
                PerformerPolicyFailureReason.RecordingContextMismatch);
        }
    }

    [TestMethod]
    public void ConfirmationTimeRejectsOnlyTheBeforeRecordingBoundary()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);

        AssertFailure(
            () => fixture.Capture(
                history,
                fixture.FirstSpeakerId,
                confirmationNumber: 1,
                confirmedAt: RecordedAt.AddTicks(-1)),
            PerformerPolicyFailureReason.InvalidEvidence);

        PerformerConfirmation simultaneous = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 2,
            confirmedAt: RecordedAt);
        PerformerConfirmation after = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 3,
            confirmedAt: RecordedAt.AddTicks(1));

        Assert.AreEqual(RecordedAt, simultaneous.ConfirmedAt);
        Assert.AreEqual(RecordedAt.AddTicks(1), after.ConfirmedAt);
    }

    [TestMethod]
    public void CaptureRequiresExactPerformerReviewerRoleFromExistingMatrix()
    {
        using Fixture fixture = new();
        fixture.Assign(ViewerSid, AuthorizationRole.Viewer);
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);

        PerformerConfirmation allowed = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);
        Assert.AreEqual(
            AuthorizationRole.PerformerReviewer,
            allowed.ActingRole);

        foreach ((string actorSid, AuthorizationRole actingRole) in new[]
        {
            (ViewerSid, AuthorizationRole.Viewer),
            (AdministratorSid, AuthorizationRole.Administrator),
        })
        {
            AuthorizationException exception =
                Assert.ThrowsExactly<AuthorizationException>(() =>
                    PerformerPolicy.CaptureConfirmation(
                        fixture.Authorization,
                        fixture.Project,
                        history,
                        fixture.FirstSpeakerId,
                        Fixture.ConfirmationId(2),
                        RecordedAt,
                        actorSid,
                        actingRole,
                        RecordedAt.AddTicks(1),
                        ConfirmationReason));
            Assert.AreEqual(
                AuthorizationOperation.RecordPerformerReview,
                exception.Operation);
            Assert.AreEqual(actingRole, exception.ActingRole);
            Assert.IsFalse(exception.ToString().Contains(
                actorSid,
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void CanonicalSidAndSubstantiveStrictReasonAreRequired()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);

        foreach (string invalidSid in new[]
        {
            "not-a-sid",
            "s-1-5-21-100-200-300-1001",
            $" {ActorSid}",
        })
        {
            AssertFailure(
                () => PerformerPolicy.CaptureConfirmation(
                    fixture.Authorization,
                    fixture.Project,
                    history,
                    fixture.FirstSpeakerId,
                    Fixture.ConfirmationId(1),
                    RecordedAt,
                    invalidSid,
                    AuthorizationRole.PerformerReviewer,
                    RecordedAt.AddHours(1),
                    ConfirmationReason),
                PerformerPolicyFailureReason.InvalidEvidence);
        }

        string[] invalidReasons =
        [
            string.Empty,
            " ",
            " reason",
            "reason ",
            "\u200B\u2060",
            "\u0301\u20DD",
            new string('\ud800', 1),
        ];
        foreach (string invalidReason in invalidReasons)
        {
            AssertFailure(
                () => PerformerPolicy.CaptureConfirmation(
                    fixture.Authorization,
                    fixture.Project,
                    history,
                    fixture.FirstSpeakerId,
                    Fixture.ConfirmationId(1),
                    RecordedAt,
                    ActorSid,
                    AuthorizationRole.PerformerReviewer,
                    RecordedAt.AddHours(1),
                    invalidReason),
                PerformerPolicyFailureReason.InvalidEvidence);
        }

        PerformerConfirmation valid = PerformerPolicy.CaptureConfirmation(
            fixture.Authorization,
            fixture.Project,
            history,
            fixture.FirstSpeakerId,
            Fixture.ConfirmationId(2),
            RecordedAt,
            ActorSid,
            AuthorizationRole.PerformerReviewer,
            RecordedAt.AddHours(1),
            "出演者本人が確認しました。🚀");
        Assert.AreEqual(ActorSid, valid.ActorSid);
        Assert.AreEqual("出演者本人が確認しました。🚀", valid.Reason);
    }

    [TestMethod]
    public void CancellationStopsCaptureAndEvaluationWithoutPartialResults()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PerformerPolicy.CaptureConfirmation(
                fixture.Authorization,
                fixture.Project,
                history,
                fixture.FirstSpeakerId,
                Fixture.ConfirmationId(1),
                RecordedAt,
                ActorSid,
                AuthorizationRole.PerformerReviewer,
                RecordedAt.AddHours(1),
                ConfirmationReason,
                source.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PerformerPolicy.Evaluate(
                fixture.Project,
                history,
                RecordedAt,
                confirmations: [],
                source.Token));
        Assert.HasCount(1, history.Revisions);
        Assert.HasCount(1, history.CurrentRevision.Assignments);
    }

    [TestMethod]
    public void EvaluationIsPureAndReturnedCollectionsAreReadOnly()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        PerformerConfirmation confirmation = fixture.Capture(
            history,
            fixture.FirstSpeakerId,
            confirmationNumber: 1);
        var confirmations = new List<PerformerConfirmation> { confirmation };
        Performer[] originalPerformers = fixture.Project.Performers.ToArray();
        SpeakerAssignmentRevision[] originalRevisions =
            history.Revisions.ToArray();
        SpeakerAssignment[] originalAssignments =
            history.CurrentRevision.Assignments.ToArray();

        PerformerPolicyResult result = PerformerPolicy.Evaluate(
            fixture.Project,
            history,
            RecordedAt,
            confirmations);

        CollectionAssert.AreEqual(
            originalPerformers,
            fixture.Project.Performers.ToArray());
        CollectionAssert.AreEqual(originalRevisions, history.Revisions.ToArray());
        CollectionAssert.AreEqual(
            originalAssignments,
            history.CurrentRevision.Assignments.ToArray());
        Assert.HasCount(1, confirmations);
        Assert.AreSame(confirmation, confirmations[0]);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<PerformerPolicyDecision>)result.Decisions).Clear());
    }

    [TestMethod]
    public void StableFailuresDoNotDiscloseReasonPathSidOrBindingIdentifier()
    {
        using Fixture fixture = new();
        SpeakerAssignmentHistory assigned = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);
        const string privateReason =
            "Confirmed from C:\\private\\speaker-body.txt.";
        PerformerConfirmation confirmation = PerformerPolicy.CaptureConfirmation(
            fixture.Authorization,
            fixture.Project,
            assigned,
            fixture.FirstSpeakerId,
            Fixture.ConfirmationId(1),
            RecordedAt,
            ActorSid,
            AuthorizationRole.PerformerReviewer,
            RecordedAt.AddHours(1),
            privateReason);
        SpeakerAssignmentHistory removed = SpeakerAssignmentService.RemoveAssignment(
            fixture.Project,
            fixture.Transcript,
            assigned,
            Fixture.RevisionId(2),
            fixture.FirstSpeakerId,
            ActorSid,
            Fixture.AssignmentAt.AddMinutes(2),
            "Remove the private binding.");
        SpeakerAssignmentHistory rebound = fixture.Set(
            removed,
            revisionNumber: 3,
            fixture.SecondSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName);

        PerformerPolicyException exception = AssertFailure(
            () => PerformerPolicy.Evaluate(
                fixture.Project,
                rebound,
                RecordedAt,
                [confirmation]),
            PerformerPolicyFailureReason.AssignmentBindingMissing);
        string rendered = exception.ToString();

        Assert.IsFalse(rendered.Contains(privateReason, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("C:\\private", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains(ActorSid, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains(
            fixture.FirstSpeakerId.ToString(),
            StringComparison.Ordinal));
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    public void PublicSurfaceIsClosedTypedImmutableAndHasNoThresholdOrPermission()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                PerformerLabelKind.Name,
                PerformerLabelKind.Role,
                PerformerLabelKind.Hidden,
            },
            Enum.GetValues<PerformerLabelKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                PerformerPolicyWarningCode.None,
                PerformerPolicyWarningCode.NameUnconfirmed,
                PerformerPolicyWarningCode.TitleOrAffiliationUnconfirmed,
                PerformerPolicyWarningCode.TitleOrAffiliationChangedAfterRecording,
                PerformerPolicyWarningCode.StaleProjectSnapshot,
                PerformerPolicyWarningCode.StaleAssignmentSnapshot,
            },
            Enum.GetValues<PerformerPolicyWarningCode>());
        CollectionAssert.AreEqual(
            new[]
            {
                PerformerPolicyDisposition.Confirmed,
                PerformerPolicyDisposition.WarningRequired,
                PerformerPolicyDisposition.Hidden,
            },
            Enum.GetValues<PerformerPolicyDisposition>());
        Assert.IsNotNull(typeof(PerformerPolicyWarningCode)
            .GetCustomAttribute<FlagsAttribute>());

        foreach (Type type in new[]
        {
            typeof(PerformerProjectSnapshot),
            typeof(PerformerAssignmentSnapshot),
            typeof(PerformerConfirmation),
            typeof(PerformerPolicyDecision),
            typeof(PerformerPolicyResult),
            typeof(PerformerPolicyException),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
            PropertyInfo[] properties = type.GetProperties(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly);
            Assert.IsTrue(properties.All(
                static property => property.GetMethod?.IsPublic == true),
                type.Name);
            Assert.IsTrue(properties.All(
                static property => property.SetMethod is null),
                type.Name);
            Assert.IsFalse(properties.Any(static property =>
                property.Name.Contains("Permission", StringComparison.Ordinal)
                || property.Name.Contains("Authorization", StringComparison.Ordinal)),
                type.Name);
        }

        MethodInfo[] methods = typeof(PerformerPolicy).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(PerformerPolicy.CaptureConfirmation),
                nameof(PerformerPolicy.Evaluate),
            },
            methods.Select(static method => method.Name).ToArray());
        Assert.IsFalse(methods.SelectMany(static method => method.GetParameters())
            .Any(static parameter => parameter.ParameterType == typeof(TimeSpan)));
        ConstructorInfo exceptionConstructor = typeof(PerformerPolicyException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        Assert.IsTrue(exceptionConstructor.IsAssembly);
        Assert.IsTrue(typeof(PerformerConfirmationIdTag).IsPublic);
        Assert.IsTrue(typeof(PerformerConfirmationIdTag).IsSealed);
        Assert.AreEqual(
            typeof(AuthorizationRole),
            typeof(PerformerConfirmation)
                .GetProperty(nameof(PerformerConfirmation.ActingRole))!
                .PropertyType);
        MethodInfo capture = methods.Single(static method =>
            method.Name == nameof(PerformerPolicy.CaptureConfirmation));
        Assert.IsTrue(capture.GetParameters().Any(static parameter =>
            parameter.ParameterType == typeof(AuthorizationService)));
        Assert.IsTrue(capture.GetParameters().Any(static parameter =>
            parameter.ParameterType == typeof(AuthorizationRole)));
    }

    private static PerformerPolicyException AssertFailure(
        Action action,
        PerformerPolicyFailureReason expectedReason)
    {
        PerformerPolicyException exception =
            Assert.ThrowsExactly<PerformerPolicyException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset AssignmentAt =
            RecordedAt.AddDays(-2);

        internal Fixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.PerformerPolicyTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            string databasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(RootPath, "backups"));
            Authorization = new AuthorizationService(
                new RoleAssignmentStore(databasePath));
            Assert.IsTrue(Authorization.BootstrapInitialRoles(AdministratorSid));
            Assign(ActorSid, AuthorizationRole.PerformerReviewer);

            ConfirmedPerformer = new Performer(
                Id<PerformerIdTag>(
                    "73000000-0000-0000-0000-000000000001"),
                "Host Alpha",
                "Host",
                RecordedAt.AddMonths(-1));
            UnconfirmedPerformer = new Performer(
                Id<PerformerIdTag>(
                    "73000000-0000-0000-0000-000000000002"),
                "Guest Beta",
                "Guest",
                titleConfirmedAt: null);
            PostRecordingPerformer = new Performer(
                Id<PerformerIdTag>(
                    "73000000-0000-0000-0000-000000000003"),
                "Guest Gamma",
                "Guest",
                RecordedAt.AddMinutes(1));
            Project = CreateProject();
            FirstSpeakerId = Id<TranscriptSpeakerIdTag>(
                "74000000-0000-0000-0000-000000000001");
            SecondSpeakerId = Id<TranscriptSpeakerIdTag>(
                "74000000-0000-0000-0000-000000000002");
            Transcript = CreateTranscript();
        }

        private string RootPath { get; }

        internal AuthorizationService Authorization { get; }

        internal Project Project { get; }

        internal Performer ConfirmedPerformer { get; }

        internal Performer UnconfirmedPerformer { get; }

        internal Performer PostRecordingPerformer { get; }

        internal EntityId<TranscriptSpeakerIdTag> FirstSpeakerId { get; }

        internal EntityId<TranscriptSpeakerIdTag> SecondSpeakerId { get; }

        internal TranscriptRevision Transcript { get; }

        internal Project CreateProject(
            string contentName = "Performer policy project") =>
            new(
                Id<ProjectIdTag>(
                    "71000000-0000-0000-0000-000000000001"),
                contentName,
                "Verify performer confirmation policy.",
                "Test runner",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers:
                [
                    ConfirmedPerformer,
                    UnconfirmedPerformer,
                    PostRecordingPerformer,
                ]);

        internal SpeakerAssignmentHistory Set(
            SpeakerAssignmentHistory? history,
            int revisionNumber,
            EntityId<TranscriptSpeakerIdTag> speakerId,
            EntityId<PerformerIdTag> performerId,
            string surname,
            SpeakerDisplayFormat displayFormat) =>
            SpeakerAssignmentService.SetAssignment(
                Project,
                Transcript,
                history,
                RevisionId(revisionNumber),
                speakerId,
                performerId,
                surname,
                displayFormat,
                ActorSid,
                AssignmentAt.AddMinutes(revisionNumber),
                "Record the exact performer assignment.");

        internal PerformerConfirmation Capture(
            SpeakerAssignmentHistory history,
            EntityId<TranscriptSpeakerIdTag> speakerId,
            int confirmationNumber,
            DateTimeOffset? confirmedAt = null) =>
            PerformerPolicy.CaptureConfirmation(
                Authorization,
                Project,
                history,
                speakerId,
                ConfirmationId(confirmationNumber),
                RecordedAt,
                ActorSid,
                AuthorizationRole.PerformerReviewer,
                confirmedAt ?? RecordedAt.AddHours(1),
                ConfirmationReason);

        internal void Assign(string actorSid, AuthorizationRole role)
        {
            Authorization.AssignRole(
                actorSid,
                role,
                AdministratorSid,
                $"Assign {role} for the performer-policy test.");
        }

        internal static EntityId<SpeakerAssignmentRevisionIdTag> RevisionId(
            int value) =>
            Id<SpeakerAssignmentRevisionIdTag>(
                $"72000000-0000-0000-0000-{value:000000000000}");

        internal static EntityId<PerformerConfirmationIdTag> ConfirmationId(
            int value) =>
            Id<PerformerConfirmationIdTag>(
                $"75000000-0000-0000-0000-{value:000000000000}");

        private TranscriptRevision CreateTranscript()
        {
            Utterance first = CreateUtterance(
                value: 1,
                FirstSpeakerId,
                "Speaker 1",
                "First private statement",
                start: 0);
            Utterance second = CreateUtterance(
                value: 2,
                SecondSpeakerId,
                "Speaker 2",
                "Second private statement",
                start: 2);
            return new TranscriptRevision(
                Id<TranscriptRevisionIdTag>(
                    "76000000-0000-0000-0000-000000000001"),
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                AssignmentAt,
                [first, second]);
        }

        private static Utterance CreateUtterance(
            int value,
            EntityId<TranscriptSpeakerIdTag> speakerId,
            string speakerLabel,
            string text,
            long start)
        {
            var range = new TimeRange(
                new RationalTime(start, 1),
                new RationalTime(1, 1));
            var token = new Token(
                Id<TranscriptTokenIdTag>(
                    $"77000000-0000-0000-0000-{value:000000000000}"),
                text,
                range,
                recognitionConfidence: 0.9);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(
                    $"78000000-0000-0000-0000-{value:000000000000}"),
                text,
                [token]);
            return new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    $"79000000-0000-0000-0000-{value:000000000000}"),
                Id<SourceSpanIdTag>(
                    $"7a000000-0000-0000-0000-{value:000000000000}"),
                speakerId,
                speakerLabel,
                text,
                range,
                recognitionConfidence: 0.9,
                [sentence]);
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