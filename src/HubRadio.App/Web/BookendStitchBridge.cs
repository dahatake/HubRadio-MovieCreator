using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Web;

/// <summary>Native file dialogs for FR-035. Paths never leave the native host.</summary>
internal interface IBookendStitchFilePicker
{
    Task<string?> PickClipAsync(CancellationToken cancellationToken);

    Task<string?> PickOutputAsync(string suggestedFileName, CancellationToken cancellationToken);
}

internal static class BookendStitchBridgeProtocol
{
    internal static readonly string[] RunProperties =
        ["projectId", "executionId", "artifactId", "artifactDigest", "openingClipId", "closingClipId"];

    internal static bool HasValidParameters(BridgeMethod method, JsonElement parameters) => method switch
    {
        BridgeMethod.StitchPickClip => parameters.ValueKind == JsonValueKind.Object && !parameters.EnumerateObject().Any(),
        BridgeMethod.StitchRun => SettingsBridgeProtocol.HasExactProperties(parameters, RunProperties) &&
            SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("projectId")) &&
            SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("executionId")) &&
            SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("artifactId")) &&
            SettingsBridgeProtocol.IsDigest(parameters.GetProperty("artifactDigest")) &&
            IsNullableClipId(parameters.GetProperty("openingClipId")) &&
            IsNullableClipId(parameters.GetProperty("closingClipId")),
        _ => false,
    };

    private static bool IsNullableClipId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null || SettingsBridgeProtocol.IsCanonicalId(value);
}

internal static class BookendStitchBridgeJson
{
    internal static string Unavailable(BridgeRequest request) => request.Method == BridgeMethod.StitchPickClip
        ? PickClip(request, "unavailable", null, null)
        : Run(request, "unavailable");

    internal static string PickClip(BridgeRequest request, string status, Guid? clipId, string? fileName) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            if (clipId is Guid id) writer.WriteString("clipId", id.ToString("D")); else writer.WriteNull("clipId");
            if (fileName is null) writer.WriteNull("fileName"); else writer.WriteString("fileName", fileName);
            writer.WriteEndObject();
        });

    internal static string Run(BridgeRequest request, string status, string? code = null, string? outputFileName = null,
        string? manifestFileName = null, decimal? durationSeconds = null) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            if (code is null) writer.WriteNull("code"); else writer.WriteString("code", code);
            if (outputFileName is null) writer.WriteNull("outputFileName"); else writer.WriteString("outputFileName", outputFileName);
            if (manifestFileName is null) writer.WriteNull("manifestFileName"); else writer.WriteString("manifestFileName", manifestFileName);
            if (durationSeconds is decimal seconds) writer.WriteNumber("durationSeconds", seconds); else writer.WriteNull("durationSeconds");
            writer.WriteEndObject();
        });
}

/// <summary>Opaque, process-lifetime clip selections. The oldest entry is evicted beyond the cap.</summary>
internal sealed class BookendStitchClipRegistry
{
    internal const int MaximumClips = 32;
    private readonly object gate = new();
    private readonly Dictionary<Guid, string> paths = [];
    private readonly Queue<Guid> order = new();

    internal Guid Register(string path)
    {
        lock (gate)
        {
            var id = Guid.NewGuid();
            paths.Add(id, path);
            order.Enqueue(id);
            while (order.Count > MaximumClips) paths.Remove(order.Dequeue());
            return id;
        }
    }

    internal string? Resolve(Guid id)
    {
        lock (gate) return paths.TryGetValue(id, out string? path) ? path : null;
    }
}

/// <summary>
/// FR-035.13〜FR-035.21. The main video is accepted only as an output listed in the
/// requested stored execution's validated result (same checks as source-only media);
/// Opening/Closing only as opaque clip IDs chosen through the native dialog. Nothing is
/// registered in the store and no workflow state changes.
/// </summary>
internal sealed class BookendStitchBridgeHandler
{
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(3600);
    internal static readonly TimeSpan PlaceholderWindow = TimeSpan.FromSeconds(2);

