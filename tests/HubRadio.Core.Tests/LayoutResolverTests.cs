using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class LayoutResolverTests
{
    [TestMethod]
    public void Resolve_StageZeroControlRectangles_MapsExactly()
    {
        ResolvedLayout resolved = LayoutResolver.Resolve(CreateStageZeroLayout(), 1280, 720);

        Assert.AreEqual(1280, resolved.OutputPixelWidth);
        Assert.AreEqual(720, resolved.OutputPixelHeight);
        AssertRect(resolved.SafeZones[0].Rect, 48, 27, 1184, 666);
        AssertRect(resolved.Regions[0].Rect, 64, 50, 448, 90);
        AssertRect(resolved.Regions[1].Rect, 64, 50, 12, 90);
        AssertRect(resolved.Regions[2].Rect, 160, 540, 960, 108);
    }

    [TestMethod]
    public void Resolve_RegionsAndSafeZones_PreserveMetadataAndOrder()
    {
        LayoutSpec spec = CreateStageZeroLayout();

        ResolvedLayout resolved = LayoutResolver.Resolve(spec, 1280, 720);

        Assert.AreEqual(spec.Id, resolved.LayoutSpecId);
        CollectionAssert.AreEqual(
            new[] { "topic-panel", "Topic-Accent", "cue-box" },
            resolved.Regions.Select(static region => region.Name).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                LayoutRegionRole.Graphic,
                LayoutRegionRole.Graphic,
                LayoutRegionRole.Subtitle,
            },
            resolved.Regions.Select(static region => region.Role).ToArray());
        CollectionAssert.AreEqual(
            new[] { "safe-frame" },
            resolved.SafeZones.Select(static zone => zone.Name).ToArray());
    }

    [TestMethod]
    public void Resolve_PortraitLayout_DoesNotSwapAxes()
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            9,
            16,
            [
                new LayoutRegion(
                    "portrait",
                    Rect(1, 4, 1, 8, 1, 2, 3, 4),
                    LayoutRegionRole.Video),
            ],
            []);

        ResolvedLayout resolved = LayoutResolver.Resolve(spec, 1080, 1920);

        AssertRect(resolved.Regions[0].Rect, 270, 240, 540, 1440);
    }

    [TestMethod]
    public void Resolve_EquivalentAspectDeclaration_IsAccepted()
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            32,
            18,
            [new LayoutRegion("full", FullRect(), LayoutRegionRole.Video)],
            []);

        ResolvedLayout resolved = LayoutResolver.Resolve(spec, 1280, 720);

        AssertRect(resolved.Regions[0].Rect, 0, 0, 1280, 720);
    }

    [TestMethod]
    [DataRow(16, 9, 1080, 1920)]
    [DataRow(9, 16, 1280, 720)]
    public void Resolve_MismatchedOutputAspect_IsRejected(
        int aspectWidth,
        int aspectHeight,
        int outputWidth,
        int outputHeight)
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            aspectWidth,
            aspectHeight,
            [],
            []);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => LayoutResolver.Resolve(spec, outputWidth, outputHeight));

        Assert.AreEqual("outputPixelWidth", exception.ParamName);
    }

    [TestMethod]
    [DataRow(0, 720, "outputPixelWidth")]
    [DataRow(-1, 720, "outputPixelWidth")]
    [DataRow(1280, 0, "outputPixelHeight")]
    [DataRow(1280, -1, "outputPixelHeight")]
    public void Resolve_NonPositiveOutputDimension_IsRejected(
        int outputWidth,
        int outputHeight,
        string expectedParameterName)
    {
        LayoutSpec spec = new(EntityId<LayoutSpecIdTag>.New(), 16, 9, [], []);

        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => LayoutResolver.Resolve(spec, outputWidth, outputHeight));

        Assert.AreEqual(expectedParameterName, exception.ParamName);
    }

    [TestMethod]
    public void Resolve_NullLayoutSpec_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => LayoutResolver.Resolve(null!, 1280, 720));
    }

    [TestMethod]
    public void Resolve_FractionalPixelProduct_IsRejectedRatherThanRounded()
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            16,
            9,
            [
                new LayoutRegion(
                    "fractional",
                    Rect(1, 3, 0, 1, 2, 3, 1, 1),
                    LayoutRegionRole.Video),
            ],
            []);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => LayoutResolver.Resolve(spec, 1280, 720));
    }

    [TestMethod]
    public void Resolve_StageZeroGeometryOnIncompatiblePixelGrid_IsRejected()
    {
        LayoutSpec spec = CreateStageZeroLayout();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => LayoutResolver.Resolve(spec, 1920, 1080));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => LayoutResolver.Resolve(spec, 1024, 576));
    }

    [TestMethod]
    public void Resolve_LargeRationalOperands_UseOverflowSafeExactArithmetic()
    {
        RationalNumber tinyOffset = new(1, long.MaxValue);
        RationalNumber remainingWidth = new(long.MaxValue - 1, long.MaxValue);
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            1,
            1,
            [
                new LayoutRegion(
                    "large-operands",
                    new NormalizedRect(tinyOffset, default, remainingWidth, new RationalNumber(1, 1)),
                    LayoutRegionRole.Video),
            ],
            []);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => LayoutResolver.Resolve(spec, int.MaxValue, int.MaxValue));
    }

    [TestMethod]
    public void Resolve_FullAndLastPixelRectangles_StayWithinSurface()
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            4,
            3,
            [
                new LayoutRegion("full", FullRect(), LayoutRegionRole.Video),
                new LayoutRegion(
                    "last-pixel",
                    Rect(3, 4, 2, 3, 1, 4, 1, 3),
                    LayoutRegionRole.Graphic),
            ],
            []);

        ResolvedLayout resolved = LayoutResolver.Resolve(spec, 4, 3);

        AssertRect(resolved.Regions[0].Rect, 0, 0, 4, 3);
        AssertRect(resolved.Regions[1].Rect, 3, 2, 1, 1);
    }

    [TestMethod]
    public void Resolve_EmptyLayout_ReturnsImmutableEmptyCollections()
    {
        LayoutSpec spec = new(EntityId<LayoutSpecIdTag>.New(), 16, 9, [], []);

        ResolvedLayout resolved = LayoutResolver.Resolve(spec, 1280, 720);

        Assert.AreEqual(0, resolved.Regions.Count);
        Assert.AreEqual(0, resolved.SafeZones.Count);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<ResolvedLayoutRegion>)resolved.Regions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<ResolvedSafeZone>)resolved.SafeZones).Clear());
    }

    [TestMethod]
    public void Resolve_RepeatedForPreviewAndRender_ProducesIdenticalGeometry()
    {
        LayoutSpec spec = CreateStageZeroLayout();

        ResolvedLayout preview = LayoutResolver.Resolve(spec, 1280, 720);
        ResolvedLayout render = LayoutResolver.Resolve(spec, 1280, 720);

        CollectionAssert.AreEqual(
            preview.Regions.Select(static region => region.Rect).ToArray(),
            render.Regions.Select(static region => region.Rect).ToArray());
        CollectionAssert.AreEqual(
            preview.SafeZones.Select(static zone => zone.Rect).ToArray(),
            render.SafeZones.Select(static zone => zone.Rect).ToArray());
    }

    [TestMethod]
    public void ResolvedTypes_AreSealedWithReadOnlyPublicProperties()
    {
        Type[] types =
        [
            typeof(PixelRect),
            typeof(ResolvedLayoutRegion),
            typeof(ResolvedSafeZone),
            typeof(ResolvedLayout),
        ];

        foreach (Type type in types)
        {
            Assert.IsTrue(type.IsSealed);
            Assert.IsTrue(type.GetProperties().All(static property => property.SetMethod is null));
        }
    }

    private static LayoutSpec CreateStageZeroLayout()
    {
        return new LayoutSpec(
            EntityId<LayoutSpecIdTag>.New(),
            16,
            9,
            [
                new LayoutRegion(
                    "topic-panel",
                    Rect(1, 20, 5, 72, 7, 20, 1, 8),
                    LayoutRegionRole.Graphic),
                new LayoutRegion(
                    "Topic-Accent",
                    Rect(1, 20, 5, 72, 3, 320, 1, 8),
                    LayoutRegionRole.Graphic),
                new LayoutRegion(
                    "cue-box",
                    Rect(1, 8, 3, 4, 3, 4, 3, 20),
                    LayoutRegionRole.Subtitle),
            ],
            [
                new SafeZone(
                    "safe-frame",
                    Rect(3, 80, 3, 80, 37, 40, 37, 40)),
            ]);
    }

    private static NormalizedRect FullRect()
    {
        return Rect(0, 1, 0, 1, 1, 1, 1, 1);
    }

    private static NormalizedRect Rect(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator)
    {
        return new NormalizedRect(
            new RationalNumber(xNumerator, xDenominator),
            new RationalNumber(yNumerator, yDenominator),
            new RationalNumber(widthNumerator, widthDenominator),
            new RationalNumber(heightNumerator, heightDenominator));
    }

    private static void AssertRect(
        PixelRect actual,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.AreEqual(expectedX, actual.X);
        Assert.AreEqual(expectedY, actual.Y);
        Assert.AreEqual(expectedWidth, actual.Width);
        Assert.AreEqual(expectedHeight, actual.Height);
    }
}