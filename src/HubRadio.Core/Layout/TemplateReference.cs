using HubRadio.Core.Identity;

namespace HubRadio.Core.Layout;

public sealed class TemplateIdTag
{
}

public sealed class TemplateReference : IEquatable<TemplateReference>
{
    public TemplateReference(
        EntityId<TemplateIdTag> id,
        string name,
        ContentDigest versionDigest)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(versionDigest);

        Id = id;
        Name = ContractText.RequireNonBlank(name, nameof(name), "A template name");
        VersionDigest = versionDigest;
    }

    public EntityId<TemplateIdTag> Id { get; }

    public string Name { get; }

    public ContentDigest VersionDigest { get; }

    public bool Equals(TemplateReference? other)
    {
        return other is not null
            && Id == other.Id
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && VersionDigest == other.VersionDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is TemplateReference other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Id,
            StringComparer.Ordinal.GetHashCode(Name),
            VersionDigest);
    }
}