using System.Reflection;
using HubRadio.Infrastructure.Diagnostics;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class DiagnosticErrorCodeTests
{
    [TestMethod]
    public void StableErrorCodes_DefineOnlyCurrentProcessAndDiagnosticFailures()
    {
        string[] expected =
        [
            "diagnostic.log_write_failed",
            "process.start_failed",
        ];

        CollectionAssert.AreEqual(expected, StableErrorCodes.DefinedCodes.ToArray());
        Assert.AreEqual(
            StableErrorCodes.ProcessStartFailed,
            ReadStringConstant(
                typeof(ProcessStartException),
                nameof(ProcessStartException.StableErrorCode)));
        Assert.AreEqual(
            StableErrorCodes.DiagnosticLogWriteFailed,
            ReadStringConstant(
                typeof(DiagnosticLogException),
                nameof(DiagnosticLogException.StableErrorCode)));

        foreach (string code in expected)
        {
            Assert.IsTrue(StableErrorCodes.IsCanonical(code));
            Assert.IsTrue(StableErrorCodes.IsDefined(code));
            Assert.AreEqual(code, StableErrorCodes.Validate(code));
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("process")]
    [DataRow("Process.start_failed")]
    [DataRow("process..start_failed")]
    [DataRow("process._start_failed")]
    [DataRow("process.start__failed")]
    [DataRow("process.start_failed_")]
    [DataRow(" process.start_failed")]
    [DataRow("process.start-failed")]
    [DataRow("process.start_failed\r\nforged")]
    public void Validate_NonCanonicalCode_IsRejected(string code)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => StableErrorCodes.Validate(code));

        Assert.AreEqual("code", exception.ParamName);
        Assert.IsFalse(StableErrorCodes.IsCanonical(code));
    }

    [TestMethod]
    public void Validate_NullOrUndefinedCode_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => StableErrorCodes.Validate(null!));

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => StableErrorCodes.Validate("future.not_defined"));

        Assert.AreEqual("code", exception.ParamName);
        Assert.IsTrue(StableErrorCodes.IsCanonical("future.not_defined"));
        Assert.IsFalse(StableErrorCodes.IsDefined("future.not_defined"));
    }

    [TestMethod]
    public void DiagnosticLogEntry_InvalidStructuredInput_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DiagnosticLogEntry(
                null!,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                (DiagnosticComponent)999,
                DiagnosticOperation.Start));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                (DiagnosticOperation)999));
        ArgumentException mismatch = Assert.ThrowsExactly<ArgumentException>(
            () => new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.DiagnosticLog,
                DiagnosticOperation.Write));
        Assert.AreEqual("code", mismatch.ParamName);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                (DiagnosticSeverity)999));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DiagnosticLogEntry(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                durationMilliseconds: -1));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => DiagnosticLogEntry.FromException(
                StableErrorCodes.ProcessStartFailed,
                DiagnosticComponent.Process,
                DiagnosticOperation.Start,
                null!));
    }

    [TestMethod]
    public void DiagnosticLogOptions_InvalidPathOrLimit_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DiagnosticLogOptions(null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => new DiagnosticLogOptions("relative.jsonl"));
        Assert.ThrowsExactly<ArgumentException>(
            () => new DiagnosticLogOptions($"{Path.GetTempPath()}\r\nforged.jsonl"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DiagnosticLogOptions(
                Path.Combine(Path.GetTempPath(), "diagnostic.jsonl"),
                DiagnosticLogOptions.MinimumMaxTotalBytes - 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DiagnosticLogOptions(
                Path.Combine(Path.GetTempPath(), "diagnostic.jsonl"),
                DiagnosticLogOptions.MaximumMaxTotalBytes + 1));
    }

    [TestMethod]
    public void DiagnosticLogOptions_WindowsRemoteDeviceAndAlternateStreamPaths_AreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows path namespaces are required for this test.");
        }

        string ordinaryPath = Path.Combine(Path.GetTempPath(), "diagnostic.jsonl");
        string[] invalidPaths =
        [
            @"\\server\share\diagnostic.jsonl",
            @"\\?\UNC\server\share\diagnostic.jsonl",
            @"\\.\pipe\hubradio-diagnostic",
            @"\\?\PIPE\hubradio-diagnostic",
            ordinaryPath + ":hidden",
        ];

        foreach (string invalidPath in invalidPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new DiagnosticLogOptions(invalidPath));

            Assert.AreEqual("filePath", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(invalidPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void DiagnosticLogOptions_ExistingReparseDirectory_IsRejectedWhenSupported()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hubradio-diagnostic-options-{Guid.NewGuid():N}");
        string target = Path.Combine(root, "target");
        string link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Inconclusive("Directory symbolic links are unavailable in this environment.");
            }

            ArgumentException rejected = Assert.ThrowsExactly<ArgumentException>(
                () => new DiagnosticLogOptions(Path.Combine(link, "diagnostic.jsonl")));
            Assert.AreEqual("filePath", rejected.ParamName);
            Assert.IsFalse(rejected.Message.Contains(link, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadStringConstant(Type type, string fieldName)
    {
        FieldInfo field = type.GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("The stable error-code field was not found.");
        return field.GetRawConstantValue() as string
            ?? throw new InvalidOperationException("The stable error-code field was not a string constant.");
    }
}