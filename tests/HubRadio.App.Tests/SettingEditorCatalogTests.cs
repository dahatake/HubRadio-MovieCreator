using System.Text.Json;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Profiles;
using HubRadio.App.Rendering;
using HubRadio.Core.Layout;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-050 AC-025.5: the settings screen's editor catalog
/// (src/HubRadio.Web/src/features/settings/settingEditorCatalog.json) classifies every shipped key,
/// and every value it offers is accepted by the profile and by the generation code.
/// </summary>
[TestClass]
public sealed class SettingEditorCatalogTests
{
    private static readonly string[] FixedReasons = ["singleValue", "runtimeFixed", "noEffect"];

    // FR-050.7 AC-025.5
    [TestMethod]
    [DataRow("1.0.0")]
    [DataRow("1.1.0")]
    [DataRow("1.2.0")]
    [DataRow("1.3.0")]
    public void SettingEditorCatalogClassifiesEveryShippedKeyExactlyOnce(string version)
    {
        DefaultSettingsProfile profile = LoadProfile(version);
        string[] fixedKeys = Fixed().Select(static item => item.Key).ToArray();
        string[] editorKeys = Editors().Select(static item => item.Key).ToArray();

        // FR-146: profile 1.3.0 adds project.templateAutoSelectEnabled on top of the four 1.2.0 keys.
        Assert.HasCount(version switch { "1.3.0" => 128, "1.2.0" => 127, _ => 123 }, profile.Definitions);
        Assert.IsEmpty(fixedKeys.Intersect(editorKeys, StringComparer.Ordinal).ToArray());
        // FR-057〜FR-060: the catalog also classifies the keys that only profile 1.2.0 defines.
        string[] classified = fixedKeys.Concat(editorKeys).ToArray();
        CollectionAssert.AreEquivalent(
            profile.Definitions.Select(static item => item.Key)
                .Concat(version is "1.0.0" or "1.1.0" ? BundledDefaultSettingsProfileLoader.KeysAddedIn120 : [])
                .Concat(version is "1.3.0" ? [] : BundledDefaultSettingsProfileLoader.KeysAddedIn130)
                .ToArray(),
            classified);
        foreach ((string key, string reason) in Fixed())
            CollectionAssert.Contains(FixedReasons, reason, key);
        Assert.HasCount(27, editorKeys);
    }

    // FR-050.1 AC-025.5
    [TestMethod]
    public void SettingEditorCatalogSingleValueKeysOfferOneValueInTheCurrentProfile()
    {
        DefaultSettingsProfile profile = LoadProfile("1.2.0");
        foreach ((string key, string reason) in Fixed().Where(static item => item.Reason == "singleValue"))
        {
            DefaultSettingDefinition definition = profile.GetDefinition(key);
            bool single = definition.DefaultValue.Kind == DefaultSettingValueKind.Text
                ? definition.AllowedTextValues.Count == 1
                : definition.Minimum is not null && definition.Minimum.Equals(definition.Maximum);
            Assert.IsTrue(single, $"{key} is classified {reason} but offers more than one value.");
        }
    }

