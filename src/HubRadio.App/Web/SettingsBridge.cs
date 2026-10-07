using System.Globalization;
using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal enum SettingsBridgeStatus
{
    Available,
    Forbidden,
    NotFound,
    Unbound,
    Conflict,
    Invalid,
    Unavailable,
}

internal static class SettingsBridgeProtocol
{
    // CanonicalJson writes integer tokens such as 10 as 1e1. Parse them
    // without floating-point or decimal rounding of fractional input.
    private const NumberStyles RevisionNumberStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    internal static bool HasValidParameters(BridgeMethod method, JsonElement parameters)
    {
        return method switch
        {
            BridgeMethod.SettingsRead =>
                HasExactProperties(parameters, ["projectId"]) &&
                IsCanonicalId(parameters.GetProperty("projectId")),
            BridgeMethod.SettingsUpdate =>
                HasMutationIdentity(parameters, ["key", "value"]) &&
                IsSettingKey(parameters.GetProperty("key")) &&
                IsSettingValue(parameters.GetProperty("value")),
            BridgeMethod.SettingsReset =>
                HasMutationIdentity(parameters, ["key"]) &&
                IsSettingKey(parameters.GetProperty("key")),
            BridgeMethod.SettingsResetAll =>
                HasMutationIdentity(parameters, []),
            BridgeMethod.SettingsGlobalRead =>
                HasExactProperties(parameters, []),
            BridgeMethod.SettingsGlobalUpdate =>
                HasExpectedIdentity(parameters, ["key", "value"]) &&
                IsSettingKey(parameters.GetProperty("key")) &&
                IsSettingValue(parameters.GetProperty("value")),
            BridgeMethod.SettingsGlobalReset =>
                HasExpectedIdentity(parameters, ["key"]) &&
                IsSettingKey(parameters.GetProperty("key")),
            BridgeMethod.SettingsGlobalResetAll =>
                HasExpectedIdentity(parameters, []),
            BridgeMethod.SettingsFileExport =>
                HasFileMutationIdentity(parameters, ["expectedFileSha256"]) &&
                (parameters.GetProperty("expectedFileSha256").ValueKind == JsonValueKind.Null ||
                    IsDigest(parameters.GetProperty("expectedFileSha256"))),
            BridgeMethod.SettingsFilePreview =>
                HasFileMutationIdentity(parameters, []),
            BridgeMethod.SettingsFileApply =>
                HasFileMutationIdentity(parameters, ["expectedFileSha256"]) &&
                IsDigest(parameters.GetProperty("expectedFileSha256")),
            BridgeMethod.SettingsWorkspaceRead =>
                HasExactProperties(parameters, []),
            BridgeMethod.SettingsWorkspaceUpdate =>
                HasExactProperties(parameters, ["path"]) &&
                (parameters.GetProperty("path").ValueKind == JsonValueKind.Null ||
                    IsBoundedText(parameters.GetProperty("path"), 260, allowEmpty: true)),
            _ => false,
        };
    }

    internal static EntityId<ProjectIdTag> ReadProjectId(BridgeRequest request) =>
        EntityId<ProjectIdTag>.Parse(request.Parameters.GetProperty("projectId").GetString()!);

    internal static int ReadExpectedRevision(BridgeRequest request) =>
        int.Parse(
            request.Parameters.GetProperty("expectedRevision").GetRawText(),
            RevisionNumberStyles,
            CultureInfo.InvariantCulture);

    internal static string ReadKey(BridgeRequest request) =>
        request.Parameters.GetProperty("key").GetString()!;

    internal static DefaultSettingValue ReadValue(BridgeRequest request)
    {
        JsonElement value = request.Parameters.GetProperty("value");
        return value.GetProperty("kind").GetString() switch
        {
            "boolean" => DefaultSettingValue.FromBoolean(
                value.GetProperty("booleanValue").GetBoolean()),
            "integer" => DefaultSettingValue.FromInteger(
                long.Parse(
                    value.GetProperty("integerValue").GetString()!,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture)),
            "rational" => DefaultSettingValue.FromRational(
                long.Parse(
                    value.GetProperty("numerator").GetString()!,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture),
                long.Parse(
                    value.GetProperty("denominator").GetString()!,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture)),
            "text" => DefaultSettingValue.FromText(
                value.GetProperty("textValue").GetString()!),
            "textList" => DefaultSettingValue.FromTextList(
                value.GetProperty("textListValue")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
                    .ToArray()),
            _ => throw new InvalidOperationException("Unsupported parsed settings value kind."),
        };
    }

