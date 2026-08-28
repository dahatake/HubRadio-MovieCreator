using HubRadio.Core.Identity;
using HubRadio.Core.Time;

namespace HubRadio.Core.Compositions;

public static class CompositionEditor
{
    public static void Apply(
        WorkingCopy workingCopy,
        EditOperation operation,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        ArgumentNullException.ThrowIfNull(operation);

        CompositionElement[] current = workingCopy.Elements.ToArray();
        ValidateCanonicalSequence(current);

        int elementIndex = FindTargetIndex(current, operation);
        List<CompositionElement> edited = [.. current];

        switch (operation.Kind)
        {
            case EditOperationKind.Keep:
                break;
            case EditOperationKind.Delete:
                edited.RemoveAt(elementIndex);
                break;
            case EditOperationKind.Split:
                SplitElement(edited, elementIndex, operation);
                break;
            case EditOperationKind.Move:
                MoveElement(edited, elementIndex, operation);
                break;
            default:
                throw new InvalidOperationException("The edit operation kind is invalid.");
        }

        CompositionElement[] reflowed = Reflow(edited);
        workingCopy.ReplaceElements(reflowed, updatedAt);
    }

    private static void ValidateCanonicalSequence(IReadOnlyList<CompositionElement> elements)
    {
        RationalTime expectedStart = default;

        foreach (CompositionElement element in elements)
        {
            if (element.OutputRange.Start != expectedStart)
            {
                throw new ArgumentException(
                    "CompositionEditor requires elements ordered without gaps or overlaps and contiguous from output time zero.",
                    "workingCopy");
            }

            expectedStart = element.OutputRange.End;
        }
    }

    private static int FindTargetIndex(
        IReadOnlyList<CompositionElement> elements,
        EditOperation operation)
    {
        int matchIndex = -1;
        int matchCount = 0;

        for (int index = 0; index < elements.Count; index++)
        {
            if (elements[index].Id != operation.ElementId)
            {
                continue;
            }

            matchIndex = index;
            matchCount++;
        }

        if (matchCount != 1)
        {
            throw new ArgumentException(
                "An edit operation target must occur exactly once in the working copy.",
                nameof(operation));
        }

        return matchIndex;
    }

    private static void SplitElement(
        List<CompositionElement> elements,
        int elementIndex,
        EditOperation operation)
    {
        if (operation.SplitOffset is not RationalTime splitOffset
            || operation.RightElementId is not EntityId<CompositionElementIdTag> rightElementId)
        {
            throw new InvalidOperationException("The split operation is incomplete.");
        }

        CompositionElement target = elements[elementIndex];
        if (splitOffset >= target.OutputRange.Duration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "A split offset must be strictly inside the target element duration.");
        }

        for (int index = 0; index < elements.Count; index++)
        {
            if (index != elementIndex && elements[index].Id == rightElementId)
            {
                throw new ArgumentException(
                    "The right split element ID already exists in the working copy.",
                    nameof(operation));
            }
        }

        RationalTime rightDuration = target.OutputRange.Duration - splitOffset;
        TimeRange leftOutputRange = new(target.OutputRange.Start, splitOffset);
        TimeRange rightOutputRange = new(
            target.OutputRange.Start + splitOffset,
            rightDuration);

        TimeRange? leftSourceRange = null;
        TimeRange? rightSourceRange = null;
        if (target.SourceRange is TimeRange sourceRange)
        {
            leftSourceRange = new TimeRange(sourceRange.Start, splitOffset);
            rightSourceRange = new TimeRange(
                sourceRange.Start + splitOffset,
                rightDuration);
        }

        CompositionElement left = new(
            target.Id,
            leftOutputRange,
            target.Origin,
            target.AdoptedSuggestion,
            leftSourceRange);
        CompositionElement right = new(
            rightElementId,
            rightOutputRange,
            target.Origin,
            target.AdoptedSuggestion,
            rightSourceRange);

        elements[elementIndex] = left;
        elements.Insert(elementIndex + 1, right);
    }

    private static void MoveElement(
        List<CompositionElement> elements,
        int elementIndex,
        EditOperation operation)
    {
        if (operation.TargetIndex is not int targetIndex)
        {
            throw new InvalidOperationException("The move operation is incomplete.");
        }

        int remainingCount = elements.Count - 1;
        if (targetIndex > remainingCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "A move target index must be between zero and the remaining element count.");
        }

        CompositionElement target = elements[elementIndex];
        elements.RemoveAt(elementIndex);
        elements.Insert(targetIndex, target);
    }

    private static CompositionElement[] Reflow(IReadOnlyList<CompositionElement> elements)
    {
        CompositionElement[] reflowed = new CompositionElement[elements.Count];
        RationalTime outputStart = default;

        for (int index = 0; index < elements.Count; index++)
        {
            CompositionElement element = elements[index];
            TimeRange outputRange = new(outputStart, element.OutputRange.Duration);
            reflowed[index] = new CompositionElement(
                element.Id,
                outputRange,
                element.Origin,
                element.AdoptedSuggestion,
                element.SourceRange);
            outputStart = outputRange.End;
        }

        return reflowed;
    }
}