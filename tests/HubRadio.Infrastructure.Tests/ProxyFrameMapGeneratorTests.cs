using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ProxyFrameMapGeneratorTests
{
    private static readonly EntityId<AssetIdTag> SourceAssetId =
        EntityId<AssetIdTag>.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly EntityId<JobIdTag> SourceJobId =
        EntityId<JobIdTag>.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly EntityId<JobIdTag> ProxyJobId =
        EntityId<JobIdTag>.Parse("44444444-4444-4444-8444-444444444444");

    private static readonly EntityId<ArtifactIdTag> OriginalArtifactId =
        EntityId<ArtifactIdTag>.Parse("55555555-5555-4555-8555-555555555555");

    private static readonly EntityId<ArtifactIdTag> ProxyArtifactId =
        EntityId<ArtifactIdTag>.Parse("66666666-6666-4666-8666-666666666666");

    private static readonly ContentDigest SourceDigest =
        new(new string('a', 64));

    private static readonly ContentDigest ProxyDigest =
        new(new string('b', 64));

    [TestMethod]
    public void Generate_BindsCurrentIndicesAndBuildsExactObservedMap()
    {
        PtsIndex source = CreateSourceIndex();
        PtsIndex proxy = CreateProxyIndex();
        GeneratedProxy generated = CreateGeneratedProxy(proxy);

        ProxyFrameMap map = new ProxyFrameMapGenerator().Generate(
            source,
            generated);

        Assert.AreEqual(SourceAssetId, map.SourceAssetId);
        Assert.AreEqual(ProjectId, map.ProjectId);
        Assert.AreEqual(SourceDigest, map.SourceContentDigest);
        Assert.AreEqual(ProxyDigest, map.ProxyContentDigest);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(source.CanonicalJson)),
            map.SourcePtsIndexDigest);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(proxy.CanonicalJson)),
            map.ProxyPtsIndexDigest);
        Assert.AreEqual(source.Entries.Count, map.SourceFrameCount);
        Assert.AreEqual(proxy.Entries.Count, map.Entries.Count);
        Assert.AreEqual(2, map.Entries[0].SourceFrameSequence);
        Assert.AreEqual(Time(0), map.Entries[0].SourcePresentationTime);
        Assert.AreEqual(3, map.Entries[2].SourceFrameSequence);
        Assert.AreEqual(Time(1, 20), map.Entries[2].SourcePresentationTime);
        Assert.AreEqual(2, map.RoundTrip.UnrepresentableSourceFrameCount);
        Assert.AreEqual(Time(1, 20), map.RoundTrip.MaximumError);
    }

    [TestMethod]
    public void Generate_RejectsSourceIdentityProjectAndDigestMismatches()
    {
        GeneratedProxy generated = CreateGeneratedProxy(CreateProxyIndex());
        PtsIndex[] mismatches =
        [
            CreateSourceIndex(candidateId: EntityId<AssetIdTag>.New()),
            CreateSourceIndex(projectId: EntityId<ProjectIdTag>.New()),
            CreateSourceIndex(contentDigest: new ContentDigest(new string('c', 64))),
        ];

        foreach (PtsIndex mismatch in mismatches)
        {
            ProxyFrameMapException exception =
                Assert.ThrowsExactly<ProxyFrameMapException>(() =>
                    new ProxyFrameMapGenerator().Generate(mismatch, generated));
            AssertSafeFailure(
                exception,
                ProxyFrameMapFailureReason.InputBindingMismatch);
        }
    }

    [TestMethod]
    public void Generate_RejectsNonoverlappingObservedTimelinesWithoutThresholdGuess()
    {
        PtsIndex source = CreateSourceIndex(
            times: [Time(10), Time(11)]);
        PtsIndex proxy = CreateProxyIndex(
            times: [Time(0), Time(1)]);
        GeneratedProxy generated = CreateGeneratedProxy(proxy);

        ProxyFrameMapException exception =
            Assert.ThrowsExactly<ProxyFrameMapException>(() =>
                new ProxyFrameMapGenerator().Generate(source, generated));

        AssertSafeFailure(
            exception,
            ProxyFrameMapFailureReason.TimingEvidenceInvalid);
        Assert.IsFalse(typeof(ProxyFrameMapGenerator)
            .GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)
            .Any(static field =>
                field.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Contains("FrameRate", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ValidateInputBinding_RechecksGeneratedProxyInsteadOfTrustingCallerPairing()
    {
        PtsIndex source = CreateSourceIndex();
        GeneratedProxy generated = CreateGeneratedProxy(CreateProxyIndex());

        ProxyFrameMapGenerator.ValidateInputBinding(source, generated);

        PtsIndex wrongJobButSameSource = CreateSourceIndex(
            jobId: EntityId<JobIdTag>.New());
        ProxyFrameMapGenerator.ValidateInputBinding(wrongJobButSameSource, generated);
        Assert.AreNotEqual(
            wrongJobButSameSource.JobId,
            generated.ProxyPtsIndex.JobId);
    }

    [TestMethod]
    public void PublicSurface_IsMinimalImmutableAndErrorsAreStablePathPrivate()
    {
        foreach (Type type in new[]
        {
            typeof(ProxyFrameMapException),
            typeof(ProxyFrameMapGenerator),
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

        CollectionAssert.AreEqual(
            new[] { "Generate" },
            typeof(ProxyFrameMapGenerator)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.AreEqual(0, typeof(ProxyFrameMapException).GetConstructors().Length);
        CollectionAssert.AreEqual(
            new[]
            {
                "InputBindingMismatch",
                "TimingEvidenceInvalid",
                "ArithmeticOverflow",
            },
            Enum.GetNames<ProxyFrameMapFailureReason>());

        const string privatePath = @"C:\private\customer-source.mov";
        foreach (ProxyFrameMapFailureReason reason in
            Enum.GetValues<ProxyFrameMapFailureReason>())
        {
            var exception = new ProxyFrameMapException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.ToString().Contains(
                privatePath,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    private static PtsIndex CreateSourceIndex(
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        ContentDigest? contentDigest = null,
        IReadOnlyList<RationalTime>? times = null) =>
        CreateIndex(
            candidateId ?? SourceAssetId,
            projectId ?? ProjectId,
            jobId ?? SourceJobId,
            "source.mov",
            contentDigest ?? SourceDigest,
            byteSize: 100,
            times ??
            [
                Time(-1, 10),
                Time(-1, 20),
                Time(0),
                Time(1, 20),
                Time(1, 10),
                Time(3, 10),
            ]);

    private static PtsIndex CreateProxyIndex(
        IReadOnlyList<RationalTime>? times = null) =>
        CreateIndex(
            SourceAssetId,
            ProjectId,
            ProxyJobId,
            "proxy.mp4",
            ProxyDigest,
            byteSize: 200,
            times ??
            [
                Time(0),
                Time(1, 30),
                Time(1, 15),
                Time(1, 10),
                Time(1, 5),
                Time(3, 10),
            ]);

    private static PtsIndex CreateIndex(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        ContentDigest contentDigest,
        long byteSize,
        IReadOnlyList<RationalTime> times)
    {
        RationalTime duration = Time(1, 300);
        PtsIndexEntry[] entries = times
            .Select((time, index) => new PtsIndexEntry(
                index,
                time,
                duration,
                keyFrame: index == 0))
            .ToArray();
        return new PtsIndex(
            candidateId,
            projectId,
            jobId,
            sourceFileName,
            contentDigest,
            byteSize,
            streamIndex: 0,
            duration,
            entries);
    }

    private static GeneratedProxy CreateGeneratedProxy(PtsIndex proxyIndex)
    {
        var proxyArtifact = new StoredArtifact(
            ProxyArtifactId,
            ProjectId,
            ArtifactKind.Proxy,
            $"projects/{ProjectId}/proxy/{SourceAssetId}/{ProxyArtifactId}.mp4",
            ProxyDigest,
            byteSize: 200,
            new DateTimeOffset(2036, 7, 8, 9, 10, 11, TimeSpan.Zero));
        var probe = new MediaProbeResult(
            SourceAssetId,
            ProjectId,
            ProxyJobId,
            "proxy.mp4",
            ProxyDigest,
            byteSize: 200,
            ContainerSignature.IsoBaseMediaFtyp,
            ["mov", "mp4"],
            "QuickTime / MOV",
            durationSeconds: null,
            declaredByteSize: 200,
            bitRate: null,
            probeScore: null,
            streams:
            [
                new ProbedMediaStream(
                    0,
                    MediaStreamKind.Video,
                    "h264",
                    "High",
                    640,
                    360,
                    "yuv420p",
                    new RationalNumber(30000, 1001),
                    new RationalNumber(30000, 1001),
                    new RationalNumber(1, 300),
                    startSeconds: null,
                    durationSeconds: null,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
            ],
            warnings: ["audio-track-absent"]);
        return new GeneratedProxy(
            SourceAssetId,
            SourceDigest,
            OriginalArtifactId,
            proxyArtifact,
            probe,
            proxyIndex,
            new ProxyGenerationProfile(),
            TimeSpan.Zero);
    }

    private static RationalTime Time(long value, long rate = 1) =>
        new(value, rate);

    private static void AssertSafeFailure(
        ProxyFrameMapException exception,
        ProxyFrameMapFailureReason reason)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\private\customer-source.mov",
            StringComparison.OrdinalIgnoreCase));
    }
}