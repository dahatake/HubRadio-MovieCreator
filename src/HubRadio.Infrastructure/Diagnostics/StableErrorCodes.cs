using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Diagnostics;

public static class StableErrorCodes
{
    public const int MaximumCodeLength = 64;

    public const string DiagnosticLogWriteFailed = "diagnostic.log_write_failed";
    public const string ProcessStartFailed = ProcessStartException.StableErrorCode;
    public const string WebViewInitializationFailed = "webview.initialization_failed";
    public const string WebViewRuntimeUnavailable = "webview.runtime_unavailable";
    public const string WorkerInvalidMessage = "worker.invalid_message";
    public const string WorkerMessageTooLarge = "worker.message_too_large";
    public const string WorkerProcessingFailed = "worker.processing_failed";
    public const string WorkerUnknownMethod = "worker.unknown_method";

    private static readonly IReadOnlyList<string> Codes = Array.AsReadOnly(
    [
        DiagnosticLogWriteFailed,
        ProcessStartFailed,
        WebViewInitializationFailed,
        WebViewRuntimeUnavailable,
        WorkerInvalidMessage,
        WorkerMessageTooLarge,
        WorkerProcessingFailed,
        WorkerUnknownMethod,
    ]);

    public static IReadOnlyList<string> DefinedCodes => Codes;

    public static bool IsDefined(string? code) =>
        string.Equals(code, DiagnosticLogWriteFailed, StringComparison.Ordinal) ||
        string.Equals(code, ProcessStartFailed, StringComparison.Ordinal) ||
        string.Equals(code, WebViewInitializationFailed, StringComparison.Ordinal) ||
        string.Equals(code, WebViewRuntimeUnavailable, StringComparison.Ordinal) ||
        string.Equals(code, WorkerInvalidMessage, StringComparison.Ordinal) ||
        string.Equals(code, WorkerMessageTooLarge, StringComparison.Ordinal) ||
        string.Equals(code, WorkerProcessingFailed, StringComparison.Ordinal) ||
        string.Equals(code, WorkerUnknownMethod, StringComparison.Ordinal);

    public static bool IsCanonical(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaximumCodeLength)
        {
            return false;
        }

        string[] segments = code.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (segment.Length == 0 || !IsLowerAsciiLetter(segment[0]))
            {
                return false;
            }

            char previous = segment[0];
            for (int index = 1; index < segment.Length; index++)
            {
                char current = segment[index];
                if (IsLowerAsciiLetter(current) || IsAsciiDigit(current))
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

            if (previous == '_')
            {
                return false;
            }
        }

        return true;
    }

    public static string Validate(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!IsCanonical(code))
        {
            throw new ArgumentException(
                "The error code must use canonical lowercase dot-separated segments.",
                nameof(code));
        }

        if (!IsDefined(code))
        {
            throw new ArgumentException(
                "The error code is not defined in the stable error catalog.",
                nameof(code));
        }

        return code;
    }

    private static bool IsLowerAsciiLetter(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}