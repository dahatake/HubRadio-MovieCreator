using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ProjectFoldersTests
{
    private static string NewWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "HubRadio.ProjectFoldersTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        return root;
    }

    // FR-133 AC-143
    [TestMethod]
    public void ValidateNameRejectsWhatWindowsCannotUseAsAFolderName()
    {
        foreach (string bad in new[] { "", " ", "a\\b", "a/b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b", "a\tb", "end.", "end ", "CON", "con", "NUL.txt", "COM1", "LPT9", new string('x', 81) })
        {
            Assert.IsNotNull(ProjectFolders.ValidateName(bad), bad);
        }

        foreach (string good in new[] { "hubradio-sample", "社内向け 第1回", new string('x', 80), "COM10", "a.b" })
        {
            Assert.IsNull(ProjectFolders.ValidateName(good), good);
        }
    }

    // FR-133 AC-142 AC-144
    [TestMethod]
    public void BindCreatesTheNamedFolderAndRejectsDuplicatesIgnoringCase()
    {
        string ws = NewWorkspace();
        var first = EntityId<ProjectIdTag>.New();
        var second = EntityId<ProjectIdTag>.New();

        string path = ProjectFolders.Bind(ws, first, "Sample");

        Assert.AreEqual(Path.Combine(ws, "projects", "Sample"), path);
        Assert.IsTrue(Directory.Exists(path));
        Assert.AreEqual(path, ProjectFolders.GetPath(ws, first));
        ProjectFolderException error = Assert.ThrowsExactly<ProjectFolderException>(() => ProjectFolders.Bind(ws, second, "SAMPLE"));
        Assert.AreEqual(ProjectFolderFailureReason.DuplicateName, error.Reason);
        Assert.AreEqual(Path.Combine(ws, "projects", second.ToString()), ProjectFolders.GetPath(ws, second));
    }

    // FR-133 AC-145
    [TestMethod]
    public void RenameMovesTheFolderWithItsFilesAndKeepsTheLogicalReference()
    {
        string ws = NewWorkspace();
        var id = EntityId<ProjectIdTag>.New();
        _ = ProjectFolders.Bind(ws, id, "old");
        File.WriteAllText(Path.Combine(ProjectFolders.GetPath(ws, id), "a.txt"), "x");

        ProjectFolders.Rename(ws, id, "new");

        Assert.AreEqual("x", File.ReadAllText(Path.Combine(ws, "projects", "new", "a.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(ws, "projects", "old")));
        Assert.AreEqual($"projects/new/a.txt", ProjectFolders.ToPhysicalRelative(ws, $"projects/{id}/a.txt"));
        Assert.AreEqual($"projects/{id}/a.txt", ProjectFolders.ToLogicalRelative(ws, "projects/new/a.txt"));
        Assert.IsTrue(WorkspacePathSafety.TryResolvePortableRelativePath(ws, $"projects/{id}/a.txt", out string full));
        Assert.AreEqual(Path.Combine(ws, "projects", "new", "a.txt"), full);

        ProjectFolders.Rename(ws, id, "NEW");
        Assert.IsTrue(Directory.Exists(Path.Combine(ws, "projects", "NEW")));
        Assert.AreEqual("x", File.ReadAllText(Path.Combine(ws, "projects", "NEW", "a.txt")));
    }

    // FR-134 AC-146 AC-147 AC-148
    [TestMethod]
    public void MigrateRenamesGuidFoldersOnceAndLeavesUnusableOnesAlone()
    {
        string ws = NewWorkspace();
        var good = EntityId<ProjectIdTag>.New();
        var bad = EntityId<ProjectIdTag>.New();
        var dupA = EntityId<ProjectIdTag>.New();
        var dupB = EntityId<ProjectIdTag>.New();
        foreach (var id in new[] { good, bad, dupA, dupB })
        {
            Directory.CreateDirectory(Path.Combine(ws, "projects", id.ToString(), "deliveries"));
            File.WriteAllText(Path.Combine(ws, "projects", id.ToString(), "deliveries", "f.txt"), id.ToString());
        }

        var projects = new List<(string, string?)>
        {
            (good.ToString(), "good"), (bad.ToString(), "bad:name"), (dupA.ToString(), "Dup"), (dupB.ToString(), "dup"),
        };
        IReadOnlyList<ProjectFolderMigrationEntry> first = ProjectFolders.Migrate(ws, projects);
        IReadOnlyList<ProjectFolderMigrationEntry> second = ProjectFolders.Migrate(ws, projects);

        Assert.AreEqual(ProjectFolderMigrationStatus.Migrated, first.Single(e => e.ProjectId == good.ToString()).Status);
        Assert.AreEqual(ProjectFolderMigrationStatus.SkippedInvalidName, first.Single(e => e.ProjectId == bad.ToString()).Status);
        Assert.AreEqual(ProjectFolderMigrationStatus.SkippedDuplicateName, first.Single(e => e.ProjectId == dupA.ToString()).Status);
        Assert.AreEqual(good.ToString(), File.ReadAllText(Path.Combine(ws, "projects", "good", "deliveries", "f.txt")));
        foreach (var id in new[] { bad, dupA, dupB })
        {
            Assert.AreEqual(id.ToString(), File.ReadAllText(Path.Combine(ws, "projects", id.ToString(), "deliveries", "f.txt")));
        }

        Assert.AreEqual(ProjectFolderMigrationStatus.AlreadyNamed, second.Single(e => e.ProjectId == good.ToString()).Status);
        Assert.AreEqual(4, Directory.GetDirectories(Path.Combine(ws, "projects")).Length);
    }

    // FR-134 AC-147: a rename interrupted after the move is completed at the next start; before the move it is abandoned.
    [TestMethod]
    public void InterruptedMigrationIsCompletedOrAbandonedWithoutLosingData()
    {
        string ws = NewWorkspace();
        var moved = EntityId<ProjectIdTag>.New();
        var notMoved = EntityId<ProjectIdTag>.New();
        foreach (var id in new[] { moved, notMoved })
        {
            Directory.CreateDirectory(Path.Combine(ws, "projects", id.ToString()));
            File.WriteAllText(Path.Combine(ws, "projects", id.ToString(), "f.txt"), id.ToString());
        }

        // Simulate the interruption: registry announces both renames, only one directory was moved.
        File.WriteAllText(
            Path.Combine(ws, ProjectFolders.RegistryFileName),
            $"{{\"folders\":{{}},\"pending\":{{\"{moved}\":\"movedname\",\"{notMoved}\":\"notmovedname\"}}}}");
        Directory.Move(Path.Combine(ws, "projects", moved.ToString()), Path.Combine(ws, "projects", "movedname"));

        Assert.AreEqual(Path.Combine(ws, "projects", "movedname"), ProjectFolders.GetPath(ws, moved));
        ProjectFolders.Reconcile(ws);
        ProjectFolders.Reconcile(ws);

        Assert.AreEqual(Path.Combine(ws, "projects", "movedname"), ProjectFolders.GetPath(ws, moved));
        Assert.AreEqual(Path.Combine(ws, "projects", notMoved.ToString()), ProjectFolders.GetPath(ws, notMoved));
        Assert.AreEqual(moved.ToString(), File.ReadAllText(Path.Combine(ws, "projects", "movedname", "f.txt")));
        Assert.AreEqual(notMoved.ToString(), File.ReadAllText(Path.Combine(ws, "projects", notMoved.ToString(), "f.txt")));
    }
}
