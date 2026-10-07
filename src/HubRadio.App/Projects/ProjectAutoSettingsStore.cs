using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.App.Projects;

/// <summary>FR-054: what the preset set for one Project, shown to the user as "自動で設定した項目".</summary>
internal sealed record AutoSettingNote(string Key, string Label, string ValueText, string Reason);

/// <summary>
/// FR-054: keeps the preset's decision per Project at <c>&lt;workspace&gt;\auto-settings\{draftId}.json</c>.
/// It is explanatory only; the applied values themselves live in the Project's settings snapshot.
/// </summary>
internal sealed class ProjectAutoSettingsStore
{
    internal const string Schema = "hubradio.auto-settings.v1";
    internal const int MaximumBytes = 64 * 1024;
    private const int MaximumNotes = 32;
    private const int MaximumTextLength = 512;

    private readonly string directory;

    internal ProjectAutoSettingsStore(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        directory = Path.Combine(workspacePath, "auto-settings");
    }

    internal string PathFor(EntityId<ProjectCreationDraftIdTag> draftId) => Path.Combine(directory, draftId + ".json");

    internal void Save(EntityId<ProjectCreationDraftIdTag> draftId, SourceMediaFacts facts,
        IReadOnlyList<AutoSettingRecommendation> applied)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(applied);
        Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = Schema,
            rule = BuzzEditingPreset.RuleVersion,
            facts = new { durationSeconds = facts.DurationSeconds, width = facts.DisplayWidth, height = facts.DisplayHeight },
            applied = applied.Select(static item => new { key = item.Key, label = item.Label, valueText = item.ValueText, reason = item.Reason }),
        });
        string staging = PathFor(draftId) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, PathFor(draftId), overwrite: true);
    }

    /// <summary>Null when no decision was recorded or the record cannot be read safely.</summary>
    internal IReadOnlyList<AutoSettingNote>? Read(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        try
        {
            var info = new FileInfo(PathFor(draftId));
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(info.FullName));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetString() != Schema)
            {
                return null;
            }

            var notes = new List<AutoSettingNote>();
            foreach (JsonElement item in root.GetProperty("applied").EnumerateArray().Take(MaximumNotes))
            {
                string Text(string name) => item.GetProperty(name).GetString() is { Length: > 0 and <= MaximumTextLength } value
                    ? value : throw new FormatException();
                notes.Add(new AutoSettingNote(Text("key"), Text("label"), Text("valueText"), Text("reason")));
            }

            return notes.AsReadOnly();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal void Delete(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        try
        {
            File.Delete(PathFor(draftId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
