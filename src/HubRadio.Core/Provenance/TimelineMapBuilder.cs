using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Time;

namespace HubRadio.Core.Provenance;

public static class TimelineMapBuilder
{
    public static TimelineMap Build(
        EntityId<TimelineMapIdTag> id,
        CompositionVersion version)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(version);

        TimelineMapEntry[] entries = new TimelineMapEntry[version.Elements.Count];
        RationalTime expectedStart = default;

        for (int index = 0; index < version.Elements.Count; index++)
        {
            CompositionElement element = version.Elements[index];
            if (element.OutputRange.Start != expectedStart)
            {
                throw new ArgumentException(
                    "TimelineMapBuilder requires composition elements ordered without gaps or overlaps and contiguous from output time zero.",
                    nameof(version));
            }

            entries[index] = new TimelineMapEntry(
                element.OutputRange,
                element.Origin,
                element.SourceRange);
            expectedStart = element.OutputRange.End;
        }

        return new TimelineMap(id, version.Id, entries);
    }
}