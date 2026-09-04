using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Compositions;

public sealed class CompositionEvidenceEnvelope
{
    public const string AutoDecisionKind = "autoDecision";
    public const string StandardCompositionPlanKind = "standardCompositionPlan";

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
        if (!string.Equals(kind, AutoDecisionKind, StringComparison.Ordinal)
            && !string.Equals(
                kind,
                StandardCompositionPlanKind,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The composition evidence kind is not supported.",
                nameof(kind));
        }

        return kind;
    }

    private static string ValidateCanonicalJson(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        try
        {
            if (canonicalJson.Length == 0
                || canonicalJson.Length > MaximumCanonicalJsonUtf8Bytes
                || StrictUtf8.GetByteCount(canonicalJson)
                    > MaximumCanonicalJsonUtf8Bytes)
            {
                throw new FormatException(
                    "Composition evidence exceeds its UTF-8 resource-safety envelope.");
            }

            byte[] utf8 = StrictUtf8.GetBytes(canonicalJson);
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