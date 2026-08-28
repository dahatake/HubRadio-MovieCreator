using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;

namespace HubRadio.Core.Serialization;

public static class CanonicalJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] CanonicalizeToUtf8(ReadOnlySpan<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(
            utf8Json.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

        return Write(document.RootElement);
    }

    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        byte[] canonical = CanonicalizeToUtf8(StrictUtf8.GetBytes(json));
        return StrictUtf8.GetString(canonical);
    }

    public static byte[] Write(JsonElement value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });

        WriteValue(writer, value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static ContentDigest ComputeDigest(ReadOnlySpan<byte> utf8Json)
    {
        return ContentDigest.Compute(CanonicalizeToUtf8(utf8Json));
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind '{value.ValueKind}'.");
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement value)
    {
        List<JsonProperty> properties = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException(
                    $"Duplicate JSON property name '{property.Name}' is not canonicalizable.");
            }

            properties.Add(property);
        }

        properties.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        writer.WriteStartObject();
        foreach (JsonProperty property in properties)
        {
            writer.WritePropertyName(property.Name);
            WriteValue(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string rawNumber)
    {
        int exponentIndex = rawNumber.IndexOfAny('e', 'E');
        string significand = exponentIndex < 0
            ? rawNumber
            : rawNumber[..exponentIndex];
        BigInteger exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(
                rawNumber.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);

        bool negative = significand[0] == '-';
        int significandStart = negative ? 1 : 0;
        int decimalPoint = significand.IndexOf('.', significandStart);
        int fractionalDigits = decimalPoint < 0
            ? 0
            : significand.Length - decimalPoint - 1;

        string digits = decimalPoint < 0
            ? significand[significandStart..]
            : string.Concat(
                significand.AsSpan(significandStart, decimalPoint - significandStart),
                significand.AsSpan(decimalPoint + 1));

        int firstNonZero = 0;
        while (firstNonZero < digits.Length && digits[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        if (firstNonZero == digits.Length)
        {
            writer.WriteNumberValue(0);
            return;
        }

        int lastNonZero = digits.Length - 1;
        while (digits[lastNonZero] == '0')
        {
            lastNonZero--;
        }

        int removedTrailingZeros = digits.Length - lastNonZero - 1;
        string coefficient = digits[firstNonZero..(lastNonZero + 1)];
        BigInteger power = exponent - fractionalDigits + removedTrailingZeros;
        string canonical = string.Concat(
            negative ? "-" : string.Empty,
            coefficient,
            power.IsZero
                ? string.Empty
                : string.Concat("e", power.ToString(CultureInfo.InvariantCulture)));

        writer.WriteRawValue(canonical, skipInputValidation: false);
    }
}