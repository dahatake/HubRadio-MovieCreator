using HubRadio.Core.Identity;
using HubRadio.Core.Time;

namespace HubRadio.Core.Layout;

public sealed class CueIdTag
{
}

public enum CueKind
{
    Subtitle,
    Graphic,
    Audio,
}

public sealed class Cue : IEquatable<Cue>
{
    public Cue(
        EntityId<CueIdTag> id,
        CueKind kind,
        TimeRange range,
        string? regionName = null)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!Enum.IsDefined(typeof(CueKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown cue kind.");
        }

        if (range.Start < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "An output cue must not start before zero.");
        }

        if (range.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "An output cue must have a positive duration.");
        }

        string? validatedRegionName = regionName is null
            ? null
            : ContractText.RequireNonBlank(regionName, nameof(regionName), "A cue region name");

        if (kind == CueKind.Audio && validatedRegionName is not null)
        {
            throw new ArgumentException(
                "An audio cue cannot reference a visual layout region.",
                nameof(regionName));
        }

        Id = id;
        Kind = kind;
        Range = range;
        RegionName = validatedRegionName;
    }

    public EntityId<CueIdTag> Id { get; }

    public CueKind Kind { get; }

    public TimeRange Range { get; }

    public string? RegionName { get; }

    public bool Equals(Cue? other)
    {
        return other is not null
            && Id == other.Id
            && Kind == other.Kind
            && Range == other.Range
            && StringComparer.Ordinal.Equals(RegionName, other.RegionName);
    }

    public override bool Equals(object? obj)
    {
        return obj is Cue other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Id,
            Kind,
            Range,
            RegionName is null ? 0 : StringComparer.Ordinal.GetHashCode(RegionName));
    }
}