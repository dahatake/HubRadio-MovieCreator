using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.App.Web;

/// <summary>FR-144 bridge parameters: the draft (Project) ID whose proxy is read or made again.</summary>
internal static class ProxyBridgeProtocol
{
    internal static bool HasValidParameters(BridgeMethod method, JsonElement parameters) => method switch
    {
        BridgeMethod.ProxyGet or BridgeMethod.ProxyRegenerate =>
            SettingsBridgeProtocol.HasExactProperties(parameters, ["draftId"]) &&
            SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("draftId")),
        _ => false,
    };
}

internal static class ProxyBridgeJson
{
    internal static string Status(BridgeRequest request, string status) => Write(request, status, null, null);

    internal static string Write(BridgeRequest request, string status, string? reason, string? uri) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            if (reason is null) { writer.WriteNull("reason"); } else { writer.WriteString("reason", reason); }
            if (uri is null) { writer.WriteNull("uri"); } else { writer.WriteString("uri", uri); }
            writer.WriteNumber("height", ProjectProxyService.ProxyHeight);
            writer.WriteEndObject();
        });
}

/// <summary>
/// FR-144: reports the state of a Project's source-video proxy for the settings screen and starts it again.
/// Both need PreviewProject. The response carries only a state, a short reason and the media URI of the proxy:
/// never a path, an exception text or a SID.
/// </summary>
internal sealed class ProxyBridgeHandler
{
    private readonly ProjectProxyService service;
    private readonly Func<bool> canPreview;
    private readonly Func<EntityId<ProjectCreationDraftIdTag>, bool> isListed;

    internal ProxyBridgeHandler(
        ProjectProxyService service,
        Func<bool> canPreview,
        Func<EntityId<ProjectCreationDraftIdTag>, bool> isListed)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.canPreview = canPreview ?? throw new ArgumentNullException(nameof(canPreview));
        this.isListed = isListed ?? throw new ArgumentNullException(nameof(isListed));
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BridgeProtocol.IsProxyMethod(request.Method))
        {
            throw new ArgumentException("A proxy request is required.", nameof(request));
        }

        try
        {
            if (!canPreview())
            {
                return ProxyBridgeJson.Status(request, "forbidden");
            }

            var draftId = EntityId<ProjectCreationDraftIdTag>.Parse(request.Parameters.GetProperty("draftId").GetString()!);
            if (!isListed(draftId))
            {
                return ProxyBridgeJson.Status(request, "unavailable");
            }

            ProjectProxyState state = request.Method == BridgeMethod.ProxyRegenerate
                ? service.Regenerate(draftId)
                : service.Get(draftId);
            return state.Status switch
            {
                ProjectProxyStatus.Ready => ProxyBridgeJson.Write(request, "ready", null,
                    SourceOnlyMediaResponseFactory.ProxyUri(draftId, state.Version!)),
                ProjectProxyStatus.Generating => ProxyBridgeJson.Write(request, "generating", state.Reason, null),
                ProjectProxyStatus.Failed => ProxyBridgeJson.Write(request, "failed", state.Reason, null),
                _ => ProxyBridgeJson.Write(request, "missing", state.Reason, null),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ProxyBridgeJson.Status(request, "unavailable");
        }
    }
}