    internal static bool MatchesExpectedIdentity(
        BridgeRequest request,
        StoredProjectSettings settings)
    {
        JsonElement parameters = request.Parameters;
        DefaultSettingsSnapshot current = settings.CurrentSnapshot;
        return settings.Revision == ReadExpectedRevision(request) &&
            string.Equals(
                parameters.GetProperty("expectedProfileId").GetString(),
                current.ProfileId.ToString(),
                StringComparison.Ordinal) &&
            string.Equals(
                parameters.GetProperty("expectedProfileVersion").GetString(),
                current.ProfileVersion.ToString(),
                StringComparison.Ordinal) &&
            string.Equals(
                parameters.GetProperty("expectedProfileDigest").GetString(),
                current.ProfileDigest.Hex,
                StringComparison.Ordinal) &&
            string.Equals(
                parameters.GetProperty("expectedSnapshotDigest").GetString(),
                current.ContentDigest.Hex,
                StringComparison.Ordinal);
    }

    private static bool HasMutationIdentity(
        JsonElement parameters,
        IReadOnlyList<string> additionalProperties) =>
        HasExpectedIdentity(parameters, ["projectId", .. additionalProperties]) &&
        IsCanonicalId(parameters.GetProperty("projectId"));

    private static bool HasFileMutationIdentity(
        JsonElement parameters,
        IReadOnlyList<string> additionalProperties) =>
        HasExpectedIdentity(parameters, ["projectId", .. additionalProperties]) &&
        (parameters.GetProperty("projectId").ValueKind == JsonValueKind.Null ||
            IsCanonicalId(parameters.GetProperty("projectId")));

    private static bool HasExpectedIdentity(
        JsonElement parameters,
        IReadOnlyList<string> additionalProperties)
    {
        string[] properties =
        [
            "expectedRevision",
            "expectedProfileId",
            "expectedProfileVersion",
            "expectedProfileDigest",
            "expectedSnapshotDigest",
            .. additionalProperties,
        ];
        return HasExactProperties(parameters, properties) &&
            IsNonnegativeInt32(parameters.GetProperty("expectedRevision")) &&
            IsCanonicalId(parameters.GetProperty("expectedProfileId")) &&
            IsProfileVersion(parameters.GetProperty("expectedProfileVersion")) &&
            IsDigest(parameters.GetProperty("expectedProfileDigest")) &&
            IsDigest(parameters.GetProperty("expectedSnapshotDigest"));
    }

