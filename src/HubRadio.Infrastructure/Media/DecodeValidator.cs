using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Media;

public enum DecodeValidationFailureReason
{
    InputBindingMismatch,
    ToolVerificationFailed,
    StagedFileInvalid,
    ProcessTimedOut,
    ProcessFailed,
    OutputTruncated,
    InvalidEncoding,
    UnexpectedOutput,
}

public sealed class DecodeValidationException : Exception
{
    internal DecodeValidationException(DecodeValidationFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public DecodeValidationFailureReason Reason { get; }

    private static string GetStableMessage(DecodeValidationFailureReason reason) =>
        reason switch
        {
            DecodeValidationFailureReason.InputBindingMismatch =>
                "The staged media, probe result, and PTS index do not identify the same source.",
            DecodeValidationFailureReason.ToolVerificationFailed =>
                "The full-decode tool could not be verified.",
            DecodeValidationFailureReason.StagedFileInvalid =>
                "The staged media file is invalid for full-decode validation.",
            DecodeValidationFailureReason.ProcessTimedOut =>
                "The full-decode process exceeded its timeout.",
            DecodeValidationFailureReason.ProcessFailed =>
                "The full-decode process failed.",
            DecodeValidationFailureReason.OutputTruncated =>
                "The full-decode output exceeded the capture limit.",
            DecodeValidationFailureReason.InvalidEncoding =>
                "The full-decode output is not valid UTF-8.",
            DecodeValidationFailureReason.UnexpectedOutput =>
                "The full-decode process produced unexpected standard output.",
            _ => "Full-decode validation failed.",
        };
}

public sealed class DecodeValidationResult
{
    internal DecodeValidationResult(
        EntityId<AssetIdTag> candidateId,
        EntityId<ProjectIdTag> projectId,
        EntityId<JobIdTag> jobId,
        string sourceFileName,
        ContentDigest contentDigest,
        long byteSize,
        int videoStreamIndex,
        int? audioStreamIndex,
        TimeSpan elapsed,
        int standardErrorLineCount,
        ContentDigest standardErrorContentDigest,
        RationalTime presentationStart,
        RationalTime presentationEnd,
        int indexedFrameCount)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(sourceFileName);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(standardErrorContentDigest);

