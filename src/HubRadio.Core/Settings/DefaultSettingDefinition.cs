using System.Numerics;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Settings;

public enum DefaultSettingGroup
{
    ProjectAndOutput,
    Editing,
    SubtitlesAndGraphics,
    AudioAndAssets,
    PrivacyAndStorage,
}

public enum DefaultSettingValueKind
{
    Boolean,
    Integer,
    Rational,
    Text,
    TextList,
}

public enum DefaultSettingUnit
{
    None,
    Count,
    Milliseconds,
    Seconds,
    Frames,
    Pixels,
    Hertz,
    BitsPerSecond,
    Percent,
    CharactersPerSecond,
    FullWidthCharacters,
    Decibels,
    DecibelsFullScale,
    LoudnessUnitsFullScale,
}

public enum DefaultSettingEvidenceKind
{
    Specification,
    OfficialRecommendation,
    PeerReviewedFinding,
    PracticalGuidance,
    CurrentImplementation,
    ProductDecision,
    UserDecision,
}

public sealed class DefaultSettingEvidence : IEquatable<DefaultSettingEvidence>
{
    public DefaultSettingEvidence(
        DefaultSettingEvidenceKind kind,
        string sourceId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown settings evidence kind.");
        }

        SourceId = RequireSourceId(sourceId, kind);
        Kind = kind;
    }

    public DefaultSettingEvidenceKind Kind { get; }

    public string SourceId { get; }

    public bool Equals(DefaultSettingEvidence? other) =>
        other is not null &&
        Kind == other.Kind &&
        StringComparer.Ordinal.Equals(SourceId, other.SourceId);

    public override bool Equals(object? obj) =>
        obj is DefaultSettingEvidence other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        Kind,
        StringComparer.Ordinal.GetHashCode(SourceId));

    private static string RequireSourceId(
        string? sourceId,
        DefaultSettingEvidenceKind kind)
    {
        if (sourceId is null)
        {
            throw new ArgumentNullException(nameof(sourceId));
        }

        bool valid = sourceId switch
        {
            "productDecision" => kind == DefaultSettingEvidenceKind.ProductDecision,
            _ when IsLedgerId(sourceId, 'U') => kind == DefaultSettingEvidenceKind.UserDecision,
            _ when IsLedgerId(sourceId, 'C') => kind == DefaultSettingEvidenceKind.CurrentImplementation,
            _ when IsLedgerId(sourceId, 'R') => kind is
                DefaultSettingEvidenceKind.Specification or
                DefaultSettingEvidenceKind.OfficialRecommendation or
                DefaultSettingEvidenceKind.PeerReviewedFinding or
                DefaultSettingEvidenceKind.PracticalGuidance,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException(
                "A settings evidence source must match its Udd, Cdd, Rdd, or productDecision evidence kind.",
                nameof(sourceId));
        }

        return sourceId;
    }

    private static bool IsLedgerId(string value, char prefix) =>
        value.Length == 3 &&
        value[0] == prefix &&
        value[1] is >= '0' and <= '9' &&
        value[2] is >= '0' and <= '9';
}

public sealed class DefaultSettingValue : IEquatable<DefaultSettingValue>
{
    private static readonly IReadOnlyList<string> EmptyTextList =
        Array.AsReadOnly(Array.Empty<string>());

    private DefaultSettingValue(
        DefaultSettingValueKind kind,
        bool? booleanValue,
        long? integerValue,
        RationalNumber? rationalValue,
        string? textValue,
        IReadOnlyList<string>? textListValue)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        IntegerValue = integerValue;
        RationalValue = rationalValue;
        TextValue = textValue;
        TextListValue = textListValue ?? EmptyTextList;
    }

    public DefaultSettingValueKind Kind { get; }

    public bool? BooleanValue { get; }

    public long? IntegerValue { get; }

    public RationalNumber? RationalValue { get; }

    public string? TextValue { get; }

    public IReadOnlyList<string> TextListValue { get; }

    public static DefaultSettingValue FromBoolean(bool value) =>
        new(DefaultSettingValueKind.Boolean, value, null, null, null, null);

    public static DefaultSettingValue FromInteger(long value) =>
        new(DefaultSettingValueKind.Integer, null, value, null, null, null);

    public static DefaultSettingValue FromRational(long numerator, long denominator) =>
        new(
            DefaultSettingValueKind.Rational,
            null,
            null,
            new RationalNumber(numerator, denominator),
            null,
            null);

    public static DefaultSettingValue FromText(string value) =>
        new(
            DefaultSettingValueKind.Text,
            null,
            null,
            null,
            ContractText.RequireNonBlankStrictUtf8Substantive(
                value,
                nameof(value),
                "A settings text value"),
            null);

    public static DefaultSettingValue FromTextList(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string[] copiedValues = new string[values.Count];
        HashSet<string> uniqueValues = new(StringComparer.Ordinal);
        for (int index = 0; index < values.Count; index++)
        {
            string value = ContractText.RequireNonBlankStrictUtf8Substantive(
                values[index],
                nameof(values),
                "A settings text-list value");
            if (!uniqueValues.Add(value))
            {
                throw new ArgumentException(
                    "A settings text-list value must not contain duplicates.",
                    nameof(values));
            }

            copiedValues[index] = value;
        }

        return new DefaultSettingValue(
            DefaultSettingValueKind.TextList,
            null,
            null,
            null,
            null,
            Array.AsReadOnly(copiedValues));
    }

    public bool Equals(DefaultSettingValue? other)
    {
        return other is not null &&
            Kind == other.Kind &&
            BooleanValue == other.BooleanValue &&
            IntegerValue == other.IntegerValue &&
            RationalValue == other.RationalValue &&
            StringComparer.Ordinal.Equals(TextValue, other.TextValue) &&
            TextListValue.SequenceEqual(other.TextListValue, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj) =>
        obj is DefaultSettingValue other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Kind);
        hash.Add(BooleanValue);
        hash.Add(IntegerValue);
        hash.Add(RationalValue);
        hash.Add(TextValue, StringComparer.Ordinal);
        foreach (string value in TextListValue)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

