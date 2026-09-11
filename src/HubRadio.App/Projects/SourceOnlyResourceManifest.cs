using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Rendering;
using Microsoft.Win32.SafeHandles;

namespace HubRadio.App.Projects;

/// <summary>
/// Package-relative contracts. Embedded inputs pin third-party bytes; generated App
/// and Worker manifests identify this build, not its authenticity or release approval.
/// Never infer approval by measuring an arbitrary executable supplied at startup.
/// </summary>
internal static class SourceOnlyResourceManifest
{
    internal const string ApplicationBuildPath = "Evidence/application-build.json";
    internal const string ApplicationFileName = "HubRadio.App.dll";
    internal const string FontManifestPath = "Fonts/font-manifest.json";
    internal const string PythonManifestResource = "HubRadio.App.Resources.PythonRuntimeManifest.json";
    internal const string FontManifestResource = "HubRadio.App.Resources.FontManifest.json";
    internal const string FontFileName = "NotoSansJP-Regular.otf";
    internal const string FontFamily = "Noto Sans JP";
    internal const string FontVersion = "2.004";
    internal const string FontSourceCommit = "523d033d6cb47f4a80c58a35753646f5c3608a78";
    internal const int MaximumManifestBytes = 4 * 1024 * 1024;

    internal static byte[] ReadEmbedded(string name)
    {
        using Stream stream = typeof(SourceOnlyResourceManifest).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("A build resource contract is missing.");
        if (stream.Length is <= 0 or > MaximumManifestBytes) { throw new InvalidDataException(); }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static BinaryVerificationRequest ReadApplicationBuild(byte[] bytes, string packageRoot, string version)
    {
        using JsonDocument document = Parse(bytes);
        JsonElement root = document.RootElement;
        RequireKeys(root, "schemaVersion", "relativePath", "version", "size", "sha256");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("relativePath").GetString() != ApplicationFileName ||
            root.GetProperty("version").GetString() != version ||
            !Version.TryParse(version, out Version? parsed) || parsed.Revision < 0 || parsed.ToString() != version)
        {
            throw new InvalidDataException();
        }
        return new("source-only.application-build", Path.Combine(packageRoot, ApplicationFileName),
            Digest(root, "sha256"), PositiveSize(root, "size"));
    }

