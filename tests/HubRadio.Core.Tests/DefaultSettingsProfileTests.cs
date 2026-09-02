using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class DefaultSettingsProfileTests
{
    private static readonly EntityId<DefaultSettingsProfileIdTag> ProfileId =
        EntityId<DefaultSettingsProfileIdTag>.From(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

    [TestMethod]
    public void SettingsProfileVersionRequiresCanonicalNonNegativeTriplet()
    {
        SettingsProfileVersion version = SettingsProfileVersion.Parse("1.20.300");

        Assert.AreEqual(1, version.Major);
        Assert.AreEqual(20, version.Minor);
        Assert.AreEqual(300, version.Patch);
        Assert.AreEqual("1.20.300", version.ToString());
        Assert.AreEqual(new SettingsProfileVersion(1, 20, 300), version);
        Assert.AreEqual(version.GetHashCode(), new SettingsProfileVersion(1, 20, 300).GetHashCode());

        foreach (string invalid in new[]
        {
            string.Empty,
            "1",
            "1.2",
            "1.2.3.4",
            "01.2.3",
            "1.02.3",
            "1.2.03",
            "+1.2.3",
            "1.-2.3",
            "1.2.3-alpha",
            " 1.2.3",
            "1.2.3 ",
            "2147483648.0.0",
        })
        {
            Assert.IsFalse(SettingsProfileVersion.TryParse(invalid, out _), invalid);
            Assert.ThrowsExactly<FormatException>(
                () => SettingsProfileVersion.Parse(invalid),
                invalid);
        }

        Assert.IsFalse(SettingsProfileVersion.TryParse(null, out _));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => SettingsProfileVersion.Parse(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SettingsProfileVersion(-1, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SettingsProfileVersion(0, -1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SettingsProfileVersion(0, 0, -1));
    }

    [TestMethod]
    public void ProfileSortsDefinitionsComputesDigestAndRoundTripsCanonicalJson()
    {
        DefaultSettingDefinition publication = PublicationScopeDefinition();
        DefaultSettingDefinition fade = FadeDurationDefinition();
        DefaultSettingsProfile profile = DefaultSettingsProfile.Create(
            ProfileId,
            "HubRadio internal defaults",
            new SettingsProfileVersion(1, 0, 0),
            [publication, fade]);

        CollectionAssert.AreEqual(
            new[] { "editing.crossfadeDuration", "project.publicationScope" },
            profile.Definitions.Select(static definition => definition.Key).ToArray());
        Assert.AreEqual(
            profile.CanonicalJson,
            CanonicalJson.Canonicalize(profile.CanonicalJson));

        using JsonDocument document = JsonDocument.Parse(profile.CanonicalJson);
        Assert.AreEqual(profile.ContentDigest.Hex, document.RootElement
            .GetProperty("contentDigest")
            .GetString());
        Assert.AreEqual("1.0.0", document.RootElement.GetProperty("version").GetString());

        DefaultSettingsProfile parsed = DefaultSettingsProfile.ParseCanonical(
            profile.CanonicalJson);
        Assert.AreEqual(profile.Id, parsed.Id);
        Assert.AreEqual(profile.Name, parsed.Name);
        Assert.AreEqual(profile.Version, parsed.Version);
        Assert.AreEqual(profile.ContentDigest, parsed.ContentDigest);
        Assert.AreEqual(profile.CanonicalJson, parsed.CanonicalJson);

        DefaultSettingsProfile reordered = DefaultSettingsProfile.Create(
            ProfileId,
            profile.Name,
            new SettingsProfileVersion(1, 0, 0),
            [fade, publication]);
        Assert.AreEqual(profile.ContentDigest, reordered.ContentDigest);
        Assert.AreEqual(profile.CanonicalJson, reordered.CanonicalJson);

        DefaultSettingsProfile renamed = DefaultSettingsProfile.Create(
            ProfileId,
            "Different profile name",
            profile.Version,
            [fade, publication]);
        DefaultSettingsProfile newVersion = DefaultSettingsProfile.Create(
            ProfileId,
            profile.Name,
            new SettingsProfileVersion(1, 1, 0),
            [fade, publication]);
        Assert.AreNotEqual(profile.ContentDigest, renamed.ContentDigest);
        Assert.AreNotEqual(profile.ContentDigest, newVersion.ContentDigest);
    }

    [TestMethod]
    public void ProfileAndDefinitionsRejectMissingDuplicateOrInvalidContracts()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => DefaultSettingsProfile.Create(
                null!,
                "profile",
                new SettingsProfileVersion(1, 0, 0),
                [PublicationScopeDefinition()]));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => DefaultSettingsProfile.Create(
                ProfileId,
                "profile",
                null!,
                [PublicationScopeDefinition()]));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => DefaultSettingsProfile.Create(
                ProfileId,
                "profile",
                new SettingsProfileVersion(1, 0, 0),
                null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => DefaultSettingsProfile.Create(
                ProfileId,
                "profile",
                new SettingsProfileVersion(1, 0, 0),
                []));
        Assert.ThrowsExactly<ArgumentException>(
            () => DefaultSettingsProfile.Create(
                ProfileId,
                "profile",
                new SettingsProfileVersion(1, 0, 0),
                [PublicationScopeDefinition(), PublicationScopeDefinition()]));
        Assert.ThrowsExactly<ArgumentException>(
            () => DefaultSettingsProfile.Create(
                ProfileId,
                " profile",
                new SettingsProfileVersion(1, 0, 0),
                [PublicationScopeDefinition()]));

        foreach (string invalidKey in new[]
        {
            string.Empty,
            "Project.scope",
            "project..scope",
            "project.scope.",
            ".project.scope",
            "project.scope-name",
            new string('a', 129),
        })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateDefinition(key: invalidKey),
                invalidKey);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDefinition(group: (DefaultSettingGroup)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDefinition(unit: (DefaultSettingUnit)int.MaxValue));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromBoolean(true),
                unit: DefaultSettingUnit.Count));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromText("value"),
                minimum: DefaultSettingValue.FromInteger(0)));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromInteger(5),
                minimum: DefaultSettingValue.FromRational(1, 1)));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromInteger(5),
                minimum: DefaultSettingValue.FromInteger(10),
                maximum: DefaultSettingValue.FromInteger(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromInteger(0),
                minimum: DefaultSettingValue.FromInteger(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromInteger(11),
                maximum: DefaultSettingValue.FromInteger(10)));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromInteger(5),
                allowedTextValues: ["5"]));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromText("missing"),
                allowedTextValues: ["allowed"]));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                value: DefaultSettingValue.FromText("allowed"),
                allowedTextValues: ["allowed", "allowed"]));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(evidence: []));
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateDefinition(
                evidence:
                [
                    ProductDecisionEvidence(),
                    ProductDecisionEvidence(),
                ]));
    }

    [TestMethod]
    public void EvidenceRequiresSourceIdentifierToMatchItsKind()
    {
        DefaultSettingEvidence user = new(
            DefaultSettingEvidenceKind.UserDecision,
            "U01");
        DefaultSettingEvidence implementation = new(
            DefaultSettingEvidenceKind.CurrentImplementation,
            "C05");
        DefaultSettingEvidence recommendation = new(
            DefaultSettingEvidenceKind.OfficialRecommendation,
            "R20");
        DefaultSettingEvidence product = ProductDecisionEvidence();

        Assert.AreEqual("U01", user.SourceId);
        Assert.AreEqual("C05", implementation.SourceId);
        Assert.AreEqual("R20", recommendation.SourceId);
        Assert.AreEqual("productDecision", product.SourceId);
        Assert.AreEqual(product, ProductDecisionEvidence());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DefaultSettingEvidence(
                (DefaultSettingEvidenceKind)int.MaxValue,
                "R01"));
        foreach ((DefaultSettingEvidenceKind kind, string sourceId) in new[]
        {
            (DefaultSettingEvidenceKind.UserDecision, "R01"),
            (DefaultSettingEvidenceKind.ProductDecision, "U01"),
            (DefaultSettingEvidenceKind.CurrentImplementation, "C1"),
            (DefaultSettingEvidenceKind.Specification, "r01"),
            (DefaultSettingEvidenceKind.PeerReviewedFinding, "productDecision"),
        })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new DefaultSettingEvidence(kind, sourceId));
        }
    }

    [TestMethod]
    public void ValueFactoriesAreTypedCanonicalAndDefensivelyCopyTextLists()
    {
        DefaultSettingValue rational = DefaultSettingValue.FromRational(10, 12);
        Assert.AreEqual(DefaultSettingValueKind.Rational, rational.Kind);
        Assert.AreEqual(5L, rational.RationalValue!.Value.Numerator);
        Assert.AreEqual(6L, rational.RationalValue.Value.Denominator);

        string[] source = ["longForm", "verticalShort"];
        DefaultSettingValue list = DefaultSettingValue.FromTextList(source);
        source[0] = "mutated";
        CollectionAssert.AreEqual(
            new[] { "longForm", "verticalShort" },
            list.TextListValue.ToArray());
        Assert.AreEqual(
            list,
            DefaultSettingValue.FromTextList(["longForm", "verticalShort"]));

        foreach (string invalid in new[] { string.Empty, " ", " value", "value ", "\ud800" })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => DefaultSettingValue.FromText(invalid));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DefaultSettingValue.FromRational(1, 0));
        Assert.ThrowsExactly<ArgumentException>(
            () => DefaultSettingValue.FromTextList(["same", "same"]));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => DefaultSettingValue.FromTextList(null!));
    }

    [TestMethod]
    public void SnapshotContainsExactlyOneValidatedValuePerDefinitionAndRoundTrips()
    {
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingsSnapshot defaults = profile.CreateSnapshot();
        Assert.AreEqual(profile.Id, defaults.ProfileId);
        Assert.AreEqual(profile.Version, defaults.ProfileVersion);
        Assert.AreEqual(profile.ContentDigest, defaults.ProfileDigest);
        CollectionAssert.AreEqual(
            new[] { "editing.crossfadeDuration", "project.publicationScope" },
            defaults.Values.Select(static value => value.Key).ToArray());

        DefaultSettingsSnapshot customized = profile.CreateSnapshot(
        [
            new DefaultSettingSelection(
                "project.publicationScope",
                DefaultSettingValue.FromText("pendingDecision")),
            new DefaultSettingSelection(
                "editing.crossfadeDuration",
                DefaultSettingValue.FromInteger(250)),
        ]);
        Assert.AreNotEqual(defaults.ContentDigest, customized.ContentDigest);
        Assert.AreEqual(
            customized.CanonicalJson,
            CanonicalJson.Canonicalize(customized.CanonicalJson));

        DefaultSettingsSnapshot parsed = DefaultSettingsSnapshot.ParseCanonical(
            customized.CanonicalJson,
            profile);
        Assert.AreEqual(customized.ContentDigest, parsed.ContentDigest);
        Assert.AreEqual(customized.CanonicalJson, parsed.CanonicalJson);

        DefaultSettingsSnapshot reordered = profile.CreateSnapshot(
        [
            new DefaultSettingSelection(
                "editing.crossfadeDuration",
                DefaultSettingValue.FromInteger(250)),
            new DefaultSettingSelection(
                "project.publicationScope",
                DefaultSettingValue.FromText("pendingDecision")),
        ]);
        Assert.AreEqual(customized.ContentDigest, reordered.ContentDigest);
        Assert.AreEqual(customized.CanonicalJson, reordered.CanonicalJson);
    }

    [TestMethod]
    public void SnapshotRejectsMissingUnknownDuplicateWrongKindRangeAndAllowlistValues()
    {
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingSelection fade = new(
            "editing.crossfadeDuration",
            DefaultSettingValue.FromInteger(250));
        DefaultSettingSelection publication = new(
            "project.publicationScope",
            DefaultSettingValue.FromText("internalOnly"));

        Assert.ThrowsExactly<ArgumentException>(
            () => profile.CreateSnapshot([fade]));
        Assert.ThrowsExactly<ArgumentException>(
            () => profile.CreateSnapshot([fade, fade]));
        Assert.ThrowsExactly<ArgumentException>(
            () => profile.CreateSnapshot(
                [
                    fade,
                    new DefaultSettingSelection(
                        "project.unknown",
                        DefaultSettingValue.FromText("internalOnly")),
                ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => profile.CreateSnapshot(
                [
                    new DefaultSettingSelection(
                        fade.Key,
                        DefaultSettingValue.FromText("250")),
                    publication,
                ]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => profile.CreateSnapshot(
                [
                    new DefaultSettingSelection(
                        fade.Key,
                        DefaultSettingValue.FromInteger(5001)),
                    publication,
                ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => profile.CreateSnapshot(
                [
                    fade,
                    new DefaultSettingSelection(
                        publication.Key,
                        DefaultSettingValue.FromText("externalPublicationApproved")),
                ]));
    }

    [TestMethod]
    public void CanonicalParsersRejectWhitespaceUnknownPropertiesTamperingAndWrongBinding()
    {
        DefaultSettingsProfile profile = CreateProfile();
        string profileJson = profile.CanonicalJson;

        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical($" {profileJson}"));
        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical(MutateCanonical(
                profileJson,
                root => root["extra"] = true)));
        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical(MutateCanonical(
                profileJson,
                root => root["contentDigest"] = new string('0', 64))));
        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical(MutateCanonical(
                profileJson,
                root => root["schemaVersion"] = "2.0.0")));
        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical(MutateCanonical(
                profileJson,
                root => root["version"] = "01.0.0")));
        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical(MutateCanonical(
                profileJson,
                root =>
                {
                    JsonArray settings = root["settings"]!.AsArray();
                    settings[0]!.AsObject()["unexpected"] = 1;
                })));

        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
        Assert.Throws<JsonException>(
            () => DefaultSettingsSnapshot.ParseCanonical(
                MutateCanonical(
                    snapshot.CanonicalJson,
                    root => root["contentDigest"] = new string('f', 64)),
                profile));
        Assert.Throws<JsonException>(
            () => DefaultSettingsSnapshot.ParseCanonical(
                MutateCanonical(
                    snapshot.CanonicalJson,
                    root => root["profileId"] = "11112233-4455-6677-8899-aabbccddeeff"),
                profile));
        Assert.Throws<JsonException>(
            () => DefaultSettingsSnapshot.ParseCanonical(
                snapshot.CanonicalJson,
                DefaultSettingsProfile.Create(
                    EntityId<DefaultSettingsProfileIdTag>.New(),
                    profile.Name,
                    profile.Version,
                    profile.Definitions)));
    }

    [TestMethod]
    public void CanonicalParsersRejectSemanticallyUnorderedArrays()
    {
        DefaultSettingsProfile profile = CreateProfile();

        foreach (Action<JsonObject> reorder in new Action<JsonObject>[]
        {
            root => Reverse(root["settings"]!.AsArray()),
            root => Reverse(
                root["settings"]!.AsArray()[1]!
                    .AsObject()["allowedTextValues"]!.AsArray()),
            root => Reverse(
                root["settings"]!.AsArray()[1]!
                    .AsObject()["evidence"]!.AsArray()),
        })
        {
            string reordered = MutateCanonical(profile.CanonicalJson, reorder);
            Assert.Throws<JsonException>(
                () => DefaultSettingsProfile.ParseCanonical(reordered));
        }

        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
        string reorderedValues = MutateCanonical(
            snapshot.CanonicalJson,
            root => Reverse(root["values"]!.AsArray()));
        Assert.Throws<JsonException>(
            () => DefaultSettingsSnapshot.ParseCanonical(reorderedValues, profile));

        DefaultSettingsProfile rationalProfile = DefaultSettingsProfile.Create(
            ProfileId,
            "Rational defaults",
            new SettingsProfileVersion(1, 0, 0),
            [
                CreateDefinition(
                    key: "subtitles.cueMinimum",
                    group: DefaultSettingGroup.SubtitlesAndGraphics,
                    value: DefaultSettingValue.FromRational(5, 6),
                    unit: DefaultSettingUnit.Seconds),
            ]);
        string unreducedRational = MutateCanonical(
            rationalProfile.CanonicalJson,
            root =>
            {
                JsonObject value = root["settings"]!.AsArray()[0]!
                    .AsObject()["defaultValue"]!.AsObject();
                value["numerator"] = 10;
                value["denominator"] = 12;
            });
        Assert.Throws<JsonException>(
            () => DefaultSettingsProfile.ParseCanonical(unreducedRational));
    }

    [TestMethod]
    public void ProfileSeparatesDefinitionsFromResolvedSnapshotAndRuntimeState()
    {
        string[] profileProperties = typeof(DefaultSettingsProfile)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();
        string[] snapshotProperties = typeof(DefaultSettingsSnapshot)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        foreach (string prohibited in new[]
        {
            "CurrentValue",
            "Status",
            "Result",
            "StartedAt",
            "CompletedAt",
            "OutputPath",
            "ActorId",
        })
        {
            Assert.IsFalse(profileProperties.Contains(prohibited, StringComparer.Ordinal));
            Assert.IsFalse(snapshotProperties.Contains(prohibited, StringComparer.Ordinal));
        }
    }

    private static DefaultSettingsProfile CreateProfile() =>
        DefaultSettingsProfile.Create(
            ProfileId,
            "HubRadio internal defaults",
            new SettingsProfileVersion(1, 0, 0),
            [PublicationScopeDefinition(), FadeDurationDefinition()]);

    private static DefaultSettingDefinition PublicationScopeDefinition() =>
        CreateDefinition(
            key: "project.publicationScope",
            group: DefaultSettingGroup.ProjectAndOutput,
            value: DefaultSettingValue.FromText("internalOnly"),
            unit: DefaultSettingUnit.None,
            allowedTextValues: ["pendingDecision", "internalOnly"],
            evidence:
            [
                new DefaultSettingEvidence(
                    DefaultSettingEvidenceKind.UserDecision,
                    "U01"),
                ProductDecisionEvidence(),
            ]);

    private static DefaultSettingDefinition FadeDurationDefinition() =>
        CreateDefinition(
            key: "editing.crossfadeDuration",
            group: DefaultSettingGroup.Editing,
            value: DefaultSettingValue.FromInteger(0),
            unit: DefaultSettingUnit.Milliseconds,
            minimum: DefaultSettingValue.FromInteger(0),
            maximum: DefaultSettingValue.FromInteger(5000),
            evidence:
            [
                new DefaultSettingEvidence(
                    DefaultSettingEvidenceKind.PracticalGuidance,
                    "R25"),
                ProductDecisionEvidence(),
            ]);

    private static DefaultSettingDefinition CreateDefinition(
        string key = "editing.exampleValue",
        DefaultSettingGroup group = DefaultSettingGroup.Editing,
        DefaultSettingValue? value = null,
        DefaultSettingUnit unit = DefaultSettingUnit.Count,
        DefaultSettingValue? minimum = null,
        DefaultSettingValue? maximum = null,
        IReadOnlyList<string>? allowedTextValues = null,
        IReadOnlyList<DefaultSettingEvidence>? evidence = null) =>
        new(
            key,
            group,
            isAdvanced: false,
            "Example setting",
            "Used only by the focused settings-domain tests.",
            unit,
            value ?? DefaultSettingValue.FromInteger(1),
            minimum,
            maximum,
            allowedTextValues,
            evidence ?? [ProductDecisionEvidence()]);

    private static DefaultSettingEvidence ProductDecisionEvidence() =>
        new(
            DefaultSettingEvidenceKind.ProductDecision,
            "productDecision");

    private static string MutateCanonical(
        string canonicalJson,
        Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(canonicalJson)!.AsObject();
        mutate(root);
        return CanonicalJson.Canonicalize(root.ToJsonString());
    }

    private static void Reverse(JsonArray array)
    {
        JsonNode?[] reversed = array
            .Select(static item => item?.DeepClone())
            .Reverse()
            .ToArray();
        array.Clear();
        foreach (JsonNode? item in reversed)
        {
            array.Add(item);
        }
    }
}
