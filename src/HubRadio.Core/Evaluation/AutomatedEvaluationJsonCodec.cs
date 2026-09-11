using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;

namespace HubRadio.Core.Evaluation;

internal static class AutomatedEvaluationJsonCodec
{
    private const string TimestampFormat = "O";

    private static readonly string[] RootProperties =
    [
        "schemaVersion",
        "evaluationId",
        "jobId",
        "projectId",
        "compositionVersionId",
        "compositionVersionDigest",
        "artifactId",
        "artifactDigest",
        "settingsProfileId",
        "settingsProfileVersion",
        "settingsProfileDigest",
        "settingsSnapshotDigest",
        "ruleSetVersion",
        "environmentVersion",
        "environmentDigest",
        "toolVersionsDigest",
        "modelVersionsDigest",
        "templateDigest",
        "musicDigest",
        "startedAt",
        "completedAt",
        "evaluatedAt",
        "inputDigest",
        "outputDigest",
        "aggregateStatus",
        "statusCounts",
        "rules",
        "limitations",
    ];

    private static readonly string[] RuleProperties =
        ["ruleId", "category", "scope", "status", "reasonCode", "evidenceDigest", "target"];

    private static readonly string[] TargetProperties =
    [
        "kind",
        "projectId",
        "compositionVersionId",
        "compositionVersionDigest",
        "artifactId",
        "artifactDigest",
        "timeBasis",
        "time",
        "settingKey",
        "editorKind",
    ];

    private static readonly string[] RationalTimeProperties = ["value", "rate"];
    private static readonly string[] StatusCountProperties =
        ["pass", "warning", "fail", "notEvaluated"];

    internal static string Serialize(AutomatedEvaluationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", AutomatedEvaluationContract.SchemaVersion);
            writer.WriteString("evaluationId", run.EvaluationId.ToString());
            WriteOptionalString(writer, "jobId", run.JobId?.ToString());
            writer.WriteString("projectId", run.ProjectId.ToString());
            writer.WriteString("compositionVersionId", run.CompositionVersionId.ToString());
            writer.WriteString("compositionVersionDigest", run.CompositionVersionDigest.Hex);
            WriteOptionalString(writer, "artifactId", run.ArtifactId?.ToString());
            WriteOptionalString(writer, "artifactDigest", run.ArtifactDigest?.Hex);
            writer.WriteString("settingsProfileId", run.SettingsProfileId.ToString());
            writer.WriteString("settingsProfileVersion", run.SettingsProfileVersion.ToString());
            writer.WriteString("settingsProfileDigest", run.SettingsProfileDigest.Hex);
            writer.WriteString("settingsSnapshotDigest", run.SettingsSnapshotDigest.Hex);
            writer.WriteString("ruleSetVersion", run.RuleSetVersion.ToString());
            writer.WriteString("environmentVersion", run.EnvironmentVersion);
            writer.WriteString("environmentDigest", run.EnvironmentDigest.Hex);
            writer.WriteString("toolVersionsDigest", run.ToolVersionsDigest.Hex);
            WriteOptionalString(writer, "modelVersionsDigest", run.ModelVersionsDigest?.Hex);
            WriteOptionalString(writer, "templateDigest", run.TemplateDigest?.Hex);
            WriteOptionalString(writer, "musicDigest", run.MusicDigest?.Hex);
            writer.WriteString("startedAt", FormatTimestamp(run.StartedAt));
            writer.WriteString("completedAt", FormatTimestamp(run.CompletedAt));
            writer.WriteString("evaluatedAt", FormatTimestamp(run.EvaluatedAt));
            writer.WriteString("inputDigest", run.InputDigest.Hex);
            WriteOptionalString(writer, "outputDigest", run.OutputDigest?.Hex);
            writer.WriteString(
                "aggregateStatus",
                AutomatedEvaluationContract.ToWireName(run.AggregateStatus));
            writer.WriteStartObject("statusCounts");
            writer.WriteNumber("pass", run.StatusCounts.Pass);
            writer.WriteNumber("warning", run.StatusCounts.Warning);
            writer.WriteNumber("fail", run.StatusCounts.Fail);
            writer.WriteNumber("notEvaluated", run.StatusCounts.NotEvaluated);
            writer.WriteEndObject();
            WriteRuleResults(writer, "rules", run.Rules);
            WriteRuleResults(writer, "limitations", run.Limitations);
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
        return AutomatedEvaluationContract.StrictUtf8.GetString(canonical);
    }

