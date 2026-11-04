using System.Collections.Concurrent;
using System.Globalization;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Projects;

internal enum ProjectProxyStatus
{
    Ready,
    Missing,
    Generating,
    Failed,
}

/// <summary>The proxy's state for the settings screen. Never carries a path or exception text.</summary>
internal sealed record ProjectProxyState(ProjectProxyStatus Status, string? Reason, string? Version);

/// <summary>The trusted host inputs of one proxy run: the Project's source video and the verified ffmpeg.</summary>
internal sealed record ProxySourceInfo(string SourcePath, string FfmpegPath);

/// <summary>
/// FR-144: a low-resolution proxy of the Project's source video, so the settings screen can play it.
/// The proxy is made in the background after the source is chosen; it lives in the Project folder
/// (<c>&lt;project&gt;\proxy\proxy-360p.mp4</c>) once the Project is created, and in
/// <c>&lt;workspace&gt;\proxies\{draftId}\</c> until then. The source video is only read.
/// A missing, running or failed proxy never fails the caller: it is reported as a state with a reason.
/// </summary>
internal sealed class ProjectProxyService
{
    /// <summary>PARAM-011: the proxy's height in pixels (the width keeps the source's aspect ratio).</summary>
    internal const int ProxyHeight = 360;

    internal const string FolderName = "proxy";
    internal const string FileName = "proxy-360p.mp4";
    internal const string StagingDirectoryName = "proxies";
    internal static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(10);

    // The bundled ffmpeg has no libx264; the first encoder that works wins.
    private static readonly string[] Encoders = ["h264_mf", "libopenh264", "h264_nvenc"];

    internal const string ReasonMissing = "プロキシ動画がまだ作られていません。「再生成」で作成できます。";
    internal const string ReasonGenerating = "プロキシ動画を作成しています。完了すると、ここで再生できます。";
    internal const string ReasonSourceUnavailable = "元動画または動画変換ツールを確認できません。準備ができてから「再生成」を押してください。";
    internal const string ReasonSourceMissing = "元動画のファイルが見つかりません。";
    internal const string ReasonEncodeFailed = "動画の変換に失敗しました。元動画の形式が対応していない可能性があります。";
    internal const string ReasonWriteFailed = "プロキシ動画を保存できませんでした。保存先を確認して「再生成」を押してください。";
    internal const string ReasonInterrupted = "プロキシ動画の作成が中断されました。「再生成」で作り直せます。";

    private readonly string workspacePath;
    private readonly Func<EntityId<ProjectCreationDraftIdTag>, CancellationToken, Task<ProxySourceInfo?>> resolveSource;
    private readonly Func<EntityId<ProjectCreationDraftIdTag>, EntityId<ProjectIdTag>?> finalizedProjectOf;
    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run;
    private readonly ConcurrentDictionary<string, Task> inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
    // One ffmpeg at a time, like the poster: a list of old Projects must not start a process per Project.
    private readonly SemaphoreSlim generation = new(1, 1);

    internal ProjectProxyService(
        string workspacePath,
        Func<EntityId<ProjectCreationDraftIdTag>, CancellationToken, Task<ProxySourceInfo?>> resolveSource,
        Func<EntityId<ProjectCreationDraftIdTag>, EntityId<ProjectIdTag>?> finalizedProjectOf,
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>? runProcess = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!Path.IsPathFullyQualified(workspacePath))
        {
            throw new ArgumentException("The workspace path must be absolute.", nameof(workspacePath));
        }

