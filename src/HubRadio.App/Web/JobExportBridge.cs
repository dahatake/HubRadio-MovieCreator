using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Web;

/// <summary>FR-132: the kinds of deliverables that can be saved from a Job. Wire names are camelCase.</summary>
internal enum JobExportKind
{
    LongClean,
    LongBurnIn,
    ShortBurnIn,
    ThumbnailLandscape,
    ThumbnailVertical,
    ShortClean,
}

internal enum JobExportFailureReason
{
    Missing,
    NotWritable,
    DiskFull,
    Unavailable,
}

/// <summary>One deliverable of a Job. <paramref name="Open"/> returns null when the deliverable is gone.</summary>
internal sealed record JobExportItem(
    JobExportKind Kind,
    string FileKindLabel,
    string Extension,
    Func<CancellationToken, Task<Stream?>> Open);

internal sealed record JobExportSource(string ProjectName, DateTimeOffset JobCreatedAt, IReadOnlyList<JobExportItem> Items);

internal sealed record JobExportSavedFile(JobExportKind Kind, string FileName);

internal sealed record JobExportFailure(JobExportKind Kind, JobExportFailureReason Reason);

internal sealed record JobExportOutcome(
    IReadOnlyList<JobExportSavedFile> Saved,
    IReadOnlyList<JobExportFailure> Failures);

internal delegate Task<JobExportSource?> JobExportSourceResolver(
    EntityId<ProjectIdTag> projectId,
    EntityId<SourceOnlyExecutionIdTag> executionId,
    CancellationToken cancellationToken);

internal static class JobExportBridgeProtocol
{
    internal const int MaximumDestinationLength = 260;
    internal const int MaximumJobNameLength = 40;
    private static readonly string[] Keys = ["projectId", "executionId", "destination", "jobName"];