    internal static FontContract ReadFont(byte[] shipped, byte[] trusted, string packageRoot)
    {
        // Only the build's embedded source contract may authorize a font. The loose
        // manifest cannot authorize replacement bytes by rewriting its own SHA.
        if (!shipped.AsSpan().SequenceEqual(trusted)) { throw new InvalidDataException(); }
        using JsonDocument document = Parse(trusted);
        JsonElement root = document.RootElement;
        RequireKeys(root, "schemaVersion", "family", "style", "version", "sourceCommit", "sourcePath",
            "relativePath", "byteSize", "sha256", "license", "releaseApproved");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("family").GetString() != FontFamily || root.GetProperty("style").GetString() != "Regular" ||
            root.GetProperty("version").GetString() != FontVersion ||
            root.GetProperty("sourceCommit").GetString() != FontSourceCommit ||
            root.GetProperty("sourcePath").GetString() != "Sans/SubsetOTF/JP/NotoSansJP-Regular.otf" ||
            root.GetProperty("relativePath").GetString() != FontFileName || root.GetProperty("releaseApproved").GetBoolean())
        {
            throw new InvalidDataException();
        }
        ContentDigest digest = Digest(root, "sha256");
        long size = PositiveSize(root, "byteSize");
        if (size > 16 * 1024 * 1024) { throw new InvalidDataException(); }
        JsonElement license = root.GetProperty("license");
        RequireKeys(license, "relativePath", "sourcePath", "spdxId", "byteSize", "sha256");
        if (license.GetProperty("relativePath").GetString() != "LICENSE" ||
            license.GetProperty("sourcePath").GetString() != "LICENSE" ||
            license.GetProperty("spdxId").GetString() != "OFL-1.1") { throw new InvalidDataException(); }
        return new(new FfmpegFontBinding(new RenderFontEvidence(FontFamily, digest),
            new BinaryVerificationRequest("source-only.font", Path.Combine(packageRoot, "Fonts", FontFileName), digest, size)),
            new BinaryVerificationRequest("source-only.font-license", Path.Combine(packageRoot, "Fonts", "LICENSE"),
                Digest(license, "sha256"), PositiveSize(license, "byteSize")));
    }

    internal static SourceOnlyApplicationResourceLease.WorkerFile[] ReadWorker(byte[] bytes, byte[] trustedSource)
    {
        using JsonDocument source = Parse(trustedSource);
        using JsonDocument document = Parse(bytes);
        JsonElement expected = source.RootElement;
        JsonElement root = document.RootElement;
        if (expected.GetProperty("schemaVersion").GetString() != "1.0.0" ||
            expected.GetProperty("runtime").GetProperty("identity").GetString() != "CPython" ||
            expected.GetProperty("runtime").GetProperty("version").GetString() != "3.12.10" ||
            expected.GetProperty("runtime").GetProperty("architecture").GetString() != "x64" ||
            expected.GetProperty("output").GetProperty("fileManifestName").GetString() != "runtime-files.json" ||
            expected.GetProperty("output").GetProperty("entryPoint").GetString() != "python.exe" ||
            root.GetProperty("schemaVersion").GetString() != "1.0.0" ||
            root.GetProperty("runtimeIdentity").GetString() != "CPython" ||
            root.GetProperty("runtimeVersion").GetString() != "3.12.10" ||
            root.GetProperty("architecture").GetString() != "x64" ||
            Digest(root, "sourceManifestSha256") != ContentDigest.Compute(trustedSource)) { throw new InvalidDataException(); }
        JsonElement output = expected.GetProperty("output");
        if (root.GetProperty("fileCount").GetInt32() != output.GetProperty("expectedFileCount").GetInt32() ||
            root.GetProperty("totalBytes").GetInt64() != output.GetProperty("expectedTotalBytes").GetInt64() ||
            Digest(root, "artifactSetSha256") != Digest(output, "expectedArtifactSetSha256")) { throw new InvalidDataException(); }
        SourceOnlyApplicationResourceLease.WorkerFile[] records = root.GetProperty("files").EnumerateArray().Select(file =>
            new SourceOnlyApplicationResourceLease.WorkerFile(file.GetProperty("path").GetString()!,
                file.GetProperty("byteSize").GetInt64(), file.GetProperty("sha256").GetString()!)).ToArray();
        ValidateWorkerRecords(records, output);
        var files = records.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (JsonElement file in expected.GetProperty("runtime").GetProperty("selectedSignedFiles").EnumerateArray())
        {
            RequireWorkerFile(files, file.GetProperty("name").GetString()!, file);
        }
        foreach (JsonElement file in expected.GetProperty("workerSource").GetProperty("files").EnumerateArray())
        {
            RequireWorkerFile(files, "Lib/site-packages/" + file.GetProperty("path").GetString(), file);
        }
        // This vendor executable pin is not replaceable through a generated manifest.
        if (!files.TryGetValue("python.exe", out var python) || python.ByteSize != SourceOnlyApplicationResourceLease.PythonByteSize ||
            python.Sha256 != SourceOnlyApplicationResourceLease.PythonSha256) { throw new InvalidDataException(); }
        return records;
    }

    internal static void ValidateWorkerRecords(IReadOnlyList<SourceOnlyApplicationResourceLease.WorkerFile> records, JsonElement output)
    {
        if (records.Count is <= 0 or > 20000) { throw new InvalidDataException(); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var text = new StringBuilder();
        foreach (var file in records.OrderBy(item => item.Path, StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), false)))
        {
            ValidateRelativePath(file.Path);
            if (!names.Add(file.Path) || file.Path.Equals("runtime-files.json", StringComparison.OrdinalIgnoreCase) ||
                file.ByteSize < 0 || !ContentDigest.TryParse(file.Sha256, out ContentDigest? digest) || digest.Hex != file.Sha256)
            {
                throw new InvalidDataException();
            }
            total = checked(total + file.ByteSize);
            text.Append(file.Path).Append('\0').Append(file.ByteSize.ToString(CultureInfo.InvariantCulture))
                .Append('\0').Append(file.Sha256).Append('\n');
        }
        if (records.Count != output.GetProperty("expectedFileCount").GetInt32() ||
            total != output.GetProperty("expectedTotalBytes").GetInt64() ||
            ContentDigest.Compute(Encoding.UTF8.GetBytes(text.ToString())) != Digest(output, "expectedArtifactSetSha256"))
        {
            throw new InvalidDataException();
        }
    }

    private static void RequireWorkerFile(IReadOnlyDictionary<string, SourceOnlyApplicationResourceLease.WorkerFile> files,
        string path, JsonElement expected)
    {
        long size = expected.GetProperty("byteSize").GetInt64();
        if (size < 0 || !files.TryGetValue(path, out var actual) || actual.ByteSize != size ||
            actual.Sha256 != Digest(expected, "sha256").Hex) { throw new InvalidDataException(); }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > SourceOnlySourceLocator.MaximumPathCharacters ||
            path.Contains('\\') || path.Contains(':') || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
            path.Any(char.IsControl) || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) ||
                part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || IsDeviceName(part)))
        {
            throw new InvalidDataException();
        }
    }

    private static bool IsDeviceName(string part)
    {
        string name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                (name[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    /// <summary>Check the actual sfnt name table, not just a manifest's family label.</summary>
    internal static void ValidateFontNames(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("OTTO"u8)) { throw new InvalidDataException(); }
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (count is <= 0 or > 256 || 12 + count * 16 > bytes.Length) { throw new InvalidDataException(); }
        ReadOnlySpan<byte> names = default;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> table = bytes.Slice(12 + i * 16, 16);
            if (!table[..4].SequenceEqual("name"u8)) { continue; }
            if (!names.IsEmpty) { throw new InvalidDataException(); }
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table[8..]));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table[12..]));
            names = bytes.Slice(offset, length);
        }
        if (names.Length < 6) { throw new InvalidDataException(); }
        int records = BinaryPrimitives.ReadUInt16BigEndian(names[2..]);
        int storage = BinaryPrimitives.ReadUInt16BigEndian(names[4..]);
        if (6 + records * 12 > names.Length || storage < 6 + records * 12) { throw new InvalidDataException(); }
        bool family = false, regular = false, postscript = false;
        var unicode = new UnicodeEncoding(true, false, true);
        for (int i = 0; i < records; i++)
        {
            ReadOnlySpan<byte> record = names.Slice(6 + i * 12, 12);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            if (platform is not (0 or 3)) { continue; }
            int id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            string value = unicode.GetString(names.Slice(storage + offset, length));
            family |= (id is 1 or 16) && value == FontFamily;
            regular |= (id is 2 or 17) && value == "Regular";
            postscript |= id == 6 && value == "NotoSansJP-Regular";
        }
        if (!family || !regular || !postscript) { throw new InvalidDataException(); }
    }

    internal static JsonDocument Parse(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { json = json[3..]; }
        if (json.Length is <= 0 or > MaximumManifestBytes) { throw new InvalidDataException(); }
        _ = new UTF8Encoding(false, true).GetCharCount(json);
        _ = CanonicalJson.CanonicalizeToUtf8(json.ToArray()); // Includes duplicate-key rejection.
        return JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static void RequireKeys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != keys.Length ||
            keys.Any(key => !value.TryGetProperty(key, out _))) { throw new InvalidDataException(); }
    }

    private static ContentDigest Digest(JsonElement value, string name)
    {
        string? text = value.GetProperty(name).GetString();
        return ContentDigest.TryParse(text, out ContentDigest? digest) && digest.Hex == text
            ? digest : throw new InvalidDataException();
    }

    private static long PositiveSize(JsonElement value, string name) =>
        value.GetProperty(name).TryGetInt64(out long size) && size > 0 ? size : throw new InvalidDataException();

    internal sealed record FontContract(FfmpegFontBinding Binding, BinaryVerificationRequest License);

    internal static FileStream OpenFile(string path)
    {
        FileStream stream = SourceOnlySourceLocator.OpenHeldFile(path);
        try
        {
            RequireHandlePath(stream.SafeFileHandle, path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    // Directory handles deny renaming/replacing an ancestor while files are in use.
    // File attributes are checked, not used as an immutability/authenticity claim.
    internal static SafeFileHandle OpenDirectory(string path)
    {
        _ = SourceOnlySourceLocator.RequirePath(path, directory: true);
        // FILE_READ_ATTRIBUTES; share READ | WRITE but not DELETE.
        SafeFileHandle handle = CreateFileW(@"\\?\" + path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) { throw new IOException(); }
            FileAttributes attributes = File.GetAttributes(handle);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) { throw new IOException(); }
            RequireHandlePath(handle, path);
            _ = SourceOnlySourceLocator.RequirePath(path, directory: true);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void RequireHandlePath(SafeFileHandle handle, string path)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        string actual = buffer.ToString();
        if (length == 0 || length >= buffer.Capacity || !actual.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(actual[4..]),
                Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)) { throw new IOException(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}