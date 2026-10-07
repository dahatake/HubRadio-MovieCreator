using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Web;

/// <summary>FR-062: reads and edits the Workspace folder location used from the next start.</summary>
internal sealed class WorkspaceLocationBridgeHandler
{
    private readonly WorkspaceLocationStore store;
    private readonly ISettingsBridgeAuthorization authorization;
    private readonly string currentPath;

    internal WorkspaceLocationBridgeHandler(
        WorkspaceLocationStore store,
        ISettingsBridgeAuthorization authorization,
        string currentPath)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        this.currentPath = currentPath;
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            switch (request.Method)
            {
                case BridgeMethod.SettingsWorkspaceRead:
                    authorization.DemandRead();
                    break;
                case BridgeMethod.SettingsWorkspaceUpdate:
                    authorization.DemandEdit();
                    JsonElement path = request.Parameters.GetProperty("path");
                    store.Save(path.ValueKind == JsonValueKind.Null ? null : path.GetString());
                    break;
                default:
                    throw new InvalidOperationException(
                        "A workspace location request is required.");
            }

            WorkspaceLocation next = store.Resolve();
            bool restartRequired = !string.Equals(
                next.SavedPath ?? next.DefaultPath,
                currentPath,
                StringComparison.OrdinalIgnoreCase);
            return WriteResponse(request, "available", currentPath, next.SavedPath, next.DefaultPath, restartRequired);
        }
        catch (AuthorizationException)
        {
            return WriteFailure(request, "forbidden");
        }
        catch (ArgumentException)
        {
            return WriteFailure(request, "invalid");
        }
        catch (Exception)
        {
            return WriteFailure(request, "unavailable");
        }
    }

    internal static string WriteFailure(BridgeRequest request, string status) =>
        WriteResponse(request, status, null, null, null, false);

    private static string WriteResponse(
        BridgeRequest request,
        string status,
        string? current,
        string? saved,
        string? defaultPath,
        bool restartRequired) =>
        BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", status);
                writer.WriteString("currentPath", current);
                writer.WriteString("savedPath", saved);
                writer.WriteString("defaultPath", defaultPath);
                writer.WriteBoolean("restartRequired", restartRequired);
                writer.WriteEndObject();
            });
}
