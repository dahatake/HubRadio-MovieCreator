namespace HubRadio.Infrastructure.Diagnostics;

public sealed class DiagnosticLogOptions
{
    public const int DefaultMaxTotalBytes = 1024 * 1024;
    public const int MinimumMaxTotalBytes = 1024;
    public const int MaximumMaxTotalBytes = 16 * 1024 * 1024;

    public DiagnosticLogOptions(
        string filePath,
        int maxTotalBytes = DefaultMaxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (string.IsNullOrWhiteSpace(filePath) ||
            !string.Equals(filePath, filePath.Trim(), StringComparison.Ordinal) ||
            filePath.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException(
                "The diagnostic log path must be a nonblank absolute file path without control characters.",
                nameof(filePath));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "The diagnostic log path must be a valid absolute file path.",
                nameof(filePath));
        }

        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)) ||
            Path.EndsInDirectorySeparator(fullPath))
        {
            throw new ArgumentException(
                "The diagnostic log path must identify a file.",
                nameof(filePath));
        }

        ValidateLocalStoragePath(fullPath, nameof(filePath));

        if (maxTotalBytes < MinimumMaxTotalBytes ||
            maxTotalBytes > MaximumMaxTotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalBytes),
                $"The diagnostic log limit must be between {MinimumMaxTotalBytes} and {MaximumMaxTotalBytes} bytes.");
        }

        FilePath = fullPath;
        MaxTotalBytes = maxTotalBytes;
    }

    public string FilePath { get; }

    public int MaxTotalBytes { get; }

    internal void ValidateForWrite()
    {
        ValidateLocalStoragePath(FilePath, "filePath");
        ValidateNoExistingReparsePoint(FilePath + ".lock", "filePath");
        ValidateNoExistingReparsePoint(FilePath + ".tmp", "filePath");
    }

    private static void ValidateLocalStoragePath(
        string fullPath,
        string parameterName)
    {
        if (OperatingSystem.IsWindows())
        {
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            bool isDriveLetterRoot = root.Length == 3 &&
                char.IsAsciiLetter(root[0]) &&
                root[1] == ':' &&
                Path.EndsInDirectorySeparator(root);
            if (!isDriveLetterRoot || fullPath.AsSpan(root.Length).Contains(':'))
            {
                throw new ArgumentException(
                    "The diagnostic log path must use a local drive and must not use a device namespace or alternate data stream.",
                    parameterName);
            }

            try
            {
                if (new DriveInfo(root).DriveType != DriveType.Fixed)
                {
                    throw new ArgumentException(
                        "The diagnostic log path must use a fixed local disk.",
                        parameterName);
                }
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException(
                    "The diagnostic log drive could not be validated.",
                    parameterName);
            }
        }

        ValidateNoExistingReparsePoint(fullPath, parameterName);
    }

    private static void ValidateNoExistingReparsePoint(
        string fullPath,
        string parameterName)
    {
        FileInfo file = new(fullPath);
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "The diagnostic log path must not contain a reparse point.",
                parameterName);
        }

        for (DirectoryInfo? directory = file.Directory;
             directory is not null;
             directory = directory.Parent)
        {
            if (directory.Exists &&
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "The diagnostic log path must not contain a reparse point.",
                    parameterName);
            }
        }
    }
}