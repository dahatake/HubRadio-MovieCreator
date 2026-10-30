using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HubRadio.App.StyleTemplates;

/// <summary>
/// FR-139 (Q-021 A): a Japanese typeface bundled with the app under the SIL Open Font License 1.1, so it may be redistributed and
/// burned into a video. <see cref="Name"/> is the display name recorded for the Job; <see cref="AssFamily"/> is the family name
/// inside the font file that the ASS <c>\fn</c> tag must carry for the renderer to pick this file.
/// </summary>
public sealed record StyleTemplateBundledFont(
    string Key, string Name, string AssFamily, string FileName, string LicenseFileName, string Sha256, string Role);

/// <summary>
/// FR-137 / FR-139: the three bundled typefaces (太ゴシック・丸ゴシック・ポップ体) and the typefaces each template uses.
/// The files sit in the <c>TemplateFonts</c> folder next to the app (copied by the build). A user's own PC typeface is never used;
/// the name, version (read from the font file) and license are recorded in the Job's <c>style-template.json</c> (NFR-BR-004).
/// </summary>
public static class StyleTemplateFonts
{
    public const string FolderName = "TemplateFonts";
    public const string LicenseName = "SIL Open Font License 1.1";

    public static readonly StyleTemplateBundledFont NotoSansJpBlack = new("noto-sans-jp-black", "Noto Sans JP Black", "Noto Sans JP Black",
        "NotoSansJP-Black.otf", "OFL-NotoSansJP.txt", "3aa30b0956510f4205f759ab3079a5b658310ebcda2577f290466ea51c948819", "太ゴシック");

    public static readonly StyleTemplateBundledFont MPlusRounded1c = new("mplus-rounded-1c", "M PLUS Rounded 1c", "Rounded Mplus 1c",
        "MPLUSRounded1c-Regular.ttf", "OFL-MPLUSRounded1c.txt", "b75708b53e45b06d17d470aeeca5b766e3d1b3999f03f13ec4eb863ca846c14c", "丸ゴシック");

    public static readonly StyleTemplateBundledFont PottaOne = new("potta-one", "Potta One", "Potta One",
        "PottaOne-Regular.ttf", "OFL-PottaOne.txt", "bde843868b56e429e1c863da2efa76df533162b226d990efcb96d533b0ca2a93", "ポップ体");

    public static IReadOnlyList<StyleTemplateBundledFont> All { get; } = [NotoSansJpBlack, MPlusRounded1c, PottaOne];

    /// <summary>The folder the build copies next to the app.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    public static StyleTemplateBundledFont? FindByFamily(string family) =>
        All.FirstOrDefault(font => string.Equals(font.AssFamily, family, StringComparison.Ordinal));

    /// <summary>True only for a bundled typeface; any other family (a typeface of the user's PC) is refused.</summary>
    public static bool IsBundledFamily(string family) => FindByFamily(family) is not null;

    /// <summary>
    /// The font records of a template for style-template.json: each used typeface with the version read from the font file and
    /// the license. The files and their SHA-256 are checked first; a missing or altered file fails closed.
    /// </summary>
    public static IReadOnlyList<StyleTemplateFontRecord> RecordsFor(StyleTemplateDefinition template, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        string root = directory ?? DefaultDirectory;
        var records = new List<StyleTemplateFontRecord>();
        foreach (string family in template.FontFamilies)
        {
            StyleTemplateBundledFont font = FindByFamily(family)
                ?? throw new InvalidOperationException($"The typeface '{family}' is not a bundled typeface.");
            records.Add(new StyleTemplateFontRecord(font.Name, ReadVersion(font, root), LicenseName, BurnInPermitted: true));
        }

        return records;
    }

    /// <summary>Reads the version of a bundled font file (name ID 5, e.g. "Version 2.004;hotconv" gives "2.004").</summary>
    public static string ReadVersion(StyleTemplateBundledFont font, string? directory = null)
    {
        byte[] bytes = Verified(font, directory ?? DefaultDirectory);
        string version = ReadName(bytes, 5) ?? throw new InvalidDataException("The font file has no version.");
        const string prefix = "Version ";
        if (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            version = version[prefix.Length..];
        }

        int end = version.IndexOfAny([';', ' ']);
        return (end < 0 ? version : version[..end]).Trim();
    }

    /// <summary>The family name stored in the font file (name ID 1), which the ASS font name must match.</summary>
    public static string ReadFamily(StyleTemplateBundledFont font, string? directory = null) =>
        ReadName(Verified(font, directory ?? DefaultDirectory), 1) ?? throw new InvalidDataException("The font file has no family.");

    private static byte[] Verified(StyleTemplateBundledFont font, string directory)
    {
        string path = Path.Combine(directory, font.FileName);
        if (!File.Exists(path) || !File.Exists(Path.Combine(directory, font.LicenseFileName)))
        {
            throw new FileNotFoundException("The bundled typeface or its license is missing.", path);
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), font.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The bundled typeface does not match its recorded digest.");
        }

        return bytes;
    }

    private static string? ReadName(byte[] font, int nameId)
    {
        ReadOnlySpan<byte> data = font;
        if (data.Length < 12)
        {
            return null;
        }

        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        for (int index = 0; index < tableCount; index++)
        {
            int record = 12 + (index * 16);
            if (record + 16 > data.Length || !data.Slice(record, 4).SequenceEqual("name"u8))
            {
                continue;
            }

            int table = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[(record + 8)..]));
            int count = BinaryPrimitives.ReadUInt16BigEndian(data[(table + 2)..]);
            int strings = table + BinaryPrimitives.ReadUInt16BigEndian(data[(table + 4)..]);
            string? fallback = null;
            for (int item = 0; item < count; item++)
            {
                int entry = table + 6 + (item * 12);
                int platform = BinaryPrimitives.ReadUInt16BigEndian(data[entry..]);
                int id = BinaryPrimitives.ReadUInt16BigEndian(data[(entry + 6)..]);
                if (id != nameId)
                {
                    continue;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(entry + 8)..]);
                int offset = strings + BinaryPrimitives.ReadUInt16BigEndian(data[(entry + 10)..]);
                if (platform == 3)
                {
                    return Encoding.BigEndianUnicode.GetString(data.Slice(offset, length));
                }

                fallback ??= Encoding.ASCII.GetString(data.Slice(offset, length));
            }

            return fallback;
        }

        return null;
    }
}
