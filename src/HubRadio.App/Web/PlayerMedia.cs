using System.Globalization;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Web;

internal interface IPlayerMediaAssetReader
{
    Task<ValidatedArtifactReadStream?> OpenAsync(
        PlayerMediaAssetReference reference,
        CancellationToken cancellationToken);
}

internal sealed class UnavailablePlayerMediaAssetReader : IPlayerMediaAssetReader
{
    internal static UnavailablePlayerMediaAssetReader Instance { get; } = new();

    private UnavailablePlayerMediaAssetReader()
    {
    }

    public Task<ValidatedArtifactReadStream?> OpenAsync(
        PlayerMediaAssetReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ValidatedArtifactReadStream?>(null);
    }
}

internal sealed class ArtifactStorePlayerMediaAssetReader : IPlayerMediaAssetReader
{
    private readonly ArtifactStore artifactStore;

    internal ArtifactStorePlayerMediaAssetReader(ArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        this.artifactStore = artifactStore;
    }

    public async Task<ValidatedArtifactReadStream?> OpenAsync(
        PlayerMediaAssetReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        ValidatedArtifactReadStream? stream = null;
        try
        {
            StoredArtifact? registered = artifactStore.GetArtifact(
                reference.ArtifactId);
            if (registered is null ||
                !HasExpectedRegistration(registered, reference))
            {
                return null;
            }

            stream = await artifactStore.OpenValidatedReadAsync(
                reference.ArtifactId,
                cancellationToken).ConfigureAwait(false);
            StoredArtifact? current = artifactStore.GetArtifact(
                reference.ArtifactId);
            if (current is null ||
                !HasSameRegistration(registered, current) ||
                stream.Id != reference.ArtifactId ||
                stream.ProjectId != reference.ProjectId ||
                stream.Kind != registered.Kind ||
                stream.ContentDigest != reference.ContentDigest ||
                stream.ByteSize != registered.ByteSize)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
                return null;
            }

            ValidatedArtifactReadStream result = stream;
            stream = null;
            return result;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            throw new OperationCanceledException(cancellationToken);
        }
        catch (ArtifactStoreException)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            return null;
        }

        static bool HasExpectedRegistration(
            StoredArtifact artifact,
            PlayerMediaAssetReference expected)
        {
            ArtifactKind expectedKind = expected.Kind switch
            {
                PlayerMediaAssetKind.ProxyVideo => ArtifactKind.Proxy,
                PlayerMediaAssetKind.ThumbnailSprite => ArtifactKind.Preview,
                PlayerMediaAssetKind.GeneratedThumbnail => ArtifactKind.Render,
                _ => throw new InvalidOperationException(
                    "The player media asset kind is unsupported."),
            };
            if (artifact.Id != expected.ArtifactId ||
                artifact.ProjectId != expected.ProjectId ||
                artifact.Kind != expectedKind ||
                artifact.ContentDigest != expected.ContentDigest ||
                artifact.ByteSize <= 0)
            {
                return false;
            }

            string[] segments = artifact.StorageReference.Split('/');
            if (segments.Length < 5 ||
                !string.Equals(segments[0], "projects", StringComparison.Ordinal) ||
                !string.Equals(
                    segments[1],
                    expected.ProjectId.ToString(),
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (expected.Kind == PlayerMediaAssetKind.ProxyVideo)
            {
                return segments.Length == 5 &&
                    string.Equals(segments[2], "proxy", StringComparison.Ordinal) &&
                    IsCanonicalAssetId(segments[3]) &&
                    string.Equals(
                        segments[4],
                        string.Concat(expected.ArtifactId.ToString(), ".mp4"),
                        StringComparison.Ordinal);
            }

            if (expected.Kind == PlayerMediaAssetKind.ThumbnailSprite)
            {
                return segments.Length == 6 &&
                    string.Equals(segments[2], "previews", StringComparison.Ordinal) &&
                    IsCanonicalAssetId(segments[3]) &&
                    IsCanonicalArtifactId(segments[4]) &&
                    string.Equals(
                        segments[5],
                        string.Concat(
                            expected.ArtifactId.ToString(),
                            ".sprite.png"),
                        StringComparison.Ordinal);
            }

            return segments.Length == 6 &&
                string.Equals(segments[2], "renders", StringComparison.Ordinal) &&
                string.Equals(segments[3], "thumbnails", StringComparison.Ordinal) &&
                string.Equals(
                    segments[4],
                    expected.ArtifactId.ToString(),
                    StringComparison.Ordinal) &&
                IsCanonicalDigestFileName(segments[5]);
        }

        static bool IsCanonicalAssetId(string value) =>
            EntityId<AssetIdTag>.TryParse(
                value,
                out EntityId<AssetIdTag>? parsed) &&
            string.Equals(value, parsed.ToString(), StringComparison.Ordinal);

        static bool IsCanonicalArtifactId(string value) =>
            EntityId<ArtifactIdTag>.TryParse(
                value,
                out EntityId<ArtifactIdTag>? parsed) &&
            string.Equals(value, parsed.ToString(), StringComparison.Ordinal);

        static bool IsCanonicalDigestFileName(string value)
        {
            const string extension = ".png";
            return value.EndsWith(extension, StringComparison.Ordinal) &&
                ContentDigest.TryParse(
                    value[..^extension.Length],
                    out ContentDigest? parsed) &&
                string.Equals(
                    value,
                    string.Concat(parsed.Hex, extension),
                    StringComparison.Ordinal);
        }

        static bool HasSameRegistration(
            StoredArtifact expected,
            StoredArtifact actual) =>
            expected.Id == actual.Id &&
            expected.ProjectId == actual.ProjectId &&
            expected.Kind == actual.Kind &&
            string.Equals(
                expected.StorageReference,
                actual.StorageReference,
                StringComparison.Ordinal) &&
            expected.ContentDigest == actual.ContentDigest &&
            expected.ByteSize == actual.ByteSize &&
            expected.CreatedAt == actual.CreatedAt;
    }
}

