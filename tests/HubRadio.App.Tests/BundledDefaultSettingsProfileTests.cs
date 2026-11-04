using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Profiles;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BundledDefaultSettingsProfileTests
{
    private const string RelativeProfilePath =
        "src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json";
    private const string CurrentRelativeProfilePath =
        "src/HubRadio.App/DefaultSettings/Profiles/1.1.0.json";
    private const string Profile130RelativeProfilePath =
        "src/HubRadio.App/DefaultSettings/Profiles/1.3.0.json";
    private const string LatestRelativeProfilePath =
        "src/HubRadio.App/DefaultSettings/Profiles/1.2.0.json";
    private const string ExpectedId = "8d75a1fe-d10e-4aa7-87c5-3ac2d06d1f9b";
    private const string ExpectedDigest =
        "5ffedbd56f43f9142b92c16e36abfd289da45cd34feea897da82101164a7414c";

    private static readonly (string RelativePath, string CanonicalDigest)[]
        ExistingDevelopmentRenderProfiles =
        [
            (
                "16x9/long-form.json",
                "a5ffe4cb92cd39e0ecf9f2dacf4dba18b5fc9b27a9808e3b42131b0a15c478e4"),
            (
                "16x9/youtube-landscape.json",
                "7c3b50a4f1223610180d2777c5755bbe61e7a36c87f41eeee0db17ace26415cb"),
            (
                "16x9/landscape-short.json",
                "bb1940c4e531f1cd1a7886f561c032f192210cedcd189a982b06bb12b913d536"),
            (
                "9x16/vertical-short.json",
                "998eb247113a144a486362f7ada653cb07e6e3ab69a86a1fd19addec04460f58"),
        ];

    [TestMethod]
    // FR-016 AC-064: quick start 既定は hardCutDuration 0 ms・crossfade 無効・有効時 250 ms。
    // FR-024 AC-072: quick start 既定の CTA は「本編を見る」のみで URL 等を推測しない。
    public void ProfileLoadsWithPinnedIdentityInventoryEvidenceAndCriticalDefaults()
    {
        DefaultSettingsProfile profile = LoadSource();

        Assert.AreEqual(ExpectedId, profile.Id.ToString());
        Assert.AreEqual("hubradio-production-defaults", profile.Name);
        Assert.AreEqual("1.0.0", profile.Version.ToString());
        Assert.AreEqual(ExpectedDigest, profile.ContentDigest.Hex);
        Assert.AreEqual(123, profile.Definitions.Count);
        Assert.AreEqual(
            123,
            profile.Definitions
                .Select(static definition => definition.Key)
                .Distinct(StringComparer.Ordinal)
                .Count());
        CollectionAssert.AreEqual(
            profile.Definitions
                .Select(static definition => definition.Key)
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToArray(),
            profile.Definitions.Select(static definition => definition.Key).ToArray());

        Assert.AreEqual(
            20,
            profile.Definitions.Count(static definition =>
                definition.Group == DefaultSettingGroup.AudioAndAssets));
        Assert.AreEqual(
            22,
            profile.Definitions.Count(static definition =>
                definition.Group == DefaultSettingGroup.Editing));
        Assert.AreEqual(
            13,
            profile.Definitions.Count(static definition =>
                definition.Group == DefaultSettingGroup.PrivacyAndStorage));
        Assert.AreEqual(
            30,
            profile.Definitions.Count(static definition =>
                definition.Group == DefaultSettingGroup.ProjectAndOutput));
        Assert.AreEqual(
            38,
            profile.Definitions.Count(static definition =>
                definition.Group == DefaultSettingGroup.SubtitlesAndGraphics));

        DefaultSettingDefinition callToAction = profile.GetDefinition(
            "editing.callToActionTexts");
        Assert.AreEqual(DefaultSettingGroup.Editing, callToAction.Group);
        Assert.IsFalse(callToAction.IsAdvanced);
        Assert.AreEqual(DefaultSettingValueKind.TextList, callToAction.DefaultValue.Kind);
        CollectionAssert.AreEqual(
            new[] { "本編を見る" },
            callToAction.DefaultValue.TextListValue.ToArray());
        Assert.AreEqual(1, callToAction.Evidence.Count);
        Assert.AreEqual(
            DefaultSettingEvidenceKind.ProductDecision,
            callToAction.Evidence[0].Kind);
        Assert.AreEqual("productDecision", callToAction.Evidence[0].SourceId);
        StringAssert.Contains(callToAction.Description, "only when");
        StringAssert.Contains(callToAction.Description, "otherwise it has no call to action");
        StringAssert.Contains(callToAction.Description, "No URL or other action is inferred");

        AssertText(profile, "output.frameRatePolicy", "sourcePreserve");
        AssertText(profile, "output.landscapeResolution", "1920x1080");
        AssertText(profile, "output.verticalResolution", "1080x1920");
        AssertText(profile, "output.hdrPolicy", "explicitToneMapToSdrBt709");
        AssertBoolean(profile, "output.hdrWarningRequired", expected: true);
        AssertInteger(profile, "editing.hardCutDuration", 0);
        AssertBoolean(profile, "editing.crossfadeEnabled", expected: false);
        AssertInteger(profile, "editing.crossfadeDurationWhenEnabled", 250);
        AssertText(profile, "subtitles.primaryProfile", "japaneseSdhOrientedCandidate");
        AssertInteger(profile, "subtitles.readingSpeedMaximum", 7);
        AssertInteger(profile, "subtitles.horizontalLineLengthMaximum", 16);
        AssertInteger(profile, "subtitles.lineCountMaximum", 2);
        Assert.AreEqual(
            new RationalNumber(5, 6),
            profile.GetDefinition("subtitles.cueMinimumDuration")
                .DefaultValue.RationalValue);
        AssertInteger(profile, "subtitles.cueMaximumDuration", 7);
        AssertInteger(profile, "subtitles.cueGapMinimum", 2);
        AssertText(profile, "subtitles.semanticCompleteness", "NOT EVALUATED");
        AssertText(profile, "subtitles.platformUiMaskStatus", "unmeasured");
        AssertBoolean(
            profile,
            "subtitles.platformUiMaskWarningRequired",
            expected: true);
        AssertInteger(profile, "audio.internalOnlineTargetLoudness", -16);
        AssertInteger(profile, "audio.truePeakMaximum", -1);
        AssertInteger(profile, "audio.broadcastTargetLoudness", -23);
        AssertText(profile, "audio.musicalFitEvaluation", "NOT EVALUATED");
        AssertText(profile, "project.template", "builtInNeutralNoLogo");
        AssertInteger(profile, "thumbnails.candidateCount", 3);
        AssertBoolean(profile, "privacy.externalPublicationEnabled", expected: false);
    }

    [TestMethod]
    public void ProfileBytesAreCanonicalStrictUtf8WithoutBomAndRoundTripExactly()
    {
        byte[] source = ReadSourceBytes();
        Assert.IsTrue(source.Length < 64 * 1024);
        Assert.IsFalse(source.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        CollectionAssert.AreEqual(source, CanonicalJson.CanonicalizeToUtf8(source));

        DefaultSettingsProfile profile = BundledDefaultSettingsProfileLoader.Load(source);
        CollectionAssert.AreEqual(
            source,
            new UTF8Encoding(false, true).GetBytes(profile.CanonicalJson));
        Assert.AreEqual(
            profile.CanonicalJson,
            DefaultSettingsProfile.ParseCanonical(profile.CanonicalJson).CanonicalJson);
    }

    [TestMethod]
    public void ProfileJsonIsCopiedBesideTheBuiltAppAssemblyWithIdenticalBytes()
    {
        string assemblyDirectory = Path.GetDirectoryName(
            typeof(BundledDefaultSettingsProfileLoader).Assembly.Location)
            ?? throw new AssertFailedException("The App assembly has no directory.");
        string outputPath = Path.Combine(
            assemblyDirectory,
            "DefaultSettings",
            "Profiles",
            "1.0.0.json");

        Assert.IsTrue(File.Exists(outputPath), outputPath);
        CollectionAssert.AreEqual(ReadSourceBytes(), File.ReadAllBytes(outputPath));
        _ = BundledDefaultSettingsProfileLoader.Load(File.ReadAllBytes(outputPath));
    }

    [TestMethod]
    public void ExistingDevelopmentRenderProfilesRemainUnchangedAndUnapproved()
    {
        foreach ((string relativePath, string canonicalDigest) in
                 ExistingDevelopmentRenderProfiles)
        {
            byte[] source = File.ReadAllBytes(ResolveRepositoryPath(
                $"src/HubRadio.App/Profiles/{relativePath}"));
            Assert.AreEqual(
                canonicalDigest,
                CanonicalJson.ComputeDigest(source).Hex,
                relativePath);

            BundledRenderProfileDefinition definition =
                BundledRenderProfileLoader.Load(source);
            Assert.AreEqual(
                "development-neutral-placeholder",
                definition.TemplateStatus,
                relativePath);
            Assert.IsFalse(definition.RequiredExternalBrandAssets, relativePath);
        }
    }

    [TestMethod]
    public void LoaderRejectsEmptyOversizedInvalidUtf8BomAndNoncanonicalInput()
    {
        AssertRejected([]);
        AssertRejected(new byte[(64 * 1024) + 1]);
        AssertRejected([0xff]);

        byte[] source = ReadSourceBytes();
        byte[] withBom = new byte[source.Length + 3];
        withBom[0] = 0xef;
        withBom[1] = 0xbb;
        withBom[2] = 0xbf;
        source.CopyTo(withBom, 3);
        AssertRejected(withBom);

        byte[] withTrailingLineFeed = new byte[source.Length + 1];
        source.CopyTo(withTrailingLineFeed, 0);
        withTrailingLineFeed[^1] = (byte)'\n';
        AssertRejected(withTrailingLineFeed);
    }

    [TestMethod]
    public void LoaderRejectsClosedShapeInventoryAndCanonicalOrderMutations()
    {
        AssertMutationRejected(root => root["extra"] = true, recomputeDigest: false);
        AssertMutationRejected(root => root.Remove("schemaVersion"), recomputeDigest: false);
        JsonException inventoryException = AssertMutationRejected(
            root => Settings(root).RemoveAt(0));
        StringAssert.Contains(
            inventoryException.Message,
            "key inventory is not approved");
        AssertMutationRejected(root =>
            Setting(root, "thumbnails.verticalCanvas")["key"] = "z.unknown");
        AssertMutationRejected(root =>
        {
            JsonArray settings = Settings(root);
            JsonNode first = settings[0]?.DeepClone()
                ?? throw new AssertFailedException("The first setting is missing.");
            settings.RemoveAt(0);
            settings.Add(first);
        });
        AssertMutationRejected(root =>
            Settings(root).Add(Settings(root)[0]?.DeepClone()));
    }

    [TestMethod]
    public void LoaderRejectsSameVersionDriftAndIdentitySubstitution()
    {
        JsonException driftException = AssertMutationRejected(root =>
            Setting(root, "editing.intensity")["description"] =
                "A different approved-looking description.");
        StringAssert.Contains(
            driftException.Message,
            "differs from the approved content for its version");

        JsonException identityException = AssertMutationRejected(
            root => root["id"] = "11111111-1111-4111-8111-111111111111",
            recomputeDigest: false);
        StringAssert.Contains(
            identityException.Message,
            "identity is not approved");
        AssertMutationRejected(root => root["name"] = "another-production-default");
        AssertMutationRejected(root => root["version"] = "1.0.1");
    }

    [TestMethod]
    public void LoaderRejectsMissingOrUnregisteredEvidenceWithRecomputedCanonicalDigest()
    {
        JsonException missingException = AssertMutationRejected(root =>
            RequireArray(
                Setting(root, "output.landscapeResolution"),
                "evidence").Clear());
        StringAssert.Contains(
            missingException.Message,
            "default-settings profile is invalid");

        JsonException unregisteredException = AssertMutationRejected(root =>
        {
            JsonArray evidence = RequireArray(
                Setting(root, "output.landscapeResolution"),
                "evidence");
            evidence[0]!.AsObject()["sourceId"] = "R99";
        });
        StringAssert.Contains(
            unregisteredException.Message,
            "outside the approved registry");
    }

    [TestMethod]
    public void LoaderRejectsCriticalSemanticDriftWithRecomputedCanonicalDigest()
    {
        JsonException frameRateException = AssertMutationRejected(root =>
        {
            JsonObject definition = Setting(root, "output.frameRatePolicy");
            RequireObject(definition, "defaultValue")["text"] = "fixed30000Over1001";
            RequireArray(definition, "allowedTextValues")[0] = "fixed30000Over1001";
        });
        StringAssert.Contains(frameRateException.Message, "not approved");
        AssertMutationRejected(root =>
            RequireObject(
                Setting(root, "subtitles.platformUiMaskWarningRequired"),
                "defaultValue")["boolean"] = false);
        AssertMutationRejected(root =>
        {
            JsonObject definition = Setting(root, "editing.callToActionTexts");
            RequireArray(
                RequireObject(definition, "defaultValue"),
                "values")[0] = "チャンネルを見る";
        });
    }

    [TestMethod]
    public void CatalogLoadsAndResolvesOnlyThePinnedProfiles()
    {
        string sourceDirectory = ResolveRepositoryPath(
            "src/HubRadio.App/DefaultSettings/Profiles",
            expectDirectory: true);
        BundledDefaultSettingsProfileCatalog catalog =
            BundledDefaultSettingsProfileCatalog.Load(sourceDirectory);

        Assert.AreEqual(4, catalog.Profiles.Count);
        CollectionAssert.AreEqual(
            new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0" },
            catalog.Profiles.Select(static profile => profile.Version.ToString()).ToArray());
        Assert.AreSame(catalog.Current, catalog.Profiles[3]);
        Assert.AreSame(
            catalog.Profiles[0],
            catalog.Resolve(
                catalog.Profiles[0].Id,
                catalog.Profiles[0].Version,
                catalog.Profiles[0].ContentDigest));
        Assert.AreSame(
            catalog.Current,
            catalog.Resolve(
                catalog.Current.Id,
                catalog.Current.Version,
                catalog.Current.ContentDigest));
        Assert.ThrowsExactly<InvalidOperationException>(() => catalog.Resolve(
            catalog.Current.Id,
            catalog.Current.Version,
            new ContentDigest(new string('0', 64))));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<DefaultSettingsProfile>)catalog.Profiles).Clear());
    }

    [TestMethod]
    public void CatalogRejectsMissingOrAdditionalProfiles()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.MissingDefaultSettingsCatalog",
            Guid.NewGuid().ToString("N"));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            BundledDefaultSettingsProfileCatalog.Load(missingPath));

        using TemporaryDirectory temporary = new();
        File.WriteAllBytes(Path.Combine(temporary.Path, "1.0.0.json"), ReadSourceBytes());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BundledDefaultSettingsProfileCatalog.Load(temporary.Path));
        File.WriteAllBytes(
            Path.Combine(temporary.Path, "1.1.0.json"),
            File.ReadAllBytes(ResolveRepositoryPath(CurrentRelativeProfilePath)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BundledDefaultSettingsProfileCatalog.Load(temporary.Path));
        File.WriteAllBytes(
            Path.Combine(temporary.Path, "1.2.0.json"),
            File.ReadAllBytes(ResolveRepositoryPath(LatestRelativeProfilePath)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BundledDefaultSettingsProfileCatalog.Load(temporary.Path));
        File.WriteAllBytes(
            Path.Combine(temporary.Path, "1.3.0.json"),
            File.ReadAllBytes(ResolveRepositoryPath(Profile130RelativeProfilePath)));
        _ = BundledDefaultSettingsProfileCatalog.Load(temporary.Path);

        File.WriteAllBytes(
            Path.Combine(temporary.Path, "2.0.0.json"),
            ReadSourceBytes());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BundledDefaultSettingsProfileCatalog.Load(temporary.Path));
    }

    [TestMethod]
    public void CurrentProfileKeepsEveryDefaultAndOnlyWidensTheHonoredRanges()
    {
        DefaultSettingsProfile previous = LoadSource();
        DefaultSettingsProfile current = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(ResolveRepositoryPath(CurrentRelativeProfilePath)));

        Assert.AreEqual("1.1.0", current.Version.ToString());
        Assert.AreEqual(previous.Id, current.Id);
        Assert.AreEqual(previous.Definitions.Count, current.Definitions.Count);
        string[] changed = current.Definitions
            .Where(definition =>
            {
                DefaultSettingDefinition before = previous.GetDefinition(definition.Key);
                return definition.Description != before.Description
                    || !Equals(definition.Minimum, before.Minimum)
                    || !Equals(definition.Maximum, before.Maximum);
            })
            .Select(static definition => definition.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "editing.shortHookMaximumStart", "thumbnails.candidateCount" },
            changed);
        foreach (DefaultSettingDefinition definition in current.Definitions)
        {
            Assert.AreEqual(
                previous.GetDefinition(definition.Key).DefaultValue,
                definition.DefaultValue,
                definition.Key);
        }

        Assert.AreEqual(1, current.GetDefinition("thumbnails.candidateCount").Minimum!.IntegerValue);
        Assert.AreEqual(3, current.GetDefinition("thumbnails.candidateCount").Maximum!.IntegerValue);
        Assert.AreEqual(1, current.GetDefinition("editing.shortHookMaximumStart").Minimum!.IntegerValue);
        Assert.AreEqual(5, current.GetDefinition("editing.shortHookMaximumStart").Maximum!.IntegerValue);
        Assert.IsTrue(BundledDefaultSettingsProfileLoader.IsApprovedProfile(previous));
        Assert.IsTrue(BundledDefaultSettingsProfileLoader.IsApprovedProfile(current));
    }

    [TestMethod]
    [TestCategory("FR-057")]
    [TestCategory("FR-058")]
    [TestCategory("FR-059")]
    [TestCategory("FR-060")]
    public void LatestProfileKeepsEveryPreviousDefinitionAndAddsOnlyTheFourNewSettings()
    {
        DefaultSettingsProfile previous = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(ResolveRepositoryPath(CurrentRelativeProfilePath)));
        DefaultSettingsProfile latest = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(ResolveRepositoryPath(LatestRelativeProfilePath)));

        Assert.AreEqual("1.2.0", latest.Version.ToString());
        Assert.AreEqual(previous.Id, latest.Id);
        foreach (DefaultSettingDefinition definition in previous.Definitions)
        {
            DefaultSettingDefinition after = latest.GetDefinition(definition.Key);
            Assert.AreEqual(definition.DefaultValue, after.DefaultValue, definition.Key);
            Assert.AreEqual(definition.Description, after.Description, definition.Key);
            Assert.AreEqual(definition.Minimum, after.Minimum, definition.Key);
            Assert.AreEqual(definition.Maximum, after.Maximum, definition.Key);
            CollectionAssert.AreEqual(definition.AllowedTextValues.ToArray(), after.AllowedTextValues.ToArray(), definition.Key);
        }

        CollectionAssert.AreEqual(
            new[] { "audio.soundEffectsEnabled", "editing.pauseTightening", "output.longFormOrientation", "subtitles.emphasisTelopAmount" },
            latest.Definitions.Select(static item => item.Key)
                .Except(previous.Definitions.Select(static item => item.Key)).Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual(DefaultSettingValue.FromBoolean(true), latest.GetDefinition("audio.soundEffectsEnabled").DefaultValue);
        Assert.AreEqual(DefaultSettingValue.FromText("standard"), latest.GetDefinition("editing.pauseTightening").DefaultValue);
        CollectionAssert.AreEquivalent(new[] { "weak", "standard", "strong" },
            latest.GetDefinition("editing.pauseTightening").AllowedTextValues.ToArray());
        Assert.AreEqual(DefaultSettingValue.FromText("followSource"), latest.GetDefinition("output.longFormOrientation").DefaultValue);
        CollectionAssert.AreEquivalent(new[] { "followSource", "landscape" },
            latest.GetDefinition("output.longFormOrientation").AllowedTextValues.ToArray());
        Assert.AreEqual(DefaultSettingValue.FromText("standard"), latest.GetDefinition("subtitles.emphasisTelopAmount").DefaultValue);
        CollectionAssert.AreEquivalent(new[] { "few", "standard", "many" },
            latest.GetDefinition("subtitles.emphasisTelopAmount").AllowedTextValues.ToArray());
        Assert.IsTrue(BundledDefaultSettingsProfileLoader.IsApprovedProfile(latest));
    }

    // FR-146 AC-183: プロファイル 1.3.0 は、1.2.0 のすべての定義を変えずに、テンプレート自動選択の項目（既定: 使う = PARAM-012）だけを足す。
    [TestMethod]
    [TestCategory("FR-146")]
    public void Profile130KeepsEveryPreviousDefinitionAndAddsOnlyTheTemplateAutoSelectionSetting()
    {
        DefaultSettingsProfile previous = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(ResolveRepositoryPath(LatestRelativeProfilePath)));
        DefaultSettingsProfile latest = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(ResolveRepositoryPath(Profile130RelativeProfilePath)));

        Assert.AreEqual("1.3.0", latest.Version.ToString());
        Assert.AreEqual(previous.Id, latest.Id);
        foreach (DefaultSettingDefinition definition in previous.Definitions)
        {
            DefaultSettingDefinition after = latest.GetDefinition(definition.Key);
            Assert.AreEqual(definition.DefaultValue, after.DefaultValue, definition.Key);
            Assert.AreEqual(definition.Description, after.Description, definition.Key);
            Assert.AreEqual(definition.Minimum, after.Minimum, definition.Key);
            Assert.AreEqual(definition.Maximum, after.Maximum, definition.Key);
        }

        CollectionAssert.AreEqual(
            new[] { "project.templateAutoSelectEnabled" },
            latest.Definitions.Select(static item => item.Key)
                .Except(previous.Definitions.Select(static item => item.Key)).ToArray());
        DefaultSettingDefinition added = latest.GetDefinition("project.templateAutoSelectEnabled");
        Assert.AreEqual(DefaultSettingValue.FromBoolean(true), added.DefaultValue);
        Assert.AreEqual(DefaultSettingGroup.ProjectAndOutput, added.Group);
        Assert.IsTrue(BundledDefaultSettingsProfileLoader.IsApprovedProfile(latest));
    }

    [TestMethod]
    public void LoaderPublicApiIsMinimalAndReturnedCollectionsAreReadOnly()
    {
        Type loaderType = typeof(BundledDefaultSettingsProfileLoader);
        Assert.IsTrue(loaderType.IsPublic);
        Assert.IsTrue(loaderType.IsAbstract && loaderType.IsSealed);
        Assert.AreEqual(0, loaderType.GetConstructors().Length);

        MethodInfo[] methods = loaderType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("Load", methods[0].Name);
        Assert.AreEqual(typeof(DefaultSettingsProfile), methods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(ReadOnlySpan<byte>) },
            methods[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        DefaultSettingsProfile profile = LoadSource();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<DefaultSettingDefinition>)profile.Definitions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<DefaultSettingEvidence>)profile.Definitions[0].Evidence).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)profile.GetDefinition("editing.callToActionTexts")
                .DefaultValue.TextListValue).Add("外部へ移動する"));
    }

    private static DefaultSettingsProfile LoadSource() =>
        BundledDefaultSettingsProfileLoader.Load(ReadSourceBytes());

    private static byte[] ReadSourceBytes() =>
        File.ReadAllBytes(ResolveRepositoryPath(RelativeProfilePath));

    private static void AssertText(
        DefaultSettingsProfile profile,
        string key,
        string expected) =>
        Assert.AreEqual(expected, profile.GetDefinition(key).DefaultValue.TextValue, key);

    private static void AssertInteger(
        DefaultSettingsProfile profile,
        string key,
        long expected) =>
        Assert.AreEqual(expected, profile.GetDefinition(key).DefaultValue.IntegerValue, key);

    private static void AssertBoolean(
        DefaultSettingsProfile profile,
        string key,
        bool expected) =>
        Assert.AreEqual(expected, profile.GetDefinition(key).DefaultValue.BooleanValue, key);

    private static JsonException AssertMutationRejected(
        Action<JsonObject> mutation,
        bool recomputeDigest = true)
    {
        JsonObject root = ParseSourceObject();
        mutation(root);
        if (recomputeDigest)
        {
            RecomputeProfileDigest(root);
        }

        return AssertRejected(CanonicalBytes(root));
    }

    private static JsonException AssertRejected(byte[] utf8Json)
    {
        return Assert.Throws<JsonException>(() =>
            BundledDefaultSettingsProfileLoader.Load(utf8Json));
    }

    private static JsonObject ParseSourceObject() =>
        JsonNode.Parse(ReadSourceBytes())?.AsObject()
        ?? throw new AssertFailedException("The profile fixture root was null.");

    private static byte[] CanonicalBytes(JsonObject root) =>
        CanonicalJson.CanonicalizeToUtf8(
            Encoding.UTF8.GetBytes(root.ToJsonString()));

    private static void RecomputeProfileDigest(JsonObject root)
    {
        JsonObject payload = new()
        {
            ["schemaVersion"] = RequireNode(root, "schemaVersion").DeepClone(),
            ["name"] = RequireNode(root, "name").DeepClone(),
            ["version"] = RequireNode(root, "version").DeepClone(),
            ["settings"] = RequireNode(root, "settings").DeepClone(),
        };
        root["contentDigest"] = CanonicalJson.ComputeDigest(
            Encoding.UTF8.GetBytes(payload.ToJsonString())).Hex;
    }

    private static JsonArray Settings(JsonObject root) =>
        RequireArray(root, "settings");

    private static JsonObject Setting(JsonObject root, string key)
    {
        foreach (JsonNode? node in Settings(root))
        {
            JsonObject definition = node?.AsObject()
                ?? throw new AssertFailedException("A setting definition was null.");
            if (string.Equals(
                    definition["key"]?.GetValue<string>(),
                    key,
                    StringComparison.Ordinal))
            {
                return definition;
            }
        }

        throw new AssertFailedException($"Setting '{key}' was not found.");
    }

    private static JsonNode RequireNode(JsonObject parent, string propertyName) =>
        parent[propertyName]
        ?? throw new AssertFailedException($"Missing test property '{propertyName}'.");

    private static JsonObject RequireObject(JsonObject parent, string propertyName) =>
        RequireNode(parent, propertyName).AsObject();

    private static JsonArray RequireArray(JsonObject parent, string propertyName) =>
        RequireNode(parent, propertyName).AsArray();

    private static string ResolveRepositoryPath(
        string relativePath,
        bool expectDirectory = false)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                bool exists = expectDirectory ? Directory.Exists(path) : File.Exists(path);
                if (!exists)
                {
                    throw new FileNotFoundException(
                        $"Expected repository-relative path '{relativePath}'.",
                        path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "HubRadio.BundledDefaultSettingsProfileTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
