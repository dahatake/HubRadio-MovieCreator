namespace HubRadio.Infrastructure.Worker;

public sealed class WorkerPingResult
{
    internal WorkerPingResult(
        string requestId,
        IReadOnlyList<WorkerProgressSnapshot> progress,
        int protocolVersion)
    {
        RequestId = requestId;
        Progress = Array.AsReadOnly(progress.ToArray());
        ProtocolVersion = protocolVersion;
    }

    public string RequestId { get; }

    public IReadOnlyList<WorkerProgressSnapshot> Progress { get; }

    public int ProtocolVersion { get; }
}

public sealed record WorkerProgressSnapshot(
    int Sequence,
    int CompletedUnits,
    int TotalUnits,
    string Phase);