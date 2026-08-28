namespace HubRadio.Core.Jobs;

internal static class JobValidation
{
    private const int MaximumErrorCodeLength = 128;

    public static void ValidateState(JobState state, string parameterName)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                state,
                "Unknown job state.");
        }
    }

    public static void ValidateRequiredText(
        string value,
        string parameterName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }
    }

    public static void ValidateOptionalText(
        string? value,
        string parameterName,
        string description)
    {
        if (value is not null)
        {
            ValidateRequiredText(value, parameterName, description);
        }
    }

    public static void ValidateErrorCode(string value, string parameterName)
    {
        if (value.Length == 0 ||
            value.Length > MaximumErrorCodeLength)
        {
            throw new ArgumentException(
                "An error code must use canonical lowercase dot-separated segments.",
                parameterName);
        }

        string[] segments = value.Split('.');
        if (segments.Length < 2 || segments.Any(static segment => !IsCanonicalSegment(segment)))
        {
            throw new ArgumentException(
                "An error code must use canonical lowercase dot-separated segments.",
                parameterName);
        }
    }

    private static bool IsCanonicalSegment(string segment)
    {
        if (segment.Length == 0 || segment[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        char previous = segment[0];
        for (int index = 1; index < segment.Length; index++)
        {
            char current = segment[index];
            if (current is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                previous = current;
                continue;
            }

            if (current != '_' || previous == '_')
            {
                return false;
            }

            previous = current;
        }

        return previous != '_';
    }
}