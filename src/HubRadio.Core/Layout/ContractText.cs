using System.Globalization;
using System.Text;

namespace HubRadio.Core.Layout;

internal static class ContractText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string RequireNonBlank(
        string? value,
        string parameterName,
        string description)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }

    public static string RequireNonBlankStrictUtf8Substantive(
        string? value,
        string parameterName,
        string description)
    {
        string validated = RequireNonBlank(value, parameterName, description);

        try
        {
            _ = StrictUtf8.GetByteCount(validated);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{description} must contain valid Unicode that can be encoded as strict UTF-8.",
                parameterName,
                exception);
        }

        if (!ContainsSubstantiveUnicodeScalar(validated))
        {
            throw new ArgumentException(
                $"{description} must contain at least one visible, substantive Unicode scalar.",
                parameterName);
        }

        return validated;
    }

    private static bool ContainsSubstantiveUnicodeScalar(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (!Rune.IsWhiteSpace(rune)
                && !IsNonSubstantiveSpecialScalar(rune)
                && category is not (UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonSubstantiveSpecialScalar(Rune rune)
    {
        return rune.Value switch
        {
            0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0 => true,
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xE0100 and <= 0xE01EF => true,
            _ => false,
        };
    }
}