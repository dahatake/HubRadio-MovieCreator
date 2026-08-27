using System.Globalization;
using System.Numerics;

namespace HubRadio.Core.Layout;

public readonly struct RationalNumber : IComparable, IComparable<RationalNumber>, IEquatable<RationalNumber>
{
    // Store denominator - 1 so default(RationalNumber) is canonical 0/1.
    // Construction accepts at most Int64.MaxValue and GCD reduction never increases it,
    // so the encoded value is always in [0, Int64.MaxValue - 1].
    private readonly long _encodedDenominator;

    public RationalNumber(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(denominator),
                denominator,
                "Denominator must be positive.");
        }

        BigInteger divisor = BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(numerator),
            denominator);

        Numerator = (long)(numerator / divisor);
        _encodedDenominator = (long)(denominator / divisor) - 1;
    }

    public long Numerator { get; }

    public long Denominator => _encodedDenominator + 1;

    public int CompareTo(RationalNumber other)
    {
        BigInteger left = (BigInteger)Numerator * other.Denominator;
        BigInteger right = (BigInteger)other.Numerator * Denominator;
        return left.CompareTo(right);
    }

    int IComparable.CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (obj is not RationalNumber other)
        {
            throw new ArgumentException(
                $"Object must be a {nameof(RationalNumber)}.",
                nameof(obj));
        }

        return CompareTo(other);
    }

    public bool Equals(RationalNumber other)
    {
        return Numerator == other.Numerator && Denominator == other.Denominator;
    }

    public override bool Equals(object? obj)
    {
        return obj is RationalNumber other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Numerator, Denominator);
    }

    public override string ToString()
    {
        return string.Concat(
            Numerator.ToString(CultureInfo.InvariantCulture),
            "/",
            Denominator.ToString(CultureInfo.InvariantCulture));
    }

    public static bool operator ==(RationalNumber left, RationalNumber right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(RationalNumber left, RationalNumber right)
    {
        return !left.Equals(right);
    }

    public static bool operator <(RationalNumber left, RationalNumber right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(RationalNumber left, RationalNumber right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(RationalNumber left, RationalNumber right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(RationalNumber left, RationalNumber right)
    {
        return left.CompareTo(right) >= 0;
    }
}