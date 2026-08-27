using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using HubRadio.Infrastructure.Diagnostics;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class LocalDiagnosticLogTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 8, 28, 12, 34, 56, 123, TimeSpan.Zero);

    [TestMethod]
    public async Task WriteAsync_WritesDeterministicUtf8JsonLine()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "diagnostic.jsonl");

        try
        {
            LocalDiagnosticLog log = CreateLog(path);
            DiagnosticLogEntry entry = new(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                DiagnosticSeverity.Error,
                durationMilliseconds: 42,
                nativeErrorCode: 2);

            await log.WriteAsync(entry);

            byte[] bytes = await File.ReadAllBytesAsync(path);
            string text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            const string expected =
                "{\"schemaVersion\":1,\"timestampUtc\":\"2026-08-28T12:34:56.1230000Z\",\"severity\":\"error\",\"code\":\"process.start_failed\",\"component\":\"process\",\"operation\":\"start\",\"durationMs\":42,\"nativeErrorCode\":2,\"exceptionType\":null,\"exceptionTypeTruncated\":false,\"exceptionTypeRedacted\":false,\"exceptionContentOmitted\":false}\n";

            Assert.AreEqual(expected, text);
            Assert.IsFalse(bytes.AsSpan().StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]));
            Assert.AreEqual((byte)'\n', bytes[^1]);
            Assert.IsFalse(bytes.Contains((byte)'\r'));
            using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_RotatesOnlyAtRecordBoundariesAndEnforcesTotalLimit()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "bounded.jsonl");
        const int maxTotalBytes = DiagnosticLogOptions.MinimumMaxTotalBytes;

        try
        {
            LocalDiagnosticLog log = CreateLog(path, maxTotalBytes);
            for (int index = 0; index < 64; index++)
            {
                await log.WriteAsync(new DiagnosticLogEntry(
                    StableErrorCodes.ProcessStartFailed,
                    DiagnosticComponent.Process,
                    DiagnosticOperation.Start,
                    nativeErrorCode: index));

                Assert.IsTrue(new FileInfo(path).Length <= maxTotalBytes);
            }

            byte[] bytes = await File.ReadAllBytesAsync(path);
            IReadOnlyList<JsonElement> records = ParseRecords(bytes);
            int[] nativeCodes = records
                .Select(record => record.GetProperty("nativeErrorCode").GetInt32())
                .ToArray();

            Assert.IsTrue(bytes.Length <= maxTotalBytes);
            Assert.IsTrue(records.Count > 0);
            Assert.IsTrue(records.Count < 64);
            Assert.AreEqual(63, nativeCodes[^1]);
            Assert.IsTrue(nativeCodes[0] > 0);
            CollectionAssert.AreEqual(
                Enumerable.Range(nativeCodes[0], nativeCodes.Length).ToArray(),
                nativeCodes);
            Assert.IsFalse(File.Exists(path + ".tmp"));
            Assert.AreEqual(0L, new FileInfo(path + ".lock").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_FromException_OmitsTranscriptPathsSecretsAndInjectionText()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "redacted.jsonl");
        const string transcript = "UNIQUE_TRANSCRIPT_SENTENCE_7E77";
        const string bearerToken = "Bearer abcdefghijklmnopqrstuvwxyz012345";
        const string apiKey = "x-api-key=APIKEY-SECRET-123456";
        const string connectionString =
            "Server=local;AccountKey=ACCOUNT-SECRET-123456;Password=PASSWORD-SECRET-123456";
        const string userPath = @"C:\Users\operator\workspace\private\clip.mov";

        try
        {
            Exception failure = CaptureException(
                $"{transcript}\r\nAuthorization: {bearerToken}; {apiKey}; {connectionString}; {userPath}");
            failure.Data["transcript"] = transcript;
            failure.Source = userPath;
            LocalDiagnosticLog log = CreateLog(path);
            DiagnosticLogEntry entry = DiagnosticLogEntry.FromException(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                failure,
                nativeErrorCode: 2);

            await log.WriteAsync(entry);

            string persisted = await File.ReadAllTextAsync(path, Encoding.UTF8);
            foreach (string forbidden in new[]
            {
                transcript,
                "abcdefghijklmnopqrstuvwxyz012345",
                "APIKEY-SECRET-123456",
                "ACCOUNT-SECRET-123456",
                "PASSWORD-SECRET-123456",
                userPath,
                "Authorization",
                "clip.mov",
                "\r",
            })
            {
                Assert.IsFalse(
                    persisted.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Persisted diagnostic data contained forbidden content: {forbidden}");
            }

            JsonElement record = ParseRecords(Encoding.UTF8.GetBytes(persisted)).Single();
            Assert.AreEqual(
                nameof(InvalidOperationException),
                record.GetProperty("exceptionType").GetString());
            Assert.IsTrue(record.GetProperty("exceptionContentOmitted").GetBoolean());
            Assert.IsFalse(record.TryGetProperty("message", out _));
            Assert.IsFalse(record.TryGetProperty("stackTrace", out _));
            Assert.IsFalse(record.TryGetProperty("path", out _));
            Assert.IsFalse(record.TryGetProperty("transcript", out _));
            Assert.IsFalse(record.TryGetProperty("media", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_ExceptionTypeMetadataRedactsSecretsAndWindowsPaths()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "type-redaction.jsonl");
        const string secret = "TYPE-SECRET-123456789";
        string typeName =
            $"Authorization: Bearer {secret} x-api-key={secret} " +
            $"ConnectionString=Server=local;AccountKey={secret}; " +
            @"C:\Users\operator\workspace\private";

        try
        {
            LocalDiagnosticLog log = CreateLog(path);
            Exception exception = CreateDynamicException(typeName);

            await log.WriteAsync(DiagnosticLogEntry.FromException(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                exception));

            string persisted = await File.ReadAllTextAsync(path, Encoding.UTF8);
            JsonElement record = ParseRecords(Encoding.UTF8.GetBytes(persisted)).Single();
            string persistedType = record.GetProperty("exceptionType").GetString()
                ?? throw new InvalidOperationException("The exception type was not persisted.");

            Assert.IsTrue(record.GetProperty("exceptionTypeRedacted").GetBoolean());
            Assert.IsFalse(persistedType.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(persistedType.Contains("operator", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(persistedType.Contains("workspace", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(persisted.Contains(@"C:\Users", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_TruncatesExceptionTypeAtUtf8ByteLimit()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "utf8.jsonl");

        try
        {
            Exception exception = CreateDynamicException(new string('A', 256));
            Assert.IsTrue(
                Encoding.UTF8.GetByteCount(exception.GetType().Name) >
                DiagnosticLogEntry.MaximumExceptionTypeUtf8Bytes);
            LocalDiagnosticLog log = CreateLog(path);

            await log.WriteAsync(DiagnosticLogEntry.FromException(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                exception));

            byte[] bytes = await File.ReadAllBytesAsync(path);
            _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            JsonElement record = ParseRecords(bytes).Single();
            string exceptionType = record.GetProperty("exceptionType").GetString()
                ?? throw new InvalidOperationException("The exception type was not persisted.");

            Assert.AreEqual(
                DiagnosticLogEntry.MaximumExceptionTypeUtf8Bytes,
                Encoding.UTF8.GetByteCount(exceptionType));
            Assert.IsTrue(exceptionType.All(character => character == 'A'));
            Assert.IsFalse(exceptionType.Contains('\uFFFD'));
            Assert.IsTrue(record.GetProperty("exceptionTypeTruncated").GetBoolean());
            Assert.IsFalse(record.GetProperty("exceptionTypeRedacted").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_ExceptionTypeMetadataRedactsUnicodeHomoglyphs()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "unicode-type-redaction.jsonl");
        const string fullWidthApi = "ＡＰＩ";
        const string cyrillicA = "А";
        const string nonAsciiSecret = "秘密";

        try
        {
            Exception exception = CreateDynamicException(
                $"DiagnosticException_{fullWidthApi}_{cyrillicA}PI_{nonAsciiSecret}");
            LocalDiagnosticLog log = CreateLog(path);

            await log.WriteAsync(DiagnosticLogEntry.FromException(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                exception));

            JsonElement record = ParseRecords(await File.ReadAllBytesAsync(path)).Single();
            string exceptionType = record.GetProperty("exceptionType").GetString()
                ?? throw new InvalidOperationException("The exception type was not persisted.");

            Assert.IsTrue(record.GetProperty("exceptionTypeRedacted").GetBoolean());
            Assert.IsTrue(exceptionType.All(character => character <= 0x7f));
            Assert.IsFalse(exceptionType.Contains(fullWidthApi, StringComparison.Ordinal));
            Assert.IsFalse(exceptionType.Contains(cyrillicA, StringComparison.Ordinal));
            Assert.IsFalse(exceptionType.Contains(nonAsciiSecret, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_ConcurrentWritersDoNotLoseOrCorruptRecords()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "concurrent.jsonl");
        const int writeCount = 200;

        try
        {
            LocalDiagnosticLog[] logs =
            [
                CreateLog(path),
                CreateLog(path),
                CreateLog(path),
                CreateLog(path),
            ];
            Task[] writes = Enumerable.Range(0, writeCount)
                .Select(index => logs[index % logs.Length].WriteAsync(
                    new DiagnosticLogEntry(
                        StableErrorCodes.ProcessStartFailed,
                        DiagnosticComponent.Process,
                        DiagnosticOperation.Start,
                        nativeErrorCode: index)))
                .ToArray();

            await Task.WhenAll(writes);

            byte[] bytes = await File.ReadAllBytesAsync(path);
            IReadOnlyList<JsonElement> records = ParseRecords(bytes);
            int[] nativeCodes = records
                .Select(record => record.GetProperty("nativeErrorCode").GetInt32())
                .Order()
                .ToArray();

            Assert.AreEqual(writeCount, records.Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, writeCount).ToArray(), nativeCodes);
            Assert.IsTrue(new FileInfo(path).Length <= DiagnosticLogOptions.DefaultMaxTotalBytes);
            Assert.AreEqual(0L, new FileInfo(path + ".lock").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_PreCancelledCallerDoesNotCreateLog()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "cancelled.jsonl");

        try
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            LocalDiagnosticLog log = CreateLog(path);

            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                    () => log.WriteAsync(
                        new DiagnosticLogEntry(
                            StableErrorCodes.ProcessStartFailed,
                            DiagnosticComponent.Process,
                            DiagnosticOperation.Start),
                        cancellation.Token));

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsFalse(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_CancellationWhileWaitingForFileLockUsesCallerToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The cross-process lock contention assertion is Windows-specific.");
        }

        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "lock-cancelled.jsonl");

        try
        {
            await using FileStream competingLock = new(
                path + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using CancellationTokenSource cancellation = new();
            LocalDiagnosticLog log = CreateLog(path);
            Task write = log.WriteAsync(
                new DiagnosticLogEntry(
                    StableErrorCodes.ProcessStartFailed,
                    DiagnosticComponent.Process,
                    DiagnosticOperation.Start),
                cancellation.Token);

            await Task.Delay(50);
            cancellation.Cancel();
            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => write);

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsFalse(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_StorageFailureUsesStableCodeWithoutPathOrInnerException()
    {
        string directory = CreateTemporaryDirectory();
        string blockingFile = Path.Combine(directory, "not-a-directory");
        string path = Path.Combine(blockingFile, "diagnostic.jsonl");

        try
        {
            await File.WriteAllTextAsync(blockingFile, "block");
            LocalDiagnosticLog log = CreateLog(path);

            DiagnosticLogException exception =
                await Assert.ThrowsExactlyAsync<DiagnosticLogException>(
                    () => log.WriteAsync(new DiagnosticLogEntry(
                        StableErrorCodes.ProcessStartFailed,
                        DiagnosticComponent.Process,
                        DiagnosticOperation.Start)));

            Assert.AreEqual(StableErrorCodes.DiagnosticLogWriteFailed, exception.Code);
            Assert.IsNull(exception.InnerException);
            Assert.IsFalse(exception.Message.Contains(path, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(exception.Message.Contains(blockingFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_DropsMalformedTailAndLeavesOnlyParseableRecords()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "repair.jsonl");

        try
        {
            LocalDiagnosticLog log = CreateLog(path);
            await log.WriteAsync(new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                nativeErrorCode: 1));
            await File.AppendAllTextAsync(path, "{\"incomplete\":", Encoding.UTF8);

            await log.WriteAsync(new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                nativeErrorCode: 2));

            IReadOnlyList<JsonElement> records = ParseRecords(await File.ReadAllBytesAsync(path));
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(1, records[0].GetProperty("nativeErrorCode").GetInt32());
            Assert.AreEqual(2, records[1].GetProperty("nativeErrorCode").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalDiagnosticLog CreateLog(
        string path,
        int maxTotalBytes = DiagnosticLogOptions.DefaultMaxTotalBytes) =>
        new(
            new DiagnosticLogOptions(path, maxTotalBytes),
            new FixedTimeProvider(FixedTimestamp));

    private static IReadOnlyList<JsonElement> ParseRecords(byte[] bytes)
    {
        string text = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
        Assert.IsTrue(text.EndsWith('\n'));
        Assert.IsFalse(text.Contains('\r'));

        var records = new List<JsonElement>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            records.Add(document.RootElement.Clone());
        }

        return records;
    }

    private static Exception CaptureException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static Exception CreateDynamicException(string typeName)
    {
        AssemblyName assemblyName = new($"DiagnosticTests{Guid.NewGuid():N}");
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule(assemblyName.Name!);
        TypeBuilder type = module.DefineType(
            typeName,
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(Exception));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        Type created = type.CreateType()
            ?? throw new InvalidOperationException("The dynamic exception type was not created.");
        return (Exception)(Activator.CreateInstance(created)
            ?? throw new InvalidOperationException("The dynamic exception was not created."));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"hubradio-diagnostic-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}