    private static void WriteRuleResults(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<AutomatedEvaluationRuleResult> results)
    {
        writer.WriteStartArray(propertyName);
        foreach (AutomatedEvaluationRuleResult result in results)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "ruleId",
                AutomatedEvaluationContract.ToWireName(result.RuleId));
            writer.WriteString(
                "category",
                AutomatedEvaluationContract.ToWireName(result.Category));
            writer.WriteString(
                "scope",
                AutomatedEvaluationContract.ToWireName(result.Scope));
            writer.WriteString(
                "status",
                AutomatedEvaluationContract.ToWireName(result.Status));
            writer.WriteString("reasonCode", result.ReasonCode);
            WriteOptionalString(writer, "evidenceDigest", result.EvidenceDigest?.Hex);
            writer.WritePropertyName("target");
            WriteTarget(writer, result.Target);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static AutomatedEvaluationRun ParseCanonical(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        byte[] utf8;
        try
        {
            utf8 = AutomatedEvaluationContract.StrictUtf8.GetBytes(canonicalJson);
        }
        catch (EncoderFallbackException exception)
        {
            throw new JsonException(
                "The automated-evaluation evidence is not valid Unicode.",
                exception);
        }

        if (utf8.Length is <= 0 or > AutomatedEvaluationRun.MaximumCanonicalJsonUtf8Bytes)
        {
            throw new JsonException(
                "The automated-evaluation evidence exceeds the supported byte limit.");
        }

        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(utf8);
        if (!utf8.AsSpan().SequenceEqual(canonical))
        {
            throw new JsonException(
                "The automated-evaluation evidence must use canonical JSON.");
        }

        using JsonDocument document = JsonDocument.Parse(canonical);
        JsonElement root = document.RootElement;
        RequireExactObject(root, "evaluation", RootProperties);
        string schemaVersion = ReadString(root, "schemaVersion", "evaluation");
        if (!string.Equals(
                schemaVersion,
                AutomatedEvaluationContract.SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new JsonException("The automated-evaluation schema version is not supported.");
        }

        EntityId<AutomatedEvaluationIdTag> evaluationId =
            ReadEntityId<AutomatedEvaluationIdTag>(root, "evaluationId", "evaluation");
        EntityId<JobIdTag>? jobId =
            ReadOptionalEntityId<JobIdTag>(root, "jobId", "evaluation");
        EntityId<ProjectIdTag> projectId =
            ReadEntityId<ProjectIdTag>(root, "projectId", "evaluation");
        EntityId<CompositionVersionIdTag> compositionVersionId =
            ReadEntityId<CompositionVersionIdTag>(
                root,
                "compositionVersionId",
                "evaluation");
        ContentDigest compositionVersionDigest = ReadDigest(
            root,
            "compositionVersionDigest",
            "evaluation");
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId =
            ReadOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                root,
                "artifactId",
                "evaluation");
        ContentDigest? artifactDigest = ReadOptionalDigest(
            root,
            "artifactDigest",
            "evaluation");
        EntityId<DefaultSettingsProfileIdTag> settingsProfileId =
            ReadEntityId<DefaultSettingsProfileIdTag>(
                root,
                "settingsProfileId",
                "evaluation");
        SettingsProfileVersion settingsProfileVersion = ReadVersion(
            root,
            "settingsProfileVersion",
            "evaluation");
        ContentDigest settingsProfileDigest = ReadDigest(
            root,
            "settingsProfileDigest",
            "evaluation");
        ContentDigest settingsSnapshotDigest = ReadDigest(
            root,
            "settingsSnapshotDigest",
            "evaluation");
        SettingsProfileVersion ruleSetVersion = ReadVersion(
            root,
            "ruleSetVersion",
            "evaluation");
        string environmentVersion = ReadString(
            root,
            "environmentVersion",
            "evaluation");
        ContentDigest environmentDigest = ReadDigest(
            root,
            "environmentDigest",
            "evaluation");
        ContentDigest toolVersionsDigest = ReadDigest(
            root,
            "toolVersionsDigest",
            "evaluation");
        ContentDigest? modelVersionsDigest = ReadOptionalDigest(
            root,
            "modelVersionsDigest",
            "evaluation");
        ContentDigest? templateDigest = ReadOptionalDigest(
            root,
            "templateDigest",
            "evaluation");
        ContentDigest? musicDigest = ReadOptionalDigest(
            root,
            "musicDigest",
            "evaluation");
        DateTimeOffset startedAt = ReadTimestamp(root, "startedAt", "evaluation");
        DateTimeOffset completedAt = ReadTimestamp(root, "completedAt", "evaluation");
        DateTimeOffset evaluatedAt = ReadTimestamp(root, "evaluatedAt", "evaluation");
        ContentDigest inputDigest = ReadDigest(root, "inputDigest", "evaluation");
        ContentDigest? outputDigest = ReadOptionalDigest(
            root,
            "outputDigest",
            "evaluation");
        AutomatedEvaluationStatus suppliedAggregate =
            AutomatedEvaluationContract.ParseStatus(
                ReadString(root, "aggregateStatus", "evaluation"));
        ParsedStatusCounts suppliedCounts = ReadStatusCounts(
            RequireProperty(root, "statusCounts", "evaluation"));
        AutomatedEvaluationRuleObservation[] observations = ReadMachineRules(
            RequireProperty(root, "rules", "evaluation"));
        ValidateLimitations(RequireProperty(root, "limitations", "evaluation"));

        AutomatedEvaluationRun evaluated;
        try
        {
            var request = new AutomatedEvaluationRequest(
                evaluationId,
                jobId,
                projectId,
                compositionVersionId,
                compositionVersionDigest,
                artifactId,
                artifactDigest,
                settingsProfileId,
                settingsProfileVersion,
                settingsProfileDigest,
                settingsSnapshotDigest,
                ruleSetVersion,
                environmentVersion,
                environmentDigest,
                toolVersionsDigest,
                modelVersionsDigest,
                templateDigest,
                musicDigest,
                startedAt,
                completedAt,
                evaluatedAt,
                inputDigest,
                outputDigest,
                observations);
            evaluated = new AutomatedEvaluationEngine().Evaluate(request);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(
                "The automated-evaluation evidence violates its domain contract.",
                exception);
        }

        if (evaluated.AggregateStatus != suppliedAggregate)
        {
            throw new JsonException(
                "The automated-evaluation aggregate status does not match its rule results.");
        }

        if (evaluated.StatusCounts.Pass != suppliedCounts.Pass ||
            evaluated.StatusCounts.Warning != suppliedCounts.Warning ||
            evaluated.StatusCounts.Fail != suppliedCounts.Fail ||
            evaluated.StatusCounts.NotEvaluated != suppliedCounts.NotEvaluated)
        {
            throw new JsonException(
                "The automated-evaluation status counts do not match its results.");
        }

        if (!string.Equals(canonicalJson, evaluated.CanonicalJson, StringComparison.Ordinal))
        {
            throw new JsonException(
                "The automated-evaluation rules are not in canonical domain order or contain inconsistent derived data.");
        }

        return evaluated;
    }

