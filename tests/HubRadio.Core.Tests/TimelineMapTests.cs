using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TimelineMapTests
{
    [TestMethod]
    public void ContractsAreImmutableAndUseTypedVersionIdentity()
    {
        Assert.IsTrue(typeof(TimelineMapIdTag).IsPublic);
        Assert.IsTrue(typeof(TimelineMapIdTag).IsSealed);
        AssertPublicGetOnlyProperties(typeof(TimelineMapPoint), "Origin", "SourceTime");
        AssertPublicGetOnlyProperties(
            typeof(TimelineMapEntry),
            "OutputRange",
            "Origin",
            "SourceRange");
        AssertPublicGetOnlyProperties(
            typeof(TimelineMap),
            "Id",
            "CompositionVersionId",
            "Entries");
        Assert.AreEqual(
            typeof(EntityId<CompositionVersionIdTag>),
            typeof(TimelineMap).GetProperty("CompositionVersionId")!.PropertyType);
    }

    [TestMethod]
    public void CutMapsOutputToExactSourceTimeAndBack()
    {
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        TimelineMap map = CreateMap([
            SourceEntry(
                sourceSpanId,
                outputStart: 0,
                sourceStart: 10,
                duration: 30),
        ]);

        Assert.IsTrue(map.TryMapOutputTime(Time(15), out TimelineMapPoint mapped));
        Assert.AreEqual(OriginRef.FromSourceSpan(sourceSpanId), mapped.Origin);
        Assert.AreEqual(Time(25), mapped.SourceTime);

        IReadOnlyList<RationalTime> outputTimes =
            map.MapSourceTimeToOutput(sourceSpanId, mapped.SourceTime!.Value);
        CollectionAssert.AreEqual(new[] { Time(15) }, outputTimes.ToArray());
    }

    [TestMethod]
    public void ConcatUsesHalfOpenBoundaryAndRoundTripsEachSource()
    {
        EntityId<SourceSpanIdTag> firstId = EntityId<SourceSpanIdTag>.New();
        EntityId<SourceSpanIdTag> secondId = EntityId<SourceSpanIdTag>.New();
        TimelineMap map = CreateMap([
            SourceEntry(firstId, outputStart: 0, sourceStart: 2, duration: 3),
            SourceEntry(secondId, outputStart: 3, sourceStart: 7, duration: 3),
        ]);

        Assert.IsTrue(map.TryMapOutputTime(Time(2999, 1000), out TimelineMapPoint before));
        Assert.AreEqual(firstId, before.Origin.SourceSpanId);
        Assert.IsTrue(map.TryMapOutputTime(Time(3), out TimelineMapPoint boundary));
        Assert.AreEqual(secondId, boundary.Origin.SourceSpanId);
        Assert.AreEqual(Time(7), boundary.SourceTime);
        CollectionAssert.AreEqual(
            new[] { Time(3) },
            map.MapSourceTimeToOutput(secondId, Time(7)).ToArray());
    }

    [TestMethod]
    public void StillImageMapsToAssetWithoutInventingSourceTime()
    {
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        OriginRef origin = OriginRef.FromAsset(assetId);
        TimelineMap map = CreateMap([
            OriginEntry(origin, outputStart: 0, duration: 5),
        ]);

        Assert.IsTrue(map.TryMapOutputTime(Time(2), out TimelineMapPoint mapped));
        Assert.AreEqual(OriginKind.Asset, mapped.Origin.Kind);
        Assert.AreEqual(assetId, mapped.Origin.AssetId);
        Assert.IsNull(mapped.SourceTime);
        CollectionAssert.AreEqual(
            new[] { new TimeRange(default, Time(5)) },
            map.MapOriginToOutputRanges(mapped.Origin).ToArray());
    }

    [TestMethod]
    public void OpeningAndCtaMapToTheirDistinctEditorialRecords()
    {
        EntityId<EditorialRecordIdTag> openingId = EntityId<EditorialRecordIdTag>.New();
        EntityId<EditorialRecordIdTag> ctaId = EntityId<EditorialRecordIdTag>.New();
        OriginRef openingOrigin = OriginRef.FromEditorialRecord(openingId);
        OriginRef ctaOrigin = OriginRef.FromEditorialRecord(ctaId);
        TimelineMap map = CreateMap([
            OriginEntry(openingOrigin, 0, 2),
            OriginEntry(ctaOrigin, 10, 3),
        ]);

        Assert.IsTrue(map.TryMapOutputTime(Time(1), out TimelineMapPoint opening));
        Assert.AreEqual(openingId, opening.Origin.EditorialRecordId);
        Assert.IsNull(opening.SourceTime);
        Assert.IsTrue(map.TryMapOutputTime(Time(11), out TimelineMapPoint cta));
        Assert.AreEqual(ctaId, cta.Origin.EditorialRecordId);
        Assert.IsNull(cta.SourceTime);
        CollectionAssert.AreEqual(
            new[] { new TimeRange(default, Time(2)) },
            map.MapOriginToOutputRanges(opening.Origin).ToArray());
        CollectionAssert.AreEqual(
            new[] { new TimeRange(Time(10), Time(3)) },
            map.MapOriginToOutputRanges(cta.Origin).ToArray());
    }

    [TestMethod]
    public void NegativeSourceTimeIsPreservedExactly()
    {
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        TimelineMap map = CreateMap([
            new TimelineMapEntry(
                new TimeRange(default, Time(1)),
                OriginRef.FromSourceSpan(sourceSpanId),
                new TimeRange(Time(-1, 12), Time(1))),
        ]);

        Assert.IsTrue(map.TryMapOutputTime(default, out TimelineMapPoint mapped));
        Assert.AreEqual(Time(-1, 12), mapped.SourceTime);
        CollectionAssert.AreEqual(
            new[] { default(RationalTime) },
            map.MapSourceTimeToOutput(sourceSpanId, Time(-1, 12)).ToArray());
    }

    [TestMethod]
    public void ReusedSourceTimeMapsToEveryOutputOccurrence()
    {
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.New();
        TimelineMap map = CreateMap([
            SourceEntry(sourceSpanId, 0, 10, 2),
            SourceEntry(sourceSpanId, 5, 10, 2),
        ]);

        IReadOnlyList<RationalTime> outputs =
            map.MapSourceTimeToOutput(sourceSpanId, Time(11));

        CollectionAssert.AreEqual(new[] { Time(1), Time(6) }, outputs.ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<RationalTime>)outputs).Clear());
    }

    [TestMethod]
    public void ReusedOriginMapsToEveryOutputRangeWithoutExposingMutation()
    {
        OriginRef origin = OriginRef.FromAsset(EntityId<AssetIdTag>.New());
        TimelineMap map = CreateMap([
            OriginEntry(origin, 0, 2),
            OriginEntry(origin, 5, 3),
        ]);

        IReadOnlyList<TimeRange> ranges = map.MapOriginToOutputRanges(origin);

        CollectionAssert.AreEqual(
            new[]
            {
                new TimeRange(default, Time(2)),
                new TimeRange(Time(5), Time(3)),
            },
            ranges.ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<TimeRange>)ranges).Clear());
        Assert.AreEqual(
            0,
            map.MapOriginToOutputRanges(
                OriginRef.FromAsset(EntityId<AssetIdTag>.New())).Count);
    }

    [TestMethod]
    public void GapsAndEndBoundaryReturnFalseWithoutFabricatingOrigin()
    {
        TimelineMap map = CreateMap([
            OriginEntry(OriginRef.FromAsset(EntityId<AssetIdTag>.New()), 2, 3),
        ]);

        Assert.IsFalse(map.TryMapOutputTime(Time(1), out _));
        Assert.IsFalse(map.TryMapOutputTime(Time(5), out _));
        Assert.IsFalse(map.TryMapOutputTime(Time(-1), out _));
    }

    [TestMethod]
    public void ConstructorDefensivelyCopiesEntriesAndAllowsEmptyMap()
    {
        TimelineMapEntry original = OriginEntry(
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()),
            0,
            1);
        List<TimelineMapEntry> entries = [original];
        TimelineMap map = CreateMap(entries);
        entries.Clear();

        Assert.AreEqual(1, map.Entries.Count);
        Assert.AreEqual(original, map.Entries[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<TimelineMapEntry>)map.Entries).Clear());

        TimelineMap empty = CreateMap([]);
        Assert.AreEqual(0, empty.Entries.Count);
        Assert.IsFalse(empty.TryMapOutputTime(default, out _));
    }

    [TestMethod]
    public void ConstructorRejectsNullUnorderedAndOverlappingEntries()
    {
        TimelineMapEntry early = OriginEntry(
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()),
            0,
            3);
        TimelineMapEntry late = OriginEntry(
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()),
            2,
            3);

        AssertParamName<ArgumentException>("entries", () => CreateMap([null!]));
        AssertParamName<ArgumentException>("entries", () => CreateMap([late, early]));
        AssertParamName<ArgumentException>("entries", () => CreateMap([early, late]));
    }

    [TestMethod]
    public void EntryRequiresOriginSpecificSourceRangeAndEqualOneToOneDuration()
    {
        OriginRef source = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        OriginRef asset = OriginRef.FromAsset(EntityId<AssetIdTag>.New());
        TimeRange output = new(default, Time(2));

        AssertParamName<ArgumentNullException>(
            "sourceRange",
            () => new TimelineMapEntry(output, source, null));
        AssertParamName<ArgumentException>(
            "sourceRange",
            () => new TimelineMapEntry(
                output,
                source,
                new TimeRange(Time(10), Time(3))));
        AssertParamName<ArgumentException>(
            "sourceRange",
            () => new TimelineMapEntry(
                output,
                asset,
                new TimeRange(Time(10), Time(2))));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputRange",
            () => new TimelineMapEntry(
                new TimeRange(Time(-1), Time(1)),
                asset,
                null));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputRange",
            () => new TimelineMapEntry(
                new TimeRange(default, default),
                asset,
                null));
    }

    [TestMethod]
    public void TimelineMapPointRejectsInventedOrMissingSourceTime()
    {
        OriginRef source = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        OriginRef asset = OriginRef.FromAsset(EntityId<AssetIdTag>.New());

        Assert.ThrowsExactly<ArgumentNullException>(() => new TimelineMapPoint(source, null));
        AssertParamName<ArgumentException>(
            "sourceTime",
            () => new TimelineMapPoint(asset, default(RationalTime)));
    }

    private static TimelineMap CreateMap(IReadOnlyList<TimelineMapEntry> entries)
    {
        return new TimelineMap(
            EntityId<TimelineMapIdTag>.New(),
            EntityId<CompositionVersionIdTag>.New(),
            entries);
    }

    private static TimelineMapEntry SourceEntry(
        EntityId<SourceSpanIdTag> sourceSpanId,
        long outputStart,
        long sourceStart,
        long duration)
    {
        return new TimelineMapEntry(
            new TimeRange(Time(outputStart), Time(duration)),
            OriginRef.FromSourceSpan(sourceSpanId),
            new TimeRange(Time(sourceStart), Time(duration)));
    }

    private static TimelineMapEntry OriginEntry(
        OriginRef origin,
        long outputStart,
        long duration)
    {
        return new TimelineMapEntry(
            new TimeRange(Time(outputStart), Time(duration)),
            origin,
            null);
    }

    private static RationalTime Time(long value, long rate = 1) => new(value, rate);

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        System.Reflection.PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}