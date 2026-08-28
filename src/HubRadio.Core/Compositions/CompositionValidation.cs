using HubRadio.Core.Identity;

namespace HubRadio.Core.Compositions;

internal static class CompositionValidation
{
    internal static CompositionElement[] CopyElements(
        IReadOnlyList<CompositionElement> elements,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(elements, parameterName);

        CompositionElement[] copied = new CompositionElement[elements.Count];
        HashSet<EntityId<CompositionElementIdTag>> ids = [];

        for (int index = 0; index < copied.Length; index++)
        {
            CompositionElement? element = elements[index];
            if (element is null)
            {
                throw new ArgumentException(
                    "Composition elements must not contain null elements.",
                    parameterName);
            }

            if (!ids.Add(element.Id))
            {
                throw new ArgumentException(
                    "Composition elements must not contain duplicate IDs.",
                    parameterName);
            }

            copied[index] = element;
        }

        return copied;
    }

    internal static string RequireNonBlank(
        string value,
        string parameterName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }
}