    private static void WriteTarget(
        Utf8JsonWriter writer,
        AutomatedEvaluationTarget? target)
    {
        if (target is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", AutomatedEvaluationContract.ToWireName(target.Kind));
        writer.WriteString("projectId", target.ProjectId.ToString());
        writer.WriteString("compositionVersionId", target.CompositionVersionId.ToString());
        writer.WriteString("compositionVersionDigest", target.CompositionVersionDigest.Hex);
        WriteOptionalString(writer, "artifactId", target.ArtifactId?.ToString());
        WriteOptionalString(writer, "artifactDigest", target.ArtifactDigest?.Hex);
        WriteOptionalString(
            writer,
            "timeBasis",
            target.TimeBasis is null
                ? null
                : AutomatedEvaluationContract.ToWireName(target.TimeBasis.Value));
        writer.WritePropertyName("time");
        WriteRationalTime(writer, target.Time);
        WriteOptionalString(writer, "settingKey", target.SettingKey);
        WriteOptionalString(
            writer,
            "editorKind",
            target.EditorKind is null
                ? null
                : AutomatedEvaluationContract.ToWireName(target.EditorKind.Value));
        writer.WriteEndObject();
    }

    private static void WriteRationalTime(Utf8JsonWriter writer, RationalTime? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value.Value);
        writer.WriteNumber("rate", value.Value.Rate);
        writer.WriteEndObject();
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

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static AutomatedEvaluationRuleObservation[] ReadMachineRules(JsonElement value)
    {
        RequireArray(value, "evaluation.rules");
        var observations = new List<AutomatedEvaluationRuleObservation>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireExactObject(item, "evaluation.rules[]", RuleProperties);
            AutomatedEvaluationRuleId ruleId = AutomatedEvaluationContract.ParseRuleId(
                ReadString(item, "ruleId", "evaluation.rules[]"));
            AutomatedEvaluationRuleDefinition definition =
                AutomatedEvaluationRules.GetDefinition(ruleId);
            AutomatedEvaluationRuleCategory category =
                AutomatedEvaluationContract.ParseRuleCategory(
                    ReadString(item, "category", "evaluation.rules[]"));
            AutomatedEvaluationRuleScope scope =
                AutomatedEvaluationContract.ParseRuleScope(
                    ReadString(item, "scope", "evaluation.rules[]"));
            if (definition.Category != AutomatedEvaluationRuleCategory.Machine ||
                category != definition.Category ||
                scope != definition.Scope)
            {
                throw new JsonException(
                    "An automated-evaluation rule has inconsistent metadata.");
            }

            AutomatedEvaluationStatus status = AutomatedEvaluationContract.ParseStatus(
                ReadString(item, "status", "evaluation.rules[]"));
            string reasonCode = ReadString(item, "reasonCode", "evaluation.rules[]");
            ContentDigest? evidenceDigest = ReadOptionalDigest(
                item,
                "evidenceDigest",
                "evaluation.rules[]");
            AutomatedEvaluationTarget? target = ReadOptionalTarget(
                RequireProperty(item, "target", "evaluation.rules[]"));

            bool isDerivedNotEvaluated =
                status == AutomatedEvaluationStatus.NotEvaluated &&
                (string.Equals(
                     reasonCode,
                     AutomatedEvaluationEngine.MissingObservationReasonCode,
                     StringComparison.Ordinal) ||
                 (scope == AutomatedEvaluationRuleScope.Artifact &&
                  string.Equals(
                      reasonCode,
                      AutomatedEvaluationEngine.ArtifactUnavailableReasonCode,
                      StringComparison.Ordinal)));
            if (isDerivedNotEvaluated)
            {
                if (evidenceDigest is not null || target is not null)
                {
                    throw new JsonException(
                        "A derived not-evaluated rule cannot contain evidence or target data.");
                }

                continue;
            }

            try
            {
                observations.Add(status switch
                {
                    AutomatedEvaluationStatus.Pass when evidenceDigest is not null =>
                        AutomatedEvaluationRuleObservation.ObjectivePass(
                            ruleId,
                            reasonCode,
                            evidenceDigest),
                    AutomatedEvaluationStatus.Warning when evidenceDigest is not null =>
                        AutomatedEvaluationRuleObservation.ObjectiveWarning(
                            ruleId,
                            reasonCode,
                            evidenceDigest,
                            target),
                    AutomatedEvaluationStatus.Fail when evidenceDigest is not null =>
                        AutomatedEvaluationRuleObservation.ObjectiveFail(
                            ruleId,
                            reasonCode,
                            evidenceDigest,
                            target),
                    AutomatedEvaluationStatus.NotEvaluated =>
                        AutomatedEvaluationRuleObservation.NotEvaluated(
                            ruleId,
                            reasonCode),
                    _ => throw new JsonException(
                        "An evaluated rule requires an evidence digest."),
                });
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                throw new JsonException(
                    "An automated-evaluation rule result is invalid.",
                    exception);
            }
        }

        return observations.ToArray();
    }

