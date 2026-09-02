using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Settings;

internal static class DefaultSettingsJsonCodec
{
    internal const string ProfileSchemaVersion = "1.0.0";
    internal const string SnapshotSchemaVersion = "1.0.0";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] ProfileProperties =
        ["schemaVersion", "id", "name", "version", "contentDigest", "settings"];

    private static readonly string[] DefinitionProperties =
    [
        "key",
        "group",
        "isAdvanced",
        "displayName",
        "description",
        "unit",
        "defaultValue",
        "minimum",
        "maximum",
        "allowedTextValues",
        "evidence",
    ];

    private static readonly string[] EvidenceProperties = ["kind", "sourceId"];
    private static readonly string[] SnapshotProperties =
        ["schemaVersion", "profileId", "profileVersion", "profileDigest", "contentDigest", "values"];
    private static readonly string[] SelectionProperties = ["key", "value"];

    internal static string SerializeProfile(DefaultSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return StrictUtf8.GetString(WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", ProfileSchemaVersion);
            writer.WriteString("id", profile.Id.ToString());
            writer.WriteString("name", profile.Name);
            writer.WriteString("version", profile.Version.ToString());
            writer.WriteString("contentDigest", profile.ContentDigest.Hex);
            WriteDefinitions(writer, profile.Definitions);
            writer.WriteEndObject();
        }));
    }

    internal static byte[] SerializeProfileDigestPayload(
        string name,
        SettingsProfileVersion version,
        IReadOnlyList<DefaultSettingDefinition> definitions)
    {
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", ProfileSchemaVersion);
            writer.WriteString("name", name);
            writer.WriteString("version", version.ToString());
            WriteDefinitions(writer, definitions);
            writer.WriteEndObject();
        });
    }

    internal static DefaultSettingsProfile ParseProfileCanonical(string canonicalJson)
    {
        using JsonDocument document = ParseCanonical(canonicalJson, "default-settings profile");
        JsonElement root = document.RootElement;
        RequireExactObject(root, "profile", ProfileProperties);
        RequireSchemaVersion(root, "profile", ProfileSchemaVersion);
        EntityId<DefaultSettingsProfileIdTag> id = ReadEntityId<DefaultSettingsProfileIdTag>(
            root,
            "id",
            "profile");
        string name = ReadString(root, "name", "profile");
        SettingsProfileVersion version = ReadVersion(root, "version", "profile");
        ContentDigest expectedDigest = ReadDigest(root, "contentDigest", "profile");
        DefaultSettingDefinition[] definitions = ReadDefinitions(
            RequireProperty(root, "settings", "profile"));

        try
        {
            DefaultSettingsProfile profile = DefaultSettingsProfile.Create(
                id,
                name,
                version,
                definitions);
            if (profile.ContentDigest != expectedDigest)
            {
                throw new JsonException(
                    "The default-settings profile content digest does not match its canonical content.");
            }

            if (!string.Equals(
                    canonicalJson,
                    SerializeProfile(profile),
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    "The default-settings profile arrays are not in canonical domain order.");
            }

            return profile;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The default-settings profile is invalid.", exception);
        }
    }

    internal static string SerializeSnapshot(DefaultSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return StrictUtf8.GetString(WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SnapshotSchemaVersion);
            writer.WriteString("profileId", snapshot.ProfileId.ToString());
            writer.WriteString("profileVersion", snapshot.ProfileVersion.ToString());
            writer.WriteString("profileDigest", snapshot.ProfileDigest.Hex);
            writer.WriteString("contentDigest", snapshot.ContentDigest.Hex);
            WriteSelections(writer, snapshot.Values);
            writer.WriteEndObject();
        }));
    }

    internal static byte[] SerializeSnapshotDigestPayload(
        EntityId<DefaultSettingsProfileIdTag> profileId,
        SettingsProfileVersion profileVersion,
        ContentDigest profileDigest,
        IReadOnlyList<DefaultSettingSelection> values)
    {
        return WriteCanonical(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SnapshotSchemaVersion);
            writer.WriteString("profileId", profileId.ToString());
            writer.WriteString("profileVersion", profileVersion.ToString());
            writer.WriteString("profileDigest", profileDigest.Hex);
            WriteSelections(writer, values);
            writer.WriteEndObject();
        });
    }

    internal static DefaultSettingsSnapshot ParseSnapshotCanonical(
        string canonicalJson,
        DefaultSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using JsonDocument document = ParseCanonical(canonicalJson, "default-settings snapshot");
        JsonElement root = document.RootElement;
        RequireExactObject(root, "snapshot", SnapshotProperties);
        RequireSchemaVersion(root, "snapshot", SnapshotSchemaVersion);
        EntityId<DefaultSettingsProfileIdTag> profileId =
            ReadEntityId<DefaultSettingsProfileIdTag>(root, "profileId", "snapshot");
        SettingsProfileVersion profileVersion =
            ReadVersion(root, "profileVersion", "snapshot");
        ContentDigest profileDigest = ReadDigest(root, "profileDigest", "snapshot");
        ContentDigest expectedDigest = ReadDigest(root, "contentDigest", "snapshot");
        if (profileId != profile.Id ||
            profileVersion != profile.Version ||
            profileDigest != profile.ContentDigest)
        {
            throw new JsonException(
                "The default-settings snapshot is not bound to the supplied profile.");
        }

        DefaultSettingSelection[] values = ReadSelections(
            RequireProperty(root, "values", "snapshot"));
        try
        {
            DefaultSettingsSnapshot snapshot = DefaultSettingsSnapshot.Create(profile, values);
            if (snapshot.ContentDigest != expectedDigest)
            {
                throw new JsonException(
                    "The default-settings snapshot content digest does not match its canonical content.");
            }

            if (!string.Equals(
                    canonicalJson,
                    SerializeSnapshot(snapshot),
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    "The default-settings snapshot values are not in canonical key order.");
            }

            return snapshot;
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The default-settings snapshot is invalid.", exception);
        }
    }

    private static byte[] WriteCanonical(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            write(writer);
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    private static void WriteDefinitions(
        Utf8JsonWriter writer,
        IReadOnlyList<DefaultSettingDefinition> definitions)
    {
        writer.WriteStartArray("settings");
        foreach (DefaultSettingDefinition definition in definitions)
        {
            writer.WriteStartObject();
            writer.WriteString("key", definition.Key);
            writer.WriteString("group", ToWireName(definition.Group));
            writer.WriteBoolean("isAdvanced", definition.IsAdvanced);
            writer.WriteString("displayName", definition.DisplayName);
            writer.WriteString("description", definition.Description);
            writer.WriteString("unit", ToWireName(definition.Unit));
            writer.WritePropertyName("defaultValue");
            WriteValue(writer, definition.DefaultValue);
            writer.WritePropertyName("minimum");
            WriteOptionalValue(writer, definition.Minimum);
            writer.WritePropertyName("maximum");
            WriteOptionalValue(writer, definition.Maximum);
            writer.WriteStartArray("allowedTextValues");
            foreach (string value in definition.AllowedTextValues)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("evidence");
            foreach (DefaultSettingEvidence evidence in definition.Evidence)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", ToWireName(evidence.Kind));
                writer.WriteString("sourceId", evidence.SourceId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSelections(
        Utf8JsonWriter writer,
        IReadOnlyList<DefaultSettingSelection> values)
    {
        writer.WriteStartArray("values");
        foreach (DefaultSettingSelection selection in values)
        {
            writer.WriteStartObject();
            writer.WriteString("key", selection.Key);
            writer.WritePropertyName("value");
            WriteValue(writer, selection.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalValue(
        Utf8JsonWriter writer,
        DefaultSettingValue? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteValue(writer, value);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, DefaultSettingValue value)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", ToWireName(value.Kind));
        switch (value.Kind)
        {
            case DefaultSettingValueKind.Boolean:
                writer.WriteBoolean("boolean", value.BooleanValue!.Value);
                break;
            case DefaultSettingValueKind.Integer:
                writer.WriteNumber("integer", value.IntegerValue!.Value);
                break;
            case DefaultSettingValueKind.Rational:
                writer.WriteNumber("numerator", value.RationalValue!.Value.Numerator);
                writer.WriteNumber("denominator", value.RationalValue.Value.Denominator);
                break;
            case DefaultSettingValueKind.Text:
                writer.WriteString("text", value.TextValue);
                break;
            case DefaultSettingValueKind.TextList:
                writer.WriteStartArray("values");
                foreach (string item in value.TextListValue)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value.Kind,
                    "Unknown settings value kind.");
        }

        writer.WriteEndObject();
    }

    private static DefaultSettingDefinition[] ReadDefinitions(JsonElement value)
    {
        RequireArray(value, "profile.settings");
        List<DefaultSettingDefinition> definitions = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireExactObject(item, "profile.settings[]", DefinitionProperties);
            string key = ReadString(item, "key", "profile.settings[]");
            DefaultSettingGroup group = ReadGroup(item, "group", "profile.settings[]");
            bool isAdvanced = ReadBoolean(item, "isAdvanced", "profile.settings[]");
            string displayName = ReadString(item, "displayName", "profile.settings[]");
            string description = ReadString(item, "description", "profile.settings[]");
            DefaultSettingUnit unit = ReadUnit(item, "unit", "profile.settings[]");
            DefaultSettingValue defaultValue = ReadValue(
                RequireProperty(item, "defaultValue", "profile.settings[]"),
                "profile.settings[].defaultValue");
            DefaultSettingValue? minimum = ReadOptionalValue(
                RequireProperty(item, "minimum", "profile.settings[]"),
                "profile.settings[].minimum");
            DefaultSettingValue? maximum = ReadOptionalValue(
                RequireProperty(item, "maximum", "profile.settings[]"),
                "profile.settings[].maximum");
            string[] allowedTextValues = ReadStringArray(
                RequireProperty(item, "allowedTextValues", "profile.settings[]"),
                "profile.settings[].allowedTextValues");
            DefaultSettingEvidence[] evidence = ReadEvidence(
                RequireProperty(item, "evidence", "profile.settings[]"));

            definitions.Add(new DefaultSettingDefinition(
                key,
                group,
                isAdvanced,
                displayName,
                description,
                unit,
                defaultValue,
                minimum,
                maximum,
                allowedTextValues,
                evidence));
        }

        return definitions.ToArray();
    }

    private static DefaultSettingEvidence[] ReadEvidence(JsonElement value)
    {
        RequireArray(value, "profile.settings[].evidence");
        List<DefaultSettingEvidence> evidence = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireExactObject(item, "profile.settings[].evidence[]", EvidenceProperties);
            evidence.Add(new DefaultSettingEvidence(
                ReadEvidenceKind(item, "kind", "profile.settings[].evidence[]"),
                ReadString(item, "sourceId", "profile.settings[].evidence[]")));
        }

        return evidence.ToArray();
    }

    private static DefaultSettingSelection[] ReadSelections(JsonElement value)
    {
        RequireArray(value, "snapshot.values");
        List<DefaultSettingSelection> selections = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireExactObject(item, "snapshot.values[]", SelectionProperties);
            selections.Add(new DefaultSettingSelection(
                ReadString(item, "key", "snapshot.values[]"),
                ReadValue(
                    RequireProperty(item, "value", "snapshot.values[]"),
                    "snapshot.values[].value")));
        }

        return selections.ToArray();
    }

    private static DefaultSettingValue? ReadOptionalValue(
        JsonElement value,
        string context) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadValue(value, context);

    private static DefaultSettingValue ReadValue(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The {context} value must be an object.");
        }

        string kind = ReadString(value, "kind", context);
        return kind switch
        {
            "boolean" => ReadBooleanValue(value, context),
            "integer" => ReadIntegerValue(value, context),
            "rational" => ReadRationalValue(value, context),
            "text" => ReadTextValue(value, context),
            "textList" => ReadTextListValue(value, context),
            _ => throw new JsonException($"The {context}.kind value is not supported."),
        };
    }

    private static DefaultSettingValue ReadBooleanValue(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["kind", "boolean"]);
        return DefaultSettingValue.FromBoolean(ReadBoolean(value, "boolean", context));
    }

    private static DefaultSettingValue ReadIntegerValue(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["kind", "integer"]);
        JsonElement number = RequireProperty(value, "integer", context);
        return DefaultSettingValue.FromInteger(
            ReadCanonicalInt64(number, $"{context}.integer"));
    }

    private static DefaultSettingValue ReadRationalValue(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["kind", "numerator", "denominator"]);
        long numerator = ReadInt64(value, "numerator", context);
        long denominator = ReadInt64(value, "denominator", context);
        if (denominator <= 0)
        {
            throw new JsonException($"The {context}.denominator value must be positive.");
        }

        return DefaultSettingValue.FromRational(numerator, denominator);
    }

    private static DefaultSettingValue ReadTextValue(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["kind", "text"]);
        return DefaultSettingValue.FromText(ReadString(value, "text", context));
    }

    private static DefaultSettingValue ReadTextListValue(JsonElement value, string context)
    {
        RequireExactObject(value, context, ["kind", "values"]);
        return DefaultSettingValue.FromTextList(
            ReadStringArray(
                RequireProperty(value, "values", context),
                $"{context}.values"));
    }

    private static JsonDocument ParseCanonical(string canonicalJson, string description)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(canonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new JsonException($"The {description} is not valid Unicode.", exception);
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
        if (!utf8.AsSpan().SequenceEqual(canonical))
        {
            throw new JsonException($"The {description} must use canonical JSON.");
        }

        return JsonDocument.Parse(canonical);
    }

    private static void RequireSchemaVersion(
        JsonElement root,
        string context,
        string expected)
    {
        string actual = ReadString(root, "schemaVersion", context);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new JsonException($"The {context} schema version is not supported.");
        }
    }

    private static void RequireArray(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The {context} value must be an array.");
        }
    }

    private static void RequireExactObject(
        JsonElement value,
        string context,
        IReadOnlyList<string> expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The {context} value must be an object.");
        }

        int count = 0;
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            if (!observed.Add(property.Name) ||
                !expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException($"Property '{property.Name}' is not allowed in {context}.");
            }
        }

        if (count != expectedProperties.Count)
        {
            throw new JsonException($"The {context} object is missing a required property.");
        }
    }

    private static JsonElement RequireProperty(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new JsonException(
                $"The {context} object is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string ReadString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new JsonException($"The {context}.{propertyName} value must be a string.");
        }

        return text;
    }

    private static bool ReadBoolean(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"The {context}.{propertyName} value must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static long ReadInt64(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        return ReadCanonicalInt64(value, $"{context}.{propertyName}");
    }

    private static long ReadCanonicalInt64(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException($"The {context} value must be an Int64 integer.");
        }

        if (value.TryGetInt64(out long direct))
        {
            return direct;
        }

        string raw = value.GetRawText();
        int exponentIndex = raw.IndexOf('e');
        if (exponentIndex <= 0
            || !BigInteger.TryParse(
                raw.AsSpan(0, exponentIndex),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out BigInteger coefficient)
            || !BigInteger.TryParse(
                raw.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out BigInteger exponent))
        {
            throw new JsonException($"The {context} value must be an Int64 integer.");
        }

        BigInteger result;
        if (exponent.Sign >= 0)
        {
            if (exponent > 19)
            {
                throw new JsonException($"The {context} value must be an Int64 integer.");
            }

            result = coefficient * BigInteger.Pow(10, (int)exponent);
        }
        else
        {
            BigInteger magnitude = BigInteger.Negate(exponent);
            if (magnitude > 19)
            {
                throw new JsonException($"The {context} value must be an Int64 integer.");
            }

            BigInteger divisor = BigInteger.Pow(10, (int)magnitude);
            BigInteger quotient = BigInteger.DivRem(
                coefficient,
                divisor,
                out BigInteger remainder);
            if (!remainder.IsZero)
            {
                throw new JsonException($"The {context} value must be an Int64 integer.");
            }

            result = quotient;
        }

        if (result < long.MinValue || result > long.MaxValue)
        {
            throw new JsonException($"The {context} value must be an Int64 integer.");
        }

        return (long)result;
    }

    private static SettingsProfileVersion ReadVersion(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadString(parent, propertyName, context);
        if (!SettingsProfileVersion.TryParse(text, out SettingsProfileVersion? version))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be canonical MAJOR.MINOR.PATCH text.");
        }

        return version;
    }

    private static EntityId<TTag> ReadEntityId<TTag>(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadString(parent, propertyName, context);
        if (!EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) ||
            !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a nonempty lowercase UUID in D format.");
        }

        return id;
    }

    private static ContentDigest ReadDigest(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadString(parent, propertyName, context);
        if (!ContentDigest.TryParse(text, out ContentDigest? digest) ||
            !string.Equals(text, digest.Hex, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a lowercase SHA-256 digest.");
        }

        return digest;
    }

    private static string[] ReadStringArray(JsonElement value, string context)
    {
        RequireArray(value, context);
        List<string> values = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string text)
            {
                throw new JsonException($"Every {context} item must be a string.");
            }

            values.Add(text);
        }

        return values.ToArray();
    }

    private static DefaultSettingGroup ReadGroup(
        JsonElement parent,
        string propertyName,
        string context) =>
        ReadString(parent, propertyName, context) switch
        {
            "projectAndOutput" => DefaultSettingGroup.ProjectAndOutput,
            "editing" => DefaultSettingGroup.Editing,
            "subtitlesAndGraphics" => DefaultSettingGroup.SubtitlesAndGraphics,
            "audioAndAssets" => DefaultSettingGroup.AudioAndAssets,
            "privacyAndStorage" => DefaultSettingGroup.PrivacyAndStorage,
            _ => throw new JsonException($"The {context}.{propertyName} value is not supported."),
        };

    private static DefaultSettingUnit ReadUnit(
        JsonElement parent,
        string propertyName,
        string context) =>
        ReadString(parent, propertyName, context) switch
        {
            "none" => DefaultSettingUnit.None,
            "count" => DefaultSettingUnit.Count,
            "milliseconds" => DefaultSettingUnit.Milliseconds,
            "seconds" => DefaultSettingUnit.Seconds,
            "frames" => DefaultSettingUnit.Frames,
            "pixels" => DefaultSettingUnit.Pixels,
            "hertz" => DefaultSettingUnit.Hertz,
            "bitsPerSecond" => DefaultSettingUnit.BitsPerSecond,
            "percent" => DefaultSettingUnit.Percent,
            "charactersPerSecond" => DefaultSettingUnit.CharactersPerSecond,
            "fullWidthCharacters" => DefaultSettingUnit.FullWidthCharacters,
            "decibels" => DefaultSettingUnit.Decibels,
            "decibelsFullScale" => DefaultSettingUnit.DecibelsFullScale,
            "loudnessUnitsFullScale" => DefaultSettingUnit.LoudnessUnitsFullScale,
            _ => throw new JsonException($"The {context}.{propertyName} value is not supported."),
        };

    private static DefaultSettingEvidenceKind ReadEvidenceKind(
        JsonElement parent,
        string propertyName,
        string context) =>
        ReadString(parent, propertyName, context) switch
        {
            "specification" => DefaultSettingEvidenceKind.Specification,
            "officialRecommendation" => DefaultSettingEvidenceKind.OfficialRecommendation,
            "peerReviewedFinding" => DefaultSettingEvidenceKind.PeerReviewedFinding,
            "practicalGuidance" => DefaultSettingEvidenceKind.PracticalGuidance,
            "currentImplementation" => DefaultSettingEvidenceKind.CurrentImplementation,
            "productDecision" => DefaultSettingEvidenceKind.ProductDecision,
            "userDecision" => DefaultSettingEvidenceKind.UserDecision,
            _ => throw new JsonException($"The {context}.{propertyName} value is not supported."),
        };

    private static string ToWireName(DefaultSettingGroup value) => value switch
    {
        DefaultSettingGroup.ProjectAndOutput => "projectAndOutput",
        DefaultSettingGroup.Editing => "editing",
        DefaultSettingGroup.SubtitlesAndGraphics => "subtitlesAndGraphics",
        DefaultSettingGroup.AudioAndAssets => "audioAndAssets",
        DefaultSettingGroup.PrivacyAndStorage => "privacyAndStorage",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown settings group."),
    };

    private static string ToWireName(DefaultSettingValueKind value) => value switch
    {
        DefaultSettingValueKind.Boolean => "boolean",
        DefaultSettingValueKind.Integer => "integer",
        DefaultSettingValueKind.Rational => "rational",
        DefaultSettingValueKind.Text => "text",
        DefaultSettingValueKind.TextList => "textList",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown settings value kind."),
    };

    private static string ToWireName(DefaultSettingUnit value) => value switch
    {
        DefaultSettingUnit.None => "none",
        DefaultSettingUnit.Count => "count",
        DefaultSettingUnit.Milliseconds => "milliseconds",
        DefaultSettingUnit.Seconds => "seconds",
        DefaultSettingUnit.Frames => "frames",
        DefaultSettingUnit.Pixels => "pixels",
        DefaultSettingUnit.Hertz => "hertz",
        DefaultSettingUnit.BitsPerSecond => "bitsPerSecond",
        DefaultSettingUnit.Percent => "percent",
        DefaultSettingUnit.CharactersPerSecond => "charactersPerSecond",
        DefaultSettingUnit.FullWidthCharacters => "fullWidthCharacters",
        DefaultSettingUnit.Decibels => "decibels",
        DefaultSettingUnit.DecibelsFullScale => "decibelsFullScale",
        DefaultSettingUnit.LoudnessUnitsFullScale => "loudnessUnitsFullScale",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown settings unit."),
    };

    private static string ToWireName(DefaultSettingEvidenceKind value) => value switch
    {
        DefaultSettingEvidenceKind.Specification => "specification",
        DefaultSettingEvidenceKind.OfficialRecommendation => "officialRecommendation",
        DefaultSettingEvidenceKind.PeerReviewedFinding => "peerReviewedFinding",
        DefaultSettingEvidenceKind.PracticalGuidance => "practicalGuidance",
        DefaultSettingEvidenceKind.CurrentImplementation => "currentImplementation",
        DefaultSettingEvidenceKind.ProductDecision => "productDecision",
        DefaultSettingEvidenceKind.UserDecision => "userDecision",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown settings evidence kind."),
    };
}
