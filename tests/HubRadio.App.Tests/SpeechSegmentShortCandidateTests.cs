using HubRadio.App.Analysis;
using HubRadio.App.Projects;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

// FR-010 AC-002 (E2E-013) / FR-132 AC-136, AC-137 (E2E-006/007): a long speech recording
// without numbers, product terms or manual emphasis metadata (the synthetic 27.9 s English
// speech used by the E2E helper, 12 sentences) must still yield a grounded 15-60 s short
// candidate that lands exactly on source frame PTS values. Before this rule the only
// automatic candidates were numeric tokens and answers after "?", so shorts were never made.
[TestClass]
public sealed class SpeechSegmentShortCandidateTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset CreatedAt = new(2042, 5, 6, 7, 8, 9, TimeSpan.Zero);

    // FR-010 AC-002: 12 English sentences of ~2.2 s with ~0.125 s gaps (27.9 s in total).
    [TestMethod]
    public void TwentyEightSecondEnglishSpeechYieldsOneFifteenToSixtySecondCandidate()
    {
        Scene scene = Scene.Create(Enumerable.Range(0, 12).Select(i => (Start: i * 2325, Length: 2200)).ToArray());

        EmphasisCandidate[] speech = scene.Speech();

        Assert.HasCount(1, speech);
        TimeRange range = speech[0].SourceSpan.Range;
        Assert.IsTrue(range.Duration >= new RationalTime(15, 1) && range.Duration <= new RationalTime(60, 1));
        Assert.AreEqual(1, speech[0].Reasons.Count);
        Assert.AreEqual(EmphasisTextSourceKind.OriginalTranscript, speech[0].TextSource);
        Assert.IsFalse(speech[0].AutomaticAdoptionAllowed);
        Assert.AreEqual(12 * 4, speech[0].SourceSpan.TranscriptTokenIds.Count);
    }

    // FR-010 AC-002: the 3 s material cannot satisfy the 15 s minimum, so there is no candidate.
    [TestMethod]
    public void ThreeSecondSpeechYieldsNoCandidate()
    {
        Scene scene = Scene.Create([(0, 1400), (1500, 1400)]);

        Assert.IsEmpty(scene.Speech());
    }

    // FR-010 AC-002: a pause over 3 s separates segments; each side is judged separately.
    [TestMethod]
    public void LongPauseSplitsSegmentsAndEachSideNeedsFifteenSeconds()
    {
        (int, int)[] shortBlocks = [.. Enumerable.Range(0, 6).Select(i => (i * 2325, 2200)),
            .. Enumerable.Range(0, 6).Select(i => (30000 + i * 2325, 2200))];
        Assert.IsEmpty(Scene.Create(shortBlocks).Speech());

        (int, int)[] longBlocks = [.. Enumerable.Range(0, 8).Select(i => (i * 2325, 2200)),
            .. Enumerable.Range(0, 8).Select(i => (30000 + i * 2325, 2200))];
        Assert.HasCount(2, Scene.Create(longBlocks).Speech());
    }

    // FR-010 AC-002: recognizer times are not on the frame grid; the candidate is aligned to
    // source frame PTS on the audio sample grid so the pipeline's exact-range check passes.
    [TestMethod]
    public void SnapAlignsCandidateToFramePtsInsideTopicRangeAndKeepsDurationPolicy()
    {
        Scene scene = Scene.Create(Enumerable.Range(0, 12).Select(i => (Start: i * 2325 + 7, Length: 2200)).ToArray());
        EmphasisSuggestionCandidate[] registered = scene.Register();

        EmphasisSuggestionCandidate[] snapped = SourceOnlyPipeline.SnapSpeechSegments(
            "seed", registered, [scene.Metadata], scene.FrameIndex(), 48000);

        Assert.HasCount(1, snapped);
        TimeRange range = snapped[0].Payload.SourceSpan.Range;
        Assert.IsTrue(scene.FrameIndex().Entries.Any(frame => frame.PresentationTime == range.Start));
        Assert.IsTrue(scene.FrameIndex().Entries.Any(frame => frame.PresentationTime == range.End));
        Assert.IsTrue(range.Start >= scene.Metadata.TopicRange.Start && range.End <= scene.Metadata.TopicRange.End);
        Assert.IsTrue(range.Duration >= new RationalTime(15, 1) && range.Duration <= new RationalTime(60, 1));
        Assert.AreEqual(registered[0].Payload.SourceSpan.TranscriptTokenIds.Count, snapped[0].Payload.SourceSpan.TranscriptTokenIds.Count);
        Assert.AreEqual(snapped[0].Payload.SourceSpan.Id, snapped[0].Origin.SourceSpanId);
    }

    private sealed class Scene
    {
        private Scene(TranscriptRevision revision, IReadOnlyList<SourceSpan> spans, TopicMetadataRevision metadata, EntityId<ModelRunIdTag> run)
        {
            Revision = revision;
            Spans = spans;
            Metadata = metadata;
            Run = run;
        }

        internal TranscriptRevision Revision { get; }
        internal IReadOnlyList<SourceSpan> Spans { get; }
        internal TopicMetadataRevision Metadata { get; }
        internal EntityId<ModelRunIdTag> Run { get; }

        internal EmphasisCandidate[] Speech() => EmphasisCandidateService.FindCandidates(Revision, Spans, Metadata, [],
            TermScope.ForProject(Metadata.ProjectId), [], Run).Candidates
            .Where(item => item.Reasons.Contains(EmphasisReasonKind.SpeechSegment)).ToArray();

        internal EmphasisSuggestionCandidate[] Register() => Speech()
            .Select(item => SuggestionService.Register(item, [])).ToArray();

        internal PtsIndex FrameIndex()
        {
            long end = Revision.Utterances.Max(item => item.Range.End.Value * 30 / item.Range.End.Rate) + 30;
            var entries = Enumerable.Range(0, (int)end + 1)
                .Select(i => new PtsIndexEntry(i, new RationalTime(i, 30), new RationalTime(1, 30), i == 0)).ToArray();
            return new PtsIndex(EntityId<AssetIdTag>.New(), Metadata.ProjectId, EntityId<JobIdTag>.New(), "source.mp4",
                new ContentDigest(new string('a', 64)), 1000, 0, new RationalTime(1, 30), entries);
        }

        // One utterance per sentence, four English words per sentence, timing in milliseconds.
        internal static Scene Create((int Start, int Length)[] sentences)
        {
            var revisionId = EntityId<TranscriptRevisionIdTag>.New();
            var asset = EntityId<AssetIdTag>.New();
            var digest = new ContentDigest(new string('a', 64));
            var project = new Project(EntityId<ProjectIdTag>.New(), "speech", "speech segment", "viewers",
                PublicationScope.InternalOnly, deadline: null, assets: [], performers: []);
            var utterances = new List<Utterance>();
            var spans = new List<SourceSpan>();
            string[] words = ["this", "is", "a", "test"];
            for (int i = 0; i < sentences.Length; i++)
            {
                var tokens = new List<Token>();
                for (int w = 0; w < 4; w++)
                {
                    int start = sentences[i].Start + w * sentences[i].Length / 4;
                    tokens.Add(new Token(EntityId<TranscriptTokenIdTag>.New(), words[w],
                        new TimeRange(new RationalTime(start, 1000), new RationalTime(sentences[i].Length / 4, 1000)), 0.9));
                }
                string text = string.Join(" ", words) + " " + (char)('a' + i % 26) + "x.";
                var sentence = new Sentence(EntityId<TranscriptSentenceIdTag>.New(), text, tokens);
                var spanId = EntityId<SourceSpanIdTag>.New();
                var range = new TimeRange(new RationalTime(sentences[i].Start, 1000), new RationalTime(sentences[i].Length, 1000));
                utterances.Add(new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), spanId, EntityId<TranscriptSpeakerIdTag>.New(),
                    "Speaker 1", text, range, 0.9, [sentence]));
                spans.Add(new SourceSpan(spanId, asset, "audio:1", range, digest, revisionId, tokens.Select(t => t.Id).ToArray()));
            }
            var revision = new TranscriptRevision(revisionId, null, TranscriptChangeSource.Machine, CreatedAt, utterances);
            var run = EntityId<ModelRunIdTag>.New();
            TopicBoundaryScanResult scan = TopicBoundaryService.FindCandidates(revision, spans, run);
            RationalTime first = utterances[0].Range.Start;
            RationalTime last = utterances[^1].Range.End;
            TopicRangeRevision topic = TopicBoundaryService.CreateTopicRange(scan, EntityId<TopicRangeIdTag>.New(),
                EntityId<TopicRangeRevisionIdTag>.New(), null, null, new TimeRange(first, last - first), ActorSid, CreatedAt, "Topic.");
            TopicMetadataRevision metadata = TopicMetadataService.CreateInitial(project, revision, spans, topic, null, 1,
                EntityId<TopicMetadataIdTag>.New(), EntityId<TopicMetadataRevisionIdTag>.New(), ActorSid, CreatedAt, "Metadata.");
            return new Scene(revision, spans, metadata, run);
        }
    }
}
