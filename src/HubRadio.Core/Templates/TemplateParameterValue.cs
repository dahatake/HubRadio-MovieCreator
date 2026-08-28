using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Templates;

public enum TemplateParameterValueKind
{
    Text,
    Integer,
    Boolean,
    Rational,
    AssetReference,
}

public sealed class TemplateParameterValue : IEquatable<TemplateParameterValue>
{
    private TemplateParameterValue(
        TemplateParameterValueKind kind,
        string? textValue = null,
        long? integerValue = null,
        bool? booleanValue = null,
        RationalNumber? rationalValue = null,
        EntityId<AssetIdTag>? assetId = null,
        ContentDigest? assetDigest = null)
    {
        Kind = kind;
        TextValue = textValue;
        IntegerValue = integerValue;
        BooleanValue = booleanValue;
        RationalValue = rationalValue;
        AssetId = assetId;
        AssetDigest = assetDigest;
    }

    public TemplateParameterValueKind Kind { get; }

    public string? TextValue { get; }

    public long? IntegerValue { get; }

    public bool? BooleanValue { get; }

    public RationalNumber? RationalValue { get; }

    public EntityId<AssetIdTag>? AssetId { get; }

    public ContentDigest? AssetDigest { get; }

    public static TemplateParameterValue FromText(string value)
    {
        return new TemplateParameterValue(
            TemplateParameterValueKind.Text,
            textValue: TemplateContract.RequireNonBlankText(value, nameof(value), "A template text value"));
    }

    public static TemplateParameterValue FromInteger(long value)
    {
        return new TemplateParameterValue(
            TemplateParameterValueKind.Integer,
            integerValue: value);
    }

    public static TemplateParameterValue FromBoolean(bool value)
    {
        return new TemplateParameterValue(
            TemplateParameterValueKind.Boolean,
            booleanValue: value);
    }

    public static TemplateParameterValue FromRational(RationalNumber value)
    {
        return new TemplateParameterValue(
            TemplateParameterValueKind.Rational,
            rationalValue: value);
    }

    public static TemplateParameterValue FromAssetReference(
        EntityId<AssetIdTag> assetId,
        ContentDigest assetDigest)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        ArgumentNullException.ThrowIfNull(assetDigest);

        return new TemplateParameterValue(
            TemplateParameterValueKind.AssetReference,
            assetId: assetId,
            assetDigest: assetDigest);
    }

    public bool Equals(TemplateParameterValue? other)
    {
        return other is not null
            && Kind == other.Kind
            && StringComparer.Ordinal.Equals(TextValue, other.TextValue)
            && IntegerValue == other.IntegerValue
            && BooleanValue == other.BooleanValue
            && RationalValue == other.RationalValue
            && AssetId == other.AssetId
            && AssetDigest == other.AssetDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is TemplateParameterValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Kind);
        hash.Add(TextValue, StringComparer.Ordinal);
        hash.Add(IntegerValue);
        hash.Add(BooleanValue);
        hash.Add(RationalValue);
        hash.Add(AssetId);
        hash.Add(AssetDigest);
        return hash.ToHashCode();
    }

    public static bool operator ==(TemplateParameterValue? left, TemplateParameterValue? right)
    {
        return left?.Equals(right) ?? right is null;
    }

    public static bool operator !=(TemplateParameterValue? left, TemplateParameterValue? right)
    {
        return !(left == right);
    }
}