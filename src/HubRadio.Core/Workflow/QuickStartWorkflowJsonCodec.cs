using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;

namespace HubRadio.Core.Workflow;

internal static class QuickStartWorkflowJsonCodec
{
    private const string TimestampFormat = "O";

    private static readonly string[] EventProperties =
    [
        "schemaVersion",
        "eventId",
        "kind",
        "projectId",
        "compositionVersionId",
        "compositionVersionDigest",
        "actorKind",
        "actorId",
        "occurredAt",
        "projectRevision",
        "projectCanonicalDigest",
        "settingsSnapshotDigest",
        "evaluationId",
        "evaluationEvaluatedAt",
        "evaluationAggregateStatus",
        "evaluationCanonicalDigest",
        "artifactId",
        "artifactDigest",
        "evaluationResultState",
        "reasonCode",
        "export",
    ];

    private static readonly string[] ExportProperties =
    [
        "publicationScope",
        "rightsAllowedForInternalUse",
        "externalDestinationRequested",
        "destinationKind",
        "authorizedState",
        "authorizedRevision",
    ];

    internal static string SerializeEvent(QuickStartWorkflowEvent workflowEvent)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", QuickStartWorkflowContract.SchemaVersion);
            writer.WriteString("eventId", workflowEvent.EventId.ToString());
            writer.WriteString("kind", ToWireName(workflowEvent.Kind));
            writer.WriteString("projectId", workflowEvent.Binding.ProjectId.ToString());
            writer.WriteString(
                "compositionVersionId",
                workflowEvent.Binding.CompositionVersionId.ToString());
            writer.WriteString(
                "compositionVersionDigest",
                workflowEvent.Binding.CompositionVersionDigest.Hex);
            writer.WriteString("actorKind", ToWireName(workflowEvent.Actor.Kind));
            writer.WriteString("actorId", workflowEvent.Actor.Id);
            writer.WriteString(
                "occurredAt",
                workflowEvent.OccurredAt.ToString(
                    TimestampFormat,
                    CultureInfo.InvariantCulture));
            WriteOptionalNumber(
                writer,
                "projectRevision",
                workflowEvent.ProjectSnapshot?.Revision);
            WriteOptionalString(
                writer,
                "projectCanonicalDigest",
                workflowEvent.ProjectSnapshot?.CanonicalPayloadDigest.Hex);
            WriteOptionalString(
                writer,
                "settingsSnapshotDigest",
                workflowEvent.SettingsSnapshotDigest?.Hex);
            WriteOptionalString(
                writer,
                "evaluationId",
                workflowEvent.EvaluationId?.ToString());
            WriteOptionalString(
                writer,
                "evaluationEvaluatedAt",
                workflowEvent.EvaluationEvaluatedAt?.ToString(
                    TimestampFormat,
                    CultureInfo.InvariantCulture));
            WriteOptionalString(
                writer,
                "evaluationAggregateStatus",
                workflowEvent.EvaluationAggregateStatus is null
                    ? null
                    : AutomatedEvaluationContract.ToWireName(
                        workflowEvent.EvaluationAggregateStatus.Value));
            WriteOptionalString(
                writer,
                "evaluationCanonicalDigest",
                workflowEvent.EvaluationCanonicalDigest?.Hex);
            WriteOptionalString(
                writer,
                "artifactId",
                workflowEvent.ArtifactId?.ToString());
            WriteOptionalString(
                writer,
                "artifactDigest",
                workflowEvent.ArtifactDigest?.Hex);
            WriteOptionalString(
                writer,
                "evaluationResultState",
                workflowEvent.EvaluationResultState is null
                    ? null
                    : ToWireName(workflowEvent.EvaluationResultState.Value));
            WriteOptionalString(writer, "reasonCode", workflowEvent.ReasonCode);
            writer.WritePropertyName("export");
            WriteExport(writer, workflowEvent.ExportFacts);
            writer.WriteEndObject();
            writer.Flush();
        }

        return ToCanonicalString(buffer.WrittenSpan);
    }

    internal static QuickStartWorkflowEvent ParseEvent(
        string canonicalJson,
        bool allowExport)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        byte[] utf8;
        try
        {
            utf8 = QuickStartWorkflowContract.StrictUtf8.GetBytes(canonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new JsonException(
                "The quick-start workflow event is not valid Unicode.",
                exception);
        }

        if (utf8.Length is <= 0 or > QuickStartWorkflowEvent.MaximumCanonicalJsonUtf8Bytes)
        {
            throw new JsonException(
                "The quick-start workflow event exceeds the supported byte limit.");
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
        if (!utf8.AsSpan().SequenceEqual(canonical))
        {
            throw new JsonException(
                "The quick-start workflow event must use canonical JSON.");
        }

        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement root = document.RootElement;
        RequireExactObject(root, "quick-start event", EventProperties);
        if (!string.Equals(
                ReadString(root, "schemaVersion", "quick-start event"),
                QuickStartWorkflowContract.SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                "The quick-start workflow schema version is not supported.");
        }

        EntityId<QuickStartWorkflowEventIdTag> eventId =
            ReadEntityId<QuickStartWorkflowEventIdTag>(
                root,
                "eventId",
                "quick-start event");
        QuickStartWorkflowEventKind kind = ParseEventKind(
            ReadString(root, "kind", "quick-start event"));
        if (kind == QuickStartWorkflowEventKind.Exported && !allowExport)
        {
            throw new JsonException(
                "Exported quick-start events can be parsed only from trusted storage.");
        }

        var binding = new QuickStartWorkflowBinding(
            ReadEntityId<ProjectIdTag>(root, "projectId", "quick-start event"),
            ReadEntityId<CompositionVersionIdTag>(
                root,
                "compositionVersionId",
                "quick-start event"),
            ReadDigest(
                root,
                "compositionVersionDigest",
                "quick-start event"));
        QuickStartActor actor;
        try
        {
            actor = QuickStartActor.ParseCanonical(
                ParseActorKind(ReadString(root, "actorKind", "quick-start event")),
                ReadString(root, "actorId", "quick-start event"));
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "The quick-start workflow actor is invalid.",
                exception);
        }
        DateTimeOffset occurredAt = ReadTimestamp(
            root,
            "occurredAt",
            "quick-start event");
        QuickStartProjectSnapshot? projectSnapshot = ReadProjectSnapshot(
            root,
            "quick-start event");
        ContentDigest? settingsSnapshotDigest = ReadOptionalDigest(
            root,
            "settingsSnapshotDigest",
            "quick-start event");
        EntityId<AutomatedEvaluationIdTag>? evaluationId =
            ReadOptionalEntityId<AutomatedEvaluationIdTag>(
                root,
                "evaluationId",
                "quick-start event");
        DateTimeOffset? evaluationEvaluatedAt = ReadOptionalTimestamp(
            root,
            "evaluationEvaluatedAt",
            "quick-start event");
        string? aggregateStatusText = ReadOptionalString(
            root,
            "evaluationAggregateStatus",
            "quick-start event");
        AutomatedEvaluationStatus? evaluationAggregateStatus =
            aggregateStatusText is null
                ? null
                : AutomatedEvaluationContract.ParseStatus(aggregateStatusText);
        ContentDigest? evaluationCanonicalDigest = ReadOptionalDigest(
            root,
            "evaluationCanonicalDigest",
            "quick-start event");
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId =
            ReadOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                root,
                "artifactId",
                "quick-start event");
        ContentDigest? artifactDigest = ReadOptionalDigest(
            root,
            "artifactDigest",
            "quick-start event");
        string? stateText = ReadOptionalString(
            root,
            "evaluationResultState",
            "quick-start event");
        QuickStartWorkflowState? evaluationResultState = stateText is null
            ? null
            : ParseState(stateText);
        string? reasonCode = ReadOptionalString(
            root,
            "reasonCode",
            "quick-start event");
        QuickStartExportFacts? exportFacts = ReadExport(
            RequireProperty(root, "export", "quick-start event"),
            binding,
            projectSnapshot,
            settingsSnapshotDigest,
            evaluationId,
            evaluationAggregateStatus,
            evaluationCanonicalDigest,
            artifactId,
            artifactDigest);

        QuickStartWorkflowEvent parsed;
        try
        {
            parsed = new QuickStartWorkflowEvent(
                eventId,
                kind,
                binding,
                actor,
                occurredAt,
                projectSnapshot,
                settingsSnapshotDigest,
                evaluationId,
                evaluationEvaluatedAt,
                evaluationAggregateStatus,
                evaluationCanonicalDigest,
                artifactId,
                artifactDigest,
                evaluationResultState,
                reasonCode,
                exportFacts);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "The quick-start workflow event violates its domain contract.",
                exception);
        }

        if (!string.Equals(canonicalJson, parsed.CanonicalJson, StringComparison.Ordinal))
        {
            throw new JsonException(
                "The quick-start workflow event is not in canonical domain form.");
        }

        return parsed;
    }

    internal static ContentDigest ComputeEventChainDigest(
        IReadOnlyList<QuickStartWorkflowEvent> events)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (QuickStartWorkflowEvent workflowEvent in events)
            {
                writer.WriteStringValue(workflowEvent.CanonicalDigest.Hex);
            }

            writer.WriteEndArray();
            writer.Flush();
        }

        return ContentDigest.Compute(CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan));
    }

    internal static string SerializeSnapshot(QuickStartWorkflow workflow)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", QuickStartWorkflowContract.SchemaVersion);
            writer.WriteString("projectId", workflow.Binding.ProjectId.ToString());
            writer.WriteString(
                "compositionVersionId",
                workflow.Binding.CompositionVersionId.ToString());
            writer.WriteString(
                "compositionVersionDigest",
                workflow.Binding.CompositionVersionDigest.Hex);
            writer.WriteString("currentState", ToWireName(workflow.State));
            writer.WriteNumber("revision", workflow.Revision);
            WriteOptionalNumber(
                writer,
                "projectRevision",
                workflow.ProjectSnapshot?.Revision);
            WriteOptionalString(
                writer,
                "projectCanonicalDigest",
                workflow.ProjectSnapshot?.CanonicalPayloadDigest.Hex);
            WriteOptionalString(
                writer,
                "settingsSnapshotDigest",
                workflow.SettingsSnapshotDigest?.Hex);
            WriteOptionalString(
                writer,
                "evaluationId",
                workflow.EvaluationId?.ToString());
            WriteOptionalString(
                writer,
                "evaluationAggregateStatus",
                workflow.EvaluationAggregateStatus is null
                    ? null
                    : AutomatedEvaluationContract.ToWireName(
                        workflow.EvaluationAggregateStatus.Value));
            WriteOptionalString(
                writer,
                "evaluationCanonicalDigest",
                workflow.EvaluationCanonicalDigest?.Hex);
            WriteOptionalString(writer, "artifactId", workflow.ArtifactId?.ToString());
            WriteOptionalString(writer, "artifactDigest", workflow.ArtifactDigest?.Hex);
            writer.WriteNumber("selfReviewCount", workflow.SelfReviewCount);
            writer.WriteNumber("changeRequestCount", workflow.ChangeRequestCount);
            writer.WriteString("eventChainDigest", workflow.EventChainDigest.Hex);
            writer.WriteString(
                "lastEventId",
                workflow.Events[^1].EventId.ToString());
            writer.WriteString(
                "lastEventDigest",
                workflow.Events[^1].CanonicalDigest.Hex);
            writer.WriteEndObject();
            writer.Flush();
        }

        return ToCanonicalString(buffer.WrittenSpan);
    }

    internal static string ToWireName(QuickStartWorkflowState value) => value switch
    {
        QuickStartWorkflowState.Editing => "editing",
        QuickStartWorkflowState.AutomatedEvaluation => "automatedEvaluation",
        QuickStartWorkflowState.Ready => "ready",
        QuickStartWorkflowState.ReadyWithWarnings => "readyWithWarnings",
        QuickStartWorkflowState.Failed => "failed",
        QuickStartWorkflowState.Exported => "exported",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown state."),
    };

    internal static QuickStartWorkflowState ParseState(string value) => value switch
    {
        "editing" => QuickStartWorkflowState.Editing,
        "automatedEvaluation" => QuickStartWorkflowState.AutomatedEvaluation,
        "ready" => QuickStartWorkflowState.Ready,
        "readyWithWarnings" => QuickStartWorkflowState.ReadyWithWarnings,
        "failed" => QuickStartWorkflowState.Failed,
        "exported" => QuickStartWorkflowState.Exported,
        _ => throw new JsonException("The quick-start workflow state is not supported."),
    };

    internal static string ToWireName(QuickStartWorkflowEventKind value) => value switch
    {
        QuickStartWorkflowEventKind.StartedEditing => "StartedEditing",
        QuickStartWorkflowEventKind.EvaluationStarted => "EvaluationStarted",
        QuickStartWorkflowEventKind.TechnicalReevaluationStarted =>
            "TechnicalReevaluationStarted",
        QuickStartWorkflowEventKind.EvaluationCompleted => "EvaluationCompleted",
        QuickStartWorkflowEventKind.SelfReviewed => "SelfReviewed",
        QuickStartWorkflowEventKind.ChangesRequested => "ChangesRequested",
        QuickStartWorkflowEventKind.Exported => "Exported",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown event kind."),
    };

    internal static QuickStartWorkflowEventKind ParseEventKind(string value) => value switch
    {
        "StartedEditing" => QuickStartWorkflowEventKind.StartedEditing,
        "EvaluationStarted" => QuickStartWorkflowEventKind.EvaluationStarted,
        "TechnicalReevaluationStarted" =>
            QuickStartWorkflowEventKind.TechnicalReevaluationStarted,
        "EvaluationCompleted" => QuickStartWorkflowEventKind.EvaluationCompleted,
        "SelfReviewed" => QuickStartWorkflowEventKind.SelfReviewed,
        "ChangesRequested" => QuickStartWorkflowEventKind.ChangesRequested,
        "Exported" => QuickStartWorkflowEventKind.Exported,
        _ => throw new JsonException("The quick-start workflow event kind is not supported."),
    };

    internal static string ToWireName(QuickStartActorKind value) => value switch
    {
        QuickStartActorKind.User => "user",
        QuickStartActorKind.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown actor kind."),
    };

    internal static QuickStartActorKind ParseActorKind(string value) => value switch
    {
        "user" => QuickStartActorKind.User,
        "system" => QuickStartActorKind.System,
        _ => throw new JsonException("The quick-start workflow actor kind is not supported."),
    };

    internal static string ToWireName(QuickStartExportDestinationKind value) => value switch
    {
        QuickStartExportDestinationKind.LocalInternalDelivery =>
            "localInternalDelivery",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Unknown destination kind."),
    };

    internal static QuickStartExportDestinationKind ParseDestinationKind(
        string value) => value switch
        {
            "localInternalDelivery" =>
                QuickStartExportDestinationKind.LocalInternalDelivery,
            _ => throw new JsonException(
                "The quick-start export destination kind is not supported."),
        };

    private static void WriteExport(
        Utf8JsonWriter writer,
        QuickStartExportFacts? facts)
    {
        if (facts is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("publicationScope", facts.PublicationScope.ToString());
        writer.WriteBoolean(
            "rightsAllowedForInternalUse",
            facts.RightsAllowedForInternalUse);
        writer.WriteBoolean(
            "externalDestinationRequested",
            facts.ExternalDestinationRequested);
        writer.WriteString("destinationKind", ToWireName(facts.DestinationKind));
        writer.WriteString("authorizedState", ToWireName(facts.AuthorizedState));
        writer.WriteNumber("authorizedRevision", facts.AuthorizedRevision);
        writer.WriteEndObject();
    }

    private static QuickStartExportFacts? ReadExport(
        JsonElement value,
        QuickStartWorkflowBinding binding,
        QuickStartProjectSnapshot? projectSnapshot,
        ContentDigest? settingsSnapshotDigest,
        EntityId<AutomatedEvaluationIdTag>? evaluationId,
        AutomatedEvaluationStatus? evaluationAggregateStatus,
        ContentDigest? evaluationCanonicalDigest,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId,
        ContentDigest? artifactDigest)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        const string Context = "quick-start event.export";
        RequireExactObject(value, Context, ExportProperties);
        if (projectSnapshot is null ||
            settingsSnapshotDigest is null ||
            evaluationId is null ||
            evaluationAggregateStatus is null ||
            evaluationCanonicalDigest is null ||
            artifactId is null ||
            artifactDigest is null)
        {
            throw new JsonException(
                "A quick-start export requires complete evaluation and artifact bindings.");
        }

        string scopeText = ReadString(value, "publicationScope", Context);
        if (!Enum.TryParse(
                scopeText,
                ignoreCase: false,
                out PublicationScope publicationScope) ||
            !Enum.IsDefined(publicationScope) ||
            !string.Equals(scopeText, publicationScope.ToString(), StringComparison.Ordinal))
        {
            throw new JsonException("The quick-start publication scope is invalid.");
        }

        try
        {
            return new QuickStartExportFacts(
                binding,
                projectSnapshot,
                publicationScope,
                ReadBoolean(value, "rightsAllowedForInternalUse", Context),
                ReadBoolean(value, "externalDestinationRequested", Context),
                ParseDestinationKind(ReadString(value, "destinationKind", Context)),
                ParseState(ReadString(value, "authorizedState", Context)),
                ReadPositiveInt32(value, "authorizedRevision", Context),
                settingsSnapshotDigest,
                evaluationId,
                evaluationAggregateStatus.Value,
                evaluationCanonicalDigest,
                artifactId,
                artifactDigest);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The quick-start export facts are invalid.", exception);
        }
    }

    private static string ToCanonicalString(ReadOnlySpan<byte> utf8)
    {
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
        return QuickStartWorkflowContract.StrictUtf8.GetString(canonical);
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

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }

    private static QuickStartProjectSnapshot? ReadProjectSnapshot(
        JsonElement parent,
        string context)
    {
        int? revision = ReadOptionalNonnegativeInt32(
            parent,
            "projectRevision",
            context);
        ContentDigest? digest = ReadOptionalDigest(
            parent,
            "projectCanonicalDigest",
            context);
        if ((revision is null) != (digest is null))
        {
            throw new JsonException(
                "A quick-start project revision and canonical digest must either both be present or both be null.");
        }

        return revision is null
            ? null
            : new QuickStartProjectSnapshot(revision.Value, digest!);
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
                throw new JsonException(
                    $"Property '{property.Name}' is not allowed in {context}.");
            }
        }

        if (count != expectedProperties.Count)
        {
            throw new JsonException(
                $"The {context} object is missing a required property.");
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
            throw new JsonException(
                $"The {context}.{propertyName} value must be a string.");
        }

        return text;
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

    private static bool ReadBoolean(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a Boolean.");
        }

        return value.GetBoolean();
    }

    private static int ReadPositiveInt32(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed <= 0 ||
            parsed > int.MaxValue)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a positive Int32.");
        }

        return decimal.ToInt32(parsed);
    }

    private static int? ReadOptionalNonnegativeInt32(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !decimal.TryParse(
                value.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed < 0 ||
            parsed > int.MaxValue)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a nonnegative Int32 or null.");
        }

        return decimal.ToInt32(parsed);
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
                $"The {context}.{propertyName} value must be a lowercase UUID in D format.");
        }

        return id;
    }

    private static EntityId<TTag>? ReadOptionalEntityId<TTag>(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string? text = ReadOptionalString(parent, propertyName, context);
        if (text is null)
        {
            return null;
        }

        if (!EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) ||
            !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a lowercase UUID in D format or null.");
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

    private static ContentDigest? ReadOptionalDigest(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string? text = ReadOptionalString(parent, propertyName, context);
        return text is null
            ? null
            : ContentDigest.TryParse(text, out ContentDigest? digest) &&
                string.Equals(text, digest.Hex, StringComparison.Ordinal)
                ? digest
                : throw new JsonException(
                    $"The {context}.{propertyName} value must be a lowercase SHA-256 digest or null.");
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadString(parent, propertyName, context);
        return ParseTimestamp(text, propertyName, context);
    }

    private static DateTimeOffset? ReadOptionalTimestamp(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string? text = ReadOptionalString(parent, propertyName, context);
        return text is null
            ? null
            : ParseTimestamp(text, propertyName, context);
    }

    private static DateTimeOffset ParseTimestamp(
        string text,
        string propertyName,
        string context)
    {
        if (!DateTimeOffset.TryParseExact(
                text,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset value) ||
            value.Offset != TimeSpan.Zero ||
            !string.Equals(
                text,
                value.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a canonical UTC timestamp.");
        }

        return value;
    }
}