    private readonly SourceOnlyExecutionStore executionStore;
    private readonly SourceOnlyResultService resultService;
    private readonly ArtifactStore artifactStore;
    private readonly Func<string> currentActorSid;
    private readonly AuthorizationService authorization;
    private readonly IBookendStitchFilePicker picker;
    private readonly string ffmpegPath;
    private readonly string ffprobePath;
    private readonly Func<BookendStitchRequest, CancellationToken, Task<BookendStitchResult>> stitch;
    private readonly TimeProvider timeProvider;
    private readonly BookendStitchClipRegistry clips = new();
    private int running;

    internal BookendStitchBridgeHandler(
        SourceOnlyExecutionStore executionStore,
        SourceOnlyResultService resultService,
        ArtifactStore artifactStore,
        Func<string> currentActorSid,
        AuthorizationService authorization,
        IBookendStitchFilePicker picker,
        string ffmpegPath,
        string ffprobePath,
        Func<BookendStitchRequest, CancellationToken, Task<BookendStitchResult>>? stitch = null,
        TimeProvider? timeProvider = null)
    {
        this.executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        this.resultService = resultService ?? throw new ArgumentNullException(nameof(resultService));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.currentActorSid = currentActorSid ?? throw new ArgumentNullException(nameof(currentActorSid));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.picker = picker ?? throw new ArgumentNullException(nameof(picker));
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        this.ffmpegPath = ffmpegPath;
        this.ffprobePath = ffprobePath;
        // The stitcher's own awaits leave the UI context; the dialogs must not.
        this.stitch = stitch ?? ((request, token) => Task.Run(() => new BookendStitcher().StitchAsync(request, token), token));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static string SuggestedFileName(SourceOnlyResultRole role) => role switch
    {
        SourceOnlyResultRole.LandscapeClean => "hubradio-long-clean-bookend.mp4",
        SourceOnlyResultRole.LandscapeBurnIn => "hubradio-long-subtitled-bookend.mp4",
        SourceOnlyResultRole.VerticalBurnIn => "hubradio-short-subtitled-bookend.mp4",
        SourceOnlyResultRole.VerticalClean => "hubradio-short-clean-bookend.mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    // No ConfigureAwait(false): the native dialogs require the caller's UI context.
    internal async Task<string> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return request.Method switch
            {
                BridgeMethod.StitchPickClip => await PickClipAsync(request, cancellationToken),
                BridgeMethod.StitchRun => await RunAsync(request, cancellationToken),
                _ => throw new ArgumentException("A stitch request is required.", nameof(request)),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception)
        {
            return BookendStitchBridgeJson.Unavailable(request);
        }
    }

    private bool IsAuthorized()
    {
        try { return authorization.CanPerform(currentActorSid(), AuthorizationOperation.ExportComposition); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return false; }
    }

    private async Task<string> PickClipAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (!IsAuthorized()) return BookendStitchBridgeJson.PickClip(request, "forbidden", null, null);
        string? path = await picker.PickClipAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (path is null) return BookendStitchBridgeJson.PickClip(request, "cancelled", null, null);
        if (!IsSupportedClip(path)) return BookendStitchBridgeJson.PickClip(request, "unsupported", null, null);
        string fullPath = Path.GetFullPath(path);
        return BookendStitchBridgeJson.PickClip(request, "selected", clips.Register(fullPath), Path.GetFileName(fullPath));
    }

