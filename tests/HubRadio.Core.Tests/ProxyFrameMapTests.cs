using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ProxyFrameMapTests
{
    private static readonly EntityId<AssetIdTag> SourceAssetId =
        EntityId<AssetIdTag>.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly ContentDigest SourceDigest =
        new(new string('a', 64));

    private static readonly ContentDigest SourceIndexDigest =
        new(new string('b', 64));

    private static readonly ContentDigest ProxyDigest =
        new(new string('c', 64));

    private static readonly ContentDigest ProxyIndexDigest =
        new(new string('d', 64));

    [TestMethod]
    public void Create_MapsObservedProxyPtsToLastSourcePtsAndVerifiesRoundTrip()
    {
        ProxyFrameMap map = CreateMap(
            sourceTimes:
            [
                Time(-1, 10),
                Time(-1, 20),
                Time(0),
                Time(1, 20),
                Time(1, 10),
                Time(3, 10),
            ],
            proxyTimes:
            [
                Time(0),
                Time(1, 30),
                Time(1, 15),
                Time(1, 10),
                Time(1, 5),
                Time(3, 10),
            ]);

        Assert.AreEqual(6, map.SourceFrameCount);
        Assert.AreEqual(Time(-1, 10), map.SourcePresentationStart);
        Assert.AreEqual(Time(3, 10), map.SourcePresentationEnd);
        Assert.AreEqual(6, map.Entries.Count);
        AssertEntry(map.Entries[0], 0, Time(0), 2, Time(0));
        AssertEntry(map.Entries[1], 1, Time(1, 30), 2, Time(0));
        AssertEntry(map.Entries[2], 2, Time(1, 15), 3, Time(1, 20));
        AssertEntry(map.Entries[3], 3, Time(1, 10), 4, Time(1, 10));
        AssertEntry(map.Entries[4], 4, Time(1, 5), 4, Time(1, 10));
        AssertEntry(map.Entries[5], 5, Time(3, 10), 5, Time(3, 10));

        Assert.AreEqual(4, map.RoundTrip.TestedSourceFrameCount);
        Assert.AreEqual(2, map.RoundTrip.UnrepresentableSourceFrameCount);
        Assert.AreEqual(Time(1, 20), map.RoundTrip.MaximumError);
    }

    [TestMethod]
    public void Lookup_UsesObservedFloorBoundariesWithoutNominalRateFallback()
    {
        ProxyFrameMap map = CreateMap(
            sourceTimes: [Time(-1, 10), Time(0), Time(1, 20), Time(1, 10)],
            proxyTimes: [Time(0), Time(1, 30), Time(1, 15), Time(1, 10)]);

        Assert.AreSame(map.Entries[2], map.MapProxyFrameToSource(2));
        Assert.AreEqual(Time(1, 20), map.MapProxyFrameToSource(2).SourcePresentationTime);
        Assert.IsFalse(map.TryMapSourceTimeToProxyFrame(Time(-1, 100), out _));

        Assert.IsTrue(map.TryMapSourceTimeToProxyFrame(Time(0), out ProxyFrameMapEntry? zero));
        Assert.AreEqual(0, zero.ProxyFrameSequence);
        Assert.IsTrue(map.TryMapSourceTimeToProxyFrame(
            Time(1, 20),
            out ProxyFrameMapEntry? between));
        Assert.AreEqual(1, between.ProxyFrameSequence);
        Assert.AreEqual(Time(1, 30), between.ProxyPresentationTime);
        Assert.IsTrue(map.TryMapSourceTimeToProxyFrame(
            Time(1, 10),
            out ProxyFrameMapEntry? exact));
        Assert.AreEqual(3, exact.ProxyFrameSequence);
        Assert.IsFalse(map.TryMapSourceTimeToProxyFrame(
            Time(100),
            out ProxyFrameMapEntry? afterEnd));
        Assert.IsNull(afterEnd);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            map.MapProxyFrameToSource(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            map.MapProxyFrameToSource(map.Entries.Count));
        Assert.IsFalse(typeof(ProxyFrameMap)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property => property.Name.Contains(
                "FrameRate",
                StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Create_ClampsOnlyProxyFramesBeforeFirstSourceFrame()
    {
        ProxyFrameMap map = CreateMap(
            sourceTimes: [Time(1), Time(2)],
            proxyTimes: [Time(0), Time(1), Time(2)]);

        AssertEntry(map.Entries[0], 0, Time(0), 0, Time(1));
        AssertEntry(map.Entries[1], 1, Time(1), 0, Time(1));
        AssertEntry(map.Entries[2], 2, Time(2), 1, Time(2));
        Assert.IsFalse(map.TryMapSourceTimeToProxyFrame(Time(1, 2), out _));
        Assert.IsTrue(map.TryMapSourceTimeToProxyFrame(
            Time(1),
            out ProxyFrameMapEntry? sourceStart));
        Assert.AreEqual(1, sourceStart.ProxyFrameSequence);
        Assert.AreEqual(Time(1), sourceStart.SourcePresentationTime);
        Assert.AreEqual(0, map.RoundTrip.UnrepresentableSourceFrameCount);
        Assert.AreEqual(default(RationalTime), map.RoundTrip.MaximumError);
    }

    [TestMethod]
    public void Create_OneFrameProxyPreservesUnrepresentablePrefixAndTailError()
    {
        ProxyFrameMap map = CreateMap(
            sourceTimes: [Time(-1), Time(0), Time(1)],
            proxyTimes: [Time(0)]);

        Assert.AreEqual(1, map.Entries.Count);
        AssertEntry(map.Entries[0], 0, Time(0), 1, Time(0));
        Assert.AreEqual(2, map.RoundTrip.TestedSourceFrameCount);
        Assert.AreEqual(1, map.RoundTrip.UnrepresentableSourceFrameCount);
        Assert.AreEqual(Time(1), map.RoundTrip.MaximumError);
        Assert.IsTrue(map.TryMapSourceTimeToProxyFrame(
            Time(1),
            out ProxyFrameMapEntry? tail));
        Assert.AreEqual(0, tail.ProxyFrameSequence);
        Assert.AreEqual(Time(0), tail.SourcePresentationTime);
    }

    [TestMethod]
    public void Create_DefensivelyCopiesInputsAndRejectsInvalidObservedTimelines()
    {
        var source = new List<RationalTime> { Time(-1), Time(0), Time(1) };
        var proxy = new List<RationalTime> { Time(0), Time(1) };
        ProxyFrameMap map = CreateMap(source, proxy);
        source.Clear();
        proxy.Clear();

        Assert.AreEqual(3, map.SourceFrameCount);
        Assert.AreEqual(2, map.Entries.Count);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ProxyFrameMapEntry>)map.Entries).Clear());

        AssertParamName<ArgumentException>(
            "sourcePresentationTimes",
            () => CreateMap([], [Time(0)]));
        AssertParamName<ArgumentException>(
            "proxyPresentationTimes",
            () => CreateMap([Time(0)], []));
        AssertParamName<ArgumentException>(
            "sourcePresentationTimes",
            () => CreateMap([Time(0), Time(0)], [Time(0)]));
        AssertParamName<ArgumentException>(
            "sourcePresentationTimes",
            () => CreateMap([Time(1), Time(0)], [Time(0), Time(1)]));
        AssertParamName<ArgumentException>(
            "proxyPresentationTimes",
            () => CreateMap([Time(0), Time(1)], [Time(0), Time(0)]));
        AssertParamName<ArgumentException>(
            "proxyPresentationTimes",
            () => CreateMap([Time(0), Time(1)], [Time(3), Time(4)]));
        AssertParamName<ArgumentException>(
            "proxyPresentationTimes",
            () => CreateMap([Time(3), Time(4)], [Time(0), Time(1)]));
    }

    [TestMethod]
    public void Create_RejectsUnrepresentableRoundTripArithmeticOverflow()
    {
        Assert.ThrowsExactly<OverflowException>(() => CreateMap(
            sourceTimes: [Time(long.MinValue), Time(long.MaxValue)],
            proxyTimes: [Time(long.MinValue)]));
    }

    [TestMethod]
    public void CanonicalJson_IsDeterministicExactAndPathPrivate()
    {
        ProxyFrameMap first = CreateMap(
            sourceTimes: [Time(-1, 20), Time(0), Time(1, 20)],
            proxyTimes: [Time(0), Time(1, 30), Time(1, 15)]);
        ProxyFrameMap second = CreateMap(
            sourceTimes: [Time(-1, 20), Time(0), Time(1, 20)],
            proxyTimes: [Time(0), Time(1, 30), Time(1, 15)]);

        Assert.AreEqual(first.CanonicalJson, second.CanonicalJson);
        Assert.AreEqual(
            first.CanonicalJson,
            CanonicalJson.Canonicalize(first.CanonicalJson));
        Assert.IsFalse(first.CanonicalJson.Contains(
            @"C:\private\source.mov",
            StringComparison.OrdinalIgnoreCase));

        using JsonDocument document = JsonDocument.Parse(first.CanonicalJson);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "sourceAssetId",
                "projectId",
                "sourceContentDigest",
                "sourcePtsIndexDigest",
                "proxyContentDigest",
                "proxyPtsIndexDigest",
                "sourceFrameCount",
                "sourcePresentationStart",
                "sourcePresentationEnd",
                "roundTrip",
                "entries",
            },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(ProxyFrameMap.CurrentSchemaVersion, root
            .GetProperty("schemaVersion")
            .GetString());
        Assert.AreEqual(SourceAssetId.ToString(), root
            .GetProperty("sourceAssetId")
            .GetString());
        Assert.AreEqual(ProjectId.ToString(), root.GetProperty("projectId").GetString());
        Assert.AreEqual(SourceDigest.Hex, root
            .GetProperty("sourceContentDigest")
            .GetString());
        Assert.AreEqual(3, ReadExactInt32(root.GetProperty("sourceFrameCount")));
        AssertRational(
            root.GetProperty("sourcePresentationStart"),
            first.SourcePresentationStart);
        AssertRational(
            root.GetProperty("sourcePresentationEnd"),
            first.SourcePresentationEnd);
        Assert.AreEqual(3, root.GetProperty("entries").GetArrayLength());

        JsonElement roundTrip = root.GetProperty("roundTrip");
        Assert.AreEqual(
            first.RoundTrip.TestedSourceFrameCount,
            ReadExactInt32(roundTrip.GetProperty("testedSourceFrameCount")));
        AssertRational(
            roundTrip.GetProperty("maximumError"),
            first.RoundTrip.MaximumError);
    }

    [TestMethod]
    public void PublicSurface_IsSealedImmutableAndUsesTypedIdentity()
    {
        foreach (Type type in new[]
        {
            typeof(ProxyFrameMapEntry),
            typeof(ProxyFrameMapRoundTripEvidence),
            typeof(ProxyFrameMap),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null),
                type.Name);
        }

        Assert.AreEqual(0, typeof(ProxyFrameMapEntry).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ProxyFrameMapRoundTripEvidence).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ProxyFrameMap).GetConstructors().Length);
        Assert.AreEqual(
            typeof(EntityId<AssetIdTag>),
            typeof(ProxyFrameMap).GetProperty("SourceAssetId")!.PropertyType);
        Assert.AreEqual(
            typeof(EntityId<ProjectIdTag>),
            typeof(ProxyFrameMap).GetProperty("ProjectId")!.PropertyType);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Create",
                "MapProxyFrameToSource",
                "TryMapSourceTimeToProxyFrame",
            },
            typeof(ProxyFrameMap)
                .GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
    }

    [TestMethod]
    public void Stage0ObservedPts_WhenAvailableMatchesRecordedMappingEvidence()
    {
        string? sourcePath = TryResolveRepositoryPath(
            "benchmarks/work/source-video-packet-pts.csv");
        string? proxyPath = TryResolveRepositoryPath(
            "benchmarks/work/proxy-video-packet-pts.csv");
        if (sourcePath is null || proxyPath is null)
        {
            Assert.Inconclusive(
                "The ignored Stage 0 observed-PTS work files are unavailable.");
            return;
        }

        RationalTime[] sourceTimes = ReadObservedTimes(
            sourcePath,
            new RationalTime(1, 600));
        RationalTime[] proxyTimes = ReadObservedTimes(
            proxyPath,
            new RationalTime(1, 30000));

        ProxyFrameMap map = CreateMap(sourceTimes, proxyTimes);

        Assert.AreEqual(62316, map.SourceFrameCount);
        Assert.AreEqual(32409, map.Entries.Count);
        Assert.AreEqual(62311, map.RoundTrip.TestedSourceFrameCount);
        Assert.AreEqual(5, map.RoundTrip.UnrepresentableSourceFrameCount);
        Assert.AreEqual(new RationalTime(127, 200), map.RoundTrip.MaximumError);
        Assert.AreEqual(5, map.Entries[0].SourceFrameSequence);
        Assert.AreEqual(62313, map.Entries[^1].SourceFrameSequence);
    }

    private static ProxyFrameMap CreateMap(
        IReadOnlyList<RationalTime> sourceTimes,
        IReadOnlyList<RationalTime> proxyTimes) =>
        ProxyFrameMap.Create(
            SourceAssetId,
            ProjectId,
            SourceDigest,
            SourceIndexDigest,
            ProxyDigest,
            ProxyIndexDigest,
            sourceTimes,
            proxyTimes);

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static RationalTime[] ReadObservedTimes(
        string path,
        RationalTime timeBase) =>
        File.ReadLines(path)
            .Select(static line => long.Parse(
                line,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture))
            .Order()
            .Select(ticks => new RationalTime(
                checked(ticks * timeBase.Value),
                timeBase.Rate))
            .ToArray();

    private static void AssertEntry(
        ProxyFrameMapEntry entry,
        int proxySequence,
        RationalTime proxyTime,
        int sourceSequence,
        RationalTime sourceTime)
    {
        Assert.AreEqual(proxySequence, entry.ProxyFrameSequence);
        Assert.AreEqual(proxyTime, entry.ProxyPresentationTime);
        Assert.AreEqual(sourceSequence, entry.SourceFrameSequence);
        Assert.AreEqual(sourceTime, entry.SourcePresentationTime);
    }

    private static void AssertRational(JsonElement value, RationalTime expected)
    {
        CollectionAssert.AreEquivalent(
            new[] { "value", "rate" },
            value.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(expected.Value, ReadExactInt64(value.GetProperty("value")));
        Assert.AreEqual(expected.Rate, ReadExactInt64(value.GetProperty("rate")));
    }

    private static int ReadExactInt32(JsonElement value)
    {
        long parsed = ReadExactInt64(value);
        return checked((int)parsed);
    }

    private static long ReadExactInt64(JsonElement value)
    {
        decimal parsed = value.GetDecimal();
        if (parsed != decimal.Truncate(parsed) ||
            parsed < long.MinValue ||
            parsed > long.MaxValue)
        {
            throw new InvalidOperationException("Expected an exact Int64 value.");
        }

        return decimal.ToInt64(parsed);
    }

    private static void AssertParamName<TException>(
        string expected,
        Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }

    private static string? TryResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? path : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}