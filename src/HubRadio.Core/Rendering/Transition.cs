using HubRadio.Core.Time;

namespace HubRadio.Core.Rendering;

public enum TransitionKind
{
    Cut,
    Crossfade,
    Fade,
    Wipe,
    Slide,
    TitleCard,
    Zoom,
}

public enum TransitionDirection
{
    Left,
    Right,
    Up,
    Down,
}

public sealed class Transition : IEquatable<Transition>
{
    private Transition(
        TransitionKind kind,
        RationalTime duration,
        TransitionDirection? direction)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transition kind.");
        }

        if (kind == TransitionKind.Cut)
        {
            if (duration != default(RationalTime))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    duration,
                    "A cut duration must be zero.");
            }
        }
        else if (duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "An effect transition duration must be positive.");
        }

        bool requiresDirection = kind is TransitionKind.Wipe or TransitionKind.Slide;
        if (requiresDirection && direction is null)
        {
            throw new ArgumentException(
                "Wipe and slide transitions require a direction.",
                nameof(direction));
        }

        if (!requiresDirection && direction is not null)
        {
            throw new ArgumentException(
                "Only wipe and slide transitions may have a direction.",
                nameof(direction));
        }

        if (direction is not null && !Enum.IsDefined(direction.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unknown transition direction.");
        }

        Kind = kind;
        Duration = duration;
        Direction = direction;
    }

    public TransitionKind Kind { get; }

    public RationalTime Duration { get; }

    public TransitionDirection? Direction { get; }

    public static Transition Cut()
    {
        return new Transition(TransitionKind.Cut, default, null);
    }

    public static Transition Crossfade(RationalTime duration)
    {
        return new Transition(TransitionKind.Crossfade, duration, null);
    }

    public static Transition Fade(RationalTime duration)
    {
        return new Transition(TransitionKind.Fade, duration, null);
    }

    public static Transition Wipe(RationalTime duration, TransitionDirection direction)
    {
        return new Transition(TransitionKind.Wipe, duration, direction);
    }

    public static Transition Slide(RationalTime duration, TransitionDirection direction)
    {
        return new Transition(TransitionKind.Slide, duration, direction);
    }

    public static Transition TitleCard(RationalTime duration)
    {
        return new Transition(TransitionKind.TitleCard, duration, null);
    }

    public static Transition Zoom(RationalTime duration)
    {
        return new Transition(TransitionKind.Zoom, duration, null);
    }

    public bool Equals(Transition? other)
    {
        return other is not null
            && Kind == other.Kind
            && Duration == other.Duration
            && Direction == other.Direction;
    }

    public override bool Equals(object? obj)
    {
        return obj is Transition other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Kind, Duration, Direction);
    }
}