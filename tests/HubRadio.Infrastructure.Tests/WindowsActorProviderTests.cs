using System.Security.Principal;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class WindowsActorProviderTests
{
    [TestMethod]
    public void GetCurrentSid_ReturnsCurrentWindowsUserSidRatherThanDisplayName()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test requires Windows.");
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string expected = identity.User?.Value
            ?? throw new AssertInconclusiveException(
                "The current Windows identity has no user SID in this environment.");
        WindowsActorProvider provider = new();

        string actual = provider.GetCurrentSid();
        string canonical = new SecurityIdentifier(actual).Value;

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(canonical, actual);
        Assert.IsFalse(string.Equals(actual, identity.Name, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetCurrentSid_OnUnsupportedPlatformFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The unsupported-platform path requires a non-Windows runner.");
            return;
        }

        WindowsActorProvider provider = new();

        Assert.ThrowsExactly<PlatformNotSupportedException>(() => provider.GetCurrentSid());
    }
}