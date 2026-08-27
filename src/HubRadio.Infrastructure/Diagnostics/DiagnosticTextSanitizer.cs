using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace HubRadio.Infrastructure.Diagnostics;

internal static class DiagnosticTextSanitizer
{
    private static readonly Regex AuthorizationPattern = CreateRegex(
        """\b(?:authorization|proxy-authorization)\s*[:=]\s*(?:(?:bearer|basic)\s+)?(?:"[^"]*"|'[^']*'|[^\s,;]+)""");
    private static readonly Regex BearerPattern = CreateRegex(
        """\bbearer\s+(?:"[^"]*"|'[^']*'|[A-Za-z0-9._~+/=-]{4,})""");
    private static readonly Regex SecretAssignmentPattern = CreateRegex(
        """\b(?:x-api-key|api[_ -]?key|subscription[_ -]?key|accountkey|sharedaccesskey|sharedaccesssignature|client[_ -]?secret|password|pwd|access[_ -]?token|connection[_ -]?string)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)""");
    private static readonly Regex WindowsPathPattern = CreateRegex(
        """(?:(?:\\\\\?\\)?[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/][^\\/\s]+(?:[\\/])?|%(?:USERPROFILE|HOMEPATH)%[\\/]|[\\/](?:users|workspace|github)[\\/])[^\r\n]*""");

    internal static SanitizedDiagnosticText SanitizeExceptionType(
        string value,
        int maxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > maxUtf8Bytes * 4)
        {
            return new SanitizedDiagnosticText(
                "RedactedException",
                WasRedacted: true,
                WasTruncated: true);
        }

        bool redacted = false;
        string sanitized = ReplaceControls(value, ref redacted);
        try
        {
            sanitized = Replace(AuthorizationPattern, sanitized, "RedactedSecret", ref redacted);
            sanitized = Replace(BearerPattern, sanitized, "RedactedSecret", ref redacted);
            sanitized = Replace(SecretAssignmentPattern, sanitized, "RedactedSecret", ref redacted);
            sanitized = Replace(WindowsPathPattern, sanitized, "RedactedPath", ref redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            sanitized = "RedactedException";
            redacted = true;
        }

        string allowlisted = AllowlistExceptionTypeCharacters(sanitized, ref redacted);
        string bounded = TruncateUtf8(allowlisted, maxUtf8Bytes, out bool truncated);
        return new SanitizedDiagnosticText(bounded, redacted, truncated);
    }

    internal static bool IsSafeExceptionType(string value, int maxUtf8Bytes)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            return false;
        }

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!IsAllowedExceptionTypeRune(rune))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReplaceControls(string value, ref bool redacted)
    {
        StringBuilder builder = new(value.Length);
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out Rune rune,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                builder.Append('_');
                redacted = true;
                remaining = remaining[Math.Max(consumed, 1)..];
                continue;
            }

            if (Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029)
            {
                builder.Append(' ');
                redacted = true;
            }
            else
            {
                builder.Append(rune.ToString());
            }

            remaining = remaining[consumed..];
        }

        return builder.ToString();
    }

    private static string Replace(
        Regex pattern,
        string value,
        string replacement,
        ref bool redacted)
    {
        if (!pattern.IsMatch(value))
        {
            return value;
        }

        redacted = true;
        return pattern.Replace(value, replacement);
    }

    private static string AllowlistExceptionTypeCharacters(
        string value,
        ref bool redacted)
    {
        StringBuilder builder = new(value.Length);
        bool previousWasReplacement = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (IsAllowedExceptionTypeRune(rune))
            {
                builder.Append(rune.ToString());
                previousWasReplacement = false;
                continue;
            }

            redacted = true;
            if (!previousWasReplacement)
            {
                builder.Append('_');
                previousWasReplacement = true;
            }
        }

        string result = builder.ToString().Trim('_');
        return result.Length == 0 ? "Exception" : result;
    }

    private static bool IsAllowedExceptionTypeRune(Rune rune)
    {
        int value = rune.Value;
        return (value >= 'A' && value <= 'Z') ||
            (value >= 'a' && value <= 'z') ||
            (value >= '0' && value <= '9') ||
            value is '.' or '_' or '+' or '`';
    }

    private static string TruncateUtf8(
        string value,
        int maxUtf8Bytes,
        out bool truncated)
    {
        StringBuilder builder = new(value.Length);
        int usedBytes = 0;
        truncated = false;

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > maxUtf8Bytes)
            {
                truncated = true;
                break;
            }

            builder.Append(rune.ToString());
            usedBytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private static Regex CreateRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
}

internal readonly record struct SanitizedDiagnosticText(
    string Value,
    bool WasRedacted,
    bool WasTruncated);