    internal static bool HasValidParameters(JsonElement parameters)
    {
        if (!SettingsBridgeProtocol.HasExactProperties(parameters, Keys) ||
            !SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("projectId")) ||
            !SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("executionId")))
        {
            return false;
        }

        JsonElement destination = parameters.GetProperty("destination");
        if (destination.ValueKind != JsonValueKind.String ||
            destination.GetString() is not { Length: > 0 and <= MaximumDestinationLength } path ||
            path.Any(char.IsControl))
        {
            return false;
        }

        JsonElement jobName = parameters.GetProperty("jobName");
        return jobName.ValueKind == JsonValueKind.Null ||
            (jobName.ValueKind == JsonValueKind.String &&
             jobName.GetString() is { Length: > 0 and <= MaximumJobNameLength });
    }

    internal static string WireName(JobExportKind kind) => kind switch
    {
        JobExportKind.LongClean => "longClean",
        JobExportKind.LongBurnIn => "longBurnIn",
        JobExportKind.ShortBurnIn => "shortBurnIn",
        JobExportKind.ShortClean => "shortClean",
        JobExportKind.ThumbnailLandscape => "thumbnailLandscape",
        JobExportKind.ThumbnailVertical => "thumbnailVertical",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string WireName(JobExportFailureReason reason) => reason switch
    {
        JobExportFailureReason.Missing => "missing",
        JobExportFailureReason.NotWritable => "notWritable",
        JobExportFailureReason.DiskFull => "diskFull",
        JobExportFailureReason.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

internal static class JobExportBridgeJson
{
    internal static string Unavailable(BridgeRequest request) => Status(request, "unavailable");

    internal static string Status(BridgeRequest request, string status, string? destinationReason = null) =>
        Write(request, status, destinationReason, [], []);

    internal static string Completed(BridgeRequest request, JobExportOutcome outcome) =>
        Write(request, "completed", null, outcome.Saved, outcome.Failures);

    private static string Write(
        BridgeRequest request,
        string status,
        string? destinationReason,
        IReadOnlyList<JobExportSavedFile> saved,
        IReadOnlyList<JobExportFailure> failures) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            if (destinationReason is null) writer.WriteNull("destinationReason"); else writer.WriteString("destinationReason", destinationReason);
            writer.WriteNumber("savedCount", saved.Count);
            writer.WriteStartArray("files");
            foreach (JobExportSavedFile file in saved)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", JobExportBridgeProtocol.WireName(file.Kind));
                writer.WriteString("fileName", file.FileName);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("failures");
            foreach (JobExportFailure failure in failures)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", JobExportBridgeProtocol.WireName(failure.Kind));
                writer.WriteString("reason", JobExportBridgeProtocol.WireName(failure.Reason));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
}

/// <summary>
/// FR-132: copies deliverables into a folder. An existing file is never opened for writing: each file is created with
/// <see cref="FileMode.CreateNew"/> and, when the name is taken, a numbered name is used. Nothing but the chosen folder
/// is written, and no path, exception text or SID is returned.
/// </summary>
internal static class JobExportWriter
{
    internal const int MaximumProjectNameLength = 80;
    internal const int MaximumJobLabelLength = 40;
    internal const int MaximumSequence = 999;
    private const int ErrorDiskFull = 112;
    private const int ErrorHandleDiskFull = 39;

    /// <summary>Returns null when the folder can receive files; otherwise notFound, notFolder or notWritable.</summary>
    internal static string? CheckDestination(string destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destination) ||
                !Path.IsPathFullyQualified(destination) ||
                destination.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                destination.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                destination.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return "notFound";
            }

            string full = Path.GetFullPath(destination);
            if (File.Exists(full))
            {
                return "notFolder";
            }

            if (!Directory.Exists(full))
            {
                return "notFound";
            }

            // Prove the folder accepts new files without leaving anything behind.
            string probe = Path.Combine(full, ".hubradio-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception)
        {
            return "notWritable";
        }
    }

    internal static async Task<JobExportOutcome> WriteAsync(
        string destination,
        string projectName,
        string jobLabel,
        IReadOnlyList<JobExportItem> items,
        CancellationToken cancellationToken)
    {
        string folder = Path.GetFullPath(destination);
        var saved = new List<JobExportSavedFile>();
        var failures = new List<JobExportFailure>();
        foreach (JobExportItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream? source;
            try
            {
                source = await item.Open(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                source = null;
            }

            if (source is null)
            {
                failures.Add(new JobExportFailure(item.Kind, JobExportFailureReason.Missing));
                continue;
            }

            await using (source.ConfigureAwait(false))
            {
                string? name = await SaveAsync(folder, projectName, jobLabel, item, source, failures, cancellationToken)
                    .ConfigureAwait(false);
                if (name is not null)
                {
                    saved.Add(new JobExportSavedFile(item.Kind, name));
                }
            }
        }

        return new JobExportOutcome(saved, failures);
    }

    private static async Task<string?> SaveAsync(
        string folder,
        string projectName,
        string jobLabel,
        JobExportItem item,
        Stream source,
        List<JobExportFailure> failures,
        CancellationToken cancellationToken)
    {
        string baseName = BuildBaseName(projectName, jobLabel, item.FileKindLabel);
        FileStream? target = null;
        string? path = null;
        try
        {
            for (int sequence = 1; sequence <= MaximumSequence && target is null; sequence++)
            {
                string fileName = sequence == 1
                    ? baseName + item.Extension
                    : $"{baseName} ({sequence}){item.Extension}";
                string candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    target = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    path = candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Another writer took this name between the check and the create: use the next number.
                }
            }

            if (target is null || path is null)
            {
                failures.Add(new JobExportFailure(item.Kind, JobExportFailureReason.NotWritable));
                return null;
            }

            await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
            target = null;
            return Path.GetFileName(path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DiscardAsync(target, path).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DiscardAsync(target, path).ConfigureAwait(false);
            failures.Add(new JobExportFailure(item.Kind, Classify(exception)));
            return null;
        }
    }

    private static async Task DiscardAsync(FileStream? target, string? path)
    {
        try
        {
            if (target is not null)
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }

            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A partial file that cannot be removed is left as is; the failure is still reported.
        }
    }

    internal static JobExportFailureReason Classify(Exception exception) => exception switch
    {
        IOException io when (io.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull => JobExportFailureReason.DiskFull,
        UnauthorizedAccessException or IOException or System.Security.SecurityException or NotSupportedException =>
            JobExportFailureReason.NotWritable,
        _ => JobExportFailureReason.Unavailable,
    };

    /// <summary>Project name, Job name (or creation time) and kind, made safe for a Windows file name.</summary>
    internal static string BuildBaseName(string projectName, string jobLabel, string kindLabel) =>
        string.Join('_', Sanitize(projectName, MaximumProjectNameLength, "Project"),
            Sanitize(jobLabel, MaximumJobLabelLength, "Job"), Sanitize(kindLabel, 40, "成果物"));

    internal static string JobLabel(string? jobName, DateTimeOffset createdAt) =>
        string.IsNullOrWhiteSpace(jobName)
            ? createdAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            : jobName;

    private static string Sanitize(string value, int maximumLength, string fallback)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        string text = builder.ToString().Trim().TrimEnd('.', ' ');
        if (text.Length > maximumLength)
        {
            text = text[..maximumLength].TrimEnd('.', ' ');
            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }
        }

        return text.Length == 0 ? fallback : text;
    }
}

