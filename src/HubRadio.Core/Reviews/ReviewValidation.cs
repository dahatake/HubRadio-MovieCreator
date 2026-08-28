namespace HubRadio.Core.Reviews;

internal static class ReviewValidation
{
    internal static void ValidateDefined<TEnum>(
        TEnum value,
        string parameterName,
        string description)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Unknown {description}.");
        }
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