    private static bool IsSettingValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out JsonElement kind) ||
            kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return kind.GetString() switch
        {
            "boolean" =>
                HasExactProperties(value, ["kind", "booleanValue"]) &&
                value.GetProperty("booleanValue").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" =>
                HasExactProperties(value, ["kind", "integerValue"]) &&
                IsCanonicalInt64(value.GetProperty("integerValue"), allowNegative: true, positive: false),
            "rational" =>
                HasExactProperties(value, ["kind", "numerator", "denominator"]) &&
                IsCanonicalInt64(value.GetProperty("numerator"), allowNegative: true, positive: false) &&
                IsCanonicalInt64(value.GetProperty("denominator"), allowNegative: false, positive: true),
            "text" =>
                HasExactProperties(value, ["kind", "textValue"]) &&
                IsBoundedText(value.GetProperty("textValue"), 4096, allowEmpty: false),
            "textList" =>
                HasExactProperties(value, ["kind", "textListValue"]) &&
                IsTextList(value.GetProperty("textListValue")),
            _ => false,
        };
    }

    private static bool IsTextList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 64)
        {
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (!IsBoundedText(item, 4096, allowEmpty: false) || !found.Add(item.GetString()!))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSettingKey(JsonElement value)
    {
        if (!IsBoundedText(value, 128, allowEmpty: false))
        {
            return false;
        }

        bool atSegmentStart = true;
        foreach (char character in value.GetString()!)
        {
            if (atSegmentStart)
            {
                if (character is < 'a' or > 'z')
                {
                    return false;
                }

                atSegmentStart = false;
            }
            else if (character == '.')
            {
                atSegmentStart = true;
            }
            else if (character is not (>= 'a' and <= 'z') &&
                     character is not (>= 'A' and <= 'Z') &&
                     character is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return !atSegmentStart;
    }

    internal static bool IsCanonicalId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is string text &&
        Guid.TryParseExact(text, "D", out Guid id) &&
        id != Guid.Empty &&
        string.Equals(text, id.ToString("D"), StringComparison.Ordinal);

    internal static bool IsDigest(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is string text &&
        text.Length == 64 &&
        text.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsProfileVersion(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String &&
            SettingsProfileVersion.TryParse(value.GetString(), out SettingsProfileVersion? version) &&
            string.Equals(value.GetString(), version.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNonnegativeInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number &&
        int.TryParse(
            value.GetRawText(),
            RevisionNumberStyles,
            CultureInfo.InvariantCulture,
            out int parsed) &&
        parsed >= 0;

    private static bool IsCanonicalInt64(
        JsonElement value,
        bool allowNegative,
        bool positive)
    {
        if (value.ValueKind != JsonValueKind.String ||
            !long.TryParse(
                value.GetString(),
                allowNegative ? NumberStyles.AllowLeadingSign : NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsed) ||
            !string.Equals(
                value.GetString(),
                parsed.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return false;
        }

        return !positive || parsed > 0;
    }

    private static bool IsBoundedText(JsonElement value, int maximumLength, bool allowEmpty) =>
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is string text &&
        (allowEmpty || text.Length > 0) &&
        text.Length <= maximumLength;

    internal static bool HasExactProperties(
        JsonElement value,
        IReadOnlyList<string> expectedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name, StringComparer.Ordinal) ||
                !found.Add(property.Name))
            {
                return false;
            }
        }

        return found.Count == expectedProperties.Count;
    }
}

internal interface ISettingsBridgeAuthorization
{
    void DemandRead();

    void DemandEdit();
}

internal sealed class SettingsBridgeAuthorization : ISettingsBridgeAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal SettingsBridgeAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, actorProvider.GetCurrentSid)
    {
    }

    internal SettingsBridgeAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        this.authorizationService = authorizationService ??
            throw new ArgumentNullException(nameof(authorizationService));
        this.getCurrentSid = getCurrentSid ??
            throw new ArgumentNullException(nameof(getCurrentSid));
    }

    public void DemandRead()
    {
        authorizationService.Demand(getCurrentSid(), AuthorizationOperation.ViewProject);
    }

    public void DemandEdit()
    {
        authorizationService.DemandAs(
            getCurrentSid(),
            AuthorizationRole.Editor,
            AuthorizationOperation.CreateCompositionVersion);
    }
}

internal sealed class SettingsBridgeRequestHandler
{
    private readonly DefaultSettingsStore store;
    private readonly ISettingsBridgeAuthorization authorization;
    private readonly Func<DateTimeOffset> getUtcNow;

