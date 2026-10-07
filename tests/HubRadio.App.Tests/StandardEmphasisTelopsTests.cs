using System.Buffers.Binary;
using HubRadio.App.Rendering;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardEmphasisTelopsTests
{
    private static readonly OriginRef Origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());

    // FR-059 AC-034.1: a telop is a verbatim number phrase (with its counter) from a displayed cue.
    [TestMethod]
    [TestCategory("FR-059")]
    [DataRow("10分だから結構なるようですよ", "10分")]
    [DataRow("だいたい10本くらい出した", "10本")]
    [DataRow("エナミさんとは第1回に取りました", "第1回")]
    [DataRow("売上が30%増えた", "30%")]
    [DataRow("１２３円です", "１２３円")]
    [DataRow("3.5倍になった", "3.5倍")]
    [DataRow("こんにちは", null)]
    [DataRow("1", null)]
    public void NumberPhraseKeepsTheNumberAndItsCounterVerbatim(string text, string? expected)
    {
        Assert.AreEqual(expected, StandardEmphasisTelops.NumberPhrase(text));
        if (expected is not null)
        {
            StringAssert.Contains(text, expected);
        }
    }

    // FR-059 AC-034.2: the amount setting caps the count; telops never overlap and start 5 s apart.
    [TestMethod]
    [TestCategory("FR-059")]
    public void SelectionFollowsTheAmountAndSpacing()
    {
        StandardDisplayedCueText[] cues =
        [
            Cue(2, 2, "10分だから"),
            Cue(5, 2, "第1回に取りました"),
            Cue(20, 1, "本当ですね"),
            Cue(30, 2, "ありがとうございます"),
            Cue(45, 2, "だいたい10本くらい"),
            Cue(60, 1, "すごい！"),
            Cue(70, 1, "誰ですかね？"),
        ];
        RationalTime duration = new(77, 1);

        Assert.IsEmpty(StandardEmphasisTelops.Select(cues, null, duration));
        IReadOnlyList<StandardEmphasisTelop> few = StandardEmphasisTelops.Select(cues, "few", duration);
        CollectionAssert.AreEqual(new[] { "10分" }, few.Select(static item => item.Text).ToArray());
        IReadOnlyList<StandardEmphasisTelop> standard = StandardEmphasisTelops.Select(cues, "standard", duration);
        // 第1回 starts 3 s after 10分 and is skipped; the second number wins over emphasis words.
        CollectionAssert.AreEqual(new[] { "10分", "10本" }, standard.Select(static item => item.Text).ToArray());
        IReadOnlyList<StandardEmphasisTelop> many = StandardEmphasisTelops.Select(cues, "many", duration);
        CollectionAssert.AreEqual(new[] { "10分", "本当ですね", "ありがとうございます", "10本", "すごい！" },
            many.Select(static item => item.Text).ToArray());
        Assert.IsTrue(many.All(item => cues.Any(cue => cue.OutputRange == item.OutputRange && cue.Text.Contains(item.Text, StringComparison.Ordinal))));
        Assert.IsTrue(many.All(static item => item.Rule.StartsWith(StandardEmphasisTelops.RuleVersion, StringComparison.Ordinal)));
        Assert.AreEqual(1, StandardEmphasisTelops.Limit("few", duration));
        Assert.AreEqual(2, StandardEmphasisTelops.Limit("standard", duration));
        Assert.AreEqual(5, StandardEmphasisTelops.Limit("many", duration));
        Assert.AreEqual(10, StandardEmphasisTelops.Limit("few", new RationalTime(600, 1)));

        // A cue split mid-sentence keeps only the verbatim part that starts at the emphasis word.
        CollectionAssert.AreEqual(new[] { "本当ですね" }, StandardEmphasisTelops.Select(
            [Cue(1, 2, "んですよ本当ですね")], "standard", duration).Select(static item => item.Text).ToArray());
    }

    // FR-060 AC-035.1: sounds at telops and at transition openers, 2 s apart, inside the output, only when enabled.
    [TestMethod]
    [TestCategory("FR-060")]
    public void SoundEffectsFollowTelopsAndTransitionOpeners()
    {
        StandardDisplayedCueText[] cues =
        [
            Cue(0, 2, "じゃあ始めます"),
            Cue(5, 2, "じゃあいきますか"),
            Cue(6, 1, "まずは"),
            Cue(20, 2, "10本です"),
            Cue(40, 2, "さて次の話"),
            Cue(76, 1, "では"),
        ];
        RationalTime duration = new(77, 1);
        IReadOnlyList<StandardEmphasisTelop> telops = StandardEmphasisTelops.Select(cues, "standard", duration);

        Assert.IsEmpty(StandardEmphasisTelops.PlanSoundEffects(false, cues, telops, duration));
        IReadOnlyList<StandardSoundEffectCue> planned = StandardEmphasisTelops.PlanSoundEffects(true, cues, telops, duration);
        CollectionAssert.AreEqual(
            new[]
            {
                (StandardSoundEffectKind.Transition, new RationalTime(5, 1)),
                (StandardSoundEffectKind.Emphasis, new RationalTime(20, 1)),
                (StandardSoundEffectKind.Transition, new RationalTime(40, 1)),
                (StandardSoundEffectKind.Transition, new RationalTime(76, 1)),
            },
            planned.Select(static item => (item.Kind, item.OutputStart)).ToArray());
        Assert.IsTrue(planned.All(item => item.OutputStart + StandardEmphasisTelops.SoundEffectDuration <= duration));
    }

    // FR-060 AC-035.2: the effects are self-generated, deterministic 48 kHz stereo PCM WAV files.
    [TestMethod]
    [TestCategory("FR-060")]
    public void SynthesizedSoundEffectsAreDeterministicPcmWav()
    {
        foreach (StandardSoundEffectKind kind in Enum.GetValues<StandardSoundEffectKind>())
        {
            byte[] first = StandardSoundEffectSynthesizer.Create(kind);
            CollectionAssert.AreEqual(first, StandardSoundEffectSynthesizer.Create(kind));
            Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(first, 0, 4));
            Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(first, 8, 4));
            Assert.AreEqual(48000, BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(24)));
            Assert.AreEqual(2, BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(22)));
            Assert.AreEqual(44 + (48000 * 3 / 10 * 4), first.Length);
            Assert.IsTrue(StandardSoundEffectSynthesizer.FileName(kind).EndsWith(".wav", StringComparison.Ordinal));
            // Peak at most half of full scale, so the effect stays under the speech.
            short peak = 0;
            for (int offset = 44; offset < first.Length; offset += 2)
                peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(first.AsSpan(offset))));
            Assert.IsTrue(peak is > 1000 and <= short.MaxValue / 2 + 1, kind.ToString());
        }

        Assert.AreNotEqual(StandardSoundEffectSynthesizer.Digest(StandardSoundEffectKind.Emphasis),
            StandardSoundEffectSynthesizer.Digest(StandardSoundEffectKind.Transition));
    }

    private static StandardDisplayedCueText Cue(long start, long duration, string text) =>
        new(new TimeRange(new RationalTime(start, 1), new RationalTime(duration, 1)), text, Origin);
}
