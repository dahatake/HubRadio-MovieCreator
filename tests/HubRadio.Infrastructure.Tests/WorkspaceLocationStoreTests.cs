using System.Text;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.Infrastructure.Tests;

/// <summary>FR-062 / AC-037.1 and AC-037.2: Workspace folder location configuration.</summary>
[TestClass]
public sealed class WorkspaceLocationStoreTests
{
    private string root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "hr-wsloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // AC-037.1
    [TestMethod]
    public void FR062_AC037_1_DefaultIsWorkspaceUnderTheDriveRoot()
    {
        var store = new WorkspaceLocationStore(Path.Combine(root, "app"));
        WorkspaceLocation location = store.Resolve();

        string expected = Path.Combine(Path.GetPathRoot(root)!, "workspace");
        Assert.AreEqual(expected, store.DefaultPath);
        Assert.AreEqual(expected, location.CurrentPath);
        Assert.AreEqual(expected, location.DefaultPath);
        Assert.IsNull(location.SavedPath);
        Assert.IsFalse(File.Exists(store.ConfigPath));
    }

    // AC-037.1
    [TestMethod]
    public void FR062_AC037_1_LegacyWorkspaceWithDatabaseIsRetainedWithoutMoving()
    {
        string app = Path.Combine(root, "app");
        string legacy = Path.Combine(app, "Workspace");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "hubradio.db"), "x");

        WorkspaceLocation location = new WorkspaceLocationStore(app).Resolve();

        Assert.AreEqual(legacy, location.CurrentPath);
        Assert.IsNull(location.SavedPath);
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "hubradio.db")));
    }

    // AC-037.1 Q-013
    [TestMethod]
    public void FR062_AC037_1_LegacyWorkspaceWithDatabaseIsMovedToTheDefault()
    {
        string app = Path.Combine(root, "app");
        string legacy = Path.Combine(app, "Workspace");
        Directory.CreateDirectory(Path.Combine(legacy, "projects"));
        File.WriteAllText(Path.Combine(legacy, "hubradio.db"), "x");
        File.WriteAllText(Path.Combine(legacy, "projects", "a.txt"), "y");
        string target = Path.Combine(root, "default-ws");
        var store = new WorkspaceLocationStore(app, target);

        Assert.IsTrue(store.TryMigrateLegacyWorkspace());

        Assert.IsFalse(Directory.Exists(legacy));
        Assert.AreEqual("x", File.ReadAllText(Path.Combine(target, "hubradio.db")));
        Assert.AreEqual("y", File.ReadAllText(Path.Combine(target, "projects", "a.txt")));
        Assert.AreEqual(target, store.Resolve().CurrentPath);
        Assert.IsFalse(store.TryMigrateLegacyWorkspace());
    }

    // AC-037.1 Q-013
    [TestMethod]
    public void FR062_AC037_1_MigrationNeverOverwritesOrMovesWhenNotSafe()
    {
        string app = Path.Combine(root, "app");
        string legacy = Path.Combine(app, "Workspace");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "hubradio.db"), "legacy");
        string target = Path.Combine(root, "default-ws");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "other.txt"), "foreign");
        var store = new WorkspaceLocationStore(app, target);

        Assert.IsFalse(store.TryMigrateLegacyWorkspace());
        Assert.AreEqual("legacy", File.ReadAllText(Path.Combine(legacy, "hubradio.db")));
        Assert.AreEqual(legacy, store.Resolve().CurrentPath);

        File.Delete(Path.Combine(target, "other.txt"));
        File.WriteAllText(Path.Combine(target, "hubradio.db"), "existing");
        Assert.IsFalse(store.TryMigrateLegacyWorkspace());
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(target, "hubradio.db")));
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "hubradio.db")));
    }

    // AC-037.1 Q-013
    [TestMethod]
    public void FR062_AC037_1_MigrationIsSkippedWhenALocationIsSaved()
    {
        string app = Path.Combine(root, "app");
        string legacy = Path.Combine(app, "Workspace");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "hubradio.db"), "x");
        string saved = Path.Combine(root, "saved-ws");
        var store = new WorkspaceLocationStore(app, Path.Combine(root, "default-ws"));
        store.Save(saved);

        Assert.IsFalse(store.TryMigrateLegacyWorkspace());
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "hubradio.db")));
    }

    // AC-037.1
    [TestMethod]
    public void FR062_AC037_1_EmptyLegacyFolderWithoutDatabaseUsesTheDefault()
    {
        string app = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(app, "Workspace"));

        var store = new WorkspaceLocationStore(app);

        Assert.AreEqual(store.DefaultPath, store.Resolve().CurrentPath);
    }

    // AC-037.1
    [TestMethod]
    public void FR062_AC037_1_SaveRoundTripsAndResetDeletesTheConfig()
    {
        string app = Path.Combine(root, "app");
        var store = new WorkspaceLocationStore(app);
        string target = Path.Combine(root, "new-workspace");

        store.Save(target);

        Assert.IsTrue(File.Exists(store.ConfigPath));
        byte[] bytes = File.ReadAllBytes(store.ConfigPath);
        Assert.IsFalse(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "UTF-8 must have no BOM.");
        string json = Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(json, "\"version\":1");
        Assert.AreEqual(0, Directory.GetFiles(app, "*.tmp").Length);
        Assert.IsFalse(Directory.Exists(target), "Save must not create the workspace folder.");

        WorkspaceLocation location = new WorkspaceLocationStore(app).Resolve();
        Assert.AreEqual(target, location.CurrentPath);
        Assert.AreEqual(target, location.SavedPath);

        store.Save(Path.Combine(root, "other") + Path.DirectorySeparatorChar);
        Assert.AreEqual(Path.Combine(root, "other"), store.Resolve().SavedPath);

        store.Save(null);
        Assert.IsFalse(File.Exists(store.ConfigPath));
        Assert.IsNull(store.Resolve().SavedPath);
        Assert.AreEqual(store.DefaultPath, store.Resolve().CurrentPath);
    }

    // AC-037.1
    [TestMethod]
    public void FR062_AC037_1_SavedConfigWinsOverLegacyWorkspace()
    {
        string app = Path.Combine(root, "app");
        string legacy = Path.Combine(app, "Workspace");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "hubradio.db"), "x");
        var store = new WorkspaceLocationStore(app);
        string target = Path.Combine(root, "chosen");

        store.Save(target);

        Assert.AreEqual(target, store.Resolve().CurrentPath);
    }

    // AC-037.1
    [TestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{\"version\":2,\"path\":\"C:\\\\x\"}")]
    [DataRow("{\"version\":1}")]
    [DataRow("{\"version\":1,\"path\":5}")]
    [DataRow("{\"version\":1,\"path\":\"relative\\\\dir\"}")]
    [DataRow("{\"version\":1,\"path\":\"\"}")]
    public void FR062_AC037_1_MalformedConfigThrowsInsteadOfFallingBack(string content)
    {
        string app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        var store = new WorkspaceLocationStore(app);
        File.WriteAllText(store.ConfigPath, content);

        _ = Assert.ThrowsExactly<WorkspaceLocationException>(() => store.Resolve());
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_AcceptsNewFolderUnderExistingParent()
    {
        string path = Path.Combine(root, "brand-new");

        Assert.AreEqual(path, WorkspaceLocationStore.Validate(path));
        Assert.AreEqual(path, WorkspaceLocationStore.Validate(path + "\\"));
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_AcceptsExistingEmptyFolderAndFolderWithDatabase()
    {
        string empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        string withDb = Path.Combine(root, "with-db");
        Directory.CreateDirectory(withDb);
        File.WriteAllText(Path.Combine(withDb, "hubradio.db"), "x");
        File.WriteAllText(Path.Combine(withDb, "other.txt"), "x");

        Assert.AreEqual(empty, WorkspaceLocationStore.Validate(empty));
        Assert.AreEqual(withDb, WorkspaceLocationStore.Validate(withDb));
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsNonEmptyForeignFolder()
    {
        string foreign = Path.Combine(root, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "document.txt"), "x");

        AssertRejected(foreign);
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsExistingFile()
    {
        string file = Path.Combine(root, "afile");
        File.WriteAllText(file, "x");

        AssertRejected(file);
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsInstallDirectoryAndItsDescendants()
    {
        string install = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        AssertRejected(install);
        AssertRejected(Path.Combine(install, "Workspace"));
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsDriveRoot()
    {
        AssertRejected(Path.GetPathRoot(root)!);
    }

    // AC-037.2
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\\\\server\\share\\workspace")]
    [DataRow("\\\\?\\C:\\workspace")]
    [DataRow("\\\\.\\C:\\workspace")]
    [DataRow("workspace")]
    [DataRow("..\\workspace")]
    [DataRow("\\workspace")]
    [DataRow("C:workspace")]
    [DataRow("C:\\a\\..\\workspace")]
    [DataRow("C:\\a\\.\\workspace")]
    [DataRow("C:/workspace")]
    [DataRow("C:\\\\workspace")]
    [DataRow("C:\\work:space")]
    [DataRow("C:\\work*space")]
    [DataRow("C:\\CON")]
    [DataRow("C:\\aux.txt\\ws")]
    [DataRow("C:\\LPT1")]
    [DataRow("C:\\ws ")]
    [DataRow("C:\\ws.")]
    public void FR062_AC037_2_RejectsNonCanonicalOrUnsafeShapes(string? path)
    {
        AssertRejected(path);
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsPathsLongerThan200Characters()
    {
        string longPath = Path.Combine(root, new string('a', 200));

        AssertRejected(longPath);
        string atLimit = "C:\\" + new string('a', 197);
        Assert.AreEqual(200, atLimit.Length);
        Assert.AreEqual(atLimit, WorkspaceLocationStore.Validate(atLimit));
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsMissingParentFolder()
    {
        AssertRejected(Path.Combine(root, "missing-parent", "workspace"));
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsReparsePointTargetAndAncestor()
    {
        string real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        string link = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Junctions need no privilege.
            using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            junction!.WaitForExit();
            if (!Directory.Exists(link) || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            {
                Assert.Inconclusive("Reparse points cannot be created in this environment.");
                return;
            }
        }

        try
        {
            AssertRejected(link);
            AssertRejected(Path.Combine(link, "child"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_RejectsNonFixedDrives()
    {
        DriveInfo? drive = DriveInfo.GetDrives().FirstOrDefault(
            static d => d.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Removable);
        if (drive is null)
        {
            Assert.Inconclusive("No network, optical or removable drive is present.");
            return;
        }

        AssertRejected(Path.Combine(drive.RootDirectory.FullName, "workspace"));
    }

    // AC-037.2
    [TestMethod]
    public void FR062_AC037_2_SaveRejectsInvalidPathWithoutChangingTheConfig()
    {
        string app = Path.Combine(root, "app");
        var store = new WorkspaceLocationStore(app);
        string good = Path.Combine(root, "good");
        store.Save(good);

        _ = Assert.ThrowsExactly<ArgumentException>(() => store.Save("C:\\a\\..\\b"));

        Assert.AreEqual(good, store.Resolve().SavedPath);
    }

    private static void AssertRejected(string? path) =>
        _ = Assert.ThrowsExactly<ArgumentException>(() => WorkspaceLocationStore.Validate(path));
}
