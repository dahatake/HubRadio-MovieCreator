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

    internal PortraitBridgeHandler(CharacterPortraitStore store, Func<bool> isAuthorized, TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.isAuthorized = isAuthorized ?? throw new ArgumentNullException(nameof(isAuthorized));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (!PortraitBridgeProtocol.HasValidParameters(request.Method, request.Parameters))
            {
                return PortraitBridgeJson.Status(request, "invalid", Safe());
            }

            if (request.Method == BridgeMethod.PortraitsList)
            {
                return PortraitBridgeJson.Status(request, "ok", store.List());
            }

            if (request.Method != BridgeMethod.PortraitsRegister)
            {
                throw new ArgumentException("A portrait request is required.", nameof(request));
            }

            if (!IsAuthorized())
            {
                return PortraitBridgeJson.Status(request, "forbidden", Safe());
            }

            return Register(request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ArgumentException)
        {
            return PortraitBridgeJson.Unavailable(request);
        }
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
