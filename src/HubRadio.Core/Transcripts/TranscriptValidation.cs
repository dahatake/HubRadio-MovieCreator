namespace HubRadio.Core.Transcripts;

internal static class TranscriptValidation
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
}