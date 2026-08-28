using HubRadio.Core.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.Infrastructure.Rendering;

public enum TransitionRenderOperation
{
    DirectCut,
    Crossfade,
    Fade,
    Wipe,
    Slide,
    TitleCard,
    Zoom,
}

public sealed class TransitionRenderInstruction
{
    internal TransitionRenderInstruction(
        TransitionRenderOperation operation,
        RationalTime duration,
        TransitionDirection? direction)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unknown transition render operation.");
        }

        if (operation == TransitionRenderOperation.DirectCut)
        {
            if (duration != default(RationalTime))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    duration,
                    "A direct-cut duration must be zero.");
            }
        }
        else if (duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "An effect render duration must be positive.");
        }

        bool requiresDirection = operation is
            TransitionRenderOperation.Wipe or TransitionRenderOperation.Slide;
        if (requiresDirection && direction is null)
        {
            throw new ArgumentException(
                "Wipe and slide render operations require a direction.",
                nameof(direction));
        }

        if (!requiresDirection && direction is not null)
        {
            throw new ArgumentException(
                "Only wipe and slide render operations may have a direction.",
                nameof(direction));
        }

        if (direction is not null && !Enum.IsDefined(direction.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unknown transition direction.");
        }

        Operation = operation;
        Duration = duration;
        Direction = direction;
    }

    public TransitionRenderOperation Operation { get; }

    public RationalTime Duration { get; }

    public TransitionDirection? Direction { get; }
}

public sealed class TransitionEmitter
{
    public TransitionRenderInstruction Emit(Transition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        TransitionRenderOperation operation = transition.Kind switch
        {
            TransitionKind.Cut => TransitionRenderOperation.DirectCut,
            TransitionKind.Crossfade => TransitionRenderOperation.Crossfade,
            TransitionKind.Fade => TransitionRenderOperation.Fade,
            TransitionKind.Wipe => TransitionRenderOperation.Wipe,
            TransitionKind.Slide => TransitionRenderOperation.Slide,
            TransitionKind.TitleCard => TransitionRenderOperation.TitleCard,
            TransitionKind.Zoom => TransitionRenderOperation.Zoom,
            _ => throw new ArgumentOutOfRangeException(
                nameof(transition),
                transition.Kind,
                "Unknown transition kind."),
        };

        return new TransitionRenderInstruction(
            operation,
            transition.Duration,
            transition.Direction);
    }
}