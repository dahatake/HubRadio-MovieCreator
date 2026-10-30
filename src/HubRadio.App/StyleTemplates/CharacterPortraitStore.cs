using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HubRadio.App.StyleTemplates;

/// <summary>NFR-BR-004 / FR-137: whether the user has confirmed the right to use a registered image.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortraitRightsStatus
{
    /// <summary>未確認: registered, but the user has not confirmed the right to use the image.</summary>
    Unconfirmed,

    /// <summary>確認済み: the user confirmed the right to use the image.</summary>
    Confirmed,
}

/// <summary>
/// FR-137 (6): a standing picture (立ち絵) the user registered. The app bundles none; the image is the user's own, kept with its
/// rights status (NFR-BR-004) and the subtitle color of the character (a different color for every character).
/// </summary>
public sealed record CharacterPortrait(
    string Id, string Name, string ImageFile, string ImageSha256, string SubtitleColor,
    PortraitRightsStatus Rights, string? RightsNote, DateTimeOffset RegisteredAt, DateTimeOffset RightsRecordedAt);

/// <summary>
/// FR-137 (6): the user-registered portraits of the character dialogue template, kept in the app's own directory
/// (<c>characters.json</c> plus <c>images/</c>, written atomically). The character dialogue template cannot be chosen while the store is
/// empty. Nothing is registered by default.
/// </summary>
public sealed class CharacterPortraitStore
{
    public const string FileName = "characters.json";
    public const string ImageDirectoryName = "images";
    public const int NameMaximumLength = 40;
    public const long ImageMaximumBytes = 20L * 1024 * 1024;

    /// <summary>Distinct, readable subtitle colors offered to characters (each is checked against the outline by FR-139).</summary>
    public static readonly IReadOnlyList<string> SubtitleColorPalette = ["#FFD400", "#4FC3F7", "#FF8A80", "#B9F6CA", "#E1BEE7", "#FFAB40"];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    private readonly string directory;
    private readonly object gate = new();

