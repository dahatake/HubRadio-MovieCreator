using System.Reflection;
using HubRadio.App.Media;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class IngestValidatorTests
{
    [TestMethod]
    public void Validate_Stage0PreservesExactTimesNegativeOffsetsAndTypedDifferences()
    {
        MediaProbeResult probe = CreateProbe(
            formatDuration: 1081.42m,
            streams:
            [
                Video(
                    nominalFrameRate: new RationalNumber(60000, 1001),
                    averageFrameRate: new RationalNumber(37389600, 648883),
                    startSeconds: -0.083333m,
                    durationSeconds: 1081.401667m),
                Audio(startSeconds: -0.044m, durationSeconds: 1081.42m),
            ],
            warnings: ["variable-rate-fields-differ"]);

        MediaInfo result = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements());

        Assert.AreEqual(new RationalTime(54071, 50), result.FormatDuration);
        MediaStreamInfo video = result.Streams.Single(
            static stream => stream.Kind == MediaStreamType.Video);
        Assert.AreEqual(new RationalTime(-83333, 1000000), video.Start);
        Assert.AreEqual(new RationalTime(1081401667, 1000000), video.Duration);
        MediaStreamInfo audio = result.Streams.Single(
            static stream => stream.Kind == MediaStreamType.Audio);
        Assert.AreEqual(new RationalTime(-11, 250), audio.Start);
        Assert.AreEqual(new RationalTime(54071, 50), audio.Duration);

        AssertWarningCodes(
            result,
            MediaInfoWarningCode.DurationFieldsDiffer,
            MediaInfoWarningCode.VariableRateFieldsDiffer);
        DurationFieldsDifferWarningEvidence durationEvidence =
            Assert.IsInstanceOfType<DurationFieldsDifferWarningEvidence>(
                result.Warnings[0].Evidence);
        Assert.HasCount(3, durationEvidence.Fields);
        AssertDurationField(
            durationEvidence.Fields[0],
            MediaDurationFieldKind.FormatDuration,
            streamIndex: null,
            new RationalTime(54071, 50));
        AssertDurationField(
            durationEvidence.Fields[1],
            MediaDurationFieldKind.StreamEndOffset,
            streamIndex: 0,
            new RationalTime(540659167, 500000));
        AssertDurationField(
            durationEvidence.Fields[2],
            MediaDurationFieldKind.StreamEndOffset,
            streamIndex: 1,
            new RationalTime(135172, 125));

        VariableRateFieldsDifferWarningEvidence rateEvidence =
            Assert.IsInstanceOfType<VariableRateFieldsDifferWarningEvidence>(
                result.Warnings[1].Evidence);
        Assert.IsTrue(rateEvidence.ProbeReportedFieldsDiffer);
        Assert.HasCount(1, rateEvidence.Differences);
        MediaFrameRateDifference difference = rateEvidence.Differences[0];
        Assert.AreEqual(0, difference.StreamIndex);
        Assert.AreEqual(new RationalNumber(60000, 1001), difference.NominalFrameRate);
        Assert.AreEqual(new RationalNumber(37389600, 648883), difference.AverageFrameRate);
    }

    [TestMethod]
    public void ExactDecimalConversionNeverUsesBinaryFloatingPoint()
    {
        Assert.AreEqual(
            new RationalTime(1081401667, 1000000),
            IngestValidator.DecimalSecondsToRationalTime(1081.401667m));
        Assert.AreEqual(
            new RationalTime(-83333, 1000000),
            IngestValidator.DecimalSecondsToRationalTime(-0.083333m));
        Assert.AreEqual(
            new RationalTime(1, 10),
            IngestValidator.DecimalSecondsToRationalTime(0.100000m));
        Assert.AreEqual(
            default(RationalTime),
            IngestValidator.DecimalSecondsToRationalTime(decimal.Zero));
    }

    [TestMethod]
    public void Validate_SelectsLowestVideoIndexAndKeepsAudioAbsentNonBlockingWarning()
    {
        MediaProbeResult probe = CreateProbe(
            formatDuration: null,
            streams:
            [
                Video(index: 9, durationSeconds: null),
                Video(index: 2, durationSeconds: null),
            ],
            warnings: ["multiple-video-streams", "audio-track-absent"]);

        MediaInfo result = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements());

        Assert.AreEqual(2, result.PrimaryVideoStream.Index);
        AssertWarningCodes(
            result,
            MediaInfoWarningCode.AudioTrackAbsent,
            MediaInfoWarningCode.MultipleVideoStreams);
        MediaStreamCountWarningEvidence countEvidence =
            Assert.IsInstanceOfType<MediaStreamCountWarningEvidence>(
                result.Warnings[1].Evidence);
        Assert.AreEqual(2, countEvidence.ObservedCount);
        Assert.IsFalse(typeof(MediaInfoWarning).GetProperties().Any(static property =>
            property.Name.Contains("Blocking", StringComparison.Ordinal) ||
            property.Name.Contains("Severity", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_UsesOnlyExplicitPositiveResolutionRequirements()
    {
        MediaProbeResult probe = CreateProbe(
            streams: [Video(width: 1280, height: 720), Audio()]);

        MediaInfo belowRequired = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements(1920, 1080));
        MediaInfo exactlyRequired = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements(1280, 720));
        MediaInfo aboveRequirement = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements(640, 360));
        MediaInfo unset = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements());

        AssertWarningCodes(belowRequired, MediaInfoWarningCode.LowResolution);
        Assert.AreEqual(1920, belowRequired.RequiredMinimumVideoWidth);
        Assert.AreEqual(1080, belowRequired.RequiredMinimumVideoHeight);
        LowResolutionWarningEvidence evidence =
            Assert.IsInstanceOfType<LowResolutionWarningEvidence>(
                belowRequired.Warnings.Single().Evidence);
        Assert.AreEqual(1280, evidence.ObservedWidth);
        Assert.AreEqual(720, evidence.ObservedHeight);
        Assert.AreEqual(1920, evidence.RequiredMinimumWidth);
        Assert.AreEqual(1080, evidence.RequiredMinimumHeight);

        Assert.IsEmpty(exactlyRequired.Warnings);
        Assert.AreEqual(1280, exactlyRequired.RequiredMinimumVideoWidth);
        Assert.AreEqual(720, exactlyRequired.RequiredMinimumVideoHeight);
        Assert.IsEmpty(aboveRequirement.Warnings);
        Assert.AreEqual(640, aboveRequirement.RequiredMinimumVideoWidth);
        Assert.AreEqual(360, aboveRequirement.RequiredMinimumVideoHeight);
        Assert.IsEmpty(unset.Warnings);
        Assert.IsNull(unset.RequiredMinimumVideoWidth);
        Assert.IsNull(unset.RequiredMinimumVideoHeight);
    }

    [TestMethod]
    public void RequirementsRejectPartialZeroAndNegativeThresholds()
    {
        AssertParamName<ArgumentException>(
            "minimumVideoWidth",
            () => new IngestValidationRequirements(320, null));
        AssertParamName<ArgumentException>(
            "minimumVideoWidth",
            () => new IngestValidationRequirements(null, 180));
        AssertParamName<ArgumentOutOfRangeException>(
            "minimumVideoWidth",
            () => new IngestValidationRequirements(0, 180));
        AssertParamName<ArgumentOutOfRangeException>(
            "minimumVideoWidth",
            () => new IngestValidationRequirements(-1, 180));
        AssertParamName<ArgumentOutOfRangeException>(
            "minimumVideoHeight",
            () => new IngestValidationRequirements(320, 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "minimumVideoHeight",
            () => new IngestValidationRequirements(320, -1));
    }

    [TestMethod]
    public void Validate_DurationComparisonUsesStreamEndWhenStartIsKnown()
    {
        MediaProbeResult equalEnds = CreateProbe(
            formatDuration: 10m,
            streams:
            [
                Video(startSeconds: -1m, durationSeconds: 11m),
                Audio(startSeconds: 2m, durationSeconds: 8m),
            ]);
        MediaProbeResult differingEnd = CreateProbe(
            formatDuration: 10m,
            streams:
            [
                Video(startSeconds: -1m, durationSeconds: 10m),
                Audio(startSeconds: null, durationSeconds: 10m),
            ]);

        MediaInfo equal = IngestValidator.Validate(
            equalEnds,
            new IngestValidationRequirements());
        MediaInfo different = IngestValidator.Validate(
            differingEnd,
            new IngestValidationRequirements());

        Assert.IsEmpty(equal.Warnings);
        AssertWarningCodes(different, MediaInfoWarningCode.DurationFieldsDiffer);
        DurationFieldsDifferWarningEvidence evidence =
            Assert.IsInstanceOfType<DurationFieldsDifferWarningEvidence>(
                different.Warnings.Single().Evidence);
        AssertDurationField(
            evidence.Fields[0],
            MediaDurationFieldKind.FormatDuration,
            streamIndex: null,
            new RationalTime(10, 1));
        AssertDurationField(
            evidence.Fields[1],
            MediaDurationFieldKind.StreamEndOffset,
            streamIndex: 0,
            new RationalTime(9, 1));
        AssertDurationField(
            evidence.Fields[2],
            MediaDurationFieldKind.StreamDuration,
            streamIndex: 1,
            new RationalTime(10, 1));
    }

    [TestMethod]
    public void Validate_NullOrSingleKnownDurationDoesNotInventAWarning()
    {
        MediaInfo allNull = IngestValidator.Validate(
            CreateProbe(
                formatDuration: null,
                streams:
                [
                    Video(startSeconds: -1m, durationSeconds: null),
                    Audio(startSeconds: 2m, durationSeconds: null),
                ]),
            new IngestValidationRequirements());
        MediaInfo formatOnly = IngestValidator.Validate(
            CreateProbe(
                formatDuration: 10m,
                streams:
                [
                    Video(durationSeconds: null),
                    Audio(durationSeconds: null),
                ]),
            new IngestValidationRequirements());

        Assert.IsEmpty(allNull.Warnings);
        Assert.IsEmpty(formatOnly.Warnings);
    }

    [TestMethod]
    public void Validate_MapsAllSixProbeWarningsWithoutFreeText()
    {
        (MediaProbeResult Probe, MediaInfoWarningCode Expected)[] cases =
        [
            (CreateProbe(
                streams: [Video(), Audio(), Audio(index: 2)],
                warnings: ["multiple-audio-streams"]),
             MediaInfoWarningCode.MultipleAudioStreams),
            (CreateProbe(
                streams: [Video(), Video(index: 2), Audio()],
                warnings: ["multiple-video-streams"]),
             MediaInfoWarningCode.MultipleVideoStreams),
            (CreateProbe(
                streams: [Video()],
                warnings: ["audio-track-absent"]),
             MediaInfoWarningCode.AudioTrackAbsent),
            (CreateProbe(
                signature: ContainerSignature.Ebml,
                warnings: ["signature-format-mismatch"]),
             MediaInfoWarningCode.SignatureFormatMismatch),
            (CreateProbe(
                signature: ContainerSignature.Unknown,
                warnings: ["unrecognized-container-signature"]),
             MediaInfoWarningCode.UnrecognizedContainerSignature),
            (CreateProbe(
                streams:
                [
                    Video(
                        nominalFrameRate: new RationalNumber(30, 1),
                        averageFrameRate: new RationalNumber(30000, 1001)),
                    Audio(),
                ],
                warnings: ["variable-rate-fields-differ"]),
             MediaInfoWarningCode.VariableRateFieldsDiffer),
        ];

        foreach ((MediaProbeResult probe, MediaInfoWarningCode expected) in cases)
        {
            MediaInfo result = IngestValidator.Validate(
                probe,
                new IngestValidationRequirements());

            AssertWarningCodes(result, expected);
        }

        Assert.IsNull(typeof(MediaInfoWarning).GetProperty("Message"));
    }

    [TestMethod]
    public void Validate_VariableRateWarningRequiresMatchingProbeReportAndRateDifference()
    {
        MediaInfo equalWithoutReport = IngestValidator.Validate(
            CreateProbe(),
            new IngestValidationRequirements());
        MediaProbeResult equalWithReport = CreateProbe(
            warnings: ["variable-rate-fields-differ"]);
        MediaProbeResult unequalWithoutReport = CreateProbe(streams:
            [
                Video(
                    nominalFrameRate: new RationalNumber(30, 1),
                    averageFrameRate: new RationalNumber(24000, 1001)),
                Audio(),
            ]);
        MediaInfo matching = IngestValidator.Validate(
            CreateProbe(
                streams:
                [
                    Video(
                        nominalFrameRate: new RationalNumber(30, 1),
                        averageFrameRate: new RationalNumber(24000, 1001)),
                    Audio(),
                ],
                warnings: ["variable-rate-fields-differ"]),
            new IngestValidationRequirements());

        Assert.IsEmpty(equalWithoutReport.Warnings);
        AssertParamName<ArgumentException>(
            "probeWarnings",
            () => IngestValidator.Validate(
                equalWithReport,
                new IngestValidationRequirements()));
        AssertParamName<ArgumentException>(
            "probeWarnings",
            () => IngestValidator.Validate(
                unequalWithoutReport,
                new IngestValidationRequirements()));
        AssertWarningCodes(matching, MediaInfoWarningCode.VariableRateFieldsDiffer);
        VariableRateFieldsDifferWarningEvidence observedEvidence =
            Assert.IsInstanceOfType<VariableRateFieldsDifferWarningEvidence>(
                matching.Warnings.Single().Evidence);
        Assert.IsTrue(observedEvidence.ProbeReportedFieldsDiffer);
        Assert.HasCount(1, observedEvidence.Differences);
    }

    [TestMethod]
    public void Validate_PreservesUnknownContainerFormatCodecAndOtherStreams()
    {
        MediaProbeResult probe = CreateProbe(
            signature: ContainerSignature.Unknown,
            formatNames: ["future-container"],
            formatLongName: "Future Container",
            streams:
            [
                Video(codecName: "future-video"),
                Audio(codecName: "future-audio"),
                Other(index: 7, codecName: "future-data"),
            ],
            warnings: ["unrecognized-container-signature"]);

        MediaInfo result = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements());

        Assert.AreEqual(MediaContainerClassification.Unknown, result.ContainerClassification);
        Assert.AreEqual("future-container", result.FormatNames.Single());
        Assert.AreEqual("Future Container", result.FormatLongName);
        CollectionAssert.AreEqual(
            new[] { "future-video", "future-audio", "future-data" },
            result.Streams.Select(static stream => stream.CodecName).ToArray());
        Assert.AreEqual(MediaStreamType.Other, result.Streams[2].Kind);
        AssertWarningCodes(
            result,
            MediaInfoWarningCode.UnrecognizedContainerSignature);
    }

    [TestMethod]
    public void Validate_WarningsAreEnumOrderedAndDeduplicatedAcrossSources()
    {
        MediaProbeResult probe = CreateProbe(
            signature: ContainerSignature.Unknown,
            formatNames: ["future-container"],
            formatDuration: null,
            streams:
            [
                Video(
                    index: 3,
                    nominalFrameRate: new RationalNumber(30, 1),
                    averageFrameRate: new RationalNumber(24000, 1001),
                    durationSeconds: null),
                Video(index: 1, durationSeconds: null),
            ],
            warnings:
            [
                "variable-rate-fields-differ",
                "audio-track-absent",
                "multiple-video-streams",
                "unrecognized-container-signature",
                "variable-rate-fields-differ",
                "audio-track-absent",
            ]);

        MediaInfo result = IngestValidator.Validate(
            probe,
            new IngestValidationRequirements());

        AssertWarningCodes(
            result,
            MediaInfoWarningCode.AudioTrackAbsent,
            MediaInfoWarningCode.MultipleVideoStreams,
            MediaInfoWarningCode.UnrecognizedContainerSignature,
            MediaInfoWarningCode.VariableRateFieldsDiffer);
        Assert.AreEqual(
            result.Warnings.Count,
            result.Warnings.Select(static warning => warning.Code).Distinct().Count());
    }

    [TestMethod]
    public void Validate_RejectsFabricatedCrossFieldProbeWarnings()
    {
        MediaProbeResult audioContradiction = CreateProbe(
            warnings: ["audio-track-absent"]);
        MediaProbeResult signatureContradiction = CreateProbe(
            warnings: ["signature-format-mismatch"]);
        MediaProbeResult unknownContradiction = CreateProbe(
            warnings: ["unrecognized-container-signature"]);

        Assert.ThrowsExactly<ArgumentException>(() => IngestValidator.Validate(
            audioContradiction,
            new IngestValidationRequirements()));
        Assert.ThrowsExactly<ArgumentException>(() => IngestValidator.Validate(
            signatureContradiction,
            new IngestValidationRequirements()));
        Assert.ThrowsExactly<ArgumentException>(() => IngestValidator.Validate(
            unknownContradiction,
            new IngestValidationRequirements()));
    }

    [TestMethod]
    public void Validate_RejectsNullInputs()
    {
        MediaProbeResult probe = CreateProbe();

        AssertParamName<ArgumentNullException>(
            "probeResult",
            () => IngestValidator.Validate(
                null!,
                new IngestValidationRequirements()));
        AssertParamName<ArgumentNullException>(
            "requirements",
            () => IngestValidator.Validate(probe, null!));
    }

    [TestMethod]
    public void PublicSurfaceIsImmutablePrivateDataFreeAndPersistenceFree()
    {
        Type[] coreTypes = typeof(MediaInfo).Assembly
            .GetTypes()
            .Where(static type =>
                type.IsPublic &&
                string.Equals(type.Namespace, "HubRadio.Core.Media", StringComparison.Ordinal))
            .ToArray();
        Assert.IsFalse(typeof(MediaInfo).Assembly.GetReferencedAssemblies().Any(static assembly =>
            string.Equals(
                assembly.Name,
                "HubRadio.Infrastructure",
                StringComparison.Ordinal)));

        string[] forbiddenNames =
        [
            "Path",
            "Tag",
            "StandardError",
            "Stderr",
            "Message",
            "Severity",
            "Blocking",
            "Store",
            "Database",
            "Rpc",
            "Bridge",
        ];
        foreach (Type type in coreTypes.Append(typeof(IngestValidationRequirements)))
        {
            PropertyInfo[] properties = type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.IsTrue(
                properties.All(static property => property.SetMethod is null),
                type.FullName);
            foreach (MemberInfo member in type.GetMembers(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(
                    forbiddenNames.Any(name =>
                        member.Name.Contains(name, StringComparison.OrdinalIgnoreCase)),
                    $"{type.FullName}.{member.Name}");
            }
        }

        Type[] forbiddenNumericTypes = [typeof(TimeSpan), typeof(double), typeof(float)];
        foreach (Type type in coreTypes.Append(typeof(IngestValidator)))
        {
            IEnumerable<Type> surfaceTypes = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(static property => property.PropertyType)
                .Concat(type
                    .GetMethods(
                        BindingFlags.Public |
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.DeclaredOnly)
                    .SelectMany(static method =>
                        method.GetParameters()
                            .Select(static parameter => parameter.ParameterType)
                            .Append(method.ReturnType)))
                .Concat(type
                    .GetConstructors()
                    .SelectMany(static constructor => constructor
                        .GetParameters()
                        .Select(static parameter => parameter.ParameterType)));
            Assert.IsFalse(
                surfaceTypes.Any(forbiddenNumericTypes.Contains),
                type.FullName);
        }

        MethodInfo[] validatorMethods = typeof(IngestValidator)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.HasCount(1, validatorMethods);
        MethodInfo validate = validatorMethods[0];
        Assert.AreEqual("Validate", validate.Name);
        Assert.AreEqual(typeof(MediaInfo), validate.ReturnType);
    }

    private static MediaProbeResult CreateProbe(
        ContainerSignature signature = ContainerSignature.IsoBaseMediaFtyp,
        IReadOnlyList<string>? formatNames = null,
        string? formatLongName = "QuickTime / MOV",
        decimal? formatDuration = 10m,
        IReadOnlyList<ProbedMediaStream>? streams = null,
        IReadOnlyList<string>? warnings = null)
    {
        return new MediaProbeResult(
            EntityId<AssetIdTag>.Parse("11111111-1111-4111-8111-111111111111"),
            EntityId<ProjectIdTag>.Parse("22222222-2222-4222-8222-222222222222"),
            EntityId<JobIdTag>.Parse("33333333-3333-4333-8333-333333333333"),
            "source.mov",
            new ContentDigest(new string('a', 64)),
            byteSize: 4096,
            signature,
            formatNames ?? ["mov", "mp4"],
            formatLongName,
            formatDuration,
            declaredByteSize: 4096,
            bitRate: null,
            probeScore: null,
            streams ?? [Video(), Audio()],
            warnings ?? []);
    }

    private static ProbedMediaStream Video(
        int index = 0,
        string codecName = "h264",
        int width = 1920,
        int height = 1080,
        RationalNumber? nominalFrameRate = null,
        RationalNumber? averageFrameRate = null,
        decimal? startSeconds = 0m,
        decimal? durationSeconds = 10m) =>
        new(
            index,
            MediaStreamKind.Video,
            codecName,
            profile: null,
            width,
            height,
            pixelFormat: "yuv420p",
            nominalFrameRate ?? new RationalNumber(30, 1),
            averageFrameRate ?? new RationalNumber(30, 1),
            new RationalNumber(1, 1000),
            startSeconds,
            durationSeconds,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static ProbedMediaStream Audio(
        int index = 1,
        string codecName = "aac",
        decimal? startSeconds = 0m,
        decimal? durationSeconds = 10m) =>
        new(
            index,
            MediaStreamKind.Audio,
            codecName,
            profile: null,
            width: null,
            height: null,
            pixelFormat: null,
            nominalFrameRate: null,
            averageFrameRate: null,
            new RationalNumber(1, 48000),
            startSeconds,
            durationSeconds,
            sampleRate: 48000,
            channels: 2,
            channelLayout: "stereo");

    private static ProbedMediaStream Other(int index, string codecName) =>
        new(
            index,
            MediaStreamKind.Other,
            codecName,
            profile: null,
            width: null,
            height: null,
            pixelFormat: null,
            nominalFrameRate: null,
            averageFrameRate: null,
            new RationalNumber(1, 1000),
            startSeconds: null,
            durationSeconds: null,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static void AssertWarningCodes(
        MediaInfo mediaInfo,
        params MediaInfoWarningCode[] expected)
    {
        CollectionAssert.AreEqual(
            expected,
            mediaInfo.Warnings.Select(static warning => warning.Code).ToArray());
    }

    private static void AssertDurationField(
        MediaDurationFieldEvidence field,
        MediaDurationFieldKind expectedKind,
        int? streamIndex,
        RationalTime expectedValue)
    {
        Assert.AreEqual(expectedKind, field.Kind);
        Assert.AreEqual(streamIndex, field.StreamIndex);
        Assert.AreEqual(expectedValue, field.Value);
    }

    private static void AssertParamName<TException>(
        string expected,
        Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}