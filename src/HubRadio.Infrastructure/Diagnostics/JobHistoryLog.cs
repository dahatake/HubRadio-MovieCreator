using System.Globalization;
using System.Security;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Infrastructure.Database;

namespace HubRadio.Infrastructure.Diagnostics;

/// <summary>
/// FR-049: writes one sanitized JSON file per job to the workspace "job" folder after each
/// committed job transition. History is best effort and never changes the job operation.
/// </summary>
public sealed class JobHistoryLog
{
    public const string FolderName = "job";
    public const string Schema = "hubradio.job-history.v1";
    public const int DefaultMaxFiles = 1000;
    public const int MaximumExceptionTypeUtf8Bytes = DiagnosticLogEntry.MaximumExceptionTypeUtf8Bytes;

    private const string FilePrefix = "job-";
    private const string FileExtension = ".json";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string FileNameTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int MaximumRememberedExceptionTypes = 1024;

    // All instances share one gate so two logs pointing at the same folder still serialize.
    private static readonly object WriteGate = new();
    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    private readonly TimeProvider timeProvider;
    private readonly object exceptionTypesSync = new();
    private readonly Dictionary<EntityId<JobAttemptIdTag>, string> exceptionTypes = [];
    private readonly Queue<EntityId<JobAttemptIdTag>> exceptionTypeOrder = new();

