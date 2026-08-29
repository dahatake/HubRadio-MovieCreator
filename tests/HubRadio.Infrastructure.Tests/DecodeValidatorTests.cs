using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class DecodeValidatorTests
{
    private static readonly EntityId<AssetIdTag> CandidateId =
        EntityId<AssetIdTag>.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly EntityId<JobIdTag> JobId =
        EntityId<JobIdTag>.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly DateTimeOffset CreatedAt =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    [TestMethod]
    public void CreateProcessRequest_UsesExactDirectArgumentsAndBoundedCapture()
    {
        using DecodeFixture fixture = new();
        string ffmpegPath = Path.Combine(fixture.RootPath, "ffmpeg.exe");
        TimeSpan timeout = TimeSpan.FromMilliseconds(4321);
        (ProbedMediaStream video, ProbedMediaStream? audio) =
            DecodeValidator.ValidateInputBinding(
                fixture.Staged,
                fixture.ProbeResult,
                fixture.PtsIndex);

        ProcessRunRequest request = DecodeValidator.CreateProcessRequest(
            ffmpegPath,
            fixture.Staged.StagingPath,
            video.Index,
            audio?.Index,
            timeout);

        Assert.AreEqual(Path.GetFullPath(ffmpegPath), request.Executable);
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin",
                "-v",
                "error",
                "-xerror",
                "-i",
                fixture.Staged.StagingPath,
                "-map",
                "0:2",
                "-map",
                "0:4",
                "-f",
                "null",
                "NUL",
            },
            request.ArgumentList.ToArray());
        Assert.AreEqual(
            Path.GetDirectoryName(fixture.Staged.StagingPath),
            request.WorkingDirectory);
        Assert.AreEqual(timeout, request.Timeout);
        Assert.AreEqual(
            ProcessRunRequest.DefaultMaxCapturedBytes,
            request.MaxCapturedBytes);
        Assert.IsNull(request.StandardInput);

        ProcessStartInfo startInfo = CreateStartInfo(request);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.AreEqual(request.Executable, startInfo.FileName);
        CollectionAssert.AreEqual(
            request.ArgumentList.ToArray(),
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void CreateProcessRequest_OmitsAudioMapWhenNoAudioWasProbed()
    {
        using DecodeFixture fixture = new(includeAudio: false);
        string ffmpegPath = Path.Combine(fixture.RootPath, "ffmpeg.exe");
        (ProbedMediaStream video, ProbedMediaStream? audio) =
            DecodeValidator.ValidateInputBinding(
                fixture.Staged,
                fixture.ProbeResult,
                fixture.PtsIndex);

        ProcessRunRequest request = DecodeValidator.CreateProcessRequest(
            ffmpegPath,
            fixture.Staged.StagingPath,
            video.Index,
            audio?.Index,
            TimeSpan.FromSeconds(1));

        Assert.IsNull(audio);
        CollectionAssert.AreEqual(
            new[]
            {
                "-nostdin",
                "-v",
                "error",
                "-xerror",
                "-i",
                fixture.Staged.StagingPath,
                "-map",
                "0:2",
                "-f",
                "null",
                "NUL",
            },
            request.ArgumentList.ToArray());
    }

    [TestMethod]
    public void CreateProcessRequest_RejectsInvalidIndicesAndTimeouts()
    {
        using DecodeFixture fixture = new();
        string ffmpegPath = Path.Combine(fixture.RootPath, "ffmpeg.exe");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DecodeValidator.CreateProcessRequest(
                ffmpegPath,
                fixture.Staged.StagingPath,
                videoStreamIndex: -1,
                audioStreamIndex: null,
                TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DecodeValidator.CreateProcessRequest(
                ffmpegPath,
                fixture.Staged.StagingPath,
                videoStreamIndex: 2,
                audioStreamIndex: -1,
                TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DecodeValidator.CreateProcessRequest(
                ffmpegPath,
                fixture.Staged.StagingPath,
                videoStreamIndex: 2,
                audioStreamIndex: 4,
                TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            DecodeValidator.CreateProcessRequest(
                ffmpegPath,
                fixture.Staged.StagingPath,
                videoStreamIndex: 2,
                audioStreamIndex: 4,
                TimeSpan.MaxValue));
    }

    [TestMethod]
    public void ParseProcessResult_EmptyStderrReturnsBoundImmutableEvidence()
    {
        using DecodeFixture fixture = new();
        TimeSpan elapsed = TimeSpan.FromMilliseconds(123);

        DecodeValidationResult result = DecodeValidator.ParseProcessResult(
            fixture.Staged,
            fixture.ProbeResult,
            fixture.PtsIndex,
            new ProcessRunResult(
                0,
                string.Empty,
                string.Empty,
                false,
                elapsed));

        Assert.AreSame(fixture.Staged.CandidateId, result.CandidateId);
        Assert.AreSame(fixture.Staged.ProjectId, result.ProjectId);
        Assert.AreSame(fixture.Staged.JobId, result.JobId);
        Assert.AreSame(fixture.Staged.ContentDigest, result.ContentDigest);
        Assert.AreEqual(fixture.Staged.SourceFileName, result.SourceFileName);
        Assert.AreEqual(fixture.Staged.ByteSize, result.ByteSize);
        Assert.AreEqual(2, result.VideoStreamIndex);
        Assert.AreEqual(4, result.AudioStreamIndex);
        Assert.IsTrue(result.DecodedSuccessfully);
        Assert.AreEqual(elapsed, result.Elapsed);
        Assert.AreEqual(0, result.StandardErrorLineCount);
        Assert.AreEqual(
            "e3b0c44298fc1c149afbf4c8996fb924" +
            "27ae41e4649b934ca495991b7852b855",
            result.StandardErrorContentDigest.Hex);
        Assert.AreEqual(fixture.PtsIndex.PresentationStart, result.PresentationStart);
        Assert.AreEqual(fixture.PtsIndex.PresentationEnd, result.PresentationEnd);
        Assert.AreEqual(fixture.PtsIndex.Entries.Count, result.IndexedFrameCount);
    }

    [TestMethod]
    public void ParseProcessResult_AllowsSixtyOneStderrLinesWithoutExposingContent()
    {
        using DecodeFixture fixture = new();
        string standardError = string.Concat(
            string.Join(
                "\r\n",
                Enumerable.Range(1, 61).Select(
                    static index => $"null-muxer-diagnostic-{index:D2}")),
            "\r\n");
        ContentDigest expectedDigest = ContentDigest.Compute(
            new UTF8Encoding(false, true).GetBytes(standardError));

        DecodeValidationResult result = DecodeValidator.ParseProcessResult(
            fixture.Staged,
            fixture.ProbeResult,
            fixture.PtsIndex,
            new ProcessRunResult(
                0,
                string.Empty,
                standardError,
                false,
                TimeSpan.FromSeconds(2)));

        Assert.IsTrue(result.DecodedSuccessfully);
        Assert.AreEqual(61, result.StandardErrorLineCount);
        Assert.AreEqual(expectedDigest, result.StandardErrorContentDigest);
        Assert.IsFalse(typeof(DecodeValidationResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property =>
                property.PropertyType == typeof(string) &&
                property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CountStandardErrorLines_TreatsCrLfLfAndBareCrDeterministically()
    {
        string[] twoLineForms =
        [
            "first\nsecond",
            "first\nsecond\n",
            "first\r\nsecond\r\n",
            "first\rsecond\r",
        ];

        Assert.AreEqual(0, DecodeValidator.CountStandardErrorLines(string.Empty));
        foreach (string value in twoLineForms)
        {
            Assert.AreEqual(2, DecodeValidator.CountStandardErrorLines(value));
        }

        Assert.AreEqual(1, DecodeValidator.CountStandardErrorLines("\n"));
        Assert.AreEqual(2, DecodeValidator.CountStandardErrorLines("\n\n"));
        Assert.AreEqual(2, DecodeValidator.CountStandardErrorLines("\r\n\r\n"));
    }

    [TestMethod]
    public void ParseProcessResult_FailsClosedWithoutLeakingProcessOutput()
    {
        using DecodeFixture fixture = new();
        const string privateOutput = @"C:\private\customer-secret.mov";
        (ProcessRunResult Result, DecodeValidationFailureReason Reason)[] failures =
        [
            (
                new ProcessRunResult(
                    1,
                    string.Empty,
                    privateOutput,
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.ProcessFailed),
            (
                new ProcessRunResult(
                    0,
                    string.Empty,
                    privateOutput,
                    true,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.OutputTruncated),
            (
                new ProcessRunResult(
                    0,
                    privateOutput,
                    string.Empty,
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.UnexpectedOutput),
            (
                new ProcessRunResult(
                    0,
                    "\uFFFD",
                    string.Empty,
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.InvalidEncoding),
            (
                new ProcessRunResult(
                    0,
                    string.Empty,
                    "prefix\uFFFDsuffix",
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.InvalidEncoding),
            (
                new ProcessRunResult(
                    0,
                    string.Empty,
                    "\uD800",
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.InvalidEncoding),
            (
                new ProcessRunResult(
                    0,
                    null!,
                    string.Empty,
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.ProcessFailed),
            (
                new ProcessRunResult(
                    0,
                    string.Empty,
                    null!,
                    false,
                    TimeSpan.Zero),
                DecodeValidationFailureReason.ProcessFailed),
        ];

        foreach ((ProcessRunResult processResult, DecodeValidationFailureReason reason) in failures)
        {
            DecodeValidationException exception =
                Assert.ThrowsExactly<DecodeValidationException>(() =>
                    DecodeValidator.ParseProcessResult(
                        fixture.Staged,
                        fixture.ProbeResult,
                        fixture.PtsIndex,
                        processResult));
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privateOutput);
            AssertDoesNotContain(exception.ToString(), fixture.Staged.StagingPath);
        }
    }

    [TestMethod]
    public void ValidateInputBinding_RejectsEveryProbeIdentityMismatch()
    {
        using DecodeFixture fixture = new();
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
            AssertBindingMismatch(() => DecodeValidator.ValidateInputBinding(
                fixture.Staged,
                mismatch,
                fixture.PtsIndex));
        }
    }

    [TestMethod]
    public void ValidateInputBinding_RejectsEveryPtsIdentityStreamAndTimebaseMismatch()
    {
        using DecodeFixture fixture = new();
        ContentDigest otherDigest = new(new string('b', 64));
        PtsIndex[] mismatches =
        [
            CreatePtsIndex(
                fixture.Staged,
                candidateId: EntityId<AssetIdTag>.New()),
            CreatePtsIndex(
                fixture.Staged,
                projectId: EntityId<ProjectIdTag>.New()),
            CreatePtsIndex(
                fixture.Staged,
                jobId: EntityId<JobIdTag>.New()),
            CreatePtsIndex(
                fixture.Staged,
                sourceFileName: fixture.Staged.SourceFileName.ToUpperInvariant()),
            CreatePtsIndex(fixture.Staged, contentDigest: otherDigest),
            CreatePtsIndex(fixture.Staged, byteSize: fixture.Staged.ByteSize + 1),
            CreatePtsIndex(fixture.Staged, streamIndex: 7),
            CreatePtsIndex(
                fixture.Staged,
                timeBase: new RationalTime(1, 2000)),
        ];

        foreach (PtsIndex mismatch in mismatches)
        {
            AssertBindingMismatch(() => DecodeValidator.ValidateInputBinding(
                fixture.Staged,
                fixture.ProbeResult,
                mismatch));
        }
    }

    [TestMethod]
    public async Task ValidateAsync_MapsTimeoutAndPreservesCallerCancellation()
    {
        using DecodeFixture fixture = new();
        string privatePath = Path.Combine(fixture.RootPath, "private-timeout.mov");
        DecodeValidator timeoutValidator = CreateValidator(
            fixture,
            (_, _) => Task.FromException<ProcessRunResult>(
                new TimeoutException(privatePath)));

        DecodeValidationException timeoutException =
            await Assert.ThrowsExactlyAsync<DecodeValidationException>(() =>
                timeoutValidator.ValidateAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    fixture.PtsIndex,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));
        Assert.AreEqual(
            DecodeValidationFailureReason.ProcessTimedOut,
            timeoutException.Reason);
        Assert.IsNull(timeoutException.InnerException);
        AssertDoesNotContain(timeoutException.ToString(), privatePath);

        using CancellationTokenSource cancellation = new();
        DecodeValidator cancellationValidator = CreateValidator(
            fixture,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromException<ProcessRunResult>(
                    new OperationCanceledException(token));
            });

        OperationCanceledException cancellationException =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                cancellationValidator.ValidateAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    fixture.PtsIndex,
                    TimeSpan.FromSeconds(1),
                    cancellation.Token));
        Assert.AreEqual(
            cancellation.Token,
            cancellationException.CancellationToken);
    }

    [TestMethod]
    public async Task ValidateAsync_PreCancellationAndInvalidTimeoutPrecedeFileWork()
    {
        using DecodeFixture fixture = new();
        int verificationCalls = 0;
        int processCalls = 0;
        DecodeValidator validator = new(
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(SuccessfulProcessResult());
            },
            (request, _) =>
            {
                verificationCalls++;
                return Task.FromResult(Verified(request));
            },
            Path.Combine(fixture.RootPath, "ffmpeg.exe"));

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException cancellationException =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                validator.ValidateAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    fixture.PtsIndex,
                    TimeSpan.FromSeconds(1),
                    cancellation.Token));
        Assert.AreEqual(cancellation.Token, cancellationException.CancellationToken);
        Assert.AreEqual(0, verificationCalls);
        Assert.AreEqual(0, processCalls);

        File.Delete(fixture.Staged.StagingPath);
        ArgumentOutOfRangeException timeoutException =
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                validator.ValidateAsync(
                    fixture.Staged,
                    fixture.ProbeResult,
                    fixture.PtsIndex,
                    TimeSpan.Zero,
                    CancellationToken.None));
        Assert.AreEqual("timeout", timeoutException.ParamName);
        Assert.AreEqual(0, verificationCalls);
        Assert.AreEqual(0, processCalls);
    }

    [TestMethod]
    public async Task ValidateAsync_VerifiesCanonicalToolAndStagedInputBeforeExecution()
    {
        using DecodeFixture fixture = new();
        List<BinaryVerificationRequest> requests = [];
        bool processCalled = false;
        DecodeValidator validator = new(
            (request, _) =>
            {
                processCalled = true;
                Assert.AreEqual(fixture.Staged.StagingPath, request.ArgumentList[5]);
                return Task.FromResult(SuccessfulProcessResult());
            },
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                requests.Add(request);
                return Task.FromResult(Verified(request));
            },
            Path.Combine(fixture.RootPath, "ffmpeg.exe"));

        DecodeValidationResult result = await validator.ValidateAsync(
            fixture.Staged,
            fixture.ProbeResult,
            fixture.PtsIndex,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.IsTrue(result.DecodedSuccessfully);
        Assert.IsTrue(processCalled);
        Assert.HasCount(2, requests);
        Assert.AreEqual(
            "tool.ffmpeg.BtbN.FFmpeg.LGPL.8.1.8.1.2-20260731",
            requests[0].ComponentIdentity);
        Assert.AreEqual(DecodeValidator.CanonicalFfmpegByteSize, requests[0].ExpectedByteSize);
        Assert.AreEqual(DecodeValidator.CanonicalFfmpegSha256, requests[0].ExpectedDigest.Hex);
        Assert.AreEqual(
            "media.staged-input.decode-validation",
            requests[1].ComponentIdentity);
        Assert.AreEqual(requests[1].ExpectedByteSize, fixture.Staged.ByteSize);
        Assert.AreEqual(requests[1].ExpectedDigest, fixture.Staged.ContentDigest);
    }

    [TestMethod]
    public async Task ValidateAsync_ToolAndInputVerificationFailuresAreStableAndPathPrivate()
    {
        using DecodeFixture fixture = new();
        foreach (int failedRequestIndex in new[] { 0, 1 })
        {
            int verificationIndex = 0;
            bool processCalled = false;
            DecodeValidator validator = new(
                (_, _) =>
                {
                    processCalled = true;
                    return Task.FromResult(SuccessfulProcessResult());
                },
                (request, _) =>
                {
                    int currentIndex = verificationIndex++;
                    return Task.FromResult(currentIndex == failedRequestIndex
                        ? BinaryVerificationResult.Failed(
                            request,
                            BinaryVerificationFailureReason.DigestMismatch,
                            request.ExpectedByteSize,
                            new ContentDigest(new string('f', 64)))
                        : Verified(request));
                },
                Path.Combine(fixture.RootPath, "private-ffmpeg.exe"));

            DecodeValidationException exception =
                await Assert.ThrowsExactlyAsync<DecodeValidationException>(() =>
                    validator.ValidateAsync(
                        fixture.Staged,
                        fixture.ProbeResult,
                        fixture.PtsIndex,
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None));

            DecodeValidationFailureReason expectedReason = failedRequestIndex == 0
                ? DecodeValidationFailureReason.ToolVerificationFailed
                : DecodeValidationFailureReason.StagedFileInvalid;
            Assert.AreEqual(expectedReason, exception.Reason);
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(processCalled);
            AssertDoesNotContain(exception.ToString(), fixture.RootPath);
            AssertDoesNotContain(exception.ToString(), fixture.Staged.StagingPath);
        }
    }

    [TestMethod]
    public async Task ValidateAsync_HoldsReadShareHandleAcrossProcessExecution()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file-share enforcement is required.");
            return;
        }

        using DecodeFixture fixture = new();
        bool writeBlocked = false;
        bool renameBlocked = false;
        string renamedPath = fixture.Staged.StagingPath + ".renamed";
        DecodeValidator validator = CreateValidator(
            fixture,
            (_, _) =>
            {
                try
                {
                    using FileStream _ = new(
                        fixture.Staged.StagingPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException)
                {
                    writeBlocked = true;
                }

                try
                {
                    File.Move(fixture.Staged.StagingPath, renamedPath);
                }
                catch (IOException)
                {
                    renameBlocked = true;
                }

                return Task.FromResult(SuccessfulProcessResult());
            });

        DecodeValidationResult result = await validator.ValidateAsync(
            fixture.Staged,
            fixture.ProbeResult,
            fixture.PtsIndex,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.IsTrue(result.DecodedSuccessfully);
        Assert.IsTrue(writeBlocked);
        Assert.IsTrue(renameBlocked);
        Assert.IsTrue(File.Exists(fixture.Staged.StagingPath));
        Assert.IsFalse(File.Exists(renamedPath));
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsReparseStagedInputBeforeExecutionWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using DecodeFixture fixture = new();
        string targetPath = fixture.CreateFile("reparse-target.ready", fixture.Content);
        string linkPath = Path.Combine(fixture.RootPath, "reparse-source.ready");
        try
        {
            _ = File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception linkCreationException)
            when (linkCreationException is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException or NotSupportedException)
        {
            Assert.IsFalse(File.Exists(linkPath));
            return;
        }

        StagedMediaImport linkedStaged = CreateStagedMedia(linkPath, fixture.Content);
        MediaProbeResult linkedProbe = CreateProbeResult(linkedStaged);
        PtsIndex linkedPts = CreatePtsIndex(linkedStaged);
        bool processCalled = false;
        BinaryVerifier realVerifier = new();
        DecodeValidator validator = new(
            (_, _) =>
            {
                processCalled = true;
                return Task.FromResult(SuccessfulProcessResult());
            },
            (request, cancellationToken) =>
                request.ComponentIdentity.StartsWith("tool.ffmpeg.", StringComparison.Ordinal)
                    ? Task.FromResult(Verified(request))
                    : realVerifier.VerifyAsync(request, cancellationToken),
            Path.Combine(fixture.RootPath, "ffmpeg.exe"));

        DecodeValidationException validationException =
            await Assert.ThrowsExactlyAsync<DecodeValidationException>(() =>
                validator.ValidateAsync(
                    linkedStaged,
                    linkedProbe,
                    linkedPts,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));

        Assert.AreEqual(
            DecodeValidationFailureReason.StagedFileInvalid,
            validationException.Reason);
        Assert.IsFalse(processCalled);
        AssertDoesNotContain(validationException.ToString(), linkPath);
        AssertDoesNotContain(validationException.ToString(), targetPath);
    }

    [TestMethod]
    public void ConstructorConstantsFailureMessagesAndPublicSurfaceAreStable()
    {
        using DecodeFixture fixture = new();
        ProcessRunner processRunner = new();
        BinaryVerifier binaryVerifier = new();
        string missingTool = Path.Combine(fixture.RootPath, "missing-ffmpeg.exe");

        _ = new DecodeValidator(processRunner, binaryVerifier, missingTool);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new DecodeValidator(null!, binaryVerifier, missingTool));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new DecodeValidator(processRunner, null!, missingTool));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new DecodeValidator(processRunner, binaryVerifier, null!));

        foreach (string invalidPath in new[]
        {
            string.Empty,
            " ",
            "ffmpeg.exe",
            ".\\ffmpeg.exe",
            $" {missingTool}",
            $"{missingTool} ",
            @"\\server\share\ffmpeg.exe",
            @"\\.\pipe\ffmpeg",
            missingTool + ":alternate-stream",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                new DecodeValidator(processRunner, binaryVerifier, invalidPath));
            Assert.AreEqual("ffmpegPath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }

        Assert.AreEqual(
            "BtbN.FFmpeg.LGPL.8.1",
            GetInternalConstant<string>("CanonicalPackageId"));
        Assert.AreEqual(
            "8.1.2-20260731",
            GetInternalConstant<string>("CanonicalPackageVersion"));
        Assert.AreEqual(
            113536512L,
            GetInternalConstant<long>("CanonicalFfmpegByteSize"));
        Assert.AreEqual(
            "6099366f31293cdc6c283ea44ffb32f07e3139cd0caf6d0db652a7d064d089cb",
            GetInternalConstant<string>("CanonicalFfmpegSha256"));

        CollectionAssert.AreEqual(
            new[]
            {
                "InputBindingMismatch",
                "ToolVerificationFailed",
                "StagedFileInvalid",
                "ProcessTimedOut",
                "ProcessFailed",
                "OutputTruncated",
                "InvalidEncoding",
                "UnexpectedOutput",
            },
            Enum.GetNames<DecodeValidationFailureReason>());
        Dictionary<DecodeValidationFailureReason, string> messages = new()
        {
            [DecodeValidationFailureReason.InputBindingMismatch] =
                "The staged media, probe result, and PTS index do not identify the same source.",
            [DecodeValidationFailureReason.ToolVerificationFailed] =
                "The full-decode tool could not be verified.",
            [DecodeValidationFailureReason.StagedFileInvalid] =
                "The staged media file is invalid for full-decode validation.",
            [DecodeValidationFailureReason.ProcessTimedOut] =
                "The full-decode process exceeded its timeout.",
            [DecodeValidationFailureReason.ProcessFailed] =
                "The full-decode process failed.",
            [DecodeValidationFailureReason.OutputTruncated] =
                "The full-decode output exceeded the capture limit.",
            [DecodeValidationFailureReason.InvalidEncoding] =
                "The full-decode output is not valid UTF-8.",
            [DecodeValidationFailureReason.UnexpectedOutput] =
                "The full-decode process produced unexpected standard output.",
        };
        foreach (DecodeValidationFailureReason reason in Enum.GetValues<DecodeValidationFailureReason>())
        {
            var exception = new DecodeValidationException(reason);
            Assert.AreEqual(reason, exception.Reason);
            Assert.AreEqual(messages[reason], exception.Message);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), fixture.RootPath);
        }

        foreach (Type type in new[]
        {
            typeof(DecodeValidationException),
            typeof(DecodeValidationResult),
            typeof(DecodeValidator),
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
            new[]
            {
                "CandidateId",
                "ProjectId",
                "JobId",
                "SourceFileName",
                "ContentDigest",
                "ByteSize",
                "VideoStreamIndex",
                "AudioStreamIndex",
                "DecodedSuccessfully",
                "Elapsed",
                "StandardErrorLineCount",
                "StandardErrorContentDigest",
                "PresentationStart",
                "PresentationEnd",
                "IndexedFrameCount",
            },
            typeof(DecodeValidationResult)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        Assert.IsEmpty(typeof(DecodeValidationResult).GetConstructors());
        Assert.IsFalse(typeof(DecodeValidationResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("Message", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("StandardError", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(
            new[] { "Reason" },
            typeof(DecodeValidationException)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "ValidateAsync" },
            typeof(DecodeValidator)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        Assert.IsEmpty(typeof(DecodeValidator).GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly));
    }

    [TestMethod]
    public async Task ValidateAsync_CanonicalFfmpegDecodesGeneratedSyntheticVideoAndAudio()
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

        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.DecodeValidatorIntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            string stagedPath = Path.Combine(rootPath, "synthetic-control.mkv");
            ProcessRunner processRunner = new();
            ProcessRunResult generationResult = await processRunner.RunAsync(
                new ProcessRunRequest(
                    ffmpegPath,
                    [
                        "-nostdin",
                        "-v",
                        "error",
                        "-f",
                        "lavfi",
                        "-i",
                        "color=c=black:s=64x64:r=5:d=1",
                        "-f",
                        "lavfi",
                        "-i",
                        "sine=frequency=1000:sample_rate=8000:duration=1",
                        "-map",
                        "0:v:0",
                        "-map",
                        "1:a:0",
                        "-c:v",
                        "ffv1",
                        "-c:a",
                        "pcm_s16le",
                        "-shortest",
                        stagedPath,
                    ],
                    rootPath,
                    TimeSpan.FromSeconds(30)),
                CancellationToken.None);
            Assert.AreEqual(0, generationResult.ExitCode);
            Assert.AreEqual(string.Empty, generationResult.StandardOutput);
            Assert.AreEqual(string.Empty, generationResult.StandardError);
            Assert.IsFalse(generationResult.Truncated);

            byte[] content = await File.ReadAllBytesAsync(stagedPath);
            StagedMediaImport stagedMedia = CreateStagedMedia(stagedPath, content);
            BinaryVerifier binaryVerifier = new();
            FfprobeClient probeClient = new(
                processRunner,
                binaryVerifier,
                ffprobePath);
            MediaProbeResult probeResult = await probeClient.ProbeAsync(
                stagedMedia,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            PtsIndexer ptsIndexer = new(
                processRunner,
                binaryVerifier,
                ffprobePath);
            PtsIndex ptsIndex = await ptsIndexer.IndexAsync(
                stagedMedia,
                probeResult,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);
            DecodeValidator validator = new(
                processRunner,
                binaryVerifier,
                ffmpegPath);

            DecodeValidationResult result = await validator.ValidateAsync(
                stagedMedia,
                probeResult,
                ptsIndex,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.IsTrue(result.DecodedSuccessfully);
            Assert.AreEqual(
                probeResult.Streams
                    .Where(static stream => stream.Kind == MediaStreamKind.Video)
                    .Min(static stream => stream.Index),
                result.VideoStreamIndex);
            Assert.AreEqual(
                probeResult.Streams
                    .Where(static stream => stream.Kind == MediaStreamKind.Audio)
                    .Min(static stream => stream.Index),
                result.AudioStreamIndex);
            Assert.AreEqual(ptsIndex.PresentationStart, result.PresentationStart);
            Assert.AreEqual(ptsIndex.PresentationEnd, result.PresentationEnd);
            Assert.AreEqual(ptsIndex.Entries.Count, result.IndexedFrameCount);
            Assert.IsGreaterThan(0, result.IndexedFrameCount);
            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, result.Elapsed);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static DecodeValidator CreateValidator(
        DecodeFixture fixture,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync) =>
        new(
            runProcessAsync,
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Verified(request));
            },
            Path.Combine(fixture.RootPath, "ffmpeg.exe"));

    private static BinaryVerificationResult Verified(
        BinaryVerificationRequest request) =>
        BinaryVerificationResult.Verified(
            request,
            request.ExpectedByteSize,
            request.ExpectedDigest);

    private static ProcessRunResult SuccessfulProcessResult() =>
        new(
            0,
            string.Empty,
            string.Empty,
            false,
            TimeSpan.FromMilliseconds(1));

    private static void AssertBindingMismatch(Action action)
    {
        DecodeValidationException exception =
            Assert.ThrowsExactly<DecodeValidationException>(action);
        Assert.AreEqual(
            DecodeValidationFailureReason.InputBindingMismatch,
            exception.Reason);
        Assert.IsNull(exception.InnerException);
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRunRequest request)
    {
        MethodInfo method = typeof(ProcessRunner).GetMethod(
            "CreateStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "ProcessRunner start-info factory was not found.");
        return (ProcessStartInfo)(method.Invoke(
            obj: null,
            parameters: new object?[] { request })
            ?? throw new InvalidOperationException(
                "ProcessRunner returned no start info."));
    }

    private static T GetInternalConstant<T>(string fieldName)
    {
        FieldInfo field = typeof(DecodeValidator).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "A decode-validator constant was not found.");
        return (T)(field.GetRawConstantValue()
            ?? throw new InvalidOperationException(
                "A decode-validator constant has no value."));
    }

    private static StagedMediaImport CreateStagedMedia(
        string stagedPath,
        byte[] content) =>
        new(
            CandidateId,
            ProjectId,
            JobId,
            Path.GetFileName(stagedPath),
            stagedPath,
            ContentDigest.Compute(content),
            content.LongLength,
            CreatedAt);

    private static MediaProbeResult CreateProbeResult(
        StagedMediaImport stagedMedia,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        bool includeAudio = true)
    {
        List<ProbedMediaStream> streams =
        [
            CreateVideoStream(7, new RationalNumber(1, 90000)),
        ];
        if (includeAudio)
        {
            streams.Add(CreateAudioStream(4));
        }

        streams.Add(CreateVideoStream(2, new RationalNumber(1, 1000)));
        return new MediaProbeResult(
            candidateId ?? stagedMedia.CandidateId,
            projectId ?? stagedMedia.ProjectId,
            jobId ?? stagedMedia.JobId,
            sourceFileName ?? stagedMedia.SourceFileName,
            contentDigest ?? stagedMedia.ContentDigest,
            byteSize ?? stagedMedia.ByteSize,
            ContainerSignature.IsoBaseMediaFtyp,
            ["mov", "mp4"],
            "QuickTime / MOV",
            durationSeconds: 1m,
            declaredByteSize: null,
            bitRate: null,
            probeScore: null,
            streams,
            warnings: includeAudio ? [] : ["audio-track-absent"]);
    }

    private static PtsIndex CreatePtsIndex(
        StagedMediaImport stagedMedia,
        EntityId<AssetIdTag>? candidateId = null,
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<JobIdTag>? jobId = null,
        string? sourceFileName = null,
        ContentDigest? contentDigest = null,
        long? byteSize = null,
        int streamIndex = 2,
        RationalTime? timeBase = null) =>
        new(
            candidateId ?? stagedMedia.CandidateId,
            projectId ?? stagedMedia.ProjectId,
            jobId ?? stagedMedia.JobId,
            sourceFileName ?? stagedMedia.SourceFileName,
            contentDigest ?? stagedMedia.ContentDigest,
            byteSize ?? stagedMedia.ByteSize,
            streamIndex,
            timeBase ?? new RationalTime(1, 1000),
            CreatePtsEntries());

    private static IReadOnlyList<PtsIndexEntry> CreatePtsEntries() =>
    [
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
    ];

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

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (string.IsNullOrWhiteSpace(forbiddenValue))
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DecodeFixture : IDisposable
    {
        internal DecodeFixture(bool includeAudio = true)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.DecodeValidatorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            Content = new byte[64];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(Content, 4);
            string stagedPath = CreateFile("synthetic-control.mov", Content);
            Staged = CreateStagedMedia(stagedPath, Content);
            ProbeResult = CreateProbeResult(Staged, includeAudio: includeAudio);
            PtsIndex = CreatePtsIndex(Staged);
        }

        internal string RootPath { get; }

        internal byte[] Content { get; }

        internal StagedMediaImport Staged { get; }

        internal MediaProbeResult ProbeResult { get; }

        internal PtsIndex PtsIndex { get; }

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