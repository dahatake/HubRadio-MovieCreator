using System.Text.Json;
using HubRadio.App.Security;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Web;

/// <summary>FR-064: native folder dialog abstraction. Returns null when the user cancels; throws when it cannot open.</summary>
internal interface IWorkspaceFolderPicker
{
    Task<string?> PickFolderAsync(string? startFolder, CancellationToken cancellationToken);
}

/// <summary>FR-062: reads and edits the Workspace folder location used from the next start.</summary>
internal sealed class WorkspaceLocationBridgeHandler
{
    private readonly WorkspaceLocationStore store;
    private readonly ISettingsBridgeAuthorization authorization;
    private readonly string currentPath;
    private readonly IWorkspaceFolderPicker? picker;
    private int browsing;

    internal WorkspaceLocationBridgeHandler(
        WorkspaceLocationStore store,
        ISettingsBridgeAuthorization authorization,
        string currentPath,
        IWorkspaceFolderPicker? picker = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        this.currentPath = currentPath;
        this.picker = picker;
    }

    /// <summary>
    /// FR-064: opens the folder dialog and returns the chosen path only. Nothing is saved here;
    /// the existing update method validates and saves. Statuses: selected, cancelled, failed, forbidden, unavailable.
    /// </summary>
    internal async Task<string> HandleBrowseAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != BridgeMethod.SettingsWorkspaceBrowse)
        {
            throw new ArgumentException("A workspace browse request is required.", nameof(request));
        }

        try
        {
            authorization.DemandEdit();
        }
        catch (AuthorizationException)
        {
            return WriteBrowseResponse(request, "forbidden", null);
        }
        catch (Exception)
        {
            return WriteBrowseResponse(request, "unavailable", null);
        }

        if (picker is null)
        {
            return WriteBrowseResponse(request, "unavailable", null);
        }

        if (Interlocked.CompareExchange(ref browsing, 1, 0) != 0)
        {
            return WriteBrowseResponse(request, "failed", null);
        }

        try
        {
            string? start = ResolveStartFolder();
            string? picked = await picker.PickFolderAsync(start, cancellationToken).ConfigureAwait(true);
            if (picked is null)
            {
                return WriteBrowseResponse(request, "cancelled", null);
            }

            return IsUsableFolder(picked)
                ? WriteBrowseResponse(request, "selected", picked)
                : WriteBrowseResponse(request, "failed", null);
        }
        catch (OperationCanceledException)
        {
            return WriteBrowseResponse(request, "cancelled", null);
        }
        catch (Exception)
        {
            return WriteBrowseResponse(request, "failed", null);
        }
        finally
        {
            Volatile.Write(ref browsing, 0);
        }
    }

    private string? ResolveStartFolder()
    {
        try
        {
            string? candidate = store.Resolve().SavedPath ?? currentPath;
            return Directory.Exists(candidate) ? candidate : currentPath;
        }
        catch (Exception)
        {
            return currentPath;
        }
    }

    private static bool IsUsableFolder(string path) =>
        path.Length is > 0 and <= 260 &&
        Path.IsPathFullyQualified(path) &&
        Directory.Exists(path);

    internal static string WriteBrowseFailure(BridgeRequest request, string status) =>
        WriteBrowseResponse(request, status, null);

    private static string WriteBrowseResponse(BridgeRequest request, string status, string? path) =>
        BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", status);
                writer.WriteString("path", path);
                writer.WriteEndObject();
            });

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