        if (string.IsNullOrWhiteSpace(sourceFileName) ||
            sourceFileName.Contains('/', StringComparison.Ordinal) ||
            sourceFileName.Contains('\\', StringComparison.Ordinal) ||
            sourceFileName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A decode-validation source file name must be a nonblank leaf name without control characters.",
                nameof(sourceFileName));
        }

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                "A decode-validation source byte size must be positive.");
        }

        if (videoStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(videoStreamIndex),
                "A decoded video stream index must not be negative.");
        }

        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioStreamIndex),
                "A decoded audio stream index must not be negative when present.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsed),
                "Decode-validation elapsed time must not be negative.");
        }

        if (standardErrorLineCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(standardErrorLineCount),
                "A standard-error line count must not be negative.");
        }

        if (presentationEnd <= presentationStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationEnd),
                "The indexed presentation end must follow its start.");
        }

        if (indexedFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(indexedFrameCount),
                "An indexed frame count must be positive.");
        }

        CandidateId = candidateId;
        ProjectId = projectId;
        JobId = jobId;
        SourceFileName = sourceFileName;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        VideoStreamIndex = videoStreamIndex;
        AudioStreamIndex = audioStreamIndex;
        Elapsed = elapsed;
        StandardErrorLineCount = standardErrorLineCount;
        StandardErrorContentDigest = standardErrorContentDigest;
        PresentationStart = presentationStart;
        PresentationEnd = presentationEnd;
        IndexedFrameCount = indexedFrameCount;
    }

    public EntityId<AssetIdTag> CandidateId { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<JobIdTag> JobId { get; }

    public string SourceFileName { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public int VideoStreamIndex { get; }

    public int? AudioStreamIndex { get; }

    public bool DecodedSuccessfully => true;

    public TimeSpan Elapsed { get; }

    public int StandardErrorLineCount { get; }

    public ContentDigest StandardErrorContentDigest { get; }

    public RationalTime PresentationStart { get; }

    public RationalTime PresentationEnd { get; }

    public int IndexedFrameCount { get; }
}

public sealed class DecodeValidator
{
    internal const string CanonicalPackageId = FfprobeClient.CanonicalPackageId;
    internal const string CanonicalPackageVersion = FfprobeClient.CanonicalPackageVersion;
    internal const long CanonicalFfmpegByteSize = 113536512;
    internal const string CanonicalFfmpegSha256 =
        "6099366f31293cdc6c283ea44ffb32f07e3139cd0caf6d0db652a7d064d089cb";

    private const int FileBufferSize = 64 * 1024;
    private const string ToolComponentIdentity =
        "tool.ffmpeg." + CanonicalPackageId + "." + CanonicalPackageVersion;
    private const string StagedMediaComponentIdentity =
        "media.staged-input.decode-validation";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly ContentDigest CanonicalFfmpegDigest =
        new(CanonicalFfmpegSha256);

    private readonly Func<
        ProcessRunRequest,
        CancellationToken,
        Task<ProcessRunResult>> _runProcessAsync;

    private readonly Func<
        BinaryVerificationRequest,
        CancellationToken,
        Task<BinaryVerificationResult>> _verifyBinaryAsync;

    private readonly string _ffmpegPath;

    public DecodeValidator(
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string ffmpegPath)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(binaryVerifier);

        _ffmpegPath = ValidateFfmpegPath(ffmpegPath);
        _runProcessAsync = processRunner.RunAsync;
        _verifyBinaryAsync = binaryVerifier.VerifyAsync;
    }

    internal DecodeValidator(
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runProcessAsync,
        Func<BinaryVerificationRequest, CancellationToken, Task<BinaryVerificationResult>> verifyBinaryAsync,
        string ffmpegPath)
    {
        ArgumentNullException.ThrowIfNull(runProcessAsync);
        ArgumentNullException.ThrowIfNull(verifyBinaryAsync);

        _ffmpegPath = ValidateFfmpegPath(ffmpegPath);
        _runProcessAsync = runProcessAsync;
        _verifyBinaryAsync = verifyBinaryAsync;
    }

    public async Task<DecodeValidationResult> ValidateAsync(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        PtsIndex ptsIndex,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(ptsIndex);
        cancellationToken.ThrowIfCancellationRequested();

        (ProbedMediaStream videoStream, ProbedMediaStream? audioStream) =
            ValidateInputBinding(stagedMedia, probeResult, ptsIndex);
        ProcessRunRequest request = CreateProcessRequest(
            _ffmpegPath,
            stagedMedia.StagingPath,
            videoStream.Index,
            audioStream?.Index,
            timeout);

        using FileStream stagedStream = OpenStagedMedia(stagedMedia.StagingPath);
        ValidateHeldStagedMedia(stagedStream, stagedMedia.ByteSize);

        await VerifyAsync(
            new BinaryVerificationRequest(
                ToolComponentIdentity,
                _ffmpegPath,
                CanonicalFfmpegDigest,
                CanonicalFfmpegByteSize),
            DecodeValidationFailureReason.ToolVerificationFailed,
            cancellationToken).ConfigureAwait(false);

        await VerifyAsync(
            new BinaryVerificationRequest(
                StagedMediaComponentIdentity,
                stagedMedia.StagingPath,
                stagedMedia.ContentDigest,
                stagedMedia.ByteSize),
            DecodeValidationFailureReason.StagedFileInvalid,
            cancellationToken).ConfigureAwait(false);

        ProcessRunResult processResult;
        try
        {
            processResult = await _runProcessAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw Failure(DecodeValidationFailureReason.ProcessTimedOut);
        }
        catch (Exception)
        {
            throw Failure(DecodeValidationFailureReason.ProcessFailed);
        }

        ValidateHeldStagedMedia(stagedStream, stagedMedia.ByteSize);
        return ParseProcessResult(stagedMedia, probeResult, ptsIndex, processResult);
    }

    internal static (
        ProbedMediaStream VideoStream,
        ProbedMediaStream? AudioStream) ValidateInputBinding(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        PtsIndex ptsIndex)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(ptsIndex);

        if (!HasSameSourceIdentity(stagedMedia, probeResult) ||
            !HasSameSourceIdentity(stagedMedia, ptsIndex))
        {
            throw Failure(DecodeValidationFailureReason.InputBindingMismatch);
        }

        ProbedMediaStream videoStream = probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Video)
            .MinBy(static stream => stream.Index)
            ?? throw Failure(DecodeValidationFailureReason.InputBindingMismatch);
        RationalTime videoTimeBase = new(
            videoStream.TimeBase.Numerator,
            videoStream.TimeBase.Denominator);
        if (ptsIndex.StreamIndex != videoStream.Index ||
            ptsIndex.TimeBase != videoTimeBase)
        {
            throw Failure(DecodeValidationFailureReason.InputBindingMismatch);
        }

        ProbedMediaStream? audioStream = probeResult.Streams
            .Where(static stream => stream.Kind == MediaStreamKind.Audio)
            .MinBy(static stream => stream.Index);
        return (videoStream, audioStream);
    }

    internal static ProcessRunRequest CreateProcessRequest(
        string ffmpegPath,
        string stagedPath,
        int videoStreamIndex,
        int? audioStreamIndex,
        TimeSpan timeout)
    {
        if (videoStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(videoStreamIndex),
                "A selected FFmpeg video stream index must not be negative.");
        }

        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioStreamIndex),
                "A selected FFmpeg audio stream index must not be negative when present.");
        }

        string normalizedFfmpegPath = ValidateFfmpegPath(ffmpegPath);
        string normalizedStagedPath = SqliteStoreSupport.ValidateLocalAbsolutePath(
            stagedPath,
            nameof(stagedPath));
        string? workingDirectory = Path.GetDirectoryName(normalizedStagedPath);
        if (string.IsNullOrEmpty(workingDirectory))
        {
            throw new ArgumentException(
                "The staged media path must have a parent directory.",
                nameof(stagedPath));
        }

        List<string> arguments =
        [
            "-nostdin",
            "-v",
            "error",
            "-xerror",
            "-i",
            normalizedStagedPath,
            "-map",
            string.Concat(
                "0:",
                videoStreamIndex.ToString(CultureInfo.InvariantCulture)),
        ];
        if (audioStreamIndex is not null)
        {
            arguments.Add("-map");
            arguments.Add(string.Concat(
                "0:",
                audioStreamIndex.Value.ToString(CultureInfo.InvariantCulture)));
        }

        arguments.Add("-f");
        arguments.Add("null");
        arguments.Add("NUL");

        return new ProcessRunRequest(
            normalizedFfmpegPath,
            arguments,
            workingDirectory,
            timeout,
            ProcessRunRequest.DefaultMaxCapturedBytes,
            standardInput: null);
    }

    internal static DecodeValidationResult ParseProcessResult(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult,
        PtsIndex ptsIndex,
        ProcessRunResult processResult)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentNullException.ThrowIfNull(probeResult);
        ArgumentNullException.ThrowIfNull(ptsIndex);
        ArgumentNullException.ThrowIfNull(processResult);

        (ProbedMediaStream videoStream, ProbedMediaStream? audioStream) =
            ValidateInputBinding(stagedMedia, probeResult, ptsIndex);
        if (processResult.Truncated)
        {
            throw Failure(DecodeValidationFailureReason.OutputTruncated);
        }

        string? standardOutput = processResult.StandardOutput;
        string? standardError = processResult.StandardError;
        if (standardOutput is null || standardError is null ||
            processResult.Duration < TimeSpan.Zero)
        {
            throw Failure(DecodeValidationFailureReason.ProcessFailed);
        }

        byte[] standardErrorBytes = EncodeStrictOutput(
            standardOutput,
            standardError);
        try
        {
            if (standardOutput.Length != 0)
            {
                throw Failure(DecodeValidationFailureReason.UnexpectedOutput);
            }

            if (processResult.ExitCode != 0)
            {
                throw Failure(DecodeValidationFailureReason.ProcessFailed);
            }

            return new DecodeValidationResult(
                stagedMedia.CandidateId,
                stagedMedia.ProjectId,
                stagedMedia.JobId,
                stagedMedia.SourceFileName,
                stagedMedia.ContentDigest,
                stagedMedia.ByteSize,
                videoStream.Index,
                audioStream?.Index,
                processResult.Duration,
                CountStandardErrorLines(standardError),
                ContentDigest.Compute(standardErrorBytes),
                ptsIndex.PresentationStart,
                ptsIndex.PresentationEnd,
                ptsIndex.Entries.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(standardErrorBytes);
        }
    }

    internal static int CountStandardErrorLines(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (standardError.Length == 0)
        {
            return 0;
        }

        int lineCount = 0;
        bool endsWithLineSeparator = false;
        for (int index = 0; index < standardError.Length; index++)
        {
            char character = standardError[index];
            if (character == '\r')
            {
                lineCount++;
                if (index + 1 < standardError.Length &&
                    standardError[index + 1] == '\n')
                {
                    index++;
                }

                endsWithLineSeparator = true;
            }
            else if (character == '\n')
            {
                lineCount++;
                endsWithLineSeparator = true;
            }
            else
            {
                endsWithLineSeparator = false;
            }
        }

        return endsWithLineSeparator ? lineCount : checked(lineCount + 1);
    }

    private static bool HasSameSourceIdentity(
        StagedMediaImport stagedMedia,
        MediaProbeResult probeResult) =>
        stagedMedia.CandidateId == probeResult.CandidateId &&
        stagedMedia.ProjectId == probeResult.ProjectId &&
        stagedMedia.JobId == probeResult.JobId &&
        string.Equals(
            stagedMedia.SourceFileName,
            probeResult.SourceFileName,
            StringComparison.Ordinal) &&
        stagedMedia.ContentDigest == probeResult.ContentDigest &&
        stagedMedia.ByteSize == probeResult.ByteSize;

    private static bool HasSameSourceIdentity(
        StagedMediaImport stagedMedia,
        PtsIndex ptsIndex) =>
        stagedMedia.CandidateId == ptsIndex.CandidateId &&
        stagedMedia.ProjectId == ptsIndex.ProjectId &&
        stagedMedia.JobId == ptsIndex.JobId &&
        string.Equals(
            stagedMedia.SourceFileName,
            ptsIndex.SourceFileName,
            StringComparison.Ordinal) &&
        stagedMedia.ContentDigest == ptsIndex.ContentDigest &&
        stagedMedia.ByteSize == ptsIndex.ByteSize;

    private static byte[] EncodeStrictOutput(
        string standardOutput,
        string standardError)
    {
        if (standardOutput.Contains('\uFFFD', StringComparison.Ordinal) ||
            standardError.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw Failure(DecodeValidationFailureReason.InvalidEncoding);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(standardOutput);
            return StrictUtf8.GetBytes(standardError);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(DecodeValidationFailureReason.InvalidEncoding);
        }
    }

    private async Task VerifyAsync(
        BinaryVerificationRequest request,
        DecodeValidationFailureReason failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            BinaryVerificationResult result = await _verifyBinaryAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsVerified)
            {
                throw Failure(failureReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DecodeValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureReason);
        }
    }

    private static FileStream OpenStagedMedia(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = FileBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        try
        {
            return new FileStream(path, options);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException or
                IOException or UnauthorizedAccessException or SecurityException or
                ArgumentException or NotSupportedException)
        {
            throw Failure(DecodeValidationFailureReason.StagedFileInvalid);
        }
    }

    private static void ValidateHeldStagedMedia(
        FileStream stream,
        long expectedByteSize)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
            if ((attributes & (FileAttributes.Directory |
                               FileAttributes.Device |
                               FileAttributes.ReparsePoint)) != 0 ||
                !stream.CanRead ||
                !stream.CanSeek ||
                stream.Length <= 0 ||
                stream.Length != expectedByteSize)
            {
                throw Failure(DecodeValidationFailureReason.StagedFileInvalid);
            }
        }
        catch (DecodeValidationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            throw Failure(DecodeValidationFailureReason.StagedFileInvalid);
        }
    }

    private static string ValidateFfmpegPath(string ffmpegPath) =>
        SqliteStoreSupport.ValidateLocalAbsolutePath(
            ffmpegPath,
            nameof(ffmpegPath));

    private static DecodeValidationException Failure(
        DecodeValidationFailureReason reason) =>
        new(reason);
}