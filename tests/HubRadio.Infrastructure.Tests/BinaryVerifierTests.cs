using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class BinaryVerifierTests
{
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [TestMethod]
    public void Request_ValidEntry_IsImmutableAndCanonical()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "artifact.bin");
        ContentDigest digest = new(EmptySha256);

        BinaryVerificationRequest request = new(
            "ffmpeg.exe",
            filePath,
            digest,
            expectedByteSize: 0);

        Assert.AreEqual("ffmpeg.exe", request.ComponentIdentity);
        Assert.AreEqual(Path.GetFullPath(filePath), request.FilePath);
        Assert.IsTrue(ReferenceEquals(digest, request.ExpectedDigest));
        Assert.AreEqual(0, request.ExpectedByteSize);
        AssertPropertiesAreGetOnly(typeof(BinaryVerificationRequest));
    }

    [TestMethod]
    public void Request_NullRequiredValues_AreRejected()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "artifact.bin");
        ContentDigest digest = new(EmptySha256);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BinaryVerificationRequest(null!, filePath, digest, 0));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BinaryVerificationRequest("tool", null!, digest, 0));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new BinaryVerificationRequest("tool", filePath, null!, 0));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" tool")]
    [DataRow("tool ")]
    [DataRow("tool name")]
    [DataRow("tool/path")]
    [DataRow("tool\\path")]
    [DataRow("tool:version")]
    [DataRow("-tool")]
    [DataRow(".tool")]
    [DataRow("tool\r\nforged")]
    [DataRow("モデル")]
    public void Request_UnsafeComponentIdentity_IsRejected(string componentIdentity)
    {
        string filePath = Path.Combine(Path.GetTempPath(), "artifact.bin");

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new BinaryVerificationRequest(
                componentIdentity,
                filePath,
                new ContentDigest(EmptySha256),
                0));

        Assert.AreEqual("componentIdentity", exception.ParamName);
        Assert.IsTrue(
            exception.Message.StartsWith(
                "The component identity must be a trimmed ASCII token",
                StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(componentIdentity))
        {
            Assert.IsFalse(exception.Message.Contains(componentIdentity, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Request_OverlongComponentIdentity_IsRejected()
    {
        string componentIdentity = new(
            'a',
            BinaryVerificationRequest.MaximumComponentIdentityLength + 1);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new BinaryVerificationRequest(
                componentIdentity,
                Path.Combine(Path.GetTempPath(), "artifact.bin"),
                new ContentDigest(EmptySha256),
                0));

        Assert.AreEqual("componentIdentity", exception.ParamName);
        Assert.IsFalse(exception.Message.Contains(componentIdentity, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("artifact.bin")]
    [DataRow(".\\artifact.bin")]
    [DataRow("..\\artifact.bin")]
    public void Request_BlankOrRelativePath_IsRejected(string filePath)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new BinaryVerificationRequest(
                "tool",
                filePath,
                new ContentDigest(EmptySha256),
                0));

        Assert.AreEqual("filePath", exception.ParamName);
        Assert.IsTrue(
            exception.Message.StartsWith(
                "The artifact file path must be a nonblank absolute path",
                StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            Assert.IsFalse(exception.Message.Contains(filePath, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Request_UntrimmedAbsolutePath_IsRejected()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "artifact.bin");
        string[] invalidPaths = [$" {filePath}", $"{filePath} "];

        foreach (string invalidPath in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new BinaryVerificationRequest(
                    "tool",
                    invalidPath,
                    new ContentDigest(EmptySha256),
                    0));

            Assert.AreEqual("filePath", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(invalidPath, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Request_WindowsDeviceAndAlternateStreamPaths_AreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows path namespaces are required for this test.");
        }

        string ordinaryPath = Path.Combine(Path.GetTempPath(), "artifact.bin");
        string[] invalidPaths =
        [
            @"\\.\pipe\hubradio-verifier-test",
            @"\\?\PIPE\hubradio-verifier-test",
            @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\artifact.bin",
            ordinaryPath + ":alternate-stream",
        ];

        foreach (string invalidPath in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new BinaryVerificationRequest(
                    "tool",
                    invalidPath,
                    new ContentDigest(EmptySha256),
                    0));

            Assert.AreEqual("filePath", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(invalidPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void Request_NegativeExpectedByteSize_IsRejected()
    {
        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new BinaryVerificationRequest(
                    "tool",
                    Path.Combine(Path.GetTempPath(), "artifact.bin"),
                    new ContentDigest(EmptySha256),
                    -1));

        Assert.AreEqual("expectedByteSize", exception.ParamName);
    }

    [TestMethod]
    public async Task VerifyAsync_ValidFile_ReturnsAuditReadySuccess()
    {
        using TemporaryDirectory temporary = new();
        byte[] content = Encoding.UTF8.GetBytes("trusted tool artifact");
        string filePath = temporary.CreateFile("tool.bin", content);
        ContentDigest expectedDigest = ContentDigest.Compute(content);
        BinaryVerificationRequest request = new(
            "tool.ffmpeg",
            filePath,
            expectedDigest,
            content.LongLength);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        BinaryVerificationResult result = await new BinaryVerifier().VerifyAsync(request);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.IsTrue(result.IsVerified);
        Assert.IsNull(result.Reason);
        Assert.AreEqual(BinaryVerificationResult.VerifiedCode, result.Code);
        Assert.AreEqual(BinaryVerificationResult.VerifiedMessage, result.Message);
        Assert.AreEqual("tool.ffmpeg", result.ComponentIdentity);
        Assert.AreSame(expectedDigest, result.ExpectedDigest);
        Assert.IsTrue(result.ExpectedByteSize == content.LongLength);
        Assert.AreEqual(content.LongLength, result.ActualByteSize);
        Assert.AreEqual(expectedDigest, result.ActualDigest);
        Assert.AreEqual(TimeSpan.Zero, result.TimestampUtc.Offset);
        Assert.IsTrue(result.TimestampUtc >= before && result.TimestampUtc <= after);
        AssertPropertiesAreGetOnly(typeof(BinaryVerificationResult));
        AssertResultHasNoPathProperty();
    }

    [TestMethod]
    public async Task VerifyAsync_EmptyFile_ReturnsKnownDigest()
    {
        using TemporaryDirectory temporary = new();
        string filePath = temporary.CreateFile("empty.model", []);
        BinaryVerificationRequest request = new(
            "model.empty",
            filePath,
            new ContentDigest(EmptySha256),
            expectedByteSize: 0);

        BinaryVerificationResult result = await new BinaryVerifier().VerifyAsync(request);

        Assert.IsTrue(result.IsVerified);
        Assert.AreEqual(0, result.ActualByteSize);
        Assert.AreEqual(EmptySha256, result.ActualDigest?.Hex);
    }

    [TestMethod]
    public async Task VerifyAsync_WrongSize_FailsBeforePublishingDigest()
    {
        using TemporaryDirectory temporary = new();
        byte[] content = [1, 2, 3, 4];
        string filePath = temporary.CreateFile("wrong-size.bin", content);
        BinaryVerificationRequest request = new(
            "tool.size",
            filePath,
            ContentDigest.Compute(content),
            expectedByteSize: content.LongLength + 1);

        BinaryVerificationException exception =
            await Assert.ThrowsExactlyAsync<BinaryVerificationException>(
                () => new BinaryVerifier().VerifyAsync(request));

        AssertFailure(
            exception,
            BinaryVerificationFailureReason.SizeMismatch,
            BinaryVerificationResult.SizeMismatchCode,
            BinaryVerificationResult.SizeMismatchMessage);
        Assert.AreEqual(content.LongLength, exception.Result.ActualByteSize);
        Assert.IsNull(exception.Result.ActualDigest);
    }

    [TestMethod]
    public async Task VerifyAsync_WrongDigest_ReportsSafelyKnownActualDigest()
    {
        using TemporaryDirectory temporary = new();
        byte[] content = Encoding.UTF8.GetBytes("actual content");
        string filePath = temporary.CreateFile("wrong-digest.bin", content);
        ContentDigest actualDigest = ContentDigest.Compute(content);
        BinaryVerificationRequest request = new(
            "model.digest",
            filePath,
            new ContentDigest(new string('0', 64)),
            content.LongLength);

        BinaryVerificationException exception =
            await Assert.ThrowsExactlyAsync<BinaryVerificationException>(
                () => new BinaryVerifier().VerifyAsync(request));

        AssertFailure(
            exception,
            BinaryVerificationFailureReason.DigestMismatch,
            BinaryVerificationResult.DigestMismatchCode,
            BinaryVerificationResult.DigestMismatchMessage);
        Assert.AreEqual(content.LongLength, exception.Result.ActualByteSize);
        Assert.IsTrue(actualDigest.Equals(exception.Result.ActualDigest));
    }

    [TestMethod]
    public async Task VerifyAsync_MissingFile_FailsClosedWithoutPathDisclosure()
    {
        using TemporaryDirectory temporary = new();
        string missingPath = Path.Combine(
            temporary.Path,
            $"missing-{Guid.NewGuid():N}.bin");
        BinaryVerificationRequest request = new(
            "tool.missing",
            missingPath,
            new ContentDigest(EmptySha256),
            0);

        BinaryVerificationException exception =
            await Assert.ThrowsExactlyAsync<BinaryVerificationException>(
                () => new BinaryVerifier().VerifyAsync(request));

        AssertFailure(
            exception,
            BinaryVerificationFailureReason.FileMissing,
            BinaryVerificationResult.FileMissingCode,
            BinaryVerificationResult.FileMissingMessage);
        Assert.IsNull(exception.InnerException);
        Assert.IsNull(exception.Result.ActualByteSize);
        Assert.IsNull(exception.Result.ActualDigest);
        AssertDoesNotContainPath(exception.Message, missingPath, temporary.Path);
        AssertDoesNotContainPath(exception.ToString(), missingPath, temporary.Path);
        AssertDoesNotContainPath(
            JsonSerializer.Serialize(exception.Result),
            missingPath,
            temporary.Path);
        AssertResultHasNoPathProperty();
    }

    [TestMethod]
    public async Task VerifyAsync_Directory_IsRejectedAsNonRegularFile()
    {
        using TemporaryDirectory temporary = new();
        BinaryVerificationRequest request = new(
            "tool.directory",
            temporary.Path,
            new ContentDigest(EmptySha256),
            0);

        BinaryVerificationException exception =
            await Assert.ThrowsExactlyAsync<BinaryVerificationException>(
                () => new BinaryVerifier().VerifyAsync(request));

        AssertFailure(
            exception,
            BinaryVerificationFailureReason.NotRegularFile,
            BinaryVerificationResult.NotRegularFileCode,
            BinaryVerificationResult.NotRegularFileMessage);
    }

    [TestMethod]
    public async Task VerifyAsync_ReparsePoint_IsRejectedWhenSupported()
    {
        using TemporaryDirectory temporary = new();
        byte[] content = Encoding.UTF8.GetBytes("target content");
        string targetPath = temporary.CreateFile("target.bin", content);
        string linkPath = Path.Combine(temporary.Path, "link.bin");

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are not available in this test environment.");
            return;
        }

        try
        {
            if ((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) == 0)
            {
                Assert.Inconclusive("The test file system does not expose symbolic links as reparse points.");
            }

            BinaryVerificationRequest request = new(
                "model.link",
                linkPath,
                ContentDigest.Compute(content),
                content.LongLength);

            BinaryVerificationException exception =
                await Assert.ThrowsExactlyAsync<BinaryVerificationException>(
                    () => new BinaryVerifier().VerifyAsync(request));

            AssertFailure(
                exception,
                BinaryVerificationFailureReason.ReparsePoint,
                BinaryVerificationResult.ReparsePointCode,
                BinaryVerificationResult.ReparsePointMessage);
        }
        finally
        {
            File.Delete(linkPath);
        }
    }

    [TestMethod]
    public async Task VerifyAsync_FileLockedAgainstSharing_IsInaccessible()
    {
        using TemporaryDirectory temporary = new();
        byte[] content = Encoding.UTF8.GetBytes("locked content");
        string filePath = temporary.CreateFile("locked.bin", content);
        BinaryVerificationRequest request = new(
            "tool.locked",
            filePath,
            ContentDigest.Compute(content),
            content.LongLength);
        using FileStream exclusiveHandle = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        BinaryVerificationException exception =
            await Assert.ThrowsExactlyAsync<BinaryVerificationException>(
                () => new BinaryVerifier().VerifyAsync(request));

        AssertFailure(
            exception,
            BinaryVerificationFailureReason.FileInaccessible,
            BinaryVerificationResult.FileInaccessibleCode,
            BinaryVerificationResult.FileInaccessibleMessage);
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    public async Task VerifyAsync_PreCancelledToken_DoesNotTouchFile()
    {
        using TemporaryDirectory temporary = new();
        string missingPath = Path.Combine(temporary.Path, "never-opened.bin");
        BinaryVerificationRequest request = new(
            "tool.cancelled",
            missingPath,
            new ContentDigest(EmptySha256),
            0);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => new BinaryVerifier().VerifyAsync(request, cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public async Task VerifyAsync_LargeFile_CanBeCancelledWithoutLoadingItIntoMemory()
    {
        const long largeFileSize = 128L * 1024 * 1024;
        using TemporaryDirectory temporary = new();
        string filePath = Path.Combine(temporary.Path, "large-model.bin");
        using (FileStream stream = new(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(largeFileSize);
        }

        BinaryVerificationRequest request = new(
            "model.large",
            filePath,
            new ContentDigest(new string('0', 64)),
            largeFileSize);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(1));

        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => new BinaryVerifier().VerifyAsync(request, cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsTrue(File.Exists(filePath));
        Assert.AreEqual(largeFileSize, new FileInfo(filePath).Length);
    }

    [TestMethod]
    public async Task VerifyAsync_WindowsVerificationHandle_BlocksWriteAndDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file sharing semantics are required for this test.");
        }

        const long largeFileSize = 256L * 1024 * 1024;
        using TemporaryDirectory temporary = new();
        string filePath = Path.Combine(temporary.Path, "share-locked-tool.bin");
        using (FileStream stream = new(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(largeFileSize);
        }

        BinaryVerificationRequest request = new(
            "tool.share-lock",
            filePath,
            new ContentDigest(new string('0', 64)),
            largeFileSize);
        using CancellationTokenSource cancellation = new();
        Task<BinaryVerificationResult> verification =
            new BinaryVerifier().VerifyAsync(request, cancellation.Token);

        Assert.IsFalse(
            verification.IsCompleted,
            "The large verification completed before its sharing behavior could be observed.");
        Assert.ThrowsExactly<IOException>(
            () => new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete).Dispose());
        Assert.ThrowsExactly<IOException>(() => File.Delete(filePath));

        cancellation.Cancel();
        OperationCanceledException exception =
            await Assert.ThrowsAsync<OperationCanceledException>(() => verification);

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsTrue(File.Exists(filePath));
    }

    private static void AssertFailure(
        BinaryVerificationException exception,
        BinaryVerificationFailureReason expectedReason,
        string expectedCode,
        string expectedMessage)
    {
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.AreEqual(expectedCode, exception.Code);
        Assert.AreEqual(expectedMessage, exception.Message);
        Assert.IsFalse(exception.Result.IsVerified);
        Assert.AreEqual(expectedReason, exception.Result.Reason);
        Assert.AreEqual(expectedCode, exception.Result.Code);
        Assert.AreEqual(expectedMessage, exception.Result.Message);
        Assert.AreEqual(TimeSpan.Zero, exception.Result.TimestampUtc.Offset);
        AssertResultHasNoPathProperty();
        AssertPropertiesAreGetOnly(typeof(BinaryVerificationResult));
    }

    private static void AssertDoesNotContainPath(
        string value,
        params string[] paths)
    {
        foreach (string path in paths)
        {
            Assert.IsFalse(value.Contains(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertResultHasNoPathProperty()
    {
        Assert.IsFalse(
            typeof(BinaryVerificationResult)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Any(property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertPropertiesAreGetOnly(Type type)
    {
        Assert.IsTrue(
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .All(property => property.SetMethod is null));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"hubradio-binary-verifier-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name, byte[] content)
        {
            string filePath = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(filePath, content);
            return filePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}