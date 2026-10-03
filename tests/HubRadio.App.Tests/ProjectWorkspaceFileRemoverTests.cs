using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ProjectWorkspaceFileRemoverTests
{
    [TestMethod]
    public void RemovesOnlyTheTargetProjectDirectoryTree()
    {
        using TempWorkspace workspace = new();
        EntityId<ProjectIdTag> target = EntityId<ProjectIdTag>.New();
        EntityId<ProjectIdTag> other = EntityId<ProjectIdTag>.New();
        string targetFile = workspace.WriteFile("projects", target.ToString(), "deliveries", "a", "video.mp4");
        workspace.WriteFile("projects", target.ToString(), "staging", "x.bin");
        string otherFile = workspace.WriteFile("projects", other.ToString(), "deliveries", "keep.mp4");
        string database = workspace.WriteFile("hubradio.db");

        Assert.IsTrue(ProjectWorkspaceFileRemover.Remove(workspace.Root, target));

        Assert.IsFalse(File.Exists(targetFile));
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "projects", target.ToString())));
        Assert.IsTrue(File.Exists(otherFile));
        Assert.IsTrue(File.Exists(database));
    }

    [TestMethod]
    public void MissingProjectDirectoryIsAlreadyRemoved()
    {
        using TempWorkspace workspace = new();

        Assert.IsTrue(ProjectWorkspaceFileRemover.Remove(workspace.Root, EntityId<ProjectIdTag>.New()));
        Assert.IsTrue(ProjectWorkspaceFileRemover.Remove(workspace.Root, EntityId<ProjectIdTag>.New()));
    }

    [TestMethod]
    public void ReportsFailureWhenAFileIsInUseAndRemovesItOnRetry()
    {
        using TempWorkspace workspace = new();
        EntityId<ProjectIdTag> target = EntityId<ProjectIdTag>.New();
        string locked = workspace.WriteFile("projects", target.ToString(), "deliveries", "locked.mp4");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsFalse(ProjectWorkspaceFileRemover.Remove(workspace.Root, target));
        }

        Assert.IsTrue(ProjectWorkspaceFileRemover.Remove(workspace.Root, target));
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "projects", target.ToString())));
    }

    [TestMethod]
    public void DoesNotFollowADirectoryLinkAtTheProjectRoot()
    {
        using TempWorkspace workspace = new();
        EntityId<ProjectIdTag> target = EntityId<ProjectIdTag>.New();
        string outside = workspace.WriteFile("outside", "precious.txt");
        string link = Path.Combine(workspace.Root, "projects", target.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(outside)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this environment.");
        }

        Assert.IsTrue(ProjectWorkspaceFileRemover.Remove(workspace.Root, target));

        Assert.IsFalse(Directory.Exists(link));
        Assert.IsTrue(File.Exists(outside));
    }

    private sealed class TempWorkspace : IDisposable
    {
        internal TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "HubRadio.FileRemoverTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string WriteFile(params string[] relative)
        {
            string path = Path.Combine([Root, .. relative]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
