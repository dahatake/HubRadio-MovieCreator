using HubRadio.Core.Provenance;

namespace HubRadio.Core.Analysis;

internal static class AnalysisValidation
{
    public static void ValidateRequiredText(
        string value,
        string parameterName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
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

    public static void ValidateConfidence(
        double? value,
        string parameterName,
        string description)
    {
        if (value is double confidence
            && (!double.IsFinite(confidence) || confidence < 0d || confidence > 1d))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{description} must be between zero and one when supplied.");
        }
    }

    public static void ValidateSourceSpanOrigin(OriginRef origin, string parameterName)
    {
        if (origin.Kind != OriginKind.SourceSpan)
        {
            throw new ArgumentException(
                "A machine detection or suggestion must refer to a time-bounded source span.",
                parameterName);
        }
    }

    public static void ValidateErrorCode(string value, string parameterName)
    {
        const int maximumLength = 64;
        if (value.Length == 0 || value.Length > maximumLength)
        {
            throw new ArgumentException(
                "An error code must use canonical lowercase dot-separated segments.",
                parameterName);
        }

        string[] segments = value.Split('.');
        if (segments.Length < 2 || segments.Any(static segment => !IsCanonicalErrorCodeSegment(segment)))
        {
            throw new ArgumentException(
                "An error code must use canonical lowercase dot-separated segments.",
                parameterName);
        }
    }

    private static bool IsCanonicalErrorCodeSegment(string segment)
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