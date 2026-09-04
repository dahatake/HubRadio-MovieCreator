using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Media;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Media;

internal sealed record MusicAudioProbeResult(
    decimal DurationSeconds,
    string CodecName,
    int SampleRate,
    int Channels);

public sealed class MusicCatalogScanner
{
    private const int FileBufferSize = 64 * 1024;
    private const int ProbeOutputLimit = 256 * 1024;
    private const string ToolIdentity =
        "tool.ffprobe.BtbN.FFmpeg.LGPL.8.1.8.1.2-20260731";

    private static readonly HashSet<string> RootProperties =
    [
        "streams",
        "format",
        "programs",
        "stream_groups",
    ];

    private static readonly HashSet<string> StreamProperties =
    [
        "codec_type",
        "codec_name",
        "sample_rate",
        "channels",
        "duration",
    ];

    private static readonly HashSet<string> FormatProperties = ["duration"];

    private static readonly FileStreamOptions ReadOptions = new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = FileBufferSize,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    };

    private readonly string _libraryRoot;
    private readonly Func<string, CancellationToken, Task<MusicAudioProbeResult>> _probe;

    public MusicCatalogScanner(
        string libraryRoot,
        ProcessRunner processRunner,
        BinaryVerifier binaryVerifier,
        string ffprobePath,
        TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        string normalizedFfprobe = ValidateLocalAbsolutePath(
            ffprobePath,
            nameof(ffprobePath));
        if (probeTimeout <= TimeSpan.Zero || probeTimeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        _libraryRoot = ValidateLocalAbsolutePath(libraryRoot, nameof(libraryRoot));
        _probe = async (path, cancellationToken) =>
        {
            BinaryVerificationRequest verification = new(
                ToolIdentity,
                normalizedFfprobe,
                new ContentDigest(FfprobeClient.CanonicalFfprobeSha256),
                FfprobeClient.CanonicalFfprobeByteSize);
            BinaryVerificationResult verified = await binaryVerifier.VerifyAsync(
                verification,
                cancellationToken).ConfigureAwait(false);
            if (!verified.IsVerified)
            {
                throw new InvalidOperationException("The music probe tool is unavailable.");
            }

            ProcessRunRequest request = CreateProbeRequest(
                normalizedFfprobe,
                path,
                probeTimeout);
            ProcessRunResult result = await processRunner.RunAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            return ParseProbeResult(result);
        };
    }

    internal MusicCatalogScanner(
        string libraryRoot,
        Func<string, CancellationToken, Task<MusicAudioProbeResult>> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _libraryRoot = ValidateLocalAbsolutePath(libraryRoot, nameof(libraryRoot));
        _probe = probe;
    }

    public async Task<MusicCatalog> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(_libraryRoot);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.Absent,
                MusicCatalogWarning.LibraryAbsent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.Inaccessible,
                MusicCatalogWarning.LibraryInaccessible);
        }

        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.Unsafe,
                MusicCatalogWarning.UnsafeLibraryLocation);
        }

        string[] candidates;
        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(_libraryRoot);
            candidates = Directory.EnumerateFiles(
                    _libraryRoot,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Where(static path => string.Equals(
                    Path.GetExtension(path),
                    ".mp3",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, WorkspacePathSafety.PathComparer)
                .ToArray();
        }
        catch (WorkspacePathSafetyException exception)
            when (exception.Failure == WorkspacePathSafetyFailure.ReparsePoint)
        {
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.Unsafe,
                MusicCatalogWarning.UnsafeLibraryLocation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or WorkspacePathSafetyException)
        {
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.Inaccessible,
                MusicCatalogWarning.LibraryInaccessible);
        }

        if (candidates.Length == 0)
        {
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.Empty,
                MusicCatalogWarning.LibraryEmpty);
        }

        SortedSet<MusicCatalogWarning> warnings = [];
        Dictionary<string, CandidateSnapshot> snapshots = new(
            WorkspacePathSafety.PathComparer);
        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WorkspacePathSafety.ValidateExistingPathChain(
                    _libraryRoot,
                    candidate,
                    targetIsDirectory: false);
                (ContentDigest digest, long byteSize) = await HashAsync(
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                snapshots.Add(candidate, new CandidateSnapshot(digest, byteSize));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WorkspacePathSafetyException exception)
                when (exception.Failure is WorkspacePathSafetyFailure.ReparsePoint or
                    WorkspacePathSafetyFailure.NotRegularFile or
                    WorkspacePathSafetyFailure.UnsafePath)
            {
                warnings.Add(MusicCatalogWarning.UnsafeLibraryLocation);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or CryptographicException or WorkspacePathSafetyException or ArgumentException)
            {
                warnings.Add(MusicCatalogWarning.TrackUnreadable);
            }
        }

        Dictionary<string, TrackScanResult> scanResults = new(
            WorkspacePathSafety.PathComparer);
        foreach ((string candidate, CandidateSnapshot snapshot) in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanResults.Add(
                candidate,
                await ProbeTrackAsync(
                    candidate,
                    snapshot,
                    cancellationToken).ConfigureAwait(false));
        }

        HashSet<string> unstableCandidates = new(WorkspacePathSafety.PathComparer);
        foreach ((string candidate, CandidateSnapshot snapshot) in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WorkspacePathSafety.ValidateExistingPathChain(
                    _libraryRoot,
                    candidate,
                    targetIsDirectory: false);
                (ContentDigest digest, long byteSize) = await HashAsync(
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                if (digest != snapshot.ContentDigest || byteSize != snapshot.ByteSize)
                {
                    unstableCandidates.Add(candidate);
                    warnings.Add(MusicCatalogWarning.SourceChangedDuringScan);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or CryptographicException or WorkspacePathSafetyException or ArgumentException)
            {
                unstableCandidates.Add(candidate);
                warnings.Add(MusicCatalogWarning.SourceChangedDuringScan);
            }
        }

        List<MusicTrack> tracks = [];
        HashSet<string> digests = new(StringComparer.Ordinal);
        foreach ((string candidate, TrackScanResult result) in scanResults)
        {
            if (result.Warning is not null)
            {
                warnings.Add(result.Warning.Value);
            }

            if (result.Track is null || unstableCandidates.Contains(candidate))
            {
                continue;
            }

            if (!digests.Add(result.Track.ContentDigest.Hex))
            {
                warnings.Add(MusicCatalogWarning.DuplicateContent);
                continue;
            }

            tracks.Add(result.Track);
        }

        if (tracks.Count == 0)
        {
            warnings.Add(MusicCatalogWarning.NoEligibleTracks);
            return MusicCatalog.CreateUnavailable(
                MusicLibraryState.NoEligibleTracks,
                warnings.ToArray());
        }

        return MusicCatalog.CreateAvailable(tracks, warnings.ToArray());
    }

    internal static ProcessRunRequest CreateProbeRequest(
        string ffprobePath,
        string musicPath,
        TimeSpan timeout)
    {
        string executable = ValidateLocalAbsolutePath(ffprobePath, nameof(ffprobePath));
        string input = ValidateLocalAbsolutePath(musicPath, nameof(musicPath));
        string? workingDirectory = Path.GetDirectoryName(input);
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new ArgumentException(
                "The music input must have an existing parent directory.",
                nameof(musicPath));
        }

        return new ProcessRunRequest(
            executable,
            [
                "-v",
                "error",
                "-show_entries",
                "format=duration:stream=codec_type,codec_name,sample_rate,channels,duration",
                "-of",
                "json",
                input,
            ],
            workingDirectory,
            timeout,
            ProbeOutputLimit);
    }

    internal static MusicAudioProbeResult ParseProbeResult(ProcessRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Truncated || result.ExitCode != 0 ||
            result.StandardError.Length != 0 ||
            string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException("The music probe failed.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            RequireObjectWithOnlyProperties(root, RootProperties);
            JsonElement streams = root.GetProperty("streams");
            JsonElement format = root.GetProperty("format");
            if (root.ValueKind != JsonValueKind.Object ||
                streams.ValueKind != JsonValueKind.Array ||
                format.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The music probe metadata is invalid.");
            }

            RequireObjectWithOnlyProperties(format, FormatProperties);
            if (root.TryGetProperty("programs", out JsonElement programs))
            {
                RequireEmptyArray(programs);
            }

            if (root.TryGetProperty("stream_groups", out JsonElement streamGroups))
            {
                RequireEmptyArray(streamGroups);
            }

            JsonElement[] audioStreams = streams.EnumerateArray()
                .Where(static stream => ReadStreamKind(stream) == "audio")
                .ToArray();
            if (audioStreams.Length != 1)
            {
                throw new InvalidOperationException("The music probe metadata is invalid.");
            }

            JsonElement audio = audioStreams[0];
            decimal duration = ReadPositiveDecimal(format, "duration");
            decimal? streamDuration = TryReadPositiveDecimal(audio, "duration");
            if (streamDuration is not null && streamDuration.Value < duration)
            {
                duration = streamDuration.Value;
            }

            string codecName = ReadRequiredTrimmedString(audio, "codec_name");
            if (!string.Equals(codecName, "mp3", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The music probe metadata is invalid.");
            }

            return new MusicAudioProbeResult(
                duration,
                codecName,
                ReadPositiveInt32(audio, "sample_rate"),
                ReadPositiveInt32(audio, "channels"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("The music probe metadata is invalid.");
        }
    }

    private async Task<TrackScanResult> ProbeTrackAsync(
        string path,
        CandidateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            WorkspacePathSafety.ValidateExistingPathChain(
                _libraryRoot,
                path,
                targetIsDirectory: false);
            MusicAudioProbeResult probe = await _probe(
                path,
                cancellationToken).ConfigureAwait(false);

            return new TrackScanResult(
                new MusicTrack(
                    snapshot.ContentDigest,
                    snapshot.ByteSize,
                    probe.DurationSeconds,
                    probe.CodecName,
                    probe.SampleRate,
                    probe.Channels),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkspacePathSafetyException exception)
            when (exception.Failure is WorkspacePathSafetyFailure.ReparsePoint or
                WorkspacePathSafetyFailure.NotRegularFile or
                WorkspacePathSafetyFailure.UnsafePath)
        {
            return new TrackScanResult(null, MusicCatalogWarning.UnsafeLibraryLocation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or CryptographicException or WorkspacePathSafetyException or InvalidOperationException or ArgumentException)
        {
            return new TrackScanResult(null, MusicCatalogWarning.TrackUnreadable);
        }
    }

    private static async Task<(ContentDigest Digest, long ByteSize)> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, ReadOptions);
        FileAttributes attributes = File.GetAttributes(stream.SafeFileHandle);
        WorkspacePathSafety.ValidateRegularFileAttributes(attributes);
        long byteSize = stream.Length;
        if (byteSize <= 0)
        {
            throw new IOException("The music input is empty.");
        }

        byte[] digest = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        if (stream.Length != byteSize)
        {
            throw new IOException("The music input changed while being read.");
        }

        return (ContentDigest.FromBytes(digest), byteSize);
    }

    private static string ValidateLocalAbsolutePath(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The path must be a local absolute path.",
                parameterName);
        }

        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (fullPath.AsSpan(root.Length).Contains(':'))
            {
                throw new ArgumentException(
                    "The path must not contain an alternate data stream.",
                    parameterName);
            }
        }

        return fullPath;
    }

    private static string ReadRequiredTrimmedString(
        JsonElement element,
        string propertyName)
    {
        string? value = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) ||
            value.Contains('\uFFFD', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The music probe metadata is invalid.");
        }

        return value;
    }

    private static string? ReadStreamKind(JsonElement stream)
    {
        RequireObjectWithOnlyProperties(stream, StreamProperties);
        return ReadString(stream, "codec_type");
    }

    private static void RequireObjectWithOnlyProperties(
        JsonElement element,
        IReadOnlySet<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The music probe metadata is invalid.");
        }

        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name) ||
                !observed.Add(property.Name))
            {
                throw new InvalidOperationException("The music probe metadata is invalid.");
            }
        }
    }

    private static void RequireEmptyArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 0)
        {
            throw new InvalidOperationException("The music probe metadata is invalid.");
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadPositiveInt32(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        if (value is null && element.TryGetProperty(propertyName, out JsonElement numeric) &&
            numeric.ValueKind == JsonValueKind.Number)
        {
            value = numeric.GetRawText();
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed <= 0)
        {
            throw new InvalidOperationException("The music probe metadata is invalid.");
        }

        return parsed;
    }

    private static decimal ReadPositiveDecimal(JsonElement element, string propertyName) =>
        TryReadPositiveDecimal(element, propertyName)
        ?? throw new InvalidOperationException("The music probe metadata is invalid.");

    private static decimal? TryReadPositiveDecimal(
        JsonElement element,
        string propertyName)
    {
        string? value = ReadString(element, propertyName);
        if (value is null)
        {
            return null;
        }

        if (!decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal parsed) ||
            parsed <= 0)
        {
            throw new InvalidOperationException("The music probe metadata is invalid.");
        }

        return parsed;
    }

    private sealed record TrackScanResult(
        MusicTrack? Track,
        MusicCatalogWarning? Warning);

    private sealed record CandidateSnapshot(
        ContentDigest ContentDigest,
        long ByteSize);
}