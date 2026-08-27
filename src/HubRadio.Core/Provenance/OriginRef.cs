using HubRadio.Core.Identity;

namespace HubRadio.Core.Provenance;

public enum OriginKind
{
    SourceSpan,
    Asset,
    EditorialRecord,
}

public sealed class OriginRef : IEquatable<OriginRef>
{
    private OriginRef(EntityId<SourceSpanIdTag> sourceSpanId)
    {
        Kind = OriginKind.SourceSpan;
        SourceSpanId = sourceSpanId;
    }

    private OriginRef(EntityId<AssetIdTag> assetId)
    {
        Kind = OriginKind.Asset;
        AssetId = assetId;
    }

    private OriginRef(EntityId<EditorialRecordIdTag> editorialRecordId)
    {
        Kind = OriginKind.EditorialRecord;
        EditorialRecordId = editorialRecordId;
    }

    public OriginKind Kind { get; }

    public EntityId<SourceSpanIdTag>? SourceSpanId { get; }

    public EntityId<AssetIdTag>? AssetId { get; }

    public EntityId<EditorialRecordIdTag>? EditorialRecordId { get; }

    public static OriginRef FromSourceSpan(EntityId<SourceSpanIdTag> sourceSpanId)
    {
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        return new OriginRef(sourceSpanId);
    }

    public static OriginRef FromAsset(EntityId<AssetIdTag> assetId)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        return new OriginRef(assetId);
    }

    public static OriginRef FromEditorialRecord(
        EntityId<EditorialRecordIdTag> editorialRecordId)
    {
        ArgumentNullException.ThrowIfNull(editorialRecordId);
        return new OriginRef(editorialRecordId);
    }

    public bool Equals(OriginRef? other)
    {
        if (other is null || Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            OriginKind.SourceSpan => SourceSpanId == other.SourceSpanId,
            OriginKind.Asset => AssetId == other.AssetId,
            OriginKind.EditorialRecord => EditorialRecordId == other.EditorialRecordId,
            _ => false,
        };
    }

    public override bool Equals(object? obj)
    {
        return obj is OriginRef other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Kind switch
        {
            OriginKind.SourceSpan => HashCode.Combine(Kind, SourceSpanId),
            OriginKind.Asset => HashCode.Combine(Kind, AssetId),
            OriginKind.EditorialRecord => HashCode.Combine(Kind, EditorialRecordId),
            _ => Kind.GetHashCode(),
        };
    }

    public static bool operator ==(OriginRef? left, OriginRef? right)
    {
        return left?.Equals(right) ?? right is null;
    }

    public static bool operator !=(OriginRef? left, OriginRef? right)
    {
        return !(left == right);
    }
}
