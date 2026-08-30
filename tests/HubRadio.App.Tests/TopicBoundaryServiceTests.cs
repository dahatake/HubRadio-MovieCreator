using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TopicBoundaryServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2039, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [TestMethod]
    public void FindCandidates_ReturnsEveryStructuralTransitionInOrder()
    {
        Fixture fixture = Fixture.CreateDefault();

        TopicBoundaryScanResult result = fixture.Scan();

        Assert.AreEqual(fixture.Revision.Id, result.SourceRevisionId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(fixture.Revision))),
            result.SourceRevisionDigest);
        Assert.AreEqual(fixture.ModelRunId, result.ModelRunId);
        Assert.AreEqual(TopicBoundaryService.RuleVersion, result.RuleVersion);
        Assert.AreEqual(2, result.InspectedTransitionCount);
        Assert.AreEqual(Time(0), result.TranscriptStart);
        Assert.AreEqual(Time(6), result.TranscriptEnd);
        Assert.HasCount(2, result.Candidates);
        CollectionAssert.AreEqual(
            new[] { Time(3), Time(4) },
            result.Candidates
                .Select(static candidate => candidate.SuggestedBoundary)
                .ToArray());
        CollectionAssert.AreEqual(
            fixture.Revision.Utterances.Skip(1)
                .Select(static utterance => utterance.Id)
                .ToArray(),
            result.Candidates
                .Select(static candidate => candidate.Features.CurrentUtteranceId)
                .ToArray());
    }

    [TestMethod]
    public void CandidateUsesOnlyObjectiveFeaturesAndRemainsReviewOnly()
    {
        Fixture fixture = Fixture.CreateDefault();

        TopicBoundaryCandidate sameSpeaker = fixture.Scan().Candidates[0];
        TopicBoundaryCandidate speakerChange = fixture.Scan().Candidates[1];

        Assert.AreEqual(DetectionKind.TopicBoundary, sameSpeaker.Detection.Kind);
        Assert.AreEqual(SuggestionKind.TopicBoundary, sameSpeaker.Suggestion.Kind);
        Assert.AreEqual(SuggestionStatus.Proposed, sameSpeaker.Suggestion.Status);
        Assert.IsNull(sameSpeaker.Detection.Confidence);
        Assert.IsNull(sameSpeaker.Suggestion.Confidence);
        Assert.AreEqual(sameSpeaker.SourceSpan.Id, sameSpeaker.Detection.Origin.SourceSpanId);
        Assert.AreEqual(sameSpeaker.SourceSpan.Id, sameSpeaker.Suggestion.Origin.SourceSpanId);
        Assert.AreEqual(sameSpeaker.SourceSpan.Id, sameSpeaker.Features.CurrentSourceSpanId);
        Assert.AreEqual(fixture.SourceSpans[0].Id, sameSpeaker.Features.PreviousSourceSpanId);
        Assert.AreEqual(Time(1), sameSpeaker.Features.ObservedGap);
        Assert.IsFalse(sameSpeaker.Features.SpeakerChanged);
        Assert.AreEqual(Time(-1), speakerChange.Features.ObservedGap);
        Assert.IsTrue(speakerChange.Features.SpeakerChanged);
        Assert.IsTrue(sameSpeaker.Features.StructurallyAdjacent);
        Assert.IsFalse(sameSpeaker.Features.SemanticClassificationPerformed);
        Assert.IsFalse(sameSpeaker.Features.ApprovedPauseOrSimilarityThresholdApplied);
        Assert.IsFalse(sameSpeaker.Features.OptionalShotBoundaryUsed);
        StringAssert.Contains(sameSpeaker.Suggestion.Reason, "manual topic-boundary review");
        StringAssert.Contains(speakerChange.Suggestion.Reason, "speaker change");
        Assert.AreEqual(
            "No semantic classifier or approved pause, similarity, or shot threshold was applied.",
            sameSpeaker.Suggestion.MissingContext);
        Assert.AreEqual(
            "A structural utterance transition is not a confirmed semantic topic boundary.",
            sameSpeaker.Suggestion.Warning);
        Assert.IsFalse(sameSpeaker.Suggestion.Reason.Contains(
            fixture.Revision.Utterances[0].Text,
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void EmptyAndSingleUtteranceRevisionsProduceNoTransitionCandidates()
    {
        Fixture empty = Fixture.Create([]);
        Fixture single = Fixture.Create(
            [new UtteranceInput(1, 1, "Speaker 1", "Only", 0, 2)]);

        TopicBoundaryScanResult emptyResult = empty.Scan();
        TopicBoundaryScanResult singleResult = single.Scan();

        Assert.AreEqual(0, emptyResult.InspectedTransitionCount);
        Assert.HasCount(0, emptyResult.Candidates);
        Assert.IsNull(emptyResult.TranscriptStart);
        Assert.IsNull(emptyResult.TranscriptEnd);
        Assert.AreEqual(0, singleResult.InspectedTransitionCount);
        Assert.HasCount(0, singleResult.Candidates);
        Assert.AreEqual(Time(0), singleResult.TranscriptStart);
        Assert.AreEqual(Time(2), singleResult.TranscriptEnd);
    }

    [TestMethod]
    public void FindCandidatesRejectsMissingMismatchedAndDuplicateSourceEvidence()
    {
        Fixture fixture = Fixture.CreateDefault();
        AssertFailure(
            () => TopicBoundaryService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans.Take(2).ToArray(),
                fixture.ModelRunId),
            TopicBoundaryFailureReason.SourceSpanBindingMismatch);

        SourceSpan source = fixture.SourceSpans[1];
        var wrongRange = new SourceSpan(
            source.Id,
            source.AssetId,
            source.StreamId,
            Range(3, 1),
            source.SourceDigest,
            source.TranscriptRevisionId,
            source.TranscriptTokenIds);
        SourceSpan[] mismatched = fixture.SourceSpans.ToArray();
        mismatched[1] = wrongRange;
        AssertFailure(
            () => TopicBoundaryService.FindCandidates(
                fixture.Revision,
                mismatched,
                fixture.ModelRunId),
            TopicBoundaryFailureReason.SourceSpanBindingMismatch);

        SourceSpan[] duplicate = fixture.SourceSpans.ToArray();
        duplicate[1] = duplicate[0];
        AssertFailure(
            () => TopicBoundaryService.FindCandidates(
                fixture.Revision,
                duplicate,
                fixture.ModelRunId),
            TopicBoundaryFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void FindCandidatesHonorsPreCancellation()
    {
        Fixture fixture = Fixture.CreateDefault();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            TopicBoundaryService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.ModelRunId,
                cancellation.Token));
    }

    [TestMethod]
    public void ManualTopicRangePreservesExactHumanRangeWithoutClamping()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicBoundaryScanResult scan = fixture.Scan();
        TimeRange humanRange = new(Time(1, 2), Time(5));

        TopicRangeRevision range = TopicBoundaryService.CreateTopicRange(
            scan,
            TopicId(1),
            RangeRevisionId(1),
            scan.Candidates[0].Suggestion.Id,
            scan.Candidates[1].Suggestion.Id,
            humanRange,
            ActorSid,
            CreatedAt,
            "Use the human-selected topic range.");

        Assert.AreEqual(humanRange, range.Range);
        Assert.AreEqual(Time(1, 2), range.Range.Start);
        Assert.AreEqual(Time(11, 2), range.Range.End);
        Assert.AreNotEqual(scan.Candidates[0].SuggestedBoundary, range.Range.Start);
        Assert.AreNotEqual(scan.Candidates[1].SuggestedBoundary, range.Range.End);
        Assert.IsNull(range.ParentRevisionId);
        Assert.IsNull(range.PreviousRange);
        Assert.AreEqual(scan.SourceRevisionId, range.SourceRevisionId);
        Assert.AreEqual(scan.SourceRevisionDigest, range.SourceRevisionDigest);
    }

    [TestMethod]
    public void ManualTopicRangeCanStartOrEndAtTranscriptExtentWithoutCandidate()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicBoundaryScanResult scan = fixture.Scan();
        TopicRangeRevision first = TopicBoundaryService.CreateTopicRange(
            scan,
            TopicId(1),
            RangeRevisionId(1),
            startBoundarySuggestionId: null,
            scan.Candidates[0].Suggestion.Id,
            Range(0, 3),
            ActorSid,
            CreatedAt,
            "Create the first topic.");
        TopicRangeRevision last = TopicBoundaryService.CreateTopicRange(
            scan,
            TopicId(2),
            RangeRevisionId(2),
            scan.Candidates[1].Suggestion.Id,
            endBoundarySuggestionId: null,
            Range(4, 2),
            ActorSid,
            CreatedAt,
            "Create the last topic.");

        Assert.IsNull(first.StartBoundarySuggestionId);
        Assert.AreEqual(scan.Candidates[0].Suggestion.Id, first.EndBoundarySuggestionId);
        Assert.AreEqual(scan.Candidates[1].Suggestion.Id, last.StartBoundarySuggestionId);
        Assert.IsNull(last.EndBoundarySuggestionId);
    }

    [TestMethod]
    public void ReviseTopicRangeCreatesHumanChildAndPreservesPreviousRange()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicBoundaryScanResult scan = fixture.Scan();
        TopicRangeRevision first = TopicBoundaryService.CreateTopicRange(
            scan,
            TopicId(1),
            RangeRevisionId(1),
            startBoundarySuggestionId: null,
            scan.Candidates[1].Suggestion.Id,
            Range(0, 4),
            ActorSid,
            CreatedAt,
            "Create a manual topic range.");
        TimeRange revised = new(Time(1, 2), Time(9, 2));

        TopicRangeRevision second = TopicBoundaryService.ReviseTopicRange(
            scan,
            first,
            RangeRevisionId(2),
            revised,
            ActorSid,
            CreatedAt.AddMinutes(1),
            "Move the topic start and end.");

        Assert.AreEqual(first.TopicRangeId, second.TopicRangeId);
        Assert.AreEqual(first.Id, second.ParentRevisionId);
        Assert.AreEqual(first.Range, second.PreviousRange);
        Assert.AreEqual(revised, second.Range);
        Assert.AreEqual(first.StartBoundarySuggestionId, second.StartBoundarySuggestionId);
        Assert.AreEqual(first.EndBoundarySuggestionId, second.EndBoundarySuggestionId);
        Assert.AreEqual(Range(0, 4), first.Range);
    }

    [TestMethod]
    public void ManualRangeRejectsOutsideZeroAndForeignOrReversedReferences()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicBoundaryScanResult scan = fixture.Scan();
        AssertFailure(
            () => TopicBoundaryService.CreateTopicRange(
                scan,
                TopicId(1),
                RangeRevisionId(1),
                null,
                null,
                Range(-1, 2),
                ActorSid,
                CreatedAt,
                "Outside start."),
            TopicBoundaryFailureReason.RangeOutsideTranscript);
        AssertFailure(
            () => TopicBoundaryService.CreateTopicRange(
                scan,
                TopicId(1),
                RangeRevisionId(1),
                null,
                null,
                new TimeRange(Time(1), default),
                ActorSid,
                CreatedAt,
                "Zero duration."),
            TopicBoundaryFailureReason.RangeOutsideTranscript);
        AssertFailure(
            () => TopicBoundaryService.CreateTopicRange(
                scan,
                TopicId(1),
                RangeRevisionId(1),
                EntityId<SuggestionIdTag>.New(),
                null,
                Range(0, 1),
                ActorSid,
                CreatedAt,
                "Foreign candidate."),
            TopicBoundaryFailureReason.CandidateBindingMismatch);
        AssertFailure(
            () => TopicBoundaryService.CreateTopicRange(
                scan,
                TopicId(1),
                RangeRevisionId(1),
                scan.Candidates[1].Suggestion.Id,
                scan.Candidates[0].Suggestion.Id,
                Range(0, 1),
                ActorSid,
                CreatedAt,
                "Reversed candidates."),
            TopicBoundaryFailureReason.CandidateBindingMismatch);
    }

    [TestMethod]
    public void RevisionRejectsNoOpDuplicateIdBackwardTimeAndForeignScan()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicBoundaryScanResult scan = fixture.Scan();
        TopicRangeRevision current = TopicBoundaryService.CreateTopicRange(
            scan,
            TopicId(1),
            RangeRevisionId(1),
            null,
            null,
            Range(0, 3),
            ActorSid,
            CreatedAt,
            "Create range.");
        AssertFailure(
            () => TopicBoundaryService.ReviseTopicRange(
                scan,
                current,
                RangeRevisionId(2),
                current.Range,
                ActorSid,
                CreatedAt.AddMinutes(1),
                "No change."),
            TopicBoundaryFailureReason.NoChange);
        AssertFailure(
            () => TopicBoundaryService.ReviseTopicRange(
                scan,
                current,
                current.Id,
                Range(0, 4),
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Duplicate ID."),
            TopicBoundaryFailureReason.RevisionConflict);
        AssertFailure(
            () => TopicBoundaryService.ReviseTopicRange(
                scan,
                current,
                RangeRevisionId(2),
                Range(0, 4),
                ActorSid,
                CreatedAt.AddTicks(-1),
                "Backward time."),
            TopicBoundaryFailureReason.RevisionConflict);

        Fixture other = Fixture.CreateDefault(
            revisionId: Id<TranscriptRevisionIdTag>(
                "61000000-0000-0000-0000-000000000099"));
        AssertFailure(
            () => TopicBoundaryService.ReviseTopicRange(
                other.Scan(),
                current,
                RangeRevisionId(2),
                Range(0, 4),
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Foreign scan."),
            TopicBoundaryFailureReason.RevisionConflict);
    }

    [TestMethod]
    public void ManualRangeRejectsMalformedActorAndReasonPrivately()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicBoundaryScanResult scan = fixture.Scan();
        TopicBoundaryException actor = AssertFailure(
            () => TopicBoundaryService.CreateTopicRange(
                scan,
                TopicId(1),
                RangeRevisionId(1),
                null,
                null,
                Range(0, 1),
                "not-a-private-sid",
                CreatedAt,
                "Reason"),
            TopicBoundaryFailureReason.InvalidText);
        TopicBoundaryException reason = AssertFailure(
            () => TopicBoundaryService.CreateTopicRange(
                scan,
                TopicId(1),
                RangeRevisionId(1),
                null,
                null,
                Range(0, 1),
                ActorSid,
                CreatedAt,
                "private\uD800reason"),
            TopicBoundaryFailureReason.InvalidText);

        Assert.IsFalse(actor.ToString().Contains("private", StringComparison.Ordinal));
        Assert.IsFalse(reason.ToString().Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExtremeValidTranscriptTimesFailWithStableArithmeticError()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(1, 1, "Speaker 1", "First", long.MinValue, 1),
                new UtteranceInput(2, 1, "Speaker 1", "Second", long.MaxValue - 1, 1),
            ]);

        AssertFailure(
            () => fixture.Scan(),
            TopicBoundaryFailureReason.ArithmeticOverflow);
    }

    [TestMethod]
    public void PublicSurfaceIsImmutableAndHasNoSemanticOrShotInput()
    {
        foreach (Type type in new[]
        {
            typeof(TopicBoundaryFeatures),
            typeof(TopicBoundaryCandidate),
            typeof(TopicBoundaryScanResult),
            typeof(TopicRangeRevision),
            typeof(TopicBoundaryException),
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

        CollectionAssert.AreEqual(
            new[] { "FindCandidates", "CreateTopicRange", "ReviseTopicRange" },
            typeof(TopicBoundaryService)
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(TopicBoundaryService).GetMethod("ClassifySemantics"));
        Assert.IsNull(typeof(TopicBoundaryService).GetMethod("UseShotBoundary"));
    }

    private static TopicBoundaryException AssertFailure(
        Action action,
        TopicBoundaryFailureReason reason)
    {
        TopicBoundaryException exception =
            Assert.ThrowsExactly<TopicBoundaryException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static TimeRange Range(long start, long duration) =>
        new(Time(start), Time(duration));

    private static EntityId<TopicRangeIdTag> TopicId(int value) =>
        Id<TopicRangeIdTag>($"62000000-0000-0000-0000-{value:000000000000}");

    private static EntityId<TopicRangeRevisionIdTag> RangeRevisionId(int value) =>
        Id<TopicRangeRevisionIdTag>(
            $"63000000-0000-0000-0000-{value:000000000000}");

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed record UtteranceInput(
        int Value,
        int Speaker,
        string SpeakerLabel,
        string Text,
        long Start,
        long Duration);

    private sealed class Fixture
    {
        private Fixture(
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans,
            EntityId<ModelRunIdTag> modelRunId)
        {
            Revision = revision;
            SourceSpans = sourceSpans;
            ModelRunId = modelRunId;
        }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal TopicBoundaryScanResult Scan() =>
            TopicBoundaryService.FindCandidates(
                Revision,
                SourceSpans,
                ModelRunId);

        internal static Fixture CreateDefault(
            EntityId<TranscriptRevisionIdTag>? revisionId = null) =>
            Create(
                [
                    new UtteranceInput(1, 1, "Speaker 1", "First private text", 0, 2),
                    new UtteranceInput(2, 1, "Speaker 1", "Second private text", 3, 2),
                    new UtteranceInput(3, 2, "Speaker 2", "Third private text", 4, 2),
                ],
                revisionId);

        internal static Fixture Create(
            IReadOnlyList<UtteranceInput> inputs,
            EntityId<TranscriptRevisionIdTag>? revisionId = null)
        {
            EntityId<TranscriptRevisionIdTag> actualRevisionId = revisionId
                ?? Id<TranscriptRevisionIdTag>(
                    "61000000-0000-0000-0000-000000000001");
            var utterances = new List<Utterance>(inputs.Count);
            var sourceSpans = new List<SourceSpan>(inputs.Count);
            var digest = new ContentDigest(new string('a', 64));
            foreach (UtteranceInput input in inputs)
            {
                TimeRange range = Range(input.Start, input.Duration);
                EntityId<TranscriptTokenIdTag> tokenId =
                    Id<TranscriptTokenIdTag>(
                        $"64000000-0000-0000-0000-{input.Value:000000000000}");
                var token = new Token(
                    tokenId,
                    input.Text,
                    range: null,
                    recognitionConfidence: 0.75);
                var sentence = new Sentence(
                    Id<TranscriptSentenceIdTag>(
                        $"65000000-0000-0000-0000-{input.Value:000000000000}"),
                    input.Text,
                    [token]);
                EntityId<SourceSpanIdTag> spanId = Id<SourceSpanIdTag>(
                    $"66000000-0000-0000-0000-{input.Value:000000000000}");
                var utterance = new Utterance(
                    Id<TranscriptUtteranceIdTag>(
                        $"67000000-0000-0000-0000-{input.Value:000000000000}"),
                    spanId,
                    Id<TranscriptSpeakerIdTag>(
                        $"68000000-0000-0000-0000-{input.Speaker:000000000000}"),
                    input.SpeakerLabel,
                    input.Text,
                    range,
                    recognitionConfidence: 0.75,
                    [sentence]);
                utterances.Add(utterance);
                sourceSpans.Add(new SourceSpan(
                    spanId,
                    Id<AssetIdTag>("69000000-0000-0000-0000-000000000001"),
                    "audio:1",
                    range,
                    digest,
                    actualRevisionId,
                    [tokenId]));
            }

            return new Fixture(
                new TranscriptRevision(
                    actualRevisionId,
                    parentRevisionId: null,
                    TranscriptChangeSource.Machine,
                    CreatedAt,
                    utterances),
                sourceSpans,
                Id<ModelRunIdTag>("6a000000-0000-0000-0000-000000000001"));
        }
    }
}