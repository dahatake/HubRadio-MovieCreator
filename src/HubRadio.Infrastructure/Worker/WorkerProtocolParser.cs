using System.Text;
using System.Text.Json;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Diagnostics;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Worker;

public static class WorkerProtocolParser
{
    public const int ProtocolVersion = 1;
    public const int MaxMessageUtf8Bytes = 512;
    public const int MaxProtocolOutputUtf8Bytes = 4096;
    public const string PingMethod = "worker.ping";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static WorkerPingResult ParsePingResult(
        ProcessRunResult processResult,
        string expectedRequestId)
    {
        ArgumentNullException.ThrowIfNull(processResult);
        ValidateExpectedRequestId(expectedRequestId);

        try
        {
            return ParseCore(processResult, expectedRequestId);
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or EncoderFallbackException or FormatException or OverflowException)
        {
            throw InvalidMessage();
        }
    }

    private static WorkerPingResult ParseCore(
        ProcessRunResult processResult,
        string expectedRequestId)
    {
        if (processResult.Truncated ||
            processResult.StandardOutput is null ||
            processResult.StandardError is null ||
            processResult.StandardError.Length != 0 ||
            processResult.StandardOutput.Length == 0 ||
            processResult.StandardOutput.Contains('\r', StringComparison.Ordinal) ||
            processResult.StandardOutput.Contains('\uFFFD', StringComparison.Ordinal) ||
            !processResult.StandardOutput.EndsWith('\n'))
        {
            throw InvalidMessage();
        }

        byte[] aggregateBytes = StrictUtf8.GetBytes(processResult.StandardOutput);
        if (aggregateBytes.Length > MaxProtocolOutputUtf8Bytes)
        {
            throw InvalidMessage();
        }

        string withoutFinalLf = processResult.StandardOutput[..^1];
        string[] lines = withoutFinalLf.Split('\n', StringSplitOptions.None);
        if (lines.Length == 0 || lines.Any(static line => line.Length == 0))
        {
            throw InvalidMessage();
        }

        List<WorkerProgressSnapshot> progress = [];
        int expectedSequence = 1;
        int? expectedTotalUnits = null;
        int previousCompletedUnits = 0;
        bool hasPreviousProgress = false;
        bool finalSeen = false;
        string? finalErrorCode = null;

        for (int index = 0; index < lines.Length; index++)
        {
            byte[] lineBytes = StrictUtf8.GetBytes(lines[index]);
            if (lineBytes.Length > MaxMessageUtf8Bytes)
            {
                throw InvalidMessage();
            }

            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(lineBytes);
            if (!lineBytes.AsSpan().SequenceEqual(canonical))
            {
                throw InvalidMessage();
            }

            using JsonDocument document = JsonDocument.Parse(lineBytes);
            JsonElement message = document.RootElement;
            if (!TryGetRequiredString(message, "type", out string? type))
            {
                throw InvalidMessage();
            }

            switch (type)
            {
                case "progress":
                    if (finalSeen ||
                        !TryParseProgress(
                            message,
                            expectedRequestId,
                            expectedSequence,
                            expectedTotalUnits,
                            previousCompletedUnits,
                            hasPreviousProgress,
                            out WorkerProgressSnapshot snapshot))
                    {
                        throw InvalidMessage();
                    }

                    progress.Add(snapshot);
                    expectedSequence++;
                    expectedTotalUnits ??= snapshot.TotalUnits;
                    previousCompletedUnits = snapshot.CompletedUnits;
                    hasPreviousProgress = true;
                    break;

                case "response":
                    if (finalSeen || index != lines.Length - 1 ||
                        !IsValidResponse(message, expectedRequestId))
                    {
                        throw InvalidMessage();
                    }

                    finalSeen = true;
                    break;

                case "error":
                    if (finalSeen || index != lines.Length - 1 ||
                        !TryParseError(message, expectedRequestId, out finalErrorCode))
                    {
                        throw InvalidMessage();
                    }

                    finalSeen = true;
                    break;

                default:
                    throw InvalidMessage();
            }
        }

        if (!finalSeen)
        {
            throw InvalidMessage();
        }

        if (finalErrorCode is not null)
        {
            if (processResult.ExitCode == 0)
            {
                throw InvalidMessage();
            }

            throw new WorkerProtocolException(finalErrorCode);
        }

        if (processResult.ExitCode != 0)
        {
            throw InvalidMessage();
        }

        return new WorkerPingResult(expectedRequestId, progress, ProtocolVersion);
    }

