using HubRadio.Core.Identity;
using HubRadio.Core.Time;

namespace HubRadio.Core.Compositions;

public enum EditOperationKind
{
    Keep,
    Delete,
    Split,
    Move,
}

public sealed class EditOperation
{
    private EditOperation(
        EditOperationKind kind,
        EntityId<CompositionElementIdTag> elementId,
        RationalTime? splitOffset,
        EntityId<CompositionElementIdTag>? rightElementId,
        int? targetIndex)
    {
        Kind = kind;
        ElementId = elementId;
        SplitOffset = splitOffset;
        RightElementId = rightElementId;
        TargetIndex = targetIndex;
    }

    public EditOperationKind Kind { get; }

    public EntityId<CompositionElementIdTag> ElementId { get; }

    public RationalTime? SplitOffset { get; }

    public EntityId<CompositionElementIdTag>? RightElementId { get; }

    public int? TargetIndex { get; }

    public static EditOperation Keep(EntityId<CompositionElementIdTag> elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        return new EditOperation(EditOperationKind.Keep, elementId, null, null, null);
    }

    public static EditOperation Delete(EntityId<CompositionElementIdTag> elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        return new EditOperation(EditOperationKind.Delete, elementId, null, null, null);
    }

    public static EditOperation Split(
        EntityId<CompositionElementIdTag> elementId,
        RationalTime splitOffset,
        EntityId<CompositionElementIdTag> rightElementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        ArgumentNullException.ThrowIfNull(rightElementId);

        if (splitOffset <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(splitOffset),
                splitOffset,
                "A split offset must be positive.");
        }

        if (rightElementId == elementId)
        {
            throw new ArgumentException(
                "The right split element must have a different ID from the target element.",
                nameof(rightElementId));
        }

        return new EditOperation(
            EditOperationKind.Split,
            elementId,
            splitOffset,
            rightElementId,
            null);
    }

    public static EditOperation Move(
        EntityId<CompositionElementIdTag> elementId,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(elementId);

        if (targetIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetIndex),
                targetIndex,
                "A move target index must not be negative.");
        }

        return new EditOperation(EditOperationKind.Move, elementId, null, null, targetIndex);
    }
}