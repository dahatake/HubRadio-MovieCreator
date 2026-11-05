using HubRadio.App.StyleTemplates;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-145 AC-186: the speaker count (FR-003) and the speech speed (FR-002) the automatic template selection uses come from the local transcript;
/// when they cannot be had they are 不明 and only length / aspect ratio decide. AC-182 / AC-184 / AC-180: the three-valued origin.
/// </summary>
[TestClass]
public sealed class TemplateSpeechFactsTests
{
    private string _temp = null!;

    [TestInitialize]
    public void Initialize() =>
        _temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-speechfacts-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, recursive: true);

    private static TimeRange Range(long start, long duration) => new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static Utterance Utter(EntityId<TranscriptSpeakerIdTag> speaker, string text, long start, long seconds)
    {
        TimeRange range = Range(start, seconds);
        return new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), EntityId<SourceSpanIdTag>.New(), speaker, "話者", text, range, null,
            [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), text, [new Token(EntityId<TranscriptTokenIdTag>.New(), "語", range, null)])]);
    }

    private static TranscriptRevision Revision(params Utterance[] utterances) =>
        new(EntityId<TranscriptRevisionIdTag>.New(), null, TranscriptChangeSource.Machine, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), utterances);

    // FR-145 AC-186: speakers = distinct identified speakers; pace = characters (Japanese) or words per minute of the spoken time.
    [TestMethod]
    [TestCategory("FR-145")]
    public void TakesTheSpeakerCountAndTheSpeedFromTheTranscript()
    {
        EntityId<TranscriptSpeakerIdTag> a = EntityId<TranscriptSpeakerIdTag>.New();
        EntityId<TranscriptSpeakerIdTag> b = EntityId<TranscriptSpeakerIdTag>.New();
        string thirtyChars = string.Concat(Enumerable.Repeat("あいうえお", 6));
        TemplateSpeechFacts facts = TemplateSpeechFacts.From(Revision(Utter(a, thirtyChars, 0, 6), Utter(b, thirtyChars, 6, 6), Utter(a, thirtyChars, 12, 6)));
        Assert.AreEqual(2, facts.SpeakerCount);
        Assert.AreEqual(300.0, facts.WordsPerMinute); // 90 characters in 18 seconds = 300 per minute
        Assert.AreEqual(3, TemplateSpeechFacts.CountUnits("one two  three"));
        Assert.AreEqual(5, TemplateSpeechFacts.CountUnits("こんにちは。"));
    }

    // FR-145 AC-186: an empty or missing transcript is 不明 (not zero).
    [TestMethod]
    [TestCategory("FR-145")]
    public void AnEmptyTranscriptIsUnknown()
    {
        Assert.AreEqual(TemplateSpeechFacts.Unknown, TemplateSpeechFacts.From(null));
        Assert.AreEqual(TemplateSpeechFacts.Unknown, TemplateSpeechFacts.From(Revision()));
    }

    // FR-145 AC-186: the facts are waited for only up to the limit and then 不明; a stored fact is returned at once; a cancel ends the wait.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task WaitsForTheFactsAtMostTheLimitThenTheyAreUnknown()
    {
        var store = new TemplateSpeechFactsStore(Path.Combine(_temp, "speech"));
        var key = Guid.NewGuid();
        Assert.AreEqual(TemplateSpeechFacts.Unknown, await store.WaitAsync(key, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        store.Save(key, new TemplateSpeechFacts(2, 320));
        Assert.AreEqual(new TemplateSpeechFacts(2, 320), await store.WaitAsync(key, TimeSpan.FromSeconds(5), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.AreEqual(TemplateSpeechFacts.Unknown, await store.WaitAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), cancelled.Token));
        store.Delete(key);
        Assert.IsNull(store.Load(key));
    }

    private static async Task<TemplateAutoSelectionRecord> Analyze(TemplateAutoSelectionStore store, TemplateAnalysisInput input, int portraits = 0)
    {
        var key = Guid.NewGuid();
        await TemplateAutoSelectionRunner.Start(store, key, true, _ => Task.FromResult<TemplateAnalysisInput?>(input), () => portraits).WaitAsync(TimeSpan.FromSeconds(10));
        return store.Load(key)!;
    }

    // FR-145 AC-186: the values used are in the reason; speech facts that are 不明 are said so and length / aspect ratio still choose.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task TheReasonShowsTheValuesUsedOrThatTheyAreUnknown()
    {
        var store = new TemplateAutoSelectionStore(Path.Combine(_temp, "auto"));
        TemplateAutoSelectionRecord known = await Analyze(store, new TemplateAnalysisInput(600, 1920, 1080, true, 2, 180));
        Assert.AreEqual("talk-show", known.TemplateId);
        StringAssert.Contains(known.Reason, "話者 2 人");
        StringAssert.Contains(known.Reason, "毎分約 180");
        TemplateAutoSelectionRecord unknown = await Analyze(store, new TemplateAnalysisInput(600, 1920, 1080, true));
        Assert.AreEqual(TemplateAutoSelectionState.Selected, unknown.State);
        Assert.AreEqual("explainer", unknown.TemplateId);
        StringAssert.Contains(unknown.Reason, "話者数は不明");
        StringAssert.Contains(unknown.Reason, "話の速さは不明");
    }

    // FR-145 AC-182 / AC-184 / AC-180 (and the first Job): the origin is always one of 自動選択 / 手動 / なし.
    [TestMethod]
    [TestCategory("FR-145")]
    public void TheOriginIsOneOfThreeValues()
    {
        StyleTemplateDefinition explainer = StyleTemplateCatalog.Find("explainer")!;
        var selected = new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", explainer.Version, "理由");
        // Skipped (setting off / failure / no fit) and no template chosen: なし. The first Job has no selection to apply: なし.
        Assert.AreEqual(JobTemplateOrigin.None, JobTemplateOrigin.Decide(null, TemplateAutoSelectionRecord.Skipped("x")).Origin);
        Assert.AreEqual(JobTemplateOrigin.None, JobTemplateOrigin.Decide(null, null).Origin);
        // The user explicitly chose "テンプレートなし" (touched) while skipped or selected: 手動, never なし.
        Assert.AreEqual(JobTemplateOrigin.Manual, JobTemplateOrigin.Decide(null, TemplateAutoSelectionRecord.Skipped("x") with { ManualTouched = true }).Origin);
        Assert.AreEqual(JobTemplateOrigin.Manual, JobTemplateOrigin.Decide(null, selected with { ManualTouched = true }).Origin);
        foreach (JobTemplateOrigin origin in new[]
        {
            JobTemplateOrigin.Decide(explainer, selected), JobTemplateOrigin.Decide(null, null), JobTemplateOrigin.Decide(null, selected with { ManualTouched = true }),
        })
        {
            CollectionAssert.Contains(new[] { "auto", "manual", "none" }, origin.Origin);
            CollectionAssert.Contains(new[] { "自動選択", "手動", "なし" }, origin.Label);
        }
    }
}
