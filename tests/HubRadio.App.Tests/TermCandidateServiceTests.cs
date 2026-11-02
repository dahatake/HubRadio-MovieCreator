using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Projects;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TermCandidateServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset CreatedAt =
        new(2037, 9, 10, 11, 12, 13, TimeSpan.Zero);
    private static readonly DateTimeOffset DecidedAt = CreatedAt.AddMinutes(2);

    [TestMethod]
    // FR-004 AC-052: 辞書の語と異なる語を含む文字起こしから修正候補が提示される。
    public void FindCandidates_CreatesOnlyExactDeterministicCorrectionCandidates()
    {
        Fixture fixture = new();

        TermCandidateScanResult result = fixture.Scan();

        Assert.AreEqual(fixture.Revision.Id, result.SourceRevisionId);
        Assert.AreEqual(fixture.ModelRunId, result.ModelRunId);
        Assert.AreSame(fixture.Scope, result.TermScope);
        Assert.AreEqual(5, result.InspectedTokenCount);
        Assert.HasCount(3, result.Candidates);
        AssertCandidate(
            result.Candidates.Single(candidate => candidate.TokenId == fixture.ReadingTokenId),
            fixture,
            fixture.HubTerm,
            "ハブ",
            "Hub",
            TermCandidateMatchKind.DictionaryReading);
        AssertCandidate(
            result.Candidates.Single(candidate => candidate.TokenId == fixture.CaseTokenId),
            fixture,
            fixture.GitHubTerm,
            "github",
            "GitHub",
            TermCandidateMatchKind.WrittenFormCaseVariant);
        AssertCandidate(
            result.Candidates.Single(candidate => candidate.TokenId == fixture.CompatibilityTokenId),
            fixture,
            fixture.CppTerm,
            "Ｃ＋＋",
            "C++",
            TermCandidateMatchKind.WrittenFormCompatibilityVariant);
        Assert.IsFalse(result.Candidates.Any(
            candidate => candidate.TokenId == fixture.CanonicalTokenId));
        Assert.IsFalse(result.Candidates.Any(
            candidate => candidate.TokenId == fixture.FuzzyTokenId));
        Assert.AreEqual(
            "ハブ github Ｃ＋＋ Azure Copliot",
            fixture.Revision.Utterances[0].Text);
        Assert.IsTrue(result.Candidates.All(
            static candidate => candidate.Suggestion.Confidence is null));
        Assert.IsTrue(result.Candidates.All(
            static candidate => candidate.Suggestion.MissingContext is null));
    }

    [TestMethod]
    public void FindCandidates_DoesNotTreatEditDistanceAsEvidence()
    {
        Fixture fixture = new();

        TermCandidateScanResult result = TermCandidateService.FindCandidates(
            fixture.Revision,
            fixture.SourceSpans,
            [fixture.CopilotTerm],
            fixture.Scope,
            fixture.ModelRunId);

        Assert.AreEqual(5, result.InspectedTokenCount);
        Assert.HasCount(0, result.Candidates);
        Assert.AreEqual("Copliot", fixture.FuzzyToken.Text);
    }

    [TestMethod]
    public void FindCandidates_ReportsAmbiguityAndHasStableEvidenceOrder()
    {
        Fixture fixture = new();
        var alternate = new TermEntry(
            Id<TermEntryIdTag>("51000000-0000-0000-0000-000000000010"),
            fixture.Scope,
            "HUB Service",
            "ハブ",
            version: 0);

        TermCandidateScanResult first = TermCandidateService.FindCandidates(
            fixture.Revision,
            fixture.SourceSpans,
            [fixture.HubTerm, alternate],
            fixture.Scope,
            fixture.ModelRunId);
        TermCandidateScanResult second = TermCandidateService.FindCandidates(
            fixture.Revision,
            fixture.SourceSpans,
            [alternate, fixture.HubTerm],
            fixture.Scope,
            fixture.ModelRunId);

        Assert.HasCount(2, first.Candidates);
        Assert.IsTrue(first.Candidates.All(candidate =>
            candidate.TokenId == fixture.ReadingTokenId
            && candidate.Suggestion.MissingContext ==
                "Multiple dictionary entries match the same transcript token."));
        CollectionAssert.AreEqual(
            first.Candidates.Select(static candidate => candidate.ProposedText).ToArray(),
            second.Candidates.Select(static candidate => candidate.ProposedText).ToArray());
    }

    [TestMethod]
    public void FindCandidates_ReturnsEmptyForEmptyDictionaryAndHonorsCancellation()
    {
        Fixture fixture = new();
        TermCandidateScanResult empty = TermCandidateService.FindCandidates(
            fixture.Revision,
            fixture.SourceSpans,
            Array.Empty<TermEntry>(),
            fixture.Scope,
            fixture.ModelRunId);

        Assert.AreEqual(5, empty.InspectedTokenCount);
        Assert.HasCount(0, empty.Candidates);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            TermCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                fixture.Terms,
                fixture.Scope,
                fixture.ModelRunId,
                cancellation.Token));
    }

    [TestMethod]
    // FR-004 AC-052: 辞書は Project またはシリーズ単位で保持され、別の単位の語は適用されない。
    public void FindCandidates_RejectsScopeDuplicateAndSourceSpanMismatch()
    {
        Fixture fixture = new();
        var otherScopeEntry = new TermEntry(
            EntityId<TermEntryIdTag>.New(),
            TermScope.ForProject(EntityId<ProjectIdTag>.New()),
            "Hub",
            "ハブ",
            version: 0);
        AssertFailure(
            () => TermCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                [otherScopeEntry],
                fixture.Scope,
                fixture.ModelRunId),
            TermCandidateFailureReason.DictionaryScopeMismatch);
        AssertFailure(
            () => TermCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                [fixture.HubTerm, fixture.HubTerm],
                fixture.Scope,
                fixture.ModelRunId),
            TermCandidateFailureReason.DuplicateDictionaryEntry);

        SourceSpan span = fixture.SourceSpans[0];
        var wrongSpan = new SourceSpan(
            span.Id,
            span.AssetId,
            span.StreamId,
            span.Range,
            span.SourceDigest,
            EntityId<TranscriptRevisionIdTag>.New(),
            span.TranscriptTokenIds);
        AssertFailure(
            () => TermCandidateService.FindCandidates(
                fixture.Revision,
                [wrongSpan],
                fixture.Terms,
                fixture.Scope,
                fixture.ModelRunId),
            TermCandidateFailureReason.SourceSpanBindingMismatch);
    }

    [TestMethod]
    public void FindCandidates_RejectsMalformedUnicodeWithoutDisclosingIt()
    {
        Fixture fixture = new();
        var invalidToken = new Token(
            EntityId<TranscriptTokenIdTag>.New(),
            "private\uD800value",
            Range(0, 1),
            recognitionConfidence: 0.5);
        var sentence = new Sentence(
            EntityId<TranscriptSentenceIdTag>.New(),
            "private\uD800value",
            [invalidToken]);
        var utterance = new Utterance(
            EntityId<TranscriptUtteranceIdTag>.New(),
            EntityId<SourceSpanIdTag>.New(),
            EntityId<TranscriptSpeakerIdTag>.New(),
            "Speaker 1",
            "private\uD800value",
            Range(0, 1),
            recognitionConfidence: 0.5,
            [sentence]);
        var revision = new TranscriptRevision(
            EntityId<TranscriptRevisionIdTag>.New(),
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            CreatedAt,
            [utterance]);
        var span = new SourceSpan(
            utterance.SourceSpanId,
            fixture.SourceSpans[0].AssetId,
            "audio:1",
            utterance.Range,
            fixture.SourceSpans[0].SourceDigest,
            revision.Id,
            [invalidToken.Id]);

        TermCandidateException exception = AssertFailure(
            () => TermCandidateService.FindCandidates(
                revision,
                [span],
                [fixture.HubTerm],
                fixture.Scope,
                fixture.ModelRunId),
            TermCandidateFailureReason.InvalidText);

        AssertPrivate(exception, "private");
    }

    [TestMethod]
    public void RejectCandidate_RecordsRejectionWithoutCreatingRevision()
    {
        Fixture fixture = new();
        TermCandidate candidate = fixture.GetReadingCandidate();
        string sourceText = fixture.Revision.Utterances[0].Text;

        TermCandidateDecisionResult result = TermCandidateService.RejectCandidate(
            candidate,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.HubTerm,
            ActorSid,
            DecidedAt,
            "現在の発言表記を維持する");

        Assert.AreSame(candidate, result.OriginalCandidate);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
        Assert.AreEqual(
            SuggestionStatus.Rejected,
            result.DecidedCandidate.Suggestion.Status);
        Assert.AreEqual(candidate.Suggestion.Id, result.DecidedCandidate.Suggestion.Id);
        Assert.IsNull(result.TranscriptEdit);
        Assert.AreEqual(sourceText, fixture.Revision.Utterances[0].Text);
        Assert.AreEqual(
            TermCandidateDecisionKind.Rejected,
            result.DecisionRecord.Decision);
        Assert.AreEqual(candidate.Suggestion.Id, result.DecisionRecord.CandidateId);
        Assert.AreEqual(
            candidate.SourceRevisionDigest,
            result.DecisionRecord.SourceRevisionDigest);
        Assert.AreEqual(ActorSid, result.DecisionRecord.ActorSid);
        Assert.IsNull(result.DecisionRecord.ResultingRevisionId);
    }

    [TestMethod]
    // FR-004 AC-052: 自動修正が行われた箇所は、利用者の明示的な決定として修正前後が確認できる。
    public void AdoptCandidate_CreatesHumanChildOnlyAfterExplicitDecision()
    {
        Fixture fixture = new();
        TermCandidate candidate = fixture.GetReadingCandidate();
        var revisionId = Id<TranscriptRevisionIdTag>(
            "52000000-0000-0000-0000-000000000001");

        TermCandidateDecisionResult result = TermCandidateService.AdoptCandidate(
            candidate,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.HubTerm,
            revisionId,
            ActorSid,
            DecidedAt,
            "辞書表記を採用する");

        Assert.AreEqual("ハブ github Ｃ＋＋ Azure Copliot", fixture.Revision.Utterances[0].Text);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
        Assert.AreEqual(
            SuggestionStatus.Adopted,
            result.DecidedCandidate.Suggestion.Status);
        Assert.IsNotNull(result.TranscriptEdit);
        Assert.AreEqual(revisionId, result.TranscriptEdit.Revision.Id);
        Assert.AreEqual(fixture.Revision.Id, result.TranscriptEdit.Revision.ParentRevisionId);
        Assert.AreEqual(
            TranscriptChangeSource.Human,
            result.TranscriptEdit.Revision.ChangeSource);
        Assert.AreEqual("Hub github Ｃ＋＋ Azure Copliot", result.TranscriptEdit.Revision.Utterances[0].Text);
        Assert.AreEqual("Hub", result.TranscriptEdit.Revision.Utterances[0].Tokens[0].Text);
        Assert.AreEqual(
            TermCandidateDecisionKind.Adopted,
            result.DecisionRecord.Decision);
        Assert.AreEqual(revisionId, result.DecisionRecord.ResultingRevisionId);
        Assert.AreEqual(candidate.TokenId, result.DecisionRecord.TokenId);
        Assert.AreEqual(candidate.TermEntryId, result.DecisionRecord.TermEntryId);
    }

    [TestMethod]
    public void DecisionsRejectStaleCandidateExactTermSnapshotDriftAndSecondDecision()
    {
        Fixture fixture = new();
        TermCandidate candidate = fixture.GetReadingCandidate();
        var changedTerm = new TermEntry(
            fixture.HubTerm.Id,
            fixture.HubTerm.Scope,
            fixture.HubTerm.WrittenForm,
            fixture.HubTerm.Reading,
            version: 1);
        AssertFailure(
            () => TermCandidateService.RejectCandidate(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                changedTerm,
                ActorSid,
                DecidedAt,
                "古い辞書候補を拒否"),
            TermCandidateFailureReason.TermEntryChanged);

        (TranscriptRevision revision, IReadOnlyList<SourceSpan> spans) =
            fixture.CloneWithNewRevisionId();
        AssertFailure(
            () => TermCandidateService.RejectCandidate(
                candidate,
                revision,
                spans,
                fixture.HubTerm,
                ActorSid,
                DecidedAt,
                "古いrevision候補を拒否"),
            TermCandidateFailureReason.CandidateStale);

        (TranscriptRevision reusedIdRevision, IReadOnlyList<SourceSpan> reusedIdSpans) =
            fixture.CloneWithReusedIdAndChangedOtherToken();
        AssertFailure(
            () => TermCandidateService.RejectCandidate(
                candidate,
                reusedIdRevision,
                reusedIdSpans,
                fixture.HubTerm,
                ActorSid,
                DecidedAt,
                "revision ID再利用を拒否"),
            TermCandidateFailureReason.CandidateStale);

        TermCandidateDecisionResult rejected = TermCandidateService.RejectCandidate(
            candidate,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.HubTerm,
            ActorSid,
            DecidedAt,
            "一度だけ判断");
        AssertFailure(
            () => TermCandidateService.RejectCandidate(
                rejected.DecidedCandidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.HubTerm,
                ActorSid,
                DecidedAt,
                "二重判断を拒否"),
            TermCandidateFailureReason.CandidateAlreadyDecided);
    }

    [TestMethod]
    public void DecisionsRejectInvalidIdentityReasonAndTimestampPrivately()
    {
        Fixture fixture = new();
        TermCandidate candidate = fixture.GetReadingCandidate();
        foreach (string invalid in new[]
        {
            "not-a-sid",
            " S-1-5-18",
            @"C:\Users\private\Bearer-secret",
        })
        {
            TermCandidateException exception = AssertFailure(
                () => TermCandidateService.RejectCandidate(
                    candidate,
                    fixture.Revision,
                    fixture.SourceSpans,
                    fixture.HubTerm,
                    invalid,
                    DecidedAt,
                    "候補を却下"),
                TermCandidateFailureReason.InvalidDecision);
            AssertPrivate(exception, invalid);
        }

        TermCandidateException invalidReason = AssertFailure(
            () => TermCandidateService.RejectCandidate(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.HubTerm,
                ActorSid,
                DecidedAt,
                " private reason "),
            TermCandidateFailureReason.InvalidDecision);
        AssertPrivate(invalidReason, "private reason");
        AssertFailure(
            () => TermCandidateService.RejectCandidate(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.HubTerm,
                ActorSid,
                CreatedAt.AddTicks(-1),
                "時刻を拒否"),
            TermCandidateFailureReason.DecisionPrecedesSource);
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndDecisionRecordsContainNoTranscriptSnapshot()
    {
        Type[] immutableTypes =
        [
            typeof(TermCandidate),
            typeof(TermCandidateScanResult),
            typeof(TermCandidateDecisionRecord),
            typeof(TermCandidateDecisionResult),
        ];
        foreach (Type type in immutableTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(TermCandidate).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TermCandidateScanResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TermCandidateDecisionRecord).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TermCandidateDecisionResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TermCandidateException).GetConstructors().Length);
        Assert.IsTrue(typeof(TermCandidateService).IsAbstract);
        Assert.IsTrue(typeof(TermCandidateService).IsSealed);

        string[] decisionProperties = typeof(TermCandidateDecisionRecord)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(decisionProperties, "ObservedText");
        CollectionAssert.DoesNotContain(decisionProperties, "ProposedText");
        CollectionAssert.DoesNotContain(decisionProperties, "TranscriptText");
    }

    private static void AssertCandidate(
        TermCandidate candidate,
        Fixture fixture,
        TermEntry entry,
        string observed,
        string proposed,
        TermCandidateMatchKind matchKind)
    {
        Assert.AreEqual(SuggestionKind.TermCorrection, candidate.Suggestion.Kind);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Suggestion.Status);
        Assert.AreEqual(fixture.ModelRunId, candidate.Suggestion.ModelRunId);
        Assert.AreEqual(OriginKind.SourceSpan, candidate.Suggestion.Origin.Kind);
        Assert.AreEqual(fixture.SourceSpans[0].Id, candidate.Suggestion.Origin.SourceSpanId);
        Assert.AreEqual(fixture.Revision.Id, candidate.SourceRevisionId);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(fixture.Revision))),
            candidate.SourceRevisionDigest);
        Assert.AreEqual(fixture.SourceSpans[0].Id, candidate.SourceSpanId);
        Assert.AreSame(fixture.Scope, candidate.TermScope);
        Assert.AreEqual(entry.Id, candidate.TermEntryId);
        Assert.AreEqual(entry.Version, candidate.TermEntryVersion);
        Assert.AreEqual(entry.Reading, candidate.DictionaryReading);
        Assert.AreEqual(observed, candidate.ObservedText);
        Assert.AreEqual(proposed, candidate.ProposedText);
        Assert.AreEqual(matchKind, candidate.MatchKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(candidate.Suggestion.Reason));
        Assert.AreEqual(
            "The transcript remains unchanged until a user adopts this candidate.",
            candidate.Suggestion.Warning);
    }

    private static TermCandidateException AssertFailure(
        Action action,
        TermCandidateFailureReason reason)
    {
        TermCandidateException exception =
            Assert.ThrowsExactly<TermCandidateException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static void AssertPrivate(Exception exception, string secret)
    {
        Assert.IsFalse(exception.ToString().Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(
            "Bearer-secret",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\Users\private",
            StringComparison.OrdinalIgnoreCase));
    }

    private static EntityId<TTag> Id<TTag>(string value) => EntityId<TTag>.Parse(value);

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private sealed class Fixture
    {
        internal Fixture()
        {
            Scope = TermScope.ForSeries(
                Id<SeriesIdTag>("50000000-0000-0000-0000-000000000001"));
            ModelRunId = Id<ModelRunIdTag>(
                "50000000-0000-0000-0000-000000000002");
            ReadingTokenId = Id<TranscriptTokenIdTag>(
                "50000000-0000-0000-0000-000000000003");
            CaseTokenId = Id<TranscriptTokenIdTag>(
                "50000000-0000-0000-0000-000000000004");
            CompatibilityTokenId = Id<TranscriptTokenIdTag>(
                "50000000-0000-0000-0000-000000000005");
            CanonicalTokenId = Id<TranscriptTokenIdTag>(
                "50000000-0000-0000-0000-000000000006");
            FuzzyTokenId = Id<TranscriptTokenIdTag>(
                "50000000-0000-0000-0000-000000000007");
            ReadingToken = new Token(ReadingTokenId, "ハブ", Range(0, 1), 0.8);
            var caseToken = new Token(CaseTokenId, "github", Range(1, 1), 0.8);
            var compatibilityToken = new Token(
                CompatibilityTokenId,
                "Ｃ＋＋",
                Range(2, 1),
                0.8);
            var canonicalToken = new Token(
                CanonicalTokenId,
                "Azure",
                Range(3, 1),
                0.8);
            FuzzyToken = new Token(FuzzyTokenId, "Copliot", Range(4, 1), 0.8);
            Token[] tokens =
            [
                ReadingToken,
                caseToken,
                compatibilityToken,
                canonicalToken,
                FuzzyToken,
            ];
            var spanId = Id<SourceSpanIdTag>(
                "50000000-0000-0000-0000-000000000008");
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(
                    "50000000-0000-0000-0000-000000000009"),
                "ハブ github Ｃ＋＋ Azure Copliot",
                tokens);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    "50000000-0000-0000-0000-00000000000a"),
                spanId,
                Id<TranscriptSpeakerIdTag>(
                    "50000000-0000-0000-0000-00000000000b"),
                "Speaker 1",
                "ハブ github Ｃ＋＋ Azure Copliot",
                Range(0, 5),
                0.8,
                [sentence]);
            Revision = new TranscriptRevision(
                Id<TranscriptRevisionIdTag>(
                    "50000000-0000-0000-0000-00000000000c"),
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [utterance]);
            var span = new SourceSpan(
                spanId,
                Id<AssetIdTag>(
                    "50000000-0000-0000-0000-00000000000d"),
                "audio:1",
                utterance.Range,
                new ContentDigest(new string('a', 64)),
                Revision.Id,
                tokens.Select(static token => token.Id).ToArray());
            SourceSpans = [span];

            HubTerm = Term(
                "51000000-0000-0000-0000-000000000001",
                "Hub",
                "ハブ");
            GitHubTerm = Term(
                "51000000-0000-0000-0000-000000000002",
                "GitHub",
                "ギットハブ");
            CppTerm = Term(
                "51000000-0000-0000-0000-000000000003",
                "C++",
                "シープラスプラス");
            AzureTerm = Term(
                "51000000-0000-0000-0000-000000000004",
                "Azure",
                "アジュール");
            CopilotTerm = Term(
                "51000000-0000-0000-0000-000000000005",
                "Copilot",
                "コパイロット");
            Terms = [HubTerm, GitHubTerm, CppTerm, AzureTerm, CopilotTerm];
        }

        internal TermScope Scope { get; }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal IReadOnlyList<TermEntry> Terms { get; }

        internal TermEntry HubTerm { get; }

        internal TermEntry GitHubTerm { get; }

        internal TermEntry CppTerm { get; }

        internal TermEntry AzureTerm { get; }

        internal TermEntry CopilotTerm { get; }

        internal EntityId<TranscriptTokenIdTag> ReadingTokenId { get; }

        internal EntityId<TranscriptTokenIdTag> CaseTokenId { get; }

        internal EntityId<TranscriptTokenIdTag> CompatibilityTokenId { get; }

        internal EntityId<TranscriptTokenIdTag> CanonicalTokenId { get; }

        internal EntityId<TranscriptTokenIdTag> FuzzyTokenId { get; }

        internal Token ReadingToken { get; }

        internal Token FuzzyToken { get; }

        internal TermCandidateScanResult Scan() =>
            TermCandidateService.FindCandidates(
                Revision,
                SourceSpans,
                Terms,
                Scope,
                ModelRunId);

        internal TermCandidate GetReadingCandidate() =>
            Scan().Candidates.Single(candidate => candidate.TokenId == ReadingTokenId);

        internal (TranscriptRevision Revision, IReadOnlyList<SourceSpan> Spans)
            CloneWithNewRevisionId()
        {
            EntityId<TranscriptRevisionIdTag> revisionId =
                Id<TranscriptRevisionIdTag>(
                    "52000000-0000-0000-0000-000000000010");
            var revision = new TranscriptRevision(
                revisionId,
                Revision.Id,
                TranscriptChangeSource.Human,
                CreatedAt.AddMinutes(1),
                Revision.Utterances);
            SourceSpan source = SourceSpans[0];
            var span = new SourceSpan(
                source.Id,
                source.AssetId,
                source.StreamId,
                source.Range,
                source.SourceDigest,
                revisionId,
                source.TranscriptTokenIds);
            return (revision, [span]);
        }

        internal (TranscriptRevision Revision, IReadOnlyList<SourceSpan> Spans)
            CloneWithReusedIdAndChangedOtherToken()
        {
            Utterance sourceUtterance = Revision.Utterances[0];
            Token[] tokens = sourceUtterance.Tokens
                .Select(token => token.Id == CaseTokenId
                    ? new Token(
                        token.Id,
                        "GitHub",
                        token.Range,
                        token.RecognitionConfidence)
                    : token)
                .ToArray();
            var sentence = new Sentence(
                sourceUtterance.Sentences[0].Id,
                "ハブ GitHub Ｃ＋＋ Azure Copliot",
                tokens);
            var utterance = new Utterance(
                sourceUtterance.Id,
                sourceUtterance.SourceSpanId,
                sourceUtterance.SpeakerId,
                sourceUtterance.SpeakerLabel,
                "ハブ GitHub Ｃ＋＋ Azure Copliot",
                sourceUtterance.Range,
                sourceUtterance.RecognitionConfidence,
                [sentence]);
            var revision = new TranscriptRevision(
                Revision.Id,
                Revision.ParentRevisionId,
                Revision.ChangeSource,
                Revision.CreatedAt,
                [utterance]);
            SourceSpan source = SourceSpans[0];
            var span = new SourceSpan(
                source.Id,
                source.AssetId,
                source.StreamId,
                source.Range,
                source.SourceDigest,
                revision.Id,
                source.TranscriptTokenIds);
            return (revision, [span]);
        }

        private TermEntry Term(string id, string writtenForm, string reading) =>
            new(Id<TermEntryIdTag>(id), Scope, writtenForm, reading, version: 0);
    }
}