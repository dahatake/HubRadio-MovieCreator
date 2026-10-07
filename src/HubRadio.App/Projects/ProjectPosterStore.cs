using System.Collections.Concurrent;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Projects;

/// <summary>
/// FR-053: one poster frame (JPEG) per Project, taken from its source video, so the Project
/// list always has a picture even before (or without) a generated thumbnail. Files live at
/// <c>&lt;workspace&gt;\posters\{draftId}.jpg</c>; only the host writes them, and readers
/// receive bytes only after the SHA-256 they asked for is re-verified.
/// </summary>
internal sealed class ProjectPosterStore
{
    internal const int MaximumPosterBytes = 2 * 1024 * 1024;
    internal const int PosterWidth = 640;
    // 8 frames keep a 4K HEVC source near 0.25 s while still skipping a single dark or blurred frame.
    internal const int RepresentativeFrameWindow = 8;
    internal static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(30);

    private readonly string directory;
    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run;
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> inFlight = new(StringComparer.Ordinal);
    // One ffmpeg at a time: opening a list of many older Projects must not start one process per Project.
    private readonly SemaphoreSlim generation = new(1, 1);

    internal ProjectPosterStore(string workspacePath,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>? runProcess = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!Path.IsPathFullyQualified(workspacePath))
        {
            throw new ArgumentException("The workspace path must be absolute.", nameof(workspacePath));
        }

        directory = Path.Combine(workspacePath, "posters");
        run = runProcess ?? new ProcessRunner().RunAsync;
    }

    internal string PathFor(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        return Path.Combine(directory, draftId + ".jpg");
    }

    /// <summary>The poster's SHA-256, or null when there is no readable poster.</summary>
    internal ContentDigest? TryGetDigest(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        byte[]? bytes = TryRead(draftId);
        return bytes is null ? null : ContentDigest.Compute(bytes);
    }

    /// <summary>Returns the poster bytes only when they still match <paramref name="expected"/>.</summary>
    internal byte[]? ReadValidated(EntityId<ProjectCreationDraftIdTag> draftId, ContentDigest expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        byte[]? bytes = TryRead(draftId);
        return bytes is not null && ContentDigest.Compute(bytes) == expected ? bytes : null;
    }

    /// <summary>
    /// Extracts a representative frame with ffmpeg's <c>thumbnail</c> filter. Concurrent calls for
    /// the same Project share one run. Returns false (never throws) when no poster could be made.
    /// </summary>
    internal Task<bool> CreateAsync(EntityId<ProjectCreationDraftIdTag> draftId, string sourcePath,
        string ffmpegPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        string key = draftId.ToString();
        // Task.Run: the caller (a bridge request) never runs file I/O or Process.Start itself.
        Lazy<Task<bool>> work = inFlight.GetOrAdd(key, _ => new Lazy<Task<bool>>(() =>
            Task.Run(() => CreateLimitedAsync(draftId, sourcePath, ffmpegPath, cancellationToken), CancellationToken.None)));
        return AwaitAndForget(key, work);
    }

    internal void Delete(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        try
        {
            File.Delete(PathFor(draftId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A poster left behind is harmless: the media route only serves posters of listed Projects.
        }
    }

    internal static IReadOnlyList<string> BuildArguments(string sourcePath, string outputPath, bool seek) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error",
        .. seek ? new[] { "-ss", "1" } : [],
        "-i", sourcePath,
        "-frames:v", "1",
        "-vf", $"thumbnail={RepresentativeFrameWindow},scale={PosterWidth}:-2",
        "-q:v", "4",
        "-y", outputPath,
    ];

    private async Task<bool> AwaitAndForget(string key, Lazy<Task<bool>> work)
    {
        try
        {
            return await work.Value.ConfigureAwait(false);
        }
        finally
        {
            inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, work));
        }
    }

    private async Task<bool> CreateLimitedAsync(EntityId<ProjectCreationDraftIdTag> draftId, string sourcePath,
        string ffmpegPath, CancellationToken cancellationToken)
    {
        await generation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return TryGetDigest(draftId) is not null
                || await CreateCoreAsync(draftId, sourcePath, ffmpegPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            generation.Release();
        }
    }

    private async Task<bool> CreateCoreAsync(EntityId<ProjectCreationDraftIdTag> draftId, string sourcePath,
        string ffmpegPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(sourcePath) || !File.Exists(ffmpegPath))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            string target = PathFor(draftId);
            string staging = Path.Combine(directory, draftId + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg");
            try
            {
                // Very short clips cannot seek 1 s in; retry from the first frame.
                foreach (bool seek in new[] { true, false })
                {
                    ProcessRunResult result = await run(new ProcessRunRequest(ffmpegPath,
                        BuildArguments(sourcePath, staging, seek), directory, GenerationTimeout), cancellationToken).ConfigureAwait(false);
                    var info = new FileInfo(staging);
                    if (result.ExitCode == 0 && info.Exists && info.Length is > 0 and <= MaximumPosterBytes)
                    {
                        File.Move(staging, target, overwrite: true);
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                File.Delete(staging);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private byte[]? TryRead(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        try
        {
            var info = new FileInfo(PathFor(draftId));
            if (!info.Exists || info.Length is <= 0 or > MaximumPosterBytes
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            return File.ReadAllBytes(info.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
