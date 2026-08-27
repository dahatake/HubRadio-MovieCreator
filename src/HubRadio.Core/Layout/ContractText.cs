namespace HubRadio.Core.Layout;

internal static class ContractText
{
    public static string RequireNonBlank(
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
}