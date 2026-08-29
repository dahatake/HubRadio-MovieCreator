using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class PtsIndexerTests
{
    private const string VfrFixturePath =
        "tests/Fixtures/pts-index-vfr-control.compact";

    private static readonly EntityId<AssetIdTag> CandidateId =
        EntityId<AssetIdTag>.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly EntityId<JobIdTag> JobId =
        EntityId<JobIdTag>.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly DateTimeOffset CreatedAt =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    [TestMethod]
    public void ParseOutput_VfrFixturePreservesNegativePtsVariableDurationsAndRange()
    {
        using PtsFixture fixture = new();
        string output = File.ReadAllText(ResolveRepositoryPath(VfrFixturePath));

        PtsIndex result = PtsIndexer.ParseOutput(
            fixture.Staged,
            fixture.ProbeResult,
            output);

        Assert.AreSame(fixture.Staged.CandidateId, result.CandidateId);
        Assert.AreSame(fixture.Staged.ProjectId, result.ProjectId);
        Assert.AreSame(fixture.Staged.JobId, result.JobId);
        Assert.AreSame(fixture.Staged.ContentDigest, result.ContentDigest);
        Assert.AreEqual(fixture.Staged.SourceFileName, result.SourceFileName);
        Assert.AreEqual(fixture.Staged.ByteSize, result.ByteSize);
        Assert.AreEqual(2, result.StreamIndex);
        Assert.AreEqual(new RationalTime(1, 1000), result.TimeBase);
        Assert.AreEqual(PtsIndex.CurrentSchemaVersion, result.SchemaVersion);
        Assert.HasCount(3, result.Entries);

        AssertEntry(
            result.Entries[0],
            sequence: 0,
            presentationTime: new RationalTime(-1, 20),
            duration: new RationalTime(1, 50),
            keyFrame: true);
        AssertEntry(
            result.Entries[1],
            sequence: 1,
            presentationTime: new RationalTime(-3, 100),
            duration: new RationalTime(33, 1000),
            keyFrame: false);
        AssertEntry(
            result.Entries[2],
            sequence: 2,
            presentationTime: new RationalTime(3, 1000),
            duration: new RationalTime(17, 1000),
            keyFrame: false);
        Assert.AreEqual(new RationalTime(-1, 20), result.PresentationStart);
        Assert.AreEqual(new RationalTime(1, 50), result.PresentationEnd);
        Assert.AreEqual(new RationalTime(7, 100), result.PresentationRange.Duration);
        Assert.AreNotEqual(result.Entries[0].Duration, result.Entries[1].Duration);
        Assert.AreNotEqual(result.Entries[1].Duration, result.Entries[2].Duration);
    }

    [TestMethod]
    public void ParseOutput_SelectsLowestVideoStreamAndRejectsEveryOtherStream()
    {
        using PtsFixture fixture = new();
        ProbedMediaStream selected = PtsIndexer.ValidateInputBinding(
            fixture.Staged,
            fixture.ProbeResult);

        Assert.AreEqual(2, selected.Index);

        PtsIndexFailureReason reason = AssertPtsFailure(
            () => PtsIndexer.ParseOutput(
                fixture.Staged,
                fixture.ProbeResult,
                FrameLine(streamIndex: "7")));
        Assert.AreEqual(PtsIndexFailureReason.StreamIndexMismatch, reason);
    }

    [TestMethod]
    public void ParseOutput_UsesObservedTicksWithoutNominalRateOrFormatDurationFallback()
    {
        using PtsFixture fixture = new();
        MediaProbeResult probeResult = CreateProbeResult(
            fixture.Staged,
            formatDurationSeconds: 999.5m);

        PtsIndex result = PtsIndexer.ParseOutput(
            fixture.Staged,
            probeResult,
            FrameLine(
                presentationTimestamp: "-50",
                duration: "20"));

        Assert.AreEqual(new RationalTime(-1, 20), result.PresentationStart);
        Assert.AreEqual(new RationalTime(-3, 100), result.PresentationEnd);
        Assert.AreEqual(new RationalTime(1, 50), result.PresentationRange.Duration);
        Assert.AreNotEqual(
            probeResult.DurationSeconds,
            (decimal)result.PresentationRange.Duration.ToDouble());
    }

    [TestMethod]
    public void ParseOutput_DuplicateAndRegressingPtsAreRejected()
    {
        using PtsFixture fixture = new();
        string first = FrameLine(presentationTimestamp: "10");
        string duplicate = string.Join(
            '\n',
            first,
            FrameLine(
                keyFrame: "0",
                presentationTimestamp: "10"));
        string regressing = string.Join(
            '\n',
            first,
            FrameLine(
                keyFrame: "0",
                presentationTimestamp: "9"));

        foreach (string output in new[] { duplicate, regressing })
        {
            Assert.AreEqual(
                PtsIndexFailureReason.NonMonotonicPresentationTime,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    output)));
        }
    }

    [TestMethod]
    public void ParseOutput_MissingAndNotAvailableTimestampOrDurationHaveDedicatedReasons()
    {
        using PtsFixture fixture = new();
        (string Output, PtsIndexFailureReason Reason)[] failures =
        [
            (
                "stream_index=2|key_frame=1|duration=20",
                PtsIndexFailureReason.MissingPresentationTimestamp),
            (
                "stream_index=2|key_frame=1|best_effort_timestamp=|duration=20",
                PtsIndexFailureReason.MissingPresentationTimestamp),
            (
                "stream_index=2|key_frame=1|best_effort_timestamp=N/A|duration=20",
                PtsIndexFailureReason.MissingPresentationTimestamp),
            (
                "stream_index=2|key_frame=1|best_effort_timestamp=0",
                PtsIndexFailureReason.MissingDuration),
            (
                "stream_index=2|key_frame=1|best_effort_timestamp=0|duration=",
                PtsIndexFailureReason.MissingDuration),
            (
                "stream_index=2|key_frame=1|best_effort_timestamp=0|duration=N/A",
                PtsIndexFailureReason.MissingDuration),
        ];

        foreach ((string output, PtsIndexFailureReason reason) in failures)
        {
            Assert.AreEqual(
                reason,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    output)));
        }
    }

    [TestMethod]
    public void ParseOutput_KeyFrameFlagsMustBeExactAndAtLeastOneMustBeSet()
    {
        using PtsFixture fixture = new();
        foreach (string invalidFlag in new[] { "", "2", "true", "01", "N/A", "+1" })
        {
            PtsIndexFailureReason expected = invalidFlag.Length == 0
                ? PtsIndexFailureReason.MissingKeyFrameFlag
                : PtsIndexFailureReason.InvalidKeyFrameFlag;
            Assert.AreEqual(
                expected,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    FrameLine(keyFrame: invalidFlag))));
        }

        Assert.AreEqual(
            PtsIndexFailureReason.MissingKeyFrameFlag,
            AssertPtsFailure(() => PtsIndexer.ParseOutput(
                fixture.Staged,
                fixture.ProbeResult,
                "stream_index=2|best_effort_timestamp=0|duration=20")));
        Assert.AreEqual(
            PtsIndexFailureReason.KeyFrameAbsent,
            AssertPtsFailure(() => PtsIndexer.ParseOutput(
                fixture.Staged,
                fixture.ProbeResult,
                string.Join(
                    '\n',
                    FrameLine(keyFrame: "0", presentationTimestamp: "0"),
                    FrameLine(keyFrame: "0", presentationTimestamp: "20")))));
    }

    [TestMethod]
    public void ParseOutput_RejectsNoncanonicalLocaleAndNonpositiveNumbers()
    {
        using PtsFixture fixture = new();
        foreach (string invalidTimestamp in new[] { "1.5", "1,000", "+1", "01", "-0" })
        {
            Assert.AreEqual(
                PtsIndexFailureReason.InvalidPresentationTimestamp,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    FrameLine(presentationTimestamp: invalidTimestamp))));
        }

        foreach (string invalidDuration in new[] { "0", "-1", "1.5", "1,000", "01", "+1" })
        {
            Assert.AreEqual(
                PtsIndexFailureReason.InvalidDuration,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    FrameLine(duration: invalidDuration))));
        }

        foreach (string invalidStreamIndex in new[] { "-1", "02", "+2", "2.0" })
        {
            Assert.AreEqual(
                PtsIndexFailureReason.InvalidStreamIndex,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    FrameLine(streamIndex: invalidStreamIndex))));
        }
    }

    [TestMethod]
    public void ParseOutput_RejectsNumericAndPresentationEndOverflow()
    {
        using PtsFixture fixture = new();
        string aboveInt64 = "9223372036854775808";
        (string Output, MediaProbeResult ProbeResult)[] failures =
        [
            (FrameLine(presentationTimestamp: aboveInt64), fixture.ProbeResult),
            (FrameLine(duration: aboveInt64), fixture.ProbeResult),
            (FrameLine(streamIndex: "2147483648"), fixture.ProbeResult),
            (
                FrameLine(
                    presentationTimestamp: long.MaxValue.ToString(),
                    duration: "1"),
                CreateProbeResult(
                    fixture.Staged,
                    streams: [CreateVideoStream(2, new RationalNumber(1, 1))])),
        ];

        foreach ((string output, MediaProbeResult probeResult) in failures)
        {
            Assert.AreEqual(
                PtsIndexFailureReason.ArithmeticOverflow,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    probeResult,
                    output)));
        }
    }

    [TestMethod]
    public void ParseOutput_RejectsExtraDuplicateMalformedAndEmptyLines()
    {
        using PtsFixture fixture = new();
        (string Output, PtsIndexFailureReason Reason)[] failures =
        [
            (FrameLine() + "|pkt_dts=0", PtsIndexFailureReason.UnexpectedField),
            (FrameLine() + "|duration=20", PtsIndexFailureReason.UnexpectedField),
            (
                "stream_index:2|key_frame=1|best_effort_timestamp=0|duration=20",
                PtsIndexFailureReason.UnexpectedField),
            (FrameLine() + "|", PtsIndexFailureReason.UnexpectedField),
            (" " + FrameLine(), PtsIndexFailureReason.MalformedOutput),
            (FrameLine() + "\t", PtsIndexFailureReason.MalformedOutput),
            (FrameLine() + "\r", PtsIndexFailureReason.MalformedOutput),
            (
                FrameLine() + "\n\n" + FrameLine(presentationTimestamp: "20"),
                PtsIndexFailureReason.EmptyLine),
            (FrameLine() + "\n\n", PtsIndexFailureReason.EmptyLine),
            ("\n", PtsIndexFailureReason.EmptyOutput),
            (string.Empty, PtsIndexFailureReason.EmptyOutput),
        ];

        foreach ((string output, PtsIndexFailureReason reason) in failures)
        {
            Assert.AreEqual(
                reason,
                AssertPtsFailure(() => PtsIndexer.ParseOutput(
                    fixture.Staged,
                    fixture.ProbeResult,
                    output)));
        }
    }

    [TestMethod]
    public void ParseOutput_AcceptsLfCrLfAndOneTerminalLineEndingOnly()
    {
        using PtsFixture fixture = new();
        string[] outputs =
        [
            FrameLine(),
            FrameLine() + "\n",
            FrameLine() + "\r\n",
            FrameLine(presentationTimestamp: "0", duration: "20") + "\r\n" +
                FrameLine(keyFrame: "0", presentationTimestamp: "20") + "\r\n",
        ];

        foreach (string output in outputs)
        {
            PtsIndex result = PtsIndexer.ParseOutput(
                fixture.Staged,
                fixture.ProbeResult,
                output);
            Assert.IsNotEmpty(result.Entries);
        }
    }

    [TestMethod]
    public void ParseProcessResult_FailsClosedForTruncationExitStderrEmptyAndEncoding()
    {
        using PtsFixture fixture = new();
        string valid = FrameLine();
        (ProcessRunResult Result, PtsIndexFailureReason Reason)[] failures =
        [
            (
                new ProcessRunResult(1, valid, "private stderr", true, TimeSpan.Zero),
                PtsIndexFailureReason.OutputTruncated),
            (
                new ProcessRunResult(1, valid, string.Empty, false, TimeSpan.Zero),
                PtsIndexFailureReason.ProcessFailed),
            (
                new ProcessRunResult(0, valid, null!, false, TimeSpan.Zero),
                PtsIndexFailureReason.ProcessFailed),
            (
                new ProcessRunResult(0, valid, "private stderr", false, TimeSpan.Zero),
                PtsIndexFailureReason.StandardErrorPresent),
            (
                new ProcessRunResult(0, string.Empty, string.Empty, false, TimeSpan.Zero),
                PtsIndexFailureReason.EmptyOutput),
            (
                new ProcessRunResult(
                    0,
                    valid.Replace("duration=20", "duration=2\uFFFD", StringComparison.Ordinal),
                    string.Empty,
                    false,
                    TimeSpan.Zero),
                PtsIndexFailureReason.InvalidEncoding),
        ];

        foreach ((ProcessRunResult result, PtsIndexFailureReason reason) in failures)
        {
            PtsIndexException exception = Assert.ThrowsExactly<PtsIndexException>(() =>
                PtsIndexer.ParseProcessResult(
                    fixture.Staged,
                    fixture.ProbeResult,
                    result));
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), "private stderr");
            AssertDoesNotContain(exception.ToString(), valid);
            AssertDoesNotContain(exception.ToString(), fixture.Staged.StagingPath);
        }
    }

    [TestMethod]
    public void CreateProcessRequest_UsesExactDirectArgumentsAndMaximumCapture()
    {
        using PtsFixture fixture = new();
        string ffprobePath = Path.Combine(fixture.RootPath, "ffprobe.exe");
        TimeSpan timeout = TimeSpan.FromMilliseconds(1234);

        ProcessRunRequest request = PtsIndexer.CreateProcessRequest(
            ffprobePath,
            fixture.Staged.StagingPath,
            streamIndex: 2,
            timeout);

        Assert.AreEqual(Path.GetFullPath(ffprobePath), request.Executable);
        CollectionAssert.AreEqual(
            new[]
            {
                "-v",
                "error",
                "-select_streams",
                "2",
                "-show_frames",
                "-show_entries",
                "frame=stream_index,key_frame,best_effort_timestamp,duration",
                "-of",
                "compact=p=0:nk=0:escape=none:s=|",
                fixture.Staged.StagingPath,
            },
            request.ArgumentList.ToArray());
        Assert.AreEqual(
            Path.GetDirectoryName(fixture.Staged.StagingPath),
            request.WorkingDirectory);
        Assert.AreEqual(timeout, request.Timeout);
        Assert.AreEqual(
            ProcessRunRequest.MaximumMaxCapturedBytes,
            request.MaxCapturedBytes);
        Assert.IsNull(request.StandardInput);

        MethodInfo createStartInfo = typeof(ProcessRunner).GetMethod(
            "CreateStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ProcessRunner start-info factory was not found.");
        ProcessStartInfo startInfo = (ProcessStartInfo)(createStartInfo.Invoke(
            obj: null,
            parameters: new object?[] { request })
            ?? throw new InvalidOperationException("ProcessRunner returned no start info."));
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.AreEqual(request.Executable, startInfo.FileName);
        CollectionAssert.AreEqual(
            request.ArgumentList.ToArray(),
            startInfo.ArgumentList.ToArray());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PtsIndexer.CreateProcessRequest(
                ffprobePath,
                fixture.Staged.StagingPath,
                streamIndex: -1,
                timeout));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PtsIndexer.CreateProcessRequest(
                ffprobePath,
                fixture.Staged.StagingPath,
                streamIndex: 2,
                TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PtsIndexer.CreateProcessRequest(
                ffprobePath,
                fixture.Staged.StagingPath,
                streamIndex: 2,
                TimeSpan.MaxValue));
    }

    [TestMethod]
    public void ValidateInputBinding_RequiresEverySourceIdentityFieldToMatchExactly()
    {
        using PtsFixture fixture = new();
        ContentDigest otherDigest = new(new string('b', 64));
        MediaProbeResult[] mismatches =
        [
            CreateProbeResult(
                fixture.Staged,
                candidateId: EntityId<AssetIdTag>.New()),
            CreateProbeResult(
                fixture.Staged,
                projectId: EntityId<ProjectIdTag>.New()),
            CreateProbeResult(
                fixture.Staged,
                jobId: EntityId<JobIdTag>.New()),
            CreateProbeResult(
                fixture.Staged,
                sourceFileName: fixture.Staged.SourceFileName.ToUpperInvariant()),
            CreateProbeResult(fixture.Staged, contentDigest: otherDigest),
            CreateProbeResult(fixture.Staged, byteSize: fixture.Staged.ByteSize + 1),
        ];

        foreach (MediaProbeResult mismatch in mismatches)
        {
            Assert.AreEqual(
                PtsIndexFailureReason.InputBindingMismatch,
                AssertPtsFailure(() => PtsIndexer.ValidateInputBinding(
                    fixture.Staged,
                    mismatch)));
        }
    }

    [TestMethod]
    public void ConstructorValidatesDependenciesAndPathWithoutOpeningTheTool()
    {
        using PtsFixture fixture = new();
        ProcessRunner processRunner = new();
        BinaryVerifier binaryVerifier = new();
        string missingTool = Path.Combine(fixture.RootPath, "missing-ffprobe.exe");

        _ = new PtsIndexer(processRunner, binaryVerifier, missingTool);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new PtsIndexer(null!, binaryVerifier, missingTool));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new PtsIndexer(processRunner, null!, missingTool));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new PtsIndexer(processRunner, binaryVerifier, null!));

        foreach (string invalidPath in new[]
        {
            string.Empty,
            " ",
            "ffprobe.exe",
            ".\\ffprobe.exe",
            $" {missingTool}",
            $"{missingTool} ",
            @"\\server\share\ffprobe.exe",
            @"\\.\pipe\ffprobe",
            missingTool + ":alternate-stream",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                new PtsIndexer(processRunner, binaryVerifier, invalidPath));
            Assert.AreEqual("ffprobePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }
    }

    [TestMethod]
    public async Task IndexAsync_InvalidTimeoutAndCancellationPrecedeFileAndToolWork()
    {
        using PtsFixture fixture = new();
        string missingTool = Path.Combine(fixture.RootPath, "missing-ffprobe.exe");
        PtsIndexer indexer = new(new ProcessRunner(), new BinaryVerifier(), missingTool);

        ArgumentOutOfRangeException timeoutException =
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                indexer.IndexAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    TimeSpan.Zero,
                    CancellationToken.None));
        Assert.AreEqual("timeout", timeoutException.ParamName);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException cancellationException =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                indexer.IndexAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    TimeSpan.FromSeconds(1),
                    cancellation.Token));
        Assert.AreEqual(cancellation.Token, cancellationException.CancellationToken);
    }

    [TestMethod]
    public async Task IndexAsync_ToolVerificationFailureIsStableAndPathPrivate()
    {
        using PtsFixture fixture = new();
        string fakeTool = fixture.CreateFile("fake-ffprobe.exe", [1, 2, 3]);
        PtsIndexer indexer = new(new ProcessRunner(), new BinaryVerifier(), fakeTool);

        PtsIndexException exception =
            await Assert.ThrowsExactlyAsync<PtsIndexException>(() =>
                indexer.IndexAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));

        Assert.AreEqual(PtsIndexFailureReason.ToolVerificationFailed, exception.Reason);
        Assert.AreEqual("The PTS indexing tool could not be verified.", exception.Message);
        Assert.IsNull(exception.InnerException);
        AssertDoesNotContain(exception.ToString(), fakeTool);
        AssertDoesNotContain(exception.ToString(), fixture.Staged.StagingPath);
    }

    [TestMethod]
    public void ModelConstructorsRevalidateSequenceTimebaseMonotonicDurationAndKeyFrame()
    {
        RationalTime timeBase = new(1, 1000);
        PtsIndexEntry first = new(
            0,
            new RationalTime(0, 1),
            new RationalTime(1, 50),
            keyFrame: true);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PtsIndexEntry(
                -1,
                default,
                new RationalTime(1, 1000),
                keyFrame: true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PtsIndexEntry(0, default, default, keyFrame: true));
        Assert.ThrowsExactly<OverflowException>(() =>
            new PtsIndexEntry(
                0,
                new RationalTime(long.MaxValue, 1),
                new RationalTime(1, 1),
                keyFrame: true));

        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(entries: []));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries: [null!]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries:
            [
                new PtsIndexEntry(
                    1,
                    default,
                    new RationalTime(1, 1000),
                    keyFrame: true),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries:
            [
                first,
                new PtsIndexEntry(
                    1,
                    default,
                    new RationalTime(1, 50),
                    keyFrame: false),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries:
            [
                first,
                new PtsIndexEntry(
                    1,
                    new RationalTime(-1, 1000),
                    new RationalTime(1, 50),
                    keyFrame: false),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries:
            [
                new PtsIndexEntry(
                    0,
                    new RationalTime(1, 3),
                    new RationalTime(1, 50),
                    keyFrame: true),
            ],
            timeBase: timeBase));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries:
            [
                new PtsIndexEntry(
                    0,
                    default,
                    new RationalTime(1, 50),
                    keyFrame: false),
            ]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateIndex(
            entries: [first],
            streamIndex: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateIndex(
            entries: [first],
            timeBase: new RationalTime(0, 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateIndex(
            entries: [first],
            byteSize: 0));
        Assert.ThrowsExactly<ArgumentException>(() => CreateIndex(
            entries: [first],
            sourceFileName: @"C:\private\source.mov"));
    }

    [TestMethod]
    public void PtsIndexDefensivelyCopiesEntriesAndDerivesRangeFromFirstToLastEnd()
    {
        var entries = new List<PtsIndexEntry>
        {
            new(
                0,
                new RationalTime(-1, 20),
                new RationalTime(1, 50),
                keyFrame: true),
            new(
                1,
                new RationalTime(-3, 100),
                new RationalTime(33, 1000),
                keyFrame: false),
            new(
                2,
                new RationalTime(3, 1000),
                new RationalTime(17, 1000),
                keyFrame: false),
        };

        PtsIndex result = CreateIndex(entries: entries);
        entries.Clear();

        Assert.HasCount(3, result.Entries);
        Assert.AreEqual(new RationalTime(-1, 20), result.PresentationRange.Start);
        Assert.AreEqual(new RationalTime(7, 100), result.PresentationRange.Duration);
        Assert.AreEqual(new RationalTime(1, 50), result.PresentationRange.End);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<PtsIndexEntry>)result.Entries).Clear());
    }

    [TestMethod]
    public void CanonicalJsonIsDeterministicExactShapeAndContainsNoPaths()
    {
        using PtsFixture fixture = new();
        string output = File.ReadAllText(ResolveRepositoryPath(VfrFixturePath));
        PtsIndex first = PtsIndexer.ParseOutput(
            fixture.Staged,
            fixture.ProbeResult,
            output);
        PtsIndex second = PtsIndexer.ParseOutput(
            fixture.Staged,
            fixture.ProbeResult,
            output);

        Assert.AreEqual(first.CanonicalJson, second.CanonicalJson);
        Assert.AreEqual(
            first.CanonicalJson,
            CanonicalJson.Canonicalize(first.CanonicalJson));
        AssertDoesNotContain(first.CanonicalJson, fixture.Staged.StagingPath);
        AssertDoesNotContain(first.CanonicalJson, fixture.RootPath);

        using JsonDocument document = JsonDocument.Parse(first.CanonicalJson);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "candidateId",
                "projectId",
                "jobId",
                "sourceFileName",
                "contentDigest",
                "byteSize",
                "streamIndex",
                "timeBase",
                "presentationRange",
                "entries",
            },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(PtsIndex.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(CandidateId.ToString(), root.GetProperty("candidateId").GetString());
        Assert.AreEqual(ProjectId.ToString(), root.GetProperty("projectId").GetString());
        Assert.AreEqual(JobId.ToString(), root.GetProperty("jobId").GetString());
        Assert.AreEqual("vfr-source.mov", root.GetProperty("sourceFileName").GetString());
        Assert.AreEqual(
            fixture.Staged.ContentDigest.Hex,
            root.GetProperty("contentDigest").GetString());
        Assert.AreEqual(fixture.Staged.ByteSize, root.GetProperty("byteSize").GetInt64());
        Assert.AreEqual(2, root.GetProperty("streamIndex").GetInt32());

        AssertRationalShape(root.GetProperty("timeBase"), new RationalTime(1, 1000));
        JsonElement range = root.GetProperty("presentationRange");
        CollectionAssert.AreEquivalent(
            new[] { "start", "duration" },
            range.EnumerateObject().Select(static property => property.Name).ToArray());
        AssertRationalShape(range.GetProperty("start"), new RationalTime(-1, 20));
        AssertRationalShape(range.GetProperty("duration"), new RationalTime(7, 100));

        JsonElement[] entries = root.GetProperty("entries").EnumerateArray().ToArray();
        Assert.HasCount(3, entries);
        foreach (JsonElement entry in entries)
        {
            CollectionAssert.AreEquivalent(
                new[] { "sequence", "presentationTime", "duration", "keyFrame" },
                entry.EnumerateObject().Select(static property => property.Name).ToArray());
        }
    }

    [TestMethod]
    public void PublicSurfaceIsSealedImmutableAndDoesNotExposePathsOrPersistence()
    {
        foreach (Type type in new[]
        {
            typeof(PtsIndexEntry),
            typeof(PtsIndex),
            typeof(PtsIndexException),
            typeof(PtsIndexer),
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

        CollectionAssert.AreEquivalent(
            new[]
            {
                "SchemaVersion",
                "CandidateId",
                "ProjectId",
                "JobId",
                "SourceFileName",
                "ContentDigest",
                "ByteSize",
                "StreamIndex",
                "TimeBase",
                "Entries",
                "PresentationRange",
                "PresentationStart",
                "PresentationEnd",
                "CanonicalJson",
            },
            typeof(PtsIndex)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "Reason" },
            typeof(PtsIndexException)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "IndexAsync" },
            typeof(PtsIndexer)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsEmpty(typeof(PtsIndexer).GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly));
        Assert.IsFalse(typeof(PtsIndex)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void FailureReasonsAndMessagesAreStableAndPrivate()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "InputBindingMismatch",
                "ToolVerificationFailed",
                "StagedFileInvalid",
                "ProcessTimedOut",
                "ProcessFailed",
                "OutputTruncated",
                "StandardErrorPresent",
                "InvalidEncoding",
                "EmptyOutput",
                "EmptyLine",
                "UnexpectedField",
                "MalformedOutput",
                "MissingStreamIndex",
                "InvalidStreamIndex",
                "StreamIndexMismatch",
                "MissingKeyFrameFlag",
                "InvalidKeyFrameFlag",
                "MissingPresentationTimestamp",
                "InvalidPresentationTimestamp",
                "MissingDuration",
                "InvalidDuration",
                "ArithmeticOverflow",
                "NonMonotonicPresentationTime",
                "KeyFrameAbsent",
            },
            Enum.GetNames<PtsIndexFailureReason>());

        Dictionary<PtsIndexFailureReason, string> messages = new()
        {
            [PtsIndexFailureReason.InputBindingMismatch] =
                "The staged media and probe result do not identify the same source.",
            [PtsIndexFailureReason.ToolVerificationFailed] =
                "The PTS indexing tool could not be verified.",
            [PtsIndexFailureReason.StagedFileInvalid] =
                "The staged media file is invalid for PTS indexing.",
            [PtsIndexFailureReason.ProcessTimedOut] =
                "The PTS indexing process exceeded its timeout.",
            [PtsIndexFailureReason.ProcessFailed] =
                "The PTS indexing process failed.",
            [PtsIndexFailureReason.OutputTruncated] =
                "The PTS index output exceeded the capture limit.",
            [PtsIndexFailureReason.StandardErrorPresent] =
                "The PTS indexing process produced standard error output.",
            [PtsIndexFailureReason.InvalidEncoding] =
                "The PTS index output is not valid UTF-8.",
            [PtsIndexFailureReason.EmptyOutput] =
                "The PTS index output contains no frame entries.",
            [PtsIndexFailureReason.EmptyLine] =
                "The PTS index output contains an empty frame line.",
            [PtsIndexFailureReason.UnexpectedField] =
                "The PTS index output contains an unexpected frame field.",
            [PtsIndexFailureReason.MalformedOutput] =
                "The PTS index output is malformed.",
            [PtsIndexFailureReason.MissingStreamIndex] =
                "A PTS index frame is missing its stream index.",
            [PtsIndexFailureReason.InvalidStreamIndex] =
                "A PTS index frame has an invalid stream index.",
            [PtsIndexFailureReason.StreamIndexMismatch] =
                "A PTS index frame belongs to an unexpected stream.",
            [PtsIndexFailureReason.MissingKeyFrameFlag] =
                "A PTS index frame is missing its key-frame flag.",
            [PtsIndexFailureReason.InvalidKeyFrameFlag] =
                "A PTS index frame has an invalid key-frame flag.",
            [PtsIndexFailureReason.MissingPresentationTimestamp] =
                "A PTS index frame is missing its presentation timestamp.",
            [PtsIndexFailureReason.InvalidPresentationTimestamp] =
                "A PTS index frame has an invalid presentation timestamp.",
            [PtsIndexFailureReason.MissingDuration] =
                "A PTS index frame is missing its duration.",
            [PtsIndexFailureReason.InvalidDuration] =
                "A PTS index frame has an invalid duration.",
            [PtsIndexFailureReason.ArithmeticOverflow] =
                "The PTS index cannot be represented exactly.",
            [PtsIndexFailureReason.NonMonotonicPresentationTime] =
                "PTS index presentation timestamps are not strictly increasing.",
            [PtsIndexFailureReason.KeyFrameAbsent] =
                "The PTS index contains no key frame.",
        };
        const string privatePath = @"C:\private\customer-secret.mov";
        const string privateOutput =
            "stream_index=2|key_frame=1|best_effort_timestamp=0|duration=20";
        const string privateError = "customer-secret decode failure";

        foreach (PtsIndexFailureReason reason in Enum.GetValues<PtsIndexFailureReason>())
        {
            var exception = new PtsIndexException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.AreEqual(messages[reason], exception.Message);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privatePath);
            AssertDoesNotContain(exception.ToString(), privateOutput);
            AssertDoesNotContain(exception.ToString(), privateError);
        }
    }

    private static PtsIndex CreateIndex(
        IReadOnlyList<PtsIndexEntry> entries,
        string sourceFileName = "vfr-source.mov",
        long byteSize = 32,
        int streamIndex = 2,
        RationalTime? timeBase = null) =>
        new(
            CandidateId,
            ProjectId,
            JobId,
            sourceFileName,
            new ContentDigest(new string('a', 64)),
            byteSize,
            streamIndex,
            timeBase ?? new RationalTime(1, 1000),
            entries);

    private static MediaProbeResult CreateProbeResult(
        StagedMediaImport staged,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        decimal? formatDurationSeconds = null,
        IReadOnlyList<ProbedMediaStream>? streams = null) =>
        new(
            candidateId ?? staged.CandidateId,
            projectId ?? staged.ProjectId,
            jobId ?? staged.JobId,
            sourceFileName ?? staged.SourceFileName,
            contentDigest ?? staged.ContentDigest,
            byteSize ?? staged.ByteSize,
            ContainerSignature.IsoBaseMediaFtyp,
            ["mov", "mp4"],
            "QuickTime / MOV",
            formatDurationSeconds,
            declaredByteSize: null,
            bitRate: null,
            probeScore: null,
            streams ??
            [
                CreateVideoStream(7, new RationalNumber(1, 90000)),
                CreateAudioStream(4),
                CreateVideoStream(2, new RationalNumber(1, 1000)),
            ],
            warnings: []);

    private static ProbedMediaStream CreateVideoStream(
        int index,
        RationalNumber timeBase) =>
        new(
            index,
            MediaStreamKind.Video,
            "h264",
            "High",
            1920,
            1080,
            "yuv420p",
            new RationalNumber(60000, 1001),
            new RationalNumber(24000, 1001),
            timeBase,
            startSeconds: null,
            durationSeconds: null,
            sampleRate: null,
            channels: null,
            channelLayout: null);

    private static ProbedMediaStream CreateAudioStream(int index) =>
        new(
            index,
            MediaStreamKind.Audio,
            "aac",
            "LC",
            width: null,
            height: null,
            pixelFormat: null,
            nominalFrameRate: null,
            averageFrameRate: null,
            new RationalNumber(1, 48000),
            startSeconds: null,
            durationSeconds: null,
            sampleRate: 48000,
            channels: 2,
            channelLayout: "stereo");

    private static string FrameLine(
        string streamIndex = "2",
        string keyFrame = "1",
        string presentationTimestamp = "0",
        string duration = "20") =>
        string.Concat(
            "stream_index=", streamIndex,
            "|key_frame=", keyFrame,
            "|best_effort_timestamp=", presentationTimestamp,
            "|duration=", duration);

    private static PtsIndexFailureReason AssertPtsFailure(Action action)
    {
        PtsIndexException exception = Assert.ThrowsExactly<PtsIndexException>(action);
        Assert.IsNull(exception.InnerException);
        return exception.Reason;
    }

    private static void AssertEntry(
        PtsIndexEntry entry,
        int sequence,
        RationalTime presentationTime,
        RationalTime duration,
        bool keyFrame)
    {
        Assert.AreEqual(sequence, entry.Sequence);
        Assert.AreEqual(presentationTime, entry.PresentationTime);
        Assert.AreEqual(duration, entry.Duration);
        Assert.AreEqual(keyFrame, entry.KeyFrame);
    }

    private static void AssertRationalShape(
        JsonElement element,
        RationalTime expected)
    {
        CollectionAssert.AreEquivalent(
            new[] { "value", "rate" },
            element.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(expected.Value, ReadCanonicalInt64(element.GetProperty("value")));
        Assert.AreEqual(expected.Rate, ReadCanonicalInt64(element.GetProperty("rate")));
    }

    private static long ReadCanonicalInt64(JsonElement element)
    {
        decimal value = element.GetDecimal();
        if (value != decimal.Truncate(value) ||
            value < long.MinValue ||
            value > long.MaxValue)
        {
            throw new InvalidOperationException("Canonical JSON value is not an Int64 integer.");
        }

        return decimal.ToInt64(value);
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (string.IsNullOrWhiteSpace(forbiddenValue))
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Expected repository-relative file '{relativePath}'.",
                        path);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }

    private sealed class PtsFixture : IDisposable
    {
        internal PtsFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.PtsIndexerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            byte[] content = new byte[32];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(content, 4);
            string stagedPath = CreateFile("candidate.ready", content);
            Staged = new StagedMediaImport(
                CandidateId,
                ProjectId,
                JobId,
                "vfr-source.mov",
                stagedPath,
                ContentDigest.Compute(content),
                content.LongLength,
                CreatedAt);
            ProbeResult = CreateProbeResult(Staged);
        }

        internal string RootPath { get; }

        internal StagedMediaImport Staged { get; }

        internal MediaProbeResult ProbeResult { get; }

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