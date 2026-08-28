using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Compositions;

public sealed class CompositionElement : IEquatable<CompositionElement>
{
    public CompositionElement(
        EntityId<CompositionElementIdTag> id,
        TimeRange outputRange,
        OriginRef origin,
        SuggestionReference? adoptedSuggestion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origin);

        if (outputRange.Start < default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputRange),
                outputRange,
                "A composition element must not start before output time zero.");
        }

        if (outputRange.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputRange),
                outputRange,
                "A composition element must have a positive output duration.");
        }

        if (adoptedSuggestion is not null && adoptedSuggestion.Origin != origin)
        {
            throw new ArgumentException(
                "An adopted suggestion reference must retain the composition element's OriginRef.",
                nameof(adoptedSuggestion));
        }

        Id = id;
        OutputRange = outputRange;
        Origin = origin;
        AdoptedSuggestion = adoptedSuggestion;
    }

    public EntityId<CompositionElementIdTag> Id { get; }

    public TimeRange OutputRange { get; }

    public OriginRef Origin { get; }

    public SuggestionReference? AdoptedSuggestion { get; }

    public bool Equals(CompositionElement? other)
    {
        return other is not null
            && Id == other.Id
            && OutputRange == other.OutputRange
            && Origin == other.Origin
            && SuggestionReferencesEqual(AdoptedSuggestion, other.AdoptedSuggestion);
    }

    public override bool Equals(object? obj)
    {
        return obj is CompositionElement other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Id,
            OutputRange,
            Origin,
            AdoptedSuggestion?.SuggestionId);
    }

    private static bool SuggestionReferencesEqual(
        SuggestionReference? left,
        SuggestionReference? right)
    {
        return ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && left.SuggestionId == right.SuggestionId
                && left.Origin == right.Origin);
    }
}