        this.workspacePath = workspacePath;
        this.resolveSource = resolveSource ?? throw new ArgumentNullException(nameof(resolveSource));
        this.finalizedProjectOf = finalizedProjectOf ?? throw new ArgumentNullException(nameof(finalizedProjectOf));
        run = runProcess ?? new ProcessRunner().RunAsync;
    }

    /// <summary>The proxy file's location now: the Project folder once created, otherwise the staging folder.</summary>
    internal string PathFor(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        return Path.Combine(DirectoryFor(draftId), FileName);
    }

    internal ProjectProxyState Get(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        string key = draftId.ToString();
        if (inFlight.ContainsKey(key))
        {
            return new(ProjectProxyStatus.Generating, ReasonGenerating, null);
        }

        FileInfo? file = TryGetReadableFile(draftId);
        if (file is not null)
        {
            return new(ProjectProxyStatus.Ready, null, VersionOf(file));
        }

        return failures.TryGetValue(key, out string? reason)
            ? new(ProjectProxyStatus.Failed, reason, null)
            : new(ProjectProxyStatus.Missing, ReasonMissing, null);
    }

    /// <summary>Starts a new run (a running one is kept). The old proxy stays playable until the new one is ready.</summary>
    internal ProjectProxyState Regenerate(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        Start(draftId);
        return Get(draftId);
    }

    /// <summary>Makes the proxy in the background unless it exists or is being made. Never throws.</summary>
    internal void Request(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        if (TryGetReadableFile(draftId) is null)
        {
            Start(draftId);
        }
    }

    /// <summary>
    /// The Project was created: moves a proxy made before that into the Project folder, or makes it there.
    /// A run still in progress finishes straight into the Project folder.
    /// </summary>
    internal void OnFinalized(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        try
        {
            string staged = Path.Combine(StagingDirectory(draftId), FileName);
            string target = PathFor(draftId);
            if (File.Exists(staged) && !File.Exists(target) && !string.Equals(staged, target, StringComparison.OrdinalIgnoreCase))
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(staged, target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next settings screen shows the state; 「再生成」 makes it again.
        }

        Request(draftId);
    }

    /// <summary>Removes what is kept outside the Project folder (the Project folder goes with the Project).</summary>
    internal void Delete(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        _ = failures.TryRemove(draftId.ToString(), out _);
        try
        {
            Directory.Delete(StagingDirectory(draftId), recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Opens the proxy for the media route only while <paramref name="version"/> still names the file.</summary>
    internal ProxyReadHandle? OpenRead(EntityId<ProjectCreationDraftIdTag> draftId, string version)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        try
        {
            FileInfo? file = TryGetReadableFile(draftId);
            if (file is null || !string.Equals(VersionOf(file), version, StringComparison.Ordinal))
            {
                return null;
            }

            var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new ProxyReadHandle(stream, stream.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Completes when the run for <paramref name="draftId"/> (if any) has ended. For tests and shutdown.</summary>
    internal Task WhenIdleAsync(EntityId<ProjectCreationDraftIdTag> draftId) =>
        inFlight.TryGetValue(draftId.ToString(), out Task? task) ? task : Task.CompletedTask;

    private void Start(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        string key = draftId.ToString();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inFlight.TryAdd(key, started.Task))
        {
            return;
        }

        _ = failures.TryRemove(key, out _);
        // Task.Run: the caller (a bridge request) never runs file I/O or Process.Start itself.
        _ = Task.Run(async () =>
        {
            try
            {
                string? failure = await GenerateAsync(draftId).ConfigureAwait(false);
                if (failure is null)
                {
                    _ = failures.TryRemove(key, out _);
                }
                else
                {
                    failures[key] = failure;
                }
            }
            catch (Exception)
            {
                failures[key] = ReasonInterrupted;
            }
            finally
            {
                _ = inFlight.TryRemove(key, out _);
                started.TrySetResult();
            }
        }, CancellationToken.None);
    }

    /// <returns>Null on success, otherwise the reason shown to the user.</returns>
    private async Task<string?> GenerateAsync(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        await generation.WaitAsync().ConfigureAwait(false);
        try
        {
            ProxySourceInfo? source = await resolveSource(draftId, CancellationToken.None).ConfigureAwait(false);
            if (source is null)
            {
                return ReasonSourceUnavailable;
            }

            if (!File.Exists(source.SourcePath))
            {
                return ReasonSourceMissing;
            }

            if (!File.Exists(source.FfmpegPath))
            {
                return ReasonSourceUnavailable;
            }

            string staging = StagingDirectory(draftId);
            string temp = Path.Combine(staging, "part-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                _ = Directory.CreateDirectory(staging);
                bool encoded = false;
                foreach (string encoder in Encoders)
                {
                    ProcessRunResult result;
                    try
                    {
                        result = await run(new ProcessRunRequest(source.FfmpegPath,
                            BuildArguments(source.SourcePath, temp, encoder), staging, GenerationTimeout),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        continue;
                    }

                    var info = new FileInfo(temp);
                    if (result.ExitCode == 0 && info.Exists && info.Length > 0)
                    {
                        encoded = true;
                        break;
                    }
                }

                if (!encoded)
                {
                    return ReasonEncodeFailed;
                }

                // The destination is decided now: a Project created while this ran gets the proxy in its folder.
                string target = PathFor(draftId);
                try
                {
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(temp, target, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return ReasonWriteFailed;
                }

                return null;
            }
            finally
            {
                try { File.Delete(temp); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            _ = generation.Release();
        }
    }

    /// <summary>ffmpeg arguments: a {PARAM-011}-pixel-high H.264/AAC MP4. The source is opened read-only.</summary>
    internal static IReadOnlyList<string> BuildArguments(string sourcePath, string outputPath, string encoder) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-i", sourcePath,
        "-map", "0:v:0", "-map", "0:a:0?",
        "-vf", string.Create(CultureInfo.InvariantCulture, $"scale=-2:{ProxyHeight}"),
        "-c:v", encoder, "-b:v", "1M", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "96k",
        "-movflags", "+faststart",
        "-f", "mp4",
        "-y", outputPath,
    ];

    private string StagingDirectory(EntityId<ProjectCreationDraftIdTag> draftId) =>
        Path.Combine(workspacePath, StagingDirectoryName, draftId.ToString());

    private string DirectoryFor(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        EntityId<ProjectIdTag>? projectId = finalizedProjectOf(draftId);
        return projectId is null
            ? StagingDirectory(draftId)
            : Path.Combine(ProjectFolders.GetPath(workspacePath, projectId), FolderName);
    }

    private FileInfo? TryGetReadableFile(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        try
        {
            var info = new FileInfo(PathFor(draftId));
            return info.Exists && info.Length > 0 && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? info : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string VersionOf(FileInfo file) =>
        string.Create(CultureInfo.InvariantCulture, $"{file.LastWriteTimeUtc.Ticks:x16}{file.Length:x16}");
}

/// <summary>An open proxy file and its length, for a ranged GET.</summary>
internal sealed class ProxyReadHandle(FileStream stream, long length) : IDisposable
{
    internal FileStream Stream { get; } = stream;

    internal long Length { get; } = length;

    public void Dispose() => Stream.Dispose();
}
