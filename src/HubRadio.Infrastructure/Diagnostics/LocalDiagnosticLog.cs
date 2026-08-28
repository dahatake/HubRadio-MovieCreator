using System.Buffers;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace HubRadio.Infrastructure.Diagnostics;

public sealed class LocalDiagnosticLog
{
    private const int SchemaVersion = 1;
    private const int RecordPropertyCount = 12;
    private const int WindowsLockViolation = 33;
    private const int WindowsSharingViolation = 32;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly JavaScriptEncoder JsonEncoder =
        JavaScriptEncoder.Create(UnicodeRanges.All);
    private static readonly object PathGatesSync = new();
    private static readonly Dictionary<string, PathGate> PathGates = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly DiagnosticLogOptions options;
    private readonly TimeProvider timeProvider;

    public LocalDiagnosticLog(
        DiagnosticLogOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task WriteAsync(
        DiagnosticLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] record = SerializeRecord(entry, timeProvider.GetUtcNow());
        if (record.Length > options.MaxTotalBytes)
        {
            throw new DiagnosticLogException();
        }

        try
        {
            using PathGateLease gate = await AcquirePathGateAsync(
                options.FilePath,
                cancellationToken).ConfigureAwait(false);
            await WriteCoreAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The diagnostic log write was cancelled by the caller.",
                cancellationToken);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new DiagnosticLogException();
        }
    }

    private async Task WriteCoreAsync(
        byte[] newRecord,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string directory = Path.GetDirectoryName(options.FilePath)
            ?? throw new IOException("The diagnostic log directory is unavailable.");
        options.ValidateForWrite();
        Directory.CreateDirectory(directory);
        options.ValidateForWrite();

        await using FileStream fileLock = await AcquireFileLockAsync(
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<byte[]> existingRecords = await ReadExistingRecordsAsync(
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<byte[]> retainedRecords = SelectRetainedRecords(
            existingRecords,
            newRecord.Length);

        await ReplaceFileAsync(
            retainedRecords,
            newRecord,
            cancellationToken).ConfigureAwait(false);
        GC.KeepAlive(fileLock);
    }

    private async Task<FileStream> AcquireFileLockAsync(
        CancellationToken cancellationToken)
    {
        string lockPath = options.FilePath + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                stream.SetLength(0);
                return stream;
            }
            catch (IOException exception) when (IsFileLockContention(exception))
            {
                stream?.Dispose();
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    private async Task<IReadOnlyList<byte[]>> ReadExistingRecordsAsync(
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                options.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<byte[]>();
        }

        await using (stream.ConfigureAwait(false))
        {
            long length = stream.Length;
            if (length == 0)
            {
                return Array.Empty<byte[]>();
            }

            long boundedStart = Math.Max(0, length - options.MaxTotalBytes);
            long readStart = boundedStart > 0 ? boundedStart - 1 : 0;
            int requested = checked((int)Math.Min(
                length - readStart,
                options.MaxTotalBytes + 1L));
            byte[] buffer = new byte[requested];
            stream.Position = readStart;

            int totalRead = 0;
            while (totalRead < requested)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, requested - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return ParseCompleteRecords(
                buffer.AsMemory(0, totalRead),
                startsBeforeBound: readStart < boundedStart);
        }
    }

    private static IReadOnlyList<byte[]> ParseCompleteRecords(
        ReadOnlyMemory<byte> bytes,
        bool startsBeforeBound)
    {
        if (bytes.IsEmpty)
        {
            return Array.Empty<byte[]>();
        }

        ReadOnlySpan<byte> span = bytes.Span;
        int start = 0;
        if (startsBeforeBound)
        {
            if (span[0] == (byte)'\n')
            {
                start = 1;
            }
            else
            {
                int firstNewLine = span.IndexOf((byte)'\n');
                if (firstNewLine < 0)
                {
                    return Array.Empty<byte[]>();
                }

                start = firstNewLine + 1;
            }
        }

        int lastNewLine = span.LastIndexOf((byte)'\n');
        if (lastNewLine < start)
        {
            return Array.Empty<byte[]>();
        }

        var records = new List<byte[]>();
        int position = start;
        while (position <= lastNewLine)
        {
            int relativeEnd = span[position..(lastNewLine + 1)].IndexOf((byte)'\n');
            if (relativeEnd < 0)
            {
                break;
            }

            int end = position + relativeEnd;
            ReadOnlySpan<byte> line = span[position..end];
            if (!line.IsEmpty && IsValidRecord(line))
            {
                records.Add(span[position..(end + 1)].ToArray());
            }

            position = end + 1;
        }

        return records;
    }

    private IReadOnlyList<byte[]> SelectRetainedRecords(
        IReadOnlyList<byte[]> existingRecords,
        int newRecordBytes)
    {
        int remaining = options.MaxTotalBytes - newRecordBytes;
        var newestFirst = new List<byte[]>();
        for (int index = existingRecords.Count - 1; index >= 0; index--)
        {
            byte[] record = existingRecords[index];
            if (record.Length > remaining)
            {
                break;
            }

            newestFirst.Add(record);
            remaining -= record.Length;
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    private async Task ReplaceFileAsync(
        IReadOnlyList<byte[]> retainedRecords,
        byte[] newRecord,
        CancellationToken cancellationToken)
    {
        string temporaryPath = options.FilePath + ".tmp";
        try
        {
            File.Delete(temporaryPath);

            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                foreach (byte[] record in retainedRecords)
                {
                    await stream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                }

                await stream.WriteAsync(newRecord, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, options.FilePath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static byte[] SerializeRecord(
        DiagnosticLogEntry entry,
        DateTimeOffset timestamp)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JsonEncoder,
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString(
                "timestampUtc",
                timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteString("severity", GetSeverityName(entry.Severity));
            writer.WriteString("code", entry.Code);
            writer.WriteString("component", GetComponentName(entry.Component));
            writer.WriteString("operation", GetOperationName(entry.Operation));

            if (entry.DurationMilliseconds is long durationMilliseconds)
            {
                writer.WriteNumber("durationMs", durationMilliseconds);
            }
            else
            {
                writer.WriteNull("durationMs");
            }

            if (entry.NativeErrorCode is int nativeErrorCode)
            {
                writer.WriteNumber("nativeErrorCode", nativeErrorCode);
            }
            else
            {
                writer.WriteNull("nativeErrorCode");
            }

            if (entry.ExceptionType is not null)
            {
                writer.WriteString("exceptionType", entry.ExceptionType);
            }
            else
            {
                writer.WriteNull("exceptionType");
            }

            writer.WriteBoolean("exceptionTypeTruncated", entry.ExceptionTypeTruncated);
            writer.WriteBoolean("exceptionTypeRedacted", entry.ExceptionTypeRedacted);
            writer.WriteBoolean("exceptionContentOmitted", entry.ExceptionContentOmitted);
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] record = new byte[buffer.WrittenCount + NewLine.Length];
        buffer.WrittenSpan.CopyTo(record);
        NewLine.CopyTo(record, buffer.WrittenCount);
        return record;
    }

    private static bool IsValidRecord(ReadOnlySpan<byte> line)
    {
        if (line.Contains((byte)'\r'))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != RecordPropertyCount)
            {
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
                !schemaVersion.TryGetInt32(out int schema) ||
                schema != SchemaVersion ||
                !TryReadTimestamp(root) ||
                !TryReadSeverity(root) ||
                !TryReadStableContract(root) ||
                !TryReadNullableNonNegativeInt64(root, "durationMs") ||
                !TryReadNullableInt32(root, "nativeErrorCode") ||
                !TryReadBoolean(root, "exceptionTypeTruncated", out bool typeTruncated) ||
                !TryReadBoolean(root, "exceptionTypeRedacted", out bool typeRedacted) ||
                !TryReadBoolean(root, "exceptionContentOmitted", out bool contentOmitted) ||
                !root.TryGetProperty("exceptionType", out JsonElement exceptionType))
            {
                return false;
            }

            if (exceptionType.ValueKind == JsonValueKind.Null)
            {
                return !typeTruncated && !typeRedacted && !contentOmitted;
            }

            return exceptionType.ValueKind == JsonValueKind.String &&
                contentOmitted &&
                DiagnosticTextSanitizer.IsSafeExceptionType(
                    exceptionType.GetString() ?? string.Empty,
                    DiagnosticLogEntry.MaximumExceptionTypeUtf8Bytes);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestampUtc", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            value.GetString(),
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
    }

    private static bool TryReadSeverity(JsonElement root)
    {
        if (!root.TryGetProperty("severity", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return value.GetString() is "information" or "warning" or "error" or "critical";
    }

    private static bool TryReadStableContract(JsonElement root)
    {
        if (!root.TryGetProperty("code", out JsonElement codeValue) ||
            codeValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("component", out JsonElement componentValue) ||
            componentValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("operation", out JsonElement operationValue) ||
            operationValue.ValueKind != JsonValueKind.String ||
            !TryParseComponent(componentValue.GetString(), out DiagnosticComponent component) ||
            !TryParseOperation(operationValue.GetString(), out DiagnosticOperation operation))
        {
            return false;
        }

        string code = codeValue.GetString() ?? string.Empty;
        return StableErrorCodes.IsDefined(code) &&
            DiagnosticLogEntry.MatchesStableContract(code, component, operation);
    }

    private static bool TryReadNullableNonNegativeInt64(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.Null ||
            (value.TryGetInt64(out long number) && number >= 0);
    }

    private static bool TryReadNullableInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) &&
            (value.ValueKind == JsonValueKind.Null || value.TryGetInt32(out _));
    }

    private static bool TryReadBoolean(
        JsonElement root,
        string propertyName,
        out bool result)
    {
        result = false;
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        result = value.GetBoolean();
        return true;
    }

    private static string GetSeverityName(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Information => "information",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Critical => "critical",
        _ => throw new InvalidOperationException("The diagnostic severity is invalid."),
    };

    private static string GetComponentName(DiagnosticComponent component) => component switch
    {
        DiagnosticComponent.Process => "process",
        DiagnosticComponent.DiagnosticLog => "diagnostic-log",
        DiagnosticComponent.WebView => "webview",
        _ => throw new InvalidOperationException("The diagnostic component is invalid."),
    };

    private static string GetOperationName(DiagnosticOperation operation) => operation switch
    {
        DiagnosticOperation.Start => "start",
        DiagnosticOperation.Write => "write",
        DiagnosticOperation.DetectRuntime => "detect-runtime",
        DiagnosticOperation.Initialize => "initialize",
        _ => throw new InvalidOperationException("The diagnostic operation is invalid."),
    };

    private static bool TryParseComponent(
        string? value,
        out DiagnosticComponent component)
    {
        component = value switch
        {
            "process" => DiagnosticComponent.Process,
            "diagnostic-log" => DiagnosticComponent.DiagnosticLog,
            "webview" => DiagnosticComponent.WebView,
            _ => default,
        };

        return value is "process" or "diagnostic-log" or "webview";
    }

    private static bool TryParseOperation(
        string? value,
        out DiagnosticOperation operation)
    {
        operation = value switch
        {
            "start" => DiagnosticOperation.Start,
            "write" => DiagnosticOperation.Write,
            "detect-runtime" => DiagnosticOperation.DetectRuntime,
            "initialize" => DiagnosticOperation.Initialize,
            _ => default,
        };

        return value is "start" or "write" or "detect-runtime" or "initialize";
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException or
            SecurityException;

    private static bool IsFileLockContention(IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() &&
            nativeError is WindowsSharingViolation or WindowsLockViolation;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException or
                SecurityException)
        {
        }
    }

    private static async Task<PathGateLease> AcquirePathGateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        PathGate gate;
        lock (PathGatesSync)
        {
            if (!PathGates.TryGetValue(path, out gate!))
            {
                gate = new PathGate();
                PathGates.Add(path, gate);
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PathGateLease(path, gate);
        }
        catch
        {
            ReleasePathGateReference(path, gate);
            throw;
        }
    }

    private static void ReleasePathGate(string path, PathGate gate)
    {
        gate.Semaphore.Release();
        ReleasePathGateReference(path, gate);
    }

    private static void ReleasePathGateReference(string path, PathGate gate)
    {
        lock (PathGatesSync)
        {
            gate.Users--;
            if (gate.Users == 0)
            {
                PathGates.Remove(path);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class PathGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class PathGateLease : IDisposable
    {
        private readonly string path;
        private readonly PathGate gate;
        private int disposed;

        public PathGateLease(string path, PathGate gate)
        {
            this.path = path;
            this.gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                ReleasePathGate(path, gate);
            }
        }
    }
}