    public JobHistoryLog(
        string directoryPath,
        string? applicationVersion,
        int maxFiles = DefaultMaxFiles,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Path.IsPathFullyQualified(directoryPath))
        {
            throw new ArgumentException("The job history folder must be an absolute path.", nameof(directoryPath));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        ApplicationVersion = string.IsNullOrWhiteSpace(applicationVersion) ? null : applicationVersion;
        MaxFiles = maxFiles;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string DirectoryPath { get; }

    public string? ApplicationVersion { get; }

    public int MaxFiles { get; }

    /// <summary>Creates the history for the workspace's fixed "job" folder.</summary>
    public static JobHistoryLog ForWorkspace(string workspacePath, string? applicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return new JobHistoryLog(Path.Combine(workspacePath, FolderName), applicationVersion);
    }

    public static string GetFileName(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return string.Concat(
            FilePrefix,
            job.CreatedAt.ToUniversalTime().ToString(FileNameTimestampFormat, CultureInfo.InvariantCulture),
            "-",
            job.Id.ToString(),
            FileExtension);
    }

    /// <summary>Remembers only the sanitized short type name; never the message or stack.</summary>
    internal void NoteExceptionType(EntityId<JobAttemptIdTag> attemptId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(attemptId);
        ArgumentNullException.ThrowIfNull(exception);
        string typeName = DiagnosticTextSanitizer.SanitizeExceptionType(
            exception.GetType().Name,
            MaximumExceptionTypeUtf8Bytes).Value;
        lock (exceptionTypesSync)
        {
            if (!exceptionTypes.ContainsKey(attemptId))
            {
                exceptionTypeOrder.Enqueue(attemptId);
            }

            exceptionTypes[attemptId] = typeName;
            while (exceptionTypeOrder.Count > MaximumRememberedExceptionTypes)
            {
                exceptionTypes.Remove(exceptionTypeOrder.Dequeue());
            }
        }
    }

    /// <summary>Returns false when the history could not be written; never throws for storage failures.</summary>
    internal bool Record(Func<StoredJobAggregate?> readLatest)
    {
        ArgumentNullException.ThrowIfNull(readLatest);
        try
        {
            lock (WriteGate)
            {
                // Read after taking the gate so the last writer always reflects the latest commit.
                StoredJobAggregate? aggregate = readLatest();
                if (aggregate is null)
                {
                    return false;
                }

                string fileName = GetFileName(aggregate.Job);
                string directory = PrepareDirectory();
                string path = Path.Combine(directory, fileName);
                byte[] content = Serialize(aggregate);
                WriteAtomically(path, content);
                ApplyRetention(directory, fileName);
                return true;
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private string PrepareDirectory()
    {
        string? parent = Path.GetDirectoryName(DirectoryPath);
        if (parent is not null && Directory.Exists(parent))
        {
            RejectReparsePoints(parent);
        }

        if (File.Exists(DirectoryPath))
        {
            throw new IOException("The job history path is not a folder.");
        }

        if (!Directory.Exists(DirectoryPath))
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        RejectReparsePoints(DirectoryPath);
        return DirectoryPath;
    }

    private static void RejectReparsePoints(string directoryPath)
    {
        for (DirectoryInfo? directory = new(directoryPath); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The job history path must not contain a link.");
            }
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        string temporaryPath = path + ".tmp";
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The job history file must not be a link.");
        }

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private void ApplyRetention(string directory, string currentFileName)
    {
        string[] historyFiles = Directory
            .EnumerateFiles(directory, FilePrefix + "*" + FileExtension, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsHistoryFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        int excess = historyFiles.Length - MaxFiles;
        foreach (string fileName in historyFiles)
        {
            if (excess <= 0)
            {
                break;
            }

            if (string.Equals(fileName, currentFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDelete(Path.Combine(directory, fileName));
            excess--;
        }
    }

    // job-yyyyMMddTHHmmssZ-{guid}.json; anything else is left untouched.
    private static bool IsHistoryFileName(string fileName)
    {
        const int timestampLength = 16;
        const int guidLength = 36;
        int expectedLength = FilePrefix.Length + timestampLength + 1 + guidLength + FileExtension.Length;
        if (fileName.Length != expectedLength ||
            !fileName.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(FileExtension, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> timestamp = fileName.AsSpan(FilePrefix.Length, timestampLength);
        ReadOnlySpan<char> guid = fileName.AsSpan(FilePrefix.Length + timestampLength + 1, guidLength);
        return fileName[FilePrefix.Length + timestampLength] == '-' &&
            DateTime.TryParseExact(
                timestamp,
                FileNameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out _) &&
            Guid.TryParseExact(guid, "D", out _);
    }

    private byte[] Serialize(StoredJobAggregate aggregate)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JsonEncoder,
        }))
        {
            Job job = aggregate.Job;
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("recordedAt", FormatTimestamp(timeProvider.GetUtcNow()));
            WriteNullableString(writer, "applicationVersion", ApplicationVersion);

            writer.WritePropertyName("job");
            writer.WriteStartObject();
            writer.WriteString("id", job.Id.ToString());
            writer.WriteString("projectId", job.ProjectId.ToString());
            writer.WriteString("kind", job.Kind);
            writer.WriteString("state", job.State.ToString());
            writer.WriteString("createdAt", FormatTimestamp(job.CreatedAt));
            writer.WriteString("updatedAt", FormatTimestamp(job.UpdatedAt));
            writer.WriteNumber("revision", aggregate.Revision);
            writer.WriteNumber("currentAttemptNumber", job.CurrentAttemptNumber);
            writer.WriteEndObject();

            writer.WritePropertyName("attempts");
            writer.WriteStartArray();
            foreach (JobAttempt attempt in aggregate.Attempts.OrderBy(static attempt => attempt.SequenceNumber))
            {
                WriteAttempt(writer, attempt, aggregate);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private void WriteAttempt(Utf8JsonWriter writer, JobAttempt attempt, StoredJobAggregate aggregate)
    {
        JobProgress[] progress = aggregate.Progress
            .Where(item => item.JobAttemptId == attempt.Id)
            .OrderBy(static item => item.RecordedAt)
            .ToArray();
        JobHeartbeat[] heartbeats = aggregate.Heartbeats
            .Where(item => item.JobAttemptId == attempt.Id)
            .OrderBy(static item => item.RecordedAt)
            .ToArray();

        writer.WriteStartObject();
        writer.WriteNumber("sequenceNumber", attempt.SequenceNumber);
        writer.WriteString("id", attempt.Id.ToString());
        writer.WriteString("state", attempt.State.ToString());
        writer.WriteString("queuedAt", FormatTimestamp(attempt.QueuedAt));
        WriteNullableTimestamp(writer, "startedAt", attempt.StartedAt);
        WriteNullableTimestamp(writer, "completedAt", attempt.CompletedAt);
        if (attempt.StartedAt is DateTimeOffset started && attempt.CompletedAt is DateTimeOffset completed)
        {
            writer.WriteNumber("durationMilliseconds", (completed - started).Ticks / TimeSpan.TicksPerMillisecond);
        }
        else
        {
            writer.WriteNull("durationMilliseconds");
        }

        WriteNullableString(writer, "errorCode", attempt.ErrorCode);
        WriteNullableString(writer, "exceptionType", FindExceptionType(attempt));

        if (progress.Length == 0)
        {
            writer.WriteNull("lastProgress");
        }
        else
        {
            JobProgress last = progress[^1];
            writer.WritePropertyName("lastProgress");
            writer.WriteStartObject();
            writer.WriteNumber("completedUnits", last.CompletedUnits);
            if (last.TotalUnits is long total)
            {
                writer.WriteNumber("totalUnits", total);
            }
            else
            {
                writer.WriteNull("totalUnits");
            }

            // Phase is a code-defined stage identifier; the free-text message is never written.
            WriteNullableString(writer, "phase", last.Phase);
            writer.WriteString("recordedAt", FormatTimestamp(last.RecordedAt));
            writer.WriteEndObject();
        }

        writer.WriteNumber("progressCount", progress.Length);
        writer.WriteNumber("heartbeatCount", heartbeats.Length);
        WriteNullableTimestamp(writer, "lastHeartbeatAt", heartbeats.Length == 0 ? null : heartbeats[^1].RecordedAt);
        writer.WriteEndObject();
    }

    private string? FindExceptionType(JobAttempt attempt)
    {
        if (attempt.State != JobState.Failed ||
            attempt.ErrorCode is not ("job.execution_failed" or "job.execution_timed_out"))
        {
            return null;
        }

        lock (exceptionTypesSync)
        {
            return exceptionTypes.TryGetValue(attempt.Id, out string? value) ? value : null;
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is DateTimeOffset timestamp)
        {
            writer.WriteString(name, FormatTimestamp(timestamp));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            NotSupportedException or ArgumentException or InvalidOperationException or
            JsonException or Microsoft.Data.Sqlite.SqliteException;
}
