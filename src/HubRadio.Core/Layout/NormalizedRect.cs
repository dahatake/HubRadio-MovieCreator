using System.Numerics;

namespace HubRadio.Core.Layout;

public sealed class NormalizedRect : IEquatable<NormalizedRect>
{
    private static readonly RationalNumber One = new(1, 1);

    public NormalizedRect(
        RationalNumber x,
        RationalNumber y,
        RationalNumber width,
        RationalNumber height)
    {
        ValidateOffset(x, nameof(x));
        ValidateOffset(y, nameof(y));
        ValidateExtent(width, nameof(width));
        ValidateExtent(height, nameof(height));

        if (!SumIsAtMostOne(x, width))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "X plus width must not exceed one.");
        }

        if (!SumIsAtMostOne(y, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                "Y plus height must not exceed one.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public RationalNumber X { get; }

    public RationalNumber Y { get; }

    public RationalNumber Width { get; }

    public RationalNumber Height { get; }

    public bool Equals(NormalizedRect? other)
    {
        return other is not null
            && X == other.X
            && Y == other.Y
            && Width == other.Width
            && Height == other.Height;
    }

    public override bool Equals(object? obj)
    {
        return obj is NormalizedRect other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Width, Height);
    }

    public static bool operator ==(NormalizedRect? left, NormalizedRect? right)
    {
        return left?.Equals(right) ?? right is null;
    }

    public static bool operator !=(NormalizedRect? left, NormalizedRect? right)
    {
        return !(left == right);
    }

    private static void ValidateOffset(RationalNumber value, string parameterName)
    {
        if (value < default(RationalNumber) || value > One)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A normalized offset must be between zero and one inclusive.");
        }
    }

    private static void ValidateExtent(RationalNumber value, string parameterName)
    {
        if (value <= default(RationalNumber) || value > One)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A normalized extent must be greater than zero and at most one.");
        }
    }

    private static bool SumIsAtMostOne(RationalNumber offset, RationalNumber extent)
    {
        BigInteger denominator = (BigInteger)offset.Denominator * extent.Denominator;
        BigInteger numerator =
            ((BigInteger)offset.Numerator * extent.Denominator)
            + ((BigInteger)extent.Numerator * offset.Denominator);

        return numerator <= denominator;
    }
}