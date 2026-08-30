using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class EmphasisCandidateServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2042, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [TestMethod]
    public void FindCandidatesReturnsEveryClosedExplicitReasonWithoutConfidence()
    {
        Fixture fixture = Fixture.CreateDefault();

        EmphasisCandidateScanResult result = fixture.Scan();

        Assert.AreEqual(fixture.Revision.Id, result.SourceRevisionId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(fixture.Revision))),
            result.SourceRevisionDigest);
        Assert.AreEqual(fixture.MetadataRevision.Id, result.TopicMetadataRevisionId);
        Assert.AreEqual(fixture.ModelRunId, result.ModelRunId);
        Assert.AreEqual(fixture.TermScope, result.TermScope);
        Assert.AreEqual(EmphasisCandidateService.RuleVersion, result.RuleVersion);
        Assert.AreEqual(7, result.InspectedTokenCount);
        Assert.AreEqual(0, result.SkippedMetadataWithoutSupportingContextCount);
        Assert.HasCount(7, result.Candidates);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<EmphasisReasonKind>(),
            result.Candidates.SelectMany(static candidate => candidate.Reasons)
                .Distinct()
                .ToArray());
        Assert.IsTrue(result.Candidates.All(static candidate =>
            candidate.Suggestion.Kind == SuggestionKind.Highlight
            && candidate.Suggestion.Status == SuggestionStatus.Proposed
            && candidate.Suggestion.Confidence is null
            && candidate.AutomaticAdoptionAllowed is false));
    }

    [TestMethod]
    public void ImportantStatementAndQuestionAnswerMergeOnSameOriginalEvidence()
    {
        Fixture fixture = Fixture.CreateDefault();

        EmphasisCandidate answer = fixture.Scan().Candidates.Single(candidate =>
            candidate.Reasons.Contains(
                EmphasisReasonKind.AnswerAfterExplicitQuestion));

        CollectionAssert.AreEqual(
            new[]
            {
                EmphasisReasonKind.ImportantStatement,
                EmphasisReasonKind.AnswerAfterExplicitQuestion,
            },
            answer.Reasons.ToArray());
        Assert.AreEqual("価格は１２３円です", answer.Text);
        Assert.AreEqual(
            EmphasisTextSourceKind.OriginalTranscript,
            answer.TextSource);
        Assert.IsTrue(answer.IsExactOriginalText);
        Assert.AreEqual(fixture.SourceSpans[1].Id, answer.SourceSpan.Id);
        Assert.AreEqual(fixture.Revision.Id, answer.SourceSpan.TranscriptRevisionId);
        Assert.AreEqual(answer.SourceSpan.Id, answer.Suggestion.Origin.SourceSpanId);
        Assert.IsNotEmpty(answer.SourceSpan.TranscriptTokenIds);
    }

    [TestMethod]
    public void NumberAndProductRulesUseNfkcExactOriginalTokenEvidence()
    {
        Fixture fixture = Fixture.CreateDefault();

        EmphasisCandidate number = fixture.Scan().Candidates.Single(candidate =>
            candidate.Reasons.SequenceEqual([EmphasisReasonKind.Number]));
        EmphasisCandidate[] products = fixture.Scan().Candidates
            .Where(candidate => candidate.Reasons.Contains(
                EmphasisReasonKind.ProductName))
            .ToArray();

        Assert.AreEqual("１２３", number.Text);
        Assert.IsTrue(number.IsExactOriginalText);
        Assert.HasCount(1, number.SourceSpan.TranscriptTokenIds);
        Assert.HasCount(2, products);
        Assert.IsTrue(products.All(static candidate => candidate.IsExactOriginalText));
        Assert.IsTrue(products.All(candidate =>
            candidate.Text == "製品Ｘ"
            && candidate.TermEvidence.Single().TermEntryId == fixture.ProductTerm.Id
            && candidate.TermEvidence.Single().TermEntryVersion == fixture.ProductTerm.Version));
    }

    [TestMethod]
    public void MetadataTextIsOriginalOnlyForExactSentenceTokenMatch()
    {
        Fixture fixture = Fixture.CreateDefault();
        EmphasisCandidateScanResult result = fixture.Scan();

        EmphasisCandidate keyword = result.Candidates.Single(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.Keyword));
        EmphasisCandidate summary = result.Candidates.Single(candidate =>
            candidate.Reasons.Contains(
                EmphasisReasonKind.TopicTransitionSummary));
        EmphasisCandidate caution = result.Candidates.Single(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.PublicationCaution));

        Assert.AreEqual("価格", keyword.Text);
        Assert.AreEqual(EmphasisTextSourceKind.OriginalTranscript, keyword.TextSource);
        Assert.HasCount(1, keyword.SourceSpan.TranscriptTokenIds);
        Assert.AreEqual("価格のまとめ", summary.Text);
        Assert.AreEqual(EmphasisTextSourceKind.ManualSummary, summary.TextSource);
        Assert.IsFalse(summary.IsExactOriginalText);
        Assert.AreEqual("製品名を確認", caution.Text);
        Assert.AreEqual(EmphasisTextSourceKind.ManualSummary, caution.TextSource);
        Assert.AreEqual(summary.SourceSpan.Id, caution.SourceSpan.Id);
        Assert.IsTrue(summary.SourceSpan.TranscriptTokenIds.Count > 1);
    }

    [TestMethod]
    public void NfkcCompatibleKeywordUsesOriginalTokenSpelling()
    {
        Fixture fixture = Fixture.CreateDefault(keyword: "製品X");

        EmphasisCandidate[] keywords = fixture.Scan().Candidates
            .Where(candidate => candidate.Reasons.Contains(EmphasisReasonKind.Keyword))
            .ToArray();

        Assert.HasCount(2, keywords);
        Assert.IsTrue(keywords.All(static keyword =>
            keyword.Text == "製品Ｘ"
            && keyword.TextSource == EmphasisTextSourceKind.OriginalTranscript
            && keyword.Reasons.Contains(EmphasisReasonKind.ProductName)));
    }

    [TestMethod]
    public void ProductNamesRequireExplicitSelectedDictionaryIds()
    {
        Fixture fixture = Fixture.CreateDefault();

        EmphasisCandidateScanResult result = EmphasisCandidateService.FindCandidates(
            fixture.Revision,
            fixture.SourceSpans,
            fixture.MetadataRevision,
            [fixture.ProductTerm],
            fixture.TermScope,
            productTermEntryIds: [],
            fixture.ModelRunId);

        Assert.IsFalse(result.Candidates.Any(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.ProductName)));
        Assert.IsTrue(result.Candidates.Any(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.Number)));
    }

    [TestMethod]
    public void ProductMatchingDoesNotUseCaseOrFuzzyExpansion()
    {
        Fixture caseDifference = Fixture.CreateDefault(productText: "製品x");
        Fixture fuzzy = Fixture.CreateDefault(productText: "製晶Ｘ");

        Assert.IsFalse(caseDifference.Scan().Candidates.Any(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.ProductName)));
        Assert.IsFalse(fuzzy.Scan().Candidates.Any(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.ProductName)));
    }

    [TestMethod]
    public void AnswerRuleRequiresImmediatelyPrecedingExplicitQuestionPunctuation()
    {
        Fixture noQuestion = Fixture.CreateDefault(questionText: "製品Ｘについて");

        Assert.IsFalse(noQuestion.Scan().Candidates.Any(candidate =>
            candidate.Reasons.Contains(
                EmphasisReasonKind.AnswerAfterExplicitQuestion)));
    }

    [TestMethod]
    public void MixedSourceIdentitySkipsOnlyUnsupportedManualMetadata()
    {
        Fixture fixture = Fixture.CreateDefault(mixedSourceIdentity: true);

        EmphasisCandidateScanResult result = fixture.Scan();

        Assert.AreEqual(2, result.SkippedMetadataWithoutSupportingContextCount);
        Assert.IsFalse(result.Candidates.Any(candidate =>
            candidate.TextSource == EmphasisTextSourceKind.ManualSummary));
        Assert.IsTrue(result.Candidates.Any(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.Keyword)));
        Assert.IsTrue(result.Candidates.Any(candidate =>
            candidate.Reasons.Contains(EmphasisReasonKind.Number)));
    }

    [TestMethod]
    public void DictionaryAndProductSelectionFailClosedForScopeDuplicatesAndMissingIds()
    {
        Fixture fixture = Fixture.CreateDefault();
        TermScope otherScope = TermScope.ForProject(EntityId<ProjectIdTag>.New());
        var otherTerm = new TermEntry(
            EntityId<TermEntryIdTag>.New(),
            otherScope,
            "Other",
            "other",
            version: 0);
        AssertFailure(
            () => EmphasisCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.MetadataRevision,
                [otherTerm],
                otherScope,
                [otherTerm.Id],
                fixture.ModelRunId),
            EmphasisCandidateFailureReason.DictionaryScopeMismatch);
        AssertFailure(
            () => EmphasisCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.MetadataRevision,
                [fixture.ProductTerm, fixture.ProductTerm],
                fixture.TermScope,
                [fixture.ProductTerm.Id],
                fixture.ModelRunId),
            EmphasisCandidateFailureReason.DuplicateDictionaryEntry);
        AssertFailure(
            () => EmphasisCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.MetadataRevision,
                [fixture.ProductTerm],
                fixture.TermScope,
                [EntityId<TermEntryIdTag>.New()],
                fixture.ModelRunId),
            EmphasisCandidateFailureReason.ProductTermNotFound);
    }

    [TestMethod]
    public void StaleMetadataAndMismatchedSourceEvidenceFailWithoutContentLeak()
    {
        Fixture fixture = Fixture.CreateDefault();
        Fixture changed = Fixture.CreateDefault(
            revisionId: fixture.Revision.Id,
            questionText: "Changed private question?");
        EmphasisCandidateException stale = AssertFailure(
            () => EmphasisCandidateService.FindCandidates(
                changed.Revision,
                changed.SourceSpans,
                fixture.MetadataRevision,
                [fixture.ProductTerm],
                fixture.TermScope,
                [fixture.ProductTerm.Id],
                fixture.ModelRunId),
            EmphasisCandidateFailureReason.TopicMetadataBindingMismatch);
        Assert.IsFalse(stale.ToString().Contains(
            "Changed private question",
            StringComparison.Ordinal));

        SourceSpan[] wrong = fixture.SourceSpans.ToArray();
        wrong[0] = wrong[1];
        AssertFailure(
            () => EmphasisCandidateService.FindCandidates(
                fixture.Revision,
                wrong,
                fixture.MetadataRevision,
                [fixture.ProductTerm],
                fixture.TermScope,
                [fixture.ProductTerm.Id],
                fixture.ModelRunId),
            EmphasisCandidateFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void PreCancellationAndPublicSurfaceRemainReviewOnly()
    {
        Fixture fixture = Fixture.CreateDefault();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            EmphasisCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.MetadataRevision,
                [fixture.ProductTerm],
                fixture.TermScope,
                [fixture.ProductTerm.Id],
                fixture.ModelRunId,
                cancellation.Token));

        EmphasisCandidateScanResult result = fixture.Scan();
        foreach (Type type in new[]
        {
            typeof(EmphasisTermEvidence),
            typeof(EmphasisCandidate),
            typeof(EmphasisCandidateScanResult),
            typeof(EmphasisCandidateException),
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
            ((IList<EmphasisCandidate>)result.Candidates).Clear());
        CollectionAssert.AreEqual(
            new[] { "FindCandidates" },
            typeof(EmphasisCandidateService)
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsNull(typeof(EmphasisCandidateService).GetMethod("Adopt"));
        Assert.IsNull(typeof(EmphasisCandidateService).GetMethod("GenerateSummary"));
    }

    private static EmphasisCandidateException AssertFailure(
        Action action,
        EmphasisCandidateFailureReason reason)
    {
        EmphasisCandidateException exception =
            Assert.ThrowsExactly<EmphasisCandidateException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(new RationalTime(start, rate), new RationalTime(duration, rate));

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed record TokenInput(string Text, TimeRange? Range);

    private sealed record UtteranceInput(
        int Value,
        int Speaker,
        string Label,
        string Text,
        TimeRange Range,
        IReadOnlyList<TokenInput> Tokens);

    private sealed class Fixture
    {
        private Fixture(
            Project project,
            TranscriptRevision revision,
            IReadOnlyList<SourceSpan> sourceSpans,
            TopicMetadataRevision metadataRevision,
            TermScope termScope,
            TermEntry productTerm,
            EntityId<ModelRunIdTag> modelRunId)
        {
            Project = project;
            Revision = revision;
            SourceSpans = sourceSpans;
            MetadataRevision = metadataRevision;
            TermScope = termScope;
            ProductTerm = productTerm;
            ModelRunId = modelRunId;
        }

        internal Project Project { get; }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal TopicMetadataRevision MetadataRevision { get; }

        internal TermScope TermScope { get; }

        internal TermEntry ProductTerm { get; }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal EmphasisCandidateScanResult Scan() =>
            EmphasisCandidateService.FindCandidates(
                Revision,
                SourceSpans,
                MetadataRevision,
                [ProductTerm],
                TermScope,
                [ProductTerm.Id],
                ModelRunId);

        internal static Fixture CreateDefault(
            string productText = "製品Ｘ",
            string questionText = "製品Ｘは？",
            string keyword = "価格",
            bool mixedSourceIdentity = false,
            EntityId<TranscriptRevisionIdTag>? revisionId = null)
        {
            TimeRange firstRange = Range(0, 2);
            TimeRange secondRange = Range(2, 2);
            TimeRange thirdRange = Range(4, 2);
            return Create(
                [
                    new UtteranceInput(
                        1,
                        1,
                        "Speaker 1",
                        questionText,
                        firstRange,
                        [
                            new TokenInput(productText, Range(0, 1)),
                            new TokenInput("は？", Range(1, 1)),
                        ]),
                    new UtteranceInput(
                        2,
                        2,
                        "Speaker 2",
                        "価格は１２３円です",
                        secondRange,
                        [
                            new TokenInput("価格", Range(4, 1, 2)),
                            new TokenInput("は", Range(5, 1, 2)),
                            new TokenInput("１２３", Range(6, 1, 2)),
                            new TokenInput("円です", Range(7, 1, 2)),
                        ]),
                    new UtteranceInput(
                        3,
                        1,
                        "Speaker 1",
                        productText,
                        thirdRange,
                        [new TokenInput(productText, thirdRange)]),
                ],
                mixedSourceIdentity,
                revisionId,
                keyword);
        }

        private static Fixture Create(
            IReadOnlyList<UtteranceInput> inputs,
            bool mixedSourceIdentity,
            EntityId<TranscriptRevisionIdTag>? revisionId,
            string keyword)
        {
            EntityId<TranscriptRevisionIdTag> actualRevisionId = revisionId
                ?? Id<TranscriptRevisionIdTag>(
                    "92000000-0000-0000-0000-000000000001");
            var project = new Project(
                Id<ProjectIdTag>("93000000-0000-0000-0000-000000000001"),
                "Emphasis candidate test",
                "Verify explicit emphasis evidence.",
                "Technical viewers",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            var utterances = new List<Utterance>();
            var sourceSpans = new List<SourceSpan>();
            for (int utteranceIndex = 0;
                utteranceIndex < inputs.Count;
                utteranceIndex++)
            {
                UtteranceInput input = inputs[utteranceIndex];
                var tokens = new List<Token>();
                for (int tokenIndex = 0;
                    tokenIndex < input.Tokens.Count;
                    tokenIndex++)
                {
                    TokenInput token = input.Tokens[tokenIndex];
                    tokens.Add(new Token(
                        Id<TranscriptTokenIdTag>(
                            $"94000000-0000-{input.Value:0000}-{tokenIndex + 1:0000}-000000000001"),
                        token.Text,
                        token.Range,
                        recognitionConfidence: 0.8));
                }

                var sentence = new Sentence(
                    Id<TranscriptSentenceIdTag>(
                        $"95000000-0000-0000-0000-{input.Value:000000000000}"),
                    input.Text,
                    tokens);
                EntityId<SourceSpanIdTag> sourceSpanId = Id<SourceSpanIdTag>(
                    $"96000000-0000-0000-0000-{input.Value:000000000000}");
                var utterance = new Utterance(
                    Id<TranscriptUtteranceIdTag>(
                        $"97000000-0000-0000-0000-{input.Value:000000000000}"),
                    sourceSpanId,
                    Id<TranscriptSpeakerIdTag>(
                        $"98000000-0000-0000-0000-{input.Speaker:000000000000}"),
                    input.Label,
                    input.Text,
                    input.Range,
                    recognitionConfidence: 0.8,
                    [sentence]);
                utterances.Add(utterance);
                sourceSpans.Add(new SourceSpan(
                    sourceSpanId,
                    Id<AssetIdTag>(mixedSourceIdentity && utteranceIndex == 2
                        ? "99000000-0000-0000-0000-000000000002"
                        : "99000000-0000-0000-0000-000000000001"),
                    "audio:1",
                    input.Range,
                    new ContentDigest(new string('a', 64)),
                    actualRevisionId,
                    tokens.Select(static token => token.Id).ToArray()));
            }

            var revision = new TranscriptRevision(
                actualRevisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                utterances);
            EntityId<ModelRunIdTag> modelRunId = Id<ModelRunIdTag>(
                "9a000000-0000-0000-0000-000000000001");
            TopicBoundaryScanResult boundaryScan = TopicBoundaryService.FindCandidates(
                revision,
                sourceSpans,
                modelRunId);
            TopicRangeRevision topicRange = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                Id<TopicRangeIdTag>(
                    "9b000000-0000-0000-0000-000000000001"),
                Id<TopicRangeRevisionIdTag>(
                    "9c000000-0000-0000-0000-000000000001"),
                null,
                null,
                Range(0, 6),
                ActorSid,
                CreatedAt,
                "Define topic range.");
            TopicMetadataRevision metadata = TopicMetadataService.CreateInitial(
                project,
                revision,
                sourceSpans,
                topicRange,
                speakerAssignments: null,
                topicOrdinal: 1,
                Id<TopicMetadataIdTag>(
                    "9d000000-0000-0000-0000-000000000001"),
                Id<TopicMetadataRevisionIdTag>(
                    "9e000000-0000-0000-0000-000000000001"),
                ActorSid,
                CreatedAt,
                "Create metadata.");
            metadata = TopicMetadataService.SetSummary(
                project,
                revision,
                sourceSpans,
                topicRange,
                null,
                metadata,
                Id<TopicMetadataRevisionIdTag>(
                    "9e000000-0000-0000-0000-000000000002"),
                "価格のまとめ",
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Set summary.");
            metadata = TopicMetadataService.SetKeywords(
                project,
                revision,
                sourceSpans,
                topicRange,
                null,
                metadata,
                Id<TopicMetadataRevisionIdTag>(
                    "9e000000-0000-0000-0000-000000000003"),
                [keyword],
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Set keyword.");
            metadata = TopicMetadataService.SetImportantStatements(
                project,
                revision,
                sourceSpans,
                topicRange,
                null,
                metadata,
                Id<TopicMetadataRevisionIdTag>(
                    "9e000000-0000-0000-0000-000000000004"),
                [sourceSpans[1].Id],
                ActorSid,
                CreatedAt.AddMinutes(3),
                "Set important statement.");
            metadata = TopicMetadataService.SetPublicationCautions(
                project,
                revision,
                sourceSpans,
                topicRange,
                null,
                metadata,
                Id<TopicMetadataRevisionIdTag>(
                    "9e000000-0000-0000-0000-000000000005"),
                ["製品名を確認"],
                ActorSid,
                CreatedAt.AddMinutes(4),
                "Set caution.");
            TermScope termScope = TermScope.ForProject(project.Id);
            var productTerm = new TermEntry(
                Id<TermEntryIdTag>(
                    "9f000000-0000-0000-0000-000000000001"),
                termScope,
                "製品X",
                "せいひんえっくす",
                version: 2);
            return new Fixture(
                project,
                revision,
                sourceSpans,
                metadata,
                termScope,
                productTerm,
                modelRunId);
        }
    }
}