    internal SettingsBridgeRequestHandler(
        DefaultSettingsStore store,
        ISettingsBridgeAuthorization authorization,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Method switch
            {
                BridgeMethod.SettingsRead => HandleRead(request),
                BridgeMethod.SettingsUpdate => HandleUpdate(request),
                BridgeMethod.SettingsReset => HandleReset(request),
                BridgeMethod.SettingsResetAll => HandleResetAll(request),
                _ => throw new ArgumentException(
                    "A settings bridge handler requires a settings request.",
                    nameof(request)),
            };
        }
        catch (AuthorizationException)
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Forbidden, null, null);
        }
        catch (StoreConcurrencyException)
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Conflict, null, null);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Invalid, null, null);
        }
        catch (InvalidOperationException)
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.NotFound, null, null);
        }
        catch (Exception)
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Unavailable, null, null);
        }
    }

    private string HandleRead(BridgeRequest request)
    {
        authorization.DemandRead();
        EntityId<ProjectIdTag> projectId = SettingsBridgeProtocol.ReadProjectId(request);
        ProjectSettingsResult result = store.GetProjectSettings(projectId);
        return !result.IsBound
            ? SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Unbound, null, null)
            : WriteAvailable(request, result.Settings!);
    }

    private string HandleUpdate(BridgeRequest request)
    {
        authorization.DemandEdit();
        StoredProjectSettings? current = ReadCurrentForMutation(request);
        if (current is null)
        {
            return SettingsBridgeJson.WriteResponse(
                request,
                SettingsBridgeStatus.Unbound,
                null,
                null);
        }

        if (!SettingsBridgeProtocol.MatchesExpectedIdentity(request, current))
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Conflict, null, null);
        }

        string key = SettingsBridgeProtocol.ReadKey(request);
        DefaultSettingsProfile profile = RequireProfile(current);
        _ = profile.GetDefinition(key);
        DefaultSettingValue replacement = SettingsBridgeProtocol.ReadValue(request);
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot(
            current.CurrentSnapshot.Values
                .Select(value => new DefaultSettingSelection(
                    value.Key,
                    string.Equals(value.Key, key, StringComparison.Ordinal)
                        ? replacement
                        : value.Value))
                .ToArray());
        if (snapshot.ContentDigest == current.CurrentSnapshot.ContentDigest)
        {
            return WriteAvailable(request, current);
        }

        StoredProjectSettings updated = store.UpdateProjectSettings(
            current.ProjectId,
            snapshot,
            current.Revision,
            NextTimestamp(current));
        return WriteAvailable(request, updated, profile);
    }

    private string HandleReset(BridgeRequest request)
    {
        authorization.DemandEdit();
        StoredProjectSettings? current = ReadCurrentForMutation(request);
        if (current is null)
        {
            return SettingsBridgeJson.WriteResponse(
                request,
                SettingsBridgeStatus.Unbound,
                null,
                null);
        }

        if (!SettingsBridgeProtocol.MatchesExpectedIdentity(request, current))
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Conflict, null, null);
        }

        DefaultSettingsProfile profile = RequireProfile(current);
        string key = SettingsBridgeProtocol.ReadKey(request);
        _ = profile.GetDefinition(key);
        StoredProjectSettings updated = store.ResetProjectSetting(
            current.ProjectId,
            key,
            current.Revision,
            NextTimestamp(current));
        return WriteAvailable(request, updated, profile);
    }

    private string HandleResetAll(BridgeRequest request)
    {
        authorization.DemandEdit();
        StoredProjectSettings? current = ReadCurrentForMutation(request);
        if (current is null)
        {
            return SettingsBridgeJson.WriteResponse(
                request,
                SettingsBridgeStatus.Unbound,
                null,
                null);
        }

        if (!SettingsBridgeProtocol.MatchesExpectedIdentity(request, current))
        {
            return SettingsBridgeJson.WriteResponse(request, SettingsBridgeStatus.Conflict, null, null);
        }

        DefaultSettingsProfile profile = RequireProfile(current);
        StoredProjectSettings updated = store.ResetAllProjectSettings(
            current.ProjectId,
            current.Revision,
            NextTimestamp(current));
        return WriteAvailable(request, updated, profile);
    }

    private StoredProjectSettings? ReadCurrentForMutation(BridgeRequest request)
    {
        ProjectSettingsResult result = store.GetProjectSettings(
            SettingsBridgeProtocol.ReadProjectId(request));
        return result.IsBound ? result.Settings! : null;
    }

    private DefaultSettingsProfile RequireProfile(StoredProjectSettings settings) =>
        store.GetProfile(
            settings.CurrentSnapshot.ProfileId,
            settings.CurrentSnapshot.ProfileVersion) ??
        throw new InvalidOperationException("The bound settings profile is unavailable.");

    private DateTimeOffset NextTimestamp(StoredProjectSettings current)
    {
        DateTimeOffset now = getUtcNow();
        return now < current.UpdatedAt ? current.UpdatedAt : now;
    }

    private string WriteAvailable(
        BridgeRequest request,
        StoredProjectSettings settings,
        DefaultSettingsProfile? profile = null)
    {
        profile ??= RequireProfile(settings);
        return SettingsBridgeJson.WriteResponse(
            request,
            SettingsBridgeStatus.Available,
            settings,
            profile);
    }

}

internal static class SettingsBridgeJson
{
    internal static string WriteResponse(
        BridgeRequest request,
        SettingsBridgeStatus status,
        StoredProjectSettings? settings,
        DefaultSettingsProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(status) ||
            (status == SettingsBridgeStatus.Available) != (settings is not null && profile is not null))
        {
            throw new ArgumentException("The settings bridge result is inconsistent.", nameof(status));
        }

        return BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", FormatStatus(status));
                writer.WritePropertyName("snapshot");
                if (settings is null || profile is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteSnapshot(writer, settings, profile);
                }