    // FR-050.7 AC-025.5
    [TestMethod]
    public void SettingEditorCatalogChoicesAreAcceptedByTheRenderConfigurationAndProfiles()
    {
        DefaultSettingsProfile profile = LoadProfile("1.2.0");
        BundledRenderProfileDefinition longForm = BundledRenderProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/16x9/long-form.json")));
        BundledRenderProfileDefinition vertical = BundledRenderProfileLoader.Load(File.ReadAllBytes(RepositoryPath("src/HubRadio.App/Profiles/9x16/vertical-short.json")));
        var standardRate = new RationalNumber(30, 1);
        var highRate = new RationalNumber(60, 1);
        int checkedValues = 0;
        foreach ((string key, DefaultSettingValue value) in OfferedChoices(profile))
        {
            string label = $"{key}={Describe(value)}";
            DefaultSettingsSnapshot settings = WithValue(profile, key, value);
            StandardRenderConfiguration standard = StandardRenderConfiguration.FromSettings(profile, settings, standardRate, 2);
            StandardRenderConfiguration high = StandardRenderConfiguration.FromSettings(profile, settings, highRate, 1);
            _ = StandardRenderProfileFactory.CreateLandscape(profile, settings, longForm, standardRate);
            _ = StandardRenderProfileFactory.CreateVerticalShort(profile, settings, vertical, standardRate);
            switch (key)
            {
                case "output.standardFrameRateBitrate": Assert.AreEqual(value.IntegerValue, (long)standard.Encoder.VideoBitrateBitsPerSecond, label); break;
                case "output.highFrameRateBitrate": Assert.AreEqual(value.IntegerValue, (long)high.Encoder.VideoBitrateBitsPerSecond, label); break;
                case "output.stereoBitrate": Assert.AreEqual(value.IntegerValue, (long)standard.Encoder.AudioBitrateBitsPerSecond, label); break;
                case "output.monoBitrate": Assert.AreEqual(value.IntegerValue, (long)high.Encoder.AudioBitrateBitsPerSecond, label); break;
                case "audio.internalOnlineTargetLoudness": Assert.AreEqual((decimal)value.IntegerValue!.Value, standard.Encoder.Loudness!.IntegratedLufs, label); break;
                case "audio.truePeakMaximum": Assert.AreEqual(value.IntegerValue!.Value - 0.1m, standard.Encoder.Loudness!.TruePeakDbtp, label); break;
                case "output.endScreenDuration": Assert.AreEqual(new RationalTime(value.IntegerValue!.Value, 1), standard.EndOverlayMaximumDuration, label); break;
                case "editing.shortHookMaximumStart": Assert.AreEqual(new RationalTime(value.IntegerValue!.Value, 1), standard.TitleMaximumDuration, label); break;
            }

            checkedValues++;
        }

        Assert.IsGreaterThan(80, checkedValues);
    }