internal sealed class PlayerMediaResponse : IDisposable
{
    private Stream? content;

    internal PlayerMediaResponse(
        int statusCode,
        string reasonPhrase,
        string headers,
        Stream? content)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        this.content = content;
    }

    internal int StatusCode { get; }

    internal string ReasonPhrase { get; }

    internal string Headers { get; }

    internal Stream? Content => content;

    internal Stream? DetachContent() =>
        Interlocked.Exchange(ref content, null);

    public void Dispose()
    {
        Stream? owned = Interlocked.Exchange(ref content, null);
        owned?.Dispose();
    }
}

internal sealed class PlayerMediaResponseFactory
{
    private const string GetMethod = "GET";
    private const string ProxyContentType = "video/mp4";
    private const string SpriteContentType = "image/png";

    private readonly IPlayerMediaAssetReader assetReader;
    private readonly IPlayerAccessAuthorization authorization;

    internal PlayerMediaResponseFactory(
        IPlayerMediaAssetReader assetReader,
        IPlayerAccessAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(assetReader);
        ArgumentNullException.ThrowIfNull(authorization);
        this.assetReader = assetReader;
        this.authorization = authorization;
    }

    internal async Task<PlayerMediaResponse> CreateAsync(
        string? method,
        string? uri,
        string? rangeHeader,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(method, GetMethod, StringComparison.Ordinal))
        {
            return CreateEmptyResponse(
                statusCode: 405,
                reasonPhrase: "Method Not Allowed",
                allowGet: true);
        }

        try
        {
            authorization.DemandPreview();
        }
        catch (Exception)
        {
            return CreateEmptyResponse(404, "Not Found");
        }

        if (!PlayerMediaAssetReference.TryParse(
                uri,
                out PlayerMediaAssetReference? reference) ||
            reference is null)
        {
            return CreateEmptyResponse(404, "Not Found");
        }

        ValidatedArtifactReadStream? source;
        try
        {
            source = await assetReader.OpenAsync(
                reference,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateEmptyResponse(404, "Not Found");
        }

        if (source is null)
        {
            return CreateEmptyResponse(404, "Not Found");
        }

        try
        {
            if (!HasExpectedBinding(source, reference))
            {
                source.Dispose();
                return CreateEmptyResponse(404, "Not Found");
            }

            if (!PlayerMediaByteRange.TryResolve(
                    rangeHeader,
                    source.ByteSize,
                    out PlayerMediaByteRange range))
            {
                long totalLength = source.ByteSize;
                source.Dispose();
                return CreateRangeNotSatisfiableResponse(totalLength);
            }

            var content = new PlayerMediaContentStream(
                source,
                range.Start,
                range.Length);
            source = null;
            string contentType = reference.Kind switch
            {
                PlayerMediaAssetKind.ProxyVideo => ProxyContentType,
                PlayerMediaAssetKind.ThumbnailSprite => SpriteContentType,
                PlayerMediaAssetKind.GeneratedThumbnail => SpriteContentType,
                _ => throw new InvalidOperationException(
                    "The player media asset kind is unsupported."),
            };
            return range.IsPartial
                ? CreatePartialResponse(contentType, range, content)
                : CreateFullResponse(contentType, range.Length, content);
        }
        catch
        {
            source?.Dispose();
            throw;
        }
    }

