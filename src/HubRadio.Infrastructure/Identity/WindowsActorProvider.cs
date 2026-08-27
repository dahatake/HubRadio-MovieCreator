using System.Security.Principal;

namespace HubRadio.Infrastructure.Identity;

public sealed class WindowsActorProvider
{
    public string GetCurrentSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "A Windows security identifier is required for the local actor.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? user = identity.User;
        if (user is null)
        {
            throw new InvalidOperationException(
                "The current Windows identity has no user security identifier.");
        }

        return user.Value;
    }
}