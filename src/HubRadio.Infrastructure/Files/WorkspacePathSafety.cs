using System.Security;

namespace HubRadio.Infrastructure.Files;

internal enum WorkspacePathSafetyFailure
{
    Missing,
    UnsafePath,
    NotRegularFile,
    ReparsePoint,
}

internal sealed class WorkspacePathSafetyException : Exception
{
    internal WorkspacePathSafetyException(WorkspacePathSafetyFailure failure)
        : base("The workspace path could not be validated safely.")
    {
        Failure = failure;
    }

    internal WorkspacePathSafetyFailure Failure { get; }
}

internal static class WorkspacePathSafety
{
    internal static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    internal static void ValidateExistingDirectoryAncestorChain(string directoryPath)
    {
        string? current = directoryPath;
        while (current is not null)
        {
            ValidateDirectoryAttributes(GetRequiredAttributes(current));

            try
            {
                current = Directory.GetParent(current)?.FullName;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                throw Failure(WorkspacePathSafetyFailure.UnsafePath);
            }
        }
    }

    internal static void ValidateExistingPathChain(
        string rootPath,
        string targetPath,
        bool targetIsDirectory)
    {
        if (!IsSameOrDescendant(rootPath, targetPath) ||
            !HasSameWindowsDriveRoot(rootPath, targetPath))
        {
            throw Failure(WorkspacePathSafetyFailure.UnsafePath);
        }

        string? current = targetPath;
        bool isTarget = true;
        while (current is not null)
        {
            FileAttributes attributes = GetRequiredAttributes(current);
            if (isTarget && !targetIsDirectory)
            {
                ValidateRegularFileAttributes(attributes);
            }
            else
            {
                ValidateDirectoryAttributes(attributes);
            }

            if (string.Equals(current, rootPath, PathComparison))
            {
                return;
            }

            current = Path.GetDirectoryName(current);
            isTarget = false;
        }

        throw Failure(WorkspacePathSafetyFailure.UnsafePath);
    }

    internal static FileAttributes GetRequiredAttributes(string path)
    {
        if (TryGetAttributes(path, out FileAttributes attributes))
        {
            return attributes;
        }

        throw Failure(WorkspacePathSafetyFailure.Missing);
    }

    internal static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SecurityException or
                NotSupportedException or ArgumentException)
        {
            throw Failure(WorkspacePathSafetyFailure.UnsafePath);
        }
    }

    internal static void ValidateDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(WorkspacePathSafetyFailure.ReparsePoint);
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.Device) != 0)
        {
            throw Failure(WorkspacePathSafetyFailure.UnsafePath);
        }
    }

    internal static void ValidateRegularFileAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(WorkspacePathSafetyFailure.ReparsePoint);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw Failure(WorkspacePathSafetyFailure.NotRegularFile);
        }
    }

    internal static bool IsValidPortableRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment[^1] is '.' or ' ' ||
                segment.Any(static character => character is '<' or '>' or '"' or '|' or '?' or '*') ||
                IsReservedWindowsDeviceSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryResolvePortableRelativePath(
        string rootPath,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (!IsValidPortableRelativePath(relativePath))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(
                rootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }

        if (!IsStrictDescendant(rootPath, fullPath) ||
            !HasSameWindowsDriveRoot(rootPath, fullPath))
        {
            fullPath = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool TryGetPortableRelativePath(
        string rootPath,
        string fullPath,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (!IsStrictDescendant(rootPath, fullPath) ||
            !HasSameWindowsDriveRoot(rootPath, fullPath))
        {
            return false;
        }

        try
        {
            relativePath = Path.GetRelativePath(rootPath, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            relativePath = string.Empty;
            return false;
        }

        if (!IsValidPortableRelativePath(relativePath))
        {
            relativePath = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool IsStrictDescendant(string rootPath, string candidatePath)
    {
        if (string.Equals(rootPath, candidatePath, PathComparison))
        {
            return false;
        }

        string prefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : string.Concat(rootPath, Path.DirectorySeparatorChar);
        return candidatePath.StartsWith(prefix, PathComparison);
    }

    internal static bool HasSameWindowsDriveRoot(string firstPath, string secondPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string? firstRoot = Path.GetPathRoot(firstPath);
        string? secondRoot = Path.GetPathRoot(secondPath);
        return !string.IsNullOrEmpty(firstRoot) &&
            !string.IsNullOrEmpty(secondRoot) &&
            string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PathEntryExistsConservatively(string path)
    {
        try
        {
            return TryGetAttributes(path, out _);
        }
        catch (WorkspacePathSafetyException)
        {
            return true;
        }
    }

    private static bool IsSameOrDescendant(string rootPath, string candidatePath) =>
        string.Equals(rootPath, candidatePath, PathComparison) ||
        IsStrictDescendant(rootPath, candidatePath);

    private static bool IsReservedWindowsDeviceSegment(string segment)
    {
        int extensionIndex = segment.IndexOf('.');
        string deviceName = extensionIndex < 0
            ? segment
            : segment[..extensionIndex];

        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (deviceName.Length != 4 ||
            (!deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                !deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return deviceName[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    private static WorkspacePathSafetyException Failure(
        WorkspacePathSafetyFailure failure) =>
        new(failure);
}