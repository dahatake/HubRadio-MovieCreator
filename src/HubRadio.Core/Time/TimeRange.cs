namespace HubRadio.Core.Time;

public readonly struct TimeRange : IEquatable<TimeRange>
{
    public TimeRange(RationalTime start, RationalTime duration)
    {
        if (duration < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Duration must not be negative.");
        }

        _ = start + duration;

        Start = start;
        Duration = duration;
    }

    public RationalTime Start { get; }

    public RationalTime Duration { get; }

    public RationalTime End => Start + Duration;

    public bool Contains(RationalTime time)
    {
        return time >= Start && time < End;
    }

    public bool Overlaps(TimeRange other)
    {
        if (Duration == default(RationalTime) || other.Duration == default(RationalTime))
        {
            return false;
        }

        return Start < other.End && other.Start < End;
    }

    public bool IsAdjacentTo(TimeRange other)
    {
        return End == other.Start || other.End == Start;
    }

    public bool Equals(TimeRange other)
    {
        return Start == other.Start && Duration == other.Duration;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimeRange other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Start, Duration);
    }

    public static bool operator ==(TimeRange left, TimeRange right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TimeRange left, TimeRange right)
    {
        return !left.Equals(right);
    }
}