    private static bool HasExpectedBinding(
        ValidatedArtifactReadStream stream,
        PlayerMediaAssetReference reference)
    {
        ArtifactKind expectedKind = reference.Kind switch
        {
            PlayerMediaAssetKind.ProxyVideo => ArtifactKind.Proxy,
            PlayerMediaAssetKind.ThumbnailSprite => ArtifactKind.Preview,
            PlayerMediaAssetKind.GeneratedThumbnail => ArtifactKind.Render,
            _ => throw new InvalidOperationException(
                "The player media asset kind is unsupported."),
        };

        try
        {
            return stream.Id == reference.ArtifactId &&
                stream.ProjectId == reference.ProjectId &&
                stream.Kind == expectedKind &&
                stream.ContentDigest == reference.ContentDigest &&
                stream.ByteSize > 0 &&
                stream.Length == stream.ByteSize;
        }
        catch (Exception exception)
            when (exception is IOException or NotSupportedException or
                ObjectDisposedException)
        {
            return false;
        }
    }

    private static PlayerMediaResponse CreateFullResponse(
        string contentType,
        long contentLength,
        Stream content) =>
        new(
            200,
            "OK",
            BuildHeaders(
                contentType,
                contentLength,
                contentRange: null,
                acceptRanges: true,
                allowGet: false),
            content);

    private static PlayerMediaResponse CreatePartialResponse(
        string contentType,
        PlayerMediaByteRange range,
        Stream content) =>
        new(
            206,
            "Partial Content",
            BuildHeaders(
                contentType,
                range.Length,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bytes {range.Start}-{range.End}/{range.TotalLength}"),
                acceptRanges: true,
                allowGet: false),
            content);

    private static PlayerMediaResponse CreateRangeNotSatisfiableResponse(
        long totalLength) =>
        new(
            416,
            "Range Not Satisfiable",
            BuildHeaders(
                contentType: null,
                contentLength: 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"bytes */{totalLength}"),
                acceptRanges: true,
                allowGet: false),
            content: null);

    private static PlayerMediaResponse CreateEmptyResponse(
        int statusCode,
        string reasonPhrase,
        bool allowGet = false) =>
        new(
            statusCode,
            reasonPhrase,
            BuildHeaders(
                contentType: null,
                contentLength: 0,
                contentRange: null,
                acceptRanges: false,
                allowGet),
            content: null);

    private static string BuildHeaders(
        string? contentType,
        long contentLength,
        string? contentRange,
        bool acceptRanges,
        bool allowGet)
    {
        var headers = new List<string>();
        if (contentType is not null)
        {
            headers.Add(string.Concat("Content-Type: ", contentType));
        }

        headers.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"Content-Length: {contentLength}"));
        if (acceptRanges)
        {
            headers.Add("Accept-Ranges: bytes");
        }

        if (contentRange is not null)
        {
            headers.Add(string.Concat("Content-Range: ", contentRange));
        }

        if (allowGet)
        {
            headers.Add("Allow: GET");
        }

        headers.Add("Cache-Control: no-store");
        headers.Add("X-Content-Type-Options: nosniff");
        headers.Add("Cross-Origin-Resource-Policy: same-site");
        return string.Join("\r\n", headers);
    }
}

