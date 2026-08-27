using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ProcessRunnerTests
{
    private static readonly string TestAssemblyPath = typeof(ProcessRunnerTests).Assembly.Location;
    private static readonly string TestWorkingDirectory =
        Path.GetDirectoryName(TestAssemblyPath)
        ?? throw new InvalidOperationException("The test assembly directory is unavailable.");
    private static readonly string DotNetExecutable = ResolveDotNetExecutable();

    [TestMethod]
    public void Request_ValidInput_DefensivelyCopiesArgumentsAndUsesSafeDefault()
    {
        List<string> arguments = ["--info"];

        ProcessRunRequest request = new(
            DotNetExecutable,
            arguments,
            TestWorkingDirectory,
            TimeSpan.FromSeconds(10));
        arguments[0] = "changed";
        arguments.Add("extra");

        Assert.AreEqual(DotNetExecutable, request.Executable);
        Assert.AreEqual("--info", request.ArgumentList[0]);
        Assert.AreEqual(1, request.ArgumentList.Count);
        Assert.AreEqual(TestWorkingDirectory, request.WorkingDirectory);
        Assert.AreEqual(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.AreEqual(ProcessRunRequest.DefaultMaxCapturedBytes, request.MaxCapturedBytes);

        IList<string> exposed = (IList<string>)request.ArgumentList;
        Assert.ThrowsExactly<NotSupportedException>(() => exposed.Add("mutation"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    [DataRow(" tool")]
    [DataRow("tool ")]
    [DataRow("\r\ntool")]
    [DataRow("tool\r\n")]
    public void Request_BlankOrUntrimmedExecutable_IsRejected(string executable)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessRunRequest(
                executable,
                [],
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1)));

        Assert.AreEqual("executable", exception.ParamName);
    }

    [TestMethod]
    [DataRow("dotnet")]
    [DataRow("dotnet.exe")]
    [DataRow(".\\dotnet.exe")]
    [DataRow("..\\dotnet.exe")]
    public void Request_RelativeExecutable_IsRejected(string executable)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessRunRequest(
                executable,
                [],
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1)));

        Assert.AreEqual("executable", exception.ParamName);
    }

    [TestMethod]
    public void Request_NullRequiredValues_AreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProcessRunRequest(
                null!,
                [],
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProcessRunRequest(
                DotNetExecutable,
                null!,
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProcessRunRequest(
                DotNetExecutable,
                [],
                null!,
                TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public void Request_NullArgumentElement_IsRejected()
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessRunRequest(
                DotNetExecutable,
                ["valid", null!],
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1)));

        Assert.AreEqual("argumentList", exception.ParamName);
    }

    [TestMethod]
    public void Request_WorkingDirectoryMustBeExistingAbsoluteDirectory()
    {
        string missingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"hubradio-missing-{Guid.NewGuid():N}");
        string[] invalidDirectories =
        [
            string.Empty,
            "relative-directory",
            $" {TestWorkingDirectory}",
            $"{TestWorkingDirectory} ",
            missingDirectory,
            TestAssemblyPath,
        ];

        foreach (string workingDirectory in invalidDirectories)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ProcessRunRequest(
                    DotNetExecutable,
                    [],
                    workingDirectory,
                    TimeSpan.FromSeconds(1)));

            Assert.AreEqual("workingDirectory", exception.ParamName);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Request_NonPositiveTimeout_IsRejected(int timeoutMilliseconds)
    {
        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ProcessRunRequest(
                    DotNetExecutable,
                    [],
                    TestWorkingDirectory,
                    TimeSpan.FromMilliseconds(timeoutMilliseconds)));

        Assert.AreEqual("timeout", exception.ParamName);
    }

    [TestMethod]
    public void Request_TimeoutBeyondRuntimeTimerRange_IsRejected()
    {
        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ProcessRunRequest(
                    DotNetExecutable,
                    [],
                    TestWorkingDirectory,
                    TimeSpan.MaxValue));

        Assert.AreEqual("timeout", exception.ParamName);
    }

    [TestMethod]
    public void Request_MaximumRuntimeTimerTimeout_IsAccepted()
    {
        ProcessRunRequest request = new(
            DotNetExecutable,
            [],
            TestWorkingDirectory,
            ProcessRunRequest.MaximumTimeout);

        Assert.AreEqual(ProcessRunRequest.MaximumTimeout, request.Timeout);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(ProcessRunRequest.MaximumMaxCapturedBytes + 1)]
    public void Request_InvalidCaptureLimit_IsRejected(int maxCapturedBytes)
    {
        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ProcessRunRequest(
                    DotNetExecutable,
                    [],
                    TestWorkingDirectory,
                    TimeSpan.FromSeconds(1),
                    maxCapturedBytes));

        Assert.AreEqual("maxCapturedBytes", exception.ParamName);
    }

    [TestMethod]
    public async Task RunAsync_DotNetInfo_ReturnsNormalResult()
    {
        ProcessRunner runner = new();
        ProcessRunRequest request = new(
            DotNetExecutable,
            ["--info"],
            TestWorkingDirectory,
            TimeSpan.FromSeconds(30));

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.StandardOutput));
        Assert.IsFalse(result.Truncated);
        Assert.IsTrue(result.Duration >= TimeSpan.Zero);
    }

    [TestMethod]
    public async Task RunAsync_ArgumentsWithSpacesAndMetacharacters_RemainSeparate()
    {
        string[] expectedArguments =
        [
            "plain",
            "value with spaces",
            "alpha&beta",
            "semi;colon",
            "$(literal)",
            "<redirect-looking>",
            "\"quoted\"",
        ];
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "echo-arguments",
            expectedArguments);

        ProcessRunResult result = await runner.RunAsync(request);
        string[]? actualArguments = JsonSerializer.Deserialize<string[]>(result.StandardOutput);

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(actualArguments);
        CollectionAssert.AreEqual(expectedArguments, actualArguments);
        Assert.AreEqual(string.Empty, result.StandardError);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_SeparatesStandardOutputAndStandardError()
    {
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "write-streams",
            ["stdout-日本語", "stderr-日本語"]);

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("stdout-日本語", result.StandardOutput);
        Assert.AreEqual("stderr-日本語", result.StandardError);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_NonzeroExitCode_IsReturnedWithoutException()
    {
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest("exit", ["23"]);

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(23, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardOutput);
        Assert.AreEqual(string.Empty, result.StandardError);
    }

    [TestMethod]
    public async Task RunAsync_HugeOutput_IsBoundedAndBothPipesAreDrained()
    {
        const int captureLimit = 4096;
        const int emittedBytesPerStream = 512 * 1024;
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "write-huge-output",
            [emittedBytesPerStream.ToString(CultureInfo.InvariantCulture)],
            maxCapturedBytes: captureLimit);

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(captureLimit, Encoding.UTF8.GetByteCount(result.StandardOutput));
        Assert.AreEqual(captureLimit, Encoding.UTF8.GetByteCount(result.StandardError));
        Assert.IsTrue(result.StandardOutput.All(static character => character == 'O'));
        Assert.IsTrue(result.StandardError.All(static character => character == 'E'));
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_TruncationDropsIncompleteTrailingUtf8Rune()
    {
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "write-utf8",
            ["あいう"],
            maxCapturedBytes: 7);

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("あい", result.StandardOutput);
        Assert.AreEqual(6, Encoding.UTF8.GetByteCount(result.StandardOutput));
        Assert.IsFalse(result.StandardOutput.Contains('\uFFFD'));
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_Timeout_KillsProcessAndThrowsTimeoutException()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string processIdPath = Path.Combine(temporaryDirectory, "timeout.pid");
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "wait",
                [processIdPath],
                timeout: TimeSpan.FromSeconds(2));

            await Assert.ThrowsExactlyAsync<TimeoutException>(() => runner.RunAsync(request));
            stopwatch.Stop();

            int processId = await ReadProcessIdAsync(processIdPath);
            await AssertProcessExitedAsync(processId);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_CallerCancellation_KillsProcessAndThrowsOperationCanceledException()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string processIdPath = Path.Combine(temporaryDirectory, "cancel.pid");

        try
        {
            using CancellationTokenSource cancellation = new();
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "wait",
                [processIdPath],
                timeout: TimeSpan.FromSeconds(30));
            Task<ProcessRunResult> runTask = runner.RunAsync(request, cancellation.Token);
            int processId = await ReadProcessIdAsync(processIdPath);

            cancellation.Cancel();
            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runTask);

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            await AssertProcessExitedAsync(processId);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_PreCancelledToken_DoesNotStartProcess()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string processIdPath = Path.Combine(temporaryDirectory, "not-started.pid");

        try
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "wait",
                [processIdPath],
                timeout: TimeSpan.FromSeconds(30));

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => runner.RunAsync(request, cancellation.Token));

            Assert.IsFalse(File.Exists(processIdPath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_StartFailure_UsesStableExceptionWithoutPathContent()
    {
        string missingExecutable = Path.Combine(
            TestWorkingDirectory,
            $"missing-host-{Guid.NewGuid():N}.exe");
        ProcessRunner runner = new();
        ProcessRunRequest request = new(
            missingExecutable,
            [],
            TestWorkingDirectory,
            ProcessRunRequest.MaximumTimeout);

        ProcessStartException exception =
            await Assert.ThrowsExactlyAsync<ProcessStartException>(() => runner.RunAsync(request));

        Assert.AreEqual(ProcessStartException.StableErrorCode, exception.Code);
        Assert.IsNull(exception.InnerException);
        Assert.IsNotNull(exception.NativeErrorCode);
        Assert.IsFalse(exception.Message.Contains(missingExecutable, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.Message.Contains(TestWorkingDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(missingExecutable, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(TestWorkingDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private static ProcessRunRequest CreateHelperRequest(
        string mode,
        IReadOnlyList<string>? helperArguments = null,
        TimeSpan? timeout = null,
        int maxCapturedBytes = ProcessRunRequest.DefaultMaxCapturedBytes)
    {
        List<string> argumentList =
        [
            "exec",
            TestAssemblyPath,
            ProcessRunnerTestHelper.Marker,
            mode,
        ];
        if (helperArguments is not null)
        {
            argumentList.AddRange(helperArguments);
        }

        return new ProcessRunRequest(
            DotNetExecutable,
            argumentList,
            TestWorkingDirectory,
            timeout ?? TimeSpan.FromSeconds(30),
            maxCapturedBytes);
    }

    private static string ResolveDotNetExecutable()
    {
        string? dotNetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(dotNetHostPath) && File.Exists(dotNetHostPath))
        {
            return Path.GetFullPath(dotNetHostPath);
        }

        string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim().Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException("The current dotnet host could not be located.");
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"hubradio-process-runner-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<int> ReadProcessIdAsync(string path)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(10))
            {
                Assert.Fail("The child process did not publish its process identifier.");
            }

            await Task.Delay(20);
        }

        string value = await File.ReadAllTextAsync(path);
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cancellation.Token);
            }

            Assert.IsTrue(process.HasExited, "The child process remained alive after cleanup.");
        }
        catch (ArgumentException)
        {
        }
    }
}

internal static class ProcessRunnerTestHelper
{
    internal const string Marker = "--hubradio-process-runner-test-helper";

    [ModuleInitializer]
    public static void RunIfRequested()
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        int markerIndex = Array.IndexOf(commandLine, Marker);
        if (markerIndex < 0)
        {
            return;
        }

        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        int exitCode;
        try
        {
            exitCode = Run(commandLine[(markerIndex + 1)..]);
        }
        catch (Exception exception)
        {
            Console.Error.Write(exception.GetType().Name);
            exitCode = 97;
        }

        Environment.Exit(exitCode);
    }

    private static int Run(string[] arguments)
    {
        string mode = RequireArgument(arguments, 0);
        return mode switch
        {
            "echo-arguments" => EchoArguments(arguments[1..]),
            "write-streams" => WriteStreams(arguments),
            "write-huge-output" => WriteHugeOutput(arguments),
            "write-utf8" => WriteUtf8(arguments),
            "exit" => int.Parse(RequireArgument(arguments, 1), CultureInfo.InvariantCulture),
            "wait" => Wait(arguments),
            _ => 98,
        };
    }

    private static int EchoArguments(string[] arguments)
    {
        Console.Out.Write(JsonSerializer.Serialize(arguments));
        return 0;
    }

    private static int WriteStreams(string[] arguments)
    {
        Console.Out.Write(RequireArgument(arguments, 1));
        Console.Error.Write(RequireArgument(arguments, 2));
        return 0;
    }

    private static int WriteHugeOutput(string[] arguments)
    {
        int bytesPerStream = int.Parse(
            RequireArgument(arguments, 1),
            CultureInfo.InvariantCulture);
        const int chunkSize = 4096;
        string standardOutputChunk = new('O', chunkSize);
        string standardErrorChunk = new('E', chunkSize);

        for (int written = 0; written < bytesPerStream; written += chunkSize)
        {
            int count = Math.Min(chunkSize, bytesPerStream - written);
            Console.Out.Write(standardOutputChunk.AsSpan(0, count));
            Console.Error.Write(standardErrorChunk.AsSpan(0, count));
        }

        return 0;
    }

    private static int WriteUtf8(string[] arguments)
    {
        Console.Out.Write(RequireArgument(arguments, 1));
        return 0;
    }

    private static int Wait(string[] arguments)
    {
        File.WriteAllText(
            RequireArgument(arguments, 1),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }

    private static string RequireArgument(string[] arguments, int index) =>
        arguments.Length > index
            ? arguments[index]
            : throw new ArgumentException("A helper argument is missing.");
}