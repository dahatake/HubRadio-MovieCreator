using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Templates;

public sealed class AppliedBrandTemplate : IEquatable<AppliedBrandTemplate>
{
    internal AppliedBrandTemplate(
        EntityId<SeriesIdTag> seriesId,
        TemplateReference reference,
        BrandTemplateKind kind,
        IReadOnlyList<TemplateParameterAssignment> parameters)
    {
        SeriesId = seriesId;
        Reference = reference;
        Kind = kind;
        Parameters = Array.AsReadOnly(parameters.ToArray());
    }

    public EntityId<SeriesIdTag> SeriesId { get; }

    public TemplateReference Reference { get; }

    public BrandTemplateKind Kind { get; }

    public IReadOnlyList<TemplateParameterAssignment> Parameters { get; }

    public bool Equals(AppliedBrandTemplate? other)
    {
        return other is not null
            && SeriesId == other.SeriesId
            && Reference.Equals(other.Reference)
            && Kind == other.Kind
            && Parameters.SequenceEqual(other.Parameters);
    }

    public override bool Equals(object? obj)
    {
        return obj is AppliedBrandTemplate other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SeriesId);
        hash.Add(Reference);
        hash.Add(Kind);
        foreach (TemplateParameterAssignment parameter in Parameters)
        {
            hash.Add(parameter);
        }

        return hash.ToHashCode();
    }
}