using System.Globalization;
using System.Text.Json;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Security;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Web;

internal sealed class GlobalSettingsBridgeRequestHandler
{
    private readonly DefaultSettingsStore store;
    private readonly GlobalSettingsService service;
    private readonly ISettingsBridgeAuthorization authorization;

    internal GlobalSettingsBridgeRequestHandler(
        DefaultSettingsStore store,
        ISettingsBridgeAuthorization authorization,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        service = new GlobalSettingsService(store, getUtcNow);
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Method switch
            {
                BridgeMethod.SettingsGlobalRead => HandleRead(request),
                BridgeMethod.SettingsGlobalUpdate or
                BridgeMethod.SettingsGlobalReset or
                BridgeMethod.SettingsGlobalResetAll => HandleMutation(request),
                _ => throw new ArgumentException(
                    "A global settings bridge handler requires a global settings request.",
                    nameof(request)),
            };
        }
        catch (AuthorizationException)
        {
            return WriteFailure(request, SettingsBridgeStatus.Forbidden);
        }
        catch (StoreConcurrencyException)
        {
            return WriteFailure(request, SettingsBridgeStatus.Conflict);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return WriteFailure(request, SettingsBridgeStatus.Invalid);
        }
        catch (Exception)
        {
            // Missing or corrupt singleton/profile state is unavailable, not a missing project.
            return WriteFailure(request, SettingsBridgeStatus.Unavailable);
        }
    }

    private string HandleRead(BridgeRequest request)
    {
        authorization.DemandRead();
        return WriteAvailable(request, service.Read());
    }

    private string HandleMutation(BridgeRequest request)
    {
        authorization.DemandEdit();
        StoredGlobalSettings current = service.Read();
        if (!MatchesExpectedIdentity(request, current))
        {
            return WriteFailure(request, SettingsBridgeStatus.Conflict);
        }

        // The service also checks the revision, including no-ops and races at the store write.
        StoredGlobalSettings updated = request.Method switch
        {
            BridgeMethod.SettingsGlobalUpdate => service.Update(
                SettingsBridgeProtocol.ReadKey(request),
                SettingsBridgeProtocol.ReadValue(request),
                current.Revision),
            BridgeMethod.SettingsGlobalReset => service.Reset(
                SettingsBridgeProtocol.ReadKey(request),
                current.Revision),
            BridgeMethod.SettingsGlobalResetAll => service.ResetAll(current.Revision),
            _ => throw new ArgumentException(
                "A global settings mutation is required.",
                nameof(request)),
        };
        return WriteAvailable(request, updated);
    }

    private static bool MatchesExpectedIdentity(
        BridgeRequest request,
        StoredGlobalSettings current)
    {
        JsonElement parameters = request.Parameters;
        DefaultSettingsSnapshot snapshot = current.Snapshot;
        return current.Revision == SettingsBridgeProtocol.ReadExpectedRevision(request) &&
            string.Equals(
                parameters.GetProperty("expectedProfileId").GetString(),
                snapshot.ProfileId.ToString(),
                StringComparison.Ordinal) &&
            string.Equals(
                parameters.GetProperty("expectedProfileVersion").GetString(),
                snapshot.ProfileVersion.ToString(),
                StringComparison.Ordinal) &&
            string.Equals(
                parameters.GetProperty("expectedProfileDigest").GetString(),
                snapshot.ProfileDigest.Hex,
                StringComparison.Ordinal) &&
            string.Equals(
                parameters.GetProperty("expectedSnapshotDigest").GetString(),
                snapshot.ContentDigest.Hex,
                StringComparison.Ordinal);
    }

    private string WriteAvailable(BridgeRequest request, StoredGlobalSettings settings)
    {
        DefaultSettingsSnapshot snapshot = settings.Snapshot;
        DefaultSettingsProfile profile = store.GetProfile(snapshot.ProfileId, snapshot.ProfileVersion) ??
            throw new InvalidOperationException("The bound settings profile is unavailable.");
        if (profile.ContentDigest != snapshot.ProfileDigest)
        {
            throw new InvalidOperationException("The bound settings profile does not match the snapshot.");
        }

        // Global initial values are the bound profile's defaults, not saved overrides.
        DefaultSettingsSnapshot initial = profile.CreateSnapshot();
        return BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", "available");
                writer.WriteStartObject("snapshot");
                writer.WriteNull("projectId");
                writer.WriteString("profileId", profile.Id.ToString());
                writer.WriteString("profileName", profile.Name);
                writer.WriteString("profileVersion", profile.Version.ToString());
                writer.WriteString("profileDigest", profile.ContentDigest.Hex);
                writer.WriteString("initialSnapshotDigest", initial.ContentDigest.Hex);
                writer.WriteString("snapshotDigest", snapshot.ContentDigest.Hex);
                writer.WriteNumber("revision", settings.Revision);
                writer.WriteString(
                    "updatedAt",
                    settings.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteNumber("definitionCount", profile.Definitions.Count);
                writer.WriteStartArray("definitions");
                foreach (DefaultSettingDefinition definition in profile.Definitions)
                {
                    SettingsBridgeJson.WriteDefinition(writer, definition);
                }

                writer.WriteEndArray();
                SettingsBridgeJson.WriteSelections(writer, "initialValues", initial.Values);
                SettingsBridgeJson.WriteSelections(writer, "values", snapshot.Values);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
    }

    private static string WriteFailure(BridgeRequest request, SettingsBridgeStatus status) =>
        SettingsBridgeJson.WriteResponse(request, status, null, null);
}