    private static void ValidateLimitations(JsonElement value)
    {
        RequireArray(value, "evaluation.limitations");
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (index >= AutomatedEvaluationRules.HumanLimitations.Count)
            {
                throw new JsonException(
                    "The automated-evaluation limitations contain extra entries.");
            }

            const string Context = "evaluation.limitations[]";
            RequireExactObject(item, Context, RuleProperties);
            AutomatedEvaluationRuleId ruleId = AutomatedEvaluationContract.ParseRuleId(
                ReadString(item, "ruleId", Context));
            AutomatedEvaluationRuleDefinition definition =
                AutomatedEvaluationRules.GetDefinition(ruleId);
            if (ruleId != AutomatedEvaluationRules.HumanLimitations[index] ||
                definition.Category != AutomatedEvaluationRuleCategory.HumanLimitation ||
                AutomatedEvaluationContract.ParseRuleCategory(
                    ReadString(item, "category", Context)) != definition.Category ||
                AutomatedEvaluationContract.ParseRuleScope(
                    ReadString(item, "scope", Context)) != definition.Scope ||
                AutomatedEvaluationContract.ParseStatus(
                    ReadString(item, "status", Context)) !=
                    AutomatedEvaluationStatus.NotEvaluated ||
                !string.Equals(
                    ReadString(item, "reasonCode", Context),
                    AutomatedEvaluationEngine.HumanReviewRequiredReasonCode,
                    StringComparison.Ordinal) ||
                ReadOptionalDigest(item, "evidenceDigest", Context) is not null ||
                RequireProperty(item, "target", Context).ValueKind != JsonValueKind.Null)
            {
                throw new JsonException(
                    "An automated-evaluation human limitation is inconsistent.");
            }

            index++;
        }

