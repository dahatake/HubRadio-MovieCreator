using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HubRadio.Infrastructure.Processes;

public sealed class ProcessRunner
{
    private const int StreamBufferSize = 8192;

    private static readonly TimeSpan CleanupWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly Encoding OutputEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource timeoutCancellation = new(request.Timeout);
        using CancellationTokenSource waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
        using Process process = new()
        {
            StartInfo = CreateStartInfo(request),
        };
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            Start(process);
        }
        catch
        {
            stopwatch.Stop();
            throw;
        }

        using CancellationTokenSource captureCancellation = new();
        Stream standardOutput;
        Stream standardError;
        try
        {
            standardOutput = process.StandardOutput.BaseStream;
            standardError = process.StandardError.BaseStream;
        }
        catch
        {
            TryKillProcessTree(process);
            await TryWaitForExitAsync(process).ConfigureAwait(false);
            throw;
        }

        Task<CapturedOutput> standardOutputTask = CaptureAsync(
            standardOutput,
            request.MaxCapturedBytes,
            captureCancellation.Token);
        Task<CapturedOutput> standardErrorTask = CaptureAsync(
            standardError,
            request.MaxCapturedBytes,
            captureCancellation.Token);

        try
        {
            CapturedOutput[] captured;
            try
            {
                await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
                captured = await WaitForCapturedOutputAsync(
                    standardOutputTask,
                    standardErrorTask,
                    waitCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested || timeoutCancellation.IsCancellationRequested)
            {
                await CleanupAsync(
                    process,
                    captureCancellation,
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "The process run was cancelled by the caller.",
                        exception,
                        cancellationToken);
                }

                throw new TimeoutException(
                    "The process exceeded the configured timeout.",
                    exception);
            }

            stopwatch.Stop();

            return new ProcessRunResult(
                process.ExitCode,
                captured[0].Text,
                captured[1].Text,
                captured[0].Truncated || captured[1].Truncated,
                stopwatch.Elapsed);
        }
        catch
        {
            if (NeedsCleanup(process, standardOutputTask, standardErrorTask))
            {
                await CleanupAsync(
                    process,
                    captureCancellation,
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRunRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding,
        };

        foreach (string argument in request.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void Start(Process process)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("No process resource was started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new ProcessStartException(exception.NativeErrorCode);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or NotSupportedException)
        {
            throw new ProcessStartException(nativeErrorCode: null);
        }
    }

    private static bool NeedsCleanup(
        Process process,
        Task<CapturedOutput> standardOutputTask,
        Task<CapturedOutput> standardErrorTask)
    {
        if (!standardOutputTask.IsCompleted || !standardErrorTask.IsCompleted)
        {
            return true;
        }

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static async Task<CapturedOutput> CaptureAsync(
        Stream stream,
        int maxCapturedBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        using MemoryStream captured = new(Math.Min(maxCapturedBytes, StreamBufferSize));
        bool truncated = false;

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, StreamBufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                int remaining = maxCapturedBytes - checked((int)captured.Length);
                int retained = Math.Min(remaining, read);
                if (retained > 0)
                {
                    captured.Write(buffer, 0, retained);
                }

                truncated |= retained < read;
            }

            return new CapturedOutput(
                DecodeCapturedOutput(
                    captured.GetBuffer().AsSpan(0, checked((int)captured.Length)),
                    truncated),
                truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string DecodeCapturedOutput(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (truncated)
        {
            while (!bytes.IsEmpty &&
                   Rune.DecodeLastFromUtf8(bytes, out _, out _) == OperationStatus.NeedMoreData)
            {
                bytes = bytes[..^1];
            }
        }

        return OutputEncoding.GetString(bytes);
    }

    private static async Task<CapturedOutput[]> WaitForCapturedOutputAsync(
        Task<CapturedOutput> standardOutputTask,
        Task<CapturedOutput> standardErrorTask,
        CancellationToken cancellationToken)
    {
        Task<CapturedOutput[]> combined = Task.WhenAll(standardOutputTask, standardErrorTask);
        try
        {
            return await combined.WaitAsync(
                OutputDrainTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "The redirected output streams did not close within the bounded drain interval.",
                exception);
        }
    }

    private static async Task CleanupAsync(
        Process process,
        CancellationTokenSource captureCancellation,
        Task<CapturedOutput> standardOutputTask,
        Task<CapturedOutput> standardErrorTask)
    {
        TryKillProcessTree(process);
        await TryWaitForExitAsync(process).ConfigureAwait(false);

        if (await TryWaitForCaptureAsync(
            standardOutputTask,
            standardErrorTask).ConfigureAwait(false))
        {
            return;
        }

        captureCancellation.Cancel();
        await TryWaitForCaptureAsync(
            standardOutputTask,
            standardErrorTask).ConfigureAwait(false);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }

    private static async Task TryWaitForExitAsync(Process process)
    {
        using CancellationTokenSource cancellation = new(CleanupWaitTimeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task<bool> TryWaitForCaptureAsync(
        Task<CapturedOutput> standardOutputTask,
        Task<CapturedOutput> standardErrorTask)
    {
        Task combined = Task.WhenAll(standardOutputTask, standardErrorTask);
        Task completed = await Task.WhenAny(
            combined,
            Task.Delay(OutputDrainTimeout)).ConfigureAwait(false);
        if (completed != combined)
        {
            return false;
        }

        try
        {
            await combined.ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        return true;
    }

    private sealed record CapturedOutput(string Text, bool Truncated);
}