using HubRadio.App.DefaultSettings;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyProjectCreationContractTests
{
    [TestMethod]
    public void ResolveUsesSourceStemAndVersionedProfileWithoutExposingPath()
    {
        using Fixture fixture = new();
        EntityId<AssetIdTag> candidateId = EntityId<AssetIdTag>.New();

        SourceOnlyProjectCreationResolution resolution =
            SourceOnlyProjectCreationContract.Resolve(
                EntityId<ProjectCreationDraftIdTag>.New(),
                candidateId,
                @"C:\private\Hubらじお.第42回.mov",
                fixture.GlobalSettings,
                fixture.Catalog);

        ProjectCreationDraft draft = resolution.Draft;
        Assert.AreEqual("Hubらじお.第42回", draft.ContentName);
        Assert.AreEqual("社内向け再利用動画の自動編集", draft.Purpose);
        Assert.AreEqual("社内視聴者", draft.Audience);
        Assert.AreEqual(PublicationScope.InternalOnly, draft.PublicationScope);
        Assert.AreEqual("ja-JP", draft.Language);
        CollectionAssert.AreEqual(new[] { "話者1" }, draft.PerformerNames.ToArray());
        Assert.IsNull(draft.DeadlineSelection!.Deadline);
        Assert.AreEqual(candidateId, draft.SourceCandidateId);
        Assert.IsTrue(draft.IsComplete);
        Assert.AreEqual(
            fixture.GlobalSettings.Snapshot.ContentDigest,
            resolution.SettingsSnapshot.ContentDigest);
        CollectionAssert.AreEqual(
            new[] { "long-form-1280x720", "vertical-short-1080x1920" },
            draft.OutputProfiles.Select(static profile => profile.Name).ToArray());
        Assert.AreEqual("development-neutral-long-form", draft.BrandTemplate!.Name);
        Assert.IsTrue(resolution.ArtifactSet.Contains(
            "landscapeLongForm",
            StringComparer.Ordinal));
        string projected = string.Join('|',
            draft.ContentName,
            draft.Purpose,
            draft.Audience,
            draft.Language,
            draft.BrandTemplate.Name,
            string.Join(',', resolution.ArtifactSet));
        Assert.IsFalse(projected.Contains(@"C:\private", StringComparison.Ordinal));
        Assert.IsFalse(projected.Contains(".mov", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DeriveContentNameRejectsUnsafeNamesWithStablePrivateSafeError()
    {
        string invalidUtf16 = "C:\\private\\bad\ud800.mov";
        string[] invalidPaths =
        [
            @"C:\private\.mov",
            @"C:\private\CON.mov",
            @"C:\private\name .mov",
            @"C:\private\name..mov",
            "C:\\private\\line\nbreak.mov",
            invalidUtf16,
            @"C:\private\" +
                new string(
                    'x',
                    ProjectCreationDraftStore.MaximumPayloadUtf8Bytes + 1) +
                ".mov",
        ];

        foreach (string path in invalidPaths)
        {
            SourceOnlyProjectCreationException exception =
                Assert.ThrowsExactly<SourceOnlyProjectCreationException>(() =>
                    SourceOnlyProjectCreationContract.DeriveContentName(path));
            Assert.AreEqual(
                SourceOnlyProjectCreationFailureReason.UnsafeSourceName,
                exception.Reason);
            Assert.IsFalse(exception.Message.Contains(path, StringComparison.Ordinal));
            Assert.IsFalse(exception.Message.Contains(@"C:\private", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ResolveRejectsMissingSettingsWithoutReadingSourceDetailsIntoError()
    {
        using Fixture fixture = new();

        SourceOnlyProjectCreationException exception =
            Assert.ThrowsExactly<SourceOnlyProjectCreationException>(() =>
                SourceOnlyProjectCreationContract.Resolve(
                    EntityId<ProjectCreationDraftIdTag>.New(),
                    EntityId<AssetIdTag>.New(),
                    @"C:\private\sensitive.mov",
                    globalSettings: null,
                    fixture.Catalog));

        Assert.AreEqual(
            SourceOnlyProjectCreationFailureReason.SettingsUnavailable,
            exception.Reason);
        Assert.IsFalse(exception.Message.Contains("sensitive", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains(@"C:\", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string rootPath;

        internal Fixture()
        {
            rootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.SourceOnlyProjectCreationContractTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            string databasePath = Path.Combine(rootPath, "hubradio.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(rootPath, "backups"));
            var store = new DefaultSettingsStore(databasePath);
            DefaultSettingsBootstrapResult bootstrap =
                new BundledDefaultSettingsBootstrap(store).Initialize(
                    ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles"),
                    new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
            GlobalSettings = bootstrap.GlobalSettings;
            Catalog = ProjectCreationCatalog.Load(
                ResolveRepositoryPath("src/HubRadio.App/Profiles"));
        }

        internal StoredGlobalSettings GlobalSettings { get; }

        internal ProjectCreationCatalog Catalog { get; }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                string path = Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(path))
                {
                    throw new DirectoryNotFoundException(relativePath);
                }

                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }
}