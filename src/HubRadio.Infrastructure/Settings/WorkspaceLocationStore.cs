using System.Text;
using System.Text.Json;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Settings;

/// <summary>FR-062: the effective, saved and default Workspace folder.</summary>
public sealed record WorkspaceLocation(string CurrentPath, string? SavedPath, string DefaultPath);

/// <summary>A malformed or unusable workspace-location.json; startup must fail visibly.</summary>
public sealed class WorkspaceLocationException : Exception
{
    public WorkspaceLocationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// FR-062: persists the Workspace folder location in &lt;applicationRoot&gt;\workspace-location.json.
/// It never creates, copies or deletes Workspace data; the only move is the Q-013 legacy rename.
/// </summary>
public sealed class WorkspaceLocationStore
{
    internal const string ConfigFileName = "workspace-location.json";
    internal const string DatabaseFileName = "hubradio.db";
    internal const int MaxPathLength = 200;
    private const int MaxConfigBytes = 8 * 1024;

    private readonly string applicationRoot;
    private readonly string? defaultPathOverride;
    private static readonly object MigrationGate = new();

    private readonly string installDirectory;
    private readonly string previousDefaultPath;

    public WorkspaceLocationStore(
        string applicationRoot,
        string? defaultPath = null,
        string? installDirectory = null,
        string? previousDefaultPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        this.applicationRoot = Path.GetFullPath(applicationRoot);
        defaultPathOverride = defaultPath is null ? null : Path.GetFullPath(defaultPath);
        this.installDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory));
        this.previousDefaultPath = previousDefaultPath is null
            ? Path.Combine(Path.GetPathRoot(this.applicationRoot)!, "workspace")
            : Path.GetFullPath(previousDefaultPath);
    }

    public string ConfigPath => Path.Combine(applicationRoot, ConfigFileName);

    /// <summary>FR-062.1: &lt;install directory&gt;\workspace.</summary>
    public string DefaultPath =>
        defaultPathOverride ?? Path.Combine(installDirectory, "workspace");

    private string LegacyPath => Path.Combine(applicationRoot, "Workspace");

    /// <summary>A-062-09: the previous default, &lt;drive of the application data folder&gt;\workspace.</summary>
    private string PreviousDefaultPath => previousDefaultPath;

    /// <summary>
    /// Q-013 (B): with no saved location, moves a legacy Workspace that holds hubradio.db to the
    /// default location. Both folders are on the application data drive, so this is a rename, never
    /// a copy or delete. Any failure leaves the legacy folder untouched and in use.
    /// </summary>
    public bool TryMigrateLegacyWorkspace()
    {
        lock (MigrationGate)
        {
            if (ReadSavedPath() is not null ||
                !File.Exists(Path.Combine(LegacyPath, DatabaseFileName)))
            {
                return false;
            }

            string target = DefaultPath;
            try
            {
                _ = Validate(target, installDirectory);
                if (!SameVolume(LegacyPath, target))
                {
                    return false;
                }

                if (File.Exists(Path.Combine(target, DatabaseFileName)))
                {
                    return false;
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: false);
                }

                Directory.Move(LegacyPath, target);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public WorkspaceLocation Resolve()
    {
        string? saved = ReadSavedPath();
        if (saved is not null)
        {
            return new WorkspaceLocation(saved, saved, DefaultPath);
        }

        // Until TryMigrateLegacyWorkspace succeeds, an existing installation keeps its data in place.
        if (File.Exists(Path.Combine(LegacyPath, DatabaseFileName)))
        {
            return new WorkspaceLocation(LegacyPath, null, DefaultPath);
        }

        // FR-062.2 (2)(a): the default, when it holds hubradio.db or is absent/empty and writable.
        if (IsUsableDefault(DefaultPath))
        {
            return new WorkspaceLocation(DefaultPath, null, DefaultPath);
        }

        // FR-062.2 (2)(b) / A-062-09: keep using the previous default when it holds data.
        string previous = PreviousDefaultPath;
        if (!string.Equals(previous, DefaultPath, WorkspacePathSafety.PathComparison) &&
            File.Exists(Path.Combine(previous, DatabaseFileName)))
        {
            return new WorkspaceLocation(previous, null, DefaultPath);
        }

        // FR-062.2 (2)(c): never fail startup; fall back to the legacy location.
        return new WorkspaceLocation(LegacyPath, null, DefaultPath);
    }

    private bool IsUsableDefault(string path)
    {
        try
        {
            _ = Validate(path, installDirectory);
            if (File.Exists(Path.Combine(path, DatabaseFileName)))
            {
                return true;
            }

            string probeDirectory = path;
            while (!Directory.Exists(probeDirectory))
            {
                probeDirectory = Path.GetDirectoryName(probeDirectory)!;
            }

            string probe = Path.Combine(probeDirectory, $".hubradio-write-test-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the normalized path or throws <see cref="ArgumentException"/>.</summary>
    public static string Validate(string? path) => Validate(path, AppContext.BaseDirectory);

    /// <summary>
    /// FR-062.5: the install directory, its descendants and ancestors are rejected, except
    /// &lt;install directory&gt;\workspace itself (the FR-062.1 default).
    /// </summary>
    internal static string Validate(string? path, string installDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid();
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid();
        }

        string normalized = Path.TrimEndingDirectorySeparator(full);
        string given = Path.TrimEndingDirectorySeparator(path);
        if (normalized.Length > MaxPathLength ||
            !string.Equals(given, normalized, StringComparison.OrdinalIgnoreCase) ||
            !HasDriveLetterRoot(normalized))
        {
            throw Invalid();
        }

        string root = Path.GetPathRoot(normalized)!;
        if (normalized.Length <= root.Length)
        {
            throw Invalid();
        }

        foreach (string segment in normalized[root.Length..].Split('\\'))
        {
            if (segment.Length == 0 ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                IsReservedDeviceName(segment))
            {
                throw Invalid();
            }
        }

        string installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectoryPath));
        string defaultInside = Path.Combine(installDirectory, "workspace");
        if (!string.Equals(normalized, defaultInside, WorkspacePathSafety.PathComparison) &&
            (string.Equals(normalized, installDirectory, WorkspacePathSafety.PathComparison) ||
            WorkspacePathSafety.IsStrictDescendant(installDirectory, normalized) ||
            WorkspacePathSafety.IsStrictDescendant(normalized, installDirectory)))
        {
            throw Invalid();
        }

        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                throw Invalid();
            }

            ValidateExistingParts(normalized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or WorkspacePathSafetyException or ArgumentException)
        {
            throw Invalid();
        }

        return normalized;
    }

    /// <summary>Null resets to the default location by deleting the config file.</summary>
    public void Save(string? path)
    {
        if (path is null)
        {
            File.Delete(ConfigPath);
            return;
        }

        string normalized = Validate(path, installDirectory);
        Directory.CreateDirectory(applicationRoot);
        byte[] bytes = new UTF8Encoding(false).GetBytes(
            JsonSerializer.Serialize(new { version = 1, path = normalized }));
        string temp = Path.Combine(applicationRoot, $"{ConfigFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string? ReadSavedPath()
    {
        string configPath = ConfigPath;
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(configPath);
            if (bytes.Length is 0 or > MaxConfigBytes)
            {
                throw new WorkspaceLocationException("workspace-location.json is invalid.");
            }

            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int versionNumber) || versionNumber != 1 ||
                !root.TryGetProperty("path", out JsonElement pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
            {
                throw new WorkspaceLocationException("workspace-location.json is invalid.");
            }

            return Validate(pathElement.GetString(), installDirectory);
        }
        catch (Exception exception) when (exception is JsonException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            throw new WorkspaceLocationException("workspace-location.json is invalid.", exception);
        }
    }

    private static void ValidateExistingParts(string normalized)
    {
        // Reject reparse points on every existing ancestor, then the target itself.
        string? parent = Path.GetDirectoryName(normalized);
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(parent!);

        if (!WorkspacePathSafety.TryGetAttributes(normalized, out FileAttributes attributes))
        {
            return;
        }

        WorkspacePathSafety.ValidateDirectoryAttributes(attributes);
        bool hasEntries = Directory.EnumerateFileSystemEntries(normalized).Any();
        if (hasEntries && !File.Exists(Path.Combine(normalized, DatabaseFileName)))
        {
            throw Invalid();
        }
    }

    private static bool HasDriveLetterRoot(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string name = (dot < 0 ? segment : segment[..dot]).TrimEnd();
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            name[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static ArgumentException Invalid() =>
        new("The workspace location is not allowed.");
}
