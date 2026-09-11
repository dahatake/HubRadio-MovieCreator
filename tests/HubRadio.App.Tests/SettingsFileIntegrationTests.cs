using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Backup;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Security;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SettingsFileIntegrationTests
{
    private const string LabelKey = "settings.label";
    private const string FlagKey = "settings.flag";
    private const string ExportMethod = "settings.file.export";
    private const string PreviewMethod = "settings.file.preview";
    private const string ApplyMethod = "settings.file.apply";
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void RequireWindowsFixedTempDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("UX-042 integration requires actual Windows file and ACL semantics.");
            return;
        }

        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())) ??
            throw new AssertInconclusiveException("The temporary directory has no drive root.");
        if (new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
        {
            Assert.Inconclusive("UX-042 integration requires a fixed local temporary drive.");
        }
    }

    [TestMethod]
    public void GlobalUpdateExportConfirmationPreviewApplyRoundTripSurvivesRestart()
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        SavedState initialState = fixture.CaptureState();
        JsonElement initial = session.Read();
        FileEvidence original = fixture.Export(session, initial);

        JsonElement customized = session.UpdateLabel(initial, "exported-global");
        Assert.AreEqual(Snapshot(fixture.Profile, "exported-global").CanonicalJson,
            session.Store.GetGlobalSettings()!.Snapshot.CanonicalJson);
        fixture.AssertFileUnchanged(original);
        SavedState beforeConfirmation = fixture.CaptureState();

        JsonElement confirmation = session.Send(ExportMethod, FileParameters(ExportMethod, customized));

        AssertFileFailure(confirmation, "overwriteRequired", original.Sha256);
        fixture.AssertStateUnchanged(beforeConfirmation);
        fixture.AssertFileUnchanged(original);
        FileEvidence exported = fixture.Export(session, customized,
            confirmation.GetProperty("fileSha256").GetString());
        Assert.AreNotEqual(original.Sha256, exported.Sha256);
        fixture.AssertStateUnchanged(beforeConfirmation);

        JsonElement later = session.UpdateLabel(customized, "later-global");
        SavedState beforePreview = fixture.CaptureState();
        fixture.AssertFileUnchanged(exported);
        JsonElement previewResult = session.Send(PreviewMethod, FileParameters(PreviewMethod, later));
        JsonElement preview = FileSnapshot(previewResult, exported.Sha256);

        AssertPreview(later, customized, preview);
        fixture.AssertStateUnchanged(beforePreview);
        fixture.AssertFileUnchanged(exported);

        // The saved target identity, not the candidate's digest, authorizes the apply.
        JsonElement applied = FileSnapshot(session.Send(ApplyMethod,
            FileParameters(ApplyMethod, later, previewResult.GetProperty("fileSha256").GetString())),
            exported.Sha256);

        AssertApplied(later, customized, applied);
        Assert.AreEqual(3, applied.GetProperty("revision").GetInt32());
        Assert.AreEqual(Snapshot(fixture.Profile, "exported-global").CanonicalJson,
            session.Store.GetGlobalSettings()!.Snapshot.CanonicalJson);
        fixture.AssertOtherScopesUnchanged(initialState, projectScope: false);
        fixture.AssertFileUnchanged(exported);

        NativeSettingsSession restarted = fixture.CreateSession();
        Assert.AreNotSame(session.Store, restarted.Store);
        AssertSnapshotEqual(applied, restarted.Read());
        JsonElement restartedPreview = FileSnapshot(restarted.Send(PreviewMethod,
            FileParameters(PreviewMethod, applied)), exported.Sha256);
        AssertSnapshotEqual(applied, restartedPreview);
        fixture.AssertOtherScopesUnchanged(initialState, projectScope: false);
        fixture.AssertFileUnchanged(exported);
    }

    [TestMethod]
    public void ProjectApplyPreservesInitialAndOtherScopesAcrossRestartAndReset()
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        StoredProjectSettings initialProject = fixture.CaptureState().Project;
        JsonElement global = session.UpdateLabel(session.Read(), "shared-file");
        FileEvidence exported = fixture.Export(session, global);
        JsonElement working = session.UpdateLabel(session.Read(fixture.ProjectId), "working-project");
        SavedState before = fixture.CaptureState();
        Assert.AreNotEqual(initialProject.InitialSnapshot.ContentDigest,
            before.Project.CurrentSnapshot.ContentDigest);
        Assert.AreNotEqual(before.Project.CurrentSnapshot.ContentDigest, before.Global.Snapshot.ContentDigest);

        JsonElement preview = FileSnapshot(session.Send(PreviewMethod,
            FileParameters(PreviewMethod, working)), exported.Sha256);

        AssertPreview(working, global, preview);
        fixture.AssertStateUnchanged(before);
        JsonElement applied = FileSnapshot(session.Send(ApplyMethod,
            FileParameters(ApplyMethod, working, exported.Sha256)), exported.Sha256);

        AssertApplied(working, global, applied);
        Assert.AreEqual(before.Global.Snapshot.CanonicalJson,
            session.Store.GetProjectSettings(fixture.ProjectId).Settings!.CurrentSnapshot.CanonicalJson);
        fixture.AssertOtherScopesUnchanged(before, projectScope: true);
        fixture.AssertFileUnchanged(exported);

        NativeSettingsSession restarted = fixture.CreateSession();
        Assert.AreNotSame(session.Store, restarted.Store);
        AssertSnapshotEqual(applied, restarted.Read(fixture.ProjectId));
        AssertSnapshotEqual(global, restarted.Read());
        Assert.AreEqual(initialProject.InitialSnapshot.CanonicalJson,
            restarted.Store.GetProjectSettings(fixture.ProjectId).Settings!.InitialSnapshot.CanonicalJson);

        Dictionary<string, object?> resetParameters = ExpectedIdentity(applied);
        resetParameters["projectId"] = fixture.ProjectId.ToString();
        JsonElement reset = AvailableSnapshot(restarted.Send("settings.resetAll", resetParameters));

        Assert.AreEqual(applied.GetProperty("revision").GetInt32() + 1, reset.GetProperty("revision").GetInt32());
        Assert.AreEqual(initialProject.InitialSnapshot.ContentDigest.Hex, reset.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(initialProject.InitialSnapshot.CanonicalJson,
            restarted.Store.GetProjectSettings(fixture.ProjectId).Settings!.CurrentSnapshot.CanonicalJson);
        AssertSnapshotEqual(reset, fixture.CreateSession().Read(fixture.ProjectId));
        fixture.AssertOtherScopesUnchanged(before, projectScope: true);
        fixture.AssertFileUnchanged(exported);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileApplyRequiresTheSelectedScopesIdentity(bool projectScope)
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        JsonElement candidate = session.Read(fixture.OtherProjectId);
        FileEvidence exported = fixture.Export(session, candidate);
        JsonElement saved = session.Read(projectScope ? fixture.ProjectId : null);
        JsonElement otherScope = session.Read(projectScope ? null : fixture.ProjectId);
        foreach (string property in new[] { "revision", "profileId", "profileVersion", "profileDigest" })
        {
            Assert.AreEqual(saved.GetProperty(property).GetRawText(), otherScope.GetProperty(property).GetRawText(), property);
        }

        Assert.AreNotEqual(saved.GetProperty("snapshotDigest").GetString(), otherScope.GetProperty("snapshotDigest").GetString());
        Dictionary<string, object?> wrongScope = FileParameters(ApplyMethod, otherScope, exported.Sha256);
        wrongScope["projectId"] = saved.GetProperty("projectId").GetString();
        SavedState before = fixture.CaptureState();

        AssertFileFailure(session.Send(ApplyMethod, wrongScope), "conflict");

        fixture.AssertStateUnchanged(before);
        fixture.AssertFileUnchanged(exported);
        JsonElement preview = FileSnapshot(session.Send(PreviewMethod,
            FileParameters(PreviewMethod, saved)), exported.Sha256);
        AssertPreview(saved, candidate, preview);
        fixture.AssertStateUnchanged(before);

        // Positive control: the same candidate and raw hash work with the actual target identity.
        JsonElement applied = FileSnapshot(session.Send(ApplyMethod,
            FileParameters(ApplyMethod, saved, exported.Sha256)), exported.Sha256);
        AssertApplied(saved, candidate, applied);
        AssertSnapshotEqual(applied, fixture.CreateSession().Read(projectScope ? fixture.ProjectId : null));
        fixture.AssertOtherScopesUnchanged(before, projectScope);
        fixture.AssertFileUnchanged(exported);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileApplyRejectsRevisionMadeStaleByNativeUpdatesEvenWhenValuesReturn(bool projectScope)
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        JsonElement saved = session.Read(projectScope ? fixture.ProjectId : null);
        JsonElement candidate = session.Read(fixture.OtherProjectId);
        FileEvidence exported = fixture.Export(session, candidate);
        AssertPreview(saved, candidate, FileSnapshot(session.Send(PreviewMethod,
            FileParameters(PreviewMethod, saved)), exported.Sha256));

        JsonElement intervening = session.UpdateLabel(saved, "intervening-native-update");
        JsonElement current = session.UpdateLabel(intervening, ReadLabel(saved));
        Assert.AreEqual(saved.GetProperty("snapshotDigest").GetString(), current.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 2, current.GetProperty("revision").GetInt32());
        SavedState before = fixture.CaptureState();
        NativeSettingsSession restarted = fixture.CreateSession();
        AssertSnapshotEqual(current, restarted.Read(projectScope ? fixture.ProjectId : null));

        AssertFileFailure(restarted.Send(ApplyMethod,
            FileParameters(ApplyMethod, saved, exported.Sha256)), "conflict");

        fixture.AssertStateUnchanged(before);
        fixture.AssertFileUnchanged(exported);
        AssertPreview(current, candidate, FileSnapshot(restarted.Send(PreviewMethod,
            FileParameters(PreviewMethod, current)), exported.Sha256));
        fixture.AssertStateUnchanged(before);
        JsonElement applied = FileSnapshot(restarted.Send(ApplyMethod,
            FileParameters(ApplyMethod, current, exported.Sha256)), exported.Sha256);
        AssertApplied(current, candidate, applied);
        AssertSnapshotEqual(applied, fixture.CreateSession().Read(projectScope ? fixture.ProjectId : null));
        fixture.AssertOtherScopesUnchanged(before, projectScope);
        fixture.AssertFileUnchanged(exported);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileApplyRequiresFreshRawHashAfterFormattingOnlyEdit(bool projectScope)
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        JsonElement saved = session.Read(projectScope ? fixture.ProjectId : null);
        JsonElement candidate = session.Read(fixture.OtherProjectId);
        FileEvidence exported = fixture.Export(session, candidate);
        JsonElement oldPreview = FileSnapshot(session.Send(PreviewMethod,
            FileParameters(PreviewMethod, saved)), exported.Sha256);
        AssertPreview(saved, candidate, oldPreview);

        // One representative edit suffices here; encoding/size/canonical boundaries live in SettingsTextFileTests.
        // Editing an already protected file preserves its production-created ACL.
        byte[] editedBytes = [.. exported.Bytes, (byte)' ', (byte)'\n'];
        File.WriteAllBytes(fixture.FilePath, editedBytes);
        var edited = new FileEvidence(ContentDigest.Compute(editedBytes).Hex, editedBytes);
        Assert.AreNotEqual(exported.Sha256, edited.Sha256);
        SavedState before = fixture.CaptureState();
        NativeSettingsSession restarted = fixture.CreateSession();
        AssertSnapshotEqual(saved, restarted.Read(projectScope ? fixture.ProjectId : null));

        AssertFileFailure(restarted.Send(ApplyMethod,
            FileParameters(ApplyMethod, saved, exported.Sha256)), "conflict");

        fixture.AssertStateUnchanged(before);
        fixture.AssertFileUnchanged(edited);
        JsonElement freshPreviewResult = restarted.Send(PreviewMethod, FileParameters(PreviewMethod, saved));
        JsonElement freshPreview = FileSnapshot(freshPreviewResult, edited.Sha256);
        AssertPreview(saved, candidate, freshPreview);
        AssertSnapshotEqual(oldPreview, freshPreview);
        fixture.AssertStateUnchanged(before);
        JsonElement applied = FileSnapshot(restarted.Send(ApplyMethod,
            FileParameters(ApplyMethod, saved, freshPreviewResult.GetProperty("fileSha256").GetString())),
            edited.Sha256);
        AssertApplied(saved, candidate, applied);
        AssertSnapshotEqual(applied, fixture.CreateSession().Read(projectScope ? fixture.ProjectId : null));
        fixture.AssertOtherScopesUnchanged(before, projectScope);
        fixture.AssertFileUnchanged(edited);
    }

    [TestMethod]
    public void NativeUserAppRootFileIsNotAutoImportedOrReplacedByWorkspaceDecoyOnRestart()
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        JsonElement saved = session.Read();
        JsonElement candidate = session.Read(fixture.OtherProjectId);
        FileEvidence exported = fixture.Export(session, candidate);
        SavedState before = fixture.CaptureState();
        Assert.AreNotEqual(saved.GetProperty("snapshotDigest").GetString(), candidate.GetProperty("snapshotDigest").GetString());

        // A valid but different Workspace decoy also uses the real protected-file writer, never a permissive ACL.
        string decoyPath = Path.Combine(fixture.WorkspacePath, "setting.txt");
        _ = new SettingsTextFile(fixture.WorkspacePath, fixture.ActorSid)
            .Export(before.Project.CurrentSnapshot, expectedExistingSha256: null);
        byte[] decoy = File.ReadAllBytes(decoyPath);
        Assert.AreNotEqual(exported.Sha256, ContentDigest.Compute(decoy).Hex);

        using (var locked = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            NativeSettingsSession restarted = fixture.CreateSession();
            Assert.AreNotSame(session.Store, restarted.Store);
            AssertSnapshotEqual(saved, restarted.Read());
            fixture.AssertStateUnchanged(before);
        }

        // Repeat with a readable, valid candidate: neither construction nor normal reads import it.
        NativeSettingsSession unlocked = fixture.CreateSession();
        AssertSnapshotEqual(saved, unlocked.Read());
        JsonElement preview = FileSnapshot(unlocked.Send(PreviewMethod,
            FileParameters(PreviewMethod, saved)), exported.Sha256);
        AssertPreview(saved, candidate, preview);
        fixture.AssertStateUnchanged(before);
        fixture.AssertFileUnchanged(exported);
        CollectionAssert.AreEqual(decoy, File.ReadAllBytes(decoyPath));

        string retainedPath = Path.Combine(fixture.AppRoot, "retained-setting.txt");
        File.Move(fixture.FilePath, retainedPath);
        AssertFileFailure(unlocked.Send(PreviewMethod, FileParameters(PreviewMethod, saved)), "fileNotFound");
        Assert.IsFalse(File.Exists(fixture.FilePath));
        CollectionAssert.AreEqual(exported.Bytes, File.ReadAllBytes(retainedPath));
        CollectionAssert.AreEqual(decoy, File.ReadAllBytes(decoyPath));
        AssertSnapshotEqual(saved, fixture.CreateSession().Read());
        fixture.AssertStateUnchanged(before);
    }

    [TestMethod]
    public async Task BackupRestoreKeepsUserFileAndReadsRestoredProfileUntilExplicitApply()
    {
        using Fixture fixture = new();
        NativeSettingsSession session = fixture.CreateSession();
        JsonElement backupGlobal = session.UpdateLabel(session.Read(), "backup-global");
        JsonElement backupProject = session.UpdateLabel(session.Read(fixture.ProjectId), "backup-project");
        FileEvidence backupFile = fixture.Export(session, backupGlobal);
        SavedState backedUp = fixture.CaptureState();

        Assert.AreEqual(fixture.AppRoot, Path.GetDirectoryName(fixture.WorkspacePath));
        Assert.AreEqual(fixture.AppRoot, Path.GetDirectoryName(fixture.BackupRootPath));
        Assert.AreNotEqual(fixture.WorkspacePath, fixture.BackupRootPath);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.WorkspacePath, "backups")));
        AssertProtectedRoot(fixture.BackupRootPath, fixture.ActorSid);
        // Only the public constructor: no move/time seams or private backup helpers.
        var service = new BackupService(fixture.DatabasePath, fixture.WorkspacePath,
            fixture.BackupRootPath, fixture.ActorSid);

        BackupResult backup = await service.CreateBackupAsync();

        Assert.AreEqual(1, backup.FileCount);
        Assert.AreEqual(backup.BackupDirectoryName, Path.GetFileName(backup.BackupDirectoryName));
        string backupDirectory = Path.Combine(fixture.BackupRootPath, backup.BackupDirectoryName);
        Assert.IsTrue(File.Exists(Path.Combine(backupDirectory, "payload", "hubradio.db")));
        Assert.IsEmpty(Directory.GetFiles(backupDirectory, "setting.txt", SearchOption.AllDirectories));
        fixture.AssertStateUnchanged(backedUp);
        fixture.AssertFileUnchanged(backupFile);

        JsonElement laterGlobal = session.UpdateLabel(backupGlobal, "file-after-backup");
        _ = session.UpdateLabel(backupProject, "project-after-backup");
        FileEvidence laterFile = fixture.Export(session, laterGlobal, backupFile.Sha256);
        Assert.AreNotEqual(backupFile.Sha256, laterFile.Sha256);

        // Model a later DB profile binding via the public store API (the UI cannot rebind profiles).
        // Keep setting.txt on v1 so it is a VALID, different import candidate after restoring v1.
        DefaultSettingsProfile newer = session.Store.RegisterProfile(Encoding.UTF8.GetBytes(
            DefaultSettingsProfile.Create(fixture.Profile.Id, "UX-042 newer profile",
                new SettingsProfileVersion(2, 0, 0), fixture.Profile.Definitions).CanonicalJson));
        _ = session.Store.UpdateGlobalSettings(Snapshot(newer, "newer-db-profile"),
            laterGlobal.GetProperty("revision").GetInt32(),
            laterGlobal.GetProperty("updatedAt").GetDateTimeOffset().AddTicks(1));
        SavedState beforeRestore = fixture.CaptureState();
        JsonElement newerGlobal = fixture.CreateSession().Read();
        Assert.AreEqual(newer.Version.ToString(), newerGlobal.GetProperty("profileVersion").GetString());
        Assert.AreEqual(newer.ContentDigest.Hex, newerGlobal.GetProperty("profileDigest").GetString());
        Assert.AreEqual(3, newerGlobal.GetProperty("revision").GetInt32());
        Assert.AreNotEqual(backedUp.Global.Snapshot.CanonicalJson, beforeRestore.Global.Snapshot.CanonicalJson);
        Assert.AreNotEqual(backedUp.Project.CurrentSnapshot.CanonicalJson, beforeRestore.Project.CurrentSnapshot.CanonicalJson);

        RestoreResult restored;
        using (var locked = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Workspace cutover and new handlers must not even need to open the sibling user file.
            restored = await service.RestoreBackupAsync(backup.BackupId);
            NativeSettingsSession restarted = fixture.CreateSession();
            Assert.AreNotSame(session.Store, restarted.Store);
            AssertSnapshotEqual(backupGlobal, restarted.Read());
            AssertSnapshotEqual(backupProject, restarted.Read(fixture.ProjectId));
            fixture.AssertStateUnchanged(backedUp);
        }

        Assert.AreEqual(backup.BackupId, restored.BackupId);
        Assert.AreEqual(1, restored.RestoredFileCount);
        Assert.AreEqual(restored.PreviousWorkspaceDirectoryName, Path.GetFileName(restored.PreviousWorkspaceDirectoryName));
        string previousWorkspace = Path.Combine(fixture.AppRoot, restored.PreviousWorkspaceDirectoryName);
        AssertProtectedRoot(previousWorkspace, fixture.ActorSid);
        AssertProtectedRoot(fixture.WorkspacePath, fixture.ActorSid);
        AssertProtectedRoot(fixture.BackupRootPath, fixture.ActorSid);
        var previousStore = new DefaultSettingsStore(Path.Combine(previousWorkspace, "hubradio.db"));
        AssertGlobalEqual(beforeRestore.Global, previousStore.GetGlobalSettings()!);
        AssertProjectEqual(beforeRestore.Project, previousStore.GetProjectSettings(fixture.ProjectId).Settings!);
        Assert.IsEmpty(Directory.GetFiles(fixture.WorkspacePath, "setting.txt", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetFiles(previousWorkspace, "setting.txt", SearchOption.AllDirectories));
        fixture.AssertFileUnchanged(laterFile);

        NativeSettingsSession unlocked = fixture.CreateSession();
        AssertSnapshotEqual(backupGlobal, unlocked.Read());
        AssertSnapshotEqual(backupProject, unlocked.Read(fixture.ProjectId));
        Assert.AreEqual(1, unlocked.Store.ListProfiles().Count);
        Assert.IsNull(unlocked.Store.GetProfile(newer.Id, newer.Version));
        fixture.AssertStateUnchanged(backedUp);
        JsonElement previewResult = unlocked.Send(PreviewMethod, FileParameters(PreviewMethod, backupGlobal));
        AssertPreview(backupGlobal, laterGlobal, FileSnapshot(previewResult, laterFile.Sha256));
        fixture.AssertStateUnchanged(backedUp);
        fixture.AssertFileUnchanged(laterFile);

        JsonElement applied = FileSnapshot(unlocked.Send(ApplyMethod,
            FileParameters(ApplyMethod, backupGlobal, previewResult.GetProperty("fileSha256").GetString())),
            laterFile.Sha256);
        AssertApplied(backupGlobal, laterGlobal, applied);
        Assert.AreEqual(2, applied.GetProperty("revision").GetInt32());
        Assert.AreEqual(fixture.Profile.Version.ToString(), applied.GetProperty("profileVersion").GetString());
        Assert.AreEqual(Snapshot(fixture.Profile, "file-after-backup").CanonicalJson,
            unlocked.Store.GetGlobalSettings()!.Snapshot.CanonicalJson);
        AssertSnapshotEqual(applied, fixture.CreateSession().Read());
        fixture.AssertOtherScopesUnchanged(backedUp, projectScope: false);
        fixture.AssertFileUnchanged(laterFile);
        Assert.IsTrue(Directory.Exists(backupDirectory));
    }

    private static Dictionary<string, object?> ExpectedIdentity(JsonElement saved) => new()
    {
        ["expectedRevision"] = saved.GetProperty("revision").GetInt32(),
        ["expectedProfileId"] = saved.GetProperty("profileId").GetString(),
        ["expectedProfileVersion"] = saved.GetProperty("profileVersion").GetString(),
        ["expectedProfileDigest"] = saved.GetProperty("profileDigest").GetString(),
        ["expectedSnapshotDigest"] = saved.GetProperty("snapshotDigest").GetString(),
    };

    private static Dictionary<string, object?> FileParameters(
        string method, JsonElement saved, string? fileSha256 = null)
    {
        Dictionary<string, object?> parameters = ExpectedIdentity(saved);
        parameters["projectId"] = saved.GetProperty("projectId").GetString();
        if (method is ExportMethod or ApplyMethod)
        {
            parameters["expectedFileSha256"] = fileSha256;
        }

        return parameters;
    }

    private static JsonElement AvailableSnapshot(JsonElement result)
    {
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Object, result.GetProperty("snapshot").ValueKind);
        return result.GetProperty("snapshot");
    }

    private static JsonElement FileSnapshot(JsonElement result, string expectedSha256)
    {
        JsonElement snapshot = AvailableSnapshot(result);
        Assert.AreEqual(expectedSha256, result.GetProperty("fileSha256").GetString());
        return snapshot;
    }

    private static void AssertFileFailure(JsonElement result, string status, string? expectedSha256 = null)
    {
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
        if (status == "overwriteRequired")
        {
            Assert.IsNotNull(expectedSha256);
            Assert.AreEqual(expectedSha256, result.GetProperty("fileSha256").GetString());
        }
        else
        {
            Assert.AreEqual(JsonValueKind.Null, result.GetProperty("fileSha256").ValueKind);
        }
    }

    private static void AssertSnapshotEqual(JsonElement expected, JsonElement actual) =>
        Assert.AreEqual(expected.GetRawText(), actual.GetRawText());

    private static void AssertPreview(JsonElement saved, JsonElement candidate, JsonElement preview)
    {
        foreach (JsonProperty property in saved.EnumerateObject())
        {
            if (property.Name is not ("snapshotDigest" or "values"))
            {
                Assert.AreEqual(property.Value.GetRawText(), preview.GetProperty(property.Name).GetRawText(), property.Name);
            }
        }

        AssertCandidateValues(candidate, preview);
    }

    private static void AssertApplied(JsonElement saved, JsonElement candidate, JsonElement applied)
    {
        Assert.AreNotEqual(saved.GetProperty("snapshotDigest").GetString(), candidate.GetProperty("snapshotDigest").GetString(),
            "The integration apply must change values rather than merely exercise a no-op.");
        foreach (JsonProperty property in saved.EnumerateObject())
        {
            if (property.Name is not ("snapshotDigest" or "values" or "revision" or "updatedAt"))
            {
                Assert.AreEqual(property.Value.GetRawText(), applied.GetProperty(property.Name).GetRawText(), property.Name);
            }
        }

        AssertCandidateValues(candidate, applied);
        Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 1, applied.GetProperty("revision").GetInt32());
        Assert.IsTrue(applied.GetProperty("updatedAt").GetDateTimeOffset() >= saved.GetProperty("updatedAt").GetDateTimeOffset());
    }

    private static void AssertCandidateValues(JsonElement expected, JsonElement actual)
    {
        foreach (string property in new[] { "profileId", "profileVersion", "profileDigest", "snapshotDigest", "values" })
        {
            Assert.AreEqual(expected.GetProperty(property).GetRawText(), actual.GetProperty(property).GetRawText(), property);
        }
    }

    private static string ReadLabel(JsonElement snapshot) => snapshot.GetProperty("values").EnumerateArray()
        .Single(selection => selection.GetProperty("key").GetString() == LabelKey)
        .GetProperty("value").GetProperty("textValue").GetString()!;

    private static void AssertGlobalEqual(StoredGlobalSettings expected, StoredGlobalSettings actual)
    {
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        Assert.AreEqual(expected.Snapshot.CanonicalJson, actual.Snapshot.CanonicalJson);
    }

    private static void AssertProjectEqual(StoredProjectSettings expected, StoredProjectSettings actual)
    {
        Assert.AreEqual(expected.ProjectId, actual.ProjectId);
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        Assert.AreEqual(expected.InitialSnapshot.CanonicalJson, actual.InitialSnapshot.CanonicalJson);
        Assert.AreEqual(expected.CurrentSnapshot.CanonicalJson, actual.CurrentSnapshot.CanonicalJson);
    }

    private static void AssertProtectedRoot(string path, string actorSid)
    {
        WorkspaceAclResult result = WorkspaceAcl.VerifyExisting(path, actorSid);
        Assert.IsTrue(result.OwnerMatchesCurrentActor);
        Assert.IsTrue(result.Protected);
        Assert.IsTrue(result.InheritanceConfigured);
        Assert.AreEqual(3, result.ExplicitRuleCount);
    }

    private static DefaultSettingsProfile CreateProfile() => DefaultSettingsProfile.Create(
        EntityId<DefaultSettingsProfileIdTag>.New(), "UX-042 integration profile", new SettingsProfileVersion(1, 0, 0),
        [Definition(LabelKey, DefaultSettingValue.FromText("profile-default")),
         Definition(FlagKey, DefaultSettingValue.FromBoolean(true))]);

    private static DefaultSettingDefinition Definition(string key, DefaultSettingValue value) =>
        new(key, DefaultSettingGroup.Editing, false, key, "Isolated UX-042 integration setting", DefaultSettingUnit.None,
            value, minimum: null, maximum: null, allowedTextValues: null,
            evidence: [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")]);

    private static DefaultSettingsSnapshot Snapshot(DefaultSettingsProfile profile, string label, bool flag = true) =>
        profile.CreateSnapshot(
        [
            new DefaultSettingSelection(LabelKey, DefaultSettingValue.FromText(label)),
            new DefaultSettingSelection(FlagKey, DefaultSettingValue.FromBoolean(flag)),
        ]);

    private sealed record FileEvidence(string Sha256, byte[] Bytes);

    private sealed record SavedState(StoredGlobalSettings Global, StoredProjectSettings Project, StoredProjectSettings Other);

    // This is a fresh native settings session, not a UI/window or a process restart.
    // It reuses the production factory, authorization and host routing with no test dispatch substitute.
    private sealed class NativeSettingsSession
    {
        private readonly SettingsBridgeRequestHandler settings;
        private readonly GlobalSettingsBridgeRequestHandler globalSettings;
        private readonly SettingsFileBridgeRequestHandler fileSettings;

        internal NativeSettingsSession(string databasePath, string appRoot, string actorSid)
        {
            Store = new DefaultSettingsStore(databasePath);
            var authorization = new SettingsBridgeAuthorization(
                new AuthorizationService(new RoleAssignmentStore(databasePath)), () => actorSid);
            var handlers = ProjectApplicationRuntime.CreateSettingsHandlers(Store, authorization, appRoot, actorSid);
            settings = handlers.Settings;
            globalSettings = handlers.GlobalSettings;
            fileSettings = handlers.FileSettings;
        }

        internal DefaultSettingsStore Store { get; }

        internal JsonElement Read(EntityId<ProjectIdTag>? projectId = null) => AvailableSnapshot(projectId is null
            ? Send("settings.global.read", new { })
            : Send("settings.read", new { projectId = projectId.ToString() }));

        internal JsonElement UpdateLabel(JsonElement saved, string label)
        {
            Assert.AreNotEqual(ReadLabel(saved), label);
            Dictionary<string, object?> parameters = ExpectedIdentity(saved);
            string? projectId = saved.GetProperty("projectId").GetString();
            if (projectId is not null)
            {
                parameters["projectId"] = projectId;
            }

            parameters["key"] = LabelKey;
            parameters["value"] = new { kind = "text", textValue = label };
            JsonElement updated = AvailableSnapshot(Send(
                projectId is null ? "settings.global.update" : "settings.update", parameters));
            Assert.AreEqual(label, ReadLabel(updated));
            Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 1, updated.GetProperty("revision").GetInt32());
            return updated;
        }

        internal JsonElement Send(string method, object parameters)
        {
            Guid requestId = Guid.NewGuid();
            string json = JsonSerializer.Serialize(new
            {
                version = 1,
                requestId = requestId.ToString("D"),
                method,
                @params = parameters,
            });
            Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json,
                out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
            Assert.IsNotNull(request);
            string response = WebViewHost.HandleSettingsRequest(request, settings, globalSettings, fileSettings, CancellationToken.None);
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" },
                root.EnumerateObject().Select(static property => property.Name).ToArray());
            Assert.AreEqual(1, root.GetProperty("version").GetInt32());
            Assert.AreEqual(requestId.ToString("D"), root.GetProperty("requestId").GetString());
            Assert.AreEqual(method, root.GetProperty("method").GetString());
            JsonElement result = root.GetProperty("result");
            CollectionAssert.AreEqual(method.StartsWith("settings.file.", StringComparison.Ordinal)
                    ? new[] { "status", "snapshot", "fileSha256" } : new[] { "status", "snapshot" },
                result.EnumerateObject().Select(static property => property.Name).ToArray());
            return result.Clone();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            ActorSid = identity.User?.Value ?? throw new AssertInconclusiveException("A Windows user SID is required.");
            RootPath = Path.Combine(Path.GetTempPath(), nameof(SettingsFileIntegrationTests), Guid.NewGuid().ToString("N"));
            // Never resolve, redirect, or inspect the real LOCALAPPDATA application directory.
            AppRoot = Path.Combine(RootPath, "user-app", "HubRadio");
            WorkspacePath = Path.Combine(AppRoot, "Workspace");
            BackupRootPath = Path.Combine(AppRoot, "BackupRoot");
            DatabasePath = Path.Combine(WorkspacePath, "hubradio.db");
            Directory.CreateDirectory(AppRoot);
            try
            {
                CreateProtectedRoot(WorkspacePath, ActorSid);
                CreateProtectedRoot(BackupRootPath, ActorSid);
                _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(WorkspacePath, "schema-backups"));
                var store = new DefaultSettingsStore(DatabasePath);
                Profile = store.RegisterProfile(Encoding.UTF8.GetBytes(CreateProfile().CanonicalJson));
                _ = store.InitializeGlobalSettings(Profile.CreateSnapshot(), InitialTime);
                ProjectId = CreateProject(store, Snapshot(Profile, "project-initial", flag: false));
                OtherProjectId = CreateProject(store, Snapshot(Profile, "other-project"));
                var authorization = new AuthorizationService(new RoleAssignmentStore(DatabasePath));
                Assert.IsTrue(authorization.BootstrapInitialRoles(ActorSid));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal string RootPath { get; }
        internal string AppRoot { get; }
        internal string WorkspacePath { get; }
        internal string BackupRootPath { get; }
        internal string DatabasePath { get; }
        internal string ActorSid { get; }
        internal string FilePath => Path.Combine(AppRoot, "setting.txt");
        internal DefaultSettingsProfile Profile { get; }
        internal EntityId<ProjectIdTag> ProjectId { get; }
        internal EntityId<ProjectIdTag> OtherProjectId { get; }

        internal NativeSettingsSession CreateSession() => new(DatabasePath, AppRoot, ActorSid);

        internal FileEvidence Export(NativeSettingsSession session, JsonElement saved, string? expectedSha256 = null)
        {
            JsonElement result = session.Send(ExportMethod, FileParameters(ExportMethod, saved, expectedSha256));
            JsonElement exported = AvailableSnapshot(result);
            AssertSnapshotEqual(saved, exported);
            Assert.IsTrue(File.Exists(FilePath));
            Assert.IsFalse(File.Exists(Path.Combine(WorkspacePath, "setting.txt")));
            byte[] bytes = File.ReadAllBytes(FilePath);
            string sha256 = ContentDigest.Compute(bytes).Hex;
            Assert.AreEqual(sha256, result.GetProperty("fileSha256").GetString());
            var evidence = new FileEvidence(sha256, bytes);
            AssertFileUnchanged(evidence);
            return evidence;
        }

        internal void AssertFileUnchanged(FileEvidence expected)
        {
            byte[] bytes = File.ReadAllBytes(FilePath);
            CollectionAssert.AreEqual(expected.Bytes, bytes);
            Assert.AreEqual(expected.Sha256, ContentDigest.Compute(bytes).Hex);
            // This public inspection also enforces the exact production owner/protected-file ACL.
            SettingsTextFileIdentity? identity = new SettingsTextFile(AppRoot, ActorSid).InspectIdentity();
            Assert.IsNotNull(identity);
            Assert.AreEqual(expected.Sha256, identity.FileSha256.Hex);
            Assert.AreEqual((long)expected.Bytes.Length, identity.ByteSize);
        }

        internal SavedState CaptureState()
        {
            var store = new DefaultSettingsStore(DatabasePath);
            return new(
                store.GetGlobalSettings() ?? throw new AssertFailedException("Global settings must remain initialized."),
                store.GetProjectSettings(ProjectId).Settings ?? throw new AssertFailedException("The project must remain bound."),
                store.GetProjectSettings(OtherProjectId).Settings ?? throw new AssertFailedException("The other project must remain bound."));
        }

        internal void AssertStateUnchanged(SavedState expected)
        {
            SavedState actual = CaptureState();
            AssertGlobalEqual(expected.Global, actual.Global);
            AssertProjectEqual(expected.Project, actual.Project);
            AssertProjectEqual(expected.Other, actual.Other);
        }

        internal void AssertOtherScopesUnchanged(SavedState expected, bool projectScope)
        {
            SavedState actual = CaptureState();
            if (projectScope)
            {
                AssertGlobalEqual(expected.Global, actual.Global);
                Assert.AreEqual(expected.Project.InitialSnapshot.CanonicalJson, actual.Project.InitialSnapshot.CanonicalJson);
            }
            else
            {
                AssertProjectEqual(expected.Project, actual.Project);
            }

            AssertProjectEqual(expected.Other, actual.Other);
        }

        private EntityId<ProjectIdTag> CreateProject(DefaultSettingsStore store, DefaultSettingsSnapshot initial)
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(projectId, "UX-042 isolated project", "Settings integration", "Internal",
                PublicationScope.InternalOnly, null, [], []);
            new ProjectStore(DatabasePath).CreateProject(project, "{\"integration\":true}", InitialTime);
            _ = store.BindProjectSettings(projectId, initial, InitialTime);
            return projectId;
        }

        private static void CreateProtectedRoot(string path, string actorSid)
        {
            try
            {
                _ = WorkspaceAcl.CreateNew(path, actorSid);
            }
            catch (WorkspaceAclException exception) when (exception.Reason == WorkspaceAclFailureReason.AccessDenied)
            {
                // Only an explicit permissions failure during setup is inconclusive; never swallow test assertions.
                Assert.Inconclusive("The Windows runner cannot create the required owner-protected temporary directory.");
                return;
            }

            AssertProtectedRoot(path, actorSid);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}