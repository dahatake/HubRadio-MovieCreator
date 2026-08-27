using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This probe requires Windows.");
            return 2;
        }

        try
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("A probe mode is required.");
                return 2;
            }

            return args[0] switch
            {
                "self-test" => await RunSelfTestAsync(),
                "crash-host" => await RunCrashHostAsync(RequireArgument(args, 1, "state path")),
                "wait-child" => await RunWaitChildAsync(args),
                "wait-grandchild" => await RunWaitGrandchildAsync(RequireArgument(args, 1, "PID path")),
                "memory-hog" => await RunMemoryHogAsync(
                    RequireArgument(args, 1, "result path"),
                    RequireArgument(args, 2, "ready path"),
                    RequireArgument(args, 3, "go path")),
                "write-png" => await WriteSolidPngAsync(
                    RequireArgument(args, 1, "output path"),
                    int.Parse(RequireArgument(args, 2, "image size"))),
                "verify-package" => await RunPackageVerificationAsync(args),
                _ => throw new ArgumentException($"Unknown probe mode: {args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSelfTestAsync()
    {
        Dictionary<string, object?> report = new()
        {
            ["schemaVersion"] = "1.0.0",
            ["processId"] = Environment.ProcessId,
            ["killOnClose"] = await TestKillOnCloseAsync(),
            ["activeProcessLimit"] = await TestActiveProcessLimitAsync(),
            ["processMemoryLimit"] = await TestProcessMemoryLimitAsync(),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static async Task<Dictionary<string, object?>> TestKillOnCloseAsync()
    {
        using SafeJobHandle job = CreateConfiguredJob(JobObjectLimitKillOnJobClose);
        string work = CreateTemporaryDirectory();
        string ready = Path.Combine(work, "ready");
        string go = Path.Combine(work, "go");
        string grandchildPid = Path.Combine(work, "grandchild.pid");
        using Process child = StartProbe("wait-child", ready, go, grandchildPid);
        AssignProcess(job, child);
        await WaitForFileAsync(ready, TimeSpan.FromSeconds(10));
        File.WriteAllText(go, string.Empty);
        await WaitForFileAsync(grandchildPid, TimeSpan.FromSeconds(10));
        int grandchildProcessId = int.Parse(await File.ReadAllTextAsync(grandchildPid));
        bool childInJobBeforeClose = IsProcessInSpecificJob(child.Id, job);
        bool grandchildInJobBeforeClose = IsProcessInSpecificJob(grandchildProcessId, job);
        int[] processIdsBeforeClose = QueryJobProcessIds(job);
        Stopwatch stopwatch = Stopwatch.StartNew();
        job.Dispose();
        bool childExited = await WaitForExitAsync(child, TimeSpan.FromSeconds(10));
        bool grandchildExited = await WaitForProcessAbsentAsync(grandchildProcessId, TimeSpan.FromSeconds(10));
        List<int> observedProcessIdsStillRunning = [];
        foreach (int processId in processIdsBeforeClose)
        {
            if (!await WaitForProcessAbsentAsync(processId, TimeSpan.FromSeconds(10)))
            {
                observedProcessIdsStillRunning.Add(processId);
            }
        }
        stopwatch.Stop();
        Directory.Delete(work, recursive: true);
        return new Dictionary<string, object?>
        {
            ["limitFlags"] = JobObjectLimitKillOnJobClose,
            ["childInJobBeforeClose"] = childInJobBeforeClose,
            ["grandchildInJobBeforeClose"] = grandchildInJobBeforeClose,
            ["observedProcessIdCountBeforeClose"] = processIdsBeforeClose.Length,
            ["childExitedAfterClose"] = childExited,
            ["grandchildExitedAfterClose"] = grandchildExited,
            ["allObservedProcessIdsExitedAfterClose"] = observedProcessIdsStillRunning.Count == 0,
            ["observedProcessIdLeakCount"] = observedProcessIdsStillRunning.Count,
            ["elapsedMilliseconds"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            ["passed"] = childInJobBeforeClose && grandchildInJobBeforeClose && childExited &&
                grandchildExited && observedProcessIdsStillRunning.Count == 0,
        };
    }

    private static async Task<Dictionary<string, object?>> TestActiveProcessLimitAsync()
    {
        using SafeJobHandle job = CreateConfiguredJob(
            JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
            activeProcessLimit: 1);
        string work = CreateTemporaryDirectory();
        try
        {
            using Process first = StartProbe("wait-grandchild", Path.Combine(work, "first.pid"));
            AssignProcess(job, first);
            Process? second = StartProbe("wait-grandchild", Path.Combine(work, "second.pid"));
            try
            {
                bool secondAssignmentSucceeded = AssignProcessToJobObject(job, second.Handle);
                int assignmentError = secondAssignmentSucceeded ? 0 : Marshal.GetLastWin32Error();
                bool secondInJobAfterAttempt = IsProcessInSpecificJob(second.Id, job);
                bool secondExitedAutomatically = await WaitForExitAsync(second, TimeSpan.FromSeconds(10));
                bool firstStillRunning = !first.HasExited;
                int[] processIdsBeforeCleanup = QueryJobProcessIds(job);
                JobObjectBasicAccountingInformation accountingBeforeCleanup = QueryBasicAccounting(job);
                bool callerCleanupRequired = !secondExitedAutomatically;
                if (callerCleanupRequired)
                {
                    await TerminateProcessAsync(second, TimeSpan.FromSeconds(10));
                }
                bool secondExitedAfterCleanup = second.HasExited;
                int secondProcessId = second.Id;
                second.Dispose();
                second = null;
                bool secondProcessAbsentAfterCleanup = await WaitForProcessAbsentAsync(secondProcessId, TimeSpan.FromSeconds(10));
                (JobObjectBasicAccountingInformation accountingAfterCleanup, int[] processIdsAfterCleanup, bool cleanupAccountingConverged) =
                    await WaitForJobActiveProcessCountAsync(job, expectedActiveProcessCount: 1, TimeSpan.FromSeconds(10));
                job.Dispose();
                await WaitForExitAsync(first, TimeSpan.FromSeconds(10));
                return new Dictionary<string, object?>
                {
                    ["limitFlags"] = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
                    ["activeProcessLimit"] = 1,
                    ["firstAssignmentSucceeded"] = true,
                    ["secondAssignmentSucceeded"] = secondAssignmentSucceeded,
                    ["secondAssignmentWin32Error"] = assignmentError,
                    ["secondInJobAfterAttempt"] = secondInJobAfterAttempt,
                    ["secondExitedAutomatically"] = secondExitedAutomatically,
                    ["callerCleanupRequired"] = callerCleanupRequired,
                    ["secondExitedAfterCleanup"] = secondExitedAfterCleanup,
                    ["secondProcessAbsentAfterCleanup"] = secondProcessAbsentAfterCleanup,
                    ["firstStillRunningAfterRejectedAssignment"] = firstStillRunning,
                    ["processIdListCountBeforeCleanup"] = processIdsBeforeCleanup.Length,
                    ["accountingActiveProcessCountBeforeCleanup"] = accountingBeforeCleanup.ActiveProcesses,
                    ["processIdListCountAfterCleanup"] = processIdsAfterCleanup.Length,
                    ["accountingActiveProcessCountAfterCleanup"] = accountingAfterCleanup.ActiveProcesses,
                    ["cleanupAccountingConverged"] = cleanupAccountingConverged,
                    ["documentedAutomaticTerminationObserved"] = secondExitedAutomatically,
                    ["safeCallerCleanupContractPassed"] = !secondAssignmentSucceeded && secondExitedAfterCleanup &&
                        secondProcessAbsentAfterCleanup && firstStillRunning,
                    ["passed"] = !secondAssignmentSucceeded && secondExitedAutomatically && firstStillRunning &&
                        cleanupAccountingConverged && processIdsAfterCleanup.Length == 1 && accountingAfterCleanup.ActiveProcesses == 1,
                };
            }
            finally
            {
                if (second is not null)
                {
                    try
                    {
                        await TerminateProcessAsync(second, TimeSpan.FromSeconds(10));
                    }
                    finally
                    {
                        second.Dispose();
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task<Dictionary<string, object?>> TestProcessMemoryLimitAsync()
    {
        const ulong memoryLimitBytes = 128UL * 1024 * 1024;
        using SafeJobHandle job = CreateConfiguredJob(
            JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory,
            processMemoryLimit: (nuint)memoryLimitBytes);
        string work = CreateTemporaryDirectory();
        string resultPath = Path.Combine(work, "memory.json");
        string readyPath = Path.Combine(work, "memory.ready");
        string goPath = Path.Combine(work, "memory.go");
        using Process child = StartProbe("memory-hog", resultPath, readyPath, goPath);
        AssignProcess(job, child);
        await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(10));
        File.WriteAllText(goPath, string.Empty);
        bool childExited = await WaitForExitAsync(child, TimeSpan.FromSeconds(30));
        Dictionary<string, object?>? childResult = null;
        if (File.Exists(resultPath))
        {
            childResult = JsonSerializer.Deserialize<Dictionary<string, object?>>(await File.ReadAllTextAsync(resultPath), JsonOptions);
        }
        JobObjectExtendedLimitInformation information = QueryExtendedLimits(job);
        job.Dispose();
        Directory.Delete(work, recursive: true);
        bool allocationFailed = childResult is not null &&
            childResult.TryGetValue("allocationFailed", out object? failedValue) &&
            failedValue is JsonElement element && element.ValueKind == JsonValueKind.True;
        long? allocatedBytesBeforeFailure = ReadJsonInt64(childResult, "allocatedBytesBeforeFailure");
        long? workingSetBytes = ReadJsonInt64(childResult, "workingSetBytes");
        return new Dictionary<string, object?>
        {
            ["limitFlags"] = JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory,
            ["processMemoryLimitBytes"] = memoryLimitBytes,
            ["childExited"] = childExited,
            ["childExitCode"] = childExited ? child.ExitCode : null,
            ["allocationStartedAfterJobAssignment"] = true,
            ["allocationFailed"] = allocationFailed,
            ["allocatedBytesBeforeFailure"] = allocatedBytesBeforeFailure,
            ["childReportedWorkingSetBytes"] = workingSetBytes,
            ["peakProcessMemoryUsedBytes"] = checked((ulong)information.PeakProcessMemoryUsed),
            ["passed"] = childExited && allocationFailed && checked((ulong)information.PeakProcessMemoryUsed) <= memoryLimitBytes,
        };
    }

    private static async Task<int> RunCrashHostAsync(string statePath)
    {
        using SafeJobHandle job = CreateConfiguredJob(JobObjectLimitKillOnJobClose);
        string work = CreateTemporaryDirectory();
        string ready = Path.Combine(work, "ready");
        string go = Path.Combine(work, "go");
        string grandchildPid = Path.Combine(work, "grandchild.pid");
        using Process child = StartProbe("wait-child", ready, go, grandchildPid);
        AssignProcess(job, child);
        await WaitForFileAsync(ready, TimeSpan.FromSeconds(10));
        File.WriteAllText(go, string.Empty);
        await WaitForFileAsync(grandchildPid, TimeSpan.FromSeconds(10));
        int grandchildProcessId = int.Parse(await File.ReadAllTextAsync(grandchildPid));
        Dictionary<string, object?> state = new()
        {
            ["hostProcessId"] = Environment.ProcessId,
            ["childProcessId"] = child.Id,
            ["grandchildProcessId"] = grandchildProcessId,
            ["childInJob"] = IsProcessInSpecificJob(child.Id, job),
            ["grandchildInJob"] = IsProcessInSpecificJob(grandchildProcessId, job),
            ["temporaryDirectory"] = work,
            ["ready"] = true,
        };
        await WriteJsonAtomicallyAsync(statePath, state);
        Environment.FailFast("Intentional ST-014 crash-host termination.");
        return 99;
    }

    private static async Task<int> RunWaitChildAsync(string[] args)
    {
        string readyPath = RequireArgument(args, 1, "ready path");
        string goPath = RequireArgument(args, 2, "go path");
        string grandchildPidPath = RequireArgument(args, 3, "grandchild PID path");
        await WriteTextAtomicallyAsync(readyPath, Environment.ProcessId.ToString());
        await WaitForFileAsync(goPath, TimeSpan.FromSeconds(30));
        using Process grandchild = StartProbe("wait-grandchild", grandchildPidPath);
        await grandchild.WaitForExitAsync();
        return grandchild.ExitCode;
    }

    private static async Task<int> RunWaitGrandchildAsync(string pidPath)
    {
        await WriteTextAtomicallyAsync(pidPath, Environment.ProcessId.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> RunMemoryHogAsync(string resultPath, string readyPath, string goPath)
    {
        List<byte[]> allocations = [];
        bool allocationFailed = false;
        await WriteTextAtomicallyAsync(readyPath, Environment.ProcessId.ToString());
        await WaitForFileAsync(goPath, TimeSpan.FromSeconds(30));
        try
        {
            for (int index = 0; index < 64; index++)
            {
                byte[] allocation = GC.AllocateUninitializedArray<byte>(8 * 1024 * 1024, pinned: false);
                Array.Fill(allocation, (byte)0xA5);
                allocations.Add(allocation);
                await Task.Yield();
            }
        }
        catch (OutOfMemoryException)
        {
            allocationFailed = true;
        }
        Dictionary<string, object?> result = new()
        {
            ["allocationFailed"] = allocationFailed,
            ["allocatedBytesBeforeFailure"] = allocations.Sum(item => (long)item.Length),
            ["workingSetBytes"] = Environment.WorkingSet,
        };
        await WriteJsonAtomicallyAsync(resultPath, result);
        return allocationFailed ? 42 : 0;
    }

    private static async Task<int> RunPackageVerificationAsync(string[] args)
    {
        string ffprobePath = RequireArgument(args, 1, "ffprobe path");
        string ffmpegPath = RequireArgument(args, 2, "ffmpeg path");
        string mediaPath = RequireArgument(args, 3, "media path");
        string modelPath = RequireArgument(args, 4, "model path");
        string expectedModelHash = RequireArgument(args, 5, "model SHA-256");
        using Process ffprobe = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        ffprobe.StartInfo.ArgumentList.Add("-v");
        ffprobe.StartInfo.ArgumentList.Add("error");
        ffprobe.StartInfo.ArgumentList.Add("-show_entries");
        ffprobe.StartInfo.ArgumentList.Add("stream=codec_name,sample_rate,channels");
        ffprobe.StartInfo.ArgumentList.Add("-of");
        ffprobe.StartInfo.ArgumentList.Add("json");
        ffprobe.StartInfo.ArgumentList.Add(mediaPath);
        if (!ffprobe.Start())
        {
            throw new InvalidOperationException("Unable to start the packaged ffprobe executable.");
        }
        string stdout = await ffprobe.StandardOutput.ReadToEndAsync();
        string stderr = await ffprobe.StandardError.ReadToEndAsync();
        await ffprobe.WaitForExitAsync();
        using JsonDocument probe = JsonDocument.Parse(stdout);
        JsonElement streams = probe.RootElement.GetProperty("streams");
        string[] codecNames = streams.EnumerateArray()
            .Select(stream => stream.GetProperty("codec_name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
        bool expectedCodecsIdentified = ffprobe.ExitCode == 0 &&
            codecNames.Contains("hevc", StringComparer.Ordinal) &&
            codecNames.Contains("aac", StringComparer.Ordinal);

        using Process ffmpeg = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        foreach (string argument in new[]
        {
            "-v", "error", "-i", mediaPath, "-map", "0:v:0", "-map", "0:a:0",
            "-t", "0.5", "-f", "null", "NUL"
        })
        {
            ffmpeg.StartInfo.ArgumentList.Add(argument);
        }
        if (!ffmpeg.Start())
        {
            throw new InvalidOperationException("Unable to start the packaged ffmpeg executable.");
        }
        Task<string> ffmpegStdoutTask = ffmpeg.StandardOutput.ReadToEndAsync();
        Task<string> ffmpegStderrTask = ffmpeg.StandardError.ReadToEndAsync();
        await ffmpeg.WaitForExitAsync();
        string ffmpegStdout = await ffmpegStdoutTask;
        string ffmpegStderr = await ffmpegStderrTask;
        bool codecsDecoded = ffmpeg.ExitCode == 0;

        await using FileStream modelStream = File.OpenRead(modelPath);
        string actualModelHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(modelStream));
        Dictionary<string, object?> result = new()
        {
            ["ffprobeExitCode"] = ffprobe.ExitCode,
            ["ffprobeStderrLength"] = stderr.Length,
            ["codecNames"] = codecNames,
            ["expectedCodecsIdentified"] = expectedCodecsIdentified,
            ["ffmpegExitCode"] = ffmpeg.ExitCode,
            ["ffmpegStdoutLength"] = ffmpegStdout.Length,
            ["ffmpegStderrLength"] = ffmpegStderr.Length,
            ["codecsDecoded"] = codecsDecoded,
            ["modelHash"] = actualModelHash,
            ["modelHashMatched"] = string.Equals(actualModelHash, expectedModelHash, StringComparison.Ordinal),
            ["passed"] = expectedCodecsIdentified && codecsDecoded &&
                string.Equals(actualModelHash, expectedModelHash, StringComparison.Ordinal),
        };
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return (bool)result["passed"]! ? 0 : 1;
    }

    private static Process StartProbe(params string[] arguments)
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start a probe child process.");
        }
        return process;
    }

    private static SafeJobHandle CreateConfiguredJob(
        uint limitFlags,
        uint activeProcessLimit = 0,
        nuint processMemoryLimit = 0)
    {
        SafeJobHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }
        JobObjectExtendedLimitInformation information = new();
        information.BasicLimitInformation.LimitFlags = limitFlags;
        information.BasicLimitInformation.ActiveProcessLimit = activeProcessLimit;
        information.ProcessMemoryLimit = processMemoryLimit;
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref information, (uint)size))
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
        return job;
    }

    private static void AssignProcess(SafeJobHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(10000))
                {
                    throw new InvalidOperationException("A process rejected by Job assignment could not be terminated.");
                }
            }
            throw new Win32Exception(error, "AssignProcessToJobObject failed.");
        }
    }

    private static bool IsProcessInSpecificJob(int processId, SafeJobHandle job)
    {
        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return false;
        }
        if (!IsProcessInJob(process, job, out bool result))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IsProcessInJob failed.");
        }
        return result;
    }

    private static int[] QueryJobProcessIds(SafeJobHandle job)
    {
        int capacity = 16;
        while (capacity <= 1024)
        {
            const int headerSize = sizeof(uint) * 2;
            int pointerSize = IntPtr.Size;
            int size = headerSize + (capacity * pointerSize);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    uint assigned = unchecked((uint)Marshal.ReadInt32(buffer));
                    uint count = unchecked((uint)Marshal.ReadInt32(buffer + sizeof(uint)));
                    if (count <= capacity && assigned >= count)
                    {
                        int[] processIds = new int[count];
                        for (int index = 0; index < processIds.Length; index++)
                        {
                            processIds[index] = checked((int)ReadNativeUInt(buffer + headerSize + (index * pointerSize)));
                        }
                        return processIds;
                    }
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 122)
                {
                    throw new Win32Exception(error, "QueryInformationJobObject process list failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            capacity *= 2;
        }
        throw new InvalidOperationException("Job process list exceeded the probe limit.");
    }

    private static JobObjectExtendedLimitInformation QueryExtendedLimits(SafeJobHandle job)
    {
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject extended limits failed.");
            }
            return Marshal.PtrToStructure<JobObjectExtendedLimitInformation>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static JobObjectBasicAccountingInformation QueryBasicAccounting(SafeJobHandle job)
    {
        int size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformationClass, buffer, (uint)size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject basic accounting failed.");
            }
            return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static async Task<(JobObjectBasicAccountingInformation Accounting, int[] ProcessIds, bool Converged)>
        WaitForJobActiveProcessCountAsync(SafeJobHandle job, uint expectedActiveProcessCount, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        JobObjectBasicAccountingInformation accounting;
        int[] processIds;
        do
        {
            accounting = QueryBasicAccounting(job);
            processIds = QueryJobProcessIds(job);
            if (accounting.ActiveProcesses == expectedActiveProcessCount &&
                processIds.Length == checked((int)expectedActiveProcessCount))
            {
                return (accounting, processIds, true);
            }
            await Task.Delay(25);
        }
        while (stopwatch.Elapsed < timeout);

        accounting = QueryBasicAccounting(job);
        processIds = QueryJobProcessIds(job);
        return (accounting, processIds, false);
    }

    private static long? ReadJsonInt64(Dictionary<string, object?>? value, string name)
    {
        if (value is null || !value.TryGetValue(name, out object? raw) || raw is not JsonElement element ||
            element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long result))
        {
            return null;
        }
        return result;
    }

    private static nuint ReadNativeUInt(IntPtr pointer) =>
        IntPtr.Size == 8 ? unchecked((nuint)(ulong)Marshal.ReadInt64(pointer)) : unchecked((nuint)(uint)Marshal.ReadInt32(pointer));

    private static async Task<int> WriteSolidPngAsync(string outputPath, int size)
    {
        if (size <= 0 || size > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using FileStream output = File.Create(outputPath);
        await output.WriteAsync(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        WriteBigEndian(header, 0, (uint)size);
        WriteBigEndian(header, 4, (uint)size);
        header[8] = 8;
        header[9] = 6;
        await WritePngChunkAsync(output, "IHDR", header);
        byte[] scanlines = new byte[size * ((size * 4) + 1)];
        for (int row = 0; row < size; row++)
        {
            int offset = row * ((size * 4) + 1);
            scanlines[offset] = 0;
            for (int column = 0; column < size; column++)
            {
                int pixel = offset + 1 + (column * 4);
                scanlines[pixel] = 32;
                scanlines[pixel + 1] = 91;
                scanlines[pixel + 2] = 166;
                scanlines[pixel + 3] = 255;
            }
        }
        await using MemoryStream compressed = new();
        await using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await zlib.WriteAsync(scanlines);
        }
        await WritePngChunkAsync(output, "IDAT", compressed.ToArray());
        await WritePngChunkAsync(output, "IEND", Array.Empty<byte>());
        return 0;
    }

    private static async Task WritePngChunkAsync(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        byte[] length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        await output.WriteAsync(length);
        await output.WriteAsync(typeBytes);
        await output.WriteAsync(data);
        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        byte[] crc = new byte[4];
        WriteBigEndian(crc, 0, CalculateCrc32(crcInput));
        await output.WriteAsync(crc);
    }

    private static uint CalculateCrc32(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
            }
        }
        return ~crc;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for {Path.GetFileName(path)}.");
            }
            await Task.Delay(25);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task TerminateProcessAsync(Process process, TimeSpan timeout)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        if (!await WaitForExitAsync(process, timeout))
        {
            throw new InvalidOperationException("A probe process could not be terminated within the bounded cleanup interval.");
        }
    }

    private static async Task<bool> WaitForProcessAbsentAsync(int processId, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    private static async Task WriteJsonAtomicallyAsync(string path, Dictionary<string, object?> value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staging = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static async Task WriteTextAtomicallyAsync(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string staging = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(staging, value);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hubradio-process-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RequireArgument(string[] args, int index, string name) =>
        args.Length > index ? args[index] : throw new ArgumentException($"Missing {name}.");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle? job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle? job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
