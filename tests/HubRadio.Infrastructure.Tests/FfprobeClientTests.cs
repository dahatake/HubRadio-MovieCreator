using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class FfprobeClientTests
{
    private const string ExpectedShowEntries =
        "format=format_name,format_long_name,duration,size,bit_rate,probe_score:" +
        "stream=index,codec_type,codec_name,profile,width,height,pix_fmt," +
        "r_frame_rate,avg_frame_rate,time_base,start_time,duration," +
        "sample_rate,channels,channel_layout";

    private static readonly DateTimeOffset CreatedAt =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    [TestMethod]
    public void ParseProbeOutput_Stage0MovReturnsExactTypedMetadataAndVfrWarning()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(
            fixture.Staged.ByteSize,
            [
                CreateVideoStream(
                    nominalFrameRate: "60000/1001",
                    averageFrameRate: "37389600/648883"),
                CreateAudioStream(),
            ]);

        MediaProbeResult result = FfprobeClient.ParseProbeOutput(
            fixture.Staged,
            ContainerSignature.IsoBaseMediaFtyp,
            json);

        Assert.AreSame(fixture.Staged.CandidateId, result.CandidateId);
        Assert.AreSame(fixture.Staged.ProjectId, result.ProjectId);
        Assert.AreSame(fixture.Staged.JobId, result.JobId);
        Assert.AreEqual("meeting-source.mov", result.SourceFileName);
        Assert.AreSame(fixture.Staged.ContentDigest, result.ContentDigest);
        Assert.AreEqual(fixture.Staged.ByteSize, result.ByteSize);
        Assert.AreEqual(ContainerSignature.IsoBaseMediaFtyp, result.ContainerSignature);
        CollectionAssert.AreEqual(
            new[] { "mov", "mp4", "m4a", "3gp", "3g2", "mj2" },
            result.FormatNames.ToArray());
        Assert.AreEqual("QuickTime / MOV", result.FormatLongName);
        Assert.AreEqual(1081.42m, result.DurationSeconds);
        Assert.AreEqual(fixture.Staged.ByteSize, result.DeclaredByteSize);
        Assert.AreEqual(48044773L, result.BitRate);
        Assert.AreEqual(100, result.ProbeScore);
        Assert.HasCount(2, result.Streams);

        ProbedMediaStream video = result.Streams[0];
        Assert.AreEqual(0, video.Index);
        Assert.AreEqual(MediaStreamKind.Video, video.Kind);
        Assert.AreEqual("hevc", video.CodecName);
        Assert.AreEqual("Main", video.Profile);
        Assert.AreEqual(3840, video.Width);
        Assert.AreEqual(2160, video.Height);
        Assert.AreEqual("yuv420p", video.PixelFormat);
        Assert.AreEqual(new RationalNumber(60000, 1001), video.NominalFrameRate);
        Assert.AreEqual(new RationalNumber(37389600, 648883), video.AverageFrameRate);
        Assert.AreEqual(new RationalNumber(1, 600), video.TimeBase);
        Assert.AreEqual(-0.083333m, video.StartSeconds);
        Assert.AreEqual(1081.401667m, video.DurationSeconds);
        Assert.IsNull(video.SampleRate);
        Assert.IsNull(video.Channels);
        Assert.IsNull(video.ChannelLayout);

        ProbedMediaStream audio = result.Streams[1];
        Assert.AreEqual(1, audio.Index);
        Assert.AreEqual(MediaStreamKind.Audio, audio.Kind);
        Assert.AreEqual("aac", audio.CodecName);
        Assert.AreEqual("LC", audio.Profile);
        Assert.IsNull(audio.Width);
        Assert.IsNull(audio.Height);
        Assert.IsNull(audio.PixelFormat);
        Assert.IsNull(audio.NominalFrameRate);
        Assert.IsNull(audio.AverageFrameRate);
        Assert.AreEqual(new RationalNumber(1, 48000), audio.TimeBase);
        Assert.AreEqual(-0.044m, audio.StartSeconds);
        Assert.AreEqual(1081.42m, audio.DurationSeconds);
        Assert.AreEqual(48000, audio.SampleRate);
        Assert.AreEqual(2, audio.Channels);
        Assert.AreEqual("stereo", audio.ChannelLayout);
        CollectionAssert.AreEqual(
            new[] { "variable-rate-fields-differ" },
            result.Warnings.ToArray());
    }

    [TestMethod]
    public void ParseProbeOutput_OmittedOptionalFieldsRemainNullAndUnknownFormatsAreNotRejected()
    {
        using ProbeFixture fixture = new();
        string json = $$"""
            {
              "streams": [
                {
                  "index": "0",
                  "codec_type": "video",
                  "codec_name": "future-codec",
                  "width": "1",
                  "height": "1",
                  "r_frame_rate": "1/1",
                  "avg_frame_rate": "1/1",
                  "time_base": "1/1000"
                }
              ],
              "format": {
                "format_name": "future-container"
              }
            }
            """;

        MediaProbeResult result = FfprobeClient.ParseProbeOutput(
            fixture.Staged,
            ContainerSignature.Unknown,
            json);

        Assert.AreEqual("future-container", result.FormatNames.Single());
        Assert.IsNull(result.FormatLongName);
        Assert.IsNull(result.DurationSeconds);
        Assert.IsNull(result.DeclaredByteSize);
        Assert.IsNull(result.BitRate);
        Assert.IsNull(result.ProbeScore);
        ProbedMediaStream video = result.Streams.Single();
        Assert.IsNull(video.Profile);
        Assert.IsNull(video.PixelFormat);
        Assert.IsNull(video.StartSeconds);
        Assert.IsNull(video.DurationSeconds);
        CollectionAssert.AreEqual(
            new[] { "audio-track-absent", "unrecognized-container-signature" },
            result.Warnings.ToArray());
    }

    [TestMethod]
    public void ParseProbeOutput_NoAudioAddsOnlyObjectiveWarning()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(
            fixture.Staged.ByteSize,
            [CreateVideoStream()]);

        MediaProbeResult result = Parse(fixture, json);

        CollectionAssert.AreEqual(
            new[] { "audio-track-absent" },
            result.Warnings.ToArray());
    }

    [TestMethod]
    public void ParseProbeOutput_MultipleTracksProduceSortedDeduplicatedWarnings()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(
            fixture.Staged.ByteSize,
            [
                CreateVideoStream(index: 0),
                CreateVideoStream(index: 1),
                CreateAudioStream(index: 2),
                CreateAudioStream(index: 3),
            ]);

        MediaProbeResult result = Parse(fixture, json);

        CollectionAssert.AreEqual(
            new[] { "multiple-audio-streams", "multiple-video-streams" },
            result.Warnings.ToArray());
    }

    [TestMethod]
    public void ParseProbeOutput_SignatureMismatchAndUnknownSignatureRemainWarnings()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(fixture.Staged.ByteSize);

        MediaProbeResult mismatch = FfprobeClient.ParseProbeOutput(
            fixture.Staged,
            ContainerSignature.Ebml,
            json);
        MediaProbeResult unknown = FfprobeClient.ParseProbeOutput(
            fixture.Staged,
            ContainerSignature.Unknown,
            json);

        CollectionAssert.AreEqual(
            new[] { "signature-format-mismatch" },
            mismatch.Warnings.ToArray());
        CollectionAssert.AreEqual(
            new[] { "unrecognized-container-signature" },
            unknown.Warnings.ToArray());
    }

    [TestMethod]
    public void ParseProbeOutput_RationallyEquivalentRatesDoNotProduceVfrWarning()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(
            fixture.Staged.ByteSize,
            [
                CreateVideoStream(
                    nominalFrameRate: "30000/1000",
                    averageFrameRate: "30/1"),
                CreateAudioStream(),
            ]);

        MediaProbeResult result = Parse(fixture, json);

        Assert.AreEqual(
            result.Streams[0].NominalFrameRate,
            result.Streams[0].AverageFrameRate);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void ParseProbeOutput_NoVideoFailsWithDedicatedReason()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(
            fixture.Staged.ByteSize,
            [CreateAudioStream(index: 0)]);

        MediaProbeException exception = Assert.ThrowsExactly<MediaProbeException>(() =>
            Parse(fixture, json));

        Assert.AreEqual(MediaProbeFailureReason.NoVideoStream, exception.Reason);
    }

    [TestMethod]
    public void ParseProbeOutput_MalformedDuplicateDeepDuplicateAndReplacementJsonFailSafely()
    {
        using ProbeFixture fixture = new();
        string[] invalidJson =
        [
            "{",
            "{\"streams\":[],\"format\":{},}",
            "{\"streams\":[],\"streams\":[],\"format\":{\"format_name\":\"mov\"}}",
            "{\"streams\":[{\"index\":0,\"index\":1}],\"format\":{\"format_name\":\"mov\"}}",
            CreateProbeJson(fixture.Staged.ByteSize)
                .Replace("QuickTime / MOV", "QuickTime \uFFFD MOV", StringComparison.Ordinal),
            CreateProbeJson(fixture.Staged.ByteSize)
                .Replace("QuickTime / MOV", "QuickTime \\uFFFD MOV", StringComparison.Ordinal),
        ];

        foreach (string json in invalidJson)
        {
            AssertFailure(
                () => Parse(fixture, json),
                MediaProbeFailureReason.InvalidJson);
        }
    }

    [TestMethod]
    public void ParseProbeOutput_ExtraRootStreamFormatAndNonemptyEnvelopeAreRejected()
    {
        using ProbeFixture fixture = new();
        string valid = CreateProbeJson(fixture.Staged.ByteSize);
        string[] invalidJson =
        [
            CreateProbeJson(
                fixture.Staged.ByteSize,
                rootExtra: "\"unexpected\": true"),
            valid.Replace(
                "\"time_base\": \"1/600\"",
                "\"time_base\": \"1/600\", \"tags\": {}",
                StringComparison.Ordinal),
            CreateProbeJson(
                fixture.Staged.ByteSize,
                formatExtra: "\"filename\": \"private.mov\""),
            CreateProbeJson(
                fixture.Staged.ByteSize,
                rootExtra: "\"programs\": [{}]"),
        ];

        foreach (string json in invalidJson)
        {
            AssertFailure(
                () => Parse(fixture, json),
                MediaProbeFailureReason.InvalidMetadata);
        }
    }

    [TestMethod]
    public void ParseProbeOutput_CanonicalFfprobeEmptyEnvelopesAreAccepted()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(
            fixture.Staged.ByteSize,
            includeCanonicalEnvelopes: true);

        MediaProbeResult result = Parse(fixture, json);

        Assert.HasCount(2, result.Streams);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void ParseProbeOutput_WrongJsonTypesAreRejected()
    {
        using ProbeFixture fixture = new();
        string valid = CreateProbeJson(fixture.Staged.ByteSize);
        string[] invalidJson =
        [
            valid.Replace("\"index\": 0", "\"index\": true", StringComparison.Ordinal),
            valid.Replace(
                "\"codec_type\": \"video\"",
                "\"codec_type\": 1",
                StringComparison.Ordinal),
            valid.Replace(
                "\"codec_name\": \"hevc\"",
                "\"codec_name\": null",
                StringComparison.Ordinal),
            valid.Replace("\"width\": 3840", "\"width\": false", StringComparison.Ordinal),
            valid.Replace(
                "\"r_frame_rate\": \"60000/1001\"",
                "\"r_frame_rate\": 60",
                StringComparison.Ordinal),
            valid.Replace(
                "\"start_time\": \"-0.083333\"",
                "\"start_time\": -0.083333",
                StringComparison.Ordinal),
            valid.Replace(
                "\"sample_rate\": \"48000\"",
                "\"sample_rate\": 48000",
                StringComparison.Ordinal),
            valid.Replace("\"channels\": 2", "\"channels\": true", StringComparison.Ordinal),
            valid.Replace(
                $"\"size\": \"{fixture.Staged.ByteSize}\"",
                $"\"size\": {fixture.Staged.ByteSize}",
                StringComparison.Ordinal),
            valid.Replace(
                "\"bit_rate\": \"48044773\"",
                "\"bit_rate\": 48044773",
                StringComparison.Ordinal),
            valid.Replace(
                "\"probe_score\": 100",
                "\"probe_score\": false",
                StringComparison.Ordinal),
        ];

        foreach (string json in invalidJson)
        {
            AssertFailure(
                () => Parse(fixture, json),
                MediaProbeFailureReason.InvalidMetadata);
        }
    }

    [TestMethod]
    public void ParseProbeOutput_NumericStringsForIntegerFieldsAreAcceptedExactly()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(fixture.Staged.ByteSize)
            .Replace("\"index\": 0", "\"index\": \"0\"", StringComparison.Ordinal)
            .Replace("\"width\": 3840", "\"width\": \"3840\"", StringComparison.Ordinal)
            .Replace("\"height\": 2160", "\"height\": \"2160\"", StringComparison.Ordinal)
            .Replace("\"channels\": 2", "\"channels\": \"2\"", StringComparison.Ordinal)
            .Replace("\"probe_score\": 100", "\"probe_score\": \"100\"", StringComparison.Ordinal);

        MediaProbeResult result = Parse(fixture, json);

        Assert.AreEqual(0, result.Streams[0].Index);
        Assert.AreEqual(3840, result.Streams[0].Width);
        Assert.AreEqual(2160, result.Streams[0].Height);
        Assert.AreEqual(2, result.Streams[1].Channels);
        Assert.AreEqual(100, result.ProbeScore);
    }

    [TestMethod]
    [DataRow("0/1")]
    [DataRow("1/0")]
    [DataRow("1/-1")]
    [DataRow("1.0/1")]
    [DataRow("1/1/1")]
    [DataRow("invalid/1")]
    [DataRow("9223372036854775808/1")]
    public void ParseProbeOutput_InvalidRationalsAreRejected(string invalidRational)
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(fixture.Staged.ByteSize)
            .Replace(
                "\"r_frame_rate\": \"60000/1001\"",
                $"\"r_frame_rate\": \"{invalidRational}\"",
                StringComparison.Ordinal);

        AssertFailure(
            () => Parse(fixture, json),
            MediaProbeFailureReason.InvalidMetadata);
    }

    [TestMethod]
    [DataRow(0, 2160)]
    [DataRow(3840, 0)]
    [DataRow(-1, 2160)]
    [DataRow(3840, -1)]
    public void ParseProbeOutput_NonpositiveVideoDimensionsAreRejected(
        int width,
        int height)
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(fixture.Staged.ByteSize)
            .Replace("\"width\": 3840", $"\"width\": {width}", StringComparison.Ordinal)
            .Replace("\"height\": 2160", $"\"height\": {height}", StringComparison.Ordinal);

        AssertFailure(
            () => Parse(fixture, json),
            MediaProbeFailureReason.InvalidMetadata);
    }

    [TestMethod]
    public void ParseProbeOutput_DeclaredSizeMustMatchStagedBytes()
    {
        using ProbeFixture fixture = new();
        string json = CreateProbeJson(fixture.Staged.ByteSize)
            .Replace(
                $"\"size\": \"{fixture.Staged.ByteSize}\"",
                $"\"size\": \"{fixture.Staged.ByteSize + 1}\"",
                StringComparison.Ordinal);

        AssertFailure(
            () => Parse(fixture, json),
            MediaProbeFailureReason.InvalidMetadata);
    }

    [TestMethod]
    public void ParseProbeOutput_InvalidDurationsDecimalsAndProbeScoresAreRejected()
    {
        using ProbeFixture fixture = new();
        string valid = CreateProbeJson(fixture.Staged.ByteSize);
        string[] invalidJson =
        [
            valid.Replace(
                "\"duration\": \"1081.42\"",
                "\"duration\": \"-0.01\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"duration\": \"1081.401667\"",
                "\"duration\": \"-0.01\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"duration\": \"1081.42\"",
                "\"duration\": \"1e3\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"duration\": \"1081.42\"",
                "\"duration\": \"NaN\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"start_time\": \"-0.083333\"",
                "\"start_time\": \"Infinity\"",
                StringComparison.Ordinal),
            valid.Replace("\"probe_score\": 100", "\"probe_score\": -1", StringComparison.Ordinal),
            valid.Replace("\"probe_score\": 100", "\"probe_score\": 101", StringComparison.Ordinal),
        ];

        foreach (string json in invalidJson)
        {
            AssertFailure(
                () => Parse(fixture, json),
                MediaProbeFailureReason.InvalidMetadata);
        }
    }

    [TestMethod]
    public void ParseProbeOutput_InvalidKindSpecificAndAudioMetadataAreRejected()
    {
        using ProbeFixture fixture = new();
        string valid = CreateProbeJson(fixture.Staged.ByteSize);
        string[] invalidJson =
        [
            valid.Replace(
                "\"sample_rate\": \"48000\"",
                "\"sample_rate\": \"0\"",
                StringComparison.Ordinal),
            valid.Replace("\"channels\": 2", "\"channels\": 0", StringComparison.Ordinal),
            valid.Replace(
                "\"codec_type\": \"audio\"",
                "\"codec_type\": \"subtitle\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"pix_fmt\": \"yuv420p\"",
                "\"pix_fmt\": \"yuv420p\", \"sample_rate\": \"48000\"",
                StringComparison.Ordinal),
        ];

        foreach (string json in invalidJson)
        {
            AssertFailure(
                () => Parse(fixture, json),
                MediaProbeFailureReason.InvalidMetadata);
        }
    }

    [TestMethod]
    public void ParseProbeOutput_DuplicateStreamIndicesAndBlankFormatTokensAreRejected()
    {
        using ProbeFixture fixture = new();
        string duplicateIndices = CreateProbeJson(
            fixture.Staged.ByteSize,
            [CreateVideoStream(index: 0), CreateAudioStream(index: 0)]);
        string blankFormatToken = CreateProbeJson(fixture.Staged.ByteSize)
            .Replace(
                "mov,mp4,m4a,3gp,3g2,mj2",
                "mov,,mp4",
                StringComparison.Ordinal);

        AssertFailure(
            () => Parse(fixture, duplicateIndices),
            MediaProbeFailureReason.InvalidMetadata);
        AssertFailure(
            () => Parse(fixture, blankFormatToken),
            MediaProbeFailureReason.InvalidMetadata);
    }

    [TestMethod]
    public void DetectContainerSignature_RecognizesOnlyExactEvidence()
    {
        byte[] isoBaseMedia = new byte[8];
        Encoding.ASCII.GetBytes("ftyp").CopyTo(isoBaseMedia, 4);
        byte[] ebml = [0x1a, 0x45, 0xdf, 0xa3];
        byte[] riffAvi = Encoding.ASCII.GetBytes("RIFF\0\0\0\0AVI ");
        byte[] flv = Encoding.ASCII.GetBytes("FLV");
        byte[] transportStream = new byte[377];
        transportStream[0] = 0x47;
        transportStream[188] = 0x47;
        transportStream[376] = 0x47;

        Assert.AreEqual(
            ContainerSignature.IsoBaseMediaFtyp,
            FfprobeClient.DetectContainerSignature(isoBaseMedia, isoBaseMedia.Length));
        Assert.AreEqual(
            ContainerSignature.Ebml,
            FfprobeClient.DetectContainerSignature(ebml, ebml.Length));
        Assert.AreEqual(
            ContainerSignature.RiffAvi,
            FfprobeClient.DetectContainerSignature(riffAvi, riffAvi.Length));
        Assert.AreEqual(
            ContainerSignature.Flv,
            FfprobeClient.DetectContainerSignature(flv, flv.Length));
        Assert.AreEqual(
            ContainerSignature.MpegTransportStream,
            FfprobeClient.DetectContainerSignature(
                transportStream,
                transportStream.LongLength));

        byte[] almostRiffAvi = Encoding.ASCII.GetBytes("RIFF\0\0\0\0AVI0");
        Assert.AreEqual(
            ContainerSignature.Unknown,
            FfprobeClient.DetectContainerSignature(
                almostRiffAvi,
                almostRiffAvi.LongLength));
    }

    [TestMethod]
    public void DetectContainerSignature_ShortOrIncompleteTransportStreamIsUnknown()
    {
        byte[] shortTransportStream = new byte[376];
        shortTransportStream[0] = 0x47;
        shortTransportStream[188] = 0x47;
        byte[] missingThirdSync = new byte[377];
        missingThirdSync[0] = 0x47;
        missingThirdSync[188] = 0x47;
        byte[] sufficientPrefixButShortDeclaredLength = new byte[377];
        sufficientPrefixButShortDeclaredLength[0] = 0x47;
        sufficientPrefixButShortDeclaredLength[188] = 0x47;
        sufficientPrefixButShortDeclaredLength[376] = 0x47;

        Assert.AreEqual(
            ContainerSignature.Unknown,
            FfprobeClient.DetectContainerSignature(
                shortTransportStream,
                shortTransportStream.LongLength));
        Assert.AreEqual(
            ContainerSignature.Unknown,
            FfprobeClient.DetectContainerSignature(
                missingThirdSync,
                missingThirdSync.LongLength));
        Assert.AreEqual(
            ContainerSignature.Unknown,
            FfprobeClient.DetectContainerSignature(
                sufficientPrefixButShortDeclaredLength,
                byteSize: 376));
    }

    [TestMethod]
    public void ConstructorValidatesDependenciesAndPathShapeWithoutRequiringTheFile()
    {
        using ProbeFixture fixture = new();
        ProcessRunner processRunner = new();
        BinaryVerifier binaryVerifier = new();
        string missingAbsolutePath = Path.Combine(
            fixture.RootPath,
            "missing",
            "ffprobe.exe");

        _ = new FfprobeClient(processRunner, binaryVerifier, missingAbsolutePath);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new FfprobeClient(null!, binaryVerifier, missingAbsolutePath));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new FfprobeClient(processRunner, null!, missingAbsolutePath));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new FfprobeClient(processRunner, binaryVerifier, null!));

        string[] invalidPaths =
        [
            string.Empty,
            " ",
            "ffprobe.exe",
            ".\\ffprobe.exe",
            $" {missingAbsolutePath}",
            $"{missingAbsolutePath} ",
            @"\\server\share\ffprobe.exe",
            @"\\.\pipe\ffprobe",
            missingAbsolutePath + ":alternate-stream",
        ];
        foreach (string invalidPath in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                new FfprobeClient(processRunner, binaryVerifier, invalidPath));
            Assert.AreEqual("ffprobePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }
    }

    [TestMethod]
    public void CreateProcessRequestUsesExactArgumentsBoundedOutputAndNoStandardInput()
    {
        using ProbeFixture fixture = new();
        string ffprobePath = Path.Combine(fixture.RootPath, "ffprobe.exe");

        ProcessRunRequest request = FfprobeClient.CreateProcessRequest(
            ffprobePath,
            fixture.Staged.StagingPath,
            TimeSpan.FromSeconds(30));

        Assert.AreEqual(Path.GetFullPath(ffprobePath), request.Executable);
        CollectionAssert.AreEqual(
            new[]
            {
                "-v",
                "error",
                "-show_entries",
                ExpectedShowEntries,
                "-of",
                "json",
                fixture.Staged.StagingPath,
            },
            request.ArgumentList.ToArray());
        Assert.AreEqual(
            Path.GetDirectoryName(fixture.Staged.StagingPath),
            request.WorkingDirectory);
        Assert.AreEqual(TimeSpan.FromSeconds(30), request.Timeout);
        Assert.AreEqual(1024 * 1024, request.MaxCapturedBytes);
        Assert.IsNull(request.StandardInput);
        Assert.AreEqual(fixture.Staged.StagingPath, request.ArgumentList[^1]);
        Assert.IsFalse(request.ArgumentList.Contains("--", StringComparer.Ordinal));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FfprobeClient.CreateProcessRequest(
                ffprobePath,
                fixture.Staged.StagingPath,
                TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            FfprobeClient.CreateProcessRequest(
                ffprobePath,
                fixture.Staged.StagingPath,
                TimeSpan.MaxValue));
    }

    [TestMethod]
    public void ParseProcessResultClassifiesTruncationProcessErrorsAndEmptyOutputSafely()
    {
        using ProbeFixture fixture = new();
        string validJson = CreateProbeJson(fixture.Staged.ByteSize);
        (ProcessRunResult Result, MediaProbeFailureReason Reason)[] failures =
        [
            (new ProcessRunResult(1, validJson, "private stderr", true, TimeSpan.Zero),
                MediaProbeFailureReason.OutputTruncated),
            (new ProcessRunResult(1, validJson, string.Empty, false, TimeSpan.Zero),
                MediaProbeFailureReason.ProcessFailed),
            (new ProcessRunResult(0, validJson, "private stderr", false, TimeSpan.Zero),
                MediaProbeFailureReason.ProcessFailed),
            (new ProcessRunResult(0, string.Empty, string.Empty, false, TimeSpan.Zero),
                MediaProbeFailureReason.ProcessFailed),
            (new ProcessRunResult(0, "  ", string.Empty, false, TimeSpan.Zero),
                MediaProbeFailureReason.ProcessFailed),
        ];

        foreach ((ProcessRunResult result, MediaProbeFailureReason reason) in failures)
        {
            MediaProbeException exception = Assert.ThrowsExactly<MediaProbeException>(() =>
                FfprobeClient.ParseProcessResult(
                    fixture.Staged,
                    ContainerSignature.IsoBaseMediaFtyp,
                    result));
            Assert.AreEqual(reason, exception.Reason);
            AssertDoesNotContain(exception.ToString(), "private stderr");
            AssertDoesNotContain(exception.ToString(), validJson);
        }
    }

    [TestMethod]
    public async Task ProbeAsync_ToolVerificationFailureIsFixedAndOccursBeforeProcessStart()
    {
        using ProbeFixture fixture = new();
        string fakeFfprobePath = fixture.CreateFile("fake-ffprobe.exe", [1, 2, 3]);
        FfprobeClient client = new(
            new ProcessRunner(),
            new BinaryVerifier(),
            fakeFfprobePath);

        MediaProbeException exception =
            await Assert.ThrowsExactlyAsync<MediaProbeException>(() =>
                client.ProbeAsync(
                    fixture.Staged,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));

        Assert.AreEqual(MediaProbeFailureReason.ToolVerificationFailed, exception.Reason);
        Assert.AreEqual("The media probe tool could not be verified.", exception.Message);
        Assert.IsNull(exception.InnerException);
        AssertDoesNotContain(exception.ToString(), fakeFfprobePath);
        AssertDoesNotContain(exception.ToString(), fixture.Staged.StagingPath);
    }

    [TestMethod]
    public async Task ProbeAsync_PreCancelledTokenDoesNotOpenOrVerifyFiles()
    {
        using ProbeFixture fixture = new();
        string missingFfprobePath = Path.Combine(fixture.RootPath, "missing-ffprobe.exe");
        FfprobeClient client = new(
            new ProcessRunner(),
            new BinaryVerifier(),
            missingFfprobePath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                client.ProbeAsync(
                    fixture.Staged,
                    TimeSpan.FromSeconds(1),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public async Task ProbeAsync_InvalidTimeoutIsRejectedBeforeToolVerification()
    {
        using ProbeFixture fixture = new();
        File.Delete(fixture.Staged.StagingPath);
        string missingFfprobePath = Path.Combine(fixture.RootPath, "missing-ffprobe.exe");
        FfprobeClient client = new(
            new ProcessRunner(),
            new BinaryVerifier(),
            missingFfprobePath);

        ArgumentOutOfRangeException exception =
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                client.ProbeAsync(
                    fixture.Staged,
                    TimeSpan.Zero,
                    CancellationToken.None));

        Assert.AreEqual("timeout", exception.ParamName);
    }

    [TestMethod]
    public void ExceptionReasonsMessagesAndPrivacyContractAreStable()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "ToolVerificationFailed",
                "StagedFileInvalid",
                "ProcessTimedOut",
                "ProcessFailed",
                "OutputTruncated",
                "InvalidJson",
                "InvalidMetadata",
                "NoVideoStream",
            },
            Enum.GetNames<MediaProbeFailureReason>());
        Dictionary<MediaProbeFailureReason, string> messages = new()
        {
            [MediaProbeFailureReason.ToolVerificationFailed] =
                "The media probe tool could not be verified.",
            [MediaProbeFailureReason.StagedFileInvalid] =
                "The staged media file is invalid.",
            [MediaProbeFailureReason.ProcessTimedOut] =
                "The media probe process exceeded its timeout.",
            [MediaProbeFailureReason.ProcessFailed] =
                "The media probe process failed.",
            [MediaProbeFailureReason.OutputTruncated] =
                "The media probe output exceeded the capture limit.",
            [MediaProbeFailureReason.InvalidJson] =
                "The media probe output is not valid JSON.",
            [MediaProbeFailureReason.InvalidMetadata] =
                "The media probe metadata is invalid.",
            [MediaProbeFailureReason.NoVideoStream] =
                "The media probe found no video stream.",
        };
        const string privatePath = @"C:\private\customer-secret.mov";
        const string privateJson = "{\"private\":true}";
        const string privateError = "customer-secret decode failure";

        foreach (MediaProbeFailureReason reason in Enum.GetValues<MediaProbeFailureReason>())
        {
            var exception = new MediaProbeException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.AreEqual(messages[reason], exception.Message);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privatePath);
            AssertDoesNotContain(exception.ToString(), privateJson);
            AssertDoesNotContain(exception.ToString(), privateError);
        }

        PropertyInfo[] properties = typeof(MediaProbeException)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEqual(
            new[] { "Reason" },
            properties.Select(static property => property.Name).ToArray());
        Assert.IsNull(properties.Single().SetMethod);
    }

    [TestMethod]
    public void PublicProbeContractsAreSealedImmutableAndDefensivelyReadOnly()
    {
        using ProbeFixture fixture = new();
        MediaProbeResult result = Parse(
            fixture,
            CreateProbeJson(fixture.Staged.ByteSize));

        foreach (Type type in new[]
        {
            typeof(ProbedMediaStream),
            typeof(MediaProbeResult),
            typeof(MediaProbeException),
            typeof(FfprobeClient),
        })
        {
            Assert.IsTrue(type.IsPublic);
            Assert.IsTrue(type.IsSealed);
        }

        Assert.IsTrue(typeof(ProbedMediaStream)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .All(static property => property.SetMethod is null));
        Assert.IsTrue(typeof(MediaProbeResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .All(static property => property.SetMethod is null));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)result.FormatNames).Add("mutation"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ProbedMediaStream>)result.Streams).Add(result.Streams[0]));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)result.Warnings).Add("audio-track-absent"));
    }

    [TestMethod]
    public void ProbedMediaStreamConstructorRejectsInconsistentOrNonpositiveMetadata()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CreateModelStream(width: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CreateModelStream(height: 0));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ProbedMediaStream(
                0,
                MediaStreamKind.Video,
                "h264",
                null,
                1920,
                1080,
                "yuv420p",
                null,
                new RationalNumber(30, 1),
                new RationalNumber(1, 1000),
                0m,
                1m,
                null,
                null,
                null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ProbedMediaStream(
                0,
                MediaStreamKind.Video,
                "h264",
                null,
                1920,
                1080,
                "yuv420p",
                new RationalNumber(30, 1),
                new RationalNumber(30, 1),
                default,
                0m,
                1m,
                null,
                null,
                null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CreateModelStream(durationSeconds: -0.01m));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ProbedMediaStream(
                0,
                MediaStreamKind.Audio,
                "aac",
                null,
                1920,
                null,
                null,
                null,
                null,
                new RationalNumber(1, 48000),
                null,
                null,
                48000,
                2,
                "stereo"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ProbedMediaStream(
                0,
                MediaStreamKind.Audio,
                "aac",
                null,
                null,
                null,
                null,
                null,
                null,
                new RationalNumber(1, 48000),
                null,
                null,
                0,
                2,
                "stereo"));
    }

    private static ProbedMediaStream CreateModelStream(
        int? width = 1920,
        int? height = 1080,
        decimal? durationSeconds = 1m) =>
        new(
            0,
            MediaStreamKind.Video,
            "h264",
            null,
            width,
            height,
            "yuv420p",
            new RationalNumber(30, 1),
            new RationalNumber(30, 1),
            new RationalNumber(1, 1000),
            0m,
            durationSeconds,
            null,
            null,
            null);

    private static MediaProbeResult Parse(
        ProbeFixture fixture,
        string json,
        ContainerSignature signature = ContainerSignature.IsoBaseMediaFtyp) =>
        FfprobeClient.ParseProbeOutput(fixture.Staged, signature, json);

    private static void AssertFailure(
        Action action,
        MediaProbeFailureReason expectedReason)
    {
        MediaProbeException exception = Assert.ThrowsExactly<MediaProbeException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (string.IsNullOrWhiteSpace(forbiddenValue))
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateProbeJson(
        long byteSize,
        IReadOnlyList<string>? streams = null,
        string? formatExtra = null,
        string? rootExtra = null,
        bool includeCanonicalEnvelopes = false)
    {
        streams ??= [CreateVideoStream(), CreateAudioStream()];
        string envelopePrefix = includeCanonicalEnvelopes
            ? "  \"programs\": [],\n  \"stream_groups\": [],\n"
            : string.Empty;
        string formatSuffix = formatExtra is null
            ? string.Empty
            : $",\n    {formatExtra}";
        string rootSuffix = rootExtra is null
            ? string.Empty
            : $",\n  {rootExtra}";

        return $$"""
            {
            {{envelopePrefix}}  "format": {
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "format_long_name": "QuickTime / MOV",
                "duration": "1081.42",
                "size": "{{byteSize}}",
                "bit_rate": "48044773",
                "probe_score": 100{{formatSuffix}}
              },
              "streams": [
            {{string.Join(",\n", streams)}}
              ]{{rootSuffix}}
            }
            """;
    }

    private static string CreateVideoStream(
        int index = 0,
        string nominalFrameRate = "60000/1001",
        string averageFrameRate = "60000/1001") =>
        $$"""
                {
                  "index": {{index}},
                  "codec_name": "hevc",
                  "profile": "Main",
                  "codec_type": "video",
                  "width": 3840,
                  "height": 2160,
                  "pix_fmt": "yuv420p",
                  "r_frame_rate": "{{nominalFrameRate}}",
                  "avg_frame_rate": "{{averageFrameRate}}",
                  "time_base": "1/600",
                  "start_time": "-0.083333",
                  "duration": "1081.401667"
                }
            """;

    private static string CreateAudioStream(int index = 1) =>
        $$"""
                {
                  "index": {{index}},
                  "codec_name": "aac",
                  "profile": "LC",
                  "codec_type": "audio",
                  "sample_rate": "48000",
                  "channels": 2,
                  "channel_layout": "stereo",
                  "r_frame_rate": "0/0",
                  "avg_frame_rate": "0/0",
                  "time_base": "1/48000",
                  "start_time": "-0.044",
                  "duration": "1081.42"
                }
            """;

    private sealed class ProbeFixture : IDisposable
    {
        internal ProbeFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.FfprobeClientTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            byte[] content = new byte[32];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(content, 4);
            string stagedPath = CreateFile("candidate.ready", content);
            Staged = new StagedMediaImport(
                EntityId<AssetIdTag>.New(),
                EntityId<ProjectIdTag>.New(),
                EntityId<JobIdTag>.New(),
                "meeting-source.mov",
                stagedPath,
                ContentDigest.Compute(content),
                content.LongLength,
                CreatedAt);
        }

        internal string RootPath { get; }

        internal StagedMediaImport Staged { get; }

        internal string CreateFile(string name, byte[] content)
        {
            string path = Path.Combine(RootPath, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}