                writer.WriteEndObject();
            });
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        StoredProjectSettings settings,
        DefaultSettingsProfile profile)
    {
        DefaultSettingsSnapshot snapshot = settings.CurrentSnapshot;
        writer.WriteStartObject();
        writer.WriteString("projectId", settings.ProjectId.ToString());
        writer.WriteString("profileId", profile.Id.ToString());
        writer.WriteString("profileName", profile.Name);
        writer.WriteString("profileVersion", profile.Version.ToString());
        writer.WriteString("profileDigest", profile.ContentDigest.Hex);
        writer.WriteString("initialSnapshotDigest", settings.InitialSnapshot.ContentDigest.Hex);
        writer.WriteString("snapshotDigest", snapshot.ContentDigest.Hex);
        writer.WriteNumber("revision", settings.Revision);
        writer.WriteString(
            "updatedAt",
            settings.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("definitionCount", profile.Definitions.Count);
        writer.WriteStartArray("definitions");
        foreach (DefaultSettingDefinition definition in profile.Definitions)
        {
            WriteDefinition(writer, definition);
        }

        writer.WriteEndArray();
        WriteSelections(writer, "initialValues", settings.InitialSnapshot.Values);
        WriteSelections(writer, "values", snapshot.Values);
        writer.WriteEndObject();
    }

    internal static void WriteDefinition(Utf8JsonWriter writer, DefaultSettingDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString("key", definition.Key);
        writer.WriteString("group", FormatGroup(definition.Group));
        writer.WriteBoolean("isAdvanced", definition.IsAdvanced);
        writer.WriteString("displayName", definition.DisplayName);
        writer.WriteString("description", definition.Description);
        writer.WriteString("unit", FormatUnit(definition.Unit));
        writer.WritePropertyName("defaultValue");
        WriteValue(writer, definition.DefaultValue);
        writer.WritePropertyName("minimum");
        WriteNullableValue(writer, definition.Minimum);
        writer.WritePropertyName("maximum");
        WriteNullableValue(writer, definition.Maximum);
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
            writer.WriteString("kind", FormatEvidenceKind(evidence.Kind));
            writer.WriteString("sourceId", evidence.SourceId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static void WriteSelections(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<DefaultSettingSelection> selections)
    {
        writer.WriteStartArray(propertyName);
        foreach (DefaultSettingSelection selection in selections)
        {
            writer.WriteStartObject();
            writer.WriteString("key", selection.Key);
            writer.WritePropertyName("value");
            WriteValue(writer, selection.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableValue(Utf8JsonWriter writer, DefaultSettingValue? value)
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
        switch (value.Kind)
        {
            case DefaultSettingValueKind.Boolean:
                writer.WriteString("kind", "boolean");
                writer.WriteBoolean("booleanValue", value.BooleanValue!.Value);
                break;
            case DefaultSettingValueKind.Integer:
                writer.WriteString("kind", "integer");
                writer.WriteString("integerValue", value.IntegerValue!.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case DefaultSettingValueKind.Rational:
                writer.WriteString("kind", "rational");
                writer.WriteString("numerator", value.RationalValue!.Value.Numerator.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("denominator", value.RationalValue!.Value.Denominator.ToString(CultureInfo.InvariantCulture));
                break;
            case DefaultSettingValueKind.Text:
                writer.WriteString("kind", "text");
                writer.WriteString("textValue", value.TextValue);
                break;
            case DefaultSettingValueKind.TextList:
                writer.WriteString("kind", "textList");
                writer.WriteStartArray("textListValue");
                foreach (string item in value.TextListValue)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("Unknown default-setting value kind.");
        }

        writer.WriteEndObject();
    }

    private static string FormatStatus(SettingsBridgeStatus status) => status switch
    {
        SettingsBridgeStatus.Available => "available",
        SettingsBridgeStatus.Forbidden => "forbidden",
        SettingsBridgeStatus.NotFound => "notFound",
        SettingsBridgeStatus.Unbound => "unbound",
        SettingsBridgeStatus.Conflict => "conflict",
        SettingsBridgeStatus.Invalid => "invalid",
        SettingsBridgeStatus.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string FormatGroup(DefaultSettingGroup group) => group switch
    {
        DefaultSettingGroup.ProjectAndOutput => "projectAndOutput",
        DefaultSettingGroup.Editing => "editing",
        DefaultSettingGroup.SubtitlesAndGraphics => "subtitlesAndGraphics",
        DefaultSettingGroup.AudioAndAssets => "audioAndAssets",
        DefaultSettingGroup.PrivacyAndStorage => "privacyAndStorage",
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private static string FormatUnit(DefaultSettingUnit unit) => unit switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private static string FormatEvidenceKind(DefaultSettingEvidenceKind kind) => kind switch
    {
        DefaultSettingEvidenceKind.Specification => "specification",
        DefaultSettingEvidenceKind.OfficialRecommendation => "officialRecommendation",
        DefaultSettingEvidenceKind.PeerReviewedFinding => "peerReviewedFinding",
        DefaultSettingEvidenceKind.PracticalGuidance => "practicalGuidance",
        DefaultSettingEvidenceKind.CurrentImplementation => "currentImplementation",
        DefaultSettingEvidenceKind.ProductDecision => "productDecision",
        DefaultSettingEvidenceKind.UserDecision => "userDecision",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}