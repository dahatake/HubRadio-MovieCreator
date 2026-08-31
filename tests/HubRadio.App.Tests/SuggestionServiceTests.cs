using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Provenance;
using HubRadio.Core.Projects;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SuggestionServiceTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2043, 6, 7, 8, 9, 10, TimeSpan.Zero);

    private static readonly DateTimeOffset DecidedAt = CreatedAt.AddMinutes(5);

    [TestMethod]
    public void RegisterPreservesEveryImplementedProducerPayloadAndExactEvidence()
    {
        ProducerFixture fixture = new();
        var registered = new List<SuggestionCandidate>();

        TermSuggestionCandidate term = SuggestionService.Register(
            fixture.Term,
            registered);
        registered.Add(term);
        SilenceSuggestionCandidate silence = SuggestionService.Register(
            fixture.Silence,
            registered);
        registered.Add(silence);
        EditorialPhraseSuggestionCandidate editorial = SuggestionService.Register(
            fixture.Editorial,
            registered);
        registered.Add(editorial);
        RepetitionSuggestionCandidate repetition = SuggestionService.Register(
            fixture.Repetition.Payload,
            fixture.Repetition.Suggestion,
            registered);
        registered.Add(repetition);
        TopicBoundarySuggestionCandidate topic = SuggestionService.Register(
            fixture.TopicBoundary,
            registered);
        registered.Add(topic);
        AudioReviewSuggestionCandidate audio = SuggestionService.Register(
            fixture.AudioReview,
            registered);
        registered.Add(audio);
        ContextDeviationSuggestionCandidate context = SuggestionService.Register(
            fixture.ContextDeviation,
            registered);
        registered.Add(context);
        EmphasisSuggestionCandidate emphasis = SuggestionService.Register(
            fixture.Emphasis,
            registered);
        registered.Add(emphasis);

        Assert.HasCount(8, registered);
        Assert.AreSame(fixture.Term, term.Payload);
        Assert.AreSame(fixture.Silence, silence.Payload);
        Assert.AreSame(fixture.Editorial, editorial.Payload);
        Assert.AreSame(fixture.Repetition.Payload, repetition.Payload);
        Assert.AreSame(fixture.TopicBoundary, topic.Payload);
        Assert.AreSame(fixture.AudioReview, audio.Payload);
        Assert.AreSame(fixture.ContextDeviation, context.Payload);
        Assert.AreSame(fixture.Emphasis, emphasis.Payload);

        AssertCommon(
            term,
            fixture.Term.Suggestion,
            fixture.Term.SourceRevisionDigest,
            SuggestionProducerKind.TermCorrection);
        AssertCommon(
            silence,
            fixture.Silence.Suggestion,
            fixture.Silence.SourceSpan.SourceDigest,
            SuggestionProducerKind.Silence);
        AssertCommon(
            editorial,
            fixture.Editorial.Suggestion,
            fixture.Editorial.SourceRevisionDigest,
            SuggestionProducerKind.EditorialPhrase);
        AssertCommon(
            repetition,
            fixture.Repetition.Suggestion,
            fixture.Repetition.Payload.SourceRevisionDigest,
            SuggestionProducerKind.Repetition);
        AssertCommon(
            topic,
            fixture.TopicBoundary.Suggestion,
            fixture.TopicBoundary.SourceRevisionDigest,
            SuggestionProducerKind.TopicBoundary);
        AssertCommon(
            audio,
            fixture.AudioReview.Suggestion,
            fixture.AudioReview.SourceSpan.SourceDigest,
            SuggestionProducerKind.AudioReview);
        AssertCommon(
            context,
            fixture.ContextDeviation.Suggestion,
            fixture.ContextDeviation.SourceRevisionDigest,
            SuggestionProducerKind.ContextDeviation);
        AssertCommon(
            emphasis,
            fixture.Emphasis.Suggestion,
            fixture.Emphasis.SourceRevisionDigest,
            SuggestionProducerKind.Emphasis);
    }

    [TestMethod]
    public void RegisterRejectsDuplicateSuggestionAndDuplicateRepetitionEvidence()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate term = SuggestionService.Register(
            fixture.Term,
            []);
        AssertFailure(
            () => SuggestionService.Register(fixture.Term, [term]),
            SuggestionServiceFailureReason.DuplicateCandidate);
        AssertFailure(
            () => SuggestionService.Register(fixture.Editorial, [term, term]),
            SuggestionServiceFailureReason.DuplicateCandidate);

        RepetitionSuggestionCandidate first = SuggestionService.Register(
            fixture.Repetition.Payload,
            fixture.Repetition.Suggestion,
            []);
        RepetitionInput duplicate = CreateRepetitionInput(
            fixture.Repetition.Evidence,
            EntityId<SuggestionIdTag>.New());
        Assert.AreEqual(
            fixture.Repetition.Payload.CandidateKey,
            duplicate.Payload.CandidateKey);
        AssertFailure(
            () => SuggestionService.Register(
                duplicate.Payload,
                duplicate.Suggestion,
                [first]),
            SuggestionServiceFailureReason.DuplicateRepetitionCandidate);
    }

    [TestMethod]
    public void ModifyRejectsDuplicateRepetitionEvidenceDespiteAnIncompleteRegistrationSet()
    {
        ProducerFixture fixture = new();
        RepetitionSuggestionCandidate original = SuggestionService.Register(
            fixture.Repetition.Payload,
            fixture.Repetition.Suggestion,
            []);
        RepetitionInput duplicate = CreateRepetitionInput(
            fixture.Repetition.Evidence,
            EntityId<SuggestionIdTag>.New());
        RepetitionSuggestionCandidate replacement = SuggestionService.Register(
            duplicate.Payload,
            duplicate.Suggestion,
            []);
        Assert.AreEqual(
            original.Payload.CandidateKey,
            replacement.Payload.CandidateKey);

        AssertFailure(
            () => SuggestionService.Modify(
                original,
                replacement,
                original.SourceDigest,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "A replacement must carry different repetition evidence."),
            SuggestionServiceFailureReason.DuplicateRepetitionCandidate);
    }

    [TestMethod]
    public void StaleProjectionAndRejectionAreImmutableAndAppendOnly()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Editorial,
            []);
        var changedDigest = new ContentDigest(new string('f', 64));

        SuggestionCandidateDisplayState stale = SuggestionService.GetDisplayState(
            candidate,
            changedDigest,
            []);

        Assert.AreSame(candidate, stale.Candidate);
        Assert.AreEqual(SuggestionCandidateFreshness.Stale, stale.Freshness);
        Assert.AreEqual(SuggestionStatus.Proposed, stale.Status);
        Assert.IsFalse(stale.CanDecide);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        AssertFailure(
            () => SuggestionService.Reject(
                candidate,
                changedDigest,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Reject stale evidence."),
            SuggestionServiceFailureReason.CandidateStale);

        EntityId<SuggestionDecisionIdTag> decisionId =
            EntityId<SuggestionDecisionIdTag>.New();
        SuggestionRejectionResult result = SuggestionService.Reject(
            candidate,
            candidate.SourceDigest,
            [],
            decisionId,
            ActorSid,
            DecidedAt,
            "Keep the source interval.");

        Assert.AreSame(candidate, result.OriginalCandidate);
        Assert.AreSame(fixture.Editorial, candidate.Payload);
        Assert.AreSame(fixture.Editorial, Assert.IsInstanceOfType<
            EditorialPhraseSuggestionCandidate>(result.DecidedCandidate).Payload);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        Assert.AreEqual(SuggestionStatus.Rejected, result.DecidedCandidate.Status);
        Assert.AreEqual(decisionId, result.DecisionRecord.Id);
        Assert.AreEqual(candidate.Id, result.DecisionRecord.CandidateId);
        Assert.AreEqual(candidate.SourceDigest, result.DecisionRecord.SourceDigest);
        Assert.AreEqual(candidate.Origin, result.DecisionRecord.Origin);
        Assert.AreEqual(ActorSid, result.DecisionRecord.ActorSid);
        Assert.AreEqual(DecidedAt, result.DecisionRecord.DecidedAt);
        Assert.HasCount(1, result.DecisionHistory);
        Assert.AreSame(result.DecisionRecord, result.DecisionHistory[0]);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SuggestionDecisionRecord>)result.DecisionHistory).Clear());

        SuggestionCandidateDisplayState decided = SuggestionService.GetDisplayState(
            candidate,
            candidate.SourceDigest,
            result.DecisionHistory);
        Assert.AreEqual(SuggestionCandidateFreshness.Current, decided.Freshness);
        Assert.AreEqual(SuggestionStatus.Rejected, decided.Status);
        Assert.AreSame(result.DecisionRecord, decided.Decision);
        Assert.IsFalse(decided.CanDecide);
        AssertFailure(
            () => SuggestionService.Reject(
                candidate,
                candidate.SourceDigest,
                result.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt.AddMinutes(1),
                "A second decision is forbidden."),
            SuggestionServiceFailureReason.CandidateAlreadyDecided);
    }

    [TestMethod]
    public void DecisionsRejectDuplicateIdentityInvalidMetadataAndCancellationPrivately()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate first = SuggestionService.Register(
            fixture.Editorial,
            []);
        EditorialPhraseCandidate secondPayload = CreateEditorialReplacement(
            fixture.Editorial);
        EditorialPhraseSuggestionCandidate second = SuggestionService.Register(
            secondPayload,
            [first]);
        EntityId<SuggestionDecisionIdTag> decisionId =
            EntityId<SuggestionDecisionIdTag>.New();
        SuggestionRejectionResult firstResult = SuggestionService.Reject(
            first,
            first.SourceDigest,
            [],
            decisionId,
            ActorSid,
            DecidedAt,
            "Reject the first candidate.");

        AssertFailure(
            () => SuggestionService.Reject(
                second,
                second.SourceDigest,
                firstResult.DecisionHistory,
                decisionId,
                ActorSid,
                DecidedAt.AddMinutes(1),
                "Reject the second candidate."),
            SuggestionServiceFailureReason.DuplicateDecision);

        SuggestionServiceException actor = AssertFailure(
            () => SuggestionService.Reject(
                second,
                second.SourceDigest,
                firstResult.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                "private-invalid-sid",
                DecidedAt.AddMinutes(1),
                "Reject candidate."),
            SuggestionServiceFailureReason.InvalidDecision);
        SuggestionServiceException reason = AssertFailure(
            () => SuggestionService.Reject(
                second,
                second.SourceDigest,
                firstResult.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt.AddMinutes(1),
                " private reason "),
            SuggestionServiceFailureReason.InvalidDecision);
        AssertPrivate(actor, "private-invalid-sid");
        AssertPrivate(reason, "private reason");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SuggestionService.Register(
                fixture.Emphasis,
                [first, second],
                cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SuggestionService.Reject(
                second,
                second.SourceDigest,
                firstResult.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt.AddMinutes(1),
                "Cancelled decision.",
                cancellation.Token));
        Assert.AreEqual(SuggestionStatus.Proposed, second.Status);
        Assert.HasCount(1, firstResult.DecisionHistory);
    }

    [TestMethod]
    public void ModifyCreatesFreshTypedChildAndPreservesBothProducerPayloads()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate original = SuggestionService.Register(
            fixture.Editorial,
            []);
        EditorialPhraseCandidate replacementPayload = CreateEditorialReplacement(
            fixture.Editorial);
        EditorialPhraseSuggestionCandidate replacement =
            SuggestionService.Register(replacementPayload, [original]);
        EntityId<SuggestionDecisionIdTag> decisionId =
            EntityId<SuggestionDecisionIdTag>.New();

        SuggestionModificationResult result = SuggestionService.Modify(
            original,
            replacement,
            original.SourceDigest,
            [],
            decisionId,
            ActorSid,
            DecidedAt,
            "Use the reviewed replacement interval.");

        Assert.AreSame(original, result.OriginalCandidate);
        Assert.AreSame(fixture.Editorial, original.Payload);
        Assert.AreSame(
            fixture.Editorial,
            Assert.IsInstanceOfType<EditorialPhraseSuggestionCandidate>(
                result.DecidedCandidate).Payload);
        Assert.AreSame(
            replacementPayload,
            Assert.IsInstanceOfType<EditorialPhraseSuggestionCandidate>(
                result.ReplacementCandidate).Payload);
        Assert.AreEqual(SuggestionStatus.Proposed, original.Status);
        Assert.AreEqual(SuggestionStatus.Reviewed, result.DecidedCandidate.Status);
        Assert.AreEqual(
            replacement.Id,
            result.DecidedCandidate.ReplacementSuggestionId);
        Assert.AreEqual(
            original.Id,
            result.ReplacementCandidate.ParentSuggestionId);
        Assert.AreEqual(SuggestionStatus.Proposed, result.ReplacementCandidate.Status);
        Assert.AreNotEqual(original.Id, result.ReplacementCandidate.Id);
        Assert.AreEqual(
            SuggestionDecisionKind.Modified,
            result.DecisionRecord.Decision);
        Assert.AreEqual(
            result.ReplacementCandidate.Id,
            result.DecisionRecord.ReplacementSuggestionId);
        Assert.HasCount(1, result.DecisionHistory);

        AssertFailure(
            () => SuggestionService.Reject(
                original,
                original.SourceDigest,
                result.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt.AddMinutes(1),
                "Do not decide the superseded parent."),
            SuggestionServiceFailureReason.CandidateAlreadyDecided);
        AssertFailure(
            () => SuggestionService.Modify(
                original,
                original,
                original.SourceDigest,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Identity reuse is forbidden."),
            SuggestionServiceFailureReason.ReplacementIdentityConflict);
    }

    [TestMethod]
    public void ModifyRequiresTypedContextForATermCandidateDecision()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate original = SuggestionService.Register(
            fixture.Term,
            []);
        TermCandidate replacementPayload = TermCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                [fixture.TermEntry],
                fixture.TermScope,
                fixture.ModelRunId)
            .Candidates
            .Single();
        TermSuggestionCandidate replacement = SuggestionService.Register(
            replacementPayload,
            [original]);

        AssertFailure(
            () => SuggestionService.Modify(
                original,
                replacement,
                original.SourceDigest,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "A term decision cannot bypass its dictionary snapshot."),
            SuggestionServiceFailureReason.TypedTermContextRequired);
    }

    [TestMethod]
    public void ModifyTermCreatesAChildAndDefersTranscriptChangeUntilAdoption()
    {
        ProducerFixture fixture = new();
        var replacementEntry = new TermEntry(
            EntityId<TermEntryIdTag>.New(),
            fixture.TermScope,
            "HubRadio",
            "ハブ",
            version: 0);
        TermCandidate[] payloads = TermCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                [fixture.TermEntry, replacementEntry],
                fixture.TermScope,
                fixture.ModelRunId)
            .Candidates
            .ToArray();
        TermCandidate originalPayload = payloads.Single(candidate =>
            candidate.TermEntryId == fixture.TermEntry.Id);
        TermCandidate replacementPayload = payloads.Single(candidate =>
            candidate.TermEntryId == replacementEntry.Id);
        TermSuggestionCandidate original = SuggestionService.Register(
            originalPayload,
            []);
        TermSuggestionCandidate replacement = SuggestionService.Register(
            replacementPayload,
            [original]);

        AssertFailure(
            () => SuggestionService.ModifyTerm(
                original,
                replacement,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TermEntry,
                replacementEntry,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                fixture.Revision.CreatedAt.AddTicks(-1),
                "A term decision cannot predate its source revision."),
            SuggestionServiceFailureReason.InvalidDecision);

        SuggestionModificationResult modified = SuggestionService.ModifyTerm(
            original,
            replacement,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.TermEntry,
            replacementEntry,
            [],
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Use the more specific reviewed term candidate.");

        TermSuggestionCandidate reviewedParent = Assert.IsInstanceOfType<
            TermSuggestionCandidate>(modified.DecidedCandidate);
        TermSuggestionCandidate child = Assert.IsInstanceOfType<
            TermSuggestionCandidate>(modified.ReplacementCandidate);
        Assert.AreSame(originalPayload, original.Payload);
        Assert.AreSame(originalPayload, reviewedParent.Payload);
        Assert.AreSame(replacementPayload, child.Payload);
        Assert.AreEqual(SuggestionStatus.Proposed, original.Status);
        Assert.AreEqual(SuggestionStatus.Reviewed, reviewedParent.Status);
        Assert.AreEqual(original.Id, child.ParentSuggestionId);
        Assert.AreEqual(child.Id, reviewedParent.ReplacementSuggestionId);
        Assert.AreEqual(SuggestionStatus.Proposed, child.Status);
        Assert.AreEqual(
            SuggestionDecisionKind.Modified,
            modified.DecisionRecord.Decision);
        Assert.AreEqual("ハブ", fixture.Revision.Utterances[0].Text);

        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        TermSuggestionAdoptionResult adopted = SuggestionService.AdoptTerm(
            child,
            fixture.Revision,
            fixture.SourceSpans,
            replacementEntry,
            revisionId,
            modified.DecisionHistory,
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt.AddMinutes(1),
            "Adopt the reviewed child term candidate.");

        Assert.AreEqual("ハブ", fixture.Revision.Utterances[0].Text);
        Assert.AreEqual(
            "HubRadio",
            adopted.TranscriptEdit.Revision.Utterances[0].Text);
        Assert.AreEqual(revisionId, adopted.TranscriptEdit.Revision.Id);
        Assert.AreEqual(child.Origin, adopted.DecisionRecord.Origin);
        Assert.HasCount(1, modified.DecisionHistory);
        Assert.HasCount(2, adopted.DecisionHistory);
        Assert.AreSame(modified.DecisionRecord, adopted.DecisionHistory[0]);
        Assert.AreSame(adopted.DecisionRecord, adopted.DecisionHistory[1]);
    }

    [TestMethod]
    public void TermDisplayRequiresTypedDictionaryContext()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);

        AssertFailure(
            () => SuggestionService.GetDisplayState(
                candidate,
                candidate.SourceDigest,
                []),
            SuggestionServiceFailureReason.TypedTermContextRequired);

        SuggestionCandidateDisplayState current =
            SuggestionService.GetDisplayState(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TermEntry,
                []);
        Assert.AreEqual(SuggestionCandidateFreshness.Current, current.Freshness);
        Assert.AreEqual(fixture.RevisionDigest, current.CurrentSourceDigest);
        Assert.IsTrue(current.CanDecide);

        var updatedEntry = new TermEntry(
            fixture.TermEntry.Id,
            fixture.TermScope,
            fixture.TermEntry.WrittenForm,
            fixture.TermEntry.Reading,
            fixture.TermEntry.Version + 1);
        SuggestionCandidateDisplayState stale =
            SuggestionService.GetDisplayState(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                updatedEntry,
                []);
        Assert.AreEqual(SuggestionCandidateFreshness.Stale, stale.Freshness);
        Assert.IsFalse(stale.CanDecide);

        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(
                fixture.Term.TokenId,
                fixture.Term.ObservedText,
                fixture.Term.ProposedText)],
            EntityId<TranscriptRevisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Create a newer transcript snapshot.");
        SuggestionCandidateDisplayState transcriptStale =
            SuggestionService.GetDisplayState(
                candidate,
                edit.Revision,
                edit.RevisedSourceSpans,
                fixture.TermEntry,
                []);
        Assert.AreEqual(
            SuggestionCandidateFreshness.Stale,
            transcriptStale.Freshness);
        Assert.AreEqual(edit.RevisionDigest, transcriptStale.CurrentSourceDigest);
        Assert.IsFalse(transcriptStale.CanDecide);
    }

    [TestMethod]
    public void RejectTermAppendsAUnifiedDecisionWithoutChangingTranscript()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);

        var changedEntry = new TermEntry(
            fixture.TermEntry.Id,
            fixture.TermScope,
            fixture.TermEntry.WrittenForm,
            fixture.TermEntry.Reading,
            fixture.TermEntry.Version + 1);
        AssertFailure(
            () => SuggestionService.RejectTerm(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                changedEntry,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Reject a stale term candidate."),
            SuggestionServiceFailureReason.CandidateStale);

        SuggestionRejectionResult result = SuggestionService.RejectTerm(
            candidate,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.TermEntry,
            [],
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Keep the original transcript spelling.");

        Assert.AreSame(fixture.Term, candidate.Payload);
        Assert.AreSame(
            fixture.Term,
            Assert.IsInstanceOfType<TermSuggestionCandidate>(
                result.DecidedCandidate).Payload);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        Assert.AreEqual(SuggestionStatus.Rejected, result.DecidedCandidate.Status);
        Assert.IsNotNull(result.TermDecision);
        Assert.IsNull(result.TermDecision.TranscriptEdit);
        Assert.IsNull(result.DecisionRecord.ResultingTranscriptRevisionId);
        Assert.AreEqual(candidate.Origin, result.DecisionRecord.Origin);
        Assert.AreEqual("ハブ", fixture.Revision.Utterances[0].Text);
        Assert.HasCount(1, result.DecisionHistory);
    }

    [TestMethod]
    public void DecisionHistoryRejectsAChildDecisionThatPrecedesItsParentLink()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate original = SuggestionService.Register(
            fixture.Editorial,
            []);
        EditorialPhraseSuggestionCandidate replacement = SuggestionService.Register(
            CreateEditorialReplacement(fixture.Editorial),
            [original]);
        SuggestionModificationResult modified = SuggestionService.Modify(
            original,
            replacement,
            original.SourceDigest,
            [],
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Create a replacement candidate.");
        SuggestionRejectionResult rejected = SuggestionService.Reject(
            modified.ReplacementCandidate,
            modified.ReplacementCandidate.SourceDigest,
            modified.DecisionHistory,
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt.AddMinutes(1),
            "Reject the replacement.");
        SuggestionDecisionRecord[] reversed = rejected.DecisionHistory
            .Reverse()
            .ToArray();

        AssertFailure(
            () => SuggestionService.Reject(
                modified.ReplacementCandidate,
                modified.ReplacementCandidate.SourceDigest,
                modified.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt.AddTicks(-1),
                "A child decision cannot predate its parent link."),
            SuggestionServiceFailureReason.InvalidDecision);
        AssertFailure(
            () => SuggestionService.GetDisplayState(
                modified.ReplacementCandidate,
                modified.ReplacementCandidate.SourceDigest,
                reversed),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void DecisionHistoryRejectsReplacementMetadataThatModifyCouldNotProduce()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate original = SuggestionService.Register(
            fixture.Editorial,
            []);
        EditorialPhraseSuggestionCandidate replacement = SuggestionService.Register(
            CreateEditorialReplacement(fixture.Editorial),
            [original]);
        SuggestionModificationResult modified = SuggestionService.Modify(
            original,
            replacement,
            original.SourceDigest,
            [],
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Create a valid replacement link.");
        var malformedChildRecord = new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            modified.ReplacementCandidate.Id,
            modified.ReplacementCandidate.Suggestion.ModelRunId,
            SuggestionProducerKind.Silence,
            modified.ReplacementCandidate.Kind,
            SuggestionDecisionKind.Rejected,
            SuggestionStatus.Rejected,
            modified.ReplacementCandidate.Origin,
            modified.ReplacementCandidate.SourceDigest,
            ActorSid,
            DecidedAt.AddMinutes(1),
            "The child producer conflicts with its parent link.",
            replacementSuggestionId: null,
            adoptedReference: null,
            resultingTranscriptRevisionId: null);

        AssertFailure(
            () => SuggestionService.GetDisplayState(
                original,
                original.SourceDigest,
                [modified.DecisionRecord, malformedChildRecord]),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);

        var malformedParentRecord = new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            original.Id,
            original.Suggestion.ModelRunId,
            SuggestionProducerKind.Silence,
            original.Kind,
            SuggestionDecisionKind.Modified,
            SuggestionStatus.Reviewed,
            original.Origin,
            original.SourceDigest,
            ActorSid,
            DecidedAt,
            "The parent producer conflicts with its replacement candidate.",
            modified.ReplacementCandidate.Id,
            adoptedReference: null,
            resultingTranscriptRevisionId: null);
        AssertFailure(
            () => SuggestionService.GetDisplayState(
                modified.ReplacementCandidate,
                modified.ReplacementCandidate.SourceDigest,
                [malformedParentRecord]),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void DecisionRecordRejectsProducerAndSuggestionKindMismatch()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Editorial,
            []);

        AssertFailure(
            () => _ = new SuggestionDecisionRecord(
                EntityId<SuggestionDecisionIdTag>.New(),
                candidate.Id,
                candidate.Suggestion.ModelRunId,
                SuggestionProducerKind.Emphasis,
                SuggestionKind.DeleteRange,
                SuggestionDecisionKind.Rejected,
                SuggestionStatus.Rejected,
                candidate.Origin,
                candidate.SourceDigest,
                ActorSid,
                DecidedAt,
                "An emphasis producer cannot record a delete-range decision.",
                replacementSuggestionId: null,
                adoptedReference: null,
                resultingTranscriptRevisionId: null),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void DecisionRecordRejectsMalformedTermModification()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);

        AssertFailure(
            () => _ = new SuggestionDecisionRecord(
                EntityId<SuggestionDecisionIdTag>.New(),
                candidate.Id,
                candidate.Suggestion.ModelRunId,
                candidate.Producer,
                candidate.Kind,
                SuggestionDecisionKind.Modified,
                SuggestionStatus.Adopted,
                candidate.Origin,
                candidate.SourceDigest,
                ActorSid,
                DecidedAt,
                "A modification must leave its parent reviewed.",
                EntityId<SuggestionIdTag>.New(),
                adoptedReference: null,
                resultingTranscriptRevisionId: null),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void DecisionHistoryRejectsTermResultThatReusesItsSourceRevisionIdentity()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);
        var impossibleRecord = new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            candidate.Id,
            candidate.Suggestion.ModelRunId,
            candidate.Producer,
            candidate.Kind,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            candidate.Origin,
            candidate.SourceDigest,
            ActorSid,
            DecidedAt,
            "A child revision cannot reuse its source identity.",
            replacementSuggestionId: null,
            adoptedReference: null,
            fixture.Revision.Id);

        AssertFailure(
            () => SuggestionService.GetDisplayState(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TermEntry,
                [impossibleRecord]),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void DecisionHistoryRejectsATermDecisionThatPredatesItsSourceRevision()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);
        var impossibleRecord = new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            candidate.Id,
            candidate.Suggestion.ModelRunId,
            candidate.Producer,
            candidate.Kind,
            SuggestionDecisionKind.Rejected,
            SuggestionStatus.Rejected,
            candidate.Origin,
            candidate.SourceDigest,
            ActorSid,
            fixture.Revision.CreatedAt.AddTicks(-1),
            "A decision cannot predate its source revision.",
            replacementSuggestionId: null,
            adoptedReference: null,
            resultingTranscriptRevisionId: null);

        AssertFailure(
            () => SuggestionService.GetDisplayState(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TermEntry,
                [impossibleRecord]),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void DecisionHistoryRejectsTermResultThatReusesSourceParentIdentity()
    {
        ProducerFixture fixture = new();
        Token otherToken = fixture.Revision.Utterances[1].Tokens.Single();
        TranscriptEditResult sourceEdit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(
                otherToken.Id,
                otherToken.Text,
                "この区間を維持")],
            EntityId<TranscriptRevisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Create a human source revision with a parent.");
        TermCandidate payload = TermCandidateService.FindCandidates(
                sourceEdit.Revision,
                sourceEdit.RevisedSourceSpans,
                [fixture.TermEntry],
                fixture.TermScope,
                fixture.ModelRunId)
            .Candidates
            .Single();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            payload,
            []);
        var impossibleRecord = new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            candidate.Id,
            candidate.Suggestion.ModelRunId,
            candidate.Producer,
            candidate.Kind,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            candidate.Origin,
            candidate.SourceDigest,
            ActorSid,
            DecidedAt.AddMinutes(1),
            "A child result cannot reuse the source parent identity.",
            replacementSuggestionId: null,
            adoptedReference: null,
            sourceEdit.Revision.ParentRevisionId);

        AssertFailure(
            () => SuggestionService.GetDisplayState(
                candidate,
                sourceEdit.Revision,
                sourceEdit.RevisedSourceSpans,
                fixture.TermEntry,
                [impossibleRecord]),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void AdoptTermCreatesHumanChildAndKeepsTheSourceRevisionImmutable()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.New();

        AssertFailure(
            () => SuggestionService.Reject(
                candidate,
                candidate.SourceDigest,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Typed term context is required."),
            SuggestionServiceFailureReason.TypedTermContextRequired);

        TermSuggestionAdoptionResult result = SuggestionService.AdoptTerm(
            candidate,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.TermEntry,
            revisionId,
            [],
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Adopt the dictionary spelling.");

        Assert.AreEqual("ハブ", fixture.Revision.Utterances[0].Text);
        Assert.AreEqual("Hub", result.TranscriptEdit.Revision.Utterances[0].Text);
        Assert.AreEqual(revisionId, result.TranscriptEdit.Revision.Id);
        Assert.AreEqual(
            fixture.Revision.Id,
            result.TranscriptEdit.Revision.ParentRevisionId);
        Assert.AreEqual(
            TranscriptChangeSource.Human,
            result.TranscriptEdit.Revision.ChangeSource);
        Assert.AreSame(fixture.Term, candidate.Payload);
        Assert.AreSame(
            fixture.Term,
            Assert.IsInstanceOfType<TermSuggestionCandidate>(
                result.DecidedCandidate).Payload);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        Assert.AreEqual(SuggestionStatus.Adopted, result.DecidedCandidate.Status);
        Assert.AreEqual(
            SuggestionDecisionKind.Adopted,
            result.DecisionRecord.Decision);
        Assert.IsNull(result.DecisionRecord.AdoptedReference);
        Assert.AreEqual(
            revisionId,
            result.DecisionRecord.ResultingTranscriptRevisionId);
        Assert.AreSame(result.TermDecision.TranscriptEdit, result.TranscriptEdit);
    }

    [TestMethod]
    public void AdoptTermMapsRevisionIdentityConflictToTheUnifiedFailureContract()
    {
        ProducerFixture fixture = new();
        TermSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Term,
            []);

        AssertFailure(
            () => SuggestionService.AdoptTerm(
                candidate,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TermEntry,
                fixture.Revision.Id,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "A child revision cannot reuse its source identity."),
            SuggestionServiceFailureReason.InvalidDecision);
    }

    [TestMethod]
    public void AdoptTermRejectsAResultRevisionIdentityAlreadyUsedByHistory()
    {
        ProducerFixture fixture = new();
        var alternativeEntry = new TermEntry(
            EntityId<TermEntryIdTag>.New(),
            fixture.TermScope,
            "HubRadio",
            "ハブ",
            version: 0);
        TermCandidate[] payloads = TermCandidateService.FindCandidates(
                fixture.Revision,
                fixture.SourceSpans,
                [fixture.TermEntry, alternativeEntry],
                fixture.TermScope,
                fixture.ModelRunId)
            .Candidates
            .ToArray();
        Assert.HasCount(2, payloads);
        TermCandidate firstPayload = payloads.Single(candidate =>
            candidate.TermEntryId == fixture.TermEntry.Id);
        TermCandidate secondPayload = payloads.Single(candidate =>
            candidate.TermEntryId == alternativeEntry.Id);
        TermSuggestionCandidate first = SuggestionService.Register(
            firstPayload,
            []);
        TermSuggestionCandidate second = SuggestionService.Register(
            secondPayload,
            [first]);
        EntityId<TranscriptRevisionIdTag> reusedRevisionId =
            EntityId<TranscriptRevisionIdTag>.New();
        SuggestionDecisionResult firstResult = SuggestionService.AdoptTerm(
            first,
            fixture.Revision,
            fixture.SourceSpans,
            fixture.TermEntry,
            reusedRevisionId,
            [],
            EntityId<SuggestionDecisionIdTag>.New(),
            ActorSid,
            DecidedAt,
            "Adopt the first dictionary spelling.");

        AssertFailure(
            () => SuggestionService.AdoptTerm(
                second,
                fixture.Revision,
                fixture.SourceSpans,
                alternativeEntry,
                reusedRevisionId,
                firstResult.DecisionHistory,
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt.AddMinutes(1),
                "Do not reuse another adoption's revision identity."),
            SuggestionServiceFailureReason.InvalidDecision);

        var conflictingRecord = new SuggestionDecisionRecord(
            EntityId<SuggestionDecisionIdTag>.New(),
            second.Id,
            second.Suggestion.ModelRunId,
            second.Producer,
            second.Kind,
            SuggestionDecisionKind.Adopted,
            SuggestionStatus.Adopted,
            second.Origin,
            second.SourceDigest,
            ActorSid,
            DecidedAt.AddMinutes(1),
            "This restored record conflicts with an existing result identity.",
            replacementSuggestionId: null,
            adoptedReference: null,
            reusedRevisionId);
        AssertFailure(
            () => SuggestionService.GetDisplayState(
                first,
                fixture.Revision,
                fixture.SourceSpans,
                fixture.TermEntry,
                [firstResult.DecisionRecord, conflictingRecord]),
            SuggestionServiceFailureReason.DecisionHistoryInvalid);
    }

    [TestMethod]
    public void AdoptDeleteRangeReturnsTypedDeleteAndExactReferenceForEveryProducer()
    {
        ProducerFixture fixture = new();
        DeleteRangeSuggestionCandidate[] candidates =
        [
            SuggestionService.Register(fixture.Silence, []),
            SuggestionService.Register(fixture.Editorial, []),
            SuggestionService.Register(
                fixture.Repetition.Payload,
                fixture.Repetition.Suggestion,
                []),
            SuggestionService.Register(fixture.AudioReview, []),
            SuggestionService.Register(fixture.ContextDeviation, []),
        ];

        foreach (DeleteRangeSuggestionCandidate candidate in candidates)
        {
            EntityId<CompositionElementIdTag> elementId =
                EntityId<CompositionElementIdTag>.New();
            EditSuggestionAdoptionResult result =
                SuggestionService.AdoptDeleteRange(
                    candidate,
                    candidate.SourceDigest,
                    elementId,
                    [],
                    EntityId<SuggestionDecisionIdTag>.New(),
                    ActorSid,
                    DecidedAt,
                    "Adopt the reviewed deletion.");

            Assert.AreSame(candidate, result.OriginalCandidate);
            Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
            Assert.AreEqual(SuggestionStatus.Adopted, result.DecidedCandidate.Status);
            Assert.AreEqual(EditOperationKind.Delete, result.Operation.Kind);
            Assert.AreEqual(elementId, result.Operation.ElementId);
            Assert.IsNull(result.Operation.SplitOffset);
            Assert.IsNull(result.Operation.RightElementId);
            Assert.IsNull(result.Operation.TargetIndex);
            Assert.AreEqual(candidate.Id, result.Reference.SuggestionId);
            Assert.AreEqual(candidate.Origin, result.Reference.Origin);
            Assert.AreEqual(candidate.Origin, result.Origin);
            Assert.AreSame(
                result.Reference,
                result.DecisionRecord.AdoptedReference);
            Assert.AreEqual(
                SuggestionDecisionKind.Adopted,
                result.DecisionRecord.Decision);
            Assert.HasCount(1, result.DecisionHistory);
            AssertPayloadPreserved(candidate, result.DecidedCandidate);
        }
    }

    [TestMethod]
    public void AdoptTopicBoundaryReturnsTypedSplitAndExactReference()
    {
        ProducerFixture fixture = new();
        TopicBoundarySuggestionCandidate candidate = SuggestionService.Register(
            fixture.TopicBoundary,
            []);
        EntityId<CompositionElementIdTag> elementId =
            EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> rightElementId =
            EntityId<CompositionElementIdTag>.New();
        var splitOffset = new RationalTime(1, 2);

        EditSuggestionAdoptionResult result =
            SuggestionService.AdoptTopicBoundary(
                candidate,
                candidate.SourceDigest,
                elementId,
                splitOffset,
                rightElementId,
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Adopt the reviewed topic boundary.");

        Assert.AreSame(candidate, result.OriginalCandidate);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        Assert.AreEqual(SuggestionStatus.Adopted, result.DecidedCandidate.Status);
        Assert.AreEqual(EditOperationKind.Split, result.Operation.Kind);
        Assert.AreEqual(elementId, result.Operation.ElementId);
        Assert.AreEqual(splitOffset, result.Operation.SplitOffset);
        Assert.AreEqual(rightElementId, result.Operation.RightElementId);
        Assert.IsNull(result.Operation.TargetIndex);
        Assert.AreEqual(candidate.Id, result.Reference.SuggestionId);
        Assert.AreEqual(candidate.Origin, result.Reference.Origin);
        Assert.AreSame(
            fixture.TopicBoundary,
            Assert.IsInstanceOfType<TopicBoundarySuggestionCandidate>(
                result.DecidedCandidate).Payload);
    }

    [TestMethod]
    public void AdoptHighlightReturnsGraphicCueAndPreservesExactTextEvidence()
    {
        ProducerFixture fixture = new();
        EmphasisSuggestionCandidate candidate = SuggestionService.Register(
            fixture.Emphasis,
            []);
        EntityId<HubRadio.Core.Layout.CueIdTag> cueId =
            EntityId<HubRadio.Core.Layout.CueIdTag>.New();
        TimeRange outputRange = Range(10, 2);

        HighlightSuggestionAdoptionResult result =
            SuggestionService.AdoptHighlight(
                candidate,
                candidate.SourceDigest,
                cueId,
                outputRange,
                "graphic-main",
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Adopt the reviewed emphasis cue.");

        Assert.AreSame(candidate, result.OriginalCandidate);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        Assert.AreEqual(SuggestionStatus.Adopted, result.DecidedCandidate.Status);
        Assert.AreEqual(cueId, result.Cue.Id);
        Assert.AreEqual(HubRadio.Core.Layout.CueKind.Graphic, result.Cue.Kind);
        Assert.AreEqual(outputRange, result.Cue.Range);
        Assert.AreEqual("graphic-main", result.Cue.RegionName);
        Assert.AreEqual(fixture.Emphasis.Text, result.Text);
        Assert.AreEqual(fixture.Emphasis.TextSource, result.TextSource);
        Assert.AreEqual(candidate.Id, result.Reference.SuggestionId);
        Assert.AreEqual(candidate.Origin, result.Reference.Origin);
        Assert.AreSame(
            result.Reference,
            result.DecisionRecord.AdoptedReference);
        Assert.AreSame(
            fixture.Emphasis,
            Assert.IsInstanceOfType<EmphasisSuggestionCandidate>(
                result.DecidedCandidate).Payload);
    }

    [TestMethod]
    public void TypedAdoptionsRejectStaleDigestsWithoutChangingCandidates()
    {
        ProducerFixture fixture = new();
        EditorialPhraseSuggestionCandidate deletion = SuggestionService.Register(
            fixture.Editorial,
            []);
        TopicBoundarySuggestionCandidate topic = SuggestionService.Register(
            fixture.TopicBoundary,
            [deletion]);
        EmphasisSuggestionCandidate highlight = SuggestionService.Register(
            fixture.Emphasis,
            [deletion, topic]);
        var staleDigest = new ContentDigest(new string('f', 64));

        AssertFailure(
            () => SuggestionService.AdoptDeleteRange(
                deletion,
                staleDigest,
                EntityId<CompositionElementIdTag>.New(),
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Do not adopt stale deletion evidence."),
            SuggestionServiceFailureReason.CandidateStale);
        AssertFailure(
            () => SuggestionService.AdoptTopicBoundary(
                topic,
                staleDigest,
                EntityId<CompositionElementIdTag>.New(),
                new RationalTime(1, 2),
                EntityId<CompositionElementIdTag>.New(),
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Do not adopt a stale topic boundary."),
            SuggestionServiceFailureReason.CandidateStale);
        AssertFailure(
            () => SuggestionService.AdoptHighlight(
                highlight,
                staleDigest,
                EntityId<HubRadio.Core.Layout.CueIdTag>.New(),
                Range(10, 2),
                "graphic-main",
                [],
                EntityId<SuggestionDecisionIdTag>.New(),
                ActorSid,
                DecidedAt,
                "Do not adopt stale highlight evidence."),
            SuggestionServiceFailureReason.CandidateStale);

        Assert.AreEqual(SuggestionStatus.Proposed, deletion.Status);
        Assert.AreEqual(SuggestionStatus.Proposed, topic.Status);
        Assert.AreEqual(SuggestionStatus.Proposed, highlight.Status);
    }

    [TestMethod]
    public void PublicLifecycleSurfaceIsImmutable()
    {
        foreach (Type type in new[]
        {
            typeof(TermSuggestionCandidate),
            typeof(SilenceSuggestionCandidate),
            typeof(EditorialPhraseSuggestionCandidate),
            typeof(RepetitionSuggestionCandidate),
            typeof(TopicBoundarySuggestionCandidate),
            typeof(AudioReviewSuggestionCandidate),
            typeof(ContextDeviationSuggestionCandidate),
            typeof(EmphasisSuggestionCandidate),
            typeof(SuggestionDecisionRecord),
            typeof(SuggestionCandidateDisplayState),
            typeof(SuggestionRejectionResult),
            typeof(SuggestionModificationResult),
            typeof(TermSuggestionAdoptionResult),
            typeof(EditSuggestionAdoptionResult),
            typeof(HighlightSuggestionAdoptionResult),
            typeof(SuggestionServiceException),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        }

        foreach (Type type in new[]
        {
            typeof(RepetitionReasonFeatures),
            typeof(RepetitionSuggestionPayload),
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

        Assert.IsTrue(typeof(SuggestionCandidate).IsAbstract);
        Assert.IsTrue(typeof(SuggestionDecisionResult).IsAbstract);
        Assert.IsTrue(typeof(SuggestionAdoptionResult).IsAbstract);
        Assert.IsTrue(typeof(SuggestionService).IsAbstract);
        Assert.IsTrue(typeof(SuggestionService).IsSealed);
    }

    private static void AssertCommon(
        SuggestionCandidate candidate,
        Suggestion producerSuggestion,
        ContentDigest sourceDigest,
        SuggestionProducerKind producer)
    {
        Assert.AreSame(producerSuggestion, candidate.Suggestion);
        Assert.AreEqual(producerSuggestion.Id, candidate.Id);
        Assert.AreEqual(producerSuggestion.Kind, candidate.Kind);
        Assert.AreEqual(SuggestionStatus.Proposed, candidate.Status);
        Assert.AreEqual(producerSuggestion.Origin, candidate.Origin);
        Assert.AreEqual(sourceDigest, candidate.SourceDigest);
        Assert.AreEqual(producer, candidate.Producer);
        Assert.IsNull(candidate.ParentSuggestionId);
        Assert.IsNull(candidate.ReplacementSuggestionId);
    }

    private static void AssertPayloadPreserved(
        DeleteRangeSuggestionCandidate original,
        SuggestionCandidate decided)
    {
        switch (original)
        {
            case SilenceSuggestionCandidate silence:
                Assert.AreSame(
                    silence.Payload,
                    Assert.IsInstanceOfType<SilenceSuggestionCandidate>(
                        decided).Payload);
                break;
            case EditorialPhraseSuggestionCandidate editorial:
                Assert.AreSame(
                    editorial.Payload,
                    Assert.IsInstanceOfType<EditorialPhraseSuggestionCandidate>(
                        decided).Payload);
                break;
            case RepetitionSuggestionCandidate repetition:
                Assert.AreSame(
                    repetition.Payload,
                    Assert.IsInstanceOfType<RepetitionSuggestionCandidate>(
                        decided).Payload);
                break;
            case AudioReviewSuggestionCandidate audio:
                Assert.AreSame(
                    audio.Payload,
                    Assert.IsInstanceOfType<AudioReviewSuggestionCandidate>(
                        decided).Payload);
                break;
            case ContextDeviationSuggestionCandidate context:
                Assert.AreSame(
                    context.Payload,
                    Assert.IsInstanceOfType<ContextDeviationSuggestionCandidate>(
                        decided).Payload);
                break;
            default:
                Assert.Fail($"Unexpected delete-range candidate: {original.GetType().Name}");
                break;
        }
    }

    private static SuggestionServiceException AssertFailure(
        Action action,
        SuggestionServiceFailureReason reason)
    {
        SuggestionServiceException exception =
            Assert.ThrowsExactly<SuggestionServiceException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static void AssertPrivate(Exception exception, string secret)
    {
        Assert.IsFalse(exception.ToString().Contains(secret, StringComparison.Ordinal));
    }

    private static EditorialPhraseCandidate CreateEditorialReplacement(
        EditorialPhraseCandidate source)
    {
        var sourceSpan = new SourceSpan(
            EntityId<SourceSpanIdTag>.New(),
            source.SourceSpan.AssetId,
            source.SourceSpan.StreamId,
            source.SourceSpan.Range,
            source.SourceSpan.SourceDigest,
            source.SourceRevisionId,
            source.SourceSpan.TranscriptTokenIds);
        OriginRef origin = OriginRef.FromSourceSpan(sourceSpan.Id);
        var detection = new Detection(
            EntityId<DetectionIdTag>.New(),
            source.Detection.ModelRunId,
            DetectionKind.EditorialInstruction,
            origin,
            "A reviewed replacement interval preserves editorial evidence.",
            confidence: null);
        var suggestion = new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            source.Suggestion.ModelRunId,
            SuggestionKind.DeleteRange,
            SuggestionStatus.Proposed,
            origin,
            "The replacement remains an explicit editorial phrase candidate.",
            confidence: null,
            missingContext: null,
            "Review the replacement interval before adoption.");
        return new EditorialPhraseCandidate(
            detection,
            suggestion,
            sourceSpan,
            source.SourceRevisionId,
            source.SourceRevisionDigest,
            source.DictionaryVersion,
            source.MatchedPhrase,
            source.NormalizedSentenceStart,
            source.NormalizedMatchLength,
            source.UsesUtteranceTimingFallback,
            source.UsesPartialTokenCoverage);
    }

    private static RepetitionInput CreateRepetitionInput(
        RepetitionEvidence? evidence = null,
        EntityId<SuggestionIdTag>? suggestionId = null)
    {
        RepetitionEvidence actual = evidence ?? new RepetitionEvidence(
            EntityId<ModelRunIdTag>.New(),
            EntityId<TranscriptRevisionIdTag>.New(),
            new ContentDigest(new string('d', 64)),
            EntityId<SourceSpanIdTag>.New(),
            [EntityId<TranscriptTokenIdTag>.New()],
            EntityId<SourceSpanIdTag>.New(),
            [EntityId<TranscriptTokenIdTag>.New()]);
        var features = new RepetitionReasonFeatures(
            RepetitionReasonFeatures.AdjacentExactUtteranceRule,
            actual.ComparisonSourceSpanId,
            actual.ComparisonTokenIds,
            matchingTokenCount: actual.EvidenceTokenIds.Count,
            exactNfkcMatch: true,
            structurallyAdjacent: true,
            sameSpeakerRequired: true,
            candidateIsLaterOccurrence: true,
            retentionChoiceRequired: true);
        ContentDigest candidateKey = RepetitionSuggestionPayload.ComputeCandidateKey(
            actual.SourceRevisionId,
            actual.SourceRevisionDigest,
            features.Rule,
            actual.SourceSpanId,
            actual.EvidenceTokenIds,
            actual.ComparisonSourceSpanId,
            actual.ComparisonTokenIds);
        const string reason =
            "Adjacent utterances have the same exact normalized token sequence.";
        var payload = new RepetitionSuggestionPayload(
            candidateKey,
            actual.ModelRunId,
            actual.SourceRevisionId,
            actual.SourceRevisionDigest,
            actual.SourceSpanId,
            actual.EvidenceTokenIds,
            RepetitionSuggestionPayload.CanonicalRuleVersion,
            SuggestionKind.DeleteRange,
            SuggestionStatus.Proposed,
            reason,
            confidence: null,
            automaticDeletionAllowed: false,
            humanReviewRequired: true,
            features);
        var suggestion = new Suggestion(
            suggestionId ?? EntityId<SuggestionIdTag>.New(),
            actual.ModelRunId,
            SuggestionKind.DeleteRange,
            SuggestionStatus.Proposed,
            OriginRef.FromSourceSpan(actual.SourceSpanId),
            reason,
            confidence: null,
            missingContext: null,
            warning: null);
        return new RepetitionInput(payload, suggestion, actual);
    }

    private static AnalysisAudioTimeline CreateAudioTimeline() =>
        new(
            EntityId<ProjectIdTag>.New(),
            EntityId<JobIdTag>.New(),
            EntityId<AssetIdTag>.New(),
            new ContentDigest(new string('b', 64)),
            sourceByteSize: 1000,
            EntityId<ArtifactIdTag>.New(),
            new ContentDigest(new string('c', 64)),
            sourceVideoStreamIndex: 0,
            sourceAudioStreamIndex: 1,
            new RationalTime(1, 48000),
            probeReportedAudioStart: null,
            firstDecodedAudioFrameTimestamp: 0,
            firstDecodedAudioFrameSampleCount: 1024,
            EntityId<ArtifactIdTag>.New(),
            new ContentDigest(new string('e', 64)),
            analysisAudioByteSize: 128044,
            analysisSampleCount: 64000,
            EntityId<ArtifactIdTag>.New());

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private sealed record RepetitionEvidence(
        EntityId<ModelRunIdTag> ModelRunId,
        EntityId<TranscriptRevisionIdTag> SourceRevisionId,
        ContentDigest SourceRevisionDigest,
        EntityId<SourceSpanIdTag> SourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> EvidenceTokenIds,
        EntityId<SourceSpanIdTag> ComparisonSourceSpanId,
        IReadOnlyList<EntityId<TranscriptTokenIdTag>> ComparisonTokenIds);

    private sealed record RepetitionInput(
        RepetitionSuggestionPayload Payload,
        Suggestion Suggestion,
        RepetitionEvidence Evidence);

    private sealed class ProducerFixture
    {
        internal ProducerFixture()
        {
            ModelRunId = EntityId<ModelRunIdTag>.New();
            EntityId<TranscriptRevisionIdTag> revisionId =
                EntityId<TranscriptRevisionIdTag>.New();
            var sourceDigest = new ContentDigest(new string('a', 64));
            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            var firstToken = new Token(
                EntityId<TranscriptTokenIdTag>.New(),
                "ハブ",
                Range(0, 1),
                recognitionConfidence: 0.8);
            var secondToken = new Token(
                EntityId<TranscriptTokenIdTag>.New(),
                "ここはカット",
                Range(2, 1),
                recognitionConfidence: 0.8);
            EntityId<SourceSpanIdTag> firstSpanId =
                EntityId<SourceSpanIdTag>.New();
            EntityId<SourceSpanIdTag> secondSpanId =
                EntityId<SourceSpanIdTag>.New();
            var first = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                firstSpanId,
                EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker 1",
                "ハブ",
                Range(0, 1),
                recognitionConfidence: 0.8,
                [new Sentence(
                    EntityId<TranscriptSentenceIdTag>.New(),
                    "ハブ",
                    [firstToken])]);
            var second = new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                secondSpanId,
                EntityId<TranscriptSpeakerIdTag>.New(),
                "Speaker 2",
                "ここはカット",
                Range(2, 1),
                recognitionConfidence: 0.8,
                [new Sentence(
                    EntityId<TranscriptSentenceIdTag>.New(),
                    "ここはカット",
                    [secondToken])]);
            Revision = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [first, second]);
            SourceSpans =
            [
                new SourceSpan(
                    firstSpanId,
                    assetId,
                    "audio:1",
                    first.Range,
                    sourceDigest,
                    revisionId,
                    [firstToken.Id]),
                new SourceSpan(
                    secondSpanId,
                    assetId,
                    "audio:1",
                    second.Range,
                    sourceDigest,
                    revisionId,
                    [secondToken.Id]),
            ];
            RevisionDigest = ContentDigest.Compute(Encoding.UTF8.GetBytes(
                TranscriptSnapshotJson.WriteRevision(Revision)));
            TermScope = TermScope.ForProject(EntityId<ProjectIdTag>.New());
            TermEntry = new TermEntry(
                EntityId<TermEntryIdTag>.New(),
                TermScope,
                "Hub",
                "ハブ",
                version: 0);
            Term = TermCandidateService.FindCandidates(
                    Revision,
                    SourceSpans,
                    [TermEntry],
                    TermScope,
                    ModelRunId)
                .Candidates
                .Single();
            Editorial = EditorialPhraseDetector.FindCandidates(
                    Revision,
                    SourceSpans,
                    ModelRunId)
                .Candidates
                .Single();
            TopicBoundaryScanResult boundaryScan =
                TopicBoundaryService.FindCandidates(
                    Revision,
                    SourceSpans,
                    ModelRunId);
            TopicBoundary = boundaryScan.Candidates.Single();
            TopicRangeRevision topicRange = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                EntityId<TopicRangeIdTag>.New(),
                EntityId<TopicRangeRevisionIdTag>.New(),
                startBoundarySuggestionId: null,
                endBoundarySuggestionId: null,
                Range(2, 1),
                ActorSid,
                CreatedAt,
                "Define the explicit topic interval.");
            ContextDeviation = ContextDeviationDetector.FindCandidates(
                    Revision,
                    SourceSpans,
                    [topicRange],
                    ModelRunId)
                .Candidates
                .Single();

            SourceSpan emphasisSpan = SourceSpans[0];
            OriginRef emphasisOrigin = OriginRef.FromSourceSpan(emphasisSpan.Id);
            Emphasis = new EmphasisCandidate(
                new Suggestion(
                    EntityId<SuggestionIdTag>.New(),
                    ModelRunId,
                    SuggestionKind.Highlight,
                    SuggestionStatus.Proposed,
                    emphasisOrigin,
                    "Explicit emphasis evidence: manual important statement.",
                    confidence: null,
                    "The rule does not establish display priority.",
                    "Review wording and layout before adoption."),
                emphasisSpan,
                Revision.Id,
                RevisionDigest,
                EntityId<TopicMetadataRevisionIdTag>.New(),
                EmphasisCandidateService.RuleVersion,
                "ハブ",
                EmphasisTextSourceKind.OriginalTranscript,
                [EmphasisReasonKind.ImportantStatement],
                termEvidence: []);

            AnalysisAudioTimeline timeline = CreateAudioTimeline();
            Silence = SilenceDetector.ParseProcessResult(
                    timeline,
                    ModelRunId,
                    new ProcessRunResult(
                        0,
                        string.Empty,
                        "[silencedetect @ a] silence_start: 1\n" +
                            "[silencedetect @ a] silence_end: 3 | silence_duration: 2\n",
                        Truncated: false,
                        TimeSpan.FromMilliseconds(1)))
                .Candidates
                .Single();
            AudioReview = AudioReviewCandidateDetector.CreateCandidates(
                    timeline,
                    ModelRunId,
                    [new AudioReviewObservation(
                        AudioReviewEventKind.Cough,
                        AudioReviewEvidenceMethod.ManualListening,
                        analysisStartSample: 100,
                        analysisEndSample: 200)])
                .Candidates
                .Single();
            Repetition = CreateRepetitionInput();
        }

        internal EntityId<ModelRunIdTag> ModelRunId { get; }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal ContentDigest RevisionDigest { get; }

        internal TermScope TermScope { get; }

        internal TermEntry TermEntry { get; }

        internal TermCandidate Term { get; }

        internal SilenceCandidate Silence { get; }

        internal EditorialPhraseCandidate Editorial { get; }

        internal RepetitionInput Repetition { get; }

        internal TopicBoundaryCandidate TopicBoundary { get; }

        internal AudioReviewCandidate AudioReview { get; }

        internal ContextDeviationCandidate ContextDeviation { get; }

        internal EmphasisCandidate Emphasis { get; }
    }
}