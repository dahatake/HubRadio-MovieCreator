using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class LayoutTests
{
    private const string DigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void MarkerTagsArePublicAndSealed()
    {
        Type[] markerTypes =
        [
            typeof(LayoutSpecIdTag),
            typeof(CueIdTag),
            typeof(TemplateIdTag),
        ];

        foreach (Type markerType in markerTypes)
        {
            Assert.IsTrue(markerType.IsPublic);
            Assert.IsTrue(markerType.IsSealed);
        }
    }

    [TestMethod]
    public void LayoutAndCueEnumsContainOnlyContractKinds()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                LayoutRegionRole.Video,
                LayoutRegionRole.Material,
                LayoutRegionRole.Subtitle,
                LayoutRegionRole.Graphic,
            },
            Enum.GetValues<LayoutRegionRole>());
        CollectionAssert.AreEqual(
            new[] { CueKind.Subtitle, CueKind.Graphic, CueKind.Audio },
            Enum.GetValues<CueKind>());
    }

    [TestMethod]
    public void RationalNumberReducesExactlyAndHasValueEquality()
    {
        RationalNumber half = new(2, 4);
        RationalNumber equivalent = new(500, 1000);

        Assert.AreEqual(1L, half.Numerator);
        Assert.AreEqual(2L, half.Denominator);
        Assert.AreEqual(half, equivalent);
        Assert.IsTrue(half == equivalent);
        Assert.IsFalse(half != equivalent);
        Assert.AreEqual(half.GetHashCode(), equivalent.GetHashCode());
        Assert.AreEqual("1/2", half.ToString());
    }

    [TestMethod]
    public void RationalNumberDefaultIsCanonicalZeroAndComparisonIsOverflowSafe()
    {
        RationalNumber zero = default;
        RationalNumber greater = new(long.MaxValue - 1, long.MaxValue);
        RationalNumber lesser = new(long.MaxValue - 2, long.MaxValue - 1);

        Assert.AreEqual(0L, zero.Numerator);
        Assert.AreEqual(1L, zero.Denominator);
        Assert.IsTrue(greater > lesser);
        Assert.IsTrue(lesser < greater);
        Assert.IsTrue(zero <= new RationalNumber(0, long.MaxValue));
    }

    [TestMethod]
    public void RationalNumberDenominatorEncodingHandlesInt64Boundary()
    {
        RationalNumber maximumDenominator = new(1, long.MaxValue);
        RationalNumber fullyReduced = new(long.MaxValue, long.MaxValue);

        Assert.AreEqual(long.MaxValue, maximumDenominator.Denominator);
        Assert.AreEqual(new RationalNumber(1, 1), fullyReduced);
        Assert.AreEqual(1L, fullyReduced.Denominator);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MinValue)]
    public void RationalNumberRejectsNonPositiveDenominator(long denominator)
    {
        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RationalNumber(1, denominator));

        Assert.AreEqual("denominator", exception.ParamName);
    }

    [TestMethod]
    public void NormalizedRectStoresCanonicalExactCoordinatesAndHasValueEquality()
    {
        NormalizedRect first = new(
            new RationalNumber(1, 8),
            new RationalNumber(3, 4),
            new RationalNumber(6, 8),
            new RationalNumber(3, 20));
        NormalizedRect equivalent = new(
            new RationalNumber(2, 16),
            new RationalNumber(6, 8),
            new RationalNumber(3, 4),
            new RationalNumber(6, 40));

        Assert.AreEqual(new RationalNumber(1, 8), first.X);
        Assert.AreEqual(new RationalNumber(3, 4), first.Y);
        Assert.AreEqual(new RationalNumber(3, 4), first.Width);
        Assert.AreEqual(new RationalNumber(3, 20), first.Height);
        Assert.AreEqual(first, equivalent);
        Assert.IsTrue(first == equivalent);
        Assert.IsFalse(first != equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
    }

    [TestMethod]
    public void NormalizedRectRejectsCoordinatesAndExtentsOutsideUnitBounds()
    {
        RationalNumber zero = default;
        RationalNumber one = new(1, 1);

        AssertParamName<ArgumentOutOfRangeException>(
            "x",
            () => new NormalizedRect(new RationalNumber(-1, 10), zero, one, one));
        AssertParamName<ArgumentOutOfRangeException>(
            "y",
            () => new NormalizedRect(zero, new RationalNumber(11, 10), one, one));
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => new NormalizedRect(zero, zero, zero, one));
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => new NormalizedRect(zero, zero, new RationalNumber(-1, 10), one));
        AssertParamName<ArgumentOutOfRangeException>(
            "height",
            () => new NormalizedRect(zero, zero, one, new RationalNumber(11, 10)));
    }

    [TestMethod]
    public void NormalizedRectChecksSumsExactlyWithoutInt64Overflow()
    {
        RationalNumber zero = default;
        RationalNumber one = new(1, 1);
        RationalNumber almostOne = new(long.MaxValue - 1, long.MaxValue);
        RationalNumber remaining = new(1, long.MaxValue);

        NormalizedRect exactBoundary = new(almostOne, almostOne, remaining, remaining);

        Assert.AreEqual(almostOne, exactBoundary.X);
        Assert.AreEqual(remaining, exactBoundary.Width);
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => new NormalizedRect(
                almostOne,
                zero,
                new RationalNumber(2, long.MaxValue),
                one));
        AssertParamName<ArgumentOutOfRangeException>(
            "height",
            () => new NormalizedRect(
                zero,
                almostOne,
                one,
                new RationalNumber(2, long.MaxValue)));
    }

    [TestMethod]
    public void LayoutSpecStoresDimensionsAndDefensivelyCopiesCollections()
    {
        LayoutRegion originalRegion = new("video-main", FullRect(), LayoutRegionRole.Video);
        SafeZone originalSafeZone = new("platform-ui", BottomHalfRect());
        List<LayoutRegion> regions = [originalRegion];
        List<SafeZone> safeZones = [originalSafeZone];
        EntityId<LayoutSpecIdTag> id = EntityId<LayoutSpecIdTag>.New();

        LayoutSpec spec = new(id, 16, 9, regions, safeZones);
        regions[0] = new LayoutRegion("replacement", FullRect(), LayoutRegionRole.Graphic);
        regions.Add(new LayoutRegion("later", FullRect(), LayoutRegionRole.Material));
        safeZones.Clear();

        Assert.AreEqual(id, spec.Id);
        Assert.AreEqual(16, spec.OutputAspectWidth);
        Assert.AreEqual(9, spec.OutputAspectHeight);
        Assert.AreEqual(1, spec.Regions.Count);
        Assert.AreEqual(originalRegion, spec.Regions[0]);
        Assert.AreEqual(1, spec.SafeZones.Count);
        Assert.AreEqual(originalSafeZone, spec.SafeZones[0]);

        IList<LayoutRegion> exposedRegions = (IList<LayoutRegion>)spec.Regions;
        IList<SafeZone> exposedSafeZones = (IList<SafeZone>)spec.SafeZones;
        Assert.ThrowsExactly<NotSupportedException>(() => exposedRegions.Add(originalRegion));
        Assert.ThrowsExactly<NotSupportedException>(() => exposedRegions[0] = originalRegion);
        Assert.ThrowsExactly<NotSupportedException>(() => exposedSafeZones.Clear());
    }

    [TestMethod]
    public void LayoutSpecAllowsEmptyRegionAndSafeZoneCollections()
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            9,
            16,
            [],
            []);

        Assert.AreEqual(0, spec.Regions.Count);
        Assert.AreEqual(0, spec.SafeZones.Count);
    }

    [TestMethod]
    public void LayoutSpecRejectsDuplicateNamesEvenWhenGeometryDiffers()
    {
        LayoutRegion firstRegion = new("shared", FullRect(), LayoutRegionRole.Video);
        LayoutRegion duplicateRegion = new("shared", BottomHalfRect(), LayoutRegionRole.Subtitle);
        SafeZone firstSafeZone = new("controls", FullRect());
        SafeZone duplicateSafeZone = new("controls", BottomHalfRect());

        AssertParamName<ArgumentException>(
            "regions",
            () => new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [firstRegion, duplicateRegion],
                []));
        AssertParamName<ArgumentException>(
            "safeZones",
            () => new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                16,
                9,
                [],
                [firstSafeZone, duplicateSafeZone]));
    }

    [TestMethod]
    public void LayoutSpecRejectsInvalidDimensionsNullCollectionsAndNullElements()
    {
        EntityId<LayoutSpecIdTag> id = EntityId<LayoutSpecIdTag>.New();

        AssertParamName<ArgumentOutOfRangeException>(
            "outputAspectWidth",
            () => new LayoutSpec(id, 0, 9, [], []));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputAspectHeight",
            () => new LayoutSpec(id, 16, -1, [], []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new LayoutSpec(null!, 16, 9, [], []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new LayoutSpec(id, 16, 9, null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new LayoutSpec(id, 16, 9, [], null!));
        AssertParamName<ArgumentException>(
            "regions",
            () => new LayoutSpec(id, 16, 9, [null!], []));
        AssertParamName<ArgumentException>(
            "safeZones",
            () => new LayoutSpec(id, 16, 9, [], [null!]));
    }

    [TestMethod]
    public void RegionAndSafeZoneValidateNamesRoleAndRect()
    {
        string[] invalidNames = [string.Empty, " ", "\t", " leading", "trailing ", "\r\n"];

        foreach (string invalidName in invalidNames)
        {
            AssertParamName<ArgumentException>(
                "name",
                () => new LayoutRegion(invalidName, FullRect(), LayoutRegionRole.Video));
            AssertParamName<ArgumentException>(
                "name",
                () => new SafeZone(invalidName, FullRect()));
        }

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new LayoutRegion("video", null!, LayoutRegionRole.Video));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SafeZone("safe", null!));
        AssertParamName<ArgumentOutOfRangeException>(
            "role",
            () => new LayoutRegion("video", FullRect(), (LayoutRegionRole)int.MaxValue));
    }

    [TestMethod]
    public void RegionAndSafeZoneUseValueEquality()
    {
        LayoutRegion firstRegion = new("subtitle", BottomHalfRect(), LayoutRegionRole.Subtitle);
        LayoutRegion equivalentRegion = new("subtitle", BottomHalfRect(), LayoutRegionRole.Subtitle);
        SafeZone firstSafeZone = new("controls", FullRect());
        SafeZone equivalentSafeZone = new("controls", FullRect());

        Assert.AreEqual(firstRegion, equivalentRegion);
        Assert.AreEqual(firstRegion.GetHashCode(), equivalentRegion.GetHashCode());
        Assert.AreEqual(firstSafeZone, equivalentSafeZone);
        Assert.AreEqual(firstSafeZone.GetHashCode(), equivalentSafeZone.GetHashCode());
    }

    [TestMethod]
    public void CuesAllowZeroStartPositiveDurationAndVisualRegionAssociation()
    {
        TimeRange range = new(default, new RationalTime(1500, 1000));
        Cue subtitle = new(EntityId<CueIdTag>.New(), CueKind.Subtitle, range, "subtitle");
        Cue graphic = new(EntityId<CueIdTag>.New(), CueKind.Graphic, range);
        Cue audio = new(EntityId<CueIdTag>.New(), CueKind.Audio, range);

        Assert.AreEqual(default(RationalTime), subtitle.Range.Start);
        Assert.AreEqual(new RationalTime(3, 2), subtitle.Range.Duration);
        Assert.AreEqual("subtitle", subtitle.RegionName);
        Assert.IsNull(graphic.RegionName);
        Assert.IsNull(audio.RegionName);
    }

    [TestMethod]
    public void CueRejectsNegativeStartAndNonPositiveDuration()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "range",
            () => new Cue(
                EntityId<CueIdTag>.New(),
                CueKind.Subtitle,
                new TimeRange(new RationalTime(-1, 1000), new RationalTime(1, 1))));
        AssertParamName<ArgumentOutOfRangeException>(
            "range",
            () => new Cue(
                EntityId<CueIdTag>.New(),
                CueKind.Graphic,
                new TimeRange(default, default)));
    }

    [TestMethod]
    public void CueRejectsUnknownKindInvalidRegionTextAndAudioRegion()
    {
        TimeRange range = new(default, new RationalTime(1, 1));

        AssertParamName<ArgumentOutOfRangeException>(
            "kind",
            () => new Cue(EntityId<CueIdTag>.New(), (CueKind)int.MaxValue, range));

        foreach (string invalidRegionName in new[] { string.Empty, " ", " subtitle", "subtitle " })
        {
            AssertParamName<ArgumentException>(
                "regionName",
                () => new Cue(
                    EntityId<CueIdTag>.New(),
                    CueKind.Subtitle,
                    range,
                    invalidRegionName));
        }

        AssertParamName<ArgumentException>(
            "regionName",
            () => new Cue(EntityId<CueIdTag>.New(), CueKind.Audio, range, "audio"));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Cue(null!, CueKind.Audio, range));
    }

    [TestMethod]
    public void CueUsesValueEqualityAndHasNoPrematurePayload()
    {
        EntityId<CueIdTag> id = EntityId<CueIdTag>.New();
        TimeRange range = new(default, new RationalTime(1, 1));
        Cue first = new(id, CueKind.Subtitle, range, "subtitle");
        Cue equivalent = new(
            EntityId<CueIdTag>.From(id.Value),
            CueKind.Subtitle,
            new TimeRange(default, new RationalTime(1000, 1000)),
            "subtitle");
        string[] propertyNames = typeof(Cue).GetProperties().Select(static property => property.Name).ToArray();

        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.IsFalse(propertyNames.Contains("Text", StringComparer.Ordinal));
        Assert.IsFalse(propertyNames.Contains("Audio", StringComparer.Ordinal));
        Assert.IsFalse(propertyNames.Contains("Payload", StringComparer.Ordinal));
    }

    [TestMethod]
    public void TemplateReferenceStoresMinimalVersionedIdentityAndUsesValueEquality()
    {
        EntityId<TemplateIdTag> id = EntityId<TemplateIdTag>.New();
        ContentDigest digest = new(DigestHex);
        TemplateReference first = new(id, "short-vertical", digest);
        TemplateReference equivalent = new(
            EntityId<TemplateIdTag>.From(id.Value),
            "short-vertical",
            new ContentDigest(DigestHex.ToUpperInvariant()));

        Assert.AreEqual(id, first.Id);
        Assert.AreEqual("short-vertical", first.Name);
        Assert.AreEqual(digest, first.VersionDigest);
        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
    }

    [TestMethod]
    public void TemplateReferenceRejectsNullIdentityDigestAndInvalidName()
    {
        EntityId<TemplateIdTag> id = EntityId<TemplateIdTag>.New();
        ContentDigest digest = new(DigestHex);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TemplateReference(null!, "template", digest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TemplateReference(id, "template", null!));

        foreach (string invalidName in new[] { string.Empty, " ", " template", "template " })
        {
            AssertParamName<ArgumentException>(
                "name",
                () => new TemplateReference(id, invalidName, digest));
        }
    }

    private static NormalizedRect FullRect()
    {
        return new NormalizedRect(default, default, new RationalNumber(1, 1), new RationalNumber(1, 1));
    }

    private static NormalizedRect BottomHalfRect()
    {
        return new NormalizedRect(
            default,
            new RationalNumber(1, 2),
            new RationalNumber(1, 1),
            new RationalNumber(1, 2));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}