    // FR-050.7 AC-025.5: subtitle size choices never grow beyond the default, so placement still fits.
    [TestMethod]
    public void SettingEditorCatalogGeometryChoicesNeverExceedTheDefault()
    {
        DefaultSettingsProfile profile = LoadProfile("1.2.0");
        string[] geometry = Editors().Where(static item => item.Entry.TryGetProperty("maximumIsDefault", out JsonElement flag) && flag.GetBoolean())
            .Select(static item => item.Key).ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            "subtitles.horizontalLineLengthMaximum", "subtitles.verticalLineLengthMaximum", "subtitles.lineCountMaximum",
            "subtitles.landscapeLineHeight", "subtitles.verticalLineHeight", "subtitles.cueGapMinimum",
        }, geometry);
        foreach ((string key, DefaultSettingValue value) in OfferedChoices(profile).Where(item => geometry.Contains(item.Key)))
        {
            DefaultSettingValue defaultValue = profile.GetDefinition(key).DefaultValue;
            RationalNumber chosen = value.RationalValue ?? new RationalNumber(value.IntegerValue!.Value, 1);
            RationalNumber initial = defaultValue.RationalValue ?? new RationalNumber(defaultValue.IntegerValue!.Value, 1);
            Assert.IsTrue(chosen <= initial, $"{key}={Describe(value)} exceeds the default.");
        }
    }

    // FR-050.1 AC-025.5: booleans the render configuration pins are classified fixed and really reject a change.
    [TestMethod]
    [DataRow("editing.longOpenerEnabled")]
    [DataRow("editing.shortOpenerEnabled")]
    [DataRow("editing.crossfadeEnabled")]
    [DataRow("audio.noiseReductionEnabled")]
    [DataRow("audio.humReductionEnabled")]
    [DataRow("audio.dialogueBackgroundMusicEnabled")]
    [DataRow("assets.musicLoopEnabled")]
    [DataRow("subtitles.enabled")]
    [DataRow("subtitles.verticalBurnInEnabled")]
    [DataRow("thumbnails.sourceFrameOnly")]
    public void SettingEditorCatalogRuntimeFixedBooleansAreRejectedWhenChanged(string key)
    {
        DefaultSettingsProfile profile = LoadProfile("1.2.0");
        Assert.AreEqual("runtimeFixed", Fixed().Single(item => item.Key == key).Reason);
        bool initial = profile.GetDefinition(key).DefaultValue.BooleanValue!.Value;
        DefaultSettingsSnapshot settings = WithValue(profile, key, DefaultSettingValue.FromBoolean(!initial));
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSetting, Assert.ThrowsExactly<StandardGenerationException>(
            () => StandardRenderConfiguration.FromSettings(profile, settings, new RationalNumber(30, 1), 2)).Reason);
    }

    /// <summary>Every value the settings screen offers for <paramref name="profile"/> (FR-050.4), validated by the profile.</summary>
    internal static IEnumerable<(string Key, DefaultSettingValue Value)> OfferedChoices(DefaultSettingsProfile profile)
    {
        foreach ((string key, JsonElement entry) in Editors())
        {
            // Keys added by a later profile (FR-057〜FR-060) are not offered for an older profile.
            if (!profile.Definitions.Any(item => item.Key == key))
                continue;
            DefaultSettingDefinition definition = profile.GetDefinition(key);
            string control = entry.GetProperty("control").GetString()!;
            IEnumerable<DefaultSettingValue> values = control switch
            {
                "boolean" => [DefaultSettingValue.FromBoolean(true), DefaultSettingValue.FromBoolean(false)],
                "range" => definition.Minimum?.IntegerValue is long minimum && definition.Maximum?.IntegerValue is long maximum
                    ? Enumerable.Range(0, checked((int)(maximum - minimum + 1))).Select(offset => DefaultSettingValue.FromInteger(minimum + offset))
                    : [],
                "choices" => entry.GetProperty("values").EnumerateArray().Select(static item => ParseChoice(item.GetString()!)),
                "text" => entry.GetProperty("options").EnumerateArray().Select(static item => DefaultSettingValue.FromText(item.GetProperty("value").GetString()!)),
                "multiple" => Subsets(entry.GetProperty("options").EnumerateArray().Select(static item => item.GetProperty("value").GetString()!).ToArray()),
                _ => throw new AssertFailedException($"Unknown control {control} for {key}."),
            };
            foreach (DefaultSettingValue value in values)
            {
                Assert.AreEqual(definition.DefaultValue.Kind, value.Kind, key);
                // CreateSnapshot validates kind, unit, range and allowed values against the profile definition.
                _ = WithValue(profile, key, value);
                yield return (key, value);
            }
        }
    }

    internal static DefaultSettingsSnapshot WithValue(DefaultSettingsProfile profile, string key, DefaultSettingValue value) =>
        profile.CreateSnapshot(profile.CreateSnapshot().Values.Select(selection => selection.Key == key
            ? new DefaultSettingSelection(key, value) : selection).ToArray());

    internal static string Describe(DefaultSettingValue value) => value.Kind switch
    {
        DefaultSettingValueKind.Boolean => value.BooleanValue!.Value ? "true" : "false",
        DefaultSettingValueKind.Integer => value.IntegerValue!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DefaultSettingValueKind.Rational => $"{value.RationalValue!.Value.Numerator}/{value.RationalValue!.Value.Denominator}",
        DefaultSettingValueKind.TextList => "[" + string.Join(",", value.TextListValue) + "]",
        _ => value.TextValue ?? string.Empty,
    };

    private static DefaultSettingValue ParseChoice(string text)
    {
        string[] parts = text.Split('/');
        return parts.Length == 2
            ? DefaultSettingValue.FromRational(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
            : DefaultSettingValue.FromInteger(long.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IEnumerable<DefaultSettingValue> Subsets(string[] options) =>
        Enumerable.Range(0, 1 << options.Length).Select(mask => DefaultSettingValue.FromTextList(
            options.Where((_, index) => (mask & (1 << index)) != 0).ToArray()));

    private static IEnumerable<(string Key, string Reason)> Fixed() =>
        Catalog().GetProperty("fixed").EnumerateObject().Select(static item => (item.Name, item.Value.GetString()!));

    private static IEnumerable<(string Key, JsonElement Entry)> Editors() =>
        Catalog().GetProperty("editors").EnumerateObject().Select(static item => (item.Name, item.Value));

    private static JsonElement Catalog()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(RepositoryPath("src/HubRadio.Web/src/features/settings/settingEditorCatalog.json")));
        JsonElement root = document.RootElement.Clone();
        Assert.AreEqual("hubradio.setting-editor-catalog.v1", root.GetProperty("schema").GetString());
        return root;
    }

    internal static DefaultSettingsProfile LoadProfile(string version) =>
        BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(RepositoryPath($"src/HubRadio.App/DefaultSettings/Profiles/{version}.json")));

    private static string RepositoryPath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            directory = directory.Parent;
        return Path.Combine((directory ?? throw new DirectoryNotFoundException()).FullName,
            relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
