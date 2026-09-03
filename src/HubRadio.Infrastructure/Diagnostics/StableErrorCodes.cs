using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Diagnostics;

public static class StableErrorCodes
{
    public const int MaximumCodeLength = 64;

    public const string DiagnosticLogWriteFailed = "diagnostic.log_write_failed";
    public const string ProcessStartFailed = ProcessStartException.StableErrorCode;
    public const string ProjectAlreadyFinalized = "project.already_finalized";
    public const string ProjectCatalogUnavailable = "project.catalog_unavailable";
    public const string ProjectIncomplete = "project.incomplete";
    public const string ProjectInvalidRequest = "project.invalid_request";
    public const string ProjectNotAuthorized = "project.not_authorized";
    public const string ProjectNotFound = "project.not_found";
    public const string ProjectOperationFailed = "project.operation_failed";
    public const string ProjectRevisionConflict = "project.revision_conflict";
    public const string ProjectSettingsMismatch = "project.settings_mismatch";
    public const string ProjectSettingsUnavailable = "project.settings_unavailable";
    public const string ProjectSourceSelectionCanceled =
        "project.source_selection_canceled";
    public const string ProjectSourceReselectionRequired =
        "project.source_reselection_required";
    public const string ProjectUnsafeSourceName = "project.unsafe_source_name";
    public const string ReviewConflict = "review.conflict";
    public const string ReviewInvalidRequest = "review.invalid_request";
    public const string ReviewNotAuthorized = "review.not_authorized";
    public const string ReviewNotFound = "review.not_found";
    public const string ReviewOperationFailed = "review.operation_failed";
    public const string ReviewStaleVersion = "review.stale_version";
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
        ProjectAlreadyFinalized,
        ProjectCatalogUnavailable,
        ProjectIncomplete,
        ProjectInvalidRequest,
        ProjectNotAuthorized,
        ProjectNotFound,
        ProjectOperationFailed,
        ProjectRevisionConflict,
        ProjectSettingsMismatch,
        ProjectSettingsUnavailable,
        ProjectSourceSelectionCanceled,
        ProjectSourceReselectionRequired,
        ProjectUnsafeSourceName,
        ReviewConflict,
        ReviewInvalidRequest,
        ReviewNotAuthorized,
        ReviewNotFound,
        ReviewOperationFailed,
        ReviewStaleVersion,
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
        string.Equals(code, ProjectAlreadyFinalized, StringComparison.Ordinal) ||
        string.Equals(code, ProjectCatalogUnavailable, StringComparison.Ordinal) ||
        string.Equals(code, ProjectIncomplete, StringComparison.Ordinal) ||
        string.Equals(code, ProjectInvalidRequest, StringComparison.Ordinal) ||
        string.Equals(code, ProjectNotAuthorized, StringComparison.Ordinal) ||
        string.Equals(code, ProjectNotFound, StringComparison.Ordinal) ||
        string.Equals(code, ProjectOperationFailed, StringComparison.Ordinal) ||
        string.Equals(code, ProjectRevisionConflict, StringComparison.Ordinal) ||
        string.Equals(code, ProjectSettingsMismatch, StringComparison.Ordinal) ||
        string.Equals(code, ProjectSettingsUnavailable, StringComparison.Ordinal) ||
        string.Equals(code, ProjectSourceSelectionCanceled, StringComparison.Ordinal) ||
        string.Equals(
            code,
            ProjectSourceReselectionRequired,
            StringComparison.Ordinal) ||
        string.Equals(code, ProjectUnsafeSourceName, StringComparison.Ordinal) ||
        string.Equals(code, ReviewConflict, StringComparison.Ordinal) ||
        string.Equals(code, ReviewInvalidRequest, StringComparison.Ordinal) ||
        string.Equals(code, ReviewNotAuthorized, StringComparison.Ordinal) ||
        string.Equals(code, ReviewNotFound, StringComparison.Ordinal) ||
        string.Equals(code, ReviewOperationFailed, StringComparison.Ordinal) ||
        string.Equals(code, ReviewStaleVersion, StringComparison.Ordinal) ||
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