public sealed class DefaultSettingDefinition
{
    private const int MaximumKeyLength = 128;

    public DefaultSettingDefinition(
        string key,
        DefaultSettingGroup group,
        bool isAdvanced,
        string displayName,
        string description,
        DefaultSettingUnit unit,
        DefaultSettingValue defaultValue,
        DefaultSettingValue? minimum,
        DefaultSettingValue? maximum,
        IReadOnlyList<string>? allowedTextValues,
        IReadOnlyList<DefaultSettingEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!Enum.IsDefined(group))
        {
            throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown settings group.");
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown settings unit.");
        }

        ValidateUnit(defaultValue.Kind, unit);
        ValidateBoundary(minimum, defaultValue.Kind, nameof(minimum));
        ValidateBoundary(maximum, defaultValue.Kind, nameof(maximum));
        if (minimum is not null && maximum is not null && Compare(minimum, maximum) > 0)
        {
            throw new ArgumentException(
                "A settings minimum must not be greater than its maximum.",
                nameof(minimum));
        }

        string[] copiedAllowedValues = CopyAllowedTextValues(
            allowedTextValues,
            defaultValue.Kind);
        DefaultSettingEvidence[] copiedEvidence = CopyEvidence(evidence);

        Key = RequireKey(key);
        Group = group;
        IsAdvanced = isAdvanced;
        DisplayName = ContractText.RequireNonBlankStrictUtf8Substantive(
            displayName,
            nameof(displayName),
            "A settings display name");
        Description = ContractText.RequireNonBlankStrictUtf8Substantive(
            description,
            nameof(description),
            "A settings description");
        Unit = unit;
        DefaultValue = defaultValue;
        Minimum = minimum;
        Maximum = maximum;
        AllowedTextValues = Array.AsReadOnly(copiedAllowedValues);
        Evidence = Array.AsReadOnly(copiedEvidence);

