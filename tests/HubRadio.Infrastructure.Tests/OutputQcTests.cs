using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class OutputQcTests
{
    private const string ProbeFixture =
        "tests/Fixtures/output-qc-probe-control.json";
    private const string VideoTimingFixture =
        "tests/Fixtures/output-qc-video-timing.compact";
    private const string AudioTimingFixture =
        "tests/Fixtures/output-qc-audio-timing.compact";
    private const string SubtitleTimingFixture =
        "tests/Fixtures/output-qc-subtitle-timing.compact";
    private const string LumaFixture =
        "tests/Fixtures/output-qc-luma-control.txt";
    private const string LoudnessFixture =
        "tests/Fixtures/output-qc-loudness-control.txt";

    [TestMethod]
    public async Task RunAsync_ValidStructureReturnsThresholdlessBoundMeasurementsForHumanReview()
    {
        using Fixture fixture = new(includeSubtitle: true);
        OutputQc qc = fixture.CreateQc();

        OutputQcResult result = await qc.RunAsync(fixture.Request());

        Assert.AreEqual(fixture.Artifact.Id, result.ArtifactId);
        Assert.AreEqual(fixture.Project.Id, result.ProjectId);
        Assert.AreEqual(fixture.Artifact.ContentDigest, result.ContentDigest);
        Assert.AreEqual(fixture.Artifact.ByteSize, result.ByteSize);
        Assert.AreEqual(fixture.Manifest.Id, result.RenderManifestId);
        Assert.AreEqual(fixture.Graph.Id, result.RenderGraphId);
        Assert.AreEqual(fixture.Composition.Id, result.CompositionVersionId);
        Assert.AreEqual(fixture.Profile.Id, result.ProfileId);
        Assert.AreEqual(fixture.Layout.Id, result.LayoutSpecId);
        Assert.AreEqual(fixture.Template.Id, result.TemplateId);
        Assert.AreEqual(
            fixture.SubtitleEvidence!.LayoutProfileDigest,
            result.SubtitleLayoutProfileDigest);
        Assert.AreEqual(
            fixture.SubtitleEvidence.DisplayProfileDigest,
            result.SubtitleDisplayProfileDigest);
        Assert.AreEqual(0, result.VideoStreamIndex);
        Assert.AreEqual(1, result.AudioStreamIndex);
        Assert.IsNull(result.SubtitleStreamIndex);

        Assert.AreEqual(3, result.VideoTiming.ObservedFrameCount);
        Assert.AreEqual(new RationalTime(1, 30000), result.VideoTiming.TimeBase);
        Assert.AreEqual(default, result.VideoTiming.PresentationStart);
        Assert.AreEqual(new RationalTime(1001, 10000), result.VideoTiming.PresentationEnd);
        Assert.AreEqual(new RationalTime(1001, 10000), result.VideoTiming.ObservedDuration);
        CollectionAssert.AreEqual(
            new long[] { 0, 1001, 2002 },
            result.VideoTiming.Entries
                .Select(static entry => entry.PresentationTimestampTicks)
                .ToArray());
        CollectionAssert.AreEqual(
            new long[] { 1001, 1001, 1001 },
            result.VideoTiming.Entries
                .Select(static entry => entry.DurationTicks)
                .ToArray());
        Assert.IsNotNull(result.AudioTiming);
        Assert.AreEqual(new RationalTime(8, 125), result.AudioTiming.ObservedDuration);

        Assert.IsTrue(
            result.DurationMeasurements.ExpectedDuration == new RationalTime(4, 1));
        Assert.AreEqual(
            new RationalTime(-38999, 10000),
            result.DurationMeasurements.VideoDurationDelta);
        Assert.AreEqual(
            new RationalTime(-492, 125),
            result.DurationMeasurements.AudioDurationDelta);
        Assert.AreEqual(default(RationalTime), result.DurationMeasurements.AudioVideoStartOffset);
        Assert.AreEqual(
            new RationalTime(-361, 10000),
            result.DurationMeasurements.AudioVideoEndOffset);

        Assert.IsTrue(result.Decode.DecodedSuccessfully);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), result.Decode.Elapsed);
        Assert.AreEqual(3, result.LumaMeasurements.MeasuredFrameCount);
        Assert.AreEqual(16m, result.LumaMeasurements.MinimumFrameAverage);
        Assert.AreEqual(235m, result.LumaMeasurements.MaximumFrameAverage);
        Assert.AreEqual(291.5m / 3m, result.LumaMeasurements.MeanFrameAverage);
        CollectionAssert.AreEqual(
            new long[] { 0, 1001, 2002 },
            result.LumaMeasurements.Frames
                .Select(static frame => frame.PresentationTimestampTicks)
                .ToArray());
        CollectionAssert.AreEqual(
            new decimal[] { 16m, 40.5m, 235m },
            result.LumaMeasurements.Frames
                .Select(static frame => frame.FrameAverage)
                .ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<OutputQcTimingEntry>)result.VideoTiming.Entries).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<OutputQcLumaFrameMeasurement>)result.LumaMeasurements.Frames)
                .Clear());
        Assert.IsNotNull(result.AudioMeasurements);
        Assert.AreEqual(
            OutputQcMeasuredValueKind.Finite,
            result.AudioMeasurements.IntegratedLoudnessLufs.Kind);
        Assert.AreEqual(
            -21.05m,
            result.AudioMeasurements.IntegratedLoudnessLufs.FiniteValue);
        Assert.AreEqual(-18.06m, result.AudioMeasurements.TruePeakDbfs.FiniteValue);

        Assert.IsTrue(result.MachineChecksPassed);
        Assert.IsTrue(result.HumanReviewRequired);
        Assert.IsFalse(result.ApprovedNumericThresholdsApplied);
        Assert.IsFalse(result.CanProceedToDelivery);
        CollectionAssert.AreEqual(
            new[]
            {
                OutputQcReviewArea.Duration,
                OutputQcReviewArea.AudioVideoSynchronization,
                OutputQcReviewArea.SubtitlePresentation,
                OutputQcReviewArea.ScreenLuma,
                OutputQcReviewArea.ProgramLoudness,
                OutputQcReviewArea.TruePeak,
            },
            result.RequiredHumanReviewAreas.ToArray());
        Assert.AreEqual(
            OutputQcRemediationKind.TechnicalRerender,
            result.RemediationPolicy.TechnicalCorrection);
        Assert.IsTrue(result.RemediationPolicy.TechnicalRerenderPreservesCompositionVersion);
        Assert.IsFalse(result.RemediationPolicy.TechnicalRerenderRequiresRenewedApproval);
        Assert.AreEqual(
            OutputQcRemediationKind.ContentRevisionAndReapproval,
            result.RemediationPolicy.ContentCorrection);
        Assert.IsTrue(result.RemediationPolicy.ContentCorrectionRequiresChildCompositionVersion);
        Assert.IsTrue(result.RemediationPolicy.ContentCorrectionRequiresRenewedApproval);
        CollectionAssert.AreEqual(
            new[] { "ffprobe", "ffmpeg" },
            result.Tools.Select(static tool => tool.Name).ToArray());

        Assert.AreEqual(2, fixture.ArtifactValidationCalls);
        Assert.AreEqual(2, fixture.ToolVerificationCalls);
        Assert.AreEqual(6, fixture.ProcessRequests.Count);
        Assert.IsTrue(fixture.ProcessRequests.All(static request =>
            request.StandardInput is null));
        Assert.IsFalse(fixture.ProcessRequests.SelectMany(static request => request.ArgumentList)
            .Any(static argument =>
                argument.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("powershell", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void FixedProcessRequestsUseAtomicAllowlistedArgumentsAndBoundedCapture()
    {
        using Fixture fixture = new();
        TimeSpan timeout = TimeSpan.FromSeconds(9);

        ProcessRunRequest timing = OutputQc.CreateTimingProcessRequest(
            fixture.FfprobePath,
            fixture.ArtifactPath,
            streamIndex: 7,
            timeout);
        CollectionAssert.AreEqual(
            new[]
            {
                "-v",
                "error",
                "-select_streams",
                "7",
                "-show_frames",
                "-show_entries",
                "frame=stream_index,best_effort_timestamp,duration",
                "-of",
                "compact=p=0:nk=0:escape=none:s=|",
                fixture.ArtifactPath,
            },
            timing.ArgumentList.ToArray());
        Assert.AreEqual(ProcessRunRequest.MaximumMaxCapturedBytes, timing.MaxCapturedBytes);

        ProcessRunRequest subtitleTiming =
            OutputQc.CreateSubtitleTimingProcessRequest(
                fixture.FfprobePath,
                fixture.ArtifactPath,
                streamIndex: 8,
                timeout);
        CollectionAssert.AreEqual(
            new[]
            {
                "-v",
                "error",
                "-select_streams",
                "8",
                "-show_frames",
                "-show_entries",
                "frame=pts,start_display_time,end_display_time,num_rects",
                "-of",
                "compact=p=0:nk=0:escape=none:s=|",
                fixture.ArtifactPath,
            },
            subtitleTiming.ArgumentList.ToArray());
        Assert.AreEqual(
            ProcessRunRequest.MaximumMaxCapturedBytes,
            subtitleTiming.MaxCapturedBytes);

        ProcessRunRequest luma = OutputQc.CreateLumaProcessRequest(
            fixture.FfmpegPath,
            fixture.ArtifactPath,
            videoStreamIndex: 3,
            timeout);
        CollectionAssert.Contains(luma.ArgumentList.ToArray(), "0:3");
        CollectionAssert.Contains(luma.ArgumentList.ToArray(), OutputQc.LumaFilter);
        CollectionAssert.DoesNotContain(luma.ArgumentList.ToArray(), "-filter_complex");
        Assert.AreEqual(ProcessRunRequest.MaximumMaxCapturedBytes, luma.MaxCapturedBytes);

        ProcessRunRequest loudness = OutputQc.CreateLoudnessProcessRequest(
            fixture.FfmpegPath,
            fixture.ArtifactPath,
            audioStreamIndex: 5,
            timeout);
        CollectionAssert.Contains(loudness.ArgumentList.ToArray(), "0:5");
        CollectionAssert.Contains(loudness.ArgumentList.ToArray(), OutputQc.LoudnessFilter);
        CollectionAssert.DoesNotContain(loudness.ArgumentList.ToArray(), "-filter_complex");
        Assert.AreEqual(ProcessRunRequest.MaximumMaxCapturedBytes, loudness.MaxCapturedBytes);

        ProcessRunRequest decode = OutputQc.CreateDecodeProcessRequest(
            fixture.FfmpegPath,
            fixture.ArtifactPath,
            videoStreamIndex: 3,
            audioStreamIndex: 5,
            timeout);
        CollectionAssert.Contains(decode.ArgumentList.ToArray(), "-xerror");
        CollectionAssert.Contains(decode.ArgumentList.ToArray(), "0:3");
        CollectionAssert.Contains(decode.ArgumentList.ToArray(), "0:5");

        foreach (ProcessRunRequest request in new[]
        {
            timing,
            subtitleTiming,
            luma,
            loudness,
            decode,
        })
        {
            Assert.AreEqual(fixture.ArtifactPath, request.ArgumentList.Single(argument =>
                string.Equals(argument, fixture.ArtifactPath, StringComparison.Ordinal)));
            Assert.AreEqual(fixture.RootPath, request.WorkingDirectory);
            Assert.AreEqual(timeout, request.Timeout);
            Assert.IsNull(request.StandardInput);
        }

        Assert.IsFalse(typeof(OutputQcRequest).GetProperties().Any(static property =>
            property.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Tolerance", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InputContractsRejectNullTimeoutAndDuplicateSubtitleEvidence()
    {
        using Fixture fixture = new(includeSubtitle: true);
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutputQcRequest(
            null!,
            fixture.Manifest,
            fixture.Graph,
            fixture.ValidationContext,
            fixture.SubtitleEvidence,
            TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutputQcRequest(
            fixture.Artifact.Id,
            null!,
            fixture.Graph,
            fixture.ValidationContext,
            fixture.SubtitleEvidence,
            TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutputQcRequest(
            fixture.Artifact.Id,
            fixture.Manifest,
            fixture.Graph,
            fixture.ValidationContext,
            fixture.SubtitleEvidence,
            TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutputQcRequest(
            fixture.Artifact.Id,
            fixture.Manifest,
            fixture.Graph,
            fixture.ValidationContext,
            fixture.SubtitleEvidence,
            ProcessRunRequest.MaximumTimeout + TimeSpan.FromMilliseconds(1)));

        OutputQcSubtitleLayoutEvidence layout = fixture.SubtitleEvidence!.Layouts.Single();
        Assert.ThrowsExactly<ArgumentException>(() => new OutputQcSubtitleEvidence(
            fixture.SubtitleEvidence.LayoutProfileDigest,
            fixture.SubtitleEvidence.DisplayProfileDigest,
            fixture.SubtitleEvidence.DisplayResult,
            [layout, layout]));
        Assert.ThrowsExactly<ArgumentException>(() => new OutputQcSubtitleEvidence(
            fixture.SubtitleEvidence.LayoutProfileDigest,
            fixture.SubtitleEvidence.DisplayProfileDigest,
            fixture.SubtitleEvidence.DisplayResult,
            [null!]));

        var mutable = new List<OutputQcSubtitleLayoutEvidence> { layout };
        var copied = new OutputQcSubtitleEvidence(
            fixture.SubtitleEvidence.LayoutProfileDigest,
            fixture.SubtitleEvidence.DisplayProfileDigest,
            fixture.SubtitleEvidence.DisplayResult,
            mutable);
        mutable.Clear();
        Assert.HasCount(1, copied.Layouts);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<OutputQcSubtitleLayoutEvidence>)copied.Layouts).Clear());
    }

    [TestMethod]
    public void TimingParserPreservesObservedRationalPtsAndRejectsMalformedOrNonmonotonicEvidence()
    {
        string valid = ReadFixture(VideoTimingFixture);
        ProbedMediaStream stream = CreateVideoStream();

        OutputQcStreamTiming timing = OutputQc.ParseTimingOutput(
            stream,
            valid,
            OutputQcStage.VideoTiming);

        Assert.AreEqual(3, timing.ObservedFrameCount);
        Assert.AreEqual(default, timing.PresentationStart);
        Assert.AreEqual(new RationalTime(1001, 10000), timing.PresentationEnd);
        Assert.AreEqual(
            ContentDigest.Compute(new UTF8Encoding(false, true).GetBytes(valid)),
            timing.EvidenceDigest);

        (string Output, OutputQcFailureReason Reason)[] failures =
        [
            (string.Empty, OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=0|duration=1001", OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=0|best_effort_timestamp=N/A|duration=1001", OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=0|best_effort_timestamp=0|duration=0", OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=00|best_effort_timestamp=0|duration=1001", OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=1|best_effort_timestamp=0|duration=1001", OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=0|best_effort_timestamp=0|duration=1001|duration=1001", OutputQcFailureReason.TimingOutputInvalid),
            ("stream_index=0|best_effort_timestamp=0|duration=1001|private=secret", OutputQcFailureReason.TimingOutputInvalid),
            (
                "stream_index=0|best_effort_timestamp=0|duration=1001\n" +
                "stream_index=0|best_effort_timestamp=0|duration=1001",
                OutputQcFailureReason.NonMonotonicTiming),
            (
                "stream_index=0|best_effort_timestamp=1|duration=1001\n" +
                "stream_index=0|best_effort_timestamp=0|duration=1001",
                OutputQcFailureReason.NonMonotonicTiming),
            (
                "stream_index=0|best_effort_timestamp=0|duration=2000\n" +
                "stream_index=0|best_effort_timestamp=1001|duration=1001",
                OutputQcFailureReason.NonMonotonicTiming),
        ];
        foreach ((string output, OutputQcFailureReason reason) in failures)
        {
            OutputQcException exception = AssertQcFailure(
                () => OutputQc.ParseTimingOutput(
                    stream,
                    output,
                    OutputQcStage.VideoTiming),
                reason,
                OutputQcStage.VideoTiming);
            AssertDoesNotContain(exception.ToString(), output);
        }

        AssertQcFailure(
            () => OutputQc.ParseTimingProcessResult(
                stream,
                new ProcessRunResult(
                    0,
                    valid,
                    "private stderr",
                    false,
                    TimeSpan.Zero),
                OutputQcStage.VideoTiming),
            OutputQcFailureReason.ProcessFailed,
            OutputQcStage.VideoTiming);
        AssertQcFailure(
            () => OutputQc.ParseTimingProcessResult(
                stream,
                new ProcessRunResult(0, valid, string.Empty, true, TimeSpan.Zero),
                OutputQcStage.VideoTiming),
            OutputQcFailureReason.OutputTruncated,
            OutputQcStage.VideoTiming);
        AssertQcFailure(
            () => OutputQc.ParseTimingOutput(
                stream,
                valid + "\uFFFD",
                OutputQcStage.VideoTiming),
            OutputQcFailureReason.InvalidEncoding,
            OutputQcStage.VideoTiming);
    }

    [TestMethod]
    public void LumaAndLoudnessParsersAcceptMeasuredValuesWithoutApplyingQualityThresholds()
    {
        string lumaText = ReadFixture(LumaFixture);
        OutputQcLumaMeasurements luma = OutputQc.ParseLumaOutput(lumaText);
        Assert.AreEqual(3, luma.MeasuredFrameCount);
        Assert.AreEqual(16m, luma.MinimumFrameAverage);
        Assert.AreEqual(235m, luma.MaximumFrameAverage);
        Assert.AreEqual(291.5m / 3m, luma.MeanFrameAverage);

        foreach (string invalid in new[]
        {
            "frame:0    pts:0       pts_time:0",
            "frame:1    pts:0       pts_time:0\nlavfi.signalstats.YAVG=16",
            "frame:0    pts:0       pts_time:0\nlavfi.signalstats.YAVG=256",
            "frame:0    pts:0       pts_time:0\nlavfi.signalstats.YAVG=16,0",
            "frame:0    pts:0       pts_time:0\nlavfi.signalstats.YAVG=016",
            "frame:0    pts_time:0  pts:0\nlavfi.signalstats.YAVG=16",
            "frame:0    pts:0       pts_time:00.0\nlavfi.signalstats.YAVG=16",
            "frame:0    pts:0       pts_time:0 extra:value\nlavfi.signalstats.YAVG=16",
            "frame:0    pts:1       pts_time:0.1\nlavfi.signalstats.YAVG=16\n" +
                "frame:1    pts:0       pts_time:0\nlavfi.signalstats.YAVG=16",
            "frame:0    pts:0       pts_time:0\nprivate.path=value",
        })
        {
            OutputQcException exception = AssertQcFailure(
                () => OutputQc.ParseLumaOutput(invalid),
                OutputQcFailureReason.LumaOutputInvalid,
                OutputQcStage.ScreenLuma);
            AssertDoesNotContain(exception.ToString(), invalid);
        }

        string loudnessText = ReadFixture(LoudnessFixture);
        OutputQcAudioMeasurements loudness = OutputQc.ParseLoudnessOutput(loudnessText);
        Assert.AreEqual(-21.05m, loudness.IntegratedLoudnessLufs.FiniteValue);
        Assert.AreEqual(-18.06m, loudness.TruePeakDbfs.FiniteValue);

        OutputQcAudioMeasurements silence = OutputQc.ParseLoudnessOutput(
            loudnessText
                .Replace("\"-21.05\"", "\"-inf\"", StringComparison.Ordinal)
                .Replace("\"-18.06\"", "\"-inf\"", StringComparison.Ordinal));
        Assert.AreEqual(
            OutputQcMeasuredValueKind.NegativeInfinity,
            silence.IntegratedLoudnessLufs.Kind);
        Assert.IsNull(silence.IntegratedLoudnessLufs.FiniteValue);
        Assert.AreEqual(
            OutputQcMeasuredValueKind.NegativeInfinity,
            silence.TruePeakDbfs.Kind);

        foreach (string invalid in new[]
        {
            "no json summary",
            loudnessText.Replace("\"input_i\"", "\"unknown\"", StringComparison.Ordinal),
            loudnessText.Replace("\"input_i\" : \"-21.05\",", "", StringComparison.Ordinal),
            loudnessText.Replace("\"-21.05\"", "\"NaN\"", StringComparison.Ordinal),
            loudnessText.Replace("\"-21.05\"", "\"-021.05\"", StringComparison.Ordinal),
            loudnessText.Replace("\"-21.05\"", "\"021.05\"", StringComparison.Ordinal),
            loudnessText.Replace("\"input_lra\" : \"0.00\"", "\"input_lra\" : \"private\"", StringComparison.Ordinal),
            loudnessText.Replace("\"normalization_type\" : \"linear\"", "\"normalization_type\" : \"forged\"", StringComparison.Ordinal),
        })
        {
            OutputQcException exception = AssertQcFailure(
                () => OutputQc.ParseLoudnessOutput(invalid),
                OutputQcFailureReason.LoudnessOutputInvalid,
                OutputQcStage.AudioLoudness);
            AssertDoesNotContain(exception.ToString(), "private-customer-output.mp4");
        }

        AssertQcFailure(
            () => OutputQc.ParseLoudnessProcessResult(new ProcessRunResult(
                0,
                string.Empty,
                loudnessText,
                true,
                TimeSpan.Zero)),
            OutputQcFailureReason.OutputTruncated,
            OutputQcStage.AudioLoudness);
        AssertQcFailure(
            () => OutputQc.ParseLumaOutput(lumaText + "\uFFFD"),
            OutputQcFailureReason.InvalidEncoding,
            OutputQcStage.ScreenLuma);
        AssertQcFailure(
            () => OutputQc.ParseLoudnessOutput(loudnessText + "\uFFFD"),
            OutputQcFailureReason.InvalidEncoding,
            OutputQcStage.AudioLoudness);
    }

    [TestMethod]
    public async Task RunAsync_SidecarSubtitleRequiresExactObservedCueTiming()
    {
        using (Fixture fixture = new(
            includeSubtitle: true,
            subtitleBurnIn: false))
        {
            OutputQcResult result = await fixture.CreateQc().RunAsync(
                fixture.Request());

            Assert.AreEqual(2, result.SubtitleStreamIndex);
            Assert.IsNotNull(result.SubtitleTiming);
            Assert.AreEqual(2, result.SubtitleTiming.StreamIndex);
            Assert.AreEqual(1, result.SubtitleTiming.ObservedCueCount);
            CollectionAssert.AreEqual(
                new[] { Range(1, 2) },
                result.SubtitleTiming.CueRanges.ToArray());
            Assert.AreEqual(
                ContentDigest.Compute(
                    new UTF8Encoding(false, true).GetBytes(
                        ReadFixture(SubtitleTimingFixture))),
                result.SubtitleTiming.EvidenceDigest);
            Assert.AreEqual(7, fixture.ProcessRequests.Count);
            Assert.IsTrue(fixture.ProcessRequests.Any(
                fixture.IsSubtitleTimingRequest));
        }

        using Fixture drift = new(
            includeSubtitle: true,
            subtitleBurnIn: false);
        OutputQc qc = drift.CreateQc(runProcessAsync: (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            ProcessRunResult result = drift.DefaultProcessResult(request);
            if (drift.IsSubtitleTimingRequest(request))
            {
                result = result with
                {
                    StandardOutput = result.StandardOutput.Replace(
                        "end_display_time=2000",
                        "end_display_time=1999",
                        StringComparison.Ordinal),
                };
            }

            return Task.FromResult(result);
        });
        OutputQcException exception = await AssertQcFailureAsync(
            () => qc.RunAsync(drift.Request()),
            OutputQcFailureReason.TimingOutputInvalid,
            OutputQcStage.SubtitleTiming,
            OutputQcRemediationKind.TechnicalRerender);
        AssertDoesNotContain(exception.ToString(), drift.ArtifactPath);
    }

    [TestMethod]
    public async Task RunAsync_RejectsMissingAudioAndLumaTimingDrift()
    {
        using (Fixture missingAudio = new())
        {
            OutputQc qc = missingAudio.CreateQc(runProcessAsync: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                ProcessRunResult result = missingAudio.DefaultProcessResult(request);
                if (missingAudio.IsProbeRequest(request))
                {
                    result = result with { StandardOutput = ProbeWithoutAudio() };
                }

                return Task.FromResult(result);
            });

            OutputQcException exception =
                await Assert.ThrowsExactlyAsync<OutputQcException>(() =>
                    qc.RunAsync(missingAudio.Request()));
            Assert.AreEqual(OutputQcFailureReason.StreamTopologyMismatch, exception.Reason);
            Assert.AreEqual(OutputQcStage.Probe, exception.Stage);
            Assert.AreEqual(
                OutputQcRemediationKind.TechnicalRerender,
                exception.Remediation);
        }

        using (Fixture timingDrift = new())
        {
            OutputQc qc = timingDrift.CreateQc(runProcessAsync: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                ProcessRunResult result = timingDrift.DefaultProcessResult(request);
                if (request.ArgumentList.Contains(
                    OutputQc.LumaFilter,
                    StringComparer.Ordinal))
                {
                    result = result with
                    {
                        StandardOutput = result.StandardOutput.Replace(
                            "frame:1    pts:1001",
                            "frame:1    pts:1002",
                            StringComparison.Ordinal),
                    };
                }

                return Task.FromResult(result);
            });

            OutputQcException exception =
                await Assert.ThrowsExactlyAsync<OutputQcException>(() =>
                    qc.RunAsync(timingDrift.Request()));
            Assert.AreEqual(OutputQcFailureReason.LumaOutputInvalid, exception.Reason);
            Assert.AreEqual(OutputQcStage.ScreenLuma, exception.Stage);
            Assert.AreEqual(
                OutputQcRemediationKind.TechnicalRerender,
                exception.Remediation);
        }
    }

    [TestMethod]
    public void EvidenceValidationRejectsGraphManifestAndSubtitleDriftBeforeFileOrProcessWork()
    {
        using Fixture fixture = new(includeSubtitle: true);

        RenderManifest staleToolManifest = fixture.CreateManifest(
            toolVersion: "unapproved-version");
        AssertQcFailure(
            () => OutputQc.ValidateEvidence(
                fixture.Request(renderManifest: staleToolManifest),
                CancellationToken.None),
            OutputQcFailureReason.ManifestBindingMismatch,
            OutputQcStage.Evidence,
            OutputQcRemediationKind.TechnicalRerender);

        RenderProfile staleProfile = new(
            Id<RenderProfileIdTag>(0x7a000010, 99),
            fixture.Profile.Name,
            Digest('0'),
            fixture.Profile.Width,
            fixture.Profile.Height,
            fixture.Profile.FrameRateNumerator,
            fixture.Profile.FrameRateDenominator,
            fixture.Profile.LayoutSpecId,
            fixture.Profile.TemplateId,
            fixture.Profile.TemplateVersionDigest,
            fixture.Profile.VideoCodec,
            fixture.Profile.AudioCodec,
            fixture.Profile.SubtitleBurnIn);
        AssertQcFailure(
            () => OutputQc.ValidateEvidence(
                fixture.Request(validationContext: fixture.CreateContext(staleProfile)),
                CancellationToken.None),
            OutputQcFailureReason.GraphInvalid,
            OutputQcStage.Evidence,
            OutputQcRemediationKind.ContentRevisionAndReapproval);

        AssertQcFailure(
            () => OutputQc.ValidateEvidence(
                fixture.RequestWithoutSubtitleEvidence(),
                CancellationToken.None),
            OutputQcFailureReason.SubtitleEvidenceInvalid,
            OutputQcStage.Evidence,
            OutputQcRemediationKind.ContentRevisionAndReapproval);

        OutputQcSubtitleEvidence staleLayout = new(
            Digest('0'),
            fixture.SubtitleEvidence!.DisplayProfileDigest,
            fixture.SubtitleEvidence.DisplayResult,
            fixture.SubtitleEvidence.Layouts);
        AssertQcFailure(
            () => OutputQc.ValidateEvidence(
                fixture.Request(subtitleEvidence: staleLayout),
                CancellationToken.None),
            OutputQcFailureReason.SubtitleEvidenceInvalid,
            OutputQcStage.Evidence,
            OutputQcRemediationKind.ContentRevisionAndReapproval);

        AssertQcFailure(
            () => OutputQc.ValidateEvidence(
                fixture.Request(
                    subtitleEvidence: fixture.CreateSubtitleEvidenceWithOmittedCue()),
                CancellationToken.None),
            OutputQcFailureReason.SubtitleEvidenceInvalid,
            OutputQcStage.Evidence,
            OutputQcRemediationKind.ContentRevisionAndReapproval);

        Assert.AreEqual(0, fixture.ArtifactValidationCalls);
        Assert.AreEqual(0, fixture.ToolVerificationCalls);
        Assert.AreEqual(0, fixture.ProcessRequests.Count);
    }

    [TestMethod]
    public async Task RunAsync_ContainerAndProfileFailuresRequireTechnicalRerenderWithoutRawLeakage()
    {
        using (Fixture invalidContainer = new(validMp4Signature: false))
        {
            OutputQcException exception = await AssertQcFailureAsync(
                () => invalidContainer.CreateQc().RunAsync(invalidContainer.Request()),
                OutputQcFailureReason.ContainerMismatch,
                OutputQcStage.Probe,
                OutputQcRemediationKind.TechnicalRerender);
            AssertDoesNotContain(exception.ToString(), invalidContainer.ArtifactPath);
        }

        using Fixture invalidCodec = new();
        OutputQc qc = invalidCodec.CreateQc(runProcessAsync: (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            ProcessRunResult result = invalidCodec.DefaultProcessResult(request);
            if (invalidCodec.IsProbeRequest(request))
            {
                result = result with
                {
                    StandardOutput = result.StandardOutput.Replace(
                        "\"codec_name\": \"h264\"",
                        "\"codec_name\": \"hevc\"",
                        StringComparison.Ordinal),
                };
            }

            return Task.FromResult(result);
        });
        OutputQcException codecException = await AssertQcFailureAsync(
            () => qc.RunAsync(invalidCodec.Request()),
            OutputQcFailureReason.StreamMetadataMismatch,
            OutputQcStage.Probe,
            OutputQcRemediationKind.TechnicalRerender);
        AssertDoesNotContain(codecException.ToString(), invalidCodec.ArtifactPath);
        AssertDoesNotContain(codecException.ToString(), "hevc");

        using Fixture invalidProcessResult = new();
        OutputQc invalidProcessQc = invalidProcessResult.CreateQc(
            runProcessAsync: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                ProcessRunResult result = invalidProcessResult.DefaultProcessResult(request);
                return Task.FromResult(invalidProcessResult.IsProbeRequest(request)
                    ? result with { Duration = TimeSpan.FromTicks(-1) }
                    : result);
            });
        OutputQcException processException = await AssertQcFailureAsync(
            () => invalidProcessQc.RunAsync(invalidProcessResult.Request()),
            OutputQcFailureReason.ProcessFailed,
            OutputQcStage.Probe,
            OutputQcRemediationKind.RepeatQualityControl);
        AssertDoesNotContain(processException.ToString(), invalidProcessResult.ArtifactPath);
    }

    [TestMethod]
    public async Task RunAsync_MapsTimeoutVerificationArtifactFailureAndPreservesCancellation()
    {
        const string privateMarker = @"C:\private\customer-output.mp4";

        using (Fixture timeoutFixture = new())
        {
            OutputQc qc = timeoutFixture.CreateQc(runProcessAsync: (request, _) =>
                timeoutFixture.IsTimingRequest(request)
                    ? Task.FromException<ProcessRunResult>(new TimeoutException(privateMarker))
                    : Task.FromResult(timeoutFixture.DefaultProcessResult(request)));
            OutputQcException exception = await AssertQcFailureAsync(
                () => qc.RunAsync(timeoutFixture.Request()),
                OutputQcFailureReason.ProcessTimedOut,
                OutputQcStage.VideoTiming,
                OutputQcRemediationKind.RepeatQualityControl);
            AssertDoesNotContain(exception.ToString(), privateMarker);
        }

        using (Fixture verificationFixture = new())
        {
            OutputQc qc = verificationFixture.CreateQc(
                verifyBinaryAsync: (request, _) => Task.FromResult(
                    BinaryVerificationResult.Failed(
                        request,
                        BinaryVerificationFailureReason.DigestMismatch,
                        request.ExpectedByteSize,
                        Digest('f'))));
            OutputQcException exception = await AssertQcFailureAsync(
                () => qc.RunAsync(verificationFixture.Request()),
                OutputQcFailureReason.ToolVerificationFailed,
                OutputQcStage.Evidence,
                OutputQcRemediationKind.RepeatQualityControl);
            Assert.AreEqual(0, verificationFixture.ProcessRequests.Count);
            AssertDoesNotContain(exception.ToString(), verificationFixture.FfprobePath);
        }

        using (Fixture artifactFixture = new())
        {
            OutputQc qc = artifactFixture.CreateQc(
                validateArtifactAsync: (_, _) =>
                    Task.FromException<StoredArtifact>(
                        new InvalidOperationException(privateMarker)));
            OutputQcException exception = await AssertQcFailureAsync(
                () => qc.RunAsync(artifactFixture.Request()),
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence,
                OutputQcRemediationKind.TechnicalRerender);
            Assert.AreEqual(0, artifactFixture.ProcessRequests.Count);
            AssertDoesNotContain(exception.ToString(), privateMarker);
        }

        using (Fixture cancellationFixture = new())
        using (CancellationTokenSource cancellation = new())
        {
            OutputQc qc = cancellationFixture.CreateQc(runProcessAsync: (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromException<ProcessRunResult>(
                    new OperationCanceledException(privateMarker, innerException: null, token));
            });
            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                    qc.RunAsync(cancellationFixture.Request(), cancellation.Token));
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        }

        using (Fixture preCancelledFixture = new())
        using (CancellationTokenSource cancellation = new())
        {
            cancellation.Cancel();
            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                    preCancelledFixture.CreateQc().RunAsync(
                        preCancelledFixture.Request(),
                        cancellation.Token));
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.AreEqual(0, preCancelledFixture.ArtifactValidationCalls);
            Assert.AreEqual(0, preCancelledFixture.ToolVerificationCalls);
            Assert.AreEqual(0, preCancelledFixture.ProcessRequests.Count);
        }
    }

    [TestMethod]
    public async Task RunAsync_NullDependencyResultsFailClosedWithStableFailures()
    {
        using (Fixture nullArtifact = new())
        {
            OutputQc qc = nullArtifact.CreateQc(
                validateArtifactAsync: (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult<StoredArtifact>(null!);
                });
            OutputQcException exception = await AssertQcFailureAsync(
                () => qc.RunAsync(nullArtifact.Request()),
                OutputQcFailureReason.RegisteredArtifactInvalid,
                OutputQcStage.Evidence,
                OutputQcRemediationKind.TechnicalRerender);
            Assert.AreEqual(1, nullArtifact.ArtifactValidationCalls);
            Assert.AreEqual(0, nullArtifact.ToolVerificationCalls);
            Assert.AreEqual(0, nullArtifact.ProcessRequests.Count);
            AssertDoesNotContain(exception.ToString(), nullArtifact.ArtifactPath);
        }

        using Fixture nullProcess = new();
        OutputQc processQc = nullProcess.CreateQc(
            runProcessAsync: (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<ProcessRunResult>(null!);
            });
        OutputQcException processException = await AssertQcFailureAsync(
            () => processQc.RunAsync(nullProcess.Request()),
            OutputQcFailureReason.ProcessFailed,
            OutputQcStage.Probe,
            OutputQcRemediationKind.RepeatQualityControl);
        Assert.AreEqual(1, nullProcess.ProcessRequests.Count);
        AssertDoesNotContain(processException.ToString(), nullProcess.ArtifactPath);
    }

    [TestMethod]
    public async Task RunAsync_RevalidatesRegisteredArtifactAndHoldsFilesAgainstMutation()
    {
        using (Fixture registryDrift = new())
        {
            int calls = 0;
            OutputQc qc = registryDrift.CreateQc(validateArtifactAsync: (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(registryDrift.Artifact);
                }

                StoredArtifact artifact = registryDrift.Artifact;
                return Task.FromResult(new StoredArtifact(
                    artifact.Id,
                    artifact.ProjectId,
                    artifact.Kind,
                    artifact.StorageReference,
                    artifact.ContentDigest,
                    artifact.ByteSize,
                    artifact.CreatedAt.AddTicks(1)));
            });

            OutputQcException exception =
                await Assert.ThrowsExactlyAsync<OutputQcException>(() =>
                    qc.RunAsync(registryDrift.Request()));
            Assert.AreEqual(
                OutputQcFailureReason.RegisteredArtifactInvalid,
                exception.Reason);
            Assert.AreEqual(OutputQcStage.Evidence, exception.Stage);
            Assert.AreEqual(
                OutputQcRemediationKind.TechnicalRerender,
                exception.Remediation);
            Assert.AreEqual(2, calls);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file-sharing semantics are required.");
            return;
        }

        using Fixture locked = new();
        bool artifactMutationBlocked = false;
        bool ffmpegMutationBlocked = false;
        OutputQc lockedQc = locked.CreateQc(runProcessAsync: (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            artifactMutationBlocked |= IsWriteBlocked(locked.ArtifactPath);
            ffmpegMutationBlocked |= IsWriteBlocked(locked.FfmpegPath);
            return Task.FromResult(locked.DefaultProcessResult(request));
        });

        OutputQcResult result = await lockedQc.RunAsync(locked.Request());
        Assert.IsTrue(result.MachineChecksPassed);
        Assert.IsTrue(artifactMutationBlocked);
        Assert.IsTrue(ffmpegMutationBlocked);
    }

    [TestMethod]
    public async Task RunAsync_ApprovedFfmpegSyntheticIntegrationWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() ||
            !TryFindCanonicalExecutable(
                "ffmpeg.exe",
                DecodeValidator.CanonicalFfmpegByteSize,
                DecodeValidator.CanonicalFfmpegSha256,
                out string ffmpegPath) ||
            !TryFindCanonicalExecutable(
                "ffprobe.exe",
                FfprobeClient.CanonicalFfprobeByteSize,
                FfprobeClient.CanonicalFfprobeSha256,
                out string ffprobePath))
        {
            Assert.Inconclusive("The approved FFmpeg package is unavailable.");
            return;
        }

        string generationRoot = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.OutputQcIntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generationRoot);
        try
        {
            string generatedPath = Path.Combine(generationRoot, "synthetic.mp4");
            string subtitlePath = Path.Combine(generationRoot, "synthetic.srt");
            await File.WriteAllTextAsync(
                subtitlePath,
                "1\n00:00:01,000 --> 00:00:03,000\ncaption\n");
            ProcessRunner runner = new();
            ProcessRunResult generation = await runner.RunAsync(
                new ProcessRunRequest(
                    ffmpegPath,
                    [
                        "-nostdin", "-v", "error",
                        "-f", "lavfi", "-i",
                        "testsrc2=size=1280x720:rate=30000/1001:duration=4",
                        "-f", "lavfi", "-i",
                        "sine=frequency=1000:sample_rate=48000:duration=4",
                        "-f", "srt", "-i", subtitlePath,
                        "-map", "0:v:0", "-map", "1:a:0", "-map", "2:s:0",
                        "-c:v", "libopenh264", "-b:v", "1200k",
                        "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "96k",
                        "-c:s", "mov_text", "-t", "4", "-n", generatedPath,
                    ],
                    generationRoot,
                    TimeSpan.FromSeconds(30)),
                CancellationToken.None);
            if (generation.ExitCode != 0 ||
                generation.Truncated ||
                generation.StandardOutput.Length != 0 ||
                generation.StandardError.Length != 0)
            {
                Assert.Inconclusive(
                    "The approved build cannot create the software-only synthetic QC control.");
                return;
            }

            byte[] artifactBytes = await File.ReadAllBytesAsync(generatedPath);
            using Fixture fixture = new(
                includeSubtitle: true,
                subtitleBurnIn: false,
                artifactBytes: artifactBytes,
                ffprobePath: ffprobePath,
                ffmpegPath: ffmpegPath);
            BinaryVerifier verifier = new();
            OutputQc qc = fixture.CreateQc(
                runProcessAsync: runner.RunAsync,
                verifyBinaryAsync: verifier.VerifyAsync,
                validateArtifactAsync: async (id, token) =>
                {
                    Assert.AreEqual(fixture.Artifact.Id, id);
                    byte[] current = await File.ReadAllBytesAsync(
                        fixture.ArtifactPath,
                        token);
                    Assert.AreEqual(
                        fixture.Artifact.ContentDigest,
                        ContentDigest.Compute(current));
                    Assert.AreEqual(fixture.Artifact.ByteSize, current.LongLength);
                    return fixture.Artifact;
                });

            OutputQcResult result = await qc.RunAsync(fixture.Request());

            Assert.IsTrue(result.MachineChecksPassed);
            Assert.IsTrue(result.HumanReviewRequired);
            Assert.IsFalse(result.ApprovedNumericThresholdsApplied);
            Assert.IsGreaterThan(0, result.VideoTiming.ObservedFrameCount);
            Assert.AreEqual(
                result.VideoTiming.ObservedFrameCount,
                result.LumaMeasurements.MeasuredFrameCount);
            Assert.IsNotNull(result.AudioTiming);
            Assert.IsNotNull(result.AudioMeasurements);
            Assert.AreEqual(2, result.SubtitleStreamIndex);
            Assert.IsNotNull(result.SubtitleTiming);
            CollectionAssert.AreEqual(
                new[] { Range(1, 2) },
                result.SubtitleTiming.CueRanges.ToArray());
            Assert.AreEqual(2, fixture.ArtifactValidationCalls);
            Assert.AreEqual(2, fixture.ToolVerificationCalls);
            Assert.AreEqual(7, fixture.ProcessRequests.Count);
        }
        finally
        {
            Directory.Delete(generationRoot, recursive: true);
        }
    }

    [TestMethod]
    public void PublicSurfaceIsImmutablePathPrivateAndHasStableRemediationClasses()
    {
        Type[] immutableTypes =
        [
            typeof(OutputQcSubtitleLayoutEvidence),
            typeof(OutputQcSubtitleEvidence),
            typeof(OutputQcRequest),
            typeof(OutputQcToolEvidence),
            typeof(OutputQcTimingEntry),
            typeof(OutputQcStreamTiming),
            typeof(OutputQcSubtitleStreamTiming),
            typeof(OutputQcDurationMeasurements),
            typeof(OutputQcLumaFrameMeasurement),
            typeof(OutputQcLumaMeasurements),
            typeof(OutputQcMeasuredValue),
            typeof(OutputQcAudioMeasurements),
            typeof(OutputQcDecodeEvidence),
            typeof(OutputQcRemediationPolicy),
            typeof(OutputQcResult),
            typeof(OutputQcException),
            typeof(OutputQc),
        ];
        foreach (Type type in immutableTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly).All(static property => property.SetMethod is null),
                type.Name);
        }

        CollectionAssert.AreEqual(
            new[] { "RunAsync" },
            typeof(OutputQc).GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsFalse(typeof(OutputQcResult).GetProperties().Any(static property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("StandardOutput", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("StandardError", StringComparison.OrdinalIgnoreCase)));

        OutputQcException content = new(
            OutputQcFailureReason.SubtitleEvidenceInvalid,
            OutputQcStage.Evidence);
        OutputQcException technical = new(
            OutputQcFailureReason.DecodeFailed,
            OutputQcStage.Decode);
        OutputQcException staleManifest = new(
            OutputQcFailureReason.ManifestBindingMismatch,
            OutputQcStage.Evidence);
        OutputQcException retry = new(
            OutputQcFailureReason.ProcessTimedOut,
            OutputQcStage.AudioLoudness);
        Assert.AreEqual(
            OutputQcRemediationKind.ContentRevisionAndReapproval,
            content.Remediation);
        Assert.AreEqual(OutputQcRemediationKind.TechnicalRerender, technical.Remediation);
        Assert.AreEqual(
            OutputQcRemediationKind.TechnicalRerender,
            staleManifest.Remediation);
        Assert.AreEqual(OutputQcRemediationKind.RepeatQualityControl, retry.Remediation);

        const string privatePath = @"C:\private\customer-output.mp4";
        const string rawOutput = "stream_index=0|best_effort_timestamp=0|duration=1001";
        foreach (OutputQcFailureReason reason in Enum.GetValues<OutputQcFailureReason>())
        {
            var exception = new OutputQcException(reason, OutputQcStage.Evidence);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privatePath);
            AssertDoesNotContain(exception.ToString(), rawOutput);
        }
    }

    private static ProbedMediaStream CreateVideoStream() =>
        new(
            0,
            MediaStreamKind.Video,
            "h264",
            "High",
            1280,
            720,
            "yuv420p",
            new RationalNumber(30000, 1001),
            new RationalNumber(30000, 1001),
            new RationalNumber(1, 30000),
            startSeconds: 0m,
            durationSeconds: 4m,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static OutputQcException AssertQcFailure(
        Action action,
        OutputQcFailureReason reason,
        OutputQcStage stage,
        OutputQcRemediationKind? remediation = null)
    {
        OutputQcException exception = Assert.ThrowsExactly<OutputQcException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(stage, exception.Stage);
        Assert.IsNull(exception.InnerException);
        if (remediation is not null)
        {
            Assert.AreEqual(remediation.Value, exception.Remediation);
        }

        return exception;
    }

    private static async Task<OutputQcException> AssertQcFailureAsync(
        Func<Task<OutputQcResult>> action,
        OutputQcFailureReason reason,
        OutputQcStage stage,
        OutputQcRemediationKind remediation)
    {
        OutputQcException exception =
            await Assert.ThrowsExactlyAsync<OutputQcException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(stage, exception.Stage);
        Assert.AreEqual(remediation, exception.Remediation);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static string ReadFixture(string relativePath) =>
        File.ReadAllText(ResolveRepositoryPath(relativePath));

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be resolved.");
    }

    private static string ProbeWithoutAudio()
    {
        JsonObject root = JsonNode.Parse(ReadFixture(ProbeFixture))?.AsObject()
            ?? throw new InvalidOperationException("The probe fixture is invalid.");
        JsonArray streams = root["streams"]?.AsArray()
            ?? throw new InvalidOperationException("The probe fixture has no streams.");
        streams.RemoveAt(1);
        return root.ToJsonString();
    }

    private static string ProbeWithSubtitle()
    {
        JsonObject root = JsonNode.Parse(ReadFixture(ProbeFixture))?.AsObject()
            ?? throw new InvalidOperationException("The probe fixture is invalid.");
        JsonArray streams = root["streams"]?.AsArray()
            ?? throw new InvalidOperationException("The probe fixture has no streams.");
        streams.Add(new JsonObject
        {
            ["index"] = 2,
            ["codec_type"] = "subtitle",
            ["codec_name"] = "mov_text",
            ["r_frame_rate"] = "0/0",
            ["avg_frame_rate"] = "0/0",
            ["time_base"] = "1/1000000",
            ["start_time"] = "0.000000",
            ["duration"] = "4.000000",
        });
        return root.ToJsonString();
    }

    private static bool IsWriteBlocked(string path)
    {
        try
        {
            using FileStream _ = File.Open(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            return false;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryFindCanonicalExecutable(
        string fileName,
        long expectedByteSize,
        string expectedSha256,
        out string executablePath)
    {
        executablePath = string.Empty;
        try
        {
            string packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Packages");
            if (!Directory.Exists(packagesRoot))
            {
                return false;
            }

            string[] packageDirectories = Directory.GetDirectories(
                packagesRoot,
                string.Concat(DecodeValidator.CanonicalPackageId, "*"),
                SearchOption.TopDirectoryOnly);
            foreach (string packageDirectory in packageDirectories)
            {
                foreach (string candidate in Directory.EnumerateFiles(
                    packageDirectory,
                    fileName,
                    SearchOption.AllDirectories))
                {
                    var fileInfo = new FileInfo(candidate);
                    if (fileInfo.Length != expectedByteSize)
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(candidate);
                    string actualSha256 = Convert.ToHexString(
                        SHA256.HashData(stream)).ToLowerInvariant();
                    if (string.Equals(
                        actualSha256,
                        expectedSha256,
                        StringComparison.Ordinal))
                    {
                        executablePath = Path.GetFullPath(candidate);
                        return true;
                    }
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                CryptographicException or NotSupportedException)
        {
        }

        return false;
    }

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-0000-0000-{value:x12}");

    private static ContentDigest Digest(char fill) => new(new string(fill, 64));

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static NormalizedRect FullRect() =>
        new(
            new RationalNumber(0, 1),
            new RationalNumber(0, 1),
            new RationalNumber(1, 1),
            new RationalNumber(1, 1));

    private static void AssertDoesNotContain(string value, string forbidden)
    {
        if (forbidden.Length == 0)
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(
            bool includeSubtitle = false,
            bool subtitleBurnIn = true,
            bool validMp4Signature = true,
            byte[]? artifactBytes = null,
            string? ffprobePath = null,
            string? ffmpegPath = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.OutputQcTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            ArtifactPath = Path.Combine(RootPath, "final-output.mp4");
            byte[] content = artifactBytes?.ToArray() ?? new byte[64];
            if (artifactBytes is null && validMp4Signature)
            {
                Encoding.ASCII.GetBytes("ftyp").CopyTo(content, 4);
            }

            File.WriteAllBytes(ArtifactPath, content);
            FfprobePath = ffprobePath ?? CreateSizedFile(
                    "ffprobe.exe",
                    FfprobeClient.CanonicalFfprobeByteSize);
            FfmpegPath = ffmpegPath ?? CreateSizedFile(
                    "ffmpeg.exe",
                    DecodeValidator.CanonicalFfmpegByteSize);

            EntityId<ProjectIdTag> projectId = Id<ProjectIdTag>(0x7a000001, 1);
            RightsRecord rights = new(
                RightsStatus.Approved,
                "rights:evidence",
                "Approved",
                externalPublicationAllowed: true);
            Asset asset = new(
                Id<AssetIdTag>(0x7a000002, 1),
                "source.mp4",
                "video/mp4",
                Digest('a'),
                100,
                rights);
            Project = new Project(
                projectId,
                "Output QC",
                "Validate final output.",
                "Reviewers",
                PublicationScope.InternalOnly,
                null,
                [asset],
                []);
            OriginRef sourceOrigin = OriginRef.FromAsset(asset.Id);
            CompositionElement element = new(
                Id<CompositionElementIdTag>(0x7a000003, 1),
                Range(0, 4),
                sourceOrigin);
            Composition = new CompositionVersion(
                Id<CompositionVersionIdTag>(0x7a000004, 1),
                projectId,
                parentVersionId: null,
                createdByActorId: "editor",
                createdAt: DateTimeOffset.UnixEpoch,
                changeReason: "Create output QC fixture.",
                contentDigest: RenderGraphCanonicalEvidence.ComputeCompositionDigest([element]),
                elements: [element]);
            Timeline = new TimelineMap(
                Id<TimelineMapIdTag>(0x7a000005, 1),
                Composition.Id,
                [new TimelineMapEntry(Range(0, 4), sourceOrigin, null)]);
            TimelineDigest = RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(Timeline);
            Layout = new LayoutSpec(
                Id<LayoutSpecIdTag>(0x7a000006, 1),
                16,
                9,
                [
                    new LayoutRegion("video", FullRect(), LayoutRegionRole.Video),
                    new LayoutRegion("subtitle", FullRect(), LayoutRegionRole.Subtitle),
                ],
                [new SafeZone("safe-frame", FullRect())]);
            LayoutDigest = Digest('b');
            Template = new TemplateReference(
                Id<TemplateIdTag>(0x7a000007, 1),
                "qc-template",
                Digest('c'));
            Profile = new RenderProfile(
                Id<RenderProfileIdTag>(0x7a000008, 1),
                "qc-profile",
                Digest('d'),
                1280,
                720,
                30000,
                1001,
                Layout.Id,
                Template.Id,
                Template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: includeSubtitle && subtitleBurnIn);

            RenderGraphInput videoInput = new(
                Id<RenderInputIdTag>(0x7a000009, 1),
                RenderInputKind.Video,
                Digest('1'),
                1000,
                sourceOrigin);
            RenderGraphInput audioInput = new(
                Id<RenderInputIdTag>(0x7a000009, 2),
                RenderInputKind.Audio,
                Digest('2'),
                200,
                sourceOrigin);
            var inputs = new List<RenderGraphInput> { videoInput, audioInput };
            RenderNode video = RenderNode.Video(
                Id<RenderNodeIdTag>(0x7a00000a, 1),
                videoInput.Id,
                Range(0, 4),
                sourceOrigin,
                sourceRange: null,
                "video");
            RenderNode audio = RenderNode.Audio(
                Id<RenderNodeIdTag>(0x7a00000a, 2),
                audioInput.Id,
                Range(0, 4),
                sourceOrigin,
                sourceRange: null);
            var nodes = new List<RenderNode> { video, audio };
            var outputInputs = new List<EntityId<RenderNodeIdTag>> { video.Id, audio.Id };
            var editorialIds = new List<EntityId<EditorialRecordIdTag>>();
            var fonts = new List<RenderFontEvidence>();

            if (includeSubtitle)
            {
                EntityId<EditorialRecordIdTag> editorialId =
                    Id<EditorialRecordIdTag>(0x7a00000b, 1);
                OriginRef editorialOrigin = OriginRef.FromEditorialRecord(editorialId);
                RenderGraphInput subtitleInput = new(
                    Id<RenderInputIdTag>(0x7a000009, 3),
                    RenderInputKind.Subtitle,
                    Digest('3'),
                    80,
                    editorialOrigin);
                Cue cue = new(
                    Id<CueIdTag>(0x7a00000c, 1),
                    CueKind.Subtitle,
                    Range(1, 2),
                    "subtitle");
                RenderNode subtitle = RenderNode.Subtitle(
                    Id<RenderNodeIdTag>(0x7a00000a, 3),
                    subtitleInput.Id,
                    video.Id,
                    cue,
                    editorialOrigin);
                inputs.Add(subtitleInput);
                nodes.Add(subtitle);
                if (Profile.SubtitleBurnIn)
                {
                    outputInputs[0] = subtitle.Id;
                }
                else
                {
                    outputInputs.Add(subtitle.Id);
                }

                editorialIds.Add(editorialId);
                fonts.Add(new RenderFontEvidence("Test Sans", Digest('4')));
                SubtitleEvidence = CreateSubtitleEvidence(cue, Layout);
            }

            RenderNode output = RenderNode.Output(
                Id<RenderNodeIdTag>(0x7a00000a, 4),
                outputInputs,
                Range(0, 4));
            nodes.Add(output);
            Graph = new RenderGraph(
                Id<RenderGraphIdTag>(0x7a00000d, 1),
                Composition.Id,
                Composition.ContentDigest,
                Timeline.Id,
                TimelineDigest,
                Profile.Id,
                Profile.ProfileDigest,
                Layout.Id,
                LayoutDigest,
                Template,
                inputs,
                fonts,
                nodes,
                output.Id);
            ValidationContext = new RenderGraphValidationContext(
                Project,
                Composition,
                Timeline,
                TimelineDigest,
                Profile,
                Layout,
                LayoutDigest,
                Template,
                [],
                editorialIds,
                inputs.Select(static input => new RenderArtifactEvidence(
                    input.Id,
                    input.ContentDigest,
                    input.ByteSize)).ToArray(),
                fonts);

            ContentDigest artifactDigest = ContentDigest.Compute(content);
            EntityId<ArtifactIdTag> artifactId = Id<ArtifactIdTag>(0x7a00000e, 1);
            string storageReference = string.Concat(
                "projects/",
                projectId.ToString(),
                "/renders/final-output.mp4");
            Artifact = new StoredArtifact(
                artifactId,
                projectId,
                ArtifactKind.Render,
                storageReference,
                artifactDigest,
                content.LongLength,
                DateTimeOffset.UnixEpoch);
            Manifest = CreateManifest();
        }

        internal string RootPath { get; }
        internal string ArtifactPath { get; }
        internal string FfprobePath { get; }
        internal string FfmpegPath { get; }
        internal Project Project { get; }
        internal CompositionVersion Composition { get; }
        internal TimelineMap Timeline { get; }
        internal ContentDigest TimelineDigest { get; }
        internal LayoutSpec Layout { get; }
        internal ContentDigest LayoutDigest { get; }
        internal TemplateReference Template { get; }
        internal RenderProfile Profile { get; }
        internal RenderGraph Graph { get; }
        internal RenderGraphValidationContext ValidationContext { get; }
        internal StoredArtifact Artifact { get; }
        internal RenderManifest Manifest { get; }
        internal OutputQcSubtitleEvidence? SubtitleEvidence { get; }
        internal int ArtifactValidationCalls { get; private set; }
        internal int ToolVerificationCalls { get; private set; }
        internal List<ProcessRunRequest> ProcessRequests { get; } = [];

        internal OutputQc CreateQc(
            Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>?
                runProcessAsync = null,
            Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>>?
                verifyBinaryAsync = null,
            Func<EntityId<ArtifactIdTag>, CancellationToken, Task<StoredArtifact>>?
                validateArtifactAsync = null)
        {
            return new OutputQc(
                async (id, token) =>
                {
                    ArtifactValidationCalls++;
                    if (validateArtifactAsync is not null)
                    {
                        return await validateArtifactAsync(id, token);
                    }

                    token.ThrowIfCancellationRequested();
                    Assert.AreEqual(Artifact.Id, id);
                    return Artifact;
                },
                id =>
                {
                    Assert.AreEqual(Artifact.Id, id);
                    return ArtifactPath;
                },
                (request, token) =>
                {
                    ToolVerificationCalls++;
                    if (verifyBinaryAsync is not null)
                    {
                        return verifyBinaryAsync(request, token);
                    }

                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(BinaryVerificationResult.Verified(
                        request,
                        request.ExpectedByteSize,
                        request.ExpectedDigest));
                },
                (request, token) =>
                {
                    ProcessRequests.Add(request);
                    if (runProcessAsync is not null)
                    {
                        return runProcessAsync(request, token);
                    }

                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(DefaultProcessResult(request));
                },
                FfprobePath,
                FfmpegPath);
        }

        internal OutputQcRequest Request(
            RenderManifest? renderManifest = null,
            RenderGraphValidationContext? validationContext = null,
            OutputQcSubtitleEvidence? subtitleEvidence = null) =>
            new(
                Artifact.Id,
                renderManifest ?? Manifest,
                Graph,
                validationContext ?? ValidationContext,
                subtitleEvidence ?? SubtitleEvidence,
                TimeSpan.FromSeconds(30));

        internal OutputQcRequest RequestWithoutSubtitleEvidence() =>
            new(
                Artifact.Id,
                Manifest,
                Graph,
                ValidationContext,
                subtitleEvidence: null,
                TimeSpan.FromSeconds(30));

        internal RenderManifest CreateManifest(string? toolVersion = null) =>
            new(
                Id<RenderManifestIdTag>(0x7a00000f, 1),
                Composition,
                Timeline,
                TimelineDigest,
                Profile,
                Digest('5'),
                "1.0.0",
                Digest('6'),
                Graph.Inputs.Select((input, index) => new RenderInputEvidence(
                    string.Concat("input-", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    input.ContentDigest)).ToArray(),
                [
                    new RenderToolEvidence(
                        "ffmpeg",
                        toolVersion ?? DecodeValidator.CanonicalPackageVersion,
                        "approved-lgpl-build",
                        new ContentDigest(DecodeValidator.CanonicalFfmpegSha256),
                        containerImageDigest: null),
                ],
                [],
                Graph.Fonts,
                [
                    new ManifestArtifact(
                        "video",
                        "video/mp4",
                        null,
                        Artifact.ByteSize,
                        Artifact.ContentDigest,
                        Artifact.StorageReference),
                ]);

        internal RenderGraphValidationContext CreateContext(RenderProfile profile) =>
            new(
                Project,
                Composition,
                Timeline,
                TimelineDigest,
                profile,
                Layout,
                LayoutDigest,
                Template,
                ValidationContext.SourceSpans,
                ValidationContext.EditorialRecordIds,
                ValidationContext.Artifacts,
                ValidationContext.Fonts);

        internal OutputQcSubtitleEvidence CreateSubtitleEvidenceWithOmittedCue()
        {
            OutputQcSubtitleEvidence evidence = SubtitleEvidence
                ?? throw new InvalidOperationException("Subtitle evidence is unavailable.");
            SubtitleDisplayResult display = InvokeInternalConstructor<SubtitleDisplayResult>(
                evidence.DisplayProfileDigest,
                2,
                evidence.DisplayResult.DisplayedCues,
                new[] { Id<CueIdTag>(0x7a00000c, 99) });
            return new OutputQcSubtitleEvidence(
                evidence.LayoutProfileDigest,
                evidence.DisplayProfileDigest,
                display,
                evidence.Layouts);
        }

        internal bool IsProbeRequest(ProcessRunRequest request) =>
            string.Equals(request.Executable, FfprobePath, StringComparison.Ordinal) &&
            !request.ArgumentList.Contains(OutputQc.TimingShowEntries, StringComparer.Ordinal) &&
            !request.ArgumentList.Contains(
                OutputQc.SubtitleTimingShowEntries,
                StringComparer.Ordinal);

        internal bool IsTimingRequest(ProcessRunRequest request) =>
            request.ArgumentList.Contains(OutputQc.TimingShowEntries, StringComparer.Ordinal);

        internal bool IsSubtitleTimingRequest(ProcessRunRequest request) =>
            request.ArgumentList.Contains(
                OutputQc.SubtitleTimingShowEntries,
                StringComparer.Ordinal);

        internal ProcessRunResult DefaultProcessResult(ProcessRunRequest request)
        {
            if (IsProbeRequest(request))
            {
                return Success(
                    Graph.Nodes.Any(static node =>
                        node.Kind == RenderNodeKind.Subtitle) &&
                    !Profile.SubtitleBurnIn
                        ? ProbeWithSubtitle()
                        : ReadFixture(ProbeFixture),
                    string.Empty);
            }

            if (IsTimingRequest(request))
            {
                int selector = Array.IndexOf(
                    request.ArgumentList.ToArray(),
                    "-select_streams");
                string streamIndex = request.ArgumentList[selector + 1];
                return Success(
                    streamIndex == "0"
                        ? ReadFixture(VideoTimingFixture)
                        : ReadFixture(AudioTimingFixture),
                    string.Empty);
            }

            if (IsSubtitleTimingRequest(request))
            {
                return Success(ReadFixture(SubtitleTimingFixture), string.Empty);
            }

            if (request.ArgumentList.Contains(OutputQc.LumaFilter, StringComparer.Ordinal))
            {
                return Success(ReadFixture(LumaFixture), string.Empty);
            }

            if (request.ArgumentList.Contains(OutputQc.LoudnessFilter, StringComparer.Ordinal))
            {
                return Success(string.Empty, ReadFixture(LoudnessFixture));
            }

            return Success(string.Empty, string.Empty);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private string CreateSizedFile(string fileName, long byteSize)
        {
            string path = Path.Combine(RootPath, fileName);
            using FileStream stream = File.Create(path);
            stream.SetLength(byteSize);
            return path;
        }

        private static ProcessRunResult Success(string stdout, string stderr) =>
            new(0, stdout, stderr, false, TimeSpan.FromMilliseconds(10));

        private static OutputQcSubtitleEvidence CreateSubtitleEvidence(
            Cue cue,
            LayoutSpec layout)
        {
            ContentDigest layoutProfileDigest = Digest('7');
            ContentDigest displayProfileDigest = Digest('8');
            SubtitlePlacementCandidate placement = new(
                Id<SubtitlePlacementCandidateIdTag>(0x7a000020, 1),
                "subtitle",
                new SubtitleLayoutRect(
                    new RationalNumber(0, 1),
                    new RationalNumber(0, 1),
                    new RationalNumber(1, 1),
                    new RationalNumber(1, 1)));
            SubtitlePlacementEvaluation evaluation = InvokeInternalConstructor<
                SubtitlePlacementEvaluation>(
                placement,
                Array.Empty<SubtitleHardConstraintKind>(),
                Array.Empty<SubtitleSoftConstraintKind>(),
                BigInteger.Zero);
            SubtitleLayoutPlan plan = InvokeInternalConstructor<SubtitleLayoutPlan>(
                placement,
                Array.Empty<SubtitleSoftConstraintKind>(),
                BigInteger.Zero);
            SubtitleLayoutResolution resolution = InvokeInternalConstructor<
                SubtitleLayoutResolution>(
                SubtitleLayoutResolutionStatus.Resolved,
                Id<SubtitleCueCandidateIdTag>(0x7a000021, 1),
                layout.Id,
                layoutProfileDigest,
                Array.Empty<SubtitleHardConstraintKind>(),
                new[] { evaluation },
                plan);
            SubtitleDisplayResult display = SubtitleDisplayPolicy.Apply(
                new SubtitleDisplayProfile(
                    displayProfileDigest,
                    SubtitleCueDisplayMode.Full,
                    SubtitlePunctuationDisplay.Preserve),
                [new SubtitleDisplaySourceCue(cue, "caption")],
                []);
            return new OutputQcSubtitleEvidence(
                layoutProfileDigest,
                displayProfileDigest,
                display,
                [new OutputQcSubtitleLayoutEvidence(cue.Id, resolution)]);
        }

        private static T InvokeInternalConstructor<T>(params object?[] arguments)
        {
            ConstructorInfo constructor = typeof(T).GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == arguments.Length);
            return (T)(constructor.Invoke(arguments)
                ?? throw new InvalidOperationException("An internal constructor returned null."));
        }
    }
}