/// <summary>
/// FR-132. Saves the videos and thumbnails of one Job into a folder the user chose. Requires the same permission as
/// composition export; without it nothing is written. The folder arrives as text (typed, or chosen with the FR-064
/// dialog), is checked before any file is written, and never appears in a response.
/// </summary>
internal sealed class JobExportBridgeHandler
{
    private readonly Func<bool> isAuthorized;
    private readonly JobExportSourceResolver resolver;
    private int running;

    internal JobExportBridgeHandler(Func<bool> isAuthorized, JobExportSourceResolver resolver)
    {
        this.isAuthorized = isAuthorized ?? throw new ArgumentNullException(nameof(isAuthorized));
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    internal async Task<string> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != BridgeMethod.JobExport)
        {
            throw new ArgumentException("A job export request is required.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAuthorized())
        {
            return JobExportBridgeJson.Status(request, "forbidden");
        }

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return JobExportBridgeJson.Status(request, "busy");
        }

        try
        {
            if (!JobExportBridgeProtocol.HasValidParameters(request.Parameters))
            {
                return JobExportBridgeJson.Status(request, "invalid");
            }

            JsonElement parameters = request.Parameters;
            string destination = parameters.GetProperty("destination").GetString()!;
            string? destinationProblem = JobExportWriter.CheckDestination(destination);
            if (destinationProblem is not null)
            {
                return JobExportBridgeJson.Status(request, "destinationInvalid", destinationProblem);
            }

            var projectId = EntityId<ProjectIdTag>.Parse(parameters.GetProperty("projectId").GetString()!);
            var executionId = EntityId<SourceOnlyExecutionIdTag>.Parse(parameters.GetProperty("executionId").GetString()!);
            JobExportSource? source = await resolver(projectId, executionId, cancellationToken).ConfigureAwait(true);
            if (source is null)
            {
                return JobExportBridgeJson.Status(request, "notFound");
            }

            string? jobName = parameters.GetProperty("jobName").GetString();
            JobExportOutcome outcome = await JobExportWriter.WriteAsync(
                destination,
                source.ProjectName,
                JobExportWriter.JobLabel(jobName, source.JobCreatedAt),
                source.Items,
                cancellationToken).ConfigureAwait(true);
            return JobExportBridgeJson.Completed(request, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return JobExportBridgeJson.Unavailable(request);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private bool IsAuthorized()
    {
        try
        {
            return isAuthorized();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>FR-132: builds the real handler from the stored results. Files are read through the validated artifact store.</summary>
internal static class JobExportSources
{
    private static readonly (SourceOnlyResultRole Role, JobExportKind Kind, string Label)[] Order =
    [
        (SourceOnlyResultRole.LandscapeClean, JobExportKind.LongClean, "長尺クリーン"),
        (SourceOnlyResultRole.LandscapeBurnIn, JobExportKind.LongBurnIn, "長尺焼き込み"),
        (SourceOnlyResultRole.VerticalClean, JobExportKind.ShortClean, "短尺クリーン"),
        (SourceOnlyResultRole.VerticalBurnIn, JobExportKind.ShortBurnIn, "短尺焼き込み"),
        (SourceOnlyResultRole.LandscapeThumbnail, JobExportKind.ThumbnailLandscape, "サムネイル"),
        (SourceOnlyResultRole.VerticalThumbnail, JobExportKind.ThumbnailVertical, "サムネイル縦型"),
    ];

    internal static JobExportBridgeHandler CreateHandler(
        SourceOnlyExecutionStore executions,
        SourceOnlyResultService results,
        ArtifactStore artifacts,
        string workspacePath,
        Func<string> currentActorSid,
        AuthorizationService authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(currentActorSid);
        return new JobExportBridgeHandler(
            () => authorization.CanPerform(currentActorSid(), AuthorizationOperation.ExportComposition),
            (projectId, executionId, token) => ResolveAsync(
                executions, results, artifacts, workspacePath, projectId, executionId, token));
    }

    private static async Task<JobExportSource?> ResolveAsync(
        SourceOnlyExecutionStore executions,
        SourceOnlyResultService results,
        ArtifactStore artifacts,
        string workspacePath,
        EntityId<ProjectIdTag> projectId,
        EntityId<SourceOnlyExecutionIdTag> executionId,
        CancellationToken cancellationToken)
    {
        StoredSourceOnlyExecution? execution = executions.Get(executionId);
        if (execution is null || execution.Request.ProjectId != projectId || execution.ResultJson is null)
        {
            return null;
        }

        SourceOnlyResultLoadResult loaded = await results.LoadAsync(execution, cancellationToken).ConfigureAwait(false);
        if (!loaded.Validation.IsValid)
        {
            return null;
        }

        var items = new List<JobExportItem>();
        foreach ((SourceOnlyResultRole role, JobExportKind kind, string label) in Order)
        {
            SourceOnlyResultArtifact[] matching = loaded.Result.Artifacts
                .Where(artifact => artifact.Role == role && artifact.FailureCode is null &&
                    artifact.Artifact.Kind == ArtifactKind.Render)
                .ToArray();
            for (int index = 0; index < matching.Length; index++)
            {
                SourceOnlyResultArtifact artifact = matching[index];
                string fileLabel = matching.Length > 1 ? label + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : label;
                items.Add(new JobExportItem(
                    kind,
                    fileLabel,
                    artifact.IsVideo ? ".mp4" : ".png",
                    token => OpenAsync(artifacts, projectId, artifact, token)));
            }
        }

        string projectName;
        try
        {
            projectName = ProjectFolders.GetFolderName(workspacePath, projectId.ToString());
        }
        catch (Exception)
        {
            projectName = projectId.ToString();
        }

        return new JobExportSource(projectName, execution.Request.CreatedAt, items);
    }

    private static async Task<Stream?> OpenAsync(
        ArtifactStore artifacts,
        EntityId<ProjectIdTag> projectId,
        SourceOnlyResultArtifact artifact,
        CancellationToken cancellationToken)
    {
        ValidatedArtifactReadStream? held = null;
        try
        {
            held = await artifacts.OpenValidatedReadAsync(artifact.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (held.Id != artifact.ArtifactId || held.ProjectId != projectId || held.Kind != ArtifactKind.Render ||
                held.ContentDigest != artifact.ArtifactDigest || held.ByteSize != artifact.ByteSize)
            {
                await held.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            Stream result = held;
            held = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
