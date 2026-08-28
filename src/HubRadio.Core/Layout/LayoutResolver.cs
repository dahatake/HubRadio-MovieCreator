using System.Numerics;
using HubRadio.Core.Identity;

namespace HubRadio.Core.Layout;

public sealed class PixelRect : IEquatable<PixelRect>
{
    internal PixelRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public bool Equals(PixelRect? other)
    {
        return other is not null
            && X == other.X
            && Y == other.Y
            && Width == other.Width
            && Height == other.Height;
    }

    public override bool Equals(object? obj)
    {
        return obj is PixelRect other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Width, Height);
    }
}

public sealed class ResolvedLayoutRegion
{
    internal ResolvedLayoutRegion(string name, PixelRect rect, LayoutRegionRole role)
    {
        Name = name;
        Rect = rect;
        Role = role;
    }

    public string Name { get; }

    public PixelRect Rect { get; }

    public LayoutRegionRole Role { get; }
}

public sealed class ResolvedSafeZone
{
    internal ResolvedSafeZone(string name, PixelRect rect)
    {
        Name = name;
        Rect = rect;
    }

    public string Name { get; }

    public PixelRect Rect { get; }
}

public sealed class ResolvedLayout
{
    internal ResolvedLayout(
        EntityId<LayoutSpecIdTag> layoutSpecId,
        int outputPixelWidth,
        int outputPixelHeight,
        ResolvedLayoutRegion[] regions,
        ResolvedSafeZone[] safeZones)
    {
        LayoutSpecId = layoutSpecId;
        OutputPixelWidth = outputPixelWidth;
        OutputPixelHeight = outputPixelHeight;
        Regions = Array.AsReadOnly(regions);
        SafeZones = Array.AsReadOnly(safeZones);
    }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public int OutputPixelWidth { get; }

    public int OutputPixelHeight { get; }

    public IReadOnlyList<ResolvedLayoutRegion> Regions { get; }

    public IReadOnlyList<ResolvedSafeZone> SafeZones { get; }
}

public static class LayoutResolver
{
    public static ResolvedLayout Resolve(
        LayoutSpec layoutSpec,
        int outputPixelWidth,
        int outputPixelHeight)
    {
        ArgumentNullException.ThrowIfNull(layoutSpec);

        if (outputPixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputPixelWidth),
                outputPixelWidth,
                "Output pixel width must be positive.");
        }

        if (outputPixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputPixelHeight),
                outputPixelHeight,
                "Output pixel height must be positive.");
        }

        BigInteger expectedWidthProduct =
            (BigInteger)outputPixelHeight * layoutSpec.OutputAspectWidth;
        BigInteger actualWidthProduct =
            (BigInteger)outputPixelWidth * layoutSpec.OutputAspectHeight;
        if (actualWidthProduct != expectedWidthProduct)
        {
            throw new ArgumentException(
                "Output pixel dimensions must match the LayoutSpec aspect ratio.",
                nameof(outputPixelWidth));
        }

        ResolvedLayoutRegion[] regions = new ResolvedLayoutRegion[layoutSpec.Regions.Count];
        for (int index = 0; index < regions.Length; index++)
        {
            LayoutRegion source = layoutSpec.Regions[index];
            regions[index] = new ResolvedLayoutRegion(
                source.Name,
                ResolveRect(source.Rect, outputPixelWidth, outputPixelHeight),
                source.Role);
        }

        ResolvedSafeZone[] safeZones = new ResolvedSafeZone[layoutSpec.SafeZones.Count];
        for (int index = 0; index < safeZones.Length; index++)
        {
            SafeZone source = layoutSpec.SafeZones[index];
            safeZones[index] = new ResolvedSafeZone(
                source.Name,
                ResolveRect(source.Rect, outputPixelWidth, outputPixelHeight));
        }

        return new ResolvedLayout(
            layoutSpec.Id,
            outputPixelWidth,
            outputPixelHeight,
            regions,
            safeZones);
    }

    private static PixelRect ResolveRect(
        NormalizedRect rect,
        int outputPixelWidth,
        int outputPixelHeight)
    {
        int x = ResolveCoordinate(rect.X, outputPixelWidth);
        int y = ResolveCoordinate(rect.Y, outputPixelHeight);
        int width = ResolveCoordinate(rect.Width, outputPixelWidth);
        int height = ResolveCoordinate(rect.Height, outputPixelHeight);

        if (width <= 0
            || height <= 0
            || (long)x + width > outputPixelWidth
            || (long)y + height > outputPixelHeight)
        {
            throw new InvalidOperationException(
                "Resolved layout geometry exceeds the output pixel surface.");
        }

        return new PixelRect(x, y, width, height);
    }

    private static int ResolveCoordinate(RationalNumber value, int pixelExtent)
    {
        BigInteger scaledNumerator = (BigInteger)value.Numerator * pixelExtent;
        BigInteger quotient = BigInteger.DivRem(
            scaledNumerator,
            value.Denominator,
            out BigInteger remainder);
        if (!remainder.IsZero)
        {
            throw new InvalidOperationException(
                "A normalized layout coordinate does not resolve to an integer pixel.");
        }

        return checked((int)quotient);
    }
}