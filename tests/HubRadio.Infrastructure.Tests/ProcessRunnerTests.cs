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
    private const int WindowsLockViolation = 33;
    private const int WindowsSharingViolation = 32;

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
        Assert.IsNull(request.StandardInput);

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
    public void Request_StandardInputIsRetainedAndStrictlyBoundedAsUtf8()
    {
        const string standardInput = "line one\n日本語\n";
        ProcessRunRequest request = new(
            DotNetExecutable,
            [],
            TestWorkingDirectory,
            TimeSpan.FromSeconds(1),
            maxCapturedBytes: 64,
            standardInput: standardInput);

        Assert.AreEqual(standardInput, request.StandardInput);

        ArgumentException invalidUtf16 = Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessRunRequest(
                DotNetExecutable,
                [],
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1),
                maxCapturedBytes: 64,
                standardInput: "\uD800"));
        Assert.AreEqual("standardInput", invalidUtf16.ParamName);
        Assert.IsFalse(invalidUtf16.ToString().Contains("\uD800", StringComparison.Ordinal));

        ArgumentException tooLarge = Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessRunRequest(
                DotNetExecutable,
                [],
                TestWorkingDirectory,
                TimeSpan.FromSeconds(1),
                maxCapturedBytes: 5,
                standardInput: "ああ"));
        Assert.AreEqual("standardInput", tooLarge.ParamName);
        Assert.IsFalse(tooLarge.ToString().Contains("ああ", StringComparison.Ordinal));
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
    public void Request_NullCharacterOrInvalidUtf16Argument_IsRejectedWithoutContent()
    {
        const string sensitiveMarker = "ARGUMENT-SECRET-7E77";
        string[] invalidArguments =
        [
            "prefix\0" + sensitiveMarker,
            "\uD800" + sensitiveMarker,
        ];

        foreach (string invalidArgument in invalidArguments)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ProcessRunRequest(
                    DotNetExecutable,
                    [invalidArgument],
                    TestWorkingDirectory,
                    TimeSpan.FromSeconds(1)));

            Assert.AreEqual("argumentList", exception.ParamName);
            Assert.IsFalse(exception.ToString().Contains(
                sensitiveMarker,
                StringComparison.Ordinal));
        }
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
    public async Task RunAsync_AdversarialArgumentsRemainSeparateOpaqueTokens()
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
            "<script>x</script>",
            "--unknown-cli",
            "-vf=movie=attack",
            "bridge.p\u0456ng",
            "line one\nline two",
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
    public async Task RunAsync_StandardInputIsWrittenExactlyAndClosedAtEof()
    {
        const string standardInput = "first line\n日本語 & ; $(literal)\nlast\0value";
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "echo-standard-input",
            standardInput: standardInput);

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(standardInput, result.StandardOutput);
        Assert.AreEqual(string.Empty, result.StandardError);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_EmptyStandardInputStillClosesChildInput()
    {
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "echo-standard-input",
            standardInput: string.Empty);

        ProcessRunResult result = await runner.RunAsync(request);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardOutput);
        Assert.AreEqual(string.Empty, result.StandardError);
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
    public async Task RunAsync_CancellationDuringBlockedStandardInputWriteKillsProcessWithoutLeakingInput()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string processIdPath = Path.Combine(temporaryDirectory, "stdin-cancel.pid");
        const string sensitiveMarker = "sensitive-standard-input";
        string standardInput = string.Concat(
            Enumerable.Repeat(
                sensitiveMarker,
                (ProcessRunRequest.DefaultMaxCapturedBytes / sensitiveMarker.Length) - 1));

        try
        {
            using CancellationTokenSource cancellation = new();
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "wait",
                [processIdPath],
                timeout: TimeSpan.FromSeconds(30),
                standardInput: standardInput);
            Task<ProcessRunResult> runTask = runner.RunAsync(request, cancellation.Token);
            int processId = await ReadProcessIdAsync(processIdPath);

            cancellation.Cancel();
            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runTask);

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsFalse(exception.ToString().Contains(sensitiveMarker, StringComparison.Ordinal));
            await AssertProcessExitedAsync(processId);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WindowsJobObject_DisposeKillsAssignedProcessAndInheritedDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        string temporaryDirectory = CreateTemporaryDirectory();
        string parentProcessIdPath = Path.Combine(temporaryDirectory, "parent.pid");
        string startDescendantPath = Path.Combine(temporaryDirectory, "start-descendant");
        string descendantProcessIdPath = Path.Combine(temporaryDirectory, "descendant.pid");
        Process? parent = null;
        Process? descendant = null;
        WindowsJobObject? jobObject = null;

        try
        {
            jobObject = WindowsJobObject.CreateKillOnClose();
            parent = StartHelperProcess(
                "spawn-descendant",
                parentProcessIdPath,
                startDescendantPath,
                descendantProcessIdPath,
                "wait");
            await ReadProcessIdAsync(parentProcessIdPath);
            jobObject.Assign(parent);
            File.WriteAllText(startDescendantPath, string.Empty);
            descendant = OpenRunningProcess(await ReadProcessIdAsync(descendantProcessIdPath));
            AssertProcessIsRunning(parent);

            jobObject.Dispose();

            await AssertProcessExitedAsync(parent);
            await AssertProcessExitedAsync(descendant);
        }
        finally
        {
            jobObject?.Dispose();
            await TryTerminateProcessTreeAsync(parent);
            parent?.Dispose();
            await TryTerminateProcessTreeAsync(descendant);
            descendant?.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AssignToJobOrStopAsync_AssignmentFailureStopsProcessAndPreservesException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        string temporaryDirectory = CreateTemporaryDirectory();
        string processIdPath = Path.Combine(temporaryDirectory, "uncontained.pid");
        Process? process = null;

        try
        {
            using WindowsJobObject disposedJob = WindowsJobObject.CreateKillOnClose();
            disposedJob.Dispose();
            process = StartHelperProcess("wait", processIdPath);
            await ReadProcessIdAsync(processIdPath);
            AssertProcessIsRunning(process);

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => ProcessRunner.AssignToJobOrStopAsync(process, disposedJob));

            await AssertProcessExitedAsync(process);
        }
        finally
        {
            await TryTerminateProcessTreeAsync(process);
            process?.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AssignToJobOrStopAsync_AssignmentAndCleanupFailurePreserveBothExceptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        using WindowsJobObject disposedJob = WindowsJobObject.CreateKillOnClose();
        disposedJob.Dispose();
        using Process unstartedProcess = new();

        AggregateException exception = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => ProcessRunner.AssignToJobOrStopAsync(unstartedProcess, disposedJob));

        Assert.HasCount(2, exception.InnerExceptions);
        Assert.IsInstanceOfType<ObjectDisposedException>(exception.InnerExceptions[0]);
        Assert.IsInstanceOfType<AggregateException>(exception.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task RunAsync_NormalRootExitClosesJobAndKillsRemainingDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        string temporaryDirectory = CreateTemporaryDirectory();
        string parentProcessIdPath = Path.Combine(temporaryDirectory, "parent.pid");
        string startDescendantPath = Path.Combine(temporaryDirectory, "start-descendant");
        string descendantProcessIdPath = Path.Combine(temporaryDirectory, "descendant.pid");
        string allowParentExitPath = Path.Combine(temporaryDirectory, "allow-parent-exit");
        Process? parent = null;
        Process? descendant = null;

        try
        {
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "spawn-descendant",
                [
                    parentProcessIdPath,
                    startDescendantPath,
                    descendantProcessIdPath,
                    "exit",
                    allowParentExitPath,
                ]);
            Task<ProcessRunResult> runTask = runner.RunAsync(request);
            parent = OpenRunningProcess(await ReadProcessIdAsync(parentProcessIdPath));
            File.WriteAllText(startDescendantPath, string.Empty);
            descendant = OpenRunningProcess(await ReadProcessIdAsync(descendantProcessIdPath));
            AssertProcessIsRunning(parent);
            File.WriteAllText(allowParentExitPath, string.Empty);

            ProcessRunResult result = await runTask;

            Assert.AreEqual(0, result.ExitCode);
            await AssertProcessExitedAsync(parent);
            await AssertProcessExitedAsync(descendant);
        }
        finally
        {
            await TryTerminateProcessTreeAsync(parent);
            parent?.Dispose();
            await TryTerminateProcessTreeAsync(descendant);
            descendant?.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_TimeoutKillsRootAndInheritedDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        string temporaryDirectory = CreateTemporaryDirectory();
        string parentProcessIdPath = Path.Combine(temporaryDirectory, "parent.pid");
        string startDescendantPath = Path.Combine(temporaryDirectory, "start-descendant");
        string descendantProcessIdPath = Path.Combine(temporaryDirectory, "descendant.pid");
        Process? parent = null;
        Process? descendant = null;

        try
        {
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "spawn-descendant",
                [
                    parentProcessIdPath,
                    startDescendantPath,
                    descendantProcessIdPath,
                    "wait",
                ],
                timeout: TimeSpan.FromSeconds(5));
            Task<ProcessRunResult> runTask = runner.RunAsync(request);
            parent = OpenRunningProcess(await ReadProcessIdAsync(parentProcessIdPath));
            File.WriteAllText(startDescendantPath, string.Empty);
            descendant = OpenRunningProcess(await ReadProcessIdAsync(descendantProcessIdPath));
            AssertProcessIsRunning(parent);

            await Assert.ThrowsExactlyAsync<TimeoutException>(() => runTask);

            await AssertProcessExitedAsync(parent);
            await AssertProcessExitedAsync(descendant);
        }
        finally
        {
            await TryTerminateProcessTreeAsync(parent);
            parent?.Dispose();
            await TryTerminateProcessTreeAsync(descendant);
            descendant?.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_CancellationKillsRootAndInheritedDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        string temporaryDirectory = CreateTemporaryDirectory();
        string parentProcessIdPath = Path.Combine(temporaryDirectory, "parent.pid");
        string startDescendantPath = Path.Combine(temporaryDirectory, "start-descendant");
        string descendantProcessIdPath = Path.Combine(temporaryDirectory, "descendant.pid");
        Process? parent = null;
        Process? descendant = null;

        try
        {
            using CancellationTokenSource cancellation = new();
            ProcessRunner runner = new();
            ProcessRunRequest request = CreateHelperRequest(
                "spawn-descendant",
                [
                    parentProcessIdPath,
                    startDescendantPath,
                    descendantProcessIdPath,
                    "wait",
                ],
                timeout: TimeSpan.FromSeconds(30));
            Task<ProcessRunResult> runTask = runner.RunAsync(request, cancellation.Token);
            parent = OpenRunningProcess(await ReadProcessIdAsync(parentProcessIdPath));
            File.WriteAllText(startDescendantPath, string.Empty);
            descendant = OpenRunningProcess(await ReadProcessIdAsync(descendantProcessIdPath));
            AssertProcessIsRunning(parent);

            cancellation.Cancel();
            OperationCanceledException exception =
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runTask);

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            await AssertProcessExitedAsync(parent);
            await AssertProcessExitedAsync(descendant);
        }
        finally
        {
            await TryTerminateProcessTreeAsync(parent);
            parent?.Dispose();
            await TryTerminateProcessTreeAsync(descendant);
            descendant?.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_HostCrashClosesJobAndKillsRootAndInheritedDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects require Windows.");
            return;
        }

        string temporaryDirectory = CreateTemporaryDirectory();
        string parentProcessIdPath = Path.Combine(temporaryDirectory, "parent.pid");
        string startDescendantPath = Path.Combine(temporaryDirectory, "start-descendant");
        string descendantProcessIdPath = Path.Combine(temporaryDirectory, "descendant.pid");
        string allowHostCrashPath = Path.Combine(temporaryDirectory, "allow-host-crash");
        string failFastEnteredPath = Path.Combine(temporaryDirectory, "fail-fast-entered.pid");
        Process? crashHost = null;
        Process? parent = null;
        Process? descendant = null;

        try
        {
            crashHost = StartHelperProcess(
                "crash-runner-host",
                parentProcessIdPath,
                startDescendantPath,
                descendantProcessIdPath,
                allowHostCrashPath,
                failFastEnteredPath);
            Task<string> standardOutputTask = crashHost.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = crashHost.StandardError.ReadToEndAsync();
            parent = OpenRunningProcess(await ReadProcessIdAsync(parentProcessIdPath));
            descendant = OpenRunningProcess(await ReadProcessIdAsync(descendantProcessIdPath));
            AssertProcessIsRunning(crashHost);
            File.WriteAllText(allowHostCrashPath, string.Empty);
            int failFastProcessId = await ReadProcessIdAsync(failFastEnteredPath);
            Assert.AreEqual(crashHost.Id, failFastProcessId);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));

            try
            {
                await crashHost.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !crashHost.HasExited)
            {
                // The 15-second wait remains the pass budget. If the helper reached
                // Environment.FailFast and Windows Error Reporting keeps it alive past that
                // budget, wait only to verify job cleanup, then report Inconclusive; this is
                // not a successful test result.
                await crashHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                await AssertProcessesExitedAsync(parent, descendant);
                string delayedStandardOutput = await standardOutputTask.WaitAsync(TimeSpan.FromSeconds(5));
                string delayedStandardError = await standardErrorTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.AreNotEqual(0, crashHost.ExitCode);
                Assert.AreEqual(string.Empty, delayedStandardOutput);
                Assert.IsFalse(delayedStandardError.Contains(temporaryDirectory, StringComparison.OrdinalIgnoreCase));
                Assert.Inconclusive(
                    "Windows Error Reporting kept the Environment.FailFast helper process alive " +
                    "beyond the 15-second budget after it reached the crash point. Job cleanup " +
                    "was verified after the delayed termination. Inconclusive is not success.");
                return;
            }

            await AssertProcessesExitedAsync(parent, descendant);
            string standardOutput = await standardOutputTask.WaitAsync(TimeSpan.FromSeconds(5));
            string standardError = await standardErrorTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreNotEqual(0, crashHost.ExitCode);
            Assert.AreEqual(string.Empty, standardOutput);
            Assert.IsFalse(standardError.Contains(temporaryDirectory, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await TryTerminateProcessTreeAsync(crashHost);
            crashHost?.Dispose();
            await TryTerminateProcessTreeAsync(parent);
            parent?.Dispose();
            await TryTerminateProcessTreeAsync(descendant);
            descendant?.Dispose();
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
        int maxCapturedBytes = ProcessRunRequest.DefaultMaxCapturedBytes,
        string? standardInput = null)
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
            maxCapturedBytes,
            standardInput);
    }

    private static Process StartHelperProcess(string mode, params string[] helperArguments)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotNetExecutable,
                WorkingDirectory = TestWorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in new[]
        {
            "exec",
            TestAssemblyPath,
            ProcessRunnerTestHelper.Marker,
            mode,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (string argument in helperArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The process test helper could not be started.");
        }

        return process;
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
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    string value = await File.ReadAllTextAsync(path);
                    return int.Parse(value, CultureInfo.InvariantCulture);
                }
            }
            catch (IOException exception) when (IsTransientProcessIdReadFailure(exception))
            {
            }

            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(10))
            {
                Assert.Fail("The child process did not publish its process identifier.");
            }

            await Task.Delay(20);
        }
    }

    private static bool IsTransientProcessIdReadFailure(IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() &&
            nativeError is WindowsSharingViolation or WindowsLockViolation;
    }

    private static Process OpenRunningProcess(int processId)
    {
        Process process = Process.GetProcessById(processId);
        try
        {
            AssertProcessIsRunning(process);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void AssertProcessIsRunning(Process process)
    {
        _ = process.SafeHandle;
        Assert.IsFalse(
            process.HasExited,
            "The child process exited before the containment event was triggered.");
    }

    private static async Task AssertProcessExitedAsync(Process process)
    {
        if (!process.HasExited)
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
        }

        Assert.IsTrue(process.HasExited, "The child process remained alive after cleanup.");
    }

    private static async Task AssertProcessesExitedAsync(params Process[] processes)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            processes.Select(process => process.WaitForExitAsync(cancellation.Token)));

        foreach (Process process in processes)
        {
            Assert.IsTrue(process.HasExited, "A child process remained alive after cleanup.");
        }
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

    private static async Task TryTerminateProcessTreeAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cancellation.Token);
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
            "echo-standard-input" => EchoStandardInput(),
            "write-streams" => WriteStreams(arguments),
            "write-huge-output" => WriteHugeOutput(arguments),
            "write-utf8" => WriteUtf8(arguments),
            "exit" => int.Parse(RequireArgument(arguments, 1), CultureInfo.InvariantCulture),
            "wait" => Wait(arguments),
            "spawn-descendant" => SpawnDescendant(arguments),
            "crash-runner-host" => CrashRunnerHost(arguments),
            _ => 98,
        };
    }

    private static int EchoArguments(string[] arguments)
    {
        Console.Out.Write(JsonSerializer.Serialize(arguments));
        return 0;
    }

    private static int EchoStandardInput()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        input.CopyTo(output);
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
        string processIdPath = RequireArgument(arguments, 1);
        WriteProcessIdAtomically(processIdPath);
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }

    private static int SpawnDescendant(string[] arguments)
    {
        string parentProcessIdPath = RequireArgument(arguments, 1);
        string startDescendantPath = RequireArgument(arguments, 2);
        string descendantProcessIdPath = RequireArgument(arguments, 3);
        string behavior = RequireArgument(arguments, 4);
        WriteProcessIdAtomically(parentProcessIdPath);
        WaitForFile(startDescendantPath);

        using Process descendant = StartHelperProcess("wait", descendantProcessIdPath);
        WaitForFile(descendantProcessIdPath);
        if (string.Equals(behavior, "exit", StringComparison.Ordinal))
        {
            string allowParentExitPath = RequireArgument(arguments, 5);
            WaitForFile(allowParentExitPath);
            return 0;
        }

        if (!string.Equals(behavior, "wait", StringComparison.Ordinal))
        {
            throw new ArgumentException("The descendant helper behavior is invalid.");
        }

        descendant.WaitForExit();
        return descendant.ExitCode;
    }

    private static int CrashRunnerHost(string[] arguments)
    {
        string parentProcessIdPath = RequireArgument(arguments, 1);
        string startDescendantPath = RequireArgument(arguments, 2);
        string descendantProcessIdPath = RequireArgument(arguments, 3);
        string allowHostCrashPath = RequireArgument(arguments, 4);
        string failFastEnteredPath = RequireArgument(arguments, 5);
        ProcessRunner runner = new();
        ProcessRunRequest request = CreateHelperRequest(
            "spawn-descendant",
            parentProcessIdPath,
            startDescendantPath,
            descendantProcessIdPath,
            "wait");
        _ = runner.RunAsync(request);
        WaitForFile(parentProcessIdPath);
        File.WriteAllText(startDescendantPath, string.Empty);
        WaitForFile(descendantProcessIdPath);
        WaitForFile(allowHostCrashPath);
        WriteProcessIdAtomically(failFastEnteredPath);
        Environment.FailFast("Intentional J-004 process containment test crash.");
        return 99;
    }

    private static ProcessRunRequest CreateHelperRequest(
        string mode,
        params string[] helperArguments)
    {
        List<string> argumentList =
        [
            "exec",
            typeof(ProcessRunnerTestHelper).Assembly.Location,
            Marker,
            mode,
        ];
        argumentList.AddRange(helperArguments);

        return new ProcessRunRequest(
            Environment.ProcessPath
                ?? throw new InvalidOperationException("The dotnet host path is unavailable."),
            argumentList,
            AppContext.BaseDirectory,
            TimeSpan.FromSeconds(30));
    }

    private static Process StartHelperProcess(string mode, params string[] helperArguments)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The dotnet host path is unavailable."),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in new[]
        {
            "exec",
            typeof(ProcessRunnerTestHelper).Assembly.Location,
            Marker,
            mode,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (string argument in helperArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The descendant helper could not be started.");
        }

        return process;
    }

    private static void WriteProcessIdAtomically(string processIdPath)
    {
        string temporaryPath = processIdPath + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(
            temporaryPath,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, processIdPath);
    }

    private static void WaitForFile(string path)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("A process test helper handshake timed out.");
            }

            Thread.Sleep(20);
        }
    }

    private static string RequireArgument(string[] arguments, int index) =>
        arguments.Length > index
            ? arguments[index]
            : throw new ArgumentException("A helper argument is missing.");
}