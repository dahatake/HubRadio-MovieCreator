using HubRadio.Core.Identity;

namespace HubRadio.Core.Layout;

public sealed class LayoutSpecIdTag
{
}

public enum LayoutRegionRole
{
    Video,
    Material,
    Subtitle,
    Graphic,
}

public sealed class LayoutRegion : IEquatable<LayoutRegion>
{
    public LayoutRegion(string name, NormalizedRect rect, LayoutRegionRole role)
    {
        ArgumentNullException.ThrowIfNull(rect);

        if (!Enum.IsDefined(typeof(LayoutRegionRole), role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown layout region role.");
        }

        Name = ContractText.RequireNonBlank(name, nameof(name), "A region name");
        Rect = rect;
        Role = role;
    }

    public string Name { get; }

    public NormalizedRect Rect { get; }

    public LayoutRegionRole Role { get; }

    public bool Equals(LayoutRegion? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && Rect == other.Rect
            && Role == other.Role;
    }

    public override bool Equals(object? obj)
    {
        return obj is LayoutRegion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Name),
            Rect,
            Role);
    }
}

public sealed class SafeZone : IEquatable<SafeZone>
{
    public SafeZone(string name, NormalizedRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);

        Name = ContractText.RequireNonBlank(name, nameof(name), "A safe-zone name");
        Rect = rect;
    }

    public string Name { get; }

    public NormalizedRect Rect { get; }

    public bool Equals(SafeZone? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && Rect == other.Rect;
    }

    public override bool Equals(object? obj)
    {
        return obj is SafeZone other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), Rect);
    }
}

public sealed class LayoutSpec
{
    public LayoutSpec(
        EntityId<LayoutSpecIdTag> id,
        int outputAspectWidth,
        int outputAspectHeight,
        IReadOnlyList<LayoutRegion> regions,
        IReadOnlyList<SafeZone> safeZones)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(safeZones);

        if (outputAspectWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputAspectWidth),
                outputAspectWidth,
                "Output aspect width must be positive.");
        }

        if (outputAspectHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputAspectHeight),
                outputAspectHeight,
                "Output aspect height must be positive.");
        }

        LayoutRegion[] copiedRegions = CopyUniqueRegions(regions);
        SafeZone[] copiedSafeZones = CopyUniqueSafeZones(safeZones);

        Id = id;
        OutputAspectWidth = outputAspectWidth;
        OutputAspectHeight = outputAspectHeight;
        Regions = Array.AsReadOnly(copiedRegions);
        SafeZones = Array.AsReadOnly(copiedSafeZones);
    }

    public EntityId<LayoutSpecIdTag> Id { get; }

    public int OutputAspectWidth { get; }

    public int OutputAspectHeight { get; }

    public IReadOnlyList<LayoutRegion> Regions { get; }

    public IReadOnlyList<SafeZone> SafeZones { get; }

    private static LayoutRegion[] CopyUniqueRegions(IReadOnlyList<LayoutRegion> regions)
    {
        LayoutRegion[] copied = new LayoutRegion[regions.Count];
        HashSet<string> names = new(StringComparer.Ordinal);

        for (int index = 0; index < copied.Length; index++)
        {
            LayoutRegion? region = regions[index];
            if (region is null)
            {
                throw new ArgumentException(
                    "Regions must not contain null elements.",
                    nameof(regions));
            }

            if (!names.Add(region.Name))
            {
                throw new ArgumentException(
                    "Region names must be unique.",
                    nameof(regions));
            }

            copied[index] = region;
        }

        return copied;
    }

    private static SafeZone[] CopyUniqueSafeZones(IReadOnlyList<SafeZone> safeZones)
    {
        SafeZone[] copied = new SafeZone[safeZones.Count];
        HashSet<string> names = new(StringComparer.Ordinal);

        for (int index = 0; index < copied.Length; index++)
        {
            SafeZone? safeZone = safeZones[index];
            if (safeZone is null)
            {
                throw new ArgumentException(
                    "Safe zones must not contain null elements.",
                    nameof(safeZones));
            }

            if (!names.Add(safeZone.Name))
            {
                throw new ArgumentException(
                    "Safe-zone names must be unique.",
                    nameof(safeZones));
            }

            copied[index] = safeZone;
        }

        return copied;
    }
}