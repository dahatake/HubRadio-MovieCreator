using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Audit;

/// <summary>NFR-BR-005: what AI generated or substantially edited.</summary>
public enum AiGeneratedElementKind
{
    SpeakerEstimate,
    AutoSubtitle,
    TopicSummaryDraft,
    SubstantialEdit,
}

public sealed record AiGeneratedElementRecord(
    EntityId<AuditEventIdTag> EventId,
    AiGeneratedElementKind Kind,
    string ElementRef,
    EntityId<ModelRunIdTag> ModelRunId,
    string Provider,
    string ModelIdentifier,
    string ModelVersion,
    DateTimeOffset RecordedAt);

public sealed record AiDisclosureLabelRecord(
    EntityId<AuditEventIdTag> EventId,
    string Destination,
    string LabelText,
    DateTimeOffset AddedAt);

/// <summary>The AI elements and the labels for one destination, as they are reflected into a deliverable.</summary>
public sealed class AiDisclosure
{
    public const string ArtifactFileName = "ai-disclosure.txt";

    internal AiDisclosure(
        string destination,
        IReadOnlyList<string> labelTexts,
        IReadOnlyList<AiGeneratedElementRecord> elements)
    {
        Destination = destination;
        LabelTexts = labelTexts;
        Elements = elements;
    }

    public string Destination { get; }

    public IReadOnlyList<string> LabelTexts { get; }

    public IReadOnlyList<AiGeneratedElementRecord> Elements { get; }

    public bool HasContent => Elements.Count > 0 && LabelTexts.Count > 0;

    /// <summary>The labels followed by one line per AI element; empty when nothing must be shown.</summary>
    public string ToText()
    {
        if (!HasContent)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (string label in LabelTexts)
        {
            _ = builder.Append(label).Append('\n');
        }

        foreach (AiGeneratedElementRecord element in Elements)
        {
            _ = builder
                .Append(element.Kind)
                .Append(' ')
                .Append(element.ElementRef)
                .Append(' ')
                .Append(element.ModelIdentifier)
                .Append(' ')
                .Append(element.ModelVersion)
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The description with the labels appended (unchanged when nothing must be shown).</summary>
    public string ApplyToDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return LabelTexts.Count == 0 || Elements.Count == 0
            ? description
            : description.TrimEnd() + "\n\n" + string.Join("\n", LabelTexts);
    }

    /// <summary>Writes the disclosure next to the deliverable; returns null when nothing must be shown.</summary>
    public string? WriteArtifact(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!HasContent)
        {
            return null;
        }

        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ArtifactFileName);
        File.WriteAllText(path, ToText(), new UTF8Encoding(false));
        return path;
    }
}

public sealed class AiGenerationDisclosureService
{
    private const int MaximumReferenceLength = 128;
    private const int MaximumLabelLength = 200;
    private const string SchemaVersion = "1.0.0";

    private readonly ReviewStore _reviewStore;

    public AiGenerationDisclosureService(ReviewStore reviewStore)
    {
        ArgumentNullException.ThrowIfNull(reviewStore);
        _reviewStore = reviewStore;
    }

    public AuditEvent RecordAiElement(
        EntityId<AuditEventIdTag> eventId,
        EntityId<ProjectIdTag> projectId,
        AiGeneratedElementKind kind,
        string elementRef,
        ModelRun modelRun,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(modelRun);
        ArgumentNullException.ThrowIfNull(actorId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        string reference = RequireText(elementRef, MaximumReferenceLength, nameof(elementRef));
        string json = Write(writer =>
        {
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("elementKind", kind.ToString());
            writer.WriteString("elementRef", reference);
            writer.WriteString("modelRunId", modelRun.Id.ToString());
            writer.WriteString("provider", modelRun.Provider);
            writer.WriteString("modelIdentifier", modelRun.ModelIdentifier);
            writer.WriteString("modelVersion", modelRun.ModelVersion);
        });
        return Append(
            eventId, AuditEventKind.AiGeneratedElementRecorded, projectId,
            actorId, actorRole, occurredAt, json);
    }

    public AuditEvent AddDestinationLabel(
        EntityId<AuditEventIdTag> eventId,
        EntityId<ProjectIdTag> projectId,
        string destination,
        string labelText,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(actorId);
        string target = RequireText(destination, 64, nameof(destination));
        string label = RequireText(labelText, MaximumLabelLength, nameof(labelText));
        string json = Write(writer =>
        {
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("destination", target);
            writer.WriteString("labelText", label);
        });
        return Append(
            eventId, AuditEventKind.AiDisclosureLabelAdded, projectId,
            actorId, actorRole, occurredAt, json);
    }

    /// <summary>The AI-generated or substantially edited elements recorded for the project, oldest first.</summary>
    public IReadOnlyList<AiGeneratedElementRecord> ListAiElements(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        var result = new List<AiGeneratedElementRecord>();
        foreach (StoredAuditEvent stored in _reviewStore.GetProjectAuditEvents(projectId)
            .Where(static e => e.Kind == AuditEventKind.AiGeneratedElementRecorded))
        {
            using JsonDocument document = JsonDocument.Parse(stored.Reason);
            JsonElement root = document.RootElement;
            result.Add(new AiGeneratedElementRecord(
                stored.Id,
                Enum.Parse<AiGeneratedElementKind>(root.GetProperty("elementKind").GetString()!),
                root.GetProperty("elementRef").GetString()!,
                EntityId<ModelRunIdTag>.Parse(root.GetProperty("modelRunId").GetString()!),
                root.GetProperty("provider").GetString()!,
                root.GetProperty("modelIdentifier").GetString()!,
                root.GetProperty("modelVersion").GetString()!,
                stored.OccurredAt));
        }

        return result;
    }

    public IReadOnlyList<AiDisclosureLabelRecord> ListLabels(
        EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        var result = new List<AiDisclosureLabelRecord>();
        foreach (StoredAuditEvent stored in _reviewStore.GetProjectAuditEvents(projectId)
            .Where(static e => e.Kind == AuditEventKind.AiDisclosureLabelAdded))
        {
            using JsonDocument document = JsonDocument.Parse(stored.Reason);
            JsonElement root = document.RootElement;
            result.Add(new AiDisclosureLabelRecord(
                stored.Id,
                root.GetProperty("destination").GetString()!,
                root.GetProperty("labelText").GetString()!,
                stored.OccurredAt));
        }

        return result;
    }

    /// <summary>The elements and the labels registered for <paramref name="destination"/>.</summary>
    public AiDisclosure BuildDisclosure(
        EntityId<ProjectIdTag> projectId,
        string destination)
    {
        string target = RequireText(destination, 64, nameof(destination));
        string[] labels = ListLabels(projectId)
            .Where(label => string.Equals(
                label.Destination, target, StringComparison.OrdinalIgnoreCase))
            .Select(static label => label.LabelText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AiDisclosure(target, labels, ListAiElements(projectId));
    }

    private AuditEvent Append(
        EntityId<AuditEventIdTag> eventId,
        AuditEventKind kind,
        EntityId<ProjectIdTag> projectId,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset occurredAt,
        string json)
    {
        var auditEvent = new AuditEvent(
            eventId, kind, actorId, actorRole, occurredAt, json, projectId);
        _reviewStore.AppendAuditEvent(auditEvent);
        return auditEvent;
    }

    private static string RequireText(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A non-blank single-line value of at most {maximumLength} characters is required."),
                name);
        }

        return value;
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
