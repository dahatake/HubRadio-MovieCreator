using System.Globalization;
using System.Text.Json;
using HubRadio.App.Audit;
using HubRadio.App.Security;
using HubRadio.Core.Audit;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.App.Web;

/// <summary>NFR-BR-005 / AC-126 bridge parameters: a Project, plus a destination and label text when adding.</summary>
internal static class DisclosureBridgeProtocol
{
    internal const int MaximumDestinationLength = 64;
    internal const int MaximumLabelLength = 200;
    internal const int MaximumListedItems = 100;

    internal static bool HasValidParameters(BridgeMethod method, JsonElement parameters) => method switch
    {
        BridgeMethod.DisclosureList => SettingsBridgeProtocol.HasExactProperties(parameters, ["projectId"]) &&
            SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("projectId")),
        BridgeMethod.DisclosureAddLabel =>
            SettingsBridgeProtocol.HasExactProperties(parameters, ["projectId", "destination", "labelText"]) &&
            SettingsBridgeProtocol.IsCanonicalId(parameters.GetProperty("projectId")) &&
            IsText(parameters.GetProperty("destination"), MaximumDestinationLength) &&
            IsText(parameters.GetProperty("labelText"), MaximumLabelLength),
        _ => false,
    };

    private static bool IsText(JsonElement value, int maximumLength) =>
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is string text &&
        !string.IsNullOrWhiteSpace(text) &&
        text.Length <= maximumLength;
}

internal static class DisclosureBridgeJson
{
    internal static string Unavailable(BridgeRequest request) => Write(request, "unavailable", [], []);

    internal static string Status(BridgeRequest request, string status) => Write(request, status, [], []);

    internal static string Write(
        BridgeRequest request,
        string status,
        IReadOnlyList<AiGeneratedElementRecord> elements,
        IReadOnlyList<AiDisclosureLabelRecord> labels) =>
        BridgeProtocol.CreateResultResponse(request, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteStartArray("elements");
            foreach (AiGeneratedElementRecord element in elements)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", element.Kind.ToString());
                writer.WriteString("elementRef", element.ElementRef);
                writer.WriteString("provider", element.Provider);
                writer.WriteString("modelIdentifier", element.ModelIdentifier);
                writer.WriteString("modelVersion", element.ModelVersion);
                writer.WriteString("recordedAt", element.RecordedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("labels");
            foreach (AiDisclosureLabelRecord label in labels)
            {
                writer.WriteStartObject();
                writer.WriteString("destination", label.Destination);
                writer.WriteString("labelText", label.LabelText);
                writer.WriteString("addedAt", label.AddedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
}

/// <summary>
/// NFR-BR-005 / AC-126: lists the AI-generated or substantially edited elements and the destination labels
/// of a Project, and adds a label. Listing needs ViewProject; adding needs ExportComposition. The added
/// label travels with the delivery set (ai-disclosure.txt) through <see cref="AiGenerationDisclosureService"/>.
/// </summary>
internal sealed class DisclosureBridgeHandler
{
    private readonly AiGenerationDisclosureService service;
    private readonly AuthorizationService authorization;
    private readonly Func<string> currentActorSid;
    private readonly TimeProvider timeProvider;

    internal DisclosureBridgeHandler(
        AiGenerationDisclosureService service,
        AuthorizationService authorization,
        Func<string> currentActorSid,
        TimeProvider? timeProvider = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.currentActorSid = currentActorSid ?? throw new ArgumentNullException(nameof(currentActorSid));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal string Handle(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BridgeProtocol.IsDisclosureMethod(request.Method))
        {
            throw new ArgumentException("A disclosure request is required.", nameof(request));
        }

        try
        {
            JsonElement parameters = request.Parameters;
            var projectId = EntityId<ProjectIdTag>.Parse(parameters.GetProperty("projectId").GetString()!);
            string sid = currentActorSid();
            if (request.Method == BridgeMethod.DisclosureAddLabel)
            {
                if (!authorization.CanPerform(sid, AuthorizationOperation.ExportComposition))
                {
                    return DisclosureBridgeJson.Status(request, "forbidden");
                }

                try
                {
                    _ = service.AddDestinationLabel(
                        EntityId<AuditEventIdTag>.New(),
                        projectId,
                        parameters.GetProperty("destination").GetString()!.Trim(),
                        parameters.GetProperty("labelText").GetString()!.Trim(),
                        sid,
                        ReviewActorRole.Editor,
                        timeProvider.GetUtcNow());
                }
                catch (ArgumentException)
                {
                    return DisclosureBridgeJson.Status(request, "invalid");
                }
            }
            else if (!authorization.CanPerform(sid, AuthorizationOperation.ViewProject))
            {
                return DisclosureBridgeJson.Status(request, "forbidden");
            }

            IReadOnlyList<AiGeneratedElementRecord> elements = service.ListAiElements(projectId);
            IReadOnlyList<AiDisclosureLabelRecord> labels = service.ListLabels(projectId);
            return DisclosureBridgeJson.Write(
                request,
                request.Method == BridgeMethod.DisclosureAddLabel ? "added" : "listed",
                Newest(elements), Newest(labels));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DisclosureBridgeJson.Unavailable(request);
        }
    }

    private static IReadOnlyList<T> Newest<T>(IReadOnlyList<T> items) =>
        items.Count <= DisclosureBridgeProtocol.MaximumListedItems
            ? items
            : items.Skip(items.Count - DisclosureBridgeProtocol.MaximumListedItems).ToArray();
}
