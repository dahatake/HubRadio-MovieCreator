using System.Runtime.Versioning;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Infrastructure.Security;

namespace HubRadio.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class PlayerApplicationRuntimeTests
{
    [TestMethod]
    public async Task Create_ComposesProtectedAuthorizedRuntimeAndReopens()
    {
        RequireWindowsFixedTempDrive();
        string applicationRoot = Path.Combine(
            Path.GetTempPath(),
            "HubRadio.PlayerApplicationRuntimeTests",
            Guid.NewGuid().ToString("N"));

        try
        {
            PlayerApplicationRuntime first =
                PlayerApplicationRuntime.Create(applicationRoot);

            first.Authorization.DemandPreview();
            PlayerBridgeReadResult missing = await first.BridgeReader.ReadAsync(
                EntityId<JobIdTag>.New(),
                CancellationToken.None);

            Assert.AreEqual(PlayerBridgeStatus.Unavailable, missing.Status);
            Assert.IsNull(missing.Snapshot);
            Assert.IsTrue(File.Exists(Path.Combine(
                applicationRoot,
                "Workspace",
                "hubradio.db")));

            PlayerApplicationRuntime reopened =
                PlayerApplicationRuntime.Create(applicationRoot);

            reopened.Authorization.DemandPreview();
            WorkspaceAclResult workspace = WorkspaceAcl.VerifyExisting(
                Path.Combine(applicationRoot, "Workspace"),
                new Infrastructure.Identity.WindowsActorProvider().GetCurrentSid());
            Assert.IsTrue(workspace.OwnerMatchesCurrentActor);
            Assert.IsTrue(workspace.Protected);
            Assert.AreEqual(3, workspace.ExplicitRuleCount);
        }
        finally
        {
            if (Directory.Exists(applicationRoot))
            {
                Directory.Delete(applicationRoot, recursive: true);
            }
        }
    }

    private static void RequireWindowsFixedTempDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Player runtime composition requires Windows ACL semantics.");
            return;
        }

        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))
            ?? throw new AssertInconclusiveException(
                "The temporary directory has no drive root.");
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            Assert.Inconclusive(
                "The temporary directory is not on a fixed local drive.");
        }
    }
}