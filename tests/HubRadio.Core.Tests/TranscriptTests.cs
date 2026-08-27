using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TranscriptTests
{
    [TestMethod]
    public void TranscriptTypesAreSealedImmutableAndUseExistingProvenanceIds()
    {
        CollectionAssert.AreEqual(
            new[] { TranscriptChangeSource.Machine, TranscriptChangeSource.Human },
            Enum.GetValues<TranscriptChangeSource>());

        Type[] markerTypes =
        [
            typeof(TranscriptUtteranceIdTag),
            typeof(TranscriptSentenceIdTag),
            typeof(TranscriptSpeakerIdTag),
        ];
        foreach (Type markerType in markerTypes)
        {
            Assert.IsTrue(markerType.IsPublic);
            Assert.IsTrue(markerType.IsSealed);
        }

        AssertPublicGetOnlyProperties(
            typeof(TranscriptRevision),
            "Id",
            "ParentRevisionId",
            "ChangeSource",
            "CreatedAt",
            "Utterances");
        AssertPublicGetOnlyProperties(
            typeof(Utterance),
            "Id",
            "SourceSpanId",
            "SpeakerId",
            "SpeakerLabel",
            "Text",
            "Range",
            "RecognitionConfidence",
            "Sentences",
            "Tokens");
        AssertPublicGetOnlyProperties(typeof(Sentence), "Id", "Text", "Tokens");
        AssertPublicGetOnlyProperties(
            typeof(Token),
            "Id",
            "Text",
            "Range",
            "RecognitionConfidence",
            "ManualTimingAdjustmentRequired");

        Assert.AreEqual(
            typeof(EntityId<TranscriptRevisionIdTag>),
            typeof(TranscriptRevision).GetProperty("Id")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<SourceSpanIdTag>),
            typeof(Utterance).GetProperty("SourceSpanId")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<TranscriptTokenIdTag>),
            typeof(Token).GetProperty("Id")!.PropertyType);
    }

    [TestMethod]
    public void UntimedTokenPreservesMissingTimingAndFallsBackToParentForEditing()
    {
        Token token = CreateToken("時刻なし", range: null, recognitionConfidence: null);
        TimeRange utteranceRange = Range(100, 25);
        Utterance utterance = CreateUtterance(utteranceRange, [CreateSentence([token])]);

        Assert.IsNull(token.Range);
        Assert.IsNull(token.RecognitionConfidence);
        Assert.IsTrue(token.ManualTimingAdjustmentRequired);
        Assert.AreEqual(utteranceRange, utterance.ResolveEditingRange(token.Id));
    }

    [TestMethod]
    public void TimedTokenUsesItsExactRangeWithoutClampingOrManualAdjustment()
    {
        TimeRange tokenRange = new(new RationalTime(103, 10), new RationalTime(7, 10));
        Token token = CreateToken("正確", tokenRange, 0.875d);
        Utterance utterance = CreateUtterance(Range(10, 2), [CreateSentence([token])]);

        Assert.AreEqual(tokenRange, token.Range);
        Assert.AreEqual(0.875d, token.RecognitionConfidence);
        Assert.IsFalse(token.ManualTimingAdjustmentRequired);
        Assert.AreEqual(tokenRange, utterance.ResolveEditingRange(token.Id));
    }

    [TestMethod]
    public void RevisionsPreserveMachineAndHumanOriginsParentAndTimestamp()
    {
        EntityId<TranscriptRevisionIdTag> machineId = EntityId<TranscriptRevisionIdTag>.New();
        DateTimeOffset machineTime = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
        TranscriptRevision machine = new(
            machineId,
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            machineTime,
            [CreateUtterance()]);

        EntityId<TranscriptRevisionIdTag> humanId = EntityId<TranscriptRevisionIdTag>.New();
        DateTimeOffset humanTime = new(2026, 8, 28, 18, 30, 0, TimeSpan.FromHours(9));
        TranscriptRevision human = new(
            humanId,
            machineId,
            TranscriptChangeSource.Human,
            humanTime,
            [CreateUtterance()]);

        Assert.IsNull(machine.ParentRevisionId);
        Assert.AreEqual(TranscriptChangeSource.Machine, machine.ChangeSource);
        Assert.AreEqual(machineTime, machine.CreatedAt);
        Assert.AreEqual(machineId, human.ParentRevisionId);
        Assert.AreEqual(TranscriptChangeSource.Human, human.ChangeSource);
        Assert.AreEqual(humanTime, human.CreatedAt);
    }

    [TestMethod]
    public void AggregateListsAreDefensivelyCopiedAndReadOnly()
    {
        Token originalToken = CreateToken("一", Range(0, 1));
        List<Token> tokenInput = [originalToken];
        Sentence sentence = CreateSentence(tokenInput);
        tokenInput[0] = CreateToken("二", Range(1, 1));
        tokenInput.Add(CreateToken("三", Range(2, 1)));

        List<Sentence> sentenceInput = [sentence];
        Utterance utterance = CreateUtterance(Range(0, 3), sentenceInput);
        sentenceInput[0] = CreateSentence([CreateToken("四", Range(0, 1))]);
        sentenceInput.Add(CreateSentence([CreateToken("五", Range(1, 1))]));

        List<Utterance> utteranceInput = [utterance];
        TranscriptRevision revision = CreateRevision(utteranceInput);
        utteranceInput[0] = CreateUtterance();
        utteranceInput.Add(CreateUtterance(Range(10, 1)));

        Assert.AreEqual(1, sentence.Tokens.Count);
        Assert.AreSame(originalToken, sentence.Tokens[0]);
        Assert.AreEqual(1, utterance.Sentences.Count);
        Assert.AreSame(sentence, utterance.Sentences[0]);
        Assert.AreEqual(1, utterance.Tokens.Count);
        Assert.AreSame(originalToken, utterance.Tokens[0]);
        Assert.AreEqual(1, revision.Utterances.Count);
        Assert.AreSame(utterance, revision.Utterances[0]);

        AssertReadOnly(sentence.Tokens, CreateToken("追加", range: null));
        AssertReadOnly(utterance.Sentences, CreateSentence());
        AssertReadOnly(utterance.Tokens, CreateToken("追加", range: null));
        AssertReadOnly(revision.Utterances, CreateUtterance());
    }

    [TestMethod]
    public void ConstructorsRejectNullRequiredReferencesAndElements()
    {
        EntityId<TranscriptTokenIdTag> tokenId = EntityId<TranscriptTokenIdTag>.New();
        Assert.ThrowsExactly<ArgumentNullException>(() => new Token(null!, "語", null, null));
        Assert.ThrowsExactly<ArgumentNullException>(() => new Token(tokenId, null!, null, null));

        EntityId<TranscriptSentenceIdTag> sentenceId = EntityId<TranscriptSentenceIdTag>.New();
        Assert.ThrowsExactly<ArgumentNullException>(() => new Sentence(null!, "文", [CreateToken()]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new Sentence(sentenceId, null!, [CreateToken()]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new Sentence(sentenceId, "文", null!));
        Assert.AreEqual(
            "tokens",
            Assert.ThrowsExactly<ArgumentException>(
                () => new Sentence(sentenceId, "文", [null!])).ParamName);

        EntityId<TranscriptUtteranceIdTag> utteranceId =
            EntityId<TranscriptUtteranceIdTag>.New();
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        EntityId<TranscriptSpeakerIdTag> speakerId = EntityId<TranscriptSpeakerIdTag>.New();
        TimeRange range = Range(0, 1);
        IReadOnlyList<Sentence> sentences = [CreateSentence()];
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Utterance(null!, sourceSpanId, speakerId, "Speaker 1", "文", range, null, sentences));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Utterance(utteranceId, null!, speakerId, "Speaker 1", "文", range, null, sentences));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Utterance(utteranceId, sourceSpanId, null!, "Speaker 1", "文", range, null, sentences));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Utterance(utteranceId, sourceSpanId, speakerId, null!, "文", range, null, sentences));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Utterance(utteranceId, sourceSpanId, speakerId, "Speaker 1", null!, range, null, sentences));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Utterance(utteranceId, sourceSpanId, speakerId, "Speaker 1", "文", range, null, null!));
        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => new Utterance(
                    utteranceId,
                    sourceSpanId,
                    speakerId,
                    "Speaker 1",
                    "文",
                    range,
                    null,
                    [null!])).ParamName);

        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TranscriptRevision(
                null!,
                null,
                TranscriptChangeSource.Machine,
                DateTimeOffset.UtcNow,
                []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TranscriptRevision(
                revisionId,
                null,
                TranscriptChangeSource.Machine,
                DateTimeOffset.UtcNow,
                null!));
        Assert.AreEqual(
            "utterances",
            Assert.ThrowsExactly<ArgumentException>(
                () => new TranscriptRevision(
                    revisionId,
                    null,
                    TranscriptChangeSource.Machine,
                    DateTimeOffset.UtcNow,
                    [null!])).ParamName);
    }

    [TestMethod]
    public void ConstructorsRejectBlankOrUntrimmedTextAndSpeakerLabels()
    {
        string[] invalidValues = [string.Empty, " ", "\t", " text", "text ", "\r\ntext"];

        foreach (string invalidValue in invalidValues)
        {
            Assert.AreEqual(
                "text",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateToken(invalidValue, range: null)).ParamName);
            Assert.AreEqual(
                "text",
                Assert.ThrowsExactly<ArgumentException>(
                    () => new Sentence(
                        EntityId<TranscriptSentenceIdTag>.New(),
                        invalidValue,
                        [CreateToken()])).ParamName);
            Assert.AreEqual(
                "speakerLabel",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateUtterance(speakerLabel: invalidValue)).ParamName);
            Assert.AreEqual(
                "text",
                Assert.ThrowsExactly<ArgumentException>(
                    () => CreateUtterance(text: invalidValue)).ParamName);
        }
    }

    [TestMethod]
    public void ConfidenceIsOptionalAndAcceptsOnlyFiniteClosedUnitInterval()
    {
        foreach (double boundary in new[] { 0d, 1d })
        {
            Assert.AreEqual(boundary, CreateToken(recognitionConfidence: boundary).RecognitionConfidence);
            Assert.AreEqual(
                boundary,
                CreateUtterance(recognitionConfidence: boundary).RecognitionConfidence);
        }

        Assert.IsNull(CreateToken(recognitionConfidence: null).RecognitionConfidence);
        Assert.IsNull(CreateUtterance(recognitionConfidence: null).RecognitionConfidence);

        double[] invalid =
        [
            -double.Epsilon,
            Math.BitIncrement(1d),
            double.NaN,
            double.NegativeInfinity,
            double.PositiveInfinity,
        ];
        foreach (double confidence in invalid)
        {
            Assert.AreEqual(
                "recognitionConfidence",
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => CreateToken(recognitionConfidence: confidence)).ParamName);
            Assert.AreEqual(
                "recognitionConfidence",
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => CreateUtterance(recognitionConfidence: confidence)).ParamName);
        }
    }

    [TestMethod]
    public void RevisionRejectsUnknownSourceAndSelfParent()
    {
        EntityId<TranscriptRevisionIdTag> id = EntityId<TranscriptRevisionIdTag>.New();

        Assert.AreEqual(
            "changeSource",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new TranscriptRevision(
                    id,
                    null,
                    (TranscriptChangeSource)99,
                    DateTimeOffset.UtcNow,
                    [])).ParamName);
        Assert.AreEqual(
            "parentRevisionId",
            Assert.ThrowsExactly<ArgumentException>(
                () => new TranscriptRevision(
                    id,
                    EntityId<TranscriptRevisionIdTag>.From(id.Value),
                    TranscriptChangeSource.Human,
                    DateTimeOffset.UtcNow,
                    [])).ParamName);
    }

    [TestMethod]
    public void PositiveRangesAreRequiredAndNegativeStartsRemainExact()
    {
        TimeRange negativeRange = new(new RationalTime(-5, 2), new RationalTime(3, 2));
        Token token = CreateToken(range: negativeRange);
        Utterance utterance = CreateUtterance(negativeRange, [CreateSentence([token])]);

        Assert.AreEqual(negativeRange, token.Range);
        Assert.AreEqual(negativeRange, utterance.Range);

        Assert.AreEqual(
            "range",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateToken(range: default(TimeRange))).ParamName);
        Assert.AreEqual(
            "range",
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateUtterance(range: default(TimeRange))).ParamName);
    }

    [TestMethod]
    public void UtteranceRejectsTokenRangesOutsideEitherBoundaryWithoutClamping()
    {
        TimeRange utteranceRange = Range(10, 2);
        Token startsBefore = CreateToken("前", new TimeRange(new RationalTime(9, 1), new RationalTime(2, 1)));
        Token endsAfter = CreateToken("後", new TimeRange(new RationalTime(11, 1), new RationalTime(2, 1)));

        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateUtterance(utteranceRange, [CreateSentence([startsBefore])])).ParamName);
        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateUtterance(utteranceRange, [CreateSentence([endsAfter])])).ParamName);
        Assert.AreEqual(new RationalTime(9, 1), startsBefore.Range!.Value.Start);
        Assert.AreEqual(new RationalTime(13, 1), endsAfter.Range!.Value.End);
    }

    [TestMethod]
    public void TimedTokensMustBeOrderedAndNonoverlappingEvenAcrossSentences()
    {
        Token first = CreateToken("一", Range(1, 2));
        Token overlapping = CreateToken("二", Range(2, 2));
        Assert.AreEqual(
            "tokens",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSentence([first, overlapping])).ParamName);

        Sentence later = CreateSentence([CreateToken("後", Range(5, 1))]);
        Sentence earlier = CreateSentence([CreateToken("前", Range(4, 1))]);
        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateUtterance(Range(0, 10), [later, earlier])).ParamName);

        Sentence withUntimedMiddle = CreateSentence(
            [
                CreateToken("一", Range(0, 1)),
                CreateToken("二", range: null),
                CreateToken("三", Range(1, 1)),
            ]);
        Utterance valid = CreateUtterance(Range(0, 2), [withUntimedMiddle]);
        Assert.AreEqual(3, valid.Tokens.Count);
    }

    [TestMethod]
    public void RevisionOrdersUtterancesByStartWhileAllowingOverlappingSpeakers()
    {
        Utterance first = CreateUtterance(Range(1, 3));
        Utterance overlapping = CreateUtterance(Range(2, 2));
        TranscriptRevision revision = CreateRevision([first, overlapping]);

        Assert.AreEqual(2, revision.Utterances.Count);

        Assert.AreEqual(
            "utterances",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateRevision([overlapping, first])).ParamName);
    }

    [TestMethod]
    public void DuplicateIdsAreRejectedAtEveryAggregateBoundary()
    {
        Token token = CreateToken();
        Token equivalentToken = new(
            EntityId<TranscriptTokenIdTag>.From(token.Id.Value),
            "別の語",
            range: null,
            recognitionConfidence: null);
        Assert.AreEqual(
            "tokens",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSentence([token, equivalentToken])).ParamName);

        Sentence sentence = CreateSentence();
        Sentence equivalentSentence = new(
            EntityId<TranscriptSentenceIdTag>.From(sentence.Id.Value),
            "別の文",
            [CreateToken("別")]);
        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateUtterance(Range(0, 2), [sentence, equivalentSentence])).ParamName);

        Token shared = CreateToken();
        Sentence firstSentence = CreateSentence([shared]);
        Sentence secondSentence = CreateSentence([shared]);
        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateUtterance(Range(0, 1), [firstSentence, secondSentence])).ParamName);

        Utterance utterance = CreateUtterance();
        Utterance equivalentUtterance = CreateUtterance(
            id: EntityId<TranscriptUtteranceIdTag>.From(utterance.Id.Value));
        Assert.AreEqual(
            "utterances",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateRevision([utterance, equivalentUtterance])).ParamName);

        Sentence reusedSentence = CreateSentence();
        Utterance firstUtterance = CreateUtterance(Range(0, 1), [reusedSentence]);
        Utterance secondUtterance = CreateUtterance(Range(1, 1), [reusedSentence]);
        Assert.AreEqual(
            "utterances",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateRevision([firstUtterance, secondUtterance])).ParamName);

        Token reusedToken = CreateToken();
        Utterance tokenOwnerOne = CreateUtterance(
            Range(0, 1),
            [CreateSentence([reusedToken])]);
        Utterance tokenOwnerTwo = CreateUtterance(
            Range(1, 1),
            [CreateSentence([reusedToken])]);
        Assert.AreEqual(
            "utterances",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateRevision([tokenOwnerOne, tokenOwnerTwo])).ParamName);
    }

    [TestMethod]
    public void SpeakerIdentityRemainsAnonymousStableAndConsistentWithinRevision()
    {
        EntityId<TranscriptSpeakerIdTag> anonymousId = EntityId<TranscriptSpeakerIdTag>.New();
        Utterance first = CreateUtterance(
            Range(0, 1),
            speakerId: anonymousId,
            speakerLabel: "Speaker 1");
        Utterance second = CreateUtterance(
            Range(1, 1),
            speakerId: anonymousId,
            speakerLabel: "Speaker 1");
        TranscriptRevision revision = CreateRevision([first, second]);

        Assert.AreEqual(anonymousId, revision.Utterances[0].SpeakerId);
        Assert.AreEqual("Speaker 1", revision.Utterances[0].SpeakerLabel);

        Utterance conflicting = CreateUtterance(
            Range(1, 1),
            speakerId: anonymousId,
            speakerLabel: "Invented Person");
        Assert.AreEqual(
            "utterances",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateRevision([first, conflicting])).ParamName);
    }

    [TestMethod]
    public void EmptySentenceAndUtteranceUnitsAreRejectedButEmptyRevisionIsAllowed()
    {
        Assert.AreEqual(
            "tokens",
            Assert.ThrowsExactly<ArgumentException>(
                () => new Sentence(
                    EntityId<TranscriptSentenceIdTag>.New(),
                    "文",
                    [])).ParamName);
        Assert.AreEqual(
            "sentences",
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateUtterance(sentences: [])).ParamName);

        Assert.AreEqual(0, CreateRevision([]).Utterances.Count);
    }

    [TestMethod]
    public void ResolvingAnUnknownOrNullTokenIdFailsClosed()
    {
        Utterance utterance = CreateUtterance();

        Assert.ThrowsExactly<ArgumentNullException>(() => utterance.ResolveEditingRange(null!));
        Assert.AreEqual(
            "tokenId",
            Assert.ThrowsExactly<ArgumentException>(
                () => utterance.ResolveEditingRange(
                    EntityId<TranscriptTokenIdTag>.New())).ParamName);
    }

    private static TranscriptRevision CreateRevision(IReadOnlyList<Utterance> utterances)
    {
        return new TranscriptRevision(
            EntityId<TranscriptRevisionIdTag>.New(),
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            utterances);
    }

    private static Utterance CreateUtterance(
        TimeRange? range = null,
        IReadOnlyList<Sentence>? sentences = null,
        EntityId<TranscriptUtteranceIdTag>? id = null,
        EntityId<TranscriptSpeakerIdTag>? speakerId = null,
        string speakerLabel = "Speaker 1",
        string text = "テスト文",
        double? recognitionConfidence = null)
    {
        TimeRange actualRange = range ?? Range(0, 1);
        return new Utterance(
            id ?? EntityId<TranscriptUtteranceIdTag>.New(),
            EntityId<SourceSpanIdTag>.New(),
            speakerId ?? EntityId<TranscriptSpeakerIdTag>.New(),
            speakerLabel,
            text,
            actualRange,
            recognitionConfidence,
            sentences ?? [CreateSentence([CreateToken(range: actualRange)])]);
    }

    private static Sentence CreateSentence(IReadOnlyList<Token>? tokens = null)
    {
        return new Sentence(
            EntityId<TranscriptSentenceIdTag>.New(),
            "テスト文",
            tokens ?? [CreateToken()]);
    }

    private static Token CreateToken(
        string text = "語",
        TimeRange? range = null,
        double? recognitionConfidence = null)
    {
        return new Token(
            EntityId<TranscriptTokenIdTag>.New(),
            text,
            range,
            recognitionConfidence);
    }

    private static TimeRange Range(long start, long duration)
    {
        return new TimeRange(new RationalTime(start, 1), new RationalTime(duration, 1));
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        Assert.IsTrue(type.IsSealed);
        System.Reflection.PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        IList<T> exposed = (IList<T>)values;
        Assert.ThrowsExactly<NotSupportedException>(() => exposed.Add(replacement));
        Assert.ThrowsExactly<NotSupportedException>(() => exposed[0] = replacement);
    }
}