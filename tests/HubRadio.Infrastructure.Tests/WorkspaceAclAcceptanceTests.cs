using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WorkspaceAclAcceptanceTests
{
    private const string InconclusiveReason =
        "Cross-account ACL acceptance requires a pre-created S-003 workspace plus HUBRADIO_ACL_UNAUTHORIZED_TEST_WORKSPACE, HUBRADIO_ACL_OWNER_SID, and HUBRADIO_ACL_UNAUTHORIZED_TEST_SID while this test process runs as that separate non-administrator account.";

    [TestMethod]
    public void UnauthorizedNonAdministratorAccountCannotEnumeratePreparedWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(InconclusiveReason);
            return;
        }

        string? workspacePath =
            Environment.GetEnvironmentVariable("HUBRADIO_ACL_UNAUTHORIZED_TEST_WORKSPACE");
        string? ownerSidText =
            Environment.GetEnvironmentVariable("HUBRADIO_ACL_OWNER_SID");
        string? unauthorizedSidText =
            Environment.GetEnvironmentVariable("HUBRADIO_ACL_UNAUTHORIZED_TEST_SID");
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            string.IsNullOrWhiteSpace(ownerSidText) ||
            string.IsNullOrWhiteSpace(unauthorizedSidText) ||
            !string.Equals(workspacePath, workspacePath.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(workspacePath))
        {
            Assert.Inconclusive(InconclusiveReason);
            return;
        }

        SecurityIdentifier ownerSid;
        SecurityIdentifier expectedUnauthorizedSid;
        try
        {
            ownerSid = new SecurityIdentifier(ownerSidText);
            expectedUnauthorizedSid = new SecurityIdentifier(unauthorizedSidText);
        }
        catch (ArgumentException)
        {
            Assert.Inconclusive(InconclusiveReason);
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? currentSid = identity.User;
        SecurityIdentifier administratorsSid = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        if (currentSid is null ||
            !currentSid.Equals(expectedUnauthorizedSid) ||
            currentSid.Equals(ownerSid) ||
            identity.Groups?.Contains(administratorsSid) == true)
        {
            Assert.Inconclusive(InconclusiveReason);
            return;
        }

        try
        {
            using IEnumerator<string> entries =
                Directory.EnumerateFileSystemEntries(workspacePath).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (SecurityException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            Assert.Inconclusive(InconclusiveReason);
            return;
        }

        Assert.Fail(
            "The separate non-administrator account could enumerate the prepared S-003 workspace.");
    }
}