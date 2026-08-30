using System.Reflection;
using System.Text;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SpeakerAssignmentServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2038, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void SetAssignment_ResolvesAllFourExplicitDisplayFormats()
    {
        Fixture fixture = new();
        (SpeakerDisplayFormat Format, bool Visible, string? Text)[] cases =
        [
            (SpeakerDisplayFormat.FullName, true, "Host Alpha"),
            (SpeakerDisplayFormat.Surname, true, "Explicit Surname"),
            (SpeakerDisplayFormat.Role, true, "Host"),
            (SpeakerDisplayFormat.Hidden, false, null),
        ];

        for (int index = 0; index < cases.Length; index++)
        {
            (SpeakerDisplayFormat format, bool visible, string? text) = cases[index];
            SpeakerAssignmentHistory history = fixture.Set(
                history: null,
                revisionNumber: index + 1,
                fixture.FirstSpeakerId,
                fixture.ConfirmedPerformer.Id,
                "Explicit Surname",
                format,
                CreatedAt.AddMinutes(index));

            ResolvedSpeakerDisplay display = SpeakerAssignmentService.ResolveDisplay(
                fixture.Project,
                fixture.Transcript,
                history,
                fixture.FirstSpeakerId);

            Assert.IsTrue(display.IsAssigned);
            Assert.AreEqual(visible, display.IsVisible);
            Assert.AreEqual(text, display.Text);
            Assert.AreEqual(format, display.Format);
            Assert.AreEqual(fixture.ConfirmedPerformer.Id, display.PerformerId);
            Assert.IsFalse(display.TitleOrAffiliationConfirmationRequired);
            Assert.AreEqual("Speaker 1", fixture.Transcript.Utterances[0].SpeakerLabel);
        }
    }

    [TestMethod]
    public void UnassignedDisplayRetainsAnonymousLabelWithoutInventingAPerson()
    {
        Fixture fixture = new();

        ResolvedSpeakerDisplay display = SpeakerAssignmentService.ResolveDisplay(
            fixture.Project,
            fixture.Transcript,
            history: null,
            fixture.FirstSpeakerId);

        Assert.IsFalse(display.IsAssigned);
        Assert.IsTrue(display.IsVisible);
        Assert.AreEqual("Speaker 1", display.Text);
        Assert.IsNull(display.PerformerId);
        Assert.IsNull(display.Format);
        Assert.IsNull(display.TitleConfirmedAt);
        Assert.IsFalse(display.TitleOrAffiliationConfirmationRequired);
    }

    [TestMethod]
    public void SetAssignment_AppendsImmutableFormatAndPerformerRevisionHistory()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory first = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt);
        SpeakerAssignmentHistory second = fixture.Set(
            first,
            revisionNumber: 2,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.Surname,
            CreatedAt.AddMinutes(1));
        SpeakerAssignmentHistory third = fixture.Set(
            second,
            revisionNumber: 3,
            fixture.FirstSpeakerId,
            fixture.UnconfirmedPerformer.Id,
            "Beta",
            SpeakerDisplayFormat.Role,
            CreatedAt.AddMinutes(2));

        Assert.HasCount(1, first.Revisions);
        Assert.HasCount(2, second.Revisions);
        Assert.HasCount(3, third.Revisions);
        Assert.IsNull(first.Revisions[0].ParentRevisionId);
        Assert.AreEqual(first.CurrentRevision.Id, second.CurrentRevision.ParentRevisionId);
        Assert.AreEqual(second.CurrentRevision.Id, third.CurrentRevision.ParentRevisionId);
        Assert.AreEqual(first.CurrentRevision, third.Revisions[0]);
        Assert.AreEqual(second.CurrentRevision, third.Revisions[1]);
        Assert.AreEqual(ActorSid, third.CurrentRevision.ActorSid);
        Assert.AreEqual("Confirm the manual speaker assignment.", third.CurrentRevision.Reason);
        Assert.AreEqual(
            fixture.ConfirmedPerformer.Id,
            third.CurrentRevision.Change.PreviousAssignment!.PerformerId);
        Assert.AreEqual(
            fixture.UnconfirmedPerformer.Id,
            third.CurrentRevision.Change.CurrentAssignment!.PerformerId);
        Assert.AreEqual(
            SpeakerDisplayFormat.FullName,
            first.CurrentRevision.Assignments.Single().DisplayFormat);
        Assert.AreEqual(
            SpeakerDisplayFormat.Surname,
            second.CurrentRevision.Assignments.Single().DisplayFormat);
        Assert.AreEqual(
            SpeakerDisplayFormat.Role,
            third.CurrentRevision.Assignments.Single().DisplayFormat);
    }

    [TestMethod]
    public void RemoveAssignment_AppendsRevisionAndReturnsToAnonymousDisplay()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory assigned = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt);

        SpeakerAssignmentHistory removed = SpeakerAssignmentService.RemoveAssignment(
            fixture.Project,
            fixture.Transcript,
            assigned,
            RevisionId(2),
            fixture.FirstSpeakerId,
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Remove the manual mapping.");
        SpeakerAssignmentHistory reassigned = fixture.Set(
            removed,
            revisionNumber: 3,
            fixture.FirstSpeakerId,
            fixture.UnconfirmedPerformer.Id,
            "Beta",
            SpeakerDisplayFormat.Surname,
            CreatedAt.AddMinutes(2));

        Assert.HasCount(2, removed.Revisions);
        Assert.HasCount(0, removed.CurrentRevision.Assignments);
        Assert.IsNotNull(removed.CurrentRevision.Change.PreviousAssignment);
        Assert.IsNull(removed.CurrentRevision.Change.CurrentAssignment);
        Assert.AreEqual(
            "Speaker 1",
            SpeakerAssignmentService.ResolveDisplay(
                fixture.Project,
                fixture.Transcript,
                removed,
                fixture.FirstSpeakerId).Text);
        Assert.HasCount(1, assigned.CurrentRevision.Assignments);
        Assert.HasCount(3, reassigned.Revisions);
        Assert.AreEqual(removed.CurrentRevision.Id, reassigned.CurrentRevision.ParentRevisionId);
        Assert.IsNull(reassigned.CurrentRevision.Change.PreviousAssignment);
        Assert.AreEqual(
            fixture.UnconfirmedPerformer.Id,
            reassigned.CurrentRevision.Change.CurrentAssignment!.PerformerId);
        Assert.AreEqual(
            "Beta",
            SpeakerAssignmentService.ResolveDisplay(
                fixture.Project,
                fixture.Transcript,
                reassigned,
                fixture.FirstSpeakerId).Text);
    }

    [TestMethod]
    public void DistinctClustersCanMapToOnePerformerInCanonicalOrder()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory first = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.SecondSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.Role,
            CreatedAt);
        SpeakerAssignmentHistory second = fixture.Set(
            first,
            revisionNumber: 2,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt.AddMinutes(1));

        Assert.HasCount(2, second.CurrentRevision.Assignments);
        CollectionAssert.AreEqual(
            second.CurrentRevision.Assignments
                .Select(static assignment => assignment.SpeakerId.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray(),
            second.CurrentRevision.Assignments
                .Select(static assignment => assignment.SpeakerId.ToString())
                .ToArray());
        Assert.IsTrue(second.CurrentRevision.Assignments.All(
            assignment => assignment.PerformerId == fixture.ConfirmedPerformer.Id));
    }

    [TestMethod]
    public void AssignmentSnapshotsPerformerEvidenceAndReportsUnconfirmedTitle()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory confirmed = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.Role,
            CreatedAt);
        SpeakerAssignmentHistory unconfirmed = fixture.Set(
            history: null,
            revisionNumber: 2,
            fixture.SecondSpeakerId,
            fixture.UnconfirmedPerformer.Id,
            "Beta",
            SpeakerDisplayFormat.FullName,
            CreatedAt);

        SpeakerAssignment confirmedAssignment =
            confirmed.CurrentRevision.Assignments.Single();
        SpeakerAssignment unconfirmedAssignment =
            unconfirmed.CurrentRevision.Assignments.Single();
        Assert.AreEqual("Host Alpha", confirmedAssignment.FullName);
        Assert.AreEqual("Alpha", confirmedAssignment.Surname);
        Assert.AreEqual("Host", confirmedAssignment.Role);
        Assert.AreEqual(
            fixture.ConfirmedPerformer.TitleConfirmedAt,
            confirmedAssignment.TitleConfirmedAt);
        Assert.IsFalse(confirmedAssignment.TitleOrAffiliationConfirmationRequired);
        Assert.IsTrue(unconfirmedAssignment.TitleOrAffiliationConfirmationRequired);
        Assert.IsTrue(SpeakerAssignmentService.ResolveDisplay(
            fixture.Project,
            fixture.Transcript,
            unconfirmed,
            fixture.SecondSpeakerId).TitleOrAffiliationConfirmationRequired);
    }

    [TestMethod]
    public void HistoryBindsExactProjectTranscriptRevisionAndDigest()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt);
        ContentDigest expectedDigest = ContentDigest.Compute(Encoding.UTF8.GetBytes(
            TranscriptSnapshotJson.WriteRevision(fixture.Transcript)));
        Assert.AreEqual(fixture.Project.Id, history.ProjectId);
        Assert.AreEqual(fixture.Transcript.Id, history.TranscriptRevisionId);
        Assert.AreEqual(expectedDigest, history.TranscriptRevisionDigest);

        var otherProject = new Project(
            Id<ProjectIdTag>("51000000-0000-0000-0000-000000000099"),
            "Other project",
            "Test context binding.",
            "Test runner",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: [fixture.ConfirmedPerformer, fixture.UnconfirmedPerformer]);
        AssertFailure(
            () => SpeakerAssignmentService.ResolveDisplay(
                otherProject,
                fixture.Transcript,
                history,
                fixture.FirstSpeakerId),
            SpeakerAssignmentFailureReason.ContextMismatch);

        TranscriptRevision changedSameId = fixture.CreateTranscript(
            fixture.Transcript.Id,
            firstText: "Changed private text");
        SpeakerAssignmentException mismatch = AssertFailure(
            () => SpeakerAssignmentService.ResolveDisplay(
                fixture.Project,
                changedSameId,
                history,
                fixture.FirstSpeakerId),
            SpeakerAssignmentFailureReason.ContextMismatch);
        Assert.IsFalse(mismatch.ToString().Contains(
            "Changed private text",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingSpeakerPerformerAndAssignmentFailWithoutMutation()
    {
        Fixture fixture = new();
        AssertFailure(
            () => fixture.Set(
                history: null,
                revisionNumber: 1,
                EntityId<TranscriptSpeakerIdTag>.New(),
                fixture.ConfirmedPerformer.Id,
                "Alpha",
                SpeakerDisplayFormat.FullName,
                CreatedAt),
            SpeakerAssignmentFailureReason.SpeakerNotFound);
        AssertFailure(
            () => fixture.Set(
                history: null,
                revisionNumber: 1,
                fixture.FirstSpeakerId,
                EntityId<PerformerIdTag>.New(),
                "Alpha",
                SpeakerDisplayFormat.FullName,
                CreatedAt),
            SpeakerAssignmentFailureReason.PerformerNotFound);

        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt);
        AssertFailure(
            () => SpeakerAssignmentService.RemoveAssignment(
                fixture.Project,
                fixture.Transcript,
                history,
                RevisionId(2),
                fixture.SecondSpeakerId,
                ActorSid,
                CreatedAt.AddMinutes(1),
                "No assignment exists."),
            SpeakerAssignmentFailureReason.AssignmentNotFound);
        Assert.HasCount(1, history.Revisions);
    }

    [TestMethod]
    public void NoOpDuplicateRevisionAndBackwardTimeAreRejected()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt);
        AssertFailure(
            () => fixture.Set(
                history,
                revisionNumber: 2,
                fixture.FirstSpeakerId,
                fixture.ConfirmedPerformer.Id,
                "Alpha",
                SpeakerDisplayFormat.FullName,
                CreatedAt.AddMinutes(1)),
            SpeakerAssignmentFailureReason.NoChange);
        AssertFailure(
            () => SpeakerAssignmentService.RemoveAssignment(
                fixture.Project,
                fixture.Transcript,
                history,
                RevisionId(1),
                fixture.FirstSpeakerId,
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Duplicate revision ID."),
            SpeakerAssignmentFailureReason.RevisionConflict);
        AssertFailure(
            () => SpeakerAssignmentService.RemoveAssignment(
                fixture.Project,
                fixture.Transcript,
                history,
                RevisionId(2),
                fixture.FirstSpeakerId,
                ActorSid,
                CreatedAt.AddTicks(-1),
                "Timestamp moved backwards."),
            SpeakerAssignmentFailureReason.RevisionConflict);
    }

    [TestMethod]
    public void InvalidTextSidAndDisplayFormatFailClosedWithoutContentDisclosure()
    {
        Fixture fixture = new();
        foreach (string invalidSurname in new[]
        {
            string.Empty,
            " private ",
            "private\uD800surname",
        })
        {
            SpeakerAssignmentException exception = AssertFailure(
                () => fixture.Set(
                    history: null,
                    revisionNumber: 1,
                    fixture.FirstSpeakerId,
                    fixture.ConfirmedPerformer.Id,
                    invalidSurname,
                    SpeakerDisplayFormat.Surname,
                    CreatedAt),
                SpeakerAssignmentFailureReason.InvalidText);
            Assert.IsFalse(exception.ToString().Contains(
                "private",
                StringComparison.Ordinal));
        }

        AssertFailure(
            () => SpeakerAssignmentService.SetAssignment(
                fixture.Project,
                fixture.Transcript,
                history: null,
                RevisionId(1),
                fixture.FirstSpeakerId,
                fixture.ConfirmedPerformer.Id,
                "Alpha",
                SpeakerDisplayFormat.FullName,
                "not-a-sid",
                CreatedAt,
                "Private reason"),
            SpeakerAssignmentFailureReason.InvalidText);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            fixture.Set(
                history: null,
                revisionNumber: 1,
                fixture.FirstSpeakerId,
                fixture.ConfirmedPerformer.Id,
                "Alpha",
                (SpeakerDisplayFormat)int.MaxValue,
                CreatedAt));
    }

    [TestMethod]
    public void PublicAssignmentSurfaceIsImmutableAndCollectionsAreReadOnly()
    {
        Fixture fixture = new();
        SpeakerAssignmentHistory history = fixture.Set(
            history: null,
            revisionNumber: 1,
            fixture.FirstSpeakerId,
            fixture.ConfirmedPerformer.Id,
            "Alpha",
            SpeakerDisplayFormat.FullName,
            CreatedAt);

        foreach (Type type in new[]
        {
            typeof(SpeakerAssignment),
            typeof(SpeakerAssignmentChange),
            typeof(SpeakerAssignmentRevision),
            typeof(SpeakerAssignmentHistory),
            typeof(ResolvedSpeakerDisplay),
            typeof(SpeakerAssignmentException),
        })
        {
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        IList<SpeakerAssignmentRevision> revisions =
            (IList<SpeakerAssignmentRevision>)history.Revisions;
        IList<SpeakerAssignment> assignments =
            (IList<SpeakerAssignment>)history.CurrentRevision.Assignments;
        Assert.ThrowsExactly<NotSupportedException>(() =>
            revisions.Add(history.CurrentRevision));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            assignments.Clear());
        Assert.IsNull(typeof(SpeakerAssignmentService).GetMethod("AutoAssign"));
        Assert.IsNull(typeof(SpeakerAssignmentService).GetMethod("InferSurname"));
    }

    private static SpeakerAssignmentException AssertFailure(
        Action action,
        SpeakerAssignmentFailureReason expectedReason)
    {
        SpeakerAssignmentException exception =
            Assert.ThrowsExactly<SpeakerAssignmentException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static EntityId<SpeakerAssignmentRevisionIdTag> RevisionId(int value) =>
        Id<SpeakerAssignmentRevisionIdTag>(
            $"52000000-0000-0000-0000-{value:000000000000}");

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed class Fixture
    {
        internal Fixture()
        {
            ConfirmedPerformer = new Performer(
                Id<PerformerIdTag>("53000000-0000-0000-0000-000000000001"),
                "Host Alpha",
                "Host",
                new DateTimeOffset(2037, 12, 1, 0, 0, 0, TimeSpan.Zero));
            UnconfirmedPerformer = new Performer(
                Id<PerformerIdTag>("53000000-0000-0000-0000-000000000002"),
                "Guest Beta",
                "Guest",
                titleConfirmedAt: null);
            Project = new Project(
                Id<ProjectIdTag>("51000000-0000-0000-0000-000000000001"),
                "Speaker assignment test",
                "Verify manual speaker assignment.",
                "Test runner",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: [ConfirmedPerformer, UnconfirmedPerformer]);
            FirstSpeakerId =
                Id<TranscriptSpeakerIdTag>("54000000-0000-0000-0000-000000000001");
            SecondSpeakerId =
                Id<TranscriptSpeakerIdTag>("54000000-0000-0000-0000-000000000002");
            Transcript = CreateTranscript(
                Id<TranscriptRevisionIdTag>(
                    "55000000-0000-0000-0000-000000000001"),
                firstText: "First statement");
        }

        internal Project Project { get; }

        internal Performer ConfirmedPerformer { get; }

        internal Performer UnconfirmedPerformer { get; }

        internal EntityId<TranscriptSpeakerIdTag> FirstSpeakerId { get; }

        internal EntityId<TranscriptSpeakerIdTag> SecondSpeakerId { get; }

        internal TranscriptRevision Transcript { get; }

        internal SpeakerAssignmentHistory Set(
            SpeakerAssignmentHistory? history,
            int revisionNumber,
            EntityId<TranscriptSpeakerIdTag> speakerId,
            EntityId<PerformerIdTag> performerId,
            string surname,
            SpeakerDisplayFormat format,
            DateTimeOffset createdAt) =>
            SpeakerAssignmentService.SetAssignment(
                Project,
                Transcript,
                history,
                RevisionId(revisionNumber),
                speakerId,
                performerId,
                surname,
                format,
                ActorSid,
                createdAt,
                "Confirm the manual speaker assignment.");

        internal TranscriptRevision CreateTranscript(
            EntityId<TranscriptRevisionIdTag> revisionId,
            string firstText)
        {
            Utterance first = CreateUtterance(
                value: 1,
                FirstSpeakerId,
                "Speaker 1",
                firstText,
                start: 0);
            Utterance second = CreateUtterance(
                value: 2,
                SecondSpeakerId,
                "Speaker 2",
                "Second statement",
                start: 2);
            return new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
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
                    $"56000000-0000-0000-0000-{value:000000000000}"),
                text,
                range,
                recognitionConfidence: 0.9);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(
                    $"57000000-0000-0000-0000-{value:000000000000}"),
                text,
                [token]);
            return new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    $"58000000-0000-0000-0000-{value:000000000000}"),
                Id<SourceSpanIdTag>(
                    $"59000000-0000-0000-0000-{value:000000000000}"),
                speakerId,
                speakerLabel,
                text,
                range,
                recognitionConfidence: 0.9,
                [sentence]);
        }
    }
}