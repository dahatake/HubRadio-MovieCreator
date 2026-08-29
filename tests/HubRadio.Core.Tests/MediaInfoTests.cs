using System.Reflection;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class MediaInfoTests
{
    private static readonly EntityId<AssetIdTag> CandidateId =
        EntityId<AssetIdTag>.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly EntityId<JobIdTag> JobId =
        EntityId<JobIdTag>.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly ContentDigest Digest = new(new string('a', 64));

    [TestMethod]
    public void ConstructorCopiesCollectionsSelectsLowestVideoAndSortsWarnings()
    {
        var formatNames = new List<string> { "future-container" };
        var streams = new List<MediaStreamInfo>
        {
            Video(index: 8, duration: null),
            Video(index: 2, duration: null),
        };
        var warnings = new List<MediaInfoWarning>
        {
            new(MediaInfoWarningCode.UnrecognizedContainerSignature),
            new(
                MediaInfoWarningCode.MultipleVideoStreams,
                new MediaStreamCountWarningEvidence(2)),
            new(MediaInfoWarningCode.AudioTrackAbsent),
        };

        MediaInfo result = CreateInfo(
            containerClassification: MediaContainerClassification.Unknown,
            formatNames: formatNames,
            formatLongName: "Future Container",
            formatDuration: null,
            streams: streams,
            warnings: warnings);
        formatNames.Clear();
        streams.Clear();
        warnings.Clear();

        Assert.AreEqual("future-container", result.FormatNames.Single());
        Assert.HasCount(2, result.Streams);
        Assert.AreEqual(2, result.PrimaryVideoStream.Index);
        CollectionAssert.AreEqual(
            new[]
            {
                MediaInfoWarningCode.AudioTrackAbsent,
                MediaInfoWarningCode.MultipleVideoStreams,
                MediaInfoWarningCode.UnrecognizedContainerSignature,
            },
            result.Warnings.Select(static warning => warning.Code).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)result.FormatNames).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<MediaStreamInfo>)result.Streams).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<MediaInfoWarning>)result.Warnings).Clear());
    }

    [TestMethod]
    public void ConstructorRejectsInvalidIdentityFileSizeContainerFormatAndStreams()
    {
        AssertParamName<ArgumentNullException>(
            "candidateId",
            () => new MediaInfo(
                null!,
                ProjectId,
                JobId,
                "source.mov",
                Digest,
                4096,
                MediaContainerClassification.IsoBaseMediaFtyp,
                ["mov"],
                null,
                null,
                [Video()],
                null,
                null,
                [new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent)]));
        AssertParamName<ArgumentNullException>(
            "projectId",
            () => new MediaInfo(
                CandidateId,
                null!,
                JobId,
                "source.mov",
                Digest,
                4096,
                MediaContainerClassification.IsoBaseMediaFtyp,
                ["mov"],
                null,
                null,
                [Video()],
                null,
                null,
                [new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent)]));
        AssertParamName<ArgumentNullException>(
            "jobId",
            () => new MediaInfo(
                CandidateId,
                ProjectId,
                null!,
                "source.mov",
                Digest,
                4096,
                MediaContainerClassification.IsoBaseMediaFtyp,
                ["mov"],
                null,
                null,
                [Video()],
                null,
                null,
                [new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent)]));
        AssertParamName<ArgumentNullException>(
            "contentDigest",
            () => new MediaInfo(
                CandidateId,
                ProjectId,
                JobId,
                "source.mov",
                null!,
                4096,
                MediaContainerClassification.IsoBaseMediaFtyp,
                ["mov"],
                null,
                null,
                [Video()],
                null,
                null,
                [new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent)]));
        AssertParamName<ArgumentException>(
            "sourceFileName",
            () => CreateInfo(sourceFileName: @"C:\private\source.mov"));
        AssertParamName<ArgumentException>(
            "sourceFileName",
            () => CreateInfo(sourceFileName: "source\n.mov"));
        AssertParamName<ArgumentOutOfRangeException>(
            "byteSize",
            () => CreateInfo(byteSize: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "containerClassification",
            () => CreateInfo(
                containerClassification: (MediaContainerClassification)int.MaxValue));
        AssertParamName<ArgumentException>(
            "formatNames",
            () => CreateInfo(formatNames: []));
        AssertParamName<ArgumentException>(
            "formatNames",
            () => CreateInfo(formatNames: [" mov"]));
        AssertParamName<ArgumentOutOfRangeException>(
            "formatDuration",
            () => CreateInfo(formatDuration: new RationalTime(-1, 1)));
        AssertParamName<ArgumentException>(
            "streams",
            () => CreateInfo(streams: [Audio()]));
        AssertParamName<ArgumentException>(
            "streams",
            () => CreateInfo(streams: [Video(index: 0), Audio(index: 0)]));
        AssertParamName<ArgumentException>(
            "streams",
            () => CreateInfo(streams: [Video(), null!]));
    }

    [TestMethod]
    public void StreamInfoPreservesUnknownCodecAndNegativeStartButRejectsInvalidMetadata()
    {
        MediaStreamInfo valid = Video(
            codecName: "future-codec",
            start: new RationalTime(-83333, 1000000),
            duration: new RationalTime(1081401667, 1000000));

        Assert.AreEqual("future-codec", valid.CodecName);
        Assert.AreEqual(new RationalTime(-83333, 1000000), valid.Start);
        Assert.AreEqual(new RationalTime(1081401667, 1000000), valid.Duration);

        AssertParamName<ArgumentOutOfRangeException>(
            "index",
            () => Video(index: -1));
        AssertParamName<ArgumentOutOfRangeException>(
            "kind",
            () => new MediaStreamInfo(
                0,
                (MediaStreamType)int.MaxValue,
                "codec",
                null,
                null,
                null,
                null,
                null,
                null,
                new RationalTime(1, 1000),
                null,
                null,
                null,
                null,
                null));
        AssertParamName<ArgumentException>(
            "codecName",
            () => Video(codecName: " codec"));
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => Video(width: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "height",
            () => Video(height: 0));
        AssertParamName<ArgumentException>(
            "nominalFrameRate",
            () => Video(includeNominalFrameRate: false));
        AssertParamName<ArgumentOutOfRangeException>(
            "timeBase",
            () => Video(usePositiveTimeBase: false));
        AssertParamName<ArgumentOutOfRangeException>(
            "duration",
            () => Video(duration: new RationalTime(-1, 1)));
        AssertParamName<ArgumentOutOfRangeException>(
            "sampleRate",
            () => Audio(sampleRate: 0));
        AssertParamName<ArgumentException>(
            "sampleRate",
            () => new MediaStreamInfo(
                0,
                MediaStreamType.Other,
                "data",
                null,
                null,
                null,
                null,
                null,
                null,
                new RationalTime(1, 1000),
                null,
                null,
                1,
                null,
                null));
    }

    [TestMethod]
    public void WarningAndEvidenceConstructorsRejectWrongShapesAndFabricatedValues()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "code",
            () => new MediaInfoWarning((MediaInfoWarningCode)int.MaxValue));
        AssertParamName<ArgumentException>(
            "evidence",
            () => new MediaInfoWarning(
                MediaInfoWarningCode.AudioTrackAbsent,
                new MediaStreamCountWarningEvidence(2)));
        AssertParamName<ArgumentException>(
            "evidence",
            () => new MediaInfoWarning(MediaInfoWarningCode.LowResolution));
        AssertParamName<ArgumentOutOfRangeException>(
            "observedCount",
            () => new MediaStreamCountWarningEvidence(1));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new LowResolutionWarningEvidence(1280, 720, 1280, 720));
        AssertParamName<ArgumentException>(
            "averageFrameRate",
            () => new MediaFrameRateDifference(
                0,
                new RationalNumber(30, 1),
                new RationalNumber(30, 1)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new VariableRateFieldsDifferWarningEvidence(false, []));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new VariableRateFieldsDifferWarningEvidence(true, []));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new VariableRateFieldsDifferWarningEvidence(
                false,
                [
                    new MediaFrameRateDifference(
                        0,
                        new RationalNumber(30, 1),
                        new RationalNumber(24000, 1001)),
                ]));
        AssertParamName<ArgumentException>(
            "streamIndex",
            () => new MediaDurationFieldEvidence(
                MediaDurationFieldKind.FormatDuration,
                0,
                new RationalTime(1, 1)));
        AssertParamName<ArgumentOutOfRangeException>(
            "streamIndex",
            () => new MediaDurationFieldEvidence(
                MediaDurationFieldKind.StreamDuration,
                null,
                new RationalTime(1, 1)));
        AssertParamName<ArgumentOutOfRangeException>(
            "value",
            () => new MediaDurationFieldEvidence(
                MediaDurationFieldKind.StreamDuration,
                0,
                new RationalTime(-1, 1)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DurationFieldsDifferWarningEvidence(
            [
                new MediaDurationFieldEvidence(
                    MediaDurationFieldKind.FormatDuration,
                    null,
                    new RationalTime(1, 1)),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DurationFieldsDifferWarningEvidence(
            [
                new MediaDurationFieldEvidence(
                    MediaDurationFieldKind.FormatDuration,
                    null,
                    new RationalTime(1, 1)),
                new MediaDurationFieldEvidence(
                    MediaDurationFieldKind.StreamDuration,
                    0,
                    new RationalTime(1, 1)),
            ]));
    }

    [TestMethod]
    public void ConstructorRejectsMissingDuplicateAndFabricatedFlagWarnings()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            warnings:
            [
                new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent),
                new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            warnings: [new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent)]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            streams: [Video(duration: null)],
            formatDuration: null,
            warnings: []));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            containerClassification: MediaContainerClassification.Unknown,
            formatNames: ["future-container"],
            warnings: []));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            containerClassification: MediaContainerClassification.Ebml,
            formatNames: ["mov"],
            warnings: []));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            warnings: [new MediaInfoWarning(MediaInfoWarningCode.SignatureFormatMismatch)]));
    }

    [TestMethod]
    public void ConstructorRequiresExactCountResolutionAndRateEvidence()
    {
        IReadOnlyList<MediaStreamInfo> twoVideosNoAudio =
        [
            Video(index: 0, duration: null),
            Video(index: 1, duration: null),
        ];
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            formatDuration: null,
            streams: twoVideosNoAudio,
            warnings:
            [
                new MediaInfoWarning(MediaInfoWarningCode.AudioTrackAbsent),
                new MediaInfoWarning(
                    MediaInfoWarningCode.MultipleVideoStreams,
                    new MediaStreamCountWarningEvidence(3)),
            ]));

        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            streams: [Video(width: 1280, height: 720), Audio()],
            requiredMinimumVideoWidth: 1920,
            requiredMinimumVideoHeight: 1080,
            warnings:
            [
                new MediaInfoWarning(
                    MediaInfoWarningCode.LowResolution,
                    new LowResolutionWarningEvidence(640, 360, 1920, 1080)),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            streams: [Video(width: 1280, height: 720), Audio()],
            requiredMinimumVideoWidth: 1920,
            requiredMinimumVideoHeight: 1080,
            warnings: []));

        MediaStreamInfo unequalRateVideo = Video(
            nominalFrameRate: new RationalNumber(30, 1),
            averageFrameRate: new RationalNumber(24000, 1001));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            streams: [unequalRateVideo, Audio()],
            warnings:
            [
                new MediaInfoWarning(
                    MediaInfoWarningCode.VariableRateFieldsDiffer,
                    new VariableRateFieldsDifferWarningEvidence(
                        false,
                        [
                            new MediaFrameRateDifference(
                                7,
                                new RationalNumber(30, 1),
                                new RationalNumber(24000, 1001)),
                        ])),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            streams: [unequalRateVideo, Audio()],
            warnings: []));
    }

    [TestMethod]
    public void ConstructorRetainsRequirementsAndAcceptsOnlyMatchingLowResolutionEvidence()
    {
        MediaInfo result = CreateInfo(
            streams: [Video(width: 1280, height: 720), Audio()],
            requiredMinimumVideoWidth: 1920,
            requiredMinimumVideoHeight: 1080,
            warnings:
            [
                new MediaInfoWarning(
                    MediaInfoWarningCode.LowResolution,
                    new LowResolutionWarningEvidence(1280, 720, 1920, 1080)),
            ]);

        Assert.AreEqual(1920, result.RequiredMinimumVideoWidth);
        Assert.AreEqual(1080, result.RequiredMinimumVideoHeight);
        Assert.AreEqual(MediaInfoWarningCode.LowResolution, result.Warnings.Single().Code);
        AssertParamName<ArgumentException>(
            "requiredMinimumVideoWidth",
            () => CreateInfo(
                requiredMinimumVideoWidth: 1280,
                requiredMinimumVideoHeight: null));
        AssertParamName<ArgumentOutOfRangeException>(
            "requiredMinimumVideoWidth",
            () => CreateInfo(
                requiredMinimumVideoWidth: 0,
                requiredMinimumVideoHeight: 720));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            requiredMinimumVideoWidth: 640,
            requiredMinimumVideoHeight: 360,
            warnings:
            [
                new MediaInfoWarning(
                    MediaInfoWarningCode.LowResolution,
                    new LowResolutionWarningEvidence(1280, 720, 1920, 1080)),
            ]));
    }

    [TestMethod]
    public void ConstructorValidatesDurationEvidenceAgainstExactEndOffsets()
    {
        var evidenceInput = new List<MediaDurationFieldEvidence>
        {
            new(
                MediaDurationFieldKind.StreamDuration,
                1,
                new RationalTime(10, 1)),
            new(
                MediaDurationFieldKind.StreamEndOffset,
                0,
                new RationalTime(9, 1)),
            new(
                MediaDurationFieldKind.FormatDuration,
                null,
                new RationalTime(10, 1)),
        };
        var evidence = new DurationFieldsDifferWarningEvidence(evidenceInput);
        evidenceInput.Clear();

        MediaInfo result = CreateInfo(
            formatDuration: new RationalTime(10, 1),
            streams:
            [
                Video(start: new RationalTime(-1, 1), duration: new RationalTime(10, 1)),
                Audio(start: null, duration: new RationalTime(10, 1)),
            ],
            warnings:
            [
                new MediaInfoWarning(
                    MediaInfoWarningCode.DurationFieldsDiffer,
                    evidence),
            ]);

        Assert.HasCount(3, evidence.Fields);
        Assert.AreEqual(MediaDurationFieldKind.FormatDuration, evidence.Fields[0].Kind);
        Assert.AreEqual(
            MediaInfoWarningCode.DurationFieldsDiffer,
            result.Warnings.Single().Code);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<MediaDurationFieldEvidence>)evidence.Fields).Clear());

        var wrongEvidence = new DurationFieldsDifferWarningEvidence(
        [
            new MediaDurationFieldEvidence(
                MediaDurationFieldKind.FormatDuration,
                null,
                new RationalTime(10, 1)),
            new MediaDurationFieldEvidence(
                MediaDurationFieldKind.StreamEndOffset,
                0,
                new RationalTime(8, 1)),
            new MediaDurationFieldEvidence(
                MediaDurationFieldKind.StreamDuration,
                1,
                new RationalTime(10, 1)),
        ]);
        Assert.ThrowsExactly<ArgumentException>(() => CreateInfo(
            formatDuration: new RationalTime(10, 1),
            streams:
            [
                Video(start: new RationalTime(-1, 1), duration: new RationalTime(10, 1)),
                Audio(start: null, duration: new RationalTime(10, 1)),
            ],
            warnings:
            [
                new MediaInfoWarning(
                    MediaInfoWarningCode.DurationFieldsDiffer,
                    wrongEvidence),
            ]));
    }

    [TestMethod]
    public void ConstructorRejectsProbeReportedVariableRateWithoutObservedDifference()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new VariableRateFieldsDifferWarningEvidence(
                probeReportedFieldsDiffer: true,
                differences: []));
    }

    [TestMethod]
    public void PublicMediaContractsAreSealedImmutableAndContainNoPersistenceApi()
    {
        Type[] sealedTypes =
        [
            typeof(MediaInfo),
            typeof(MediaStreamInfo),
            typeof(MediaInfoWarning),
            typeof(MediaStreamCountWarningEvidence),
            typeof(LowResolutionWarningEvidence),
            typeof(MediaFrameRateDifference),
            typeof(VariableRateFieldsDifferWarningEvidence),
            typeof(MediaDurationFieldEvidence),
            typeof(DurationFieldsDifferWarningEvidence),
        ];
        foreach (Type type in sealedTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .All(static property => property.SetMethod is null), type.Name);
            Assert.IsFalse(type
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly)
                .Any(static method =>
                    method.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.Contains("Migrate", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.Contains("Rpc", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.Contains("Bridge", StringComparison.OrdinalIgnoreCase)),
                type.Name);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                "AudioTrackAbsent",
                "DurationFieldsDiffer",
                "LowResolution",
                "MultipleAudioStreams",
                "MultipleVideoStreams",
                "SignatureFormatMismatch",
                "UnrecognizedContainerSignature",
                "VariableRateFieldsDiffer",
            },
            Enum.GetNames<MediaInfoWarningCode>());
    }

    private static MediaInfo CreateInfo(
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string sourceFileName = "source.mov",
        ContentDigest? contentDigest = null,
        long byteSize = 4096,
        MediaContainerClassification containerClassification =
            MediaContainerClassification.IsoBaseMediaFtyp,
        IReadOnlyList<string>? formatNames = null,
        string? formatLongName = "QuickTime / MOV",
        RationalTime? formatDuration = null,
        IReadOnlyList<MediaStreamInfo>? streams = null,
        int? requiredMinimumVideoWidth = null,
        int? requiredMinimumVideoHeight = null,
        IReadOnlyList<MediaInfoWarning>? warnings = null)
    {
        return new MediaInfo(
            candidateId ?? CandidateId,
            projectId ?? ProjectId,
            jobId ?? JobId,
            sourceFileName,
            contentDigest ?? Digest,
            byteSize,
            containerClassification,
            formatNames ?? ["mov", "mp4"],
            formatLongName,
            formatDuration,
            streams ?? [Video(), Audio()],
            requiredMinimumVideoWidth,
            requiredMinimumVideoHeight,
            warnings ?? []);
    }

    private static MediaStreamInfo Video(
        int index = 0,
        string codecName = "h264",
        int? width = 1920,
        int? height = 1080,
        RationalNumber? nominalFrameRate = default,
        RationalNumber? averageFrameRate = default,
        RationalTime? timeBase = null,
        RationalTime? start = null,
        RationalTime? duration = null,
        bool includeNominalFrameRate = true,
        bool usePositiveTimeBase = true) =>
        new(
            index,
            MediaStreamType.Video,
            codecName,
            profile: null,
            width,
            height,
            pixelFormat: "yuv420p",
            includeNominalFrameRate
                ? nominalFrameRate ?? new RationalNumber(30, 1)
                : null,
            averageFrameRate ?? new RationalNumber(30, 1),
            usePositiveTimeBase
                ? timeBase ?? new RationalTime(1, 1000)
                : default,
            start,
            duration,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static MediaStreamInfo Audio(
        int index = 1,
        RationalTime? start = null,
        RationalTime? duration = null,
        int? sampleRate = 48000) =>
        new(
            index,
            MediaStreamType.Audio,
            "aac",
            profile: null,
            width: null,
            height: null,
            pixelFormat: null,
            nominalFrameRate: null,
            averageFrameRate: null,
            new RationalTime(1, 48000),
            start,
            duration,
            sampleRate,
            channels: 2,
            channelLayout: "stereo");

    private static void AssertParamName<TException>(
        string expected,
        Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}