using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Time;

namespace HubRadio.Core.Provenance;

public sealed class TimelineMapIdTag
{
}

public readonly struct TimelineMapPoint : IEquatable<TimelineMapPoint>
{
    public TimelineMapPoint(OriginRef origin, RationalTime? sourceTime)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (origin.Kind == OriginKind.SourceSpan && sourceTime is null)
        {
            throw new ArgumentNullException(nameof(sourceTime));
        }

        if (origin.Kind != OriginKind.SourceSpan && sourceTime is not null)
        {
            throw new ArgumentException(
                "Only a SourceSpan origin can map to a source time.",
                nameof(sourceTime));
        }

        Origin = origin;
        SourceTime = sourceTime;
    }

    public OriginRef Origin { get; }

    public RationalTime? SourceTime { get; }

    public bool Equals(TimelineMapPoint other)
    {
        return Origin == other.Origin && SourceTime == other.SourceTime;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimelineMapPoint other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Origin, SourceTime);
    }

    public static bool operator ==(TimelineMapPoint left, TimelineMapPoint right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TimelineMapPoint left, TimelineMapPoint right)
    {
        return !left.Equals(right);
    }
}

public sealed class TimelineMapEntry : IEquatable<TimelineMapEntry>
{
    public TimelineMapEntry(
        TimeRange outputRange,
        OriginRef origin,
        TimeRange? sourceRange)
    {
        ArgumentNullException.ThrowIfNull(origin);

        if (outputRange.Start < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputRange),
                outputRange,
                "A timeline-map output range must not start before zero.");
        }

        if (outputRange.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputRange),
                outputRange,
                "A timeline-map output range must have a positive duration.");
        }

        if (origin.Kind == OriginKind.SourceSpan)
        {
            if (sourceRange is null)
            {
                throw new ArgumentNullException(nameof(sourceRange));
            }

            if (sourceRange.Value.Duration != outputRange.Duration)
            {
                throw new ArgumentException(
                    "A one-to-one SourceSpan mapping requires equal source and output durations.",
                    nameof(sourceRange));
            }
        }
        else if (sourceRange is not null)
        {
            throw new ArgumentException(
                "Asset and EditorialRecord origins do not have source time ranges.",
                nameof(sourceRange));
        }

        OutputRange = outputRange;
        Origin = origin;
        SourceRange = sourceRange;
    }

    public TimeRange OutputRange { get; }

    public OriginRef Origin { get; }

    public TimeRange? SourceRange { get; }

    public bool Equals(TimelineMapEntry? other)
    {
        return other is not null
            && OutputRange == other.OutputRange
            && Origin == other.Origin
            && SourceRange == other.SourceRange;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimelineMapEntry other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(OutputRange, Origin, SourceRange);
    }
}

public sealed class TimelineMap
{
    public TimelineMap(
        EntityId<TimelineMapIdTag> id,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        IReadOnlyList<TimelineMapEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(entries);

        TimelineMapEntry[] copiedEntries = new TimelineMapEntry[entries.Count];
        RationalTime? previousEnd = null;

        for (int index = 0; index < copiedEntries.Length; index++)
        {
            TimelineMapEntry? entry = entries[index];
            if (entry is null)
            {
                throw new ArgumentException(
                    "Timeline-map entries must not contain null elements.",
                    nameof(entries));
            }

            if (previousEnd is not null && entry.OutputRange.Start < previousEnd.Value)
            {
                throw new ArgumentException(
                    "Timeline-map entries must be ordered by output time and must not overlap.",
                    nameof(entries));
            }

            copiedEntries[index] = entry;
            previousEnd = entry.OutputRange.End;
        }

        Id = id;
        CompositionVersionId = compositionVersionId;
        Entries = Array.AsReadOnly(copiedEntries);
    }

    public EntityId<TimelineMapIdTag> Id { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public IReadOnlyList<TimelineMapEntry> Entries { get; }

    public bool TryMapOutputTime(RationalTime outputTime, out TimelineMapPoint point)
    {
        int low = 0;
        int high = Entries.Count - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            TimelineMapEntry entry = Entries[middle];

            if (outputTime < entry.OutputRange.Start)
            {
                high = middle - 1;
            }
            else if (outputTime >= entry.OutputRange.End)
            {
                low = middle + 1;
            }
            else
            {
                RationalTime? sourceTime = entry.SourceRange is null
                    ? null
                    : entry.SourceRange.Value.Start + (outputTime - entry.OutputRange.Start);
                point = new TimelineMapPoint(entry.Origin, sourceTime);
                return true;
            }
        }

        point = default;
        return false;
    }

    public IReadOnlyList<RationalTime> MapSourceTimeToOutput(
        EntityId<SourceSpanIdTag> sourceSpanId,
        RationalTime sourceTime)
    {
        ArgumentNullException.ThrowIfNull(sourceSpanId);

        List<RationalTime> outputTimes = [];
        foreach (TimelineMapEntry entry in Entries)
        {
            if (entry.Origin.Kind != OriginKind.SourceSpan
                || entry.Origin.SourceSpanId != sourceSpanId
                || entry.SourceRange is null
                || !entry.SourceRange.Value.Contains(sourceTime))
            {
                continue;
            }

            outputTimes.Add(
                entry.OutputRange.Start + (sourceTime - entry.SourceRange.Value.Start));
        }

        return outputTimes.AsReadOnly();
    }

    public IReadOnlyList<TimeRange> MapOriginToOutputRanges(OriginRef origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        List<TimeRange> outputRanges = [];
        foreach (TimelineMapEntry entry in Entries)
        {
            if (entry.Origin == origin)
            {
                outputRanges.Add(entry.OutputRange);
            }
        }

        return outputRanges.AsReadOnly();
    }
}