        if (index != AutomatedEvaluationRules.HumanLimitations.Count)
        {
            throw new JsonException(
                "The automated-evaluation limitations are incomplete.");
        }
    }

    private static ParsedStatusCounts ReadStatusCounts(JsonElement value)
    {
        const string Context = "evaluation.statusCounts";
        RequireExactObject(value, Context, StatusCountProperties);
        long pass = ReadInt64(value, "pass", Context);
        long warning = ReadInt64(value, "warning", Context);
        long fail = ReadInt64(value, "fail", Context);
        long notEvaluated = ReadInt64(value, "notEvaluated", Context);
        int maximum = AutomatedEvaluationRules.All.Count;
        if (pass < 0 || pass > maximum ||
            warning < 0 || warning > maximum ||
            fail < 0 || fail > maximum ||
            notEvaluated < 0 || notEvaluated > maximum ||
            pass + warning + fail + notEvaluated != AutomatedEvaluationRules.All.Count)
        {
            throw new JsonException(
                "The automated-evaluation status counts are invalid.");
        }

        return new ParsedStatusCounts(
            checked((int)pass),
            checked((int)warning),
            checked((int)fail),
            checked((int)notEvaluated));
    }

    private static AutomatedEvaluationTarget? ReadOptionalTarget(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        const string Context = "evaluation.rules[].target";
        RequireExactObject(value, Context, TargetProperties);
        AutomatedEvaluationTargetKind kind = AutomatedEvaluationContract.ParseTargetKind(
            ReadString(value, "kind", Context));
        EntityId<ProjectIdTag> projectId = ReadEntityId<ProjectIdTag>(
            value,
            "projectId",
            Context);
        EntityId<CompositionVersionIdTag> compositionVersionId =
            ReadEntityId<CompositionVersionIdTag>(
                value,
                "compositionVersionId",
                Context);
        ContentDigest compositionVersionDigest = ReadDigest(
            value,
            "compositionVersionDigest",
            Context);
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId =
            ReadOptionalEntityId<AutomatedEvaluationArtifactIdTag>(
                value,
                "artifactId",
                Context);
        ContentDigest? artifactDigest = ReadOptionalDigest(
            value,
            "artifactDigest",
            Context);
        string? timeBasisText = ReadOptionalString(value, "timeBasis", Context);
        RationalTime? time = ReadOptionalRationalTime(
            RequireProperty(value, "time", Context),
            $"{Context}.time");
        string? settingKey = ReadOptionalString(value, "settingKey", Context);
        string? editorKindText = ReadOptionalString(value, "editorKind", Context);

        try
        {
            return kind switch
            {
                AutomatedEvaluationTargetKind.Preview when
                    timeBasisText is not null && time is not null &&
                    settingKey is null && editorKindText is null =>
                    AutomatedEvaluationTarget.Preview(
                        projectId,
                        compositionVersionId,
                        compositionVersionDigest,
                        AutomatedEvaluationContract.ParseTimeBasis(timeBasisText),
                        time.Value,
                        artifactId,
                        artifactDigest),
                AutomatedEvaluationTargetKind.Settings when
                    timeBasisText is null && time is null &&
                    settingKey is not null && editorKindText is null =>
                    AutomatedEvaluationTarget.Settings(
                        projectId,
                        compositionVersionId,
                        compositionVersionDigest,
                        settingKey,
                        artifactId,
                        artifactDigest),
                AutomatedEvaluationTargetKind.Evidence when
                    timeBasisText is null && time is null &&
                    settingKey is null && editorKindText is null =>
                    AutomatedEvaluationTarget.Evidence(
                        projectId,
                        compositionVersionId,
                        compositionVersionDigest,
                        artifactId,
                        artifactDigest),
                AutomatedEvaluationTargetKind.Editor when
                    timeBasisText is null && time is null &&
                    settingKey is null && editorKindText is not null =>
                    AutomatedEvaluationTarget.Editor(
                        projectId,
                        compositionVersionId,
                        compositionVersionDigest,
                        AutomatedEvaluationContract.ParseEditorKind(editorKindText),
                        artifactId,
                        artifactDigest),
                _ => throw new JsonException(
                    "The target fields do not match its kind."),
            };
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The evaluation target is invalid.", exception);
        }
    }

    private static RationalTime? ReadOptionalRationalTime(
        JsonElement value,
        string context)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireExactObject(value, context, RationalTimeProperties);
        long rationalValue = ReadInt64(value, "value", context);
        long rate = ReadInt64(value, "rate", context);
        try
        {
            return new RationalTime(rationalValue, rate);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException("The target rational time is invalid.", exception);
        }
    }

    private readonly record struct ParsedStatusCounts(
        int Pass,
        int Warning,
        int Fail,
        int NotEvaluated);

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

    private static long ReadInt64(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = RequireProperty(parent, propertyName, context);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be an Int64 integer.");
        }

        if (value.TryGetInt64(out long direct))
        {
            return direct;
        }

        string raw = value.GetRawText();
        int exponentIndex = raw.IndexOf('e');
        if (exponentIndex <= 0 ||
            !BigInteger.TryParse(
                raw.AsSpan(0, exponentIndex),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out BigInteger coefficient) ||
            !BigInteger.TryParse(
                raw.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out BigInteger exponent))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be an Int64 integer.");
        }

        BigInteger result;
        if (exponent.Sign >= 0)
        {
            if (exponent > 19)
            {
                throw new JsonException(
                    $"The {context}.{propertyName} value must be an Int64 integer.");
            }

            result = coefficient * BigInteger.Pow(10, (int)exponent);
        }
        else
        {
            BigInteger magnitude = BigInteger.Negate(exponent);
            if (magnitude > 19)
            {
                throw new JsonException(
                    $"The {context}.{propertyName} value must be an Int64 integer.");
            }

            BigInteger divisor = BigInteger.Pow(10, (int)magnitude);
            BigInteger quotient = BigInteger.DivRem(
                coefficient,
                divisor,
                out BigInteger remainder);
            if (!remainder.IsZero)
            {
                throw new JsonException(
                    $"The {context}.{propertyName} value must be an Int64 integer.");
            }

            result = quotient;
        }

        if (result < long.MinValue || result > long.MaxValue)
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be an Int64 integer.");
        }

        return (long)result;
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
                $"The {context}.{propertyName} value must be a nonempty lowercase UUID in D format or null.");
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
        if (text is null)
        {
            return null;
        }

        if (!ContentDigest.TryParse(text, out ContentDigest? digest) ||
            !string.Equals(text, digest.Hex, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a lowercase SHA-256 digest or null.");
        }

        return digest;
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

    private static DateTimeOffset ReadTimestamp(
        JsonElement parent,
        string propertyName,
        string context)
    {
        string text = ReadString(parent, propertyName, context);
        if (!DateTimeOffset.TryParseExact(
                text,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset value))
        {
            throw new JsonException(
                $"The {context}.{propertyName} value must be a round-trip timestamp.");
        }

        return value;
    }
}
