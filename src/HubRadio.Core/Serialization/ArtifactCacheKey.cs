using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Serialization;

public sealed class ArtifactCacheKey : IEquatable<ArtifactCacheKey>
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const string Domain = "HubRadio.MovieCreator.ArtifactCacheKey/v1";

    private ArtifactCacheKey(ContentDigest digest)
    {
        Digest = digest;
    }

    public ContentDigest Digest { get; }

    public static ArtifactCacheKey Create(
        string operation,
        ContentDigest configurationDigest,
        IReadOnlyList<ContentDigest> inputDigests)
    {
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(inputDigests);

        string validatedOperation = ContractText.RequireNonBlank(
            operation,
            nameof(operation),
            "An artifact operation");

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8Field(hash, tag: 1, Domain);
        AppendUtf8Field(hash, tag: 2, validatedOperation);
        AppendDigestField(hash, tag: 3, configurationDigest);
        AppendInt32(hash, inputDigests.Count);

        for (int index = 0; index < inputDigests.Count; index++)
        {
            ContentDigest? digest = inputDigests[index];
            if (digest is null)
            {
                throw new ArgumentException(
                    "Input digests must not contain null values.",
                    nameof(inputDigests));
            }

            AppendDigestField(hash, tag: 4, digest);
        }

        return new ArtifactCacheKey(ContentDigest.FromBytes(hash.GetHashAndReset()));
    }

    public bool Equals(ArtifactCacheKey? other)
    {
        return other is not null && Digest == other.Digest;
    }

    public override bool Equals(object? obj)
    {
        return obj is ArtifactCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Digest.GetHashCode();
    }

    public override string ToString()
    {
        return Digest.ToString();
    }

    public static bool operator ==(ArtifactCacheKey? left, ArtifactCacheKey? right)
    {
        return left?.Equals(right) ?? right is null;
    }

    public static bool operator !=(ArtifactCacheKey? left, ArtifactCacheKey? right)
    {
        return !(left == right);
    }

    private static void AppendUtf8Field(IncrementalHash hash, byte tag, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        AppendField(hash, tag, bytes);
    }

    private static void AppendDigestField(
        IncrementalHash hash,
        byte tag,
        ContentDigest digest)
    {
        AppendField(hash, tag, Convert.FromHexString(digest.Hex));
    }

    private static void AppendField(
        IncrementalHash hash,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], value.Length);
        hash.AppendData(header);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}