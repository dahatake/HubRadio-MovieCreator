using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TopicMetadataServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2041, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [TestMethod]
    public void CreateInitialUsesOnlyPlaceholderProjectAndExactOverlapValues()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);

        TopicMetadataRevision revision = fixture.CreateInitial(topicOrdinal: 12);
        TopicMetadata metadata = revision.Metadata;

        Assert.AreEqual("Topic 12", metadata.Title);
        Assert.AreEqual(
            TopicMetadataValueOrigin.DeterministicPlaceholder,
            metadata.TitleOrigin);
        Assert.IsTrue(metadata.TitleRequiresManualReview);
        Assert.AreEqual("Internal engineers", metadata.Audience);
        Assert.AreEqual(
            TopicMetadataValueOrigin.ProjectSnapshot,
            metadata.AudienceOrigin);
        Assert.IsNull(metadata.Summary);
        Assert.AreEqual(TopicMetadataValueOrigin.Unset, metadata.SummaryOrigin);
        Assert.IsEmpty(metadata.Keywords);
        Assert.IsEmpty(metadata.ImportantStatementSourceSpanIds);
        Assert.IsEmpty(metadata.PublicationCautions);
        Assert.IsNotNull(metadata.MainSpeaker);
        Assert.AreEqual(fixture.FirstSpeakerId, metadata.MainSpeaker.SpeakerId);
        Assert.AreEqual(fixture.FirstPerformer.Id, metadata.MainSpeaker.PerformerId);
        Assert.AreEqual("Host Alpha", metadata.MainSpeaker.DisplayText);
        Assert.IsTrue(metadata.MainSpeaker.DisplayVisible);
        Assert.AreEqual(new RationalTime(4, 1), metadata.MainSpeaker.OverlapDuration);
        Assert.IsFalse(metadata.MainSpeakerTieUnresolved);
        Assert.AreEqual(
            TopicMetadataValueOrigin.DeterministicTranscriptOverlap,
            metadata.MainSpeakerOrigin);
        Assert.IsNull(revision.ParentRevisionId);
        Assert.AreEqual(TopicMetadataField.Initial, revision.ChangedField);
        Assert.AreEqual(fixture.Project.Id, revision.ProjectId);
        Assert.AreEqual(fixture.TopicRange.TopicRangeId, revision.TopicRangeId);
        Assert.AreEqual(fixture.TopicRange.Id, revision.TopicRangeRevisionId);
        Assert.AreEqual(fixture.TopicRange.Range, revision.TopicRange);
        Assert.AreEqual(fixture.Revision.Id, revision.SourceRevisionId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(fixture.Revision))),
            revision.SourceRevisionDigest);
    }

    [TestMethod]
    public void EqualSpeakerOverlapRemainsExplicitlyUnresolved()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(1, 1, "Speaker 1", "First", 0, 2),
                new UtteranceInput(2, 2, "Speaker 2", "Second", 2, 2),
            ],
            topicRange: Range(0, 4),
            withSpeakerAssignment: false);

        TopicMetadata metadata = fixture.CreateInitial().Metadata;

        Assert.IsNull(metadata.MainSpeaker);
        Assert.AreEqual(TopicMetadataValueOrigin.Unset, metadata.MainSpeakerOrigin);
        Assert.IsTrue(metadata.MainSpeakerTieUnresolved);
    }

    [TestMethod]
    public void UnassignedDeterministicSpeakerRetainsAnonymousDisplay()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: false);

        TopicMainSpeakerSnapshot speaker =
            fixture.CreateInitial().Metadata.MainSpeaker!;

        Assert.AreEqual(fixture.FirstSpeakerId, speaker.SpeakerId);
        Assert.IsNull(speaker.PerformerId);
        Assert.AreEqual("Speaker 1", speaker.DisplayText);
        Assert.IsTrue(speaker.DisplayVisible);
        Assert.IsFalse(speaker.TitleOrAffiliationConfirmationRequired);
    }

    [TestMethod]
    public void EveryEditableFieldCreatesOneImmutableChildRevision()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);
        TopicMetadataRevision initial = fixture.CreateInitial();
        TopicMetadataRevision title = fixture.SetTitle(
            initial,
            revisionNumber: 2,
            "Architecture update");
        TopicMetadataRevision summary = fixture.SetSummary(
            title,
            revisionNumber: 3,
            "A manually entered summary.");
        TopicMetadataRevision speaker = fixture.SetMainSpeaker(
            summary,
            revisionNumber: 4,
            fixture.SecondSpeakerId);
        TopicMetadataRevision keywords = fixture.SetKeywords(
            speaker,
            revisionNumber: 5,
            ["Azure", "Media"]);
        TopicMetadataRevision important = fixture.SetImportantStatements(
            keywords,
            revisionNumber: 6,
            [fixture.SourceSpans[1].Id]);
        TopicMetadataRevision audience = fixture.SetAudience(
            important,
            revisionNumber: 7,
            "External developers");
        TopicMetadataRevision cautions = fixture.SetCautions(
            audience,
            revisionNumber: 8,
            ["Verify the product name."]);

        TopicMetadataRevision[] revisions =
        [initial, title, summary, speaker, keywords, important, audience, cautions];
        CollectionAssert.AreEqual(
            new[]
            {
                TopicMetadataField.Initial,
                TopicMetadataField.Title,
                TopicMetadataField.Summary,
                TopicMetadataField.MainSpeaker,
                TopicMetadataField.Keywords,
                TopicMetadataField.ImportantStatements,
                TopicMetadataField.Audience,
                TopicMetadataField.PublicationCautions,
            },
            revisions.Select(static revision => revision.ChangedField).ToArray());
        for (int index = 1; index < revisions.Length; index++)
        {
            Assert.AreEqual(revisions[index - 1].Id, revisions[index].ParentRevisionId);
            Assert.AreEqual(revisions[0].TopicMetadataId, revisions[index].TopicMetadataId);
        }

        TopicMetadata result = cautions.Metadata;
        Assert.AreEqual("Architecture update", result.Title);
        Assert.AreEqual(TopicMetadataValueOrigin.ManualEditorial, result.TitleOrigin);
        Assert.IsFalse(result.TitleRequiresManualReview);
        Assert.AreEqual("A manually entered summary.", result.Summary);
        Assert.AreEqual(fixture.SecondSpeakerId, result.MainSpeaker!.SpeakerId);
        Assert.AreEqual(fixture.SecondPerformer.Id, result.MainSpeaker.PerformerId);
        Assert.IsTrue(result.MainSpeaker.TitleOrAffiliationConfirmationRequired);
        CollectionAssert.AreEqual(new[] { "Azure", "Media" }, result.Keywords.ToArray());
        CollectionAssert.AreEqual(
            new[] { fixture.SourceSpans[1].Id },
            result.ImportantStatementSourceSpanIds.ToArray());
        Assert.AreEqual("External developers", result.Audience);
        CollectionAssert.AreEqual(
            new[] { "Verify the product name." },
            result.PublicationCautions.ToArray());
        Assert.AreEqual("Topic 1", initial.Metadata.Title);
        Assert.IsNull(initial.Metadata.Summary);
    }

    [TestMethod]
    public void NullableAndListFieldsCanBeExplicitlyCleared()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);
        TopicMetadataRevision current = fixture.CreateInitial();
        current = fixture.SetSummary(current, 2, "Summary");
        current = fixture.SetKeywords(current, 3, ["Keyword"]);
        current = fixture.SetImportantStatements(current, 4, [fixture.SourceSpans[1].Id]);
        current = fixture.SetCautions(current, 5, ["Caution"]);
        current = fixture.SetMainSpeaker(current, 6, fixture.SecondSpeakerId);

        current = fixture.SetSummary(current, 7, null);
        current = fixture.SetKeywords(current, 8, []);
        current = fixture.SetImportantStatements(current, 9, []);
        current = fixture.SetCautions(current, 10, []);
        current = fixture.SetMainSpeaker(current, 11, null);

        Assert.IsNull(current.Metadata.Summary);
        Assert.IsNull(current.Metadata.MainSpeaker);
        Assert.IsEmpty(current.Metadata.Keywords);
        Assert.IsEmpty(current.Metadata.ImportantStatementSourceSpanIds);
        Assert.IsEmpty(current.Metadata.PublicationCautions);
        Assert.AreEqual(TopicMetadataValueOrigin.Unset, current.Metadata.SummaryOrigin);
        Assert.AreEqual(TopicMetadataValueOrigin.Unset, current.Metadata.MainSpeakerOrigin);
        Assert.AreEqual(TopicMetadataValueOrigin.Unset, current.Metadata.KeywordsOrigin);
        Assert.AreEqual(
            TopicMetadataValueOrigin.Unset,
            current.Metadata.ImportantStatementsOrigin);
        Assert.AreEqual(
            TopicMetadataValueOrigin.Unset,
            current.Metadata.PublicationCautionsOrigin);
    }

    [TestMethod]
    public void MainSpeakerOverrideRequiresActualTopicOverlap()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);
        TopicMetadataRevision initial = fixture.CreateInitial();
        EntityId<TranscriptSpeakerIdTag> outsideSpeaker =
            Id<TranscriptSpeakerIdTag>(
                "81000000-0000-0000-0000-000000000099");

        AssertFailure(
            () => fixture.SetMainSpeaker(initial, 2, outsideSpeaker),
            TopicMetadataFailureReason.MainSpeakerNotInTopic);
    }

    [TestMethod]
    public void ImportantStatementsMustBeUniqueKnownAndFullyInsideTopic()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: false);
        TopicMetadataRevision initial = fixture.CreateInitial();

        AssertFailure(
            () => fixture.SetImportantStatements(
                initial,
                2,
                [fixture.SourceSpans[^1].Id]),
            TopicMetadataFailureReason.ImportantStatementOutsideTopic);
        AssertFailure(
            () => fixture.SetImportantStatements(
                initial,
                2,
                [EntityId<SourceSpanIdTag>.New()]),
            TopicMetadataFailureReason.ImportantStatementOutsideTopic);
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.SetImportantStatements(
                initial,
                2,
                [fixture.SourceSpans[0].Id, fixture.SourceSpans[0].Id]));
    }

    [TestMethod]
    public void ContextRejectsStaleTranscriptRangeAssignmentAndSourceEvidence()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);
        TopicMetadataRevision initial = fixture.CreateInitial();
        Fixture changedTranscript = Fixture.CreateDefault(
            withSpeakerAssignment: false,
            revisionId: fixture.Revision.Id,
            firstText: "Changed private text");
        TopicMetadataException transcriptFailure = AssertFailure(
            () => TopicMetadataService.SetTitle(
                fixture.Project,
                changedTranscript.Revision,
                changedTranscript.SourceSpans,
                fixture.TopicRange,
                speakerAssignments: null,
                initial,
                RevisionId(2),
                "Changed title",
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Edit title."),
            TopicMetadataFailureReason.TopicRangeBindingMismatch);
        Assert.IsFalse(transcriptFailure.ToString().Contains(
            "Changed private text",
            StringComparison.Ordinal));

        TopicRangeRevision revisedRange = TopicBoundaryService.ReviseTopicRange(
            fixture.BoundaryScan,
            fixture.TopicRange,
            Id<TopicRangeRevisionIdTag>(
                "82000000-0000-0000-0000-000000000099"),
            Range(0, 5),
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Move range.");
        AssertFailure(
            () => TopicMetadataService.SetTitle(
                fixture.Project,
                fixture.Revision,
                fixture.SourceSpans,
                revisedRange,
                fixture.SpeakerAssignments,
                initial,
                RevisionId(2),
                "Changed title",
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Edit title."),
            TopicMetadataFailureReason.ContextMismatch);

        SourceSpan[] wrongSpans = fixture.SourceSpans.ToArray();
        wrongSpans[0] = wrongSpans[1];
        AssertFailure(
            () => TopicMetadataService.SetTitle(
                fixture.Project,
                fixture.Revision,
                wrongSpans,
                fixture.TopicRange,
                fixture.SpeakerAssignments,
                initial,
                RevisionId(2),
                "Changed title",
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Edit title."),
            TopicMetadataFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void NoOpDuplicateRevisionAndBackwardTimeFailClosed()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);
        TopicMetadataRevision initial = fixture.CreateInitial();
        TopicMetadataRevision confirmed = fixture.SetTitle(
            initial,
            2,
            initial.Metadata.Title);

        Assert.AreEqual(
            TopicMetadataValueOrigin.ManualEditorial,
            confirmed.Metadata.TitleOrigin);

        AssertFailure(
            () => fixture.SetTitle(confirmed, 3, confirmed.Metadata.Title),
            TopicMetadataFailureReason.NoChange);
        AssertFailure(
            () => TopicMetadataService.SetTitle(
                fixture.Project,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TopicRange,
                fixture.SpeakerAssignments,
                initial,
                initial.Id,
                "New title",
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Duplicate ID."),
            TopicMetadataFailureReason.RevisionConflict);
        AssertFailure(
            () => TopicMetadataService.SetTitle(
                fixture.Project,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TopicRange,
                fixture.SpeakerAssignments,
                initial,
                RevisionId(2),
                "New title",
                ActorSid,
                CreatedAt.AddTicks(-1),
                "Backward time."),
            TopicMetadataFailureReason.RevisionConflict);
    }

    [TestMethod]
    public void InvalidManualTextListsSidAndOrdinalAreRejectedPrivately()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: false);
        TopicMetadataRevision initial = fixture.CreateInitial();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            fixture.CreateInitial(topicOrdinal: 0));
        TopicMetadataException title = AssertFailure(
            () => fixture.SetTitle(initial, 2, " private\uD800title"),
            TopicMetadataFailureReason.InvalidText);
        Assert.IsFalse(title.ToString().Contains("private", StringComparison.Ordinal));
        AssertFailure(
            () => fixture.SetKeywords(initial, 2, ["Same", "Same"]),
            TopicMetadataFailureReason.InvalidText);
        AssertFailure(
            () => TopicMetadataService.SetAudience(
                fixture.Project,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TopicRange,
                fixture.SpeakerAssignments,
                initial,
                RevisionId(2),
                "New audience",
                "not-a-sid",
                CreatedAt.AddMinutes(1),
                "Edit audience."),
            TopicMetadataFailureReason.InvalidText);
    }

    [TestMethod]
    public void PublicSurfaceIsImmutableAndHasNoModelGenerationMethod()
    {
        Fixture fixture = Fixture.CreateDefault(withSpeakerAssignment: true);
        TopicMetadata metadata = fixture.CreateInitial().Metadata;
        foreach (Type type in new[]
        {
            typeof(TopicMainSpeakerSnapshot),
            typeof(TopicMetadata),
            typeof(TopicMetadataRevision),
            typeof(TopicMetadataException),
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

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)metadata.Keywords).Add("Mutation"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<EntityId<SourceSpanIdTag>>)
                metadata.ImportantStatementSourceSpanIds).Clear());
        CollectionAssert.AreEqual(
            new[]
            {
                "CreateInitial",
                "SetTitle",
                "SetSummary",
                "SetMainSpeaker",
                "SetKeywords",
                "SetImportantStatements",
                "SetAudience",
                "SetPublicationCautions",
            },
            typeof(TopicMetadataService)
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(TopicMetadataService).GetMethod("GenerateSummary"));
        Assert.IsNull(typeof(TopicMetadataService).GetMethod("GenerateKeywords"));
    }

    private static TopicMetadataException AssertFailure(
        Action action,
        TopicMetadataFailureReason reason)
    {
        TopicMetadataException exception =
            Assert.ThrowsExactly<TopicMetadataException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static EntityId<TopicMetadataRevisionIdTag> RevisionId(int value) =>
        Id<TopicMetadataRevisionIdTag>(
            $"83000000-0000-0000-0000-{value:000000000000}");

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed record UtteranceInput(
        int Value,
        int Speaker,
        string Label,
        string Text,
        long Start,
        long Duration);

    private sealed class Fixture
    {
        private Fixture(
            Project project,
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans,
            TopicBoundaryScanResult boundaryScan,
            TopicRangeRevision topicRange,
            SpeakerAssignmentHistory? speakerAssignments,
            Performer firstPerformer,
            Performer secondPerformer,
            EntityId<TranscriptSpeakerIdTag> firstSpeakerId,
            EntityId<TranscriptSpeakerIdTag> secondSpeakerId)
        {
            Project = project;
            Revision = revision;
            SourceSpans = sourceSpans;
            BoundaryScan = boundaryScan;
            TopicRange = topicRange;
            SpeakerAssignments = speakerAssignments;
            FirstPerformer = firstPerformer;
            SecondPerformer = secondPerformer;
            FirstSpeakerId = firstSpeakerId;
            SecondSpeakerId = secondSpeakerId;
        }

        internal Project Project { get; }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal TopicBoundaryScanResult BoundaryScan { get; }

        internal TopicRangeRevision TopicRange { get; }

        internal SpeakerAssignmentHistory? SpeakerAssignments { get; }

        internal Performer FirstPerformer { get; }

        internal Performer SecondPerformer { get; }

        internal EntityId<TranscriptSpeakerIdTag> FirstSpeakerId { get; }

        internal EntityId<TranscriptSpeakerIdTag> SecondSpeakerId { get; }

        internal TopicMetadataRevision CreateInitial(int topicOrdinal = 1) =>
            TopicMetadataService.CreateInitial(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                topicOrdinal,
                Id<TopicMetadataIdTag>(
                    "84000000-0000-0000-0000-000000000001"),
                RevisionId(1),
                ActorSid,
                CreatedAt,
                "Create manual-first topic metadata.");

        internal TopicMetadataRevision SetTitle(
            TopicMetadataRevision current,
            int revisionNumber,
            string title) =>
            TopicMetadataService.SetTitle(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                title,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit topic title.");

        internal TopicMetadataRevision SetSummary(
            TopicMetadataRevision current,
            int revisionNumber,
            string? summary) =>
            TopicMetadataService.SetSummary(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                summary,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit topic summary.");

        internal TopicMetadataRevision SetMainSpeaker(
            TopicMetadataRevision current,
            int revisionNumber,
            EntityId<TranscriptSpeakerIdTag>? speakerId) =>
            TopicMetadataService.SetMainSpeaker(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                speakerId,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit main speaker.");

        internal TopicMetadataRevision SetKeywords(
            TopicMetadataRevision current,
            int revisionNumber,
            IReadOnlyList<string> keywords) =>
            TopicMetadataService.SetKeywords(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                keywords,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit keywords.");

        internal TopicMetadataRevision SetImportantStatements(
            TopicMetadataRevision current,
            int revisionNumber,
            IReadOnlyList<EntityId<SourceSpanIdTag>> sourceSpanIds) =>
            TopicMetadataService.SetImportantStatements(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                sourceSpanIds,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit important statements.");

        internal TopicMetadataRevision SetAudience(
            TopicMetadataRevision current,
            int revisionNumber,
            string audience) =>
            TopicMetadataService.SetAudience(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                audience,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit target audience.");

        internal TopicMetadataRevision SetCautions(
            TopicMetadataRevision current,
            int revisionNumber,
            IReadOnlyList<string> cautions) =>
            TopicMetadataService.SetPublicationCautions(
                Project,
                Revision,
                SourceSpans,
                TopicRange,
                SpeakerAssignments,
                current,
                RevisionId(revisionNumber),
                cautions,
                ActorSid,
                CreatedAt.AddMinutes(revisionNumber),
                "Edit publication cautions.");

        internal static Fixture CreateDefault(
            bool withSpeakerAssignment,
            EntityId<TranscriptRevisionIdTag>? revisionId = null,
            string firstText = "First statement") =>
            Create(
                [
                    new UtteranceInput(1, 1, "Speaker 1", firstText, 0, 2),
                    new UtteranceInput(2, 2, "Speaker 2", "Second statement", 1, 3),
                    new UtteranceInput(3, 1, "Speaker 1", "Third statement", 2, 2),
                    new UtteranceInput(4, 2, "Speaker 2", "Outside statement", 6, 1),
                ],
                Range(0, 6),
                withSpeakerAssignment,
                revisionId);

        internal static Fixture Create(
            IReadOnlyList<UtteranceInput> inputs,
            TimeRange topicRange,
            bool withSpeakerAssignment,
            EntityId<TranscriptRevisionIdTag>? revisionId = null)
        {
            EntityId<TranscriptRevisionIdTag> actualRevisionId = revisionId
                ?? Id<TranscriptRevisionIdTag>(
                    "85000000-0000-0000-0000-000000000001");
            EntityId<TranscriptSpeakerIdTag> firstSpeakerId =
                Id<TranscriptSpeakerIdTag>(
                    "86000000-0000-0000-0000-000000000001");
            EntityId<TranscriptSpeakerIdTag> secondSpeakerId =
                Id<TranscriptSpeakerIdTag>(
                    "86000000-0000-0000-0000-000000000002");
            var firstPerformer = new Performer(
                Id<PerformerIdTag>("87000000-0000-0000-0000-000000000001"),
                "Host Alpha",
                "Host",
                CreatedAt.AddDays(-1));
            var secondPerformer = new Performer(
                Id<PerformerIdTag>("87000000-0000-0000-0000-000000000002"),
                "Guest Beta",
                "Guest",
                titleConfirmedAt: null);
            var project = new Project(
                Id<ProjectIdTag>("88000000-0000-0000-0000-000000000001"),
                "Topic metadata test",
                "Verify topic metadata editing.",
                "Internal engineers",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: [firstPerformer, secondPerformer]);
            var utterances = new List<Utterance>(inputs.Count);
            var sourceSpans = new List<SourceSpan>(inputs.Count);
            var digest = new ContentDigest(new string('a', 64));
            foreach (UtteranceInput input in inputs)
            {
                TimeRange range = Range(input.Start, input.Duration);
                EntityId<TranscriptTokenIdTag> tokenId = Id<TranscriptTokenIdTag>(
                    $"89000000-0000-0000-0000-{input.Value:000000000000}");
                var token = new Token(
                    tokenId,
                    input.Text,
                    range: null,
                    recognitionConfidence: 0.75);
                var sentence = new Sentence(
                    Id<TranscriptSentenceIdTag>(
                        $"8a000000-0000-0000-0000-{input.Value:000000000000}"),
                    input.Text,
                    [token]);
                EntityId<SourceSpanIdTag> sourceSpanId = Id<SourceSpanIdTag>(
                    $"8b000000-0000-0000-0000-{input.Value:000000000000}");
                EntityId<TranscriptSpeakerIdTag> speakerId = input.Speaker == 1
                    ? firstSpeakerId
                    : secondSpeakerId;
                utterances.Add(new Utterance(
                    Id<TranscriptUtteranceIdTag>(
                        $"8c000000-0000-0000-0000-{input.Value:000000000000}"),
                    sourceSpanId,
                    speakerId,
                    input.Label,
                    input.Text,
                    range,
                    recognitionConfidence: 0.75,
                    [sentence]));
                sourceSpans.Add(new SourceSpan(
                    sourceSpanId,
                    Id<AssetIdTag>("8d000000-0000-0000-0000-000000000001"),
                    "audio:1",
                    range,
                    digest,
                    actualRevisionId,
                    [tokenId]));
            }

            var transcript = new TranscriptRevision(
                actualRevisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                utterances);
            EntityId<HubRadio.Core.Analysis.ModelRunIdTag> modelRunId =
                Id<HubRadio.Core.Analysis.ModelRunIdTag>(
                    "8e000000-0000-0000-0000-000000000001");
            TopicBoundaryScanResult boundaryScan = TopicBoundaryService.FindCandidates(
                transcript,
                sourceSpans,
                modelRunId);
            TopicRangeRevision rangeRevision = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                Id<TopicRangeIdTag>(
                    "8f000000-0000-0000-0000-000000000001"),
                Id<TopicRangeRevisionIdTag>(
                    "90000000-0000-0000-0000-000000000001"),
                null,
                null,
                topicRange,
                ActorSid,
                CreatedAt,
                "Define topic range.");
            SpeakerAssignmentHistory? assignments = withSpeakerAssignment
                ? SpeakerAssignmentService.SetAssignment(
                    project,
                    transcript,
                    history: null,
                    Id<SpeakerAssignmentRevisionIdTag>(
                        "91000000-0000-0000-0000-000000000001"),
                    firstSpeakerId,
                    firstPerformer.Id,
                    "Alpha",
                    SpeakerDisplayFormat.FullName,
                    ActorSid,
                    CreatedAt,
                    "Assign first speaker.")
                : null;
            if (withSpeakerAssignment)
            {
                assignments = SpeakerAssignmentService.SetAssignment(
                    project,
                    transcript,
                    assignments,
                    Id<SpeakerAssignmentRevisionIdTag>(
                        "91000000-0000-0000-0000-000000000002"),
                    secondSpeakerId,
                    secondPerformer.Id,
                    "Beta",
                    SpeakerDisplayFormat.FullName,
                    ActorSid,
                    CreatedAt.AddMinutes(1),
                    "Assign second speaker.");
            }

            return new Fixture(
                project,
                transcript,
                sourceSpans,
                boundaryScan,
                rangeRevision,
                assignments,
                firstPerformer,
                secondPerformer,
                firstSpeakerId,
                secondSpeakerId);
        }
    }
}