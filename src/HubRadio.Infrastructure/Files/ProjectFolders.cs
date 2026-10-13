using System.Text;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Infrastructure.Files;

public enum ProjectFolderFailureReason
{
    InvalidName,
    DuplicateName,
    RenameFailed,
}

public sealed class ProjectFolderException : InvalidOperationException
{
    public ProjectFolderException(ProjectFolderFailureReason reason)
        : base("The project folder operation failed: " + reason)
    {
        Reason = reason;
    }

    public ProjectFolderFailureReason Reason { get; }
}

public enum ProjectFolderMigrationStatus
{
    Migrated,
    AlreadyNamed,
    SkippedInvalidName,
    SkippedDuplicateName,
    SkippedFolderExists,
    SkippedFailed,
}

public sealed record ProjectFolderMigrationEntry(string ProjectId, string? Name, ProjectFolderMigrationStatus Status);

/// <summary>
/// FR-133 / FR-134: maps a Project to its folder under <c>&lt;workspace&gt;\projects</c>. The folder is named after the
/// Project name; the Project identity (and every stored "projects/&lt;id&gt;/..." reference) stays the GUID.
/// The id-to-folder mapping lives in <c>&lt;workspace&gt;\.project-folders.json</c>. A folder rename is a single
/// same-volume directory move that is announced in the registry first ("pending"), so an interrupted rename is
/// completed or abandoned by <see cref="Reconcile"/> without copying or deleting any data.
/// A project without a registry entry keeps using its GUID folder.
/// </summary>
public static class ProjectFolders
{
    public const int MaxNameLength = 80;
    public const string RegistryFileName = ".project-folders.json";
    private const string ProjectsDirectoryName = "projects";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Returns null when the name can be a folder name; otherwise a short reason.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
        {
            return "empty";
        }

        if (name.EnumerateRunes().Count() > MaxNameLength)
        {
            return "too-long";
        }

