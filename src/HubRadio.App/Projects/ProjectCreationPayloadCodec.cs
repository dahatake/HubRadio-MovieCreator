using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;

namespace HubRadio.App.Projects;

internal static class ProjectCreationPayloadCodec
{
    internal const string SchemaVersion = "1.0.0";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] InputProperties =
    [
        "sourceCandidateId",
        "contentName",
        "purpose",
        "audience",
        "publicationScope",
        "outputProfileIds",
        "language",
        "performerNames",
        "deadlineSelection",
        "brandTemplateId",
    ];

    private static readonly string[] PayloadProperties =
    [
        "schemaVersion",
        "draftId",
        "sourceCandidateId",
        "contentName",
        "purpose",
        "audience",
        "publicationScope",
        "outputProfiles",
        "language",
        "performerNames",
        "deadlineSelection",
        "brandTemplate",
    ];

    internal static ProjectCreationDraft ParseInput(
        JsonElement input,
        EntityId<ProjectCreationDraftIdTag> draftId,
        ProjectCreationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        ArgumentNullException.ThrowIfNull(catalog);
        RequireExactObject(input, "input", InputProperties);

        EntityId<AssetIdTag>? sourceCandidateId = ReadOptionalEntityId<AssetIdTag>(
            input,
            "sourceCandidateId",
            "input");
        string? contentName = ReadOptionalString(input, "contentName", "input");
        string? purpose = ReadOptionalString(input, "purpose", "input");
        string? audience = ReadOptionalString(input, "audience", "input");
        PublicationScope? publicationScope = ReadOptionalPublicationScope(
            input,
            "publicationScope",
            "input");
        ProjectOutputProfileReference[] outputProfiles = ReadOutputProfileIds(
            RequireProperty(input, "outputProfileIds", "input"),
            catalog);
        string? language = ReadOptionalString(input, "language", "input");
        string[] performerNames = ReadStringArray(
            RequireProperty(input, "performerNames", "input"),
            "input.performerNames");
        ProjectDeadlineSelection? deadlineSelection = ReadInputDeadlineSelection(
            RequireProperty(input, "deadlineSelection", "input"));
        TemplateReference? brandTemplate = ReadOptionalBrandTemplateId(
            input,
            catalog);

        return new ProjectCreationDraft(
            draftId,
            sourceCandidateId,
            contentName,
            purpose,
            audience,
            publicationScope,
            outputProfiles,
            language,
            performerNames,
            deadlineSelection,
            brandTemplate);
    }

    internal static string Serialize(ProjectCreationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("draftId", draft.Id.ToString());
            WriteOptionalEntityId(writer, "sourceCandidateId", draft.SourceCandidateId);
            WriteOptionalString(writer, "contentName", draft.ContentName);
            WriteOptionalString(writer, "purpose", draft.Purpose);
            WriteOptionalString(writer, "audience", draft.Audience);
            WriteOptionalPublicationScope(
                writer,
                "publicationScope",
                draft.PublicationScope);
            writer.WriteStartArray("outputProfiles");
            foreach (ProjectOutputProfileReference profile in draft.OutputProfiles)
            {
                writer.WriteStartObject();
                writer.WriteString("id", profile.Id.ToString());
                writer.WriteString("name", profile.Name);
                writer.WriteString("profileDigest", profile.ProfileDigest.Hex);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteOptionalString(writer, "language", draft.Language);
            writer.WriteStartArray("performerNames");
            foreach (string performerName in draft.PerformerNames)
            {
                writer.WriteStringValue(performerName);
            }

            writer.WriteEndArray();
            WriteDeadlineSelection(writer, draft.DeadlineSelection);
            WriteBrandTemplate(writer, draft.BrandTemplate);
            writer.WriteEndObject();
        }

        return StrictUtf8.GetString(
            CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    internal static ProjectCreationDraft ParseStored(
        string canonicalJson,
        ProjectCreationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        ArgumentNullException.ThrowIfNull(catalog);

        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(canonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new JsonException(
                "The stored project-creation payload is not valid Unicode.",
                exception);
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
        if (!utf8.AsSpan().SequenceEqual(canonical))
        {
            throw new JsonException(
                "The stored project-creation payload is not canonical JSON.");
        }

        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement root = document.RootElement;
        RequireExactObject(root, "payload", PayloadProperties);
        string schemaVersion = RequireString(root, "schemaVersion", "payload");
        if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException(
                "The project-creation payload schema version is not supported.");
        }

        EntityId<ProjectCreationDraftIdTag> draftId =
            ReadEntityId<ProjectCreationDraftIdTag>(root, "draftId", "payload");
        EntityId<AssetIdTag>? sourceCandidateId = ReadOptionalEntityId<AssetIdTag>(
            root,
            "sourceCandidateId",
            "payload");
        string? contentName = ReadOptionalString(root, "contentName", "payload");
        string? purpose = ReadOptionalString(root, "purpose", "payload");
        string? audience = ReadOptionalString(root, "audience", "payload");
        PublicationScope? publicationScope = ReadOptionalPublicationScope(
            root,
            "publicationScope",
            "payload");
        ProjectOutputProfileReference[] outputProfiles = ReadStoredOutputProfiles(
            RequireProperty(root, "outputProfiles", "payload"),
            catalog);
        string? language = ReadOptionalString(root, "language", "payload");
        string[] performerNames = ReadStringArray(
            RequireProperty(root, "performerNames", "payload"),
            "payload.performerNames");
        ProjectDeadlineSelection? deadlineSelection = ReadStoredDeadlineSelection(
            RequireProperty(root, "deadlineSelection", "payload"));
        TemplateReference? brandTemplate = ReadStoredBrandTemplate(
            RequireProperty(root, "brandTemplate", "payload"),
            catalog);

        return new ProjectCreationDraft(
            draftId,
            sourceCandidateId,
            contentName,
            purpose,
            audience,
            publicationScope,
            outputProfiles,
            language,
            performerNames,
            deadlineSelection,
            brandTemplate);
    }

    internal static string ToWireName(PublicationScope scope) => scope switch
    {
        PublicationScope.InternalOnly => "internalOnly",
        PublicationScope.SpecificGroupOnly => "specificGroupOnly",
        PublicationScope.ExternalCandidate => "externalCandidate",
        PublicationScope.ExternalPublicationApproved =>
            "externalPublicationApproved",
        PublicationScope.PublicationForbidden => "publicationForbidden",
        PublicationScope.PendingDecision => "pendingDecision",
        _ => throw new ArgumentOutOfRangeException(
            nameof(scope),
            scope,
            "Unknown publication scope."),
    };

    internal static string ToWireName(ProjectCreationField field) => field switch
    {
        ProjectCreationField.SourceMedia => "sourceMedia",
        ProjectCreationField.ContentName => "contentName",
        ProjectCreationField.Purpose => "purpose",
        ProjectCreationField.Audience => "audience",
        ProjectCreationField.PublicationScope => "publicationScope",
        ProjectCreationField.OutputFormats => "outputFormats",
        ProjectCreationField.Language => "language",
        ProjectCreationField.PerformerNames => "performerNames",
        ProjectCreationField.Deadline => "deadline",
        ProjectCreationField.BrandTemplate => "brandTemplate",
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field,
            "Unknown project-creation field."),
    };

    internal static string FormatTimestamp(DateTimeOffset value)
    {
        try
        {
            return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The timestamp cannot be represented in UTC.");
        }
    }

    private static ProjectOutputProfileReference[] ReadOutputProfileIds(
        JsonElement value,
        ProjectCreationCatalog catalog)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The input.outputProfileIds value must be an array.");
        }

        var profiles = new List<ProjectOutputProfileReference>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string id)
            {
                throw new JsonException(
                    "Every input.outputProfileIds value must be a string.");
            }

            profiles.Add(catalog.ResolveOutputProfile(id));
        }

        return profiles.ToArray();
    }

    private static ProjectOutputProfileReference[] ReadStoredOutputProfiles(
        JsonElement value,
        ProjectCreationCatalog catalog)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The payload.outputProfiles value must be an array.");
        }

        var profiles = new List<ProjectOutputProfileReference>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireExactObject(
                item,
                "payload.outputProfiles[]",
                ["id", "name", "profileDigest"]);
            string id = RequireString(item, "id", "payload.outputProfiles[]");
            ProjectOutputProfileReference approved = catalog.ResolveOutputProfile(id);
            string name = RequireString(item, "name", "payload.outputProfiles[]");
            ContentDigest digest = ReadDigest(
                item,
                "profileDigest",
                "payload.outputProfiles[]");
            if (!string.Equals(name, approved.Name, StringComparison.Ordinal) ||
                digest != approved.ProfileDigest)
            {
                throw new JsonException(
                    "A stored output profile does not match the bundled catalog.");
            }

            profiles.Add(approved);
        }

        return profiles.ToArray();
    }

    private static TemplateReference? ReadOptionalBrandTemplateId(
        JsonElement input,
        ProjectCreationCatalog catalog)
    {
        JsonElement value = RequireProperty(input, "brandTemplateId", "input");
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string id)
        {
            throw new JsonException(
                "The input.brandTemplateId value must be a string or null.");
        }

        return catalog.ResolveBrandTemplate(id);
    }

    private static TemplateReference? ReadStoredBrandTemplate(
        JsonElement value,
        ProjectCreationCatalog catalog)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireExactObject(
            value,
            "payload.brandTemplate",
            ["id", "name", "versionDigest"]);
        string id = RequireString(value, "id", "payload.brandTemplate");
        TemplateReference approved = catalog.ResolveBrandTemplate(id);
        string name = RequireString(value, "name", "payload.brandTemplate");
        ContentDigest digest = ReadDigest(
            value,
            "versionDigest",
            "payload.brandTemplate");
        if (!string.Equals(name, approved.Name, StringComparison.Ordinal) ||
            digest != approved.VersionDigest)
        {
            throw new JsonException(
                "The stored brand template does not match the bundled catalog.");
        }

        return approved;
    }

    private static ProjectDeadlineSelection? ReadInputDeadlineSelection(
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireExactObject(
            value,
            "input.deadlineSelection",
            ["hasDeadline", "value"]);
        JsonElement hasDeadlineValue = RequireProperty(
            value,
            "hasDeadline",
            "input.deadlineSelection");
        if (hasDeadlineValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException(
                "The input.deadlineSelection.hasDeadline value must be a boolean.");
        }

        bool hasDeadline = hasDeadlineValue.GetBoolean();
        JsonElement deadlineValue = RequireProperty(
            value,
            "value",
            "input.deadlineSelection");
        if (!hasDeadline)
        {
            if (deadlineValue.ValueKind != JsonValueKind.Null)
            {
                throw new JsonException(
                    "A no-deadline selection must have a null value.");
            }

            return new ProjectDeadlineSelection(deadline: null);
        }

        return new ProjectDeadlineSelection(
            ReadTimestamp(deadlineValue, "input.deadlineSelection.value"));
    }

    private static ProjectDeadlineSelection? ReadStoredDeadlineSelection(
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireExactObject(
            value,
            "payload.deadlineSelection",
            ["deadline"]);
        JsonElement deadline = RequireProperty(
            value,
            "deadline",
            "payload.deadlineSelection");
        return deadline.ValueKind == JsonValueKind.Null
            ? new ProjectDeadlineSelection(deadline: null)
            : new ProjectDeadlineSelection(
                ReadTimestamp(deadline, "payload.deadlineSelection.deadline"));
    }

    private static DateTimeOffset ReadTimestamp(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new JsonException($"The {context} value must be a timestamp string.");
        }

        bool parsed = DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp) ||
            DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        if (!parsed || timestamp.Offset != TimeSpan.Zero)
        {
            throw new JsonException(
                $"The {context} value must be a canonical UTC timestamp.");
        }

        return timestamp;
    }

    private static string[] ReadStringArray(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The {context} value must be an array.");
        }

        var values = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string text)
            {
                throw new JsonException($"Every {context} value must be a string.");
            }

            values.Add(text);
        }

        return values.ToArray();
    }

    private static PublicationScope? ReadOptionalPublicationScope(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a string or null.");
        }

        return text switch
        {
            "internalOnly" => PublicationScope.InternalOnly,
            "specificGroupOnly" => PublicationScope.SpecificGroupOnly,
            "externalCandidate" => PublicationScope.ExternalCandidate,
            "externalPublicationApproved" =>
                PublicationScope.ExternalPublicationApproved,
            "publicationForbidden" => PublicationScope.PublicationForbidden,
            "pendingDecision" => PublicationScope.PendingDecision,
            _ => throw new JsonException(
                $"The {context}.{propertyName} value is not supported."),
        };
    }

    private static EntityId<TTag>? ReadOptionalEntityId<TTag>(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadEntityId<TTag>(value, $"{context}.{propertyName}");
    }

    private static EntityId<TTag> ReadEntityId<TTag>(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        return ReadEntityId<TTag>(value, $"{context}.{propertyName}");
    }

    private static EntityId<TTag> ReadEntityId<TTag>(
        JsonElement value,
        string context)
    {
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string text ||
            !EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) ||
            !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context} value must be a nonempty lowercase UUID in D format.");
        }

        return id;
    }

    private static ContentDigest ReadDigest(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = RequireString(parent, propertyName, context);
        if (!ContentDigest.TryParse(text, out ContentDigest? digest) ||
            !string.Equals(text, digest.Hex, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a lowercase SHA-256 digest.");
        }

        return digest;
    }

    private static string? ReadOptionalString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a string or null.");
        }

        return text;
    }

    private static void WriteOptionalEntityId<TTag>(
        Utf8JsonWriter writer,
        string propertyName,
        EntityId<TTag>? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.ToString());
        }
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteOptionalPublicationScope(
        Utf8JsonWriter writer,
        string propertyName,
        PublicationScope? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, ToWireName(value.Value));
        }
    }

    private static void WriteDeadlineSelection(
        Utf8JsonWriter writer,
        ProjectDeadlineSelection? value)
    {
        if (value is null)
        {
            writer.WriteNull("deadlineSelection");
            return;
        }

        writer.WriteStartObject("deadlineSelection");
        if (value.Deadline is DateTimeOffset deadline)
        {
            writer.WriteString("deadline", FormatTimestamp(deadline));
        }
        else
        {
            writer.WriteNull("deadline");
        }

        writer.WriteEndObject();
    }

    private static void WriteBrandTemplate(
        Utf8JsonWriter writer,
        TemplateReference? value)
    {
        if (value is null)
        {
            writer.WriteNull("brandTemplate");
            return;
        }

        writer.WriteStartObject("brandTemplate");
        writer.WriteString("id", value.Id.ToString());
        writer.WriteString("name", value.Name);
        writer.WriteString("versionDigest", value.VersionDigest.Hex);
        writer.WriteEndObject();
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
        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException(
                    $"Property '{property.Name}' is not allowed in {context}.");
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

    private static string RequireString(
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
}