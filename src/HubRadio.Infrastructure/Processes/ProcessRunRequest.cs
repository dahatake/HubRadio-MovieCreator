namespace HubRadio.Infrastructure.Processes;

public sealed class ProcessRunRequest
{
    public const int DefaultMaxCapturedBytes = 1024 * 1024;
    public const int MaximumMaxCapturedBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan MaximumTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    public ProcessRunRequest(
        string executable,
        IReadOnlyList<string> argumentList,
        string workingDirectory,
        TimeSpan timeout,
        int maxCapturedBytes = DefaultMaxCapturedBytes)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(argumentList);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        if (string.IsNullOrWhiteSpace(executable) ||
            !string.Equals(executable, executable.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException(
                "The executable must be a nonblank absolute path without surrounding whitespace.",
                nameof(executable));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !string.Equals(workingDirectory, workingDirectory.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(workingDirectory) ||
            !Directory.Exists(workingDirectory))
        {
            throw new ArgumentException(
                "The working directory must be an existing absolute directory without surrounding whitespace.",
                nameof(workingDirectory));
        }

        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
            "The process timeout must be positive and supported by the runtime timer.");
        }

        if (maxCapturedBytes <= 0 || maxCapturedBytes > MaximumMaxCapturedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCapturedBytes),
                $"The capture limit must be between 1 and {MaximumMaxCapturedBytes} bytes per stream.");
        }

        string[] arguments = new string[argumentList.Count];
        for (int index = 0; index < argumentList.Count; index++)
        {
            string? argument = argumentList[index];
            if (argument is null)
            {
                throw new ArgumentException(
                    "The argument list must not contain null elements.",
                    nameof(argumentList));
            }

            arguments[index] = argument;
        }

        Executable = executable;
        ArgumentList = Array.AsReadOnly(arguments);
        WorkingDirectory = workingDirectory;
        Timeout = timeout;
        MaxCapturedBytes = maxCapturedBytes;
    }

    public string Executable { get; }

    public IReadOnlyList<string> ArgumentList { get; }

    public string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }

    public int MaxCapturedBytes { get; }
}