    public CharacterPortraitStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
    }

    public string Directory => directory;

    public IReadOnlyList<CharacterPortrait> List()
    {
        lock (gate)
        {
            return Read();
        }
    }

    public int Count => List().Count;

    /// <summary>FR-137 (6): whether the template can be chosen now (the character dialogue one needs a registered portrait).</summary>
    public bool IsSelectable(StyleTemplateDefinition template) => StyleTemplateCatalog.IsSelectable(template, Count);

    public CharacterPortrait? Find(string id) => List().FirstOrDefault(item => item.Id == id);

    /// <summary>
    /// Registers the user's own image with its rights status. The image is copied into the store; a name must be unique, and the
    /// subtitle color (given or the next palette color) must differ from every other character.
    /// </summary>
    public CharacterPortrait Register(string name, string sourceImagePath, PortraitRightsStatus rights, DateTimeOffset now,
        string? subtitleColor = null, string? rightsNote = null)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > NameMaximumLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("A character needs a name of 1 to 40 characters.", nameof(name));
        }

        if (!Enum.IsDefined(rights))
        {
            throw new ArgumentOutOfRangeException(nameof(rights));
        }

        string extension = Path.GetExtension(sourceImagePath ?? string.Empty).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension) || !File.Exists(sourceImagePath))
        {
            throw new ArgumentException("The image must be an existing PNG, JPEG or WebP file.", nameof(sourceImagePath));
        }

        byte[] bytes = File.ReadAllBytes(sourceImagePath);
        if (bytes.Length is 0 || bytes.Length > ImageMaximumBytes || !HasImageSignature(bytes, extension))
        {
            throw new ArgumentException("The image is empty, too large or not the image it claims to be.", nameof(sourceImagePath));
        }

        lock (gate)
        {
            List<CharacterPortrait> items = [.. Read()];
            if (items.Any(item => string.Equals(item.Name, trimmed, StringComparison.Ordinal)))
            {
                throw new ArgumentException("A character with this name is already registered.", nameof(name));
            }

            string color = subtitleColor is null
                ? SubtitleColorPalette.FirstOrDefault(candidate => items.All(item => !SameColor(item.SubtitleColor, candidate)))
                    ?? throw new InvalidOperationException("No subtitle color is left for another character.")
                : Normalize(subtitleColor);
            if (items.Any(item => SameColor(item.SubtitleColor, color)))
            {
                throw new ArgumentException("The subtitle color is already used by another character.", nameof(subtitleColor));
            }

            string id = "char-" + Guid.NewGuid().ToString("N")[..12];
            string imageFile = id + extension;
            System.IO.Directory.CreateDirectory(Path.Combine(directory, ImageDirectoryName));
            File.WriteAllBytes(Path.Combine(directory, ImageDirectoryName, imageFile), bytes);
            var portrait = new CharacterPortrait(id, trimmed, imageFile, Convert.ToHexStringLower(SHA256.HashData(bytes)), color,
                rights, string.IsNullOrWhiteSpace(rightsNote) ? null : rightsNote.Trim(), now, now);
            items.Add(portrait);
            Write(items);
            return portrait;
        }
    }

    /// <summary>NFR-BR-004: records a changed rights confirmation (for example 未確認 to 確認済み) with the time.</summary>
    public CharacterPortrait SetRights(string id, PortraitRightsStatus rights, DateTimeOffset now, string? rightsNote = null)
    {
        if (!Enum.IsDefined(rights))
        {
            throw new ArgumentOutOfRangeException(nameof(rights));
        }

        lock (gate)
        {
            List<CharacterPortrait> items = [.. Read()];
            int index = items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException("The character is not registered.");
            }

            items[index] = items[index] with
            {
                Rights = rights,
                RightsNote = string.IsNullOrWhiteSpace(rightsNote) ? items[index].RightsNote : rightsNote.Trim(),
                RightsRecordedAt = now,
            };
            Write(items);
            return items[index];
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            List<CharacterPortrait> items = [.. Read()];
            CharacterPortrait? found = items.FirstOrDefault(item => item.Id == id);
            if (found is null)
            {
                return false;
            }

            _ = items.Remove(found);
            Write(items);
            string path = Path.Combine(directory, ImageDirectoryName, found.ImageFile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
    }

    /// <summary>The path of the stored image of a portrait.</summary>
    public string ImagePath(CharacterPortrait portrait)
    {
        ArgumentNullException.ThrowIfNull(portrait);
        return Path.Combine(directory, ImageDirectoryName, Path.GetFileName(portrait.ImageFile));
    }

    /// <summary>
    /// FR-140: cuts one character out of a registered image that holds several (for example angles side by side) and registers it.
    /// The cut-out inherits the rights confirmation of the source (status, note and time of the confirmation, NFR-BR-004); the
    /// source image file is only read and stays byte for byte as it was.
    /// </summary>
    public CharacterPortrait RegisterCutout(string sourceId, string name, PortraitCropRect rect, DateTimeOffset now, string? subtitleColor = null)
    {
        lock (gate)
        {
            CharacterPortrait source = Find(sourceId) ?? throw new KeyNotFoundException("The source image is not registered.");
            string sourcePath = ImagePath(source);
            (int width, int height) = PortraitImageCropper.Size(sourcePath);
            if (!rect.IsInside(width, height))
            {
                throw new ArgumentOutOfRangeException(nameof(rect), "The range must lie inside the image.");
            }

            string temporary = Path.Combine(Path.GetTempPath(), "hubradio-cutout-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                PortraitImageCropper.CropToPng(sourcePath, temporary, rect);
                CharacterPortrait made = Register(name, temporary, source.Rights, now, subtitleColor,
                    source.RightsNote ?? "切り出し元: " + source.Name);
                List<CharacterPortrait> items = [.. Read()];
                int index = items.FindIndex(item => item.Id == made.Id);
                items[index] = made with { RightsRecordedAt = source.RightsRecordedAt };
                Write(items);
                return items[index];
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private List<CharacterPortrait> Read()
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<CharacterPortrait>>(File.ReadAllText(path)) ?? [];
    }

    private void Write(List<CharacterPortrait> items)
    {
        System.IO.Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Normalize(string color)
    {
        (byte r, byte g, byte b) = StyleTemplateColor.Parse(color);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static bool SameColor(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static bool HasImageSignature(byte[] bytes, string extension) => extension switch
    {
        ".png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        ".jpg" or ".jpeg" => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        ".webp" => bytes.Length > 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}
