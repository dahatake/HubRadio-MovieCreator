namespace HubRadio.Core.Templates;

public sealed class TemplateParameterDefinition : IEquatable<TemplateParameterDefinition>
{
    public TemplateParameterDefinition(
        string name,
        TemplateParameterValue defaultValue,
        bool editorOverrideAllowed,
        IReadOnlyList<TemplateParameterValue>? editorValueAllowlist = null)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);

        if (!editorOverrideAllowed && editorValueAllowlist is not null)
        {
            throw new ArgumentException(
                "A fixed template parameter cannot define an editor value allowlist.",
                nameof(editorValueAllowlist));
        }

        if (editorValueAllowlist is { Count: 0 })
        {
            throw new ArgumentException(
                "An editor value allowlist must contain at least one value when supplied.",
                nameof(editorValueAllowlist));
        }

        Name = TemplateContract.RequireParameterName(name, nameof(name));
        DefaultValue = defaultValue;
        EditorOverrideAllowed = editorOverrideAllowed;
        EditorValueAllowlist = editorValueAllowlist is null
            ? null
            : Array.AsReadOnly(
                CopyAllowedValues(
                    defaultValue.Kind,
                    editorValueAllowlist,
                    nameof(editorValueAllowlist)));
    }

    public string Name { get; }

    public TemplateParameterValue DefaultValue { get; }

    public bool EditorOverrideAllowed { get; }

    public IReadOnlyList<TemplateParameterValue>? EditorValueAllowlist { get; }

    internal bool AllowsEditorValue(TemplateParameterValue value)
    {
        return EditorOverrideAllowed
            && value.Kind == DefaultValue.Kind
            && (EditorValueAllowlist is null || EditorValueAllowlist.Contains(value));
    }

    public bool Equals(TemplateParameterDefinition? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && DefaultValue == other.DefaultValue
            && EditorOverrideAllowed == other.EditorOverrideAllowed
            && SequenceEqual(EditorValueAllowlist, other.EditorValueAllowlist);
    }

    public override bool Equals(object? obj)
    {
        return obj is TemplateParameterDefinition other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(DefaultValue);
        hash.Add(EditorOverrideAllowed);
        if (EditorValueAllowlist is not null)
        {
            foreach (TemplateParameterValue allowedValue in EditorValueAllowlist)
            {
                hash.Add(allowedValue);
            }
        }

        return hash.ToHashCode();
    }

    private static bool SequenceEqual(
        IReadOnlyList<TemplateParameterValue>? left,
        IReadOnlyList<TemplateParameterValue>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.SequenceEqual(right);
    }

    private static TemplateParameterValue[] CopyAllowedValues(
        TemplateParameterValueKind expectedKind,
        IReadOnlyList<TemplateParameterValue> values,
        string parameterName)
    {
        TemplateParameterValue[] copied = new TemplateParameterValue[values.Count];
        HashSet<TemplateParameterValue> uniqueValues = [];

        for (int index = 0; index < copied.Length; index++)
        {
            TemplateParameterValue? value = values[index];
            if (value is null)
            {
                throw new ArgumentException(
                    "Allowed editor values must not contain null elements.",
                    parameterName);
            }

            if (value.Kind != expectedKind)
            {
                throw new ArgumentException(
                    "Allowed editor values must have the same kind as the default value.",
                    parameterName);
            }

            if (!uniqueValues.Add(value))
            {
                throw new ArgumentException(
                    "Allowed editor values must not contain duplicates.",
                    parameterName);
            }

            copied[index] = value;
        }

        return copied;
    }
}

public sealed class TemplateParameterAssignment : IEquatable<TemplateParameterAssignment>
{
    public TemplateParameterAssignment(string name, TemplateParameterValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Name = TemplateContract.RequireParameterName(name, nameof(name));
        Value = value;
    }

    public string Name { get; }

    public TemplateParameterValue Value { get; }

    public bool Equals(TemplateParameterAssignment? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is TemplateParameterAssignment other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), Value);
    }
}

internal static class TemplateContract
{
    public static string RequireParameterName(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!IsCanonicalParameterName(value))
        {
            throw new ArgumentException(
                "A template parameter name must use lowercase ASCII kebab-case.",
                parameterName);
        }

        return value;
    }

    public static string RequireNonBlankText(
        string? value,
        string parameterName,
        string description)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }

    private static bool IsCanonicalParameterName(string value)
    {
        if (value.Length == 0 || value[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        bool previousWasHyphen = false;
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            bool isLowercaseLetter = character is >= 'a' and <= 'z';
            bool isDigit = character is >= '0' and <= '9';
            bool isHyphen = character == '-';
            if ((!isLowercaseLetter && !isDigit && !isHyphen)
                || (isHyphen && previousWasHyphen))
            {
                return false;
            }

            previousWasHyphen = isHyphen;
        }

        return !previousWasHyphen;
    }
}