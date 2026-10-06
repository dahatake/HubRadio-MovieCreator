using System.Globalization;
using System.Text;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Profiles;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardSubtitleBuilderTests
{
    private const string Actor = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset Created = new(2044, 9, 8, 1, 2, 3, TimeSpan.Zero);

    // FR-050.7 AC-025.5: every subtitle value offered by the settings screen builds placed cues in both orientations.
    [TestMethod]
    public void SettingEditorCatalogSubtitleChoicesBuildInBothOrientations()
    {
        TokenInput[] tokens = Enumerable.Range(0, 10).Select(index => new TokenInput(
            $"字幕{index:D2}。", Range(index * 30, index == 9 ? 30 : 29, 10))).ToArray();
        int built = 0;
        foreach (bool vertical in new[] { false, true })
        {
            var fixture = new Fixture([new UtteranceInput(Range(0, 30), tokens)], vertical: vertical);
            foreach ((string key, DefaultSettingValue value) in SettingEditorCatalogTests.OfferedChoices(fixture.Profile)
                .Where(static item => item.Key.StartsWith("subtitles.", StringComparison.Ordinal)))
            {
                DefaultSettingsSnapshot settings = fixture.WithSettings((key, value));
                StandardSubtitleBuildResult result = fixture.Result(settings: settings);
                string label = $"{(vertical ? "vertical" : "landscape")} {key}={SettingEditorCatalogTests.Describe(value)}\n{Describe(result)}";
                Assert.AreEqual(settings.ContentDigest, result.SettingsDigest, label);
                Assert.IsFalse(result.HasUnresolvedCues, label);
                Assert.IsNotEmpty(result.Subtitles.Revisions, label);
                built++;
            }
        }

        Assert.IsGreaterThan(70, built);
    }

    [TestMethod]
    public void ThirtySecondMachineRootProducesBoundedInitialSourceCuesWithActualDefaults()
    {
        TokenInput[] tokens = Enumerable.Range(0, 10).Select(index => new TokenInput(
            $"字幕{index:D2}。", Range(index * 30, index == 9 ? 30 : 29, 10))).ToArray();
        var fixture = new Fixture([new UtteranceInput(Range(0, 30), tokens)]);
        string originalJson = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(5, result.Subtitles.Revisions);
        Assert.AreEqual(SubtitleSegmenter.MachineRuleVersion, result.SegmentationRuleVersion);
        Assert.AreEqual(fixture.Settings.ContentDigest, result.SettingsDigest);
        Assert.IsNull(fixture.Transcript.ParentRevisionId);
        Assert.AreEqual(TranscriptChangeSource.Machine, fixture.Transcript.ChangeSource);
        Assert.AreEqual(originalJson, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        Assert.AreEqual(fixture.Transcript.Utterances[0].Text,
            string.Concat(result.Subtitles.Revisions.Select(static revision => revision.Snapshot.Text)));
        CollectionAssert.AreEqual(fixture.Transcript.Utterances[0].Tokens.Select(static token => token.Id).ToArray(),
            result.Subtitles.Candidates.SelectMany(static candidate => candidate.EvidenceTokenIds).ToArray());
        foreach (SubtitleCueRevision revision in result.Subtitles.Revisions)
        {
            SubtitleCueCandidate candidate = result.Subtitles.Candidates.Single(item => item.Id == revision.SourceCandidateId);
            Assert.IsNull(revision.ParentRevisionId);
            Assert.AreEqual(SubtitleCueChangeKind.Initial, revision.Change.Kind);
            Assert.AreEqual(fixture.Version.Id, revision.CompositionVersionId);
            Assert.AreEqual(fixture.Version.ContentDigest, revision.CompositionVersionDigest);
            Assert.AreEqual(fixture.Transcript.Id, revision.SourceRevisionId);
            Assert.AreEqual(fixture.TranscriptDigest, revision.SourceRevisionDigest);
            Assert.AreEqual(OriginKind.SourceSpan, revision.Snapshot.TextOrigin.Kind);
            Assert.AreEqual(candidate.Origin, revision.Snapshot.TextOrigin);
            Assert.AreEqual(candidate.SourceRange, revision.Snapshot.Cue.Range);
            Assert.AreEqual(candidate.Text, revision.Snapshot.Text);
            Assert.AreEqual(fixture.Spans[0].AssetId, candidate.SourceSpan.AssetId);
            Assert.AreEqual(fixture.Spans[0].StreamId, candidate.SourceSpan.StreamId);
            Assert.AreEqual(fixture.Spans[0].SourceDigest, candidate.SourceSpan.SourceDigest);
            Assert.HasCount(0, candidate.TimingFallbacks);
            Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, revision.Snapshot.LayoutResolution.Status);
            Assert.AreEqual(fixture.Layout.Id, revision.Snapshot.LayoutResolution.LayoutSpecId);
            StandardSubtitlePlacement.Validate(result.Subtitles.AssProfile, revision.Snapshot.LayoutResolution);
        }

        AssertBounds(result.Subtitles, fixture.RenderProfile, 16, 2, 7, Time(5, 6), Time(7));
        Assert.AreEqual(SubtitleCueDisplayMode.Full, result.Subtitles.DisplayProfile.CueMode);
        Assert.AreEqual(SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod, result.Subtitles.DisplayProfile.PunctuationDisplay);
        Assert.IsTrue(result.Subtitles.AssProfile.Styles.All(static style => style.FontName == "Noto Sans JP"
            && style.FontSize == 81 && style.PrimaryColor.Equals(new AssColor(0, 255, 255, 255))
            && style.BackgroundColor.Equals(new AssColor(255, 0, 0, 0))));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Key == "subtitles.backgroundOpaque" && !warning.IsUnresolved));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "NOT EVALUATED"));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "platform-ui-mask-not-measured"));
        Assert.HasCount(5, fixture.Build().Revisions);
    }

    [TestMethod]
    public void HumanChildUsesExistingHumanEntryPointWithoutAddingEditorialChanges()
    {
        var fixture = new Fixture([Utterance("人手の原文。", Range(0, 2))], TranscriptChangeSource.Human);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.AreEqual(SubtitleSegmenter.RuleVersion, result.SegmentationRuleVersion);
        Assert.AreEqual("人手の原文。", result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(SubtitleCueChangeKind.Initial, result.Subtitles.Revisions[0].Change.Kind);
        Assert.AreEqual(OriginKind.SourceSpan, result.Subtitles.Revisions[0].Snapshot.TextOrigin.Kind);
        var humanRoot = new TranscriptRevision(fixture.Transcript.Id, null, TranscriptChangeSource.Human,
            fixture.Transcript.CreatedAt, fixture.Transcript.Utterances);
        Assert.AreEqual(SubtitleSegmentationFailureReason.SourceRevisionNotHumanChild,
            Assert.ThrowsExactly<SubtitleSegmentationException>(() => fixture.Result(transcript: humanRoot)).Reason);
    }

    [TestMethod]
    public void EmptyRealAsrReturnsNoCuesAndNoFabricatedSpeechOrRevision()
    {
        var fixture = new Fixture([]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.HasCount(0, result.Subtitles.Revisions);
        Assert.HasCount(0, result.Subtitles.Candidates);
        Assert.IsFalse(result.HasUnresolvedCues);
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "no-real-asr-cues"));
        Assert.IsNull(fixture.Transcript.ParentRevisionId);
        CollectionAssert.AreEqual(result.Warnings.ToArray(), result.Subtitles.Warnings.ToArray());
        Assert.HasCount(0, fixture.Build().Revisions);
    }

    [TestMethod]
    public void MissingTimingIsNeverEvaluatedUsingUtteranceFallbackAndStrictBuildReportsIt()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 3), [new TokenInput("未確定。", null)])]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.HasCount(0, result.Subtitles.Revisions);
        StandardSubtitleBuildWarning warning = result.Warnings.Single(static item => item.Reason == "missing-token-timing");
        Assert.IsTrue(warning.IsUnresolved);
        Assert.AreEqual("subtitles.sourceTiming", warning.Key);
        Assert.AreEqual(fixture.Transcript.Utterances[0].Range, warning.SourceRange);
        Assert.IsNull(fixture.Transcript.Utterances[0].Tokens[0].Range);
        StandardSubtitleBuildException exception = Assert.ThrowsExactly<StandardSubtitleBuildException>(() => fixture.Build());
        Assert.IsTrue(exception.Result.HasUnresolvedCues);
        CollectionAssert.AreEqual(exception.Result.Warnings.ToArray(), exception.Result.Subtitles.Warnings.ToArray());
        Assert.IsFalse(exception.ToString().Contains("未確定", StringComparison.Ordinal));
        Assert.IsFalse(Describe(result).Contains("未確定", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidTokensOnEitherSideOfMissingTimingKeepOnlyTheirOwnExactEvidence()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 5),
            [new TokenInput("先。", Range(0, 1)), new TokenInput("不明", null), new TokenInput("後。", Range(4, 1))])]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.HasCount(2, result.Subtitles.Revisions);
        CollectionAssert.AreEqual(new[] { Range(0, 1), Range(4, 1) },
            result.Subtitles.Candidates.Select(static candidate => candidate.SourceRange).ToArray());
        EntityId<TranscriptTokenIdTag> untimed = fixture.Transcript.Utterances[0].Tokens[1].Id;
        Assert.IsFalse(result.Subtitles.Candidates.Any(candidate => candidate.EvidenceTokenIds.Contains(untimed)));
        Assert.IsTrue(result.Subtitles.Candidates.All(static candidate => !candidate.ManualTimingAdjustmentRequired));
    }

    [TestMethod]
    public void RealOverlappingTokensAreExcludedWithTheirExactRangesWithoutClamping()
    {
        var fixture = new Fixture([Utterance("先。", Range(0, 3)), Utterance("重複。", Range(2, 2)),
            Utterance("安全。", Range(6, 2))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsTrue(result.HasUnresolvedCues);
        CollectionAssert.AreEquivalent(new[] { Range(0, 3), Range(2, 2) },
            result.Warnings.Where(static warning => warning.Reason == "overlapping-token-timing")
                .Select(static warning => warning.SourceRange!.Value).ToArray());
        Assert.AreEqual("安全。", result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(Range(6, 2), result.Subtitles.Candidates.Single().SourceRange);
    }

    [TestMethod]
    [DataRow(16, false)]
    [DataRow(17, true)]
    public void LongSingleTokenCannotBorrowInventedInnerTiming(int characters, bool unresolved)
    {
        var fixture = new Fixture([Utterance(new string('あ', characters), Range(0, 3))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        if (unresolved)
            Assert.IsTrue(result.Warnings.Any(static warning => warning.Key == "subtitles.horizontalLineLengthMaximum"
                && warning.Reason == "text-cannot-split-without-inner-token-timing"));
    }

    [TestMethod]
    public void SeventeenAsciiCharactersInOneThreeSecondTokenFitActualDefaultsUnchanged()
    {
        const string text = "abcdefghijklmnopq";
        var fixture = new Fixture([Utterance(text, Range(0, 3))]);
        string originalJson = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(17, text.Length);
        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.AreEqual(text, result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(Range(0, 3), result.Subtitles.Candidates.Single().SourceRange);
        CollectionAssert.AreEqual(fixture.Spans[0].TranscriptTokenIds.ToArray(),
            result.Subtitles.Candidates[0].EvidenceTokenIds.ToArray());
        Assert.AreEqual(originalJson, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        AssertBounds(result.Subtitles, fixture.RenderProfile, 16, 2, 7, Time(5, 6), Time(7));
        Assert.AreEqual(text, fixture.Build().Revisions.Single().Snapshot.Text);
        CollectionAssert.AreEqual(result.Warnings.ToArray(), result.Subtitles.Warnings.ToArray());
    }

    [TestMethod]
    [DataRow("A", 32, false)]
    [DataRow("A", 33, true)]
    [DataRow("ｱ", 32, false)]
    [DataRow("ｱ", 33, true)]
    [DataRow("ｶﾞ", 16, false)]
    [DataRow("ｶﾞ", 17, true)]
    [DataRow("ﾊﾟ", 16, false)]
    [DataRow("ﾊﾟ", 17, true)]
    [DataRow("Ａ", 16, false)]
    [DataRow("Ａ", 17, true)]
    [DataRow("か\u3099", 16, false)]
    [DataRow("か\u3099", 17, true)]
    [DataRow("1\uFE0F\u20E3", 16, false)]
    [DataRow("1\uFE0F\u20E3", 17, true)]
    public void SingleTokenLineLengthUsesExactHalfWidthUnitsWithoutNormalizingText(
        string grapheme, int count, bool unresolved)
    {
        string text = string.Concat(Enumerable.Repeat(grapheme, count));
        var fixture = new Fixture([Utterance(text, Range(0, 5))]);
        string originalJson = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        Assert.AreEqual(originalJson, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        if (unresolved)
        {
            StandardSubtitleBuildWarning warning = result.Warnings.Single(static item => item.IsUnresolved);
            Assert.AreEqual("subtitles.horizontalLineLengthMaximum", warning.Key);
            Assert.AreEqual("text-cannot-split-without-inner-token-timing", warning.Reason);
            Assert.AreEqual(Range(0, 5), warning.SourceRange);
            Assert.AreEqual(fixture.Spans[0].Id, warning.SourceSpanId);
        }
        else
        {
            Assert.AreEqual(text, result.Subtitles.Revisions[0].Snapshot.Text);
            Assert.AreEqual(text, result.Subtitles.Candidates[0].Text);
            Assert.AreEqual(Range(0, 5), result.Subtitles.Candidates[0].SourceRange);
            AssertBounds(result.Subtitles, fixture.RenderProfile, 16, 2, 7, Time(5, 6), Time(7));
        }
    }

    [TestMethod]
    [DataRow("AB", false)]
    [DataRow("ABC", true)]
    [DataRow(" ｱ", false)]
    [DataRow("　ｱ", true)]
    [DataRow("!ｱ", false)]
    [DataRow("！ｱ", true)]
    [DataRow("Ａ", false)]
    [DataRow("ＡA", true)]
    public void MixedLineCountsHalfWidthSpacesAndPunctuationWithoutRoundingDown(string suffix, bool unresolved)
    {
        string text = new string('声', 15) + suffix;
        var fixture = new Fixture([Utterance(text, Range(0, 4))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        if (unresolved)
            Assert.AreEqual("subtitles.horizontalLineLengthMaximum",
                result.Warnings.Single(static warning => warning.IsUnresolved).Key);
        else
            Assert.AreEqual(text, result.Subtitles.Revisions[0].Snapshot.Text);
        Assert.AreEqual(text, fixture.Transcript.Utterances[0].Text);
    }

    [TestMethod]
    [DataRow(22, false)]
    [DataRow(23, true)]
    public void VerticalLineLimitUsesTwentyTwoHalfWidthUnits(int count, bool unresolved)
    {
        string text = new string('A', count);
        var fixture = new Fixture([Utterance(text, Range(0, 4))], vertical: true);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        if (unresolved)
            Assert.AreEqual("subtitles.verticalLineLengthMaximum",
                result.Warnings.Single(static warning => warning.IsUnresolved).Key);
        else
        {
            Assert.AreEqual(text, result.Subtitles.Revisions[0].Snapshot.Text);
            AssertBounds(result.Subtitles, fixture.RenderProfile, 11, 2, 7, Time(5, 6), Time(7));
        }
    }

    [TestMethod]
    [DataRow(16, 1)]
    [DataRow(17, 2)]
    public void PartitionUsesHalfWidthLineUnitsAndOnlyExistingTokenTimes(int charactersPerToken, int cueCount)
    {
        string first = new('A', charactersPerToken);
        string second = new('B', charactersPerToken);
        var fixture = new Fixture([new UtteranceInput(Range(0, 51, 10),
            [new TokenInput(first, Range(0, 25, 10)), new TokenInput(second, Range(26, 25, 10))])]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(cueCount, result.Subtitles.Revisions);
        Assert.AreEqual(first + second, string.Concat(result.Subtitles.Revisions.Select(static cue => cue.Snapshot.Text)));
        TimeRange[] expected = cueCount == 1 ? [Range(0, 51, 10)] : [Range(0, 25, 10), Range(26, 25, 10)];
        CollectionAssert.AreEqual(expected, result.Subtitles.Candidates.Select(static cue => cue.SourceRange).ToArray());
        CollectionAssert.AreEqual(fixture.Spans[0].TranscriptTokenIds.ToArray(),
            result.Subtitles.Candidates.SelectMany(static cue => cue.EvidenceTokenIds).ToArray());
        AssertBounds(result.Subtitles, fixture.RenderProfile, 16, 2, 7, Time(5, 6), Time(7));
    }

    [TestMethod]
    public void ExistingTwoLinesArePreservedButThirdLineIsNotSilentlyRewritten()
    {
        string text = new string('あ', 16) + "\n" + new string('い', 16);
        var fixture = new Fixture([Utterance(text, Range(0, 5))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.AreEqual(text, result.Subtitles.Revisions.Single().Snapshot.Text);
        AssertBounds(result.Subtitles, fixture.RenderProfile, 16, 2, 7, Time(5, 6), Time(7));
        var threeLines = new Fixture([Utterance("一\n二\n三", Range(0, 2))]);
        StandardSubtitleBuildResult excluded = threeLines.Result();
        Assert.IsTrue(excluded.HasUnresolvedCues);
        Assert.HasCount(0, excluded.Subtitles.Revisions);
        Assert.IsTrue(excluded.Warnings.Any(static warning => warning.Key == "subtitles.lineCountMaximum" && warning.IsUnresolved));
    }

    [TestMethod]
    public void CombiningCharactersAreCountedAsTextElementsAndNeverSplitInsideGraphemes()
    {
        string text = string.Concat(Enumerable.Repeat("か\u3099", 16));
        var fixture = new Fixture([Utterance(text, Range(0, 3))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.AreEqual(32, result.Subtitles.Revisions[0].Snapshot.Text.Length);
        Assert.AreEqual(16, StringInfo.ParseCombiningCharacters(result.Subtitles.Revisions[0].Snapshot.Text).Length);
        Assert.AreEqual(text, result.Subtitles.Revisions[0].Snapshot.Text);
    }

    [TestMethod]
    public void TokenBoundaryInsideAGraphemeCannotBeUsedToSatisfyTheLineLimit()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 41, 10),
            [new TokenInput(new string('あ', 16), Range(0, 3)), new TokenInput("\u3099い", Range(31, 10, 10))])]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.HasCount(0, result.Subtitles.Revisions);
        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "no-grapheme-safe-token-partition"));
    }

    [TestMethod]
    public void NonPunctuationTextOutsideTokensDoesNotBorrowTheirTiming()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 2),
            [new TokenInput("先", Range(0, 2))], "先。未対応の語句")]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.HasCount(0, result.Subtitles.Revisions);
        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "unmatched-text-without-token-timing"));
        Assert.IsFalse(Describe(result).Contains("未対応の語句", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(5, 6, false)]
    [DataRow(4, 5, true)]
    [DataRow(7, 1, false)]
    [DataRow(701, 100, true)]
    public void ExactMinimumAndMaximumDurationsAreEnforcedWithoutTimeInterpolation(int value, int rate, bool unresolved)
    {
        var fixture = new Fixture([Utterance("声。", Range(0, value, rate))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        if (!unresolved)
            Assert.AreEqual(Range(0, value, rate), result.Subtitles.Candidates[0].SourceRange);
        else
            Assert.IsTrue(result.Warnings.Any(static warning => warning.IsUnresolved
                && warning.Key is "subtitles.cueMinimumDuration" or "subtitles.cueMaximumDuration"));
    }

    [TestMethod]
    [DataRow(7, false)]
    [DataRow(8, true)]
    public void ReadingSpeedIsCheckedOnActualDisplayTextAndExactDuration(int count, bool unresolved)
    {
        var fixture = new Fixture([Utterance(new string('声', count) + "。", Range(0, 1))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        if (unresolved)
            Assert.IsTrue(result.Warnings.Any(static warning => warning.Key == "subtitles.readingSpeedMaximum"
                && warning.Reason == "source-reading-speed-exceeded"));
    }

    [TestMethod]
    [DataRow("A", 7, false)]
    [DataRow("A", 8, true)]
    [DataRow("ｱ", 7, false)]
    [DataRow("ｱ", 8, true)]
    [DataRow("ｶﾞ", 7, false)]
    [DataRow("ｶﾞ", 8, true)]
    public void HalfWidthLineDiscountDoesNotHalveReadingSpeed(string grapheme, int count, bool unresolved)
    {
        string text = string.Concat(Enumerable.Repeat(grapheme, count)) + "。";
        var fixture = new Fixture([Utterance(text, Range(0, 1))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 1, result.Subtitles.Revisions);
        if (unresolved)
        {
            StandardSubtitleBuildWarning warning = result.Warnings.Single(static item => item.IsUnresolved);
            Assert.AreEqual("subtitles.readingSpeedMaximum", warning.Key);
            Assert.AreEqual("source-reading-speed-exceeded", warning.Reason);
        }
        else
            Assert.AreEqual(text, result.Subtitles.Revisions[0].Snapshot.Text);
    }

    [TestMethod]
    [DataRow(30, 1, 2, false)]
    [DataRow(30000, 1001, 2, false)]
    [DataRow(30, 1, 1, true)]
    public void RequiredTwoFrameGapUsesActualFrameRateAndCannotBeFabricated(int numerator, int denominator, int gapFrames, bool unresolved)
    {
        RationalTime duration = RationalTime.FromFrames(45, numerator, denominator);
        RationalTime secondStart = RationalTime.FromFrames(45 + gapFrames, numerator, denominator);
        var fixture = new Fixture([new UtteranceInput(new TimeRange(default, secondStart + duration),
            [new TokenInput(new string('あ', 9), new TimeRange(default, duration)),
             new TokenInput(new string('い', 9), new TimeRange(secondStart, duration))])],
            frameRate: new RationalNumber(numerator, denominator));

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.AreEqual(unresolved, result.HasUnresolvedCues, Describe(result));
        Assert.HasCount(unresolved ? 0 : 2, result.Subtitles.Revisions);
        if (unresolved)
            Assert.IsTrue(result.Warnings.Any(static warning => warning.Key == "subtitles.cueGapMinimum" && warning.IsUnresolved));
        else
        {
            Assert.AreEqual(RationalTime.FromFrames(2, numerator, denominator),
                result.Subtitles.Revisions[1].Snapshot.Cue.Range.Start - result.Subtitles.Revisions[0].Snapshot.Cue.Range.End);
            AssertBounds(result.Subtitles, fixture.RenderProfile, 16, 2, 7, Time(5, 6), Time(7));
        }
    }

    [TestMethod]
    public void BackwardPartitionAvoidsGreedyOvershootLeavingAnUntimedOrTooShortTail()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 25, 10),
            [new TokenInput("一", Range(0, 10, 10)), new TokenInput("二", Range(11, 8, 10)),
             new TokenInput("三", Range(20, 5, 10))])]);
        DefaultSettingsSnapshot settings = fixture.WithSettings(("subtitles.cueMaximumDuration", DefaultSettingValue.FromInteger(2)));

        StandardSubtitleBuildResult result = fixture.Result(settings: settings);

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        CollectionAssert.AreEqual(new[] { "一", "二三" }, result.Subtitles.Revisions.Select(static revision => revision.Snapshot.Text).ToArray());
        CollectionAssert.AreEqual(new[] { Range(0, 1), Range(11, 14, 10) },
            result.Subtitles.Candidates.Select(static candidate => candidate.SourceRange).ToArray());
        Assert.AreEqual(settings.ContentDigest, result.SettingsDigest);
    }

    [TestMethod]
    public void OutputCutsReorderOnlyWholeRealTokensAndPreserveTheirOriginalSourceRanges()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 8),
            [new TokenInput("一。", Range(0, 1)), new TokenInput("二。", Range(3, 1)), new TokenInput("三。", Range(6, 1))])]);
        CompositionVersion version = fixture.CutVersion((fixture.Spans[0], Range(3, 2)), (fixture.Spans[0], Range(0, 2)));

        StandardSubtitleBuildResult result = fixture.Result(version: version);

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        CollectionAssert.AreEqual(new[] { "二。", "一。" }, result.Subtitles.Revisions.Select(static revision => revision.Snapshot.Text).ToArray());
        CollectionAssert.AreEqual(new[] { Range(0, 1), Range(2, 1) },
            result.Subtitles.Revisions.Select(static revision => revision.Snapshot.Cue.Range).ToArray());
        CollectionAssert.AreEqual(new[] { Range(3, 1), Range(0, 1) },
            result.Subtitles.Candidates.Select(static candidate => candidate.SourceRange).ToArray());
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "outside-output-cuts" && !warning.IsUnresolved));
        Assert.IsFalse(result.Subtitles.Revisions.Any(static revision => revision.Snapshot.Text == "三。"));
    }

    [TestMethod]
    public void GapRemovedByOutputCutsIsReportedAgainstOriginalSourceSpanRatherThanRetimed()
    {
        var fixture = new Fixture([Utterance("一。", Range(0, 2)), Utterance("二。", Range(4, 2))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.HasCount(1, result.Subtitles.Revisions);
        Assert.IsTrue(result.HasUnresolvedCues);
        StandardSubtitleBuildWarning warning = result.Warnings.Single(static warning =>
            warning.Reason == "output-gap-below-minimum-without-retiming");
        Assert.AreEqual(Range(4, 2), warning.SourceRange);
        Assert.AreEqual(fixture.Spans[1].Id, warning.SourceSpanId);
        Assert.AreEqual(Range(0, 2), result.Subtitles.Revisions[0].Snapshot.Cue.Range);
        Assert.AreEqual(Range(4, 2), fixture.Transcript.Utterances[1].Tokens[0].Range);
    }

    [TestMethod]
    public void CutInsideATokenProducesWarningRatherThanClippingTextOrSynthesizingTimes()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 5),
            [new TokenInput("途中。", Range(0, 2)), new TokenInput("残る。", Range(3, 1))])]);
        CompositionVersion version = fixture.CutVersion((fixture.Spans[0], Range(1, 4)));

        StandardSubtitleBuildResult result = fixture.Result(version: version);

        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.AreEqual("残る。", result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(Range(2, 1), result.Subtitles.Revisions[0].Snapshot.Cue.Range);
        Assert.AreEqual(Range(3, 1), result.Subtitles.Candidates[0].SourceRange);
        StandardSubtitleBuildWarning warning = result.Warnings.Single(static warning =>
            warning.Reason == "cut-crosses-token-without-inner-timing");
        Assert.AreEqual(Range(0, 2), warning.SourceRange);
        Assert.IsTrue(warning.IsUnresolved);
    }

    [TestMethod]
    public void EqualTimesWithoutASourceSpanBindingAreNotValidMappingEvidence()
    {
        var fixture = new Fixture([Utterance("本物。", Range(0, 2))]);
        SourceSpan original = fixture.Spans[0];
        var unknown = new SourceSpan(EntityId<SourceSpanIdTag>.New(), original.AssetId, original.StreamId,
            original.Range, original.SourceDigest, original.TranscriptRevisionId, original.TranscriptTokenIds);
        CompositionVersion version = fixture.CutVersion((unknown, unknown.Range));

        StandardSubtitleBuildResult result = fixture.Result(version: version);

        Assert.HasCount(0, result.Subtitles.Revisions);
        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.IsTrue(result.Warnings.Any(warning => warning.Reason == "unresolved-output-source-span"
            && warning.SourceSpanId == unknown.Id && warning.IsUnresolved));
    }

    [TestMethod]
    public void ExplicitCutSpanRequiresContiguousRevisionBoundTokenEvidence()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 5),
            [new TokenInput("一。", Range(0, 1)), new TokenInput("二。", Range(3, 1))])]);
        SourceSpan original = fixture.Spans[0];
        var cut = new SourceSpan(EntityId<SourceSpanIdTag>.New(), original.AssetId, original.StreamId,
            Range(3, 2), original.SourceDigest, original.TranscriptRevisionId, [original.TranscriptTokenIds[1]]);
        CompositionVersion version = fixture.CutVersion((cut, cut.Range));

        StandardSubtitleBuildResult result = fixture.Result(version: version, spans: [original, cut]);

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.AreEqual("二。", result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(Range(3, 1), result.Subtitles.Candidates[0].SourceRange);
        var forged = new SourceSpan(cut.Id, cut.AssetId, "audio:99", cut.Range, cut.SourceDigest,
            cut.TranscriptRevisionId, cut.TranscriptTokenIds);
        Assert.AreEqual(SubtitleSegmentationFailureReason.SourceSpanBindingMismatch,
            Assert.ThrowsExactly<SubtitleSegmentationException>(() => fixture.Result(version: version, spans: [original, forged])).Reason);
    }

    [TestMethod]
    public void RepeatedSourceCutsUseUniqueCandidateCueAndSpanIdentitiesAndDeterministicEvidence()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 2), [new TokenInput("反復。", Range(5, 10, 10))])]);
        CompositionVersion version = fixture.CutVersion((fixture.Spans[0], Range(0, 2)), (fixture.Spans[0], Range(0, 2)));
        TimelineMap timeline = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version);

        StandardSubtitleBuildResult first = fixture.Result(version: version, timeline: timeline);
        StandardSubtitleBuildResult second = fixture.Result(version: version, timeline: timeline);

        Assert.IsFalse(first.HasUnresolvedCues, Describe(first));
        Assert.HasCount(2, first.Subtitles.Revisions);
        Assert.HasCount(2, first.Subtitles.Candidates.Select(static candidate => candidate.Id).Distinct().ToArray());
        Assert.HasCount(2, first.Subtitles.Candidates.Select(static candidate => candidate.SourceSpan.Id).Distinct().ToArray());
        CollectionAssert.AreEqual(first.Subtitles.Revisions.Select(static revision => revision.ContentDigest).ToArray(),
            second.Subtitles.Revisions.Select(static revision => revision.ContentDigest).ToArray());
        CollectionAssert.AreEqual(first.Subtitles.Revisions.Select(static revision => revision.Id).ToArray(),
            second.Subtitles.Revisions.Select(static revision => revision.Id).ToArray());
        Assert.AreEqual(Describe(first), Describe(second));
        Assert.IsTrue(first.Subtitles.Candidates.All(candidate => candidate.EvidenceTokenIds.Single()
            == fixture.Transcript.Utterances[0].Tokens[0].Id));
    }

    [TestMethod]
    public void SameDisplayTextFeedsAssSrtAndVttWhileSourcePunctuationIsUnmodified()
    {
        const string source = "こんにちは、 世界。！";
        const string displayText = "こんにちは 世界！";
        var fixture = new Fixture([Utterance(source, Range(0, 3))]);
        StandardSubtitleInput input = fixture.Build();
        SubtitleCueSnapshot snapshot = input.Revisions.Single().Snapshot;
        SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(input.DisplayProfile,
            input.Revisions.Select(static revision => SubtitleDisplayMapper.MapSource(revision.Snapshot)).ToArray(), []);
        SubtitleFileCue cue = SubtitleDisplayMapper.MapForExport(snapshot, display.DisplayedCues.Single());

        string ass = Encoding.UTF8.GetString(StandardSubtitlePlacement.Write(cue, input.AssProfile, snapshot.LayoutResolution).GetUtf8Bytes());
        string srt = Encoding.UTF8.GetString(SubRipSubtitleWriter.Write([cue]).GetUtf8Bytes());
        string vtt = Encoding.UTF8.GetString(WebVttSubtitleWriter.Write([cue]).GetUtf8Bytes());

        Assert.AreEqual(source, snapshot.Text);
        Assert.AreEqual(source, input.Candidates[0].Text);
        Assert.AreEqual(source, fixture.Transcript.Utterances[0].Text);
        Assert.AreEqual(displayText, cue.Text);
        Assert.HasCount(0, display.OmittedCueIds);
        StringAssert.Contains(ass, displayText);
        StringAssert.Contains(srt, displayText);
        StringAssert.Contains(vtt, displayText);
        StringAssert.Contains(ass, "&HFF000000");
        StringAssert.Contains(ass, "Noto Sans JP");
        Assert.IsFalse(ass.Contains(source, StringComparison.Ordinal));
    }

    [TestMethod]
    public void PunctuationOnlyCueHasAnExplicitWarningInsteadOfAnApparentlyValidEmptyDisplay()
    {
        var fixture = new Fixture([Utterance("、。", Range(0, 2))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.HasCount(0, result.Subtitles.Revisions);
        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Reason == "no-displayable-source-text"));
    }

    [TestMethod]
    public void PartialSubtitleInputPreservesAllUnresolvedInformationalAndPolicyWarnings()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 12),
            [new TokenInput("安全。", Range(0, 1)), new TokenInput(new string('声', 17), Range(3, 3)),
             new TokenInput("対象外。", Range(9, 1))])]);
        CompositionVersion version = fixture.CutVersion((fixture.Spans[0], Range(0, 7)));
        string originalJson = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);

        StandardSubtitleBuildResult result = fixture.Result(version: version);

        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.AreEqual("安全。", result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(Range(0, 1), result.Subtitles.Candidates.Single().SourceRange);
        CollectionAssert.AreEqual(result.Warnings.ToArray(), result.Subtitles.Warnings.ToArray());
        StandardSubtitleBuildWarning unresolved = result.Subtitles.Warnings.Single(static warning => warning.IsUnresolved);
        Assert.AreEqual("subtitles.horizontalLineLengthMaximum", unresolved.Key);
        Assert.AreEqual("text-cannot-split-without-inner-token-timing", unresolved.Reason);
        Assert.AreEqual(StandardSubtitleBuilder.RuleVersion + ".source-token", unresolved.Rule);
        Assert.AreEqual(Range(3, 3), unresolved.SourceRange);
        Assert.AreEqual(fixture.Spans[0].Id, unresolved.SourceSpanId);
        Assert.IsTrue(result.Subtitles.Warnings.Any(static warning => warning.Reason == "outside-output-cuts" && !warning.IsUnresolved));
        Assert.IsTrue(result.Subtitles.Warnings.Any(static warning => warning.Reason == "NOT EVALUATED" && !warning.IsUnresolved));
        Assert.IsTrue(result.Subtitles.Warnings.Any(static warning => warning.Reason == "platform-ui-mask-not-measured"));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StandardSubtitleBuildWarning>)result.Subtitles.Warnings).Clear());
        Assert.AreEqual(originalJson, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
    }

    [TestMethod]
    [DataRow("\\N")]
    [DataRow("\\n")]
    [DataRow("\\h")]
    [DataRow("\\\\N")]
    [DataRow("\\、N")]
    [DataRow("\\｡h")]
    public void ReservedAssSourceSequencesHaveStablePrivateWarningsWhileSafeCuesSurvive(string sequence)
    {
        string unsupported = "private" + sequence + "text";
        var fixture = new Fixture([new UtteranceInput(Range(0, 7),
            [new TokenInput("先。", Range(0, 1)), new TokenInput(unsupported, Range(3, 1)),
             new TokenInput("後。", Range(6, 1))])]);
        string originalJson = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsTrue(result.HasUnresolvedCues);
        CollectionAssert.AreEqual(new[] { "先。", "後。" },
            result.Subtitles.Revisions.Select(static revision => revision.Snapshot.Text).ToArray());
        CollectionAssert.AreEqual(new[] { Range(0, 1), Range(6, 1) },
            result.Subtitles.Candidates.Select(static candidate => candidate.SourceRange).ToArray());
        StandardSubtitleBuildWarning warning = result.Subtitles.Warnings.Single(static item => item.IsUnresolved);
        Assert.AreEqual("subtitles.lineBreakPolicy", warning.Key);
        Assert.AreEqual(StandardSubtitleBuilder.RuleVersion + ".source-token", warning.Rule);
        Assert.AreEqual("unsupported-source-display-text", warning.Reason);
        Assert.AreEqual(Range(3, 1), warning.SourceRange);
        Assert.AreEqual(fixture.Spans[0].Id, warning.SourceSpanId);
        CollectionAssert.AreEqual(result.Warnings.ToArray(), result.Subtitles.Warnings.ToArray());
        Assert.AreEqual(originalJson, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
        Assert.IsFalse(Describe(result).Contains("private", StringComparison.Ordinal));
        Assert.ThrowsExactly<StandardSubtitleBuildException>(() => fixture.Build());
    }

    [TestMethod]
    [DataRow("N")]
    [DataRow("n")]
    [DataRow("h")]
    public void ReservedAssSequenceAcrossTokensCannotBeMadeSafeByInventingAGap(string command)
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 6),
            [new TokenInput("先。", Range(0, 1)), new TokenInput("\\", Range(2, 1)),
             new TokenInput(command, Range(3, 1)), new TokenInput("後。", Range(5, 1))])]);
        string originalJson = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsTrue(result.HasUnresolvedCues);
        CollectionAssert.AreEqual(new[] { "先。", "後。" },
            result.Subtitles.Revisions.Select(static revision => revision.Snapshot.Text).ToArray());
        StandardSubtitleBuildWarning warning = result.Subtitles.Warnings.Single(static item =>
            item.Reason == "unsupported-source-display-text");
        Assert.IsTrue(warning.IsUnresolved);
        Assert.AreEqual("subtitles.lineBreakPolicy", warning.Key);
        Assert.AreEqual(StandardSubtitleBuilder.RuleVersion + ".exact-token-partition", warning.Rule);
        Assert.AreEqual(Range(2, 2), warning.SourceRange);
        Assert.AreEqual(fixture.Spans[0].Id, warning.SourceSpanId);
        CollectionAssert.AreEqual(result.Warnings.ToArray(), result.Subtitles.Warnings.ToArray());
        Assert.AreEqual(originalJson, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
    }

    [TestMethod]
    [DataRow("A\\q")]
    [DataRow("A\\H")]
    [DataRow("A\\Ｎ")]
    [DataRow("A\nB")]
    [DataRow("A\r\nB")]
    [DataRow("A\rB")]
    public void NonReservedBackslashesAndRealLineBreaksArePreserved(string text)
    {
        var fixture = new Fixture([Utterance(text, Range(0, 3))]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        Assert.AreEqual(text, result.Subtitles.Revisions.Single().Snapshot.Text);
        Assert.AreEqual(text, result.Subtitles.Candidates.Single().Text);
        Assert.AreEqual(text, fixture.Transcript.Utterances[0].Text);
    }

    [TestMethod]
    public void SettingsIdentityVersionDigestAndRenderTimelineBindingsFailClosed()
    {
        var fixture = new Fixture([Utterance("設定。", Range(0, 2))]);
        DefaultSettingsProfile alien = DefaultSettingsProfile.Create(EntityId<DefaultSettingsProfileIdTag>.New(),
            fixture.Profile.Name, fixture.Profile.Version, fixture.Profile.Definitions);
        DefaultSettingsProfile changedVersion = DefaultSettingsProfile.Create(fixture.Profile.Id,
            fixture.Profile.Name, SettingsProfileVersion.Parse("1.0.1"), fixture.Profile.Definitions);
        DefaultSettingsProfile changedDigest = DefaultSettingsProfile.Create(fixture.Profile.Id,
            "same-version-different-profile", fixture.Profile.Version, fixture.Profile.Definitions);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(profile: alien));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(profile: changedVersion));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(profile: changedDigest));
        var differentVersion = new TimelineMap(fixture.Timeline.Id, EntityId<CompositionVersionIdTag>.New(), fixture.Timeline.Entries);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(timeline: differentVersion));
        var differentCuts = new TimelineMap(fixture.Timeline.Id, fixture.Version.Id,
            [new TimelineMapEntry(Range(0, 1), fixture.Timeline.Entries[0].Origin, Range(0, 1))]);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(timeline: differentCuts));
        var badDigest = new CompositionVersion(fixture.Version.Id, fixture.Version.ProjectId, null, Actor, Created,
            "bad digest test", Hash("not the composition"), fixture.Version.Elements);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(version: badDigest));
        var wrongLayout = new LayoutSpec(EntityId<LayoutSpecIdTag>.New(), 16, 9, fixture.Layout.Regions, fixture.Layout.SafeZones);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(layout: wrongLayout));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(font: new RenderFontEvidence("Renamed font", fixture.Font.FileDigest)));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(actorSid: "not-a-sid"));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(createdAt: Created.AddSeconds(-1)));
    }

    [TestMethod]
    public void ChangedSnapshotValuesAreAppliedRatherThanReplacingThemWithProfileDefaults()
    {
        var fixture = new Fixture([Utterance("あいうえおかきく", Range(0, 2))]);
        StandardSubtitleBuildResult baseline = fixture.Result();
        DefaultSettingsSnapshot strict = fixture.WithSettings(("subtitles.readingSpeedMaximum", DefaultSettingValue.FromInteger(3)));

        StandardSubtitleBuildResult result = fixture.Result(settings: strict);

        Assert.IsFalse(baseline.HasUnresolvedCues, Describe(baseline));
        Assert.IsTrue(result.HasUnresolvedCues);
        Assert.HasCount(0, result.Subtitles.Revisions);
        Assert.AreEqual(strict.ContentDigest, result.SettingsDigest);
        Assert.AreNotEqual(baseline.Subtitles.DisplayProfile.VersionDigest, result.Subtitles.DisplayProfile.VersionDigest);
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Key == "subtitles.readingSpeedMaximum" && warning.IsUnresolved));
        DefaultSettingsSnapshot disabled = fixture.WithSettings(("subtitles.enabled", DefaultSettingValue.FromBoolean(false)));
        StandardSubtitleBuildResult none = fixture.Result(settings: disabled);
        Assert.HasCount(0, none.Subtitles.Revisions);
        Assert.IsFalse(none.HasUnresolvedCues);
        Assert.IsTrue(none.Warnings.Any(static warning => warning.Reason == "disabled-by-settings"));
        DefaultSettingsSnapshot backwards = fixture.WithSettings(("subtitles.cueMaximumDuration", DefaultSettingValue.FromInteger(1)),
            ("subtitles.cueMinimumDuration", DefaultSettingValue.FromRational(2, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Result(settings: backwards));
    }

    [TestMethod]
    public void TranscriptDigestAndSourceTokenOrderRemainMandatoryForMachineInput()
    {
        var fixture = new Fixture([new UtteranceInput(Range(0, 2),
            [new TokenInput("一", Range(0, 1)), new TokenInput("二", Range(1, 1))])]);
        Assert.AreEqual(SubtitleSegmentationFailureReason.RevisionDigestMismatch,
            Assert.ThrowsExactly<SubtitleSegmentationException>(() => fixture.Result(transcriptDigest: Hash("wrong"))).Reason);
        SourceSpan span = fixture.Spans[0];
        var reversed = new SourceSpan(span.Id, span.AssetId, span.StreamId, span.Range, span.SourceDigest,
            span.TranscriptRevisionId, span.TranscriptTokenIds.Reverse().ToArray());
        Assert.AreEqual(SubtitleSegmentationFailureReason.SourceSpanBindingMismatch,
            Assert.ThrowsExactly<SubtitleSegmentationException>(() => fixture.Result(spans: [reversed])).Reason);
    }

    [TestMethod]
    public void VerticalSettingsUseActualRegionLineHeightAndLengthWithInwardPixelRounding()
    {
        var fixture = new Fixture([Utterance(new string('声', 11), Range(0, 2))], vertical: true);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.IsFalse(result.HasUnresolvedCues, Describe(result));
        AssSubtitleProfile ass = result.Subtitles.AssProfile;
        Assert.AreEqual(1080, ass.PlayResolutionWidth);
        Assert.AreEqual(1920, ass.PlayResolutionHeight);
        Assert.IsTrue(ass.Styles.All(static style => style.FontSize == 80));
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Key == "subtitles.verticalLineHeight"
            && warning.Reason == "line-height-rounded-inward-to-pixels"));
        SubtitleLayoutRect rect = result.Subtitles.Revisions[0].Snapshot.ResolvedPlacement!.Rect;
        Assert.AreEqual(new RationalNumber(1, 20), rect.X);
        Assert.AreEqual(new RationalNumber(9, 10), rect.Width);
        Assert.AreEqual(new RationalNumber(19, 24), rect.Y);
        Assert.AreEqual(new RationalNumber(1, 12), rect.Height);
        AssertBounds(result.Subtitles, fixture.RenderProfile, 11, 2, 7, Time(5, 6), Time(7));
    }

    [TestMethod]
    public void AbsentPlacementOrSafeZoneIsAnUnresolvedWarningNotAValidCue()
    {
        var fixture = new Fixture([Utterance("配置。", Range(0, 2))]);
        var absentRegion = new LayoutSpec(fixture.Layout.Id, 16, 9, [], fixture.Layout.SafeZones);
        var absentSafeZone = new LayoutSpec(fixture.Layout.Id, 16, 9, fixture.Layout.Regions, []);

        StandardSubtitleBuildResult noRegion = fixture.Result(layout: absentRegion);
        StandardSubtitleBuildResult noSafeZone = fixture.Result(layout: absentSafeZone);

        Assert.HasCount(0, noRegion.Subtitles.Revisions);
        Assert.HasCount(0, noSafeZone.Subtitles.Revisions);
        Assert.IsTrue(noRegion.Warnings.Any(static warning => warning.Reason == "layout-not-resolved" && warning.IsUnresolved));
        Assert.IsTrue(noSafeZone.Warnings.Any(static warning => warning.Reason == "no-versioned-safe-zone" && warning.IsUnresolved));
    }

    [TestMethod]
    public void RendererLimitHasExplicitWarningsForEveryExcludedCue()
    {
        int count = StandardSubtitleInput.MaximumCueCount + 1;
        TokenInput[] tokens = Enumerable.Range(0, count).Select(index =>
            new TokenInput($"声{index:D6}。", Range(index * 2, 1))).ToArray();
        var fixture = new Fixture([new UtteranceInput(Range(0, count * 2), tokens)]);

        StandardSubtitleBuildResult result = fixture.Result();

        Assert.HasCount(StandardSubtitleInput.MaximumCueCount, result.Subtitles.Revisions);
        Assert.IsTrue(result.HasUnresolvedCues);
        StandardSubtitleBuildWarning warning = result.Warnings.Single(static warning => warning.Reason == "renderer-cue-limit");
        Assert.AreEqual(Range((count - 1) * 2, 1), warning.SourceRange);
        Assert.AreEqual(fixture.Spans[0].Id, warning.SourceSpanId);
        Assert.IsTrue(warning.IsUnresolved);
    }

    [TestMethod]
    public void CancellationAndNullValidationDoNotReturnPartiallyEvaluatedSubtitles()
    {
        var fixture = new Fixture([Utterance("取消。", Range(0, 2))]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Result(cancellationToken: cancellation.Token));
        Assert.ThrowsExactly<ArgumentNullException>(() => StandardSubtitleBuilder.BuildWithEvidence(fixture.Version,
            fixture.Timeline, fixture.Layout, fixture.RenderProfile, null!, fixture.TranscriptDigest,
            fixture.Spans, fixture.Profile, fixture.Settings, fixture.Font, Actor, Created));
        StandardSubtitleBuildResult result = fixture.Result();
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StandardSubtitleBuildWarning>)result.Warnings).Clear());
    }

    private static void AssertBounds(StandardSubtitleInput input, RenderProfile render, int characters, int lines,
        int speed, RationalTime minimum, RationalTime maximum)
    {
        SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(input.DisplayProfile,
            input.Revisions.Select(static revision => SubtitleDisplayMapper.MapSource(revision.Snapshot)).ToArray(), []);
        Assert.HasCount(0, display.OmittedCueIds);
        RationalTime? previousEnd = null;
        foreach (SubtitleDisplayedCue cue in display.DisplayedCues)
        {
            Assert.IsTrue(cue.Cue.Range.Start >= default(RationalTime));
            Assert.IsTrue(cue.Cue.Range.Duration >= minimum && cue.Cue.Range.Duration <= maximum);
            string[] textLines = cue.DisplayText.Split('\n');
            Assert.IsTrue(textLines.Length <= lines);
            Assert.IsTrue(textLines.All(line => LineHalfWidthUnits(line) <= 2L * characters));
            // Reading speed is intentionally NOT the half-width line-length metric.
            int count = textLines.Sum(static line => StringInfo.ParseCombiningCharacters(line).Length);
            Assert.IsTrue(new RationalTime(count, speed) <= cue.Cue.Range.Duration);
            if (previousEnd is RationalTime end)
                Assert.IsTrue(cue.Cue.Range.Start - end >= RationalTime.FromFrames(2, render.FrameRateNumerator, render.FrameRateDenominator));
            previousEnd = cue.Cue.Range.End;
        }
    }

    private static long LineHalfWidthUnits(string line)
    {
        long units = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(line);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            units += element.All(static character => character is >= '\u0020' and <= '\u007E'
                or >= '\uFF61' and <= '\uFF9F') ? element.Length : 2;
        }

        return units;
    }

    private static string Describe(StandardSubtitleBuildResult result) => string.Join("\n", result.Warnings.Select(static warning =>
        $"{warning.Key}|{warning.Rule}|{warning.Reason}|{warning.IsUnresolved}|{warning.SourceRange}|{warning.SourceSpanId}"));
    private static ContentDigest Hash(string value) => ContentDigest.Compute(Encoding.UTF8.GetBytes(value));
    private static RationalTime Time(long value, long rate = 1) => new(value, rate);
    private static TimeRange Range(long start, long duration, long rate = 1) => new(Time(start, rate), Time(duration, rate));
    private static UtteranceInput Utterance(string text, TimeRange range) => new(range, [new TokenInput(text, range)]);
    private sealed record TokenInput(string Text, TimeRange? Range);
    private sealed record UtteranceInput(TimeRange Range, IReadOnlyList<TokenInput> Tokens, string? Text = null);

    // Pure typed test evidence. No worker, file promotion, DB, user profile, or live ASR is invoked.
    private sealed class Fixture
    {
        internal Fixture(IReadOnlyList<UtteranceInput> inputs, TranscriptChangeSource changeSource = TranscriptChangeSource.Machine,
            bool vertical = false, RationalNumber? frameRate = null)
        {
            Profile = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            Settings = Profile.CreateSnapshot();
            BundledRenderProfileDefinition reference = BundledRenderProfileLoader.Load(File.ReadAllBytes(RepositoryPath(vertical
                ? "src/HubRadio.App/Profiles/9x16/vertical-short.json" : "src/HubRadio.App/Profiles/16x9/long-form.json")));
            RationalNumber rate = frameRate ?? new RationalNumber(30, 1);
            BundledRenderProfileDefinition render = vertical
                ? StandardRenderProfileFactory.CreateVerticalShort(Profile, Settings, reference, rate)
                : StandardRenderProfileFactory.CreateLandscape(Profile, Settings, reference, rate);
            Layout = render.Layout;
            RenderProfile = render.Profile;
            var utterances = new List<Utterance>();
            var spans = new List<SourceSpan>();
            EntityId<TranscriptRevisionIdTag> revisionId = EntityId<TranscriptRevisionIdTag>.New();
            EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
            EntityId<TranscriptSpeakerIdTag> speakerId = EntityId<TranscriptSpeakerIdTag>.New();
            ContentDigest sourceDigest = Hash("test source evidence");
            foreach (UtteranceInput input in inputs)
            {
                Token[] tokens = input.Tokens.Select(static token => new Token(EntityId<TranscriptTokenIdTag>.New(), token.Text, token.Range, null)).ToArray();
                string text = input.Text ?? string.Concat(tokens.Select(static token => token.Text));
                EntityId<SourceSpanIdTag> spanId = EntityId<SourceSpanIdTag>.New();
                utterances.Add(new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), spanId, speakerId, "Speaker 1", text,
                    input.Range, null, [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), text, tokens)]));
                spans.Add(new SourceSpan(spanId, assetId, "audio:1", input.Range, sourceDigest, revisionId,
                    tokens.Select(static token => token.Id).ToArray()));
            }

            Transcript = new TranscriptRevision(revisionId,
                changeSource == TranscriptChangeSource.Human ? EntityId<TranscriptRevisionIdTag>.New() : null,
                changeSource, Created, utterances);
            TranscriptDigest = Hash(TranscriptSnapshotJson.WriteRevision(Transcript));
            Spans = spans.ToArray();
            Version = CutVersion(Spans.Select(static span => (span, span.Range)).ToArray());
            Timeline = TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), Version);
        }

        internal DefaultSettingsProfile Profile { get; }
        internal DefaultSettingsSnapshot Settings { get; }
        internal TranscriptRevision Transcript { get; }
        internal ContentDigest TranscriptDigest { get; }
        internal SourceSpan[] Spans { get; }
        internal LayoutSpec Layout { get; }
        internal RenderProfile RenderProfile { get; }
        internal CompositionVersion Version { get; }
        internal TimelineMap Timeline { get; }
        internal RenderFontEvidence Font { get; } = new("Noto Sans JP", Hash("font evidence only; not actual font bytes"));
        private EntityId<ProjectIdTag> ProjectId { get; } = EntityId<ProjectIdTag>.New();

        internal CompositionVersion CutVersion(params (SourceSpan Span, TimeRange Range)[] cuts)
        {
            RationalTime cursor = default;
            var elements = new List<CompositionElement>();
            foreach ((SourceSpan span, TimeRange range) in cuts)
            {
                elements.Add(new CompositionElement(EntityId<CompositionElementIdTag>.New(), new TimeRange(cursor, range.Duration),
                    OriginRef.FromSourceSpan(span.Id), sourceRange: range));
                cursor += range.Duration;
            }

            return new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), ProjectId, null, Actor, Created,
                "Test source cuts", RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements), elements);
        }

        internal DefaultSettingsSnapshot WithSettings(params (string Key, DefaultSettingValue Value)[] overrides)
        {
            var byKey = overrides.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            return Profile.CreateSnapshot(Settings.Values.Select(selection => new DefaultSettingSelection(selection.Key,
                byKey.TryGetValue(selection.Key, out DefaultSettingValue? replacement) ? replacement : selection.Value)).ToArray());
        }

        internal StandardSubtitleBuildResult Result(CompositionVersion? version = null, TimelineMap? timeline = null,
            LayoutSpec? layout = null, DefaultSettingsProfile? profile = null, DefaultSettingsSnapshot? settings = null,
            TranscriptRevision? transcript = null, ContentDigest? transcriptDigest = null, IReadOnlyList<SourceSpan>? spans = null,
            RenderFontEvidence? font = null, string actorSid = Actor, DateTimeOffset? createdAt = null,
            CancellationToken cancellationToken = default) => StandardSubtitleBuilder.BuildWithEvidence(version ?? Version,
                timeline ?? (version is null ? Timeline : TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), version)),
                layout ?? Layout, RenderProfile, transcript ?? Transcript,
                transcriptDigest ?? (transcript is null ? TranscriptDigest : Hash(TranscriptSnapshotJson.WriteRevision(transcript))),
                spans ?? Spans, profile ?? Profile, settings ?? Settings, font ?? Font, actorSid, createdAt ?? Created, cancellationToken);

        internal StandardSubtitleInput Build() => StandardSubtitleBuilder.Build(Version, Timeline, Layout, RenderProfile,
            Transcript, TranscriptDigest, Spans, Profile, Settings, Font, Actor, Created);
    }

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            directory = directory.Parent;
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName,
            relative.Replace('/', Path.DirectorySeparatorChar));
    }
}