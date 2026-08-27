namespace HubRadio.Infrastructure.Processes;

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Truncated,
    TimeSpan Duration);