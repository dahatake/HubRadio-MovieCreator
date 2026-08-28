using System.Text.Json;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Diagnostics;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Worker;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class WorkerClientTests
{
    private const string RequestId = "123e4567-e89b-12d3-a456-426614174000";
    private const string OtherRequestId = "123e4567-e89b-12d3-a456-426614174001";

    [TestMethod]
    public void ParsePingResult_ValidProgressAndResponseReturnImmutableSnapshot()
    {
        ProcessRunResult processResult = Result(
            Progress(1, 0, 1) +
            Progress(2, 1, 1) +
            Response());

        WorkerPingResult result = WorkerProtocolParser.ParsePingResult(processResult, RequestId);

        Assert.AreEqual(RequestId, result.RequestId);
        Assert.AreEqual(1, result.ProtocolVersion);
        Assert.HasCount(2, result.Progress);
        Assert.AreEqual(new WorkerProgressSnapshot(1, 0, 1, "worker.ping"), result.Progress[0]);
        Assert.AreEqual(new WorkerProgressSnapshot(2, 1, 1, "worker.ping"), result.Progress[1]);
        IList<WorkerProgressSnapshot> exposed = (IList<WorkerProgressSnapshot>)result.Progress;
        Assert.ThrowsExactly<NotSupportedException>(
            () => exposed.Add(new WorkerProgressSnapshot(3, 1, 1, "worker.ping")));
    }

    [TestMethod]
    public void ParsePingResult_ZeroProgressThenResponseIsValid()
    {
        WorkerPingResult result = WorkerProtocolParser.ParsePingResult(Result(Response()), RequestId);

        Assert.HasCount(0, result.Progress);
        Assert.AreEqual(1, result.ProtocolVersion);
    }

    [TestMethod]
    [DataRow("worker.invalid_message")]
    [DataRow("worker.message_too_large")]
    [DataRow("worker.unknown_method")]
    [DataRow("worker.processing_failed")]
    public void ParsePingResult_NonzeroValidErrorExposesOnlyStableCode(string code)
    {
        WorkerProtocolException exception = Assert.ThrowsExactly<WorkerProtocolException>(
            () => WorkerProtocolParser.ParsePingResult(Result(Error(code), exitCode: 1), RequestId));

        Assert.AreEqual(code, exception.Code);
        Assert.AreEqual("The worker protocol operation failed.", exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(RequestId, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParsePingResult_ExitCodeMustAgreeWithFinalEnvelope()
    {
        AssertInvalid(Result(Response(), exitCode: 9));
        AssertInvalid(Result(Error(StableErrorCodes.WorkerUnknownMethod), exitCode: 0));
    }

    [TestMethod]
    public void ParsePingResult_TransportFramingAndCaptureViolationsFailClosed()
    {
        string response = Response();
        ProcessRunResult[] invalidResults =
        [
            Result(response, truncated: true),
            Result(response, standardError: "private stderr payload"),
            Result(response[..^1]),
            Result(response[..^1] + "\r\n"),
            Result(response[..^1] + "\uFFFD\n"),
            Result(string.Empty),
            Result("\n"),
            Result(response + "\n"),
            Result(new string(' ', WorkerProtocolParser.MaxProtocolOutputUtf8Bytes) + "\n"),
        ];

        foreach (ProcessRunResult invalid in invalidResults)
        {
            WorkerProtocolException exception = AssertInvalid(invalid);
            Assert.IsFalse(
                exception.ToString().Contains("private stderr payload", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ParsePingResult_MalformedDuplicateAndNoncanonicalJsonFailClosed()
    {
        string[] invalidOutputs =
        [
            "{\n",
            $"{{\"version\":1,\"type\":\"response\",\"requestId\":\"{RequestId}\",\"method\":\"worker.ping\",\"result\":{{\"protocolVersion\":1}}}}\n",
            $"{{\"method\":\"worker.ping\",\"method\":\"worker.ping\",\"requestId\":\"{RequestId}\",\"result\":{{\"protocolVersion\":1}},\"type\":\"response\",\"version\":1}}\n",
            $"{{\"method\":\"worker.ping\",\"requestId\":\"{RequestId}\",\"result\":{{\"protocolVersion\":1,\"protocolVersion\":1}},\"type\":\"response\",\"version\":1}}\n",
            $"{{\"method\":\"worker.ping\",\"requestId\":\"{RequestId}\",\"result\":{{\"protocolVersion\":1}},\"type\":\"response\",\"version\":1.0}}\n",
            " " + Response(),
        ];

        foreach (string invalid in invalidOutputs)
        {
            AssertInvalid(Result(invalid));
        }
    }

    [TestMethod]
    public void ParsePingResult_WrongResponseAndErrorShapesFailClosed()
    {
        (string Output, int ExitCode)[] invalid =
        [
            (Line(new { method = "worker.ping", requestId = RequestId, result = new { protocolVersion = 1 }, type = "response", version = 2 }), 0),
            (Line(new { method = "worker.ping", requestId = RequestId, result = new { protocolVersion = 1 }, type = true, version = 1 }), 0),
            (Line(new { method = "worker.ping", requestId = OtherRequestId, result = new { protocolVersion = 1 }, type = "response", version = 1 }), 0),
            (Line(new { method = "worker.future", requestId = RequestId, result = new { protocolVersion = 1 }, type = "response", version = 1 }), 0),
            (Line(new { requestId = RequestId, result = new { protocolVersion = 1 }, type = "response", version = 1 }), 0),
            (Line(new { extra = 1, method = "worker.ping", requestId = RequestId, result = new { protocolVersion = 1 }, type = "response", version = 1 }), 0),
            (Line(new { method = "worker.ping", requestId = RequestId, result = new { protocolVersion = 2 }, type = "response", version = 1 }), 0),
            (Line(new { method = "worker.ping", requestId = RequestId, result = new { protocolVersion = true }, type = "response", version = 1 }), 0),
            (Line(new { method = "worker.ping", requestId = RequestId, result = new { extra = 1, protocolVersion = 1 }, type = "response", version = 1 }), 0),
            (Line(Array.Empty<object>()), 0),
            (Line(new { error = new { code = "worker.future" }, requestId = RequestId, type = "error", version = 1 }), 1),
            (Line(new { error = new { code = "worker.invalid_message", details = true }, requestId = RequestId, type = "error", version = 1 }), 1),
            (Line(new { error = new { code = "worker.invalid_message" }, method = "worker.ping", requestId = RequestId, type = "error", version = 1 }), 1),
        ];

        foreach ((string output, int exitCode) in invalid)
        {
            AssertInvalid(Result(output, exitCode));
        }
    }

    [TestMethod]
    public void ParsePingResult_ProgressSequenceOrderingAndCrossFieldViolationsFailClosed()
    {
        string[] invalid =
        [
            Progress(2, 0, 1) + Response(),
            Progress(1, 0, 1) + Progress(3, 1, 1) + Response(),
            Progress(1, 0, 1) + Progress(1, 1, 1) + Response(),
            Progress(1, -1, 1) + Response(),
            Progress(1, 0, 0) + Response(),
            Progress(1, 2, 1) + Response(),
            Progress(1, 0, 2) + Progress(2, 1, 3) + Response(),
            Progress(1, 1, 2) + Progress(2, 0, 2) + Response(),
            Progress(1, 0, 1, phase: "worker.future") + Response(),
            Progress(1, 0, 1, requestId: OtherRequestId) + Response(),
            Line(new { completedUnits = 0, extra = 1, phase = "worker.ping", requestId = RequestId, sequence = 1, totalUnits = 1, type = "progress", version = 1 }) + Response(),
            Line(new { completedUnits = 0, phase = "worker.ping", requestId = RequestId, sequence = true, totalUnits = 1, type = "progress", version = 1 }) + Response(),
            Response() + Progress(1, 0, 1),
            Progress(1, 0, 1),
            Response() + Response(),
            Progress(1, 0, 1) + "\n" + Response(),
        ];

        foreach (string output in invalid)
        {
            AssertInvalid(Result(output));
        }
    }

    [TestMethod]
    public void ParsePingResult_RecordOver512Utf8BytesFailsClosed()
    {
        string oversized = Line(
            new
            {
                method = "worker.ping",
                padding = new string('あ', 171),
                requestId = RequestId,
                result = new { protocolVersion = 1 },
                type = "response",
                version = 1,
            });
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(oversized[..^1]) > 512);

        AssertInvalid(Result(oversized));
    }

    [TestMethod]
    public void ParsePingResult_InvalidExpectedRequestIdIsAProgrammingError()
    {
        foreach (string invalid in new[]
        {
            "00000000-0000-0000-0000-000000000000",
            "123E4567-E89B-12D3-A456-426614174000",
            "not-a-uuid",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => WorkerProtocolParser.ParsePingResult(Result(Response()), invalid));
            Assert.AreEqual("expectedRequestId", exception.ParamName);
        }

        Assert.ThrowsExactly<ArgumentNullException>(
            () => WorkerProtocolParser.ParsePingResult(Result(Response()), null!));
    }

    [TestMethod]
    public void WorkerClient_ConstructorRequiresRunnerAbsoluteExecutableAndExistingWorkingDirectory()
    {
        string repositoryRoot = GetRepositoryRoot();
        string workerRoot = Path.Combine(repositoryRoot, "src", "HubRadio.Worker");
        string absolutePython = Path.Combine(workerRoot, ".venv", "Scripts", "python.exe");

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new WorkerClient(null!, absolutePython, workerRoot));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new WorkerClient(new ProcessRunner(), null!, workerRoot));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new WorkerClient(new ProcessRunner(), absolutePython, null!));
        Assert.AreEqual(
            "pythonExecutable",
            Assert.ThrowsExactly<ArgumentException>(
                () => new WorkerClient(new ProcessRunner(), "python.exe", workerRoot)).ParamName);
        Assert.AreEqual(
            "workerWorkingDirectory",
            Assert.ThrowsExactly<ArgumentException>(
                () => new WorkerClient(
                    new ProcessRunner(),
                    absolutePython,
                    Path.Combine(workerRoot, "missing-directory"))).ParamName);
    }

    [TestMethod]
    public async Task PingAsync_RealRepositoryPythonWorkerCompletesEndToEnd()
    {
        string repositoryRoot = GetRepositoryRoot();
        string workerRoot = Path.Combine(repositoryRoot, "src", "HubRadio.Worker");
        string python = Path.Combine(workerRoot, ".venv", "Scripts", "python.exe");
        Assert.IsTrue(File.Exists(python), "The repository worker virtual environment must exist for integration tests.");
        WorkerClient client = new(new ProcessRunner(), python, workerRoot);

        WorkerPingResult result = await client.PingAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.IsTrue(Guid.TryParseExact(result.RequestId, "D", out Guid requestId));
        Assert.AreNotEqual(Guid.Empty, requestId);
        Assert.AreEqual(result.RequestId, requestId.ToString("D"));
        Assert.AreEqual(1, result.ProtocolVersion);
        CollectionAssert.AreEqual(
            new[]
            {
                new WorkerProgressSnapshot(1, 0, 1, "worker.ping"),
                new WorkerProgressSnapshot(2, 1, 1, "worker.ping"),
            },
            result.Progress.ToArray());
    }

    private static WorkerProtocolException AssertInvalid(ProcessRunResult result)
    {
        WorkerProtocolException exception = Assert.ThrowsExactly<WorkerProtocolException>(
            () => WorkerProtocolParser.ParsePingResult(result, RequestId));
        Assert.AreEqual(StableErrorCodes.WorkerInvalidMessage, exception.Code);
        Assert.AreEqual("The worker protocol operation failed.", exception.Message);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static ProcessRunResult Result(
        string standardOutput,
        int exitCode = 0,
        string standardError = "",
        bool truncated = false) =>
        new(exitCode, standardOutput, standardError, truncated, TimeSpan.Zero);

    private static string Progress(
        int sequence,
        int completedUnits,
        int totalUnits,
        string phase = "worker.ping",
        string requestId = RequestId) =>
        Line(
            new
            {
                completedUnits,
                phase,
                requestId,
                sequence,
                totalUnits,
                type = "progress",
                version = 1,
            });

    private static string Response() =>
        Line(
            new
            {
                method = "worker.ping",
                requestId = RequestId,
                result = new { protocolVersion = 1 },
                type = "response",
                version = 1,
            });

    private static string Error(string code) =>
        Line(
            new
            {
                error = new { code },
                requestId = RequestId,
                type = "error",
                version = 1,
            });

    private static string Line<T>(T value)
    {
        string serialized = JsonSerializer.Serialize(value);
        return CanonicalJson.Canonicalize(serialized) + "\n";
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}