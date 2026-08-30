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
public sealed class ContextDeviationDetectorTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2040, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [TestMethod]
    public void FindCandidatesClassifiesOnlyFullyOutsideUtterances()
    {
        Fixture fixture = Fixture.CreateDefault();

        ContextDeviationScanResult result = fixture.Scan();

        Assert.AreEqual(fixture.Revision.Id, result.SourceRevisionId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(fixture.Revision))),
            result.SourceRevisionDigest);
        Assert.AreEqual(fixture.ModelRunId, result.ModelRunId);
        Assert.AreEqual(ContextDeviationDetector.RuleVersion, result.RuleVersion);
        Assert.AreEqual(7, result.InspectedUtteranceCount);
        Assert.AreEqual(3, result.InsideTopicUtteranceCount);
        Assert.AreEqual(1, result.PartiallyCoveredUtteranceCount);
        Assert.AreEqual(0, result.UnclassifiedWithoutTopicContextCount);
        Assert.IsTrue(result.TopicContextAvailable);
        Assert.HasCount(3, result.Candidates);
        CollectionAssert.AreEqual(
            new[]
            {
                ContextDeviationPosition.LeadingBeforeFirstTopic,
                ContextDeviationPosition.BetweenTopicRanges,
                ContextDeviationPosition.TrailingAfterLastTopic,
            },
            result.Candidates
                .Select(static candidate => candidate.Features.Position)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { Time(0), Time(6), Time(12) },
            result.Candidates
                .Select(static candidate => candidate.SourceSpan.Range.Start)
                .ToArray());
    }

    [TestMethod]
    public void CandidatesExposeExactStructuralDistanceWithoutSemanticScore()
    {
        Fixture fixture = Fixture.CreateDefault();
        IReadOnlyList<ContextDeviationCandidate> candidates =
            fixture.Scan().Candidates;
        ContextDeviationCandidate leading = candidates[0];
        ContextDeviationCandidate between = candidates[1];
        ContextDeviationCandidate trailing = candidates[2];

        Assert.AreEqual(DetectionKind.PreparationConversation, leading.Detection.Kind);
        Assert.AreEqual(DetectionKind.ContextDeviation, between.Detection.Kind);
        Assert.AreEqual(DetectionKind.PreparationConversation, trailing.Detection.Kind);
        Assert.IsNull(leading.Features.PreviousTopicRangeId);
        Assert.AreEqual(fixture.TopicRanges[0].TopicRangeId, leading.Features.NextTopicRangeId);
        Assert.AreEqual(Time(1), leading.Features.DistanceToNearestTopicRange);
        Assert.AreEqual(fixture.TopicRanges[0].TopicRangeId, between.Features.PreviousTopicRangeId);
        Assert.AreEqual(fixture.TopicRanges[1].TopicRangeId, between.Features.NextTopicRangeId);
        Assert.AreEqual(default(RationalTime), between.Features.DistanceToNearestTopicRange);
        Assert.AreEqual(fixture.TopicRanges[1].TopicRangeId, trailing.Features.PreviousTopicRangeId);
        Assert.IsNull(trailing.Features.NextTopicRangeId);
        Assert.AreEqual(default(RationalTime), trailing.Features.DistanceToNearestTopicRange);
        Assert.IsTrue(candidates.All(static candidate =>
            candidate.Features.FullyOutsideExplicitTopicRanges
            && !candidate.Features.SemanticDistanceComputed
            && !candidate.Features.TimeThresholdApplied
            && !candidate.Features.AutomaticDeletionAllowed));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ContextDeviationFeatures(
                ContextDeviationPosition.LeadingBeforeFirstTopic,
                fixture.Revision.Utterances[0].Id,
                fixture.SourceSpans[0].Id,
                fixture.Revision.Utterances[0].SpeakerId,
                fixture.Revision.Utterances[0].Range,
                previousTopicRangeId: null,
                fixture.TopicRanges[0].TopicRangeId,
                new RationalTime(-1, 1)));
    }

    [TestMethod]
    public void CandidatesAreProposedRevisionBoundAndDoNotLeakTranscriptContent()
    {
        Fixture fixture = Fixture.CreateDefault();

        foreach (ContextDeviationCandidate candidate in fixture.Scan().Candidates)
        {
            Assert.AreEqual(SuggestionKind.DeleteRange, candidate.Suggestion.Kind);
            Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
            Assert.IsNull(candidate.Detection.Confidence);
            Assert.IsNull(candidate.Suggestion.Confidence);
            Assert.AreEqual(candidate.SourceSpan.Id, candidate.Features.SourceSpanId);
            Assert.AreEqual(candidate.SourceSpan.Id, candidate.Detection.Origin.SourceSpanId);
            Assert.AreEqual(candidate.SourceSpan.Id, candidate.Suggestion.Origin.SourceSpanId);
            Assert.AreEqual(fixture.Revision.Id, candidate.SourceSpan.TranscriptRevisionId);
            Assert.IsNotEmpty(candidate.SourceSpan.TranscriptTokenIds);
            Assert.AreEqual(
                "No semantic distance, phrase classifier, or time threshold was applied.",
                candidate.Suggestion.MissingContext);
            Assert.AreEqual(
                "The explicit topic ranges do not prove that this utterance should be deleted.",
                candidate.Suggestion.Warning);
            Assert.IsTrue(candidate.ExplicitHumanDecisionRequired);
            Assert.IsFalse(fixture.Revision.Utterances.Any(utterance =>
                candidate.Suggestion.Reason.Contains(
                    utterance.Text,
                    StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void NoExplicitTopicRangesReturnsTransparentZeroCandidateResult()
    {
        Fixture fixture = Fixture.CreateDefault();

        ContextDeviationScanResult result = ContextDeviationDetector.FindCandidates(
            fixture.Revision,
            fixture.SourceSpans,
            topicRanges: [],
            fixture.ModelRunId);

        Assert.AreEqual(7, result.InspectedUtteranceCount);
        Assert.AreEqual(0, result.InsideTopicUtteranceCount);
        Assert.AreEqual(0, result.PartiallyCoveredUtteranceCount);
        Assert.AreEqual(7, result.UnclassifiedWithoutTopicContextCount);
        Assert.IsFalse(result.TopicContextAvailable);
        Assert.HasCount(0, result.Candidates);
    }

    [TestMethod]
    public void EmptyTranscriptWithoutRangesIsValidAndDoesNotInventContext()
    {
        Fixture empty = Fixture.Create([]);

        ContextDeviationScanResult result = empty.Scan(topicRanges: []);

        Assert.AreEqual(0, result.InspectedUtteranceCount);
        Assert.AreEqual(0, result.UnclassifiedWithoutTopicContextCount);
        Assert.IsFalse(result.TopicContextAvailable);
        Assert.HasCount(0, result.Candidates);
    }

    [TestMethod]
    public void TopicRangePartialOverlapNeverCreatesWholeUtteranceDeleteCandidate()
    {
        Fixture fixture = Fixture.CreateDefault();
        EntityId<TranscriptUtteranceIdTag> partialUtteranceId =
            fixture.Revision.Utterances[4].Id;

        ContextDeviationScanResult result = fixture.Scan();

        Assert.AreEqual(1, result.PartiallyCoveredUtteranceCount);
        Assert.IsFalse(result.Candidates.Any(
            candidate => candidate.Features.UtteranceId == partialUtteranceId));
    }

    [TestMethod]
    public void SourceSpanMismatchFailsClosed()
    {
        Fixture fixture = Fixture.CreateDefault();
        AssertFailure(
            () => ContextDeviationDetector.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans.Take(6).ToArray(),
                fixture.TopicRanges,
                fixture.ModelRunId),
            ContextDeviationFailureReason.SourceSpanBindingMismatch);

        SourceSpan[] wrong = fixture.SourceSpans.ToArray();
        SourceSpan original = wrong[0];
        wrong[0] = new SourceSpan(
            original.Id,
            original.AssetId,
            original.StreamId,
            original.Range,
            original.SourceDigest,
            EntityId<TranscriptRevisionIdTag>.New(),
            original.TranscriptTokenIds);
        AssertFailure(
            () => ContextDeviationDetector.FindCandidates(
                fixture.Revision,
                wrong,
                fixture.TopicRanges,
                fixture.ModelRunId),
            ContextDeviationFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void TopicRangesRejectStaleDuplicateOverlappingAndDisorderedInput()
    {
        Fixture fixture = Fixture.CreateDefault();
        AssertFailure(
            () => fixture.Scan(
                [fixture.TopicRanges[1], fixture.TopicRanges[0]]),
            ContextDeviationFailureReason.TopicRangeOverlapOrDisorder);

        TopicRangeRevision duplicateTopic = CreateRange(
            fixture.BoundaryScan,
            topicValue: 1,
            revisionValue: 99,
            Range(8, 2));
        AssertFailure(
            () => fixture.Scan([fixture.TopicRanges[0], duplicateTopic]),
            ContextDeviationFailureReason.TopicRangeBindingMismatch);

        TopicRangeRevision overlapping = CreateRange(
            fixture.BoundaryScan,
            topicValue: 3,
            revisionValue: 3,
            Range(5, 4));
        AssertFailure(
            () => fixture.Scan([fixture.TopicRanges[0], overlapping]),
            ContextDeviationFailureReason.TopicRangeOverlapOrDisorder);

        Fixture changed = Fixture.CreateDefault(
            revisionId: fixture.Revision.Id,
            firstText: "Changed private transcript");
        ContextDeviationException stale = AssertFailure(
            () => ContextDeviationDetector.FindCandidates(
                changed.Revision,
                changed.SourceSpans,
                fixture.TopicRanges,
                changed.ModelRunId),
            ContextDeviationFailureReason.TopicRangeBindingMismatch);
        Assert.IsFalse(stale.ToString().Contains(
            "Changed private transcript",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void AdjacentTopicRangesAreValidAndLeaveNoInternalGapCandidate()
    {
        Fixture fixture = Fixture.CreateDefault();
        TopicRangeRevision adjacent = CreateRange(
            fixture.BoundaryScan,
            topicValue: 4,
            revisionValue: 4,
            Range(6, 2));

        ContextDeviationScanResult result = fixture.Scan(
            [fixture.TopicRanges[0], adjacent, fixture.TopicRanges[1]]);

        Assert.IsFalse(result.Candidates.Any(candidate =>
            candidate.Features.Position ==
                ContextDeviationPosition.BetweenTopicRanges));
        Assert.IsTrue(result.TopicContextAvailable);
    }

    [TestMethod]
    public void PreCancelledScanStopsBeforeProducingCandidates()
    {
        Fixture fixture = Fixture.CreateDefault();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            ContextDeviationDetector.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TopicRanges,
                fixture.ModelRunId,
                cancellation.Token));
    }

    [TestMethod]
    public void PublicSurfaceIsImmutableAndHasNoSemanticOrDeleteOperation()
    {
        Fixture fixture = Fixture.CreateDefault();
        ContextDeviationScanResult result = fixture.Scan();
        foreach (Type type in new[]
        {
            typeof(ContextDeviationFeatures),
            typeof(ContextDeviationCandidate),
            typeof(ContextDeviationScanResult),
            typeof(ContextDeviationException),
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

        IList<ContextDeviationCandidate> candidates =
            (IList<ContextDeviationCandidate>)result.Candidates;
        Assert.ThrowsExactly<NotSupportedException>(() => candidates.Clear());
        CollectionAssert.AreEqual(
            new[] { "FindCandidates" },
            typeof(ContextDeviationDetector)
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(ContextDeviationDetector).GetMethod("Delete"));
        Assert.IsNull(typeof(ContextDeviationDetector).GetMethod("SemanticDistance"));
    }

    private static ContextDeviationException AssertFailure(
        Action action,
        ContextDeviationFailureReason reason)
    {
        ContextDeviationException exception =
            Assert.ThrowsExactly<ContextDeviationException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\private\customer.mov",
            StringComparison.OrdinalIgnoreCase));
        return exception;
    }

    private static TopicRangeRevision CreateRange(
        TopicBoundaryScanResult scan,
        int topicValue,
        int revisionValue,
        TimeRange range) =>
        TopicBoundaryService.CreateTopicRange(
            scan,
            Id<TopicRangeIdTag>(
                $"71000000-0000-0000-0000-{topicValue:000000000000}"),
            Id<TopicRangeRevisionIdTag>(
                $"72000000-0000-0000-0000-{revisionValue:000000000000}"),
            startBoundarySuggestionId: null,
            endBoundarySuggestionId: null,
            range,
            ActorSid,
            CreatedAt,
            "Define explicit topic context.");

    private static RationalTime Time(long value) => new(value, 1);

    private static TimeRange Range(long start, long duration) =>
        new(Time(start), Time(duration));

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
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans,
            EntityId<ModelRunIdTag> modelRunId)
        {
            Revision = revision;
            SourceSpans = sourceSpans;
            ModelRunId = modelRunId;
            BoundaryScan = TopicBoundaryService.FindCandidates(
                revision,
                sourceSpans,
                modelRunId);
            TopicRanges = revision.Utterances.Count == 0
                ? []
                :
                [
                    CreateRange(BoundaryScan, 1, 1, Range(2, 4)),
                    CreateRange(BoundaryScan, 2, 2, Range(8, 4)),
                ];
        }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal TopicBoundaryScanResult BoundaryScan { get; }

        internal IReadOnlyList<TopicRangeRevision> TopicRanges { get; }

        internal ContextDeviationScanResult Scan(
            IReadOnlyList<TopicRangeRevision>? topicRanges = null) =>
            ContextDeviationDetector.FindCandidates(
                Revision,
                SourceSpans,
                topicRanges ?? TopicRanges,
                ModelRunId);

        internal static Fixture CreateDefault(
            EntityId<TranscriptRevisionIdTag>? revisionId = null,
            string firstText = "Leading private conversation") =>
            Create(
                [
                    new UtteranceInput(1, 1, "Speaker 1", firstText, 0, 1),
                    new UtteranceInput(2, 1, "Speaker 1", "Topic one A", 2, 1),
                    new UtteranceInput(3, 1, "Speaker 1", "Topic one B", 5, 1),
                    new UtteranceInput(4, 2, "Speaker 2", "Internal gap", 6, 1),
                    new UtteranceInput(5, 2, "Speaker 2", "Partial context", 7, 2),
                    new UtteranceInput(6, 2, "Speaker 2", "Topic two", 9, 1),
                    new UtteranceInput(7, 1, "Speaker 1", "Trailing private conversation", 12, 1),
                ],
                revisionId);

        internal static Fixture Create(
            IReadOnlyList<UtteranceInput> inputs,
            EntityId<TranscriptRevisionIdTag>? revisionId = null)
        {
            EntityId<TranscriptRevisionIdTag> actualRevisionId = revisionId
                ?? Id<TranscriptRevisionIdTag>(
                    "73000000-0000-0000-0000-000000000001");
            var utterances = new List<Utterance>(inputs.Count);
            var sourceSpans = new List<SourceSpan>(inputs.Count);
            var digest = new ContentDigest(new string('a', 64));
            foreach (UtteranceInput input in inputs)
            {
                TimeRange range = Range(input.Start, input.Duration);
                EntityId<TranscriptTokenIdTag> tokenId = Id<TranscriptTokenIdTag>(
                    $"74000000-0000-0000-0000-{input.Value:000000000000}");
                var token = new Token(
                    tokenId,
                    input.Text,
                    range: null,
                    recognitionConfidence: 0.75);
                var sentence = new Sentence(
                    Id<TranscriptSentenceIdTag>(
                        $"75000000-0000-0000-0000-{input.Value:000000000000}"),
                    input.Text,
                    [token]);
                EntityId<SourceSpanIdTag> spanId = Id<SourceSpanIdTag>(
                    $"76000000-0000-0000-0000-{input.Value:000000000000}");
                utterances.Add(new Utterance(
                    Id<TranscriptUtteranceIdTag>(
                        $"77000000-0000-0000-0000-{input.Value:000000000000}"),
                    spanId,
                    Id<TranscriptSpeakerIdTag>(
                        $"78000000-0000-0000-0000-{input.Speaker:000000000000}"),
                    input.Label,
                    input.Text,
                    range,
                    recognitionConfidence: 0.75,
                    [sentence]));
                sourceSpans.Add(new SourceSpan(
                    spanId,
                    Id<AssetIdTag>("79000000-0000-0000-0000-000000000001"),
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
                Id<ModelRunIdTag>("7a000000-0000-0000-0000-000000000001"));
        }
    }
}