internal readonly struct PlayerMediaByteRange
{
    internal PlayerMediaByteRange(
        long start,
        long length,
        long totalLength,
        bool isPartial)
    {
        Start = start;
        Length = length;
        TotalLength = totalLength;
        IsPartial = isPartial;
    }

    internal long Start { get; }

    internal long Length { get; }

    internal long TotalLength { get; }

    internal bool IsPartial { get; }

    internal long End => checked(Start + Length - 1);

    internal static bool TryResolve(
        string? rangeHeader,
        long totalLength,
        out PlayerMediaByteRange range)
    {
        if (totalLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalLength));
        }

        if (rangeHeader is null)
        {
            range = new PlayerMediaByteRange(
                start: 0,
                length: totalLength,
                totalLength,
                isPartial: false);
            return true;
        }

        const string prefix = "bytes=";
        if (!rangeHeader.StartsWith(prefix, StringComparison.Ordinal) ||
            rangeHeader.Contains(',', StringComparison.Ordinal))
        {
            range = default;
            return false;
        }

        ReadOnlySpan<char> specification = rangeHeader.AsSpan(prefix.Length);
        int separator = specification.IndexOf('-');
        if (separator < 0 ||
            separator != specification.LastIndexOf('-'))
        {
            range = default;
            return false;
        }

        ReadOnlySpan<char> startText = specification[..separator];
        ReadOnlySpan<char> endText = specification[(separator + 1)..];
        if (startText.IsEmpty)
        {
            if (!TryParseNonnegativeInt64(endText, out long suffixLength) ||
                suffixLength == 0)
            {
                range = default;
                return false;
            }

            long start = suffixLength >= totalLength
                ? 0
                : totalLength - suffixLength;
            range = new PlayerMediaByteRange(
                start,
                totalLength - start,
                totalLength,
                isPartial: true);
            return true;
        }

        if (!TryParseNonnegativeInt64(startText, out long requestedStart) ||
            requestedStart >= totalLength)
        {
            range = default;
            return false;
        }

        long requestedEnd;
        if (endText.IsEmpty)
        {
            requestedEnd = totalLength - 1;
        }
        else if (!TryParseNonnegativeInt64(endText, out requestedEnd) ||
            requestedEnd < requestedStart)
        {
            range = default;
            return false;
        }

        long end = Math.Min(requestedEnd, totalLength - 1);
        range = new PlayerMediaByteRange(
            requestedStart,
            checked(end - requestedStart + 1),
            totalLength,
            isPartial: true);
        return true;
    }

    private static bool TryParseNonnegativeInt64(
        ReadOnlySpan<char> value,
        out long parsed)
    {
        if (value.IsEmpty)
        {
            parsed = default;
            return false;
        }

        long result = 0;
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                parsed = default;
                return false;
            }

            int digit = character - '0';
            if (result > (long.MaxValue - digit) / 10)
            {
                parsed = default;
                return false;
            }

            result = (result * 10) + digit;
        }

        parsed = result;
        return true;
    }
}

internal sealed class PlayerMediaContentStream : Stream
{
    private ValidatedArtifactReadStream? source;
    private readonly long sourceStart;
    private readonly long length;
    private long position;

    internal PlayerMediaContentStream(
        ValidatedArtifactReadStream source,
        long sourceStart,
        long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceStart < 0 ||
            length <= 0 ||
            sourceStart > source.ByteSize - length)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStart));
        }

        this.source = source;
        this.sourceStart = sourceStart;
        this.length = length;
        source.Position = sourceStart;
    }

    public override bool CanRead => source?.CanRead == true;

    public override bool CanSeek => source?.CanSeek == true;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            _ = GetSource();
            return length;
        }
    }

    public override long Position
    {
        get
        {
            _ = GetSource();
            return position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _ = GetSource();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ValidatedArtifactReadStream current = GetSource();
        int permitted = (int)Math.Min(buffer.Length, length - position);
        if (permitted == 0)
        {
            return 0;
        }

        current.Position = checked(sourceStart + position);
        int read = current.Read(buffer[..permitted]);
        if ((uint)read > (uint)permitted)
        {
            throw new IOException("The player media stream returned an invalid byte count.");
        }

        position = checked(position + read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(
            buffer.AsMemory(offset, count),
            cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ValidatedArtifactReadStream current = GetSource();
        int permitted = (int)Math.Min(buffer.Length, length - position);
        if (permitted == 0)
        {
            return 0;
        }

        current.Position = checked(sourceStart + position);
        int read = await current.ReadAsync(
            buffer[..permitted],
            cancellationToken).ConfigureAwait(false);
        if ((uint)read > (uint)permitted)
        {
            throw new IOException("The player media stream returned an invalid byte count.");
        }

        position = checked(position + read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _ = GetSource();
        long basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => position,
            SeekOrigin.End => length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        long target;
        try
        {
            target = checked(basis + offset);
        }
        catch (OverflowException exception)
        {
            throw new IOException(
                "The player media seek exceeds the response range.",
                exception);
        }

        if (target < 0 || target > length)
        {
            throw new IOException(
                "The player media seek exceeds the response range.");
        }

        position = target;
        GetSource().Position = checked(sourceStart + target);
        return position;
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("The player media stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The player media stream is read-only.");

    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("The player media stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        ValidatedArtifactReadStream? owned = Interlocked.Exchange(
            ref source,
            null);
        if (disposing)
        {
            owned?.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        ValidatedArtifactReadStream? owned = Interlocked.Exchange(
            ref source,
            null);
        if (owned is not null)
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private ValidatedArtifactReadStream GetSource() =>
        source ?? throw new ObjectDisposedException(
            nameof(PlayerMediaContentStream));
}
