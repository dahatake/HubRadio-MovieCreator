using System.Collections;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Security;

public enum WorkspaceAclFailureReason
{
    UnsafePath,
    UnsupportedVolume,
    ParentDirectoryMissing,
    WorkspaceMissing,
    WorkspaceAlreadyExists,
    WorkspaceNotDirectory,
    ReparsePoint,
    AccessDenied,
    CreationFailed,
    WorkspaceNotEmpty,
    VerificationFailed,
}

public sealed class WorkspaceAclException : Exception
{
    internal WorkspaceAclException(
        WorkspaceAclFailureReason reason,
        bool reconciliationRequired = false)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
        ReconciliationRequired = reconciliationRequired;
    }

    public WorkspaceAclFailureReason Reason { get; }

    public bool ReconciliationRequired { get; }

    private static string GetStableMessage(WorkspaceAclFailureReason reason) =>
        reason switch
        {
            WorkspaceAclFailureReason.UnsafePath =>
                "The workspace path is unsafe.",
            WorkspaceAclFailureReason.UnsupportedVolume =>
                "The workspace must be on a local fixed drive.",
            WorkspaceAclFailureReason.ParentDirectoryMissing =>
                "The workspace parent directory does not exist.",
            WorkspaceAclFailureReason.WorkspaceMissing =>
                "The workspace directory does not exist.",
            WorkspaceAclFailureReason.WorkspaceAlreadyExists =>
                "The workspace path already exists.",
            WorkspaceAclFailureReason.WorkspaceNotDirectory =>
                "The workspace path is not a directory.",
            WorkspaceAclFailureReason.ReparsePoint =>
                "The workspace path contains a reparse point.",
            WorkspaceAclFailureReason.AccessDenied =>
                "The workspace access control operation was denied.",
            WorkspaceAclFailureReason.CreationFailed =>
                "The workspace directory could not be created safely.",
            WorkspaceAclFailureReason.WorkspaceNotEmpty =>
                "The new workspace directory is not empty.",
            WorkspaceAclFailureReason.VerificationFailed =>
                "The workspace access control settings are invalid.",
            _ => "The workspace access control operation failed.",
        };
}

public sealed class WorkspaceAclResult
{
    internal WorkspaceAclResult(
        bool ownerMatchesCurrentActor,
        bool protectedAccessRules,
        int explicitRuleCount,
        bool inheritanceConfigured,
        bool crossAccountAccessTested)
    {
        OwnerMatchesCurrentActor = ownerMatchesCurrentActor;
        Protected = protectedAccessRules;
        ExplicitRuleCount = explicitRuleCount;
        InheritanceConfigured = inheritanceConfigured;
        CrossAccountAccessTested = crossAccountAccessTested;
    }

    public bool OwnerMatchesCurrentActor { get; }

    public bool Protected { get; }

    public int ExplicitRuleCount { get; }

    public bool InheritanceConfigured { get; }

    public bool CrossAccountAccessTested { get; }
}

public static class WorkspaceAcl
{
    private const int ExpectedExplicitRuleCount = 3;

    public static WorkspaceAclResult CreateNew(
        string workspacePath,
        string currentActorSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Workspace access control requires Windows.");
        }

        string normalizedPath = NormalizeWorkspacePath(workspacePath);
        SecurityIdentifier actorSid = ParseActorSid(currentActorSid);

        ValidateFixedDrive(normalizedPath);
        ValidateParentForCreation(normalizedPath);
        EnsureTargetAbsent(normalizedPath);

        DirectorySecurity directorySecurity = CreateDirectorySecurity(actorSid);
        DirectoryInfo createdDirectory;
        try
        {
            createdDirectory = FileSystemAclExtensions.CreateDirectory(
                directorySecurity,
                normalizedPath);
        }
        catch (DirectoryNotFoundException)
        {
            throw Failure(WorkspaceAclFailureReason.ParentDirectoryMissing);
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(WorkspaceAclFailureReason.AccessDenied);
        }
        catch (SecurityException)
        {
            throw Failure(WorkspaceAclFailureReason.AccessDenied);
        }
        catch (IOException)
        {
            WorkspaceAclFailureReason reason =
                WorkspacePathSafety.PathEntryExistsConservatively(normalizedPath)
                    ? WorkspaceAclFailureReason.WorkspaceAlreadyExists
                    : WorkspaceAclFailureReason.CreationFailed;
            throw Failure(reason);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException)
        {
            throw Failure(WorkspaceAclFailureReason.CreationFailed);
        }

