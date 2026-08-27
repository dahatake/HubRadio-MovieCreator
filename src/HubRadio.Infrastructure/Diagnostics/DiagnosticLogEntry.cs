namespace HubRadio.Infrastructure.Diagnostics;

public sealed class DiagnosticLogEntry
{
    public const int MaximumExceptionTypeUtf8Bytes = 192;

    public DiagnosticLogEntry(
        string code,
        DiagnosticComponent component,
        DiagnosticOperation operation,
        DiagnosticSeverity severity = DiagnosticSeverity.Error,
        long? durationMilliseconds = null,
        int? nativeErrorCode = null)
        : this(
            code,
            component,
            operation,
            severity,
            durationMilliseconds,
            nativeErrorCode,
            exceptionType: null,
            exceptionTypeTruncated: false,
            exceptionTypeRedacted: false,
            exceptionContentOmitted: false)
    {
    }

    private DiagnosticLogEntry(
        string code,
        DiagnosticComponent component,
        DiagnosticOperation operation,
        DiagnosticSeverity severity,
        long? durationMilliseconds,
        int? nativeErrorCode,
        string? exceptionType,
        bool exceptionTypeTruncated,
        bool exceptionTypeRedacted,
        bool exceptionContentOmitted)
    {
        Code = StableErrorCodes.Validate(code);

        if (!Enum.IsDefined(component))
        {
            throw new ArgumentOutOfRangeException(
                nameof(component),
                "The diagnostic component must be defined.");
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                "The diagnostic operation must be defined.");
        }

        if (!MatchesStableContract(Code, component, operation))
        {
            throw new ArgumentException(
                "The diagnostic component and operation must match the stable error code.",
                nameof(code));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(severity),
                "The diagnostic severity must be defined.");
        }

        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds),
                "The diagnostic duration must not be negative.");
        }

        Severity = severity;
        DurationMilliseconds = durationMilliseconds;
        NativeErrorCode = nativeErrorCode;
        ExceptionType = exceptionType;
        ExceptionTypeTruncated = exceptionTypeTruncated;
        ExceptionTypeRedacted = exceptionTypeRedacted;
        ExceptionContentOmitted = exceptionContentOmitted;
    }

    public string Code { get; }

    public DiagnosticComponent Component { get; }

    public DiagnosticOperation Operation { get; }

    public DiagnosticSeverity Severity { get; }

    public long? DurationMilliseconds { get; }

    public int? NativeErrorCode { get; }

    public string? ExceptionType { get; }

    public bool ExceptionTypeTruncated { get; }

    public bool ExceptionTypeRedacted { get; }

    public bool ExceptionContentOmitted { get; }

    public static DiagnosticLogEntry FromException(
        string code,
        DiagnosticComponent component,
        DiagnosticOperation operation,
        Exception exception,
        DiagnosticSeverity severity = DiagnosticSeverity.Error,
        long? durationMilliseconds = null,
        int? nativeErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        SanitizedDiagnosticText exceptionType =
            DiagnosticTextSanitizer.SanitizeExceptionType(
                exception.GetType().Name,
                MaximumExceptionTypeUtf8Bytes);

        return new DiagnosticLogEntry(
            code,
            component,
            operation,
            severity,
            durationMilliseconds,
            nativeErrorCode,
            exceptionType.Value,
            exceptionType.WasTruncated,
            exceptionType.WasRedacted,
            exceptionContentOmitted: true);
    }

    internal static bool MatchesStableContract(
        string code,
        DiagnosticComponent component,
        DiagnosticOperation operation) =>
        (string.Equals(code, StableErrorCodes.ProcessStartFailed, StringComparison.Ordinal) &&
            component == DiagnosticComponent.Process &&
            operation == DiagnosticOperation.Start) ||
        (string.Equals(code, StableErrorCodes.DiagnosticLogWriteFailed, StringComparison.Ordinal) &&
            component == DiagnosticComponent.DiagnosticLog &&
            operation == DiagnosticOperation.Write);
}

public enum DiagnosticComponent
{
    Process,
    DiagnosticLog,
}

public enum DiagnosticOperation
{
    Start,
    Write,
}