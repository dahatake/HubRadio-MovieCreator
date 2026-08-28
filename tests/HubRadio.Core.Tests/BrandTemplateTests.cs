using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Templates;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class BrandTemplateTests
{
    private const string DigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void ContractEnumsAndMarkerHaveOnlyTheRequiredKinds()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                BrandTemplateKind.LongForm,
                BrandTemplateKind.YouTubeLandscape,
                BrandTemplateKind.YouTubeShorts,
                BrandTemplateKind.TikTok,
                BrandTemplateKind.Quiz,
                BrandTemplateKind.Announcement,
                BrandTemplateKind.Thumbnail,
                BrandTemplateKind.Opening,
                BrandTemplateKind.Ending,
                BrandTemplateKind.Telop,
                BrandTemplateKind.Subtitle,
                BrandTemplateKind.TopicCard,
                BrandTemplateKind.CallToAction,
                BrandTemplateKind.PublicationScopeIndicator,
            },
            Enum.GetValues<BrandTemplateKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                TemplateParameterValueKind.Text,
                TemplateParameterValueKind.Integer,
                TemplateParameterValueKind.Boolean,
                TemplateParameterValueKind.Rational,
                TemplateParameterValueKind.AssetReference,
            },
            Enum.GetValues<TemplateParameterValueKind>());
        Assert.IsTrue(typeof(SeriesIdTag).IsPublic);
        Assert.IsTrue(typeof(SeriesIdTag).IsSealed);
    }

    [TestMethod]
    public void ParameterValuesAreClosedTypedValuesWithValueEquality()
    {
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        ContentDigest digest = new(DigestHex);
        TemplateParameterValue text = TemplateParameterValue.FromText("brand-blue");
        TemplateParameterValue integer = TemplateParameterValue.FromInteger(long.MinValue);
        TemplateParameterValue boolean = TemplateParameterValue.FromBoolean(true);
        TemplateParameterValue rational = TemplateParameterValue.FromRational(new RationalNumber(2, 4));
        TemplateParameterValue asset = TemplateParameterValue.FromAssetReference(assetId, digest);
        TemplateParameterValue equivalentAsset = TemplateParameterValue.FromAssetReference(
            EntityId<AssetIdTag>.From(assetId.Value),
            new ContentDigest(DigestHex.ToUpperInvariant()));

        Assert.AreEqual(TemplateParameterValueKind.Text, text.Kind);
        Assert.AreEqual("brand-blue", text.TextValue);
        Assert.AreEqual(long.MinValue, integer.IntegerValue);
        Assert.AreEqual(true, boolean.BooleanValue);
        Assert.AreEqual(new RationalNumber(1, 2), rational.RationalValue);
        Assert.AreEqual(assetId, asset.AssetId);
        Assert.AreEqual(digest, asset.AssetDigest);
        Assert.AreEqual(asset, equivalentAsset);
        Assert.AreEqual(asset.GetHashCode(), equivalentAsset.GetHashCode());
        Assert.AreNotEqual(text, integer);
    }

    [TestMethod]
    public void ParameterValuesRejectInvalidReferencesAndText()
    {
        ContentDigest digest = new(DigestHex);
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();

        foreach (string invalid in new[] { string.Empty, " ", " leading", "trailing ", "\t" })
        {
            AssertParamName<ArgumentException>(
                "value",
                () => TemplateParameterValue.FromText(invalid));
        }

        Assert.ThrowsExactly<ArgumentNullException>(
            () => TemplateParameterValue.FromText(null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => TemplateParameterValue.FromAssetReference(null!, digest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => TemplateParameterValue.FromAssetReference(assetId, null!));
    }

    [TestMethod]
    public void ParameterDefinitionsDefensivelyCopyApprovedValuesAndExposeFixedState()
    {
        TemplateParameterValue defaultValue = TemplateParameterValue.FromText("brand-blue");
        TemplateParameterValue approved = TemplateParameterValue.FromText("brand-yellow");
        List<TemplateParameterValue> allowed = [approved];
        TemplateParameterDefinition restricted = new(
            "accent-color",
            defaultValue,
            editorOverrideAllowed: true,
            allowed);
        TemplateParameterDefinition unrestricted = new(
            "episode-title",
            TemplateParameterValue.FromText("Default title"),
            editorOverrideAllowed: true);
        TemplateParameterDefinition fixedParameter = new(
            "brand-font",
            defaultValue,
            editorOverrideAllowed: false);

        allowed[0] = TemplateParameterValue.FromText("unapproved");
        allowed.Add(TemplateParameterValue.FromText("another-unapproved"));

        Assert.AreEqual("accent-color", restricted.Name);
        Assert.AreSame(defaultValue, restricted.DefaultValue);
        Assert.IsTrue(restricted.EditorOverrideAllowed);
        Assert.IsNotNull(restricted.EditorValueAllowlist);
        Assert.AreEqual(1, restricted.EditorValueAllowlist.Count);
        Assert.AreSame(approved, restricted.EditorValueAllowlist[0]);
        Assert.IsTrue(unrestricted.EditorOverrideAllowed);
        Assert.IsNull(unrestricted.EditorValueAllowlist);
        Assert.IsFalse(fixedParameter.EditorOverrideAllowed);
        Assert.IsNull(fixedParameter.EditorValueAllowlist);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<TemplateParameterValue>)restricted.EditorValueAllowlist).Add(defaultValue));
    }

    [TestMethod]
    public void ParameterDefinitionsRejectNonCanonicalNamesAndInvalidAllowedValues()
    {
        TemplateParameterValue text = TemplateParameterValue.FromText("default");
        TemplateParameterValue boolean = TemplateParameterValue.FromBoolean(true);
        string[] invalidNames =
        [
            string.Empty,
            "Accent-color",
            "accent_color",
            "accent--color",
            "accent-color-",
            " accent-color",
            "accent-color ",
            "аccent-color",
            "色",
        ];

        foreach (string invalidName in invalidNames)
        {
            AssertParamName<ArgumentException>(
                "name",
                () => new TemplateParameterDefinition(
                    invalidName,
                    text,
                    editorOverrideAllowed: false));
            AssertParamName<ArgumentException>(
                "name",
                () => new TemplateParameterAssignment(invalidName, text));
        }

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TemplateParameterDefinition(null!, text, editorOverrideAllowed: false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TemplateParameterDefinition("accent-color", null!, editorOverrideAllowed: false));
        AssertParamName<ArgumentException>(
            "editorValueAllowlist",
            () => new TemplateParameterDefinition(
                "accent-color",
                text,
                editorOverrideAllowed: false,
                [text]));
        AssertParamName<ArgumentException>(
            "editorValueAllowlist",
            () => new TemplateParameterDefinition(
                "accent-color",
                text,
                editorOverrideAllowed: true,
                []));
        AssertParamName<ArgumentException>(
            "editorValueAllowlist",
            () => new TemplateParameterDefinition(
                "accent-color",
                text,
                editorOverrideAllowed: true,
                [null!]));
        AssertParamName<ArgumentException>(
            "editorValueAllowlist",
            () => new TemplateParameterDefinition(
                "accent-color",
                text,
                editorOverrideAllowed: true,
                [boolean]));
        AssertParamName<ArgumentException>(
            "editorValueAllowlist",
            () => new TemplateParameterDefinition(
                "accent-color",
                text,
                editorOverrideAllowed: true,
                [text, text]));
        AssertParamName<ArgumentException>(
            "editorValueAllowlist",
            () => new TemplateParameterDefinition(
                "scale",
                TemplateParameterValue.FromRational(new RationalNumber(1, 2)),
                editorOverrideAllowed: true,
                [
                    TemplateParameterValue.FromRational(new RationalNumber(1, 2)),
                    TemplateParameterValue.FromRational(new RationalNumber(2, 4)),
                ]));
    }

    [TestMethod]
    public void TemplateVersionStoresSeriesVersionAndDefinitionsImmutably()
    {
        EntityId<SeriesIdTag> seriesId = EntityId<SeriesIdTag>.New();
        TemplateReference reference = CreateReference();
        TemplateParameterDefinition original = new(
            "accent-color",
            TemplateParameterValue.FromText("brand-blue"),
            editorOverrideAllowed: true,
            [TemplateParameterValue.FromText("brand-yellow")]);
        List<TemplateParameterDefinition> definitions = [original];

        BrandTemplateVersion version = new(
            seriesId,
            reference,
            BrandTemplateKind.YouTubeShorts,
            definitions);
        definitions[0] = new(
            "different",
            TemplateParameterValue.FromBoolean(false),
            editorOverrideAllowed: false);
        definitions.Clear();

        Assert.AreEqual(seriesId, version.SeriesId);
        Assert.AreSame(reference, version.Reference);
        Assert.AreEqual(BrandTemplateKind.YouTubeShorts, version.Kind);
        Assert.AreEqual(1, version.ParameterDefinitions.Count);
        Assert.AreSame(original, version.ParameterDefinitions[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<TemplateParameterDefinition>)version.ParameterDefinitions).Clear());
    }

    [TestMethod]
    public void TemplateVersionAllowsFullyLockedTemplatesAndRejectsInvalidAggregateInput()
    {
        EntityId<SeriesIdTag> seriesId = EntityId<SeriesIdTag>.New();
        TemplateReference reference = CreateReference();
        TemplateParameterDefinition definition = new(
            "brand-font",
            TemplateParameterValue.FromText("approved-font"),
            editorOverrideAllowed: false);

        BrandTemplateVersion locked = new(
            seriesId,
            reference,
            BrandTemplateKind.Opening,
            []);

        Assert.AreEqual(0, locked.ParameterDefinitions.Count);
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BrandTemplateVersion(null!, reference, BrandTemplateKind.Opening, []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BrandTemplateVersion(seriesId, null!, BrandTemplateKind.Opening, []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BrandTemplateVersion(seriesId, reference, BrandTemplateKind.Opening, null!));
        AssertParamName<ArgumentOutOfRangeException>(
            "kind",
            () => new BrandTemplateVersion(seriesId, reference, (BrandTemplateKind)999, []));
        AssertParamName<ArgumentException>(
            "parameterDefinitions",
            () => new BrandTemplateVersion(
                seriesId,
                reference,
                BrandTemplateKind.Opening,
                [null!]));
        AssertParamName<ArgumentException>(
            "parameterDefinitions",
            () => new BrandTemplateVersion(
                seriesId,
                reference,
                BrandTemplateKind.Opening,
                [definition, new TemplateParameterDefinition(
                    "brand-font",
                    TemplateParameterValue.FromText("another-font"),
                    editorOverrideAllowed: false)]));
    }

    [TestMethod]
    public void ApplyEditorOverridesResolvesOnlyExplicitlyApprovedValues()
    {
        TemplateParameterValue brandBlue = TemplateParameterValue.FromText("brand-blue");
        TemplateParameterValue brandYellow = TemplateParameterValue.FromText("brand-yellow");
        TemplateParameterValue approvedLogo = TemplateParameterValue.FromAssetReference(
            EntityId<AssetIdTag>.New(),
            new ContentDigest(DigestHex));
        BrandTemplateVersion version = CreateVersion(
            new TemplateParameterDefinition(
                "accent-color",
                brandBlue,
                editorOverrideAllowed: true,
                [brandYellow]),
            new TemplateParameterDefinition(
                "brand-logo",
                approvedLogo,
                editorOverrideAllowed: false),
            new TemplateParameterDefinition(
                "show-topic-card",
                TemplateParameterValue.FromBoolean(true),
                editorOverrideAllowed: true,
                [TemplateParameterValue.FromBoolean(false)]));
        List<TemplateParameterAssignment> overrides =
        [
            new("accent-color", brandYellow),
            new("show-topic-card", TemplateParameterValue.FromBoolean(false)),
        ];

        AppliedBrandTemplate applied = version.ApplyEditorOverrides(overrides);
        overrides.Clear();

        Assert.AreEqual(version.SeriesId, applied.SeriesId);
        Assert.AreSame(version.Reference, applied.Reference);
        Assert.AreEqual(version.Kind, applied.Kind);
        Assert.AreEqual(3, applied.Parameters.Count);
        Assert.AreEqual(brandYellow, FindValue(applied, "accent-color"));
        Assert.AreSame(approvedLogo, FindValue(applied, "brand-logo"));
        Assert.AreEqual(
            TemplateParameterValue.FromBoolean(false),
            FindValue(applied, "show-topic-card"));
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<TemplateParameterAssignment>)applied.Parameters).Clear());
    }

    [TestMethod]
    public void ApplyEditorOverridesRejectsUnknownFixedUnapprovedDuplicateAndNullAssignments()
    {
        TemplateParameterValue defaultColor = TemplateParameterValue.FromText("brand-blue");
        TemplateParameterValue allowedColor = TemplateParameterValue.FromText("brand-yellow");
        BrandTemplateVersion version = CreateVersion(
            new TemplateParameterDefinition(
                "accent-color",
                defaultColor,
                editorOverrideAllowed: true,
                [allowedColor]),
            new TemplateParameterDefinition(
                "brand-font",
                TemplateParameterValue.FromText("approved-font"),
                editorOverrideAllowed: false));

        Assert.ThrowsExactly<ArgumentNullException>(() => version.ApplyEditorOverrides(null!));
        AssertOverridesRejected(
            version,
            [new TemplateParameterAssignment("unknown", allowedColor)]);
        AssertOverridesRejected(
            version,
            [new TemplateParameterAssignment("brand-font", TemplateParameterValue.FromText("other-font"))]);
        AssertOverridesRejected(
            version,
            [new TemplateParameterAssignment("accent-color", TemplateParameterValue.FromText("unapproved"))]);
        AssertOverridesRejected(
            version,
            [new TemplateParameterAssignment("accent-color", TemplateParameterValue.FromBoolean(true))]);
        AssertOverridesRejected(
            version,
            [
                new TemplateParameterAssignment("accent-color", allowedColor),
                new TemplateParameterAssignment("accent-color", allowedColor),
            ]);
        AssertOverridesRejected(version, [null!]);
    }

    [TestMethod]
    public void ApplyEditorOverridesAllowsDynamicValuesOnlyForExplicitlyEditableParameters()
    {
        BrandTemplateVersion version = CreateVersion(
            new TemplateParameterDefinition(
                "episode-title",
                TemplateParameterValue.FromText("Default title"),
                editorOverrideAllowed: true));
        TemplateParameterValue episodeTitle = TemplateParameterValue.FromText("Episode 42");

        AppliedBrandTemplate applied = version.ApplyEditorOverrides(
            [new TemplateParameterAssignment("episode-title", episodeTitle)]);

        Assert.AreSame(episodeTitle, FindValue(applied, "episode-title"));
        AssertOverridesRejected(
            version,
            [new TemplateParameterAssignment(
                "episode-title",
                TemplateParameterValue.FromInteger(42))]);
    }

    [TestMethod]
    public void DefinitionsAndAssignmentsHaveValueEqualityAcrossIndependentInstances()
    {
        TemplateParameterDefinition firstDefinition = new(
            "accent-color",
            TemplateParameterValue.FromText("brand-blue"),
            editorOverrideAllowed: true,
            [TemplateParameterValue.FromText("brand-yellow")]);
        TemplateParameterDefinition equivalentDefinition = new(
            "accent-color",
            TemplateParameterValue.FromText("brand-blue"),
            editorOverrideAllowed: true,
            [TemplateParameterValue.FromText("brand-yellow")]);
        TemplateParameterDefinition unrestrictedDefinition = new(
            "accent-color",
            TemplateParameterValue.FromText("brand-blue"),
            editorOverrideAllowed: true);
        TemplateParameterAssignment firstAssignment = new(
            "accent-color",
            TemplateParameterValue.FromText("brand-yellow"));
        TemplateParameterAssignment equivalentAssignment = new(
            "accent-color",
            TemplateParameterValue.FromText("brand-yellow"));

        Assert.AreEqual(firstDefinition, equivalentDefinition);
        Assert.AreEqual(firstDefinition.GetHashCode(), equivalentDefinition.GetHashCode());
        Assert.AreNotEqual(firstDefinition, unrestrictedDefinition);
        Assert.AreEqual(firstAssignment, equivalentAssignment);
        Assert.AreEqual(firstAssignment.GetHashCode(), equivalentAssignment.GetHashCode());
        Assert.AreNotEqual(
            firstAssignment,
            new TemplateParameterAssignment(
                "background-color",
                TemplateParameterValue.FromText("brand-yellow")));
        Assert.AreNotEqual(
            firstAssignment,
            new TemplateParameterAssignment(
                "accent-color",
                TemplateParameterValue.FromText("brand-blue")));
    }

    [TestMethod]
    public void AppliedTemplateEqualityIncludesSeriesReferenceKindAndParameters()
    {
        EntityId<SeriesIdTag> seriesId = EntityId<SeriesIdTag>.New();
        TemplateReference reference = CreateReference();
        TemplateParameterDefinition parameter = new(
            "episode-title",
            TemplateParameterValue.FromText("Default title"),
            editorOverrideAllowed: true);
        AppliedBrandTemplate baseline = CreateApplied(seriesId, reference, BrandTemplateKind.Opening, parameter);
        AppliedBrandTemplate equivalent = CreateApplied(
            EntityId<SeriesIdTag>.From(seriesId.Value),
            new TemplateReference(
                EntityId<TemplateIdTag>.From(reference.Id.Value),
                reference.Name,
                new ContentDigest(reference.VersionDigest.Hex)),
            BrandTemplateKind.Opening,
            new TemplateParameterDefinition(
                "episode-title",
                TemplateParameterValue.FromText("Default title"),
                editorOverrideAllowed: true));

        Assert.AreEqual(baseline, equivalent);
        Assert.AreEqual(baseline.GetHashCode(), equivalent.GetHashCode());
        Assert.AreNotEqual(
            baseline,
            CreateApplied(EntityId<SeriesIdTag>.New(), reference, BrandTemplateKind.Opening, parameter));
        Assert.AreNotEqual(
            baseline,
            CreateApplied(
                seriesId,
                new TemplateReference(EntityId<TemplateIdTag>.New(), reference.Name, reference.VersionDigest),
                BrandTemplateKind.Opening,
                parameter));
        Assert.AreNotEqual(
            baseline,
            CreateApplied(
                seriesId,
                new TemplateReference(reference.Id, "other-name", reference.VersionDigest),
                BrandTemplateKind.Opening,
                parameter));
        Assert.AreNotEqual(
            baseline,
            CreateApplied(
                seriesId,
                new TemplateReference(reference.Id, reference.Name, new ContentDigest(new string('0', 64))),
                BrandTemplateKind.Opening,
                parameter));
        Assert.AreNotEqual(
            baseline,
            CreateApplied(seriesId, reference, BrandTemplateKind.Ending, parameter));
        Assert.AreNotEqual(
            baseline,
            CreateApplied(
                seriesId,
                reference,
                BrandTemplateKind.Opening,
                new TemplateParameterDefinition(
                    "episode-name",
                    TemplateParameterValue.FromText("Default title"),
                    editorOverrideAllowed: true)));
        Assert.AreNotEqual(
            baseline,
            CreateApplied(
                seriesId,
                reference,
                BrandTemplateKind.Opening,
                new TemplateParameterDefinition(
                    "episode-title",
                    TemplateParameterValue.FromText("Other title"),
                    editorOverrideAllowed: true)));
    }

    [TestMethod]
    public void AppliedTemplatesUseValueEqualityWithoutMutatingPriorApplications()
    {
        TemplateParameterValue firstValue = TemplateParameterValue.FromInteger(1);
        TemplateParameterValue secondValue = TemplateParameterValue.FromInteger(2);
        BrandTemplateVersion version = CreateVersion(
            new TemplateParameterDefinition(
                "title-size",
                firstValue,
                editorOverrideAllowed: true,
                [secondValue]));
        BrandTemplateVersion equivalentVersion = new(
            EntityId<SeriesIdTag>.From(version.SeriesId.Value),
            new TemplateReference(
                EntityId<TemplateIdTag>.From(version.Reference.Id.Value),
                version.Reference.Name,
                new ContentDigest(version.Reference.VersionDigest.Hex)),
            version.Kind,
            version.ParameterDefinitions);

        AppliedBrandTemplate first = version.ApplyEditorOverrides([]);
        AppliedBrandTemplate firstEquivalent = equivalentVersion.ApplyEditorOverrides([]);
        AppliedBrandTemplate second = version.ApplyEditorOverrides(
            [new TemplateParameterAssignment("title-size", secondValue)]);

        Assert.AreEqual(first, firstEquivalent);
        Assert.AreEqual(first.GetHashCode(), firstEquivalent.GetHashCode());
        Assert.AreNotEqual(first, second);
        Assert.AreSame(firstValue, FindValue(first, "title-size"));
        Assert.AreSame(secondValue, FindValue(second, "title-size"));
    }

    private static BrandTemplateVersion CreateVersion(
        params TemplateParameterDefinition[] definitions)
    {
        return new BrandTemplateVersion(
            EntityId<SeriesIdTag>.New(),
            CreateReference(),
            BrandTemplateKind.YouTubeShorts,
            definitions);
    }

    private static AppliedBrandTemplate CreateApplied(
        EntityId<SeriesIdTag> seriesId,
        TemplateReference reference,
        BrandTemplateKind kind,
        params TemplateParameterDefinition[] definitions)
    {
        return new BrandTemplateVersion(seriesId, reference, kind, definitions)
            .ApplyEditorOverrides([]);
    }

    private static TemplateReference CreateReference()
    {
        return new TemplateReference(
            EntityId<TemplateIdTag>.New(),
            "short-vertical",
            new ContentDigest(DigestHex));
    }

    private static TemplateParameterValue FindValue(
        AppliedBrandTemplate applied,
        string name)
    {
        return applied.Parameters.Single(parameter => parameter.Name == name).Value;
    }

    private static void AssertOverridesRejected(
        BrandTemplateVersion version,
        IReadOnlyList<TemplateParameterAssignment> overrides)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => version.ApplyEditorOverrides(overrides));
        Assert.AreEqual("editorOverrides", exception.ParamName);
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}