    private static bool IsSupportedClip(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return false;
        string fileName = Path.GetFileName(path);
        return fileName.Length is > 0 and <= 255 && File.Exists(path) &&
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) == 0 &&
            BookendStitcher.AllowedInputExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> RunAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        if (!IsAuthorized()) return BookendStitchBridgeJson.Run(request, "forbidden");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return BookendStitchBridgeJson.Run(request, "busy");
        ValidatedArtifactReadStream? held = null;
        try
        {
            JsonElement parameters = request.Parameters;
            string? opening = ResolveClip(parameters.GetProperty("openingClipId"), out bool openingValid);
            string? closing = ResolveClip(parameters.GetProperty("closingClipId"), out bool closingValid);
            if (!openingValid || !closingValid || (opening is null && closing is null))
                return BookendStitchBridgeJson.Run(request, "invalid");

            (string MainPath, SourceOnlyResultRole Role, ValidatedArtifactReadStream Held)? main =
                await ResolveMainAsync(parameters, cancellationToken);
            if (main is null) return BookendStitchBridgeJson.Run(request, "notFound");
            held = main.Value.Held;

            DateTimeOffset openedAt = timeProvider.GetUtcNow();
            string? output = await picker.PickOutputAsync(SuggestedFileName(main.Value.Role), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (output is null) return BookendStitchBridgeJson.Run(request, "cancelled");
            RemoveDialogPlaceholder(output, openedAt);

            BookendStitchResult result;
            try
            {
                result = await stitch(new BookendStitchRequest(main.Value.MainPath, opening, closing, output,
                    ffmpegPath, ffprobePath, ProcessTimeout), cancellationToken);
            }
            catch (BookendStitchException exception) when (exception.Failure != BookendStitchFailure.Usage)
            {
                return BookendStitchBridgeJson.Run(request, "failed", exception.Code);
            }

            return BookendStitchBridgeJson.Run(request, "completed", null, Path.GetFileName(result.OutputPath),
                Path.GetFileName(result.ManifestPath), BookendStitcher.RoundSeconds(result.OutputDurationSeconds));
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            Volatile.Write(ref running, 0);
        }
    }

    private string? ResolveClip(JsonElement value, out bool valid)
    {
        valid = true;
        if (value.ValueKind == JsonValueKind.Null) return null;
        string? path = clips.Resolve(Guid.ParseExact(value.GetString()!, "D"));
        valid = path is not null;
        return path;
    }

    private async Task<(string, SourceOnlyResultRole, ValidatedArtifactReadStream)?> ResolveMainAsync(
        JsonElement parameters, CancellationToken cancellationToken)
    {
        ValidatedArtifactReadStream? held = null;
        try
        {
            var projectId = EntityId<ProjectIdTag>.Parse(parameters.GetProperty("projectId").GetString()!);
            var executionId = EntityId<SourceOnlyExecutionIdTag>.Parse(parameters.GetProperty("executionId").GetString()!);
            var artifactId = EntityId<ArtifactIdTag>.Parse(parameters.GetProperty("artifactId").GetString()!);
            var digest = new ContentDigest(parameters.GetProperty("artifactDigest").GetString()!);
            StoredSourceOnlyExecution? execution = executionStore.Get(executionId);
            if (execution is null || execution.Request.ProjectId != projectId || execution.ResultJson is null) return null;
            SourceOnlyResultLoadResult loaded = await resultService.LoadAsync(execution, cancellationToken);
            if (!loaded.Validation.IsValid) return null;
            SourceOnlyResultArtifact? output = loaded.Result.Artifacts.SingleOrDefault(item => item.ArtifactId == artifactId);
            if (output is null || !output.IsVideo || output.ArtifactDigest != digest || output.Artifact.Kind != ArtifactKind.Render)
                return null;
            held = await artifactStore.OpenValidatedReadAsync(artifactId, cancellationToken);
            if (held.Id != artifactId || held.ProjectId != projectId || held.Kind != ArtifactKind.Render ||
                held.ContentDigest != digest || held.ByteSize != output.ByteSize)
                return null;
            string path = artifactStore.GetValidatedRegisteredArtifactPath(artifactId);
            StoredSourceOnlyExecution? current = executionStore.Get(executionId);
            if (current is null || current.ResultDigest != execution.ResultDigest || current.ResultJson != execution.ResultJson)
                return null;
            (string, SourceOnlyResultRole, ValidatedArtifactReadStream) resolved = (path, output.Role, held);
            held = null;
            return resolved;
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
            if (held is not null) await held.DisposeAsync();
        }
    }

    // A save dialog may create the chosen file empty; only such a fresh 0-byte file is removed (A-035-11).
    private static void RemoveDialogPlaceholder(string output, DateTimeOffset openedAt)
    {
        try
        {
            var file = new FileInfo(output);
            if (file.Exists && file.Length == 0 && file.CreationTimeUtc >= (openedAt - PlaceholderWindow).UtcDateTime &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0)
                file.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // The stitcher reports any remaining file as stitch.output_exists or a path error.
        }
    }
}
