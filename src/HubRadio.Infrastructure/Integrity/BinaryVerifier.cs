using System.Security;
using System.Security.Cryptography;
using HubRadio.Core.Identity;

namespace HubRadio.Infrastructure.Integrity;

public enum BinaryVerificationFailureReason
{
    FileMissing,
    FileInaccessible,
    NotRegularFile,
    ReparsePoint,
    SizeMismatch,
    DigestMismatch,
    VerificationFailed,
}

public sealed class BinaryVerificationRequest
{
    public const int MaximumComponentIdentityLength = 128;

    public BinaryVerificationRequest(
        string componentIdentity,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        ArgumentNullException.ThrowIfNull(componentIdentity);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(expectedDigest);

        if (!IsValidComponentIdentity(componentIdentity))
        {
            throw new ArgumentException(
                $"The component identity must be a trimmed ASCII token of 1 to {MaximumComponentIdentityLength} characters using letters, digits, '.', '_' or '-'.",
                nameof(componentIdentity));
        }

        if (string.IsNullOrWhiteSpace(filePath) ||
            !string.Equals(filePath, filePath.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException(
                "The artifact file path must be a nonblank absolute path without surrounding whitespace.",
                nameof(filePath));
        }

        if (expectedByteSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedByteSize),
                "The expected artifact byte size must not be negative.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "The artifact file path must be a valid absolute path.",
                nameof(filePath));
        }

        if (OperatingSystem.IsWindows() && IsUnsafeWindowsPath(fullPath))
        {
            throw new ArgumentException(
                "The artifact file path must not use a device namespace or alternate data stream.",
                nameof(filePath));
        }

