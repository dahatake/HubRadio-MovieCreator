namespace HubRadio.Infrastructure.Diagnostics;

public sealed class DiagnosticLogException : Exception
{
    public const string StableErrorCode = StableErrorCodes.DiagnosticLogWriteFailed;

    internal DiagnosticLogException()
        : base("The local diagnostic log could not be written.")
    {
    }

    public string Code => StableErrorCode;
}