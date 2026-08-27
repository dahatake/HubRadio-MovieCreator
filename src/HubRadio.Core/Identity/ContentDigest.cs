using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace HubRadio.Core.Identity;

public sealed class ContentDigest : IEquatable<ContentDigest>
{
    private const int Sha256ByteLength = 32;
    private const int Sha256HexLength = Sha256ByteLength * 2;

    public ContentDigest(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        if (!TryNormalize(hex, out string normalizedHex))
        {
            throw new ArgumentException(
                $"A SHA-256 digest must contain exactly {Sha256HexLength} hexadecimal characters.",
                nameof(hex));
        }

        Hex = normalizedHex;
    }

    public string Hex { get; }

    public static ContentDigest Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        if (!TryParse(hex, out ContentDigest? digest))
        {
            throw new FormatException(
                $"A SHA-256 digest must contain exactly {Sha256HexLength} hexadecimal characters.");
        }

        return digest;
    }

    public static bool TryParse(
        string? hex,
        [NotNullWhen(true)] out ContentDigest? digest)
    {
        if (!TryNormalize(hex, out string normalizedHex))
        {
            digest = null;
            return false;
        }

        digest = new ContentDigest(normalizedHex);
        return true;
    }

    public static ContentDigest FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Sha256ByteLength)
        {
            throw new ArgumentException(
                $"A SHA-256 digest must contain exactly {Sha256ByteLength} bytes.",
                nameof(bytes));
        }

        return new ContentDigest(Convert.ToHexString(bytes));
    }

    public static ContentDigest Compute(ReadOnlySpan<byte> content) =>
        FromBytes(SHA256.HashData(content));

    public bool Equals(ContentDigest? other) =>
        other is not null && StringComparer.Ordinal.Equals(Hex, other.Hex);

    public override bool Equals(object? obj) =>
        obj is ContentDigest other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hex);

    public override string ToString() => Hex;

    public static bool operator ==(ContentDigest? left, ContentDigest? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(ContentDigest? left, ContentDigest? right) =>
        !(left == right);

    private static bool TryNormalize(string? hex, out string normalizedHex)
    {
        normalizedHex = string.Empty;

        if (hex is null || hex.Length != Sha256HexLength)
        {
            return false;
        }

        Span<char> normalized = stackalloc char[Sha256HexLength];

        for (int index = 0; index < hex.Length; index++)
        {
            char character = hex[index];

            if (character is >= '0' and <= '9' or >= 'a' and <= 'f')
            {
                normalized[index] = character;
            }
            else if (character is >= 'A' and <= 'F')
            {
                normalized[index] = (char)(character + ('a' - 'A'));
            }
            else
            {
                return false;
            }
        }

        normalizedHex = new string(normalized);
        return true;
    }
}