        ComponentIdentity = componentIdentity;
        FilePath = fullPath;
        ExpectedDigest = expectedDigest;
        ExpectedByteSize = expectedByteSize;
    }

    public string ComponentIdentity { get; }

    public string FilePath { get; }

    public ContentDigest ExpectedDigest { get; }

    public long ExpectedByteSize { get; }

    private static bool IsValidComponentIdentity(string value)
    {
        if (value.Length is 0 or > MaximumComponentIdentityLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (!IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsUnsafeWindowsPath(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\PIPE\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }
}

public sealed class BinaryVerificationResult
{
    public const string VerifiedCode = "binary_verification.verified";
    public const string FileMissingCode = "binary_verification.file_missing";
    public const string FileInaccessibleCode = "binary_verification.file_inaccessible";
    public const string NotRegularFileCode = "binary_verification.not_regular_file";
    public const string ReparsePointCode = "binary_verification.reparse_point";
    public const string SizeMismatchCode = "binary_verification.size_mismatch";
    public const string DigestMismatchCode = "binary_verification.digest_mismatch";
    public const string VerificationFailedCode = "binary_verification.failed";

    public const string VerifiedMessage =
        "The artifact file passed integrity verification.";
    public const string FileMissingMessage =
        "The artifact file is missing.";
    public const string FileInaccessibleMessage =
        "The artifact file is not accessible for integrity verification.";
    public const string NotRegularFileMessage =
        "The artifact path does not identify a regular file.";
    public const string ReparsePointMessage =
        "The artifact path identifies a reparse point.";
    public const string SizeMismatchMessage =
        "The artifact file size does not match the manifest.";
    public const string DigestMismatchMessage =
        "The artifact file digest does not match the manifest.";
    public const string VerificationFailedMessage =
        "The artifact file could not be verified.";

    private BinaryVerificationResult(
        string componentIdentity,
        ContentDigest expectedDigest,
        long expectedByteSize,
        long? actualByteSize,
        ContentDigest? actualDigest,
        bool isVerified,
        BinaryVerificationFailureReason? reason,
        string code,
        string message,
        DateTimeOffset timestampUtc)
    {
        ComponentIdentity = componentIdentity;
        ExpectedDigest = expectedDigest;
        ExpectedByteSize = expectedByteSize;
        ActualByteSize = actualByteSize;
        ActualDigest = actualDigest;
        IsVerified = isVerified;
        Reason = reason;
        Code = code;
        Message = message;
        TimestampUtc = timestampUtc;
    }

    public string ComponentIdentity { get; }

    public ContentDigest ExpectedDigest { get; }

    public long ExpectedByteSize { get; }

    public long? ActualByteSize { get; }

    public ContentDigest? ActualDigest { get; }

    public bool IsVerified { get; }

    public BinaryVerificationFailureReason? Reason { get; }

    public string Code { get; }

    public string Message { get; }

    public DateTimeOffset TimestampUtc { get; }

    internal static BinaryVerificationResult Verified(
        BinaryVerificationRequest request,
        long actualByteSize,
        ContentDigest actualDigest) =>
        new(
            request.ComponentIdentity,
            request.ExpectedDigest,
            request.ExpectedByteSize,
            actualByteSize,
            actualDigest,
            isVerified: true,
            reason: null,
            VerifiedCode,
            VerifiedMessage,
            DateTimeOffset.UtcNow);

    internal static BinaryVerificationResult Failed(
        BinaryVerificationRequest request,
        BinaryVerificationFailureReason reason,
        long? actualByteSize = null,
        ContentDigest? actualDigest = null)
    {
        (string code, string message) = reason switch
        {
            BinaryVerificationFailureReason.FileMissing =>
                (FileMissingCode, FileMissingMessage),
            BinaryVerificationFailureReason.FileInaccessible =>
                (FileInaccessibleCode, FileInaccessibleMessage),
            BinaryVerificationFailureReason.NotRegularFile =>
                (NotRegularFileCode, NotRegularFileMessage),
            BinaryVerificationFailureReason.ReparsePoint =>
                (ReparsePointCode, ReparsePointMessage),
            BinaryVerificationFailureReason.SizeMismatch =>
                (SizeMismatchCode, SizeMismatchMessage),
            BinaryVerificationFailureReason.DigestMismatch =>
                (DigestMismatchCode, DigestMismatchMessage),
            BinaryVerificationFailureReason.VerificationFailed =>
                (VerificationFailedCode, VerificationFailedMessage),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

        return new BinaryVerificationResult(
            request.ComponentIdentity,
            request.ExpectedDigest,
            request.ExpectedByteSize,
            actualByteSize,
            actualDigest,
            isVerified: false,
            reason,
            code,
            message,
            DateTimeOffset.UtcNow);
    }
}

public sealed class BinaryVerificationException : Exception
{
    internal BinaryVerificationException(BinaryVerificationResult result)
        : base(result.Message)
    {
        Result = result;
    }

    public string Code => Result.Code;

    public BinaryVerificationFailureReason Reason =>
        Result.Reason
        ?? throw new InvalidOperationException("A verification failure must include a reason.");

    public BinaryVerificationResult Result { get; }
}

public sealed class BinaryVerifier
{
    private const int FileBufferSize = 64 * 1024;
    private const int Sha256ByteLength = 32;

    private static readonly FileStreamOptions ReadOptions = new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = FileBufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    };

    public async Task<BinaryVerificationResult> VerifyAsync(
        BinaryVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidatePathAttributes(request, GetPathAttributes(request));
        cancellationToken.ThrowIfCancellationRequested();

        using FileStream stream = OpenRead(request);

        ValidatePathAttributes(request, GetPathAttributes(request));
        ValidateOpenedFileAttributes(request, stream);

        long actualByteSize = GetLength(request, stream);
        if (actualByteSize != request.ExpectedByteSize)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.SizeMismatch,
                actualByteSize);
        }

        byte[] actualDigestBytes = new byte[Sha256ByteLength];
        try
        {
            int bytesWritten = await SHA256.HashDataAsync(
                stream,
                actualDigestBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytesWritten != Sha256ByteLength)
            {
                throw CreateFailure(
                    request,
                    BinaryVerificationFailureReason.VerificationFailed,
                    actualByteSize);
            }
        }
        catch (BinaryVerificationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException or CryptographicException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileInaccessible,
                actualByteSize);
        }

        long finalByteSize = GetLength(request, stream);
        if (finalByteSize != request.ExpectedByteSize)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.SizeMismatch,
                finalByteSize);
        }

        ContentDigest actualDigest = ContentDigest.FromBytes(actualDigestBytes);
        byte[] expectedDigestBytes = Convert.FromHexString(request.ExpectedDigest.Hex);
        if (!CryptographicOperations.FixedTimeEquals(expectedDigestBytes, actualDigestBytes))
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.DigestMismatch,
                finalByteSize,
                actualDigest);
        }

        return BinaryVerificationResult.Verified(
            request,
            finalByteSize,
            actualDigest);
    }

    private static FileAttributes GetPathAttributes(BinaryVerificationRequest request)
    {
        try
        {
            return File.GetAttributes(request.FilePath);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileMissing);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileInaccessible);
        }
    }

    private static void ValidatePathAttributes(
        BinaryVerificationRequest request,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.ReparsePoint);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.NotRegularFile);
        }
    }

    private static FileStream OpenRead(BinaryVerificationRequest request)
    {
        try
        {
            return new FileStream(request.FilePath, ReadOptions);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileMissing);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.NotRegularFile);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileInaccessible);
        }
    }

    private static void ValidateOpenedFileAttributes(
        BinaryVerificationRequest request,
        FileStream stream)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(stream.SafeFileHandle);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileInaccessible);
        }

        ValidatePathAttributes(request, attributes);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.NotRegularFile);
        }
    }

    private static long GetLength(
        BinaryVerificationRequest request,
        FileStream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (NotSupportedException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.NotRegularFile);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw CreateFailure(
                request,
                BinaryVerificationFailureReason.FileInaccessible);
        }
    }

    private static BinaryVerificationException CreateFailure(
        BinaryVerificationRequest request,
        BinaryVerificationFailureReason reason,
        long? actualByteSize = null,
        ContentDigest? actualDigest = null) =>
        new(BinaryVerificationResult.Failed(
            request,
            reason,
            actualByteSize,
            actualDigest));
}