using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class ArtifactStoreTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.FromHours(9));

    [TestMethod]
    public async Task PromoteAsync_HashesMovesRegistersAndLoadsCanonicalArtifact()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("completed staging artifact");
        string stagingPath = fixture.CreateStagingFile(
            projectId,
            "completed/source.bin",
            content);
        ArtifactPromotionRequest request = fixture.CreateRequest(
            projectId,
            ArtifactKind.Original,
            stagingPath,
            "media/source.bin",
            content,
            createdAt: CreatedAt);

        StoredArtifact promoted = await fixture.Store.PromoteAsync(request);

        const string relativeStorageSuffix = "/original/media/source.bin";
        string expectedStorageReference = string.Concat(
            "projects/",
            projectId.ToString(),
            relativeStorageSuffix);
        string destinationPath = fixture.GetStoragePath(expectedStorageReference);
        Assert.IsFalse(File.Exists(stagingPath));
        Assert.IsTrue(File.Exists(destinationPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(destinationPath));
        Assert.AreEqual(ContentDigest.Compute(content), ContentDigest.Compute(File.ReadAllBytes(destinationPath)));
        AssertArtifact(
            promoted,
            request.Id,
            projectId,
            ArtifactKind.Original,
            expectedStorageReference,
            ContentDigest.Compute(content),
            content.LongLength,
            CreatedAt.ToUniversalTime());

        StoredArtifact loaded = fixture.Store.GetArtifact(request.Id)
            ?? throw new AssertFailedException("The promoted artifact was not loaded.");
        AssertArtifactEqual(promoted, loaded);
        IReadOnlyList<StoredArtifact> listed =
            fixture.Store.ListProjectArtifacts(projectId);
        Assert.AreEqual(1, listed.Count);
        AssertArtifactEqual(promoted, listed[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredArtifact>)listed).Clear());
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.IsNull(fixture.Store.GetArtifact(EntityId<ArtifactIdTag>.New()));
    }

    [TestMethod]
    public async Task PromoteAsync_MapsAllEightKindsToTheirExactFolders()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        (ArtifactKind Kind, string Folder)[] mappings =
        [
            (ArtifactKind.Original, "original"),
            (ArtifactKind.Proxy, "proxy"),
            (ArtifactKind.Analysis, "analysis"),
            (ArtifactKind.Composition, "compositions"),
            (ArtifactKind.Preview, "previews"),
            (ArtifactKind.Render, "renders"),
            (ArtifactKind.Delivery, "deliveries"),
            (ArtifactKind.Evidence, "evidence"),
        ];

        for (int index = 0; index < mappings.Length; index++)
        {
            (ArtifactKind kind, string folder) = mappings[index];
            byte[] content = Encoding.UTF8.GetBytes($"kind-{kind}");
            string stagingPath = fixture.CreateStagingFile(
                projectId,
                $"kind-{index}.bin",
                content);
            ArtifactPromotionRequest request = fixture.CreateRequest(
                projectId,
                kind,
                stagingPath,
                $"nested/item-{index}.bin",
                content,
                createdAt: CreatedAt.AddMinutes(index));

            StoredArtifact stored = await fixture.Store.PromoteAsync(request);

            string expectedReference = string.Concat(
                "projects/",
                projectId.ToString(),
                "/",
                folder,
                "/nested/item-",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ".bin");
            Assert.AreEqual(expectedReference, stored.StorageReference);
            CollectionAssert.AreEqual(
                content,
                File.ReadAllBytes(fixture.GetStoragePath(expectedReference)));
        }

        Assert.AreEqual(8, fixture.Store.ListProjectArtifacts(projectId).Count);
        Assert.AreEqual(8L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task ListProjectArtifacts_OrdersByCreatedAtThenId()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        EntityId<ArtifactIdTag> earlyId = EntityId<ArtifactIdTag>.From(
            Guid.Parse("33333333-3333-4333-8333-333333333333"));
        EntityId<ArtifactIdTag> lowerId = EntityId<ArtifactIdTag>.From(
            Guid.Parse("11111111-1111-4111-8111-111111111111"));
        EntityId<ArtifactIdTag> higherId = EntityId<ArtifactIdTag>.From(
            Guid.Parse("22222222-2222-4222-8222-222222222222"));
        (EntityId<ArtifactIdTag> Id, DateTimeOffset Time, string Name)[] insertions =
        [
            (higherId, CreatedAt, "higher.bin"),
            (earlyId, CreatedAt.AddMinutes(-1), "early.bin"),
            (lowerId, CreatedAt, "lower.bin"),
        ];

        foreach ((EntityId<ArtifactIdTag> id, DateTimeOffset time, string name) in insertions)
        {
            byte[] content = Encoding.UTF8.GetBytes(name);
            string stagingPath = fixture.CreateStagingFile(projectId, name, content);
            await fixture.Store.PromoteAsync(fixture.CreateRequest(
                projectId,
                ArtifactKind.Analysis,
                stagingPath,
                name,
                content,
                id,
                time));
        }

        CollectionAssert.AreEqual(
            new[] { earlyId, lowerId, higherId },
            fixture.Store
                .ListProjectArtifacts(projectId)
                .Select(static artifact => artifact.Id)
                .ToArray());
    }

    [TestMethod]
    public async Task PromoteAsync_DigestAndSizeMismatchLeaveStagingAndDatabaseUnchanged()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("unchanged staging bytes");

        string digestStaging = fixture.CreateStagingFile(
            projectId,
            "digest.bin",
            content);
        ArtifactPromotionRequest digestRequest = fixture.CreateRequest(
            projectId,
            ArtifactKind.Analysis,
            digestStaging,
            "digest.bin",
            content,
            expectedDigest: new ContentDigest(new string('0', 64)));
        await AssertPromotionFailure(
            fixture,
            digestRequest,
            ArtifactStoreFailureReason.DigestMismatch);
        AssertStagingUnchanged(digestStaging, content);

        string sizeStaging = fixture.CreateStagingFile(
            projectId,
            "size.bin",
            content);
        ArtifactPromotionRequest sizeRequest = fixture.CreateRequest(
            projectId,
            ArtifactKind.Analysis,
            sizeStaging,
            "size.bin",
            content,
            expectedByteSize: content.LongLength + 1);
        await AssertPromotionFailure(
            fixture,
            sizeRequest,
            ArtifactStoreFailureReason.SizeMismatch);
        AssertStagingUnchanged(sizeStaging, content);

        Assert.AreEqual(0L, fixture.CountArtifacts());
        Assert.IsFalse(File.Exists(fixture.GetDestinationPath(projectId, "analysis", "digest.bin")));
        Assert.IsFalse(File.Exists(fixture.GetDestinationPath(projectId, "analysis", "size.bin")));
    }

    [TestMethod]
    public async Task PromoteAsync_MissingAndDirectoryStagingFailClosed()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = [1, 2, 3];
        string missingPath = fixture.GetStagingPath(projectId, "missing.bin");
        ArtifactPromotionRequest missingRequest = fixture.CreateRequest(
            projectId,
            ArtifactKind.Proxy,
            missingPath,
            "missing.bin",
            content);
        await AssertPromotionFailure(
            fixture,
            missingRequest,
            ArtifactStoreFailureReason.StagingMissing);

        string directoryPath = fixture.GetStagingPath(projectId, "directory.bin");
        Directory.CreateDirectory(directoryPath);
        ArtifactPromotionRequest directoryRequest = fixture.CreateRequest(
            projectId,
            ArtifactKind.Proxy,
            directoryPath,
            "directory.bin",
            content);
        await AssertPromotionFailure(
            fixture,
            directoryRequest,
            ArtifactStoreFailureReason.NotRegularFile);

        Assert.IsTrue(Directory.Exists(directoryPath));
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_ReparsePointStagingIsRejectedWhenLinksAreAvailable()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("link target");
        string targetPath = fixture.CreateStagingFile(projectId, "target.bin", content);
        string linkPath = fixture.GetStagingPath(projectId, "link.bin");

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception linkException)
            when (linkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
            return;
        }

        if ((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) == 0)
        {
            Assert.Inconclusive("The test file system does not expose the link as a reparse point.");
        }

        ArtifactPromotionRequest request = fixture.CreateRequest(
            projectId,
            ArtifactKind.Evidence,
            linkPath,
            "link.bin",
            content);
        await AssertPromotionFailure(
            fixture,
            request,
            ArtifactStoreFailureReason.ReparsePoint);

        Assert.IsTrue(File.Exists(linkPath));
        AssertStagingUnchanged(targetPath, content);
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_OutsideAndWrongProjectStagingAreRejected()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> firstProjectId = fixture.CreateProject();
        EntityId<ProjectIdTag> secondProjectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("must remain in place");
        string outsidePath = Path.Combine(fixture.WorkspacePath, "outside.bin");
        File.WriteAllBytes(outsidePath, content);
        ArtifactPromotionRequest outsideRequest = fixture.CreateRequest(
            firstProjectId,
            ArtifactKind.Original,
            outsidePath,
            "outside.bin",
            content);
        await AssertPromotionFailure(
            fixture,
            outsideRequest,
            ArtifactStoreFailureReason.UnsafePath);
        AssertStagingUnchanged(outsidePath, content);

        string wrongProjectPath = fixture.CreateStagingFile(
            secondProjectId,
            "wrong-project.bin",
            content);
        ArtifactPromotionRequest wrongProjectRequest = fixture.CreateRequest(
            firstProjectId,
            ArtifactKind.Original,
            wrongProjectPath,
            "wrong-project.bin",
            content);
        await AssertPromotionFailure(
            fixture,
            wrongProjectRequest,
            ArtifactStoreFailureReason.UnsafePath);
        AssertStagingUnchanged(wrongProjectPath, content);
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public void PromotionRequest_RejectsNullUnknownNonPositiveAndUnsafePaths()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<ArtifactIdTag> artifactId = EntityId<ArtifactIdTag>.New();
        ContentDigest digest = ContentDigest.Compute([1]);
        string validStagingPath = Path.Combine(fixture.WorkspacePath, "staged.bin");

        Assert.ThrowsExactly<ArgumentNullException>(() => new ArtifactPromotionRequest(
            null!, projectId, ArtifactKind.Original, validStagingPath, "item.bin", digest, 1, CreatedAt));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ArtifactPromotionRequest(
            artifactId, null!, ArtifactKind.Original, validStagingPath, "item.bin", digest, 1, CreatedAt));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ArtifactPromotionRequest(
            artifactId, projectId, ArtifactKind.Original, null!, "item.bin", digest, 1, CreatedAt));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ArtifactPromotionRequest(
            artifactId, projectId, ArtifactKind.Original, validStagingPath, null!, digest, 1, CreatedAt));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ArtifactPromotionRequest(
            artifactId, projectId, ArtifactKind.Original, validStagingPath, "item.bin", null!, 1, CreatedAt));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ArtifactPromotionRequest(
            artifactId, projectId, (ArtifactKind)999, validStagingPath, "item.bin", digest, 1, CreatedAt));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ArtifactPromotionRequest(
            artifactId, projectId, ArtifactKind.Original, validStagingPath, "item.bin", digest, 0, CreatedAt));

        string[] invalidStagingPaths =
        [
            "relative.bin",
            @"\\server\share\staged.bin",
            @"\\?\C:\staged.bin",
            validStagingPath + ":alternate",
        ];
        foreach (string invalidPath in invalidStagingPaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ArtifactPromotionRequest(
                    artifactId,
                    projectId,
                    ArtifactKind.Original,
                    invalidPath,
                    "item.bin",
                    digest,
                    1,
                    CreatedAt));
            Assert.AreEqual("stagingPath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }

        string[] invalidRelativePaths =
        [
            string.Empty,
            "/rooted.bin",
            "../escape.bin",
            "nested/../escape.bin",
            "nested/./item.bin",
            @"nested\item.bin",
            "nested:item.bin",
            "nested//item.bin",
            "nested/item.",
            "nested/item ",
            "nested/\u0001item.bin",
            "nested/<item>.bin",
            "nested/>item.bin",
            "nested/\"item.bin",
            "nested/item|.bin",
            "nested/item?.bin",
            "nested/item*.bin",
            "CON",
            "con.txt",
            "nested/NUL.bin",
            "COM1.bin",
            "nested/LPT9.txt",
            "COM¹.txt",
        ];
        foreach (string invalidPath in invalidRelativePaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ArtifactPromotionRequest(
                    artifactId,
                    projectId,
                    ArtifactKind.Original,
                    validStagingPath,
                    invalidPath,
                    digest,
                    1,
                    CreatedAt));
            Assert.AreEqual("relativePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }
    }

    [TestMethod]
    public void StoreConstructor_RejectsUnsafeDatabaseAndWorkspaceInputsWithoutDisclosure()
    {
        using StoreTestDatabase database = new();
        string workspacePath = Path.Combine(database.RootPath, "workspace");
        Directory.CreateDirectory(workspacePath);
        string[] invalidDatabasePaths =
        [
            "relative.db",
            @"\\server\share\store.db",
            @"\\?\C:\store.db",
            database.DatabasePath + ":alternate",
        ];
        foreach (string invalidPath in invalidDatabasePaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ArtifactStore(invalidPath, workspacePath));
            Assert.AreEqual("databasePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }

        string[] invalidWorkspacePaths =
        [
            "relative-workspace",
            @"\\server\share\workspace",
            @"\\?\C:\workspace",
            workspacePath + ":alternate",
        ];
        foreach (string invalidPath in invalidWorkspacePaths)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new ArtifactStore(database.DatabasePath, invalidPath));
            Assert.AreEqual("workspacePath", exception.ParamName);
            AssertDoesNotContain(exception.Message, invalidPath);
        }

        string missingWorkspace = Path.Combine(database.RootPath, "missing-workspace");
        ArtifactStoreException missing = Assert.ThrowsExactly<ArtifactStoreException>(
            () => new ArtifactStore(database.DatabasePath, missingWorkspace));
        AssertSafeException(
            missing,
            ArtifactStoreFailureReason.UnsafePath,
            reconciliationRequired: false,
            database.DatabasePath,
            missingWorkspace);

        string fileWorkspace = Path.Combine(database.RootPath, "workspace-file");
        File.WriteAllBytes(fileWorkspace, [1]);
        ArtifactStoreException file = Assert.ThrowsExactly<ArtifactStoreException>(
            () => new ArtifactStore(database.DatabasePath, fileWorkspace));
        AssertSafeException(
            file,
            ArtifactStoreFailureReason.UnsafePath,
            reconciliationRequired: false,
            database.DatabasePath,
            fileWorkspace);
    }

    [TestMethod]
    public void StoreConstructor_RejectsReparsePointWorkspaceAncestorWhenLinksAreAvailable()
    {
        using StoreTestDatabase database = new();
        string targetRoot = Path.Combine(database.RootPath, "workspace-target");
        string targetChild = Path.Combine(targetRoot, "child");
        Directory.CreateDirectory(targetChild);
        string linkRoot = Path.Combine(database.RootPath, "workspace-link");

        try
        {
            Directory.CreateSymbolicLink(linkRoot, targetRoot);
        }
        catch (Exception linkException)
            when (linkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
            return;
        }

        if ((File.GetAttributes(linkRoot) & FileAttributes.ReparsePoint) == 0)
        {
            Assert.Inconclusive("The test file system does not expose the link as a reparse point.");
        }

        string workspaceThroughLink = Path.Combine(linkRoot, "child");
        ArtifactStoreException exception = Assert.ThrowsExactly<ArtifactStoreException>(
            () => new ArtifactStore(database.DatabasePath, workspaceThroughLink));
        AssertSafeException(
            exception,
            ArtifactStoreFailureReason.ReparsePoint,
            reconciliationRequired: false,
            database.DatabasePath,
            workspaceThroughLink,
            linkRoot);
    }

    [TestMethod]
    public async Task PromoteAsync_RejectsReparsePointDestinationAncestorWhenLinksAreAvailable()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("destination link target");
        string stagingPath = fixture.CreateStagingFile(projectId, "link-target.bin", content);
        string destinationPath = fixture.GetDestinationPath(
            projectId,
            "analysis",
            "nested/item.bin");
        string linkRoot = Path.GetDirectoryName(Path.GetDirectoryName(destinationPath)!)!;
        string targetRoot = Path.Combine(
            Path.GetDirectoryName(fixture.WorkspacePath)!,
            "destination-target");
        Directory.CreateDirectory(targetRoot);

        try
        {
            Directory.CreateSymbolicLink(linkRoot, targetRoot);
        }
        catch (Exception linkException)
            when (linkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are unavailable in this test environment.");
            return;
        }

        if ((File.GetAttributes(linkRoot) & FileAttributes.ReparsePoint) == 0)
        {
            Assert.Inconclusive("The test file system does not expose the link as a reparse point.");
        }

        ArtifactPromotionRequest request = fixture.CreateRequest(
            projectId,
            ArtifactKind.Analysis,
            stagingPath,
            "nested/item.bin",
            content);
        await AssertPromotionFailure(
            fixture,
            request,
            ArtifactStoreFailureReason.ReparsePoint);

        AssertStagingUnchanged(stagingPath, content);
        Assert.IsFalse(File.Exists(Path.Combine(targetRoot, "nested", "item.bin")));
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_ExistingDestinationLeavesStagingAndDatabaseUnchanged()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] stagingContent = Encoding.UTF8.GetBytes("new bytes");
        byte[] existingContent = Encoding.UTF8.GetBytes("existing bytes");
        string stagingPath = fixture.CreateStagingFile(projectId, "new.bin", stagingContent);
        string destinationPath = fixture.GetDestinationPath(projectId, "renders", "final.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, existingContent);
        ArtifactPromotionRequest request = fixture.CreateRequest(
            projectId,
            ArtifactKind.Render,
            stagingPath,
            "final.bin",
            stagingContent);

        await AssertPromotionFailure(
            fixture,
            request,
            ArtifactStoreFailureReason.DestinationExists);

        AssertStagingUnchanged(stagingPath, stagingContent);
        CollectionAssert.AreEqual(existingContent, File.ReadAllBytes(destinationPath));
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_NonexistentProjectIsRejectedWithoutOrphaningFile()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> missingProjectId = EntityId<ProjectIdTag>.New();
        byte[] content = Encoding.UTF8.GetBytes("unregistered project artifact");
        string stagingPath = fixture.CreateStagingFile(
            missingProjectId,
            "missing-project.bin",
            content);
        ArtifactPromotionRequest request = fixture.CreateRequest(
            missingProjectId,
            ArtifactKind.Original,
            stagingPath,
            "missing-project.bin",
            content);

        await AssertPromotionFailure(
            fixture,
            request,
            ArtifactStoreFailureReason.DatabaseRejected);

        AssertStagingUnchanged(stagingPath, content);
        Assert.IsFalse(File.Exists(fixture.GetDestinationPath(
            missingProjectId,
            "original",
            "missing-project.bin")));
        Assert.AreEqual(0L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_DuplicateStorageReferenceIsPrecheckedAndLeavesSecondStaging()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] firstContent = Encoding.UTF8.GetBytes("first artifact");
        byte[] secondContent = Encoding.UTF8.GetBytes("second artifact");
        string firstStaging = fixture.CreateStagingFile(projectId, "first.bin", firstContent);
        ArtifactPromotionRequest first = fixture.CreateRequest(
            projectId,
            ArtifactKind.Render,
            firstStaging,
            "same.bin",
            firstContent);
        _ = await fixture.Store.PromoteAsync(first);
        string secondStaging = fixture.CreateStagingFile(projectId, "second.bin", secondContent);
        ArtifactPromotionRequest second = fixture.CreateRequest(
            projectId,
            ArtifactKind.Render,
            secondStaging,
            "same.bin",
            secondContent);

        await AssertPromotionFailure(
            fixture,
            second,
            ArtifactStoreFailureReason.DatabaseRejected);

        AssertStagingUnchanged(secondStaging, secondContent);
        CollectionAssert.AreEqual(
            firstContent,
            File.ReadAllBytes(fixture.GetDestinationPath(projectId, "renders", "same.bin")));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_DuplicateArtifactIdAfterRenameRollsBackToStaging()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        EntityId<ArtifactIdTag> duplicateId = EntityId<ArtifactIdTag>.New();
        byte[] firstContent = Encoding.UTF8.GetBytes("first ID owner");
        byte[] secondContent = Encoding.UTF8.GetBytes("must roll back");
        string firstStaging = fixture.CreateStagingFile(projectId, "id-first.bin", firstContent);
        _ = await fixture.Store.PromoteAsync(fixture.CreateRequest(
            projectId,
            ArtifactKind.Analysis,
            firstStaging,
            "id-first.bin",
            firstContent,
            duplicateId));
        string secondStaging = fixture.CreateStagingFile(projectId, "id-second.bin", secondContent);
        ArtifactPromotionRequest duplicate = fixture.CreateRequest(
            projectId,
            ArtifactKind.Analysis,
            secondStaging,
            "id-second.bin",
            secondContent,
            duplicateId);

        ArtifactStoreException exception = await AssertPromotionFailure(
            fixture,
            duplicate,
            ArtifactStoreFailureReason.DatabaseRejected);

        Assert.IsFalse(exception.ReconciliationRequired);
        AssertStagingUnchanged(secondStaging, secondContent);
        Assert.IsFalse(File.Exists(fixture.GetDestinationPath(
            projectId,
            "analysis",
            "id-second.bin")));
        Assert.IsTrue(File.Exists(fixture.GetDestinationPath(
            projectId,
            "analysis",
            "id-first.bin")));
        Assert.AreEqual(1L, fixture.CountArtifacts());
    }

    [TestMethod]
    public async Task PromoteAsync_ConcurrentSameDestinationHasOneWinnerAndNoLoserOrphan()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] firstContent = Encoding.UTF8.GetBytes("concurrent first");
        byte[] secondContent = Encoding.UTF8.GetBytes("concurrent second");
        string firstStaging = fixture.CreateStagingFile(projectId, "race-first.bin", firstContent);
        string secondStaging = fixture.CreateStagingFile(projectId, "race-second.bin", secondContent);
        ArtifactPromotionRequest firstRequest = fixture.CreateRequest(
            projectId,
            ArtifactKind.Preview,
            firstStaging,
            "race.bin",
            firstContent);
        ArtifactPromotionRequest secondRequest = fixture.CreateRequest(
            projectId,
            ArtifactKind.Preview,
            secondStaging,
            "race.bin",
            secondContent);
        var firstStore = new ArtifactStore(fixture.DatabasePath, fixture.WorkspacePath);
        var secondStore = new ArtifactStore(fixture.DatabasePath, fixture.WorkspacePath);
        using var release = new ManualResetEventSlim(initialState: false);

        Task<PromotionOutcome> firstTask = Task.Run(
            () => PromoteAfterRelease(firstStore, firstRequest, release));
        Task<PromotionOutcome> secondTask = Task.Run(
            () => PromoteAfterRelease(secondStore, secondRequest, release));
        release.Set();
        PromotionOutcome[] outcomes = await Task.WhenAll(firstTask, secondTask);

        Assert.AreEqual(1, outcomes.Count(static outcome => outcome.Artifact is not null));
        Assert.AreEqual(1, outcomes.Count(static outcome => outcome.Exception is not null));
        PromotionOutcome winner = outcomes.Single(static outcome => outcome.Artifact is not null);
        PromotionOutcome loser = outcomes.Single(static outcome => outcome.Exception is not null);
        Assert.IsFalse(loser.Exception!.ReconciliationRequired);
        Assert.IsTrue(loser.Exception.Reason is
            ArtifactStoreFailureReason.DatabaseRejected or
            ArtifactStoreFailureReason.DestinationExists);

        bool firstWon = winner.Artifact!.Id == firstRequest.Id;
        string winnerStaging = firstWon ? firstStaging : secondStaging;
        string loserStaging = firstWon ? secondStaging : firstStaging;
        byte[] winnerContent = firstWon ? firstContent : secondContent;
        byte[] loserContent = firstWon ? secondContent : firstContent;
        Assert.IsFalse(File.Exists(winnerStaging));
        AssertStagingUnchanged(loserStaging, loserContent);
        string destinationPath = fixture.GetDestinationPath(projectId, "previews", "race.bin");
        CollectionAssert.AreEqual(winnerContent, File.ReadAllBytes(destinationPath));
        Assert.AreEqual(1L, fixture.CountArtifacts());
        Assert.AreEqual(winner.Artifact.Id, fixture.Store.ListProjectArtifacts(projectId).Single().Id);
    }

    [TestMethod]
    public async Task PromoteAsync_PreCancelledTokenLeavesCompletedStagingUntouched()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = new byte[1024 * 1024];
        content[^1] = 42;
        string stagingPath = fixture.CreateStagingFile(projectId, "cancel.bin", content);
        ArtifactPromotionRequest request = fixture.CreateRequest(
            projectId,
            ArtifactKind.Proxy,
            stagingPath,
            "cancel.bin",
            content);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => fixture.Store.PromoteAsync(request, cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        AssertStagingUnchanged(stagingPath, content);
        Assert.AreEqual(0L, fixture.CountArtifacts());
        Assert.IsFalse(File.Exists(fixture.GetDestinationPath(
            projectId,
            "proxy",
            "cancel.bin")));
    }

    [TestMethod]
    public async Task CorruptKindAndStorageReferenceFailClosedWithoutPathOrSqlDisclosure()
    {
        using ArtifactFixture fixture = new();
        EntityId<ProjectIdTag> projectId = fixture.CreateProject();
        byte[] content = Encoding.UTF8.GetBytes("stored before corruption");
        string stagingPath = fixture.CreateStagingFile(projectId, "corrupt.bin", content);
        ArtifactPromotionRequest request = fixture.CreateRequest(
            projectId,
            ArtifactKind.Render,
            stagingPath,
            "corrupt.bin",
            content);
        _ = await fixture.Store.PromoteAsync(request);

        fixture.Database.ExecuteNonQuery(
            "UPDATE artifacts SET kind = $kind WHERE id = $id;",
            ("$kind", "UnknownKind"),
            ("$id", request.Id.ToString()));
        ArtifactStoreException corruptKind = Assert.ThrowsExactly<ArtifactStoreException>(
            () => fixture.Store.GetArtifact(request.Id));
        AssertSafeException(
            corruptKind,
            ArtifactStoreFailureReason.DatabaseRejected,
            reconciliationRequired: false,
            fixture.DatabasePath,
            fixture.WorkspacePath,
            "UPDATE artifacts",
            "UnknownKind");

        fixture.Database.ExecuteNonQuery(
            "UPDATE artifacts SET kind = $kind, storage_reference = $reference WHERE id = $id;",
            ("$kind", ArtifactKind.Render.ToString()),
            ("$reference", $"projects/{projectId}/renders/../escape.bin"),
            ("$id", request.Id.ToString()));
        ArtifactStoreException corruptReference = Assert.ThrowsExactly<ArtifactStoreException>(
            () => fixture.Store.ListProjectArtifacts(projectId));
        AssertSafeException(
            corruptReference,
            ArtifactStoreFailureReason.DatabaseRejected,
            reconciliationRequired: false,
            fixture.DatabasePath,
            fixture.WorkspacePath,
            "SELECT",
            "escape.bin");
    }

    [TestMethod]
    public void PublicArtifactStoreSurfaceIsConcreteSealedAndImmutable()
    {
        Assert.IsTrue(typeof(ArtifactIdTag).IsPublic);
        Assert.IsTrue(typeof(ArtifactIdTag).IsSealed);
        Assert.IsTrue(typeof(ArtifactKind).IsPublic);
        CollectionAssert.AreEqual(
            new[]
            {
                "Original",
                "Proxy",
                "Analysis",
                "Composition",
                "Preview",
                "Render",
                "Delivery",
                "Evidence",
            },
            Enum.GetNames<ArtifactKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                "StagingMissing",
                "UnsafePath",
                "NotRegularFile",
                "ReparsePoint",
                "SizeMismatch",
                "DigestMismatch",
                "DestinationExists",
                "DatabaseRejected",
                "RollbackFailed",
            },
            Enum.GetNames<ArtifactStoreFailureReason>());

        foreach (Type immutableType in new[]
        {
            typeof(ArtifactPromotionRequest),
            typeof(StoredArtifact),
            typeof(ArtifactStoreException),
        })
        {
            Assert.IsTrue(immutableType.IsPublic);
            Assert.IsTrue(immutableType.IsSealed);
            Assert.IsTrue(immutableType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null));
        }

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Id",
                "ProjectId",
                "Kind",
                "StagingPath",
                "RelativePath",
                "ExpectedDigest",
                "ExpectedByteSize",
                "CreatedAt",
            },
            typeof(ArtifactPromotionRequest)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Id",
                "ProjectId",
                "Kind",
                "StorageReference",
                "ContentDigest",
                "ByteSize",
                "CreatedAt",
            },
            typeof(StoredArtifact)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "Reason", "ReconciliationRequired" },
            typeof(ArtifactStoreException)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());

        Assert.IsTrue(typeof(ArtifactStore).IsPublic);
        Assert.IsTrue(typeof(ArtifactStore).IsSealed);
        Assert.AreEqual(0, typeof(ArtifactStore).GetInterfaces().Length);
        Assert.IsFalse(typeof(ArtifactStore).IsGenericType);
        Type[] exported = typeof(ArtifactStore).Assembly.GetExportedTypes();
        Assert.IsFalse(exported.Any(static type =>
            type.IsInterface &&
            (type.Name.Contains("Artifact", StringComparison.Ordinal) ||
                type.Name.Contains("Repository", StringComparison.Ordinal))));
        Assert.IsFalse(exported.Any(static type =>
            type.IsGenericTypeDefinition &&
            type.Name.Contains("Repository", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WindowsOpenReadDeleteShareHandleAllowsSameVolumeRename()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file-sharing semantics are the supported target.");
        }

        using StoreTestDatabase database = new();
        byte[] content = Encoding.UTF8.GetBytes("open handle rename");
        string sourcePath = Path.Combine(database.RootPath, "open-source.bin");
        string destinationPath = Path.Combine(database.RootPath, "open-destination.bin");
        File.WriteAllBytes(sourcePath, content);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        using FileStream handle = new(sourcePath, options);
        File.Move(sourcePath, destinationPath, overwrite: false);

        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(destinationPath));
        Assert.AreEqual(content.LongLength, handle.Length);
        FileAttributes attributes = File.GetAttributes(handle.SafeFileHandle);
        Assert.AreEqual(
            (FileAttributes)0,
            attributes &
                (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint));
        handle.Position = 0;
        byte[] read = new byte[content.Length];
        handle.ReadExactly(read);
        CollectionAssert.AreEqual(content, read);
    }

    private static async Task<ArtifactStoreException> AssertPromotionFailure(
        ArtifactFixture fixture,
        ArtifactPromotionRequest request,
        ArtifactStoreFailureReason reason)
    {
        ArtifactStoreException exception =
            await Assert.ThrowsExactlyAsync<ArtifactStoreException>(
                () => fixture.Store.PromoteAsync(request));
        AssertSafeException(
            exception,
            reason,
            reconciliationRequired: false,
            fixture.DatabasePath,
            fixture.WorkspacePath,
            request.StagingPath,
            request.RelativePath);
        return exception;
    }

    private static void AssertSafeException(
        ArtifactStoreException exception,
        ArtifactStoreFailureReason reason,
        bool reconciliationRequired,
        params string[] forbiddenValues)
    {
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        foreach (string forbiddenValue in forbiddenValues)
        {
            AssertDoesNotContain(exception.Message, forbiddenValue);
        }
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (forbiddenValue.Length == 0)
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertStagingUnchanged(string stagingPath, byte[] content)
    {
        Assert.IsTrue(File.Exists(stagingPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(stagingPath));
    }

    private static void AssertArtifact(
        StoredArtifact artifact,
        EntityId<ArtifactIdTag> id,
        EntityId<ProjectIdTag> projectId,
        ArtifactKind kind,
        string storageReference,
        ContentDigest digest,
        long byteSize,
        DateTimeOffset createdAt)
    {
        Assert.AreEqual(id, artifact.Id);
        Assert.AreEqual(projectId, artifact.ProjectId);
        Assert.AreEqual(kind, artifact.Kind);
        Assert.AreEqual(storageReference, artifact.StorageReference);
        Assert.AreEqual(digest, artifact.ContentDigest);
        Assert.AreEqual(byteSize, artifact.ByteSize);
        Assert.AreEqual(createdAt, artifact.CreatedAt);
    }

    private static void AssertArtifactEqual(StoredArtifact expected, StoredArtifact actual)
    {
        AssertArtifact(
            actual,
            expected.Id,
            expected.ProjectId,
            expected.Kind,
            expected.StorageReference,
            expected.ContentDigest,
            expected.ByteSize,
            expected.CreatedAt);
    }

    private static async Task<PromotionOutcome> PromoteAfterRelease(
        ArtifactStore store,
        ArtifactPromotionRequest request,
        ManualResetEventSlim release)
    {
        release.Wait();
        try
        {
            return new PromotionOutcome(
                await store.PromoteAsync(request),
                Exception: null);
        }
        catch (ArtifactStoreException exception)
        {
            return new PromotionOutcome(Artifact: null, exception);
        }
    }

    private sealed record PromotionOutcome(
        StoredArtifact? Artifact,
        ArtifactStoreException? Exception);

    private sealed class ArtifactFixture : IDisposable
    {
        internal ArtifactFixture()
        {
            Database = new StoreTestDatabase();
            WorkspacePath = Path.Combine(Database.RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            Store = new ArtifactStore(Database.DatabasePath, WorkspacePath);
        }

        internal StoreTestDatabase Database { get; }

        internal string DatabasePath => Database.DatabasePath;

        internal string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal EntityId<ProjectIdTag> CreateProject()
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(
                projectId,
                "Artifact test project",
                "Exercise safe artifact promotion.",
                "Test suite",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(DatabasePath).CreateProject(
                project,
                "{\"artifactStore\":true}",
                CreatedAt);
            return projectId;
        }

        internal string CreateStagingFile(
            EntityId<ProjectIdTag> projectId,
            string relativePath,
            byte[] content)
        {
            string stagingPath = GetStagingPath(projectId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            File.WriteAllBytes(stagingPath, content);
            return stagingPath;
        }

        internal string GetStagingPath(
            EntityId<ProjectIdTag> projectId,
            string relativePath) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                "staging",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal string GetDestinationPath(
            EntityId<ProjectIdTag> projectId,
            string folder,
            string relativePath) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                folder,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal string GetStoragePath(string storageReference) =>
            Path.Combine(
                WorkspacePath,
                storageReference.Replace('/', Path.DirectorySeparatorChar));

        internal ArtifactPromotionRequest CreateRequest(
            EntityId<ProjectIdTag> projectId,
            ArtifactKind kind,
            string stagingPath,
            string relativePath,
            byte[] content,
            EntityId<ArtifactIdTag>? id = null,
            DateTimeOffset? createdAt = null,
            ContentDigest? expectedDigest = null,
            long? expectedByteSize = null) =>
            new(
                id ?? EntityId<ArtifactIdTag>.New(),
                projectId,
                kind,
                stagingPath,
                relativePath,
                expectedDigest ?? ContentDigest.Compute(content),
                expectedByteSize ?? content.LongLength,
                createdAt ?? CreatedAt);

        internal long CountArtifacts() =>
            Database.ExecuteScalarInt64("SELECT COUNT(*) FROM artifacts;");

        public void Dispose() => Database.Dispose();
    }
}