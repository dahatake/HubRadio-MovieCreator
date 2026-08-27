using System.Globalization;
using System.Numerics;

namespace HubRadio.Core.Time;

public enum RationalTimeRoundingMode
{
    TowardZero,
    Floor,
    Ceiling,
    NearestAwayFromZero,
    NearestToEven,
}

public readonly struct RationalTime : IComparable, IComparable<RationalTime>, IEquatable<RationalTime>
{
    private readonly long _encodedRate;

    public RationalTime(long value, long rate)
    {
        if (rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "Rate must be positive.");
        }

        this = Create(value, rate);
    }

    private RationalTime(long value, long rate, CanonicalForm _)
    {
        Value = value;
        _encodedRate = rate - 1;
    }

    public long Value { get; }

    public long Rate => _encodedRate + 1;

    public static RationalTime FromFrames(
        long frameNumber,
        long frameRateNumerator,
        long frameRateDenominator)
    {
        if (frameRateNumerator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateNumerator),
                frameRateNumerator,
                "Frame-rate numerator must be positive.");
        }

        if (frameRateDenominator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateDenominator),
                frameRateDenominator,
                "Frame-rate denominator must be positive.");
        }

        return Create(
            (BigInteger)frameNumber * frameRateDenominator,
            frameRateNumerator);
    }

    public RationalTime Quantize(long targetRate, RationalTimeRoundingMode mode)
    {
        if (targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetRate),
                targetRate,
                "Target rate must be positive.");
        }

        BigInteger scaledNumerator = (BigInteger)Value * targetRate;
        BigInteger quotient = BigInteger.DivRem(scaledNumerator, Rate, out BigInteger remainder);

        BigInteger roundedValue = mode switch
        {
            RationalTimeRoundingMode.TowardZero => quotient,
            RationalTimeRoundingMode.Floor => remainder.Sign < 0
                ? quotient - BigInteger.One
                : quotient,
            RationalTimeRoundingMode.Ceiling => remainder.Sign > 0
                ? quotient + BigInteger.One
                : quotient,
            RationalTimeRoundingMode.NearestAwayFromZero => RoundNearest(
                quotient,
                remainder,
                Rate,
                tiesToEven: false),
            RationalTimeRoundingMode.NearestToEven => RoundNearest(
                quotient,
                remainder,
                Rate,
                tiesToEven: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown rounding mode."),
        };

        return Create(roundedValue, targetRate);
    }

    public double ToDouble()
    {
        return (double)Value / Rate;
    }

    public int CompareTo(RationalTime other)
    {
        BigInteger left = (BigInteger)Value * other.Rate;
        BigInteger right = (BigInteger)other.Value * Rate;
        return left.CompareTo(right);
    }

    int IComparable.CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (obj is not RationalTime other)
        {
            throw new ArgumentException($"Object must be a {nameof(RationalTime)}.", nameof(obj));
        }

        return CompareTo(other);
    }

    public bool Equals(RationalTime other)
    {
        return Value == other.Value && Rate == other.Rate;
    }

    public override bool Equals(object? obj)
    {
        return obj is RationalTime other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Value, Rate);
    }

    public override string ToString()
    {
        return string.Concat(
            Value.ToString(CultureInfo.InvariantCulture),
            "/",
            Rate.ToString(CultureInfo.InvariantCulture));
    }

    public static RationalTime operator +(RationalTime left, RationalTime right)
    {
        return Create(
            ((BigInteger)left.Value * right.Rate) + ((BigInteger)right.Value * left.Rate),
            (BigInteger)left.Rate * right.Rate);
    }

    public static RationalTime operator -(RationalTime left, RationalTime right)
    {
        return Create(
            ((BigInteger)left.Value * right.Rate) - ((BigInteger)right.Value * left.Rate),
            (BigInteger)left.Rate * right.Rate);
    }

    public static RationalTime operator -(RationalTime value)
    {
        return Create(-(BigInteger)value.Value, value.Rate);
    }

    public static bool operator ==(RationalTime left, RationalTime right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(RationalTime left, RationalTime right)
    {
        return !left.Equals(right);
    }

    public static bool operator <(RationalTime left, RationalTime right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(RationalTime left, RationalTime right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(RationalTime left, RationalTime right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(RationalTime left, RationalTime right)
    {
        return left.CompareTo(right) >= 0;
    }

    private static RationalTime Create(BigInteger numerator, BigInteger denominator)
    {
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        BigInteger reducedNumerator = numerator / divisor;
        BigInteger reducedDenominator = denominator / divisor;

        if (reducedNumerator < long.MinValue
            || reducedNumerator > long.MaxValue
            || reducedDenominator > long.MaxValue)
        {
            throw new OverflowException(
                "The reduced rational time cannot be represented by signed 64-bit integers.");
        }

        return new RationalTime(
            (long)reducedNumerator,
            (long)reducedDenominator,
            CanonicalForm.Reduced);
    }

    private static BigInteger RoundNearest(
        BigInteger quotient,
        BigInteger remainder,
        BigInteger denominator,
        bool tiesToEven)
    {
        int halfComparison = (BigInteger.Abs(remainder) * 2).CompareTo(denominator);
        if (halfComparison < 0 || (halfComparison == 0 && tiesToEven && quotient.IsEven))
        {
            return quotient;
        }

        return quotient + remainder.Sign;
    }

    private enum CanonicalForm : byte
    {
        Reduced,
    }
}
