using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class SubtitleSegmenterTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2043, 8, 31, 1, 2, 3, TimeSpan.Zero);

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [TestMethod]
    public void JapaneseSentenceEndingPunctuationSplitsInTokenOrderWithSourceEvidence()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    Speaker: 1,
                    Range: Range(0, 2),
                    Tokens:
                    [
                        new TokenInput("「最初です。」", Range(0, 1)),
                        new TokenInput("次です", Range(1, 1)),
                    ]),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.AreEqual(fixture.Revision.Id, result.SourceRevisionId);
        Assert.AreEqual(fixture.RevisionDigest, result.SourceRevisionDigest);
        Assert.AreEqual(SubtitleSegmenter.RuleVersion, result.RuleVersion);
        Assert.AreEqual(2, result.InspectedTokenCount);
        Assert.HasCount(2, result.Candidates);
        SubtitleCueCandidate first = result.Candidates[0];
        Assert.AreEqual("「最初です。」", first.Text);
        Assert.AreEqual(Range(0, 1), first.SourceRange);
        CollectionAssert.AreEqual(
            new[] { SubtitleBoundaryReason.JapaneseSentenceEndingPunctuation },
            first.BoundaryReasons.ToArray());
        Assert.IsNull(first.ObservedPositiveGapAfter);
        Assert.AreEqual(fixture.Revision.Utterances[0].SpeakerId, first.SpeakerId);
        Assert.AreEqual(fixture.Revision.Utterances[0].Tokens[0].Id, first.EvidenceTokenIds.Single());
        Assert.AreEqual(first.SourceSpan.Id, first.Origin.SourceSpanId);
        Assert.AreEqual(OriginKind.SourceSpan, first.Origin.Kind);
        Assert.AreEqual(fixture.SourceSpans[0].AssetId, first.SourceSpan.AssetId);
        Assert.AreEqual(fixture.SourceSpans[0].StreamId, first.SourceSpan.StreamId);
        Assert.AreEqual(fixture.SourceSpans[0].SourceDigest, first.SourceSpan.SourceDigest);
        Assert.AreEqual(fixture.Revision.Id, first.SourceSpan.TranscriptRevisionId);
        CollectionAssert.AreEqual(
            first.EvidenceTokenIds.ToArray(),
            first.SourceSpan.TranscriptTokenIds.ToArray());
        StringAssert.Contains(first.Reason, "Japanese sentence-ending punctuation");
        CollectionAssert.AreEqual(
            new[] { SubtitleBoundaryReason.EndOfTranscript },
            result.Candidates[1].BoundaryReasons.ToArray());
    }

    [TestMethod]
    public void CorrectedRevisionTextPreservesSpacingAndTokenExternalPunctuation()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    Speaker: 1,
                    Range: Range(0, 2),
                    Tokens:
                    [
                        new TokenInput("こんにちは", Range(0, 1)),
                        new TokenInput("世界", Range(1, 1)),
                    ],
                    TranscriptText: "こんにちは 世界。"),
            ]);

        SubtitleCueCandidate candidate = fixture.Segment().Candidates.Single();

        Assert.AreEqual("こんにちは 世界。", candidate.Text);
        CollectionAssert.AreEqual(
            new[]
            {
                SubtitleBoundaryReason.JapaneseSentenceEndingPunctuation,
                SubtitleBoundaryReason.EndOfTranscript,
            },
            candidate.BoundaryReasons.ToArray());
    }

    [TestMethod]
    public void SpeakerChangeCreatesBoundaryWithoutInventingAGap()
    {
        Fixture fixture = Fixture.Create(
            [
                Utterance(1, Range(0, 1), new TokenInput("前", Range(0, 1))),
                Utterance(2, Range(1, 1), new TokenInput("後", Range(1, 1))),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.HasCount(2, result.Candidates);
        CollectionAssert.AreEqual(
            new[] { SubtitleBoundaryReason.SpeakerChange },
            result.Candidates[0].BoundaryReasons.ToArray());
        Assert.IsNull(result.Candidates[0].ObservedPositiveGapAfter);
        Assert.AreNotEqual(
            result.Candidates[0].SpeakerId,
            result.Candidates[1].SpeakerId);
        StringAssert.Contains(result.Candidates[0].Reason, "speaker change");
    }

    [TestMethod]
    public void ExactPositiveTokenGapCreatesBoundaryWithoutASecondsThreshold()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    Speaker: 1,
                    Range: Range(0, 4),
                    Tokens:
                    [
                        new TokenInput("前", Range(0, 1)),
                        new TokenInput("後", Range(3, 1)),
                    ]),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.HasCount(2, result.Candidates);
        SubtitleCueCandidate first = result.Candidates[0];
        CollectionAssert.AreEqual(
            new[] { SubtitleBoundaryReason.PositiveSourceGap },
            first.BoundaryReasons.ToArray());
        Assert.AreEqual(Time(2), first.ObservedPositiveGapAfter);
        Assert.AreEqual(Range(0, 1), first.SourceRange);
        Assert.AreEqual(Range(3, 1), result.Candidates[1].SourceRange);
        StringAssert.Contains(first.Reason, "positive source-timing gap");
    }

    [TestMethod]
    public void PositiveGapBoundaryPreservesPriorTokenTextAfterExactlyOnce()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    Speaker: 1,
                    Range: Range(0, 4),
                    Tokens:
                    [
                        new TokenInput("前", Range(0, 1)),
                        new TokenInput("後", Range(3, 1)),
                    ],
                    TranscriptText: "前、後"),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.HasCount(2, result.Candidates);
        CollectionAssert.AreEqual(
            new[] { "前、", "後" },
            result.Candidates.Select(static candidate => candidate.Text).ToArray());
        Assert.AreEqual(
            "前、後",
            string.Concat(result.Candidates.Select(static candidate => candidate.Text)));
    }

    [TestMethod]
    public void NegativeSourceStartRemainsValidAndIsNotConvertedToOutputCue()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(-3, 2), new TokenInput("負の時刻", Range(-3, 2)))]);

        SubtitleCueCandidate candidate = fixture.Segment().Candidates.Single();

        Assert.AreEqual(Range(-3, 2), candidate.SourceRange);
        Assert.AreEqual(Time(-3), candidate.SourceSpan.Range.Start);
        Assert.IsTrue(candidate.SourceRange.Start < default(RationalTime));
        Assert.IsFalse(typeof(SubtitleCueCandidate).GetProperties().Any(
            static property => property.PropertyType == typeof(Cue)));
    }

    [TestMethod]
    public void UntimedTokenUsesExplicitParentRangeFallbackAndRequiresManualAdjustment()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(10, 4), new TokenInput("時刻なし", Range: null))]);

        SubtitleCueCandidate candidate = fixture.Segment().Candidates.Single();

        Assert.AreEqual(Range(10, 4), candidate.SourceRange);
        Assert.IsTrue(candidate.ManualTimingAdjustmentRequired);
        SubtitleTimingFallback fallback = candidate.TimingFallbacks.Single();
        Assert.AreEqual(candidate.EvidenceTokenIds.Single(), fallback.TokenId);
        Assert.AreEqual(fixture.Revision.Utterances.Single().Id, fallback.ParentUtteranceId);
        Assert.AreEqual(fixture.Revision.Utterances.Single().Range, fallback.ParentUtteranceRange);
        Assert.IsNull(fixture.Revision.Utterances.Single().Tokens.Single().Range);
    }

    [TestMethod]
    public void UntimedFallbackTokensDoNotCreateObservedTimingOverlapPairs()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    Speaker: 1,
                    Range: Range(10, 4),
                    Tokens:
                    [
                        new TokenInput("時", Range: null),
                        new TokenInput("刻", Range: null),
                        new TokenInput("なし", Range: null),
                    ]),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        SubtitleCueCandidate candidate = result.Candidates.Single();
        Assert.HasCount(3, candidate.TimingFallbacks);
        CollectionAssert.AreEqual(
            candidate.EvidenceTokenIds.ToArray(),
            candidate.TimingFallbacks.Select(static fallback => fallback.TokenId).ToArray());
        Assert.HasCount(0, result.TimingOverlaps);
        Assert.IsFalse(result.HasTimingOverlaps);
    }

    [TestMethod]
    public void OverlapIsReportedExactlyWithoutClampingEitherCandidate()
    {
        Fixture fixture = Fixture.Create(
            [
                Utterance(1, Range(0, 3), new TokenInput("前。", Range(0, 3))),
                Utterance(1, Range(2, 2), new TokenInput("後", Range(2, 2))),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.HasCount(2, result.Candidates);
        Assert.AreEqual(Range(0, 3), result.Candidates[0].SourceRange);
        Assert.AreEqual(Range(2, 2), result.Candidates[1].SourceRange);
        Assert.IsTrue(result.HasTimingOverlaps);
        SubtitleTimingOverlap overlap = result.TimingOverlaps.Single();
        Assert.AreEqual(result.Candidates[0].EvidenceTokenIds.Single(), overlap.PreviousEvidenceTokenId);
        Assert.AreEqual(result.Candidates[1].EvidenceTokenIds.Single(), overlap.CurrentEvidenceTokenId);
        Assert.AreEqual(Range(2, 1), overlap.SourceRange);
        Assert.IsFalse(overlap.UsesParentUtteranceFallback);
        Assert.IsFalse(result.Candidates[0].BoundaryReasons.Contains(
            SubtitleBoundaryReason.PositiveSourceGap));
    }

    [TestMethod]
    public void NonAdjacentOverlapsAreAllReportedWithoutClamping()
    {
        Fixture fixture = Fixture.Create(
            [
                Utterance(1, Range(0, 10), new TokenInput("長い。", Range(0, 10))),
                Utterance(1, Range(1, 1), new TokenInput("短い。", Range(1, 1))),
                Utterance(1, Range(3, 1), new TokenInput("後", Range(3, 1))),
            ]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.HasCount(3, result.Candidates);
        Assert.HasCount(2, result.TimingOverlaps);
        EntityId<TranscriptTokenIdTag> longToken =
            result.Candidates[0].EvidenceTokenIds.Single();
        CollectionAssert.AreEqual(
            new[]
            {
                (longToken, result.Candidates[1].EvidenceTokenIds.Single(), Range(1, 1)),
                (longToken, result.Candidates[2].EvidenceTokenIds.Single(), Range(3, 1)),
            },
            result.TimingOverlaps
                .Select(static overlap => (
                    overlap.PreviousEvidenceTokenId,
                    overlap.CurrentEvidenceTokenId,
                    overlap.SourceRange))
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { Range(0, 10), Range(1, 1), Range(3, 1) },
            result.Candidates.Select(static candidate => candidate.SourceRange).ToArray());
    }

    [TestMethod]
    public void RevisionDigestAndSourceSpanMismatchesFailClosedWithoutContentDisclosure()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(0, 1), new TokenInput("private-source-text", Range(0, 1)))]);

        SubtitleSegmentationException digestFailure = AssertFailure(
            () => SubtitleSegmenter.Segment(
                fixture.Revision,
                new ContentDigest(new string('f', 64)),
                fixture.SourceSpans),
            SubtitleSegmentationFailureReason.RevisionDigestMismatch);

        SourceSpan original = fixture.SourceSpans.Single();
        var wrongRevisionSpan = new SourceSpan(
            original.Id,
            original.AssetId,
            original.StreamId,
            original.Range,
            original.SourceDigest,
            Id<TranscriptRevisionIdTag>(0x71000001, 99),
            original.TranscriptTokenIds);
        SubtitleSegmentationException spanFailure = AssertFailure(
            () => SubtitleSegmenter.Segment(
                fixture.Revision,
                fixture.RevisionDigest,
                [wrongRevisionSpan]),
            SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);

        Assert.IsFalse(digestFailure.ToString().Contains("private-source-text", StringComparison.Ordinal));
        Assert.IsFalse(spanFailure.ToString().Contains("private-source-text", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CandidateCannotMergeDifferentSourceDigestsWithoutInventingABoundary()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    1,
                    Range(0, 1),
                    [new TokenInput("前", Range(0, 1))],
                    SourceDigestFill: 'a'),
                new UtteranceInput(
                    1,
                    Range(1, 1),
                    [new TokenInput("後", Range(1, 1))],
                    SourceDigestFill: 'b'),
            ]);

        AssertFailure(
            () => fixture.Segment(),
            SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void DuplicateSourceSpanIdentifiersFailClosed()
    {
        Fixture fixture = Fixture.Create(
            [
                Utterance(1, Range(0, 1), new TokenInput("前", Range(0, 1))),
                Utterance(1, Range(1, 1), new TokenInput("後", Range(1, 1))),
            ]);
        Utterance first = fixture.Revision.Utterances[0];
        Utterance second = fixture.Revision.Utterances[1];
        var duplicateIdSecond = new Utterance(
            second.Id,
            first.SourceSpanId,
            second.SpeakerId,
            second.SpeakerLabel,
            second.Text,
            second.Range,
            second.RecognitionConfidence,
            second.Sentences);
        var revision = new TranscriptRevision(
            fixture.Revision.Id,
            fixture.Revision.ParentRevisionId,
            TranscriptChangeSource.Human,
            fixture.Revision.CreatedAt,
            [first, duplicateIdSecond]);
        SourceSpan secondSource = fixture.SourceSpans[1];
        var duplicateIdSpan = new SourceSpan(
            first.SourceSpanId,
            secondSource.AssetId,
            secondSource.StreamId,
            secondSource.Range,
            secondSource.SourceDigest,
            revision.Id,
            secondSource.TranscriptTokenIds);

        AssertFailure(
            () => SubtitleSegmenter.Segment(
                revision,
                ComputeRevisionDigest(revision),
                [fixture.SourceSpans[0], duplicateIdSpan]),
            SubtitleSegmentationFailureReason.DuplicateIdentifier);
    }

    [TestMethod]
    public void MalformedUnicodeAndTimeOverflowUseStableContentPrivateFailures()
    {
        Fixture malformed = Fixture.Create(
            [Utterance(1, Range(0, 1), new TokenInput("private\uD800value", Range(0, 1)))]);
        SubtitleSegmentationException malformedFailure = AssertFailure(
            () => SubtitleSegmenter.Segment(
                malformed.Revision,
                new ContentDigest(new string('a', 64)),
                malformed.SourceSpans),
            SubtitleSegmentationFailureReason.InvalidText);
        Assert.IsFalse(malformedFailure.ToString().Contains("private", StringComparison.Ordinal));
        Assert.IsNull(malformedFailure.InnerException);

        Fixture overflow = Fixture.Create(
            [
                Utterance(
                    1,
                    Range(long.MinValue, 1),
                    new TokenInput("前", Range(long.MinValue, 1))),
                Utterance(
                    1,
                    Range(long.MaxValue - 1, 1),
                    new TokenInput("後", Range(long.MaxValue - 1, 1))),
            ]);
        SubtitleSegmentationException overflowFailure = AssertFailure(
            () => overflow.Segment(),
            SubtitleSegmentationFailureReason.ArithmeticOverflow);
        Assert.IsNull(overflowFailure.InnerException);
    }

    [TestMethod]
    public void EquivalentRunsAreDeterministicApartFromGeneratedIdentities()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    1,
                    Range(0, 4),
                    [
                        new TokenInput("一。", Range(0, 1)),
                        new TokenInput("二", Range: null),
                    ]),
            ]);

        SubtitleSegmentationResult first = fixture.Segment();
        SubtitleSegmentationResult second = fixture.Segment();

        Assert.AreEqual(first.SourceRevisionId, second.SourceRevisionId);
        Assert.AreEqual(first.SourceRevisionDigest, second.SourceRevisionDigest);
        Assert.AreEqual(first.RuleVersion, second.RuleVersion);
        Assert.AreEqual(first.InspectedTokenCount, second.InspectedTokenCount);
        Assert.AreEqual(first.Candidates.Count, second.Candidates.Count);
        for (int index = 0; index < first.Candidates.Count; index++)
        {
            AssertCandidateSpecificationEqual(
                first.Candidates[index],
                second.Candidates[index]);
        }

        Assert.AreEqual(first.TimingOverlaps.Count, second.TimingOverlaps.Count);
        for (int index = 0; index < first.TimingOverlaps.Count; index++)
        {
            SubtitleTimingOverlap left = first.TimingOverlaps[index];
            SubtitleTimingOverlap right = second.TimingOverlaps[index];
            Assert.AreEqual(left.PreviousEvidenceTokenId, right.PreviousEvidenceTokenId);
            Assert.AreEqual(left.CurrentEvidenceTokenId, right.CurrentEvidenceTokenId);
            Assert.AreEqual(left.SourceRange, right.SourceRange);
            Assert.AreEqual(left.UsesParentUtteranceFallback, right.UsesParentUtteranceFallback);
        }
    }

    [TestMethod]
    public void SegmentationDoesNotMutateOriginalRevisionOrSourceEvidence()
    {
        Fixture fixture = Fixture.Create(
            [
                new UtteranceInput(
                    1,
                    Range(0, 2),
                    [
                        new TokenInput("原文。", Range(0, 1)),
                        new TokenInput("保持", Range(1, 1)),
                    ]),
            ]);
        TranscriptRevision revision = fixture.Revision;
        Utterance utterance = revision.Utterances.Single();
        Sentence sentence = utterance.Sentences.Single();
        Token[] tokens = sentence.Tokens.ToArray();
        SourceSpan sourceSpan = fixture.SourceSpans.Single();
        string utteranceText = utterance.Text;
        string sentenceText = sentence.Text;
        TimeRange utteranceRange = utterance.Range;

        _ = fixture.Segment();

        Assert.AreSame(revision, fixture.Revision);
        Assert.AreSame(utterance, revision.Utterances.Single());
        Assert.AreSame(sentence, utterance.Sentences.Single());
        CollectionAssert.AreEqual(tokens, sentence.Tokens.ToArray());
        Assert.AreEqual(utteranceText, utterance.Text);
        Assert.AreEqual(sentenceText, sentence.Text);
        Assert.AreEqual(utteranceRange, utterance.Range);
        Assert.AreSame(sourceSpan, fixture.SourceSpans.Single());
        Assert.AreEqual(revision.Id, sourceSpan.TranscriptRevisionId);
    }

    [TestMethod]
    public void EmptyHumanChildRevisionProducesAnEmptyResult()
    {
        Fixture fixture = Fixture.Create([]);

        SubtitleSegmentationResult result = fixture.Segment();

        Assert.AreEqual(0, result.InspectedTokenCount);
        Assert.HasCount(0, result.Candidates);
        Assert.HasCount(0, result.TimingOverlaps);
        Assert.IsFalse(result.HasTimingOverlaps);
    }

    [TestMethod]
    public void MachineRootUsesDistinctRuleAndPreservesRealTextTimingAndProvenance()
    {
        UtteranceInput[] inputs =
        [
            new(1, Range(0, 4),
                [new TokenInput("前", Range(0, 1)), new TokenInput("後", Range(3, 1))],
                TranscriptText: "前、後。"),
        ];
        Fixture machine = Fixture.Create(inputs, TranscriptChangeSource.Machine);
        Fixture human = Fixture.Create(inputs);

        SubtitleSegmentationResult result = machine.SegmentMachine();
        SubtitleSegmentationResult compatible = human.Segment();

        Assert.IsNull(machine.Revision.ParentRevisionId);
        Assert.AreEqual(TranscriptChangeSource.Machine, machine.Revision.ChangeSource);
        Assert.AreEqual(SubtitleSegmenter.MachineRuleVersion, result.RuleVersion);
        Assert.AreNotEqual(SubtitleSegmenter.RuleVersion, result.RuleVersion);
        Assert.AreEqual(machine.RevisionDigest, result.SourceRevisionDigest);
        CollectionAssert.AreEqual(compatible.Candidates.Select(static cue => cue.Text).ToArray(),
            result.Candidates.Select(static cue => cue.Text).ToArray());
        CollectionAssert.AreEqual(compatible.Candidates.Select(static cue => cue.SourceRange).ToArray(),
            result.Candidates.Select(static cue => cue.SourceRange).ToArray());
        CollectionAssert.AreEqual(machine.Revision.Utterances[0].Tokens.Select(static token => token.Id).ToArray(),
            result.Candidates.SelectMany(static cue => cue.EvidenceTokenIds).ToArray());
        Assert.IsTrue(result.Candidates.All(cue => cue.SourceRevisionId == machine.Revision.Id
            && cue.SourceRevisionDigest == machine.RevisionDigest
            && cue.Origin == OriginRef.FromSourceSpan(cue.SourceSpan.Id)
            && cue.SourceSpan.AssetId == machine.SourceSpans[0].AssetId
            && cue.SourceSpan.SourceDigest == machine.SourceSpans[0].SourceDigest));
        AssertFailure(() => machine.Segment(), SubtitleSegmentationFailureReason.SourceRevisionNotHumanChild);
        AssertFailure(() => human.SegmentMachine(), SubtitleSegmentationFailureReason.SourceRevisionNotMachine);
    }

    [TestMethod]
    public void EmptyMachineRootDoesNotInventCuesOrHumanRevision()
    {
        Fixture fixture = Fixture.Create([], TranscriptChangeSource.Machine);

        SubtitleSegmentationResult result = fixture.SegmentMachine();

        Assert.HasCount(0, result.Candidates);
        Assert.HasCount(0, result.TimingOverlaps);
        Assert.AreEqual(0, result.InspectedTokenCount);
        Assert.IsNull(fixture.Revision.ParentRevisionId);
    }

    [TestMethod]
    public void MachineDigestAndTokenEvidenceAreValidatedBeforeSegmentation()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(0, 2), new TokenInput("一", Range(0, 1)),
                new TokenInput("二", Range(1, 1)))], TranscriptChangeSource.Machine);
        SourceSpan original = fixture.SourceSpans.Single();
        var reversed = new SourceSpan(original.Id, original.AssetId, original.StreamId,
            original.Range, original.SourceDigest, fixture.Revision.Id,
            original.TranscriptTokenIds.Reverse().ToArray());

        AssertFailure(() => SubtitleSegmenter.SegmentMachine(fixture.Revision,
            new ContentDigest(new string('f', 64)), fixture.SourceSpans),
            SubtitleSegmentationFailureReason.RevisionDigestMismatch);
        AssertFailure(() => SubtitleSegmenter.SegmentMachine(fixture.Revision,
            fixture.RevisionDigest, [reversed]), SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
        AssertFailure(() => SubtitleSegmenter.SegmentMachine(fixture.Revision,
            fixture.RevisionDigest, []), SubtitleSegmentationFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void MachineUntimedTokensAndOverlapsRemainExplicitUnresolvedEvidence()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(0, 3), new TokenInput("先。", Range(0, 3))),
             Utterance(2, Range(2, 2), new TokenInput("後。", Range(2, 2))),
             Utterance(2, Range(5, 2), new TokenInput("不明", null))],
            TranscriptChangeSource.Machine);

        SubtitleSegmentationResult result = fixture.SegmentMachine();

        Assert.HasCount(1, result.TimingOverlaps);
        Assert.AreEqual(Range(2, 1), result.TimingOverlaps[0].SourceRange);
        Assert.IsTrue(result.Candidates[^1].ManualTimingAdjustmentRequired);
        Assert.IsNull(fixture.Revision.Utterances[^1].Tokens[0].Range);
    }

    [TestMethod]
    public void MachinePreCancellationDoesNotReturnPartialCandidates()
    {
        Fixture fixture = Fixture.Create([], TranscriptChangeSource.Machine);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => SubtitleSegmenter.SegmentMachine(
            fixture.Revision, fixture.RevisionDigest, fixture.SourceSpans, cancellation.Token));
    }

    [TestMethod]
    public void PreCancellationWinsWithoutReturningPartialCandidates()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(0, 1), new TokenInput("取消", Range(0, 1)))]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SubtitleSegmenter.Segment(
                fixture.Revision,
                fixture.RevisionDigest,
                fixture.SourceSpans,
                cancellation.Token));
    }

    [TestMethod]
    public void RootOrMachineRevisionIsRejectedBecauseR010ConsumesHumanChildren()
    {
        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(0, 1), new TokenInput("人手修正", Range(0, 1)))]);
        var root = new TranscriptRevision(
            fixture.Revision.Id,
            parentRevisionId: null,
            TranscriptChangeSource.Human,
            fixture.Revision.CreatedAt,
            fixture.Revision.Utterances);
        var machine = new TranscriptRevision(
            fixture.Revision.Id,
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            fixture.Revision.CreatedAt,
            fixture.Revision.Utterances);

        AssertFailure(
            () => SubtitleSegmenter.Segment(
                root,
                ComputeRevisionDigest(root),
                fixture.SourceSpans),
            SubtitleSegmentationFailureReason.SourceRevisionNotHumanChild);
        AssertFailure(
            () => SubtitleSegmenter.Segment(
                machine,
                ComputeRevisionDigest(machine),
                fixture.SourceSpans),
            SubtitleSegmentationFailureReason.SourceRevisionNotHumanChild);
    }

    [TestMethod]
    public void PublicSurfaceIsImmutableAndContainsNoInventedThresholdOrFrameApi()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "JapaneseSentenceEndingPunctuation",
                "SpeakerChange",
                "PositiveSourceGap",
                "EndOfTranscript",
                "ExactTokenBoundary",
            },
            Enum.GetNames<SubtitleBoundaryReason>());

        Type[] immutableTypes =
        [
            typeof(SubtitleTimingFallback),
            typeof(SubtitleTimingOverlap),
            typeof(SubtitleCueCandidate),
            typeof(SubtitleSegmentationResult),
            typeof(SubtitleSegmentationException),
        ];
        foreach (Type type in immutableTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.IsTrue(typeof(SubtitleCueCandidateIdTag).IsPublic);
        Assert.IsTrue(typeof(SubtitleCueCandidateIdTag).IsSealed);
        Assert.IsTrue(typeof(SubtitleSegmenter).IsAbstract);
        Assert.IsTrue(typeof(SubtitleSegmenter).IsSealed);
        MethodInfo[] methods = typeof(SubtitleSegmenter).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[] { nameof(SubtitleSegmenter.Segment), nameof(SubtitleSegmenter.SegmentMachine) },
            methods.Select(static method => method.Name).ToArray());
        foreach (MethodInfo method in methods)
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    typeof(TranscriptRevision),
                    typeof(ContentDigest),
                    typeof(IReadOnlyList<SourceSpan>),
                    typeof(CancellationToken),
                },
                method.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
        }

        MemberInfo[] publicMembers = typeof(SubtitleSegmenter).Assembly
            .GetExportedTypes()
            .Where(static type => type.Namespace == "HubRadio.Core.Subtitles")
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .ToArray();
        Assert.IsFalse(publicMembers.Any(static member =>
            member.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("FramesPerSecond", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("CharactersPerSecond", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(methods.SelectMany(static method => method.GetParameters()).Any(
            static parameter => parameter.ParameterType == typeof(double)
                || parameter.ParameterType == typeof(float)));

        Fixture fixture = Fixture.Create(
            [Utterance(1, Range(0, 1), new TokenInput("fallback", Range: null))]);
        SubtitleSegmentationResult result = fixture.Segment();
        SubtitleCueCandidate candidate = result.Candidates.Single();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleCueCandidate>)result.Candidates).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleTimingOverlap>)result.TimingOverlaps).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<EntityId<TranscriptTokenIdTag>>)candidate.EvidenceTokenIds).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleTimingFallback>)candidate.TimingFallbacks).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleBoundaryReason>)candidate.BoundaryReasons).Clear());
    }

    private static void AssertCandidateSpecificationEqual(
        SubtitleCueCandidate expected,
        SubtitleCueCandidate actual)
    {
        Assert.AreEqual(expected.SourceRevisionId, actual.SourceRevisionId);
        Assert.AreEqual(expected.SourceRevisionDigest, actual.SourceRevisionDigest);
        Assert.AreEqual(expected.SpeakerId, actual.SpeakerId);
        Assert.AreEqual(expected.Text, actual.Text);
        Assert.AreEqual(expected.SourceRange, actual.SourceRange);
        Assert.AreEqual(expected.SourceSpan.AssetId, actual.SourceSpan.AssetId);
        Assert.AreEqual(expected.SourceSpan.StreamId, actual.SourceSpan.StreamId);
        Assert.AreEqual(expected.SourceSpan.SourceDigest, actual.SourceSpan.SourceDigest);
        Assert.AreEqual(expected.SourceSpan.TranscriptRevisionId, actual.SourceSpan.TranscriptRevisionId);
        CollectionAssert.AreEqual(expected.EvidenceTokenIds.ToArray(), actual.EvidenceTokenIds.ToArray());
        CollectionAssert.AreEqual(expected.BoundaryReasons.ToArray(), actual.BoundaryReasons.ToArray());
        Assert.AreEqual(expected.ObservedPositiveGapAfter, actual.ObservedPositiveGapAfter);
        Assert.AreEqual(expected.Reason, actual.Reason);
        Assert.AreEqual(expected.ManualTimingAdjustmentRequired, actual.ManualTimingAdjustmentRequired);
        Assert.AreEqual(expected.TimingFallbacks.Count, actual.TimingFallbacks.Count);
        for (int index = 0; index < expected.TimingFallbacks.Count; index++)
        {
            Assert.AreEqual(expected.TimingFallbacks[index].TokenId, actual.TimingFallbacks[index].TokenId);
            Assert.AreEqual(
                expected.TimingFallbacks[index].ParentUtteranceId,
                actual.TimingFallbacks[index].ParentUtteranceId);
            Assert.AreEqual(
                expected.TimingFallbacks[index].ParentUtteranceRange,
                actual.TimingFallbacks[index].ParentUtteranceRange);
        }
    }

    private static SubtitleSegmentationException AssertFailure(
        Action action,
        SubtitleSegmentationFailureReason expectedReason)
    {
        SubtitleSegmentationException exception =
            Assert.ThrowsExactly<SubtitleSegmentationException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static ContentDigest ComputeRevisionDigest(TranscriptRevision revision)
    {
        var snapshot = new DigestRevision(
            revision.Id.ToString(),
            revision.ParentRevisionId?.ToString(),
            revision.ChangeSource switch
            {
                TranscriptChangeSource.Machine => "machine",
                TranscriptChangeSource.Human => "human",
                _ => throw new InvalidOperationException(),
            },
            revision.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            revision.Utterances.Select(static utterance => new DigestUtterance(
                utterance.Id.ToString(),
                utterance.SourceSpanId.ToString(),
                utterance.SpeakerId.ToString(),
                utterance.SpeakerLabel,
                utterance.Text,
                DigestRange.From(utterance.Range),
                utterance.RecognitionConfidence,
                utterance.Sentences.Select(static sentence => new DigestSentence(
                    sentence.Id.ToString(),
                    sentence.Text,
                    sentence.Tokens.Select(static token => new DigestToken(
                        token.Id.ToString(),
                        token.Text,
                        token.Range is TimeRange range
                            ? DigestRange.From(range)
                            : null,
                        token.RecognitionConfidence,
                        token.ManualTimingAdjustmentRequired)).ToArray())).ToArray())).ToArray());
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            SnapshotJsonOptions);
        return CanonicalJson.ComputeDigest(serialized);
    }

    private static UtteranceInput Utterance(
        int speaker,
        TimeRange range,
        params TokenInput[] tokens) =>
        new(speaker, range, tokens, SourceDigestFill: 'a');

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(Time(start, rate), Time(duration, rate));

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-0000-0000-{value:x12}");

    private sealed record TokenInput(string Text, TimeRange? Range);

    private sealed record UtteranceInput(
        int Speaker,
        TimeRange Range,
        IReadOnlyList<TokenInput> Tokens,
        char SourceDigestFill = 'a',
        string? TranscriptText = null);

    private sealed class Fixture
    {
        private Fixture(
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans)
        {
            Revision = revision;
            SourceSpans = sourceSpans;
        }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal ContentDigest RevisionDigest => ComputeRevisionDigest(Revision);

        internal SubtitleSegmentationResult Segment() =>
            SubtitleSegmenter.Segment(Revision, RevisionDigest, SourceSpans);

        internal SubtitleSegmentationResult SegmentMachine() =>
            SubtitleSegmenter.SegmentMachine(Revision, RevisionDigest, SourceSpans);

        internal static Fixture Create(IReadOnlyList<UtteranceInput> inputs,
            TranscriptChangeSource changeSource = TranscriptChangeSource.Human)
        {
            EntityId<TranscriptRevisionIdTag> revisionId =
                Id<TranscriptRevisionIdTag>(0x71000001, 1);
            var utterances = new List<Utterance>(inputs.Count);
            var sourceSpans = new List<SourceSpan>(inputs.Count);
            int tokenOrdinal = 0;
            for (int utteranceIndex = 0; utteranceIndex < inputs.Count; utteranceIndex++)
            {
                UtteranceInput input = inputs[utteranceIndex];
                var tokens = new List<Token>(input.Tokens.Count);
                foreach (TokenInput tokenInput in input.Tokens)
                {
                    tokenOrdinal++;
                    tokens.Add(new Token(
                        Id<TranscriptTokenIdTag>(0x71000002, tokenOrdinal),
                        tokenInput.Text,
                        tokenInput.Range,
                        recognitionConfidence: null));
                }

                string text = input.TranscriptText
                    ?? string.Concat(tokens.Select(static token => token.Text));
                var sentence = new Sentence(
                    Id<TranscriptSentenceIdTag>(0x71000003, utteranceIndex + 1),
                    text,
                    tokens);
                EntityId<SourceSpanIdTag> sourceSpanId =
                    Id<SourceSpanIdTag>(0x71000004, utteranceIndex + 1);
                var utterance = new Utterance(
                    Id<TranscriptUtteranceIdTag>(0x71000005, utteranceIndex + 1),
                    sourceSpanId,
                    Id<TranscriptSpeakerIdTag>(0x71000006, input.Speaker),
                    $"Speaker {input.Speaker}",
                    text,
                    input.Range,
                    recognitionConfidence: null,
                    [sentence]);
                utterances.Add(utterance);
                sourceSpans.Add(new SourceSpan(
                    sourceSpanId,
                    Id<AssetIdTag>(0x71000007, 1),
                    "audio:1",
                    input.Range,
                    new ContentDigest(new string(input.SourceDigestFill, 64)),
                    revisionId,
                    tokens.Select(static token => token.Id).ToArray()));
            }

            return new Fixture(
                new TranscriptRevision(
                    revisionId,
                    changeSource == TranscriptChangeSource.Human
                        ? Id<TranscriptRevisionIdTag>(0x71000001, 2) : null,
                    changeSource,
                    CreatedAt,
                    utterances),
                Array.AsReadOnly(sourceSpans.ToArray()));
        }
    }

    private sealed record DigestRevision(
        string Id,
        string? ParentRevisionId,
        string ChangeSource,
        string CreatedAt,
        DigestUtterance[] Utterances);

    private sealed record DigestUtterance(
        string Id,
        string SourceSpanId,
        string SpeakerId,
        string SpeakerLabel,
        string Text,
        DigestRange Range,
        double? RecognitionConfidence,
        DigestSentence[] Sentences);

    private sealed record DigestSentence(
        string Id,
        string Text,
        DigestToken[] Tokens);

    private sealed record DigestToken(
        string Id,
        string Text,
        DigestRange? Range,
        double? RecognitionConfidence,
        bool ManualTimingAdjustmentRequired);

    private sealed record DigestRange(DigestTime Start, DigestTime Duration)
    {
        internal static DigestRange From(TimeRange range) =>
            new(
                new DigestTime(range.Start.Value, range.Start.Rate),
                new DigestTime(range.Duration.Value, range.Duration.Rate));
    }

    private sealed record DigestTime(long Value, long Rate);
}