        foreach (char c in name)
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c))
            {
                return "invalid-character";
            }
        }

        if (name[^1] is '.' or ' ' || name[0] == ' ')
        {
            return "trailing-dot-or-space";
        }

        string stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            return "reserved-name";
        }

        return null;
    }

    public static string GetPath(string workspacePath, EntityId<ProjectIdTag> projectId) =>
        GetPath(workspacePath, projectId.ToString());

    public static string GetPath(string workspacePath, string projectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath);
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        string root = Path.Combine(workspacePath, ProjectsDirectoryName);
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            return Path.Combine(root, ResolveFolderName(root, registry, projectId));
        }
    }

    /// <summary>Folder name (not path) currently used by the project.</summary>
    public static string GetFolderName(string workspacePath, string projectId)
    {
        lock (Gate)
        {
            return ResolveFolderName(
                Path.Combine(workspacePath, ProjectsDirectoryName), Load(workspacePath), projectId);
        }
    }

    /// <summary>Project id owning a folder name directly under projects, or the name itself when it is not mapped.</summary>
    public static string GetProjectIdForFolder(string workspacePath, string folderName)
    {
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            string root = Path.Combine(workspacePath, ProjectsDirectoryName);
            foreach ((string id, string name) in registry.Folders)
            {
                if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ResolveFolderName(root, registry, id), folderName, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }

            foreach ((string id, string name) in registry.Pending)
            {
                if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ResolveFolderName(root, registry, id), folderName, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }

            return folderName;
        }
    }

    /// <summary>"projects/&lt;id&gt;/x" to "projects/&lt;folder&gt;/x"; anything else is returned unchanged.</summary>
    public static string ToPhysicalRelative(string workspacePath, string relativePath)
    {
        if (!TrySplitProjectsPrefix(relativePath, out string segment, out string rest) ||
            !File.Exists(Path.Combine(workspacePath, RegistryFileName)))
        {
            return relativePath;
        }

        return ProjectsDirectoryName + "/" + GetFolderName(workspacePath, segment) + rest;
    }

    /// <summary>"projects/&lt;folder&gt;/x" to "projects/&lt;id&gt;/x"; anything else is returned unchanged.</summary>
    public static string ToLogicalRelative(string workspacePath, string relativePath)
    {
        if (!TrySplitProjectsPrefix(relativePath, out string segment, out string rest) ||
            !File.Exists(Path.Combine(workspacePath, RegistryFileName)))
        {
            return relativePath;
        }

        return ProjectsDirectoryName + "/" + GetProjectIdForFolder(workspacePath, segment) + rest;
    }

    /// <summary>Creates the folder for a new project and records it. Rejects invalid and duplicate names.</summary>
    public static string Bind(string workspacePath, EntityId<ProjectIdTag> projectId, string name)
    {
        string id = projectId.ToString();
        ThrowIfInvalid(name);
        string root = Path.Combine(workspacePath, ProjectsDirectoryName);
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            if (registry.Folders.TryGetValue(id, out string? existing))
            {
                return Path.Combine(root, existing);
            }

            ThrowIfDuplicate(root, registry, id, name);
            string target = Path.Combine(root, name);
            try
            {
                _ = Directory.CreateDirectory(target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ProjectFolderException(ProjectFolderFailureReason.RenameFailed);
            }

            registry.Folders[id] = name;
            Save(workspacePath, registry);
            return target;
        }
    }

    /// <summary>Checks that <paramref name="name"/> can be used by <paramref name="projectId"/> (create or rename).</summary>
    public static void EnsureNameAvailable(string workspacePath, EntityId<ProjectIdTag>? projectId, string name)
    {
        ThrowIfInvalid(name);
        string root = Path.Combine(workspacePath, ProjectsDirectoryName);
        lock (Gate)
        {
            ThrowIfDuplicate(root, Load(workspacePath), projectId?.ToString() ?? string.Empty, name);
        }
    }

    /// <summary>
    /// Renames the project folder with one directory move. On failure the folder and the registry are unchanged.
    /// </summary>
    public static void Rename(string workspacePath, EntityId<ProjectIdTag> projectId, string newName)
    {
        string id = projectId.ToString();
        ThrowIfInvalid(newName);
        string root = Path.Combine(workspacePath, ProjectsDirectoryName);
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            string oldName = ResolveFolderName(root, registry, id);
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return;
            }

            ThrowIfDuplicate(root, registry, id, newName);
            MoveFolder(workspacePath, root, registry, id, oldName, newName);
        }
    }

    /// <summary>Forgets the folder of a deleted project.</summary>
    public static void Unbind(string workspacePath, EntityId<ProjectIdTag> projectId)
    {
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            bool changed = registry.Folders.Remove(projectId.ToString());
            changed |= registry.Pending.Remove(projectId.ToString());
            if (changed)
            {
                Save(workspacePath, registry);
            }
        }
    }

    /// <summary>Completes or abandons renames that were interrupted. Safe to run any number of times.</summary>
    public static void Reconcile(string workspacePath)
    {
        string root = Path.Combine(workspacePath, ProjectsDirectoryName);
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            if (registry.Pending.Count == 0)
            {
                return;
            }

            foreach ((string id, string target) in registry.Pending.ToArray())
            {
                string current = registry.Folders.TryGetValue(id, out string? known) ? known : id;
                bool currentExists = Directory.Exists(Path.Combine(root, current));
                bool targetExists = Directory.Exists(Path.Combine(root, target));
                if (!currentExists && targetExists)
                {
                    // The move completed before the interruption: commit it.
                    registry.Folders[id] = target;
                }

                // Otherwise nothing was moved (or both exist): keep using the current folder.
                _ = registry.Pending.Remove(id);
            }

            Save(workspacePath, registry);
        }
    }

    /// <summary>
    /// FR-134: gives every project that still has a GUID-named folder its Project-name folder.
    /// Projects whose name is unusable, duplicated or whose target already exists keep the GUID folder.
    /// </summary>
    public static IReadOnlyList<ProjectFolderMigrationEntry> Migrate(
        string workspacePath,
        IReadOnlyList<(string ProjectId, string? Name)> projects)
    {
        string root = Path.Combine(workspacePath, ProjectsDirectoryName);
        var results = new List<ProjectFolderMigrationEntry>();
        Reconcile(workspacePath);
        lock (Gate)
        {
            Registry registry = Load(workspacePath);
            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach ((string _, string? name) in projects)
            {
                if (name is not null)
                {
                    nameCounts[name] = nameCounts.GetValueOrDefault(name) + 1;
                }
            }

            foreach ((string id, string? name) in projects)
            {
                if (registry.Folders.ContainsKey(id))
                {
                    results.Add(new(id, name, ProjectFolderMigrationStatus.AlreadyNamed));
                    continue;
                }

                if (name is null || ValidateName(name) is not null)
                {
                    results.Add(new(id, name, ProjectFolderMigrationStatus.SkippedInvalidName));
                    continue;
                }

                if (nameCounts[name] > 1)
                {
                    results.Add(new(id, name, ProjectFolderMigrationStatus.SkippedDuplicateName));
                    continue;
                }

                string guidPath = Path.Combine(root, id);
                bool guidExists = Directory.Exists(guidPath);
                bool clash = registry.Folders.Any(
                        f => string.Equals(f.Value, name, StringComparison.OrdinalIgnoreCase)) ||
                    (Directory.Exists(Path.Combine(root, name)) &&
                        !string.Equals(Path.GetFileName(guidPath), name, StringComparison.OrdinalIgnoreCase));
                if (clash)
                {
                    results.Add(new(id, name, ProjectFolderMigrationStatus.SkippedFolderExists));
                    continue;
                }

                try
                {
                    if (guidExists)
                    {
                        MoveFolder(workspacePath, root, registry, id, id, name);
                    }
                    else
                    {
                        registry.Folders[id] = name;
                        Save(workspacePath, registry);
                    }

                    results.Add(new(id, name, ProjectFolderMigrationStatus.Migrated));
                }
                catch (ProjectFolderException)
                {
                    results.Add(new(id, name, ProjectFolderMigrationStatus.SkippedFailed));
                }
            }
        }

        return results;
    }

    private static void MoveFolder(
        string workspacePath,
        string root,
        Registry registry,
        string id,
        string oldName,
        string newName)
    {
        string oldPath = Path.Combine(root, oldName);
        string newPath = Path.Combine(root, newName);
        registry.Pending[id] = newName;
        try
        {
            Save(workspacePath, registry);
            if (Directory.Exists(oldPath))
            {
                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    string hop = Path.Combine(root, ".rename-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(oldPath, hop);
                    try
                    {
                        Directory.Move(hop, newPath);
                    }
                    catch
                    {
                        Directory.Move(hop, oldPath);
                        throw;
                    }
                }
                else
                {
                    Directory.Move(oldPath, newPath);
                }
            }
            else
            {
                _ = Directory.CreateDirectory(root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ = registry.Pending.Remove(id);
            try
            {
                Save(workspacePath, registry);
            }
            catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException)
            {
                // The pending entry is resolved by Reconcile on the next start.
            }

            throw new ProjectFolderException(ProjectFolderFailureReason.RenameFailed);
        }

        registry.Folders[id] = newName;
        _ = registry.Pending.Remove(id);
        try
        {
            Save(workspacePath, registry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder is already renamed; Reconcile commits the pending entry on the next start,
            // and GetPath finds the renamed folder through the pending entry until then.
            registry.Folders[id] = oldName;
            registry.Pending[id] = newName;
            throw new ProjectFolderException(ProjectFolderFailureReason.RenameFailed);
        }
    }

    private static void ThrowIfInvalid(string name)
    {
        if (ValidateName(name) is not null)
        {
            throw new ProjectFolderException(ProjectFolderFailureReason.InvalidName);
        }
    }

    private static void ThrowIfDuplicate(string root, Registry registry, string selfId, string name)
    {
        foreach ((string id, string folder) in registry.Folders)
        {
            if (!string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(folder, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectFolderException(ProjectFolderFailureReason.DuplicateName);
            }
        }

        string target = Path.Combine(root, name);
        if (Directory.Exists(target) || File.Exists(target))
        {
            string current = selfId.Length == 0 ? string.Empty : ResolveFolderName(root, registry, selfId);
            if (!string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectFolderException(ProjectFolderFailureReason.DuplicateName);
            }
        }
    }

    private static string ResolveFolderName(string root, Registry registry, string projectId)
    {
        if (registry.Folders.TryGetValue(projectId, out string? name))
        {
            if (registry.Pending.TryGetValue(projectId, out string? pending) &&
                !Directory.Exists(Path.Combine(root, name)) &&
                Directory.Exists(Path.Combine(root, pending)))
            {
                return pending;
            }

            return name;
        }

        if (registry.Pending.TryGetValue(projectId, out string? movedTo) &&
            !Directory.Exists(Path.Combine(root, projectId)) &&
            Directory.Exists(Path.Combine(root, movedTo)))
        {
            return movedTo;
        }

        return projectId;
    }

    private static bool TrySplitProjectsPrefix(string relativePath, out string segment, out string rest)
    {
        segment = string.Empty;
        rest = string.Empty;
        const string prefix = ProjectsDirectoryName + "/";
        if (relativePath is null || !relativePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int end = relativePath.IndexOf('/', prefix.Length);
        segment = end < 0 ? relativePath[prefix.Length..] : relativePath[prefix.Length..end];
        rest = end < 0 ? string.Empty : relativePath[end..];
        return segment.Length > 0;
    }

    private sealed class Registry
    {
        public Dictionary<string, string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CacheEntry(DateTime Stamp, long Length, Registry Registry);

    private static Registry Load(string workspacePath)
    {
        string file = Path.Combine(workspacePath, RegistryFileName);
        FileInfo info = new(file);
        DateTime stamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        long length = info.Exists ? info.Length : -1;
        string key = Path.GetFullPath(workspacePath);
        if (Cache.TryGetValue(key, out CacheEntry? cached) && cached.Stamp == stamp && cached.Length == length)
        {
            return cached.Registry;
        }

        var registry = new Registry();
        if (info.Exists)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
                Fill(document.RootElement, "folders", registry.Folders);
                Fill(document.RootElement, "pending", registry.Pending);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                registry.Folders.Clear();
                registry.Pending.Clear();
            }
        }

        Cache[key] = new CacheEntry(stamp, length, registry);
        return registry;
    }

    private static void Fill(JsonElement root, string property, Dictionary<string, string> target)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in element.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } value)
                {
                    target[entry.Name] = value;
                }
            }
        }
    }

    private static void Save(string workspacePath, Registry registry)
    {
        string file = Path.Combine(workspacePath, RegistryFileName);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteMap(writer, "folders", registry.Folders);
            WriteMap(writer, "pending", registry.Pending);
            writer.WriteEndObject();
        }

        try
        {
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // best effort
            }

            throw;
        }

        FileInfo info = new(file);
        Cache[Path.GetFullPath(workspacePath)] = new CacheEntry(info.LastWriteTimeUtc, info.Length, registry);
    }

    private static void WriteMap(Utf8JsonWriter writer, string name, Dictionary<string, string> map)
    {
        writer.WriteStartObject(name);
        foreach ((string key, string value) in map.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }
}
