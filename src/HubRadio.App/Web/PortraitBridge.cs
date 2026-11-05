using System.Text.Json;
using HubRadio.App.StyleTemplates;

namespace HubRadio.App.Web;

/// <summary>
/// FR-137 (6): the bridge protocol for the user's standing pictures (立ち絵). <c>portraits.list</c> takes no parameters;
/// <c>portraits.register</c> takes the character name, the image kind and bytes (base64), the rights status the user chose and an
/// optional note. No path, exception text or SID is ever returned; the app bundles no picture.
/// </summary>
internal static class PortraitBridgeProtocol
{
    internal const int MaximumImageBytes = 4 * 1024 * 1024;
    internal const int MaximumNoteLength = 200;
    internal const int MaximumPortraits = 16;
    private static readonly string[] RegisterKeys = ["name", "imageType", "imageBase64", "rights", "rightsNote"];

    internal static bool HasValidParameters(BridgeMethod method, JsonElement parameters)
    {
        if (method == BridgeMethod.PortraitsList)
        {
            return parameters.ValueKind == JsonValueKind.Object && !parameters.EnumerateObject().Any();
        }

        if (method == BridgeMethod.PortraitsCrop)
        {
            return HasValidCrop(parameters);
        }

        if (method is BridgeMethod.SpeakersGet or BridgeMethod.SpeakersConfirm or BridgeMethod.TemplateAutoGet or BridgeMethod.TemplateAutoTouch)
        {
            return SettingsBridgeProtocol.HasExactProperties(parameters, ["projectId"]) && IsProjectId(parameters.GetProperty("projectId"));
        }

        if (method == BridgeMethod.SpeakersAssign)
        {
            if (!SettingsBridgeProtocol.HasExactProperties(parameters, ["projectId", "cueIndex", "speakerId"])
                || !IsProjectId(parameters.GetProperty("projectId")))
            {
                return false;
            }

            JsonElement cue = parameters.GetProperty("cueIndex");
            JsonElement speaker = parameters.GetProperty("speakerId");
            bool cueOk = cue.ValueKind == JsonValueKind.Null
                || (cue.ValueKind == JsonValueKind.Number && TryReadInt(cue, out int index) && index is >= 0 and < MaximumCueIndex);
            bool speakerOk = speaker.ValueKind == JsonValueKind.Null
                || (speaker.ValueKind == JsonValueKind.String && IsPortraitId(speaker.GetString()));
            // A null cue asks for the system's estimate (a draft); it carries no speaker of its own.
            return cueOk && speakerOk && (cue.ValueKind != JsonValueKind.Null || speaker.ValueKind == JsonValueKind.Null);
        }

        if (method != BridgeMethod.PortraitsRegister || !SettingsBridgeProtocol.HasExactProperties(parameters, RegisterKeys))
        {
            return false;
        }

        JsonElement name = parameters.GetProperty("name");
        if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } text
            || text.Trim().Length is 0 or > CharacterPortraitStore.NameMaximumLength || text.Any(char.IsControl))
        {
            return false;
        }

        if (!IsOneOf(parameters.GetProperty("imageType"), "png", "jpeg", "webp")
            || !IsOneOf(parameters.GetProperty("rights"), "confirmed", "unconfirmed"))
        {
            return false;
        }

        JsonElement image = parameters.GetProperty("imageBase64");
        if (image.ValueKind != JsonValueKind.String || image.GetString() is not { Length: > 0 } base64
            || base64.Length > ((MaximumImageBytes + 2) / 3 * 4))
        {
            return false;
        }

        JsonElement note = parameters.GetProperty("rightsNote");
        return note.ValueKind == JsonValueKind.Null
            || (note.ValueKind == JsonValueKind.String && note.GetString() is { Length: <= MaximumNoteLength } noteText
                && !noteText.Any(char.IsControl));
    }

    internal const int MaximumCueIndex = 10_000;
    internal const int MaximumCropPixels = 32_768;
    internal const int MaximumAssignmentsReturned = 200;
    private static readonly string[] CropKeys = ["sourceId", "name", "x", "y", "width", "height"];

    /// <summary>The request arrives in canonical JSON, where 20 is written 2e1: an integer is read through a decimal.</summary>
    private static bool TryReadInt(JsonElement value, out int result)
    {
        result = 0;
        if (!value.TryGetDecimal(out decimal number) || number != decimal.Truncate(number) || number is < 0 or > int.MaxValue)
        {
            return false;
        }

        result = (int)number;
        return true;
    }

    internal static int ReadInt(JsonElement value) =>
        TryReadInt(value, out int result) ? result : throw new ArgumentException("An integer is required.", nameof(value));

    private static bool IsProjectId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out _);

    private static bool IsPortraitId(string? text) =>
        text is { Length: > 0 and <= 64 } && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool HasValidCrop(JsonElement parameters)
    {
        if (!SettingsBridgeProtocol.HasExactProperties(parameters, CropKeys))
        {
            return false;
        }

        JsonElement source = parameters.GetProperty("sourceId");
        JsonElement name = parameters.GetProperty("name");
        if (source.ValueKind != JsonValueKind.String || !IsPortraitId(source.GetString())
            || name.ValueKind != JsonValueKind.String || name.GetString() is not { } text
            || text.Trim().Length is 0 or > CharacterPortraitStore.NameMaximumLength || text.Any(char.IsControl))
        {
            return false;
        }

        foreach (string key in new[] { "x", "y", "width", "height" })
        {
            JsonElement number = parameters.GetProperty(key);
            if (number.ValueKind != JsonValueKind.Number || !TryReadInt(number, out int pixels)
                || pixels < 0 || pixels > MaximumCropPixels)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOneOf(JsonElement value, params string[] allowed) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text && allowed.Contains(text, StringComparer.Ordinal);

    internal static string WireName(PortraitRightsStatus rights) => rights switch
    {
        PortraitRightsStatus.Confirmed => "confirmed",
        PortraitRightsStatus.Unconfirmed => "unconfirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(rights)),
    };
}

internal static class PortraitBridgeJson
{
    internal static string Unavailable(BridgeRequest request) => Write(request, "unavailable", []);

    internal static string Status(BridgeRequest request, string status, IReadOnlyList<CharacterPortrait> portraits) =>
        Write(request, status, portraits);

    private static string Write(BridgeRequest request, string status, IReadOnlyList<CharacterPortrait> portraits) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteNumber("portraitCount", portraits.Count);
            writer.WriteStartArray("portraits");
            foreach (CharacterPortrait portrait in portraits.Take(PortraitBridgeProtocol.MaximumPortraits))
            {
                writer.WriteStartObject();
                writer.WriteString("id", portrait.Id);
                writer.WriteString("name", portrait.Name);
                writer.WriteString("rights", PortraitBridgeProtocol.WireName(portrait.Rights));
                writer.WriteString("subtitleColor", portrait.SubtitleColor);
                if (portrait.RightsNote is null) writer.WriteNull("rightsNote"); else writer.WriteString("rightsNote", portrait.RightsNote);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
}

/// <summary>FR-145: reads (and, on a manual choice, touches) the automatic template selection of a Project (null when it has none).</summary>
internal sealed record TemplateAutoSelectionAccess(
    Func<Guid, TemplateAutoSelectionRecord?> Read,
    Func<Guid, TemplateAutoSelectionRecord?> Touch,
    Func<Guid, string?>? ReadOrigin = null);

/// <summary>FR-145: the closed result of templateAuto.get / templateAuto.touch (state, template, the Japanese reason, the origin of the Project's latest Job; no path or SID).</summary>
internal static class TemplateAutoBridgeJson
{
    internal static string Write(BridgeRequest request, string status, TemplateAutoSelectionRecord? record, string? jobOrigin = null) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteString("state", record is null ? "none" : record.StateName);
            if (record?.TemplateId is { } id && record.State == TemplateAutoSelectionState.Selected)
            {
                writer.WriteString("templateId", id);
            }
            else
            {
                writer.WriteNull("templateId");
            }

            writer.WriteString("reason", record?.Reason ?? "");
            // FR-145: the origin recorded for the latest Job: auto / manual / none (never any other value), or null when no Job has one yet.
            if (jobOrigin is JobTemplateOrigin.Auto or JobTemplateOrigin.Manual or JobTemplateOrigin.None)
            {
                writer.WriteString("jobOrigin", jobOrigin);
            }
            else
            {
                writer.WriteNull("jobOrigin");
            }

            writer.WriteEndObject();
        });
}

