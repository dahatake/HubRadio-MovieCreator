using HubRadio.App.DefaultSettings;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BuzzEditingPresetTests
{
    private static DefaultSettingsProfile Profile(string version = "1.2.0") => BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(
        RepositoryPath($"src/HubRadio.App/DefaultSettings/Profiles/{version}.json")));

    // FR-054 FR-057 FR-059 AC-029.6: pacing and emphasis follow the orientation and length of the source.
    [TestMethod]
    [TestCategory("FR-057")]
    [TestCategory("FR-059")]
    [DataRow(77.4, 1080, 1920, "strong", "many")]
    [DataRow(600.0, 1080, 1920, "strong", "few")]
    [DataRow(77.4, 1920, 1080, null, null)]
    [DataRow(600.0, 1920, 1080, null, "few")]
    public void RecommendsPacingAndEmphasisByOrientationAndDuration(double seconds, int width, int height, string? pause, string? emphasis)
    {
        var byKey = BuzzEditingPreset.Recommend(new SourceMediaFacts(seconds, width, height)).ToDictionary(static item => item.Key);

        Assert.AreEqual(pause, byKey.TryGetValue("editing.pauseTightening", out AutoSettingRecommendation? p) ? p.Value.TextValue : null);
        Assert.AreEqual(emphasis, byKey.TryGetValue("subtitles.emphasisTelopAmount", out AutoSettingRecommendation? e) ? e.Value.TextValue : null);
        Assert.IsFalse(byKey.ContainsKey("audio.soundEffectsEnabled"));
        Assert.IsFalse(byKey.ContainsKey("output.longFormOrientation"));
    }

    // FR-054 AC-029.3: a profile older than 1.2.0 has no pacing or emphasis keys, so those recommendations are skipped.
    [TestMethod]
    public void ApplySkipsKeysTheProfileDoesNotDefine()
    {
        DefaultSettingsProfile previous = Profile("1.1.0");
        (DefaultSettingsSnapshot tuned, IReadOnlyList<AutoSettingRecommendation> applied) = BuzzEditingPreset.Apply(
            previous, previous.CreateSnapshot(), BuzzEditingPreset.Recommend(new SourceMediaFacts(77.4, 1080, 1920)));

        Assert.AreEqual(previous.ContentDigest, tuned.ProfileDigest);
        CollectionAssert.DoesNotContain(applied.Select(static item => item.Key).ToArray(), "editing.pauseTightening");
        CollectionAssert.DoesNotContain(applied.Select(static item => item.Key).ToArray(), "subtitles.emphasisTelopAmount");
        CollectionAssert.Contains(applied.Select(static item => item.Key).ToArray(), "editing.shortHookMaximumStart");
    }

    // FR-054 AC-029.1: the preset is a fixed, offline table driven only by duration and orientation.
    [TestMethod]
    [DataRow(30.0, 1920, 1080, 1, false)]
    [DataRow(77.4, 1920, 1080, 2, false)]
    [DataRow(77.4, 1080, 1920, 3, false)]
    [DataRow(30.0, 1080, 1920, 1, false)]
    [DataRow(600.0, 1920, 1080, 3, true)]
    public void RecommendsByDurationAndOrientation(double seconds, int width, int height, int shorts, bool endScreen)
    {
        IReadOnlyList<AutoSettingRecommendation> recommendations =
            BuzzEditingPreset.Recommend(new SourceMediaFacts(seconds, width, height));
        var byKey = recommendations.ToDictionary(static item => item.Key);

        Assert.AreEqual(shorts, byKey["project.verticalShortCandidateCount"].Value.IntegerValue);
        Assert.AreEqual(2L, byKey["editing.shortHookMaximumStart"].Value.IntegerValue);
        Assert.AreEqual(9L, byKey["subtitles.verticalLineLengthMaximum"].Value.IntegerValue);
        Assert.AreEqual(endScreen, byKey.ContainsKey("output.endScreenDuration"));
        Assert.IsTrue(recommendations.All(static item => item.Label.Length > 0 && item.ValueText.Length > 0
            && item.Reason.Length > 0 && !item.Reason.Contains("http", StringComparison.Ordinal)));
        Assert.HasCount(0, BuzzEditingPreset.Recommend(new SourceMediaFacts(double.NaN, width, height)));
        Assert.HasCount(0, BuzzEditingPreset.Recommend(new SourceMediaFacts(seconds, 0, height)));
    }

    // FR-054 AC-029.2: every value the preset can choose is a choice the settings screen offers (FR-050),
    // so each tuned Project is a combination the pipeline is known to build.
    [TestMethod]
    public void EveryRecommendedValueIsAnOfferedSettingsChoice()
    {
        DefaultSettingsProfile profile = Profile();
        var offered = SettingEditorCatalogTests.OfferedChoices(profile)
            .Select(static item => (item.Key, SettingEditorCatalogTests.Describe(item.Value))).ToHashSet();
        foreach (var facts in new[] { new SourceMediaFacts(10, 1080, 1920), new SourceMediaFacts(100, 1920, 1080),
            new SourceMediaFacts(1000, 1920, 1080), new SourceMediaFacts(1000, 1080, 1920) })
        {
            foreach (AutoSettingRecommendation recommendation in BuzzEditingPreset.Recommend(facts))
            {
                Assert.Contains((recommendation.Key, SettingEditorCatalogTests.Describe(recommendation.Value)), offered,
                    $"{recommendation.Key}={SettingEditorCatalogTests.Describe(recommendation.Value)}");
            }
        }
    }

    // FR-054 AC-029.3: only values still equal to the profile default change; a user's own choice is kept.
    [TestMethod]
    public void ApplyChangesOnlyDefaultValuedKeys()
    {
        DefaultSettingsProfile profile = Profile();
        DefaultSettingsSnapshot defaults = profile.CreateSnapshot();
        DefaultSettingsSnapshot userChanged = profile.CreateSnapshot(defaults.Values.Select(item =>
            item.Key == "subtitles.verticalLineLengthMaximum"
                ? new DefaultSettingSelection(item.Key, DefaultSettingValue.FromInteger(10)) : item).ToArray());
        IReadOnlyList<AutoSettingRecommendation> recommendations =
            BuzzEditingPreset.Recommend(new SourceMediaFacts(77.4, 1080, 1920));

        (DefaultSettingsSnapshot tuned, IReadOnlyList<AutoSettingRecommendation> applied) =
            BuzzEditingPreset.Apply(profile, userChanged, recommendations);

        var values = tuned.Values.ToDictionary(static item => item.Key, static item => item.Value);
        Assert.AreEqual(10L, values["subtitles.verticalLineLengthMaximum"].IntegerValue);
        Assert.AreEqual(2L, values["editing.shortHookMaximumStart"].IntegerValue);
        CollectionAssert.DoesNotContain(applied.Select(static item => item.Key).ToArray(), "subtitles.verticalLineLengthMaximum");
        // project.verticalShortCandidateCount defaults to 3 already, so recommending 3 changes nothing.
        CollectionAssert.DoesNotContain(applied.Select(static item => item.Key).ToArray(), "project.verticalShortCandidateCount");
        Assert.AreEqual(profile.ContentDigest, tuned.ProfileDigest);
        Assert.AreEqual(defaults.Values.Count, tuned.Values.Count);
        (DefaultSettingsSnapshot same, IReadOnlyList<AutoSettingRecommendation> none) = BuzzEditingPreset.Apply(profile, tuned, recommendations);
        Assert.AreSame(tuned, same);
        Assert.HasCount(0, none);
    }

    // FR-054 AC-029.1: facts come from ffprobe; a quarter-turn rotation swaps the displayed size.
    [TestMethod]
    public void ProbeParsesDurationSizeAndRotation()
    {
        SourceMediaFacts plain = SourceMediaProbe.Parse(
            """{"streams":[{"width":1080,"height":1920,"tags":{}}],"format":{"duration":"77.366667"}}""")!;
        Assert.AreEqual(77.366667, plain.DurationSeconds, 1e-9);
        Assert.IsTrue(plain.IsVertical);
        SourceMediaFacts rotated = SourceMediaProbe.Parse(
            """{"streams":[{"width":1920,"height":1080,"side_data_list":[{"rotation":-90}]}],"format":{"duration":"12.5"}}""")!;
        Assert.AreEqual((1080, 1920), (rotated.DisplayWidth, rotated.DisplayHeight));
        SourceMediaFacts tagged = SourceMediaProbe.Parse(
            """{"streams":[{"width":1920,"height":1080,"tags":{"rotate":"90"}}],"format":{"duration":"1"}}""")!;
        Assert.IsTrue(tagged.IsVertical);
        foreach (string bad in new[] { "", "{}", """{"streams":[],"format":{"duration":"1"}}""",
            """{"streams":[{"width":0,"height":1}],"format":{"duration":"1"}}""",
            """{"streams":[{"width":1,"height":1}],"format":{"duration":"N/A"}}""" })
        {
            Assert.IsNull(SourceMediaProbe.Parse(bad), bad);
        }

        CollectionAssert.AreEqual(new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=width,height:stream_side_data=rotation:stream_tags=rotate:format=duration", "-of", "json", @"C:\a.mp4" },
            SourceMediaProbe.BuildArguments(@"C:\a.mp4").ToArray());
    }

    // FR-054 AC-029.4: the explanation record round-trips and an unreadable record is treated as absent.
    [TestMethod]
    public void AutoSettingsRecordRoundTripsAndRejectsDamage()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "hubradio-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var store = new ProjectAutoSettingsStore(workspace);
            var draftId = EntityId<ProjectCreationDraftIdTag>.New();
            Assert.IsNull(store.Read(draftId));
            IReadOnlyList<AutoSettingRecommendation> applied =
                BuzzEditingPreset.Recommend(new SourceMediaFacts(30, 1080, 1920));
            store.Save(draftId, new SourceMediaFacts(30, 1080, 1920), applied);

            IReadOnlyList<AutoSettingNote> notes = store.Read(draftId)!;
            CollectionAssert.AreEqual(applied.Select(static item => item.Key).ToArray(), notes.Select(static item => item.Key).ToArray());
            Assert.AreEqual(applied[0].Reason, notes[0].Reason);

            File.WriteAllText(store.PathFor(draftId), """{"schema":"other","applied":[]}""");
            Assert.IsNull(store.Read(draftId));
            File.WriteAllText(store.PathFor(draftId), "{");
            Assert.IsNull(store.Read(draftId));
            store.Delete(draftId);
            Assert.IsFalse(File.Exists(store.PathFor(draftId)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
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
