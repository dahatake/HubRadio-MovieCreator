using System.Text;
using System.Text.Json;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Worker;

public sealed class WorkerClient
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ProcessRunner processRunner;
    private readonly string pythonExecutable;
    private readonly string workerWorkingDirectory;

    public WorkerClient(
        ProcessRunner processRunner,
        string pythonExecutable,
        string workerWorkingDirectory)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(pythonExecutable);
        ArgumentNullException.ThrowIfNull(workerWorkingDirectory);

        if (string.IsNullOrWhiteSpace(pythonExecutable) ||
            !string.Equals(pythonExecutable, pythonExecutable.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(pythonExecutable))
        {
            throw new ArgumentException(
                "The Python executable must be a nonblank absolute path without surrounding whitespace.",
                nameof(pythonExecutable));
        }

        if (string.IsNullOrWhiteSpace(workerWorkingDirectory) ||
            !string.Equals(
                workerWorkingDirectory,
                workerWorkingDirectory.Trim(),
                StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(workerWorkingDirectory) ||
            !Directory.Exists(workerWorkingDirectory))
        {
            throw new ArgumentException(
                "The worker working directory must be an existing absolute directory without surrounding whitespace.",
                nameof(workerWorkingDirectory));
        }

        this.processRunner = processRunner;
        this.pythonExecutable = pythonExecutable;
        this.workerWorkingDirectory = workerWorkingDirectory;
    }

    public async Task<WorkerPingResult> PingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        string requestId = Guid.NewGuid().ToString("D");
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = WorkerProtocolParser.ProtocolVersion,
                type = "request",
                requestId,
                method = WorkerProtocolParser.PingMethod,
                @params = new { },
            });
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(serialized);
        string standardInput = string.Concat(StrictUtf8.GetString(canonical), "\n");
        ProcessRunRequest request = new(
            pythonExecutable,
            ["-I", "-B", "-m", "hubradio_worker", "--protocol"],
            workerWorkingDirectory,
            timeout,
            maxCapturedBytes: WorkerProtocolParser.MaxProtocolOutputUtf8Bytes,
            standardInput: standardInput);

        ProcessRunResult processResult = await processRunner.RunAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        return WorkerProtocolParser.ParsePingResult(processResult, requestId);
    }
}