using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SettingsFileBridgeTests
{
    private const string ExportMethod = "settings.file.export";
    private const string PreviewMethod = "settings.file.preview";
    private const string ApplyMethod = "settings.file.apply";
    private const string PrivateDetail = "ux010-private-detail";
    private const string CandidateText = "日本語\r\n  \"引用\"\tΩ🙂 終端";
    private static readonly string[] FileMethods = [ExportMethod, PreviewMethod, ApplyMethod];
    private static readonly string[] ReadMethods = [PreviewMethod, ApplyMethod];
    private static readonly DateTimeOffset InitialTime = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly ContentDigest WrongHash = ContentDigest.Compute("not the settings file"u8);

    [TestInitialize]
    public void RequireWindowsFileSemantics()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("These tests require actual Windows file and ACL semantics.");
            return;
        }

        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())) ??
            throw new AssertInconclusiveException("The temporary directory has no drive root.");
        if (new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
        {
            Assert.Inconclusive("These tests require a fixed local temporary drive.");
        }
    }

    [TestMethod]
    public void PublicConstructorHasTheRequiredConcreteDependenciesAndRejectsNulls()
    {
        using Fixture fixture = new();
        ConstructorInfo constructor = typeof(SettingsFileBridgeRequestHandler).GetConstructors().Single();
        ParameterInfo[] parameters = constructor.GetParameters();
        CollectionAssert.AreEqual(
            new[] { typeof(DefaultSettingsStore), typeof(SettingsTextFile), typeof(ISettingsBridgeAuthorization), typeof(Func<DateTimeOffset>) },
            parameters.Select(static parameter => parameter.ParameterType).ToArray());
        Assert.IsTrue(parameters[3].HasDefaultValue);
        Assert.IsNull(parameters[3].DefaultValue);
        Assert.AreEqual("clock", parameters[3].Name);
        Assert.AreEqual("store", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new SettingsFileBridgeRequestHandler(null!, fixture.File, fixture.Authorization)).ParamName);
        Assert.AreEqual("file", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new SettingsFileBridgeRequestHandler(fixture.Store, null!, fixture.Authorization)).ParamName);
        Assert.AreEqual("authorization", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, null!)).ParamName);
        Func<BridgeRequest, string> handle = fixture.Handler.Handle;
        Assert.AreEqual("request", Assert.ThrowsExactly<ArgumentNullException>(() =>
            handle(null!)).ParamName);
    }

    [TestMethod]
    public void ConstructorDoesNotReadFileOrStoreOrInitializeSettings()
    {
        using Fixture fixture = new(initializeGlobal: false);
        File.Move(fixture.DatabasePath, fixture.DatabasePath + ".offline");
        Directory.Move(fixture.AppRoot, Path.Combine(fixture.RootPath, "offline-application"));

        _ = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization,
            () => throw new AssertFailedException("Construction must not request a timestamp."));

        Assert.IsFalse(File.Exists(fixture.DatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.AppRoot));
        Assert.AreEqual(0, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(0, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExportUsesSavedSnapshotAndReadPermissionWithoutChangingAnyScope(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SavedState before = fixture.CaptureState();
        byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
        fixture.Authorization.DenyEdit = true;

        string response = fixture.Handle(ExportMethod, ParametersFor(ExportMethod, saved));

        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        ContentDigest hash = ContentDigest.Compute(bytes);
        AssertSnapshotEqual(saved, ReadAvailable(response, hash));
        SettingsTextFileReadResult read = fixture.File.Read(fixture.Profile);
        Assert.AreEqual(saved.GetProperty("snapshotDigest").GetString(), read.Snapshot.ContentDigest.Hex);
        Assert.AreEqual(hash, read.Identity.FileSha256);
        Assert.AreNotEqual(read.Snapshot.ContentDigest, hash);
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
        fixture.AssertStateUnchanged(before);
        fixture.AssertOnlySettingsFile();
        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(0, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreviewUsesBoundProfileAndCandidateValuesButKeepsTargetMetadataWithoutWriting(bool projectScope)
    {
        using Fixture fixture = new();
        fixture.UpdateSaved(projectScope, ReplaceValue(fixture.Profile, fixture.CurrentSnapshot(projectScope),
            "settings.integer", DefaultSettingValue.FromInteger(19)));
        JsonElement saved = fixture.ReadSaved(projectScope);
        Assert.AreEqual(1, saved.GetProperty("revision").GetInt32());
        DefaultSettingsProfile newer = DefaultSettingsProfile.Create(
            fixture.Profile.Id, "Newer unbound profile", new SettingsProfileVersion(2, 0, 0), fixture.Profile.Definitions);
        _ = fixture.Store.RegisterProfile(Encoding.UTF8.GetBytes(newer.CanonicalJson));
        DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile);
        SettingsTextFileIdentity identity = fixture.File.Export(candidate, null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        fixture.Authorization.DenyEdit = true;
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization,
            () => throw new AssertFailedException("Preview must not request a write timestamp."));

        JsonElement preview = ReadAvailable(fixture.Handle(handler, PreviewMethod,
            ParametersFor(PreviewMethod, saved)), identity.FileSha256);

        AssertCandidateSnapshot(saved, preview, candidate);
        Assert.AreNotEqual(saved.GetProperty("snapshotDigest").GetString(), preview.GetProperty("snapshotDigest").GetString());
        AssertSnapshotEqual(saved, fixture.ReadSaved(projectScope, restart: true));
        AssertSelections(projectScope ? before.Project.InitialSnapshot : fixture.Profile.CreateSnapshot(),
            preview.GetProperty("initialValues"));
        fixture.AssertStateUnchanged(before);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        fixture.AssertOnlySettingsFile();
        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(0, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow(false, long.MinValue)]
    [DataRow(false, long.MaxValue)]
    [DataRow(true, long.MinValue)]
    [DataRow(true, long.MaxValue)]
    public void ApplySavesAllFiveKindsOnceAndPreservesInitialAndOtherScopes(bool projectScope, long integer)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile, integer);
        SettingsTextFileIdentity identity = fixture.File.Export(candidate, null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        JsonElement preview = ReadAvailable(fixture.Handle(PreviewMethod,
            ParametersFor(PreviewMethod, saved)), identity.FileSha256);
        AssertCandidateSnapshot(saved, preview, candidate);
        fixture.AssertStateUnchanged(before);

        JsonElement applied = ReadAvailable(fixture.Handle(ApplyMethod,
            ParametersFor(ApplyMethod, saved, identity.FileSha256)), identity.FileSha256);

        Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 1, applied.GetProperty("revision").GetInt32());
        Assert.AreEqual(fixture.UtcNow, applied.GetProperty("updatedAt").GetDateTimeOffset());
        foreach (string property in new[] { "projectId", "profileId", "profileName", "profileVersion", "profileDigest", "initialSnapshotDigest", "initialValues", "definitionCount", "definitions" })
        {
            Assert.AreEqual(saved.GetProperty(property).GetRawText(), applied.GetProperty(property).GetRawText(), property);
        }

        Assert.AreEqual(candidate.ContentDigest.Hex, applied.GetProperty("snapshotDigest").GetString());
        AssertSelections(candidate, applied.GetProperty("values"));
        Assert.AreEqual(candidate.CanonicalJson, fixture.CurrentSnapshot(projectScope).CanonicalJson);
        AssertSnapshotEqual(applied, fixture.ReadSaved(projectScope, restart: true));
        Assert.AreEqual(before.Writes + 1, fixture.ReadWriteCount());
        fixture.AssertOtherScopesUnchanged(before, projectScope);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        fixture.AssertOnlySettingsFile();
        fixture.AssertNoWorkStarted();
        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(1, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyNoOpStillDemandsEditButPreservesRevisionTimestampAndDoesNotCallClock(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(fixture.CurrentSnapshot(projectScope), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        fixture.Authorization.DenyRead = true;
        int calls = 0;
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization, () =>
        {
            calls++;
            throw new AssertFailedException("A no-op must not request a timestamp.");
        });

        AssertSnapshotEqual(saved, ReadAvailable(fixture.Handle(handler, ApplyMethod,
            ParametersFor(ApplyMethod, saved, identity.FileSha256)), identity.FileSha256));

        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(1, fixture.Authorization.EditDemandCount);
        fixture.AssertStateUnchanged(before);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ExportRequiresConfirmationEvenForIdenticalContentAndThenExportsSavedState(bool projectScope, bool identical)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity original = fixture.File.Export(
            identical ? fixture.CurrentSnapshot(projectScope) : CreateCandidate(fixture.Profile), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ExportMethod, ParametersFor(ExportMethod, saved)),
            "overwriteRequired", original.FileSha256);

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertStateUnchanged(before);
        string response = fixture.Handle(ExportMethod, ParametersFor(ExportMethod, saved, original.FileSha256));
        ContentDigest committedHash = ContentDigest.Compute(File.ReadAllBytes(fixture.FilePath));
        AssertSnapshotEqual(saved, ReadAvailable(response, committedHash));
        Assert.AreEqual(identical, committedHash == original.FileSha256);
        Assert.AreEqual(fixture.CurrentSnapshot(projectScope).CanonicalJson, fixture.File.Read(fixture.Profile).Snapshot.CanonicalJson);
        fixture.AssertStateUnchanged(before);
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
        fixture.AssertOnlySettingsFile();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DiscardedExportAndApplyResponsesDoNotCauseDuplicateMutationsOnRetry(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SavedState before = fixture.CaptureState();
        BridgeRequest export = ParseRequest(ExportMethod, ParametersFor(ExportMethod, saved));

        // Deliberately discard the response; reconcile from the actual file, not the reply.
        _ = fixture.Handler.Handle(export);
        byte[] exportedBytes = File.ReadAllBytes(fixture.FilePath);
        ContentDigest exportedHash = ContentDigest.Compute(exportedBytes);
        AssertFailure(fixture.Handler.Handle(export), "overwriteRequired", exportedHash);
        CollectionAssert.AreEqual(exportedBytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertStateUnchanged(before);

        DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile);
        SettingsTextFileIdentity identity = fixture.File.Export(candidate, exportedHash);
        BridgeRequest apply = ParseRequest(ApplyMethod, ParametersFor(ApplyMethod, saved, identity.FileSha256));
        _ = fixture.Handler.Handle(apply);
        JsonElement persisted = fixture.ReadSaved(projectScope, restart: true);
        Assert.AreEqual(candidate.ContentDigest.Hex, persisted.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 1, persisted.GetProperty("revision").GetInt32());

        AssertFailure(fixture.Handler.Handle(apply), "conflict");

        AssertSnapshotEqual(persisted, fixture.ReadSaved(projectScope, restart: true));
        Assert.AreEqual(before.Writes + 1, fixture.ReadWriteCount());
        fixture.AssertOtherScopesUnchanged(before, projectScope);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
        fixture.AssertOnlySettingsFile();
        fixture.AssertNoWorkStarted();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ExportWithWrongHashNeitherCreatesNorOverwrites(bool projectScope, bool existing)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        if (existing)
        {
            _ = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        }

        byte[]? bytes = existing ? File.ReadAllBytes(fixture.FilePath) : null;
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ExportMethod, ParametersFor(ExportMethod, saved, WrongHash)), "conflict");

        fixture.AssertStateUnchanged(before);
        if (bytes is null)
        {
            Assert.IsFalse(File.Exists(fixture.FilePath));
            Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.AppRoot));
        }
        else
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
            fixture.AssertOnlySettingsFile();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExportRejectsAConfirmationAfterTheFileChanges(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity original = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        AssertFailure(fixture.Handle(ExportMethod, ParametersFor(ExportMethod, saved)), "overwriteRequired", original.FileSha256);
        SettingsTextFileIdentity changed = fixture.File.Export(CreateCandidate(fixture.Profile, 42), original.FileSha256);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ExportMethod, ParametersFor(ExportMethod, saved, original.FileSha256)), "conflict");

        Assert.AreEqual(changed.FileSha256, ContentDigest.Compute(File.ReadAllBytes(fixture.FilePath)));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertStateUnchanged(before);
        fixture.AssertOnlySettingsFile();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ApplyRejectsWrongRawHashIncludingTheCandidateSnapshotDigestBeforeAnyWrite(bool projectScope, bool snapshotDigest)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile);
        SettingsTextFileIdentity identity = fixture.File.Export(candidate, null);
        ContentDigest incorrect = snapshotDigest ? candidate.ContentDigest : WrongHash;
        Assert.AreNotEqual(identity.FileSha256, incorrect);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        int calls = 0;
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization, () =>
        {
            calls++;
            return fixture.UtcNow;
        });

        AssertFailure(fixture.Handle(handler, ApplyMethod, ParametersFor(ApplyMethod, saved, incorrect)), "conflict");

        Assert.AreEqual(0, calls);
        fixture.AssertStateUnchanged(before);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FormattingOnlyFileChangeRequiresFreshPreviewHashAndPreservesExactBomCrLfBytes(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile);
        SettingsTextFileIdentity original = fixture.File.Export(candidate, null);
        _ = ReadAvailable(fixture.Handle(PreviewMethod, ParametersFor(PreviewMethod, saved)), original.FileSha256);
        string formatted = Encoding.UTF8.GetString(File.ReadAllBytes(fixture.FilePath)).Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(formatted)];
        File.WriteAllBytes(fixture.FilePath, bytes);
        ContentDigest freshHash = ContentDigest.Compute(bytes);
        Assert.AreNotEqual(original.FileSha256, freshHash);
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ApplyMethod, ParametersFor(ApplyMethod, saved, original.FileSha256)), "conflict");
        fixture.AssertStateUnchanged(before);
        JsonElement preview = ReadAvailable(fixture.Handle(PreviewMethod, ParametersFor(PreviewMethod, saved)), freshHash);
        AssertCandidateSnapshot(saved, preview, candidate);
        fixture.AssertStateUnchanged(before);
        JsonElement applied = ReadAvailable(fixture.Handle(ApplyMethod, ParametersFor(ApplyMethod, saved, freshHash)), freshHash);

        Assert.AreEqual(candidate.ContentDigest.Hex, applied.GetProperty("snapshotDigest").GetString());
        AssertSnapshotEqual(applied, fixture.ReadSaved(projectScope, restart: true));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
    }

    [TestMethod]
    [DataRow(false, "expectedRevision")]
    [DataRow(false, "expectedProfileId")]
    [DataRow(false, "expectedProfileVersion")]
    [DataRow(false, "expectedProfileDigest")]
    [DataRow(false, "expectedSnapshotDigest")]
    [DataRow(true, "expectedRevision")]
    [DataRow(true, "expectedProfileId")]
    [DataRow(true, "expectedProfileVersion")]
    [DataRow(true, "expectedProfileDigest")]
    [DataRow(true, "expectedSnapshotDigest")]
    public void EveryMethodChecksEveryTargetIdentityFieldBeforeFileAccess(bool projectScope, string field)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        object stale = field switch
        {
            "expectedRevision" => saved.GetProperty("revision").GetInt32() + 1,
            "expectedProfileId" => EntityId<DefaultSettingsProfileIdTag>.New().ToString(),
            "expectedProfileVersion" => "2.0.0",
            _ => WrongHash.Hex,
        };
        using (var held = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (string method in FileMethods)
            {
                Dictionary<string, object?> parameters = ParametersFor(method, saved, identity.FileSha256);
                Assert.AreNotEqual(parameters[field], stale, field);
                parameters[field] = stale;
                AssertFailure(fixture.Handle(method, parameters), "conflict");
            }
        }

        fixture.AssertStateUnchanged(before);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        Assert.AreEqual(2, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(1, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnotherScopeCannotReuseTheOriginalTargetsIdentity(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        JsonElement other = fixture.ReadSaved(!projectScope);
        Assert.AreNotEqual(saved.GetProperty("snapshotDigest").GetString(), other.GetProperty("snapshotDigest").GetString());
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        SavedState before = fixture.CaptureState();

        foreach (string method in FileMethods)
        {
            Dictionary<string, object?> parameters = ParametersFor(method, saved, identity.FileSha256);
            parameters["projectId"] = other.GetProperty("projectId").GetString();
            AssertFailure(fixture.Handle(method, parameters), "conflict");
        }

        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CandidatePreviewIdentityCannotBeUsedAsTheSavedTargetIdentity(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        JsonElement preview = ReadAvailable(fixture.Handle(PreviewMethod, ParametersFor(PreviewMethod, saved)), identity.FileSha256);
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ApplyMethod, ParametersFor(ApplyMethod, preview, identity.FileSha256)), "conflict");

        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaleRevisionRejectsNoOpEvenAfterValuesReturnToTheSameDigest(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement original = fixture.ReadSaved(projectScope);
        DefaultSettingsSnapshot originalValues = fixture.CurrentSnapshot(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(originalValues, null);
        fixture.UpdateSaved(projectScope, CreateCandidate(fixture.Profile));
        fixture.UpdateSaved(projectScope, originalValues);
        JsonElement current = fixture.ReadSaved(projectScope);
        Assert.AreEqual(original.GetProperty("snapshotDigest").GetString(), current.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(original.GetProperty("revision").GetInt32() + 2, current.GetProperty("revision").GetInt32());
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ApplyMethod, ParametersFor(ApplyMethod, original, identity.FileSha256)), "conflict");

        fixture.AssertStateUnchanged(before);
        AssertSnapshotEqual(current, fixture.ReadSaved(projectScope, restart: true));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConcurrentStoreWriterWinsWithoutBeingOverwritten(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        DefaultSettingsSnapshot winner = ReplaceValue(fixture.Profile, fixture.CurrentSnapshot(projectScope),
            "settings.integer", DefaultSettingValue.FromInteger(44));
        int calls = 0;
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization, () =>
        {
            calls++;
            var racingStore = new DefaultSettingsStore(fixture.DatabasePath);
            if (projectScope)
            {
                _ = racingStore.UpdateProjectSettings(fixture.ProjectId, winner, before.Project.Revision, fixture.UtcNow);
            }
            else
            {
                _ = racingStore.UpdateGlobalSettings(winner, before.Global!.Revision, fixture.UtcNow);
            }

            return fixture.UtcNow;
        });

        AssertFailure(fixture.Handle(handler, ApplyMethod, ParametersFor(ApplyMethod, saved, identity.FileSha256)), "conflict");

        Assert.AreEqual(1, calls);
        Assert.AreEqual(winner.CanonicalJson, fixture.CurrentSnapshot(projectScope).CanonicalJson);
        Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 1, fixture.ReadSaved(projectScope, restart: true).GetProperty("revision").GetInt32());
        Assert.AreEqual(before.Writes + 1, fixture.ReadWriteCount());
        fixture.AssertOtherScopesUnchanged(before, projectScope);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertNoWorkStarted();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyDoesNotMoveTheSavedTimestampBackwards(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization,
            () => InitialTime.AddDays(-1));

        JsonElement applied = ReadAvailable(fixture.Handle(handler, ApplyMethod,
            ParametersFor(ApplyMethod, saved, identity.FileSha256)), identity.FileSha256);

        Assert.AreEqual(saved.GetProperty("updatedAt").GetDateTimeOffset(), applied.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.AreEqual(saved.GetProperty("revision").GetInt32() + 1, applied.GetProperty("revision").GetInt32());
    }

    [TestMethod]
    [DataRow(false, "id")]
    [DataRow(false, "version")]
    [DataRow(false, "digest")]
    [DataRow(true, "id")]
    [DataRow(true, "version")]
    [DataRow(true, "digest")]
    public void ReaderProfileMismatchesAreDistinctFromTargetConflictsAndNeverChangeBindings(bool projectScope, string field)
    {
        using Fixture fixture = new();
        DefaultSettingsProfile foreign = DefaultSettingsProfile.Create(
            field == "id" ? EntityId<DefaultSettingsProfileIdTag>.New() : fixture.Profile.Id,
            field == "digest" ? "Different profile content" : fixture.Profile.Name,
            field == "version" ? new SettingsProfileVersion(2, 0, 0) : fixture.Profile.Version,
            fixture.Profile.Definitions);
        SettingsTextFileIdentity identity = fixture.File.Export(foreign.CreateSnapshot(), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        JsonElement saved = fixture.ReadSaved(projectScope);
        SavedState before = fixture.CaptureState();
        SettingsTextFileException exception = Assert.ThrowsExactly<SettingsTextFileException>(() => fixture.File.Read(fixture.Profile));
        Assert.AreEqual(SettingsTextFileFailureReason.ProfileMismatch, exception.Reason);
        Assert.IsNull(exception.InnerException);

        foreach (string method in ReadMethods)
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, saved, identity.FileSha256)), "profileMismatch");
        }

        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(1, fixture.Store.ListProfiles().Count);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertOnlySettingsFile();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ReaderSnapshotMismatchIsNotRepairedOrApplied(bool projectScope, bool changedValue)
    {
        using Fixture fixture = new();
        DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile);
        _ = fixture.File.Export(candidate, null);
        JsonObject root = JsonNode.Parse(candidate.CanonicalJson)!.AsObject();
        if (changedValue)
        {
            ValueNode(root, "settings.integer")["integer"] = 37;
        }
        else
        {
            root["contentDigest"] = WrongHash.Hex;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(fixture.FilePath, bytes);
        JsonElement saved = fixture.ReadSaved(projectScope);
        SavedState before = fixture.CaptureState();
        SettingsTextFileException exception = Assert.ThrowsExactly<SettingsTextFileException>(() => fixture.File.Read(fixture.Profile));
        Assert.AreEqual(SettingsTextFileFailureReason.SnapshotMismatch, exception.Reason);
        Assert.IsNull(exception.InnerException);

        foreach (string method in ReadMethods)
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, saved, ContentDigest.Compute(bytes))), "snapshotMismatch");
        }

        fixture.AssertStateUnchanged(before);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        fixture.AssertOnlySettingsFile();
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("duplicate")]
    [DataRow("unknown")]
    [DataRow("missingValue")]
    [DataRow("wrongKind")]
    [DataRow("range")]
    [DataRow("allowlist")]
    [DataRow("tooLarge")]
    public void StrictReaderFailuresAreInvalidWithoutPartialApplyOrFileRepair(string variation)
    {
        foreach (bool projectScope in new[] { false, true })
        {
            using Fixture fixture = new();
            DefaultSettingsSnapshot candidate = CreateCandidate(fixture.Profile);
            _ = fixture.File.Export(candidate, null);
            JsonObject root = JsonNode.Parse(candidate.CanonicalJson)!.AsObject();
            switch (variation)
            {
                case "unknown": root["private"] = PrivateDetail; break;
                case "missingValue": root["values"]!.AsArray().RemoveAt(0); break;
                case "wrongKind":
                    ValueNode(root, "settings.integer")["kind"] = "boolean";
                    break;
                case "range":
                    ValueNode(root, "settings.ratio")["numerator"] = 2;
                    ValueNode(root, "settings.ratio")["denominator"] = 1;
                    break;
                case "allowlist": ValueNode(root, "settings.text")["text"] = PrivateDetail; break;
            }

            byte[] bytes = variation switch
            {
                "malformed" => [0xff],
                "duplicate" => Encoding.UTF8.GetBytes(candidate.CanonicalJson.Insert(1, "\"schemaVersion\":\"1.0.0\",")),
                "tooLarge" => new byte[SettingsTextFile.MaximumRawUtf8Bytes + 1],
                _ => Encoding.UTF8.GetBytes(root.ToJsonString()),
            };
            File.WriteAllBytes(fixture.FilePath, bytes);
            byte[] acl = FileDescriptor(fixture.FilePath);
            JsonElement saved = fixture.ReadSaved(projectScope);
            SavedState before = fixture.CaptureState();
            SettingsTextFileException exception = Assert.ThrowsExactly<SettingsTextFileException>(() => fixture.File.Read(fixture.Profile));
            Assert.AreEqual(variation == "tooLarge" ? SettingsTextFileFailureReason.TooLarge : SettingsTextFileFailureReason.Invalid,
                exception.Reason);

            foreach (string method in ReadMethods)
            {
                AssertFailure(fixture.Handle(method, ParametersFor(method, saved, ContentDigest.Compute(bytes))), "invalid");
            }

            fixture.AssertStateUnchanged(before);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
            CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
            fixture.AssertOnlySettingsFile();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingFileIsReportedWithoutCreatingOrInitializingAnything(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SavedState before = fixture.CaptureState();

        foreach (string method in ReadMethods)
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, saved, WrongHash)), "fileNotFound");
        }

        fixture.AssertStateUnchanged(before);
        Assert.IsFalse(File.Exists(fixture.FilePath));
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.AppRoot));
    }

    [TestMethod]
    [DataRow("global", "unavailable")]
    [DataRow("missingProject", "notFound")]
    [DataRow("unboundProject", "unbound")]
    public void MissingOrUnboundTargetFailsBeforeFileAccess(string target, string expectedStatus)
    {
        using Fixture fixture = new(initializeGlobal: target != "global");
        JsonElement identity = fixture.ReadSaved(projectScope: true);
        string? projectId = target switch
        {
            "global" => null,
            "missingProject" => EntityId<ProjectIdTag>.New().ToString(),
            _ => fixture.CreateProject(null).ToString(),
        };
        SettingsTextFileIdentity fileIdentity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        using (var held = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (string method in FileMethods)
            {
                Dictionary<string, object?> parameters = ParametersFor(method, identity, fileIdentity.FileSha256);
                parameters["projectId"] = projectId;
                AssertFailure(fixture.Handle(method, parameters), expectedStatus);
            }
        }

        fixture.AssertStateUnchanged(before);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        if (target == "unboundProject")
        {
            Assert.IsFalse(fixture.Store.GetProjectSettings(EntityId<ProjectIdTag>.Parse(projectId!)).IsBound);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void AuthorizationPrecedesMissingOrCorruptTargetAndFileInspection(bool projectScope, bool corrupt)
    {
        using Fixture fixture = new(initializeGlobal: corrupt || projectScope);
        JsonElement identity = fixture.ReadSaved(projectScope: true);
        string? projectId = projectScope
            ? (corrupt ? fixture.ProjectId.ToString() : EntityId<ProjectIdTag>.New().ToString())
            : null;
        _ = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        byte[] fileBytes = Encoding.UTF8.GetBytes(PrivateDetail);
        File.WriteAllBytes(fixture.FilePath, fileBytes);
        if (corrupt)
        {
            fixture.ExecuteSql(projectScope
                ? "UPDATE project_default_settings SET current_snapshot_digest = '" + new string('0', 64) + "';"
                : "UPDATE default_settings_global SET snapshot_digest = '" + new string('0', 64) + "';");
        }

        byte[] databaseBytes = File.ReadAllBytes(fixture.DatabasePath);
        fixture.Authorization.DenyRead = true;
        fixture.Authorization.DenyEdit = true;

        foreach (string method in FileMethods)
        {
            Dictionary<string, object?> parameters = ParametersFor(method, identity, WrongHash);
            parameters["projectId"] = projectId;
            AssertFailure(fixture.Handle(method, parameters), "forbidden");
        }

        Assert.AreEqual(2, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(1, fixture.Authorization.EditDemandCount);
        CollectionAssert.AreEqual(databaseBytes, File.ReadAllBytes(fixture.DatabasePath));
        CollectionAssert.AreEqual(fileBytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertOnlySettingsFile();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReadPermissionDoesNotPermitApplyEvenWithValidPreview(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        fixture.Authorization.DenyEdit = true;
        _ = ReadAvailable(fixture.Handle(PreviewMethod, ParametersFor(PreviewMethod, saved)), identity.FileSha256);
        SavedState before = fixture.CaptureState();

        AssertFailure(fixture.Handle(ApplyMethod, ParametersFor(ApplyMethod, saved, identity.FileSha256)), "forbidden");

        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(1, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow("acl")]
    [DataRow("unsafePath")]
    [DataRow("locked")]
    public void FileSecurityAndIoFailuresUseClosedStatusesWithoutLeakingDetails(string variation)
    {
        foreach (bool projectScope in new[] { false, true })
        {
            using Fixture fixture = new();
            JsonElement saved = fixture.ReadSaved(projectScope);
            SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
            byte[] bytes = File.ReadAllBytes(fixture.FilePath);
            SavedState before = fixture.CaptureState();
            byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
            string preservedPath = fixture.FilePath;
            if (variation == "acl")
            {
                FileSecurity security = FileSystemAclExtensions.GetAccessControl(new FileInfo(fixture.FilePath));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read, AccessControlType.Allow));
                FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
            }
            else if (variation == "unsafePath")
            {
                preservedPath = Path.Combine(fixture.RootPath, "preserved-setting.txt");
                File.Move(fixture.FilePath, preservedPath);
                Directory.CreateDirectory(fixture.FilePath);
            }

            byte[] acl = FileDescriptor(preservedPath);
            using (FileStream? held = variation == "locked"
                ? new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.None)
                : null)
            {
                SettingsTextFileException exception = Assert.ThrowsExactly<SettingsTextFileException>(() => fixture.File.Read(fixture.Profile));
                Assert.AreEqual(variation switch
                {
                    "acl" => SettingsTextFileFailureReason.AccessDenied,
                    "unsafePath" => SettingsTextFileFailureReason.UnsafePath,
                    _ => SettingsTextFileFailureReason.Unavailable,
                }, exception.Reason);
                Assert.IsNull(exception.InnerException);
                foreach (string method in FileMethods)
                {
                    AssertFailure(fixture.Handle(method, ParametersFor(method, saved, identity.FileSha256)), "unavailable");
                }
            }

            fixture.AssertStateUnchanged(before);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(preservedPath));
            CollectionAssert.AreEqual(acl, FileDescriptor(preservedPath));
            CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
            fixture.AssertOnlySettingsFile();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StoreWriteFailureDoesNotPartiallyApplyOrLeakSqlDetails(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        byte[] bytes = File.ReadAllBytes(fixture.FilePath);
        SavedState before = fixture.CaptureState();
        string table = projectScope ? "project_default_settings" : "default_settings_global";
        fixture.ExecuteSql($"""
            CREATE TRIGGER ux010_fail_write BEFORE UPDATE ON {table}
            BEGIN
                SELECT RAISE(ABORT, '{PrivateDetail}');
            END;
            """);

        AssertFailure(fixture.Handle(ApplyMethod, ParametersFor(ApplyMethod, saved, identity.FileSha256)), "unavailable");

        fixture.AssertStateUnchanged(before);
        AssertSnapshotEqual(saved, fixture.ReadSaved(projectScope, restart: true));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fixture.FilePath));
        fixture.AssertOnlySettingsFile();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnexpectedFailureRedactsRootAndInnerExceptionAndDoesNotWrite(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        SavedState before = fixture.CaptureState();
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization,
            () => throw new IOException(fixture.AppRoot, new InvalidOperationException(PrivateDetail)));

        AssertFailure(fixture.Handle(handler, ApplyMethod, ParametersFor(ApplyMethod, saved, identity.FileSha256)), "unavailable");

        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationBeforeWorkFailsClosedWithoutMutationAndStillRequiresAuthorization(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        SavedState before = fixture.CaptureState();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (string method in FileMethods)
        {
            AssertFailure(fixture.Handle(fixture.Handler, method, ParametersFor(method, saved, identity.FileSha256), cancellation.Token),
                "unavailable");
        }

        fixture.Authorization.DenyEdit = true;
        AssertFailure(fixture.Handle(fixture.Handler, ApplyMethod,
            ParametersFor(ApplyMethod, saved, identity.FileSha256), cancellation.Token), "forbidden");
        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
        Assert.AreEqual(2, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(2, fixture.Authorization.EditDemandCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationImmediatelyBeforeStoreUpdateDoesNotCommit(bool projectScope)
    {
        using Fixture fixture = new();
        JsonElement saved = fixture.ReadSaved(projectScope);
        SettingsTextFileIdentity identity = fixture.File.Export(CreateCandidate(fixture.Profile), null);
        SavedState before = fixture.CaptureState();
        using var cancellation = new CancellationTokenSource();
        var handler = new SettingsFileBridgeRequestHandler(fixture.Store, fixture.File, fixture.Authorization, () =>
        {
            cancellation.Cancel();
            return fixture.UtcNow;
        });

        AssertFailure(fixture.Handle(handler, ApplyMethod,
            ParametersFor(ApplyMethod, saved, identity.FileSha256), cancellation.Token), "unavailable");

        fixture.AssertStateUnchanged(before);
        Assert.AreEqual(identity.FileSha256, fixture.File.InspectIdentity()!.FileSha256);
    }

    [TestMethod]
    public void NonFileMethodsAreRejectedWithoutAuthorizationOrLookup()
    {
        using Fixture fixture = new(initializeGlobal: false);
        foreach (BridgeRequest request in new[]
        {
            ParseRequest("settings.global.read", new { }),
            ParseRequest("settings.read", new { projectId = EntityId<ProjectIdTag>.New().ToString() }),
            ParseRequest("bridge.ping", new { }),
        })
        {
            AssertFailure(fixture.Handler.Handle(request), "invalid");
        }

        Assert.AreEqual(0, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(0, fixture.Authorization.EditDemandCount);
        Assert.IsNull(fixture.Store.GetGlobalSettings());
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.AppRoot));
    }

    [TestMethod]
    public void MalformedUnparsedRequestsAreInvalidAfterAuthorizationWithoutStoreOrFileAccess()
    {
        using Fixture fixture = new(initializeGlobal: false);
        foreach (BridgeMethod method in new[] { BridgeMethod.SettingsFileExport, BridgeMethod.SettingsFilePreview, BridgeMethod.SettingsFileApply })
        {
            BridgeRequest request = BridgeRequest.Create(Guid.NewGuid(), method, JsonSerializer.SerializeToElement(new { projectId = (string?)null }));
            AssertFailure(fixture.Handler.Handle(request), "invalid");
        }

        Assert.AreEqual(2, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(1, fixture.Authorization.EditDemandCount);
        Assert.IsNull(fixture.Store.GetGlobalSettings());
        Assert.IsEmpty(Directory.GetFileSystemEntries(fixture.AppRoot));
    }

    private static Dictionary<string, object?> ParametersFor(string method, JsonElement snapshot, ContentDigest? fileSha256 = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["projectId"] = snapshot.GetProperty("projectId").GetString(),
            ["expectedRevision"] = snapshot.GetProperty("revision").GetInt32(),
            ["expectedProfileId"] = snapshot.GetProperty("profileId").GetString(),
            ["expectedProfileVersion"] = snapshot.GetProperty("profileVersion").GetString(),
            ["expectedProfileDigest"] = snapshot.GetProperty("profileDigest").GetString(),
            ["expectedSnapshotDigest"] = snapshot.GetProperty("snapshotDigest").GetString(),
        };
        if (method is ExportMethod or ApplyMethod)
        {
            parameters["expectedFileSha256"] = fileSha256?.Hex;
        }

        return parameters;
    }

    private static BridgeRequest ParseRequest(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            requestId = Guid.NewGuid().ToString("D"),
            method,
            @params = parameters,
        });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json,
            out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
        Assert.IsNotNull(request);
        return request;
    }

    private static JsonElement ReadResult(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static JsonElement ReadAvailable(string response, ContentDigest fileSha256)
    {
        JsonElement result = ReadResult(response);
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        Assert.AreEqual(fileSha256.Hex, result.GetProperty("fileSha256").GetString());
        Assert.AreEqual(JsonValueKind.Object, result.GetProperty("snapshot").ValueKind);
        return result.GetProperty("snapshot");
    }

    private static void AssertFailure(string response, string expectedStatus, ContentDigest? expectedHash = null)
    {
        JsonElement result = ReadResult(response);
        Assert.AreEqual(expectedStatus, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
        if (expectedStatus == "overwriteRequired")
        {
            Assert.IsNotNull(expectedHash);
            Assert.AreEqual(expectedHash.Hex, result.GetProperty("fileSha256").GetString());
        }
        else
        {
            Assert.IsNull(expectedHash);
            Assert.AreEqual(JsonValueKind.Null, result.GetProperty("fileSha256").ValueKind);
        }

        foreach (string forbidden in new[] { PrivateDetail, "Exception", "setting.txt", "settings.db", "SELECT", "UPDATE" })
        {
            Assert.IsFalse(response.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertEnvelope(BridgeRequest request, string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(request.RequestId.ToString("D"), root.GetProperty("requestId").GetString());
        Assert.AreEqual(BridgeProtocol.GetMethodName(request.Method), root.GetProperty("method").GetString());
        CollectionAssert.AreEqual(new[] { "status", "snapshot", "fileSha256" },
            root.GetProperty("result").EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.IsTrue(Encoding.UTF8.GetByteCount(response) <= BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
    }

    private static void AssertSnapshotEqual(JsonElement expected, JsonElement actual) =>
        Assert.AreEqual(expected.GetRawText(), actual.GetRawText());

    private static void AssertCandidateSnapshot(JsonElement saved, JsonElement preview, DefaultSettingsSnapshot candidate)
    {
        CollectionAssert.AreEqual(saved.EnumerateObject().Select(static property => property.Name).ToArray(),
            preview.EnumerateObject().Select(static property => property.Name).ToArray());
        foreach (JsonProperty property in saved.EnumerateObject())
        {
            if (property.Name is not ("snapshotDigest" or "values"))
            {
                Assert.AreEqual(property.Value.GetRawText(), preview.GetProperty(property.Name).GetRawText(), property.Name);
            }
        }

        Assert.AreEqual(candidate.ContentDigest.Hex, preview.GetProperty("snapshotDigest").GetString());
        AssertSelections(candidate, preview.GetProperty("values"));
    }

    private static void AssertSelections(DefaultSettingsSnapshot expected, JsonElement actual)
    {
        CollectionAssert.AreEqual(expected.Values.Select(static selection => selection.Key).ToArray(),
            actual.EnumerateArray().Select(static selection => selection.GetProperty("key").GetString()).ToArray());
        foreach (DefaultSettingSelection selection in expected.Values)
        {
            JsonElement value = actual.EnumerateArray()
                .Single(item => item.GetProperty("key").GetString() == selection.Key).GetProperty("value");
            switch (selection.Value.Kind)
            {
                case DefaultSettingValueKind.Boolean:
                    Assert.AreEqual("boolean", value.GetProperty("kind").GetString());
                    Assert.AreEqual(selection.Value.BooleanValue!.Value, value.GetProperty("booleanValue").GetBoolean());
                    break;
                case DefaultSettingValueKind.Integer:
                    Assert.AreEqual("integer", value.GetProperty("kind").GetString());
                    Assert.AreEqual(selection.Value.IntegerValue!.Value.ToString(CultureInfo.InvariantCulture), value.GetProperty("integerValue").GetString());
                    break;
                case DefaultSettingValueKind.Rational:
                    Assert.AreEqual("rational", value.GetProperty("kind").GetString());
                    Assert.AreEqual(selection.Value.RationalValue!.Value.Numerator.ToString(CultureInfo.InvariantCulture), value.GetProperty("numerator").GetString());
                    Assert.AreEqual(selection.Value.RationalValue!.Value.Denominator.ToString(CultureInfo.InvariantCulture), value.GetProperty("denominator").GetString());
                    break;
                case DefaultSettingValueKind.Text:
                    Assert.AreEqual("text", value.GetProperty("kind").GetString());
                    Assert.AreEqual(selection.Value.TextValue, value.GetProperty("textValue").GetString());
                    break;
                case DefaultSettingValueKind.TextList:
                    Assert.AreEqual("textList", value.GetProperty("kind").GetString());
                    CollectionAssert.AreEqual(selection.Value.TextListValue.ToArray(),
                        value.GetProperty("textListValue").EnumerateArray().Select(static item => item.GetString()).ToArray());
                    break;
                default:
                    Assert.Fail("Unexpected settings value kind.");
                    break;
            }
        }
    }

    private static void AssertGlobalEqual(StoredGlobalSettings? expected, StoredGlobalSettings? actual)
    {
        if (expected is null)
        {
            Assert.IsNull(actual);
            return;
        }

        Assert.IsNotNull(actual);
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

    private static DefaultSettingsProfile CreateProfile() =>
        DefaultSettingsProfile.Create(EntityId<DefaultSettingsProfileIdTag>.New(), "UX010 file bridge test", new SettingsProfileVersion(1, 0, 0),
        [
            Definition("settings.flag", DefaultSettingValue.FromBoolean(true)),
            Definition("settings.integer", DefaultSettingValue.FromInteger(0),
                DefaultSettingValue.FromInteger(long.MinValue), DefaultSettingValue.FromInteger(long.MaxValue)),
            Definition("settings.ratio", DefaultSettingValue.FromRational(1, 2),
                DefaultSettingValue.FromRational(0, 1), DefaultSettingValue.FromRational(1, 1)),
            Definition("settings.text", DefaultSettingValue.FromText("既定値"), allowed: ["既定値", "案件初期値", CandidateText]),
            Definition("settings.list", DefaultSettingValue.FromTextList(["既定値"])),
        ]);

    private static DefaultSettingDefinition Definition(string key, DefaultSettingValue value,
        DefaultSettingValue? minimum = null, DefaultSettingValue? maximum = null, IReadOnlyList<string>? allowed = null) =>
        new(key, DefaultSettingGroup.Editing, false, key, "Isolated settings file bridge test", DefaultSettingUnit.None,
            value, minimum, maximum, allowed,
            [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")]);

    private static DefaultSettingsSnapshot CreateCandidate(DefaultSettingsProfile profile, long integer = long.MaxValue) =>
        profile.CreateSnapshot(
        [
            new DefaultSettingSelection("settings.flag", DefaultSettingValue.FromBoolean(false)),
            new DefaultSettingSelection("settings.integer", DefaultSettingValue.FromInteger(integer)),
            new DefaultSettingSelection("settings.ratio", DefaultSettingValue.FromRational(9007199254740993, 9007199254740995)),
            new DefaultSettingSelection("settings.text", DefaultSettingValue.FromText(CandidateText)),
            new DefaultSettingSelection("settings.list", DefaultSettingValue.FromTextList(["末尾", "先頭", "値\t空白"])),
        ]);

    private static DefaultSettingsSnapshot ReplaceValue(DefaultSettingsProfile profile, DefaultSettingsSnapshot snapshot,
        string key, DefaultSettingValue replacement) =>
        profile.CreateSnapshot(snapshot.Values.Select(selection => new DefaultSettingSelection(selection.Key,
            selection.Key == key ? replacement : selection.Value)).ToArray());

    private static JsonObject ValueNode(JsonObject root, string key) =>
        root["values"]!.AsArray().Single(item => item!["key"]!.GetValue<string>() == key)!["value"]!.AsObject();

    private static byte[] FileDescriptor(string path) =>
        FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access | AccessControlSections.Owner)
            .GetSecurityDescriptorBinaryForm();

    private static byte[] DirectoryDescriptor(string path) =>
        FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access | AccessControlSections.Owner)
            .GetSecurityDescriptorBinaryForm();

    private static void AssertFileAcl(string path, string currentSid)
    {
        FileSecurity security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path),
            AccessControlSections.Access | AccessControlSections.Owner);
        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.AreEqual(currentSid, ((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!).Value);
        FileSystemAccessRule[] rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        CollectionAssert.AreEquivalent(new[] { currentSid, "S-1-5-18", "S-1-5-32-544" },
            rules.Select(static rule => rule.IdentityReference.Value).ToArray());
        Assert.IsTrue(rules.All(static rule => !rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights == FileSystemRights.FullControl && rule.InheritanceFlags == InheritanceFlags.None &&
            rule.PropagationFlags == PropagationFlags.None));
    }

    private sealed record SavedState(StoredGlobalSettings? Global, StoredProjectSettings Project, StoredProjectSettings Other, long Writes);

    private sealed class Fixture : IDisposable
    {
        internal Fixture(bool initializeGlobal = true)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            CurrentSid = identity.User?.Value ?? throw new AssertInconclusiveException("The Windows runner has no user SID.");
            RootPath = Path.Combine(Path.GetTempPath(), nameof(SettingsFileBridgeTests), Guid.NewGuid().ToString("N"));
            AppRoot = Path.Combine(RootPath, "application");
            Directory.CreateDirectory(AppRoot);
            DatabasePath = Path.Combine(RootPath, "settings.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(RootPath, "backups"));
            Store = new DefaultSettingsStore(DatabasePath);
            Profile = Store.RegisterProfile(Encoding.UTF8.GetBytes(CreateProfile().CanonicalJson));
            DefaultSettingsSnapshot defaults = Profile.CreateSnapshot();
            if (initializeGlobal)
            {
                _ = Store.InitializeGlobalSettings(ReplaceValue(Profile, defaults, "settings.integer", DefaultSettingValue.FromInteger(12)), InitialTime);
            }

            ProjectId = CreateProject(ReplaceValue(Profile, defaults, "settings.text", DefaultSettingValue.FromText("案件初期値")));
            OtherProjectId = CreateProject(defaults);
            File = new SettingsTextFile(AppRoot, CurrentSid);
            Handler = new SettingsFileBridgeRequestHandler(Store, File, Authorization, () => UtcNow);
            ExecuteSql("""
                CREATE TABLE ux010_writes (target TEXT NOT NULL);
                CREATE TRIGGER ux010_global_write AFTER UPDATE ON default_settings_global
                BEGIN
                    INSERT INTO ux010_writes (target) VALUES ('global');
                END;
                CREATE TRIGGER ux010_project_write AFTER UPDATE ON project_default_settings
                BEGIN
                    INSERT INTO ux010_writes (target) VALUES ('project');
                END;
                """);
        }

        internal string RootPath { get; }
        internal string AppRoot { get; }
        internal string FilePath => Path.Combine(AppRoot, SettingsTextFile.FileName);
        internal string DatabasePath { get; }
        internal string CurrentSid { get; }
        internal DefaultSettingsStore Store { get; }
        internal DefaultSettingsProfile Profile { get; }
        internal EntityId<ProjectIdTag> ProjectId { get; }
        internal EntityId<ProjectIdTag> OtherProjectId { get; }
        internal SettingsTextFile File { get; }
        internal FakeAuthorization Authorization { get; } = new();
        internal SettingsFileBridgeRequestHandler Handler { get; }
        internal DateTimeOffset UtcNow { get; set; } = InitialTime.AddMinutes(1);

        internal EntityId<ProjectIdTag> CreateProject(DefaultSettingsSnapshot? initial)
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(projectId, "UX010 isolation test", "Settings file bridge", "Internal",
                PublicationScope.InternalOnly, null, [], []);
            new ProjectStore(DatabasePath).CreateProject(project, "{\"project\":true}", InitialTime);
            if (initial is not null)
            {
                _ = Store.BindProjectSettings(projectId, initial, InitialTime);
            }

            return projectId;
        }

        internal DefaultSettingsSnapshot CurrentSnapshot(bool projectScope) => projectScope
            ? Store.GetProjectSettings(ProjectId).Settings!.CurrentSnapshot
            : Store.GetGlobalSettings()!.Snapshot;

        internal void UpdateSaved(bool projectScope, DefaultSettingsSnapshot snapshot)
        {
            if (projectScope)
            {
                _ = Store.UpdateProjectSettings(ProjectId, snapshot, Store.GetProjectSettings(ProjectId).Settings!.Revision, UtcNow);
            }
            else
            {
                _ = Store.UpdateGlobalSettings(snapshot, Store.GetGlobalSettings()!.Revision, UtcNow);
            }
        }

        internal JsonElement ReadSaved(bool projectScope, bool restart = false)
        {
            DefaultSettingsStore store = restart ? new DefaultSettingsStore(DatabasePath) : Store;
            var authorization = new FakeAuthorization();
            string response = projectScope
                ? new SettingsBridgeRequestHandler(store, authorization).Handle(ParseRequest("settings.read", new { projectId = ProjectId.ToString() }))
                : new GlobalSettingsBridgeRequestHandler(store, authorization).Handle(ParseRequest("settings.global.read", new { }));
            JsonElement result = ReadResult(response);
            Assert.AreEqual("available", result.GetProperty("status").GetString());
            return result.GetProperty("snapshot");
        }

        internal string Handle(string method, object parameters) => Handle(Handler, method, parameters);

        internal string Handle(SettingsFileBridgeRequestHandler handler, string method, object parameters,
            CancellationToken cancellationToken = default)
        {
            BridgeRequest request = ParseRequest(method, parameters);
            string response = handler.Handle(request, cancellationToken);
            AssertEnvelope(request, response);
            foreach (string privateValue in new[] { RootPath, AppRoot, DatabasePath, CurrentSid, PrivateDetail })
            {
                Assert.IsFalse(response.Contains(privateValue, StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(response.Contains(JsonSerializer.Serialize(privateValue), StringComparison.OrdinalIgnoreCase));
            }

            return response;
        }

        internal SavedState CaptureState() => new(Store.GetGlobalSettings(), Store.GetProjectSettings(ProjectId).Settings!,
            Store.GetProjectSettings(OtherProjectId).Settings!, ReadWriteCount());

        internal void AssertStateUnchanged(SavedState expected)
        {
            SavedState actual = CaptureState();
            AssertGlobalEqual(expected.Global, actual.Global);
            AssertProjectEqual(expected.Project, actual.Project);
            AssertProjectEqual(expected.Other, actual.Other);
            Assert.AreEqual(expected.Writes, actual.Writes);
            AssertNoWorkStarted();
        }

        internal void AssertOtherScopesUnchanged(SavedState expected, bool projectScope)
        {
            if (projectScope)
            {
                AssertGlobalEqual(expected.Global, Store.GetGlobalSettings());
                Assert.AreEqual(expected.Project.InitialSnapshot.CanonicalJson, Store.GetProjectSettings(ProjectId).Settings!.InitialSnapshot.CanonicalJson);
            }
            else
            {
                AssertProjectEqual(expected.Project, Store.GetProjectSettings(ProjectId).Settings!);
            }

            AssertProjectEqual(expected.Other, Store.GetProjectSettings(OtherProjectId).Settings!);
        }

        internal void AssertOnlySettingsFile() =>
            CollectionAssert.AreEquivalent(new[] { SettingsTextFile.FileName },
                Directory.GetFileSystemEntries(AppRoot).Select(static path => Path.GetFileName(path)).ToArray());

        internal long ReadWriteCount() => ReadCount("SELECT COUNT(*) FROM ux010_writes;");

        internal void AssertNoWorkStarted() => Assert.AreEqual(0L, ReadCount("""
            SELECT (SELECT COUNT(*) FROM jobs) +
                   (SELECT COUNT(*) FROM composition_versions) +
                   (SELECT COUNT(*) FROM project_creation_drafts);
            """));

        private long ReadCount(string sql)
        {
            using SqliteConnection connection = OpenDatabase();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar() ?? throw new AssertFailedException("A test counter was unavailable."));
        }

        internal void ExecuteSql(string sql)
        {
            using SqliteConnection connection = OpenDatabase();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            _ = command.ExecuteNonQuery();
        }

        private SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }

    private sealed class FakeAuthorization : ISettingsBridgeAuthorization
    {
        internal bool DenyRead { get; set; }
        internal bool DenyEdit { get; set; }
        internal int ReadDemandCount { get; private set; }
        internal int EditDemandCount { get; private set; }

        public void DemandRead()
        {
            ReadDemandCount++;
            if (DenyRead)
            {
                throw new AuthorizationException(AuthorizationOperation.ViewProject, null);
            }
        }

        public void DemandEdit()
        {
            EditDemandCount++;
            if (DenyEdit)
            {
                throw new AuthorizationException(AuthorizationOperation.CreateCompositionVersion, AuthorizationRole.Editor);
            }
        }
    }
}