/// <summary>FR-140: the closed result of the speaker-assignment methods (state label, draft/confirmed, assignments; no path or SID).</summary>
internal static class SpeakerBridgeJson
{
    internal static string Write(BridgeRequest request, string status, CueSpeakerAssignmentSet? set) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteString("state", set is null ? "none" : set.IsConfirmed ? "confirmed" : "draft");
            writer.WriteString("label", set is null ? "未割り当て" : set.Label);
            writer.WriteBoolean("estimated", set?.Estimated ?? false);
            writer.WriteStartArray("assignments");
            foreach (CueSpeakerAssignment item in (set?.Assignments ?? []).Take(PortraitBridgeProtocol.MaximumAssignmentsReturned))
            {
                writer.WriteStartObject();
                writer.WriteNumber("cueIndex", item.CueIndex);
                writer.WriteString("speakerId", item.SpeakerId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
}

/// <summary>
/// FR-137 (6): registers and lists the standing pictures in the app's own directory (<see cref="CharacterPortraitStore"/>). The
/// rights status the user chose (確認済み / 未確認) is saved with the picture (NFR-BR-004). Registering needs the same permission as
/// composition export; listing is read-only.
/// </summary>
internal sealed class PortraitBridgeHandler
{
    private readonly CharacterPortraitStore store;
    private readonly Func<bool> isAuthorized;
    private readonly TimeProvider timeProvider;
    private readonly string? speakerAssignmentRoot;
    private readonly TemplateAutoSelectionAccess? templateAuto;

    internal PortraitBridgeHandler(CharacterPortraitStore store, Func<bool> isAuthorized, TimeProvider timeProvider,
        string? speakerAssignmentRoot = null, TemplateAutoSelectionAccess? templateAuto = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.isAuthorized = isAuthorized ?? throw new ArgumentNullException(nameof(isAuthorized));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.speakerAssignmentRoot = speakerAssignmentRoot;
        this.templateAuto = templateAuto;
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool isTemplateAuto = request.Method is BridgeMethod.TemplateAutoGet or BridgeMethod.TemplateAutoTouch;
        try
        {
            if (!PortraitBridgeProtocol.HasValidParameters(request.Method, request.Parameters))
            {
                return isTemplateAuto
                    ? TemplateAutoBridgeJson.Write(request, "invalid", null)
                    : request.Method is BridgeMethod.SpeakersGet or BridgeMethod.SpeakersAssign or BridgeMethod.SpeakersConfirm
                    ? SpeakerBridgeJson.Write(request, "invalid", null)
                    : PortraitBridgeJson.Status(request, "invalid", Safe());
            }

            if (isTemplateAuto)
            {
                return HandleTemplateAuto(request);
            }

            if (request.Method is BridgeMethod.SpeakersGet or BridgeMethod.SpeakersAssign or BridgeMethod.SpeakersConfirm)
            {
                return HandleSpeakers(request);
            }

            if (request.Method == BridgeMethod.PortraitsList)
            {
                return PortraitBridgeJson.Status(request, "ok", store.List());
            }

            if (request.Method is not (BridgeMethod.PortraitsRegister or BridgeMethod.PortraitsCrop))
            {
                throw new ArgumentException("A portrait request is required.", nameof(request));
            }

            if (!IsAuthorized())
            {
                return PortraitBridgeJson.Status(request, "forbidden", Safe());
            }

            return request.Method == BridgeMethod.PortraitsCrop ? Crop(request) : Register(request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ArgumentException)
        {
            return isTemplateAuto
                ? TemplateAutoBridgeJson.Write(request, "unavailable", null)
                : request.Method is BridgeMethod.SpeakersGet or BridgeMethod.SpeakersAssign or BridgeMethod.SpeakersConfirm
                ? SpeakerBridgeJson.Write(request, "unavailable", null)
                : PortraitBridgeJson.Unavailable(request);
        }
    }

    /// <summary>
    /// FR-145: <c>templateAuto.get</c> reads the automatic template selection of a Project (read-only; the user sees 解析中, the chosen
    /// template and the reason, or the reason none was chosen). <c>templateAuto.touch</c> tells the host the user chose a template themself:
    /// an open selection becomes 取消 and is never written over a manual choice. Touching needs the same permission as creating a Job.
    /// </summary>
    private string HandleTemplateAuto(BridgeRequest request)
    {
        if (templateAuto is null)
        {
            return TemplateAutoBridgeJson.Write(request, "unavailable", null);
        }

        var projectId = Guid.ParseExact(request.Parameters.GetProperty("projectId").GetString()!, "D");
        if (request.Method == BridgeMethod.TemplateAutoGet)
        {
            return TemplateAutoBridgeJson.Write(request, "ok", templateAuto.Read(projectId), templateAuto.ReadOrigin?.Invoke(projectId));
        }

        return IsAuthorized()
            ? TemplateAutoBridgeJson.Write(request, "ok", templateAuto.Touch(projectId), templateAuto.ReadOrigin?.Invoke(projectId))
            : TemplateAutoBridgeJson.Write(request, "forbidden", templateAuto.Read(projectId), templateAuto.ReadOrigin?.Invoke(projectId));
    }

    /// <summary>FR-140: cuts one character out of a registered image; the cut-out inherits the rights confirmation of the source.</summary>
    private string Crop(BridgeRequest request)
    {
        JsonElement p = request.Parameters;
        try
        {
            _ = store.RegisterCutout(p.GetProperty("sourceId").GetString()!, p.GetProperty("name").GetString()!,
                new PortraitCropRect(PortraitBridgeProtocol.ReadInt(p.GetProperty("x")), PortraitBridgeProtocol.ReadInt(p.GetProperty("y")),
                                    PortraitBridgeProtocol.ReadInt(p.GetProperty("width")), PortraitBridgeProtocol.ReadInt(p.GetProperty("height"))), timeProvider.GetUtcNow());
            return PortraitBridgeJson.Status(request, "ok", store.List());
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            // An unknown source, a duplicate name or a range outside the image.
            return PortraitBridgeJson.Status(request, "invalid", Safe());
        }
    }

    /// <summary>
    /// FR-140: the speaker assignment of a Project. <c>speakers.assign</c> with no cue asks for the system's estimate (a draft);
    /// with a cue it changes (or clears) that cue's speaker (a draft again); <c>speakers.confirm</c> is the user's confirmation.
    /// A draft is never reported as confirmed. Changing needs the same permission as registering a portrait.
    /// </summary>
    private string HandleSpeakers(BridgeRequest request)
    {
        if (speakerAssignmentRoot is null)
        {
            return SpeakerBridgeJson.Write(request, "unavailable", null);
        }

        JsonElement p = request.Parameters;
        string directory = Path.Combine(speakerAssignmentRoot, Guid.ParseExact(p.GetProperty("projectId").GetString()!, "D").ToString("D"));
        if (request.Method != BridgeMethod.SpeakersGet && !IsAuthorized())
        {
            return SpeakerBridgeJson.Write(request, "forbidden", CueSpeakerAssignmentStore.Load(directory));
        }

        IReadOnlyList<CharacterPortrait> characters = store.List();
        CueSpeakerAssignmentSet? current = CueSpeakerAssignmentStore.Load(directory);
        if (request.Method == BridgeMethod.SpeakersGet)
        {
            return SpeakerBridgeJson.Write(request, "ok", current);
        }

        CueSpeakerAssignmentSet next;
        if (request.Method == BridgeMethod.SpeakersConfirm)
        {
            if (current is null)
            {
                return SpeakerBridgeJson.Write(request, "invalid", null);
            }

            next = current.Confirm();
        }
        else if (p.GetProperty("cueIndex").ValueKind == JsonValueKind.Null)
        {
            if (characters.Count == 0)
            {
                return SpeakerBridgeJson.Write(request, "invalid", current);
            }

            // The cue count is known only when the Job runs: this is a draft the turn-taking estimate expands over the cues then.
            next = CueSpeakerAssignmentSet.Estimate(0, characters);
        }
        else
        {
            string? speaker = p.GetProperty("speakerId").GetString();
            if (speaker is not null && characters.All(item => item.Id != speaker))
            {
                return SpeakerBridgeJson.Write(request, "invalid", current);
            }

            next = (current ?? CueSpeakerAssignmentSet.Empty).Assign(PortraitBridgeProtocol.ReadInt(p.GetProperty("cueIndex")), speaker);
        }

        CueSpeakerAssignmentStore.Save(directory, next);
        return SpeakerBridgeJson.Write(request, "ok", next);
    }

    private string Register(BridgeRequest request)
    {
        JsonElement parameters = request.Parameters;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(parameters.GetProperty("imageBase64").GetString()!);
        }
        catch (FormatException)
        {
            return PortraitBridgeJson.Status(request, "invalid", Safe());
        }

        if (bytes.Length is 0 or > PortraitBridgeProtocol.MaximumImageBytes)
        {
            return PortraitBridgeJson.Status(request, "invalid", Safe());
        }

        string extension = parameters.GetProperty("imageType").GetString() switch
        {
            "png" => ".png",
            "jpeg" => ".jpg",
            _ => ".webp",
        };
        PortraitRightsStatus rights = parameters.GetProperty("rights").GetString() == "confirmed"
            ? PortraitRightsStatus.Confirmed : PortraitRightsStatus.Unconfirmed;
        string? note = parameters.GetProperty("rightsNote").GetString();
        string temporary = Path.Combine(Path.GetTempPath(), "hubradio-portrait-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(temporary, bytes);
            _ = store.Register(parameters.GetProperty("name").GetString()!, temporary, rights, timeProvider.GetUtcNow(),
                rightsNote: note);
            return PortraitBridgeJson.Status(request, "ok", store.List());
        }
        catch (ArgumentException)
        {
            // A duplicate name, a picture that is not the image it claims to be, or a bad value.
            return PortraitBridgeJson.Status(request, "invalid", Safe());
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private IReadOnlyList<CharacterPortrait> Safe()
    {
        try
        {
            return store.List();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private bool IsAuthorized()
    {
        try
        {
            return isAuthorized();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }
}
