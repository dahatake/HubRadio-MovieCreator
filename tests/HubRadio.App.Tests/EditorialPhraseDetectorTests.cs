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
public sealed class EditorialPhraseDetectorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2037, 9, 10, 11, 12, 13, TimeSpan.Zero);

    [TestMethod]
    public void Dictionary_IsClosedVersionedAndContainsOnlyRequirementPhrase()
    {
        FieldInfo versionField = typeof(EditorialPhraseDetector).GetField(
            nameof(EditorialPhraseDetector.DictionaryVersion),
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Dictionary version field is missing.");
        Assert.AreEqual("fr005-2026-08-31.v1", versionField.GetRawConstantValue());
        CollectionAssert.AreEqual(
            new[] { "ここはカット" },
            EditorialPhraseDetector.Phrases.ToArray());
        IList<string> exposed = (IList<string>)EditorialPhraseDetector.Phrases;
        Assert.ThrowsExactly<NotSupportedException>(() => exposed.Add("削除して"));
    }

    [TestMethod]
    public void FindCandidates_MatchesExactPhraseAcrossTimedEvidenceTokens()
    {
        Fixture fixture = Fixture.Create(
            new SentenceInput(
                new TokenInput("ここ", Range(0, 1)),
                new TokenInput("は", Range(1, 1)),
                new TokenInput("カット", Range(2, 1)),
                new TokenInput("です", Range(3, 1))));

        EditorialPhraseScanResult result = fixture.Scan();

        Assert.AreEqual(fixture.Revision.Id, result.SourceRevisionId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(fixture.Revision))),
            result.SourceRevisionDigest);
        Assert.AreEqual(fixture.ModelRunId, result.ModelRunId);
        Assert.AreEqual(EditorialPhraseDetector.DictionaryVersion, result.DictionaryVersion);
        Assert.AreEqual(4, result.InspectedTokenCount);
        Assert.HasCount(1, result.Candidates);
        EditorialPhraseCandidate candidate = result.Candidates[0];
        Assert.AreEqual(DetectionKind.EditorialInstruction, candidate.Detection.Kind);
        Assert.AreEqual(SuggestionKind.DeleteRange, candidate.Suggestion.Kind);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
        Assert.AreEqual(fixture.ModelRunId, candidate.Detection.ModelRunId);
        Assert.AreEqual(fixture.ModelRunId, candidate.Suggestion.ModelRunId);
        Assert.AreEqual(
            "A versioned explicit editorial instruction matched the transcript token sequence.",
            candidate.Detection.Description);
        Assert.AreEqual(
            "The matched instruction explicitly requests an editorial cut.",
            candidate.Suggestion.Reason);
        Assert.AreEqual(candidate.SourceSpan.Id, candidate.Detection.Origin.SourceSpanId);
        Assert.AreEqual(candidate.SourceSpan.Id, candidate.Suggestion.Origin.SourceSpanId);
        Assert.AreEqual("ここはカット", candidate.MatchedPhrase);
        Assert.AreEqual(0, candidate.NormalizedSentenceStart);
        Assert.AreEqual(6, candidate.NormalizedMatchLength);
        Assert.IsFalse(candidate.UsesUtteranceTimingFallback);
        Assert.IsFalse(candidate.UsesPartialTokenCoverage);
        Assert.AreEqual(Range(0, 3), candidate.SourceSpan.Range);
        Assert.AreEqual(fixture.Revision.Id, candidate.SourceSpan.TranscriptRevisionId);
        CollectionAssert.AreEqual(
            fixture.Tokens.Take(3).Select(static token => token.Id).ToArray(),
            candidate.SourceSpan.TranscriptTokenIds.ToArray());
        Assert.AreEqual(fixture.SourceSpan.AssetId, candidate.SourceSpan.AssetId);
        Assert.AreEqual(fixture.SourceSpan.SourceDigest, candidate.SourceSpan.SourceDigest);
        Assert.AreEqual(fixture.SourceSpan.StreamId, candidate.SourceSpan.StreamId);
        Assert.IsNull(candidate.Suggestion.Confidence);
        Assert.IsNull(candidate.Detection.Confidence);
        Assert.IsNull(candidate.Suggestion.MissingContext);
        Assert.AreEqual(
            "Review the surrounding speech before adopting this deletion candidate.",
            candidate.Suggestion.Warning);
        Assert.AreEqual("ここ は カット です", fixture.Revision.Utterances[0].Text);
    }

    [TestMethod]
    public void FindCandidates_MatchesNfkcCompatiblePhraseWithoutFuzzyExpansion()
    {
        Fixture compatible = Fixture.Create(
            new SentenceInput(new TokenInput("ここはｶｯﾄ", Range(0, 1))));
        Fixture fuzzy = Fixture.Create(
            new SentenceInput(new TokenInput("ここはカト", Range(0, 1))));

        EditorialPhraseCandidate candidate = compatible.Scan().Candidates.Single();

        Assert.AreEqual("ここはカット", candidate.MatchedPhrase);
        Assert.AreEqual(0, candidate.NormalizedSentenceStart);
        Assert.AreEqual(6, candidate.NormalizedMatchLength);
        Assert.IsFalse(candidate.UsesPartialTokenCoverage);
        Assert.HasCount(1, candidate.SourceSpan.TranscriptTokenIds);
        Assert.HasCount(0, fuzzy.Scan().Candidates);
    }

    [TestMethod]
    public void FindCandidates_ReportsEveryNonOverlappingSubstringOccurrenceAndBroaderTokenRange()
    {
        Fixture fixture = Fixture.Create(
            new SentenceInput(
                new TokenInput("前ここはカット後ここはカット", Range(0, 4))));

        EditorialPhraseScanResult result = fixture.Scan();

        Assert.HasCount(2, result.Candidates);
        CollectionAssert.AreEqual(
            new[] { 1, 8 },
            result.Candidates
                .Select(static candidate => candidate.NormalizedSentenceStart)
                .ToArray());
        Assert.IsTrue(result.Candidates.All(
            static candidate => candidate.UsesPartialTokenCoverage));
        Assert.IsTrue(result.Candidates.All(candidate =>
            candidate.SourceSpan.Range == Range(0, 4)));
        Assert.IsTrue(result.Candidates.All(candidate =>
            candidate.SourceSpan.TranscriptTokenIds.SequenceEqual(
                [fixture.Tokens[0].Id])));
        Assert.IsTrue(result.Candidates.All(candidate =>
            candidate.Suggestion.MissingContext ==
                "The phrase covers only part of an evidence token; the candidate range includes the whole token."));
        Assert.AreNotEqual(
            result.Candidates[0].SourceSpan.Id,
            result.Candidates[1].SourceSpan.Id);
    }

    [TestMethod]
    public void FindCandidates_DoesNotCrossSentenceOrPunctuationBoundaries()
    {
        Fixture crossSentence = Fixture.Create(
            new SentenceInput(
                new TokenInput("ここ", Range(0, 1)),
                new TokenInput("は", Range(1, 1))),
            new SentenceInput(new TokenInput("カット", Range(2, 1))));
        Fixture punctuation = Fixture.Create(
            new SentenceInput(
                new TokenInput("ここ", Range(0, 1)),
                new TokenInput("は", Range(1, 1)),
                new TokenInput("、", Range(2, 1)),
                new TokenInput("カット", Range(3, 1))));

        Assert.HasCount(0, crossSentence.Scan().Candidates);
        Assert.HasCount(0, punctuation.Scan().Candidates);
    }

    [TestMethod]
    public void FindCandidates_UntimedEvidenceFallsBackToUtteranceRangeWithoutInventingTiming()
    {
        Fixture fixture = Fixture.Create(
            new SentenceInput(
                new TokenInput("ここ", Range(1, 1)),
                new TokenInput("は", null),
                new TokenInput("カット", Range(3, 1))),
            utteranceRange: Range(0, 8));

        EditorialPhraseCandidate candidate = fixture.Scan().Candidates.Single();

        Assert.IsTrue(candidate.UsesUtteranceTimingFallback);
        Assert.IsFalse(candidate.UsesPartialTokenCoverage);
        Assert.AreEqual(Range(0, 8), candidate.SourceSpan.Range);
        CollectionAssert.AreEqual(
            fixture.Tokens.Select(static token => token.Id).ToArray(),
            candidate.SourceSpan.TranscriptTokenIds.ToArray());
        Assert.AreEqual(
            "One or more evidence tokens have no accepted timing; the candidate uses the utterance range.",
            candidate.Suggestion.MissingContext);
    }

    [TestMethod]
    public void FindCandidates_EmptyRevisionAndNoMatchReturnNoCandidates()
    {
        var emptyRevision = new TranscriptRevision(
            EntityId<TranscriptRevisionIdTag>.New(),
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            CreatedAt,
            []);

        EditorialPhraseScanResult empty = EditorialPhraseDetector.FindCandidates(
            emptyRevision,
            [],
            EntityId<ModelRunIdTag>.New());
        Fixture noMatch = Fixture.Create(
            new SentenceInput(
                new TokenInput("通常", Range(0, 1)),
                new TokenInput("会話", Range(1, 1))));

        Assert.AreEqual(0, empty.InspectedTokenCount);
        Assert.HasCount(0, empty.Candidates);
        Assert.AreEqual(2, noMatch.Scan().InspectedTokenCount);
        Assert.HasCount(0, noMatch.Scan().Candidates);
    }

    [TestMethod]
    public void FindCandidates_RejectsSourceSpanMismatchMalformedUnicodeAndCancellationPrivately()
    {
        Fixture fixture = Fixture.Create(
            new SentenceInput(new TokenInput("ここはカット", Range(0, 1))));
        SourceSpan source = fixture.SourceSpan;
        var wrongRevision = new SourceSpan(
            source.Id,
            source.AssetId,
            source.StreamId,
            source.Range,
            source.SourceDigest,
            EntityId<TranscriptRevisionIdTag>.New(),
            source.TranscriptTokenIds);
        AssertFailure(
            () => EditorialPhraseDetector.FindCandidates(
                fixture.Revision,
                [wrongRevision],
                fixture.ModelRunId),
            EditorialPhraseDetectorFailureReason.SourceSpanBindingMismatch);

        Fixture invalidText = Fixture.Create(
            new SentenceInput(new TokenInput("private\uD800value", Range(0, 1))));
        EditorialPhraseDetectorException invalidException = AssertFailure(
            () => invalidText.Scan(),
            EditorialPhraseDetectorFailureReason.InvalidText);
        Assert.IsFalse(invalidException.ToString().Contains(
            "private",
            StringComparison.Ordinal));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            EditorialPhraseDetector.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.ModelRunId,
                cancellation.Token));
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndHasNoDeleteOrAdoptOperation()
    {
        foreach (Type type in new[]
        {
            typeof(EditorialPhraseCandidate),
            typeof(EditorialPhraseScanResult),
            typeof(EditorialPhraseDetectorException),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(EditorialPhraseCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(EditorialPhraseScanResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(EditorialPhraseDetectorException).GetConstructors().Length);
        Assert.IsTrue(typeof(EditorialPhraseDetector).IsAbstract);
        Assert.IsTrue(typeof(EditorialPhraseDetector).IsSealed);
        CollectionAssert.AreEqual(
            new[] { "FindCandidates" },
            typeof(EditorialPhraseDetector)
                .GetMethods(
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(EditorialPhraseDetector).GetMethod("Delete"));
        Assert.IsNull(typeof(EditorialPhraseDetector).GetMethod("Adopt"));
    }

    private static EditorialPhraseDetectorException AssertFailure(
        Action action,
        EditorialPhraseDetectorFailureReason reason)
    {
        EditorialPhraseDetectorException exception =
            Assert.ThrowsExactly<EditorialPhraseDetectorException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\private\customer.mov",
            StringComparison.OrdinalIgnoreCase));
        return exception;
    }

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private sealed record TokenInput(string Text, TimeRange? Range);

    private sealed record SentenceInput
    {
        internal SentenceInput(params TokenInput[] tokens)
        {
            Tokens = tokens;
        }

        internal IReadOnlyList<TokenInput> Tokens { get; }
    }

    private sealed class Fixture
    {
        private Fixture(
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans,
            EntityId<ModelRunIdTag> modelRunId)
        {
            Revision = revision;
            SourceSpans = sourceSpans;
            SourceSpan = sourceSpans.Single();
            ModelRunId = modelRunId;
            Tokens = revision.Utterances.Single().Tokens;
        }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal SourceSpan SourceSpan { get; }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal IReadOnlyList<Token> Tokens { get; }

        internal EditorialPhraseScanResult Scan() =>
            EditorialPhraseDetector.FindCandidates(
                Revision,
                SourceSpans,
                ModelRunId);

        internal static Fixture Create(
            SentenceInput first,
            SentenceInput? second = null,
            TimeRange? utteranceRange = null)
        {
            SentenceInput[] inputs = second is null ? [first] : [first, second];
            var sentences = new List<Sentence>(inputs.Length);
            var allTokens = new List<Token>();
            foreach (SentenceInput input in inputs)
            {
                Token[] tokens = input.Tokens
                    .Select(token => new Token(
                        EntityId<TranscriptTokenIdTag>.New(),
                        token.Text,
                        token.Range,
                        recognitionConfidence: 0.75))
                    .ToArray();
                allTokens.AddRange(tokens);
                sentences.Add(new Sentence(
                    EntityId<TranscriptSentenceIdTag>.New(),
                    string.Join(" ", tokens.Select(static token => token.Text)),
                    tokens));
            }

            TimeRange range = utteranceRange ?? Range(0, 20);
            var spanId = EntityId<SourceSpanIdTag>.New();
            var utterance = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                spanId,
                EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker 1",
                string.Join("。", sentences.Select(static sentence => sentence.Text)),
                range,
                recognitionConfidence: 0.75,
                sentences);
            var revision = new TranscriptRevision(
                EntityId<TranscriptRevisionIdTag>.New(),
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [utterance]);
            var sourceSpan = new SourceSpan(
                spanId,
                EntityId<AssetIdTag>.New(),
                "audio:1",
                range,
                new ContentDigest(new string('a', 64)),
                revision.Id,
                allTokens.Select(static token => token.Id).ToArray());
            return new Fixture(
                revision,
                [sourceSpan],
                EntityId<ModelRunIdTag>.New());
        }
    }
}