        try
        {
            if (!string.Equals(
                Path.TrimEndingDirectorySeparator(createdDirectory.FullName),
                normalizedPath,
                WorkspacePathSafety.PathComparison))
            {
                throw Failure(WorkspaceAclFailureReason.CreationFailed);
            }

            ValidateExistingWorkspacePath(normalizedPath);
            EnsureNewWorkspaceIsEmpty(normalizedPath);
            WorkspaceAclResult result = VerifyExistingCore(normalizedPath, actorSid);
            EnsureNewWorkspaceIsEmpty(normalizedPath);
            return result;
        }
        catch (WorkspaceAclException exception)
        {
            throw FailureAfterCreation(exception.Reason);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or SecurityException)
        {
            throw FailureAfterCreation(WorkspaceAclFailureReason.AccessDenied);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            throw FailureAfterCreation(
                WorkspaceAclFailureReason.VerificationFailed);
        }
    }

    public static WorkspaceAclResult VerifyExisting(
        string workspacePath,
        string currentActorSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Workspace access control requires Windows.");
        }

        string normalizedPath = NormalizeWorkspacePath(workspacePath);
        SecurityIdentifier actorSid = ParseActorSid(currentActorSid);

        ValidateFixedDrive(normalizedPath);
        ValidateExistingWorkspacePath(normalizedPath);
        try
        {
            return VerifyExistingCore(normalizedPath, actorSid);
        }
        catch (WorkspaceAclException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or SecurityException)
        {
            throw Failure(WorkspaceAclFailureReason.AccessDenied);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }
    }

    [SupportedOSPlatform("windows")]
    private static WorkspaceAclResult VerifyExistingCore(
        string normalizedPath,
        SecurityIdentifier actorSid)
    {
        var directory = new DirectoryInfo(normalizedPath);
        DirectorySecurity security = FileSystemAclExtensions.GetAccessControl(
            directory,
            AccessControlSections.Access | AccessControlSections.Owner);

        if (!security.AreAccessRulesProtected)
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }

        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier ownerSid || !ownerSid.Equals(actorSid))
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }

        FileSystemAccessRule[] inheritedRules = GetAccessRules(
            security,
            includeExplicit: false,
            includeInherited: true);
        if (inheritedRules.Length != 0)
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }

        FileSystemAccessRule[] explicitRules = GetAccessRules(
            security,
            includeExplicit: true,
            includeInherited: false);
        if (explicitRules.Length != ExpectedExplicitRuleCount)
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }

        var expectedSids = new HashSet<string>(StringComparer.Ordinal)
        {
            actorSid.Value,
            CreateWellKnownSid(WellKnownSidType.LocalSystemSid).Value,
            CreateWellKnownSid(WellKnownSidType.BuiltinAdministratorsSid).Value,
        };

        foreach (FileSystemAccessRule rule in explicitRules)
        {
            if (rule.IsInherited ||
                rule.IdentityReference is not SecurityIdentifier ruleSid ||
                !expectedSids.Remove(ruleSid.Value) ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != GetExpectedInheritance() ||
                rule.PropagationFlags != PropagationFlags.None)
            {
                throw Failure(WorkspaceAclFailureReason.VerificationFailed);
            }
        }

        if (expectedSids.Count != 0)
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }

        ValidateExistingWorkspacePath(normalizedPath);
        return new WorkspaceAclResult(
            ownerMatchesCurrentActor: true,
            protectedAccessRules: true,
            explicitRules.Length,
            inheritanceConfigured: true,
            crossAccountAccessTested: false);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateDirectorySecurity(
        SecurityIdentifier actorSid)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(actorSid);
        AddFullControlRule(security, actorSid);
        AddFullControlRule(
            security,
            CreateWellKnownSid(WellKnownSidType.LocalSystemSid));
        AddFullControlRule(
            security,
            CreateWellKnownSid(WellKnownSidType.BuiltinAdministratorsSid));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControlRule(
        DirectorySecurity security,
        SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            GetExpectedInheritance(),
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule[] GetAccessRules(
        DirectorySecurity security,
        bool includeExplicit,
        bool includeInherited)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit,
            includeInherited,
            typeof(SecurityIdentifier));
        var typedRules = new List<FileSystemAccessRule>(rules.Count);
        foreach (AuthorizationRule rule in (IEnumerable)rules)
        {
            if (rule is not FileSystemAccessRule fileSystemRule)
            {
                throw Failure(WorkspaceAclFailureReason.VerificationFailed);
            }

            typedRules.Add(fileSystemRule);
        }

        return typedRules.ToArray();
    }

    private static string NormalizeWorkspacePath(string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        string trimmedPath = workspacePath.Trim();
        if (trimmedPath.Length == 0 ||
            !Path.IsPathFullyQualified(trimmedPath) ||
            !HasLocalDrivePrefix(trimmedPath))
        {
            throw InvalidPathArgument(nameof(workspacePath));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmedPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPathArgument(nameof(workspacePath));
        }

        string? root = Path.GetPathRoot(fullPath);
        if (root is null ||
            !HasLocalDrivePrefix(root) ||
            fullPath.AsSpan(root.Length).Contains(':'))
        {
            throw InvalidPathArgument(nameof(workspacePath));
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ParseActorSid(string currentActorSid)
    {
        ArgumentNullException.ThrowIfNull(currentActorSid);
        string trimmedSid = currentActorSid.Trim();
        if (trimmedSid.Length == 0)
        {
            throw InvalidSidArgument();
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(trimmedSid);
        }
        catch (ArgumentException)
        {
            throw InvalidSidArgument();
        }

        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
        {
            throw InvalidSidArgument();
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? processUserSid = identity.User;
            if (processUserSid is null)
            {
                throw Failure(WorkspaceAclFailureReason.VerificationFailed);
            }

            if (!sid.Equals(processUserSid))
            {
                throw InvalidSidArgument();
            }
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or SecurityException)
        {
            throw Failure(WorkspaceAclFailureReason.AccessDenied);
        }

        return sid;
    }

    private static void ValidateFixedDrive(string path)
    {
        string root = Path.GetPathRoot(path)
            ?? throw Failure(WorkspaceAclFailureReason.UnsafePath);
        try
        {
            FileAttributes rootAttributes = WorkspacePathSafety.GetRequiredAttributes(root);
            WorkspacePathSafety.ValidateDirectoryAttributes(rootAttributes);
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                throw Failure(WorkspaceAclFailureReason.UnsupportedVolume);
            }
        }
        catch (WorkspaceAclException)
        {
            throw;
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                WorkspaceAclFailureReason.UnsupportedVolume));
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            throw Failure(WorkspaceAclFailureReason.UnsupportedVolume);
        }
    }

    private static void ValidateParentForCreation(string path)
    {
        string? parentPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parentPath) ||
            string.Equals(parentPath, path, WorkspacePathSafety.PathComparison))
        {
            throw Failure(WorkspaceAclFailureReason.WorkspaceAlreadyExists);
        }

        try
        {
            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(parentPath);
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                WorkspaceAclFailureReason.ParentDirectoryMissing));
        }
    }

    private static void EnsureTargetAbsent(string path)
    {
        try
        {
            if (!WorkspacePathSafety.TryGetAttributes(path, out FileAttributes attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(WorkspaceAclFailureReason.ReparsePoint);
            }

            throw Failure(WorkspaceAclFailureReason.WorkspaceAlreadyExists);
        }
        catch (WorkspaceAclException)
        {
            throw;
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                WorkspaceAclFailureReason.WorkspaceAlreadyExists));
        }
    }

    private static void ValidateExistingWorkspacePath(string path)
    {
        try
        {
            FileAttributes attributes = WorkspacePathSafety.GetRequiredAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(WorkspaceAclFailureReason.ReparsePoint);
            }

            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.Device) != 0)
            {
                throw Failure(WorkspaceAclFailureReason.WorkspaceNotDirectory);
            }

            WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(path);
        }
        catch (WorkspaceAclException)
        {
            throw;
        }
        catch (WorkspacePathSafetyException exception)
        {
            throw Failure(MapPathFailure(
                exception.Failure,
                WorkspaceAclFailureReason.WorkspaceMissing));
        }
    }

    private static void EnsureNewWorkspaceIsEmpty(string path)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw Failure(WorkspaceAclFailureReason.WorkspaceNotEmpty);
            }
        }
        catch (WorkspaceAclException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(WorkspaceAclFailureReason.AccessDenied);
        }
        catch (SecurityException)
        {
            throw Failure(WorkspaceAclFailureReason.AccessDenied);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException)
        {
            throw Failure(WorkspaceAclFailureReason.VerificationFailed);
        }
    }

    private static bool HasLocalDrivePrefix(string path) =>
        path.Length >= 3 &&
        IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        IsDirectorySeparator(path[2]);

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsDirectorySeparator(char value) =>
        value is '\\' or '/';

    [SupportedOSPlatform("windows")]
    private static InheritanceFlags GetExpectedInheritance() =>
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CreateWellKnownSid(WellKnownSidType type) =>
        new(type, domainSid: null);

    private static WorkspaceAclFailureReason MapPathFailure(
        WorkspacePathSafetyFailure failure,
        WorkspaceAclFailureReason missingReason) =>
        failure switch
        {
            WorkspacePathSafetyFailure.Missing => missingReason,
            WorkspacePathSafetyFailure.ReparsePoint => WorkspaceAclFailureReason.ReparsePoint,
            WorkspacePathSafetyFailure.NotRegularFile => WorkspaceAclFailureReason.WorkspaceNotDirectory,
            _ => WorkspaceAclFailureReason.UnsafePath,
        };

    private static WorkspaceAclException FailureAfterCreation(
        WorkspaceAclFailureReason reason)
    {
        // CreateDirectory may return a directory created concurrently by another caller.
        // Without a retained file identity, deleting the path cannot prove ownership.
        return Failure(reason, reconciliationRequired: true);
    }

    private static WorkspaceAclException Failure(
        WorkspaceAclFailureReason reason,
        bool reconciliationRequired = false) =>
        new(reason, reconciliationRequired);

    private static ArgumentException InvalidPathArgument(string parameterName) =>
        new(
            "The workspace path must be a valid fully qualified path on a local drive without a device namespace or alternate data stream.",
            parameterName);

    private static ArgumentException InvalidSidArgument() =>
        new(
            "The current actor SID must be a valid distinct Windows user security identifier.",
            "currentActorSid");

}