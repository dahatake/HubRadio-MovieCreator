using System.Diagnostics.CodeAnalysis;

namespace HubRadio.Core.Identity;

public sealed class EntityId<TTag> : IEquatable<EntityId<TTag>>
{
    private const int GuidDLength = 36;

    private EntityId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static EntityId<TTag> New() => From(Guid.NewGuid());

    public static EntityId<TTag> From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An entity ID cannot be an empty GUID.", nameof(value));
        }

        return new EntityId<TTag>(value);
    }

    public static EntityId<TTag> Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out EntityId<TTag>? entityId))
        {
            throw new FormatException("An entity ID must be a non-empty GUID in D format.");
        }

        return entityId;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out EntityId<TTag>? entityId)
    {
        if (value is null ||
            value.Length != GuidDLength ||
            !Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            entityId = null;
            return false;
        }

        entityId = new EntityId<TTag>(parsed);
        return true;
    }

    public bool Equals(EntityId<TTag>? other) =>
        other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) =>
        obj is EntityId<TTag> other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString("D");

    public static bool operator ==(EntityId<TTag>? left, EntityId<TTag>? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(EntityId<TTag>? left, EntityId<TTag>? right) =>
        !(left == right);
}