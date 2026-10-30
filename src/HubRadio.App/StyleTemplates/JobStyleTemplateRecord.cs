using System.Text.Json;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-136 (FR-014): one user adjustment of a telop after the template was applied.</summary>
public sealed record StyleTemplateAdjustment(string TargetId, string Property, string Value);

/// <summary>FR-139 (NFR-BR-004): a font a Job used, with the version and license it was permitted under.</summary>
public sealed record StyleTemplateFontRecord(string Name, string Version, string License, bool BurnInPermitted);

/// <summary>
/// FR-136: the template a Job applied (ID and version) and the adjustments the user made afterwards. A Job with
/// adjustments is shown as 「テンプレート＋変更あり」. The fonts used are recorded with their version and license (FR-139).
/// </summary>
public sealed record JobStyleTemplateRecord(string TemplateId, int Version, IReadOnlyList<StyleTemplateAdjustment> Adjustments,
    IReadOnlyList<StyleTemplateFontRecord>? Fonts = null)
{
    /// <summary>The properties FR-014 lets the user change after the template is applied.</summary>
    public static readonly IReadOnlyList<string> AdjustableProperties = ["start", "end", "position", "size", "color", "animation"];

    public const string AppliedLabel = "テンプレート";
    public const string ModifiedLabel = "テンプレート＋変更あり";

    public bool IsModified => Adjustments.Count > 0;

    /// <summary>FR-139: a font may be used only when it is recorded with a version and a license that permit burning it in.</summary>
    public static bool IsUsable(StyleTemplateFontRecord font) =>
        font is not null && !string.IsNullOrWhiteSpace(font.Name) && !string.IsNullOrWhiteSpace(font.Version)
        && !string.IsNullOrWhiteSpace(font.License) && font.BurnInPermitted;

    /// <summary>The same record with the fonts of the output (the font name, version and license are kept as given).</summary>
    public JobStyleTemplateRecord WithFonts(IEnumerable<StyleTemplateFontRecord> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        StyleTemplateFontRecord[] list = fonts.ToArray();
        if (list.Any(static font => !IsUsable(font)))
        {
            throw new ArgumentException("A font needs a name, a version and a license that permits burning it in.", nameof(fonts));
        }

        return this with { Fonts = list };
    }

    public string Label => IsModified ? ModifiedLabel : AppliedLabel;

    public static JobStyleTemplateRecord For(StyleTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new(template.Id, template.Version, []);
    }

    /// <summary>A later adjustment of the same target and property replaces the earlier one.</summary>
    public JobStyleTemplateRecord Adjust(string targetId, string property, string value)
    {
        if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrEmpty(value) || !AdjustableProperties.Contains(property))
        {
            throw new ArgumentException("An adjustment needs a target, a known property and a value.");
        }

        var adjustments = Adjustments.Where(item => item.TargetId != targetId || item.Property != property).ToList();
        adjustments.Add(new(targetId, property, value));
        return this with { Adjustments = adjustments };
    }
}

/// <summary>FR-136: keeps the record in the Job's own directory (<c>style-template.json</c>), written atomically.</summary>
public static class JobStyleTemplateStore
{
    public const string FileName = "style-template.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string jobDirectory, JobStyleTemplateRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        ArgumentNullException.ThrowIfNull(record);
        if (StyleTemplateCatalog.Find(record.TemplateId, record.Version) is null)
        {
            throw new ArgumentException("The template and version are not defined.", nameof(record));
        }

        Directory.CreateDirectory(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Null when the Job applied no template.</summary>
    public static JobStyleTemplateRecord? Load(string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        JobStyleTemplateRecord? record = JsonSerializer.Deserialize<JobStyleTemplateRecord>(File.ReadAllText(path));
        return record is not null && StyleTemplateCatalog.Find(record.TemplateId, record.Version) is not null ? record : null;
    }
}
