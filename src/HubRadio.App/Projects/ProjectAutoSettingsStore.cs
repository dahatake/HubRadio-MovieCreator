using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.App.Projects;

/// <summary>FR-054: what the preset set for one Project, shown to the user as "自動で設定した項目".</summary>
internal sealed record AutoSettingNote(string Key, string Label, string ValueText, string Reason);

/// <summary>
/// FR-001 AC-049: what was registered for the source video (name, length, size, frame rate, audio, file size,
/// created time, registrant's display name) and the warnings that affect editing. Facts are null-valued when
/// ffprobe could not read the file.
/// </summary>
internal sealed record SourceMediaNote(string FileName, double? DurationSeconds, int? Width, int? Height,
    double? FrameRate, bool? HasAudio, long SizeBytes, string CreatedAt, string RegisteredBy, IReadOnlyList<string> Warnings)
{
    internal const int LowResolutionShortSide = 360;

    internal static SourceMediaNote Create(string sourcePath, SourceMediaFacts? facts, string registeredBy)
    {
        var info = new FileInfo(sourcePath);
        var warnings = new List<string>();
        if (facts is null)
        {
            warnings.Add("動画を読み取れませんでした。破損している可能性があります。");
        }
        else
        {
            if (facts.HasAudio == false)
            {
                warnings.Add("音声トラックがありません。");
            }

            if (Math.Min(facts.DisplayWidth, facts.DisplayHeight) < LowResolutionShortSide)
            {
                warnings.Add($"解像度が極端に低い動画です（{facts.DisplayWidth}×{facts.DisplayHeight}）。");
            }
        }

        DateTime created = info.Exists ? info.CreationTimeUtc : DateTime.UtcNow;
        return new SourceMediaNote(info.Name, facts?.DurationSeconds, facts?.DisplayWidth, facts?.DisplayHeight,
            facts?.FrameRate, facts?.HasAudio, info.Exists ? info.Length : 0,
            created.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            registeredBy, warnings.AsReadOnly());
    }
}

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
            File.Delete(SourcePathFor(draftId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal const string SourceSchema = "hubradio.source-media.v1";

    internal string SourcePathFor(EntityId<ProjectCreationDraftIdTag> draftId) => Path.Combine(directory, draftId + ".source.json");

    /// <summary>FR-001 AC-049: records the registered source's metadata beside the Project's draft.</summary>
    internal void SaveSource(EntityId<ProjectCreationDraftIdTag> draftId, SourceMediaNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = SourceSchema,
            fileName = note.FileName,
            durationSeconds = note.DurationSeconds,
            width = note.Width,
            height = note.Height,
            frameRate = note.FrameRate,
            hasAudio = note.HasAudio,
            sizeBytes = note.SizeBytes,
            createdAt = note.CreatedAt,
            registeredBy = note.RegisteredBy,
            warnings = note.Warnings,
        });
        string staging = SourcePathFor(draftId) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, SourcePathFor(draftId), overwrite: true);
    }

    /// <summary>Null when nothing was recorded or the record cannot be read safely.</summary>
    internal SourceMediaNote? ReadSource(EntityId<ProjectCreationDraftIdTag> draftId)
    {
        try
        {
            var info = new FileInfo(SourcePathFor(draftId));
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(info.FullName));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetString() != SourceSchema)
            {
                return null;
            }

            double? Number(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetDouble();
            int? Integer(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetInt32();
            JsonElement audio = root.GetProperty("hasAudio");
            string Text(string name) => root.GetProperty(name).GetString() is { Length: > 0 and <= MaximumTextLength } value
                ? value : throw new FormatException();
            var warnings = root.GetProperty("warnings").EnumerateArray().Take(MaximumNotes)
                .Select(static item => item.GetString() is { Length: > 0 and <= MaximumTextLength } text ? text : throw new FormatException())
                .ToList();
            return new SourceMediaNote(Text("fileName"), Number("durationSeconds"), Integer("width"), Integer("height"),
                Number("frameRate"), audio.ValueKind == JsonValueKind.Null ? null : audio.GetBoolean(),
                root.GetProperty("sizeBytes").GetInt64(), Text("createdAt"), Text("registeredBy"), warnings.AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
