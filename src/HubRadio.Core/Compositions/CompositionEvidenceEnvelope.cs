using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Compositions;

public sealed class CompositionEvidenceEnvelope
{
    public const string AutoDecisionKind = "autoDecision";

    /// <summary>
    /// Maximum canonical evidence size accepted for resource safety. This is not a
    /// product decision or quality threshold.
    /// </summary>
    public const int MaximumCanonicalJsonUtf8Bytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private CompositionEvidenceEnvelope(
        string kind,
        ContentDigest contentDigest,
        string canonicalJson)
    {
        Kind = kind;
        ContentDigest = contentDigest;
        CanonicalJson = canonicalJson;
    }

    public string Kind { get; }

    public ContentDigest ContentDigest { get; }

    public string CanonicalJson { get; }

    public static CompositionEvidenceEnvelope Create(
        string kind,
        string canonicalJson)
    {
        string validatedKind = ValidateKind(kind);
        string validatedJson = ValidateCanonicalJson(canonicalJson);
        return new CompositionEvidenceEnvelope(
            validatedKind,
            ContentDigest.Compute(StrictUtf8.GetBytes(validatedJson)),
            validatedJson);
    }

    public static CompositionEvidenceEnvelope Restore(
        string kind,
        ContentDigest contentDigest,
        string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(contentDigest);
        string validatedKind = ValidateKind(kind);
        string validatedJson = ValidateCanonicalJson(canonicalJson);
        ContentDigest computed = ContentDigest.Compute(StrictUtf8.GetBytes(validatedJson));
        if (computed != contentDigest)
        {
            throw new FormatException(
                "The composition evidence digest does not match its canonical JSON.");
        }

        return new CompositionEvidenceEnvelope(
            validatedKind,
            contentDigest,
            validatedJson);
    }

    private static string ValidateKind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!string.Equals(kind, AutoDecisionKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only the V2-007 autoDecision composition evidence kind is supported.",
                nameof(kind));
        }

        return kind;
    }

    private static string ValidateCanonicalJson(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        try
        {
            byte[] utf8 = StrictUtf8.GetBytes(canonicalJson);
            if (utf8.Length == 0 || utf8.Length > MaximumCanonicalJsonUtf8Bytes)
            {
                throw new FormatException(
                    "Composition evidence exceeds its UTF-8 resource-safety envelope.");
            }

            byte[] canonical = HubRadio.Core.Serialization.CanonicalJson
                .CanonicalizeToUtf8(utf8);
            if (!utf8.AsSpan().SequenceEqual(canonical))
            {
                throw new FormatException(
                    "Composition evidence must use canonical JSON.");
            }

            return canonicalJson;
        }
        catch (EncoderFallbackException exception)
        {
            throw new FormatException(
                "Composition evidence must be valid UTF-8 text.",
                exception);
        }
    }
}