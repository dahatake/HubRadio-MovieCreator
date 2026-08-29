using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using HubRadio.Infrastructure.Security;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WorkspaceAclTests
{
    private const InheritanceFlags ExpectedInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    [TestMethod]
    public void CreateNew_CreatesProtectedOwnedExactAclAndVerifiedResult()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();

        WorkspaceAclResult created = WorkspaceAcl.CreateNew(
            fixture.WorkspacePath,
            fixture.CurrentSid);

        AssertSuccessfulResult(created);
        Assert.IsTrue(Directory.Exists(fixture.WorkspacePath));
        Assert.AreEqual(
            FileAttributes.Directory,
            File.GetAttributes(fixture.WorkspacePath) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.WorkspacePath));
        AssertExactRootAcl(fixture.WorkspacePath, fixture.CurrentSid);

        WorkspaceAclResult verified = WorkspaceAcl.VerifyExisting(
            fixture.WorkspacePath,
            fixture.CurrentSid);

        AssertSuccessfulResult(verified);
    }

    [TestMethod]
    public void CreateNew_TrimsPathAndCanonicalizesActorSid()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();
        string surroundedPath = string.Concat("  ", fixture.WorkspacePath, "  ");
        string surroundedSid = string.Concat("  ", fixture.CurrentSid, "  ");

        WorkspaceAclResult result = WorkspaceAcl.CreateNew(
            surroundedPath,
            surroundedSid);

        AssertSuccessfulResult(result);
        AssertExactRootAcl(fixture.WorkspacePath, fixture.CurrentSid);
    }

    [TestMethod]
    public void CreatedChildrenInheritOnlyTheThreeExpectedFullControlRules()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();
        _ = WorkspaceAcl.CreateNew(fixture.WorkspacePath, fixture.CurrentSid);
        string childDirectory = Path.Combine(fixture.WorkspacePath, "child");
        string childFile = Path.Combine(childDirectory, "artifact.bin");
        Directory.CreateDirectory(childDirectory);
        File.WriteAllBytes(childFile, [1, 2, 3]);

        DirectorySecurity directorySecurity = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(childDirectory),
            AccessControlSections.Access);
        FileSecurity fileSecurity = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(childFile),
            AccessControlSections.Access);

        AssertInheritedRules(
            directorySecurity,
            fixture.CurrentSid,
            ExpectedInheritance);
        AssertInheritedRules(
            fileSecurity,
            fixture.CurrentSid,
            InheritanceFlags.None);
    }

    [TestMethod]
    public void VerifyInheritedDirectory_AcceptsExactChildAndRejectsExplicitRule()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        string childDirectory = Path.Combine(fixture.WorkspacePath, "backup-child");
        Directory.CreateDirectory(childDirectory);

        WorkspaceAcl.VerifyInheritedDirectory(
            childDirectory,
            fixture.WorkspacePath,
            fixture.CurrentSid);

        DirectorySecurity security = ReadDirectorySecurity(childDirectory);
        security.AddAccessRule(new FileSystemAccessRule(
            CreateWellKnownSid(WellKnownSidType.BuiltinUsersSid),
            FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(childDirectory),
            security);

        WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.VerifyInheritedDirectory(
                childDirectory,
                fixture.WorkspacePath,
                fixture.CurrentSid));
        AssertSafeFailure(
            exception,
            WorkspaceAclFailureReason.VerificationFailed,
            reconciliationRequired: false,
            childDirectory,
            fixture.WorkspacePath,
            fixture.CurrentSid);
    }

    [TestMethod]
    public void VerifyInheritedDirectory_RequiresStrictDescendantOfProtectedRoot()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        string outsideDirectory = Path.Combine(fixture.RootPath, "outside");
        Directory.CreateDirectory(outsideDirectory);

        foreach (string candidate in new[]
        {
            fixture.WorkspacePath,
            outsideDirectory,
        })
        {
            WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(() =>
                WorkspaceAcl.VerifyInheritedDirectory(
                    candidate,
                    fixture.WorkspacePath,
                    fixture.CurrentSid));
            AssertSafeFailure(
                exception,
                WorkspaceAclFailureReason.UnsafePath,
                reconciliationRequired: false,
                candidate,
                fixture.WorkspacePath,
                fixture.CurrentSid);
        }
    }

    [TestMethod]
    public void VerifyInheritedDirectory_RejectsDescendantReparsePointWhenAvailable()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        string linkTarget = Path.Combine(fixture.RootPath, "backup-target");
        Directory.CreateDirectory(linkTarget);
        string childLink = Path.Combine(fixture.WorkspacePath, "backup-link");
        if (!TryCreateDirectorySymbolicLink(childLink, linkTarget))
        {
            Assert.Inconclusive(
                "Directory symbolic links are unavailable in this test environment.");
            return;
        }

        WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.VerifyInheritedDirectory(
                childLink,
                fixture.WorkspacePath,
                fixture.CurrentSid));
        AssertSafeFailure(
            exception,
            WorkspaceAclFailureReason.ReparsePoint,
            reconciliationRequired: false,
            childLink,
            fixture.WorkspacePath,
            fixture.CurrentSid);
    }

    [TestMethod]
    public void VerifyExisting_AllowsContentAndDoesNotRewriteAcl()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();
        _ = WorkspaceAcl.CreateNew(fixture.WorkspacePath, fixture.CurrentSid);
        string contentPath = Path.Combine(fixture.WorkspacePath, "existing.bin");
        File.WriteAllBytes(contentPath, [4, 5, 6]);
        byte[] before = ReadSecurityDescriptor(fixture.WorkspacePath);

        WorkspaceAclResult result = WorkspaceAcl.VerifyExisting(
            fixture.WorkspacePath,
            fixture.CurrentSid);

        AssertSuccessfulResult(result);
        CollectionAssert.AreEqual(before, ReadSecurityDescriptor(fixture.WorkspacePath));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(contentPath));
    }

    [TestMethod]
    public void CreateNew_RejectsExistingWorkspaceWithoutChangingAclOrContent()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();
        Directory.CreateDirectory(fixture.WorkspacePath);
        string existingPath = Path.Combine(fixture.WorkspacePath, "existing.bin");
        File.WriteAllBytes(existingPath, [7, 8, 9]);
        byte[] before = ReadSecurityDescriptor(fixture.WorkspacePath);

        WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.CreateNew(fixture.WorkspacePath, fixture.CurrentSid));

        AssertSafeFailure(
            exception,
            WorkspaceAclFailureReason.WorkspaceAlreadyExists,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            fixture.CurrentSid);
        CollectionAssert.AreEqual(before, ReadSecurityDescriptor(fixture.WorkspacePath));
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(existingPath));
    }

    [TestMethod]
    public void VerifyExisting_RejectsMissingExpectedRule()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        FileSystemAccessRule systemRule = GetRules(
                security,
                includeExplicit: true,
                includeInherited: false)
            .Single(rule => GetSid(rule).IsWellKnown(WellKnownSidType.LocalSystemSid));
        security.RemoveAccessRuleSpecific(systemRule);
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsUnexpectedRule()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        security.AddAccessRule(CreateRule(
            CreateWellKnownSid(WellKnownSidType.BuiltinUsersSid),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsDenyRule()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        var unrelatedSid = new SecurityIdentifier("S-1-5-21-1-2-3-424242");
        security.AddAccessRule(CreateRule(
            unrelatedSid,
            FileSystemRights.ReadData,
            AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsUnprotectedDacl()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsWrongRights()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        FileSystemAccessRule systemRule = GetRules(
                security,
                includeExplicit: true,
                includeInherited: false)
            .Single(rule => GetSid(rule).IsWellKnown(WellKnownSidType.LocalSystemSid));
        security.RemoveAccessRuleSpecific(systemRule);
        security.AddAccessRule(CreateRule(
            CreateWellKnownSid(WellKnownSidType.LocalSystemSid),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsWrongInheritanceFlags()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        FileSystemAccessRule systemRule = GetRules(
                security,
                includeExplicit: true,
                includeInherited: false)
            .Single(rule => GetSid(rule).IsWellKnown(WellKnownSidType.LocalSystemSid));
        security.RemoveAccessRuleSpecific(systemRule);
        security.AddAccessRule(new FileSystemAccessRule(
            CreateWellKnownSid(WellKnownSidType.LocalSystemSid),
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsWrongPropagationFlags()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        FileSystemAccessRule systemRule = GetRules(
                security,
                includeExplicit: true,
                includeInherited: false)
            .Single(rule => GetSid(rule).IsWellKnown(WellKnownSidType.LocalSystemSid));
        security.RemoveAccessRuleSpecific(systemRule);
        security.AddAccessRule(new FileSystemAccessRule(
            CreateWellKnownSid(WellKnownSidType.LocalSystemSid),
            FileSystemRights.FullControl,
            ExpectedInheritance,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(fixture.WorkspacePath),
            security);

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void VerifyExisting_RejectsWrongOwnerWhenOwnerChangeIsPermitted()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = CreateWorkspace();
        DirectorySecurity security = ReadDirectorySecurity(fixture.WorkspacePath);
        security.SetOwner(CreateWellKnownSid(WellKnownSidType.BuiltinAdministratorsSid));
        try
        {
            FileSystemAclExtensions.SetAccessControl(
                new DirectoryInfo(fixture.WorkspacePath),
                security);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            Assert.Inconclusive("Changing the directory owner is not permitted in this test environment.");
            return;
        }

        AssertVerificationFailure(fixture);
    }

    [TestMethod]
    public void MethodsRejectNullWhitespaceAndInvalidSidsWithoutDisclosure()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows path and SID validation requires Windows.");
            return;
        }

        string target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string currentSid = GetCurrentSid();
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            WorkspaceAcl.CreateNew(null!, currentSid));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            WorkspaceAcl.CreateNew(target, null!));

        foreach (string invalidPath in new[] { string.Empty, " ", "\t\r\n" })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                WorkspaceAcl.CreateNew(invalidPath, currentSid));
            Assert.AreEqual("workspacePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, target);
            Assert.IsFalse(Directory.Exists(target));
        }

        foreach (string invalidSid in new[]
        {
            string.Empty,
            " ",
            "not-a-sid",
            "S-1-5-invalid",
            "S-1-5-18",
            "S-1-5-32-544",
            "S-1-1-0",
            "S-1-5-32-545",
            "S-1-5-21-1-2-3-424242",
        })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                WorkspaceAcl.CreateNew(target, invalidSid));
            Assert.AreEqual("currentActorSid", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidSid);
            Assert.IsFalse(Directory.Exists(target));
        }
    }

    [TestMethod]
    public void MethodsRejectRelativeUncDeviceAndAdsPathsWithoutDisclosure()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows path namespace validation requires Windows.");
            return;
        }

        string currentSid = GetCurrentSid();
        string ordinaryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string[] invalidPaths =
        [
            "relative-workspace",
            @"\rooted-but-drive-relative",
            @"\\server\share\workspace",
            @"\\?\C:\workspace",
            @"\\.\C:\workspace",
            @"\??\C:\workspace",
            ordinaryPath + ":private-stream",
        ];

        foreach (string invalidPath in invalidPaths)
        {
            ArgumentException createException = Assert.ThrowsExactly<ArgumentException>(() =>
                WorkspaceAcl.CreateNew(invalidPath, currentSid));
            ArgumentException verifyException = Assert.ThrowsExactly<ArgumentException>(() =>
                WorkspaceAcl.VerifyExisting(invalidPath, currentSid));
            Assert.AreEqual("workspacePath", createException.ParamName);
            Assert.AreEqual("workspacePath", verifyException.ParamName);
            AssertDoesNotContain(createException.Message, invalidPath);
            AssertDoesNotContain(verifyException.Message, invalidPath);
        }
    }

    [TestMethod]
    public void CreateNew_RequiresExistingParentAndDoesNotCreateAncestors()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();
        string missingParent = Path.Combine(fixture.RootPath, "missing", "parent");
        string target = Path.Combine(missingParent, "workspace");

        WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.CreateNew(target, fixture.CurrentSid));

        AssertSafeFailure(
            exception,
            WorkspaceAclFailureReason.ParentDirectoryMissing,
            reconciliationRequired: false,
            target,
            fixture.CurrentSid);
        Assert.IsFalse(Directory.Exists(missingParent));
        Assert.IsFalse(Directory.Exists(target));
    }

    [TestMethod]
    public void VerifyExisting_RejectsMissingAndNonDirectoryTargets()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();

        WorkspaceAclException missing = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.VerifyExisting(fixture.WorkspacePath, fixture.CurrentSid));
        AssertSafeFailure(
            missing,
            WorkspaceAclFailureReason.WorkspaceMissing,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            fixture.CurrentSid);

        File.WriteAllBytes(fixture.WorkspacePath, [1]);
        WorkspaceAclException file = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.VerifyExisting(fixture.WorkspacePath, fixture.CurrentSid));
        AssertSafeFailure(
            file,
            WorkspaceAclFailureReason.WorkspaceNotDirectory,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            fixture.CurrentSid);
    }

    [TestMethod]
    public void MethodsRejectReparsePointTargetAndAncestorWhenLinksAreAvailable()
    {
        RequireWindowsFixedTempDrive();
        using WorkspaceFixture fixture = new();
        string linkTarget = Path.Combine(fixture.RootPath, "link-target");
        Directory.CreateDirectory(linkTarget);
        string targetLink = fixture.WorkspacePath;
        if (!TryCreateDirectorySymbolicLink(targetLink, linkTarget))
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
            return;
        }

        AssertReparseFailure(
            () => WorkspaceAcl.CreateNew(targetLink, fixture.CurrentSid),
            targetLink,
            fixture.CurrentSid);
        AssertReparseFailure(
            () => WorkspaceAcl.VerifyExisting(targetLink, fixture.CurrentSid),
            targetLink,
            fixture.CurrentSid);

        Directory.Delete(targetLink);
        string ancestorTarget = Path.Combine(fixture.RootPath, "ancestor-target");
        Directory.CreateDirectory(ancestorTarget);
        string ancestorLink = Path.Combine(fixture.RootPath, "ancestor-link");
        if (!TryCreateDirectorySymbolicLink(ancestorLink, ancestorTarget))
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this test environment.");
            return;
        }

        string throughLink = Path.Combine(ancestorLink, "workspace");
        AssertReparseFailure(
            () => WorkspaceAcl.CreateNew(throughLink, fixture.CurrentSid),
            throughLink,
            fixture.CurrentSid);
        Assert.IsFalse(Directory.Exists(Path.Combine(ancestorTarget, "workspace")));
    }

    [TestMethod]
    public void UnsupportedPlatformFailsClosedBeforePathOrSidProcessing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The unsupported-platform path requires a non-Windows runner.");
            return;
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            WorkspaceAcl.CreateNew(null!, null!));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            WorkspaceAcl.VerifyExisting(null!, null!));
    }

    [TestMethod]
    public void PublicSurfaceIsStaticMinimalImmutableAndPathSidPrivate()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "UnsafePath",
                "UnsupportedVolume",
                "ParentDirectoryMissing",
                "WorkspaceMissing",
                "WorkspaceAlreadyExists",
                "WorkspaceNotDirectory",
                "ReparsePoint",
                "AccessDenied",
                "CreationFailed",
                "WorkspaceNotEmpty",
                "VerificationFailed",
            },
            Enum.GetNames<WorkspaceAclFailureReason>());

        Assert.IsTrue(typeof(WorkspaceAcl).IsPublic);
        Assert.IsTrue(typeof(WorkspaceAcl).IsAbstract);
        Assert.IsTrue(typeof(WorkspaceAcl).IsSealed);
        MethodInfo[] methods = typeof(WorkspaceAcl)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[] { "CreateNew", "VerifyExisting" },
            methods.Select(static method => method.Name).ToArray());
        Assert.IsTrue(methods.All(static method =>
            method.ReturnType == typeof(WorkspaceAclResult) &&
            method.GetParameters().Select(static parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(string), typeof(string) })));

        AssertImmutableContract(
            typeof(WorkspaceAclResult),
            "OwnerMatchesCurrentActor",
            "Protected",
            "ExplicitRuleCount",
            "InheritanceConfigured",
            "CrossAccountAccessTested");
        AssertImmutableContract(
            typeof(WorkspaceAclException),
            "Reason",
            "ReconciliationRequired");
        Assert.AreEqual(0, typeof(WorkspaceAclResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(WorkspaceAclException).GetConstructors().Length);
        Assert.IsFalse(typeof(WorkspaceAclResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(static property => property.PropertyType == typeof(string)));

        const string privatePath = @"C:\private\customer-workspace";
        const string privateSid = "S-1-5-21-111-222-333-444";
        foreach (WorkspaceAclFailureReason reason in Enum.GetValues<WorkspaceAclFailureReason>())
        {
            var exception = new WorkspaceAclException(
                reason,
                reconciliationRequired: true);
            AssertSafeFailure(
                exception,
                reason,
                reconciliationRequired: true,
                privatePath,
                privateSid);
        }
    }

    private static WorkspaceFixture CreateWorkspace()
    {
        var fixture = new WorkspaceFixture();
        _ = WorkspaceAcl.CreateNew(fixture.WorkspacePath, fixture.CurrentSid);
        return fixture;
    }

    private static void AssertSuccessfulResult(WorkspaceAclResult result)
    {
        Assert.IsTrue(result.OwnerMatchesCurrentActor);
        Assert.IsTrue(result.Protected);
        Assert.AreEqual(3, result.ExplicitRuleCount);
        Assert.IsTrue(result.InheritanceConfigured);
        Assert.IsFalse(result.CrossAccountAccessTested);
    }

    private static void AssertExactRootAcl(string path, string currentSid)
    {
        DirectorySecurity security = ReadDirectorySecurity(path);
        Assert.IsTrue(security.AreAccessRulesProtected);
        SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new AssertFailedException("The workspace ACL owner was not returned as a SID.");
        Assert.AreEqual(
            currentSid,
            owner.Value);
        Assert.AreEqual(
            0,
            GetRules(security, includeExplicit: false, includeInherited: true).Length);
        AssertExpectedRules(
            GetRules(security, includeExplicit: true, includeInherited: false),
            currentSid,
            isInherited: false,
            ExpectedInheritance);
    }

    private static void AssertInheritedRules(
        FileSystemSecurity security,
        string currentSid,
        InheritanceFlags expectedInheritance)
    {
        Assert.AreEqual(
            0,
            GetRules(security, includeExplicit: true, includeInherited: false).Length);
        AssertExpectedRules(
            GetRules(security, includeExplicit: false, includeInherited: true),
            currentSid,
            isInherited: true,
            expectedInheritance);
    }

    private static void AssertExpectedRules(
        FileSystemAccessRule[] rules,
        string currentSid,
        bool isInherited,
        InheritanceFlags expectedInheritance)
    {
        Assert.AreEqual(3, rules.Length);
        string[] expectedSids =
        [
            currentSid,
            CreateWellKnownSid(WellKnownSidType.LocalSystemSid).Value,
            CreateWellKnownSid(WellKnownSidType.BuiltinAdministratorsSid).Value,
        ];
        CollectionAssert.AreEquivalent(
            expectedSids,
            rules.Select(rule => GetSid(rule).Value).ToArray());
        Assert.IsTrue(rules.All(rule =>
            rule.IsInherited == isInherited &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights == FileSystemRights.FullControl &&
            rule.InheritanceFlags == expectedInheritance &&
            rule.PropagationFlags == PropagationFlags.None));
    }

    private static void AssertVerificationFailure(WorkspaceFixture fixture)
    {
        WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(() =>
            WorkspaceAcl.VerifyExisting(fixture.WorkspacePath, fixture.CurrentSid));
        AssertSafeFailure(
            exception,
            WorkspaceAclFailureReason.VerificationFailed,
            reconciliationRequired: false,
            fixture.WorkspacePath,
            fixture.CurrentSid);
    }

    private static void AssertReparseFailure(
        Action action,
        string path,
        string sid)
    {
        WorkspaceAclException exception = Assert.ThrowsExactly<WorkspaceAclException>(action);
        AssertSafeFailure(
            exception,
            WorkspaceAclFailureReason.ReparsePoint,
            reconciliationRequired: false,
            path,
            sid);
    }

    private static void AssertSafeFailure(
        WorkspaceAclException exception,
        WorkspaceAclFailureReason reason,
        bool reconciliationRequired,
        params string[] forbiddenValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string forbiddenValue in forbiddenValues)
        {
            AssertDoesNotContain(exception.Message, forbiddenValue);
            AssertDoesNotContain(exception.ToString(), forbiddenValue);
        }
    }

    private static void AssertImmutableContract(Type type, params string[] propertyNames)
    {
        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            propertyNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static DirectorySecurity ReadDirectorySecurity(string path) =>
        FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(path),
            AccessControlSections.Access | AccessControlSections.Owner);

    private static byte[] ReadSecurityDescriptor(string path) =>
        ReadDirectorySecurity(path).GetSecurityDescriptorBinaryForm();

    private static FileSystemAccessRule CreateRule(
        SecurityIdentifier sid,
        FileSystemRights rights,
        AccessControlType type) =>
        new(
            sid,
            rights,
            ExpectedInheritance,
            PropagationFlags.None,
            type);

    private static SecurityIdentifier GetSid(FileSystemAccessRule rule) =>
        (SecurityIdentifier)rule.IdentityReference;

    private static FileSystemAccessRule[] GetRules(
        FileSystemSecurity security,
        bool includeExplicit,
        bool includeInherited)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit,
            includeInherited,
            typeof(SecurityIdentifier));
        var typed = new List<FileSystemAccessRule>(rules.Count);
        foreach (AuthorizationRule rule in (IEnumerable)rules)
        {
            typed.Add((FileSystemAccessRule)rule);
        }

        return typed.ToArray();
    }

    private static SecurityIdentifier CreateWellKnownSid(WellKnownSidType type) =>
        new(type, domainSid: null);

    private static string GetCurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new AssertInconclusiveException(
                "The current Windows identity has no user SID in this environment.");
    }

    private static void RequireWindowsFixedTempDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test requires Windows NTFS access-control semantics.");
            return;
        }

        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))
            ?? throw new AssertInconclusiveException(
                "The temporary directory has no drive root.");
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            Assert.Inconclusive("The test temporary directory is not on a fixed local drive.");
        }
    }

    private static bool TryCreateDirectorySymbolicLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (string.IsNullOrWhiteSpace(forbiddenValue))
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        internal WorkspaceFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.WorkspaceAclTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(RootPath);
            CurrentSid = GetCurrentSid();
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal string CurrentSid { get; }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}