        ValidateValue(defaultValue, nameof(defaultValue));
    }

    public string Key { get; }

    public DefaultSettingGroup Group { get; }

    public bool IsAdvanced { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public DefaultSettingUnit Unit { get; }

    public DefaultSettingValue DefaultValue { get; }

    public DefaultSettingValue? Minimum { get; }

    public DefaultSettingValue? Maximum { get; }

    public IReadOnlyList<string> AllowedTextValues { get; }

    public IReadOnlyList<DefaultSettingEvidence> Evidence { get; }

    internal void ValidateValue(DefaultSettingValue value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Kind != DefaultValue.Kind)
        {
            throw new ArgumentException(
                "A resolved settings value must have the definition's value kind.",
                parameterName);
        }

        if (Minimum is not null && Compare(value, Minimum) < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A resolved settings value is below the definition's minimum.");
        }

        if (Maximum is not null && Compare(value, Maximum) > 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A resolved settings value is above the definition's maximum.");
        }

        if (AllowedTextValues.Count > 0 &&
            (value.TextValue is null ||
             !AllowedTextValues.Contains(value.TextValue, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "A resolved settings text value is not in the definition's allowlist.",
                parameterName);
        }
    }

    private static string RequireKey(string? key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (key.Length == 0 || key.Length > MaximumKeyLength)
        {
            throw new ArgumentException(
                "A settings key must contain between 1 and 128 ASCII characters.",
                nameof(key));
        }

        bool atSegmentStart = true;
        foreach (char character in key)
        {
            if (atSegmentStart)
            {
                if (character is < 'a' or > 'z')
                {
                    throw new ArgumentException(
                        "Every settings-key segment must start with a lowercase ASCII letter.",
                        nameof(key));
                }

                atSegmentStart = false;
                continue;
            }

            if (character == '.')
            {
                atSegmentStart = true;
            }
            else if (character is not (>= 'a' and <= 'z') &&
                     character is not (>= 'A' and <= 'Z') &&
                     character is not (>= '0' and <= '9'))
            {
                throw new ArgumentException(
                    "A settings key may contain only ASCII letters, digits, and segment dots.",
                    nameof(key));
            }
        }

        if (atSegmentStart)
        {
            throw new ArgumentException(
                "A settings key must not end with a segment dot.",
                nameof(key));
        }

        return key;
    }

    private static void ValidateUnit(
        DefaultSettingValueKind kind,
        DefaultSettingUnit unit)
    {
        if (kind is DefaultSettingValueKind.Boolean or
            DefaultSettingValueKind.Text or
            DefaultSettingValueKind.TextList &&
            unit != DefaultSettingUnit.None)
        {
            throw new ArgumentException(
                "Boolean and text settings must use the none unit.",
                nameof(unit));
        }
    }

    private static void ValidateBoundary(
        DefaultSettingValue? boundary,
        DefaultSettingValueKind valueKind,
        string parameterName)
    {
        if (boundary is null)
        {
            return;
        }

        if (valueKind is not (DefaultSettingValueKind.Integer or DefaultSettingValueKind.Rational) ||
            boundary.Kind != valueKind)
        {
            throw new ArgumentException(
                "Settings boundaries must use the same numeric kind as the default value.",
                parameterName);
        }
    }

    private static string[] CopyAllowedTextValues(
        IReadOnlyList<string>? allowedTextValues,
        DefaultSettingValueKind valueKind)
    {
        if (allowedTextValues is null || allowedTextValues.Count == 0)
        {
            return [];
        }

        if (valueKind != DefaultSettingValueKind.Text)
        {
            throw new ArgumentException(
                "Only text settings may declare allowed text values.",
                nameof(allowedTextValues));
        }

        HashSet<string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < allowedTextValues.Count; index++)
        {
            string value = ContractText.RequireNonBlankStrictUtf8Substantive(
                allowedTextValues[index],
                nameof(allowedTextValues),
                "An allowed settings text value");
            if (!values.Add(value))
            {
                throw new ArgumentException(
                    "Allowed settings text values must be unique.",
                    nameof(allowedTextValues));
            }
        }

        string[] sorted = values.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    private static DefaultSettingEvidence[] CopyEvidence(
        IReadOnlyList<DefaultSettingEvidence> evidence)
    {
        if (evidence.Count == 0)
        {
            throw new ArgumentException(
                "A default setting must have at least one evidence reference.",
                nameof(evidence));
        }

        DefaultSettingEvidence[] copied = new DefaultSettingEvidence[evidence.Count];
        HashSet<DefaultSettingEvidence> unique = [];
        for (int index = 0; index < evidence.Count; index++)
        {
            DefaultSettingEvidence item = evidence[index] ??
                throw new ArgumentException(
                    "Settings evidence must not contain null elements.",
                    nameof(evidence));
            if (!unique.Add(item))
            {
                throw new ArgumentException(
                    "Settings evidence references must be unique.",
                    nameof(evidence));
            }

            copied[index] = item;
        }

        Array.Sort(
            copied,
            static (left, right) =>
            {
                int sourceComparison = StringComparer.Ordinal.Compare(
                    left.SourceId,
                    right.SourceId);
                return sourceComparison != 0
                    ? sourceComparison
                    : left.Kind.CompareTo(right.Kind);
            });
        return copied;
    }

    private static int Compare(DefaultSettingValue left, DefaultSettingValue right)
    {
        if (left.Kind != right.Kind)
        {
            throw new InvalidOperationException("Only settings values of the same kind can be compared.");
        }

        return left.Kind switch
        {
            DefaultSettingValueKind.Integer =>
                left.IntegerValue!.Value.CompareTo(right.IntegerValue!.Value),
            DefaultSettingValueKind.Rational => CompareRationals(
                left.RationalValue!.Value,
                right.RationalValue!.Value),
            _ => throw new InvalidOperationException(
                "Only numeric settings values can be compared."),
        };
    }

    private static int CompareRationals(RationalNumber left, RationalNumber right)
    {
        BigInteger leftValue = (BigInteger)left.Numerator * right.Denominator;
        BigInteger rightValue = (BigInteger)right.Numerator * left.Denominator;
        return leftValue.CompareTo(rightValue);
    }
}
