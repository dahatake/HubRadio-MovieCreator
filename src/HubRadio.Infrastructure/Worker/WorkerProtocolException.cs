using HubRadio.Infrastructure.Diagnostics;

namespace HubRadio.Infrastructure.Worker;

public sealed class WorkerProtocolException : Exception
{
    internal WorkerProtocolException(string code)
        : base("The worker protocol operation failed.")
    {
        Code = StableErrorCodes.Validate(code);
    }

    public string Code { get; }
}