    private static bool TryParseProgress(
        JsonElement message,
        string expectedRequestId,
        int expectedSequence,
        int? expectedTotalUnits,
        int previousCompletedUnits,
        bool hasPreviousProgress,
        out WorkerProgressSnapshot snapshot)
    {
        snapshot = null!;
        if (!HasExactProperties(
                message,
                "version",
                "type",
                "requestId",
                "sequence",
                "completedUnits",
                "totalUnits",
                "phase") ||
            !HasExactInt32(message, "version", ProtocolVersion) ||
            !HasExactString(message, "type", "progress") ||
            !HasExactString(message, "requestId", expectedRequestId) ||
            !TryGetRequiredInt32(message, "sequence", out int sequence) ||
            !TryGetRequiredInt32(message, "completedUnits", out int completedUnits) ||
            !TryGetRequiredInt32(message, "totalUnits", out int totalUnits) ||
            !HasExactString(message, "phase", PingMethod) ||
            sequence != expectedSequence ||
            sequence < 1 ||
            completedUnits < 0 ||
            totalUnits < 1 ||
            completedUnits > totalUnits ||
            (expectedTotalUnits.HasValue && totalUnits != expectedTotalUnits.Value) ||
            (hasPreviousProgress && completedUnits < previousCompletedUnits))
        {
            return false;
        }

        snapshot = new WorkerProgressSnapshot(
            sequence,
            completedUnits,
            totalUnits,
            PingMethod);
        return true;
    }

    private static bool IsValidResponse(JsonElement message, string expectedRequestId)
    {
        if (!HasExactProperties(
                message,
                "version",
                "type",
                "requestId",
                "method",
                "result") ||
            !HasExactInt32(message, "version", ProtocolVersion) ||
            !HasExactString(message, "type", "response") ||
            !HasExactString(message, "requestId", expectedRequestId) ||
            !HasExactString(message, "method", PingMethod) ||
            !message.TryGetProperty("result", out JsonElement result))
        {
            return false;
        }

        return HasExactProperties(result, "protocolVersion") &&
            HasExactInt32(result, "protocolVersion", ProtocolVersion);
    }

    private static bool TryParseError(
        JsonElement message,
        string expectedRequestId,
        out string? code)
    {
        code = null;
        if (!HasExactProperties(message, "version", "type", "requestId", "error") ||
            !HasExactInt32(message, "version", ProtocolVersion) ||
            !HasExactString(message, "type", "error") ||
            !HasExactString(message, "requestId", expectedRequestId) ||
            !message.TryGetProperty("error", out JsonElement error) ||
            !HasExactProperties(error, "code") ||
            !TryGetRequiredString(error, "code", out string? parsedCode) ||
            parsedCode is null ||
            !IsWorkerErrorCode(parsedCode))
        {
            return false;
        }

        code = parsedCode;
        return true;
    }

    private static bool HasExactProperties(JsonElement value, params string[] expectedNames)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            if (!expectedNames.Contains(property.Name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return count == expectedNames.Length;
    }

    private static bool HasExactInt32(JsonElement value, string name, int expected) =>
        TryGetRequiredInt32(value, name, out int actual) && actual == expected;

    private static bool TryGetRequiredInt32(
        JsonElement value,
        string name,
        out int result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out result);
    }

    private static bool HasExactString(JsonElement value, string name, string expected) =>
        TryGetRequiredString(value, name, out string? actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TryGetRequiredString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return result is not null;
    }

    private static bool IsWorkerErrorCode(string code) =>
        string.Equals(code, StableErrorCodes.WorkerInvalidMessage, StringComparison.Ordinal) ||
        string.Equals(code, StableErrorCodes.WorkerMessageTooLarge, StringComparison.Ordinal) ||
        string.Equals(code, StableErrorCodes.WorkerUnknownMethod, StringComparison.Ordinal) ||
        string.Equals(code, StableErrorCodes.WorkerProcessingFailed, StringComparison.Ordinal);

    private static void ValidateExpectedRequestId(string expectedRequestId)
    {
        ArgumentNullException.ThrowIfNull(expectedRequestId);
        if (!Guid.TryParseExact(expectedRequestId, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(parsed.ToString("D"), expectedRequestId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The expected request identifier must be a lowercase nonzero UUID.",
                nameof(expectedRequestId));
        }
    }

    private static WorkerProtocolException InvalidMessage() =>
        new(StableErrorCodes.WorkerInvalidMessage);
}