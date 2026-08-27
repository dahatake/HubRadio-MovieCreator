namespace HubRadio.Infrastructure.Processes;

public sealed class ProcessStartException : Exception
{
    public const string StableErrorCode = "process.start_failed";

    internal ProcessStartException(int? nativeErrorCode)
        : base("The child process could not be started.")
    {
        NativeErrorCode = nativeErrorCode;
    }

    public string Code => StableErrorCode;

    public int? NativeErrorCode { get; }
}